using SimCore.Sim;

namespace SimCore.Tests;

public class RepairTests
{
    [Fact]
    public void RepairKitFixesNearbyVehiclesOnly()
    {
        var w = Arena.New(32);
        var mechanic = w.Spawn(1, "Mechanic", 10, 10);
        var near = w.Spawn(1, "technical", 11, 11);
        var far = w.Spawn(1, "technical", 20, 20);
        var soldier = w.Spawn(1, "Rifleman", 9, 10);
        near.HP = 100;
        far.HP = 100;
        soldier.HP = 10;

        w.Run(41); // INF-E1a: idle, he takes on a damaged vehicle within 3 cells within a second, then 6 HP every 20 ticks
        Assert.InRange(near.HP, 106, 118);
        Assert.Equal(100, far.HP); // out of reach
        Assert.Equal(10, soldier.HP); // the kit fixes vehicles, not people

        w.Run(1000);
        Assert.Equal(near.Def.MaxHp, near.HP); // never above max
        Assert.Equal(mechanic.Def.MaxHp, mechanic.HP);
    }
}
