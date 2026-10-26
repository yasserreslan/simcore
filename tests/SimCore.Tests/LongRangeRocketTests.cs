using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The long-range rocket truck (longrange_rocket_truck; docs/design.md, "Long-range rocket truck"): 10 unguided rockets in a
// ripple, one every 1.5 s, from 50 to 130 cells (the second-longest ground missile), deploy 6 s / stow 5 s, a depot reload
// only while stowed (20 s and 80 supply a rocket), interceptible as a rocket, fired only on orders.
public class LongRangeRocketTests
{
    private const int Deploy = 120, Stow = 100, Interval = 30, Reload = 400, Cost = 80, Y = 31;

    private static (World W, Unit Truck, Unit Target) Range(int distance, bool depot = false, bool fog = false)
    {
        var w = Arena.New(160, edit: fog ? s => s with { Fog = true } : null);
        if (depot)
        {
            w.Spawn(1, "supply_depot", 3, 30);
        }
        var (t, _) = w.SpawnCrewed(1, "longrange_rocket_truck", 5, Y, Facing.East);
        var target = w.Spawn(2, "technical", 5 + distance, Y);
        target.HP = 1_000_000;
        w.Spawn(1, "worker", 5 + distance, Y - 4); // the spotter
        return (w, t, target);
    }

    private static List<(uint Id, uint Tick)> Launches(World w, int ticks)
    {
        var seen = new List<(uint Id, uint Tick)>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (!seen.Exists(s => s.Id == p.Id))
                {
                    seen.Add((p.Id, w.Tick));
                }
            }
        }
        return seen;
    }

    private static void Ground(World w, Unit t, Cell at) =>
        w.Order(1, CommandKind.AttackGround, [t], cell: at);

    [Fact]
    public void TheContentNumbers()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype("longrange_rocket_truck")!;
        Assert.Equal("Long-range rocket truck", a.Name);
        Assert.Equal(1600, a.Cost);
        Assert.Equal(1000, a.BuildTime); // 50 s
        Assert.Equal(280, a.MaxHp);
        Assert.Equal(c.Archetype("missile_truck")!.Armor, a.Armor);
        Assert.Equal(c.Archetype("heavy_rocket_truck")!.Speed, a.Speed);
        Assert.Equal(Deploy, a.DeployTicks);
        Assert.Equal(Stow, a.StowTicks);
        Assert.True(c.Factions.Single(f => f.Id == "resistance").Archetypes[a.Index]);
        Assert.False(c.Factions.Single(f => f.Id == "sentinel").Archetypes[a.Index]);

        var lr = c.Component("longrange_rockets")!.Effects.OfType<WeaponEffect>().Single();
        Assert.Equal(300, lr.Damage);
        Assert.Equal(Fixed.FromInt(130), lr.Range);
        Assert.Equal(Fixed.FromInt(50), lr.MinRange);
        Assert.Equal(Fixed.FromInt(5), lr.Splash);
        Assert.Equal(InterceptClass.Rocket, lr.Intercept);
        Assert.Equal((10, Interval), (lr.Ammo, lr.SalvoInterval));
        Assert.Equal(Fixed.FromInt(4), lr.ScatterNear);
        Assert.Equal(Fixed.FromInt(6), lr.Scatter);
        Assert.Equal(new DepotReload(Reload, 20, Cost, true), lr.DepotReload);
    }

    [Fact]
    public void SecondLongestGroundMissile()
    {
        var c = TestContent.Mvp;
        Fixed R(string id) => c.Component(id)!.Effects.OfType<WeaponEffect>().Single().Range;
        Assert.True(R("longrange_rockets") < R("ballistic_missile"));
        Assert.True(R("longrange_rockets") > R("twin_missiles"));
        Assert.True(R("longrange_rockets") > R("heavy_artillery_rockets"));
    }

    [Fact]
    public void TenRocketsAndHalfASecondBetweenThemAfterASixSecondDeploy()
    {
        var (w, t, target) = Range(90);
        Ground(w, t, new Cell(95, Y));
        var shots = Launches(w, Deploy + 10 * Interval + 200);
        Assert.Equal(10, shots.Count);
        Assert.True(shots[0].Tick > Deploy, $"first rocket on tick {shots[0].Tick}, before deploying");
        Assert.All(shots.Zip(shots.Skip(1)), p => Assert.Equal(Interval, (int)(p.Second.Tick - p.First.Tick)));
        Assert.Equal(0, t.Charges[0]);
        Assert.NotNull(target);
    }

    [Fact]
    public void NothingCloserThanFiftyCells()
    {
        var (w, t, target) = Range(47);
        w.Order(1, CommandKind.Deploy, [t]);
        w.Run(Deploy + 2);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Assert.Empty(Launches(w, 300));
        Assert.Equal(10, t.Charges[0]);
    }

    [Fact]
    public void NothingBeyondOneHundredThirtyCells()
    {
        var (w, t, target) = Range(133);
        w.Order(1, CommandKind.Deploy, [t]);
        w.Run(Deploy + 2);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Assert.Empty(Launches(w, 100));
        Assert.Equal(10, t.Charges[0]);
    }

    [Fact]
    public void FiresInsideTheBandAtBothEnds()
    {
        foreach (var d in new[] { 52, 128 })
        {
            var (w, t, target) = Range(d);
            w.Order(1, CommandKind.Attack, [t], target.Id);
            Assert.NotEmpty(Launches(w, Deploy + 2 * Interval + 60));
        }
    }

    [Fact]
    public void FliesAtRocketSpeedOnTheUsualLauncherArc()
    {
        var (w, t, target) = Range(100);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Projectile? p = null;
        for (var i = 0; i < Deploy + 100 && p is null; i++)
        {
            w.Step();
            p = w.Projectiles.FirstOrDefault();
        }
        Assert.NotNull(p);
        Assert.Equal(InterceptClass.Rocket, p!.Interceptible);
        var heavy = TestContent.Mvp.Component("heavy_artillery_rockets")!.Effects.OfType<WeaponEffect>().Single();
        Assert.Equal(heavy.ArcPercent, p.Weapon.Effect.ArcPercent); // not the ballistic missile's lob
    }

    [Fact]
    public void FiresOnlyOnOrders()
    {
        var (w, t, target) = Range(75);
        Assert.Empty(Launches(w, 300));
        Assert.Equal(10, t.Charges[0]);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Assert.NotEmpty(Launches(w, Deploy + 60));
    }

    [Fact]
    public void AnAttackOnAnUnseenUnitNeedsASpotterButAGroundPointDoesNot()
    {
        var w = Arena.New(160, edit: s => s with { Fog = true });
        var (t, _) = w.SpawnCrewed(1, "longrange_rocket_truck", 5, Y, Facing.East);
        var enemy = w.Spawn(2, "worker", 85, Y); // 80 cells off, far outside its sight
        w.Run(3);
        Assert.False(World.IsVisibleTo(enemy, 1));
        w.Order(1, CommandKind.Attack, [t], enemy.Id);
        Assert.Empty(Launches(w, Deploy + 60));

        w.Order(1, CommandKind.AttackGround, [t], cell: new Cell(85, Y));
        Assert.NotEmpty(Launches(w, Deploy + 60));
    }

    [Fact]
    public void ReloadsOnlyStowedAtADepotTwentySecondsAndEightySupplyEach()
    {
        var (w, t, _) = Range(70, depot: true);
        var depot = w.Units.First(u => u.Def.Archetype.Id == "supply_depot");
        Ground(w, t, new Cell(75, Y));
        w.Run(Deploy + 10 * Interval + 10);
        Assert.Equal(0, t.Charges[0]);

        // Out of rockets the order ends; at the depot it packs up first (5 s), and only then reloads.
        w.Run(Stow + 30);
        Assert.Equal(DeployState.Stowed, t.Deploy);
        Assert.Equal(0, t.Charges[0]);
        var stock = depot.Stock;
        w.Run(Reload);
        Assert.Equal(1, t.Charges[0]);
        Assert.Equal(stock - Cost, depot.Stock);
        w.Run(Reload);
        Assert.Equal(2, t.Charges[0]);
        Assert.Equal(stock - 2 * Cost, depot.Stock);
    }

    [Fact]
    public void NoReloadAwayFromADepotOrWhileDeployed()
    {
        var (w, t, _) = Range(70);
        Ground(w, t, new Cell(75, Y));
        w.Run(Deploy + 10 * Interval + 10);
        Assert.Equal(0, t.Charges[0]);
        w.Run(Reload * 2);
        Assert.Equal(0, t.Charges[0]);

        var (w2, t2, _) = Range(70, depot: true);
        w2.Order(1, CommandKind.Deploy, [t2]);
        w2.Run(Deploy + 2);
        t2.MutableCharges[0] = 5;
        w2.Run(Stow - 20); // idle at a depot it packs up first; nothing reloads until it is stowed
        Assert.NotEqual(DeployState.Stowed, t2.Deploy);
        Assert.Equal(5, t2.Charges[0]);
    }

    [Fact]
    public void RunningTwiceGivesTheSameHash()
    {
        ulong Run()
        {
            var (w, t, target) = Range(100);
            w.Order(1, CommandKind.Attack, [t], target.Id);
            w.Run(Deploy + 10 * Interval + 400);
            return w.Hash();
        }
        Assert.Equal(Run(), Run());
    }
}
