using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The military garage and the Sentinel cargo truck (the game repo's docs/military-garage-design.md, owner 2026-10-21): 5
// shared doors; production builds behind up to 3 of them, every other door services a vehicle of its side sent there
// (Service): it drives to a free door (first come first served), the door opens, it drives in, is reloaded in 5 s and
// repaired evenly over 10 s (an empty cargo truck refilled in 10 s), then drives out to the rally point and the door
// closes behind it. A damaged garage stops; aircraft, infantry and the other side are refused.
public class MilitaryGarageTests
{
    private const int GX = 20, GY = 10; // the garage's top-left: x 20-59, y 10-27; doors south, exits at y 29.5
    // Vehicles sent from y 36 are within 8 cells of a door's exit: they wait at once, all the same tick (then by ID).

    private static (World W, Unit Garage) Yard(ulong seed = 1)
    {
        var w = Arena.NewSentinel(96, seed);
        var g = w.Spawn(1, "military_garage", GX, GY);
        w.Step(); // its bays and doors
        return (w, g);
    }

    private static void Service(World w, Unit g, IEnumerable<Unit> units, byte player = 1) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.Service, Units = [.. units.Select(u => u.Id)], Target = g.Id });

    private static void Produce(World w, Unit g, string unit) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [g.Id], Unit = unit });

    // Damaged: a crewed vehicle of player 1 at (x, y), at hp.
    private static Unit Damaged(World w, string arch, int x, int y, int hp)
    {
        var v = w.SpawnCrewed(1, arch, x, y, Facing.North).Vehicle;
        v.HP = hp;
        return v;
    }

    private static int Count(Unit g, DoorUse use) => g.Doors.Count(d => d.Use == use);

    // RunUntil steps until done (or fails after max ticks).
    private static void RunUntil(World w, Func<bool> done, int max, string what)
    {
        for (var t = 0; t < max; t++)
        {
            if (done())
            {
                return;
            }
            w.Step();
        }
        Assert.True(done(), $"{what}: not after {max} ticks");
    }

    [Fact]
    public void ItIsData()
    {
        var c = TestContent.Mvp;
        var g = c.Archetype("military_garage")!;
        Assert.Equal((40, 18, 4000, 4000, 1200), (g.FootprintW, g.FootprintH, g.MaxHp, g.Cost, g.BuildTime));
        var pr = g.Production!;
        Assert.Equal((3, 10, 5, 35), (pr.Bays, pr.QueueMax, pr.Doors, pr.MinHpPercent));
        Assert.True(c.Archetype("technical")!.Tags.Matches(pr.Tags));
        Assert.Equal((100, 200, 200), (g.Service!.ReloadTicks, g.Service.RepairTicks, g.Service.CargoTicks));
        Assert.True(c.Archetype("technical")!.Tags.Matches(g.Service.Tags));
        Assert.False(c.Archetype("assault")!.Tags.Matches(g.Service.Tags));
        Assert.Null(c.Archetype("vehicle_garage")!.Service); // the Resistance's garage only produces
        Assert.NotNull(g.Construction);

        var truck = c.Archetype("sentinel_cargo_truck")!;
        var cargo = c.Archetype("cargo_truck")!;
        Assert.Equal(260, truck.MaxHp);
        Assert.True(truck.MaxHp > cargo.MaxHp);
        Assert.Equal(cargo.Speed, truck.Speed);
        Assert.Equal(400, truck.Components.SelectMany(i => c.Components[i].Effects).OfType<CargoEffect>().Single().Amount);
        Assert.True(truck.Tags.Matches(pr.Tags));

        var sentinel = c.Faction("sentinel")!;
        var resistance = c.Faction("resistance")!;
        Assert.True(sentinel.Archetypes[g.Index] && sentinel.Archetypes[truck.Index]);
        Assert.False(sentinel.Archetypes[cargo.Index]); // replaced
        Assert.True(resistance.Archetypes[cargo.Index]);
        Assert.False(resistance.Archetypes[truck.Index] || resistance.Archetypes[g.Index]);
        var army = TestContent.Sentinel(1).Players.Single(p => p.Id == 1).Army.Select(e => e.Unit).ToList();
        Assert.Contains("sentinel_cargo_truck", army);
        Assert.DoesNotContain("cargo_truck", army);

        // A worker or the bulldozer builds it.
        var w = Arena.NewSentinel(96);
        var def = w.Player(1)!.Setup.Find(w.Content, "military_garage")!;
        Assert.True(w.CanPlace(def, new Cell(20, 20), 0, [w.Spawn(1, "worker", 40, 60)]));
        Assert.True(w.CanPlace(def, new Cell(20, 20), 0, [w.Spawn(1, "bulldozer", 40, 62)]));
    }

    [Fact]
    public void BothGaragesBuildTheSentinelCargoTruck()
    {
        var w = Arena.NewSentinel(96);
        var mg = w.Spawn(1, "military_garage", GX, GY);
        var vg = w.Spawn(1, "vehicle_garage", 20, 50);
        Produce(w, mg, "sentinel_cargo_truck");
        Produce(w, vg, "sentinel_cargo_truck");
        w.Run(300 + 30 + 10);
        var made = w.All("sentinel_cargo_truck");
        Assert.Equal(2, made.Count);
        Assert.All(made, t => Assert.Equal(400, t.Cargo));
        Assert.All(made, t => Assert.NotEqual(0u, t.Driver));
    }

    [Fact]
    public void TheSentinelCargoTruckUnloadsIntoADepotAndOnlyTheGarageRefillsIt()
    {
        var (w, g) = Yard();
        var depot = w.Spawn(1, "supply_depot", 70, 60);
        depot.Stock = 300;
        var (truck, _) = w.SpawnCrewed(1, "sentinel_cargo_truck", 60, 50);
        w.Order(1, CommandKind.Unload, [truck], depot.Id);
        w.Run(600);
        Assert.Equal((700, 0), (depot.Stock, truck.Cargo)); // all at once, as the cargo truck does
        w.Run(400);
        Assert.Equal(0, truck.Cargo); // nothing else refills it
        Service(w, g, [truck]);
        RunUntil(w, () => truck.Cargo == 400, 600, "refilled in the garage");
    }

    [Fact]
    public void ThreeBuildWhileTwoDoorsServe()
    {
        var (w, g) = Yard();
        foreach (var _ in Enumerable.Range(0, 4))
        {
            Produce(w, g, "humvee");
        }
        var cars = Enumerable.Range(0, 3).Select(i => Damaged(w, "humvee", 30 + 10 * i, 36, 100)).ToList();
        Service(w, g, cars);
        w.Run(5);
        Assert.Equal(3, g.Bays.Count(b => b.Def >= 0)); // 3 at once, each behind its own door
        Assert.Equal(3, g.Bays.Select(b => b.Door).Where(d => d >= 0).Distinct().Count());
        Assert.Single(g.ProductionQueue);
        Assert.All(g.Bays, b => Assert.Equal(DoorUse.Production, w.DoorOccupantOf(g, b.Door).Use));
        Assert.Equal(3, Count(g, DoorUse.Production));
        Assert.Equal(2, Count(g, DoorUse.Service)); // the other two doors serve
        var waiting = Assert.Single(w.ServiceQueue(g));
        Assert.Equal(cars[2].Id, waiting); // first come (all at once): by ID
        RunUntil(w, () => cars[0].InGarage && cars[1].InGarage, 200, "two in their bays");
        Assert.Equal(3, g.Bays.Count(b => b.Def >= 0));
        Assert.Equal(ServiceStage.Waiting, cars[2].ServiceStage);
        foreach (var car in cars.Take(2))
        {
            var (f, door, use) = w.DoorOf(car);
            Assert.Equal((g.Id, DoorUse.Service), (f, use));
            Assert.Equal(new DoorOccupant(DoorUse.Service, car.Id, -1, -1), w.DoorOccupantOf(g, door));
        }
        RunUntil(w, () => cars.All(c => c.ServiceAt == 0 && !c.InGarage), 800, "all three serviced");
        Assert.All(cars, c => Assert.Equal(c.Def.MaxHp, c.HP));
        RunUntil(w, () => w.All("humvee").Count == 7, 600, "the four built");
    }

    [Fact]
    public void FiveAreServicedWhenNothingBuildsAndASixthWaits()
    {
        var (w, g) = Yard();
        var cars = Enumerable.Range(0, 6).Select(i => Damaged(w, "humvee", 24 + 6 * i, 36, 100)).ToList();
        Service(w, g, cars);
        RunUntil(w, () => cars.Count(c => c.InGarage) == 5, 300, "five in their bays");
        Assert.Equal(5, Count(g, DoorUse.Service));
        Assert.All(g.Doors, d => Assert.Equal(DoorState.Open, d.State));
        var sixth = Assert.Single(cars, c => !c.InGarage);
        Assert.Equal(cars[5], sixth); // the last by ID (they all got there the same tick)
        Assert.Equal(ServiceStage.Waiting, sixth.ServiceStage);
        Assert.Equal([sixth.Id], w.ServiceQueue(g));
        Assert.Equal((g.Id, -1, DoorUse.Service), w.DoorOf(sixth));
        // Enemies can't see or hit what is in the bays.
        Assert.All(cars.Where(c => c.InGarage), c => Assert.False(World.IsVisibleTo(c, 2) || World.IsRevealed(c, 2)));
        RunUntil(w, () => sixth.InGarage, 600, "the sixth in a bay"); // past the first five driving out to the rally
        RunUntil(w, () => cars.All(c => c.ServiceAt == 0), 600, "all six serviced");
        Assert.All(cars, c => Assert.Equal(c.Def.MaxHp, c.HP));
    }

    // Into: a crewed vehicle sent for service and stepped until it is in its bay; returns its door.
    private static int Into(World w, Unit g, Unit v)
    {
        Service(w, g, [v]);
        RunUntil(w, () => v.InGarage, 300, "in its bay");
        return v.ServiceDoor;
    }

    [Fact]
    public void ReloadIn5sAndRepairEvenlyOver10s()
    {
        var (w, g) = Yard();
        var sam = Damaged(w, "sam_vehicle", 40, 40, 80);
        var stock = sam.Def.Weapons[0].Stock;
        var full = sam.Def.StockMax[stock];
        sam.MutableCharges[stock] = 0;
        var door = Into(w, g, sam);
        Assert.Equal(new ServiceProgress(sam.Id, 100, 100, 200, 200, 0, 0), w.ServiceProgressOf(g, door));
        for (var k = 1; k <= 200; k++)
        {
            w.Step();
            Assert.Equal(80 + k, sam.HP); // 80 to 280 over 200 ticks: 1 HP a tick
            Assert.Equal(k >= 100 ? full : 0, sam.Charges[stock]);
            if (k < 200)
            {
                Assert.True(sam.InGarage);
                Assert.Equal((Math.Max(0, 100 - k), 200 - k), (w.ServiceProgressOf(g, door)!.Value.ReloadLeft, w.ServiceProgressOf(g, door)!.Value.RepairLeft));
            }
        }
        Assert.False(sam.InGarage); // done: out it comes
        Assert.Equal(0u, sam.ServiceAt);
    }

    [Fact]
    public void AnEmptyCargoTruckIsRefilledIn10s()
    {
        var (w, g) = Yard();
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 40, 40).Vehicle;
        truck.Cargo = 0;
        var door = Into(w, g, truck);
        Assert.Equal(new ServiceProgress(truck.Id, 0, 0, 0, 0, 200, 200), w.ServiceProgressOf(g, door));
        w.Run(199);
        Assert.Equal(0, truck.Cargo);
        Assert.True(truck.InGarage);
        w.Step();
        Assert.Equal(400, truck.Cargo);
        Assert.False(truck.InGarage);
    }

    [Fact]
    public void ItDrivesOutToTheRallyAndTheDoorCloses()
    {
        var (w, g) = Yard();
        var car = Damaged(w, "humvee", 40, 40, 150);
        var door = Into(w, g, car);
        Assert.Equal(DoorState.Open, g.Doors[door].State);
        RunUntil(w, () => !car.InGarage, 300, "out of its bay");
        Assert.Equal(OrderKind.Move, car.Order.Kind);
        Assert.Equal(w.RallyCell(g), car.Order.Cell);
        Assert.Equal(new Cell(GX + 20, GY + 23), w.RallyCell(g));
        Assert.Equal(g.Id, car.OutOf); // through its garage's footprint
        Assert.Equal((g.Id, door, DoorUse.Service), w.DoorOf(car));
        var exit = World.ExitPoint(g, door);
        var wall = (long)(GY + 18) * Fixed.OneRaw;
        for (var t = 0; t < 200 && car.OutOf != 0; t++)
        {
            w.Step();
            if (car.Pos.Y.Raw < wall)
            {
                Assert.True(Math.Abs(car.Pos.X.Raw - exit.X.Raw) < Fixed.OneRaw, $"tick {t}: {car.Pos} off its lane");
            }
        }
        Assert.Equal(0u, car.OutOf);
        RunUntil(w, () => g.Doors[door].State == DoorState.Closed, 200, "the door closed");
        Assert.Equal(DoorUse.None, g.Doors[door].Use);
        Assert.Equal(new DoorOccupant(DoorUse.None, 0, -1, -1), w.DoorOccupantOf(g, door));
        w.Run(200);
        Assert.True((car.Pos - w.RallyCell(g).Center()).Len() < Fixed.FromInt(3));
    }

    [Fact]
    public void OtherSidesAircraftAndInfantryAreRefused()
    {
        var (w, g) = Yard();
        var theirs = w.SpawnCrewed(2, "technical", 30, 40).Vehicle;
        theirs.HP = 50;
        Service(w, g, [theirs], 2); // onto the other side's garage
        Service(w, g, [theirs]);    // or ordered by this side
        var soldier = w.Spawn(1, "Rifleman", 34, 40);
        soldier.HP = 10;
        var jet = w.Spawn(1, "Multirole 500 lb", 40, 40);
        var drone = w.Spawn(1, "watcher_drone", 44, 40);
        var empty = w.Spawn(1, "humvee", 48, 40); // no driver
        empty.HP = 50;
        var other = w.Spawn(1, "vehicle_garage", 20, 60);
        Service(w, g, [soldier, jet, drone, empty, other]);
        var mine = Damaged(w, "humvee", 52, 40, 50);
        Service(w, other, [mine]); // the vehicle garage services nothing
        w.Run(3);
        foreach (var u in new[] { theirs, soldier, jet, drone, empty, other, mine })
        {
            Assert.Equal(0u, u.ServiceAt);
            Assert.NotEqual(OrderKind.Service, u.Order.Kind);
        }
        Assert.Equal(0, Count(g, DoorUse.Service));
    }

    [Fact]
    public void ADamagedGarageStopsServiceAndLetsItsVehiclesOut()
    {
        var (w, g) = Yard();
        var cars = Enumerable.Range(0, 6).Select(i => Damaged(w, "humvee", 24 + 6 * i, 36, 100)).ToList();
        Service(w, g, cars);
        RunUntil(w, () => cars.Count(c => c.InGarage) == 5, 300, "five in their bays");
        g.HP = g.Def.MaxHp * 30 / 100; // below 35%: its doors are wreckage
        w.Step();
        Assert.All(cars, c => Assert.Equal(0u, c.ServiceAt));
        Assert.All(cars, c => Assert.False(c.InGarage));
        Assert.All(cars.Take(5), c => Assert.Equal((OrderKind.Move, w.RallyCell(g), g.Id), (c.Order.Kind, c.Order.Cell, c.OutOf)));
        Assert.Equal(OrderKind.Idle, cars[5].Order.Kind); // the one waiting: its order ends
        Assert.All(cars.Take(5), c => Assert.True(c.HP < c.Def.MaxHp)); // not finished
        Service(w, g, cars);
        w.Run(3);
        Assert.All(cars, c => Assert.Equal(0u, c.ServiceAt)); // refused while it is down
        w.Run(300);
        Assert.All(cars.Take(5), c => Assert.True(c.Pos.Y.Raw > (long)(GY + 18) * Fixed.OneRaw, $"{c.Id} at {c.Pos}")); // they got out
    }

    [Fact]
    public void AStopInTheDoorwaySendsItBackOut()
    {
        var (w, g) = Yard();
        var car = Damaged(w, "humvee", 40, 40, 100);
        Service(w, g, [car]);
        RunUntil(w, () => car.ServiceStage == ServiceStage.DriveIn, 300, "driving in");
        var door = car.ServiceDoor;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Stop, Units = [car.Id] });
        w.Step();
        Assert.Equal(0u, car.ServiceAt);
        Assert.Equal((OrderKind.Move, w.RallyCell(g)), (car.Order.Kind, car.Order.Cell));
        RunUntil(w, () => g.Doors[door].State == DoorState.Closed && car.OutOf == 0, 400, "out, the door shut");
        Assert.Equal(DoorUse.None, g.Doors[door].Use);
    }

    // Play: six damaged vehicles (spawned in order or reversed) sent at once; the hashes of every tick, and the order the
    // vehicles (by spawn position) came into their bays.
    private static (List<ulong> Hashes, List<int> Order) Play(bool reversed)
    {
        var (w, g) = Yard(5);
        var xs = Enumerable.Range(0, 6).Select(i => 24 + 6 * i).ToList();
        if (reversed)
        {
            xs.Reverse();
        }
        var cars = xs.Select(x => Damaged(w, "humvee", x, 36, 100)).ToList();
        Produce(w, g, "humvee");
        Service(w, g, cars);
        var hashes = new List<ulong>();
        var order = new List<int>();
        for (var t = 0; t < 900; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
            foreach (var c in cars.OrderBy(c => c.Id))
            {
                if (c.InGarage && !order.Contains(xs[cars.IndexOf(c)]))
                {
                    order.Add(xs[cars.IndexOf(c)]);
                }
            }
        }
        Assert.All(cars, c => Assert.Equal(c.Def.MaxHp, c.HP));
        return (hashes, order);
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var a = Play(false);
        Assert.Equal(a.Hashes, Play(false).Hashes);
        var r = Play(true);
        Assert.Equal(6, a.Order.Count);
        Assert.Equal(6, r.Order.Count);
        // Doors go by arrival tick, then ID: with the spawn order reversed the IDs are, so the whole order is mirrored (the
        // Humvees, faster than the technicals before them, get the last door at the second from the end: 48, mirrored 30).
        Assert.Equal(a.Order.Select(x => 24 + (24 + 6 * 5) - x), r.Order);
        Assert.Equal(48, a.Order[^1]);
        Assert.Equal(30, r.Order[^1]);
    }
}
