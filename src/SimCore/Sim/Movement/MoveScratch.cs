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

    // The bucket grid, as a counting sort: bucket b's bodies are Items[Start[b]..Start[b+1]).
    internal int Bw;
    internal int Bh;
    internal int[] Start = [];
    internal int[] Items = [];
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
            Start = new int[bw * bh + 1];
        }
        N = 0;
        Pairs = 0;
        MaxR = 0;
        Boxes.Clear();
    }

    internal int Bx(long raw) => (int)Math.Clamp(raw >> BucketShift, 0, Bw - 1);

    internal int By(long raw) => (int)Math.Clamp(raw >> BucketShift, 0, Bh - 1);

    /// <summary>Files every body under the bucket of its position (counting sort, body order kept).</summary>
    internal void BuildGrid()
    {
        var nb = Bw * Bh;
        Array.Clear(Start);
        for (var i = 0; i < N; i++)
        {
            var b = By(Y[i]) * Bw + Bx(X[i]);
            BucketOf[i] = b;
            Rank[i] = Start[b + 1]++; // its place among its bucket's bodies, in body order
        }
        for (var b = 0; b < nb; b++)
        {
            Start[b + 1] += Start[b];
        }
        for (var i = 0; i < N; i++)
        {
            Items[Start[BucketOf[i]] + Rank[i]] = i;
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
