using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-4 part 3: scene 1's pace (battle rules 10.3's "closed", 11.8's leader pause, 8.1, 8.4).
public class SquadPaceTests(ITestOutputHelper output)
{
    private static World New(int size = 48, ulong seed = 1)
    {
        var w = Arena.New(size, seed);
        w.CombatOff = false;
        return w;
    }

    private static Squad Form(World w, params Unit[] units)
    {
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.FormSquad, Units = [.. units.Select(u => u.Id)] });
        w.Step();
        return w.SquadOf(units[0].Id)!;
    }

    private static double MeanX(IEnumerable<Unit> men) =>
        men.Where(u => u.Alive && u.HP > 0).Select(u => u.Pos.X.Raw / (double)Fixed.OneRaw).DefaultIfEmpty(0).Average();

    // 11.8: in the leader's pause the squad keeps its choice and starts no bound, but "members fight on their own": a member of an
    // Attack-move with no target in his reach keeps his order (8.1) and walks on toward the enemy; he doesn't wait 10 s out of range.
    [Fact]
    public void InTheLeadersPauseMenOutOfReachWalkOnTheirOrder()
    {
        var w = New();
        var blue = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "Rifleman", 8, 16 + i * 2, Facing.East)).ToArray();
        var red = Enumerable.Range(0, 4).Select(i => w.Spawn(2, "Rifleman", 22, 17 + i * 2, Facing.West)).ToArray();
        var bs = Form(w, blue);
        Form(w, red);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldGround });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AttackMove, Units = [blue[0].Id], Cell = new Cell(40, 20) });
        var m = bs.Mind!;
        for (var i = 0; i < 400 && !m.InContact; i++)
        {
            w.Step();
        }
        Assert.True(m.InContact, "blue in contact");
        blue.First(u => u.Id == bs.Leader).HP = 0; // the leader killed: 11.8's pause
        w.Step();
        Assert.True(m.LeaderPauseUntil > w.Tick + 100, "the pause");
        for (var i = 0; i < 120 && m.Moving != 0; i++)
        {
            w.Step(); // a bound under way finishes (11.3)
        }
        var men = blue.Where(u => u.HP > 0).ToList();
        var x0 = MeanX(men);
        var reach0 = men.Count(u => u.AutoTarget != 0);
        for (var i = 0; i < 60 && w.Tick < m.LeaderPauseUntil; i++)
        {
            w.Step();
        }
        var x1 = MeanX(men);
        output.WriteLine($"pause to {m.LeaderPauseUntil}, tick {w.Tick}: mean x {x0:0.00} -> {x1:0.00}, in reach {reach0} -> {men.Count(u => u.AutoTarget != 0)}, choice {m.Choice}");
        Assert.True(reach0 < men.Count, "someone out of reach when the bound ended");
        Assert.True(x1 - x0 >= 0.8, $"the men out of reach walk on in the pause ({x0:0.00} -> {x1:0.00})");
    }

    // 10.3: "closed" is the squad's line within advanceToPercent of the enemy group (8.4: "both lines kneeling at 3.5 cells"), not
    // one man of it: a squad with one man pushed ahead goes on closing with its line.
    [Fact]
    public void OneManAheadDoesNotCloseTheLine()
    {
        var w = New();
        var line = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "Rifleman", 10, 16 + i * 2, Facing.East)).ToList();
        line.Add(w.Spawn(1, "Rifleman", 15, 20, Facing.East)); // 3 cells from red's middle man
        var red = Enumerable.Range(0, 3).Select(i => w.Spawn(2, "Rifleman", 18, 18 + i * 2, Facing.West)).ToArray();
        var bs = Form(w, [.. line]);
        Form(w, red);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldGround });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AttackMove, Units = [line[0].Id], Cell = new Cell(40, 20) });
        var m = bs.Mind!;
        var x0 = MeanX(line.Take(5));
        int advance = 0, fip = 0;
        var last = SquadChoice.None;
        for (var i = 0; i < 160; i++)
        {
            w.Step();
            if (m.Choice != last)
            {
                output.WriteLine($"t{w.Tick} {m.Choice} R {m.Ratio} line x {MeanX(line.Take(5)):0.00}");
                last = m.Choice;
            }
            advance += m.InContact && m.Choice == SquadChoice.Advance ? 1 : 0;
            fip += m.InContact && m.Choice == SquadChoice.FightInPlace ? 1 : 0;
        }
        var x1 = MeanX(line.Take(5));
        output.WriteLine($"advance {advance} fight in place {fip} ticks; the line's mean x {x0:0.00} -> {x1:0.00}; R {m.Ratio}");
        Assert.True(x1 - x0 >= 2.5, $"the line closes ({x0:0.00} -> {x1:0.00})");
    }
}
