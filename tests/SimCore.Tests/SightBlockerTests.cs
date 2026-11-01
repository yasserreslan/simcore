using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Sight blockers under fog of war (fog phase F3e; docs/design.md, "Sight blockers"): standing structures and smoke screens
// block the fog's rays at their height. A wall is seen, not what is behind it; a garrison sees from its roof, over its own
// building and lower ones; a collapse opens the sight again; a screen greys the grid behind it while it stands; the
// incremental layers and grids always equal a rebuild from scratch.
public class SightBlockerTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    // Fogged returns an arena with fog of war on flat terrain (or no terrain at all), optionally the Sentinel lobby.
    private static World Fogged(int size, bool sentinel = false, bool terrain = true, Func<int, int, int>? height = null)
    {
        var w = sentinel ? Arena.NewSentinel(size, edit: s => s with { Fog = true }) : Arena.New(size, edit: s => s with { Fog = true });
        if (terrain)
        {
            var corners = new int[(size + 1) * (size + 1)];
            for (var y = 0; y <= size; y++)
            {
                for (var x = 0; x <= size; x++)
                {
                    corners[y * (size + 1) + x] = height?.Invoke(x, y) ?? 0;
                }
            }
            w.AttachTerrain(new TerrainMap(size, size, corners, new byte[size * size], Rules));
        }
        return w;
    }

    private static bool Spotted(Unit u, byte player) => (u.SpottedBy & (1u << player)) != 0;

    // A house (house_small: 5 x 5 cells, 8.4 m) between a marksman and a soldier hides the soldier and the ground behind
    // it; its near wall is seen. The same on a map without terrain. Without the house the soldier is seen.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void AWallHidesWhatIsBehindIt(bool house, bool terrain)
    {
        var w = Fogged(40, terrain: terrain);
        var me = w.Spawn(1, "Marksman", 4, 24); // vision 14 cells
        var them = w.Spawn(2, "Rifleman", 16, 24);
        them.Stance = Stance.HoldFire;
        me.Stance = Stance.HoldFire;
        if (house)
        {
            w.Spawn(1, "house_small", 9, 22); // cells 9-13, 22-26 (neutral)
        }
        w.Run(1);
        var fog = w.FogOf(1)!;
        Assert.Equal(!house, Spotted(them, 1));
        Assert.Equal(!house, fog.Visible(15, 24));
        Assert.True(fog.Visible(9, 24)); // the near wall
        Assert.Equal(house ? 84 * 512 / 10 / 256 : 0, w.SightBlocker(11, 24)); // 8.4 m: 16 half metres
        Assert.True(fog.Visible(10, 18)); // beside it, the open ground
        Assert.True(w.FogMatchesRebuild());
    }

    // A soldier garrisoned in a block of flats (apartment_3f, 10.5 m) sees from its roof: its own building does not block
    // it (the street along its far wall is seen) and it sees over a lower shed (shack_tin, 3.2 m) to a soldier behind it,
    // whom a soldier on the ground at the flats' door does not see.
    [Fact]
    public void ARoofSeesOverItsOwnBuildingAndLowerOnes()
    {
        var w = Fogged(48);
        var flats = w.Spawn(1, "apartment_3f", 10, 20); // cells 10-19, 20-25: its centre (15, 23)
        w.Spawn(1, "shack_tin", 21, 21); // cells 21-24, 21-24
        var behind = w.Spawn(2, "Rifleman", 26, 23);
        var beyondWall = w.Spawn(2, "Rifleman", 15, 18); // in the street along its north wall
        foreach (var u in new[] { behind, beyondWall })
        {
            u.Stance = Stance.HoldFire;
        }
        var ground = w.Spawn(1, "Marksman", 20, 23); // at its east wall, west of the shed
        ground.Stance = Stance.HoldFire;
        w.Run(1);
        Assert.False(Spotted(behind, 1)); // the shed hides it from the ground
        Assert.False(Spotted(beyondWall, 1)); // and the flats hide the street from the marksman

        var roof = w.Spawn(1, "Marksman", 30, 40);
        roof.Stance = Stance.HoldFire;
        World.EnterBuilding(roof, flats);
        w.Run(1);
        Assert.True(Spotted(behind, 1));
        Assert.True(Spotted(beyondWall, 1));
        Assert.True(w.FogMatchesRebuild());
    }

    // A structure sees from its own height past its own footprint: a supply depot (4 m) sees the ground behind it.
    [Fact]
    public void AStructureIsNotBlockedByItself()
    {
        var w = Fogged(40);
        var depot = w.Spawn(1, "supply_depot", 18, 18); // cells 18-23
        w.Run(1);
        Assert.Equal(1, depot.Owner);
        var fog = w.FogOf(1)!;
        Assert.True(fog.Visible(25, 20));
        Assert.True(fog.Visible(16, 20));
        Assert.True(fog.Visible(20, 25));
        Assert.True(w.FogMatchesRebuild());
    }

    // A house that collapses (destroyed) opens the sight again; the standing marksman is swept again for it.
    [Fact]
    public void ACollapseReopensSight()
    {
        var w = Fogged(40);
        var me = w.Spawn(1, "Marksman", 4, 24);
        var them = w.Spawn(2, "Rifleman", 16, 24);
        var house = w.Spawn(1, "house_small", 9, 22);
        (me.Stance, them.Stance) = (Stance.HoldFire, Stance.HoldFire);
        w.Run(2);
        Assert.False(Spotted(them, 1));
        house.HP = 0;
        w.Run(2);
        Assert.Null(w.Unit(house.Id));
        Assert.Equal(0, w.SightBlocker(11, 24));
        Assert.True(Spotted(them, 1));
        Assert.True(w.FogMatchesRebuild());
    }

    // Rubble does not block: a radar site (12 x 12 cells, 8 m) hides what is behind it until it falls to rubble.
    [Fact]
    public void RubbleDoesNotBlock()
    {
        var w = Fogged(48, sentinel: true);
        var me = w.Spawn(2, "Marksman", 6, 24); // vision 14: to x 20
        var them = w.Spawn(1, "Rifleman", 20, 24);
        (me.Stance, them.Stance) = (Stance.HoldFire, Stance.HoldFire);
        var radar = w.Spawn(1, "radar_site", 8, 18); // cells 8-19, 18-29
        w.Run(2);
        Assert.Equal(16, w.SightBlocker(12, 24));
        Assert.False(Spotted(them, 2));
        Assert.True(radar.Def.Archetype.RubbleHpPercent > 0);
        radar.HP = 0;
        w.Run(2);
        Assert.True(radar.Rubble);
        Assert.Equal(0, w.SightBlocker(12, 24));
        Assert.True(Spotted(them, 2));
        Assert.True(w.FogMatchesRebuild());
    }

    // A Humvee's smoke screen (radius 6 cells, a dome 12 m high) greys the grid of an enemy looking across it: its near
    // edge is seen, the ground in and behind it is not, until it is gone.
    [Fact]
    public void SmokeGreysTheGridThenClears()
    {
        var w = Fogged(48, sentinel: true);
        var (humvee, _) = w.SpawnCrewed(1, "humvee", 12, 24);
        humvee.Stance = Stance.HoldFire;
        var eye = w.Spawn(2, "Marksman", 2, 24); // vision 14: to x 16
        eye.Stance = Stance.HoldFire;
        w.Run(1);
        var fog = w.FogOf(2)!;
        Assert.True(fog.Visible(15, 24));
        Assert.True(Spotted(humvee, 2));

        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Smoke, Units = [humvee.Id] });
        w.Run(2);
        var screen = Assert.Single(w.Smokes);
        Assert.Equal(screen.Radius, screen.Height);
        Assert.Equal(24, w.SightBlocker(12, 24)); // 12 m: 24 half metres
        Assert.True(fog.Visible(6, 24)); // its near edge
        Assert.False(fog.Visible(9, 24));
        Assert.False(fog.Visible(15, 24));
        Assert.False(Spotted(humvee, 2));
        Assert.True(w.FogMatchesRebuild());

        // The Humvee inside sees its screen's cells, not past it.
        var own = w.FogOf(1)!;
        Assert.True(own.Visible(15, 24));
        Assert.False(own.Visible(20, 24));

        w.Run((int)(screen.Until - w.Tick) + 1);
        Assert.Empty(w.Smokes);
        Assert.Equal(0, w.SightBlocker(12, 24));
        Assert.True(fog.Visible(15, 24));
        Assert.True(Spotted(humvee, 2));
        Assert.True(w.FogMatchesRebuild());
    }

    // What is not a sight blocker: a site still being built (until it is finished), a hidden structure, pavement.
    [Fact]
    public void SitesHiddenStructuresAndPavementDoNotBlock()
    {
        var w = Fogged(48, sentinel: true);
        var hatch = w.Spawn(2, "tunnel_medium", 10, 10);
        Assert.True(hatch.Def.Hidden);
        var stand = w.Spawn(1, "airfield_stand", 20, 30);
        Assert.True(stand.Def.Archetype.Pavement);
        var site = w.Spawn(2, "house_small", 30, 10);
        site.Construction = ConstructionState.Building;
        w.Spawn(1, "Rifleman", 2, 2);
        w.Run(1);
        Assert.Equal(0, w.SightBlocker(12, 12));
        Assert.Equal(0, w.SightBlocker(22, 32));
        Assert.Equal(0, w.SightBlocker(32, 12));
        site.Construction = ConstructionState.Complete;
        w.Run(1);
        Assert.Equal(16, w.SightBlocker(32, 12));
        Assert.True(w.FogMatchesRebuild());
    }

    // Without fog there is no layer: nothing changes.
    [Fact]
    public void WithoutFogThereIsNoLayer()
    {
        var w = Arena.New(40);
        w.Spawn(1, "house_small", 9, 22);
        w.Run(2);
        Assert.False(w.Fog);
        Assert.Equal(0, w.SightBlocker(11, 24));
    }

    // Content: sightHeight overrides a structure's height for sight (0: it never blocks) and is hashed only when given.
    [Fact]
    public void SightHeightIsContent()
    {
        var c = TestContent.Mvp;
        Assert.Equal(-1, c.Archetype("cell_tower")!.SightHeightRaw);
        Assert.Equal(c.Archetype("cell_tower")!.HeightRaw, c.Archetype("cell_tower")!.SightBlockRaw);
        var sources = TestContent.MvpSources().Select(s => s.Name == "archetypes/structures.json"
            ? s with { Json = s.Json.Replace("\"id\": \"cell_tower\",", "\"id\": \"cell_tower\", \"sightHeight\": 0,", StringComparison.Ordinal) }
            : s).ToList();
        var other = ContentLoader.Load(sources);
        Assert.Equal(0, other.Archetype("cell_tower")!.SightBlockRaw);
        Assert.NotEqual(c.Hash, other.Hash);
    }

    // The incremental layers and grids equal a rebuild from scratch on hills with a town: soldiers walking through it,
    // garrisons coming and going, houses collapsing, a site finished, smoke popped and gone.
    [Fact]
    public void IncrementalMatchesRebuild()
    {
        const int m = 512;
        var w = Fogged(64, sentinel: true, height: (x, y) => (x * 7 + y * 3) % 23 < 6 ? 3 * m : x % 13 * 80);
        string[] kinds = ["house_small", "shack_tin", "apartment_3f", "stone_tower_house", "kiosk", "water_tower_town"];
        var houses = new List<Unit>();
        for (var i = 0; i < 12; i++)
        {
            var before = w.Units.Count;
            w.Spawn(2, kinds[i % kinds.Length], 6 + i % 4 * 14, 6 + i / 4 * 16); // (town buildings: the Resistance's lobby)
            if (w.Units.Count > before)
            {
                houses.Add(w.Units[^1]);
            }
        }
        Assert.True(houses.Count >= 10);
        var walkers = new List<Unit>();
        for (var i = 0; i < 8; i++)
        {
            var u = w.Spawn((byte)(1 + i % 2), "Rifleman", 2 + i * 7, 60 - i * 7);
            u.Stance = Stance.HoldFire;
            walkers.Add(u);
        }
        var (humvee, _) = w.SpawnCrewed(1, "humvee", 32, 32);
        humvee.Stance = Stance.HoldFire;
        var rng = new Rng.Rand(3);
        for (var t = 0; t < 600; t++)
        {
            if (t % 40 == 0)
            {
                foreach (var u in walkers.Where(u => u.Alive && u.Garrison == 0))
                {
                    w.Order(u.Owner, CommandKind.Move, [u], cell: new Cell(rng.Range(1, 63), rng.Range(1, 63)));
                }
            }
            if (t == 100)
            {
                World.EnterBuilding(walkers[0], houses[0]);
            }
            if (t == 150 && houses[2].Def.Archetype.Garrison is not null)
            {
                World.EnterBuilding(walkers[1], houses[2]);
            }
            if (t is 200 or 320 or 450)
            {
                houses[t / 100].HP = 0;
            }
            if (t == 250)
            {
                w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Smoke, Units = [humvee.Id] });
            }
            w.Step();
            if (t % 10 == 0)
            {
                Assert.True(w.FogMatchesRebuild(), $"tick {w.Tick}: the incremental grid drifted");
            }
        }
        Assert.True(w.FogMatchesRebuild());
    }
}
