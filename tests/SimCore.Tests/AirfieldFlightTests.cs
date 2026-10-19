using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// The Sentinel airfield, milestone M2 (the game repo's docs/airfield-design.md, sections 1, 1.2 and
// 2.2): an aircraft whose home is an airfield holds, lands (one at a time: the runway lock), rolls
// out, taxis to a station it reserved, parks (fuel does not burn), refuels and takes off again the
// other way, keeping its ID and HP. With no airfield to take it, a Watcher still goes to its ground
// control station. Nothing here is specific to the Watcher: a made-up class-M jet uses the same rules.
public class AirfieldFlightTests
{
    private const int N = 200;

    // A facility at (30, 90) puts the origin at (41, 66): the runway on y 66-76 (centre 71) from x 41
    // to 101 (3 segments), the taxiway y 82-90, row r at y 90 + 10r, column c at x 41 + 28c. Landings
    // come from the east (the hold point at (126, 71)), takeoffs leave eastward.
    private static readonly Cell FacilityCell = new(30, 90);

    private static UnitDef Def(World w, string id, byte player = 1) => w.Player(player)!.Setup.Find(w.Content, id)!;

    internal static Airfield Field(World w, IReadOnlyList<(int, int)> rows, Cell? at = null, int east = 0, byte player = 1) =>
        w.SpawnAirfield(Def(w, "airfield_facility", player), at ?? FacilityCell, 0, rows, 0, east) ?? throw new InvalidOperationException("no airfield");

    // Homebound: a Watcher put down in flight at (x, y) that heads home at once.
    private static Unit Homebound(World w, int x, int y, string kind = "watcher_drone")
    {
        var d = w.Spawn(1, kind, x, y);
        d.Homing = true;
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

    // ParkAll runs until every aircraft has parked once (they may sortie again at once: a full tank
    // needs no service); it returns their stations as they parked, or null.
    private static uint[]? ParkAll(World w, IReadOnlyList<Unit> ds, int max = 12000, Action? each = null)
    {
        var stations = new uint[ds.Count];
        var t = RunUntil(w, () =>
        {
            each?.Invoke();
            for (var i = 0; i < ds.Count; i++)
            {
                if (stations[i] == 0 && ds[i].AirOps is { Phase: FlightPhase.Parked } o)
                {
                    stations[i] = o.Station;
                }
            }
            return stations.All(s => s != 0);
        }, max);
        return t > 0 ? stations : null;
    }

    private static Vec Local(Airfield af, int tenthsX, int tenthsY) => World.AirfieldPoint(af, tenthsX, tenthsY);

    // StopMark is the map point where station st of row r has its aircraft stop.
    private static Vec StopMark(Airfield af, AirfieldRow r, AirfieldStation st)
    {
        var t = af.Type;
        var p = t.RowSlots[st.Slot].ParkIn[^1];
        return Local(af, t.RowW * r.Col * 10 + p.X, (t.SegmentDepth + t.RowH * r.Row) * 10 + p.Y);
    }

    private static (AirfieldRow Row, AirfieldStation St) StationOf(Airfield af, uint unit)
    {
        foreach (var r in af.Rows)
        {
            foreach (var st in r.Stations)
            {
                if (st.Unit == unit)
                {
                    return (r, st);
                }
            }
        }
        throw new InvalidOperationException("no station");
    }

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    // ---- One landing, end to end ----

    [Fact]
    public void AWatcherHoldsLandsTaxisAndParksKeepingItsIdAndHp()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0), (0, 1)]);
        var d = Homebound(w, 175, 150);
        d.HP = 150; // damaged: it wants a loading station (2x repair, M3)
        var id = d.Id;
        var events = new List<AirfieldEvent>();
        var phases = new List<FlightPhase>();
        long lastAlt = long.MaxValue;
        int maxTurn = 0, maxAirTurn = 0;
        long maxSpeedJump = 0;
        var prevFacing = d.Facing;
        long prevSpeed = 0;
        var t = RunUntil(w, () =>
        {
            events.AddRange(w.AirfieldEvents);
            var phase = World.FlightPhaseOf(d);
            if (phases.Count == 0 || phases[^1] != phase)
            {
                phases.Add(phase);
            }
            else if (d.AirOps is { } ops)
            {
                var turn = Math.Abs(Facing.Diff(prevFacing, d.Facing));
                if (ops.Grounded)
                {
                    maxTurn = Math.Max(maxTurn, turn);
                    maxSpeedJump = Math.Max(maxSpeedJump, Math.Abs(ops.Speed - prevSpeed));
                }
                else
                {
                    maxAirTurn = Math.Max(maxAirTurn, turn);
                }
            }
            if (phase == FlightPhase.Final && d.AirOps!.Leg == 2)
            {
                Assert.True(d.Altitude.Raw <= lastAlt, "the glide never climbs");
                lastAlt = d.Altitude.Raw;
            }
            prevFacing = d.Facing;
            prevSpeed = d.AirOps?.Speed ?? 0;
            return phase == FlightPhase.Parked;
        });
        Assert.True(t > 0, $"parks (phases: {string.Join(", ", phases)})");
        Assert.Equal(
            [FlightPhase.Holding, FlightPhase.Final, FlightPhase.Rollout, FlightPhase.TaxiIn, FlightPhase.Parking, FlightPhase.Parked],
            phases);
        Assert.Same(d, w.Unit(id));
        Assert.Equal(150, d.HP);
        Assert.DoesNotContain(events, e => e.Kind == AirfieldEventKind.GoAround);

        // It touched down within a cell of the aim point: 8 cells in from the east threshold.
        var touch = events.Single(e => e.Kind == AirfieldEventKind.Touchdown);
        var aim = Local(af, 600 - 80, 50);
        Assert.True((touch.At - aim).Len().Raw <= Fixed.OneRaw, $"touchdown {touch.At} vs aim {aim}");

        // Smooth: at most two facing steps a tick on the ground (corners are rounded), one in the air;
        // the speed changes at most by its braking a tick (speeds are kept x20).
        Assert.InRange(maxTurn, 0, 2);
        Assert.InRange(maxAirTurn, 0, 1);
        var ac = d.Def.Archetype.Aircraft!;
        Assert.True(maxSpeedJump <= Math.Max(ac.Brake.Raw, ac.TurnSpeed.Raw * World.TicksPerSecond / 2), $"speed jump {maxSpeedJump}");

        // Damaged, it took a loading station; it stands at its stop mark, facing the slot's heading.
        var (row, st) = StationOf(af, d.AirOps!.Station);
        Assert.Equal(StationKind.Loading, st.Kind);
        Assert.Equal(id, st.Occupant);
        Assert.Equal(0u, st.ReservedBy);
        Assert.Equal(StopMark(af, row, st), d.Pos);
        Assert.Equal(0u, af.RunwayLock);
        w.Run(10);
        Assert.Equal(Facing.West, d.Facing); // nose in, toward -x (the facility's side)
        Assert.Equal(0, d.Altitude.Raw); // the pad of a flat map
    }

    [Fact]
    public void FuelDoesNotBurnOnTheGroundAndItRefuelsParked()
    {
        var w = Arena.NewSentinel(N);
        Field(w, [(0, 0)]);
        var d = Homebound(w, 175, 150);
        w.Step();
        d.AirOps!.Hold = true; // as Stop would: it stays parked
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Rollout }) > 0);
        var fuel = d.Lifetime;
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }) > 0);
        Assert.Equal(fuel, d.Lifetime); // roll-out and taxi burn nothing
        d.Lifetime = 1000;
        w.Run(20);
        Assert.Equal(1000 + 20 * 60, d.Lifetime); // a full tank (24000) in 400 ticks: 60 a tick
        w.Run(400);
        Assert.Equal(d.Def.Lifetime, d.Lifetime);
        Assert.Equal(FlightPhase.Parked, World.FlightPhaseOf(d)); // held: no sortie
    }

    // ---- Stations ----

    [Fact]
    public void ItTakesAStandWhenItNeedsNothingAndAShelterWhenItNeedsRearming()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var full = Homebound(w, 185, 150);
        var shot = Homebound(w, 185, 170);
        shot.MutableCharges[0] = 1; // one missile fired
        w.Step();
        Assert.Equal(StationKind.Normal, StationOf(af, full.AirOps!.Station).St.Kind);
        Assert.Equal(StationKind.Loading, StationOf(af, shot.AirOps!.Station).St.Kind);
        Assert.Equal(full.Id, StationOf(af, full.AirOps.Station).St.ReservedBy);
        Assert.Equal(2, af.Landings.Count);
    }

    // ---- The queue and the runway lock ----

    [Fact]
    public void ThreeWatchersQueueStackAndNeverShareTheRunway()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0), (0, 1)]);
        Unit[] ds = [Homebound(w, 185, 130), Homebound(w, 185, 155), Homebound(w, 185, 180)];
        var places = new SortedSet<int>();
        var lastQueue = new List<uint>();
        var parkedAt = ParkAll(w, ds, 12000, () =>
        {
            var onRunway = ds.Where(d => d.AirOps is { } o && (o.Phase is FlightPhase.Final or FlightPhase.Rollout or FlightPhase.TakeoffRoll ||
                (o.Phase == FlightPhase.LineUp && o.Leg == 1))).ToList();
            Assert.True(onRunway.Count <= 1, "one aircraft on the runway at a time");
            if (onRunway.Count == 1)
            {
                Assert.Equal(onRunway[0].Id, af.RunwayLock);
            }
            // Holding at the hold point, the queue's places stack 15 m apart from 40 m.
            var queue = af.Landings.Select(q => q.Aircraft).ToList();
            var settled = queue.SequenceEqual(lastQueue);
            lastQueue = queue;
            for (var i = 0; settled && i < af.Landings.Count; i++)
            {
                var h = w.Unit(af.Landings[i].Aircraft)!;
                if (h.AirOps is { Phase: FlightPhase.Holding } && h.VelZ.Raw == 0 && Cells((h.Pos - Local(af, 850, 50)).Len().Raw) <= 50)
                {
                    Assert.Equal(40 + 15 * Math.Min(i, 3), h.Altitude.Raw / 512);
                    places.Add(i);
                }
            }
        });
        Assert.NotNull(parkedAt);
        Assert.True(places.Count >= 2, $"stacked places seen: {string.Join(", ", places)}");
        Assert.Equal(3, parkedAt.Distinct().Count());
        Assert.Empty(af.Landings);
    }

    [Fact]
    public void MisalignedAtTheFixItGoesAroundAndRejoinsTheQueueAtItsHead()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0), (0, 1)]);
        var d = Homebound(w, 175, 150);
        var other = Homebound(w, 185, 180);
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Final, Leg: 1 } && d.Pos.X.Raw < 136 * Fixed.OneRaw) > 0);
        d.Pos += new Vec(Fixed.Zero, Fixed.FromInt(5)); // a gust: 5 cells off the centre line
        var events = new List<AirfieldEvent>();
        Assert.True(RunUntil(w, () =>
        {
            events.AddRange(w.AirfieldEvents);
            return events.Any(e => e.Kind == AirfieldEventKind.GoAround);
        }, 400) > 0);
        var go = events.FindIndex(e => e.Kind == AirfieldEventKind.GoAround);
        Assert.Equal(d.Id, events[go].Aircraft);
        // It rejoined at the head of the queue (ahead of the other, which asked first): being at the
        // hold point already, it is cleared again at once.
        Assert.Equal((AirfieldEventKind.Cleared, d.Id), (events[go + 1].Kind, events[go + 1].Aircraft));
        Assert.Equal(d.Id, af.RunwayLock);
        Assert.Equal(other.Id, Assert.Single(af.Landings).Aircraft);
        Assert.NotNull(ParkAll(w, [d, other]));
    }

    // ---- Takeoff ----

    [Fact]
    public void ServicedItPushesBackTakesOffTheOtherWayAndFliesBackToItsOrbit()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = w.Spawn(1, "watcher_drone", 170, 160);
        w.Run(5);
        d.Lifetime = 2400; // bingo soon: home to refuel
        d.MutableCharges[0] = 1; // one missile left: a shelter (pushback out of it)
        var orbit = d.OrbitAt;
        var events = new List<AirfieldEvent>();
        var phases = new List<FlightPhase>();
        Vec landingVel = default, takeoffVel = default;
        var t = RunUntil(w, () =>
        {
            events.AddRange(w.AirfieldEvents);
            var phase = World.FlightPhaseOf(d);
            if (phases.Count == 0 || phases[^1] != phase)
            {
                phases.Add(phase);
            }
            if (phase == FlightPhase.Rollout)
            {
                landingVel = d.Vel;
            }
            if (phase == FlightPhase.TakeoffRoll)
            {
                takeoffVel = d.Vel;
            }
            return phases.Count > 2 && phase == FlightPhase.Airborne;
        }, 12000);
        Assert.True(t > 0, $"back in the air (phases: {string.Join(", ", phases)})");
        Assert.Equal(
            [FlightPhase.Airborne, FlightPhase.Holding, FlightPhase.Final, FlightPhase.Rollout, FlightPhase.TaxiIn, FlightPhase.Parking,
             FlightPhase.Parked, FlightPhase.PushBack, FlightPhase.TaxiOut, FlightPhase.LineUp, FlightPhase.TakeoffRoll, FlightPhase.Climb,
             FlightPhase.Airborne],
            phases);
        Assert.True(landingVel.X.Raw < 0, "lands westward, toward the facility");
        Assert.True(takeoffVel.X.Raw > 0, "takes off eastward, away from it");
        Assert.True(d.Lifetime > d.Def.Lifetime - 200, "it left with a full tank");
        Assert.Contains(events, e => e.Kind == AirfieldEventKind.WheelsUp);
        Assert.Equal(0u, af.RunwayLock);
        Assert.Null(d.AirOps);
        Assert.False(d.Homing);
        Assert.Equal(Cell.Of(orbit).Center(), d.OrbitAt);
        Assert.Equal(2, World.StrikesLeft(d)); // rearmed in the shelter before it left (M3b)
        Assert.All(af.Rows[0].Stations, s => Assert.Equal((0u, 0u), (s.Occupant, s.ReservedBy)));
        w.Run(1500);
        Assert.InRange(Cells((d.Pos - orbit).Len().Raw), 15, 30); // orbiting where it was
    }

    [Fact]
    public void StopKeepsItParkedAndAMoveOrderSendsItOff()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = Homebound(w, 175, 150);
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.TaxiIn }) > 0);
        w.Order(1, CommandKind.Stop, [d]);
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }) > 0);
        w.Run(600); // refuelled, but held
        Assert.Equal(FlightPhase.Parked, World.FlightPhaseOf(d));
        w.Order(1, CommandKind.Move, [d], cell: new Cell(60, 150));
        Assert.True(RunUntil(w, () => d.AirOps is null, 4000) > 0);
        Assert.True(Cells((d.OrbitAt - new Cell(60, 150).Center()).Len().Raw) <= 2, "it flies to its new point");
        Assert.Equal(0u, af.RunwayLock);
    }

    // ---- The lane rule ----

    [Fact]
    public void AnInboundAircraftWaitsAtTheLaneMouthWhileAnotherPushesBackAndNeverDeadlocks()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var a = Homebound(w, 175, 150);
        a.MutableCharges[0] = 1; // the shelter: it will push back into the lane
        Assert.True(RunUntil(w, () => a.AirOps is { Phase: FlightPhase.Parked }) > 0);
        a.AirOps!.Hold = true;
        var b = Homebound(w, 185, 170);
        Assert.True(RunUntil(w, () => b.AirOps is { Phase: FlightPhase.TaxiIn }) > 0);
        w.Order(1, CommandKind.Move, [a], cell: new Cell(150, 30)); // a leaves: the pushback
        var waited = 0;
        Assert.True(RunUntil(w, () =>
        {
            waited = Math.Max(waited, b.AirOps?.Wait ?? 0);
            return b.AirOps is { Phase: FlightPhase.Parked };
        }) > 0);
        Assert.InRange(waited, 1, af.Type.TaxiWaitTicks);
    }

    // ---- Homes ----

    [Fact]
    public void WithEveryStationTakenItGoesToItsGroundControlStationAsBefore()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        Unit[] parked = [Homebound(w, 175, 150), Homebound(w, 185, 175)];
        w.Step();
        foreach (var p in parked)
        {
            p.AirOps!.Hold = true;
        }
        Assert.True(RunUntil(w, () => parked.All(p => p.AirOps is { Phase: FlightPhase.Parked }), 12000) > 0);
        var station = w.Spawn(1, "watcher_station", 150, 160);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(150, 120), ability: "watcher_launch");
        w.Run(400);
        var d = w.All("watcher_drone").Single(u => u.Source == station.Id);
        d.Homing = true;
        Assert.True(RunUntil(w, () => d.Recovered || d.AirOps is not null, 3000) > 0);
        Assert.Null(d.AirOps);
        Assert.True(d.Recovered, "its station recovered it");
        Assert.True(station.RelaunchPending);
        Assert.All(af.Rows[0].Stations, st => Assert.Contains(st.Occupant, parked.Select(p => p.Id)));
    }

    [Fact]
    public void WithAFreeStationTheAirfieldIsPreferredToTheGroundControlStation()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var station = w.Spawn(1, "watcher_station", 150, 160);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(150, 120), ability: "watcher_launch");
        w.Run(400);
        var d = Assert.Single(w.All("watcher_drone"));
        d.Homing = true;
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }) > 0);
        Assert.False(d.Recovered);
        Assert.Equal(station.Id, d.Source);
        Assert.Equal(1, w.Aloft(station, d.Def.Archetype.Index)); // parked, it still counts as its station's
        Assert.Equal(d.Id, StationOf(af, d.AirOps!.Station).St.Occupant);
    }

    [Fact]
    public void AnEnemyAirfieldIsNoHome()
    {
        var w = Arena.NewSentinel(N, edit: m => m with { Players = [m.Players[0], m.Players[1] with { Faction = "sentinel", Presets = [] }] });
        Field(w, [(0, 0)], player: 2);
        Assert.Equal(2, w.Airfields[0].Owner);
        var station = w.Spawn(1, "watcher_station", 150, 160);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(150, 120), ability: "watcher_launch");
        w.Run(400);
        var d = Assert.Single(w.All("watcher_drone"));
        d.Homing = true;
        Assert.True(RunUntil(w, () => d.Recovered || d.AirOps is not null, 3000) > 0);
        Assert.True(d.Recovered, "its own station, not the enemy's airfield");

        var lone = Homebound(w, 175, 150); // no station of its own either
        w.Run(100);
        Assert.Null(lone.AirOps);
        Assert.False(lone.Homing, "no home: it orbits on until it runs dry");
    }

    // ---- A made-up class-M jet: the same rules ----

    private const string JetFixture = """
        {"archetypes":[
          {"id":"test_jet","name":"Test jet","glyph":"y","tags":["drone"],"locomotor":"flying","armor":"drone","hp":300,"speed":120,"turnRate":2,
           "vision":200,"lifetime":24000,"cost":0,
           "flight":{"height":1500,"climbRate":100,"launchHeight":10,"orbitRadius":300,"turnRate":30,"interceptible":"watcher","reserve":300,"recoverRange":40,"turnaround":600},
           "aircraft":{"sizeClass":"M","span":60,"length":70,"landing":"runway","minLandRun":700,"minTakeoffRun":400,
             "approach":{"fixDistance":300,"fixHeight":150,"touchdown":100,"flare":50,"rolloutDecel":10,"brake":20},
             "speeds":{"approach":60,"touchdown":55,"taxi":30,"turn":15,"pushback":5,"takeoff":80,"takeoffAccel":15},
             "service":{"refuelTicks":600,"rearmTicksPerRound":200,"repairHpPerSecond":15},
             "groundVision":60,"groundEye":30,"holdReserve":1200,"sortie":true}}
        ]}
        """;

    internal static World JetWorld(ulong seed = 1)
    {
        var sources = TestContent.MvpSources();
        sources.Add(new ContentSource("fixtures/jet.json", JetFixture));
        var i = sources.FindIndex(s => s.Name.EndsWith("factions/sentinel.json", StringComparison.Ordinal));
        sources[i] = new ContentSource(sources[i].Name, sources[i].Json.Replace("\"watcher_drone\",", "\"watcher_drone\", \"test_jet\",", StringComparison.Ordinal));
        var content = ContentLoader.Load(sources);
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(content, seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(N, N), MatchCompiler.Compile(content, setup));
    }

    [Fact]
    public void AClassMJetLandsOnlyWhereItsMinimumLandingRunFits()
    {
        var w = JetWorld();
        var near = Field(w, [(0, 0)], at: new Cell(30, 60));         // 3 segments: 60 cells, hold point (126, 41)
        var far = Field(w, [(0, 0)], at: new Cell(30, 150), east: 1); // 4 segments: 80 cells, hold point (146, 131)
        var watcher = Homebound(w, 150, 35);
        var jet = Homebound(w, 150, 38, "test_jet");
        w.Step();
        Assert.Equal(near.Id, watcher.AirOps!.Airfield); // the nearest that takes it
        Assert.Equal(far.Id, jet.AirOps!.Airfield);      // 70 cells to land: only the long runway
        var parked = ParkAll(w, [watcher, jet]);
        Assert.NotNull(parked);
        Assert.Equal(StationOf(near, parked[0]).St.Unit, parked[0]);
        Assert.Equal(StationOf(far, parked[1]).St.Unit, parked[1]);
    }

    // ---- Determinism ----

    [Fact]
    public void TheSameSeedGivesTheSameHashes()
    {
        ulong[] Run()
        {
            var w = Arena.NewSentinel(N, 7);
            Field(w, [(0, 0), (0, 1)]);
            var a = Homebound(w, 185, 130);
            Homebound(w, 185, 175);
            a.MutableCharges[0] = 1;
            var hashes = new List<ulong>();
            for (var t = 0; t < 3000; t++)
            {
                w.Step();
                hashes.Add(w.Hash());
            }
            return [.. hashes];
        }
        var one = Run();
        Assert.Equal(one, Run());
    }

    [Fact]
    public void AirOpsAreHashed()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, [(0, 0)]);
        var d = Homebound(w, 175, 150);
        w.Step();
        var h = w.Hash();
        d.AirOps!.Wait = 3;
        Assert.NotEqual(h, w.Hash());
        d.AirOps.Wait = 0;
        Assert.Equal(h, w.Hash());
        af.RunwayLock = 77;
        Assert.NotEqual(h, w.Hash());
    }
}
