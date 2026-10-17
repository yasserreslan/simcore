using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// The Kornet team (kornet): a guided missile, 1 s to aim before every shot, standing still,
// guided up to 36 cells of flight while the operator stays put, 5 s to reload once it stops
// guiding; the atgm warhead and the medium_vehicle class of the launchers.
public class KornetTests
{
    private const int Aim = 20, Reload = 100;

    private static (World W, Unit K, Unit Target) Range(string target, int tx, int ty, int kx = 20, int ky = 40)
    {
        var w = Arena.New(96);
        var k = w.Spawn(1, "kornet", kx, ky, Facing.East);
        var (t, _) = target is "technical" or "dirt_bike" or "cargo_truck" ? w.SpawnCrewed(2, target, tx, ty) : (w.Spawn(2, target, tx, ty), null);
        t.HP = 100_000;
        // Spotters (a worker sees 5 cells) along the target's way.
        for (var y = ty - 3; y <= ty + 30; y += 6)
        {
            w.Spawn(1, "worker", tx + 3, y);
        }
        return (w, k, t);
    }

    // Runs until a projectile is in flight (fails after 10 s).
    private static void UntilLaunch(World w)
    {
        for (var i = 0; i < 200 && w.Projectiles.Count == 0; i++)
        {
            w.Step();
        }
        Assert.NotEmpty(w.Projectiles);
    }

    // Runs until the first projectile of the kornet goes off (or gives up), returning that tick
    // and the projectile's state on its last tick in flight.
    private static (uint Tick, Projectile? Last) UntilGoneOff(World w, int maxTicks)
    {
        Projectile? seen = null;
        for (var i = 0; i < maxTicks; i++)
        {
            w.Step();
            if (w.Projectiles.Count > 0)
            {
                seen = w.Projectiles[0];
            }
            else if (seen is not null)
            {
                return (w.Tick, seen);
            }
        }
        return (0, seen);
    }

    [Fact]
    public void HomingHitsATechnicalCrossingAtSpeed()
    {
        // 12 cells east of the operator, driving south across his line: a straight shot would miss.
        var (w, k, t) = Range("technical", 32, 30);
        w.Order(2, CommandKind.Move, [t], cell: new Cell(32, 70));
        w.Run(15); // let it get going
        w.Order(1, CommandKind.Attack, [k], t.Id);
        var (tick, last) = UntilGoneOff(w, 200);
        Assert.NotEqual(0u, tick);
        Assert.True(last!.Guided);
        Assert.Equal(100_000 - 126, t.HP);
    }

    [Fact]
    public void HitsATechnicalFleeingFromTheEdgeOfRange()
    {
        // 14 cells away, driving straight away at full speed from the launch: caught well inside
        // the 36-cell guided flight.
        var (w, k, t) = Range("technical", 34, 40);
        w.Order(1, CommandKind.Attack, [k], t.Id);
        UntilLaunch(w);
        w.Order(2, CommandKind.Move, [t], cell: new Cell(90, 40));
        var (tick, last) = UntilGoneOff(w, 200);
        Assert.NotEqual(0u, tick);
        Assert.True(last!.Guided);
        Assert.Equal(100_000 - 126, t.HP);
    }

    [Fact]
    public void BeyondTheGuidedFlightItDropsUnguided()
    {
        var (w, k, t) = Range("technical", 33, 40);
        w.Order(1, CommandKind.Attack, [k], t.Id);
        UntilLaunch(w);
        // The target is suddenly 45 cells from the operator: farther than the 36-cell guided flight.
        t.Pos = new Cell(60, 66).Center();
        w.Step();
        var p = w.Projectiles[0];
        Assert.False(p.Guided);
        Assert.Equal(0u, k.Guiding);
        UntilGoneOff(w, 200);
        Assert.Equal(100_000, t.HP); // it went into the ground
        Assert.True(k.Reload > 0); // reloading since it lost guidance
    }

    [Fact]
    public void MovingTheOperatorCutsGuidanceAtOnce()
    {
        var (w, k, t) = Range("technical", 33, 30);
        w.Order(2, CommandKind.Move, [t], cell: new Cell(33, 70));
        w.Order(1, CommandKind.Attack, [k], t.Id);
        UntilLaunch(w);
        Assert.True(w.Projectiles[0].Guided);
        w.Order(1, CommandKind.Move, [k], cell: new Cell(10, 40));
        w.Run(2);
        Assert.False(w.Projectiles.Count > 0 && w.Projectiles[0].Guided);
        Assert.Equal(0u, k.Guiding);
        UntilGoneOff(w, 200);
        Assert.Equal(100_000, t.HP); // flying straight, it missed the crossing technical
    }

    [Fact]
    public void AimsOneSecondBeforeEveryShotAndReloadsFiveSeconds()
    {
        var (w, k, t) = Range("cargo_truck", 30, 40); // unarmed: it can't shoot back over the 10 s
        w.Order(1, CommandKind.Attack, [k], t.Id);
        w.Step(); // the order lands
        var ordered = w.Tick;
        UntilLaunch(w);
        var launch1 = w.Tick;
        Assert.InRange((int)(launch1 - ordered), Aim, Aim + 1);
        Assert.Equal(0, k.AimLeft);
        Assert.NotEqual(0u, k.Guiding);
        var (hit1, _) = UntilGoneOff(w, 100);
        Assert.Equal(0u, k.Guiding);
        UntilLaunch(w);
        // Reload from the moment it stopped guiding, then aim again.
        Assert.InRange((int)(w.Tick - hit1), Reload + Aim - 1, Reload + Aim + 1);
    }

    [Fact]
    public void HitsOnlyItsTargetNotInfantryInThePath()
    {
        var (w, k, t) = Range("technical", 34, 40);
        w.Order(2, CommandKind.Stop, [t]);
        var inTheWay = w.Spawn(2, "Rifleman", 27, 40);
        w.Order(1, CommandKind.Attack, [k], t.Id);
        UntilGoneOff(w, 200);
        Assert.Equal(inTheWay.Def.MaxHp, inTheWay.HP);
        Assert.Equal(100_000 - 126, t.HP);
    }

    [Fact]
    public void GuidedOntoABuildingWallLikeAnRpgHit()
    {
        var w = Arena.New(96);
        var k = w.Spawn(1, "kornet", 14, 9, Facing.East);
        var block = w.Spawn(2, "block_brick_4", 28, 6); // 14 x 7 cells: its centre is far, its wall in range
        w.Spawn(1, "worker", 34, 14);
        var hp = block.HP;
        w.Order(1, CommandKind.Attack, [k], block.Id);
        var (tick, last) = UntilGoneOff(w, 200);
        Assert.NotEqual(0u, tick);
        Assert.True(last!.Guided);
        Assert.Equal(56, hp - block.HP);
    }

    [Fact]
    public void AttackGroundAimsAndGuidesOntoThePoint()
    {
        var w = Arena.New(96);
        var k = w.Spawn(1, "kornet", 14, 9, Facing.East);
        var point = new Cell(27, 12);
        w.Order(1, CommandKind.AttackGround, [k], cell: point);
        w.Step(); // the order lands
        var ordered = w.Tick;
        UntilLaunch(w);
        Assert.InRange((int)(w.Tick - ordered), Aim, Aim + 1); // the same 1 s aim
        var p = w.Projectiles[0];
        Assert.Equal(0u, p.Target);
        var lastGuided = false;
        uint landed = 0;
        for (var i = 0; i < 100 && landed == 0; i++)
        {
            lastGuided = w.Projectiles.Count > 0 && w.Projectiles[0].Guided;
            w.Step();
            if (w.Projectiles.Count == 0)
            {
                landed = w.Tick;
            }
        }
        Assert.True(lastGuided); // guided all the way
        Assert.Equal(point.Center(), p.Pos); // onto the point
        Assert.Equal(0u, k.Guiding);
        Assert.True(k.Reload > 0);
    }

    [Fact]
    public void AttackGroundOnABuildingHitsItsWall()
    {
        // The owner's case: attack ground on a cell of an enemy block (a click on it). The missile
        // aims, stays guided and hits the wall on the way to the point: an RPG hit's 56.
        var w = Arena.New(96);
        var k = w.Spawn(1, "kornet", 14, 9, Facing.East);
        var block = w.Spawn(2, "block_brick_4", 28, 6);
        var hp = block.HP;
        w.Order(1, CommandKind.AttackGround, [k], cell: Cell.Of(block.Pos));
        UntilLaunch(w);
        var guided = true;
        for (var i = 0; i < 100 && w.Projectiles.Count > 0; i++)
        {
            guided &= w.Projectiles[0].Guided;
            w.Step();
        }
        Assert.True(guided);
        Assert.Equal(56, hp - block.HP);
        Assert.True(block.Alive);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ShortRangeShotsStayGuidedOntoAVehicle(int cells)
    {
        var (w, k, t) = Range("cargo_truck", 20 + cells, 40);
        w.Order(1, CommandKind.Attack, [k], t.Id);
        UntilLaunch(w);
        var guided = true;
        for (var i = 0; i < 60 && w.Projectiles.Count > 0; i++)
        {
            guided &= w.Projectiles[0].Guided;
            w.Step();
        }
        Assert.True(guided);
        Assert.Equal(100_000 - 126, t.HP);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void ShortRangeShotsStayGuidedOntoABuilding(int cells)
    {
        var w = Arena.New(96);
        var block = w.Spawn(2, "block_brick_4", 30, 30); // its west wall at x = 30 - 7 = 23
        var wall = (int)(block.Pos.X.Raw / 1024) - block.Def.Archetype.FootprintW / 2;
        var k = w.Spawn(1, "kornet", wall - cells, (int)(block.Pos.Y.Raw / 1024), Facing.East);
        var hp = block.HP;
        w.Order(1, CommandKind.Attack, [k], block.Id);
        UntilLaunch(w);
        var guided = true;
        for (var i = 0; i < 60 && w.Projectiles.Count > 0; i++)
        {
            guided &= w.Projectiles[0].Guided;
            w.Step();
        }
        Assert.True(guided);
        Assert.Equal(56, hp - block.HP);
    }

    [Fact]
    public void DamagePerArmorClass()
    {
        var w = Arena.New(64);
        var h = w.Content.Warhead("atgm")!;
        var rpg = w.Content.Warhead("rocket")!;
        var tech = w.Spawn(2, "technical", 5, 5);
        var launcher = w.Spawn(2, "heavy_rocket_launcher", 15, 5);
        var missile = w.Spawn(2, "missile_truck", 25, 5);
        var heavy = w.Spawn(2, "heavy_rocket_truck", 35, 5);
        var soldier = w.Spawn(2, "Rifleman", 45, 5);
        var block = w.Spawn(2, "block_brick_4", 5, 30);
        int Dealt(Unit u, Model.Warhead wh, int dmg)
        {
            var before = u.HP;
            w.Hurt(u, wh, dmg, 100);
            return before - u.HP;
        }
        Assert.Equal(126, Dealt(tech, h, 126));                 // 70% of the technical's 180
        Assert.Equal(69, Dealt(launcher, h, 126));              // 31% of 220: never a one-shot
        Assert.Equal(69, Dealt(missile, h, 126));               // 27% of 260
        Assert.Equal(69, Dealt(heavy, h, 126));                 // 23% of 300
        Assert.Equal(18, Dealt(soldier, h, 126));               // barely
        Assert.Equal(Dealt(block, rpg, 70), Dealt(block, h, 126)); // a building takes an RPG hit's damage
        Assert.False(h.CanHit(w.Content.ArmorClass("drone")));
    }

    [Fact]
    public void OtherWarheadsTreatLaunchersAsBefore()
    {
        var c = TestContent.Mvp;
        var light = c.ArmorClass("light_vehicle");
        var medium = c.ArmorClass("medium_vehicle");
        foreach (var wh in c.Warheads.Where(x => x.Id != "atgm"))
        {
            Assert.Equal(wh.Versus[light], wh.Versus[medium]);
        }
        foreach (var id in new[] { "light_rocket_truck", "medium_rocket_launcher", "heavy_rocket_launcher", "heavy_rocket_truck", "missile_truck" })
        {
            Assert.Equal(medium, c.Archetypes.First(a => a.Id == id).Armor);
        }
        Assert.Equal(light, c.Archetypes.First(a => a.Id == "technical").Armor);
        Assert.Equal(light, c.Archetypes.First(a => a.Id == "cargo_truck").Armor);
    }

    [Fact]
    public void PicksVehiclesFirstThenBuildingsThenInfantry()
    {
        var w = Arena.New(64);
        var k = w.Spawn(1, "kornet", 20, 20, Facing.East);
        var soldier = w.Spawn(2, "Rifleman", 24, 20);
        var tech = w.Spawn(2, "technical", 27, 20);
        w.Run(3);
        Assert.Equal(tech.Id, k.AutoTarget);
        tech.HP = 0;
        w.Run(3);
        Assert.Equal(soldier.Id, k.AutoTarget);
    }
}
