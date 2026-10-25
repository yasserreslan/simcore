using SimCore.Sim;

namespace SimCore.Tests;

public class RoutingLevelTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LazyBordersKeepExactLegacyPaths(int seed)
    {
        const int size = 224;
        var grid = new Grid(size, size);
        var rng = new Random(seed);
        for (var i = 0; i < 900; i++)
        {
            int x = rng.Next(size), y = rng.Next(size);
            for (var dx = 0; dx < 3; dx++)
            {
                for (var dy = 0; dy < 3; dy++)
                {
                    if (x + dx < size && y + dy < size) { grid.Set(new Cell(x + dx, y + dy), Terrain.Blocked); }
                }
            }
        }
        var actual = new Router(grid, null, []);
        var legacy = new Router(grid, null, []) { LegacyRouting = true };
        var k = actual.Nav.Class(0, 1);
        Assert.Equal(k, legacy.Nav.Class(0, 1));
        for (var i = 0; i < 24; i++)
        {
            var a = actual.NearestPassable(k, new Cell(rng.Next(size), rng.Next(size)), 16);
            var b = actual.NearestPassable(k, new Cell(rng.Next(size), rng.Next(size)), 16);
            Assert.True(a >= 0 && b >= 0);
            Assert.Equal(Route(legacy, k, a, b), Route(actual, k, a, b));
        }
        // Existing caches also survive a changed border (node numbering and partners can change).
        grid.Set(new Cell(32, 32), Terrain.Blocked);
        actual.DrainChanges();
        legacy.Nav.Refresh(32, 32, 32, 32);
        legacy.Graph(k).Invalidate(28, 28, 36, 36);
        legacy.RegionsOf(k).Invalidate(28, 28, 36, 36);
        Assert.Equal(Route(legacy, k, 1, size * size - 2), Route(actual, k, 1, size * size - 2));
    }

    private static int[] Route(Router router, int k, int a, int b)
    {
        var path = new List<int>();
        var route = new List<int>();
        router.FindRoute(k, a, b, path, route);
        var from = a;
        var idx = 0;
        while (idx < route.Count)
        {
            Assert.True(router.Refine(k, from, route, ref idx, 2, path));
            from = path[^1];
        }
        return [.. path];
    }

    [Fact]
    public void LookingUpAnUnexpandedPortalDoesNotBuildItsSector()
    {
        var nav = new NavGrid(new Grid(96, 96), null);
        var k = nav.Class(0, 1);
        var graph = new Hpa(nav, k, new LocalSearch());
        var nodes = graph.PortalNodes(4);
        Assert.NotEmpty(nodes);
        Assert.Equal(0, graph.Builds);
        var before = graph.Partner(4, 0);
        Assert.Equal(0, graph.Builds);
        graph.BuildAll();
        Assert.Equal(before, graph.Partner(4, 0));
        Assert.Equal(nodes, graph.Get(4).Nodes);
    }

    [Fact]
    public void HeaderOnlyRebuildUnlinksIncomingPartnerIndices()
    {
        var grid = new Grid(160, 96);
        var nav = new NavGrid(grid, null);
        var k = nav.Class(0, 1);
        var graph = new Hpa(nav, k, new LocalSearch());
        var before = graph.Partner(9, 0); // sector 9's left edge points into sector 8's right edge
        grid.Set(new Cell(95, 40), Terrain.Blocked); // split sector 8's left portal list, renumbering its right edge
        nav.Refresh(95, 40, 95, 40);
        graph.Invalidate(95, 40, 95, 40);
        graph.PortalNodes(8); // border-only rebuild; sector 9 itself was outside invalidation
        var expected = new Hpa(nav, k, new LocalSearch()).Partner(9, 0);
        Assert.NotEqual(before.Node, expected.Node);
        Assert.Equal(expected, graph.Partner(9, 0));
        Assert.Equal(0, graph.Builds);
    }

    [Fact]
    public void SixteenHundredSquareKeepsTheOriginalRouteAtTheBoundary()
    {
        const int n = 1600;
        var grid = new Grid(n, n);
        var current = new Router(grid, null, []);
        var legacy = new Router(grid, null, []) { LegacyRouting = true };
        var k = current.Nav.Class(0, 1);
        legacy.Nav.Class(0, 1);
        Assert.Equal(Route(legacy, k, n + 1, n * n - n - 2), Route(current, k, n + 1, n * n - n - 2));
    }

    [Fact]
    public void CoarseGraphTracksRegionConnectivityAndInvalidation()
    {
        var grid = new Grid(160, 96);
        // The two halves of the middle sector connect only by going round its wall.
        for (var y = 0; y < 64; y++) { grid.Set(new Cell(80, y), Terrain.Blocked); }
        var nav = new NavGrid(grid, null);
        var k = nav.Class(0, 1);
        var regions = new Regions(nav, k);
        var sectors = new List<int>();
        Assert.True(regions.SectorRoute(20 * 160 + 70, 20 * 160 + 90, sectors));
        Assert.Contains(sectors, s => s / 5 == 2);
        for (var y = 64; y < 96; y++) { grid.Set(new Cell(80, y), Terrain.Blocked); }
        nav.Refresh(80, 64, 80, 95);
        regions.Invalidate(76, 60, 84, 95);
        Assert.False(regions.SectorRoute(20 * 160 + 70, 20 * 160 + 90, sectors));
        grid.Set(new Cell(80, 30), Terrain.Clear);
        nav.Refresh(80, 30, 80, 30);
        regions.Invalidate(76, 26, 84, 34);
        Assert.True(regions.SectorRoute(20 * 160 + 70, 20 * 160 + 90, sectors));
    }
}
