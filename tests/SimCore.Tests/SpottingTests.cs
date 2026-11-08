using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Spotting under fog of war (fog phase F4e; the terrain rules' spotting block, Sim/Spotting.cs; docs/design.md, "Spotting"):
// seeing a cell is not spotting the unit in it. A still soldier in a grove is spotted only close, a moving one further, one
// that fired far for 5 s; a tank far; a sniper's optics further; the owner's forest rule both ways (from outside within the
// forest reach only; from inside 50% further over the woods, not over open ground, and no forest budget in the wood); ridges
// and buildings still block; a soldier beside a building (town); decoys as their disguise; the Hide stance; the API for the
// client; no change without fog or terrain; determinism, the index against full scans, and the grids against a rebuild.
// Player 1 is the battle lobby's (Resistance) observer side unless a test says otherwise; targets hold fire unless a test
// makes them fire. A rifleman sees 10.5 cells, a marksman 21 (optics 150), a tank 12.
public class SpottingTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int Y = 32; // every unit stands on this row
    private const int Wood0 = 20, Wood1 = 44; // the wood covers columns [Wood0, Wood1) of rows [8, 56)

    // Wooded is a world on flat terrain (64 x 64) with a wood of the density over columns [20, 44) and rows [8, 56), fog of war
    // on (unless fog is false), the battle lobby or the Sentinel's; ridge (a column) raises a 10 m wall across the map.
    private static World Wooded(int density, bool fog = true, bool sentinel = false, int ridge = -1, bool terrain = true)
    {
        const int size = 64;
        var w = sentinel ? Arena.NewSentinel(size, edit: fog ? s => s with { Fog = true } : null)
            : Arena.New(size, edit: fog ? s => s with { Fog = true } : null);
        if (!terrain)
        {
            return w;
        }
        var flags = new byte[size * size];
        for (var y = 8; y < 56; y++)
        {
            for (var x = Wood0; x < Wood1; x++)
            {
                flags[y * size + x] = (byte)density;
            }
        }
        var corners = new int[(size + 1) * (size + 1)];
        if (ridge >= 0)
        {
            for (var y = 0; y <= size; y++)
            {
                corners[y * (size + 1) + ridge] = corners[y * (size + 1) + ridge + 1] = 10 * 512;
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, corners, flags, Rules));
        return w;
    }

    private static Unit Put(World w, byte player, string name, int x)
    {
        var u = w.Spawn(player, name, x, Y);
        u.Stance = Stance.HoldFire; // nobody fires unless a test makes it (a shot reveals the shooter)
        return u;
    }

    // Looks lets every observer look again this tick (as on entering a cell) and steps once.
    private static void Looks(World w)
    {
        foreach (var u in w.Units)
        {
            u.LookedAt = uint.MaxValue;
        }
        w.Step();
    }

    private static void Move(Unit t) => t.Vel = new Vec(Fixed.FromRaw(20), Fixed.Zero);

    private static void Fired(World w, Unit t) => t.LastShot = w.Tick - 1;

    // Spots: whether an observer (name, player 1) at column eye spots player 2's target (name) at column at, in a grove
    // (density 2) unless density says otherwise, still, moving or just after a shot.
    private static bool Spots(string eye, int eyeX, string target, int at, int density = 2, bool moving = false, bool fired = false,
        bool sentinel = false)
    {
        var w = Wooded(density, sentinel: sentinel);
        Put(w, 1, eye, eyeX);
        var t = Put(w, 2, target, at);
        w.Run(5); // (a shot at tick 0 would read as none)
        if (moving)
        {
            Move(t);
        }
        if (fired)
        {
            Fired(w, t);
        }
        Looks(w);
        Assert.True(w.FogMatchesRebuild());
        return World.IsSpotted(t, 1);
    }

    // A still soldier in a grove is spotted only within 30% of a rifleman's 10.5 cells (3.15); his cell is seen further (the
    // grove's 4 cells of forest reach), and in the open he is spotted anywhere in sight.
    [Theory]
    [InlineData(17, Wood0, true)]       // 3 cells, the grove's first cell
    [InlineData(16, Wood0, false)]      // 4 cells: his cell is seen, he is not spotted
    [InlineData(18, Wood0 + 2, false)]  // 4 cells
    public void AStillSoldierInAGroveIsSpottedOnlyClose(int eye, int at, bool seen)
    {
        Assert.Equal(seen, Spots("Rifleman", eye, "Rifleman", at));
        var w = Wooded(2);
        Put(w, 1, "Rifleman", eye);
        Looks(w);
        Assert.True(w.FogOf(1)!.Visible(at, Y)); // the cell is seen either way
        Assert.True(Spots("Rifleman", 4, "Rifleman", 13, density: 0)); // in the open: 9 cells, spotted
    }

    // Moving he is spotted within 60% (6.3 cells); firing within the whole sight, for 5 s.
    [Theory]
    [InlineData(14, true, false, true)]   // 6 cells moving
    [InlineData(14, false, false, false)] // 6 cells still
    [InlineData(13, true, false, false)]  // 7 cells: out of a moving soldier's 6.3
    [InlineData(11, false, true, true)]   // 9 cells, just fired
    [InlineData(11, true, false, false)]
    public void MovingIsSpottedFurtherAndFiringFar(int eye, bool moving, bool fired, bool seen) =>
        Assert.Equal(seen, Spots("Rifleman", eye, "Rifleman", Wood0, moving: moving, fired: fired));

    // The reveal of a shot lasts its 100 ticks (5 s): spotted on every look through the 100th tick after it, then not (the
    // last look's spot lingers at most VisionTicks - 1 ticks).
    [Fact]
    public void AShotRevealsForFiveSeconds()
    {
        var w = Wooded(3);
        var eye = Put(w, 1, "Rifleman", 14); // 6 cells from the dense wood's first cell
        var t = Put(w, 2, "Rifleman", Wood0);
        w.Run(5);
        Looks(w);
        Assert.False(World.IsSpotted(t, 1));
        var shot = w.Tick - 1;
        t.LastShot = shot;
        var spotting = w.SpottingOf(t)!.Value;
        Assert.Equal((SpotCover.Dense, SpotState.Fired, 100, shot + 101), (spotting.Cover, spotting.State, spotting.Percent, spotting.RevealedUntil));
        while (w.Tick <= shot + 100)
        {
            Looks(w);
            Assert.True(World.IsSpotted(t, 1), $"tick {w.Tick}");
        }
        for (var k = 0; k < (int)World.VisionTicks; k++)
        {
            Looks(w);
        }
        Assert.False(World.IsSpotted(t, 1));
        Assert.Equal(SpotState.Still, w.SpottingOf(t)!.Value.State);
        Assert.True(eye.Alive);
    }

    // A tank (concealment 100) still in a grove is spotted within 75% of the sight (7.9 cells), a light vehicle (70) within
    // 52.5% (5.5), a soldier 30% (3.15). The Sentinel lobby: player 1 Sentinel targets, player 2 Resistance observers.
    [Theory]
    [InlineData("main_battle_tank", 7, true)]
    [InlineData("main_battle_tank", 8, false)]
    [InlineData("humvee", 5, true)]
    [InlineData("humvee", 6, false)]
    [InlineData("Rifleman", 3, true)]
    [InlineData("Rifleman", 4, false)]
    public void ATankIsSpottedFarAndASoldierClose(string target, int cells, bool seen)
    {
        var w = Wooded(2, sentinel: true);
        Put(w, 2, "Rifleman", Wood0 - cells);
        var t = Put(w, 1, target, Wood0);
        w.Run(5);
        Looks(w);
        Assert.Equal(seen, World.IsSpotted(t, 2));
    }

    // Optics: the marksman (21 cells, optics 150) spots a still soldier in a grove within 21 x 1.5 x 30% = 9.45 cells, where a
    // rifleman needs 3.15.
    [Theory]
    [InlineData("Marksman", 9, true)]
    [InlineData("Marksman", 10, false)]
    [InlineData("Rifleman", 9, false)]
    public void OpticsExtendTheSpottingRange(string eye, int cells, bool seen)
    {
        // The grove's forest reach is 4 cells: the target stands at its edge and the eye in the open.
        Assert.Equal(seen, Spots(eye, Wood0 - cells, "Rifleman", Wood0));
    }

    // The forest rule from outside: within the forest reach (4 cells into a grove, 2 into dense forest) a unit is spotted by
    // the rule, deeper in it stays hidden whatever it does (here: it just fired).
    [Theory]
    [InlineData(2, Wood0 + 3, true)]   // the grove's 4th cell
    [InlineData(2, Wood0 + 4, false)]  // its 5th: past the reach
    [InlineData(3, Wood0 + 1, true)]   // dense forest's 2nd cell
    [InlineData(3, Wood0 + 2, false)]
    public void FromOutsideTheForestReachHolds(int density, int at, bool seen) =>
        Assert.Equal(seen, Spots("Rifleman", Wood0 - 1, "Rifleman", at, density: density, fired: true));

    // The forest rule from inside: a soldier standing in the wood sees 50% further over it (15.75 cells) with no forest budget
    // along the wood, and open ground at his 10.5 cells.
    [Theory]
    [InlineData(37, 22, true)]   // 15 cells over the wood
    [InlineData(37, 21, false)]  // 16 cells
    [InlineData(42, 52, true)]   // 10 cells, out of the wood onto open ground
    [InlineData(42, 53, false)]  // 11 cells of open ground: not 50% further there
    [InlineData(25, 13, false)]  // 12 cells, open ground (past his 10.5, within the woods' 15.75)
    public void FromInsideTheWoodsSightReachesFurtherOverThem(int eye, int at, bool seen)
    {
        Assert.Equal(seen, Spots("Rifleman", eye, "Rifleman", at, fired: true));
        var w = Wooded(2);
        Put(w, 1, "Rifleman", eye);
        Looks(w);
        Assert.Equal(seen, w.FogOf(1)!.Visible(at, Y)); // the grid shows it too
        Assert.True(w.FogMatchesRebuild());
    }

    // Units in the wood cannot hide from a soldier in it but by the rule: still within 15.75 x 30% = 4.7 cells, moving 9.45 (a
    // soldier outside sees only 4 cells into a grove at all), dense forest the same over its 50% further sight.
    [Theory]
    [InlineData(2, 4, false, true)]
    [InlineData(2, 5, false, false)]
    [InlineData(2, 9, true, true)]
    [InlineData(2, 10, true, false)]
    [InlineData(3, 3, false, true)]  // dense still: 15.75 x 20% = 3.15
    [InlineData(3, 4, false, false)]
    [InlineData(3, 6, true, true)]   // dense moving: 6.3, where a soldier outside sees 2 cells in
    public void InTheWoodsTheyHideOnlyByTheRule(int density, int cells, bool moving, bool seen) =>
        Assert.Equal(seen, Spots("Rifleman", 36, "Rifleman", 36 - cells, density: density, moving: moving));

    // A ridge in the wood still blocks a soldier in it (10 m across column 32), as it blocks from outside.
    [Theory]
    [InlineData(-1, true)]
    [InlineData(32, false)]
    public void RidgesStillBlockInTheWoods(int ridge, bool seen)
    {
        var w = Wooded(2, ridge: ridge);
        Put(w, 1, "Rifleman", 37);
        var t = Put(w, 2, "Rifleman", 29);
        w.Run(5);
        Fired(w, t);
        Looks(w);
        Assert.Equal(seen, World.IsSpotted(t, 1));
        Assert.True(w.FogMatchesRebuild());
    }

    // A building still blocks: a house (house_small, 5 x 5 cells) between a marksman and a soldier who just fired hides him. A
    // still soldier beside the house is in town cover: a rifleman spots him within 50% (5.25 cells).
    [Fact]
    public void BuildingsBlockAndTownCoverHides()
    {
        var w = Wooded(0);
        var me = Put(w, 1, "Marksman", 4);
        var them = Put(w, 2, "Rifleman", 16);
        w.Spawn(1, "house_small", 9, Y - 2); // cells 9-13 (neutral)
        w.Run(5);
        Fired(w, them);
        Looks(w);
        Assert.False(World.IsSpotted(them, 1));
        Assert.True(me.Alive);

        foreach (var (cells, seen) in new[] { (5, true), (6, false) })
        {
            var t = Wooded(0);
            t.Spawn(1, "house_small", 30, Y - 4); // cells 30-34, rows 28-32
            var beside = Put(t, 2, "Rifleman", 35); // next to its east wall
            Put(t, 1, "Rifleman", 35 + cells);
            t.Run(5);
            Looks(t);
            Assert.Equal(SpotCover.Town, t.SpottingOf(beside)!.Value.Cover);
            Assert.Equal(50, t.SpottingOf(beside)!.Value.Percent);
            Assert.Equal(seen, World.IsSpotted(beside, 1));
        }
    }

    // A decoy hides as what it passes for: the decoy heavy rocket truck (wheeled) has the real truck's concealment and is
    // spotted at the same ranges in a grove (70 x 75% of a rifleman's 10.5 cells: 5.5). Sentinel lobby: player 2 owns the decoys.
    [Theory]
    [InlineData("decoy_heavy_rocket_truck", 5, true)]
    [InlineData("decoy_heavy_rocket_truck", 6, false)]
    [InlineData("heavy_rocket_truck", 5, true)]
    [InlineData("heavy_rocket_truck", 6, false)]
    public void ADecoyIsSpottedAsItsDisguise(string target, int cells, bool seen)
    {
        var w = Wooded(2, sentinel: true);
        Put(w, 1, "Rifleman", Wood0 - cells);
        var t = Put(w, 2, target, Wood0);
        Assert.Equal(70, w.ConcealmentOf(t));
        w.Run(5);
        Looks(w);
        Assert.Equal(seen, World.IsSpotted(t, 1));
    }

    // The Hide stance holds fire: a marksman at the edge of a grove with an enemy soldier 5 cells off in the open stays still
    // and unspotted (a still soldier in a grove: 3.15 cells); on Guard he fires and his shot shows him.
    [Theory]
    [InlineData(Stance.Hide, false)]
    [InlineData(Stance.Guard, true)]
    public void TheHideStanceKeepsASoldierHidden(Stance stance, bool seen)
    {
        var w = Wooded(2);
        var hider = w.Spawn(2, "Marksman", Wood0, Y);
        hider.Stance = stance;
        foreach (var dy in new[] { 0, 3, -3 })
        {
            w.Spawn(1, "Rifleman", Wood0 - 5, Y + dy).Stance = Stance.HoldFire; // within 10.5 cells: they see his cell
        }
        for (var t = 0; t < 200 && hider.LastShot == 0; t++)
        {
            w.Step();
        }
        Looks(w);
        Assert.Equal(seen, hider.LastShot != 0);
        Assert.Equal(seen ? SpotState.Fired : SpotState.Still, w.SpottingOf(hider)!.Value.State);
        Assert.Equal(seen, World.IsSpotted(hider, 1));
    }

    // The API for the client: the ranges a ring shows and a unit's own state.
    [Fact]
    public void TheClientReadsRangesAndStates()
    {
        var w = Wooded(2);
        var rifleman = Put(w, 1, "Rifleman", 4);
        var marksman = Put(w, 1, "Marksman", 6);
        var inWood = Put(w, 1, "Rifleman", 30);
        var them = Put(w, 2, "Rifleman", 10);
        w.Run(5);
        const int sight = 105 * 1024 / 10; // a rifleman's 10.5 cells, raw
        Assert.Equal(sight * 30 / 100, w.SpotRange(rifleman, 40, SpotCover.Grove, SpotState.Still).Raw);
        Assert.Equal(sight, w.SpotRange(rifleman, 40, SpotCover.Open, SpotState.Still).Raw);
        Assert.Equal(21 * 1024 * 150 * 30 / 100 / 100, w.SpotRange(marksman, 40, SpotCover.Grove, SpotState.Still).Raw);
        Assert.Equal(sight * 150 / 100 * 30 / 100, w.SpotRange(inWood, 40, SpotCover.Grove, SpotState.Still).Raw);
        Assert.Equal(sight, w.SpotRange(inWood, 40, SpotCover.Open, SpotState.Fired).Raw); // open ground: its own sight
        Assert.Equal(sight * 150 / 100, w.SpotRange(inWood, 100, SpotCover.Grove, SpotState.Fired).Raw);
        Assert.Equal((150, 100), (w.OpticsOf(marksman), w.OpticsOf(rifleman)));
        Assert.Equal(new GroundSpotting(SpotCover.Open, SpotState.Still, 100, 0, true), w.SpottingOf(them));
        Assert.Equal(new GroundSpotting(SpotCover.Grove, SpotState.Still, 30, 0, false), w.SpottingOf(inWood));
        Move(inWood);
        Assert.Equal(SpotState.Moving, w.SpottingOf(inWood)!.Value.State);
        Assert.Equal(60, w.SpottingOf(inWood)!.Value.Percent);
        Assert.NotNull(w.Spotting);
    }

    // No fog, or fog on a map without terrain: spotting does not apply (the fog grid spots as before), nothing to read.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WithoutFogOrTerrainNothingChanges(bool fog, bool terrain)
    {
        var w = Wooded(2, fog: fog, terrain: terrain);
        var eye = Put(w, 1, "Rifleman", 14);
        var t = Put(w, 2, "Rifleman", Wood0);
        w.Run(5);
        Looks(w);
        Assert.Null(w.Spotting);
        Assert.Null(w.SpottingOf(t));
        Assert.Equal(0, w.ConcealmentOf(t));
        Assert.Equal(w.SightRange(eye), w.SpotRange(eye, 40, SpotCover.Grove, SpotState.Still));
        Assert.True(World.IsSpotted(t, 1)); // 6 cells: in sight
    }

    // The terrain rules' optics name content archetypes: an unknown one is refused when the terrain is attached.
    [Fact]
    public void OpticsMustNameAnArchetype()
    {
        var rules = new TerrainRules
        {
            Classes = Rules.Classes,
            Vision = Rules.Vision,
            Spotting = new SpottingRules
            {
                Table = Rules.Spotting!.Table,
                ConcealmentFoot = 40,
                ConcealmentWheeled = 70,
                ConcealmentTracked = 100,
                Optics = [("no_such_unit", 150)],
                FiredTicks = 100,
                Woods = [false, false, true, true],
                WoodsSightPercent = 150,
            },
        };
        var w = Arena.New(16, edit: s => s with { Fog = true });
        Assert.Throws<MatchSetupException>(() => w.AttachTerrain(new TerrainMap(16, 16, new int[17 * 17], new byte[16 * 16], rules)));
    }

    // Walking in and out of the wood (the viewers' woods sight switching on and off), firing and moving: the same hashes on two
    // runs, the same world with the spatial index and with full scans, and the grids always equal a rebuild.
    [Fact]
    public void SpottingIsDeterministicAndMatchesFullScansAndARebuild()
    {
        List<ulong> Play(bool fullScan)
        {
            var w = Wooded(2);
            w.FullScan = fullScan;
            var blue = Enumerable.Range(0, 6).Select(i => w.Spawn(1, i % 2 == 0 ? "Rifleman" : "Marksman", 6 + i, Y - 6 + 2 * i)).ToList();
            var red = Enumerable.Range(0, 6).Select(i => w.Spawn(2, i % 3 == 0 ? "Gunner" : "Rifleman", 50 + i, Y - 6 + 2 * i)).ToList();
            w.Order(1, CommandKind.AttackMove, blue, cell: new Cell(54, Y));
            w.Order(2, CommandKind.Move, red, cell: new Cell(30, Y));
            var hashes = new List<ulong>();
            for (var t = 0; t < 600; t++)
            {
                w.Step();
                if (t % 50 == 49)
                {
                    hashes.Add(w.Hash());
                    Assert.True(w.FogMatchesRebuild(), $"tick {w.Tick}");
                }
            }
            Assert.Contains(w.Units, u => u.LastShot != 0);
            return hashes;
        }
        var a = Play(false);
        Assert.Equal(a, Play(false));
        Assert.Equal(a, Play(true));
    }
}
