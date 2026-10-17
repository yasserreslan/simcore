using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// The vehicle garage: a queue, two bays producing at once, doors that open (1.5 s), the vehicle
// out crewed at its bay's exit and off to the rally point, the door closing behind it; friends in
// the exit lane step aside, enemies block (until the push-through); nothing while under
// construction; the queue lost with the garage.
public class GarageTests
{
    private const int Door = 30;

    private static (World W, Unit Garage) Yard()
    {
        var w = Arena.New(64);
        var g = w.Spawn(1, "vehicle_garage", 20, 10); // x 20-39, y 10-20; doors south
        return (w, g);
    }

    private static void Produce(World w, Unit g, string unit, byte player = 1) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.Produce, Units = [g.Id], Unit = unit });

    private static List<Unit> Made(World w, string arch) => w.Units.Where(u => u.Def.Archetype.Id == arch).ToList();

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
        w.Run(200 - 1 + Door + 1);
        var tech = Assert.Single(Made(w, "technical"));
        Assert.NotEqual(0u, tech.Driver);
        Assert.Equal(OrderKind.Move, tech.Order.Kind);
        Assert.Equal(new Cell(28, 26), tech.Order.Cell); // the art's rally: 10 m in front
        Assert.Equal(Cell.Of(World.ExitPoint(g, 0)), Cell.Of(tech.Pos));
        Assert.Equal(DoorState.Open, g.Bays[0].Door);
        w.Run(100);
        Assert.Single(Made(w, "rocket_jeep"));
        Assert.Equal(DoorState.Closed, g.Bays[0].Door); // it cleared the lane; the door closed
        Assert.Equal("cargo_truck", w.Match.Defs[g.Bays[0].Def].Archetype.Id);
        w.Run(400);
        Assert.Single(Made(w, "cargo_truck"));
        Assert.True(Within(Made(w, "technical")[0].Pos, new Cell(28, 26).Center(), 3));
    }

    private static bool Within(Vec a, Vec b, int cells) => (a - b).LenSq() <= (long)cells * Fixed.OneRaw * cells * Fixed.OneRaw;

    [Fact]
    public void ExitsAreTheArtsThreeMetresOutAtEachDoor()
    {
        var (w, g) = Yard();
        Assert.Equal(new Vec(Fixed.FromInt(26), Fixed.FromRaw(22 * Fixed.OneRaw + Fixed.OneRaw / 2)), World.ExitPoint(g, 0));
        Assert.Equal(new Vec(Fixed.FromInt(30), Fixed.FromRaw(22 * Fixed.OneRaw + Fixed.OneRaw / 2)), World.ExitPoint(g, 1));
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
        Assert.Equal(DoorState.Opening, g.Bays[0].Door);
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
        var e0 = World.ExitPoint(g, 0);
        var parked = w.SpawnCrewed(1, "technical", Cell.Of(e0).X, Cell.Of(e0).Y + 1).Vehicle;
        var e1 = World.ExitPoint(g, 1);
        var enemy = w.Spawn(2, "Rifleman", Cell.Of(e1).X, Cell.Of(e1).Y);
        enemy.HP = 100_000;
        Produce(w, g, "technical");
        Produce(w, g, "technical");
        w.Run(203);
        Assert.Equal(OrderKind.Move, parked.Order.Kind);
        Assert.True(Math.Abs(parked.Order.Cell.X - Cell.Of(e0).X) >= 4); // across the lane
        w.Run(Door + 57);
        Assert.Equal(2, Made(w, "technical").Count); // the parked one and bay 0's
        Assert.True(g.Bays[1].Def >= 0 && g.Bays[1].Wait > 0); // bay 1 waits behind the enemy
        w.Run(200);
        Assert.Equal(3, Made(w, "technical").Count); // pushed out after the wait
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
