using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// The vehicle garage: a queue, two bays producing at once, four doors; a finished vehicle takes a
// random free door (never the last one's), the door opens (1.5 s), the vehicle appears crewed on
// its bay floor inside and drives out through the door to the rally point at its own speed, the
// door closing behind it; friends in the exit lane step aside, enemies block (until the
// push-through); nothing while under construction; the queue lost with the garage.
public class GarageTests
{
    private const int Door = 30;

    private static (World W, Unit Garage) Yard(ulong seed = 1)
    {
        var w = Arena.New(64, seed);
        var g = w.Spawn(1, "vehicle_garage", 20, 10); // x 20-39, y 10-20; doors south
        return (w, g);
    }

    private static void Produce(World w, Unit g, string unit, byte player = 1) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.Produce, Units = [g.Id], Unit = unit });

    private static List<Unit> Made(World w, string arch) => w.Units.Where(u => u.Def.Archetype.Id == arch).ToList();

    private static int DoorOf(Unit g, Unit v) => Array.FindIndex(g.Doors, d => d.Vehicle == v.Id);

    [Fact]
    public void TwoBaysProduceAtOnceInQueueOrderCrewedToTheRally()
    {
        var (w, g) = Yard();
        foreach (var u in new[] { "technical", "rocket_jeep", "cargo_truck" })
        {
            Produce(w, g, u);
        }
        w.Step();
        Assert.Equal(["technical", "rocket_jeep"], g.Bays.Select(b => w.Match.Defs[b.Def].Archetype.Id));
        Assert.Equal(["cargo_truck"], g.ProductionQueue.Select(d => w.Match.Defs[d].Archetype.Id));
        w.Run(201);
        Assert.Equal("cargo_truck", w.Match.Defs[g.Bays[0].Def].Archetype.Id); // the technical took a door: bay 0 is free
        w.Run(Door - 1);
        var tech = Assert.Single(Made(w, "technical"));
        Assert.NotEqual(0u, tech.Driver);
        Assert.Equal(OrderKind.Move, tech.Order.Kind);
        Assert.Equal(new Cell(28, 26), tech.Order.Cell); // the art's rally: 10 m in front
        var door = DoorOf(g, tech);
        Assert.True(Within(tech.Pos, World.BayPoint(g, door), 1)); // on its bay floor, inside (pulling away)
        Assert.Equal(g.Id, tech.OutOf);
        Assert.Equal(DoorState.Open, g.Doors[door].State);
        w.Run(100);
        Assert.Single(Made(w, "rocket_jeep"));
        Assert.Equal(DoorState.Closed, g.Doors[door].State); // it cleared the lane; the door closed
        w.Run(400);
        Assert.Single(Made(w, "cargo_truck"));
        Assert.True(Within(Made(w, "technical")[0].Pos, new Cell(28, 26).Center(), 3));
    }

    [Fact]
    public void ItDrivesOutThroughItsDoorAtItsOwnSpeed()
    {
        var (w, g) = Yard();
        Produce(w, g, "technical");
        w.Run(201 + Door);
        var tech = Assert.Single(Made(w, "technical"));
        var door = DoorOf(g, tech);
        var exit = World.ExitPoint(g, door);
        var wall = (long)(10 + 11) * Fixed.OneRaw;
        Assert.True(tech.Pos.Y.Raw < wall - 3 * Fixed.OneRaw); // well inside
        var speed = tech.Def.Speed.Raw;
        var passed = false;
        var first = -1L;
        for (var t = 0; t < 200 && tech.OutOf != 0; t++)
        {
            var before = tech.Pos;
            w.Step();
            var step = (tech.Pos - before).Len().Raw;
            Assert.True(step <= speed + 1, $"tick {t}: {step} > speed {speed}");
            first = first < 0 && step > 0 ? step : first;
            Assert.True(Math.Abs(tech.Pos.X.Raw - exit.X.Raw) < Fixed.OneRaw); // straight down its lane
            passed |= tech.Pos.Y.Raw >= wall;
        }
        Assert.True(passed);
        Assert.Equal(0u, tech.OutOf); // out: its garage's footprint blocks it again
        Assert.True(first < speed); // it pulled away from standing
        w.Run(100);
        Assert.Equal(DoorState.Closed, g.Doors[door].State);
        Assert.True(Within(tech.Pos, new Cell(28, 26).Center(), 3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OrderedAwayInsideItStillLeavesThroughItsDoor(int movers)
    {
        var (w, g) = Yard();
        var friend = w.Spawn(1, "Rifleman", 45, 30);
        Produce(w, g, "technical");
        w.Run(201 + Door);
        var tech = Assert.Single(Made(w, "technical"));
        var exit = World.ExitPoint(g, DoorOf(g, tech));
        w.Order(1, CommandKind.Move, movers == 1 ? [tech] : [tech, friend], cell: new Cell(50, 40)); // a group move too
        var wall = (long)(10 + 11) * Fixed.OneRaw;
        for (var t = 0; t < 300 && tech.OutOf != 0; t++)
        {
            w.Step();
            if (tech.Pos.Y.Raw < wall)
            {
                Assert.True(Math.Abs(tech.Pos.X.Raw - exit.X.Raw) < Fixed.OneRaw, $"tick {t}: {tech.Pos} off its lane");
            }
        }
        Assert.Equal(0u, tech.OutOf);
        w.Run(400);
        Assert.True(Within(tech.Pos, new Cell(50, 40).Center(), 4));
    }

    // The doors the garage's finished vehicles took, in order (two the same tick: the last one last).
    private static List<int> DoorSequence(ulong seed, int vehicles)
    {
        var (w, g) = Yard(seed);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetRally, Units = [g.Id], Cell = new Cell(30, 56) });
        var seq = new List<int>();
        var was = new DoorState[4];
        var queued = 0;
        for (var t = 0; t < 20_000 && seq.Count < vehicles; t++)
        {
            if (queued < vehicles && g.ProductionQueue.Count < 4)
            {
                Produce(w, g, "dirt_bike");
                queued++;
            }
            w.Step();
            var opened = Enumerable.Range(0, g.Doors.Length).Where(i => was[i] == DoorState.Closed && g.Doors[i].State == DoorState.Opening).ToList();
            opened.Sort((a, b) => (a == g.LastDoor).CompareTo(b == g.LastDoor));
            seq.AddRange(opened);
            for (var i = 0; i < g.Doors.Length; i++)
            {
                was[i] = g.Doors[i].State;
            }
            foreach (var u in Made(w, "dirt_bike").Where(u => u.Pos.Y.Raw > 40L * Fixed.OneRaw))
            {
                u.HP = -1000; // far enough: out of the way of the next ones
            }
        }
        return seq;
    }

    [Fact]
    public void ARandomFreeDoorNeverTheLastOneAcrossAllFour()
    {
        var seq = DoorSequence(7, 48);
        Assert.Equal(48, seq.Count);
        for (var i = 1; i < seq.Count; i++)
        {
            Assert.NotEqual(seq[i - 1], seq[i]);
        }
        for (var d = 0; d < 4; d++)
        {
            Assert.True(seq.Count(x => x == d) >= 6, $"door {d}: {seq.Count(x => x == d)} of 48 ({string.Join(",", seq)})");
        }
    }

    [Fact]
    public void TheDoorPickIsDeterministicAndSeeded()
    {
        Assert.Equal(DoorSequence(7, 24), DoorSequence(7, 24));
        Assert.NotEqual(DoorSequence(7, 24), DoorSequence(8, 24));
    }

    [Fact]
    public void OnlyTheLastDoorFreeItWaitsTwoSecondsThenTakesIt()
    {
        var (w, g) = Yard();
        w.Step();
        Assert.Equal(4, g.Doors.Length);
        g.LastDoor = 2;
        foreach (var i in new[] { 0, 1, 3 })
        {
            g.Doors[i].State = DoorState.Open;
        }
        Assert.Equal(-1, w.PickDoor(g, false)); // only the last one's: it waits
        Assert.Equal(2, w.PickDoor(g, true)); // waited 2 s: it takes it
        g.Doors[1].State = DoorState.Closed;
        Assert.Equal(1, w.PickDoor(g, false));
        g.Doors[1].Def = 0; // reserved
        Assert.Equal(-1, w.PickDoor(g, false));
    }

    [Fact]
    public void AFinishedVehicleWaitsForADoor()
    {
        var (w, g) = Yard();
        Produce(w, g, "dirt_bike");
        w.Step();
        g.LastDoor = 2;
        foreach (var i in new[] { 0, 1, 3 })
        {
            g.Doors[i].Def = 0; // reserved for others
        }
        w.Run(120);
        Assert.Equal(0, g.Bays[0].Left);
        Assert.True(g.Bays[0].Def >= 0); // finished, waiting
        Assert.Equal(DoorState.Closed, g.Doors[2].State);
        w.Run(40);
        Assert.Equal(DoorState.Opening, g.Doors[2].State); // after 2 s: the last one's door
    }

    private static bool Within(Vec a, Vec b, int cells) => (a - b).LenSq() <= (long)cells * Fixed.OneRaw * cells * Fixed.OneRaw;

    [Fact]
    public void ExitsAndBayPointsAreTheArts()
    {
        var (w, g) = Yard();
        var y = Fixed.FromRaw(22 * Fixed.OneRaw + Fixed.OneRaw / 2); // 3 m out of the 22 m deep garage
        Assert.Equal(new Vec(Fixed.FromInt(26), y), World.ExitPoint(g, 0));
        Assert.Equal(new Vec(Fixed.FromInt(30), y), World.ExitPoint(g, 1));
        Assert.Equal(new Vec(Fixed.FromInt(22), y), World.ExitPoint(g, 2));
        Assert.Equal(new Vec(Fixed.FromInt(34), y), World.ExitPoint(g, 3));
        Assert.Equal(new Vec(Fixed.FromInt(26), Fixed.FromRaw(17 * Fixed.OneRaw + Fixed.OneRaw / 5)), World.BayPoint(g, 0)); // 7.5 m inside
    }

    [Fact]
    public void BelowThirtyFivePercentItStops()
    {
        var (w, g) = Yard();
        Produce(w, g, "technical");
        w.Step();
        g.HP = g.Def.MaxHp * 34 / 100;
        w.Run(400);
        Assert.Empty(Made(w, "technical"));
        g.HP = g.Def.MaxHp;
        w.Run(250);
        Assert.Single(Made(w, "technical"));
    }

    [Fact]
    public void TheDoorTakesDoorTicksToOpen()
    {
        var (w, g) = Yard();
        Produce(w, g, "technical");
        w.Run(201);
        Assert.Single(g.Doors, d => d.State == DoorState.Opening);
        Assert.Empty(Made(w, "technical"));
        w.Run(Door);
        Assert.Single(Made(w, "technical"));
    }

    [Fact]
    public void CancelTheLastOrABayAndTheQueueIsCapped()
    {
        var (w, g) = Yard();
        for (var i = 0; i < 12; i++)
        {
            Produce(w, g, "technical");
        }
        w.Step();
        Assert.Equal(8, g.ProductionQueue.Count); // 10 at most: 2 in the bays, 8 waiting
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.CancelProduce, Target = g.Id, Index = -1 });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.CancelProduce, Target = g.Id, Index = 1 });
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.CancelProduce, Target = g.Id, Index = -1 });
        w.Step();
        Assert.Equal(6, g.ProductionQueue.Count); // the last one, and bay 1's (the next waiting one took bay 1)
    }

    [Fact]
    public void RallyOnACellOrNextToAUnit()
    {
        var (w, g) = Yard();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetRally, Units = [g.Id], Cell = new Cell(10, 40) });
        Produce(w, g, "technical");
        w.Run(240);
        Assert.Equal(new Cell(10, 40), Made(w, "technical")[0].Order.Cell);
        var depot = w.Spawn(1, "supply_depot", 40, 40);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetRally, Units = [g.Id], Target = depot.Id });
        Produce(w, g, "dirt_bike");
        w.Run(170);
        Assert.Equal(new Cell(43, 47), Made(w, "dirt_bike")[0].Order.Cell);
    }

    [Fact]
    public void AFriendInTheLaneStepsAsideAnEnemyBlocksUntilThePush()
    {
        var (w, g) = Yard();
        // A parked friend in front of every door, and an enemy in front of the door the second takes.
        var parked = Enumerable.Range(0, 4).Select(i => World.ExitPoint(g, i)).Select(e => w.SpawnCrewed(1, "technical", Cell.Of(e).X, Cell.Of(e).Y + 1).Vehicle).ToList();
        Produce(w, g, "technical");
        Produce(w, g, "technical");
        w.Run(201);
        var doors = Enumerable.Range(0, 4).Where(i => g.Doors[i].State == DoorState.Opening).ToList();
        Assert.Equal(2, doors.Count);
        var (d0, d1) = doors[0] == g.LastDoor ? (doors[1], doors[0]) : (doors[0], doors[1]);
        var p = parked[d0];
        Assert.Equal(OrderKind.Move, p.Order.Kind);
        Assert.True(Math.Abs(p.Order.Cell.X - Cell.Of(World.ExitPoint(g, d0)).X) >= 4); // across the lane
        var e1 = World.ExitPoint(g, d1);
        // An enemy truck (a soldier there would be run over by the trucks coming out), holding its ground.
        var enemy = w.SpawnCrewed(2, "cargo_truck", Cell.Of(e1).X, Cell.Of(e1).Y + 1).Vehicle;
        enemy.HP = 100_000;
        w.Run(2 + Door + 57);
        Assert.Equal(5, Made(w, "technical").Count); // the four parked ones and the first one out
        Assert.True(g.Doors[d1].Def >= 0 && g.Doors[d1].Wait > 0); // the second waits behind the enemy
        w.Run(200);
        Assert.Equal(6, Made(w, "technical").Count); // pushed out after the wait
    }

    [Fact]
    public void NothingUnderConstructionAndTheQueueGoesWithTheGarage()
    {
        var w = Arena.New(64);
        var worker = w.Spawn(1, "worker", 18, 12);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = new Cell(20, 10), Unit = "vehicle_garage" });
        w.Step();
        var site = Assert.Single(w.All("vehicle_garage"));
        Produce(w, site, "technical");
        w.Run(300);
        Assert.Empty(Made(w, "technical"));
        Assert.False(site.IsBuilt);
        site.HP = -10_000;
        w.Step();
        Assert.Empty(w.All("vehicle_garage"));
    }

    [Fact]
    public void TheSameOrdersGiveTheSameHashes()
    {
        ulong[] Run()
        {
            var (w, g) = Yard();
            w.SpawnCrewed(1, "technical", 24, 19);
            foreach (var u in new[] { "technical", "rocket_jeep", "cargo_truck", "heavy_rocket_launcher" })
            {
                Produce(w, g, u);
            }
            var hashes = new ulong[900];
            for (var i = 0; i < hashes.Length; i++)
            {
                w.Step();
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }
}
