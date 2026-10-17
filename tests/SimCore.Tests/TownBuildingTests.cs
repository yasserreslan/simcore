using SimCore.Sim;

namespace SimCore.Tests;

// The town buildings (house_small, villa_walled, shop_row, skeleton_frame): garrisonable like the
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

    [Theory]
    [MemberData(nameof(Buildings))]
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
}
