using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The observation post: built by workers, its builder's for good, a garrison of 3 that fires
// out, occupants surviving its destruction at 40% HP, a big sight radius that grows on high
// ground (and sees over a ridge from there under fog), and the sniper launch-point hook.
public class ObservationPostTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int M = 512; // raw per metre

    private static World Terrain(int size, Func<int, int, int> height, bool fog = false)
    {
        var w = Arena.New(size, edit: s => s with { Fog = fog });
        var corners = new int[(size + 1) * (size + 1)];
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                corners[y * (size + 1) + x] = height(x, y);
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, corners, new byte[size * size], Rules));
        return w;
    }

    // A round knoll of the given height at (cx, cy): flat top 3 cells across, falling to 0 over 6 cells.
    private static Func<int, int, int> Knoll(int cx, int cy, int metres) => (x, y) =>
    {
        var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)); // test-side geometry only
        return (int)(metres * M * Math.Clamp((9 - d) / 6, 0, 1));
    };

    [Fact]
    public void WorkersBuildItAndItStaysTheirs()
    {
        var w = Arena.New(48);
        var workers = Enumerable.Range(0, 2).Select(i => w.Spawn(1, "worker", 18, 20 + i)).ToList();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [.. workers.Select(u => u.Id)], Cell = new Cell(20, 20), Unit = "observation_post" });
        w.Run(2);
        var post = Assert.Single(w.All("observation_post"));
        Assert.False(post.IsBuilt);
        var early = w.Spawn(1, "Rifleman", 22, 26);
        w.Order(1, CommandKind.Enter, [early], post.Id);
        w.Run(20);
        Assert.Equal(0u, early.Garrison); // a site holds nobody
        w.Run(600);
        Assert.True(post.IsBuilt);
        Assert.Equal(1, post.Owner);

        // Empty, it stays the builder's; the enemy can't get in, its own side can (3 at most).
        var enemy = w.Spawn(2, "Rifleman", 22, 30);
        w.Order(2, CommandKind.Enter, [enemy], post.Id);
        var squad = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 20 + i, 27)).ToList();
        w.Order(1, CommandKind.Enter, squad, post.Id);
        w.Run(200);
        Assert.Equal(0u, enemy.Garrison);
        Assert.Equal(3, squad.Count(u => u.Garrison == post.Id));
        Assert.Equal(1, post.Owner);
        w.Order(1, CommandKind.Exit, [post], cell: new Cell(-1, -1));
        w.Run(5);
        Assert.Equal(0, post.Occupants);
        Assert.Equal(1, post.Owner); // never neutral
    }

    [Fact]
    public void OccupantsFireOutAndSurviveItsDestructionAtFortyPercent()
    {
        var w = Arena.New(48);
        var post = w.Spawn(1, "observation_post", 20, 20);
        Assert.Equal(1, post.Owner);
        var inside = Enumerable.Range(0, 3).Select(i => w.Spawn(1, "Rifleman", 21 + i, 25)).ToList();
        inside[2].HP = 10; // already badly hurt: keeps its 10
        w.Order(1, CommandKind.Enter, inside, post.Id);
        w.Run(100);
        Assert.All(inside, u => Assert.Equal(post.Id, u.Garrison));

        var target = w.Spawn(2, "Rifleman", 22, 29);
        target.HP = 100_000;
        w.Run(100);
        Assert.True(target.HP < 100_000, "the garrison did not fire out");
        target.HP = 0; // gone, so nobody shoots the survivors as they come out
        w.Run(1);
        Assert.All(inside, u => Assert.True(u.HP == u.Def.MaxHp || u == inside[2]));

        post.HP = 0;
        w.Run(2);
        Assert.All(inside, u => Assert.True(u.Alive && u.Garrison == 0));
        var max = inside[0].Def.MaxHp;
        Assert.Equal(max * 40 / 100, inside[0].HP);
        Assert.Equal(max * 40 / 100, inside[1].HP);
        Assert.Equal(10, inside[2].HP);
    }

    [Fact]
    public void HighGroundWidensItsSight()
    {
        var flat = Arena.New(64);
        var onFlat = flat.Spawn(1, "observation_post", 30, 30);
        Assert.Equal(0, flat.HighGroundPercent(onFlat));
        Assert.Equal(16 * Fixed.OneRaw, flat.SightRange(onFlat).Raw);

        static int Percent(int metres)
        {
            var w = Terrain(64, Knoll(32, 32, metres));
            var post = w.Spawn(1, "observation_post", 30, 30); // its centre (32.5, 32) on the knoll top
            return w.HighGroundPercent(post);
        }
        var low = Percent(3);
        var mid = Percent(6);
        var high = Percent(10);
        Assert.InRange(mid, 25, 32); // about +30% on a 6 m knoll (5% a metre above the ground around)
        Assert.True(low < mid && mid < high, $"{low} {mid} {high}");
        Assert.Equal(60, Percent(25)); // capped

        // A rifleman gets nothing from the same knoll (no high-ground rule).
        var hill = Terrain(64, Knoll(32, 32, 11));
        var soldier = hill.Spawn(1, "Rifleman", 32, 32);
        Assert.Equal(soldier.Def.Vision, hill.SightRange(soldier));
    }

    // Under fog: a post on an 11 m knoll spots a soldier 19 cells away that the same post on flat
    // ground does not, and sees over a 3 m bank that hides him from a post at its foot.
    [Fact]
    public void OnAHillItSpotsFartherAndOverABank()
    {
        static bool Spots(Func<int, int, int> h)
        {
            var w = Terrain(80, h, fog: true);
            w.Spawn(1, "observation_post", 30, 38);
            var them = w.Spawn(2, "Rifleman", 51, 40);
            w.Run(1);
            return World.IsVisibleTo(them, 1);
        }
        Assert.False(Spots((_, _) => 0));
        Assert.True(Spots(Knoll(32, 40, 11)));

        static bool OverBank(int metres)
        {
            int H(int x, int y) => Knoll(32, 40, metres)(x, y) + (x is >= 40 and <= 41 ? 3 * M : 0);
            var w = Terrain(80, H, fog: true);
            w.Spawn(1, "observation_post", 30, 38);
            var them = w.Spawn(2, "Rifleman", 44, 40); // just behind the bank
            w.Run(1);
            return World.IsVisibleTo(them, 1);
        }
        Assert.False(OverBank(0));
        Assert.True(OverBank(11));
    }

    // The scout-drone hook: with a sniper inside, the post is a launch point.
    [Fact]
    public void ASniperInsideMakesItALaunchPoint()
    {
        var w = Arena.New(48);
        var post = w.Spawn(1, "observation_post", 20, 20);
        var rifleman = w.Spawn(1, "Rifleman", 21, 25);
        w.Order(1, CommandKind.Enter, [rifleman], post.Id);
        w.Run(60);
        Assert.Equal(post.Id, rifleman.Garrison);
        Assert.Null(w.PostLauncher(post));
        var sniper = w.Spawn(1, "Marksman", 23, 25);
        w.Order(1, CommandKind.Enter, [sniper], post.Id);
        w.Run(60);
        Assert.Same(sniper, w.PostLauncher(post));
        Assert.True(post.Def.Tags.HasAll(w.Content.TagsOf("stealth")));
    }

    // The sniper launches the scout drone from the post: nothing without a sniper inside, one drone
    // from the post with one, and only one aloft at a time.
    [Fact]
    public void ASniperInsideLaunchesTheScoutDroneFromIt()
    {
        var w = Arena.New(160);
        var post = w.Spawn(1, "observation_post", 40, 40);
        var rifleman = w.Spawn(1, "Rifleman", 41, 36);
        w.Order(1, CommandKind.Enter, [rifleman], post.Id);
        w.Run(60);
        Assert.Equal(post.Id, rifleman.Garrison);
        w.Order(1, CommandKind.UseAbility, [post], cell: new Cell(120, 120), ability: "scout_station_launch");
        w.Run(5);
        Assert.Empty(w.All("scout_drone"));

        var sniper = w.Spawn(1, "Marksman", 43, 36);
        w.Order(1, CommandKind.Enter, [sniper], post.Id);
        w.Run(60);
        Assert.Same(sniper, w.PostLauncher(post));
        w.Order(1, CommandKind.UseAbility, [post], cell: new Cell(120, 120), ability: "scout_station_launch");
        w.Run(5);
        var drone = Assert.Single(w.All("scout_drone"));
        Assert.Equal(post.Id, drone.Source);
        Assert.Equal(1, drone.Owner);

        w.Order(1, CommandKind.UseAbility, [post], cell: new Cell(100, 140), ability: "scout_station_launch");
        w.Run(5);
        Assert.Single(w.All("scout_drone")); // one aloft at a time
    }
}
