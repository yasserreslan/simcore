using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// INF-E1b-3 part 3: the squad's fighting withdrawal (rules 12.1 to 12.3), the hooks for breaking and routing (12.4: Shaken and
// Broken come with morale, E1b-4) and the consolidation after the fight (18). Called by ThinkTactics (SquadTacticsSystem.cs);
// the steps are LinePlace goals (Bounding) walked by FollowSlots, as the bounds' are.
public sealed partial class World
{
    // SquadBroken (12.4, brief 7.3): the squad's morale under 20. A hook: morale is E1b-4's, so no squad is Broken yet.
    private static bool SquadBroken(Squad s, SquadMind m)
    {
        _ = (s, m);
        return false;
    }

    // SquadRouts (12.4): morale under routBelow 10: the men run to the safety point each on his own, backs turned, no fire. A hook
    // (E1b-4); while it is false a broken squad withdraws by elements like any other.
    private static bool SquadRouts(Squad s, SquadMind m)
    {
        _ = (s, m);
        return false;
    }

    // WantsWithdraw (10.3's "no order" rows): Broken, or R under autoWithdrawBelow with no withdrawal yet in this fight (once a fight,
    // until it has consolidated), not in a garrison, and 12.2 finding a point worth it; with none, no new look for withdrawRetryTicks.
    private bool WantsWithdraw(Squad s, SquadMind m, Cell centre, InfantryCombatRules r)
    {
        var broken = SquadBroken(s, m);
        if (!broken && (m.Ratio >= r[CombatValue.SqAutoWithdrawBelow] || m.Withdrew || Tick < m.WithdrawNoUntil))
        {
            return false;
        }
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Garrison: not 0 } && !broken)
            {
                return false;
            }
        }
        if (WithdrawPoint(s, m, centre, !broken, r) is not { } at)
        {
            m.WithdrawNoUntil = Tick + (uint)r[CombatValue.SqWithdrawRetryTicks];
            return false;
        }
        (m.WithdrawTo, m.Withdrew) = (at, m.Withdrew || !broken);
        return true;
    }

    // WithdrawPoint (12.2, the unordered withdrawal): the standable cell 2 cells to withdrawReach behind the line's centre (away from
    // the threat axis, withdrawWidth either side) where the line's first able member kneeling is least exposed (ties: nearest, then
    // the box's order); worth it only if the able members' mean exposure kneeling there is withdrawGainPercent of theirs where they
    // stand (out of every contact's reach there counts as 0). A broken squad (worth false) takes the same point without the test (12.2's safety, a building or the base, waits
    // for E1b-4).
    private Cell? WithdrawPoint(Squad s, SquadMind m, Cell centre, bool worth, InfantryCombatRules r)
    {
        if (m.LineAxis < 0)
        {
            return null;
        }
        Unit? rep = null;
        foreach (var p in m.LineList)
        {
            if (Unit(p.Unit) is { } u && Able(u))
            {
                rep = u;
                break;
            }
        }
        if (rep is null)
        {
            return null;
        }
        var (dx, dy) = InfantryCombatDirections((m.LineAxis + 16) & 31);
        var reach = r[CombatValue.SqWithdrawReach];
        var width = r[CombatValue.SqWithdrawWidth];
        Cell? best = null;
        long bestE = long.MaxValue, bestD = 0;
        var seen = 0;
        for (var a = 20; a <= reach; a += 10)
        {
            for (var t = -width; t <= width; t += 10)
            {
                var c = new Cell(centre.X + (int)((a * dx - t * dy) / 10240), centre.Y + (int)((a * dy + t * dx) / 10240));
                if (!Standable(c) || ++seen > r[CombatValue.PosMaxCandidates])
                {
                    continue;
                }
                var e = Exposure(rep, c, Posture.Kneel);
                var d = (c.Center() - centre.Center()).LenSq();
                if (best is null || e < bestE || (e == bestE && d < bestD))
                {
                    (best, bestE, bestD) = (c, e, d);
                }
            }
        }
        if (best is not { } at || !worth)
        {
            return best;
        }
        long here = 0, there = 0;
        foreach (var p in m.LineList)
        {
            if (Unit(p.Unit) is { } u && Able(u))
            {
                here += Exposure(u, Cell.Of(u.Pos), Posture.Kneel);
                there += Exposure(u, at, Posture.Kneel);
            }
        }
        return here > 0 && there * 100 <= here * r[CombatValue.SqWithdrawGainPercent] ? at : null; // (nothing to gain where nobody reaches them)
    }

    // StartWithdraw: an Advance's movers stop and drop at once (11.3 step 6), the flank or the assault ends; the element with the LMG
    // steps first (12.1 step 1: B, the LMG's, then they alternate).
    private void StartWithdraw(Squad s, SquadMind m)
    {
        EndBound(s, m, false);
        EndManoeuvres(s, m);
        (m.Last, m.WithdrawRefused) = (1, 0);
    }

    // Withdraw (12.1): never two elements moving; the stepping element's men each take the best cell (BoundGoal: cover toward his axis,
    // else the least exposed, else the nearest on his track) withdrawStepMin to Max behind him along the way back, withdrawAcross
    // either side, never past the point; the other element kneels and fires (ready, or readyTicks after the last step settled); a
    // step lasts withdrawStepTicks at most. The rear roles step with B. The men move away from their axis: the back rule (7.2) makes
    // it a backpedal while in reach. Ends at the point (every able man on its line or past it, half a cell) or after withdrawRefusals refused
    // steps in a row (12.1 step 6), in Fight in place there.
    private void Withdraw(Squad s, SquadMind m, InfantryCombatRules r)
    {
        var (dx, dy) = InfantryCombatDirections((m.LineAxis + 16) & 31);
        long Along(Cell c) => (c.X * 10L * dx + c.Y * 10L * dy) / 1024;
        long Across(Cell c) => (c.X * 10L * -dy + c.Y * 10L * dx) / 1024;
        if (m.Moving != 0)
        {
            var settled = true;
            foreach (var p in m.LineList)
            {
                if (p.Bounding && Unit(p.Unit) is { } u && Able(u))
                {
                    settled &= Cell.Of(u.Pos) == p.Goal && !u.Moving;
                }
            }
            if (settled || Tick - m.BoundTick >= (uint)r[CombatValue.SqWithdrawStepTicks])
            {
                EndBound(s, m, false);
            }
            return;
        }
        var end = Along(m.WithdrawTo);
        var there = true;
        foreach (var p in m.LineList)
        {
            there &= Unit(p.Unit) is not { } u || !Able(u) || Along(Cell.Of(u.Pos)) >= end - 5;
        }
        if (there)
        {
            EndWithdraw(s, m);
            return;
        }
        if (!m.LineList.Exists(p => p.Element != 0))
        {
            DealElements(s, m);
        }
        var next = (byte)(m.Last == 1 ? 2 : 1);
        if (!Ready(m, (byte)(3 - next), r) && Tick - m.SettledTick < (uint)r[CombatValue.SqReadyTicks])
        {
            return;
        }
        _lineTaken.Clear();
        foreach (var p in m.LineList)
        {
            _lineTaken.Add(p.Goal);
        }
        int wanted = 0, movers = 0;
        var (lo, hi, across) = (r[CombatValue.SqWithdrawStepMin], r[CombatValue.SqWithdrawStepMax], r[CombatValue.SqWithdrawAcross]);
        for (var i = 0; i < m.LineList.Count; i++)
        {
            var p = m.LineList[i];
            if ((p.Element != next && !(p.Element == 0 && next == 2)) || Unit(p.Unit) is not { } u || !Able(u) ||
                u.Stance is not (Stance.Guard or Stance.Aggressive))
            {
                continue;
            }
            var me = Cell.Of(u.Pos);
            var a = Along(me);
            if (a >= end - 5)
            {
                continue; // at the point's line already
            }
            wanted++;
            var top = SquadRouts(s, m) ? end : Math.Min(a + hi, end); // (a routing man goes for the point at once: the hook)
            if (BoundGoal(u, p, (m.LineAxis + 16) & 31, Math.Min(a + lo, top), top, Across(me), across, Along, Across, r) is not { } g)
            {
                continue; // no way back inside his step box (12.1 step 6)
            }
            _lineTaken.Add(g);
            movers++;
            m.LineList[i] = p with { Goal = g, Bounding = true };
        }
        if (movers == 0)
        {
            (m.Last, m.SettledTick) = (next, Tick);
            if (wanted > 0 && ++m.WithdrawRefused >= r[CombatValue.SqWithdrawRefusals])
            {
                EndWithdraw(s, m); // two refused steps in a row: it fights in place where it is
            }
            return;
        }
        m.WithdrawRefused = 0;
        (m.Moving, m.BoundTick) = (next, Tick);
        TacticsStats.Bounds++;
    }

    // EndWithdraw (12.3): the squad fights in place where it is; with no order its post moves there (18 step 6: it stays at its
    // withdrawal point).
    private void EndWithdraw(Squad s, SquadMind m)
    {
        EndBound(s, m, false);
        (m.Choice, m.ChoiceTick, m.ChoiceR, m.WithdrawRefused) = (SquadChoice.FightInPlace, Tick, 0, 0);
        if (s.Order == OrderKind.Idle)
        {
            ReAnchor(s);
        }
    }

    // Consolidates (18): the squad out of contact for calmTicks after a fight holds its line, facing the last threat axis, for
    // consolidateTicks with no order (Hold, Ambush and Guard too) or consolidateOrderTicks under an Attack-move, then calms (Release:
    // an unordered squad walks back to its post; one that withdrew stays, its post moved). Under a Move or any other order with a
    // goal it goes on at once. Top-ups need nothing here: an idle soldier already tops up 3 s after his last shot (ArmsSystem.cs).
    // The end clears 12.2's "once a fight". True while it consolidates.
    private bool Consolidates(Squad s, SquadMind m, InfantryCombatRules r)
    {
        if (m.Choice == SquadChoice.Consolidate)
        {
            if (Tick < m.ConsolidateUntil)
            {
                return true;
            }
            (m.ConsolidateUntil, m.Withdrew) = (0, false);
            return false;
        }
        var ticks = s.Order is OrderKind.Idle or OrderKind.Guard ? r[CombatValue.SqConsolidateTicks] :
            s.Order == OrderKind.AttackMove ? r[CombatValue.SqConsolidateOrderTicks] : 0;
        if (ticks == 0 || !m.HasLine || (m.Choice == SquadChoice.None && !m.OpenedFire))
        {
            m.Withdrew = false;
            return false;
        }
        EndBound(s, m, true);
        if (m.FlankStage != 0 || m.AssaultTick != 0)
        {
            MoversHere(m);
            EndManoeuvres(s, m);
        }
        (m.Choice, m.ChoiceTick, m.ChoiceR, m.ConsolidateUntil) = (SquadChoice.Consolidate, Tick, 0, Tick + (uint)ticks);
        return true;
    }
}
