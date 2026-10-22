using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The stealth bomber (a B-2 type; the game repo's b2 spec, owner 2026-10-22): class XL, built by the main air base's hangar and
// parked only in its heavy bays; very high (700 m), a very low signature (detected at a quarter of every sensor's range, locked in
// three times as long, kill chances x 0.7) and fragile once seen (3 air defence missile hits). Two weapons the player picks between
// (CommandKind.SetBombWeapon): 16 precision bombs (no scatter, each on its own point; all16 / group4; up to 16 queued aims) and 2
// bunker busters (both / single; they destroy everything in 3 cells whatever the armour, kill the units in a tunnel there and
// collapse its entrance, a deep crater). The heavy bombers' exclusive group: one at a time, the type locked by the first built.
public class StealthBomberTests(ITestOutputHelper output)
{
    private const string Bomber = "stealth_bomber";
    private const long Cell1 = Fixed.OneRaw;
    private const int M = 512; // raw per metre

    private static World Sentinel(int n, ulong seed = 1) => Arena.NewSentinel(n, seed);

    // Both players Sentinel (jets on both sides), no starting army.
    private static World Duel(int n, ulong seed)
    {
        var content = TestContent.Mvp;
        var setup = TestContent.Sentinel(seed).For(content, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        return new World(new Grid(n, n), MatchCompiler.Compile(content, setup with { Players = [p0, p1] }));
    }

    // A Sentinel-lobby world (player 2 the Resistance) on content patched in one file (test variations of the data).
    private static World Patched(int n, string file, params (string From, string To)[] edits)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(x => x.Name.EndsWith(file, StringComparison.Ordinal));
        var json = sources[i].Json;
        foreach (var (from, to) in edits)
        {
            Assert.Contains(from, json, StringComparison.Ordinal);
            json = json.Replace(from, to, StringComparison.Ordinal);
        }
        sources[i] = new ContentSource(sources[i].Name, json);
        var content = ContentLoader.Load(sources);
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(content, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(n, n), MatchCompiler.Compile(content, setup));
    }

    // The stealth bomber flying low (test content: 80 m, so the ground's air defence can see and reach it).
    private static World Low(int n) => Patched(n, "archetypes/sentinel.json",
        ("\"flight\": { \"kind\": \"fixedWing\", \"height\": 7000, \"climbRate\": 120", "\"flight\": { \"kind\": \"fixedWing\", \"height\": 800, \"climbRate\": 120"),
        ("\"ingressHeight\": 7000", "\"ingressHeight\": 800"));

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, Cell? cell2 = null, uint target = 0, bool queue = false, int index = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Cell2 = cell2, Target = target, Queue = queue, Index = index });

    private static double Cells(long raw) => raw / (double)Cell1;

    private static long Dist(Vec a, Vec b) => World.Len3(a.X.Raw - b.X.Raw, a.Y.Raw - b.Y.Raw, 0);

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static string F2(double v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    // A flying stealth bomber of player 1 at (x, y), at its height already.
    private static Unit Fly(World w, int x, int y, int facing, string unit = Bomber, byte player = 1)
    {
        var b = w.Spawn(player, unit, x, y, facing);
        w.Step();
        return b;
    }

    private sealed record Drop(uint Tick, BombRelease R, Vec At, BombImpact I);

    // Flies u's strike until until() (or, without one, its order ends with every bomb down; at most max ticks).
    private static List<Drop> Strike(World w, Unit u, int max, Func<bool>? until = null)
    {
        var rel = new List<(uint Tick, BombRelease R)>();
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
        return [.. rel.Select(r => { var i = imp.Single(x => x.Bomb == r.R.Bomb); return new Drop(r.Tick, r.R, i.At, i); })];
    }

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

    // Frame measures p from `from` along the release velocity of r and across it (cells).
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

    // ---- Content ----

    [Fact]
    public void TheStealthBomberIsData()
    {
        var c = TestContent.Mvp;
        var w = Sentinel(64);
        var d = w.Player(1)!.Setup.Find(c, Bomber)!;
        var a = d.Archetype;
        var b52 = c.Archetype("strategic_bomber")!;
        Assert.Equal("Stealth bomber", a.Name);
        Assert.Equal((AircraftClass.XL, 2600, 6000, 1600), (a.Aircraft!.SizeClass, a.BuildTime, a.Cost, d.MaxHp)); // 130 s, 6,000
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        Assert.InRange(a.Aircraft.Span.Raw, 21 * Cell1, 22 * Cell1); // 0.82 of the real 52.4 m: 43 m (21.5 cells), through the heavy bay's 48 m door
        Assert.InRange(a.Aircraft.Length.Raw, 8 * Cell1, 9 * Cell1); // 0.82 of 21 m: 17 m
        // Very high (owner): 700 m, the other heavies 400. Cruise 15, between the strategic bomber's 14 and the supersonic's 18.
        Assert.Equal(700 * M, a.Flight!.HeightRaw);
        Assert.Equal(400 * M, b52.Flight!.HeightRaw);
        var g = a.Flight.Wing!;
        Assert.False(g.HasAfterburner);
        Assert.InRange(g.Cruise, 15 * Cell1 - 20, 15 * Cell1);
        Assert.True(a.Flight.FlyZoneMarginRaw >= 2 * g.Radius(g.Cruise) + g.Cruise, "its fly zone holds its turn");
        output.WriteLine($"turn at cruise {F(Cells(g.Radius(g.Cruise)))} cells; fly zone {F(Cells(a.Flight.FlyZoneMarginRaw))}");
        // Hard to see (owner): a quarter of the range, three times the lock, kill chances x 0.7; its own air class (3 hits).
        Assert.Equal(new Signature(25, 300, 70), a.Signature);
        Assert.Equal(InterceptClass.Stealth, a.Flight.Class);
        Assert.Equal([(CountermeasureKind.Flare, 8, 1)], a.Countermeasures.Select(x => (x.Kind, x.Salvos, x.PerMissile)));
        // The heavy bombers' exclusive group.
        var grp = Assert.Single(c.ExclusiveGroups);
        Assert.Equal(("heavy_bomber", 1, true), (grp.Id, grp.Max, grp.LockType));
        Assert.Equal(["stealth_bomber", "strategic_bomber", "supersonic_bomber"], c.Archetypes.Where(x => x.Exclusive == grp.Index).Select(x => x.Id));
        // Two weapons the player picks between, each with its own fire modes.
        var u = w.Spawn(1, Bomber, 30, 30);
        var slots = World.BombWeaponsOf(u);
        Assert.Equal(2, slots.Count);
        var pb = slots[0].Effect;
        var bb = slots[1].Effect;
        Assert.Equal((16, 1600, 7), (pb.Ammo, pb.Damage, pb.Delivery!.Tier));
        Assert.Equal((2 * Cell1, 4 * Cell1), (pb.SplashInner.Raw, pb.Splash.Raw));
        Assert.True(pb.Delivery.Precision);
        Assert.Equal((0L, 0L), (pb.Delivery.ScatterLevel.Raw, bb.Delivery!.ScatterLevel.Raw));
        Assert.Equal(16, pb.Delivery.MaxAims);
        Assert.Equal(["all16", "group4"], pb.Delivery.FireModes.Select(x => x.Id));
        Assert.Equal((2, 6000, "bunker_buster"), (bb.Ammo, bb.Damage, c.Warheads[bb.Warhead.Index].Id));
        Assert.Equal(["both", "single"], bb.Delivery.FireModes.Select(x => x.Id));
        Assert.Equal(3 * Cell1, bb.Penetrator!.RadiusRaw);
        Assert.Null(pb.Penetrator);
        Assert.Equal(["all16", "group4"], World.FireModesOf(u).Select(x => x.Id));
        Assert.Equal(["both", "single"], World.FireModesOf(u, 1).Select(x => x.Id));
        Assert.Equal(0, u.BombWeapon);
        // Every other aircraft has one bomb weapon at most: nothing to toggle.
        foreach (var x in c.Archetypes.Where(x => x.Flight is not null && x.Id != Bomber && w.Player(1)!.Setup.Find(c, x.Id) is not null))
        {
            Assert.True(World.BombWeaponsOf(w.Spawn(1, x.Id, 40, 40)).Count <= 1, x.Id);
        }
    }

    [Fact]
    public void ItCruisesAt700MAndFifteenCellsASecond()
    {
        var w = Sentinel(600);
        var b = Fly(w, 40, 300, Facing.East);
        Issue(w, CommandKind.Move, b, new Cell(560, 300));
        var lo = long.MaxValue;
        for (var t = 0; t < 20 * World.TicksPerSecond; t++)
        {
            w.Step();
            lo = Math.Min(lo, b.Altitude.Raw);
        }
        output.WriteLine($"cruise {F(b.Wing!.Speed / (double)Cell1)} cells/s, lowest {F(lo / (double)M)} m");
        Assert.InRange(b.Wing.Speed, 14.5 * Cell1, 15.5 * Cell1);
        Assert.True(lo >= 690 * M, "700 m up all along");
    }

    // ---- Seen and shot ----

    // DetectedAt: is a stealth bomber (or `unit`) at slant distance about dx cells from sensor s (same height as its eye) tracked by
    // s's side after one tick.
    private static bool TrackedAt(Func<World, (Unit Sensor, long EyeRaw)> sensor, int dx, string unit = Bomber, Func<int, World>? make = null)
    {
        var w = (make ?? (n => Sentinel(n)))(400);
        var (s, eye) = sensor(w);
        var b = w.Spawn(1, unit, 150 + dx, 200, Facing.North);
        b.Altitude = Fixed.FromRaw(eye);
        w.Order(1, CommandKind.Move, [b], cell: new Cell(150 + dx, 200));
        w.Run(1);
        return w.AirTracked(s.Owner, b.Id);
    }

    private static (Unit, long) MobileRadar(World w)
    {
        var r = w.SpawnCrewed(2, "mobile_radar", 150, 200).Vehicle;
        w.Order(2, CommandKind.Deploy, [r]);
        w.Run(r.Def.Archetype.DeployTicks + 2);
        return (r, w.RadarEyeRaw(r));
    }

    private static (Unit, long) AirDefenceVehicle(World w)
    {
        var v = w.SpawnCrewed(2, "sam_vehicle", 150, 200).Vehicle;
        w.Run(2);
        return (v, w.RadarEyeRaw(v) > 0 ? w.RadarEyeRaw(v) : 3 * M);
    }

    private static (Unit, long) Fighter(World w)
    {
        var j = w.Spawn(2, "fighter_jet", 150, 200, Facing.North);
        j.Altitude = Fixed.FromRaw(300 * M);
        w.Order(2, CommandKind.Move, [j], cell: new Cell(150, 200));
        return (j, 300 * M);
    }

    [Theory]
    [InlineData("mobile radar", 400)]
    [InlineData("air defence vehicle", 260)]
    [InlineData("fighter jet", 160)]
    public void EverySensorDetectsItAtAQuarterOfItsRange(string sensor, int range)
    {
        Func<World, (Unit, long)> s = sensor switch { "mobile radar" => MobileRadar, "air defence vehicle" => AirDefenceVehicle, _ => Fighter };
        Func<int, World>? make = sensor == "fighter jet" ? n => Duel(n, 1) : null;
        var quarter = range / 4;
        // The strategic bomber (a normal signature) is seen out to the full range; the stealth bomber out to a quarter of it.
        Assert.True(TrackedAt(s, range - 8, "strategic_bomber", make), "a normal heavy near its full range");
        Assert.False(TrackedAt(s, quarter + 4, Bomber, make), "the stealth bomber beyond a quarter");
        Assert.True(TrackedAt(s, quarter - 4, Bomber, make), "the stealth bomber within a quarter");
        output.WriteLine($"{sensor}: a normal aircraft {range} cells of slant, the stealth bomber {quarter} ({quarter * 2} m); at 700 m it is 350 cells up");
    }

    [Fact]
    public void At700MNothingSeesOrReachesIt()
    {
        // A mobile radar, three air defence vehicles and five AA soldiers right under its run: never tracked, never a launch, and
        // its precision bombs go off on target. The strategic bomber on the same run is tracked at once.
        var w = Sentinel(400);
        var (radar, _) = MobileRadar(w);
        var sams = Enumerable.Range(0, 3).Select(i => w.SpawnCrewed(2, "sam_vehicle", 170 + 20 * i, 205).Vehicle).ToList();
        var aa = Enumerable.Range(0, 5).Select(i => w.Spawn(2, "manpads", 160 + 15 * i, 190)).ToList();
        var b = Fly(w, 40, 200, Facing.East);
        Issue(w, CommandKind.AttackGround, b, new Cell(250, 200));
        var tracked = 0;
        var drops = Strike(w, b, 3000, () =>
        {
            tracked += w.AirTracked(2, b.Id) ? 1 : 0;
            Assert.DoesNotContain(w.Projectiles, p => p.Weapon.Effect.Interceptor is not null);
            return b.Strike is { Passes: >= 1 } && w.Projectiles.All(p => p.Bomb is null);
        });
        Assert.Equal(0, tracked);
        Assert.Equal(16, drops.Count);
        Assert.True(b.HP == b.Def.MaxHp);
        var w2 = Sentinel(400);
        MobileRadar(w2);
        var s = Fly(w2, 150, 200, Facing.East, "strategic_bomber");
        w2.Run(2);
        Assert.True(w2.AirTracked(2, s.Id), "the strategic bomber at 400 m: tracked");
    }

    [Fact]
    public void ItsLockTakesThreeTimesAsLong()
    {
        // An air defence vehicle's lock: 1 s on a normal aircraft, 3 s on the stealth bomber (low enough to be seen), 2.1 s radar-cued.
        foreach (var (unit, cue, want) in new[] { ("strategic_bomber", false, 20), (Bomber, false, 60), (Bomber, true, 42) })
        {
            var w = Low(400);
            if (cue)
            {
                MobileRadar(w);
            }
            var v = w.SpawnCrewed(2, "sam_vehicle", 150, 200).Vehicle;
            var b = Fly(w, 160, 205, Facing.East, unit);
            Issue(w, CommandKind.Move, b, new Cell(160, 205));
            var ticks = 0;
            for (var t = 0; t < 600 && ticks == 0; t++)
            {
                w.Step();
                if (v.AirTarget == b.Id)
                {
                    ticks = w.AirLockTicks(v);
                }
            }
            output.WriteLine($"{unit}{(cue ? " (radar-cued)" : "")}: lock {ticks} ticks");
            Assert.Equal(want, ticks);
        }
    }

    [Fact]
    public void ThreeAirDefenceMissileHitsBringItDown()
    {
        var w = Low(320);
        var b = Fly(w, 160, 160, Facing.East);
        for (var i = 0; i < 4; i++)
        {
            w.SpawnCrewed(2, "sam_vehicle", 150 + 6 * i, 150);
        }
        Issue(w, CommandKind.Move, b, new Cell(160, 160));
        var hp = new List<int>();
        var bursts = new List<AirBurstKind>();
        for (var t = 0; t < 12000 && b.Alive; t++)
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
        Assert.Equal([AirBurstKind.Hit, AirBurstKind.Hit, AirBurstKind.Kill], bursts);
        Assert.Equal([1056, 512, 0], hp.Select(h => Math.Max(0, h))); // 34% of its 1,600 HP a hit (the strategic bomber takes 6)
    }

    // ---- Precision bombs ----

    [Fact]
    public void All16OnAPointLaysAFourByFourGridExactlyCentredOnIt()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        var point = new Cell(250, 200);
        Issue(w, CommandKind.AttackGround, b, point);
        List<Vec>? planned = null;
        var drops = Strike(w, b, 6000, () =>
        {
            if (b.Strike is { Planned: true, Phase: StrikePhase.Run, Drops.Count: 16 } r && planned is null)
            {
                planned = [.. Enumerable.Range(0, 16).Select(r.DropPoint)];
            }
            return false;
        });
        Assert.Equal(16, drops.Count);
        Assert.Single(SplitPasses(drops));
        var c = point.Center();
        var fr = drops.Select(d => Frame(d.At, c, drops[0].R)).ToList();
        var centre = Centre(drops);
        var acrossVals = fr.Select(x => Math.Round(x.Across * 2) / 2).Distinct().Order().ToList();
        var alongVals = fr.Select(x => Math.Round(x.Along * 2) / 2).Distinct().Order().ToList();
        output.WriteLine($"along {string.Join(", ", alongVals.Select(F))}; across {string.Join(", ", acrossVals.Select(F))}; centre {F2(Cells(Dist(centre, c)))} off; " +
            $"released over {drops[^1].Tick - drops[0].Tick} ticks; worst miss of its planned point {F2(drops.Zip(planned!).Max(x => Cells(Dist(x.First.At, x.Second))))} cells");
        Assert.Equal([-3.0, -1.0, 1.0, 3.0], acrossVals); // 4 columns 2 cells apart
        Assert.Equal([-3.0, -1.0, 1.0, 3.0], alongVals); // 4 rows 2 cells apart
        Assert.InRange(Cells(Dist(centre, c)), 0, 0.15);
        Assert.NotNull(planned);
        Assert.All(drops.Zip(planned!), x => Assert.InRange(Cells(Dist(x.First.At, x.Second)), 0, 0.15)); // no scatter: on its point
        Assert.All(drops, d => Assert.InRange(Cells(Dist(d.R.Predicted, d.At)), 0, 0.1));
        Assert.All(drops, d => Assert.Equal(7, d.I.Tier)); // a very big explosion
        Assert.All(drops, d => Assert.True(d.R.Altitude.Raw >= 680 * M, "released from 700 m"));
        Assert.Equal(0, b.Charges[0]);
        Assert.Equal(2, b.Charges[1]); // the bunker busters untouched
    }

    [Fact]
    public void Group4DropsATwoByTwoOnThePointEachPass()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        Issue(w, CommandKind.SetFireMode, b, index: 1);
        var point = new Cell(250, 200);
        Issue(w, CommandKind.AttackGround, b, point);
        var drops = Strike(w, b, 12000, () => b.Strike is { Passes: >= 2 } && w.Projectiles.All(p => p.Bomb is null));
        var passes = SplitPasses(drops);
        Assert.Equal([4, 4], passes.Select(p => p.Count));
        foreach (var p in passes)
        {
            var fr = p.Select(d => Frame(d.At, point.Center(), d.R)).ToList();
            output.WriteLine("pass: " + string.Join(" ", fr.Select(x => $"({F2(x.Along)}, {F2(x.Across)})")));
            Assert.All(fr, x => Assert.InRange(Math.Abs(x.Along), 0.85, 1.15));
            Assert.All(fr, x => Assert.InRange(Math.Abs(x.Across), 0.85, 1.15));
            Assert.InRange(Cells(Dist(Centre(p), point.Center())), 0, 0.15);
        }
        Assert.Equal(8, b.Charges[0]);
    }

    [Fact]
    public void SixteenQueuedAimsGetOneBombEachInOnePass()
    {
        // 16 points over 30 x 12 cells, Shift-queued: all16 drops one bomb on each, exactly, in one pass (aside of its run line too).
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        var aims = Enumerable.Range(0, 16).Select(i => new Cell(225 + 2 * i, 200 + new[] { -6, -2, 2, 6 }[i % 4])).ToList();
        for (var i = 0; i < aims.Count; i++)
        {
            Issue(w, CommandKind.AttackGround, b, aims[i], queue: i > 0);
        }
        w.Step();
        Assert.Equal(16, b.Aims.Count); // 16 aims queued (the jets' 8)
        var drops = Strike(w, b, 8000, () => b.Strike is { Passes: >= 1 } && w.Projectiles.All(p => p.Bomb is null));
        Assert.Equal(16, drops.Count);
        Assert.Single(SplitPasses(drops));
        var misses = aims.Select(a => drops.Min(d => Cells(Dist(d.At, a.Center())))).ToList();
        var ownBomb = aims.Select(a => drops.Count(d => Cells(Dist(d.At, a.Center())) < 0.5)).ToList();
        output.WriteLine($"misses {string.Join(" ", misses.Select(F2))}; released over {drops[^1].Tick - drops[0].Tick} ticks");
        Assert.All(misses, m => Assert.InRange(m, 0, 0.15));
        Assert.All(ownBomb, n => Assert.Equal(1, n)); // one each
        Assert.Empty(b.Aims);
    }

    [Fact]
    public void TwoQueuedAimsShareAllSixteenAndGroup4TakesFourAimsAPass()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200, Facing.East);
        Cell a = new(240, 200), c = new(255, 204);
        Issue(w, CommandKind.AttackGround, b, a);
        Issue(w, CommandKind.AttackGround, b, c, queue: true);
        var drops = Strike(w, b, 8000, () => b.Strike is { Passes: >= 1 } && w.Projectiles.All(p => p.Bomb is null));
        Assert.Equal(16, drops.Count);
        Assert.Equal(8, drops.Count(d => Cells(Dist(d.At, a.Center())) < 3.5)); // a grid of 4 x 2 on each, 2 cells apart
        Assert.Equal(8, drops.Count(d => Cells(Dist(d.At, c.Center())) < 3.5));
        Assert.InRange(Cells(Dist(Centre(drops.Where(d => Cells(Dist(d.At, a.Center())) < 3.5)), a.Center())), 0, 0.15);

        var w2 = Sentinel(400);
        var b2 = Fly(w2, 40, 200, Facing.East);
        Issue(w2, CommandKind.SetFireMode, b2, index: 1);
        var pts = Enumerable.Range(0, 8).Select(i => new Cell(225 + 3 * i, 200 + (i % 2 == 0 ? -3 : 3))).ToList();
        for (var i = 0; i < pts.Count; i++)
        {
            Issue(w2, CommandKind.AttackGround, b2, pts[i], queue: i > 0);
        }
        var d2 = Strike(w2, b2, 14000, () => b2.Strike is { Passes: >= 2 } && w2.Projectiles.All(p => p.Bomb is null));
        var passes = SplitPasses(d2);
        output.WriteLine($"group4 over 8 aims: passes {string.Join(", ", passes.Select(p => p.Count))}");
        Assert.Equal([4, 4], passes.Select(p => p.Count));
        Assert.All(pts, p => Assert.Equal(1, d2.Count(d => Cells(Dist(d.At, p.Center())) < 0.5)));
    }

    // ---- The weapon toggle and the bunker busters ----

    [Fact]
    public void EachWeaponKeepsItsOwnFireMode()
    {
        var w = Sentinel(64);
        var b = Fly(w, 30, 30, Facing.East);
        Issue(w, CommandKind.SetFireMode, b, index: 1); // group4
        Issue(w, CommandKind.SetBombWeapon, b, index: 1);
        w.Step();
        Assert.Equal((1, 0), (b.BombWeapon, b.FireMode)); // the bunker busters: "both"
        Assert.Equal(["both", "single"], World.FireModesOf(b).Select(x => x.Id));
        Issue(w, CommandKind.SetFireMode, b, index: 1); // single
        Issue(w, CommandKind.SetBombWeapon, b, index: 0);
        w.Step();
        Assert.Equal((0, 1, 1), (b.BombWeapon, b.FireMode, b.FireModeOf(1))); // group4 back; single kept
        Issue(w, CommandKind.SetBombWeapon, b, index: 2); // no third weapon: ignored
        var s = Fly(w, 40, 40, Facing.East, "strategic_bomber");
        Issue(w, CommandKind.SetBombWeapon, s, index: 1); // one bomb weapon: nothing to toggle
        w.Step();
        Assert.Equal((0, 0), (b.BombWeapon, s.BombWeapon));
    }

    [Fact]
    public void BunkerBustersBothLeaveTogetherSingleOneAPass()
    {
        foreach (var (mode, want) in new[] { (0, new[] { 2 }), (1, new[] { 1, 1 }) })
        {
            var w = Sentinel(400);
            var b = Fly(w, 40, 200, Facing.East);
            Issue(w, CommandKind.SetBombWeapon, b, index: 1);
            Issue(w, CommandKind.SetFireMode, b, index: mode);
            var point = new Cell(250, 200);
            Issue(w, CommandKind.AttackGround, b, point);
            var drops = Strike(w, b, 14000, () => b.Charges[1] == 0 && w.Projectiles.All(p => p.Bomb is null));
            var passes = SplitPasses(drops);
            var fr = drops.Select(d => Frame(d.At, point.Center(), d.R)).ToList();
            output.WriteLine($"{(mode == 0 ? "both" : "single")}: passes {string.Join(", ", passes.Select(p => p.Count))}; impacts " +
                string.Join(" ", fr.Select(x => $"({F2(x.Along)}, {F2(x.Across)})")) + $"; fall {drops[0].R.ImpactTick - drops[0].Tick} ticks");
            Assert.Equal(want, passes.Select(p => p.Count));
            Assert.All(drops, d => Assert.Equal(CraterKind.Deep, d.I.Crater));
            Assert.Equal(16, b.Charges[0]); // its precision bombs untouched
            if (mode == 0)
            {
                Assert.Single(drops.Select(d => d.Tick).Distinct());
                Assert.Equal([-1.5, 1.5], fr.Select(x => Math.Round(x.Across * 2) / 2).Order());
                Assert.All(fr, x => Assert.InRange(x.Along, -0.15, 0.15));
            }
            else
            {
                Assert.All(drops, d => Assert.InRange(Cells(Dist(d.At, point.Center())), 0, 0.15));
            }
            Assert.Equal(drops.Count, w.Craters.Count);
            Assert.All(w.Craters, k => Assert.Equal((CraterKind.Deep, 1.5), (k.Kind, Cells(k.RadiusRaw))));
            w.Run(6000);
            Assert.Empty(w.Craters); // gone after 5 minutes
        }
    }

    [Fact]
    public void ABunkerBusterDestroysEverythingInThreeCellsWhateverItsArmour()
    {
        var w = Sentinel(400);
        var point = new Cell(250, 200);
        var tough = w.Spawn(2, "infantry_carrier", 251, 201);
        tough.HP = 100_000; // far more than its 6,000 could take
        var rider = w.Spawn(2, "Rifleman", 251, 202);
        rider.Inside = tough.Id;
        var villa = w.Spawn(2, "villa_walled", 247, 196); // a walled house, garrisoned
        var inside = Enumerable.Range(0, 3).Select(i => w.Spawn(2, "Rifleman", 250, 196 + i)).ToList();
        foreach (var s in inside)
        {
            s.Garrison = villa.Id;
            s.Inside = 0;
        }
        var soldier = w.Spawn(2, "Rifleman", 252, 199);
        var far = w.Spawn(2, "infantry_carrier", 256, 200); // 6 cells off: outside the 3 cells
        w.Step();
        var b = Fly(w, 40, 200, Facing.East);
        Issue(w, CommandKind.SetBombWeapon, b, index: 1);
        Issue(w, CommandKind.SetFireMode, b, index: 1); // single: one bomb on the point
        Issue(w, CommandKind.AttackGround, b, point);
        var drops = Strike(w, b, 8000, () => b.Charges[1] == 1 && w.Projectiles.All(p => p.Bomb is null));
        w.Run(2);
        var d = Assert.Single(drops);
        output.WriteLine($"impact {F2(Cells(Dist(d.At, point.Center())))} cells off; carrier HP {tough.HP}, villa {villa.HP}, far carrier {far.HP}/{far.Def.MaxHp}");
        Assert.False(tough.Alive, "an armoured vehicle with 100,000 HP");
        Assert.False(rider.Alive, "its rider");
        Assert.False(villa.Alive, "the house");
        Assert.All(inside, s => Assert.False(s.Alive, "its garrison dies in it"));
        Assert.False(soldier.Alive);
        Assert.True(far.Alive, "6 cells off");
    }

    [Fact]
    public void ABunkerBusterKillsEveryUnitInTheTunnelItHitsAndCollapsesTheEntrance()
    {
        // Three linked entrances of one network (40 cells apart): e2's units after the precision bombs, then a bunker buster on e1.
        var w = Sentinel(400);
        var e1 = w.Spawn(2, "tunnel_large", 250, 200);
        var e2 = w.Spawn(2, "tunnel_large", 290, 200);
        var e3 = w.Spawn(2, "tunnel_large", 210, 200);
        w.Step();
        Assert.True(World.TunnelLinked(e1, e2) && World.TunnelLinked(e1, e3));
        var at1 = Enumerable.Range(0, 4).Select(i => w.Spawn(2, "Rifleman", 240, 230 + i)).ToList();
        var truck = w.SpawnCrewed(2, "technical", 245, 230);
        var at2 = Enumerable.Range(0, 3).Select(i => w.Spawn(2, "Rifleman", 280, 230 + i)).ToList();
        var at3 = Enumerable.Range(0, 2).Select(i => w.Spawn(2, "Rifleman", 200, 230 + i)).ToList();
        foreach (var u in at1.Append(truck.Vehicle))
        {
            Bury(u, e1);
        }
        foreach (var u in at2)
        {
            Bury(u, e2);
        }
        foreach (var u in at3)
        {
            Bury(u, e3);
        }
        w.Step();
        Assert.Equal(10, w.TunnelOccupants(e1).Count); // one network (the technical's driver rides inside it)
        var b = Fly(w, 40, 200, Facing.East);
        // Precision bombs on e2 first: the entrance and its rubble go, but nobody underground dies (the survival rules move its
        // units to the nearest linked entrance, e1): only the bunker buster reaches them.
        Issue(w, CommandKind.AttackGround, b, Cell.Of(e2.Pos));
        Strike(w, b, 6000, () => b.Strike is { Passes: >= 1 } && w.Projectiles.All(p => p.Bomb is null));
        w.Run(2);
        Assert.False(e2.Alive && !e2.Rubble, "the precision bombs wreck the entrance");
        Assert.All(at2, u => Assert.True(u.Alive && u.InTunnel, "underground: untouched by the precision bombs"));
        // The bunker buster on e1: every unit at it dies, the entrance collapses; e3's units live on.
        var below = w.Units.Where(u => u.InTunnel && u.Tunnel == e1.Id && u.TunnelNext == 0).Select(u => u.Id).Order().ToList();
        Assert.Superset(at1.Select(u => u.Id).Append(truck.Vehicle.Id).ToHashSet(), below.ToHashSet());
        Issue(w, CommandKind.SetBombWeapon, b, index: 1);
        Issue(w, CommandKind.SetFireMode, b, index: 1);
        Issue(w, CommandKind.AttackGround, b, Cell.Of(e1.Pos));
        var kills = new List<TunnelKill>();
        var collapses = new List<TunnelCollapse>();
        var bbDrops = Strike(w, b, 8000, () =>
        {
            kills.AddRange(w.TunnelKills);
            collapses.AddRange(w.TunnelCollapses);
            return b.Charges[1] == 1 && w.Projectiles.All(p => p.Bomb is null);
        });
        w.Run(2);
        output.WriteLine($"bunker buster {F2(Cells(Dist(Assert.Single(bbDrops).At, e1.Pos)))} cells from the entrance; " +
            $"killed underground {kills.Count} of {below.Count} below it; collapsed {string.Join(" ", collapses.Select(c => c.Entrance))}");
        Assert.All(at1, u => Assert.False(u.Alive));
        Assert.False(truck.Vehicle.Alive);
        Assert.False(truck.Driver.Alive, "its driver dies in it");
        Assert.Equal(below, kills.Select(k => k.Unit).Order());
        Assert.All(kills, k => Assert.Equal(e1.Id, k.Entrance));
        Assert.Equal([e1.Id], collapses.Select(c => c.Entrance));
        Assert.True(e1.Rubble, "the entrance collapses into rubble");
        Assert.All(at3, u => Assert.True(u.Alive && u.InTunnel, "another node's units live"));
    }

    private static void Bury(Unit u, Unit e)
    {
        u.Idle();
        u.Inside = World.Underground;
        u.Tunnel = e.Id;
        u.Pos = e.Pos;
    }

    // ---- The heavy bombers' exclusive group ----

    private static (World W, Airfield Af) Base(World w)
    {
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        return (w, af);
    }

    private static void Produce(World w, Airfield af, string id) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [af.Hangar], Unit = id });

    private static int Queued(World w, Airfield af) => w.Unit(af.Hangar)!.ProductionQueue.Count + w.Unit(af.Hangar)!.Bays.Count(b => b.Def >= 0);

    private Unit BuildOne(World w, Airfield af, string id)
    {
        Unit? u = null;
        for (var t = 0; t < 4000 && u is null; t++)
        {
            w.Step();
            u = w.Units.FirstOrDefault(x => x.Def.Archetype.Id == id && x.Owner == 1 && x.Alive);
        }
        Assert.NotNull(u);
        return u!;
    }

    [Fact]
    public void OneHeavyBomberAtATimeItsTypeLockedByTheFirst()
    {
        var (w, af) = Base(Sentinel(400));
        var c = w.Content;
        Assert.True(w.ExclusiveOf(1, c.Archetype(Bomber)!)!.Value.Allowed);
        Assert.Null(w.ExclusiveOf(1, c.Archetype("attack_jet")!)); // not in the group
        Produce(w, af, "supersonic_bomber");
        w.Step();
        Assert.Equal(1, Queued(w, af));
        // While one is queued or being built: no second of any type.
        foreach (var id in new[] { "supersonic_bomber", "strategic_bomber", Bomber })
        {
            Produce(w, af, id);
            w.Step();
            Assert.Equal(1, Queued(w, af));
            Assert.Equal(ExclusiveRefusal.AtMax, w.ExclusiveOf(1, c.Archetype(id)!)!.Value.Refusal);
        }
        Assert.Equal(-1, w.Player(1)!.ExclusiveLocks[0]); // not built yet: nothing locked
        var b1 = BuildOne(w, af, "supersonic_bomber");
        var st = w.ExclusiveOf(1, c.Archetype(Bomber)!)!.Value;
        Assert.Equal((1, c.Archetype("supersonic_bomber")!.Index, ExclusiveRefusal.AtMax), (st.Count, st.Locked, st.Refusal));
        // Lost: the same type may be rebuilt, another never.
        b1.HP = 0;
        w.Run(3);
        Assert.Equal(ExclusiveRefusal.Locked, w.ExclusiveOf(1, c.Archetype(Bomber)!)!.Value.Refusal);
        Assert.Equal(ExclusiveRefusal.Locked, w.ExclusiveOf(1, c.Archetype("strategic_bomber")!)!.Value.Refusal);
        Produce(w, af, Bomber);
        Produce(w, af, "strategic_bomber");
        w.Step();
        Assert.Equal(0, Queued(w, af));
        Produce(w, af, "supersonic_bomber");
        w.Step();
        Assert.Equal(1, Queued(w, af));
        // The other player is not affected.
        Assert.Equal(-1, w.Player(2)!.ExclusiveLocks[0]);
    }

    [Fact]
    public void WithoutLockTypeItIsOneAliveAtATimeAnyType()
    {
        var w = Patched(400, "exclusive_groups.json", ("\"lockType\": true", "\"lockType\": false"));
        (w, var af) = Base(w);
        Produce(w, af, "strategic_bomber");
        var b1 = BuildOne(w, af, "strategic_bomber");
        Produce(w, af, Bomber);
        w.Step();
        Assert.Equal(0, Queued(w, af)); // one alive: no second
        b1.HP = 0;
        w.Run(3);
        Produce(w, af, Bomber);
        w.Step();
        Assert.Equal(1, Queued(w, af)); // lost: any type now
        Assert.Equal(-1, w.Player(1)!.ExclusiveLocks[0]);
    }

    // ---- Home to rearm ----

    [Fact]
    public void EveryBombOfBothSpentItGoesHomeToRearm()
    {
        var (w, af) = Base(Sentinel(400));
        var b = Fly(w, 40, 240, Facing.East);
        Issue(w, CommandKind.AttackGround, b, new Cell(240, 180));
        Strike(w, b, 8000, () => b.Charges[0] == 0 && w.Projectiles.All(p => p.Bomb is null));
        w.Run(20 * World.TicksPerSecond);
        Assert.False(b.Homing, "its bunker busters left: it orbits on (the owner's rule)");
        Issue(w, CommandKind.SetBombWeapon, b, index: 1);
        Issue(w, CommandKind.AttackGround, b, new Cell(200, 260));
        for (var t = 0; t < 8000 && !b.Homing; t++)
        {
            w.Step();
        }
        Assert.Equal([0, 0], b.Charges);
        Assert.True(b.Homing, "every bomb spent: home on its own");
        Assert.Equal(HomeReason.Rearm, b.HomeReason);
        var parked = false;
        for (var t = 0; t < 15000 && !parked; t++)
        {
            w.Step();
            parked = b.AirOps is { Phase: FlightPhase.Parked };
        }
        Assert.True(parked, "it lands and parks");
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == b.AirOps!.Station).Kind);
        var at = w.Tick;
        for (var t = 0; t < 90 * World.TicksPerSecond && !w.ServiceOf(b).Serviced; t++)
        {
            w.Step();
        }
        output.WriteLine($"rearmed 16 + 2 in {(w.Tick - at) / (double)World.TicksPerSecond:F1} s");
        Assert.Equal([16, 2], b.Charges);
        Assert.Equal(8, b.Salvos[0]);
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
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.AttackGround, Units = [b1.Id], Cell = new Cell(200, 260) },
            new Command { Tick = 1, Player = 1, Seq = 2, Kind = CommandKind.AttackGround, Units = [b1.Id], Cell = new Cell(215, 266), Queue = true },
            new Command { Tick = 1, Player = 2, Seq = 1, Kind = CommandKind.SetBombWeapon, Units = [b2.Id], Index = 1 },
            new Command { Tick = 1, Player = 2, Seq = 2, Kind = CommandKind.Attack, Units = [b2.Id], Target = tech.Id },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        var drops = 0;
        for (var t = 0; t < 2400; t++)
        {
            w.Step();
            drops += w.BombReleases.Count;
            hashes.Add(w.Hash());
        }
        Assert.True(drops >= 18, $"both bomb ({drops})");
        Assert.False(tech.Alive, "the bunker busters' technical is destroyed");
        Assert.NotEmpty(w.Craters);
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
