using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// INF-E1b-3 part 2, the squad's manoeuvres (docs/design.md, "Infantry combat: the squad's manoeuvres"; the game repo's
// docs/plans/infantry-battle-rules.md 11.5 flank and 11.7 assault): part of the squad works round an enemy it can't hurt in its
// cover, or the squad rushes a close, pinned enemy. Both run in the squad think (after the line, before the suppressive fire
// orders); the movers' goals are the line's goals (LinePlace, Bounding), so they walk through INF-MOVE's FollowSlots like every
// squad move: picked at the think, walked to together by pace. Every loop is bounded by the members, the picture's 16 entries
// or the six flank candidates; one flank plan a tick on the whole map.
public sealed partial class World
{
    private uint _flankPlanTick = uint.MaxValue; // the tick a flank plan ran (11.5: one a tick on the whole map); not state: a cap

    internal (long Plans, long Rejected, long Flanks, long Aborts, long Assaults, long AssaultFails) ManoeuvreStats;

    // NoteSquadHit (11.5's counters): squad member `by` hit an enemy soldier.
    private void NoteSquadHit(Unit t, Unit by)
    {
        if (by.Squad != 0 && t.Fight is not null && t.Owner != by.Owner && Squad(by.Squad) is { Mind: { } m })
        {
            m.HitsNow++;
        }
    }

    // InLine: u has a place in his squad's line: the squad places him (no chase of his own, 11.1).
    internal bool InLine(Unit u) => u.Squad != 0 && Squad(u.Squad) is { Mind: { HasLine: true } m } && LineIndex(m, u.Id) >= 0;

    // ManoeuvreGait (7.2 for the squad's moves): an assault's runner runs (toward the threat: the back rule allows it), a flanker on
    // the way crouch-runs, a bound's mover crouch-runs inside runSteps of his axis under suppression 60 (11.3); None leaves the
    // soldier's own gait.
    internal Gait ManoeuvreGait(Unit u, SoldierFight f, int move, InfantryCombatRules r)
    {
        if (u.Squad == 0 || Squad(u.Squad) is not { Mind: { HasLine: true } m } || LineIndex(m, u.Id) is not (>= 0 and var i) || !m.LineList[i].Bounding)
        {
            return Gait.None;
        }
        if (m.Choice == SquadChoice.Assault && m.IsMover(u.Id))
        {
            return Gait.Run;
        }
        if (m.FlankStage == 2 && m.IsMover(u.Id))
        {
            return Gait.CrouchRun;
        }
        return m.Moving != 0 && m.LineList[i].Element == m.Moving && f.Suppression < r[CombatValue.Pinned] && f.Axis >= 0 &&
            DirSteps(move, f.Axis) < r[CombatValue.FaceRunSteps] ? Gait.CrouchRun : Gait.None; // (11.3: at 60+ the crouched walk; inside
                                                                                              // runSteps, never at its edge past 90 deg)
    }

    // FlankerHolds (11.5): a flanker on the way doesn't fire, unless at an enemy within flankCloseFire.
    private bool FlankerHolds(Unit u, Unit target, InfantryCombatRules r)
    {
        if (u.Squad == 0 || Squad(u.Squad) is not { Mind: { FlankStage: 2 } m } || !m.IsMover(u.Id))
        {
            return false;
        }
        var close = (long)r[CombatValue.SqFlankCloseFire] * Fixed.OneRaw / 10;
        return (target.Pos - u.Pos).LenSq() > close * close;
    }

    // Manoeuvre: one squad think's flank and assault (after the line and the bounds).
    private void Manoeuvre(Squad s, SquadMind m, Cell centre, InfantryCombatRules r)
    {
        m.FipSince = m.Choice == SquadChoice.FightInPlace ? (m.FipSince == 0 ? Tick : m.FipSince) : 0;
        if (!m.HasLine)
        {
            EndManoeuvres(s, m);
            return;
        }
        if (m.Choice == SquadChoice.Assault)
        {
            ThinkAssault(s, m, r);
            return;
        }
        if (m.AssaultTick != 0)
        {
            EndAssault(m, false, r);
        }
        if (m.FlankStage is 1 or 2)
        {
            ThinkFlank(s, m, r);
            return;
        }
        if (m.Choice == SquadChoice.FightInPlace && Tick >= m.LeaderPauseUntil && Stalled(s, m, centre, r)) // (E1b-4, 11.8: not in the pause)
        {
            PlanFlank(s, m, centre, r);
        }
    }

    // EndManoeuvres: the line went (calm, an order, a Move): the flank and the assault end with it; the waits stay.
    private static void EndManoeuvres(Squad s, SquadMind m)
    {
        (m.FlankStage, m.MoverCount, m.AssaultTick, m.RushAt) = (0, 0, 0, 0);
        _ = s;
    }

    // MoversHere: the movers stop where they are and take their positions from there (section 6): their slot is their cell.
    private void MoversHere(SquadMind m)
    {
        for (var i = 0; i < m.LineList.Count; i++)
        {
            var p = m.LineList[i];
            if (m.IsMover(p.Unit) && Unit(p.Unit) is { } u)
            {
                var here = Cell.Of(u.Pos);
                m.LineList[i] = p with { Slot = here, Goal = here, Bounding = false };
            }
        }
    }

    private void BackToFight(SquadMind m)
    {
        (m.Choice, m.ChoiceTick, m.ChoiceR, m.FipSince) = (SquadChoice.FightInPlace, Tick, 0, Tick);
    }

    // ---- 11.5: flank ----

    // Mover (11.5, 11.7): who may flank or rush: an able man of the line (not the LMG gunner, the medic, the leader for a flank, or a
    // heavy weapon's role), in Guard or Aggressive, with a place in the line.
    private bool Mover(Squad s, SquadMind m, Unit u, bool flank) =>
        Able(u) && SquadRoleOf(u) == SquadRole.Line && u.Fight is { Lmg: false } && u.Def.Weapons.Count > 0 &&
        u.Stance is Stance.Guard or Stance.Aggressive && (!flank || u.Id != s.Leader) && LineIndex(m, u.Id) >= 0;

    // Stalled (11.5's stall test): flankFipTicks in Fight in place (no order, Guard or Attack-move; never Hold or Ambush), under
    // flankHitsBelow hits on enemy soldiers in the two windows, the group's mean protection toward the squad flankCoverAvg+,
    // flankMinAble men of the line, R flankMinR+, autoFlank on; not before FlankNoUntil, one plan a tick on the map.
    private bool Stalled(Squad s, SquadMind m, Cell centre, InfantryCombatRules r)
    {
        if (r[CombatValue.SqAutoFlank] == 0 || s.Mode is SquadMode.Hold or SquadMode.Ambush || s.Order is not (OrderKind.Idle or OrderKind.AttackMove or OrderKind.Guard) ||
            !Manoeuvres(s) || m.FipSince == 0 || Tick - m.FipSince < (uint)r[CombatValue.SqFlankFipTicks] || Tick < m.FlankNoUntil ||
            m.HitsNow + m.HitsPrev >= r[CombatValue.SqFlankHitsBelow] || m.Ratio < r[CombatValue.SqFlankMinR] || m.GroupCount == 0 || _flankPlanTick == Tick)
        {
            return false;
        }
        var line = 0;
        foreach (var id in s.MemberList)
        {
            line += Unit(id) is { } u && Able(u) && SquadRoleOf(u) is SquadRole.Line or SquadRole.Flank ? 1 : 0;
        }
        if (line < r[CombatValue.SqFlankMinAble])
        {
            return false;
        }
        long prot = 0, n = 0;
        var gr = (long)r[CombatValue.SqGroupRadius] * Fixed.OneRaw / 10;
        foreach (var k in m.PictureList)
        {
            if (k.Enemy != 0 && InGroup(m, k, gr / 2) && Unit(k.Enemy) is { Alive: true } e)
            {
                prot += Protection(k.At, e.Fight?.Posture ?? Posture.Stand, Dir32(centre.X - k.At.X, centre.Y - k.At.Y), false);
                n++;
            }
        }
        return n > 0 && prot >= n * r[CombatValue.SqFlankCoverAvg];
    }

    // PlanFlank (11.5's plan): six candidates round the enemy group (the axis back from it turned 7, 8, 9 steps left, then right, at
    // flankDistancePercent of the line weapon's range, snapped within a cell), kept with a line of fire to the group and outside its
    // cover's side toward them; each way's exposure read cell by cell along the straight way from the squad's centre (at most
    // flankMaxPath; crouched, to the known contacts; the routing walks it). The lowest mean exposure wins (then the shorter, then
    // the left, then candidate order); over flankMaxExposed% of exposed cells: rejected, the squad fights on in place.
    private void PlanFlank(Squad s, SquadMind m, Cell centre, InfantryCombatRules r)
    {
        _flankPlanTick = Tick;
        ManoeuvreStats.Plans++;
        Unit? probe = null;
        foreach (var id in s.MemberList)
        {
            probe ??= Unit(id) is { } u && Mover(s, m, u, true) ? u : null;
        }
        var dist = SquadLineRange(s) * r[CombatValue.SqFlankDistancePercent] / 100;
        var away = (m.Axis + 16) & 31;
        var g = m.GroupAt.Center();
        Cell? best = null;
        long bestE = long.MaxValue, bestN = long.MaxValue, bestShare = 0;
        for (var i = 0; i < 6 && probe is not null; i++)
        {
            var d = (away + (i < 3 ? -1 : 1) * (7 + i % 3)) & 31;
            var (dx, dy) = Directions[d];
            var at = Cell.Of(OnMap(new Vec(Fixed.FromRaw(g.X.Raw + dx * dist / 1024), Fixed.FromRaw(g.Y.Raw + dy * dist / 1024))));
            if (SnapNear(at) is not { } c || !FireLineFrom(c, g) ||
                CoverToward(m.GroupAt, Dir32(c.X - m.GroupAt.X, c.Y - m.GroupAt.Y), r) is CoverKind.Low or CoverKind.High)
            {
                continue;
            }
            if (WayExposure(probe, centre, c, r) is not { } way)
            {
                continue;
            }
            var mean = way.Sum / way.N;
            if (mean < bestE || (mean == bestE && way.N < bestN))
            {
                (best, bestE, bestN, bestShare) = (c, mean, way.N, way.Exposed * 100 / way.N);
            }
        }
        var count = m.AbleCount >= r[CombatValue.SqFlankBigSquad] ? 3 : 2;
        if (best is not { } to || bestShare > r[CombatValue.SqFlankMaxExposed] || m.AbleCount - count < 2)
        {
            m.FlankNoUntil = Tick + (uint)r[CombatValue.SqFlankRejectTicks]; // no safe flank: its call's urgency +10 (13.1)
            m.FlankRefused = true;
            ManoeuvreStats.Rejected++;
            return;
        }
        PickMovers(s, m, to, count, true);
        if (m.MoverCount == 0)
        {
            m.FlankNoUntil = Tick + (uint)r[CombatValue.SqFlankRejectTicks];
            return;
        }
        (m.FlankTo, m.FlankFrom, m.FlankTick, m.FlankStage, m.FlankRefused) = (to, m.GroupAt, Tick, 1, false);
        (m.Choice, m.ChoiceTick, m.ChoiceR) = (SquadChoice.Flank, Tick, 0);
    }

    // FlankCell: each flanker's cell: the nearest to the flank cell within 2 rings (ring by ring, row, then column) that is standable,
    // not taken, and outside the group's cover toward it (the enemy's cover useless against him, 5.2).
    private Cell? FlankCell(SquadMind m, InfantryCombatRules r)
    {
        for (var ring = 0; ring <= 2; ring++)
        {
            for (var dy = -ring; dy <= ring; dy++)
            {
                for (var dx = -ring; dx <= ring; dx++)
                {
                    var c = new Cell(m.FlankTo.X + dx, m.FlankTo.Y + dy);
                    if ((Math.Abs(dx) == ring || Math.Abs(dy) == ring) && Standable(c) && !_lineTaken.Contains(c) &&
                        CoverToward(m.GroupAt, Dir32(c.X - m.GroupAt.X, c.Y - m.GroupAt.Y), r) is not (CoverKind.Low or CoverKind.High))
                    {
                        return c;
                    }
                }
            }
        }
        return null;
    }

    // SnapNear: c, else the first standable cell round it (row, then column), else null.
    private Cell? SnapNear(Cell c)
    {
        if (Standable(c))
        {
            return c;
        }
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var n = new Cell(c.X + dx, c.Y + dy);
                if (Standable(n))
                {
                    return n;
                }
            }
        }
        return null;
    }

    // WayExposure: the cells of the straight way from a to b (a's excluded; blocked cells counted, not scored): u's crouched exposure
    // summed, and how many are exposed (over flankExposed); null when the way is over flankMaxPath.
    private (long Sum, long N, long Exposed)? WayExposure(Unit u, Cell a, Cell b, InfantryCombatRules r)
    {
        int dx = Math.Abs(b.X - a.X), dy = -Math.Abs(b.Y - a.Y), sx = a.X < b.X ? 1 : -1, sy = a.Y < b.Y ? 1 : -1;
        int x = a.X, y = a.Y, err = dx + dy;
        long sum = 0, n = 0, exposed = 0;
        var cap = r[CombatValue.SqFlankMaxPath] / 10;
        while (x != b.X || y != b.Y)
        {
            var e2 = 2 * err;
            if (e2 >= dy)
            {
                (err, x) = (err + dy, x + sx);
            }
            if (e2 <= dx)
            {
                (err, y) = (err + dx, y + sy);
            }
            if (++n > cap)
            {
                return null;
            }
            var c = new Cell(x, y);
            if (!Standable(c))
            {
                continue;
            }
            var ex = Exposure(u, c, Posture.Crouch);
            sum += ex;
            exposed += ex > r[CombatValue.SqFlankExposed] ? 1 : 0;
        }
        return n == 0 ? null : (sum, n, exposed);
    }

    // PickMovers: the count movers nearest to cell `to` (ties: ID), in Movers by ID.
    private void PickMovers(Squad s, SquadMind m, Cell to, int count, bool flank)
    {
        m.MoverCount = 0;
        var at = to.Center();
        for (var k = 0; k < count && k < m.Movers.Length; k++)
        {
            uint pick = 0;
            var pickD = long.MaxValue;
            foreach (var id in s.MemberList)
            {
                if (Unit(id) is { } u && !m.IsMover(id) && Mover(s, m, u, flank) && (u.Pos - at).LenSq() is var d && d < pickD)
                {
                    (pick, pickD) = (id, d);
                }
            }
            if (pick == 0)
            {
                break;
            }
            m.Movers[m.MoverCount++] = pick;
        }
        Array.Sort(m.Movers, 0, m.MoverCount);
    }

    // ThinkFlank (11.5's go, arrive and abort): the base of fire suppresses flankSuppressTicks, then the flankers walk to cells round
    // the flank cell; there (each on his goal and still), they take positions from there and the squad fights in place. Aborted when
    // a flanker is hit or down on the way, the base of fire is under 2 able men, the group moved flankMoved from the plan's cell, or
    // flankMaxTicks on the way: the flankers take positions where they are; no new plan for flankRetryTicks.
    private void ThinkFlank(Squad s, SquadMind m, InfantryCombatRules r)
    {
        var go = m.FlankTick + (uint)r[CombatValue.SqFlankSuppressTicks];
        int able = 0, there = 0;
        var hit = false;
        for (var i = 0; i < m.MoverCount; i++)
        {
            if (Unit(m.Movers[i]) is not { } u || !Able(u))
            {
                hit = true;
                continue;
            }
            able++;
            hit |= m.FlankStage == 2 && u.LastHitTick > go;
            there += LineIndex(m, u.Id) is >= 0 and var li && Cell.Of(u.Pos) == m.LineList[li].Goal && !u.Moving ? 1 : 0;
        }
        var moved = (long)r[CombatValue.SqFlankMoved] * Fixed.OneRaw / 10;
        if (hit || m.AbleCount - able < 2 || m.GroupCount == 0 || (m.GroupAt.Center() - m.FlankFrom.Center()).LenSq() > moved * moved ||
            (m.FlankStage == 2 && Tick - go >= (uint)r[CombatValue.SqFlankMaxTicks]))
        {
            MoversHere(m);
            (m.FlankStage, m.MoverCount, m.FlankNoUntil) = (0, 0, Tick + (uint)r[CombatValue.SqFlankRetryTicks]);
            ManoeuvreStats.Aborts++;
            BackToFight(m);
            return;
        }
        if (m.FlankStage == 1 && Tick >= go)
        {
            _lineTaken.Clear();
            foreach (var p in m.LineList)
            {
                if (!m.IsMover(p.Unit))
                {
                    _lineTaken.Add(p.Goal);
                }
            }
            for (var i = 0; i < m.LineList.Count; i++)
            {
                var p = m.LineList[i];
                if (m.IsMover(p.Unit))
                {
                    var c = FlankCell(m, r) ?? SnapFree(m.FlankTo);
                    _lineTaken.Add(c);
                    m.LineList[i] = p with { Slot = c, Goal = c, Bounding = true };
                }
            }
            m.FlankStage = 2;
            return;
        }
        if (m.FlankStage == 2 && there == m.MoverCount)
        {
            for (var i = 0; i < m.LineList.Count; i++)
            {
                if (m.IsMover(m.LineList[i].Unit))
                {
                    m.LineList[i] = m.LineList[i] with { Bounding = false }; // their own positions round the flank cell (section 6)
                }
            }
            (m.FlankStage, m.FlankNoUntil) = (3, Tick + (uint)r[CombatValue.SqFlankRetryTicks]);
            MoraleFlanked(Unit(m.Main) is { Squad: not 0 } e ? Squad(e.Squad) : null); // E1b-4: the enemy is flanked (-10, brief 7.3)
            ManoeuvreStats.Flanks++;
            BackToFight(m);
        }
        _ = s;
    }

    // ---- 11.7: assault ----

    // AssaultTest (10.3's): the group within assaultRange of the nearest able member, R assaultR+, assaultMinAble able, the group
    // pinned (half or more at suppression pinned+); never in Hold or Ambush, in a still stance, or before AssaultNoUntil. Steady
    // (morale) comes with E1b-4; an enemy in a building (6.6, 14) is left.
    // INF-E1b-6 (row 1312, the coordinator's pursuit call on scene 4's seed 56): a squad that is not Broken itself may assault a Broken
    // or Routing enemy group (half or more of its contacts) whatever its own Steady or Shaken state; 10.3's Steady is for an enemy
    // that can still fight. The test's other terms stand (R, able members, pinned or in a building, assaultRange).
    private bool AssaultTest(Squad s, SquadMind m, InfantryCombatRules r) =>
        s.Mode is not (SquadMode.Hold or SquadMode.Ambush) && s.Order is OrderKind.Idle or OrderKind.AttackMove && Manoeuvres(s) &&
        (m.Spirit == MoraleState.Steady || Pursues(m)) &&
        Tick >= m.AssaultNoUntil && m.AbleCount >= r[CombatValue.SqAssaultMinAble] && m.Ratio >= r[CombatValue.SqAssaultR] && m.GroupCount > 0 &&
        m.GroupPinned * 2 >= m.GroupCount && NearestMember(s, m.GroupAt.Center()) <= (long)r[CombatValue.SqAssaultRange] * Fixed.OneRaw / 10;

    // Pursues (row 1312): not Broken itself, and the enemy group is Broken or Routing.
    private static bool Pursues(SquadMind m) => m.Spirit != MoraleState.Broken && m.GroupCount > 0 && m.GroupBroken * 2 >= m.GroupCount;

    // ThinkAssault (11.7): started on the first think (runners, grenades); then the runners run at the enemy nearest each, the rest
    // suppress (OrderSuppression); it ends with no able enemy within assaultEndRange of a member, and fails with half the runners
    // down, R under assaultFailR or assaultMaxTicks.
    private void ThinkAssault(Squad s, SquadMind m, InfantryCombatRules r)
    {
        if (m.AssaultTick == 0)
        {
            StartAssault(s, m, r);
            return;
        }
        var down = 0;
        for (var i = 0; i < m.MoverCount; i++)
        {
            down += Unit(m.Movers[i]) is { } u && Able(u) ? 0 : 1;
        }
        if (down * 2 >= m.MoverCount || m.Ratio < r[CombatValue.SqAssaultFailR] || Tick - m.AssaultTick >= (uint)r[CombatValue.SqAssaultMaxTicks])
        {
            EndAssault(m, true, r);
            return;
        }
        var end = (long)r[CombatValue.SqAssaultEndRange] * Fixed.OneRaw / 10;
        var near = false;
        foreach (var k in m.PictureList)
        {
            near |= k.Enemy != 0 && Unit(k.Enemy) is { Alive: true } e && Able(e) && e.Inside == 0 && NearestMember(s, e.Pos) <= end;
        }
        if (!near)
        {
            EndAssault(m, false, r); // Consolidate (18) comes with part 3: the squad fights in place, then calms
            return;
        }
        if (Tick < m.RushAt)
        {
            return;
        }
        _lineTaken.Clear();
        for (var i = 0; i < m.LineList.Count; i++)
        {
            var p = m.LineList[i];
            if (!m.IsMover(p.Unit) || Unit(p.Unit) is not { } u || !Able(u) || RushCell(m, u) is not { } c)
            {
                continue;
            }
            c = NearSide(u, c); // part 5 (the coordinator's answer 2): his rush cell on his side of a wall across his way
            _lineTaken.Add(c);
            m.LineList[i] = p with { Slot = c, Goal = c, Bounding = true };
        }
    }

    // RushCell: the cell a step short of the able enemy of the picture nearest u, on u's side (that enemy's cell when it isn't
    // standable or is taken), or null.
    private Cell? RushCell(SquadMind m, Unit u)
    {
        Unit? best = null;
        var bestD = long.MaxValue;
        foreach (var k in m.PictureList)
        {
            if (k.Enemy != 0 && Unit(k.Enemy) is { Alive: true } e && Able(e) && e.Inside == 0 && (e.Pos - u.Pos).LenSq() is var d && d < bestD)
            {
                (best, bestD) = (e, d);
            }
        }
        if (best is null)
        {
            return null;
        }
        var (dx, dy) = Directions[Dir32(u.Pos.X.Raw - best.Pos.X.Raw, u.Pos.Y.Raw - best.Pos.Y.Raw) & 31];
        var c = Cell.Of(OnMap(new Vec(Fixed.FromRaw(best.Pos.X.Raw + dx * Fixed.OneRaw / 1024), Fixed.FromRaw(best.Pos.Y.Raw + dy * Fixed.OneRaw / 1024))));
        return Standable(c) && !_lineTaken.Contains(c) ? c : Cell.Of(best.Pos);
    }

    // StartAssault (11.7 steps 1 and 2): the runners (assaultShare of the able, at least 1, the men of the line nearest the group);
    // up to assaultGrenades grenades by the members nearest the group (the squad's cooldown ignored), the rush waiting
    // assaultGrenadeWait for them; a flank ends (its men may run).
    private void StartAssault(Squad s, SquadMind m, InfantryCombatRules r)
    {
        ManoeuvreStats.Assaults++;
        m.FlankStage = 0;
        PickMovers(s, m, m.GroupAt, Math.Max(1, m.AbleCount * r[CombatValue.SqAssaultShare] / 1000), false);
        if (m.MoverCount == 0)
        {
            m.AssaultNoUntil = Tick + (uint)r[CombatValue.SqAssaultRetryTicks];
            BackToFight(m);
            return;
        }
        var threw = 0;
        var g = m.GroupAt.Center();
        for (var k = 0; k < r[CombatValue.SqAssaultGrenades]; k++)
        {
            Unit? pick = null;
            var pickD = long.MaxValue;
            foreach (var id in s.MemberList)
            {
                if (Unit(id) is { Fight: { Grenades: > 0, ThrowAt: 0 } } u && Able(u) && (u.Pos - g).LenSq() is var d && d < pickD)
                {
                    (pick, pickD) = (u, d);
                }
            }
            if (pick is null || !AssaultThrow(pick, m, r))
            {
                break;
            }
            threw++;
        }
        (m.AssaultTick, m.RushAt) = (Tick, Tick + (threw > 0 ? (uint)r[CombatValue.SqAssaultGrenadeWait] : 0));
    }

    // AssaultThrow (11.7 step 1, 14.1's throw without the squad's cooldown): a group enemy in cover toward u within his throwing
    // range, no friend near the aim point.
    private bool AssaultThrow(Unit u, SquadMind m, InfantryCombatRules r)
    {
        var f = u.Fight!;
        if (f.Suppression >= r[CombatValue.PinnedHard] || f.PostureLeft > 0 || u.Life is { Busy: true } || u.Def.Weapons.Count == 0)
        {
            return false;
        }
        var lo = (long)r[CombatValue.GrenadeMinRange] * Fixed.OneRaw / 10;
        var hi = (long)(f.Posture == Posture.Prone ? r[CombatValue.GrenadeProneRange] : r[CombatValue.GrenadeMaxRange]) * Fixed.OneRaw / 10;
        var gr = (long)r[CombatValue.SqGroupRadius] * Fixed.OneRaw / 10;
        foreach (var k in m.PictureList)
        {
            if (k.Enemy == 0 || !InGroup(m, k, gr / 2) || Unit(k.Enemy) is not { Alive: true, Inside: 0 } e || e.Life is { Wound: WoundState.Down } ||
                !GrenadeAim(u, k, e, r, out var aim, out var key))
            {
                continue;
            }
            var d2 = (aim - u.Pos).LenSq();
            var toMe = Dir32(u.Pos.X.Raw - aim.X.Raw, u.Pos.Y.Raw - aim.Y.Raw);
            if (d2 < lo * lo || d2 > hi * hi || (e.Garrison == 0 && CoverToward(k.At, toMe, r) is not (CoverKind.Low or CoverKind.High)) || // (INF-E1b-8: or a building)
                FriendNear(u.Owner, aim, (long)r[CombatValue.GrenadeFriendClear] * Fixed.OneRaw / 10))
            {
                continue;
            }
            f.ThrowAt = Tick + (uint)r[CombatValue.GrenadeThrowTicks];
            (f.ThrowTo, f.LastThrow) = (aim, Tick);
            f.Grenades--;
            u.AutoTarget = 0;
            var oldest = 0;
            for (var i = 1; i < m.Thrown.Length; i++)
            {
                oldest = m.Thrown[i].Tick < m.Thrown[oldest].Tick ? i : oldest;
            }
            m.Thrown[oldest] = (key, Tick);
            ThinkStats.Throws++;
            return true;
        }
        return false;
    }

    // EndAssault: the runners take positions where they are (section 6) and the squad fights in place; a failed one waits
    // assaultRetryTicks before the next.
    private void EndAssault(SquadMind m, bool failed, InfantryCombatRules r)
    {
        MoversHere(m);
        (m.AssaultTick, m.RushAt, m.MoverCount) = (0, 0, 0);
        if (failed)
        {
            m.AssaultNoUntil = Tick + (uint)r[CombatValue.SqAssaultRetryTicks];
            ManoeuvreStats.AssaultFails++;
        }
        if (m.Choice == SquadChoice.Assault)
        {
            BackToFight(m);
        }
    }

    // RunnersFar (11.7 step 2): the rest suppress until the nearest runner is within assaultShiftRange of the group.
    private bool RunnersFar(SquadMind m, InfantryCombatRules r)
    {
        var shift = (long)r[CombatValue.SqAssaultShiftRange] * Fixed.OneRaw / 10;
        for (var i = 0; i < m.MoverCount; i++)
        {
            if (Unit(m.Movers[i]) is { } u && Able(u) && (u.Pos - m.GroupAt.Center()).LenSq() <= shift * shift)
            {
                return false;
            }
        }
        return true;
    }
}
