using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimCore.Tests;

// Forest fire (docs/design.md, "Forest fire"): ignition by warhead (a bomb lights a tree or two that never spread, shells and
// missiles nothing), burning 20 to 40 s and ending as a snag on ash, spread by forest density, the global cap, smoke that blocks
// sight, infantry hurt and walking out, vehicle routes round the fire and back after, blasted cells opening sight (decision 456),
// determinism, the hash and replays.
public class FireTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
    private static readonly FireRules Fire = TestContent.Mvp.Fire!;

    private static TerrainMap Map(int n, Func<int, int, int> forest)
    {
        var flags = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                flags[y * n + x] = (byte)forest(x, y);
            }
        }
        return new TerrainMap(n, n, new int[(n + 1) * (n + 1)], flags, Rules);
    }

    private static World Wood(int n, Func<int, int, int> forest, bool fog = false)
    {
        var w = Arena.NewSentinel(n, edit: fog ? s => s with { Fog = true } : null);
        w.AttachTerrain(Map(n, forest));
        return w;
    }

    // The splash of a weapon (its first effect with this warhead).
    private static (Warhead Warhead, Fixed Splash) Weapon(string component, string warhead)
    {
        var w = TestContent.Mvp.Components.Single(c => c.Id == component).Effects.OfType<WeaponEffect>().First(e => e.Warhead.Id == warhead);
        return (w.Warhead, w.Splash);
    }

    // Blast is what Explode does to the trees and the forest: blown down, then set on fire.
    private static void Blast(World w, Vec at, Warhead wh, Fixed splash)
    {
        w.FellByBlast(at, wh, splash);
        w.IgniteByBlast(at, wh, splash);
    }

    // Lit counts the cells blasts of a weapon set on fire, each at one of 16 points over forest of a density (a fresh map each).
    private static List<int> Lit(string component, string warhead, int density)
    {
        var (wh, splash) = Weapon(component, warhead);
        var counts = new List<int>();
        for (var k = 0; k < 16; k++)
        {
            var w = Wood(64, (_, _) => density);
            var at = new Vec(Fixed.FromRaw(20 * 1024 + k * 1531 % 24576), Fixed.FromRaw(20 * 1024 + k * 2741 % 24576));
            Blast(w, at, wh, splash);
            var lit = w.FireEvents.Where(e => e.Kind == FireEventKind.Ignited).ToList();
            Assert.All(lit, e =>
            {
                Assert.False(e.Spreads); // a normal bomb's fires never spread
                Assert.True(w.Ground!.Burning(e.Cell));
                Assert.True((e.Cell.Center() - at).Len().Raw <= splash.Raw + Fixed.OneRaw); // round the crater, within the splash
            });
            Assert.Equal(lit.Count, w.Fires.Count);
            counts.Add(lit.Count);
        }
        return counts;
    }

    [Fact]
    public void BombsLightATreeOrTwoAndShellsAndMissilesNone()
    {
        foreach (var bomb in new[] { "bombs_500lb", "bombs_2000lb", "heavy_bombs", "strategic_bombs" })
        {
            Assert.All(Lit(bomb, "bomb", 3), n => Assert.InRange(n, 1, 2));
            Assert.All(Lit(bomb, "bomb", 2), n => Assert.InRange(n, 1, 2));
        }
        Assert.Contains(2, Lit("heavy_bombs", "bomb", 3));
        Assert.Contains(1, Lit("heavy_bombs", "bomb", 3));
        // The trees round the crater burn first: a bomb in dense forest lights standing trunks, not the stumps it blew down.
        var w = Wood(64, (_, _) => 3);
        var (wh, splash) = Weapon("heavy_bombs", "bomb");
        Blast(w, new Cell(32, 32).Center(), wh, splash);
        Assert.All(w.FireEvents.Where(e => e.Kind == FireEventKind.Ignited), e => Assert.True(e.Snag));
        // Shells, rockets and missiles light nothing.
        foreach (var (component, warhead) in new[]
        {
            ("tank_120mm", "he_mpat"), ("mortar_tube", "mortar_bomb"), ("kornet_atgm", "atgm"), ("at_missile", "rocket"),
            ("twin_missiles", "heavy_missile"), ("ballistic_missile", "ballistic"), ("heavy_rockets", "mrl_rocket"),
        })
        {
            Assert.All(Lit(component, warhead, 3), n => Assert.Equal(0, n));
        }
        // Open ground and water never burn.
        Assert.All(Lit("heavy_bombs", "bomb", 0), n => Assert.Equal(0, n));
    }

    [Fact]
    public void ACellBurnsTwentyToFortySecondsThenStandsASnagOnEmbersThenAsh()
    {
        var lengths = new List<int>();
        var w = Wood(64, (_, _) => 3);
        var g = w.Ground!;
        var trunks = new List<Cell>();
        for (var y = 10; y < 54 && trunks.Count < 40; y += 3)
        {
            for (var x = 10; x < 54 && trunks.Count < 40; x += 3)
            {
                if (g.HasTrunk(new Cell(x, y)))
                {
                    trunks.Add(new Cell(x, y));
                }
            }
        }
        var tank = Rules.ClassOf(Locomotor.Tracked, Fixed.One);
        foreach (var c in trunks)
        {
            Assert.True(w.IgniteCell(c, 0));
            Assert.Equal(FireState.Burning, w.FireAt(c));
            Assert.NotEqual(0, g.Cost(tank, c)); // the costs follow at the next terrain tick
            lengths.Add(w.FireOf(c)!.Value.Length);
        }
        w.Step(); // tick 0: a terrain tick
        Assert.All(trunks, c => Assert.Equal(0, g.Cost(tank, c))); // a wall to a tank while it burns
        Assert.All(lengths, l => Assert.InRange(l, 400, 800)); // 20 to 40 s
        Assert.True(lengths.Distinct().Count() > 10, "lengths are rolled per cell");
        var outAt = new Dictionary<Cell, uint>();
        var cooled = new HashSet<Cell>();
        for (var t = 0; t < 1500; t++)
        {
            if (t > 0)
            {
                w.Step();
            }
            foreach (var e in w.FireEvents)
            {
                if (e.Kind == FireEventKind.BurntOut)
                {
                    outAt[e.Cell] = w.Tick - 1;
                    Assert.True(e.Snag); // its trunk stands on, black
                    Assert.Equal(FireState.Burnt, w.FireAt(e.Cell));
                }
                if (e.Kind == FireEventKind.Cooled)
                {
                    cooled.Add(e.Cell);
                    Assert.Equal(w.Tick - 1, outAt[e.Cell] + (uint)Fire.EmberTicks);
                }
                Assert.NotEqual(FireEventKind.Ignited, e.Kind); // they do not spread
            }
        }
        Assert.Equal(trunks.Count, outAt.Count);
        Assert.Equal(trunks.Count, cooled.Count);
        Assert.Empty(w.Fires);
        foreach (var c in trunks)
        {
            Assert.Equal(FireState.Ash, w.FireAt(c));
            Assert.Equal(2, g.Forest(c)); // one forest level down
            Assert.True(w.IsSnag(c));
            Assert.True(g.HasTrunk(c)); // a snag is a trunk: an obstacle as before
            Assert.False(w.IgniteCell(c, 100)); // it never burns again
            Assert.Contains(w.BurntCells, b => b.Cell == c && b.EndedAt == outAt[c]);
        }
    }

    // Partners: islands of two forest cells (a source and a partner beside it or at a corner) far apart on open ground; the
    // sources lit, the fraction of partners that caught from them.
    private static double Partners(int density, bool corner)
    {
        const int n = 128;
        var sources = new List<Cell>();
        var partners = new List<Cell>();
        for (var j = 0; j < 8; j++)
        {
            for (var i = 0; i < 8; i++)
            {
                var s = new Cell(8 + i * 14, 8 + j * 14);
                sources.Add(s);
                partners.Add(corner ? new Cell(s.X + 1, s.Y - 1) : new Cell(s.X + 1, s.Y));
            }
        }
        var w = Wood(n, (x, y) => sources.Contains(new Cell(x, y)) || partners.Contains(new Cell(x, y)) ? density : 0);
        foreach (var s in sources)
        {
            Assert.True(w.IgniteCell(s, 100));
        }
        w.Run(1800);
        Assert.Empty(w.Fires);
        return partners.Count(p => w.Ground!.Burnt(p)) / (double)partners.Count;
    }

    [Fact]
    public void FireSpreadsMostInDensePineLessInAGroveLeastInScrubAndNeverOverOpenGround()
    {
        // Over a burn of 20 to 40 s (20 to 40 looks): a side neighbour 1 - (1 - p)^looks, about 0.91 dense, 0.70 grove, 0.16 scrub.
        var dense = Partners(3, corner: false);
        var grove = Partners(2, corner: false);
        var scrub = Partners(1, corner: false);
        Assert.InRange(dense, 0.80, 1.0);
        Assert.InRange(grove, 0.52, 0.86);
        Assert.InRange(scrub, 0.04, 0.32);
        Assert.True(dense > grove && grove > scrub);
        // A corner neighbour at half the chance: about 0.70 in dense forest.
        Assert.InRange(Partners(3, corner: true), 0.52, 0.86);
        // Open ground never burns; nor does water.
        var w = Wood(32, (x, _) => x < 16 ? 3 : 0);
        Assert.False(w.IgniteCell(new Cell(20, 10), 100));
        Assert.True(w.IgniteCell(new Cell(15, 10), 100));
        w.Run(3000);
        Assert.All(w.BurntCells, b => Assert.True(b.Cell.X < 16));
    }

    [Fact]
    public void NoMoreThanTheCapBurnAtOnceAndSpreadingLightsFewATick()
    {
        var w = Wood(160, (_, _) => 3);
        Assert.True(w.IgniteCell(new Cell(80, 80), 100));
        var most = 0;
        var reached = 0;
        for (var t = 0; t < 4000; t++)
        {
            w.Step();
            most = Math.Max(most, w.Fires.Count);
            reached += w.Fires.Count == Fire.MaxBurning ? 1 : 0;
            Assert.True(w.FireEvents.Count(e => e.Kind == FireEventKind.Ignited) <= Fire.MaxIgnitionsPerTick);
            Assert.True(w.Smokes.Count(s => s.Fire) <= Fire.SmokeMax);
        }
        Assert.Equal(Fire.MaxBurning, most);
        Assert.True(reached > 100, $"at the cap {reached} ticks");
        Assert.True(w.BurntCells.Count > 400, $"{w.BurntCells.Count} burnt");
        // The burning list stays in cell order.
        Assert.Equal(w.Fires.OrderBy(f => f.Cell.Y).ThenBy(f => f.Cell.X).ToList(), w.Fires.ToList());
    }

    [Fact]
    public void ABombsFiresBurnOutWithoutSpreading()
    {
        var w = Wood(64, (_, _) => 3);
        var (wh, splash) = Weapon("heavy_bombs", "bomb");
        Blast(w, new Cell(32, 32).Center(), wh, splash);
        var lit = w.Fires.Select(f => f.Cell).ToList();
        Assert.InRange(lit.Count, 1, 2);
        Assert.All(w.Fires, f => Assert.Equal(0, f.SpreadPercent));
        for (var t = 0; t < 900; t++)
        {
            w.Step();
            Assert.DoesNotContain(w.FireEvents, e => e.Kind == FireEventKind.Ignited);
        }
        Assert.Empty(w.Fires);
        Assert.Equal(lit.OrderBy(c => c.Y).ThenBy(c => c.X).ToList(), w.BurntCells.Select(b => b.Cell).OrderBy(c => c.Y).ThenBy(c => c.X).ToList());
    }

    [Fact]
    public void BurningForestSmokesAndTheSmokeBlocksSight()
    {
        // A soldier on open ground sees an enemy 6 cells off; scrub burning beside the line between them smokes it over.
        bool Spotted(bool burn)
        {
            var w = Wood(48, (x, y) => x == 13 && y == 36 ? 1 : 0, fog: true);
            var eye = w.Spawn(1, "Rifleman", 10, 33);
            var t = w.Spawn(2, "Rifleman", 16, 33);
            eye.Stance = Stance.HoldFire;
            t.Stance = Stance.HoldFire;
            if (burn)
            {
                Assert.True(w.IgniteCell(new Cell(13, 36), 0));
                var s = Assert.Single(w.Smokes);
                Assert.True(s.Fire);
                Assert.Equal(0u, s.Source);
                Assert.Equal(new Cell(13, 36).Center(), s.At);
                Assert.Equal(Fire.SmokeRadius, s.Radius);
                Assert.Contains(w.SmokeEvents, e => e.Kind == SmokeEventKind.Popped && e.Unit == 0);
            }
            w.Step();
            w.Step();
            return World.IsSpotted(t, 1);
        }
        Assert.True(Spotted(false));
        Assert.False(Spotted(true));
    }

    [Fact]
    public void InfantryInAFireAreHurtAndWalkOut()
    {
        var w = Wood(48, (x, y) => x is >= 18 and < 26 && y is >= 18 and < 26 ? 1 : 0);
        var s = w.Spawn(1, "Rifleman", 20, 20);
        var full = s.HP;
        var cells = new List<Cell>();
        for (var y = 19; y <= 21; y++)
        {
            for (var x = 19; x <= 21; x++)
            {
                cells.Add(new Cell(x, y));
                Assert.True(w.IgniteCell(new Cell(x, y), 0));
            }
        }
        w.Step(); // the first burn
        Assert.Equal(full - full * Fire.DamagePercent / 100, s.HP);
        Assert.Equal(OrderKind.Move, s.Order.Kind);
        Assert.False(w.Ground!.Burning(s.Order.Cell)); // to the nearest cell not burning
        Assert.True(Math.Max(Math.Abs(s.Order.Cell.X - 20), Math.Abs(s.Order.Cell.Y - 20)) == 2);
        w.Run(80);
        Assert.False(w.Ground.Burning(Cell.Of(s.Pos)), "walked out");
        var hp = s.HP;
        Assert.True(hp >= full - 3 * full * Fire.DamagePercent / 100, $"{hp} of {full}");
        w.Run(100);
        Assert.Equal(hp, s.HP); // out of the fire, no more burns
        // Fire changes nothing for infantry routes, burning or burnt: they walk out when it burns them.
        var foot = Rules.ClassOf(Locomotor.Foot, s.Def.Archetype.Radius);
        Assert.Equal(w.Ground.Cost(foot, new Cell(24, 24)), w.Ground.Cost(foot, cells[4]));
        w.Run(800);
        Assert.Equal(FireState.Burnt, w.FireAt(cells[4]));
        Assert.Equal(0, w.Ground.Forest(cells[4]));
        Assert.Equal(w.Ground.Cost(foot, new Cell(24, 24)), w.Ground.Cost(foot, cells[4])); // the scrub's cost as before
        Assert.Equal(w.Ground.SpeedPercent(foot, new Cell(24, 24), false), w.Ground.SpeedPercent(foot, cells[4], false));
    }

    [Fact]
    public void AVehicleInACellThatCatchesIsPushedOut()
    {
        var w = Wood(48, (x, y) => x is >= 18 and < 26 && y is >= 18 and < 26 ? 1 : 0);
        var (car, driver) = w.SpawnCrewed(2, "technical", 20, 20);
        for (var y = 20; y <= 20; y++)
        {
            Assert.True(w.IgniteCell(new Cell(20, y), 0));
        }
        w.Run(40);
        Assert.False(w.Ground!.Burning(Cell.Of(car.Pos)));
        Assert.Equal(driver.Def.MaxHp, driver.HP); // inside, never burnt
    }

    [Fact]
    public void VehicleRoutesGoRoundAFireAndThroughAgainAfter()
    {
        const int n = 64;
        var w = Wood(n, (x, _) => x is >= 28 and < 36 ? 1 : 0); // a band of scrub north to south
        for (var y = 0; y < 40; y++)
        {
            for (var x = 30; x <= 32; x++)
            {
                Assert.True(w.IgniteCell(new Cell(x, y), 0)); // burning across the band except its south end
            }
        }
        var (car, _) = w.SpawnCrewed(2, "technical", 16, 20, Facing.East);
        var cls = Rules.ClassOf(car.Def);
        w.Step(); // the terrain tick: the burning cells become walls to it
        Assert.Equal(0, w.Ground!.Cost(cls, new Cell(31, 20)));
        w.Order(2, CommandKind.Move, [car], cell: new Cell(48, 20));
        var south = false;
        for (var t = 0; t < 360 && Cell.Of(car.Pos) != new Cell(48, 20); t++)
        {
            w.Step();
            Assert.False(w.Ground.Burning(Cell.Of(car.Pos)), $"tick {w.Tick}: in the fire at {Cell.Of(car.Pos)}");
            south |= car.Pos.X.Raw >> Fixed.Shift is >= 29 and <= 33 && car.Pos.Y.Raw >> Fixed.Shift >= 40;
        }
        Assert.True(south, "went round the fire's south end");
        Assert.True((car.Pos - new Cell(48, 20).Center()).Len().Raw < Fixed.FromInt(2).Raw, $"arrived at {Cell.Of(car.Pos)}");
        w.Run(900); // the fire burns out
        Assert.Empty(w.Fires);
        Assert.True(w.Ground.Passable(cls, new Cell(31, 20)));
        w.Order(2, CommandKind.Move, [car], cell: new Cell(16, 20));
        var most = 0;
        for (var t = 0; t < 300 && Cell.Of(car.Pos) != new Cell(16, 20); t++)
        {
            w.Step();
            most = Math.Max(most, (int)(car.Pos.Y.Raw >> Fixed.Shift));
        }
        Assert.True(most < 30, $"straight back through the burnt band (south to row {most})");
    }

    [Fact]
    public void AFireLitAcrossARouteSendsItRound()
    {
        // A technical already driving east when the band catches: the reroute pass sends it round.
        var w = Wood(64, (x, _) => x is >= 28 and < 36 ? 1 : 0);
        var (car, _) = w.SpawnCrewed(2, "technical", 12, 20, Facing.East);
        w.Order(2, CommandKind.Move, [car], cell: new Cell(52, 20));
        w.Run(20);
        Assert.True(car.Moving);
        for (var y = 0; y < 40; y++)
        {
            for (var x = 30; x <= 32; x++)
            {
                w.IgniteCell(new Cell(x, y), 0);
            }
        }
        for (var t = 0; t < 400 && Cell.Of(car.Pos) != new Cell(52, 20); t++)
        {
            w.Step();
            Assert.False(w.Ground!.Burning(Cell.Of(car.Pos)), $"tick {w.Tick}: in the fire");
        }
        Assert.True((car.Pos - new Cell(52, 20).Center()).Len().Raw < Fixed.FromInt(2).Raw, $"arrived at {Cell.Of(car.Pos)}");
    }

    [Fact]
    public void BurntCellsDropAForestLevelAndOpenSight()
    {
        var w = Wood(32, (_, _) => 3, fog: true);
        var g = w.Ground!;
        var c = new Cell(10, 10);
        var v = g.Version;
        Assert.True(w.IgniteCell(c, 0));
        w.Step(); // the terrain tick: its costs
        var cells = new List<Cell>();
        Assert.True(g.ChangedSince(v, cells, sightOnly: false) && cells.Contains(c));
        cells.Clear();
        Assert.True(g.ChangedSince(v, cells, sightOnly: true));
        Assert.Empty(cells); // catching fire changes no sight line (its smoke does)
        v = g.Version;
        w.Run(840);
        Assert.Equal(2, g.Forest(c));
        Assert.True(g.ChangedSince(v, cells, sightOnly: true));
        Assert.Contains(c, cells); // burnt out: sight lines through it change
        Assert.True(w.FogMatchesRebuild());
    }

    [Fact]
    public void BlastedCellsLoseAForestLevelSoSightOpensAndPushedOnesDoNot()
    {
        var w = Wood(64, (_, _) => 3, fog: true);
        var g = w.Ground!;
        var (wh, splash) = Weapon("heavy_bombs", "bomb");
        var v = g.Version;
        w.FellByBlast(new Cell(32, 32).Center(), wh, splash);
        var fell = w.TreeEvents.Where(e => e.Kind == TreeEventKind.Fell).Select(e => e.Stump).ToList();
        Assert.True(fell.Count >= 12);
        Assert.Equal(fell, w.BlastedCells);
        Assert.All(fell, c =>
        {
            Assert.Equal(2, g.Forest(c));
            Assert.True(g.Thinned(c));
        });
        Assert.Equal(3, g.Forest(new Cell(5, 5)));
        var cells = new List<Cell>();
        Assert.True(g.ChangedSince(v, cells, sightOnly: true));
        Assert.All(fell, c => Assert.Contains(c, cells));
        // A second blast thins nothing twice.
        w.FellByBlast(new Cell(32, 32).Center(), wh, splash);
        Assert.All(fell, c => Assert.Equal(2, g.Forest(c)));
        // A pushed tree thins nothing.
        var tree = Enumerable.Range(0, 60).Select(x => new Cell(x, 5)).First(g.HasTrunk);
        Assert.True(w.FellTree(tree, 2, 0, TreeFallCause.Pushed, default));
        Assert.Equal(3, g.Forest(tree));
        Assert.False(g.Thinned(tree));
        w.Step();
        Assert.True(w.FogMatchesRebuild());
    }

    [Fact]
    public void FireLeavesTheMapsDigestAlone()
    {
        var before = Map(48, (_, _) => 3).Digest();
        var w = Wood(48, (_, _) => 3);
        Assert.True(w.IgniteCell(new Cell(20, 20), 100));
        var (wh, splash) = Weapon("heavy_bombs", "bomb");
        w.FellByBlast(new Cell(10, 10).Center(), wh, splash);
        w.Run(1200);
        Assert.NotEmpty(w.BurntCells);
        Assert.NotEmpty(w.BlastedCells);
        Assert.Equal(before, w.Ground!.Digest()); // computed now, with the cells burnt and thinned
    }

    // Burning runs a dense wood with soldiers and technicals walking through it and two spreading fires, hashing every tick.
    private static ulong[] Burning(bool reverse, bool fire = true)
    {
        var w = Wood(96, (x, y) => x is >= 30 and < 66 ? (y % 20 < 14 ? 3 : 1) : 0);
        w.ReverseMoveOrder = reverse;
        var walkers = new List<Unit>();
        for (var i = 0; i < 16; i++)
        {
            walkers.Add(w.Spawn(2, i % 4 == 3 ? "Gunner" : "Rifleman", 20 + i % 4 * 2, 30 + i / 4 * 8));
        }
        for (var i = 0; i < 3; i++)
        {
            walkers.Add(w.SpawnCrewed(2, "technical", 18, 40 + i * 12).Vehicle);
        }
        w.Order(2, CommandKind.Move, walkers, cell: new Cell(84, 48));
        if (fire)
        {
            Assert.True(w.IgniteCell(new Cell(48, 40), 100));
            Assert.True(w.IgniteCell(new Cell(40, 70), 100));
        }
        var hashes = new List<ulong>();
        for (var t = 0; t < 1500; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
        }
        if (fire)
        {
            Assert.True(w.BurntCells.Count > 100, $"{w.BurntCells.Count} burnt");
        }
        return [.. hashes];
    }

    [Fact]
    public void FireIsDeterministic()
    {
        var a = Burning(false);
        Assert.Equal(a, Burning(false));
        Assert.Equal(a, Burning(true));
        Assert.NotEqual(a[^1], Burning(false, fire: false)[^1]); // fire is game state: hashed
    }

    [Fact]
    public void AReplayOfAFirePlaysTheSameHashes()
    {
        const int n = 64;
        TerrainMap Forest() => Map(n, (x, y) => x is >= 16 and < 48 && y is >= 16 and < 48 ? (x + y) % 5 == 0 ? 2 : 3 : 0);
        var setup = TestContent.Sentinel(9) with { Scenario = Scenario.SandboxName, Terrain = "firetest.json" };
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = Replay.NewWorld(TestContent.Mvp, setup, Forest());
        var commands = new List<Command>
        {
            w.Issue(new Command { Tick = 1, Player = 2, Kind = CommandKind.SandboxSpawn, Unit = "Rifleman", Cell = new Cell(30, 30) }),
            w.Issue(new Command { Tick = 1, Player = 2, Kind = CommandKind.SandboxSpawn, Unit = "Rifleman", Cell = new Cell(31, 30) }),
            w.Issue(new Command { Tick = 2, Player = 1, Kind = CommandKind.SandboxIgnite, Cell = new Cell(30, 30), Index = 100 }),
            w.Issue(new Command { Tick = 2, Player = 1, Kind = CommandKind.SandboxIgnite, Cell = new Cell(31, 30), Index = 0 }),
            w.Issue(new Command { Tick = 2, Player = 1, Kind = CommandKind.SandboxIgnite, Cell = new Cell(40, 20), Index = 100 }),
        };
        var hashes = new List<ulong>();
        while (w.Tick < 1200)
        {
            w.Step();
            hashes.Add(w.Hash());
        }
        Assert.True(w.BurntCells.Count > 50);
        Assert.Contains(w.Units, u => u.Def.Archetype.Id == "assault" && u.HP < u.Def.MaxHp); // the soldiers got burnt
        var r = new Replay { Setup = setup, Ticks = 1200, Commands = commands };
        var (again, world) = r.Play(TestContent.Mvp, Forest());
        Assert.Equal(hashes, again);
        Assert.Equal(w.BurntCells, world.BurntCells);
        // In a real match the sandbox's fire command does nothing.
        var real = Replay.NewWorld(TestContent.Mvp, setup with { Scenario = "battle" }, Forest());
        real.Issue(new Command { Tick = 1, Player = 1, Kind = CommandKind.SandboxIgnite, Cell = new Cell(30, 31), Index = 100 });
        real.Run(5);
        Assert.Empty(real.Fires);
    }

    [Fact]
    public void WithoutTerrainOrFireRulesNothingBurns()
    {
        var flat = Arena.NewSentinel(32);
        Assert.False(flat.IgniteCell(new Cell(10, 10), 100));
        Assert.Equal(FireState.None, flat.FireAt(new Cell(10, 10)));
        var (wh, splash) = Weapon("heavy_bombs", "bomb");
        flat.IgniteByBlast(new Cell(10, 10).Center(), wh, splash);
        Assert.Empty(flat.Fires);
    }

    [Theory]
    [InlineData("fire.json", "\"maxBurning\": 150,", "", "maxBurning is required")]
    [InlineData("fire.json", "\"burnTicks\": [400, 800]", "\"burnTicks\": [800, 400]", "burnTicks[1]")]
    [InlineData("fire.json", "\"spread\": [0, 6, 40, 80]", "\"spread\": [0, 6, 40]", "spread: want 4 values")]
    [InlineData("fire.json", "\"tags\": [\"infantry\"]", "\"tags\": [\"tinder\"]", "unknown tag")]
    [InlineData("fire.json", "\"fleeCells\": 8", "\"fleeCells\": 8, \"wind\": 1", "wind")]
    [InlineData("warheads.json", "\"fire\": { \"radiusPercent\": 100, \"count\": [1, 2], \"spreads\": false }", "\"fire\": { \"count\": [1, 2], \"spreads\": false }", "radius or a radiusPercent")]
    [InlineData("warheads.json", "\"fire\": { \"radiusPercent\": 100, \"count\": [1, 2], \"spreads\": false }", "\"fire\": { \"radiusPercent\": 100, \"count\": [1, 2], \"spreads\": false, \"spreadPercent\": 200 }", "spreadPercent is for")]
    [InlineData("warheads.json", "\"fire\": { \"radiusPercent\": 100, \"count\": [1, 2], \"spreads\": false }", "\"fire\": { \"radiusPercent\": 100, \"count\": [1, 2] }", "fire.spreads is required")]
    public void BadFireContentIsRefused(string file, string from, string to, string want)
    {
        var sources = TestContent.MvpSources()
            .Select(s => s.Name == file ? s with { Json = s.Json.Replace(from, to, StringComparison.Ordinal) } : s)
            .ToList();
        Assert.NotEqual(TestContent.MvpSources().Single(s => s.Name == file).Json, sources.Single(s => s.Name == file).Json);
        Assert.Contains(want, Assert.Throws<ContentException>(() => ContentLoader.Load(sources)).Message);
    }

    [Fact]
    public void TwoFireBlocksAreRefused()
    {
        var sources = TestContent.MvpSources();
        sources.Add(sources.Single(s => s.Name == "fire.json") with { Name = "extra/fire.json" });
        Assert.Contains("fire: already defined", Assert.Throws<ContentException>(() => ContentLoader.Load(sources)).Message);
    }

    [Fact]
    public void AWarheadFireBlockCanLightAWideAreaThatSpreads()
    {
        // The hook the incendiary bomb and the hypersonic missile will use: every forest cell within the radius, spreading.
        var sources = TestContent.MvpSources()
            .Select(s => s.Name == "warheads.json" ? s with
            {
                Json = s.Json.Replace("\"fire\": { \"radiusPercent\": 100, \"count\": [1, 2], \"spreads\": false }",
                    "\"fire\": { \"radius\": 40, \"spreads\": true, \"spreadPercent\": 200 }", StringComparison.Ordinal),
            } : s)
            .ToList();
        var content = ContentLoader.Load(sources);
        var bomb = content.Warhead("bomb")!;
        Assert.Equal(WarheadFire.Every, bomb.Fire!.CountMin);
        Assert.Equal(200, bomb.Fire.SpreadPercent);
        Assert.NotEqual(TestContent.Mvp.Hash, content.Hash);
        var setup = TestContent.Sentinel(1).For(content, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = new World(new Grid(64, 64), MatchCompiler.Compile(content, setup));
        w.AttachTerrain(Map(64, (_, _) => 2));
        w.IgniteByBlast(new Cell(32, 32).Center(), bomb, Fixed.Zero);
        var lit = w.Fires.Select(f => f.Cell).ToList();
        Assert.InRange(lit.Count, 44, 56); // the cells within 4 cells (about 50)
        Assert.All(w.Fires, f => Assert.Equal(200, f.SpreadPercent));
        Assert.All(lit, c => Assert.True((c.Center() - new Cell(32, 32).Center()).Len().Raw <= Fixed.FromInt(4).Raw + 400));
    }
}
