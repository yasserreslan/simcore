using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

public class SimTests
{
    private static World NewTestWorld(int w, int h) => new(new Grid(w, h), 1, 2);

    private static void Run(World w, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
        }
    }

    [Fact]
    public void TankKillsRiflemanInRange()
    {
        var w = NewTestWorld(16, 16);
        var tank = w.Spawn(UnitTypeId.BattleTank, 1, new Cell(5, 5), Facing.East);
        var rifle = w.Spawn(UnitTypeId.Rifleman, 2, new Cell(8, 5), Facing.West);

        w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.Attack, Units = [tank], Target = rifle });
        Run(w, 200);

        Assert.Null(w.Unit(rifle)); // rifleman dead within 10 s
        var u = w.Unit(tank);
        Assert.NotNull(u);
        Assert.True(u.HP < u.Def.MaxHP, "tank should have taken return fire");
    }

    [Fact]
    public void MoveRoutesAroundWall()
    {
        var w = NewTestWorld(10, 10);
        for (var y = 0; y < 9; y++)
        {
            w.Grid.Set(new Cell(5, y), Terrain.Blocked); // the only gap is at y = 9
        }
        var id = w.Spawn(UnitTypeId.BattleTank, 1, new Cell(2, 2), Facing.East);
        var dest = new Cell(8, 2);
        w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.Move, Units = [id], Cell = dest });
        w.Step();

        var u = w.Unit(id)!;
        var prev = new Cell(2, 2);
        foreach (var c in u.Path)
        {
            Assert.True(w.Grid.Passable(c), $"path enters blocked cell {c}");
            Assert.True(Math.Abs(c.X - prev.X) <= 1 && Math.Abs(c.Y - prev.Y) <= 1, $"path jumps from {prev} to {c}");
            prev = c;
        }
        Assert.Equal(dest, prev);

        Run(w, 40 * World.TicksPerSecond);
        Assert.Equal(dest, Cell.Of(u.Pos));
        Assert.Equal(OrderKind.Idle, u.Order.Kind);
    }

    [Fact]
    public void GroupMoveSpreadsDestinations()
    {
        var w = NewTestWorld(20, 20);
        var ids = new List<uint>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(w.Spawn(UnitTypeId.Rifleman, 1, new Cell(2, 2 + i), Facing.East));
        }
        w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.Move, Units = ids, Cell = new Cell(15, 10) });
        w.Step();
        var cells = ids.Select(id => w.Unit(id)!.Order.Cell).ToList();
        Assert.Equal(cells.Count, cells.Distinct().Count());
    }

    [Fact]
    public void CommandsForOtherPlayersUnitsAreIgnored()
    {
        var w = NewTestWorld(10, 10);
        var enemy = w.Spawn(UnitTypeId.BattleTank, 2, new Cell(5, 5), Facing.West);
        w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.Move, Units = [enemy], Cell = new Cell(0, 0) });
        w.Step();
        var u = w.Unit(enemy)!;
        Assert.Equal(OrderKind.Idle, u.Order.Kind);
        Assert.False(u.Moving, "player 1 moved player 2's tank");
    }

    [Fact]
    public void IssueRejectsLateCommands()
    {
        var w = NewTestWorld(4, 4);
        Run(w, 5);
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
            var w = NewTestWorld(16, 16);
            w.Spawn(UnitTypeId.BattleTank, 1, new Cell(2, 2), Facing.East);
            w.Spawn(UnitTypeId.BattleTank, 2, new Cell(12, 12), Facing.West);
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
        Run(a, 200);
        Run(b, 200);
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(new Cell(2, 10), Cell.Of(a.Unit(1)!.Pos)); // the later command wins
    }

    [Fact]
    public void HashChangesWithState()
    {
        var w = NewTestWorld(8, 8);
        var id = w.Spawn(UnitTypeId.Rifleman, 1, new Cell(1, 1), 0);
        var h = w.Hash();
        Assert.Equal(h, w.Hash());
        w.Unit(id)!.HP--;
        Assert.NotEqual(h, w.Hash());
    }

    [Fact]
    public void SplashHitsEveryoneInRadius()
    {
        var w = NewTestWorld(4, 4);
        var a = w.Spawn(UnitTypeId.Rifleman, 1, new Cell(1, 1), 0);
        var b = w.Spawn(UnitTypeId.Rifleman, 2, new Cell(1, 1), 0);
        var far = w.Spawn(UnitTypeId.Rifleman, 2, new Cell(3, 3), 0);
        w.ApplyHits([new Hit(UnitTypes.Shell, b, new Cell(1, 1).Center())]);

        var maxHP = UnitTypes.Of(UnitTypeId.Rifleman).MaxHP;
        var full = maxHP - UnitTypes.Damage(UnitTypes.Shell, Armor.None, 100);
        Assert.Equal(full, w.Unit(a)!.HP); // units at the impact point take full splash damage
        Assert.Equal(full, w.Unit(b)!.HP);
        Assert.Equal(maxHP, w.Unit(far)!.HP); // outside the radius
    }
}
