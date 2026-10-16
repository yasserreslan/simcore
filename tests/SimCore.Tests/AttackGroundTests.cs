using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

public class AttackGroundTests
{
    private static int Shots(World w, Unit u, int ticks)
    {
        var shots = 0;
        for (var i = 0; i < ticks; i++)
        {
            var reload = u.Reload;
            w.Step();
            if (u.Reload > reload)
            {
                shots++;
            }
        }
        return shots;
    }

    [Fact]
    public void RiflemanFiresAtTheGroundRepeatedly()
    {
        var w = Arena.New(32);
        var r = w.Spawn(1, "Rifleman", 5, 16, Facing.East);
        w.Order(1, CommandKind.AttackGround, [r], cell: new Cell(9, 16));
        var shots = Shots(w, r, 100);
        Assert.True(shots >= 5, $"fired {shots} times");
        Assert.Equal(OrderKind.AttackGround, r.Order.Kind);
        Assert.Equal(new Cell(9, 16), r.Order.Cell);
        Assert.Equal(Facing.East, r.Facing);
    }

    [Fact]
    public void WalksIntoRangeFirst()
    {
        var w = Arena.New(32);
        var r = w.Spawn(1, "Rifleman", 2, 16, Facing.East);
        var target = new Cell(25, 16);
        w.Order(1, CommandKind.AttackGround, [r], cell: target);
        w.Run(5);
        Assert.True(r.Moving);
        var range = r.Def.Weapons[0].Range;
        for (var i = 0; i < 600 && !World.Within(r.Pos, target.Center(), range); i++)
        {
            w.Step();
        }
        Assert.True(World.Within(r.Pos, target.Center(), range));
        Assert.True(Shots(w, r, 60) > 0);
        Assert.False(r.Moving);
    }

    [Fact]
    public void RocketSplashHitsWhoeverStandsOnThePoint()
    {
        var w = Arena.New(32);
        var hunter = w.Spawn(1, "Tank hunter", 5, 16, Facing.East);
        var friend = w.Spawn(1, "Rifleman", 9, 16);  // ground fire hits friends too
        var truck = w.Spawn(1, "technical", 9, 17);  // own and empty, a cell from the point
        w.Order(1, CommandKind.AttackGround, [hunter], cell: new Cell(9, 16));
        var rockets = new HashSet<uint>();
        for (var i = 0; i < 40; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                rockets.Add(p.Id);
                Assert.Equal(new Cell(9, 16).Center(), p.Dest); // AT missiles have no scatter
            }
        }
        Assert.NotEmpty(rockets);
        Assert.True(friend.HP < friend.Def.MaxHp);
        Assert.Equal(truck.Def.MaxHp, truck.HP); // a cell away: outside the AT missile's splash
    }

    [Fact]
    public void LauncherSalvoGoesToThePointAndSpendsAmmo()
    {
        var w = Arena.New(32);
        var (l, _) = w.SpawnCrewed(1, "light_rocket_truck", 5, 16, Facing.East);
        w.Order(1, CommandKind.AttackGround, [l], cell: new Cell(9, 16));
        var rockets = new HashSet<uint>();
        for (var i = 0; i < 40; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                rockets.Add(p.Id);
            }
        }
        Assert.Equal(4, rockets.Count);
        Assert.Equal([3], l.Charges);
        w.Run(1000);
        Assert.Equal([0], l.Charges);
        Assert.Equal(OrderKind.Idle, l.Order.Kind); // dry: the order ends
    }

    [Fact]
    public void StopAndMoveEndIt()
    {
        var w = Arena.New(32);
        var a = w.Spawn(1, "Rifleman", 5, 16, Facing.East);
        var b = w.Spawn(1, "Rifleman", 5, 18, Facing.East);
        w.Order(1, CommandKind.AttackGround, [a, b], cell: new Cell(9, 17));
        w.Run(20);
        Assert.Equal(OrderKind.AttackGround, a.Order.Kind);
        w.Order(1, CommandKind.Stop, [a]);
        w.Order(1, CommandKind.Move, [b], cell: new Cell(2, 18));
        w.Run(1);
        Assert.Equal(OrderKind.Idle, a.Order.Kind);
        Assert.Equal(OrderKind.Move, b.Order.Kind);
        Assert.Equal(0, Shots(w, a, 100));
    }

    [Fact]
    public void AirOnlyWeaponsSkipTheGround()
    {
        var w = Arena.New(32);
        var aa = w.Spawn(1, "Drone hunter", 5, 16, Facing.East); // AA missile + short rifle
        aa.WeaponMode = Model.WeaponMode.Auto; // allowed its sidearm (it starts on its launcher only)
        w.Order(1, CommandKind.AttackGround, [aa], cell: new Cell(8, 16));
        w.Run(60);
        Assert.Equal(OrderKind.AttackGround, aa.Order.Kind);
        Assert.Equal([], w.Projectiles);
        Assert.True(Shots(w, aa, 60) > 0); // with the short rifle

        // A unit whose only weapon hits the air ignores the order.
        var w2 = Arena.New(32, edit: s => s with
        {
            Players = [.. s.Players.Select(p => p with { Presets = [.. p.Presets, new Model.Preset { Name = "AA only", Archetype = "anti_armor", Components = ["aa_missile", "booby_trap"] }] })],
        });
        var only = w2.Spawn(1, "AA only", 5, 16, Facing.East);
        w2.Order(1, CommandKind.AttackGround, [only], cell: new Cell(8, 16));
        w2.Run(5);
        Assert.Equal(OrderKind.Idle, only.Order.Kind);
    }

    [Fact]
    public void OffMapCellsAreIgnored()
    {
        var w = Arena.New(32);
        var r = w.Spawn(1, "Rifleman", 5, 16, Facing.East);
        w.Order(1, CommandKind.AttackGround, [r], cell: new Cell(40, 16));
        w.Run(5);
        Assert.Equal(OrderKind.Idle, r.Order.Kind);
    }
}
