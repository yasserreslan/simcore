using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

public class TrapTests
{
    [Fact]
    public void PlantedTrapIsHiddenFromTheEnemy()
    {
        var w = Arena.New(32);
        var sapper = w.Spawn(1, "Sapper", 10, 10);
        var enemy = w.Spawn(2, "Rifleman", 20, 10);
        w.Order(1, CommandKind.UseAbility, [sapper], cell: new Cell(11, 10), ability: "trap_kit");
        w.Step();

        var trap = Assert.Single(w.All("booby_trap"));
        Assert.Equal((1, sapper.Id), (trap.Owner, trap.Source));
        Assert.Equal(new Cell(11, 10).Center(), trap.Pos);
        Assert.Equal([2, 1, 1], sapper.Charges); // the trap kit, the drone station kit and the Kaman station kit
        Assert.True(World.IsVisibleTo(trap, 1));
        Assert.False(World.IsVisibleTo(trap, 2));

        w.Order(2, CommandKind.Attack, [enemy], trap.Id);
        w.Step();
        Assert.Equal(OrderKind.Idle, enemy.Order.Kind); // can't attack what you can't see
    }

    [Fact]
    public void TrapFiresOnceOnAnEnemyGroundUnit()
    {
        var w = Arena.New(32);
        var trap = w.Spawn(1, "booby_trap", 11, 10);
        var friend = w.Spawn(1, "worker", 14, 10);
        w.Order(1, CommandKind.Move, [friend], cell: new Cell(8, 10)); // walks right over it
        w.Run(100);
        Assert.NotNull(w.Unit(trap.Id));

        var enemy = w.Spawn(2, "Rifleman", 16, 10);
        w.Order(2, CommandKind.Move, [enemy], cell: new Cell(4, 10));
        w.Run(200);
        Assert.Null(w.Unit(trap.Id));
        Assert.Null(w.Unit(enemy.Id)); // 90 explosive damage kills a rifleman
    }

    [Fact]
    public void DronesDoNotSetOffTraps()
    {
        var w = Arena.New(32);
        var trap = w.Spawn(1, "booby_trap", 11, 10);
        var pilot = w.Spawn(2, "Drone pilot", 20, 10);
        w.Order(2, CommandKind.UseAbility, [pilot], cell: new Cell(6, 10), ability: "drone_kit");
        w.Step();
        var drone = Assert.Single(w.All("fpv_drone"));
        var passed = false;
        for (var i = 0; i < 100 && w.Unit(drone.Id) is not null; i++)
        {
            w.Step();
            passed |= World.Within(drone.Pos, trap.Pos, Fixed.FromRaw(Fixed.OneRaw / 2));
        }
        Assert.True(passed, "the drone should have flown over the trap");
        Assert.Null(w.Unit(drone.Id));
        Assert.NotNull(w.Unit(trap.Id));
    }

    [Theory]
    [InlineData("Marksman", 14, 12, true)] // snipers reveal traps within 5 cells
    [InlineData("Sapper", 11, 10, false)] // engineers no longer do (2026-10-18: only snipers, scout drones and posts)
    [InlineData("Marksman", 16, 10, false)]
    [InlineData("Rifleman", 13, 10, false)] // assault infantry never do
    public void OnlySnipersAmongSoldiersRevealTraps(string scout, int x, int y, bool reveals)
    {
        var w = Arena.New(32);
        var trap = w.Spawn(1, "booby_trap", 10, 10);
        w.Spawn(2, scout, x, y, Facing.West);
        w.Step();
        Assert.Equal(reveals, World.IsVisibleTo(trap, 2));
        Assert.True(World.IsVisibleTo(trap, 1));
    }

    [Fact]
    public void RevealedTrapsCanBeShotByAnyUnit()
    {
        var w = Arena.New(32);
        var trap = w.Spawn(1, "booby_trap", 10, 10);
        var rifleman = w.Spawn(2, "Rifleman", 14, 10, Facing.West);
        w.Order(2, CommandKind.Attack, [rifleman], trap.Id);
        w.Run(100);
        Assert.Equal(OrderKind.Idle, rifleman.Order.Kind);
        Assert.Equal(trap.Def.MaxHp, trap.HP); // hidden: not even auto-targeted

        w.Spawn(2, "Marksman", 12, 12, Facing.West); // reveals it, for the whole side
        w.Step();
        w.Order(2, CommandKind.Attack, [rifleman], trap.Id);
        w.Run(100);
        Assert.Null(w.Unit(trap.Id));
        Assert.Equal(rifleman.Def.MaxHp, rifleman.HP); // shot from afar, it never went off
    }

    [Fact]
    public void PlantingPastTheLimitRemovesTheOldestTrap()
    {
        var w = Arena.New(32);
        var sapper = w.Spawn(1, "Sapper", 11, 11);
        var traps = new List<uint>();
        foreach (var c in new[] { new Cell(12, 10), new Cell(12, 11), new Cell(12, 12), new Cell(10, 11) })
        {
            if (sapper.Charges[0] == 0)
            {
                sapper.MutableCharges[0] = 3; // as if rearmed
            }
            w.Order(1, CommandKind.UseAbility, [sapper], cell: c, ability: "trap_kit");
            w.Run(45); // past the cooldown
            traps.Add(w.All("booby_trap").Max(t => t.Id));
        }
        Assert.Equal(4, traps.Distinct().Count());
        Assert.Equal(traps.Skip(1), w.All("booby_trap").Select(t => t.Id)); // the first one is gone
    }
}
