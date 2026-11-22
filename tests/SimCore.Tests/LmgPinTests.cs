using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// INF-E1b-5, the LMG's pin (battle rules 16.2, brief 8.2; scene 20d / scene 7).
public class LmgPinTests
{
    private static World New(int size = 40, ulong seed = 1)
    {
        var w = Arena.New(size, seed);
        w.CombatOff = false;
        return w;
    }

    private static void Stance(World w, Stance st, params Unit[] units) =>
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.SetStance, Units = [.. units.Select(u => u.Id)], Stance = st });

    // The scene's "LMG deployed": prone on its bipod, set up facing east (as the bench's scene 20d).
    private static Unit Deployed(World w, int x, int y)
    {
        var g = w.Spawn(1, "Gunner", x, y, Facing.East);
        var f = g.Fight!;
        (f.Posture, f.PostureTo) = (Posture.Prone, Posture.Prone);
        (f.Deployed, f.DeployDir, f.Forced) = (true, 0, Posture.Prone);
        return g;
    }

    // Brief 8.2: deployed, the LMG's suppression bonus is +50%: a near miss of his adds 7 x 150% = 10, undeployed 7.
    [Fact]
    public void ADeployedLmgsShotSuppressesHalfAgain()
    {
        foreach (var deployed in new[] { true, false })
        {
            var w = New();
            var g = deployed ? Deployed(w, 10, 10) : w.Spawn(1, "Gunner", 10, 10, Facing.East);
            var red = w.Spawn(2, "Rifleman", 15, 10, Facing.West);
            Stance(w, Sim.Stance.HoldFire, g, red);
            w.Step();
            var before = red.Fight!.Suppression;
            w.NoteSuppressingShot(g, g.Def.Weapons[0], red, false, red.Pos);
            Assert.Equal(deployed ? 10 : 7, red.Fight.Suppression - before);
        }
    }

    // 16.2: he takes the next target along the line after each burst, and keeps his target through a burst: inside a burst every
    // shot goes at the man the burst began on while that man stands (on abc58c1 he switched mid-burst to whoever scored best
    // as his target ducked, spreading one burst over the line).
    [Fact]
    public void TheLmgKeepsHisTargetThroughABurstAndMovesAlongTheLineAfterIt()
    {
        var w = New();
        var g = Deployed(w, 10, 12);
        Stance(w, Sim.Stance.HoldGround, g);
        var red = Enumerable.Range(0, 5).Select(i => w.Spawn(2, "Rifleman", 15, 10 + i, Facing.West)).ToArray();
        Stance(w, Sim.Stance.HoldFire, red);
        uint last = 0;
        var inBurst = false;
        int switchedMid = 0, bursts = 0, shots = 0;
        var targets = new List<uint>();
        for (var t = 0; t < 200; t++)
        {
            w.Step();
            foreach (var e in w.ShotEvents.Where(e => e.Shooter == g.Id))
            {
                shots++;
                var lastUp = w.Unit(last) is { Alive: true } lu && lu.Life is not { Wound: WoundState.Down };
                switchedMid += inBurst && lastUp && e.Target != last ? 1 : 0;
                bursts += !inBurst ? 1 : 0;
                targets.Add(targets.Contains(e.Target) ? 0 : e.Target);
                last = e.Target;
            }
            inBurst = g.Fight!.BurstLeft > 0 && last != 0;
        }
        Assert.True(shots >= 16 && bursts >= 2, $"shots {shots} bursts {bursts}");
        Assert.Equal(0, switchedMid);
        Assert.True(targets.Count(x => x != 0) >= 2, $"targets {targets.Count(x => x != 0)}"); // along the line after a burst
    }
}
