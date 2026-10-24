using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Scenarios;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Bridges (docs/design.md, "Bridges"): neutral structures the map places over water (and the sandbox), never built by players;
// the footprint's long side is the road, its cells over water the deck (flat ground with the bridge's road while the span
// stands). Damage stages by HP; at 0 HP the span falls: the deck is lifted, whoever is on it is lost in the water, ways across
// are re-planned, the ruin stays. Builders of either side repair it; the deck comes back at the reopen percent.
public class BridgeTests(ITestOutputHelper output)
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int M = 512; // raw per metre

    // Map is an n x n terrain with these bodies of water; corner heights from height (raw; flat without one).
    private static TerrainMap Map(int n, IReadOnlyList<WaterBody> water, Func<int, int, int>? height = null)
    {
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = height?.Invoke(x, y) ?? 0;
            }
        }
        return new TerrainMap(n, n, corners, new byte[n * n], Rules, water);
    }

    // A river running west to east along row y across the whole map, width cells wide, its surface 0.4 m under the banks.
    private static WaterBody RiverEW(int n, int y, int width = 3) =>
        WaterBody.River(Fixed.FromRaw(-M * 4 / 10), [new Cell(0, y), new Cell(n - 1, y)], [width, width]);

    // A river running north to south along column x across the whole map.
    private static WaterBody RiverNS(int n, int x, int width = 3) =>
        WaterBody.River(Fixed.FromRaw(-M * 4 / 10), [new Cell(x, 0), new Cell(x, n - 1)], [width, width]);

    // A carved bed: the corners strictly inside a west-east river of width 3 along row y are 3 m down (the cells over it grade
    // as cliffs: impassable even as dry ground).
    private static Func<int, int, int> Bed(int y) => (_, cy) => cy == y || cy == y + 1 ? -3 * M : 0;

    private static World River(int n = 64, int y = 30, bool sentinel = false, bool sandbox = false, IEnumerable<BridgeSite>? sites = null, bool bed = false)
    {
        Func<MatchSetup, MatchSetup>? edit = sandbox ? s => s with { Scenario = Scenario.SandboxName } : null;
        var w = sentinel ? Arena.NewSentinel(n, edit: edit) : Arena.New(n, edit: edit);
        var map = Map(n, [RiverEW(n, y)], bed ? Bed(y) : null);
        foreach (var s in sites ?? [])
        {
            map.AddBridgeSite(s);
        }
        w.AttachTerrain(map);
        return w;
    }

    // A concrete bridge (5 x 12, the road north-south) over the river along row 30, from (30, 24) to (34, 35): its deck rows 29-31.
    private static readonly BridgeSite Concrete = new("concrete", new Cell(30, 24), Facing.South);

    private static Unit OnlyBridge(World w) => Assert.Single(w.Bridges);

    // ---- Content ----

    [Theory]
    [InlineData("bridge_stone", 3, 8, 2500, RoadKind.Paved, 35)]
    [InlineData("bridge_concrete", 5, 12, 3500, RoadKind.Paved, 37)]
    [InlineData("bridge_metal", 9, 18, 4500, RoadKind.Highway, 90)]
    public void ThreeBridgesWithTheArtsSizesNeverBuilt(string id, int w, int h, int hp, RoadKind road, int height)
    {
        var a = TestContent.Mvp.Archetype(id)!;
        Assert.Equal((w, h), (a.FootprintW, a.FootprintH)); // w across, h along the road (the art's glTF X and Z)
        Assert.Equal(hp, a.MaxHp);
        Assert.Equal("building", TestContent.Mvp.ArmorClasses[a.Armor]);
        Assert.Equal(height * 512L / 10, a.HeightRaw);
        var b = a.Bridge!;
        Assert.Equal(road, b.Road);
        Assert.Equal([70, 40], b.Stages);
        Assert.Equal(40, b.ReopenPercent);
        Assert.Equal(3, b.DownStage);
        Assert.Equal([100, 50, 0], b.Helpers);
        Assert.Null(a.Construction); // players never build one
        Assert.False(a.Neutral || a.Pavement || a.Untargetable || a.Invulnerable);
        Assert.Equal(100, a.RepairPercentOfBuild);
        Assert.True(World.CanTurn(a));
        foreach (var f in TestContent.Mvp.Factions)
        {
            Assert.True(f.Archetypes[a.Index], $"{f.Id} lists {id}"); // the map puts them up in any match
        }
    }

    public static TheoryData<string, string, string> BadBridgeContent => new()
    {
        { "buildable", """ "construction": { "startHp": 10, "stages": [15, 40, 70, 95], "refund": 75, "helpers": [100], "padBlend": 0 }, "bridge": { "road": "paved", "stages": [70, 40], "reopen": 40 } """, "construction" },
        { "square", """ "bridge": { "road": "paved", "stages": [70, 40], "reopen": 40 } """, "longer" },
        { "unknown road", """ "bridge": { "road": "tarmac", "stages": [70, 40], "reopen": 40 } """, "tarmac" },
        { "stages up", """ "bridge": { "road": "paved", "stages": [40, 70], "reopen": 40 } """, "bridge.stages" },
        { "no stages", """ "bridge": { "road": "paved", "stages": [], "reopen": 40 } """, "bridge.stages" },
        { "reopen 0", """ "bridge": { "road": "paved", "stages": [70, 40], "reopen": 0 } """, "bridge.reopen" },
        { "neutral", """ "neutral": true, "bridge": { "road": "paved", "stages": [70, 40], "reopen": 40 } """, "neutral" },
    };

    [Theory]
    [MemberData(nameof(BadBridgeContent))]
    public void BadBridgeContentIsAnError(string name, string fields, string mention)
    {
        var size = name == "square" ? """{ "w": 5, "h": 5 }""" : """{ "w": 5, "h": 12 }""";
        var repair = """, "repairable": { "fullPercentOfBuild": 100 }""";
        var json = $$"""{"archetypes":[{"id":"bad_bridge","name":"Bad","glyph":"X","tags":["structure"],"locomotor":"none","armor":"building","hp":100,"footprint":{{size}},"buildTime":100{{repair}}, {{fields}} }]}""";
        var sources = TestContent.MvpSources();
        sources.Add(new ContentSource("extra/bad.json", json));
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.True(e.Message.Contains(mention, StringComparison.Ordinal), $"{name}: {e.Message}");
        // And a bridge must be repairable.
        var unrepairable = $$"""{"archetypes":[{"id":"bad_bridge","name":"Bad","glyph":"X","tags":["structure"],"locomotor":"none","armor":"building","hp":100,"footprint":{ "w": 3, "h": 8 },"buildTime":100, "bridge": { "road": "paved", "stages": [70, 40], "reopen": 40 } }]}""";
        sources[^1] = new ContentSource("extra/bad.json", unrepairable);
        Assert.Contains("repairable", Assert.Throws<ContentException>(() => ContentLoader.Load(sources)).Message);
    }

    // ---- The map puts them up ----

    // LoadJson writes a map JSON (no heightmap) to a temporary directory and loads it (checking its bridges against the content).
    private static TerrainMap LoadJson(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "simcore-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "map.json"), json);
            return TerrainLoader.LoadMap(Path.Combine(dir, "map.json"), Rules, TestContent.Mvp);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string MapJson(string bridges) => $$"""
        {
          "width": 64, "height": 64,
          "roads": [{ "points": [[32, 0], [32, 63]], "width": 3, "kind": "dirt" }],
          "water": [
            { "kind": "river", "points": [[0, 30], [63, 30]], "width": 3, "height": -4 },
            { "kind": "river", "points": [[50, 0], [50, 63]], "width": 3, "height": -4 }
          ],
          "bridges": [{{bridges}}]
        }
        """;

    [Fact]
    public void TheMapPutsItsBridgesUpWithTheirRoadAndDeck()
    {
        var map = LoadJson(MapJson("""
            { "kind": "concrete", "cell": [30, 24], "dir": "ns" },
            { "kind": "stone", "cell": [46, 10], "dir": "ew" },
            { "kind": "metal", "cell": [6, 21], "dir": "ns" }
            """));
        Assert.Equal([new BridgeSite("concrete", new Cell(30, 24), Facing.South), new BridgeSite("stone", new Cell(46, 10), Facing.East),
            new BridgeSite("metal", new Cell(6, 21), Facing.South)], map.BridgeSites);
        var w = Arena.New(64);
        w.AttachTerrain(map);
        var g = w.Ground!;
        Assert.Equal(["bridge_concrete", "bridge_stone", "bridge_metal"], w.Bridges.Select(b => b.Def.Archetype.Id));
        foreach (var b in w.Bridges)
        {
            Assert.Equal(0, b.Owner); // neutral for good
            Assert.Equal((0, false, false), (b.Stage, b.Bridge!.Down, b.Bridge.Repairing));
            Assert.Equal(b.Def.MaxHp, b.HP);
            var (o, fw, fh) = (World.FootprintOrigin(b), World.FootW(b), World.FootH(b));
            var road = b.Def.Archetype.Bridge!.Road;
            for (var y = o.Y; y < o.Y + fh; y++)
            {
                for (var x = o.X; x < o.X + fw; x++)
                {
                    var c = new Cell(x, y);
                    Assert.Equal(road, g.RoadKindAt(c)); // the bridge's road on every cell (the map's dirt track gave way)
                    Assert.True(w.Grid.Passable(c), $"{b.Def.Archetype.Id} blocks {c}"); // it blocks nothing
                    Assert.Equal(g.Water(c), b.Bridge.DeckCells.Contains(c));
                    Assert.Equal(g.Water(c), g.Deck(c));
                    Assert.False(g.OpenWater(c) || w.InWater(c));
                    Assert.Same(b, w.BridgeAt(c));
                }
            }
        }
        var concrete = w.Bridges[0];
        Assert.Equal((new Cell(30, 24), 5, 12, false), (World.FootprintOrigin(concrete), World.FootW(concrete), World.FootH(concrete), concrete.Turned));
        Assert.Equal(15, concrete.Bridge!.DeckCells.Count); // 5 across x rows 29-31
        var stone = w.Bridges[1];
        Assert.Equal((new Cell(46, 10), 8, 3, true), (World.FootprintOrigin(stone), World.FootW(stone), World.FootH(stone), stone.Turned)); // ew: turned
        Assert.Equal(9, stone.Bridge!.DeckCells.Count); // 3 rows x columns 49-51
        Assert.Equal(27, w.Bridges[2].Bridge!.DeckCells.Count);
        Assert.Null(w.BridgeAt(new Cell(1, 1)));
        Assert.Equal(RoadKind.Dirt, g.RoadKindAt(new Cell(32, 10))); // the track off the bridge keeps its kind
        // It goes into the hash with the terrain: a world without the sites differs.
        var bare = Arena.New(64);
        bare.AttachTerrain(LoadJson(MapJson("")));
        Assert.NotEqual(bare.Hash(), w.Hash());
        Assert.Empty(bare.Bridges);
    }

    [Theory]
    [InlineData("""{ "kind": "concrete", "cell": [30, 19], "dir": "ns" }""", "in the water")] // rows 19-30: its south end on the river
    [InlineData("""{ "kind": "concrete", "cell": [30, 2], "dir": "ns" }""", "spans no water")]
    [InlineData("""{ "kind": "concrete", "cell": [30, 28], "dir": "ew" }""", "in the water")] // turned along the river: its ends in it
    [InlineData("""{ "kind": "concrete", "cell": [60, 24], "dir": "ns" }""", "leaves the map")]
    [InlineData("""{ "kind": "wooden", "cell": [30, 24], "dir": "ns" }""", "bridge_wooden")]
    [InlineData("""{ "kind": "concrete", "cell": [30, 24], "dir": "nw" }""", "dir")]
    [InlineData("""{ "kind": "concrete", "cell": [30], "dir": "ns" }""", "cell")]
    [InlineData("""{ "kind": "Concrete!", "cell": [30, 24], "dir": "ns" }""", "kind")]
    [InlineData("""{ "kind": "concrete", "cell": [30, 24], "dir": "ns", "hp": 5 }""", "hp")]
    [InlineData("""{ "kind": "concrete", "cell": [30, 24], "dir": "ns" }, { "kind": "stone", "cell": [33, 26], "dir": "ns" }""", "overlaps bridges[0]")]
    public void ABridgeThatCannotStandIsALoadError(string bridges, string mention)
    {
        var e = Assert.Throws<ContentException>(() => LoadJson(MapJson(bridges)));
        output.WriteLine(e.Message);
        Assert.Contains(mention, e.Message);
    }

    [Fact]
    public void TheWorldChecksTheMapsBridgesToo()
    {
        // Loaded without the content (no check), the world refuses the bad site when the terrain is attached.
        var map = Map(64, [RiverEW(64, 30)]);
        map.AddBridgeSite(new BridgeSite("concrete", new Cell(30, 19), Facing.South));
        var e = Assert.Throws<MatchSetupException>(() => Arena.New(64).AttachTerrain(map));
        Assert.Contains("bridges[0]", e.Message);
        Assert.Contains("in the water", e.Message);
        var unknown = Map(64, [RiverEW(64, 30)]);
        unknown.AddBridgeSite(new BridgeSite("rope", new Cell(30, 24), Facing.South));
        Assert.Contains("bridge_rope", Assert.Throws<MatchSetupException>(() => Arena.New(64).AttachTerrain(unknown)).Message);
    }

    // ---- Crossing: the deck's road and speed ----

    [Fact]
    public void TheDeckIsFlatGroundWithTheBridgesRoadWhateverTheBedUnderIt()
    {
        var w = River(sites: [Concrete, new BridgeSite("metal", new Cell(4, 21), Facing.South)], bed: true);
        var g = w.Ground!;
        var open = new Cell(20, 29); // the river beside the bridges
        var deck = new Cell(32, 29); // over the carved bank: a cliff as dry ground
        var highway = new Cell(8, 30);
        Assert.True(g.OpenWater(open));
        Assert.Equal(0, g.Grade(deck));
        for (var k = 0; k < Rules.Classes.Count; k++)
        {
            var mc = Rules.Classes[k];
            Assert.False(g.Passable(k, open));
            Assert.True(g.Passable(k, deck), mc.Id); // the carved bed under it would be a cliff
            Assert.Equal(mc.RoadSpeedOf(RoadKind.Paved), g.SpeedPercent(k, deck, uphill: false)); // flat, paved
            Assert.Equal(mc.RoadSpeedOf(RoadKind.Highway), g.SpeedPercent(k, highway, uphill: false)); // flat, the metal bridge's highway
            Assert.Equal(mc.Cost[0] * mc.RoadCostOf(RoadKind.Highway) / 100, g.Cost(k, highway));
        }
        Assert.Equal(125, Rules.Classes[g.Rules.ClassOf(w.Player(1)!.Setup.Find(w.Content, "technical")!)].RoadSpeedOf(RoadKind.Paved));
    }

    // Cross drives a crewed vehicle from (x, 10) to (x, 50) over the river along row 30 and returns the ticks it took, or -1.
    private static int Cross(World w, string vehicle, int x, int limit = 2000)
    {
        var (v, _) = w.SpawnCrewed(1, vehicle, x, 10, Facing.South);
        w.Order(1, CommandKind.Move, [v], cell: new Cell(x, 50));
        for (var t = 0; t < limit; t++)
        {
            w.Step();
            Assert.False(w.InWater(v.Pos), $"{vehicle} in the water at {Cell.Of(v.Pos)}");
            if (Cell.Of(v.Pos).Y >= 49)
            {
                return t;
            }
        }
        return -1;
    }

    [Fact]
    public void EveryGroundClassCrossesOnTheDeckAndATruckIsFasterOnTheMetalBridgesHighway()
    {
        foreach (var v in new[] { "technical", "cargo_truck", "heavy_rocket_truck", "kub_launcher" })
        {
            var w = River(sites: [new BridgeSite("stone", new Cell(31, 26), Facing.South)], bed: true); // the narrowest: 3 cells
            var t = Cross(w, v, 32);
            output.WriteLine($"{v} over the stone bridge: {t} ticks");
            Assert.True(t > 0, $"{v} did not cross the stone bridge");
        }
        var foot = River(sites: [Concrete]);
        var r = foot.Spawn(1, "Rifleman", 32, 10);
        foot.Order(1, CommandKind.Move, [r], cell: new Cell(32, 50));
        foot.Run(900);
        Assert.True(Cell.Of(r.Pos).Y >= 49, $"the rifleman stopped at {Cell.Of(r.Pos)}");
        // The same truck over a paved bridge and over the metal bridge's highway (a 9 x 18 one, the deck rows 29-31 of its 18).
        var paved = Cross(River(sites: [new BridgeSite("metal", new Cell(28, 21), Facing.South)]), "cargo_truck", 32);
        var w2 = River(sites: [new BridgeSite("metal", new Cell(28, 21), Facing.South)]);
        var g = w2.Ground!;
        for (var y = 21; y < 39; y++)
        {
            for (var x = 28; x < 37; x++)
            {
                g.SetRoadKind(new Cell(x, y), RoadKind.Paved); // the same bridge paved instead
            }
        }
        var slower = Cross(w2, "cargo_truck", 32);
        output.WriteLine($"highway {paved} ticks, paved {slower}");
        Assert.True(paved > 0 && paved < slower, $"highway {paved}, paved {slower}");
    }

    [Fact]
    public void WithoutABridgeNobodyCrossesAndAStandingSpanLetsThemOver()
    {
        Assert.Equal(-1, Cross(River(), "technical", 32, 800)); // no bridge: it stops on the near bank
        Assert.True(Cross(River(sites: [Concrete]), "technical", 32) > 0);
    }

    // ---- Stages, the fall, the ruin ----

    [Fact]
    public void ItsStageFollowsItsHpAndEveryStandingStageIsPassable()
    {
        var w = River(sites: [Concrete]);
        var b = OnlyBridge(w);
        var max = b.Def.MaxHp;
        var seen = new List<(int Hp, int Stage)>();
        foreach (var pct in new[] { 100, 71, 70, 41, 40, 1 })
        {
            b.HP = max * pct / 100;
            w.Step();
            seen.Add((pct, b.Stage));
            Assert.False(b.Bridge!.Down);
            Assert.All(b.Bridge.DeckCells, c => Assert.True(w.Ground!.Deck(c) && w.Grid.Passable(c)));
        }
        Assert.Equal([(100, 0), (71, 0), (70, 1), (41, 1), (40, 2), (1, 2)], seen);
        // The stage changes are reported as they happen.
        var w2 = River(sites: [Concrete]);
        var b2 = OnlyBridge(w2);
        b2.HP = max * 50 / 100;
        w2.Step();
        var e = Assert.Single(w2.BridgeEvents);
        Assert.Equal((BridgeEventKind.Stage, b2.Id, 1, b2.Pos, 0), (e.Kind, e.Bridge, e.Stage, e.At, e.Lost.Count));
        w2.Step();
        Assert.Empty(w2.BridgeEvents);
    }

    [Fact]
    public void AtZeroHpTheSpanFallsAndEveryoneOnItIsLostButNotOnTheRamps()
    {
        var w = River(sites: [Concrete]);
        var b = OnlyBridge(w);
        // On the deck (cells 30-34 x rows 29-31), apart: a rifleman, a crewed technical, a carrier with its driver and three
        // riders, a mine.
        var onDeck = w.Spawn(1, "Rifleman", 30, 31);
        var (truck, driver) = w.SpawnCrewed(1, "technical", 34, 30);
        var carrier = w.Spawn(1, "infantry_carrier", 32, 30);
        var cDriver = w.Spawn(1, "Rifleman", 32, 30);
        World.Board(cDriver, carrier);
        var riders = Enumerable.Range(0, 3).Select(_ => w.Spawn(1, "Rifleman", 32, 30)).ToList();
        foreach (var r in riders)
        {
            World.Ride(r, carrier);
        }
        var mine = w.Spawn(1, "ap_mine", 30, 29);
        var onRamp = w.Spawn(1, "Rifleman", 32, 27); // on the bridge's dry end: it stays
        var bank = w.Spawn(1, "Rifleman", 20, 27);
        var lost = new[] { onDeck, truck, driver, carrier, cDriver, mine }.Concat(riders).Select(u => u.Id).Order().ToList();
        b.HP = 0; // a blast brings it down
        w.Step();
        var fell = Assert.Single(w.BridgeEvents);
        Assert.Equal((BridgeEventKind.Collapsed, b.Id, 3), (fell.Kind, fell.Bridge, fell.Stage));
        Assert.Equal(lost, fell.Lost.Order());
        Assert.All(lost, id => Assert.Null(w.Unit(id))); // nobody swims, nobody bails out
        Assert.NotNull(w.Unit(onRamp.Id));
        Assert.NotNull(w.Unit(bank.Id));
        Assert.Same(b, w.Unit(b.Id)); // the ruin stays
        Assert.Equal((true, 3, 1), (b.Bridge!.Down, b.Stage, b.HP));
        Assert.All(b.Bridge.DeckCells, c => Assert.True(w.Ground!.OpenWater(c) && w.Grid.At(c) == Terrain.Water));
        Assert.True(w.Grid.Passable(new Cell(32, 27)) && w.Grid.Passable(new Cell(32, 34))); // the ramps on the banks stay ground
        // Hit again, it stays a ruin; it is no target any more.
        b.HP = -500;
        w.Run(20);
        Assert.Equal((1, true), (b.HP, b.Bridge.Down));
        Assert.Empty(w.BridgeEvents);
        Assert.Same(b, OnlyBridge(w));
    }

    [Fact]
    public void ANeutralBridgeIsNobodysTargetButAnOrderedAttackOrBlastBringsItDown()
    {
        var w = River(sites: [new BridgeSite("stone", new Cell(31, 26), Facing.South)]);
        var b = OnlyBridge(w);
        // Armed units stand beside it for 20 s: nobody fires at it on his own.
        var rpg = w.Spawn(1, "Tank hunter", 28, 27);
        var (tech, _) = w.SpawnCrewed(1, "technical", 36, 33);
        w.Run(400);
        Assert.Equal(b.Def.MaxHp, b.HP);
        Assert.Equal(0u, rpg.AutoTarget);
        Assert.Equal(0u, tech.AutoTarget);
        // Ordered at it, they hurt it until the span falls; then the order ends (nothing left to hit).
        var mortar = w.Spawn(1, "mortar", 31, 4, Facing.South);
        w.Order(1, CommandKind.Attack, [rpg, tech], target: b.Id);
        var hp = b.HP;
        w.Run(100);
        Assert.True(b.HP < hp, "an ordered attack hurts it");
        b.HP = 1;
        w.Run(100);
        Assert.True(b.Bridge!.Down);
        Assert.NotEqual(OrderKind.Attack, rpg.Order.Kind);
        // A fallen span is no target: an attack order on it is refused.
        w.Order(1, CommandKind.Attack, [rpg, mortar], target: b.Id);
        w.Step();
        Assert.NotEqual(OrderKind.Attack, rpg.Order.Kind);
        Assert.NotEqual(OrderKind.Attack, mortar.Order.Kind);
    }

    [Fact]
    public void AShellInTheWaterBesideItHurtsIt()
    {
        var w = River(sites: [new BridgeSite("stone", new Cell(31, 26), Facing.South)]);
        var b = OnlyBridge(w);
        var mortar = w.Spawn(1, "mortar", 34, 4, Facing.South);
        var aim = new Cell(35, 30); // the river 2 cells east of the deck
        Assert.True(w.InWater(aim));
        w.Order(1, CommandKind.AttackGround, [mortar], cell: aim);
        var booms = new List<Detonation>();
        for (var t = 0; t < 1200 && b.HP == b.Def.MaxHp; t++)
        {
            w.Step();
            booms.AddRange(w.Detonations);
        }
        output.WriteLine(string.Join(" ", booms.Select(d => $"({d.At.X.Raw / 1024.0:0.0},{d.At.Y.Raw / 1024.0:0.0}){(d.Water ? "~" : "")}")));
        Assert.Contains(booms, d => d.Water);
        Assert.True(b.HP < b.Def.MaxHp, "the splash from the water reaches the bridge");
    }

    [Fact]
    public void ArtilleryOrderedAtItBringsItDown()
    {
        var w = River(n: 120, y: 60, sites: [new BridgeSite("stone", new Cell(59, 56), Facing.South)]);
        var b = OnlyBridge(w);
        var g = w.Spawn(1, "howitzer", 10, 50);
        var crew = w.Spawn(1, "Rifleman", 12, 46);
        World.Board(crew, g);
        w.Order(1, CommandKind.AttackGround, [g], cell: new Cell(60, 60)); // the deck's middle: no spotter needed
        var t = 0;
        for (; t < 6000 && !b.Bridge!.Down; t++)
        {
            w.Step();
        }
        output.WriteLine($"down after {t} ticks, stage {b.Stage}");
        Assert.True(b.Bridge!.Down, $"still {b.HP} HP");
    }

    [Fact]
    public void AnAirStrikeBringsItDown()
    {
        const int n = 240;
        var w = Arena.NewSentinel(n);
        var map = Map(n, [RiverEW(n, 122)]);
        map.AddBridgeSite(new BridgeSite("stone", new Cell(134, 118), Facing.South));
        w.AttachTerrain(map);
        var b = OnlyBridge(w);
        var jet = w.Spawn(1, "Multirole 500 lb", 100, 120, Facing.East);
        w.Step();
        w.Order(1, CommandKind.Attack, [jet], target: b.Id);
        var impacts = new List<BombImpact>();
        for (var t = 0; t < 4000 && !b.Bridge!.Down; t++)
        {
            w.Step();
            impacts.AddRange(w.BombImpacts);
        }
        output.WriteLine($"{impacts.Count} bombs, HP {b.HP}, down {b.Bridge!.Down}");
        Assert.NotEmpty(impacts);
        Assert.True(b.Bridge.Down);
    }

    // ---- Re-routing ----

    [Fact]
    public void AUnitHeadingForAFallingSpanGoesRoundByAnotherBridge()
    {
        // A river across the whole map; bridges at x 18-22 and x 58-62. A truck from (20, 8) to (20, 70) heads for the near
        // one; it falls while the truck is on its way: the truck crosses by the far one.
        var w = River(n: 80, y: 40, sites: [new BridgeSite("concrete", new Cell(18, 34), Facing.South), new BridgeSite("concrete", new Cell(58, 34), Facing.South)]);
        var near = w.Bridges[0];
        var (truck, _) = w.SpawnCrewed(1, "cargo_truck", 20, 8, Facing.South);
        var squad = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 16 + i, 6)).ToList();
        w.Order(1, CommandKind.Move, [truck], cell: new Cell(20, 70));
        w.Order(1, CommandKind.Move, squad, cell: new Cell(20, 70));
        var fell = false;
        var crossedAt = new List<int>();
        for (var t = 0; t < 4000 && !(Cell.Of(truck.Pos).Y >= 68 && squad.All(u => Cell.Of(u.Pos).Y >= 60)); t++)
        {
            w.Step();
            if (!fell && Cell.Of(truck.Pos).Y >= 26)
            {
                output.WriteLine($"tick {w.Tick}: the truck at {Cell.Of(truck.Pos)} heads for {string.Join(" ", truck.Path.Skip(truck.PathIdx).Take(12))}");
                Assert.InRange(Cell.Of(truck.Pos).X, 16, 24); // heading for the near bridge
                near.HP = 0;
                fell = true;
            }
            foreach (var u in squad.Append(truck))
            {
                Assert.False(w.InWater(u.Pos), $"{u.Def.Name} in the water at {Cell.Of(u.Pos)}, tick {w.Tick}");
                if (Cell.Of(u.Pos).Y == 40)
                {
                    crossedAt.Add(Cell.Of(u.Pos).X);
                }
            }
        }
        Assert.True(fell);
        Assert.True(near.Bridge!.Down);
        output.WriteLine($"truck at {Cell.Of(truck.Pos)}, squad {string.Join(" ", squad.Select(u => Cell.Of(u.Pos)))}");
        Assert.True(Cell.Of(truck.Pos).Y >= 68, $"the truck stopped at {Cell.Of(truck.Pos)}");
        Assert.All(squad, u => Assert.True(Cell.Of(u.Pos).Y >= 60, $"a rifleman stopped at {Cell.Of(u.Pos)}"));
        Assert.All(crossedAt, x => Assert.InRange(x, 58, 62)); // all by the far bridge
    }

    // ---- Repair ----

    [Fact]
    public void BuildersOfEitherSideRepairAFallenSpanAndItsDeckComesBack()
    {
        var w = River(sentinel: true, sites: [Concrete]);
        var b = OnlyBridge(w);
        b.HP = 0;
        w.Step();
        Assert.True(b.Bridge!.Down);
        // A Sentinel worker on the north bank, a Resistance worker on the south bank (the neutral bridge is anybody's road).
        var north = w.Spawn(1, "worker", 32, 20);
        var south = w.Spawn(2, "worker", 32, 40);
        w.Order(1, CommandKind.Repair, [north], target: b.Id);
        w.Order(2, CommandKind.Repair, [south], target: b.Id);
        int? started = null, reopened = null;
        var events = new List<BridgeEvent>();
        for (var t = 0; t < 2000 && b.HP < b.Def.MaxHp; t++)
        {
            w.Step();
            events.AddRange(w.BridgeEvents);
            if (b.Bridge.Repairing)
            {
                started ??= t;
            }
            if (reopened is null && !b.Bridge.Down)
            {
                reopened = t;
                Assert.True(b.HP * 100 >= b.Def.MaxHp * 40); // laid again at 40 %: damaged (stage 1) just above it
                Assert.Equal(b.HP * 100 > b.Def.MaxHp * 40 ? 1 : 2, b.Stage);
                Assert.All(b.Bridge.DeckCells, c => Assert.True(w.Ground!.Deck(c) && w.Grid.Passable(c)));
            }
            if (b.Bridge.Down)
            {
                Assert.All(b.Bridge.DeckCells, c => Assert.True(w.Ground!.OpenWater(c)));
            }
        }
        output.WriteLine($"work from tick {started}, reopened {reopened}, full at {w.Tick}");
        Assert.NotNull(started);
        Assert.NotNull(reopened);
        // Two builders (100 % and 50 %: 1.5 times a worker's rate) take the ruin from 1 HP to 40 %: 0.4 x 900 / 1.5 = 240 ticks.
        Assert.InRange(reopened!.Value - started!.Value, 230, 250);
        Assert.Equal(b.Def.MaxHp, b.HP);
        Assert.Equal(0, b.Stage);
        w.Step();
        Assert.False(b.Bridge.Repairing || north.Order.Kind == OrderKind.Repair || south.Order.Kind == OrderKind.Repair); // done: they idle
        Assert.Equal(BridgeEventKind.Reopened, events[0].Kind);
        Assert.All(events.Skip(1), e => Assert.Equal(BridgeEventKind.Stage, e.Kind));
        Assert.Equal(0, events[^1].Stage);
        // A unit crosses again.
        Assert.True(Cross(w, "humvee", 32) > 0);
    }

    [Fact]
    public void ABulldozerRepairsADamagedBridgeAndTheScaffoldShowsOnlyWhileItWorks()
    {
        var w = River(sentinel: true, sites: [Concrete]);
        var b = OnlyBridge(w);
        b.HP = b.Def.MaxHp / 2;
        w.Step();
        Assert.Equal(1, b.Stage);
        var dozer = w.SpawnCrewed(1, "bulldozer", 32, 14, Facing.South).Vehicle;
        w.Order(1, CommandKind.Repair, [dozer], target: b.Id);
        var working = 0;
        for (var t = 0; t < 1500 && b.HP < b.Def.MaxHp; t++)
        {
            w.Step();
            working += b.Bridge!.Repairing ? 1 : 0;
            Assert.Equal(b.Bridge.Repairing, dozer.Building);
        }
        Assert.Equal(b.Def.MaxHp, b.HP);
        // One builder restores the whole bar in the build time (900 ticks): half of it in 450.
        Assert.InRange(working, 445, 455);
        w.Step();
        Assert.False(b.Bridge!.Repairing);
        Assert.Equal(0, b.Stage);
    }

    [Fact]
    public void PlayersCannotBuildABridgeAndTheSandboxPutsOneOnlyWhereItStands()
    {
        var w = River(sandbox: true);
        var worker = w.Spawn(1, "worker", 32, 20);
        var def = w.Player(1)!.Setup.Find(w.Content, "bridge_concrete")!;
        Assert.False(w.CanPlace(def, new Cell(30, 24), Facing.South, [worker]));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = new Cell(30, 24), Unit = "bridge_concrete", Facing = Facing.South });
        w.Run(50);
        Assert.Empty(w.Bridges);
        // The sandbox: refused along the river or off any water, put up across it (turned for a road running east-west).
        Assert.Contains("in the water", w.BridgeRefusal(def.Archetype, new Cell(30, 28), Facing.East));
        Assert.Contains("spans no water", w.BridgeRefusal(def.Archetype, new Cell(10, 2), Facing.South));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "bridge_concrete", Cell = new Cell(10, 2), Facing = Facing.South });
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SandboxSpawn, Unit = "bridge_concrete", Cell = new Cell(30, 24), Facing = Facing.North });
        w.Step();
        var b = OnlyBridge(w);
        Assert.Equal((0, new Cell(30, 24), false), (b.Owner, World.FootprintOrigin(b), b.Turned));
        Assert.True(w.Ground!.Deck(new Cell(32, 30)));
        // Nothing else is put on it: a second bridge, a town building.
        Assert.Contains("deck", w.BridgeRefusal(def.Archetype, new Cell(31, 23), Facing.South));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "chicken_coop", Cell = new Cell(31, 25) });
        w.Step();
        Assert.Empty(w.All("chicken_coop"));
        Assert.False(w.CanPlace(w.Player(1)!.Setup.Find(w.Content, "supply_depot")!, new Cell(30, 32), 0, [worker]));
    }

    // ---- Determinism ----

    [Fact]
    public void FallsAndRepairsAreDeterministicAndHashed()
    {
        ulong[] Play()
        {
            var w = River(sites: [Concrete, new BridgeSite("stone", new Cell(9, 26), Facing.South)]);
            var b = w.Bridges[0];
            var mortar = w.Spawn(1, "mortar", 32, 4, Facing.South);
            var (truck, _) = w.SpawnCrewed(2, "technical", 12, 50, Facing.North);
            var worker = w.Spawn(2, "worker", 33, 40);
            w.Order(1, CommandKind.AttackGround, [mortar], cell: new Cell(32, 30));
            w.Order(2, CommandKind.Move, [truck], cell: new Cell(32, 10));
            var hashes = new List<ulong>();
            for (var t = 0; t < 1600; t++)
            {
                if (t == 300)
                {
                    b.HP = 0;
                }
                if (t == 320)
                {
                    w.Order(2, CommandKind.Repair, [worker], target: b.Id);
                }
                w.Step();
                hashes.Add(w.Hash());
            }
            return [.. hashes];
        }
        Assert.Equal(Play(), Play());
        // Its state is in the hash: the same world with the span down hashes otherwise.
        var a = River(sites: [Concrete]);
        var c = River(sites: [Concrete]);
        a.Step();
        c.Step();
        Assert.Equal(a.Hash(), c.Hash());
        OnlyBridge(c).Bridge!.Down = true;
        Assert.NotEqual(a.Hash(), c.Hash());
        OnlyBridge(c).Bridge!.Down = false;
        OnlyBridge(c).Bridge!.Repairing = true;
        Assert.NotEqual(a.Hash(), c.Hash());
    }
}
