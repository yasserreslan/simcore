using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

public class ProjectileHitTests
{
    // Runs until the first detonation and returns it.
    private static Detonation RunToDetonation(World w, int maxTicks = 100)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            w.Step();
            if (w.Detonations.Count > 0)
            {
                return w.Detonations[0];
            }
        }
        throw new Xunit.Sdk.XunitException("no detonation");
    }

    [Fact]
    public void HitsTheFirstUnitInItsPathEvenAFriend()
    {
        var w = Arena.New(32);
        var hunter = w.Spawn(1, "Tank hunter", 4, 16, Facing.East);
        var friend = w.Spawn(1, "worker", 7, 16);     // unarmed, standing in the line of fire
        var truck = w.Spawn(2, "technical", 10, 16); // empty: it can't shoot back
        w.Order(1, CommandKind.Attack, [hunter], truck.Id);
        var d = RunToDetonation(w);
        Assert.Equal(friend.Id, d.Hit);
        Assert.False(d.Terrain);
        Assert.True(friend.HP < friend.Def.MaxHp);
        Assert.Equal(truck.Def.MaxHp, truck.HP);
        // It went off on the friend's near side, not at the aim point.
        Assert.True(d.At.X < friend.Pos.X);
    }

    [Fact]
    public void DirectHitDetonatesOnTheHullNotAtTheCentre()
    {
        var w = Arena.New(32);
        var hunter = w.Spawn(1, "Tank hunter", 4, 16, Facing.East);
        var truck = w.Spawn(2, "technical", 10, 16);
        w.Order(1, CommandKind.Attack, [hunter], truck.Id);
        var d = RunToDetonation(w);
        Assert.Equal(truck.Id, d.Hit);
        var gap = (truck.Pos - d.At).Len();
        Assert.True(Math.Abs(gap.Raw - truck.Def.Archetype.Radius.Raw) <= 4, $"went off {gap.Raw} raw from the centre");
        Assert.Equal(truck.Def.MaxHp - 84, truck.HP); // full AT damage, 120% vs light vehicles
    }

    [Fact]
    public void MissesATargetThatMovedAndGoesOffOnTheGround()
    {
        var w = Arena.New(32);
        var hunter = w.Spawn(1, "Tank hunter", 4, 16, Facing.East);
        var truck = w.Spawn(2, "technical", 10, 16);
        w.Order(1, CommandKind.Attack, [hunter], truck.Id);
        for (var i = 0; i < 100 && w.Projectiles.Count == 0; i++)
        {
            w.Step();
        }
        var p = Assert.Single(w.Projectiles);
        truck.Pos = new Vec(Fixed.FromInt(10), Fixed.FromInt(22)); // it drove off the aim point
        var d = RunToDetonation(w);
        Assert.Equal(0u, d.Hit);
        Assert.False(d.Terrain);
        Assert.Equal(p.Dest, d.At);
        Assert.Equal(truck.Def.MaxHp, truck.HP);
    }

    [Fact]
    public void StopsAtARock()
    {
        var w = Arena.New(32);
        w.Grid.Set(new Cell(7, 16), Terrain.Blocked);
        var hunter = w.Spawn(1, "Tank hunter", 4, 16, Facing.East);
        var truck = w.Spawn(2, "technical", 10, 16);
        w.Order(1, CommandKind.Attack, [hunter], truck.Id);
        var d = RunToDetonation(w);
        Assert.True(d.Terrain);
        Assert.Equal(0u, d.Hit);
        Assert.Equal(new Cell(7, 16), Cell.Of(d.At));
        Assert.Equal(truck.Def.MaxHp, truck.HP);
    }

    [Fact]
    public void LauncherRocketsComeDownOnRubbleAtTheirPoint()
    {
        // Blocked cells around the aim point, as a destroyed building's rubble leaves them: a lobbed
        // rocket comes down on them from above, at its own point, not at their edge.
        var w = Arena.New(32);
        for (var x = 7; x <= 11; x++)
        {
            for (var y = 14; y <= 18; y++)
            {
                w.Grid.Set(new Cell(x, y), Terrain.Blocked);
            }
        }
        var (l, _) = w.SpawnCrewed(1, "light_rocket_truck", 3, 16, Facing.East);
        w.Order(1, CommandKind.AttackGround, [l], cell: new Cell(9, 16));
        var dests = new List<(uint Id, Vec Dest)>();
        var all = new List<Detonation>();
        for (var i = 0; i < 400; i++)
        {
            dests.AddRange(w.Projectiles.Where(p => dests.All(d => d.Id != p.Id)).Select(p => (p.Id, p.Dest)));
            w.Step();
            all.AddRange(w.Detonations);
        }
        Assert.NotEmpty(all);
        foreach (var d in all)
        {
            Assert.False(d.Terrain, "a lobbed rocket is not stopped by rubble or rocks");
            Assert.Equal(dests.Single(x => x.Id == d.Projectile).Dest, d.At);
        }
    }

    [Fact]
    public void LauncherRocketsFlyOverUnitsUntilTheyComeDown()
    {
        var w = Arena.New(32);
        var (l, _) = w.SpawnCrewed(1, "light_rocket_truck", 4, 16, Facing.East);
        var friend = w.Spawn(1, "Rifleman", 6, 16); // under the arc
        var target = w.Spawn(2, "technical", 10, 16);
        target.HP = 100_000;
        w.Order(1, CommandKind.Attack, [l], target.Id);
        w.Run(60);
        Assert.Equal(friend.Def.MaxHp, friend.HP);
        Assert.True(target.HP < 100_000);
    }

    [Fact]
    public void SameInputsSameDetonations()
    {
        List<Detonation> Run()
        {
            var w = Arena.New(32);
            var hunter = w.Spawn(1, "Tank hunter", 4, 16, Facing.East);
            w.Spawn(1, "Rifleman", 7, 15);
            var truck = w.Spawn(2, "technical", 10, 16);
            w.Order(1, CommandKind.Attack, [hunter], truck.Id);
            var all = new List<Detonation>();
            for (var i = 0; i < 300; i++)
            {
                w.Step();
                all.AddRange(w.Detonations);
            }
            return all;
        }
        var a = Run();
        Assert.NotEmpty(a);
        Assert.Equal(a, Run());
    }
}
