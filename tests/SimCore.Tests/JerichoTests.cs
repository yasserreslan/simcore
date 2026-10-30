using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The Jericho type ballistic missile truck (jericho_truck; docs/design.md, "Jericho type ballistic missile truck"; the owner's
// spec of 2026-10-29): the Sentinel's ground-launched strike missile, one truck and nothing else, slow, the heaviest missile.
// One missile, 300 to 2,500 cells, at any ground point; the weapon's terminal profile with a vertical start (boostTicks: straight
// up off the erector, pitching over onto a very high arc) and a steep fall, about 25 s at full range; 2,500 on the
// heavy_ballistic table in two rings (8 / 16 cells); a 10 s warning to every enemy; each launch shows the truck for 30 s;
// reloaded only at a supply depot or a field ammo point, packed up, never by a garage's service.
public class JerichoTests
{
    private const int Deploy = 400, Stow = 300, Reload = 3600, Cost = 1000, Warn = 200, Reveal = 600, Y = 40;

    private static WeaponEffect Missile => TestContent.Mvp.Component("jericho_missile")!.Effects.OfType<WeaponEffect>().Single();

    // Range: player 1's (Sentinel) crewed truck at (5, Y) facing east on an open map of the given size.
    private static (World W, Unit Truck) Range(int size, bool fog = false, ulong seed = 1)
    {
        var w = Arena.NewSentinel(size, seed, fog ? s => s with { Fog = true } : null);
        var (t, _) = w.SpawnCrewed(1, "jericho_truck", 5, Y, Facing.East);
        return (w, t);
    }

    private static void Ground(World w, Unit t, Cell at) =>
        w.Issue(new Command { Tick = w.Tick, Player = t.Owner, Kind = CommandKind.AttackGround, Units = [t.Id], Cell = at });

    private sealed class Flight
    {
        public List<(uint Id, uint Tick, Vec Dest)> Launches { get; } = [];
        public List<(uint Tick, IncomingEvent E)> Events { get; } = [];
        public List<(uint Tick, Detonation D)> Blasts { get; } = [];
    }

    private static Flight Watch(World w, int ticks, Action<World>? each = null)
    {
        var f = new Flight();
        for (var i = 0; i < ticks; i++)
        {
            var tick = w.Tick;
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (p.Weapon.Effect.Terminal is not null && !f.Launches.Exists(l => l.Id == p.Id))
                {
                    f.Launches.Add((p.Id, tick, p.Dest));
                }
            }
            f.Events.AddRange(w.IncomingEvents.Select(e => (tick, e)));
            f.Blasts.AddRange(w.Detonations.Select(d => (tick, d)));
            each?.Invoke(w);
        }
        return f;
    }

    // Fly: the truck fires its missile `cells` east on an open map; the world just after the launch and the missile.
    private static (World W, Projectile P) Fly(int cells, ulong seed = 1)
    {
        var (w, t) = Range(cells + 40, seed: seed);
        Ground(w, t, new Cell(5 + cells, Y));
        Projectile? p = null;
        while (p is null)
        {
            w.Step();
            p = w.Projectiles.FirstOrDefault(q => q.Weapon.Effect.Terminal is not null);
        }
        return (w, p);
    }

    // Path: its drawn flight from the launch to the impact, a point a tick (FlightPointAt).
    private static List<FlightPoint> Path(World w, Projectile p)
    {
        var impact = w.ImpactTickOf(p)!.Value;
        var list = new List<FlightPoint>();
        for (var k = w.Tick - 1; k <= impact + 1; k++)
        {
            list.Add(w.FlightPointAt(p, k)!.Value);
        }
        return list;
    }

    private static int FlightTicks(World w, Projectile p) => (int)(w.ImpactTickOf(p)!.Value - w.Tick + p.Age + 1);

    // ---- Content ----

    [Fact]
    public void TheContentNumbers()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype("jericho_truck")!;
        Assert.Equal("Ballistic missile truck", a.Name);
        Assert.Equal((4500, 2400, 500), (a.Cost, a.BuildTime, a.MaxHp)); // 120 s
        Assert.Equal(c.Archetype("hypersonic_truck")!.Armor, a.Armor); // medium_vehicle
        Assert.Equal(Fixed.Frac(10, 200), a.Speed); // 1 cell a second: slower than the hypersonic truck's 1.4
        Assert.True(a.Speed < c.Archetype("hypersonic_truck")!.Speed);
        Assert.Equal((Deploy, Stow), (a.DeployTicks, a.StowTicks)); // 20 s, 15 s
        Assert.True(a.VerticalLaunch);
        Assert.True(c.Factions.Single(f => f.Id == "sentinel").Archetypes[a.Index]);
        Assert.False(c.Factions.Single(f => f.Id == "resistance").Archetypes[a.Index]);
        Assert.True(a.Exclusive < 0, "no cap (decision to confirm)");

        var m = Missile;
        Assert.Equal(2500, m.Damage);
        Assert.Equal("heavy_ballistic", m.Warhead.Id);
        Assert.Equal((Fixed.FromInt(300), Fixed.FromInt(2500)), (m.MinRange, m.Range));
        Assert.Equal((Fixed.FromInt(16), Fixed.FromInt(8), 30), (m.Splash, m.SplashInner, m.SplashOuterPercent));
        Assert.Equal((1, 1), (m.Ammo, m.SalvoSize));
        Assert.Equal(Reveal, m.RevealTicks); // 30 s
        Assert.Equal(InterceptClass.Ballistic, m.Intercept);
        Assert.Equal(new DepotReload(Reload, 20, Cost, true, true), m.DepotReload);
        var tf = m.Terminal!;
        Assert.Equal((120, 120, 50, 600L * 512, 2500L * 512, 60), (tf.BoostTicks, tf.ClimbTicks, tf.PeakPercent, tf.PeakMinRaw, tf.PeakMaxRaw, tf.PeakAtPercent));
        Assert.Equal((Fixed.FromInt(20), 5000, Warn, 0), (tf.DiveFrom, tf.DiveSlopePermille, tf.WarnTicks, tf.Weaves));
        Assert.Equal(Fixed.FromInt(8), m.Fire!.Radius);
        Assert.Equal(0, TestContent.Mvp.Component("hypersonic_missiles")!.Effects.OfType<WeaponEffect>().Single().Terminal!.BoostTicks); // unchanged
    }

    [Fact]
    public void TheHeaviestMissileAndItBringsDownAnyBuilding()
    {
        var c = TestContent.Mvp;
        var m = Missile;
        var others = c.Components.Where(k => k.Id != "jericho_missile").SelectMany(k => k.Effects.OfType<WeaponEffect>()).ToList();
        // Above every missile, shell and rocket; only bombs (a delivery: the MOAB type bomb's 3,000, the bunker buster's 6,000) hit harder.
        Assert.All(others.Where(o => o.Delivery is null), o => Assert.True(o.Damage < m.Damage, $"damage {o.Damage}"));
        var building = c.Archetypes.Where(x => x.IsStructure).Max(x => x.MaxHp); // the heavy hangar's 6,000
        Assert.True(m.Warhead.Damage(m.Damage, c.Archetype("airbase_hangar")!.Armor, 100) >= building);
        Assert.True(m.Warhead.Damage(m.Damage, c.Archetype("main_battle_tank")!.Armor, 100) >= c.Archetype("main_battle_tank")!.MaxHp);
        // The heavy_ballistic table is the ballistic one but for buildings.
        var (hb, b) = (c.Warhead("heavy_ballistic")!, c.Warhead("ballistic")!);
        Assert.Equal(250, hb.Damage(100, c.Archetype("airbase_hangar")!.Armor, 100));
        var bld = c.Archetype("airbase_hangar")!.Armor;
        Assert.Equal(b.Versus.Where((_, i) => i != bld), hb.Versus.Where((_, i) => i != bld));
    }

    [Fact]
    public void ItIsBuiltAtTheVehicleGarageAndThereIsNoCap()
    {
        var w = Arena.NewSentinel(80);
        var g = w.Spawn(1, "vehicle_garage", 20, 10);
        w.Step();
        for (var i = 0; i < 2; i++)
        {
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [g.Id], Unit = "jericho_truck" });
        }
        w.Step();
        Assert.Equal(2, g.Bays.Count(b => b.Def >= 0)); // two at once: no exclusive group
        w.Run(2400 + 300);
        Assert.Equal(2, w.All("jericho_truck").Count);
    }

    // ---- Range ----

    [Fact]
    public void ItTakesPointsFromThreeHundredToTwentyFiveHundredCells()
    {
        var (w, t) = Range(2560);
        Assert.Equal(World.GroundShot.TooClose, w.ShotAt(t, new Cell(5 + 298, Y).Center()));
        Assert.Equal(World.GroundShot.InRange, w.ShotAt(t, new Cell(5 + 302, Y).Center()));
        Assert.Equal(World.GroundShot.InRange, w.ShotAt(t, new Cell(5 + 2498, Y).Center()));
        Assert.Equal(World.GroundShot.OutOfRange, w.ShotAt(t, new Cell(5 + 2502, Y).Center()));
        Ground(w, t, new Cell(5 + 298, Y)); // too close: it never fires
        Assert.Empty(Watch(w, Deploy + 100).Launches);
        Assert.Equal(1, t.Charges[0]);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(2498)]
    public void ItFiresAtBothEndsOfItsBandAtAnUnseenPoint(int d)
    {
        var (w, t) = Range(d + 20, fog: true);
        Ground(w, t, new Cell(5 + d, Y));
        var f = Watch(w, Deploy + 560);
        var launch = Assert.Single(f.Launches);
        Assert.True(launch.Tick >= Deploy, "it fires once set up (20 s)");
        Assert.Contains(f.Blasts, b => b.D.Projectile == launch.Id);
        Assert.Equal(0, t.Charges[0]); // one missile
    }

    // ---- The flight ----

    [Theory]
    [InlineData(300, 70, 90)]
    [InlineData(1000, 240, 260)]
    [InlineData(2500, 490, 510)]
    public void AboutTwentyFiveSecondsToFullRange(int cells, int low, int high)
    {
        var (w, p) = Fly(cells);
        Assert.InRange(FlightTicks(w, p), low, high); // 2,500 cells: 501 ticks (25.05 s)
    }

    [Theory]
    [InlineData(300)]
    [InlineData(1200)]
    [InlineData(2500)]
    public void AVerticalStartAVeryHighArcAndASteepFall(int cells)
    {
        var (w, p) = Fly(cells);
        var path = Path(w, p);
        var total = (p.Dest - p.Origin).Len().Raw;
        Assert.Equal((p.Origin, p.LaunchHeight), (path[0].Pos, path[0].Altitude)); // from the erector
        Assert.Equal(p.LandHeight, path[^1].Altitude);
        // The peak: half the ground range over the launch height (flat), at least 600 m, at most 2.5 km.
        var peak = p.LaunchHeight.Raw + Math.Clamp(total / 2, 600L * 512, 2500L * 512);
        Assert.Equal(peak, p.ArcRise.Raw);
        Assert.InRange(path.Max(x => x.Altitude.Raw), peak - 512, peak);
        // Straight up off the erector: its first quarter second climbs at least ten times what it moves over the ground, its
        // first second at least five times (a short shot's boost is shorter: it pitches over sooner).
        foreach (var (k, ratio) in new[] { (5, 10), (20, 5) })
        {
            var x = path[k];
            Assert.True(x.Altitude.Raw - p.LaunchHeight.Raw > ratio * (x.Pos - p.Origin).Len().Raw, $"tick {k}: {x.Altitude.Raw - p.LaunchHeight.Raw} up, {(x.Pos - p.Origin).Len().Raw} along");
        }
        Assert.True(path[1].Altitude > path[0].Altitude);
        // Up then down, always forward; never under its point's height before the impact.
        var top = path.FindIndex(x => x.Altitude.Raw == path.Max(y => y.Altitude.Raw));
        for (var i = 1; i < path.Count; i++)
        {
            Assert.True(i <= top ? path[i].Altitude >= path[i - 1].Altitude : path[i].Altitude <= path[i - 1].Altitude, $"monotonic at {i}");
            Assert.True((path[i].Pos - p.Origin).Len() >= (path[i - 1].Pos - p.Origin).Len(), "always forward");
            Assert.True(path[i].Altitude >= p.LandHeight);
        }
        // Smooth velocity: no tick's step differs from the one before by more than a fifth of the projectile speed per axis
        // and tick, but in the dive (it steepens there) and the last tick (it reaches its point early).
        var vc = Missile.ProjectileSpeed.Raw;
        for (var i = 2; i < path.Count - 2; i++)
        {
            if (path[i].Phase == TerminalPhase.Dive)
            {
                break;
            }
            var (a, b) = (Step(path, i - 1), Step(path, i));
            Assert.True(Math.Abs(b.H - a.H) <= vc / 5 && Math.Abs(b.Z - a.Z) <= vc / 5 * 2, $"tick {i}: {a} then {b}");
        }
        // The fall: steeper than 60 degrees at the separation point (a full shot), and near vertical (over 75) at the end.
        var last = path.FindLastIndex(x => x.Altitude > p.LandHeight);
        var end = Step(path, last);
        Assert.True(-end.Z > 3 * end.H, $"the last stretch {end}"); // tan 75 = 3.7: over 71 at least
        if (cells >= 1200)
        {
            var sep = path.FindIndex(x => x.Phase == TerminalPhase.Dive);
            var before = Step(path, sep - 1);
            Assert.True(-before.Z * 1000 / before.H > 1732, $"at the separation point {before}"); // over 60 degrees
        }
    }

    // Step: the tick's move into point i over the ground and up (raw, the height in the same raw unit: 1,024 a cell).
    private static (long H, long Z) Step(List<FlightPoint> path, int i) =>
        ((path[i].Pos - path[i - 1].Pos).Len().Raw, path[i].Altitude.Raw - path[i - 1].Altitude.Raw);

    [Fact]
    public void TheDrawnPathIsTheFlight()
    {
        var (w, p) = Fly(800);
        var start = w.Tick;
        var impact = w.ImpactTickOf(p)!.Value;
        var drawn = new List<FlightPoint>();
        for (var k = start; k <= impact; k++)
        {
            drawn.Add(w.FlightPointAt(p, k)!.Value);
        }
        Detonation? blast = null;
        for (var k = start; blast is null; k++)
        {
            var at = drawn[(int)(k - start)];
            Assert.Equal((at.Pos, at.Altitude), (p.Pos, p.Altitude));
            var tick = w.Tick;
            w.Step();
            blast = w.Detonations.Cast<Detonation?>().FirstOrDefault(d => d!.Value.Projectile == p.Id);
            if (blast is not null)
            {
                Assert.Equal(impact, tick);
            }
        }
        Assert.True((blast!.Value.At - new Cell(805, Y).Center()).Len() <= Fixed.FromInt(3)); // its scatter: up to 2 cells an axis
    }

    [Fact]
    public void ItIsTheSameEveryRun()
    {
        static (ulong Hash, Vec At, uint Tick) Run(ulong seed)
        {
            var (w, t) = Range(1300, seed: seed);
            Ground(w, t, new Cell(1205, Y));
            var f = Watch(w, Deploy + 330);
            var b = f.Blasts.Single();
            return (w.Hash(), b.D.At, b.Tick);
        }
        Assert.Equal(Run(7), Run(7));
        Assert.Equal(Run(42), Run(42));
    }

    // ---- Damage ----

    [Fact]
    public void FullWithinEightCellsThirtyPercentOutToSixteen()
    {
        var (w, t) = Range(420);
        var aim = new Cell(400, Y);
        var inner = w.Spawn(2, "worker", 400, Y + 3);  // 3 cells off: inside 8 with any scatter (2 an axis)
        var outer = w.Spawn(2, "worker", 400, Y + 12); // 12 cells off: between 8 and 16 with any scatter
        var outside = w.Spawn(2, "worker", 400, Y + 19); // 19 cells off: outside 16
        foreach (var u in new[] { inner, outer, outside })
        {
            u.HP = 1_000_000;
        }
        Ground(w, t, aim);
        var f = Watch(w, Deploy + 200);
        Assert.Single(f.Blasts);
        Assert.Equal(1_000_000 - 2500, inner.HP);
        Assert.Equal(1_000_000 - 750, outer.HP);
        Assert.Equal(1_000_000, outside.HP);
    }

    [Fact]
    public void ArmourAndTheBiggestBuildingFallToOneMissile()
    {
        var (w, t) = Range(420);
        var tank = w.Spawn(2, "infantry_carrier", 400, Y + 4);
        var depot = w.Spawn(2, "supply_depot", 400, Y - 6);
        depot.HP = 6000; // the heavy hangar's HP, the most of any building
        Ground(w, t, new Cell(400, Y));
        Watch(w, Deploy + 200);
        Assert.False(tank.Alive);
        Assert.False(depot.Alive);
    }

    [Fact]
    public void ItSetsTheForestInsideItsEightCellRingOnFire()
    {
        const int n = 420;
        var rules = TerrainLoader.LoadRules(System.IO.Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        var flags = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 360; x < n; x++)
            {
                flags[y * n + x] = 3; // dense forest round the point
            }
        }
        var w = Arena.NewSentinel(n);
        w.AttachTerrain(new TerrainMap(n, n, new int[(n + 1) * (n + 1)], flags, rules));
        var (t, _) = w.SpawnCrewed(1, "jericho_truck", 5, Y, Facing.East);
        Ground(w, t, new Cell(400, Y));
        var lit = new List<FireEvent>();
        var f = Watch(w, Deploy + 200, ww => lit.AddRange(ww.FireEvents.Where(e => e.Kind == FireEventKind.Ignited)));
        var at = f.Blasts.Single().D.At;
        var ring = lit.Where(e => (e.Cell.Center() - at).Len().Raw <= 9 * Fixed.OneRaw).ToList();
        Assert.True(ring.Count >= 120, $"{ring.Count} cells lit round the blast"); // the forest within 8 cells: about 200
        Assert.All(ring, e => Assert.True(e.Spreads));
    }

    // ---- Interception ----

    [Fact]
    public void ThePatriotAndThaadTypesWouldEngageItTheS400TypeAndShortRangeDefenceNever()
    {
        var c = TestContent.Mvp;
        foreach (var id in new[] { "patriot_missiles", "thaad_missiles" })
        {
            Assert.True(c.Component(id)!.Effects.OfType<WeaponEffect>().Single().Interceptor!.Engages(InterceptClass.Ballistic), id);
        }
        foreach (var id in new[] { "s400_missile", "igla", "sam_missiles", "kub_missiles" })
        {
            var ic = c.Component(id)!.Effects.OfType<WeaponEffect>().Single(e => e.Interceptor is not null).Interceptor!;
            Assert.False(ic.Engages(InterceptClass.Ballistic), id);
        }

        // Live: an S-400 type battery (radar and launcher set up) at its point fires nothing at it, and dies.
        var w = Arena.NewSentinel(1100);
        var radar = w.SpawnCrewed(2, "s400_radar", 1000, Y + 6).Vehicle;
        var launcher = w.SpawnCrewed(2, "s400_launcher", 1000, Y).Vehicle;
        w.Order(2, CommandKind.Deploy, [radar, launcher]);
        w.Run(620);
        var (t, _) = w.SpawnCrewed(1, "jericho_truck", 5, Y, Facing.East);
        Ground(w, t, new Cell(1000, Y + 3));
        var interceptors = 0;
        var f = Watch(w, Deploy + 300, ww => interceptors += ww.Projectiles.Count(p => p.Weapon.Effect.Interceptor is not null));
        Assert.Single(f.Launches);
        Assert.Equal(0, interceptors);
        Assert.False(radar.Alive);
        Assert.False(launcher.Alive);
    }

    // ---- Counterplay ----

    [Fact]
    public void EveryEnemyIsWarnedTenSecondsBeforeTheImpact()
    {
        var (w, t) = Range(1300);
        Ground(w, t, new Cell(1205, Y));
        var strikes = new List<(uint Tick, IncomingStrike S)>();
        var f = Watch(w, Deploy + 330, ww => strikes.AddRange(ww.IncomingStrikes.Select(s => (ww.Tick, s))));
        var id = f.Launches.Single().Id;
        var launched = f.Events.Single(e => e.E.Kind == IncomingEventKind.Launched).Tick;
        var warned = f.Events.Single(e => e.E.Kind == IncomingEventKind.Warned);
        var struck = f.Events.Single(e => e.E.Kind == IncomingEventKind.Struck).Tick;
        Assert.True(warned.Tick - launched > 50, "a long flight: warned well after the launch");
        Assert.Equal((uint)Warn, struck - warned.Tick);
        Assert.Equal(struck, warned.E.ImpactTick);
        Assert.Equal(1u << 2, warned.E.Warned); // every player but the shooter's
        var mine = strikes.Where(s => s.S.Projectile == id).ToList();
        Assert.Equal(Warn, mine.Count);
        Assert.All(mine, s => Assert.Equal((struck, Fixed.FromInt(16)), (s.S.ImpactTick, s.S.Radius)));
    }

    [Fact]
    public void ALaunchShowsTheTruckToEveryEnemyForThirtySeconds()
    {
        var (w, t) = Range(420, fog: true);
        var watcher = w.Spawn(2, "worker", 200, Y + 80); // far off: it never sees the truck itself
        w.Run(3);
        Assert.False(World.IsSpotted(t, 2));
        Ground(w, t, new Cell(400, Y));
        var f = Watch(w, Deploy + 10);
        var first = f.Launches.Single().Tick;
        Assert.True(World.IsSpotted(t, 2));
        Assert.True(w.RevealedBy(t, 2));
        w.Run(Reveal - (int)(w.Tick - first) - 2);
        Assert.True(w.RevealedBy(t, 2));
        w.Run(20);
        Assert.False(w.RevealedBy(t, 2));
        Assert.False(World.IsSpotted(t, 2));
        Assert.True(watcher.Alive);
    }

    // ---- Reload ----

    [Theory]
    [InlineData("supply_depot")]
    [InlineData("facp")]
    public void ItReloadsPackedUpAtADepotOrAFieldAmmoPoint(string store)
    {
        var (w, t) = Range(420);
        var depot = w.Spawn(1, store, 3, Y - 10);
        depot.Stock = 3000;
        Ground(w, t, new Cell(400, Y));
        w.Run(Deploy + 40);
        Assert.Equal(0, t.Charges[0]);
        Assert.NotEqual(DeployState.Stowed, t.Deploy);
        // Out of missiles beside a depot it packs up first (15 s), never reloading set up; then 180 s.
        var packing = 0;
        while (t.Deploy != DeployState.Stowed)
        {
            w.Step();
            packing++;
            Assert.Equal(0, t.Charges[0]);
        }
        Assert.InRange(packing, 1, Stow);
        var stowed = w.Tick;
        while (t.Charges[0] == 0 && w.Tick < stowed + Reload + 100)
        {
            w.Step();
        }
        Assert.InRange(w.Tick - stowed, (uint)Reload - 1, (uint)Reload); // (the count runs from the tick it is stowed)
        Assert.Equal(1, t.Charges[0]);
        Assert.Equal(3000 - Cost, depot.Stock);
    }

    [Fact]
    public void NotAwayFromADepotNorFromAPoorOne()
    {
        // Away from every depot (the vehicle garage and a forward airbase's stock are not depots): never.
        var (w2, t2) = Range(420);
        w2.Spawn(1, "vehicle_garage", 20, Y - 30);
        t2.MutableCharges[0] = 0;
        w2.Run(Reload + 200);
        Assert.Equal(0, t2.Charges[0]);
        // A depot with less than 1,000 in stock: never.
        var (w3, t3) = Range(420);
        var poor = w3.Spawn(1, "supply_depot", 3, Y - 10);
        poor.Stock = Cost - 1;
        t3.MutableCharges[0] = 0;
        w3.Run(Reload + 200);
        Assert.Equal(0, t3.Charges[0]);
        Assert.Equal(Cost - 1, poor.Stock);
    }

    [Fact]
    public void TheMilitaryGarageServiceDoesNotReloadIt()
    {
        var w = Arena.NewSentinel(96);
        var g = w.Spawn(1, "military_garage", 20, 10);
        w.Step();
        var t = w.SpawnCrewed(1, "jericho_truck", 40, 36).Vehicle;
        var sam = w.SpawnCrewed(1, "sam_vehicle", 30, 36).Vehicle; // a control: the garage reloads it
        t.MutableCharges[0] = 0;
        t.HP = 400;
        var samStock = sam.Def.Weapons[0].Stock;
        sam.MutableCharges[samStock] = 0;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Service, Units = [t.Id, sam.Id], Target = g.Id });
        var wasIn = false;
        for (var i = 0; i < 3000 && (!wasIn || t.InGarage); i++)
        {
            w.Step();
            wasIn |= t.InGarage;
        }
        Assert.True(wasIn, "it went in (to be repaired)");
        Assert.False(t.InGarage);
        Assert.Equal(t.Def.MaxHp, t.HP); // repaired
        Assert.Equal(0, t.Charges[0]);   // not reloaded
        w.Run(400);
        Assert.Equal(sam.Def.StockMax[samStock], sam.Charges[samStock]);
    }
}
