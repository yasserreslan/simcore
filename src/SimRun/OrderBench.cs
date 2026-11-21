// OrderBench measures the tick a squad order lands on (INF-PERF): the game's hitch log (INF-SMOOTH, 2026-11-21) found sim steps
// of 18 to 30 ms on the tick a squad got an order (ambush-road, squads-hold), and the client steps the sim on the frame. A squad of
// 10 (or three, one command for all: a box-select) formed and settled on a 96 x 96 map like the demos' (rolling ground, woods,
// the client's armies: structures and a soldier each, every routing sector prepared), then given one order; each of the next 80
// ticks timed with a breakdown by system. Each workload runs Runs times on a fresh world (the first is a JIT warm-up, not
// counted) and prints the median worst tick, its tick after the order, its slowest systems, and the world's hash at the end (the
// same before and after an optimisation: nothing changed in play).
//
//   dotnet run -c Release --project src/SimRun -- -order-bench all                 # march,assault,hold,ambush x open,town x 1,3
//   dotnet run -c Release --project src/SimRun -- -order-bench hold,ambush -order-layout town -order-squads 3
//   dotnet run -c Release --project src/SimRun -- -order-bench ambush -map ../game/client/data/maps/squads_hold_01/map.json
using System.Diagnostics;
using System.Globalization;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimRun;

internal static class OrderBench
{
    private static readonly int Runs = int.TryParse(Environment.GetEnvironmentVariable("ORDER_BENCH_RUNS"), out var r) ? r : 6;
    private const int Size = 96;
    private const int After = 80;

    // The ambush-road demo's squad (Demo.AmbushRoad.cs KzKinds) and a tenth rifleman.
    private static readonly string[] Kinds = ["Rifleman", "Rifleman", "Rifleman", "Gunner", "Tank hunter", "Tank hunter", "Marksman", "kornet", "engineer_sapper", "Rifleman"];

    // The town: archetype and top-left cell, round the squads' ways east (front to the south).
    private static readonly (string Kind, int X, int Y)[] Town =
    [
        ("house_small", 26, 20), ("shop_row", 34, 20), ("apartment_3f", 46, 20), ("corner_building", 58, 20),
        ("stone_rowhouse", 26, 38), ("villa_tiled", 34, 40), ("kiosk", 44, 40), ("apartment_l", 52, 38),
        ("block_brick_4", 26, 56), ("clinic", 40, 56), ("market_stalls", 54, 56),
        ("warehouse", 26, 74), ("school", 40, 74), ("stone_tower_house", 56, 74),
    ];

    private sealed record Result(string Name, double WorstMs, int WorstAt, string Top, double SumMs, int Paths, ulong Hash, double JitMs, long Jitted);

    public static int Run(string workloads, string layouts, string squadCounts, string map, string contentDir)
    {
        var content = ContentLoader.LoadDirectory(contentDir);
        var root = Path.GetDirectoryName(Path.GetFullPath(contentDir))!;
        var rules = TerrainLoader.LoadRules(Path.Combine(root, "terrain", "rules.json"));
        var orders = workloads == "all" ? ["march", "assault", "hold", "ambush"] : workloads.Split(',');
        var lays = map != "" ? ["map"] : layouts.Split(',');
        var counts = squadCounts.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"order bench: {Size} x {Size}{(map != "" ? $" ({map})" : "")}, squads of {Kinds.Length}, the worst of {After} ticks after the order, median of {Runs - 1} runs"));
        foreach (var lay in lays)
        {
            foreach (var n in counts)
            {
                foreach (var order in orders)
                {
                    var runs = new List<Result>();
                    var warm = "";
                    for (var r = 0; r < Runs; r++)
                    {
                        if (r == 0 && Environment.GetEnvironmentVariable("ORDER_BENCH_WARM") == "1")
                        {
                            warm = WarmUp(content, rules, root, map);
                        }
                        runs.Add(One(content, rules, root, map, lay, n, order));
                    }
                    var counted = runs.Skip(1).OrderBy(x => x.WorstMs).ToList();
                    var m = counted[counted.Count / 2];
                    var hashes = runs.Select(x => x.Hash).Distinct().Count();
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  {lay,-5} {n} x {order,-7} worst {m.WorstMs,6:0.00} ms (min {counted[0].WorstMs:0.00}, max {counted[^1].WorstMs:0.00}) at +{m.WorstAt}: {m.Top}; {warm}cold {runs[0].WorstMs:0.00} ms at +{runs[0].WorstAt} ({runs[0].Top}; JIT {runs[0].JitMs:0.00} ms, {runs[0].Jitted} methods); second {runs[1].WorstMs:0.00} ms (JIT {runs[1].JitMs:0.00} ms); ticks over 1 ms {m.SumMs:0.0} ms; paths {m.Paths}; hash {m.Hash:x16}{(hashes == 1 ? "" : " (RUNS DIFFER)")}"));
                }
            }
        }
        return 0;
    }

    // WarmUp times World.WarmUp on a world made as One makes it (the client calls it at load).
    private static string WarmUp(ContentSet content, TerrainRules rules, string root, string map)
    {
        var setup = Replay.LoadSetup(Path.Combine(root, "setups", "battle.json")).For(content, 42) with { Scenario = Scenario.SandboxName, Terrain = "bench/map.json" };
        var w = Replay.NewWorld(content, setup, map != "" ? TerrainLoader.LoadMap(Path.GetFullPath(map), rules, content) : Ground(rules));
        var h = w.Hash();
        var j0 = System.Runtime.JitInfo.GetCompilationTime(currentThread: true);
        var t0 = Stopwatch.GetTimestamp();
        w.WarmUp();
        var ms = Ms(Stopwatch.GetTimestamp() - t0);
        var jit = (System.Runtime.JitInfo.GetCompilationTime(currentThread: true) - j0).TotalMilliseconds;
        return string.Create(CultureInfo.InvariantCulture, $"warm-up {ms:0} ms (JIT {jit:0} ms{(w.Hash() == h ? "" : ", THE WORLD CHANGED")}); ");
    }

    private static Result One(ContentSet content, TerrainRules rules, string root, string map, string lay, int n, string order)
    {
        var setup = Replay.LoadSetup(Path.Combine(root, "setups", "battle.json")).For(content, 42) with { Scenario = Scenario.SandboxName };
        // The client's demos on a terrain map: only the structures and one soldier each (SimSession.Create).
        setup = setup with
        {
            Players = [.. setup.Players.Select(p => p with
            {
                Army = [.. p.Army.Where(a => content.Archetypes.Any(t => t.Id == a.Unit && t.IsStructure)),
                    .. p.Army.Where(a => a.Driver is null && !content.Archetypes.Any(t => t.Id == a.Unit && t.IsStructure)).Take(1).Select(a => a with { Count = 1 })],
            })],
        };
        var terrain = map != "" ? TerrainLoader.LoadMap(Path.GetFullPath(map), rules, content) : Ground(rules);
        setup = setup with { Terrain = "bench/map.json" };
        var w = Replay.NewWorld(content, setup, terrain);
        w.PrepareRouting(everySector: true);
        if (lay == "town")
        {
            foreach (var (kind, x, y) in Town)
            {
                w.Issue(new Command { Tick = 0, Player = 2, Kind = CommandKind.SandboxSpawn, Unit = kind, Cell = new Cell(x, y) });
            }
        }
        w.Step();
        var p1 = w.Player(1)!.Setup;
        var squads = new List<List<uint>>();
        for (var g = 0; g < n; g++)
        {
            var (gx, gy) = (10, 30 + g * 18);
            var ids = new List<uint>();
            for (var i = 0; i < Kinds.Length; i++)
            {
                var def = p1.Find(content, Kinds[i])!;
                var k = w.Routing.ClassOf(def);
                var c = w.Routing.NearestPassable(k, new Cell(gx + i % 5, gy + i / 5), 16);
                ids.Add(w.Spawn(def, c < 0 ? new Cell(gx + i % 5, gy + i / 5) : w.Routing.CellAt(c), Facing.East));
            }
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.FormSquad, Units = [.. ids] });
            squads.Add(ids);
        }
        w.Step();
        if (order == "assault")
        {
            foreach (var ids in squads)
            {
                w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetSquadMode, Units = [ids[0]], Index = (int)SquadMode.Assault });
            }
        }
        for (var t = 0; t < 60; t++)
        {
            w.Step(); // settled in its shape
        }
        var all = squads.SelectMany(s => s).ToList();
        var y0 = 30 + (n - 1) * 9; // the middle squad's row
        var cmd = order switch
        {
            "hold" => new Command { Kind = CommandKind.SetSquadMode, Index = (int)SquadMode.Hold, Cell = new Cell(30, y0), Cell2 = new Cell(42, y0) },
            "ambush" => new Command { Kind = CommandKind.SetSquadMode, Index = (int)SquadMode.Ambush, Cell = new Cell(36, y0 + 12), Cell2 = new Cell(36, y0 - 12) },
            "march" or "assault" => new Command { Kind = CommandKind.Move, Cell = new Cell(60, y0) },
            _ => throw new ArgumentException($"order bench: unknown order {order} (march, assault, hold, ambush)"),
        };
        w.Issue(cmd with { Tick = w.Tick, Player = 1, Units = [.. all] });
        var probe = new Probe();
        w.Probe = probe;
        double worst = 0, sum = 0;
        var worstAt = 0;
        var top = "";
        var paths = 0;
        double worstJit = 0;
        long worstJitted = 0;
        for (var t = 0; t < After; t++)
        {
            var j0 = System.Runtime.JitInfo.GetCompilationTime(currentThread: true);
            var m0 = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
            var t0 = Stopwatch.GetTimestamp();
            w.Step();
            var ms = Ms(Stopwatch.GetTimestamp() - t0);
            var jit = (System.Runtime.JitInfo.GetCompilationTime(currentThread: true) - j0).TotalMilliseconds;
            var jitted = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - m0;
            paths += w.PathRequests;
            if (ms > 1)
            {
                sum += ms;
            }
            if (ms > worst)
            {
                (worst, worstAt, top, worstJit, worstJitted) = (ms, t, probe.Top(), jit, jitted);
            }
        }
        w.Probe = null;
        return new Result($"{lay} {n} {order}", worst, worstAt, top, sum, paths, w.Hash(), worstJit, worstJitted);
    }

    // The demos' kind of ground (squads_hold_01: hills): rolling ground under 2 m, a low hill, two woods.
    private static TerrainMap Ground(TerrainRules rules)
    {
        const int m = 512;
        var corners = new int[(Size + 1) * (Size + 1)];
        var flags = new byte[Size * Size];
        for (var y = 0; y <= Size; y++)
        {
            for (var x = 0; x <= Size; x++)
            {
                var h = 2 * m + Math.Abs(x % 32 - 16) * 32 + Math.Abs(y % 24 - 12) * 24;
                var d2 = (x - 70) * (x - 70) + (y - 30) * (y - 30);
                h += Math.Max(0, 5 * m - d2 * 5 * m / 196); // a hill 5 m high, 14 cells round
                corners[y * (Size + 1) + x] = h;
                if (x < Size && y < Size && ((x is >= 64 and < 84 && y is >= 60 and < 80) || (x is >= 18 and < 30 && y is >= 4 and < 14)))
                {
                    flags[y * Size + x] = (byte)(1 + (x + y) % 3); // forest density 1 to 3
                }
            }
        }
        return new TerrainMap(Size, Size, corners, flags, rules);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private sealed class Probe : IStepProbe
    {
        private readonly long[] _ticks = new long[Enum.GetValues<StepPhase>().Length];
        private long _last;

        public void Begin()
        {
            Array.Clear(_ticks);
            _last = Stopwatch.GetTimestamp();
        }

        public void After(StepPhase phase)
        {
            var now = Stopwatch.GetTimestamp();
            _ticks[(int)phase] += now - _last;
            _last = now;
        }

        public string Top() => string.Join(", ", Enum.GetValues<StepPhase>()
            .Select(p => (p, Ms(_ticks[(int)p]))).OrderByDescending(x => x.Item2).Take(3)
            .Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.p} {x.Item2:0.00}")));
    }
}
