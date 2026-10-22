using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The Sentinel airfield, milestone M3 part B (the game repo's docs/airfield-design.md, sections 1,
// 1.1 and 2.2): service at the stations (refuel 1x everywhere, rearm and repair 2x at a loading
// station, 1x at an open stand; nothing at a loading station while the facility is down), the
// rearm and sortie loop (since E2 only on a Rearm order: an automatic refuel stop refuels only,
// AirRulesTests), ReturnToBase, the parked aircraft armour and sight (air defence leaves
// grounded aircraft alone), and the aircraft's reactions to craters: the divert chain, the
// go-around, stopping short of (or running into) a crater on the runway, and stations that fall.
public class AirfieldServiceTests
{
    private const int N = 200;

    private static Airfield Field(World w, IReadOnlyList<(int, int)> rows, Cell? at = null, int east = 0) =>
        AirfieldFlightTests.Field(w, rows, at, east);

    private static Unit Homebound(World w, int x, int y)
    {
        var d = w.Spawn(1, "watcher_drone", x, y);
        d.Homing = true;
        return d;
    }

    // Rearming: a Watcher put down in flight at (x, y) heading home on a Rearm order (the full
    // service: rearm and repair too; E2's owner rules). Homebound's automatic trip only refuels.
    private static Unit Rearming(World w, int x, int y)
    {
        var d = Homebound(w, x, y);
        d.HomeReason = HomeReason.Rearm;
        return d;
    }

    private static int RunUntil(World w, Func<bool> done, int max = 6000)
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

    private static List<AirfieldEvent> Collect(World w, int ticks, Func<bool>? until = null)
    {
        var list = new List<AirfieldEvent>();
        for (var t = 0; t < ticks; t++)
        {
            w.Step();
            list.AddRange(w.AirfieldEvents);
            if (until?.Invoke() == true)
            {
                break;
            }
        }
        return list;
    }

    private static AirfieldStation StationOf(Airfield af, uint unit) => af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == unit);

    private static AirfieldStation Shelter(Airfield af) => af.Rows[0].Stations.Single(s => s.Kind == StationKind.Loading);

    private static AirfieldStation Stand(Airfield af) => af.Rows[0].Stations.Single(s => s.Kind == StationKind.Normal);

    // Blast digs a small crater at the airfield frame's (x, y) cells (it opens at the end of the next step).
    private static void Crater(World w, Airfield af, long xRaw, int y = 5) =>
        w.RunwayBlast(World.AirfieldLocalToMap(af, xRaw, (long)y * Fixed.OneRaw), new RunwayEffect(CraterKind.Small, 0));

    private static void Close(World w, Airfield af)
    {
        // Two small craters at 20 and 40 cells leave no 36-cell stretch on a 60-cell runway.
        Crater(w, af, 20 * Fixed.OneRaw);
        Crater(w, af, 40 * Fixed.OneRaw);
    }

    private static long LocalX(Airfield af, Unit u) => World.AirfieldMapToLocal(af, u.Pos).X;

    // Two empty Watchers sent to rearm, held at a one-row field: one parks in the shelter, the other on the stand.
    private static (World W, Airfield Af, Unit InShelter, Unit OnStand) Parked()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var a = Rearming(w, 175, 150);
        var b = Rearming(w, 185, 175);
        a.MutableCharges[0] = 0;
        b.MutableCharges[0] = 0;
        w.Step();
        a.AirOps!.Hold = true;
        b.AirOps!.Hold = true;
        Assert.True(RunUntil(w, () => a.AirOps is { Phase: FlightPhase.Parked } && b.AirOps is { Phase: FlightPhase.Parked }, 12000) > 0);
        var (inShelter, onStand) = StationOf(af, a.AirOps!.Station).Kind == StationKind.Loading ? (a, b) : (b, a);
        Assert.Equal(StationKind.Normal, StationOf(af, onStand.AirOps!.Station).Kind);
        return (w, af, inShelter, onStand);
    }

    // Drain empties an aircraft's ammunition, fuel and half its HP, with its service starting over.
    private static void Drain(Unit u)
    {
        u.MutableCharges[0] = 0;
        u.Lifetime = 1;
        u.HP = u.Def.MaxHp / 2;
        (u.AirOps!.FuelAcc, u.AirOps.AmmoAcc, u.AirOps.HpAcc) = (0, 0, 0);
    }

    // Times runs until u is serviced and returns the ticks to its first round, its second, full HP and a full tank.
    private static (int Round1, int Round2, int Hp, int Fuel) Times(World w, Unit u, int max = 1000)
    {
        int r1 = -1, r2 = -1, hp = -1, fuel = -1;
        for (var t = 1; t <= max && (r2 < 0 || hp < 0 || fuel < 0); t++)
        {
            w.Step();
            if (r1 < 0 && u.Charges[0] >= 1)
            {
                r1 = t;
            }
            if (r2 < 0 && u.Charges[0] >= 2)
            {
                r2 = t;
            }
            if (hp < 0 && u.HP >= u.Def.MaxHp)
            {
                hp = t;
            }
            if (fuel < 0 && u.Lifetime >= u.Def.Lifetime)
            {
                fuel = t;
            }
        }
        return (r1, r2, hp, fuel);
    }

    // ---- Content ----

    [Fact]
    public void EveryRunwayAircraftIsAParkedAircraftOnTheGroundAndAirDefenceCannotHitThat()
    {
        var c = TestContent.Mvp;
        var parked = c.ArmorClass("parked_aircraft");
        var aircraft = c.Archetypes.Where(a => a.Aircraft is { Landing: LandingKind.Runway }).ToList();
        Assert.Equal(["attack_jet", "fighter_jet", "gunship", "multirole_jet", "overseer_drone", "strategic_bomber", "supersonic_bomber", "watcher_drone"], aircraft.Select(a => a.Id));
        Assert.All(aircraft, a => Assert.Equal((parked, c.ArmorClass(a.Tags.Matches(c.TagsOf("aircraft")) ? "aircraft" : "drone")), (a.Aircraft!.GroundArmor, a.Armor))); // jets, the gunship and the bomber fly in the aircraft armor (E8, H3, H4)
        Assert.False(c.Warhead("aa_missile")!.CanHit(parked));
        Assert.All(c.Warheads.Where(wh => wh.Id != "aa_missile"), wh => Assert.True(wh.CanHit(parked), wh.Id)); // every ground warhead hurts it
        Assert.Equal(50, c.AirfieldTypes[0].CraterHitDamage);
    }

    // ---- Service rates ----

    [Fact]
    public void ALoadingStationRearmsAndRepairsAtTwiceTheStandsRateAndBothRefuelAtOneX()
    {
        var (w, af, inShelter, onStand) = Parked();
        Drain(inShelter);
        Drain(onStand);
        var shelter = Times(w, inShelter);
        Drain(onStand);
        var stand = Times(w, onStand);
        // The Watcher: a missile in 15 s (300) at 1x, 7.5 s (150) at 2x; 10 HP/s at 1x (100 HP in
        // 200 ticks), 20 HP/s at 2x; a full tank in 400 ticks at 1x everywhere (60 a tick from 1).
        Assert.Equal((150, 300, 100, 400), shelter);
        Assert.Equal((300, 600, 200, 400), stand);
        // Parity with its ground control station: both missiles at 1x take the station's turnaround.
        Assert.Equal(inShelter.Def.Archetype.Flight!.TurnaroundTicks, stand.Round2);
        var s = w.ServiceOf(inShelter);
        Assert.Equal((true, true, 100, 200, 200, true), (s.AtStation, s.Working, s.RefuelPercent, s.RearmPercent, s.RepairPercent, s.Serviced));
        Assert.Equal((100, 100, 100), (w.ServiceOf(onStand).RefuelPercent, w.ServiceOf(onStand).RearmPercent, w.ServiceOf(onStand).RepairPercent));
        Assert.All(new[] { inShelter, onStand }, u => Assert.Equal(FlightPhase.Parked, World.FlightPhaseOf(u))); // held
        Assert.Equal(inShelter.Id, Shelter(af).Occupant);
    }

    [Fact]
    public void WithTheFacilityDownTheShelterGivesNothingAndTheStandKeepsServing()
    {
        var (w, af, inShelter, onStand) = Parked();
        var fac = w.Unit(af.Facility)!;
        fac.HP = 0;
        w.Step();
        Assert.True(fac.Rubble);
        Drain(inShelter);
        Drain(onStand);
        Assert.False(w.StationWorks(af, w.Unit(Shelter(af).Unit)));
        Assert.True(w.StationWorks(af, w.Unit(Stand(af).Unit)));
        w.Run(600);
        Assert.Equal((0, 1, inShelter.Def.MaxHp / 2), (inShelter.Charges[0], inShelter.Lifetime, inShelter.HP)); // no rearm, refuel or repair
        Assert.Equal(2, onStand.Charges[0]);
        Assert.Equal(onStand.Def.Lifetime, onStand.Lifetime);
        var s = w.ServiceOf(inShelter);
        Assert.Equal((true, false, 0, 0, 0, false), (s.AtStation, s.Working, s.RefuelPercent, s.RearmPercent, s.RepairPercent, s.Serviced));

        // The facility repaired, the shelter serves at 2x again.
        (fac.HP, fac.Rubble) = (fac.Def.MaxHp, false);
        Assert.Equal(150, Times(w, inShelter).Round1);
    }

    [Fact]
    public void AnEmptyStrikeDroneOrbitsOnUntilARearmOrderThenRearmsAndSortiesAgainWithBothMissiles()
    {
        // E2 (the owner's rules): empty, it no longer goes home on its own (M3b's "home to rearm");
        // a Rearm order sends it, the shelter rearms it, and it resumes its orbit.
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = w.Spawn(1, "watcher_drone", 170, 160);
        w.Run(5);
        var orbit = d.OrbitAt;
        d.MutableCharges[0] = 0; // both missiles fired
        w.Run(600);
        Assert.Null(d.AirOps);
        Assert.False(d.Homing, "empty: it orbits on");
        w.Order(1, CommandKind.Rearm, [d]);
        var phases = new List<FlightPhase>();
        int parkedAt = -1, leftAt = -1;
        var t = RunUntil(w, () =>
        {
            var phase = World.FlightPhaseOf(d);
            if (phases.Count == 0 || phases[^1] != phase)
            {
                phases.Add(phase);
                if (phase == FlightPhase.Parked)
                {
                    parkedAt = (int)w.Tick;
                    Assert.Equal(StationKind.Loading, StationOf(af, d.AirOps!.Station).Kind); // sent to rearm: the shelter
                }
                if (phase == FlightPhase.PushBack)
                {
                    leftAt = (int)w.Tick;
                }
            }
            return phases.Count > 2 && phase == FlightPhase.Airborne;
        }, 12000);
        Assert.True(t > 0, $"back in the air (phases: {string.Join(", ", phases)})");
        Assert.Contains(FlightPhase.Parked, phases);
        Assert.Equal(2, World.StrikesLeft(d));
        Assert.InRange(leftAt - parkedAt, 300, 420); // two rounds at 2x, or its tank if that took longer
        Assert.Null(d.AirOps);
        Assert.Equal(Cell.Of(orbit).Center(), d.OrbitAt);
    }

    // ---- ReturnToBase ----

    private static void ReturnToBase(World w, IReadOnlyList<Unit> units, uint target = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.ReturnToBase, Units = [.. units.Select(u => u.Id)], Target = target });

    [Fact]
    public void ReturnToBaseSendsAFreeFlyingAircraftHomeWhereItStaysUntilOrdered()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = w.Spawn(1, "watcher_drone", 60, 160);
        w.Run(5);
        Assert.Null(d.AirOps); // full fuel and missiles: it would orbit on
        ReturnToBase(w, [d]);
        w.Step();
        Assert.Equal((FlightPhase.Holding, af.Id, true), (d.AirOps!.Phase, d.AirOps.Airfield, d.AirOps.Hold));
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }) > 0);
        w.Run(600);
        Assert.Equal(FlightPhase.Parked, World.FlightPhaseOf(d)); // serviced, but held
        ReturnToBase(w, [d]); // on the ground: ignored
        w.Step();
        Assert.Equal(FlightPhase.Parked, World.FlightPhaseOf(d));
        w.Order(1, CommandKind.Move, [d], cell: new Cell(60, 160));
        Assert.True(RunUntil(w, () => d.AirOps is null, 4000) > 0);
    }

    [Fact]
    public void ReturnToBaseGoesToTheTargetAirfieldOrElseTheGroundControlStation()
    {
        var w = Arena.NewSentinel(N);
        var near = Field(w, [(0, 0)], at: new Cell(30, 60));
        var far = Field(w, [(0, 0)], at: new Cell(30, 150));
        var d = w.Spawn(1, "watcher_drone", 150, 38);
        w.Run(5);
        ReturnToBase(w, [d], far.Facility);
        w.Step();
        Assert.Equal(far.Id, d.AirOps!.Airfield);

        // No airfield: its ground control station recovers it.
        var w2 = Arena.NewSentinel(N);
        var station = w2.Spawn(1, "watcher_station", 150, 160);
        w2.Order(1, CommandKind.UseAbility, [station], cell: new Cell(150, 100), ability: "watcher_launch");
        w2.Run(400);
        var d2 = Assert.Single(w2.All("watcher_drone"));
        Assert.False(d2.Homing);
        ReturnToBase(w2, [d2]);
        w2.Step();
        Assert.True(d2.Homing);
        Assert.True(RunUntil(w2, () => d2.Recovered, 3000) > 0);
        Assert.NotEqual(near.Id, far.Id);
    }

    [Fact]
    public void ReturnToBaseWhileClimbingOutBringsItStraightBack()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = Homebound(w, 175, 150);
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Climb }, 12000) > 0); // full: it sorties at once
        ReturnToBase(w, [d]);
        w.Step();
        Assert.True(d.AirOps!.Recall);
        Assert.True(RunUntil(w, () => d.AirOps is not { Phase: FlightPhase.Climb }) > 0);
        Assert.Equal((af.Id, true), (d.AirOps!.Airfield, d.AirOps.Hold));
        Assert.True(d.AirOps.Phase is FlightPhase.Holding or FlightPhase.Final, $"back to land ({d.AirOps.Phase})"); // near the hold point: cleared at once
    }

    // ---- Parked armour, targeting, air defence, sight ----

    [Fact]
    public void OnTheGroundAnAircraftHasParkedArmourTakesSplashAndIsNoTarget()
    {
        var (w, af, inShelter, _) = Parked();
        var c = w.Content;
        Assert.Equal(c.ArmorClass("parked_aircraft"), w.ArmorOf(inShelter));
        var flying = w.Spawn(1, "watcher_drone", 50, 45);
        w.Step();
        Assert.Equal(c.ArmorClass("drone"), w.ArmorOf(flying));
        flying.Pos = inShelter.Pos;

        // An explosive's splash: 120% on the parked aircraft; nothing on the one flying over it.
        var hp = inShelter.HP;
        w.Explode(inShelter.Pos, 50, c.Warhead("explosive")!, Fixed.FromInt(2));
        Assert.Equal(hp - 60, inShelter.HP);
        Assert.Equal(flying.Def.MaxHp, flying.HP);

        // Never an attack target: no weapon may be aimed at it, and an enemy rifleman beside it holds fire.
        var rifleman = w.Spawn(2, "Rifleman", Cell.Of(inShelter.Pos).X + 2, Cell.Of(inShelter.Pos).Y + 2);
        Assert.All(rifleman.Def.Weapons, wpn => Assert.False(World.CanHit(wpn, inShelter)));
        for (var t = 0; t < 200; t++)
        {
            hp = inShelter.HP;
            w.Step();
            Assert.True(inShelter.HP >= hp, "nobody shoots it (its shelter repairs it meanwhile)");
        }
        Assert.Equal(af.Id, inShelter.AirOps!.Airfield);
    }

    [Fact]
    public void AirDefenceNeverEngagesAnAircraftOnTheGround()
    {
        var w = Arena.NewSentinel(N);
        Field(w, [(0, 0)]);
        var d = Homebound(w, 175, 150);
        w.Step();
        d.AirOps!.Hold = true;
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }) > 0);
        var aa = w.Spawn(2, "manpads", Cell.Of(d.Pos).X + 6, Cell.Of(d.Pos).Y + 6);
        for (var t = 0; t < 300; t++)
        {
            w.Step();
            Assert.Equal(0u, aa.AirTarget);
        }
        Assert.Equal(d.Def.MaxHp, d.HP);
        Assert.False(w.InAir(d));

        // Off it goes: once airborne the same soldier tracks it.
        w.Order(1, CommandKind.Move, [d], cell: new Cell(Cell.Of(aa.Pos).X, Cell.Of(aa.Pos).Y));
        Assert.True(RunUntil(w, () => aa.AirTarget == d.Id, 4000) > 0);
        Assert.True(w.InAir(d));
    }

    [Fact]
    public void OnTheGroundItSeesAsAGroundUnit()
    {
        var w = Arena.NewSentinel(N);
        Field(w, [(0, 0)]);
        var d = Homebound(w, 175, 150);
        Assert.Equal(d.Def.Vision, w.SightRange(d)); // 35 cells in the air
        w.Step();
        d.AirOps!.Hold = true;
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Rollout }) > 0);
        Assert.Equal(Fixed.FromInt(6), w.SightRange(d)); // 6 cells on the ground, from the roll-out on
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }) > 0);
        Assert.Equal(Fixed.FromInt(6), w.SightRange(d));
    }

    // ---- Craters: divert, go-around, the roll ----

    [Fact]
    public void WithItsRunwayClosedItDivertsToAnotherAirfield()
    {
        var w = Arena.NewSentinel(N);
        var near = Field(w, [(0, 0)], at: new Cell(30, 60));
        var far = Field(w, [(0, 0)], at: new Cell(30, 150));
        var d = Homebound(w, 150, 38);
        w.Step();
        Assert.Equal(near.Id, d.AirOps!.Airfield);
        Close(w, near);
        var events = Collect(w, 100, () => d.AirOps?.Airfield == far.Id);
        Assert.Contains(events, e => e.Kind == AirfieldEventKind.Diverted && e.Aircraft == d.Id && e.Airfield == near.Id);
        Assert.Equal(far.Id, d.AirOps!.Airfield);
        Assert.Empty(near.Landings);
        Assert.Equal(0u, near.RunwayLock);
        Assert.All(near.Rows[0].Stations, s => Assert.Equal(0u, s.ReservedBy));
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }, 12000) > 0);
        Assert.Equal(far.Id, d.AirOps!.Airfield);
    }

    [Fact]
    public void WithNoOtherAirfieldItGoesToItsGroundControlStation()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var station = w.Spawn(1, "watcher_station", 150, 160);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(150, 120), ability: "watcher_launch");
        w.Run(400);
        var d = Assert.Single(w.All("watcher_drone"));
        d.Homing = true;
        w.Step();
        Assert.Equal(af.Id, d.AirOps!.Airfield); // a free station: the airfield first
        Close(w, af);
        var events = Collect(w, 100, () => d.AirOps is null);
        Assert.Contains(events, e => e.Kind == AirfieldEventKind.Diverted);
        Assert.True(d.Homing);
        Assert.True(RunUntil(w, () => d.Recovered || d.AirOps is not null, 3000) > 0);
        Assert.True(d.Recovered, "its ground control station recovered it");
    }

    [Fact]
    public void WithNowhereElseItHoldsUntilTheRunwayIsRepairedThenLands()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = Homebound(w, 175, 150);
        w.Step();
        Close(w, af);
        w.Run(600);
        Assert.Equal(FlightPhase.Holding, World.FlightPhaseOf(d)); // holding on, no stretch to land on
        Assert.Equal(af.Id, d.AirOps!.Airfield);
        Assert.Equal(0u, af.RunwayLock);
        foreach (var c in af.Craters)
        {
            c.Repaired = true; // the builders filled them
        }
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }, 6000) > 0);
    }

    [Fact]
    public void ACraterCuttingItsStretchOnFinalSendsItAroundAndItLandsOnWhatIsLeft()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = Homebound(w, 175, 150);
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Final, Leg: 2 }) > 0);
        Crater(w, af, 45 * Fixed.OneRaw); // closes 41.5 to 48.5: 41.5 cells are left to the west
        var events = Collect(w, 6000, () => d.AirOps is { Phase: FlightPhase.Parked });
        var go = events.FindIndex(e => e.Kind == AirfieldEventKind.GoAround);
        Assert.True(go >= 0, "it went around");
        var touch = events.FindIndex(e => e.Kind == AirfieldEventKind.Touchdown);
        Assert.True(touch > go);
        // It touched down 8 cells in from the new threshold (the west stretch's east end at 41.5).
        var x = World.AirfieldMapToLocal(af, events[touch].At).X;
        Assert.InRange(x, (long)(33.5 * Fixed.OneRaw) - Fixed.OneRaw, (long)(33.5 * Fixed.OneRaw) + Fixed.OneRaw);
        Assert.Equal(FlightPhase.Parked, World.FlightPhaseOf(d));
    }

    [Theory]
    [InlineData(3, true)]   // 3 cells ahead at 4.5 cells/s: it needs 10 to stop (1 cell/s2), so it runs into it
    [InlineData(13, false)] // 13 cells ahead: it stops short
    public void RollingOutTowardAFreshCraterItStopsShortOrRunsIntoItsEdge(int ahead, bool hit)
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = Homebound(w, 175, 150);
        w.Step();
        d.AirOps!.Hold = true;
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Rollout }) > 0);
        var lx = LocalX(af, d);
        var radius = af.Type.SmallCrater.Radius.Raw;
        Crater(w, af, lx - ahead * Fixed.OneRaw - radius);
        var events = Collect(w, 400, () => d.AirOps!.Leg == 0 && d.AirOps.Phase != FlightPhase.Rollout);
        var kind = hit ? AirfieldEventKind.CraterHit : AirfieldEventKind.CraterStop;
        var e = Assert.Single(events, e => e.Kind is AirfieldEventKind.CraterHit or AirfieldEventKind.CraterStop);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(hit ? d.Def.MaxHp / 2 : d.Def.MaxHp, d.HP);
        var edge = lx - ahead * Fixed.OneRaw;
        var stopX = World.AirfieldMapToLocal(af, e.At).X;
        Assert.InRange(stopX, edge - Fixed.OneRaw / 4, edge + Fixed.OneRaw / 2); // at the crater's edge, never in it
        Assert.Equal(0u, af.RunwayLock); // the runway is given back
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }, 4000) > 0); // it taxis off to its station
    }

    [Fact]
    public void OnTheTakeoffRollAFreshCraterAheadStopsItAndItWaitsForTheRunway()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = Homebound(w, 175, 150);
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.TakeoffRoll }, 12000) > 0);
        var lx = LocalX(af, d);
        Crater(w, af, lx + 12 * Fixed.OneRaw); // ahead, before it leaves the ground (18 cells of roll)
        var events = Collect(w, 400, () => d.AirOps is { Phase: FlightPhase.TaxiOut });
        Assert.Contains(events, e => e.Kind is AirfieldEventKind.CraterStop or AirfieldEventKind.CraterHit);
        Assert.DoesNotContain(events, e => e.Kind == AirfieldEventKind.WheelsUp);
        Assert.Equal(0u, af.RunwayLock);
        // Back at the holding point it waits: no stretch long enough to take off is left.
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.LineUp }, 4000) > 0);
        w.Run(200);
        Assert.Equal((FlightPhase.LineUp, 0), (d.AirOps!.Phase, d.AirOps.Leg));
    }

    // ---- Stations that fall ----

    [Fact]
    public void AtAFallenShelterItStaysPutUnservicedAndLeavesWhenOrdered()
    {
        var (w, af, inShelter, _) = Parked();
        inShelter.AirOps!.Hold = false;
        var shelter = w.Unit(Shelter(af).Unit)!;
        shelter.HP = 0;
        w.Step();
        Assert.True(shelter.Rubble);
        Assert.Equal(inShelter.Def.MaxHp * 60 / 100, inShelter.HP); // the canopy fell on it: 40%
        var charges = inShelter.Charges[0];
        w.Run(600);
        Assert.Equal(FlightPhase.Parked, World.FlightPhaseOf(inShelter));
        Assert.Equal(charges, inShelter.Charges[0]); // nothing from rubble
        (inShelter.HP, inShelter.Lifetime, inShelter.MutableCharges[0]) = (inShelter.Def.MaxHp, inShelter.Def.Lifetime, 2);
        w.Run(100);
        Assert.Equal(FlightPhase.Parked, World.FlightPhaseOf(inShelter)); // serviced, still it waits for an order

        // Gone (hit again): it still leaves by its slot's way out when ordered.
        shelter.HP = 0;
        w.Step();
        Assert.Equal(0u, Shelter(af).Unit);
        w.Order(1, CommandKind.Move, [inShelter], cell: new Cell(150, 30));
        Assert.True(RunUntil(w, () => inShelter.AirOps is { Phase: FlightPhase.PushBack }, 50) > 0);
        Assert.True(RunUntil(w, () => inShelter.AirOps is null, 4000) > 0);
    }

    [Fact]
    public void AReservationOnAFallenShelterIsDroppedAndAnotherStationTaken()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = Rearming(w, 175, 150);
        d.MutableCharges[0] = 0; // sent to rearm: it wants the shelter
        w.Step();
        Assert.Equal(Shelter(af).Unit, d.AirOps!.Station);
        w.Unit(Shelter(af).Unit)!.HP = 0;
        w.Step();
        Assert.Equal(0u, Shelter(af).ReservedBy);
        Assert.True(RunUntil(w, () => d.AirOps!.Station == Stand(af).Unit, 100) > 0); // the stand, within a second
        Assert.Equal(d.Id, Stand(af).ReservedBy);
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }) > 0);
        Assert.Equal(d.Id, Stand(af).Occupant);
    }

    [Fact]
    public void TaxiingInToAShelterThatFallsItReroutesToTheStand()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = Rearming(w, 175, 150);
        d.MutableCharges[0] = 0;
        w.Step();
        d.AirOps!.Hold = true;
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.TaxiIn }) > 0);
        Assert.Equal(Shelter(af).Unit, d.AirOps!.Station);
        w.Unit(Shelter(af).Unit)!.HP = 0;
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }, 4000) > 0);
        Assert.Equal(Stand(af).Unit, d.AirOps!.Station);
        Assert.Equal(d.Id, Stand(af).Occupant);
    }

    // ---- Determinism ----

    [Fact]
    public void ServiceAndCraterReactionsAreDeterministicAndHashed()
    {
        ulong[] Run()
        {
            var w = Arena.NewSentinel(N, 3);
            var af = Field(w, [(0, 0), (0, 1)]);
            var a = Rearming(w, 185, 130);
            var b = Homebound(w, 185, 175);
            a.MutableCharges[0] = 0;
            b.HP = 120;
            var hashes = new List<ulong>();
            for (var t = 0; t < 2500; t++)
            {
                if (t == 900)
                {
                    Crater(w, af, 25 * Fixed.OneRaw);
                }
                w.Step();
                hashes.Add(w.Hash());
            }
            return [.. hashes];
        }
        Assert.Equal(Run(), Run());

        var (w1, _, inShelter, _) = Parked();
        var h = w1.Hash();
        inShelter.AirOps!.AmmoAcc += 1;
        Assert.NotEqual(h, w1.Hash());
        inShelter.AirOps.AmmoAcc -= 1;
        inShelter.AirOps.Recall = true;
        Assert.NotEqual(h, w1.Hash());
    }
}
