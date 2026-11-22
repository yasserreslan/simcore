using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-5 part 4 (docs/design.md rows 1301 to 1303; the game repo's infantry-combat-brief.md 6.8 and section 8, the battle rules'
// 6.4 band term): each main arm's range band is content, 6.4's band term scores how far a cell misses it (to the nearest known
// contact), and a line fighting in place comes into its band. All failed on 99885b0 (no bands; the marksman at 12 cells).
public class RangeBandTests(ITestOutputHelper output)
{
    [Fact]
    public void TheRangeBandsAreContentAndTheMarksmanReaches11Cells()
    {
        var r = TestContent.Mvp.InfantryCombat!;
        CombatWeapon W(string id) => r.Weapons.Single(w => w.Id == id);
        Assert.Equal((40, 100, 0, TooCloseAct.Hold), (W("res_rifle").BandMin, W("res_rifle").BandMax, W("res_rifle").TooClose, W("res_rifle").OnTooClose));
        Assert.Equal((20, 80), (W("res_smg").BandMin, W("res_smg").BandMax));
        Assert.Equal((50, 100, 20, TooCloseAct.Secondary), (W("res_lmg").BandMin, W("res_lmg").BandMax, W("res_lmg").TooClose, W("res_lmg").OnTooClose));
        Assert.Equal((70, 100, 40, TooCloseAct.BackOff), (W("res_sniper").BandMin, W("res_sniper").BandMax, W("res_sniper").TooClose, W("res_sniper").OnTooClose));
        Assert.Equal((50, 90, 60, 100), (W("at_missile").BandMin, W("at_missile").BandMax, W("at_missile").VehicleMin, W("at_missile").VehicleMax));
        Assert.Equal(0, W("res_carbine").BandMax); // no band given (the crews' sidearm): no band term
        Assert.Equal(Fixed.FromInt(11), W("res_sniper").Effect.Range); // the rules' table, row 1116's condition met (row 1301)
    }

    [Fact]
    public void TheBandTermMeasuresTheMissToTheNearestKnownContact()
    {
        var w = Arena.New(32);
        w.CombatOff = false; // the rules on (the arena turns them off), before any unit is added
        var u = w.Spawn(1, "Rifleman", 5, 10, Facing.East);
        var r = w.Content.InfantryCombat!;
        Assert.Equal(0, w.BandMiss(u, new Cell(5, 10), r)); // no contact known: no term
        u.Fight!.ContactList.Add(new Contact { Enemy = 99, At = new Cell(13, 10), Source = ContactSource.SeenNow, Class = CombatClass.Rifle });
        var (far, inBand, close) = (w.BandMiss(u, new Cell(5, 10), r), w.BandMiss(u, new Cell(9, 10), r), w.BandMiss(u, new Cell(12, 10), r));
        output.WriteLine($"miss at 8 cells {far}, at 4 {inBand}, at 1 {close}");
        Assert.Equal((60, 0, 20), (far, inBand, close)); // 160% of the range is 60 past 100; 20% is 20 short of 40
        u.Fight.ContactList.Add(new Contact { Enemy = 98, At = new Cell(9, 13), Source = ContactSource.SeenNow, Class = CombatClass.Rifle });
        Assert.Equal(0, w.BandMiss(u, new Cell(5, 10), r)); // measured to the nearer one: 5 cells, the band's far edge
    }
}
