using System.Globalization;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

public class ReplayTests
{
    private const string GoldenReplayFile = "skirmish_seed42.json";
    private const string GoldenHashesFile = "skirmish_seed42.hashes";
    private const uint MatchTicks = 10 * 60 * World.TicksPerSecond; // the 10-minute gate
    private const int CheckEvery = 30 * World.TicksPerSecond;

    // ReplayMatchesLiveMatch is the build-order gate for the sim core: a
    // recorded 10-minute match, saved and loaded, replays to the same hash on
    // every tick, twice in a row.
    [Fact]
    public void ReplayMatchesLiveMatch()
    {
        var (rec, live) = Replay.Record("skirmish", 7, MatchTicks);
        using var buf = new MemoryStream();
        rec.Encode(buf);
        buf.Position = 0;
        var loaded = Replay.Decode(buf);
        for (var run = 1; run <= 2; run++)
        {
            var (replayed, _) = loaded.Play();
            CompareHashes($"replay run {run}", live, replayed);
        }
    }

    // GoldenReplay replays a committed match and checks its hashes against
    // committed values. CI runs it on Linux, macOS and Windows: the same
    // replay must produce the same hashes on every OS and CPU architecture.
    // The golden files were recorded by the original Go implementation, so
    // this also proves the C# port is bit-exact.
    //
    // After an intentional gameplay change, re-record with:
    //
    //   SIMCORE_UPDATE_GOLDEN=1 dotnet test --filter GoldenReplay
    [Fact]
    public void GoldenReplay()
    {
        if (Environment.GetEnvironmentVariable("SIMCORE_UPDATE_GOLDEN") == "1")
        {
            UpdateGolden();
        }

        var (hashes, _) = Replay.Load(TestPaths.TestData(GoldenReplayFile)).Play();
        var want = ReadCheckpoints(TestPaths.TestData(GoldenHashesFile));
        Assert.NotEmpty(want);
        foreach (var (tick, h) in want)
        {
            Assert.InRange(tick, 1, hashes.Length);
            Assert.True(hashes[tick - 1] == h, $"desync at tick {tick}: hash {hashes[tick - 1]:x16}, golden {h:x16}");
        }
    }

    // BotMatchMatchesGoldenCommands records the golden match again from
    // scratch. The command list must equal the committed one, which proves the
    // bot and RNG reproduce the original Go recording exactly.
    [Fact]
    public void BotMatchMatchesGoldenCommands()
    {
        var golden = Replay.Load(TestPaths.TestData(GoldenReplayFile));
        var (rec, live) = Replay.Record(golden.Scenario, golden.Seed, golden.Ticks);

        Assert.Equal((golden.Version, golden.Scenario, golden.Seed, golden.Ticks), (rec.Version, rec.Scenario, rec.Seed, rec.Ticks));
        Assert.Equal(golden.Commands.Count, rec.Commands.Count);
        for (var i = 0; i < golden.Commands.Count; i++)
        {
            Command want = golden.Commands[i], got = rec.Commands[i];
            Assert.True(
                want.Tick == got.Tick && want.Player == got.Player && want.Seq == got.Seq && want.Kind == got.Kind &&
                want.Cell == got.Cell && want.Target == got.Target && want.Units.SequenceEqual(got.Units),
                $"command {i} differs: got {Describe(got)}, golden {Describe(want)}");
        }
        foreach (var (tick, h) in ReadCheckpoints(TestPaths.TestData(GoldenHashesFile)))
        {
            Assert.True(live[tick - 1] == h, $"live match desync at tick {tick}");
        }
    }

    // EncodeMatchesGoldenBytes checks that saving writes the same bytes as
    // the Go version did, so replays stay diffable across the two.
    [Fact]
    public void EncodeMatchesGoldenBytes()
    {
        var path = TestPaths.TestData(GoldenReplayFile);
        using var buf = new MemoryStream();
        Replay.Load(path).Encode(buf);
        Assert.Equal(File.ReadAllText(path), System.Text.Encoding.UTF8.GetString(buf.ToArray()));
    }

    private static string Describe(Command c) =>
        $"{{tick {c.Tick} player {c.Player} seq {c.Seq} kind {c.Kind} units [{string.Join(' ', c.Units)}] cell {c.Cell} target {c.Target}}}";

    private static void CompareHashes(string label, ulong[] want, ulong[] got)
    {
        Assert.True(got.Length == want.Length, $"{label}: {got.Length} ticks, want {want.Length}");
        for (var i = 0; i < want.Length; i++)
        {
            Assert.True(got[i] == want[i], $"{label}: desync at tick {i + 1}: {got[i]:x16} vs {want[i]:x16}");
        }
    }

    private static void UpdateGolden()
    {
        var dir = Path.Combine(TestPaths.RepoRoot(), "tests", "SimCore.Tests", "testdata");
        var (rec, _) = Replay.Record("skirmish", 42, MatchTicks);
        rec.Save(Path.Combine(dir, GoldenReplayFile));
        var (hashes, _) = rec.Play();
        WriteCheckpoints(Path.Combine(dir, GoldenHashesFile), hashes);
        // Refresh the copies the test is about to read.
        File.Copy(Path.Combine(dir, GoldenReplayFile), TestPaths.TestData(GoldenReplayFile), overwrite: true);
        File.Copy(Path.Combine(dir, GoldenHashesFile), TestPaths.TestData(GoldenHashesFile), overwrite: true);
    }

    // Checkpoint files hold "tick hash" lines; hashes[i] is the hash after tick i+1.
    private static void WriteCheckpoints(string path, ulong[] hashes)
    {
        var b = new System.Text.StringBuilder();
        for (var i = 0; i < hashes.Length; i++)
        {
            var tick = i + 1;
            if (tick % CheckEvery == 0 || tick == hashes.Length)
            {
                b.Append(CultureInfo.InvariantCulture, $"{tick} {hashes[i]:x16}\n");
            }
        }
        File.WriteAllText(path, b.ToString());
    }

    private static List<(int Tick, ulong Hash)> ReadCheckpoints(string path)
    {
        var output = new List<(int, ulong)>();
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
            {
                continue;
            }
            Assert.True(fields.Length == 2, $"bad checkpoint line \"{line}\"");
            output.Add((int.Parse(fields[0], CultureInfo.InvariantCulture),
                ulong.Parse(fields[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)));
        }
        return output;
    }
}
