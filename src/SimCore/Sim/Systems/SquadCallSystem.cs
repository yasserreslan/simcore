using System.Runtime.CompilerServices;
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
// INF-HELP-2 (the coordinator's calls on ~/Desktop/rts-work/handover/inf-help2-brief.md): a call's F(them) is its whole known enemy
// group at full firepower (SquadTacticsSystem.ForceRatio), so the helpers sent outgun all of it; 13.2's Hold exception as written (a
// squad in Hold mode answers a call within holdReach of its Hold spot at U holdUrgency or more, and holds its spot again after); and
// loose soldiers answer calls within looseRadius (no order in progress, or on the player's Attack-move), each counting his own
// firepower, coming to a support position as 13.4's and going on after (his Attack-move again, or the stand-down and the walk back).
// 13.6 (contacts without a call) is the hearing scan's: a squad that hears or sees the fight is in contact and acts on its own.
public sealed partial class World
{
    private readonly List<Squad> _calls = []; // scratch: the open calls of a pass, sorted
    private readonly List<Unit> _helpUnits = []; // scratch: a helper's members for its squad move
    private readonly List<Unit> _loose = []; // scratch: a pass's loose soldiers (INF-HELP-2), by ID

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
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // BATTLE-PERF: run every tick, compiled optimised at once (row 1390)
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
        _loose.Clear();
        foreach (var u in _units)
        {
            if (u is { Squad: 0, Alive: true, Fight: not null })
            {
                _loose.Add(u); // INF-HELP-2: the loose soldiers, the helpers among them kept up first
            }
        }
        ThinkLooseHelpers(r);
        if (_calls.Count == 0)
        {
            return;
        }
        CallStats.Passes++;
        _calls.Sort(static (a, b) => a.Mind!.Urgency != b.Mind!.Urgency ? b.Mind.Urgency.CompareTo(a.Mind.Urgency) : a.Id.CompareTo(b.Id));
        foreach (var c in _calls)
        {
            var cm = c.Mind!;
            var (need, count) = CallNeed(c, r, _loose);
            for (var pass = cm.CallArmour ? 0 : 1; pass < 2; pass++)
            {
                // INF-HELP-2: squads and loose soldiers nearest first (a tie: the squad); maxHelpers counts squads only (a loose man
                // is one man's force, and answers only within looseRadius)
                while (need > 0)
                {
                    var hd = long.MaxValue;
                    var h = count < r[CombatValue.CaMaxHelpers] ? NearestHelper(c, cm, pass == 0, r, out hd) : null;
                    var lu = NearestLoose(c, cm, pass == 0, r, out var ld);
                    if (lu is not null && (h is null || ld < hd))
                    {
                        AssignLoose(lu, c, cm, r);
                        need -= ManForce(lu, r);
                        continue;
                    }
                    if (h is null)
                    {
                        break;
                    }
                    AssignHelper(h, c, cm, r);
                    need -= HelperForce(h, r);
                    count++;
                }
            }
        }
    }

    // NearestHelper (13.2): the nearest free squad (straight distance from its centre to the call's; ties by ID) of the caller's player.
    private Squad? NearestHelper(Squad c, SquadMind cm, bool antiArmour, InfantryCombatRules r, out long bestD)
    {
        var radius = (long)r[CombatValue.CaCallRadius] * Fixed.OneRaw / 10;
        Squad? best = null;
        bestD = long.MaxValue;
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
            h.Retreating ? HelpBlock.Retreating : (h.Mode is SquadMode.Hold or SquadMode.Ambush && !HoldAnswers(h, c, r)) ? HelpBlock.Hold : hm.InContact ? HelpBlock.InContact :
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

    // HoldAnswers (13.2's Hold exception, INF-HELP-2; content holdAnswers): a squad in Hold mode answers a call within holdReach of its
    // Hold spot when U is holdUrgency or more (a held position helps its immediate neighbour in serious trouble). Ambush never does.
    private bool HoldAnswers(Squad h, Squad c, InfantryCombatRules r)
    {
        var reach = (long)r[CombatValue.CaHoldReach] * Fixed.OneRaw / 10;
        return h.Mode == SquadMode.Hold && r[CombatValue.CaHoldAnswers] != 0 && c.Mind!.Urgency >= r[CombatValue.CaHoldUrgency] &&
            (c.Mind.CallAt.Center() - h.HoldSpot.Center()).LenSq() <= reach * reach;
    }

    // CallNeed (13.3): what call c still needs and how many squads help it (the bench's reasons); INF-HELP-2: its loose helpers' force
    // counted too (the pass's loose soldiers; else every unit).
    internal (long Need, int Count) CallNeed(Squad c, InfantryCombatRules r) => CallNeed(c, r, _units);

    private (long Need, int Count) CallNeed(Squad c, InfantryCombatRules r, List<Unit> loose)
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
        foreach (var u in loose)
        {
            if (u is { Squad: 0, Fight.Help.Of: var of } && of == c.Id && Able(u))
            {
                need -= ManForce(u, r);
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
            if (Unit(id) is { } u)
            {
                v += ManForce(u, r);
            }
        }
        return v;
    }

    // ManForce: one able man's weapon at its in-range value (10.2), wounded less, x100 (a squad helper's member, or a loose helper).
    private long ManForce(Unit u, InfantryCombatRules r)
    {
        if (!Able(u) || u.Def.Weapons.Count == 0)
        {
            return 0;
        }
        var w = u.Def.Weapons[u.Arms is { } a ? a.InHand : 0];
        long x = ForceValue(r.Weapon(w.Effect)?.Class, 0, Math.Max(1, w.Range.Raw), false, r);
        return (u.Life is { Wound: WoundState.Wounded } ? x * r[CombatValue.SqWounded] / 100 : x) * 100;
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
        if (h.Mode == SquadMode.Hold)
        {
            // INF-HELP-2 (13.2's Hold exception): it leaves its Hold for the help (its cells and buildings) and holds its spot again
            // after, facing as it did (walking back to it already: the same spot)
            (hm.HelpFrom, hm.HelpFacing, hm.HoldToward, hm.HoldBack) = (h.HoldSpot, h.HoldFacing, Cell.Of(h.HoldThreat), true);
            LeaveHold(h);
            h.Mode = SquadMode.March;
            BuildSlots(h, Content.Squad!);
        }
        else if (!hm.Returning)
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

    private Cell SupportCell(Squad h, SquadMind cm, InfantryCombatRules r) =>
        SupportCell(SquadCentre(h, out var at) ? at : Cell.Of(h.Anchor), SquadLineRange(h) * SupportBandPercent(h, r) / 100, Unit(h.Leader), cm, r);

    // (from hc, at band from the enemy group; lead's way there weighed for exposure: a squad's leader, or a loose helper himself)
    private Cell SupportCell(Cell hc, long band, Unit? lead, SquadMind cm, InfantryCombatRules r)
    {
        var (g, cc) = (cm.GroupAt, cm.CallAt);
        var axis = cm.Axis >= 0 ? cm.Axis : Dir32(g.X - cc.X, g.Y - cc.Y);
        var back = (axis + 16) & 31;
        Cell Point(int d, long len, Cell from)
        {
            var (dx, dy) = Directions[d & 31];
            return Cell.Of(new Vec(Fixed.FromRaw(from.Center().X.Raw + dx * len / 1024), Fixed.FromRaw(from.Center().Y.Raw + dy * len / 1024)));
        }
        long D2(Cell a, Cell b) => (a.Center() - b.Center()).LenSq();
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
    private static void HelpEndsByOrder(SquadMind m) => (m.HelpOf, m.HelpRun, m.StandDownUntil, m.Returning, m.Resume, m.HoldBack) = (0, false, 0, false, false, false);

    // WalkBack (13.4): the helper walks back to where it answered; INF-HELP-2: one that answered from Hold mode holds its spot again,
    // facing as it did (its Hold order: it walks there and picks its cells).
    private void WalkBack(Squad s, SquadMind m)
    {
        m.Returning = true;
        if (m.HoldBack && Content.Squad is { Hold: { } hold } rules)
        {
            m.HoldBack = false;
            ClearTactics(s, m);
            (m.Urgent, m.OrderNew) = (true, true);
            HoldOrder(s, m.HelpFrom, m.HoldToward, rules, hold);
            return;
        }
        AutoOrder(s, m, m.HelpFrom, OrderKind.Move);
    }

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
                m.StandDownUntil = 0;
                WalkBack(s, m);
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
            WalkBack(s, m);
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

    // ---- INF-HELP-2: loose soldiers as helpers (13.2: "a squad (or loose soldier)"; the coordinator's call 3) ----

    // NearestLoose (13.2): the nearest free loose soldier of the caller's player (straight distance to the call's centre; ties by ID)
    // within looseRadius (12 cells).
    private Unit? NearestLoose(Squad c, SquadMind cm, bool antiArmour, InfantryCombatRules r, out long bestD)
    {
        var radius = (long)r[CombatValue.CaLooseRadius] * Fixed.OneRaw / 10;
        Unit? best = null;
        bestD = long.MaxValue;
        foreach (var u in _loose)
        {
            if (u.Owner != c.Owner)
            {
                continue;
            }
            CallStats.Tests++;
            var d = (u.Pos - cm.CallAt.Center()).LenSq();
            if (d > radius * radius || d >= bestD || (antiArmour && !HurtsArmour(u, r)) || LooseTest(u, c, r) != HelpBlock.Free)
            {
                continue;
            }
            (best, bestD) = (u, d);
        }
        return best;
    }

    // LooseTest (13.2 for a loose soldier): no order in progress or the player's Attack-move (and not in a building or a vehicle), on
    // Guard or Aggressive (the still stances never move on their own), able, not in contact, Steady (not routing or cowed), not helping
    // or standing down, not released from this call in the last releaseTicks, minAmmo% of his ammo. Free when he passes them all.
    internal HelpBlock LooseTest(Unit u, Squad c, InfantryCombatRules r)
    {
        var f = u.Fight!;
        var lh = f.Help;
        var order = (u.Order.Kind is OrderKind.Idle or OrderKind.AttackMove || lh is { Returning: true }) && u.Garrison == 0 && u.Inside == 0;
        var block = !order ? HelpBlock.Order : u.Stance is not (Stance.Guard or Stance.Aggressive) ? HelpBlock.Stance : !Able(u) ? HelpBlock.Able :
            f.InContact ? HelpBlock.InContact : f.Spirit != MoraleState.Steady || f.Routs || f.Cowed ? HelpBlock.Spirit : lh is { Of: not 0 } ? HelpBlock.Helping :
            lh is { StandDownUntil: not 0 } ? HelpBlock.StandingDown : lh is { } x && x.NotCall == c.Id && Tick < x.NotCallUntil ? HelpBlock.Released : HelpBlock.Free;
        if (block != HelpBlock.Free || u.Def.Weapons.Count == 0)
        {
            return block != HelpBlock.Free ? block : HelpBlock.Able;
        }
        return u.Def.Weapons[0] is { Stock: >= 0, Effect.Carried: > 0 } w && u.Charges[w.Stock] * 100L < (long)u.StockMax(w.Stock) * r[CombatValue.CaMinAmmo] ? HelpBlock.Ammo : HelpBlock.Free;
    }

    // AssignLoose (13.3, 13.4): u answers c: he remembers where he was (unless walking back already) and attack-moves to a support
    // position as a squad's (off the caller's threat axis round the enemy group, at his band from it: advanceToPercent of his weapon's
    // range, an Aggressive man the low end of its band).
    private void AssignLoose(Unit u, Squad c, SquadMind cm, InfantryCombatRules r)
    {
        var lh = u.Fight!.Help ??= new LooseHelp();
        (lh.Resume, lh.ResumeGoal) = u.Order.Kind == OrderKind.AttackMove && !lh.Returning ? (true, u.Order.Cell) : (false, default);
        if (!lh.Returning)
        {
            lh.From = Cell.Of(u.Pos);
        }
        (lh.Of, lh.Tick, lh.Near, lh.Credited, lh.Returning, lh.StandDownUntil) = (c.Id, Tick, false, false, false, 0);
        var w = u.Def.Weapons[0];
        var pct = u.Stance == Stance.Aggressive && r.Weapon(w.Effect) is { BandMin: > 0 } cw ? Math.Min(cw.BandMin, r[CombatValue.SqAdvanceToPercent]) : r[CombatValue.SqAdvanceToPercent];
        var to = SupportCell(Cell.Of(u.Pos), Math.Max(1, w.Range.Raw) * pct / 100, u, cm, r);
        LooseOrder(u, to, OrderKind.AttackMove);
        lh.To = u.Order.Kind == OrderKind.AttackMove ? u.Order.Cell : to;
        CallStats.Assigned++;
    }

    // LooseOrder: a loose helper's own move (as the player's order to one man would give it, but not one: it doesn't end the help).
    private void LooseOrder(Unit u, Cell to, OrderKind kind)
    {
        _helpUnits.Clear();
        _helpUnits.Add(u);
        MoveGround(_helpUnits, to);
        if (kind == OrderKind.AttackMove && u.Order.Kind == OrderKind.Move)
        {
            u.ResetAi();
            u.Order = u.Order with { Kind = OrderKind.AttackMove };
        }
    }

    // ThinkLooseHelpers (13.3, 13.4 for loose helpers), at each match pass: the walk back's end; the stand-down's end (then the walk
    // back); the call withdrawn or gone, or a fight of his own beyond otherFight from the call (he stops where he is and stands down);
    // stuck (not within supportNear of his position releaseTicks after he answered: released, not matched to that call for releaseTicks,
    // he walks back). On the player's Attack-move he goes on with it instead, at once. Downed: the help goes.
    private void ThinkLooseHelpers(InfantryCombatRules r)
    {
        var near = (long)r[CombatValue.CaSupportNear] * Fixed.OneRaw / 10;
        var far = (long)r[CombatValue.CaOtherFight] * Fixed.OneRaw / 10;
        foreach (var u in _loose)
        {
            if (u.Fight is not { Help: { } lh } f)
            {
                continue;
            }
            if (!Able(u))
            {
                f.Help = null;
                continue;
            }
            lh.Returning = lh.Returning && u.Order.Kind != OrderKind.Idle;
            if (lh.StandDownUntil != 0)
            {
                if (Tick >= lh.StandDownUntil && !f.InContact)
                {
                    (lh.StandDownUntil, lh.Returning) = (0, true);
                    LooseOrder(u, lh.From, OrderKind.Move);
                }
            }
            else if (lh.Of != 0)
            {
                var cm = Squad(lh.Of) is { Mind: { Calling: true } m } ? m : null;
                var other = cm is not null && f.InContact && (u.Pos - cm.CallAt.Center()).LenSq() > far * far;
                lh.Near |= (u.Pos - lh.To.Center()).LenSq() <= near * near;
                var stuck = cm is not null && !lh.Near && Tick - lh.Tick >= (uint)r[CombatValue.CaReleaseTicks];
                if (stuck)
                {
                    (lh.NotCall, lh.NotCallUntil) = (lh.Of, Tick + (uint)r[CombatValue.CaReleaseTicks]);
                    CallStats.Released++;
                }
                if (cm is null || other || stuck)
                {
                    lh.Of = 0;
                    if (lh.Resume)
                    {
                        lh.Resume = false;
                        LooseOrder(u, lh.ResumeGoal, OrderKind.AttackMove); // on with the player's Attack-move
                    }
                    else if (stuck)
                    {
                        lh.Returning = true;
                        LooseOrder(u, lh.From, OrderKind.Move);
                    }
                    else
                    {
                        if (u.Order.Kind == OrderKind.AttackMove && u.Order.Cell == lh.To)
                        {
                            u.Idle(); // where he stands: consolidate, then walk back
                        }
                        lh.StandDownUntil = Tick + (uint)r[CombatValue.CaStandDownTicks];
                    }
                }
            }
            if (lh is { Of: 0, Returning: false, StandDownUntil: 0 } && Tick >= lh.NotCallUntil)
            {
                f.Help = null;
            }
        }
    }

    // LooseHelpEnds (13.4): a player's order (not a stance, a weapon or fire mode, or the support switch) to a loose helper, or one
    // standing down or walking back, ends his help at once: no walk back.
    private void LooseHelpEnds(Command c)
    {
        if (c.Kind is CommandKind.SetStance or CommandKind.SetAutoSupport or CommandKind.SetWeaponMode or CommandKind.SetFireMode || !_soldiers)
        {
            return;
        }
        foreach (var id in c.Units)
        {
            if (Unit(id) is { Fight: { Help: not null } f } u && u.Owner == c.Player)
            {
                f.Help = null;
            }
        }
    }

    // HelperFired (13.4): a helper's member fired: the first shot within arrivedReach of the caller gives it "help arrived", +5 once.
    private void HelperFired(Unit u, InfantryCombatRules r)
    {
        if (u is { Squad: 0, Fight.Help: { Of: not 0, Credited: false } lh } && Squad(lh.Of) is { } lc)
        {
            if (NearestMember(lc, u.Pos) <= (long)r[CombatValue.CaArrivedReach] * Fixed.OneRaw / 10)
            {
                lh.Credited = true; // INF-HELP-2: a loose helper's first shot near the caller is "help arrived" too
                CallStats.Arrived++;
                MoraleChange(lc, r[CombatValue.CaHelpArrived]);
            }
            return;
        }
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

/// <summary>A loose soldier's help (INF-HELP-2, 13.2's "or loose soldier"): the call he answers, where he goes and where he came from.</summary>
internal sealed class LooseHelp
{
    internal uint Of; // the calling squad (0: the help is over: standing down, walking back, or released from NotCall)
    internal Cell To; // his support position (13.4)
    internal Cell From; // where he was when he answered: he walks back there
    internal uint Tick; // the assignment
    internal bool Near; // he came within supportNear of To
    internal bool Credited; // the caller had its "help arrived" from him
    internal bool Resume; // he answered on the player's Attack-move: the help over, he goes on with it
    internal Cell ResumeGoal;
    internal uint StandDownUntil; // the call withdrawn: he stays until, then walks back
    internal bool Returning;
    internal uint NotCall; // released from this call (stuck) until NotCallUntil
    internal uint NotCallUntil;

    internal void HashInto(ref WordHash h)
    {
        h.Put4(Of | ((ulong)Tick << 32), unchecked((ulong)(((long)To.X << 20) ^ (To.Y & 0xFFFFF))) ^ ((ulong)(((long)From.X << 20) ^ (From.Y & 0xFFFFF)) << 40),
            (Near ? 1UL : 0) | (Credited ? 2UL : 0) | (Resume ? 4UL : 0) | (Returning ? 8UL : 0) | ((ulong)StandDownUntil << 32),
            unchecked((ulong)(((long)ResumeGoal.X << 20) ^ (ResumeGoal.Y & 0xFFFFF))) ^ ((ulong)NotCall << 40) ^ ((ulong)NotCallUntil << 8) ^ 0x1CBBUL);
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
