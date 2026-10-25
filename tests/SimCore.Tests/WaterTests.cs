using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Scenarios;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Water (docs/design.md, "Water"): rivers, lakes and the sea in the map file, each with a flat surface height; a cell is water
// when its centre lies in the water or on its edge. No ground unit enters open water or is put there (no swimming); shots and
// sight cross it at its surface; a blast there digs no crater and its events say Water. A deck over a water cell (the bridge
// hook) makes it ground again. Maps without water hash and play exactly as before.
public class WaterTests(ITestOutputHelper output)
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int M = 512; // raw per metre

    // LoadJson writes a map JSON (no heightmap) to a temporary directory and loads it.
    private static TerrainMap LoadJson(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "simcore-water-" + Guid.NewGuid().ToString("N"));
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

    // Map is a terrain of n x h cells, corner heights from height (raw; flat without one), with these bodies of water.
    private static TerrainMap Map(int n, int h, IReadOnlyList<WaterBody> water, Func<int, int, int>? height = null)
    {
        var corners = new int[(n + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = height?.Invoke(x, y) ?? 0;
            }
        }
        return new TerrainMap(n, h, corners, new byte[n * h], Rules, water);
    }

    private static WaterBody Lake(int x0, int y0, int x1, int y1, int surfaceRaw = 0) =>
        WaterBody.Area(WaterKind.Lake, Fixed.FromRaw(surfaceRaw), [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]);

    // A river from (x, y0) to (x, y1), width cells wide.
    private static WaterBody RiverNS(int x, int y0, int y1, int width) =>
        WaterBody.River(Fixed.Zero, [new Cell(x, y0), new Cell(x, y1)], [width, width]);

    private static string Rows(TerrainMap t, int x0, int x1, int y0, int y1)
    {
        var s = new System.Text.StringBuilder();
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                s.Append(t.Water(new Cell(x, y)) ? '~' : '.');
            }
            s.Append('\n');
        }
        return s.ToString();
    }

    // ---- The map file ----

    [Fact]
    public void ParsesRiversLakesAndTheSeaWithTheirSurfaceHeights()
    {
        var t = LoadJson("""
            {
              "width": 40, "height": 30,
              "forest": [{ "rect": [0, 0, 40, 30], "density": 2 }],
              "water": [
                { "kind": "river", "points": [[0, 5], [20, 5, 5], [39, 5]], "width": 3, "height": -10 },
                { "kind": "lake", "polygon": [[10, 15], [20, 15], [20, 25], [10, 25]], "height": -5 },
                { "kind": "sea", "polygon": [[30, 20], [40, 20], [40, 30], [30, 30]], "height": 0 }
              ]
            }
            """);
        output.WriteLine(Rows(t, 0, 39, 0, 29));
        Assert.True(t.HasWater);
        Assert.Equal([WaterKind.River, WaterKind.Lake, WaterKind.Sea], t.WaterBodies.Select(b => b.Kind));
        Assert.Equal([-M, -M / 2, 0], t.WaterBodies.Select(b => (int)b.Surface.Raw)); // tenths of a metre, as pads
        // The river: 3 cells wide at its ends (rows 4 to 6), 5 at its middle point (rows 3 to 7).
        Assert.Equal([false, true, true, true, false], Enumerable.Range(3, 5).Select(y => t.Water(new Cell(0, y))));
        Assert.Equal([false, true, true, true, true, true, false], Enumerable.Range(2, 7).Select(y => t.Water(new Cell(20, y))));
        Assert.Equal(1, t.WaterIdAt(new Cell(0, 5)));
        Assert.Equal(new Vec(Fixed.One, Fixed.Zero), t.WaterFlowAt(new Cell(10, 4))); // it flows in the points' order: east
        // The lake: cells 10..19 by 15..24 (its outline runs on cell corners).
        Assert.True(t.Water(new Cell(10, 15)) && t.Water(new Cell(19, 24)));
        Assert.False(t.Water(new Cell(9, 15)) || t.Water(new Cell(20, 15)) || t.Water(new Cell(10, 14)) || t.Water(new Cell(10, 25)));
        Assert.Equal((2, WaterKind.Lake), (t.WaterIdAt(new Cell(15, 20)), t.WaterBodyAt(new Cell(15, 20))!.Kind));
        Assert.Equal(Fixed.FromRaw(-M / 2), t.WaterSurfaceAt(new Cell(15, 20)));
        Assert.Equal(default, t.WaterFlowAt(new Cell(15, 20))); // still water
        // The sea, to the map's corner.
        Assert.Equal(3, t.WaterIdAt(new Cell(39, 29)));
        Assert.Equal(0, t.WaterIdAt(new Cell(29, 29)));
        Assert.Null(t.WaterBodyAt(new Cell(-1, 0)));
        // No forest in the water.
        Assert.Equal(0, t.Forest(new Cell(15, 20)));
        Assert.Equal(2, t.Forest(new Cell(5, 20)));
    }

    [Theory]
    [InlineData("""{ "kind": "pond", "polygon": [[0, 0], [4, 0], [4, 4]], "height": 0 }""", "pond")]
    [InlineData("""{ "kind": "lake", "polygon": [[0, 0], [4, 0], [4, 4]] }""", "height")]
    [InlineData("""{ "kind": "river", "points": [[0, 0]], "height": 0 }""", "points")]
    [InlineData("""{ "kind": "river", "points": [[0, 0], [5, 0]], "width": 0, "height": 0 }""", "width")]
    [InlineData("""{ "kind": "river", "points": [[0, 0], [5, 0, 0]], "height": 0 }""", "width")]
    [InlineData("""{ "kind": "lake", "points": [[0, 0], [4, 0], [4, 4]], "height": 0 }""", "polygon")]
    [InlineData("""{ "kind": "sea", "polygon": [[0, 0], [4, 0, 1], [4, 4]], "height": 0 }""", "polygon")]
    [InlineData("""{ "kind": "lake", "polygon": [[0, 0], [4, 0], [4, 4]], "height": 9999 }""", "height")]
    [InlineData("""{ "kind": "lake", "polygon": [[0, 0], [4, 0], [4, 4]], "height": 0, "depth": 3 }""", "depth")]
    public void BadWaterIsAnError(string entry, string mention)
    {
        var e = Assert.Throws<ContentException>(() => LoadJson($$"""{ "width": 8, "height": 8, "water": [{{entry}}] }"""));
        Assert.Contains(mention, e.Message);
    }

    [Fact]
    public void ACellIsWaterWhenItsCentreLiesInTheWaterOrOnItsEdge()
    {
        // A river's width w covers the cells whose centre is within w / 2 of its line: w cells across a straight stretch
        // for an odd width, w + 1 for an even one (the edge counts); a diagonal one stays watertight for a mover (no
        // diagonal step cuts a corner).
        var t = Map(30, 30, [
            RiverNS(2, 0, 29, 1), RiverNS(6, 0, 29, 2), RiverNS(12, 0, 29, 3), RiverNS(20, 0, 29, 4),
        ]);
        int Across(int x0, int x1) => Enumerable.Range(x0, x1 - x0 + 1).Count(x => t.Water(new Cell(x, 10)));
        Assert.Equal(1, Across(0, 4));
        Assert.Equal(3, Across(4, 8));
        Assert.Equal(3, Across(9, 15));
        Assert.Equal(5, Across(16, 25));
        var diag = Map(12, 12, [WaterBody.River(Fixed.Zero, [new Cell(0, 0), new Cell(11, 11)], [1, 1])]);
        output.WriteLine(Rows(diag, 0, 11, 0, 11));
        Assert.True(diag.Water(new Cell(5, 5)) && !diag.Water(new Cell(5, 6)) && !diag.Water(new Cell(6, 5)));
        // A triangle: a cell counts by its centre; one on the slanted edge counts too.
        var tri = Map(10, 10, [WaterBody.Area(WaterKind.Lake, Fixed.Zero, [(0, 0), (8, 0), (0, 8)])]);
        output.WriteLine(Rows(tri, 0, 9, 0, 9));
        Assert.True(tri.Water(new Cell(0, 0)) && tri.Water(new Cell(6, 0)) && tri.Water(new Cell(3, 3)));
        Assert.False(tri.Water(new Cell(4, 4)) || tri.Water(new Cell(7, 1)) || tri.Water(new Cell(8, 0)));
        Assert.True(tri.Water(new Cell(3, 4))); // its centre (3.5, 4.5) lies on the edge x + y = 8
        // Where bodies overlap the later one wins.
        var both = Map(20, 20, [RiverNS(10, 0, 19, 3), Lake(5, 5, 15, 15, 7)]);
        Assert.Equal(2, both.WaterIdAt(new Cell(10, 10)));
        Assert.Equal(1, both.WaterIdAt(new Cell(10, 2)));
    }

    // ---- Storage and the hash ----

    [Fact]
    public void MapsWithoutWaterHashAndReadAsBefore()
    {
        // The digest of this map on simcore 0acc631 (before water): SimRun -terrain-bench on the same JSON. Re-pinned when the
        // terrain rules gained their trees block (tree-trunks): with the rules before it the map still gives 0x11e94e9299bf36a2.
        // Re-pinned again when the owner accepted the tree defaults (2026-10-25) and trees.trunkChance went from [0, 0, 35, 70]
        // to [0, 0, 50, 85]: the digest covers the terrain rules, and with the old chances this map gave 0x1cdfe664e830b835.
        const string Dry = """
            { "width": 40, "height": 30,
              "forest": [{ "rect": [2, 2, 10, 6], "density": 2 }, { "rect": [20, 15, 5, 5], "density": 3 }],
              "roads": [{ "points": [[0, 12], [39, 20]], "width": 3, "kind": "gravel" }, { "rect": [30, 2, 4, 8] }],
              "pads": [{ "rect": [5, 20, 6, 4], "height": 15, "blend": 2 }] {{WATER}} }
            """;
        var dry = LoadJson(Dry.Replace("{{WATER}}", ""));
        Assert.Equal(0xb2970a8a29d459b3UL, dry.Digest());
        Assert.False(dry.HasWater);
        Assert.Empty(dry.WaterBodies);
        Assert.Equal(dry.Digest(), LoadJson(Dry.Replace("{{WATER}}", ", \"water\": []")).Digest());
        for (var i = 0; i < 200; i++)
        {
            var p = new Vec(Fixed.FromRaw(i * 211 % (40 * 1024)), Fixed.FromRaw(i * 137 % (30 * 1024)));
            Assert.Equal(dry.GroundHeightAt(p), dry.HeightAt(p));
            Assert.False(dry.Water(Cell.Of(p)) || dry.OpenWater(Cell.Of(p)));
        }
        // Water joins the hash: its cells, kind, surface and shape.
        var lake = LoadJson(Dry.Replace("{{WATER}}", ", \"water\": [{ \"kind\": \"lake\", \"polygon\": [[30, 22], [38, 22], [38, 28], [30, 28]], \"height\": 0 }]"));
        var higher = LoadJson(Dry.Replace("{{WATER}}", ", \"water\": [{ \"kind\": \"lake\", \"polygon\": [[30, 22], [38, 22], [38, 28], [30, 28]], \"height\": 5 }]"));
        var sea = LoadJson(Dry.Replace("{{WATER}}", ", \"water\": [{ \"kind\": \"sea\", \"polygon\": [[30, 22], [38, 22], [38, 28], [30, 28]], \"height\": 0 }]"));
        Assert.Equal(4, new[] { dry.Digest(), lake.Digest(), higher.Digest(), sea.Digest() }.Distinct().Count());
        Assert.Equal(lake.Digest(), LoadJson(Dry.Replace("{{WATER}}", ", \"water\": [{ \"kind\": \"lake\", \"polygon\": [[30, 22], [38, 22], [38, 28], [30, 28]], \"height\": 0 }]")).Digest());
        // The flags' water and deck bits alone mean nothing.
        var stray = new byte[8 * 4];
        stray[0] = 1 << 6;
        stray[1] = 1 << 7;
        Assert.Equal(TerrainMap.Flat(8, 4, Rules).Digest(), new TerrainMap(8, 4, new int[9 * 5], stray, Rules).Digest());
        Assert.False(new TerrainMap(8, 4, new int[9 * 5], stray, Rules).Water(new Cell(0, 0)));
    }

    [Fact]
    public void TheSurfaceIsFlatAtTheWaterHeightTheBedBelow()
    {
        // A lake over a bed 6 m down (corners inside its outline at -6 m), its surface at -1 m; the banks at 0.
        var t = Map(30, 30, [Lake(10, 10, 20, 20, -M)], (x, y) => x > 10 && x < 20 && y > 10 && y < 20 ? -6 * M : 0);
        var mid = new Cell(15, 15).Center();
        Assert.Equal(Fixed.FromRaw(-6 * M), t.GroundHeightAt(mid));
        Assert.Equal(Fixed.FromRaw(-M), t.HeightAt(mid));
        Assert.Equal(Fixed.FromRaw(-M), t.WaterSurfaceAt(new Cell(15, 15)));
        Assert.Equal(t.GroundHeightAt(new Cell(5, 5).Center()), t.HeightAt(new Cell(5, 5).Center()));
        // Where the bed rises above the surface (the lake's edge cells, their outer corners at 0) the ground shows.
        Assert.Equal(t.GroundHeightAt(new Vec(Fixed.FromInt(10), Fixed.FromInt(15))), t.HeightAt(new Vec(Fixed.FromInt(10), Fixed.FromInt(15))));
    }

    // ---- No swimming ----

    [Fact]
    public void EveryGroundClassRefusesOpenWater()
    {
        var t = Map(20, 20, [Lake(5, 5, 15, 15)]);
        var c = new Cell(10, 10);
        Assert.True(t.OpenWater(c));
        for (var k = 0; k < Rules.Classes.Count; k++)
        {
            Assert.False(t.Passable(k, c), Rules.Classes[k].Id);
            Assert.Equal(0, t.SpeedPercent(k, c, uphill: false));
            Assert.Equal(0, t.StepCost(k, new Cell(4, 10), new Cell(5, 10)));
            Assert.True(t.Passable(k, new Cell(4, 10)), Rules.Classes[k].Id); // the bank
        }
        // Every locomotor the rules know is covered.
        Assert.Equal([Locomotor.Foot, Locomotor.Wheeled, Locomotor.Tracked], Rules.Classes.Select(m => m.Locomotor).Distinct().Order());
        // On the world's grid it is open water: not passable, not rock.
        var w = Arena.New(20);
        w.AttachTerrain(t);
        Assert.Equal(Terrain.Water, w.Grid.At(c));
        Assert.False(w.Grid.Passable(c));
        Assert.True(w.InWater(c) && w.InWater(c.Center()) && !w.InWater(new Cell(4, 10)));
    }

    // Crossing orders units of every ground class (foot, light wheeled, wheeled, heavy wheeled, tracked) from x = 10 to
    // x = 70 across a river at x = 40 (3 wide) running from y = 0 to y = gapFrom - 1 on an 80 x 80 map, and returns them
    // after ticks; it fails if any of them ever stands in the water.
    private (World W, List<Unit> Units) Crossing(int gapFrom, int ticks)
    {
        var w = Arena.New(80);
        w.AttachTerrain(Map(80, 80, [RiverNS(40, 0, gapFrom - 1, 3)]));
        var units = new List<Unit> { w.Spawn(1, "Rifleman", 10, 10) };
        var y = 14;
        foreach (var v in new[] { "technical", "sam_vehicle", "cargo_truck", "kub_launcher" })
        {
            units.Add(w.SpawnCrewed(1, v, 10, y, Facing.East).Vehicle);
            y += 4;
        }
        var classes = units.Select(u => Rules.Classes[w.Ground!.Rules.ClassOf(u.Def)].Id).ToList();
        Assert.Equal(["foot", "light_wheeled", "wheeled", "heavy_wheeled", "tracked"], classes);
        foreach (var u in units)
        {
            w.Order(1, CommandKind.Move, [u], cell: new Cell(70, Cell.Of(u.Pos).Y));
        }
        for (var t = 0; t < ticks; t++)
        {
            w.Step();
            foreach (var u in units)
            {
                Assert.False(w.InWater(u.Pos), $"{u.Def.Archetype.Id} in the water at {Cell.Of(u.Pos)}, tick {w.Tick}");
            }
        }
        return (w, units);
    }

    [Fact]
    public void NoGroundClassCrossesARiverWithoutABridge()
    {
        var (_, units) = Crossing(80, 1500); // the river runs the whole map's height
        foreach (var u in units)
        {
            output.WriteLine($"{u.Def.Archetype.Id}: {Cell.Of(u.Pos)}");
            Assert.True(Cell.Of(u.Pos).X < 40, $"{u.Def.Archetype.Id} crossed to {Cell.Of(u.Pos)}");
        }
        // They came up to the bank, not stuck back home.
        Assert.All(units, u => Assert.True(Cell.Of(u.Pos).X >= 30, $"{u.Def.Archetype.Id} stopped at {Cell.Of(u.Pos)}"));
    }

    [Fact]
    public void ARiverWithAnEndIsDrivenRound()
    {
        var (_, units) = Crossing(36, 2400); // dry from row 36: the long way round
        foreach (var u in units)
        {
            output.WriteLine($"{u.Def.Archetype.Id}: {Cell.Of(u.Pos)}");
            Assert.True(Cell.Of(u.Pos).X >= 66, $"{u.Def.Archetype.Id} did not get round: {Cell.Of(u.Pos)}");
        }
    }

    [Fact]
    public void NothingIsPutInTheWater()
    {
        var w = Arena.New(48, edit: s => s with { Scenario = Scenario.SandboxName });
        w.AttachTerrain(Map(48, 48, [Lake(20, 10, 40, 40)]));
        var worker = w.Spawn(1, "worker", 15, 20);
        var depot = w.Player(1)!.Setup.Find(w.Content, "supply_depot")!;
        Assert.False(w.CanPlace(depot, new Cell(18, 20), 0, [worker])); // 6 x 6: two columns in the lake
        Assert.True(w.CanPlace(depot, new Cell(13, 21), 0, [worker]));
        // A unit asked to stand in the water stands on the nearest dry cell (a scenario's spawn, the sandbox's).
        var r = w.Spawn(1, "Rifleman", 21, 25);
        Assert.False(w.InWater(r.Pos));
        Assert.Equal((19, 2), (Cell.Of(r.Pos).X, Math.Max(Math.Abs(Cell.Of(r.Pos).X - 21), Math.Abs(Cell.Of(r.Pos).Y - 25))));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "Rifleman", Cell = new Cell(30, 25) });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "supply_depot", Cell = new Cell(25, 25) });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "supply_depot", Cell = new Cell(2, 40) });
        w.Step();
        var spawned = w.Units.Last(u => u.Def.Name == "Rifleman");
        Assert.False(w.InWater(spawned.Pos));
        var dryDepot = Assert.Single(w.All("supply_depot")); // the one on dry ground only
        Assert.Equal(new Cell(2, 40), World.FootprintOrigin(dryDepot));
        // A mine is never laid in the water (on the bank it is).
        var layer = w.Spawn(1, "Rifleman", 18, 30);
        w.Order(1, CommandKind.UseAbility, [layer], cell: new Cell(21, 30), ability: "ap_mine_kit");
        w.Run(100);
        Assert.Empty(w.All("ap_mine"));
        w.Order(1, CommandKind.UseAbility, [layer], cell: new Cell(18, 32), ability: "ap_mine_kit");
        w.Run(100);
        Assert.Equal(new Cell(18, 32), Cell.Of(Assert.Single(w.All("ap_mine")).Pos));
        // A build order on the water makes no site.
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = new Cell(25, 20), Unit = "supply_depot" });
        w.Run(100);
        Assert.Single(w.All("supply_depot"));
    }

    [Fact]
    public void AScenarioFormsItsArmiesUpOnDryGround()
    {
        var setup = TestContent.Battle(3);
        var match = MatchCompiler.Compile(TestContent.Mvp, setup);
        // The battle's rally points sit at (14, h/2) and mirrored: a lake over the west one.
        var map = Map(96, 96, [Lake(8, 38, 22, 58)]);
        var w = Scenario.Create(match, map);
        w.AttachTerrain(map);
        Assert.NotEmpty(w.Units);
        Assert.All(w.Units.Where(u => !u.Def.IsStructure && u.Inside == 0), u => Assert.False(w.InWater(u.Pos), $"{u.Def.Name} at {Cell.Of(u.Pos)}"));
    }

    // ---- Blasts in the water ----

    [Fact]
    public void AShellInTheWaterMakesNoCraterAndSaysSoItsSplashStillHurts()
    {
        var w = Arena.New(64);
        w.AttachTerrain(Map(64, 64, [Lake(20, 20, 40, 44, -M)], (x, y) => x > 20 && x < 40 && y > 20 && y < 44 ? -4 * M : 0));
        var terrain = w.Ground!.Digest();
        var grid = w.Grid.Digest();
        var mortar = w.Spawn(1, "mortar", 8, 32, Facing.East);
        var friend = w.Spawn(1, "Rifleman", 19, 33); // on the bank, in the splash's reach
        w.Order(1, CommandKind.AttackGround, [mortar], cell: new Cell(21, 32));
        var booms = new List<Detonation>();
        for (var t = 0; t < 1200 && friend.Alive; t++)
        {
            w.Step();
            booms.AddRange(w.Detonations);
        }
        output.WriteLine(string.Join(" ", booms.Select(d => $"({d.At.X.Raw / 1024.0:0.0},{d.At.Y.Raw / 1024.0:0.0}){(d.Water ? "~" : "")}")));
        Assert.NotEmpty(booms);
        Assert.Contains(booms, d => d.Water);
        Assert.All(booms, d => Assert.Equal(d.Hit == 0 && !d.Terrain && w.Ground.OpenWater(Cell.Of(d.At)), d.Water));
        Assert.True(friend.HP < friend.Def.MaxHp, "the splash from the water reaches the bank");
        Assert.Equal(terrain, w.Ground.Digest()); // no crater, no ground change
        Assert.Equal(grid, w.Grid.Digest());
        Assert.Empty(w.Craters);
    }

    [Fact]
    public void ABunkerBusterInALakeLeavesNoCrater()
    {
        var w = Arena.NewSentinel(400);
        w.AttachTerrain(Map(400, 400, [Lake(235, 185, 265, 215, -M)], (x, y) => x > 235 && x < 265 && y > 185 && y < 215 ? -8 * M : 0));
        var terrain = w.Ground!.Digest();
        var b = w.Spawn(1, "stealth_bomber", 40, 200, Facing.East);
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetBombWeapon, Units = [b.Id], Index = 1 });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetFireMode, Units = [b.Id], Index = 1 }); // single
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AttackGround, Units = [b.Id], Cell = new Cell(250, 200) });
        var impacts = new List<BombImpact>();
        for (var t = 0; t < 14000 && impacts.Count == 0; t++)
        {
            w.Step();
            impacts.AddRange(w.BombImpacts);
            Assert.Empty(w.Craters);
        }
        var i = Assert.Single(impacts);
        Assert.True(i.Water);
        Assert.Equal(CraterKind.None, i.Crater);
        Assert.Equal(Fixed.FromRaw(-M), i.Altitude); // it went off at the surface, not on the bed 8 m down
        Assert.Contains(w.Detonations, d => d.Projectile == i.Bomb && d.Water);
        Assert.Equal(terrain, w.Ground.Digest());
    }

    // ---- Sight ----

    [Fact]
    public void SightCrossesALakeAtItsSurface()
    {
        // Banks at 0 m, a bed 6 m down, the surface at -1 m.
        var map = Map(64, 64, [Lake(12, 20, 16, 40, -M)], (x, y) => x > 12 && x < 16 && y > 20 && y < 40 ? -6 * M : 0);
        var w = Arena.New(64, edit: s => s with { Fog = true });
        w.AttachTerrain(map);
        var me = w.Spawn(1, "Rifleman", 10, 30); // vision 7 cells
        var them = w.Spawn(2, "Rifleman", 16, 30); // on the far bank
        w.Run(1);
        Assert.True(World.IsVisibleTo(them, 1));
        Assert.True(w.FogOf(1)!.Visible(14, 30)); // the water itself
        // The surface, not the bed, is what a low line meets: over the water 2 m under the banks it is blocked by the
        // water at -1 m (the bed lies 6 m down).
        Vec a = new Cell(12, 30).Center(), b = new Cell(15, 30).Center();
        Assert.True(w.LineOfSight(a, Fixed.FromRaw(M), b, Fixed.FromRaw(M)));
        Assert.False(w.LineOfSight(a, Fixed.FromRaw(-2 * M), b, Fixed.FromRaw(-2 * M)));
        Assert.Equal(Fixed.FromRaw(-M), w.GroundHeight(new Cell(14, 30).Center()));
    }

    // ---- The bridge hook ----

    [Fact]
    public void ADeckMakesWaterCellsGroundAgainAndLiftingItTakesThatBack()
    {
        var w = Arena.New(80);
        w.AttachTerrain(Map(80, 80, [RiverNS(40, 0, 79, 3)]));
        var g = w.Ground!;
        var before = g.Digest();
        for (var x = 39; x <= 41; x++)
        {
            for (var y = 20; y <= 23; y++)
            {
                w.SetDeck(new Cell(x, y), true);
            }
        }
        var c = new Cell(40, 21);
        Assert.True(g.Water(c) && g.Deck(c) && !g.OpenWater(c) && !w.InWater(c));
        Assert.Equal(Terrain.Clear, w.Grid.At(c));
        Assert.All(Enumerable.Range(0, Rules.Classes.Count), k => Assert.True(g.Passable(k, c)));
        Assert.NotEqual(before, g.Digest());
        w.SetDeck(new Cell(5, 5), true); // a dry cell: nothing
        Assert.False(g.Deck(new Cell(5, 5)));
        // A truck crosses on it.
        var (truck, _) = w.SpawnCrewed(1, "cargo_truck", 10, 21, Facing.East);
        w.Order(1, CommandKind.Move, [truck], cell: new Cell(70, 21));
        for (var t = 0; t < 1500 && Cell.Of(truck.Pos).X < 69; t++)
        {
            w.Step();
            Assert.False(w.InWater(truck.Pos));
        }
        Assert.True(Cell.Of(truck.Pos).X >= 69, $"the truck stopped at {Cell.Of(truck.Pos)}");
        // Lifted, it is open water again (and hashes as before).
        for (var x = 39; x <= 41; x++)
        {
            for (var y = 20; y <= 23; y++)
            {
                w.SetDeck(new Cell(x, y), false);
            }
        }
        Assert.True(g.OpenWater(c) && w.InWater(c));
        Assert.Equal(Terrain.Water, w.Grid.At(c));
        Assert.Equal(before, g.Digest());
    }
}
