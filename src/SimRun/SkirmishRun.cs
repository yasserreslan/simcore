// SkirmishRun plays the skirmish AI against itself (SimRun -skirmish TICKS): the battle lobby, each army with 4 Iglas and a
// mortar added, on a 128 x 96 map of terrain with roads (a highway along the middle, two crossing roads) and woods beside them.
// It plays the same match twice, the AI without squads (as before phase S5) and with them, and prints the AI's think time per
// decision (the wall clock round SkirmishAi.Think), what the squads did and how the match ended.
using System.Diagnostics;
using System.Globalization;
using SimCore.Bots;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static class SkirmishRun
{
    public static int Run(uint ticks, ulong seed, string contentDir, bool flat)
    {
        var content = SimCore.Content.ContentLoader.LoadDirectory(contentDir);
        var root = Path.GetDirectoryName(Path.GetFullPath(contentDir))!;
        var setup = Replay.LoadSetup(Path.Combine(root, "setups", "battle.json"));
        setup = setup with
        {
            Players = [.. setup.Players.Select(p => p with { Army = [.. p.Army, new ArmyEntry { Unit = "manpads", Count = 4 }, new ArmyEntry { Unit = "mortar" }] })],
        };
        setup = setup.For(content, seed);
        var rules = SimCore.Content.TerrainLoader.LoadRules(Path.Combine(root, "terrain", "rules.json"));
        TerrainMap? M() => flat ? null : Map(rules);
        One(content, setup, M(), ticks, squads: false); // JIT warm-up, not printed
        One(content, setup, M(), ticks, squads: true);
        Console.WriteLine(One(content, setup, M(), ticks, squads: false));
        Console.WriteLine(One(content, setup, M(), ticks, squads: true));
        Console.WriteLine(FirstJobs(content, setup, M()));
        return 0;
    }

    // Map: 128 x 96, flat but for two low hills; a paved highway along rows 47 and 48, gravel roads down columns 30 and 97; dense
    // woods north of the highway in each half and groves south of it.
    internal static TerrainMap Map(TerrainRules rules)
    {
        const int w = 128, h = 96;
        var corners = new int[(w + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        {
            for (var x = 0; x <= w; x++)
            {
                var d1 = (int)Math.Sqrt((x - 45) * (x - 45) + (y - 30) * (y - 30));
                var d2 = (int)Math.Sqrt((x - 82) * (x - 82) + (y - 66) * (y - 66));
                corners[y * (w + 1) + x] = Math.Max(Math.Max(0, 2048 - 205 * d1), Math.Max(0, 2048 - 205 * d2));
            }
        }
        var flags = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                byte f = 0;
                if (y is 47 or 48)
                {
                    f = TerrainMap.WithRoad(0, RoadKind.Paved);
                }
                else if (x is 30 or 97)
                {
                    f = TerrainMap.WithRoad(0, RoadKind.Gravel);
                }
                else if (y is >= 40 and <= 44 && (x is >= 18 and <= 42 || x is >= 85 and <= 109))
                {
                    f = 3;
                }
                else if (y is >= 51 and <= 54 && (x is >= 22 and <= 40 || x is >= 87 and <= 105))
                {
                    f = 2;
                }
                flags[y * w + x] = f;
            }
        }
        return new TerrainMap(w, h, corners, flags, rules);
    }

    // FirstJobs times the decision that gives the first squads their jobs (the road zone's search), warm: the median of 20 games.
    private static string FirstJobs(ContentSet content, MatchSetup setup, TerrainMap? map)
    {
        var times = new List<double>();
        for (var i = 0; i < 25; i++)
        {
            var w = Replay.NewWorld(content, setup, map);
            var ai = new SkirmishAi(1);
            while (w.Tick <= ai.Every)
            {
                var t0 = Stopwatch.GetTimestamp();
                foreach (var c in ai.Think(w))
                {
                    w.Issue(c);
                }
                if (w.Tick == ai.Every && i >= 5)
                {
                    times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }
                w.Step();
            }
        }
        times.Sort();
        return string.Create(CultureInfo.InvariantCulture, $"the decision giving the first squads their jobs, warm: median {times[times.Count / 2]:0.000} ms, max {times[^1]:0.000} ms");
    }

    private static string One(ContentSet content, MatchSetup setup, TerrainMap? map, uint ticks, bool squads)
    {
        var w = Replay.NewWorld(content, setup, map);
        var ais = w.Players.Select(p => new SkirmishAi(p.Id) { UseSquads = squads }).ToList();
        var times = new List<double>();
        int formed = 0, ambushes = 0, airAmbushes = 0, holds = 0, sprung = 0, assaults = 0;
        var triggers = new int[16];
        var log = new List<string>();
        var maxMs = 0.0;
        uint maxAt = 0;
        while (w.Tick < ticks && !w.Result.Over)
        {
            foreach (var ai in ais)
            {
                var t0 = Stopwatch.GetTimestamp();
                var cmds = ai.Think(w);
                if (w.Tick % ai.Every == 0)
                {
                    var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    times.Add(ms);
                    if (ms > maxMs)
                    {
                        (maxMs, maxAt) = (ms, w.Tick);
                    }
                }
                foreach (var c in cmds)
                {
                    w.Issue(c);
                    if (c.Kind is CommandKind.SetSquadMode or CommandKind.FormSquad && squads && log.Count < 60)
                    {
                        log.Add($"  {w.Tick / 20 / 60}:{w.Tick / 20 % 60:00} P{c.Player} {c.Kind} {(c.Kind == CommandKind.SetSquadMode ? (SquadMode)c.Index : "")} {c.Units.Count} units {(c.Kind == CommandKind.SetSquadMode && c.Index >= 2 ? $"{c.Cell}->{c.Cell2}" : "")}");
                    }
                    if (c.Kind == CommandKind.SetSquadMode)
                    {
                        ambushes += c.Index == (int)SquadMode.Ambush ? 1 : 0;
                        holds += c.Index == (int)SquadMode.Hold ? 1 : 0;
                        assaults += c.Index == (int)SquadMode.Assault ? 1 : 0;
                    }
                }
            }
            w.Step();
            foreach (var e in w.SquadEvents)
            {
                if (squads && log.Count < 60 && e.Kind is SquadEventKind.Sprung or SquadEventKind.Disbanded or SquadEventKind.Ready)
                {
                    log.Add($"  {w.Tick / 20 / 60}:{w.Tick / 20 % 60:00} P{e.Owner} squad #{e.Squad} {e.Kind}{(e.Kind == SquadEventKind.Sprung && w.Squad(e.Squad) is { } q ? $" {q.AmbushTrigger}{(q.AmbushAir ? " (air)" : "")}" : "")}");
                }
                formed += e.Kind == SquadEventKind.Formed ? 1 : 0;
                if (e.Kind == SquadEventKind.Sprung)
                {
                    sprung++;
                    if (w.Squad(e.Squad) is { } s)
                    {
                        triggers[(int)s.AmbushTrigger]++;
                        airAmbushes += s.AmbushAir ? 1 : 0;
                    }
                }
            }
        }
        times.Sort();
        var mean = times.Count == 0 ? 0 : times.Average();
        var p99 = times.Count == 0 ? 0 : times[Math.Min(times.Count - 1, times.Count * 99 / 100)];
        var max = times.Count == 0 ? 0 : times[^1];
        var secs = w.Tick / World.TicksPerSecond;
        var units = string.Join(", ", w.Players.Select(p =>
        {
            var left = w.Units.Where(u => u.Owner == p.Id && u.Alive && (u.Def.Tags.Bits & p.Setup.Faction.DefeatedWithout.Bits) != 0).ToList();
            var kinds = string.Join(" ", left.GroupBy(u => u.Def.Name).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));
            return $"P{p.Id} {left.Count} soldiers{(p.Defeated ? " (defeated)" : "")} [{kinds}]";
        }));
        var outcome = w.Result.Over ? (w.Result.Winner == 0 ? "draw" : $"P{w.Result.Winner} wins") : "not over";
        var trig = string.Join(" ", Enumerable.Range(0, triggers.Length).Where(i => triggers[i] > 0).Select(i => $"{(AmbushTrigger)i}:{triggers[i]}"));
        var squadsNow = string.Join("; ", w.Squads.Select(s =>
            $"P{s.Owner} #{s.Id} {s.Members.Count} {s.Mode}{(s.Mode == SquadMode.Ambush ? (s.AmbushAir ? " air" : " road") + $" {s.AmbushStart}->{s.AmbushEnd} {s.State}" : "")}{(s.Mode == SquadMode.Hold ? $" at {s.HoldSpot}" : "")}"));
        var head = string.Create(CultureInfo.InvariantCulture,
            $"squads {(squads ? "on " : "off")}: {secs / 60}:{secs % 60:00}, {outcome}, {units}; think {times.Count} decisions mean {mean:0.000} ms p99 {p99:0.000} max {max:0.000} (tick {maxAt}); " +
            $"squads formed {formed}, ambush orders {ambushes}, hold orders {holds}, assault switches {assaults}, sprung {sprung} ({trig}{(airAmbushes > 0 ? $", air {airAmbushes}" : "")})\n  squads at the end: {squadsNow}");
        return log.Count > 0 ? head + "\n" + string.Join("\n", log) : head;
    }
}
