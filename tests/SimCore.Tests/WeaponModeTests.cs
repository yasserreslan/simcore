using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

public class WeaponModeTests
{
    [Fact]
    public void ContentSetsTheStartingMode()
    {
        var w = Arena.New(32);
        Assert.Equal(WeaponMode.PrimaryOnly, w.Spawn(1, "Tank hunter", 5, 5).WeaponMode);
        Assert.Equal(WeaponMode.PrimaryOnly, w.Spawn(1, "Drone hunter", 5, 7).WeaponMode);
        Assert.Equal(WeaponMode.Auto, w.Spawn(1, "Rifleman", 5, 9).WeaponMode);
    }

    [Fact]
    public void PrimaryOnlyNeverUsesTheSidearm()
    {
        // A drone hunter (AA missile + short rifle) next to an enemy soldier: on its launcher only,
        // nothing it carries can hit him; switched to auto, the rifle opens up.
        var w = Arena.New(32);
        var aa = w.Spawn(1, "Drone hunter", 5, 16, Facing.East);
        var enemy = w.Spawn(2, "Rifleman", 8, 16, Facing.West);
        enemy.HP = 100_000;
        w.Order(2, CommandKind.Stop, [enemy]);
        w.Run(60);
        Assert.Equal(0u, aa.AutoTarget);
        Assert.Equal(0, aa.Reload);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetWeaponMode, Units = [aa.Id], Mode = WeaponMode.Auto });
        w.Run(60);
        Assert.Equal(WeaponMode.Auto, aa.WeaponMode);
        Assert.Equal(enemy.Id, aa.AutoTarget);
        Assert.True(enemy.HP < 100_000);
    }

    [Fact]
    public void PrimaryOnlyAttackGroundUsesOnlyThePrimary()
    {
        var w = Arena.New(32);
        var aa = w.Spawn(1, "Drone hunter", 5, 16, Facing.East);
        w.Order(1, CommandKind.AttackGround, [aa], cell: new Cell(8, 16));
        w.Run(3);
        Assert.Equal(OrderKind.Idle, aa.Order.Kind); // AA missiles can't hit the ground and the rifle is off
    }

    [Fact]
    public void OnlyTheOwnerCanChangeIt()
    {
        var w = Arena.New(32);
        var hunter = w.Spawn(1, "Tank hunter", 5, 16);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetWeaponMode, Units = [hunter.Id], Mode = WeaponMode.Auto });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetWeaponMode, Units = [hunter.Id], Mode = (WeaponMode)7 });
        w.Run(2);
        Assert.Equal(WeaponMode.PrimaryOnly, hunter.WeaponMode);
    }

    [Fact]
    public void ModeIsInTheHash()
    {
        var a = Arena.New(32);
        var b = Arena.New(32);
        var ua = a.Spawn(1, "Tank hunter", 5, 16);
        b.Spawn(1, "Tank hunter", 5, 16);
        a.Issue(new Command { Tick = a.Tick, Player = 1, Kind = CommandKind.SetWeaponMode, Units = [ua.Id], Mode = WeaponMode.Auto });
        a.Run(1);
        b.Run(1);
        Assert.NotEqual(a.Hash(), b.Hash());
    }

    [Fact]
    public void UnknownModeInContentIsAnError()
    {
        var sources = TestContent.MvpSources()
            .Select(s => s.Name == "archetypes/infantry.json" ? s with { Json = s.Json.Replace("\"primaryOnly\"", "\"sometimes\"") } : s);
        var e = Assert.ThrowsAny<Exception>(() => Content.ContentLoader.Load(sources));
        Assert.Contains("weaponMode", e.Message);
    }
}
