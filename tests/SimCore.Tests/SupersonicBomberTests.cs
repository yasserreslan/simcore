using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The supersonic bomber (a B-1 type; the game repo's b1 spec, owner 2026-10-22): class XL, built by the main air base's hangar
// and parked only in its heavy bays; faster than the strategic bomber (cruise 18 cells/s, 22 on its run) at the same 400 m
// above the AA soldier's ceiling; 24 bombs (1,400, tier 6) dropped level in rows of 4 ACROSS its flight path: "row4" (the
// default: a row at each aim; Shift-queued aims in reach share a pass) and "block24" (all 24 in one pass: 6 rows of 4
// stacked along the path, a filled block centred on the aim; on a dragged line the rows spread from end to end). The rows
// are generic (a fire mode's `across`), so the strategic bomber and the attack jet are unchanged. No air-to-air weapons;
// 30 flare salvos; home to rearm when every bomb is spent; all of it deterministic.
public class SupersonicBomberTests(ITestOutputHelper output)
{
    private const string Bomber = "supersonic_bomber";
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
    private static Unit Fly(World w, int x, int y, int facing, string unit = Bomber)
    {
        var b = w.Spawn(1, unit, x, y, facing);
        w.Step();
        return b;
    }

    // A bomb's release and where it went off (matched by the bomb's ID).
    private sealed record Drop(uint Tick, BombRelease R, Vec At);

    // Flies u's strike until until() (or, without one, its order ends with every bomb down; at most max ticks): its drops (each
    // release with its impact) and its track.
    private static (List<Drop> Drops, List<(uint Tick, Vec Pos, long Alt, StrikePhase? Phase, long Speed)> Track) Strike(
        World w, Unit u, int max, Func<bool>? until = null)
    {
        var rel = new List<(uint Tick, BombRelease R)>();
        var imp = new List<BombImpact>();
        var track = new List<(uint, Vec, long, StrikePhase?, long)>();
        for (var t = 0; t < max; t++)
        {
            w.Step();
            rel.AddRange(w.BombReleases.Where(r => r.Aircraft == u.Id).Select(r => (w.Tick, r)));
            imp.AddRange(w.BombImpacts.Where(i => i.Aircraft == u.Id));
            track.Add((w.Tick, u.Pos, u.Altitude.Raw, u.Strike?.Phase, u.Wing?.Speed ?? 0));
            if (until?.Invoke() == true || (until is null && u.Order.Kind == OrderKind.Idle && w.Projectiles.All(p => p.Bomb is null)))
            {
                break;
            }
        }
        var drops = rel.Select(r => new Drop(r.Tick, r.R, imp.Single(i => i.Bomb == r.R.Bomb).At)).ToList();
        return (drops, track);
    }

    // SplitPasses splits drops into passes (more than 5 s between two releases: a new pass).
    private static List<List<Drop>> SplitPasses(List<Drop> drops)
    {
        var passes = new List<List<Drop>>();
        foreach (var d in drops)
        {
            if (passes.Count == 0 || d.Tick - passes[^1][^1].Tick > 5 * World.TicksPerSecond)
            {
                passes.Add([]);
            }
            passes[^1].Add(d);
        }
        return passes;
    }

    // Rows: the drops released in the same tick (a row across the path leaves together).
    private static List<List<Drop>> Rows(List<Drop> drops) => [.. drops.GroupBy(d => d.Tick).Select(g => g.ToList())];

    // Along and Across measure p from `from` along the unit direction of the release velocity and across it (cells).
    private static (double Along, double Across) Frame(Vec p, Vec from, BombRelease r)
    {
        var l = Math.Sqrt((double)r.VelX * r.VelX + (double)r.VelY * r.VelY);
        double ux = r.VelX / l, uy = r.VelY / l;
        double px = Cells(p.X.Raw - from.X.Raw), py = Cells(p.Y.Raw - from.Y.Raw);
        return (px * ux + py * uy, -px * uy + py * ux);
    }

    private static Vec Centre(IEnumerable<Drop> ds)
    {
        var l = ds.ToList();
        return new Vec(Fixed.FromRaw((long)l.Average(d => (double)d.At.X.Raw)), Fixed.FromRaw((long)l.Average(d => (double)d.At.Y.Raw)));
    }

    // The largest distance from any point of the rectangle (along a..b, across c..d of `from` in r's frame; a 0.5-cell grid) to the
    // nearest impact: under the kill zone, no gaps.
    private static double WorstGap(List<Drop> ds, Vec from, BombRelease r, double a, double b, double c, double d)
    {
        var pts = ds.Select(x => Frame(x.At, from, r)).ToList();
        var worst = 0.0;
        for (var x = a; x <= b + 1e-9; x += 0.5)
        {
            for (var y = c; y <= d + 1e-9; y += 0.5)
            {
                worst = Math.Max(worst, pts.Min(p => Math.Sqrt((p.Along - x) * (p.Along - x) + (p.Across - y) * (p.Across - y))));
            }
        }
        return worst;
    }

    // ---- Content ----

    [Fact]
    public void TheSupersonicBomberIsData()
    {
        var c = TestContent.Mvp;
        var w = Sentinel(64);
        var d = w.Player(1)!.Setup.Find(c, Bomber)!;
        var a = d.Archetype;
        var b52 = c.Archetype("strategic_bomber")!;
        Assert.Equal("Supersonic bomber", a.Name);
        Assert.Equal((AircraftClass.XL, 2000, 4500, 1700), (a.Aircraft!.SizeClass, a.BuildTime, a.Cost, d.MaxHp)); // 100 s, 4,500 (placeholders)
        Assert.Equal(InterceptClass.Bomber, a.Flight!.Class); // the strategic bomber's air class: 6 missile hits
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        Assert.True(a.Aircraft.Span < b52.Aircraft!.Span && a.Aircraft.Length < b52.Aircraft.Length, "a little smaller than the strategic bomber");
        // Faster than the strategic bomber (owner): cruise 18, 22 on its run (afterburners); a tighter turn, wider than the jets'.
        var g = a.Flight.Wing!;
        var bg = b52.Flight!.Wing!;
        Assert.True(g.HasAfterburner);
        Assert.InRange(g.Cruise, 18 * Cell1 - 20, 18 * Cell1); // 18 cells/s (a tick's whole raw units)
        Assert.InRange(g.Military, 22 * Cell1 - 20, 22 * Cell1);
        Assert.True(g.Cruise > bg.Cruise && g.Military > bg.Military, "faster than the strategic bomber");
        Assert.True(g.Radius(g.Cruise) < bg.Radius(bg.Cruise), "a tighter turn than the strategic bomber's");
        foreach (var x in c.Archetypes.Where(x => x.Flight?.Wing is not null && x.Tags.HasAll(c.TagsOf("jet"))))
        {
            Assert.True(g.Radius(g.Cruise) > x.Flight!.Wing!.Radius(x.Flight.Wing.Cruise), $"wider than the {x.Id}'s");
        }
        Assert.Equal(25 * 60 * World.TicksPerSecond, a.Lifetime); // 25 minutes of fuel
        Assert.Equal(b52.Flight.HeightRaw, a.Flight.HeightRaw); // 400 m, the strategic bomber's height (owner)
        Assert.Equal(400 * 512, a.Flight.HeightRaw);
        output.WriteLine($"cruise {F(g.Cruise / (double)Cell1)} cells/s, run {F(g.Military / (double)Cell1)}, dash {F(g.Dash / (double)Cell1)}; " +
            $"turn at cruise {F(Cells(g.Radius(g.Cruise)))} cells (strategic bomber {F(Cells(bg.Radius(bg.Cruise)))}), on its run {F(Cells(g.Radius(g.Military)))}; " +
            $"fly zone {F(Cells(a.Flight.FlyZoneMarginRaw))} cells");
        Assert.True(a.Flight.FlyZoneMarginRaw >= 2 * g.Radius(g.Cruise) + g.Cruise, "its fly zone holds its turn");
        // 24 bombs a bit lighter than the strategic bomber's: 1,400, 100% within 4 cells, 30% to 10, tier 6; no air weapons.
        var bombs = Assert.Single(d.Weapons).Effect;
        var sb = c.Components.Single(x => x.Id == "strategic_bombs").Effects.OfType<WeaponEffect>().Single();
        Assert.Equal((24, 1400, 6), (bombs.Ammo, bombs.Damage, World.StrikeTier(bombs.Damage)));
        Assert.Equal((4 * Cell1, 10 * Cell1), (bombs.SplashInner.Raw, bombs.Splash.Raw));
        Assert.True(bombs.Damage < sb.Damage && bombs.Splash.Raw < sb.Splash.Raw && bombs.SplashInner.Raw < sb.SplashInner.Raw);
        Assert.Equal(CraterKind.Large, bombs.Runway!.Crater);
        var dl = bombs.Delivery!;
        Assert.Equal(["row4", "block24"], dl.FireModes.Select(x => x.Id)); // row4 the default
        Assert.Equal([(4, 4, 1, false), (24, 4, 6, true)], dl.FireModes.Select(x => (x.Salvo, x.Across, x.Rows, x.Spread)));
        Assert.All(dl.FireModes, x => Assert.Equal(3 * Cell1, x.AcrossSpacingRaw));
        Assert.Equal(3 * Cell1, dl.StickSpacing.Raw); // rows 3 cells apart along the path
        Assert.True(dl.Overfly, "it flies on over its drops");
        Assert.Null(bombs.Interceptor);
        Assert.Null(bombs.AirGun);
        Assert.Equal(0, w.Spawn(1, Bomber, 30, 30).FireMode);
        // 30 flare salvos, two a missile, no chaff, no break.
        Assert.Equal([(CountermeasureKind.Flare, 30, 2)], a.Countermeasures.Select(x => (x.Kind, x.Salvos, x.PerMissile)));
        Assert.Null(a.Evade);
        // The other bombs keep their modes: no rows (the stealth bomber's precision bombs lay their own patterns).
        Assert.All(c.Components.SelectMany(x => x.Effects.OfType<WeaponEffect>()).Where(x => x.Delivery is { Precision: false } && x != bombs),
            x => Assert.All(x.Delivery!.FireModes, m => Assert.Equal(1, m.Across)));
    }

    // ---- Flight and threats ----

    [Fact]
    public void ItCruisesAt18AndRunsInAt22()
    {
        // Cruise: a move order across the map. The run: an attack order from far off, already heading for it (no turn to make).
        var w = Sentinel(600);
        var b = Fly(w, 40, 300, Facing.East);
        Issue(w, CommandKind.Move, b, new Cell(560, 300));
        w.Run(15 * World.TicksPerSecond);
        var cruise = b.Wing!.Speed;
        var w2 = Sentinel(600);
        var b2 = Fly(w2, 40, 300, Facing.East);
        Issue(w2, CommandKind.AttackGround, b2, new Cell(400, 300));
        var (drops, track) = Strike(w2, b2, 4000, () => b2.Strike is { Passes: >= 1 } && w2.Projectiles.All(p => p.Bomb is null));
        var run = track.Where(t => t.Phase == StrikePhase.Run).ToList();
        var speeds = drops.Select(d => Math.Sqrt((double)d.R.VelX * d.R.VelX + (double)d.R.VelY * d.R.VelY) / Cell1).ToList();
        output.WriteLine($"cruise {F(cruise / (double)Cell1)} cells/s; on its run {F(run.Min(t => t.Speed) / (double)Cell1)} to {F(run.Max(t => t.Speed) / (double)Cell1)}, " +
            $"released at {F(speeds.Min())} to {F(speeds.Max())}; the strategic bomber's 15; lowest {F(track.Min(t => t.Alt) / 512.0)} m");
        Assert.InRange(cruise, 17.5 * Cell1, 18.5 * Cell1);
        Assert.Equal(4, drops.Count);
        Assert.All(speeds, v => Assert.InRange(v, 21, 22.5)); // its run at 22 cells/s
        Assert.All(track, t => Assert.True(t.Alt > 380 * 512, "400 m up all through"));
    }

    [Fact]
    public void AaSoldiersCannotReachIt()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        Issue(w, CommandKind.SetFireMode, b, index: 1);
        var aa = new List<Unit>();
        for (var i = 0; i < 10; i++)
        {
            aa.Add(w.Spawn(2, "manpads", 120 + 20 * i, 185 + (i % 2) * 30)); // 15 cells off its line: outside the block's blast
        }
        Issue(w, CommandKind.AttackGround, b, new Cell(250, 200));
        var (drops, track) = Strike(w, b, 3000, () =>
        {
            Assert.DoesNotContain(w.Projectiles, p => p.Weapon.Effect.Interceptor is not null && p.Target == b.Id);
            return false;
        });
        output.WriteLine($"releases {drops.Count}; lowest {F(track.Min(t => t.Alt) / 512.0)} m");
        Assert.Equal(24, drops.Count);
        Assert.True(b.Alive && b.HP == b.Def.MaxHp);
        Assert.All(aa.Where(a => a.Alive), a => Assert.Equal(a.Def.StockMax[0], a.Charges[0]));
        Assert.All(track, t => Assert.True(t.Alt > 320 * 512, "above the ceiling all through its run and break"));
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
        Assert.Equal([1411, 1122, 833, 544, 255, 0], hp); // 17% of its 1,700 HP a hit (the bomber class)
    }

    [Fact]
    public void ItNeverFiresAtAircraft()
    {
        var w = Duel(320, 5);
        var b = Fly(w, 100, 160, Facing.East);
        var e = w.Spawn(2, "Multirole 500 lb", 140, 170, Facing.West);
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.Move, Units = [e.Id], Cell = new Cell(150, 160) });
        Issue(w, CommandKind.Attack, b, target: e.Id);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [b.Id], Mission = AirTaskKind.Guard, Cell = new Cell(120, 140), Cell2 = new Cell(180, 180), Filter = (byte)AirTargetKinds.Aircraft });
        for (var t = 0; t < 1200; t++)
        {
            w.Step();
            Assert.DoesNotContain(w.Projectiles, p => p.Shooter == b.Id);
            Assert.DoesNotContain(w.AirLaunches, l => l.Shooter == b.Id);
            Assert.DoesNotContain(w.GunBursts, g => g.Shooter == b.Id);
        }
        Assert.True(e.Alive && e.HP == e.Def.MaxHp);
        Assert.Equal(24, b.Charges[0]);
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
        output.WriteLine($"salvos {salvos.Count}, flares left {b.Salvos[0]}, HP {b.HP}, alive {b.Alive}");
        Assert.All(salvos, x => Assert.Equal(CountermeasureKind.Flare, x.Kind));
        Assert.Equal(Enumerable.Range(0, salvos.Count).Select(i => 29 - i), salvos.Select(x => x.Left));
        Assert.All(salvos.GroupBy(x => x.Missile), x => Assert.InRange(x.Count(), 1, 2)); // two a missile at most
        Assert.Equal(30, salvos.Count);
        Assert.Equal(0, b.Salvos[0]);
    }

    // ---- The rows ----

    [Fact]
    public void Row4LaysARowOfFourAcrossItsPathCentredOnThePoint()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        var point = new Cell(250, 200);
        Issue(w, CommandKind.AttackGround, b, point);
        StrikeRun? seen = null;
        var (drops, _) = Strike(w, b, 9000, () =>
        {
            if (b.Strike is { Planned: true, Phase: StrikePhase.Run, Drops.Count: > 0 } r && seen is null)
            {
                seen = r;
                Assert.Equal(4, r.Across);
                Assert.Equal(4, r.Drops.Count);
                Assert.Equal(4, r.DropSide.Count);
                Assert.Single(r.Drops.Distinct()); // one place: the row's centre on its run line
            }
            return b.Strike is { Passes: >= 2 } && w.Projectiles.All(p => p.Bomb is null);
        });
        var passes = SplitPasses(drops);
        output.WriteLine($"passes {passes.Count}: {string.Join(" | ", passes.Select(p => $"{p.Count} bombs t{p[0].Tick}..t{p[^1].Tick}"))}");
        Assert.Equal([4, 4], passes.Select(p => p.Count));
        Assert.True(passes[1][0].Tick - passes[0][^1].Tick > 10 * World.TicksPerSecond, "a pass apart");
        foreach (var row in passes)
        {
            Assert.Single(row.Select(d => d.Tick).Distinct()); // the row leaves together
            var c = point.Center();
            var fr = row.Select(d => Frame(d.At, c, d.R)).OrderBy(x => x.Across).ToList();
            var gaps = fr.Zip(fr.Skip(1), (x, y) => y.Across - x.Across).ToList();
            var centre = Centre(row);
            output.WriteLine($"row: along {string.Join(", ", fr.Select(x => F(x.Along)))}; across {string.Join(", ", fr.Select(x => F(x.Across)))}; " +
                $"gaps {string.Join(", ", gaps.Select(F))}; width {F(fr[^1].Across - fr[0].Across)}; centre {P(centre)} ({F(Cells(Dist(centre, c)))} off)");
            Assert.All(gaps, g => Assert.InRange(g, 3 - 2.2, 3 + 2.2)); // 3 cells apart, give or take a cell of scatter each
            Assert.InRange(fr[^1].Across - fr[0].Across, 9 - 2.2, 9 + 2.2); // 9 cells wide
            Assert.All(fr, x => Assert.InRange(x.Along, -2, 2)); // across the path, not along it
            Assert.InRange(Cells(Dist(centre, c)), 0, 1.5); // centred on the point
            // The predicted impacts are where they land (the client draws them).
            Assert.All(row, d => Assert.InRange(Cells(Dist(d.R.Predicted, d.At)), 0, 0.1));
            Assert.All(row, d => Assert.True(d.R.ImpactTick == d.R.ImpactTick && d.R.Altitude.Raw > 380 * 512));
        }
        Assert.Equal(16, b.Charges[0]);
    }

    [Fact]
    public void Block24DropsAllTwentyFourInOnePassAsAFilledBlockCentredOnThePoint()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        Issue(w, CommandKind.SetFireMode, b, index: 1);
        var point = new Cell(250, 200);
        Issue(w, CommandKind.AttackGround, b, point);
        List<Vec>? planned = null;
        var (drops, track) = Strike(w, b, 6000, () =>
        {
            if (b.Strike is { Planned: true, Phase: StrikePhase.Run, Drops.Count: 24 } r && planned is null)
            {
                planned = [.. Enumerable.Range(0, 24).Select(r.DropPoint)];
            }
            return false;
        });
        Assert.Equal(24, drops.Count);
        Assert.Single(SplitPasses(drops)); // one pass
        var rows = Rows(drops);
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Equal(4, r.Count));
        var c = point.Center();
        var r0 = drops[0].R;
        var fr = drops.Select(d => Frame(d.At, c, r0)).ToList();
        double alongLo = fr.Min(x => x.Along), alongHi = fr.Max(x => x.Along), acrossLo = fr.Min(x => x.Across), acrossHi = fr.Max(x => x.Across);
        var rowAlong = rows.Select(r => r.Average(d => Frame(d.At, c, r0).Along)).ToList();
        var rowGaps = rowAlong.Zip(rowAlong.Skip(1), (x, y) => y - x).ToList();
        var centre = Centre(drops);
        var worst = WorstGap(drops, c, r0, -7.5, 7.5, -4.5, 4.5);
        var kill = b.Def.Weapons[0].Effect.SplashInner.Raw;
        output.WriteLine($"block {F(acrossHi - acrossLo)} across x {F(alongHi - alongLo)} along; rows at {string.Join(", ", rowAlong.Select(F))} " +
            $"(gaps {string.Join(", ", rowGaps.Select(F))}); centre {P(centre)} ({F(Cells(Dist(centre, c)))} off); " +
            $"released over {drops[^1].Tick - drops[0].Tick} ticks; the worst point of the 9 x 15 block {F(worst)} cells from an impact (kill zone {F(Cells(kill))})");
        Assert.InRange(acrossHi - acrossLo, 9 - 2.2, 9 + 2.2); // 9 cells across
        Assert.InRange(alongHi - alongLo, 15 - 2.2, 15 + 2.2); // 15 cells along
        Assert.All(rowGaps, g => Assert.InRange(g, 3 - 1.6, 3 + 1.6)); // rows 3 cells apart (a row's mean: the scatter and the release tick)
        Assert.InRange(Cells(Dist(centre, c)), 0, 1); // centred on the point
        Assert.True(worst < Cells(kill), "no gaps: every point of the block within the kill zone of a bomb");
        // The plan's drop points (StrikeRun.DropPoint) and the releases' predictions are where the bombs land, within the scatter.
        Assert.NotNull(planned);
        Assert.All(drops, d => Assert.InRange(Cells(Dist(d.R.Predicted, d.At)), 0, 0.1));
        Assert.All(drops.Zip(planned!), x => Assert.InRange(Cells(Dist(x.First.At, x.Second)), 0, 1.6)); // scatter 1 cell per axis
        // It flies on over the block before it breaks; every bomb spent, the strike ends.
        var breakAt = track.First(t => t.Phase == StrikePhase.Break);
        Assert.True(Frame(breakAt.Pos, c, r0).Along >= alongHi - 2, "it passed over the block before it broke");
        Assert.Equal(0, b.Charges[0]);
        Assert.Equal(OrderKind.Idle, b.Order.Kind);
    }

    [Fact]
    public void Block24AlongADraggedLineSpreadsItsSixRowsEndToEnd()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 240, Facing.East);
        Issue(w, CommandKind.SetFireMode, b, index: 1);
        Cell from = new(200, 180), to = new(260, 180); // 60 cells: rows 12 apart
        Issue(w, CommandKind.AttackLine, b, from, to);
        var (drops, _) = Strike(w, b, 8000);
        Assert.Equal(24, drops.Count);
        Assert.Single(SplitPasses(drops));
        var rows = Rows(drops);
        Assert.Equal(6, rows.Count);
        for (var i = 0; i < 6; i++)
        {
            var want = new Vec(Fixed.FromRaw(from.Center().X.Raw + (to.Center().X.Raw - from.Center().X.Raw) * i / 5), from.Center().Y);
            var centre = Centre(rows[i]);
            var ys = rows[i].Select(d => Cells(d.At.Y.Raw - want.Y.Raw)).Order().ToList();
            output.WriteLine($"row {i}: centre {P(centre)}, want {P(want)}; across {string.Join(", ", ys.Select(F))}");
            Assert.InRange(Cells(Dist(centre, want)), 0, 1.5); // evenly from end to end
            Assert.InRange(ys[^1] - ys[0], 9 - 2.2, 9 + 2.2); // each a row of 4 across the line
            Assert.All(rows[i], d => Assert.InRange(Cells(d.At.X.Raw - want.X.Raw), -2, 2));
        }
        Assert.All(drops, d => Assert.True(d.R.VelX > 10 * Math.Abs(d.R.VelY), "the run aligned with the line"));
        Assert.Equal(OrderKind.Idle, b.Order.Kind);
        Assert.Empty(b.Aims);
    }

    [Fact]
    public void Row4ShiftQueuedPointsInReachShareAPassARowAtEach()
    {
        // a and b (15 cells apart along its path, within the 30-cell group): one pass, a row of 4 at each; c (120 cells off): the
        // next pass.
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        Cell a = new(220, 200), bb = new(235, 201), c = new(230, 320);
        Issue(w, CommandKind.AttackGround, b, a);
        Issue(w, CommandKind.AttackGround, b, bb, queue: true);
        Issue(w, CommandKind.AttackGround, b, c, queue: true);
        w.Step();
        Assert.Equal([a, bb, c], b.Aims.Select(x => x.Cell));
        var (drops, _) = Strike(w, b, 12000, () => b.Strike is { Passes: >= 2 } && w.Projectiles.All(p => p.Bomb is null));
        var passes = SplitPasses(drops);
        output.WriteLine($"passes {string.Join(" | ", passes.Select(p => $"{p.Count} t{p[0].Tick}"))}; impacts {string.Join(" ", drops.Select(d => P(d.At)))}");
        Assert.Equal([8, 4], passes.Select(p => p.Count));
        var rows = Rows(passes[0]);
        Assert.Equal(2, rows.Count);
        foreach (var (row, at) in rows.Zip(new[] { a, bb }))
        {
            var fr = row.Select(d => Frame(d.At, at.Center(), d.R)).OrderBy(x => x.Across).ToList();
            Assert.InRange(Cells(Dist(Centre(row), at.Center())), 0, 1.5);
            Assert.InRange(fr[^1].Across - fr[0].Across, 9 - 2.2, 9 + 2.2);
        }
        Assert.InRange(Cells(Dist(Centre(passes[1]), c.Center())), 0, 1.5);
        Assert.Equal(c, b.Order.Cell); // the last one goes on as a plain order
        Assert.Empty(b.Aims);
        Assert.Equal(12, b.Charges[0]);
    }

    // ---- Home to rearm ----

    [Theory]
    [InlineData("block")]
    [InlineData("rows")]
    public void EveryBombSpentItGoesHomeToRearmAndIsHeldInItsBay(string how)
    {
        var w = Sentinel(400);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var b = Fly(w, 40, 240, Facing.East);
        if (how == "block")
        {
            Issue(w, CommandKind.SetFireMode, b, index: 1);
            Issue(w, CommandKind.AttackGround, b, new Cell(240, 180));
        }
        else
        {
            // row4: six points in a line, 5 cells apart (within the group): one pass, a row at each, every bomb spent.
            for (var i = 0; i < 6; i++)
            {
                Issue(w, CommandKind.AttackGround, b, new Cell(220 + 5 * i, 180), queue: i > 0);
            }
        }
        var released = 0;
        for (var t = 0; t < 8000 && !b.Homing; t++)
        {
            w.Step();
            released += w.BombReleases.Count(r => r.Aircraft == b.Id);
        }
        output.WriteLine($"{how}: {released} released; homing {b.Homing} ({b.HomeReason}) at t{w.Tick}");
        Assert.Equal(24, released);
        Assert.Equal(0, b.Charges[0]);
        Assert.True(b.Homing, "every bomb spent: home on its own");
        Assert.Equal(HomeReason.Rearm, b.HomeReason);
        Assert.Equal(OrderKind.Idle, b.Order.Kind);
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
        Assert.Equal(24, b.Charges[0]);
        Assert.Equal(30, b.Salvos[0]);
        w.Run(30 * World.TicksPerSecond);
        Assert.Equal(FlightPhase.Parked, b.AirOps?.Phase); // held in its bay until its next order
    }

    // ---- The other bombers unchanged ----

    [Fact]
    public void TheOtherBombersLayNoRows()
    {
        // The strategic bomber's carpet and the attack jet's salvo4: no rows (Across 1, no DropSide), one bomb a tick.
        foreach (var (unit, mode, n) in new[] { ("strategic_bomber", 0, 20), ("attack_jet", 1, 4) })
        {
            var w = Sentinel(400);
            var j = w.Spawn(1, unit, 40, 200, Facing.East);
            w.Step();
            Issue(w, CommandKind.SetFireMode, j, index: mode);
            Issue(w, CommandKind.AttackGround, j, new Cell(250, 200));
            var ticks = new List<uint>();
            for (var t = 0; t < 4000 && ticks.Count < n; t++)
            {
                w.Step();
                if (j.Strike is { Planned: true } r)
                {
                    Assert.Equal(1, r.Across);
                    Assert.Empty(r.DropSide);
                }
                ticks.AddRange(w.BombReleases.Where(x => x.Aircraft == j.Id).Select(_ => w.Tick));
            }
            Assert.Equal(n, ticks.Count);
            Assert.Equal(n, ticks.Distinct().Count());
        }
    }

    [Fact]
    public void RowsAreGenericAnAttackJetWithARowModeLaysOneTooItsRetardedBombsIncluded()
    {
        // A fire mode's `across` is data: the attack jet's heavy bombs (high drag, released low) given a row4 mode (test content)
        // lay a row of 4 across its path, the drag worked into each bomb's push across.
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(x => x.Name.EndsWith("components/sentinel.json", StringComparison.Ordinal));
        var json = sources[i].Json.Replace("\"modes\": [ { \"id\": \"single\", \"salvo\": 1 },",
            "\"modes\": [ { \"id\": \"row4\", \"salvo\": 4, \"across\": 4, \"acrossSpacing\": 30 }, { \"id\": \"single\", \"salvo\": 1 },", StringComparison.Ordinal);
        Assert.NotEqual(sources[i].Json, json);
        sources[i] = new ContentSource(sources[i].Name, json);
        var content = ContentLoader.Load(sources);
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(content, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = new World(new Grid(400, 400), MatchCompiler.Compile(content, setup));
        var j = Fly(w, 40, 200, Facing.East, "attack_jet");
        Assert.Equal("row4", World.FireModesOf(j)[j.FireMode].Id);
        var point = new Cell(200, 200);
        Issue(w, CommandKind.AttackGround, j, point);
        var (drops, _) = Strike(w, j, 4000, () => j.Strike is { Passes: >= 1 } && w.Projectiles.All(p => p.Bomb is null));
        var fr = drops.Select(d => Frame(d.At, point.Center(), d.R)).OrderBy(x => x.Across).ToList();
        output.WriteLine($"attack jet row: along {string.Join(", ", fr.Select(x => F(x.Along)))}; across {string.Join(", ", fr.Select(x => F(x.Across)))}; " +
            $"predicted off by at most {F(drops.Max(d => Cells(Dist(d.R.Predicted, d.At))))} cells");
        Assert.Equal(4, drops.Count);
        Assert.Single(drops.Select(d => d.Tick).Distinct());
        Assert.InRange(fr[^1].Across - fr[0].Across, 9 - 2.2, 9 + 2.2);
        Assert.All(fr, x => Assert.InRange(x.Along, -2.5, 2.5));
        Assert.All(drops, d => Assert.InRange(Cells(Dist(d.R.Predicted, d.At)), 0, 0.1));
    }

    // ---- Determinism ----

    private static List<ulong> Script(bool reversed)
    {
        var w = Duel(400, 9);
        var b1 = w.Spawn(1, Bomber, 40, 260, Facing.East);
        var b2 = w.Spawn(2, Bomber, 360, 120, Facing.West);
        var tech = w.Spawn(1, "technical", 150, 120);
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.SetFireMode, Units = [b1.Id], Index = 1 },
            new Command { Tick = 1, Player = 1, Seq = 2, Kind = CommandKind.AttackLine, Units = [b1.Id], Cell = new Cell(180, 260), Cell2 = new Cell(240, 270) },
            new Command { Tick = 1, Player = 2, Seq = 1, Kind = CommandKind.AttackGround, Units = [b2.Id], Cell = new Cell(170, 120) },
            new Command { Tick = 1, Player = 2, Seq = 2, Kind = CommandKind.Attack, Units = [b2.Id], Target = tech.Id, Queue = true },
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
        Assert.True(drops >= 32, $"both bomb ({drops})");
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
