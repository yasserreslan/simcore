using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// Infantry squads, phase S1 (docs/design.md, "Infantry squads"): forming, merging, adding, detaching, disbanding, the leader,
// losses, boarding, March and Assault with their slots and speeds, the leash to the slot, replays and determinism.
public class SquadTests
{
    private static void Cmd(World w, byte player, CommandKind kind, IEnumerable<Unit> units, Cell cell = default, int index = 0, uint target = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = kind, Units = [.. units.Select(u => u.Id)], Cell = cell, Index = index, Target = target });

    private static List<Unit> Riflemen(World w, int n, int x, int y, byte player = 1) =>
        [.. Enumerable.Range(0, n).Select(i => w.Spawn(player, "Rifleman", x + i % 5, y + i / 5))];

    private static Squad Form(World w, IEnumerable<Unit> units)
    {
        var list = units.ToList();
        Cmd(w, list[0].Owner, CommandKind.FormSquad, list);
        w.Run(1);
        return w.SquadOf(list[0].Id) ?? throw new InvalidOperationException("no squad formed");
    }

    // The content's squad block: the sizes, the roles by tag and who may not lead.
    [Fact]
    public void ContentHasTheSquadRules()
    {
        var w = Arena.New(64);
        var rules = w.Content.Squad!;
        Assert.Equal((2, 10), (rules.MinSize, rules.MaxSize));
        Assert.Equal(SquadRole.Line, w.SquadRoleOf(w.Spawn(1, "Rifleman", 5, 5)));
        Assert.Equal(SquadRole.Line, w.SquadRoleOf(w.Spawn(1, "Sapper", 6, 5)));
        Assert.Equal(SquadRole.Recon, w.SquadRoleOf(w.Spawn(1, "Marksman", 7, 5)));
        Assert.Equal(SquadRole.Flank, w.SquadRoleOf(w.Spawn(1, "Tank hunter", 8, 5)));
        Assert.Equal(SquadRole.LongFlank, w.SquadRoleOf(w.Spawn(1, "kornet", 9, 5)));
        Assert.Equal(SquadRole.AirGuard, w.SquadRoleOf(w.Spawn(1, "manpads", 10, 5)));
        Assert.Equal(SquadRole.Support, w.SquadRoleOf(w.Spawn(1, "mortar", 11, 5)));
        Assert.False(rules.Leads(SquadRole.Medic));
        Assert.False(rules.Leads(SquadRole.Recon));
        Assert.False(rules.Leads(SquadRole.Support));
        Assert.True(rules.Leads(SquadRole.Flank));
        var s = Arena.NewSentinel(64);
        Assert.Equal(SquadRole.Medic, s.SquadRoleOf(s.Spawn(1, "sentinel_medic", 5, 5)));
    }

    // Form: up to 10 in ID order; workers and pilots left out; a last group of one stays loose; vehicles never join.
    [Theory]
    [InlineData(2, new[] { 2 })]
    [InlineData(10, new[] { 10 })]
    [InlineData(12, new[] { 10, 2 })]
    [InlineData(21, new[] { 10, 10 })]
    [InlineData(23, new[] { 10, 10, 3 })]
    public void FormsSquadsOfTenInIdOrder(int n, int[] sizes)
    {
        var w = Arena.New(64);
        var men = Riflemen(w, n, 10, 10);
        var worker = w.Spawn(1, "worker", 30, 30);
        var (car, _) = w.SpawnCrewed(1, "technical", 40, 40);
        Cmd(w, 1, CommandKind.FormSquad, [worker, car, .. men.AsEnumerable().Reverse()]);
        w.Run(1);
        Assert.Equal(sizes, w.Squads.Select(s => s.Members.Count).ToArray());
        var formed = w.Squads.SelectMany(s => s.Members).ToList();
        Assert.Equal(men.Take(formed.Count).Select(u => u.Id), formed); // ID order
        Assert.Equal(0u, worker.Squad);
        Assert.Equal(0u, car.Squad);
        Assert.All(men.Skip(formed.Count), u => Assert.Equal(0u, u.Squad));
        Assert.Equal(sizes.Length, w.SquadEvents.Count(e => e.Kind == SquadEventKind.Formed));
    }

    // A pilot (Sentinel) never joins, nor one soldier alone, nor another player's soldiers.
    [Fact]
    public void LeavesOutPilotsLoneSoldiersAndEnemies()
    {
        var w = Arena.NewSentinel(64);
        var pilot = w.Spawn(1, "pilot", 10, 10);
        var man = w.Spawn(1, "Rifleman", 11, 10);
        var enemy = w.Spawn(2, "Rifleman", 12, 10);
        Cmd(w, 1, CommandKind.FormSquad, [pilot, man, enemy]);
        w.Run(1);
        Assert.Empty(w.Squads);
        Assert.False(w.CanJoinSquad(pilot));
        Assert.True(w.CanJoinSquad(man));
    }

    // Merge: two squads of 4 and 5 become one of 9 (the lower ID); 6 and 6 are refused and stay as they were.
    [Fact]
    public void MergesOnlyUpToTen()
    {
        var w = Arena.New(64);
        var a = Form(w, Riflemen(w, 4, 10, 10));
        var b = Form(w, Riflemen(w, 5, 10, 20));
        Cmd(w, 1, CommandKind.FormSquad, [w.Unit(a.Members[0])!, w.Unit(b.Members[2])!]);
        w.Run(1);
        Assert.Single(w.Squads);
        Assert.Equal(a.Id, w.Squads[0].Id);
        Assert.Equal(9, w.Squads[0].Members.Count);
        Assert.Contains(w.SquadEvents, e => e.Kind == SquadEventKind.Merged && e.Other == b.Id);

        var c = Form(w, Riflemen(w, 6, 30, 10));
        var d = Form(w, Riflemen(w, 6, 30, 20));
        Cmd(w, 1, CommandKind.FormSquad, [.. c.Members.Select(id => w.Unit(id)!), .. d.Members.Select(id => w.Unit(id)!)]);
        w.Run(1);
        Assert.Equal(6, c.Members.Count);
        Assert.Equal(6, d.Members.Count);
        Assert.Equal(3, w.Squads.Count);
        Assert.Contains(w.SquadEvents, e => e.Kind == SquadEventKind.MergeRefused && e.Other == 12);
    }

    // Add: a squad of 8 and 4 loose soldiers: the two lowest IDs join, the other two stay loose.
    [Fact]
    public void AddFillsUpToTen()
    {
        var w = Arena.New(64);
        var s = Form(w, Riflemen(w, 8, 10, 10));
        var loose = Riflemen(w, 4, 30, 10);
        Cmd(w, 1, CommandKind.FormSquad, [.. loose, w.Unit(s.Members[3])!]);
        w.Run(1);
        Assert.Equal(10, s.Members.Count);
        Assert.Equal(s.Id, loose[0].Squad);
        Assert.Equal(s.Id, loose[1].Squad);
        Assert.Equal(0u, loose[2].Squad);
        Assert.Equal(0u, loose[3].Squad);
        Assert.Equal(2, w.SquadEvents.Count(e => e.Kind == SquadEventKind.Joined));
    }

    // Detach: loose with the squad's stance; a squad of two left with one disbands.
    [Fact]
    public void DetachLeavesALooseSoldierWithTheStance()
    {
        var w = Arena.New(64);
        var men = Riflemen(w, 3, 10, 10);
        var s = Form(w, men);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetStance, Units = [men[1].Id], Stance = Stance.HoldGround });
        w.Run(1);
        Assert.Equal(Stance.HoldGround, s.Stance);
        Assert.All(men, u => Assert.Equal(Stance.HoldGround, u.Stance)); // a stance given to one member is the squad's
        Cmd(w, 1, CommandKind.Detach, [men[2]]);
        w.Run(1);
        Assert.Equal(0u, men[2].Squad);
        Assert.Equal(Stance.HoldGround, men[2].Stance);
        Assert.Equal(2, s.Members.Count);
        Cmd(w, 1, CommandKind.Detach, [men[0]]);
        w.Run(1);
        Assert.Empty(w.Squads);
        Assert.All(men, u => Assert.Equal(0u, u.Squad));
        Assert.Contains(w.SquadEvents, e => e.Kind == SquadEventKind.Disbanded);
    }

    // Disband: everyone loose, in place: a moving squad stops where it is.
    [Fact]
    public void DisbandLeavesEveryoneInPlace()
    {
        var w = Arena.New(64);
        var men = Riflemen(w, 5, 10, 10);
        var s = Form(w, men);
        Cmd(w, 1, CommandKind.Move, [men[4]], new Cell(50, 10));
        w.Run(60);
        Assert.Equal(SquadState.Moving, s.State);
        Cmd(w, 1, CommandKind.Disband, [men[1]]);
        w.Run(1);
        Assert.Empty(w.Squads);
        var at = men.Select(u => u.Pos).ToList();
        w.Run(40);
        for (var i = 0; i < men.Count; i++)
        {
            Assert.Equal(0u, men[i].Squad);
            Assert.Equal(OrderKind.Idle, men[i].Order.Kind);
            Assert.True(World.Within(at[i], men[i].Pos, Fixed.One), $"member {i} walked on");
        }
    }

    // The leader is the lowest-ID member who is not a medic, sniper or mortar; when he dies the next takes over the same tick,
    // and the squad shrinks; at one member it disbands.
    [Fact]
    public void LeaderHandoverAndShrinking()
    {
        var w = Arena.New(64);
        var sniper = w.Spawn(1, "Marksman", 10, 10);
        var mortar = w.Spawn(1, "mortar", 11, 10);
        var a = w.Spawn(1, "Rifleman", 12, 10);
        var b = w.Spawn(1, "Tank hunter", 13, 10);
        var s = Form(w, [sniper, mortar, a, b]);
        Assert.Equal(a.Id, s.Leader);
        a.HP = 0;
        w.Run(1);
        Assert.Equal(b.Id, s.Leader);
        Assert.Equal([sniper.Id, mortar.Id, b.Id], s.Members);
        Assert.Contains(w.SquadEvents, e => e.Kind == SquadEventKind.Lost && e.Unit == a.Id);
        Assert.Contains(w.SquadEvents, e => e.Kind == SquadEventKind.Leader && e.Unit == b.Id && e.Other == a.Id);
        b.HP = 0;
        w.Run(1);
        Assert.Equal(sniper.Id, s.Leader); // nobody left who leads: the lowest ID
        mortar.HP = 0;
        w.Run(1);
        Assert.Empty(w.Squads);
        Assert.Equal(0u, sniper.Squad);
    }

    // An order given to one member goes to the whole squad; the squad walks one path and arrives as one.
    [Fact]
    public void AMoveToOneMemberMovesTheSquad()
    {
        var w = Arena.New(80);
        var men = Riflemen(w, 6, 10, 30);
        var s = Form(w, men);
        Cmd(w, 1, CommandKind.Move, [men[3]], new Cell(60, 30));
        w.Run(1);
        Assert.All(men, u => Assert.Equal(OrderKind.Move, u.Order.Kind));
        Assert.Equal(SquadState.Moving, s.State);
        Assert.NotEmpty(s.Path);
        Assert.Empty(w.GroupsForTests); // no group move: one squad path
        w.Run(900);
        Assert.Equal(SquadState.Idle, s.State);
        Assert.True(World.Within(s.Anchor, new Cell(60, 30).Center(), Fixed.FromInt(1)));
        Assert.All(men, u =>
        {
            Assert.Equal(OrderKind.Idle, u.Order.Kind);
            Assert.True(World.Within(u.Pos, w.SquadSlotPoint(u.Id)!.Value, Fixed.FromInt(1)), $"{u.Id} at {u.Pos}, slot {w.SquadSlotPoint(u.Id)}");
        });
    }

    // Where p stands in the squad's frame: how far ahead of the anchor along its facing, and how far to its right (raw).
    private static (long Along, long Across) Frame(Squad s, Vec p)
    {
        var d = p - s.Anchor;
        var f = s.Facing;
        return ((d.X.Raw * f.X.Raw + d.Y.Raw * f.Y.Raw) >> Fixed.Shift, (d.X.Raw * -f.Y.Raw + d.Y.Raw * f.X.Raw) >> Fixed.Shift);
    }

    // How far the anchor walks in n ticks (summed tick by tick, so the path's bends don't count against it).
    private static long Walked(World w, Squad s, int n)
    {
        long sum = 0;
        for (var i = 0; i < n; i++)
        {
            var a = s.Anchor;
            w.Run(1);
            sum += (s.Anchor - a).Len().Raw;
        }
        return sum;
    }

    // March: a file along the path, 1.5 cells apart, at the slowest member's speed (a Kornet team's 1.4 cells/s).
    [Fact]
    public void MarchIsAFileAtTheSlowestSpeed()
    {
        var w = Arena.New(128);
        var men = Riflemen(w, 4, 10, 60);
        men.Add(w.Spawn(1, "kornet", 15, 60));
        var s = Form(w, men);
        Assert.Equal(SquadMode.March, s.Mode);
        w.Run(300); // formed up at rest, in role order (an order given at once hands the slots out by place: SquadOrderTests)
        Cmd(w, 1, CommandKind.Move, [men[0]], new Cell(120, 60));
        w.Run(300); // formed up and walking
        var kornet = men[4].Def.Speed.Raw * 100; // per 100 ticks
        Assert.InRange(Walked(w, s, 100), kornet * 97 / 100, kornet + 2);
        Assert.Equal(SquadState.Moving, s.State);
        // The file: the leader in front, everyone else behind him on the path, 1.5 cells apart, each on his slot.
        var file = men.OrderByDescending(u => Frame(s, u.Pos).Along).ToList();
        Assert.Equal(s.Leader, file[0].Id);
        for (var i = 0; i < file.Count; i++)
        {
            Assert.True(World.Within(file[i].Pos, w.SquadSlotPoint(file[i].Id)!.Value, Fixed.One), $"member {i} off his slot");
            Assert.InRange(Frame(s, file[i].Pos).Across, -Fixed.OneRaw, Fixed.OneRaw);
            if (i > 0)
            {
                var gap = (w.SquadSlotPoint(file[i - 1].Id)!.Value - w.SquadSlotPoint(file[i].Id)!.Value).Len().Raw;
                Assert.InRange(gap, Fixed.OneRaw * 13 / 10, Fixed.OneRaw * 3 / 2 + 2); // 1.5 cells along the trail
            }
        }
        Assert.Equal(men[4], file[^1]); // the long flank at the rear
    }

    // March roles: recon ahead, the flank behind the leader, the air guard and the support at the rear.
    [Fact]
    public void MarchPutsTheRolesInTheirPlaces()
    {
        var w = Arena.New(128);
        var sniper = w.Spawn(1, "Marksman", 10, 60);
        var lead = w.Spawn(1, "Rifleman", 11, 60);
        var line1 = w.Spawn(1, "Rifleman", 12, 60);
        var line2 = w.Spawn(1, "Rifleman", 13, 60);
        var rpg = w.Spawn(1, "Tank hunter", 14, 60);
        var aa = w.Spawn(1, "manpads", 15, 60);
        var mortar = w.Spawn(1, "mortar", 16, 60);
        var s = Form(w, [sniper, lead, line1, line2, rpg, aa, mortar]);
        Assert.Equal(lead.Id, s.Leader);
        w.Run(300); // formed up at rest, in role order (an order given at once hands the slots out by place: SquadOrderTests)
        Cmd(w, 1, CommandKind.Move, [lead], new Cell(120, 60));
        w.Run(400);
        Assert.Equal(SquadState.Moving, s.State);
        long X(Unit u) => Frame(s, w.SquadSlotPoint(u.Id)!.Value).Along;
        Assert.True(X(sniper) > X(lead), "recon ahead of the leader");
        Assert.True(X(lead) > X(rpg) && X(rpg) > X(line1) && X(line1) > X(line2), "the flank behind the leader, then the line");
        Assert.True(X(line2) > X(aa) && X(aa) > X(mortar), "the air guard and the support at the rear");
        Assert.InRange(X(sniper) - X(lead), Fixed.FromInt(4).Raw, Fixed.FromInt(6).Raw);
        foreach (var u in new[] { sniper, lead, rpg, line1, line2, aa, mortar })
        {
            Assert.True(World.Within(u.Pos, w.SquadSlotPoint(u.Id)!.Value, Fixed.FromInt(2)), $"{u.Def.Archetype.Id} off its slot");
        }
    }

    // The medic walks in the middle of the file (the Sentinel's assault medic).
    [Fact]
    public void TheMedicWalksInTheMiddle()
    {
        var w = Arena.NewSentinel(128);
        var men = Riflemen(w, 4, 10, 60);
        var medic = w.Spawn(1, "sentinel_medic", 15, 60);
        var s = Form(w, [.. men, medic]);
        Assert.Equal(men[0].Id, s.Leader);
        var order = men.Append(medic).OrderByDescending(u => Frame(s, w.SquadSlotPoint(u.Id)!.Value).Along).ToList();
        Assert.Equal(medic, order[2]); // leader, line, medic, line, line
    }

    // Assault: a line facing the move, 2 cells apart, at 80% of the slowest speed; the flanks at the ends.
    [Fact]
    public void AssaultIsALineAtEightyPercent()
    {
        var w = Arena.New(128);
        var men = Riflemen(w, 4, 10, 60);
        var rpg = w.Spawn(1, "Tank hunter", 15, 60);
        men.Add(rpg);
        var s = Form(w, men);
        Cmd(w, 1, CommandKind.SetSquadMode, [men[0]], index: (int)SquadMode.Assault);
        w.Run(1);
        Assert.Equal(SquadMode.Assault, s.Mode);
        w.Run(300); // formed up at rest, in role order (an order given at once hands the slots out by place: SquadOrderTests)
        Cmd(w, 1, CommandKind.Move, [men[0]], new Cell(120, 60));
        w.Run(300);
        var slowest = men.Min(u => u.Def.Speed.Raw);
        var want = slowest * 80 / 100 * 100;
        Assert.InRange(Walked(w, s, 100), want * 97 / 100, want + 2);
        Assert.Equal(SquadState.Moving, s.State);
        // A line across the move: everyone level with the anchor, 2 cells apart across it.
        foreach (var u in men)
        {
            Assert.InRange(Frame(s, u.Pos).Along, -Fixed.OneRaw, Fixed.OneRaw);
        }
        var line = men.OrderBy(u => Frame(s, u.Pos).Across).ToList();
        for (var i = 1; i < line.Count; i++)
        {
            Assert.InRange(Frame(s, line[i].Pos).Across - Frame(s, line[i - 1].Pos).Across, Fixed.OneRaw * 3 / 2, Fixed.OneRaw * 5 / 2);
        }
        Assert.True(rpg == line[0] || rpg == line[^1], "the flank at an end of the line");
        Assert.Equal(s.Leader, line[2].Id); // the leader in the middle
    }

    // The shape is rebuilt on arrival, and an idle squad keeps it: a member pushed off his slot walks back.
    [Fact]
    public void KeepsItsShapeWhenIdle()
    {
        var w = Arena.New(64);
        var men = Riflemen(w, 4, 20, 20);
        var s = Form(w, men);
        Cmd(w, 1, CommandKind.SetSquadMode, [men[0]], index: (int)SquadMode.Assault);
        w.Run(200);
        foreach (var u in men)
        {
            Assert.True(World.Within(u.Pos, w.SquadSlotPoint(u.Id)!.Value, Fixed.One), "formed up");
        }
        var pushed = men[2];
        pushed.Pos += new Vec(Fixed.FromInt(3), Fixed.FromInt(2));
        w.Run(100);
        Assert.True(World.Within(pushed.Pos, w.SquadSlotPoint(pushed.Id)!.Value, Fixed.One), "back on his slot");
        Assert.Equal(SquadState.Idle, s.State);
    }

    // Two squads in one move stand side by side at the goal, not on top of each other.
    [Fact]
    public void TwoSquadsInOneMoveStandSideBySide()
    {
        var w = Arena.New(128);
        var a = Form(w, Riflemen(w, 5, 10, 50));
        var b = Form(w, Riflemen(w, 5, 10, 70));
        Cmd(w, 1, CommandKind.Move, [w.Unit(a.Members[0])!, w.Unit(b.Members[0])!], new Cell(90, 60));
        w.Run(1);
        Assert.Equal(new Cell(90, 54), a.Goal);
        Assert.Equal(new Cell(90, 66), b.Goal);
    }

    // Attack-move: the squad stops while a member fights and walks on after.
    [Fact]
    public void AttackMoveFightsOnTheWay()
    {
        var w = Arena.New(128);
        var men = Riflemen(w, 5, 10, 60);
        var s = Form(w, men);
        var enemy = w.Spawn(2, "Rifleman", 40, 60, Facing.West);
        Cmd(w, 1, CommandKind.AttackMove, [men[0]], new Cell(90, 60));
        w.Run(1);
        Assert.All(men, u => Assert.Equal(OrderKind.AttackMove, u.Order.Kind));
        var halted = false;
        for (var t = 0; t < 1500 && s.State == SquadState.Moving; t++)
        {
            var before = s.Anchor;
            w.Run(1);
            halted |= enemy.Alive && s.Anchor == before && men.Any(u => u.SquadHalt || u.Ai != AiState.None);
        }
        Assert.True(halted, "it held while fighting");
        Assert.False(enemy.Alive);
        Assert.Equal(SquadState.Idle, s.State);
        Assert.True(World.Within(s.Anchor, new Cell(90, 60).Center(), Fixed.FromInt(2)));
    }

    // In a fight a member fights as the unit AI says, with his slot as his leash point: he chases, then walks back to his slot.
    [Fact]
    public void TheLeashPointIsTheSlot()
    {
        var w = Arena.New(96);
        var men = Riflemen(w, 4, 40, 40);
        var s = Form(w, men);
        w.Run(100);
        // An enemy sniper 10 cells off: out of their reach (5), within the retaliation distance; he fires at one on an order.
        var enemy = w.Spawn(2, "Marksman", 46, 50, Facing.North);
        Cmd(w, 2, CommandKind.Attack, [enemy], target: men[0].Id);
        var chased = false;
        for (var t = 0; t < 600 && enemy.Alive; t++)
        {
            w.Run(1);
            chased |= men.Any(u => u.Ai == AiState.Engage);
        }
        Assert.True(chased, "they went after him");
        Assert.False(enemy.Alive);
        w.Run(400);
        foreach (var u in men.Where(m => m.Alive))
        {
            Assert.Equal(AiState.None, u.Ai);
            var slot = w.SquadSlotPoint(u.Id)!.Value; // (a new leader, if the sniper got the old one, moves the slots)
            Assert.True(World.Within(u.Pos, slot, Fixed.FromInt(1)), $"{u.Id} at {u.Pos}, slot {slot}");
        }
        Assert.Equal(SquadState.Idle, s.State);
    }

    // Boarding: an Enter given to one member takes the whole squad into the building; an Exit given to one brings them all out;
    // it stays a squad inside and is one again outside, its shape rebuilt round the leader.
    [Fact]
    public void BoardsAndComesOutAsOne()
    {
        var w = Arena.New(64);
        var house = w.Unit(w.Spawn(w.Player(1)!.Setup.Find(w.Content, "house_small")!, new Cell(30, 20), Facing.South))!;
        var men = Riflemen(w, 4, 30, 32);
        var s = Form(w, men);
        Cmd(w, 1, CommandKind.Enter, [men[2]], target: house.Id);
        w.Run(300);
        Assert.All(men, u => Assert.Equal(house.Id, u.Garrison));
        Assert.Equal(s, w.SquadOf(men[0].Id));
        Assert.Equal(4, s.Members.Count);
        Cmd(w, 1, CommandKind.Exit, [men[1]], cell: new Cell(31, 30));
        w.Run(1);
        Assert.All(men, u => Assert.Equal(0u, u.Garrison));
        w.Run(200);
        Assert.False(s.Loose);
        Assert.All(men, u => Assert.True(World.Within(u.Pos, w.SquadSlotPoint(u.Id)!.Value, Fixed.One), "formed up outside"));
        Cmd(w, 1, CommandKind.Move, [men[0]], new Cell(30, 50));
        w.Run(1);
        Assert.Equal(SquadState.Moving, s.State);
    }

    // A carrier: an Enter given to one member puts the whole squad in the back; an Exit given to one lets them all out.
    [Fact]
    public void RidesACarrierAndGetsOutAsOne()
    {
        var w = Arena.New(64);
        var (carrier, driver) = w.SpawnCrewed(1, "infantry_carrier", 30, 20);
        var men = Riflemen(w, 5, 28, 30);
        var s = Form(w, men);
        Cmd(w, 1, CommandKind.Enter, [men[4]], target: carrier.Id);
        w.Run(300);
        Assert.All(men, u => Assert.Equal(carrier.Id, u.Inside));
        Assert.Equal(5, s.Members.Count);
        Assert.Equal(0u, driver.Squad);
        Cmd(w, 1, CommandKind.Exit, [men[0]]);
        w.Run(1);
        Assert.All(men, u => Assert.Equal(0u, u.Inside));
        Assert.Equal(carrier.Id, driver.Inside); // the driver (no member) stays at the wheel
        w.Run(200);
        Assert.False(s.Loose);
        Assert.All(men, u => Assert.True(World.Within(u.Pos, w.SquadSlotPoint(u.Id)!.Value, Fixed.One), "formed up outside"));
    }

    // An attack given to one member is the squad's: everyone goes for the target, and once it is done the shape is rebuilt
    // round the leader where he stands.
    [Fact]
    public void AnAttackGoesToTheWholeSquadThenItReforms()
    {
        var w = Arena.New(64);
        var men = Riflemen(w, 4, 20, 20);
        var s = Form(w, men);
        var enemy = w.Spawn(2, "Rifleman", 20, 40);
        enemy.HP = 1;
        w.Run(1);
        Cmd(w, 1, CommandKind.Attack, [men[1]], target: enemy.Id);
        w.Run(1);
        Assert.True(s.Loose);
        Assert.All(men, u => Assert.Equal(OrderKind.Attack, u.Order.Kind));
        w.Run(300);
        Assert.False(enemy.Alive);
        Assert.False(s.Loose);
        var leader = w.Unit(s.Leader)!;
        Assert.True(leader.Pos.Y.Raw > Fixed.FromInt(30).Raw, "rebuilt where the leader went");
        w.Run(200);
        Assert.All(men, u => Assert.True(World.Within(u.Pos, w.SquadSlotPoint(u.Id)!.Value, Fixed.One), "formed up again"));
    }

    // Content without a squad block forms no squads; a block naming an unknown role is refused.
    [Fact]
    public void TheSquadBlockIsContent()
    {
        var none = ContentLoader.Load([.. TestContent.MvpSources().Where(x => x.Name != "squad.json")]);
        Assert.Null(none.Squad);
        Assert.NotEqual(none.Hash, TestContent.Mvp.Hash);
        var bad = TestContent.MvpSources().Select(x => x.Name == "squad.json"
            ? x with { Json = x.Json.Replace("\"role\": \"support\"", "\"role\": \"cook\"", StringComparison.Ordinal) } : x).ToList();
        Assert.Contains("unknown role \"cook\"", Assert.Throws<ContentException>(() => ContentLoader.Load(bad)).Message);
        var two = TestContent.MvpSources();
        two.Add(two.Single(x => x.Name == "squad.json") with { Name = "extra/squad.json" });
        Assert.Contains("squad: already defined", Assert.Throws<ContentException>(() => ContentLoader.Load(two)).Message);
    }

    // The world hash covers the squads, and the original one-by-one hash agrees.
    [Fact]
    public void SquadStateIsHashed()
    {
        var a = Arena.New(64);
        var b = Arena.New(64);
        var ma = Riflemen(a, 4, 10, 10);
        Riflemen(b, 4, 10, 10);
        Assert.Equal(a.Hash(), b.Hash());
        Cmd(a, 1, CommandKind.FormSquad, ma);
        a.Run(1);
        b.Run(1);
        Assert.NotEqual(a.Hash(), b.Hash());
        Cmd(a, 1, CommandKind.Move, [ma[0]], new Cell(50, 40));
        for (var i = 0; i < 100; i++)
        {
            a.Run(1);
            Assert.Equal(a.Hash(), a.HashReference());
        }
    }

    // The same orders give the same positions on two runs, whatever order the units are listed in.
    [Fact]
    public void Deterministic()
    {
        static (ulong, List<Vec>) Play(bool reversed)
        {
            var w = Arena.New(128, seed: 5);
            var men = Enumerable.Range(0, 23).Select(i => w.Spawn(1, i % 4 == 3 ? "Tank hunter" : "Rifleman", 10 + i % 6, 40 + i / 6)).ToList();
            var order = reversed ? Enumerable.Reverse(men).ToList() : men;
            Cmd(w, 1, CommandKind.FormSquad, order);
            w.Run(1);
            Cmd(w, 1, CommandKind.SetSquadMode, [men[11]], index: 1);
            Cmd(w, 1, CommandKind.Move, order, new Cell(100, 60));
            w.Run(300);
            Cmd(w, 1, CommandKind.AttackMove, reversed ? men.Take(5).Reverse() : men.Take(5), new Cell(40, 100));
            w.Run(300);
            return (w.Hash(), men.Select(u => u.Pos).ToList());
        }
        var (h1, p1) = Play(false);
        var (h2, p2) = Play(false);
        var (h3, p3) = Play(true);
        Assert.Equal(h1, h2);
        Assert.Equal(p1, p2);
        Assert.Equal(h1, h3);
        Assert.Equal(p1, p3);
    }

    // Replays store the squad commands and play them back to the same hashes.
    [Fact]
    public void ReplayRoundTrip()
    {
        var setup = TestContent.Battle(11);
        var preview = Replay.NewWorld(TestContent.Mvp, setup);
        var blue = preview.Units.Where(u => u.Owner == 1 && preview.CanJoinSquad(u)).Select(u => u.Id).ToList();
        Assert.True(blue.Count >= 8);
        var commands = new List<Command>
        {
            new() { Tick = 5, Player = 1, Kind = CommandKind.FormSquad, Units = blue },
            new() { Tick = 10, Player = 1, Kind = CommandKind.SetSquadMode, Units = [blue[0]], Index = 1 },
            new() { Tick = 12, Player = 1, Kind = CommandKind.Move, Units = [blue[1]], Cell = new Cell(40, 40) },
            new() { Tick = 150, Player = 1, Kind = CommandKind.Detach, Units = [blue[2]] },
            new() { Tick = 200, Player = 1, Kind = CommandKind.AttackMove, Units = [blue[0]], Cell = new Cell(60, 30) },
            new() { Tick = 300, Player = 1, Kind = CommandKind.Disband, Units = [blue[3]] },
        };
        var live = Replay.NewWorld(TestContent.Mvp, setup);
        var issued = commands.Select(c => live.Issue(c)).ToList();
        var hashes = new List<ulong>();
        var formed = false;
        while (live.Tick < 400)
        {
            live.Step();
            hashes.Add(live.Hash());
            formed |= live.Squads.Count > 0;
        }
        Assert.True(formed);
        var rec = new Replay { Setup = setup, Ticks = 400, Commands = issued };
        using var buf = new MemoryStream();
        rec.Encode(buf);
        buf.Position = 0;
        var loaded = Replay.Decode(buf);
        static string Key(Command c) => $"{c.Tick}/{c.Player}/{c.Seq}/{c.Kind}/{string.Join(',', c.Units)}/{c.Cell}/{c.Index}";
        Assert.Equal(issued.Select(Key), loaded.Commands.Select(Key));
        var (replayed, _) = loaded.Play(TestContent.Mvp);
        Assert.Equal(hashes, replayed);
    }
}
