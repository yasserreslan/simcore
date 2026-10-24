using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SimCore.Sim;

/// <summary>What occupies a map cell.</summary>
public enum Terrain : byte
{
    /// <summary>Open ground.</summary>
    Clear,

    /// <summary>Rock; ground units cannot enter.</summary>
    Blocked,

    /// <summary>
    /// Open water (a river, lake or sea of the map's terrain: <see cref="TerrainMap.OpenWater"/>): ground units cannot
    /// enter it and nothing is put there, but shots fly over it and sight crosses it (unlike rock).
    /// </summary>
    Water,
}

/// <summary>The map: Width×Height cells, stored row by row.</summary>
public sealed class Grid
{
    private readonly Terrain[] _cells;

    /// <summary>Returns an all-clear map.</summary>
    public Grid(int width, int height)
    {
        Width = width;
        Height = height;
        _cells = new Terrain[width * height];
    }

    /// <summary>The width in cells.</summary>
    public int Width { get; }

    /// <summary>The height in cells.</summary>
    public int Height { get; }

    /// <summary>The terrain of every cell, row by row.</summary>
    internal ReadOnlySpan<Terrain> Cells => _cells;

    /// <summary>Reports whether c is on the map.</summary>
    public bool Contains(Cell c) => c.X >= 0 && c.Y >= 0 && c.X < Width && c.Y < Height;

    /// <summary>Returns the terrain at c. Off-map cells are blocked.</summary>
    public Terrain At(Cell c) => Contains(c) ? _cells[Index(c)] : Terrain.Blocked;

    /// <summary>Changes the terrain at c. Off-map cells are ignored.</summary>
    public void Set(Cell c, Terrain t)
    {
        if (Contains(c))
        {
            if (_cells[Index(c)] != t)
            {
                ChangeLog?.Add(Index(c));
                if (_journal.Count >= JournalMax)
                {
                    _journalFloor = _journal[JournalMax / 2 - 1].Version;
                    _journal.RemoveRange(0, JournalMax / 2);
                }
                _journal.Add((Version + 1, Index(c)));
                _digest = null;
            }
            _cells[Index(c)] = t;
            Version++;
        }
    }

    /// <summary>The cells changed since routing last looked (null until routing starts). Not game state.</summary>
    internal List<int>? ChangeLog { get; set; }

    /// <summary>Counts changes, so caches derived from the cells (not game state) know when to rebuild.</summary>
    internal int Version { get; private set; }

    // The cells changed by each Set (the version it made), oldest first, for caches catching up.
    private readonly List<(int Version, int Cell)> _journal = [];
    private int _journalFloor; // changes up to this version were dropped
    private const int JournalMax = 1 << 16;

    /// <summary>
    /// Adds to cells (indices) every cell changed after version since and returns true, or returns
    /// false when the changes reach back further than it remembers (rebuild everything).
    /// </summary>
    internal bool ChangedSince(int since, List<int> cells)
    {
        if (since >= Version)
        {
            return true;
        }
        if (since < _journalFloor)
        {
            return false; // the oldest were dropped
        }
        foreach (var (v, i) in _journal)
        {
            if (v > since)
            {
                cells.Add(i);
            }
        }
        return true;
    }

    /// <summary>Reports whether ground units can enter c (not rock, a building or open water).</summary>
    public bool Passable(Cell c) => At(c) == Terrain.Clear;

    /// <summary>
    /// Finds the n-th passable cell (0 = nearest) walking outward from c in
    /// rings. Group orders use it to give each unit its own destination.
    /// </summary>
    public bool TrySpiral(Cell c, int n, out Cell cell)
    {
        var maxR = Math.Max(Width, Height);
        for (var r = 0; r <= maxR; r++)
        {
            foreach (var p in Ring(c, r))
            {
                if (!Passable(p))
                {
                    continue;
                }
                if (n == 0)
                {
                    cell = p;
                    return true;
                }
                n--;
            }
        }
        cell = default;
        return false;
    }

    /// <summary>
    /// Finds the n-th passable cell (0 = nearest) that <paramref name="accept"/> also takes, walking outward from c in rings
    /// as <see cref="TrySpiral"/> does.
    /// </summary>
    public bool TrySpiralWhere(Cell c, int n, Func<Cell, bool> accept, out Cell cell)
    {
        var maxR = Math.Max(Width, Height);
        for (var r = 0; r <= maxR; r++)
        {
            foreach (var p in Ring(c, r))
            {
                if (!Passable(p) || !accept(p))
                {
                    continue;
                }
                if (n == 0)
                {
                    cell = p;
                    return true;
                }
                n--;
            }
        }
        cell = default;
        return false;
    }

    // Ring returns the cells at Chebyshev distance r from c, clockwise from
    // the top-left corner.
    private static List<Cell> Ring(Cell c, int r)
    {
        if (r == 0)
        {
            return [c];
        }
        var output = new List<Cell>(8 * r);
        for (var x = c.X - r; x <= c.X + r; x++)
        {
            output.Add(new Cell(x, c.Y - r));
        }
        for (var y = c.Y - r + 1; y <= c.Y + r; y++)
        {
            output.Add(new Cell(c.X + r, y));
        }
        for (var x = c.X + r - 1; x >= c.X - r; x--)
        {
            output.Add(new Cell(x, c.Y + r));
        }
        for (var y = c.Y + r - 1; y > c.Y - r; y--)
        {
            output.Add(new Cell(c.X - r, y));
        }
        return output;
    }

    // The digest of the cells, kept until a Set changes one (cells change nowhere else).
    private ulong? _digest;

    /// <summary>
    /// A digest of every cell for the game hash: the cell count, then the cells packed
    /// eight to a word (little-endian, the last word zero-padded). It is cached and
    /// recomputed only after a cell changes.
    /// </summary>
    internal ulong Digest()
    {
        if (_digest is { } cached)
        {
            return cached;
        }
        var bytes = MemoryMarshal.AsBytes(_cells.AsSpan());
        var h = new WordHash();
        h.PutSigned(bytes.Length);
        var i = 0;
        for (; i + 8 <= bytes.Length; i += 8)
        {
            h.Put(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i, 8)));
        }
        if (i < bytes.Length)
        {
            ulong last = 0;
            for (var k = 0; i + k < bytes.Length; k++)
            {
                last |= (ulong)bytes[i + k] << (8 * k);
            }
            h.Put(last);
        }
        _digest = h.Sum;
        return h.Sum;
    }

    internal int Index(Cell c) => c.Y * Width + c.X;

    internal Cell CellAt(int i) => new(i % Width, i / Width);
}
