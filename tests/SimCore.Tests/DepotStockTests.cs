using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// Supply depots hold a finite stock (1000, starting full); reloading launchers costs each
// rocket's depotReload.cost; cargo trucks (400 aboard, full or empty) refill a depot after
// standing 3 s in its radius, only when the whole load fits.
public class DepotStockTests
{
    private const int UnloadTicks = 60;

    private static (World W, Unit Depot) Yard(int stock)
    {
        var w = Arena.New(48);
        var depot = w.Spawn(1, "supply_depot", 20, 20);
        depot.Stock = stock;
        return (w, depot);
    }

    private static Unit EmptyLauncher(World w, string archetype)
    {
        var (t, _) = w.SpawnCrewed(1, archetype, 22, 24);
        t.MutableCharges[0] = 0;
        return t;
    }

    [Fact]
    public void DepotsStartFull()
    {
        var w = Arena.New(32);
        var d = w.Spawn(1, "supply_depot", 5, 5);
        Assert.Equal(1000, d.Stock);
        Assert.Equal(1000, d.Def.Supply!.Stock);
    }

    [Fact]
    public void EachRocketTypeCostsItsShare()
    {
        var (w, depot) = Yard(1000);
        var mrl = EmptyLauncher(w, "heavy_rocket_launcher");
        w.Run(1); // the first rocket at once, then one every 40 ticks
        Assert.Equal(1, mrl.Charges[0]);
        Assert.Equal(1000 - 10, depot.Stock);
        w.Run(40);
        Assert.Equal(2, mrl.Charges[0]);
        Assert.Equal(1000 - 20, depot.Stock);

        var (w2, depot2) = Yard(1000);
        var heavy = EmptyLauncher(w2, "heavy_rocket_truck");
        w2.Run(1); // the first at once, then one every 100 ticks
        Assert.Equal(1, heavy.Charges[0]);
        Assert.Equal(1000 - 40, depot2.Stock);
        w2.Run(100);
        Assert.Equal(2, heavy.Charges[0]);
        Assert.Equal(1000 - 80, depot2.Stock);
    }

    [Fact]
    public void ReloadWaitsWhenTheStockIsBelowTheCost()
    {
        var (w, depot) = Yard(25);
        var mrl = EmptyLauncher(w, "heavy_rocket_launcher");
        w.Run(200);
        Assert.Equal(2, mrl.Charges[0]); // 25 pays for two rockets at 10
        Assert.Equal(5, depot.Stock);
        Assert.True(mrl.DepotStarved);

        depot.Stock = 10;
        w.Run(41);
        Assert.Equal(3, mrl.Charges[0]);
        Assert.Equal(0, depot.Stock);
    }

    [Fact]
    public void ACargoTruckUnloadsAfterThreeSecondsInTheRadius()
    {
        var (w, depot) = Yard(500);
        var truck = w.Spawn(1, "cargo_truck", 23, 21);
        Assert.Equal(400, truck.Cargo);
        w.Run(UnloadTicks - 1);
        Assert.Equal(500, depot.Stock);
        Assert.Equal(400, truck.Cargo);
        Assert.True(truck.UnloadLeft > 0);
        w.Run(1);
        Assert.Equal(900, depot.Stock);
        Assert.Equal(0, truck.Cargo);
        Assert.Equal(0u, truck.UnloadDepot);
    }

    [Fact]
    public void ATruckWaitsForRoomThenUnloadsTheWholeLoad()
    {
        var (w, depot) = Yard(700); // room for 300, not 400
        var truck = w.Spawn(1, "cargo_truck", 23, 21);
        w.Run(200);
        Assert.Equal(700, depot.Stock);
        Assert.Equal(400, truck.Cargo);
        Assert.Equal(0, truck.UnloadLeft);
        Assert.Equal(depot.Id, truck.UnloadDepot);

        depot.Stock = 600; // launchers used some
        w.Run(UnloadTicks);
        Assert.Equal(1000, depot.Stock);
        Assert.Equal(0, truck.Cargo);
    }

    [Fact]
    public void TwoTrucksNeverOverfillTheDepot()
    {
        var (w, depot) = Yard(300); // room for one load (700), not two
        var a = w.Spawn(1, "cargo_truck", 23, 21);
        var b = w.Spawn(1, "cargo_truck", 17, 21);
        w.Run(UnloadTicks * 3);
        Assert.Equal(700, depot.Stock);
        Assert.Equal(0, a.Cargo);
        Assert.Equal(400, b.Cargo); // waiting beside it, still full
        Assert.Equal(0, b.UnloadLeft);
        depot.Stock = 600; // launchers used some: now it fits
        w.Run(UnloadTicks);
        Assert.Equal(1000, depot.Stock);
        Assert.Equal(0, b.Cargo);
    }

    [Fact]
    public void DrivingOffCancelsAndTheTruckStaysFull()
    {
        var (w, depot) = Yard(300);
        var (truck, _) = w.SpawnCrewed(1, "cargo_truck", 23, 21);
        w.Run(30);
        Assert.True(truck.UnloadLeft > 0);
        w.Order(1, CommandKind.Move, [truck], cell: new Cell(44, 44));
        w.Run(150);
        Assert.Equal(0, truck.UnloadLeft);
        Assert.Equal(400, truck.Cargo);
        Assert.Equal(300, depot.Stock);
    }

    [Fact]
    public void LosingTheDepotCancels()
    {
        var (w, depot) = Yard(300);
        var truck = w.Spawn(1, "cargo_truck", 23, 21);
        w.Run(30);
        depot.HP = 0;
        w.Run(UnloadTicks);
        Assert.Equal(400, truck.Cargo);
        Assert.Equal(0, truck.UnloadLeft);
    }

    [Fact]
    public void AnEmptyTruckNeverUnloads()
    {
        var (w, depot) = Yard(300);
        var (truck, _) = w.SpawnCrewed(1, "cargo_truck", 40, 40);
        truck.Cargo = 0;
        w.Order(1, CommandKind.Unload, [truck], depot.Id);
        w.Step();
        Assert.Equal(OrderKind.Idle, truck.Order.Kind);
        var near = w.Spawn(1, "cargo_truck", 23, 21);
        near.Cargo = 0;
        w.Run(UnloadTicks * 2);
        Assert.Equal(300, depot.Stock);
    }

    [Fact]
    public void AnUnloadOrderDrivesIntoTheRadiusAndUnloads()
    {
        var (w, depot) = Yard(300);
        var (truck, _) = w.SpawnCrewed(1, "cargo_truck", 40, 40);
        w.Order(1, CommandKind.Unload, [truck], depot.Id);
        w.Run(600);
        Assert.Equal(700, depot.Stock);
        Assert.Equal(0, truck.Cargo);
        Assert.Equal(OrderKind.Idle, truck.Order.Kind);
        Assert.True(World.Within(depot.Pos, truck.Pos, depot.Def.Supply!.Radius));
    }

    [Fact]
    public void NoUnloadingIntoAnEnemyDepot()
    {
        var (w, depot) = Yard(300);
        var (truck, _) = w.SpawnCrewed(2, "cargo_truck", 23, 21);
        w.Order(2, CommandKind.Unload, [truck], depot.Id);
        w.Run(UnloadTicks * 2);
        Assert.Equal(300, depot.Stock);
        Assert.Equal(400, truck.Cargo);
    }
}
