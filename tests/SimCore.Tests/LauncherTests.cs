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
    public void AmmoTruckRearmsFromItsStock()
    {
        var w = Arena.New(32);
        var (l, _) = w.SpawnCrewed(1, "light_rocket_truck", 5, 16, Facing.East);
        l.MutableCharges[0] = 0;
        var truck = w.Spawn(1, "ammo_truck", 5, 19); // no driver needed to hand out ammo
        w.Run(161); // supply pulses on ticks 0, 40, 80, 120 and 160
        Assert.Equal([4], l.Charges);
        Assert.Equal(16, truck.Stock);

        l.MutableCharges[0] = 0;
        truck.Stock = 2;
        w.Run(200);
        Assert.Equal([2], l.Charges); // the truck ran dry
        Assert.Equal(0, truck.Stock);
    }

    [Fact]
    public void DepotRearmsLaunchersAndRestocksTrucks()
    {
        var w = Arena.New(32);
        w.Spawn(1, "supply_depot", 3, 3);
        var (l, _) = w.SpawnCrewed(1, "medium_rocket_launcher", 7, 4);
        var truck = w.Spawn(1, "ammo_truck", 2, 10); // in the depot's radius, out of its own reach of the launcher
        l.MutableCharges[0] = 0;
        truck.Stock = 0;
        w.Run(81); // pulses on ticks 0, 40 and 80
        Assert.Equal([3], l.Charges); // one salvo per pulse; the depot's stock is unlimited
        Assert.Equal(12, truck.Stock); // 4 per pulse
        w.Run(400);
        Assert.Equal(20, truck.Stock); // never above its capacity
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

    // Launchers fire only at enemies some friendly unit sees: the medium
    // launcher outranges its own vision and needs a spotter.
    [Fact]
    public void LaunchersNeedASpotter()
    {
        var w = Arena.New(48);
        var (l, _) = w.SpawnCrewed(1, "medium_rocket_launcher", 5, 30, Facing.East);
        var target = w.Spawn(2, "technical", 15, 30);
        Assert.True(World.Within(l.Pos, target.Pos, l.Def.Weapons[0].Range));
        Assert.False(World.Within(l.Pos, target.Pos, l.Def.Vision));

        w.Order(1, CommandKind.Attack, [l], target.Id);
        Assert.Empty(Watch(w, 200));
        Assert.Equal([4], l.Charges);
        Assert.Equal(OrderKind.Attack, l.Order.Kind); // still ordered to, but blind

        w.Spawn(1, "worker", 15, 26);
        Assert.NotEmpty(Watch(w, 20));
        Assert.Equal([3], l.Charges);
    }
}
