using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// The mission layer and the flight controller (E3; the plan's air-combat model, sections 2.2, 2.5
// and 2.6). Three layers fly every aircraft: the mission (what it is doing: orbit, patrol, guard,
// search, roam, a strike on top, the trips home) picks a point to steer at; the flight controller
// (pure functions, no state of its own) turns that into a turn demand, with edge avoidance first;
// the kinematics of its kind (drones: velocity turned at most TurnPermille of the speed a tick)
// moves it. The airfield phases (holding, final, taxi, climb-out) keep their own steering.
//
// The owner's rules: an aircraft picks targets only on Guard (with a target filter: the "hunt");
// Patrol, Search and Roam only spot; a strike (an attack order) runs on top of any task and the task
// resumes after; bingo fuel takes it home to refuel and it resumes the task at the nearest leg; it
// never bumps into the map's edge.
//
// Edge avoidance: the controller predicts its position a second ahead; when that point is outside
// the map less the flight's edgeMargin, on a side it is flying toward, it turns at its full rate
// toward the map's centre (the side by the cross product of its velocity and the centre's
// direction; exactly zero: right). The loader keeps the margin at least twice its turn radius plus
// a second of travel, so the turn always fits. Orbit centres, waypoints, areas and lanes are kept
// inside the map less the margin and the orbit radius. A point it is homing to (its station) widens
// the allowed rectangle to include that point. OnMap's clamp stays as a last resort; tests assert
// it never fires (AirEdgeClamps).
public sealed partial class World
{
    // The controller predicts this far ahead (the margin rule's second of travel).
    private const int EdgeLookAheadTicks = TicksPerSecond;

    // A waypoint counts as reached within its turn radius plus this (raw: 2 cells).
    private const long CaptureSlackRaw = 2 * Fixed.OneRaw;

    // Guard looks for a target this often (staggered by ID): twice a second.
    private const int HuntScanTicks = TicksPerSecond / 2;

    // Search lanes lie 1.6 times its sight apart (the plan, 2.5).
    private const long LaneSpacingPermille = 1600;

    // Search and Roam without a second corner: a square this many sights across each way round the point.
    private const int DefaultAreaSights = 2;

    private TagSet _droneTags;
    private bool _droneTagsSet;

    /// <summary>
    /// Diagnostic, not game state: how many aircraft steps in free flight OnMap had to clamp to the
    /// map. Edge avoidance keeps it at 0 (tests assert it); it is the last resort only.
    /// </summary>
    internal int AirEdgeClamps { get; private set; }

    // ---- Commands ----

    // AirTaskCommand gives the listed aircraft of the player a mission (CommandKind.AirTask).
    private void AirTaskCommand(Command c)
    {
        if (c.Mission is not (AirTaskKind.Patrol or AirTaskKind.Guard or AirTaskKind.Search or AirTaskKind.Roam) ||
            !Grid.Contains(c.Cell) || (c.Cell2 is { } c2 && !Grid.Contains(c2)))
        {
            return;
        }
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || u!.Def.Archetype.Flight is null || !u.Alive)
            {
                continue;
            }
            var area = c.Cell2 is not null || c.Mission is AirTaskKind.Search or AirTaskKind.Roam;
            var filter = c.Filter != 0 ? (AirTargetKinds)c.Filter & AirTargetKinds.All : GuardDefault(u);
            if (!area && c.Queue && u.AirMission is { HasArea: false } old && old.Kind == c.Mission)
            {
                // Shift+click: one more waypoint on the route (up to 8).
                if (old.RouteList.Count < AirMission.MaxRoute)
                {
                    old.RouteList.Add(c.Cell);
                }
                if (c.Filter != 0)
                {
                    old.Filter = filter;
                }
                continue;
            }
            var m = new AirMission { Kind = c.Mission, Filter = filter, Loop = c.On && c.Mission == AirTaskKind.Search };
            if (area)
            {
                var (a, b) = c.Cell2 is { } far ? (c.Cell, far) : AreaRound(c.Cell, u);
                (m.HasArea, m.AreaMin, m.AreaMax) = (true, new Cell(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Cell(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
            }
            else
            {
                m.RouteList.Add(c.Cell);
            }
            u.AirMission = m;
            if (m.Kind == AirTaskKind.Roam)
            {
                m.RoamAim = RoamPick(u, u.Def.Archetype.Flight!, m);
            }
            if (u.AirOps is { } ops)
            {
                m.Away = true; // at its airfield: it takes the task once it flies free
                if (ops.Phase is >= FlightPhase.Rollout and <= FlightPhase.Parked)
                {
                    (ops.Launch, ops.Hold) = (true, false); // as a Move order: it leaves once parked
                }
            }
            else if (u.Homing)
            {
                m.Away = true; // on its way home: the task waits for its return
            }
            else
            {
                u.Idle(); // a new task drops a strike in progress
            }
        }
    }

    // GuardDefault is the target kinds u engages on Guard when the order names none.
    private static AirTargetKinds GuardDefault(Unit u) =>
        u.Def.Archetype.Mission?.DefaultGuard ?? AirTargetKinds.Vehicles | AirTargetKinds.Infantry | AirTargetKinds.Structures;

    // AreaRound is the default area round cell c: DefaultAreaSights of u's sight each way, on the grid.
    private (Cell, Cell) AreaRound(Cell c, Unit u)
    {
        var h = (int)Math.Max(1, u.Def.Vision.Raw * DefaultAreaSights / Fixed.OneRaw);
        return (new Cell(Math.Max(0, c.X - h), Math.Max(0, c.Y - h)), new Cell(Math.Min(Grid.Width - 1, c.X + h), Math.Min(Grid.Height - 1, c.Y + h)));
    }

    // StopMission: Stop ends an airborne aircraft's mission; it orbits where it is.
    private static void StopMission(Unit u)
    {
        if (u.AirMission is null || u.AirOps is not null || u.Homing)
        {
            return;
        }
        u.AirMission = null;
        u.OrbitAt = u.Pos;
    }

    // ---- The mission layer: where it steers ----

    // SteerAim is where free-flying aircraft u steers this tick: over its strike target (an attack
    // order), else at its mission's point, else round its orbit.
    private Vec SteerAim(Unit u, ScoutFlight f)
    {
        var r = f.OrbitRadius.Raw;
        if (u.Order.Kind == OrderKind.Attack && Unit(u.Order.Target) is { } t)
        {
            return OrbitPoint(u, Inner(f, t.Pos, r), r); // a strike drone circles over its target until it is in reach
        }
        if (u.AirMission is not { } m)
        {
            return OrbitPoint(u, Inner(f, u.OrbitAt, r), r);
        }
        if (m.Away)
        {
            Rejoin(u, f, m); // back from a trip home: the nearest leg
            m.Away = false;
        }
        return m.Kind switch
        {
            AirTaskKind.Search => SearchAim(u, f, m),
            AirTaskKind.Roam => RoamAim(u, f, m),
            _ => RouteAim(u, f, m),
        };
    }

    // Capture is how near a waypoint counts as reached: its turn radius plus 2 cells.
    private static long Capture(Unit u, ScoutFlight f) => ScoutFlight.TurnRadiusRaw(u.Def.Speed.Raw, f.TurnPermille) + CaptureSlackRaw;

    // RoutePoint is waypoint i of m's route (Patrol, Guard over a route), or for Guard over an area
    // the ends of a racetrack along the area's long axis; kept inside the edge margin.
    private Vec RoutePoint(Unit u, ScoutFlight f, AirMission m, int i)
    {
        var r = f.OrbitRadius.Raw;
        if (!m.HasArea)
        {
            return Inner(f, m.RouteList[i].Center(), r);
        }
        var (x0, y0, x1, y1) = AreaRect(f, m);
        var cap = Capture(u, f);
        if (x1 - x0 >= y1 - y0)
        {
            var y = (y0 + y1) / 2;
            return P(i == 0 ? Math.Min(x0 + cap, (x0 + x1) / 2) : Math.Max(x1 - cap, (x0 + x1) / 2), y);
        }
        var x = (x0 + x1) / 2;
        return P(x, i == 0 ? Math.Min(y0 + cap, (y0 + y1) / 2) : Math.Max(y1 - cap, (y0 + y1) / 2));
    }

    // RouteCount is how many waypoints RoutePoint has: the route's, or 2 for an area's racetrack.
    private static int RouteCount(AirMission m) => m.HasArea ? 2 : m.RouteList.Count;

    // RouteAim: one waypoint is an orbit round it; two a racetrack; more a closed loop. It flies at
    // its waypoint and takes the next once within Capture of it.
    private Vec RouteAim(Unit u, ScoutFlight f, AirMission m)
    {
        var n = RouteCount(m);
        if (n <= 1)
        {
            var r = f.OrbitRadius.Raw;
            return OrbitPoint(u, RoutePoint(u, f, m, 0), r);
        }
        m.Leg = Math.Clamp(m.Leg, 0, n - 1);
        var aim = RoutePoint(u, f, m, m.Leg);
        if (Within(u.Pos, aim, Fixed.FromRaw(Capture(u, f))))
        {
            m.Leg = (m.Leg + 1) % n;
            aim = RoutePoint(u, f, m, m.Leg);
        }
        return aim;
    }

    // AreaRect is m's area (raw, the far edges exclusive) kept inside the map less the edge margin and the orbit radius.
    private (long X0, long Y0, long X1, long Y1) AreaRect(ScoutFlight f, AirMission m)
    {
        var r = f.OrbitRadius.Raw;
        var a = Inner(f, P((long)m.AreaMin.X * Fixed.OneRaw, (long)m.AreaMin.Y * Fixed.OneRaw), r);
        var b = Inner(f, P((long)(m.AreaMax.X + 1) * Fixed.OneRaw, (long)(m.AreaMax.Y + 1) * Fixed.OneRaw), r);
        return (a.X.Raw, a.Y.Raw, b.X.Raw, b.Y.Raw);
    }

    // SearchLanes is m's sweep: the lane count, whether lanes run along x, and the lanes' ends.
    private (int N, bool AlongX, long Lo, long Hi, long CrossLo, long Cross) SearchLanes(Unit u, ScoutFlight f, AirMission m)
    {
        var (x0, y0, x1, y1) = AreaRect(f, m);
        var alongX = x1 - x0 >= y1 - y0;
        var (lo, hi, clo, chi) = alongX ? (x0, x1, y0, y1) : (y0, y1, x0, x1);
        var spacing = Math.Max(Fixed.OneRaw, u.Def.Vision.Raw * LaneSpacingPermille / 1000);
        var cross = chi - clo;
        var n = (int)Math.Max(1, (cross + spacing - 1) / spacing);
        return (n, alongX, lo, hi, clo, cross);
    }

    // LaneEnd is lane i's start (end 0) or far end (end 1): lanes alternate direction, start a
    // turn radius out beyond the area and lie cross * (2i + 1) / 2n across it.
    private Vec LaneEnd(Unit u, ScoutFlight f, (int N, bool AlongX, long Lo, long Hi, long CrossLo, long Cross) s, int i, int end)
    {
        var cap = Capture(u, f);
        var c = s.CrossLo + s.Cross * (2 * i + 1) / (2 * s.N);
        var first = i % 2 == 0 ? s.Lo - cap : s.Hi + cap;
        var last = i % 2 == 0 ? s.Hi + cap : s.Lo - cap;
        var a = end == 0 ? first : last;
        return Inner(f, s.AlongX ? P(a, c) : P(c, a), f.OrbitRadius.Raw);
    }

    // SearchAim flies the lanes in turn, steering at a carrot a turn radius ahead on the lane (so it
    // settles onto the line after each turn); a lane is done within Capture of its far end. The sweep
    // done, it orbits the area's centre (or starts over: Loop).
    private Vec SearchAim(Unit u, ScoutFlight f, AirMission m)
    {
        var s = SearchLanes(u, f, m);
        if (m.Done)
        {
            var (x0, y0, x1, y1) = AreaRect(f, m);
            return OrbitPoint(u, P((x0 + x1) / 2, (y0 + y1) / 2), f.OrbitRadius.Raw);
        }
        m.Lane = Math.Clamp(m.Lane, 0, s.N - 1);
        var a = LaneEnd(u, f, s, m.Lane, 0);
        var b = LaneEnd(u, f, s, m.Lane, 1);
        var cap = Capture(u, f);
        if (Within(u.Pos, b, Fixed.FromRaw(cap)))
        {
            m.Leg = 0;
            if (m.Lane + 1 < s.N)
            {
                m.Lane++;
            }
            else if (m.Loop)
            {
                m.Lane = 0;
            }
            else
            {
                m.Done = true;
                return SearchAim(u, f, m);
            }
            a = LaneEnd(u, f, s, m.Lane, 0);
            b = LaneEnd(u, f, s, m.Lane, 1);
        }
        // The carrot: its projection on the lane plus a turn radius, at most the far end.
        long lx = b.X.Raw - a.X.Raw, ly = b.Y.Raw - a.Y.Raw;
        var len = Len3(lx, ly, 0);
        if (len == 0)
        {
            return b;
        }
        var t = ((u.Pos.X.Raw - a.X.Raw) * lx + (u.Pos.Y.Raw - a.Y.Raw) * ly) / len;
        m.Leg = t * 2 >= len ? 1 : 0; // the lane's second half (for the progress)
        var along = Math.Clamp(t + cap - CaptureSlackRaw, 0, len);
        return P(a.X.Raw + lx * along / len, a.Y.Raw + ly * along / len);
    }

    /// <summary>A searching aircraft's progress, 0 to 100 (half lanes swept); 100 once the sweep is done.</summary>
    private int SearchPercent(Unit u, ScoutFlight f, AirMission m)
    {
        if (m.Done)
        {
            return 100;
        }
        var s = SearchLanes(u, f, m);
        return Math.Clamp((m.Lane * 2 + m.Leg) * 100 / (2 * s.N), 0, 99);
    }

    // RoamAim marks the sector it flies over and steers at its roam waypoint, taking the next once there.
    private Vec RoamAim(Unit u, ScoutFlight f, AirMission m)
    {
        if (Sector(f, m, u.Pos) is var here and >= 0)
        {
            m.Visited |= 1UL << here;
        }
        if (Within(u.Pos, m.RoamAim, Fixed.FromRaw(Capture(u, f))))
        {
            m.Leg++;
            m.RoamAim = RoamPick(u, f, m);
        }
        return m.RoamAim;
    }

    // Sector is the roam sector (y * 8 + x) of point p in m's area, or -1 outside it.
    private int Sector(ScoutFlight f, AirMission m, Vec p)
    {
        var (x0, y0, x1, y1) = AreaRect(f, m);
        if (p.X.Raw < x0 || p.Y.Raw < y0 || p.X.Raw >= x1 || p.Y.Raw >= y1)
        {
            return -1;
        }
        var sx = (int)((p.X.Raw - x0) * AirMission.Sectors / Math.Max(1, x1 - x0));
        var sy = (int)((p.Y.Raw - y0) * AirMission.Sectors / Math.Max(1, y1 - y0));
        return Math.Clamp(sy, 0, AirMission.Sectors - 1) * AirMission.Sectors + Math.Clamp(sx, 0, AirMission.Sectors - 1);
    }

    // RoamPick is roam leg m.Leg's waypoint: a seeded point (Rand.Hash of the seed, the unit and the
    // leg: no generator state) in a seeded one of the sectors it has not flown over since the memory
    // last filled (a full memory starts over); unpredictable for air defence, covering the area.
    private Vec RoamPick(Unit u, ScoutFlight f, AirMission m)
    {
        const int all = AirMission.Sectors * AirMission.Sectors;
        if (m.Visited == ulong.MaxValue)
        {
            m.Visited = 0;
        }
        var free = all - System.Numerics.BitOperations.PopCount(m.Visited);
        var seed = Match.Setup.Seed;
        var k = (int)(Rand.Hash(seed, u.Id, (ulong)m.Leg, 0x0A11) % (ulong)free);
        var sector = 0;
        for (var i = 0; i < all; i++)
        {
            if ((m.Visited & (1UL << i)) == 0 && k-- == 0)
            {
                sector = i;
                break;
            }
        }
        var (x0, y0, x1, y1) = AreaRect(f, m);
        long sw = Math.Max(1, (x1 - x0) / AirMission.Sectors), sh = Math.Max(1, (y1 - y0) / AirMission.Sectors);
        var px = x0 + sector % AirMission.Sectors * sw + (long)(Rand.Hash(seed, u.Id, (ulong)m.Leg, 0x0A12) % (ulong)sw);
        var py = y0 + sector / AirMission.Sectors * sh + (long)(Rand.Hash(seed, u.Id, (ulong)m.Leg, 0x0A13) % (ulong)sh);
        return Inner(f, P(px, py), f.OrbitRadius.Raw);
    }

    // Rejoin: back from a trip home, a route or racetrack resumes at its nearest waypoint, a search
    // at the start of the lane it was on, a roam with a fresh waypoint.
    private void Rejoin(Unit u, ScoutFlight f, AirMission m)
    {
        m.Hunt = 0;
        switch (m.Kind)
        {
            case AirTaskKind.Search:
                m.Leg = 0;
                break;
            case AirTaskKind.Roam:
                m.Leg++;
                m.RoamAim = RoamPick(u, f, m);
                break;
            default:
                var best = 0;
                var bestD = long.MaxValue;
                for (var i = 0; i < RouteCount(m); i++)
                {
                    var d = (RoutePoint(u, f, m, i) - u.Pos).LenSq();
                    if (d < bestD)
                    {
                        (best, bestD) = (i, d);
                    }
                }
                m.Leg = best;
                break;
        }
    }

    // ---- Guard: the hunt (the one automatic mode) ----

    // GuardHunt runs Guard's targeting for u (called each tick it flies free, before its strike
    // order): a target it engages that died, hid or left the area plus the leash is dropped; with
    // none it looks (twice a second) for the nearest spotted enemy of the filter's kinds its strike
    // weapon can hit, inside the area plus the leash (a route: within the leash of it), and attacks
    // it as an attack order would (the strike resumes the task when it ends). Out of ammunition it
    // keeps guarding and spotting: it never goes home for it.
    private void GuardHunt(Unit u, ScoutFlight f, AirMission m, WeaponDef? w)
    {
        if (m.Kind != AirTaskKind.Guard || w is null)
        {
            return;
        }
        if (u.Order.Kind == OrderKind.Attack)
        {
            if (m.Hunt != 0 && u.Order.Target == m.Hunt && (Unit(m.Hunt) is not { Alive: true } t || !InGuardZone(u, f, m, t.Pos)))
            {
                u.Idle(); // gone, or past the leash: back to guarding
                m.Hunt = 0;
            }
            return; // a strike (its own or ordered) runs
        }
        m.Hunt = 0;
        if (u.Homing || m.Away || !HasAmmo(u, w) || u.Reload > 0 || (Tick + u.Id) % HuntScanTicks != 0)
        {
            return;
        }
        Unit? best = null;
        long bestD = 0;
        foreach (var t in _units)
        {
            if (t.Owner == 0 || t.Owner == u.Owner || !t.Alive || t.Def.Archetype.Untargetable || (KindOf(t) & m.Filter) == 0 ||
                !StrikeTarget(u, w, t) || !InGuardZone(u, f, m, t.Pos))
            {
                continue;
            }
            var d = (t.Pos - u.Pos).LenSq();
            if (best is null || d < bestD)
            {
                (best, bestD) = (t, d);
            }
        }
        if (best is not null)
        {
            u.Order = new Order(OrderKind.Attack, Cell.Of(best.Pos), best.Id, 0);
            m.Hunt = best.Id;
        }
    }

    // KindOf is the Guard filter kind of unit t.
    private AirTargetKinds KindOf(Unit t)
    {
        var a = t.Def.Archetype;
        if (a.Locomotor == Locomotor.Flying)
        {
            if (!_droneTagsSet)
            {
                (_droneTags, _droneTagsSet) = (Content.TagsOf("drone"), true);
            }
            return !_droneTags.IsEmpty && t.Def.Tags.Matches(_droneTags) ? AirTargetKinds.Drones : AirTargetKinds.Aircraft;
        }
        return a.IsStructure || a.Locomotor == Locomotor.None ? AirTargetKinds.Structures
            : a.Locomotor == Locomotor.Foot ? AirTargetKinds.Infantry : AirTargetKinds.Vehicles;
    }

    // InGuardZone: p lies inside m's area plus u's leash, or within the leash of its route (one
    // waypoint: of its orbit).
    private bool InGuardZone(Unit u, ScoutFlight f, AirMission m, Vec p)
    {
        var leash = u.Def.Archetype.Mission?.HuntLeash.Raw ?? 0;
        if (m.HasArea)
        {
            long x0 = (long)m.AreaMin.X * Fixed.OneRaw - leash, y0 = (long)m.AreaMin.Y * Fixed.OneRaw - leash;
            long x1 = (long)(m.AreaMax.X + 1) * Fixed.OneRaw + leash, y1 = (long)(m.AreaMax.Y + 1) * Fixed.OneRaw + leash;
            return p.X.Raw >= x0 && p.X.Raw < x1 && p.Y.Raw >= y0 && p.Y.Raw < y1;
        }
        var n = m.RouteList.Count;
        if (n == 1)
        {
            return Within(p, m.RouteList[0].Center(), Fixed.FromRaw(leash + f.OrbitRadius.Raw));
        }
        for (var i = 0; i < n; i++)
        {
            if (n == 2 && i == 1)
            {
                break; // two waypoints: one leg
            }
            if (SegmentDistSq(p, m.RouteList[i].Center(), m.RouteList[(i + 1) % n].Center()) <= leash * leash)
            {
                return true;
            }
        }
        return false;
    }

    // SegmentDistSq is the squared distance (raw) from p to the segment a-b.
    private static long SegmentDistSq(Vec p, Vec a, Vec b)
    {
        long dx = b.X.Raw - a.X.Raw, dy = b.Y.Raw - a.Y.Raw, px = p.X.Raw - a.X.Raw, py = p.Y.Raw - a.Y.Raw;
        var l2 = dx * dx + dy * dy;
        if (l2 > 0)
        {
            var t = Math.Clamp((px * dx + py * dy) * 1024 / l2, 0, 1024);
            (px, py) = (px - dx * t / 1024, py - dy * t / 1024);
        }
        return px * px + py * py;
    }

    // ---- The flight controller ----

    private static Vec P(long x, long y) => new(Fixed.FromRaw(x), Fixed.FromRaw(y));

    // Inner keeps point p inside the map less f's edge margin and `shrink` (raw); an axis too short
    // for both takes the map's middle.
    private Vec Inner(ScoutFlight f, Vec p, long shrink)
    {
        static long Clamp(long v, long size, long m)
        {
            long lo = m, hi = size - m;
            return lo > hi ? size / 2 : Math.Clamp(v, lo, hi);
        }
        var m = f.EdgeMarginRaw + shrink;
        return P(Clamp(p.X.Raw, (long)Grid.Width * Fixed.OneRaw, m), Clamp(p.Y.Raw, (long)Grid.Height * Fixed.OneRaw, m));
    }

    // EdgeSide: +1 (clockwise on the map) or -1 when edge avoidance must turn u flying (vx, vy)
    // now; 0 when its position a second ahead stays inside the map less the margin (widened to
    // include aim; homing, a narrower band) on every side it flies toward.
    private int EdgeSide(Unit u, ScoutFlight f, Vec aim, long vx, long vy)
    {
        // The narrow band: one turn radius plus a second of travel (what a turn away from one edge
        // needs; the full margin covers the corner case of free flight). Homing (to a station in the
        // band along the edge, or holding at an airfield whose orbit reaches over it) it must fly
        // there and swing round, so the band narrows to it; an aim nearer the edge widens the
        // allowed rectangle to include it, but never past the narrow band.
        var speed = Len3(vx, vy, 0);
        var narrow = ScoutFlight.TurnRadiusRaw(speed, f.TurnPermille) + speed * EdgeLookAheadTicks;
        return EdgeSide(u, f, aim, vx, vy, narrow, vx * EdgeLookAheadTicks, vy * EdgeLookAheadTicks);
    }

    // EdgeSide with the narrow band and the predicted offset (ax, ay: where it will be, raw, from
    // its position) given: the fixed wing's (E4) use their turn radius at their speed, and a dash
    // adds the distance it needs to slow to cruise speed.
    private int EdgeSide(Unit u, ScoutFlight f, Vec aim, long vx, long vy, long narrow, long ax, long ay)
    {
        long w = (long)Grid.Width * Fixed.OneRaw, h = (long)Grid.Height * Fixed.OneRaw, mg = f.EdgeMarginRaw;
        if (u.Homing)
        {
            mg = Math.Min(mg, narrow);
        }
        long loX = Math.Min(mg, Math.Max(aim.X.Raw, narrow)), hiX = Math.Max(w - mg, Math.Min(aim.X.Raw, w - narrow));
        long loY = Math.Min(mg, Math.Max(aim.Y.Raw, narrow)), hiY = Math.Max(h - mg, Math.Min(aim.Y.Raw, h - narrow));
        long px = u.Pos.X.Raw + ax, py = u.Pos.Y.Raw + ay;
        var out_ = (vx < 0 && px < loX) || (vx > 0 && px > hiX) || (vy < 0 && py < loY) || (vy > 0 && py > hiY);
        if (!out_)
        {
            return 0;
        }
        long cx = w / 2 - u.Pos.X.Raw, cy = h / 2 - u.Pos.Y.Raw;
        return vx * cy - vy * cx >= 0 ? 1 : -1; // toward the centre; exactly zero: right
    }

    // HardTurn turns velocity (vx, vy) at the full drone rate (TurnPermille of the speed) to side
    // (+1 clockwise on the map), at speed.
    private static (long, long) HardTurn(long vx, long vy, int side, long speed, int turnPermille)
    {
        var vl = Len3(vx, vy, 0);
        if (vl == 0)
        {
            return (vx, vy);
        }
        var max = Math.Max(1, speed * turnPermille / 1000);
        (vx, vy) = (vx - side * vy * max / vl, vy + side * vx * max / vl);
        var l = Len3(vx, vy, 0);
        return l > 0 ? (vx * speed / l, vy * speed / l) : (vx, vy);
    }

    // DroneControl is the flight controller over the drone kinematics: velocity (vx, vy) at speed
    // after a tick steering u at aim. Edge avoidance first; an aim behind the beam (more than 90
    // degrees off) and beyond its turning circles (twice its turn radius) turns it at its full rate
    // toward the aim's side (exactly behind: right), which the drone law alone barely does (it turns
    // at cos(half the angle) of its rate: a drone at bingo flying away from its station flew on and
    // ran dry); otherwise the drone law as it always was (closer in, its lazy turn swings wide so it
    // comes back over the point, as a recovery over its station needs): the velocity moves toward
    // the aim's direction by at most TurnPermille of the speed.
    private (long, long) DroneControl(Unit u, ScoutFlight f, Vec aim, long vx, long vy, long speed)
    {
        if (EdgeSide(u, f, aim, vx, vy) is var edge and not 0)
        {
            return HardTurn(vx, vy, edge, speed, f.TurnPermille);
        }
        long wx = aim.X.Raw - u.Pos.X.Raw, wy = aim.Y.Raw - u.Pos.Y.Raw;
        var wl = Len3(wx, wy, 0);
        if (wl > 0)
        {
            if (wx * vx + wy * vy < 0 && wl > 2 * ScoutFlight.TurnRadiusRaw(speed, f.TurnPermille))
            {
                return HardTurn(vx, vy, vx * wy - vy * wx >= 0 ? 1 : -1, speed, f.TurnPermille);
            }
            long dx = wx * speed / wl - vx, dy = wy * speed / wl - vy;
            var max = Math.Max(1, speed * f.TurnPermille / 1000);
            var dl = Len3(dx, dy, 0);
            if (dl > max)
            {
                (dx, dy) = (dx * max / dl, dy * max / dl);
            }
            (vx, vy) = (vx + dx, vy + dy);
        }
        var vl = Len3(vx, vy, 0);
        return vl > 0 ? (vx * speed / vl, vy * speed / vl) : (vx, vy);
    }

    // FlyStep moves free-flying aircraft u by (vx, vy): its velocity is kept exact (never the
    // clamped step, so a clamp can never leave it stuck at the edge), its facing follows it, and a
    // clamp by OnMap is counted (AirEdgeClamps; edge avoidance keeps it at 0).
    private Vec FlyStep(Unit u, long vx, long vy)
    {
        long nx = u.Pos.X.Raw + vx, ny = u.Pos.Y.Raw + vy;
        var pos = OnMap(nx, ny);
        if (pos.X.Raw != nx || pos.Y.Raw != ny)
        {
            AirEdgeClamps++;
        }
        u.Vel = P(vx, vy);
        u.Pos = pos;
        if (Facing.TryFromDirection(u.Vel, out var facing))
        {
            u.Facing = facing;
            u.TurretFacing = facing;
        }
        return pos;
    }

    // ---- The status query ----

    /// <summary>
    /// The centre free-flying aircraft u actually orbits now (its orbit point kept inside the map
    /// less its edge margin and orbit radius: the mission layer's edge rule), for drawing its orbit.
    /// </summary>
    public Vec OrbitCentreOf(Unit u) =>
        u.Def.Archetype.Flight is { } f ? Inner(f, u.OrbitAt, f.OrbitRadius.Raw) : u.OrbitAt;

    /// <summary>
    /// What aircraft u is doing (the aircraft sidebar's and the mission UI's one query; read-only):
    /// its activity, task, flight phase, reason, progress, queue place, fuel and ammunition. For a
    /// ground control station holding its recovered drone for the relaunch: Docked, with the
    /// turnaround's progress and the mission the drone resumes. Anything else: <see cref="AirActivity.None"/>.
    /// </summary>
    public AirStatus AirStatusOf(Unit u)
    {
        if (u.Def.Archetype.Flight is not { } f)
        {
            if (!u.RelaunchPending || Content.Archetypes[u.RelaunchArchetype].Flight is not { } rf)
            {
                return default;
            }
            var turn = Math.Max(1, rf.TurnaroundTicks);
            return new AirStatus(AirActivity.Docked, u.KeptMission?.Kind ?? AirTaskKind.Orbit, FlightPhase.Parked, HomeReason.Refuel,
                Math.Clamp((turn - u.Cooldown) * 100 / turn, 0, 100), 0, 100, -1, false, 0);
        }
        var m = u.AirMission;
        var task = m?.Kind ?? AirTaskKind.Orbit;
        var fuel = u.Def.Lifetime > 0 ? Math.Clamp(u.Lifetime * 100 / u.Def.Lifetime, 0, 100) : 100;
        int ammo = 0, max = 0;
        for (var i = 0; i < u.Charges.Count; i++)
        {
            (ammo, max) = (ammo + u.Charges[i], max + u.Def.StockMax[i]);
        }
        var armed = u.Def.Weapons.Count > 0;
        var target = u.Order.Kind == OrderKind.Attack ? u.Order.Target : 0;
        var phase = FlightPhaseOf(u);
        var (act, pct, place) = (AirActivity.Orbiting, 0, 0);
        if (u.AirOps is { } ops)
        {
            switch (ops.Phase)
            {
                case FlightPhase.Holding:
                    act = AirActivity.Holding;
                    if (Airfield(ops.Airfield) is { } af)
                    {
                        place = af.LandingList.FindIndex(e => e.Aircraft == u.Id) + 1;
                    }
                    break;
                case FlightPhase.Final or FlightPhase.Rollout:
                    act = AirActivity.Landing;
                    break;
                case FlightPhase.TaxiIn or FlightPhase.Parking:
                    act = AirActivity.Taxiing;
                    break;
                case FlightPhase.Parked:
                    var rearm = u.HomeReason == HomeReason.Rearm;
                    (act, pct) = fuel < 100 ? (AirActivity.Refuelling, fuel)
                        : rearm && ammo < max ? (AirActivity.Rearming, max > 0 ? ammo * 100 / max : 100)
                        : rearm && u.HP < u.Def.MaxHp && u.Def.Archetype.Aircraft is { RepairHpPerSecond: > 0 } ? (AirActivity.Repairing, u.HP * 100 / Math.Max(1, u.Def.MaxHp))
                        : (ops.Hold ? AirActivity.Held : AirActivity.Ready, 100);
                    break;
                default:
                    act = AirActivity.Departing;
                    break;
            }
        }
        else if (u.Homing)
        {
            act = u.HomeReason switch { HomeReason.Rearm => AirActivity.RearmEnRoute, HomeReason.Recall => AirActivity.Recalled, _ => AirActivity.Returning };
        }
        else if (target != 0)
        {
            act = m is { Hunt: not 0 } && m.Hunt == target ? AirActivity.Engaging : AirActivity.Striking;
        }
        else if (m is not null)
        {
            (act, pct) = m.Kind switch
            {
                AirTaskKind.Patrol => (AirActivity.Patrolling, 0),
                AirTaskKind.Guard => (AirActivity.Guarding, 0),
                AirTaskKind.Search => (AirActivity.Searching, SearchPercent(u, f, m)),
                _ => (AirActivity.Roaming, 0),
            };
        }
        return new AirStatus(act, task, phase, u.HomeReason, pct, place, fuel, armed ? ammo : -1, armed && ammo == 0, target);
    }
}
