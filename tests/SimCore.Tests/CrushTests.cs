using SimCore.FixedPoint;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// Crushing: vehicles run over enemy infantry they drive into (the unit AI).
public class CrushTests
{
    // A technical driven at an enemy rifleman standing in its way runs him over.
    [Fact]
    public void ATruckDrivingAtEnemyInfantryCrushesIt()
    {
        var w = Arena.New(64);
        var (tech, _) = w.SpawnCrewed(1, "technical", 10, 30, Facing.East);
        tech.Stance = Stance.HoldFire; // no shooting: only the wheels
        var foe = w.Spawn(2, "Rifleman", 24, 30);
        foe.Stance = Stance.HoldFire;
        w.Order(1, CommandKind.Move, [tech], cell: new Cell(40, 30));
        var crushed = false;
        for (var i = 0; i < 400 && foe.Alive; i++)
        {
            w.Run(1);
            crushed |= w.Crushes.Any(c => c.Victim == foe.Id && c.By == tech.Id);
        }
        Assert.True(crushed);
        Assert.False(foe.Alive);
    }

    // Friends are safe, and so is anyone a vehicle backs into or stands next to.
    [Fact]
    public void FriendsAreSafe()
    {
        var w = Arena.New(64);
        var (tech, _) = w.SpawnCrewed(1, "technical", 10, 30, Facing.East);
        var friend = w.Spawn(1, "Rifleman", 24, 30);
        w.Order(1, CommandKind.Move, [tech], cell: new Cell(40, 30));
        w.Run(400);
        Assert.True(friend.Alive);
    }

    [Fact]
    public void StandingStillCrushesNobody()
    {
        var w = Arena.New(64);
        var (tech, _) = w.SpawnCrewed(1, "technical", 20, 30, Facing.East);
        tech.Stance = Stance.HoldFire;
        var foe = w.Spawn(2, "Rifleman", 21, 30); // right in front of it
        foe.Stance = Stance.HoldFire;
        w.Run(100);
        Assert.True(foe.Alive);
        Assert.Empty(w.Crushes);
    }

    // The dirt bike never crushes.
    [Fact]
    public void TheDirtBikeNeverCrushes()
    {
        Assert.Equal(0, TestContent.Mvp.Archetype("dirt_bike")!.Crusher);
        Assert.Equal(2, TestContent.Mvp.Archetype("technical")!.Crusher);
        Assert.Equal(2, TestContent.Mvp.Archetype("infantry_carrier")!.Crusher);
        Assert.Equal(1, TestContent.Mvp.Archetype("assault")!.Crushable);
        Assert.Equal(2, TestContent.Mvp.Archetype("technical")!.Crushable); // only a tank (crusher 3) runs it over
        Assert.Equal(0, TestContent.Mvp.Archetype("humvee")!.Crushable);
        var w = Arena.New(64);
        var (bike, _) = w.SpawnCrewed(1, "dirt_bike", 10, 30, Facing.East);
        bike.Stance = Stance.HoldFire;
        var foe = w.Spawn(2, "Rifleman", 24, 30);
        foe.Stance = Stance.HoldFire;
        w.Order(1, CommandKind.Move, [bike], cell: new Cell(40, 30));
        w.Run(300);
        Assert.True(foe.Alive);
    }

    // Soldiers in a vehicle or a building can't be run over; vehicles can't be either.
    [Fact]
    public void PassengersAndVehiclesAreSafe()
    {
        var w = Arena.New(64);
        var (tech, _) = w.SpawnCrewed(1, "technical", 10, 30, Facing.East);
        tech.Stance = Stance.HoldFire;
        var (foeTruck, foeDriver) = w.SpawnCrewed(2, "cargo_truck", 24, 30);
        w.Order(1, CommandKind.Move, [tech], cell: new Cell(40, 30));
        w.Run(300);
        Assert.True(foeTruck.Alive);
        Assert.True(foeDriver.Alive);
        Assert.Empty(w.Crushes);
    }
}
