using System.Numerics;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// BigMap is a 6,000 x 3,000 map (12 x 6 km, the first big map's size) loaded once from its files for MapSizeTests: a
// 6,001 x 3,001 16-bit heightmap (hills from -20 to 120 m), an 8-bit forest map, roads, a lake and a pad.
public sealed class BigMap : IDisposable
{
    public const int W = 6000, H = 3000;

    public static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "simcore-bigmap-" + Guid.NewGuid().ToString("N"));

    public BigMap()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "big.png"), PngWriter.Grey(W + 1, H + 1, 16, Corners()));
        var forest = new int[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                // Woods in 200-cell squares on a checker of 400, none near the corners.
                var wood = x / 200 % 2 == 1 && y / 200 % 2 == 1 && x > 300 && x < W - 300;
                forest[y * W + x] = wood ? (x + y) % 3 == 0 ? 255 : 160 : 0;
            }
        }
        File.WriteAllBytes(Path.Combine(_dir, "big_forest.png"), PngWriter.Grey(W, H, 8, forest));
        File.WriteAllText(Path.Combine(_dir, "big.json"), $$"""
            { "width": {{W}}, "height": {{H}}, "heightmap": "big.png", "forestMap": "big_forest.png", "minHeight": -20, "maxHeight": 120,
              "roads": [
                { "kind": "highway", "points": [[0, 1500], [3000, 1400], [5999, 1500]], "width": 4 },
                { "kind": "dirt", "points": [[2000, 0], [2100, 2999]], "width": 2 }
              ],
              "water": [{ "kind": "lake", "polygon": [[2900, 600], [3300, 600], [3300, 900], [2900, 900]], "height": 300 }],
              "pads": [{ "rect": [{{W - 40}}, {{H - 40}}, 12, 8], "height": 50 }] }
            """);
        Map = TerrainLoader.LoadMap(Path.Combine(_dir, "big.json"), Rules);
    }

    public TerrainMap Map { get; }

    // Corners: rolling hills (triangle waves, about 33 m of relief and at most a 3% grade), pixels over the PNG's -20..120 m.
    public static int[] Corners()
    {
        var corners = new int[(W + 1) * (H + 1)];
        for (var y = 0; y <= H; y++)
        {
            for (var x = 0; x <= W; x++)
            {
                corners[y * (W + 1) + x] = Sample(x, y);
            }
        }
        return corners;
    }

    public static int Sample(int x, int y) => 20000 + Tri(x, 1600) * 10 + Tri(y, 1200) * 8 + Tri(x + y, 900) * 6;

    private static int Tri(int v, int period) => Math.Abs(v % period - period / 2);

    public void Dispose() => Directory.Delete(_dir, true);
}

// MapSizeTests: maps up to TerrainMap.MaxSide (8,192) cells a side load, route, see and fight at their far corners; one a
// cell over is refused. The 6,000 x 3,000 map (18 M cells) is loaded once for the class (its tests run one after another).
public class MapSizeTests(BigMap big) : IClassFixture<BigMap>
{
    private const int W = BigMap.W, H = BigMap.H;

    private static World NewWorld(TerrainMap map, bool fog = false)
    {
        var setup = TestContent.Battle(3);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })], Fog = fog };
        var w = new World(new Grid(map.Width, map.Height), MatchCompiler.Compile(TestContent.Mvp, setup));
        w.AttachTerrain(map);
        return w;
    }

    [Fact]
    public void ASixThousandByThreeThousandMapLoadsWithItsHeightmap()
    {
        var m = big.Map;
        Assert.Equal((W, H), (m.Width, m.Height));
        // Pixel p maps to -20 + 140 p / 65535 metres, 512 raw a metre: check the far corners and the middle exactly.
        static long Raw(int p) => -20 * 512 + ((long)p * 140 * 512 + 32767) / 65535;
        foreach (var (x, y) in new[] { (0, 0), (W, H), (W, 0), (0, H), (W / 2, H / 2), (W - 1, H - 1) })
        {
            Assert.Equal(Raw(BigMap.Sample(x, y)), m.Corner(x, y).Raw);
        }
        Assert.True(m.Road(new Cell(W - 1, 1500)) && m.RoadKindAt(new Cell(W - 1, 1500)) == RoadKind.Highway);
        Assert.True(m.Water(new Cell(3100, 750)) && !m.Water(new Cell(3100, 950)));
        Assert.True(m.Pad(new Cell(W - 35, H - 35)));
        Assert.Equal(m.GroundHeightAt(new Cell(W - 1, H - 1).Center()), m.HeightAt(new Cell(W - 1, H - 1).Center()));
        // Past the far edge (the fly zone) the ground is the edge's.
        var far = new Vec(Fixed.FromInt(W + 70), Fixed.FromInt(H + 70));
        Assert.Equal(m.HeightAt(new Vec(Fixed.FromInt(W), Fixed.FromInt(H))), m.HeightAt(far));
        Assert.Equal(m.Digest(), m.Digest());
        // The terrain's own centre heights are the ground at every cell's centre (what routing and fog read).
        foreach (var (x, y) in new[] { (0, 0), (W - 1, H - 1), (4321, 1234) })
        {
            Assert.Equal(m.GroundHeightAt(new Cell(x, y).Center()).Raw, (m.CentreSums[y * W + x] + 2) >> 2);
        }
    }

    [Fact]
    public void RoutesCornerToCorner()
    {
        var w = NewWorld(big.Map);
        var r = w.Routing;
        int s = 2 * W + 2, d = (H - 3) * W + (W - 3);
        foreach (var name in new[] { "Rifleman", "technical" })
        {
            var def = w.Player(1)!.Setup.Find(w.Content, name)!;
            var k = r.ClassOf(def);
            var path = new List<int>();
            var route = new List<int>();
            r.FindRoute(k, s, d, path, route);
            Assert.Equal(d, route[^1]);
            var idx = 0;
            Assert.True(r.Refine(k, s, route, ref idx, route.Count, path), $"{name}: every leg can be walked");
            Assert.Equal(d, path[^1]);
            var c = s;
            foreach (var n in path)
            {
                int cx = c % W, cy = c / W, nx = n % W, ny = n / W;
                Assert.True(Math.Max(Math.Abs(cx - nx), Math.Abs(cy - ny)) == 1 && r.Nav.Step(k, cx, cy, nx - cx, ny - cy) > 0, $"{name}: a step at {cx}, {cy}");
                c = n;
            }
            Assert.True(path.Count >= W - 5, $"{name}: {path.Count} cells corner to corner");
        }

        // Through the world: a soldier ordered from the near corner to the far one gets that route and sets off.
        var u = w.Spawn(1, "Rifleman", 2, 2);
        w.Order(1, CommandKind.Move, [u], cell: new Cell(W - 3, H - 3));
        w.Run(40);
        Assert.Equal(d, u.Route[^1]);
        Assert.True(u.Pos.X.Raw > new Cell(2, 2).Center().X.Raw, "it walks toward the far corner");
    }

    [Theory]
    [InlineData("foot", 1)]
    [InlineData("wheeled", 2)]
    [InlineData("tracked", 2)]
    public void SecondLevelColdRouteArrivesWithBoundedSectorBuilds(string terrainClass, int width)
    {
        var w = NewWorld(big.Map);
        var r = w.Routing;
        var tc = BigMap.Rules.Classes.Select(c => c.Id).ToList().IndexOf(terrainClass);
        var k = r.Nav.Class(tc, width);
        var from = r.NearestPassable(k, new Cell(2, 2), 32);
        var goal = r.NearestPassable(k, new Cell(W - 3, H - 3), 32);
        var path = new List<int>();
        var route = new List<int>();
        r.FindRoute(k, from, goal, path, route);
        Assert.NotEmpty(route);
        Assert.Equal(goal, route[^1]);
        // Coarse border routing never builds an all-pairs sector matrix for this order.
        Assert.Equal(0, r.Graph(k).Builds);
        var idx = 0;
        Assert.True(r.Refine(k, from, route, ref idx, route.Count, path));
        Assert.Equal(goal, path[^1]);
        var previous = from;
        foreach (var cell in path)
        {
            var dx = cell % W - previous % W;
            var dy = cell / W - previous / W;
            Assert.InRange(Math.Max(Math.Abs(dx), Math.Abs(dy)), 1, 1);
            Assert.True(r.Nav.Step(k, previous % W, previous / W, dx, dy) > 0);
            previous = cell;
        }
    }

    [Fact]
    public void FogAndVisionWorkAtTheFarCorner()
    {
        var w = NewWorld(big.Map, fog: true);
        var scout = w.Spawn(1, "Rifleman", W - 30, H - 6);
        var near = w.Spawn(2, "Rifleman", W - 26, H - 6);
        var away = w.Spawn(2, "Rifleman", 40, 40);
        w.Run(3);
        var fog = w.FogOf(1)!;
        Assert.True(fog.Visible(W - 30, H - 6) && fog.Visible(W - 26, H - 6) && fog.Explored(W - 30, H - 6));
        Assert.False(fog.Visible(40, 40));
        Assert.False(fog.Visible(W - 30, H - 200));
        Assert.True(w.FogOf(2)!.Visible(40, 40) && w.FogOf(2)!.Visible(W - 30, H - 6));
        Assert.NotEqual(0u, near.SpottedBy & World.Bit(1)); // player 1 sees the enemy beside its soldier ...
        Assert.Equal(0u, away.SpottedBy & World.Bit(1));     // ... and not the one 12 km away
        Assert.True(w.FogMatchesRebuild());
        Assert.True(scout.Alive);
        // A terrain change at the far corner (a pad flattened; the shared map keeps it, far from what the other tests read)
        // re-sweeps the viewers there; the grids stay exact.
        big.Map.SetPad(new Cell(W - 28, H - 12), 3, 3, big.Map.GroundHeightAt(new Cell(W - 28, H - 12).Center()) + Fixed.FromInt(2), 1);
        w.Run(2);
        Assert.True(w.FogMatchesRebuild());
    }

    [Fact]
    public void NoOverflowAtTheExtremes()
    {
        // Distances across the whole map and its fly zone (the widest is 74 cells), raw, against exact arithmetic.
        long m = 80 * Fixed.OneRaw;
        var a = new Vec(Fixed.FromRaw(-m), Fixed.FromRaw(-m));
        var b = new Vec(Fixed.FromRaw((long)TerrainMap.MaxSide * Fixed.OneRaw + m), Fixed.FromRaw((long)TerrainMap.MaxSide * Fixed.OneRaw + m));
        var d = b - a;
        var exact = (BigInteger)d.X.Raw * d.X.Raw + (BigInteger)d.Y.Raw * d.Y.Raw;
        Assert.Equal(exact, (BigInteger)d.LenSq());
        var len = (BigInteger)d.Len().Raw;
        Assert.True(len * len <= exact && (len + 1) * (len + 1) > exact);
        Assert.Equal(new Cell(TerrainMap.MaxSide - 1, TerrainMap.MaxSide - 1), Cell.Of(new Cell(TerrainMap.MaxSide - 1, TerrainMap.MaxSide - 1).Center()));
        Assert.True((long)(TerrainMap.MaxSide + 1) * (TerrainMap.MaxSide + 1) < int.MaxValue / 16, "cell and corner indices stay far inside an int");

        // Units at the far corner fight, with one at the near corner 13.4 km away; the hash covers the whole map.
        var w = NewWorld(big.Map);
        var red = w.Spawn(1, "Rifleman", W - 2, H - 2);
        var blue = w.Spawn(2, "Rifleman", W - 6, H - 2);
        var corner = w.Spawn(1, "Rifleman", 0, 0);
        w.Run(150);
        Assert.True(red.HP < red.Def.MaxHp || blue.HP < blue.Def.MaxHp || !red.Alive || !blue.Alive, "they fight at the far corner");
        Assert.True(corner.Alive);
        var across = (corner.Pos - new Cell(W - 1, H - 1).Center()).Len();
        Assert.True(Math.Abs(across.Raw - (long)(Math.Sqrt((double)(W - 1) * (W - 1) + (double)(H - 1) * (H - 1)) * Fixed.OneRaw)) < 2 * Fixed.OneRaw);
        Assert.Equal(w.Hash(), w.Hash());
    }

    [Fact]
    public void AMapOverTheCapIsRefused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "simcore-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "m.json");
            foreach (var (w, h) in new[] { (TerrainMap.MaxSide + 1, 16), (16, TerrainMap.MaxSide + 1), (0, 16), (16, -1) })
            {
                File.WriteAllText(path, $$"""{ "width": {{w}}, "height": {{h}} }""");
                var e = Assert.Throws<ContentException>(() => TerrainLoader.LoadMap(path, BigMap.Rules));
                Assert.Equal($"m.json: width and height want 1 to 8192 cells, got {w}x{h}", e.Message);
            }
            Assert.Throws<ArgumentException>(() => TerrainMap.Flat(TerrainMap.MaxSide + 1, 2, BigMap.Rules));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Grid(TerrainMap.MaxSide + 1, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Grid(2, TerrainMap.MaxSide + 1));

            // The cap itself loads: 8,192 cells wide with its 8,193-pixel heightmap.
            const int cw = TerrainMap.MaxSide, ch = 8;
            var samples = new int[(cw + 1) * (ch + 1)];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i % (cw + 1) * 7; // up to 57,344
            }
            File.WriteAllBytes(Path.Combine(dir, "wide.png"), PngWriter.Grey(cw + 1, ch + 1, 16, samples));
            File.WriteAllText(path, $$"""{ "width": {{cw}}, "height": {{ch}}, "heightmap": "wide.png", "minHeight": 0, "maxHeight": 128 }""");
            var map = TerrainLoader.LoadMap(path, BigMap.Rules);
            Assert.Equal((cw, ch), (map.Width, map.Height));
            Assert.Equal(((long)cw * 7 * 128 * 512 + 32767) / 65535, map.Corner(cw, ch).Raw);
            var world = NewWorld(map);
            var u = world.Spawn(1, "Rifleman", cw - 1, ch - 1);
            world.Order(1, CommandKind.Move, [u], cell: new Cell(cw - 40, 1));
            world.Run(40);
            Assert.True(u.Pos.X.Raw < new Cell(cw - 1, ch - 1).Center().X.Raw, "it walks at the cap's far edge");

            // A heightmap of the wrong size is refused from its header, before its pixels are decoded (these are no zlib).
            File.WriteAllBytes(Path.Combine(dir, "bad.png"), FakePng(W + 1, H, 16));
            File.WriteAllText(path, $$"""{ "width": {{W}}, "height": {{H}}, "heightmap": "bad.png" }""");
            var bad = Assert.Throws<ContentException>(() => TerrainLoader.LoadMap(path, BigMap.Rules));
            Assert.Equal($"m.json: bad.png wants a 16-bit greyscale PNG of {W + 1}x{H + 1} (corners), got {W + 1}x{H} 16-bit", bad.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // FakePng is a PNG header of the given size and depth followed by an IDAT that is not zlib data.
    private static byte[] FakePng(int w, int h, int depth)
    {
        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        void Chunk(string type, byte[] body)
        {
            png.AddRange([(byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length]);
            png.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            png.AddRange(body);
            png.AddRange([0, 0, 0, 0]);
        }
        Chunk("IHDR", [(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, (byte)depth, 0, 0, 0, 0]);
        Chunk("IDAT", [1, 2, 3, 4]);
        Chunk("IEND", []);
        return [.. png];
    }
}
