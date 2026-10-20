using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Jets at the airfield (E9; the plan's sections 2.6 and 2.12, decision D17): the overhead break pattern (the
// initial point, the run-in, the break at midfield, the downwind leg with the gear coming down, the
// descending base turn, the short final), aerobraking after touchdown, the takeoff on military power when its
// roll fits in half the open runway and on afterburner otherwise, the climb-out on the fixed-wing envelope
// handed to free flight inside the corridor it needs, and the corridor / pattern-on-map check applied when an
// aircraft picks its home (the placement keeps the type's 30 cells, so every existing airfield is valid).
// The drones keep their straight-in approach (their tests are unchanged).
public class JetAirfieldTests
{
    private const int N = 240;

    private static Unit Spawn(World w, string jet, int x, int y)
    {
        var d = w.Spawn(1, jet == "multirole_jet" ? "Multirole 500 lb" : jet, x, y, Facing.East);
        d.Homing = true; // heading home at once
        return d;
    }

    private static Airfield Field(World w, int fx, int fy, int east = 1, IReadOnlyList<(int, int)>? rows = null) =>
        AirfieldFlightTests.Field(w, rows ?? [(0, 0)], at: new Cell(fx, fy), east: east);

    private static (long X, long Y) Local(Airfield af, Vec p) => World.AirfieldMapToLocal(af, p);

    private static long Cells(long raw) => raw / Fixed.OneRaw;

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

    private static void Crater(World w, Airfield af, long xRaw) =>
        w.RunwayBlast(World.AirfieldLocalToMap(af, xRaw, 5 * Fixed.OneRaw), new RunwayEffect(CraterKind.Small, 0));

    private static long RunwayEast(Airfield af) => (long)af.Type.SegmentCells * (af.East + 1) * Fixed.OneRaw;

    private static long RunwayWest(Airfield af) => (long)af.Type.SegmentCells * af.West * Fixed.OneRaw;

    private static long Centre(Airfield af) => (long)af.Type.RunwayCells * Fixed.OneRaw / 2;

    // ---- The overhead break ----

    [Theory]
    [InlineData("multirole_jet")]
    [InlineData("fighter_jet")]
    public void AJetFliesTheOverheadBreakWithinItsEnvelopeAndAerobrakes(string jet)
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, 30, 120); // 4 segments: 80 cells, east end x 121, centre line y 101
        var d = Spawn(w, jet, 200, 60);
        var ac = d.Def.Archetype.Aircraft!;
        var f = d.Def.Archetype.Flight!;
        var g = f.Wing!;
        Assert.Equal(ApproachPattern.Overhead, ac.Pattern);
        var steps = new List<ApproachStep>();
        var events = new List<(AirfieldEvent E, long Speed, long Alt)>();
        var prev = d.Vel;
        var prevFinal = false;
        long worstTurn = 0;
        var ok = RunUntil(w, () =>
        {
            foreach (var e in w.AirfieldEvents.Where(e => e.Aircraft == d.Id))
            {
                events.Add((e, d.AirOps?.Speed ?? 0, d.Altitude.Raw - af.PadRaw));
            }
            var st = World.ApproachStepOf(d);
            if (st != ApproachStep.None && (steps.Count == 0 || steps[^1] != st))
            {
                steps.Add(st);
            }
            var final = d.AirOps is { Phase: FlightPhase.Final };
            if (final && prevFinal)
            {
                // Its turn a tick never beats a(S) / 400 (raw a tick), a raw unit of rounding aside.
                long px = prev.X.Raw, py = prev.Y.Raw, vx = d.Vel.X.Raw, vy = d.Vel.Y.Raw;
                var pl = Math.Max(1, (long)Math.Sqrt((double)(px * px + py * py)));
                var turn = Math.Abs(px * vy - py * vx) / pl;
                var allowed = g.LateralAccel(d.AirOps!.Speed) / 400 + 2;
                worstTurn = Math.Max(worstTurn, turn * 1000 / allowed);
                Assert.True(turn <= allowed, $"turn {turn} > a(S) {allowed} at {World.ApproachStepOf(d)}");
                Assert.True(Math.Abs(d.VelZ.Raw) <= Math.Max(f.ClimbPerTick, g.DivePerTick), "its height changes within its climb and dive rates");
            }
            prevFinal = final;
            prev = d.Vel;
            return d.AirOps is { Phase: FlightPhase.Parked };
        }, 8000);
        Assert.True(ok > 0, $"it lands and parks ({World.FlightPhaseOf(d)}, steps {string.Join(",", steps)})");
        Assert.Equal(
            [ApproachStep.Join, ApproachStep.Initial, ApproachStep.Break, ApproachStep.Downwind, ApproachStep.Base,
             ApproachStep.Final, ApproachStep.Glide, ApproachStep.Aerobrake, ApproachStep.Rollout], steps);
        var kinds = events.Select(e => e.E.Kind).ToList();
        Assert.DoesNotContain(AirfieldEventKind.GoAround, kinds);
        AirfieldEventKind[] order = [AirfieldEventKind.Cleared, AirfieldEventKind.Initial, AirfieldEventKind.Break, AirfieldEventKind.GearDown,
            AirfieldEventKind.Perch, AirfieldEventKind.Touchdown, AirfieldEventKind.NoseDown, AirfieldEventKind.RunwayVacated, AirfieldEventKind.Parked];
        Assert.Equal(order, kinds.Where(order.Contains).ToArray());
        (AirfieldEvent E, long Speed, long Alt) At(AirfieldEventKind k) => events.Single(e => e.E.Kind == k);
        long east = RunwayEast(af), west = RunwayWest(af), c = Centre(af), r = ac.PatternRadius.Raw, mid = (east + west) / 2;
        // The initial point: on the centre line, its initial distance out, at about pattern height.
        var ip = Local(af, At(AirfieldEventKind.Initial).E.At);
        Assert.InRange(ip.X - east, ac.InitialDistance.Raw - ac.JoinRadius.Raw, ac.InitialDistance.Raw + ac.JoinRadius.Raw / 2);
        Assert.InRange(Math.Abs(ip.Y - c), 0, r);
        // The break at midfield, at break speed and pattern height.
        var brk = At(AirfieldEventKind.Break);
        Assert.InRange(Local(af, brk.E.At).X, mid - Fixed.OneRaw, mid + Fixed.OneRaw);
        Assert.InRange(brk.Speed, ac.BreakSpeed.Raw * World.TicksPerSecond - 2 * ac.TakeoffAccel.Raw, ac.BreakSpeed.Raw * World.TicksPerSecond); // slowing from here
        Assert.InRange(brk.Alt, ac.PatternHeightRaw - 3 * 512, ac.PatternHeightRaw + 3 * 512);
        // The gear comes down rolling out on the downwind leg abeam midfield, two pattern radii out, away from
        // the rows (the -y side of the airfield's frame), still at pattern height.
        var gear = At(AirfieldEventKind.GearDown);
        var gp = Local(af, gear.E.At);
        Assert.InRange(c - gp.Y, r * 3 / 2, r * 5 / 2);
        Assert.InRange(gp.X, mid - 2 * r, mid + 2 * r);
        Assert.InRange(gear.Alt, ac.PatternHeightRaw - 3 * 512, ac.PatternHeightRaw + 3 * 512);
        // The perch: abeam its base point, slowed to its approach speed.
        var perch = At(AirfieldEventKind.Perch);
        Assert.InRange(Local(af, perch.E.At).X - east, ac.FixDistance.Raw + r, ac.FixDistance.Raw + r + 2 * Fixed.OneRaw);
        Assert.Equal(ac.ApproachSpeed.Raw * World.TicksPerSecond, perch.Speed);
        // Touchdown on its point; the nose comes down at its aerobrake speed, before it turns off.
        Assert.InRange(Local(af, At(AirfieldEventKind.Touchdown).E.At).X, east - ac.Touchdown.Raw - 2 * Fixed.OneRaw, east - ac.Touchdown.Raw + Fixed.OneRaw);
        Assert.True(At(AirfieldEventKind.NoseDown).Speed <= ac.AerobrakeSpeed.Raw * World.TicksPerSecond);
        Assert.True(worstTurn > 800, $"its break and base turns pull most of what it has ({worstTurn} per mille)");
        Assert.Equal(0, w.AirEdgeClamps);
    }

    [Fact]
    public void ACraterCuttingItsStretchOnTheDownwindSendsItAroundAndItLandsOnWhatIsLeft()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, 30, 120, 2); // 5 segments: 100 cells
        var d = Spawn(w, "multirole_jet", 200, 60);
        Assert.True(RunUntil(w, () => World.ApproachStepOf(d) == ApproachStep.Downwind, 8000) > 0);
        Crater(w, af, 15 * Fixed.OneRaw); // closes 11.5 to 18.5: 81.5 cells are left to the east
        var kinds = new List<AirfieldEventKind>();
        Assert.True(RunUntil(w, () =>
        {
            kinds.AddRange(w.AirfieldEvents.Where(e => e.Aircraft == d.Id).Select(e => e.Kind));
            return d.AirOps is { Phase: FlightPhase.Parked };
        }, 9000) > 0, "it lands after all");
        Assert.Equal(AirfieldEventKind.GoAround, kinds[0]);
        Assert.True(kinds.IndexOf(AirfieldEventKind.Break) > kinds.IndexOf(AirfieldEventKind.Cleared), "a second overhead");
        Assert.Contains(AirfieldEventKind.Touchdown, kinds);
        Assert.Equal(0, w.AirEdgeClamps);
    }

    [Fact]
    public void TheDronesKeepTheirStraightInApproach()
    {
        var w = Arena.NewSentinel(N);
        Field(w, 30, 120);
        var d = Spawn(w, "watcher_drone", 200, 60);
        var seen = new List<ApproachStep>();
        Assert.True(RunUntil(w, () =>
        {
            seen.Add(World.ApproachStepOf(d));
            return d.AirOps is { Phase: FlightPhase.Parked };
        }, 8000) > 0);
        Assert.Contains(ApproachStep.Join, seen);
        Assert.Contains(ApproachStep.Glide, seen);
        Assert.DoesNotContain(seen, s => s is ApproachStep.Initial or ApproachStep.Break or ApproachStep.Downwind or ApproachStep.Base or ApproachStep.Aerobrake);
    }

    // ---- The takeoff rule and the climb-out ----

    // TakeOff lands the jet at af, lets it sortie (a full tank: at once) with `prepare` run once it parks, and
    // records the takeoff roll and the climb-out up to free flight.
    private static (bool Ab, long Roll, List<long> Speeds, long HandX, long HandSpeed, Throttle RollThrottle) TakeOff(
        World w, Airfield af, Unit d, Action? prepare = null)
    {
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }, 8000) > 0, "it lands and parks");
        prepare?.Invoke();
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.TakeoffRoll }, 4000) > 0, "it taxis out and lines up");
        var ab = w.AirfieldEvents.Any(e => e.Kind == AirfieldEventKind.Afterburner && e.Aircraft == d.Id);
        var rollThrottle = World.ThrottleOf(d);
        var start = Local(af, d.Pos).X;
        long roll = 0;
        var speeds = new List<long>();
        Assert.True(RunUntil(w, () =>
        {
            ab |= w.AirfieldEvents.Any(e => e.Kind == AirfieldEventKind.Afterburner && e.Aircraft == d.Id);
            if (w.AirfieldEvents.Any(e => e.Kind == AirfieldEventKind.WheelsUp && e.Aircraft == d.Id))
            {
                roll = Local(af, d.Pos).X - start;
            }
            if (d.AirOps is { Phase: FlightPhase.Climb } o)
            {
                speeds.Add(o.Speed);
            }
            return d.AirOps is null;
        }, 2000) > 0, "it climbs out and flies free");
        return (ab, roll, speeds, Local(af, d.Pos).X, d.Vel.Len().Raw * World.TicksPerSecond, rollThrottle);
    }

    [Theory]
    [InlineData(2, false)] // 5 segments: 90 cells open from its line-up; its military roll (34 cells) fits in half
    [InlineData(1, false)] // 4 segments: 70 cells open; 34 still fits in 35
    public void OnALongRunwayItTakesOffOnMilitaryPower(int east, bool ab)
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, 30, 120, east);
        var d = Spawn(w, "multirole_jet", 200, 60);
        var ac = d.Def.Archetype.Aircraft!;
        var t = TakeOff(w, af, d);
        Assert.Equal(ab, t.Ab);
        Assert.Equal(Throttle.Military, t.RollThrottle);
        var v = ac.TakeoffSpeed.Raw * World.TicksPerSecond;
        var milRoll = v * v / (2 * ac.MilitaryAccel.Raw * World.TicksPerSecond);
        Assert.InRange(t.Roll, milRoll - 2 * Fixed.OneRaw, milRoll + 2 * Fixed.OneRaw);
    }

    [Fact]
    public void OnAShortStretchItTakesOffOnAfterburnerWithAShorterRoll()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, 30, 120);
        var d = Spawn(w, "multirole_jet", 200, 60);
        var ac = d.Def.Archetype.Aircraft!;
        // A crater 8 cells in closes the runway to 11.5 cells in: the stretch keeps its 65 cells to land
        // (68.5), but its westmost connector is now 30 cells in: 50 cells open, and 34 is more than half.
        var t = TakeOff(w, af, d, () => Crater(w, af, 8 * Fixed.OneRaw));
        Assert.True(t.Ab, "the afterburner takeoff event");
        Assert.Equal(Throttle.Afterburner, t.RollThrottle);
        var v = ac.TakeoffSpeed.Raw * World.TicksPerSecond;
        var abRoll = v * v / (2 * ac.TakeoffAccel.Raw * World.TicksPerSecond);
        Assert.InRange(t.Roll, abRoll - 2 * Fixed.OneRaw, abRoll + 2 * Fixed.OneRaw);
        Assert.True(Local(af, d.Pos).X > 30 * Fixed.OneRaw);
    }

    [Theory]
    [InlineData("multirole_jet", 30)]  // in the open: 119 cells beyond the runway's east end
    [InlineData("fighter_jet", 30)]
    [InlineData("multirole_jet", 88)]  // the east end 61 cells from the map's edge: just its corridor
    [InlineData("fighter_jet", 88)]
    public void TheClimbOutStaysInItsEnvelopeAndFliesFreeInsideItsCorridor(string jet, int fx)
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, fx, 120);
        var d = Spawn(w, jet, fx + 150 > N - 30 ? N - 60 : fx + 150, 40);
        var ac = d.Def.Archetype.Aircraft!;
        var g = d.Def.Archetype.Flight!.Wing!;
        var t = TakeOff(w, af, d);
        var th = t.Ab ? Throttle.Afterburner : Throttle.Military;
        for (var i = 1; i < t.Speeds.Count; i++)
        {
            var ds = t.Speeds[i] - t.Speeds[i - 1];
            Assert.True(ds >= 0 && ds <= (g.Thrust(th) - g.DragAt(t.Speeds[i - 1])) / World.TicksPerSecond + 1, $"speed step {ds} within its envelope");
        }
        Assert.True(t.HandSpeed >= g.Corner - 2 * World.TicksPerSecond, $"free at its corner speed ({t.HandSpeed} vs {g.Corner})");
        Assert.True(t.HandX - RunwayEast(af) <= ac.CorridorNeed.Raw, $"free {Cells(t.HandX - RunwayEast(af))} cells past the end, within its corridor");
        w.Run(400);
        Assert.True(d.Alive);
        Assert.Equal(0, w.AirEdgeClamps); // edge avoidance takes it from there
        Assert.Null(d.AirOps);
    }

    // ---- The corridor and the pattern on the map (the home check) ----

    [Fact]
    public void AJetNeverPicksAFieldWhoseCorridorLeavesTheMapButADroneDoes()
    {
        var w = Arena.NewSentinel(N);
        // Squeezed: the east end at x 200, 40 cells from the edge (the placement's 30 hold; a jet needs 60).
        var squeezed = Field(w, 109, 50);
        var open = Field(w, 30, 160);
        var jet = Spawn(w, "multirole_jet", 215, 70);
        w.Step();
        Assert.Equal(open.Id, jet.AirOps!.Airfield);
        // The same fields in another world: a Watcher (no corridor need; its straight-in join fits there over a
        // displaced threshold, E11) takes the nearer one.
        var w2 = Arena.NewSentinel(N);
        var squeezed2 = Field(w2, 109, 50);
        Field(w2, 30, 160);
        var drone = Spawn(w2, "watcher_drone", 215, 72);
        w2.Step();
        Assert.Equal(squeezed2.Id, drone.AirOps!.Airfield);
        Assert.Equal(squeezed.Origin, squeezed2.Origin);
        Assert.True(RunUntil(w, () => jet.AirOps is { Phase: FlightPhase.Parked }, 9000) > 0, "it lands at the open field");
        Assert.Equal(0, w.AirEdgeClamps);
    }

    [Fact]
    public void OnALongerSqueezedRunwayItLandsOnADisplacedThreshold()
    {
        var w = Arena.NewSentinel(N);
        var def = w.Player(1)!.Setup.Find(w.Content, "airfield_facility")!;
        // 5 segments (one west of the first block): x 100 to 200, 40 cells from the edge. Its 60 cells of
        // corridor need the approach end 20 cells in (local x 60): 80 cells are left, more than its 65.
        var af = w.SpawnAirfield(def, new Cell(109, 50), 0, [(0, 0)], 1, 1) ?? throw new InvalidOperationException("no airfield");
        var d = Spawn(w, "multirole_jet", 215, 70);
        var ac = d.Def.Archetype.Aircraft!;
        w.Step();
        Assert.Equal(af.Id, d.AirOps!.Airfield);
        AirfieldEvent? touch = null;
        Assert.True(RunUntil(w, () =>
        {
            touch ??= w.AirfieldEvents.FirstOrDefault(e => e.Kind == AirfieldEventKind.Touchdown && e.Aircraft == d.Id) is { Aircraft: not 0 } e ? e : null;
            return d.AirOps is { Phase: FlightPhase.Parked };
        }, 9000) > 0, "it lands and parks");
        var displaced = RunwayEast(af) - 20 * Fixed.OneRaw;
        Assert.InRange(Local(af, touch!.Value.At).X, displaced - ac.Touchdown.Raw - 2 * Fixed.OneRaw, displaced - ac.Touchdown.Raw + Fixed.OneRaw);
        Assert.Equal(0, w.AirEdgeClamps);
    }

    // E11: a drone flying straight in has its join's turn circle checked too (its corridor without a number): the
    // circle (the Watcher's reaches about 64 cells beyond the approach end, 29 to the side) must keep its far and
    // outer sides inside the map by the band the join's edge avoidance keeps clear (about 14.6 cells). Before, a
    // Watcher whose circle reached into that band circled on its join for good: at a 4-segment field 30 to 60 cells
    // from the east edge, or coming in from the north at a field 50 cells from the north edge, it never landed. Now
    // it lands, on a displaced threshold where its circle needs one; a field where no threshold leaves room (30
    // cells: 74 cells at most, it needs about 79) is never its home. The Overseer's smaller circle (73) fits there.
    [Theory]
    [InlineData(50, 120)] // the east end 50 cells from the edge: a displaced threshold
    [InlineData(100, 20)] // 100 cells from the edge, coming in from the north: the join on the far (rows') side
    public void AWatcherNearTheMapsEdgeLandsWithItsJoinInsideTheMap(int squeeze, int fromY)
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, 149 - squeeze, 50); // 4 segments
        Assert.Equal(squeeze, N - Cells(World.AirfieldLocalToMap(af, RunwayEast(af), Centre(af)).X.Raw));
        var d = Spawn(w, "watcher_drone", 120, fromY);
        var ac = d.Def.Archetype.Aircraft!;
        Assert.Equal(ac.ApproachRadius.Raw, d.Def.Archetype.Flight!.RadiusAt(ac.ApproachSpeed.Raw));
        w.Step();
        Assert.Equal(af.Id, d.AirOps!.Airfield);
        AirfieldEvent? touch = null;
        long east = 0, north = long.MaxValue;
        Assert.True(RunUntil(w, () =>
        {
            touch ??= w.AirfieldEvents.FirstOrDefault(e => e.Kind == AirfieldEventKind.Touchdown && e.Aircraft == d.Id) is { Aircraft: not 0 } e ? e : null;
            (east, north) = (Math.Max(east, d.Pos.X.Raw), Math.Min(north, d.Pos.Y.Raw));
            return d.AirOps is { Phase: FlightPhase.Parked };
        }, 6000) > 0, "it lands and parks");
        Assert.Equal(0, w.AirEdgeClamps);
        Assert.True(east <= (N - 10) * Fixed.OneRaw && north >= 10 * Fixed.OneRaw, $"it came within {Cells(N * Fixed.OneRaw - east)} cells of the east edge, {Cells(north)} of the north");
        var shortOfEnd = RunwayEast(af) - Local(af, touch!.Value.At).X;
        Assert.True(squeeze >= 100 || shortOfEnd > ac.Touchdown.Raw + 20 * Fixed.OneRaw, $"touchdown {Cells(shortOfEnd)} cells short of the end: a displaced threshold");
    }

    [Fact]
    public void AFieldWhereTheWatchersJoinNeverFitsIsNotItsHomeButTheOverseersFits()
    {
        var w = Arena.NewSentinel(N);
        var squeezed = Field(w, 119, 50); // 4 segments, the east end 30 cells from the edge
        var open = Field(w, 30, 160);
        var watcher = Spawn(w, "watcher_drone", 215, 72); // nearer the squeezed one
        w.Step();
        Assert.Equal(open.Id, watcher.AirOps!.Airfield);

        var w2 = Arena.NewSentinel(N);
        var squeezed2 = Field(w2, 119, 50);
        Field(w2, 30, 160);
        var overseer = Spawn(w2, "overseer_drone", 215, 72);
        w2.Step();
        Assert.Equal(squeezed2.Id, overseer.AirOps!.Airfield);
        Assert.Equal(squeezed.Origin, squeezed2.Origin);
        Assert.True(RunUntil(w2, () => overseer.AirOps is { Phase: FlightPhase.Parked }, 6000) > 0, "the Overseer lands there");
        Assert.Equal(0, w2.AirEdgeClamps);
    }

    [Fact]
    public void AFieldThatCannotTakeAJetDoesNotBuildIt()
    {
        var w = Arena.NewSentinel(N);
        var squeezed = Field(w, 109, 50); // 4 segments 40 cells from the edge: never a jet's home
        var open = Field(w, 30, 160);
        foreach (var af in new[] { squeezed, open })
        {
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [af.Facility], Unit = "fighter_jet" });
        }
        w.Step();
        var sq = w.Unit(squeezed.Facility)!;
        var op = w.Unit(open.Facility)!;
        Assert.DoesNotContain(sq.ProductionQueue.Concat(sq.Bays.Select(b => b.Def)), i => i >= 0 && w.Match.Defs[i].Archetype.Id == "fighter_jet");
        Assert.Contains(op.ProductionQueue.Concat(op.Bays.Select(b => b.Def)), i => i >= 0 && w.Match.Defs[i].Archetype.Id == "fighter_jet");
    }

    [Fact]
    public void WithNoFieldItsPatternFitsAJetHasNoHomeAndOrbitsOn()
    {
        var w = Arena.NewSentinel(N);
        Field(w, 109, 50); // the only field: its corridor is 40 cells
        var jet = Spawn(w, "multirole_jet", 150, 120);
        w.Run(40);
        Assert.Null(jet.AirOps);
        Assert.True(jet.Alive);
    }

    [Fact]
    public void WhenTheFarSideIsOffTheMapItBreaksOverTheRows()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, 30, 29); // the runway on y 5-15: the -y side has 10 cells, its downwind needs 22.5
        var d = Spawn(w, "multirole_jet", 200, 80);
        var r = d.Def.Archetype.Aircraft!.PatternRadius.Raw;
        AirfieldEvent? gear = null;
        Assert.True(RunUntil(w, () =>
        {
            gear ??= w.AirfieldEvents.FirstOrDefault(e => e.Kind == AirfieldEventKind.GearDown && e.Aircraft == d.Id) is { Aircraft: not 0 } e ? e : null;
            return d.AirOps is { Phase: FlightPhase.Parked };
        }, 8000) > 0, "it lands");
        Assert.NotNull(gear);
        Assert.InRange(Local(af, gear.Value.At).Y - Centre(af), r * 3 / 2, r * 5 / 2); // the rows' side
        Assert.Equal(0, w.AirEdgeClamps);
    }

    [Theory]
    [InlineData(8, 140, 30)]   // a quarter turn: the runway runs south, its approach end at y 121
    [InlineData(16, 160, 110)] // a half turn: the runway runs west, its approach end at x 79
    public void OnATurnedFieldThePatternTurnsWithIt(int facing, int fx, int fy)
    {
        var w = Arena.NewSentinel(N);
        var def = w.Player(1)!.Setup.Find(w.Content, "airfield_facility")!;
        var af = w.SpawnAirfield(def, new Cell(fx, fy), facing, [(0, 0)], 0, 1) ?? throw new InvalidOperationException("no airfield");
        var d = Spawn(w, "fighter_jet", 120, 120);
        var r = d.Def.Archetype.Aircraft!.PatternRadius.Raw;
        AirfieldEvent? gear = null;
        Assert.True(RunUntil(w, () =>
        {
            gear ??= w.AirfieldEvents.FirstOrDefault(e => e.Kind == AirfieldEventKind.GearDown && e.Aircraft == d.Id) is { Aircraft: not 0 } e ? e : null;
            return d.AirOps is { Phase: FlightPhase.Parked };
        }, 9000) > 0, $"it lands ({World.FlightPhaseOf(d)}, {World.ApproachStepOf(d)})");
        Assert.NotNull(gear);
        Assert.InRange(Math.Abs(Local(af, gear.Value.At).Y - Centre(af)), r * 3 / 2, r * 5 / 2);
        Assert.Equal(0, w.AirEdgeClamps);
    }

    // ---- Traffic ----

    [Fact]
    public void TwoJetsAndTwoDronesQueueAndNeverShareTheRunway()
    {
        var w = Arena.NewSentinel(N, 3);
        var af = Field(w, 30, 120, 1, [(0, 0), (0, 1)]);
        Unit[] all =
        [
            Spawn(w, "multirole_jet", 200, 60), Spawn(w, "fighter_jet", 210, 170),
            Spawn(w, "watcher_drone", 190, 100), Spawn(w, "watcher_drone", 180, 140),
        ];
        var parked = new bool[all.Length];
        var overhead = new bool[all.Length];
        Assert.True(RunUntil(w, () =>
        {
            var onRunway = all.Where(u => u.AirOps is { Phase: FlightPhase.Final or FlightPhase.Rollout or FlightPhase.TakeoffRoll }).ToList();
            Assert.True(onRunway.Count <= 1, "one aircraft at a time on final, rolling out or taking off");
            if (onRunway.Count == 1 && onRunway[0].AirOps!.Phase == FlightPhase.Final)
            {
                Assert.Equal(onRunway[0].Id, af.RunwayLock);
            }
            for (var i = 0; i < all.Length; i++)
            {
                parked[i] |= all[i].AirOps is { Phase: FlightPhase.Parked };
                overhead[i] |= World.ApproachStepOf(all[i]) == ApproachStep.Break;
            }
            return parked.All(p => p);
        }, 16000) > 0, "all four land");
        Assert.Equal([true, true, false, false], overhead);
        Assert.All(all, u => Assert.True(u.Alive));
        Assert.Equal(0, w.AirEdgeClamps);
    }

    // ---- Craters on the roll ----

    [Theory]
    [InlineData(100, false)] // full HP: it runs into the crater's edge and loses half its HP
    [InlineData(40, true)]   // 40%: the crater finishes it there (a crash on the ground: no wreck, no ejection)
    public void RollingOutIntoAFreshCraterHurtsOrWrecksItWithTheCrashCause(int hpPercent, bool dies)
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w, 30, 120);
        var d = Spawn(w, "multirole_jet", 200, 60);
        d.HP = d.Def.MaxHp * hpPercent / 100;
        var hp = d.HP;
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Rollout }, 8000) > 0);
        var lx = Local(af, d.Pos).X;
        Crater(w, af, lx - 3 * Fixed.OneRaw - af.Type.SmallCrater.Radius.Raw); // 3 cells ahead at about 7 cells/s
        var losses = new List<AirLoss>();
        var hit = false;
        RunUntil(w, () =>
        {
            losses.AddRange(w.AirLosses.Where(l => l.Aircraft == d.Id));
            hit |= w.AirfieldEvents.Any(e => e.Kind == AirfieldEventKind.CraterHit && e.Aircraft == d.Id);
            return !d.Alive || d.AirOps is { Phase: FlightPhase.TaxiIn or FlightPhase.Parked };
        }, 600);
        Assert.True(hit);
        if (dies)
        {
            var loss = Assert.Single(losses);
            Assert.Equal((KillCause.Crash, true), (loss.Cause, loss.Grounded));
            w.Step();
            Assert.Empty(w.Wrecks);
            Assert.Empty(w.Chutes);
        }
        else
        {
            Assert.Empty(losses);
            Assert.Equal(hp - d.Def.MaxHp / 2, d.HP);
        }
    }

    // ---- Determinism, hashing, content ----

    [Fact]
    public void JetsAtTheAirfieldAreDeterministicAndHashed()
    {
        static List<ulong> Run()
        {
            var w = Arena.NewSentinel(N, 5);
            Field(w, 30, 120, 1, [(0, 0), (0, 1)]);
            Spawn(w, "multirole_jet", 200, 60);
            Spawn(w, "fighter_jet", 205, 180);
            Spawn(w, "watcher_drone", 190, 100);
            var hashes = new List<ulong>();
            for (var t = 0; t < 4000; t++)
            {
                w.Step();
                if (t % 40 == 0)
                {
                    hashes.Add(w.Hash());
                }
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
        var w2 = Arena.NewSentinel(N);
        Field(w2, 30, 120);
        var d = Spawn(w2, "multirole_jet", 200, 60);
        w2.Step();
        var h = w2.Hash();
        d.AirOps!.Afterburner = true;
        Assert.NotEqual(h, w2.Hash());
        d.AirOps.Afterburner = false;
        Assert.Equal(h, w2.Hash());
    }

    [Theory]
    [InlineData("\"corridorNeed\": 600", "\"corridorNeed\": 450", "corridorNeed")]
    [InlineData("\"militaryAccel\": 12", "\"militaryAccel\": 30", "militaryAccel")]
    [InlineData("\"patternHeight\": 400", "\"patternHeight\": 100", "patternHeight")]
    [InlineData("\"pattern\": \"overhead\"", "\"pattern\": \"circuit\"", "pattern")]
    [InlineData("\"aerobrakeSpeed\": 60", "\"aerobrakeSpeed\": 90", "aerobrakeSpeed")]
    public void TheLoaderRefusesABrokenPattern(string from, string to, string what)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Name.EndsWith("archetypes/sentinel.json", StringComparison.Ordinal));
        Assert.Contains(from, sources[i].Json, StringComparison.Ordinal);
        sources[i] = new ContentSource(sources[i].Name, sources[i].Json.Replace(from, to, StringComparison.Ordinal));
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.Contains(what, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJetsCarryThePatternNumbers()
    {
        foreach (var id in new[] { "multirole_jet", "fighter_jet" })
        {
            var ac = TestContent.Mvp.Archetype(id)!.Aircraft!;
            Assert.Equal(ApproachPattern.Overhead, ac.Pattern);
            Assert.Equal(40, Cells(ac.InitialDistance.Raw));
            Assert.Equal(40 * 512, ac.PatternHeightRaw);
            Assert.Equal(12, Cells(ac.FixDistance.Raw));
            Assert.Equal(60, Cells(ac.CorridorNeed.Raw));
            Assert.True(ac.MilitaryAccel.Raw > 0 && ac.MilitaryAccel.Raw < ac.TakeoffAccel.Raw);
        }
        // The pattern radius is 5/4 of the tightest turn at the break and approach speeds (below corner
        // speed: the corner radius): 10 cells for the multirole, 11.25 for the fighter.
        Assert.InRange(TestContent.Mvp.Archetype("multirole_jet")!.Aircraft!.PatternRadius.Raw, 10 * Fixed.OneRaw - Fixed.OneRaw / 8, 10 * Fixed.OneRaw + Fixed.OneRaw / 8);
        Assert.InRange(TestContent.Mvp.Archetype("fighter_jet")!.Aircraft!.PatternRadius.Raw, 11 * Fixed.OneRaw + Fixed.OneRaw / 8, 11 * Fixed.OneRaw + Fixed.OneRaw * 3 / 8);
        Assert.Equal(ApproachPattern.StraightIn, TestContent.Mvp.Archetype("watcher_drone")!.Aircraft!.Pattern);
    }
}
