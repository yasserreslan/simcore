using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// ENG-SMALL-4 (the owner: "half a truck yes"; the coordinator's call after GAME-BATCH-2): WPN-2's 13% a rocket gave half a truck
// only when all four rockets hit the hull's centre. As played (the game's --demo=heli-duel --heliduel-pods) the 1.5-cell scatter and
// the splash's falloff cut a salvo to 9% of a cargo truck and 32% of a technical. Here the real thing, 40 seeds: the AH-64 type,
// its missiles and cannon emptied so only the pods fire, on an Attack order at a truck, stopped or driving across its front; the
// truck's loss to the first salvo (its four rockets landed, before the second). The average is to be about half (45-55%).
public class HeliRocketPodSalvoTests(ITestOutputHelper output)
{
    private const int Seeds = 40;

    // One salvo's share (percent of max HP) of truck `id` on seed `seed`; moving: the truck drives across the helicopter's front.
    private static int Salvo(string id, bool moving, ulong seed) => SalvoAt(id, moving, seed).Share;

    private static (int Share, long Moved) SalvoAt(string id, bool moving, ulong seed)
    {
        var w = Arena.NewSentinel(96, seed);
        var u = w.Spawn(1, "attack_helicopter", 16, 48, Facing.East);
        w.Step();
        var pods = -1;
        for (var i = 0; i < u.Def.Weapons.Count; i++)
        {
            var x = u.Def.Weapons[i];
            if (x.Warhead.Id == "pod_rocket")
            {
                pods = x.Stock;
            }
            else if (x.Stock >= 0)
            {
                u.MutableCharges[x.Stock] = 0; // the missiles and the cannon: only the pods fire
            }
        }
        var t = w.SpawnCrewed(2, id, 46, moving ? 30 : 48).Vehicle; // crewed: a truck with nobody aboard does not drive
        if (moving)
        {
            w.Order(2, CommandKind.Move, [t], cell: new Cell(46, 90));
        }
        w.Order(1, CommandKind.Attack, [u], target: t.Id);
        var full = u.Charges[pods];
        var at = t.Pos;
        var hp = t.HP;
        var left = 0;
        for (var i = 0; i < 1200 && (left == 0 ? u.Charges[pods] > full - 4 : --left > 0); i++)
        {
            w.Step();
            hp = u.Charges[pods] == full ? t.HP : hp;
            left = left == 0 && u.Charges[pods] <= full - 4 ? 30 : left; // the 4th rocket away: 1.5 s for them to land (reload 3 s)
        }
        Assert.True(u.Charges[pods] == full - 4, $"{id} seed {seed}: {full - u.Charges[pods]} rockets fired");
        return ((hp - Math.Max(0, t.HP)) * 100 / t.Def.MaxHp, (t.Pos - at).Len().Raw);
    }

    private (double Mean, int Min, int Max, double Sd) Spread(string id, bool moving)
    {
        var runs = Enumerable.Range(1, Seeds).Select(k => SalvoAt(id, moving, (ulong)k)).ToList();
        var s = runs.Select(x => x.Share).ToList();
        var mean = s.Average();
        var sd = Math.Sqrt(s.Sum(x => (x - mean) * (x - mean)) / s.Count);
        output.WriteLine($"{id} {(moving ? "moving" : "stopped")}: mean {mean:F1}% min {s.Min()} max {s.Max()} sd {sd:F1}; " +
            $"killed {s.Count(x => x >= 100)}; moved {runs.Average(x => x.Moved) / (double)Fixed.OneRaw:F1} cells; {string.Join(" ", s)}");
        return (mean, s.Min(), s.Max(), sd);
    }

    // The cargo truck (200 HP), as in the game's demo: 45-55% stopped and driving. Measured at 22% a rocket (40 seeds): stopped mean
    // 54.1% (31 to 74, sd 11.6), driving 48.7% (22 to 77, sd 14.5); at WPN-2's 13% 31.8% and 30.3%. None killed by one salvo (four
    // direct hits take 88%). The technical (180 HP) comes out lower, 46.1% stopped and 42.0% driving (26.8 and 25.9 at 13%): held to
    // 40-55 here.
    [Theory]
    [InlineData("cargo_truck", false, 45)]
    [InlineData("cargo_truck", true, 45)]
    [InlineData("technical", false, 40)]
    [InlineData("technical", true, 40)]
    public void TheAverageSalvoTakesAboutHalfATruck(string id, bool moving, int least)
    {
        var (mean, _, max, _) = Spread(id, moving);
        Assert.InRange(mean, least, 55);
        Assert.True(max < 100, $"{id}: one salvo killed it");
    }
}
