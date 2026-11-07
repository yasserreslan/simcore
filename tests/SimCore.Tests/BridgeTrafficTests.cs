using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Bridge traffic (docs/design.md, "Bridge traffic"): columns that meet on a span too narrow to pass on take turns, the others
// waiting on the bank clear of the way off it; on one wide enough they keep right and pass. Alone or all going one way nothing
// changes. The river runs along row Mid (rows Mid-1 to Mid+1), the road north-south along column Mid, the bridges' deck on those rows.
public class BridgeTrafficTests
{
    private const int Size = 192, Mid = 96;
    private const string Tank = "main_battle_tank";

    private static readonly BridgeSite Stone = new("stone", new Cell(Mid - 1, Mid - 4), Facing.South);
    private static readonly BridgeSite Concrete = new("concrete", new Cell(Mid - 2, Mid - 6), Facing.South);

    private static World Bridge(BridgeSite site) => BridgeTests.River(Size, Mid, sentinel: true, sites: [site]);

    // A column of n, two abreast, starting from the north (going south, +1) or south of the river, ordered over to the far bank.
    private static List<Unit> Column(World w, string[] kinds, int n, int dir, int start = 28)
    {
        var units = new List<Unit>();
        for (var i = 0; i < n; i++)
        {
            int x = Mid - 2 + i % 2 * 5, y = Mid - dir * (start + i / 2 * 5);
            var name = kinds[i % kinds.Length];
            units.Add(name == "Rifleman" ? w.Spawn(1, name, x, y, dir > 0 ? Facing.South : Facing.North) : w.SpawnCrewed(1, name, x, y, dir > 0 ? Facing.South : Facing.North).Vehicle);
        }
        w.Order(1, CommandKind.Move, units, cell: new Cell(Mid, Mid + dir * 36));
        return units;
    }

    private static bool OnDeck(Unit u)
    {
        var c = Cell.Of(u.Pos);
        return c.Y is >= Mid - 1 and <= Mid + 1;
    }

    private static bool Over(Unit u, int dir) => (Cell.Of(u.Pos).Y - Mid) * dir > 10;

    // Runs until both columns are over (or the limit): the ticks each took.
    private static (int South, int North) Cross(World w, List<Unit> south, List<Unit> north, int limit, Action? each = null)
    {
        int s = -1, n = -1;
        for (var t = 0; t < limit && (s < 0 || n < 0); t++)
        {
            w.Step();
            each?.Invoke();
            if (s < 0 && south.All(u => Over(u, 1)))
            {
                s = t;
            }
            if (n < 0 && north.All(u => Over(u, -1)))
            {
                n = t;
            }
        }
        return (s, n);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void OnAOneLaneBridgeTheTwoColumnsTakeTurnsAndNobodyTurnsRoundOrIsStuck(int n)
    {
        var w = Bridge(Stone);
        var south = Column(w, [Tank], n, 1);
        var north = Column(w, [Tank], n, -1);
        int both = 0, turned = 0, stuck = 0;
        var (s, nn) = Cross(w, south, north, 2000, () =>
        {
            both += south.Any(OnDeck) && north.Any(OnDeck) ? 1 : 0;
            turned += south.Count(u => OnDeck(u) && u.Heading.Y.Raw < 0) + north.Count(u => OnDeck(u) && u.Heading.Y.Raw > 0);
            stuck += south.Concat(north).Count(u => u.Intent.Stuck);
        });
        Assert.True(s >= 0 && nn >= 0, $"not over in time: south {s}, north {nn}");
        Assert.Equal((0, 0, 0), (both, turned, stuck));
        Assert.True(w.Bridges[0].Bridge!.Flow == 0, "the contest ends when they are over");
    }

    [Fact]
    public void WithoutTheRuleTheSameColumnsJam()
    {
        static int Last(bool rule)
        {
            var w = Bridge(Stone);
            w.NoBridgeTraffic = !rule;
            var (s, n) = Cross(w, Column(w, [Tank], 4, 1), Column(w, [Tank], 4, -1), 2000);
            return s < 0 || n < 0 ? int.MaxValue : Math.Max(s, n);
        }
        int with = Last(true), without = Last(false);
        Assert.True(without > with * 3 / 2, $"with the rule {with} ticks, without it {without}");
    }

    [Fact]
    public void TheWaitingColumnStandsOnTheBankToItsRightClearOfTheWayOffTheDeck()
    {
        var w = Bridge(Stone);
        var south = Column(w, [Tank], 4, 1);
        var north = Column(w, [Tank], 4, -1);
        var b = w.Bridges[0].Bridge!;
        for (var t = 0; t < 1000 && !south.Any(u => Over(u, 1)) && !north.Any(u => Over(u, -1)); t++)
        {
            w.Step();
        }
        Assert.NotEqual(0, b.Flow);
        var (waiting, way) = b.Flow > 0 ? (north, -1) : (south, 1);
        foreach (var u in waiting)
        {
            var cross = u.Pos.X.Raw - b.Cross;
            var right = way > 0 ? -cross : cross; // going north the right is east
            Assert.True(right >= b.HalfWidth + u.Def.Archetype.Radius.Raw, $"{u.Id} {right / (double)Fixed.OneRaw} cells to its right, in the way off the deck");
            Assert.True(Math.Abs(b.Along(u.Pos)) > b.HalfLength + Fixed.OneRaw * 5, $"{u.Id} too close to the span");
            Assert.False(u.Intent.Stuck);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void OnATwoLaneBridgeTheyKeepRightAndPassAboutAsFastAsAlone(int n)
    {
        var clear = Bridge(Concrete);
        var alone = Column(clear, [Tank], n, 1);
        var (t0, _) = Cross(clear, alone, [], 1500);

        var w = Bridge(Concrete);
        var south = Column(w, [Tank], n, 1);
        var north = Column(w, [Tank], n, -1);
        var b = w.Bridges[0].Bridge!;
        var wrongSide = 0;
        var (s, nn) = Cross(w, south, north, 1500, () =>
            wrongSide += south.Count(u => OnDeck(u) && u.Pos.X.Raw > b.Cross) + north.Count(u => OnDeck(u) && u.Pos.X.Raw < b.Cross));
        Assert.True(s >= 0 && nn >= 0, $"not over in time: south {s}, north {nn}");
        Assert.True(Math.Max(s, nn) * 10 <= t0 * 14, $"head-on {Math.Max(s, nn)} ticks, alone {t0}"); // they go in single file: on the game's map within 1.05
        Assert.Equal(0, wrongSide);
    }

    [Fact]
    public void WhoFitsBesideTheOthersGoesOnWhoDoesNotWaits()
    {
        // Soldiers fit beside each other on the stone bridge and nobody waits; two trucks wait on it, but not on the concrete one.
        var foot = Bridge(Stone);
        var a = Column(foot, ["Rifleman"], 8, 1);
        var b = Column(foot, ["Rifleman"], 8, -1);
        var stopped = 0;
        var (s, n) = Cross(foot, a, b, 1500, () => stopped += a.Concat(b).Count(u => u.Moving && u.Intent.Desired == default));
        Assert.True(s >= 0 && n >= 0, $"soldiers not over: {s}, {n}");
        Assert.Equal(0, stopped);

        var w = Bridge(Stone);
        var c = Column(w, ["sentinel_cargo_truck"], 4, 1);
        var d = Column(w, ["sentinel_cargo_truck"], 4, -1);
        var both = 0;
        (s, n) = Cross(w, c, d, 1500, () => both += c.Any(OnDeck) && d.Any(OnDeck) ? 1 : 0);
        Assert.True(s >= 0 && n >= 0, $"trucks not over: {s}, {n}");
        Assert.Equal(0, both);

        var wide = Bridge(Concrete);
        var e = Column(wide, ["sentinel_cargo_truck"], 4, 1);
        var f = Column(wide, ["sentinel_cargo_truck"], 4, -1);
        stopped = 0;
        (s, n) = Cross(wide, e, f, 1500, () => stopped += e.Concat(f).Count(u => u.Moving && u.Intent.Desired == default));
        Assert.True(s >= 0 && n >= 0, $"trucks not over on concrete: {s}, {n}");
        Assert.Equal(0, stopped);
    }

    [Fact]
    public void AMixedColumnCrossesAndNobodyIsLeftBehind()
    {
        var w = Bridge(Stone);
        string[] mix = ["humvee", "sentinel_cargo_truck", Tank, "Rifleman"];
        var south = Column(w, mix, 8, 1);
        var north = Column(w, mix, 8, -1);
        var stuck = ""; // (a vehicle squeezing past the far end of the other column's queue may re-plan once: that's on the open bank)
        var (s, n) = Cross(w, south, north, 2000, () => stuck += string.Join(" ", south.Concat(north).Where(u => u.Intent.Stuck && u.Def.Archetype.Locomotor != Locomotor.Foot && Math.Abs(Cell.Of(u.Pos).Y - Mid) <= 10).Select(u => $"{w.Tick}:{u.Id}:{u.Def.Archetype.Id}@{Cell.Of(u.Pos)} ")));
        Assert.True(s >= 0 && n >= 0, $"not over in time: south {s}, north {n}");
        Assert.True(stuck.Length == 0, stuck);
    }

    [Fact]
    public void ALongColumnDoesNotKeepTheOtherSideWaitingForEver()
    {
        var w = Bridge(Stone);
        var longOne = Column(w, [Tank], 30, 1, start: 12);
        var shortOne = Column(w, [Tank], 2, -1, start: 16);
        int s = -1, n = -1;
        for (var t = 0; t < 3000 && (s < 0 || n < 0); t++)
        {
            w.Step();
            s = s < 0 && longOne.All(u => Over(u, 1)) ? t : s;
            n = n < 0 && shortOne.All(u => Over(u, -1)) ? t : n;
        }
        Assert.True(s >= 0 && n >= 0, $"not over in time: long {s}, short {n}");
        Assert.True(n < s, $"the short one waited for all of the long one: {n}, {s}");
        Assert.True(n <= 2 * w.Content.UnitAi.BridgeTurnTicks, $"the short one over only after {n} ticks");
    }

    [Fact]
    public void AloneOrAllOneWayNothingChanges()
    {
        static ulong[] Play(bool rule)
        {
            var w = Bridge(Stone);
            w.NoBridgeTraffic = !rule;
            Column(w, [Tank, "sentinel_cargo_truck"], 6, 1);
            var hashes = new ulong[8];
            for (var i = 0; i < hashes.Length; i++)
            {
                w.Run(100);
                Assert.Equal(0, w.Bridges[0].Bridge!.Flow);
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        Assert.Equal(Play(false), Play(true));
    }

    [Fact]
    public void ItPlaysTheSameInAnyVisitingOrderAndItsStateIsHashed()
    {
        static ulong[] Play(bool reverse)
        {
            var w = Bridge(Stone);
            w.ReverseMoveOrder = reverse;
            Column(w, [Tank, "humvee"], 6, 1);
            Column(w, [Tank, "humvee"], 6, -1);
            var hashes = new ulong[10];
            for (var i = 0; i < hashes.Length; i++)
            {
                w.Run(60);
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        Assert.Equal(Play(false), Play(false));
        Assert.Equal(Play(false), Play(true));

        var a = Bridge(Stone);
        var b = Bridge(Stone);
        a.Step();
        b.Step();
        Assert.Equal(a.Hash(), b.Hash());
        b.Bridges[0].Bridge!.Flow = 1;
        Assert.NotEqual(a.Hash(), b.Hash());
        var flowed = b.Hash();
        b.Bridges[0].Bridge!.TurnSince = 7;
        Assert.NotEqual(flowed, b.Hash());
    }

    [Fact]
    public void AnIdleTankOnTheDeckIsPushedAlongAndAFiringOneToo()
    {
        foreach (var firing in new[] { false, true })
        {
            var w = BridgeTests.River(Size, Mid, sentinel: true, sites: [Stone, new BridgeSite("concrete", new Cell(Mid + 40, Mid - 6), Facing.South)]);
            var (idle, _) = w.SpawnCrewed(1, Tank, Mid, Mid, Facing.South);
            if (firing)
            {
                w.Order(1, CommandKind.Attack, [idle], target: w.Bridges[1].Id); // from where it stands: a target that shoots back would end the test early
                w.Run(20);
                Assert.Equal(OrderKind.Attack, idle.Order.Kind);
            }
            var south = Column(w, [Tank], 4, 1);
            var (s, _) = Cross(w, south, [], 1200);
            Assert.True(s >= 0, $"firing {firing}: the column did not get past");
        }
    }

    [Theory]
    [InlineData(1)] // its own
    [InlineData(2)] // the enemy's: no unit shoves an enemy anywhere else
    public void ATruckNobodyDrivesOnTheDeckIsShovedOffIt(byte owner)
    {
        var w = Bridge(Stone);
        var empty = w.Spawn(owner, owner == 1 ? "sentinel_cargo_truck" : "technical", Mid, Mid, Facing.South);
        Assert.False(empty.CanAct);
        var south = Column(w, [Tank], 4, 1);
        var (s, _) = Cross(w, south, [], 1500);
        Assert.True(s >= 0, "the column did not get past it");
        // Off the span it stands as an empty vehicle does: nothing moves it.
        var c = empty.Pos;
        w.Run(100);
        Assert.Equal(c, empty.Pos);
    }

    [Fact]
    public void ASpanFallingEndsTheContest()
    {
        var w = Bridge(Stone);
        Column(w, [Tank], 4, 1);
        Column(w, [Tank], 4, -1);
        var b = w.Bridges[0];
        for (var t = 0; t < 500 && b.Bridge!.Flow == 0; t++)
        {
            w.Step();
        }
        Assert.NotEqual(0, b.Bridge!.Flow);
        b.HP = 0;
        w.Run(5);
        Assert.True(b.Bridge.Down);
        Assert.Equal(0, b.Bridge.Flow);
    }
}
