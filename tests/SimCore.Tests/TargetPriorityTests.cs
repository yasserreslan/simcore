using SimCore.FixedPoint;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// What units pick on their own: the attacker, threats before harmless units, and no structures
// that can't fight (the unit AI).
public class TargetPriorityTests
{
    // A rifleman holding his ground with an enemy worker (harmless) nearer than an enemy rifleman
    // (a threat): he picks the rifleman.
    [Fact]
    public void ThreatsBeforeHarmlessUnits()
    {
        var w = Arena.New(40);
        var u = w.Spawn(1, "Rifleman", 10, 10);
        u.Stance = Stance.HoldGround;
        var worker = w.Spawn(2, "worker", 12, 10);
        var rifle = w.Spawn(2, "Rifleman", 10, 14);
        rifle.Stance = Stance.HoldFire;
        w.Run(2);
        Assert.Equal(rifle.Id, u.AutoTarget);
        rifle.HP = 0;
        w.Run(2);
        Assert.Equal(worker.Id, u.AutoTarget); // alone, a harmless unit is still fair game
    }

    // Shooting a harmless unit, he switches to the enemy that hurts him once it is in reach.
    [Fact]
    public void SwitchesFromAHarmlessTargetToItsAttacker()
    {
        var w = Arena.New(40);
        var u = w.Spawn(1, "Rifleman", 10, 10);
        u.Stance = Stance.HoldGround;
        u.HP = 10_000;
        var worker = w.Spawn(2, "worker", 12, 10);
        worker.HP = 10_000;
        w.Run(3);
        Assert.Equal(worker.Id, u.AutoTarget);
        var attacker = w.Spawn(2, "Rifleman", 10, 15); // 5 cells: in reach of both
        attacker.HP = 10_000;
        for (var i = 0; i < 40 && u.LastHitBy != attacker.Id; i++)
        {
            w.Run(1);
        }
        w.Run(2);
        Assert.Equal(attacker.Id, u.AutoTarget);
    }

    // Of two threats, the one that hurt him comes first when he picks a new target.
    [Fact]
    public void TheAttackerFirst()
    {
        var w = Arena.New(40);
        var u = w.Spawn(1, "Rifleman", 10, 10);
        u.Stance = Stance.HoldGround;
        u.HP = 10_000;
        var attacker = w.Spawn(2, "Rifleman", 10, 15);
        attacker.HP = 10_000;
        var near = w.Spawn(2, "Gunner", 13, 10); // nearer, a threat too, but silent
        near.Stance = Stance.HoldFire;
        near.HP = 10_000;
        for (var i = 0; i < 40 && u.LastHitBy != attacker.Id; i++)
        {
            w.Run(1);
        }
        u.AutoTarget = 0; // pick afresh (on its search phase, within VisionTicks ticks)
        for (var i = 0; i < (int)World.VisionTicks && u.AutoTarget == 0; i++)
        {
            w.Run(1);
        }
        Assert.Equal(attacker.Id, u.AutoTarget);
    }

    // Structures that can't fight are left to orders (an enemy-held building is picked: below).
    [Fact]
    public void StructuresOnlyWhenTheyFight()
    {
        var w = Arena.New(64);
        var u = w.Spawn(1, "Rifleman", 30, 30);
        u.Stance = Stance.HoldGround;
        var depot = w.Spawn(2, "supply_depot", 30, 34); // in reach (its footprint edge)
        w.Run(30);
        Assert.Equal(0u, u.AutoTarget);
        Assert.Equal(depot.Def.MaxHp, depot.HP);

        // An ordered attack still works.
        w.Order(1, CommandKind.Attack, [u], target: depot.Id);
        w.Run(60);
        Assert.True(depot.HP < depot.Def.MaxHp);
    }

    [Fact]
    public void AnEnemyHeldBuildingIsATarget()
    {
        var w = Arena.New(64);
        var b = w.Spawn(1, "block_brick_4", 24, 20);
        var occupant = w.Spawn(2, "Rifleman", 30, 16);
        occupant.Stance = Stance.HoldFire;
        w.Order(2, CommandKind.Enter, [occupant], b.Id);
        w.Run(200);
        Assert.Equal(occupant.Garrison, b.Id);
        Assert.Equal(2, b.Owner);
        var u = w.Spawn(1, "Rifleman", 31, 29); // a few cells off its south wall
        u.Stance = Stance.HoldGround;
        w.Run(5);
        Assert.Equal(b.Id, u.AutoTarget);
    }
}
