using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// The terrain digest computed a slice at a time (TerrainMap.DigestStep, BUSTER-FPS-2): a renderer warms it over a few frames after the
// ground changes, so the game hash never pays the whole map in one frame. The value must be the one computed whole, byte for byte.
public class TerrainDigestStepTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    // Every part of the digest: corners (a pad), flags (forest, roads), water, an alley (the second flags byte) and a bridge site.
    private const string Json = """
        { "width": 40, "height": 30,
          "forest": [{ "rect": [2, 2, 10, 6], "density": 2 }, { "rect": [20, 15, 5, 5], "density": 3 }],
          "roads": [{ "points": [[0, 12], [39, 20]], "width": 3, "kind": "gravel" }, { "points": [[0, 26], [25, 26]], "width": 1, "kind": "alley" }],
          "pads": [{ "rect": [5, 20, 6, 4], "height": 15, "blend": 2 }],
          "water": [{ "kind": "lake", "polygon": [[30, 22], [38, 22], [38, 28], [30, 28]], "height": 0 }] }
        """;

    private static TerrainMap LoadJson(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "simcore-digest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "map.json"), json);
            return TerrainLoader.LoadMap(Path.Combine(dir, "map.json"), Rules);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static TerrainMap Map()
    {
        var map = LoadJson(Json);
        map.AddBridgeSite(new BridgeSite("concrete", new Cell(30, 19), Facing.South));
        return map;
    }

    // Slices runs DigestStep with the budget until the digest is ready and returns how many calls it took.
    private static int Slices(TerrainMap map, int bytes)
    {
        var calls = 1;
        while (!map.DigestStep(bytes))
        {
            calls++;
        }
        return calls;
    }

    [Fact]
    public void ASlicedDigestEqualsTheWholeOne()
    {
        var whole = Map().Digest();
        foreach (var bytes in new[] { 1, 7, 64, 333, 1000, int.MaxValue })
        {
            var map = Map();
            Assert.False(map.DigestReady);
            var calls = Slices(map, bytes);
            Assert.True(map.DigestReady);
            Assert.Equal(whole, map.Digest());
            Assert.True(bytes == int.MaxValue ? calls == 1 : calls > 1, $"{bytes} bytes a slice: {calls} calls");
        }
        // The pinned value of a map without water (WaterTests): sliced, still the same bytes.
        var dry = LoadJson("""
            { "width": 40, "height": 30,
              "forest": [{ "rect": [2, 2, 10, 6], "density": 2 }, { "rect": [20, 15, 5, 5], "density": 3 }],
              "roads": [{ "points": [[0, 12], [39, 20]], "width": 3, "kind": "gravel" }, { "rect": [30, 2, 4, 8] }],
              "pads": [{ "rect": [5, 20, 6, 4], "height": 15, "blend": 2 }] }
            """);
        Slices(dry, 100);
        Assert.Equal(1521206828070589749UL, dry.Digest());
    }

    [Fact]
    public void AChangeMidwayStartsTheDigestAgain()
    {
        var before = Map().Digest();
        var changed = Map();
        changed.SetForest(new Cell(35, 3), 3);
        var after = changed.Digest();
        Assert.NotEqual(before, after);
        // Stopped in the corners, the flags, the water and the second flags byte: the rest after the change, sliced or whole.
        foreach (var stop in new[] { 1, 10, 18, 22, 26 })
        {
            foreach (var finishWhole in new[] { false, true })
            {
                var map = Map();
                for (var k = 0; k < stop; k++)
                {
                    Assert.False(map.DigestStep(300));
                }
                map.SetForest(new Cell(35, 3), 3);
                Assert.False(map.DigestReady);
                if (!finishWhole)
                {
                    Slices(map, 300);
                }
                Assert.Equal(after, map.Digest());
            }
        }
        // A change after it is ready clears it too.
        var ready = Map();
        Slices(ready, 300);
        ready.SetForest(new Cell(35, 3), 3);
        Assert.False(ready.DigestReady);
        Slices(ready, 300);
        Assert.Equal(after, ready.Digest());
    }

    [Fact]
    public void ABlastMidwayLeavesTheDigestAsItWas()
    {
        // A blast's thinned tree is not in the digest (the world hashes it) and does not clear it: half done, the rest reads the
        // cell's forest as it was.
        var before = Map().Digest();
        foreach (var stop in new[] { 1, 17, 18 })
        {
            var map = Map();
            for (var k = 0; k < stop; k++)
            {
                map.DigestStep(300);
            }
            Assert.True(map.Thin(new Cell(4, 4)));
            Slices(map, 300);
            Assert.Equal(before, map.Digest());
        }
    }
}
