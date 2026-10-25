using SimCore.Rng;
using SimCore.Sim;

namespace SimCore.Tests;

public class RoutingReachTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RefreshContainsEveryCostChangeAndMatchesFreshBuild(int seed)
    {
        const int size = 48;
        var r = new Rand((ulong)seed);
        var grid = new Grid(size, size);
        for (var i = 0; i < 150; i++) { grid.Set(new Cell(r.Intn(size), r.Intn(size)), Terrain.Blocked); }
        var nav = new NavGrid(grid, null);
        for (var width = 1; width <= NavGrid.MaxWidth; width++) { nav.Class(0, width); }
        for (var trial = 0; trial < 60; trial++)
        {
            var c = new Cell(r.Intn(size), r.Intn(size));
            var before = Enumerable.Range(0, NavGrid.MaxWidth)
                .Select(k => Enumerable.Range(0, size * size).Select(i => nav.Cost(k, i)).ToArray()).ToArray();
            // Exercise block, unblock, structure appearance and removal independently.
            for (var phase = 0; phase < 4; phase++)
            {
                if (phase < 2) { grid.Set(c, phase == 0 ? Terrain.Blocked : Terrain.Clear); }
                else { nav.Occupy(c.X, c.Y, 1, 1, phase == 2 ? 1 : -1); }
                var rect = nav.Refresh(c.X, c.Y, c.X, c.Y);
                var fresh = new NavGrid(grid, null);
                if (phase == 2) { fresh.Occupy(c.X, c.Y, 1, 1, 1); fresh.Refresh(c.X, c.Y, c.X, c.Y); }
                for (var width = 1; width <= NavGrid.MaxWidth; width++)
                {
                    var k = fresh.Class(0, width);
                    for (var i = 0; i < size * size; i++)
                    {
                        Assert.Equal(fresh.Cost(k, i), nav.Cost(k, i));
                        if (before[k][i] != fresh.Cost(k, i))
                        {
                            Assert.InRange(i % size, rect.X0, rect.X1);
                            Assert.InRange(i / size, rect.Y0, rect.Y1);
                        }
                        before[k][i] = fresh.Cost(k, i);
                    }
                }
            }
        }
        // The conservative formula remains sufficient for odd AND even future widths.
        for (var max = 1; max <= 32; max++)
        {
            for (var width = 1; width <= max; width++)
            {
                var off = width % 2 == 1 ? (width - 1) / 2 : width / 2 - 1;
                Assert.True(off <= max - 1 && width - 1 - off <= max - 1);
            }
        }
        Assert.Equal((20 - (NavGrid.MaxWidth - 1), 20 - (NavGrid.MaxWidth - 1),
            20 + NavGrid.MaxWidth - 1, 20 + NavGrid.MaxWidth - 1), nav.Around(20, 20, 20, 20));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void CachedPartnersMatchFreshLookupAfterBorderChanges(int width)
    {
        var grid = new Grid(96, 96);
        var nav = new NavGrid(grid, null);
        var k = nav.Class(0, width);
        var hpa = new Hpa(nav, k, new LocalSearch());
        hpa.BuildAll();
        void Check(bool fill)
        {
            var fresh = new Hpa(nav, k, new LocalSearch());
            fresh.BuildAll();
            for (var s = 0; s < hpa.SX * hpa.SY; s++)
            {
                if (!hpa.IsBuilt(s)) { continue; }
                var sector = hpa.Get(s);
                Assert.Equal(fresh.Get(s).Nodes, sector.Nodes);
                for (var n = 0; n < sector.Nodes.Length; n++)
                {
                    if (fill || sector.Partner[n] != -2)
                    {
                        Assert.Equal(fresh.Partner(s, n), hpa.Partner(s, n));
                    }
                }
            }
        }
        Check(true); // populate all caches before invalidating, not just fresh caches afterwards
        foreach (var c in new[] { new Cell(32, 30), new Cell(63, 64), new Cell(31, 31), new Cell(64, 63) })
        {
            foreach (var terrain in new[] { Terrain.Blocked, Terrain.Clear })
            {
                grid.Set(c, terrain);
                var rect = nav.Refresh(c.X, c.Y, c.X, c.Y);
                hpa.Invalidate(rect.X0, rect.Y0, rect.X1, rect.Y1);
                // Invalid sectors can still hold old arrays but are never read before rebuild.
                hpa.BuildAll();
                Check(false);
                Check(true);
            }
        }
    }
}
