// MoveBench measures movement at game scale: up to 10,000 units on a 1,500 x 1,500 map with
// mountains, passes, winding roads and forests (made by the game repo's
// tools/terrain/make_heightmap.py --bench-layout, which also writes <map>_layout.json), or on a
// flat map of that size. Each group gets a real Move command, so routing (the path system:
// formation slots, flow fields, routes) and movement are measured together, with the rest of
// the tick as in a game. MOVEBENCH_DRIVER=1 measures movement alone instead: a bench driver
// stands in for routing, feeding each unit's MoveIntent from one flow field per group
// (Dijkstra over the wheeled cost grid, eroded by a cell for clearance; per class in the forest
// workload) toward its own slots, right after the path system runs (through the step probe, so
// its time is not counted).
//
//   python3 tools/terrain/make_heightmap.py --size 1500 --bench-layout --out /tmp/b/map   (game repo)
//   dotnet run -c Release --project src/SimRun -- -move-bench all -map /tmp/b/map.json -out bench/move_1500.json
//   dotnet run -c Release --project src/SimRun -- -move-bench idle10k,moving10k -map flat
//
// MOVEBENCH_DEBUG=1 prints each new worst overlap (the pair), and with MOVEBENCH_ASCII=N a map
// of the units around it once it reaches N percent (digits: infantry per cell, V vehicle,
// # blocked for infantry, f forest); MOVEBENCH_DEBUG=2 lists the units that didn't arrive.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static class MoveBench
{
    public static readonly string[] Workloads = ["idle10k", "moving10k", "group500", "group2000", "pass", "forest"];

    private const ulong Seed = 42;

    public sealed record Options(string Map, string[] Workloads, int MaxTicks, double BudgetSeconds, string Out, string Content, string Setup);

    // The parts of <map>_layout.json the bench uses (all in cells).
    private sealed class Layout
    {
        public required (int X, int Y, int Half)[] Passes { get; init; }
        public required (int X, int Y)[] PassMouths { get; init; }
        public required (int X, int Y)[] ZonesWest { get; init; }
        public required (int X, int Y)[] ZonesEast { get; init; }
        public required (int X0, int Y0, int X1, int Y1) Forest { get; init; }
    }

    public static int Run(Options o)
    {
        var content = ContentLoader.LoadDirectory(o.Content);
        var setup = Replay.LoadSetup(o.Setup).For(content, Seed);
        var match = MatchCompiler.Compile(content, setup);
        var rules = TerrainLoader.LoadRules(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.Content))!, "terrain", "rules.json"));
        TerrainMap map;
        Layout layout;
        var sw = Stopwatch.StartNew();
        if (o.Map == "flat")
        {
            map = TerrainMap.Flat(1500, 1500, rules);
            layout = FlatLayout(1500);
        }
        else
        {
            map = TerrainLoader.LoadMap(o.Map, rules);
            layout = ReadLayout(Path.Combine(Path.GetDirectoryName(o.Map) ?? ".", Path.GetFileNameWithoutExtension(o.Map) + "_layout.json"));
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"map {o.Map}: {map.Width}x{map.Height}, loaded in {sw.Elapsed.TotalMilliseconds:0} ms"));

        // JIT warm-up, not recorded: long enough, and a pause, for the hot methods to reach the
        // optimizing tier (or run with DOTNET_TieredCompilation=0).
        var warm = Console.Out;
        Console.SetOut(TextWriter.Null);
        RunOne(match, map, layout, "group500", o with { MaxTicks = 150, BudgetSeconds = 20 });
        Console.SetOut(warm);
        GC.Collect();
        Thread.Sleep(1000);

        var results = new JsonArray();
        foreach (var wl in o.Workloads)
        {
            results.Add(RunOne(match, map, layout, wl, o));
            GC.Collect();
        }
        var doc = new JsonObject
        {
            ["label"] = "move-bench",
            ["map"] = o.Map,
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["machine"] = $"{Environment.ProcessorCount} cpus, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}",
            ["results"] = results,
        };
        if (o.Out != "")
        {
            var dir = Path.GetDirectoryName(o.Out);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(o.Out, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        return 0;
    }

    private static Layout ReadLayout(string path)
    {
        var j = JsonNode.Parse(File.ReadAllText(path))!;
        static (int, int) P(JsonNode n) => ((int)n[0]!, (int)n[1]!);
        var f = j["forestBlock"]!;
        return new Layout
        {
            Passes = [.. j["passes"]!.AsArray().Select(p => ((int)p!["x"]!, (int)p["y"]!, (int)p["halfWidth"]!))],
            PassMouths = [.. j["passMouths"]!.AsArray().Select(p => P(p!))],
            ZonesWest = [.. j["zonesWest"]!.AsArray().Select(p => P(p!))],
            ZonesEast = [.. j["zonesEast"]!.AsArray().Select(p => P(p!))],
            Forest = ((int)f[0]!, (int)f[1]!, (int)f[2]!, (int)f[3]!),
        };
    }

    // FlatLayout places the same named spots on a flat map (no pass or forest there).
    private static Layout FlatLayout(int s) => new()
    {
        Passes = [(s / 2, s / 2, 8)],
        PassMouths = [(s / 2 - 110, s / 2), (s / 2 + 110, s / 2)],
        ZonesWest = [(s * 15 / 100, s / 5), (s * 15 / 100, s / 2), (s * 15 / 100, s * 4 / 5), (s / 3, s * 38 / 100)],
        ZonesEast = [(s * 85 / 100, s / 5), (s * 85 / 100, s / 2), (s * 85 / 100, s * 4 / 5), (s * 2 / 3, s * 38 / 100)],
        Forest = (s * 22 / 100, s * 58 / 100, s * 30 / 100, s * 70 / 100),
    };

    // ---- Groups and the driver ---------------------------------------------------------------

    // A group: its units, each one's slot and the flow field it follows (cell distances to the slots).
    private sealed class Group
    {
        public required string Name { get; init; }
        public List<Unit> Units { get; } = [];
        public List<Vec> Slots { get; } = [];
        public List<int[]> Field { get; } = [];
        public List<bool> Done { get; } = [];
        public int ArrivedAt { get; set; } = -1;
    }

    // Driver times each system like Bench's probe and, right after the path system, writes the
    // intents of the groups' units (its own time is left out of every phase).
    private sealed class Driver(World w, List<Group> groups) : IStepProbe
    {
        public readonly long[] Ticks = new long[Enum.GetValues<StepPhase>().Length];
        private long _last;

        public void Begin()
        {
            Array.Clear(Ticks);
            _last = Stopwatch.GetTimestamp();
        }

        public void After(StepPhase phase)
        {
            var now = Stopwatch.GetTimestamp();
            Ticks[(int)phase] += now - _last;
            if (phase == StepPhase.Paths)
            {
                foreach (var g in groups)
                {
                    for (var k = 0; k < g.Units.Count; k++)
                    {
                        if (StandIn)
                        {
                            Drive(w, g, k);
                            continue;
                        }
                        // Real orders: done once the order is (the unit idles), slots from routing.
                        var u = g.Units[k];
                        if (u.Group != 0 || u.Order.Kind == OrderKind.Move)
                        {
                            g.Slots[k] = u.Dest.Center();
                        }
                        g.Done[k] |= w.Tick > 1 && u.Order.Kind == OrderKind.Idle;
                    }
                }
            }
            _last = Stopwatch.GetTimestamp();
        }
    }

    // Within this many cells of its slot a unit heads straight for it; farther out it follows
    // the field a few cells ahead.
    private const int Direct = 12;
    private const int LookAhead = 3;

    private static readonly (int Dx, int Dy)[] Eight = [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];

    private static void Drive(World w, Group g, int k)
    {
        var u = g.Units[k];
        if (!u.Alive || u.Inside != 0)
        {
            return;
        }
        var slot = g.Slots[k];
        var intent = u.Intent;
        var speed = u.Def.Speed;
        var to = slot - u.Pos;
        var dist = to.Len();
        if (g.Done[k] || (intent.Arrived && intent.Goal == slot) || (intent.Stuck && dist.Raw < 4 * Fixed.OneRaw))
        {
            g.Done[k] = true;
            u.Intent = intent with { Desired = default };
            return;
        }
        Vec goal;
        if (dist.Raw <= Direct * Fixed.OneRaw)
        {
            goal = slot;
        }
        else
        {
            var f = g.Field[k];
            var c = Cell.Of(u.Pos);
            var start = c;
            for (var s = 0; s < LookAhead; s++)
            {
                var best = c;
                var bestD = At(f, w.Grid, c);
                foreach (var (dx, dy) in Eight)
                {
                    var n = new Cell(c.X + dx, c.Y + dy);
                    var d = At(f, w.Grid, n);
                    if (d < bestD && (dx == 0 || dy == 0 || (At(f, w.Grid, new Cell(c.X + dx, c.Y)) < int.MaxValue && At(f, w.Grid, new Cell(c.X, c.Y + dy)) < int.MaxValue)))
                    {
                        best = n;
                        bestD = d;
                    }
                }
                if (best == c)
                {
                    break;
                }
                c = best;
            }
            goal = c == start ? slot : c.Center();
        }
        var step = goal - u.Pos;
        var len = step.Len();
        var desired = goal == slot && len.Raw <= speed.Raw ? step : len.Raw == 0 ? default : step.WithLen(speed);
        u.Intent = goal == intent.Goal ? intent with { Desired = desired } : new MoveIntent(goal, desired, default, false);
    }

    private static int At(int[] f, Grid g, Cell c) => g.Contains(c) ? f[c.Y * g.Width + c.X] : int.MaxValue;

    // Field is a Dijkstra distance field over a movement class's cost grid (8-way, no corner
    // cutting) from the seed cells; cells the class can't enter, and with erode those next to
    // one, are unreachable (int.MaxValue).
    private static int[] Field(TerrainMap map, int cls, bool erode, IEnumerable<Cell> seeds)
    {
        int w = map.Width, h = map.Height;
        var cost = map.CostGrid(cls).ToArray();
        if (erode)
        {
            var e = (byte[])cost.Clone();
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (cost[y * w + x] == 0)
                    {
                        continue;
                    }
                    foreach (var (dx, dy) in Eight)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h || cost[ny * w + nx] == 0)
                        {
                            e[y * w + x] = 0;
                            break;
                        }
                    }
                }
            }
            cost = e;
        }
        var dist = new int[w * h];
        Array.Fill(dist, int.MaxValue);
        var open = new PriorityQueue<int, long>();
        foreach (var s in seeds)
        {
            var i = s.Y * w + s.X;
            if (cost[i] != 0 && dist[i] != 0)
            {
                dist[i] = 0;
                open.Enqueue(i, i);
            }
        }
        while (open.TryDequeue(out var i, out var pri))
        {
            var d = (int)(pri >> 22);
            if (d != dist[i])
            {
                continue;
            }
            int x = i % w, y = i / w;
            foreach (var (dx, dy) in Eight)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                {
                    continue;
                }
                var ni = ny * w + nx;
                if (cost[ni] == 0)
                {
                    continue;
                }
                if (dx != 0 && dy != 0 && (cost[y * w + nx] == 0 || cost[ny * w + x] == 0))
                {
                    continue;
                }
                var nd = d + cost[ni] * (dx != 0 && dy != 0 ? 14 : 10);
                if (nd < dist[ni])
                {
                    dist[ni] = nd;
                    open.Enqueue(ni, ((long)nd << 22) | (uint)ni);
                }
            }
        }
        return dist;
    }

    // ---- Worlds --------------------------------------------------------------------------------

    private static readonly (string Name, int Weight)[] Infantry =
    [
        ("Rifleman", 45), ("Gunner", 10), ("Raider", 10), ("Tank hunter", 15), ("Marksman", 5), ("Drone hunter", 5), ("Skirmisher", 5), ("Sapper", 5),
    ];

    private static UnitDef PickInfantry(CompiledMatch m, CompiledPlayer p, int roll)
    {
        foreach (var (name, weight) in Infantry)
        {
            if (roll < weight)
            {
                return p.Find(m.Content, name)!;
            }
            roll -= weight;
        }
        return p.Find(m.Content, "Rifleman")!;
    }

    // Spots hands out cells around a point in rings (nearest first) that a unit of a class can
    // stand on, one unit per cell for infantry and a 3 x 3 block for vehicles, never twice.
    private sealed class Spots(TerrainMap map, Grid grid)
    {
        private readonly bool[] _used = new bool[map.Width * map.Height];

        private Cell _at = new(-1, -1);
        private int _from;

        public Cell Take(Cell at, int cls, bool vehicle)
        {
            if (at != _at)
            {
                _at = at;
                _from = 0;
            }
            for (var r = _from; r < 600; r++)
            {
                var fullRing = true;
                for (var k = 0; k < Math.Max(1, 8 * r); k++)
                {
                    var c = Ring(at, r, k);
                    if (!grid.Contains(c) || _used[c.Y * map.Width + c.X])
                    {
                        continue;
                    }
                    fullRing = false;
                    if (Free(c, cls, vehicle ? 1 : 0))
                    {
                        Mark(c, vehicle ? 1 : 0);
                        return c;
                    }
                }
                if (fullRing && r == _from)
                {
                    _from = r + 1; // every cell of this ring is taken: start further out next time
                }
            }
            throw new InvalidOperationException($"bench: no room near {at}");
        }

        // Ring returns the k-th cell (clockwise from the top-left corner) at Chebyshev distance r.
        private static Cell Ring(Cell c, int r, int k)
        {
            if (r == 0)
            {
                return c;
            }
            var side = 2 * r;
            var (s, o) = (k / side, k % side);
            return s switch
            {
                0 => new Cell(c.X - r + o, c.Y - r),
                1 => new Cell(c.X + r, c.Y - r + o),
                2 => new Cell(c.X + r - o, c.Y + r),
                _ => new Cell(c.X - r, c.Y + r - o),
            };
        }

        private bool Free(Cell c, int cls, int half)
        {
            for (var y = c.Y - half; y <= c.Y + half; y++)
            {
                for (var x = c.X - half; x <= c.X + half; x++)
                {
                    var n = new Cell(x, y);
                    if (!grid.Contains(n) || _used[y * map.Width + x] || !map.Passable(cls, n))
                    {
                        return false;
                    }
                }
            }
            // A vehicle also wants a free ring around it (clearance from cliffs and forest).
            return half == 0 || (map.Passable(cls, new Cell(c.X - 2, c.Y)) && map.Passable(cls, new Cell(c.X + 2, c.Y)) &&
                                 map.Passable(cls, new Cell(c.X, c.Y - 2)) && map.Passable(cls, new Cell(c.X, c.Y + 2)));
        }

        private void Mark(Cell c, int half)
        {
            for (var y = c.Y - half; y <= c.Y + half; y++)
            {
                for (var x = c.X - half; x <= c.X + half; x++)
                {
                    _used[y * map.Width + x] = true;
                }
            }
        }
    }

    // Army spawns count orderable units of player p around a point (each vehicle with a
    // driver inside, not counted): vehicles every tenth unit when vehicleEvery is 10, none for 0.
    private static List<Unit> Army(World w, Spots spots, CompiledPlayer p, Cell at, int count, int vehicleEvery, int facing, int seed)
    {
        var rules = w.Ground!.Rules;
        var tech = p.Find(w.Content, "technical")!;
        var driver = p.Find(w.Content, "Rifleman")!;
        var units = new List<Unit>();
        for (var i = 0; i < count; i++)
        {
            if (vehicleEvery > 0 && i % vehicleEvery == vehicleEvery - 1)
            {
                var c = spots.Take(at, rules.ClassOf(tech), vehicle: true);
                var v = w.Unit(w.Spawn(tech, c, facing))!;
                var d = w.Unit(w.Spawn(driver, c, facing))!;
                World.Board(d, v);
                units.Add(v);
                continue;
            }
            var def = PickInfantry(w.Match, p, (i * 37 + seed) % 100);
            var cell = spots.Take(at, rules.ClassOf(def), vehicle: false);
            units.Add(w.Unit(w.Spawn(def, cell, facing))!);
        }
        return units;
    }

    // Order sends a group to slots around target: the units nearest the target take the slots
    // nearest it (they get there first), and each class follows one field toward the slots.
    private static readonly bool StandIn = Environment.GetEnvironmentVariable("MOVEBENCH_DRIVER") == "1";

    private static Group Order(World w, string name, List<Unit> units, Cell target, bool perClass, Dictionary<string, int[]>? cache = null)
    {
        if (!StandIn)
        {
            var rg = new Group { Name = name };
            foreach (var u in units)
            {
                rg.Units.Add(u);
                rg.Slots.Add(target.Center());
                rg.Done.Add(false);
            }
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [.. units.Select(u => u.Id)], Cell = target });
            return rg;
        }
        var map = w.Ground!;
        var rules = map.Rules;
        var g = new Group { Name = name };
        var spots = new Spots(map, w.Grid);
        var tc = target.Center();
        var order = units.OrderBy(u => (u.Pos - tc).LenSq()).ThenBy(u => u.Id).ToList();
        var wheeled = rules.ClassOf(Locomotor.Wheeled, Fixed.FromInt(0));
        var slotsByClass = new Dictionary<int, List<Cell>>();
        foreach (var u in order)
        {
            var vehicle = u.Def.Archetype.Locomotor is Locomotor.Wheeled or Locomotor.Tracked;
            var cls = perClass ? rules.ClassOf(u.Def) : wheeled;
            var c = spots.Take(target, rules.ClassOf(u.Def), vehicle);
            g.Units.Add(u);
            g.Slots.Add(c.Center());
            g.Done.Add(false);
            if (!slotsByClass.TryGetValue(cls, out var list))
            {
                slotsByClass[cls] = list = [];
            }
            list.Add(c);
        }
        var fields = new Dictionary<int, int[]>();
        foreach (var (cls, cells) in slotsByClass)
        {
            fields[cls] = Field(map, cls, erode: !perClass || rules.Classes[cls].Locomotor != Locomotor.Foot, cells);
        }
        foreach (var u in g.Units)
        {
            g.Field.Add(fields[perClass ? rules.ClassOf(u.Def) : wheeled]);
        }
        return g;
    }

    private static (World W, List<Group> Groups) Build(CompiledMatch match, TerrainMap map, Layout l, string workload)
    {
        var s = map.Width;
        var w = new World(new Grid(map.Width, map.Height), match);
        w.AttachTerrain(map);
        var p1 = match.Players[0];
        var spots = new Spots(map, w.Grid);
        var groups = new List<Group>();
        switch (workload)
        {
            case "idle10k":
            case "moving10k":
            {
                // 20 groups of 500 on a 5 x 4 grid of points; moving: each crosses to the point
                // mirrored through the centre (paths cross everywhere, and through the passes).
                var points = new List<Cell>();
                for (var gy = 0; gy < 4; gy++)
                {
                    for (var gx = 0; gx < 5; gx++)
                    {
                        points.Add(new Cell(s * (2 * gx + 1) / 10, s * (2 * gy + 1) / 8));
                    }
                }
                var armies = points.Select((pt, k) => Army(w, spots, p1, pt, 500, 10, Facing.East, k)).ToList();
                if (workload == "moving10k")
                {
                    for (var k = 0; k < armies.Count; k++)
                    {
                        var dst = points[points.Count - 1 - k];
                        groups.Add(Order(w, $"g{k}", armies[k], dst, perClass: false));
                    }
                }
                break;
            }
            case "group500":
            case "group2000":
            {
                var n = workload == "group500" ? 500 : 2000;
                var from = l.ZonesWest[1];
                var to = l.ZonesEast[1];
                var army = Army(w, spots, p1, new Cell(from.X, from.Y), n, 10, Facing.East, 0);
                groups.Add(Order(w, workload, army, new Cell(to.X, to.Y), perClass: false));
                break;
            }
            case "pass":
            {
                // Two 2,500-unit infantry groups on either side of the narrow middle pass swap sides.
                var west = l.PassMouths[2];
                var east = l.PassMouths[3];
                var a = Army(w, spots, p1, new Cell(west.X - 40, west.Y), 2500, 0, Facing.East, 0);
                var b = Army(w, spots, p1, new Cell(east.X + 40, east.Y), 2500, 0, Facing.West, 7);
                groups.Add(Order(w, "eastbound", a, new Cell(east.X + 40, east.Y), perClass: false));
                groups.Add(Order(w, "westbound", b, new Cell(west.X - 40, west.Y), perClass: false));
                break;
            }
            case "forest":
            {
                // 500 infantry and 100 technicals cross the forest block west to east, each class
                // on its own field (infantry straight through, vehicles slower or around).
                var (x0, y0, x1, y1) = l.Forest;
                var my = (y0 + y1) / 2;
                var inf = Army(w, spots, p1, new Cell(x0 - 30, my), 500, 0, Facing.East, 0);
                var veh = Army(w, spots, p1, new Cell(x0 - 30, my + 30), 100, 1, Facing.East, 3);
                groups.Add(Order(w, "infantry", inf, new Cell(x1 + 30, my), perClass: true));
                groups.Add(Order(w, "vehicles", veh, new Cell(x1 + 30, my + 30), perClass: true));
                break;
            }
            default:
                throw new ArgumentException($"move-bench: unknown workload \"{workload}\"; want one of {string.Join(", ", Workloads)}");
        }
        return (w, groups);
    }

    // ---- Running -------------------------------------------------------------------------------

    private static JsonObject RunOne(CompiledMatch match, TerrainMap map, Layout layout, string workload, Options o)
    {
        var sw = Stopwatch.StartNew();
        var (w, groups) = Build(match, map, layout, workload);
        var setupMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        w.PrepareRouting(); // routing's caches up front, like a game's loading screen
        var prepareMs = sw.Elapsed.TotalMilliseconds;
        var driver = new Driver(w, groups);
        w.Probe = driver;
        var phases = Enum.GetValues<StepPhase>();
        var tickMs = new List<double>();
        var moveMs = new List<double>();
        var phaseMs = phases.Select(_ => new List<double>()).ToArray();
        var maxOverlap = 0;
        var overTicks = new List<double>();
        var maxPairsOver10 = 0;
        var overlapTicks = 0;
        var maxStuck = 0;
        var lastStuck = 0;
        var orderable = groups.Sum(g => g.Units.Count);
        var bodies = w.Units.Count(u => u.Inside == 0);
        var wall = Stopwatch.StartNew();
        var resolvedAt = -1;
        // The pass and forest crossings run until 95% of every group arrived (or the cap).
        var maxTicks = workload switch { "pass" => Math.Max(o.MaxTicks, 12000), "forest" => Math.Max(o.MaxTicks, 6000), _ => o.MaxTicks };
        while (w.Tick < maxTicks && wall.Elapsed.TotalSeconds < o.BudgetSeconds)
        {
            w.Step();
            var total = driver.Ticks.Sum();
            tickMs.Add(Ms(total));
            moveMs.Add(Ms(driver.Ticks[(int)StepPhase.GroundMovement]));
            for (var i = 0; i < phases.Length; i++)
            {
                phaseMs[i].Add(Ms(driver.Ticks[i]));
            }
            var (over, over10, stuck) = w.MovementStats();
            overTicks.Add(over);
            maxPairsOver10 = Math.Max(maxPairsOver10, over10);
            if (over > maxOverlap && Environment.GetEnvironmentVariable("MOVEBENCH_DEBUG") == "1")
            {
                Console.WriteLine($"  tick {w.Tick}: overlap {over}% {w.MovementWorst}");
                if (over >= int.Parse(Environment.GetEnvironmentVariable("MOVEBENCH_ASCII") ?? "25", CultureInfo.InvariantCulture))
                {
                    var parts = w.MovementWorst.Split(' ');
                    var ida = uint.Parse(parts[0].Split(':')[0], CultureInfo.InvariantCulture);
                    var idb = uint.Parse(parts[1].Split(':')[0], CultureInfo.InvariantCulture);
                    foreach (var uu in new[] { w.Unit(ida)!, w.Unit(idb)! })
                    {
                        Console.WriteLine($"    {uu.Id} {uu.Def.Archetype.Id} pos={uu.Pos.X.Raw / 1024.0:0.00},{uu.Pos.Y.Raw / 1024.0:0.00} vel={uu.Vel.X.Raw},{uu.Vel.Y.Raw} des={uu.Intent.Desired.X.Raw},{uu.Intent.Desired.Y.Raw} goal={uu.Intent.Goal.X.Raw / 1024.0:0.0},{uu.Intent.Goal.Y.Raw / 1024.0:0.0}");
                    }
                    var at = Cell.Of(w.Unit(ida)!.Pos);
                    var cnt = new int[41 * 21];
                    foreach (var uu in w.Units)
                    {
                        if (uu.Inside != 0) { continue; }
                        var c = Cell.Of(uu.Pos);
                        int gx = c.X - at.X + 20, gy = c.Y - at.Y + 10;
                        if (gx >= 0 && gx < 41 && gy >= 0 && gy < 21) { cnt[gy * 41 + gx] += uu.Def.Archetype.Locomotor == Locomotor.Foot ? 1 : 10; }
                    }
                    for (var gy = 0; gy < 21; gy++)
                    {
                        var sb = new System.Text.StringBuilder("    ");
                        for (var gx = 0; gx < 41; gx++)
                        {
                            var c = new Cell(at.X + gx - 20, at.Y + gy - 10);
                            var n = cnt[gy * 41 + gx];
                            sb.Append(!w.Ground!.Passable(0, c) ? '#' : n >= 10 ? 'V' : n > 0 ? (char)('0' + n) : w.Ground.Forest(c) >= 2 ? 'f' : '.');
                        }
                        Console.WriteLine(sb.ToString());
                    }
                }
            }
            maxOverlap = Math.Max(maxOverlap, over);
            overlapTicks += over >= 10 ? 1 : 0;
            maxStuck = Math.Max(maxStuck, stuck);
            lastStuck = stuck;
            if (groups.Count > 0)
            {
                foreach (var g in groups)
                {
                    if (g.ArrivedAt < 0 && g.Done.Count(d => d) * 100 >= g.Units.Count * 95)
                    {
                        g.ArrivedAt = (int)w.Tick;
                    }
                }
                if (resolvedAt < 0 && groups.All(g => g.ArrivedAt >= 0))
                {
                    resolvedAt = (int)w.Tick;
                    if (workload is "pass" or "forest")
                    {
                        break;
                    }
                }
            }
        }
        var systems = new JsonObject();
        for (var i = 0; i < phases.Length; i++)
        {
            systems[char.ToLowerInvariant(phases[i].ToString()[0]) + phases[i].ToString()[1..]] =
                new JsonObject { ["mean"] = R(phaseMs[i].Average()), ["p99"] = R(Pct(phaseMs[i], 99)), ["max"] = R(phaseMs[i].Max()) };
        }
        if (Environment.GetEnvironmentVariable("MOVEBENCH_DEBUG") == "2")
        {
            foreach (var g in groups)
            {
                for (var k = 0; k < g.Units.Count && k < 400; k++)
                {
                    var u = g.Units[k];
                    if (!g.Done[k])
                    {
                        Console.WriteLine($"  {g.Name} {u.Id} {u.Def.Archetype.Id} pos={u.Pos.X.Raw / 1024.0:0.0},{u.Pos.Y.Raw / 1024.0:0.0} slot={g.Slots[k].X.Raw / 1024.0:0.0},{g.Slots[k].Y.Raw / 1024.0:0.0} goal={u.Intent.Goal.X.Raw / 1024.0:0.0},{u.Intent.Goal.Y.Raw / 1024.0:0.0} des={u.Intent.Desired.X.Raw},{u.Intent.Desired.Y.Raw} vel={u.Vel.X.Raw},{u.Vel.Y.Raw} spd={u.DriveSpeed.Raw} f={u.Facing} stuck={u.Intent.Stuck} forest={w.Ground!.Forest(Cell.Of(u.Pos))} cost={w.Ground.Cost(w.Ground.Rules.ClassOf(u.Def), Cell.Of(u.Pos))}");
                    }
                }
            }
        }
        var groupsJson = new JsonArray();
        foreach (var g in groups)
        {
            var done = g.Done.Count(d => d);
            groupsJson.Add(new JsonObject
            {
                ["name"] = g.Name,
                ["units"] = g.Units.Count,
                ["arrivedPercent"] = R(100.0 * done / Math.Max(1, g.Units.Count)),
                ["arrived95AtTick"] = g.ArrivedAt,
                ["meanCellsLeft"] = R(g.Units.Select((u, k) => (u.Pos - g.Slots[k]).Len().Raw / 1024.0).Average()),
            });
        }
        var res = new JsonObject
        {
            ["workload"] = workload,
            ["orderedUnits"] = orderable,
            ["bodies"] = bodies,
            ["setupMs"] = R(setupMs),
            ["prepareRoutingMs"] = R(prepareMs),
            ["ticks"] = tickMs.Count,
            ["tickMs"] = Stats(tickMs),
            ["movementMs"] = Stats(moveMs),
            ["systemsMs"] = systems,
            ["maxOverlapPercent"] = maxOverlap,
            ["ticksWithOverlapOver10"] = overlapTicks,
            ["overlapPercentPerTick"] = Stats(overTicks),
            ["maxPairsOver10"] = maxPairsOver10,
            ["stuck"] = new JsonObject { ["max"] = maxStuck, ["end"] = lastStuck },
            ["resolvedAtTick"] = resolvedAt,
            ["groups"] = groupsJson,
            ["finalHash"] = w.Hash().ToString("x16", CultureInfo.InvariantCulture),
        };
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{workload,-10} bodies={bodies,-6} ticks={tickMs.Count,-5} tick p50={Pct(tickMs, 50),6:0.00} p99={Pct(tickMs, 99),6:0.00} max={tickMs.Max(),7:0.00}  movement mean={moveMs.Average(),5:0.00} p99={Pct(moveMs, 99),5:0.00} max={moveMs.Max(),6:0.00} ms  overlap max={maxOverlap}% p99={Pct(overTicks, 99)}% (ticks>=10%: {overlapTicks}, pairs>=10% max {maxPairsOver10})  stuck max={maxStuck} end={lastStuck}  resolved@{resolvedAt}  setup {setupMs / 1000:0.0}s routing caches {prepareMs / 1000:0.0}s"));
        foreach (var g in groupsJson)
        {
            Console.WriteLine($"    {g!["name"]}: {g["units"]} units, arrived {g["arrivedPercent"]}%, 95% at tick {g["arrived95AtTick"]}, mean {g["meanCellsLeft"]} cells left");
        }
        return res;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double R(double v) => Math.Round(v, 4);

    private static double Pct(List<double> xs, int p)
    {
        var s = xs.Order().ToList();
        var i = (int)Math.Ceiling(p / 100.0 * s.Count) - 1;
        return s[Math.Clamp(i, 0, s.Count - 1)];
    }

    private static JsonObject Stats(List<double> xs) => new()
    {
        ["mean"] = R(xs.Average()),
        ["p50"] = R(Pct(xs, 50)),
        ["p95"] = R(Pct(xs, 95)),
        ["p99"] = R(Pct(xs, 99)),
        ["max"] = R(xs.Max()),
    };
}
