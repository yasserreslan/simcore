using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Multi-target ground attack (MT; docs/design.md, "Multi-target ground attack"): one objective of ordered points and enemy units
// for a whole selection. All integer; objectives by ID, members by unit ID, points in the order placed; no random draws.
//
//  - The order (CommandKind.MultiTarget): its aims (up to MaxMultiAims; a point twice or a unit twice counts once; points off the
//    map, the player's own units and untargetable pieces dropped) become the objective's points; its units that can take one of
//    them become its members (an aircraft that cannot says why: World.OrderRefusals). A unit leaves any objective it was on.
//  - A shot: what a unit puts on one point. A ground or helicopter weapon's one munition, or its whole salvo for a weapon whose
//    volleyPerTarget is set (the rocket trucks' and launchers' ripples: the default for a salvo); a split salvo (volleyPerTarget
//    false) aims each next round at the unit's next point while that is in reach. A fixed wing's pass that released (its fire
//    mode's group of bombs, or a strafing pass's bursts). A bomber in a spread fire mode (carpet20, block24) takes its first mode
//    that is not spread while it is on the objective (salvo aims: one bomb group a point), and gets its mode back after.
//  - The assignment: each free member takes a point with the fewest shots and units on it (every point covered before any gets
//    a second; then the spare shots spread evenly); of those first the point the fewest free units can take (a point in reach
//    of only a gun that cannot move is not left uncovered), then the nearest (unit and point together, straight-line), ties by
//    unit ID, then point order. At the order every member is free; afterwards a member is free once its shot is delivered, its
//    aimed-at unit is gone or it gave its point up (the point is never given to it again). A member that can take no point
//    (dry, or every point it could fire at gone) leaves. A dead member or one that cannot act leaves, and its point goes to
//    the next unit that is free.
//  - Movement and fire: a point is given as the member's own Attack-ground (or Attack) order, through the command path, so it
//    walks or drives into range, sets up, flies its attack run as for any such order. A fixed wing already on a strike flies on
//    toward its next point after its break (AttackPlanSystem's Retarget).
//  - Another player order to a member takes it out (its order is that order); Stop too.
//  - A point counts as hit when its shot is released or fired (not on impact): a shot intercepted still counts. A point hit only
//    by another point's splash is not hit: every point gets its own shot. The objective is complete (CompleteTick) once every
//    point is hit or its unit gone; its members go on spreading their spare shots until they run dry, as Ground attack does.
public sealed partial class World
{
    /// <summary>The most aims a <see cref="CommandKind.MultiTarget"/> order may hold (a command with more is rejected).</summary>
    public const int MaxMultiAims = 64;

    private readonly List<MultiTarget> _multi = []; // by ID, ascending
    private uint _nextMulti;

    // Scratch for the assignment (not state).
    private readonly List<MultiMember> _multiFree = [];
    private readonly List<Unit> _multiUnits = [];
    private long[] _multiDist = [];
    private bool[] _multiCan = [];

    /// <summary>The live multi-target objectives, by ID (<see cref="CommandKind.MultiTarget"/>).</summary>
    public IReadOnlyList<MultiTarget> MultiTargets => _multi;

    /// <summary>The objective unit is on, or null.</summary>
    public MultiTarget? MultiTargetOf(uint unit)
    {
        foreach (var m in _multi)
        {
            if (m.Member(unit) is not null)
            {
                return m;
            }
        }
        return null;
    }

    /// <summary>
    /// The assignment a <see cref="CommandKind.MultiTarget"/> order of player's units at aims would start with now, without giving it:
    /// for each point in order, the IDs of the units it would take first (the client's lines while the points are placed). Units
    /// that are not the player's or cannot take any point, and aims the order would drop, are left out. Pure: it changes nothing.
    /// </summary>
    public IReadOnlyList<(int Point, uint Unit)> PreviewMultiTarget(byte player, IReadOnlyList<uint> units, IReadOnlyList<MultiAim> aims)
    {
        var m = new MultiTarget(0, player, Tick);
        AddMultiPoints(m, player, aims);
        var free = new List<MultiMember>();
        foreach (var u in MultiCandidates(player, units))
        {
            free.Add(new MultiMember(u.Id));
        }
        var picks = new List<(int, uint)>();
        PlanMulti(m, free, (mm, u, j) =>
        {
            picks.Add((j, u.Id));
            return true;
        });
        return picks;
    }

    // ---- The order ----

    private void ApplyMultiTarget(Command c)
    {
        if (c.Aims is not { Count: > 0 })
        {
            return;
        }
        var m = new MultiTarget(_nextMulti + 1, c.Player, Tick);
        AddMultiPoints(m, c.Player, c.Aims);
        if (m.PointList.Count == 0)
        {
            return;
        }
        var units = MultiCandidates(c.Player, c.Units, refuse: true);
        LeaveMultiTargets(c.Units);
        if (units.Count == 0)
        {
            return;
        }
        _nextMulti++;
        var free = _multiFree;
        free.Clear();
        foreach (var u in units)
        {
            var mm = new MultiMember(u.Id);
            SalvoAimMode(u, mm);
            m.MemberList.Add(mm);
            free.Add(mm);
        }
        AssignMulti(m, free, true);
        DropUnplaced(m, free);
        if (m.MemberList.Count > 0)
        {
            _multi.Add(m);
        }
    }

    // AddMultiPoints adds aims to m's points: a point on the map once, an enemy unit (not an untargetable piece, not an aircraft
    // in the air) once.
    private void AddMultiPoints(MultiTarget m, byte player, IReadOnlyList<MultiAim> aims)
    {
        foreach (var a in aims)
        {
            if (m.PointList.Count >= MaxMultiAims)
            {
                break;
            }
            if (a.Target != 0)
            {
                if (Unit(a.Target) is not { Alive: true } t || t.Owner == player || t.Def.Archetype.Untargetable || AirBodyOfUnit(t) is not null ||
                    m.PointList.Exists(p => p.Target == t.Id))
                {
                    continue;
                }
                m.PointList.Add(new MultiPoint(Cell.Of(t.Pos), t.Id));
                continue;
            }
            if (Grid.Contains(a.Cell) && !m.PointList.Exists(p => p.Target == 0 && p.Cell == a.Cell))
            {
                m.PointList.Add(new MultiPoint(a.Cell, 0));
            }
        }
    }

    // MultiCandidates: the player's units of ids (once each, by ID) that could take part: commandable, armed for the ground. With
    // refuse, an aircraft without the weapons says why (E13, D-ORD1), as for Attack-ground.
    private List<Unit> MultiCandidates(byte player, IReadOnlyList<uint> ids, bool refuse = false)
    {
        var list = new List<Unit>();
        foreach (var id in ids)
        {
            var u = Unit(id);
            if (!Commandable(u, player) || !u!.Alive || list.Contains(u) || HasSideFire(u.Def))
            {
                continue; // a gunship fires at what it sees round its circle, never at a point: not for this order
            }
            if (!MultiArmed(u))
            {
                if (refuse && u.Def.Archetype.Flight is not null && u.Heli is null && u.Def.Weapons.Count > 0)
                {
                    Refuse(u, OutOf(u, null), 0, default);
                }
                continue;
            }
            list.Add(u);
        }
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    // MultiArmed: u has a weapon for the ground now (a parked jet whose full load has one: it rearms first, D-ORD2).
    private bool MultiArmed(Unit u)
    {
        if (u.Heli is not null)
        {
            return HeliGroundWeapon(u) >= 0;
        }
        if (u.Def.Archetype.Flight is not null)
        {
            return AirGroundWeapon(u, null) is not null || StrikeWeapon(u) is not null || RearmsForGround(u);
        }
        return GroundWeapon(u, false) >= 0;
    }

    // RearmsForGround: aircraft u is parked (or rolling out, taxiing in) at home and its full loadout has a ground weapon (the
    // implicit rearm of D-ORD2; DockedRearm's test without its effect).
    private static bool RearmsForGround(Unit u)
    {
        if (u.AirOps is not { Phase: >= FlightPhase.Rollout and <= FlightPhase.Parked })
        {
            return false;
        }
        foreach (var w in u.Def.Weapons)
        {
            if (w.Stock >= 0 && u.StockMax(w.Stock) > 0 && (w.Effect.Targets & Layers.Ground) != 0 && w.Effect.Airdrop is null &&
                (w.Effect.Delivery is not null || w.Effect.AirGun is not null))
            {
                return true;
            }
        }
        return false;
    }

    // MultiCan: u can take point p now (pure). A ground point: a weapon for the ground, the point not inside its minimum range from
    // where it stands (a unit that cannot move: the point in its range). An enemy unit: still there, and a weapon that hurts it (a ground unit or a helicopter: only while its side sees it).
    private bool MultiCan(Unit u, MultiPoint p)
    {
        if (p.Target == 0)
        {
            if (u.Heli is not null || u.Def.Archetype.Flight is not null)
            {
                return MultiArmed(u);
            }
            // (a point inside its minimum range from where it stands is not its: Attack-ground closes in, it never backs off)
            var shot = ShotAt(u, p.Cell.Center());
            return GroundWeapon(u, false) >= 0 && (u.Def.Speed.Raw > 0 ? shot != GroundShot.TooClose : shot == GroundShot.InRange);
        }
        if (p.Gone || Unit(p.Target) is not { Alive: true, Inside: 0, Garrison: 0 } t || t.Owner == u.Owner)
        {
            return false;
        }
        if (u.Heli is not null)
        {
            return IsVisibleTo(t, u.Owner) && HeliCanHit(u, t);
        }
        if (u.Def.Archetype.Flight is not null)
        {
            return AirGroundWeapon(u, t) is not null || (StrikeWeapon(u) is not null && IsVisibleTo(t, u.Owner)) || RearmsForGround(u);
        }
        return IsVisibleTo(t, u.Owner) && PickWeapon(u, t, false) >= 0 && (u.Def.Speed.Raw > 0 || PickWeapon(u, t, true) >= 0);
    }

    // SalvoAimMode: a bomber in a spread fire mode (one stick for the pass: carpet20, block24) takes its first mode that is not
    // spread (salvo aims: one bomb group a point) while it is on the objective; mm keeps the mode to give back.
    private static void SalvoAimMode(Unit u, MultiMember mm)
    {
        var modes = FireModesOf(u);
        if (u.FireMode < 0 || u.FireMode >= modes.Count || !modes[u.FireMode].Spread)
        {
            return;
        }
        for (var i = 0; i < modes.Count; i++)
        {
            if (!modes[i].Spread)
            {
                mm.ModeBefore = u.FireMode;
                u.FireMode = i;
                return;
            }
        }
    }

    // LeaveMultiTargets takes the units of ids off the objectives they are on (another order of the player's).
    private void LeaveMultiTargets(IReadOnlyList<uint> ids)
    {
        if (_multi.Count == 0)
        {
            return;
        }
        foreach (var id in ids)
        {
            for (var k = 0; k < _multi.Count; k++)
            {
                if (_multi[k].Member(id) is { } mm)
                {
                    DropMember(_multi[k], mm);
                }
            }
        }
        _multi.RemoveAll(m => m.MemberList.Count == 0);
    }

    // KeepsMultiTarget: command kinds that leave a unit on its objective (they give it no order: settings, production, sandbox).
    private static bool KeepsMultiTarget(CommandKind k) => k is CommandKind.SetWeaponMode or CommandKind.SetStance or
        CommandKind.SetFireMode or CommandKind.SetBombWeapon or CommandKind.Detonate or CommandKind.SetAutoDetonate or CommandKind.Smoke or
        CommandKind.Produce or CommandKind.CancelProduce or CommandKind.SetRally or CommandKind.CancelBuild or CommandKind.SandboxSpawn or
        CommandKind.SandboxSetHp or CommandKind.SandboxIgnite or CommandKind.SandboxUpgrade or CommandKind.MultiTarget;

    // DropMember takes mm off m: its point loses it, a bomber gets its fire mode back.
    private void DropMember(MultiTarget m, MultiMember mm)
    {
        if (mm.Point >= 0)
        {
            m.PointList[mm.Point].Assigned--;
            mm.Point = -1;
        }
        if (mm.ModeBefore >= 0 && Unit(mm.Unit) is { Alive: true } u && mm.ModeBefore < FireModesOf(u).Count)
        {
            u.FireMode = mm.ModeBefore;
        }
        m.MemberList.Remove(mm);
    }

    // DropUnplaced takes the members of free that got no point off m (they can take none).
    private void DropUnplaced(MultiTarget m, List<MultiMember> free)
    {
        foreach (var mm in free)
        {
            if (mm.Point < 0)
            {
                DropMember(m, mm);
            }
        }
        free.Clear();
    }

    // ---- The assignment ----

    // AssignMulti gives each member of free a point (see the header): the order of the point through the command path, or (a
    // fixed wing on a strike) its strike retargeted. A point whose order is refused is never given to that member again. fresh:
    // the order's own tick (a shot this tick counts: the commands run first).
    private void AssignMulti(MultiTarget m, List<MultiMember> free, bool fresh)
    {
        PlanMulti(m, free, (mm, u, j) =>
        {
            var p = m.PointList[j];
            if (!GiveMultiPoint(u, p))
            {
                mm.Refused |= 1UL << j;
                return false;
            }
            mm.Point = j;
            p.Assigned++;
            mm.From = Firing(u) ? uint.MaxValue : fresh ? Tick : Tick + 1;
            return true;
        });
    }

    // PlanMulti picks a point for each member of free in turn (see the header) and calls give; give false: that pair is refused and
    // the member is picked again. The members picked are taken off free; those left could take no point.
    private void PlanMulti(MultiTarget m, List<MultiMember> free, Func<MultiMember, Unit, int, bool> give)
    {
        var units = _multiUnits;
        units.Clear();
        for (var i = 0; i < free.Count; i++)
        {
            units.Add(Unit(free[i].Unit)!);
        }
        var pts = m.PointList;
        var n = free.Count * pts.Count;
        if (_multiDist.Length < n)
        {
            _multiDist = new long[n];
            _multiCan = new bool[n];
        }
        for (var i = 0; i < free.Count; i++)
        {
            var u = units[i];
            for (var j = 0; j < pts.Count; j++)
            {
                var k = i * pts.Count + j;
                _multiCan[k] = (free[i].Refused >> j & 1) == 0 && MultiCan(u, pts[j]);
                if (_multiCan[k])
                {
                    var at = MultiPointPos(pts[j]);
                    long dx = at.X.Raw - u.Pos.X.Raw, dy = at.Y.Raw - u.Pos.Y.Raw;
                    _multiDist[k] = dx * dx + dy * dy;
                }
            }
        }
        // How many of the free units can take each point: the scarcer point goes first among those with the fewest shots and
        // units (a point only a gun that cannot move reaches is not left to it alone after others took the rest).
        var few = new int[pts.Count];
        for (var k = 0; k < free.Count * pts.Count; k++)
        {
            few[k % pts.Count] += _multiCan[k] ? 1 : 0;
        }
        var left = new bool[free.Count];
        var waiting = free.Count;
        while (waiting > 0)
        {
            int bi = -1, bj = -1, bl = int.MaxValue, bf = int.MaxValue;
            var bd = long.MaxValue;
            for (var i = 0; i < free.Count; i++)
            {
                if (left[i])
                {
                    continue;
                }
                for (var j = 0; j < pts.Count; j++)
                {
                    var k = i * pts.Count + j;
                    if (!_multiCan[k])
                    {
                        continue;
                    }
                    var load = pts[j].Shots + pts[j].Assigned;
                    if (load < bl || (load == bl && (few[j] < bf || (few[j] == bf &&
                        (_multiDist[k] < bd || (_multiDist[k] == bd && units[i].Id < units[bi].Id))))))
                    {
                        (bi, bj, bl, bf, bd) = (i, j, load, few[j], _multiDist[k]);
                    }
                }
            }
            if (bi < 0)
            {
                break; // nobody left can take a point
            }
            if (give(free[bi], units[bi], bj))
            {
                left[bi] = true;
                waiting--;
                for (var j = 0; j < pts.Count; j++)
                {
                    few[j] -= _multiCan[bi * pts.Count + j] ? 1 : 0;
                }
                if (m.Id == 0)
                {
                    pts[bj].Assigned++; // a preview counts its own picks
                }
            }
            else
            {
                _multiCan[bi * pts.Count + bj] = false;
                few[bj]--;
            }
        }
        for (var i = free.Count - 1; i >= 0; i--)
        {
            if (left[i])
            {
                free.RemoveAt(i);
            }
        }
    }

    // MultiPointPos is where point p is: an aimed-at unit where its side last saw it (its cell), else the point's centre.
    private static Vec MultiPointPos(MultiPoint p) => p.Cell.Center();

    // GiveMultiPoint gives u point p as its order: a fixed wing on a strike flies on toward it (planned before its next run); every
    // other unit takes it through the command path (Attack-ground at the point, Attack at the unit). True when u now has it.
    private bool GiveMultiPoint(Unit u, MultiPoint p)
    {
        if (u.Strike is not null && StrikesByRun(u) && u.Order.Kind is OrderKind.Attack or OrderKind.AttackGround && u.AirOps is null)
        {
            Retarget(u, new StrikeAim(p.Target, p.Cell, default, false));
            return OnMultiPoint(u, p);
        }
        if (u.Order.Kind == OrderKind.AttackGround && u.HasNextGround)
        {
            (u.NextGround, u.HasNextGround) = (default, false); // a drone launcher's second point: the objective gives its points itself
        }
        if (u.Order.Kind == OrderKind.AttackGround && u.Def.Archetype.Flight is null && GroundWeapon(u, false) is >= 0 and var gw &&
            u.Def.Weapons[gw].Effect.Drone is not null)
        {
            u.Order = default; // a drone launcher on a point takes a new one as its second; the objective's is a new order
        }
        ApplyOrder(new Command
        {
            Tick = Tick, Player = u.Owner, Kind = p.Target != 0 ? CommandKind.Attack : CommandKind.AttackGround, Units = [u.Id],
            Cell = p.Target != 0 ? default : p.Cell, Target = p.Target,
        }, false);
        return OnMultiPoint(u, p);
    }

    // OnMultiPoint: u's order is point p's.
    private static bool OnMultiPoint(Unit u, MultiPoint p) => p.Target != 0
        ? u.Order.Kind == OrderKind.Attack && u.Order.Target == p.Target
        : u.Order.Kind == OrderKind.AttackGround && u.Order.Cell == p.Cell;

    // StrikesByRun: u is a fixed wing whose ground attack is the strike runs (its passes deliver its shots: MultiPassDone), not a
    // helicopter, a strike drone or a gunship.
    private static bool StrikesByRun(Unit u) =>
        u.Heli is null && u.Def.Archetype.Flight is not null && StrikeWeapon(u) is null && !HasSideFire(u.Def);

    // Firing: u is still firing a volley (a salvo, a missile it guides, a helicopter's salvo).
    private static bool Firing(Unit u) => u.SalvoLeft > 0 || u.Guiding != 0 || u.Heli is { SalvoLeft: > 0 };

    // ---- Each tick ----

    // RunMultiTargets (after the strikes): each objective's members are checked in ID order (dead or unable to act: off; a shot
    // delivered, its unit gone, its point given up: free), the free ones are given points, the objectives left without members
    // go, and an objective whose every point is done is complete.
    private void RunMultiTargets()
    {
        if (_multi.Count == 0)
        {
            return;
        }
        foreach (var m in _multi)
        {
            foreach (var p in m.PointList)
            {
                if (p.Target == 0 || p.Gone)
                {
                    continue;
                }
                if (Unit(p.Target) is not { Alive: true, Inside: 0, Garrison: 0 } t)
                {
                    p.Gone = true;
                }
                else if (IsVisibleTo(t, m.Owner))
                {
                    p.Cell = Cell.Of(t.Pos);
                }
            }
            var free = _multiFree;
            free.Clear();
            for (var i = 0; i < m.MemberList.Count; i++)
            {
                var mm = m.MemberList[i];
                var u = Unit(mm.Unit);
                if (u is not { Alive: true } || !Commandable(u, m.Owner))
                {
                    DropMember(m, mm);
                    i--;
                    continue;
                }
                if (mm.Point < 0)
                {
                    free.Add(mm);
                    continue;
                }
                var p = m.PointList[mm.Point];
                if (mm.From == uint.MaxValue && !Firing(u))
                {
                    mm.From = Tick + 1; // the volley of an earlier order is over: its own shots count from now
                }
                if (SplitSalvo(m, mm, u))
                {
                    continue;
                }
                var fired = !StrikesByRun(u) && mm.From != uint.MaxValue && u.LastShot >= mm.From && u.LastShot != 0 && !Firing(u); // (0: never fired)
                if (fired || p.Gone)
                {
                    if (fired)
                    {
                        MultiShot(p);
                    }
                    p.Assigned--;
                    mm.Point = -1;
                    free.Add(mm);
                    continue;
                }
                if (OnMultiPoint(u, p))
                {
                    continue;
                }
                p.Assigned--;
                mm.Point = -1;
                if (u.Order.Kind != OrderKind.Idle)
                {
                    DropMember(m, mm); // another order (a squad's, the system's): it goes its own way
                    i--;
                    continue;
                }
                mm.Refused |= 1UL << m.PointList.IndexOf(p); // given up (lost, refused): another point
                free.Add(mm);
            }
            if (free.Count > 0)
            {
                AssignMulti(m, free, false);
                DropUnplaced(m, free);
            }
            if (m.CompleteTick == 0 && m.Done == m.PointList.Count)
            {
                m.CompleteTick = Tick;
            }
        }
        _multi.RemoveAll(m => m.MemberList.Count == 0);
    }

    // MultiShot counts a shot at p.
    private void MultiShot(MultiPoint p)
    {
        if (p.Shots == 0)
        {
            p.HitTick = Tick;
        }
        p.Shots++;
    }

    // MultiPassDone (StartBreak): fixed wing u's pass released; on an objective, that is its shot at its point, and it flies on
    // toward its next (retargeted before its break).
    private void MultiPassDone(Unit u, StrikeRun run)
    {
        if (_multi.Count == 0 || run.Released == 0)
        {
            return;
        }
        foreach (var m in _multi)
        {
            if (m.Member(u.Id) is not { Point: >= 0 } mm)
            {
                continue;
            }
            var p = m.PointList[mm.Point];
            if (!OnMultiPoint(u, p))
            {
                return;
            }
            MultiShot(p);
            p.Assigned--;
            mm.Point = -1;
            var free = new List<MultiMember> { mm };
            AssignMulti(m, free, false);
            return; // a member left without a point leaves in the tick's pass
        }
    }

    // SplitSalvo (volleyPerTarget false on a salvo weapon): a round of u's salvo left this tick; it counts at u's point and the
    // rest of the salvo aims at its next point (the fewest shots, the nearest) if that is in the weapon's reach from where it stands
    // and its launcher can point there; else the salvo stays on its point. True when it handled u this tick.
    private bool SplitSalvo(MultiTarget m, MultiMember mm, Unit u)
    {
        if (u.LastShot != Tick || u.LastShot == 0 || mm.From == uint.MaxValue || u.LastShot < mm.From)
        {
            return false;
        }
        WeaponDef w;
        if (u.Heli is { SalvoLeft: > 0 } h)
        {
            w = u.Def.Weapons[h.SalvoWeapon];
        }
        else if (u.SalvoLeft > 0 && u.Heli is null)
        {
            w = u.Def.Weapons[u.SalvoWeapon];
        }
        else
        {
            return false;
        }
        if (w.Effect.VolleyPerTarget)
        {
            return false;
        }
        var cur = m.PointList[mm.Point];
        MultiShot(cur);
        var best = -1;
        var bd = long.MaxValue;
        var bl = int.MaxValue;
        for (var j = 0; j < m.PointList.Count; j++)
        {
            var p = m.PointList[j];
            if (j == mm.Point || (mm.Refused >> j & 1) != 0 || !MultiCan(u, p) || !SalvoReaches(u, w, p))
            {
                continue;
            }
            var at = MultiPointPos(p);
            long dx = at.X.Raw - u.Pos.X.Raw, dy = at.Y.Raw - u.Pos.Y.Raw;
            var d = dx * dx + dy * dy;
            var load = p.Shots + p.Assigned;
            if (load < bl || (load == bl && d < bd))
            {
                (best, bl, bd) = (j, load, d);
            }
        }
        if (best < 0 || bl > cur.Shots + cur.Assigned - 1)
        {
            mm.From = Tick + 1; // no better point in reach: the next round goes at this one again
            return true;
        }
        var next = m.PointList[best];
        cur.Assigned--;
        next.Assigned++;
        mm.Point = best;
        mm.From = Tick + 1;
        var t = next.Target != 0 ? Unit(next.Target) : null;
        var aim = t is not null ? Lead(u, t, w) : next.Cell.Center();
        if (u.Heli is { } hh)
        {
            (hh.SalvoAim, hh.SalvoTarget) = (aim, t?.Id ?? 0);
        }
        else
        {
            (u.SalvoAim, u.SalvoTarget) = (aim, t?.Id ?? 0);
        }
        u.Order = t is not null ? new Order(OrderKind.Attack, next.Cell, t.Id, 0) : new Order(OrderKind.AttackGround, next.Cell, 0, 0);
        return true;
    }

    // SalvoReaches: the rest of u's salvo of w can go at p from where it stands: in reach, not too close, and (a launcher aimed with its
    // hull) within its arc.
    private bool SalvoReaches(Unit u, WeaponDef w, MultiPoint p)
    {
        var at = p.Target != 0 && Unit(p.Target) is { } t ? AimPoint(u.Pos, t) : p.Cell.Center();
        if (!InReach(u, at, w) || TooClose(u.Pos, at, w))
        {
            return false;
        }
        var a = u.Def.Archetype;
        if (u.Heli is not null || a.VerticalLaunch || u.Def.Turret)
        {
            return true;
        }
        return Facing.TryFromDirection(at - u.Pos, out var want) && Math.Abs(Facing.Diff(u.Facing, want)) <= Math.Max(a.HullArc, FireAlignFacings);
    }
}
