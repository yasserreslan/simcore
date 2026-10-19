using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>
/// A uniform grid of buckets over the map (<see cref="BucketCells"/> cells a side) that
/// finds the units near a point or a segment without scanning every unit. It is scratch
/// space, not game state: it is rebuilt from the unit list (in ID order) when a system
/// needs it, never hashed, and only narrows the candidates a system then checks with its
/// own exact test. A unit is filed under the bucket of its position; a structure under
/// every bucket its footprint touches. Positions off the map are clamped to the edge
/// buckets, so a query always finds every unit whose extent overlaps its box.
/// </summary>
internal sealed class SpatialIndex
{
    /// <summary>log2 of the bucket size in cells.</summary>
    internal const int BucketShift = 3;

    /// <summary>The bucket size in cells.</summary>
    internal const int BucketCells = 1 << BucketShift;

    private const int RawShift = Fixed.Shift + BucketShift;

    private List<Unit>[] _buckets = [];
    private int _bw;
    private int _bh;

    /// <summary>The largest collision radius (raw) of any indexed unit.</summary>
    internal long MaxRadiusRaw { get; private set; }

    /// <summary>Files every unit, in list order (ID order), replacing the previous contents.</summary>
    internal void Build(List<Unit> units, Grid grid)
    {
        var bw = Math.Max(1, (grid.Width + BucketCells - 1) >> BucketShift);
        var bh = Math.Max(1, (grid.Height + BucketCells - 1) >> BucketShift);
        if (bw != _bw || bh != _bh)
        {
            _bw = bw;
            _bh = bh;
            _buckets = new List<Unit>[bw * bh];
            for (var i = 0; i < _buckets.Length; i++)
            {
                _buckets[i] = [];
            }
        }
        else
        {
            foreach (var b in _buckets)
            {
                b.Clear();
            }
        }
        long maxR = 0;
        foreach (var u in units)
        {
            Extent(u, out var x0, out var y0, out var x1, out var y1);
            int bx0 = Bx(x0), bx1 = Bx(x1), by0 = By(y0), by1 = By(y1);
            for (var by = by0; by <= by1; by++)
            {
                for (var bx = bx0; bx <= bx1; bx++)
                {
                    _buckets[by * _bw + bx].Add(u);
                }
            }
            maxR = Math.Max(maxR, u.Def.Archetype.Radius.Raw);
        }
        MaxRadiusRaw = maxR;
    }

    /// <summary>
    /// Appends to into (after clearing it) every unit whose extent may overlap the box
    /// [x0, x1] x [y0, y1] (raw units), in no particular order; a structure that spans
    /// several buckets can appear more than once. For callers whose result does not
    /// depend on order or repeats (flags that are OR-ed in, a minimum with an explicit
    /// ID tie-break).
    /// </summary>
    internal void QueryUnordered(long x0, long y0, long x1, long y1, List<Unit> into)
    {
        into.Clear();
        int bx0 = Bx(x0), bx1 = Bx(x1), by0 = By(y0), by1 = By(y1);
        for (var by = by0; by <= by1; by++)
        {
            for (var bx = bx0; bx <= bx1; bx++)
            {
                into.AddRange(_buckets[by * _bw + bx]);
            }
        }
    }

    /// <summary>
    /// Like <see cref="QueryUnordered"/>, but the result is in ascending ID order without
    /// repeats: the order a scan of the whole unit list visits them in.
    /// </summary>
    internal void Query(long x0, long y0, long x1, long y1, List<Unit> into)
    {
        QueryUnordered(x0, y0, x1, y1, into);
        if (Bx(x0) == Bx(x1) && By(y0) == By(y1))
        {
            return; // one bucket: already in ID order, and nothing repeats
        }
        into.Sort(ById);
        var k = 0;
        for (var i = 0; i < into.Count; i++)
        {
            if (k == 0 || into[k - 1] != into[i])
            {
                into[k++] = into[i];
            }
        }
        into.RemoveRange(k, into.Count - k);
    }

    private static readonly Comparison<Unit> ById = (a, b) => a.Id.CompareTo(b.Id);

    // Extent is the box a unit occupies for every query: a structure's footprint, anyone
    // else's position.
    internal static void Extent(Unit u, out long x0, out long y0, out long x1, out long y1)
    {
        long hw = 0, hh = 0;
        if (u.Def.IsStructure)
        {
            hw = (long)World.FootW(u) * Fixed.OneRaw / 2;
            hh = (long)World.FootH(u) * Fixed.OneRaw / 2;
        }
        x0 = u.Pos.X.Raw - hw;
        x1 = u.Pos.X.Raw + hw;
        y0 = u.Pos.Y.Raw - hh;
        y1 = u.Pos.Y.Raw + hh;
    }

    private int Bx(long raw) => (int)Math.Clamp(raw >> RawShift, 0, _bw - 1);

    private int By(long raw) => (int)Math.Clamp(raw >> RawShift, 0, _bh - 1);
}
