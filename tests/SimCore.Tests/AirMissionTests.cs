using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The mission layer (E3; the plan's air-combat model, sections 2.5 and 2.6): Orbit (Move), Patrol,
// Guard (the hunt, the only mode that picks targets), Search, Roam and spot, Strike on top of a
// task, the trips home, edge avoidance (never bump into the map's edge), AirTask (30) and the
// status query AirStatusOf, on the Watcher, the Overseer and the scout drone.
public class AirMissionTests
{
    private const int N = 200;

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    // Overhead: an aircraft put down in flight over (x, y) (the sandbox's way), settled into its orbit.
    private static Unit Overhead(World w, string kind, int x, int y, byte player = 1)
    {
        var d = w.Spawn(player, kind, x, y);
        w.Run(5);
        return d;
    }

    private static void Give(World w, Unit d, AirTaskKind task, Cell cell, Cell? cell2 = null, bool queue = false, AirTargetKinds filter = 0, bool loop = false) =>
        w.Issue(new Command
        {
            Tick = w.Tick, Player = d.Owner, Kind = CommandKind.AirTask, Units = [d.Id], Mission = task, Cell = cell, Cell2 = cell2,
            Queue = queue, Filter = (byte)filter, On = loop,
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

    // ---- The command ----

    [Fact]
    public void AirTaskSetsAMissionShiftAppendsWaypointsAndMoveOrStopEndsIt()
    {
        var w = Arena.NewSentinel(N);
        var d = Overhead(w, "watcher_drone", 100, 100);
        var rifle = w.Spawn(1, "Rifleman", 50, 50);
        Give(w, d, AirTaskKind.Patrol, new Cell(80, 80));
        w.Order(1, CommandKind.Move, [rifle], cell: new Cell(60, 60)); // a ground unit takes no air task
        w.Step();
        Assert.Equal(AirTaskKind.Patrol, d.AirMission!.Kind);
        Assert.Single(d.AirMission.Route);
        for (var i = 0; i < 9; i++)
        {
            Give(w, d, AirTaskKind.Patrol, new Cell(80 + i, 120), queue: true);
        }
        w.Step();
        Assert.Equal(AirMission.MaxRoute, d.AirMission.Route.Count); // at most 8 waypoints
        Assert.Equal(new Cell(80, 80), d.AirMission.Route[0]);
        Assert.Null(rifle.AirMission);
        Give(w, d, AirTaskKind.Guard, new Cell(90, 90), new Cell(110, 105));
        w.Step();
        Assert.Equal((AirTaskKind.Guard, true, new Cell(90, 90), new Cell(110, 105)), (d.AirMission.Kind, d.AirMission.HasArea, d.AirMission.AreaMin, d.AirMission.AreaMax));
        Assert.Equal(AirTargetKinds.Vehicles | AirTargetKinds.Structures, d.AirMission.Filter); // the Watcher's default
        w.Order(1, CommandKind.Stop, [d]);
        w.Step();
        Assert.Null(d.AirMission); // Stop: it orbits where it is
        Give(w, d, AirTaskKind.Roam, new Cell(100, 100));
        w.Step();
        Assert.Equal(AirTaskKind.Roam, d.AirMission!.Kind);
        Assert.True(d.AirMission.HasArea, "Roam without a second corner works over a square round the point");
        w.Order(1, CommandKind.Move, [d], cell: new Cell(110, 110));
        w.Step();
        Assert.Null(d.AirMission); // Move: an orbit there
        Assert.Equal(new Cell(110, 110), Cell.Of(d.OrbitAt));
    }

    // ---- The modes ----

    [Fact]
    public void PatrolFliesARacetrackBetweenTwoPointsAndALoopThroughThree()
    {
        var w = Arena.NewSentinel(N);
        var d = Overhead(w, "watcher_drone", 100, 100);
        Give(w, d, AirTaskKind.Patrol, new Cell(75, 100));
        Give(w, d, AirTaskKind.Patrol, new Cell(125, 100), queue: true);
        var legs = new List<int>();
        long nearA = long.MaxValue, nearB = long.MaxValue;
        for (var t = 0; t < 2400; t++)
        {
            w.Step();
            if (legs.Count == 0 || legs[^1] != d.AirMission!.Leg)
            {
                legs.Add(d.AirMission!.Leg);
            }
            nearA = Math.Min(nearA, (d.Pos - new Cell(75, 100).Center()).Len().Raw);
            nearB = Math.Min(nearB, (d.Pos - new Cell(125, 100).Center()).Len().Raw);
            Assert.InRange(Cells(d.Pos.Y.Raw), 60, 140); // a racetrack, not a wide circle
        }
        Assert.True(legs.Count >= 6, $"back and forth: {string.Join(",", legs)}");
        Assert.All(legs.Zip(legs.Skip(1)), p => Assert.NotEqual(p.First, p.Second));
        Assert.InRange(Cells(nearA), 0, 18); // it reaches each end (within its turn radius and 2 cells)
        Assert.InRange(Cells(nearB), 0, 18);

        Give(w, d, AirTaskKind.Patrol, new Cell(130, 100), queue: true); // a third point: a closed loop
        var seen = new HashSet<int>();
        var order = new List<int>();
        for (var t = 0; t < 4000; t++)
        {
            w.Step();
            if (order.Count == 0 || order[^1] != d.AirMission!.Leg)
            {
                order.Add(d.AirMission!.Leg);
            }
            seen.Add(d.AirMission.Leg);
        }
        Assert.Equal([0, 1, 2], seen.Order());
        for (var i = 1; i < order.Count; i++)
        {
            Assert.Equal((order[i - 1] + 1) % 3, order[i]); // in order round the loop
        }
    }

    [Fact]
    public void PatrolSearchAndRoamOnlySpotGuardEngages()
    {
        foreach (var task in new[] { AirTaskKind.Patrol, AirTaskKind.Search, AirTaskKind.Roam, AirTaskKind.Guard })
        {
            var w = Arena.NewSentinel(N);
            var d = Overhead(w, "watcher_drone", 100, 100);
            var tech = w.SpawnCrewed(2, "technical", 104, 100).Vehicle;
            if (task == AirTaskKind.Patrol)
            {
                Give(w, d, task, new Cell(90, 100));
                Give(w, d, task, new Cell(110, 100), queue: true);
            }
            else
            {
                Give(w, d, task, new Cell(85, 85), new Cell(115, 115));
            }
            var strikes = 0;
            for (var t = 0; t < 1200; t++)
            {
                w.Step();
                strikes += w.StrikeReports.Count(r => r.Outcome == StrikeOutcome.Launched);
            }
            if (task == AirTaskKind.Guard)
            {
                Assert.True(strikes >= 1, "Guard picks the technical in its area and strikes it");
                Assert.False(tech.Alive);
                Assert.Equal(AirTaskKind.Guard, d.AirMission!.Kind); // and guards on
                Assert.Equal(AirActivity.Guarding, w.AirStatusOf(d).Activity);
            }
            else
            {
                Assert.Equal(0, strikes); // the others never fire on their own
                Assert.True(tech.Alive, $"{task}: never fires");
                Assert.True(World.IsVisibleTo(tech, 1), $"{task}: it still spots");
            }
        }
    }

    [Fact]
    public void GuardEngagesOnlyTheFilterSKindsInsideTheAreaPlusTheLeash()
    {
        var w = Arena.NewSentinel(N);
        var d = Overhead(w, "watcher_drone", 100, 100);
        var soldier = w.Spawn(2, "Rifleman", 102, 104);
        var near = w.SpawnCrewed(2, "technical", 140, 100).Vehicle; // 20 cells past the area: inside the 30-cell leash
        var far = w.SpawnCrewed(2, "technical", 100, 160).Vehicle; // 45 cells past it: outside
        Give(w, d, AirTaskKind.Guard, new Cell(90, 90), new Cell(119, 119));
        var targets = new List<uint>();
        long farthest = 0;
        for (var t = 0; t < 4000; t++)
        {
            w.Step();
            targets.AddRange(w.StrikeReports.Where(r => r.Outcome == StrikeOutcome.Launched).Select(r => r.Target));
            if (d.Order.Kind == OrderKind.Attack)
            {
                Assert.Equal(d.AirMission!.Hunt, d.Order.Target);
                Assert.Equal(AirActivity.Engaging, w.AirStatusOf(d).Activity);
            }
            farthest = Math.Max(farthest, d.Pos.X.Raw);
        }
        Assert.Equal([near.Id], targets); // the soldier is not in its default filter; the far truck is past the leash
        Assert.True(soldier.Alive);
        Assert.True(far.Alive);
        Assert.Equal(1, World.StrikesLeft(d));

        // Filter infantry: now the soldier is fair game.
        Give(w, d, AirTaskKind.Guard, new Cell(90, 90), new Cell(119, 119), filter: AirTargetKinds.Infantry);
        Assert.True(RunUntil(w, () => !soldier.Alive, 2000) > 0, "the infantry filter engages the soldier");
        Assert.True(far.Alive);
        Assert.Equal(0, World.StrikesLeft(d));
        // Out of ammunition it guards on (never home for it) and its status says so.
        var late = w.SpawnCrewed(2, "technical", 104, 100).Vehicle;
        Give(w, d, AirTaskKind.Guard, new Cell(90, 90), new Cell(119, 119));
        for (var t = 0; t < 1200; t++)
        {
            w.Step();
            Assert.False(d.Homing);
            Assert.Equal(OrderKind.Idle, d.Order.Kind);
        }
        Assert.True(late.Alive);
        var st = w.AirStatusOf(d);
        Assert.Equal((AirActivity.Guarding, true, 0), (st.Activity, st.NoAmmo, st.Ammo));
    }

    [Fact]
    public void GuardOverARouteEngagesNearTheRouteOnly()
    {
        var w = Arena.NewSentinel(N);
        var d = Overhead(w, "watcher_drone", 100, 100);
        var onRoute = w.SpawnCrewed(2, "technical", 100, 115).Vehicle; // 15 cells off the leg
        var offRoute = w.SpawnCrewed(2, "technical", 100, 140).Vehicle; // 40 cells off: past the 30-cell leash
        Give(w, d, AirTaskKind.Guard, new Cell(75, 100));
        Give(w, d, AirTaskKind.Guard, new Cell(125, 100), queue: true);
        var targets = new List<uint>();
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            targets.AddRange(w.StrikeReports.Where(r => r.Outcome == StrikeOutcome.Launched).Select(r => r.Target));
        }
        Assert.Equal([onRoute.Id], targets);
        Assert.True(offRoute.Alive);
    }

    [Fact]
    public void SearchSweepsTheWholeAreaThenOrbitsItsCentre()
    {
        var w = Arena.NewSentinel(N);
        var d = Overhead(w, "overseer_drone", 100, 100);
        Cell a = new(62, 70), b = new(137, 129);
        Give(w, d, AirTaskKind.Search, a, b);
        w.Step();
        var seen = new bool[b.X - a.X + 1, b.Y - a.Y + 1];
        var vision = d.Def.Vision.Raw;
        var last = -1;
        var t = 0;
        for (; t < 20000 && !d.AirMission!.Done; t++)
        {
            w.Step();
            var st = w.AirStatusOf(d);
            Assert.Equal(AirActivity.Searching, st.Activity);
            Assert.True(st.Percent >= last, "progress only grows");
            last = st.Percent;
            if (t % 5 != 0)
            {
                continue;
            }
            for (var x = a.X; x <= b.X; x++)
            {
                for (var y = a.Y; y <= b.Y; y++)
                {
                    if (!seen[x - a.X, y - a.Y] && (new Cell(x, y).Center() - d.Pos).LenSq() <= vision * vision)
                    {
                        seen[x - a.X, y - a.Y] = true;
                    }
                }
            }
        }
        Assert.True(d.AirMission!.Done, "the sweep completes");
        var missed = 0;
        foreach (var s in seen)
        {
            missed += s ? 0 : 1;
        }
        Assert.Equal(0, missed); // 100% of the area's cells came within its sight
        Assert.Equal(100, w.AirStatusOf(d).Percent);
        w.Run(1500);
        var centre = new Vec(Fixed.FromInt((a.X + b.X + 1) / 2), Fixed.FromInt((a.Y + b.Y + 1) / 2));
        Assert.InRange(Cells((d.Pos - centre).Len().Raw), 20, 36); // then it orbits the centre
    }

    [Fact]
    public void RoamVisitsEverySectorAndNeverRepeatsTheSamePath()
    {
        var w = Arena.NewSentinel(N);
        var d = Overhead(w, "scout_drone", 100, 100, 2); // the Resistance's scout drone
        d.Lifetime = 100000;
        Cell a = new(50, 50), b = new(149, 149);
        Give(w, d, AirTaskKind.Roam, a, b);
        var visited = new HashSet<int>();
        var aims = new HashSet<Vec>();
        var t = RunUntil(w, () =>
        {
            aims.Add(d.AirMission!.RoamAim);
            var sx = (int)Math.Clamp((d.Pos.X.Raw / Fixed.OneRaw - 50) * 8 / 100, 0, 7);
            var sy = (int)Math.Clamp((d.Pos.Y.Raw / Fixed.OneRaw - 50) * 8 / 100, 0, 7);
            visited.Add(sy * 8 + sx);
            return visited.Count == 64;
        }, 10 * 60 * World.TicksPerSecond);
        Assert.True(t > 0, $"every sector within 10 minutes ({visited.Count} of 64)");
        Assert.True(aims.Count >= 20, "seeded waypoints, not a fixed circuit");
        Assert.Equal(AirActivity.Roaming, w.AirStatusOf(d).Activity);
    }

    [Fact]
    public void AStrikeRunsOnTopOfTheTaskWhichResumesAfter()
    {
        var w = Arena.NewSentinel(N);
        var d = Overhead(w, "watcher_drone", 100, 100);
        Give(w, d, AirTaskKind.Patrol, new Cell(80, 100));
        Give(w, d, AirTaskKind.Patrol, new Cell(120, 100), queue: true);
        w.Run(200);
        var tech = w.SpawnCrewed(2, "technical", 100, 135).Vehicle; // off the route: it flies over to it
        w.Order(1, CommandKind.Attack, [d], target: tech.Id);
        w.Step();
        Assert.Equal(AirActivity.Striking, w.AirStatusOf(d).Activity);
        Assert.True(RunUntil(w, () => !tech.Alive, 1500) > 0);
        Assert.Equal(AirTaskKind.Patrol, d.AirMission!.Kind); // the task is kept
        w.Run(800);
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(d).Activity);
        Assert.InRange(Cells(d.Pos.Y.Raw), 70, 125); // back on its racetrack

        // A plain orbit: after a strike out of reach it goes back to its orbit (E3: it no longer moves the orbit there).
        var w2 = Arena.NewSentinel(N);
        var d2 = Overhead(w2, "watcher_drone", 100, 100);
        var centre = w2.OrbitCentreOf(d2);
        var t2 = w2.SpawnCrewed(2, "technical", 100, 135).Vehicle;
        w2.Order(1, CommandKind.Attack, [d2], target: t2.Id);
        Assert.True(RunUntil(w2, () => !t2.Alive, 1500) > 0);
        w2.Run(1200);
        Assert.Equal(centre, w2.OrbitCentreOf(d2));
        Assert.InRange(Cells((d2.Pos - centre).Len().Raw), 15, 30);
    }

    [Fact]
    public void AtBingoItRefuelsAndTheRelaunchResumesTheTaskAtTheNearestLeg()
    {
        var w = Arena.NewSentinel(N);
        var station = w.Spawn(1, "watcher_station", 60, 100);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(120, 100), ability: "watcher_launch");
        w.Run(2);
        var d = Assert.Single(w.All("watcher_drone"));
        w.Run(600);
        Give(w, d, AirTaskKind.Patrol, new Cell(110, 80));
        Give(w, d, AirTaskKind.Patrol, new Cell(140, 80), queue: true);
        Give(w, d, AirTaskKind.Patrol, new Cell(140, 120), queue: true);
        w.Run(600);
        d.Lifetime = 1200; // bingo soon
        Assert.True(RunUntil(w, () => d.Homing, 1200) > 0);
        Assert.Equal(AirActivity.Returning, w.AirStatusOf(d).Activity);
        Assert.True(RunUntil(w, () => d.Recovered || !d.Alive, 3000) > 0);
        Assert.True(d.Recovered, "back at its station, not lost");
        var docked = w.AirStatusOf(station);
        Assert.Equal((AirActivity.Docked, AirTaskKind.Patrol), (docked.Activity, docked.Mission));
        w.Run(station.Cooldown + 3);
        var again = w.All("watcher_drone").Single(u => u.Source == station.Id);
        Assert.Equal(AirTaskKind.Patrol, again.AirMission!.Kind);
        Assert.Equal([new Cell(110, 80), new Cell(140, 80), new Cell(140, 120)], again.AirMission.Route);
        w.Run(30);
        Assert.Equal(0, again.AirMission.Leg); // the nearest waypoint to its station
        Assert.False(again.AirMission.Away);
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(again).Activity);
    }

    [Fact]
    public void AtAnAirfieldTheTaskIsKeptAndResumedAfterTheRefuelStop()
    {
        var w = Arena.NewSentinel(N);
        var af = AirfieldFlightTests.Field(w, [(0, 0)]);
        var d = Overhead(w, "watcher_drone", 110, 140);
        Give(w, d, AirTaskKind.Search, new Cell(80, 120), new Cell(140, 150));
        w.Run(100);
        d.Lifetime = 2400;
        var statuses = new List<AirActivity>();
        var t = RunUntil(w, () =>
        {
            var a = w.AirStatusOf(d).Activity;
            if (statuses.Count == 0 || statuses[^1] != a)
            {
                statuses.Add(a);
            }
            return statuses.Contains(AirActivity.Departing) && d.AirOps is null;
        }, 14000);
        Assert.True(t > 0, $"back in the air: {string.Join(", ", statuses)}");
        foreach (var a in new[] { AirActivity.Holding, AirActivity.Landing, AirActivity.Taxiing, AirActivity.Refuelling, AirActivity.Departing })
        {
            Assert.Contains(a, statuses);
        }
        Assert.Equal(AirTaskKind.Search, d.AirMission!.Kind);
        w.Run(20);
        Assert.Equal(AirActivity.Searching, w.AirStatusOf(d).Activity);
        Assert.NotNull(af);
    }

    [Fact]
    public void TheStatusFollowsARecallAndARearmThroughTheAirfield()
    {
        var w = Arena.NewSentinel(N);
        AirfieldFlightTests.Field(w, [(0, 0)]);
        var d = Overhead(w, "watcher_drone", 110, 140);
        var st = w.AirStatusOf(d);
        Assert.Equal((AirActivity.Orbiting, AirTaskKind.Orbit, FlightPhase.Airborne, 2, false, 0u), (st.Activity, st.Mission, st.Phase, st.Ammo, st.NoAmmo, st.Target));
        Assert.InRange(st.FuelPercent, 99, 100);
        Assert.Equal(AirActivity.None, w.AirStatusOf(w.Spawn(1, "Rifleman", 10, 10)).Activity);

        // Recalled: it comes home, refuels only and is held.
        w.Order(1, CommandKind.ReturnToBase, [d]);
        var seen = new List<AirActivity>();
        void Track()
        {
            var a = w.AirStatusOf(d).Activity;
            if (seen.Count == 0 || seen[^1] != a)
            {
                seen.Add(a);
            }
        }
        Assert.True(RunUntil(w, () =>
        {
            Track();
            return seen[^1] == AirActivity.Held;
        }, 8000) > 0, string.Join(", ", seen));
        Assert.Contains(AirActivity.Holding, seen);
        Assert.Contains(AirActivity.Landing, seen);
        Assert.Contains(AirActivity.Taxiing, seen);
        Assert.Equal(HomeReason.Recall, w.AirStatusOf(d).Reason);

        // Rearm where it is parked: it rearms and repairs, then it is ready and leaves.
        d.MutableCharges[0] = 1; // a missile (15 s at its stand) and 180 HP (18 s): rearming, then repairing
        d.HP = 20;
        w.Order(1, CommandKind.Rearm, [d]);
        seen.Clear();
        Assert.True(RunUntil(w, () =>
        {
            Track();
            return seen[^1] == AirActivity.Departing;
        }, 4000) > 0, string.Join(", ", seen));
        Assert.Contains(AirActivity.Rearming, seen);
        Assert.Contains(AirActivity.Repairing, seen);

        // Rearm from the air: on its way it says so.
        Assert.True(RunUntil(w, () => d.AirOps is null, 2000) > 0);
        d.MutableCharges[0] = 1;
        w.Order(1, CommandKind.Rearm, [d]);
        w.Step();
        var now = w.AirStatusOf(d).Activity;
        Assert.True(now is AirActivity.RearmEnRoute or AirActivity.Holding or AirActivity.Landing, $"on its way: {now}"); // (just off the field: cleared at once)
        Assert.Equal(HomeReason.Rearm, w.AirStatusOf(d).Reason);
    }

    // ---- Edges: never bump ----

    [Theory]
    [InlineData(96)]
    [InlineData(160)]
    public void RandomOrdersNeverBringAnAircraftToTheMapEdge(int size)
    {
        string[] kinds = ["watcher_drone", "overseer_drone", "scout_drone"];
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var w = Arena.NewSentinel(size, seed);
            var rng = new SimCore.Rng.Rand(seed * 7919);
            var drones = new List<Unit>();
            foreach (var k in kinds)
            {
                for (var i = 0; i < 2; i++)
                {
                    var mg = (int)(w.Content.Archetypes.First(a => a.Id == k).Flight!.EdgeMarginRaw / Fixed.OneRaw);
                    var lo = Math.Min(mg, size / 2);
                    var x = lo + rng.Intn(Math.Max(1, size - 2 * lo));
                    var y = lo + rng.Intn(Math.Max(1, size - 2 * lo));
                    var d = w.Spawn(k == "scout_drone" ? (byte)2 : (byte)1, k, x, y, rng.Intn(Facing.Count)); // the scout is the Resistance's
                    d.Lifetime = 100000;
                    drones.Add(d);
                }
            }
            for (var t = 0; t < 10000; t++)
            {
                if (t % 150 == 0)
                {
                    foreach (var d in drones)
                    {
                        var c = new Cell(rng.Intn(size), rng.Intn(size));
                        var c2 = new Cell(rng.Intn(size), rng.Intn(size));
                        switch (rng.Intn(6))
                        {
                            case 0:
                                w.Order(d.Owner, CommandKind.Move, [d], cell: c);
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
                            default:
                                w.Order(d.Owner, CommandKind.Stop, [d]);
                                break;
                        }
                    }
                }
                w.Step();
                foreach (var d in drones)
                {
                    var step = d.Def.Speed.Raw;
                    long x = d.Pos.X.Raw, y = d.Pos.Y.Raw, edge = (long)size * Fixed.OneRaw;
                    Assert.True(x >= step && y >= step && x < edge - step && y < edge - step,
                        $"seed {seed} tick {t}: {d.Def.Archetype.Id} at ({x / Fixed.OneRaw}, {y / Fixed.OneRaw}) within a tick of the edge");
                }
            }
            Assert.Equal(0, w.AirEdgeClamps);
        }
    }

    [Fact]
    public void ADroneAtTheEdgeHeadingOffTheMapTurnsBackInsteadOfSticking()
    {
        // The night's bug: put down at the edge facing off the map, the clamped step zeroed its
        // velocity and its facing pointed off the map again every tick: stuck. Now it keeps its
        // exact velocity and edge avoidance turns it back.
        var w = Arena.NewSentinel(N);
        var d = w.Spawn(1, "watcher_drone", 0, 100, Facing.West);
        var last = d.Pos;
        var still = 0;
        for (var t = 0; t < 400; t++)
        {
            w.Step();
            still = d.Pos == last ? still + 1 : 0;
            Assert.True(still < 2, $"tick {t}: stuck at {d.Pos}");
            last = d.Pos;
        }
        Assert.True(Cells(d.Pos.X.Raw) > 20, $"back over the map: {Cells(d.Pos.X.Raw)}");
        Assert.NotEqual(default, d.Vel);
    }

    [Fact]
    public void AtBingoFlyingStraightAwayFromANearStationItTurnsHomeAtOnce()
    {
        // The E2 note: the drone law barely turns with home nearly dead astern, so a drone at bingo
        // flying straight away from a station 40 cells off flew on and ran dry. The controller turns
        // it at its full rate.
        var w = Arena.NewSentinel(N);
        var station = w.Spawn(1, "watcher_station", 60, 100);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(120, 100), ability: "watcher_launch");
        w.Run(2);
        var d = Assert.Single(w.All("watcher_drone"));
        w.Run(800);
        // Heading straight east, away from the station, 40 cells off, at bingo.
        d.Pos = new Vec(Fixed.FromInt(100), station.Pos.Y);
        d.Vel = new Vec(d.Def.Speed, Fixed.Zero);
        d.Lifetime = 690; // bingo (the 19 s descent and the 15 s reserve) in a second
        long farthest = 0;
        var t = RunUntil(w, () =>
        {
            farthest = Math.Max(farthest, (d.Pos - station.Pos).Len().Raw);
            return d.Recovered || !d.Alive;
        }, 3000);
        Assert.True(t > 0 && d.Recovered, $"home, not dry (farthest {Cells(farthest)} cells)");
        Assert.InRange(Cells(farthest), 40, 80); // it turned at once: about a turn radius further out
    }

    [Fact]
    public void HoldingAtAFieldBesideTheEdgeStaysOnTheMap()
    {
        // The runway's centre line 13 cells from the north edge: the 25-cell holding orbit reaches
        // 12 cells past it (the corridors only cover the runway strip). Four Watchers for two
        // stations, coming home a little apart: they queue and hold. Edge avoidance keeps them on the map; the
        // final's join takes the side that fits (M2); they all land.
        var w = Arena.NewSentinel(N);
        var af = AirfieldFlightTests.Field(w, [(0, 0)], new Cell(30, 32));
        var ds = new List<Unit>();
        for (var i = 0; i < 4; i++)
        {
            var d = w.Spawn(1, "watcher_drone", 110 + 5 * i, 40 + 5 * i);
            ds.Add(d);
        }
        w.Run(5);
        for (var i = 0; i < 4; i++)
        {
            ds[i].Lifetime = 2400 + 700 * i;
        }
        var held = 0;
        var nearest = long.MaxValue;
        var landed = new HashSet<uint>();
        var t = RunUntil(w, () =>
        {
            held = Math.Max(held, ds.Count(d => d.AirOps is { Phase: FlightPhase.Holding }));
            foreach (var d in ds)
            {
                if (d.AirOps is { Phase: FlightPhase.Holding })
                {
                    nearest = Math.Min(nearest, d.Pos.Y.Raw);
                }
            }
            foreach (var d in ds)
            {
                if (d.AirOps is { Phase: FlightPhase.Rollout } ops && ops.Airfield == af.Id)
                {
                    landed.Add(d.Id);
                }
            }
            return landed.Count == 4;
        }, 14000);
        Assert.True(t > 0, $"they all land ({landed.Count}: {string.Join(", ", ds.Select(World.FlightPhaseOf))})");
        Assert.True(held >= 2, $"some held at once: {held}");
        Assert.InRange(Cells(nearest), 1, 25); // holding, it came close to the edge (the hold point is 13 cells off it)
        Assert.Equal(0, w.AirEdgeClamps);
    }

    // ---- Determinism ----

    [Fact]
    public void MissionsAreDeterministic()
    {
        static List<ulong> Run()
        {
            var w = Arena.NewSentinel(N, 3);
            var a = Overhead(w, "watcher_drone", 100, 100);
            var b = Overhead(w, "overseer_drone", 90, 110);
            var c = Overhead(w, "scout_drone", 110, 90, 2);
            w.SpawnCrewed(2, "technical", 110, 110);
            Give(w, a, AirTaskKind.Guard, new Cell(80, 80), new Cell(120, 120));
            Give(w, b, AirTaskKind.Search, new Cell(70, 70), new Cell(130, 130), loop: true);
            Give(w, c, AirTaskKind.Roam, new Cell(60, 60), new Cell(140, 140));
            var hashes = new List<ulong>();
            for (var t = 0; t < 3000; t++)
            {
                w.Step();
                if (t % 100 == 0)
                {
                    hashes.Add(w.Hash());
                }
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }

    // ---- Content ----

    [Fact]
    public void TheLoaderRefusesAnEdgeMarginTooSmallForTheTurn()
    {
        var sources = TestContent.MvpSources()
            .Select(s => s.Name == "archetypes/sentinel.json" ? s with { Json = s.Json.Replace("\"edgeMargin\": 400", "\"edgeMargin\": 300") } : s)
            .ToList();
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.Contains("edgeMargin", e.Message, StringComparison.Ordinal);
        Assert.Contains("watcher_drone", e.Message, StringComparison.Ordinal);
        var w = TestContent.Mvp.Archetypes.First(a => a.Id == "watcher_drone").Flight!;
        Assert.Equal(40 * Fixed.OneRaw, w.EdgeMarginRaw);
    }
}
