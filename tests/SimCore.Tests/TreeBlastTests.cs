using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Strikes knock trees down (docs/design.md, "Trees as obstacles", "Strikes blow trees down"): blast rings by warhead (the
// rules' trees.fell percent of the weapon's splash, never under fellMinRadius), trunks falling away from the impact, the fall
// damage to infantry under them, a felled trunk across a road, determinism and the hash.
public class TreeBlastTests
{
    private static readonly string RulesPath = Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json");
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(RulesPath);

    private static TerrainMap Flat(int n, Func<int, int, int> forest, TerrainRules? rules = null)
    {
        var flags = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                flags[y * n + x] = (byte)forest(x, y);
            }
        }
        return new TerrainMap(n, n, new int[(n + 1) * (n + 1)], flags, rules ?? Rules);
    }

    // OneTree is the first cell from x0 along row y where forest of this density stands a trunk.
    private static Cell OneTree(int x0, int y, int density)
    {
        for (var x = x0; ; x++)
        {
            if (TreeTrunks.Stands(x, y, Rules.Trees!.TrunkChance[density]))
            {
                return new Cell(x, y);
            }
        }
    }

    // The splash of a weapon (its first effect with this warhead).
    private static (Warhead Warhead, Fixed Splash) Weapon(string component, string warhead)
    {
        var w = TestContent.Mvp.Components.Single(c => c.Id == component).Effects.OfType<WeaponEffect>().First(e => e.Warhead.Id == warhead);
        return (w.Warhead, w.Splash);
    }

    private static Vec Off(Vec p, long dx, long dy) => new(Fixed.FromRaw(p.X.Raw + dx), Fixed.FromRaw(p.Y.Raw + dy));

    // Blasts counts the trees blasts of a weapon fell, each at one of 16 points over dense forest (a fresh map each).
    private static List<int> Blasts(string component, string warhead)
    {
        var (wh, splash) = Weapon(component, warhead);
        var counts = new List<int>();
        for (var k = 0; k < 16; k++)
        {
            var w = Arena.New(64);
            w.AttachTerrain(Flat(64, (_, _) => 3));
            var at = new Vec(Fixed.FromRaw(20 * 1024 + k * 1531 % 24576), Fixed.FromRaw(20 * 1024 + k * 2741 % 24576));
            w.FellByBlast(at, wh, splash);
            var fell = w.TreeEvents.Where(e => e.Kind == TreeEventKind.Fell).ToList();
            Assert.All(fell, e =>
            {
                Assert.Equal(TreeFallCause.Blast, e.Cause);
                Assert.Equal(at, e.From);
                // Away from the impact: the 8-way direction from the impact to the trunk.
                var d = TerrainMap.TrunkPoint(e.Stump) - at;
                Assert.Equal(TreeTrunks.DirOf(d), e.Dir);
                var (sx, sy) = TreeTrunks.Step(e.Dir);
                Assert.True(sx * d.X.Raw + sy * d.Y.Raw > 0, "the trunk falls away from the blast");
                Assert.False(w.Ground!.HasTrunk(e.Stump));
            });
            counts.Add(fell.Count);
        }
        return counts;
    }

    [Fact]
    public void SmallWarheadsFellATreeOrTwoShellsAClearingAndBombsARing()
    {
        // An anti-tank missile (no splash) and a rocket (half a cell) blow trees down within 1.5 cells: 1 or 2 in dense forest.
        var atgm = Blasts("kornet_atgm", "atgm");
        var rpg = Blasts("at_missile", "rocket");
        Assert.InRange(atgm.Average(), 1.0, 2.0);
        Assert.InRange(rpg.Average(), 1.0, 2.0);
        Assert.All(atgm.Concat(rpg), n => Assert.InRange(n, 0, 4));
        // The tank's high-explosive round (3 cells): a small clearing.
        var shell = Blasts("tank_120mm", "he_mpat");
        Assert.InRange(shell.Average(), 3.5, 7.0);
        // A heavy bomb (half its 12 cells of splash): a ring of about 20.
        var bomb = Blasts("heavy_bombs", "bomb");
        Assert.All(bomb, n => Assert.InRange(n, 12, 30));
        // Bullets and darts fell nothing.
        Assert.All(Blasts("tank_120mm", "apfsds"), n => Assert.Equal(0, n));
        Assert.All(Blasts("heli_cannon", "heli_cannon"), n => Assert.Equal(0, n));
    }

    [Fact]
    public void AMortarRoundFellsTreesRoundWhereItLands()
    {
        // End to end: a mortar soldier's attack-ground order on a dense wood; every fall the shooter's, away from where it hit.
        ulong[] Run(out List<TreeEvent> falls)
        {
            var w = Arena.New(64);
            w.AttachTerrain(Flat(64, (x, _) => x is >= 26 and < 40 ? 3 : 0));
            var m = w.Spawn(1, "mortar", 10, 32, Facing.East);
            w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(32, 32));
            var hashes = new List<ulong>();
            falls = [];
            for (var t = 0; t < 600; t++)
            {
                w.Step();
                hashes.Add(w.Hash());
                foreach (var e in w.TreeEvents.Where(e => e.Kind == TreeEventKind.Fell))
                {
                    falls.Add(e);
                    Assert.Equal(m.Id, e.By);
                    Assert.Equal(TreeFallCause.Blast, e.Cause);
                    Assert.Equal(TreeTrunks.DirOf(TerrainMap.TrunkPoint(e.Stump) - e.From), e.Dir);
                    Assert.True((TerrainMap.TrunkPoint(e.Stump) - e.From).Len().Raw <= Fixed.FromInt(3).Raw); // within 2.5 cells of it (and the trunk's offset)
                }
            }
            Assert.Equal(falls.Count, w.Stumps.Count);
            return [.. hashes];
        }
        var a = Run(out var falls);
        Assert.True(falls.Count >= 3, $"{falls.Count} trees felled");
        Assert.Equal(a, Run(out _)); // the same commands, the same game
    }

    [Fact]
    public void ATrunkFallingOnInfantryHurtsThemOnce()
    {
        const int n = 40;
        var w = Arena.New(n);
        var tree = OneTree(14, 20, density: 2);
        w.AttachTerrain(Flat(n, (x, y) => x == tree.X && y == tree.Y ? 2 : 0));
        var under = w.Spawn(1, "Rifleman", tree.X + 2, tree.Y);  // on its second cell east
        var beside = w.Spawn(1, "Rifleman", tree.X, tree.Y + 2); // clear of it
        var (car, driver) = w.SpawnCrewed(1, "technical", tree.X + 4, tree.Y); // on its fourth cell: a vehicle, no damage
        var (rocket, _) = Weapon("at_missile", "rocket");
        // A blast a cell west of the trunk: it falls east, over the soldier and the car.
        w.FellByBlast(Off(TerrainMap.TrunkPoint(tree), -1024, 0), rocket, Fixed.Zero);
        var f = Assert.Single(w.FallenTrunks);
        Assert.Equal(2, f.Dir);
        Assert.True(f.Covers(Cell.Of(under.Pos)) && f.Covers(Cell.Of(car.Pos)));
        var max = under.Def.MaxHp;
        Assert.Equal(max - max * Rules.Trees!.FallDamagePercent / 100, under.HP);
        Assert.Equal(beside.Def.MaxHp, beside.HP);
        Assert.Equal(car.Def.MaxHp, car.HP);
        Assert.Equal(driver.Def.MaxHp, driver.HP);
        var hp = under.HP;
        w.Run(100);
        Assert.Equal(hp, under.HP); // once
        Assert.True(Rules.Trees.FallDamagePercent is > 0 and <= 20, "light damage");
    }

    [Fact]
    public void ATrunkBlownAcrossARoadBlocksAJeepForItsTimeThenItPasses()
    {
        const int n = 48;
        var w = Arena.NewSentinel(n);
        var tree = OneTree(20, 18, density: 2);
        w.AttachTerrain(Flat(n, (x, y) => x == tree.X && y == tree.Y ? 2 : 0));
        var g = w.Ground!;
        // A dirt road on rows 19 to 21 between walls (rows 17 and 22 and beyond); the tree stands at its north edge, row 18.
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                if (y is <= 17 or >= 22)
                {
                    w.Grid.Set(new Cell(x, y), Sim.Terrain.Blocked);
                }
                else if (y >= 19)
                {
                    g.SetRoadKind(new Cell(x, y), RoadKind.Dirt);
                }
            }
        }
        var (jeep, _) = w.SpawnCrewed(1, "humvee", tree.X - 12, 20, Facing.East);
        w.Step();
        // A rocket lands just north of the tree: it falls south across the road (rows 19 to 21; row 22 is the wall).
        var (rocket, _) = Weapon("at_missile", "rocket");
        w.FellByBlast(Off(TerrainMap.TrunkPoint(tree), 0, -700), rocket, Fixed.Zero);
        var f = Assert.Single(w.FallenTrunks);
        Assert.Equal(4, f.Dir);
        Assert.Equal(3, f.Length);
        Assert.True(g.Fallen(new Cell(tree.X, 20)) && g.Road(new Cell(tree.X, 20)));
        var felled = w.Tick;
        var passedAt = 0u;
        for (var t = 0; t < 1200 && passedAt == 0; t++)
        {
            if (t % 100 == 0)
            {
                w.Order(1, CommandKind.Move, [jeep], cell: new Cell(tree.X + 12, 20)); // a player sending it again and again
            }
            w.Step();
            if (jeep.Pos.X.Raw > Fixed.FromInt(tree.X + 1).Raw)
            {
                passedAt = w.Tick;
            }
        }
        Assert.True(passedAt > felled + 600, $"the jeep got past at tick {passedAt}, the trunk fell at {felled}");
        Assert.True(passedAt < felled + 600 + 200, $"the jeep got past at tick {passedAt}, the trunk was gone at {felled + 600}");
    }

    [Fact]
    public void BlastFallsAreHashedAndMapsWithoutTheListFellNothing()
    {
        const int n = 48;
        var (bomb, splash) = Weapon("heavy_bombs", "bomb");
        var at = new Vec(Fixed.FromInt(24), Fixed.FromInt(24));
        World Make(TerrainRules rules)
        {
            var w = Arena.New(n);
            w.AttachTerrain(Flat(n, (_, _) => 3, rules));
            return w;
        }
        var a = Make(Rules);
        var b = Make(Rules);
        Assert.Equal(a.Hash(), b.Hash());
        a.FellByBlast(at, bomb, splash);
        Assert.NotEqual(a.Hash(), b.Hash());
        b.FellByBlast(at, bomb, splash);
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(a.FallenTrunks, b.FallenTrunks);
        // The list is in stump order whatever the order the blast felled them in.
        Assert.Equal(a.FallenTrunks.OrderBy(f => f.Stump.Y).ThenBy(f => f.Stump.X), a.FallenTrunks);
        // Rules whose trees name no warheads blow nothing down.
        var json = File.ReadAllText(RulesPath);
        var start = json.IndexOf("\"fell\": {", StringComparison.Ordinal);
        var end = json.IndexOf('}', start);
        var quiet = TerrainLoader.ParseRules(json[..start] + "\"fell\": {}" + json[(end + 1)..]);
        var c = Make(quiet);
        c.FellByBlast(at, bomb, splash);
        Assert.Empty(c.TreeEvents);
        Assert.NotEqual(Rules.Digest(), quiet.Digest());
    }

    [Fact]
    public void TheRulesNameOnlyWarheadsAndTagsTheContentHas()
    {
        var json = File.ReadAllText(RulesPath);
        var bad = TerrainLoader.ParseRules(json.Replace("\"rocket\": 100,", "\"rocket\": 100, \"pebble\": 100,"));
        var e = Assert.Throws<MatchSetupException>(() => Arena.New(16).AttachTerrain(Flat(16, (_, _) => 0, bad)));
        Assert.Contains("pebble", e.Message);
        var tag = TerrainLoader.ParseRules(json.Replace("\"fallDamageTags\": [\"infantry\"]", "\"fallDamageTags\": [\"elves\"]"));
        Assert.Contains("elves", Assert.Throws<MatchSetupException>(() => Arena.New(16).AttachTerrain(Flat(16, (_, _) => 0, tag))).Message);
        Assert.Contains("fallDamage wants", Assert.Throws<ContentException>(() => TerrainLoader.ParseRules(json.Replace("\"fallDamage\": 10", "\"fallDamage\": 101"))).Message);
        Assert.Contains("trees.fell wants", Assert.Throws<ContentException>(() => TerrainLoader.ParseRules(json.Replace("\"rocket\": 100,", "\"rocket\": 0,"))).Message);
        Assert.Contains("fellMinRadius wants", Assert.Throws<ContentException>(() => TerrainLoader.ParseRules(json.Replace("\"fellMinRadius\": 15", "\"fellMinRadius\": -1"))).Message);
    }
}
