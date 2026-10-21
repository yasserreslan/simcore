using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The strategic bomber (H4, 2026-10-21; the game repo's docs/heavy-air-plan.md, section 1.3): a B-52 type of class XL
// built by the main air base's hangar and parked only in its heavy bays; it flies high above the AA soldier's ceiling (the
// air defence vehicle and the jets reach it; 6 missile hits bring it down, guns scratch it); 20 very heavy bombs (tier 7)
// dropped level from altitude, released well before the target, in two fire modes ("carpet20": all 20 in one stick along
// its path, the default; "salvo4": sticks of 4); Shift queues and AttackLine as the attack jet's (a carpet along a line
// spreads its 20 end to end); it flies on over its drops before it breaks; no air-to-air weapons; 30 flare salvos; all of
// it deterministic.
public class StrategicBomberTests(ITestOutputHelper output)
{
    private const string Bomber = "strategic_bomber";
    private const long Cell1 = Fixed.OneRaw;

    private static World Sentinel(int n, ulong seed = 1) => Arena.NewSentinel(n, seed);

    // Both players Sentinel (aircraft on both sides), no starting army.
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

    private static string P(Vec v) => $"({F(Cells(v.X.Raw))}, {F(Cells(v.Y.Raw))})";

    // A flying bomber of player 1 at (x, y), at its height already (the sandbox's put-down rule).
    private static Unit Fly(World w, int x, int y, int facing)
    {
        var b = w.Spawn(1, Bomber, x, y, facing);
        w.Step();
        return b;
    }

    // Flies u's strike until until() (or, without one, its order ends with every bomb down; at most max ticks), collecting its
    // releases (with the tick), impacts and its track (position each tick).
    private static (List<(uint Tick, BombRelease R)> Releases, List<BombImpact> Impacts, List<(uint Tick, Vec Pos, long Alt, StrikePhase? Phase)> Track) Strike(
        World w, Unit u, int max, Func<bool>? until = null)
    {
        var rel = new List<(uint, BombRelease)>();
        var imp = new List<BombImpact>();
        var track = new List<(uint, Vec, long, StrikePhase?)>();
        for (var t = 0; t < max; t++)
        {
            w.Step();
            rel.AddRange(w.BombReleases.Where(r => r.Aircraft == u.Id).Select(r => (w.Tick, r)));
            imp.AddRange(w.BombImpacts.Where(i => i.Aircraft == u.Id));
            track.Add((w.Tick, u.Pos, u.Altitude.Raw, u.Strike?.Phase));
            if (until?.Invoke() == true || (until is null && u.Order.Kind == OrderKind.Idle && w.Projectiles.All(p => p.Bomb is null)))
            {
                break;
            }
        }
        return (rel, imp, track);
    }

    // ---- Content ----

    [Fact]
    public void TheStrategicBomberIsData()
    {
        var c = TestContent.Mvp;
        var w = Sentinel(64);
        var d = w.Player(1)!.Setup.Find(c, Bomber)!;
        var a = d.Archetype;
        Assert.Equal("Strategic bomber", a.Name);
        Assert.Equal((AircraftClass.XL, 2400, 2000), (a.Aircraft!.SizeClass, a.BuildTime, d.MaxHp)); // 120 s (owner, placeholder)
        Assert.Equal("bomber", c.AirClasses[(int)InterceptClass.Bomber - 1].Id); // appended at the end (the arcing classes after it)
        Assert.Equal(InterceptClass.Bomber, a.Flight!.Class);
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        // Medium speed (between the gunship and the attack jet), wide turns, long range, high.
        var g = a.Flight.Wing!;
        var gun = c.Archetype("gunship")!;
        var jet = c.Archetype("attack_jet")!;
        Assert.False(g.HasAfterburner);
        Assert.True(g.Cruise > gun.Flight!.Wing!.Cruise && g.Cruise < jet.Flight!.Wing!.Cruise, "cruise between the gunship's and the attack jet's");
        Assert.True(g.Military > gun.Flight.Wing.Military && g.Military < jet.Flight.Wing.Military, "full power between them too");
        Assert.True(g.Radius(g.Cruise) > gun.Flight.Wing.Radius(gun.Flight.Wing.Cruise), "wider turns than the gunship");
        foreach (var x in c.Archetypes.Where(x => x.Flight?.Wing is not null && x.Tags.HasAll(c.TagsOf("jet"))))
        {
            Assert.True(a.Lifetime >= 4 * x.Lifetime, $"fuel well above the {x.Id}'s");
        }
        Assert.True(gun.Lifetime > a.Lifetime, "the gunship keeps the longest endurance (owner)");
        Interceptor Ic(string unit) => w.Player(2)!.Setup.Find(c, unit)!.Weapons.Single(x => x.Effect.Interceptor is not null).Effect.Interceptor!;
        var igla = Ic("manpads");
        Assert.True(a.Flight.HeightRaw > igla.CeilingRaw + 50 * 512, "high above the AA soldier's ceiling");
        Assert.True(a.Flight.HeightRaw > gun.Flight.HeightRaw, "above the gunship's orbit");
        output.WriteLine($"cruise {F(g.Cruise / (double)Cell1)} cells/s, full power {F(g.Military / (double)Cell1)}, turn at cruise {F(Cells(g.Radius(g.Cruise)))} cells, " +
            $"fuel {a.Lifetime / World.TicksPerSecond / 60} min, height {a.Flight.HeightRaw / 512} m");
        // 20 very heavy bombs, tier 7; carpet20 (the default) and salvo4; no air-to-air weapons.
        var bombs = Assert.Single(d.Weapons).Effect;
        Assert.NotNull(bombs.Delivery);
        Assert.Equal((20, 7), (bombs.Ammo, World.StrikeTier(bombs.Damage)));
        Assert.Equal(["carpet20", "salvo4"], bombs.Delivery!.FireModes.Select(x => x.Id));
        Assert.Equal((20, 4), (bombs.Delivery.SalvoOf(0), bombs.Delivery.SalvoOf(1)));
        Assert.True(bombs.Delivery.SpreadOf(0) && !bombs.Delivery.SpreadOf(1), "the carpet spreads along the pass");
        Assert.True(bombs.Delivery.Overfly, "it flies on over its drops");
        Assert.Null(bombs.Interceptor);
        Assert.Null(bombs.AirGun);
        Assert.Equal(0, w.Spawn(1, Bomber, 30, 30).FireMode); // carpet20 by default
        // 30 flare salvos, no chaff, no break.
        Assert.Equal([(CountermeasureKind.Flare, 30)], a.Countermeasures.Select(x => (x.Kind, x.Salvos)));
        Assert.Null(a.Evade);
        // Every interceptor and air gun has values for it (the owner's 6 missile hits; guns scratch).
        Assert.True(igla.Engages(InterceptClass.Bomber));
        Assert.Equal(17, Ic("sam_vehicle").HitDamageOn(InterceptClass.Bomber));
        Assert.Equal(17, igla.HitDamageOn(InterceptClass.Bomber));
        foreach (var name in new[] { "fighter_jet", "Multirole 500 lb", "attack_jet" })
        {
            foreach (var x in w.Player(1)!.Setup.Find(c, name)!.Weapons)
            {
                if (x.Effect.Interceptor is { } ic)
                {
                    Assert.True(ic.Engages(InterceptClass.Bomber));
                    Assert.Equal(17, ic.HitDamageOn(InterceptClass.Bomber));
                }
                if (x.Effect.AirGun is { } ag)
                {
                    Assert.True(ag.HitOn(InterceptClass.Bomber) > 0);
                    Assert.True(x.Effect.Damage * 10 < d.MaxHp, "a gun burst is a scratch");
                }
            }
        }
        var mg = w.Player(2)!.Setup.Find(c, "technical")!.Weapons.Single(x => x.Effect.AirGun is not null).Effect;
        Assert.True(mg.AirGun!.HitOn(InterceptClass.Bomber) > 0);
        Assert.Equal(5, 100 / 17); // 5 hits leave it flying, the 6th brings it down
    }

    // ---- Toughness and threats ----

    [Fact]
    public void AaSoldiersCannotReachIt()
    {
        // AA soldiers under its whole bomb run (round the target, along its run line, under its break): not one missile.
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        var aa = new List<Unit>();
        for (var i = 0; i < 10; i++)
        {
            aa.Add(w.Spawn(2, "manpads", 120 + 20 * i, 185 + (i % 2) * 30)); // 15 cells off its line: outside the carpet's blast
        }
        Issue(w, CommandKind.AttackGround, b, new Cell(250, 200));
        var (rel, _, track) = Strike(w, b, 3000, () =>
        {
            Assert.DoesNotContain(w.Projectiles, p => p.Weapon.Effect.Interceptor is not null && p.Target == b.Id);
            return false;
        });
        output.WriteLine($"releases {rel.Count}; lowest {F(track.Min(t => t.Alt) / 512.0)} m; AA ammo {string.Join(",", aa.Select(a => a.Alive ? a.Charges[0] : -1))}");
        Assert.Equal(20, rel.Count); // it flew its run over them
        Assert.True(b.Alive && b.HP == b.Def.MaxHp);
        Assert.All(aa.Where(a => a.Alive), a => Assert.Equal(a.Def.StockMax[0], a.Charges[0]));
        Assert.All(track, t => Assert.True(t.Alt > 320 * 512, "above the ceiling all through its run and break"));
    }

    [Fact]
    public void TheAaCeilingCutsOnlyAFighterCruisingOnABigMapAndOnlyFromAHill()
    {
        // H3's D-112 reviewed (H4): the Igla's 320 m ceiling sits under every heavy's height and over every jet's cruise but the
        // air superiority fighter's on maps of 1,024 cells and more (336 to 384 m). There the Igla's 280 m of slant range alone
        // already keeps it out of reach over level ground; the ceiling only takes away a shot from a soldier on ground at least
        // the difference (56 m at 1,024 cells) above the ground under the fighter. On a strike every jet runs in far below it.
        var c = TestContent.Mvp;
        var igla = c.Components.Single(x => x.Id == "igla").Effects.OfType<WeaponEffect>().Single();
        var ceiling = igla.Interceptor!.CeilingRaw;
        var slant = igla.Range.Raw; // one raw scale for both: 1024 a cell (2 m), 512 a metre
        foreach (var n in new[] { 128, 256, 512, 1024, 2048 })
        {
            foreach (var a in c.Archetypes.Where(x => x.Flight?.Wing is not null))
            {
                var h = a.Flight!.HeightOn(n);
                var over = h > ceiling;
                var heavy = a.Aircraft!.SizeClass == AircraftClass.XL;
                if (heavy)
                {
                    Assert.True(over, $"{a.Id} flies above the ceiling at {n} cells");
                    continue;
                }
                Assert.True(a.Flight.Wing!.IngressRaw <= ceiling, $"{a.Id} runs in under it");
                if (over)
                {
                    Assert.Equal("fighter_jet", a.Id);
                    Assert.True(n >= 1024);
                    Assert.True(h > slant, "out of the Igla's slant range over level ground anyway");
                    output.WriteLine($"{a.Id} at {n} cells: {h / 512} m (ceiling {ceiling / 512} m); reachable without the ceiling only from {(h - slant) / 512} m above it");
                }
            }
        }
    }

    [Fact]
    public void SixAirDefenceMissileHitsBringItDown()
    {
        var w = Sentinel(320);
        var b = Fly(w, 160, 160, Facing.East);
        b.MutableSalvos[0] = 0; // no flares (radar missiles anyway)
        for (var i = 0; i < 4; i++)
        {
            w.SpawnCrewed(2, "sam_vehicle", 150 + 6 * i, 150);
        }
        Issue(w, CommandKind.Move, b, new Cell(160, 160)); // it orbits over them
        var hp = new List<int>();
        var bursts = new List<AirBurstKind>();
        for (var t = 0; t < 9000 && b.Alive; t++)
        {
            w.Step();
            foreach (var x in w.AirBursts.Where(x => x.Target == b.Id && x.Kind is AirBurstKind.Hit or AirBurstKind.Kill))
            {
                bursts.Add(x.Kind);
                hp.Add(b.HP);
            }
        }
        output.WriteLine("hits: " + string.Join(", ", bursts.Zip(hp, (k, h) => $"{k} {h}")));
        Assert.False(b.Alive);
        Assert.Equal([.. Enumerable.Repeat(AirBurstKind.Hit, 5), AirBurstKind.Kill], bursts);
        Assert.Equal([1660, 1320, 980, 640, 300, 0], hp); // 17% of its 2,000 HP a hit
        Assert.Equal(KillCause.Missile, b.KilledBy);
    }

    [Fact]
    public void ItNeverFiresAtAircraft()
    {
        // An enemy jet flies round it and an enemy drone sits in its path: it has nothing to fire at them with, on any order.
        var w = Duel(320, 5);
        var b = Fly(w, 100, 160, Facing.East);
        var e = w.Spawn(2, "Multirole 500 lb", 140, 170, Facing.West);
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.Move, Units = [e.Id], Cell = new Cell(150, 160) });
        Issue(w, CommandKind.Attack, b, target: e.Id); // an attack order at an aircraft: refused or ignored
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [b.Id], Mission = AirTaskKind.Guard, Cell = new Cell(120, 140), Cell2 = new Cell(180, 180), Filter = (byte)AirTargetKinds.Aircraft });
        for (var t = 0; t < 1200; t++)
        {
            w.Step();
            Assert.DoesNotContain(w.Projectiles, p => p.Shooter == b.Id);
            Assert.DoesNotContain(w.AirLaunches, l => l.Shooter == b.Id);
            Assert.DoesNotContain(w.GunBursts, g => g.Shooter == b.Id);
        }
        Assert.True(e.Alive && e.HP == e.Def.MaxHp);
        Assert.Equal(20, b.Charges[0]);
    }

    [Fact]
    public void ItSpendsItsThirtyFlaresTwoAMissile()
    {
        var w = Duel(400, 3);
        var b = Fly(w, 200, 200, Facing.East);
        Issue(w, CommandKind.Move, b, new Cell(200, 200));
        var jets = new List<Unit>();
        for (var i = 0; i < 9; i++)
        {
            jets.Add(w.Spawn(2, "attack_jet", 60 + 10 * (i % 3), 170 + 30 * (i / 3), Facing.East)); // two IR missiles each
        }
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.Attack, Units = [.. jets.Select(j => j.Id)], Target = b.Id });
        var salvos = new List<CountermeasureSalvo>();
        for (var t = 0; t < 6000 && b.Alive && b.Salvos[0] > 0; t++)
        {
            w.Step();
            salvos.AddRange(w.Countermeasures.Where(x => x.Aircraft == b.Id));
        }
        output.WriteLine($"salvos {salvos.Count}, flares left {b.Salvos[0]}, HP {b.HP}, alive {b.Alive}; by missile " +
            string.Join(",", salvos.GroupBy(x => x.Missile).Select(x => x.Count())));
        Assert.All(salvos, x => Assert.Equal(CountermeasureKind.Flare, x.Kind));
        Assert.Equal(Enumerable.Range(0, salvos.Count).Select(i => 29 - i), salvos.Select(x => x.Left));
        Assert.All(salvos.GroupBy(x => x.Missile), x => Assert.InRange(x.Count(), 1, 2)); // two a missile at most
        Assert.Equal(30, salvos.Count);
        Assert.Equal(0, b.Salvos[0]);
    }

    // ---- The bombs ----

    [Fact]
    public void Carpet20LaysAllTwentyInOneStickAlongItsPathCentredOnThePoint()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        var point = new Cell(250, 200);
        Issue(w, CommandKind.AttackGround, b, point);
        var (rel, imp, track) = Strike(w, b, 4000);
        var dl = b.Def.Weapons[0].Effect.Delivery!;
        foreach (var (t, r) in rel.Take(3).Concat(rel.TakeLast(1)))
        {
            output.WriteLine($"release t{t} at {P(r.At)} alt {F(r.Altitude.Raw / 512.0)} m, lands {P(r.Predicted)} at t{r.ImpactTick} ({F(Cells(Dist(r.At, r.Predicted)))} cells on)");
        }
        Assert.Equal(20, rel.Count);
        Assert.Equal(20, imp.Count);
        Assert.All(imp, i => Assert.Equal(7, i.Tier));
        Assert.Single(SplitPasses(rel)); // one pass
        Assert.True(rel[^1].Tick - rel[0].Tick < 6 * World.TicksPerSecond, "one stick");
        var gaps = imp.Zip(imp.Skip(1), (x, y) => Cells(Dist(x.At, y.At))).ToList();
        output.WriteLine($"gaps min {F(gaps.Min())}, max {F(gaps.Max())}, mean {F(gaps.Average())} (spacing {F(Cells(dl.StickSpacing.Raw))}); length {F(Cells(Dist(imp[0].At, imp[^1].At)))}");
        Assert.All(gaps, g => Assert.InRange(g, 0.3, 5.5)); // 2.5 cells apart, give or take 1.5 cells of scatter each
        Assert.InRange(Cells(Dist(imp[0].At, imp[^1].At)), 19 * 2.5 - 4, 19 * 2.5 + 4); // 47.5 cells end to end
        var cx = imp.Average(i => (double)i.At.X.Raw) / Cell1;
        var cy = imp.Average(i => (double)i.At.Y.Raw) / Cell1;
        var c = point.Center();
        output.WriteLine($"centre ({F(cx)}, {F(cy)})");
        Assert.InRange(Math.Sqrt((cx - Cells(c.X.Raw)) * (cx - Cells(c.X.Raw)) + (cy - Cells(c.Y.Raw)) * (cy - Cells(c.Y.Raw))), 0, 2); // all on target
        Assert.True(Math.Abs(Cells(imp[^1].At.X.Raw - imp[0].At.X.Raw)) > 5 * Math.Abs(Cells(imp[^1].At.Y.Raw - imp[0].At.Y.Raw)), "along its path");
        // Released from altitude, well before each impact, flying on over the stick before it breaks.
        Assert.All(rel, r => Assert.True(r.R.Altitude.Raw > 360 * 512, "released high"));
        Assert.All(rel, r => Assert.InRange(Cells(Dist(r.R.At, r.R.Predicted)), 40, 60));
        var breakAt = track.First(t => t.Phase == StrikePhase.Break);
        output.WriteLine($"break at t{breakAt.Tick} at {P(breakAt.Pos)}; the last impact {P(imp[^1].At)}");
        Assert.True(breakAt.Pos.X.Raw >= imp[^1].At.X.Raw - 2 * Cell1, "it passed over the stick before it broke");
        Assert.Equal(OrderKind.Idle, b.Order.Kind); // out of bombs: the strike ends
        Assert.Equal(0, b.Charges[0]);
    }

    [Fact]
    public void Salvo4DropsSticksOf4AtEachPoint()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        Issue(w, CommandKind.SetFireMode, b, index: 1);
        var point = new Cell(250, 200);
        Issue(w, CommandKind.AttackGround, b, point);
        w.Step();
        Assert.Equal(1, b.FireMode);
        var (rel, imp, _) = Strike(w, b, 9000, () => b.Strike is { Passes: >= 2 } && w.Projectiles.All(p => p.Bomb is null));
        var passes = SplitPasses(rel);
        output.WriteLine($"passes {passes.Count}: {string.Join(" | ", passes.Select(p => $"{p.Count} bombs t{p[0].Tick}..t{p[^1].Tick}"))}");
        Assert.Equal(8, rel.Count);
        Assert.Equal([4, 4], passes.Select(p => p.Count));
        Assert.True(passes[1][0].Tick - passes[0][^1].Tick > 10 * World.TicksPerSecond, "a pass apart");
        foreach (var stick in imp.Chunk(4))
        {
            var gaps = stick.Zip(stick.Skip(1), (x, y) => Cells(Dist(x.At, y.At))).ToList();
            var cx = stick.Average(i => (double)i.At.X.Raw) / Cell1;
            var cy = stick.Average(i => (double)i.At.Y.Raw) / Cell1;
            output.WriteLine($"stick gaps {string.Join(", ", gaps.Select(F))}, centre ({F(cx)}, {F(cy)})");
            Assert.All(gaps, g => Assert.InRange(g, 0.3, 5.5));
            Assert.InRange(Cells(Dist(stick[0].At, stick[^1].At)), 7.5 - 4, 7.5 + 4); // 7.5 cells end to end
            Assert.InRange(Math.Sqrt((cx - 250.5) * (cx - 250.5) + (cy - 200.5) * (cy - 200.5)), 0, 2.5);
        }
        Assert.Equal(12, b.Charges[0]);
    }

    // SplitPasses splits releases into passes (more than 5 s between two releases: a new pass).
    private static List<List<(uint Tick, BombRelease R)>> SplitPasses(List<(uint Tick, BombRelease R)> rel)
    {
        var passes = new List<List<(uint, BombRelease)>>();
        foreach (var r in rel)
        {
            if (passes.Count == 0 || r.Tick - passes[^1][^1].Item1 > 5 * World.TicksPerSecond)
            {
                passes.Add([]);
            }
            passes[^1].Add(r);
        }
        return passes;
    }

    [Fact]
    public void ACarpetAlongADraggedLineSpreadsAllTwentyEndToEnd()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 240, Facing.East);
        Cell from = new(200, 180), to = new(280, 180); // 80 cells: 4.2 apart
        Issue(w, CommandKind.AttackLine, b, from, to);
        w.Step();
        Assert.True(b.Aims is [{ Line: true }]);
        var (rel, imp, track) = Strike(w, b, 8000);
        output.WriteLine($"releases {rel.Count}; impacts {string.Join(" ", imp.Select(i => P(i.At)))}");
        Assert.Equal(20, imp.Count);
        Assert.Single(SplitPasses(rel)); // one pass
        for (var i = 0; i < 20; i++)
        {
            var want = new Vec(Fixed.FromRaw(from.Center().X.Raw + (to.Center().X.Raw - from.Center().X.Raw) * i / 19), from.Center().Y);
            Assert.InRange(Cells(Dist(imp[i].At, want)), 0, 2.5); // evenly from end to end
        }
        Assert.All(rel, r => Assert.True(r.R.VelX > 10 * Math.Abs(r.R.VelY), "the run aligned with the line, flown from its start"));
        // Each released well before its impact point, the bomber's track passing over the whole line before it breaks.
        Assert.All(rel, r => Assert.True(r.R.Predicted.X.Raw - r.R.At.X.Raw > 40 * Cell1, "released well before"));
        var lastRelease = rel[^1].Tick;
        var breakAt = track.First(t => t.Phase == StrikePhase.Break);
        var overLine = track.Where(t => t.Tick > lastRelease && t.Tick < breakAt.Tick).ToList();
        output.WriteLine($"last release t{lastRelease} at {P(rel[^1].R.At)}; break t{breakAt.Tick} at {P(breakAt.Pos)}");
        Assert.True(breakAt.Pos.X.Raw >= to.Center().X.Raw - Cell1, "still on its run until past the line's end");
        Assert.All(overLine, t => Assert.InRange(Cells(Math.Abs(t.Pos.Y.Raw - from.Center().Y.Raw)), 0, 3)); // over the line
        Assert.Equal(OrderKind.Idle, b.Order.Kind); // the line bombed: the order is done
        Assert.Empty(b.Aims);
    }

    [Fact]
    public void AShortLineGetsTheWholeCarpetStretchedAboutItsMiddle()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 240, Facing.East);
        Cell from = new(230, 180), to = new(250, 180); // 20 cells: shorter than the carpet's 47.5
        Issue(w, CommandKind.AttackLine, b, from, to);
        var (_, imp, _) = Strike(w, b, 8000);
        Assert.Equal(20, imp.Count);
        var cx = imp.Average(i => (double)i.At.X.Raw) / Cell1;
        output.WriteLine($"from {F(Cells(imp[0].At.X.Raw))} to {F(Cells(imp[^1].At.X.Raw))}, centre {F(cx)}");
        Assert.InRange(Cells(imp[^1].At.X.Raw - imp[0].At.X.Raw), 47.5 - 4, 47.5 + 4);
        Assert.InRange(cx, 240.5 - 2, 240.5 + 2);
    }

    [Fact]
    public void ShiftQueuedPointsAreBombedTogetherWhenCloseAndOneAfterAnother()
    {
        // salvo4: a and b (20 cells apart, within the carpet's 48-cell group) in one pass, a stick of 4 at each; c (120 cells
        // off) on the next pass. Then carpet20 on two close points: one carpet spread from the first to the second.
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        Issue(w, CommandKind.SetFireMode, b, index: 1);
        Cell a = new(220, 200), bb = new(240, 201), c = new(230, 320);
        Issue(w, CommandKind.AttackGround, b, a);
        Issue(w, CommandKind.AttackGround, b, bb, queue: true);
        Issue(w, CommandKind.AttackGround, b, c, queue: true);
        w.Step();
        Assert.Equal([a, bb, c], b.Aims.Select(x => x.Cell));
        var (rel, imp, _) = Strike(w, b, 12000, () => b.Strike is { Passes: >= 2 } && w.Projectiles.All(p => p.Bomb is null));
        var passes = SplitPasses(rel);
        output.WriteLine($"passes {string.Join(" | ", passes.Select(p => $"{p.Count} t{p[0].Tick}"))}; impacts {string.Join(" ", imp.Select(i => P(i.At)))}");
        Assert.Equal([8, 4], passes.Select(p => p.Count));
        double Near(Cell x) => imp.Take(8).Count(i => Cells(Dist(i.At, x.Center())) < 7);
        Assert.Equal(4, Near(a));
        Assert.Equal(4, Near(bb));
        Assert.All(imp.Skip(8), i => Assert.InRange(Cells(Dist(i.At, c.Center())), 0, 7));
        Assert.Equal(c, b.Order.Cell); // the last one goes on as a plain order
        Assert.Empty(b.Aims);

        // carpet20 on two queued points 30 cells apart: one pass, 20 bombs spread from the first to the second (47.5 cells, centred).
        var w2 = Sentinel(400);
        var b2 = Fly(w2, 40, 200, Facing.East);
        Cell p = new(220, 200), q = new(250, 200);
        Issue(w2, CommandKind.AttackGround, b2, p);
        Issue(w2, CommandKind.AttackGround, b2, q, queue: true);
        var (rel2, imp2, _) = Strike(w2, b2, 8000);
        Assert.Single(SplitPasses(rel2));
        Assert.Equal(20, imp2.Count);
        var cx = imp2.Average(i => (double)i.At.X.Raw) / Cell1;
        output.WriteLine($"carpet on two points: from {F(Cells(imp2[0].At.X.Raw))} to {F(Cells(imp2[^1].At.X.Raw))}, centre {F(cx)}");
        Assert.InRange(cx, 235.5 - 2, 235.5 + 2);
        Assert.InRange(Cells(imp2[^1].At.X.Raw - imp2[0].At.X.Raw), 47.5 - 4, 47.5 + 4);
        Assert.Equal(OrderKind.Idle, b2.Order.Kind);
        Assert.Empty(b2.Aims);
    }

    // ---- The main air base ----

    [Fact]
    public void ItIsBuiltAtTheMainAirBaseAndParksOnlyInAHeavyBay()
    {
        var w = Sentinel(400);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var hangar = w.Unit(af.Hangar)!;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [hangar.Id], Unit = Bomber });
        Unit? b = null;
        for (var t = 0; t < 2600 && b is null; t++)
        {
            w.Step();
            b = w.Units.FirstOrDefault(u => u.Def.Archetype.Id == Bomber);
        }
        Assert.NotNull(b);
        Assert.InRange((int)w.Tick, 2400, 2500); // 120 s
        Assert.Equal(FlightPhase.Parked, b!.AirOps!.Phase);
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == b.AirOps.Station).Kind);
        Assert.True(w.StationsFor(af, b.Def.Archetype.Aircraft!).All(s => s.Kind == StationKind.Heavy), "only heavy bays take it");
        var fwd = w.SpawnAirfield(w.Player(1)!.Setup.Find(w.Content, "airfield_facility")!, new Cell(60, 320), 0, [(0, 0)])!;
        Assert.Empty(w.StationsFor(fwd, b.Def.Archetype.Aircraft!)); // a forward airfield never takes it
        // Out on a carpet run and back: it lands on the base's runway and parks in a heavy bay again.
        Issue(w, CommandKind.AttackGround, b, new Cell(300, 300));
        for (var t = 0; t < 4000 && b.AirOps is not null; t++)
        {
            w.Step();
        }
        Assert.Null(b.AirOps); // taxied out and took off
        var released = 0;
        for (var t = 0; t < 6000 && b.Order.Kind != OrderKind.Idle; t++)
        {
            w.Step();
            released += w.BombReleases.Count(r => r.Aircraft == b.Id);
        }
        Assert.Equal(20, released);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.ReturnToBase, Units = [b.Id], Target = af.Facility });
        var parked = false;
        for (var t = 0; t < 12000 && !parked; t++)
        {
            w.Step();
            parked = b.AirOps is { Phase: FlightPhase.Parked };
        }
        Assert.True(parked, "it lands and parks");
        Assert.Equal(af.Id, b.AirOps!.Airfield);
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == b.AirOps.Station).Kind);
        // Parked without bombs, an attack order is an implicit Rearm (D-ORD2): rearmed in its bay, then it leaves.
        Issue(w, CommandKind.AttackGround, b, new Cell(300, 300));
        w.Step();
        Assert.Contains(w.OrderRefusals, r => r.Aircraft == b.Id && r.Reason == OrderRefusalReason.Rearming);
        for (var t = 0; t < 6000 && b.AirOps is not null; t++)
        {
            w.Step();
        }
        Assert.Null(b.AirOps);
        Assert.Equal(20, b.Charges[0]);
        Assert.Equal(30, b.Salvos[0]);
    }

    // 2026-10-21 polish (decision 143): a strike that spends every bomb ends with the trip home to rearm (the gunship's rule,
    // decision 141), not idle in the air: on a dragged line (its pass Final), on a point, and on a queue whose far aim gets
    // nothing. It lands at the main air base, is rearmed in a heavy bay (20 bombs, 30 flare salvos) and held there.
    [Theory]
    [InlineData("line")]
    [InlineData("point")]
    [InlineData("queue")]
    public void EveryBombSpentItGoesHomeToRearmAndIsHeldInItsBay(string how)
    {
        var w = Sentinel(400);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var b = Fly(w, 40, 240, Facing.East);
        switch (how)
        {
            case "line":
                Issue(w, CommandKind.AttackLine, b, new Cell(200, 180), new Cell(280, 180));
                break;
            case "point":
                Issue(w, CommandKind.AttackGround, b, new Cell(240, 180));
                break;
            default:
                Issue(w, CommandKind.AttackGround, b, new Cell(240, 180));
                Issue(w, CommandKind.AttackGround, b, new Cell(240, 330), queue: true); // beyond the group: nothing left for it
                break;
        }
        var released = 0;
        for (var t = 0; t < 8000 && !b.Homing; t++)
        {
            w.Step();
            released += w.BombReleases.Count(r => r.Aircraft == b.Id);
        }
        output.WriteLine($"{how}: {released} released; homing {b.Homing} ({b.HomeReason}) at t{w.Tick}");
        Assert.Equal(20, released);
        Assert.Equal(0, b.Charges[0]);
        Assert.True(b.Homing, "every bomb spent: home on its own");
        Assert.Equal(HomeReason.Rearm, b.HomeReason);
        Assert.Equal(OrderKind.Idle, b.Order.Kind);
        Assert.Null(b.Strike);
        var parked = false;
        for (var t = 0; t < 12000 && !parked; t++)
        {
            w.Step();
            parked = b.AirOps is { Phase: FlightPhase.Parked };
        }
        Assert.True(parked, "it lands and parks");
        Assert.Equal(af.Id, b.AirOps!.Airfield);
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == b.AirOps.Station).Kind);
        for (var t = 0; t < 90 * World.TicksPerSecond && !w.ServiceOf(b).Serviced; t++)
        {
            w.Step();
        }
        Assert.Equal(20, b.Charges[0]);
        Assert.Equal(30, b.Salvos[0]);
        w.Run(60 * World.TicksPerSecond);
        Assert.Equal(FlightPhase.Parked, b.AirOps?.Phase); // held in its bay until its next order
    }

    // ---- Determinism ----

    // A scripted strike on both sides (a Sentinel duel world): player 1's bomber a carpet on a line, player 2's salvo4 on a
    // queue of a point and a technical; the commands issued in order or reversed; the world's hash every tick.
    private static List<ulong> Script(bool reversed)
    {
        var w = Duel(400, 9);
        var b1 = w.Spawn(1, Bomber, 40, 260, Facing.East);
        var b2 = w.Spawn(2, Bomber, 360, 120, Facing.West);
        var tech = w.Spawn(1, "technical", 150, 120);
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.AttackLine, Units = [b1.Id], Cell = new Cell(180, 260), Cell2 = new Cell(250, 270) },
            new Command { Tick = 1, Player = 2, Seq = 1, Kind = CommandKind.SetFireMode, Units = [b2.Id], Index = 1 },
            new Command { Tick = 1, Player = 2, Seq = 2, Kind = CommandKind.AttackGround, Units = [b2.Id], Cell = new Cell(200, 120) },
            new Command { Tick = 1, Player = 2, Seq = 3, Kind = CommandKind.Attack, Units = [b2.Id], Target = tech.Id, Queue = true },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        var drops = 0;
        for (var t = 0; t < 2000; t++)
        {
            w.Step();
            drops += w.BombReleases.Count;
            hashes.Add(w.Hash());
        }
        Assert.True(drops >= 28, $"both bomb ({drops})");
        Assert.False(tech.Alive, "the queue's technical is destroyed");
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
