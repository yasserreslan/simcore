using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The tactical missile truck (ballistic_truck): one ballistic missile from 60 to 160 cells after a
// 10 s aim standing still (its deploy: moving cancels it), 730 damage in a two-ring splash 10%
// bigger than the twin missile's, 1.8x its speed, and a 60 s reload at a depot, stowed, for 250.
public class BallisticTruckTests
{
    private const int Aim = 200, Stow = 120, Reload = 1200;

    private static (World W, Unit Truck, Unit Target) Range(int cells, bool depot = false)
    {
        var w = Arena.New(256);
        if (depot)
        {
            w.Spawn(1, "supply_depot", 3, 30);
        }
        var (t, _) = w.SpawnCrewed(1, "ballistic_truck", 5, 31, Facing.East);
        var target = w.Spawn(2, "technical", 5 + cells, 31);
        target.HP = 1_000_000;
        w.Spawn(1, "worker", 5 + cells, 27); // the spotter
        return (w, t, target);
    }

    private static List<uint> Launches(World w, int ticks)
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
        return [.. seen.Select(s => s.Tick)];
    }

    private static WeaponDef Weapon(World w, string unit) => w.Match.Players[0].Find(w.Content, unit)!.Weapons[0];

    [Fact]
    public void AimsTenSecondsStandingStillThenFiresItsOneMissile()
    {
        var (w, t, target) = Range(120);
        var start = w.Tick;
        w.Order(1, CommandKind.Attack, [t], target.Id);
        w.Run(Aim / 2);
        Assert.Equal(DeployState.Deploying, t.Deploy);
        Assert.False(t.Moving);
        var shots = Launches(w, Aim + 200);
        Assert.Single(shots);
        Assert.InRange((int)(shots[0] - start), Aim, Aim + 10);
        Assert.Equal(0, t.Charges[0]);
    }

    [Fact]
    public void MovingCancelsTheAim()
    {
        var (w, t, target) = Range(120);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        w.Run(Aim - 20);
        Assert.Equal(DeployState.Deploying, t.Deploy);
        w.Order(1, CommandKind.Move, [t], cell: new Cell(5, 60));
        Assert.Empty(Launches(w, 60));
        Assert.True(t.Deploy is DeployState.Stowing or DeployState.Stowed);
        // Back on target, the aim starts over from the beginning.
        w.Run(Stow + 300);
        var from = w.Tick;
        w.Order(1, CommandKind.Attack, [t], target.Id);
        var shots = Launches(w, Aim + 400);
        Assert.Single(shots);
        Assert.True(shots[0] - from >= Aim);
    }

    [Fact]
    public void ReachesSixtyToOneHundredSixtyCells()
    {
        var (near, nt, ntarget) = Range(57);
        near.Order(1, CommandKind.Deploy, [nt]);
        near.Run(Aim + 2);
        near.Order(1, CommandKind.Attack, [nt], ntarget.Id);
        Assert.Empty(Launches(near, 300));

        var (far, ft, ftarget) = Range(158);
        far.Order(1, CommandKind.Attack, [ft], ftarget.Id);
        Assert.Single(Launches(far, Aim + 200));
        Assert.Equal(600 * Fixed.OneRaw / 10, Weapon(far, "ballistic_truck").Effect.MinRange.Raw);
        Assert.Equal(1600 * Fixed.OneRaw / 10, Weapon(far, "ballistic_truck").Range.Raw);
    }

    [Fact]
    public void FliesAtOnePointEightTimesTheTwinMissileOnAHighArcMarkedBallistic()
    {
        var w = Arena.New(64);
        var ballistic = Weapon(w, "ballistic_truck").Effect;
        var twin = Weapon(w, "missile_truck").Effect;
        Assert.InRange(ballistic.ProjectileSpeed.Raw * 10, twin.ProjectileSpeed.Raw * 18 - 18, twin.ProjectileSpeed.Raw * 18 + 18); // per-tick rounding
        Assert.Equal(45, ballistic.ArcPercent);
        Assert.Equal(InterceptClass.Ballistic, ballistic.Intercept);
        Assert.Equal(InterceptClass.None, twin.Intercept);

        // In flight: 120 cells in under 5 s, and the projectile carries the marker.
        var (r, t, target) = Range(120);
        r.Order(1, CommandKind.Attack, [t], target.Id);
        r.Run(Aim + 5);
        var p = Assert.Single(r.Projectiles);
        Assert.Equal(InterceptClass.Ballistic, p.Interceptible);
        var flight = 0;
        while (r.Projectiles.Count > 0 && flight < 400)
        {
            r.Step();
            flight++;
        }
        Assert.InRange(flight, 80, 100); // 120 cells at 25.2 cells/s: 4.8 s
    }

    [Fact]
    public void FliesOverWhatIsInTheWay()
    {
        // Lobbed (its own arc): a technical halfway along the line neither stops nor takes the missile.
        var (w, t, target) = Range(120);
        var inTheWay = w.Spawn(2, "technical", 65, 31);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Assert.Single(Launches(w, Aim + 200));
        Assert.Equal(inTheWay.Def.MaxHp, inTheWay.HP);
        Assert.True(target.HP < 1_000_000);
    }

    [Fact]
    public void TwoRingSplashTenPercentBiggerThanTheTwinMissile()
    {
        var w = Arena.New(64);
        var weapon = Weapon(w, "ballistic_truck");
        var at = new Cell(30, 30).Center();
        Unit Dummy(int tenths)
        {
            var u = w.Spawn(2, "technical", 30, 30);
            u.Pos = at + new Vec(Fixed.FromRaw(tenths * Fixed.OneRaw / 10), Fixed.Zero);
            u.HP = 10_000;
            return u;
        }
        var (a, b, c, d, e) = (Dummy(0), Dummy(32), Dummy(34), Dummy(98), Dummy(100));
        w.Explode(at, weapon.Damage, weapon.Warhead, weapon.Effect.Splash, 0, weapon.Effect.SplashCenter, weapon.Effect.SplashEdge,
            weapon.Effect.SplashInner, weapon.Effect.SplashOuterPercent);
        Assert.Equal(10_000 - 730, a.HP);
        Assert.Equal(10_000 - 730, b.HP);
        Assert.Equal(10_000 - 219, c.HP);
        Assert.Equal(10_000 - 219, d.HP);
        Assert.Equal(10_000, e.HP);
    }

    [Fact]
    public void OneHitBringsDownABrickBlockAndTwoAPanelBlock()
    {
        var w = Arena.New(64);
        var weapon = Weapon(w, "ballistic_truck");
        var panel = w.Spawn(2, "block_panel_5", 2, 2);
        var brick = w.Spawn(2, "block_brick_4", 30, 2);
        w.Hurt(brick, weapon.Warhead, weapon.Damage, 100);
        Assert.False(brick.Alive);
        w.Hurt(panel, weapon.Warhead, weapon.Damage, 100);
        Assert.True(panel.Alive);
        w.Hurt(panel, weapon.Warhead, weapon.Damage, 100);
        Assert.False(panel.Alive);
    }

    [Fact]
    public void ReloadsOnlyStowedAtADepotSixtySecondsForTwoHundredFifty()
    {
        var (w, t, target) = Range(120, depot: true);
        var depot = w.Units.First(u => u.Def.Archetype.Id == "supply_depot");
        w.Order(1, CommandKind.Attack, [t], target.Id);
        w.Run(Aim + 10);
        Assert.Equal(0, t.Charges[0]);

        // Out of its missile the order ends; at the depot it packs up first, then reloads in 60 s.
        w.Run(Stow + 30);
        Assert.Equal(DeployState.Stowed, t.Deploy);
        w.Run(Reload - 60);
        Assert.Equal(0, t.Charges[0]);
        Assert.Equal(1000, depot.Stock);
        w.Run(60);
        Assert.Equal(1, t.Charges[0]);
        Assert.Equal(1000 - 250, depot.Stock);
    }

    [Fact]
    public void NoReloadAwayFromADepot()
    {
        var (w, t, target) = Range(120);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        w.Run(Aim + 10 + Stow + Reload * 2);
        Assert.Equal(0, t.Charges[0]);
    }

    [Fact]
    public void TheSlowestLauncherSlowToGetGoing()
    {
        var w = Arena.New(64);
        var all = w.Content.Archetypes.Where(a => a.Locomotor is Locomotor.Wheeled or Locomotor.Tracked && a.Speed.Raw > 0).ToList(); // every vehicle
        var mine = all.Single(a => a.Id == "ballistic_truck");
        var heavy = all.Single(a => a.Id == "heavy_rocket_truck");
        Assert.Equal(mine.Speed.Raw, all.Min(a => a.Speed.Raw));
        Assert.InRange(mine.Speed.Raw * 100 / heavy.Speed.Raw, 66, 72);
        Assert.Equal(300, mine.MaxHp);
        Assert.Equal(2000, mine.Cost);

        // Two seconds to top speed on open ground.
        var (t, _) = w.SpawnCrewed(1, "ballistic_truck", 5, 30, Facing.East);
        w.Order(1, CommandKind.Move, [t], cell: new Cell(60, 30));
        w.Run(20);
        Assert.True(t.DriveSpeed.Raw < mine.Speed.Raw * 3 / 4);
        w.Run(40);
        Assert.Equal(mine.Speed.Raw, t.DriveSpeed.Raw);
    }
}
