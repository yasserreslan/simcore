using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Straight routes (docs/design.md, "Straight routes"): over ground flat enough to walk and drive normally every class goes in a
// straight line at a steady heading; real hills, woods, cliffs and rock still shape the way; the smoothing and the steering's
// look-ahead never cut a blocked cell or a corner; all of it the same on every run.
public class StraightRouteTests(ITestOutputHelper output)
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int M = 512; // raw height per metre

    private static UnitAiRules Ai => TestContent.Mvp.UnitAi;

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

    // Meadow: w x h cells of flat ground whose corners are jittered by up to 5 cm either way (seeded), plus lift(x, y) raw.
    private static TerrainMap Meadow(int w, int h, int seed, Func<int, int, int>? lift = null, byte[]? flags = null)
    {
        var rng = new Random(seed);
        var corners = new int[(w + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        {
            for (var x = 0; x <= w; x++)
            {
                corners[y * (w + 1) + x] = rng.Next(-5 * M / 100, 5 * M / 100 + 1) + (lift?.Invoke(x, y) ?? 0);
            }
        }
        return new TerrainMap(w, h, corners, flags ?? new byte[w * h], Rules);
    }

    // Plan routes class k from s to d as the path system does (World.RouteCellsFor), with the content's tolerance and slack: the
    // route, then every leg refined three at a time, each chunk added when fewer than 8 cells are left beyond a unit's aim and
    // the lot smoothed again from there. Returns the cells after s.
    private static List<int> Plan(Router r, int k, int s, int d)
    {
        var path = new List<int>();
        var route = new List<int>();
        r.FindRoute(k, s, d, path, route);
        var idx = 0;
        if (route.Count > 0)
        {
            Assert.True(r.Refine(k, s, route, ref idx, 3, path));
        }
        r.Smooth(k, s, path, 0, Ai.SmoothTolerance, Ai.SmoothSlack);
        var chunk = new List<int>();
        while (idx < route.Count)
        {
            chunk.Clear();
            var from = path.Count > 0 ? path[^1] : s;
            Assert.True(r.Refine(k, from, route, ref idx, 3, chunk));
            var aimAt = Math.Max(0, path.Count - 7);
            var aim = aimAt == 0 ? s : path[aimAt - 1];
            chunk.InsertRange(0, path.GetRange(aimAt, path.Count - aimAt));
            path.RemoveRange(aimAt, path.Count - aimAt);
            r.Smooth(k, aim, chunk, 0, Ai.SmoothTolerance, Ai.SmoothSlack);
            path.AddRange(chunk);
        }
        return path;
    }

    // Off is how far (cells) the farthest cell of a path lies from the straight line between s and d.
    private static double Off(int w, List<int> path, int s, int d)
    {
        double sx = s % w, sy = s / w, lx = d % w - sx, ly = d / w - sy, l = Math.Sqrt(lx * lx + ly * ly);
        return path.Max(c => Math.Abs((c % w - sx) * ly - (c / w - sy) * lx) / l);
    }

    // Walk checks a path is a chain of neighbouring cells, every step one the class can take, and returns its cost.
    private static int Walk(NavGrid nav, int k, int s, List<int> path)
    {
        var cost = 0;
        var c = s;
        foreach (var n in path)
        {
            int cx = c % nav.W, cy = c / nav.W, nx = n % nav.W, ny = n / nav.W;
            Assert.True(Math.Max(Math.Abs(cx - nx), Math.Abs(cy - ny)) == 1, $"not neighbours: {cx},{cy} -> {nx},{ny}");
            var st = nav.Step(k, cx, cy, nx - cx, ny - cy);
            Assert.True(st > 0, $"a step it can't take: {cx},{cy} -> {nx},{ny}");
            cost += st;
            c = n;
        }
        return cost;
    }

    [Fact]
    public void TheContentHoldsTheStraightRouteNumbers()
    {
        Assert.Equal(2, Ai.SmoothTolerance);
        Assert.Equal(5, Ai.SmoothSlack);
        Assert.Equal(100, Ai.AimTicks);
        Assert.Equal(Fixed.FromInt(12), Ai.AimMin);
        Assert.Equal(Fixed.FromInt(24), Ai.AimMax);
        foreach (var c in Rules.Classes)
        {
            Assert.Equal((5, 10), (c.UphillFrom, c.UphillFull));
        }
    }

    [Fact]
    public void ClimbsInTheDeadZoneCostNothingAndRealClimbsPayInFull()
    {
        var foot = Rules.Classes[TerrainClass("foot")];
        // Rises are of cell-centre sums (four corners): a 2 m straight step climbing g percent rises 4 x g x 1,024 / 100.
        int Rise(double grade) => (int)(grade * 4 * Fixed.OneRaw / 100);
        Assert.Equal(0, foot.ClimbPercent(-Rise(20), diagonal: false)); // downhill
        Assert.Equal(0, foot.ClimbPercent(Rise(2.5), diagonal: false)); // a 5 cm rise over a cell
        Assert.Equal(0, foot.ClimbPercent(Rise(5), diagonal: false));   // 10 cm: still free
        Assert.InRange(foot.ClimbPercent(Rise(7.5), diagonal: false), 14, 16); // half way up the ramp: half of 30
        Assert.Equal(30, foot.ClimbPercent(Rise(10), diagonal: false));
        Assert.Equal(30, foot.ClimbPercent(Rise(40), diagonal: false));
        Assert.Equal(0, foot.ClimbPercent(Rise(6.5), diagonal: true)); // a diagonal step is longer: 6.5% of a straight step is under 5% of it
        // Without a dead zone (the old rules) any rise pays it all.
        var old = new MoveClass
        {
            Id = "old", Locomotor = foot.Locomotor, MinRadius = foot.MinRadius, GentleFrom = 10, SteepFrom = 25, CliffFrom = 45,
            SpeedUp = foot.SpeedUp, SpeedDown = foot.SpeedDown, Cost = foot.Cost, UphillCostPercent = 30, RoadCost = foot.RoadCost,
            RoadSpeed = foot.RoadSpeed, ForestCostPercent = foot.ForestCostPercent, ForestSpeedPercent = foot.ForestSpeedPercent,
            ForestBlockedFrom = foot.ForestBlockedFrom,
        };
        Assert.Equal(30, old.ClimbPercent(1, diagonal: false));

        // On the map: a 5 cm step costs the flat cell's 10, a 40 cm one 10 plus 30%.
        var t = new TerrainMap(3, 1, [0, 0, 5 * M / 100, 45 * M / 100, 0, 0, 5 * M / 100, 45 * M / 100], new byte[3], Rules);
        var k = TerrainClass("foot");
        Assert.Equal(10, t.StepCost(k, new Cell(0, 0), new Cell(1, 0)));
        Assert.True(t.Grade(new Cell(2, 0)) >= 10);
        var steep = t.Cost(k, new Cell(2, 0));
        Assert.Equal(steep + steep * 30 / 100, t.StepCost(k, new Cell(1, 0), new Cell(2, 0)));
    }

    [Fact]
    public void RulesRefuseADeadZoneThatEndsBeforeItStarts()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        var bad = text.Replace("\"uphillFrom\": 5, \"uphillFull\": 10", "\"uphillFrom\": 12, \"uphillFull\": 10");
        Assert.NotEqual(text, bad);
        var e = Assert.Throws<ContentException>(() => TerrainLoader.ParseRules(bad, "rules.json"));
        Assert.Contains("uphillFull", e.Message);
    }

    [Fact]
    public void ANoisyMeadowIsCrossedInAStraightLineByEveryClass()
    {
        const int W = 220, H = 140;
        var t = Meadow(W, H, 7);
        var r = new Router(new Grid(W, H), t, []);
        (int, int, int, int)[] trips = [(8, 20, 210, 120), (12, 130, 205, 14), (6, 70, 214, 64), (30, 8, 70, 131), (100, 60, 128, 72)];
        foreach (var mc in Rules.Classes)
        {
            for (var width = 1; width <= 3; width++)
            {
                var k = r.Nav.Class(TerrainClass(mc.Id), width);
                foreach (var (sx, sy, dx, dy) in trips)
                {
                    int s = sy * W + sx, d = dy * W + dx;
                    var path = Plan(r, k, s, d);
                    Assert.Equal(d, path[^1]);
                    Walk(r.Nav, k, s, path);
                    var off = Off(W, path, s, d);
                    Assert.True(off <= 0.5, $"{mc.Id} width {width} {sx},{sy} -> {dx},{dy}: a cell {off:F2} off the line");
                    Assert.Equal(Math.Max(Math.Abs(dx - sx), Math.Abs(dy - sy)), path.Count); // one raster line, no step to spare
                }
            }
        }
    }

    [Fact]
    public void AFlatMapsOpenGroundIsCrossedInAStraightLineToo()
    {
        const int W = 320, H = 200;
        var g = new Grid(W, H);
        var r = new Router(g, null, []);
        var k = r.Nav.Class(0, 2);
        (int, int, int, int)[] trips = [(10, 20, 300, 170), (300, 10, 15, 190), (5, 100, 315, 96)];
        foreach (var (sx, sy, dx, dy) in trips)
        {
            int s = sy * W + sx, d = dy * W + dx;
            var path = Plan(r, k, s, d);
            Assert.Equal(d, path[^1]);
            var off = Off(W, path, s, d);
            output.WriteLine($"{sx},{sy} -> {dx},{dy}: {path.Count} cells, {off:F3} off the line");
            Assert.True(off <= 0.501, $"{sx},{sy} -> {dx},{dy}: {off:F3} off the line");
            Assert.Equal(Math.Max(Math.Abs(dx - sx), Math.Abs(dy - sy)), path.Count);
        }
        // A rock in the way: round it, and straight on either side of it (two lines, not a staircase).
        for (var y = 90; y < 110; y++)
        {
            for (var x = 150; x < 160; x++)
            {
                g.Set(new Cell(x, y), Terrain.Blocked);
            }
        }
        r = new Router(g, null, []);
        k = r.Nav.Class(0, 1);
        var around = Plan(r, k, 100 * W + 5, 100 * W + 315);
        Assert.All(around, c => Assert.True(g.Passable(new Cell(c % W, c / W))));
        var bends = Bends(W, [100 * W + 5, .. around]);
        output.WriteLine($"round the rock: {around.Count} cells, {bends} bends");
        Assert.True(bends <= 2, $"{bends} bends round one rock: a staircase");
    }

    // Bends counts the inner points of a path's cells simplified to the polyline within a cell of every one (Douglas-Peucker):
    // the turns a unit makes along it (the raster of a straight line has none).
    private static int Bends(int w, List<int> cells)
    {
        var keep = new bool[cells.Count];
        void Simplify(int a, int b)
        {
            double ax = cells[a] % w, ay = cells[a] / w, dx = cells[b] % w - ax, dy = cells[b] / w - ay, l = Math.Sqrt(dx * dx + dy * dy);
            int far = -1;
            var worst = 1.0;
            for (var i = a + 1; i < b; i++)
            {
                var off = Math.Abs((cells[i] % w - ax) * dy - (cells[i] / w - ay) * dx) / Math.Max(l, 1e-9);
                if (off > worst)
                {
                    (worst, far) = (off, i);
                }
            }
            if (far >= 0)
            {
                keep[far] = true;
                Simplify(a, far);
                Simplify(far, b);
            }
        }
        Simplify(0, cells.Count - 1);
        return keep.Count(x => x);
    }

    [Fact]
    public void VehiclesAndSoldiersCrossTheMeadowAtASteadyHeading()
    {
        const int Size = 170;
        foreach (var kind in new[] { "humvee", "main_battle_tank", "sentinel_cargo_truck", "Rifleman" })
        {
            var w = Arena.NewSentinel(Size);
            w.AttachTerrain(Meadow(Size, Size, 11));
            var line = new Vec(Fixed.FromInt(140), Fixed.FromInt(50));
            Facing.TryFromDirection(line, out var face);
            var u = kind == "Rifleman" ? w.Spawn(1, kind, 12, 40, face) : w.SpawnCrewed(1, kind, 12, 40, face).Vehicle;
            var start = u.Pos;
            w.Order(1, CommandKind.Move, [u], cell: new Cell(152, 90));
            var want = Math.Atan2(50, 140) * 180 / Math.PI;
            double worstHeading = 0, worstOff = 0;
            var facings = 0;
            var lastFacing = u.Facing;
            for (var i = 0; i < 3000 && (i < 5 || u.Moving); i++)
            {
                w.Step();
                var p = u.Pos - start;
                var along = (p.X.Raw * 140.0 + p.Y.Raw * 50.0) / Math.Sqrt(140 * 140 + 50 * 50) / Fixed.OneRaw;
                var total = Math.Sqrt(140 * 140 + 50 * 50);
                worstOff = Math.Max(worstOff, Math.Abs(p.X.Raw * 50.0 - p.Y.Raw * 140.0) / Math.Sqrt(140 * 140 + 50 * 50) / Fixed.OneRaw);
                if (along > 4 && along < total - 6 && u.Vel.LenSq() > 0)
                {
                    var h = kind == "Rifleman" ? u.Vel : u.Heading;
                    worstHeading = Math.Max(worstHeading, Math.Abs(Math.Atan2(h.Y.Raw, h.X.Raw) * 180 / Math.PI - want));
                    facings += u.Facing != lastFacing ? 1 : 0;
                }
                lastFacing = u.Facing;
            }
            output.WriteLine($"{kind}: heading off the line at most {worstHeading:F2} degrees, {worstOff:F2} cells off it, {facings} facing changes");
            Assert.False(u.Moving);
            Assert.True(Within(u.Pos, new Cell(152, 90).Center(), 2), $"{kind} stopped at {u.Pos}");
            Assert.True(worstHeading <= 3.5, $"{kind}: heading {worstHeading:F2} degrees off the line");
            Assert.True(worstOff <= 1.0, $"{kind}: {worstOff:F2} cells off the line");
            Assert.True(facings <= 1, $"{kind}: {facings} facing changes on a straight line");
        }
    }

    private static bool Within(Vec a, Vec b, int cells) => (a - b).LenSq() <= (long)cells * cells * Fixed.OneRaw * Fixed.OneRaw;

    [Fact]
    public void RealHillsWoodsAndCliffsStillShapeRoutes()
    {
        const int W = 200, H = 120;
        // A hill 10 m high (radius 25 cells: its flanks 20 to 40% grade) on the line, a grove across it beyond, a cliff ridge with
        // a gap further on.
        static int Hill(int x, int y)
        {
            var d2 = (x - 70) * (x - 70) + (y - 60) * (y - 60);
            return d2 >= 25 * 25 ? 0 : 10 * M * (25 * 25 - d2) / (25 * 25);
        }
        static int Ridge(int x, int y) => x is >= 150 and <= 152 && y is < 20 or > 26 ? 30 * M : 0;
        var flags = new byte[W * H];
        for (var y = 50; y < 70; y++)
        {
            for (var x = 108; x < 126; x++)
            {
                flags[y * W + x] = 2; // a grove across the line
            }
        }
        var t = Meadow(W, H, 3, (x, y) => Hill(x, y) + Ridge(x, y), flags);
        var r = new Router(new Grid(W, H), t, []);
        int s = 60 * W + 10, d = 60 * W + 190;
        foreach (var mc in Rules.Classes)
        {
            var k = r.Nav.Class(TerrainClass(mc.Id), mc.Locomotor == Locomotor.Foot ? 1 : 2);
            var path = Plan(r, k, s, d);
            Assert.Equal(d, path[^1]);
            var cost = Walk(r.Nav, k, s, path);
            var best = new List<int>();
            Assert.True(new LocalSearch().AStar(r.Nav, k, s, d, (0, 0, W - 1, H - 1), best, out var optimal));
            output.WriteLine($"{mc.Id}: {path.Count} cells, cost {cost} against A*'s {optimal}");
            Assert.True(cost * 100 <= optimal * 110, $"{mc.Id}: cost {cost}, A* {optimal}"); // within the smoothing's tolerance
            // Through the ridge's gap, as every class must; and where the cheapest way keeps off the hilltop (above 5 m) or out of
            // the grove, so does the route (the smoothing never takes a line over or through what the search went round).
            Assert.Contains(path, c => c % W is >= 150 and <= 152 && c / W is >= 20 and <= 26);
            bool Top(int c) => Hill(c % W, c / W) > 5 * M;
            bool Grove(int c) => flags[c] == 2;
            output.WriteLine($"  A* over the top {best.Any(Top)}, through the grove {best.Any(Grove)}; the route {path.Any(Top)}, {path.Any(Grove)}");
            Assert.True(best.Any(Top) || !path.Any(Top), $"{mc.Id} went over the hill");
            Assert.True(best.Any(Grove) || !path.Any(Grove), $"{mc.Id} went through the grove");
            if (mc.Locomotor != Locomotor.Foot)
            {
                Assert.False(path.Any(Top), $"{mc.Id} drove over the hill");
            }
        }
    }

    [Fact]
    public void SmoothingNeverCutsRockOrAClassClearance()
    {
        for (var seed = 1; seed <= 4; seed++)
        {
            const int Size = 160;
            var rng = new Random(seed);
            var g = new Grid(Size, Size);
            for (var i = 0; i < 70; i++)
            {
                int x = rng.Next(Size), y = rng.Next(Size), w = 1 + rng.Next(9), h = 1 + rng.Next(9);
                for (var yy = y; yy < Math.Min(y + h, Size); yy++)
                {
                    for (var xx = x; xx < Math.Min(x + w, Size); xx++)
                    {
                        g.Set(new Cell(xx, yy), Terrain.Blocked);
                    }
                }
            }
            var t = Meadow(Size, Size, seed, (x, y) => (int)(1200 * Math.Sin(x / 13.0) * Math.Cos(y / 17.0)));
            var r = new Router(g, t, []);
            for (var width = 1; width <= 3; width++)
            {
                var k = r.Nav.Class(TerrainClass(width == 1 ? "foot" : "wheeled"), width);
                for (var n = 0; n < 25;)
                {
                    int s = rng.Next(Size * Size), d = rng.Next(Size * Size);
                    if (!r.Passable(k, s) || !r.Passable(k, d) || s == d || r.Component(k, s) != r.Component(k, d))
                    {
                        continue;
                    }
                    n++;
                    var path = Plan(r, k, s, d);
                    Assert.Equal(d, path[^1]);
                    Walk(r.Nav, k, s, path); // every cell clear for the class's width, every step one it can take
                }
            }
        }
        // A gap two cells wide: the technical's class (width 1 and 2) goes through it, a tank's (3) can't.
        var wall = new Grid(60, 40);
        for (var y = 0; y < 40; y++)
        {
            if (y is < 19 or > 20)
            {
                wall.Set(new Cell(30, y), Terrain.Blocked);
            }
        }
        var wr = new Router(wall, null, []);
        foreach (var width in new[] { 1, 2 })
        {
            var k = wr.Nav.Class(0, width);
            var path = Plan(wr, k, 20 * 60 + 5, 20 * 60 + 55);
            Assert.Contains(path, c => c % 60 == 30);
        }
        var tank = wr.Nav.Class(0, 3);
        Assert.NotEqual(wr.Component(tank, 20 * 60 + 5), wr.Component(tank, 20 * 60 + 55));
    }

    [Fact]
    public void SightIsExactAtCorners()
    {
        var g = new Grid(12, 12);
        g.Set(new Cell(5, 4), Terrain.Blocked);
        var r = new Router(g, null, []);
        var k = r.Nav.Class(0, 1);
        int At(int x, int y) => y * 12 + x;
        Assert.False(r.Sight(k, At(1, 1), At(10, 10))); // the diagonal touches (5,4) at its corner, between (4,4) and (5,5): as a diagonal step there may not
        Assert.True(r.Sight(k, At(1, 2), At(9, 10)));   // the next diagonal down keeps clear of it
        Assert.False(r.Sight(k, At(1, 0), At(9, 8)));   // the one up runs through it
        Assert.False(r.Sight(k, At(2, 4), At(9, 4)));   // so does its row
        Assert.True(r.Sight(k, At(2, 3), At(9, 3)));    // the row above: clear
        // Rows just under and just over the blocked cell's lower edge (y = 5).
        Vec P(double x, double y) => new(Fixed.FromRaw((long)(x * Fixed.OneRaw)), Fixed.FromRaw((long)(y * Fixed.OneRaw)));
        Assert.True(r.Sight(k, P(2.5, 5.01), P(9.5, 5.01)));
        Assert.False(r.Sight(k, P(2.5, 4.99), P(9.5, 4.99)));
        Assert.False(r.Sight(k, P(2.5, 5.6), P(5.9, 4.9)));   // clips its corner on the way up
        Assert.True(r.Sight(k, P(2.5, 5.6), P(6.5, 4.99)));   // passes under it, into (6,4)
        Assert.False(r.Sight(k, P(1.5, 2.5), P(13, 3)));     // off the map
    }

    [Fact]
    public void TheLookAheadNeverAimsAcrossACorner()
    {
        // An L of rock: the way runs east under a wall, then north past its end.
        var w = Arena.NewSentinel(64);
        for (var x = 0; x < 40; x++)
        {
            w.Grid.Set(new Cell(x, 30), Terrain.Blocked); // the wall's long arm, north of the start
        }
        for (var y = 10; y <= 30; y++)
        {
            w.Grid.Set(new Cell(40, y), Terrain.Blocked); // its short arm, up the east side
        }
        foreach (var kind in new[] { "humvee", "Rifleman" })
        {
            var u = kind == "Rifleman" ? w.Spawn(1, kind, 8, 36, Facing.East) : w.SpawnCrewed(1, kind, 8, 40, Facing.East).Vehicle;
            w.Order(1, CommandKind.Move, [u], cell: new Cell(20, 12));
            var r = w.Routing;
            var k = r.ClassOf(u.Def);
            var aims = 0;
            for (var i = 0; i < 2400 && (i < 5 || u.Moving); i++)
            {
                w.Step();
                if (u.Moving)
                {
                    Assert.True(r.Sight(k, u.Pos, u.Intent.Goal), $"{kind} at {u.Pos} aims across a corner at {u.Intent.Goal}");
                    aims++;
                }
                Assert.True(w.Grid.Passable(Cell.Of(u.Pos)), $"{kind} in the rock at {u.Pos}");
            }
            Assert.True(aims > 100);
            Assert.True(Within(u.Pos, new Cell(20, 12).Center(), 2), $"{kind} stopped at {u.Pos}");
        }
    }

    [Fact]
    public void StraightRoutesAreDeterministic()
    {
        ulong[] Run(bool reverse)
        {
            var w = Arena.NewSentinel(140);
            w.AttachTerrain(Meadow(140, 140, 5, (x, y) => (int)(900 * Math.Sin(x / 11.0 + y / 23.0))));
            w.ReverseMoveOrder = reverse;
            var units = new List<Unit>();
            foreach (var (kind, x, y) in new[] { ("humvee", 10, 40), ("sentinel_cargo_truck", 10, 48), ("main_battle_tank", 10, 56) })
            {
                units.Add(w.SpawnCrewed(1, kind, x, y, Facing.East).Vehicle);
            }
            for (var i = 0; i < 6; i++)
            {
                units.Add(w.Spawn(1, "Rifleman", 4 + i % 3 * 2, 44 + i / 3 * 4, Facing.East));
            }
            w.Order(1, CommandKind.Move, units, cell: new Cell(120, 100));
            var lone = w.SpawnCrewed(1, "humvee", 20, 120, Facing.East).Vehicle;
            w.Order(1, CommandKind.Move, [lone], cell: new Cell(130, 20));
            var hashes = new ulong[900];
            for (var i = 0; i < hashes.Length; i++)
            {
                w.Step();
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        var a = Run(false);
        Assert.Equal(a, Run(false));
        Assert.Equal(a, Run(true));
    }

    [Fact]
    public void AFacingHoldsOnTheLineBetweenTwo()
    {
        Vec At(double degrees) => new(Fixed.FromRaw((long)Math.Round(Math.Sin(degrees * Math.PI / 180) * 4096)),
            Fixed.FromRaw((long)Math.Round(-Math.Cos(degrees * Math.PI / 180) * 4096)));
        Assert.True(Facing.TryFromDirection(At(6), out var plain));
        Assert.Equal(1, plain);                         // past the line halfway to facing 1 (5.6 degrees)
        Assert.True(Facing.TryFromDirection(At(6), 0, out var kept));
        Assert.Equal(0, kept);                          // facing 0 keeps it a little past the line
        Assert.True(Facing.TryFromDirection(At(8), 0, out var turned));
        Assert.Equal(1, turned);                        // 8 degrees: on to facing 1
        Assert.True(Facing.TryFromDirection(At(5), 1, out var back));
        Assert.Equal(1, back);                          // the same on the other side
        Assert.True(Facing.TryFromDirection(At(40), 0, out var far));
        Assert.Equal(4, far);                           // far off: the nearest, as before
        Assert.False(Facing.TryFromDirection(default, 7, out var none));
        Assert.Equal(7, none);
    }
}
