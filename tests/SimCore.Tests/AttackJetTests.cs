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
            Assert.Equal(50, x.Effect.Interceptor.HitDamageOn(InterceptClass.Attacker));
        }
        Assert.True(jetGun.AirGun.HitOn(InterceptClass.Attacker) > 0);
        var mg = w.Player(2)!.Setup.Find(c, "technical")!.Weapons.Single(x => x.Effect.AirGun is not null).Effect;
        Assert.True(mg.AirGun!.HitOn(InterceptClass.Attacker) > 0);
        Assert.True(mg.Damage * 20 < d.MaxHp, "the heavy MG only scratches it");
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
}
