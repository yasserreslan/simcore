using SimCore.Bots;
using SimCore.Content;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

public class PerceptionEquivalenceTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void DenseBattlesMatchOriginalEveryTick(int seed, bool fog)
    {
        var a = Build(seed, fog, legacy: false);
        var b = Build(seed, fog, legacy: true);
        var botsA = a.Players.Select(p => new Bot(p.Id, (ulong)(seed + p.Id)) { Every = 40 }).ToArray();
        var botsB = b.Players.Select(p => new Bot(p.Id, (ulong)(seed + p.Id)) { Every = 40 }).ToArray();
        Assert.True(a.Units.Count >= 2000);
        for (var tick = 0; tick < 200; tick++)
        {
            for (var i = 0; i < botsA.Length; i++)
            {
                var ca = botsA[i].Think(a);
                var cb = botsB[i].Think(b);
                Assert.Equal(System.Text.Json.JsonSerializer.Serialize(ca), System.Text.Json.JsonSerializer.Serialize(cb));
                foreach (var command in ca) { a.Issue(command); }
                foreach (var command in cb) { b.Issue(command); }
            }
            a.Step(); b.Step();
            Assert.True(a.Hash() == b.Hash(), $"seed {seed}, fog {fog}, tick {a.Tick}");
        }
    }

    private static World Build(int seed, bool fog, bool legacy)
    {
        const int n = 160;
        var w = Arena.NewSentinel(n, (ulong)seed, s => s with { Fog = fog });
        w.LegacyPerception = legacy;
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 30; y < 125; y++)
        {
            for (var x = 65; x < 95; x++)
            {
                corners[y * (n + 1) + x] = Math.Max(0, 15 - Math.Abs(x - 80)) * 100;
            }
        }
        var flags = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                if (x / 20 % 2 == 1 && y / 20 % 2 == 1) { flags[y * n + x] = 2; }
            }
        }
        w.AttachTerrain(new TerrainMap(n, n, corners, flags, rules));
        var rng = new Random(seed);
        for (var i = 0; i < 2000; i++)
        {
            var p = (byte)(1 + i % 2);
            if (i % 20 is 0 or 1)
            {
                w.SpawnCrewed(p, p == 1 ? "humvee" : "technical", 4 + rng.Next(150), 4 + rng.Next(150));
            }
            else { w.Spawn(p, p == 2 && i % 11 == 0 ? "Marksman" : "Rifleman", 4 + rng.Next(150), 4 + rng.Next(150)); }
        }
        var house = w.Spawn(2, "block_brick_4", 35, 70);
        var soldier = w.Spawn(2, "Rifleman", 40, 77);
        w.Order(2, CommandKind.Enter, [soldier], target: house.Id);
        w.Spawn(1, "overseer_drone", 60, 45);
        w.Spawn(1, "attack_helicopter", 45, 50);
        return w;
    }

    [Fact]
    public void VisibilityPruningExpiresWithTheIndexWindowAndDoesNotChangeOtherQueries()
    {
        var w = Arena.New(64);
        var target = w.Spawn(2, "Rifleman", 12, 12);
        var index = new SpatialIndex();
        var units = w.Units.ToList();
        var found = new List<Unit>();
        index.Build(units, w.Grid);
        index.QueryVision(0, 0, 32 * 1024, 32 * 1024, 1, 100, false, found);
        Assert.Contains(target, found);
        target.SpotUntil[1] = 100;
        index.QueryVision(0, 0, 32 * 1024, 32 * 1024, 1, 100, false, found);
        Assert.Empty(found);
        index.QueryOthers(0, 0, 32 * 1024, 32 * 1024, 1, false, found);
        Assert.Contains(target, found);
        index.Build(units, w.Grid);
        index.QueryVision(0, 0, 32 * 1024, 32 * 1024, 1, 101, false, found);
        Assert.Contains(target, found);
        index.Build(units, w.Grid);
        index.QueryVision(0, 0, 32 * 1024, 32 * 1024, 1, 101, true, found);
        Assert.Empty(found); // ground spotting is handled by fog, not this air-only pass
        index.QueryVision(0, 0, 32 * 1024, 32 * 1024, 1, 101, false, found);
        Assert.Contains(target, found); // an air viewer with canopy sensors, same side and window: its own list
    }
}
