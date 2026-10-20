using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The ground attack jet (H2, 2026-10-21; the game repo's docs/heavy-air-plan.md, section 1.1): an A-10 type of class L
// built by the main air base's hangar and parked only in its heavy bays; the "attacker" air class (4 AA-soldier hits
// bring it down); 8 heavy bombs (tier 6, 1.5x the 2,000 lb blast) released low over the target in two fire modes
// ("single" and "salvo4": a stick of 4 along its path); Shift-queued aims bombed together when close (a stick's length)
// and one after another when not; AttackLine's bombs spread along a dragged line; its IR missiles and heavy gun fight
// back in the air, but it loses to the air superiority fighter; all of it deterministic.
public class AttackJetTests(ITestOutputHelper output)
{
    private const string Jet = "attack_jet";
    private const long Cell1 = Fixed.OneRaw;

    private static World Sentinel(int n, ulong seed = 1) => Arena.NewSentinel(n, seed);

    // Both players Sentinel (an aircraft against an aircraft), no starting army.
    private static World Duel(int n, ulong seed)
    {
        var content = TestContent.Mvp;
        var setup = TestContent.Sentinel(seed).For(content, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        return new World(new Grid(n, n), MatchCompiler.Compile(content, setup with { Players = [p0, p1] }));
    }

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, Cell? cell2 = null, uint target = 0, bool queue = false, int index = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Cell2 = cell2, Target = target, Queue = queue, Index = index });

    private static double Cells(long raw) => raw / (double)Cell1;

    private static long Dist(Vec a, Vec b) => World.Len3(a.X.Raw - b.X.Raw, a.Y.Raw - b.Y.Raw, 0);

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    // Flies u's strike until its order ends (or max ticks), collecting its releases and impacts.
    private static (List<(uint Tick, BombRelease R)> Releases, List<BombImpact> Impacts) Fly(World w, Unit u, int max = 6000, Func<bool>? until = null)
    {
        var rel = new List<(uint, BombRelease)>();
        var imp = new List<BombImpact>();
        for (var t = 0; t < max; t++)
        {
            w.Step();
            rel.AddRange(w.BombReleases.Where(r => r.Aircraft == u.Id).Select(r => (w.Tick, r)));
            imp.AddRange(w.BombImpacts.Where(i => i.Aircraft == u.Id));
            if (until?.Invoke() == true || (until is null && u.Order.Kind == OrderKind.Idle && w.Projectiles.All(p => p.Bomb is null)))
            {
                break;
            }
        }
        return (rel, imp);
    }

    // ---- Content ----

    [Fact]
    public void TheAttackJetIsData()
    {
        var c = TestContent.Mvp;
        var w = Sentinel(64);
        var d = w.Player(1)!.Setup.Find(c, Jet)!;
        var a = d.Archetype;
        Assert.Equal("Ground attack jet", a.Name);
        Assert.True(a.Tags.HasAll(c.TagsOf("jet", "aircraft")));
        Assert.Equal((AircraftClass.L, 1200, 700), (a.Aircraft!.SizeClass, a.BuildTime, d.MaxHp));
        Assert.Equal(InterceptClass.Attacker, a.Flight!.Class);
        Assert.Equal("attacker", c.AirClasses[^1].Id); // appended at the end
        var g = a.Flight.Wing!;
        var mj = c.Archetype("multirole_jet")!;
        var m = mj.Flight!.Wing!;
        Assert.False(g.HasAfterburner);
        Assert.True(g.Cruise < m.Cruise && g.Military < m.Military, "slower than the multirole");
        Assert.True(g.Radius(g.Cruise) < m.Radius(m.Cruise), "a tighter turn at cruise");
        Assert.True(g.FloorRaw < m.FloorRaw && a.Flight.HeightRaw < mj.Flight.HeightRaw, "low");
        Assert.True(a.Lifetime > mj.Lifetime, "a longer loiter");
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        // The weapons: 8 heavy bombs, the heavy gun, 2 IR missiles.
        var bombs = d.Weapons.Single(x => x.Effect.Delivery is not null).Effect;
        var big = w.Player(1)!.Setup.Find(c, "Multirole 2000 lb")!.Weapons.Single(x => x.Effect.Delivery is not null).Effect;
        Assert.Equal((8, 6), (bombs.Ammo, World.StrikeTier(bombs.Damage))); // the 2,000 lb look
        Assert.Equal((big.Splash.Raw * 3 / 2, big.SplashInner.Raw * 3 / 2), (bombs.Splash.Raw, bombs.SplashInner.Raw)); // 1.5x its blast
        Assert.Equal(["single", "salvo4"], bombs.Delivery!.FireModes.Select(x => x.Id));
        Assert.Equal(["single", "salvo4"], World.FireModesOf(w.Spawn(1, Jet, 30, 30)).Select(x => x.Id));
        Assert.Equal((1, 4), (bombs.Delivery.SalvoOf(0), bombs.Delivery.SalvoOf(1)));
        Assert.Equal(12 * Cell1, bombs.Delivery.GroupRaw);
        Assert.Equal(2, d.Weapons.Single(x => x.Effect.Interceptor is { Seeker: Seeker.Ir }).Effect.Ammo);
        var gun = d.Weapons.Single(x => x.Effect.AirGun is not null).Effect;
        var jetGun = w.Player(1)!.Setup.Find(c, "fighter_jet")!.Weapons.Single(x => x.Effect.AirGun is not null).Effect;
        Assert.True(gun.Damage > jetGun.Damage, "a heavier gun on the ground");
        Assert.True(gun.AirGun!.HitOn(InterceptClass.Fighter) < jetGun.AirGun!.HitOn(InterceptClass.Fighter), "weak against aircraft");
        // Every interceptor has values for it (the owner's toughness: 4 Igla hits).
        Interceptor Ic(string unit) => w.Player(2)!.Setup.Find(c, unit)!.Weapons.Single(x => x.Effect.Interceptor is not null).Effect.Interceptor!;
        var igla = Ic("manpads");
        Assert.Equal(25, igla.HitDamageOn(InterceptClass.Attacker));
        Assert.InRange(igla.Best(InterceptClass.Attacker), 36, 60);
        Assert.Equal(50, Ic("sam_vehicle").HitDamageOn(InterceptClass.Attacker));
        foreach (var x in w.Player(1)!.Setup.Find(c, "fighter_jet")!.Weapons.Where(x => x.Effect.Interceptor is not null))
        {
            Assert.True(x.Effect.Interceptor!.Engages(InterceptClass.Attacker));
            Assert.Equal(100, x.Effect.Interceptor.HitDamageOn(InterceptClass.Attacker)); // a jet's missile hit kills it
        }
        Assert.True(jetGun.AirGun.HitOn(InterceptClass.Attacker) > 0);
        var mg = w.Player(2)!.Setup.Find(c, "technical")!.Weapons.Single(x => x.Effect.AirGun is not null).Effect;
        Assert.True(mg.AirGun!.HitOn(InterceptClass.Attacker) > 0);
        Assert.True(mg.Damage * 20 < d.MaxHp, "the heavy MG only scratches it");
        Assert.Equal("pilot", c.Archetypes[a.Loss!.Eject!.Unit].Id); // a gun kill makes the pilot eject (E10's rule)
    }

    // ---- Toughness ----

    [Fact]
    public void FourIglaHitsBringItDown()
    {
        var w = Sentinel(240);
        var j = w.Spawn(1, Jet, 120, 120);
        j.MutableSalvos[0] = 0; // no flares: every fuse is a straight roll
        for (var i = 0; i < 6; i++)
        {
            w.Spawn(2, "manpads", 112 + 3 * i, 124);
        }
        var hp = new List<int>();
        var bursts = new List<AirBurstKind>();
        for (var t = 0; t < 8000 && j.Alive; t++)
        {
            w.Step();
            foreach (var b in w.AirBursts.Where(b => b.Target == j.Id && b.Kind is AirBurstKind.Hit or AirBurstKind.Kill))
            {
                bursts.Add(b.Kind);
                hp.Add(j.HP);
            }
        }
        output.WriteLine("hits: " + string.Join(", ", bursts.Zip(hp, (k, h) => $"{k} {h}")));
        Assert.False(j.Alive);
        Assert.Equal([AirBurstKind.Hit, AirBurstKind.Hit, AirBurstKind.Hit, AirBurstKind.Kill], bursts);
        Assert.Equal([525, 350, 175, 0], hp); // 25% of its 700 HP a hit
        Assert.Equal(KillCause.Missile, j.KilledBy);
    }

    // ---- Fire modes ----

    [Fact]
    public void Salvo4DropsAStickOf4AlongItsPathCentredOnThePoint()
    {
        var w = Sentinel(320);
        var j = w.Spawn(1, Jet, 40, 160, Facing.East);
        w.Step();
        Issue(w, CommandKind.SetFireMode, j, index: 1);
        Issue(w, CommandKind.AttackGround, j, cell: new Cell(160, 160));
        w.Step();
        Assert.Equal(1, j.FireMode);
        var (rel, imp) = Fly(w, j, 3000, () => j.Strike is { Passes: >= 1, Phase: StrikePhase.Extend });
        w.Run(80);
        imp.AddRange(w.BombImpacts.Where(i => i.Aircraft == j.Id));
        var point = new Cell(160, 160).Center();
        output.WriteLine($"releases {rel.Count} at ticks {string.Join(",", rel.Select(r => r.Tick))}");
        foreach (var (t, r) in rel)
        {
            output.WriteLine($"  release at ({F(Cells(r.At.X.Raw))}, {F(Cells(r.At.Y.Raw))}) alt {F(r.Altitude.Raw / 512.0)} m predicted ({F(Cells(r.Predicted.X.Raw))}, {F(Cells(r.Predicted.Y.Raw))})");
        }
        foreach (var i in imp)
        {
            output.WriteLine($"  impact ({F(Cells(i.At.X.Raw))}, {F(Cells(i.At.Y.Raw))}) tier {i.Tier}");
        }
        Assert.Equal(4, rel.Count);
        Assert.Equal(4, imp.Count);
        Assert.All(imp, i => Assert.Equal(6, i.Tier));
        var gaps = imp.Zip(imp.Skip(1), (a, b) => Cells(Dist(a.At, b.At))).ToList();
        output.WriteLine("gaps " + string.Join(", ", gaps.Select(F)));
        Assert.All(gaps, g => Assert.InRange(g, 2, 6)); // 4 cells apart (stickSpacing), give or take a cell of scatter each
        var cx = imp.Average(i => (double)i.At.X.Raw) / Cell1;
        var cy = imp.Average(i => (double)i.At.Y.Raw) / Cell1;
        output.WriteLine($"centre ({F(cx)}, {F(cy)})");
        Assert.InRange(Math.Sqrt((cx - Cells(point.X.Raw)) * (cx - Cells(point.X.Raw)) + (cy - Cells(point.Y.Raw)) * (cy - Cells(point.Y.Raw))), 0, 2.5);
        // Along its path: the stick lies along its heading (east), released over the target, low.
        Assert.True(Math.Abs(Cells(imp[^1].At.X.Raw - imp[0].At.X.Raw)) > 3 * Math.Abs(Cells(imp[^1].At.Y.Raw - imp[0].At.Y.Raw)), "along its path");
        Assert.All(rel, r => Assert.InRange(Cells(Dist(r.R.At, r.R.Predicted)), 3, 14)); // released almost over its impact point
    }

    [Fact]
    public void SingleModeDropsOneBombAPass()
    {
        var w = Sentinel(320);
        var j = w.Spawn(1, Jet, 40, 160, Facing.East);
        w.Step();
        Assert.Equal(0, j.FireMode); // "single" is the default
        Issue(w, CommandKind.AttackGround, j, cell: new Cell(160, 160));
        var (rel, imp) = Fly(w, j, 6000, () => j.Strike is { Passes: >= 3 });
        var passes = rel.Select(r => r.Tick).ToList();
        output.WriteLine("releases at " + string.Join(", ", passes));
        Assert.Equal(3, rel.Count); // one a pass
        Assert.All(passes.Zip(passes.Skip(1), (a, b) => (int)(b - a)), g => Assert.True(g > 5 * World.TicksPerSecond, $"a pass apart ({g} ticks)"));
        var point = new Cell(160, 160).Center();
        Assert.All(imp, i => Assert.InRange(Cells(Dist(i.At, point)), 0, 2.5)); // each on the point
    }

    // ---- Several targets ----

    [Fact]
    public void ShiftQueuedPointsCloseTogetherAreBombedInOnePassTheRestAfter()
    {
        var w = Sentinel(320);
        var j = w.Spawn(1, Jet, 40, 160, Facing.East);
        w.Step();
        Cell a = new(160, 160), b = new(168, 161), c = new(150, 240);
        Issue(w, CommandKind.AttackGround, j, cell: a);
        Issue(w, CommandKind.AttackGround, j, cell: b, queue: true);
        Issue(w, CommandKind.AttackGround, j, cell: c, queue: true);
        w.Step();
        Assert.Equal([a, b, c], j.Aims.Select(x => x.Cell));
        var (rel, imp) = Fly(w, j, 8000, () => j.Strike is { Passes: >= 2 } && w.Projectiles.All(p => p.Bomb is null));
        foreach (var (t, r) in rel)
        {
            output.WriteLine($"release t{t} at ({F(Cells(r.At.X.Raw))}, {F(Cells(r.At.Y.Raw))})");
        }
        foreach (var i in imp)
        {
            output.WriteLine($"  impact ({F(Cells(i.At.X.Raw))}, {F(Cells(i.At.Y.Raw))})");
        }
        Assert.Equal(3, rel.Count);
        Assert.InRange(Cells(Dist(imp[0].At, a.Center())), 0, 2.5); // a and b in one pass (8 cells apart: within a stick's length)
        Assert.InRange(Cells(Dist(imp[1].At, b.Center())), 0, 2.5);
        Assert.InRange((int)(rel[1].Tick - rel[0].Tick), 1, 3 * World.TicksPerSecond);
        Assert.InRange(Cells(Dist(imp[2].At, c.Center())), 0, 2.5); // c, 80 cells away: the next pass
        Assert.True(rel[2].Tick - rel[1].Tick > 5 * World.TicksPerSecond, "c on a pass of its own");
        Assert.Equal(c, j.Order.Cell); // the last one goes on as a plain order
        Assert.Empty(j.Aims);
    }

    [Fact]
    public void AnAttackLineSpreadsTheSalvoEvenlyAlongTheLine()
    {
        var w = Sentinel(320);
        var j = w.Spawn(1, Jet, 40, 200, Facing.East);
        w.Step();
        Issue(w, CommandKind.SetFireMode, j, index: 1);
        Cell from = new(140, 150), to = new(176, 150);
        Issue(w, CommandKind.AttackLine, j, cell: from, cell2: to);
        w.Step();
        Assert.Equal(OrderKind.AttackGround, j.Order.Kind);
        Assert.True(j.Aims is [{ Line: true }]);
        var (rel, imp) = Fly(w, j, 6000);
        foreach (var (t, r) in rel)
        {
            output.WriteLine($"release t{t} at ({F(Cells(r.At.X.Raw))}, {F(Cells(r.At.Y.Raw))}) v ({r.VelX}, {r.VelY})");
        }
        foreach (var i in imp)
        {
            output.WriteLine($"  impact ({F(Cells(i.At.X.Raw))}, {F(Cells(i.At.Y.Raw))})");
        }
        Assert.Equal(4, rel.Count); // one pass
        for (var i = 0; i < 4; i++)
        {
            var want = new Vec(Fixed.FromRaw(from.Center().X.Raw + (to.Center().X.Raw - from.Center().X.Raw) * i / 3), from.Center().Y);
            Assert.InRange(Cells(Dist(imp[i].At, want)), 0, 2.5); // 0, 12, 24 and 36 cells along it
        }
        Assert.All(rel, r => Assert.True(r.R.VelX > 10 * Math.Abs(r.R.VelY), "the run aligned with the line, flown from its start"));
        Assert.Equal(OrderKind.Idle, j.Order.Kind); // the line bombed: the order is done
        Assert.Null(j.Strike);
        Assert.Empty(j.Aims);
    }

    // ---- Air-to-air ----

    // A duel from (ax, ay) heading east against an air superiority fighter at (fx, fy) heading west, both ordered at the other:
    // who is left (true: the attack jet down, the fighter up) and the attack jet's own shots.
    private (bool FighterWon, int Shots, string Log) Dogfight(ulong seed, int ax, int fx)
    {
        var w = Duel(320, seed);
        var a = w.Spawn(1, Jet, ax, 160, Facing.East);
        var f = w.Spawn(2, "fighter_jet", fx, 165, Facing.West);
        w.Step();
        Issue(w, CommandKind.Attack, a, target: f.Id);
        Issue(w, CommandKind.Attack, f, target: a.Id);
        int mine = 0, hits = 0, theirs = 0, t = 0;
        for (; t < 6000 && a.Alive && f.Alive; t++)
        {
            w.Step();
            mine += w.AirLaunches.Count(l => l.Shooter == a.Id) + w.GunBursts.Count(g => g.Shooter == a.Id);
            hits += w.GunBursts.Count(g => g.Shooter == a.Id && g.Hit) + w.AirBursts.Count(b => b.Shooter == a.Id && b.Kind != AirBurstKind.Miss);
            theirs += w.AirLaunches.Count(l => l.Shooter == f.Id) + w.GunBursts.Count(g => g.Shooter == f.Id);
        }
        var log = $"seed {seed}: attack jet {(a.Alive ? "up" : $"down ({a.KilledBy})")} HP {a.HP}, fighter {(f.Alive ? "up" : $"down ({f.KilledBy})")} HP {f.HP}, " +
            $"its shots {mine} (hits {hits}), the fighter's {theirs}, {t / 20} s";
        return (!a.Alive && f.Alive, mine, log);
    }

    [Fact]
    public void ItFightsBackButLosesToTheAirSuperiorityFighter()
    {
        var shots = 0;
        foreach (var seed in new ulong[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            // Met at range (130 cells: the fighter's radar missiles first) and in a merge (40 cells: its own missiles and gun in reach).
            foreach (var (ax, fx) in new[] { (100, 230), (140, 180) })
            {
                var (won, mine, log) = Dogfight(seed, ax, fx);
                output.WriteLine(log);
                shots += mine;
                Assert.True(won, log);
            }
        }
        Assert.True(shots > 0, "it fights back");
    }

    [Theory]
    [InlineData(40, 200)]  // from behind the line's start: straight onto it
    [InlineData(280, 150)] // from beyond its end: out to the entry point, round, and down the line from its start
    public void AnAttackLineIsFlownFromItsStartWhereverItComesFrom(int x, int y)
    {
        var w = Sentinel(320);
        var j = w.Spawn(1, Jet, x, y, x < 160 ? Facing.East : Facing.West);
        w.Step();
        Issue(w, CommandKind.SetFireMode, j, index: 1);
        Cell from = new(140, 160), to = new(176, 160);
        Issue(w, CommandKind.AttackLine, j, cell: from, cell2: to);
        var (rel, imp) = Fly(w, j, 8000);
        output.WriteLine(string.Join("; ", imp.Select(i => $"({F(Cells(i.At.X.Raw))}, {F(Cells(i.At.Y.Raw))})")));
        Assert.Equal(4, imp.Count);
        Assert.All(rel, r => Assert.True(r.R.VelX > 0, "flown from the start toward the end"));
        Assert.InRange(Cells(Dist(imp[0].At, from.Center())), 0, 2.5);
        Assert.InRange(Cells(Dist(imp[^1].At, to.Center())), 0, 2.5);
    }

    [Fact]
    public void ShiftQueuedUnitsAreBombedOneAfterAnotherUntilDeadOrBombed()
    {
        var w = Sentinel(320);
        var j = w.Spawn(1, Jet, 40, 160, Facing.East);
        var t1 = w.Spawn(2, "technical", 160, 160);
        var t2 = w.Spawn(2, "technical", 160, 230); // 70 cells off: a pass of its own
        w.Step();
        Issue(w, CommandKind.Attack, j, target: t1.Id);
        Issue(w, CommandKind.Attack, j, target: t2.Id, queue: true);
        w.Step();
        Assert.Equal([t1.Id, t2.Id], j.Aims.Select(a => a.Target));
        var (_, imp) = Fly(w, j, 8000, () => !t1.Alive && !t2.Alive);
        Assert.False(t1.Alive || t2.Alive, "both technicals destroyed");
        Assert.InRange(Cells(Dist(imp[0].At, new Cell(160, 160).Center())), 0, 3);
        Assert.Contains(imp, i => Cells(Dist(i.At, new Cell(160, 230).Center())) < 3);
    }

    [Fact]
    public void TheMultiroleTakesQueuesAndLinesToo()
    {
        var w = Sentinel(320);
        var m = w.Spawn(1, "Multirole 500 lb", 40, 200, Facing.East);
        w.Step();
        Issue(w, CommandKind.AttackLine, m, cell: new Cell(140, 150), cell2: new Cell(176, 150));
        w.Step();
        Assert.True(m.Aims is [{ Line: true }]); // generic: any aircraft with free-fall bombs
        var (rel, imp) = Fly(w, m, 6000);
        Assert.Single(rel); // no fire modes: its stick of 1, at the line's middle
        Assert.InRange(Cells(Dist(imp[0].At, new Cell(158, 150).Center())), 0, 3);
        Assert.Equal(OrderKind.Idle, m.Order.Kind);
        // Its glide-bomb sibling has no free-fall bombs: refused.
        var f = w.Spawn(1, "fighter_jet", 40, 100, Facing.East);
        w.Step();
        Issue(w, CommandKind.AttackLine, f, cell: new Cell(140, 150), cell2: new Cell(176, 150));
        w.Step();
        Assert.Equal(OrderKind.Idle, f.Order.Kind);
        // SetFireMode skips aircraft without that many modes.
        Issue(w, CommandKind.SetFireMode, m, index: 1);
        w.Step();
        Assert.Equal(0, m.FireMode);
    }

    [Fact]
    public void TwoAttackJetsInAnElementBothBombALine()
    {
        var w = Sentinel(320);
        var a = w.Spawn(1, Jet, 40, 200, Facing.East);
        var b = w.Spawn(1, Jet, 44, 206, Facing.East);
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [a.Id, b.Id], Cell = new Cell(90, 200) }); // an element
        w.Run(100);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetFireMode, Units = [a.Id, b.Id], Index = 1 });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AttackLine, Units = [a.Id, b.Id], Cell = new Cell(150, 150), Cell2 = new Cell(186, 150) });
        var drops = new List<BombRelease>();
        for (var t = 0; t < 9000 && (a.Strike is not null || b.Strike is not null || t < 5); t++)
        {
            w.Step();
            drops.AddRange(w.BombReleases);
        }
        output.WriteLine($"releases: {drops.Count(d => d.Aircraft == a.Id)} and {drops.Count(d => d.Aircraft == b.Id)}");
        Assert.Equal(4, drops.Count(d => d.Aircraft == a.Id));
        Assert.Equal(4, drops.Count(d => d.Aircraft == b.Id));
        Assert.True(a.Strike is null && b.Strike is null, "both done");
    }

    // ---- The main air base ----

    [Fact]
    public void ItIsBuiltAtTheMainAirBaseAndParksOnlyInAHeavyBay()
    {
        var w = Sentinel(360);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var hangar = w.Unit(af.Hangar)!;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [hangar.Id], Unit = Jet });
        w.Step();
        Assert.Equal(1, hangar.ProductionQueue.Count + hangar.Bays.Count(b => b.Def >= 0));
        Unit? j = null;
        for (var t = 0; t < 1400 && j is null; t++)
        {
            w.Step();
            j = w.Units.FirstOrDefault(u => u.Def.Archetype.Id == Jet);
        }
        Assert.NotNull(j);
        Assert.InRange((int)w.Tick, 1200, 1300); // 60 s
        Assert.Equal(FlightPhase.Parked, j!.AirOps!.Phase);
        var st = af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == j.AirOps.Station);
        Assert.Equal(StationKind.Heavy, st.Kind);
        Assert.True(w.StationsFor(af, j.Def.Archetype.Aircraft!).All(s => s.Kind == StationKind.Heavy), "only heavy bays take it");
        // A forward airfield never builds or takes it.
        var fwd = w.SpawnAirfield(w.Player(1)!.Setup.Find(w.Content, "airfield_facility")!, new Cell(60, 300), 0, [(0, 0)])!;
        Assert.Empty(w.StationsFor(fwd, j.Def.Archetype.Aircraft!));
        var fac = w.Unit(fwd.Facility)!;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [fac.Id], Unit = Jet });
        w.Step();
        Assert.Empty(fac.ProductionQueue);
        // Ordered out and back, it lands on the base's runway and parks in a heavy bay again.
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [j.Id], Cell = new Cell(200, 260) });
        for (var t = 0; t < 4000 && j.AirOps is not null; t++)
        {
            w.Step();
        }
        Assert.Null(j.AirOps); // taxied out, took off on military power (no afterburner), flying free
        w.Run(200);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.ReturnToBase, Units = [j.Id], Target = af.Facility });
        var parked = false;
        for (var t = 0; t < 9000 && !parked; t++)
        {
            w.Step();
            parked = j.AirOps is { Phase: FlightPhase.Parked };
        }
        Assert.True(parked, "it lands and parks");
        Assert.Equal(af.Id, j.AirOps!.Airfield);
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == j.AirOps.Station).Kind);
    }

    // ---- Determinism ----

    // A scripted strike on both sides (a Sentinel duel world): player 1's jet a salvo on a queue of a point and a technical,
    // player 2's a line; the commands issued in order or reversed (the engine orders them by player and sequence); the
    // world's hash every tick.
    private static List<ulong> Script(bool reversed)
    {
        var w = Duel(320, 7);
        var j1 = w.Spawn(1, Jet, 40, 200, Facing.East);
        var j2 = w.Spawn(2, Jet, 280, 120, Facing.West);
        var tech = w.Spawn(2, "technical", 170, 200);
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.SetFireMode, Units = [j1.Id], Index = 1 },
            new Command { Tick = 1, Player = 1, Seq = 2, Kind = CommandKind.AttackGround, Units = [j1.Id], Cell = new Cell(160, 200) },
            new Command { Tick = 1, Player = 1, Seq = 3, Kind = CommandKind.Attack, Units = [j1.Id], Target = tech.Id, Queue = true },
            new Command { Tick = 1, Player = 2, Seq = 1, Kind = CommandKind.SetFireMode, Units = [j2.Id], Index = 1 },
            new Command { Tick = 1, Player = 2, Seq = 2, Kind = CommandKind.AttackLine, Units = [j2.Id], Cell = new Cell(150, 120), Cell2 = new Cell(110, 130) },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        for (var t = 0; t < 1500; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
        }
        Assert.False(tech.Alive, "the queue's second aim, the technical, is destroyed");
        Assert.True(j1.Strike is null && j2.Strike is null, "both strikes done");
        return hashes;
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var a = Script(false);
        Assert.Equal(a, Script(false));
        Assert.Equal(a, Script(true));
    }
}
