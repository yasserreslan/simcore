using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// DamageTableTests pins the content's damage table to the design doc's
// "Combat rules" table, and checks that weapons only target what their layer
// and warhead allow, picking the right weapon for each target.
public class DamageTableTests
{
    // Warhead, then infantry, light vehicle, building, drone; null is "—".
    public static TheoryData<string, int?, int?, int?, int?> DesignTable => new()
    {
        { "bullet", 100, 40, 10, 15 },
        { "sniper_round", 150, 20, 5, 15 },
        { "rocket", 40, 120, 80, null },
        { "explosive", 100, 100, 60, null },
        { "aa_missile", null, null, null, 100 },
    };

    [Theory]
    [MemberData(nameof(DesignTable))]
    public void MatchesDesign(string warhead, int? infantry, int? light, int? building, int? drone)
    {
        var c = TestContent.Mvp;
        var w = c.Warhead(warhead)!;
        foreach (var (armor, want) in new[] { ("infantry", infantry), ("light_vehicle", light), ("building", building), ("drone", drone) })
        {
            var i = c.ArmorClass(armor);
            Assert.Equal(want ?? Warhead.CannotTarget, w.Versus[i]);
            Assert.Equal(want is not null, w.CanHit(i));
        }
    }

    [Fact]
    public void DamageLookups()
    {
        var c = TestContent.Mvp;
        int Armor(string id) => c.ArmorClass(id);
        Assert.Equal(12, c.Warhead("bullet")!.Damage(12, Armor("infantry"), 100));
        Assert.Equal(1, c.Warhead("bullet")!.Damage(12, Armor("drone"), 100)); // 15% of 12, truncated
        Assert.Equal(1, c.Warhead("bullet")!.Damage(6, Armor("building"), 100)); // never below 1
        Assert.Equal(36, c.Warhead("rocket")!.Damage(30, Armor("light_vehicle"), 100));
        Assert.Equal(18, c.Warhead("rocket")!.Damage(30, Armor("light_vehicle"), 50)); // the edge of a splash
        Assert.Equal(0, c.Warhead("rocket")!.Damage(30, Armor("drone"), 100)); // cannot target
        Assert.Equal(0, c.Warhead("aa_missile")!.Damage(60, Armor("infantry"), 100));
        Assert.Equal(60, c.Warhead("aa_missile")!.Damage(60, Armor("drone"), 100));
        Assert.Equal(60, c.Warhead("sniper_round")!.Damage(40, Armor("infantry"), 100));
    }

    [Fact]
    public void WeaponsTargetOnlyWhatTheyCan()
    {
        var w = Arena.New(16);
        var aaSoldier = w.Spawn(1, "Drone hunter", 2, 2);
        var atSoldier = w.Spawn(1, "Tank hunter", 2, 4);
        var drone = w.Spawn(2, "fpv_drone", 10, 2);
        var rifleman = w.Spawn(2, "Rifleman", 10, 4);

        // The AA soldier keeps to its launcher by default (no weapon for infantry); allowed its
        // sidearm, it fights infantry with the short rifle, never its missile.
        Assert.Equal(-1, World.PickWeapon(aaSoldier, rifleman, false));
        aaSoldier.WeaponMode = WeaponMode.Auto;
        Assert.Equal(1, World.PickWeapon(aaSoldier, rifleman, false));
        Assert.Equal(0, World.PickWeapon(aaSoldier, drone, false));
        // Nothing the AT soldier carries can hit a drone, so it won't take the order.
        Assert.Equal(-1, World.PickWeapon(atSoldier, drone, false));
        w.Order(1, CommandKind.Attack, [atSoldier], drone.Id);
        w.Step();
        Assert.Equal(OrderKind.Idle, atSoldier.Order.Kind);
    }

    [Fact]
    public void SoldierUsesTheRightWeaponForTheTarget()
    {
        var w = Arena.New(32);
        var sniper = w.Spawn(1, "Skirmisher", 2, 2); // sniper rifle + SMG
        var far = w.Spawn(2, "Rifleman", 12, 2);
        var near = w.Spawn(2, "Rifleman", 4, 2);
        var tankHunter = w.Spawn(1, "Tank hunter", 2, 10); // AT missile + booby trap
        var bike = w.Spawn(2, "dirt_bike", 6, 10);

        Assert.Equal(0, World.PickWeapon(sniper, far, true)); // only the rifle reaches
        Assert.Equal(1, World.PickWeapon(sniper, near, true)); // up close the SMG does more per tick
        Assert.Equal(0, World.PickWeapon(tankHunter, bike, true));
        Assert.True(World.CanHit(sniper.Def.Weapons[0], w.Spawn(2, "fpv_drone", 3, 3))); // bullets reach the air layer
        Assert.Equal(Fixed.FromInt(12), sniper.Def.Weapons[0].Range);
    }
}
