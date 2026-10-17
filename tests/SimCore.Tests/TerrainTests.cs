using System.IO.Compression;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The terrain data foundation: heights per corner, HeightAt, slope classes and passability per
// movement class, road and forest costs, pads, the map file format, and flat maps unchanged.
public class TerrainTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
    private static int Foot => Index("foot");
    private static int Wheeled => Index("wheeled");
    private static int Heavy => Index("heavy_wheeled");

    private static int Index(string id)
    {
        for (var i = 0; i < Rules.Classes.Count; i++)
        {
            if (Rules.Classes[i].Id == id)
            {
                return i;
            }
        }
        throw new ArgumentException(id);
    }

    // Terrain builds a w x h map with corner heights from f (raw fixed point).
    private static TerrainMap Terrain(int w, int h, Func<int, int, int> f, byte[]? flags = null)
    {
        var corners = new short[(w + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        {
            for (var x = 0; x <= w; x++)
            {
                corners[y * (w + 1) + x] = (short)f(x, y);
            }
        }
        return new TerrainMap(w, h, corners, flags ?? new byte[w * h], Rules);
    }

    [Fact]
    public void HeightAtIsBilinearBetweenTheCornersRoundedHalfUp()
    {
        // A ramp: corner x has height 100 x raw.
        var t = Terrain(4, 4, (x, y) => x * 100 + y * 10);
        Assert.Equal(0, t.HeightAt(new Vec(Fixed.Zero, Fixed.Zero)).Raw);
        Assert.Equal(100, t.HeightAt(new Vec(Fixed.FromInt(1), Fixed.Zero)).Raw);
        Assert.Equal(155, t.HeightAt(new Cell(1, 0).Center()).Raw); // (100 + 200 + 110 + 210) / 4
        Assert.Equal(25, t.HeightAt(new Vec(Fixed.FromRaw(256), Fixed.Zero)).Raw); // a quarter of the way
        // Rounding: 1/3 of 1 raw between 0 and 1 rounds to 0, 2/3 to 1.
        var tiny = Terrain(1, 1, (x, y) => x);
        Assert.Equal(0, tiny.HeightAt(new Vec(Fixed.FromRaw(341), Fixed.Zero)).Raw);
        Assert.Equal(1, tiny.HeightAt(new Vec(Fixed.FromRaw(683), Fixed.Zero)).Raw);
        // Off the map: the nearest edge.
        Assert.Equal(400, t.HeightAt(new Vec(Fixed.FromInt(9), Fixed.Zero)).Raw);
    }

    [Theory]
    [InlineData(0, SlopeClass.Flat)]
    [InlineData(9, SlopeClass.Flat)]
    [InlineData(10, SlopeClass.Gentle)]
    [InlineData(24, SlopeClass.Gentle)]
    [InlineData(25, SlopeClass.Steep)]
    [InlineData(44, SlopeClass.Steep)]
    [InlineData(45, SlopeClass.Cliff)]
    [InlineData(200, SlopeClass.Cliff)]
    public void SlopeClassesFromTheGrade(int grade, SlopeClass want)
    {
        // A cell rising grade percent of its width (1024 raw) from one side to the other.
        var t = Terrain(1, 1, (x, y) => x * grade * 1024 / 100 + (grade * 1024 % 100 != 0 ? 1 : 0) * x);
        Assert.Equal(grade, t.Grade(new Cell(0, 0)));
        Assert.Equal(want, t.Slope(Foot, new Cell(0, 0)));
    }

    [Fact]
    public void PassabilityPerMovementClass()
    {
        // Columns of cells with grades 0, 15, 30 and 60 percent (corner x rises by those steps).
        int[] grades = [0, 15, 30, 60];
        var t = Terrain(4, 1, (x, y) =>
        {
            var hgt = 0;
            for (var i = 0; i < x; i++)
            {
                hgt += grades[i] * 1024 / 100;
            }
            return hgt;
        });
        var (flat, gentle, steep, cliff) = (new Cell(0, 0), new Cell(1, 0), new Cell(2, 0), new Cell(3, 0));
        Assert.True(t.Passable(Foot, flat) && t.Passable(Foot, gentle) && t.Passable(Foot, steep));
        Assert.False(t.Passable(Foot, cliff));
        Assert.True(t.Passable(Wheeled, flat) && t.Passable(Wheeled, gentle));
        Assert.False(t.Passable(Wheeled, steep)); // vehicles keep to roads and valleys
        Assert.False(t.Passable(Wheeled, cliff));
        Assert.Equal(TerrainMap.BaseCost, t.Cost(Foot, flat));
        Assert.True(t.Cost(Foot, steep) > t.Cost(Foot, gentle) && t.Cost(Foot, gentle) > t.Cost(Foot, flat));
        // Speed: up and down a gentle slope.
        Assert.Equal(85, t.SpeedPercent(Foot, gentle, uphill: true));
        Assert.Equal(105, t.SpeedPercent(Foot, gentle, uphill: false));
        Assert.Equal(75, t.SpeedPercent(Wheeled, gentle, uphill: true));
        Assert.Equal(0, t.SpeedPercent(Wheeled, steep, uphill: false));
        // Climbing costs more than going down (centre to centre).
        Assert.True(t.StepCost(Foot, flat, gentle) > t.StepCost(Foot, steep, gentle));
        Assert.Equal(0, t.StepCost(Wheeled, gentle, steep));
        Assert.False(t.Passable(Foot, new Cell(-1, 0))); // off the map
    }

    [Fact]
    public void RoadsAreCheaperForestsDearerAndDenseForestStopsHeavyTrucks()
    {
        // Flat 4 x 1: plain, road, grove (2), dense forest (3).
        var t = Terrain(4, 1, (x, y) => 0, [0, 1 << 2, 2, 3]);
        var (plain, road, grove, dense) = (new Cell(0, 0), new Cell(1, 0), new Cell(2, 0), new Cell(3, 0));
        Assert.True(t.Road(road));
        Assert.Equal(2, t.Forest(grove));
        Assert.True(t.Cost(Wheeled, road) < t.Cost(Wheeled, plain));
        Assert.True(t.Cost(Wheeled, grove) > t.Cost(Wheeled, plain));
        Assert.True(t.Cost(Foot, grove) >= t.Cost(Foot, plain));
        Assert.True(t.Passable(Wheeled, dense)); // a light vehicle squeezes through, slowly
        Assert.True(t.SpeedPercent(Wheeled, dense, false) < t.SpeedPercent(Wheeled, plain, false));
        Assert.False(t.Passable(Heavy, dense)); // a truck can't
        Assert.True(t.Passable(Heavy, grove));
        Assert.True(t.Passable(Foot, dense)); // infantry walk in
        // Knocking the forest down reopens the cell.
        t.SetForest(dense, 1);
        Assert.True(t.Passable(Heavy, dense));
    }

    [Fact]
    public void ClassesByLocomotorAndRadius()
    {
        var c = TestContent.Mvp;
        int Of(string id) => Rules.ClassOf(c.Archetype(id)!.Locomotor, c.Archetype(id)!.Radius);
        Assert.Equal(Foot, Of("assault"));
        Assert.Equal(Wheeled, Of("technical"));
        Assert.Equal(Wheeled, Of("rocket_jeep"));
        Assert.Equal(Heavy, Of("cargo_truck"));
        Assert.Equal(Heavy, Of("missile_truck"));
        Assert.Equal(-1, Of("supply_depot"));
    }

    [Fact]
    public void PadsFlattenTheGroundAndAreMarked()
    {
        var t = Terrain(8, 8, (x, y) => x * 300); // a 29% grade everywhere: steep
        Assert.Equal(SlopeClass.Steep, t.Slope(Foot, new Cell(3, 3)));
        t.SetPad(new Cell(2, 2), 3, 3, Fixed.FromRaw(700));
        for (var y = 2; y < 5; y++)
        {
            for (var x = 2; x < 5; x++)
            {
                Assert.True(t.Pad(new Cell(x, y)));
                Assert.Equal(SlopeClass.Flat, t.Slope(Foot, new Cell(x, y)));
                Assert.Equal(700, t.HeightAt(new Cell(x, y).Center()).Raw);
            }
        }
        Assert.False(t.Pad(new Cell(5, 3)));
    }

    [Fact]
    public void LoadsAMapFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "simcore-terrain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 6 x 4 cells: 7 x 5 corners, heights rising along x from pixel 0 to 65535 (-16 to 48 m).
            const int W = 6, H = 4;
            var heights = new int[(W + 1) * (H + 1)];
            for (var y = 0; y <= H; y++)
            {
                for (var x = 0; x <= W; x++)
                {
                    heights[y * (W + 1) + x] = x * 65535 / W;
                }
            }
            File.WriteAllBytes(Path.Combine(dir, "hills.png"), PngWriter.Grey(W + 1, H + 1, 16, heights));
            var forest = new int[W * H];
            forest[3 * W + 5] = 255; // dense
            File.WriteAllBytes(Path.Combine(dir, "hills_forest.png"), PngWriter.Grey(W, H, 8, forest));
            File.WriteAllText(Path.Combine(dir, "hills.json"), """
                {
                  "width": 6, "height": 4, "heightmap": "hills.png", "minHeight": -16, "maxHeight": 48,
                  "forestMap": "hills_forest.png",
                  "forest": [{ "rect": [0, 0, 1, 1], "density": 2 }],
                  "roads": [{ "points": [[0, 2], [5, 2]], "width": 1 }],
                  "pads": [{ "rect": [1, 0, 2, 1], "height": 50 }]
                }
                """);
            var t = TerrainLoader.LoadMap(Path.Combine(dir, "hills.json"), Rules);
            Assert.Equal((W, H), (t.Width, t.Height));
            Assert.Equal(-16 * TerrainLoader.RawPerMetre, t.Corner(0, 3).Raw);
            Assert.Equal(48 * TerrainLoader.RawPerMetre, t.Corner(W, 3).Raw);
            Assert.Equal(3, t.Forest(new Cell(5, 3)));
            Assert.Equal(2, t.Forest(new Cell(0, 0)));
            for (var x = 0; x <= 5; x++)
            {
                Assert.True(t.Road(new Cell(x, 2)));
            }
            Assert.False(t.Road(new Cell(0, 1)));
            Assert.True(t.Pad(new Cell(1, 0)) && t.Pad(new Cell(2, 0)));
            Assert.Equal(5 * TerrainLoader.RawPerMetre, t.HeightAt(new Cell(1, 0).Center()).Raw); // the pad at 5 m
            // The same file twice: the same digest.
            Assert.Equal(t.Digest(), TerrainLoader.LoadMap(Path.Combine(dir, "hills.json"), Rules).Digest());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FlatMapsHashAsBeforeAndTerrainJoinsTheHash()
    {
        Arena.New(32).Hash();
        var plain = Arena.New(32);
        var withTerrain = Arena.New(32);
        Assert.Equal(plain.Hash(), withTerrain.Hash());
        withTerrain.AttachTerrain(TerrainMap.Flat(32, 32, Rules));
        Assert.NotEqual(plain.Hash(), withTerrain.Hash());
        var hilly = Arena.New(32);
        hilly.AttachTerrain(Terrain(32, 32, (x, y) => (x * y) % 900));
        Assert.NotEqual(withTerrain.Hash(), hilly.Hash());
        Assert.Throws<InvalidOperationException>(() => Arena.New(32).AttachTerrain(TerrainMap.Flat(16, 16, Rules)));
    }

    [Fact]
    public void CompactAtFifteenHundredSquare()
    {
        var t = TerrainMap.Flat(1500, 1500, Rules);
        // 2 bytes a corner, 1 flag, 1 grade and 1 per movement class a cell.
        var want = 1501L * 1501 * 2 + 1500L * 1500 * (2 + Rules.Classes.Count);
        Assert.Equal(want, t.Bytes);
        Assert.True(t.Bytes < 32L * 1024 * 1024);
    }
}

// PngWriter writes a greyscale PNG (tests only): 8 or 16 bits, no filtering.
internal static class PngWriter
{
    public static byte[] Grey(int w, int h, int depth, int[] samples)
    {
        var bpp = depth / 8;
        var raw = new byte[(w * bpp + 1) * h];
        for (var y = 0; y < h; y++)
        {
            var row = y * (w * bpp + 1);
            for (var x = 0; x < w; x++)
            {
                var v = samples[y * w + x];
                if (depth == 16)
                {
                    raw[row + 1 + x * 2] = (byte)(v >> 8);
                    raw[row + 2 + x * 2] = (byte)v;
                }
                else
                {
                    raw[row + 1 + x] = (byte)v;
                }
            }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
        {
            zs.Write(raw);
        }
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        Be(ihdr, 0, w);
        Be(ihdr, 4, h);
        ihdr[8] = (byte)depth;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Be(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        var len = new byte[4];
        Be(len, 0, body.Length);
        s.Write(len);
        s.Write(System.Text.Encoding.ASCII.GetBytes(type));
        s.Write(body);
        s.Write(new byte[4]); // CRC: not checked by the reader
    }
}
