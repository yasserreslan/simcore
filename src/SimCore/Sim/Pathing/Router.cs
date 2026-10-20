using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// Routing's view of the map and its searches: the navigation grid (per class cost and
/// clearance), one HPA* graph per class, single-unit routes (abstract A* over the portals, then
/// A* inside one sector at a time to refine them), and the group flows. Everything here is a
/// cache derived from the map, built lazily or up front (World.PrepareRouting):
/// no result depends on what was built when. The game state it feeds (paths, routes, groups,
/// the request queue) lives on the units and the world, and is hashed there.
/// </summary>
internal sealed class Router
{
    internal const int Unreachable = LocalSearch.Unreachable;

    // Routes between cells this close (Chebyshev) try one windowed A* first.
    private const int NearRange = 40;
    private const int NearMargin = 16;

    // A group's route search weighs its heuristic by this (tenths): faster, and the corridor's
    // Dijkstra gives the exact costs inside the corridor.
    private const int RouteGreed = 15;

    // A single route's abstract search weighs its heuristic by this (tenths).
    private const int AbstractGreed = 12;

    // Routes between sectors further apart than this search a corridor CorridorWidth sectors
    // either side of a quick greedy route.
    private const int CorridorFrom = 8;
    private const int CorridorWidth = 3;
    private const int CorridorGreed = 12;

    // A single route's abstract search gives up after this many portals (an unreachable goal
    // would otherwise explore all the ground the unit can reach), keeping the nearest point.
    private const int MaxAbstractNodes = 40_000;

    private readonly Grid _grid;
    private readonly TerrainMap? _terrain;
    private readonly List<Hpa?> _hpa = [];
    private readonly int[] _defClass; // per UnitDef.Index: nav class, -1 not routed
    private readonly int[] _defFlowClass; // per UnitDef.Index: the class its group flows use

    // Abstract search buffers, shared by every class (all have the same sectors).
    private int[] _ag = [];
    private int[] _ap = [];
    private uint[] _aseen = [];
    private uint[] _aclosed = [];
    private uint _agen;
    private readonly MinHeap _aopen = new();

    // The structure footprints blocked (by unit ID), and the rectangles to refresh.
    private List<(uint Id, int X, int Y, int W, int H)> _structures = [];
    private List<(uint Id, int X, int Y, int W, int H)> _scratchStructures = [];
    private readonly List<(int X0, int Y0, int X1, int Y1)> _pendingRects = [];

    // Scratch marks for formation slots.
    private uint[] _mark = [];
    private uint _markGen;

    internal Router(Grid grid, TerrainMap? terrain, IReadOnlyList<UnitDef> defs)
    {
        _grid = grid;
        _terrain = terrain;
        Nav = new NavGrid(grid, terrain);
        grid.ChangeLog = [];
        if (terrain is not null)
        {
            terrain.ChangeLog = [];
        }
        _defClass = new int[defs.Count];
        foreach (var d in defs)
        {
            _defClass[d.Index] = ComputeClass(d);
        }
        _defFlowClass = new int[defs.Count];
        for (var i = 0; i < _defClass.Length; i++)
        {
            _defFlowClass[i] = FlowClass(_defClass[i]); // the crowd variants up front, so a first group order doesn't build them
        }
    }

    internal NavGrid Nav { get; }

    internal LocalSearch Search { get; } = new();

    /// <summary>Counts map changes seen; a group flow planned at another version is stale.</summary>
    internal uint Version { get; private set; }

    /// <summary>
    /// Deterministic work done by searches (cells and abstract nodes expanded), for the per-tick
    /// budget. Building the cached sectors is not counted: when that happens depends on the cache.
    /// </summary>
    internal long Work => Search.Expanded;

    internal void ResetWork() => Search.Expanded = 0;

    private int ComputeClass(UnitDef d)
    {
        if (d.Layer != Layers.Ground || d.Speed.Raw == 0 || d.IsStructure)
        {
            return -1;
        }
        var tc = 0;
        if (_terrain is not null)
        {
            tc = _terrain.Rules.ClassOf(d);
            if (tc < 0)
            {
                return -1;
            }
        }
        // Clearance: a unit of radius r needs ceil(2r) free cells side by side.
        var width = (int)((2 * d.Archetype.Radius.Raw + Fixed.OneRaw - 1) / Fixed.OneRaw);
        return Nav.Class(tc, width);
    }

    /// <summary>
    /// The navigation class a group's flow uses for members of class k: for soldiers on terrain,
    /// the same ground with roads costed as the ground beside them, so a crowd keeps its width
    /// instead of funnelling single file down a road (a road is no faster on foot); vehicles keep
    /// preferring roads.
    /// </summary>
    internal int FlowClass(int k)
    {
        if (k < 0 || _terrain is null || _terrain.Rules.Classes[Nav.TerrainClass(k)].Locomotor != Locomotor.Foot)
        {
            return k;
        }
        return Nav.Class(Nav.TerrainClass(k), Nav.Width(k), noRoads: true);
    }

    /// <summary>The navigation class of a unit definition, or -1 (not routed: aircraft, structures).</summary>
    internal int ClassOf(UnitDef d) => d.Index < _defClass.Length ? _defClass[d.Index] : -1;

    /// <summary>The navigation class a group flow uses for a unit definition (<see cref="FlowClass"/>), or -1.</summary>
    internal int FlowClassOf(UnitDef d) => d.Index < _defFlowClass.Length ? _defFlowClass[d.Index] : -1;

    internal Hpa Graph(int k)
    {
        while (_hpa.Count <= k)
        {
            _hpa.Add(null);
        }
        return _hpa[k] ??= new Hpa(Nav, k, Search);
    }

    /// <summary>
    /// Builds the HPA* sectors of the classes flagged in use (by class index) that are not built
    /// yet, in class then sector order, until about budget cells of build work are done (a sector's
    /// node searches one at a time, so at most one search past it). Only the cache changes:
    /// results never depend on what was built when.
    /// </summary>
    internal void Warm(bool[] inUse, long budget)
    {
        long spent = 0;
        for (var k = 0; k < Nav.Classes && k < inUse.Length; k++)
        {
            if (!inUse[k])
            {
                continue;
            }
            spent += Graph(k).Warm(budget - spent);
            if (spent >= budget)
            {
                return;
            }
        }
    }

    /// <summary>Builds every class's sectors now.</summary>
    internal void BuildAll()
    {
        var work = Search.Expanded;
        for (var k = 0; k < Nav.Classes; k++)
        {
            Graph(k).BuildAll();
            Component(k, 0);
        }
        Search.Expanded = work;
    }

    /// <summary>Applies the map changes logged since the last call; returns whether there were any.</summary>
    internal bool DrainChanges()
    {
        var any = _pendingRects.Count > 0;
        foreach (var (x0, y0, x1, y1) in _pendingRects)
        {
            Touch(Nav.Refresh(x0, y0, x1, y1));
        }
        _pendingRects.Clear();
        any |= Drain(_grid.ChangeLog!);
        if (_terrain?.ChangeLog is { } log)
        {
            any |= Drain(log);
        }
        if (any)
        {
            Version++;
        }
        return any;
    }

    // The rectangles of cells refreshed by each map change (the version it made), oldest first,
    // so a group flow re-plans only when a change reaches its corridor.
    private readonly List<(uint Version, int X0, int Y0, int X1, int Y1)> _touched = [];
    private const int TouchedMax = 4096;

    // Touch invalidates the sectors over a refreshed rectangle and remembers it for staleness.
    private void Touch((int X0, int Y0, int X1, int Y1) rect)
    {
        foreach (var h in _hpa)
        {
            h?.Invalidate(rect.X0, rect.Y0, rect.X1, rect.Y1);
        }
        if (_touched.Count >= TouchedMax)
        {
            _touched.RemoveRange(0, TouchedMax / 2);
        }
        _touched.Add((Version + 1, rect.X0, rect.Y0, rect.X1, rect.Y1));
    }

    /// <summary>
    /// Whether a planned flow must re-plan: a map change since it was planned touched a sector of
    /// its corridor or one beside it (whose rebuild renumbers the corridor's portals). A flow the
    /// changes missed is brought up to date and stays valid.
    /// </summary>
    internal bool IsStale(Flow flow)
    {
        if (flow.Version is not { } v)
        {
            return true;
        }
        if (v == Version)
        {
            return false;
        }
        if (_touched.Count == 0 || _touched[0].Version > v + 1 || flow.Corridor.Length == 0)
        {
            return true; // changes older than remembered, or no corridor to check
        }
        var hpa = Graph(flow.K);
        foreach (var (tv, x0, y0, x1, y1) in _touched)
        {
            if (tv <= v)
            {
                continue;
            }
            int sx0 = Math.Max(x0 / Hpa.S - 1, 0), sy0 = Math.Max(y0 / Hpa.S - 1, 0);
            int sx1 = Math.Min(x1 / Hpa.S + 1, hpa.SX - 1), sy1 = Math.Min(y1 / Hpa.S + 1, hpa.SY - 1);
            for (var sy = sy0; sy <= sy1; sy++)
            {
                for (var sx = sx0; sx <= sx1; sx++)
                {
                    if (flow.Corridor[sy * hpa.SX + sx])
                    {
                        return true;
                    }
                }
            }
        }
        flow.Version = Version;
        return false;
    }

    /// <summary>
    /// Brings the structure footprints routing blocks up to date with the live units (sorted by
    /// ID): structures that aren't garrisonable buildings (those block the grid itself) are hard
    /// boxes for movement, so their footprints are blocked for every class while they stand.
    /// The cells are refreshed on the next <see cref="DrainChanges"/>.
    /// </summary>
    internal void SyncStructures(IReadOnlyList<Unit> units)
    {
        var i = 0;
        var changed = false;
        _scratchStructures.Clear();
        foreach (var u in units)
        {
            if (!u.Def.IsStructure || u.Def.Archetype.Garrison is not null || u.Def.Archetype.Pavement)
            {
                continue; // pavement (an airfield's apron and runway) blocks no route
            }
            while (i < _structures.Count && _structures[i].Id < u.Id)
            {
                Stamp(_structures[i++], -1); // gone
                changed = true;
            }
            if (i < _structures.Count && _structures[i].Id == u.Id)
            {
                _scratchStructures.Add(_structures[i++]);
                continue;
            }
            var o = World.FootprintOrigin(u);
            var s = (u.Id, o.X, o.Y, World.FootW(u), World.FootH(u));
            Stamp(s, 1); // new
            _scratchStructures.Add(s);
            changed = true;
        }
        while (i < _structures.Count)
        {
            Stamp(_structures[i++], -1);
            changed = true;
        }
        if (changed)
        {
            (_structures, _scratchStructures) = (_scratchStructures, _structures);
        }

        void Stamp((uint Id, int X, int Y, int W, int H) s, int delta)
        {
            Nav.Occupy(s.X, s.Y, s.W, s.H, delta);
            _pendingRects.Add((s.X, s.Y, s.X + s.W - 1, s.Y + s.H - 1));
        }
    }

    private bool Drain(List<int> log)
    {
        if (log.Count == 0)
        {
            return false;
        }
        var w = Nav.W;
        foreach (var cell in log)
        {
            Touch(Nav.Refresh(cell % w, cell / w, cell % w, cell / w));
        }
        log.Clear();
        return true;
    }

    internal int Index(Cell c) => c.Y * Nav.W + c.X;

    internal Cell CellAt(int i) => new(i % Nav.W, i / Nav.W);

    internal bool Passable(int k, int cell) => Nav.Cost(k, cell) != 0;

    /// <summary>
    /// The passable cell (for class k) nearest to c, walking out in rings of the same order as
    /// <see cref="Grid.TrySpiral"/> up to radius r, or -1. Open: only cells the class crosses at
    /// full cost (flat ground or road, no forest); skipMarked: not marked (slots taken).
    /// </summary>
    internal int NearestPassable(int k, Cell c, int r, bool skipMarked = false, bool open = false)
    {
        c = new Cell(Math.Clamp(c.X, 0, Nav.W - 1), Math.Clamp(c.Y, 0, Nav.H - 1));
        for (var d = 0; d <= r; d++)
        {
            if (d == 0)
            {
                if (Ok(c.X, c.Y))
                {
                    return Index(c);
                }
                continue;
            }
            for (var x = c.X - d; x <= c.X + d; x++)
            {
                if (Ok(x, c.Y - d))
                {
                    return (c.Y - d) * Nav.W + x;
                }
            }
            for (var y = c.Y - d + 1; y <= c.Y + d; y++)
            {
                if (Ok(c.X + d, y))
                {
                    return y * Nav.W + c.X + d;
                }
            }
            for (var x = c.X + d - 1; x >= c.X - d; x--)
            {
                if (Ok(x, c.Y + d))
                {
                    return (c.Y + d) * Nav.W + x;
                }
            }
            for (var y = c.Y + d - 1; y > c.Y - d; y--)
            {
                if (Ok(c.X - d, y))
                {
                    return y * Nav.W + c.X - d;
                }
            }
        }
        return -1;

        bool Ok(int x, int y) => Nav.Contains(x, y) && Nav.Cost(k, x, y) != 0 && (!open || Nav.Cost(k, x, y) <= NavGrid.Straight) &&
            (!skipMarked || _mark[y * Nav.W + x] != _markGen);
    }

    /// <summary>Starts a new set of scratch marks (formation slots taken).</summary>
    internal void BeginMarks()
    {
        if (_mark.Length != Nav.W * Nav.H)
        {
            _mark = new uint[Nav.W * Nav.H];
            _markGen = 0;
        }
        _markGen++;
        if (_markGen == 0)
        {
            Array.Clear(_mark);
            _markGen = 1;
        }
    }

    internal void Mark(int cell) => _mark[cell] = _markGen;

    /// <summary>
    /// Plans a route for class k from cell s to cell d. Near routes come back refined, as every
    /// cell in path (route empty); far ones as abstract waypoints in route (portal cells, then
    /// d) for <see cref="Refine"/>. When d can't be reached, toward the nearest reachable point.
    /// </summary>
    internal void FindRoute(int k, int s, int d, List<int> path, List<int> route)
    {
        path.Clear();
        route.Clear();
        if (s == d)
        {
            return;
        }
        if (Component(k, s) != Component(k, d))
        {
            // Not reachable at all: as near as the start's ground goes (or stay).
            var near = NearestInComponent(k, d, Component(k, s));
            if (near == s)
            {
                return;
            }
            if (near >= 0)
            {
                d = near; // else the capped search below gets as near as it can
            }
        }
        int sx = s % Nav.W, sy = s / Nav.W, dx = d % Nav.W, dy = d / Nav.W;
        if (Math.Max(Math.Abs(sx - dx), Math.Abs(sy - dy)) <= NearRange)
        {
            var win = (Math.Min(sx, dx) - NearMargin, Math.Min(sy, dy) - NearMargin, Math.Max(sx, dx) + NearMargin, Math.Max(sy, dy) + NearMargin);
            if (Search.AStar(Nav, k, s, d, win, path, out _))
            {
                return;
            }
            path.Clear();
        }
        Abstract(k, s, d, route);
        if (route.Count == 0)
        {
            // Not even a portal is reachable: as close as a window around the start allows.
            var win = (sx - NearRange, sy - NearRange, sx + NearRange, sy + NearRange);
            Search.AStar(Nav, k, s, d, win, path, out _);
        }
    }

    // Per class, the connected region of every cell (0: impassable), rebuilt on first use after
    // the map changed. Steps are passable both ways or neither, so regions are undirected.
    private readonly List<int[]?> _comp = [];
    private readonly List<uint> _compVersion = [];
    private int[] _stack = [];

    /// <summary>The connected region (for class k) of a cell: 0 when impassable.</summary>
    internal int Component(int k, int cell)
    {
        while (_comp.Count <= k)
        {
            _comp.Add(null);
            _compVersion.Add(0);
        }
        if (_comp[k] is null || _compVersion[k] != Version)
        {
            Label(k);
        }
        return _comp[k]![cell];
    }

    private void Label(int k)
    {
        var n = Nav.W * Nav.H;
        var comp = _comp[k] is { } old && old.Length == n ? old : new int[n];
        Array.Clear(comp);
        if (_stack.Length < n)
        {
            _stack = new int[n];
        }
        var next = 0;
        for (var start = 0; start < n; start++)
        {
            if (comp[start] != 0 || Nav.Cost(k, start) == 0)
            {
                continue;
            }
            next++;
            var top = 0;
            _stack[top++] = start;
            comp[start] = next;
            while (top > 0)
            {
                var c = _stack[--top];
                int x = c % Nav.W, y = c / Nav.W;
                for (var d = 0; d < 8; d++)
                {
                    int nx = x + LocalSearch.Dx[d], ny = y + LocalSearch.Dy[d];
                    if (!Nav.Contains(nx, ny) || comp[ny * Nav.W + nx] != 0 || Nav.Step(k, x, y, LocalSearch.Dx[d], LocalSearch.Dy[d]) == 0)
                    {
                        continue;
                    }
                    comp[ny * Nav.W + nx] = next;
                    _stack[top++] = ny * Nav.W + nx;
                }
            }
        }
        _comp[k] = comp;
        _compVersion[k] = Version;
    }

    // NearestInComponent is the cell of region id nearest to cell (rings out to 64), or -1.
    private int NearestInComponent(int k, int cell, int id)
    {
        int cx = cell % Nav.W, cy = cell / Nav.W;
        for (var r = 1; r <= 64; r++)
        {
            for (var y = cy - r; y <= cy + r; y++)
            {
                for (var x = cx - r; x <= cx + r; x++)
                {
                    if ((Math.Abs(x - cx) == r || Math.Abs(y - cy) == r) && Nav.Contains(x, y) && Component(k, y * Nav.W + x) == id)
                    {
                        return y * Nav.W + x;
                    }
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// Refines up to n waypoints of a route, from cell from: appends the cells to path and
    /// advances idx. Returns false if a leg can't be walked any more (the map changed): re-plan.
    /// </summary>
    internal bool Refine(int k, int from, IReadOnlyList<int> route, ref int idx, int n, List<int> path)
    {
        var hpa = Graph(k);
        for (var i = 0; i < n && idx < route.Count; i++)
        {
            if (route[idx] == from)
            {
                idx++; // a waypoint where it already is (the start or goal on a portal)
                continue;
            }
            // Two legs at a time, straight to the second waypoint inside the sectors of all three:
            // the path is free to leave the portal in between, so it bends less.
            var last = Math.Min(idx + 1, route.Count - 1);
            var to = route[last];
            int fx = from % Nav.W, fy = from / Nav.W, tx = to % Nav.W, ty = to / Nav.W;
            if (last == idx && Math.Max(Math.Abs(fx - tx), Math.Abs(fy - ty)) <= 1 && Nav.Step(k, fx, fy, tx - fx, ty - fy) != 0)
            {
                path.Add(to); // a neighbour (a diagonal that would cut a corner goes the long way below)
            }
            else
            {
                var a = hpa.Bounds(hpa.SectorOf(from));
                var b = hpa.Bounds(hpa.SectorOf(route[idx]));
                var c = hpa.Bounds(hpa.SectorOf(to));
                var win = (Math.Min(a.X0, Math.Min(b.X0, c.X0)), Math.Min(a.Y0, Math.Min(b.Y0, c.Y0)),
                    Math.Max(a.X1, Math.Max(b.X1, c.X1)), Math.Max(a.Y1, Math.Max(b.Y1, c.Y1)));
                if (!Search.AStar(Nav, k, from, to, win, path, out _))
                {
                    return false;
                }
            }
            from = to;
            idx = last + 1;
        }
        return true;
    }

    /// <summary>
    /// Whether the straight segment from a to b crosses only cells class k can use (sampled
    /// every quarter cell): a unit aiming from a at b doesn't cut a corner.
    /// </summary>
    internal bool Clear(int k, Vec a, Vec b)
    {
        var d = b - a;
        var n = (int)(Math.Max(Math.Abs(d.X.Raw), Math.Abs(d.Y.Raw)) / (Fixed.OneRaw / 4)) + 1;
        for (var i = 0; i <= n; i++)
        {
            var p = new Vec(Fixed.FromRaw(a.X.Raw + d.X.Raw * i / n), Fixed.FromRaw(a.Y.Raw + d.Y.Raw * i / n));
            var c = Cell.Of(p);
            if (!Nav.Contains(c.X, c.Y) || (Nav.Cost(k, c.X, c.Y) == 0 && i > 0))
            {
                return false;
            }
        }
        return true;
    }

    private const int MaxJump = 12;
    private readonly List<int> _smooth = [];
    private readonly List<long> _prefix = [];

    /// <summary>
    /// Straightens path[start..] (which follows cell from) in place: from each anchor it jumps
    /// to the farthest of the next cells (up to 12) that a straight 8-way line reaches at no more
    /// cost, so equal-cost zigzags (a search's diagonal-first steps, the bends at portals) become
    /// the line between. Never dearer than the path it straightens.
    /// </summary>
    internal void Smooth(int k, int from, List<int> path, int start)
    {
        var n = path.Count - start;
        if (n < 3)
        {
            return;
        }
        // _prefix[i]: the cost from the anchor before path[start] to path[start + i - 1].
        _prefix.Clear();
        _prefix.Add(0);
        var prev = from;
        for (var i = start; i < path.Count; i++)
        {
            var st = StepBetween(k, prev, path[i]);
            _prefix.Add(_prefix[^1] + (st == 0 ? Unreachable : st)); // not neighbours: any line is cheaper
            prev = path[i];
        }
        _smooth.Clear();
        var a = 0; // index in _prefix of the anchor (0 = from)
        var anchorCell = from;
        while (a < n)
        {
            var best = a + 1;
            for (var j = Math.Min(a + MaxJump, n); j >= a + 2; j--)
            {
                var c = LineCost(k, anchorCell, path[start + j - 1]);
                if (c != Unreachable && c <= _prefix[j] - _prefix[a])
                {
                    best = j;
                    break;
                }
            }
            if (best == a + 1)
            {
                _smooth.Add(path[start + a]);
            }
            else
            {
                Line(anchorCell, path[start + best - 1], _smooth);
            }
            anchorCell = path[start + best - 1];
            a = best;
        }
        path.RemoveRange(start, n);
        path.AddRange(_smooth);
    }

    private int StepBetween(int k, int a, int b)
    {
        int ax = a % Nav.W, ay = a / Nav.W, bx = b % Nav.W, by = b / Nav.W;
        return Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by)) == 1 ? Nav.Step(k, ax, ay, bx - ax, by - ay) : 0;
    }

    // LineCell is cell t of the straight 8-way line of n steps from (ax, ay) by (dx, dy): each
    // coordinate rounded to nearest (halves up), so neighbours differ by at most one each way.
    private int LineCell(int ax, int ay, int dx, int dy, int n, int t) =>
        (ay + FloorDiv(2 * dy * t + n, 2 * n)) * Nav.W + ax + FloorDiv(2 * dx * t + n, 2 * n);

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // LineCost is the cost of walking the straight line from a to b, or Unreachable.
    private int LineCost(int k, int a, int b)
    {
        int ax = a % Nav.W, ay = a / Nav.W, dx = b % Nav.W - ax, dy = b / Nav.W - ay;
        var n = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var cost = 0;
        var prev = a;
        for (var t = 1; t <= n; t++)
        {
            var c = LineCell(ax, ay, dx, dy, n, t);
            var st = StepBetween(k, prev, c);
            if (st == 0)
            {
                return Unreachable;
            }
            cost += st;
            prev = c;
        }
        return cost;
    }

    // Line appends the cells of the straight line from a (excluded) to b.
    private void Line(int a, int b, List<int> cells)
    {
        int ax = a % Nav.W, ay = a / Nav.W, dx = b % Nav.W - ax, dy = b / Nav.W - ay;
        var n = Math.Max(Math.Abs(dx), Math.Abs(dy));
        for (var t = 1; t <= n; t++)
        {
            cells.Add(LineCell(ax, ay, dx, dy, n, t));
        }
    }

    private void BeginAbstract(int n)
    {
        if (_ag.Length < n)
        {
            _ag = new int[n];
            _ap = new int[n];
            _aseen = new uint[n];
            _aclosed = new uint[n];
            _agen = 0;
        }
        _agen++;
        if (_agen == 0)
        {
            Array.Clear(_aseen);
            Array.Clear(_aclosed);
            _agen = 1;
        }
        _aopen.Clear();
    }

    // Abstract runs A* over the portal graph of class k from cell s to cell d, and appends the
    // waypoint cells (each portal on the way, then d) to route; when d is unreachable, the cells
    // up to the reached portal nearest to it.
    private void Abstract(int k, int s, int d, List<int> route)
    {
        var hpa = Graph(k);
        int ss = hpa.SectorOf(s), ds = hpa.SectorOf(d);
        var sNodes = hpa.Get(ss).Nodes;
        var dNodes = hpa.Get(ds).Nodes;
        Span<(int, int)> seed = [(s, 0)];
        Search.Dijkstra(Nav, k, seed, hpa.Bounds(ss), reverse: false);
        var startCost = new int[sNodes.Length];
        for (var i = 0; i < sNodes.Length; i++)
        {
            startCost[i] = Search.Dist(sNodes[i].Cell);
        }
        var direct = ss == ds ? Search.Dist(d) : Unreachable;
        seed[0] = (d, 0);
        Search.Dijkstra(Nav, k, seed, hpa.Bounds(ds), reverse: true);
        var goalCost = new int[dNodes.Length];
        for (var i = 0; i < dNodes.Length; i++)
        {
            goalCost[i] = Search.Dist(dNodes[i].Cell);
        }

        var total = hpa.SX * hpa.SY * Hpa.MaxNodes;
        int start = total, goal = total + 1;
        int dx = d % Nav.W, dy = d / Nav.W;
        var hScale = Nav.MinCost(k);
        // At the cheapest cell's cost (a road's) a little over (tenths, AbstractGreed), but at most
        // open flat ground's: it explores far less where roads make the cheapest cell cheap, and
        // the routes stay within a few percent (RoutingTests measure it against plain A*).
        var h10 = Math.Min(hScale * AbstractGreed, NavGrid.Straight * 10);
        int best = start, bestH = 0;
        var found = false;
        bool[]? allowed = null;
        if (h10 < NavGrid.Straight * 10 && Math.Max(Math.Abs(ss % hpa.SX - ds % hpa.SX), Math.Abs(ss / hpa.SX - ds / hpa.SX)) > CorridorFrom)
        {
            // Far, with a weak heuristic (roads make the cheapest cell cheap): a quick greedy
            // search at open ground's cost first, and the careful one only inside its corridor.
            if (!Run(NavGrid.Straight * CorridorGreed))
            {
                Collect();
                return;
            }
            allowed = Corridor(hpa, route, ss, ds, CorridorWidth);
            route.Clear();
        }
        Run(h10);
        Collect();

        bool Run(int scale)
        {
            h10 = scale;
            BeginAbstract(total + 2);
            _ag[start] = 0;
            _ap[start] = -1;
            _aseen[start] = _agen;
            var h0 = H(s);
            _aopen.Push(((long)h0 << 32) | (uint)h0, start);
            best = start;
            bestH = h0;
            found = false;
            var expansions = 0;
            while (_aopen.Count > 0)
            {
                var (key, id) = _aopen.Pop();
                if (_aclosed[id] == _agen)
                {
                    continue;
                }
                _aclosed[id] = _agen;
                if (id == goal)
                {
                    found = true;
                    break;
                }
                if (++expansions > MaxAbstractNodes)
                {
                    break; // an unreachable goal: as near as this got
                }
                var h = (int)(key & 0xFFFFFFFF);
                if (h < bestH)
                {
                    best = id;
                    bestH = h;
                }
                var g = _ag[id];
                if (id == start)
                {
                    for (var i = 0; i < sNodes.Length; i++)
                    {
                        if (startCost[i] != Unreachable)
                        {
                            Relax(ss * Hpa.MaxNodes + i, g + startCost[i], id);
                        }
                    }
                    if (direct != Unreachable)
                    {
                        Relax(goal, g + direct, id);
                    }
                    continue;
                }
                int sec = id / Hpa.MaxNodes, li = id % Hpa.MaxNodes;
                var sector = hpa.Get(sec);
                Search.Expanded += 1 + sector.OutStart[li + 1] - sector.OutStart[li];
                for (var e = sector.OutStart[li]; e < sector.OutStart[li + 1]; e++)
                {
                    Relax(sec * Hpa.MaxNodes + sector.OutTo[e], g + sector.OutCost[e], id);
                }
                var (p, pc) = hpa.Partner(sec, li);
                if (p >= 0 && (allowed is null || allowed[p / Hpa.MaxNodes]))
                {
                    Relax(p, g + pc, id);
                }
                if (sec == ds && goalCost[li] != Unreachable)
                {
                    Relax(goal, g + goalCost[li], id);
                }
            }
            if (found)
            {
                Collect();
            }
            return found;
        }

        // Collect appends the waypoints of the search's result (or of the nearest point reached).
        void Collect()
        {
            if (route.Count > 0 || (!found && best == start))
            {
                return;
            }
            var from = route.Count;
            for (var id = found ? goal : best; id != start; id = _ap[id])
            {
                route.Add(id == goal ? d : NodeCell(id));
            }
            route.Reverse(from, route.Count - from);
        }

        int NodeCell(int id) => hpa.Get(id / Hpa.MaxNodes).Nodes[id % Hpa.MaxNodes].Cell;

        int H(int cell) => LocalSearch.Octile(cell % Nav.W, cell / Nav.W, dx, dy) * h10 / (NavGrid.Straight * 10);

        void Relax(int v, int ng, int parent)
        {
            if (_aclosed[v] == _agen || (_aseen[v] == _agen && ng >= _ag[v]))
            {
                return;
            }
            _aseen[v] = _agen;
            _ag[v] = ng;
            _ap[v] = parent;
            var hv = v == goal ? 0 : H(NodeCell(v));
            _aopen.Push(((long)(ng + hv) << 32) | (uint)hv, v);
        }
    }

    // Corridor marks the sectors of a route's waypoints and of its two ends, and those within
    // width sectors of them.
    private static bool[] Corridor(Hpa hpa, List<int> route, int ss, int ds, int width)
    {
        var on = new bool[hpa.SX * hpa.SY];
        on[ss] = true;
        on[ds] = true;
        foreach (var c in route)
        {
            on[hpa.SectorOf(c)] = true;
        }
        var wide = new bool[on.Length];
        for (var sec = 0; sec < on.Length; sec++)
        {
            if (!on[sec])
            {
                continue;
            }
            int sx = sec % hpa.SX, sy = sec / hpa.SX;
            for (var y = Math.Max(sy - width, 0); y <= Math.Min(sy + width, hpa.SY - 1); y++)
            {
                for (var x = Math.Max(sx - width, 0); x <= Math.Min(sx + width, hpa.SX - 1); x++)
                {
                    wide[y * hpa.SX + x] = true;
                }
            }
        }
        return wide;
    }

    /// <summary>
    /// Plans a group flow: the group's one abstract route, an A* over the portal graph backward
    /// from the goal toward the sectors the members start in (it goes on, a bounded while, until
    /// it has reached each of them); its corridor, the sectors on the way and around them; and a
    /// Dijkstra over the corridor's portals from the goal, which leaves each one's cost to the
    /// goal in the flow. Its sectors' flow fields are built from them on use.
    /// </summary>
    internal void Plan(Flow flow, IReadOnlyList<int> startCells)
    {
        var k = flow.K;
        var hpa = Graph(k);
        var sectors = hpa.SX * hpa.SY;
        flow.Values = new int[]?[sectors];
        flow.Fields = new FlowField?[sectors];
        flow.Version = Version;
        var gs = hpa.SectorOf(flow.Goal);
        var need = new bool[sectors];
        var corridor = new bool[sectors];
        var remaining = 0;
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = int.MinValue, by1 = int.MinValue;
        foreach (var c in startCells)
        {
            var sec = hpa.SectorOf(c);
            var (x0, y0, x1, y1) = hpa.Bounds(sec);
            (bx0, by0, bx1, by1) = (Math.Min(bx0, x0), Math.Min(by0, y0), Math.Max(bx1, x1), Math.Max(by1, y1));
            if (!need[sec])
            {
                need[sec] = true;
                corridor[sec] = true;
                remaining++;
            }
        }
        if (bx0 > bx1)
        {
            (bx0, by0, bx1, by1) = hpa.Bounds(gs); // no start cells (every member stands where its class can't): an empty plan
        }
        corridor[gs] = true;
        var gNodes = hpa.Get(gs).Nodes;
        Span<(int, int)> seed = [(flow.Goal, 0)];
        Search.Dijkstra(Nav, k, seed, hpa.Bounds(gs), reverse: true);
        var goalCost = new int[gNodes.Length];
        for (var i = 0; i < gNodes.Length; i++)
        {
            goalCost[i] = Search.Dist(gNodes[i].Cell);
        }
        var hScale = Nav.MinCost(k);
        var total = sectors * Hpa.MaxNodes;

        // The route: A* backward from the goal toward the start sectors' bounding box.
        if (need[gs])
        {
            need[gs] = false;
            remaining--;
        }
        BeginAbstract(total);
        for (var i = 0; i < gNodes.Length; i++)
        {
            if (goalCost[i] != Unreachable)
            {
                var id = gs * Hpa.MaxNodes + i;
                _aseen[id] = _agen;
                _ag[id] = goalCost[i];
                _ap[id] = -1;
                var h = H(gNodes[i].Cell);
                _aopen.Push(((long)(goalCost[i] + h) << 32) | (uint)h, id);
            }
        }
        long expanded = 0, limit = long.MaxValue;
        while (remaining > 0 && _aopen.Count > 0 && expanded < limit)
        {
            var (_, id) = _aopen.Pop();
            if (_aclosed[id] == _agen)
            {
                continue;
            }
            _aclosed[id] = _agen;
            expanded++;
            int sec = id / Hpa.MaxNodes, li = id % Hpa.MaxNodes;
            if (need[sec])
            {
                need[sec] = false;
                remaining--;
                for (var v = id; v >= 0; v = _ap[v])
                {
                    corridor[v / Hpa.MaxNodes] = true;
                }
                if (limit == long.MaxValue)
                {
                    limit = expanded + expanded / 2 + 512; // the rest of the group gets a bounded while more
                }
            }
            var sector = hpa.Get(sec);
            Search.Expanded += 1 + sector.InStart[li + 1] - sector.InStart[li];
            var g = _ag[id];
            for (var e = sector.InStart[li]; e < sector.InStart[li + 1]; e++)
            {
                Relax(sec * Hpa.MaxNodes + sector.InFrom[e], g + sector.InCost[e], id);
            }
            var (p, _) = hpa.Partner(sec, li);
            if (p >= 0)
            {
                var (_, back) = hpa.Partner(p / Hpa.MaxNodes, p % Hpa.MaxNodes);
                Relax(p, g + back, id);
            }
        }

        flow.Unreached = remaining > 0 && _aopen.Count == 0;
        // The corridor: the route's sectors and the start sectors, and their neighbours.
        var wide = new bool[sectors];
        for (var sec = 0; sec < sectors; sec++)
        {
            if (!corridor[sec])
            {
                continue;
            }
            int sx = sec % hpa.SX, sy = sec / hpa.SX;
            for (var y = Math.Max(sy - 1, 0); y <= Math.Min(sy + 1, hpa.SY - 1); y++)
            {
                for (var x = Math.Max(sx - 1, 0); x <= Math.Min(sx + 1, hpa.SX - 1); x++)
                {
                    wide[y * hpa.SX + x] = true;
                }
            }
        }

        flow.Corridor = wide;

        // The costs to the goal: Dijkstra over the corridor's portals.
        BeginAbstract(total);
        for (var i = 0; i < gNodes.Length; i++)
        {
            if (goalCost[i] != Unreachable)
            {
                var id = gs * Hpa.MaxNodes + i;
                _aseen[id] = _agen;
                _ag[id] = goalCost[i];
                _aopen.Push(goalCost[i], id);
            }
        }
        while (_aopen.Count > 0)
        {
            var (g, id) = _aopen.Pop();
            if (_aclosed[id] == _agen || g != _ag[id])
            {
                continue;
            }
            _aclosed[id] = _agen;
            int sec = id / Hpa.MaxNodes, li = id % Hpa.MaxNodes;
            var sector = hpa.Get(sec);
            var n = sector.Nodes.Length;
            Search.Expanded += 1 + sector.InStart[li + 1] - sector.InStart[li];
            var vals = flow.Values[sec];
            if (vals is null)
            {
                vals = new int[n];
                Array.Fill(vals, Unreachable);
                flow.Values[sec] = vals;
            }
            vals[li] = (int)g;
            for (var e = sector.InStart[li]; e < sector.InStart[li + 1]; e++)
            {
                Relax(sec * Hpa.MaxNodes + sector.InFrom[e], (int)g + sector.InCost[e], -1, heuristic: false);
            }
            var (p, _) = hpa.Partner(sec, li);
            if (p >= 0 && wide[p / Hpa.MaxNodes])
            {
                var (_, back) = hpa.Partner(p / Hpa.MaxNodes, p % Hpa.MaxNodes);
                Relax(p, (int)g + back, -1, heuristic: false);
            }
        }

        // H: the distance to the start sectors' bounding box, at open ground's cost and then some:
        // greedier than admissible, for a quick route; the corridor's Dijkstra makes the costs exact.
        int H(int cell)
        {
            int x = cell % Nav.W, y = cell / Nav.W;
            return LocalSearch.Octile(x, y, Math.Clamp(x, bx0, bx1), Math.Clamp(y, by0, by1)) * Math.Max(hScale, NavGrid.Straight) * RouteGreed / (NavGrid.Straight * 10);
        }

        void Relax(int v, int ng, int parent, bool heuristic = true)
        {
            if (_aclosed[v] == _agen || (_aseen[v] == _agen && ng >= _ag[v]))
            {
                return;
            }
            _aseen[v] = _agen;
            _ag[v] = ng;
            _ap[v] = parent;
            if (heuristic)
            {
                var h = H(hpa.Get(v / Hpa.MaxNodes).Nodes[v % Hpa.MaxNodes].Cell);
                _aopen.Push(((long)(ng + h) << 32) | (uint)h, v);
            }
            else
            {
                _aopen.Push(ng, v);
            }
        }
    }

    /// <summary>A sector's flow field for a planned flow, built on first use.</summary>
    internal FlowField? Field(Flow flow, int sec)
    {
        if (flow.Fields[sec] is { } f)
        {
            return f;
        }
        var k = flow.K;
        var hpa = Graph(k);
        var (x0, y0, x1, y1) = hpa.Bounds(sec);
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1, w = Nav.W;
        int sx = sec % hpa.SX, sy = sec / hpa.SX;
        var seeds = new List<(int, int)>();
        var exitVal = new int[bw * bh];
        var exitDir = new byte[bw * bh];
        Array.Fill(exitVal, Unreachable);
        if (hpa.SectorOf(flow.Goal) == sec)
        {
            seeds.Add((flow.Goal, 0));
        }
        // Exits: every open cell of a border with the corridor beyond, seeded with the cost of
        // crossing plus an upper bound of the cost from the far cell (the far side's portal
        // costs, walked along the border on the far side).
        for (var side = 0; side < 4; side++)
        {
            int nsec, dir, len, nx0, ny0, fx0, fy0, stx, sty, opposite;
            switch (side)
            {
                case 0: if (sx == 0) { continue; } nsec = sec - 1; dir = 6; opposite = 1; len = bh; nx0 = x0; ny0 = y0; fx0 = x0 - 1; fy0 = y0; stx = 0; sty = 1; break;
                case 1: if (sx + 1 >= hpa.SX) { continue; } nsec = sec + 1; dir = 2; opposite = 0; len = bh; nx0 = x1; ny0 = y0; fx0 = x1 + 1; fy0 = y0; stx = 0; sty = 1; break;
                case 2: if (sy == 0) { continue; } nsec = sec - hpa.SX; dir = 0; opposite = 3; len = bw; nx0 = x0; ny0 = y0; fx0 = x0; fy0 = y0 - 1; stx = 1; sty = 0; break;
                default: if (sy + 1 >= hpa.SY) { continue; } nsec = sec + hpa.SX; dir = 4; opposite = 2; len = bw; nx0 = x0; ny0 = y1; fx0 = x0; fy0 = y1 + 1; stx = 1; sty = 0; break;
            }
            if (flow.Values[nsec] is not { } nvals)
            {
                continue;
            }
            var far = new int[len];
            Array.Fill(far, Unreachable);
            var nNodes = hpa.Get(nsec).Nodes;
            for (var j = 0; j < nNodes.Length && j < nvals.Length; j++)
            {
                if (nNodes[j].Side != opposite || nvals[j] == Unreachable)
                {
                    continue;
                }
                var c = nNodes[j].Cell;
                var t = stx == 1 ? c % w - fx0 : c / w - fy0;
                far[t] = Math.Min(far[t], nvals[j]);
            }
            for (var t = 1; t < len; t++)
            {
                if (far[t - 1] != Unreachable && Open(t) && Open(t - 1))
                {
                    var st = Nav.Step(k, fx0 + stx * t, fy0 + sty * t, -stx, -sty);
                    if (st != 0)
                    {
                        far[t] = Math.Min(far[t], far[t - 1] + st);
                    }
                }
            }
            for (var t = len - 2; t >= 0; t--)
            {
                if (far[t + 1] != Unreachable && Open(t) && Open(t + 1))
                {
                    var st = Nav.Step(k, fx0 + stx * t, fy0 + sty * t, stx, sty);
                    if (st != 0)
                    {
                        far[t] = Math.Min(far[t], far[t + 1] + st);
                    }
                }
            }
            for (var t = 0; t < len; t++)
            {
                if (far[t] == Unreachable || !Open(t))
                {
                    continue;
                }
                int ncx = nx0 + stx * t, ncy = ny0 + sty * t;
                var e = Nav.Step(k, ncx, ncy, LocalSearch.Dx[dir], LocalSearch.Dy[dir]);
                if (e == 0)
                {
                    continue;
                }
                e += far[t];
                var li = (ncy - y0) * bw + (ncx - x0);
                if (e < exitVal[li])
                {
                    exitVal[li] = e;
                    exitDir[li] = (byte)dir;
                }
                seeds.Add((ncy * w + ncx, e));
            }

            bool Open(int t) => Nav.Cost(k, nx0 + stx * t, ny0 + sty * t) != 0 && Nav.Cost(k, fx0 + stx * t, fy0 + sty * t) != 0;
        }
        Search.Dijkstra(Nav, k, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(seeds), (x0, y0, x1, y1), reverse: true);
        var field = new FlowField(x0, y0, bw, new int[bw * bh], new byte[bw * bh]);
        for (var ly = 0; ly < bh; ly++)
        {
            for (var lx = 0; lx < bw; lx++)
            {
                field.Dist[ly * bw + lx] = Search.Dist((y0 + ly) * w + x0 + lx);
            }
        }
        for (var ly = 0; ly < bh; ly++)
        {
            for (var lx = 0; lx < bw; lx++)
            {
                var li = ly * bw + lx;
                field.Dir[li] = FlowField.None;
                if (field.Dist[li] == Unreachable)
                {
                    continue;
                }
                int cx = x0 + lx, cy = y0 + ly;
                if (cy * w + cx == flow.Goal)
                {
                    continue;
                }
                // Ties go to the straight step (rows of units stay side by side down a wide way
                // instead of all drifting onto one lane), then to the lower cell index.
                int best = Unreachable, bestStep = int.MaxValue, bestCell = int.MaxValue, bestDir = FlowField.None;
                for (var d = 0; d < 8; d++)
                {
                    int nlx = lx + LocalSearch.Dx[d], nly = ly + LocalSearch.Dy[d];
                    if (nlx < 0 || nly < 0 || nlx >= bw || nly >= bh)
                    {
                        continue;
                    }
                    var nd = field.Dist[nly * bw + nlx];
                    if (nd == Unreachable)
                    {
                        continue;
                    }
                    var st = Nav.Step(k, cx, cy, LocalSearch.Dx[d], LocalSearch.Dy[d]);
                    if (st == 0)
                    {
                        continue;
                    }
                    var v = st + nd;
                    var ncell = (cy + LocalSearch.Dy[d]) * w + cx + LocalSearch.Dx[d];
                    if (v < best || (v == best && (st < bestStep || (st == bestStep && ncell < bestCell))))
                    {
                        best = v;
                        bestStep = st;
                        bestCell = ncell;
                        bestDir = d;
                    }
                }
                if (exitVal[li] != Unreachable)
                {
                    var ed = exitDir[li];
                    var ecell = (cy + LocalSearch.Dy[ed]) * w + cx + LocalSearch.Dx[ed];
                    if (exitVal[li] < best || (exitVal[li] == best && (Nav.Step(k, cx, cy, LocalSearch.Dx[ed], LocalSearch.Dy[ed]) < bestStep ||
                        (Nav.Step(k, cx, cy, LocalSearch.Dx[ed], LocalSearch.Dy[ed]) == bestStep && ecell < bestCell))))
                    {
                        bestDir = ed;
                    }
                }
                field.Dir[li] = (byte)bestDir;
            }
        }
        flow.Fields[sec] = field;
        return field;
    }
}

/// <summary>
/// One navigation class's share of a group move: the goal cell, and once planned, the cost to
/// the goal of every portal in the group's corridor and the sectors' flow fields built from
/// them. Derived from the map, the goal and the members' sectors when it was planned.
/// </summary>
internal sealed class Flow(int k, int goal)
{
    /// <summary>The navigation class of the members it carries (K may be its crowd variant).</summary>
    internal int UnitClass { get; init; } = k;

    /// <summary>Extra cost the corridor reaches past the farthest start sector.</summary>
    internal const int Margin = 640;

    internal int K { get; } = k;

    internal int Goal { get; set; } = goal;

    /// <summary>Whether the last plan found start sectors the goal can't be reached from.</summary>
    internal bool Unreached { get; set; }

    /// <summary>The router version it was planned at; null while unplanned.</summary>
    internal uint? Version { get; set; }

    internal int[]?[] Values { get; set; } = [];

    internal FlowField?[] Fields { get; set; } = [];

    /// <summary>Per sector: whether it is in the corridor the flow was planned over.</summary>
    internal bool[] Corridor { get; set; } = [];
}

/// <summary>A flow field over one sector: per cell the cost to the goal and the step to take.</summary>
internal sealed record FlowField(int X0, int Y0, int Bw, int[] Dist, byte[] Dir)
{
    /// <summary>No step: the goal, or no way to it from here.</summary>
    internal const byte None = 255;
}
