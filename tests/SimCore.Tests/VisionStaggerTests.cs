using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// Vision and idle target search are staggered by unit ID (VisionSystem.VisionTicks): a viewer
// that stands still looks around every VisionTicks ticks, so spotting, losing sight and picking a
// target may lag by up to VisionTicks - 1 ticks, never more, and the same match plays the same.
public class VisionStaggerTests
{
    private static int Within(World w, Func<bool> done, int max)
    {
        for (var i = 1; i <= max; i++)
        {
            w.Step();
            if (done())
            {
                return i;
            }
        }
        return -1;
    }

    // Whatever the phase, a standing viewer spots an enemy that appears in its sight within
    // VisionTicks ticks, and on at least one phase it takes more than one tick (it is staggered).
    [Fact]
    public void AStandingViewerSpotsANewEnemyWithinVisionTicks()
    {
        var lags = new List<int>();
        for (var offset = 0; offset < (int)World.VisionTicks; offset++)
        {
            var w = Arena.New(32);
            w.Spawn(1, "Rifleman", 5, 16);
            w.Run(1 + offset);
            var them = w.Spawn(2, "worker", 8, 16);
            var lag = Within(w, () => World.IsSpotted(them, 1), (int)World.VisionTicks);
            Assert.InRange(lag, 1, (int)World.VisionTicks);
            lags.Add(lag);
        }
        Assert.Contains(lags, l => l > 1);
    }

    // An enemy walking out of sight is dropped within VisionTicks ticks of leaving the disc.
    [Fact]
    public void AnEnemyLeavingSightIsDroppedWithinVisionTicks()
    {
        var w = Arena.New(48);
        var watcher = w.Spawn(1, "Rifleman", 5, 24);
        var them = w.Spawn(2, "worker", 8, 24);
        w.Run((int)World.VisionTicks);
        Assert.True(World.IsSpotted(them, 1));
        w.Order(2, CommandKind.Move, [them], cell: new Cell(44, 24));
        var sight = watcher.Def.Vision.Raw + FixedPoint.Fixed.OneRaw; // a cell of slack for the 3D distance
        var outside = 0;
        for (var i = 0; i < 400 && outside < (int)World.VisionTicks; i++)
        {
            w.Step();
            if ((them.Pos - watcher.Pos).Len().Raw > sight)
            {
                outside++;
            }
        }
        Assert.Equal((int)World.VisionTicks, outside);
        Assert.False(World.IsSpotted(them, 1));
    }

    // An idle armed unit picks an enemy that walks into its reach within VisionTicks ticks.
    [Fact]
    public void AnIdleUnitPicksATargetWithinVisionTicks()
    {
        for (var offset = 0; offset < (int)World.VisionTicks; offset++)
        {
            var w = Arena.New(32);
            var shooter = w.Spawn(1, "Rifleman", 5, 16);
            w.Run(1 + offset);
            var them = w.Spawn(2, "worker", 8, 16);
            var lag = Within(w, () => shooter.AutoTarget == them.Id, (int)World.VisionTicks);
            Assert.InRange(lag, 1, (int)World.VisionTicks);
        }
    }

    // Two runs of the same fight from the same seed hash the same on every tick.
    [Fact]
    public void TheSameFightPlaysTheSame()
    {
        static List<ulong> Play()
        {
            var w = Arena.New(40, seed: 7);
            for (var i = 0; i < 6; i++)
            {
                w.Spawn(1, "Rifleman", 8 + i, 12 + 2 * i);
                w.Spawn(2, "Rifleman", 22 + i, 13 + 2 * i);
            }
            var hashes = new List<ulong>();
            for (var t = 0; t < 300; t++)
            {
                w.Step();
                hashes.Add(w.Hash());
            }
            return hashes;
        }
        Assert.Equal(Play(), Play());
    }
}
