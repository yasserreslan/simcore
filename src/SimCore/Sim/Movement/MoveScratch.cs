using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>
/// Scratch space of the movement system, reused every tick and never hashed: the bodies that
/// take part in local movement (one per ground unit standing on the map), a grid of 2-cell
/// buckets over them, and the pairs of bodies close enough to touch this tick. Bodies are in
/// unit-ID order, and every bucket lists its bodies in that order, so the same world always
/// gives the same candidates in the same order. The movement system only sums integer
/// contributions from them, so its result does not depend on that order either.
/// </summary>
internal sealed class MoveScratch
{
    /// <summary>log2 of the bucket size in raw units (2 cells).</summary>
    internal const int BucketShift = Fixed.Shift + 1;

    // Body flags.
    internal const byte Mobile = 1;    // it can drive itself this tick
    internal const byte Moving = 2;    // it wants to move (mobile, not arrived, Desired non-zero)
    internal const byte Anchored = 4;   // nothing pushes it (an empty vehicle, a deployed launcher)
    internal const byte Vehicle = 8;   // wheeled or tracked: turn rate, acceleration
    internal const byte Pushed = 32;   // moved by a collision this tick

    internal int N;
    internal Unit[] U = [];
    internal long[] X = [];
    internal long[] Y = [];
    internal long[] X0 = [];
    internal long[] Y0 = [];
    internal long[] VX = [];
    internal long[] VY = [];
    internal long[] CX = [];
    internal long[] CY = [];
    internal int[] R = [];
    internal int[] Mass = [];
    internal int[] Cls = [];
    internal int[] Pct = [];
    internal int[] Contacts = [];
    internal byte[] Near = []; // Chebyshev cells from the body's cell to the nearest wall, capped (see WallCap)
    internal byte[] Flags = [];
    internal uint[] Id = [];      // unit IDs
    internal byte[] Owner = [];   // owners
    internal uint[] Enter = [];   // the vehicle a soldier is boarding (passes through it), or 0
    internal long[] PVX = [];     // last tick's velocity (Unit.Vel)
    internal long[] PVY = [];
    internal long MaxR;
    internal long Worst; // the deepest overlap met in the current collision round, percent of the smaller radius

    // Up to MaxContacts contacts per moving body with bodies standing still: the unit normal
    // toward each (raw, length 1024) and the speed it may walk into it with.
    internal const int MaxContacts = 8;
    internal long[] KX = [];
    internal long[] KY = [];
    internal long[] KL = [];
    internal long[] KD = [];
    internal uint[] KI = [];
    internal int[] KN = []; // how many contacts each body stored

    // The bucket grid: bucket b holds bodies when Slot[b] >= 0, and they are Items[SlotFirst[s]..SlotFirst[s] + SlotCount[s])
    // for s = Slot[b], in body order. Only the buckets that hold bodies get a slot (and are emptied again next tick), so a
    // tick costs the bodies, not the map's area.
    internal int Bw;
    internal int Bh;
    internal int[] Slot = [];
    internal int[] SlotFirst = [];
    internal int[] SlotCount = [];
    internal int[] Items = [];
    private readonly List<int> _used = []; // the buckets with a slot, in slot order
    internal int[] BucketOf = [];
    internal int[] Rank = [];

    // Pairs of body indices (A < B) that may touch this tick.
    internal int[] PairA = new int[1024];
    internal int[] PairB = new int[1024];
    internal int Pairs;

    // Per movement class (index + 1; 0 for no class): every cell's Chebyshev distance in cells to
    // the nearest cell that class can't enter (off the map counts), capped at WallCap; rebuilt
    // when the grid or the terrain changes. Bodies farther than that from any wall skip the wall
    // tests entirely.
    internal const byte WallCap = 4;
    internal byte[]?[] Wall = [];
    internal int[] WallGrid = [];
    internal int[] WallTerrain = [];
    internal TerrainMap? WallMap;
    internal readonly List<int> Changed = []; // scratch: cells changed since a wall map was built

    // Structures that block ground units with their footprint (raw box), besides blocked cells;
    // Id is the structure's (a vehicle driving out of its factory passes through that one's box).
    internal readonly List<(long X0, long Y0, long X1, long Y1, uint Id)> Boxes = [];

    // Out[i] is the factory body i is driving out of (its box doesn't block it), or 0.
    internal uint[] Out = [];

    // The Boxes indices filed by 8-cell bucket wherever a box's wall reach (WallCap cells) touches, so
    // NearWall tests only the boxes near a body; built each tick after Boxes (FileBoxes).
    private const int BoxShift = Fixed.Shift + 3;
    private List<int>[] _boxBuckets = [];
    private static readonly List<int> NoBoxes = []; // every bucket no box was ever filed under (never written)
    private readonly List<int> _boxUsed = [];
    private int _boxBw;
    private int _boxBh;

    internal void FileBoxes(int width, int height)
    {
        var bw = Math.Max(1, (width + 7) >> 3);
        var bh = Math.Max(1, (height + 7) >> 3);
        if (bw != _boxBw || bh != _boxBh)
        {
            _boxBw = bw;
            _boxBh = bh;
            _boxBuckets = new List<int>[bw * bh];
            Array.Fill(_boxBuckets, NoBoxes); // each gets its own list the first time a box is filed there
        }
        else
        {
            foreach (var b in _boxUsed)
            {
                _boxBuckets[b].Clear();
            }
        }
        _boxUsed.Clear();
        const long reach = WallCap * Fixed.OneRaw;
        for (var k = 0; k < Boxes.Count; k++)
        {
            var (x0, y0, x1, y1, _) = Boxes[k];
            int bx0 = BoxBx(x0 - reach), bx1 = BoxBx(x1 + reach), by0 = BoxBy(y0 - reach), by1 = BoxBy(y1 + reach);
            for (var by = by0; by <= by1; by++)
            {
                for (var bx = bx0; bx <= bx1; bx++)
                {
                    var b = by * _boxBw + bx;
                    if (_boxBuckets[b].Count == 0)
                    {
                        _boxUsed.Add(b);
                        if (ReferenceEquals(_boxBuckets[b], NoBoxes))
                        {
                            _boxBuckets[b] = [];
                        }
                    }
                    _boxBuckets[b].Add(k);
                }
            }
        }
    }

    // BoxesNear lists the indices of every box whose wall reach may contain the point (x, y) (raw).
    internal List<int> BoxesNear(long x, long y) => _boxBuckets[BoxBy(y) * _boxBw + BoxBx(x)];

    private int BoxBx(long raw) => (int)Math.Clamp(raw >> BoxShift, 0, _boxBw - 1);

    private int BoxBy(long raw) => (int)Math.Clamp(raw >> BoxShift, 0, _boxBh - 1);

    internal void Reset(int units, int width, int height)
    {
        if (U.Length < units)
        {
            var n = Math.Max(units, U.Length * 2);
            U = new Unit[n];
            X = new long[n];
            Y = new long[n];
            X0 = new long[n];
            Y0 = new long[n];
            VX = new long[n];
            VY = new long[n];
            CX = new long[n];
            CY = new long[n];
            R = new int[n];
            Mass = new int[n];
            Cls = new int[n];
            Pct = new int[n];
            Contacts = new int[n];
            Near = new byte[n];
            Flags = new byte[n];
            Id = new uint[n];
            Owner = new byte[n];
            Enter = new uint[n];
            Out = new uint[n];
            PVX = new long[n];
            PVY = new long[n];
            Items = new int[n];
            BucketOf = new int[n];
            Rank = new int[n];
            KX = new long[n * MaxContacts];
            KY = new long[n * MaxContacts];
            KL = new long[n * MaxContacts];
            KD = new long[n * MaxContacts];
            KI = new uint[n * MaxContacts];
            KN = new int[n];
        }
        var bw = Math.Max(1, (width + 1) >> 1);
        var bh = Math.Max(1, (height + 1) >> 1);
        if (bw != Bw || bh != Bh)
        {
            Bw = bw;
            Bh = bh;
            Slot = new int[bw * bh];
            Array.Fill(Slot, -1);
            _used.Clear();
        }
        N = 0;
        Pairs = 0;
        MaxR = 0;
        Boxes.Clear();
    }

    internal int Bx(long raw) => (int)Math.Clamp(raw >> BucketShift, 0, Bw - 1);

    internal int By(long raw) => (int)Math.Clamp(raw >> BucketShift, 0, Bh - 1);

    /// <summary>
    /// Files every body under the bucket of its position, body order kept within a bucket: a counting sort over the
    /// buckets in use only (last tick's are emptied first), so its cost is the bodies', whatever the map's size.
    /// </summary>
    internal void BuildGrid()
    {
        foreach (var b in _used)
        {
            Slot[b] = -1;
        }
        _used.Clear();
        if (SlotFirst.Length < N)
        {
            SlotFirst = new int[Math.Max(N, SlotFirst.Length * 2)];
            SlotCount = new int[SlotFirst.Length];
        }
        for (var i = 0; i < N; i++)
        {
            var b = By(Y[i]) * Bw + Bx(X[i]);
            if (Slot[b] < 0)
            {
                Slot[b] = _used.Count;
                SlotCount[_used.Count] = 0;
                _used.Add(b);
            }
            var s = Slot[b];
            BucketOf[i] = s; // (its bucket's slot)
            Rank[i] = SlotCount[s]++; // its place among its bucket's bodies, in body order
        }
        var at = 0;
        for (var s = 0; s < _used.Count; s++)
        {
            SlotFirst[s] = at;
            at += SlotCount[s];
        }
        for (var i = 0; i < N; i++)
        {
            Items[SlotFirst[BucketOf[i]] + Rank[i]] = i;
        }
    }

    internal void AddPair(int a, int b)
    {
        if (Pairs == PairA.Length)
        {
            Array.Resize(ref PairA, Pairs * 2);
            Array.Resize(ref PairB, Pairs * 2);
        }
        PairA[Pairs] = a;
        PairB[Pairs] = b;
        Pairs++;
    }
}
