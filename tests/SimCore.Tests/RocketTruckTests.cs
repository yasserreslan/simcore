using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The rocket truck (heavy_rocket_launcher): deploys before firing, stows before
// moving, ripple-fires 30 rockets, has a minimum range, and only a depot reloads it.
public class RocketTruckTests
{
    private const int Deploy = 50, Stow = 40, Interval = 10;

    private static (World W, Unit Truck, Unit Target) Range(int targetX = 40, bool depot = false)
    {
        var w = Arena.New(64);
        if (depot)
        {
            w.Spawn(1, "supply_depot", 3, 30);
        }
        var (t, _) = w.SpawnCrewed(1, "heavy_rocket_launcher", 5, 31, Facing.East);
        var target = w.Spawn(2, "technical", targetX, 31);
        target.HP = 1_000_000;
        w.Spawn(1, "worker", targetX, 27); // the spotter
        return (w, t, target);
    }

    private static int Launched(World w, int ticks)
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
        return seen.Count;
    }

    [Fact]
    public void DeploysBeforeTheFirstRocket()
    {
        var (w, t, target) = Range();
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Assert.Equal(0, Launched(w, Deploy - 5));
        Assert.Equal(DeployState.Deploying, t.Deploy);
        Assert.True(Launched(w, 20) > 0);
        Assert.Equal(DeployState.Deployed, t.Deploy);
    }

    [Fact]
    public void RipplesThirtyRocketsHalfASecondApartAndRunsDry()
    {
        var (w, t, target) = Range();
        w.Order(1, CommandKind.Attack, [t], target.Id);
        var shots = new List<uint>();
        var seen = new HashSet<uint>();
        for (var i = 0; i < Deploy + 30 * Interval + 100; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles.Where(p => seen.Add(p.Id)))
            {
                shots.Add(w.Tick);
            }
        }
        Assert.Equal(30, shots.Count);
        Assert.All(shots.Zip(shots.Skip(1)), p => Assert.Equal((uint)Interval, p.Second - p.First));
        Assert.Equal([0], t.Charges);
        Assert.Equal(OrderKind.Idle, t.Order.Kind); // out of rockets
    }

    [Fact]
    public void StopEndsTheSalvoAndStaysDeployed()
    {
        var (w, t, target) = Range();
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Launched(w, Deploy + 5 * Interval);
        var left = t.Charges[0];
        Assert.True(left is > 0 and < 30);
        var before = w.Projectiles.Select(p => p.Id).DefaultIfEmpty(0u).Max();
        w.Order(1, CommandKind.Stop, [t]);
        var later = 0;
        for (var i = 0; i < 200; i++)
        {
            w.Step();
            later += w.Projectiles.Count(p => p.Id > before && p.Shooter == t.Id);
        }
        Assert.Equal(0, later);
        Assert.Equal(left, t.Charges[0]);
        Assert.Equal(DeployState.Deployed, t.Deploy);
        Assert.Equal(0, t.SalvoLeft);
    }

    [Fact]
    public void StowsBeforeDriving()
    {
        var (w, t, target) = Range();
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Launched(w, Deploy + 2 * Interval);
        var at = t.Pos;
        w.Order(1, CommandKind.Move, [t], cell: new Cell(5, 40));
        w.Run(Stow - 5);
        Assert.Equal(DeployState.Stowing, t.Deploy);
        Assert.Equal(at, t.Pos);
        w.Run(40);
        Assert.Equal(DeployState.Stowed, t.Deploy);
        Assert.NotEqual(at, t.Pos);
    }

    [Fact]
    public void TooCloseIsOutOfReach()
    {
        var (w, t, target) = Range(targetX: 15); // 10 cells: inside the 15-cell minimum range
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Assert.Equal(0, Launched(w, 200));
    }

    [Fact]
    public void DirectHitTakesThreeQuartersOfATechnical()
    {
        var w = Arena.New(64);
        var (t, _) = w.SpawnCrewed(1, "heavy_rocket_launcher", 5, 31, Facing.East);
        var truck = w.Spawn(2, "technical", 30, 31);
        var hp = truck.Def.MaxHp;
        var wpn = t.Def.Weapons[0];
        // 113 damage x 120% vs light vehicles = 135 of 180 HP.
        Assert.Equal(135, wpn.Warhead.Damage(wpn.Damage, truck.Def.Armor, 100));
        Assert.Equal(75, 100 * 135 / hp);
        // Splash: 28% at the centre, 10% at the edge.
        Assert.Equal((28, 10), (wpn.Effect.SplashCenter, wpn.Effect.SplashEdge));
    }

    [Fact]
    public void OnlyADepotReloadsItRoundByRoundAfterASecond()
    {
        var w = Arena.New(64);
        var depot = w.Spawn(1, "supply_depot", 3, 30);
        var (t, _) = w.SpawnCrewed(1, "heavy_rocket_launcher", 6, 31);
        w.Run(2);
        t.MutableCharges[0] = 0;
        t.LastShot = w.Tick;
        w.Run(19);
        Assert.Equal([0], t.Charges); // within a second of the last shot
        w.Run(21 + 40);
        Assert.Equal([1], t.Charges); // 1 s wait, then 2 s
        w.Run(40 * 4);
        Assert.Equal([5], t.Charges);

        // Away from the depot nothing reloads; an ammo truck doesn't either.
        var (far, _) = w.SpawnCrewed(1, "heavy_rocket_launcher", 40, 40);
        w.Spawn(1, "ammo_truck", 41, 41);
        far.MutableCharges[0] = 0;
        w.Run(400);
        Assert.Equal([0], far.Charges);
        _ = depot;
    }

    [Fact]
    public void NoReloadDuringASalvo()
    {
        var (w, t, target) = Range(depot: true);
        t.MutableCharges[0] = 10;
        w.Order(1, CommandKind.Attack, [t], target.Id);
        w.Run(Deploy + 2);
        var charges = new List<int>();
        while (t.SalvoLeft > 0)
        {
            charges.Add(t.Charges[0]);
            w.Step();
        }
        Assert.True(charges.Count > 50);
        Assert.True(charges.Zip(charges.Skip(1)).All(p => p.Second <= p.First), "a rocket was reloaded mid-salvo");
    }

    [Fact]
    public void DeployAndStowCommands()
    {
        var w = Arena.New(32);
        var (t, _) = w.SpawnCrewed(1, "heavy_rocket_launcher", 5, 16);
        w.Order(1, CommandKind.Deploy, [t]);
        w.Run(Deploy + 2);
        Assert.Equal(DeployState.Deployed, t.Deploy);
        w.Order(1, CommandKind.Stow, [t]);
        w.Run(Stow + 2);
        Assert.Equal(DeployState.Stowed, t.Deploy);
    }
}
