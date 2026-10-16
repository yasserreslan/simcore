using SimCore.Rng;

namespace SimCore.Tests;

public class RngTests
{
    // KnownSequence pins the output so a change to the generator, which would
    // silently break every saved replay, fails loudly. The values are the
    // ones the original Go implementation produced.
    [Fact]
    public void KnownSequence()
    {
        var r = new Rand(42);
        ulong[] want = [0xbdd732262feb6e95, 0x28efe333b266f103, 0x47526757130f9f52];
        foreach (var w in want)
        {
            Assert.Equal(w, r.NextUInt64());
        }
    }

    [Fact]
    public void RangeBounds()
    {
        var r = new Rand(7);
        for (var i = 0; i < 10_000; i++)
        {
            Assert.InRange(r.Range(-3, 3), -3, 3);
        }
    }
}
