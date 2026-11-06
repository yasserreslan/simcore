using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Overseer Track (CommandKind.Track, Sim/Systems/TrackSystem.cs; docs/design.md, "Overseer Track"; the owner's request of
// 2026-11-03): the Overseer locks onto an enemy soldier or vehicle its side spots; its orbit follows it; locked and within its
// 50-cell sight the target stays spotted for its side whatever hides it from the air (canopy, a cold engine, standing still); the
// lock breaks out of sight, inside a building, tunnel or vehicle, and a dead target ends it; lost, it searches round the last known
// position 1.5x, 1.75x and 2x its orbit radius for 60 s and re-locks the same unit when it spots it again, then holds a normal orbit
// there. Player 1 is the Sentinel (the Overseer), player 2 the Resistance (the targets). Fog of war on a flat 200-cell terrain map
// with a dense wood over cells [100, 140) both ways unless a test says otherwise.
public class TrackTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int N = 200;
    private const int Sight = 50; // the Overseer's sight, cells
    private const int Orbit = 30; // its orbit radius, cells
    private const int Look = (int)World.VisionTicks; // a viewer looks round every 4 ticks (staggered by ID)

    // Wooded: a Sentinel-lobby world on flat terrain with dense forest over [100, 140) both ways (density 3), fog on unless off.
    private static World Wooded(bool fog = true, ulong seed = 1)
    {
        var w = Arena.NewSentinel(N, seed, edit: fog ? s => s with { Fog = true } : null);
        var flags = new byte[N * N];
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                flags[y * N + x] = (byte)(x is >= 100 and < 140 && y is >= 100 and < 140 ? 3 : 0);
            }
        }
        w.AttachTerrain(new TerrainMap(N, N, new int[(N + 1) * (N + 1)], flags, Rules));
        return w;
    }

    // Overhead: an Overseer put down in flight over (x, y) (the sandbox's way), orbiting there.
    private static Unit Overhead(World w, int x = 100, int y = 100)
    {
        var d = w.Spawn(1, "overseer_drone", x, y);
        w.Run(5);
        Assert.True(w.InAir(d));
        return d;
    }

    private static Unit Foe(World w, string archetype, int x, int y)
    {
        var t = w.Spawn(2, archetype, x, y);
        t.Stance = Stance.HoldFire; // it never fires unless a test makes it (a shot reveals it)
        return t;
    }

    private static void Track(World w, Unit drone, Unit target, byte player = 1) =>
        w.Order(player, CommandKind.Track, [drone], target: target.Id);

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    // LevelCells is the level distance from the point under drone d to unit t, whole cells.
    private static long LevelCells(Unit d, Unit t) => Cells((t.Pos - d.Pos).Len().Raw);

    private static int RunUntil(World w, Func<bool> done, int max)
    {
        for (var i = 1; i <= max; i++)
        {
            w.Step();
            if (done())
            {
                return i;
            }
        }
        return -1;
    }

    // Hide puts t still under the dense wood at (x, y) (moved there, its engine warm from the move) and steps once.
    private static void Hide(World w, Unit t, int x = 112, int y = 112)
    {
        t.Pos = new Cell(x, y).Center();
        t.Vel = new Vec(Fixed.FromRaw(20), Fixed.Zero); // it drove in: its engine is warm
        w.Step();
        t.Vel = default;
    }

    [Fact]
    public void TheOverseerCarriesTheTrackBlockWithTheOwnersNumbers()
    {
        var a = TestContent.Mvp.Archetypes.Single(x => x.Id == "overseer_drone");
        var tr = a.Track!;
        Assert.Equal(80 * Fixed.OneRaw / 10 / World.TicksPerSecond, tr.FollowRaw); // 8 cells/s
        Assert.Equal((150, 25, 400, 200, 1200), (tr.SearchStartPercent, tr.SearchStepPercent, tr.SearchStepTicks, tr.SearchMaxPercent, tr.SearchTicks));
        Assert.Equal([150, 150, 175, 175, 200, 200, 200], new[] { 0L, 399, 400, 799, 800, 1199, 5000 }.Select(tr.SearchPercentAt));
        Assert.Equal(["overseer_drone"], TestContent.Mvp.Archetypes.Where(x => x.Track is not null).Select(x => x.Id));
    }

    [Fact]
    public void ALockedTargetStaysSpottedUnderCanopyStillAndColdWhileInSight()
    {
        // Control: without the track the Overseer's thermal loses the parked truck once its engine is cold (30 s).
        var control = Wooded();
        var eye0 = Overhead(control);
        var truck0 = Foe(control, "technical", 95, 95);
        control.Run(Look);
        Assert.True(World.IsSpotted(truck0, 1)); // in the open, in its sight
        Hide(control, truck0);
        control.Run(Look);
        Assert.True(World.IsSpotted(truck0, 1)); // thermal: its engine is warm
        control.Run(700);
        Assert.False(World.IsSpotted(truck0, 1)); // cold under dense canopy: hidden from the air
        Assert.Null(control.TrackOf(eye0));

        var w = Wooded();
        var eye = Overhead(w);
        var truck = Foe(w, "technical", 95, 95);
        w.Run(Look);
        Track(w, eye, truck);
        w.Step();
        Assert.Equal(TrackPhase.Locked, w.TrackOf(eye)!.Value.Phase);
        Assert.Contains(w.TrackEvents, e => e.Kind == TrackEventKind.Locked && e.Target == truck.Id && e.Drone == eye.Id);
        Hide(w, truck);
        for (var i = 0; i < 900; i++)
        {
            w.Step();
            Assert.True(World.IsSpotted(truck, 1), $"tick {i}: the lock holds under canopy, still and cold");
            Assert.True(LevelCells(eye, truck) <= Sight, $"tick {i}: in its sight ({LevelCells(eye, truck)} cells)");
        }
        Assert.Equal(TrackPhase.Locked, w.TrackOf(eye)!.Value.Phase);
        Assert.True(w.TrackOf(eye)!.Value.InSight);
        Assert.True(w.AirCoverOf(truck).Hidden); // the canopy still hides it from cameras: only the lock shows it
        Assert.True(w.FogMatchesRebuild());
    }

    [Fact]
    public void ALockedSoldierStaysSpottedStillInDenseForest()
    {
        var w = Wooded();
        var eye = Overhead(w);
        var rifle = Foe(w, "Rifleman", 95, 95);
        w.Run(Look);
        Track(w, eye, rifle);
        w.Step();
        Hide(w, rifle, 110, 110); // no air sensor ever sees a still soldier in dense forest
        w.Run(300);
        Assert.True(World.IsSpotted(rifle, 1));
        Assert.Equal(TrackPhase.Locked, w.TrackOf(eye)!.Value.Phase);
    }

    [Fact]
    public void ItFollowsADirtBikeAcrossTheMapWithoutLosingItSmoothly()
    {
        var w = Wooded();
        var eye = Overhead(w, 30, 60);
        var (bike, _) = w.SpawnCrewed(2, "dirt_bike", 30, 60); // the fastest ground unit: 4.5 cells/s
        w.Run(Look);
        Track(w, eye, bike);
        w.Step();
        Assert.Equal(TrackPhase.Locked, w.TrackOf(eye)!.Value.Phase);
        w.Order(2, CommandKind.Move, [bike], cell: new Cell(185, 60));
        var follow = eye.Def.Archetype.Track!.FollowRaw;
        var maxGap = 0L;
        var prev = eye.OrbitAt;
        for (var i = 0; i < 1200; i++)
        {
            w.Step();
            Assert.Equal(TrackPhase.Locked, w.TrackOf(eye)!.Value.Phase);
            Assert.True(World.IsSpotted(bike, 1));
            maxGap = Math.Max(maxGap, LevelCells(eye, bike));
            Assert.True((eye.OrbitAt - prev).Len().Raw <= follow + 1, $"tick {i}: the orbit centre glides (at most 8 cells/s)");
            prev = eye.OrbitAt;
        }
        Assert.True(Cells(bike.Pos.X.Raw) >= 150, $"it drove across: x {Cells(bike.Pos.X.Raw)}");
        Assert.InRange(maxGap, Orbit - 5, Sight - 5); // round its orbit, never near the edge of its sight
        Assert.Equal(0, w.AirEdgeClamps);
        Assert.True(eye.Altitude.Raw >= 240 * 512, "at its height");
    }

    // A technical (3.5 cells/s, S-turns) and a cargo truck (2.2 cells/s, a circle) driven round a square, then back the way they
    // came: the lock never breaks and the drone keeps well inside its sight.
    [Theory]
    [InlineData("technical")]
    [InlineData("cargo_truck")]
    [InlineData("Rifleman")]
    public void ItKeepsItThroughTurnsAndAReversal(string kind)
    {
        var w = Wooded();
        var eye = Overhead(w, 40, 40);
        var (car, _) = kind == "Rifleman" ? (Foe(w, kind, 40, 40), null!) : w.SpawnCrewed(2, kind, 40, 40);
        w.Run(Look);
        Track(w, eye, car);
        w.Step();
        Cell[] legs = [new(90, 40), new(90, 90), new(40, 90), new(150, 160), new(40, 40)];
        var leg = 0;
        w.Order(2, CommandKind.Move, [car], cell: legs[0]);
        var maxGap = 0L;
        for (var i = 0; i < 9000 && leg < legs.Length; i++)
        {
            w.Step();
            Assert.Equal(TrackPhase.Locked, w.TrackOf(eye)!.Value.Phase);
            maxGap = Math.Max(maxGap, LevelCells(eye, car));
            if (car.Order.Kind == OrderKind.Idle && i > 20 && ++leg < legs.Length)
            {
                w.Order(2, CommandKind.Move, [car], cell: legs[leg]);
            }
        }
        Assert.Equal(legs.Length, leg); // it drove every leg
        Assert.True(maxGap <= Sight - 3, $"at most {maxGap} cells");
    }

    [Fact]
    public void TheLockBreaksOutOfSightAndItSearchesWiderAndWiderThenGivesUp()
    {
        var w = Wooded();
        var eye = Overhead(w, 60, 60);
        var truck = Foe(w, "technical", 62, 62);
        w.Run(Look);
        Track(w, eye, truck);
        w.Run(200);
        var last = truck.Pos;
        truck.Pos = new Cell(190, 190).Center(); // gone at once, far out of its sight
        w.Step();
        var st = w.TrackOf(eye)!.Value;
        Assert.Equal(TrackPhase.Searching, st.Phase);
        Assert.Equal(last, st.LastSeen);
        Assert.Equal(w.Tick - 1, st.LostAt);
        Assert.Equal(st.LostAt, st.SearchFrom); // over the last known position already: the search starts at once
        Assert.Contains(w.TrackEvents, e => e.Kind == TrackEventKind.Lost && e.Why == TrackLoss.OutOfSight && e.Cell == Cell.Of(last));
        Assert.Equal(last, eye.OrbitAt); // its orbit centre: the last known position
        w.Run(Look - 1);
        Assert.False(World.IsSpotted(truck, 1)); // (what its last look saw stays spotted until its next look)
        Assert.Equal(AirActivity.TrackSearching, w.AirStatusOf(eye).Activity);
        // The radius: 45, 52.5, then 60 cells, each flown (it spirals out to it), for 60 s.
        // (The drone law settles about 13% inside the circle it aims round, as every orbit always did: 26 of 30 cells.)
        long[] want = [45 * Fixed.OneRaw, 105 * Fixed.OneRaw / 2, 60 * Fixed.OneRaw];
        var before = 0L;
        for (var step = 0; step < 3; step++)
        {
            w.Run(step == 0 ? 380 - Look + 1 : 400);
            var r = w.TrackOf(eye)!.Value.Radius.Raw;
            Assert.Equal(want[step], r);
            var flown = (eye.Pos - last).Len().Raw;
            Assert.InRange(flown, r * 80 / 100, r + 2 * Fixed.OneRaw);
            Assert.True(flown > before, $"step {step}: the circle it flies widens ({Cells(flown)} cells)");
            before = flown;
        }
        Assert.Equal(st.SearchFrom + 1200, w.TrackOf(eye)!.Value.SearchEnds);
        Assert.InRange(w.AirStatusOf(eye).Percent, 95, 100);
        Assert.True(RunUntil(w, () => eye.AirMission is null, 40) > 0);
        Assert.Contains(w.TrackEvents, e => e.Kind == TrackEventKind.GaveUp);
        Assert.Equal(st.SearchFrom + 1200, w.Tick - 1);
        Assert.Equal(last, eye.OrbitAt); // a normal orbit there
        Assert.Equal(AirActivity.Orbiting, w.AirStatusOf(eye).Activity);
        w.Run(600);
        Assert.InRange((eye.Pos - last).Len().Raw, Orbit * Fixed.OneRaw * 80 / 100, (Orbit + 2) * Fixed.OneRaw);
    }

    [Fact]
    public void IntoABuildingTheLockBreaksAndComingOutItIsLockedAgain()
    {
        var w = Wooded();
        var eye = Overhead(w, 60, 60);
        var house = w.Spawn(2, "block_brick_4", 70, 70);
        var rifle = Foe(w, "Rifleman", 64, 76);
        w.Run(Look);
        Track(w, eye, rifle);
        w.Step();
        Assert.Equal(TrackPhase.Locked, w.TrackOf(eye)!.Value.Phase);
        w.Order(2, CommandKind.Enter, [rifle], target: house.Id);
        Assert.True(RunUntil(w, () => rifle.Garrison != 0, 600) > 0);
        w.Step();
        var st = w.TrackOf(eye)!.Value;
        Assert.Equal(TrackPhase.Searching, st.Phase);
        Assert.False(World.IsSpotted(rifle, 1));
        w.Run(200);
        w.Order(2, CommandKind.Exit, [rifle]);
        Assert.True(RunUntil(w, () => w.TrackOf(eye)?.Phase == TrackPhase.Locked, 100) > 0);
        Assert.Contains(w.TrackEvents, e => e.Kind == TrackEventKind.Relocked && e.Target == rifle.Id);
        Assert.Equal(0u, rifle.Garrison);
        Assert.True(World.IsSpotted(rifle, 1));
        Assert.Equal(0u, w.TrackOf(eye)!.Value.LostAt);
    }

    [Fact]
    public void IntoATunnelTheLockBreaksAndOutOfTheOtherEntranceItIsLockedAgain()
    {
        var w = Wooded();
        var eye = Overhead(w, 60, 60);
        var a = w.Spawn(2, "tunnel_medium", 60, 75);
        var b = w.Spawn(2, "tunnel_medium", 85, 70);
        var (truck, _) = w.SpawnCrewed(2, "technical", 52, 78);
        truck.Stance = Stance.HoldFire;
        w.Run(Look);
        Track(w, eye, truck);
        w.Step();
        w.Order(2, CommandKind.Enter, [truck], target: a.Id);
        Assert.True(RunUntil(w, () => truck.InTunnel, 600) > 0);
        w.Step();
        Assert.Equal(TrackPhase.Searching, w.TrackOf(eye)!.Value.Phase);
        Assert.False(World.IsSpotted(truck, 1));
        w.Run(100);
        Assert.Equal(TrackPhase.Searching, w.TrackOf(eye)!.Value.Phase);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.TunnelGo, Units = [truck.Id], Target = b.Id, On = true });
        Assert.True(RunUntil(w, () => w.TrackOf(eye)?.Phase == TrackPhase.Locked, 1000) > 0, "out of the other entrance, in its sight");
        Assert.False(truck.InTunnel);
        Assert.True(World.IsSpotted(truck, 1));
    }

    [Fact]
    public void IntoAVehicleTheLockBreaks()
    {
        var w = Wooded();
        var eye = Overhead(w, 60, 60);
        var rifle = Foe(w, "Rifleman", 64, 64);
        var car = Foe(w, "technical", 66, 64);
        w.Run(Look);
        Track(w, eye, rifle);
        w.Step();
        World.Board(rifle, car);
        w.Step();
        Assert.Equal(TrackPhase.Searching, w.TrackOf(eye)!.Value.Phase);
        Assert.Contains(w.TrackEvents, e => e.Kind == TrackEventKind.Lost && e.Why == TrackLoss.Inside);
    }

    [Fact]
    public void ADeadTargetEndsTheTrackAndItOrbitsWhereItWas()
    {
        var w = Wooded();
        var eye = Overhead(w, 60, 60);
        var truck = Foe(w, "technical", 70, 70);
        w.Run(Look);
        Track(w, eye, truck);
        w.Run(300);
        var at = eye.OrbitAt;
        Assert.Equal(truck.Pos, at); // its orbit centre caught up with the target
        truck.HP = 0;
        w.Step();
        Assert.Null(eye.AirMission);
        Assert.Null(w.TrackOf(eye));
        Assert.Contains(w.TrackEvents, e => e.Kind == TrackEventKind.TargetGone && e.Target == truck.Id);
        Assert.Equal(at, eye.OrbitAt);
        Assert.Equal(AirActivity.Orbiting, w.AirStatusOf(eye).Activity);
    }

    [Fact]
    public void ItRefusesWhatItCannotTrack()
    {
        var w = Wooded();
        var eye = Overhead(w, 60, 60);
        var far = Foe(w, "technical", 180, 20);     // out of every sight of player 1
        var hut = w.Spawn(2, "block_brick_4", 70, 70); // a structure
        var own = w.Spawn(1, "Rifleman", 64, 64);       // its own side
        var watcher = w.Spawn(1, "watcher_drone", 60, 60); // an aircraft (and its own)
        w.Run(Look);
        Assert.Equal(OrderRefusalReason.NotVisible, w.TrackRefusalOf(eye, far));
        Assert.Equal(OrderRefusalReason.NotTrackable, w.TrackRefusalOf(eye, hut));
        Assert.Equal(OrderRefusalReason.NotTrackable, w.TrackRefusalOf(eye, own));
        Assert.Equal(OrderRefusalReason.NotTrackable, w.TrackRefusalOf(eye, watcher));
        Assert.Equal(OrderRefusalReason.NotTrackable, w.TrackRefusalOf(eye, null));
        foreach (var t in new[] { far, hut, own })
        {
            Track(w, eye, t);
            w.Step();
            var r = Assert.Single(w.OrderRefusals);
            Assert.Equal((eye.Id, t.Id), (r.Aircraft, r.Target));
            Assert.Equal(t == far ? OrderRefusalReason.NotVisible : OrderRefusalReason.NotTrackable, r.Reason);
            Assert.Null(eye.AirMission);
        }
        // A drone without a track block (the Watcher) cannot: the order is not its, nothing happens.
        var near = Foe(w, "technical", 62, 62);
        w.Run(Look);
        Assert.False(World.CanTrack(watcher));
        Assert.True(World.CanTrack(eye));
        Assert.Equal(OrderRefusalReason.NotTrackable, w.TrackRefusalOf(watcher, near));
        Track(w, watcher, near);
        w.Step();
        Assert.Empty(w.OrderRefusals);
        Assert.Null(watcher.AirMission);
        // The player's own order only: another player's Track on its drone is ignored.
        Track(w, eye, near, player: 2);
        w.Step();
        Assert.Null(eye.AirMission);
    }

    [Fact]
    public void OrderedFromFarItFliesThereAndLocksOnceInSight()
    {
        var w = Wooded();
        var eye = Overhead(w, 30, 30);
        var scout = w.Spawn(1, "Rifleman", 170, 172); // its side spots the target
        var truck = Foe(w, "technical", 172, 172);
        w.Step();
        Assert.True(World.IsSpotted(truck, 1));
        Track(w, eye, truck);
        w.Step();
        Assert.Equal(TrackPhase.Approach, w.TrackOf(eye)!.Value.Phase);
        Assert.Equal(AirActivity.Tracking, w.AirStatusOf(eye).Activity);
        Assert.Equal(truck.Pos, eye.OrbitAt);
        var n = RunUntil(w, () => w.TrackOf(eye)?.Phase == TrackPhase.Locked, 900);
        Assert.True(n > 0, "it locks once the target is in its sight");
        Assert.InRange(LevelCells(eye, truck), Sight - 2, Sight);
        scout.HP = 0; // its side's other eyes are gone: the lock alone keeps it spotted
        w.Run(200);
        Assert.True(World.IsSpotted(truck, 1));
    }

    [Fact]
    public void LostOnTheWayItSearchesOnlyOnceOverTheLastKnownPosition()
    {
        var w = Wooded();
        var eye = Overhead(w, 30, 30);
        var scout = w.Spawn(1, "Rifleman", 170, 172);
        var truck = Foe(w, "technical", 172, 172);
        w.Run(Look);
        Track(w, eye, truck);
        w.Run(20);
        var last = truck.Pos;
        scout.HP = 0; // its side stops spotting the target before the drone is there
        Assert.True(RunUntil(w, () => w.TrackOf(eye)!.Value.Phase == TrackPhase.Searching, Look + 1) > 0);
        Assert.Contains(w.TrackEvents, e => e.Kind == TrackEventKind.Lost && e.Why == TrackLoss.Unspotted);
        var st = w.TrackOf(eye)!.Value;
        Assert.Equal(TrackPhase.Searching, st.Phase);
        Assert.Equal(0u, st.SearchFrom); // far from the last known position: the search waits for it
        Assert.Equal(last, eye.OrbitAt);
        Assert.Equal(150, eye.Def.Archetype.Track!.SearchPercentAt(0));
        var n = RunUntil(w, () => w.TrackOf(eye)!.Value.SearchFrom != 0, 900);
        Assert.True(n > 0);
        Assert.InRange(LevelCells(eye, truck), 0, Sight);
        // Then its own look finds the truck in the open: locked again.
        Assert.True(RunUntil(w, () => w.TrackOf(eye)?.Phase == TrackPhase.Locked, 5) > 0);
    }

    [Fact]
    public void SearchingItReLocksOnlyWhatItsOwnEyeSpots()
    {
        // Lost out of sight, the truck (its engine never ran) comes back still under the dense wood inside the search circle: its
        // own look cannot spot it there, so no re-lock; out in the open at the last known position it is spotted and locked again.
        var w = Wooded();
        var eye = Overhead(w, 100, 100);
        var truck = Foe(w, "technical", 95, 95);
        w.Run(Look);
        Track(w, eye, truck);
        w.Run(100);
        var last = truck.Pos;
        truck.Pos = new Cell(195, 195).Center(); // gone out of its sight
        w.Step();
        Assert.Equal(TrackPhase.Searching, w.TrackOf(eye)!.Value.Phase);
        w.Run(20);
        truck.Pos = new Cell(103, 103).Center(); // back under the wood, still, cold
        w.Run(200);
        Assert.Equal(TrackPhase.Searching, w.TrackOf(eye)!.Value.Phase);
        Assert.False(World.IsSpotted(truck, 1));
        truck.Pos = last; // in the open, where it was lost: inside its sight from the 45-cell circle
        Assert.True(RunUntil(w, () => w.TrackOf(eye)?.Phase == TrackPhase.Locked, Look + 1) > 0);
        Assert.True(World.IsSpotted(truck, 1));
    }

    [Fact]
    public void AGarrisonedSoldierIsNeverFoundAndTheSearchGivesUp()
    {
        var w = Wooded();
        var eye = Overhead(w, 100, 100);
        var house = w.Spawn(2, "block_brick_4", 92, 85);
        var rifle = Foe(w, "Rifleman", 96, 96);
        w.Run(Look);
        Track(w, eye, rifle);
        w.Step();
        w.Order(2, CommandKind.Enter, [rifle], target: house.Id);
        Assert.True(RunUntil(w, () => rifle.Garrison != 0, 600) > 0);
        w.Step();
        Assert.Equal(TrackPhase.Searching, w.TrackOf(eye)!.Value.Phase);
        Assert.True(RunUntil(w, () => eye.AirMission is null, 1300) > 0);
        Assert.Contains(w.TrackEvents, e => e.Kind == TrackEventKind.GaveUp && e.Target == rifle.Id);
    }

    [Fact]
    public void MoveStopOrAnAirTaskEndTheTrack()
    {
        foreach (var end in new[] { CommandKind.Move, CommandKind.Stop, CommandKind.AirTask })
        {
            var w = Wooded();
            var eye = Overhead(w, 60, 60);
            var truck = Foe(w, "technical", 62, 62);
            w.Run(Look);
            Track(w, eye, truck);
            w.Run(10);
            Assert.NotNull(w.TrackOf(eye));
            w.Issue(new Command
            {
                Tick = w.Tick, Player = 1, Kind = end, Units = [eye.Id], Cell = new Cell(150, 40),
                Mission = end == CommandKind.AirTask ? AirTaskKind.Patrol : default,
            });
            w.Step();
            Assert.Null(w.TrackOf(eye));
            Assert.Empty(w.TrackEvents);
        }
    }

    [Fact]
    public void ATripHomeEndsTheTrackAndAHomingDroneRefusesIt()
    {
        var w = Wooded();
        var station = w.Spawn(1, "overseer_station", 20, 100);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(60, 100), ability: "overseer_launch");
        w.Step();
        var eye = Assert.Single(w.All("overseer_drone"));
        var truck = Foe(w, "technical", 40, 100);
        Assert.True(RunUntil(w, () => World.IsSpotted(truck, 1), 200) > 0);
        Track(w, eye, truck);
        w.Run(100);
        Assert.Equal(TrackPhase.Locked, w.TrackOf(eye)!.Value.Phase);
        w.Order(1, CommandKind.ReturnToBase, [eye]);
        w.Step();
        Assert.True(eye.Homing);
        Assert.Null(w.TrackOf(eye));
        Assert.Contains(w.TrackEvents, e => e.Kind == TrackEventKind.Home);
        Track(w, eye, truck);
        w.Step();
        Assert.Equal(OrderRefusalReason.Homing, Assert.Single(w.OrderRefusals).Reason);
        Assert.Null(w.TrackOf(eye));
    }

    [Fact]
    public void WithoutFogItTracksTooAndTheLockIsItsSight()
    {
        var w = Wooded(fog: false);
        var eye = Overhead(w, 60, 60);
        var truck = Foe(w, "technical", 62, 62);
        w.Run(Look);
        Track(w, eye, truck);
        w.Step();
        Assert.Equal(TrackPhase.Locked, w.TrackOf(eye)!.Value.Phase);
        truck.Pos = new Cell(190, 190).Center();
        w.Step();
        Assert.Equal(TrackPhase.Searching, w.TrackOf(eye)!.Value.Phase);
    }

    // Scenario: an Overseer locks a truck, it hides cold under the wood, drives out of sight, the drone searches; per-tick hashes.
    private static List<ulong> Scenario(bool reverse)
    {
        var w = Wooded(seed: 9);
        var a = Overhead(w, 100, 100);
        var b = w.Spawn(1, "overseer_drone", 90, 100);
        var truck = Foe(w, "technical", 95, 95);
        w.Run(Look);
        foreach (var d in reverse ? new[] { b, a } : [a, b])
        {
            Track(w, d, truck);
        }
        var hashes = new List<ulong>();
        for (var i = 0; i < 1500; i++)
        {
            if (i == 10)
            {
                truck.Pos = new Cell(112, 112).Center();
            }
            if (i == 900)
            {
                truck.Pos = new Cell(195, 20).Center();
            }
            w.Step();
            hashes.Add(w.Hash());
        }
        Assert.Equal(TrackPhase.Searching, w.TrackOf(a)!.Value.Phase);
        return hashes;
    }

    [Fact]
    public void ItIsDeterministicAndTheOrderOfTheOrdersDoesNotMatter()
    {
        var one = Scenario(false);
        Assert.Equal(one, Scenario(false));
        Assert.Equal(one, Scenario(true));
    }

    [Fact]
    public void TheTrackIsHashedAndNothingChangesWithoutIt()
    {
        // The same world with and without a Track order the drone could not take (the Watcher's): the same hashes.
        static (World, Unit, Unit, Unit) Make()
        {
            var w = Wooded();
            var eye = Overhead(w, 60, 60);
            var watcher = w.Spawn(1, "watcher_drone", 70, 60);
            var truck = Foe(w, "technical", 62, 62);
            w.Run(Look);
            return (w, eye, watcher, truck);
        }
        var (plain, _, _, _) = Make();
        var (ignored, _, watcher, truck) = Make();
        Track(ignored, watcher, truck);
        for (var i = 0; i < 200; i++)
        {
            plain.Step();
            ignored.Step();
            Assert.Equal(plain.Hash(), ignored.Hash());
        }
        // A Track changes the hash (its mission is hashed) and so does each phase.
        var (tracked, eye2, _, truck2) = Make();
        var (base2, _, _, _) = Make();
        Track(tracked, eye2, truck2);
        tracked.Step();
        base2.Step();
        Assert.NotEqual(base2.Hash(), tracked.Hash());
        var locked = tracked.Hash();
        eye2.AirMission!.Phase = TrackPhase.Searching;
        Assert.NotEqual(locked, tracked.Hash());
    }
}
