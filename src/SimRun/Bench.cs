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

    // RunAir is the air bench (-bench-air N, E3's gate: 100 aircraft in mission modes add at most
    // 0.5 ms to the tick p99): the Sentinel lobby, 300 Resistance soldiers and vehicles spread over
    // a 256-cell map, run once without aircraft ("air0") and once with N Sentinel drones in mission
    // modes over them ("air": Watchers on Guard and Patrol, Overseers on Search and Roam, a tenth on
    // a plain orbit). It prints both and the difference of the p99 and the mean.
    /// <summary>
    /// The missile defence bench (-bench-sam N; the game repo's docs/patriot-design.md): a defended base under a raid of N
    /// enemy launchers (twin missile, heavy rocket, tactical missile and drone trucks, a quarter each) aimed at it, without
    /// ("raid0") and with ("raid") N/2 set-up missile defence launchers and N/4 radar trucks. Reports what the defence adds.
    /// </summary>
    public static int RunSam(int raiders, uint maxTicks, double budget, string outPath, string label, string contentDir)
    {
        var content = SimCore.Content.ContentLoader.LoadDirectory(contentDir);
        var setup = Replay.LoadSetup(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir))!, "setups", "sentinel.json")).For(content, Seed);
        var match = MatchCompiler.Compile(content, setup);
        RunOne(match, "raid0", raiders, 60, 5, quiet: true); // JIT warm-up, not recorded
        RunOne(match, "raid", raiders, 60, 5, quiet: true);
        var basis = RunOne(match, "raid0", raiders, maxTicks, budget, quiet: false);
        var raid = RunOne(match, "raid", raiders, maxTicks, budget, quiet: false);
        double P(JsonObject r, string k) => r["tickMs"]![k]!.GetValue<double>();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"raid: {raiders} raiders against {raiders / 2} launchers and {Math.Max(1, raiders / 4)} radars add p99 {P(raid, "p99") - P(basis, "p99"):0.000} ms, mean {P(raid, "mean") - P(basis, "mean"):0.000} ms"));
        var doc = new JsonObject
        {
            ["label"] = label,
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["raiders"] = raiders,
            ["addedP99Ms"] = R(P(raid, "p99") - P(basis, "p99")),
            ["addedMeanMs"] = R(P(raid, "mean") - P(basis, "mean")),
            ["results"] = new JsonArray(basis, raid),
        };
        if (outPath != "")
        {
            File.WriteAllText(outPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        return 0;
    }

    // RaidWorld: 256 x 256; player 1's base (riflemen spread round (128, 190)) and, defended, its set-up missile defence
    // launchers and radar trucks; player 2's n crewed launchers 80 to 140 cells north, each told to attack the ground in the
    // base (they set up, then fire everything they carry).
    private static World RaidWorld(CompiledMatch match, int n, bool defended)
    {
        var w = new World(new Grid(256, 256), match);
        var blue = match.Players[0];
        var red = match.Players[1];
        var rng = new System.Random(23);
        var rifle = blue.Find(match.Content, "Rifleman")!;
        for (var i = 0; i < 40; i++)
        {
            w.Spawn(rifle, new Cell(108 + rng.Next(40), 180 + rng.Next(20)), Facing.North);
        }
        if (defended)
        {
            var launcher = blue.Find(match.Content, "patriot_launcher")!;
            var radar = blue.Find(match.Content, "radar_truck")!;
            for (var i = 0; i < n / 2; i++)
            {
                var c = new Cell(100 + (i * 7) % 56, 205 + (i * 7) / 56 * 6);
                var l = w.Spawn(launcher, c, Facing.North);
                w.Unit(l)!.Deploy = DeployState.Deployed;
                w.Issue(new Command { Tick = 0, Player = blue.Id, Kind = CommandKind.Enter, Units = [w.Spawn(rifle, c, Facing.North)], Target = l }); // its crew
            }
            for (var i = 0; i < Math.Max(1, n / 4); i++)
            {
                w.Unit(w.Spawn(radar, new Cell(104 + (i * 9) % 48, 225 + (i * 9) / 48 * 6), Facing.North))!.Deploy = DeployState.Deployed;
            }
        }
        var kinds = new[] { "missile_truck", "heavy_rocket_truck", "ballistic_truck", "drone_truck" };
        var distance = new[] { 90, 70, 130, 120 };
        var driver = red.Find(match.Content, "Rifleman")!;
        for (var i = 0; i < n; i++)
        {
            var k = i % kinds.Length;
            var c = new Cell(40 + rng.Next(176), 190 - distance[k] + rng.Next(8));
            var v = w.Spawn(red.Find(match.Content, kinds[k])!, c, Facing.South);
            var d = w.Spawn(driver, c, Facing.South);
            w.Issue(new Command { Tick = 0, Player = red.Id, Kind = CommandKind.Enter, Units = [d], Target = v });
            w.Issue(new Command { Tick = 10, Player = red.Id, Kind = CommandKind.AttackGround, Units = [v], Cell = new Cell(110 + rng.Next(36), 182 + rng.Next(16)) });
        }
        return w;
    }

    /// <summary>
    /// The helicopter bench (-bench-heli N; the helicopters night): a hilly 256 x 256 map, player 2's ground army of 300
    /// (soldiers, vehicles, its AA soldiers among them) in the east, without ("heli0") and with ("heli") N Sentinel
    /// helicopters (half attack, a quarter armoured, a quarter light) from the west on attack-move into the army: the
    /// rotary flight, the terrain following, the cover search and pop-ups, the guns, rockets and missiles, the AA fire back,
    /// out of control and the crashes. Reports what the helicopters add to the tick.
    /// </summary>
    public static int RunHeli(int helicopters, uint maxTicks, double budget, string outPath, string label, string contentDir)
    {
        var content = SimCore.Content.ContentLoader.LoadDirectory(contentDir);
        var setup = Replay.LoadSetup(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir))!, "setups", "sentinel.json")).For(content, Seed);
        var match = MatchCompiler.Compile(content, setup);
        _heliRules = SimCore.Content.TerrainLoader.LoadRules(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir))!, "terrain", "rules.json"));
        RunOne(match, "heli0", helicopters, 60, 5, quiet: true); // JIT warm-up, not recorded
        RunOne(match, "heli", helicopters, 60, 5, quiet: true);
        var basis = RunOne(match, "heli0", helicopters, maxTicks, budget, quiet: false);
        var heli = RunOne(match, "heli", helicopters, maxTicks, budget, quiet: false);
        double P(JsonObject r, string k) => r["tickMs"]![k]!.GetValue<double>();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"heli: {helicopters} helicopters attacking add p99 {P(heli, "p99") - P(basis, "p99"):0.000} ms, mean {P(heli, "mean") - P(basis, "mean"):0.000} ms"));
        var doc = new JsonObject
        {
            ["label"] = label,
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["helicopters"] = helicopters,
            ["addedP99Ms"] = R(P(heli, "p99") - P(basis, "p99")),
            ["addedMeanMs"] = R(P(heli, "mean") - P(basis, "mean")),
            ["results"] = new JsonArray(basis, heli),
        };
        if (outPath != "")
        {
            File.WriteAllText(outPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        return 0;
    }

    private static TerrainRules? _heliRules;

    // HeliWorld: 256 x 256 with rolling hills (ridges every 40 cells, up to 24 m, so the pop-up finds cover); player 2's
    // army of 300 in the east (its usual mix, AA soldiers and air defence vehicles included) and, with helicopters, n of
    // player 1's from the west on attack-move into it (a helipad behind them for the ones that come home).
    private static World HeliWorld(CompiledMatch match, int n, bool withHelis)
    {
        const int s = 256;
        var w = new World(new Grid(s, s), match);
        var corners = new int[(s + 1) * (s + 1)];
        for (var y = 0; y <= s; y++)
        {
            for (var x = 0; x <= s; x++)
            {
                var d = Math.Abs(((x + 20) % 40) - 20); // 0 at the ridge lines x = 20, 60, 100, ...
                corners[y * (s + 1) + x] = Math.Max(0, 12 - d) * 1024 + (y % 64 < 32 ? 0 : 512 * (y % 32) / 8);
            }
        }
        w.AttachTerrain(new TerrainMap(s, s, corners, new byte[s * s], _heliRules!));
        var blue = match.Players[0];
        var red = match.Players[1];
        Army(w, red, 300, 150, 230, 40, 216, Facing.West, vehicles: true);
        var aa = red.Find(match.Content, "manpads")!;
        var sam = red.Find(match.Content, "sam_vehicle")!;
        var crew = red.Find(match.Content, "Rifleman")!;
        for (var i = 0; i < 12; i++)
        {
            w.Spawn(aa, new Cell(160 + (i % 4) * 16, 50 + (i / 4) * 60), Facing.West);
        }
        for (var i = 0; i < 4; i++)
        {
            var c = new Cell(200, 60 + i * 45);
            var v = w.Spawn(sam, c, Facing.West);
            w.Issue(new Command { Tick = 0, Player = red.Id, Kind = CommandKind.Enter, Units = [w.Spawn(crew, c, Facing.West)], Target = v });
        }
        if (!withHelis)
        {
            return w;
        }
        w.Spawn(blue.Find(match.Content, "helipad")!, new Cell(8, 120), Facing.South);
        var kinds = new[] { "attack_helicopter", "armored_helicopter", "attack_helicopter", "light_helicopter" };
        var rng = new System.Random(19);
        for (var i = 0; i < n; i++)
        {
            var c = new Cell(20 + rng.Next(40), 30 + rng.Next(196));
            var u = w.Spawn(blue.Find(match.Content, kinds[i % kinds.Length])!, c, Facing.East);
            w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.AttackMove, Units = [u], Cell = new Cell(190 + rng.Next(40), c.Y) });
        }
        return w;
    }

    public static int RunAir(int aircraft, uint maxTicks, double budget, string outPath, string label, string contentDir)
    {
        var content = SimCore.Content.ContentLoader.LoadDirectory(contentDir);
        var setup = Replay.LoadSetup(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir))!, "setups", "sentinel.json")).For(content, Seed);
        var match = MatchCompiler.Compile(content, setup);
        // JIT and first-sight warm-up of both worlds, not recorded (the first tick's vision of a
        // fresh world otherwise lands in whichever workload runs first).
        RunOne(match, "air0", aircraft, 60, 5, quiet: true);
        RunOne(match, "air", aircraft, 60, 5, quiet: true);
        var results = new JsonArray();
        var basis = RunOne(match, "air0", aircraft, maxTicks, budget, quiet: false);
        var air = RunOne(match, "air", aircraft, maxTicks, budget, quiet: false);
        results.Add(basis);
        results.Add(air);
        double P(JsonObject r, string k) => r["tickMs"]![k]!.GetValue<double>();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"air: {aircraft} aircraft add p99 {P(air, "p99") - P(basis, "p99"):0.000} ms, mean {P(air, "mean") - P(basis, "mean"):0.000} ms"));
        // E4: the same with N fixed wings (the test fixed wing of content/fixtures, the plan's
        // multirole numbers: the jets' kinematics) in the same mission mix, when the fixture is there.
        var fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir))!, "fixtures", "fixed_wing_test.json");
        JsonObject? jets = null;
        if (File.Exists(fixture))
        {
            var jc = JetContent(contentDir, fixture);
            var jetMatch = MatchCompiler.Compile(jc, Replay.LoadSetup(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir))!, "setups", "sentinel.json")).For(jc, Seed));
            RunOne(jetMatch, "jets", aircraft, 60, 5, quiet: true);
            jets = RunOne(jetMatch, "jets", aircraft, maxTicks, budget, quiet: false);
            results.Add(jets);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"jets: {aircraft} fixed wings add p99 {P(jets, "p99") - P(basis, "p99"):0.000} ms, mean {P(jets, "mean") - P(basis, "mean"):0.000} ms"));
        }
        // E6: N real jets in air-to-air combat ("dogfight"): half Sentinel (multiroles and air superiority
        // jets), half enemy air superiority jets (the bench lets the Resistance field them), all on Guard
        // against aircraft and drones over the same ground army (its air defence fires at them too):
        // missiles, guns, flares, chaff, breaks.
        var dfc = DogfightContent(contentDir);
        var dfMatch = MatchCompiler.Compile(dfc, Replay.LoadSetup(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir))!, "setups", "sentinel.json")).For(dfc, Seed));
        RunOne(dfMatch, "dogfight", aircraft, 60, 5, quiet: true);
        var dogfight = RunOne(dfMatch, "dogfight", aircraft, maxTicks, budget, quiet: false);
        results.Add(dogfight);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"dogfight: {aircraft} jets in combat add p99 {P(dogfight, "p99") - P(basis, "p99"):0.000} ms, mean {P(dogfight, "mean") - P(basis, "mean"):0.000} ms"));
        // E7: N real jets striking the ground ("strike"): Sentinel multiroles (both presets) and air
        // superiority jets on Guard against vehicles, infantry and structures over the same ground army:
        // strike runs, bombs (the release solution each run-in tick, the bombs' fall), glide bombs,
        // strafing; its air defence fires back.
        RunOne(match, "strike", aircraft, 60, 5, quiet: true);
        var strike = RunOne(match, "strike", aircraft, maxTicks, budget, quiet: false);
        results.Add(strike);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"strike: {aircraft} jets striking add p99 {P(strike, "p99") - P(basis, "p99"):0.000} ms, mean {P(strike, "mean") - P(basis, "mean"):0.000} ms"));
        var doc = new JsonObject
        {
            ["label"] = label,
            ["strikeAddedP99Ms"] = R(P(strike, "p99") - P(basis, "p99")),
            ["strikeAddedMeanMs"] = R(P(strike, "mean") - P(basis, "mean")),
            ["dogfightAddedP99Ms"] = R(P(dogfight, "p99") - P(basis, "p99")),
            ["dogfightAddedMeanMs"] = R(P(dogfight, "mean") - P(basis, "mean")),
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["aircraft"] = aircraft,
            ["addedP99Ms"] = R(P(air, "p99") - P(basis, "p99")),
            ["addedMeanMs"] = R(P(air, "mean") - P(basis, "mean")),
            ["jetsAddedP99Ms"] = jets is null ? null : R(P(jets, "p99") - P(basis, "p99")),
            ["jetsAddedMeanMs"] = jets is null ? null : R(P(jets, "mean") - P(basis, "mean")),
            ["results"] = results,
        };
        if (outPath != "")
        {
            File.WriteAllText(outPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        return 0;
    }

    // JetContent is the content directory plus the test fixed wing (fixture), added to the Sentinel's list.
    private static ContentSet JetContent(string contentDir, string fixture)
    {
        var sources = Directory.EnumerateFiles(contentDir, "*.json", SearchOption.AllDirectories)
            .Select(p => new SimCore.Content.ContentSource(Path.GetRelativePath(contentDir, p).Replace('\\', '/'), File.ReadAllText(p)))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        var i = sources.FindIndex(c => c.Name.EndsWith("factions/sentinel.json", StringComparison.Ordinal));
        sources[i] = new SimCore.Content.ContentSource(sources[i].Name,
            sources[i].Json.Replace("\"watcher_drone\",", "\"watcher_drone\", \"test_fixed_wing\",", StringComparison.Ordinal));
        sources.Add(new SimCore.Content.ContentSource("fixtures/fixed_wing_test.json", File.ReadAllText(fixture)));
        return SimCore.Content.ContentLoader.Load(sources);
    }

    // DogfightContent is the content directory with the air superiority jet in the Resistance's list too (bench only).
    private static ContentSet DogfightContent(string contentDir)
    {
        var sources = Directory.EnumerateFiles(contentDir, "*.json", SearchOption.AllDirectories)
            .Select(p => new SimCore.Content.ContentSource(Path.GetRelativePath(contentDir, p).Replace('\\', '/'), File.ReadAllText(p)))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        var i = sources.FindIndex(c => c.Name.EndsWith("factions/resistance.json", StringComparison.Ordinal));
        sources[i] = new SimCore.Content.ContentSource(sources[i].Name,
            sources[i].Json.Replace("\"sam_vehicle\",", "\"sam_vehicle\", \"fighter_jet\",", StringComparison.Ordinal));
        return SimCore.Content.ContentLoader.Load(sources);
    }

    // DogfightWorld is the air bench's ground world plus n jets on Guard (aircraft and drones) over the
    // army: player 1 multiroles and air superiority jets from the west, player 2 air superiority jets from the east.
    private static World DogfightWorld(CompiledMatch match, int n)
    {
        var w = AirWorld(match, n, false);
        var blue = match.Players[0];
        var red = match.Players[1];
        var multirole = blue.Find(match.Content, "Multirole 500 lb")!;
        var fighter = blue.Find(match.Content, "fighter_jet")!;
        var enemy = red.Find(match.Content, "fighter_jet")!;
        var rng = new System.Random(13);
        for (var i = 0; i < n; i++)
        {
            var west = i % 2 == 0;
            var def = west ? (i % 4 == 0 ? multirole : fighter) : enemy;
            var c = new Cell(west ? 60 + rng.Next(30) : 166 + rng.Next(30), 60 + rng.Next(136));
            var u = w.Unit(w.Spawn(def, c, west ? Facing.East : Facing.West))!;
            w.Issue(new Command
            {
                Tick = 0, Player = u.Owner, Kind = CommandKind.AirTask, Units = [u.Id], Mission = AirTaskKind.Guard,
                Cell = new Cell(64, 64), Cell2 = new Cell(192, 192), Filter = (byte)(AirTargetKinds.Aircraft | AirTargetKinds.Drones),
            });
        }
        return w;
    }

    // StrikeWorld is the air bench's ground world plus n Sentinel jets on Guard against vehicles, infantry
    // and structures over the army (E7): a third each multiroles with 500 lb and 2,000 lb bombs and air
    // superiority jets (glide bombs), from the west.
    private static World StrikeWorld(CompiledMatch match, int n)
    {
        var w = AirWorld(match, n, false);
        var blue = match.Players[0];
        var defs = new[] { blue.Find(match.Content, "Multirole 500 lb")!, blue.Find(match.Content, "Multirole 2000 lb")!, blue.Find(match.Content, "fighter_jet")! };
        var rng = new System.Random(17);
        for (var i = 0; i < n; i++)
        {
            var c = new Cell(60 + rng.Next(40), 60 + rng.Next(136));
            var u = w.Unit(w.Spawn(defs[i % 3], c, Facing.East))!;
            w.Issue(new Command
            {
                Tick = 0, Player = u.Owner, Kind = CommandKind.AirTask, Units = [u.Id], Mission = AirTaskKind.Guard,
                Cell = new Cell(64, 64), Cell2 = new Cell(192, 192), Filter = (byte)(AirTargetKinds.Vehicles | AirTargetKinds.Infantry | AirTargetKinds.Structures),
            });
        }
        return w;
    }

    // AirWorld builds the air bench's world (see RunAir): with aircraft false, the ground alone;
    // jets: the aircraft are all the test fixed wing.
    private static World AirWorld(CompiledMatch match, int aircraft, bool withAir, bool jets = false)
    {
        const int s = 256;
        var w = new World(new Grid(s, s), match);
        var blue = match.Players[0];
        var red = match.Players[1];
        Army(w, red, 300, 70, 190, 70, 190, Facing.West, vehicles: true);
        if (!withAir)
        {
            return w;
        }
        var watcher = blue.Find(match.Content, jets ? "test_fixed_wing" : "watcher_drone")!;
        var overseer = blue.Find(match.Content, jets ? "test_fixed_wing" : "overseer_drone")!;
        var rng = new System.Random(11);
        for (var i = 0; i < aircraft; i++)
        {
            var kind = i % 10;
            var def = kind is 0 or 1 or 2 or 3 or 4 or 5 ? watcher : overseer;
            var c = new Cell(60 + rng.Next(136), 60 + rng.Next(136));
            var u = w.Unit(w.Spawn(def, c, rng.Next(Facing.Count)))!;
            var a = new Cell(70 + rng.Next(60), 70 + rng.Next(60));
            var b = new Cell(a.X + 30 + rng.Next(40), a.Y + 30 + rng.Next(40));
            Command? cmd = kind switch
            {
                0 or 1 or 2 => new Command { Kind = CommandKind.AirTask, Mission = AirTaskKind.Guard, Cell = a, Cell2 = b },
                3 or 4 => new Command { Kind = CommandKind.AirTask, Mission = AirTaskKind.Patrol, Cell = a },
                5 or 6 or 7 => new Command { Kind = CommandKind.AirTask, Mission = AirTaskKind.Search, Cell = a, Cell2 = b, On = true },
                8 => new Command { Kind = CommandKind.AirTask, Mission = AirTaskKind.Roam, Cell = a, Cell2 = b },
                _ => null,
            };
            if (cmd is not null)
            {
                w.Issue(cmd with { Tick = 0, Player = 1, Units = [u.Id] });
                if (cmd.Mission == AirTaskKind.Patrol)
                {
                    w.Issue(cmd with { Tick = 0, Player = 1, Units = [u.Id], Cell = b, Queue = true });
                    w.Issue(cmd with { Tick = 0, Player = 1, Units = [u.Id], Cell = new Cell(a.X, b.Y), Queue = true });
                }
            }
        }
        return w;
    }

    // Probe times each system of a tick with the high-resolution clock.
    private sealed class Probe : IStepProbe
    {
        public readonly long[] Ticks = new long[Enum.GetValues<StepPhase>().Length];
        public readonly long[] Bytes = new long[Enum.GetValues<StepPhase>().Length];
        private long _last, _lastBytes;

        public void Begin()
        {
            Array.Clear(Ticks);
            Array.Clear(Bytes);
            _lastBytes = GC.GetAllocatedBytesForCurrentThread();
            _last = Stopwatch.GetTimestamp();
        }

        public void After(StepPhase phase)
        {
            var now = Stopwatch.GetTimestamp();
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            Ticks[(int)phase] += now - _last;
            Bytes[(int)phase] += bytes - _lastBytes;
            _lastBytes = bytes;
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
        var phaseBytes = new long[phases.Length];
        var wall = Stopwatch.StartNew();
        var spawned = 0;
        long launches = 0, bursts = 0, salvos = 0, losses = 0, bombs = 0, strafes = 0, fused = 0, kills = 0;
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
            (launches, bursts, salvos, losses) = (launches + w.AirLaunches.Count, bursts + w.GunBursts.Count, salvos + w.Countermeasures.Count, losses + w.AirLosses.Count);
            (bombs, strafes) = (bombs + w.BombImpacts.Count, strafes + w.StrafeBursts.Count);
            foreach (var b in w.AirBursts)
            {
                fused += b.Kind is AirBurstKind.Kill or AirBurstKind.Miss or AirBurstKind.Hit ? 1 : 0;
                kills += b.Kind == AirBurstKind.Kill ? 1 : 0;
            }
            for (var i = 0; i < phases.Length; i++)
            {
                phaseMs[i].Add(Ms(probe.Ticks[i]));
                phaseBytes[i] += probe.Bytes[i];
            }
        }
        var systems = new JsonObject();
        for (var i = 0; i < phases.Length; i++)
        {
            systems[Name(phases[i])] = new JsonObject
            {
                ["mean"] = R(phaseMs[i].Average()), ["p99"] = R(Pct(phaseMs[i], 99)), ["max"] = R(phaseMs[i].Max()),
                ["allocBytesPerTick"] = phaseBytes[i] / tickMs.Count,
            };
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
            ["airCombat"] = new JsonObject { ["launches"] = launches, ["gunBursts"] = bursts, ["salvos"] = salvos, ["aircraftLost"] = losses, ["bombs"] = bombs, ["strafes"] = strafes, ["interceptsFused"] = fused, ["interceptKills"] = kills },
            ["spawned"] = spawned,
            ["finalHash"] = w.Hash().ToString("x16", CultureInfo.InvariantCulture),
        };
        if (!quiet)
        {
            var top = phases.Select((p, i) => (Name(p), phaseMs[i].Average())).OrderByDescending(x => x.Item2).Take(3)
                .Select(x => $"{x.Item1} {x.Item2:0.00}");
            var topAlloc = phases.Select((p, i) => (Name(p), phaseBytes[i] / (double)tickMs.Count / 1024)).Where(x => x.Item2 >= 1)
                .OrderByDescending(x => x.Item2).Take(4).Select(x => $"{x.Item1} {x.Item2:0.0}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{workload,-12} n={n,-5} ticks={tickMs.Count,-4} mean={tickMs.Average(),7:0.000} p50={Pct(tickMs, 50),7:0.000} p95={Pct(tickMs, 95),7:0.000} p99={Pct(tickMs, 99),7:0.000} max={tickMs.Max(),8:0.000} ms  hash={hashMs.Average():0.000}  alloc={alloc.Average() / 1024:0.0} KB  paths={paths.Sum()}  top: {string.Join(", ", top)}  alloc KB: {string.Join(", ", topAlloc)}"));
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
        if (workload == "dogfight")
        {
            return (DogfightWorld(match, n), null);
        }
        if (workload == "strike")
        {
            return (StrikeWorld(match, n), null);
        }
        if (workload is "raid" or "raid0")
        {
            return (RaidWorld(match, n, workload == "raid"), null);
        }
        if (workload is "heli" or "heli0")
        {
            return (HeliWorld(match, n, workload == "heli"), null);
        }
        if (workload is "air" or "air0" or "jets")
        {
            return (AirWorld(match, n, workload != "air0", workload == "jets"), null);
        }
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
    internal static List<uint> Army(World w, CompiledPlayer p, int count, int x0, int x1, int y0, int y1, int facing, bool vehicles)
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
                w.Issue(new Command { Tick = w.Tick, Player = p.Id, Kind = CommandKind.Enter, Units = [d], Target = v });
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
