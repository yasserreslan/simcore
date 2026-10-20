using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// E13: the attack-run state machine (Ingress, Align, Run, Break, Extend, Reposition, Egress) and its pass gap,
// pursuit separation (no fly-through, gun kills still happen), elements (formation slots off the lead's path,
// staggered runs, a common break), the break with flares against MANPADS, never dropping a player's attack order
// silently (D-ORD1), and flight heights by map size (D-ALT1).
public class AttackRunTests(ITestOutputHelper output)
{
    private const long Cell1 = Fixed.OneRaw;

    private static World Sentinel(int n, ulong seed = 1, bool fog = false) =>
        Arena.NewSentinel(n, seed, fog ? s => s with { Fog = true } : null);

    // Both players Sentinel (an aircraft against an aircraft), no starting army.
    private static World Duel(int n, ulong seed = 1)
    {
        var content = TestContent.Mvp;
        var setup = TestContent.Sentinel(seed).For(content, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        return new World(new Grid(n, n), MatchCompiler.Compile(content, setup with { Players = [p0, p1] }));
    }

    private static Unit Jet(World w, string preset, int x, int y, byte player = 1, int facing = Facing.East) =>
        w.Spawn(player, preset, x, y, facing);

    private static void NoMissiles(Unit u)
    {
        foreach (var wd in u.Def.Weapons)
        {
            if (wd.Effect.Interceptor is not null && wd.Stock >= 0)
            {
                u.MutableCharges[wd.Stock] = 0;
            }
        }
    }

    private static long Flat(Vec a, Vec b) => World.Len3(a.X.Raw - b.X.Raw, a.Y.Raw - b.Y.Raw, 0);

    // ---- The state machine and the pass gap ----

    private static readonly (StrikePhase From, StrikePhase To)[] Allowed =
    [
        (StrikePhase.Ingress, StrikePhase.Align), (StrikePhase.Ingress, StrikePhase.Extend),
        (StrikePhase.Align, StrikePhase.Run), (StrikePhase.Align, StrikePhase.Break),
        (StrikePhase.Run, StrikePhase.Break),
        (StrikePhase.Break, StrikePhase.Extend), (StrikePhase.Break, StrikePhase.Egress),
        (StrikePhase.Extend, StrikePhase.Reposition), (StrikePhase.Extend, StrikePhase.Egress),
        (StrikePhase.Reposition, StrikePhase.Align), (StrikePhase.Reposition, StrikePhase.Extend),
    ];

    [Fact]
    public void AStrikeFliesTheAttackRunStateMachineWithAPassGapOf12To25Seconds()
    {
        var w = Sentinel(320);
        var m = Jet(w, "Multirole 500 lb", 40, 160);
        w.Step();
        var point = new Cell(160, 160);
        w.Order(1, CommandKind.AttackGround, [m], cell: point);
        w.Step();
        var phases = new List<StrikePhase>();
        var releases = new List<uint>();
        var locks = new List<long>();
        var runStartCross = new List<long>();
        var alignAlt = new List<(long Start, long End)>();
        long alignStart = 0, extendStartAlt = 0, extendMaxClimb = 0;
        for (var t = 0; t < 6000 && m.Order.Kind == OrderKind.AttackGround; t++)
        {
            w.Step();
            releases.AddRange(w.BombReleases.Select(_ => w.Tick));
            if (m.Strike is not { } s)
            {
                continue;
            }
            if (phases.Count == 0 || phases[^1] != s.Phase)
            {
                if (s.Phase == StrikePhase.Align)
                {
                    locks.Add(Flat(m.Pos, s.Aim));
                    alignStart = m.Altitude.Raw;
                }
                if (phases.Count > 0 && phases[^1] == StrikePhase.Align)
                {
                    alignAlt.Add((alignStart, m.Altitude.Raw));
                }
                if (s.Phase == StrikePhase.Run)
                {
                    // Off the locked run line as the run begins (raw).
                    runStartCross.Add(Math.Abs(((m.Pos.X.Raw - s.Aim.X.Raw) * s.DirY - (m.Pos.Y.Raw - s.Aim.Y.Raw) * s.DirX) / 1000));
                }
                if (s.Phase == StrikePhase.Extend)
                {
                    extendStartAlt = m.Altitude.Raw;
                }
                phases.Add(s.Phase);
            }
            if (s.Phase == StrikePhase.Extend)
            {
                extendMaxClimb = Math.Max(extendMaxClimb, m.Altitude.Raw - extendStartAlt);
            }
        }
        output.WriteLine("phases " + string.Join(" ", phases));
        Assert.Equal(OrderKind.Idle, m.Order.Kind); // bombs, then the gun, then the egress: the strike ends
        Assert.Equal(4, releases.Count);
        for (var i = 1; i < phases.Count; i++)
        {
            Assert.Contains((phases[i - 1], phases[i]), Allowed);
        }
        Assert.Equal(StrikePhase.Egress, phases[^1]);
        var gaps = releases.Zip(releases.Skip(1), (a, b) => (int)(b - a)).ToList();
        output.WriteLine($"pass gaps (s): {string.Join(", ", gaps.Select(g => (g / 20.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}");
        Assert.All(gaps, g => Assert.InRange(g, 12 * World.TicksPerSecond, 25 * World.TicksPerSecond));
        // Every run line is locked well out (the turn-in behind it), and the run begins on it.
        output.WriteLine($"lines locked at {string.Join(", ", locks.Select(l => l / Cell1))} cells; run starts off the line by {string.Join(", ", runStartCross.Select(c => c * 10 / Cell1))} tenths");
        Assert.All(locks, l => Assert.True(l >= 70 * Cell1, $"locked {l / Cell1} cells out"));
        Assert.All(runStartCross, c => Assert.True(c <= 2 * Cell1, $"{c * 10 / Cell1} tenths off the line"));
        // D-ALT1: it comes down from cruise to its run on the align (the dive's roll-in) and climbs back on the extend.
        Assert.Contains(alignAlt, a => a.End < a.Start - 20 * 512);
        Assert.True(extendMaxClimb > 30 * 512, $"climbs {extendMaxClimb / 512} m on an extend");
    }

    // ---- Pursuit separation (row 67, D-PUR1) ----

    [Theory]
    [InlineData("watcher_drone", "fighter_jet")]
    [InlineData("watcher_drone", "Multirole 500 lb")]
    [InlineData("overseer_drone", "fighter_jet")]
    public void AGunOnlyJetNeverFliesThroughItsTargetAndStillGunsItDown(string prey, string jet)
    {
        var kills = 0;
        long worst = long.MaxValue;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var w = Duel(240, seed);
            var f = Jet(w, jet, 60, 120);
            NoMissiles(f);
            var t = w.Spawn(2, prey, 150, 120, Facing.East);
            w.Step();
            w.Order(1, CommandKind.Attack, [f], target: t.Id);
            var min = long.MaxValue;
            var bursts = 0;
            for (var k = 0; k < 2400 && t.Alive && f.Alive; k++)
            {
                w.Step();
                bursts += w.GunBursts.Count(b => b.Shooter == f.Id);
                if (t.Alive)
                {
                    min = Math.Min(min, World.Len3(t.Pos.X.Raw - f.Pos.X.Raw, t.Pos.Y.Raw - f.Pos.Y.Raw, t.Altitude.Raw - f.Altitude.Raw));
                }
            }
            kills += !t.Alive && t.KilledBy == KillCause.Gun ? 1 : 0;
            worst = Math.Min(worst, min);
            Assert.True(bursts > 0, "it gets its gun on the target");
        }
        output.WriteLine($"{prey} vs {jet}: closest {worst * 100 / Cell1 / 100.0} cells, gun kills {kills}/5");
        Assert.True(worst > 4 * Cell1, $"closest approach {worst * 10 / Cell1} tenths (fly-through before E13: 0.4 cells)"); // the brief's bar: 1.5
        Assert.True(kills >= 3, $"{kills} gun kills of 5");
    }

    // ---- Elements ----

    [Fact]
    public void JetsGivenTheSameOrderFormElementsOfAtMostFour()
    {
        var w = Sentinel(240);
        var jets = Enumerable.Range(0, 6).Select(i => Jet(w, "Multirole 500 lb", 60, 60 + i * 12)).ToList();
        var f = Jet(w, "fighter_jet", 60, 150);
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [.. jets.Select(j => j.Id), f.Id], Mission = AirTaskKind.Patrol, Cell = new Cell(120, 120) });
        w.Step();
        // Six multiroles: two elements of three (the lowest IDs lead); the fighter, alone of its kind, flies on its own.
        Assert.Equal([null, (jets[0].Id, 1), (jets[0].Id, 2), null, (jets[3].Id, 1), (jets[3].Id, 2)],
            jets.Select(j => j.Wingman is { } e ? (e.Lead, e.Slot) : ((uint, int)?)null));
        Assert.Null(f.Wingman);
        // A new order to one of them takes it out of its element; Shift-appending a waypoint to all keeps them.
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [.. jets.Select(j => j.Id)], Mission = AirTaskKind.Patrol, Cell = new Cell(180, 120), Queue = true });
        w.Step();
        Assert.Equal(jets[0].Id, jets[1].Wingman?.Lead);
        w.Order(1, CommandKind.Move, [jets[2]], cell: new Cell(30, 30));
        w.Step();
        Assert.Null(jets[2].Wingman);
        // The lead lost: the lowest ID of its wingmen leads the rest.
        w.Order(1, CommandKind.Move, [jets[3], jets[4], jets[5]], cell: new Cell(200, 200));
        w.Step();
        jets[3].HP = 0;
        w.Run(3);
        Assert.Null(jets[4].Wingman);
        Assert.Equal((jets[4].Id, 1), jets[5].Wingman is { } e5 ? (e5.Lead, e5.Slot) : default);
    }

    [Fact]
    public void AWingmanHoldsItsSlotOffTheLeadsPathThroughTurns()
    {
        var w = Sentinel(320);
        var a = Jet(w, "Multirole 500 lb", 100, 160);
        var b = Jet(w, "Multirole 500 lb", 95, 170);
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [a.Id, b.Id], Mission = AirTaskKind.Patrol, Cell = new Cell(100, 160) });
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [a.Id, b.Id], Mission = AirTaskKind.Patrol, Cell = new Cell(240, 160), Queue = true });
        Assert.Equal((a.Id, 1), b.Wingman is { } e0 ? (e0.Lead, e0.Slot) : default);
        long straightMax = 0, turnMax = 0, sum = 0, n = 0;
        var aG = a.Def.Archetype.Flight!.Wing!.TurnAccel;
        long minSep = long.MaxValue;
        for (var t = 0; t < 2000; t++)
        {
            w.Step();
            minSep = Math.Min(minSep, Flat(a.Pos, b.Pos));
            if (t < 400 || b.Wingman is not { Following: true } e)
            {
                continue; // settling into the slot first
            }
            var turning = Math.Abs(a.Wing!.Lateral) > aG / 4;
            if (turning)
            {
                turnMax = Math.Max(turnMax, e.SlotError);
            }
            else
            {
                straightMax = Math.Max(straightMax, e.SlotError);
            }
            (sum, n) = (sum + e.SlotError, n + 1);
        }
        output.WriteLine($"slot error: mean {sum / Math.Max(1, n) * 10 / Cell1} tenths, max {straightMax * 10 / Cell1} straight, {turnMax * 10 / Cell1} turning; closest to its lead {minSep * 10 / Cell1} tenths");
        Assert.True(n > 1000, "it flies its slot");
        Assert.True(sum / n <= 8 * Cell1, $"mean slot error {sum / n * 10 / Cell1} tenths");
        Assert.True(turnMax <= 25 * Cell1, $"turning {turnMax * 10 / Cell1} tenths");
        Assert.True(minSep >= 4 * Cell1, $"closest {minSep * 10 / Cell1} tenths: separation holds");
    }

    [Fact]
    public void AnElementStaggersItsRunsAndBreaksTogether()
    {
        var w = Sentinel(320);
        var a = Jet(w, "Multirole 500 lb", 40, 160);
        var b = Jet(w, "Multirole 500 lb", 35, 170);
        w.Step();
        w.Order(1, CommandKind.AttackGround, [a, b], cell: new Cell(240, 200));
        var rel = new List<(uint Tick, uint Aircraft)>();
        var sideMatch = true;
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            rel.AddRange(w.BombReleases.Select(r => (w.Tick, r.Aircraft)));
            if (a.Strike is { Side: not 0 } sa && b.Strike is { Side: not 0 } sb)
            {
                sideMatch &= sa.Side == sb.Side;
            }
        }
        output.WriteLine("releases " + string.Join(", ", rel.Select(r => $"{r.Tick}:{(r.Aircraft == a.Id ? "A" : "B")}")));
        var la = rel.Where(r => r.Aircraft == a.Id).Select(r => r.Tick).ToList();
        var lb = rel.Where(r => r.Aircraft == b.Id).Select(r => r.Tick).ToList();
        Assert.Equal(4, la.Count);
        Assert.Equal(4, lb.Count);
        foreach (var tb in lb)
        {
            var lag = (int)(tb - la.Where(x => x < tb).DefaultIfEmpty(0u).Max());
            Assert.InRange(lag, 2 * World.TicksPerSecond, 5 * World.TicksPerSecond); // a stagger of 3 s (60 ticks) behind its lead's
        }
        Assert.True(sideMatch, "the wingman breaks the lead's way");
        Assert.Equal(a.Id, b.Wingman?.Lead);
    }

    // ---- The break with flares against MANPADS (row 64, D-BRK1) ----

    private static readonly Lazy<ContentSet> NoEvade = new(() =>
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(c => c.Name.EndsWith("archetypes/sentinel.json", StringComparison.Ordinal));
        sources[i] = new ContentSource(sources[i].Name, sources[i].Json.Replace("\"evade\": { \"breakG\": 100, \"warn\": 60, \"maxTicks\": 50 },", "", StringComparison.Ordinal));
        return ContentLoader.Load(sources);
    });

    private static (int KillPermille, int Salvos, uint SalvoTick, uint Launch) Igla(ContentSet content, int x, int y)
    {
        var setup = TestContent.Sentinel(1).For(content, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = new World(new Grid(240, 240), MatchCompiler.Compile(content, setup));
        var j = w.Spawn(1, "Multirole 500 lb", x, y, Facing.East);
        w.Spawn(2, "manpads", 120, 120);
        Projectile? missile = null;
        int? pk = null;
        var salvos = 0;
        uint salvoTick = 0, launch = 0;
        for (var t = 0; t < 1200 && pk is null; t++)
        {
            w.Step();
            if (missile is null && w.Projectiles.FirstOrDefault(p => p.Weapon.Effect.Interceptor is not null && p.Target == j.Id) is { } m0)
            {
                (missile, launch) = (m0, w.Tick);
            }
            if (w.Countermeasures.Count > 0 && salvoTick == 0)
            {
                salvoTick = w.Tick;
            }
            salvos += w.Countermeasures.Count;
            if (missile is not null && w.AirBursts.FirstOrDefault(b => b.Missile == missile.Id) is { Missile: not 0 } b)
            {
                pk = b.KillPermille;
            }
        }
        Assert.NotNull(pk);
        return (pk!.Value, salvos, salvoTick, launch);
    }

    [Theory]
    [InlineData(140, 120)]
    [InlineData(150, 110)]
    [InlineData(130, 140)]
    [InlineData(160, 125)]
    public void BreakingWithFlaresBeatsFlyingStraightAgainstAnIgla(int x, int y)
    {
        var breaks = Igla(TestContent.Mvp, x, y);
        var straight = Igla(NoEvade.Value, x, y);
        output.WriteLine($"kill chance: break + flares {breaks.KillPermille / 10.0}%, straight + flares {straight.KillPermille / 10.0}%");
        Assert.Equal((1, 1), (breaks.Salvos, straight.Salvos));
        Assert.True(breaks.SalvoTick < straight.SalvoTick || breaks.Launch != straight.Launch, "breaking, it dispenses at the launch warning");
        Assert.True(breaks.KillPermille * 4 <= straight.KillPermille * 3, $"break {breaks.KillPermille} vs straight {straight.KillPermille} permille: at least a quarter lower");
    }

    // ---- D-ORD1: never drop a player's attack order silently ----

    [Fact]
    public void AnAttackAtAnUnseenTargetFliesToItsLastKnownCellAndStrikesOnceSeen()
    {
        var w = Sentinel(240, fog: true);
        var m = Jet(w, "Multirole 500 lb", 40, 120);
        var depot = w.Spawn(2, "supply_depot", 180, 120);
        w.Step();
        Assert.False(World.IsVisibleTo(depot, 1), "fogged");
        w.Order(1, CommandKind.Attack, [m], target: depot.Id);
        w.Step();
        Assert.Equal((OrderKind.Attack, depot.Id), (m.Order.Kind, m.Order.Target)); // taken, not dropped
        var hp = depot.HP;
        var seenAt = RunUntil(w, () => World.IsVisibleTo(depot, 1), 1200);
        Assert.True(seenAt > 0, "it flew there and its own eyes found it");
        Assert.True(RunUntil(w, () => depot.HP < hp, 2400) > 0, "then it struck");
        Assert.Empty(w.OrderRefusals);
    }

    [Fact]
    public void ATargetNotFoundAtItsLastKnownCellIsGivenUpWithAReason()
    {
        var w = Sentinel(240, fog: true);
        var m = Jet(w, "Multirole 500 lb", 40, 120);
        var truck = w.SpawnCrewed(2, "technical", 180, 120).Vehicle;
        w.Step();
        // Ordered at it with a click 60 cells away from where it is: nothing to see there.
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Attack, Units = [m.Id], Target = truck.Id, Cell = new Cell(120, 40) });
        var refusals = new List<OrderRefusal>();
        Assert.True(RunUntil(w, () =>
        {
            refusals.AddRange(w.OrderRefusals);
            return m.Order.Kind == OrderKind.Idle;
        }, 2400) > 0, "it gives up");
        Assert.Equal([OrderRefusalReason.Unseen, OrderRefusalReason.TargetLost], refusals.Select(r => r.Reason)); // taken (a note), then given up
        Assert.All(refusals, r => Assert.Equal((m.Id, truck.Id), (r.Aircraft, r.Target)));
    }

    [Fact]
    public void WithoutAmmunitionOrOnARefuelTripTheOrderIsRefusedWithAReason()
    {
        var w = Sentinel(240);
        var m = Jet(w, "Multirole 500 lb", 60, 120);
        var truck = w.SpawnCrewed(2, "technical", 150, 120).Vehicle;
        w.Step();
        foreach (var wd in m.Def.Weapons.Where(x => x.Stock >= 0))
        {
            m.MutableCharges[wd.Stock] = 0;
        }
        w.Order(1, CommandKind.Attack, [m], target: truck.Id);
        w.Step();
        Assert.Equal(OrderKind.Idle, m.Order.Kind);
        Assert.Equal((m.Id, OrderRefusalReason.NoAmmo), (w.OrderRefusals.Single().Aircraft, w.OrderRefusals.Single().Reason));
        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(150, 150));
        w.Step();
        Assert.Equal(OrderRefusalReason.NoAmmo, w.OrderRefusals.Single().Reason);

        // Only its missiles left (air-to-air): still no ammunition for the ground (it rearms only on a Rearm order).
        var ir = m.Def.Weapons.First(x => x.Effect.Interceptor is not null);
        m.MutableCharges[ir.Stock] = 2;
        w.Order(1, CommandKind.Attack, [m], target: truck.Id);
        w.Step();
        Assert.Equal(OrderRefusalReason.NoAmmo, w.OrderRefusals.Single().Reason);

        // On its automatic trip home to refuel (bingo), an attack order is refused, not lost.
        var w2 = Sentinel(240);
        AirfieldFlightTests.Field(w2, [(0, 0)], at: new Cell(30, 170), east: 1);
        var j = Jet(w2, "Multirole 500 lb", 120, 110);
        var t2 = w2.SpawnCrewed(2, "technical", 200, 110).Vehicle;
        w2.Step();
        j.Lifetime = 800;
        Assert.True(RunUntil(w2, () => j.AirOps is not null, 100) > 0, "bingo: home to refuel");
        output.WriteLine($"phase {j.AirOps!.Phase}, reason {j.HomeReason}, homing {j.Homing}");
        w2.Order(1, CommandKind.Attack, [j], target: t2.Id);
        w2.Step();
        Assert.Equal((j.Id, OrderRefusalReason.RefuelTrip), (w2.OrderRefusals.Single().Aircraft, w2.OrderRefusals.Single().Reason));
    }

    [Fact]
    public void WithItsBombsGoneItAttacksWithTheGunAndAPlayersOrderOverridesGuardsPick()
    {
        var w = Sentinel(240);
        var m = Jet(w, "Multirole 500 lb", 60, 120);
        var near = w.Spawn(2, "supply_depot", 120, 120);
        var ordered = w.Spawn(2, "supply_depot", 170, 150);
        near.HP = ordered.HP = 100_000;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [m.Id], Mission = AirTaskKind.Guard, Cell = new Cell(80, 80), Cell2 = new Cell(200, 200) });
        w.Step();
        foreach (var wd in m.Def.Weapons.Where(x => x.Effect.Delivery is not null))
        {
            m.MutableCharges[wd.Stock] = 0;
        }
        w.Order(1, CommandKind.Attack, [m], target: ordered.Id);
        var strafes = new List<StrafeBurst>();
        for (var t = 0; t < 1500 && strafes.Count < 3; t++)
        {
            w.Step();
            strafes.AddRange(w.StrafeBursts);
            Assert.Equal(ordered.Id, m.Order.Target); // Guard's own pick waits until the order is done
        }
        Assert.NotEmpty(strafes);
        Assert.All(strafes, s => Assert.Equal(ordered.Id, s.Target));
        Assert.Empty(w.OrderRefusals);
    }

    // ---- D-ORD2: a jet parked at home without bombs rearms, then attacks ----

    [Theory]
    [InlineData(CommandKind.Attack)]
    [InlineData(CommandKind.AttackGround)]
    public void AParkedJetWithoutBombsGivenAnAttackRearmsLaunchesAndAttacks(CommandKind kind)
    {
        var w = Sentinel(240);
        AirfieldFlightTests.Field(w, [(0, 0)], at: new Cell(40, 170), east: 1);
        var m = Jet(w, "Multirole 500 lb", 120, 120);
        var depot = w.Spawn(2, "supply_depot", 180, 60);
        depot.HP = 100_000;
        w.Step();
        foreach (var wd in m.Def.Weapons.Where(x => x.Stock >= 0 && x.Effect.Interceptor is null))
        {
            m.MutableCharges[wd.Stock] = 0; // back from a sortie: no bombs, no gun
        }
        w.Order(1, CommandKind.ReturnToBase, [m]);
        Assert.True(RunUntil(w, () => m.AirOps is { Phase: FlightPhase.Parked }, 6000) > 0, "home and parked");
        var bombs = m.Def.Weapons.First(x => x.Effect.Delivery is not null);
        Assert.Equal(0, m.Charges[bombs.Stock]);
        if (kind == CommandKind.Attack)
        {
            w.Order(1, CommandKind.Attack, [m], target: depot.Id);
        }
        else
        {
            w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(180, 60));
        }
        var notes = new List<OrderRefusalReason>();
        Assert.True(RunUntil(w, () =>
        {
            notes.AddRange(w.OrderRefusals.Select(r => r.Reason));
            return m.AirOps is null && m.Order.Kind == (kind == CommandKind.Attack ? OrderKind.Attack : OrderKind.AttackGround);
        }, 3000) > 0, "it rearmed, launched and flies free on the order");
        Assert.Equal([OrderRefusalReason.Rearming], notes); // taken with a note: an implicit Rearm (D-ORD2), no silent drop
        Assert.Equal(m.Def.StockMax[bombs.Stock], m.Charges[bombs.Stock]);
        var hp = depot.HP;
        Assert.True(RunUntil(w, () => w.BombReleases.Any(r => r.Aircraft == m.Id), 3000) > 0, "and attacks");
    }

    [Fact]
    public void AnAirborneJetWithoutAnythingForTheGroundRefusesAttackGroundWithNoAmmo()
    {
        var w = Sentinel(240);
        var m = Jet(w, "Multirole 500 lb", 120, 120);
        w.Step();
        foreach (var wd in m.Def.Weapons.Where(x => x.Stock >= 0 && x.Effect.Interceptor is null))
        {
            m.MutableCharges[wd.Stock] = 0;
        }
        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(180, 60));
        w.Step();
        Assert.Equal(OrderRefusalReason.NoAmmo, Assert.Single(w.OrderRefusals).Reason);
        Assert.Null(m.AirOps); // not home: it stays on its task (orbit) and says why
    }

    // ---- D-ALT1: flight heights by map size ----

    [Theory]
    [InlineData(256, 140)]
    [InlineData(768, 210)]
    public void JetsCruiseHigherOnBiggerMaps(int size, int metres)
    {
        var w = Sentinel(size);
        var m = Jet(w, "Multirole 500 lb", 60, 60);
        var f = Jet(w, "fighter_jet", 60, 90);
        w.Order(1, CommandKind.Move, [m, f], cell: new Cell(80, 80));
        w.Run(600);
        Assert.Equal(metres * 512L, w.FlightHeightOf(m));
        Assert.Equal(metres * 512L * 6 / 5, w.FlightHeightOf(f));
        Assert.InRange(m.Altitude.Raw - w.GroundHeight(m.Pos).Raw, (metres - 6) * 512L, (metres + 18) * 512L); // plus its wingman step
    }

    // ---- D-FUEL1: endurance by map size; Bingo refusals ----

    [Theory]
    [InlineData(256, 100)]
    [InlineData(768, 150)]
    [InlineData(1024, 200)]
    [InlineData(1536, 200)]
    public void JetsCarryMoreFuelOnBiggerMaps(int size, int percent)
    {
        var w = Sentinel(size);
        var m = Jet(w, "Multirole 500 lb", 60, 60);
        var watcher = w.Spawn(1, "watcher_drone", 80, 80);
        Assert.Equal(m.Def.Lifetime * percent / 100, m.Tank);
        Assert.Equal(m.Tank, m.Lifetime);
        Assert.Equal(watcher.Def.Lifetime, watcher.Tank); // drones keep theirs
        Assert.Equal(100, w.AirStatusOf(m).FuelPercent);
    }

    [Fact]
    public void AnOrderItsFuelCannotFlyIsRefusedWithBingoAndOneItCanIsTaken()
    {
        var w = Sentinel(1024);
        AirfieldFlightTests.Field(w, [(0, 0)], at: new Cell(80, 500), east: 1);
        var m = Jet(w, "Multirole 500 lb", 200, 500);
        var near = w.Spawn(2, "supply_depot", 320, 500);
        var far = w.Spawn(2, "supply_depot", 950, 500);
        w.Step();
        w.Order(1, CommandKind.Attack, [m], target: far.Id);
        w.Step();
        Assert.Equal((OrderKind.Attack, far.Id), (m.Order.Kind, m.Order.Target)); // a full tank: 750 cells out and home is fine
        Assert.Empty(w.OrderRefusals);
        m.Lifetime = m.Tank / 3; // a third of a tank left: not to the far side and back
        w.Order(1, CommandKind.Attack, [m], target: far.Id);
        w.Step();
        Assert.Equal(OrderRefusalReason.Bingo, Assert.Single(w.OrderRefusals).Reason);
        w.Order(1, CommandKind.Attack, [m], target: near.Id); // ... but to the near depot it can
        w.Step();
        Assert.Empty(w.OrderRefusals);
        Assert.Equal(near.Id, m.Order.Target);
    }

    // ---- D-SPD1: transit speeds by map size; runs keep today's speed ----

    [Fact]
    public void OnABigMapJetsTransitFasterButRunInAtTodaysSpeedAndTheirFlyZoneGrows()
    {
        var small = Sentinel(256);
        var big = Sentinel(1024);
        var ms = Jet(small, "Multirole 500 lb", 60, 60);
        var mb = Jet(big, "Multirole 500 lb", 60, 500);
        var fb = Jet(big, "fighter_jet", 60, 540);
        Assert.Equal(World.FlyZoneMarginOf(ms), small.FlyZoneMarginFor(ms)); // 50 cells, as before
        output.WriteLine($"fly zone margins on 1,024 cells: multirole {big.FlyZoneMarginFor(mb) * 10 / Cell1} tenths, fighter {big.FlyZoneMarginFor(fb) * 10 / Cell1} (content {World.FlyZoneMarginOf(mb) * 10 / Cell1} / {World.FlyZoneMarginOf(fb) * 10 / Cell1})");
        Assert.True(big.FlyZoneMarginFor(mb) > World.FlyZoneMarginOf(mb), "a faster cruise needs a wider band");
        // A crossing: 900 cells east.
        big.Order(1, CommandKind.Move, [mb], cell: new Cell(960, 500));
        var t = RunUntil(big, () => mb.Pos.X.Raw >= 960 * Cell1 - 30 * Cell1, 4000);
        output.WriteLine($"900 cells in {t / 20.0} s at {mb.Wing!.Speed * 10 / Cell1} tenths a second");
        Assert.InRange(mb.Wing!.Speed, 26 * Cell1, 28 * Cell1); // 27 cells/s (20 on a small map)
        Assert.InRange(t, 30 * World.TicksPerSecond, 40 * World.TicksPerSecond);
        // A strike: the run is flown at today's attack speed (23 cells/s), not the transit's.
        big.Order(1, CommandKind.AttackGround, [mb], cell: new Cell(700, 500));
        var runSpeeds = new List<long>();
        for (var k = 0; k < 3000 && runSpeeds.Count < 10; k++)
        {
            big.Step();
            if (mb.Strike is { Phase: StrikePhase.Run })
            {
                runSpeeds.Add(mb.Wing!.Speed);
            }
        }
        Assert.NotEmpty(runSpeeds);
        Assert.All(runSpeeds, s => Assert.InRange(s, 19 * Cell1, 24 * Cell1));
        Assert.Equal(0, big.AirEdgeClamps);
    }

    [Fact]
    public void HighOverABigMapAJetsRadarStillSeesLowDronesBelow()
    {
        // D-ALT1b: at 336 m (168 cells up) the fighter's 160-cell radar measured as slant would see nothing near the
        // ground; airborne sensors measure the level distance.
        var w = Sentinel(1024);
        var f = Jet(w, "fighter_jet", 500, 500);
        var scout = w.Spawn(2, "scout_drone", 600, 500);
        w.Run(200);
        Assert.True(f.Altitude.Raw - scout.Altitude.Raw > 160 * Cell1 / 2, "far above it");
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [f.Id], Mission = AirTaskKind.Guard, Cell = new Cell(450, 450), Cell2 = new Cell(650, 550) });
        Assert.True(RunUntil(w, () => f.AirTarget == scout.Id, 200) > 0, "it picks the drone below");
    }

    private static int RunUntil(World w, Func<bool> done, int max)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            if (done())
            {
                return t;
            }
        }
        return -1;
    }
}
