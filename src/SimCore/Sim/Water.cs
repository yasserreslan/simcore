using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>What a body of water is (for renderers: a river flows, a lake and the sea lie still).</summary>
public enum WaterKind : byte
{
    /// <summary>A river: a centre line with a width, flowing from its first point to its last.</summary>
    River,

    /// <summary>A lake: a polygon.</summary>
    Lake,

    /// <summary>The sea: a polygon.</summary>
    Sea,
}

/// <summary>
/// A body of water on a map (<see cref="TerrainMap.WaterBodies"/>): a river (a centre line of cells with a width per
/// point, flowing in the line's order) or a lake or the sea (a polygon), each with one flat surface height. Its cells are
/// the cells whose centre lies in it or on its edge (the shore rule, docs/design.md "Water"). Immutable.
/// </summary>
public sealed class WaterBody
{
    private WaterBody(WaterKind kind, Fixed surface, Cell[] line, int[] widths, (int X, int Y)[] outline)
    {
        Kind = kind;
        Surface = surface;
        Line = line;
        Widths = widths;
        Outline = outline;
    }

    /// <summary>The widest river, in cells.</summary>
    public const int MaxWidth = 255;

    /// <summary>The most water bodies a map may have.</summary>
    public const int MaxBodies = 255;

    /// <summary>River, lake or sea.</summary>
    public WaterKind Kind { get; }

    /// <summary>The water surface height (absolute, raw: 512 per metre, as the terrain's corners).</summary>
    public Fixed Surface { get; }

    /// <summary>A river's centre line: the cells it runs through, upstream first (empty for a lake or the sea).</summary>
    public IReadOnlyList<Cell> Line { get; }

    /// <summary>A river's width in cells at each point of its line (between points it changes evenly).</summary>
    public IReadOnlyList<int> Widths { get; }

    /// <summary>
    /// A lake's or the sea's outline: points in map coordinates in cells from the map's top-left corner (cell (x, y)
    /// spans x to x + 1), in order (empty for a river).
    /// </summary>
    public IReadOnlyList<(int X, int Y)> Outline { get; }

    /// <summary>A river along line (2 points or more, upstream first), widths cells wide at each point (1 to <see cref="MaxWidth"/>).</summary>
    /// <exception cref="ArgumentException">Too few points, or the widths do not match them.</exception>
    public static WaterBody River(Fixed surface, IReadOnlyList<Cell> line, IReadOnlyList<int> widths)
    {
        if (line.Count < 2 || widths.Count != line.Count)
        {
            throw new ArgumentException("water: a river wants 2 points or more and a width for each");
        }
        var w = new int[widths.Count];
        for (var i = 0; i < w.Length; i++)
        {
            w[i] = Math.Clamp(widths[i], 1, MaxWidth);
        }
        return new WaterBody(WaterKind.River, surface, [.. line], w, []);
    }

    /// <summary>A lake or the sea inside outline (3 points or more, map coordinates).</summary>
    /// <exception cref="ArgumentException">A river kind, or too few points.</exception>
    public static WaterBody Area(WaterKind kind, Fixed surface, IReadOnlyList<(int X, int Y)> outline)
    {
        if (kind == WaterKind.River || outline.Count < 3)
        {
            throw new ArgumentException("water: a lake or the sea wants an outline of 3 points or more");
        }
        return new WaterBody(kind, surface, [], [], [.. outline]);
    }

    // Everything below works in half cells, so cell centres ((2x + 1, 2y + 1)) and the outline's corners ((2X, 2Y)) are
    // whole numbers; Int128 keeps the river test's products exact on the largest maps.

    // Paint writes id into cells (w x h, row by row) on every cell of this body (later bodies paint over earlier ones).
    internal void Paint(byte id, int w, int h, byte[] cells)
    {
        if (Kind == WaterKind.River)
        {
            for (var k = 0; k + 1 < Line.Count; k++)
            {
                PaintSegment(k, id, w, h, cells);
            }
            return;
        }
        PaintArea(id, w, h, cells);
    }

    private void PaintSegment(int k, byte id, int w, int h, byte[] cells)
    {
        Cell a = Line[k], b = Line[k + 1];
        int wa = Widths[k], wb = Widths[k + 1];
        var reach = (Math.Max(wa, wb) + 1) / 2 + 1;
        int x0 = Math.Max(Math.Min(a.X, b.X) - reach, 0), x1 = Math.Min(Math.Max(a.X, b.X) + reach, w - 1);
        int y0 = Math.Max(Math.Min(a.Y, b.Y) - reach, 0), y1 = Math.Min(Math.Max(a.Y, b.Y) + reach, h - 1);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (OnSegment(a, b, wa, wb, x, y))
                {
                    cells[y * w + x] = id;
                }
            }
        }
    }

    // OnSegment: is cell (x, y)'s centre within half the width of the centre line from a to b (the width changing evenly
    // along it, round caps at the ends)? In half cells the half width is the width itself.
    private static bool OnSegment(Cell a, Cell b, int wa, int wb, int x, int y)
    {
        long px = x - a.X, py = y - a.Y; // from a, in cells (doubling cancels on both sides of every test below)
        long dx = b.X - a.X, dy = b.Y - a.Y;
        var l = dx * dx + dy * dy;
        var s = px * dx + py * dy;
        // In half cells: |P - A|^2 * 4 <= w^2.
        if (l == 0 || s <= 0)
        {
            return 4 * (px * px + py * py) <= (long)wa * wa;
        }
        if (s >= l)
        {
            long qx = x - b.X, qy = y - b.Y;
            return 4 * (qx * qx + qy * qy) <= (long)wb * wb;
        }
        // Perpendicular: dist^2 = cross^2 / l, the width there (wa l + (wb - wa) s) / l: 4 cross^2 l <= (wa l + (wb - wa) s)^2.
        var cross = (Int128)(px * dy - py * dx);
        var wl = (Int128)wa * l + (Int128)(wb - wa) * s;
        return 4 * cross * cross * l <= wl * wl;
    }

    // PaintArea fills the outline (even-odd) a row at a time: a cell is in when its centre lies inside or on the edge.
    // Centres sit on odd half-cell rows and the corners on even ones, so no vertex ever lies on a scan line.
    private void PaintArea(byte id, int w, int h, byte[] cells)
    {
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (var (_, oy) in Outline)
        {
            (minY, maxY) = (Math.Min(minY, oy), Math.Max(maxY, oy));
        }
        var xs = new List<(long Num, long Den)>();
        for (var y = Math.Max(minY, 0); y < Math.Min(maxY, h); y++)
        {
            long py = 2 * y + 1;
            xs.Clear();
            for (var i = 0; i < Outline.Count; i++)
            {
                var (ax, ay) = Outline[i];
                var (bx, by) = Outline[(i + 1) % Outline.Count];
                long y2a = 2L * ay, y2b = 2L * by;
                if ((y2a < py) == (y2b < py))
                {
                    continue;
                }
                // x = 2ax + (py - 2ay) (2bx - 2ax) / (2by - 2ay), as a fraction with a positive denominator.
                var den = y2b - y2a;
                var num = 2L * ax * den + (py - y2a) * (2L * bx - 2L * ax);
                if (den < 0)
                {
                    (num, den) = (-num, -den);
                }
                xs.Add((num, den));
            }
            xs.Sort((p, q) => (p.Num * q.Den).CompareTo(q.Num * p.Den));
            for (var k = 0; k + 1 < xs.Count; k += 2)
            {
                // Cells whose centre 2cx + 1 lies in [x_k, x_k+1]: cx from ceil((x_k - 1) / 2) to floor((x_k+1 - 1) / 2).
                var lo = CeilDiv(xs[k].Num - xs[k].Den, 2 * xs[k].Den);
                var hi = FloorDiv(xs[k + 1].Num - xs[k + 1].Den, 2 * xs[k + 1].Den);
                for (var x = Math.Max(lo, 0); x <= Math.Min(hi, w - 1); x++)
                {
                    cells[y * w + (int)x] = id;
                }
            }
        }
    }

    private static long FloorDiv(long p, long q) => p >= 0 ? p / q : -((-p + q - 1) / q);

    private static long CeilDiv(long p, long q) => -FloorDiv(-p, q);

    /// <summary>
    /// The direction a river flows at cell c: along the segment of its centre line nearest c's centre (the earlier
    /// segment on a tie), a vector of length 1; zero for a lake or the sea.
    /// </summary>
    public Vec FlowAt(Cell c)
    {
        if (Kind != WaterKind.River)
        {
            return default;
        }
        var best = -1;
        Int128 bestNum = 0, bestDen = 1;
        for (var k = 0; k + 1 < Line.Count; k++)
        {
            Cell a = Line[k], b = Line[k + 1];
            long dx = b.X - a.X, dy = b.Y - a.Y, px = c.X - a.X, py = c.Y - a.Y;
            var l = dx * dx + dy * dy;
            if (l == 0)
            {
                continue;
            }
            var s = px * dx + py * dy;
            Int128 num, den;
            if (s <= 0)
            {
                (num, den) = (px * px + py * py, 1);
            }
            else if (s >= l)
            {
                long qx = c.X - b.X, qy = c.Y - b.Y;
                (num, den) = (qx * qx + qy * qy, 1);
            }
            else
            {
                var cross = (Int128)(px * dy - py * dx);
                (num, den) = (cross * cross, l);
            }
            if (best < 0 || num * bestDen < bestNum * den)
            {
                (best, bestNum, bestDen) = (k, num, den);
            }
        }
        if (best < 0)
        {
            return default;
        }
        var d = new Vec(Fixed.FromInt(Line[best + 1].X - Line[best].X), Fixed.FromInt(Line[best + 1].Y - Line[best].Y));
        return d.WithLen(Fixed.One);
    }
}
