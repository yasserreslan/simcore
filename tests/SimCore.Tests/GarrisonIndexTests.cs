using SimCore.FixedPoint;
using SimCore.Rng;
using SimCore.Sim;

namespace SimCore.Tests;

// RunGarrisons counts every building's occupants in one pass over the units (a town of a thousand buildings scanned every
// unit for every building: docs/design.md "Performance", the Zuberec town). These tests run the same worlds with the
// original per-building scan (World.FullGarrisonScan) and want the same hash every tick: soldiers of both sides going in,
// stages dropping under fire and by hand (the excess thrown out), occupants killed, a building destroyed.
public class GarrisonIndexTests
{
    private static readonly string[] Kinds = ["block_brick_4", "house_small", "shop_row", "villa_tiled", "block_panel_5", "apartment_3f"];

    private static World RandomWorld(int seed, bool full)
    {
        var w = Arena.New(160, (ulong)seed);
        w.FullGarrisonScan = full;
        var r = new Rand((ulong)seed);
        var buildings = new List<Unit>();
        for (var i = 0; i < 12; i++)
        {
            var b = w.Spawn(1, Kinds[(i + seed) % Kinds.Length], 8 + i % 4 * 38, 10 + i / 4 * 46);
            buildings.Add(b);
        }
        for (var i = 0; i < 90; i++)
        {
            var owner = (byte)(1 + r.Intn(2));
            var b = buildings[r.Intn(buildings.Count)];
            var p = World.FootprintOrigin(b);
            var u = w.Spawn(owner, i % 9 == 0 ? "Tank hunter" : i % 4 == 0 ? "Gunner" : "Rifleman", Math.Clamp(p.X + r.Range(-6, 20), 1, 158),
                Math.Clamp(p.Y + r.Range(18, 30), 1, 158), Facing.North);
            if (i % 3 != 2)
            {
                w.Order(owner, CommandKind.Enter, [u], b.Id);
            }
        }
        return w;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OnePassCountsMatchFullScansEveryTick(int seed)
    {
        var a = RandomWorld(seed, false);
        var b = RandomWorld(seed, true);
        Assert.Equal(b.Hash(), a.Hash());
        var occupied = 0;
        for (var t = 0; t < 700; t++)
        {
            foreach (var w in new[] { a, b })
            {
                var held = w.Units.Where(u => u.Def.Archetype.Garrison is not null && u.Occupants > 0).OrderBy(u => u.Id).ToList();
                if (t == 250 && held.Count > 0) { held[0].HP = held[0].Def.MaxHp / 4; } // a lower stage: the excess thrown out
                if (t == 300 && held.Count > 1) { held[1].HP = 0; } // destroyed: the collapse
                if (t == 350)
                {
                    foreach (var o in w.Units.Where(u => u.Garrison != 0).OrderBy(u => u.Id).Where((_, k) => k % 3 == 0).ToList()) { o.HP = 0; }
                }
                if (t == 400 && held.Count > 2) { held[^1].HP = held[^1].Def.MaxHp / 2; }
            }
            a.Step();
            b.Step();
            occupied = Math.Max(occupied, a.Units.Count(u => u.Garrison != 0));
            Assert.True(a.Hash() == b.Hash(), $"seed {seed}, tick {a.Tick}");
        }
        Assert.True(occupied >= 10, $"only {occupied} soldiers ever inside");
    }
}
