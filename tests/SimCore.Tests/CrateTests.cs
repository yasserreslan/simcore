using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Supply crates (CRATE-ENGINE; docs/design.md, decisions 1099 to 1106; decisions 1099 to 1105): cargo trucks drop their load anywhere as a
// small depot; the depot's bigger, simpler numbers.
public class CrateTests
{
    private const int Tps = World.TicksPerSecond;
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static TerrainMap Flat(int n, Func<int, int, int> forest)
    {
        var flags = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                flags[y * n + x] = (byte)forest(x, y);
            }
        }
        return new TerrainMap(n, n, new int[(n + 1) * (n + 1)], flags, Rules);
    }

    private static Unit Crate(World w, byte owner, int x, int y, int stock)
    {
        var c = w.Spawn(owner, World.CrateArchetype, x, y);
        c.Stock = stock;
        return c;
    }

    // Runs until done (or max ticks), collecting the logistics events and order refusals; returns the ticks run.
    private static int RunUntil(World w, Func<bool> done, int max, List<LogisticsEvent>? ev = null, List<OrderRefusal>? refused = null)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            ev?.AddRange(w.LogisticsEvents);
            refused?.AddRange(w.OrderRefusals);
            if (done())
            {
                return t;
            }
        }
        return -1;
    }

    private static long D2(Vec a, Vec b)
    {
        long dx = (a.X.Raw - b.X.Raw) / 64, dy = (a.Y.Raw - b.Y.Raw) / 64;
        return dx * dx + dy * dy;
    }

    private static int Cells(Vec a, Vec b) => (int)Math.Sqrt(D2(a, b)) * 64 / (int)Fixed.OneRaw;

    [Fact]
    public void ATruckDropsItsWholeLoadAsACrateAtItsTailInTheOpen()
    {
        var w = Arena.New(64);
        var (t, _) = w.SpawnCrewed(1, "cargo_truck", 10, 32, Facing.East);
        Assert.Equal(2000, t.Cargo); // it spawns full: 2,000 (decision 1099)
        Assert.Equal(new Cell(30, 32), w.CrateDropPoint(t, new Cell(30, 32))); // open ground: the point itself
        w.Order(1, CommandKind.DropCrate, [t], cell: new Cell(30, 32));
        var ev = new List<LogisticsEvent>();
        Assert.True(RunUntil(w, () => w.All(World.CrateArchetype).Count > 0, 40 * Tps, ev) > 0);
        var crate = w.All(World.CrateArchetype).Single();
        Assert.Equal((1, 2000, 0, OrderKind.Idle), (crate.Owner, crate.Stock, t.Cargo, t.Order.Kind));
        Assert.Contains(ev, e => e.Kind == LogisticsEventKind.CrateDropped && e.Unit == t.Id && e.Store == crate.Id && e.Amount == 2000);
        Assert.InRange(Cells(crate.Pos, new Cell(30, 32).Center()), 0, 4);
        var dir = Facing.Dir(t.Facing);
        Assert.True((crate.Pos.X.Raw - t.Pos.X.Raw) * dir.X.Raw + (crate.Pos.Y.Raw - t.Pos.Y.Raw) * dir.Y.Raw < 0); // behind it
        Assert.Equal(0, crate.Def.Archetype.FootprintW); // no footprint: it blocks nothing
        Assert.False(crate.Def.IsStructure);
        Assert.Equal((150, true), (crate.Def.MaxHp, crate.Def.Supply!.Crate));
    }

    [Fact]
    public void ADropOnATreeInAWoodUsesTheNearestCellTheTruckStandsOn()
    {
        const int n = 64;
        var w = Arena.NewSentinel(n);
        var g = Flat(n, (x, y) => x is >= 20 and < 44 && y is >= 20 and < 44 ? 2 : 0);
        w.AttachTerrain(g);
        var tree = Enumerable.Range(28, 8).SelectMany(y => Enumerable.Range(28, 8).Select(x => new Cell(x, y))).First(g.HasTrunk);
        var (t, _) = w.SpawnCrewed(1, "sentinel_cargo_truck", 8, 32, Facing.East);
        var spot = w.CrateDropPoint(t, tree);
        Assert.NotNull(spot);
        Assert.NotEqual(tree, spot);
        Assert.False(g.HasTrunk(spot!.Value)); // between the trees
        Assert.Equal(2, g.Forest(spot.Value)); // still in the wood
        Assert.InRange(Math.Max(Math.Abs(spot.Value.X - tree.X), Math.Abs(spot.Value.Y - tree.Y)), 1, 2);
        w.Order(1, CommandKind.DropCrate, [t], cell: tree);
        w.Step();
        Assert.Equal((OrderKind.DropCrate, spot.Value), (t.Order.Kind, t.Order.Cell));
        Assert.True(RunUntil(w, () => w.All(World.CrateArchetype).Count > 0, 60 * Tps) > 0);
        Assert.InRange(Cells(w.All(World.CrateArchetype).Single().Pos, spot.Value.Center()), 0, 5);
        Assert.Equal(0, t.Cargo);
    }

    [Fact]
    public void SeveralTrucksSpreadAnEmptyOneIsRefusedAndMovingOffCancels()
    {
        var w = Arena.New(64);
        var trucks = Enumerable.Range(0, 3).Select(i => w.SpawnCrewed(1, "cargo_truck", 8, 26 + 6 * i, Facing.East).Vehicle).ToList();
        var (empty, _) = w.SpawnCrewed(1, "cargo_truck", 8, 50, Facing.East);
        empty.Cargo = 0;
        var refused = new List<OrderRefusal>();
        w.Order(1, CommandKind.DropCrate, [.. trucks, empty], cell: new Cell(36, 32));
        Assert.True(RunUntil(w, () => w.All(World.CrateArchetype).Count == 3, 60 * Tps, null, refused) > 0);
        Assert.Contains(refused, r => r.Aircraft == empty.Id && r.Reason == OrderRefusalReason.NoCargo);
        var crates = w.All(World.CrateArchetype);
        for (var i = 0; i < crates.Count; i++)
        {
            for (var j = i + 1; j < crates.Count; j++)
            {
                Assert.True(Cells(crates[i].Pos, crates[j].Pos) >= 2, "crates spread round the point");
            }
        }
        Assert.All(trucks, t => Assert.Equal(0, t.Cargo));
        Assert.Equal(OrderKind.Idle, empty.Order.Kind);

        // Moving off before the drop cancels it: no crate, the load stays aboard.
        var (mover, _) = w.SpawnCrewed(1, "cargo_truck", 50, 10, Facing.East);
        w.Order(1, CommandKind.DropCrate, [mover], cell: new Cell(54, 10));
        Assert.True(RunUntil(w, () => mover.UnloadLeft > 0, 20 * Tps) > 0);
        w.Order(1, CommandKind.Move, [mover], cell: new Cell(50, 20));
        w.Run(10 * Tps);
        Assert.Equal((3, 2000), (w.All(World.CrateArchetype).Count, mover.Cargo));
    }

    [Fact]
    public void AtACrateATankInfantryAndARocketTruckRefillWithinTwentySeconds()
    {
        var s = Arena.NewSentinel(64);
        var crateS = Crate(s, 1, 30, 30, 2000);
        var tank = s.SpawnCrewed(1, "main_battle_tank", 36, 30).Vehicle;
        Array.Clear(tank.MutableCharges);
        var r = Arena.New(64);
        var crateR = Crate(r, 1, 30, 30, 2000);
        var rifleman = r.Spawn(1, "Rifleman", 33, 33);
        var rockets = r.SpawnCrewed(1, "heavy_rocket_truck", 24, 30).Vehicle;
        Array.Clear(rifleman.MutableCharges);
        Array.Clear(rockets.MutableCharges);
        foreach (var u in s.Units.Concat(r.Units))
        {
            u.Stance = Stance.HoldFire;
        }
        bool Full(Unit u) => Enumerable.Range(0, u.Def.StockMax.Count).All(i => u.Charges[i] == u.StockMax(i));
        var ts = RunUntil(s, () => Full(tank), 60 * Tps);
        var tr = RunUntil(r, () => Full(rifleman) && Full(rockets), 60 * Tps);
        Assert.InRange(ts, 1, 20 * Tps + 1); // 40 rounds: was 80 s at 1 every 2 s
        Assert.InRange(tr, 1, 20 * Tps + 1); // 12 rockets, depot-reloaded round by round: was 60 s
        Assert.Equal(2000 - 46, crateS.Stock); // 40 rounds and 6 charges, a supply each
        Assert.Equal(2000 - 6 - 12 * 40, crateR.Stock); // the rifleman's 6, the rockets at their own 40 each
    }

    [Fact]
    public void AnEnemyAndAnAircraftBesideTheCrateGetNothing()
    {
        var w = Arena.NewSentinel(64);
        var crate = Crate(w, 1, 30, 30, 2000);
        var enemy = w.SpawnCrewed(2, "heavy_rocket_truck", 34, 30).Vehicle;
        var heli = w.Player(1)!.Setup.Defaults.First(d => d is { Archetype.Flight.Rotor: not null, StockMax.Count: > 0 })!;
        var air = w.Unit(w.Spawn(heli, new Cell(30, 34), Facing.North))!;
        Array.Clear(enemy.MutableCharges);
        Array.Clear(air.MutableCharges);
        foreach (var u in w.Units)
        {
            u.Stance = Stance.HoldFire;
        }
        w.Run(30 * Tps);
        Assert.All(enemy.Charges, c => Assert.Equal(0, c));
        Assert.All(air.Charges, c => Assert.Equal(0, c));
        Assert.Equal(2000, crate.Stock);
    }

    [Fact]
    public void ACrateEmptiesAndGoesATruckTopsItUpAndFireDestroysIt()
    {
        var w = Arena.New(64);
        var small = Crate(w, 1, 10, 10, 5);
        var inf = w.Spawn(1, "Rifleman", 12, 12);
        inf.Stance = Stance.HoldFire;
        Array.Clear(inf.MutableCharges);
        var ev = new List<LogisticsEvent>();
        Assert.True(RunUntil(w, () => small.Stock == 0, 30 * Tps, ev) > 0);
        w.Step();
        ev.AddRange(w.LogisticsEvents);
        Assert.Contains(ev, e => e.Kind == LogisticsEventKind.CrateEmpty && e.Store == small.Id);
        Assert.True(small.Alive);
        var gone = RunUntil(w, () => !small.Alive, 20 * Tps);
        Assert.InRange(gone, World.CrateEmptyTicks - 2, World.CrateEmptyTicks + 2); // 10 s after it ran empty

        // A truck tops a crate up (Unload aimed at it), as a depot.
        var crate = Crate(w, 1, 40, 40, 100);
        var (t, _) = w.SpawnCrewed(1, "cargo_truck", 30, 40, Facing.East);
        w.Order(1, CommandKind.Unload, [t], target: crate.Id);
        Assert.True(RunUntil(w, () => t.Cargo == 0, 30 * Tps) > 0);
        Assert.Equal(2100, crate.Stock);

        // Fire burns it: 10% of its HP a second.
        var f = Arena.NewSentinel(64);
        f.AttachTerrain(Flat(64, (x, y) => x is >= 20 and < 40 && y is >= 20 and < 40 ? 2 : 0));
        var burning = Crate(f, 1, 30, 30, 2000);
        Assert.True(f.IgniteCell(Cell.Of(burning.Pos), 0));
        Assert.True(RunUntil(f, () => !burning.Alive, 30 * Tps) > 0);
    }

    [Fact]
    public void AWorkerBuiltDepotStartsFullAndAnEmptyTruckLoadsAtADepot()
    {
        var w = Arena.New(64);
        var depot = w.Spawn(1, "supply_depot", 20, 20);
        var sup = depot.Def.Supply!;
        Assert.Equal((10000, 10000, 10000, 120), (depot.Stock, sup.Stock, sup.ConstructedStock, sup.Radius.Raw / (int)Fixed.OneRaw * 10));
        var (t, _) = w.SpawnCrewed(1, "cargo_truck", 40, 23, Facing.West);
        t.Cargo = 0;
        w.Order(1, CommandKind.LoadCargo, [t], target: depot.Id);
        Assert.True(RunUntil(w, () => t.Cargo == 2000, 30 * Tps) > 0);
        Assert.Equal(8000, depot.Stock);
        w.Run(20 * Tps); // idle in its radius: it does not unload back into the depot it loaded at
        Assert.Equal((2000, 8000), (t.Cargo, depot.Stock));
        w.Order(1, CommandKind.Unload, [t], target: depot.Id); // unless told to
        Assert.True(RunUntil(w, () => t.Cargo == 0, 30 * Tps) > 0);
        Assert.Equal(10000, depot.Stock);
    }

    [Fact]
    public void TheCratesStateIsHashed()
    {
        World Make()
        {
            var w = Arena.New(32);
            Crate(w, 1, 10, 10, 2000);
            w.Step();
            return w;
        }
        var a = Make();
        var b = Make();
        Assert.Equal(a.Hash(), b.Hash());
        b.All(World.CrateArchetype)[0].Stock = 1999;
        Assert.NotEqual(a.Hash(), b.Hash());
        var c = Make();
        c.All(World.CrateArchetype)[0].Lifetime = 7;
        Assert.NotEqual(a.Hash(), c.Hash());
    }
}
