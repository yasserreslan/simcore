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
    private const int Lookahead = 8;          // flow-field cells written ahead into a path
    private const int RefineAhead = 3;        // route legs refined at a time
    private const int RefineWhen = 8;         // ... when fewer path cells than this are left
    private const int ApproachRange = 12;     // cells from its slot where a member leaves the field
    private const int StartSearch = 4;        // rings searched for a passable start cell
    private const int DestSearch = 16;        // rings searched for a passable destination cell
    private const int FlowGoalSearch = 64;    // ... for a group's goal
    private const int FieldAim = 3;           // cells ahead a unit aims down a flow field
    private const int FieldDrift = 4;         // cells off its next field waypoint before it re-traces
    private const int DoneRange = 3;          // stuck this close to its destination: done

    private Router? _router;
    private readonly List<RouteGroup> _groups = [];       // sorted by ID
    private readonly List<PathRequest> _requests = [];    // sorted by (Tick, Unit, Group)
    private readonly List<int> _cells = [];               // scratch
    private readonly List<int> _route = [];               // scratch
    private uint _nextGroup;

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
    /// HPA* sector), so the first orders don't pay for them. Optional: they are built on first
    /// use otherwise, with the same results.
    /// </summary>
    public void PrepareRouting()
    {
        SyncRouting();
        Routing.BuildAll();
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
                var fg = r.NearestPassable(classes[i], goal, FlowGoalSearch);
                g.Flows.Add(new Flow(classes[i], fg));
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
            Steer(r, u);
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
        PathRequests = served;
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
                if (m.Mode == RouteMode.Field && r.ClassOf(m.Def) == f.K)
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
        var to = _route.Count > 0 ? _route[^1] : path.Count > 0 ? path[^1] : s;
        if (to == f.Goal)
        {
            return;
        }
        f.Goal = to;
        var members = new List<Unit>();
        foreach (var id in g.Members)
        {
            if (Unit(id) is { } m && r.ClassOf(m.Def) == k)
            {
                members.Add(m);
            }
        }
        var classes = new int[members.Count];
        Array.Fill(classes, k);
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
        var here = Cell.Of(u.Pos);
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
        r.Smooth(k, s, _cells, 0);
        if (s != r.Index(here) && (_cells.Count == 0 || _cells[0] != s))
        {
            _cells.Insert(0, s); // first back onto ground it fits
        }
        u.SetPath(ToCells(r, _cells, 0));
        if (u.Intent.Stuck)
        {
            u.Intent = u.Intent with { Stuck = false };
        }
    }

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
        if (u.Mode == RouteMode.Field && u.Moving &&
            (u.Intent.Stuck || !Within(u.Pos, u.Path[u.PathIdx].Center(), Fixed.FromInt(FieldDrift))))
        {
            // Stuck or pushed off its stretch of the field: a fresh stretch from where it stands.
            u.SetPath([]);
            u.Intent = u.Intent with { Stuck = false };
        }
        else if (u.Intent.Stuck && !u.PathPending && u.Mode == RouteMode.Direct)
        {
            RequestRoute(u, u.Dest); // stuck: its own route, from where it is
        }
        Advance(r, u);
        var left = u.Path.Count - u.PathIdx;
        if (u.Mode == RouteMode.Field && left <= FieldAim + 1)
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
                r.Smooth(k, from, _cells, 0);
                u.RouteIdx = idx;
                Extend(r, u, _cells);
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

    // Advance moves u's path past the waypoints it reached: the last one once the movement
    // system reports it arrived there; a pass-through one then too, or within a cell (a few cells
    // down a flow field, room for local avoidance to sidestep), or once the next is no farther
    // (pushed past it in a crowd), but only when the straight way to the next one is clear for
    // its class (it never aims across a corner).
    private static void Advance(Router r, Unit u)
    {
        var k = r.ClassOf(u.Def);
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
            var near = arrived || Within(u.Pos, c, reach) || (next - u.Pos).LenSq() <= (c - u.Pos).LenSq();
            if (near && (arrived || k < 0 || r.Clear(k, u.Pos, next)))
            {
                u.PathIdx++;
                continue;
            }
            break;
        }
    }

    // Extend appends cells to what is left of u's path.
    private static void Extend(Router r, Unit u, List<int> cells)
    {
        var left = u.Path.Count - u.PathIdx;
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
        var here = Cell.Of(u.Pos);
        var slot = u.Dest;
        if (f.Goal < 0 || Cheb(here, slot) <= ApproachRange || Cheb(here, r.CellAt(f.Goal)) <= Cheb(slot, r.CellAt(f.Goal)) + 4)
        {
            RequestRoute(u, slot);
            return;
        }
        var c = u.Moving ? r.Index(u.Path[^1]) : r.Index(here);
        var hpa = r.Graph(k);
        _cells.Clear();
        if (!u.Moving && !r.Passable(k, c))
        {
            // Standing where its class doesn't fit (pressed against a wall): from the nearest cell that does.
            c = r.NearestPassable(k, here, StartSearch);
            if (c < 0)
            {
                RequestRoute(u, slot);
                return;
            }
            _cells.Add(c);
        }
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

    private static int Cheb(Cell a, Cell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
