using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

public class AimTests
{
    [Fact]
    public void LeadsAStraightMovingTechnical()
    {
        var w = Arena.New(48);
        var hunter = w.Spawn(1, "Tank hunter", 20, 30, Facing.North);
        var (truck, _) = w.SpawnCrewed(2, "technical", 13, 24, Facing.East);
        truck.HP = 100_000;
        w.Order(2, CommandKind.Move, [truck], cell: new Cell(40, 24)); // drives across, 6 cells in front of the hunter
        w.Run(3);
        w.Order(1, CommandKind.Attack, [hunter], truck.Id);
        var hits = 0;
        var misses = 0;
        for (var i = 0; i < 160; i++)
        {
            w.Step();
            foreach (var d in w.Detonations)
            {
                if (d.Hit == truck.Id)
                {
                    hits++;
                }
                else
                {
                    misses++;
                }
            }
        }
        Assert.True(hits >= 1, $"{hits} hits, {misses} misses");
        Assert.True(truck.HP < 100_000);
    }

    [Fact]
    public void RocketsOnlyEngageInfantryUpClose()
    {
        var w = Arena.New(48);
        var hunter = w.Spawn(1, "Tank hunter", 10, 30, Facing.East);
        var far = w.Spawn(2, "worker", 16, 30);  // 6 cells: in AT range, beyond its 4-cell auto range vs infantry
        w.Run(40);
        Assert.Equal(0u, hunter.AutoTarget);
        var near = w.Spawn(2, "worker", 13, 30); // 3 cells
        w.Run(5);
        Assert.Equal(near.Id, hunter.AutoTarget);
        _ = far;
    }

    [Fact]
    public void VehiclesComeFirst()
    {
        var w = Arena.New(48);
        var hunter = w.Spawn(1, "Tank hunter", 10, 30, Facing.East);
        w.Spawn(2, "worker", 12, 30);                 // close infantry
        var truck = w.Spawn(2, "technical", 17, 30);  // a vehicle further away
        w.Run(5);
        Assert.Equal(truck.Id, hunter.AutoTarget);
    }

    [Fact]
    public void ExplicitOrdersOnFarInfantryAreObeyed()
    {
        var w = Arena.New(48);
        var hunter = w.Spawn(1, "Tank hunter", 10, 30, Facing.East);
        var far = w.Spawn(2, "worker", 17, 30);
        w.Order(1, CommandKind.Attack, [hunter], far.Id);
        w.Run(30);
        Assert.Equal(OrderKind.Attack, hunter.Order.Kind);
        Assert.True(hunter.Reload > 0 || w.Projectiles.Count > 0 || far.HP < far.Def.MaxHp);
    }

    [Fact]
    public void GivesUpAfterThreeMissesInARow()
    {
        var w = Arena.New(48);
        var hunter = w.Spawn(1, "Tank hunter", 10, 30, Facing.East);
        var truck = w.Spawn(2, "technical", 14, 30);
        hunter.MissTarget = truck.Id;
        hunter.MissStreak = 3;
        w.Run(5);
        Assert.Equal(0u, hunter.AutoTarget);
        hunter.MissStreak = 2;
        w.Run(2);
        Assert.Equal(truck.Id, hunter.AutoTarget);
    }
}
