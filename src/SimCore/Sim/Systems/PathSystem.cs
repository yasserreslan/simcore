using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The path system (routing): it decides where each ground unit wants to go and writes its
// MoveIntent; the movement system moves it. Route requests wait in one queue, served in
// (tick, unit ID) order up to a budget a tick. A unit ordered alone gets its own route: HPA*
// over the map's sectors, refined into cells a few legs at a time. A group order plans one
// abstract route per navigation class (a corridor of portal costs) and its members follow the
// corridor's flow fields toward the goal, then walk their own short path to their formation slot.
public sealed partial class World
{
    private const int RequestBudget = 64;     // route searches served a tick
    private const long WorkBudget = 80_000;   // cells expanded a tick before the queue waits a tick
    private const int Lookahead = 32;         // flow-field cells traced ahead into a path (and smoothed)
    private const int RefineAhead = 3;        // route legs refined at a time
    private const int RefineWhen = 8;         // ... when fewer path cells than this are left
    private const int ApproachRange = 12;     // cells from its slot where a member leaves the field
    private const int StartSearch = 4;        // rings searched for a passable start cell
    private const int DestSearch = 16;        // rings searched for a passable destination cell
    private const int FlowGoalSearch = 64;    // ... for a group's goal
    private const int FieldAim = 3;           // cells from its waypoint where a unit down a flow field may pass it
    private const int FieldLineReach = 128;   // cells a straight line down a flow field reaches toward the slot at a time
    private const int FieldLineCells = 40;    // ... of which a stretch writes this many into the path
    private const int FieldDrift = 4;         // cells off its next field waypoint before it re-traces
    private const int DoneRange = 3;          // stuck this close to its destination: done
    internal const long WarmWork = 2_000;     // cells of HPA* sector building a quiet tick (two or three of a sector's searches, ~0.1 ms)

    private Router? _router;
    private readonly List<RouteGroup> _groups = [];       // sorted by ID
    private readonly List<PathRequest> _requests = [];    // sorted by (Tick, Unit, Group)
    private readonly List<int> _cells = [];               // scratch
    private readonly List<int> _route = [];               // scratch
    private readonly List<int> _straight = [];            // scratch
    private uint _nextGroup;
    private bool[] _classInUse = [];                      // scratch: per nav class, a ground unit of it can move

    // Whether quiet ticks build routing's caches ahead (tests turn it off to compare).
    internal bool RoutingWarmup { get; set; } = true;

    // For tests: the live group moves, by ID.
    internal IReadOnlyList<RouteGroup> GroupsForTests => _groups;
    private (uint, int, uint) _routingSynced = (uint.MaxValue, 0, 0);

    internal Router Routing
    {
        get
        {
            if (_router is null)
            {
                _router = new Router(Grid, Ground, Match.Defs);
                _routingSynced = (uint.MaxValue, 0, 0);
            }
            return _router;
        }
    }

    /// <summary>
    /// Builds routing's caches for the whole map now (the per-class clearance grids and every
    /// HPA* sector), so the first orders don't pay for them. Optional: otherwise each tick with no
    /// route search builds a little of the sectors of the classes of ground units that can move (a few of a
    /// sector's searches, about 0.1 ms, until they are all built: on 256 x 256 up to ~25 s a class),
    /// and a search builds what it meets that isn't built yet; the results are the same either way.
    /// </summary>
    public void PrepareRouting() => PrepareRouting(everySector: true);

    /// <summary>
    /// Builds routing's caches up front as <see cref="PrepareRouting()"/> does; with <paramref name="everySector"/> false only
    /// the per-class clearance grids and connected regions (the first order of a class then pays for neither), leaving the
    /// HPA* sectors to be built where routes first reach them and on quiet ticks. The loading step for a big map: building
    /// every sector of every class there takes minutes (docs/design.md, "Map size"). The results are the same either way.
    /// </summary>
    public void PrepareRouting(bool everySector)
    {
        SyncRouting();
        Routing.BuildAll(everySector);
    }

    // SyncRouting brings routing's map up to date: structures built or gone, grid and terrain changes.
    private Router SyncRouting()
    {
        var r = Routing;
        // Structures appear or go only with units spawned or removed: skip the walk otherwise.
        var mark = (Tick, _units.Count, _nextId);
        if (mark != _routingSynced)
        {
            r.SyncStructures(_units);
            _routingSynced = mark;
        }
        r.DrainChanges();
        return r;
    }

    /// <summary>The goal cell of a group move, or null when the group is gone.</summary>
    public Cell? GroupGoal(uint group) => FindGroup(group)?.Goal;

    private RouteGroup? FindGroup(uint id)
    {
        int lo = 0, hi = _groups.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_groups[mid].Id < id)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < _groups.Count && _groups[lo].Id == id ? _groups[lo] : null;
    }

    private void Enqueue(PathRequest q)
    {
        var i = _requests.BinarySearch(q);
        _requests.Insert(i < 0 ? ~i : i, q);
    }

    // RequestRoute asks for a route of u's own to dest; it keeps its current path meanwhile.
    private void RequestRoute(Unit u, Cell dest)
    {
        u.Dest = dest;
        u.Mode = RouteMode.Direct;
        if (!u.PathPending)
        {
            u.PathPending = true;
            Enqueue(new PathRequest(Tick, u.Id, 0));
        }
    }

    // MoveGround gives ground units a move order to cell c: one unit its own route, several a
    // group move with formation slots around c.
    private void MoveGround(List<Unit> units, Cell c)
    {
        var r = SyncRouting();
        units.Sort((a, b) => a.Id.CompareTo(b.Id));
        for (var i = units.Count - 1; i > 0; i--)
        {
            if (units[i] == units[i - 1])
            {
                units.RemoveAt(i); // listed twice
            }
        }
        var classes = new int[units.Count];
        for (var i = 0; i < units.Count; i++)
        {
            classes[i] = r.ClassOf(units[i].Def);
        }
        if (units.Count == 1)
        {
            var u = units[0];
            if (!Grid.TrySpiral(c, 0, out var cell))
            {
                return;
            }
            u.ClearPath();
            u.Order = new Order(OrderKind.Move, cell, 0, 0);
            RequestRoute(u, cell);
            return;
        }
        var goal = new Cell(Math.Clamp(c.X, 0, Grid.Width - 1), Math.Clamp(c.Y, 0, Grid.Height - 1));
        var slots = new int[units.Count];
        var offsets = new Vec[units.Count];
        Formation.Assign(r, units, classes, goal, slots, offsets);
        var g = new RouteGroup(++_nextGroup, goal);
        for (var i = 0; i < units.Count; i++)
        {
            var u = units[i];
            var slot = r.CellAt(slots[i]);
            u.ClearPath();
            u.Order = new Order(OrderKind.Move, slot, 0, 0);
            u.Group = g.Id;
            u.Dest = slot;
            u.Mode = RouteMode.Field;
            u.SlotOffset = offsets[i];
            g.Members.Add(u.Id);
            if (g.FlowFor(classes[i]) is null)
            {
                var fk = r.FlowClass(classes[i]);
                var fg = r.NearestPassable(fk, goal, FlowGoalSearch);
                g.Flows.Add(new Flow(fk, fg) { UnitClass = classes[i] });
            }
        }
        _groups.Add(g); // IDs only grow: the list stays sorted
        g.Queued = true;
        Enqueue(new PathRequest(Tick, g.Members[0], g.Id));
    }

    private void RunPaths()
    {
        var r = SyncRouting();
        KeepGroups(r);
        ServeRequests(r);
        _detoursLeft = Content.UnitAi.DetourBudget;
        if (_classInUse.Length != r.Nav.Classes)
        {
            _classInUse = new bool[r.Nav.Classes];
        }
        Array.Clear(_classInUse);
        foreach (var u in _units)
        {
            if (u.Def.Layer != Layers.Ground || u.Def.Speed.Raw == 0)
            {
                continue;
            }
            if (!u.CanAct)
            {
                u.Intent = u.Intent with { Desired = default };
                continue;
            }
            if (r.ClassOf(u.Def) is var k and >= 0)
            {
                _classInUse[k] = true; // only units that can move now: not a driver in his seat
                _classInUse[r.FlowClassOf(u.Def)] = true;
            }
            if (u.Squad != 0 && u.SquadFollow)
            {
                SteerSquad(r, u); // to his squad slot (SquadSystem.cs)
                continue;
            }
            Steer(r, u);
        }
        if (RoutingWarmup && PathRequests == 0)
        {
            // A quiet tick: build some of the sectors a later order would otherwise build all at
            // once (a first group order across an unbuilt map took tens of milliseconds).
            r.Warm(_classInUse, WarmWork);
        }
    }

    // KeepGroups drops members that left (dead, or given another order) and empty groups, and
    // queues a re-plan for a group whose flows went stale (the map changed).
    private void KeepGroups(Router r)
    {
        for (var gi = 0; gi < _groups.Count; gi++)
        {
            var g = _groups[gi];
            g.Members.RemoveAll(id => Unit(id) is not { } m || m.Group != g.Id);
            if (g.Members.Count == 0)
            {
                _groups.RemoveAt(gi--);
                continue;
            }
            if (g.Queued)
            {
                continue;
            }
            foreach (var f in g.Flows)
            {
                if (r.IsStale(f))
                {
                    g.Queued = true;
                    Enqueue(new PathRequest(Tick, g.Members[0], g.Id));
                    break;
                }
            }
        }
    }

    // ServeRequests serves queued requests in order, up to the budget.
    private void ServeRequests(Router r)
    {
        r.ResetWork();
        var served = 0;
        var i = 0;
        while (i < _requests.Count && _requests[i].Tick <= Tick && served < RequestBudget && (served == 0 || r.Work < WorkBudget))
        {
            var q = _requests[i++];
            if (q.Group != 0)
            {
                if (FindGroup(q.Group) is { } g)
                {
                    g.Queued = false;
                    PlanGroup(r, g);
                    served++;
                }
                continue;
            }
            if (Unit(q.Unit) is { PathPending: true } u)
            {
                ServeRoute(r, u);
                served++;
            }
        }
        _requests.RemoveRange(0, i);
        PathRequests = served + _squadPlans; // the squads' paths count too (SquadSystem.cs)
    }

    // PlanGroup plans each of the group's flows from where its members stand.
    private void PlanGroup(Router r, RouteGroup g)
    {
        var planned = 0;
        foreach (var f in g.Flows)
        {
            if (!r.IsStale(f))
            {
                continue;
            }
            if (planned > 0 && r.Work >= WorkBudget / 4)
            {
                // The other classes' flows next tick.
                g.Queued = true;
                Enqueue(new PathRequest(Tick + 1, g.Members[0], g.Id));
                return;
            }
            _cells.Clear();
            foreach (var id in g.Members)
            {
                var m = Unit(id)!;
                if (m.Mode == RouteMode.Field && r.ClassOf(m.Def) == f.UnitClass)
                {
                    _cells.Add(r.Index(Cell.Of(m.Pos)));
                }
            }
            planned++;
            if (f.Goal < 0)
            {
                f.Version = r.Version; // no passable goal: members fall back to their own routes
                f.Fields = [];
                continue;
            }
            r.Plan(f, _cells);
            if (f.Unreached && _cells.Count > 0)
            {
                Retarget(r, g, f);
            }
        }
    }

    // Retarget moves a flow's goal, which some members can't reach, to the nearest point the
    // first of them can, gives that class's members new slots around it, and plans again.
    private void Retarget(Router r, RouteGroup g, Flow f)
    {
        var k = f.K;
        var s = r.NearestPassable(k, r.CellAt(_cells[0]), StartSearch);
        if (s < 0)
        {
            return;
        }
        var path = new List<int>();
        r.FindRoute(k, s, f.Goal, path, _route);
        var to = _route.Count > 0 ? Router.Waypoint(_route[^1]) : path.Count > 0 ? path[^1] : s;
        if (to == f.Goal)
        {
            return;
        }
        f.Goal = to;
        var members = new List<Unit>();
        foreach (var id in g.Members)
        {
            if (Unit(id) is { } m && r.ClassOf(m.Def) == f.UnitClass)
            {
                members.Add(m);
            }
        }
        var classes = new int[members.Count];
        Array.Fill(classes, f.UnitClass);
        var slots = new int[members.Count];
        var offsets = new Vec[members.Count];
        Formation.Assign(r, members, classes, r.CellAt(to), slots, offsets);
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            m.Dest = r.CellAt(slots[i]);
            m.SlotOffset = offsets[i];
            m.Order = m.Order with { Cell = m.Dest };
        }
        r.Plan(f, _cells);
    }

    // ServeRoute plans u's own route to its Dest.
    private void ServeRoute(Router r, Unit u)
    {
        u.PathPending = false;
        var k = r.ClassOf(u.Def);
        if (k < 0)
        {
            u.SetPath([]);
            return;
        }
        // Driving out of its factory: from the door's exit (it gets there through the footprint).
        var here = u.OutOf != 0 ? u.OutVia : Cell.Of(u.Pos);
        var s = r.NearestPassable(k, here, StartSearch);
        var d = r.NearestPassable(k, u.Dest, DestSearch);
        if (d < 0)
        {
            // Deep inside a large blocked area: toward its raw cell, which routing turns into the
            // nearest point the unit can reach.
            d = r.Index(new Cell(Math.Clamp(u.Dest.X, 0, Grid.Width - 1), Math.Clamp(u.Dest.Y, 0, Grid.Height - 1)));
        }
        if (s < 0)
        {
            u.Route = [];
            u.RouteIdx = 0;
            u.SetPath([]);
            return;
        }
        r.FindRoute(k, s, d, _cells, _route);
        u.Route = [.. _route];
        u.RouteIdx = 0;
        if (_route.Count > 0)
        {
            var idx = 0;
            r.Refine(k, s, u.Route, ref idx, RefineAhead, _cells);
            u.RouteIdx = idx;
        }
        SmoothRoute(r, k, s, _cells);
        if ((s != r.Index(here) || u.OutOf != 0) && (_cells.Count == 0 || _cells[0] != s))
        {
            _cells.Insert(0, s); // first back onto ground it fits (or out through its door)
        }
        u.SetPath(ToCells(r, _cells, 0));
        if (u.Intent.Stuck)
        {
            u.Intent = u.Intent with { Stuck = false };
        }
    }

    /// <summary>
    /// For tools and tests: the cells a lone unit of <paramref name="def"/> would be routed along from one cell to
    /// another, planned as ServeRoute and Steer plan them (the first legs, then
    /// <see cref="RefineAhead"/> legs at a time, each chunk smoothed from the end of the last), all at once and without a
    /// unit. Empty when the class can't be routed. Reads routing's caches only: not game state.
    /// </summary>
    internal List<Cell> RouteCellsFor(UnitDef def, Cell from, Cell to)
    {
        var r = SyncRouting();
        var k = r.ClassOf(def);
        var result = new List<Cell>();
        var s = k < 0 ? -1 : r.NearestPassable(k, from, StartSearch);
        var d = k < 0 ? -1 : r.NearestPassable(k, to, DestSearch);
        if (s < 0 || d < 0)
        {
            return result;
        }
        var cells = new List<int>();
        var route = new List<int>();
        r.FindRoute(k, s, d, cells, route);
        var idx = 0;
        if (route.Count > 0)
        {
            r.Refine(k, s, route, ref idx, RefineAhead, cells);
        }
        SmoothRoute(r, k, s, cells);
        var all = new List<int>(cells);
        while (idx < route.Count)
        {
            cells.Clear();
            var at = all.Count > 0 ? all[^1] : s;
            if (!r.Refine(k, at, route, ref idx, RefineAhead, cells))
            {
                break;
            }
            // As Steer does: the chunk comes when fewer than RefineWhen cells are left beyond the aim, and the lot is
            // straightened again from the aim (ExtendStraight).
            var aimAt = Math.Max(0, all.Count - (RefineWhen - 1));
            var aim = aimAt == 0 ? s : all[aimAt - 1];
            cells.InsertRange(0, all.GetRange(aimAt, all.Count - aimAt));
            all.RemoveRange(aimAt, all.Count - aimAt);
            SmoothRoute(r, k, aim, cells);
            all.AddRange(cells);
        }
        result.Add(r.CellAt(s));
        foreach (var c in all)
        {
            result.Add(r.CellAt(c));
        }
        return result;
    }

    // SmoothRoute straightens a stretch of planned cells (from cell from on) with the content's tolerance and slack.
    private void SmoothRoute(Router r, int k, int from, List<int> cells) =>
        r.Smooth(k, from, cells, 0, Content.UnitAi.SmoothTolerance, Content.UnitAi.SmoothSlack);

    private static Cell[] ToCells(Router r, List<int> cells, int from)
    {
        var a = new Cell[cells.Count - from];
        for (var i = from; i < cells.Count; i++)
        {
            a[i - from] = r.CellAt(cells[i]);
        }
        return a;
    }

    // Steer keeps u's path going (flow-field lookahead or route refinement), moves it past the
    // waypoints it reached, and writes its intent.
    private void Steer(Router r, Unit u)
    {
        if (u.Mode != RouteMode.None && u.Intent.Stuck && (u.Moving || u.Mode == RouteMode.Field) &&
            Within(u.Pos, u.Dest.Center(), Fixed.FromInt(DoneRange)))
        {
            // Stuck this close to its slot or destination (pressed against friends that arrived
            // first): it is done, so the tail of a big group doesn't wait behind the first ring.
            u.Route = [];
            u.RouteIdx = 0;
            u.PathPending = false;
            u.Mode = RouteMode.Direct; // nothing left: the order completes next tick
            u.SetPath([]);
            u.Intent = u.Intent with { Desired = default, Stuck = false };
            return;
        }
        if (u.Intent.Stuck && u.Moving && !u.PathPending && Tick >= u.DetourRetryAt && _detoursLeft > 0)
        {
            TryDetour(r, u);
        }
        var aim = AimReach(u);
        var drift = Fixed.FromRaw(Math.Max((long)FieldDrift * Fixed.OneRaw, aim.Raw + Fixed.OneRaw));
        if (!u.Detouring && u.Mode == RouteMode.Field && u.Moving &&
            (u.Intent.Stuck || (u.OutOf == 0 && !Within(u.Pos, u.Path[u.PathIdx].Center(), drift))))
        {
            // Stuck or pushed off its stretch of the field: a fresh stretch from where it stands.
            u.SetPath([]);
            u.Intent = u.Intent with { Stuck = false };
        }
        else if (u.Intent.Stuck && !u.PathPending && u.Mode == RouteMode.Direct)
        {
            RequestRoute(u, u.Dest); // stuck: its own route, from where it is
        }
        Advance(r, u, aim, drift);
        var left = u.Path.Count - u.PathIdx;
        if (!u.Detouring && u.Mode == RouteMode.Field && left <= FieldAim + 1)
        {
            FollowField(r, u);
        }
        else if (u.Mode == RouteMode.Direct && !u.PathPending && u.RouteIdx < u.Route.Length && left < RefineWhen)
        {
            var k = r.ClassOf(u.Def);
            _cells.Clear();
            var from = u.Moving ? r.Index(u.Path[^1]) : r.Index(Cell.Of(u.Pos));
            var idx = u.RouteIdx;
            if (r.Refine(k, from, u.Route, ref idx, RefineAhead, _cells))
            {
                u.RouteIdx = idx;
                ExtendStraight(r, u, k, from, _cells);
            }
            else
            {
                RequestRoute(u, u.Dest); // the map changed under the route
            }
        }
        if (!u.Moving)
        {
            u.Intent = u.Intent with { Desired = default };
            return;
        }
        var wp = u.Path[u.PathIdx].Center();
        var d = wp - u.Pos;
        var speed = u.Def.Speed;
        // Full base speed toward pass-through waypoints; capped by the distance only at the end.
        var step = Final(u) && d.Len().Raw <= speed.Raw ? d : d.LenSq() == 0 ? d : d.WithLen(speed);
        var slot = u.Group != 0 ? u.SlotOffset : default;
        // Arrived and Stuck belong to the movement system for the same Goal; a new Goal resets them.
        u.Intent = wp == u.Intent.Goal ? u.Intent with { Desired = step, Slot = slot } : new MoveIntent(wp, step, slot, false);
    }

    // Final reports whether u's next waypoint is the end of where it is going.
    private static bool Final(Unit u) =>
        u.PathIdx == u.Path.Count - 1 && u.Mode != RouteMode.Field && u.RouteIdx >= u.Route.Length && !u.PathPending;

    // AimReach is how far ahead u looks along its path for the cell it aims at (docs/design.md, "Straight routes"): its top
    // speed for the content's aim ticks plus its radius, within the content's least and most.
    internal Fixed AimReach(Unit u)
    {
        var ai = Content.UnitAi;
        var reach = u.Def.Speed.Raw * ai.AimTicks + u.Def.Archetype.Radius.Raw;
        return Fixed.FromRaw(Math.Clamp(reach, ai.AimMin.Raw, Math.Max(ai.AimMin.Raw, ai.AimMax.Raw)));
    }

    // Advance moves u's aim along its path: past the last waypoint once the movement system reports it arrived there; past
    // a pass-through one then too, or within a cell of it (a few cells down a flow field, room for local avoidance to
    // sidestep), or once the next is no farther (pushed past it in a crowd); and, once it is within half its look-ahead of
    // its aim, on while the next is within its look-ahead and the straight way there keeps to the path (Along): so it
    // heads for a point that far along its path, holding that heading for a while, not for the next cell's centre (whose
    // staircase made it weave). Only when the straight way to the next one is clear for its class (Router.Sight: it never
    // aims across a corner), and down a flow field never to a waypoint beyond drift.
    private static void Advance(Router r, Unit u, Fixed aim, Fixed drift)
    {
        var k = r.ClassOf(u.Def);
        // It holds its aim (a steady heading) until it is within half its look-ahead of it, then looks ahead again; never on a
        // detour round bodies standing in its way (Sight sees the ground, not them) nor driving out of its factory (straight
        // down its door's lane).
        var look = u.Moving && !u.Detouring && u.OutOf == 0 && Within(u.Pos, u.Path[u.PathIdx].Center(), Fixed.FromRaw(aim.Raw / 2));
        while (u.Moving)
        {
            var c = u.Path[u.PathIdx].Center();
            var arrived = u.Intent.Arrived && u.Intent.Goal == c;
            if (Final(u))
            {
                if (arrived)
                {
                    u.PathIdx++;
                }
                break;
            }
            var next = u.PathIdx + 1 < u.Path.Count ? u.Path[u.PathIdx + 1].Center() : c;
            var reach = u.Mode == RouteMode.Field ? Fixed.FromInt(FieldAim) : Fixed.One;
            var near = arrived || Within(u.Pos, c, reach) || (next - u.Pos).LenSq() <= (c - u.Pos).LenSq() ||
                (look && Within(u.Pos, next, aim) && Along(u, next, aim));
            // Down a flow field it never moves on to a waypoint beyond drift: Steer would take
            // that for being pushed off its stretch and trace a fresh one from where it stands, and
            // when that one starts with a step back, the unit turned back and forth on the spot.
            var inDrift = u.Detouring || u.Mode != RouteMode.Field || arrived || Within(u.Pos, next, drift);
            if (near && inDrift && (arrived || k < 0 || r.Sight(k, u.Pos, next)))
            {
                u.PathIdx++;
                continue;
            }
            break;
        }
    }

    // Along reports whether the straight way from u to point to keeps within a cell of the waypoints of its path it would
    // pass on the way (its aim and those before it still ahead of it, within reach): so the look-ahead takes a straight
    // stretch of path whole but rounds a bend no wider than the path itself does.
    private static bool Along(Unit u, Vec to, Fixed reach)
    {
        var d = to - u.Pos;
        var len2 = d.LenSq();
        var back = (int)(reach.Raw >> Fixed.Shift) + 2;
        for (var i = u.PathIdx; i >= 0 && i > u.PathIdx - back; i--)
        {
            var p = u.Path[i].Center() - u.Pos;
            var dot = p.X.Raw * d.X.Raw + p.Y.Raw * d.Y.Raw;
            if (dot <= 0)
            {
                break; // behind it: passed
            }
            var cross = p.X.Raw * d.Y.Raw - p.Y.Raw * d.X.Raw;
            if (cross * cross > Fixed.OneRaw * Fixed.OneRaw * len2)
            {
                return false;
            }
        }
        return true;
    }

    // ExtendStraight appends a refined chunk (which follows cell from) to what is left of u's path, smoothed: on the move and
    // not on a detour, the lot again from its aim, so the cell where the last chunk ended (a portal on a sector border, up to
    // half a portal spacing off the straight line) is no bend it must make (docs/design.md, "Straight routes").
    private void ExtendStraight(Router r, Unit u, int k, int from, List<int> chunk)
    {
        if (!u.Moving || u.Detouring)
        {
            SmoothRoute(r, k, from, chunk);
            Extend(r, u, chunk);
            return;
        }
        _straight.Clear();
        for (var i = u.PathIdx + 1; i < u.Path.Count; i++)
        {
            _straight.Add(r.Index(u.Path[i]));
        }
        _straight.AddRange(chunk);
        SmoothRoute(r, k, r.Index(u.Path[u.PathIdx]), _straight);
        var a = new Cell[1 + _straight.Count];
        a[0] = u.Path[u.PathIdx];
        for (var i = 0; i < _straight.Count; i++)
        {
            a[1 + i] = r.CellAt(_straight[i]);
        }
        u.SetPath(a);
    }

    // Extend appends cells to what is left of u's path.
    private static void Extend(Router r, Unit u, List<int> cells)
    {
        var left = u.Path.Count - u.PathIdx;
        var detourLeft = Math.Max(0, u.DetourEnd - u.PathIdx);
        var a = new Cell[left + cells.Count];
        for (var i = 0; i < left; i++)
        {
            a[i] = u.Path[u.PathIdx + i];
        }
        for (var i = 0; i < cells.Count; i++)
        {
            a[left + i] = r.CellAt(cells[i]);
        }
        u.SetPath(a);
        u.DetourEnd = detourLeft;
    }

    // FollowField writes the next stretch of u's group flow field into its path, or sends it on
    // its own route to its slot once near it (or when the field doesn't reach it).
    private void FollowField(Router r, Unit u)
    {
        var g = FindGroup(u.Group);
        var k = r.ClassOf(u.Def);
        var f = g?.FlowFor(k);
        if (g is null || f is null)
        {
            RequestRoute(u, u.Dest);
            return;
        }
        // A map change reached its corridor: until the re-plan, it follows the fields built already.
        var stale = r.IsStale(f);
        if (stale && f.Fields.Length == 0)
        {
            return;
        }
        var here = u.OutOf != 0 && !u.Moving ? u.OutVia : Cell.Of(u.Pos); // driving out: from its door's exit
        var slot = u.Dest;
        if (f.Goal < 0 || Cheb(here, slot) <= ApproachRange || Cheb(here, r.CellAt(f.Goal)) <= Cheb(slot, r.CellAt(f.Goal)) + 4)
        {
            RequestRoute(u, slot);
            return;
        }
        var c = u.Moving ? r.Index(u.Path[^1]) : r.Index(here);
        var hpa = r.Graph(f.K);
        _cells.Clear();
        if (!u.Moving && (u.OutOf != 0 || !r.Passable(k, c)))
        {
            // Standing where its class doesn't fit (pressed against a wall), or inside its factory:
            // from the nearest cell that fits (the door's exit).
            c = r.NearestPassable(k, here, StartSearch);
            if (c < 0)
            {
                RequestRoute(u, slot);
                return;
            }
            _cells.Add(c);
        }
        if (FieldLine(r, f, stale, c, slot))
        {
            Extend(r, u, _cells);
            return;
        }
        int traceFrom = c, start = _cells.Count;
        for (var i = 0; i < Lookahead; i++)
        {
            var sec = hpa.SectorOf(c);
            var field = stale ? (sec < f.Fields.Length ? f.Fields[sec] : null) : r.Field(f, sec);
            if (field is null)
            {
                break;
            }
            int cx = c % r.Nav.W, cy = c / r.Nav.W;
            var li = (cy - field.Y0) * field.Bw + (cx - field.X0);
            var dir = field.Dir[li];
            if (dir == FlowField.None)
            {
                break;
            }
            c = (cy + LocalSearch.Dy[dir]) * r.Nav.W + cx + LocalSearch.Dx[dir];
            _cells.Add(c);
        }
        // The field's eight directions as straight lines where the ground lets them be (the trace is the field's cheapest way,
        // so the smoothing's tolerance and slack hold against it).
        r.Smooth(f.K, traceFrom, _cells, start, Content.UnitAi.SmoothTolerance, Content.UnitAi.SmoothSlack);
        if (_cells.Count <= (u.Moving || r.Passable(k, r.Index(here)) ? 0 : 1))
        {
            if (!u.Moving && !stale)
            {
                RequestRoute(u, slot); // off the field: its own way
            }
            return;
        }
        Extend(r, u, _cells);
    }

    // FieldLine appends to _cells the start of the straight line of cells from cell c toward the slot (FieldLineCells of it),
    // when that way is clear for the flow's class (Router.Sight) and going straight is not dearer than the field (docs/design.md,
    // "Straight routes"): the straight line toward the flow's goal (or, farther than FieldLineReach, to the point that far along
    // it) costs no more than the field says the way there does (its cost to the goal from c, less that from the line's end), by
    // the content's smoothing tolerance and slack; and the line to the slot no more than that one plus the slot's open-ground
    // distance from the goal's line. So down a group's flow field a unit goes straight for its slot over open ground instead of
    // along the field's eight directions, and follows the field round what is in the way (a wood, a slope, a road it is faster on).
    private bool FieldLine(Router r, Flow f, bool stale, int c, Cell slot)
    {
        var k = f.K;
        var w = r.Nav.W;
        if (f.Goal < 0 || Cheb(r.CellAt(c), slot) <= 1)
        {
            return false;
        }
        var g = Toward(r, c, f.Goal);
        var s = Toward(r, c, r.Index(slot));
        if (g < 0 || s < 0 || !r.Passable(k, g) || !r.Passable(k, s))
        {
            return false;
        }
        var from = FieldDist(r, f, stale, c);
        var to = g == f.Goal ? 0 : FieldDist(r, f, stale, g);
        if (from == Router.Unreachable || to == Router.Unreachable || from <= to)
        {
            return false;
        }
        long budget = from - to;
        var ai = Content.UnitAi;
        var line = r.LineCost(k, c, g);
        if (line == Router.Unreachable || line > budget + Math.Min(budget * ai.SmoothTolerance / 100, ai.SmoothSlack) || !r.Sight(k, c, g))
        {
            return false;
        }
        if (s != g)
        {
            var side = r.LineCost(k, c, s);
            if (side == Router.Unreachable || side > line + LocalSearch.Octile(g % w, g / w, s % w, s / w) + ai.SmoothSlack || !r.Sight(k, c, s))
            {
                return false;
            }
        }
        r.Line(c, s, _cells, FieldLineCells);
        return true;
    }

    // Toward is the cell on the straight line from cell a to cell b at most FieldLineReach cells (Chebyshev) from a: b itself
    // when it is that near, or -1 off the map.
    private static int Toward(Router r, int a, int b)
    {
        var w = r.Nav.W;
        int ax = a % w, ay = a / w, bx = b % w, by = b / w;
        var cheb = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
        if (cheb <= FieldLineReach)
        {
            return b;
        }
        int tx = ax + RoundDiv((bx - ax) * FieldLineReach, cheb), ty = ay + RoundDiv((by - ay) * FieldLineReach, cheb);
        return r.Nav.Contains(tx, ty) ? ty * w + tx : -1;
    }

    // FieldDist is a flow's cost to its goal from a cell (its sector's field, built if the sector is in the flow's corridor
    // and the flow is current), or Router.Unreachable.
    private static int FieldDist(Router r, Flow f, bool stale, int cell)
    {
        var hpa = r.Graph(f.K);
        var sec = hpa.SectorOf(cell);
        if (sec >= f.Corridor.Length || !f.Corridor[sec])
        {
            return Router.Unreachable;
        }
        var field = stale ? (sec < f.Fields.Length ? f.Fields[sec] : null) : r.Field(f, sec);
        if (field is null)
        {
            return Router.Unreachable;
        }
        int x = cell % r.Nav.W, y = cell / r.Nav.W;
        return field.Dist[(y - field.Y0) * field.Bw + (x - field.X0)];
    }

    private static int Cheb(Cell a, Cell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
