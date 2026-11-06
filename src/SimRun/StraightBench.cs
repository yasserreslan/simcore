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
// tick by tick), its turning per 100 m and its facing's flips (a turn back the other way) per 100 m, all of them and those within
// 5 s of the turn they undo (a slower one is the route's bend, not a swerve). -fights N: at N of the sites eight vehicles
// attack-move into an enemy group (Battle). -look lips|blockers|climbs|climbroads|border: instead of all that, what the ground is
// made of (docs/design.md, "Straight routes"): its steep cells by the rise round them, what stops each trip's straight line, a
// truck's way up 30 m or more (with both ends on a road: climbroads), and passability and arrival at the map's edge.
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
    private const int QuickTicks = 100; // a facing flip within 5 s of the turn it undoes: a swerve; a slower one is a bend of the route
    private const double ShortCells = 180; // a short trip (content/terrain/rules.json, shortTrip): the bench's split, not the rule

    private static readonly (string Name, int Player)[] RouteKinds =
    [
        ("Rifleman", 0), ("technical", 1), ("humvee", 0), ("sentinel_cargo_truck", 0), ("main_battle_tank", 0),
    ];

    private static readonly string[] MoveKinds = ["humvee", "Rifleman", "main_battle_tank"];

    public static int Run(string mapArg, int trips, string region, int moveTrips, int groupTrips, int size, ulong seed,
        string contentDir, string setupPath, string outPath, int battles = 0, string look = "")
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
            var loading = Stopwatch.StartNew();
            map = TerrainLoader.LoadMap(Path.Combine(mapArg, "map.json"), rules, content);
            Console.Error.WriteLine($"straight-bench: map loaded in {loading.Elapsed.TotalMilliseconds:0} ms");
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
        Console.Error.WriteLine($"straight-bench: {list.Count} trips over {x0},{y0}-{x1},{y1}, routing prepared in {prepareMs:0} ms, {r.Nav.Classes} navigation classes, grids {r.Bytes().Nav / 1048576} MiB");

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
            if (look == "lips")
            {
                LipStats(map, x0, y0, x1, y1);
                return 0;
            }
        }
        if (map is not null && look == "blockers")
        {
            Blockers(map, r, list, RouteKinds[3].Name, classes[3]);
            Blockers(map, r, list, RouteKinds[2].Name, classes[2]);
            Blockers(map, r, list, RouteKinds[4].Name, classes[4]);
            return 0;
        }
        if (map is not null && look == "border")
        {
            Border(w, content, map, classes, defs);
            return 0;
        }
        if (map is not null && look is "climbs" or "climbroads")
        {
            Climbs(w, map!, defs[3], classes[3], x0, y0, x1, y1, trips, new Random((int)seed + 1), look == "climbroads");
            return 0;
        }
        var routes = new Dictionary<string, object>();
        for (var i = 0; i < defs.Length; i++)
        {
            var ratio = new List<double>();
            var dev = new List<double>();
            var wig = new List<double>();
            var turn = new List<double>();
            var failed = 0;
            var ratioShort = new List<double>();
            var ratioOpen = new List<double>();
            var ratioLong = new List<double>();
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
                var dist = Math.Sqrt(Sq(s.X - d.X) + Sq(s.Y - d.Y));
                (dist <= ShortCells ? ratioShort : ratioLong).Add(m.Ratio);
                if (dist <= ShortCells && r.LineCost(classes[i], r.Index(s), r.Index(d)) != SimCore.Sim.Router.Unreachable && r.Sight(classes[i], r.Index(s), r.Index(d)))
                {
                    ratioOpen.Add(m.Ratio);
                }
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
                ["ratioShortTrips"] = Stat(ratioShort, 4),
                ["ratioOpenShortTrips"] = Stat(ratioOpen, 4),
                ["openShortTrips"] = ratioOpen.Count,
                ["ratioLongTrips"] = Stat(ratioLong, 4),
                ["maxDevCells"] = Stat(dev, 2),
                ["wigglesPer100m"] = Stat(wig, 3),
                ["turnDegPer100m"] = Stat(turn, 1),
                ["planMs"] = Math.Round(ms, 3),
            };
            Console.Error.WriteLine($"  {RouteKinds[i].Name,-22} short {Mean(ratioShort):0.000} ({ratioShort.Count}), open short {Mean(ratioOpen):0.000} ({ratioOpen.Count}), long {Mean(ratioLong):0.000} ({ratioLong.Count})");
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
        if (battles > 0)
        {
            result["battle"] = Battle(w, content, list, battles);
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

    // Battle: at each of n sites eight vehicles (four Humvees, four tanks, crewed) attack-move into an enemy group 60 cells ahead of
    // them (technicals and riflemen), for at most a minute: their facing's flips and turning, per 100 m and per minute, how
    // much of the turning they do standing still, and how often they report stuck. Done units are parked in far corners.
    private static Dictionary<string, object> Battle(World w, ContentSet content, List<(Cell S, Cell D)> sites, int n)
    {
        var p1 = w.Match.Players[0];
        var p2 = w.Match.Players[1];
        var rifle1 = p1.Find(content, "Rifleman")!;
        var rifle2 = p2.Find(content, "Rifleman")!;
        var hum = p1.Find(content, "humvee")!;
        var tank = p1.Find(content, "main_battle_tank")!;
        var tech = p2.Find(content, "technical")!;
        var flipsPer100 = new List<double>();
        var flipsPerMin = new List<double>();
        var turnPerMin = new List<double>();
        var stillPerMin = new List<double>();
        var quick = new List<double>();
        long stucks = 0;
        var parked = 0;
        uint Crewed(UnitDef def, UnitDef rifle, byte player, Cell at, int face)
        {
            var id = w.Spawn(def, Near(w, at, def), face);
            if (def.Archetype.Crew is not null)
            {
                var drv = w.Spawn(rifle, Near(w, at, def), face);
                w.Issue(new Command { Tick = w.Tick + 1, Player = player, Kind = CommandKind.Enter, Units = [drv], Target = id });
            }
            return id;
        }
        foreach (var (s, d0) in sites.Where(t => Math.Sqrt(Sq(t.S.X - t.D.X) + Sq(t.S.Y - t.D.Y)) >= 60).Take(n))
        {
            double ux = d0.X - s.X, uy = d0.Y - s.Y, ul = Math.Sqrt(ux * ux + uy * uy);
            (ux, uy) = (ux / ul, uy / ul);
            Cell Off(double along, double side) => new((int)(s.X + ux * along - uy * side), (int)(s.Y + uy * along + ux * side));
            Facing.TryFromDirection(d0.Center() - s.Center(), out var face);
            var mine = new List<uint>();
            var theirs = new List<uint>();
            for (var i = 0; i < 8; i++)
            {
                mine.Add(Crewed(i < 4 ? hum : tank, rifle1, p1.Id, Off(i < 4 ? 0 : -8, (i % 4 - 1.5) * 7), face));
            }
            for (var i = 0; i < 10; i++)
            {
                theirs.Add(Crewed(i < 4 ? tech : rifle2, rifle2, p2.Id, Off(60 + (i % 3) * 5, (i % 4 - 1.5) * 6), Facing.Wrap(face + Facing.Count / 2)));
            }
            for (var i = 0; i < 60; i++)
            {
                w.Step();
            }
            var goal = Off(75, 0);
            w.Issue(new Command { Tick = w.Tick + 1, Player = p1.Id, Kind = CommandKind.AttackMove, Units = [.. mine], Cell = goal });
            var track = mine.ToDictionary(x => x, _ => (Last: -1, LastTurn: 0, LastTick: 0L, Flips: 0, Quick: 0, Dist: 0.0, Turn: 0.0, Still: 0.0, Ticks: 0L, WasStuck: false));
            var start = w.Tick;
            for (var t = 0; t < 1200; t++)
            {
                w.Step();
                var live = false;
                foreach (var id in mine)
                {
                    if (w.Unit(id) is not { } u)
                    {
                        continue;
                    }
                    var k = track[id];
                    var step = Math.Sqrt(Sq(u.Vel.X.Raw) + Sq(u.Vel.Y.Raw)) / Fixed.OneRaw;
                    k.Dist += step;
                    k.Ticks++;
                    live |= true;
                    if (k.Last >= 0 && u.Facing != k.Last)
                    {
                        var d = ((u.Facing - k.Last + 48) % 32) - 16;
                        var deg = Math.Abs(d) * 11.25;
                        k.Turn += deg;
                        k.Still += step < 0.01 ? deg : 0;
                        var dir = d > 0 ? 1 : -1;
                        if (k.LastTurn == -dir)
                        {
                            k.Flips++;
                            k.Quick += w.Tick - k.LastTick < 40 ? 1 : 0;
                        }
                        k.LastTurn = dir;
                        k.LastTick = w.Tick;
                    }
                    k.Last = u.Facing;
                    if (u.Intent.Stuck && !k.WasStuck)
                    {
                        stucks++;
                    }
                    k.WasStuck = u.Intent.Stuck;
                    track[id] = k;
                }
                if (!live || (theirs.All(x => w.Unit(x) is null) && mine.All(x => w.Unit(x) is not { } u || !u.Moving)))
                {
                    break;
                }
            }
            foreach (var (id, k) in track)
            {
                var minutes = Math.Max(k.Ticks, 1) / 1200.0;
                flipsPer100.Add(k.Flips * 100 / Math.Max(k.Dist * CellMetres, 10));
                flipsPerMin.Add(k.Flips / minutes);
                turnPerMin.Add(k.Turn / minutes);
                stillPerMin.Add(k.Still / minutes);
                quick.Add(k.Quick / minutes);
            }
            foreach (var (list, x, y, sign) in new[] { (mine, 2, w.Grid.Height - 3, 1), (theirs, w.Grid.Width - 3, 2, -1) })
            {
                foreach (var id in list)
                {
                    if (w.Unit(id) is { } u)
                    {
                        u.Pos = new Cell(x + sign * (parked % 200) * 6, y - sign * (parked / 200) * 6).Center();
                        u.ClearPath();
                        u.Order = new Order(OrderKind.Idle, default, 0, 0);
                        parked++;
                    }
                }
            }
        }
        Console.Error.WriteLine($"  battle: flips/100m {Mean(flipsPer100):0.00} p95 {P95(flipsPer100):0.00}, flips/min {Mean(flipsPerMin):0.0} p95 {P95(flipsPerMin):0.0}, quick flips/min {Mean(quick):0.0}, turning/min {Mean(turnPerMin):0} p95 {P95(turnPerMin):0}, standing still {Mean(stillPerMin):0}/min, stuck {stucks} times ({flipsPer100.Count} vehicles)");
        return new Dictionary<string, object>
        {
            ["vehicles"] = flipsPer100.Count,
            ["flipsPer100m"] = Stat(flipsPer100, 3),
            ["flipsPerMinute"] = Stat(flipsPerMin, 2),
            ["quickFlipsPerMinute"] = Stat(quick, 2),
            ["turnDegPerMinute"] = Stat(turnPerMin, 0),
            ["stillTurnDegPerMinute"] = Stat(stillPerMin, 0),
            ["stuckEvents"] = stucks,
        };
    }

    // Border: per class and width, the share of cells passable by their distance from the map's edge; then a unit of each kind
    // ordered to cells on the edge of the map from 30 cells inside it: how far from the edge it ends.
    private static void Border(World w, ContentSet content, TerrainMap map, int[] classes, UnitDef[] defs)
    {
        var r = w.Routing;
        int W = map.Width, H = map.Height;
        var rows = new[] { 0, 1, 2, 3, 4, 5, 6, 8, 12, 20, 40 };
        for (var c = 0; c < classes.Length; c++)
        {
            var k = classes[c];
            var line = $"{RouteKinds[c].Name,-22} w{r.Nav.Width(k)}:";
            foreach (var at in rows)
            {
                long open = 0, all = 0;
                for (var x = at; x < W - at; x++)
                {
                    all += 2;
                    open += (r.Passable(k, r.Index(new Cell(x, at))) ? 1 : 0) + (r.Passable(k, r.Index(new Cell(x, H - 1 - at))) ? 1 : 0);
                }
                for (var y = at + 1; y < H - at - 1; y++)
                {
                    all += 2;
                    open += (r.Passable(k, r.Index(new Cell(at, y))) ? 1 : 0) + (r.Passable(k, r.Index(new Cell(W - 1 - at, y))) ? 1 : 0);
                }
                line += $" {at}:{100.0 * open / all:0}";
            }
            Console.Error.WriteLine(line);
        }
        foreach (var at in rows)
        {
            long gr = 0, n = 0, steep = 0;
            for (var x = at; x < W - at; x++)
            {
                foreach (var y in new[] { at, H - 1 - at })
                {
                    var g = map.Grade(new Cell(x, y));
                    gr += g;
                    n++;
                    steep += g >= 25 ? 1 : 0;
                }
            }
            Console.Error.WriteLine($"  north and south rows {at} from the edge: mean grade {1.0 * gr / n:0.0}, steep or more {100.0 * steep / n:0.0}%");
        }
        var p1 = w.Match.Players[0];
        var rifleman = p1.Find(content, "Rifleman")!;
        var rng = new Random(11);
        foreach (var kind in MoveKinds.Append("sentinel_cargo_truck"))
        {
            var def = p1.Find(content, kind)!;
            var k = r.ClassOf(def);
            var edge = new List<double>();
            var off = new List<double>();
            for (var trial = 0; trial < 40; trial++)
            {
                // A passable start 30 cells in, a goal on the edge straight out from it, on a side chosen in turn.
                var side = trial % 4;
                var along = rng.Next(40, (side < 2 ? W : H) - 40);
                Cell Pick(int inward) => side switch { 0 => new Cell(along, inward), 1 => new Cell(along, H - 1 - inward), 2 => new Cell(inward, along), _ => new Cell(W - 1 - inward, along) };
                var start = Pick(30);
                if (!r.Passable(k, r.Index(start)) || r.Component(k, r.Index(start)) != r.Component(k, r.NearestPassable(k, new Cell(W / 2, H / 2), 64)))
                {
                    continue; // only from the map's main ground, not a pocket in a wood
                }
                var goal = Pick(0);
                var id = w.Spawn(def, start, 0);
                var u = w.Unit(id)!;
                uint? drv = null;
                if (def.Archetype.Crew is not null)
                {
                    drv = w.Spawn(rifleman, start, 0);
                    w.Issue(new Command { Tick = w.Tick + 1, Player = p1.Id, Kind = CommandKind.Enter, Units = [drv.Value], Target = id });
                    for (var i = 0; i < 200 && w.Unit(drv.Value) is { Inside: 0 }; i++)
                    {
                        w.Step();
                    }
                }
                w.Issue(new Command { Tick = w.Tick + 1, Player = p1.Id, Kind = CommandKind.Move, Units = [id], Cell = goal });
                for (var i = 0; i < 1500; i++)
                {
                    w.Step();
                    if (i > 10 && !u.Moving && !u.PathPending && u.Order.Kind == OrderKind.Idle)
                    {
                        break;
                    }
                }
                double px = u.Pos.X.Raw / (double)Fixed.OneRaw, py = u.Pos.Y.Raw / (double)Fixed.OneRaw;
                edge.Add(Math.Min(Math.Min(px, py), Math.Min(W - px, H - py)));
                off.Add(Math.Sqrt(Sq(px - goal.X - 0.5) + Sq(py - goal.Y - 0.5)));
                // Park it far from the test.
                u.Pos = new Cell(W / 2, H / 2).Center();
                u.ClearPath();
                u.Order = new Order(OrderKind.Idle, default, 0, 0);
            }
            Console.Error.WriteLine($"  ordered to the edge, {kind,-22}: {edge.Count} runs; ends from the edge mean {Mean(edge):0.0} p95 {P95(edge):0.0} max {(edge.Count == 0 ? 0 : edge.Max()):0.0}; from the order's cell mean {Mean(off):0.0} p95 {P95(off):0.0}");
        }
    }

    // Climbs: trips by a truck with its ends far apart in height (at least 30 m in at most 300 cells; both on a road, 12 m): whether they find a way, how
    // long it is, how much of it is on a road, and the steepest it climbs over any 12 cells of the way: a truck takes the road's
    // switchbacks up a mountain, not the hillside.
    private static void Climbs(World w, TerrainMap map, UnitDef def, int k, int x0, int y0, int x1, int y1, int trips, Random rng, bool roadEnds)
    {
        var r = w.Routing;
        var ratio = new List<double>();
        var worst = new List<double>();
        var onRoad = new List<double>();
        var failed = 0;
        var done = 0;
        for (var tries = 0; done < trips && tries < trips * 20000; tries++)
        {
            var s = new Cell(rng.Next(x0, x1 + 1), rng.Next(y0, y1 + 1));
            var d = new Cell(rng.Next(x0, x1 + 1), rng.Next(y0, y1 + 1));
            var dist = Math.Sqrt(Sq(s.X - d.X) + Sq(s.Y - d.Y));
            int si = r.Index(s), di = r.Index(d);
            if (dist < 40 || dist > 300 || !r.Passable(k, si) || !r.Passable(k, di) || r.Component(k, si) != r.Component(k, di) ||
                Math.Abs(map.Corner(s.X, s.Y).Raw - map.Corner(d.X, d.Y).Raw) < (roadEnds ? 12 : 30) * 512 ||
                (roadEnds && (map.RoadKindAt(s) == RoadKind.None || map.RoadKindAt(d) == RoadKind.None)))
            {
                continue;
            }
            done++;
            var cells = w.RouteCellsFor(def, s, d);
            if (cells.Count < 2 || Cheb(cells[^1], d) > 2)
            {
                failed++;
                continue;
            }
            double len = 0, steepest = 0, roadCells = 0;
            for (var i = 0; i < cells.Count; i++)
            {
                roadCells += map.RoadKindAt(cells[i]) != RoadKind.None ? 1 : 0;
                len += i == 0 ? 0 : Math.Sqrt(Sq(cells[i].X - cells[i - 1].X) + Sq(cells[i].Y - cells[i - 1].Y));
                if (i >= 12)
                {
                    double run = Math.Sqrt(Sq(cells[i].X - cells[i - 12].X) + Sq(cells[i].Y - cells[i - 12].Y));
                    var rise = Math.Abs(map.Corner(cells[i].X, cells[i].Y).Raw - map.Corner(cells[i - 12].X, cells[i - 12].Y).Raw) / 1024.0;
                    steepest = Math.Max(steepest, rise * 100 / Math.Max(run, 1));
                }
            }
            ratio.Add(len / dist);
            worst.Add(steepest);
            onRoad.Add(100 * roadCells / cells.Count);
        }
        Console.Error.WriteLine($"climbs: {done} trips, {failed} failed, ratio {Mean(ratio):0.000} p95 {P95(ratio):0.000}, on road {Mean(onRoad):0.0}%, steepest 12 cells of the way: mean {Mean(worst):0.0}% p95 {P95(worst):0.0}% max {(worst.Count == 0 ? 0 : worst.Max()):0.0}%");
    }

    // Blockers walks each trip's straight line and sorts the runs of cells the class can't use by what stops it and how long
    // the run is along the line; a run of slope also by the height it rises in all.
    private static void Blockers(TerrainMap map, SimCore.Sim.Router r, List<(Cell S, Cell D)> trips, string name, int k)
    {
        var tc = r.Nav.TerrainClass(k);
        var runs = new Dictionary<string, long[]>();
        var riseBins = new long[6];
        int clear = 0, tripsDone = 0;
        var lineCells = new List<int>();
        foreach (var (s, d) in trips)
        {
            tripsDone++;
            lineCells.Clear();
            r.Line(r.Index(s), r.Index(d), lineCells);
            var run = 0;
            string cause = "";
            long lo = long.MaxValue, hi = long.MinValue;
            var blockedAny = false;
            void Flush()
            {
                if (run > 0)
                {
                    if (!runs.TryGetValue(cause, out var b))
                    {
                        runs[cause] = b = new long[7];
                    }
                    b[run == 1 ? 0 : run == 2 ? 1 : run <= 4 ? 2 : run <= 8 ? 3 : run <= 16 ? 4 : run <= 32 ? 5 : 6]++;
                    if (cause is "steep" or "cliff" or "slopeClearance")
                    {
                        var m = (hi - lo) / 512.0;
                        riseBins[m <= 0.4 ? 0 : m <= 0.8 ? 1 : m <= 1.2 ? 2 : m <= 1.6 ? 3 : m <= 3 ? 4 : 5]++;
                    }
                }
                run = 0;
                lo = long.MaxValue;
                hi = long.MinValue;
            }
            foreach (var i in lineCells)
            {
                var c = r.CellAt(i);
                if (r.Nav.Cost(k, i) != 0)
                {
                    Flush();
                    continue;
                }
                blockedAny = true;
                var own = map.Cost(tc, c);
                var why = own == 0
                    ? map.OpenWater(c) ? "water"
                    : map.Forest(c) >= map.Rules.Classes[tc].ForestBlockedFrom ? "dense"
                    : map.Rules.Classes[tc].Cost[(int)map.Rules.Classes[tc].Classify(map.Grade(c))] == 0 ? (map.Grade(c) >= 45 ? "cliff" : "steep")
                    : "trunk/fire/alley"
                    : "slopeClearance";
                if (why != cause)
                {
                    Flush();
                    cause = why;
                }
                run++;
                for (var yy = c.Y; yy <= c.Y + 1; yy++)
                {
                    for (var xx = c.X; xx <= c.X + 1; xx++)
                    {
                        lo = Math.Min(lo, map.Corner(xx, yy).Raw);
                        hi = Math.Max(hi, map.Corner(xx, yy).Raw);
                    }
                }
            }
            Flush();
            clear += blockedAny ? 0 : 1;
        }
        Console.Error.WriteLine($"blockers {name} (width {r.Nav.Width(k)}): {clear} of {tripsDone} straight lines clear");
        foreach (var (cause, b) in runs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"  {cause,-16} runs by length 1:{b[0]} 2:{b[1]} 3-4:{b[2]} 5-8:{b[3]} 9-16:{b[4]} 17-32:{b[5]} 33+:{b[6]}");
        }
        Console.Error.WriteLine($"  slope runs by rise <=0.4 m:{riseBins[0]} <=0.8:{riseBins[1]} <=1.2:{riseBins[2]} <=1.6:{riseBins[3]} <=3:{riseBins[4]} more:{riseBins[5]}");
    }

    // LipStats: over a region, what the steep and gentle cells are made of: their own rise against the rise over windows of
    // 3, 5, 7 and 9 cells round them (docs/design.md, "Straight routes").
    internal static void LipStats(TerrainMap map, int x0, int y0, int x1, int y1)
    {
        var w = map.Width;
        var h = map.Height;
        long Rise(int cx, int cy, int r)
        {
            long lo = long.MaxValue, hi = long.MinValue;
            for (var yy = Math.Max(cy - r, 0); yy <= Math.Min(cy + r + 1, h); yy++)
            {
                for (var xx = Math.Max(cx - r, 0); xx <= Math.Min(cx + r + 1, w); xx++)
                {
                    var v = map.Corner(xx, yy).Raw;
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }
            }
            return hi - lo;
        }
        var radii = new[] { 1, 2, 3, 4 };
        var hist = new long[radii.Length, 6];   // of steep cells: windowed grade <10, 10-25, >=25; and rise <=0.8, <=1.6, more
        long gentle = 0, steep = 0, cliff = 0, all = 0, lipFlat = 0, lipGentle = 0, lipSteep = 0, ownOver = 0;
        const int Lip = 410; // 0.8 m in raw height
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                all++;
                var g = map.Grade(new Cell(x, y));
                if (g < 10)
                {
                    continue;
                }
                if (g >= 45)
                {
                    cliff++;
                    continue;
                }
                if (g < 25)
                {
                    gentle++;
                    continue;
                }
                steep++;
                for (var i = 0; i < radii.Length; i++)
                {
                    var r = radii[i];
                    var rise = Rise(x, y, r);
                    var wg = rise * 100 / (1024 * (2 * r + 1));
                    hist[i, wg < 10 ? 0 : wg < 25 ? 1 : 2]++;
                    hist[i, rise <= Lip ? 3 : rise <= 2 * Lip ? 4 : 5]++;
                }
                var w5 = Rise(x, y, 2) * 100 / (1024 * 5);
                if (g * 1024 / 100 > Lip)
                {
                    ownOver++;
                }
                else if (w5 < 10)
                {
                    lipFlat++;
                }
                else if (w5 < 25)
                {
                    lipGentle++;
                }
                else
                {
                    lipSteep++;
                }
            }
        }
        double Pc(long a, long b) => Math.Round(100.0 * a / Math.Max(b, 1), 1);
        long mFlat = 0, mGentle = 0, mSteep = 0, mCliff = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var g = map.MoveGrade(new Cell(x, y));
                if (g < 10) { mFlat++; } else if (g < 25) { mGentle++; } else if (g < 45) { mSteep++; } else { mCliff++; }
            }
        }
        Console.Error.WriteLine($"movement reads: flat {Pc(mFlat, all)}% gentle {Pc(mGentle, all)}% steep {Pc(mSteep, all)}% cliff {Pc(mCliff, all)}%");
        Console.Error.WriteLine($"lips over {x0},{y0}-{x1},{y1}: gentle {Pc(gentle, all)}% steep {Pc(steep, all)}% cliff {Pc(cliff, all)}%; of steep, window 5 flat {Pc(lipFlat, steep)}% gentle {Pc(lipGentle, steep)}% steep {Pc(lipSteep, steep)}%");
        for (var i = 0; i < radii.Length; i++)
        {
            Console.Error.WriteLine($"  window {2 * radii[i] + 1}: steep cells whose windowed grade is <10 {Pc(hist[i, 0], steep)}%, 10-25 {Pc(hist[i, 1], steep)}%, 25+ {Pc(hist[i, 2], steep)}%; window rise <=0.8 m {Pc(hist[i, 3], steep)}%, <=1.6 m {Pc(hist[i, 4], steep)}%, more {Pc(hist[i, 5], steep)}%");
        }
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
        var stats = new Dictionary<string, (List<double> Dist, List<double> Time, List<double> Rev, List<double> Turn, List<double> Flips, List<double> Quick, int Late)>();
        var parked = 0;
        var sw = Stopwatch.StartNew();
        long ticks = 0, lipTicks = 0, lipStalls = 0;
        void Add(string kind, Track t, double straight, double speed)
        {
            if (!stats.TryGetValue(kind, out var s))
            {
                s = ([], [], [], [], [], [], 0);
            }
            var (rev, turn) = Reversals(t.Heads, 3);
            var metres = Math.Max(t.Travelled * CellMetres, 1);
            s.Dist.Add(t.Travelled / straight);
            s.Time.Add(t.Ticks / (straight / Math.Max(speed, 1e-6)));
            s.Rev.Add(rev * 100 / metres);
            s.Turn.Add(turn * 100 / metres);
            s.Flips.Add(t.FacingFlips * 100 / metres);
            s.Quick.Add(t.QuickFlips * 100 / metres);
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
                            if (w.Ground is { } ground && ground.Grade(Cell.Of(u.Pos)) >= 25 && ground.MoveGrade(Cell.Of(u.Pos)) < 25)
                            {
                                lipTicks++;
                                lipStalls += u.StallTicks >= 10 ? 1 : 0;
                            }
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
                                t.QuickFlips += t.LastTurn == -turnDir && w.Tick - t.LastTurnTick < QuickTicks ? 1 : 0;
                                t.LastTurn = turnDir;
                                t.LastTurnTick = w.Tick;
                            }
                            t.LastFacing = u.Facing;
                            trace?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"{units[i].Kind},{tripNo},{w.Tick - order},{u.Pos.X.Raw / 1024.0:0.000},{u.Pos.Y.Raw / 1024.0:0.000},{(t.Heads.Count > 0 ? t.Heads[^1] : 0):0.0},{u.PathIdx},{u.Path.Count},{u.Intent.Goal.X.Raw / 1024.0:0.00},{u.Intent.Goal.Y.Raw / 1024.0:0.00},{u.DriveSpeed.Raw},{(u.Detouring ? 1 : 0)},{u.StallTicks},{(u.Intent.Stuck ? 1 : 0)},{u.Facing},{u.Mode},{TraceTight(w, u)}"));
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
                ["quickFlipsPer100m"] = Stat(s.Quick, 3),
            };
            Console.Error.WriteLine($"  {(group ? "group " : "alone ")}{kind,-22} dist {Mean(s.Dist):0.0000} p95 {P95(s.Dist):0.0000}  time {Mean(s.Time):0.000}" +
                $"  rev/100m {Mean(s.Rev):0.000} p95 {P95(s.Rev):0.000}  turn/100m {Mean(s.Turn):0.0} p95 {P95(s.Turn):0.0}  flips/100m {Mean(s.Flips):0.00} p95 {P95(s.Flips):0.00}  quick {Mean(s.Quick):0.00}  ({s.Late} not arrived)");
        }
        res["msPerTick"] = Math.Round(sw.Elapsed.TotalMilliseconds / Math.Max(1, ticks), 3);
        res["ticksOnLips"] = lipTicks;
        res["ticksStalledOnLips"] = lipStalls;
        Console.Error.WriteLine($"  on lips (a steep cell by itself that movement reads gentler) {lipTicks} ticks, stalled {lipStalls}");
        return res;
    }

    // TraceTight: for the trace, the forest density at a unit and how many of the 5 x 5 cells round it its class can't use.
    private static string TraceTight(World w, Unit u)
    {
        var c = Cell.Of(u.Pos);
        var k = w.Routing.ClassOf(u.Def);
        var walls = 0;
        for (var y = c.Y - 2; y <= c.Y + 2; y++)
        {
            for (var x = c.X - 2; x <= c.X + 2; x++)
            {
                walls += x >= 0 && y >= 0 && x < w.Grid.Width && y < w.Grid.Height && w.Routing.Nav.Cost(k, x, y) == 0 ? 1 : 0;
            }
        }
        return $"{w.Ground?.Forest(c) ?? 0},{walls},{(w.Ground?.RoadKindAt(c) ?? RoadKind.None)},{w.Ground?.MoveGrade(c) ?? 0}";
    }

    private sealed class Track
    {
        public double Travelled;
        public long Ticks;
        public bool Arrived;
        public readonly List<double> Heads = [];
        public int LastFacing = -1, LastTurn, FacingTurns, FacingFlips, QuickFlips;
        public uint LastTurnTick;
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
