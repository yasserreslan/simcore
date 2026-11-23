// The game's --demo=battle (its client/scripts/Demo.Battle.cs, INF-BATTLE) staged here, to measure routing's on-demand sector
// builds on a real map (ROUTE-WARM): 30 squads of eight a side (480 men, the Resistance mirror of content/setups/battle.json, each
// army trimmed to one loose rifleman as the game's battle lobby), in the demo's eleven groups across zuberec_mini's middle band,
// the map's town spawned neutral; tick 3 each squad formed, tick 5 the only orders (Attack-move, or Hold facing the threat). The
// world is made with routing prepared light (regions, no sectors), as the game does for the battle, then World.WarmRouting
// (-route-warm on, the default; off: as before; full: every sector at load instead) and World.WarmUp. For each tick: its time, the sectors built in it and its phases; the worst ticks listed.
//
//   dotnet run -c Release --project src/SimRun -- -zuberec-bench <game>/client/data/maps/zuberec_mini
//       -town <game>/client/data/towns/zuberec_mini.json -scenes battle_inf [-maxticks 2400] [-route-warm off]
using System.Diagnostics;
using System.Globalization;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimRun;

internal static partial class ZuberecBench
{
    /// <summary>-route-warm off: the world as before ROUTE-WARM (no sectors at load, the warm-up only on quiet ticks); full: every sector at load.</summary>
    internal static string RouteWarm = "on";

    /// <summary>-timeline FILE (BATTLE-PERF): a CSV row a tick of battle_inf: its ms, every phase's and fight section's ms, the counts.</summary>
    internal static string? Timeline;

    private sealed class SectionTimer : ISectionProbe
    {
        public readonly double[] Ms = new double[Enum.GetValues<StepSection>().Length];
        public readonly int[] Calls = new int[Enum.GetValues<StepSection>().Length];

        public long Start() => Stopwatch.GetTimestamp();

        public void Stop(StepSection section, long start)
        {
            Ms[(int)section] += (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            Calls[(int)section]++;
        }

        public void Clear()
        {
            Array.Clear(Ms);
            Array.Clear(Calls);
        }
    }

    // TimelineHeader / TimelineRow: the -timeline CSV (BATTLE-PERF).
    private static string TimelineHeader() => "tick,ms," + string.Join(",", Enum.GetNames<StepPhase>().Select(n => "p_" + n)) + "," +
        string.Join(",", Enum.GetNames<StepSection>().Select(n => "s_" + n)) + ",n_positions,men,inContact,alert,shots,sights,searches,paths,soldierThinks,squadThinks,gc0,gc1,gc2,allocKb";

    private static string TimelineRow(World w, double ms, TickProbe probe, SectionTimer sec, long sights, long searches, long thinks, long squadThinks,
        int gc0, int gc1, int gc2, long allocKb)
    {
        int men = 0, contact = 0, alert = 0;
        foreach (var u in w.Units)
        {
            if (u.Alive && u.Fight is { } f)
            {
                men++;
                contact += f.InContact ? 1 : 0;
                alert += f.Alert ? 1 : 0;
            }
        }
        var inv = CultureInfo.InvariantCulture;
        return string.Create(inv, $"{w.Tick},{ms:0.000},") + string.Join(",", probe.Ms.Select(x => x.ToString("0.000", inv))) + "," +
            string.Join(",", sec.Ms.Select(x => x.ToString("0.000", inv))) +
            string.Create(inv, $",{sec.Calls[(int)StepSection.Positions]},{men},{contact},{alert},{w.ShotEvents.Count},{sights},{searches},{w.PathRequests},{thinks},{squadThinks},{gc0},{gc1},{gc2},{allocKb}");
    }

    private enum IbOrder { AttackMove, Hold }

    private readonly record struct IbGroup(byte Side, Cell At, int Share, IbOrder Order, Cell To, Cell Threat = default);

    // Demo.Battle.cs's IbGroups (blue north, town, slope, centre, south, far south; red north, town, centre, south, flank).
    private static readonly IbGroup[] IbGroups =
    [
        new(1, new Cell(545, 125), 5, IbOrder.AttackMove, new Cell(680, 205)),
        new(1, new Cell(560, 235), 4, IbOrder.Hold, new Cell(592, 250), new Cell(645, 258)),
        new(1, new Cell(550, 345), 5, IbOrder.Hold, new Cell(566, 380), new Cell(640, 405)),
        new(1, new Cell(575, 470), 6, IbOrder.AttackMove, new Cell(680, 430)),
        new(1, new Cell(545, 600), 5, IbOrder.AttackMove, new Cell(680, 565)),
        new(1, new Cell(550, 725), 5, IbOrder.AttackMove, new Cell(680, 690)),
        new(2, new Cell(670, 180), 6, IbOrder.AttackMove, new Cell(545, 140)),
        new(2, new Cell(665, 285), 5, IbOrder.AttackMove, new Cell(588, 252)),
        new(2, new Cell(655, 405), 7, IbOrder.AttackMove, new Cell(560, 385)),
        new(2, new Cell(660, 540), 6, IbOrder.AttackMove, new Cell(550, 610)),
        new(2, new Cell(705, 670), 6, IbOrder.AttackMove, new Cell(520, 790)),
    ];

    private static readonly string[] IbKinds = ["Rifleman", "Rifleman", "Gunner", "Rifleman", "Tank hunter", "Rifleman", "Rifleman", "Marksman"];

    private static Dictionary<string, object> BattleInf(ContentSet content, string contentDir, TerrainMap map, Town town, uint ticks, bool fog)
    {
        var setup = Replay.LoadSetup(Path.Combine(contentDir, "..", "setups", "battle.json")).For(content, 42) with { Fog = fog };
        setup = setup with
        {
            Scenario = Scenario.SandboxName,
            Players = [.. setup.Players.Select(p => p with { Army = [.. p.Army.Where(a => a.Driver is null && a.Unit == "Rifleman").Take(1).Select(a => a with { Count = 1 })] })],
        };
        var sw = Stopwatch.StartNew();
        var w = Replay.NewWorld(content, setup, map);
        var worldMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        w.PrepareRouting(everySector: RouteWarm == "full");
        var lightMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        w.WarmUp(); // as the game's SimSession after routing: the orders' and the fight's code compiled at load
        var jitMs = sw.Elapsed.TotalMilliseconds;
        var res = new Dictionary<string, object>
        {
            ["routeWarm"] = RouteWarm,
            ["worldMs"] = Math.Round(worldMs, 1),
            ["prepareLightMs"] = Math.Round(lightMs, 1),
            ["warmUpMs"] = Math.Round(jitMs, 1),
        };
        var p2 = w.Match.Players[1].Id;
        foreach (var b in town.Buildings)
        {
            w.Issue(new Command { Tick = w.Tick + World.InputDelay, Player = p2, Kind = CommandKind.SandboxSpawn, Cell = new Cell(b.X, b.Y), Unit = b.Archetype, Facing = b.Facing });
        }
        // The squads: Demo.Battle's IbSpawnSquad (two ranks of four, the second two cells behind), 12 cells apart in a group.
        var squads = new List<(int Group, byte Side, double X, double Y, List<uint> Men)>();
        foreach (var side in new byte[] { 1, 2 })
        {
            foreach (var (g, gi) in IbGroups.Select((g, i) => (g, i)).Where(p => p.g.Side == side))
            {
                for (var s = 0; s < g.Share; s++)
                {
                    var at = new Cell(g.At.X, g.At.Y + (s * 12));
                    var back = side == 1 ? -2 : 2;
                    for (var i = 0; i < IbKinds.Length; i++)
                    {
                        w.Issue(new Command { Tick = w.Tick + World.InputDelay, Player = side, Kind = CommandKind.SandboxSpawn, Cell = new Cell(at.X + ((i / 4) * back), at.Y + ((i % 4) * 2)), Unit = IbKinds[i], Facing = side == 1 ? Facing.East : Facing.West });
                    }
                    squads.Add((gi, side, at.X + (back / 2.0), at.Y + 3.0, []));
                }
            }
        }
        var probe = new TickProbe();
        var sec = new SectionTimer();
        using var timeline = Timeline is { } tf ? new StreamWriter(tf) : null;
        timeline?.WriteLine(TimelineHeader());
        var total = new List<double>((int)ticks);
        var top = new List<(double Ms, uint Tick, long Sectors, string Phases)>();
        long sectorsInPlay = 0;
        var buildTicks = 0;
        var routing = w.Routing;
        var warmMs = 0.0;
        while (w.Tick < ticks)
        {
            if (w.Tick == World.InputDelay + 1)
            {
                // The units are in (the town, the squads): the game's load ends here, the warm-up's place (World.WarmRouting).
                var t1 = Stopwatch.GetTimestamp();
                if (RouteWarm == "on")
                {
                    // ROUTE-WARM-2: the spots Demo.Battle.cs gives (every group's start, goal and threat), as the town no longer counts
                    w.WarmRouting([.. IbGroups.SelectMany(g => new[] { g.At, g.To, g.Threat == default ? g.To : g.Threat })]);
                }
                warmMs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                res["warmSectors"] = routing.BuildTotals().Sectors;
                w.Probe = probe;
                w.Sections = timeline is null ? null : sec;
            }
            if (w.Tick == 3)
            {
                foreach (var u in w.Units.Where(u => u.Owner is 1 or 2 && w.CanJoinSquad(u)).OrderBy(u => u.Id))
                {
                    double x = u.Pos.X.Raw / (double)Fixed.OneRaw, y = u.Pos.Y.Raw / (double)Fixed.OneRaw;
                    double D((int, byte, double X, double Y, List<uint>) s) => Math.Sqrt(((s.X - x) * (s.X - x)) + ((s.Y - y) * (s.Y - y)));
                    var best = squads.Where(s => s.Side == u.Owner && s.Men.Count < 8 && D(s) <= 10).OrderBy(D).FirstOrDefault();
                    best.Men?.Add(u.Id);
                }
                foreach (var s in squads.Where(s => s.Men.Count >= 2))
                {
                    w.Issue(new Command { Tick = w.Tick + World.InputDelay, Player = s.Side, Kind = CommandKind.FormSquad, Units = [.. s.Men] });
                }
            }
            if (w.Tick == 5)
            {
                for (var gi = 0; gi < IbGroups.Length; gi++)
                {
                    var g = IbGroups[gi];
                    List<uint> men = [.. squads.Where(s => s.Group == gi).SelectMany(s => s.Men)];
                    w.Issue(g.Order == IbOrder.Hold
                        ? new Command { Tick = w.Tick + World.InputDelay, Player = g.Side, Kind = CommandKind.SetSquadMode, Units = men, Cell = g.To, Index = (int)SquadMode.Hold, Cell2 = g.Threat }
                        : new Command { Tick = w.Tick + World.InputDelay, Player = g.Side, Kind = CommandKind.AttackMove, Units = men, Cell = g.To });
                }
            }
            var b0 = routing.BuildTotals().Sectors;
            sec.Clear();
            var (sights0, searches0, thinks0, sq0) = (w.SightChecks, w.PositionSearches, w.CombatStats.Thinks, w.CombatStats.SquadThinks);
            var (gc0, gc1, gc2, alloc0) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetAllocatedBytesForCurrentThread());
            var t0 = Stopwatch.GetTimestamp();
            w.Step();
            var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (timeline is not null && w.Probe is not null)
            {
                timeline.WriteLine(TimelineRow(w, ms, probe, sec, w.SightChecks - sights0, w.PositionSearches - searches0, w.CombatStats.Thinks - thinks0,
                    w.CombatStats.SquadThinks - sq0, GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2,
                    (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 1024));
            }
            var built = routing.BuildTotals().Sectors - b0;
            if (w.Probe is null)
            {
                continue; // the set-up ticks before the load's end
            }
            sectorsInPlay += built;
            buildTicks += built > 0 ? 1 : 0;
            total.Add(ms);
            if (top.Count < 12 || ms > top[^1].Ms)
            {
                top.Add((ms, w.Tick, built, string.Join(", ", probe.Ms.Select((x, k) => (x, k)).Where(p => p.x >= 0.5).OrderByDescending(p => p.x)
                    .Select(p => string.Create(CultureInfo.InvariantCulture, $"{(StepPhase)p.k} {p.x:0.0}")))));
                top = [.. top.OrderByDescending(t => t.Ms).Take(12)];
            }
        }
        w.Probe = null;
        res["warmMs"] = Math.Round(warmMs, 1);
        res["loadRoutingMs"] = Math.Round(lightMs + warmMs, 1);
        res["men"] = string.Join(" / ", new byte[] { 1, 2 }.Select(p => w.Units.Count(u => u.Alive && u.Owner == p && !u.Def.IsStructure)));
        res["squads"] = squads.Count(s => s.Men.Count >= 2);
        res["sectorsBuiltInPlay"] = sectorsInPlay;
        res["ticksBuildingSectors"] = buildTicks;
        res["tickMs"] = Stats(total);
        res["ticksOver8Ms"] = total.Count(x => x > 8);
        res["worstTicks"] = top.Select(t => string.Create(CultureInfo.InvariantCulture, $"t{t.Tick} {t.Ms:0.0} ms, {t.Sectors} sectors: {t.Phases}")).ToList();
        var (allSectors, allWork) = routing.BuildTotals();
        res["sectorsBuilt"] = allSectors;
        res["cellsPerSector"] = allSectors > 0 ? allWork / allSectors : 0;
        res["hash"] = w.Hash().ToString("x16", CultureInfo.InvariantCulture);
        return res;
    }
}
