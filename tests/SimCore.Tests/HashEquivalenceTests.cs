using SimCore.Bots;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

public class HashEquivalenceTests
{
    [Fact]
    public void BatchedRoundsEqualSerialWordsIncludingSignedBits()
    {
        var rng = new Random(42);
        var serial = new WordHash();
        var batch = new WordHash();
        for (var i = 0; i < 1000; i++)
        {
            var a = unchecked((ulong)rng.NextInt64(long.MinValue, long.MaxValue));
            var b = unchecked((ulong)rng.NextInt64(long.MinValue, long.MaxValue));
            var c = unchecked((ulong)rng.NextInt64(long.MinValue, long.MaxValue));
            var d = unchecked((ulong)rng.NextInt64(long.MinValue, long.MaxValue));
            serial.Put(a); serial.Put(b); serial.Put(c); serial.Put(d);
            batch.Put4(a, b, c, d);
            Assert.Equal(serial.Sum, batch.Sum);
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void MatchesOriginalEveryTickWithMixedState(int seed, bool terrain)
    {
        const int n = 128;
        var w = Arena.NewSentinel(n, (ulong)seed, s => s with { Fog = true });
        if (terrain)
        {
            var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
            var flags = new byte[n * n];
            for (var y = 70; y < 105; y++)
            {
                for (var x = 5; x < 110; x++) { flags[y * n + x] = 3; }
            }
            var river = WaterBody.River(Fixed.FromRaw(-200), [new Cell(0, 60), new Cell(n - 1, 60)], [3, 3]);
            var map = new TerrainMap(n, n, new int[(n + 1) * (n + 1)], flags, rules, [river]);
            map.AddBridgeSite(new BridgeSite("concrete", new Cell(30, 54), Facing.South));
            w.AttachTerrain(map);
            w.ClearTrees(new Cell(20, 80));
            Assert.True(w.IgniteCell(new Cell(40, 80), 100)); // forest fire: burning, burnt and the batch not yet applied
            Assert.True(w.IgniteCell(new Cell(90, 95), 0));
        }
        var rng = new Random(seed);
        for (var p = (byte)1; p <= 2; p++)
        {
            for (var i = 0; i < 60; i++)
            {
                var u = w.Spawn(p, "Rifleman", 5 + rng.Next(110), 10 + rng.Next(35));
                u.SetPath([new Cell(20, 20), new Cell(21, 20), new Cell(22, 21)]);
                u.PathIdx = i % 3;
                u.MutableCharges.AsSpan().Fill(i % 3);
                if (i % 3 == 0) { w.Order(p, CommandKind.AttackMove, [u], cell: new Cell(60, 28)); }
            }
            w.Spawn(p, "supply_depot", 4 + 90 * (p - 1), 5);
        }
        var house = w.Spawn(2, "block_brick_4", 48, 90);
        var turnedDef = w.Player(2)!.Setup.Find(w.Content, "block_brick_4")!;
        _ = w.Spawn(turnedDef, new Cell(30, 90), Facing.South, 1); // a building standing turned (Unit.Quarter)
        var occupant = w.Spawn(2, "Rifleman", 54, 98);
        w.Order(2, CommandKind.Enter, [occupant], target: house.Id);
        var tunnel = w.Spawn(2, "tunnel_hatch", 105, 108);
        w.Spawn(2, "tunnel_hatch", 78, 108);
        var passenger = w.Spawn(2, "Rifleman", 106, 111);
        w.Order(2, CommandKind.Enter, [passenger], target: tunnel.Id);
        w.SpawnCrewed(1, "main_battle_tank", 75, 15);
        w.Spawn(1, "attack_helicopter", 50, 30);
        w.Spawn(1, "overseer_drone", 65, 25);
        var bots = w.Players.Select(p => new Bot(p.Id, (ulong)(seed + p.Id)) { Every = 20 }).ToList();
        for (var tick = 0; tick <= 250; tick++)
        {
            Assert.Equal(w.HashReference(), w.Hash());
            foreach (var bot in bots) { foreach (var command in bot.Think(w)) { w.Issue(command); } }
            w.Step();
        }
    }

    [Fact]
    public void OriginalBattleMatchesForMoreThanTwoHundredTicks()
    {
        var replay = Replay.Load(TestPaths.TestData("battle_seed42.json"));
        var w = replay.Start(TestContent.Mvp);
        foreach (var c in replay.Commands) { w.Issue(c); }
        for (var tick = 0; tick <= 300; tick++)
        {
            Assert.Equal(w.HashReference(), w.Hash());
            w.Step();
        }
    }
}
