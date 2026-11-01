using SimCore.Bots;
using SimCore.Content;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// The fog golden replay (fog phase F3e): a committed three-minute match with fog of war on a 128 x 128 terrain map with
// hills, woods and a town of 20 neutral buildings in its middle: the Sentinel lobby against the Resistance's, played by
// the bots, a few soldiers of each side garrisoned in the town at the start and the Humvees popping smoke now and then.
// Its hashes must reproduce on every OS (CI runs it on Linux, macOS and Windows): what fog decides (who spots whom, the
// bots' orders follow it) goes into every tick's hash, so every later fog phase is checked here, where the battle golden
// plays without fog. The map is made here in integers (not a file). After an intentional change to fog (each later fog
// phase), re-record it with:
//
//   SIMCORE_UPDATE_GOLDEN=1 dotnet test --filter FogGoldenReplay
public class FogGoldenTests
{
    private const string ReplayFile = "fog_town_seed7.json";
    private const string HashesFile = "fog_town_seed7.hashes";
    private const uint MatchTicks = 3 * 60 * World.TicksPerSecond;
    private const int Size = 128;
    private const string TerrainName = "fog_town";

    // The town: archetype and top-left cell, as laid out (front to the south), with streets of at least 3 cells.
    private static readonly (string Kind, int X, int Y)[] Town =
    [
        ("house_small", 40, 36), ("shop_row", 48, 36), ("apartment_3f", 58, 36), ("corner_building", 71, 36), ("office_block", 81, 36),
        ("stone_rowhouse", 40, 48), ("stone_rowhouse", 44, 48), ("villa_tiled", 52, 48), ("kiosk", 62, 48), ("apartment_l", 66, 48),
        ("water_tower_town", 82, 48),
        ("block_brick_4", 40, 64), ("clinic", 58, 64), ("market_stalls", 70, 64), ("house_over_shop", 82, 64),
        ("warehouse", 40, 78), ("school", 56, 78), ("stone_tower_house", 72, 78), ("shack_tin", 80, 78), ("mudbrick_house", 86, 78),
    ];

    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    // Map is the golden's terrain: rolling ground, two hills away from the armies and the town, the town's ground flat,
    // woods in stripes outside it. Heights raw (512 a metre).
    internal static TerrainMap Map()
    {
        const int m = 512;
        var corners = new int[(Size + 1) * (Size + 1)];
        var flags = new byte[Size * Size];
        static bool InTown(int x, int y) => x is >= 36 and <= 94 && y is >= 32 and <= 92;
        for (var y = 0; y <= Size; y++)
        {
            for (var x = 0; x <= Size; x++)
            {
                var h = 2 * m;
                if (!InTown(x, y))
                {
                    h += Math.Abs(x % 32 - 16) * 24 + Math.Abs(y % 24 - 12) * 20; // rolling ground, under 1.3 m
                    foreach (var (hx, hy) in new[] { (22, 22), (106, 106), (22, 108) })
                    {
                        var d2 = (x - hx) * (x - hx) + (y - hy) * (y - hy);
                        h += Math.Max(0, 6 * m - d2 * 6 * m / 256); // a hill 6 m high, 16 cells round
                    }
                }
                corners[y * (Size + 1) + x] = h;
                if (x < Size && y < Size && !InTown(x, y) && !InTown(x + 1, y + 1) && (x / 6 + y / 5) % 9 == 0)
                {
                    flags[y * Size + x] = (byte)(1 + (x + y) % 3); // forest density 1 to 3
                }
            }
        }
        return new TerrainMap(Size, Size, corners, flags, Rules);
    }

    private static MatchSetup Setup() =>
        TestContent.Sentinel(7) with { Scenario = Scenarios.Scenario.SandboxName, Fog = true, Terrain = TerrainName };

    // Record plays the golden match: the town spawned by SandboxSpawn commands (player 2's: buildings go neutral), the
    // garrisons ordered in, the Humvees' smoke and the bots.
    internal static (Replay Replay, ulong[] Hashes, World World) Record()
    {
        var setup = Setup();
        var w = Replay.NewWorld(TestContent.Mvp, setup, Map());
        var bots = w.Players.Select(p => new Bot(p.Id, setup.Seed + p.Id)).ToList();
        var commands = new List<Command>();
        var hashes = new List<ulong>((int)MatchTicks);
        foreach (var (kind, x, y) in Town)
        {
            commands.Add(w.Issue(new Command { Tick = 0, Player = 2, Kind = CommandKind.SandboxSpawn, Unit = kind, Cell = new Cell(x, y) }));
        }
        while (w.Tick < MatchTicks)
        {
            var at = w.Tick + World.InputDelay;
            if (w.Tick == 2)
            {
                foreach (var p in w.Players)
                {
                    foreach (var c in Garrisons(w, p.Id, at))
                    {
                        commands.Add(w.Issue(c));
                    }
                }
            }
            if (w.Tick >= 300 && w.Tick % 100 == 0 && Shelled(w) is { } aim)
            {
                // The Resistance's tank hunters fire their missiles at the kiosk, then at the market stalls (told again while
                // the bot has given them other orders): they fall, and the sight past them opens.
                var hunters = w.Units.Where(u => u.Owner == 2 && u.Def.Name == "Tank hunter" && u.Alive && u.Garrison == 0 &&
                    u.Inside == 0 && u.Order.Kind != OrderKind.AttackGround).Select(u => u.Id).ToList();
                if (hunters.Count > 0)
                {
                    commands.Add(w.Issue(new Command { Tick = at, Player = 2, Kind = CommandKind.AttackGround, Units = hunters, Cell = aim }));
                }
            }
            if (w.Tick is 600 or 1600 or 2600)
            {
                var humvees = w.Units.Where(u => u.Owner == 1 && u.Def.Archetype.Id == "humvee" && u.Alive && w.SmokeOf(u) is { Charges: > 0 })
                    .Select(u => u.Id).ToList();
                if (humvees.Count > 0)
                {
                    commands.Add(w.Issue(new Command { Tick = at, Player = 1, Kind = CommandKind.Smoke, Units = humvees }));
                }
            }
            foreach (var b in bots)
            {
                foreach (var c in b.Think(w))
                {
                    commands.Add(w.Issue(c));
                }
            }
            w.Step();
            hashes.Add(w.Hash());
        }
        return (new Replay { Setup = setup, Ticks = MatchTicks, Commands = commands }, [.. hashes], w);
    }

    // Shelled is the centre of the first of the kiosk and the market stalls still standing, or null.
    private static Cell? Shelled(World w)
    {
        foreach (var id in new[] { "kiosk", "market_stalls" })
        {
            if (w.Units.FirstOrDefault(u => u.Def.Archetype.Id == id && u.Alive) is { } b)
            {
                return Cell.Of(b.Pos);
            }
        }
        return null;
    }

    // Garrisons are the Enter orders of three of player's riflemen (the lowest IDs) into the nearest garrisonable building
    // of the town, ties by ID.
    private static IEnumerable<Command> Garrisons(World w, byte player, uint at)
    {
        var riflemen = w.Units.Where(u => u.Owner == player && u.Def.Name == "Rifleman" && u.Garrison == 0 && u.Inside == 0).Take(3).ToList();
        if (riflemen.Count == 0)
        {
            yield break;
        }
        var from = riflemen[0].Pos;
        var best = w.Units.Where(u => u.Owner == 0 && u.Def.Archetype.Garrison is not null)
            .OrderBy(u => (u.Pos - from).LenSq()).ThenBy(u => u.Id).FirstOrDefault();
        if (best is not null)
        {
            yield return new Command { Tick = at, Player = player, Kind = CommandKind.Enter, Units = [.. riflemen.Select(u => u.Id)], Target = best.Id };
        }
    }

    [Fact]
    public void FogGoldenReplay()
    {
        if (Environment.GetEnvironmentVariable("SIMCORE_UPDATE_GOLDEN") == "1")
        {
            UpdateGolden();
        }
        var replay = Replay.Load(TestPaths.TestData(ReplayFile));
        Assert.Equal(Setup().Digest(), replay.Setup.Digest()); // the committed lobby, unchanged
        var (hashes, world) = replay.Play(TestContent.Mvp, Map());
        Assert.True(world.Fog);
        Assert.Equal(Town.Length, replay.Commands.Count(c => c.Kind == CommandKind.SandboxSpawn));
        var want = ReplayTests.ReadCheckpoints(TestPaths.TestData(HashesFile));
        Assert.NotEmpty(want);
        foreach (var (tick, h) in want)
        {
            Assert.InRange(tick, 1, hashes.Length);
            Assert.True(hashes[tick - 1] == h, $"desync at tick {tick}: hash {hashes[tick - 1]:x16}, golden {h:x16}");
        }
        Assert.True(world.FogMatchesRebuild());
        // The match exercises a collapse: the tank hunters brought the kiosk and the market stalls down (if a re-record
        // loses this, change the script, not the check).
        Assert.DoesNotContain(world.Units, u => u.Def.Archetype.Id is "kiosk" or "market_stalls");
        Assert.Equal(Town.Length - 2, world.Units.Count(u => u.Def.Archetype.Garrison is not null || u.Def.Archetype.Neutral));
    }

    // The golden match as recorded: the town stands (all 20 buildings spawned), garrisons went in, smoke was popped, and
    // the sight blockers changed in play; recording again gives the committed commands.
    [Fact]
    public void FogGoldenRecordsTheSameCommands()
    {
        var golden = Replay.Load(TestPaths.TestData(ReplayFile));
        var (rec, live, _) = Record();
        Assert.Equal(golden.Commands.Count, rec.Commands.Count);
        for (var i = 0; i < golden.Commands.Count; i++)
        {
            Command want = golden.Commands[i], got = rec.Commands[i];
            Assert.True(want.Tick == got.Tick && want.Player == got.Player && want.Seq == got.Seq && want.Kind == got.Kind &&
                want.Cell == got.Cell && want.Target == got.Target && want.Units.SequenceEqual(got.Units), $"command {i} differs");
        }
        foreach (var (tick, h) in ReplayTests.ReadCheckpoints(TestPaths.TestData(HashesFile)))
        {
            Assert.True(live[tick - 1] == h, $"live match desync at tick {tick}");
        }
        Assert.Contains(golden.Commands, c => c.Kind == CommandKind.Enter && c.Tick < 10);
        Assert.Contains(golden.Commands, c => c.Kind == CommandKind.Smoke);
    }

    private static void UpdateGolden()
    {
        var dir = Path.Combine(TestPaths.RepoRoot(), "tests", "SimCore.Tests", "testdata");
        var (rec, _, _) = Record();
        rec.Save(Path.Combine(dir, ReplayFile));
        var (hashes, _) = rec.Play(TestContent.Mvp, Map());
        ReplayTests.WriteCheckpoints(Path.Combine(dir, HashesFile), hashes);
        File.Copy(Path.Combine(dir, ReplayFile), TestPaths.TestData(ReplayFile), overwrite: true);
        File.Copy(Path.Combine(dir, HashesFile), TestPaths.TestData(HashesFile), overwrite: true);
    }
}
