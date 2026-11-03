// StraightBench measures how straight ground units go where the ground lets them: seeded random trips across a region of
// a map (the game's zuberec_02 valley floor, or a flat map with rocks), and per movement class how long each route is
// against the straight line, how far it strays from it and how often it weaves (docs/design.md, "Straight routes").
//
//   dotnet run -c Release --project src/SimRun -- -straight-bench DIR [-trips 300] [-region 200,900,2600,1300] [-seed 7]
//       [-move 60] [-groups 20] [-out FILE]
//   dotnet run -c Release --project src/SimRun -- -straight-bench flat [-size 1024] ...
//
// DIR holds a map.json (its heightmap and forest map: the game's client/data/maps/zuberec_02); "flat" is a map without
// terrain, size x size cells with rock blobs. For each trip (both ends passable and connected for every class, 40 to 300
// cells apart) it plans each class's route as the path system does (World.RouteCellsFor: HPA*, the legs refined and
// smoothed chunk by chunk) and reports per class, mean and p95:
// (each route as the polyline a unit drives: its cells' centres simplified to within a cell of every one, so the raster
// of a straight line is one segment):
//   ratio      the route's length over the straight distance;
//   maxDev     the farthest a cell gets from the straight line, in cells;
//   wiggles    heading reversals per 100 m: a turn back by 10 degrees or more after turning the other way;
//   turn       degrees of turning per 100 m.
// Then (-move N) the first N trips driven alone by a Humvee, a soldier and a tank, and (-groups N) N trips by a column
// (a Humvee, a cargo truck, a tank) and four riflemen given one group order: per kind the distance driven over the straight
// one, the time over the straight distance at top speed, the heading's reversals per 100 m (swings of 3 degrees or more,
// tick by tick) and its turning per 100 m.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static class StraightBench
{
    private const double CellMetres = 2.0;

    private static readonly (string Name, int Player)[] RouteKinds =
    [
        ("Rifleman", 0), ("technical", 1), ("humvee", 0), ("sentinel_cargo_truck", 0), ("main_battle_tank", 0),
    ];

    private static readonly string[] MoveKinds = ["humvee", "Rifleman", "main_battle_tank"];

    public static int Run(string mapArg, int trips, string region, int moveTrips, int groupTrips, int size, ulong seed,
        string contentDir, string setupPath, string outPath)
    {
        var content = ContentLoader.LoadDirectory(contentDir);
        var setup = Replay.LoadSetup(setupPath).For(content, 42);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var match = MatchCompiler.Compile(content, setup);
        TerrainMap? map = null;
        Grid grid;
        if (mapArg == "flat")
        {
            grid = Rocks(size);
        }
        else
        {
            var rules = TerrainLoader.LoadRules(Path.Combine(contentDir, "..", "terrain", "rules.json"));
            map = TerrainLoader.LoadMap(Path.Combine(mapArg, "map.json"), rules, content);
            grid = new Grid(map.Width, map.Height);
        }
        var w = new World(grid, match);
        if (map is not null)
        {
            w.AttachTerrain(map);
        }
        var sw = Stopwatch.StartNew();
        w.PrepareRouting(everySector: false);
        var prepareMs = sw.Elapsed.TotalMilliseconds;
        int x0 = 0, y0 = 0, x1 = grid.Width - 1, y1 = grid.Height - 1;
        if (region != "")
        {
            var p = region.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            (x0, y0, x1, y1) = (p[0], p[1], p[2], p[3]);
        }
        var r = w.Routing;
        var defs = RouteKinds.Select(k => w.Match.Players[k.Player].Find(content, k.Name)!).ToArray();
        var classes = defs.Select(d => r.ClassOf(d)).ToArray();
        var rng = new Random((int)seed);
        var list = new List<(Cell S, Cell D)>();
        var tries = 0;
        while (list.Count < trips && tries++ < trips * 2000)
        {
            var s = new Cell(rng.Next(x0, x1 + 1), rng.Next(y0, y1 + 1));
            var d = new Cell(rng.Next(x0, x1 + 1), rng.Next(y0, y1 + 1));
            var dist = Math.Sqrt(Sq(s.X - d.X) + Sq(s.Y - d.Y));
            if (dist < 40 || dist > 300)
            {
                continue;
            }
            var ok = true;
            foreach (var k in classes)
            {
                int si = r.Index(s), di = r.Index(d);
                ok &= r.Passable(k, si) && r.Passable(k, di) && r.Component(k, si) == r.Component(k, di);
            }
            if (ok)
            {
                list.Add((s, d));
            }
        }
        Console.Error.WriteLine($"straight-bench: {list.Count} trips over {x0},{y0}-{x1},{y1}, routing prepared in {prepareMs:0} ms");

        var result = new Dictionary<string, object>
        {
            ["map"] = mapArg == "flat" ? $"flat {size} x {size} with rocks" : Path.GetFileName(Path.GetFullPath(mapArg).TrimEnd('/')),
            ["region"] = $"{x0},{y0},{x1},{y1}",
            ["trips"] = list.Count,
            ["seed"] = seed,
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        if (map is not null)
        {
            result["terrain"] = TerrainStats(map, x0, y0, x1, y1);
        }
        var routes = new Dictionary<string, object>();
        for (var i = 0; i < defs.Length; i++)
        {
            var ratio = new List<double>();
            var dev = new List<double>();
            var wig = new List<double>();
            var turn = new List<double>();
            var failed = 0;
            sw.Restart();
            foreach (var (s, d) in list)
            {
                var cells = w.RouteCellsFor(defs[i], s, d);
                if (cells.Count < 2 || Cheb(cells[^1], d) > 2)
                {
                    failed++;
                    continue;
                }
                var m = PathMetrics(cells, s, d);
                ratio.Add(m.Ratio);
                dev.Add(m.MaxDev);
                wig.Add(m.Wiggles);
                turn.Add(m.Turn);
            }
            var ms = sw.Elapsed.TotalMilliseconds / Math.Max(1, list.Count);
            var tc = map is null ? "flat" : map.Rules.Classes[r.Nav.TerrainClass(classes[i])].Id;
            routes[RouteKinds[i].Name] = new Dictionary<string, object>
            {
                ["class"] = $"{tc}, width {r.Nav.Width(classes[i])}",
                ["failed"] = failed,
                ["ratio"] = Stat(ratio, 4),
                ["maxDevCells"] = Stat(dev, 2),
                ["wigglesPer100m"] = Stat(wig, 3),
                ["turnDegPer100m"] = Stat(turn, 1),
                ["planMs"] = Math.Round(ms, 3),
            };
            Console.Error.WriteLine($"  {RouteKinds[i].Name,-22} ratio {Mean(ratio):0.0000} p95 {P95(ratio):0.0000}  dev {Mean(dev):0.00} p95 {P95(dev):0.00}" +
                $"  wiggles/100m {Mean(wig):0.000} p95 {P95(wig):0.000}  turn/100m {Mean(turn):0.0} p95 {P95(turn):0.0}  ({failed} failed, {ms:0.00} ms a route)");
        }
        result["routes"] = routes;

        if (moveTrips > 0)
        {
            result["move"] = Drive(w, content, list.Take(moveTrips).ToList(), group: false);
        }
        if (groupTrips > 0)
        {
            result["group"] = Drive(w, content, list.Skip(moveTrips).Take(groupTrips).ToList(), group: true);
        }
        var text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(text);
        if (outPath != "")
        {
            File.WriteAllText(outPath, text + "\n");
        }
        return 0;
    }

    private static double Sq(double v) => v * v;

    // TerrainStats: over the region, the share of cells per grade class (the foot class's thresholds), at 7 to 13% grade, and
    // of east-going steps (centre to centre) that climb and by how much; and how many of each gentle cell's 8 neighbours are
    // gentle or steeper.
    private static Dictionary<string, object> TerrainStats(TerrainMap map, int x0, int y0, int x1, int y1)
    {
        var mc = map.Rules.Classes[0];
        long cells = 0, flat = 0, gentle = 0, steep = 0, cliff = 0, flicker = 0, steps = 0, climbs = 0;
        var under = new long[4]; // climbs under 5, 10, 15, 20 cm
        var nb = new long[9];
        var cs = map.CentreSums;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var g = map.Grade(new Cell(x, y));
                cells++;
                switch (mc.Classify(g))
                {
                    case SlopeClass.Flat: flat++; break;
                    case SlopeClass.Gentle: gentle++; break;
                    case SlopeClass.Steep: steep++; break;
                    default: cliff++; break;
                }
                flicker += g >= 7 && g <= 13 ? 1 : 0;
                if (mc.Classify(g) == SlopeClass.Gentle)
                {
                    var n = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            n += (dx != 0 || dy != 0) && mc.Classify(map.Grade(new Cell(x + dx, y + dy))) >= SlopeClass.Gentle ? 1 : 0;
                        }
                    }
                    nb[n]++;
                }
                if (x < x1)
                {
                    steps++;
                    var rise = (cs[y * map.Width + x + 1] - cs[y * map.Width + x]) / 4.0 / Fixed.OneRaw * CellMetres; // metres
                    if (rise > 0)
                    {
                        climbs++;
                        for (var k = 0; k < 4; k++)
                        {
                            under[k] += rise < 0.05 * (k + 1) ? 1 : 0;
                        }
                    }
                }
            }
        }
        double Pc(long a, long b) => Math.Round(100.0 * a / Math.Max(b, 1), 1);
        return new Dictionary<string, object>
        {
            ["cells"] = cells,
            ["flatPct"] = Pc(flat, cells), ["gentlePct"] = Pc(gentle, cells), ["steepPct"] = Pc(steep, cells), ["cliffPct"] = Pc(cliff, cells),
            ["grade7to13Pct"] = Pc(flicker, cells),
            ["eastStepsClimbingPct"] = Pc(climbs, steps),
            ["climbsUnder5cmPct"] = Pc(under[0], climbs), ["climbsUnder10cmPct"] = Pc(under[1], climbs),
            ["climbsUnder15cmPct"] = Pc(under[2], climbs), ["climbsUnder20cmPct"] = Pc(under[3], climbs),
            ["gentleWithGentleNeighboursPct"] = nb.Select(n => Pc(n, gentle)).ToArray(),
        };
    }

    private static int Cheb(Cell a, Cell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    // PathMetrics: the route as a unit drives it, its cells' centres simplified to the polyline that keeps within a cell
    // of every one (Douglas-Peucker: a raster of a straight line is one segment, a dodge round a cell a bend): its length over
    // the straight distance, the farthest a cell gets from the straight line (cells), and its reversals (a turn back by 10
    // degrees or more after one the other way) and turning, per 100 m.
    private static (double Ratio, double MaxDev, double Wiggles, double Turn) PathMetrics(List<Cell> cells, Cell s, Cell d)
    {
        double dev = 0;
        double lx = d.X - s.X, ly = d.Y - s.Y, ll = Math.Sqrt(lx * lx + ly * ly);
        foreach (var c in cells)
        {
            dev = Math.Max(dev, Math.Abs((c.X - s.X) * ly - (c.Y - s.Y) * lx) / ll);
        }
        var keep = new bool[cells.Count];
        keep[0] = keep[^1] = true;
        Simplify(cells, 0, cells.Count - 1, 1.0, keep);
        var pts = cells.Where((_, i) => keep[i]).ToList();
        double len = 0;
        var heads = new List<double>();
        for (var i = 1; i < pts.Count; i++)
        {
            len += Math.Sqrt(Sq(pts[i].X - pts[i - 1].X) + Sq(pts[i].Y - pts[i - 1].Y));
            heads.Add(Math.Atan2(pts[i].Y - pts[i - 1].Y, pts[i].X - pts[i - 1].X) * 180 / Math.PI);
        }
        var (rev, turn) = Reversals(heads, 10);
        var metres = Math.Max(len * CellMetres, 1);
        return (len / ll, dev, rev * 100 / metres, turn * 100 / metres);
    }

    // Simplify marks the cells Douglas-Peucker keeps between a and b (both kept): the farthest from the chord a-b when it is
    // more than eps cells off it, and so on either side.
    private static void Simplify(List<Cell> cells, int a, int b, double eps, bool[] keep)
    {
        if (b <= a + 1)
        {
            return;
        }
        double ax = cells[a].X, ay = cells[a].Y, dx = cells[b].X - ax, dy = cells[b].Y - ay, l = Math.Sqrt(dx * dx + dy * dy);
        int far = -1;
        double worst = eps;
        for (var i = a + 1; i < b; i++)
        {
            var off = l == 0 ? Math.Sqrt(Sq(cells[i].X - ax) + Sq(cells[i].Y - ay)) : Math.Abs((cells[i].X - ax) * dy - (cells[i].Y - ay) * dx) / l;
            if (off > worst)
            {
                (worst, far) = (off, i);
            }
        }
        if (far < 0)
        {
            return;
        }
        keep[far] = true;
        Simplify(cells, a, far, eps, keep);
        Simplify(cells, far, b, eps, keep);
    }

    // Reversals counts the heading's swings back by at least threshold degrees after turning the other way, and the total
    // turning (degrees).
    private static (int Count, double Turn) Reversals(List<double> heads, double threshold)
    {
        if (heads.Count < 2)
        {
            return (0, 0);
        }
        var ext = heads[0];
        var dir = 0;
        var count = 0;
        double turn = 0;
        for (var i = 1; i < heads.Count; i++)
        {
            turn += Math.Abs(Wrap(heads[i] - heads[i - 1]));
            var dd = Wrap(heads[i] - ext);
            if (dir == 0)
            {
                if (Math.Abs(dd) >= threshold)
                {
                    dir = Math.Sign(dd);
                    ext = heads[i];
                }
            }
            else if (dir * dd > 0)
            {
                ext = heads[i];
            }
            else if (-dir * dd >= threshold)
            {
                count++;
                dir = -dir;
                ext = heads[i];
            }
        }
        return (count, turn);
    }

    private static double Wrap(double a)
    {
        while (a > 180)
        {
            a -= 360;
        }
        while (a < -180)
        {
            a += 360;
        }
        return a;
    }

    // Drive runs the trips with real units (one kind at a time alone, or a column and four riflemen in one group order)
    // and measures their motion tick by tick; done units are parked off the region.
    private static Dictionary<string, object> Drive(World w, ContentSet content, List<(Cell S, Cell D)> trips, bool group)
    {
        var p1 = w.Match.Players[0];
        var rifleman = p1.Find(content, "Rifleman")!;
        var stats = new Dictionary<string, (List<double> Dist, List<double> Time, List<double> Rev, List<double> Turn, List<double> Flips, int Late)>();
        var parked = 0;
        var sw = Stopwatch.StartNew();
        long ticks = 0;
        void Add(string kind, Track t, double straight, double speed)
        {
            if (!stats.TryGetValue(kind, out var s))
            {
                s = ([], [], [], [], [], 0);
            }
            var (rev, turn) = Reversals(t.Heads, 3);
            var metres = Math.Max(t.Travelled * CellMetres, 1);
            s.Dist.Add(t.Travelled / straight);
            s.Time.Add(t.Ticks / (straight / Math.Max(speed, 1e-6)));
            s.Rev.Add(rev * 100 / metres);
            s.Turn.Add(turn * 100 / metres);
            s.Flips.Add(t.FacingFlips * 100 / metres);
            if (!t.Arrived)
            {
                s.Late++;
            }
            stats[kind] = s;
        }
        var kinds = group ? [string.Join("+", MoveKinds)] : MoveKinds;
        // STRAIGHT_TRACE=FILE writes every tick of every unit (kind, trip, tick, x, y, heading, path index and length, goal).
        using var trace = Environment.GetEnvironmentVariable("STRAIGHT_TRACE") is { Length: > 0 } tf ? new StreamWriter(tf + (group ? ".group" : "") + ".csv") : null;
        var tripNo = -1;
        foreach (var kind in kinds)
        {
            foreach (var (s, d) in trips)
            {
                tripNo++;
                var units = new List<(uint Id, string Kind)>();
                var drivers = new List<uint>();
                var names = group ? ["humvee", "sentinel_cargo_truck", "main_battle_tank", "Rifleman", "Rifleman", "Rifleman", "Rifleman"] : new[] { kind };
                // Facing the trip's way, so the turn on the spot at the start doesn't count as weaving.
                Facing.TryFromDirection(d.Center() - s.Center(), out var face);
                for (var i = 0; i < names.Length; i++)
                {
                    var def = p1.Find(content, names[i])!;
                    var at = Near(w, group ? new Cell(s.X - (i < 3 ? 0 : 6), s.Y + (i < 3 ? (i - 1) * 7 : (i - 4) * 2)) : s, def);
                    var id = w.Spawn(def, at, face);
                    if (def.Archetype.Crew is not null)
                    {
                        var drv = w.Spawn(rifleman, at, face);
                        w.Issue(new Command { Tick = w.Tick + 1, Player = p1.Id, Kind = CommandKind.Enter, Units = [drv], Target = id });
                        drivers.Add(drv);
                    }
                    units.Add((id, names[i]));
                }
                for (var i = 0; i < 200 && drivers.Any(x => w.Unit(x) is { Inside: 0 }); i++)
                {
                    w.Step();
                }
                var order = w.Tick + 1;
                w.Issue(new Command { Tick = order, Player = p1.Id, Kind = CommandKind.Move, Units = [.. units.Select(u => u.Id)], Cell = d });
                var straight = Math.Sqrt(Sq(s.X - d.X) + Sq(s.Y - d.Y));
                var slowest = units.Min(u => w.Unit(u.Id)!.Def.Speed.Raw) / (double)Fixed.OneRaw; // cells a tick
                var limit = order + (uint)(straight * 3 / slowest) + 400;
                var tracks = units.Select(_ => new Track()).ToArray();
                while (w.Tick < limit)
                {
                    w.Step();
                    ticks++;
                    var moving = false;
                    for (var i = 0; i < units.Count; i++)
                    {
                        var u = w.Unit(units[i].Id)!;
                        var t = tracks[i];
                        if (w.Tick <= order || t.Arrived)
                        {
                            continue;
                        }
                        if (u.Moving || u.Vel.LenSq() > 0)
                        {
                            moving = true;
                            t.Ticks = w.Tick - order;
                            var v = u.Vel;
                            var step = Math.Sqrt(Sq(v.X.Raw) + Sq(v.Y.Raw)) / Fixed.OneRaw;
                            t.Travelled += step;
                            if (step > 0.002)
                            {
                                var h = u.Def.Archetype.Locomotor == Locomotor.Foot ? v : u.Heading;
                                t.Heads.Add(Math.Atan2(h.Y.Raw, h.X.Raw) * 180 / Math.PI);
                            }
                            // The facing (32 directions, what the client draws a unit's yaw from): its turns, and its flips
                            // (a turn back the other way right after one).
                            if (t.LastFacing >= 0 && u.Facing != t.LastFacing)
                            {
                                var turnDir = ((u.Facing - t.LastFacing + 48) % 32) - 16 > 0 ? 1 : -1;
                                t.FacingTurns++;
                                t.FacingFlips += t.LastTurn == -turnDir ? 1 : 0;
                                t.LastTurn = turnDir;
                            }
                            t.LastFacing = u.Facing;
                            trace?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"{units[i].Kind},{tripNo},{w.Tick - order},{u.Pos.X.Raw / 1024.0:0.000},{u.Pos.Y.Raw / 1024.0:0.000},{(t.Heads.Count > 0 ? t.Heads[^1] : 0):0.0},{u.PathIdx},{u.Path.Count},{u.Intent.Goal.X.Raw / 1024.0:0.00},{u.Intent.Goal.Y.Raw / 1024.0:0.00}"));
                        }
                        else if (!u.Moving && !u.PathPending && u.Order.Kind == OrderKind.Idle)
                        {
                            t.Arrived = true;
                        }
                    }
                    if (!moving && tracks.All(t => t.Arrived))
                    {
                        break;
                    }
                }
                for (var i = 0; i < units.Count; i++)
                {
                    var u = w.Unit(units[i].Id)!;
                    var end = Math.Sqrt(Sq(u.Pos.X.Raw / (double)Fixed.OneRaw - d.X - 0.5) + Sq(u.Pos.Y.Raw / (double)Fixed.OneRaw - d.Y - 0.5));
                    tracks[i].Arrived &= end < 12;
                    Add(units[i].Kind, tracks[i], straight, u.Def.Speed.Raw / (double)Fixed.OneRaw);
                    // Park it off the region (south-west corner rows), where nothing else goes.
                    u.Pos = new Cell(2 + parked % 200 * 6, w.Grid.Height - 3 - parked / 200 * 6).Center();
                    u.ClearPath();
                    u.Order = new Order(OrderKind.Idle, default, 0, 0);
                    parked++;
                }
            }
        }
        var res = new Dictionary<string, object>();
        foreach (var (kind, s) in stats.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            res[kind] = new Dictionary<string, object>
            {
                ["runs"] = s.Dist.Count,
                ["notArrived"] = s.Late,
                ["distRatio"] = Stat(s.Dist, 4),
                ["timeRatio"] = Stat(s.Time, 3),
                ["reversalsPer100m"] = Stat(s.Rev, 3),
                ["turnDegPer100m"] = Stat(s.Turn, 1),
                ["facingFlipsPer100m"] = Stat(s.Flips, 3),
            };
            Console.Error.WriteLine($"  {(group ? "group " : "alone ")}{kind,-22} dist {Mean(s.Dist):0.0000} p95 {P95(s.Dist):0.0000}  time {Mean(s.Time):0.000}" +
                $"  rev/100m {Mean(s.Rev):0.000} p95 {P95(s.Rev):0.000}  turn/100m {Mean(s.Turn):0.0} p95 {P95(s.Turn):0.0}  flips/100m {Mean(s.Flips):0.00} p95 {P95(s.Flips):0.00}  ({s.Late} not arrived)");
        }
        res["msPerTick"] = Math.Round(sw.Elapsed.TotalMilliseconds / Math.Max(1, ticks), 3);
        return res;
    }

    private sealed class Track
    {
        public double Travelled;
        public long Ticks;
        public bool Arrived;
        public readonly List<double> Heads = [];
        public int LastFacing = -1, LastTurn, FacingTurns, FacingFlips;
    }

    // Near is the nearest cell to c that the def's class can stand in.
    private static Cell Near(World w, Cell c, UnitDef def)
    {
        var r = w.Routing;
        var k = r.ClassOf(def);
        var i = k < 0 ? -1 : r.NearestPassable(k, c, 16);
        return i < 0 ? c : r.CellAt(i);
    }

    private static Dictionary<string, double> Stat(List<double> xs, int digits) => new()
    {
        ["mean"] = Math.Round(Mean(xs), digits),
        ["p95"] = Math.Round(P95(xs), digits),
        ["max"] = Math.Round(xs.Count == 0 ? 0 : xs.Max(), digits),
    };

    private static double Mean(List<double> xs) => xs.Count == 0 ? 0 : xs.Average();

    private static double P95(List<double> xs)
    {
        if (xs.Count == 0)
        {
            return 0;
        }
        var s = xs.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, (int)Math.Ceiling(s.Count * 0.95) - 1)];
    }

    // Rocks: a flat size x size map with round rock blobs over about 5% of it (RoutingBench's).
    private static Grid Rocks(int size)
    {
        var rng = new Random(3);
        var g = new Grid(size, size);
        for (var i = 0; i < size * size / 2000; i++)
        {
            int x = rng.Next(size), y = rng.Next(size), r = 2 + rng.Next(8);
            for (var yy = Math.Max(y - r, 0); yy < Math.Min(y + r, size); yy++)
            {
                for (var xx = Math.Max(x - r, 0); xx < Math.Min(x + r, size); xx++)
                {
                    if ((xx - x) * (xx - x) + (yy - y) * (yy - y) <= r * r)
                    {
                        g.Set(new Cell(xx, yy), Terrain.Blocked);
                    }
                }
            }
        }
        return g;
    }
}
