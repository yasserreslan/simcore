namespace SimCore.FixedPoint;

/// <summary>
/// The 32 directions a unit can face. Facing 0 is north (up the screen) and
/// facings increase clockwise, 11.25° apart. This matches the 32-direction
/// sprite sheets.
/// </summary>
public static class Facing
{
    /// <summary>The number of facings.</summary>
    public const int Count = 32;

    /// <summary>Up the screen.</summary>
    public const int North = 0;

    /// <summary>Right.</summary>
    public const int East = 8;

    /// <summary>Down the screen.</summary>
    public const int South = 16;

    /// <summary>Left.</summary>
    public const int West = 24;

    // Quarter holds sin(k × 11.25°) × 1024 for k = 0..8, rounded. The other
    // quadrants are built from it by symmetry, so no trig runs at startup.
    private static readonly long[] Quarter = [0, 200, 392, 569, 724, 851, 946, 1004, 1024];

    private static readonly Vec[] Dirs = BuildDirs();

    private static Vec[] BuildDirs()
    {
        var d = new Vec[Count];
        for (var i = 0; i < Count; i++)
        {
            var r = i % 8;
            var s = Quarter[r];
            var c = Quarter[8 - r];
            var (sin, cos) = (i / 8) switch
            {
                0 => (s, c),
                1 => (c, -s),
                2 => (-s, -c),
                _ => (-c, s),
            };
            d[i] = new Vec(Fixed.FromRaw(sin), Fixed.FromRaw(-cos));
        }
        return d;
    }

    /// <summary>Maps any integer to a facing in [0, <see cref="Count"/>).</summary>
    public static int Wrap(int f) => ((f % Count) + Count) % Count;

    /// <summary>Returns the unit vector for facing f.</summary>
    public static Vec Dir(int f) => Dirs[Wrap(f)];

    /// <summary>
    /// Finds the facing closest to direction d. Returns false, with facing 0,
    /// for the zero vector.
    /// </summary>
    public static bool TryFromDirection(Vec d, out int facing)
    {
        facing = 0;
        if (d.X.Raw == 0 && d.Y.Raw == 0)
        {
            return false;
        }
        long bestDot = 0;
        for (var i = 0; i < Count; i++)
        {
            var dot = d.X.Raw * Dirs[i].X.Raw + d.Y.Raw * Dirs[i].Y.Raw;
            if (i == 0 || dot > bestDot)
            {
                facing = i;
                bestDot = dot;
            }
        }
        return true;
    }

    /// <summary>Returns the signed shortest turn from cur to want, in [-16, 15].</summary>
    public static int Diff(int cur, int want)
    {
        var d = Wrap(want - cur);
        if (d >= Count / 2)
        {
            d -= Count;
        }
        return d;
    }

    /// <summary>Rotates cur toward want by at most rate facings, the short way.</summary>
    public static int TurnToward(int cur, int want, int rate)
    {
        var d = Math.Max(-rate, Math.Min(rate, Diff(cur, want)));
        return Wrap(cur + d);
    }
}
