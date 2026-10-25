namespace SimCore.Sim;

/// <summary>
/// One navigation class's connected regions, kept up to date incrementally. Each HPA* sector
/// labels the regions of its own cells (steps inside the sector), each sector keeps the links
/// from its regions to those of the neighbouring sectors after it, and a union of all the
/// sectors' regions over the links gives the regions of the whole map. A map change relabels only
/// the sectors it touched and re-links them and the neighbours that link to them; the union is
/// over regions and links, not cells. Which cells share a region is exactly what a flood fill of
/// the whole map gives (the region numbers differ, and nothing depends on them).
/// </summary>
internal sealed partial class Regions
{
    private const int S = Hpa.S;

    // A sector's link list checks this many of its last entries for a duplicate before adding.
    private const int DedupWindow = 32;

    // A sector with this many regions or more (never on real ground: a checkerboard of single cells) keeps its labels in a
    // ushort array of its own (_wide); the rest keep them in the byte a cell.
    private const int Narrow = 255;

    private readonly NavGrid _nav;
    private readonly int _k;
    private readonly int _sx;
    private readonly int _sy;
    private readonly byte[] _local; // per cell: its region inside its sector (from 1), 0 impassable; in a wide sector unused
    private readonly ushort[]?[] _wide; // per sector: null, or its cells' regions (S x S, row by row) when it has Narrow or more
    private readonly ushort[] _scratch = new ushort[S * S]; // a sector's labels while it is flooded
    private readonly int[] _count; // per sector: its regions
    private readonly int[][] _links; // per sector: (own region, later sector, its region) triples
    private readonly int[] _linkLen; // per sector: ints used in _links
    private readonly bool[] _dirty; // per sector: relabel
    private readonly bool[] _relink; // per sector: re-link
    private readonly List<int> _dirtyList = [];
    private readonly List<int> _relinkList = [];
    private readonly int[] _base; // per sector: its first region's node
    private readonly int[] _stack = new int[S * S];
    private int[] _parent = [];
    private int[] _id = [];
    private bool _unite = true;

    internal Regions(NavGrid nav, int k)
    {
        _nav = nav;
        _k = k;
        _sx = (nav.W + S - 1) / S;
        _sy = (nav.H + S - 1) / S;
        var n = _sx * _sy;
        _local = new byte[nav.W * nav.H];
        _wide = new ushort[]?[n];
        _count = new int[n];
        _links = new int[n][];
        _linkLen = new int[n];
        _dirty = new bool[n];
        _relink = new bool[n];
        _base = new int[n];
        for (var s = 0; s < n; s++)
        {
            _links[s] = [];
            _dirty[s] = true;
            _dirtyList.Add(s);
        }
    }

    /// <summary>
    /// Cells labelled, border cells linked and regions and links united so far: deterministic
    /// work counts (a full flood fill of the map is at least its cell count).
    /// </summary>
    internal long Work { get; private set; }

    /// <summary>The memory the regions hold, in bytes (array payloads, about: for benchmarks; not game state).</summary>
    internal long Bytes
    {
        get
        {
            long b = CoarseBytes + _local.Length + 4L * (_count.Length + _linkLen.Length + _base.Length + _parent.Length + _id.Length) + 2L * _dirty.Length + 8L * _wide.Length;
            foreach (var wide in _wide)
            {
                b += 2L * (wide?.Length ?? 0);
            }
            foreach (var l in _links)
            {
                b += 4L * l.Length;
            }
            return b;
        }
    }

    /// <summary>The region of a cell: 0 when impassable, else the same number for every cell connected to it.</summary>
    internal int Get(int cell)
    {
        if (_unite)
        {
            Update();
        }
        int x = cell % _nav.W, y = cell / _nav.W;
        var s = y / S * _sx + x / S;
        var l = Local(cell, x, y, s);
        if (l == 0)
        {
            return 0;
        }
        return _id[_base[s] + l - 1];
    }

    // Local is cell (x, y)'s region inside its sector s (from 1; 0 impassable).
    private int Local(int cell, int x, int y, int s) => _wide[s] is { } wide ? wide[(y % S) * S + x % S] : _local[cell];

    /// <summary>Marks the sectors over a rectangle of cells whose cost may have changed for relabelling.</summary>
    internal void Invalidate(int x0, int y0, int x1, int y1)
    {
        int sx0 = Math.Max(x0, 0) / S, sy0 = Math.Max(y0, 0) / S;
        int sx1 = Math.Min(x1 / S, _sx - 1), sy1 = Math.Min(y1 / S, _sy - 1);
        for (var sy = sy0; sy <= sy1; sy++)
        {
            for (var sx = sx0; sx <= sx1; sx++)
            {
                var s = sy * _sx + sx;
                if (!_dirty[s])
                {
                    _dirty[s] = true;
                    _dirtyList.Add(s);
                }
            }
        }
        _unite = true;
    }

    private void Update()
    {
        foreach (var s in _dirtyList)
        {
            Label(s);
            _dirty[s] = false;
            // Links between two sectors live on the earlier one: this sector's and its earlier neighbours'.
            int sx = s % _sx, sy = s / _sx;
            Relink(s);
            if (sx > 0)
            {
                Relink(s - 1);
            }
            if (sy > 0)
            {
                Relink(s - _sx);
                if (sx > 0)
                {
                    Relink(s - _sx - 1);
                }
                if (sx < _sx - 1)
                {
                    Relink(s - _sx + 1);
                }
            }
        }
        _dirtyList.Clear();
        foreach (var s in _relinkList)
        {
            Link(s);
            _relink[s] = false;
        }
        _relinkList.Clear();
        Unite();
        _unite = false;
    }

    private void Relink(int s)
    {
        if (!_relink[s])
        {
            _relink[s] = true;
            _relinkList.Add(s);
        }
    }

    // Label floods the regions of one sector over the steps inside it (in sector-local scratch, (x - x0) + (y - y0) * S),
    // then keeps them a byte a cell, or in the sector's own ushort array when there are Narrow or more.
    private void Label(int s)
    {
        int w = _nav.W, x0 = s % _sx * S, y0 = s / _sx * S;
        int x1 = Math.Min(x0 + S, w) - 1, y1 = Math.Min(y0 + S, _nav.H) - 1;
        var lab = _scratch;
        Array.Clear(lab);
        var next = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var start = y * w + x;
                if (lab[(y - y0) * S + x - x0] != 0 || _nav.Cost(_k, start) == 0)
                {
                    continue;
                }
                next++;
                var top = 0;
                _stack[top++] = start;
                lab[(y - y0) * S + x - x0] = (ushort)next;
                while (top > 0)
                {
                    var c = _stack[--top];
                    int cx = c % w, cy = c / w;
                    for (var d = 0; d < 8; d++)
                    {
                        int nx = cx + LocalSearch.Dx[d], ny = cy + LocalSearch.Dy[d];
                        if (nx < x0 || nx > x1 || ny < y0 || ny > y1 || lab[(ny - y0) * S + nx - x0] != 0 ||
                            _nav.Step(_k, cx, cy, LocalSearch.Dx[d], LocalSearch.Dy[d]) == 0)
                        {
                            continue;
                        }
                        lab[(ny - y0) * S + nx - x0] = (ushort)next;
                        _stack[top++] = ny * w + nx;
                    }
                }
            }
        }
        if (next < Narrow)
        {
            _wide[s] = null;
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    _local[y * w + x] = (byte)lab[(y - y0) * S + x - x0];
                }
            }
        }
        else
        {
            _wide[s] = [.. lab];
        }
        _count[s] = next;
        Work += (x1 - x0 + 1) * (y1 - y0 + 1);
    }

    // Link lists the links from a sector's border cells to the regions of the sectors after it.
    private void Link(int s)
    {
        _linkLen[s] = 0;
        int w = _nav.W, x0 = s % _sx * S, y0 = s / _sx * S;
        int x1 = Math.Min(x0 + S, w) - 1, y1 = Math.Min(y0 + S, _nav.H) - 1;
        for (var y = y0; y <= y1; y++)
        {
            var edge = y == y0 || y == y1;
            for (var x = x0; x <= x1; x += edge || x == x1 ? 1 : Math.Max(x1 - x0, 1))
            {
                Work++;
                var a = Local(y * w + x, x, y, s);
                if (a == 0)
                {
                    continue;
                }
                for (var d = 0; d < 8; d++)
                {
                    int nx = x + LocalSearch.Dx[d], ny = y + LocalSearch.Dy[d];
                    if (!_nav.Contains(nx, ny))
                    {
                        continue;
                    }
                    var t = ny / S * _sx + nx / S;
                    if (t <= s || _nav.Step(_k, x, y, LocalSearch.Dx[d], LocalSearch.Dy[d]) == 0)
                    {
                        continue;
                    }
                    Add(s, a, t, Local(ny * w + nx, nx, ny, t));
                }
            }
        }
    }

    private void Add(int s, int a, int t, int b)
    {
        var list = _links[s];
        var len = _linkLen[s];
        for (var i = len - 3; i >= 0 && i >= len - 3 * DedupWindow; i -= 3)
        {
            if (list[i] == a && list[i + 1] == t && list[i + 2] == b)
            {
                return;
            }
        }
        if (len + 3 > list.Length)
        {
            Array.Resize(ref list, Math.Max(12, list.Length * 2));
            _links[s] = list;
        }
        list[len] = a;
        list[len + 1] = t;
        list[len + 2] = b;
        _linkLen[s] = len + 3;
    }

    // Unite joins every sector's regions over the links, giving each node its region number.
    private void Unite()
    {
        _coarseStart = []; // region nodes may have been renumbered by this update
        var total = 0;
        for (var s = 0; s < _count.Length; s++)
        {
            _base[s] = total;
            total += _count[s];
        }
        if (_parent.Length < total)
        {
            _parent = new int[total];
            _id = new int[total];
        }
        for (var i = 0; i < total; i++)
        {
            _parent[i] = i;
        }
        var links = 0;
        for (var s = 0; s < _count.Length; s++)
        {
            var list = _links[s];
            for (var i = 0; i < _linkLen[s]; i += 3)
            {
                int ra = Find(_base[s] + list[i] - 1), rb = Find(_base[list[i + 1]] + list[i + 2] - 1);
                if (ra < rb)
                {
                    _parent[rb] = ra;
                }
                else if (rb < ra)
                {
                    _parent[ra] = rb;
                }
            }
            links += _linkLen[s] / 3;
        }
        for (var i = 0; i < total; i++)
        {
            _id[i] = Find(i) + 1;
        }
        Work += _count.Length + total + links;
    }

    private int Find(int i)
    {
        while (_parent[i] != i)
        {
            _parent[i] = _parent[_parent[i]];
            i = _parent[i];
        }
        return i;
    }
}
