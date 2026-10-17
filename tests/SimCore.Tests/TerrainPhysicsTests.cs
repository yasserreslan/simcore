using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// Terrain physics, phase 1 (docs/terrain-design.md section 3): ballistic arcs meet the ground,
// range with height, splash in 3D and behind crests, line of sight, launchers deploy only on
// flat enough ground; the heightfield's range, change journal, and terrain in a match setup.
public class TerrainPhysicsTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int M = 512; // raw per metre

    // Hills returns an arena with terrain: corner heights from f (raw).
    private static World Hills(int size, Func<int, int, int> f)
    {
        var w = Arena.New(size);
        var corners = new int[(size + 1) * (size + 1)];
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                corners[y * (size + 1) + x] = f(x, y);
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, corners, new byte[size * size], Rules));
        return w;
    }

    private static int Ridge(int x) => x is >= 12 and <= 14 ? 3 * M : 0; // cells 12-13 on a 3 m ridge

    private static Vec FirstBlast(World w, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            if (w.Detonations.Count > 0)
            {
                return w.Detonations[0].At;
            }
        }
        throw new InvalidOperationException("nothing went off");
    }

    [Fact]
    public void ARocketHitsTheRidgeInFrontOfItsTarget()
    {
        var w = Hills(32, (x, _) => Ridge(x));
        var hunter = w.Spawn(1, "Tank hunter", 8, 16, Facing.East);
        w.Order(1, CommandKind.AttackGround, [hunter], cell: new Cell(16, 16));
        var at = FirstBlast(w, 400);
        Assert.InRange(at.X.Raw, 11 * Fixed.OneRaw, 14 * Fixed.OneRaw); // on the ridge, short of the point
        Assert.True(w.Projectiles.Count == 0 || w.Projectiles.All(p => p.Arcs));

        // The same shot on flat ground (no terrain) lands at the point.
        var flat = Arena.New(32);
        var h2 = flat.Spawn(1, "Tank hunter", 8, 16, Facing.East);
        flat.Order(1, CommandKind.AttackGround, [h2], cell: new Cell(16, 16));
        Assert.True(FirstBlast(flat, 400).X.Raw > 15 * Fixed.OneRaw);
    }

    [Fact]
    public void HighArcsRiseHigherThanLowArcsAndShoulderShotsFlyFlat()
    {
        var w = Arena.New(16);
        var p = w.Player(1)!.Setup;
        var mrl = p.Find(w.Content, "heavy_rocket_launcher")!.Weapons[0];
        var heavy = p.Find(w.Content, "heavy_rocket_truck")!.Weapons[0];
        var rpg = p.Find(w.Content, "Tank hunter")!.Weapons[0];
        var d = 60L * Fixed.OneRaw;
        Assert.True(World.ArcRiseRaw(heavy, d) * 2 > 3 * World.ArcRiseRaw(mrl, d)); // the high arc: 1.5 times and more
        Assert.True(World.ArcRiseRaw(mrl, d) > World.ArcRiseRaw(mrl, 20L * Fixed.OneRaw)); // farther, higher
        Assert.True(World.ArcRiseRaw(rpg, d) < M / 2);
    }

    [Fact]
    public void RangeGrowsFromHighGroundAndShrinksUphillWithinACap()
    {
        // A step: x < 10 at height h, the rest at 0.
        static World Step(int h) => Hills(40, (x, _) => x < 10 ? h : 0);
        foreach (var (h, lo, hi) in new[] { (4 * M, 12.2, 13.0), (-4 * M, 10.5, 11.2), (16 * M, 11.5, 12.0), (0, 12.0, 12.0) })
        {
            var w = Step(h);
            var sniper = w.Spawn(1, "Marksman", 5, 5);
            var weapon = sniper.Def.Weapons[0];
            Assert.Equal(120, weapon.Range.Raw * 10 / Fixed.OneRaw); // 12 cells
            var reach = w.ReachTo(sniper, new Cell(30, 5).Center(), weapon).Raw / (double)Fixed.OneRaw;
            Assert.InRange(reach, lo, hi);
        }
        // Without terrain: the weapon's range.
        var flat = Arena.New(40);
        var s = flat.Spawn(1, "Marksman", 5, 5);
        Assert.Equal(s.Def.Weapons[0].Range, flat.ReachTo(s, new Cell(30, 5).Center(), s.Def.Weapons[0]));
    }

    [Fact]
    public void SplashCountsHeightAndCrestsShieldTheOuterRing()
    {
        // A 3 m crest over cells 12-13; a 10 m bank over cells 22+ (rising over cell 21).
        var w = Hills(40, (x, _) => Ridge(x) + (x >= 22 ? 10 * M : 0));
        var rocket = w.Content.Warhead("explosive")!;
        var at = new Cell(10, 10).Center();
        var open = w.Spawn(1, "Rifleman", 5, 10);     // 5 cells west, level
        var behind = w.Spawn(1, "Rifleman", 15, 10);  // 5 cells east, behind the crest
        var level = w.Spawn(1, "Rifleman", 10, 15);   // 5 cells south, level
        w.Explode(at, 60, rocket, Fixed.FromInt(8));
        var lost = (Func<Unit, int>)(u => u.Def.MaxHp - u.HP);
        Assert.Equal(lost(open), lost(level));
        Assert.Equal(lost(open) / 2, lost(behind));

        // 3D distance: the same 2D distance, 10 m up the bank, takes less.
        var w2 = Hills(40, (x, _) => x >= 22 ? 10 * M : 0);
        var low = w2.Spawn(1, "Rifleman", 15, 10);
        var high = w2.Spawn(1, "Rifleman", 25, 10);
        w2.Explode(new Cell(20, 10).Center(), 60, w2.Content.Warhead("explosive")!, Fixed.FromInt(8));
        Assert.True(lost(high) < lost(low));

        // Two rings: the inner ring is never shielded.
        var w3 = Hills(40, (x, _) => Ridge(x));
        var inside = w3.Spawn(1, "Rifleman", 15, 10);
        var mirror = w3.Spawn(1, "Rifleman", 5, 10);
        w3.Explode(at, 60, w3.Content.Warhead("explosive")!, Fixed.FromInt(8), inner: Fixed.FromInt(6), outer: 30);
        Assert.Equal(lost(mirror), lost(inside));
    }

    [Fact]
    public void LineOfSightIsBlockedByARidge()
    {
        var w = Hills(32, (x, _) => Ridge(x));
        Vec a = new Cell(8, 8).Center(), b = new Cell(18, 8).Center(), c = new Cell(8, 20).Center();
        var eye = Fixed.FromRaw(M); // 1 m up
        Assert.False(w.LineOfSight(a, eye, b, eye));
        Assert.True(w.LineOfSight(a, eye, c, eye));              // along the valley
        Assert.True(w.LineOfSight(a, Fixed.FromRaw(8 * M), b, Fixed.FromRaw(8 * M))); // high above it
        Assert.True(Arena.New(32).LineOfSight(a, eye, b, eye));  // no terrain
    }

    [Fact]
    public void LaunchersSetUpOnlyOnFlatEnoughGround()
    {
        // A 20% slope (wheels drive it) over cells 10-14, flat ground either side.
        static World Slope() => Hills(40, (x, _) => Math.Clamp(x - 10, 0, 5) * 205);
        var w = Slope();
        var (truck, _) = w.SpawnCrewed(1, "heavy_rocket_launcher", 13, 20, Facing.East);
        Assert.Equal(20, w.Ground!.Grade(new Cell(13, 20)));
        w.Order(1, CommandKind.Deploy, [truck]);
        w.Step();
        Assert.Equal(DeployState.Stowed, truck.Deploy); // refuses here
        w.Run(600);
        Assert.Equal(DeployState.Deployed, truck.Deploy); // moved off the slope, then set up
        Assert.True(w.Ground.Grade(Cell.Of(truck.Pos)) < 15);
        Assert.True(Math.Abs(Cell.Of(truck.Pos).X - 13) <= 4);

        // On flat ground it sets up where it stands.
        var f = Slope();
        var (t2, _) = f.SpawnCrewed(1, "heavy_rocket_launcher", 30, 20, Facing.East);
        f.Order(1, CommandKind.Deploy, [t2]);
        f.Run(2);
        Assert.Equal(DeployState.Deploying, t2.Deploy);
    }

    [Fact]
    public void HeightsReachMountainsAndTheJournalListsChanges()
    {
        var t = new TerrainMap(4, 4, Enumerable.Range(0, 25).Select(i => i == 12 ? 200 * M : 0).ToArray(), new byte[16], Rules);
        Assert.Equal(200 * M, t.Corner(2, 2).Raw); // 200 m
        Assert.Throws<ArgumentException>(() => new TerrainMap(1, 1, [0, 0, 0, 300 * M], new byte[1], Rules));

        var v0 = t.Version;
        var changed = new List<Cell>();
        Assert.True(t.ChangedSince(v0, changed));
        Assert.Empty(changed);
        t.SetForest(new Cell(1, 1), 2);
        Assert.Equal(v0 + 1, t.Version);
        Assert.True(t.ChangedSince(v0, changed));
        Assert.Equal([new Cell(1, 1)], changed);
        changed.Clear();
        Assert.True(t.ChangedSince(t.Version, changed));
        Assert.Empty(changed);
    }

    [Fact]
    public void ASetupNamesItsTerrainAndReplaysWithIt()
    {
        var setup = TestContent.Battle(3) with { Terrain = "valley.json" };
        Assert.NotEqual(TestContent.Battle(3).Digest(), setup.Digest());
        Assert.Throws<MatchSetupException>(() => Replay.NewWorld(TestContent.Mvp, setup));

        var size = Replay.NewWorld(TestContent.Mvp, TestContent.Battle(3)).Grid.Width;
        var corners = new int[(size + 1) * (size + 1)];
        for (var i = 0; i < corners.Length; i++)
        {
            corners[i] = i % (size + 1) * 40; // a gentle tilt
        }
        TerrainMap Map() => new(size, size, (int[])corners.Clone(), new byte[size * size], Rules);
        var (rec, hashes) = Replay.Record(TestContent.Mvp, setup, 200, Map());
        Assert.Equal(Map().Digest(), Replay.NewWorld(TestContent.Mvp, setup, Map()).Ground!.Digest());
        var (again, _) = rec.Play(TestContent.Mvp, Map());
        Assert.Equal(hashes, again);

        // A flat setup writes no terrain field (replay files unchanged).
        using var buf = new MemoryStream();
        new Replay { Setup = TestContent.Battle(3), Ticks = 1 }.Encode(buf);
        Assert.DoesNotContain("terrain", System.Text.Encoding.UTF8.GetString(buf.ToArray()));
    }
}
