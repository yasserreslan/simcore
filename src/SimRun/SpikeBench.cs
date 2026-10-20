// SpikeBench measures single-tick spikes headless: the tick a group order is given, and the tick an
// ejected pilot lands and becomes a unit. Each workload runs several times in one process (the first
// run is cold: the JIT compiles what it meets), and prints the worst ticks with their slowest systems.
//
//   move: 38 crewed technicals in two rows, after 20 s, get one Move order to a cell about 40 cells off.
//   land: a multirole is shot down by a gun at its patrol height; its pilot comes down and lands
//         among 38 enemy technicals.
//   field: an airfield laid finished (World.SpawnAirfield, as the client's jets demo does at its
//          set-up) after 20 s; the tick after pays for routing's refresh of the levelled ground.
//   sectors: what building each routing class's HPA* sectors costs.
//   regions: what the next route search pays per class for routing's connected regions after a map
//            change (a 4 x 4 building's cells blocked, then cleared again), on synthetic 256 x 256 and
//            1,500 x 1,500 hilly maps (the -map option is ignored).
//
//   dotnet run -c Release --project src/SimRun -- -spike-bench move,land                 # a synthetic 256 x 256 hilly map
//   dotnet run -c Release --project src/SimRun -- -spike-bench move,land -map maps/x.json # a terrain map file
using System.Diagnostics;
using System.Globalization;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimRun;

internal static class SpikeBench
{
    private const int Runs = 4;

    public static int Run(string workloads, string map, bool prepare, bool fog, string contentDir)
    {
        var content = ContentLoader.LoadDirectory(contentDir);
        var setup = Replay.LoadSetup(Path.Combine("content", "setups", "sentinel.json")).For(content, 42) with { Fog = fog };
        setup = setup with { Scenario = Scenario.SandboxName, Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var rules = TerrainLoader.LoadRules(Path.Combine(contentDir, "..", "terrain", "rules.json"));
        foreach (var wl in workloads.Split(','))
        {
            for (var run = 0; run < Runs; run++)
            {
                if (wl == "regions")
                {
                    foreach (var size in new[] { 256, 1500 })
                    {
                        var rw = Replay.NewWorld(content, setup, RoutingBench.HillsMap(size, rules));
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"regions run {run}{(run == 0 ? " (cold)" : "")} {size}x{size}:{Regions(rw)}"));
                    }
                    continue;
                }
                var terrain = map == "flat" ? RoutingBench.HillsMap(256, rules) : TerrainLoader.LoadMap(Path.GetFullPath(map), rules);
                var w = Replay.NewWorld(content, setup, terrain);
                if (prepare)
                {
                    w.PrepareRouting();
                }
                var probe = new Probe();
                w.Probe = probe;
                var (label, ticks) = wl switch
                {
                    "move" => Move(w, probe),
                    "land" => Land(w, probe),
                    "field" => Field(w, probe),
                    "sectors" => Sectors(w),
                    _ => throw new FormatException($"spike-bench: unknown workload \"{wl}\""),
                };
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{wl} run {run}{(run == 0 ? " (cold)" : "")}{(prepare ? " prepared" : "")}{(fog ? " fog" : "")}: {label}"));
                foreach (var t in ticks.OrderByDescending(t => t.Ms).Take(3))
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   tick {t.Tick}: {t.Ms:0.000} ms  {t.Top}  paths {t.Paths}"));
                }
            }
        }
        return 0;
    }

    private sealed record TickTime(uint Tick, double Ms, string Top, int Paths);

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

        public string Top() => string.Join(", ", Enum.GetValues<StepPhase>()
            .Select(p => (p, Ms(Ticks[(int)p]))).OrderByDescending(x => x.Item2).Take(4)
            .Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.p} {x.Item2:0.00}")));
    }

    private static TickTime Timed(World w, Probe probe)
    {
        var t0 = Stopwatch.GetTimestamp();
        w.Step();
        return new TickTime(w.Tick, Ms(Stopwatch.GetTimestamp() - t0), probe.Top(), w.PathRequests);
    }

    // Open finds the passable cell for def nearest (x, y).
    private static Cell Open(World w, UnitDef def, int x, int y)
    {
        var r = w.Routing;
        var i = r.NearestPassable(r.ClassOf(def), new Cell(x, y), 64);
        return i < 0 ? new Cell(x, y) : r.CellAt(i);
    }

    private static (string, List<TickTime>) Move(World w, Probe probe)
    {
        var tech = w.Player(2)!.Setup.Find(w.Content, "technical")!;
        var size = w.Grid.Width;
        var (cx, cy) = (size / 2, size / 2);
        var units = Technicals(w, 38, cx, cy);
        var idle = new List<TickTime>();
        for (var i = 0; i < 400; i++)
        {
            idle.Add(Timed(w, probe));
        }
        var worstIdle = idle.Skip(1).MaxBy(t => t.Ms)!;
        var to = Open(w, tech, cx + 25, cy + 30);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.Move, Units = units, Cell = to });
        var ticks = new List<TickTime>();
        for (var i = 0; i < 60; i++)
        {
            ticks.Add(Timed(w, probe));
        }
        return (string.Create(CultureInfo.InvariantCulture, $"{units.Count} technicals to {to.X},{to.Y}, moving after 60 ticks: {w.Units.Count(u => u.Moving)}; before the order mean {idle.Average(t => t.Ms):0.000} ms, worst tick {worstIdle.Tick} {worstIdle.Ms:0.000} ms ({worstIdle.Top})"), ticks);
    }

    // Technicals spawns up to n crewed technicals of player 2 in rows around (cx, cy); returns their IDs.
    private static List<uint> Technicals(World w, int n, int cx, int cy)
    {
        var tech = w.Player(2)!.Setup.Find(w.Content, "technical")!;
        var rifle = w.Player(2)!.Setup.Find(w.Content, "Rifleman")!;
        var units = new List<uint>();
        for (var i = 0; units.Count < n && i < 400; i++)
        {
            var c = new Cell(cx - 20 + (i % 19) * 2, cy - 6 + (i / 19) * 3);
            if (!w.Grid.Passable(c) || !w.Routing.Passable(w.Routing.ClassOf(tech), w.Routing.Index(c)))
            {
                continue;
            }
            var v = w.Unit(w.Spawn(tech, c, Facing.North))!;
            var d = w.Unit(w.Spawn(rifle, c, Facing.North))!;
            World.Board(d, v);
            units.Add(v.Id);
        }
        return units;
    }

    private static (string, List<TickTime>) Land(World w, Probe probe)
    {
        var jet = w.Player(1)!.Setup.Find(w.Content, "Multirole 500 lb")!;
        var size = w.Grid.Width;
        var j = w.Unit(w.Spawn(jet, new Cell(size / 4, size / 2), Facing.East))!;
        for (var i = 0; i < 40; i++)
        {
            w.Step();
        }
        j.HP = 0;
        j.KilledBy = KillCause.Gun;
        var ticks = new List<TickTime>();
        for (var i = 0; i < 70; i++)
        {
            ticks.Add(Timed(w, probe));
        }
        // Red's technicals (holding their ground) round where he comes down, as in the jets demo.
        var at = Cell.Of(w.Chutes[0].Pos);
        var red = Technicals(w, 38, at.X + 6, at.Y + 4);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = red, Stance = Stance.HoldGround });
        uint landedAt = 0;
        for (var i = 0; i < 1000 && landedAt == 0; i++)
        {
            ticks.Add(Timed(w, probe));
            if (w.EjectionEvents.Any(e => e.Kind == EjectionEventKind.Landed))
            {
                landedAt = w.Tick;
            }
        }
        for (var i = 0; i < 40; i++)
        {
            ticks.Add(Timed(w, probe));
        }
        var landing = ticks.Find(t => t.Tick == landedAt);
        return ($"landed at tick {landedAt} by {at.X},{at.Y}: {landing?.Ms.ToString("0.000", CultureInfo.InvariantCulture)} ms ({landing?.Top})", ticks);
    }

    private static (string, List<TickTime>) Field(World w, Probe probe)
    {
        var fac = w.Player(1)!.Setup.Find(w.Content, "airfield_facility")!;
        for (var i = 0; i < 400; i++)
        {
            w.Step();
        }
        var size = w.Grid.Width;
        Airfield? af = null;
        var at = new Cell(0, 0);
        for (var y = size / 4; af is null && y < size * 3 / 4; y += 8)
        {
            for (var x = size / 4; af is null && x < size * 3 / 4; x += 8)
            {
                at = new Cell(x, y);
                try
                {
                    af = w.SpawnAirfield(fac, at, 0, [(0, 0)], 0, 1);
                }
                catch (ArgumentException)
                {
                    af = w.Airfields.LastOrDefault(); // the facility and the first row stand
                }
            }
        }
        var ticks = new List<TickTime>();
        for (var i = 0; i < 20; i++)
        {
            ticks.Add(Timed(w, probe));
        }
        return (string.Create(CultureInfo.InvariantCulture, $"airfield {(af is null ? "none" : $"#{af.Id}")} at {at.X},{at.Y}: next tick {ticks[0].Ms:0.000} ms ({ticks[0].Top})"), ticks);
    }

    // Sectors builds every class's HPA* sectors one by one and reports what they cost.
    private static (string, List<TickTime>) Sectors(World w)
    {
        var r = w.Routing;
        var lines = new List<string>();
        for (var k = 0; k < r.Nav.Classes; k++)
        {
            var h = r.Graph(k);
            var per = new List<(int Sec, double Ms, int Nodes, long Work)>();
            for (var sec = 0; sec < h.SX * h.SY; sec++)
            {
                var b0 = h.BuildWork;
                var s0 = Stopwatch.GetTimestamp();
                var n = h.Get(sec).Nodes.Length;
                per.Add((sec, Ms(Stopwatch.GetTimestamp() - s0), n, h.BuildWork - b0));
            }
            var t0 = Stopwatch.GetTimestamp();
            r.Component(k, 0);
            var label = Ms(Stopwatch.GetTimestamp() - t0);
            var slow = per.MaxBy(p => p.Ms);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"   class {k} (terrain {r.Nav.TerrainClass(k)}, width {r.Nav.Width(k)}{(r.Nav.NoRoads(k) ? ", no roads" : "")}): {per.Sum(p => p.Ms):0.0} ms for {per.Count} sectors " +
                $"(median {per.OrderBy(p => p.Ms).ElementAt(per.Count / 2).Ms:0.00} ms, slowest #{slow.Sec} {slow.Ms:0.00} ms with {slow.Nodes} nodes), {per.Average(p => p.Work):0} cells a sector, regions {label:0.00} ms"));
        }
        return (Environment.NewLine + string.Join(Environment.NewLine, lines), []);
    }

    // Regions times each class's connected regions: built the first time, then the first query after
    // each of a few map changes (a 4 x 4 block of cells blocked, then cleared again, at three places).
    private static string Regions(World w)
    {
        var r = w.Routing;
        var lines = new List<string>();
        var n = r.Nav.W;
        for (var k = 0; k < r.Nav.Classes; k++)
        {
            var t0 = Stopwatch.GetTimestamp();
            r.Component(k, 0);
            var first = Ms(Stopwatch.GetTimestamp() - t0);
            var after = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                int bx = n / 7 * (i / 2 + 1) + 5 * k, by = n / 5 + n / 9 * (i / 2);
                for (var y = by; y < by + 4; y++)
                {
                    for (var x = bx; x < bx + 4; x++)
                    {
                        w.Grid.Set(new Cell(x, y), i % 2 == 0 ? Terrain.Blocked : Terrain.Clear);
                    }
                }
                r.DrainChanges();
                var t1 = Stopwatch.GetTimestamp();
                r.Component(k, n * n - 1);
                after.Add(Ms(Stopwatch.GetTimestamp() - t1));
            }
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"   class {k}: first build {first:0.000} ms, after a change: median {after.OrderBy(a => a).ElementAt(after.Count / 2):0.000} ms, max {after.Max():0.000} ms"));
        }
        return Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
