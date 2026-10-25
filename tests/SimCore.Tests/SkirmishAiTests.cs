using SimCore.Bots;
using SimCore.FixedPoint;
using SimCore.Rng;
using Xunit.Abstractions;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// The skirmish AI with the unit AI: an Aggressive army on guard posts round its base, waves that
// attack-move, air defence on Guard.
public class SkirmishAiTests(ITestOutputHelper output)
{
    private static (World W, SkirmishAi Ai, List<Unit> Army, Unit Aa) Game()
    {
        var w = Arena.New(96);
        w.Spawn(2, "supply_depot", 70, 70);
        var army = Enumerable.Range(0, 5).Select(i => w.Spawn(2, "Rifleman", 72 + i, 78)).ToList();
        var aa = w.Spawn(2, "manpads", 70, 79);
        w.Spawn(1, "Rifleman", 10, 10);
        return (w, new SkirmishAi(2) { WaveEvery = 200 }, army, aa);
    }

    private static List<Command> Play(World w, SkirmishAi ai, int ticks)
    {
        var all = new List<Command>();
        for (var t = 0; t < ticks; t++)
        {
            foreach (var c in ai.Think(w))
            {
                all.Add(c);
                w.Issue(c);
            }
            w.Step();
        }
        return all;
    }

    [Fact]
    public void ItsArmyIsAggressiveOnGuardPostsAndAirDefenceStaysOnGuard()
    {
        var (w, ai, army, aa) = Game();
        Play(w, ai, 60);
        Assert.All(army, u => Assert.Equal(Stance.Aggressive, u.Stance));
        Assert.All(army, u => Assert.Equal(OrderKind.Guard, u.Order.Kind));
        Assert.Equal(Stance.Guard, aa.Stance);
    }

    [Fact]
    public void WavesAttackMove()
    {
        var (w, ai, army, _) = Game();
        var cmds = Play(w, ai, 260);
        var wave = Assert.Single(cmds, c => c.Kind == CommandKind.AttackMove && c.Units.Count == army.Count);
        Assert.All(army, u => Assert.Contains(u.Id, wave.Units));
        Assert.DoesNotContain(cmds, c => c.Kind == CommandKind.Attack); // nothing spotted: no target to order
        Assert.All(army, u => Assert.Equal(OrderKind.AttackMove, u.Order.Kind));
    }

    // The same game plays the same twice (the AI and the unit AI are deterministic).
    [Fact]
    public void Deterministic()
    {
        var (a, aiA, _, _) = Game();
        var (b, aiB, _, _) = Game();
        var ca = Play(a, aiA, 600);
        var cb = Play(b, aiB, 600);
        Assert.Equal(ca.Count, cb.Count);
        Assert.Equal(a.Hash(), b.Hash());
    }
    private static (World W, SkirmishAi Ai, List<Unit> Structures) Turtle(int seed, int x, int y)
    {
        var w = Arena.New(128, (ulong)seed, s => s with { Fog = true });
        w.Spawn(1, "supply_depot", 100, 100);
        var r = new Rand((ulong)seed);
        for (var i = 0; i < 12; i++) { w.Spawn(1, "Rifleman", 96 + r.Intn(6), 108 + r.Intn(5)); }
        var structures = new List<Unit>
        {
            w.Spawn(2, "supply_depot", x, y),
            w.Spawn(2, "supply_depot", x + 8, y),
            w.Spawn(2, "supply_depot", x, y + 8),
        };
        w.Spawn(2, "Rifleman", x + (x < 64 ? -3 : 10), y + (y < 64 ? -3 : 10));
        foreach (var u in w.Units) { u.Stance = Stance.HoldFire; }
        return (w, new SkirmishAi(1), structures);
    }

    [Theory]
    [InlineData(1, 8, 108)]
    [InlineData(2, 8, 108)]
    [InlineData(3, 8, 108)]
    [InlineData(4, 8, 108)]
    [InlineData(1, 108, 8)]
    [InlineData(2, 108, 8)]
    [InlineData(3, 108, 8)]
    [InlineData(4, 108, 8)]
    public void TurtleFindsAndAttacksNonMirroredBase(int seed, int x, int y)
    {
        var (w, ai, structures) = Turtle(seed, x, y);
        var hp = structures.Select(u => u.HP).ToArray();
        uint? first = null;
        const int limit = 12 * 60 * World.TicksPerSecond;
        for (var t = 0; t < limit; t++)
        {
            foreach (var c in ai.Think(w)) { w.Issue(c); }
            w.Step();
            if (structures.Where((u, i) => u.HP < hp[i]).Any()) { first = w.Tick; break; }
        }
        output.WriteLine("SCOUT_RESULT " + System.Text.Json.JsonSerializer.Serialize(new { seed, x, y, firstAttackTick = first, limitTicks = limit }));
        Assert.True(first is not null, $"seed {seed}, corner {x},{y}: no structure attack in twelve minutes");
    }

    private static (World W, SkirmishAi Ai, Unit Scout, Unit Depot) MemoryGame()
    {
        var w = Arena.New(128, edit: s => s with { Fog = true });
        w.Spawn(1, "supply_depot", 100, 100);
        w.Spawn(1, "Rifleman", 108, 108);
        var scout = w.Spawn(1, "Rifleman", 24, 28);
        var depot = w.Spawn(2, "supply_depot", 20, 20);
        w.Spawn(2, "Rifleman", 5, 5);
        foreach (var u in w.Units) { u.Stance = Stance.HoldFire; }
        w.Run(20);
        return (w, new SkirmishAi(1) { Every = 1, WaveEvery = 20 }, scout, depot);
    }

    [Fact]
    public void WaveTargetsNearestRememberedStructureAfterLosingSight()
    {
        var (w, ai, scout, depot) = MemoryGame();
        var nearerHome = w.Spawn(2, "supply_depot", 26, 20);
        w.Run(20);
        ai.Think(w);
        Assert.True(World.IsVisibleTo(depot, 1));
        Assert.True(World.IsVisibleTo(nearerHome, 1));
        Assert.Equal(new[] { depot.Id, nearerHome.Id }, ai.MemoryForTests.Select(m => m.Id));
        var seen = ai.MemoryForTests[1];
        Assert.Equal(nearerHome.Def.Archetype.Index, seen.Archetype);
        scout.Pos = new Cell(108, 110).Center();
        w.Run(20);
        Assert.False(World.IsVisibleTo(nearerHome, 1));
        var commands = ai.Think(w);
        Assert.Contains(commands, c => c.Kind == CommandKind.AttackMove && c.Cell == Cell.Of(nearerHome.Pos));
        Assert.Equal(seen.LastSeen, ai.MemoryForTests[1].LastSeen);
    }

    [Fact]
    public void HiddenDestructionIsRememberedUntilOldCellIsSeenEmpty()
    {
        var (w, ai, scout, depot) = MemoryGame();
        ai.Think(w);
        Assert.Single(ai.MemoryForTests);
        var cell = Cell.Of(depot.Pos);
        scout.Pos = new Cell(108, 110).Center();
        w.Run(20);
        depot.HP = 0;
        w.Run(20);
        ai.Think(w);
        Assert.Single(ai.MemoryForTests); // hidden death does not leak through World.Unit(id)
        scout.Pos = cell.Center();
        w.Run(20);
        Assert.True(w.FogOf(1)!.Visible(cell.X, cell.Y));
        ai.Think(w);
        Assert.Empty(ai.MemoryForTests);
    }

    [Fact]
    public void CommandsOnlyTargetSeenEnemiesForTenMinutes()
    {
        var (w, ai, _) = Turtle(3, 8, 108);
        var seen = new List<uint>();
        var commands = 0;
        for (var t = 0; t < 12000; t++)
        {
            foreach (var e in w.Units.Where(u => u.Owner == 2 && World.IsVisibleTo(u, 1)))
            {
                if (!seen.Contains(e.Id)) { seen.Add(e.Id); }
            }
            foreach (var c in ai.Think(w))
            {
                commands++;
                if (c.Target != 0 && w.Unit(c.Target) is { Owner: 2 } enemy) { Assert.True(World.IsVisibleTo(enemy, 1)); }
                if (c.Kind is CommandKind.AttackMove or CommandKind.AttackGround)
                {
                    foreach (var enemyStructure in w.Units.Where(u => u.Owner == 2 && u.Def.IsStructure && Cell.Of(u.Pos) == c.Cell))
                    {
                        Assert.Contains(enemyStructure.Id, seen);
                    }
                }
                w.Issue(c);
            }
            w.Step();
        }
        Assert.True(commands > 10);
    }

    [Fact]
    public void ScoutingCommandsAndHashesAreDeterministicForTenMinutes()
    {
        var a = Turtle(2, 108, 8);
        var b = Turtle(2, 108, 8);
        for (var t = 0; t < 12000; t++)
        {
            var ca = a.Ai.Think(a.W);
            var cb = b.Ai.Think(b.W);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(ca), System.Text.Json.JsonSerializer.Serialize(cb));
            foreach (var c in ca) { a.W.Issue(c); }
            foreach (var c in cb) { b.W.Issue(c); }
            a.W.Step(); b.W.Step();
            Assert.Equal(a.W.Hash(), b.W.Hash());
        }
    }

    [Fact]
    public void ScoutProbesStayInReachableRegion()
    {
        var (w, ai, _) = Turtle(1, 8, 108);
        for (var y = 0; y < 128; y++) { w.Grid.Set(new Cell(64, y), Terrain.Blocked); }
        var commands = Play(w, ai, 3000);
        var moves = commands.Where(c => c.Kind == CommandKind.AttackMove).ToList();
        Assert.NotEmpty(moves);
        Assert.All(moves, c => Assert.True(c.Cell.X > 64, $"unreachable probe {c.Cell}"));
    }

}
