using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-HELP (the owner, 2026-11-22, after the battle demo: "I'd like the nearby infantry to engage too when teammate infantry is being
// attacked ... they should outgun the forces they identify as threat unless told otherwise and they should engage directly when on
// guard, aggressive or hold ground"; the coordinator's calls 1 to 4, ~/Desktop/rts-work/handover/inf-help-brief.md).
public class SquadHelpTests(ITestOutputHelper output)
{
    private static World New(int size = 80, ulong seed = 1)
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

    private static Unit[] Men(World w, byte player, int x, int y0, int n, int facing = Facing.East) =>
        [.. Enumerable.Range(0, n).Select(i => w.Spawn(player, "Rifleman", x, y0 + i, facing))];

    private static void Issue(World w, Squad s, CommandKind kind, Cell cell = default, Stance stance = Stance.Guard, int index = 0, Cell? cell2 = null) =>
        w.Issue(new Command { Tick = w.Tick, Player = s.Owner, Kind = kind, Units = [.. s.Members], Cell = cell, Stance = stance, Index = index, Cell2 = cell2 });

    private static void Steps(World w, int n)
    {
        for (var i = 0; i < n; i++)
        {
            w.Step();
        }
    }

    private static (int X, int Y) Centre(World w, Squad s)
    {
        long x = 0, y = 0, n = 0;
        foreach (var id in s.Members)
        {
            if (w.Unit(id) is { Alive: true } u)
            {
                (x, y, n) = (x + u.Pos.X.Raw, y + u.Pos.Y.Raw, n + 1);
            }
        }
        return n == 0 ? (0, 0) : ((int)(x / n / Fixed.OneRaw), (int)(y / n / Fixed.OneRaw));
    }

    // A call made by hand (as scene 24): A calls at its centre, its enemy group 8 cells north, a need no helper can fill.
    private static void Call(World w, Squad a, int u = 70)
    {
        var m = a.Mind!;
        var (x, y) = Centre(w, a);
        (m.Calling, m.Urgency, m.CallSeen, m.CallAt, m.FThem, m.FUs, m.GroupAt, m.Axis) = (true, u, w.Tick, new Cell(x, y), 100_000_000, 0, new Cell(x, y - 8), 24);
    }

    private static void ToMatch(World w)
    {
        while (w.Tick % 20 != 0)
        {
            w.Step();
        }
        w.Step();
    }

    // Call 1: an Attack-move is not "told otherwise": B, on the player's Attack-move 20 cells from A's call, answers; the call
    // withdrawn, B goes on with that Attack-move (not 13.4's walk back). A plain Move (C), Hold mode (D) and the Return fire (E),
    // Hold fire (F) and Hold ground (G) stances are "told otherwise": they never leave. Before (on 4a7c8dd) B and G answered nothing
    // and everything (G: an idle squad on Hold ground was a helper).
    [Fact]
    public void AnAttackMoveAnswersAndGoesOnTheToldOtherwiseStay()
    {
        var w = New(100);
        var a = Form(w, Men(w, 1, 50, 50, 4));
        var b = Form(w, Men(w, 1, 50, 70, 4));
        var c = Form(w, Men(w, 1, 30, 50, 4));
        var d = Form(w, Men(w, 1, 70, 50, 4));
        var e = Form(w, Men(w, 1, 50, 30, 4));
        var f = Form(w, Men(w, 1, 35, 35, 4));
        var g = Form(w, Men(w, 1, 65, 35, 4));
        Steps(w, 60);
        var bGoal = new Cell(50, 98);
        Issue(w, b, CommandKind.AttackMove, bGoal);
        Issue(w, c, CommandKind.Move, new Cell(2, 50));
        Issue(w, d, CommandKind.SetSquadMode, new Cell(70, 50), index: (int)SquadMode.Hold, cell2: new Cell(90, 50));
        Issue(w, e, CommandKind.SetStance, stance: Stance.ReturnFire);
        Issue(w, f, CommandKind.SetStance, stance: Stance.HoldFire);
        Issue(w, g, CommandKind.SetStance, stance: Stance.HoldGround);
        Steps(w, 5);
        Assert.Equal(OrderKind.AttackMove, b.Order);
        Call(w, a);
        ToMatch(w);
        output.WriteLine($"helpers b {b.Mind!.HelpOf} c {c.Mind!.HelpOf} d {d.Mind!.HelpOf} e {e.Mind!.HelpOf} f {f.Mind!.HelpOf} g {g.Mind!.HelpOf}; b's order {b.Order} to {b.Goal}");
        Assert.Equal(a.Id, b.Mind.HelpOf);
        Assert.All(new[] { c, d, e, f, g }, q => Assert.Equal(0u, q.Mind!.HelpOf));
        Assert.Equal(OrderKind.AttackMove, b.Order);
        Assert.NotEqual(bGoal, b.Goal); // on its way to the support position
        a.Mind!.Calling = false; // the call withdrawn
        Steps(w, 20);
        output.WriteLine($"after: b help {b.Mind.HelpOf} order {b.Order} to {b.Goal} returning {b.Mind.Returning} standing down {b.Mind.StandDownUntil}");
        Assert.Equal((0u, OrderKind.AttackMove, bGoal, false, 0u), (b.Mind.HelpOf, b.Order, b.Goal, b.Mind.Returning, b.Mind.StandDownUntil));
        Steps(w, 200);
        Assert.True(Centre(w, b).Y > 72, $"B goes on south with its Attack-move: {Centre(w, b)}");
    }

    // Call 2, Aggressive: an Aggressive squad answers a call within aggressivePercent (150) of callRadius (25 cells: 37.5) and takes
    // its support position at the low end of its line weapon's band (the rifle's 40% against advanceToPercent 70% of its 5 cells);
    // a Guard squad as far off answers nothing (it answers within callRadius, as written). Before: neither answered from 33 cells.
    [Fact]
    public void AnAggressiveSquadAnswersFurtherAndComesNearer()
    {
        var w = New(100);
        var a = Form(w, Men(w, 1, 50, 50, 4));
        var guardFar = Form(w, Men(w, 1, 17, 50, 4)); // 33 cells west
        var aggrFar = Form(w, Men(w, 1, 83, 50, 4)); // 33 cells east
        Steps(w, 60);
        Issue(w, aggrFar, CommandKind.SetStance, stance: Stance.Aggressive);
        Steps(w, 5);
        Call(w, a);
        ToMatch(w);
        Assert.Equal((0u, a.Id), (guardFar.Mind!.HelpOf, aggrFar.Mind!.HelpOf));
        var group = a.Mind!.GroupAt.Center();
        var aggrBand = (aggrFar.Mind.HelpTo.Center() - group).Len();
        // The same call answered by a Guard squad within callRadius: its support cell is at advanceToPercent.
        var w2 = New(100);
        var a2 = Form(w2, Men(w2, 1, 50, 50, 4));
        var guardNear = Form(w2, Men(w2, 1, 70, 50, 4));
        Steps(w2, 65);
        Call(w2, a2);
        ToMatch(w2);
        Assert.Equal(a2.Id, guardNear.Mind!.HelpOf);
        var guardBand = (guardNear.Mind.HelpTo.Center() - a2.Mind!.GroupAt.Center()).Len();
        output.WriteLine($"aggressive support cell {aggrBand.Raw / 1024.0:0.00} cells from the enemy group, guard {guardBand.Raw / 1024.0:0.00}");
        Assert.True(aggrBand.Raw < guardBand.Raw, $"Aggressive comes nearer: {aggrBand.Raw / 1024.0:0.00} against {guardBand.Raw / 1024.0:0.00} cells");
        Assert.InRange(aggrBand.Raw, 1 * Fixed.OneRaw, 3 * Fixed.OneRaw); // the rifle's 40% of 5 cells: 2, to the cell
    }

    // Calls 2 (Hold ground, Guard, Aggressive) and 4: R, a red rifleman on the player's Attack order at A (a friend), shoots only at
    // A; B's squad, 4 cells from R, is not shot at. On Hold ground, Guard and Aggressive B's men take R as their target (a friend's
    // attacker in range is a target, whether or not it shoots at them); on Hold fire and Hide they don't (15.7), on Return fire only
    // who shot at them. What existed: 15.1's targets are the enemies a man sees now (his side sees them, within his vision) in his
    // weapon's range, whoever they shoot at: this test pins it.
    [Theory]
    [InlineData(Stance.HoldGround, true)]
    [InlineData(Stance.Guard, true)]
    [InlineData(Stance.Aggressive, true)]
    [InlineData(Stance.ReturnFire, false)]
    [InlineData(Stance.HoldFire, false)]
    [InlineData(Stance.Hide, false)]
    public void AFriendsAttackerInRangeIsATarget(Stance stance, bool fires)
    {
        var w = New(64);
        var friend = w.Spawn(1, "Rifleman", 20, 30, Facing.East);
        var b = Form(w, w.Spawn(1, "Rifleman", 25, 34, Facing.North), w.Spawn(1, "Rifleman", 26, 34, Facing.North));
        Issue(w, b, CommandKind.SetStance, stance: stance);
        Steps(w, 40);
        var red = w.Spawn(2, "Rifleman", 24, 30, Facing.West);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.Attack, Units = [red.Id], Target = friend.Id });
        uint first = 0;
        var redOnB = false;
        for (var t = 0; t < 120 && red.Alive; t++)
        {
            w.Step();
            first = first == 0 && b.Members.Any(id => w.Unit(id) is { AutoTarget: var at } && at == red.Id) ? w.Tick : first;
            redOnB |= friend.Alive && b.Members.Contains(red.AutoTarget); // (the friend dead, R's order is over and he may turn on B)
        }
        output.WriteLine($"{stance}: B's first target on R at tick {first}; R shot at B {redOnB}; friend hp {friend.HP}");
        Assert.False(redOnB);
        Assert.Equal(fires, first != 0);
    }

    // Call 3, outgun the threat: 13.3's need (F(them) x 130% - F(us)) stays; maxHelpers is 4 (was 2), so helpers are sent until the
    // need is met. (1) A (4) is hit by two red squads of 8, five idle blue squads of 4 near: at every match the call has its need met,
    // or 4 helpers, or no free squad left in reach (10.2's F(them) counts the enemy far off at little, so one or two helpers meet it
    // here). (2) Calls made by hand among six free squads: a need of 2.5 helpers' force gets 3, a need beyond 4 helpers gets 4 (on
    // 4a7c8dd: 2 and 2).
    [Fact]
    public void HelpersAreSentUntilTheNeedIsMet()
    {
        (long Need, int Count) Made(long fThem)
        {
            var v = New(100);
            var c = Form(v, Men(v, 1, 50, 50, 4));
            foreach (var (x, y) in new[] { (35, 40), (65, 40), (35, 60), (65, 60), (50, 32), (50, 68) })
            {
                Form(v, Men(v, 1, x, y, 4));
            }
            Steps(v, 60);
            Call(v, c);
            c.Mind!.FThem = fThem;
            ToMatch(v);
            return v.CallNeed(c, v.Content.InfantryCombat!);
        }
        const long Huge = 100_000_000;
        var big = Made(Huge);
        var force = (Huge * 130 / 100 - big.Need) / big.Count; // one helper's force (the six squads alike)
        var some = Made(force * 25 / 10 * 100 / 130);
        output.WriteLine($"a helper's force {force}; need of 2.5 helpers: {some.Count} sent, left {some.Need}; a huge need: {big.Count} sent");
        var w = New(100);
        var a = Form(w, Men(w, 1, 40, 48, 4));
        Squad[] near = [Form(w, Men(w, 1, 30, 30, 4)), Form(w, Men(w, 1, 30, 64, 4)), Form(w, Men(w, 1, 22, 44, 4)), Form(w, Men(w, 1, 40, 28, 4)), Form(w, Men(w, 1, 40, 68, 4))];
        Steps(w, 60);
        var r1 = Form(w, Men(w, 2, 52, 40, 8, Facing.West));
        var r2 = Form(w, Men(w, 2, 52, 49, 8, Facing.West));
        Issue(w, r1, CommandKind.AttackMove, new Cell(30, 45));
        Issue(w, r2, CommandKind.AttackMove, new Cell(30, 53));
        var rules = w.Content.InfantryCombat!;
        var most = 0;
        var checks = 0;
        for (var t = 0; t < 600; t++)
        {
            // The match's view (it runs before the squads think and fight, which refresh F(them) and F(us), wound men and may end a
            // help): taken just before the matching tick.
            var matching = w.Tick % 20 == 0 && a.Mind is { Calling: true };
            var (needPre, countPre) = matching ? w.CallNeed(a, rules) : (0, 0);
            var free = matching ? near.Where(h => h.Mind is { } hm && w.HelpTest(h, hm, a, rules) == HelpBlock.Free &&
                (Centre(w, h).X - a.Mind!.CallAt.X) * (Centre(w, h).X - a.Mind.CallAt.X) + (Centre(w, h).Y - a.Mind.CallAt.Y) * (Centre(w, h).Y - a.Mind.CallAt.Y) <= 24 * 24).ToList() : [];
            w.Step();
            if (!matching || !a.Mind!.Calling || !w.Squads.Contains(a))
            {
                continue; // (a call withdrawn in the tick, or A disbanded, down to one man, is matched no more)
            }
            var sent = free.Count(h => h.Mind!.HelpOf == a.Id);
            var count = countPre + sent;
            // (free before and after the tick: the soldiers' thinks before the match may put a squad in contact)
            var left = free.Count(h => h.Mind!.HelpOf != a.Id && w.HelpTest(h, h.Mind, a, rules) == HelpBlock.Free);
            // (the soldiers' thinks before the match may change a helper's arm in hand, and so its force, by up to a man's: slack)
            Assert.True(needPre <= force / 4 || countPre == 4 || left == 0 || sent > 0, $"t{w.Tick}: need {needPre} with {countPre} helpers, none sent, {left} free");
            Assert.True(needPre - (long)sent * force <= force / 4 || count == 4 || left == 0, $"t{w.Tick}: need {needPre} with {count} helpers and {left} free left");
            most = Math.Max(most, count);
            checks++;
        }
        output.WriteLine($"the two-squad attack: most helpers {most} in {checks} matches; assigned {w.CallStats.Assigned}");
        Assert.True(checks > 0 && most >= 1);
        Assert.Equal(4, big.Count);
        Assert.Equal(3, some.Count);
        Assert.True(some.Need <= 0);
    }
    // INF-HELP-2, the coordinator's call 1 (the owner: "they should outgun the forces they identify as threat"): a call's F(them) is
    // the whole known enemy group it is in contact with, every man at his full firepower, in range or not; so A (4), hit by two red
    // squads of 8 with five idle squads of 4 near, gets helpers enough to outgun all 16 (4: 16 x 130% is more than A's 4 and four
    // helpers' 16). Before (on bd9b744) one or two (row 1335: a man beyond 1.5 x his range counted 0).
    [Fact]
    public void HelpersOutgunTheWholeEnemyGroup()
    {
        var w = New(100);
        var a = Form(w, Men(w, 1, 40, 48, 4));
        Squad[] near = [Form(w, Men(w, 1, 30, 30, 4)), Form(w, Men(w, 1, 30, 64, 4)), Form(w, Men(w, 1, 22, 44, 4)), Form(w, Men(w, 1, 40, 28, 4)), Form(w, Men(w, 1, 40, 68, 4))];
        Steps(w, 60);
        var r1 = Form(w, Men(w, 2, 52, 40, 8, Facing.West));
        var r2 = Form(w, Men(w, 2, 52, 49, 8, Facing.West));
        Issue(w, r1, CommandKind.AttackMove, new Cell(30, 45));
        Issue(w, r2, CommandKind.AttackMove, new Cell(30, 53));
        var rules = w.Content.InfantryCombat!;
        var man = (long)rules[CombatValue.SqRifle] * 100;
        var (most, firstThem, maxThem) = (0, 0L, 0L);
        for (var t = 0; t < 400; t++)
        {
            w.Step();
            if (a.Mind is { Calling: true } m && w.Squads.Contains(a))
            {
                (firstThem, maxThem) = (firstThem == 0 ? m.FThem : firstThem, Math.Max(maxThem, m.FThem));
                most = Math.Max(most, near.Count(h => h.Mind!.HelpOf == a.Id));
            }
        }
        output.WriteLine($"A's call: F(them) first {firstThem}, most {maxThem} ({maxThem * 10 / man / 10.0} rifles in range); most helpers at once {most}; assigned {w.CallStats.Assigned}");
        Assert.True(maxThem >= 12 * man, $"F(them) {maxThem}: the group whole at full firepower");
        Assert.True(most >= 3, $"most helpers {most}");
    }

    // INF-HELP-2, the coordinator's call 2 (13.2's Hold exception as written, content holdAnswers): D, in Hold mode with its spot 8
    // cells from A's call (at A's centre), answers at U 60 or more (not at 50); E, its spot 13 cells off, never; the call withdrawn, D stands down,
    // walks back and holds its spot again, facing as it did. Before (on bd9b744) a squad in Hold mode answered nothing.
    [Fact]
    public void AHeldSquadHelpsACloseNeighbourAndHoldsAgain()
    {
        var w = New(100);
        var a = Form(w, Men(w, 1, 50, 48, 4));
        var d = Form(w, Men(w, 1, 56, 47, 4));
        var e = Form(w, Men(w, 1, 48, 60, 4));
        Steps(w, 40);
        Issue(w, d, CommandKind.SetSquadMode, new Cell(56, 49), index: (int)SquadMode.Hold, cell2: new Cell(76, 49));
        Issue(w, e, CommandKind.SetSquadMode, new Cell(48, 61), index: (int)SquadMode.Hold, cell2: new Cell(68, 61));
        Steps(w, 80);
        Assert.Equal((SquadMode.Hold, SquadMode.Hold, OrderKind.Idle), (d.Mode, e.Mode, d.Order));
        var facing = d.HoldFacing;
        Call(w, a, 50);
        ToMatch(w);
        Assert.Equal(0u, d.Mind!.HelpOf); // U under 60: it holds
        Call(w, a, 70);
        ToMatch(w);
        output.WriteLine($"U 70: d helps {d.Mind.HelpOf} mode {d.Mode} order {d.Order} ({w.HelpTest(d, d.Mind, a, w.Content.InfantryCombat!)}, spot {d.HoldSpot}, call at {a.Mind!.CallAt}); e helps {e.Mind!.HelpOf}");
        Assert.Equal(a.Id, d.Mind.HelpOf);
        Assert.Equal(0u, e.Mind.HelpOf);
        Assert.Equal(OrderKind.AttackMove, d.Order);
        a.Mind!.Calling = false; // the call withdrawn
        Steps(w, 700);
        var (x, y) = Centre(w, d);
        output.WriteLine($"after: d helps {d.Mind.HelpOf} mode {d.Mode} spot {d.HoldSpot} order {d.Order} centre ({x}, {y}) facing {d.HoldFacing} (was {facing})");
        Assert.Equal((0u, SquadMode.Hold, new Cell(56, 49), OrderKind.Idle), (d.Mind.HelpOf, d.Mode, d.HoldSpot, d.Order));
        Assert.True((x - 56) * (x - 56) + (y - 49) * (y - 49) <= 9, $"D back at its spot: ({x}, {y})");
        Assert.Equal(facing, d.HoldFacing);
    }

    // INF-HELP-2, the coordinator's call 3 (13.2: "a squad (or loose soldier)"): loose soldiers answer calls within 12 cells, each
    // counting his own firepower: L1 (idle, 8 cells off) and L3 (on the player's Attack-move, 10 cells off) attack-move to support
    // positions, L2 (idle, 16 cells off) stays; the call withdrawn, L3 goes on with his Attack-move at once and L1 stands down and walks
    // back. Before (on bd9b744) no loose soldier answered.
    [Fact]
    public void ALooseSoldierAnswersAndGoesOn()
    {
        var w = New(100);
        var a = Form(w, Men(w, 1, 50, 48, 4));
        var l1 = w.Spawn(1, "Rifleman", 50, 58, Facing.East);
        var l2 = w.Spawn(1, "Rifleman", 50, 66, Facing.East);
        var l3 = w.Spawn(1, "Rifleman", 42, 55, Facing.East);
        Steps(w, 40);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AttackMove, Units = [l3.Id], Cell = new Cell(42, 98) });
        Steps(w, 3);
        var goal3 = l3.Order.Cell;
        Assert.Equal(OrderKind.AttackMove, l3.Order.Kind);
        var rules = w.Content.InfantryCombat!;
        Call(w, a);
        var before = w.CallNeed(a, rules).Need;
        ToMatch(w);
        var after = w.CallNeed(a, rules).Need;
        output.WriteLine($"l1 {l1.Order.Kind} to {l1.Order.Cell}; l2 {l2.Order.Kind}; l3 {l3.Order.Kind} to {l3.Order.Cell} (goal {goal3}); need {before} -> {after}; assigned {w.CallStats.Assigned}");
        Assert.Equal(OrderKind.AttackMove, l1.Order.Kind);
        Assert.Equal(OrderKind.Idle, l2.Order.Kind);
        Assert.Equal(OrderKind.AttackMove, l3.Order.Kind);
        Assert.NotEqual(goal3, l3.Order.Cell);
        Assert.True(after < before, "their own firepower counted");
        a.Mind!.Calling = false; // the call withdrawn
        ToMatch(w);
        Assert.Equal((OrderKind.AttackMove, goal3), (l3.Order.Kind, l3.Order.Cell));
        Steps(w, 500);
        var back = (l1.Pos - new Cell(50, 58).Center()).Len();
        output.WriteLine($"after: l1 {l1.Order.Kind} {back} cells from where he answered; l3 at {Cell.Of(l3.Pos)}");
        Assert.True(back <= Fixed.FromInt(2), $"L1 walked back: {back}");
        Assert.True(l3.Pos.Y > Fixed.FromInt(60), "L3 goes on south");
    }

    // ENG-SMALL-3 (13.4 for a loose helper; INF-HELP-2 row 1342's left): a loose soldier who answers raises the "moving to support"
    // alert (a Support event with squad 0, the soldier its unit) and his support position follows the fight as a squad helper's does
    // (TheSupportPositionFollowsTheEnemyGroup): kept while the caller's enemy group moves 2 cells, picked again round its new centre
    // (4 to 8 steps off the back of A's axis, at 70% of his rifle's range) once it has moved 4, and he attack-moves there. On
    // c7995b3 he made no alert and his cell stayed where it was picked at the answer.
    [Fact]
    public void ALooseHelpersSupportPositionFollowsTheEnemyGroup()
    {
        var w = New(100);
        var a = Form(w, Men(w, 1, 50, 48, 4));
        var l = w.Spawn(1, "Rifleman", 50, 58, Facing.East);
        Steps(w, 40);
        var group = new Cell(50, 40);
        void CallNow()
        {
            var m = a.Mind!;
            var (x, y) = Centre(w, a);
            (m.Calling, m.Urgency, m.CallSeen, m.CallAt, m.FThem, m.FUs, m.GroupAt, m.GroupCount, m.Axis) = (true, 70, w.Tick, new Cell(x, y), 1_000_000, 0, group, 1, 24);
        }
        var alerts = 0;
        for (var i = 0; i < 60 && w.LooseSupport(l) is null; i++)
        {
            CallNow();
            w.Step();
            alerts += w.SquadEvents.Count(e => e == new SquadEvent(0, 1, SquadEventKind.Support, l.Id, a.Id));
        }
        Assert.Equal(a.Id, w.LooseSupport(l)?.Of);
        Assert.Equal(1, alerts);
        var first = w.LooseSupport(l)!.Value.To;
        group = new Cell(52, 40); // 2 cells: kept
        for (var i = 0; i < 40; i++)
        {
            CallNow();
            w.Step();
        }
        Assert.Equal(first, w.LooseSupport(l)!.Value.To);
        group = new Cell(54, 40); // 4 cells from the pick: picked again
        for (var i = 0; i < 40; i++)
        {
            CallNow();
            w.Step();
        }
        var to = w.LooseSupport(l)!.Value.To;
        output.WriteLine($"first {first}, then {to} round {group}; his order {l.Order.Kind} to {l.Order.Cell}");
        Assert.NotEqual(first, to);
        Assert.Equal((OrderKind.AttackMove, to), (l.Order.Kind, l.Order.Cell));
        Assert.InRange(World.DirSteps(World.Dir32(to.X - group.X, to.Y - group.Y), 8), 4, 8); // A's axis north (24): back south (8)
        Assert.InRange((to.Center() - group.Center()).Len().Raw, 5L * Fixed.OneRaw / 2, 9L * Fixed.OneRaw / 2);
    }
}
