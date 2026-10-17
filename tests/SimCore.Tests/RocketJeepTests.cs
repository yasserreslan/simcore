using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// The rocket jeep (rocket_jeep): a fast 4x4 with 2 heavy rockets 3 s apart from 20 to 50 cells,
// deploy 3 s / stow 2 s, reloaded only stowed at a depot: one rocket every 6 s for 80 supply.
public class RocketJeepTests
{
    private const int Deploy = 60, Stow = 40, Interval = 60, Reload = 120;

    private static (World W, Unit Jeep, Unit Target) Range(int targetX, bool depot = false)
    {
        var w = Arena.New(96);
        if (depot)
        {
            w.Spawn(1, "supply_depot", 3, 30);
        }
        var (j, _) = w.SpawnCrewed(1, "rocket_jeep", 5, 31, Facing.East);
        var target = w.Spawn(2, "cargo_truck", targetX, 31); // unarmed
        target.HP = 1_000_000;
        w.Spawn(1, "worker", targetX, 27); // the spotter
        return (w, j, target);
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
    public void TwoRocketsThreeSecondsApartAfterAThreeSecondDeploy()
    {
        var (w, j, target) = Range(40);
        w.Order(1, CommandKind.Attack, [j], target.Id);
        var shots = Launches(w, Deploy + Interval + 200);
        Assert.Equal(2, shots.Count);
        Assert.True(shots[0] > Deploy);
        Assert.Equal(Interval, (int)(shots[1] - shots[0]));
        Assert.Equal(0, j.Charges[0]);
    }

    [Fact]
    public void FastAsATechnicalFasterThanTheOtherLaunchers()
    {
        var c = TestContent.Mvp;
        var speed = c.Archetype("rocket_jeep")!.Speed;
        Assert.Equal(c.Archetype("technical")!.Speed, speed);
        foreach (var id in new[] { "light_rocket_truck", "medium_rocket_launcher", "heavy_rocket_launcher", "heavy_rocket_truck", "missile_truck" })
        {
            Assert.True(speed > c.Archetype(id)!.Speed, id);
        }
        Assert.Equal(c.ArmorClass("medium_vehicle"), c.Archetype("rocket_jeep")!.Armor);
    }

    [Fact]
    public void DeploysInThreeSecondsAndStowsInTwo()
    {
        var (w, j, _) = Range(40);
        w.Order(1, CommandKind.Deploy, [j]);
        w.Step();
        var start = w.Tick;
        while (j.Deploy != DeployState.Deployed && w.Tick < start + 200)
        {
            w.Step();
        }
        Assert.InRange((int)(w.Tick - start), Deploy - 1, Deploy + 1);
        w.Order(1, CommandKind.Stow, [j]);
        w.Step();
        start = w.Tick;
        while (j.Deploy != DeployState.Stowed && w.Tick < start + 200)
        {
            w.Step();
        }
        Assert.InRange((int)(w.Tick - start), Stow - 1, Stow + 1);
    }

    [Fact]
    public void NothingCloserThanTwentyCells()
    {
        var (w, j, target) = Range(5 + 18);
        w.Order(1, CommandKind.Deploy, [j]);
        w.Run(Deploy + 2);
        w.Order(1, CommandKind.Attack, [j], target.Id);
        Assert.Empty(Launches(w, 200));
    }

    [Fact]
    public void ReloadsOnlyStowedAtADepotEightyEachEverySixSeconds()
    {
        var (w, j, target) = Range(40, depot: true);
        var depot = w.Units.First(u => u.Def.Archetype.Id == "supply_depot");
        w.Order(1, CommandKind.Attack, [j], target.Id);
        w.Run(Deploy + Interval + 10);
        Assert.Equal(0, j.Charges[0]);
        w.Run(Stow + 30); // out of rockets: it packs up at the depot first
        Assert.Equal(DeployState.Stowed, j.Deploy);
        Assert.Equal(0, j.Charges[0]);
        Assert.Equal(1000, depot.Stock);
        w.Run(Reload);
        Assert.Equal(1, j.Charges[0]);
        Assert.Equal(1000 - 80, depot.Stock);
        w.Run(Reload);
        Assert.Equal(2, j.Charges[0]);
        Assert.Equal(1000 - 160, depot.Stock);
    }

    [Fact]
    public void TwoHitsDropABrickBlockThreeAPanelBlock()
    {
        var w = Arena.New(64);
        var h = w.Content.Warhead("heavy_rocket")!;
        var brick = w.Spawn(2, "block_brick_4", 30, 2);
        var panel = w.Spawn(2, "block_panel_5", 2, 30);
        w.Hurt(brick, h, 450, 100);
        Assert.True(brick.Alive);
        w.Hurt(brick, h, 450, 100);
        Assert.False(brick.Alive);
        w.Hurt(panel, h, 450, 100);
        w.Hurt(panel, h, 450, 100);
        Assert.True(panel.Alive);
        w.Hurt(panel, h, 450, 100);
        Assert.False(panel.Alive);
    }

    [Fact]
    public void SplashFallsFromFullToTwentyPercentAtFourCells()
    {
        var w = Arena.New(64);
        var weapon = w.Match.Players[0].Find(w.Content, "rocket_jeep")!.Weapons[0];
        var at = new Cell(30, 30).Center();
        Unit Dummy(int tenths)
        {
            var u = w.Spawn(2, "cargo_truck", 30, 30);
            u.Pos = at + new Vec(Fixed.FromRaw(tenths * Fixed.OneRaw / 10), Fixed.Zero);
            u.HP = 10_000;
            return u;
        }
        var (a, e, outside) = (Dummy(0), Dummy(40), Dummy(41));
        w.Explode(at, weapon.Damage, weapon.Warhead, weapon.Effect.Splash, 0, weapon.Effect.SplashCenter, weapon.Effect.SplashEdge,
            weapon.Effect.SplashInner, weapon.Effect.SplashOuterPercent);
        Assert.Equal(10_000 - 450, a.HP);
        Assert.Equal(10_000 - 90, e.HP); // 20% at the edge
        Assert.Equal(10_000, outside.HP);
    }
}
