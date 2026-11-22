using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-3 part 3: the fighting withdrawal (battle rules 12.1 to 12.3, scene 9) and the consolidation after a fight (18).
public class SquadWithdrawTests(ITestOutputHelper output)
{
    private static World New(int size = 48, ulong seed = 1, ContentSet? content = null)
    {
        var w = Arena.New(size, seed, content: content);
        w.CombatOff = false;
        return w;
    }

    private static Squad Form(World w, params Unit[] units)
    {
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.FormSquad, Units = [.. units.Select(u => u.Id)] });
        w.Step();
        return w.SquadOf(units[0].Id)!;
    }

    private static Unit[] Men(World w, byte player, int x, int y0, int n, int dy = 1, string name = "Rifleman") =>
        [.. Enumerable.Range(0, n).Select(i => w.Spawn(player, name, x, y0 + i * dy))];

    [Fact]
    public void TheBlockLoadsTheWithdrawalAndConsolidationNumbers()
    {
        var r = New().Content.InfantryCombat!;
        Assert.Equal((20, 30, 15, 60, 80, 70, 2, 200, 60, 60, 9),
            (r[CombatValue.SqWithdrawStepMin], r[CombatValue.SqWithdrawStepMax], r[CombatValue.SqWithdrawAcross], r[CombatValue.SqWithdrawStepTicks],
             r[CombatValue.SqWithdrawReach], r[CombatValue.SqWithdrawGainPercent], r[CombatValue.SqWithdrawRefusals], r[CombatValue.SqConsolidateTicks],
             r[CombatValue.SqConsolidateOrderTicks], r[CombatValue.SqWithdrawRescueWaitTicks], r[CombatValue.SqMoveBehindSteps]));
    }

    // 12.1, 12.2 (scene 9's shape): three idle rifles against twelve in reach (R under 50) give ground by elements to a point worth it
    // behind them: never both elements stepping, the squad ends further back in Fight in place, its post moved there, and it doesn't
    // withdraw a second time in the fight (once a fight).
    [Fact]
    public void AnOutgunnedIdleSquadWithdrawsByElementsOnce()
    {
        var w = New();
        var blue = Men(w, 1, 20, 20, 3, 2);
        Unit[] red = [.. Men(w, 2, 25, 16, 6, 2), .. Men(w, 2, 26, 17, 6, 2)];
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        var s = Form(w, blue);
        var post = s.Anchor;
        int withdrawals = 0, both = 0, steps = 0;
        var was = SquadChoice.None;
        var minX = long.MaxValue;
        static long mxNow(Unit[] b) => b.Where(u => u.Alive).Select(u => u.Pos.X.Raw).DefaultIfEmpty(long.MaxValue).Max();
        for (var i = 0; i < 900; i++)
        {
            w.Step();
            var m = s.Mind!;
            if (m.Choice == SquadChoice.Withdraw && was != SquadChoice.Withdraw)
            {
                withdrawals++;
            }
            if (m.Choice == SquadChoice.Withdraw)
            {
                both += m.Line.Where(p => p.Bounding && p.Element != 0).Select(p => p.Element).Distinct().Count() > 1 ? 1 : 0;
                steps += m.Moving != 0 ? 1 : 0;
            }
            was = m.Choice;
            if (i % 60 == 0)
            {
                output.WriteLine($"{w.Tick} ratio {m.Ratio} choice {m.Choice} moving {m.Moving} to {m.WithdrawTo} men {string.Join(" ", blue.Select(u => Cell.Of(u.Pos)))}");
            }
            minX = Math.Min(minX, mxNow(blue));
        }
        var mx = blue.Where(u => u.Alive).Select(u => u.Pos.X.Raw).DefaultIfEmpty(0).Max();
        output.WriteLine($"withdrawals {withdrawals} both {both} step-ticks {steps} choice {s.Mind!.Choice} ratio {s.Mind.Ratio} post {post} -> {s.Anchor} rear man x {mx / (double)Fixed.OneRaw:F1}");
        Assert.Equal(1, withdrawals);
        Assert.Equal(0, both);
        Assert.True(steps > 0, "the elements stepped");
        Assert.NotEqual(SquadChoice.Withdraw, s.Mind.Choice);
        Assert.True(s.Anchor.X.Raw <= post.X.Raw - Fixed.OneRaw / 2, $"its post moved back with it ({post} -> {s.Anchor})");
        Assert.True(mx < 20L * Fixed.OneRaw, "every man gave ground"); // out of the rifles' reach: the least exposed cell behind
    }

    // 12.2: no point worth it (the ground behind the squad is all wall): it doesn't withdraw, it fights in place.
    [Fact]
    public void NoWithdrawalWithNoWayBack()
    {
        var w = New();
        for (var x = 11; x <= 19; x++)
        {
            for (var y = 10; y <= 32; y++)
            {
                w.Grid.Set(new Cell(x, y), Terrain.Blocked);
            }
        }
        var blue = Men(w, 1, 20, 20, 3, 2);
        Unit[] red = [.. Men(w, 2, 25, 16, 6, 2), .. Men(w, 2, 26, 17, 6, 2)];
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        var s = Form(w, blue);
        var seen = false;
        var low = false;
        for (var i = 0; i < 300; i++)
        {
            w.Step();
            if (!seen && s.Mind!.Choice == SquadChoice.Withdraw)
            {
                output.WriteLine($"withdraw at {w.Tick} to {s.Mind.WithdrawTo} axis {s.Mind.LineAxis} ratio {s.Mind.Ratio}");
            }
            seen |= s.Mind!.Choice == SquadChoice.Withdraw;
            low |= s.Mind.InContact && s.Mind.Ratio < 50;
        }
        output.WriteLine($"low {low} ratio {s.Mind!.Ratio} choice {s.Mind.Choice}");
        Assert.True(low, "blue is outgunned in contact");
        Assert.False(seen);
    }

    // 18: an idle squad that fought holds its line, facing the last threat, for consolidateTicks once it has been out of contact for
    // calmTicks, then calms (its line goes); under a Move it goes on at once.
    [Fact]
    public void AnIdleSquadConsolidatesAfterTheFight()
    {
        var w = New();
        var blue = Men(w, 1, 20, 20, 4, 2);
        var red = w.Spawn(2, "Rifleman", 26, 23);
        red.HP = 1;
        var s = Form(w, blue);
        uint start = 0, end = 0;
        var lineHeld = true;
        for (var i = 0; i < 800 && end == 0; i++)
        {
            w.Step();
            var m = s.Mind!;
            if (m.Choice == SquadChoice.Consolidate && start == 0)
            {
                start = w.Tick;
            }
            if (start != 0 && m.Choice == SquadChoice.Consolidate)
            {
                lineHeld &= m.HasLine;
            }
            if (start != 0 && m.Choice != SquadChoice.Consolidate)
            {
                end = w.Tick;
            }
        }
        output.WriteLine($"red alive {red.Alive}, consolidated {start} to {end}, choice {s.Mind!.Choice}, line {s.Mind.HasLine}");
        Assert.False(red.Alive);
        Assert.NotEqual(0u, start);
        Assert.True(lineHeld, "the line holds while it consolidates");
        Assert.InRange(end - start, 195u, 215u); // 200 ticks, on the squad's think cadence
        Assert.Equal(SquadChoice.None, s.Mind.Choice);
        Assert.False(s.Mind.HasLine);
    }

    // Scene 9 as written (INF-E1b-7, row 1320; it replaces rows 1307 and 1316's red 12 holding fire): blue (4) idle, red (8) attacks:
    // two squads of 4 Attack-move onto blue from 11 cells, closing and firing; a wall line 6 cells behind blue with gaps every 4 cells.
    // Red's closing men count in R from the first think (row 1319), so blue is outgunned at once and gives ground: the point is behind
    // the wall, protected toward red; blue ends there in Fight in place, every man in cover toward the threat; never both elements
    // stepping inside reach; no back turned inside reach (a back turned only out of reach: beyond 1.2 x range, 6 cells, or unspotted
    // 2 s); out of reach a man runs to the point at once, not by 2-3 cell steps; and blue has lost fewer men 20 s into the fight than
    // in the same scene with autoWithdrawBelow 0.
    [Fact]
    public void SceneNineWithdrawsBehindTheWallNotJustOutOfReach()
    {
        var (w, atEnd, point, ended, after, both, backTurned, straight, axis, lost) = SceneNine(null);
        var (_, _, broken, _, _, _, _, _, _, lostStaying) = SceneNine(NoAutoWithdraw.Value);
        output.WriteLine($"point {point} ended {ended} choice {after} both {both} back {backTurned} straight {straight} lost {lost} (autoWithdrawBelow 0: {lostStaying}, broken to {broken}) men at the end {string.Join(" ", atEnd.Select(c => $"{c}:{w.Protection(c, Posture.Kneel, axis, false)}"))}");
        Assert.NotNull(point);
        Assert.True(point!.Value.X <= 13 && w.Protection(point.Value, Posture.Kneel, axis, false) > 0, $"the point is behind the wall ({point})");
        Assert.NotEqual(0, ended);
        Assert.Equal(SquadChoice.FightInPlace, after);
        Assert.NotEmpty(atEnd);
        Assert.All(atEnd, c => Assert.True(c.X <= 13 && w.Protection(c, Posture.Kneel, axis, false) > 0, $"{c} in cover"));
        Assert.Equal(0, both);
        Assert.Equal(0, backTurned);
        Assert.True(straight > 0, "out of reach they ran to the point");
        Assert.True(lost < lostStaying, $"fewer men lost 20 s in: {lost} against {lostStaying} staying");
    }

    // 12.4, 12.2 (INF-E1b-7, row 1318, the coordinator's call 1 on row 1316): a broken squad with no order and no building near goes to
    // the nearest hard cover away from the threat, here behind a wall line 9 cells back, beyond the 8 cells an unordered withdrawal
    // looks (12.2's R-under-50 point), and stays there in Fight in place. Before, it took that point's bare ground 2 to 8 cells back
    // as its safety and stayed there in the open, in Fight in place, until it rallied (or died: row 1316's blue at t164).
    [Fact]
    public void ABrokenSquadGoesBackToHardCoverBeyondItsLine()
    {
        var w = New();
        for (var y = 10; y <= 32; y++)
        {
            if (y % 4 != 1)
            {
                w.Grid.Set(new Cell(14, y), Terrain.Blocked);
            }
        }
        var blue = Men(w, 1, 23, 18, 4, 2);
        Unit[] red = [.. Men(w, 2, 28, 14, 6, 2), .. Men(w, 2, 29, 15, 6, 2)];
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        var s = Form(w, blue);
        Cell? point = null;
        var axis = -1;
        var (after, safe) = (SquadChoice.None, false);
        for (var i = 0; i < 900; i++)
        {
            if (s.Mind is { } bm && (bm.InContact || bm.Spirit == MoraleState.Broken))
            {
                bm.Morale100 = 1500; // broken from the first contact on (12.4), not routing
            }
            w.Step();
            var m = s.Mind!;
            if (m.Choice == SquadChoice.Withdraw)
            {
                point ??= m.WithdrawTo;
                axis = m.LineAxis;
            }
            else if (point is not null && after == SquadChoice.None)
            {
                (after, safe) = (m.Choice, m.BrokenSafe); // the withdrawal's end: Fight in place at its safety (12.3), then 18's consolidation
            }
        }
        var mind = s.Mind!;
        output.WriteLine($"point {point} then {after} safe {safe}, at the end {mind.Choice} spirit {mind.Spirit} safe {mind.BrokenSafe} men {string.Join(" ", blue.Select(u => $"{Cell.Of(u.Pos)}:{w.Protection(Cell.Of(u.Pos), Posture.Kneel, axis, false)}"))}");
        Assert.Equal(MoraleState.Broken, mind.Spirit);
        Assert.NotNull(point);
        Assert.True(point!.Value.X <= 13 && w.Protection(point.Value, Posture.Kneel, axis, false) > 0, $"its safety is the hard cover behind the wall ({point})");
        Assert.True(safe && mind.BrokenSafe, "it got there and stays");
        Assert.True(after is SquadChoice.FightInPlace or SquadChoice.Consolidate, $"it ended in Fight in place ({after})");
        Assert.All(blue.Where(u => u.Alive), u => Assert.True(Cell.Of(u.Pos).X <= 13 && w.Protection(Cell.Of(u.Pos), Posture.Kneel, axis, false) > 0, $"{Cell.Of(u.Pos)} in cover"));
    }

    private static readonly Lazy<ContentSet> NoAutoWithdraw = new(() => ContentLoader.Load(TestContent.MvpSources().Select(s => s.Name.EndsWith("infantry_combat.json", StringComparison.Ordinal)
        ? s with { Json = s.Json.Replace("\"autoWithdrawBelow\": 50", "\"autoWithdrawBelow\": 0", StringComparison.Ordinal) } : s)));

    private (World W, Cell[] AtEnd, Cell? Point, int Ended, SquadChoice After, int Both, int BackTurned, int Straight, int Axis, int Lost) SceneNine(ContentSet? content)
    {
        var w = New(content: content);
        for (var y = 10; y <= 32; y++)
        {
            if (y % 4 != 1)
            {
                w.Grid.Set(new Cell(14, y), Terrain.Blocked);
            }
        }
        var blue = Men(w, 1, 20, 18, 4, 2);
        var s = Form(w, blue);
        Unit[] red1 = Men(w, 2, 31, 15, 4, 2), red2 = Men(w, 2, 32, 16, 4, 2);
        Form(w, red1);
        Form(w, red2);
        Unit[] red = [.. red1, .. red2];
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackMove, Units = [.. red.Select(u => u.Id)], Cell = new Cell(17, 21) });
        Cell? point = null;
        int both = 0, backTurned = 0, straight = 0, ended = 0, axis = -1, lost = -1;
        uint contact = 0;
        var after = SquadChoice.None;
        Cell[] atEnd = [];
        for (var i = 0; i < 1200; i++)
        {
            w.Step();
            var m = s.Mind!;
            contact = contact == 0 && m.InContact ? w.Tick : contact;
            if (contact != 0 && w.Tick == contact + 20 * World.TicksPerSecond)
            {
                lost = blue.Count(u => !u.Alive || u.Life is { Wound: WoundState.Down });
            }
            if (m.Choice == SquadChoice.Withdraw)
            {
                if (point is null)
                {
                    output.WriteLine($"withdraw at {w.Tick} ratio {m.Ratio} to {m.WithdrawTo} (contact at {contact})");
                }
                point ??= m.WithdrawTo;
                axis = m.LineAxis;
                both += m.Line.Where(p => p.Bounding && p.Element != 0 && w.Unit(p.Unit) is { Fight.OutOfReach: false }).Select(p => p.Element).Distinct().Count() > 1 ? 1 : 0;
                straight += m.Line.Count(p => p.Bounding && w.Unit(p.Unit) is { Fight.OutOfReach: true } u && Math.Abs(p.Goal.X - Cell.Of(u.Pos).X) > 3);
            }
            else if (point is not null && ended == 0)
            {
                (ended, after) = ((int)w.Tick, m.Choice);
            }
            // (row 1307: from the withdrawal's start, the scene's pass line: before it, a man stepping into his band toward red, 6.8,
            // turns to face him on his first ticks of walk; row 1320: until it ends, the withdrawal's steps: the fight at the wall after
            // it is section 6's moves)
            foreach (var u in blue)
            {
                if (point is not null && ended == 0 && u.Alive && u.Fight is { InContact: true, OutOfReach: false, Axis: >= 0 } f && World.Walking(u))
                {
                    backTurned += World.DirSteps(World.FacingToDir(u.Facing), f.Axis) > 8 ? 1 : 0;
                }
            }
            if (ended == w.Tick)
            {
                atEnd = [.. blue.Where(u => u.Alive && u.Life is not { Wound: WoundState.Down }).Select(u => Cell.Of(u.Pos))];
            }
            if (i % 100 == 0)
            {
                output.WriteLine($"{w.Tick} ratio {m.Ratio} contact {m.InContact} choice {m.Choice} blue {blue.Count(u => u.Alive)} red {red.Count(u => u.Alive)}");
            }
        }
        return (w, atEnd, point, ended, after, both, backTurned, straight, axis, lost);
    }

    // 12.2 in the open (the coordinator's call): with no cover in the band the point is just out of the enemy's reach, a cell or two.
    [Fact]
    public void InTheOpenThePointIsJustOutOfReach()
    {
        var w = New();
        var blue = Men(w, 1, 20, 20, 3, 2);
        Unit[] red = [.. Men(w, 2, 25, 16, 6, 2), .. Men(w, 2, 26, 17, 6, 2)];
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        var s = Form(w, blue);
        Cell? point = null;
        for (var i = 0; i < 300 && point is null; i++)
        {
            w.Step();
            point = s.Mind!.Choice == SquadChoice.Withdraw ? s.Mind.WithdrawTo : null;
        }
        output.WriteLine($"point {point} centre {s.Mind!.LineCentre}");
        Assert.NotNull(point);
        Assert.InRange(s.Mind.LineCentre.X - point!.Value.X, 1, 3);
    }

    // 12.2: no point worth it: the outgunned squad fights in place with its men prone (9.1), even with nobody firing at them yet.
    [Fact]
    public void WithNoPointWorthItTheMenGoProne()
    {
        var w = New();
        for (var x = 11; x <= 19; x++)
        {
            for (var y = 10; y <= 32; y++)
            {
                w.Grid.Set(new Cell(x, y), Terrain.Blocked);
            }
        }
        var blue = Men(w, 1, 20, 20, 3, 2);
        Unit[] red = [.. Men(w, 2, 25, 16, 6, 2), .. Men(w, 2, 26, 17, 6, 2)];
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        Form(w, blue);
        for (var i = 0; i < 200; i++)
        {
            w.Step();
        }
        output.WriteLine(string.Join(" ", blue.Select(u => $"{u.Id}:{u.Fight!.Posture}")));
        Assert.All(blue, u => Assert.Equal(Posture.Prone, u.Fight!.Posture));
    }

    // 12.1 step 5: a squad withdrawing on its own waits up to 3 s for a rescue to start before it leaves a downed man.
    [Fact]
    public void ItWaitsThreeSecondsBeforeLeavingADownedMan()
    {
        var w = New();
        var blue = Men(w, 1, 20, 18, 4, 2);
        Unit[] red = [.. Men(w, 2, 25, 16, 6, 2), .. Men(w, 2, 26, 17, 6, 2)];
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        var s = Form(w, blue);
        blue[3].HP = 4;
        uint chose = 0, stepped = 0;
        for (var i = 0; i < 400 && stepped == 0; i++)
        {
            w.Step();
            chose = chose == 0 && s.Mind!.Choice == SquadChoice.Withdraw ? w.Tick : chose;
            stepped = chose != 0 && s.Mind!.Moving != 0 ? w.Tick : 0;
        }
        output.WriteLine($"down {blue[3].Life!.Wound} chose {chose} stepped {stepped}");
        Assert.Equal(WoundState.Down, blue[3].Life!.Wound);
        Assert.NotEqual(0u, chose);
        Assert.InRange(stepped - chose, 60u, 80u);
    }

    // 10.3's Move rows (part 4): a Move whose goal is behind the squad in contact is a fighting withdrawal toward it, backs never
    // turned in reach; out of reach the move goes on and the squad gets there.
    [Fact]
    public void AMoveWithTheGoalBehindWithdrawsThenGoesOn()
    {
        var w = New();
        var blue = Men(w, 1, 20, 20, 3, 2);
        Unit[] red = [.. Men(w, 2, 25, 16, 6, 2), .. Men(w, 2, 26, 17, 6, 2)];
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        var s = Form(w, blue);
        for (var i = 0; i < 40; i++)
        {
            w.Step();
        }
        Assert.True(s.Mind!.InContact);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [.. blue.Select(u => u.Id)], Cell = new Cell(8, 22) });
        int withdrew = 0, backTurned = 0;
        for (var i = 0; i < 900; i++)
        {
            w.Step();
            withdrew += s.Mind!.Choice == SquadChoice.Withdraw ? 1 : 0;
            foreach (var u in blue)
            {
                if (u.Alive && u.Fight is { InContact: true, OutOfReach: false, Axis: >= 0 } f && World.Walking(u))
                {
                    backTurned += World.DirSteps(World.FacingToDir(u.Facing), f.Axis) > 8 ? 1 : 0;
                }
            }
        }
        var far = blue.Max(u => Math.Abs(Cell.Of(u.Pos).X - 8));
        output.WriteLine($"withdrew {withdrew} back {backTurned} choice {s.Mind!.Choice} men {string.Join(" ", blue.Select(u => Cell.Of(u.Pos)))}");
        Assert.True(withdrew > 0, "the squad withdrew toward its goal");
        Assert.Equal(0, backTurned);
        Assert.True(far <= 3, "it got there");
    }
}
