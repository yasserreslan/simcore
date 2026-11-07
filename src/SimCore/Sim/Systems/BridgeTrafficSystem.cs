using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Bridge traffic (docs/design.md, "Bridge traffic"; decisions 937-945). Columns that meet on a deck used to press head-on until
// they squeezed past: the deck is walled by water, so a dodge finds no room, and a column two abreast fills it. Nothing else
// on the map is like that: on open ground units pass each other as they always did.
//
// This step runs last in the path system (after every unit's intent is written, before the movement system reads them) and
// only overrides the intents of ground units on their way across a standing span while others come the other way. Alone or
// all going one way, a unit crosses as it always did and nothing is touched. Per span, by unit ID, then nearest first:
//  - Who crosses: a unit whose next waypoints (or, short of the deck, what its route or flow field says next) cross the
//    deck's middle line. One on the span counts until it is off it.
//  - Vehicles keep right: they aim for their lane's point on the near bank, then on the far one, so two that fit side by side
//    pass without touching, whatever the bridge. Soldiers weave as they always did.
//  - Two that don't fit (their widths and a little more than the deck's) take turns: one way has the flow (BridgeState.Flow)
//    and the others queue on the bank to their right, in single file, clear of the way off the deck, and go when it is theirs.
//    One that fits beside the widest of those coming goes on. Whoever is on the span is never stopped, or turned round.
//  - A turn ends when its side has nobody left, or when it has no one near and the others stand ready, or after the content's
//    bridgeTurnTicks with the others waiting: then it lets through only those close enough to be committed. Neither side
//    waits for ever: twice bridgeTurnTicks and the turn passes even if something sits on the deck.
//  - A contest lasts until nobody is on the way across any more, so a queue set free on the bank still drives to its lane.
//
// What stands on a deck: a unit standing idle or firing is pushed along by those who need the way, as anywhere. An enemy holds its
// ground until it is dead, as anywhere. An empty vehicle, which nothing moves anywhere else, would shut the span for good: on a
// span whoever needs the way shoves it along, friend or foe, until it is off (MovementSystem: MoveScratch.Shoved). A launcher
// deployed on a deck stays where its owner put it.
//
// The flow is game state (hashed in BridgeState). Everything else is worked out afresh every tick.
public sealed partial class World
{
    private const long TrafficZone = 26 * Fixed.OneRaw;     // how far from the span a unit counts
    private const int TrafficWays = 12;                     // waypoints of its path looked along for the crossing
    private const int TrafficLegs = 2;                      // legs of its route after those
    private const int TrafficField = 32;                    // or cells down its flow field
    private const long LaneRange = 8 * Fixed.OneRaw;        // how far from the span a vehicle starts for its lane
    private const long LaneMark = 2 * Fixed.OneRaw;         // its lane's points, this far beyond each end of the span
    private const long LanePass = 1536;                     // it moves on from the near one this far short of it (raw: 1.5 cells)
    private const long LaneAhead = 3 * Fixed.OneRaw;        // its next waypoint is at least this far ahead of it down the road
    private const long LaneMargin = 51;                     // a lane's edge stays this far from the deck's (raw: a twentieth of a cell)
    private const long PassGap = 102;                       // two pass if their widths and this fit the deck (a tenth of a cell)
    private const long HoldGap = 6 * Fixed.OneRaw;          // the queue's head waits this far from the span: room to swing into the lane
    private const long SlotGap = 1 * Fixed.OneRaw;          // between bodies in a queue
    private const long SlotAside = 820;                     // and between the queue and the way off the deck (raw: 0.8 of a cell)
    private const long QueueReach = 24 * Fixed.OneRaw;      // the queue stands aside this far from the span; beyond, on its own way
    private const long SlotReach = 16 * Fixed.OneRaw;       // how far off a unit starts for its place in the queue
    private const long SlotNear = 307;                      // and how near it is there at least (raw: 0.3 of a cell; half a vehicle's radius if more)
    private const long CommitGap = 4 * Fixed.OneRaw;        // this close to the span when its turn ends, a unit still goes
    private const long FlowGap = 12 * Fixed.OneRaw;         // a side with nobody this close gives way to a side ready at the bank
    private const long ReadyGap = 14 * Fixed.OneRaw;        // ready: this close to the span
    private const long ShoveReach = 6 * Fixed.OneRaw;       // an empty vehicle is shoved until it is this far off the span: room to pass it

    private readonly List<TrafficUnit> _traffic = [];

    private readonly record struct TrafficUnit(Unit U, int Dir, long Along, long Side, long Dist);

    private static readonly Comparison<TrafficUnit> NearestFirst = (a, b) =>
        a.Dist != b.Dist ? a.Dist.CompareTo(b.Dist) : a.U.Id.CompareTo(b.U.Id);

    /// <summary>Test switch: no bridge traffic, so tests can measure what it changes. Not game state.</summary>
    internal bool NoBridgeTraffic { get; set; }

    private void RunBridgeTraffic(Router r)
    {
        if (_bridgeList.Count == 0 || NoBridgeTraffic)
        {
            return;
        }
        foreach (var b in _bridgeList)
        {
            b.Bridge!.Near.Clear();
        }
        foreach (var u in _units)
        {
            if (u.Def.Layer != Layers.Ground || u.Def.IsStructure || !u.Moving || !u.CanAct || u.Inside != 0 || u.Garrison != 0 ||
                (u.Squad != 0 && u.SquadFollow))
            {
                continue;
            }
            foreach (var b in _bridgeList)
            {
                var s = b.Bridge!;
                if (s.Traffic && !s.Down && Math.Abs(s.Along(u.Pos)) <= s.HalfLength + TrafficZone && Math.Abs(s.Side(u.Pos)) <= s.HalfWidth + TrafficZone)
                {
                    s.Near.Add(u);
                }
            }
        }
        foreach (var b in _bridgeList)
        {
            ServeSpan(r, b.Bridge!);
        }
    }

    // OnSpan reports whether a unit stands on a standing span or close enough to its end to shut it.
    private bool OnSpan(Unit u)
    {
        if (NoBridgeTraffic)
        {
            return false;
        }
        foreach (var b in _bridgeList)
        {
            var s = b.Bridge!;
            if (s.Traffic && !s.Down && Math.Abs(s.Along(u.Pos)) <= s.HalfLength + ShoveReach && Math.Abs(s.Side(u.Pos)) <= s.HalfWidth + Fixed.OneRaw)
            {
                return true;
            }
        }
        return false;
    }

    // ServeSpan settles who goes on this span this tick.
    private void ServeSpan(Router r, BridgeState s)
    {
        if (s.Near.Count == 0 || (s.Flow == 0 && OneBank(s)))
        {
            s.EndFlow();
            return;
        }
        var t = _traffic;
        t.Clear();
        int ups = 0, downs = 0;
        long wideUp = 0, wideDown = 0;
        foreach (var u in s.Near)
        {
            long a = s.Along(u.Pos), side = s.Side(u.Pos);
            var dir = CrossingOf(r, u, s, a, side);
            if (dir == 0)
            {
                continue;
            }
            var rad = u.Def.Archetype.Radius.Raw;
            if (dir > 0)
            {
                ups++;
                wideUp = Math.Max(wideUp, rad);
            }
            else
            {
                downs++;
                wideDown = Math.Max(wideDown, rad);
            }
            t.Add(new TrafficUnit(u, dir, a, side, Math.Max(0, Math.Max(Math.Abs(a) - s.HalfLength, Math.Abs(side) - s.HalfWidth))));
        }
        if (t.Count == 0 || (s.Flow == 0 && (ups == 0 || downs == 0)))
        {
            s.EndFlow();
            return;
        }
        t.Sort(NearestFirst);
        if (s.Flow == 0)
        {
            s.Flow = (sbyte)t[0].Dir;
            s.TurnSince = Tick;
        }
        var over = NextTurn(s, ups, downs, 2 * (wideUp + wideDown) + PassGap > s.Width);
        long queueUp = HoldGap, queueDown = HoldGap;
        foreach (var x in t)
        {
            var rad = x.U.Def.Archetype.Radius.Raw;
            var against = x.Dir > 0 ? wideDown : wideUp;
            var tight = against > 0 && 2 * (rad + against) + PassGap > s.Width;
            var onSpan = x.Dist <= rad;
            if (tight && !onSpan && (x.Dir != s.Flow || (over && x.Dist > CommitGap)))
            {
                ref var queue = ref x.Dir > 0 ? ref queueUp : ref queueDown;
                Hold(r, s, x, rad, ref queue);
            }
            else if (x.U.Def.Archetype.Locomotor != Locomotor.Foot && !x.U.Detouring && x.Dist <= LaneRange)
            {
                KeepLane(r, s, x, rad);
            }
        }
    }

    // OneBank reports whether every unit near the span is on one side of its middle: nobody to meet.
    private static bool OneBank(BridgeState s)
    {
        var first = s.Along(s.Near[0].Pos) < 0;
        foreach (var u in s.Near)
        {
            if (s.Along(u.Pos) < 0 != first)
            {
                return false;
            }
        }
        return true;
    }

    // NextTurn passes the turn when it is time (see the top of the file) and reports whether the side that has it must stop
    // letting more on.
    private bool NextTurn(BridgeState s, int ups, int downs, bool tight)
    {
        var theirs = s.Flow > 0 ? ups : downs;
        var others = s.Flow > 0 ? downs : ups;
        if (theirs == 0)
        {
            s.PassTurn(Tick);
            return false;
        }
        if (others == 0 || !tight)
        {
            return false;
        }
        var held = Tick - s.TurnSince;
        var turn = (uint)Content.UnitAi.BridgeTurnTicks;
        long nearest = long.MaxValue, ready = long.MaxValue;
        var committed = false;
        foreach (var x in _traffic)
        {
            if (x.Dir == s.Flow)
            {
                nearest = Math.Min(nearest, x.Dist);
                committed |= x.Dist <= CommitGap;
            }
            else
            {
                ready = Math.Min(ready, x.Dist);
            }
        }
        if ((held >= turn && !committed) || held >= 2 * turn || (nearest > FlowGap && ready <= ReadyGap))
        {
            s.PassTurn(Tick);
            return false;
        }
        return held >= turn;
    }

    // CrossingOf says which way u's way crosses the span's middle line (+1 along the road, -1 back), 0 if it doesn't; one on
    // the span past the middle or stopping on it, the way it is going.
    private int CrossingOf(Router r, Unit u, BridgeState s, long a, long side)
    {
        long a0 = a, s0 = side;
        var first = 0;
        var end = Math.Min(u.Path.Count, u.PathIdx + TrafficWays);
        for (var i = u.PathIdx; i < end; i++)
        {
            var p = u.Path[i].Center();
            long a1 = s.Along(p), s1 = s.Side(p);
            var dir = CrossesMiddle(s, a0, s0, a1, s1);
            if (dir != 0)
            {
                return dir;
            }
            if (first == 0 && Math.Abs(a1 - a) >= Fixed.OneRaw)
            {
                first = Math.Sign(a1 - a);
            }
            (a0, s0) = (a1, s1);
        }
        var onSpan = Math.Abs(a) <= s.HalfLength + u.Def.Archetype.Radius.Raw && Math.Abs(side) <= s.HalfWidth + Fixed.OneRaw;
        if (onSpan)
        {
            return first;
        }
        // Its path ends short of the deck: what comes after it.
        var from = u.Path[^1];
        if (u.Mode == RouteMode.Direct)
        {
            var legs = Math.Min(u.Route.Length, u.RouteIdx + TrafficLegs);
            for (var i = u.RouteIdx; i < legs; i++)
            {
                var p = r.CellAt(u.Route[i]).Center();
                long a1 = s.Along(p), s1 = s.Side(p);
                var dir = CrossesMiddle(s, a0, s0, a1, s1);
                if (dir != 0)
                {
                    return dir;
                }
                (a0, s0) = (a1, s1);
            }
            return 0;
        }
        return u.Mode == RouteMode.Field ? FieldCrossing(r, u, s, r.Index(from), a0, s0) : 0;
    }

    // FieldCrossing follows u's group flow field on from cell c, not writing anything down, as far as a path's stretch reads it.
    private int FieldCrossing(Router r, Unit u, BridgeState s, int c, long a0, long s0)
    {
        var k = r.ClassOf(u.Def);
        if (FindGroup(u.Group)?.FlowFor(k) is not { } f || r.IsStale(f))
        {
            return 0;
        }
        var hpa = r.Graph(f.K);
        for (var i = 0; i < TrafficField; i++)
        {
            if (r.Field(f, hpa.SectorOf(c)) is not { } field)
            {
                return 0;
            }
            int cx = c % r.Nav.W, cy = c / r.Nav.W;
            var dir = field.Dir[(cy - field.Y0) * field.Bw + (cx - field.X0)];
            if (dir == FlowField.None)
            {
                return 0;
            }
            c = (cy + LocalSearch.Dy[dir]) * r.Nav.W + cx + LocalSearch.Dx[dir];
            var p = r.CellAt(c).Center();
            long a1 = s.Along(p), s1 = s.Side(p);
            var way = CrossesMiddle(s, a0, s0, a1, s1);
            if (way != 0)
            {
                return way;
            }
            (a0, s0) = (a1, s1);
        }
        return 0;
    }

    // CrossesMiddle: the way the step from (a0, s0) to (a1, s1) crosses the span's middle line on the deck, or 0.
    private static int CrossesMiddle(BridgeState s, long a0, long s0, long a1, long s1)
    {
        if ((a0 < 0 && a1 < 0) || (a0 > 0 && a1 > 0) || a0 == a1)
        {
            return 0;
        }
        var at = s0 + (s1 - s0) * -a0 / (a1 - a0);
        return Math.Abs(at) <= s.HalfWidth + Fixed.OneRaw ? Math.Sign(a1 - a0) : 0;
    }

    // Hold keeps a unit off the span for now: it goes to its place in its side's queue on the bank, in single file to its right
    // (its left if there's no ground there), or stops on its way there, or where it is if it is past its place already.
    private void Hold(Router r, BridgeState s, in TrafficUnit x, long rad, ref long queue)
    {
        var u = x.U;
        var place = queue + rad;
        queue = place + rad + SlotGap;
        u.StallTicks = 0; // it isn't stuck, it is waiting
        var k = r.ClassOf(u.Def);
        if (place <= QueueReach && k >= 0 && QueueSlot(r, s, k, x.Dir, place, rad) is { } slot)
        {
            var off = (slot - u.Pos).Len().Raw;
            if (x.Dist < place - Fixed.OneRaw || off <= Math.Max(rad / 2, SlotNear))
            {
                Stop(u);
                return;
            }
            if (off <= SlotReach && r.Sight(k, u.Pos, slot))
            {
                Aim(u, slot);
                return;
            }
        }
        if (x.Dist <= place)
        {
            Stop(u);
        }
    }

    // QueueSlot is the point of a queue place this far from the span: to the right of the way into it, else to the left, if a unit of class k can stand there.
    private static Vec? QueueSlot(Router r, BridgeState s, int k, int dir, long place, long rad)
    {
        var aside = s.HalfWidth + rad + SlotAside;
        var along = -dir * (s.HalfLength + place);
        var right = s.At(along, s.Right(dir) * aside);
        if (Stands(r, k, right))
        {
            return right;
        }
        var left = s.At(along, -s.Right(dir) * aside);
        return Stands(r, k, left) ? left : null;
    }

    private static bool Stands(Router r, int k, Vec p)
    {
        var c = Cell.Of(p);
        return p.X.Raw >= 0 && p.Y.Raw >= 0 && r.Nav.Contains(c.X, c.Y) && r.Passable(k, r.Index(c));
    }

    // KeepLane sends a vehicle at its lane's point on the near bank, then at the one on the far bank.
    private void KeepLane(Router r, BridgeState s, in TrafficUnit x, long rad)
    {
        var k = r.ClassOf(x.U.Def);
        var lane = s.Right(x.Dir) * Math.Max(0, Math.Min(s.Width / 4, s.HalfWidth - rad - LaneMargin));
        var mark = s.HalfLength + LaneMark;
        var aim = s.At(-mark * x.Dir, lane);
        if (x.Along * x.Dir >= -mark || (aim - x.U.Pos).Len().Raw <= LanePass)
        {
            aim = s.At(mark * x.Dir, lane);
        }
        if (k >= 0 && r.Sight(k, x.U.Pos, aim))
        {
            Aim(x.U, aim);
        }
        // In its lane it passes its path's waypoints wide: those it is abreast of are behind it, or it would swing back to them coming off.
        var u = x.U;
        while (u.PathIdx < u.Path.Count - 1 && (s.Along(u.Path[u.PathIdx].Center()) - x.Along) * x.Dir < LaneAhead)
        {
            u.PathIdx++;
        }
    }

    private void Aim(Unit u, Vec goal)
    {
        var d = goal - u.Pos;
        u.PreciseTick = Tick;
        u.Intent = u.Intent with { Goal = goal, Desired = d.LenSq() == 0 ? d : d.WithLen(u.Def.Speed), Arrived = false };
    }

    private static void Stop(Unit u) => u.Intent = u.Intent with { Desired = default };
}
