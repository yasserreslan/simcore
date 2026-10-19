using SimCore.Bots;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// The skirmish AI with the unit AI: an Aggressive army on guard posts round its base, waves that
// attack-move, air defence on Guard.
public class SkirmishAiTests
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
}
