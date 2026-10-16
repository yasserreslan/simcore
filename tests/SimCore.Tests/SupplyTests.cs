using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

public class SupplyTests
{
    // Shots fired by a rifleman at a sturdy target in 600 ticks, with or
    // without a friendly (or enemy) depot nearby.
    private static (int Shots, int Bonus) Shoot(byte? depotOwner)
    {
        var w = Arena.New(16);
        if (depotOwner is { } owner)
        {
            w.Spawn(owner, "supply_depot", 0, 10); // in supply radius, out of rifle range
        }
        var rifleman = w.Spawn(1, "Rifleman", 5, 5, Facing.East);
        var target = w.Spawn(2, "technical", 9, 5);
        target.HP = 100_000;
        w.Run(600);
        var perShot = rifleman.Def.Weapons[0].Warhead.Damage(12, target.Def.Armor, 100);
        return ((100_000 - target.HP) / perShot, rifleman.ReloadBonus);
    }

    [Fact]
    public void SupplyRadiusSpeedsUpReloads()
    {
        var plain = Shoot(null);
        var supplied = Shoot(1);
        var enemyDepot = Shoot(2);
        Assert.Equal((40, 0), plain); // a shot every 15 ticks
        Assert.Equal((47, 20), supplied); // +20%: every 13 ticks (1500 / 120, rounded up)
        Assert.Equal(plain, enemyDepot); // only friendly supply helps
    }
}
