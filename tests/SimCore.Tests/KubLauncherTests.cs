using SimCore.FixedPoint;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The Resistance SA-6 launcher (kub_launcher; docs/design.md, "SA-6 launcher"; a 2K12 Kub type): a separated battery. It has
// no radar of its own: set up (5 s; packs up in 4 s) it fires only at an air target that a set-up mobile radar of its side
// within 60 cells of it tracks (the battery link), out to 350 cells of slant, and its semi-active missile needs that track all
// the way to the fuse. 3 missiles, about 1.3x the Osa's kill chance and share of HP a hit; a depot reloads it packed up, one
// missile every 20 s. Player 1 is the Sentinel (the aircraft), player 2 the Resistance.
public class KubLauncherTests(ITestOutputHelper output)
{
    private const int N = 700;
    private const int Y = 350;
    private const string Multirole = "Multirole 500 lb";
    private const string Bomber = "strategic_bomber";

    private static World NewWorld(ulong seed = 1) => Arena.NewSentinel(N, seed);

    // SetUp orders units (player 2's) to deploy and runs until all are set up.
    private static void SetUp(World w, params Unit[] units)
    {
        w.Order(2, CommandKind.Deploy, units);
        w.Run(units.Max(u => u.Def.Archetype.DeployTicks) + 2);
        Assert.All(units, u => Assert.Equal(DeployState.Deployed, u.Deploy));
    }

    private static Unit Radar(World w, int x, int y = Y) => w.SpawnCrewed(2, "mobile_radar", x, y).Vehicle;

    private static Unit Kub(World w, int x, int y = Y, int facing = Facing.North) => w.SpawnCrewed(2, "kub_launcher", x, y, facing).Vehicle;

    // Jet: player 1's jet in flight over (x, y) at the given height in metres (by default 170 m: 85 cells up), ordered to orbit
    // there (a Move).
    private static Unit Jet(World w, string name, int x, int y = Y, int metres = 170)
    {
        var j = w.Spawn(1, name, x, y);
        j.Altitude = Fixed.FromRaw(metres * 512L);
        w.Order(1, CommandKind.Move, [j], cell: new Cell(x, y));
        return j;
    }

    // BomberOver: player 1's strategic bomber in flight over (x, y) at its 400 m, ordered to orbit there.
    private static Unit BomberOver(World w, int x, int y = Y)
    {
        var b = w.Spawn(1, Bomber, x, y, Facing.East);
        b.MutableSalvos[0] = 0; // no flares (radar missiles anyway; chaff is not its)
        w.Order(1, CommandKind.Move, [b], cell: new Cell(x, y));
        return b;
    }

    private static long SlantCells(Unit from, Unit jet, long eye) =>
        World.Len3(jet.Pos.X.Raw - from.Pos.X.Raw, jet.Pos.Y.Raw - from.Pos.Y.Raw, jet.Altitude.Raw - eye) / Fixed.OneRaw;

    private static IEnumerable<Projectile> Missiles(World w, Unit shooter) =>
        w.Projectiles.Where(p => p.Shooter == shooter.Id && p.Weapon.Effect.Interceptor is not null);

    // FirstShot runs up to ticks and returns shooter's first air defence missile (null if it fired none).
    private static Projectile? FirstShot(World w, Unit shooter, int ticks)
    {
        for (var t = 0; t < ticks; t++)
        {
            w.Step();
            if (Missiles(w, shooter).FirstOrDefault() is { } m)
            {
                return m;
            }
        }
        return null;
    }

    // Follow runs until missile m is gone (at most ticks), returning its burst (null if none) and whether it lost guidance
    // (World.BatteryLost) and on which tick.
    private static (AirBurst? Burst, int LostAt) Follow(World w, Projectile m, int ticks, Action<int>? each = null)
    {
        var lostAt = -1;
        for (var t = 0; t < ticks; t++)
        {
            each?.Invoke(t);
            w.Step();
            if (w.BatteryLost.Contains(m.Id))
            {
                Assert.Equal(-1, lostAt);
                lostAt = t;
            }
            if (w.AirBursts.FirstOrDefault(b => b.Missile == m.Id) is { Missile: > 0 } burst)
            {
                return (burst, lostAt);
            }
        }
        return (null, lostAt);
    }

    // ---- Content ----

    [Fact]
    public void ItIsData()
    {
        var c = TestContent.Mvp;
        var k = c.Archetype("kub_launcher")!;
        var osa = c.Archetype("sam_vehicle")!;
        Assert.Equal(osa.MaxHp, k.MaxHp); // the owner: the same HP as the Osa
        Assert.Equal(c.Archetype("mobile_radar")!.Speed, k.Speed); // the radar's tracked chassis and speed
        Assert.Equal(Model.Locomotor.Tracked, k.Locomotor);
        Assert.Equal((100, 80), (k.DeployTicks, k.StowTicks)); // sets up in 5 s, packs up in 4 s
        Assert.Equal(0, k.AirVision.Raw); // no radar of its own
        Assert.Equal(900, k.BuildTime); // 45 s
        Assert.True(c.Faction("resistance")!.Archetypes[k.Index]);
        Assert.False(c.Faction("sentinel")!.Archetypes[k.Index]);
        Assert.True(k.Tags.Matches(c.Archetype("vehicle_garage")!.Production!.Tags), "built at the vehicle garage");

        var w = NewWorld();
        var kd = w.Player(2)!.Setup.Find(w.Content, "kub_launcher")!;
        var od = w.Player(2)!.Setup.Find(w.Content, "sam_vehicle")!;
        Assert.Equal(2000, kd.Cost); // 1,800 + 200 for the missiles
        var kw = kd.Weapons[0];
        var ow = od.Weapons[0];
        var ic = kw.Effect.Interceptor!;
        Assert.Equal(3, kw.Effect.Ammo);
        Assert.Equal((Fixed.FromInt(350), Fixed.FromInt(15)), (kw.Range, kw.Effect.MinRange));
        Assert.Equal(Fixed.FromInt(220), ow.Range);
        Assert.Equal(55 * Fixed.OneRaw / 20, kw.Effect.ProjectileSpeed.Raw); // 55 cells/s (the Osa's 45)
        Assert.True(kw.Effect.ProjectileSpeed > ow.Effect.ProjectileSpeed);
        Assert.Equal(Model.Seeker.Radar, ic.Seeker); // chaff answers it
        Assert.Equal(0, ic.CeilingRaw); // no ceiling (the Osa has none either): its slant reach gives it the height
        Assert.False(ic.RadarLinked);
        var b = ic.Battery!;
        Assert.Equal(Fixed.FromInt(60).Raw, b.LinkRadiusRaw);
        Assert.True(b.SemiActive);
        Assert.True(c.Archetype("mobile_radar")!.Tags.HasAll(b.RadarTags));
        Assert.Equal(new Model.DepotReload(400, 20, 200, true), kw.Effect.DepotReload); // one every 20 s for 200, packed up
        Assert.Null(od.Weapons[0].Effect.Interceptor!.Battery); // the Osa keeps its own radar
    }

    [Theory]
    [InlineData("\"battery\": { \"linkRadius\": 600,", "\"radarLinked\": true, \"battery\": { \"linkRadius\": 600,", "exclusive")]
    [InlineData("\"radarTags\": [\"radar\"]", "\"radarTags\": []", "radarTags is empty")]
    [InlineData("\"linkRadius\": 600", "\"linkRadius\": 0", "linkRadius")]
    [InlineData("\"radarTags\": [\"radar\"]", "\"radarTags\": [\"no_such_tag\"]", "unknown tag")]
    public void BadBatteryContentIsRefused(string from, string to, string error)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Name.EndsWith("components/vehicle_weapons.json", StringComparison.Ordinal));
        Assert.Contains(from, sources[i].Json, StringComparison.Ordinal);
        sources[i] = new Content.ContentSource(sources[i].Name, sources[i].Json.Replace(from, to, StringComparison.Ordinal));
        var e = Assert.ThrowsAny<Exception>(() => Content.ContentLoader.Load(sources));
        Assert.Contains(error, e.Message, StringComparison.Ordinal);
    }

    // ---- The battery link: it fires only on a linked radar's track ----

    [Fact]
    public void WithoutALinkedRadarItHoldsFire()
    {
        // An Osa of its side tracks the jet with its own radar (team cueing) and a mobile radar stands beside it packed up: the
        // launcher has nothing to fire on. Once the radar is set up it fires.
        var w = NewWorld();
        var k = Kub(w, 100);
        var osa = w.SpawnCrewed(2, "sam_vehicle", 100, Y + 20).Vehicle;
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [osa.Id], Stance = Stance.HoldFire });
        var r = Radar(w, 110, Y - 10);
        SetUp(w, k);
        var j = Jet(w, Multirole, 220);
        var trackedBySide = 0;
        for (var t = 0; t < 600; t++)
        {
            w.Step();
            trackedBySide += w.AirTracked(2, j.Id) ? 1 : 0;
            Assert.Equal(0u, k.AirTarget);
            Assert.Empty(Missiles(w, k));
        }
        Assert.True(trackedBySide > 500, $"premise: its side tracked the jet {trackedBySide} ticks (the Osa's radar)");
        Assert.Null(w.BatteryRadar(k));
        Assert.Empty(w.BatteryLaunchers(r));
        Assert.Equal(3, k.Charges[0]);

        SetUp(w, r);
        Assert.Equal(r, w.BatteryRadar(k));
        Assert.Equal([k], w.BatteryLaunchers(r));
        Assert.NotNull(FirstShot(w, k, 200));
    }

    [Fact]
    public void PackedUpItHoldsFire()
    {
        var w = NewWorld();
        var r = Radar(w, 110, Y - 10);
        var k = Kub(w, 100);
        SetUp(w, r);
        var j = Jet(w, Multirole, 220);
        Assert.Null(FirstShot(w, k, 400));
        Assert.True(w.AirTracked(2, j.Id));
        Assert.Equal(r, w.BatteryRadar(k)); // linked, but packed up it cannot fire
        w.Order(2, CommandKind.Deploy, [k]);
        var start = w.Tick;
        var shot = FirstShot(w, k, 300);
        Assert.NotNull(shot);
        output.WriteLine($"set up and fired {w.Tick - start} ticks after the order");
        Assert.True(w.Tick - start > k.Def.Archetype.DeployTicks, "not before it is set up (5 s)");
    }

    [Theory]
    [InlineData(58, true)]
    [InlineData(62, false)]
    public void ItLinksOnlyToARadarWithinSixtyCells(int dist, bool fires)
    {
        var w = NewWorld();
        var r = Radar(w, 100, Y - dist);
        var k = Kub(w, 100);
        SetUp(w, r, k);
        var j = Jet(w, Multirole, 300);
        var shot = FirstShot(w, k, 600);
        Assert.True(w.AirTracked(2, j.Id), "the radar tracks the jet either way");
        Assert.Equal(fires, shot is not null);
        Assert.Equal(fires ? r : null, w.BatteryRadar(k));
    }

    // ---- Reach: 350 cells against the Osa's 220 ----

    [Fact]
    public void ItOutReachesTheOsa()
    {
        // A multirole orbits about 300 cells out, 85 cells up: the launcher fires at it; the Osa beside it, cued by the same
        // radar, never does (its missile reaches 220).
        var w = NewWorld();
        var r = Radar(w, 60, Y - 10);
        var k = Kub(w, 60);
        var osa = w.SpawnCrewed(2, "sam_vehicle", 60, Y + 10).Vehicle;
        SetUp(w, r, k);
        var j = Jet(w, Multirole, 360);
        long osaNearest = long.MaxValue;
        Projectile? shot = null;
        for (var t = 0; t < 800 && shot is null; t++)
        {
            w.Step();
            osaNearest = Math.Min(osaNearest, SlantCells(osa, j, w.RadarEyeRaw(osa)));
            shot = Missiles(w, k).FirstOrDefault();
            Assert.Empty(Missiles(w, osa));
        }
        Assert.NotNull(shot);
        var launch = shot.LaunchRange.Raw / Fixed.OneRaw;
        output.WriteLine($"launched at {launch} cells of slant; the Osa's nearest {osaNearest}");
        Assert.InRange(launch, 221, 350);
        Assert.True(osaNearest > 220, $"premise: {osaNearest}");
        Assert.Equal(10, osa.Charges[0]);
    }

    [Fact]
    public void ItReachesTheBomberHigherAndFurtherOut()
    {
        // The strategic bomber orbits 400 m (200 cells) up: within the Osa's 220 of slant only within about 92 cells of ground,
        // within the launcher's 350 out to about 287.
        var w = NewWorld();
        var r = Radar(w, 60, Y - 10);
        var k = Kub(w, 60);
        var osa = w.SpawnCrewed(2, "sam_vehicle", 60, Y + 10).Vehicle;
        SetUp(w, r, k);
        var b = BomberOver(w, 300);
        long osaNearest = long.MaxValue;
        Projectile? shot = null;
        for (var t = 0; t < 2000 && shot is null; t++)
        {
            w.Step();
            osaNearest = Math.Min(osaNearest, SlantCells(osa, b, w.RadarEyeRaw(osa)));
            shot = Missiles(w, k).FirstOrDefault();
            Assert.Empty(Missiles(w, osa));
        }
        Assert.NotNull(shot);
        var level = World.Len3(b.Pos.X.Raw - k.Pos.X.Raw, b.Pos.Y.Raw - k.Pos.Y.Raw, 0) / Fixed.OneRaw;
        var height = b.Altitude.Raw / 512; // a flat arena
        output.WriteLine($"launched at {shot.LaunchRange.Raw / Fixed.OneRaw} cells of slant, {level} level, the bomber {height} m up; the Osa's nearest {osaNearest}");
        Assert.InRange(height, 380, 420);
        Assert.True(level > 150, $"{level} cells out over the ground");
        Assert.True(osaNearest > 220, $"premise: {osaNearest}");
    }

    // ---- Semi-active homing: the radar must track the target all the way ----

    [Theory]
    [InlineData("none")]     // the radar tracks it to the fuse
    [InlineData("stow")]     // the radar packs up 1 s after the launch: the missile is lost that tick
    [InlineData("destroy")]  // the radar is destroyed 1 s after the launch: likewise
    [InlineData("second")]   // a second linked radar keeps tracking when the first packs up: still guided
    [InlineData("launcher")] // the launcher is destroyed in flight: the radar still guides it (the link counts from the launch point)
    public void TheMissileIsLostWhenNoLinkedRadarTracksTheTarget(string mode)
    {
        var w = NewWorld();
        var r = Radar(w, 100, Y - 10);
        var k = Kub(w, 100);
        Unit[] units = mode == "second" ? [r, k, Radar(w, 100, Y + 30)] : [r, k];
        SetUp(w, units);
        var j = Jet(w, Multirole, 350);
        var m = FirstShot(w, k, 1200);
        Assert.NotNull(m);
        var (burst, lost) = Follow(w, m, 1000, t =>
        {
            if (t == 20 && mode is "stow" or "second")
            {
                w.Order(2, CommandKind.Stow, [r]);
            }
            if (t == 20 && mode == "destroy")
            {
                r.HP = 0;
            }
            if (t == 20 && mode == "launcher")
            {
                k.HP = 0;
            }
        });
        Assert.NotNull(burst);
        output.WriteLine($"{mode}: launched at {m.LaunchRange.Raw / Fixed.OneRaw} cells; lost at {lost}; {burst}");
        if (mode is "stow" or "destroy")
        {
            Assert.Equal(20, lost); // the tick the radar stopped tracking
            Assert.False(m.Guided);
            Assert.True(burst.Value.Kind is AirBurstKind.SelfDestruct or AirBurstKind.Ground, $"{burst.Value.Kind}: it cannot fuse");
            Assert.True(j.Alive);
        }
        else
        {
            Assert.Equal(-1, lost);
            Assert.True(burst.Value.Kind is AirBurstKind.Kill or AirBurstKind.Miss or AirBurstKind.Hit, $"{burst.Value.Kind}: it reached the jet");
        }
    }

    [Fact]
    public void ALostMissileDoesNotHoldTheTarget()
    {
        // Lost, the missile no longer flies at the jet for its side: once a radar tracks it again the launcher fires anew.
        var w = NewWorld();
        var r = Radar(w, 100, Y - 10);
        var k = Kub(w, 100);
        SetUp(w, r, k);
        var j = Jet(w, Multirole, 350);
        var m = FirstShot(w, k, 1200)!;
        w.Order(2, CommandKind.Stow, [r]);
        w.Step();
        Assert.False(m.Guided);
        w.Run(r.Def.Archetype.StowTicks + 2);
        SetUp(w, r);
        var next = Missiles(w, k).FirstOrDefault(p => p.Id != m.Id) ?? FirstShotOtherThan(w, k, m, 400);
        Assert.NotNull(next);
        Assert.Equal(j.Id, next.Target);
        Assert.Equal(1, k.Charges[0]);
    }

    private static Projectile? FirstShotOtherThan(World w, Unit shooter, Projectile not, int ticks)
    {
        for (var t = 0; t < ticks; t++)
        {
            w.Step();
            if (Missiles(w, shooter).FirstOrDefault(p => p.Id != not.Id) is { } m)
            {
                return m;
            }
        }
        return null;
    }

    // ---- Damage: about 1.3x the Osa's ----

    [Theory]
    [InlineData("Multirole 500 lb", 1300)] // killBest 91 against 70
    [InlineData("fighter_jet", 1300)]      // 91 against 70
    [InlineData("attack_jet", 1266)]       // 95 against 75 (capped at its 95)
    [InlineData("gunship", 1187)]          // 95 against 80 (capped)
    [InlineData(Bomber, 1117)]             // 95 against 85 (capped)
    public void ItsKillChanceIsAboutOnePointThreeTimesTheOsas(string aircraft, int permilleOfOsa)
    {
        // The same shot (from the launcher's point, the same velocities, crossing, tracking, at no range) through both kill
        // models: the launcher's chance over the Osa's, permille.
        var w = NewWorld();
        var k = Kub(w, 100);
        var osa = w.SpawnCrewed(2, "sam_vehicle", 100, Y + 4).Vehicle;
        var a = w.Spawn(1, aircraft, 200, Y);
        w.Step();
        int Pk(Unit s)
        {
            var wd = s.Def.Weapons[0];
            var m = new Projectile(1_000_000, s, wd, a.Pos, a.Id)
            {
                Vel = new Vec(Fixed.FromRaw(200), Fixed.Zero), LaunchRange = Fixed.Zero, LaunchCross = 0, Tracked = 20,
            };
            return w.KillPermille(m, new AirBody(a), wd.Effect.Interceptor!);
        }
        var (kp, op) = (Pk(k), Pk(osa));
        output.WriteLine($"{aircraft}: launcher {kp}, Osa {op} permille");
        Assert.InRange(kp * 1000L / op, permilleOfOsa - 15, permilleOfOsa + 15);
    }

    [Fact]
    public void FiveHitsBringTheBomberDownAgainstTheOsasSix()
    {
        // Each hit takes 22% of the bomber's 2,000 HP (the Osa's 17%, StrategicBomberTests: 6 hits).
        var w = NewWorld(seed: 3);
        var r = Radar(w, 150, 150);
        var ks = Enumerable.Range(0, 4).Select(i => Kub(w, 140 + 8 * i, 160)).ToArray();
        SetUp(w, [r, .. ks]);
        var b = BomberOver(w, 150, 150);
        var hp = new List<int>();
        var bursts = new List<AirBurstKind>();
        for (var t = 0; t < 6000 && b.Alive; t++)
        {
            w.Step();
            foreach (var x in w.AirBursts.Where(x => x.Target == b.Id && x.Kind is AirBurstKind.Hit or AirBurstKind.Kill))
            {
                bursts.Add(x.Kind);
                hp.Add(b.HP);
            }
        }
        output.WriteLine("hits: " + string.Join(", ", bursts.Zip(hp, (kd, h) => $"{kd} {h}")) + $"; missiles left {ks.Sum(k => k.Charges[0])}");
        Assert.False(b.Alive);
        Assert.Equal([.. Enumerable.Repeat(AirBurstKind.Hit, 4), AirBurstKind.Kill], bursts);
        Assert.Equal([1560, 1120, 680, 240, 0], hp);
    }

    // ---- Three missiles, then the depot ----

    [Fact]
    public void ThreeMissilesThenItIsEmpty()
    {
        var w = NewWorld();
        var r = Radar(w, 100, Y - 10);
        var k = Kub(w, 100);
        SetUp(w, r, k);
        var b = BomberOver(w, 200);
        var launches = new List<(uint Id, uint Tick)>();
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            foreach (var m in Missiles(w, k))
            {
                if (!launches.Exists(l => l.Id == m.Id))
                {
                    launches.Add((m.Id, w.Tick));
                }
            }
        }
        output.WriteLine("launch ticks: " + string.Join(", ", launches.Select(l => l.Tick)));
        Assert.Equal(3, launches.Count);
        Assert.Equal(0, k.Charges[0]);
        Assert.True(launches[1].Tick - launches[0].Tick >= 40 && launches[2].Tick - launches[1].Tick >= 40, "2 s apart at least");
    }

    [Fact]
    public void ADepotReloadsItPackedUpOneMissileEveryTwentySeconds()
    {
        var w = NewWorld();
        var depot = w.Spawn(2, "supply_depot", 100, Y + 3);
        var k = Kub(w, 100);
        SetUp(w, k);
        k.MutableCharges[0] = 0;
        var changes = new List<(int Tick, int Charges, int Stock)>();
        var stowedAt = -1;
        for (var t = 1; t <= 1400; t++)
        {
            var before = k.Charges[0];
            w.Step();
            if (stowedAt < 0 && k.Deploy == DeployState.Stowed)
            {
                stowedAt = t;
            }
            if (k.Charges[0] != before)
            {
                changes.Add((t, k.Charges[0], depot.Stock));
            }
        }
        output.WriteLine($"stowed at {stowedAt}; " + string.Join(", ", changes));
        Assert.InRange(stowedAt, 80, 83); // idle at the depot with nothing to fire at, it packs up (4 s) to reload
        Assert.Equal(3, changes.Count);
        Assert.Equal([1, 2, 3], changes.Select(c => c.Charges));
        Assert.Equal([800, 600, 400], changes.Select(c => c.Stock)); // 200 supply a missile
        Assert.Equal(400, changes[1].Tick - changes[0].Tick); // one every 20 s
        Assert.Equal(400, changes[2].Tick - changes[1].Tick);
        Assert.InRange(changes[0].Tick - stowedAt, 399, 402);
    }

    [Fact]
    public void SetUpAwayFromADepotItNeverReloads()
    {
        var w = NewWorld();
        w.Spawn(2, "supply_depot", 100, Y + 80);
        var k = Kub(w, 100);
        SetUp(w, k);
        k.MutableCharges[0] = 0;
        w.Order(2, CommandKind.Stow, [k]);
        w.Run(1500);
        Assert.Equal(0, k.Charges[0]);
    }

    [Fact]
    public void AtADepotItStaysSetUpWhileItTracksATarget()
    {
        // Short of missiles at a depot it would pack up to reload, but not while it tracks: it fires all three at the bomber.
        var w = NewWorld();
        w.Spawn(2, "supply_depot", 100, Y + 3);
        var r = Radar(w, 100, Y - 10);
        var k = Kub(w, 100);
        SetUp(w, r, k);
        var b = BomberOver(w, 200);
        var fired = 0;
        for (var t = 0; t < 3000 && k.Charges[0] > 0; t++)
        {
            w.Step();
            if (k.Charges[0] < 3)
            {
                fired = 3 - k.Charges[0];
                Assert.True(k.Deploy == DeployState.Deployed || !b.Alive, $"tick {t}: {k.Deploy} with {k.Charges[0]} left");
            }
        }
        Assert.Equal(3, fired);
    }

    // ---- Chaff answers it ----

    [Fact]
    public void JetsAnswerItWithChaff()
    {
        var w = NewWorld();
        var r = Radar(w, 100, Y - 10);
        var k = Kub(w, 100);
        SetUp(w, r, k);
        var j = Jet(w, Multirole, 350);
        var m = FirstShot(w, k, 1200)!;
        var chaff = j.Salvos.ToArray();
        var (burst, _) = Follow(w, m, 1000);
        output.WriteLine($"decoys {m.Decoys}, {m.DecoyPercent}%; salvos {string.Join("/", chaff)} -> {string.Join("/", j.Salvos)}; {burst}");
        Assert.True(m.Decoys > 0, "the jet dispensed chaff against it");
    }

    // ---- Determinism ----

    [Fact]
    public void ItIsDeterministic()
    {
        static (List<ulong> Hashes, int Missiles) Play()
        {
            var w = NewWorld(seed: 7);
            var r = Radar(w, 100, Y - 10);
            var r2 = Radar(w, 100, Y + 40);
            var k = Kub(w, 100);
            var k2 = Kub(w, 120, Y + 20);
            SetUp(w, r, r2, k, k2);
            Jet(w, Multirole, 350);
            BomberOver(w, 250, Y + 60);
            var hashes = new List<ulong>();
            var ids = new List<uint>();
            for (var t = 0; t < 1500; t++)
            {
                if (t == 300)
                {
                    w.Order(2, CommandKind.Stow, [r]);
                }
                w.Step();
                hashes.Add(w.Hash());
                ids.AddRange(w.Projectiles.Where(p => p.Weapon.Effect.Interceptor is not null && !ids.Contains(p.Id)).Select(p => p.Id));
            }
            return (hashes, ids.Count);
        }
        var a = Play();
        var b = Play();
        Assert.True(a.Missiles >= 3, $"premise: {a.Missiles} missiles flew");
        Assert.Equal(a.Missiles, b.Missiles);
        Assert.Equal(a.Hashes, b.Hashes);
    }
}
