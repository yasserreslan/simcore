using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

public class CrewTests
{
    [Fact]
    public void EmptyVehicleCannotMoveOrFire()
    {
        var w = Arena.New(16);
        var tech = w.Spawn(1, "technical", 5, 5, Facing.East);
        var enemy = w.Spawn(2, "Rifleman", 9, 5);
        Assert.False(tech.CanAct);

        w.Order(1, CommandKind.Move, [tech], cell: new Cell(12, 12));
        w.Run(100);
        Assert.Equal(OrderKind.Idle, tech.Order.Kind);
        Assert.Equal(new Cell(5, 5).Center(), tech.Pos);
        Assert.Equal(enemy.Def.MaxHp, enemy.HP); // its heavy MG never fired
        Assert.True(tech.HP < tech.Def.MaxHp, "the enemy may shoot an empty vehicle");
    }

    [Fact]
    public void SoldierWalksToVehicleAndDrivesIt()
    {
        var w = Arena.New(32);
        var soldier = w.Spawn(1, "Rifleman", 2, 10);
        var tech = w.Spawn(1, "technical", 10, 10);
        w.Order(1, CommandKind.Enter, [soldier], tech.Id);
        w.Run(300);

        Assert.Equal(tech.Id, soldier.Inside);
        Assert.Equal(soldier.Id, tech.Driver);
        Assert.False(World.IsVisibleTo(soldier, 1));
        Assert.True(tech.CanAct);
        Assert.False(soldier.CanAct);

        w.Order(1, CommandKind.Move, [tech], cell: new Cell(25, 10));
        w.Run(300);
        Assert.Equal(new Cell(25, 10), Cell.Of(tech.Pos));
    }

    [Fact]
    public void OnlyOwnSoldiersDriveAndOnlyOneAtATime()
    {
        var w = Arena.New(32);
        var tech = w.Spawn(1, "technical", 10, 10);
        var enemyTech = w.Spawn(2, "technical", 14, 10);
        var enemy = w.Spawn(2, "Rifleman", 11, 11);
        var worker = w.Spawn(1, "worker", 9, 11);
        var a = w.Spawn(1, "Rifleman", 9, 9);
        var b = w.Spawn(1, "Rifleman", 9, 8);

        w.Order(2, CommandKind.Enter, [enemy], tech.Id); // no capturing
        w.Order(1, CommandKind.Enter, [a], enemyTech.Id);
        w.Order(1, CommandKind.Enter, [worker], tech.Id); // workers are not soldiers
        w.Step();
        Assert.Equal(OrderKind.Idle, enemy.Order.Kind);
        Assert.Equal(OrderKind.Idle, a.Order.Kind);
        Assert.Equal(OrderKind.Idle, worker.Order.Kind);

        w.Order(1, CommandKind.Enter, [a, b], tech.Id);
        w.Run(100);
        Assert.Equal(a.Id, tech.Driver); // a was closer
        Assert.Equal(0u, b.Inside);
        Assert.Equal(OrderKind.Idle, b.Order.Kind);
    }

    [Fact]
    public void ExitPutsDriverNextToVehicle()
    {
        var w = Arena.New(32);
        var (tech, driver) = w.SpawnCrewed(1, "technical", 10, 10);
        w.Order(1, CommandKind.Exit, [tech]);
        w.Step();

        Assert.Equal(0u, driver.Inside);
        Assert.Equal(0u, tech.Driver);
        Assert.True(World.Within(driver.Pos, tech.Pos, Fixed.FromInt(2)));
        Assert.True(driver.CanAct);
        Assert.False(tech.CanAct);

        // Giving the driver's own ID works too.
        w.Order(1, CommandKind.Enter, [driver], tech.Id);
        w.Run(40);
        Assert.Equal(tech.Id, driver.Inside);
        w.Order(1, CommandKind.Exit, [driver]);
        w.Step();
        Assert.Equal(0u, driver.Inside);
    }

    // The driver of a destroyed vehicle survives by a roll on the world's
    // generator: the same seed always gives the same outcome, and over many
    // seeds both outcomes happen.
    [Fact]
    public void DriverSurvivalIsDrawnFromTheSeed()
    {
        (bool Survived, ulong Hash) Destroy(ulong seed)
        {
            var w = Arena.New(16, seed);
            var (tech, driver) = w.SpawnCrewed(1, "technical", 5, 5);
            w.Step();
            tech.HP = 0;
            w.Step();
            var alive = w.Unit(driver.Id);
            if (alive is not null)
            {
                Assert.Equal(0u, alive.Inside);
                Assert.Equal(new Cell(5, 5).Center(), alive.Pos);
                Assert.Equal(alive.Def.MaxHp - alive.Def.MaxHp * 40 / 100, alive.HP);
            }
            Assert.Null(w.Unit(tech.Id));
            return (alive is not null, w.Hash());
        }

        var outcomes = new List<bool>();
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var first = Destroy(seed);
            Assert.Equal(first, Destroy(seed));
            outcomes.Add(first.Survived);
        }
        Assert.Contains(true, outcomes);
        Assert.Contains(false, outcomes);
    }
}
