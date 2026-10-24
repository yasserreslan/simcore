using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Road kinds (docs/design.md, "Roads, props and terrain"): highway, paved, gravel and dirt per road
// cell, read from the map's roads; speed and path cost by kind per movement class (terrain rules);
// plain roads stay paved and hash as before.
public class RoadKindTests(ITestOutputHelper output)
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static int Class(string id)
    {
        for (var i = 0; i < Rules.Classes.Count; i++)
        {
            if (Rules.Classes[i].Id == id)
            {
                return i;
            }
        }
        throw new ArgumentException(id);
    }

    // LoadJson writes a map JSON (flat: no heightmap) to a temporary directory and loads it.
    private static TerrainMap LoadJson(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "simcore-roads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "map.json"), json);
            return TerrainLoader.LoadMap(Path.Combine(dir, "map.json"), Rules);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Row lays a flat 1 x 5 map whose cells are off-road, highway, paved, gravel and dirt.
    private static TerrainMap Row()
    {
        var flags = new byte[5];
        flags[1] = TerrainMap.WithRoad(0, RoadKind.Highway);
        flags[2] = TerrainMap.WithRoad(0, RoadKind.Paved);
        flags[3] = TerrainMap.WithRoad(0, RoadKind.Gravel);
        flags[4] = TerrainMap.WithRoad(0, RoadKind.Dirt);
        return new TerrainMap(5, 1, new int[6 * 2], flags, Rules);
    }

    [Fact]
    public void MapRoadsTakeAKindPavedByDefaultAndTheFasterWinsWhereTheyCross()
    {
        var t = LoadJson("""
            {
              "width": 12, "height": 8,
              "roads": [
                { "points": [[0, 0], [11, 0]], "width": 1 },
                { "points": [[0, 2], [11, 2]], "width": 1, "kind": "highway" },
                { "points": [[0, 4], [11, 4]], "width": 1, "kind": "gravel" },
                { "points": [[0, 6], [11, 6]], "width": 1, "kind": "dirt" },
                { "points": [[5, 0], [5, 7]], "width": 1, "kind": "dirt" },
                { "rect": [9, 6, 2, 2], "kind": "paved" }
              ]
            }
            """);
        Assert.Equal(RoadKind.Paved, t.RoadKindAt(new Cell(0, 0)));
        Assert.Equal(RoadKind.Highway, t.RoadKindAt(new Cell(0, 2)));
        Assert.Equal(RoadKind.Gravel, t.RoadKindAt(new Cell(0, 4)));
        Assert.Equal(RoadKind.Dirt, t.RoadKindAt(new Cell(0, 6)));
        Assert.Equal(RoadKind.None, t.RoadKindAt(new Cell(0, 1)));
        Assert.Equal(RoadKind.None, t.RoadKindAt(new Cell(-1, 0))); // off the map
        Assert.True(t.Road(new Cell(0, 6)) && !t.Road(new Cell(0, 1)));
        // The dirt track crossing them: each crossing keeps the faster road, laid before or after.
        Assert.Equal(RoadKind.Paved, t.RoadKindAt(new Cell(5, 0)));
        Assert.Equal(RoadKind.Highway, t.RoadKindAt(new Cell(5, 2)));
        Assert.Equal(RoadKind.Gravel, t.RoadKindAt(new Cell(5, 4)));
        Assert.Equal(RoadKind.Dirt, t.RoadKindAt(new Cell(5, 5)));
        Assert.Equal(RoadKind.Paved, t.RoadKindAt(new Cell(9, 6))); // the paved rect over the dirt row
        Assert.Equal(RoadKind.Paved, t.RoadKindAt(new Cell(10, 7)));

        // An unknown kind is an error.
        var e = Assert.Throws<ContentException>(() => LoadJson("""{ "width": 4, "height": 4, "roads": [{ "rect": [0, 0, 1, 1], "kind": "cobbles" }] }"""));
        Assert.Contains("cobbles", e.Message);
    }

    [Fact]
    public void PlainRoadsArePavedAndHashAsBeforeKindsJoinTheHash()
    {
        const string Plain = """{ "width": 8, "height": 4, "roads": [{ "points": [[0, 1], [7, 1]] }] }""";
        var plain = LoadJson(Plain);
        var paved = LoadJson("""{ "width": 8, "height": 4, "roads": [{ "points": [[0, 1], [7, 1]], "kind": "paved" }] }""");
        var gravel = LoadJson("""{ "width": 8, "height": 4, "roads": [{ "points": [[0, 1], [7, 1]], "kind": "gravel" }] }""");
        Assert.Equal(plain.Digest(), paved.Digest());
        Assert.NotEqual(plain.Digest(), gravel.Digest());
        // The same as the road bit alone (the flags byte before road kinds).
        var flags = new byte[8 * 4];
        for (var x = 0; x < 8; x++)
        {
            flags[1 * 8 + x] = flags[2 * 8 + x] = 1 << 2; // a 2-wide road covers the line and the row after it
        }
        Assert.Equal(plain.Digest(), new TerrainMap(8, 4, new int[9 * 5], flags, Rules).Digest());
        // A kind's bits without the road bit mean nothing and are dropped.
        var stray = new byte[8 * 4];
        stray[0] = 3 << 4;
        Assert.Equal(TerrainMap.Flat(8, 4, Rules).Digest(), new TerrainMap(8, 4, new int[9 * 5], stray, Rules).Digest());
        Assert.Equal(RoadKind.None, new TerrainMap(8, 4, new int[9 * 5], stray, Rules).RoadKindAt(new Cell(0, 0)));
    }

    [Fact]
    public void RulesTakeAPerKindTableOrOneNumber()
    {
        static string Rules(string road) => $$"""
            { "classes": [{ "id": "car", "locomotor": "wheeled", "slopes": { "gentle": 10, "steep": 25, "cliff": 45 },
              "speedUp": [100, 75, 0, 0], "speedDown": [100, 100, 0, 0], "cost": [10, 14, 0, 0], "uphillCost": 50,
              {{road}} "forestCost": [100, 130, 170, 250], "forestSpeed": [100, 85, 60, 40] }] }
            """;
        var old = TerrainLoader.ParseRules(Rules("\"roadCost\": 50,")).Classes[0];
        Assert.Equal([50, 50, 50, 50], old.RoadCost);
        Assert.Equal([100, 100, 100, 100], old.RoadSpeed); // no road speed: as before road kinds
        Assert.Equal(50, old.RoadCostPercent);
        var none = TerrainLoader.ParseRules(Rules("")).Classes[0];
        Assert.Equal([100, 100, 100, 100], none.RoadCost);
        var kinds = TerrainLoader.ParseRules(Rules("\"roadCost\": [45, 50, 57, 63], \"roadSpeed\": [140, 125, 110, 100],"));
        var mc = kinds.Classes[0];
        Assert.Equal(45, mc.RoadCostOf(RoadKind.Highway));
        Assert.Equal(63, mc.RoadCostOf(RoadKind.Dirt));
        Assert.Equal(100, mc.RoadCostOf(RoadKind.None));
        Assert.Equal(110, mc.RoadSpeedOf(RoadKind.Gravel));
        Assert.Equal(100, mc.RoadSpeedOf(RoadKind.None));
        // The tables join the rules' digest.
        var faster = TerrainLoader.ParseRules(Rules("\"roadCost\": [45, 50, 57, 63], \"roadSpeed\": [150, 125, 110, 100],"));
        Assert.NotEqual(kinds.Digest(), faster.Digest());
        Assert.Throws<ContentException>(() => TerrainLoader.ParseRules(Rules("\"roadSpeed\": [140, 125, 110],")));
        Assert.Throws<ContentException>(() => TerrainLoader.ParseRules(Rules("\"roadCost\": [0, 50, 50, 50],")));
        Assert.Throws<ContentException>(() => TerrainLoader.ParseRules(Rules("\"roadSpeed\": \"fast\",")));
    }

    [Theory]
    [InlineData("wheeled", 140, 125, 110, 100)]
    [InlineData("light_wheeled", 140, 125, 110, 100)]
    [InlineData("heavy_wheeled", 140, 125, 110, 100)]
    [InlineData("tracked", 110, 110, 105, 100)]
    [InlineData("foot", 100, 100, 100, 100)]
    public void SpeedByRoadKindPerMovementClass(string cls, int highway, int paved, int gravel, int dirt)
    {
        var t = Row();
        var k = Class(cls);
        Assert.Equal(100, t.SpeedPercent(k, new Cell(0, 0), uphill: false));
        Assert.Equal(highway, t.SpeedPercent(k, new Cell(1, 0), uphill: false));
        Assert.Equal(paved, t.SpeedPercent(k, new Cell(2, 0), uphill: false));
        Assert.Equal(gravel, t.SpeedPercent(k, new Cell(3, 0), uphill: false));
        Assert.Equal(dirt, t.SpeedPercent(k, new Cell(4, 0), uphill: false));
    }

    [Fact]
    public void RoadSpeedMultipliesSlopeAndForest()
    {
        // A gentle 15% cell, paved, in scrub: wheeled 75% up x 85% scrub x 125% paved.
        var flags = new byte[] { (byte)(TerrainMap.WithRoad(0, RoadKind.Paved) | 1) };
        var t = new TerrainMap(1, 1, [0, 154, 0, 154], flags, Rules);
        Assert.Equal(SlopeClass.Gentle, t.Slope(Class("wheeled"), new Cell(0, 0)));
        Assert.Equal(75 * 85 / 100 * 125 / 100, t.SpeedPercent(Class("wheeled"), new Cell(0, 0), uphill: true));
        // A road never opens what the class can't cross: steep ground stays shut to trucks.
        var steep = new TerrainMap(1, 1, [0, 300, 0, 300], [TerrainMap.WithRoad(0, RoadKind.Highway)], Rules);
        Assert.Equal(0, steep.SpeedPercent(Class("heavy_wheeled"), new Cell(0, 0), uphill: false));
        Assert.False(steep.Passable(Class("heavy_wheeled"), new Cell(0, 0)));
    }

    [Fact]
    public void PathCostsFollowTheSpeedsPavedAsBefore()
    {
        var t = Row();
        int Cost(string cls, int x) => t.Cost(Class(cls), new Cell(x, 0));
        // Wheeled: off-road 10; highway 45%, paved 50% (as the single road cost was), gravel 57%, dirt 63%,
        // in whole numbers on the base of 10.
        Assert.Equal([10, 4, 5, 5, 6], Enumerable.Range(0, 5).Select(x => Cost("wheeled", x)));
        Assert.Equal([10, 7, 7, 7, 7], Enumerable.Range(0, 5).Select(x => Cost("tracked", x)));
        Assert.Equal([10, 9, 9, 9, 9], Enumerable.Range(0, 5).Select(x => Cost("foot", x)));
        // The costs keep the speed order: a faster kind never costs more, for every class.
        foreach (var mc in Rules.Classes)
        {
            for (var k = RoadKind.Highway; k < RoadKind.Dirt; k++)
            {
                Assert.True(mc.RoadSpeedOf(k) >= mc.RoadSpeedOf(k + 1), $"{mc.Id} speed {k}");
                Assert.True(mc.RoadCostOf(k) <= mc.RoadCostOf(k + 1), $"{mc.Id} cost {k}");
            }
            Assert.True(mc.RoadSpeedOf(RoadKind.Dirt) >= 100 && mc.RoadCostOf(RoadKind.Dirt) <= 100, mc.Id);
        }
        // The cheapest cell the router counts on: paved always, a highway where the map has one.
        Assert.Equal(45, t.MinRoadCostPercent(Class("wheeled")));
        Assert.Equal(50, TerrainMap.Flat(4, 4, Rules).MinRoadCostPercent(Class("wheeled")));
    }

    [Fact]
    public void AnAirfieldsPavementIsPavedAndLeavesAFasterRoad()
    {
        var t = Row();
        t.SetRoad(new Cell(0, 0), 5, 1, true);
        Assert.Equal(RoadKind.Paved, t.RoadKindAt(new Cell(0, 0)));
        Assert.Equal(RoadKind.Highway, t.RoadKindAt(new Cell(1, 0)));
        Assert.Equal(RoadKind.Paved, t.RoadKindAt(new Cell(3, 0)));
        Assert.Equal(RoadKind.Paved, t.RoadKindAt(new Cell(4, 0)));
        t.SetRoad(new Cell(0, 0), 5, 1, false);
        for (var x = 0; x < 5; x++)
        {
            Assert.Equal(RoadKind.None, t.RoadKindAt(new Cell(x, 0)));
        }
        Assert.Equal(TerrainMap.Flat(5, 1, Rules).Digest(), t.Digest());
    }

    // Race drives a crewed vehicle 80 cells east along a 7-wide road of the kind (none: open
    // ground) on a flat 96-cell map, and returns the ticks it took to arrive.
    private int Race(string vehicle, RoadKind kind)
    {
        const int N = 96;
        var flags = new byte[N * N];
        for (var y = 17; y <= 23; y++)
        {
            for (var x = 0; x < N; x++)
            {
                flags[y * N + x] = TerrainMap.WithRoad(0, kind);
            }
        }
        var w = Arena.NewSentinel(N);
        w.AttachTerrain(new TerrainMap(N, N, new int[(N + 1) * (N + 1)], flags, Rules));
        var (v, _) = w.SpawnCrewed(1, vehicle, 6, 20, Facing.East);
        var goal = new Cell(86, 20);
        w.Order(1, CommandKind.Move, [v], cell: goal);
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            if (Cell.Of(v.Pos) == goal && !v.Moving)
            {
                output.WriteLine($"{vehicle} on {kind}: {w.Tick} ticks");
                return (int)w.Tick;
            }
        }
        throw new Xunit.Sdk.XunitException($"{vehicle} on {kind} never arrived (at {Cell.Of(v.Pos)})");
    }

    [Fact]
    public void AWheeledUnitOnAHighwayBeatsOneOnDirtATankGainsLess()
    {
        var hvHighway = Race("humvee", RoadKind.Highway);
        var hvPaved = Race("humvee", RoadKind.Paved);
        var hvDirt = Race("humvee", RoadKind.Dirt);
        var hvOpen = Race("humvee", RoadKind.None);
        var tankHighway = Race("main_battle_tank", RoadKind.Highway);
        var tankDirt = Race("main_battle_tank", RoadKind.Dirt);
        // Dirt drives at open ground's speed (the route along a road runs a little straighter).
        Assert.InRange(hvDirt, hvOpen * 95 / 100, hvOpen);
        // 140% against 100%: the highway takes about 1/1.4 of the time (accelerating and braking
        // at the ends shave a little off the ratio).
        var wheeled = (double)hvDirt / hvHighway;
        var tracked = (double)tankDirt / tankHighway;
        output.WriteLine($"wheeled dirt/highway {wheeled:0.000}, paved {(double)hvDirt / hvPaved:0.000}; tracked {tracked:0.000}");
        Assert.InRange(wheeled, 1.30, 1.42);
        Assert.InRange((double)hvDirt / hvPaved, 1.18, 1.27);
        Assert.True(hvHighway < hvPaved && hvPaved < hvDirt);
        // The tank: 110% on the highway, so about 1.1.
        Assert.InRange(tracked, 1.06, 1.11);
        Assert.True(tracked < wheeled);
    }

    // Detour drives a Humvee from (5, 10) to (94, 10) on a flat map with a straight dirt track
    // between them and a highway that leaves the track and comes back through row y, and returns
    // the farthest row it reached.
    private int Detour(int y)
    {
        const int W = 100;
        var t = LoadJson($$"""
            {
              "width": {{W}}, "height": {{W}},
              "roads": [
                { "points": [[0, 10], [99, 10]], "width": 3, "kind": "dirt" },
                { "points": [[5, 10], [5, {{y}}], [94, {{y}}], [94, 10]], "width": 3, "kind": "highway" }
              ]
            }
            """);
        var w = Arena.NewSentinel(W);
        w.AttachTerrain(t);
        var (v, _) = w.SpawnCrewed(1, "humvee", 5, 10, Facing.East);
        var goal = new Cell(94, 10);
        w.Order(1, CommandKind.Move, [v], cell: goal);
        var far = 10;
        for (var tick = 0; tick < 3000 && !(Cell.Of(v.Pos) == goal && !v.Moving); tick++)
        {
            w.Step();
            far = Math.Max(far, Cell.Of(v.Pos).Y);
        }
        Assert.Equal(goal, Cell.Of(v.Pos));
        output.WriteLine($"highway through row {y}: reached row {far}, arrived at tick {w.Tick}");
        return far;
    }

    [Fact]
    public void RoutesTakeTheHighwayWhenTheDetourPays()
    {
        // Through row 20: 109 cells at 140% (78 cells' time) beat 89 cells of dirt.
        Assert.True(Detour(20) >= 19);
        // Through row 45: 159 cells at 140% (114) don't: the Humvee keeps to the track.
        Assert.True(Detour(45) <= 12);
    }
}
