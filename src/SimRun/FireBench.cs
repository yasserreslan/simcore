// FireBench measures forest fire at its cap (-bench-fire N; docs/design.md, "Forest fire"): a 1,024 x 1,024 map of forest
// (8 x 8 blocks of dense, grove, scrub or open ground by an integer hash: about half dense), 300 Resistance soldiers and
// technicals walking east through the middle with fog of war, and N spreading fires lit round the centre at the start ("fire")
// or none ("fire0"). Each workload runs twice in one process (the first warms the JIT) and reports the tick and the fire
// system's p50 / p99 / max, the ticks at the cap and the cells burnt.
//
//   dotnet run -c Release --project src/SimRun -- -bench-fire 8 -maxticks 2400 -out fire.json
//
// The incendiary variant (-bench-incendiary N; docs/design.md, "Incendiary bomb"): the map all dense forest, and N incendiary bombs
// going off round the centre at the start (the bomb's whole blast: its rings, its fire over 6 cells spreading at 3x, the cap)
// ("incendiary"), against none ("incendiary0").
//
//   dotnet run -c Release --project src/SimRun -- -bench-incendiary 4 -maxticks 2400 -out incendiary.json
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static class FireBench
{
    private const int Side = 1024;

    public static int Run(int fires, uint maxTicks, string outPath, string label, string contentDir, bool incendiary = false)
    {
        var content = SimCore.Content.ContentLoader.LoadDirectory(contentDir);
        var root = Path.GetDirectoryName(Path.GetFullPath(contentDir))!;
        var setup = Replay.LoadSetup(Path.Combine(root, "setups", "sentinel.json")).For(content, 42) with { Fog = true };
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var match = MatchCompiler.Compile(content, setup);
        var rules = SimCore.Content.TerrainLoader.LoadRules(Path.Combine(root, "terrain", "rules.json"));
        var results = new JsonArray();
        foreach (var withFire in new[] { false, true, false, true })
        {
            results.Add(One(match, rules, withFire ? fires : 0, maxTicks, incendiary));
        }
        if (outPath != "")
        {
            var doc = new JsonObject
            {
                ["label"] = label,
                ["date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["map"] = $"{Side}x{Side}",
                ["fires"] = fires,
                ["incendiary"] = incendiary,
                ["maxBurning"] = content.Fire?.MaxBurning ?? 0,
                ["results"] = results,
            };
            File.WriteAllText(outPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        return 0;
    }

    // Forest is the map's density at (x, y): 8 x 8 blocks, an integer hash picking dense (50%), grove (30%), scrub (15%) or open.
    private static byte Forest(int x, int y)
    {
        unchecked
        {
            var h = (uint)(x / 8) * 0x8DA6B343u ^ (uint)(y / 8) * 0xD8163841u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            var r = h % 100;
            return (byte)(r < 50 ? 3 : r < 80 ? 2 : r < 95 ? 1 : 0);
        }
    }

    private static JsonObject One(CompiledMatch match, TerrainRules rules, int fires, uint maxTicks, bool incendiary)
    {
        var w = new World(new Grid(Side, Side), match);
        var flags = new byte[Side * Side];
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                flags[y * Side + x] = incendiary ? (byte)3 : Forest(x, y);
            }
        }
        w.AttachTerrain(new TerrainMap(Side, Side, new int[(Side + 1) * (Side + 1)], flags, rules));
        var red = match.Players[1];
        var ids = Bench.Army(w, red, 300, 440, 480, 480, 544, Facing.East, vehicles: true);
        for (var i = 0; i < ids.Count; i += 20)
        {
            var group = ids.Skip(i).Take(20).ToList();
            w.Issue(new Command { Tick = World.InputDelay, Player = red.Id, Kind = CommandKind.Move, Units = group, Cell = new Cell(600, 480 + i % 64) });
        }
        var bomb = match.Content.Component("incendiary_bomb")?.Effects.OfType<WeaponEffect>().FirstOrDefault();
        for (var k = 0; k < fires && incendiary && bomb is not null; k++)
        {
            // Round the centre, 24 cells out: the whole blast (its rings, its fire).
            var at = new Cell(512 + 24 * (k % 4 == 0 ? 1 : k % 4 == 2 ? -1 : 0) + k / 4 * 16, 512 + 24 * (k % 4 == 1 ? 1 : k % 4 == 3 ? -1 : 0)).Center();
            w.Explode(at, bomb.Damage, bomb.Warhead, bomb.Splash, 0, bomb.SplashCenter, bomb.SplashEdge, bomb.SplashInner, bomb.SplashOuterPercent);
        }
        for (var k = 0; k < fires && !incendiary; k++)
        {
            // Round the centre, 24 cells out, on forest that can burn (the next cell east until one does).
            var x = 512 + 24 * (k % 4 == 0 ? 1 : k % 4 == 2 ? -1 : 0) + k / 4 * 3;
            var y = 512 + 24 * (k % 4 == 1 ? 1 : k % 4 == 3 ? -1 : 0);
            while (!w.IgniteCell(new Cell(x, y), 100))
            {
                x++;
            }
        }
        var probe = new FireProbe();
        w.Probe = probe;
        var tick = new List<double>();
        var phases = Enum.GetValues<StepPhase>();
        var phaseMs = phases.Select(_ => new List<double>()).ToArray();
        var fire = new List<double>();
        var atCap = 0;
        var paths = new List<double>();
        var most = 0;
        var cap = match.Content.Fire?.MaxBurning ?? 0;
        while (w.Tick < maxTicks)
        {
            var t0 = Stopwatch.GetTimestamp();
            w.Step();
            tick.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            fire.Add(probe.FireTicks * 1000.0 / Stopwatch.Frequency);
            for (var i = 0; i < phases.Length; i++)
            {
                phaseMs[i].Add(probe.Ticks[i] * 1000.0 / Stopwatch.Frequency);
            }
            atCap += w.Fires.Count == cap ? 1 : 0;
            paths.Add(w.PathRequests);
            most = Math.Max(most, w.Fires.Count);
        }
        var name = (incendiary ? "incendiary" : "fire") + (fires > 0 ? "" : "0");
        JsonObject Stats(List<double> xs)
        {
            var s = xs.Order().ToList();
            double P(int p) => s[Math.Clamp((int)Math.Ceiling(p / 100.0 * s.Count) - 1, 0, s.Count - 1)];
            return new JsonObject
            {
                ["mean"] = Math.Round(xs.Average(), 4), ["p50"] = Math.Round(P(50), 4), ["p95"] = Math.Round(P(95), 4),
                ["p99"] = Math.Round(P(99), 4), ["max"] = Math.Round(s[^1], 4), ["over2ms"] = xs.Count(x => x > 2), ["over10ms"] = xs.Count(x => x > 10),
            };
        }
        var systems = new JsonObject();
        for (var i = 0; i < phases.Length; i++)
        {
            systems[phases[i].ToString()] = Stats(phaseMs[i]);
        }
        var top = string.Join(", ", phases.Select((p, i) => (p, Stats(phaseMs[i])["p99"]!.GetValue<double>()))
            .OrderByDescending(x => x.Item2).Take(4).Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.p} {x.Item2:0.000}")));
        var res = new JsonObject
        {
            ["systemsMs"] = systems,
            ["workload"] = name,
            ["ticks"] = tick.Count,
            ["tickMs"] = Stats(tick),
            ["fireMs"] = Stats(fire),
            ["pathRequestsPerTick"] = Stats(paths),
            ["mostBurning"] = most,
            ["ticksAtCap"] = atCap,
            ["burnt"] = w.BurntCells.Count,
            ["fireSmokes"] = w.Smokes.Count(s => s.Fire),
            ["units"] = w.Units.Count,
        };
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: tick p50 {res["tickMs"]!["p50"]} p95 {res["tickMs"]!["p95"]} p99 {res["tickMs"]!["p99"]} max {res["tickMs"]!["max"]} ms (over 2 ms {res["tickMs"]!["over2ms"]}, over 10 ms {res["tickMs"]!["over10ms"]}); fire system mean {res["fireMs"]!["mean"]} " +
            $"p50 {res["fireMs"]!["p50"]} p99 {res["fireMs"]!["p99"]} max {res["fireMs"]!["max"]} ms; burning at most {most}, {atCap} ticks at the cap, {w.BurntCells.Count} burnt; p99 by system: {top}; path requests mean {paths.Average():0.00} max {paths.Max()}"));
        return res;
    }

    // FireProbe times the fire system of each tick (the phase after the ground movement).
    private sealed class FireProbe : IStepProbe
    {
        private long _last;

        public long FireTicks => Ticks[(int)StepPhase.Fire];

        public long[] Ticks { get; } = new long[Enum.GetValues<StepPhase>().Length];

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
}
