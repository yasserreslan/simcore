using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The helicopters' ceiling (2026-11-02, the owner: "helicopters stay limited in height but still can climb up to a certain
// point", for high mountain maps; docs/design.md, "Helicopters", "Ceiling"): every helicopter flies at most 400 m over the
// map's lowest ground; ground higher than that less its 5 m hide height is a wall it routes round (valleys, passes); a point
// beyond such ground takes it to the nearest point it can reach. Maps with no ground that high fly exactly as before.
public class HeliCeilingTests(ITestOutputHelper output)
{
    private const string Apache = "attack_helicopter", Havoc = "armored_helicopter", Bird = "light_helicopter";
    private const long Metre = 512;
    private const int N = 160;

    // The valley floor at the map's datum (0 m): the ceiling is 400 m absolute and the mountains rise above it (the terrain allows
    // ±1,024 m since 2026-11-02, decision 886).
    private const int Floor = 0;

    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static World Sentinel() => Arena.NewSentinel(N, 1, null);

    private static Unit Fly(World w, string id, int x, int y)
    {
        var u = w.Spawn(1, id, x, y, Facing.East);
        w.Step();
        return u;
    }

    private static void Move(World w, Unit u, Cell to) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.Move, Units = [u.Id], Cell = to });

    // A map at the valley floor with height(x, y) metres over it (corner heights, absolute = Floor + that).
    private static TerrainMap Map(Func<int, int, int> height)
    {
        var corners = new int[(N + 1) * (N + 1)];
        for (var y = 0; y <= N; y++)
        {
            for (var x = 0; x <= N; x++)
            {
                corners[y * (N + 1) + x] = (Floor + height(x, y)) * (int)Metre;
            }
        }
        return new TerrainMap(N, N, corners, new byte[N * N], Rules);
    }

    // A cone: `top` metres over the floor at (cx, cy), down to the floor `radius` cells out.
    private static Func<int, int, int> Peak(int cx, int cy, int radius, int top) => (x, y) =>
    {
        var d = (int)Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        return d >= radius ? 0 : top * (radius - d) / radius;
    };

    // A ridge across x = rx, `half` cells either side, `top` metres high, but only `pass` metres high for y in [p0, p1].
    private static Func<int, int, int> RidgeWithPass(int rx, int half, int top, int pass, int p0, int p1) => (x, y) =>
    {
        var d = Math.Abs(x - rx);
        var h = y >= p0 && y <= p1 ? pass : top;
        return d >= half ? 0 : h * (half - d) / half;
    };

    private sealed record Flight(bool Arrived, int Ticks, long MaxGround, long MaxAlt, long MinAgl, bool EnteredWall, bool Routed, List<Vec> Track);

    // Flies u to `to` for at most `ticks`, checking every tick that it is under its ceiling and outside any wall cell.
    private static Flight FlyTo(World w, Unit u, Cell to, int ticks)
    {
        Move(w, u, to);
        w.Step();
        var ceiling = w.HeliCeilingOf(u);
        long maxGround = long.MinValue, maxAlt = long.MinValue, minAgl = long.MaxValue;
        var (wall, routed) = (false, false);
        var track = new List<Vec>();
        var t = 0;
        for (; t < ticks && u.Order.Kind == OrderKind.Move; t++)
        {
            w.Step();
            var ground = w.GroundHeight(u.Pos).Raw;
            (maxGround, maxAlt, minAgl) = (Math.Max(maxGround, ground), Math.Max(maxAlt, u.Altitude.Raw), Math.Min(minAgl, u.Altitude.Raw - ground));
            wall |= w.HeliWallFor(u, Cell.Of(u.Pos));
            routed |= u.Heli!.RouteAhead.Count > 0;
            track.Add(u.Pos);
            Assert.True(u.Altitude.Raw <= ceiling, $"tick {t}: {u.Altitude.Raw} over its ceiling {ceiling}");
        }
        return new Flight(u.Order.Kind == OrderKind.Idle, t, maxGround, maxAlt, minAgl, wall, routed, track);
    }

    private static string M(long raw) => (raw / (double)Metre).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void EveryHelicopterHasA400MetreCeiling()
    {
        var w = Sentinel();
        w.AttachTerrain(Map((_, _) => 0));
        foreach (var id in new[] { Apache, Havoc, Bird })
        {
            Assert.Equal(400 * Metre, w.Content.Archetype(id)!.Flight!.Rotor!.CeilingRaw);
            var u = Fly(w, id, 20, 20);
            Assert.Equal((Floor + 400) * Metre, w.HeliCeilingOf(u)); // over the map's lowest ground
        }
        // Fixed wings and drones have none (unchanged).
        foreach (var id in new[] { "multirole_jet", "watcher_drone", "scout_drone" })
        {
            Assert.Null(w.Content.Archetype(id)!.Flight!.Rotor);
        }
    }

    [Fact]
    public void ItClimbsASlopeBelowTheCeiling()
    {
        // A hill 385 m over the floor: under the 395 m wall height, so it crosses it and may hover over its top, where at its
        // 30 m flight height it would be at about 410 m: it holds the 400 m ceiling there instead, about 20 m over the crest.
        var w = Sentinel();
        w.AttachTerrain(Map(Peak(80, 80, 75, 385)));
        var u = Fly(w, Apache, 10, 80);
        var f = FlyTo(w, u, new Cell(80, 80), 1200);
        Assert.True(f.Arrived);
        Assert.False(f.EnteredWall);
        Assert.False(f.Routed, "nothing in its way is a wall: straight up the slope");
        w.Run(200);
        var agl = u.Altitude.Raw - w.GroundHeight(u.Pos).Raw;
        output.WriteLine($"over the top: ground {M(w.GroundHeight(u.Pos).Raw - Floor * Metre)} m over the floor, flying {M(u.Altitude.Raw - Floor * Metre)} m, {M(agl)} m over the ground");
        Assert.Equal((Floor + 400) * Metre, u.Altitude.Raw); // at its ceiling
        Assert.InRange(agl, 5 * Metre, 30 * Metre - 1);
        // On over the top and down the far side, straight.
        var g = FlyTo(w, u, new Cell(150, 80), 1200);
        Assert.True(g.Arrived);
        Assert.False(g.EnteredWall || g.Routed);
        Assert.True(g.Track.All(p => Math.Abs(p.Y.Raw - new Cell(0, 80).Center().Y.Raw) <= Fixed.OneRaw), "straight across");
    }

    [Theory]
    [InlineData(Apache)]
    [InlineData(Havoc)]
    [InlineData(Bird)]
    public void ItFliesRoundAPeakAboveTheCeiling(string id)
    {
        // A peak 450 m over the floor, 40 cells round: above the 395 m wall height from its top down to 395 m (the inner 4.9
        // cells of its 40); it flies round that core, never into it.
        var w = Sentinel();
        w.AttachTerrain(Map(Peak(80, 80, 40, 450)));
        var u = Fly(w, id, 20, 80);
        var f = FlyTo(w, u, new Cell(140, 80), 1600);
        var off = f.Track.Max(p => Math.Abs(p.Y.Raw - new Cell(80, 80).Center().Y.Raw)) / Fixed.OneRaw;
        output.WriteLine($"{id}: {f.Ticks} ticks, {off} cells off the straight line, ground up to {M(f.MaxGround - Floor * Metre)} m over the floor");
        Assert.True(f.Arrived);
        Assert.False(f.EnteredWall);
        Assert.True(f.Routed, "it planned a way round");
        Assert.InRange(off, 4, 20);
        Assert.True(f.MaxGround - Floor * Metre <= 395 * Metre);
        Assert.Empty(u.Heli!.RouteAhead); // arrived: no route left
    }

    [Fact]
    public void ItCrossesThroughAPass()
    {
        // A ridge 450 m over the floor across x = 80 (the whole map's height), but only 300 m in a pass from y = 110 to 130.
        // From (20, 30) to (140, 30) it goes up the valley, through the pass and back down.
        var w = Sentinel();
        w.AttachTerrain(Map(RidgeWithPass(80, 30, 450, 300, 110, 130)));
        var u = Fly(w, Apache, 20, 30);
        var f = FlyTo(w, u, new Cell(140, 30), 3000);
        var crossing = f.Track.First(p => p.X.Raw >= new Cell(80, 0).Center().X.Raw);
        output.WriteLine($"{f.Ticks} ticks; crossed x = 80 at y = {crossing.Y.Raw / Fixed.OneRaw}");
        Assert.True(f.Arrived);
        Assert.False(f.EnteredWall);
        Assert.InRange(crossing.Y.Raw / Fixed.OneRaw, 110, 130);
    }

    [Fact]
    public void APointBeyondItsReachTakesItToTheNearestPointItCanReach()
    {
        // The goal is the peak's top (a wall): it ends at the edge of the wall, the order done.
        var w = Sentinel();
        w.AttachTerrain(Map(Peak(80, 80, 40, 450)));
        var u = Fly(w, Apache, 20, 80);
        var f = FlyTo(w, u, new Cell(80, 80), 1200);
        var left = (u.Pos - new Cell(80, 80).Center()).Len().Raw;
        output.WriteLine($"{f.Ticks} ticks; stopped {left / (double)Fixed.OneRaw:F1} cells from the top");
        Assert.True(f.Arrived);
        Assert.False(f.EnteredWall);
        Assert.InRange(left, 4 * Fixed.OneRaw, 8 * Fixed.OneRaw);
        // It hovers there (not flying on, not re-planning into the wall).
        var at = u.Pos;
        w.Run(200);
        Assert.False(w.HeliWallFor(u, Cell.Of(u.Pos)));
        Assert.True((u.Pos - at).Len().Raw <= 2 * Fixed.OneRaw);
    }

    [Fact]
    public void ItIsDeterministic()
    {
        static (ulong, Vec) Run()
        {
            var w = Sentinel();
            w.AttachTerrain(Map(RidgeWithPass(80, 30, 450, 300, 110, 130)));
            var a = Fly(w, Apache, 20, 30);
            var b = Fly(w, Bird, 25, 40);
            Move(w, a, new Cell(140, 30));
            Move(w, b, new Cell(140, 60));
            w.Run(1500);
            return (w.Hash(), a.Pos + b.Pos);
        }
        Assert.Equal(Run(), Run());
    }

    // The content without the ceiling (as before 2026-11-02).
    private static readonly Lazy<ContentSet> NoCeiling = new(() => ContentLoader.Load(TestContent.MvpSources().Select(x =>
        x.Name == "archetypes/sentinel.json" ? x with { Json = x.Json.Replace("\"ceiling\": 4000, ", "", StringComparison.Ordinal) } : x)));

    [Fact]
    public void OnMapsWithNothingThatHighItFliesAsBefore()
    {
        // A 250 m mountain over the map's lowest ground: nothing near the 395 m wall height. With the ceiling and without it,
        // every helicopter flies the same, tick by tick (and the hash differs only by the content's).
        static List<(Vec, long, int)> Run(ContentSet content)
        {
            var setup = Replays.Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(content, 1);
            setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
            var w = new World(new Grid(N, N), MatchCompiler.Compile(content, setup));
            w.AttachTerrain(Map(Peak(80, 80, 40, 250)));
            var units = new[] { Fly(w, Apache, 20, 80), Fly(w, Havoc, 20, 70), Fly(w, Bird, 20, 90) };
            foreach (var u in units)
            {
                Assert.DoesNotContain(Enumerable.Range(0, N), x => w.HeliWallFor(u, new Cell(x, 80)));
                Move(w, u, new Cell(140, 82));
            }
            var trace = new List<(Vec, long, int)>();
            for (var i = 0; i < 600; i++)
            {
                w.Step();
                foreach (var u in units)
                {
                    Assert.Empty(u.Heli!.RouteAhead);
                    trace.Add((u.Pos, u.Altitude.Raw, u.Facing));
                }
            }
            return trace;
        }
        Assert.NotEqual(0, TestContent.Mvp.Archetype(Apache)!.Flight!.Rotor!.CeilingRaw);
        Assert.Equal(0, NoCeiling.Value.Archetype(Apache)!.Flight!.Rotor!.CeilingRaw);
        Assert.Equal(Run(NoCeiling.Value), Run(TestContent.Mvp));
    }
}
