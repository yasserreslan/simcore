using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-3 part 3: the fighting withdrawal (battle rules 12.1 to 12.3, scene 9) and the consolidation after a fight (18).
public class SquadWithdrawTests(ITestOutputHelper output)
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

    private static Unit[] Men(World w, byte player, int x, int y0, int n, int dy = 1, string name = "Rifleman") =>
        [.. Enumerable.Range(0, n).Select(i => w.Spawn(player, name, x, y0 + i * dy))];

    [Fact]
    public void TheBlockLoadsTheWithdrawalAndConsolidationNumbers()
    {
        var r = New().Content.InfantryCombat!;
        Assert.Equal((20, 30, 15, 60, 80, 70, 2, 200, 60),
            (r[CombatValue.SqWithdrawStepMin], r[CombatValue.SqWithdrawStepMax], r[CombatValue.SqWithdrawAcross], r[CombatValue.SqWithdrawStepTicks],
             r[CombatValue.SqWithdrawReach], r[CombatValue.SqWithdrawGainPercent], r[CombatValue.SqWithdrawRefusals], r[CombatValue.SqConsolidateTicks],
             r[CombatValue.SqConsolidateOrderTicks]));
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
}
