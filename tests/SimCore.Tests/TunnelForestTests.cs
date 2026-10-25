using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Tunnel entrances in forest (docs/design.md, "Tunnels" and "Trees as obstacles", decisions 458 to 462): foot workers place a
// hatch or a medium entrance in dense forest (the large keeps its flat-ground rule); placing one clears the trees (forest,
// standing trunks, fallen trunks across the cells) only on its footprint and its apron, the door and the strip in front of
// it; the trees on the blend ring stay standing; units walk out of the door; the cleared cells stay cleared when the
// entrance is gone; the same commands give the same hashes. Sizes: footprint s, apron, blend (content/mvp/archetypes/structures.json).
public class TunnelForestTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int N = 96;
    private const int Dense = 3;

    // The four quarter turns of an entrance: the mouth faces south, west, north, east.
    private static readonly int[] Facings = [0, 8, 16, 24];

    // Wood builds an n x n arena of terrain from forest density and height (raw corners; flat without).
    private static World Wood(Func<int, int, int> forest, Func<int, int, int>? height = null, int n = N)
    {
        var w = Arena.New(n);
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = height?.Invoke(x, y) ?? 0;
            }
        }
        var flags = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                flags[y * n + x] = (byte)forest(x, y);
            }
        }
        w.AttachTerrain(new TerrainMap(n, n, corners, flags, Rules));
        return w;
    }

    private static World DenseWood() => Wood((_, _) => Dense);

    private readonly record struct Box(int X0, int Y0, int X1, int Y1)
    {
        public bool Has(int x, int y) => x >= X0 && x < X1 && y >= Y0 && y < Y1;

        public int Cells => (X1 - X0) * (Y1 - Y0);

        public Box Grow(int by) => new(X0 - by, Y0 - by, X1 + by, Y1 + by);
    }

    // PadCells is the cells of the footprint and the apron of an entrance with top-left cell o and a footprint s cells square,
    // turned by facing (the mouth faces south, west, north, east): x and y from X0 and Y0 up to but not including X1 and Y1.
    private static Box PadCells(Cell o, int s, int apron, int facing) => ((facing / 8) % 4) switch
    {
        0 => new Box(o.X, o.Y, o.X + s, o.Y + s + apron),
        1 => new Box(o.X - apron, o.Y, o.X + s, o.Y + s),
        2 => new Box(o.X, o.Y - apron, o.X + s, o.Y + s),
        _ => new Box(o.X, o.Y, o.X + s + apron, o.Y + s),
    };

    private static int Trunks(TerrainMap g, Box b)
    {
        var n = 0;
        for (var y = b.Y0; y < b.Y1; y++)
        {
            for (var x = b.X0; x < b.X1; x++)
            {
                n += g.HasTrunk(new Cell(x, y)) ? 1 : 0;
            }
        }
        return n;
    }

    // Site finds where an entrance of this size, turned by facing, has a trunk on its footprint, one on its apron and a few on
    // the ring round them (in an all-dense wood): so the checks have something to clear and something to spare.
    private static Cell Site(int s, int apron, int blend, int facing)
    {
        var g = DenseWood().Ground!;
        for (var y = 30; y < 60; y++)
        {
            for (var x = 30; x < 60; x++)
            {
                var o = new Cell(x, y);
                var pad = PadCells(o, s, apron, facing);
                var foot = new Box(o.X, o.Y, o.X + s, o.Y + s);
                if (Trunks(g, foot) > 0 && Trunks(g, pad) > Trunks(g, foot) && Trunks(g, pad.Grow(blend + 1)) - Trunks(g, pad) >= 3)
                {
                    return o;
                }
            }
        }
        throw new InvalidOperationException("no site with trees in the footprint, the apron and the ring");
    }

    private static UnitDef Def(World w, string id) => w.Player(1)!.Setup.Find(w.Content, id)!;

    private static Vec Door(Unit e) => World.TunnelDoor(e);

    // Bury puts u underground at entrance e at once (tests of what happens below).
    private static void Bury(Unit u, Unit e)
    {
        u.Idle();
        u.Inside = World.Underground;
        u.Tunnel = e.Id;
        u.Pos = e.Pos;
    }

    private static void Go(World w, IEnumerable<Unit> units, Unit e, bool exit) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.TunnelGo, Units = [.. units.Select(u => u.Id)], Target = e.Id, On = exit });

    private static int RunUntil(World w, Func<bool> done, int max)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            if (done())
            {
                return t;
            }
        }
        return -1;
    }

    private static bool Within(Vec a, Vec b, int cells) => (a - b).LenSq() <= (long)cells * cells * Fixed.OneRaw * Fixed.OneRaw;

    [Theory]
    [InlineData("tunnel_hatch", 200)]
    [InlineData("tunnel_medium", 500)]
    [InlineData("tunnel_large", 800)]
    public void AFootWorkerBuildsEachEntranceInDenseForest(string id, int ticks)
    {
        var w = DenseWood();
        var worker = w.Spawn(1, "worker", 20, 50);
        var at = new Cell(40, 40);
        Assert.True(w.CanPlace(Def(w, id), at, 0, [worker])); // flat dense forest: its builders walk there
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = at, Unit = id });
        w.Step();
        var site = Assert.Single(w.All(id));
        Assert.False(site.IsBuilt);
        var t = RunUntil(w, () => site.IsBuilt, 3000);
        Assert.InRange(t, ticks - 60, ticks + 400); // through the wood to it, then BuildTime of work
        Assert.Single(w.TunnelNodes(1));
        Assert.Equal(Dense, w.Ground!.Forest(new Cell(30, 40))); // the wood round it is still dense
    }

    [Fact]
    public void TheLargeKeepsItsFlatGroundRuleInForestAndTheOthersNeedOnlyWalkableGround()
    {
        var at = new Cell(40, 40);
        // A 15% slope under dense forest: too steep for the large, walkable for the hatch and the medium.
        var slope = Wood((_, _) => Dense, (_, y) => y * 154);
        var worker = slope.Spawn(1, "worker", 40, 30);
        Assert.True(slope.CanPlace(Def(slope, "tunnel_hatch"), at, 0, [worker]));
        Assert.True(slope.CanPlace(Def(slope, "tunnel_medium"), at, 0, [worker]));
        Assert.False(slope.CanPlace(Def(slope, "tunnel_large"), at, 0, [worker]));
        // Flat, all three go in scrub, grove and dense forest alike.
        foreach (var density in new[] { 1, 2, 3 })
        {
            var flat = Wood((_, _) => density);
            var wk = flat.Spawn(1, "worker", 40, 30);
            foreach (var id in new[] { "tunnel_hatch", "tunnel_medium", "tunnel_large" })
            {
                Assert.True(flat.CanPlace(Def(flat, id), at, 0, [wk]), $"{id} in forest of density {density}");
            }
        }
    }

    [Theory]
    [InlineData("tunnel_hatch", 2, 1, 3)]
    [InlineData("tunnel_medium", 5, 2, 5)]
    [InlineData("tunnel_large", 7, 3, 6)]
    public void OnlyTheFootprintAndTheApronLoseTheirForestAndTrunks(string id, int s, int apron, int blend)
    {
        var arch = TestContent.Mvp.Archetype(id)!;
        Assert.Equal((s, apron, blend), (arch.FootprintW, arch.Tunnel!.Apron, arch.Tunnel.Blend));
        foreach (var facing in Facings)
        {
            var o = Site(s, apron, blend, facing);
            var control = DenseWood().Ground!;
            var w = DenseWood();
            w.Spawn(1, id, o.X, o.Y, facing);
            var g = w.Ground!;
            var pad = PadCells(o, s, apron, facing);
            var shaped = pad.Grow(blend + 1); // all that was cleared before: the shaped ground and a cell round it
            int ring = 0, ringTrunks = 0;
            for (var y = 0; y < N; y++)
            {
                for (var x = 0; x < N; x++)
                {
                    var c = new Cell(x, y);
                    if (pad.Has(x, y))
                    {
                        Assert.Equal(0, g.Forest(c));
                        Assert.False(g.HasTrunk(c), $"a trunk stands on the footprint or apron at {c}, facing {facing}");
                        continue;
                    }
                    // Everything else is as it was: the ring (where the ground only eases) and the wood beyond.
                    Assert.Equal(control.Forest(c), g.Forest(c));
                    Assert.Equal(control.HasTrunk(c), g.HasTrunk(c));
                    if (shaped.Has(x, y))
                    {
                        ring++;
                        ringTrunks += g.HasTrunk(c) ? 1 : 0;
                        Assert.Equal(Dense, g.Forest(c));
                    }
                }
            }
            Assert.Equal(shaped.Cells - pad.Cells, ring);
            Assert.True(ringTrunks >= 3, $"trees on the blend ring stay standing ({ringTrunks})");
            Assert.True(Trunks(control, pad) > 0 && Trunks(control, new Box(o.X, o.Y, o.X + s, o.Y + s)) > 0, "the site had trunks to clear");
            Assert.Empty(w.FallenTrunks);
        }
    }

    [Theory]
    [InlineData("tunnel_hatch", 2, 1, 3)]
    [InlineData("tunnel_medium", 5, 2, 5)]
    [InlineData("tunnel_large", 7, 3, 6)]
    public void AFallenTrunkAcrossTheClearedCellsGoesWholeAndOneOnTheRingStays(string id, int s, int apron, int blend)
    {
        // Facing south. A trunk felled south from a stump on the ring behind the back edge lies across the footprint; one felled
        // north from a stump on the west ring lies on the ring alone.
        var g0 = DenseWood().Ground!;
        Cell? behind = null, side = null;
        Cell o = default;
        for (var y = 34; y < 60 && behind is null; y++)
        {
            for (var x = 34; x < 60 && behind is null; x++)
            {
                o = new Cell(x, y);
                behind = Enumerable.Range(o.Y - 4, 4).SelectMany(sy => Enumerable.Range(o.X, s).Select(sx => new Cell(sx, sy)))
                    .Cast<Cell?>().FirstOrDefault(c => g0.HasTrunk(c!.Value));
                side = Enumerable.Range(o.Y + 1, s + apron - 1).SelectMany(sy => Enumerable.Range(o.X - blend, blend).Select(sx => new Cell(sx, sy)))
                    .Cast<Cell?>().FirstOrDefault(c => g0.HasTrunk(c!.Value));
                behind = side is null ? null : behind;
            }
        }
        Assert.NotNull(behind);
        var w = DenseWood();
        var g = w.Ground!;
        Assert.True(w.FellTree(behind!.Value, 4, 0, TreeFallCause.Pushed, default)); // south, over the footprint
        Assert.True(w.FellTree(side!.Value, 0, 0, TreeFallCause.Pushed, default));   // north, along the ring
        var across = w.FallenTrunks.Single(f => f.Stump == behind);
        var along = w.FallenTrunks.Single(f => f.Stump == side);
        var pad = PadCells(o, s, apron, 0);
        Assert.Contains(Enumerable.Range(1, across.Length), k => pad.Has(across.CellAt(k).X, across.CellAt(k).Y));
        Assert.Contains(Enumerable.Range(1, across.Length), k => !pad.Has(across.CellAt(k).X, across.CellAt(k).Y)); // and on the ring too
        Assert.All(Enumerable.Range(1, along.Length), k => Assert.False(pad.Has(along.CellAt(k).X, along.CellAt(k).Y)));
        w.Spawn(1, id, o.X, o.Y, 0);
        var left = Assert.Single(w.FallenTrunks);
        Assert.Equal(side!.Value, left.Stump); // the one across the footprint is taken away whole, the ring's lies on
        Assert.All(Enumerable.Range(1, across.Length), k => Assert.False(g.Fallen(across.CellAt(k))));
        Assert.All(Enumerable.Range(1, along.Length), k => Assert.True(g.Fallen(along.CellAt(k))));
        Assert.Contains(w.TreeEvents, e => e.Kind == TreeEventKind.Cleared && e.Stump == behind);
        Assert.DoesNotContain(w.TreeEvents, e => e.Kind == TreeEventKind.Cleared && e.Stump == side);
        Assert.Contains(behind.Value, w.Stumps); // a felled tree's stump stays a stump
    }

    [Theory]
    [InlineData("tunnel_hatch", 2, 1)]
    [InlineData("tunnel_medium", 5, 2)]
    [InlineData("tunnel_large", 7, 3)]
    public void UnitsWalkOutOfTheDoorAndOnThroughTheWood(string id, int s, int apron)
    {
        var w = DenseWood();
        var o = new Cell(40, 40);
        var e = w.Spawn(1, id, o.X, o.Y);
        var g = w.Ground!;
        // The door is on the cleared strip in front of the mouth (the hatch's, on the outer edge of its one cell).
        var pad = PadCells(o, s, apron, 0);
        var door = Door(e);
        Assert.InRange(door.X.Raw, (long)pad.X0 * Fixed.OneRaw, (long)pad.X1 * Fixed.OneRaw);
        Assert.InRange(door.Y.Raw, (long)pad.Y0 * Fixed.OneRaw, (long)pad.Y1 * Fixed.OneRaw);
        Assert.Equal(0, g.Forest(new Cell(o.X + s / 2, pad.Y1 - 1)));
        var squad = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 20 + i, 70)).ToList();
        foreach (var u in squad)
        {
            Bury(u, e);
        }
        Go(w, squad, e, true);
        var emerged = new Dictionary<uint, Vec>();
        for (var t = 0; t < 800 && emerged.Count < squad.Count; t++)
        {
            w.Step();
            foreach (var u in squad.Where(u => !u.InTunnel && !emerged.ContainsKey(u.Id)))
            {
                emerged[u.Id] = u.Pos;
            }
        }
        Assert.Equal(squad.Count, emerged.Count); // all four come out
        Assert.All(emerged.Values, p => Assert.True(Within(p, door, 1), "each comes out at the door"));
        // Then on through the dense wood, south and west (the entrance a wall in the middle of it).
        var south = new Cell(42, 80);
        var west = new Cell(12, 40);
        w.Order(1, CommandKind.Move, squad.Take(2), cell: south);
        w.Order(1, CommandKind.Move, squad.Skip(2), cell: west);
        Assert.True(RunUntil(w, () => squad.All(u => u.Order.Kind == OrderKind.Idle), 2400) > 0, "they walk on");
        Assert.All(squad.Take(2), u => Assert.True(Within(u.Pos, south.Center(), 3), $"at {Cell.Of(u.Pos)}"));
        Assert.All(squad.Skip(2), u => Assert.True(Within(u.Pos, west.Center(), 3), $"at {Cell.Of(u.Pos)}"));
        // And back to the door through the wood: it is reachable from outside.
        w.Order(1, CommandKind.Enter, squad, target: e.Id);
        Assert.True(RunUntil(w, () => squad.All(u => u.InTunnel), 3000) > 0, "all four go back in");
    }

    [Theory]
    [InlineData("tunnel_hatch", 2, 1, 3)]
    [InlineData("tunnel_medium", 5, 2, 5)]
    [InlineData("tunnel_large", 7, 3, 6)]
    public void TheTreesDoNotGrowBackWhenTheEntranceIsGoneAndTheGroundReturns(string id, int s, int apron, int blend)
    {
        // Rolling land (a flat hatch would change nothing of it).
        static int Land(int x, int y) => (x * 37 + y * 11) % 300;
        var o = Site(s, apron, blend, 0);
        var w = Wood((_, _) => Dense, Land);
        var control = Wood((_, _) => Dense, Land).Ground!;
        ulong[] Corners() => [.. w.Ground!.ReadCorners(o.X - blend - 2, o.Y - blend - 2, s + 2 * blend + 5, s + apron + 2 * blend + 5).Select(v => (ulong)v)];
        var before = Corners();
        var e = w.Spawn(1, id, o.X, o.Y, 0);
        Assert.NotEqual(before, Corners()); // the ground is shaped
        e.HP = 0;
        w.Step(); // rubble
        e.HP = 0;
        w.Step(); // gone
        w.Run(700); // and the ground has stepped back
        Assert.Equal(before, Corners());
        var g = w.Ground!;
        var pad = PadCells(o, s, apron, 0);
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var c = new Cell(x, y);
                Assert.False(g.Pad(c));
                if (pad.Has(x, y))
                {
                    Assert.Equal(0, g.Forest(c)); // still clear: the trees do not grow back
                    Assert.False(g.HasTrunk(c));
                }
                else
                {
                    Assert.Equal(control.Forest(c), g.Forest(c));
                    Assert.Equal(control.HasTrunk(c), g.HasTrunk(c));
                }
            }
        }
    }

    [Theory]
    [InlineData("tunnel_hatch", 200)]
    [InlineData("tunnel_medium", 500)]
    [InlineData("tunnel_large", 800)]
    public void TheSameCommandsGiveTheSameHashesInTheWood(string id, int build)
    {
        ulong[] Run()
        {
            var w = DenseWood();
            var worker = w.Spawn(1, "worker", 20, 50);
            var squad = Enumerable.Range(0, 3).Select(i => w.Spawn(1, "Rifleman", 30 + i, 60)).ToList();
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = new Cell(40, 40), Unit = id });
            var hashes = new ulong[build + 500];
            for (var i = 0; i < hashes.Length; i++)
            {
                if (i == 20)
                {
                    w.Order(1, CommandKind.Move, squad, cell: new Cell(60, 20));
                }
                w.Step();
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        var a = Run();
        Assert.Equal(a, Run());
        Assert.True(a.Distinct().Count() > 100, "the hash moves with the game");
    }

    [Fact]
    public void WhichTreesStandAfterPlacingIsPartOfTheHash()
    {
        // The same entrance gives the same hash; one more cell cleared on the ring, a different one.
        var o = Site(5, 2, 5, 0);
        World Make(bool extra)
        {
            var w = DenseWood();
            w.Spawn(1, "tunnel_medium", o.X, o.Y, 0);
            if (extra)
            {
                w.ClearTrees(new Cell(o.X - 3, o.Y + 2)); // on the west ring
            }
            return w;
        }
        Assert.Equal(Make(false).Hash(), Make(false).Hash());
        Assert.NotEqual(Make(false).Hash(), Make(true).Hash());
    }

    [Fact]
    public void CarsCannotUseAMediumEntranceInDenseForestButTrucksPushTheirWayAndAGroveLetsCarsIn()
    {
        // Dense forest is shut to cars (forestBlockedFrom 3, decision 450): the cleared strip in front of a medium entrance in
        // a dense wood is a pocket they can neither reach nor leave. A heavy truck (size 2 as well) pushes the trees over on its
        // way. A grove (35% trunks) is open to cars between the trees. The wood is a block of 40 x 40 cells in open land.
        var strip = PadCells(new Cell(40, 40), 5, 2, 0).Grow(1);
        var open = new Cell(40, 78);
        (World W, Unit E) Make(int forest)
        {
            var w = Wood((x, y) => x is >= 20 and < 60 && y is >= 20 and < 60 ? forest : 0);
            return (w, w.Spawn(1, "tunnel_medium", 40, 40));
        }

        // Out: a car that comes out of it stays on the strip in a dense wood, and drives away through a grove.
        foreach (var (forest, stays) in new[] { (Dense, true), (2, false) })
        {
            var (w, e) = Make(forest);
            var (car, _) = w.SpawnCrewed(1, "technical", 10, 70);
            Bury(car, e);
            Go(w, [car], e, true);
            Assert.True(RunUntil(w, () => !car.InTunnel, 600) > 0);
            w.Order(1, CommandKind.Move, [car], cell: open);
            w.Run(1500);
            Assert.Equal(OrderKind.Idle, car.Order.Kind); // it went as near as it could and stopped
            Assert.Equal(stays, strip.Has(Cell.Of(car.Pos).X, Cell.Of(car.Pos).Y));
            Assert.True(Within(car.Pos, open.Center(), 3) == !stays, $"in forest {forest} the car ended at {Cell.Of(car.Pos)}");
        }

        // In: from outside, a car ordered to the door stops at the edge of a dense wood; through a grove it goes in.
        foreach (var (forest, gets) in new[] { (Dense, false), (2, true) })
        {
            var (w, e) = Make(forest);
            var (car, _) = w.SpawnCrewed(1, "technical", 40, 75);
            w.Order(1, CommandKind.Enter, [car], target: e.Id);
            w.Run(2500);
            Assert.Equal(gets, car.InTunnel);
            Assert.True(gets || Cell.Of(car.Pos).Y >= 58, $"the car stops short of the wood, at {Cell.Of(car.Pos)}"); // y 20 to 59 is the wood
        }

        // A heavy truck pushes its way to the door through dense forest.
        var (wt, et) = Make(Dense);
        var (truck, _) = wt.SpawnCrewed(1, "cargo_truck", 40, 75);
        wt.Order(1, CommandKind.Enter, [truck], target: et.Id);
        Assert.True(RunUntil(wt, () => truck.InTunnel, 3000) > 0, $"the truck pushed its way in; it is at {Cell.Of(truck.Pos)}");
        Assert.NotEmpty(wt.Stumps); // over the trees in its way
    }
}
