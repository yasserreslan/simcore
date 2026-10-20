using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// The fixed-wing kinematics (E4; the plan's air-combat model, section 2.4) on the test fixed wing
// (content/fixtures/fixed_wing_test.json, the plan's multirole numbers): the envelope (turn radius
// against speed, corner speed, the energy balance), the per-tick limits, the stall, fuel by throttle,
// edge avoidance at jet speeds, every mission, bingo through the airfield, determinism. Drones keep
// their law byte for byte (their own tests and the golden replay).
public class FixedWingTests
{
    private const int N = 200;
    private const string Jet = "test_fixed_wing";

    private static readonly Lazy<ContentSet> JetContent = new(() =>
    {
        var sources = TestContent.MvpSources();
        sources.Add(new ContentSource("fixtures/fixed_wing_test.json",
            File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "content", "fixtures", "fixed_wing_test.json"))));
        var i = sources.FindIndex(s => s.Name.EndsWith("factions/sentinel.json", StringComparison.Ordinal));
        sources[i] = new ContentSource(sources[i].Name, sources[i].Json.Replace("\"watcher_drone\",", "\"watcher_drone\", \"test_fixed_wing\",", StringComparison.Ordinal));
        return ContentLoader.Load(sources);
    });

    // JetWorld: the Sentinel lobby (player 1 Sentinel, 2 Resistance), no starting army, with the test fixed wing.
    internal static World JetWorld(int size = N, ulong seed = 1)
    {
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(JetContent.Value, seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(size, size), MatchCompiler.Compile(JetContent.Value, setup));
    }

    private static FixedWingSpec Spec => JetContent.Value.Archetypes.First(a => a.Id == Jet).Flight!.Wing!;

    private static ScoutFlight Flight => JetContent.Value.Archetypes.First(a => a.Id == Jet).Flight!;

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    private static long Gnd(World w, Vec p) => w.Ground?.HeightAt(p).Raw ?? 0;

    // Tenths of a cell a second, raw a second.
    private static long S(int tenths) => (long)tenths * Fixed.OneRaw / 10;

    private static Unit Overhead(World w, int x, int y, int facing = Facing.East, byte player = 1)
    {
        var d = w.Spawn(player, Jet, x, y, facing);
        w.Step();
        return d;
    }

    private static void Give(World w, Unit d, AirTaskKind task, Cell cell, Cell? cell2 = null, bool queue = false, bool loop = false) =>
        w.Issue(new Command
        {
            Tick = w.Tick, Player = d.Owner, Kind = CommandKind.AirTask, Units = [d.Id], Mission = task, Cell = cell, Cell2 = cell2,
            Queue = queue, On = loop,
        });

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

    // ---- The envelope ----

    [Fact]
    public void TheTurnRadiusGrowsWithSpeedSquaredAboveCornerAndTheTurnRatePeaksAtCorner()
    {
        var g = Spec;
        Assert.Equal(FlightKind.FixedWing, Flight.Kind);
        Assert.Equal(S(200), g.Cruise);
        Assert.InRange(Cells(g.Radius(g.Corner) * 10), 79, 80);  // 8 cells at corner speed
        Assert.InRange(Cells(g.Radius(S(100)) * 10), 79, 80);    // below corner the radius stays the corner radius
        Assert.InRange(Cells(g.Radius(g.Cruise) * 10), 141, 143); // 14.2 cells at cruise
        Assert.InRange(Cells(g.Radius(g.Dash) * 10), 277, 280);   // 27.9 at dash: (28 / 15)^2 x 8
        long Rate(long s) => g.LateralAccel(s) * 1_000_000 / s;   // the turn rate a / S
        Assert.True(Rate(g.Corner) > Rate(S(120)) && Rate(g.Corner) > Rate(S(180)), "the turn rate peaks at corner speed");
        Assert.True(Rate(S(100)) < Rate(S(120)) && Rate(S(280)) < Rate(S(200)));
        // The radius at cruise of the flown drone-law rate (airfield phases, capture) matches the envelope.
        Assert.InRange(ScoutFlight.TurnRadiusRaw(S(200) / 20, Flight.TurnPermille) * 100 / g.Radius(g.Cruise), 99, 102);
    }

    [Fact]
    public void ThrustDragBleedAndGravityBalanceAsTheContentSays()
    {
        var g = Spec;
        Assert.Equal(0, g.SpeedChange(g.Cruise, Throttle.Cruise, 0, 0));          // cruise holds level
        Assert.Equal(0, g.SpeedChange(g.Military, Throttle.Military, 0, 0));      // attack speed on military power
        Assert.Equal(0, g.SpeedChange(g.Dash, Throttle.Afterburner, 0, 0));       // dash on afterburner
        Assert.Equal(0, g.SpeedChange(g.Min, Throttle.Idle, 0, 0));               // idle settles at its minimum
        var sustained = g.Cruise * g.Cruise / S(180);
        Assert.InRange(g.SpeedChange(g.Cruise, Throttle.Military, sustained, 0), -1, 1); // military holds the 18-cell turn at cruise
        Assert.True(g.SpeedChange(g.Cruise, Throttle.Military, g.TurnAccel, 0) < 0, "a max-g turn bleeds speed");
        Assert.True(g.SpeedChange(g.Cruise, Throttle.Cruise, 0, -500) > 0, "a dive gains speed");
        Assert.True(g.SpeedChange(g.Cruise, Throttle.Cruise, 0, 500) < 0, "a climb costs speed");
        Assert.True(g.SpeedChange(g.Cruise, Throttle.Afterburner, 0, 0) > g.SpeedChange(g.Cruise, Throttle.Military, 0, 0));
        Assert.Equal((60, 100, 140, 300), (g.Burn(Throttle.Idle), g.Burn(Throttle.Cruise), g.Burn(Throttle.Military), g.Burn(Throttle.Afterburner)));
    }

    [Fact]
    public void AFlownMaxGTurnHasTheEnvelopesRadiusAtEachSpeed()
    {
        var g = Spec;
        foreach (var speed in new[] { S(120), g.Corner, g.Cruise, S(250) })
        {
            var w = JetWorld(400);
            var d = Overhead(w, 300, 200);
            var st = d.Wing!;
            st.Speed = speed;
            (st.VelX, st.VelY) = (speed, 0);
            w.Order(1, CommandKind.Move, [d], cell: new Cell(150, 200)); // far behind: a full turn
            w.Step();
            w.Step();
            // The lateral acceleration it pulled is its full a(S) at the speed it had: radius S^2 / a.
            Assert.True(Math.Abs(st.Lateral) >= g.LateralAccel(speed) * 98 / 100 && Math.Abs(st.Lateral) <= g.LateralAccel(speed),
                $"speed {speed} now {st.Speed} lateral {st.Lateral} of {g.LateralAccel(speed)} vz {d.VelZ.Raw} th {st.Throttle}");
            var r = st.Speed * st.Speed / Math.Abs(st.Lateral);
            Assert.InRange(r * 100 / g.Radius(st.Speed), 97, 103);
            Assert.True(st.LoadPermille > 1000);
        }
    }

    [Fact]
    public void PerTickLimitsAreNeverExceededInRandomFlight()
    {
        var g = Spec;
        var f = Flight;
        var w = JetWorld(N, 5);
        var rng = new SimCore.Rng.Rand(77);
        var jets = new List<Unit>();
        for (var i = 0; i < 4; i++)
        {
            var d = Overhead(w, 70 + 20 * i, 90 + 5 * i, rng.Intn(Facing.Count));
            d.Lifetime = 100000;
            jets.Add(d);
        }
        var prev = jets.Select(d => (d.Wing!.Speed, d.Wing.VelX, d.Wing.VelY)).ToArray();
        var maxDs = (g.ThrustAfterburner + g.Drag + g.Bleed + g.Gravity) / 20 + 1;
        for (var t = 0; t < 4000; t++)
        {
            if (t % 200 == 0)
            {
                foreach (var d in jets)
                {
                    var c = new Cell(rng.Intn(N), rng.Intn(N));
                    switch (rng.Intn(4))
                    {
                        case 0:
                            w.Order(1, CommandKind.Move, [d], cell: c);
                            break;
                        case 1:
                            Give(w, d, AirTaskKind.Search, c, new Cell(rng.Intn(N), rng.Intn(N)), loop: true);
                            break;
                        case 2:
                            Give(w, d, AirTaskKind.Roam, c, new Cell(rng.Intn(N), rng.Intn(N)));
                            break;
                        default:
                            d.Wing!.Boost = 100; // a dash
                            break;
                    }
                }
            }
            w.Step();
            for (var i = 0; i < jets.Count; i++)
            {
                var d = jets[i];
                var st = d.Wing!;
                var (s0, vx0, vy0) = prev[i];
                Assert.InRange(st.Speed, g.Min, g.Dash);
                Assert.True(Math.Abs(st.Speed - s0) <= maxDs, $"tick {t}: speed step {st.Speed - s0}");
                // The velocity's change: the turn (at most a(S) / 20) plus the speed change, plus rounding.
                var dv = World.Len3(st.VelX - vx0, st.VelY - vy0, 0);
                Assert.True(dv <= g.LateralAccel(s0) / 20 + Math.Abs(st.Speed - s0) + 3, $"tick {t}: velocity step {dv} > {g.LateralAccel(s0) / 20}");
                Assert.True(Math.Abs(st.Lateral) <= g.LateralAccel(s0));
                var ground = Gnd(w, d.Pos);
                Assert.True(d.Altitude.Raw >= ground + g.FloorRaw, $"tick {t}: under its floor");
                Assert.True(d.VelZ.Raw <= Math.Max(f.ClimbPerTick, 0) || d.Altitude.Raw == ground + g.FloorRaw, $"tick {t}: climb {d.VelZ.Raw}");
                Assert.True(d.VelZ.Raw >= -g.DivePerTick, $"tick {t}: dive {d.VelZ.Raw}");
                prev[i] = (st.Speed, st.VelX, st.VelY);
            }
        }
        Assert.Equal(0, w.AirEdgeClamps);
    }

    // ---- Energy ----

    [Fact]
    public void AHardTurnBleedsSpeedOnMilitaryPower()
    {
        var w = JetWorld(400);
        var d = Overhead(w, 300, 200);
        (d.Wing!.Speed, d.Wing.VelX, d.Wing.VelY) = (Spec.Cruise, Spec.Cruise, 0);
        w.Order(1, CommandKind.Move, [d], cell: new Cell(150, 200)); // behind it: a full max-g turn
        var least = long.MaxValue;
        var military = false;
        for (var t = 0; t < 40; t++)
        {
            w.Step();
            least = Math.Min(least, d.Wing!.Speed);
            military |= d.Wing.Throttle == Throttle.Military;
        }
        Assert.True(military, "it pushes the throttle up in the turn");
        // A max-g turn at cruise on military power bleeds about 0.4 cells/s^2 with these numbers (the
        // sustained 18-cell turn against the 14.2-cell max-g turn): 0.8 cells/s over the 2 s.
        Assert.True(least < Spec.Cruise - S(6), $"it bled speed: {least * 10 / Fixed.OneRaw} tenths of a cell/s");
        Assert.NotEqual(Throttle.Afterburner, d.Wing!.Throttle); // no afterburner to hold a cruise turn
    }

    [Fact]
    public void AClimbCostsSpeedAndADiveGivesSome()
    {
        var w = JetWorld();
        var level = Overhead(w, 100, 60);
        var climb = Overhead(w, 100, 100);
        var dive = Overhead(w, 100, 140);
        w.Run(10);
        foreach (var d in new[] { level, climb, dive })
        {
            (d.Wing!.Speed, d.Wing.VelX, d.Wing.VelY) = (Spec.Cruise, Spec.Cruise, 0);
            w.Order(1, CommandKind.Move, [d], cell: Cell.Of(d.Pos + new Vec(Fixed.FromInt(60), Fixed.Zero)));
        }
        climb.Altitude = Fixed.FromRaw(Gnd(w, climb.Pos) + Spec.FloorRaw);
        dive.Altitude = Fixed.FromRaw(Gnd(w, dive.Pos) + 400 * 512);
        w.Run(30);
        Assert.True(climb.VelZ.Raw > 0 && dive.VelZ.Raw < 0);
        Assert.True(climb.Wing!.Speed < level.Wing!.Speed - S(5), $"climb {climb.Wing.Speed} vs level {level.Wing.Speed}");
        Assert.True(dive.Wing!.Speed > level.Wing.Speed, $"dive {dive.Wing.Speed} vs level {level.Wing.Speed}");
    }

    [Fact]
    public void AtItsMinimumSpeedItCannotClimbAndTheAfterburnerRecoversIt()
    {
        var w = JetWorld();
        var d = Overhead(w, 100, 100);
        var g = Spec;
        (d.Wing!.Speed, d.Wing.VelX, d.Wing.VelY) = (g.Min, g.Min, 0);
        d.Altitude = Fixed.FromRaw(Gnd(w, d.Pos) + g.FloorRaw + 512);
        w.Step();
        Assert.True(d.VelZ.Raw <= 0, "stalled: no climb");
        Assert.Equal(Throttle.Afterburner, d.Wing.Throttle);
        var t = RunUntil(w, () => d.Wing.Speed >= g.Corner, 200);
        Assert.True(t > 0, "it recovers its energy");
        Assert.True(d.Wing.Speed >= g.Min);
        w.Run(100);
        Assert.True(d.Altitude.Raw > Gnd(w, d.Pos) + g.FloorRaw + 512, "then it climbs back");
    }

    // ---- Fuel ----

    [Fact]
    public void FuelBurnsByThrottle()
    {
        var w = JetWorld();
        var cruise = Overhead(w, 100, 80);
        var dash = Overhead(w, 100, 120);
        w.Run(40);
        var (c0, d0) = (cruise.Lifetime, dash.Lifetime);
        var expected = new long[2];
        var ab = false;
        for (var t = 0; t < 400; t++)
        {
            dash.Wing!.Boost = 10;
            w.Step();
            expected[0] += Spec.Burn(cruise.Wing!.Throttle);
            expected[1] += Spec.Burn(dash.Wing.Throttle);
            ab |= World.ThrottleOf(dash) == Throttle.Afterburner;
            Assert.Equal(dash.Wing.Throttle, World.ThrottleOf(dash));
        }
        Assert.True(ab);
        Assert.InRange(c0 - cruise.Lifetime, expected[0] / 100 - 1, expected[0] / 100 + 1);
        Assert.InRange(d0 - dash.Lifetime, expected[1] / 100 - 1, expected[1] / 100 + 1);
        Assert.True(c0 - cruise.Lifetime <= 400 * 14 / 10 + 1, $"cruise (orbiting: cruise or military) {c0 - cruise.Lifetime}");
        Assert.True(d0 - dash.Lifetime >= 400 * 22 / 10, $"dash (afterburner) {d0 - dash.Lifetime}");
    }

    [Fact]
    public void ItRunsDryAtTheEndOfItsTankWithNowhereToGo()
    {
        var w = JetWorld();
        var d = Overhead(w, 100, 100);
        d.Lifetime = 100;
        w.Run(120);
        Assert.False(d.Alive);
    }

    // ---- Edges ----

    [Theory]
    [InlineData(96)]
    [InlineData(160)]
    public void RandomOrdersNeverBringAJetToTheMapEdge(int size)
    {
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var w = JetWorld(size, seed);
            var rng = new SimCore.Rng.Rand(seed * 104729);
            var jets = new List<Unit>();
            var mg = (int)(Flight.EdgeMarginRaw / Fixed.OneRaw);
            var lo = Math.Min(mg, size / 2);
            for (var i = 0; i < 4; i++)
            {
                var d = w.Spawn(1, Jet, lo + rng.Intn(Math.Max(1, size - 2 * lo)), lo + rng.Intn(Math.Max(1, size - 2 * lo)), rng.Intn(Facing.Count));
                d.Lifetime = 100000;
                jets.Add(d);
            }
            for (var t = 0; t < 10000; t++)
            {
                if (t % 150 == 0)
                {
                    foreach (var d in jets)
                    {
                        var c = new Cell(rng.Intn(size), rng.Intn(size));
                        var c2 = new Cell(rng.Intn(size), rng.Intn(size));
                        switch (rng.Intn(7))
                        {
                            case 0:
                                w.Order(1, CommandKind.Move, [d], cell: c);
                                break;
                            case 1:
                                Give(w, d, AirTaskKind.Patrol, c, queue: rng.Intn(2) == 0);
                                break;
                            case 2:
                                Give(w, d, AirTaskKind.Guard, c, c2);
                                break;
                            case 3:
                                Give(w, d, AirTaskKind.Search, c, c2, loop: true);
                                break;
                            case 4:
                                Give(w, d, AirTaskKind.Roam, c, c2);
                                break;
                            case 5:
                                if (d.Wing is { } st)
                                {
                                    st.Boost = 200; // a dash
                                }
                                break;
                            default:
                                w.Order(1, CommandKind.Stop, [d]);
                                break;
                        }
                    }
                }
                w.Step();
                foreach (var d in jets)
                {
                    var step = Spec.Dash / 20;
                    long x = d.Pos.X.Raw, y = d.Pos.Y.Raw, edge = (long)size * Fixed.OneRaw;
                    Assert.True(x >= step && y >= step && x < edge - step && y < edge - step,
                        $"seed {seed} tick {t}: at ({x / Fixed.OneRaw}, {y / Fixed.OneRaw}) within a tick of the edge");
                }
            }
            Assert.Equal(0, w.AirEdgeClamps);
        }
    }

    // ---- Missions ----

    [Fact]
    public void AJetPatrolsSearchesAndRoams()
    {
        var w = JetWorld();
        var p = Overhead(w, 100, 100);
        Give(w, p, AirTaskKind.Patrol, new Cell(70, 80));
        Give(w, p, AirTaskKind.Patrol, new Cell(130, 120), queue: true);
        var s = Overhead(w, 100, 60);
        Give(w, s, AirTaskKind.Search, new Cell(60, 60), new Cell(140, 140));
        var r = Overhead(w, 100, 140);
        Give(w, r, AirTaskKind.Roam, new Cell(60, 60), new Cell(140, 140));
        long nearA = long.MaxValue, nearB = long.MaxValue;
        var legs = 0;
        var lastLeg = 0;
        for (var t = 0; t < 4000; t++)
        {
            w.Step();
            nearA = Math.Min(nearA, (p.Pos - new Cell(70, 80).Center()).Len().Raw);
            nearB = Math.Min(nearB, (p.Pos - new Cell(130, 120).Center()).Len().Raw);
            if (p.AirMission!.Leg != lastLeg)
            {
                (legs, lastLeg) = (legs + 1, p.AirMission.Leg);
            }
        }
        Assert.InRange(Cells(nearA), 0, 20);
        Assert.InRange(Cells(nearB), 0, 20);
        Assert.True(legs >= 4, $"it flies the racetrack round: {legs} legs");
        Assert.True(s.AirMission!.Done || s.AirMission.Lane >= 1, $"it sweeps lanes: lane {s.AirMission.Lane}");
        Assert.True(System.Numerics.BitOperations.PopCount(r.AirMission!.Visited) >= 10, "it roams over many sectors");
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(p).Activity);
        Assert.Equal(0, w.AirEdgeClamps);
        foreach (var j in new[] { p, s, r })
        {
            Assert.NotNull(j.Wing); // flown by its envelope throughout
        }
    }

    [Fact]
    public void AJetOnGuardEngagesAndAStrikeResumesItsTask()
    {
        var w = JetWorld();
        var d = Overhead(w, 60, 100);
        var (truck, _) = w.SpawnCrewed(2, "technical", 120, 100);
        w.Run(2);
        Give(w, d, AirTaskKind.Guard, new Cell(90, 80), new Cell(150, 120));
        var t = RunUntil(w, () => w.StrikeReports.Any(r => r.Shooter == d.Id), 2000);
        Assert.True(t > 0, "on Guard it engages the vehicle in its area");
        Assert.Equal(1, World.StrikesLeft(d));
        Assert.True(d.Wing!.Speed <= Spec.Military + 5);
        // A strike ordered on top of a patrol: it fires and the patrol resumes.
        Give(w, d, AirTaskKind.Patrol, new Cell(80, 80));
        Give(w, d, AirTaskKind.Patrol, new Cell(120, 120), queue: true);
        var other = w.SpawnCrewed(2, "technical", 110, 60).Vehicle;
        w.Run(30);
        w.Order(1, CommandKind.Attack, [d], target: other.Id);
        w.Step();
        Assert.Equal(AirActivity.Striking, w.AirStatusOf(d).Activity);
        Assert.True(RunUntil(w, () => World.StrikesLeft(d) == 0, 2000) > 0, "it strikes");
        w.Run(5);
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(d).Activity);
        Assert.Equal(AirTaskKind.Patrol, d.AirMission!.Kind);
        Assert.NotNull(truck);
    }

    // ---- Home at bingo through the airfield ----

    [Fact]
    public void AtBingoAJetLandsRefuelsTakesOffAndResumesItsPatrol()
    {
        var w = JetWorld();
        var af = AirfieldFlightTests.Field(w, [(0, 0)], at: new Cell(30, 120), east: 1); // 4 segments: 80 cells of runway
        var d = Overhead(w, 120, 60);
        Give(w, d, AirTaskKind.Patrol, new Cell(90, 70));
        Give(w, d, AirTaskKind.Patrol, new Cell(140, 70), queue: true);
        w.Run(100);
        d.Lifetime = 1500; // bingo soon (the flight to the hold, the approach, 30 s of holding, the reserve)
        Assert.True(RunUntil(w, () => d.AirOps is not null, 1500) > 0, "it turns for home at bingo");
        Assert.Null(d.Wing);
        Assert.Equal(af.Id, d.AirOps!.Airfield);
        Assert.Equal(HomeReason.Refuel, d.HomeReason);
        var touchdown = false;
        var parked = RunUntil(w, () =>
        {
            touchdown |= w.AirfieldEvents.Any(e => e.Kind == AirfieldEventKind.Touchdown && e.Aircraft == d.Id);
            return d.AirOps is { Phase: FlightPhase.Parked } || !d.Alive;
        }, 8000);
        Assert.True(parked > 0 && d.Alive, $"it lands and parks ({World.FlightPhaseOf(d)})");
        Assert.True(touchdown);
        Assert.True(d.Lifetime > 0);
        var wheelsUp = false;
        var free = RunUntil(w, () =>
        {
            wheelsUp |= w.AirfieldEvents.Any(e => e.Kind == AirfieldEventKind.WheelsUp && e.Aircraft == d.Id);
            return d.AirOps is null;
        }, 4000);
        Assert.True(free > 0 && wheelsUp, "refuelled, it takes off and flies free");
        Assert.InRange(d.Lifetime, d.Def.Lifetime - 400, d.Def.Lifetime); // a full tank (less the taxi out and the climb)
        w.Step();
        Assert.NotNull(d.Wing); // its envelope again
        // E9: the climb-out hands it to free flight once the runway is free and it has its corner speed
        // (no longer at cruise, about 80 cells out); it speeds up to cruise on its envelope from there.
        Assert.InRange(d.Wing!.Speed, Spec.Corner - S(5), Spec.Cruise + S(10));
        w.Run(20);
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(d).Activity);
        Assert.Equal(0, w.AirEdgeClamps);
    }

    // ---- Determinism, hashing, content ----

    [Fact]
    public void FixedWingFlightIsDeterministicAndHashed()
    {
        static List<ulong> Run()
        {
            var w = JetWorld(N, 9);
            var a = Overhead(w, 100, 100);
            var b = Overhead(w, 90, 110);
            w.SpawnCrewed(2, "technical", 110, 110);
            Give(w, a, AirTaskKind.Guard, new Cell(80, 80), new Cell(120, 120));
            Give(w, b, AirTaskKind.Roam, new Cell(60, 60), new Cell(140, 140));
            var hashes = new List<ulong>();
            for (var t = 0; t < 2000; t++)
            {
                if (t == 500)
                {
                    b.Wing!.Boost = 60;
                }
                w.Step();
                if (t % 50 == 0)
                {
                    hashes.Add(w.Hash());
                }
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
        var w = JetWorld();
        var d = Overhead(w, 100, 100);
        var h = w.Hash();
        d.Wing!.Boost = 3;
        Assert.NotEqual(h, w.Hash());
        d.Wing.Boost = 0;
        Assert.Equal(h, w.Hash());
        var drone = w.Spawn(1, "watcher_drone", 60, 60);
        w.Step();
        Assert.Null(drone.Wing); // drones keep their law and their hash
    }

    [Theory]
    [InlineData("\"sustainedRadius\": 180", "\"sustainedRadius\": 120", "sustainedRadius")]
    [InlineData("\"edgeMargin\": 500", "\"edgeMargin\": 400", "edgeMargin")]
    [InlineData("\"orbitRadius\": 200", "\"orbitRadius\": 100", "orbitRadius")]
    [InlineData("\"kind\": \"fixedWing\",", "\"kind\": \"fixedWing\", \"turnRate\": 30,", "turnRate")]
    [InlineData("\"kind\": \"fixedWing\",", "", "fixedWing")]
    [InlineData("\"attack\": 230", "\"attack\": 190", "speeds")]
    public void TheLoaderRefusesABrokenEnvelope(string from, string to, string what)
    {
        var sources = TestContent.MvpSources();
        var json = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "content", "fixtures", "fixed_wing_test.json"));
        Assert.Contains(from, json, StringComparison.Ordinal);
        sources.Add(new ContentSource("fixtures/fixed_wing_test.json", json.Replace(from, to, StringComparison.Ordinal)));
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.Contains(what, e.Message, StringComparison.Ordinal);
    }
}
