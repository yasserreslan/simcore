using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

public class LauncherTests
{
    // Records every projectile ID seen while running ticks.
    private static HashSet<uint> Watch(World w, int ticks)
    {
        var seen = new HashSet<uint>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                seen.Add(p.Id);
            }
        }
        return seen;
    }

    private static (World W, Unit Launcher, Unit Target) Range(string launcher = "light_rocket_truck")
    {
        var w = Arena.New(32);
        var (l, _) = w.SpawnCrewed(1, launcher, 5, 16, Facing.East);
        var target = w.Spawn(2, "technical", 9, 16);
        target.HP = 100_000;
        return (w, l, target);
    }

    [Fact]
    public void SalvoFiresItsRocketsAndSpendsOneSalvo()
    {
        var (w, l, target) = Range();
        Assert.Equal([4], l.Charges);
        w.Order(1, CommandKind.Attack, [l], target.Id);
        var rockets = Watch(w, 40);
        Assert.Equal(4, rockets.Count);
        Assert.Equal([3], l.Charges);
        Assert.True(target.HP < 100_000);
    }

    [Fact]
    public void EmptyLauncherStopsFiring()
    {
        var (w, l, target) = Range();
        w.Order(1, CommandKind.Attack, [l], target.Id);
        var rockets = Watch(w, 1000);
        Assert.Equal(16, rockets.Count); // 4 salvos of 4
        Assert.Equal([0], l.Charges);
        Assert.Equal(OrderKind.Idle, l.Order.Kind); // nothing left to attack with
        Assert.Empty(w.Projectiles);
    }

    [Fact]
    public void DepotRearmsLaunchersFromItsStock()
    {
        var w = Arena.New(32);
        var depot = w.Spawn(1, "supply_depot", 3, 3);
        var (l, _) = w.SpawnCrewed(1, "medium_rocket_launcher", 7, 4);
        l.MutableCharges[0] = 0;
        w.Run(81); // pulses on ticks 0, 40 and 80
        Assert.Equal([3], l.Charges); // one salvo per pulse
        Assert.Equal(1000 - 3, depot.Stock); // one supply per salvo

        l.MutableCharges[0] = 0;
        depot.Stock = 2;
        w.Run(200);
        Assert.Equal([2], l.Charges); // the depot ran dry
        Assert.Equal(0, depot.Stock);
    }

    // Launcher rockets fly to where the target stood when the salvo began,
    // so a target that drives off in time is missed.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RocketsMissATargetThatMoved(bool dodge)
    {
        var w = Arena.New(48);
        var (l, _) = w.SpawnCrewed(1, "medium_rocket_launcher", 5, 30, Facing.East);
        var (bike, _) = w.SpawnCrewed(2, "dirt_bike", 17, 30); // at the edge of its 12-cell range
        w.Spawn(1, "worker", 17, 34); // the spotter: the bike is beyond the launcher's own vision
        w.Order(1, CommandKind.Attack, [l], bike.Id);
        w.Step();
        Assert.True(l.SalvoLeft > 0, "the salvo should have begun");
        if (dodge)
        {
            w.Order(2, CommandKind.Move, [bike], cell: new Cell(17, 5));
        }
        var rockets = Watch(w, 60);
        Assert.Equal(6, rockets.Count);
        if (dodge)
        {
            Assert.Equal(bike.Def.MaxHp, bike.HP);
        }
        else
        {
            Assert.True(bike.HP < bike.Def.MaxHp);
        }
    }

    // Until fog of war ships, the player's attack order is enough: the medium launcher
    // outranges its own vision and fires at an enemy no friendly unit spots. When fog
    // lands, IsVisibleTo becomes "spotted by your side" and the spotter rule returns.
    [Fact]
    public void OrderedLaunchersFireWithoutASpotter()
    {
        var w = Arena.New(48);
        var (l, _) = w.SpawnCrewed(1, "medium_rocket_launcher", 5, 30, Facing.East);
        var target = w.Spawn(2, "technical", 15, 30);
        target.HP = 100_000;
        Assert.True(World.Within(l.Pos, target.Pos, l.Def.Weapons[0].Range));
        Assert.False(World.Within(l.Pos, target.Pos, l.Def.Vision));

        w.Order(1, CommandKind.Attack, [l], target.Id);
        Assert.NotEmpty(Watch(w, 100));
        Assert.Equal([3], l.Charges);
        Assert.Equal(0u, target.SpottedBy & (1u << 1)); // nobody on side 1 sees it
        Assert.True(target.HP < 100_000);
    }

    // Every long-range launcher, ordered to attack an enemy far beyond its own
    // vision that no friendly unit spots, still fires and hits.
    [Theory]
    [InlineData("heavy_rocket_launcher", 45)]
    [InlineData("missile_truck", 70)]
    [InlineData("rocket_jeep", 35)]
    public void OrderedLaunchersFireAtAnUnspottedEnemy(string launcher, int distance)
    {
        var w = Arena.New(200);
        var (l, _) = w.SpawnCrewed(1, launcher, 10, 100, Facing.East);
        var enemy = w.Spawn(2, "Rifleman", 10 + distance, 100);
        enemy.HP = 1_000_000;
        var charges = l.Charges[0];

        w.Order(1, CommandKind.Attack, [l], enemy.Id);
        w.Run(600);
        Assert.True(l.Charges[0] < charges, $"{launcher} never fired");
        Assert.True(enemy.HP < 1_000_000, $"{launcher} never hit");
        Assert.Equal(0u, enemy.SpottedBy & (1u << 1));
    }
}
