using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The movement system: collision and crowds, head-on crossings, vehicle turning and clearance,
// terrain speed (slope up and down, forest), arrival, stuck detection, pushing, determinism.
public class MovementTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    // The same rules without a trees block: forest is undergrowth only, no tree trunks (TreeTrunkTests has those).
    private static readonly TerrainRules NoTrunks = new() { Classes = Rules.Classes, Vision = Rules.Vision };

    // Terrain builds a w x h terrain with corner heights from f (raw) and forest density from forest; rules defaults to the
    // game's (forest then stands trunks too: a car steers between them, so keep its lane clear or pass NoTrunks).
    private static TerrainMap Ground(int w, int h, Func<int, int, int> f, Func<int, int, int>? forest = null, TerrainRules? rules = null)
    {
        var corners = new short[(w + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        {
            for (var x = 0; x <= w; x++)
            {
                corners[y * (w + 1) + x] = (short)f(x, y);
            }
        }
        var flags = new byte[w * h];
        if (forest is not null)
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    flags[y * w + x] = (byte)forest(x, y);
                }
            }
        }
        return new TerrainMap(w, h, corners, flags, rules ?? Rules);
    }

    // Drive sends u straight at cell to along a row or column, past routing, as an order whose
    // own route it already has: the movement system meets whatever is on the line (routing would
    // go round it). Stuck re-plans with real routing from there.
    private static void Drive(World w, Unit u, Cell to)
    {
        var from = Cell.Of(u.Pos);
        int dx = Math.Sign(to.X - from.X), dy = Math.Sign(to.Y - from.Y), n = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
        var path = new Cell[n];
        for (var i = 1; i <= n; i++)
        {
            path[i - 1] = new Cell(from.X + dx * i, from.Y + dy * i);
        }
        u.ClearPath();
        u.Order = new Order(OrderKind.Move, to, 0, 0);
        u.Dest = to;
        u.Mode = RouteMode.Direct;
        u.SetPath(path);
    }

    private static void Wall(World w, int x, int y0, int y1)
    {
        for (var y = y0; y <= y1; y++)
        {
            w.Grid.Set(new Cell(x, y), Sim.Terrain.Blocked);
        }
    }

    // Corridor blocks every row of the map except y0..y1.
    private static void Corridor(World w, int y0, int y1)
    {
        for (var y = 0; y < w.Grid.Height; y++)
        {
            if (y < y0 || y > y1)
            {
                for (var x = 0; x < w.Grid.Width; x++)
                {
                    w.Grid.Set(new Cell(x, y), Sim.Terrain.Blocked);
                }
            }
        }
    }

    private static int Overlap(World w)
    {
        // The deepest overlap between any two ground units on the map, percent of the smaller radius.
        var units = w.Units.Where(u => u.Def.Layer == Layers.Ground && u.Inside == 0 && !u.Def.IsStructure).ToList();
        long worst = 0;
        for (var a = 0; a < units.Count; a++)
        {
            for (var b = a + 1; b < units.Count; b++)
            {
                var touch = units[a].Def.Archetype.Radius.Raw + units[b].Def.Archetype.Radius.Raw;
                var d = (units[a].Pos - units[b].Pos).Len().Raw;
                if (d < touch)
                {
                    worst = Math.Max(worst, (touch - d) * 100 / Math.Min(units[a].Def.Archetype.Radius.Raw, units[b].Def.Archetype.Radius.Raw));
                }
            }
        }
        return (int)worst;
    }

    [Fact]
    public void OpenGroundSpeedIsUnchangedWithoutTerrain()
    {
        var w = Arena.New(40);
        var u = w.Spawn(1, "worker", 5, 5);
        w.Order(1, CommandKind.Move, [u], cell: new Cell(30, 5));
        w.Run(5);
        Assert.Equal(u.Def.Speed.Raw, u.Vel.Len().Raw);
        Assert.Equal(0, u.Vel.Y.Raw);
    }

    [Fact]
    public void CrowdNeverOverlapsMuch()
    {
        var w = Arena.New(48);
        var units = new List<Unit>();
        for (var i = 0; i < 120; i++)
        {
            units.Add(w.Spawn(1, "worker", 4 + i % 10, 4 + i / 10)); // a 10 x 12 block
        }
        w.Order(1, CommandKind.Move, units, cell: new Cell(36, 36));
        w.Step();
        var worst = 0;
        for (var t = 0; t < 900; t++)
        {
            w.Step();
            worst = Math.Max(worst, w.MovementStats().MaxOverlapPercent);
        }
        Assert.True(worst < 10, $"overlap reached {worst}% of a radius");
        Assert.True(Overlap(w) < 10, $"final overlap {Overlap(w)}%");
        // Most arrive; a few with inner slots stay stuck behind the ring that arrived first
        // (slot assignment is routing's job: the stub hands out spiral slots in ID order).
        var idle = units.Count(u => u.Order.Kind == OrderKind.Idle);
        Assert.True(idle >= 100, $"only {idle} of 120 arrived");
    }

    [Fact]
    public void StackedUnitsSpreadOut()
    {
        var w = Arena.New(20);
        var units = Enumerable.Range(0, 6).Select(_ => w.Spawn(1, "worker", 10, 10)).ToList();
        w.Run(60);
        Assert.True(Overlap(w) < 10, $"overlap {Overlap(w)}% after 3 s");
    }

    [Fact]
    public void HeadOnCrossingInACorridorResolves()
    {
        var w = Arena.New(64);
        Corridor(w, 26, 33); // 8 cells wide
        var east = new List<Unit>();
        var west = new List<Unit>();
        for (var i = 0; i < 40; i++)
        {
            east.Add(w.Spawn(1, "worker", 4 + i / 8, 26 + i % 8));
            west.Add(w.Spawn(1, "worker", 59 - i / 8, 26 + i % 8));
        }
        w.Order(1, CommandKind.Move, east, cell: new Cell(56, 29));
        w.Order(1, CommandKind.Move, west, cell: new Cell(7, 29));
        var worst = 0;
        w.Step();
        var t = 1;
        for (; t < 120 * World.TicksPerSecond && w.Units.Any(u => u.Order.Kind != OrderKind.Idle); t++)
        {
            w.Step();
            worst = Math.Max(worst, w.MovementStats().MaxOverlapPercent);
        }
        Assert.All(east, u => Assert.True(u.Pos.X.Raw > Fixed.FromInt(45).Raw, $"unit {u.Id} stayed at {u.Pos.X.Floor()}"));
        Assert.All(west, u => Assert.True(u.Pos.X.Raw < Fixed.FromInt(18).Raw, $"unit {u.Id} stayed at {u.Pos.X.Floor()}"));
        Assert.True(t < 90 * World.TicksPerSecond, $"crossing took {t / World.TicksPerSecond} s");
        Assert.True(worst < 10, $"overlap reached {worst}%");
    }

    [Fact]
    public void VehicleTurnsOnlyWhileRollingAtItsTurnRate()
    {
        var w = Arena.New(40);
        var (v, _) = w.SpawnCrewed(1, "technical", 20, 10, Facing.North);
        w.Order(1, CommandKind.Move, [v], cell: new Cell(20, 30)); // straight behind it
        var turned = 0;
        for (var t = 0; t < 400; t++)
        {
            var f = v.Facing;
            w.Step();
            var d = Math.Abs(Facing.Diff(f, v.Facing));
            Assert.True(d <= v.Def.TurnRate, $"turned {d} facings in one tick");
            if (d > 0)
            {
                turned++;
                Assert.True(v.Vel.LenSq() > 0, "turned in place");
            }
        }
        Assert.True(turned >= 16 / v.Def.TurnRate, "should have turned around");
        Assert.Equal(new Cell(20, 30), Cell.Of(v.Pos));
        Assert.Equal(OrderKind.Idle, v.Order.Kind);
    }

    [Fact]
    public void VehiclesNeedClearanceInfantrySqueezeThrough()
    {
        static World Gap()
        {
            var w = Arena.New(30);
            Wall(w, 15, 0, 29);
            w.Grid.Set(new Cell(15, 14), Sim.Terrain.Clear); // a one-cell gap
            return w;
        }
        var w = Gap();
        var (tech, _) = w.SpawnCrewed(1, "technical", 5, 14, Facing.East);
        Drive(w, tech, new Cell(25, 14)); // straight at the gap (routing wouldn't send it there)
        var stuckAt = -1;
        for (var t = 0; t < 600; t++)
        {
            w.Step();
            if (stuckAt < 0 && tech.Intent.Stuck)
            {
                stuckAt = t;
            }
            Assert.True(tech.Pos.X.Raw < Fixed.FromInt(15).Raw, "the technical went through the one-cell gap");
        }
        Assert.True(stuckAt >= 0, "the technical should report Stuck at the gap");

        var s = Gap();
        var soldier = s.Spawn(1, "worker", 5, 18);
        s.Order(1, CommandKind.Move, [soldier], cell: new Cell(25, 18));
        s.Run(600);
        Assert.Equal(new Cell(25, 18), Cell.Of(soldier.Pos));
    }

    [Fact]
    public void SlopeSpeedUpAndDownhill()
    {
        // A ramp of 15% grade rising to the east: Gentle for infantry (85% up, 105% down).
        foreach (var (from, to, pct) in new[] { (5, 35, 85), (35, 5, 105) })
        {
            var w = Arena.New(40);
            w.AttachTerrain(Ground(40, 40, (x, y) => x * 154));
            var u = w.Spawn(1, "worker", from, 10);
            w.Order(1, CommandKind.Move, [u], cell: new Cell(to, 10));
            w.Run(10);
            Assert.Equal(u.Def.Speed.Raw * pct / 100, Math.Abs(u.Vel.X.Raw));
        }
        // Flat terrain: full speed.
        var flat = Arena.New(40);
        flat.AttachTerrain(TerrainMap.Flat(40, 40, Rules));
        var f = flat.Spawn(1, "worker", 5, 10);
        flat.Order(1, CommandKind.Move, [f], cell: new Cell(35, 10));
        flat.Run(10);
        Assert.Equal(f.Def.Speed.Raw, f.Vel.X.Raw);
    }

    [Fact]
    public void SteepGroundBlocksWheelsNotFeet()
    {
        // Flat but a 30% band at x 15..19: Steep, impassable to trucks (light 4x4s climb it).
        static int H(int x) => x <= 15 ? 0 : x <= 20 ? (x - 15) * 307 : 5 * 307;
        var w = Arena.New(40);
        w.AttachTerrain(Ground(40, 40, (x, _) => H(x)));
        var (tech, _) = w.SpawnCrewed(1, "light_rocket_truck", 5, 10, Facing.East);
        var soldier = w.Spawn(1, "worker", 5, 20);
        Drive(w, tech, new Cell(30, 10)); // straight at the steep band (routing wouldn't send it there)
        w.Order(1, CommandKind.Move, [soldier], cell: new Cell(30, 20));
        var stuck = false;
        for (var t = 0; t < 800; t++)
        {
            w.Step();
            stuck |= tech.Intent.Stuck;
            Assert.True(tech.Pos.X.Raw + tech.Def.Archetype.Radius.Raw <= Fixed.FromInt(15).Raw + 1, "the truck climbed steep ground");
        }
        Assert.Equal(new Cell(30, 20), Cell.Of(soldier.Pos));
        Assert.True(stuck);
    }

    [Fact]
    public void ForestSlowsVehiclesAndDenseForestSlowsHeavyOnesMost()
    {
        // A grove (density 2) everywhere: wheeled 60%, infantry 95%. Without tree trunks, which stand in a grove (half of its
        // 2 x 2 slots hold one) and would make the technical, driven straight along row 10, swerve round the one beside its
        // lane and lose the undergrowth's speed (with the first take, 35%, row 10 had none near the start; with 50% there is
        // a trunk at (7, 9)). TreeTrunkTests has the trunks.
        var w = Arena.New(60);
        w.AttachTerrain(Ground(60, 60, (_, _) => 0, (_, _) => 2, NoTrunks));
        var (tech, _) = w.SpawnCrewed(1, "technical", 5, 10, Facing.East);
        var soldier = w.Spawn(1, "worker", 5, 20);
        Drive(w, tech, new Cell(55, 10)); // due east, so the velocity is all X
        Drive(w, soldier, new Cell(55, 20));
        w.Run(30);
        Assert.Equal(tech.Def.Speed.Raw * 60 / 100, tech.Vel.X.Raw);
        Assert.Equal(soldier.Def.Speed.Raw * 95 / 100, soldier.Vel.X.Raw);

        // Dense undergrowth (3) at x 20..24, without tree trunks (TreeTrunkTests has those): a heavy truck no longer stops at
        // its edge but drives through at its dense speed (35%); a light vehicle stops there now (its trunks leave a car no way).
        var d = Arena.New(40);
        d.AttachTerrain(new TerrainMap(40, 40, new int[41 * 41], [.. Enumerable.Range(0, 1600).Select(i => (byte)(i % 40 is >= 20 and < 25 ? 3 : 0))], NoTrunks));
        var (truck, _) = d.SpawnCrewed(1, "cargo_truck", 5, 10, Facing.East);
        var (bike, _) = d.SpawnCrewed(1, "technical", 5, 25, Facing.East);
        Drive(d, truck, new Cell(35, 10)); // straight at the forest
        Drive(d, bike, new Cell(35, 25));
        var top = 0L;
        var stuck = false;
        for (var t = 0; t < 1000; t++)
        {
            d.Step();
            if (Cell.Of(truck.Pos).X is >= 21 and <= 23)
            {
                top = Math.Max(top, truck.Vel.X.Raw);
            }
            stuck |= bike.Intent.Stuck;
            Assert.True(bike.Pos.X.Raw + bike.Def.Archetype.Radius.Raw <= Fixed.FromInt(20).Raw + 1, "the car entered dense forest");
        }
        Assert.Equal(truck.Def.Speed.Raw * 35 / 100, top);
        Assert.Equal(new Cell(35, 10), Cell.Of(truck.Pos));
        Assert.True(stuck);
    }

    [Fact]
    public void ArrivalIsStillNoJitter()
    {
        var w = Arena.New(30);
        var u = w.Spawn(1, "worker", 5, 5);
        var (v, _) = w.SpawnCrewed(1, "technical", 5, 15, Facing.North);
        w.Order(1, CommandKind.Move, [u], cell: new Cell(20, 5));
        w.Order(1, CommandKind.Move, [v], cell: new Cell(20, 15));
        w.Run(400);
        Assert.Equal(OrderKind.Idle, u.Order.Kind);
        Assert.Equal(OrderKind.Idle, v.Order.Kind);
        var (pu, pv, fu, fv) = (u.Pos, v.Pos, u.Facing, v.Facing);
        for (var t = 0; t < 100; t++)
        {
            w.Step();
            Assert.Equal(pu, u.Pos);
            Assert.Equal(pv, v.Pos);
            Assert.Equal(fu, u.Facing);
            Assert.Equal(fv, v.Facing);
        }
        Assert.True((u.Pos - new Cell(20, 5).Center()).Len().Raw <= 160);
    }

    [Fact]
    public void MovingUnitsNudgeIdleFriendsButNotEnemies()
    {
        foreach (var enemy in new[] { false, true })
        {
            var w = Arena.New(30);
            Corridor(w, 5, 5); // one cell wide
            var mover = w.Spawn(1, "worker", 2, 5);
            var idle = w.Spawn(enemy ? (byte)2 : (byte)1, "worker", 10, 5);
            var at = idle.Pos;
            w.Order(1, CommandKind.Move, [mover], cell: new Cell(16, 5));
            w.Run(400);
            if (enemy)
            {
                Assert.Equal(at, idle.Pos);
                Assert.True(mover.Pos.X.Raw < at.X.Raw, "walked through an enemy");
                Assert.True(mover.Intent.Stuck);
            }
            else
            {
                Assert.True(idle.Pos.X.Raw > at.X.Raw + Fixed.OneRaw, "the idle friend should have been pushed along");
                Assert.Equal(OrderKind.Idle, mover.Order.Kind);
            }
            Assert.True(Overlap(w) < 10);
        }
    }

    [Fact]
    public void BuildingsAreHard()
    {
        var w = Arena.New(30);
        w.Spawn(1, "supply_depot", 14, 9); // 2 x 2 at cells 14..15, 9..10
        var u = w.Spawn(1, "worker", 5, 10);
        w.Order(1, CommandKind.Move, [u], cell: new Cell(25, 10)); // the stub route runs straight through it
        for (var t = 0; t < 400; t++)
        {
            w.Step();
            var p = u.Pos;
            var qx = Math.Clamp(p.X.Raw, Fixed.FromInt(14).Raw, Fixed.FromInt(16).Raw);
            var qy = Math.Clamp(p.Y.Raw, Fixed.FromInt(9).Raw, Fixed.FromInt(11).Raw);
            var d = Fixed.Sqrt((p.X.Raw - qx) * (p.X.Raw - qx) + (p.Y.Raw - qy) * (p.Y.Raw - qy));
            Assert.True(d >= u.Def.Archetype.Radius.Raw - 8, $"walked into the depot at {p}");
        }
        Assert.True(u.Intent.Stuck || Cell.Of(u.Pos) == new Cell(25, 10));
    }

    [Fact]
    public void StuckClearsWhenTheGoalChanges()
    {
        var w = Arena.New(30);
        Corridor(w, 5, 5);
        var mover = w.Spawn(1, "worker", 2, 5);
        w.Spawn(2, "worker", 10, 5);
        w.Order(1, CommandKind.Move, [mover], cell: new Cell(16, 5));
        w.Run(200);
        Assert.True(mover.Intent.Stuck);
        w.Order(1, CommandKind.Move, [mover], cell: new Cell(1, 5));
        w.Run(3);
        Assert.False(mover.Intent.Stuck);
        w.Run(100);
        Assert.Equal(new Cell(1, 5), Cell.Of(mover.Pos));
    }

    // Crossing builds a busy world: two groups crossing head-on, vehicles among them.
    private static World Crossing(bool reverse)
    {
        var w = Arena.New(48, seed: 3);
        w.ReverseMoveOrder = reverse;
        Corridor(w, 14, 25);
        var a = new List<Unit>();
        var b = new List<Unit>();
        for (var i = 0; i < 36; i++)
        {
            a.Add(w.Spawn(1, "worker", 3 + i / 12, 14 + i % 12));
            b.Add(w.Spawn(1, "worker", 44 - i / 12, 14 + i % 12));
        }
        a.Add(w.SpawnCrewed(1, "technical", 8, 18, Facing.East).Vehicle);
        b.Add(w.SpawnCrewed(1, "technical", 39, 21, Facing.West).Vehicle);
        w.Order(1, CommandKind.Move, a, cell: new Cell(40, 19));
        w.Order(1, CommandKind.Move, b, cell: new Cell(7, 19));
        return w;
    }

    [Fact]
    public void DeterministicAndIndependentOfNeighbourOrder()
    {
        var x = Crossing(false);
        var y = Crossing(false);
        var z = Crossing(true);
        for (var t = 0; t < 600; t++)
        {
            x.Step();
            y.Step();
            z.Step();
            var h = x.Hash();
            Assert.Equal(h, y.Hash());
            Assert.Equal(h, z.Hash());
        }
    }
}
