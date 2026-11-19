using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Getting on a bridge (docs/design.md, "Bridges"; decision 1107): rails beside its footprint's long sides, all along it but the
// cell at each end (the ramp's foot, room to turn off), are a wall to every ground class, so the ramps and the deck are reached
// only over the ends; a vehicle on its way across lines up on the road a few cells short of the near end and keeps to its lane
// until it is off the far one.
// The river runs along rows Mid-1 to Mid+1, the road north-south along column Mid, each kind's footprint centred on (Mid, Mid).
public class BridgeEntryTests(ITestOutputHelper output)
{
    private const int Size = 128, Mid = 64;

    public static TheoryData<string> Kinds => new() { "stone", "concrete", "metal" };

    private static (int W, int H) FootOf(string kind) => kind switch { "stone" => (3, 8), "concrete" => (5, 12), _ => (9, 18) };

    private static Cell OriginOf(string kind) => new(Mid - FootOf(kind).W / 2, Mid - FootOf(kind).H / 2);

    private static World Bridge(string kind) => BridgeTests.River(Size, Mid, sites: [new BridgeSite(kind, OriginOf(kind), Facing.South)]);

    // What a drive over the bridge saw: where it came onto the footprint and onto the deck (x off the road's middle, the
    // heading's angle to the road in degrees), whether it was ever beside the footprint within its length, and when it was over.
    private sealed record Drive(double FootOff, double FootAngle, double DeckOff, double DeckAngle, bool Beside, int Over);

    private static Drive Run(World w, string kind, string vehicle, int x, int y, int limit = 2400)
    {
        var (fw, fh) = FootOf(kind);
        var o = OriginOf(kind);
        var (v, _) = w.SpawnCrewed(1, vehicle, x, y, Facing.South);
        w.Order(1, CommandKind.Move, [v], cell: new Cell(Mid, Mid + 30));
        double footOff = double.NaN, footAngle = 0, deckOff = double.NaN, deckAngle = 0;
        var beside = false;
        for (var t = 0; t < limit; t++)
        {
            w.Step();
            double px = v.Pos.X.Raw / (double)Fixed.OneRaw, py = v.Pos.Y.Raw / (double)Fixed.OneRaw;
            var angle = Math.Atan2(Math.Abs(v.Heading.X.Raw), v.Heading.Y.Raw) * 180 / Math.PI;
            var along = py >= o.Y && py < o.Y + fh;
            var across = px >= o.X && px < o.X + fw;
            beside |= along && !across;
            if (along && across && double.IsNaN(footOff))
            {
                (footOff, footAngle) = (px - (Mid + 0.5), angle);
            }
            if (w.Ground!.Deck(Cell.Of(v.Pos)) && double.IsNaN(deckOff))
            {
                (deckOff, deckAngle) = (px - (Mid + 0.5), angle);
            }
            Assert.False(w.InWater(v.Pos), $"{vehicle} in the water at {Cell.Of(v.Pos)}");
            if (py >= Mid + 28)
            {
                return new Drive(footOff, footAngle, deckOff, deckAngle, beside, t);
            }
        }
        return new Drive(footOff, footAngle, deckOff, deckAngle, beside, -1);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheRailsBesideItsLongSidesAreAWallToEveryGroundClassAndItsEndsAreOpen(string kind)
    {
        var w = Bridge(kind);
        var g = w.Ground!;
        var (fw, fh) = FootOf(kind);
        var o = OriginOf(kind);
        for (var y = o.Y + 1; y < o.Y + fh - 1; y++)
        {
            foreach (var x in new[] { o.X - 1, o.X + fw })
            {
                var c = new Cell(x, y);
                Assert.True(g.Rail(c), $"no rail at {c}");
                for (var k = 0; k < g.Rules.Classes.Count; k++)
                {
                    Assert.Equal(TerrainMap.Impassable, g.Cost(k, c));
                }
            }
        }
        // The cells beside the deck's ends (those decision 915 opened) are rails; beside the ramps' feet and past them the bank is
        // open, and so are the ends.
        Assert.True(g.Rail(new Cell(o.X - 1, Mid - 2)) && g.Rail(new Cell(o.X + fw, Mid + 2)));
        Assert.False(g.Rail(new Cell(o.X - 1, o.Y)) || g.Rail(new Cell(o.X + fw, o.Y + fh - 1)) || g.Rail(new Cell(o.X - 1, o.Y - 1)));
        var truck = g.Rules.ClassOf(w.Player(1)!.Setup.Find(w.Content, "cargo_truck")!);
        Assert.True(g.Cost(truck, new Cell(Mid, o.Y - 1)) > 0 && g.Cost(truck, new Cell(Mid, o.Y)) > 0 && g.Cost(truck, new Cell(Mid, o.Y + fh)) > 0);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void StraightOnATruckGetsOnAtTheMiddleAlongTheRoad(string kind)
    {
        var d = Run(Bridge(kind), kind, "cargo_truck", Mid, Mid - 30);
        output.WriteLine($"{kind}: {d}");
        Assert.True(d.Over > 0, "not over");
        Assert.False(d.Beside);
        Assert.True(Math.Abs(d.FootOff) <= 0.5 && d.FootAngle < 3 && Math.Abs(d.DeckOff) <= 0.5 && d.DeckAngle < 3, $"{d}");
    }

    [Theory]
    [InlineData("stone", "cargo_truck", 14)]
    [InlineData("stone", "heavy_rocket_truck", -14)]
    [InlineData("concrete", "cargo_truck", -14)]
    [InlineData("concrete", "heavy_rocket_truck", 14)]
    [InlineData("metal", "cargo_truck", 14)]
    [InlineData("metal", "heavy_rocket_truck", -14)]
    [InlineData("metal", "ballistic_truck", 10)]
    public void FromAnAngleItLinesUpShortOfTheEndAndNeverComesOnOverASide(string kind, string vehicle, int aside)
    {
        var (fw, fh) = FootOf(kind);
        var straight = Run(Bridge(kind), kind, vehicle, Mid, OriginOf(kind).Y - 18);
        var d = Run(Bridge(kind), kind, vehicle, Mid + aside, OriginOf(kind).Y - 18);
        output.WriteLine($"{kind} {vehicle} {aside}: {d} (straight on {straight.Over} ticks)");
        Assert.True(d.Over > 0, "not over");
        Assert.False(d.Beside, "it came onto the footprint over a long side");
        // On near the middle (within a quarter of the width, the lane a vehicle keeps, and half a cell) and along the road.
        Assert.True(Math.Abs(d.FootOff) <= fw / 4.0 + 0.5 && d.FootAngle < 8, $"onto the ramp at {d.FootOff:0.00} off, {d.FootAngle:0} degrees");
        Assert.True(Math.Abs(d.DeckOff) <= fw / 4.0 + 0.5 && d.DeckAngle < 5, $"onto the deck at {d.DeckOff:0.00} off, {d.DeckAngle:0} degrees");
        Assert.True(d.Over <= straight.Over * 13 / 10 + 40, $"{d.Over} ticks against {straight.Over} straight on");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void AFallenSpanStillBlocksAndItsRailsStay(string kind)
    {
        var w = Bridge(kind);
        var b = Assert.Single(w.Bridges);
        b.HP = 0;
        w.Step();
        Assert.True(b.Bridge!.Down);
        var d = Run(w, kind, "cargo_truck", Mid, Mid - 30, 1200);
        Assert.Equal(-1, d.Over);
        Assert.True(double.IsNaN(d.DeckOff), "it got onto a fallen span");
        Assert.True(w.Ground!.Rail(new Cell(OriginOf(kind).X - 1, Mid - 2)));
    }
}
