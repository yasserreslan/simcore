using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// HPA* for one navigation class: the map in 32 x 32-cell sectors, portals where a sector edge
/// is passable on both sides (both ends of every open stretch, every 8 cells along it, and the
/// middle of every road crossing: the road "highway"), and per sector the cost between each pair
/// of its portals (a Dijkstra inside the sector on the class's step costs, kept as its pruned edges). Sectors are built
/// lazily, on first use, and rebuilt when their cells change. Abstract nodes are numbered
/// sector * MaxNodes + local. Scratch derived from the map: not game state.
/// </summary>
internal sealed class Hpa
{
    internal const int S = 32;
    internal const int MaxNodes = 128;
    internal const int Unreachable = int.MaxValue;
    internal const int Spacing = 8;

    // An edge is one int: its cost above EdgeShift bits, the node at its other end (a local index, below MaxNodes) in them.
    // A cost inside a sector is at most 1,023 steps of 3,927 (a diagonal into a cost-255 cell, climbing at 1,000%):
    // under 2^22, so it always fits the 24 bits above the index.
    internal const int EdgeShift = 7;
    private const int EdgeMask = (1 << EdgeShift) - 1;

    /// <summary>The node at an edge's other end (a local index).</summary>
    internal static int EdgeNode(int edge) => edge & EdgeMask;

    /// <summary>An edge's cost.</summary>
    internal static int EdgeCost(int edge) => edge >> EdgeShift;

    private static int Edge(int node, int cost) => (cost << EdgeShift) | node;

    // A portal pair across a border: cell A in the lower sector (left or top), B in the higher.
    internal readonly record struct Portal(int A, int B, int CostAB, int CostBA);

    // A node of a sector: its cell, and where its portal is (border slot 0 left, 1 right, 2 top,
    // 3 bottom, and the index in that border's portal list).
    internal readonly record struct Node(int Cell, int Side, int Pos);

    internal sealed class Sector
    {
        public Node[] Nodes = [];
        public int[] OutStart = [];   // pruned edges from each node (CSR): Out[OutStart[i]..OutStart[i + 1]), each an Edge
        public int[] Out = [];
        public int[] InStart = [];    // pruned edges into each node: In[InStart[j]..InStart[j + 1]), the other end's index
        public int[] In = [];
        public int[] Partner = [];    // per node: the global node across its border (-1 none, -2 not looked up yet)
        public int[] PartnerCost = []; // per node: the cost of crossing to it
        public bool Built;
        public bool NodesReady;
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

    internal bool LegacyBorders { get; set; }

    // Border nodes have the same numbering before or after the expensive internal edge build.
    // Merely queuing a portal must not build the neighbouring sector's entire cost matrix.
    internal Node[] PortalNodes(int sector) => PortalData(sector).Nodes;

    private Sector PortalData(int sector)
    {
        if (LegacyBorders) { return Get(sector); }
        var s = _sectors[sector];
        if (!s.NodesReady)
        {
            s.Nodes = [.. Nodes(sector)];
            s.Partner = new int[s.Nodes.Length];
            s.PartnerCost = new int[s.Nodes.Length];
            Array.Fill(s.Partner, -2);
            s.NodesReady = true;
            // Rebuilding only the border header can renumber nodes just as a full
            // build does. Incoming partners two sectors from a change must be reset too.
            UnlinkNeighbours(sector);
        }
        return s;
    }

    /// <summary>Whether a sector is built (tests).</summary>
    internal bool IsBuilt(int sector) => _sectors[sector].Built;

    /// <summary>Sectors built so far (the warm-up's tests read it).</summary>
    internal int Builds { get; private set; }

    /// <summary>Cells expanded building sectors so far (the warm-up's budget).</summary>
    internal long BuildWork { get; private set; }

    /// <summary>The nodes, pruned edges and border portals of the sectors built so far (for benchmarks; not game state).</summary>
    internal (long Nodes, long Edges, long Portals) Counts()
    {
        long nodes = 0, edges = 0, portals = 0;
        foreach (var s in _sectors)
        {
            nodes += s.Nodes.Length;
            edges += s.Out.Length;
        }
        foreach (var list in _vert)
        {
            portals += list?.Count ?? 0;
        }
        foreach (var list in _horz)
        {
            portals += list?.Count ?? 0;
        }
        return (nodes, edges, portals);
    }

    /// <summary>The memory the graph holds, in bytes (array payloads, about: for benchmarks; not game state).</summary>
    internal long Bytes
    {
        get
        {
            long b = (long)_sectors.Length * 8 * 3; // the sector and border reference arrays
            foreach (var s in _sectors)
            {
                b += 12L * s.Nodes.Length + 4L * (s.OutStart.Length + s.Out.Length + s.InStart.Length + s.In.Length +
                    s.Partner.Length + s.PartnerCost.Length);
            }
            foreach (var list in _vert)
            {
                b += 16L * (list?.Capacity ?? 0);
            }
            foreach (var list in _horz)
            {
                b += 16L * (list?.Capacity ?? 0);
            }
            return b;
        }
    }

    /// <summary>
    /// The background warm-up (World.RunPaths): builds the sectors not built yet in order, a few of
    /// a sector's node searches at a time, until about budget cells were expanded (at least one
    /// search); returns the cells expanded, 0 when every sector is built. A sector counts as built
    /// only when all its searches are done, with the very matrix <see cref="Get"/> would build; a
    /// map change drops the one in progress, and a search that needs it builds it whole.
    /// </summary>
    internal long Warm(long budget)
    {
        long spent = 0;
        while (spent < budget)
        {
            if (_pending is null)
            {
                while (_warmFrom < _sectors.Length && _sectors[_warmFrom].Built)
                {
                    _warmFrom++;
                }
                if (_warmFrom == _sectors.Length)
                {
                    break;
                }
                var nodes = Nodes(_warmFrom);
                _pending = new Pending(_warmFrom, nodes, new int[nodes.Count * nodes.Count]);
            }
            var p = _pending;
            if (p.Row < p.Nodes.Count)
            {
                var work = _search.Expanded;
                Row(p.Sector, p.Nodes, p.Cost, p.Row++);
                spent += _search.Expanded - work;
                BuildWork += _search.Expanded - work;
                _search.Expanded = work;
            }
            if (p.Row == p.Nodes.Count)
            {
                _pending = null;
                Finish(p.Sector, p.Nodes, p.Cost);
                spent = Math.Max(spent, 1);
            }
        }
        return spent;
    }

    // The sector the warm-up is building: its nodes, and the cost rows done so far.
    private sealed class Pending(int sector, List<Node> nodes, int[] cost)
    {
        public readonly int Sector = sector;
        public readonly List<Node> Nodes = nodes;
        public readonly int[] Cost = cost;
        public int Row;
    }

    private Pending? _pending;

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
        var s = PortalData(sector);
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
        var nodes = PortalNodes(sector);
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
        _pending = null; // its borders may have changed
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
                    _sectors[i - 1].Built = _sectors[i - 1].NodesReady = false;
                }
                if (sy > 0)
                {
                    _horz[i - SX] = null;
                    _sectors[i - SX].Built = _sectors[i - SX].NodesReady = false;
                }
                _sectors[i].Built = _sectors[i].NodesReady = false;
                if (sx + 1 < SX)
                {
                    _sectors[i + 1].Built = _sectors[i + 1].NodesReady = false;
                }
                if (sy + 1 < SY)
                {
                    _sectors[i + SX].Built = _sectors[i + SX].NodesReady = false;
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
                // The middle of each road crossing gets its own portal: roads are the cheap highway (for soldiers an
                // alley and a footpath too).
                var r0 = -1;
                var foot = terrain.Rules.Classes[_nav.TerrainClass(_k)].EntersAlleys;
                for (var t = t0; t <= t1 + 1; t++)
                {
                    var onRoad = t <= t1 && OnRoad(terrain, foot, new Cell(ax + stepX * t, ay + stepY * t)) && OnRoad(terrain, foot, new Cell(bx + stepX * t, by + stepY * t));
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

    // OnRoad is whether a cell is road for a class: a vehicle road, or for the foot class an alley or a footpath.
    private static bool OnRoad(TerrainMap terrain, bool foot, Cell c) =>
        terrain.Road(c) || (foot && terrain.RoadKindAt(c) is RoadKind.Alley or RoadKind.Footpath);

    // Prune lists the edges of a sector's cost matrix c (n x n, from row to column; Unreachable if none) the searches
    // need: an edge i -> j is left out when some other node m lies on a path as cheap (both legs dearer than zero), so the
    // graph's distances stay the same with fewer edges to relax. Only the pruned edges are kept: the matrix (n squared
    // ints a sector and class, half the graphs' memory) is dropped once they are listed.
    private static void Prune(Sector s, int[] c, int n)
    {
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
        s.Out = new int[count];
        s.InStart = new int[n + 1];
        s.In = new int[count];
        var e = 0;
        for (var i = 0; i < n; i++)
        {
            s.OutStart[i] = e;
            for (var j = 0; j < n; j++)
            {
                if (keep[i * n + j])
                {
                    s.Out[e++] = Edge(j, c[i * n + j]);
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
                    s.In[e++] = Edge(i, c[i * n + j]);
                }
            }
        }
        s.InStart[n] = e;
    }

    private void Unlink(int sector)
    {
        if (_sectors[sector].NodesReady)
        {
            Array.Fill(_sectors[sector].Partner, -2);
        }
    }

    // Build gathers a sector's nodes from its four borders and the costs between them.
    private void Build(int sector)
    {
        if (_pending?.Sector == sector)
        {
            _pending = null; // a search needs it now: built whole
        }
        var work = _search.Expanded; // cache work: not counted (see Router.Work)
        var nodes = Nodes(sector);
        var n = nodes.Count;
        var cost = new int[n * n];
        for (var i = 0; i < n; i++)
        {
            Row(sector, nodes, cost, i);
        }
        BuildWork += _search.Expanded - work;
        _search.Expanded = work;
        Finish(sector, nodes, cost);
    }

    // Nodes lists a sector's nodes from its four borders (left, right, top, bottom).
    private List<Node> Nodes(int sector)
    {
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
        return nodes;
    }

    // Row fills row i of a sector's cost matrix: a Dijkstra inside the sector from node i.
    private void Row(int sector, List<Node> nodes, int[] cost, int i)
    {
        var n = nodes.Count;
        Span<int> cells = n <= 128 ? stackalloc int[n] : new int[n];
        for (var j = 0; j < n; j++)
        {
            cells[j] = nodes[j].Cell;
        }
        Span<(int, int)> seed = stackalloc (int, int)[1];
        seed[0] = (nodes[i].Cell, 0);
        _search.Dijkstra(_nav, _k, seed, Bounds(sector), reverse: false, cells);
        for (var j = 0; j < n; j++)
        {
            cost[i * n + j] = _search.Dist(nodes[j].Cell);
        }
    }

    // Finish stores a sector's nodes and cost matrix, prunes its edges and marks it built.
    private void Finish(int sector, List<Node> nodes, int[] cost)
    {
        var n = nodes.Count;
        var s = _sectors[sector];
        s.Nodes = [.. nodes];
        Prune(s, cost, n);
        s.Partner = new int[n];
        s.PartnerCost = new int[n];
        Array.Fill(s.Partner, -2);
        s.Built = s.NodesReady = true;
        Builds++;
        // The neighbours' links into this sector may point at other local indices now.
        UnlinkNeighbours(sector);
    }

    private void UnlinkNeighbours(int sector)
    {
        int sx = sector % SX, sy = sector / SX;
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
    }
}
