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
// INF-HELP (the owner, 2026-11-22, after the battle demo: "what are they doing laying back? they should outgun the forces they
// identify as threat unless told otherwise"; the coordinator's calls in ~/Desktop/rts-work/handover/inf-help-brief.md): a squad on
// the player's Attack-move is a helper too (an Attack-move is not "told otherwise"), and once the help is over it goes on with that
// Attack-move instead of walking back; a plain Move, Hold mode and the still stances (Hold ground, Return fire, Hold fire, Hide: they
// never move on their own, 19.3) are "told otherwise" and don't leave; an Aggressive squad answers within aggressivePercent of
// callRadius and takes its support position at the low end of its line weapon's band; maxHelpers is content (4).
// 13.6 (contacts without a call) is the hearing scan's: a squad that hears or sees the fight is in contact and acts on its own.
public sealed partial class World
{
    private readonly List<Squad> _calls = []; // scratch: the open calls of a pass, sorted
    private readonly List<Unit> _helpUnits = []; // scratch: a helper's members for its squad move

    internal (long Calls, long Assigned, long Released, long Arrived, long Tests, long Passes) CallStats; // (Tests, Passes: INF-HELP's cost)

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
            if (m.Resume)
            {
                ResumeAttack(s, m); // INF-HELP: a different enemy, on the player's Attack-move: it fights it as that Attack-move does
            }
            else
            {
                EndHelp(s, m); // a different enemy: a squad in contact like any other
                m.StandDownUntil = Tick + (uint)r[CombatValue.CaStandDownTicks]; // (part 4: and once that fight is over it walks back, 13.4)
            }
        }
        if (m.StandDownUntil != 0 || m.Returning)
        {
            // in a fight of its own on the way back: it stays and fights, and walks back standDownTicks after its last contact
            // (INF-E1b-5 part 4, 13.4 "B walks back after the fight": before, the stand-down was dropped and it stayed where it fought)
            (m.StandDownUntil, m.Returning) = (Tick + (uint)r[CombatValue.CaStandDownTicks], false);
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
        CallStats.Passes++;
        _calls.Sort(static (a, b) => a.Mind!.Urgency != b.Mind!.Urgency ? b.Mind.Urgency.CompareTo(a.Mind.Urgency) : a.Id.CompareTo(b.Id));
        foreach (var c in _calls)
        {
            var cm = c.Mind!;
            var (need, count) = CallNeed(c, r);
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
            CallStats.Tests += h != c && h.Owner == c.Owner ? 1 : 0;
            if (h == c || h.Owner != c.Owner || h.Mind is not { } hm || !FreeHelper(h, hm, c, r) || !SquadCentre(h, out var hc))
            {
                continue;
            }
            var d = (hc.Center() - cm.CallAt.Center()).LenSq();
            var reach = Aggressive(h) ? radius * r[CombatValue.CaAggressivePercent] / 100 : radius; // INF-HELP: Aggressive answers further
            if (d > reach * reach || d >= bestD)
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

    // FreeHelper (13.2): no order of the player in progress (idle, or walking back from a help; INF-HELP: or on the player's
    // Attack-move, which is not "told otherwise"), on Guard or Aggressive (INF-HELP: the still stances never move on their own, 19.3),
    // its switch on (19.4, part 3), not loose or retreating, not Hold or Ambush, not in contact or calling, Steady, minAmmo% of its
    // ammo on average, minAble able members, not answering or standing down, and not released from this call in the last
    // releaseTicks. INF-HELP (the bench's "laying back" on row 1332's first fix: red's squads, their own fight over, walked on past
    // a friend's for the 10 s their call stays open): a squad whose call is open with no contact for matchEvery or more is free, and
    // answering withdraws its own call. (13.2's Guard order: squads take only Move and Attack-move orders, TakeSquads; the Guard stance answers within
    // callRadius, inside a guard's leash + 10 with the content's guardOuterPercent 220 of a rifleman's vision, 23 cells.)
    private bool FreeHelper(Squad h, SquadMind hm, Squad c, InfantryCombatRules r) => HelpTest(h, hm, c, r) == HelpBlock.Free;

    // HelpTest: FreeHelper's first failed test (the bench's "laying back" reasons, INF-HELP), Free when it passes them all.
    internal HelpBlock HelpTest(Squad h, SquadMind hm, Squad c, InfantryCombatRules r)
    {
        var order = h.Order == OrderKind.Idle || hm.Returning || (h.Order == OrderKind.AttackMove && hm.HelpOf == 0);
        var block = !order ? HelpBlock.Order : !Manoeuvres(h) ? HelpBlock.Stance : !h.AutoSupport ? HelpBlock.Switch : h.Loose ? HelpBlock.Loose :
            h.Retreating ? HelpBlock.Retreating : h.Mode is SquadMode.Hold or SquadMode.Ambush ? HelpBlock.Hold : hm.InContact ? HelpBlock.InContact :
            hm.Calling && Tick - hm.CallSeen < (uint)r[CombatValue.CaMatchEvery] ? HelpBlock.Calling : hm.Spirit != MoraleState.Steady ? HelpBlock.Spirit : hm.HelpOf != 0 ? HelpBlock.Helping :
            hm.StandDownUntil != 0 ? HelpBlock.StandingDown : hm.NotCall == c.Id && Tick < hm.NotCallUntil ? HelpBlock.Released : HelpBlock.Free;
        if (block != HelpBlock.Free)
        {
            return block;
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
        return able < r[CombatValue.CaMinAble] ? HelpBlock.Able : have * 100 < max * r[CombatValue.CaMinAmmo] ? HelpBlock.Ammo : HelpBlock.Free;
    }

    // CallNeed (13.3): what call c still needs and how many helpers it has (the bench's reasons).
    internal (long Need, int Count) CallNeed(Squad c, InfantryCombatRules r)
    {
        var need = c.Mind!.FThem * r[CombatValue.CaNeedPercent] / 100 - c.Mind.FUs;
        var count = 0;
        foreach (var h in _squads)
        {
            if (h.Mind is { } hm && hm.HelpOf == c.Id)
            {
                need -= HelperForce(h, r);
                count++;
            }
        }
        return (need, count);
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
        if (hm.Calling)
        {
            WithdrawCall(hm); // INF-HELP: its own fight is over (no contact for matchEvery): it answers, and its call goes
        }
        (hm.Resume, hm.ResumeGoal) = h.Order == OrderKind.AttackMove && !hm.Returning ? (true, h.Goal) : (false, default); // INF-HELP:
        // the player's Attack-move, taken up again when the help is over
        if (!hm.Returning)
        {
            (hm.HelpFrom, hm.HelpFacing) = (SquadCentre(h, out var from) ? from : Cell.Of(h.Anchor), h.Facing);
        }
        (hm.HelpOf, hm.HelpTick, hm.HelpNear, hm.HelpCredited, hm.Returning) = (c.Id, Tick, false, false, false);
        (hm.HelpTo, hm.HelpGroupAt, hm.HelpPickTick) = (SupportCell(h, cm, r), cm.GroupAt, Tick);
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
    // INF-E1b-5 part 3, again (repick): the coordinator's "still 45-90 degrees off A's threat axis": the helper's side first, supportSteps
    // to 8 steps round the group, no exposure test (the way was weighed at the answer; the helper is on it) and no cell level with the
    // caller's line (none walkable: null, the old cell kept). With 11.5's test, once the enemy is near every way over the open is over
    // flankMaxExposed% exposed, both sides fail, and the helper was sent level with the caller's line, firing along its axis (scene 8).
    private Cell? SupportCell(Squad h, SquadMind cm, InfantryCombatRules r, bool repick)
    {
        var (g, cc) = (cm.GroupAt, cm.CallAt);
        var axis = cm.Axis >= 0 ? cm.Axis : Dir32(g.X - cc.X, g.Y - cc.Y);
        var hc = SquadCentre(h, out var at) ? at : Cell.Of(h.Anchor);
        var band = SquadLineRange(h) * SupportBandPercent(h, r) / 100;
        var back = (axis + 16) & 31;
        Cell Point(int d)
        {
            var (dx, dy) = Directions[d & 31];
            return Cell.Of(new Vec(Fixed.FromRaw(g.Center().X.Raw + dx * band / 1024), Fixed.FromRaw(g.Center().Y.Raw + dy * band / 1024)));
        }
        var steps = r[CombatValue.CaSupportSteps];
        var first = (Point(back + steps).Center() - hc.Center()).LenSq() <= (Point(back - steps).Center() - hc.Center()).LenSq() ? 1 : -1;
        for (var side = 0; side < 2 && repick; side++)
        {
            for (var k = steps; k <= 8; k++)
            {
                var p = Point(back + (side == 0 ? first : -first) * k);
                if (Grid.Contains(p) && Standable(p))
                {
                    return p;
                }
            }
        }
        return null;
    }

    private Cell SupportCell(Squad h, SquadMind cm, InfantryCombatRules r)
    {
        var (g, cc) = (cm.GroupAt, cm.CallAt);
        var axis = cm.Axis >= 0 ? cm.Axis : Dir32(g.X - cc.X, g.Y - cc.Y);
        var hc = SquadCentre(h, out var at) ? at : Cell.Of(h.Anchor);
        var band = SquadLineRange(h) * SupportBandPercent(h, r) / 100;
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
    private static void HelpEndsByOrder(SquadMind m) => (m.HelpOf, m.HelpRun, m.StandDownUntil, m.Returning, m.Resume) = (0, false, 0, false, false);

    // ResumeAttack (INF-HELP): the help of a squad that answered on the player's Attack-move is over (the call withdrawn or gone, a
    // stuck helper released, a different enemy): it goes on with that Attack-move from where it stands, at once (no stand-down or walk
    // back: an Attack-move in contact fights as it goes). Still on its way to the support position, only the goal changes (its line,
    // bounds and fight stand, as RepickSupport's); else the move is given again.
    private void ResumeAttack(Squad s, SquadMind m)
    {
        var (to, was) = (m.ResumeGoal, m.HelpTo);
        (m.HelpOf, m.HelpRun, m.Resume, m.StandDownUntil, m.Returning) = (0, false, false, 0, false);
        if (s.Order == OrderKind.AttackMove && s.Goal == was)
        {
            s.Goal = to;
            ClearSquadPath(s);
            s.PathPending = true;
            foreach (var id in s.MemberList)
            {
                if (Unit(id) is { } u && u.Order.Kind == OrderKind.AttackMove && u.Order.Cell == was)
                {
                    u.Order = u.Order with { Cell = to };
                }
            }
            return;
        }
        AutoOrder(s, m, to, OrderKind.AttackMove);
    }

    // Aggressive (INF-HELP): the squad's leader is on the Aggressive stance (the squad's stance, as Manoeuvres reads it).
    private bool Aggressive(Squad s) => Unit(s.Leader) is { Stance: Stance.Aggressive };

    // SupportBandPercent (13.4, INF-HELP): the helper's distance from the enemy group, in percent of its line range: advanceToPercent
    // (as 10.3 closes); an Aggressive helper the low end of its line weapon's band (row 1301), nearer.
    private int SupportBandPercent(Squad h, InfantryCombatRules r)
    {
        if (Aggressive(h))
        {
            foreach (var id in h.MemberList)
            {
                if (Unit(id) is { } u && Able(u) && u.Def.Weapons.Count > 0 && SquadRoleOf(u) == SquadRole.Line && r.Weapon(u.Def.Weapons[0].Effect) is { BandMin: > 0 } cw)
                {
                    return Math.Min(cw.BandMin, r[CombatValue.SqAdvanceToPercent]);
                }
            }
        }
        return r[CombatValue.SqAdvanceToPercent];
    }

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
            if (m.Resume)
            {
                ResumeAttack(s, m); // INF-HELP: the call withdrawn: on with the player's Attack-move
                return;
            }
            EndHelp(s, m);
            m.StandDownUntil = Tick + (uint)r[CombatValue.CaStandDownTicks]; // the call withdrawn: consolidate, then walk back
            return;
        }
        RepickSupport(s, m, r);
        var near = (long)r[CombatValue.CaSupportNear] * Fixed.OneRaw / 10;
        foreach (var id in s.MemberList)
        {
            m.HelpNear |= Unit(id) is { } u && Able(u) && (u.Pos - m.HelpTo.Center()).LenSq() <= near * near;
        }
        if (!m.HelpNear && Tick - m.HelpTick >= (uint)r[CombatValue.CaReleaseTicks])
        {
            (m.NotCall, m.NotCallUntil) = (m.HelpOf, Tick + (uint)r[CombatValue.CaReleaseTicks]);
            CallStats.Released++;
            if (m.Resume)
            {
                ResumeAttack(s, m); // INF-HELP: released on the player's Attack-move: on with it
                return;
            }
            EndHelp(s, m);
            m.Returning = true;
            AutoOrder(s, m, m.HelpFrom, OrderKind.Move);
            return;
        }
        m.HelpRun = m.HelpRun && !m.InContact && !m.OpenedFire && !EnemyInApproach(s, m, r);
    }

    // RepickSupport (13.4, the coordinator's answer to INF-E1b-5 part 2): the support position follows the fight, with hysteresis:
    // picked again (SupportCell's repick: 45 to 90 degrees off the caller's current threat axis, round the enemy group's current centre, at the helper's band)
    // once that centre has moved more than repickMove since the last pick, at most once every repickTicks. Before, it was fixed at the
    // answer, and an enemy coming on through the caller walked onto it (scene 8: red's attack-move through A onto B's cell). The
    // helper's own auto order goes to the new cell (no new plan: its line and bounds stand; its march heads there, AdvanceDir).
    private void RepickSupport(Squad s, SquadMind m, InfantryCombatRules r)
    {
        if (Squad(m.HelpOf) is not { Mind: { GroupCount: > 0 } cm } || Tick - m.HelpPickTick < (uint)r[CombatValue.CaRepickTicks])
        {
            return;
        }
        var (dx, dy) = ((long)cm.GroupAt.X - m.HelpGroupAt.X, (long)cm.GroupAt.Y - m.HelpGroupAt.Y);
        var move = (long)r[CombatValue.CaRepickMove];
        if ((dx * dx + dy * dy) * 100 <= move * move)
        {
            return;
        }
        (m.HelpGroupAt, m.HelpPickTick) = (cm.GroupAt, Tick);
        if (SupportCell(s, cm, r, true) is not { } to || to == m.HelpTo)
        {
            return;
        }
        var was = m.HelpTo;
        m.HelpTo = to;
        if (s.Order == OrderKind.Idle && !m.HasLine)
        {
            AutoOrder(s, m, to, OrderKind.AttackMove); // it had arrived (out of contact): on to the new cell
            return;
        }
        if (s.Order != OrderKind.AttackMove || s.Goal != was)
        {
            return; // in a fight of its own: Choose's helper rows take it on to the new cell (AdvanceDir: HelpTo)
        }
        s.Goal = to;
        ClearSquadPath(s);
        s.PathPending = true;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { } u && u.Order.Kind == OrderKind.AttackMove && u.Order.Cell == was)
            {
                u.Order = u.Order with { Cell = to };
            }
        }
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

/// <summary>Why a squad is not a free helper for a call (13.2's tests in order; INF-HELP's bench reasons). Free when it is one.</summary>
internal enum HelpBlock : byte
{
    Free,
    Order,
    Switch,
    Loose,
    Retreating,
    Hold,
    InContact,
    Calling,
    Spirit,
    Helping,
    StandingDown,
    Released,
    Able,
    Ammo,
    Stance,
    Far,
}
