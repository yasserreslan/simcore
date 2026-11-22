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

    // INF-E1b-6 (row 1313, brief 6.8's onTooClose backOff, the marksman's content): an enemy 3 cells off, inside his tooClose of 4:
    // he moves away 2 to 4 cells, behind the rock when there is one (cover toward the enemy), and gets there; then his band and cover
    // search again. On a334956 he held his cell in the open (no cover cell to search: no backOff).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMarksmanTooCloseBacksOffTwoToFourCellsToCover(bool rock)
    {
        var w = Arena.New(48);
        w.CombatOff = false;
        if (rock)
        {
            foreach (var y in new[] { 19, 20, 21 })
            {
                w.Grid.Set(new Cell(17, y), Terrain.Blocked);
            }
        }
        var m = w.Spawn(2, "Marksman", 20, 20, Facing.East);
        var e = w.Spawn(1, "Rifleman", 23, 20, Facing.West);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetStance, Units = [e.Id], Stance = Stance.HoldFire });
        e.HP = 20000;
        var start = (e.Pos - m.Pos).Len().Raw;
        uint moved = 0, there = 0;
        for (var i = 0; i < 400 && there == 0; i++)
        {
            w.Step();
            moved = moved == 0 && m.Moving ? w.Tick : moved;
            there = moved != 0 && !m.Moving && !m.Fight!.BackingOff ? w.Tick : 0;
        }
        var end = (e.Pos - m.Pos).Len().Raw;
        var c = Cell.Of(m.Pos);
        var axis = World.Dir32(23 - c.X, 20 - c.Y);
        output.WriteLine($"rock {rock} moved at {moved} there at {there} from {start * 10 / Fixed.OneRaw} to {end * 10 / Fixed.OneRaw} tenths, at {c}, protection {w.Protection(c, Posture.Kneel, axis, false)}");
        Assert.NotEqual(0u, moved);
        Assert.NotEqual(0u, there);
        Assert.InRange(end - start, 2 * Fixed.OneRaw - Fixed.OneRaw / 4, 4 * Fixed.OneRaw + Fixed.OneRaw / 4);
        if (rock)
        {
            Assert.True(c.X <= 16 && w.Protection(c, Posture.Kneel, axis, false) > 0, $"{c} behind the rock");
        }
    }
}
