using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Fieldcraft between trees (the sniper's fieldcraft block, Sim/Systems/FieldcraftSystem.cs; docs/design.md, "Fieldcraft"; owner
// 2026-11-20, "snipers have faster camouflage abilities between trees"): still 1 s in scrub, a grove or dense forest the sniper
// is concealed; enemy ground units then spot him only within 1.5 cells in dense forest (a rifleman's 2.1 today), detectors as
// today; crawling keeps it (twice the reach), walking breaks it; a shot reveals him for 1.5 s, then he hides again; from the air,
// still in scrub or a grove, he counts as in dense forest (a scout drone as today); nothing without fog; determinism and hashing.
// The sniper ("Marksman") is player 2, the observers player 1; everyone holds fire unless a test says otherwise.
public class FieldcraftTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int Y = 32; // every soldier stands on this row
    private const int Wood0 = 20, Wood1 = 44; // the wood covers columns [Wood0, Wood1) of rows [8, 56)

    // Wooded is a world on flat terrain (64 x 64) with a wood of the density over columns [20, 44) and rows [8, 56), fog of war
    // on unless fog is false; the battle lobby or the Sentinel's.
    private static World Wooded(int density, bool fog = true, bool sentinel = false)
    {
        const int size = 64;
        var w = sentinel ? Arena.NewSentinel(size, edit: fog ? s => s with { Fog = true } : null)
            : Arena.New(size, edit: fog ? s => s with { Fog = true } : null);
        var flags = new byte[size * size];
        for (var y = 8; y < 56; y++)
        {
            for (var x = Wood0; x < Wood1; x++)
            {
                flags[y * size + x] = (byte)density;
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, new int[(size + 1) * (size + 1)], flags, Rules));
        return w;
    }

    private static Unit Put(World w, byte player, string name, int x)
    {
        var u = w.Spawn(player, name, x, Y);
        u.Stance = Stance.HoldFire;
        return u;
    }

    // Settled is a sniper at the wood's first column (its cell centre) still for 25 ticks: concealed.
    private static (World W, Unit Sniper) Settled(int density, bool sentinel = false)
    {
        var w = Wooded(density, sentinel: sentinel);
        var s = Put(w, 2, "Marksman", Wood0);
        w.Run(25);
        Assert.True(w.FieldcraftOf(s).Concealed);
        return (w, s);
    }

    // Eye is a player 1 observer (name) tenths of a cell west of t, on t's row.
    private static Unit Eye(World w, string name, Unit t, int tenths)
    {
        var u = Put(w, 1, name, 4);
        u.Pos = new Vec(t.Pos.X - Fixed.Frac(tenths, 10), t.Pos.Y);
        return u;
    }

    // Looks lets every observer look again this tick and steps once.
    private static void Looks(World w)
    {
        foreach (var u in w.Units)
        {
            u.LookedAt = uint.MaxValue;
        }
        w.Step();
    }

    private static void Crawl(Unit u, bool walk = false) =>
        u.Vel = new Vec(Fixed.FromRaw(walk ? u.Def.Speed.Raw : u.Def.Speed.Raw / 2), Fixed.Zero);

    // Settling: still for 1 s (20 ticks) in scrub, a grove or dense forest he is concealed; in the open, without fog, or another
    // soldier (a rifleman: no fieldcraft), never.
    [Theory]
    [InlineData(1, "Marksman", true, true)]
    [InlineData(2, "Marksman", true, true)]
    [InlineData(3, "Marksman", true, true)]
    [InlineData(0, "Marksman", true, false)]
    [InlineData(3, "Marksman", false, false)]
    [InlineData(3, "Rifleman", true, false)]
    public void StillForOneSecondBetweenTreesHeIsConcealed(int density, string name, bool fog, bool concealed)
    {
        var w = Wooded(density, fog);
        var s = Put(w, 2, name, Wood0);
        w.Run(19);
        Assert.False(w.FieldcraftOf(s).Concealed);
        Assert.Equal(concealed ? 20u : 0u, w.FieldcraftOf(s).ConcealedAt); // settling: concealed at tick 20
        w.Run(1);
        Assert.Equal(concealed, w.FieldcraftOf(s).Concealed);
        Assert.Equal(concealed ? 20u : 0u, s.ConcealedAt);
    }

    // Concealed in dense forest, a rifleman (2.1 cells today: 10.5 x 40% x 50%) spots him at 1.4 cells, not at 1.6; a rifleman
    // target (no fieldcraft) is spotted at 1.6; an enemy sniper (a detector) keeps today's rules (4.2 cells).
    [Theory]
    [InlineData("Rifleman", "Marksman", 14, true)]
    [InlineData("Rifleman", "Marksman", 16, false)]
    [InlineData("Rifleman", "Rifleman", 16, true)]
    [InlineData("Marksman", "Marksman", 30, true)]
    public void ConcealedInDenseForestHeIsSpottedOnlyWithinOneAndAHalfCells(string eye, string target, int tenths, bool seen)
    {
        var w = Wooded(3);
        var t = Put(w, 2, target, Wood0);
        w.Run(25);
        Eye(w, eye, t, tenths);
        Looks(w);
        Assert.Equal(target == "Marksman", w.FieldcraftOf(t).Concealed);
        Assert.Equal(seen, World.IsSpotted(t, 1));
        Assert.True(w.FogMatchesRebuild());
    }

    // Crawling (at most half his speed) keeps him concealed, the reach doubled (3 cells): a rifleman 3.4 cells off does not spot
    // him; walking breaks it, and today's rules spot a moving soldier in dense forest within 4.2 cells.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void CrawlingKeepsHisConcealmentWalkingBreaksIt(bool walk, bool seen)
    {
        var (w, s) = Settled(3);
        Eye(w, "Rifleman", s, 34);
        Crawl(s, walk);
        Looks(w);
        Assert.Equal(!walk, w.FieldcraftOf(s).Concealed);
        Assert.Equal(seen, World.IsSpotted(s, 1));
    }

    // A shot reveals him for 1.5 s (30 ticks) to a rifleman 1.6 cells off (today's rules, the shot's state); then, still, he is
    // concealed again (1 s after the shot) and unspotted once the rifleman's last look runs out.
    [Fact]
    public void AShotRevealsHimForOneAndAHalfSecondsThenHeHidesAgain()
    {
        var (w, s) = Settled(3);
        Eye(w, "Rifleman", s, 16);
        Looks(w);
        Assert.False(World.IsSpotted(s, 1));
        var shot = w.Tick - 1;
        s.LastShot = shot;
        Looks(w);
        Assert.True(World.IsSpotted(s, 1));
        Assert.Equal(new FieldCover(false, Fixed.Zero, shot + 20, shot + 31), w.FieldcraftOf(s));
        while (w.Tick <= shot + 30)
        {
            Looks(w);
            Assert.True(World.IsSpotted(s, 1), $"tick {w.Tick}"); // the last look at tick shot + 30
        }
        Assert.True(w.FieldcraftOf(s).Concealed);
        Assert.Equal(Fixed.Frac(15, 10), w.FieldcraftOf(s).Reach);
        for (var i = 0; i < (int)World.VisionTicks + 1; i++)
        {
            Looks(w);
        }
        Assert.False(World.IsSpotted(s, 1));
    }

    // From the air (a fighter jet's camera 2 cells off): still in a grove or scrub the concealed sniper counts as in dense forest
    // (hidden from cameras); a rifleman there is seen (a grove: within 3 cells; scrub: as in the open); crawling, the air table
    // takes the cell as it is (moving in a grove: within 8); a scout drone (a detector) keeps today's rules.
    [Theory]
    [InlineData("fighter_jet", 2, "Rifleman", false, true)]
    [InlineData("fighter_jet", 2, "Marksman", false, false)]
    [InlineData("fighter_jet", 1, "Rifleman", false, true)]
    [InlineData("fighter_jet", 1, "Marksman", false, false)]
    [InlineData("fighter_jet", 2, "Marksman", true, true)]
    [InlineData("scout_drone", 2, "Marksman", false, true)]
    public void FromTheAirStillInScrubOrAGroveHeCountsAsInDenseForest(string air, int density, string target, bool crawl, bool seen)
    {
        var w = Wooded(density, sentinel: air != "scout_drone"); // (the scout drone is the Resistance's: the battle lobby's player 1)
        var t = Put(w, 2, target, Wood0 + 4);
        w.Run(25);
        var eye = w.Spawn(1, air, 2, 2);
        eye.Stance = Stance.HoldFire;
        if (crawl)
        {
            Crawl(t);
        }
        eye.Pos = new Cell(Wood0 + 6, Y).Center();
        eye.LookedAt = uint.MaxValue;
        w.Step();
        Assert.True(w.InAir(eye));
        Assert.Equal(seen, World.IsSpotted(t, 1));
        if (target == "Marksman" && !crawl)
        {
            Assert.True(w.AirCoverOf(t).Hidden); // the client's leaf marker
            Assert.Equal(Fixed.Zero, w.AirCoverOf(t).CameraReach);
        }
    }

    // A sniper on Guard in a grove and a rifle squad walking past: the same hashes on two runs (the concealed flag and its timers
    // hashed, Hash and HashReference agree), and he was concealed, fired and moved in between.
    [Fact]
    public void FieldcraftIsDeterministicAndHashed()
    {
        List<ulong> Play()
        {
            var w = Wooded(2);
            var s = w.Spawn(2, "Marksman", Wood0 + 2, Y);
            s.Stance = Stance.Guard;
            var squad = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 4, Y - 8 + i)).ToList();
            w.Order(1, CommandKind.Move, squad, cell: new Cell(60, Y - 6));
            var hashes = new List<ulong>();
            var concealed = false;
            for (var t = 0; t < 600; t++)
            {
                w.Step();
                concealed |= w.FieldcraftOf(s).Concealed;
                if (t % 50 == 49)
                {
                    hashes.Add(w.Hash());
                    Assert.Equal(w.HashReference(), w.Hash());
                }
            }
            Assert.True(concealed);
            Assert.NotEqual(0u, s.LastShot);
            if (s.ConcealedAt != 0)
            {
                var h = w.Hash();
                s.ConcealedAt++;
                Assert.NotEqual(h, w.Hash());
                s.ConcealedAt--;
            }
            return hashes;
        }
        Assert.Equal(Play(), Play());
    }

    // ENG-SMALL-3 (rules 5.3, INF-ROCK row 1358's left): fog spotting counts a hedge: a rifleman kneeling or prone on open ground
    // with a hedge on his side toward a ground observer is looked at as in scrub (a line of bushes): still, a rifleman spots him
    // within 10.5 x 40% x 150% = 6.3 cells, so not at 8 cells, at 5; standing he shows over it, and from the other side (no hedge
    // toward the observer) he is in the open as before. On c7995b3 the hedge counted for nothing: seen at 8 cells kneeling or prone.
    [Theory]
    [InlineData(38, Posture.Prone, false)]
    [InlineData(38, Posture.Kneel, false)]
    [InlineData(35, Posture.Prone, true)]
    [InlineData(38, Posture.Stand, true)]
    [InlineData(22, Posture.Prone, true)]
    public void AHedgeHidesAKneelingOrProneManAsScrub(int eyeX, Posture posture, bool seen)
    {
        var w = Wooded(0);
        w.CombatOff = false; // (before any unit: the soldiers' postures)
        w.PlaceProp("hedge", new Vec(Fixed.FromRaw(31L * Fixed.OneRaw + Fixed.OneRaw / 2), Fixed.FromRaw(Y * Fixed.OneRaw + Fixed.OneRaw / 2)), 900); // east of (30, 32)
        var t = Put(w, 2, "Rifleman", 30);
        w.Run(25);
        Assert.Equal(CoverKind.None, w.CoverSide(new Cell(30, Y), 0)); // (the cover data built: concealment only)
        t.Fight!.Posture = posture;
        Put(w, 1, "Rifleman", eyeX); // his first look
        Looks(w);
        Assert.Equal(seen, World.IsSpotted(t, 1));
        Assert.True(w.FogMatchesRebuild());
    }
}
