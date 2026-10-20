// RoutingBench measures routing headless on a big synthetic map (hills, a road, forest and rock
// blobs): the time to build routing's caches for the map, and per tick the whole step and the
// routing share of it (the command system, which plans formations, and the path system).
//
//   group: 2,000 units (1,800 riflemen, 200 crewed technicals) in one group order across the map.
//   many:  10,000 moving units in 40 groups of 250, each ordered to a far point.
//
//   dotnet run -c Release --project src/SimRun -- -routing-bench group -maxticks 1200
//   dotnet run -c Release --project src/SimRun -- -routing-bench group,many -size 1500 -out bench/routing.json
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimCore.Content;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static class RoutingBench
{
    private const ulong Seed = 42;

    public static int Run(string workloads, int size, uint maxTicks, double budget, string outPath, string contentDir, string setupPath)
    {
        var content = ContentLoader.LoadDirectory(contentDir);
        var setup = Replay.LoadSetup(setupPath).For(content, Seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var match = MatchCompiler.Compile(content, setup);
        var rules = TerrainLoader.LoadRules(Path.Combine("content", "terrain", "rules.json"));

        // JIT warm-up, not recorded: long enough, and a pause, for the hot methods to reach the
        // optimizing tier (or run with DOTNET_TieredCompilation=0).
        RunOne(match, rules, "group", 256, 120, 10, quiet: true);
        Thread.Sleep(1000);
        var results = new JsonArray();
        foreach (var wl in workloads.Split(','))
        {
            results.Add(RunOne(match, rules, wl, size, maxTicks, budget, quiet: false));
        }
        var json = new JsonObject
        {
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["machine"] = $"{Environment.ProcessorCount} cpus, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}",
            ["results"] = results,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (outPath != "")
        {
            File.WriteAllText(outPath, json + "\n");
        }
        return 0;
    }

    private sealed class Probe : IStepProbe
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
            _last = now;
        }
    }

    private static JsonObject RunOne(CompiledMatch match, TerrainRules rules, string workload, int size, uint maxTicks, double budget, bool quiet)
    {
        var sw = Stopwatch.StartNew();
        var w = new World(Rocks(size), match);
        w.AttachTerrain(Hills(size, rules));
        var mapMs = sw.Elapsed.TotalMilliseconds;
        var rng = new Random(7);
        var orders = new List<(List<Unit> Units, Cell To)>();
        var vehicles = new List<(Unit Vehicle, Unit Driver)>();
        int groups = workload == "many" ? 40 : 1, infantry = workload == "many" ? 225 : 1800, cars = workload == "many" ? 25 : 200;
        for (var gi = 0; gi < groups; gi++)
        {
            int cx, cy, tx, ty;
            if (groups == 1)
            {
                (cx, cy, tx, ty) = (size / 12, size / 2, size - size / 12, size / 2);
            }
            else
            {
                do
                {
                    (cx, cy, tx, ty) = (rng.Next(80, size - 80), rng.Next(80, size - 80), rng.Next(80, size - 80), rng.Next(80, size - 80));
                }
                while (Math.Abs(cx - tx) + Math.Abs(cy - ty) < size / 2);
            }
            var units = new List<Unit>();
            Place(w, "Rifleman", infantry, cx, cy, 1, units, null);
            Place(w, "technical", cars, cx - 4 - (int)Math.Sqrt(infantry) / 2, cy, 3, units, vehicles);
            orders.Add((units, new Cell(tx, ty)));
        }
        foreach (var (v, d) in vehicles)
        {
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Enter, Units = [d.Id], Target = v.Id });
        }
        sw.Restart();
        w.PrepareRouting();
        var prepareMs = sw.Elapsed.TotalMilliseconds;
        for (var i = 0; i < 200 && vehicles.Any(x => x.Driver.Inside == 0); i++)
        {
            w.Step(); // drivers climb in
        }
        foreach (var (units, to) in orders)
        {
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [.. units.Select(u => u.Id)], Cell = to });
        }

        var probe = new Probe();
        w.Probe = probe;
        var tickMs = new List<double>();
        var routeMs = new List<double>();
        var paths = new List<int>();
        var worst = (Ms: 0.0, Tick: 0u, Phases: "");
        var worstRoute = (Ms: 0.0, Tick: 0u, Phases: "");
        var gcs = GC.CollectionCount(0);
        var wall = Stopwatch.StartNew();
        var start = w.Tick;
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        while (w.Tick - start < maxTicks && wall.Elapsed.TotalSeconds < budget)
        {
            var t0 = Stopwatch.GetTimestamp();
            w.Step();
            tickMs.Add(Ms(Stopwatch.GetTimestamp() - t0));
            routeMs.Add(Ms(probe.Ticks[(int)StepPhase.Commands] + probe.Ticks[(int)StepPhase.Paths]));
            paths.Add(w.PathRequests);
            if (tickMs[^1] > worst.Ms)
            {
                worst = (tickMs[^1], w.Tick - start, Top(probe));
            }
            if (routeMs[^1] > worstRoute.Ms)
            {
                worstRoute = (routeMs[^1], w.Tick - start, Top(probe));
            }
        }
        var moving = w.Units.Count(u => u.Moving);
        var res = new JsonObject
        {
            ["workload"] = workload,
            ["size"] = size,
            ["units"] = w.Units.Count,
            ["ticks"] = tickMs.Count,
            ["mapMs"] = R(mapMs),
            ["prepareRoutingMs"] = R(prepareMs),
            ["tickMs"] = Stats(tickMs),
            ["routingMs"] = Stats(routeMs),
            ["pathRequests"] = new JsonObject { ["max"] = paths.Max(), ["total"] = paths.Sum() },
            ["movingAtEnd"] = moving,
            ["worstTick"] = new JsonObject { ["tick"] = worst.Tick, ["ms"] = R(worst.Ms), ["top"] = worst.Phases },
            ["worstRoutingTick"] = new JsonObject { ["tick"] = worstRoute.Tick, ["ms"] = R(worstRoute.Ms), ["top"] = worstRoute.Phases },
            ["gen0Collections"] = GC.CollectionCount(0) - gcs,
            ["finalHash"] = w.Hash().ToString("x16", CultureInfo.InvariantCulture),
        };
        if (!quiet)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{workload,-6} {size}x{size} units={w.Units.Count} ticks={tickMs.Count} prepare={prepareMs:0} ms  tick mean={tickMs.Average():0.000} p99={Pct(tickMs, 99):0.000} max={tickMs.Max():0.000}  routing mean={routeMs.Average():0.000} p99={Pct(routeMs, 99):0.000} max={routeMs.Max():0.000} ms  paths={paths.Sum()} moving={moving}  gc0={GC.CollectionCount(0) - gcs} alloc/tick {(GC.GetAllocatedBytesForCurrentThread() - alloc0) / tickMs.Count / 1024} KB\n  worst tick {worst.Tick}: {worst.Phases}\n  worst routing tick {worstRoute.Tick}: {worstRoute.Phases}"));
        }
        return res;
    }

    // Place spawns n units of a preset in a block around (cx, cy), on cells their class can use.
    private static void Place(World w, string name, int n, int cx, int cy, int spacing, List<Unit> units, List<(Unit, Unit)>? crew)
    {
        var def = w.Player(1)!.Setup.Find(w.Content, name)!;
        var driverDef = w.Player(1)!.Setup.Find(w.Content, "Rifleman")!;
        var k = w.Ground!.Rules.ClassOf(def);
        var cols = Math.Max(1, (int)Math.Sqrt(n) / (spacing == 1 ? 1 : 2));
        int placed = 0, i = 0;
        while (placed < n)
        {
            var c = new Cell(cx - cols * spacing / 2 + i % cols * spacing, cy - (n / cols) * spacing / 2 + i / cols * spacing);
            i++;
            if (!w.Grid.Passable(c) || !w.Ground.Passable(k, c) || (spacing > 1 && !w.Grid.Passable(new Cell(c.X + 1, c.Y))))
            {
                continue;
            }
            var u = w.Unit(w.Spawn(def, c, 8))!;
            units.Add(u);
            if (crew is not null)
            {
                var d = w.Unit(w.Spawn(driverDef, new Cell(c.X + 1, c.Y), 8))!;
                crew.Add((u, d));
            }
            placed++;
        }
    }

    // Rocks scatters rock blobs over about 5% of the map.
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

    internal static TerrainMap HillsMap(int size, TerrainRules rules) => Hills(size, rules);

    // Hills is TerrainBench's synthetic map, in memory: hills and ridges, a road across, forest.
    private static TerrainMap Hills(int size, TerrainRules rules)
    {
        var corners = new short[(size + 1) * (size + 1)];
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                var v = 0.4 + 0.3 * Math.Sin(x * 0.006) * Math.Cos(y * 0.007) + 0.1 * Math.Sin((x + 2 * y) * 0.019) + 0.02 * Math.Sin(x * 0.11 + y * 0.05);
                corners[y * (size + 1) + x] = (short)((-16 + 64 * Math.Clamp(v, 0, 1)) * 512);
            }
        }
        var flags = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var f = Math.Sin(x * 0.02 + 1) * Math.Sin(y * 0.025);
                flags[y * size + x] = (byte)(f > 0.6 ? 3 : f > 0.4 ? 2 : f > 0.25 ? 1 : 0);
            }
        }
        for (var x = 0; x < size; x++)
        {
            var y = x < size / 3 ? size / 2 - x / 2 : size / 3 + (x - size / 3) * (size / 2 - size / 3) / (size - size / 3);
            for (var d = -1; d <= 1; d++)
            {
                if (y + d >= 0 && y + d < size)
                {
                    flags[(y + d) * size + x] |= 1 << 2;
                }
            }
        }
        return new TerrainMap(size, size, corners, flags, rules);
    }

    private static string Top(Probe probe) => string.Join(", ", Enum.GetValues<StepPhase>()
        .Select(p => (p, Ms(probe.Ticks[(int)p]))).OrderByDescending(x => x.Item2).Take(3)
        .Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.p} {x.Item2:0.00}")));

    private static JsonObject Stats(List<double> xs) => new()
    {
        ["mean"] = R(xs.Average()),
        ["p50"] = R(Pct(xs, 50)),
        ["p99"] = R(Pct(xs, 99)),
        ["max"] = R(xs.Max()),
    };

    private static double Pct(List<double> xs, int p)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, s.Count * p / 100)];
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double R(double v) => Math.Round(v, 3);
}
