using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// NavGrid.Refresh over terrain finds each wide class's block costs by a max along rows then columns (BUSTER-FPS-2); every cost must
// be the one CellCost gives cell by cell, which a fresh grid's classes are built with.
public class NavTerrainRefreshTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static List<int> AddClasses(NavGrid nav)
    {
        var classes = new List<int>();
        for (var tc = 0; tc < Rules.Classes.Count; tc++)
        {
            for (var width = 1; width <= NavGrid.MaxWidth; width++)
            {
                classes.Add(nav.Class(tc, width));
                classes.Add(nav.Class(tc, width, noRoads: true));
            }
        }
        return classes;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RefreshOverChangedTerrainMatchesAFreshBuild(int seed)
    {
        const int size = 40;
        var r = new Rand((ulong)seed);
        var grid = new Grid(size, size);
        for (var i = 0; i < 40; i++)
        {
            grid.Set(new Cell(r.Intn(size), r.Intn(size)), Terrain.Blocked);
        }
        var heights = new int[(size + 1) * (size + 1)];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = (int)(r.Intn(6) * Fixed.OneRaw / 3); // slopes, some too steep
        }
        var terrain = new TerrainMap(size, size, heights, new byte[size * size], Rules);
        for (var i = 0; i < 120; i++)
        {
            terrain.SetForest(new Cell(r.Intn(size), r.Intn(size)), 1 + r.Intn(3));
        }
        terrain.SetRoad(new Cell(0, 17), size, 3, true);
        terrain.SetRoad(new Cell(22, 0), 2, size, true);
        var nav = new NavGrid(grid, terrain);
        var classes = AddClasses(nav);
        for (var trial = 0; trial < 12; trial++)
        {
            // A box of corners rewritten (a tunnel's ground stepping), at the map's edge too, and a forest cell and a road changed.
            int bw = 2 + r.Intn(20), bh = 2 + r.Intn(20), bx = r.Intn(size - 1), by = r.Intn(size - 1);
            var box = new int[bw * bh];
            for (var i = 0; i < box.Length; i++)
            {
                box[i] = (int)(r.Intn(8) * Fixed.OneRaw / 3);
            }
            terrain.WriteCorners(bx, by, bw, bh, box, null);
            var f = new Cell(r.Intn(size), r.Intn(size));
            terrain.SetForest(f, r.Intn(4));
            terrain.SetRoad(new Cell(bx, by), 1, 1, trial % 2 == 0);
            if (trial % 4 == 3)
            {
                nav.Refresh(0, 0, size - 1, size - 1);
            }
            else
            {
                nav.Refresh(Math.Max(0, Math.Min(bx, f.X) - 3), Math.Max(0, Math.Min(by, f.Y) - 3),
                    Math.Min(size - 1, Math.Max(bx + bw, f.X) + 3), Math.Min(size - 1, Math.Max(by + bh, f.Y) + 3));
            }
            var fresh = new NavGrid(grid, terrain);
            var freshClasses = AddClasses(fresh);
            for (var n = 0; n < classes.Count; n++)
            {
                for (var i = 0; i < size * size; i++)
                {
                    Assert.Equal(fresh.Cost(freshClasses[n], i), nav.Cost(classes[n], i));
                }
            }
        }
    }
}
