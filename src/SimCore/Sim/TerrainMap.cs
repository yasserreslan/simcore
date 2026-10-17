using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// The terrain of a map with elevation, roads and forest (a flat map without one has none, and
/// plays and hashes exactly as before). Compact, one array per layer, no per-cell objects:
/// <list type="bullet">
/// <item>heights per cell corner, (W+1) x (H+1) values in the sim's fixed point (raw units, 1024
/// per cell = 2 m), 2 bytes each;</item>
/// <item>per-cell flags, 1 byte: forest density 0 to 3 (bits 0-1), road (bit 2), building pad (bit 3);</item>
/// <item>per-cell grade (percent, the largest corner height difference over the cell's width), 1 byte;</item>
/// <item>per movement class, a path cost grid, 1 byte a cell: 0 = impassable, flat open ground 10,
/// with slope, road and forest folded in. Built when the map loads and rebuilt per cell when a
/// pad or the forest changes.</item>
/// </list>
/// About 3 + 2 + classes bytes a cell: 1,500 x 1,500 cells with 4 classes take about 20 MB. The
/// cost grids are what the pathfinding milestone (HPA* sectors, the road layer, flow fields)
/// reads; <see cref="StepCost"/> adds the climbing penalty for a directed step.
/// </summary>
public sealed class TerrainMap
{
    /// <summary>Path cost of an impassable cell.</summary>
    public const byte Impassable = 0;

    /// <summary>Path cost of open flat ground (the unit of the cost grids).</summary>
    public const int BaseCost = 10;

    private const byte ForestMask = 0b11, RoadBit = 1 << 2, PadBit = 1 << 3;

    private readonly short[] _corners;
    private readonly byte[] _flags;
    private readonly byte[] _grade;
    private readonly byte[][] _cost;
    private ulong? _digest;

    /// <summary>A terrain from corner heights (raw fixed point, row by row) and per-cell flags.</summary>
    /// <exception cref="ArgumentException">The arrays do not match the size.</exception>
    public TerrainMap(int width, int height, short[] corners, byte[] flags, TerrainRules rules)
    {
        if (width <= 0 || height <= 0 || corners.Length != (width + 1) * (height + 1) || flags.Length != width * height)
        {
            throw new ArgumentException($"terrain: want {width + 1}x{height + 1} corners and {width}x{height} flags");
        }
        Width = width;
        Height = height;
        Rules = rules;
        _corners = corners;
        _flags = flags;
        _grade = new byte[width * height];
        _cost = new byte[rules.Classes.Count][];
        for (var k = 0; k < _cost.Length; k++)
        {
            _cost[k] = new byte[width * height];
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Rebuild(x, y);
            }
        }
    }

    /// <summary>A flat terrain at height 0 with no roads or forest.</summary>
    public static TerrainMap Flat(int width, int height, TerrainRules rules) =>
        new(width, height, new short[(width + 1) * (height + 1)], new byte[width * height], rules);

    /// <summary>The width in cells.</summary>
    public int Width { get; }

    /// <summary>The height in cells.</summary>
    public int Height { get; }

    /// <summary>The movement classes and their rules.</summary>
    public TerrainRules Rules { get; }

    /// <summary>Counts changes (forest, pads), so caches derived from the layers (not game state) know when to rebuild.</summary>
    internal int Version { get; private set; }

    /// <summary>The memory the layers take, in bytes.</summary>
    public long Bytes => (long)_corners.Length * 2 + _flags.Length + _grade.Length + (long)_cost.Length * _flags.Length;

    private bool Contains(Cell c) => c.X >= 0 && c.Y >= 0 && c.X < Width && c.Y < Height;

    private int Index(Cell c) => c.Y * Width + c.X;

    /// <summary>The height of corner (x, y), 0 to Width and 0 to Height (clamped).</summary>
    public Fixed Corner(int x, int y) =>
        Fixed.FromRaw(_corners[Math.Clamp(y, 0, Height) * (Width + 1) + Math.Clamp(x, 0, Width)]);

    /// <summary>
    /// The ground height at p: bilinear between the four corners of its cell, rounded to the
    /// nearest raw unit (halves up). Points off the map take the nearest edge.
    /// </summary>
    public Fixed HeightAt(Vec p)
    {
        var max = (long)Width * Fixed.OneRaw;
        var maxY = (long)Height * Fixed.OneRaw;
        var px = Math.Clamp(p.X.Raw, 0, max);
        var py = Math.Clamp(p.Y.Raw, 0, maxY);
        var cx = (int)Math.Min(px >> 10, Width - 1);
        var cy = (int)Math.Min(py >> 10, Height - 1);
        long fx = px - ((long)cx << 10), fy = py - ((long)cy << 10);
        long h00 = Corner(cx, cy).Raw, h10 = Corner(cx + 1, cy).Raw, h01 = Corner(cx, cy + 1).Raw, h11 = Corner(cx + 1, cy + 1).Raw;
        var top = h00 * (Fixed.OneRaw - fx) + h10 * fx;
        var bottom = h01 * (Fixed.OneRaw - fx) + h11 * fx;
        var v = top * (Fixed.OneRaw - fy) + bottom * fy; // in raw * 2^20
        return Fixed.FromRaw((v + (1L << 19)) >> 20);
    }

    /// <summary>The grade of a cell in percent (the largest corner height difference over its width); off-map 255.</summary>
    public int Grade(Cell c) => Contains(c) ? _grade[Index(c)] : 255;

    /// <summary>The slope class of a cell for a movement class; off-map Cliff.</summary>
    public SlopeClass Slope(int moveClass, Cell c) => Contains(c) ? Rules.Classes[moveClass].Classify(_grade[Index(c)]) : SlopeClass.Cliff;

    /// <summary>The path cost of a cell for a movement class (0: impassable; flat open ground 10).</summary>
    public int Cost(int moveClass, Cell c) => Contains(c) ? _cost[moveClass][Index(c)] : Impassable;

    /// <summary>The raw cost grid of a movement class, row by row (for the pathfinder).</summary>
    public ReadOnlySpan<byte> CostGrid(int moveClass) => _cost[moveClass];

    /// <summary>Reports whether a movement class can enter a cell.</summary>
    public bool Passable(int moveClass, Cell c) => Cost(moveClass, c) != Impassable;

    /// <summary>
    /// The cost of stepping from one cell into a neighbour: the target cell's cost, plus the
    /// class's climbing penalty when the step goes uphill (centre to centre); 0 when impassable.
    /// </summary>
    public int StepCost(int moveClass, Cell from, Cell to)
    {
        var cost = Cost(moveClass, to);
        if (cost == Impassable)
        {
            return 0;
        }
        if (HeightAt(to.Center()).Raw > HeightAt(from.Center()).Raw)
        {
            cost += cost * Rules.Classes[moveClass].UphillCostPercent / 100;
        }
        return cost;
    }

    /// <summary>The speed percent of a movement class in a cell, going up or not (0: impassable).</summary>
    public int SpeedPercent(int moveClass, Cell c, bool uphill)
    {
        if (!Contains(c))
        {
            return 0;
        }
        var mc = Rules.Classes[moveClass];
        var forest = _flags[Index(c)] & ForestMask;
        if (forest >= mc.ForestBlockedFrom)
        {
            return 0;
        }
        var slope = (int)mc.Classify(_grade[Index(c)]);
        return (uphill ? mc.SpeedUp[slope] : mc.SpeedDown[slope]) * mc.ForestSpeedPercent[forest] / 100;
    }

    /// <summary>The forest density of a cell, 0 (none) to 3 (dense).</summary>
    public int Forest(Cell c) => Contains(c) ? _flags[Index(c)] & ForestMask : 0;

    /// <summary>Reports whether a cell is road.</summary>
    public bool Road(Cell c) => Contains(c) && (_flags[Index(c)] & RoadBit) != 0;

    /// <summary>Reports whether a cell is on a building pad.</summary>
    public bool Pad(Cell c) => Contains(c) && (_flags[Index(c)] & PadBit) != 0;

    /// <summary>Sets the forest density of a cell (0 to 3) and rebuilds its costs.</summary>
    public void SetForest(Cell c, int density)
    {
        if (!Contains(c))
        {
            return;
        }
        var i = Index(c);
        _flags[i] = (byte)((_flags[i] & ~ForestMask) | Math.Clamp(density, 0, 3));
        Rebuild(c.X, c.Y);
        _digest = null;
        Version++;
    }

    /// <summary>
    /// Flattens a building pad: every corner of the w x h cells from origin to height, the cells
    /// marked as pad, and the costs of the cells around rebuilt (the hook for buildings and depots).
    /// </summary>
    public void SetPad(Cell origin, int w, int h, Fixed height)
    {
        var raw = (short)Math.Clamp(height.Raw, short.MinValue, short.MaxValue);
        for (var y = origin.Y; y <= origin.Y + h; y++)
        {
            for (var x = origin.X; x <= origin.X + w; x++)
            {
                if (x >= 0 && y >= 0 && x <= Width && y <= Height)
                {
                    _corners[y * (Width + 1) + x] = raw;
                }
            }
        }
        for (var y = origin.Y - 1; y <= origin.Y + h; y++)
        {
            for (var x = origin.X - 1; x <= origin.X + w; x++)
            {
                var c = new Cell(x, y);
                if (!Contains(c))
                {
                    continue;
                }
                if (x >= origin.X && y >= origin.Y && x < origin.X + w && y < origin.Y + h)
                {
                    _flags[Index(c)] |= PadBit;
                }
                Rebuild(x, y);
            }
        }
        _digest = null;
        Version++;
    }

    /// <summary>The cells rebuilt since routing last looked (null until routing starts). Not game state.</summary>
    internal List<int>? ChangeLog { get; set; }

    // Rebuild recomputes a cell's grade and every class's cost from its corners and flags.
    private void Rebuild(int x, int y)
    {
        var i = y * Width + x;
        ChangeLog?.Add(i);
        int a = _corners[y * (Width + 1) + x], b = _corners[y * (Width + 1) + x + 1];
        int c = _corners[(y + 1) * (Width + 1) + x], d = _corners[(y + 1) * (Width + 1) + x + 1];
        var diff = Math.Max(Math.Max(a, b), Math.Max(c, d)) - Math.Min(Math.Min(a, b), Math.Min(c, d));
        _grade[i] = (byte)Math.Min(diff * 100 / Fixed.OneRaw, 255);
        var flags = _flags[i];
        var forest = flags & ForestMask;
        for (var k = 0; k < _cost.Length; k++)
        {
            var mc = Rules.Classes[k];
            var cost = mc.Cost[(int)mc.Classify(_grade[i])];
            if (cost > 0 && forest >= mc.ForestBlockedFrom)
            {
                cost = 0;
            }
            if (cost > 0)
            {
                if ((flags & RoadBit) != 0)
                {
                    cost = cost * mc.RoadCostPercent / 100;
                }
                cost = cost * mc.ForestCostPercent[forest] / 100;
                cost = Math.Clamp(cost, 1, 255);
            }
            _cost[k][i] = (byte)cost;
        }
    }

    /// <summary>A 64-bit FNV-1a digest of the terrain and its rules (in the game hash when a map has terrain).</summary>
    public ulong Digest()
    {
        if (_digest is { } cached)
        {
            return cached;
        }
        var h = Rules.Digest();
        void Put(byte v)
        {
            h ^= v;
            h *= 1099511628211UL;
        }
        for (var k = 0; k < 4; k++)
        {
            Put((byte)(Width >> (8 * k)));
        }
        for (var k = 0; k < 4; k++)
        {
            Put((byte)(Height >> (8 * k)));
        }
        foreach (var v in _corners)
        {
            Put((byte)v);
            Put((byte)(v >> 8));
        }
        foreach (var f in _flags)
        {
            Put(f);
        }
        _digest = h;
        return h;
    }
}
