using SimCore.Bots;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// The spatial index must never change a result: vision, target acquisition, splash and
// projectile hits give the same world, tick for tick, as the full scans they replace
// (World.FullScan). Ties break by unit ID exactly as before.
public class SpatialIndexTests
{
    private static readonly string[] Infantry =
        ["Rifleman", "Gunner", "Raider", "Sapper", "Drone pilot", "Mechanic", "Tank hunter", "Drone hunter", "Marksman", "Skirmisher", "worker"];

    private static readonly string[] Vehicles =
        ["technical", "dirt_bike", "light_rocket_truck", "medium_rocket_launcher", "heavy_rocket_launcher", "missile_truck", "cargo_truck"];

    // RandomWorld fills an open map with both armies mixed at random (infantry, crewed
    // vehicles, depots, and neutral buildings some soldiers are sent into), so every
    // system meets dense, overlapping crowds, structures and garrisons.
    private static World RandomWorld(int seed, int size, int count, bool fullScan)
    {
        var w = Arena.New(size, (ulong)seed);
        w.FullScan = fullScan;
        var r = new System.Random(seed);
        w.Spawn(1, "supply_depot", 2, 2);
        w.Spawn(2, "supply_depot", size - 6, size - 6);
        var brick = w.Spawn(1, "block_brick_4", size / 2 - 7, size / 2 - 12);
        var panel = w.Spawn(1, "block_panel_5", size / 2 - 11, size / 2 + 6);
        for (var i = 0; i < count; i++)
        {
            var player = (byte)(1 + r.Next(2));
            int x = r.Next(size), y = r.Next(size);
            if (!w.Grid.Passable(new Cell(x, y)))
            {
                continue;
            }
            if (r.Next(8) == 0)
            {
                w.SpawnCrewed(player, Vehicles[r.Next(Vehicles.Length)], x, y, r.Next(32));
                continue;
            }
            var s = w.Spawn(player, Infantry[r.Next(Infantry.Length)], x, y, r.Next(32));
            if (r.Next(10) == 0)
            {
                w.Order(player, CommandKind.Enter, [s], r.Next(2) == 0 ? brick.Id : panel.Id);
            }
        }
        return w;
    }

    // Twins runs the same world with and without the index, bots giving orders on both
    // sides, and checks the hashes agree after every tick.
    private static void Twins(int seed, int size, int count, int ticks)
    {
        var a = RandomWorld(seed, size, count, fullScan: false);
        var b = RandomWorld(seed, size, count, fullScan: true);
        Assert.Equal(b.Hash(), a.Hash());
        var botsA = a.Players.Select(p => new Bot(p.Id, (ulong)seed + p.Id) { Every = 20 }).ToList();
        var botsB = b.Players.Select(p => new Bot(p.Id, (ulong)seed + p.Id) { Every = 20 }).ToList();
        for (var t = 0; t < ticks; t++)
        {
            for (var i = 0; i < botsA.Count; i++)
            {
                foreach (var c in botsA[i].Think(a))
                {
                    a.Issue(c);
                }
                foreach (var c in botsB[i].Think(b))
                {
                    b.Issue(c);
                }
            }
            a.Step();
            b.Step();
            Assert.True(a.Hash() == b.Hash(), $"seed {seed}: index and full scan differ at tick {a.Tick}");
        }
        Assert.True(a.Units.Count > 0);
    }

    [Theory]
    [InlineData(1, 64, 220, 600)]
    [InlineData(2, 48, 300, 500)]
    [InlineData(3, 96, 400, 500)]
    [InlineData(4, 40, 160, 800)]
    public void IndexMatchesFullScanOnRandomWorlds(int seed, int size, int count, int ticks) => Twins(seed, size, count, ticks);

    // The golden battle, replayed both ways, agrees on every tick (the golden test itself
    // only checks every 30 s).
    [Fact]
    public void GoldenReplayMatchesFullScanEveryTick()
    {
        var r = Replay.Load(TestPaths.TestData("battle_seed42.json"));
        var a = r.Start(TestContent.Mvp);
        var b = r.Start(TestContent.Mvp);
        b.FullScan = true;
        while (a.Tick < r.Ticks)
        {
            a.Step();
            b.Step();
            Assert.True(a.Hash() == b.Hash(), $"index and full scan differ at tick {a.Tick}");
        }
    }

    // A query returns every unit whose extent overlaps the box (a structure by its
    // footprint, anyone else by position, also off the map), in ascending ID order
    // without repeats; the unordered query finds the same set.
    [Fact]
    public void QueryFindsEveryOverlappingUnitInIdOrder()
    {
        var w = Arena.New(70, 5);
        var r = new System.Random(5);
        w.Spawn(1, "block_panel_5", 20, 30);
        w.Spawn(2, "supply_depot", 50, 8);
        for (var i = 0; i < 400; i++)
        {
            w.Spawn((byte)(1 + r.Next(2)), Infantry[r.Next(Infantry.Length)], r.Next(-4, 74), r.Next(-4, 74));
        }
        var units = w.Units.ToList();
        var index = new SpatialIndex();
        index.Build(units, w.Grid);
        var into = new List<Unit>();
        var unordered = new List<Unit>();
        for (var q = 0; q < 2000; q++)
        {
            long x0 = r.Next(-10, 80) * Fixed.OneRaw + r.Next(1024), y0 = r.Next(-10, 80) * Fixed.OneRaw + r.Next(1024);
            long x1 = x0 + r.Next(0, 30) * Fixed.OneRaw + r.Next(1024), y1 = y0 + r.Next(0, 30) * Fixed.OneRaw + r.Next(1024);
            index.Query(x0, y0, x1, y1, into);
            index.QueryUnordered(x0, y0, x1, y1, unordered);
            for (var i = 1; i < into.Count; i++)
            {
                Assert.True(into[i - 1].Id < into[i].Id, "not in ascending ID order, or repeated");
            }
            Assert.Equal(into.Select(u => u.Id), unordered.Select(u => u.Id).Order().Distinct());
            foreach (var u in units)
            {
                SpatialIndex.Extent(u, out var ux0, out var uy0, out var ux1, out var uy1);
                if (ux0 <= x1 && ux1 >= x0 && uy0 <= y1 && uy1 >= y0)
                {
                    Assert.Contains(u, into);
                }
            }
        }
    }
}
