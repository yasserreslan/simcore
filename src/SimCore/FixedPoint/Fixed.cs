// Deterministic fixed-point math for the simulation.
//
// The sim never uses floating point: results can differ between CPU
// architectures (Mac ARM vs Windows x86), and any difference desyncs a
// lockstep match. Integer math is bit-identical everywhere.
namespace SimCore.FixedPoint;

/// <summary>
/// A signed fixed-point number with <see cref="Shift"/> fractional bits,
/// stored in a 64-bit integer. One map cell is <see cref="One"/> raw units wide.
/// </summary>
/// <remarks>
/// <c>*</c> and <c>/</c> are fixed-point operations (the result is rescaled).
/// Arithmetic wraps on overflow, like the original Go <c>int64</c>.
/// </remarks>
public readonly record struct Fixed : IComparable<Fixed>
{
    /// <summary>The number of fractional bits.</summary>
    public const int Shift = 10;

    /// <summary>The raw value of 1.0.</summary>
    public const long OneRaw = 1L << Shift;

    /// <summary>Zero.</summary>
    public static readonly Fixed Zero;

    /// <summary>1.0. One map cell is this many raw units wide.</summary>
    public static readonly Fixed One = new(OneRaw);

    private Fixed(long raw) => Raw = raw;

    /// <summary>The underlying integer, in units of 1/<see cref="OneRaw"/>.</summary>
    public long Raw { get; }

    /// <summary>Wraps a raw value without scaling it.</summary>
    public static Fixed FromRaw(long raw) => new(raw);

    /// <summary>Converts an integer to <see cref="Fixed"/>.</summary>
    public static Fixed FromInt(int i) => new((long)i << Shift);

    /// <summary>Returns num/den, truncated toward zero.</summary>
    public static Fixed Frac(int num, int den) => new(((long)num << Shift) / den);

    /// <summary>Returns the integer part, rounded toward negative infinity.</summary>
    public int Floor() => (int)(Raw >> Shift);

    /// <summary>Returns |a|.</summary>
    public Fixed Abs() => Raw < 0 ? new(-Raw) : this;

    /// <summary>Returns floor(sqrt(n)) for a plain integer n.</summary>
    /// <exception cref="ArgumentOutOfRangeException">n is negative.</exception>
    public static long Sqrt(long n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (n < 2)
        {
            return n;
        }
        // Newton's method from a power of two at or above the root: the iterates fall
        // monotonically to floor(sqrt(n)) in a handful of steps.
        var x = 1L << ((System.Numerics.BitOperations.Log2((ulong)n) >> 1) + 1);
        var y = (x + n / x) / 2;
        while (y < x)
        {
            x = y;
            y = (x + n / x) / 2;
        }
        return x;
    }

    /// <summary>Returns a+b.</summary>
    public static Fixed operator +(Fixed a, Fixed b) => new(unchecked(a.Raw + b.Raw));

    /// <summary>Returns a-b.</summary>
    public static Fixed operator -(Fixed a, Fixed b) => new(unchecked(a.Raw - b.Raw));

    /// <summary>Returns -a.</summary>
    public static Fixed operator -(Fixed a) => new(unchecked(-a.Raw));

    /// <summary>Returns a×b, rounded toward negative infinity.</summary>
    public static Fixed operator *(Fixed a, Fixed b) => new(unchecked(a.Raw * b.Raw) >> Shift);

    /// <summary>Returns a/b, truncated toward zero.</summary>
    public static Fixed operator /(Fixed a, Fixed b) => new((a.Raw << Shift) / b.Raw);

    /// <summary>Compares raw values.</summary>
    public static bool operator <(Fixed a, Fixed b) => a.Raw < b.Raw;

    /// <summary>Compares raw values.</summary>
    public static bool operator >(Fixed a, Fixed b) => a.Raw > b.Raw;

    /// <summary>Compares raw values.</summary>
    public static bool operator <=(Fixed a, Fixed b) => a.Raw <= b.Raw;

    /// <summary>Compares raw values.</summary>
    public static bool operator >=(Fixed a, Fixed b) => a.Raw >= b.Raw;

    /// <inheritdoc/>
    public int CompareTo(Fixed other) => Raw.CompareTo(other.Raw);

    /// <summary>Formats the raw value, so it round-trips exactly.</summary>
    public override string ToString() => Raw.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
