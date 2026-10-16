namespace SimCore.Sim;

/// <summary>What occupies a map cell.</summary>
public enum Terrain : byte
{
    /// <summary>Open ground.</summary>
    Clear,

    /// <summary>Rock; ground units cannot enter.</summary>
    Blocked,
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
            _cells[Index(c)] = t;
        }
    }

    /// <summary>Reports whether ground units can enter c.</summary>
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

    internal int Index(Cell c) => c.Y * Width + c.X;

    internal Cell CellAt(int i) => new(i % Width, i / Width);
}
