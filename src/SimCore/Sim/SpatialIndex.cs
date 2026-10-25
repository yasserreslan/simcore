using SimCore.FixedPoint;
using SimCore.Model;

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
    private List<Unit>[][] _owned = []; // [owner][bucket]: the same units filed by owner, so a query can skip a side
    private int _ownerSlots; // owners 0 .. _ownerSlots - 1 have lists this build
    private readonly List<int> _used = []; // buckets filled by the last build (only these need clearing)
    private int _bw;
    private int _bh;

    // Distance spotting only ORs a side's deadline into targets. Once a target has this
    // phase's deadline it cannot help another viewer of that side. Lazy per-bucket lists
    // omit it for the rest of this index window; never used for target selection.
    private sealed class VisionBucket
    {
        internal readonly List<Unit>?[] Pending = new List<Unit>?[MatchSetup.MaxPlayers + 1];
        internal readonly bool[] Ready = new bool[MatchSetup.MaxPlayers + 1];
        internal void Clear()
        {
            Array.Clear(Ready);
            foreach (var list in Pending) { list?.Clear(); }
        }
    }
    private VisionBucket?[] _vision = [];


    /// <summary>The largest collision radius (raw) of any indexed unit.</summary>
    internal long MaxRadiusRaw { get; private set; }

    /// <summary>Files every unit, in list order (ID order), replacing the previous contents.</summary>
    internal void Build(List<Unit> units, Grid grid)
    {
        var bw = Math.Max(1, (grid.Width + BucketCells - 1) >> BucketShift);
        var bh = Math.Max(1, (grid.Height + BucketCells - 1) >> BucketShift);
        var owners = 1;
        foreach (var u in units)
        {
            owners = Math.Max(owners, u.Owner + 1);
        }
        if (bw != _bw || bh != _bh)
        {
            _bw = bw;
            _bh = bh;
            _buckets = NewBuckets(bw * bh);
            _owned = [];
            _vision = [];
        }
        else
        {
            foreach (var b in _used)
            {
                _buckets[b].Clear();
                if (_vision.Length != 0) { _vision[b]?.Clear(); }
                for (var o = 0; o < _ownerSlots; o++)
                {
                    if (!ReferenceEquals(_owned[o][b], Empty))
                    {
                        _owned[o][b].Clear(); // (the shared empty list is never written, not even cleared)
                    }
                }
            }
        }
        _used.Clear();
        if (owners > _owned.Length)
        {
            var grown = new List<Unit>[owners][];
            for (var o = 0; o < owners; o++)
            {
                grown[o] = o < _owned.Length ? _owned[o] : NewBuckets(_buckets.Length);
            }
            _owned = grown;
        }
        _ownerSlots = owners;
        long maxR = 0;
        foreach (var u in units)
        {
            Extent(u, out var x0, out var y0, out var x1, out var y1);
            int bx0 = Bx(x0), bx1 = Bx(x1), by0 = By(y0), by1 = By(y1);
            var mine = _owned[u.Owner];
            for (var by = by0; by <= by1; by++)
            {
                for (var bx = bx0; bx <= bx1; bx++)
                {
                    var b = by * _bw + bx;
                    if (_buckets[b].Count == 0)
                    {
                        _used.Add(b);
                        if (ReferenceEquals(_buckets[b], Empty))
                        {
                            _buckets[b] = []; // a bucket gets its own list the first time something is filed there
                        }
                    }
                    if (ReferenceEquals(mine[b], Empty))
                    {
                        mine[b] = [];
                    }
                    _buckets[b].Add(u);
                    mine[b].Add(u);
                }
            }
            maxR = Math.Max(maxR, u.Def.Archetype.Radius.Raw);
        }
        MaxRadiusRaw = maxR;
    }

    // Empty stands for every bucket nothing was ever filed under (never added to): a big map's buckets cost a reference
    // each until used.
    private static readonly List<Unit> Empty = [];

    private static List<Unit>[] NewBuckets(int n)
    {
        var buckets = new List<Unit>[n];
        Array.Fill(buckets, Empty);
        return buckets;
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
    /// Like <see cref="QueryUnordered"/>, but only units not owned by owner (and, with skipNeutral, not
    /// neutral): the other owners' lists are copied, so a side's own units are never visited. For callers
    /// that drop those units anyway and whose result does not depend on order or repeats.
    /// </summary>
    internal void QueryOthers(long x0, long y0, long x1, long y1, byte owner, bool skipNeutral, List<Unit> into)
    {
        into.Clear();
        int bx0 = Bx(x0), bx1 = Bx(x1), by0 = By(y0), by1 = By(y1);
        for (var o = skipNeutral ? 1 : 0; o < _ownerSlots; o++)
        {
            if (o == owner)
            {
                continue;
            }
            var lists = _owned[o];
            for (var by = by0; by <= by1; by++)
            {
                for (var bx = bx0; bx <= bx1; bx++)
                {
                    into.AddRange(lists[by * _bw + bx]);
                }
            }
        }
    }

    // Same candidate superset as QueryOthers for the distance-vision pass, with targets
    // already assigned this exact deadline omitted. Inside/garrison state, positions,
    // ownership and the air-only filter stay fixed in RunVision; deadlines only become
    // 'until'. The index is cleared before the next phase.
    internal void QueryVision(long x0, long y0, long x1, long y1, byte owner, uint until, bool airOnly, List<Unit> into)
    {
        into.Clear();
        if (_vision.Length == 0) { _vision = new VisionBucket?[_buckets.Length]; }
        int bx0 = Bx(x0), bx1 = Bx(x1), by0 = By(y0), by1 = By(y1);
        for (var by = by0; by <= by1; by++)
        {
            for (var bx = bx0; bx <= bx1; bx++)
            {
                var bucket = by * _bw + bx;
                if (_buckets[bucket].Count == 0) { continue; }
                var state = _vision[bucket] ??= new VisionBucket();
                var list = state.Pending[owner] ??= [];
                if (!state.Ready[owner])
                {
                    foreach (var t in _buckets[bucket])
                    {
                        if (t.Owner != owner && t.Inside == 0 && t.Garrison == 0 &&
                            (!airOnly || t.Def.Layer == Layers.Air) && t.SpotUntil[owner] != until)
                        {
                            list.Add(t);
                        }
                    }
                    state.Ready[owner] = true;
                }
                var write = 0;
                for (var read = 0; read < list.Count; read++)
                {
                    var t = list[read];
                    if (t.SpotUntil[owner] == until) { continue; }
                    list[write++] = t;
                    into.Add(t);
                }
                list.RemoveRange(write, list.Count - write);
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
