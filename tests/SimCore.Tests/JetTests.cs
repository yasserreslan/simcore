using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The two jets and the pilot as content (E8; the plan's sections 3 and 5): the multirole fighter
// (multirole_jet, an F-16 type) and the air superiority fighter (fighter_jet, an F-22 type) fly on the
// fixed-wing model (E4) under the mission layer (E3), are built by the airfield facility into a free
// station (decision D9), fly every mission, go home at bingo and on a Rearm order through their
// airfield, and carry their loadouts (the multirole's two bomb presets, D18) as data: firing is E6
// (air-to-air) and E7 (air-to-ground). The pilot is a weak soldier spawned later by an ejection (E10).
public class JetTests
{
    private const int N = 240;
    private static readonly string[] Jets = ["multirole_jet", "fighter_jet"];

    private static ContentSet C => TestContent.Mvp;

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    // Tenths of a cell a second, raw a second.
    private static long S(int tenths) => (long)tenths * Fixed.OneRaw / 10;

    private static Archetype A(string id) => C.Archetype(id)!;

    // Spawnable: the multirole has a bombs slot, so it is spawned by a preset (preset A).
    private static string Spawnable(string jet) => jet == "multirole_jet" ? "Multirole 500 lb" : jet;

    private static Unit Overhead(World w, string jet, int x, int y, byte player = 1)
    {
        var d = w.Spawn(player, Spawnable(jet), x, y, Facing.East);
        w.Step();
        return d;
    }

    private static void Give(World w, Unit d, AirTaskKind task, Cell cell, Cell? cell2 = null, bool queue = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = d.Owner, Kind = CommandKind.AirTask, Units = [d.Id], Mission = task, Cell = cell, Cell2 = cell2, Queue = queue });

    private static void Produce(World w, Unit factory, string unit) =>
        w.Issue(new Command { Tick = w.Tick, Player = factory.Owner, Kind = CommandKind.Produce, Units = [factory.Id], Unit = unit });

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

    // A one-row forward airfield with 4 runway segments (80 cells: both jets land on it) at (30, 120).
    private static (World W, Airfield Af, Unit Facility) Field(int size = N, ulong seed = 1)
    {
        var w = Arena.NewSentinel(size, seed);
        var af = AirfieldFlightTests.Field(w, [(0, 0)], at: new Cell(30, 120), east: 1);
        return (w, af, w.Unit(af.Facility)!);
    }

    private static AirfieldStation Station(Airfield af, StationKind kind) => af.Rows[0].Stations.Single(s => s.Kind == kind);

    // ---- Content ----

    [Fact]
    public void TheJetsAreClassMFixedWingsWithThePlansNumbers()
    {
        var m = A("multirole_jet");
        var f = A("fighter_jet");
        Assert.Equal(("Multirole fighter", "Air superiority fighter"), (m.Name, f.Name));
        foreach (var a in new[] { m, f })
        {
            Assert.True(a.Tags.Matches(C.TagsOf("jet")));
            Assert.False(a.Tags.Matches(C.TagsOf("drone"))); // a jet is an aircraft, not a drone (Guard's filter kinds)
            Assert.Equal(FlightKind.FixedWing, a.Flight!.Kind);
            Assert.Equal(C.ArmorClass("aircraft"), a.Armor);
            Assert.Equal((AircraftClass.M, LandingKind.Runway), (a.Aircraft!.SizeClass, a.Aircraft.Landing));
            Assert.Equal(C.ArmorClass("parked_aircraft"), a.Aircraft.GroundArmor);
            Assert.True(C.AirfieldType("forward_airfield")!.Serves(a.Aircraft.SizeClass));
            Assert.True(a.Aircraft.Span <= C.Archetype("airfield_shelter")!.Airfield!.MaxSpan, "it fits a loading shelter");
            Assert.True(C.Factions.Single(x => x.Id == "sentinel").Archetypes[a.Index]);
        }
        // HP, fuel, sensors, cost and build time.
        Assert.Equal((280, 320), (m.MaxHp, f.MaxHp));
        Assert.Equal((4200, 4800), (m.Lifetime, f.Lifetime)); // 3.5 and 4 minutes at cruise
        Assert.Equal((40L, 45L, 110L, 160L), (Cells(m.Vision.Raw), Cells(f.Vision.Raw), Cells(m.AirVision.Raw), Cells(f.AirVision.Raw)));
        Assert.Equal((2000, 900, 2400, 1000), (m.Cost, m.BuildTime, f.Cost, f.BuildTime));
        // The envelopes: cruise / attack / dash / stall, corner speed and radius, the margin.
        var mw = m.Flight!.Wing!;
        var fw = f.Flight!.Wing!;
        Assert.Equal((S(200), S(230), S(280), S(85)), (mw.Cruise, mw.Military, mw.Dash, mw.Min));
        Assert.Equal((S(240), S(300), S(90)), (fw.Military, fw.Dash, fw.Min));
        Assert.InRange(fw.Cruise, S(210) - 20, S(210)); // cruise is the archetype's speed, a whole raw step a tick
        Assert.Equal((S(150), S(160)), (mw.Corner, fw.Corner));
        Assert.InRange(Cells(mw.Radius(mw.Corner) * 10), 79, 80);  // 8 cells at corner speed
        Assert.InRange(Cells(fw.Radius(fw.Corner) * 10), 89, 90);  // 9
        Assert.InRange(Cells(mw.Radius(mw.Cruise) * 10), 141, 143); // 14.2 at cruise
        Assert.InRange(Cells(fw.Radius(fw.Cruise) * 10), 154, 156); // 15.5
        Assert.True(fw.Dash > mw.Dash && fw.Cruise > mw.Cruise, "the air superiority jet is faster");
        Assert.True(mw.Radius(mw.Corner) < fw.Radius(fw.Corner), "the multirole out-turns it at low speed");
        Assert.Equal((50L, 55L), (Cells(m.Flight.FlyZoneMarginRaw), Cells(f.Flight.FlyZoneMarginRaw))); // the fly zone (E12)
        // Patrol 140 and 168 m on maps up to 512 cells, 280 and 336 m at 1,024, at most 320 and 384 (E13, D-ALT1; 60 and 70 m before).
        Assert.Equal((140 * 512L, 168 * 512L), (m.Flight.HeightRaw, f.Flight.HeightRaw));
        Assert.Equal((140 * 512L, 210 * 512L, 280 * 512L, 320 * 512L), (m.Flight.HeightOn(256), m.Flight.HeightOn(768), m.Flight.HeightOn(1024), m.Flight.HeightOn(2048)));
        Assert.Equal((168 * 512L, 336 * 512L, 384 * 512L), (f.Flight.HeightOn(512), f.Flight.HeightOn(1024), f.Flight.HeightOn(4096)));
        Assert.Equal((70 * 512L, 80 * 512L), (mw.IngressRaw, fw.IngressRaw)); // strike runs come in at 70 and 80 m
        // Air classes and signatures (E5): the air superiority jet is harder to see, lock and kill.
        Assert.Equal((InterceptClass.Multirole, InterceptClass.Fighter), (m.Flight.Class, f.Flight.Class));
        Assert.Same(Signature.Normal, m.Signature);
        Assert.Equal(new Signature(50, 200, 60), f.Signature);
        // Countermeasures (data; dispensing is E6): the multirole works low over MANPADS.
        Assert.Equal([(CountermeasureKind.Flare, 8, Seeker.Ir), (CountermeasureKind.Chaff, 6, Seeker.Radar)], m.Countermeasures.Select(x => (x.Kind, x.Salvos, x.Vs)));
        Assert.Equal([(CountermeasureKind.Flare, 4, Seeker.Ir), (CountermeasureKind.Chaff, 4, Seeker.Radar)], f.Countermeasures.Select(x => (x.Kind, x.Salvos, x.Vs)));
        Assert.All(m.Countermeasures.Concat(f.Countermeasures), x => Assert.Equal(x.Kind == CountermeasureKind.Flare ? 55 : 60, x.Pk));
        // Guard's default filters by role; the leash.
        Assert.Equal(AirTargetKinds.Vehicles | AirTargetKinds.Structures | AirTargetKinds.Drones, m.Mission!.DefaultGuard);
        Assert.Equal(AirTargetKinds.Aircraft | AirTargetKinds.Drones, f.Mission!.DefaultGuard);
        // Runway minimums: both need 4 segments (80 cells) to land.
        Assert.Equal((65L, 45L, 70L, 50L), (Cells(m.Aircraft!.MinLandRun.Raw), Cells(m.Aircraft.MinTakeoffRun.Raw), Cells(f.Aircraft!.MinLandRun.Raw), Cells(f.Aircraft.MinTakeoffRun.Raw)));
    }

    // Loadout: the weapons a def of preset or archetype name carries, as (component-free) weapon facts.
    private static List<WeaponDef> Weapons(World w, string name) => [.. w.Player(1)!.Setup.Find(w.Content, name)!.Weapons];

    [Fact]
    public void TheLoadoutsAreThePlansIncludingTheMultirolesTwoBombPresets()
    {
        var w = Arena.NewSentinel(64);
        static (string Warhead, int Damage, int Ammo, long Range) Fact(World w, WeaponDef x) =>
            (x.Warhead.Id, x.Effect.Damage, x.Stock >= 0 ? x.Effect.Ammo : -1, Cells(x.Range.Raw * 10));
        var a = Weapons(w, "Multirole 500 lb");
        var b = Weapons(w, "Multirole 2000 lb");
        var f = Weapons(w, "fighter_jet");
        // Preset A: 2 IR missiles, 8 gun bursts, 4 x 500 lb; preset B: the same with 2 x 2,000 lb.
        Assert.Equal([("aa_missile", 100, 2, 400L), ("air_gun", 90, 8, 150L), ("bomb", 700, 4, 300L)], a.Select(x => Fact(w, x)).OrderBy(x => x.Warhead));
        Assert.Equal([("aa_missile", 100, 2, 400L), ("air_gun", 90, 8, 150L), ("bomb", 1300, 2, 300L)], b.Select(x => Fact(w, x)).OrderBy(x => x.Warhead));
        // The air superiority jet: 6 radar missiles out to 110 cells, 2 IR, 10 bursts, 2 glide bombs.
        Assert.Equal([("aa_missile", 100, 2, 400L), ("aa_missile", 100, 6, 1100L), ("air_gun", 90, 10, 150L), ("bomb", 350, 2, 500L)],
            f.Select(x => Fact(w, x)).OrderBy(x => x.Warhead).ThenBy(x => x.Range));
        var radar = f.Single(x => x.Effect.Interceptor is { Seeker: Seeker.Radar }).Effect.Interceptor!;
        var ir = f.Single(x => x.Effect.Interceptor is { Seeker: Seeker.Ir }).Effect.Interceptor!;
        Assert.Equal((60, 30), (radar.Boresight, ir.Boresight));
        Assert.Equal((20, 10), (radar.LockTicks, ir.LockTicks)); // 1 s and 0.5 s
        Assert.Equal((70, 75, 70, 75), (radar.Best(InterceptClass.Multirole), ir.Best(InterceptClass.Multirole), radar.Best(InterceptClass.Fighter), ir.Best(InterceptClass.Fighter)));
        // The bombs' damage gives the airstrike tier the client draws (the plan's 6.3): T2, T4, T6.
        int Tier(int dmg) => dmg < 250 ? 1 : dmg < 450 ? 2 : dmg < 600 ? 3 : dmg < 900 ? 4 : dmg < 1200 ? 5 : dmg < 1800 ? 6 : 7;
        Assert.Equal((2, 4, 6), (Tier(f.Single(x => x.Warhead.Id == "bomb").Effect.Damage), Tier(a.Single(x => x.Warhead.Id == "bomb").Effect.Damage),
            Tier(b.Single(x => x.Warhead.Id == "bomb").Effect.Damage)));
        // Runway effects: the 2,000 lb bomb digs a large crater, the others a small one.
        Assert.Equal((CraterKind.Small, CraterKind.Small, CraterKind.Large),
            (f.Single(x => x.Warhead.Id == "bomb").Effect.Runway!.Crater, a.Single(x => x.Warhead.Id == "bomb").Effect.Runway!.Crater,
             b.Single(x => x.Warhead.Id == "bomb").Effect.Runway!.Crater));
        // The gun's and the bombs' delivery data (E8; fired by E6 and E7).
        var gun = f.Single(x => x.Warhead.Id == "air_gun").Effect.AirGun!;
        Assert.Equal((10, 85, 70, 50, 60, 30, 25), (gun.Cone, gun.HitOn(InterceptClass.Drone), gun.HitOn(InterceptClass.Scout), gun.HitOn(InterceptClass.Watcher),
            gun.HitOn(InterceptClass.Overseer), gun.HitOn(InterceptClass.Multirole), gun.HitOn(InterceptClass.Fighter)));
        Assert.Equal(0, gun.HitOn(InterceptClass.Ballistic));
        Assert.Equal(gun.HitBase, a.Single(x => x.Warhead.Id == "air_gun").Effect.AirGun!.HitBase); // the same gun on both jets
        var glide = f.Single(x => x.Warhead.Id == "bomb").Effect.Delivery!;
        Assert.Equal((DeliveryProfile.Level, 95, 75, 3L, 5L), (glide.Profile, glide.Guided!.HitIdle, glide.Guided.HitMoving, Cells(glide.Guided.MissNear.Raw), Cells(glide.Guided.MissFar.Raw)));
        foreach (var bombs in new[] { a, b })
        {
            var dl = bombs.Single(x => x.Warhead.Id == "bomb").Effect.Delivery!;
            Assert.Equal((DeliveryProfile.Dive, 35, 16L, 60 * 512L, 1), (dl.Profile, dl.DiveAngle, Cells(dl.IpDistance.Raw), dl.ReleaseHeightRaw, dl.Stick));
            Assert.Equal((15L, 10L, 30L), (Cells(dl.ScatterLevel.Raw * 10), Cells(dl.ScatterDive.Raw * 10), Cells(dl.ScatterToss.Raw * 10))); // 1.5, 1.0, 3.0 cells
            Assert.Null(dl.Guided);
        }
        // Every jet weapon is refilled only by a station's rearm, and no ground combat ever fires it.
        Assert.All(a.Concat(b).Concat(f), x => Assert.True(x.Effect.NoResupply, x.Warhead.Id));
        Assert.Equal(2, w.Player(1)!.Setup.Presets.Count(p => p.Archetype.Id == "multirole_jet"));
    }

    [Theory]
    [InlineData("\"profile\": \"dive\"", "\"profile\": \"loop\"", "weapon.delivery.profile")]
    [InlineData("\"hitBase\": { \"drone\": 85,", "\"hitBase\": { \"helicopter\": 85,", "unknown air class")]
    [InlineData("\"profile\": \"level\", \"releaseHeight\": 700,", "\"profile\": \"level\", \"releaseHeight\": 700, \"stick\": 40,", "weapon.delivery.stick")]
    public void BadJetWeaponDataIsRefused(string from, string to, string error)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(x => x.Json.Contains(from, StringComparison.Ordinal));
        Assert.True(i >= 0, $"no file has {from}");
        var json = sources[i].Json;
        var at = json.IndexOf(from, StringComparison.Ordinal);
        sources[i] = sources[i] with { Json = string.Concat(json.AsSpan(0, at), to, json.AsSpan(at + from.Length)) };
        var e = Assert.ThrowsAny<Exception>(() => SimCore.Content.ContentLoader.Load(sources));
        Assert.Contains(error, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJetWarheadsBombsBreakBuildingsTheGunKillsDronesInABurstAndJetsInSeveral()
    {
        var bomb = C.Warhead("bomb")!;
        var gun = C.Warhead("air_gun")!;
        int V(Warhead wh, string armor) => wh.Versus[C.ArmorClass(armor)];
        Assert.Equal((200, 100, 100, 60, 120), (V(bomb, "building"), V(bomb, "infantry"), V(bomb, "light_vehicle"), V(bomb, "armored_vehicle"), V(bomb, "parked_aircraft")));
        Assert.False(bomb.CanHit(C.ArmorClass("aircraft")) || bomb.CanHit(C.ArmorClass("drone"))); // bombs never hit anything flying
        var burst = 90;
        foreach (var drone in C.Archetypes.Where(a => a.Flight is not null && a.Armor == C.ArmorClass("drone")))
        {
            Assert.True(burst * V(gun, "drone") / 100 >= drone.MaxHp, $"{drone.Id} dies to one burst");
        }
        foreach (var jet in Jets)
        {
            var bursts = (A(jet).MaxHp * 100 + burst * V(gun, "aircraft") - 1) / (burst * V(gun, "aircraft"));
            Assert.InRange(bursts, 3, 5); // a jet takes several
        }
        var crash = C.Warhead("crash")!;
        Assert.True(crash.CanHit(C.ArmorClass("infantry")) && !crash.CanHit(C.ArmorClass("aircraft")));
    }

    [Fact]
    public void ThePilotIsAWeakSoldierNoFactoryTrains()
    {
        var p = A("pilot");
        Assert.True(p.Tags.Matches(C.TagsOf("infantry")) && p.Tags.Matches(C.TagsOf("soldier")) && p.Tags.Matches(C.TagsOf("pilot")));
        Assert.Equal((45, 0, Locomotor.Foot), (p.MaxHp, p.Cost, p.Locomotor));
        Assert.True(p.Speed < A("assault").Speed, "a little slower than the rifleman");
        Assert.Equal(5L, Cells(p.Vision.Raw));
        Assert.True(p.Crushable > 0);
        Assert.Equal(["compact_rifle"], p.Components.Select(i => C.Components[i].Id));
        var w = Arena.NewSentinel(64);
        var pilot = w.Spawn(1, "pilot", 20, 20);
        var rifle = pilot.Def.Weapons.Single();
        Assert.Equal((8, 4L, "bullet"), (rifle.Effect.Damage, Cells(rifle.Range.Raw), rifle.Warhead.Id));
        Assert.DoesNotContain(C.Archetypes, a => a.Production is { } pr && p.Tags.Matches(pr.Tags)); // never trained
    }

    // ---- Built by the airfield facility into a free station (D9) ----

    [Fact]
    public void TheFacilityBuildsJetsIntoFreeStationsAndAFinishedJetWaitsForOne()
    {
        var (w, af, facility) = Field();
        var stand = Station(af, StationKind.Normal);
        var shelter = Station(af, StationKind.Loading);
        Produce(w, facility, "Multirole 500 lb");
        Produce(w, facility, "fighter_jet");
        Assert.True(RunUntil(w, () => w.All("multirole_jet").Count == 1, A("multirole_jet").BuildTime + 5) > A("multirole_jet").BuildTime - 5);
        var m = w.All("multirole_jet").Single();
        Assert.Equal((FlightPhase.Parked, stand.Unit, true), (m.AirOps!.Phase, m.AirOps.Station, m.AirOps.Hold)); // the open stand first; held
        Assert.Equal(m.Id, stand.Occupant);
        Assert.Equal(m.Def.Lifetime, m.Lifetime); // a full tank
        Assert.All(Enumerable.Range(0, m.Charges.Count), i => Assert.Equal(m.Def.StockMax[i], m.Charges[i])); // and full stocks
        Assert.Contains(m.Def.Weapons, x => x.Warhead.Id == "bomb" && x.Effect.Damage == 700); // preset A
        Assert.Equal(FlightPhase.Parked, World.FlightPhaseOf(m));
        Assert.True(w.Grounded(m));
        Assert.True(RunUntil(w, () => w.All("fighter_jet").Count == 1, A("fighter_jet").BuildTime + 5) > 0);
        var f = w.All("fighter_jet").Single();
        Assert.Equal((FlightPhase.Parked, shelter.Unit), (f.AirOps!.Phase, f.AirOps.Station)); // then the loading shelter
        // Both stations taken: a third jet is built and waits in the facility.
        Produce(w, facility, "Multirole 2000 lb");
        w.Run(A("multirole_jet").BuildTime + 100);
        Assert.Single(w.All("multirole_jet"));
        Assert.True(facility.Bays[0].Def >= 0 && facility.Bays[0].Wait > 0, "finished, waiting for a station");
        // The held jets stay parked (no sortie on their own); ordered off, one frees its station.
        Assert.Equal(FlightPhase.Parked, m.AirOps!.Phase);
        w.Order(1, CommandKind.Move, [f], cell: new Cell(150, 100));
        Assert.True(RunUntil(w, () => w.All("multirole_jet").Count == 2, 2000) > 0, "the waiting jet parks in the station freed");
        var b = w.All("multirole_jet").Single(x => x != m);
        Assert.Equal(shelter.Unit, b.AirOps!.Station);
        Assert.Contains(b.Def.Weapons, x => x.Warhead.Id == "bomb" && x.Effect.Damage == 1300); // preset B
        Assert.True(f.AirOps is null || f.AirOps.Phase > FlightPhase.Parked, "the fighter is on its way out");
    }

    [Fact]
    public void OnlyAStandingFacilityBuildsAndOnlyJets()
    {
        var (w, af, facility) = Field();
        Produce(w, facility, "technical");   // not a jet: refused
        Produce(w, facility, "watcher_drone"); // a drone: not tagged jet either
        w.Step();
        Assert.Empty(facility.ProductionQueue);
        Assert.True(facility.Bays.All(b => b.Def < 0));
        var garage = w.Spawn(1, "vehicle_garage", 150, 30);
        w.Step();
        Produce(w, garage, "fighter_jet"); // the garage builds vehicles, not jets
        w.Step();
        Assert.Empty(garage.ProductionQueue);
        Assert.True(garage.Bays.All(b => b.Def < 0));
        // Rubble builds nothing.
        facility.Rubble = true;
        facility.HP = facility.Def.MaxHp / 2;
        Produce(w, facility, "fighter_jet");
        w.Run(A("fighter_jet").BuildTime + 50);
        Assert.Empty(w.All("fighter_jet"));
        Assert.Single(af.Rows);
    }

    // ---- Flying: every mission, on the fixed-wing model ----

    [Fact]
    public void ABuiltJetTakesOffOnAnOrderAndPatrolsOnItsEnvelope()
    {
        var (w, af, facility) = Field();
        Produce(w, facility, "fighter_jet");
        Assert.True(RunUntil(w, () => w.All("fighter_jet").Count == 1, 1100) > 0);
        var f = w.All("fighter_jet").Single();
        w.Run(100);
        Assert.Equal(FlightPhase.Parked, f.AirOps!.Phase); // held until ordered
        Give(w, f, AirTaskKind.Patrol, new Cell(110, 100));
        Give(w, f, AirTaskKind.Patrol, new Cell(150, 140), queue: true);
        var wheelsUp = false;
        Assert.True(RunUntil(w, () =>
        {
            wheelsUp |= w.AirfieldEvents.Any(e => e.Kind == AirfieldEventKind.WheelsUp && e.Aircraft == f.Id);
            return f.AirOps is null;
        }, 3000) > 0 && wheelsUp, "it taxis out, takes off and flies free");
        w.Step();
        Assert.NotNull(f.Wing); // the fixed-wing kinematics
        var legs = 0;
        var last = f.AirMission!.Leg;
        for (var t = 0; t < 1500; t++) // short of bingo (4 minutes of fuel less the way home and the reserves)
        {
            w.Step();
            if (f.AirMission.Leg != last)
            {
                (legs, last) = (legs + 1, f.AirMission.Leg);
            }
            Assert.NotNull(f.Wing);
        }
        Assert.True(legs >= 3, $"it flies the racetrack round: {legs} legs");
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(f).Activity);
        Assert.Equal(0, w.AirEdgeClamps);
    }

    [Theory]
    [InlineData("multirole_jet")]
    [InlineData("fighter_jet")]
    public void EachJetFliesEveryMission(string jet)
    {
        var w = Arena.NewSentinel(N);
        var o = Overhead(w, jet, 120, 120); // orbit (no mission)
        var p = Overhead(w, jet, 100, 90);
        Give(w, p, AirTaskKind.Patrol, new Cell(90, 100));
        Give(w, p, AirTaskKind.Patrol, new Cell(150, 140), queue: true);
        var g = Overhead(w, jet, 140, 100);
        Give(w, g, AirTaskKind.Guard, new Cell(90, 90), new Cell(150, 150));
        var s = Overhead(w, jet, 100, 150);
        Give(w, s, AirTaskKind.Search, new Cell(80, 80), new Cell(160, 160));
        var r = Overhead(w, jet, 150, 150);
        Give(w, r, AirTaskKind.Roam, new Cell(80, 80), new Cell(160, 160));
        var spec = A(jet).Flight!.Wing!;
        for (var t = 0; t < 4000; t++)
        {
            w.Step();
            foreach (var d in new[] { o, p, g, s, r })
            {
                Assert.NotNull(d.Wing);
                Assert.InRange(d.Wing!.Speed, spec.Min, spec.Dash);
                long x = d.Pos.X.Raw, y = d.Pos.Y.Raw, edge = (long)N * Fixed.OneRaw, step = spec.Dash / 20;
                Assert.True(x >= step && y >= step && x < edge - step && y < edge - step, $"{jet} tick {t} at the edge");
            }
        }
        Assert.Equal(AirActivity.Orbiting, w.AirStatusOf(o).Activity);
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(p).Activity);
        Assert.Equal(AirActivity.Guarding, w.AirStatusOf(g).Activity);
        Assert.True(s.AirMission!.Done || s.AirMission.Lane >= 1, "it sweeps lanes");
        Assert.True(System.Numerics.BitOperations.PopCount(r.AirMission!.Visited) >= 8, "it roams over many sectors");
        Assert.Equal(0, w.AirEdgeClamps);
    }

    // ---- Home: bingo, refuel, resume; Rearm ----

    [Theory]
    [InlineData("multirole_jet")]
    [InlineData("fighter_jet")]
    public void AtBingoAJetLandsAtItsAirfieldRefuelsAndResumesItsPatrol(string jet)
    {
        var (w, af, _) = Field();
        var d = Overhead(w, jet, 140, 80);
        Give(w, d, AirTaskKind.Patrol, new Cell(110, 90));
        Give(w, d, AirTaskKind.Patrol, new Cell(160, 90), queue: true);
        w.Run(100);
        d.Lifetime = 1500;
        d.MutableSalvos[0] = 1; // flares spent (E6): an automatic refuel stop does not reload them
        Assert.True(RunUntil(w, () => d.AirOps is not null, 1500) > 0, "it turns for home at bingo");
        Assert.Equal((af.Id, HomeReason.Refuel), (d.AirOps!.Airfield, d.HomeReason));
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked } || !d.Alive, 8000) > 0 && d.Alive, $"it lands and parks ({World.FlightPhaseOf(d)})");
        Assert.Equal(Station(af, StationKind.Normal).Unit, d.AirOps!.Station); // a refuel stop takes the open stand
        Assert.True(RunUntil(w, () => d.AirOps is null, 4000) > 0, "refuelled, it takes off");
        Assert.InRange(d.Lifetime, d.Def.Lifetime - 400, d.Def.Lifetime);
        Assert.Equal(1, d.Salvos[0]);
        w.Run(20);
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(d).Activity);
        Assert.Equal(AirTaskKind.Patrol, d.AirMission!.Kind);
    }

    [Theory]
    [InlineData("multirole_jet")]
    [InlineData("fighter_jet")]
    public void ARearmOrderRearmsAndRepairsItAtTheAirfieldAndItResumes(string jet)
    {
        var (w, af, _) = Field();
        var d = Overhead(w, jet, 140, 80);
        Give(w, d, AirTaskKind.Patrol, new Cell(130, 90));
        w.Run(50);
        for (var i = 0; i < d.Charges.Count; i++)
        {
            d.MutableCharges[i] = 0; // as if every missile, burst and bomb were spent
        }
        for (var i = 0; i < d.Salvos.Count; i++)
        {
            d.MutableSalvos[i] = 0; // and every flare and chaff salvo (E6)
        }
        d.HP = d.Def.MaxHp / 2;
        w.Order(1, CommandKind.Rearm, [d]);
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }, 8000) > 0, "it goes home and parks");
        Assert.Equal(HomeReason.Rearm, d.HomeReason);
        Assert.Equal(Station(af, StationKind.Loading).Unit, d.AirOps!.Station); // a Rearm prefers the loading shelter
        Assert.True(RunUntil(w, () => d.AirOps is null, 4000) > 0, "rearmed, it takes off");
        Assert.All(Enumerable.Range(0, d.Charges.Count), i => Assert.Equal(d.Def.StockMax[i], d.Charges[i]));
        Assert.Equal(d.Def.Archetype.Countermeasures.Select(c => c.Salvos), d.Salvos); // the dispensers refilled too (E6)
        Assert.Equal(d.Def.MaxHp, d.HP);
        w.Run(20);
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(d).Activity);
    }

    // ---- What they do not do yet, and what reaches them ----

    [Fact]
    public void JetsFightInTheAirAndBombTheGround()
    {
        // E6: the air superiority jet on Guard (aircraft and drones) engages the scout drone with its
        // missiles; E7: the multirole sent at a truck dive-bombs it.
        var w = Arena.NewSentinel(N);
        var f = Overhead(w, "fighter_jet", 120, 120);
        var m = Overhead(w, "multirole_jet", 120, 100);
        var scout = w.Spawn(2, "scout_drone", 150, 120);
        var truck = w.SpawnCrewed(2, "technical", 125, 110).Vehicle;
        Give(w, f, AirTaskKind.Guard, new Cell(100, 100), new Cell(160, 140)); // aircraft and drones
        w.Order(1, CommandKind.Attack, [m], target: truck.Id);
        var fired = false;
        var bombs = 0;
        for (var t = 0; t < 900; t++)
        {
            w.Step();
            fired |= w.AirLaunches.Any(l => l.Shooter == f.Id) || w.GunBursts.Any(b => b.Shooter == f.Id);
            bombs += w.BombReleases.Count(r => r.Aircraft == m.Id);
            Assert.DoesNotContain(w.BombReleases, r => r.Aircraft == f.Id); // Guard on aircraft and drones: no ground targets
        }
        Assert.True(fired, "the fighter on Guard engages the scout drone");
        Assert.False(scout.Alive);
        Assert.InRange(bombs, 1, 4);
        Assert.False(truck.Alive);
    }

    [Theory]
    [InlineData("multirole_jet", 100, true)] // about 104 cells of slant: inside the AA soldier's 150
    [InlineData("fighter_jet", 100, false)]  // the air superiority jet's signature: seen at 75
    [InlineData("fighter_jet", 60, true)]
    public void AirDefenceSeesTheAirSuperiorityJetAtHalfTheRange(string jet, int dx, bool seen)
    {
        var w = Arena.NewSentinel(N);
        var aa = w.Spawn(2, "manpads", 100, 120);
        var j = w.Spawn(1, Spawnable(jet), 100 + dx, 120);
        j.Altitude = Fixed.FromRaw((jet == "fighter_jet" ? 70 : 60) * 512L); // the E8 patrol heights (E13 flies them higher: see the design)
        w.Run(2);
        Assert.Equal(seen, aa.AirTarget == j.Id);
    }

    // ---- Determinism ----

    [Fact]
    public void BuildingAndFlyingJetsIsDeterministic()
    {
        static List<ulong> Run()
        {
            var (w, _, facility) = Field(N, 7);
            Produce(w, facility, "Multirole 500 lb");
            Produce(w, facility, "fighter_jet");
            var hashes = new List<ulong>();
            for (var t = 0; t < 3000; t++)
            {
                if (t == 2000)
                {
                    foreach (var j in w.All("multirole_jet").Concat(w.All("fighter_jet")))
                    {
                        Give(w, j, AirTaskKind.Roam, new Cell(90, 60), new Cell(170, 140));
                    }
                }
                w.Step();
                if (t % 50 == 0)
                {
                    hashes.Add(w.Hash());
                }
            }
            Assert.Equal(2, w.All("multirole_jet").Count + w.All("fighter_jet").Count);
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }
}
