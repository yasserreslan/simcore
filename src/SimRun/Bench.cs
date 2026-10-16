// Bench measures the simulation headless: tick time per system, the hash, allocations
// and path requests, for six workloads at the given unit counts. It builds its own
// worlds from the content (no scenario, no bots) and only reads them while they run:
// the probe it hangs on World.Step reads a clock and never touches game state.
//
//   dotnet run -c Release --project src/SimRun -- -bench 500 -out bench/baseline_500.json
//   dotnet run -c Release --project src/SimRun -- -bench 100,500 -workloads combat,projectiles -budget 8
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static class Bench
{
    public static readonly string[] Workloads = ["stationary", "movement", "combat", "projectiles", "churn", "obstacles"];

    private const ulong Seed = 42;

    public sealed record Options(int[] Units, string[] Workloads, uint MaxTicks, double BudgetSeconds, string Out, string Label, string Content, string Setup);

    public static int Run(Options o)
    {
        var content = SimCore.Content.ContentLoader.LoadDirectory(o.Content);
        var setup = Replay.LoadSetup(o.Setup).For(content, Seed);
        var match = MatchCompiler.Compile(content, setup);

        // JIT warm-up on a small world, not recorded.
        RunOne(match, "combat", 100, 60, 5, quiet: true);

        var results = new JsonArray();
        foreach (var n in o.Units)
        {
            foreach (var wl in o.Workloads)
            {
                var r = RunOne(match, wl, n, o.MaxTicks, o.BudgetSeconds, quiet: false);
                results.Add(r);
            }
        }
        var doc = new JsonObject
        {
            ["label"] = o.Label,
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["machine"] = $"{Environment.ProcessorCount} cpus, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}",
            ["maxTicks"] = o.MaxTicks,
            ["budgetSeconds"] = o.BudgetSeconds,
            ["results"] = results,
        };
        var json = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (o.Out != "")
        {
            var dir = Path.GetDirectoryName(o.Out);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(o.Out, json + "\n");
        }
        return 0;
    }

    // Probe times each system of a tick with the high-resolution clock.
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

    private static JsonObject RunOne(CompiledMatch match, string workload, int n, uint maxTicks, double budget, bool quiet)
    {
        var (w, churn) = Build(match, workload, n);
        var probe = new Probe();
        w.Probe = probe;
        var phases = Enum.GetValues<StepPhase>();
        var tickMs = new List<double>();
        var hashMs = new List<double>();
        var alloc = new List<long>();
        var paths = new List<int>();
        var units = new List<int>();
        var projectiles = new List<int>();
        var phaseMs = phases.Select(_ => new List<double>()).ToArray();
        var wall = Stopwatch.StartNew();
        var spawned = 0;
        while (w.Tick < maxTicks && wall.Elapsed.TotalSeconds < budget)
        {
            if (churn is not null)
            {
                spawned += churn(w);
            }
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            w.Step();
            var t1 = Stopwatch.GetTimestamp();
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            _ = w.Hash();
            var t2 = Stopwatch.GetTimestamp();
            tickMs.Add(Ms(t1 - t0));
            hashMs.Add(Ms(t2 - t1));
            alloc.Add(a1 - a0);
            paths.Add(w.PathRequests);
            units.Add(w.Units.Count);
            projectiles.Add(w.Projectiles.Count);
            for (var i = 0; i < phases.Length; i++)
            {
                phaseMs[i].Add(Ms(probe.Ticks[i]));
            }
        }
        var systems = new JsonObject();
        for (var i = 0; i < phases.Length; i++)
        {
            systems[Name(phases[i])] = new JsonObject { ["mean"] = R(phaseMs[i].Average()), ["p99"] = R(Pct(phaseMs[i], 99)), ["max"] = R(phaseMs[i].Max()) };
        }
        var res = new JsonObject
        {
            ["workload"] = workload,
            ["units"] = n,
            ["ticks"] = tickMs.Count,
            ["wallSeconds"] = R(wall.Elapsed.TotalSeconds),
            ["tickMs"] = Stats(tickMs),
            ["hashMs"] = Stats(hashMs),
            ["systemsMs"] = systems,
            ["allocBytesPerTick"] = new JsonObject { ["mean"] = (long)alloc.Average(), ["p99"] = (long)Pct(alloc.Select(x => (double)x).ToList(), 99), ["max"] = alloc.Max() },
            ["pathRequestsPerTick"] = new JsonObject { ["mean"] = R(paths.Average()), ["max"] = paths.Max(), ["total"] = paths.Sum() },
            ["unitsLive"] = new JsonObject { ["start"] = units[0], ["end"] = units[^1], ["max"] = units.Max() },
            ["projectilesInFlight"] = new JsonObject { ["mean"] = R(projectiles.Average()), ["max"] = projectiles.Max() },
            ["spawned"] = spawned,
            ["finalHash"] = w.Hash().ToString("x16", CultureInfo.InvariantCulture),
        };
        if (!quiet)
        {
            var top = phases.Select((p, i) => (Name(p), phaseMs[i].Average())).OrderByDescending(x => x.Item2).Take(3)
                .Select(x => $"{x.Item1} {x.Item2:0.00}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{workload,-12} n={n,-5} ticks={tickMs.Count,-4} p50={Pct(tickMs, 50),7:0.000} p95={Pct(tickMs, 95),7:0.000} p99={Pct(tickMs, 99),7:0.000} max={tickMs.Max(),8:0.000} ms  hash={hashMs.Average():0.000}  alloc={alloc.Average() / 1024:0.0} KB  paths={paths.Sum()}  top: {string.Join(", ", top)}"));
        }
        return res;
    }

    private static string Name(StepPhase p) => char.ToLowerInvariant(p.ToString()[0]) + p.ToString()[1..];

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

    // ---- Worlds ----------------------------------------------------------

    // The infantry mix of an army, by preset name and weight (out of 100).
    private static readonly (string Name, int Weight)[] Infantry =
    [
        ("Rifleman", 45), ("Gunner", 10), ("Raider", 10), ("Tank hunter", 15), ("Marksman", 5), ("Drone hunter", 5), ("Skirmisher", 5), ("Sapper", 5),
    ];

    private static int Side(int n) => Math.Max(96, (int)Math.Ceiling(Math.Sqrt(n) * 3.2));

    private static (World W, Func<World, int>? Churn) Build(CompiledMatch match, string workload, int n)
    {
        var s = Side(n);
        var g = new Grid(s, s);
        if (workload == "obstacles")
        {
            Ridge(g);
        }
        var w = new World(g, match);
        var p1 = match.Players[0];
        var p2 = match.Players[1];
        var half = n / 2;
        switch (workload)
        {
            case "stationary":
                // Two armies far out of sight of each other: vision and orders run, nobody fights.
                Army(w, p1, half, 2, s / 3 - 2, 2, s - 2, Facing.East, vehicles: true);
                Army(w, p2, n - half, s - s / 3 + 2, s - 2, 2, s - 2, Facing.West, vehicles: true);
                break;
            case "movement":
            {
                // One player's army in the west third marches east across open ground, in groups of 20.
                var ids = Army(w, p1, n, 2, s / 3, 2, s - 2, Facing.East, vehicles: true);
                MoveGroups(w, 1, ids, s - 6, s);
                break;
            }
            case "combat":
            case "churn":
            {
                // Two armies a few cells apart, inside weapon range: they acquire targets and fight.
                var depth = Math.Max(4, half / (s - 4) + 2);
                var mid = s / 2;
                Army(w, p1, half, mid - 3 - depth, mid - 3, 2, s - 2, Facing.East, vehicles: true);
                Army(w, p2, n - half, mid + 3, mid + 3 + depth, 2, s - 2, Facing.West, vehicles: true);
                if (workload == "churn")
                {
                    // Keep the count up: every tick, replace the dead behind each line.
                    var rng = new System.Random(7);
                    return (w, world =>
                    {
                        var missing = n - world.Units.Count(u => u.Inside == 0);
                        var made = 0;
                        for (var k = 0; k < missing; k++)
                        {
                            var p = k % 2 == 0 ? p1 : p2;
                            var x = p == p1 ? mid - 4 - depth - rng.Next(3) : mid + 4 + depth + rng.Next(3);
                            world.Spawn(Pick(match, p, rng.Next(100)), new Cell(x, 2 + rng.Next(s - 4)), p == p1 ? Facing.East : Facing.West);
                            made++;
                        }
                        return made;
                    });
                }
                break;
            }
            case "projectiles":
            {
                // Rocket trucks and tank hunters on both sides within range: rockets and missiles in flight all the time.
                var mid = s / 2;
                var depth = Math.Max(4, half / (s - 4) + 2);
                Launchers(w, p1, half, mid - 3 - depth, mid - 3, s, Facing.East);
                Launchers(w, p2, n - half, mid + 3, mid + 3 + depth, s, Facing.West);
                break;
            }
            case "obstacles":
            {
                // A large group behind the ridge is ordered across it: paths go through the three gaps.
                var ids = Army(w, p1, n, 2, s / 2 - 4, 2, s - 2, Facing.East, vehicles: true);
                MoveGroups(w, 1, ids, s - 6, s);
                break;
            }
            default:
                throw new ArgumentException($"bench: unknown workload \"{workload}\"; want one of {string.Join(", ", Workloads)}");
        }
        return (w, null);
    }

    // Ridge blocks the middle column pair except three 4-cell gaps, like the battle map, plus rock clusters.
    private static void Ridge(Grid g)
    {
        var s = g.Width;
        var gaps = new[] { s / 4, s / 2, 3 * s / 4 };
        for (var y = 6; y < s - 6; y++)
        {
            if (gaps.Any(gy => y >= gy - 2 && y < gy + 2))
            {
                continue;
            }
            g.Set(new Cell(s / 2 - 1, y), Terrain.Blocked);
            g.Set(new Cell(s / 2, y), Terrain.Blocked);
        }
        var r = new System.Random(3);
        for (var i = 0; i < s / 5; i++)
        {
            var x = s / 2 + 3 + r.Next(s / 2 - 10);
            var y = 2 + r.Next(s - 6);
            for (var d = 0; d < 4; d++)
            {
                g.Set(new Cell(x + d % 2, y + d / 2), Terrain.Blocked);
            }
        }
    }

    private static UnitDef Pick(CompiledMatch m, CompiledPlayer p, int roll)
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

    // Army spawns count units of player p spread over the box [x0,x1) x [y0,y1): infantry by
    // the mix, and (with vehicles) one technical in ten with a driver who boards it by command.
    // Drivers count toward the total. Returns the IDs of the units that can be ordered.
    private static List<uint> Army(World w, CompiledPlayer p, int count, int x0, int x1, int y0, int y1, int facing, bool vehicles)
    {
        var ids = new List<uint>();
        var cells = Cells(x0, x1, y0, y1, count);
        var tech = p.Find(w.Content, "technical")!;
        var driver = p.Find(w.Content, "Rifleman")!;
        var made = 0;
        for (var i = 0; made < count; i++)
        {
            var c = cells[i % cells.Count];
            if (vehicles && i % 10 == 9 && count - made >= 2)
            {
                var v = w.Spawn(tech, c, facing);
                var d = w.Spawn(driver, c, facing);
                w.Issue(new Command { Tick = 0, Player = p.Id, Kind = CommandKind.Enter, Units = [d], Target = v });
                ids.Add(v);
                made += 2;
                continue;
            }
            ids.Add(w.Spawn(Pick(w.Match, p, (i * 37) % 100), c, facing));
            made++;
        }
        return ids;
    }

    // Launchers spawns a projectile-heavy army: a third medium rocket launchers, a third light
    // rocket trucks (each with a boarding driver) and the rest tank hunters.
    private static void Launchers(World w, CompiledPlayer p, int count, int x0, int x1, int s, int facing)
    {
        var cells = Cells(x0, x1, 2, s - 2, count);
        var defs = new[] { p.Find(w.Content, "medium_rocket_launcher")!, p.Find(w.Content, "light_rocket_truck")! };
        var hunter = p.Find(w.Content, "Tank hunter")!;
        var driver = p.Find(w.Content, "Rifleman")!;
        var made = 0;
        for (var i = 0; made < count; i++)
        {
            var c = cells[i % cells.Count];
            if (i % 3 != 2 && count - made >= 2)
            {
                var v = w.Spawn(defs[i % 3], c, facing);
                var d = w.Spawn(driver, c, facing);
                w.Issue(new Command { Tick = 0, Player = p.Id, Kind = CommandKind.Enter, Units = [d], Target = v });
                made += 2;
                continue;
            }
            w.Spawn(hunter, c, facing);
            made++;
        }
    }

    // Cells returns up to count cells spread evenly over a box, row by row.
    private static List<Cell> Cells(int x0, int x1, int y0, int y1, int count)
    {
        var all = new List<Cell>();
        for (var x = x0; x < x1; x++)
        {
            for (var y = y0; y < y1; y++)
            {
                all.Add(new Cell(x, y));
            }
        }
        if (all.Count <= count)
        {
            return all;
        }
        var step = (double)all.Count / count;
        return Enumerable.Range(0, count).Select(i => all[(int)(i * step)]).ToList();
    }

    // MoveGroups orders the units east in groups of 20, each group to its own row near column x.
    private static void MoveGroups(World w, byte player, List<uint> ids, int x, int s)
    {
        for (var i = 0; i < ids.Count; i += 20)
        {
            var group = ids.Skip(i).Take(20).ToList();
            var y = 2 + (i / 20 * 7) % (s - 4);
            w.Issue(new Command { Tick = World.InputDelay, Player = player, Kind = CommandKind.Move, Units = group, Cell = new Cell(x, y) });
        }
    }
}
