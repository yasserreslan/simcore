using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The Retreat order (INF-E2a; owner decision 17; the game repo's infantry-combat-brief 7.5): CommandKind.Retreat. The squad falls
// back away from the threat it knows (an enemy its side sees within retreat.threatSearch of it; else where the last shot that hurt a
// member came from; else straight ahead of it) to the best hard cover retreat.min to retreat.max away (Hold's cover points; ties: the
// nearer, then the middle), or to the dragged point (Cell2). It goes loose and moves in pairs, bounding: one pair walks boundMin to
// boundMax toward its places while the rest stand and fire at what they see; the next pair goes once both are there or after
// boundTicks. Downed men are carried if a squadmate can (RunCare: to the retreat's spot). Everyone there: Hold at the spot facing
// the threat (SquadHoldSystem.cs). Any other order to a member ends it (stance and weapon modes don't). It never changes morale.
public sealed partial class World
{
    // RetreatCommand: the squads of the listed members (ID order) retreat.
    private void RetreatCommand(Command c)
    {
        if (Content.Soldiers is not { } r || Content.Squad is not { } rules)
        {
            return;
        }
        var squads = new List<Squad>();
        foreach (var id in c.Units)
        {
            if (Unit(id) is { Squad: not 0 } u && u.Owner == c.Player && Squad(u.Squad) is { } s && !squads.Contains(s))
            {
                squads.Add(s);
            }
        }
        squads.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var s in squads)
        {
            StartRetreat(s, c.Cell2, r, rules);
        }
    }

    private void StartRetreat(Squad s, Cell? to, SoldierRules r, SquadRules rules)
    {
        var able = RetreatAble(s);
        if (able.Count == 0)
        {
            return;
        }
        long cx = 0, cy = 0;
        foreach (var m in able)
        {
            (cx, cy) = (cx + m.Pos.X.Raw, cy + m.Pos.Y.Raw);
        }
        var mid = new Vec(Fixed.FromRaw(cx / able.Count), Fixed.FromRaw(cy / able.Count));
        var threat = KnownThreat(s, mid, r);
        var away = mid - threat;
        away = away.LenSq() == 0 ? new Vec(Fixed.Zero, Fixed.One) : away.WithLen(Fixed.One);
        var side = new Vec(Fixed.FromRaw(-away.Y.Raw), away.X);
        Cell spot;
        if (to is { } p)
        {
            spot = new Cell(Math.Clamp(p.X, 0, Grid.Width - 1), Math.Clamp(p.Y, 0, Grid.Height - 1));
        }
        else if (BestRetreatCell(mid, away, side, threat, r, rules) is { } best)
        {
            spot = best;
        }
        else
        {
            return; // nowhere to go
        }
        LeaveAmbush(s);
        LeaveHold(s);
        Loosen(s);
        s.Retreating = true;
        s.RetreatSpot = spot;
        s.RetreatThreat = threat;
        (s.RetreatPair, s.RetreatBoundUntil) = (-1, 0);
        s.RetreatMoving.Clear();
        s.RetreatPlaces.Clear();
        var n = able.Count;
        for (var i = 0; i < n; i++)
        {
            var m = able[i];
            var off = Fixed.FromRaw((long)(2 * i - (n - 1)) * Fixed.OneRaw * 15 / 20); // 1.5 cells apart across the way back
            var cell = Cell.Of(spot.Center() + new Vec(side.X * off, side.Y * off));
            cell = new Cell(Math.Clamp(cell.X, 0, Grid.Width - 1), Math.Clamp(cell.Y, 0, Grid.Height - 1));
            s.RetreatPlaces.Add((m.Id, Grid.TrySpiral(cell, 0, out var free) ? free : cell));
            if (m.Life is not { Busy: true })
            {
                m.ResetAi();
                Halt(m);
            }
        }
        _soldierEvents.Add(new SoldierEvent(SoldierEventKind.RetreatStart, s.Leader, s.Id, 0, spot.Center()));
    }

    // RetreatAble: the members who retreat on their feet: alive, on the map, able to act, not down.
    private List<Unit> RetreatAble(Squad s)
    {
        var able = _careAble;
        able.Clear();
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Inside: 0, Garrison: 0 } m && m.Alive && m.CanAct && m.Def.Speed.Raw > 0)
            {
                able.Add(m);
            }
        }
        return able;
    }

    // KnownThreat: the nearest enemy its side sees within threatSearch of mid (ties: the lower ID); else where the latest shot that
    // hurt a member came from; else 10 cells ahead of the squad.
    private Vec KnownThreat(Squad s, Vec mid, SoldierRules r)
    {
        var search = Fixed.Frac(r[SoldierValue.ThreatSearch], 10);
        Unit? near = null;
        foreach (var e in _units)
        {
            if (e.Owner != s.Owner && e.Owner != 0 && e.Alive && e.Def.Weapons.Count > 0 && IsSpotted(e, s.Owner) && Within(e.Pos, mid, search) &&
                (near is null || (e.Pos - mid).LenSq() < (near.Pos - mid).LenSq()))
            {
                near = e;
            }
        }
        if (near is not null)
        {
            return near.Pos;
        }
        Unit? hit = null;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { LastHitTick: > 0 } m && (hit is null || m.LastHitTick > hit.LastHitTick))
            {
                hit = m;
            }
        }
        if (hit is not null)
        {
            return hit.LastHitFrom;
        }
        var f = s.Facing.LenSq() == 0 ? new Vec(Fixed.Zero, -Fixed.One) : s.Facing.WithLen(Fixed.One);
        return mid + new Vec(f.X * Fixed.FromInt(10), f.Y * Fixed.FromInt(10));
    }

    // BestRetreatCell: the standable cell min to max away from mid along `away` (1-cell steps, up to 3 cells either side) with the most
    // cover from the threat (Hold's points; none without Hold's rules); ties: the nearer, then the middle, then the left.
    private Cell? BestRetreatCell(Vec mid, Vec away, Vec side, Vec threat, SoldierRules r, SquadRules rules)
    {
        Cell? best = null;
        var bestScore = -1;
        for (var d = r[SoldierValue.RetreatMin]; d <= r[SoldierValue.RetreatMax]; d += 10)
        {
            ReadOnlySpan<int> lats = [0, -10, 10, -20, 20, -30, 30];
            foreach (var lat in lats)
            {
                var dd = Fixed.Frac(d, 10);
                var ll = Fixed.Frac(lat, 10);
                var c = Cell.Of(mid + new Vec(away.X * dd + side.X * ll, away.Y * dd + side.Y * ll));
                if (!Grid.Contains(c) || !Grid.Passable(c))
                {
                    continue;
                }
                var score = rules.Hold is { } hold ? CoverPoints(c, threat, hold, Ground?.Forest(c) ?? 0) : 0;
                if (score > bestScore)
                {
                    (best, bestScore) = (c, score);
                }
            }
        }
        return best;
    }

    // StepRetreat runs a retreating squad's bounds; everyone there, it takes Hold facing the threat.
    private void StepRetreat(Squad s, SoldierRules r)
    {
        var arrive = Fixed.Frac(r[SoldierValue.RetreatArrive], 10);
        var able = RetreatAble(s);
        var left = new List<Unit>();
        foreach (var m in able)
        {
            if (m.Life is { Busy: true })
            {
                continue; // carrying a downed man: on his own walk
            }
            if (PlaceOf(s, m.Id) is { } place && !Within(m.Pos, place.Center(), arrive))
            {
                left.Add(m);
            }
        }
        if (left.Count == 0)
        {
            EndRetreat(s, true);
            return;
        }
        var moving = false;
        foreach (var id in s.RetreatMoving)
        {
            moving |= Unit(id) is { Alive: true, Order.Kind: OrderKind.Move } && left.Exists(m => m.Id == id);
        }
        if (moving && Tick < s.RetreatBoundUntil)
        {
            return;
        }
        // The next pair bounds toward its places; the others stand (and fire at what they see).
        var pairs = (left.Count + 1) / 2;
        s.RetreatPair = (s.RetreatPair + 1) % pairs;
        s.RetreatMoving.Clear();
        foreach (var m in left)
        {
            if (m.Order.Kind == OrderKind.Move)
            {
                Halt(m);
            }
        }
        for (var k = 2 * s.RetreatPair; k < left.Count && k < 2 * s.RetreatPair + 2; k++)
        {
            var m = left[k];
            var place = PlaceOf(s, m.Id)!.Value.Center();
            var to = place - m.Pos;
            var len = to.Len().Raw;
            long max = Fixed.OneRaw * r[SoldierValue.BoundMax] / 10, min = Fixed.OneRaw * r[SoldierValue.BoundMin] / 10;
            var step = len - max < min ? len : max;
            var at = step >= len ? place : m.Pos + to.WithLen(Fixed.FromRaw(step));
            m.ResetAi();
            WalkTo(m, Cell.Of(at), 0);
            s.RetreatMoving.Add(m.Id);
        }
        s.RetreatBoundUntil = Tick + (uint)Math.Max(1, r[SoldierValue.BoundTicks]);
    }

    private static Cell? PlaceOf(Squad s, uint unit)
    {
        foreach (var (u, c) in s.RetreatPlaces)
        {
            if (u == unit)
            {
                return c;
            }
        }
        return null;
    }

    // EndRetreat: there (Hold at the spot facing the threat, with Hold's rules) or ended by another order.
    private void EndRetreat(Squad s, bool there)
    {
        s.Retreating = false;
        s.RetreatPlaces.Clear();
        s.RetreatMoving.Clear();
        _soldierEvents.Add(new SoldierEvent(SoldierEventKind.RetreatEnd, s.Leader, s.Id, there ? 1 : 0, s.RetreatSpot.Center()));
        if (!there || Content.Squad is not { Hold: { } hold } rules)
        {
            return;
        }
        var t = Cell.Of(s.RetreatThreat);
        HoldOrder(s, s.RetreatSpot, new Cell(Math.Clamp(t.X, 0, Grid.Width - 1), Math.Clamp(t.Y, 0, Grid.Height - 1)), rules, hold);
    }

    // CancelRetreats: an order (other than a stance, a weapon or fire mode, a scavenge or another retreat) to a member of a
    // retreating squad ends its retreat.
    private void CancelRetreats(Command c)
    {
        if (c.Kind is CommandKind.Retreat or CommandKind.SetStance or CommandKind.SetWeaponMode or CommandKind.SetFireMode or CommandKind.Scavenge ||
            !_soldiers)
        {
            return;
        }
        foreach (var id in c.Units)
        {
            if (Unit(id) is { Squad: not 0 } u && u.Owner == c.Player && Squad(u.Squad) is { Retreating: true } s)
            {
                EndRetreat(s, false);
            }
        }
    }
}
