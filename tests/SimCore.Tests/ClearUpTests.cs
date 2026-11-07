using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimCore.Tests;

// The bulldozer and trees (docs/design.md, "Clear up"; decisions 960 to 972): it fells trees as it drives, much faster than a tank, and
// takes the Clear up order: an area worked over until nothing it can remove is left.
public class ClearUpTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static TerrainMap Flat(int n, Func<int, int, int> forest)
    {
        var flags = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                flags[y * n + x] = (byte)forest(x, y);
            }
        }
        return new TerrainMap(n, n, new int[(n + 1) * (n + 1)], flags, Rules);
    }

    private static void ClearUp(World w, Unit dozer, Cell from, Cell to) =>
        w.Issue(new Command { Tick = w.Tick, Player = dozer.Owner, Kind = CommandKind.ClearArea, Units = [dozer.Id], Cell = from, Cell2 = to });

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

    private static int TreesIn(World w, Cell a, Cell b)
    {
        var n = 0;
        for (var y = a.Y; y <= b.Y; y++)
        {
            for (var x = a.X; x <= b.X; x++)
            {
                n += w.Ground!.HasTrunk(new Cell(x, y)) ? 1 : 0;
            }
        }
        return n;
    }

    private static int ForestIn(World w, Cell a, Cell b)
    {
        var n = 0;
        for (var y = a.Y; y <= b.Y; y++)
        {
            for (var x = a.X; x <= b.X; x++)
            {
                n += w.Ground!.Forest(new Cell(x, y)) != 0 ? 1 : 0;
            }
        }
        return n;
    }

    // Drive runs archetype name along a lane of dense wood 40 cells long: the ticks across and the trees it felled.
    private static (int Ticks, int Trees) Drive(string name)
    {
        var w = Arena.NewSentinel(96);
        w.AttachTerrain(Flat(96, (x, y) => x is >= 20 and < 60 ? 3 : 0));
        var (u, _) = name == "bulldozer" ? (w.Spawn(1, name, 15, 40, Facing.East), (Unit?)null) : w.SpawnCrewed(1, name, 15, 40, Facing.East);
        w.Order(1, CommandKind.Move, [u], cell: new Cell(64, 40));
        var t = RunUntil(w, () => u.Pos.X.Raw >= Fixed.FromInt(62).Raw, 6000);
        Assert.True(t > 0, $"{name} never crossed");
        return (t, w.Stumps.Count);
    }

    [Fact]
    public void ItFellsTreesMuchFasterThanATankAndKeepsMoving()
    {
        var (dt, dn) = Drive("bulldozer");
        var (tt, tn) = Drive("main_battle_tank");
        Assert.True(dn > 0 && tn > 0, $"felled: bulldozer {dn}, tank {tn}");
        // Trees a second, compared without a division: the bulldozer at least twice the tank's, and the wood crossed sooner.
        Assert.True((long)dn * tt >= 2L * tn * dt, $"bulldozer {dn} trees in {dt} ticks, tank {tn} in {tt}");
        Assert.True(dt < tt, $"bulldozer {dt} ticks, tank {tt}");
    }

    [Fact]
    public void OnlyTheBulldozerHasTheFastPushAndTheResistanceHasNone()
    {
        var w = Arena.NewSentinel(32);
        var d = w.Player(1)!.Setup.Find(w.Content, "bulldozer")!;
        var tank = w.Player(1)!.Setup.Find(w.Content, "main_battle_tank")!;
        var dozer = Rules.Classes[Rules.ClassOf(d)];
        var tracked = Rules.Classes[Rules.ClassOf(tank)];
        Assert.Equal(("dozer", 10, 60, 100), (dozer.Id, dozer.PushTicks, dozer.PushSpeedPercent, dozer.PushCost));
        Assert.Equal(("tracked", 30, 30, 300), (tracked.Id, tracked.PushTicks, tracked.PushSpeedPercent, tracked.PushCost));
        Assert.Equal("tracked", Rules.Classes[Rules.ClassOf(Locomotor.Tracked, d.Archetype.Radius)].Id); // by locomotor nobody takes its class
        Assert.Equal(1, w.Content.Archetypes.Count(a => a.Clear is not null));
        Assert.False(TestContent.Mvp.Faction("resistance")!.Archetypes[d.Archetype.Index]);
    }

    [Fact]
    public void ItsRoutesGoStraightThroughWoodsWhereATanksGoRound()
    {
        // A dense belt 6 cells thick across the map with a gap 10 cells off the straight line.
        static TerrainMap Belt() => Flat(64, (x, y) => x is >= 30 and < 36 && y is not (>= 18 and <= 26) ? 3 : 0);
        var (dw, tw) = (Arena.NewSentinel(64), Arena.NewSentinel(64));
        dw.AttachTerrain(Belt());
        tw.AttachTerrain(Belt());
        var dozer = dw.Spawn(1, "bulldozer", 12, 32, Facing.East);
        var (tank, _) = tw.SpawnCrewed(1, "main_battle_tank", 12, 32, Facing.East);
        dw.Order(1, CommandKind.Move, [dozer], cell: new Cell(52, 32));
        tw.Order(1, CommandKind.Move, [tank], cell: new Cell(52, 32));
        Assert.True(RunUntil(dw, () => dozer.Pos.X.Raw >= Fixed.FromInt(33).Raw, 4000) > 0);
        Assert.True(RunUntil(tw, () => tank.Pos.X.Raw >= Fixed.FromInt(33).Raw, 4000) > 0);
        var (dy, ty) = ((int)(dozer.Pos.Y.Raw >> Fixed.Shift), (int)(tank.Pos.Y.Raw >> Fixed.Shift));
        Assert.True(dw.Stumps.Count > 0 && dy >= 28, $"the bulldozer crossed at row {dy}, {dw.Stumps.Count} felled: it went round");
        Assert.True(ty <= 27, $"the tank crossed at row {ty}: it should take the gap");
    }

    [Fact]
    public void ATrunkItFellsDrivingLiesTheNormalTime()
    {
        var w = Arena.NewSentinel(64);
        w.AttachTerrain(Flat(64, (x, _) => x is >= 20 and < 30 ? 3 : 0));
        var u = w.Spawn(1, "bulldozer", 15, 32, Facing.East);
        w.Order(1, CommandKind.Move, [u], cell: new Cell(40, 32));
        Assert.True(RunUntil(w, () => w.FallenTrunks.Count > 0, 1000) > 0);
        Assert.Equal(w.Tick - 1 + 600, w.FallenTrunks[0].ExpiresAt);
    }

    [Fact]
    public void AnAreaIsEmptiedToTheEndAndItIdles()
    {
        var w = Arena.NewSentinel(64);
        w.AttachTerrain(Flat(64, (x, y) => x is >= 20 and < 44 && y is >= 20 and < 44 ? 3 : 0));
        var u = w.Spawn(1, "bulldozer", 15, 32, Facing.East);
        var (a, b) = (new Cell(20, 20), new Cell(43, 43));
        var trees = TreesIn(w, a, b);
        ClearUp(w, u, a, b);
        w.Step();
        Assert.Equal(OrderKind.ClearArea, u.Order.Kind);
        Assert.NotNull(u.Clearing);
        var t = RunUntil(w, () => u.Order.Kind != OrderKind.ClearArea, 12000);
        Assert.True(t > 0, $"not done: {TreesIn(w, a, b)} trees, {w.FallenTrunks.Count} trunks, {ForestIn(w, a, b)} forest cells left");
        Assert.Equal(0, TreesIn(w, a, b));
        Assert.Equal(0, ForestIn(w, a, b));
        Assert.DoesNotContain(w.FallenTrunks, f => a.X <= f.Stump.X && f.Stump.X <= b.X && a.Y <= f.Stump.Y && f.Stump.Y <= b.Y);
        Assert.Null(u.Clearing);
        Assert.Equal(OrderKind.Idle, u.Order.Kind);
        Assert.True(trees > 100, $"{trees} trees");
    }

    [Fact]
    public void ItsOwnBuildingsARoadAndARockStandAndWhatIsOutsideTheAreaToo()
    {
        var w = Arena.NewSentinel(64);
        w.AttachTerrain(Flat(64, (x, y) => y is >= 20 and < 44 ? 3 : 0));
        var u = w.Spawn(1, "bulldozer", 12, 32, Facing.East);
        var depot = w.Spawn(1, "supply_depot", 24, 26); // its own, with trees round it
        var (a, b) = (new Cell(20, 24), new Cell(32, 36));
        var outside = TreesIn(w, new Cell(36, 20), new Cell(63, 43));
        var outsideForest = ForestIn(w, new Cell(36, 20), new Cell(63, 43));
        ClearUp(w, u, a, b);
        Assert.True(RunUntil(w, () => u.Order.Kind != OrderKind.ClearArea, 8000) > 0);
        Assert.Equal(depot.Def.MaxHp, depot.HP);
        Assert.Equal(outside, TreesIn(w, new Cell(36, 20), new Cell(63, 43)));
        Assert.Equal(outsideForest, ForestIn(w, new Cell(36, 20), new Cell(63, 43)));
    }

    [Fact]
    public void ABridgeInTheAreaIsLeftStanding()
    {
        var w = BridgeTests.River(64, 30, sentinel: true, sites: [new BridgeSite("stone", new Cell(30, 27), Facing.South)]);
        var bridge = Assert.Single(w.Bridges);
        var u = w.Spawn(1, "bulldozer", 20, 25, Facing.East);
        var shack = w.Spawn(2, "shack_tin", 24, 22);
        ClearUp(w, u, new Cell(20, 20), new Cell(40, 40));
        Assert.True(RunUntil(w, () => u.Order.Kind != OrderKind.ClearArea, 2000) > 0);
        Assert.False(shack.Alive); // what it can remove went
        Assert.Equal(bridge.Def.MaxHp, bridge.HP); // and the bridge stands
        Assert.Equal(0, bridge.Stage);
    }

    [Fact]
    public void AHouseGoesInAboutTwentySecondsThroughItsDamageStagesAndLeavesARuin()
    {
        var w = Arena.NewSentinel(64);
        var house = w.Spawn(2, "house_small", 30, 30);
        var u = w.Spawn(1, "bulldozer", 25, 32, Facing.East);
        ClearUp(w, u, new Cell(28, 28), new Cell(36, 36));
        var cell = new Cell(32, 32);
        Assert.False(w.Grid.Passable(cell));
        var maxStage = 0;
        var started = 0;
        var t = RunUntil(w, () =>
        {
            maxStage = Math.Max(maxStage, house.Stage);
            started = started == 0 && house.HP < house.Def.MaxHp ? (int)w.Tick : started;
            return !house.Alive;
        }, 2000);
        Assert.True(t > 0);
        var took = (int)w.Tick - started;
        Assert.InRange(took, 380, 430); // 700 HP and 25 cells: 405 ticks, 20 s
        Assert.True(maxStage >= 2, $"stage {maxStage}");
        w.Run(2);
        Assert.Null(w.Unit(house.Id)); // the building's gone
        Assert.False(w.Grid.Passable(cell)); // and its ruin stays, blocking its footprint
        Assert.Equal(OrderKind.Idle, u.Order.Kind);
    }

    [Fact]
    public void ABigBlockTakesOverAMinute()
    {
        var w = Arena.NewSentinel(64);
        var d = w.Player(1)!.Setup.Find(w.Content, "bulldozer")!.Archetype.Clear!;
        var block = w.Spawn(2, "block_panel_5", 20, 20);
        var ticks = World.ClearTicks(d, block);
        Assert.True(ticks > 60 * 20, $"{ticks} ticks");
        Assert.InRange(World.ClearTicks(d, w.Spawn(2, "kiosk", 40, 40)), 80, 200);
    }

    [Fact]
    public void OccupantsWalkOutWhenItStartsAndOneBackInsideIsHurtByTheCollapse()
    {
        var w = Arena.NewSentinel(64);
        var house = w.Spawn(2, "house_small", 30, 30);
        var men = new[] { w.Spawn(2, "Rifleman", 30, 37), w.Spawn(2, "Rifleman", 31, 37), w.Spawn(2, "Rifleman", 32, 37) };
        w.Order(2, CommandKind.Enter, men, house.Id);
        Assert.True(RunUntil(w, () => house.Occupants == 3, 600) > 0);
        Assert.Equal(2, house.Owner); // it's the enemy's now
        var hp = men[0].HP;
        var u = w.Spawn(1, "bulldozer", 25, 32, Facing.East);
        ClearUp(w, u, new Cell(28, 28), new Cell(36, 36));
        Assert.True(RunUntil(w, () => house.HP < house.Def.MaxHp || men[0].Garrison == 0, 400) > 0);
        Assert.All(men, m => Assert.Equal(0u, m.Garrison)); // out, unhurt
        Assert.Equal(hp, men[0].HP);
        // One goes back in; it comes down on him.
        w.Order(2, CommandKind.Enter, [men[2]], house.Id);
        Assert.True(RunUntil(w, () => men[2].Garrison == house.Id, 200) > 0);
        Assert.True(RunUntil(w, () => !house.Alive, 800) > 0);
        w.Run(2);
        var g = house.Def.Archetype.Garrison!;
        Assert.Equal(0u, men[2].Garrison);
        Assert.True(men[2].HP <= men[2].Def.MaxHp * g.CollapseHp / 100, $"{men[2].HP} HP");
        Assert.Equal(hp, men[0].HP); // the one who stayed out: nothing
    }

    [Fact]
    public void AnEnemyBuildingIsClearedAndOneOfItsOwnSideIsNot()
    {
        var w = Arena.NewSentinel(64);
        var enemy = w.Spawn(2, "supply_depot", 30, 30);
        var own = w.Spawn(1, "supply_depot", 30, 40);
        var u = w.Spawn(1, "bulldozer", 25, 36, Facing.East);
        ClearUp(w, u, new Cell(28, 28), new Cell(44, 48));
        Assert.True(RunUntil(w, () => u.Order.Kind != OrderKind.ClearArea, 3000) > 0);
        Assert.False(enemy.Alive);
        Assert.Equal(own.Def.MaxHp, own.HP);
    }

    [Fact]
    public void AnEnemyBuildingFogHidesIsNoTarget()
    {
        var w = Arena.NewSentinel(64, edit: s => s with { Fog = true });
        var enemy = w.Spawn(2, "supply_depot", 50, 50); // far beyond its sight
        var u = w.Spawn(1, "bulldozer", 10, 10, Facing.East);
        ClearUp(w, u, new Cell(45, 45), new Cell(60, 60));
        w.Run(5);
        Assert.Equal(OrderKind.Idle, u.Order.Kind); // nothing it knows of
        Assert.Equal(enemy.Def.MaxHp, enemy.HP);
    }

    [Fact]
    public void WhatItCannotReachIsSkippedAndItCarriesOn()
    {
        var w = Arena.NewSentinel(64);
        w.AttachTerrain(Flat(64, (x, y) => x is >= 32 and < 40 && y is >= 32 and < 40 ? 3 : 0));
        var u = w.Spawn(1, "bulldozer", 15, 16, Facing.East);
        // A depot of the enemy's sealed in a ring of blocked cells, a few cells from the wood.
        var sealedIn = w.Spawn(2, "supply_depot", 22, 34);
        for (var y = 30; y <= 44; y++)
        {
            for (var x = 18; x <= 32; x++)
            {
                if (x is 18 or 32 || y is 30 or 44)
                {
                    w.Grid.Set(new Cell(x, y), Terrain.Blocked);
                }
            }
        }
        ClearUp(w, u, new Cell(20, 32), new Cell(39, 39));
        var t = RunUntil(w, () => u.Order.Kind != OrderKind.ClearArea, 8000);
        Assert.True(t > 0, "a stuck loop");
        Assert.Equal(sealedIn.Def.MaxHp, sealedIn.HP);
        Assert.Equal(0, TreesIn(w, new Cell(33, 32), new Cell(39, 39)));
    }

    [Fact]
    public void AnyOtherOrderStopsItAndItCanBeShot()
    {
        var w = Arena.NewSentinel(64);
        w.AttachTerrain(Flat(64, (_, _) => 3));
        var u = w.Spawn(1, "bulldozer", 32, 32, Facing.East);
        ClearUp(w, u, new Cell(20, 20), new Cell(44, 44));
        w.Run(40);
        Assert.NotNull(u.Clearing);
        var felled = w.Stumps.Count;
        w.Order(1, CommandKind.Stop, [u]);
        w.Step();
        Assert.Null(u.Clearing);
        w.Run(50);
        Assert.Equal(OrderKind.Idle, u.Order.Kind);
        Assert.True(w.Stumps.Count <= felled + 8); // what it was on when stopped, at most
        ClearUp(w, u, new Cell(20, 20), new Cell(44, 44));
        w.Step();
        Assert.NotNull(u.Clearing);
        w.Order(1, CommandKind.Move, [u], cell: new Cell(2, 2));
        w.Step();
        Assert.Null(u.Clearing);
        ClearUp(w, u, new Cell(20, 20), new Cell(44, 44));
        w.Step();
        u.HP = 1;
        var (_, _) = (w.Spawn(2, "Rifleman", (int)(u.Pos.X.Raw >> Fixed.Shift) + 3, (int)(u.Pos.Y.Raw >> Fixed.Shift)), 0);
        Assert.True(RunUntil(w, () => !u.Alive, 2000) > 0, "nothing shot it");
    }

    [Fact]
    public void AnAreaOverTheLimitOrWithoutASecondCornerIsRefusedAndOnlyABulldozerTakesIt()
    {
        var w = Arena.NewSentinel(128);
        var u = w.Spawn(1, "bulldozer", 15, 16);
        var (tank, _) = w.SpawnCrewed(1, "main_battle_tank", 20, 16);
        ClearUp(w, u, new Cell(0, 0), new Cell(60, 10)); // 61 cells long
        w.Step();
        Assert.Equal(OrderKind.Idle, u.Order.Kind);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.ClearArea, Units = [u.Id], Cell = new Cell(0, 0) });
        w.Step();
        Assert.Equal(OrderKind.Idle, u.Order.Kind);
        ClearUp(w, tank, new Cell(0, 0), new Cell(10, 10));
        w.Step();
        Assert.Equal(OrderKind.Idle, tank.Order.Kind);
    }

    private static List<ulong> Scene(bool reference = false)
    {
        var w = Arena.NewSentinel(64, seed: 5);
        w.AttachTerrain(Flat(64, (x, y) => x is >= 18 and < 40 && y is >= 18 and < 40 ? 3 : 0));
        w.Spawn(2, "house_small", 26, 26);
        var men = new[] { w.Spawn(2, "Rifleman", 26, 32), w.Spawn(2, "Rifleman", 27, 32) };
        w.Order(2, CommandKind.Enter, men, w.All("house_small")[0].Id);
        var (d1, d2) = (w.Spawn(1, "bulldozer", 14, 22, Facing.East), w.Spawn(1, "bulldozer", 14, 30, Facing.East));
        ClearUp(w, d1, new Cell(18, 18), new Cell(39, 39));
        ClearUp(w, d2, new Cell(18, 18), new Cell(39, 39));
        var hashes = new List<ulong>();
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            if (t % 100 == 99)
            {
                hashes.Add(w.Hash());
                if (reference)
                {
                    Assert.Equal(w.HashReference(), w.Hash());
                }
            }
        }
        return hashes;
    }

    [Fact]
    public void ItPlaysAndHashesTheSameTwice()
    {
        Assert.Equal(Scene(), Scene(reference: true));
    }

    [Fact]
    public void TheOrderGoesThroughAReplay()
    {
        var setup = TestContent.Sentinel(3);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var c = new Command { Tick = 10, Player = 1, Kind = CommandKind.ClearArea, Units = [7], Cell = new Cell(4, 5), Cell2 = new Cell(9, 12) };
        var rec = new Replay { Setup = setup, Ticks = 20, Commands = [c] };
        using var buf = new MemoryStream();
        rec.Encode(buf);
        buf.Position = 0;
        var back = Replay.Decode(buf).Commands.Single();
        Assert.Equal((CommandKind.ClearArea, new Cell(4, 5), new Cell(9, 12)), (back.Kind, back.Cell, back.Cell2!.Value));
    }
}
