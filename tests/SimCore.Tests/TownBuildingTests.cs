using SimCore.Sim;

namespace SimCore.Tests;

// The town buildings (house_small, villa_walled, shop_row, skeleton_frame), the countryside homes
// (farmhouse_stone, house_unfinished, house_courtyard, villa_tiled, shack_tin; second batch shepherd_hut,
// mountain_house, mudbrick_house), the farm shed and the house
// over a shop: garrisonable like the blocks, footprints from the art rounded up to whole cells, doors on the
// front (south). The other farm buildings (greenhouse, hay_store, chicken_coop, farm_water_tower) hold nobody:
// plain neutral civilian structures.
public class TownBuildingTests
{
    public static TheoryData<string, int, int, int, int> Buildings => new()
    {
        { "house_small", 5, 5, 4, 700 },
        { "villa_walled", 8, 9, 6, 1000 },
        { "shop_row", 7, 6, 6, 1100 },
        { "skeleton_frame", 8, 6, 8, 1300 },
    };

    // The countryside homes (first batch 2026-10-24, second batch 2026-10-25): id, footprint, capacity, HP.
    public static TheoryData<string, int, int, int, int> Homes => new()
    {
        { "farmhouse_stone", 7, 5, 5, 900 },
        { "house_unfinished", 6, 5, 5, 800 },
        { "house_courtyard", 9, 9, 5, 800 },
        { "villa_tiled", 7, 6, 6, 900 },
        { "shack_tin", 4, 4, 2, 350 },
        { "shepherd_hut", 4, 4, 2, 400 },
        { "mountain_house", 6, 6, 5, 900 },
        { "mudbrick_house", 6, 5, 4, 600 },
    };

    // The second batch's heights (tenths of a metre): id, height.
    public static TheoryData<string, int> SecondBatchHeights => new()
    {
        { "shepherd_hut", 35 },
        { "mountain_house", 92 },
        { "mudbrick_house", 43 },
    };

    // The garrisonable farm building and town filler (task 2, 2026-10-24): id, footprint, capacity, HP; the
    // homes' garrison numbers and door.
    public static TheoryData<string, int, int, int, int> FarmAndFiller => new()
    {
        { "farm_shed", 8, 5, 4, 600 },
        { "house_over_shop", 6, 5, 6, 1000 },
    };

    // The farm buildings nobody garrisons: id, footprint, height (tenths of a metre), HP.
    public static TheoryData<string, int, int, int, int> FarmProps => new()
    {
        { "greenhouse", 10, 4, 30, 150 },
        { "hay_store", 6, 4, 50, 300 },
        { "chicken_coop", 3, 3, 20, 100 },
        { "farm_water_tower", 3, 3, 120, 400 },
    };

    [Theory]
    [MemberData(nameof(Buildings))]
    [MemberData(nameof(Homes))]
    [MemberData(nameof(FarmAndFiller))]
    public void FootprintCapacityAndHp(string id, int w, int h, int capacity, int hp)
    {
        var a = TestContent.Mvp.Archetype(id)!;
        Assert.Equal((w, h), (a.FootprintW, a.FootprintH));
        Assert.Equal(capacity, a.Garrison!.Capacity);
        Assert.Equal(hp, a.MaxHp);
        Assert.All(a.Garrison.Doors, d => Assert.Equal(h, d.Y)); // just outside the front
    }

    [Theory]
    [MemberData(nameof(Buildings))]
    [MemberData(nameof(Homes))]
    [MemberData(nameof(FarmAndFiller))]
    public void ASquadGarrisonsItUpToCapacity(string id, int w, int h, int capacity, int hp)
    {
        _ = (w, hp);
        var world = Arena.New(48);
        var b = world.Spawn(1, id, 10, 10);
        Assert.Equal(0, b.Owner);
        var squad = Enumerable.Range(0, capacity + 2).Select(i => world.Spawn(1, "Rifleman", 6 + i, 10 + h + 6)).ToList();
        world.Order(1, CommandKind.Enter, squad, b.Id);
        world.Run(600);
        Assert.Equal(capacity, world.Units.Count(u => u.Garrison == b.Id));
        Assert.Equal(1, b.Owner);
    }

    [Fact]
    public void TheBallisticMissileBringsDownEveryOne()
    {
        var w = Arena.New(64);
        var weapon = w.Match.Players[0].Find(w.Content, "ballistic_truck")!.Weapons[0];
        foreach (var (id, x) in new[] { ("house_small", 2), ("villa_walled", 10), ("shop_row", 22), ("skeleton_frame", 32) })
        {
            var b = w.Spawn(2, id, x, 2);
            w.Hurt(b, weapon.Warhead, weapon.Damage, 100);
            Assert.False(b.Alive, id);
        }
    }

    [Theory]
    [MemberData(nameof(Homes))]
    [MemberData(nameof(FarmAndFiller))]
    public void HomesAreFreeNeutralCivilianBuildingsWithOneFrontDoor(string id, int w, int h, int capacity, int hp)
    {
        _ = (capacity, hp);
        var house = TestContent.Mvp.Archetype("house_small")!;
        var a = TestContent.Mvp.Archetype(id)!;
        Assert.Equal((0, 600, house.Armor), (a.Cost, a.BuildTime, a.Armor));
        Assert.Equal([(w / 2, h)], a.Garrison!.Doors); // the front row centre
        Assert.Equal(house.Garrison!.StageCapacity, a.Garrison.StageCapacity);
        Assert.True(TestContent.Mvp.Factions[0].Archetypes[a.Index]); // the Resistance list: sandbox spawns

        var world = Arena.New(48);
        var b = world.Spawn(1, id, 10, 10);
        Assert.Equal(0, b.Owner);
        Assert.False(world.Grid.Passable(new Cell(10, 10)));
        Assert.False(world.Grid.Passable(new Cell(10 + w - 1, 10 + h - 1)));
        var door = new Cell(10 + w / 2, 10 + h);
        Assert.True(world.Grid.Passable(door));

        // In, then out by the door.
        var s = world.Spawn(1, "Rifleman", 4, 4);
        World.EnterBuilding(s, b);
        world.Step();
        Assert.Equal(b.Id, s.Garrison);
        world.Order(1, CommandKind.Exit, [s], cell: new Cell(10 + w / 2, 40));
        world.Step();
        Assert.Equal(0u, s.Garrison);
        Assert.Equal(door, Cell.Of(s.Pos));
        Assert.Equal(0, b.Owner);
    }

    [Theory]
    [MemberData(nameof(Homes))]
    [MemberData(nameof(FarmAndFiller))]
    public void HomesThrowOutTheExcessByStageAndCollapseToRubble(string id, int w, int h, int capacity, int hp)
    {
        _ = hp;
        var world = Arena.New(48);
        var b = world.Spawn(1, id, 10, 10);
        var squad = Enumerable.Range(0, capacity).Select(i => world.Spawn(1, "Rifleman", 2 + i, 2)).ToList();
        foreach (var s in squad)
        {
            World.EnterBuilding(s, b);
        }
        world.Step();
        Assert.Equal(capacity, b.Occupants);

        b.HP = b.Def.MaxHp * 50 / 100; // stage 2 holds 75%
        world.Step();
        Assert.Equal(2, b.Stage);
        Assert.Equal(capacity * 75 / 100, b.Occupants);

        b.HP = b.Def.MaxHp * 20 / 100; // stage 3 holds half
        world.Step();
        Assert.Equal(3, b.Stage);
        Assert.Equal(capacity / 2, b.Occupants);
        Assert.All(squad.Where(s => s.Garrison == 0), s => Assert.Equal(s.Def.MaxHp - s.Def.MaxHp * 20 / 100, s.HP));

        var inside = squad.Where(s => s.Garrison == b.Id).ToList();
        b.HP = 0;
        world.Step();
        Assert.Null(world.Unit(b.Id));
        Assert.False(world.Grid.Passable(new Cell(10 + w / 2, 10 + h / 2))); // the rubble still blocks
        Assert.All(inside.Where(s => world.Unit(s.Id) is not null), s =>
        {
            Assert.Equal(0u, s.Garrison);
            Assert.Equal(s.Def.MaxHp * 30 / 100, s.HP);
        });
    }

    [Theory]
    [MemberData(nameof(SecondBatchHeights))]
    public void TheSecondBatchHomesHaveTheirHeightsAndAreNeutralByTheirGarrison(string id, int height)
    {
        var a = TestContent.Mvp.Archetype(id)!;
        Assert.Equal(height * 512 / 10, a.HeightRaw);
        Assert.False(a.Neutral); // neutral by its garrison, not the flag
        Assert.True(a.PlacedNeutral);
    }

    [Fact]
    public void TheFarmShedAndTheHouseOverAShopTakeTheHomesHeights()
    {
        Assert.Equal(55 * 512 / 10, TestContent.Mvp.Archetype("farm_shed")!.HeightRaw); // 5.5 m
        Assert.Equal(95 * 512 / 10, TestContent.Mvp.Archetype("house_over_shop")!.HeightRaw); // 9.5 m
        Assert.False(TestContent.Mvp.Archetype("farm_shed")!.Neutral); // neutral by its garrison, not the flag
    }

    [Theory]
    [MemberData(nameof(FarmProps))]
    public void FarmPropsAreFreeNeutralStructuresNobodyGarrisons(string id, int w, int h, int height, int hp)
    {
        var house = TestContent.Mvp.Archetype("house_small")!;
        var a = TestContent.Mvp.Archetype(id)!;
        Assert.Equal((w, h), (a.FootprintW, a.FootprintH));
        Assert.Equal(hp, a.MaxHp);
        Assert.Equal(height * 512 / 10, a.HeightRaw);
        Assert.Equal((0, 600, house.Armor), (a.Cost, a.BuildTime, a.Armor));
        Assert.Null(a.Garrison);
        Assert.True(a.Neutral);
        Assert.True(a.PlacedNeutral);
        Assert.True(TestContent.Mvp.Factions[0].Archetypes[a.Index]); // the Resistance list: sandbox spawns

        // Placed neutral, blocking its whole footprint and nothing outside it.
        var world = Arena.New(48);
        var b = world.Spawn(1, id, 10, 10);
        Assert.Equal(0, b.Owner);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                Assert.False(world.Grid.Passable(new Cell(10 + x, 10 + y)), $"{id} ({x}, {y})");
            }
        }
        Assert.True(world.Grid.Passable(new Cell(10 + w / 2, 10 + h)));
        Assert.True(world.Grid.Passable(new Cell(10 + w, 10)));

        // A garrison order is refused: the soldier never goes in and the structure stays nobody's.
        var s = world.Spawn(1, "Rifleman", 10 + w / 2, 10 + h + 3);
        world.Order(1, CommandKind.Enter, [s], b.Id);
        world.Run(200);
        Assert.Equal(0u, s.Garrison);
        Assert.Equal(0u, s.Inside);
        Assert.Equal(0, b.Owner);
        Assert.Equal(0, b.Occupants);

        // Bullets scratch it (building armour); the ballistic missile brings it down. Its footprint stays blocked (rubble).
        var rifle = world.Match.Players[0].Find(world.Content, "Rifleman")!.Weapons[0];
        world.Hurt(b, rifle.Warhead, rifle.Damage, 100);
        Assert.InRange(b.HP, 1, b.Def.MaxHp - 1);
        var missile = world.Match.Players[0].Find(world.Content, "ballistic_truck")!.Weapons[0];
        world.Hurt(b, missile.Warhead, missile.Damage, 100);
        Assert.False(b.Alive, id);
        world.Step();
        Assert.Null(world.Unit(b.Id));
        Assert.False(world.Grid.Passable(new Cell(10 + w / 2, 10 + h / 2))); // the rubble still blocks
        Assert.False(world.Grid.Passable(new Cell(10, 10)));
    }

    [Fact]
    public void TheSandboxPlacesAFarmPropNeutralWhereItFits()
    {
        var w = Arena.New(48, edit: m => m with { Scenario = SimCore.Scenarios.Scenario.SandboxName });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "greenhouse", Cell = new Cell(5, 5) });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "chicken_coop", Cell = new Cell(14, 8) }); // overlaps
        w.Step();
        var b = Assert.Single(w.Units);
        Assert.Equal("greenhouse", b.Def.Archetype.Id);
        Assert.Equal(0, b.Owner);
        Assert.Equal(new Cell(5, 5), World.FootprintOrigin(b));
        Assert.False(w.Grid.Passable(new Cell(14, 8)));
    }
}
