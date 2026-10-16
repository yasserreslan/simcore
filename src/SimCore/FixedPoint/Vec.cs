namespace SimCore.FixedPoint;

/// <summary>A 2D vector in fixed-point. X grows right, Y grows down (screen space).</summary>
/// <param name="X">The horizontal component.</param>
/// <param name="Y">The vertical component.</param>
public readonly record struct Vec(Fixed X, Fixed Y)
{
    /// <summary>Returns a+b.</summary>
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>Returns a-b.</summary>
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Returns the squared length in raw units.</summary>
    public long LenSq() => unchecked(X.Raw * X.Raw + Y.Raw * Y.Raw);

    /// <summary>Returns the length, rounded down.</summary>
    public Fixed Len() => Fixed.FromRaw(Fixed.Sqrt(LenSq()));

    /// <summary>Returns this vector scaled to length l. The zero vector stays zero.</summary>
    public Vec WithLen(Fixed l)
    {
        var n = Len().Raw;
        if (n == 0)
        {
            return default;
        }
        return new(Fixed.FromRaw(unchecked(X.Raw * l.Raw) / n), Fixed.FromRaw(unchecked(Y.Raw * l.Raw) / n));
    }
}
