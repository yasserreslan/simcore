using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;
using SimCore.Sim;

namespace SimCore.Tests;

public class SupplyIndexTests
{
    // Restocking makes duplicate structure candidates observable as extra stock transfers.
    private static readonly Lazy<ContentSet> Restocking = new(() => ContentLoader.Load(TestContent.MvpSources().Select(s =>
        s.Name == "components/kits.json" ? s with { Json = s.Json.Replace("\"restock\": 0", "\"restock\": 7") } : s)));

    private static World WorldWithRestock(int seed)
    {
        var c = Restocking.Value;
        var setup = TestContent.Battle((ulong)seed).For(c, (ulong)seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(128, 128), MatchCompiler.Compile(c, setup));
    }

    private static World RandomWorld(int seed, int suppliers, bool full)
    {
        var w = WorldWithRestock(seed);
        w.FullSupplyScan = full;
        var r = new Rand((ulong)seed);
        for (var i = 0; i < suppliers; i++)
        {
            var d = w.Spawn((byte)(1 + r.Intn(2)), "supply_depot", r.Range(6, 115), r.Range(6, 115));
            d.Stock = r.Intn(40);
            if (suppliers > 8 && i % 17 == 0) { d.Construction = ConstructionState.Building; }
            if (suppliers > 8 && i % 19 == 0) { d.HP = 0; }
        }
        for (var i = 0; i < 240; i++)
        {
            var owner = (byte)(1 + r.Intn(2));
            var u = i % 6 == 0
                ? w.SpawnCrewed(owner, "heavy_rocket_launcher", r.Range(2, 125), r.Range(2, 125)).Vehicle
                : w.Spawn(owner, i % 3 == 0 ? "Drone pilot" : "Sapper", r.Range(2, 125), r.Range(2, 125));
            Array.Clear(u.MutableCharges);
            w.Order(owner, CommandKind.Move, [u], cell: new Cell(r.Range(2, 125), r.Range(2, 125)));
        }
        foreach (var u in w.Units) { u.Stance = Stance.HoldFire; }
        return w;
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 7)]
    [InlineData(4, 8)]
    [InlineData(1, 100)]
    [InlineData(2, 100)]
    [InlineData(3, 100)]
    [InlineData(4, 100)]
    public void CandidatePathsMatchFullScansEveryTick(int seed, int suppliers)
    {
        var a = RandomWorld(seed, suppliers, false);
        var b = RandomWorld(seed, suppliers, true);
        Assert.Equal(b.Hash(), a.Hash());
        for (var t = 0; t < 300; t++)
        {
            foreach (var w in new[] { a, b })
            {
                if (t == 80) { w.Spawn(1, "supply_depot", 50, 50).Stock = 30; }
                if (t == 120 && suppliers > 0) { w.Units.First(u => u.Def.Supply is not null).HP = 0; }
                if (t == 160)
                {
                    foreach (var site in w.Units.Where(u => !u.IsBuilt)) { site.Construction = ConstructionState.Complete; }
                }
            }
            a.Step(); b.Step();
            Assert.True(a.Hash() == b.Hash(), $"seed {seed}, sources {suppliers}, tick {a.Tick}");
        }
    }

    private static void RemoteDepots(World w)
    {
        // Seven active suppliers force the indexed path alongside the local supplier.
        for (var i = 0; i < 7; i++) { w.Spawn(1, "supply_depot", 90 + i % 3 * 7, 90 + i / 3 * 7).Stock = 0; }
    }

    [Fact]
    public void LastChargeGoesToLowestRecipientId()
    {
        var w = Arena.New(128);
        var depot = w.Spawn(1, "supply_depot", 5, 5);
        depot.Stock = 1;
        RemoteDepots(w);
        var first = w.Spawn(1, "Sapper", 10, 9);
        var second = w.Spawn(1, "Sapper", 9, 10);
        Array.Clear(first.MutableCharges); Array.Clear(second.MutableCharges);
        w.SupplyForTests();
        Assert.Equal(1, first.Charges[0]);
        Assert.Equal(0, second.Charges[0]);
        Assert.Equal(0, depot.Stock);
    }

    [Fact]
    public void StructureAcrossBucketsReceivesRestockOnce()
    {
        var w = WorldWithRestock(1);
        var source = w.Spawn(1, "supply_depot", 2, 2);
        source.Stock = 100;
        var target = w.Spawn(1, "supply_depot", 6, 6); // footprint crosses four buckets
        target.Stock = 0;
        target.Construction = ConstructionState.Building; // receives stock, but cannot give it back
        RemoteDepots(w);
        w.SupplyForTests();
        Assert.Equal(7, target.Stock);
        Assert.Equal(93, source.Stock);
    }

    [Fact]
    public void ExactRadiusBoundaryIsIncludedAndNextRawUnitIsExcluded()
    {
        var w = Arena.New(128);
        var d = w.Spawn(1, "supply_depot", 5, 5);
        RemoteDepots(w);
        var on = w.Spawn(1, "Sapper", 16, 8);
        var beyond = w.Spawn(1, "Sapper", 16, 8);
        on.Pos = d.Pos + new Vec(d.Def.Supply!.Radius, Fixed.Zero);
        beyond.Pos = on.Pos + new Vec(Fixed.FromRaw(1), Fixed.Zero);
        Array.Clear(on.MutableCharges); Array.Clear(beyond.MutableCharges);
        w.SupplyForTests();
        Assert.Equal(1, on.Charges[0]);
        Assert.Equal(0, beyond.Charges[0]);
    }

    [Fact]
    public void FreshWindowFindsNewUnitsAndDropsOldPositions()
    {
        var w = Arena.New(128);
        w.Spawn(1, "supply_depot", 5, 5);
        RemoteDepots(w);
        w.SupplyForTests();
        var added = w.Spawn(1, "Rifleman", 12, 8);
        w.SupplyForTests();
        Assert.Equal(20, added.ReloadBonus);
        added.Pos = new Cell(50, 50).Center();
        w.SupplyForTests();
        Assert.Equal(0, added.ReloadBonus);
        w.Spawn(1, "supply_depot", 46, 46);
        w.SupplyForTests();
        Assert.Equal(20, added.ReloadBonus);
    }

    [Fact]
    public void GroundOnlyStorageAndUnfinishedSourcesKeepTheirFilters()
    {
        var content = ContentLoader.Load(TestContent.MvpSources().Select(s => s.Name == "components/logistics.json"
            ? s with { Json = s.Json.Replace("\"reloadBonus\": 0", "\"reloadBonus\": 20") } : s));
        var setup = TestContent.Sentinel(1).For(content, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = new World(new Grid(128, 128), MatchCompiler.Compile(content, setup));
        var ammo = w.Spawn(1, "facp", 10, 10);
        RemoteDepots(w);
        w.Spawn(1, "supply_depot", 115, 115).Stock = 0; // keep eight active sources after disabling ammo
        var ground = w.Spawn(1, "Rifleman", 18, 14);
        var air = w.Spawn(1, "overseer_drone", 18, 14);
        w.SupplyForTests();
        Assert.True(ground.ReloadBonus > 0);
        Assert.Equal(0, air.ReloadBonus);
        ammo.Construction = ConstructionState.Building;
        w.Spawn(1, "supply_stash", 10, 10);
        w.SupplyForTests();
        Assert.Equal(0, ground.ReloadBonus);
        Assert.Equal(0, air.ReloadBonus);
    }
}
