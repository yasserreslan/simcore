using SimCore.FixedPoint;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// Attack-move, the Guard order and the Aggressive stance (the unit AI).
public class AttackMoveTests
{
    private static void SetStance(World w, Unit u, Stance s) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = s });

    private static long Cells(Vec a, Vec b) => (a - b).Len().Raw / Fixed.OneRaw;

    // A squad attack-moves through two enemies: it stops when they come in reach, kills them,
    // carries on and arrives (the order ends there).
    [Fact]
    public void GroupStopsKillsResumesArrives()
    {
        var w = Arena.New(64);
        var squad = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 8, 28 + i * 2, Facing.East)).ToList();
        foreach (var u in squad)
        {
            u.HP = 10_000;
        }
        var foes = new[] { w.Spawn(2, "Rifleman", 30, 30), w.Spawn(2, "Rifleman", 30, 33) };
        foreach (var f in foes)
        {
            f.Stance = Stance.HoldFire;
        }
        w.Order(1, CommandKind.AttackMove, squad, cell: new Cell(52, 31));
        w.Run(1);
        Assert.All(squad, u => Assert.Equal(OrderKind.AttackMove, u.Order.Kind));
        var stopped = false;
        for (var i = 0; i < 1500 && squad.Any(u => u.Order.Kind != OrderKind.Idle); i++)
        {
            w.Run(1);
            stopped |= foes.Any(f => f.Alive) && squad.Any(u => u.MoveOn && !u.Moving);
        }
        Assert.True(stopped, "they stopped to fight");
        Assert.All(foes, f => Assert.False(f.Alive));
        Assert.All(squad, u => Assert.Equal(OrderKind.Idle, u.Order.Kind));
        Assert.All(squad, u => Assert.True(Cells(u.Pos, new Cell(52, 31).Center()) <= 5, $"{u.Id} is {Cells(u.Pos, new Cell(52, 31).Center())} cells off"));
    }

    // A plain move walks through: infantry don't fire on the move.
    [Fact]
    public void PlainMoveWalksThrough()
    {
        var w = Arena.New(64);
        var u = w.Spawn(1, "Rifleman", 8, 30, Facing.East);
        var foe = w.Spawn(2, "Rifleman", 30, 33);
        foe.Stance = Stance.HoldFire;
        w.Order(1, CommandKind.Move, [u], cell: new Cell(52, 30));
        w.Run(600);
        Assert.Equal(foe.Def.MaxHp, foe.HP);
    }

    // A technical attack-moving past an enemy fires its turret on the move and never stops for it.
    [Fact]
    public void TurretFiresOnTheMove()
    {
        var w = Arena.New(64);
        var (tech, _) = w.SpawnCrewed(1, "technical", 6, 30, Facing.East);
        var foe = w.Spawn(2, "Rifleman", 30, 34);
        foe.Stance = Stance.HoldFire;
        foe.HP = 10_000;
        w.Order(1, CommandKind.AttackMove, [tech], cell: new Cell(58, 30));
        w.Run(1);
        var firedMoving = 0;
        var stoppedEarly = false;
        for (var i = 0; i < 600 && tech.Order.Kind == OrderKind.AttackMove; i++)
        {
            w.Run(1);
            if (tech.LastShot == w.Tick - 1 && tech.Moving)
            {
                firedMoving++;
            }
            stoppedEarly |= tech.MoveOn;
        }
        Assert.True(firedMoving > 0, "it fired on the move");
        Assert.False(stoppedEarly);
        Assert.True(foe.HP < 10_000);
        Assert.True(Cells(tech.Pos, new Cell(58, 30).Center()) <= 3);
    }

    // Fog: an attack target that slips out of sight turns the order into an attack-move to where it
    // was last seen.
    [Fact]
    public void LostTargetBecomesAttackMoveToItsLastSeenCell()
    {
        var w = Arena.New(64, edit: s => s with { Fog = true });
        var u = w.Spawn(1, "Rifleman", 20, 30);
        u.Stance = Stance.HoldFire; // it doesn't shoot it first
        var (bike, _) = w.SpawnCrewed(2, "dirt_bike", 26, 30);
        bike.Stance = Stance.HoldFire;
        w.Run(2);
        Assert.True(World.IsVisibleTo(bike, 1));
        w.Order(1, CommandKind.Attack, [u], target: bike.Id);
        w.Run(2);
        w.Order(2, CommandKind.Move, [bike], cell: new Cell(60, 50));
        Cell last = default;
        for (var i = 0; i < 200 && u.Order.Kind == OrderKind.Attack; i++)
        {
            if (World.IsVisibleTo(bike, 1))
            {
                last = Cell.Of(bike.Pos);
            }
            w.Run(1);
        }
        Assert.Equal(OrderKind.AttackMove, u.Order.Kind);
        Assert.Equal(0u, u.Order.Target);
        Assert.True(Math.Abs(u.Order.Cell.X - last.X) <= 1 && Math.Abs(u.Order.Cell.Y - last.Y) <= 1, $"{u.Order.Cell} vs {last}");
        w.Run(400);
        Assert.Equal(OrderKind.Idle, u.Order.Kind); // it got there and the order ended
    }

    // Guard a point: it walks there, engages an enemy that comes within the inner circle (vision x
    // 1.8), kills it and walks back.
    [Fact]
    public void GuardAPoint()
    {
        var w = Arena.New(64);
        var g = w.Spawn(1, "Rifleman", 10, 30);
        g.HP = 10_000;
        var post = new Cell(24, 30);
        w.Order(1, CommandKind.Guard, [g], cell: post);
        w.Run(300);
        Assert.Equal(OrderKind.Guard, g.Order.Kind);
        Assert.True(Cells(g.Pos, post.Center()) <= 1);
        var foe = w.Spawn(2, "Rifleman", 24, 21); // 9 cells off the post, seen by a spotter
        foe.Stance = Stance.HoldFire;
        w.Spawn(1, "worker", 24, 19).Stance = Stance.HoldFire;
        var engaged = false;
        for (var i = 0; i < 400 && foe.Alive; i++)
        {
            w.Run(1);
            engaged |= g.Ai == AiState.Engage && g.AiTarget == foe.Id;
        }
        Assert.True(engaged);
        Assert.False(foe.Alive);
        w.Run(400);
        Assert.Equal(AiState.None, g.Ai);
        Assert.Equal(OrderKind.Guard, g.Order.Kind); // it stays on guard
        Assert.True(Cells(g.Pos, post.Center()) <= 1);
    }

    // Guard a unit: the circle moves with it; once it falls, the guard holds its last cell.
    [Fact]
    public void GuardAUnitFollowsIt()
    {
        var w = Arena.New(64);
        var g = w.Spawn(1, "Rifleman", 10, 30);
        var (truck, _) = w.SpawnCrewed(1, "cargo_truck", 12, 30);
        w.Order(1, CommandKind.Guard, [g], target: truck.Id);
        w.Run(2);
        w.Order(1, CommandKind.Move, [truck], cell: new Cell(40, 30));
        w.Run(500);
        Assert.Equal(truck.Id, g.Order.Target);
        Assert.True(Cells(g.Pos, truck.Pos) <= 4, $"{Cells(g.Pos, truck.Pos)} cells behind");
        truck.HP = 0;
        w.Run(2);
        Assert.Equal(OrderKind.Guard, g.Order.Kind);
        Assert.Equal(0u, g.Order.Target);
    }

    // A guard chases on the leash from its post and gives up after the chase time.
    [Fact]
    public void GuardChaseIsLeashedToThePost()
    {
        var w = Arena.New(64);
        var g = w.Spawn(1, "Rifleman", 30, 30);
        g.HP = 10_000;
        w.Order(1, CommandKind.Guard, [g], cell: new Cell(30, 30));
        w.Run(5);
        var foe = w.Spawn(2, "Rifleman", 30, 37); // 7 cells: spotted, in the circle
        foe.HP = 10_000;
        foe.Stance = Stance.HoldFire;
        w.Run(30);
        Assert.Equal(AiState.Engage, g.Ai);
        w.Order(2, CommandKind.Move, [foe], cell: new Cell(30, 62)); // off past the leash (15.4 cells)
        var farthest = 0L;
        for (var i = 0; i < 900; i++)
        {
            w.Run(1);
            farthest = Math.Max(farthest, Cells(g.Pos, new Cell(30, 30).Center()));
        }
        Assert.True(farthest <= 16, $"went {farthest} cells from its post");
        Assert.Equal(AiState.None, g.Ai);
        Assert.True(Cells(g.Pos, new Cell(30, 30).Center()) <= 1);
    }

    // Aggressive: it goes after what its side spots out to vision x 1.33 (9.3 cells for a rifleman),
    // where Guard only fires in reach; then it walks back.
    [Theory]
    [InlineData(Stance.Aggressive, 9, true)]
    [InlineData(Stance.Aggressive, 10, false)]
    [InlineData(Stance.Guard, 6, false)]
    public void AggressiveAcquiresFarther(Stance stance, int distance, bool chases)
    {
        var w = Arena.New(64);
        var u = w.Spawn(1, "Rifleman", 30, 30);
        SetStance(w, u, stance);
        var foe = w.Spawn(2, "Rifleman", 30, 30 + distance);
        foe.Stance = Stance.HoldFire;
        w.Spawn(1, "worker", 31, 30 + distance + 2).Stance = Stance.HoldFire; // a spotter next to it
        var home = u.Pos;
        var engaged = false;
        for (var i = 0; i < 300; i++)
        {
            w.Run(1);
            engaged |= u.Ai == AiState.Engage;
        }
        Assert.Equal(chases, engaged);
        Assert.Equal(chases, !foe.Alive || foe.HP < foe.Def.MaxHp);
        if (chases)
        {
            w.Run(400);
            Assert.Equal(AiState.None, u.Ai);
            Assert.True(Cells(u.Pos, home) <= 1);
        }
        else
        {
            Assert.Equal(home, u.Pos);
        }
    }

    // A guarding or attack-moving unit's chase and orders replay the same with and without the
    // spatial index.
    [Fact]
    public void SameWorldWithAndWithoutTheSpatialIndex()
    {
        static World Build(bool full)
        {
            var w = Arena.New(64, seed: 3);
            w.FullScan = full;
            var squad = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "Rifleman", 8, 26 + i * 2, Facing.East)).ToList();
            w.Order(1, CommandKind.AttackMove, squad, cell: new Cell(52, 30));
            var g = w.Spawn(1, "Gunner", 40, 40);
            w.Order(1, CommandKind.Guard, [g], cell: new Cell(40, 40));
            for (var i = 0; i < 4; i++)
            {
                var e = w.Spawn(2, "Rifleman", 30 + i * 3, 28 + i * 3);
                e.Stance = i % 2 == 0 ? Stance.Aggressive : Stance.Guard;
            }
            return w;
        }
        var a = Build(false);
        var b = Build(true);
        for (var i = 0; i < 600; i++)
        {
            a.Step();
            b.Step();
            Assert.Equal(a.Hash(), b.Hash());
        }
    }
}
