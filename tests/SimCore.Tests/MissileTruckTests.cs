using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// The twin missile truck (missile_truck): 2 missiles 6 s apart from 45 to 100 cells, a two-ring
// splash (full within 3 cells, a flat 30% out to 9), deploy 6 s / stow 5 s, and a depot reload
// only while stowed: one missile every 10 s for 150 supply.
public class MissileTruckTests
{
    private const int Deploy = 120, Stow = 100, Interval = 120, Reload = 200;

    private static (World W, Unit Truck, Unit Target) Range(int targetX, bool depot = false)
    {
        var w = Arena.New(128);
        if (depot)
        {
            w.Spawn(1, "supply_depot", 3, 30);
        }
        var (t, _) = w.SpawnCrewed(1, "missile_truck", 5, 31, Facing.East);
        var target = w.Spawn(2, "technical", targetX, 31);
        target.HP = 1_000_000;
        w.Spawn(1, "worker", targetX, 27); // the spotter
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

    [Fact]
    public void TwoMissilesSixSecondsApartAfterASixSecondDeploy()
    {
        var (w, t, target) = Range(70);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        var shots = Launches(w, Deploy + Interval + 300);
        Assert.Equal(2, shots.Count);
        Assert.True(shots[0] > Deploy);
        Assert.Equal(Interval, (int)(shots[1] - shots[0]));
        Assert.Equal(0, t.Charges[0]);
    }

    [Fact]
    public void NothingCloserThanFortyFiveCells()
    {
        var (w, t, target) = Range(5 + 42);
        w.Order(1, CommandKind.Deploy, [t]);
        w.Run(Deploy + 2);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Assert.Empty(Launches(w, 300));
    }

    [Fact]
    public void TwoRingSplashFullInsideThreeCellsThirtyPercentOutToNine()
    {
        var w = Arena.New(64);
        var weapon = w.Match.Players[0].Find(w.Content, "missile_truck")!.Weapons[0];
        var at = new Cell(30, 30).Center();
        Unit Dummy(int tenths)
        {
            var u = w.Spawn(2, "technical", 30, 30);
            u.Pos = at + new Vec(Fixed.FromRaw(tenths * Fixed.OneRaw / 10), Fixed.Zero);
            u.HP = 10_000;
            return u;
        }
        var (a, b, c, d, e) = (Dummy(0), Dummy(29), Dummy(31), Dummy(89), Dummy(91));
        w.Explode(at, weapon.Damage, weapon.Warhead, weapon.Effect.Splash, 0, weapon.Effect.SplashCenter, weapon.Effect.SplashEdge,
            weapon.Effect.SplashInner, weapon.Effect.SplashOuterPercent);
        Assert.Equal(10_000 - 550, a.HP);
        Assert.Equal(10_000 - 550, b.HP);
        Assert.Equal(10_000 - 165, c.HP);
        Assert.Equal(10_000 - 165, d.HP);
        Assert.Equal(10_000, e.HP);
    }

    [Fact]
    public void TheInnerRingKillsEveryUnitAndTheOuterRingKillsInfantry()
    {
        var w = Arena.New(64);
        var weapon = w.Match.Players[0].Find(w.Content, "missile_truck")!.Weapons[0];
        var heaviest = w.Spawn(2, "heavy_rocket_truck", 30, 30); // 300 HP
        var tech = w.Spawn(2, "technical", 36, 30);              // 6 cells out
        var soldier = w.Spawn(2, "Rifleman", 30, 37);            // 7 cells out
        w.Explode(new Cell(30, 30).Center(), weapon.Damage, weapon.Warhead, weapon.Effect.Splash, 0, weapon.Effect.SplashCenter,
            weapon.Effect.SplashEdge, weapon.Effect.SplashInner, weapon.Effect.SplashOuterPercent);
        Assert.False(heaviest.Alive);
        Assert.True(tech.Alive);
        Assert.Equal(tech.Def.MaxHp - 165, tech.HP);
        Assert.False(soldier.Alive);
    }

    [Fact]
    public void TwoHitsBringDownEitherBlock()
    {
        var w = Arena.New(64);
        var h = w.Content.Warhead("heavy_missile")!;
        var panel = w.Spawn(2, "block_panel_5", 2, 2);
        var brick = w.Spawn(2, "block_brick_4", 30, 2);
        w.Hurt(panel, h, 550, 100);
        Assert.True(panel.Alive);
        w.Hurt(panel, h, 550, 100);
        Assert.False(panel.Alive);
        w.Hurt(brick, h, 550, 100);
        Assert.True(brick.Alive);
        w.Hurt(brick, h, 550, 100);
        Assert.False(brick.Alive);
    }

    [Fact]
    public void ReloadsOnlyStowedAtADepotForOneHundredFiftyEach()
    {
        var (w, t, target) = Range(70, depot: true);
        var depot = w.Units.First(u => u.Def.Archetype.Id == "supply_depot");
        w.Order(1, CommandKind.Attack, [t], target.Id);
        w.Run(Deploy + Interval + 10);
        Assert.Equal(0, t.Charges[0]);

        // Out of missiles the order ends; at the depot it packs up first (5 s), and only then
        // reloads, one missile every 10 s for 150 supply each.
        w.Run(Stow + 30);
        Assert.Equal(DeployState.Stowed, t.Deploy);
        Assert.Equal(0, t.Charges[0]);
        Assert.Equal(1000, depot.Stock);
        w.Run(Reload);
        Assert.Equal(1, t.Charges[0]);
        Assert.Equal(1000 - 150, depot.Stock);
        w.Run(Reload);
        Assert.Equal(2, t.Charges[0]);
        Assert.Equal(1000 - 300, depot.Stock);
    }

    [Fact]
    public void ExistingLinearSplashIsUnchanged()
    {
        var w = Arena.New(64);
        var weapon = w.Match.Players[0].Find(w.Content, "heavy_rocket_truck")!.Weapons[0];
        Assert.Equal(0, weapon.Effect.SplashInner.Raw);
        var at = new Cell(30, 30).Center();
        var u = w.Spawn(2, "technical", 30, 30);
        u.Pos = at + new Vec(Fixed.FromInt(2), Fixed.Zero); // 2 of 5 cells: 100 - 90 * 2 / 5 = 64%
        u.HP = 10_000;
        w.Explode(at, weapon.Damage, weapon.Warhead, weapon.Effect.Splash, 0, weapon.Effect.SplashCenter, weapon.Effect.SplashEdge,
            weapon.Effect.SplashInner, weapon.Effect.SplashOuterPercent);
        Assert.Equal(10_000 - 400 * 64 / 100, u.HP);
    }
}
