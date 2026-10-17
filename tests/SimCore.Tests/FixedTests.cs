using SimCore.FixedPoint;

namespace SimCore.Tests;

public class FixedTests
{
    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(2L, 1L)]
    [InlineData(3L, 1L)]
    [InlineData(4L, 2L)]
    [InlineData(15L, 3L)]
    [InlineData(16L, 4L)]
    [InlineData(1L << 40, 1L << 20)]
    [InlineData((1L << 40) - 1, (1L << 20) - 1)]
    public void Sqrt(long input, long want) => Assert.Equal(want, Fixed.Sqrt(input));

    [Fact]
    public void SqrtIsTheFloorOfTheRootEverywhere()
    {
        // Squares and their neighbours over the whole range, plus a sweep of small values.
        for (long r = 0; r < 3_037_000_499; r = r < 5000 ? r + 1 : r + r / 997 + 1)
        {
            var sq = r * r;
            Assert.Equal(r, Fixed.Sqrt(sq));
            Assert.Equal(r, Fixed.Sqrt(sq + r)); // below (r + 1)^2 = sq + 2r + 1
            Assert.Equal(r, Fixed.Sqrt(sq + 2 * r));
            if (r > 0)
            {
                Assert.Equal(r - 1, Fixed.Sqrt(sq - 1));
            }
        }
        Assert.Equal(3_037_000_499L, Fixed.Sqrt(long.MaxValue));
    }

    [Fact]
    public void MulDiv()
    {
        Fixed a = Fixed.FromInt(3), b = Fixed.Frac(1, 2);
        Assert.Equal(Fixed.Frac(3, 2), a * b);
        Assert.Equal(Fixed.FromInt(6), a / b);
        Assert.Equal(-1, Fixed.Frac(-1, 2).Floor());
    }

    [Fact]
    public void DirsAreUnitLength()
    {
        for (var i = 0; i < Facing.Count; i++)
        {
            var l = Facing.Dir(i).Len().Raw;
            Assert.InRange(l, Fixed.OneRaw - 2, Fixed.OneRaw + 2);
        }
    }

    public static TheoryData<long, long, int> FacingToCases => new()
    {
        { 0, -Fixed.OneRaw, Facing.North },
        { Fixed.OneRaw, 0, Facing.East },
        { 0, Fixed.OneRaw, Facing.South },
        { -Fixed.OneRaw, 0, Facing.West },
        { Fixed.OneRaw, -Fixed.OneRaw, 4 },
        { -Fixed.OneRaw, Fixed.OneRaw, 20 },
    };

    [Theory]
    [MemberData(nameof(FacingToCases))]
    public void FacingTo(long x, long y, int want)
    {
        Assert.True(Facing.TryFromDirection(new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y)), out var got));
        Assert.Equal(want, got);
    }

    [Fact]
    public void FacingToRoundTripsEveryDir()
    {
        for (var i = 0; i < Facing.Count; i++)
        {
            Assert.True(Facing.TryFromDirection(Facing.Dir(i), out var got));
            Assert.Equal(i, got);
        }
        Assert.False(Facing.TryFromDirection(default, out _));
    }

    [Theory]
    [InlineData(0, 8, 1, 1)]
    [InlineData(0, 24, 1, 31)]
    [InlineData(30, 2, 2, 0)]
    [InlineData(5, 6, 4, 6)]
    [InlineData(0, 16, 32, 16)]
    public void TurnToward(int cur, int want, int rate, int output) =>
        Assert.Equal(output, Facing.TurnToward(cur, want, rate));
}
