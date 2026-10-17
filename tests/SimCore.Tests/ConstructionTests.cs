using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// Construction: a worker places a supply depot site (6x6, 600 ticks with one worker) and builds
// it. The site starts at 10% HP, is not a depot until finished, rises with the work; a second
// worker adds half a worker, a third nothing; leaving pauses it, any worker resumes it; cancel
// removes it. A worker-built depot starts empty. On terrain the site's ground becomes a pad.
public class ConstructionTests
{
    private const int BuildTime = 600;

    private static void Build(World w, IEnumerable<Unit> workers, Cell at, string unit = "supply_depot", int facing = 0, byte player = 1) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.Build, Units = [.. workers.Select(u => u.Id)], Cell = at, Unit = unit, Facing = facing });

    private static Unit? Site(World w) => w.Units.FirstOrDefault(u => u.Def.Archetype.Id == "supply_depot" && !u.IsBuilt);

    // Ticks until the site is finished, counting from now.
    private static int RunToFinish(World w, Unit site, int max = 2000)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            if (site.IsBuilt)
            {
                return t;
            }
        }
        return -1;
    }

    // Builders standing right at the site's edge, so the count starts at once.
    private static (World W, List<Unit> Workers) Yard(int workers)
    {
        var w = Arena.New(48);
        var list = Enumerable.Range(0, workers).Select(i => w.Spawn(1, "worker", 19, 20 + i)).ToList();
        return (w, list);
    }

    [Fact]
    public void ASiteStartsAtTenPercentAndIsNotADepotYet()
    {
        var (w, workers) = Yard(1);
        var (truck, _) = w.SpawnCrewed(1, "missile_truck", 30, 22, Facing.East);
        truck.MutableCharges[0] = 0;
        Build(w, workers, new Cell(20, 20));
        w.Step();
        var site = Assert.IsType<Unit>(Site(w));
        Assert.Equal(ConstructionState.Building, site.Construction);
        Assert.Equal(site.Def.MaxHp / 10 + site.Def.MaxHp * 9 / 10 * site.Work / (BuildTime * 100), site.HP);
        Assert.Equal(0, site.Stock);
        Assert.Equal(new Cell(20, 20), World.FootprintOrigin(site));
        Assert.Equal((6, 6), (site.Def.Archetype.FootprintW, site.Def.Archetype.FootprintH));
        w.Run(400);
        Assert.Equal(0, truck.Charges[0]); // no reload from a site
    }

    [Fact]
    public void OneWorkerTakesTheBuildTimeASecondAddsHalfAThirdNothing()
    {
        foreach (var (n, perTick) in new[] { (1, 100), (2, 150), (3, 150) })
        {
            var (w, workers) = Yard(n);
            Build(w, workers, new Cell(20, 20));
            w.Run(5);
            var site = Site(w)!;
            var before = site.Work;
            w.Step();
            Assert.Equal(perTick, site.Work - before);
            // Work only ever adds up to BuildTime x 100: one worker finishes in BuildTime ticks of work.
            Assert.InRange(RunToFinish(w, site), (BuildTime * 100 - before - perTick) / perTick, (BuildTime * 100 - before - perTick) / perTick + 1);
            Assert.True(site.HP == site.Def.MaxHp);
            Assert.All(workers, u => Assert.Equal(OrderKind.Idle, u.Order.Kind));
        }
    }

    [Fact]
    public void LeavingPausesAndAnyWorkerResumes()
    {
        var (w, workers) = Yard(1);
        Build(w, workers, new Cell(20, 20));
        w.Run(100);
        var site = Site(w)!;
        w.Order(1, CommandKind.Move, workers, cell: new Cell(5, 40));
        w.Run(5);
        var paused = site.Work;
        w.Run(100);
        Assert.Equal(paused, site.Work);
        Assert.False(site.IsBuilt);

        var other = w.Spawn(1, "worker", 27, 22);
        w.Order(1, CommandKind.Resume, [other], site.Id);
        Assert.True(RunToFinish(w, site) > 0);
    }

    [Fact]
    public void HpRisesWithTheWorkAndDamageStays()
    {
        var (w, workers) = Yard(1);
        Build(w, workers, new Cell(20, 20));
        w.Run(2);
        var site = Site(w)!;
        var last = site.HP;
        for (var i = 0; i < 100; i++)
        {
            w.Step();
            Assert.True(site.HP >= last);
            last = site.HP;
        }
        site.HP -= 50;
        RunToFinish(w, site);
        Assert.Equal(site.Def.MaxHp - 50, site.HP);
    }

    [Fact]
    public void StagesBeginAtFifteenFortySeventyNinetyFive()
    {
        var (w, workers) = Yard(1);
        Build(w, workers, new Cell(20, 20));
        w.Step();
        var site = Site(w)!;
        foreach (var (pct, stage) in new[] { (0, 0), (14, 0), (15, 1), (39, 1), (40, 2), (70, 3), (94, 3), (95, 4) })
        {
            site.Work = pct * BuildTime;
            Assert.Equal(stage, World.ConstructionStage(site));
        }
    }

    [Fact]
    public void AWorkerBuiltDepotStartsEmptyAScenarioOneFull()
    {
        var (w, workers) = Yard(1);
        var placed = w.Spawn(1, "supply_depot", 2, 2);
        Assert.Equal(1000, placed.Stock);
        Build(w, workers, new Cell(20, 20));
        w.Step();
        var site = Site(w)!;
        RunToFinish(w, site);
        Assert.True(site.IsBuilt);
        Assert.Equal(0, site.Stock);
    }

    [Fact]
    public void BadPlacesAndBadBuildersAreRefused()
    {
        var w = Arena.New(48);
        var worker = w.Spawn(1, "worker", 10, 10);
        var soldier = w.Spawn(1, "Rifleman", 10, 12);
        w.Grid.Set(new Cell(32, 32), Terrain.Blocked);
        w.Spawn(2, "Rifleman", 22, 33);
        w.Spawn(1, "supply_depot", 2, 30);
        Build(w, [worker], new Cell(45, 10));         // off the map's edge
        Build(w, [worker], new Cell(30, 30));         // a rock
        Build(w, [worker], new Cell(20, 30));         // an enemy soldier inside
        Build(w, [worker], new Cell(4, 32));          // overlaps the depot
        Build(w, [soldier], new Cell(14, 14));        // not a worker
        Build(w, [worker], new Cell(14, 14), "block_brick_4"); // not buildable
        Build(w, [worker], new Cell(14, 14), facing: 4);       // not a quarter turn
        Build(w, [worker], new Cell(14, 14), player: 2);       // not the player's worker
        w.Step();
        Assert.Null(Site(w));
        Build(w, [worker], new Cell(14, 14), facing: 8); // a quarter turn of a square footprint is fine
        w.Step();
        Assert.NotNull(Site(w));
    }

    [Fact]
    public void CancelRemovesTheSiteOnlyForItsOwner()
    {
        var (w, workers) = Yard(1);
        Build(w, workers, new Cell(20, 20));
        w.Run(50);
        var site = Site(w)!;
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.CancelBuild, Target = site.Id });
        w.Step();
        Assert.NotNull(w.Unit(site.Id));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.CancelBuild, Target = site.Id });
        w.Step();
        Assert.Null(w.Unit(site.Id));
        Assert.Equal(OrderKind.Idle, workers[0].Order.Kind);
        // The ground is free again.
        Build(w, workers, new Cell(20, 20));
        w.Step();
        Assert.NotNull(Site(w));
    }

    [Fact]
    public void OnTerrainTheSiteIsAFlatPadOnGentleGroundOnly()
    {
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        // A gentle ramp west of x = 30 (5%), a steep one east of it.
        var n = 48;
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = x < 30 ? x * 51 : 30 * 51 + (x - 30) * 600;
            }
        }
        var w = Arena.New(n);
        var flags = new byte[n * n];
        flags[14 * n + 14] = 2; // a grove tree where the site goes
        w.AttachTerrain(new TerrainMap(n, n, corners, flags, rules));
        var worker = w.Spawn(1, "worker", 10, 10);
        Build(w, [worker], new Cell(36, 10)); // steep
        w.Step();
        Assert.Null(Site(w));
        Build(w, [worker], new Cell(12, 12)); // gentle
        w.Step();
        Assert.NotNull(Site(w));
        var g = w.Ground!;
        var h = g.Corner(12, 12);
        for (var y = 12; y <= 18; y++)
        {
            for (var x = 12; x <= 18; x++)
            {
                Assert.Equal(h, g.Corner(x, y));
            }
        }
        Assert.NotEqual(g.Corner(10, 12), g.Corner(21, 12)); // the ground around still slopes
        Assert.Equal(0, g.Forest(new Cell(14, 14)));          // and the trees on it are cleared
    }

    [Fact]
    public void TheSameCommandsGiveTheSameHashes()
    {
        ulong[] Run()
        {
            var (w, workers) = Yard(2);
            Build(w, workers, new Cell(20, 20));
            var hashes = new ulong[500];
            for (var i = 0; i < hashes.Length; i++)
            {
                if (i == 200)
                {
                    w.Order(1, CommandKind.Move, [workers[1]], cell: new Cell(5, 40));
                }
                w.Step();
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }
}
