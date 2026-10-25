using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Scenarios;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The foot kinds of road (docs/design.md, "Alleys and footpaths"): an alley (a narrow old-town street) is open to the foot
// movement class only and a wall to every other ground class; a footpath blocks and slows nobody and only pulls infantry
// routes toward it. A vehicle road wins the cell where they overlap. A map without either hashes and plays as before.
public class RoadKind2Tests(ITestOutputHelper output)
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static readonly string[] Classes = ["foot", "wheeled", "light_wheeled", "heavy_wheeled", "tracked"];

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
        var dir = Path.Combine(Path.GetTempPath(), "simcore-roads2-" + Guid.NewGuid().ToString("N"));
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

    // ---- The map file and the layers ----

    [Fact]
    public void MapRoadsTakeTheFootKindsAndAnUnknownKindIsStillAnError()
    {
        var t = LoadJson("""
            {
              "width": 20, "height": 12,
              "roads": [
                { "points": [[0, 1], [19, 1]], "width": 1, "kind": "alley" },
                { "points": [[0, 4], [19, 4]], "width": 1, "kind": "footpath" },
                { "rect": [3, 7, 4, 2], "kind": "alley" },
                { "rect": [10, 7, 4, 2], "kind": "footpath" },
                { "points": [[0, 10], [19, 10]] }
              ]
            }
            """);
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(new Cell(0, 1)));
        Assert.Equal(RoadKind.Footpath, t.RoadKindAt(new Cell(19, 4)));
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(new Cell(6, 8)));
        Assert.Equal(RoadKind.Footpath, t.RoadKindAt(new Cell(10, 7)));
        Assert.Equal(RoadKind.Paved, t.RoadKindAt(new Cell(5, 10)));
        Assert.Equal(RoadKind.None, t.RoadKindAt(new Cell(5, 2)));
        Assert.Equal(RoadKind.None, t.RoadKindAt(new Cell(-1, 1))); // off the map
        // IsFootOnly is the alleys alone; Road is the vehicle roads alone.
        Assert.True(t.IsFootOnly(new Cell(0, 1)) && t.IsFootOnly(new Cell(6, 8)));
        Assert.False(t.IsFootOnly(new Cell(0, 4)) || t.IsFootOnly(new Cell(5, 10)) || t.IsFootOnly(new Cell(5, 2)) || t.IsFootOnly(new Cell(-1, 1)));
        Assert.True(t.Road(new Cell(5, 10)));
        Assert.False(t.Road(new Cell(0, 1)) || t.Road(new Cell(0, 4)));
        // The enum keeps its old numbers and appends the new ones.
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], new[] { RoadKind.None, RoadKind.Highway, RoadKind.Paved, RoadKind.Gravel, RoadKind.Dirt, RoadKind.Alley, RoadKind.Footpath }.Select(k => (int)k));

        var e = Assert.Throws<ContentException>(() => LoadJson("""{ "width": 4, "height": 4, "roads": [{ "rect": [0, 0, 1, 1], "kind": "cobbles" }] }"""));
        Assert.Contains("cobbles", e.Message);
        Assert.Contains("alley", e.Message);
        Assert.Throws<ContentException>(() => LoadJson("""{ "width": 4, "height": 4, "roads": [{ "rect": [0, 0, 1, 1], "kind": "Alley" }] }"""));
    }

    [Fact]
    public void AVehicleRoadWinsTheCellWhereTheFootKindsOverlapItAndAnAlleyBeatsAFootpath()
    {
        // Each pair in both orders: the vehicle road laid before the foot kind, and after it.
        var t = LoadJson("""
            {
              "width": 16, "height": 16,
              "roads": [
                { "points": [[0, 4], [15, 4]], "width": 1, "kind": "gravel" },
                { "points": [[3, 0], [3, 15]], "width": 1, "kind": "alley" },
                { "points": [[0, 9], [15, 9]], "width": 1, "kind": "alley" },
                { "points": [[8, 0], [8, 15]], "width": 1, "kind": "dirt" },
                { "points": [[12, 0], [12, 15]], "width": 1, "kind": "footpath" },
                { "points": [[0, 12], [15, 12]], "width": 1, "kind": "highway" },
                { "points": [[0, 7], [15, 7]], "width": 1, "kind": "footpath" },
                { "points": [[0, 14], [15, 14]], "width": 1, "kind": "footpath" },
                { "points": [[0, 14], [15, 14]], "width": 1, "kind": "alley" },
                { "points": [[0, 2], [15, 2]], "width": 1, "kind": "alley" },
                { "points": [[0, 2], [15, 2]], "width": 1, "kind": "footpath" }
              ]
            }
            """);
        Assert.Equal(RoadKind.Gravel, t.RoadKindAt(new Cell(3, 4))); // alley laid first, gravel after... and the other order
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(new Cell(3, 5)));
        Assert.Equal(RoadKind.Dirt, t.RoadKindAt(new Cell(8, 9))); // alley row first, dirt column after
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(new Cell(7, 9)));
        Assert.Equal(RoadKind.Highway, t.RoadKindAt(new Cell(12, 12))); // footpath column first, highway row after
        Assert.Equal(RoadKind.Footpath, t.RoadKindAt(new Cell(12, 11)));
        Assert.Equal(RoadKind.Gravel, t.RoadKindAt(new Cell(12, 4))); // footpath column over the gravel row laid before it
        Assert.Equal(RoadKind.Highway, t.RoadKindAt(new Cell(3, 12))); // highway over the alley column
        Assert.False(t.IsFootOnly(new Cell(3, 4)) || t.IsFootOnly(new Cell(8, 9)) || t.IsFootOnly(new Cell(3, 12)));
        // Alley over footpath, whatever the order.
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(new Cell(5, 14)));
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(new Cell(5, 2)));
        // The footpath row over the dirt column: the dirt stays; over the alley column: the alley stays.
        Assert.Equal(RoadKind.Dirt, t.RoadKindAt(new Cell(8, 7)));
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(new Cell(3, 7)));
        Assert.Equal(RoadKind.Footpath, t.RoadKindAt(new Cell(5, 7)));
    }

    [Theory]
    [InlineData("foot", true)]
    [InlineData("wheeled", false)]
    [InlineData("light_wheeled", false)]
    [InlineData("heavy_wheeled", false)]
    [InlineData("tracked", false)]
    public void AnAlleyIsPassableToTheFootClassAndAWallToEveryOther(string cls, bool enters)
    {
        var t = LoadJson("""{ "width": 6, "height": 3, "roads": [{ "points": [[1, 1], [4, 1]], "width": 1, "kind": "alley" }] }""");
        var k = Class(cls);
        Assert.Equal(enters, Rules.Classes[k].EntersAlleys);
        var alley = new Cell(2, 1);
        var open = new Cell(2, 0);
        Assert.Equal(enters, t.Passable(k, alley));
        Assert.Equal(10, t.Cost(k, open));
        Assert.Equal(enters ? 9 : 0, t.Cost(k, alley)); // foot pays what paved costs it (9 on open ground's 10); the rest, nothing: a wall
        Assert.Equal(enters ? 100 : 0, t.SpeedPercent(k, alley, uphill: false)); // no speed change for foot (decision 334)
        Assert.Equal(enters ? 100 : 0, t.SpeedPercent(k, alley, uphill: true));
        Assert.Equal(enters ? 9 : 0, t.StepCost(k, open, alley));
        if (enters)
        {
            Assert.Equal(Rules.Classes[k].RoadCostOf(RoadKind.Paved), Rules.Classes[k].RoadCostOf(RoadKind.Alley));
        }
    }

    [Theory]
    [InlineData("foot", 9)]
    [InlineData("wheeled", 10)]
    [InlineData("light_wheeled", 10)]
    [InlineData("heavy_wheeled", 10)]
    [InlineData("tracked", 10)]
    public void AFootpathBlocksAndSlowsNobodyAndOnlyCheapensTheFootClass(string cls, int cost)
    {
        var t = LoadJson("""{ "width": 6, "height": 3, "roads": [{ "points": [[1, 1], [4, 1]], "width": 1, "kind": "footpath" }] }""");
        var k = Class(cls);
        var path = new Cell(2, 1);
        var open = new Cell(2, 0);
        Assert.True(t.Passable(k, path));
        Assert.Equal(cost, t.Cost(k, path));
        Assert.Equal(10, t.Cost(k, open));
        // No speed change for anyone, uphill or not: the same percent as the ground beside it.
        Assert.Equal(t.SpeedPercent(k, open, uphill: false), t.SpeedPercent(k, path, uphill: false));
        Assert.Equal(100, t.SpeedPercent(k, path, uphill: true));
        Assert.False(t.IsFootOnly(path));
        // The cheapest cell the router counts on: a footpath is never cheaper than paved for foot, nothing for the rest.
        Assert.Equal(TerrainMap.Flat(4, 4, Rules).MinRoadCostPercent(k), t.MinRoadCostPercent(k));
    }

    [Fact]
    public void AFootKindOnASlopeKeepsTheSlopeAndAForestedAlleyKeepsTheForest()
    {
        // A gentle 15% cell with an alley in scrub: foot 85% going up (the gentle row), 100% scrub... 90% cost on top.
        var flags = new byte[] { 1 };
        var t = new TerrainMap(1, 1, [0, 154, 0, 154], flags, Rules, null, [1]);
        var foot = Class("foot");
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(new Cell(0, 0)));
        Assert.Equal(SlopeClass.Gentle, t.Slope(foot, new Cell(0, 0)));
        Assert.Equal(85 * Rules.Classes[foot].ForestSpeedPercent[1] / 100, t.SpeedPercent(foot, new Cell(0, 0), uphill: true));
        Assert.Equal(12 * 90 / 100 * Rules.Classes[foot].ForestCostPercent[1] / 100, t.Cost(foot, new Cell(0, 0)));
        // An alley opens nothing: steep ground stays shut to the classes that can't climb it, dense forest to the trucks.
        var steep = new TerrainMap(1, 1, [0, 300, 0, 300], [0], Rules, null, [1]);
        Assert.Equal(SlopeClass.Steep, steep.Slope(Class("foot"), new Cell(0, 0)));
        Assert.False(steep.Passable(Class("heavy_wheeled"), new Cell(0, 0)));
        Assert.True(steep.Passable(Class("foot"), new Cell(0, 0)));
        var dense = new TerrainMap(1, 1, [0, 0, 0, 0], [3], Rules, null, [1]);
        Assert.False(dense.Passable(Class("heavy_wheeled"), new Cell(0, 0)));
        Assert.False(dense.Passable(Class("tracked"), new Cell(0, 0)));
    }

    [Fact]
    public void ARoadAtTheFootKindsCellAnOverlapKeepsTheVehicleRoadsSpeedAndCost()
    {
        // A paved row crossed by an alley column and a footpath column (laid after it), and the other way round.
        var t = LoadJson("""
            {
              "width": 12, "height": 12,
              "roads": [
                { "points": [[0, 3], [11, 3]], "width": 1 },
                { "points": [[4, 0], [4, 11]], "width": 1, "kind": "alley" },
                { "points": [[8, 0], [8, 11]], "width": 1, "kind": "footpath" },
                { "points": [[0, 8], [11, 8]], "width": 1 }
              ]
            }
            """);
        var plain = LoadJson("""{ "width": 12, "height": 12, "roads": [{ "points": [[0, 3], [11, 3]], "width": 1 }, { "points": [[0, 8], [11, 8]], "width": 1 }] }""");
        foreach (var cls in Classes)
        {
            var k = Class(cls);
            foreach (var c in new[] { new Cell(4, 3), new Cell(8, 3), new Cell(4, 8), new Cell(8, 8) })
            {
                Assert.Equal(plain.RoadKindAt(c), t.RoadKindAt(c));
                Assert.Equal(plain.Cost(k, c), t.Cost(k, c));
                Assert.Equal(plain.SpeedPercent(k, c, uphill: false), t.SpeedPercent(k, c, uphill: false));
                Assert.True(t.Passable(k, c), $"{cls} at {c}");
            }
        }
        // The roads' own cells beside the crossing are the alley and the footpath still.
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(new Cell(4, 5)));
        Assert.Equal(RoadKind.Footpath, t.RoadKindAt(new Cell(8, 5)));
    }

    // ---- Storage and the hash ----

    // Two maps built the way the road kinds' own tests would (a flags byte per cell, and a map file), digested by the engine
    // before the foot kinds existed: they must hash the same now.
    private static TerrainMap OldStyleFlagsMap(bool secondByte = false)
    {
        const int W = 24, H = 16;
        var corners = new int[(W + 1) * (H + 1)];
        for (var y = 0; y <= H; y++)
        {
            for (var x = 0; x <= W; x++)
            {
                corners[y * (W + 1) + x] = (x * 37 + y * 91) % 700;
            }
        }
        var flags = new byte[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                flags[y * W + x] = (byte)((x / 5 + y / 3) % 4);
            }
        }
        for (var x = 0; x < W; x++)
        {
            flags[2 * W + x] = TerrainMap.WithRoad(flags[2 * W + x], RoadKind.Highway);
            flags[5 * W + x] = TerrainMap.WithRoad(flags[5 * W + x], RoadKind.Paved);
            flags[8 * W + x] = TerrainMap.WithRoad(flags[8 * W + x], RoadKind.Gravel);
            flags[11 * W + x] = TerrainMap.WithRoad(flags[11 * W + x], RoadKind.Dirt);
        }
        if (!secondByte)
        {
            return new TerrainMap(W, H, corners, flags, Rules);
        }
        // A second byte whose only foot kinds sit under vehicle roads (dropped there) or are not kinds at all.
        var flags2 = new byte[W * H];
        for (var x = 0; x < W; x++)
        {
            flags2[2 * W + x] = flags2[5 * W + x] = flags2[8 * W + x] = flags2[11 * W + x] = 1; // alleys under the roads
            flags2[0 * W + x] = 0b1111_1100; // spare bits mean nothing
            flags2[14 * W + x] = 3; // not a kind
        }
        return new TerrainMap(W, H, corners, flags, Rules, null, flags2);
    }

    private const string OldJson = """
        {
          "width": 40, "height": 30,
          "forest": [{ "rect": [3, 3, 10, 8], "density": 2 }],
          "roads": [
            { "points": [[0, 4], [39, 4]], "width": 3 },
            { "points": [[0, 12], [39, 20]], "width": 2, "kind": "gravel" },
            { "points": [[20, 0], [20, 29]], "width": 1, "kind": "dirt" },
            { "rect": [30, 22, 6, 5], "kind": "highway" }
          ],
          "pads": [{ "rect": [8, 15, 6, 6], "height": 12 }],
          "water": [{ "kind": "lake", "polygon": [[25, 6], [38, 6], [38, 11], [25, 11]], "height": -3 }]
        }
        """;

    [Fact]
    public void MapsWithoutFootKindsHashExactlyAsBeforeThem()
    {
        // The digests the engine gave these maps at af9d53d, before the second flags byte. Re-pinned when the terrain rules
        // gained their trees block (tree-trunks): with the rules before it they still give 429021359868806026 and
        // 18074894086379094227. Re-pinned again when trees.trunkChance went from [0, 0, 35, 70] to [0, 0, 50, 85] (the owner
        // accepted the tree defaults, 2026-10-25): the digest covers the terrain rules; with the old chances they gave
        // 3286354020458391409 and 3153694624166956842.
        Assert.Equal(16537106901726366067UL, OldStyleFlagsMap().Digest());
        Assert.Equal(11235144880084327484UL, LoadJson(OldJson).Digest());
        // A second byte with nothing in it (or nothing that survives) is no second byte.
        Assert.Equal(OldStyleFlagsMap().Digest(), OldStyleFlagsMap(secondByte: true).Digest());
        Assert.Equal(OldStyleFlagsMap().Bytes, OldStyleFlagsMap(secondByte: true).Bytes);
        // Foot kinds laid wholly under vehicle roads in a map file leave the digest alone too.
        var under = OldJson.Replace("\"roads\": [", "\"roads\": [{ \"points\": [[0, 4], [39, 4]], \"width\": 3, \"kind\": \"alley\" }, { \"points\": [[20, 0], [20, 29]], \"width\": 1, \"kind\": \"footpath\" },");
        Assert.Equal(LoadJson(OldJson).Digest(), LoadJson(under).Digest());
    }

    [Fact]
    public void FootKindsJoinTheDigestAndTheMemoryOnlyWhereTheMapHasThem()
    {
        var none = LoadJson("""{ "width": 8, "height": 8, "roads": [{ "points": [[0, 1], [7, 1]] }] }""");
        var alley = LoadJson("""{ "width": 8, "height": 8, "roads": [{ "points": [[0, 1], [7, 1]] }, { "points": [[0, 3], [7, 3]], "width": 1, "kind": "alley" }] }""");
        var path = LoadJson("""{ "width": 8, "height": 8, "roads": [{ "points": [[0, 1], [7, 1]] }, { "points": [[0, 3], [7, 3]], "width": 1, "kind": "footpath" }] }""");
        Assert.NotEqual(none.Digest(), alley.Digest());
        Assert.NotEqual(none.Digest(), path.Digest());
        Assert.NotEqual(alley.Digest(), path.Digest());
        Assert.Equal(none.Bytes + 8 * 8, alley.Bytes); // one more byte a cell, on this map only
        // The same map twice digests the same.
        Assert.Equal(alley.Digest(), LoadJson("""{ "width": 8, "height": 8, "roads": [{ "points": [[0, 1], [7, 1]] }, { "points": [[0, 3], [7, 3]], "width": 1, "kind": "alley" }] }""").Digest());
        // The world's hash carries it.
        var a = Arena.NewSentinel(8);
        a.AttachTerrain(none);
        var b = Arena.NewSentinel(8);
        b.AttachTerrain(alley);
        Assert.NotEqual(a.Hash(), b.Hash());
    }

    [Fact]
    public void LayingAndClearingFootKindsAtRunTimeRebuildsTheCellsAndTheDigest()
    {
        var t = TerrainMap.Flat(6, 3, Rules);
        var before = t.Digest();
        var cell = new Cell(2, 1);
        t.SetRoadKind(cell, RoadKind.Alley);
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(cell));
        Assert.False(t.Passable(Class("tracked"), cell));
        Assert.True(t.Passable(Class("foot"), cell));
        Assert.NotEqual(before, t.Digest());
        t.SetRoadKind(cell, RoadKind.Footpath);
        Assert.Equal(RoadKind.Footpath, t.RoadKindAt(cell));
        Assert.True(t.Passable(Class("tracked"), cell));
        t.SetRoadKind(cell, RoadKind.Paved); // a vehicle road replaces it
        Assert.Equal(RoadKind.Paved, t.RoadKindAt(cell));
        t.SetRoadKind(cell, RoadKind.Alley); // and an alley replaces that (SetRoadKind lays exactly the kind it is given)
        Assert.Equal(RoadKind.Alley, t.RoadKindAt(cell));
        Assert.False(t.Road(cell));
        t.SetRoad(new Cell(0, 1), 6, 1, true); // an airfield's pavement over it
        Assert.Equal(RoadKind.Paved, t.RoadKindAt(cell));
        Assert.True(t.Passable(Class("tracked"), cell));
        t.SetRoad(new Cell(0, 1), 6, 1, false);
        Assert.Equal(RoadKind.None, t.RoadKindAt(cell));
        Assert.Equal(before, t.Digest());
    }

    // ---- Routing, movement and placement ----

    // The town: two blocks of buildings (rock on the grid) with a one-cell alley between them along row 31, running a little
    // past their ends. The blocks cover rows 12-30 and 32-50 of a 64-cell map, or (closed) rows 0-30 and 32-63: no way round.
    private static World Town(bool closed = false, bool sandbox = false)
    {
        const int N = 64;
        var w = Arena.NewSentinel(N, edit: sandbox ? s => s with { Scenario = Scenario.SandboxName } : null);
        var map = LoadJson("""{ "width": 64, "height": 64, "roads": [{ "points": [[18, 31], [43, 31]], "width": 1, "kind": "alley" }] }""");
        w.AttachTerrain(map);
        for (var x = 20; x <= 41; x++)
        {
            for (var y = closed ? 0 : 12; y <= 30; y++)
            {
                w.Grid.Set(new Cell(x, y), Terrain.Blocked);
            }
            for (var y = 32; y <= (closed ? 63 : 50); y++)
            {
                w.Grid.Set(new Cell(x, y), Terrain.Blocked);
            }
        }
        return w;
    }

    private static readonly string[] Vehicles = ["humvee", "sentinel_cargo_truck", "main_battle_tank"];

    [Fact]
    public void TheTownsClassesAreWhatTheTestsMean()
    {
        var w = Town();
        var classes = new[] { "Rifleman", "humvee", "sentinel_cargo_truck", "main_battle_tank" }
            .Select(n => Rules.Classes[w.Ground!.Rules.ClassOf(w.Spawn(1, n, 5, 5).Def)].Id).ToList();
        Assert.Equal(["foot", "light_wheeled", "heavy_wheeled", "tracked"], classes);
    }

    // March runs ticks and returns whether each unit was ever in an alley cell.
    private static List<bool> March(World w, IReadOnlyList<Unit> units, int ticks)
    {
        var seen = new bool[units.Count];
        for (var t = 0; t < ticks; t++)
        {
            w.Step();
            for (var i = 0; i < units.Count; i++)
            {
                seen[i] |= w.IsAlley(Cell.Of(units[i].Pos));
            }
        }
        return [.. seen];
    }

    [Fact]
    public void InfantryRouteThroughAnAlleyBetweenTwoBuildings()
    {
        // One soldier on his own route, and a squad on a group flow.
        var goal = new Cell(58, 31);
        var w = Town();
        var one = w.Spawn(1, "Rifleman", 6, 31);
        w.Order(1, CommandKind.Move, [one], cell: goal);
        Assert.True(March(w, [one], 900)[0], "the lone soldier never entered the alley");
        Assert.True(Cell.Of(one.Pos).X >= 54, $"the lone soldier stopped at {Cell.Of(one.Pos)}");

        var w2 = Town();
        var squad = Enumerable.Range(0, 4).Select(i => w2.Spawn(1, "Rifleman", 6, 29 + i)).ToList();
        w2.Order(1, CommandKind.Move, squad, cell: goal);
        var seen = March(w2, squad, 1200);
        output.WriteLine(string.Join(", ", squad.Select(u => Cell.Of(u.Pos).ToString())));
        Assert.All(seen, s => Assert.True(s, "a soldier of the squad never entered the alley"));
        Assert.All(squad, u => Assert.True(Cell.Of(u.Pos).X >= 52, $"{u.Id} stopped at {Cell.Of(u.Pos)}"));
    }

    [Fact]
    public void VehiclesDriveRoundAnAlleyAndNeverEnterIt()
    {
        var goal = new Cell(58, 31);
        var w = Town();
        var units = Vehicles.Select((v, i) => w.SpawnCrewed(1, v, 6, 20 + i * 4, Facing.East).Vehicle).ToList();
        foreach (var u in units)
        {
            w.Order(1, CommandKind.Move, [u], cell: goal);
        }
        var seen = March(w, units, 2400);
        for (var i = 0; i < units.Count; i++)
        {
            output.WriteLine($"{Vehicles[i]}: {Cell.Of(units[i].Pos)}");
            Assert.False(seen[i], $"{Vehicles[i]} entered an alley");
            Assert.True(Cell.Of(units[i].Pos).X >= 50, $"{Vehicles[i]} did not get round: {Cell.Of(units[i].Pos)}");
        }
    }

    [Fact]
    public void WithNoWayRoundTheVehiclesStayOutAndTheInfantryGetThrough()
    {
        var goal = new Cell(58, 31);
        var w = Town(closed: true);
        var vehicles = Vehicles.Select((v, i) => w.SpawnCrewed(1, v, 6, 20 + i * 4, Facing.East).Vehicle).ToList();
        var soldier = w.Spawn(1, "Rifleman", 6, 31);
        var all = new List<Unit>(vehicles) { soldier };
        foreach (var u in all)
        {
            w.Order(1, CommandKind.Move, [u], cell: goal);
        }
        var seen = March(w, all, 1500);
        for (var i = 0; i < vehicles.Count; i++)
        {
            output.WriteLine($"{Vehicles[i]}: {Cell.Of(vehicles[i].Pos)}");
            Assert.False(seen[i], $"{Vehicles[i]} entered an alley");
            Assert.True(Cell.Of(vehicles[i].Pos).X < 20, $"{Vehicles[i]} got through to {Cell.Of(vehicles[i].Pos)}");
            Assert.True(Cell.Of(vehicles[i].Pos).X >= 8, $"{Vehicles[i]} did not come up to the buildings: {Cell.Of(vehicles[i].Pos)}");
        }
        Assert.True(seen[^1]);
        Assert.True(Cell.Of(soldier.Pos).X >= 54, $"the soldier stopped at {Cell.Of(soldier.Pos)}");
    }

    [Fact]
    public void TheRouterSeesAnAlleyAsBlockedForEveryClassButFoot()
    {
        var w = Town();
        var router = w.Routing;
        var alley = new Cell(30, 31);
        foreach (var (name, enters) in new[] { ("Rifleman", true), ("humvee", false), ("sentinel_cargo_truck", false), ("main_battle_tank", false) })
        {
            var u = w.Spawn(1, name, 5, 5);
            var k = router.ClassOf(u.Def);
            Assert.Equal(enters, router.Passable(k, router.Index(alley)));
        }
    }

    [Fact]
    public void AVehicleSpawnedOnAnAlleyIsMovedOffItAndAStructureIsRefusedThere()
    {
        var w = Town(sandbox: true);
        // Between the blocks every cell off the alley is rock; the nearest free ground is beside the alley's open end, 11 cells
        // away (x = 19), the same for each vehicle.
        foreach (var name in Vehicles)
        {
            var v = w.Spawn(1, name, 30, 31);
            var at = Cell.Of(v.Pos);
            Assert.False(w.IsAlley(at), $"{name} stands in the alley at {at}");
            Assert.True(w.Grid.Passable(at));
            Assert.Equal(11, Math.Max(Math.Abs(at.X - 30), Math.Abs(at.Y - 31)));
        }
        // Infantry stay where they are put.
        var r = w.Spawn(1, "Rifleman", 30, 31);
        Assert.Equal(new Cell(30, 31), Cell.Of(r.Pos));
        // The sandbox: a soldier stays, a vehicle is moved, a structure is refused (a depot is 6 x 6 cells).
        var before = w.Units.Count;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "Rifleman", Cell = new Cell(25, 31) });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "main_battle_tank", Cell = new Cell(25, 31) });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "supply_depot", Cell = new Cell(14, 29) }); // covers the alley's end cells (18, 31) and (19, 31)
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "supply_depot", Cell = new Cell(2, 40) }); // clear ground
        w.Step();
        var soldier = w.Units.Last(u => u.Def.Name == "Rifleman");
        Assert.Equal(new Cell(25, 31), Cell.Of(soldier.Pos));
        var tank = w.Units.Last(u => u.Def.Name == "main_battle_tank");
        Assert.False(w.IsAlley(Cell.Of(tank.Pos)));
        var depot = Assert.Single(w.All("supply_depot"));
        Assert.Equal(new Cell(2, 40), World.FootprintOrigin(depot));
        Assert.True(w.Units.Count > before);
    }

    [Fact]
    public void AProducedVehicleWhoseExitIsAnAlleyRoutesFromTheNearestCellThatIsNone()
    {
        var w = Arena.New(64);
        w.AttachTerrain(LoadJson("""{ "width": 64, "height": 64, "roads": [{ "rect": [18, 21, 24, 2], "kind": "alley" }] }"""));
        var garage = w.Spawn(1, "vehicle_garage", 20, 10); // x 20-39, y 10-20; doors south, their exits on rows 21-22
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [garage.Id], Unit = "technical" });
        w.Run(201 + 30);
        var tech = Assert.Single(w.All("technical"));
        Assert.Equal(garage.Id, tech.OutOf);
        Assert.False(w.IsAlley(tech.OutVia), $"its route starts in the alley at {tech.OutVia}");
        Assert.True(w.Grid.Passable(tech.OutVia));
    }

    [Fact]
    public void BuildersMayNotPlaceAStructureOnAnAlleyAndMayBesideIt()
    {
        var w = Arena.New(48);
        w.AttachTerrain(LoadJson("""{ "width": 48, "height": 48, "roads": [{ "points": [[10, 24], [40, 24]], "width": 1, "kind": "alley" }, { "points": [[10, 30], [40, 30]], "width": 3, "kind": "footpath" }] }"""));
        var worker = w.Spawn(1, "worker", 5, 20);
        var depot = w.Player(1)!.Setup.Find(w.Content, "supply_depot")!;
        Assert.False(w.CanPlace(depot, new Cell(20, 20), 0, [worker])); // rows 20-25: covers the alley
        Assert.False(w.CanPlace(depot, new Cell(20, 24), 0, [worker]));
        Assert.True(w.CanPlace(depot, new Cell(20, 17), 0, [worker])); // rows 17-22: beside it
        Assert.True(w.CanPlace(depot, new Cell(20, 25), 0, [worker])); // rows 25-30: below it
        Assert.True(w.CanPlace(depot, new Cell(20, 28), 0, [worker])); // on a footpath: footpaths are ground
        Assert.True(w.IsAlley(new Cell(20, 24)) && !w.IsAlley(new Cell(20, 25)) && !w.IsAlley(new Cell(20, 30)));
    }

    [Fact]
    public void ABridgeSiteOnAnAlleyIsRefused()
    {
        const int N = 64;
        var corners = new int[(N + 1) * (N + 1)];
        var flags2 = new byte[N * N];
        var river = WaterBody.River(Fixed.Zero, [new Cell(0, 30), new Cell(N - 1, 30)], [3, 3]);
        var a = TestContent.Mvp.Archetype("bridge_concrete")!;
        var open = new TerrainMap(N, N, corners, new byte[N * N], Rules, [river], flags2);
        var site = new Cell(30, 24); // 5 x 12, road north-south over the river along row 30
        Assert.Null(World.BridgeSiteRefusal(open, a, site, Facing.South));
        flags2 = new byte[N * N];
        flags2[26 * N + 32] = 1; // an alley cell on the ramp
        var blocked = new TerrainMap(N, N, new int[(N + 1) * (N + 1)], new byte[N * N], Rules, [river], flags2);
        Assert.Contains("alley", World.BridgeSiteRefusal(blocked, a, site, Facing.South));
    }

    // ---- Footpaths ----

    // Walk drives a crewed vehicle along row 20 from x = 6 to x = 86 on a flat 96-cell map and records its position each tick
    // until it arrives.
    private static List<Vec> Walk(string json, string unit)
    {
        var w = Arena.NewSentinel(96);
        w.AttachTerrain(LoadJson(json));
        var u = w.SpawnCrewed(1, unit, 6, 20, Facing.East).Vehicle;
        var goal = new Cell(86, 20);
        w.Order(1, CommandKind.Move, [u], cell: goal);
        var track = new List<Vec>();
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            track.Add(u.Pos);
            if (Cell.Of(u.Pos) == goal && !u.Moving)
            {
                return track;
            }
        }
        throw new Xunit.Sdk.XunitException($"{unit} never arrived (at {Cell.Of(u.Pos)})");
    }

    private const string Field = """{ "width": 96, "height": 96 }""";

    [Theory]
    [InlineData("main_battle_tank")]
    [InlineData("humvee")]
    [InlineData("sentinel_cargo_truck")]
    public void AVehicleDrivesAFootpathExactlyAsItDrivesTheFieldBesideIt(string unit)
    {
        var field = Walk(Field, unit);
        // Along the path, across it, and a path that bends: the same positions, tick by tick.
        foreach (var path in new[]
        {
            """{ "width": 96, "height": 96, "roads": [{ "points": [[0, 20], [95, 20]], "width": 3, "kind": "footpath" }] }""",
            """{ "width": 96, "height": 96, "roads": [{ "points": [[40, 0], [40, 95]], "width": 2, "kind": "footpath" }, { "points": [[60, 10], [62, 40]], "width": 3, "kind": "footpath" }] }""",
            """{ "width": 96, "height": 96, "roads": [{ "points": [[0, 20], [48, 14], [95, 20]], "width": 1, "kind": "footpath" }, { "rect": [20, 15, 30, 10], "kind": "footpath" }] }""",
        })
        {
            var track = Walk(path, unit);
            output.WriteLine($"{unit}: {field.Count} ticks on the field, {track.Count} with the path");
            Assert.Equal(field, track);
        }
    }

    [Fact]
    public void ATankCrossingAFootpathAndAnAlleyElsewhereOnTheMapIsUnaffected()
    {
        var field = Walk(Field, "main_battle_tank");
        // An alley well off its way changes nothing for it either.
        var track = Walk("""{ "width": 96, "height": 96, "roads": [{ "points": [[10, 60], [80, 60]], "width": 1, "kind": "alley" }] }""", "main_battle_tank");
        Assert.Equal(field, track);
    }

    // Detour walks a lone soldier or a squad of four from (6, 48) to (86, 48) with a footpath bending through row y at the
    // middle, 3 wide, and returns the farthest the soldiers got from row 48 and the share of soldier-ticks they spent on path
    // cells.
    private (int Far, double OnPath) Detour(int y, int soldiers, bool footpath = true)
    {
        var json = footpath
            ? $$"""{ "width": 96, "height": 96, "roads": [{ "points": [[0, 48], [43, {{y}}], [52, {{y}}], [95, 48]], "width": 3, "kind": "footpath" }] }"""
            : Field;
        var w = Arena.NewSentinel(96);
        var map = LoadJson(json);
        w.AttachTerrain(map);
        var squad = Enumerable.Range(0, soldiers).Select(i => w.Spawn(1, "Rifleman", 6, 47 + i)).ToList();
        var goal = new Cell(86, 48);
        w.Order(1, CommandKind.Move, squad, cell: goal);
        int far = 0, on = 0, n = 0;
        for (var t = 0; t < 2400; t++)
        {
            w.Step();
            foreach (var u in squad)
            {
                var c = Cell.Of(u.Pos);
                far = Math.Max(far, Math.Abs(c.Y - 48));
                on += map.RoadKindAt(c) == RoadKind.Footpath ? 1 : 0;
                n++;
            }
            if (squad.All(u => !u.Moving))
            {
                break;
            }
        }
        output.WriteLine($"footpath via row {y}, {soldiers} soldiers: far {far}, {100.0 * on / n:0.0}% of soldier-ticks on the path, {w.Tick} ticks, ends {string.Join(" ", squad.Select(u => Cell.Of(u.Pos).ToString()))}");
        Assert.All(squad, u => Assert.True(Math.Abs(Cell.Of(u.Pos).X - 86) <= 6, $"stopped at {Cell.Of(u.Pos)}"));
        return (far, 1.0 * on / n);
    }

    [Fact]
    public void ASquadPrefersAFootpathWhenTheDetourIsSmallAndIgnoresItWhenItIsNot()
    {
        // Through row 42: the path is a little longer than the straight way (about 1% over 80 cells) and 10% cheaper per cell.
        var near = Detour(42, 4);
        var none = Detour(42, 4, footpath: false);
        Assert.True(near.Far >= 5, $"the squad kept to the straight way (far {near.Far})");
        Assert.True(near.OnPath > 0.8, $"{near.OnPath:0.00} of the soldier-ticks on the path");
        Assert.True(none.Far <= 3, $"no path, yet the squad wandered {none.Far} rows");
        // Through row 20: 28 rows off, a detour 18% longer that 10% cheaper cells do not pay.
        var far = Detour(20, 4);
        Assert.True(far.Far <= 6, $"the squad took an 18% detour (far {far.Far})");
        Assert.True(far.OnPath < 0.2, $"{far.OnPath:0.00} of the soldier-ticks on the far path");
    }

    [Fact]
    public void ALoneSoldierTakesToAFootpathToo()
    {
        // His own route is searched with a deliberate slack (routes within a few percent of the best), so a 10% cheaper path
        // pulls him onto it for part of the way, not all of it.
        var near = Detour(42, 1);
        var none = Detour(42, 1, footpath: false);
        Assert.True(near.OnPath > 0.25, $"{near.OnPath:0.00} of the soldier-ticks on the path");
        Assert.True(near.Far > none.Far, $"far {near.Far} with the path, {none.Far} without");
        var away = Detour(20, 1);
        Assert.True(away.Far <= 6, $"the soldier took an 18% detour (far {away.Far})");
    }

    [Fact]
    public void AFootpathCrossingAPavedRoadLeavesThePavedRoadsSpeedAndPassabilityAlone()
    {
        // A Humvee and a tank along a paved road crossed by a footpath and an alley: the same positions tick by tick as on the
        // road alone.
        const string Road = """{ "points": [[0, 20], [95, 20]], "width": 3 }""";
        foreach (var unit in new[] { "humvee", "main_battle_tank", "sentinel_cargo_truck" })
        {
            var alone = Walk($$"""{ "width": 96, "height": 96, "roads": [{{Road}}] }""", unit);
            var crossed = Walk($$"""
                { "width": 96, "height": 96, "roads": [
                  {{Road}},
                  { "points": [[30, 0], [30, 95]], "width": 3, "kind": "footpath" },
                  { "points": [[60, 0], [60, 95]], "width": 3, "kind": "alley" },
                  { "points": [[0, 20], [95, 20]], "width": 1, "kind": "footpath" }
                ] }
                """, unit);
            output.WriteLine($"{unit}: {alone.Count} ticks on the road alone, {crossed.Count} crossed");
            Assert.Equal(alone, crossed);
        }
    }

    // ---- Determinism ----

    [Fact]
    public void TheSameRunTwiceHashesTheSameAtEveryTick()
    {
        ulong[] Run()
        {
            var w = Town();
            var vehicles = Vehicles.Select((v, i) => w.SpawnCrewed(1, v, 6, 20 + i * 4, Facing.East).Vehicle).ToList();
            var squad = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 6, 29 + i)).ToList();
            w.Order(1, CommandKind.Move, vehicles, cell: new Cell(58, 31));
            w.Order(1, CommandKind.Move, squad, cell: new Cell(58, 31));
            var hashes = new List<ulong>();
            for (var t = 0; t < 400; t++)
            {
                w.Step();
                hashes.Add(w.Hash());
            }
            return [.. hashes];
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a, b);
        Assert.True(a.Distinct().Count() > 100); // (a test, not the sim: the hashes move)
    }
}
