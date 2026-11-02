using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Helicopter ceilings (2026-11-02, the owner: "helicopters stay limited in height but still can climb up to a certain point",
// for high mountain maps; docs/design.md, "Helicopters", "Ceiling"). A helicopter with a ceiling (flight.rotary.ceiling: raw
// over the map's lowest ground as loaded, TerrainMap.BaseRaw) never climbs above it, and ground higher than its ceiling less
// its hide height (the least it keeps over the ground) is a wall to it:
//  - it never moves into a wall cell (a cell whose highest corner is above that; it slides along the wall instead);
//  - steering: while nothing on the straight line to its goal is a wall (looked at up to 5 s of its top speed, at least 64
//    cells) it flies straight as before; otherwise it plans a route (A* over the cells round it and the goal, a diagonal only
//    past two free sides, a step next to a wall dearer), pulled tight into waypoints, and flies along it through valleys and
//    passes. A goal it cannot reach (walled in, or on the wall itself) takes it to the nearest cell it can reach, where it
//    hovers. The route is state (HeliState.Route, hashed while it has one); the search buffers are scratch.
// Nothing changes on a map whose highest ground stays below the wall height (every map so far with the 400 m ceiling: the
// most relief is 264 m; the terrain allows ±1,024 m since 2026-11-02): no wall, no route, no clamp that bites.
public sealed partial class World
{
    // The search window: this many cells round the start and the goal, plus half their distance; at most this many cells a
    // side (a farther goal is planned to a point on the way first: a partial route, planned again from its end).
    private const int HeliRouteMargin = 64, HeliRouteWindow = 1024;

    // The most cells a search expands (a bigger search ends at the cell nearest the goal so far: a partial route).
    private const int HeliRouteBudget = 400_000;

    // A step into a cell next to a wall costs this much more (a straight step costs 10, a diagonal 14): it keeps off the slopes.
    private const int HeliRouteNearWall = 20;

    // A goal that moved less than this (a target, a post) keeps its route; the last leg follows it while it sees it.
    private const long HeliRegoalRaw = 4 * Fixed.OneRaw;

    // Segments are sampled every half cell; a waypoint is passed once the next one is in sight along a lane this far to
    // either side of the line (three quarters of a cell: it keeps a little off the walls).
    private const long HeliSampleRaw = Fixed.OneRaw / 2, HeliLaneRaw = Fixed.OneRaw * 3 / 4;

    // A route waypoint at most this many path cells after the last (bounds the pulling's sight tests).
    private const int HeliPullMax = 64;

    // Scratch (not state): the search's buffers, indexed in its window, and the generation that marks the current search.
    private int[] _hrG = [], _hrFrom = [], _hrSeen = [], _hrShut = [], _hrWallGen = [];
    private bool[] _hrWall = [];
    private int _hrGen;
    private readonly MinHeap _hrOpen = new();
    private readonly List<int> _hrPath = [];

    // HeliWallTop is the highest ground (raw, absolute) a helicopter of rotary r may fly over on this map, or long.MaxValue when
    // nothing is higher (no ceiling, no terrain, or no corner of the map above it): then it flies exactly as before.
    private long HeliWallTop(RotarySpec r)
    {
        if (r.CeilingRaw == 0 || Ground is not { } g)
        {
            return long.MaxValue;
        }
        var top = g.BaseRaw + r.CeilingRaw - r.HideHeightRaw;
        return g.HighestRaw > top ? top : long.MaxValue;
    }

    // HeliCeilingAbs is the absolute height (raw) a helicopter of rotary r never climbs above, or long.MaxValue without a ceiling.
    private long HeliCeilingAbs(RotarySpec r) => r.CeilingRaw == 0 ? long.MaxValue : (Ground?.BaseRaw ?? 0) + r.CeilingRaw;

    /// <summary>
    /// The absolute height (raw) helicopter u never climbs above on this map: its ceiling over the map's lowest ground
    /// (2026-11-02); <see cref="long.MaxValue"/> for anything without one.
    /// </summary>
    public long HeliCeilingOf(Unit u) => u.Def.Archetype.Flight?.Rotor is { } r ? HeliCeilingAbs(r) : long.MaxValue;

    /// <summary>
    /// Whether cell c is a wall to helicopter u (2026-11-02): ground above its ceiling less its hide height. False off the
    /// map, without a ceiling, or on a map with no ground that high.
    /// </summary>
    public bool HeliWallFor(Unit u, Cell c) =>
        u.Def.Archetype.Flight?.Rotor is { } r && HeliWallTop(r) is var top && top != long.MaxValue && HeliWallCell(c.X, c.Y, top);

    // HeliWallCell: cell (x, y) is on the map and its highest corner is above top.
    private bool HeliWallCell(int x, int y, long top) =>
        x >= 0 && y >= 0 && x < Grid.Width && y < Grid.Height && Ground!.CellTopRaw(x, y) > top;

    // HeliWallAt: the cell under the point (raw) is a wall.
    private bool HeliWallAt(long x, long y, long top) =>
        x >= 0 && y >= 0 && HeliWallCell((int)(x / Fixed.OneRaw), (int)(y / Fixed.OneRaw), top);

    // HeliClear reports whether the segment from a to b crosses no wall (sampled every half cell, both ends included); with a
    // lane, nor do the parallel segments that far to either side of it.
    private bool HeliClear(Vec a, Vec b, long top, long lane)
    {
        long dx = b.X.Raw - a.X.Raw, dy = b.Y.Raw - a.Y.Raw;
        var len = Len3(dx, dy, 0);
        var n = len / HeliSampleRaw + 1;
        long px = 0, py = 0;
        if (lane > 0 && len > 0)
        {
            (px, py) = (-dy * lane / len, dx * lane / len);
        }
        for (long i = 0; i <= n; i++)
        {
            long x = a.X.Raw + dx * i / n, y = a.Y.Raw + dy * i / n;
            if (HeliWallAt(x, y, top) || (lane > 0 && (HeliWallAt(x + px, y + py, top) || HeliWallAt(x - px, y - py, top))))
            {
                return false;
            }
        }
        return true;
    }

    // HeliWallStep is the part of helicopter u's step (sx, sy, raw) that keeps it out of the walls: all of it, else along x or
    // along y (it slides along the wall; the blocked part of its velocity is gone), else none. Over a wall already (put there,
    // or the ground rose under it) it may go anywhere: out.
    private (long X, long Y) HeliWallStep(Unit u, HeliState h, long top, long sx, long sy)
    {
        var p = u.Pos;
        if ((sx == 0 && sy == 0) || HeliWallAt(p.X.Raw, p.Y.Raw, top) || HeliClear(p, P(p.X.Raw + sx, p.Y.Raw + sy), top, 0))
        {
            return (sx, sy);
        }
        if (sx != 0 && HeliClear(p, P(p.X.Raw + sx, p.Y.Raw), top, 0))
        {
            (h.VelY, h.RemY) = (0, 0);
            return (sx, 0);
        }
        if (sy != 0 && HeliClear(p, P(p.X.Raw, p.Y.Raw + sy), top, 0))
        {
            (h.VelX, h.RemX) = (0, 0);
            return (0, sy);
        }
        (h.VelX, h.VelY, h.RemX, h.RemY) = (0, 0, 0, 0);
        return (0, 0);
    }

    // HeliAim is where helicopter u flies now on its way to goal past the walls (ground above top): straight at the goal while
    // nothing on the way is a wall, else at its route's next waypoint; with how much of the route lies beyond that point (raw)
    // and where the route ends (the goal, or the nearest point to it that it can reach).
    private (Vec Aim, long Beyond, Vec End) HeliAim(Unit u, HeliState h, RotarySpec r, Vec goal, long top)
    {
        if (HeliWallAt(u.Pos.X.Raw, u.Pos.Y.Raw, top))
        {
            h.Route.Clear(); // over a wall already: straight out (it may leave a wall cell, never enter one)
            return (goal, 0, goal);
        }
        if (h.Route.Count > 0 && h.RouteGoal != goal)
        {
            // The goal moved (a target, its post): a little, and the route ended at it, the last leg follows it while it sees it.
            var from = h.Route.Count > 1 ? h.Route[^2] : u.Pos;
            if (!h.RoutePartial && h.Route[^1] == h.RouteGoal && Within(h.RouteGoal, goal, Fixed.FromRaw(HeliRegoalRaw)) && !HeliWallAt(goal.X.Raw, goal.Y.Raw, top) &&
                HeliClear(from, goal, top, 0))
            {
                (h.Route[^1], h.RouteGoal) = (goal, goal);
            }
            else
            {
                h.Route.Clear();
            }
        }
        if (h.Route.Count == 0)
        {
            long dx = goal.X.Raw - u.Pos.X.Raw, dy = goal.Y.Raw - u.Pos.Y.Raw;
            var d = Len3(dx, dy, 0);
            var look = Math.Max(64L * Fixed.OneRaw, r.MaxSpeed * 5); // 5 s of its top speed (raw a second), at least 64 cells
            var ahead = d <= look ? goal : P(u.Pos.X.Raw + dx * look / d, u.Pos.Y.Raw + dy * look / d);
            if (HeliClear(u.Pos, ahead, top, 0))
            {
                return (goal, 0, goal); // nothing in the way (yet): straight on, as without a ceiling
            }
            HeliPlan(u, h, goal, top);
        }
        // Follow it: past the waypoints it reached or sees beyond.
        while (h.Route.Count > 1 && (Within(u.Pos, h.Route[0], Fixed.One) || HeliClear(u.Pos, h.Route[1], top, HeliLaneRaw)))
        {
            h.Route.RemoveAt(0);
        }
        if (!HeliClear(u.Pos, h.Route[0], top, 0) || (h.RoutePartial && h.Route.Count == 1 && Within(u.Pos, h.Route[0], Fixed.FromInt(2))))
        {
            HeliPlan(u, h, goal, top); // blocked (it drifted, the ground changed) or at the end of a partial route: again from here
        }
        if (h.Route.Count == 1 && h.Route[0] == goal && !h.RoutePartial)
        {
            h.Route.Clear(); // the goal itself in sight: straight on
            return (goal, 0, goal);
        }
        long beyond = 0;
        for (var i = 1; i < h.Route.Count; i++)
        {
            var leg = h.Route[i] - h.Route[i - 1];
            beyond += Len3(leg.X.Raw, leg.Y.Raw, 0);
        }
        return (h.Route[0], beyond, h.Route[^1]);
    }

    // HeliPlan plans helicopter u's route to goal round the walls (ground above top) into h.Route: A* over the cells of a window
    // round its cell and the goal's (8 neighbours in a fixed order, a diagonal only past two free sides, a step next to a wall
    // dearer; the open set pops by cost then cell, so the result is fixed), then the path pulled tight into waypoints that see
    // the next along a lane. The goal unreachable (walled in, on a wall, beyond the window or the budget): to the cell nearest it
    // that the search reached (ties to the lower index); beyond the window or the budget the route is partial.
    private void HeliPlan(Unit u, HeliState h, Vec goal, long top)
    {
        h.Route.Clear();
        (h.RouteGoal, h.RoutePartial) = (goal, false);
        var s = OnGrid(Cell.Of(u.Pos));
        var t = OnGrid(Cell.Of(goal));
        int dx = t.X - s.X, dy = t.Y - s.Y, far = Math.Max(Math.Abs(dx), Math.Abs(dy));
        const int reach = (HeliRouteWindow - 2 * HeliRouteMargin) / 2;
        var shrunk = far > reach;
        if (shrunk)
        {
            (t, far, h.RoutePartial) = (new Cell(s.X + dx * reach / far, s.Y + dy * reach / far), reach, true);
        }
        var m = HeliRouteMargin + far / 2;
        int x0 = Math.Max(0, Math.Min(s.X, t.X) - m), y0 = Math.Max(0, Math.Min(s.Y, t.Y) - m);
        int x1 = Math.Min(Grid.Width - 1, Math.Max(s.X, t.X) + m), y1 = Math.Min(Grid.Height - 1, Math.Max(s.Y, t.Y) + m);
        int w = x1 - x0 + 1, n = w * (y1 - y0 + 1);
        if (_hrG.Length < n)
        {
            (_hrG, _hrFrom, _hrSeen, _hrShut, _hrWallGen, _hrWall) = (new int[n], new int[n], new int[n], new int[n], new int[n], new bool[n]);
            _hrGen = 0;
        }
        if (++_hrGen == int.MaxValue)
        {
            Array.Clear(_hrSeen);
            Array.Clear(_hrShut);
            Array.Clear(_hrWallGen);
            _hrGen = 1;
        }
        var gen = _hrGen;
        int start = (s.Y - y0) * w + (s.X - x0), target = (t.Y - y0) * w + (t.X - x0);
        int tx = t.X - x0, ty = t.Y - y0;
        long Heur(int i)
        {
            int ax = Math.Abs(i % w - tx), ay = Math.Abs(i / w - ty);
            return 10L * Math.Max(ax, ay) + 4L * Math.Min(ax, ay); // octile, in step costs
        }
        bool Wall(int lx, int ly)
        {
            if (lx < 0 || ly < 0 || lx >= w || ly > y1 - y0)
            {
                return true; // outside the window: closed
            }
            var i = ly * w + lx;
            if (_hrWallGen[i] != gen)
            {
                (_hrWallGen[i], _hrWall[i]) = (gen, HeliWallCell(lx + x0, ly + y0, top));
            }
            return _hrWall[i];
        }
        bool NearWall(int lx, int ly)
        {
            for (var oy = -1; oy <= 1; oy++)
            {
                for (var ox = -1; ox <= 1; ox++)
                {
                    int ax = lx + ox, ay = ly + oy;
                    if ((ox != 0 || oy != 0) && ax >= 0 && ay >= 0 && ax < w && ay <= y1 - y0 && Wall(ax, ay))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        ReadOnlySpan<int> ddx = [1, 0, -1, 0, 1, -1, -1, 1], ddy = [0, 1, 0, -1, 1, 1, -1, -1];
        _hrOpen.Clear();
        (_hrSeen[start], _hrG[start], _hrFrom[start]) = (gen, 0, -1);
        _hrOpen.Push(Heur(start), start);
        var (best, bestH) = (start, Heur(start));
        var expanded = 0;
        while (_hrOpen.Count > 0)
        {
            var (_, i) = _hrOpen.Pop();
            if (_hrShut[i] == gen)
            {
                continue;
            }
            _hrShut[i] = gen;
            var hi = Heur(i);
            if (hi < bestH || (hi == bestH && i < best))
            {
                (best, bestH) = (i, hi);
            }
            if (i == target)
            {
                break;
            }
            if (++expanded > HeliRouteBudget)
            {
                h.RoutePartial = true;
                break;
            }
            int cx = i % w, cy = i / w;
            for (var k = 0; k < 8; k++)
            {
                int nx = cx + ddx[k], ny = cy + ddy[k];
                if (Wall(nx, ny) || (k >= 4 && (Wall(cx + ddx[k], cy) || Wall(cx, cy + ddy[k]))))
                {
                    continue;
                }
                var j = ny * w + nx;
                if (_hrShut[j] == gen)
                {
                    continue;
                }
                var g = _hrG[i] + (k >= 4 ? 14 : 10) + (NearWall(nx, ny) ? HeliRouteNearWall : 0);
                if (_hrSeen[j] != gen || g < _hrG[j])
                {
                    (_hrSeen[j], _hrG[j], _hrFrom[j]) = (gen, g, i);
                    _hrOpen.Push(g + Heur(j), j);
                }
            }
        }
        // The path, start to end, pulled tight: from each waypoint the farthest path cell (at most HeliPullMax on) it sees along
        // the lane is the next.
        _hrPath.Clear();
        for (var i = best; i >= 0; i = _hrFrom[i])
        {
            _hrPath.Add(i);
        }
        _hrPath.Reverse();
        Vec Centre(int i) => new Cell(i % w + x0, i / w + y0).Center();
        var reached = best == target && !shrunk;
        var anchor = u.Pos;
        var at = 0;
        while (at < _hrPath.Count - 1)
        {
            var j = at + 1;
            while (j + 1 < _hrPath.Count && j + 1 - at <= HeliPullMax && HeliClear(anchor, Centre(_hrPath[j + 1]), top, HeliLaneRaw))
            {
                j++;
            }
            anchor = Centre(_hrPath[j]);
            h.Route.Add(anchor);
            at = j;
        }
        if (reached)
        {
            // Its own goal, not the centre of the goal's cell: in place of the centre when it sees it from the waypoint before,
            // else after it (from the centre it is in the same free cell).
            var before = h.Route.Count > 1 ? h.Route[^2] : u.Pos;
            if (h.Route.Count > 0 && HeliClear(before, goal, top, 0))
            {
                h.Route[^1] = goal;
            }
            else
            {
                h.Route.Add(goal);
            }
        }
        else if (h.Route.Count == 0)
        {
            h.Route.Add(Centre(start)); // nowhere nearer: it holds over its own cell
        }
    }

    // OnGrid clamps a cell onto the map.
    private Cell OnGrid(Cell c) => new(Math.Clamp(c.X, 0, Grid.Width - 1), Math.Clamp(c.Y, 0, Grid.Height - 1));
}
