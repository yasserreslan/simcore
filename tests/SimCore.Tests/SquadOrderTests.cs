using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Slots by place on a new order (docs/design.md, "Squads: slots by place", SQ-ORDER; the owner's note of 2026-10-28): a member
// whose role slot is at the rear but who stands at the front never walks back against the move, and the squad never waits for
// him, so a squad reaches its goal in the time its anchor needs to walk there at the squad's speed.
public class SquadOrderTests(ITestOutputHelper output)
{
    private static void Cmd(World w, CommandKind kind, IEnumerable<Unit> units, Cell cell = default, int index = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = kind, Units = [.. units.Select(u => u.Id)], Cell = cell, Index = index });

    // Row 48 of the open 128-cell arena: the squad's route is the straight row (on row 60 the router's route jogs down to row 63
    // and back, 2.4 cells longer, which is not what these tests measure).
    private static readonly Cell Goal = new(100, Row);
    private const int Row = 48;

    // A squad facing east round its leader at (30, 48): two riflemen and the tank hunter where the mode puts them, and the mortar
    // (the support: the rear of the file, 7 cells behind the line) 3 cells ahead of the leader, as a squad just formed or loose.
    private static (World W, Squad S, List<Unit> Men, Unit Mortar) Setup(SquadMode mode, ulong seed = 1)
    {
        var w = Arena.New(128, seed);
        var lead = w.Spawn(1, "Rifleman", 30, Row, Facing.East);
        var (a, b, c) = mode == SquadMode.Assault ? ((30, Row + 2), (30, Row - 2), (30, Row + 4)) : ((29, Row), (27, Row), (26, Row));
        var r1 = w.Spawn(1, "Rifleman", a.Item1, a.Item2, Facing.East);
        var r2 = w.Spawn(1, "Rifleman", b.Item1, b.Item2, Facing.East);
        var rpg = w.Spawn(1, "Tank hunter", c.Item1, c.Item2, Facing.East);
        var mortar = w.Spawn(1, "mortar", 33, Row, Facing.East);
        List<Unit> men = [lead, r1, r2, rpg, mortar];
        Cmd(w, CommandKind.FormSquad, men);
        if (mode != SquadMode.March)
        {
            Cmd(w, CommandKind.SetSquadMode, [lead], index: (int)mode);
        }
        w.Run(1);
        var s = w.SquadOf(lead.Id)!;
        Assert.Equal(lead.Id, s.Leader);
        Assert.Equal(mode, s.Mode);
        return (w, s, men, mortar);
    }

    // What a march measured: the ticks until the squad arrived; the ticks its anchor needs from where it stood to the goal at the
    // squad's speed (the slowest member's, times the mode's percent); the ticks before the last in which the anchor did not walk a
    // full step (it waited); and per member his steps along the move (x, raw) and his wished steps (the steering's Desired x).
    private sealed record Run(int Took, int Ideal, int Stalls, Dictionary<uint, List<long>> Steps, Dictionary<uint, List<long>> Wished);

    private static Run March(World w, Squad s, List<Unit> men, CommandKind kind = CommandKind.Move)
    {
        var pct = s.Mode == SquadMode.Assault ? w.Content.Squad!.AssaultSpeedPercent : w.Content.Squad!.MarchSpeedPercent;
        var speed = men.Min(u => u.Def.Speed.Raw) * pct / 100;
        var dist = (Goal.Center() - s.Anchor).Len().Raw;
        var ideal = (int)((dist + speed - 1) / speed);
        var steps = men.ToDictionary(u => u.Id, _ => new List<long>());
        var wished = men.ToDictionary(u => u.Id, _ => new List<long>());
        var stalls = 0;
        Cmd(w, kind, [men[0]], Goal);
        for (var t = 1; t <= 4000; t++)
        {
            var before = men.Select(u => u.Pos).ToList();
            var anchor = s.Anchor;
            w.Run(1);
            Assert.Equal(w.Hash(), w.HashReference());
            if (w.SquadEvents.Any(e => e.Squad == s.Id && e.Kind == SquadEventKind.Arrived))
            {
                return new Run(t, ideal, stalls, steps, wished); // (from this tick it stands at rest: the role order comes back)
            }
            for (var i = 0; i < men.Count; i++)
            {
                steps[men[i].Id].Add(men[i].Pos.X.Raw - before[i].X.Raw);
                wished[men[i].Id].Add(men[i].Intent.Desired.X.Raw);
            }
            if ((s.Anchor - anchor).Len().Raw < speed - 1)
            {
                stalls++;
            }
        }
        throw new InvalidOperationException("the squad never arrived");
    }

    // Asserts nobody walked back: no member ever wished a step against the move (his first step included), and none was moved
    // back more than 1/16 cell in a tick (a nudge from a neighbour stepping aside; a walking step is 52 to 66 raw).
    private void NobodyWalkedBack(Run run, List<Unit> men)
    {
        foreach (var u in men)
        {
            var first = run.Wished[u.Id].FirstOrDefault(d => d != 0);
            var worstWish = run.Wished[u.Id].Min();
            var worst = run.Steps[u.Id].Min();
            output.WriteLine($"  #{u.Id} {u.Def.Archetype.Id}: first wished step {first}, worst wished {worstWish}, worst step {worst} raw along the move");
            Assert.True(worstWish >= 0, $"{u.Def.Archetype.Id} #{u.Id} wished a step back ({worstWish} raw)");
            Assert.True(worst > -Fixed.OneRaw / 16, $"{u.Def.Archetype.Id} #{u.Id} went back ({worst} raw in a tick)");
        }
    }

    // The owner's test: the rear-role member (the mortar) at the front, the squad ordered forward (east). Nobody's first step
    // goes against the move (nor any later one), the anchor never waits, and the time to the goal is the anchor's straight walk
    // at the squad's speed (the slowest member's, the mortar's; Assault 80% of it), within a tick.
    [Theory]
    [InlineData(SquadMode.March)]
    [InlineData(SquadMode.Assault)]
    public void TheRearRoleAtTheFrontNeverWalksBack(SquadMode mode)
    {
        var (w, s, men, mortar) = Setup(mode);
        var run = March(w, s, men);
        output.WriteLine($"{mode}: arrived after {run.Took} ticks, the anchor's straight walk {run.Ideal}, {run.Stalls} ticks waited");
        NobodyWalkedBack(run, men);
        Assert.Equal(0, run.Stalls);
        Assert.InRange(run.Took, run.Ideal - 1, run.Ideal + 1);
        // Arrived: everyone on his slot by role within a few seconds (role order at rest), the mortar at the rear again.
        w.Run(300);
        foreach (var u in men)
        {
            Assert.True(World.Within(u.Pos, w.SquadSlotPoint(u.Id)!.Value, Fixed.One), $"{u.Def.Archetype.Id} off his slot at rest");
        }
        Assert.True(mortar.Pos.X < w.Unit(s.Leader)!.Pos.X, "the mortar back at the rear at rest");
    }

    // On the move the slots go by place: the mortar stood ahead of everyone, but the support may not lead, so the leader (the
    // foremost who may) keeps the head and the mortar walks right behind him until the squad arrives; at rest the role order
    // comes back (TheRearRoleAtTheFrontNeverWalksBack).
    [Fact]
    public void MembersAheadTakeTheForwardSlots()
    {
        var (w, s, men, mortar) = Setup(SquadMode.March);
        Cmd(w, CommandKind.Move, [men[0]], Goal);
        w.Run(200);
        Assert.Equal(SquadState.Moving, s.State);
        var lead = w.Unit(s.Leader)!;
        var file = men.OrderByDescending(u => w.SquadSlotPoint(u.Id)!.Value.X.Raw).ToList();
        Assert.Equal(lead, file[0]);
        Assert.Equal(mortar, file[1]); // the first slot behind the head
        Assert.Equal(men[3], file[^1]); // the tank hunter stood last: the last slot
        Assert.True(World.Within(mortar.Pos, w.SquadSlotPoint(mortar.Id)!.Value, Fixed.One), "the mortar on his slot");
    }

    // A medic never takes a slot in the Assault line (level with the anchor), even standing ahead of everyone.
    [Fact]
    public void TheMedicStaysOutOfTheLine()
    {
        var w = Arena.NewSentinel(128);
        var lead = w.Spawn(1, "Rifleman", 30, Row, Facing.East);
        var r1 = w.Spawn(1, "Rifleman", 30, Row + 2, Facing.East);
        var r2 = w.Spawn(1, "Rifleman", 26, Row - 2, Facing.East);
        var medic = w.Spawn(1, "sentinel_medic", 34, Row, Facing.East);
        List<Unit> men = [lead, r1, r2, medic];
        Cmd(w, CommandKind.FormSquad, men);
        Cmd(w, CommandKind.SetSquadMode, [lead], index: (int)SquadMode.Assault);
        w.Run(1);
        var s = w.SquadOf(lead.Id)!;
        Cmd(w, CommandKind.Move, [lead], Goal);
        w.Run(2);
        long Back(Unit u)
        {
            var d = w.SquadSlotPoint(u.Id)!.Value - s.Anchor;
            return -((d.X.Raw * s.Facing.X.Raw + d.Y.Raw * s.Facing.Y.Raw) >> Fixed.Shift);
        }
        Assert.True(Back(medic) > Fixed.OneRaw * 2, $"the medic's slot {Back(medic)} raw behind the line");
        Assert.InRange(Back(r2), -Fixed.OneRaw / 4, Fixed.OneRaw / 4); // the rifleman 4 cells back takes the line
    }

    // A reversed order (the file walking west, sent east): every member stands ahead of the leader. The rear man takes the head
    // and the anchor starts where he stands (each member is already on his slot): nobody walks back, nobody is shoved, the anchor
    // never waits, and the squad arrives sooner than its anchor's walk from the leader would take.
    [Fact]
    public void AnAboutTurnNeverWalksBack()
    {
        var (w, s, men, _) = Setup(SquadMode.March);
        Cmd(w, CommandKind.Move, [men[0]], new Cell(10, Row));
        for (var t = 0; t < 1000 && !w.SquadEvents.Any(e => e.Kind == SquadEventKind.Arrived); t++)
        {
            w.Run(1);
        }
        w.Run(300); // formed up at rest, the file pointing east behind its leader facing west
        var lead = w.Unit(s.Leader)!;
        Assert.True(men.Where(u => u != lead).All(u => u.Pos.X > lead.Pos.X), "the file behind the leader, to the east");
        var tail = men.MaxBy(u => u.Pos.X)!;
        var run = March(w, s, men);
        output.WriteLine($"about turn: arrived after {run.Took} ticks, the anchor's walk from the leader {run.Ideal}, {run.Stalls} ticks waited");
        NobodyWalkedBack(run, men);
        Assert.Equal(0, run.Stalls);
        Assert.True(run.Took < run.Ideal - 60, "the anchor started at the new head, not behind the file");
        Assert.True(tail.Pos.X > lead.Pos.X, "the old rear man walked at the head");
    }

    // The same orders give the same hashes and positions on two runs (the places are hashed; the hash agrees with the reference
    // one tick by tick inside March).
    [Theory]
    [InlineData(SquadMode.March)]
    [InlineData(SquadMode.Assault)]
    public void Deterministic(SquadMode mode)
    {
        (ulong, List<Vec>) Play()
        {
            var (w, s, men, _) = Setup(mode, seed: 7);
            March(w, s, men, CommandKind.AttackMove);
            return (w.Hash(), men.Select(u => u.Pos).ToList());
        }
        var (h1, p1) = Play();
        var (h2, p2) = Play();
        Assert.Equal(h1, h2);
        Assert.Equal(p1, p2);
    }
}
