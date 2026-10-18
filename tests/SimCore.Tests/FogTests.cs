using SimCore.Bots;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// Fog of war (MatchSetup.Fog): what a player sees, line of sight over ridges and through woods,
// air units seeing over both, the spotter rule for ordered attacks, determinism of the grids,
// and the skirmish AI seeing only what its side spots.
public class FogTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int M = 512; // raw per metre

    // Fogged returns an arena with fog of war, on terrain: corner heights from height (raw) and
    // per-cell forest density from forest.
    private static World Fogged(int size, Func<int, int, int>? height = null, Func<int, int, int>? forest = null)
    {
        var w = Arena.New(size, edit: s => s with { Fog = true });
        var corners = new int[(size + 1) * (size + 1)];
        var flags = new byte[size * size];
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                corners[y * (size + 1) + x] = height?.Invoke(x, y) ?? 0;
                if (x < size && y < size)
                {
                    flags[y * size + x] = (byte)(forest?.Invoke(x, y) ?? 0);
                }
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, corners, flags, Rules));
        return w;
    }

    private static bool Sees(World w, byte player, Unit u) => World.IsVisibleTo(u, player);

    [Fact]
    public void WithoutFogEverythingIsVisible()
    {
        var w = Arena.New(64);
        var a = w.Spawn(1, "Rifleman", 5, 5);
        var b = w.Spawn(2, "Rifleman", 60, 60);
        w.Run(2);
        Assert.False(w.Fog);
        Assert.Null(w.FogOf(1));
        Assert.True(Sees(w, 1, b));
        Assert.True(Sees(w, 2, a));
        Assert.Equal(0u, b.SpottedBy & (1u << 1)); // but nobody spots it: weapons still need a spotter
    }

    [Fact]
    public void EnemiesOutsideVisionAreInvisibleAndExploredGroundStays()
    {
        var w = Fogged(64);
        var me = w.Spawn(1, "Rifleman", 10, 30); // vision 7 cells
        var near = w.Spawn(2, "Rifleman", 15, 30);
        var far = w.Spawn(2, "Rifleman", 40, 30);
        w.Run(1);
        Assert.True(w.Fog);
        Assert.True(Sees(w, 1, me));
        Assert.True(Sees(w, 1, near));
        Assert.False(Sees(w, 1, far));
        Assert.True(World.IsSpotted(near, 1));
        Assert.False(World.IsSpotted(far, 1));
        var fog = w.FogOf(1)!;
        Assert.True(fog.Visible(10, 30) && fog.Visible(16, 30));
        Assert.False(fog.Visible(18, 30));
        Assert.False(fog.Explored(40, 30));

        // Walk away: the old ground stays explored but is no longer visible.
        w.Order(1, CommandKind.Move, [me], cell: new Cell(10, 55));
        w.Run(400);
        Assert.True(fog.Explored(10, 30));
        Assert.False(fog.Visible(10, 30));
        Assert.True(fog.Visible(me.Pos.X.Floor(), me.Pos.Y.Floor()));
        Assert.True(w.FogMatchesRebuild());
    }

    // A 6 m ridge at cells 10-11 between a marksman at x 5 and a soldier at x 15 hides it; a 1 m
    // bank does not; the same ridge seen from a 12 m hill does not hide it.
    [Theory]
    [InlineData(6, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 0, true)]
    [InlineData(6, 12, true)]
    public void RidgesBlockGroundVision(int ridgeM, int hillM, bool seen)
    {
        var w = Fogged(40, (x, _) => x is >= 10 and <= 12 ? ridgeM * M : x <= 6 ? hillM * M : 0);
        var me = w.Spawn(1, "Marksman", 5, 20); // vision 14 cells
        var them = w.Spawn(2, "Rifleman", 15, 20);
        w.Run(1);
        Assert.Equal(seen, Sees(w, 1, them));
        Assert.Equal(seen, World.IsSpotted(them, 1));
        Assert.False(Sees(w, 2, me)); // the rifleman's 7 cells do not reach
        Assert.True(w.FogMatchesRebuild());
    }

    [Fact]
    public void LineOfSightAgreesWithTheTerrainReference()
    {
        // Random hills: wherever World.LineOfSight (the sampled reference) says the eye sees a
        // target's head even 1 m lower, the grid shows the cell, and where the line is blocked even
        // 1 m higher, it does not (the grid uses cell-centre heights: edges may differ in between).
        var rng = new Rng.Rand(7);
        for (var trial = 0; trial < 20; trial++)
        {
            var bumps = new List<(int X, int Y, int H)>();
            for (var i = 0; i < 6; i++)
            {
                bumps.Add((rng.Intn(40), rng.Intn(40), rng.Range(2, 10)));
            }
            int H(int x, int y)
            {
                var h = 0;
                foreach (var (bx, by, bh) in bumps)
                {
                    var d2 = (x - bx) * (x - bx) + (y - by) * (y - by);
                    h += Math.Max(0, bh * M - d2 * M / 40); // falls 0.9 m a cell at most (45% grade)
                }
                return Math.Min(h, 40 * M);
            }
            var w = Fogged(40, H);
            var me = w.Spawn(1, "Marksman", 20, 20);
            w.Run(1);
            var fog = w.FogOf(1)!;
            var eye = w.GroundHeight(new Cell(20, 20).Center()) + Fixed.FromRaw(Rules.Vision!.EyeFootRaw);
            for (var y = 6; y < 35; y++)
            {
                for (var x = 6; x < 35; x++)
                {
                    var c = new Cell(x, y);
                    if ((x - 20) * (x - 20) + (y - 20) * (y - 20) > 12 * 12)
                    {
                        continue;
                    }
                    var head = w.GroundHeight(c.Center()) + Fixed.FromRaw(Rules.Vision.TargetRaw);
                    var margin = Fixed.FromRaw(M); // 1 m: clear even 1 m lower, blocked even 1 m higher
                    var clear = w.LineOfSight(me.Pos, eye - margin, c.Center(), head - margin);
                    var blocked = !w.LineOfSight(me.Pos, eye + margin, c.Center(), head + margin);
                    if (clear)
                    {
                        Assert.True(fog.Visible(x, y), $"trial {trial}: clear line to {c} not seen");
                    }
                    else if (blocked)
                    {
                        Assert.False(fog.Visible(x, y), $"trial {trial}: hidden cell {c} seen");
                    }
                }
            }
        }
    }

    // A band of dense forest (cells 8-11) hides a soldier beyond it; scrub does not; a soldier
    // standing in the first cells of the wood is seen.
    [Theory]
    [InlineData(3, 15, false)]
    [InlineData(2, 15, false)]
    [InlineData(1, 15, true)]
    [InlineData(3, 9, true)]
    public void WoodsBlockGroundVision(int density, int at, bool seen)
    {
        var w = Fogged(40, forest: (x, _) => x is >= 8 and <= 11 ? density : 0);
        w.Spawn(1, "Marksman", 5, 20);
        var them = w.Spawn(2, "Rifleman", at, 20);
        w.Run(1);
        Assert.Equal(seen, Sees(w, 1, them));
    }

    // A drone sees over the ridge and the wood that blind a soldier standing where it flies.
    [Fact]
    public void AirUnitsSeeOverRidgesAndWoods()
    {
        var w = Fogged(40, (x, _) => x is >= 10 and <= 11 ? 8 * M : 0, (x, _) => x is >= 12 and <= 14 ? 3 : 0);
        var soldier = w.Spawn(1, "Marksman", 7, 20);
        var them = w.Spawn(2, "Rifleman", 16, 20);
        w.Run(1);
        Assert.False(Sees(w, 1, them));

        var drone = w.Spawn(1, "fpv_drone", 13, 20, Facing.East); // over the wood, 3 cells from it
        Assert.Equal(Layers.Air, drone.Def.Layer);
        drone.Lifetime = 1000;
        w.Run(1);
        Assert.True(Sees(w, 1, them)); // the drone's 4 cells reach it over the ridge and the wood
        Assert.True(soldier.Alive);

        // And an enemy drone behind the ridge is seen by the soldier: the sky is not hidden.
        var enemyDrone = w.Spawn(2, "fpv_drone", 13, 21, Facing.West);
        enemyDrone.Lifetime = 1000;
        w.Run(1);
        Assert.True(Sees(w, 1, enemyDrone));
    }

    // With fog, an ordered attack needs a spotter again: the launcher outranging its own vision
    // holds fire at an enemy nobody on its side spots (the order is refused), and fires once a
    // friendly soldier spots it.
    [Fact]
    public void OrderedAttacksNeedASpotterUnderFog()
    {
        var w = Fogged(48);
        var (l, _) = w.SpawnCrewed(1, "medium_rocket_launcher", 5, 30, Facing.East);
        var target = w.Spawn(2, "technical", 15, 30);
        target.HP = 100_000;
        w.Run(1);
        Assert.False(World.Within(l.Pos, target.Pos, l.Def.Vision));
        Assert.False(Sees(w, 1, target));

        w.Order(1, CommandKind.Attack, [l], target.Id);
        w.Run(100);
        Assert.Equal(100_000, target.HP);
        Assert.Equal(OrderKind.Idle, l.Order.Kind);

        var spotter = w.Spawn(1, "Rifleman", 12, 30);
        w.Run(1);
        Assert.True(World.IsSpotted(target, 1));
        w.Order(1, CommandKind.Attack, [l], target.Id);
        w.Run(200);
        Assert.True(target.HP < 100_000);
        Assert.True(spotter.Owner == 1);
    }

    // Shells hit what is there: a soldier in the fog under a salvo is hit all the same.
    [Fact]
    public void ShellsHitEnemiesInTheFog()
    {
        var w = Fogged(64);
        var (l, _) = w.SpawnCrewed(1, "medium_rocket_launcher", 5, 30, Facing.East);
        var hidden = w.Spawn(2, "Rifleman", 22, 30);
        hidden.HP = 100_000;
        w.Run(1);
        Assert.False(Sees(w, 1, hidden));
        w.Order(1, CommandKind.AttackGround, [l], cell: new Cell(22, 30));
        w.Run(300);
        Assert.True(hidden.HP < 100_000);
    }

    // Bot matches with fog on the hills: the same seed gives the same hashes, the incremental
    // grids always equal a rebuild from scratch, and fog changes the game (another hash).
    [Fact]
    public void FogMatchesAreDeterministicAndGridsStayExact()
    {
        var size = Replay.NewWorld(TestContent.Mvp, TestContent.Battle(5)).Grid.Width;
        var corners = new int[(size + 1) * (size + 1)];
        var flags = new byte[size * size];
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                corners[y * (size + 1) + x] = (x * 7 + y * 3) % 23 < 6 ? 4 * M : x % 17 * 60;
                if (x < size && y < size && (x / 4 + y / 5) % 7 == 0)
                {
                    flags[y * size + x] = (byte)(1 + (x + y) % 3);
                }
            }
        }
        TerrainMap Map() => new(size, size, (int[])corners.Clone(), (byte[])flags.Clone(), Rules);
        var setup = TestContent.Battle(5) with { Terrain = "fogtest.json", Fog = true };
        var (rec, hashes) = Replay.Record(TestContent.Mvp, setup, 600, Map());
        var (again, world) = rec.Play(TestContent.Mvp, Map());
        Assert.Equal(hashes, again);
        Assert.True(world.FogMatchesRebuild());
        Assert.NotEqual(setup.Digest(), (setup with { Fog = false }).Digest());

        var w = Replay.NewWorld(TestContent.Mvp, setup, Map());
        var bots = w.Players.Select(p => new Bot(p.Id, 99UL + p.Id)).ToList();
        for (var t = 0; t < 400; t++)
        {
            foreach (var b in bots)
            {
                foreach (var c in b.Think(w))
                {
                    w.Issue(c);
                }
            }
            w.Step();
            if (t % 50 == 0)
            {
                Assert.True(w.FogMatchesRebuild(), $"tick {w.Tick}: the incremental grid drifted");
            }
        }
    }

    // The skirmish AI sees only what its side spots: a far enemy hidden in the fog changes
    // nothing it does, and once spotted near its base it is attacked.
    [Fact]
    public void SkirmishAiDoesNotSeeThroughFog()
    {
        static (World W, SkirmishAi Ai) Game(bool extra)
        {
            var w = Fogged(96);
            w.Spawn(2, "supply_depot", 70, 70);
            for (var i = 0; i < 6; i++)
            {
                w.Spawn(2, "Rifleman", 72 + i, 76);
            }
            w.Spawn(1, "Rifleman", 10, 10);
            if (extra)
            {
                w.Spawn(1, "Rifleman", 40, 88); // far from the AI, in the fog
            }
            return (w, new SkirmishAi(2) { WaveEvery = 200 });
        }
        var (a, aiA) = Game(false);
        var (b, aiB) = Game(true);
        var hidden = b.Units[^1];
        for (var t = 0; t < 600; t++)
        {
            var ca = aiA.Think(a);
            var cb = aiB.Think(b);
            Assert.Equal(ca.Select(Text), cb.Select(Text));
            foreach (var c in ca)
            {
                a.Issue(c);
            }
            foreach (var c in cb)
            {
                b.Issue(c);
            }
            a.Step();
            b.Step();
            if ((hidden.SpottedBy & (1u << 2)) != 0)
            {
                break;
            }
        }

        // An enemy walks up to the base: spotted, it is attacked.
        var w2 = Game(false);
        var intruder = w2.W.Spawn(1, "Rifleman", 68, 77);
        intruder.HP = 100_000;
        var sawAttack = false;
        for (var t = 0; t < 200 && !sawAttack; t++)
        {
            foreach (var c in w2.Ai.Think(w2.W))
            {
                sawAttack |= c.Kind == CommandKind.Attack && c.Target == intruder.Id;
                w2.W.Issue(c);
            }
            w2.W.Step();
        }
        Assert.True(sawAttack);
    }

    private static string Text(Command c) => $"{c.Tick} {c.Kind} [{string.Join(' ', c.Units)}] {c.Cell} {c.Target}";
}
