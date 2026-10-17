using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Routing: HPA* routes against plain A*, the road layer, terrain classes and clearance widths,
// incremental rebuilds, the request queue, group moves in formation, and determinism.
public class RoutingTests(ITestOutputHelper output)
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static int TerrainClass(string id)
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

    // RandomGrid scatters rocks (rectangles and blobs) over about a quarter of a size x size map.
    private static Grid RandomGrid(int size, int seed)
    {
        var rng = new Random(seed);
        var g = new Grid(size, size);
        var blocked = 0;
        while (blocked < size * size / 4)
        {
            int x = rng.Next(size), y = rng.Next(size), w = 1 + rng.Next(12), h = 1 + rng.Next(12);
            for (var yy = y; yy < Math.Min(y + h, size); yy++)
            {
                for (var xx = x; xx < Math.Min(x + w, size); xx++)
                {
                    if (g.Passable(new Cell(xx, yy)))
                    {
                        g.Set(new Cell(xx, yy), Terrain.Blocked);
                        blocked++;
                    }
                }
            }
        }
        return g;
    }

    // RandomTerrain makes rolling hills with a few roads and forest patches.
    private static TerrainMap RandomTerrain(int size, int seed)
    {
        var rng = new Random(seed);
        var corners = new short[(size + 1) * (size + 1)];
        double a = rng.NextDouble() * 6, b = rng.NextDouble() * 6;
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                corners[y * (size + 1) + x] = (short)(2200 * Math.Sin(x / 9.0 + a) * Math.Cos(y / 11.0 + b) + 900 * Math.Sin((x + y) / 5.0));
            }
        }
        var flags = new byte[size * size];
        for (var i = 0; i < 6; i++)
        {
            int x = rng.Next(size), y = rng.Next(size), r = 3 + rng.Next(8), d = 1 + rng.Next(3);
            for (var yy = Math.Max(y - r, 0); yy < Math.Min(y + r, size); yy++)
            {
                for (var xx = Math.Max(x - r, 0); xx < Math.Min(x + r, size); xx++)
                {
                    flags[yy * size + xx] = (byte)d;
                }
            }
        }
        for (var i = 0; i < 3; i++)
        {
            var y = rng.Next(size);
            for (var x = 0; x < size; x++)
            {
                y = Math.Clamp(y + rng.Next(3) - 1, 0, size - 1);
                flags[y * size + x] |= 1 << 2;
                if (y + 1 < size)
                {
                    flags[(y + 1) * size + x] |= 1 << 2;
                }
            }
        }
        return new TerrainMap(size, size, corners, flags, Rules);
    }

    // FullRoute plans a route like the path system and refines every leg; returns the cells.
    private static List<int> FullRoute(Router r, int k, int s, int d)
    {
        var path = new List<int>();
        var route = new List<int>();
        r.FindRoute(k, s, d, path, route);
        var idx = 0;
        var from = s;
        if (route.Count > 0)
        {
            Assert.True(r.Refine(k, from, route, ref idx, route.Count, path));
        }
        r.Smooth(k, s, path, 0);
        return path;
    }

    private static int PathCost(NavGrid nav, int k, int s, List<int> path)
    {
        var cost = 0;
        var c = s;
        foreach (var n in path)
        {
            int cx = c % nav.W, cy = c / nav.W, nx = n % nav.W, ny = n / nav.W;
            Assert.True(Math.Max(Math.Abs(cx - nx), Math.Abs(cy - ny)) == 1, "path cells must be neighbours");
            var st = nav.Step(k, cx, cy, nx - cx, ny - cy);
            Assert.True(st > 0, "path steps must be passable");
            cost += st;
            c = n;
        }
        return cost;
    }

    private (double Mean, double Worst, int Count) Quality(Grid g, TerrainMap? t, int tc, int width, int seed, int pairs)
    {
        var r = new Router(g, t, []);
        var k = r.Nav.Class(tc, width);
        var plain = new LocalSearch();
        var rng = new Random(seed);
        var all = (0, 0, g.Width - 1, g.Height - 1);
        double sum = 0, worst = 0;
        var n = 0;
        while (n < pairs)
        {
            int s = rng.Next(g.Width * g.Height), d = rng.Next(g.Width * g.Height);
            if (!r.Passable(k, s) || !r.Passable(k, d) || s == d)
            {
                continue;
            }
            var best = new List<int>();
            if (!plain.AStar(r.Nav, k, s, d, all, best, out var optimal))
            {
                continue;
            }
            var path = FullRoute(r, k, s, d);
            Assert.Equal(d, path[^1]);
            var cost = PathCost(r.Nav, k, s, path);
            Assert.Equal(optimal, PathCost(r.Nav, k, s, best));
            var ratio = (double)cost / optimal;
            sum += ratio;
            worst = Math.Max(worst, ratio);
            n++;
        }
        return (sum / n, worst, n);
    }

    [Fact]
    public void RoutesAreWithinFivePercentOfAStarOnRandomMaps()
    {
        for (var seed = 1; seed <= 4; seed++)
        {
            var (mean, worst, n) = Quality(RandomGrid(192, seed), null, 0, 1, seed, 60);
            output.WriteLine($"flat seed {seed}: {n} routes, mean {mean:F4}, worst {worst:F4}");
            Assert.True(mean <= 1.02 && worst <= 1.08, $"mean {mean}, worst {worst}");
        }
        for (var seed = 1; seed <= 2; seed++)
        {
            // Big enough for the corridor search of far routes.
            var (mean, worst, n) = Quality(RandomGrid(640, seed), null, 0, 1, seed, 20);
            output.WriteLine($"big flat seed {seed}: {n} routes, mean {mean:F4}, worst {worst:F4}");
            Assert.True(mean <= 1.02 && worst <= 1.08, $"mean {mean}, worst {worst}");
            var t = RandomTerrain(640, seed);
            (mean, worst, n) = Quality(RandomGrid(640, seed + 20), t, TerrainClass("wheeled"), 2, seed, 20);
            output.WriteLine($"big terrain seed {seed}: {n} routes, mean {mean:F4}, worst {worst:F4}");
            Assert.True(mean <= 1.02 && worst <= 1.08, $"mean {mean}, worst {worst}");
        }
        for (var seed = 1; seed <= 3; seed++)
        {
            var size = 160;
            var t = RandomTerrain(size, seed);
            var (mean, worst, n) = Quality(RandomGrid(size, seed + 10), t, TerrainClass("wheeled"), 2, seed, 40);
            output.WriteLine($"terrain seed {seed}: {n} routes, mean {mean:F4}, worst {worst:F4}");
            Assert.True(mean <= 1.02 && worst <= 1.08, $"mean {mean}, worst {worst}");
        }
    }

    // Band builds a w x h terrain: a band of gentle checkerboard slope between x0 and x1 (no
    // climbing penalty: every cell's centre is at the same height) and the given flags.
    private static TerrainMap Band(int w, int h, int x0, int x1, byte[] flags)
    {
        var corners = new short[(w + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        {
            for (var x = x0 + 1; x < x1; x++)
            {
                corners[y * (w + 1) + x] = (short)((x + y) % 2 * 150);
            }
        }
        return new TerrainMap(w, h, corners, flags, Rules);
    }

    [Fact]
    public void VehiclesTakeTheMountainRoadInfantryGoStraight()
    {
        const int W = 96, H = 48;
        var flags = new byte[W * H];
        for (var x = 0; x < W; x++)
        {
            flags[30 * W + x] |= 1 << 2;
            flags[31 * W + x] |= 1 << 2;
        }
        var t = Band(W, H, 30, 66, flags);
        var r = new Router(new Grid(W, H), t, []);
        var wheeled = r.Nav.Class(TerrainClass("wheeled"), 2);
        var foot = r.Nav.Class(TerrainClass("foot"), 1);
        int s = 10 * W + 5, d = 10 * W + 90;

        var car = FullRoute(r, wheeled, s, d);
        var band = car.Where(c => c % W >= 31 && c % W < 65).ToList();
        Assert.NotEmpty(band);
        Assert.All(band, c => Assert.True(t.Road(new Cell(c % W, c / W)), $"off-road at {c % W},{c / W}"));

        var walker = FullRoute(r, foot, s, d);
        Assert.All(walker, c => Assert.True(c / W < 20, $"the walker strayed to {c % W},{c / W}"));

        // Both as good as plain A*.
        foreach (var (k, path) in new[] { (wheeled, car), (foot, walker) })
        {
            var best = new List<int>();
            new LocalSearch().AStar(r.Nav, k, s, d, (0, 0, W - 1, H - 1), best, out var optimal);
            Assert.True(PathCost(r.Nav, k, s, path) * 100 <= optimal * 105);
        }
    }

    [Fact]
    public void CliffsBlockEveryoneAndDenseForestTheHeavyTrucks()
    {
        const int W = 64, H = 40;
        var corners = new short[(W + 1) * (H + 1)];
        for (var y = 0; y <= H; y++)
        {
            if (y < 35 || y > 38)
            {
                corners[y * (W + 1) + 31] = 600; // a cliff ridge, x 30-31, but for a gap at y 35-37
            }
        }
        var flags = new byte[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 40; x < 44; x++)
            {
                flags[y * W + x] = (byte)(y >= 2 && y <= 4 ? 0 : 3); // dense forest but for a gap at y 2-4
            }
        }
        var t = new TerrainMap(W, H, corners, flags, Rules);
        var r = new Router(new Grid(W, H), t, []);
        int s = 20 * W + 5, d = 20 * W + 60;
        var cliff = TerrainClass("foot");
        foreach (var (id, width) in new[] { ("foot", 1), ("wheeled", 2), ("heavy_wheeled", 3) })
        {
            var k = r.Nav.Class(TerrainClass(id), width);
            var path = FullRoute(r, k, s, d);
            Assert.Equal(d, path[^1]);
            Assert.All(path, c => Assert.NotEqual(SlopeClass.Cliff, t.Slope(cliff, new Cell(c % W, c / W))));
            var dense = path.Count(c => t.Forest(new Cell(c % W, c / W)) == 3);
            if (id == "heavy_wheeled")
            {
                Assert.Equal(0, dense); // through the gap
                Assert.Contains(path, c => c % W == 41 && c / W == 3);
            }
            else
            {
                Assert.True(dense > 0, $"{id} should push through the forest"); // dearer, but much shorter
            }
        }
    }

    [Fact]
    public void ClearanceWidthPicksTheGapEachClassFits()
    {
        const int W = 60, H = 40;
        var g = new Grid(W, H);
        for (var y = 0; y < H; y++)
        {
            if (y != 5 && y != 15 && y != 16 && (y < 30 || y > 32))
            {
                g.Set(new Cell(30, y), Terrain.Blocked);
            }
        }
        var r = new Router(g, null, []);
        int s = 5 * W + 5, d = 5 * W + 55;
        foreach (var (width, lo, hi) in new[] { (1, 5, 5), (2, 15, 16), (3, 30, 32) })
        {
            var k = r.Nav.Class(0, width);
            var path = FullRoute(r, k, s, d);
            Assert.Equal(d, path[^1]);
            var at = path.Where(c => c % W == 30).Select(c => c / W).ToList();
            Assert.NotEmpty(at);
            Assert.All(at, y => Assert.InRange(y, lo, hi));
        }
    }

    [Fact]
    public void IncrementalRebuildMatchesAFreshBuild()
    {
        const int size = 128;
        var g = RandomGrid(size, 7);
        var t = RandomTerrain(size, 7);
        var r1 = new Router(g, t, []);
        var classes = new[] { (TerrainClass("foot"), 1), (TerrainClass("wheeled"), 2), (TerrainClass("heavy_wheeled"), 3) };
        foreach (var (tc, w) in classes)
        {
            r1.Nav.Class(tc, w);
        }
        r1.BuildAll();
        var rng = new Random(3);
        for (var i = 0; i < 60; i++)
        {
            var c = new Cell(rng.Next(size), rng.Next(size));
            g.Set(c, g.Passable(c) ? Terrain.Blocked : Terrain.Clear);
        }
        for (var i = 0; i < 40; i++)
        {
            t.SetForest(new Cell(rng.Next(size), rng.Next(size)), rng.Next(4));
        }
        t.SetPad(new Cell(40, 40), 6, 5, Fixed.FromInt(1));
        t.SetPad(new Cell(95, 10), 4, 4, Fixed.Zero);
        Assert.True(r1.DrainChanges());
        r1.BuildAll();

        var r2 = new Router(g, t, []);
        foreach (var (tc, w) in classes)
        {
            r2.Nav.Class(tc, w);
        }
        r2.BuildAll();
        for (var k = 0; k < classes.Length; k++)
        {
            for (var i = 0; i < size * size; i++)
            {
                Assert.Equal(r2.Nav.Cost(k, i), r1.Nav.Cost(k, i));
            }
            Assert.Equal(r2.Nav.MinCost(k), r1.Nav.MinCost(k));
            var h1 = r1.Graph(k);
            var h2 = r2.Graph(k);
            for (var sec = 0; sec < h1.SX * h1.SY; sec++)
            {
                Assert.Equal(h2.Get(sec).Nodes, h1.Get(sec).Nodes);
                Assert.Equal(h2.Get(sec).Cost, h1.Get(sec).Cost);
            }
            var rr = new Random(k);
            for (var i = 0; i < 20; i++)
            {
                int s = rr.Next(size * size), d = rr.Next(size * size);
                if (r1.Passable(k, s) && r1.Passable(k, d))
                {
                    Assert.Equal(FullRoute(r2, k, s, d), FullRoute(r1, k, s, d));
                }
            }
        }
    }

    // GroupWorld is an open 128 x 96 arena with two rock walls, and player 1's army in a block on
    // the left: 40 riflemen and 6 crewed technicals.
    private static (World W, List<Unit> Army) GroupWorld(bool prepare)
    {
        var w = Arena.New(128);
        for (var y = 0; y < 96; y++)
        {
            if (y < 40 || y > 48)
            {
                w.Grid.Set(new Cell(50, y), Terrain.Blocked);
            }
            if (y > 70 || y < 20)
            {
                w.Grid.Set(new Cell(80, y), Terrain.Blocked);
            }
        }
        var army = new List<Unit>();
        for (var i = 0; i < 40; i++)
        {
            army.Add(w.Spawn(1, "Rifleman", 10 + i % 8, 30 + i / 8));
        }
        for (var i = 0; i < 6; i++)
        {
            army.Add(w.SpawnCrewed(1, "technical", 20 + i % 3 * 3, 40 + i / 3 * 3).Vehicle);
        }
        if (prepare)
        {
            w.PrepareRouting();
        }
        return (w, army);
    }

    private static List<ulong> Play(World w, IEnumerable<Unit> order, int ticks)
    {
        w.Order(1, CommandKind.Move, order, cell: new Cell(110, 60));
        var hashes = new List<ulong>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            hashes.Add(w.Hash());
        }
        return hashes;
    }

    [Fact]
    public void GroupMoveArrivesInFormationInfantryInFront()
    {
        var (w, army) = GroupWorld(false);
        Play(w, army, 1);
        var group = army[0].Group;
        Assert.NotEqual(0u, group);
        Assert.All(army, u => Assert.Equal(group, u.Group));
        // Slots: distinct cells; along the move direction every rifleman ahead of every technical.
        var slots = army.Select(u => u.Dest).ToList();
        Assert.Equal(slots.Count, slots.Distinct().Count());
        var dir = new Cell(110, 60).Center() - new Vec(Fixed.FromInt(15), Fixed.FromInt(36));
        long Along(Cell c) => (c.Center() - new Cell(110, 60).Center()).X.Raw * dir.X.Raw + (c.Center() - new Cell(110, 60).Center()).Y.Raw * dir.Y.Raw;
        var infantry = army.Where(u => u.Def.Archetype.Id != "technical").Select(u => Along(u.Dest)).ToList();
        var vehicles = army.Where(u => u.Def.Archetype.Id == "technical").Select(u => Along(u.Dest)).ToList();
        var cell = Fixed.OneRaw * dir.Len().Raw; // one cell along, rounding slots to cells
        Assert.True(infantry.Min() > vehicles.Max() - cell, "infantry ranks in front of the vehicles");
        Assert.True(infantry.Average() > vehicles.Max() + 2 * cell);

        w.Run(1500);
        Assert.All(army, u =>
        {
            Assert.Equal(OrderKind.Idle, u.Order.Kind);
            Assert.True(World.Within(u.Pos, slots[army.IndexOf(u)].Center(), Fixed.FromInt(3)), $"unit {u.Id} at {u.Pos} far from its slot");
        });
        Assert.All(w.Units, u => Assert.True(w.Grid.Passable(Cell.Of(u.Pos)) || u.Inside != 0));
    }

    [Fact]
    public void GroupMovesAreDeterministicWhateverTheOrderOfUnitsOrCaches()
    {
        var (w1, a1) = GroupWorld(false);
        var h1 = Play(w1, a1, 300);
        var (w2, a2) = GroupWorld(false);
        var h2 = Play(w2, Enumerable.Reverse(a2), 300);
        var (w3, a3) = GroupWorld(true);
        var h3 = Play(w3, a3, 300);
        Assert.Equal(h1, h2);
        Assert.Equal(h1, h3);
        Assert.NotEqual(a1[0].Pos, new Cell(10, 30).Center()); // it moved
    }

    [Fact]
    public void RequestsAreServedInOrderWithinTheBudget()
    {
        var w = Arena.New(64);
        var men = new List<Unit>();
        for (var i = 0; i < 150; i++)
        {
            men.Add(w.Spawn(1, "Rifleman", i % 30, i / 30));
        }
        foreach (var m in men)
        {
            w.Order(1, CommandKind.Move, [m], cell: new Cell(m.Pos.X.Floor() + 3, 40));
        }
        w.Step();
        Assert.Equal(64, w.PathRequests);
        for (var i = 0; i < men.Count; i++)
        {
            Assert.Equal(i >= 64, men[i].PathPending); // the lowest IDs first
        }
        w.Step();
        Assert.Equal(64, w.PathRequests);
        w.Step();
        Assert.Equal(22, w.PathRequests);
        Assert.All(men, m => Assert.True(m.Moving));
    }

    [Fact]
    public void ChaseReplansAreStaggeredByID()
    {
        var w = Arena.New(64);
        var hunters = new List<Unit>();
        for (var i = 0; i < 5; i++)
        {
            hunters.Add(w.Spawn(1, "Rifleman", 2 + i, 2));
        }
        var target = w.Spawn(2, "Rifleman", 60, 60);
        w.Order(1, CommandKind.Attack, hunters, target: target.Id);
        w.Run(2);
        foreach (var h in hunters)
        {
            Assert.Equal(20 + h.Id % 20, h.RepathAt);
        }
    }

    [Fact]
    public void RoutesGoAroundStructuresWhileTheyStand()
    {
        var w = Arena.New(48);
        var depot = w.Spawn(1, "supply_depot", 20, 9);
        var man = w.Spawn(1, "Rifleman", 5, 10);
        w.Order(1, CommandKind.Move, [man], cell: new Cell(40, 10));
        w.Step();
        var origin = World.FootprintOrigin(depot);
        bool OnDepot(Cell c) => c.X >= origin.X && c.Y >= origin.Y && c.X < origin.X + 2 && c.Y < origin.Y + 2;
        Assert.DoesNotContain(man.Path, OnDepot);

        depot.HP = 0;
        w.Step(); // removed at the end of this tick
        w.Order(1, CommandKind.Move, [man], cell: new Cell(5, 10));
        w.Step();
        w.Order(1, CommandKind.Move, [man], cell: new Cell(40, 10));
        w.Step();
        Assert.Contains(man.Path, OnDepot); // the straight line again
    }
}
