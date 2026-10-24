using SimCore.Sim;

namespace SimCore.Tests;

// The town buildings (house_small, villa_walled, shop_row, skeleton_frame) and the countryside homes
// (farmhouse_stone, house_unfinished, house_courtyard, villa_tiled, shack_tin): garrisonable like the
// blocks, footprints from the art rounded up to whole cells, doors on the front (south).
public class TownBuildingTests
{
    public static TheoryData<string, int, int, int, int> Buildings => new()
    {
        { "house_small", 5, 5, 4, 700 },
        { "villa_walled", 8, 9, 6, 1000 },
        { "shop_row", 7, 6, 6, 1100 },
        { "skeleton_frame", 8, 6, 8, 1300 },
    };

    // The countryside homes (first batch, 2026-10-24): id, footprint, capacity, HP.
    public static TheoryData<string, int, int, int, int> Homes => new()
    {
        { "farmhouse_stone", 7, 5, 5, 900 },
        { "house_unfinished", 6, 5, 5, 800 },
        { "house_courtyard", 9, 9, 5, 800 },
        { "villa_tiled", 7, 6, 6, 900 },
        { "shack_tin", 4, 4, 2, 350 },
    };

    [Theory]
    [MemberData(nameof(Buildings))]
    [MemberData(nameof(Homes))]
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
}
