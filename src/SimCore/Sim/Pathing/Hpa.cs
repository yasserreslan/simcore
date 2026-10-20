namespace SimCore.Sim;

/// <summary>
/// HPA* for one navigation class: the map in 32 x 32-cell sectors, portals where a sector edge
/// is passable on both sides (both ends of every open stretch, every 8 cells along it, and the
/// middle of every road crossing: the road "highway"), and per sector the cost between each pair
/// of its portals (a Dijkstra inside the sector on the class's step costs). Sectors are built
/// lazily, on first use, and rebuilt when their cells change. Abstract nodes are numbered
/// sector * MaxNodes + local. Scratch derived from the map: not game state.
/// </summary>
internal sealed class Hpa
{
    internal const int S = 32;
    internal const int MaxNodes = 128;
    internal const int Unreachable = int.MaxValue;
    internal const int Spacing = 8;

    // A portal pair across a border: cell A in the lower sector (left or top), B in the higher.
    internal readonly record struct Portal(int A, int B, int CostAB, int CostBA);

    // A node of a sector: its cell, and where its portal is (border slot 0 left, 1 right, 2 top,
    // 3 bottom, and the index in that border's portal list).
    internal readonly record struct Node(int Cell, int Side, int Pos);

    internal sealed class Sector
    {
        public Node[] Nodes = [];
        public int[] Cost = [];       // Nodes.Length squared: from row to column; Unreachable if none
        public int[] OutStart = [];   // pruned edges from each node (CSR): OutTo/OutCost[OutStart[i]..OutStart[i + 1])
        public int[] OutTo = [];
        public int[] OutCost = [];
        public int[] InStart = [];    // pruned edges into each node
        public int[] InFrom = [];
        public int[] InCost = [];
        public int[] Partner = [];    // per node: the global node across its border (-1 none, -2 not looked up yet)
        public int[] PartnerCost = []; // per node: the cost of crossing to it
        public bool Built;
    }

    private readonly NavGrid _nav;
    private readonly int _k;
    private readonly List<Portal>?[] _vert;  // between (sx, sy) and (sx + 1, sy)
    private readonly List<Portal>?[] _horz;  // between (sx, sy) and (sx, sy + 1)
    private readonly Sector[] _sectors;
    private readonly LocalSearch _search;
    private int _warmFrom; // every sector below this is built (invalidation sends it back to 0)

    internal Hpa(NavGrid nav, int k, LocalSearch search)
    {
        _nav = nav;
        _k = k;
        _search = search;
        SX = (nav.W + S - 1) / S;
        SY = (nav.H + S - 1) / S;
        _vert = new List<Portal>?[SX * SY];
        _horz = new List<Portal>?[SX * SY];
        _sectors = new Sector[SX * SY];
        for (var i = 0; i < _sectors.Length; i++)
        {
            _sectors[i] = new Sector();
        }
    }

    internal int SX { get; }

    internal int SY { get; }

    internal int Class => _k;

    /// <summary>Whether a sector is built (tests).</summary>
    internal bool IsBuilt(int sector) => _sectors[sector].Built;

    /// <summary>Sectors built so far (the warm-up's tests read it).</summary>
    internal int Builds { get; private set; }

    /// <summary>Cells expanded building sectors so far (the warm-up's budget).</summary>
    internal long BuildWork { get; private set; }

    /// <summary>The most cells one sector's build expanded so far (tests).</summary>
    internal long MaxBuildWork { get; private set; }

    /// <summary>
    /// Builds the first sector not built yet (the background warm-up, World.RunPaths); false when
    /// every sector is built.
    /// </summary>
    internal bool WarmOne()
    {
        while (_warmFrom < _sectors.Length && _sectors[_warmFrom].Built)
        {
            _warmFrom++;
        }
        if (_warmFrom == _sectors.Length)
        {
            return false;
        }
        Build(_warmFrom);
        return true;
    }

    internal NavGrid Nav => _nav;

    internal int SectorOf(int cell) => (cell / _nav.W / S) * SX + (cell % _nav.W) / S;

    internal (int X0, int Y0, int X1, int Y1) Bounds(int sector)
    {
        int sx = sector % SX, sy = sector / SX;
        return (sx * S, sy * S, Math.Min(sx * S + S, _nav.W) - 1, Math.Min(sy * S + S, _nav.H) - 1);
    }

    /// <summary>A sector, built if needed.</summary>
    internal Sector Get(int sector)
    {
        var s = _sectors[sector];
        if (!s.Built)
        {
            Build(sector);
        }
        return s;
    }

    /// <summary>The partner of node (sector, local) across its border, with the crossing cost, or (-1, 0).</summary>
    internal (int Node, int Cost) Partner(int sector, int local)
    {
        var s = Get(sector);
        if (s.Partner[local] == -2)
        {
            var n = s.Nodes[local];
            int sx = sector % SX, sy = sector / SX;
            (int other, int side, Portal p) = n.Side switch
            {
                0 => (sector - 1, 1, Border(_vert, sx - 1, sy, true)[n.Pos]),
                1 => (sector + 1, 0, Border(_vert, sx, sy, true)[n.Pos]),
                2 => (sector - SX, 3, Border(_horz, sx, sy - 1, false)[n.Pos]),
                _ => (sector + SX, 2, Border(_horz, sx, sy, false)[n.Pos]),
            };
            s.Partner[local] = Find(other, side, n.Pos);
            s.PartnerCost[local] = n.Side is 0 or 2 ? p.CostBA : p.CostAB;
        }
        return (s.Partner[local], s.PartnerCost[local]);
    }

    // Find: the global node id of a sector's node on a given side and portal position.
    private int Find(int sector, int side, int pos)
    {
        var nodes = Get(sector).Nodes;
        for (var i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Side == side && nodes[i].Pos == pos)
            {
                return sector * MaxNodes + i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Forgets what a rectangle of changed cells touches: the borders of every sector overlapping
    /// it, and those sectors and their neighbours (whose node lists come from those borders),
    /// rebuilt on next use.
    /// </summary>
    internal void Invalidate(int x0, int y0, int x1, int y1)
    {
        _warmFrom = 0;
        int sx0 = Math.Max(x0 / S, 0), sy0 = Math.Max(y0 / S, 0), sx1 = Math.Min(x1 / S, SX - 1), sy1 = Math.Min(y1 / S, SY - 1);
        for (var sy = sy0; sy <= sy1; sy++)
        {
            for (var sx = sx0; sx <= sx1; sx++)
            {
                var i = sy * SX + sx;
                _vert[i] = null;
                _horz[i] = null;
                if (sx > 0)
                {
                    _vert[i - 1] = null;
                    _sectors[i - 1].Built = false;
                }
                if (sy > 0)
                {
                    _horz[i - SX] = null;
                    _sectors[i - SX].Built = false;
                }
                _sectors[i].Built = false;
                if (sx + 1 < SX)
                {
                    _sectors[i + 1].Built = false;
                }
                if (sy + 1 < SY)
                {
                    _sectors[i + SX].Built = false;
                }
            }
        }
    }

    /// <summary>Builds every sector now (before the first tick, so no order pays for it).</summary>
    internal void BuildAll()
    {
        for (var i = 0; i < _sectors.Length; i++)
        {
            Get(i);
        }
    }

    // Border returns the portal list of a border, scanning it if needed.
    private List<Portal> Border(List<Portal>?[] store, int sx, int sy, bool vertical)
    {
        var i = sy * SX + sx;
        return store[i] ??= Scan(sx, sy, vertical);
    }

    // Scan finds the open stretches along a border and places its portals.
    private List<Portal> Scan(int sx, int sy, bool vertical)
    {
        var list = new List<Portal>();
        var w = _nav.W;
        int len, ax, ay, bx, by, stepX, stepY;
        if (vertical)
        {
            ax = sx * S + S - 1;
            ay = sy * S;
            bx = ax + 1;
            by = ay;
            len = Math.Min(S, _nav.H - ay);
            (stepX, stepY) = (0, 1);
            if (bx >= _nav.W)
            {
                return list;
            }
        }
        else
        {
            ax = sx * S;
            ay = sy * S + S - 1;
            bx = ax;
            by = ay + 1;
            len = Math.Min(S, _nav.W - ax);
            (stepX, stepY) = (1, 0);
            if (by >= _nav.H)
            {
                return list;
            }
        }
        var open = new bool[len];
        for (var t = 0; t < len; t++)
        {
            open[t] = _nav.Cost(_k, ax + stepX * t, ay + stepY * t) != 0 && _nav.Cost(_k, bx + stepX * t, by + stepY * t) != 0;
        }
        var t0 = 0;
        while (t0 < len)
        {
            if (!open[t0])
            {
                t0++;
                continue;
            }
            var t1 = t0;
            while (t1 + 1 < len && open[t1 + 1])
            {
                t1++;
            }
            var picks = new List<int>();
            if (t1 - t0 < 2)
            {
                picks.Add((t0 + t1) / 2);
            }
            else
            {
                for (var t = t0; t < t1; t += Spacing)
                {
                    picks.Add(t);
                }
                picks.Add(t1);
            }
            if (_nav.Terrain is { } terrain)
            {
                // The middle of each road crossing gets its own portal: roads are the cheap highway.
                var r0 = -1;
                for (var t = t0; t <= t1 + 1; t++)
                {
                    var onRoad = t <= t1 && terrain.Road(new Cell(ax + stepX * t, ay + stepY * t)) && terrain.Road(new Cell(bx + stepX * t, by + stepY * t));
                    if (onRoad && r0 < 0)
                    {
                        r0 = t;
                    }
                    else if (!onRoad && r0 >= 0)
                    {
                        var mid = (r0 + t - 1) / 2;
                        if (!picks.Contains(mid))
                        {
                            picks.Add(mid);
                        }
                        r0 = -1;
                    }
                }
                picks.Sort();
            }
            foreach (var t in picks)
            {
                int cax = ax + stepX * t, cay = ay + stepY * t, cbx = bx + stepX * t, cby = by + stepY * t;
                list.Add(new Portal(cay * w + cax, cby * w + cbx,
                    _nav.Step(_k, cax, cay, cbx - cax, cby - cay), _nav.Step(_k, cbx, cby, cax - cbx, cay - cby)));
            }
            t0 = t1 + 1;
        }
        return list;
    }

    // Prune lists the edges of a sector's cost matrix the searches need: an edge i -> j is left
    // out when some other node m lies on a path as cheap (both legs dearer than zero), so the
    // graph's distances stay the same with fewer edges to relax.
    private static void Prune(Sector s, int n)
    {
        var c = s.Cost;
        var keep = new bool[n * n];
        var count = 0;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                var cij = c[i * n + j];
                if (i == j || cij == Unreachable)
                {
                    continue;
                }
                var redundant = false;
                for (var m = 0; m < n && !redundant; m++)
                {
                    int cim = c[i * n + m], cmj = c[m * n + j];
                    redundant = m != i && m != j && cim != Unreachable && cmj != Unreachable && cim > 0 && cmj > 0 && cim + cmj == cij;
                }
                if (!redundant)
                {
                    keep[i * n + j] = true;
                    count++;
                }
            }
        }
        s.OutStart = new int[n + 1];
        s.OutTo = new int[count];
        s.OutCost = new int[count];
        s.InStart = new int[n + 1];
        s.InFrom = new int[count];
        s.InCost = new int[count];
        var e = 0;
        for (var i = 0; i < n; i++)
        {
            s.OutStart[i] = e;
            for (var j = 0; j < n; j++)
            {
                if (keep[i * n + j])
                {
                    s.OutTo[e] = j;
                    s.OutCost[e++] = c[i * n + j];
                }
            }
        }
        s.OutStart[n] = e;
        e = 0;
        for (var j = 0; j < n; j++)
        {
            s.InStart[j] = e;
            for (var i = 0; i < n; i++)
            {
                if (keep[i * n + j])
                {
                    s.InFrom[e] = i;
                    s.InCost[e++] = c[i * n + j];
                }
            }
        }
        s.InStart[n] = e;
    }

    private void Unlink(int sector)
    {
        if (_sectors[sector].Built)
        {
            Array.Fill(_sectors[sector].Partner, -2);
        }
    }

    // Build gathers a sector's nodes from its four borders and the costs between them.
    private void Build(int sector)
    {
        var work = _search.Expanded; // cache work: not counted (see Router.Work)
        int sx = sector % SX, sy = sector / SX;
        var nodes = new List<Node>();
        if (sx > 0)
        {
            var b = Border(_vert, sx - 1, sy, true);
            for (var i = 0; i < b.Count; i++)
            {
                nodes.Add(new Node(b[i].B, 0, i));
            }
        }
        if (sx + 1 < SX)
        {
            var b = Border(_vert, sx, sy, true);
            for (var i = 0; i < b.Count; i++)
            {
                nodes.Add(new Node(b[i].A, 1, i));
            }
        }
        if (sy > 0)
        {
            var b = Border(_horz, sx, sy - 1, false);
            for (var i = 0; i < b.Count; i++)
            {
                nodes.Add(new Node(b[i].B, 2, i));
            }
        }
        if (sy + 1 < SY)
        {
            var b = Border(_horz, sx, sy, false);
            for (var i = 0; i < b.Count; i++)
            {
                nodes.Add(new Node(b[i].A, 3, i));
            }
        }
        if (nodes.Count > MaxNodes)
        {
            nodes.RemoveRange(MaxNodes, nodes.Count - MaxNodes); // never on real maps: at most 16 stretches a border
        }
        var n = nodes.Count;
        var cost = new int[n * n];
        var bounds = Bounds(sector);
        Span<(int, int)> seed = stackalloc (int, int)[1];
        var cells = new int[n];
        for (var i = 0; i < n; i++)
        {
            cells[i] = nodes[i].Cell;
        }
        for (var i = 0; i < n; i++)
        {
            seed[0] = (nodes[i].Cell, 0);
            _search.Dijkstra(_nav, _k, seed, bounds, reverse: false, cells);
            for (var j = 0; j < n; j++)
            {
                cost[i * n + j] = _search.Dist(nodes[j].Cell);
            }
        }
        var s = _sectors[sector];
        s.Nodes = [.. nodes];
        s.Cost = cost;
        Prune(s, n);
        s.Partner = new int[n];
        s.PartnerCost = new int[n];
        Array.Fill(s.Partner, -2);
        s.Built = true;
        // The neighbours' links into this sector may point at other local indices now.
        if (sx > 0)
        {
            Unlink(sector - 1);
        }
        if (sx + 1 < SX)
        {
            Unlink(sector + 1);
        }
        if (sy > 0)
        {
            Unlink(sector - SX);
        }
        if (sy + 1 < SY)
        {
            Unlink(sector + SX);
        }
        Builds++;
        BuildWork += _search.Expanded - work;
        MaxBuildWork = Math.Max(MaxBuildWork, _search.Expanded - work);
        _search.Expanded = work;
    }
}
