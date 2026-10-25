namespace SimCore.Sim;

/// <summary>
/// A binary min-heap of (key, value) pairs, ordered by key then value: a total order, so any
/// sequence of pushes pops in one fixed order. Scratch, not game state.
/// </summary>
internal sealed class MinHeap
{
    private long[] _key = new long[256];
    private int[] _val = new int[256];

    internal int Count { get; private set; }

    internal void Clear() => Count = 0;

    internal void Push(long key, int val)
    {
        if (Count == _key.Length)
        {
            Array.Resize(ref _key, Count * 2);
            Array.Resize(ref _val, Count * 2);
        }
        var i = Count++;
        while (i > 0)
        {
            var p = (i - 1) >> 1;
            if (_key[p] < key || (_key[p] == key && _val[p] < val))
            {
                break;
            }
            _key[i] = _key[p];
            _val[i] = _val[p];
            i = p;
        }
        _key[i] = key;
        _val[i] = val;
    }

    internal (long Key, int Val) Pop()
    {
        var top = (_key[0], _val[0]);
        var n = --Count;
        if (n > 0)
        {
            long k = _key[n];
            var v = _val[n];
            var i = 0;
            while (true)
            {
                var c = 2 * i + 1;
                if (c >= n)
                {
                    break;
                }
                if (c + 1 < n && (_key[c + 1] < _key[c] || (_key[c + 1] == _key[c] && _val[c + 1] < _val[c])))
                {
                    c++;
                }
                if (k < _key[c] || (k == _key[c] && v < _val[c]))
                {
                    break;
                }
                _key[i] = _key[c];
                _val[i] = _val[c];
                i = c;
            }
            _key[i] = k;
            _val[i] = v;
        }
        return top;
    }
}

/// <summary>
/// Cell searches on a <see cref="NavGrid"/> class, inside a rectangle: A* between two cells and
/// Dijkstra from seeded cells (forward: cost from the seeds; reverse: cost to them). Buffers are
/// indexed inside the rectangle and reused; a generation counter marks the current search.
/// Neighbours are visited in a fixed order and the open set pops by (cost, cell), so results
/// are deterministic. Scratch, not game state.
/// </summary>
internal sealed class LocalSearch
{
    internal const int Unreachable = int.MaxValue;

    // The eight neighbours in a fixed order.
    internal static readonly int[] Dx = [0, 1, 1, 1, 0, -1, -1, -1];
    internal static readonly int[] Dy = [-1, -1, 0, 1, 1, 1, 0, -1];

    private int[] _g = [];
    private int[] _parent = [];
    private uint[] _seen = [];
    private uint[] _closed = [];
    private uint[] _stop = [];
    private uint _gen;
    private readonly MinHeap _open = new();
    private int _x0, _y0, _x1, _y1, _bw, _w;

    /// <summary>Cells expanded since the last reset of the counter (a deterministic work measure).</summary>
    internal long Expanded { get; set; }

    /// <summary>The memory the search buffers hold, in bytes (for benchmarks; not game state).</summary>
    internal long Bytes => 20L * _g.Length;

    private void Begin(NavGrid nav, (int X0, int Y0, int X1, int Y1) r)
    {
        _x0 = Math.Max(r.X0, 0);
        _y0 = Math.Max(r.Y0, 0);
        _x1 = Math.Min(r.X1, nav.W - 1);
        _y1 = Math.Min(r.Y1, nav.H - 1);
        _bw = _x1 - _x0 + 1;
        _w = nav.W;
        var n = Math.Max(_bw * (_y1 - _y0 + 1), 1);
        if (_g.Length < n)
        {
            var size = Math.Max(n, _g.Length * 2);
            _g = new int[size];
            _parent = new int[size];
            _seen = new uint[size];
            _closed = new uint[size];
            _stop = new uint[size];
            _gen = 0;
        }
        _gen++;
        if (_gen == 0)
        {
            Array.Clear(_seen);
            Array.Clear(_closed);
            Array.Clear(_stop);
            _gen = 1;
        }
        _open.Clear();
    }

    // The search's class: its cost grid, heights and climbing percent, for FastStep.
    private byte[] _cost = [];
    private int[]? _centre;
    private int _uphill;

    private void Use(NavGrid nav, int k)
    {
        _cost = nav.CostGrid(k);
        _centre = nav.Centre;
        _uphill = nav.Uphill(k);
    }

    // FastStep is NavGrid.Step from cell a = (x, y) to its neighbour b in direction d, for cells
    // known to be on the map with the diagonal's two corner cells too (both inside the rectangle).
    private int FastStep(int a, int x, int y, int d)
    {
        int dx = Dx[d], dy = Dy[d], b = a + dy * _w + dx;
        int c = _cost[b];
        if (c == 0)
        {
            return 0;
        }
        int s;
        if (dx != 0 && dy != 0)
        {
            if (_cost[a + dx] == 0 || _cost[a + dy * _w] == 0)
            {
                return 0;
            }
            s = c * NavGrid.Diagonal / 10;
        }
        else
        {
            s = c;
        }
        if (_centre is not null && _centre[b] > _centre[a])
        {
            s += s * _uphill / 100;
        }
        return s > 0 ? s : 1;
    }

    private bool In(int x, int y) => x >= _x0 && y >= _y0 && x <= _x1 && y <= _y1;

    private int Local(int x, int y) => (y - _y0) * _bw + (x - _x0);

    /// <summary>The octile distance between two cells, in step-cost units (10 straight, 14 diagonal).</summary>
    internal static int Octile(int ax, int ay, int bx, int by)
    {
        int dx = Math.Abs(ax - bx), dy = Math.Abs(ay - by);
        return NavGrid.Straight * (dx + dy) + (NavGrid.Diagonal - 2 * NavGrid.Straight) * Math.Min(dx, dy);
    }

    /// <summary>
    /// Dijkstra inside a rectangle from seed cells with starting costs. Forward, the costs are of
    /// paths from the seeds; reverse, of paths to them (a flow field toward the seeds). Read the
    /// results with <see cref="Dist"/> until the next search.
    /// </summary>
    internal void Dijkstra(NavGrid nav, int k, ReadOnlySpan<(int Cell, int Cost)> seeds, (int X0, int Y0, int X1, int Y1) r, bool reverse,
        ReadOnlySpan<int> stop = default)
    {
        Begin(nav, r);
        Use(nav, k);
        // With stop cells, it ends once they are all settled (the rest keep no cost).
        var left = 0;
        foreach (var c in stop)
        {
            int x = c % _w, y = c / _w;
            if (In(x, y) && _stop[Local(x, y)] != _gen)
            {
                _stop[Local(x, y)] = _gen;
                left++;
            }
        }
        var stopping = left > 0;
        // A bucket queue (Dial): every step costs less than the ring, so the costs waiting are
        // all within one ring's span of the current one. Seeds join, cheapest first, as the
        // current cost comes within a ring of theirs.
        _seeds.Clear();
        foreach (var (cell, cost) in seeds)
        {
            int x = cell % _w, y = cell / _w;
            if (In(x, y) && _cost[cell] != 0)
            {
                _seeds.Add(((long)cost << 32) | (uint)Local(x, y));
            }
        }
        _seeds.Sort();
        Array.Fill(_head, -1);
        _entries = 0;
        var live = 0;
        var si = 0;
        long cur = _seeds.Count > 0 ? _seeds[0] >> 32 : 0;
        while (true)
        {
            while (si < _seeds.Count && (_seeds[si] >> 32) < cur + Ring)
            {
                var li = (int)(_seeds[si] & 0xFFFFFFFF);
                var cost = (int)(_seeds[si++] >> 32);
                if (_seen[li] != _gen || cost < _g[li])
                {
                    _seen[li] = _gen;
                    _g[li] = cost;
                    Put(cost, li);
                    live++;
                }
            }
            if (live == 0)
            {
                if (si >= _seeds.Count)
                {
                    break;
                }
                cur = _seeds[si] >> 32;
                continue;
            }
            var bucket = (int)(cur & (Ring - 1));
            var e = _head[bucket];
            if (e < 0)
            {
                cur++;
                continue;
            }
            _head[bucket] = _next[e];
            live--;
            var lc = _entry[e];
            if (_closed[lc] == _gen || _g[lc] != cur)
            {
                continue;
            }
            _closed[lc] = _gen;
            Expanded++;
            if (stopping && _stop[lc] == _gen && --left == 0)
            {
                break;
            }
            int x = _x0 + lc % _bw, y = _y0 + lc / _bw;
            var cell = y * _w + x;
            for (var d = 0; d < 8; d++)
            {
                int nx = x + Dx[d], ny = y + Dy[d];
                if (!In(nx, ny))
                {
                    continue;
                }
                var ni = Local(nx, ny);
                if (_closed[ni] == _gen)
                {
                    continue;
                }
                var step = reverse ? FastStep(cell + Dy[d] * _w + Dx[d], nx, ny, (d + 4) & 7) : FastStep(cell, x, y, d);
                if (step == 0)
                {
                    continue;
                }
                var ng = (int)cur + step;
                if (_seen[ni] == _gen && ng >= _g[ni])
                {
                    continue;
                }
                _seen[ni] = _gen;
                _g[ni] = ng;
                Put(ng, ni);
                live++;
            }
        }
    }

    // The bucket queue: Ring buckets of entries (a cell each), linked through _next.
    private const int Ring = 4096; // more than any step (255 x 14 / 10, plus a climb of up to 1,000%)
    private readonly int[] _head = new int[Ring];
    private int[] _entry = new int[1024];
    private int[] _next = new int[1024];
    private int _entries;
    private readonly List<long> _seeds = [];

    private void Put(long cost, int li)
    {
        if (_entries == _entry.Length)
        {
            Array.Resize(ref _entry, _entries * 2);
            Array.Resize(ref _next, _entries * 2);
        }
        var b = (int)(cost & (Ring - 1));
        _entry[_entries] = li;
        _next[_entries] = _head[b];
        _head[b] = _entries++;
    }

    /// <summary>The cost the last Dijkstra found for a cell, or <see cref="Unreachable"/>.</summary>
    internal int Dist(int cell)
    {
        int x = cell % _w, y = cell / _w;
        if (!In(x, y))
        {
            return Unreachable;
        }
        var li = Local(x, y);
        return _closed[li] == _gen ? _g[li] : Unreachable;
    }

    /// <summary>
    /// A* from one cell to another inside a rectangle (8-way, no corner cutting, the class's step
    /// costs). Appends the cells after start up to goal to path, or, when goal can't be reached,
    /// up to the reached cell nearest to it (then returns false). Returns the path cost in cost.
    /// </summary>
    internal bool AStar(NavGrid nav, int k, int start, int goal, (int X0, int Y0, int X1, int Y1) r, List<int> path, out int cost, ReadOnlySpan<byte> penalty = default)
    {
        cost = 0;
        Begin(nav, r);
        Use(nav, k);
        int gx = goal % _w, gy = goal / _w, sx = start % _w, sy = start / _w;
        if (!In(sx, sy))
        {
            return false;
        }
        var hScale = nav.MinCost(k);
        var s = Local(sx, sy);
        _g[s] = 0;
        _parent[s] = -1;
        _seen[s] = _gen;
        var h0 = Octile(sx, sy, gx, gy) * hScale / NavGrid.Straight;
        _open.Push(((long)h0 << 32) | (uint)h0, s);
        int best = s, bestH = h0;
        var goalLocal = In(gx, gy) ? Local(gx, gy) : -1;
        var reached = false;
        while (_open.Count > 0)
        {
            var (key, li) = _open.Pop();
            if (_closed[li] == _gen)
            {
                continue;
            }
            _closed[li] = _gen;
            Expanded++;
            if (li == goalLocal)
            {
                best = li;
                reached = true;
                break;
            }
            var h = (int)(key & 0xFFFFFFFF);
            if (h < bestH)
            {
                best = li;
                bestH = h;
            }
            int x = _x0 + li % _bw, y = _y0 + li / _bw;
            var g = _g[li];
            for (var d = 0; d < 8; d++)
            {
                int nx = x + Dx[d], ny = y + Dy[d];
                if (!In(nx, ny))
                {
                    continue;
                }
                var ni = Local(nx, ny);
                if (_closed[ni] == _gen)
                {
                    continue;
                }
                var step = FastStep(y * _w + x, x, y, d);
                if (step == 0)
                {
                    continue;
                }
                if (!penalty.IsEmpty)
                {
                    if (penalty[ni] == byte.MaxValue ||
                        (Dx[d] != 0 && Dy[d] != 0 &&
                         (penalty[Local(nx, y)] == byte.MaxValue || penalty[Local(x, ny)] == byte.MaxValue)))
                    {
                        continue;
                    }
                    step += penalty[ni];
                }
                var ng = g + step;
                if (_seen[ni] == _gen && ng >= _g[ni])
                {
                    continue;
                }
                _seen[ni] = _gen;
                _g[ni] = ng;
                _parent[ni] = li;
                var nh = Octile(nx, ny, gx, gy) * hScale / NavGrid.Straight;
                _open.Push(((long)(ng + nh) << 32) | (uint)nh, ni);
            }
        }
        cost = _g[best];
        var from = path.Count;
        for (var i = best; i != s; i = _parent[i])
        {
            path.Add((_y0 + i / _bw) * _w + _x0 + i % _bw);
        }
        path.Reverse(from, path.Count - from);
        return reached;
    }
}
