using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// What routing works on: per navigation class, one cost byte a cell (0 = impassable, open flat
/// ground 10), with the step cost of moving between neighbours. A navigation class is a terrain
/// movement class (from the map's <see cref="TerrainMap"/>, or one uniform class on a flat map
/// without terrain) combined with a clearance width: a unit of radius r needs w = ceil(2r) free
/// cells side by side (infantry 1, the technical 2, the heavy trucks 3), and a cell is passable for
/// width w when the w x w block around it is free (odd w centred on it, even w with it at the top
/// left of the centre). Built when routing starts; cells are refreshed when the map grid or the
/// terrain changes (buildings, pads, forest), and the touched sectors reported for the HPA* rebuild. An alley (foot only)
/// costs 0 in every class but foot's, so no route for a vehicle enters one.
/// Scratch for routing, derived from the map: not game state, not hashed.
/// </summary>
internal sealed class NavGrid
{
    /// <summary>Straight and diagonal step weights (x cost / 10).</summary>
    internal const int Straight = 10, Diagonal = 14;

    private readonly Grid _grid;
    private readonly TerrainMap? _terrain;
    private readonly List<byte[]> _cost = [];       // per nav class
    private readonly List<int> _terrainClass = [];  // per nav class
    private readonly List<int> _width = [];         // per nav class
    private readonly List<bool> _noRoads = [];      // per nav class: roads cost as the ground beside them (crowds)
    private readonly List<int> _uphill = [];        // per nav class: extra percent for a climbing step
    private readonly List<int> _minCost = [];       // per nav class: the cheapest passable cell (for an admissible heuristic)
    private readonly byte[] _free;                  // the largest free square with this cell at its top left (capped)
    private readonly byte[] _occupied;              // structures standing on the cell (hard boxes, not in the grid)
    private readonly int[]? _centre;                // per cell: centre height (raw, sum of the 4 corners): the terrain's own array

    internal NavGrid(Grid grid, TerrainMap? terrain)
    {
        _grid = grid;
        _terrain = terrain;
        W = grid.Width;
        H = grid.Height;
        _free = new byte[W * H];
        _occupied = new byte[W * H];
        _centre = terrain?.CentreSums; // (the terrain keeps it current; every search runs after routing caught up with it)
        RebuildFree(0, 0, W - 1, H - 1);
    }

    /// <summary>The widest clearance block (cells) a class can have; wider units use it.</summary>
    internal const int MaxWidth = 5;

    /// <summary>
    /// The navigation class for a terrain movement class (ignored without terrain) and a clearance
    /// width, added on first use. Indices are handles only: no result depends on them.
    /// </summary>
    internal int Class(int terrainClass, int width, bool noRoads = false)
    {
        width = Math.Clamp(width, 1, MaxWidth);
        if (_terrain is null)
        {
            terrainClass = 0;
            noRoads = false;
        }
        for (var k = 0; k < _cost.Count; k++)
        {
            if (_terrainClass[k] == terrainClass && _width[k] == width && _noRoads[k] == noRoads)
            {
                return k;
            }
        }
        var n = _cost.Count;
        _cost.Add(new byte[W * H]);
        _terrainClass.Add(terrainClass);
        _width.Add(width);
        _noRoads.Add(noRoads);
        // Climbing adds at most 1,000% (the searches' bucket queue holds steps below 4,096).
        _uphill.Add(_terrain is null ? 0 : Math.Clamp(_terrain.Rules.Classes[terrainClass].UphillCostPercent, 0, 1000));
        _minCost.Add(0);
        var cost = _cost[n];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                cost[y * W + x] = CellCost(n, x, y);
            }
        }
        UpdateMinCost(n);
        return n;
    }

    internal TerrainMap? Terrain => _terrain;

    internal int W { get; }

    internal int H { get; }

    internal int Classes => _cost.Count;

    /// <summary>The memory the grids hold, in bytes (for benchmarks; not game state).</summary>
    internal long Bytes => _free.Length + _occupied.Length + (long)_cost.Count * _free.Length; // (the centre heights are the terrain's)

    /// <summary>The terrain movement class of a navigation class.</summary>
    internal int TerrainClass(int k) => _terrainClass[k];

    /// <summary>Whether a navigation class costs roads as the ground beside them.</summary>
    internal bool NoRoads(int k) => _noRoads[k];

    /// <summary>The clearance width (cells) of a navigation class.</summary>
    internal int Width(int k) => _width[k];

    /// <summary>The cheapest cell cost of a class (an admissible heuristic multiplies distances by it).</summary>
    internal int MinCost(int k) => _minCost[k];

    internal bool Contains(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;

    /// <summary>The cost of entering a cell (0 = impassable; off the map impassable).</summary>
    internal int Cost(int k, int x, int y) => Contains(x, y) ? _cost[k][y * W + x] : 0;

    internal int Cost(int k, int idx) => _cost[k][idx];

    /// <summary>The cost grid of a class, row by row (for the searches' inner loops).</summary>
    internal byte[] CostGrid(int k) => _cost[k];

    /// <summary>The cells' centre heights (raw, sum of the 4 corners), or null on a flat map.</summary>
    internal int[]? Centre => _centre;

    /// <summary>The extra percent a class pays for a climbing step.</summary>
    internal int Uphill(int k) => _uphill[k];

    /// <summary>
    /// The cost of a step from (x, y) to its neighbour (x + dx, y + dy): the target's cost times
    /// 10 or 14 over 10, plus the class's climbing percent going up; 0 if it can't step there
    /// (impassable, or a diagonal that cuts a corner).
    /// </summary>
    internal int Step(int k, int x, int y, int dx, int dy)
    {
        int nx = x + dx, ny = y + dy;
        var c = Cost(k, nx, ny);
        if (c == 0)
        {
            return 0;
        }
        var diag = dx != 0 && dy != 0;
        if (diag && (Cost(k, nx, y) == 0 || Cost(k, x, ny) == 0))
        {
            return 0;
        }
        var s = c * (diag ? Diagonal : Straight) / 10;
        if (_centre is not null && _centre[ny * W + nx] > _centre[y * W + x])
        {
            s += s * _uphill[k] / 100;
        }
        return Math.Max(s, 1);
    }

    /// <summary>
    /// Counts a structure's footprint on (delta 1) or off (-1) its cells: they are blocked for
    /// routing while any structure stands there. Call <see cref="Refresh"/> on it after.
    /// </summary>
    internal void Occupy(int x0, int y0, int w, int h, int delta)
    {
        for (var y = Math.Max(y0, 0); y < Math.Min(y0 + h, H); y++)
        {
            for (var x = Math.Max(x0, 0); x < Math.Min(x0 + w, W); x++)
            {
                _occupied[y * W + x] = (byte)Math.Clamp(_occupied[y * W + x] + delta, 0, 255);
            }
        }
    }

    /// <summary>
    /// Refreshes the cells around a rectangle of changed cells (the grid, the terrain or the
    /// structures changed there) and returns the rectangle of cells whose cost may have changed.
    /// </summary>
    internal (int X0, int Y0, int X1, int Y1) Refresh(int cx0, int cy0, int cx1, int cy1)
    {
        var (x0, y0, x1, y1) = Around(cx0, cy0, cx1, cy1);
        RefreshedCells += (long)(x1 - x0 + 1) * (y1 - y0 + 1);
        RebuildFree(x0, y0, x1, y1);
        ulong changed = 0;
        for (var k = 0; k < Classes; k++)
        {
            var cost = _cost[k];
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var c = CellCost(k, x, y);
                    if (cost[y * W + x] != c)
                    {
                        cost[y * W + x] = c;
                        changed |= ClassBit(k);
                    }
                }
            }
            UpdateMinCost(k);
        }
        LastChanged = changed;
        return (x0, y0, x1, y1);
    }

    /// <summary>
    /// The classes whose cost of some cell the last <see cref="Refresh"/> changed, a bit each (<see cref="ClassBit"/>): a change
    /// that leaves a class's costs as they were (a tree trunk falling, to infantry) leaves its caches valid. Not game state.
    /// </summary>
    internal ulong LastChanged { get; private set; }

    /// <summary>A class's bit in <see cref="LastChanged"/>: every bit from class 63 on (they share it, and so stay conservative).</summary>
    internal static ulong ClassBit(int k) => k < 63 ? 1UL << k : 1UL << 63;

    // A width-w square anchored at a depends on cells a..a+w-1. Its cost cell is
    // a+off (off=(w-1)/2 for odd w, w/2-1 for even w), so a changed cell c can affect
    // costs c-(w-1-off)..c+off. Both distances are <= MaxWidth-1. Keep that symmetric
    // conservative reach (4 at width 5), also covering all relevant free-square anchors
    // to the left/up; RebuildFree repairs its capped recurrence farther left/up itself.
    private const int Reach = MaxWidth - 1;

    /// <summary>The cells <see cref="Refresh"/> has recomputed so far (each rectangle's area once, for every class): a measure for tests and tools, not game state.</summary>
    internal long RefreshedCells { get; private set; }

    /// <summary>The rectangle of cells whose cost a change of the cells in a rectangle may change (what <see cref="Refresh"/> returns).</summary>
    internal (int X0, int Y0, int X1, int Y1) Around(int cx0, int cy0, int cx1, int cy1) =>
        (Math.Max(cx0 - Reach, 0), Math.Max(cy0 - Reach, 0), Math.Min(cx1 + Reach, W - 1), Math.Min(cy1 + Reach, H - 1));

    // CellCost: the cell's own terrain cost, if its clearance block is free for the class.
    private byte CellCost(int k, int x, int y)
    {
        var w = _width[k];
        var off = w % 2 == 1 ? (w - 1) / 2 : w / 2 - 1;
        int ax = x - off, ay = y - off;
        if (ax < 0 || ay < 0 || ax + w > W || ay + w > H || _free[ay * W + ax] < w)
        {
            return 0;
        }
        if (_terrain is null)
        {
            return (byte)NavGrid.Straight;
        }
        // The terrain cost of every cell of the block counts: the dearest one (a wide truck in a
        // grove pays for the grove beside it too); impassable anywhere in it blocks.
        var cost = 0;
        for (var by = ay; by < ay + w; by++)
        {
            for (var bx = ax; bx < ax + w; bx++)
            {
                var c = _terrain.Cost(_terrainClass[k], new Cell(bx, by));
                if (c == 0)
                {
                    return 0;
                }
                if (_noRoads[k] && _terrain.RoadKindAt(new Cell(bx, by)) is var kind and not (RoadKind.None or RoadKind.Footpath) &&
                    _terrain.Rules.Classes[_terrainClass[k]].RoadCostOf(kind) is var rp and > 0 and < 100)
                {
                    c = Math.Min(c * 100 / rp, 255); // the road's discount undone
                }
                cost = Math.Max(cost, c);
            }
        }
        return (byte)cost;
    }

    // RebuildFree recomputes the free-square sizes for a rectangle (and the cells above and to the
    // left of it that depend on it), from the bottom right up: size = 1 + min(right, down, diagonal).
    private void RebuildFree(int x0, int y0, int x1, int y1)
    {
        const int Cap = 8;
        int fx0 = Math.Max(x0 - Cap, 0), fy0 = Math.Max(y0 - Cap, 0);
        for (var y = y1; y >= fy0; y--)
        {
            for (var x = x1; x >= fx0; x--)
            {
                int v;
                if (!_grid.Passable(new Cell(x, y)) || _occupied[y * W + x] != 0)
                {
                    v = 0;
                }
                else
                {
                    var r = x + 1 < W ? _free[y * W + x + 1] : 0;
                    var d = y + 1 < H ? _free[(y + 1) * W + x] : 0;
                    var dg = x + 1 < W && y + 1 < H ? _free[(y + 1) * W + x + 1] : 0;
                    v = Math.Min(Cap, 1 + Math.Min(r, Math.Min(d, dg)));
                }
                _free[y * W + x] = (byte)v;
            }
        }
    }

    private void UpdateMinCost(int k)
    {
        if (_terrain is null)
        {
            _minCost[k] = Straight;
            return;
        }
        // The cheapest possible cell for this class: a flat road of the cheapest kind on the map
        // (forest only adds). A crowd's class pays for no road but a footpath.
        var mc = _terrain.Rules.Classes[_terrainClass[k]];
        var percent = _noRoads[k] ? _terrain.MinCrowdRoadCostPercent(_terrainClass[k]) : _terrain.MinRoadCostPercent(_terrainClass[k]);
        _minCost[k] = Math.Max(1, mc.Cost[0] * percent / 100);
    }
}
