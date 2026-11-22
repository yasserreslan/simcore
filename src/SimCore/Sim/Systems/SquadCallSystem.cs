using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// INF-E1b-4 part 2: units help each other (rules 13). A squad in contact makes a call at its first contact think and refreshes it every
// squad think: its centre, the urgency U (13.1's table), F(them) and F(us) from the force ratio (10.2), anti-armour wanted. Every
// matchEvery ticks one pass matches the open calls, U high first (ties: squad ID), to free squads of the same player within callRadius,
// nearest first, until the need (F(them) x needPercent - F(us) - the helpers' own) is met or maxHelpers are sent (13.3). A helper
// attack-moves (INF-MOVE's coherent squad move, at Run until it is in reach of a known enemy or shot at) to its support position off
// the caller's threat axis round the enemy group (13.4); there it is a squad in contact like any other. "Help arrived": the caller's
// +5 morale once, at a helper's first shot within arrivedReach of it. The call withdrawn: the helper stands down (consolidates for
// standDownTicks), then walks back to where it answered and faces as it did. Help makes no call of its own, unless it meets a
// different enemy beyond otherFight from its caller (then it is a squad in contact like any other). A player's order ends the help.
// 13.6 (contacts without a call) is the hearing scan's: a squad that hears or sees the fight is in contact and acts on its own.
public sealed partial class World
{
    private readonly List<Squad> _calls = []; // scratch: the open calls of a pass, sorted
    private readonly List<Unit> _helpUnits = []; // scratch: a helper's members for its squad move

    internal (long Calls, long Assigned, long Released, long Arrived) CallStats;

    // NoteCallLoss (13.1): a member downed or killed: U's +10 each for lossTicks, the last three kept.
    private void NoteCallLoss(SquadMind m)
    {
        (m.LossTicks[2], m.LossTicks[1], m.LossTicks[0]) = (m.LossTicks[1], m.LossTicks[0], Tick);
    }

    // QuietCall (13.1): a call is withdrawn after the squad has had no contact for withdrawTicks.
    private void QuietCall(SquadMind m, InfantryCombatRules r)
    {
        if (m.Calling && !m.InContact && Tick - m.CallSeen >= (uint)r[CombatValue.CaWithdrawTicks])
        {
            WithdrawCall(m);
        }
    }

    private static void WithdrawCall(SquadMind m) => (m.Calling, m.Urgency, m.CallArmour, m.FlankRefused) = (false, 0, false, false);

    // RefreshCall (13.1), at each squad think in contact after the force ratio: U from the table, clamped 0 to 100. A helper makes none
    // (help doesn't call for help) unless it is in contact beyond otherFight from its caller with an enemy group beyond otherFight of the
    // caller's (this job's reading of "a different enemy": a helper that hears the caller's fight from 20 cells is in the same one): then
    // it drops the help and calls.
    private void RefreshCall(Squad s, SquadMind m, Cell centre, InfantryCombatRules r)
    {
        if (r[CombatValue.CaAutoSupport] == 0)
        {
            return;
        }
        if (m.HelpOf != 0)
        {
            var far = (long)r[CombatValue.CaOtherFight] * Fixed.OneRaw / 10;
            if (Squad(m.HelpOf) is { Mind: { } cm } && ((centre.Center() - cm.CallAt.Center()).LenSq() <= far * far ||
                (m.GroupCount > 0 && (m.GroupAt.Center() - cm.GroupAt.Center()).LenSq() <= far * far)))
            {
                return; // (the same fight: in contact near the caller, or with the caller's enemy group)
            }
            EndHelp(s, m); // a different enemy: a squad in contact like any other
        }
        if (m.StandDownUntil != 0 || m.Returning)
        {
            (m.StandDownUntil, m.Returning) = (0, false); // in a fight of its own on the way back: it stays and fights
        }
        CallStats.Calls += m.Calling ? 0 : 1;
        (m.Calling, m.CallSeen, m.CallAt) = (true, Tick, centre);
        var u = r[CombatValue.CaUContact];
        u += m.Ratio < 100 ? r[CombatValue.CaURatio] : 0;
        u += m.Ratio < 50 ? r[CombatValue.CaURatioLow] : 0;
        int able = 0, pinned = 0, losses = 0;
        bool garrison = false, antiArmour = false;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is not { Alive: true } mu)
            {
                continue;
            }
            garrison |= mu.Garrison != 0;
            if (!Able(mu) || mu.Fight is not { } f)
            {
                continue;
            }
            able++;
            pinned += f.Suppression >= r[CombatValue.Pinned] ? 1 : 0;
            antiArmour |= HurtsArmour(mu, r);
        }
        u += able > 0 && pinned * 2 >= able ? r[CombatValue.CaUPinned] : 0;
        foreach (var t in m.LossTicks)
        {
            losses += t != 0 && Tick - t < (uint)r[CombatValue.CaLossTicks] ? 1 : 0;
        }
        u += Math.Min(losses * r[CombatValue.CaULoss], r[CombatValue.CaULossMax]);
        m.CallArmour = false;
        foreach (var k in m.PictureList)
        {
            m.CallArmour |= !antiArmour && k.Enemy != 0 && k.Class is CombatClass.Armour or CombatClass.LightVehicle;
        }
        u += m.CallArmour ? r[CombatValue.CaUArmour] : 0;
        u += m.FlankRefused ? r[CombatValue.CaUFlankRefused] : 0;
        u -= garrison ? r[CombatValue.CaUGarrison] : 0;
        m.Urgency = Math.Clamp(u, 0, 100);
    }

    // HurtsArmour: a launcher or a Kornet crew (13.1's anti-armour, 13.3's helpers taken first).
    private bool HurtsArmour(Unit u, InfantryCombatRules r)
    {
        if (SquadRoleOf(u) == SquadRole.LongFlank)
        {
            return true;
        }
        foreach (var w in u.Def.Weapons)
        {
            if (r.Weapon(w.Effect)?.Class == CombatClass.Launcher)
            {
                return true;
            }
        }
        return false;
    }

    // MatchCalls (13.3), on ticks where tick mod matchEvery is 0, before the squad thinks: calls by U high first, ties by squad ID; each
    // takes the nearest free helpers (anti-armour first when it asks for it) until its need is met or it has maxHelpers.
    private void MatchCalls(InfantryCombatRules r)
    {
        if (r[CombatValue.CaAutoSupport] == 0 || Tick % (uint)Math.Max(1, r[CombatValue.CaMatchEvery]) != 0)
        {
            return;
        }
        _calls.Clear();
        foreach (var s in _squads)
        {
            if (s.Mind is { Calling: true })
            {
                _calls.Add(s);
            }
        }
        if (_calls.Count == 0)
        {
            return;
        }
        _calls.Sort(static (a, b) => a.Mind!.Urgency != b.Mind!.Urgency ? b.Mind.Urgency.CompareTo(a.Mind.Urgency) : a.Id.CompareTo(b.Id));
        foreach (var c in _calls)
        {
            var cm = c.Mind!;
            var need = cm.FThem * r[CombatValue.CaNeedPercent] / 100 - cm.FUs;
            var count = 0;
            foreach (var h in _squads)
            {
                if (h.Mind is { } hm && hm.HelpOf == c.Id)
                {
                    need -= HelperForce(h, r);
                    count++;
                }
            }
            for (var pass = cm.CallArmour ? 0 : 1; pass < 2; pass++)
            {
                while (need > 0 && count < r[CombatValue.CaMaxHelpers] && NearestHelper(c, cm, pass == 0, r) is { } h)
                {
                    AssignHelper(h, c, cm, r);
                    need -= HelperForce(h, r);
                    count++;
                }
            }
        }
    }

    // NearestHelper (13.2): the nearest free squad (straight distance from its centre to the call's; ties by ID) of the caller's player.
    private Squad? NearestHelper(Squad c, SquadMind cm, bool antiArmour, InfantryCombatRules r)
    {
        var radius = (long)r[CombatValue.CaCallRadius] * Fixed.OneRaw / 10;
        Squad? best = null;
        var bestD = long.MaxValue;
        foreach (var h in _squads)
        {
            if (h == c || h.Owner != c.Owner || h.Mind is not { } hm || !FreeHelper(h, hm, c, r) || !SquadCentre(h, out var hc))
            {
                continue;
            }
            var d = (hc.Center() - cm.CallAt.Center()).LenSq();
            if (d > radius * radius || d >= bestD)
            {
                continue;
            }
            if (antiArmour)
            {
                var any = false;
                foreach (var id in h.MemberList)
                {
                    any |= Unit(id) is { } u && Able(u) && HurtsArmour(u, r);
                }
                if (!any)
                {
                    continue;
                }
            }
            (best, bestD) = (h, d);
        }
        return best;
    }

    // FreeHelper (13.2): no order of the player in progress (idle, or walking back from a help), its switch on (19.4, part 3), not
    // loose or retreating, not Hold
    // or Ambush, not in contact or calling, Steady, minAmmo% of its ammo on average, minAble able members, not answering or standing
    // down, and not released from this call in the last releaseTicks.
    private bool FreeHelper(Squad h, SquadMind hm, Squad c, InfantryCombatRules r)
    {
        if (!(h.Order == OrderKind.Idle || hm.Returning) || !h.AutoSupport || h.Loose || h.Retreating || h.Mode is SquadMode.Hold or SquadMode.Ambush ||
            hm.InContact || hm.Calling || hm.Spirit != MoraleState.Steady || hm.HelpOf != 0 || hm.StandDownUntil != 0 ||
            (hm.NotCall == c.Id && Tick < hm.NotCallUntil))
        {
            return false;
        }
        int able = 0;
        long have = 0, max = 0;
        foreach (var id in h.MemberList)
        {
            if (Unit(id) is not { } u || !Able(u))
            {
                continue;
            }
            able++;
            if (u.Def.Weapons.Count > 0 && u.Def.Weapons[0] is { Stock: >= 0, Effect.Carried: > 0 } w)
            {
                (have, max) = (have + u.Charges[w.Stock], max + u.StockMax(w.Stock));
            }
        }
        return able >= r[CombatValue.CaMinAble] && have * 100 >= max * r[CombatValue.CaMinAmmo];
    }

    // HelperForce (13.3): a helper's own firepower, x100 as F(us): each able member's weapon at its in-range value (10.2), wounded less.
    private long HelperForce(Squad h, InfantryCombatRules r)
    {
        long v = 0;
        foreach (var id in h.MemberList)
        {
            if (Unit(id) is not { } u || !Able(u) || u.Def.Weapons.Count == 0)
            {
                continue;
            }
            var w = u.Def.Weapons[u.Arms is { } a ? a.InHand : 0];
            long x = ForceValue(r.Weapon(w.Effect)?.Class, 0, Math.Max(1, w.Range.Raw), false, r);
            v += (u.Life is { Wound: WoundState.Wounded } ? x * r[CombatValue.SqWounded] / 100 : x) * 100;
        }
        return v;
    }

    // AssignHelper (13.3, 13.4): h answers c: it remembers where it was (unless walking back already) and goes to its support position.
    private void AssignHelper(Squad h, Squad c, SquadMind cm, InfantryCombatRules r)
    {
        var hm = h.Mind!;
        if (!hm.Returning)
        {
            (hm.HelpFrom, hm.HelpFacing) = (SquadCentre(h, out var from) ? from : Cell.Of(h.Anchor), h.Facing);
        }
        (hm.HelpOf, hm.HelpTick, hm.HelpNear, hm.HelpCredited, hm.Returning) = (c.Id, Tick, false, false, false);
        hm.HelpTo = SupportCell(h, cm, r);
        AutoOrder(h, hm, hm.HelpTo, OrderKind.AttackMove);
        hm.HelpRun = true; // 13.4: March at Run until in reach of a known enemy or shot at
        CallStats.Assigned++;
        _squadEvents.Add(new SquadEvent(h.Id, h.Owner, SquadEventKind.Support, h.Leader, c.Id));
    }

    // SupportCell (13.4): round the enemy group, at the helper's band from it (advanceToPercent of its line range), on the side of the
    // caller's line toward the helper, rotated off the caller's threat axis by supportSteps of the 32 (one more if that cell can't be
    // stood on); that side blocked (water, a cliff, the map's edge, or its straight way over flankMaxExposed% exposed: 11.5's test,
    // part 3): the other; both: level with the caller's line, lineSideMin to
    // lineSideMax to its side (the helper's side first).
    private Cell SupportCell(Squad h, SquadMind cm, InfantryCombatRules r)
    {
        var (g, cc) = (cm.GroupAt, cm.CallAt);
        var axis = cm.Axis >= 0 ? cm.Axis : Dir32(g.X - cc.X, g.Y - cc.Y);
        var hc = SquadCentre(h, out var at) ? at : Cell.Of(h.Anchor);
        var band = SquadLineRange(h) * r[CombatValue.SqAdvanceToPercent] / 100;
        var back = (axis + 16) & 31;
        Cell Point(int d, long len, Cell from)
        {
            var (dx, dy) = Directions[d & 31];
            return Cell.Of(new Vec(Fixed.FromRaw(from.Center().X.Raw + dx * len / 1024), Fixed.FromRaw(from.Center().Y.Raw + dy * len / 1024)));
        }
        long D2(Cell a, Cell b) => (a.Center() - b.Center()).LenSq();
        var lead = Unit(h.Leader);
        bool Exposed(Cell p) => lead is not null && WayExposure(lead, hc, p, r) is { } way && way.Exposed * 100 > way.N * r[CombatValue.SqFlankMaxExposed];
        var steps = r[CombatValue.CaSupportSteps];
        var first = D2(Point(back + steps, band, g), hc) <= D2(Point(back - steps, band, g), hc) ? 1 : -1;
        for (var side = 0; side < 2; side++)
        {
            var sgn = side == 0 ? first : -first;
            for (var k = steps; k <= steps + 1; k++)
            {
                var p = Point(back + sgn * k, band, g);
                if (Grid.Contains(p) && Standable(p))
                {
                    if (Exposed(p))
                    {
                        break; // part 3 (13.4, 11.5): the way there over flankMaxExposed% exposed: that side is blocked
                    }
                    return p;
                }
            }
        }
        var (ax, ay) = Directions[axis & 31];
        var right = (long)ax * (hc.Y - cc.Y) - (long)ay * (hc.X - cc.X) >= 0 ? 8 : -8;
        for (var side = 0; side < 2; side++)
        {
            for (var len = r[CombatValue.CaLineSideMin]; len <= r[CombatValue.CaLineSideMax]; len += 10)
            {
                var p = Point(axis + (side == 0 ? right : -right), (long)len * Fixed.OneRaw / 10, cc);
                if (Grid.Contains(p) && Standable(p))
                {
                    return p;
                }
            }
        }
        return cc;
    }

    // AutoOrder: the squad's own move (a helper's, 13.4): as a player's order would (its line and plan go, an urgent think) but it is
    // not one: the morale state's men keep their behaviour, and it doesn't end the help.
    private void AutoOrder(Squad s, SquadMind m, Cell goal, OrderKind kind)
    {
        if (m.HasLine)
        {
            ReAnchor(s);
        }
        ClearTactics(s, m);
        (m.Urgent, m.OrderNew) = (true, true);
        _helpUnits.Clear();
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Alive: true } u)
            {
                _helpUnits.Add(u);
            }
        }
        OrderSquad(s, goal, kind, _helpUnits);
    }

    // StopAutoOrder: the help's move ends where the squad stands (the call withdrawn, or a fight of its own).
    private void StopAutoOrder(Squad s)
    {
        if (s.Order == OrderKind.Idle)
        {
            return;
        }
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { } u && u.Order.Kind == s.Order && u.Order.Cell == s.Goal)
            {
                (u.Order, u.MoveOn) = (default, false);
            }
        }
        s.Order = OrderKind.Idle;
        ClearSquadPath(s);
        ReAnchor(s);
        s.FinalsStale = true;
    }

    // EndHelp: the help ends (the helper is a squad like any other where it stands).
    private void EndHelp(Squad s, SquadMind m)
    {
        if (s.Order == OrderKind.AttackMove && s.Goal == m.HelpTo)
        {
            StopAutoOrder(s);
        }
        (m.HelpOf, m.HelpRun) = (0, false);
    }

    // HelpEndsByOrder (13.4): a player's order at any time ends the help at once (no walk back).
    private static void HelpEndsByOrder(SquadMind m) => (m.HelpOf, m.HelpRun, m.StandDownUntil, m.Returning) = (0, false, 0, false);

    // ThinkHelper (13.3, 13.4), at each squad think: the walk back's end; the stand-down's end (then the walk back); the call
    // withdrawn or gone (stand down); a stuck helper released (none of its members within supportNear of the support position
    // releaseTicks after it was assigned: it walks back and is not matched to that call for releaseTicks); the Run's end.
    private void ThinkHelper(Squad s, SquadMind m, InfantryCombatRules r)
    {
        if (m.Returning && s.Order == OrderKind.Idle)
        {
            m.Returning = false;
            s.Facing = m.HelpFacing.LenSq() == 0 ? s.Facing : m.HelpFacing; // back where it was, facing as it did
            s.FinalsStale = true;
        }
        if (m.StandDownUntil != 0)
        {
            if (Tick >= m.StandDownUntil && !m.InContact)
            {
                (m.StandDownUntil, m.Returning) = (0, true);
                AutoOrder(s, m, m.HelpFrom, OrderKind.Move);
            }
            return;
        }
        if (m.HelpOf == 0)
        {
            return;
        }
        if (Squad(m.HelpOf) is not { Mind.Calling: true })
        {
            EndHelp(s, m);
            m.StandDownUntil = Tick + (uint)r[CombatValue.CaStandDownTicks]; // the call withdrawn: consolidate, then walk back
            return;
        }
        var near = (long)r[CombatValue.CaSupportNear] * Fixed.OneRaw / 10;
        foreach (var id in s.MemberList)
        {
            m.HelpNear |= Unit(id) is { } u && Able(u) && (u.Pos - m.HelpTo.Center()).LenSq() <= near * near;
        }
        if (!m.HelpNear && Tick - m.HelpTick >= (uint)r[CombatValue.CaReleaseTicks])
        {
            (m.NotCall, m.NotCallUntil) = (m.HelpOf, Tick + (uint)r[CombatValue.CaReleaseTicks]);
            EndHelp(s, m);
            m.Returning = true;
            AutoOrder(s, m, m.HelpFrom, OrderKind.Move);
            CallStats.Released++;
            return;
        }
        m.HelpRun = m.HelpRun && !m.InContact && !m.OpenedFire && !EnemyInApproach(s, m, r);
    }

    // EnemyInApproach (13.4): an able member within approachPercent of a known enemy's weapon range of it.
    private bool EnemyInApproach(Squad s, SquadMind m, InfantryCombatRules r)
    {
        foreach (var k in m.PictureList)
        {
            if (k.Enemy == 0)
            {
                continue;
            }
            ReachOf(k, r, out var range);
            var d = range * r[CombatValue.CaApproachPercent] / 100;
            if (NearestMember(s, k.At.Center()) <= d)
            {
                return true;
            }
        }
        return false;
    }

    // HelperFired (13.4): a helper's member fired: the first shot within arrivedReach of the caller gives it "help arrived", +5 once.
    private void HelperFired(Unit u, InfantryCombatRules r)
    {
        if (u.Squad == 0 || Squad(u.Squad) is not { Mind: { HelpOf: not 0, HelpCredited: false } m } || Squad(m.HelpOf) is not { } c)
        {
            return;
        }
        if (NearestMember(c, u.Pos) <= (long)r[CombatValue.CaArrivedReach] * Fixed.OneRaw / 10)
        {
            m.HelpCredited = true;
            CallStats.Arrived++;
            MoraleChange(c, r[CombatValue.CaHelpArrived]);
        }
    }
}
