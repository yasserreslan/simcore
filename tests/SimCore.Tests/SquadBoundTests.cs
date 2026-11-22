using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-5 part 2, scene 4's attack (battle rules 11.1, 11.3; simcore rows 1290 and 1292 to 1294): the bound that the choice
// stops, and the line an Advance leaves behind it.
public class SquadBoundTests(ITestOutputHelper output)
{
    private static World New(int size = 64, ulong seed = 1)
    {
        var w = Arena.New(size, seed);
        w.CombatOff = false;
        w.SureHits = false; // the rules' own hit rolls, as the bench's scene 4
        return w;
    }

    private static Squad Form(World w, params Unit[] units)
    {
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.FormSquad, Units = [.. units.Select(u => u.Id)] });
        w.Step();
        return w.SquadOf(units[0].Id)!;
    }

    // Scene 4 as the bench stages it (BattleBench.Bounding): blue's 6 rifles and an LMG attack-move 15 cells onto red's 4, each
    // behind a wall cell, in their squad's line (Assault's slots), on Hold ground.
    // Part 1's staging (file: red formed with no mode, walking into a file behind its leader, so only he stands at a wall) for the
    // fight in place that holds (there R stays under Advance's).
    private static (World W, Squad Blue, Unit[] B, Unit[] R) Scene(ulong seed = 1, bool file = false)
    {
        var w = New(64, seed);
        foreach (var (x, y) in file ? new[] { (34, 15), (34, 18), (35, 21), (34, 24) } : [(34, 15), (34, 17), (34, 19), (34, 21)])
        {
            w.Grid.Set(new Cell(x, y), Terrain.Blocked);
        }
        var rifles = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 18, 15 + i * 2, Facing.East)).ToArray();
        var blue = rifles.Append(w.Spawn(1, "Gunner", 17, 20, Facing.East)).ToArray();
        var red = (file ? new[] { 15, 18, 21, 24 } : [19, 17, 21, 15]).Select(y => w.Spawn(2, "Rifleman", 35, y, Facing.West)).ToArray();
        var s = Form(w, blue);
        Form(w, red);
        if (!file)
        {
            w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetSquadMode, Units = [.. red.Select(u => u.Id)], Index = (int)SquadMode.Assault });
        }
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldGround });
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AttackMove, Units = [blue[0].Id], Cell = new Cell(46, 20) });
        return (w, s, blue, red);
    }

    // Runs the scene to the first think at which the choice turns from Advance to Fight in place; null if it never does. With it,
    // the bound's goals at the tick before (its movers).
    private static (byte Moving, List<LinePlace> Movers)? ToTheStop(World w, Squad s, Unit[] red)
    {
        var was = SquadChoice.None;
        byte moving = 0;
        var movers = new List<LinePlace>();
        for (var i = 0; i < 1200 && red.Any(u => u.Alive); i++)
        {
            w.Step();
            if (s.Mind is not { } m)
            {
                continue;
            }
            if (was == SquadChoice.Advance && m.Choice == SquadChoice.FightInPlace)
            {
                return (moving, movers);
            }
            (was, moving) = (m.Choice, m.Moving);
            movers = [.. m.Line.Where(p => p.Bounding)];
        }
        return null;
    }

    // 11.3 step 6: "a choice that changes in mid-bound lets the movers finish to their goals". The bound runs on as a bound (its
    // element still the one moving, its goals kept: no rebuild sends a mover back into the open) until it is settled or
    // boundMaxTicks have passed. On 7ead82a the bound ended at the think the choice changed and the next rebuild gave the movers
    // new boxes short of their goals.
    [Fact]
    public void AChoiceChangedInMidBoundLetsTheBoundFinish()
    {
        var (w, s, _, red) = Scene();
        var stop = ToTheStop(w, s, red);
        Assert.NotNull(stop);
        var (before, movers) = stop.Value;
        var m = s.Mind!;
        output.WriteLine($"t{w.Tick}: Advance -> FightInPlace, element {before} bounding before, {m.Moving} now, movers {string.Join(" ", movers.Select(p => $"{p.Unit}:{p.Goal}"))}");
        Assert.NotEqual(0, before); // (the scene's stop comes in mid-bound)
        var maxTicks = (uint)w.Content.InfantryCombat![CombatValue.SqBoundMaxTicks];
        var from = w.Tick;
        while (m.Moving != 0 && w.Tick - from <= maxTicks + 20)
        {
            foreach (var p in m.Line.Where(p => p.Bounding))
            {
                Assert.Equal(movers.First(q => q.Unit == p.Unit).Goal, p.Goal); // no rebuild while it runs
            }
            w.Step();
        }
        output.WriteLine($"the bound ended t{w.Tick}, {w.Tick - from} ticks after the change");
        Assert.Equal(0, m.Moving);
        foreach (var p in movers)
        {
            if (w.Unit(p.Unit) is { Alive: true } u && w.Tick - from < maxTicks)
            {
                Assert.True((u.Pos - p.Goal.Center()).Len().Raw <= Fixed.OneRaw, $"{u.Id} at {u.Pos} short of his bound's goal {p.Goal}"); // finished, not stopped
            }
        }
    }

    // 11.1: "an Advance carries the line with it". When the choice turns from Advance to Fight in place (and no bound runs) the line
    // is rebuilt on the squad's front (the element that bounded last), so the men behind it, out of reach (the base of fire with the
    // LMG), come up into it: every slot is within a cell of the front's line along the advance. On 7ead82a the line was rebuilt
    // only once the squad's centre had moved 3 cells, and B kept its old boxes 4 to 8 cells back, out of reach.
    [Fact]
    public void AnAdvanceThatStopsRebuildsTheLineOnItsFront()
    {
        // The first seed whose Advance stops into a Fight in place that holds to the squad's next think after its bound (in seed 1
        // R climbs back and the bounds go on); on 7ead82a no seed rebuilds there (the centre moved under 3 cells).
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (w, s, blue, red) = Scene(seed, file: true);
            if (ToTheStop(w, s, red) is null)
            {
                continue;
            }
            var m = s.Mind!;
            for (var i = 0; i < 120 && m.Moving != 0; i++)
            {
                w.Step(); // (a bound in progress finishes first: the test above)
            }
            var old = m.Line.Select(p => p.Slot).ToList();
            var ended = w.Tick;
            for (var i = 0; i < 40 && m.Choice == SquadChoice.FightInPlace && m.Line.Select(p => p.Slot).SequenceEqual(old); i++)
            {
                w.Step(); // to the next squad think
            }
            if (m.Choice != SquadChoice.FightInPlace || m.Line.Select(p => p.Slot).SequenceEqual(old))
            {
                output.WriteLine($"seed {seed}: bound ended t{ended}, t{w.Tick} {m.Choice} moving {m.Moving} contact {m.InContact} flank {m.FlankStage} line {m.HasLine}");
                continue;
            }
            var front = blue.Where(u => u.Alive && u.Life is not { Wound: WoundState.Down }).Max(u => u.Pos.X.Raw) / Fixed.OneRaw;
            output.WriteLine($"seed {seed}: bound ended t{ended}, rebuilt t{w.Tick}: foremost man at x {front}, slots {string.Join(" ", m.Line.Select(p => $"{p.Unit}:{p.Slot}"))}, before {string.Join(" ", old)}");
            Assert.InRange(m.Line.Select(p => front - p.Slot.X).Max(), -1, 3); // the whole line on the front (within 3 cells of its foremost man)
            return;
        }
        Assert.Fail("no seed rebuilt the line when its Advance stopped");
    }
}
