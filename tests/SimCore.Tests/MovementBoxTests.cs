using SimCore.FixedPoint;
using SimCore.Rng;
using SimCore.Sim;

namespace SimCore.Tests;

public class MovementBoxTests
{
    private static World RandomWorld(int seed, bool full)
    {
        var w = Arena.New(128, (ulong)seed);
        w.FullBoxScan = full;
        var r = new Rand((ulong)seed);
        for (var i = 0; i < 100; i++)
        {
            var u = w.Spawn(1, i % 3 == 0 ? "vehicle_garage" : "supply_depot", r.Range(8, 115), r.Range(8, 115), r.Intn(32));
            // Vary positions at sub-cell precision too; garage/depot footprints differ.
            u.Pos += new Vec(Fixed.FromRaw(r.Intn(1024)), Fixed.FromRaw(r.Intn(1024)));
        }
        for (var i = 0; i < 250; i++)
        {
            var u = i % 5 == 0
                ? w.SpawnCrewed(1, "technical", r.Range(2, 125), r.Range(2, 125)).Vehicle
                : w.Spawn(1, "Rifleman", r.Range(2, 125), r.Range(2, 125));
            w.Order(1, CommandKind.Move, [u], cell: new Cell(r.Range(2, 125), r.Range(2, 125)));
        }
        return w;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BucketsMatchFullScanEveryTick(int seed)
    {
        var a = RandomWorld(seed, false);
        var b = RandomWorld(seed, true);
        for (var t = 0; t < 300; t++)
        {
            a.Step();
            b.Step();
            Assert.True(a.Hash() == b.Hash(), $"seed {seed}, tick {a.Tick}");
        }
    }

    private static MoveScratch Scratch(long radius = 1024)
    {
        var m = new MoveScratch();
        m.Reset(1, 128, 128);
        m.N = 1;
        m.X[0] = 32 * 1024;
        m.Y[0] = 32 * 1024;
        m.R[0] = (int)radius;
        m.Cls[0] = -1;
        return m;
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(10000)]
    public void CandidatesAreSortedUniqueAndCoverCircle(long radius)
    {
        var m = Scratch(radius);
        var r = new Rand(42);
        for (var k = 0; k < 200; k++)
        {
            long x = r.Intn(128 * 1024), y = r.Intn(128 * 1024);
            m.Boxes.Add((x, y, x + r.Range(1024, 20000), y + r.Range(1024, 20000), (uint)k + 1));
        }
        m.FileBoxes(128, 128);
        for (var y = 0; y < 128 * 1024; y += 4001)
        {
            for (var x = 0; x < 128 * 1024; x += 4095)
            {
                var list = m.BoxesNear(x, y);
                Assert.True(list.Zip(list.Skip(1)).All(p => p.First < p.Second));
                var candidates = new List<int>();
                for (var k = m.NextBox(x, y, radius, -1); k < m.Boxes.Count; k = m.NextBox(x, y, radius, k))
                {
                    Assert.True(candidates.Count == 0 || k > candidates[^1]);
                    candidates.Add(k);
                }
                for (var k = 0; k < m.Boxes.Count; k++)
                {
                    var b = m.Boxes[k];
                    if (x + radius > b.X0 && x - radius < b.X1 && y + radius > b.Y0 && y - radius < b.Y1)
                    {
                        Assert.Contains(k, candidates);
                    }
                }
            }
        }
    }

    [Fact]
    public void ThreeBoxCornerMatchesReference()
    {
        var a = Scratch();
        a.Boxes.Add((31 * 1024, 30 * 1024, 32 * 1024, 34 * 1024, 1));
        a.Boxes.Add((32 * 1024, 31 * 1024, 34 * 1024, 32 * 1024, 2));
        a.Boxes.Add((32 * 1024, 32 * 1024, 34 * 1024, 34 * 1024, 3));
        var b = Scratch();
        b.Boxes.AddRange(a.Boxes);
        a.FileBoxes(128, 128);
        b.FileBoxes(128, 128);
        var w = Arena.New(128);
        w.PushOutForTest(a, 0);
        w.FullBoxScan = true;
        w.PushOutForTest(b, 0);
        Assert.Equal((b.X[0], b.Y[0]), (a.X[0], a.Y[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChainedPushAcrossBucketsMatchesReference(bool factoryExit)
    {
        var a = Scratch();
        // The first large box throws the body across several buckets into two later boxes.
        a.Boxes.Add((16 * 1024, 0, 70 * 1024, 100 * 1024, 1));
        var near = factoryExit ? 30 : 14;
        a.Boxes.Add((near * 1024, 31 * 1024, (near + 4) * 1024, 34 * 1024, 2));
        a.Boxes.Add((near * 1024, 29 * 1024, (near + 5) * 1024, 33 * 1024, 3));
        if (factoryExit)
        {
            // The factory alone must leave the body unchanged, even deep inside its box.
            var only = Scratch();
            only.Boxes.Add(a.Boxes[0]);
            only.Out[0] = 1;
            only.FileBoxes(128, 128);
            Arena.New(128).PushOutForTest(only, 0);
            Assert.Equal((32 * 1024L, 32 * 1024L), (only.X[0], only.Y[0]));
        }
        var b = Scratch();
        b.Boxes.AddRange(a.Boxes);
        a.Out[0] = b.Out[0] = factoryExit ? 1u : 0u;
        a.FileBoxes(128, 128);
        b.FileBoxes(128, 128);
        if (!factoryExit) { Assert.DoesNotContain(1, a.BoxesNear(a.X[0], a.Y[0])); }
        var w = Arena.New(128);
        w.PushOutForTest(a, 0);
        w.FullBoxScan = true;
        w.PushOutForTest(b, 0);
        Assert.Equal((b.X[0], b.Y[0]), (a.X[0], a.Y[0]));
        Assert.NotEqual((32 * 1024L, 32 * 1024L), (a.X[0], a.Y[0]));
        if (factoryExit) { Assert.InRange(a.X[0], 13 * 1024L, 36 * 1024L); }
    }
}
