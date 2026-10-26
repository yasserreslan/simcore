using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Trees as obstacles (docs/design.md, "Trees as obstacles"): standing trunks worked out from the forest (one per 2 x 2 slot at
// most, pinned hash), jeeps and light trucks steering between them, infantry ignoring them, tanks and heavy trucks pushing
// them over at low speed, fallen trunks blocking vehicles for their time, clearing, hashing and the local routing refresh.
public class TreeTrunkTests
{
    private static readonly string RulesPath = Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json");
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(RulesPath);

    // The same rules without a trees block: forest is undergrowth only (as before trunks).
    private static readonly TerrainRules NoTrees = new() { Classes = Rules.Classes, Vision = Rules.Vision };

    private static int Class(string id) => Rules.Classes.ToList().FindIndex(c => c.Id == id);

    // Flat builds an n x n flat terrain with forest density from forest.
    private static TerrainMap Flat(int n, Func<int, int, int> forest, TerrainRules? rules = null)
    {
        var flags = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                flags[y * n + x] = (byte)forest(x, y);
            }
        }
        return new TerrainMap(n, n, new int[(n + 1) * (n + 1)], flags, rules ?? Rules);
    }

    // OneTree is the first cell from x0 along row y where forest of this density stands a trunk: with forest only there, the
    // map's one tree.
    private static Cell OneTree(int x0, int y, int density = 3)
    {
        for (var x = x0; ; x++)
        {
            if (TreeTrunks.Stands(x, y, Rules.Trees!.TrunkChance[density]))
            {
                return new Cell(x, y);
            }
        }
    }

    // Drive sends u straight along a row or column to cell to, past routing (MovementTests.Drive): movement meets what is there.
    private static void Drive(Unit u, Cell to)
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

    // Overlap is how deep (raw) a unit's circle reaches into the trunk cells round it (0: clear of them).
    private static long Overlap(TerrainMap g, Unit u)
    {
        long r = u.Def.Archetype.Radius.Raw, px = u.Pos.X.Raw, py = u.Pos.Y.Raw, worst = 0;
        for (var cy = (int)((py - r) >> 10); cy <= (int)((py + r) >> 10); cy++)
        {
            for (var cx = (int)((px - r) >> 10); cx <= (int)((px + r) >> 10); cx++)
            {
                if (!g.HasTrunk(new Cell(cx, cy)) && !g.Fallen(new Cell(cx, cy)))
                {
                    continue;
                }
                var dx = Math.Clamp(px, (long)cx << 10, ((long)cx + 1) << 10) - px;
                var dy = Math.Clamp(py, (long)cy << 10, ((long)cy + 1) << 10) - py;
                worst = Math.Max(worst, r - Fixed.Sqrt(dx * dx + dy * dy));
            }
        }
        return worst;
    }

    [Fact]
    public void TheTrunkHashIsPinned()
    {
        // These values are the trees every map has: changing the hash moves every tree the client draws.
        Assert.Equal(0xB43C3957u, TreeTrunks.SlotHash(0, 0));
        Assert.Equal(0x6C0DD175u, TreeTrunks.SlotHash(1, 0));
        Assert.Equal(0xE80E5ADAu, TreeTrunks.SlotHash(0, 1));
        Assert.Equal(0x5B764213u, TreeTrunks.SlotHash(7, 3));
        Assert.Equal(0x5025B19Bu, TreeTrunks.SlotHash(1000, 2000));
        Assert.Equal(0xCDA0E98Eu, TreeTrunks.SlotHash(4095, 4095));
        var h = TreeTrunks.SlotHash(0, 0);
        Assert.Equal(new Cell(1, 1), TreeTrunks.SlotCell(0, 0, h));
        Assert.Equal(22, TreeTrunks.Roll(h));
        Assert.Equal((-170, 130), TreeTrunks.Offset(h));
        Assert.Equal((-25, -92), TreeTrunks.Offset(TreeTrunks.SlotHash(7, 3)));
        Assert.Equal(new Cell(2001, 4001), TreeTrunks.SlotCell(1000, 2000, TreeTrunks.SlotHash(1000, 2000)));
        Assert.Equal(69, TreeTrunks.Roll(TreeTrunks.SlotHash(1000, 2000)));
        // Slot (0, 0) picks cell (1, 1) with roll 22: a grove (50) or dense forest (85) has a trunk there, scrub never.
        Assert.True(TreeTrunks.Stands(1, 1, 50));
        Assert.False(TreeTrunks.Stands(1, 1, 22));
        Assert.False(TreeTrunks.Stands(0, 0, 100));
        Assert.Equal(new Vec(Fixed.FromRaw(-170), Fixed.FromRaw(130)), TerrainMap.TrunkOffset(new Cell(1, 1)));
        Assert.Equal(new Vec(Fixed.FromRaw(512 + 1024 - 170), Fixed.FromRaw(512 + 1024 + 130)), TerrainMap.TrunkPoint(new Cell(1, 1)));
        // The 8 fall directions, north and then clockwise, from facings.
        Assert.Equal(0, TreeTrunks.DirOf(Facing.North));
        Assert.Equal(2, TreeTrunks.DirOf(Facing.East));
        Assert.Equal(4, TreeTrunks.DirOf(Facing.South));
        Assert.Equal(6, TreeTrunks.DirOf(Facing.West));
        Assert.Equal(1, TreeTrunks.DirOf(Facing.North + 4));
        Assert.Equal(0, TreeTrunks.DirOf(Facing.Count - 1));
        Assert.Equal((1, 1), TreeTrunks.Step(3));
    }

    [Fact]
    public void TheSameForestGivesTheSameTrunksAtTheRulesDensity()
    {
        const int n = 200;
        int Density(int x, int y) => x < 50 ? 0 : x < 100 ? 1 : x < 150 ? 2 : 3;
        var a = Flat(n, Density);
        var b = Flat(n, Density);
        int[] count = new int[4];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var c = new Cell(x, y);
                Assert.Equal(a.HasTrunk(c), b.HasTrunk(c));
                Assert.Equal(TreeTrunks.Stands(x, y, Rules.Trees!.TrunkChance[Density(x, y)]), a.HasTrunk(c));
                if (a.HasTrunk(c))
                {
                    count[Density(x, y)]++;
                    var o = TerrainMap.TrunkOffset(c);
                    Assert.InRange(o.X.Raw, -TreeTrunks.MaxOffsetRaw, TreeTrunks.MaxOffsetRaw);
                    Assert.InRange(o.Y.Raw, -TreeTrunks.MaxOffsetRaw, TreeTrunks.MaxOffsetRaw);
                    // One trunk at most in a 2 x 2 slot.
                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var o2 = new Cell(x / 2 * 2 + dx, y / 2 * 2 + dy);
                            Assert.True(o2 == c || !a.HasTrunk(o2));
                        }
                    }
                }
            }
        }
        // 2,500 slots a band (50 columns x 200 rows of cells = 25 x 100 slots): trunkChance 50% and 85% of them hold a trunk in a
        // grove and dense forest, so the expected counts are 1,250 and 2,125 (within 3 points: 1,175 to 1,325 and 2,050 to 2,200).
        Assert.Equal(0, count[0]);
        Assert.Equal(0, count[1]);
        Assert.InRange(count[2], 2500 * 47 / 100, 2500 * 53 / 100);
        Assert.InRange(count[3], 2500 * 82 / 100, 2500 * 88 / 100);
        // Without a trees block there are no trunks.
        Assert.False(Flat(n, Density, NoTrees).HasTrunks);
    }

    [Fact]
    public void TrunksAreWallsToJeepsAndDearerToPushersAndNothingToInfantry()
    {
        var at = OneTree(10, 5);
        var g = Flat(32, (x, y) => x == at.X && y == at.Y ? 3 : 0);
        Assert.True(g.HasTrunk(at));
        int foot = Class("foot"), light = Class("light_wheeled"), wheeled = Class("wheeled"), heavy = Class("heavy_wheeled"), tracked = Class("tracked");
        var bare = Flat(32, (x, y) => x == at.X && y == at.Y ? 3 : 0, NoTrees);
        Assert.Equal(bare.Cost(foot, at), g.Cost(foot, at));
        Assert.Equal(bare.SpeedPercent(foot, at, false), g.SpeedPercent(foot, at, false));
        Assert.Equal(0, g.Cost(light, at));
        Assert.Equal(0, g.Cost(wheeled, at));
        Assert.Equal(0, g.SpeedPercent(light, at, false));
        Assert.Equal(bare.Cost(tracked, at) * 300 / 100, g.Cost(tracked, at));
        Assert.Equal(bare.Cost(heavy, at) * 300 / 100, g.Cost(heavy, at));
        Assert.Equal(Math.Min(bare.SpeedPercent(tracked, at, false), 30), g.SpeedPercent(tracked, at, false));
        // Dense forest no longer stops the pushers (its undergrowth slows them); it is shut to cars, trunk or not.
        var dense = Flat(32, (_, _) => 3);
        var open = Enumerable.Range(0, 32).Select(x => new Cell(x, 9)).First(c => !dense.HasTrunk(c));
        Assert.True(dense.Cost(tracked, open) > 0);
        Assert.True(dense.Cost(heavy, open) > 0);
        Assert.Equal(0, dense.Cost(light, open));
        Assert.Equal(0, dense.Cost(wheeled, open));
        Assert.Equal(Flat(32, (_, _) => 3, NoTrees).Cost(foot, open), dense.Cost(foot, open));
        // Cleared, the trunk is gone; dense again, a felled stump stays a stump.
        g.SetForest(at, 0);
        Assert.False(g.HasTrunk(at));
        g.SetForest(at, 3);
        Assert.True(g.HasTrunk(at));
        Assert.True(g.FellTrunk(at));
        g.SetForest(at, 2);
        Assert.False(g.HasTrunk(at));
        Assert.Equal(Flat(32, (_, _) => 2, NoTrees).Cost(light, at), g.Cost(light, at)); // the grove's undergrowth only
    }

    [Fact]
    public void AJeepDrivesThroughAGroveBetweenTheTrunks()
    {
        const int n = 72;
        var w = Arena.NewSentinel(n);
        w.AttachTerrain(Flat(n, (x, _) => x is >= 16 and < 56 ? 2 : 0));
        var g = w.Ground!;
        var (jeep, _) = w.SpawnCrewed(1, "humvee", 6, 36, Facing.East);
        w.Order(1, CommandKind.Move, [jeep], cell: new Cell(66, 36));
        w.Step();
        var inGrove = 0;
        for (var t = 0; t < 3000 && jeep.Order.Kind == OrderKind.Move; t++)
        {
            w.Step();
            var c = Cell.Of(jeep.Pos);
            Assert.False(g.HasTrunk(c), $"the jeep's centre is in a trunk's cell {c} at tick {w.Tick}");
            Assert.True(Overlap(g, jeep) <= jeep.Def.Archetype.Radius.Raw / 8, $"the jeep is {Overlap(g, jeep)} raw into a trunk's cell at tick {w.Tick}");
            inGrove += c.X is >= 16 and < 56 ? 1 : 0;
        }
        Assert.Equal(OrderKind.Idle, jeep.Order.Kind);
        Assert.Equal(new Cell(66, 36), Cell.Of(jeep.Pos));
        Assert.True(inGrove > 40 * 20 / 40, "the jeep crossed the grove"); // the grove spans the map: no way round it
        Assert.True(g.HasTrunks);
    }

    [Fact]
    public void AJeepGoesRoundDenseForestOrStopsShortWithoutWedging()
    {
        const int n = 72;
        // Dense forest over the top two thirds: the jeep can't get between the trunks, so it goes round below.
        var w = Arena.NewSentinel(n);
        w.AttachTerrain(Flat(n, (x, y) => x is >= 20 and < 52 && y < 48 ? 3 : 0));
        var g = w.Ground!;
        var (jeep, _) = w.SpawnCrewed(1, "humvee", 6, 20, Facing.East);
        w.Order(1, CommandKind.Move, [jeep], cell: new Cell(66, 20));
        w.Step();
        var below = false;
        for (var t = 0; t < 4000 && jeep.Order.Kind == OrderKind.Move; t++)
        {
            w.Step();
            Assert.False(g.HasTrunk(Cell.Of(jeep.Pos)));
            Assert.True(Overlap(g, jeep) <= jeep.Def.Archetype.Radius.Raw / 8);
            below |= jeep.Pos.Y.Raw >= Fixed.FromInt(48).Raw && jeep.Pos.X.Raw > Fixed.FromInt(30).Raw;
        }
        Assert.True(below, "the jeep went round the forest");
        Assert.Equal(new Cell(66, 20), Cell.Of(jeep.Pos));

        // Dense forest across the whole map: no way through for a jeep. It ends short of it and stays still: no wedging,
        // no back and forth.
        var d = Arena.NewSentinel(n);
        d.AttachTerrain(Flat(n, (x, _) => x is >= 20 and < 52 ? 3 : 0));
        var (stuck, _) = d.SpawnCrewed(1, "humvee", 6, 36, Facing.East);
        d.Order(1, CommandKind.Move, [stuck], cell: new Cell(66, 36));
        var last = new List<Vec>();
        for (var t = 0; t < 1500; t++)
        {
            d.Step();
            Assert.False(d.Ground!.HasTrunk(Cell.Of(stuck.Pos)));
            if (t >= 1500 - 100)
            {
                last.Add(stuck.Pos);
            }
        }
        Assert.True(stuck.Pos.X.Raw < Fixed.FromInt(52).Raw, "the jeep did not get through dense forest");
        Assert.Equal(OrderKind.Idle, stuck.Order.Kind); // it went as near as it could, into a pocket between the trunks, and stopped
        long mx = last.Sum(p => p.X.Raw) / last.Count, my = last.Sum(p => p.Y.Raw) / last.Count;
        var variance = last.Sum(p => (p.X.Raw - mx) * (p.X.Raw - mx) + (p.Y.Raw - my) * (p.Y.Raw - my)) / last.Count;
        Assert.True(variance <= 32 * 32, $"the jeep kept moving over its last 5 s: variance {variance} raw^2");
    }

    [Fact]
    public void InfantryWalkThroughDenseForestAsBefore()
    {
        const int n = 64;
        (uint Tick, Vec Pos)[] Walk(TerrainRules rules)
        {
            var w = Arena.New(n);
            w.AttachTerrain(Flat(n, (x, _) => x is >= 16 and < 48 ? 3 : 0, rules));
            var squad = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "Rifleman", 4, 28 + i * 2)).ToList();
            w.Order(1, CommandKind.Move, squad, cell: new Cell(58, 32));
            var arrived = new (uint, Vec)[squad.Count];
            for (var t = 0; t < 3000; t++)
            {
                w.Step();
                for (var i = 0; i < squad.Count; i++)
                {
                    if (arrived[i].Item1 == 0 && squad[i].Order.Kind == OrderKind.Idle)
                    {
                        arrived[i] = (w.Tick, squad[i].Pos);
                    }
                }
            }
            Assert.True(w.Ground!.HasTrunks == (rules.Trees is not null));
            return arrived;
        }
        var with = Walk(Rules);
        var without = Walk(NoTrees);
        Assert.All(with, a => Assert.NotEqual(0u, a.Tick));
        Assert.Equal(without, with); // tick for tick: trunks are nothing to them
    }

    // Tank: a tank sent through dense forest, every tick checked. Returns the world.
    private static World TankThroughDenseForest(Func<World, Unit, int, bool>? each = null, string tank = "main_battle_tank", TerrainRules? rules = null)
    {
        const int n = 64;
        var w = Arena.NewSentinel(n);
        w.AttachTerrain(Flat(n, (x, _) => x is >= 20 and < 40 ? 3 : 0, rules));
        var (t, _) = w.SpawnCrewed(1, tank, 8, 32, Facing.East);
        w.Order(1, CommandKind.Move, [t], cell: new Cell(54, 32));
        w.Step();
        for (var k = 0; k < 4000 && t.Order.Kind == OrderKind.Move; k++)
        {
            w.Step();
            if (each is not null && !each(w, t, k))
            {
                break;
            }
        }
        return w;
    }

    // Cross sends a tank or a cargo truck east along row y through a 20-cell band of dense forest (x 20 to 39) on an open 64 x 64
    // map and returns the ticks it took to arrive (6,000 at most), and whether it did.
    private static (int Ticks, bool Arrived) Cross(string unit, int y, TerrainRules rules)
    {
        const int n = 64;
        var w = unit == "cargo_truck" ? Arena.New(n) : Arena.NewSentinel(n);
        w.AttachTerrain(Flat(n, (x, _) => x is >= 20 and < 40 ? 3 : 0, rules));
        var (u, _) = w.SpawnCrewed(1, unit, 8, y, Facing.East);
        w.Order(1, CommandKind.Move, [u], cell: new Cell(54, y));
        w.Step();
        var k = 0;
        for (; k < 6000 && u.Order.Kind == OrderKind.Move; k++)
        {
            w.Step();
        }
        return (k, Cell.Of(u.Pos).X >= 52);
    }

    [Fact]
    public void TanksAndHeavyTrucksStillCrossDenseForestAtTheRulesDensity()
    {
        // Owner question (2026-10-25, trunkChance 50 / 85): do the pushers now avoid dense forest? On 9 lanes (9 trunk layouts) a
        // tank and a heavy truck each get across a 20-cell band of it. Pushing costs time (a tree is 1.5 s of contact at 30% of
        // the speed): measured on this layout, the tank takes 593 ticks against 403 with no trunks, the truck 933 against 769;
        // with the first take (35 / 70) 574 and 882. So allow under twice the undergrowth-only time.
        foreach (var unit in new[] { "main_battle_tank", "cargo_truck" })
        {
            long with = 0, without = 0;
            var lanes = 0;
            for (var y = 8; y <= 56; y += 6)
            {
                var (ticks, arrived) = Cross(unit, y, Rules);
                Assert.True(arrived, $"{unit} did not get across dense forest on row {y}");
                with += ticks;
                without += Cross(unit, y, NoTrees).Ticks;
                lanes++;
            }
            Assert.True(with < without * 2, $"{unit}: {with / lanes} ticks a lane through the trunks, {without / lanes} without");
            Assert.True(with > without, $"{unit}: pushing the trunks over cost it no time");
        }
    }

    [Fact]
    public void ATankPushesItsWayThroughDenseForest()
    {
        var g0 = (TerrainMap?)null;
        var falls = new List<TreeEvent>();
        var pushing = 0;
        var contact = 0;
        var light = Class("light_wheeled");
        var tracked = Class("tracked");
        var openDense = Flat(4, (_, _) => 3, NoTrees).Cost(tracked, new Cell(0, 0)); // dense undergrowth without a trunk
        var blocked = new List<(FallenTrunk Trunk, uint Tick)>();
        var w = TankThroughDenseForest((w, t, k) =>
        {
            var g = w.Ground!;
            g0 = g;
            contact = t.PushContact ? contact + 1 : 0;
            if (contact > 0)
            {
                pushing++;
            }
            if (contact > 3)
            {
                // Pressing on a trunk it slows to its push speed (braking from full speed takes up to 3 ticks).
                var top = t.Def.Speed.Raw * 30 / 100;
                Assert.True(t.DriveSpeed.Raw <= top + 2, $"pushing at {t.DriveSpeed.Raw}, more than the push speed {top}"); // (+2: a capped vector's length rounds)
            }
            foreach (var e in w.TreeEvents.Where(e => e.Kind == TreeEventKind.Fell))
            {
                falls.Add(e);
                Assert.Equal(t.Id, e.By);
                Assert.Equal(TreeTrunks.DirOf(t.Facing), e.Dir); // the way the tank faces
                Assert.False(g.HasTrunk(e.Stump));                // its stump's cell opens
                var f = w.FallenTrunks.Single(f => f.Stump == e.Stump);
                Assert.Equal(w.Tick - 1 + 600, f.ExpiresAt);
                Assert.Equal(e.Length, f.Length);
                for (var i = 1; i <= f.Length; i++)
                {
                    Assert.True(g.Fallen(f.CellAt(i)));
                    Assert.Equal(0, g.Cost(light, f.CellAt(i))); // a wall to a jeep
                }
                if (!g.Fallen(e.Stump))
                {
                    Assert.Equal(openDense, g.Cost(tracked, e.Stump)); // the stump's cell opens: undergrowth only (unless a trunk lies over it)
                }
                blocked.Add((f, w.Tick - 1));
            }
            // Each fallen trunk blocks a jeep its whole time and not a tick more.
            foreach (var (f, felled) in blocked)
            {
                var age = w.Tick - 1 - felled;
                var lying = w.FallenTrunks.Contains(f);
                Assert.Equal(age < 600, lying);
            }
            return true;
        });
        Assert.True(pushing > 30, "the tank pushed");
        Assert.True(falls.Count >= 3, $"the tank felled {falls.Count} trees");
        var tank = w.All("main_battle_tank").Single();
        Assert.Equal(OrderKind.Idle, tank.Order.Kind);
        Assert.True(tank.Pos.X.Raw > Fixed.FromInt(50).Raw, "the tank got through");
        Assert.Equal(falls.Select(e => e.Stump).OrderBy(c => c.Y * 64 + c.X), w.Stumps);
        // Every fallen trunk is gone 600 ticks after its fall.
        w.Run(620);
        Assert.Empty(w.FallenTrunks);
        Assert.All(falls, e => Assert.False(g0!.HasTrunk(e.Stump)));
        Assert.DoesNotContain(Enumerable.Range(0, 64 * 64), i => g0!.Fallen(new Cell(i % 64, i / 64)));
    }

    [Fact]
    public void AFallenTrunkBlocksAJeepForItsTimeThenItPasses()
    {
        const int n = 40;
        var w = Arena.NewSentinel(n);
        var at = OneTree(14, 20, density: 2); // a grove tree: dense forest is shut to cars, trunk or not
        w.AttachTerrain(Flat(n, (x, y) => x == at.X && y == at.Y ? 2 : 0));
        var g = w.Ground!;
        var (tank, _) = w.SpawnCrewed(1, "main_battle_tank", at.X - 6, at.Y, Facing.East);
        Drive(tank, new Cell(at.X + 12, at.Y));
        for (var t = 0; t < 400 && w.FallenTrunks.Count == 0; t++)
        {
            w.Step();
        }
        var f = Assert.Single(w.FallenTrunks);
        Assert.Equal(at, f.Stump);
        Assert.Equal(2, f.Dir); // east, the way the tank drove
        Assert.Equal(4, f.Length);
        var felled = w.Tick - 1;
        // Walls across the map but for two rows at the trunk: a jeep must pass along it, over its cells.
        var gap = f.CellAt(2).X;
        for (var y = 0; y < n; y++)
        {
            if (y != at.Y && y != at.Y + 1)
            {
                w.Grid.Set(new Cell(gap, y), Sim.Terrain.Blocked);
            }
        }
        tank.ClearPath(); // out of the way
        tank.Order = default;
        tank.Pos = new Cell(at.X + 10, at.Y + 10).Center();
        var (jeep, _) = w.SpawnCrewed(1, "humvee", gap - 8, at.Y, Facing.East);
        var passedAt = 0u;
        for (var t = 0; t < 1200 && passedAt == 0; t++)
        {
            if (t % 100 == 0)
            {
                w.Order(1, CommandKind.Move, [jeep], cell: new Cell(gap + 8, at.Y)); // a player sending it again and again
            }
            w.Step();
            Assert.True(Overlap(g, jeep) <= jeep.Def.Archetype.Radius.Raw / 8, "the jeep drove onto the fallen trunk");
            if (jeep.Pos.X.Raw > Fixed.FromInt(gap + 1).Raw)
            {
                passedAt = w.Tick;
            }
        }
        Assert.True(passedAt > felled + 600, $"the jeep got past at tick {passedAt}, the trunk fell at {felled}");
        Assert.True(passedAt < felled + 600 + 200, $"the jeep got past at tick {passedAt}, the trunk was gone at {felled + 600}");
        Assert.Empty(w.FallenTrunks);
    }

    [Fact]
    public void AHeavyTruckPushesTreesOverAndALightTruckCannot()
    {
        const int n = 40;
        foreach (var (truck, pushes) in new[] { ("cargo_truck", true), ("scout_truck", false) })
        {
            var w = Arena.New(n);
            var at = OneTree(14, 20, density: 2); // a grove tree: dense forest would stop the light truck anyway
            w.AttachTerrain(Flat(n, (x, y) => x == at.X && y == at.Y ? 2 : 0));
            var g = w.Ground!;
            var (u, _) = w.SpawnCrewed(1, truck, at.X - 6, at.Y, Facing.East);
            Drive(u, new Cell(at.X + 10, at.Y));
            for (var t = 0; t < 300; t++)
            {
                w.Step();
                if (!pushes)
                {
                    Assert.True(Overlap(g, u) <= u.Def.Archetype.Radius.Raw / 8, "the light truck drove into the trunk");
                }
            }
            Assert.Equal(pushes, w.FallenTrunks.Count == 1);
            Assert.Equal(!pushes, g.HasTrunk(at));
            if (pushes)
            {
                Assert.Equal(u.Id, w.FallenTrunks[0].By);
                Assert.Equal(2, w.FallenTrunks[0].Dir);
            }
        }
    }

    [Fact]
    public void BuildingOverTreesClearsTheTrunkAndAFallenTrunk()
    {
        const int n = 48;
        var w = Arena.New(n);
        // A depot (4 x 4) at (20, 20) clears x 19 to 24, y 19 to 24: a tree standing there, and one north of it that falls
        // south across it.
        Cell First(int x0, int x1, int y0, int y1) =>
            Enumerable.Range(y0, y1 - y0 + 1).SelectMany(y => Enumerable.Range(x0, x1 - x0 + 1).Select(x => new Cell(x, y)))
                .First(c => TreeTrunks.Stands(c.X, c.Y, Rules.Trees!.TrunkChance[3]));
        var at = First(19, 24, 19, 24);
        var other = First(19, 24, 15, 18);
        w.AttachTerrain(Flat(n, (x, y) => (x == at.X && y == at.Y) || (x == other.X && y == other.Y) ? 3 : 0));
        var g = w.Ground!;
        Assert.True(g.HasTrunk(at));
        Assert.True(w.FellTree(other, 4, 0, TreeFallCause.Pushed, default)); // south, over row 19
        var lying = Assert.Single(w.FallenTrunks);
        Assert.Equal(4, lying.Length);
        Assert.True(g.Fallen(new Cell(other.X, 19)));
        var worker = w.Spawn(1, "worker", 6, 30);
        var site = new Cell(20, 20);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = site, Unit = "supply_depot" });
        w.Step();
        Assert.NotNull(w.Units.FirstOrDefault(u => u.Def.Archetype.Id == "supply_depot"));
        Assert.False(g.HasTrunk(at));
        Assert.Empty(w.FallenTrunks);
        Assert.Contains(w.TreeEvents, e => e.Kind == TreeEventKind.Cleared && e.Stump == lying.Stump);
        for (var k = 1; k <= lying.Length; k++)
        {
            Assert.False(g.Fallen(lying.CellAt(k)));
        }
        Assert.Contains(other, w.Stumps); // a stump stays a stump
    }

    [Fact]
    public void TheHashCoversFallsAndFallenTrunks()
    {
        const int n = 40;
        var at = OneTree(10, 20);
        World Make()
        {
            var w = Arena.New(n);
            w.AttachTerrain(Flat(n, (x, y) => x == at.X && y == at.Y ? 3 : 0));
            return w;
        }
        var untouched = Make();
        var lying = Make();
        var cleared = Make();
        Assert.Equal(untouched.Hash(), lying.Hash());
        Assert.True(lying.FellTree(at, 2, 0, TreeFallCause.Pushed, default));
        Assert.True(cleared.FellTree(at, 2, 0, TreeFallCause.Pushed, default));
        Assert.NotEqual(untouched.Hash(), lying.Hash()); // the fall
        Assert.Equal(lying.Hash(), cleared.Hash());
        cleared.ClearTrees(new Cell(at.X + 2, at.Y));
        Assert.NotEqual(lying.Hash(), cleared.Hash()); // a fallen trunk lying or not
        for (var t = 0; t <= 600; t++)
        {
            untouched.Step();
            lying.Step();
            cleared.Step();
            if (t < 600)
            {
                Assert.NotEqual(lying.Hash(), cleared.Hash()); // ticks 0 to 599: it lies
            }
        }
        Assert.Empty(lying.FallenTrunks); // gone in tick 600: its expiry is in the hash
        Assert.Equal(lying.Hash(), cleared.Hash());
        Assert.NotEqual(untouched.Hash(), lying.Hash()); // the stump stays
    }

    [Fact]
    public void AFallRefreshesRoutingOnlyRoundIt()
    {
        const int n = 96;
        var at = OneTree(40, 48);
        var w = Arena.NewSentinel(n);
        w.AttachTerrain(Flat(n, (x, y) => x == at.X && y == at.Y ? 3 : 0));
        var (jeep, _) = w.SpawnCrewed(1, "humvee", 10, 10, Facing.East);
        w.PrepareRouting();
        w.Step();
        var nav = w.Routing.Nav;
        var before = nav.RefreshedCells;
        Assert.True(w.FellTree(at, 2, 0, TreeFallCause.Pushed, default));
        w.Step();
        var refreshed = nav.RefreshedCells - before;
        // The stump and its 4 cells east: one rectangle with the clearance reach (4) round it, 13 x 9 cells, not the map.
        Assert.InRange(refreshed, 1, (5 + 2 * (NavGrid.MaxWidth - 1)) * (1 + 2 * (NavGrid.MaxWidth - 1)));
        // And the jeep's routing sees the trunk: its class can't stand on it.
        var k = w.Routing.ClassOf(jeep.Def);
        Assert.Equal(0, nav.Cost(k, at.X + 2, at.Y));
        w.Run(600 + 1 + 1); // gone in its 600th tick, routing sees it the tick after
        Assert.True(nav.Cost(k, at.X + 2, at.Y) > 0);
    }

    [Fact]
    public void AFallLeavesTheRoutingOfClassesItDoesNotChangeAlone()
    {
        // A trunk falling changes no cost for infantry: their sectors stay built and a squad's flow is not re-planned; a jeep's
        // sectors round it are rebuilt. And fog ignores it: trunks change no sight.
        const int n = 96;
        var at = OneTree(40, 48);
        var w = Arena.NewSentinel(n);
        w.AttachTerrain(Flat(n, (x, y) => x == at.X && y == at.Y ? 3 : 0));
        var (jeep, _) = w.SpawnCrewed(1, "humvee", 10, 10, Facing.East);
        var squad = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 10 + i, 80)).ToList();
        w.PrepareRouting();
        w.Order(1, CommandKind.Move, squad, cell: new Cell(80, 20)); // across the tree's sector
        w.Run(5);
        var r = w.Routing;
        int foot = r.ClassOf(squad[0].Def), car = r.ClassOf(jeep.Def);
        var sector = at.Y / Hpa.S * r.Graph(foot).SX + at.X / Hpa.S;
        Assert.True(r.Graph(foot).IsBuilt(sector) && r.Graph(car).IsBuilt(sector));
        Assert.True(w.FellTree(at, 2, 0, TreeFallCause.Pushed, default));
        w.Step();
        Assert.True(r.Graph(foot).IsBuilt(sector), "infantry's sector was dropped for a change that is nothing to them");
        Assert.False(r.Graph(car).IsBuilt(sector), "the jeep's sector was kept though its costs changed");
        Assert.Equal(0, w.PathRequests); // the squad's flow was not re-planned
        var cells = new List<Cell>();
        Assert.True(w.Ground!.ChangedSince(0, cells, sightOnly: true));
        Assert.Empty(cells); // fog: no sight changed
        Assert.True(w.Ground.ChangedSince(0, cells) && cells.Contains(at)); // renderers: the tree's cell
    }

    [Fact]
    public void TankPushesAreDeterministic()
    {
        ulong[] Run(bool reverse)
        {
            var hashes = new List<ulong>();
            TankThroughDenseForest((w, _, _) =>
            {
                w.ReverseMoveOrder = reverse;
                hashes.Add(w.Hash());
                return hashes.Count < 1200;
            });
            return [.. hashes];
        }
        var a = Run(false);
        Assert.Equal(a, Run(false));
        Assert.Equal(a, Run(true));
    }

    [Theory]
    [InlineData("\"trunks\": \"push\", \"pushCost\": 300, \"pushSpeedPercent\": 30, \"pushTicks\": 30, \"crossFallen\": true, \"fireCost\": 0 }\n  ],", "\"trunks\": \"push\", \"pushCost\": 300, \"pushSpeedPercent\": 30, \"pushTicks\": 30, \"crossFallen\": true, \"fireCost\": 0, \"bark\": 1 }\n  ],", "bark")]
    [InlineData("\"trunks\": \"ignore\"", "\"forestBlockedFrom\": 4", "trunks wants")]
    [InlineData("\"trunks\": \"ignore\"", "\"trunks\": \"climb\"", "got \"climb\"")]
    [InlineData("\"trunks\": \"ignore\"", "\"trunks\": \"ignore\", \"pushTicks\": 5", "are for a class")]
    [InlineData("\"pushSpeedPercent\": 30, \"pushTicks\": 30, \"crossFallen\": true, \"fireCost\": 0 }\n  ],", "\"pushSpeedPercent\": 0, \"pushTicks\": 30, \"crossFallen\": true, \"fireCost\": 0 }\n  ],", "pushSpeedPercent wants")]
    [InlineData("\"trunkChance\": [0, 0, 50, 85]", "\"trunkChance\": [0, 0, 50, 170]", "trunkChance wants")]
    [InlineData("\"fallenTicks\": 600", "\"fallenTicks\": 0", "fallenTicks wants")]
    public void TreeRulesAreChecked(string from, string to, string error)
    {
        var json = File.ReadAllText(RulesPath).Replace("\r\n", "\n");
        Assert.Contains(from, json);
        var e = Assert.Throws<ContentException>(() => TerrainLoader.ParseRules(ReplaceFirst(json, from, to)));
        Assert.Contains(error, e.Message);
    }

    private static string ReplaceFirst(string s, string from, string to)
    {
        var i = s.IndexOf(from, StringComparison.Ordinal);
        return s[..i] + to + s[(i + from.Length)..];
    }

    [Fact]
    public void TheTreeRulesAreInTheTerrainDigest()
    {
        var json = File.ReadAllText(RulesPath);
        var other = TerrainLoader.ParseRules(json.Replace("\"fallenTicks\": 600", "\"fallenTicks\": 601"));
        Assert.NotEqual(Rules.Digest(), other.Digest());
        Assert.NotEqual(Rules.Digest(), NoTrees.Digest());
        Assert.Equal(Rules.Digest(), TerrainLoader.ParseRules(json).Digest());
    }
}
