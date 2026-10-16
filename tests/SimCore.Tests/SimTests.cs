using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

public class SimTests
{
    [Fact]
    public void GunnerKillsRiflemanInRange()
    {
        var w = Arena.New(16);
        var gunner = w.Spawn(1, "Gunner", 5, 5, Facing.East);
        var rifle = w.Spawn(2, "Rifleman", 8, 5, Facing.West);

        w.Order(1, CommandKind.Attack, [gunner], rifle.Id);
        w.Run(200);

        Assert.Null(w.Unit(rifle.Id)); // dead within 10 s
        Assert.True(gunner.HP < gunner.Def.MaxHp, "the gunner should have taken return fire");
    }

    [Fact]
    public void MoveRoutesAroundWall()
    {
        var w = Arena.New(10);
        for (var y = 0; y < 9; y++)
        {
            w.Grid.Set(new Cell(5, y), Terrain.Blocked); // the only gap is at y = 9
        }
        var u = w.Spawn(1, "Rifleman", 2, 2, Facing.East);
        var dest = new Cell(8, 2);
        w.Order(1, CommandKind.Move, [u], cell: dest);
        w.Step();

        var prev = new Cell(2, 2);
        foreach (var c in u.Path)
        {
            Assert.True(w.Grid.Passable(c), $"path enters blocked cell {c}");
            Assert.True(Math.Abs(c.X - prev.X) <= 1 && Math.Abs(c.Y - prev.Y) <= 1, $"path jumps from {prev} to {c}");
            prev = c;
        }
        Assert.Equal(dest, prev);

        w.Run(40 * World.TicksPerSecond);
        Assert.Equal(dest, Cell.Of(u.Pos));
        Assert.Equal(OrderKind.Idle, u.Order.Kind);
    }

    [Fact]
    public void GroupMoveSpreadsDestinations()
    {
        var w = Arena.New(20);
        var units = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "Rifleman", 2, 2 + i, Facing.East)).ToList();
        w.Order(1, CommandKind.Move, units, cell: new Cell(15, 10));
        w.Step();
        var cells = units.Select(u => u.Order.Cell).ToList();
        Assert.Equal(cells.Count, cells.Distinct().Count());
    }

    [Fact]
    public void CommandsForOtherPlayersUnitsAreIgnored()
    {
        var w = Arena.New(10);
        var enemy = w.Spawn(2, "Rifleman", 5, 5);
        w.Order(1, CommandKind.Move, [enemy], cell: new Cell(0, 0));
        w.Step();
        Assert.Equal(OrderKind.Idle, enemy.Order.Kind);
        Assert.False(enemy.Moving, "player 1 moved player 2's soldier");
    }

    [Fact]
    public void IssueRejectsLateCommands()
    {
        var w = Arena.New(4);
        w.Run(5);
        Assert.Throws<LateCommandException>(() => w.Issue(new Command { Tick = 3, Player = 1, Kind = CommandKind.Stop }));
        Assert.Throws<UnknownPlayerException>(() => w.Issue(new Command { Tick = 9, Player = 7, Kind = CommandKind.Stop }));
    }

    // CommandOrderIndependentOfArrival checks that peers receiving the same
    // commands in different orders still compute the same world.
    [Fact]
    public void CommandOrderIndependentOfArrival()
    {
        static World Build()
        {
            var w = Arena.New(16);
            w.Spawn(1, "Rifleman", 2, 2, Facing.East);
            w.Spawn(2, "Rifleman", 12, 12, Facing.West);
            return w;
        }
        Command[] cmds =
        [
            new() { Tick = 3, Player = 2, Seq = 1, Kind = CommandKind.Move, Units = [2], Cell = new Cell(4, 4) },
            new() { Tick = 3, Player = 1, Seq = 1, Kind = CommandKind.Move, Units = [1], Cell = new Cell(10, 2) },
            new() { Tick = 3, Player = 1, Seq = 2, Kind = CommandKind.Move, Units = [1], Cell = new Cell(2, 10) },
        ];
        World a = Build(), b = Build();
        foreach (var c in cmds)
        {
            a.Issue(c);
        }
        for (var i = cmds.Length - 1; i >= 0; i--)
        {
            b.Issue(cmds[i]);
        }
        a.Run(400);
        b.Run(400);
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(new Cell(2, 10), Cell.Of(a.Unit(1)!.Pos)); // the later command wins
    }

    [Fact]
    public void HashChangesWithState()
    {
        var w = Arena.New(8);
        var u = w.Spawn(1, "Rifleman", 1, 1);
        var h = w.Hash();
        Assert.Equal(h, w.Hash());
        u.HP--;
        Assert.NotEqual(h, w.Hash());
    }

    // The setup's digest is part of the world hash: two worlds that differ
    // only in a player's lobby choices desync on the first tick.
    [Fact]
    public void HashCoversSetup()
    {
        var a = Arena.New(8);
        var b = Arena.New(8, edit: s => s with
        {
            Players = [s.Players[0] with { Presets = [.. s.Players[0].Presets.Take(3)] }, s.Players[1]],
        });
        Assert.NotEqual(a.Hash(), b.Hash());
    }

    [Fact]
    public void SplashHitsEveryoneInRadius()
    {
        var w = Arena.New(8);
        var a = w.Spawn(1, "Rifleman", 1, 1);
        var b = w.Spawn(2, "Rifleman", 1, 1);
        var far = w.Spawn(2, "Rifleman", 5, 5);
        var explosive = w.Content.Warhead("explosive")!;
        w.Explode(new Cell(1, 1).Center(), 30, explosive, Fixed.One);

        var full = a.Def.MaxHp - 30;
        Assert.Equal(full, a.HP); // friends at the impact point take full splash damage too
        Assert.Equal(full, b.HP);
        Assert.Equal(far.Def.MaxHp, far.HP); // outside the radius
    }
}
