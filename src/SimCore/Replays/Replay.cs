// Records matches as command logs and plays them back.
//
// A replay is the match setup (scenario, seed, content hash, every player's
// faction, presets and army) and every command. Because the sim is
// deterministic, playing it back with the same content recreates the match
// exactly, and the per-tick hashes must match the original run.
using System.Text.Json;
using System.Text.Json.Serialization;
using SimCore.Bots;
using SimCore.Model;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimCore.Replays;

/// <summary>A recorded match.</summary>
public sealed record Replay
{
    /// <summary>The replay format version. Version 2 added the match setup.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The longest replay accepted: 24 hours of game time.</summary>
    public const uint MaxTicks = World.TicksPerSecond * 60 * 60 * 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
#if NET9_0_OR_GREATER
        IndentSize = 1,
        NewLine = "\n",
#endif // on net8.0 the JSON is equivalent but indented by two spaces
    };

    private static readonly JsonSerializerOptions SetupOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The format version; must be <see cref="CurrentVersion"/>.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>The match setup, pinned to the content hash and seed it was played with.</summary>
    [JsonPropertyName("setup")]
    public MatchSetup Setup { get; init; } = new();

    /// <summary>How many ticks the match lasts.</summary>
    [JsonPropertyName("ticks")]
    public uint Ticks { get; init; }

    /// <summary>Every command issued, in issue order.</summary>
    [JsonPropertyName("commands")]
    public IReadOnlyList<Command> Commands { get; init; } = [];

    /// <summary>Checks setup against content and builds its scenario's starting world.</summary>
    /// <exception cref="ContentHashMismatchException">The setup was made for other content.</exception>
    /// <exception cref="MatchSetupException">The setup is invalid or names an unknown scenario.</exception>
    /// <remarks>
    /// A setup with a <see cref="MatchSetup.Terrain"/> needs its map, loaded by the caller (see
    /// <c>Content.TerrainLoader.ForSetup</c>); it is attached before the first tick.
    /// </remarks>
    public static World NewWorld(ContentSet content, MatchSetup setup, TerrainMap? terrain = null)
    {
        if (setup.Terrain is not null && terrain is null)
        {
            throw new MatchSetupException($"setup: terrain \"{setup.Terrain}\" wants its map");
        }
        var w = Scenario.Create(MatchCompiler.Compile(content, setup), terrain);
        if (terrain is not null)
        {
            w.AttachTerrain(terrain);
        }
        return w;
    }

    /// <summary>
    /// Plays a bot-vs-bot match for ticks ticks and returns its replay and the
    /// world hash after every tick. The setup must already be pinned to the
    /// content (see <see cref="MatchSetup.For"/>).
    /// </summary>
    public static (Replay Replay, ulong[] Hashes) Record(ContentSet content, MatchSetup setup, uint ticks, TerrainMap? terrain = null)
    {
        var w = NewWorld(content, setup, terrain);
        var bots = w.Players.Select(p => new Bot(p.Id, setup.Seed + p.Id)).ToList();
        var commands = new List<Command>();
        var hashes = new List<ulong>((int)ticks);
        while (w.Tick < ticks)
        {
            foreach (var b in bots)
            {
                foreach (var c in b.Think(w))
                {
                    try
                    {
                        commands.Add(w.Issue(c));
                    }
                    catch (CommandRejectedException e)
                    {
                        throw new InvalidOperationException($"tick {w.Tick}: {e.Message}", e);
                    }
                }
            }
            w.Step();
            hashes.Add(w.Hash());
        }
        var r = new Replay { Setup = setup, Ticks = ticks, Commands = commands };
        return (r, [.. hashes]);
    }

    /// <summary>
    /// Runs the replay from its starting world and returns the world hash
    /// after every tick, plus the final world.
    /// </summary>
    /// <exception cref="InvalidDataException">The replay is malformed; see <see cref="Start"/>.</exception>
    /// <exception cref="MatchSetupException">The setup does not fit content.</exception>
    public (ulong[] Hashes, World World) Play(ContentSet content, TerrainMap? terrain = null)
    {
        var w = Start(content, terrain);
        var hashes = new ulong[Ticks];
        while (w.Tick < Ticks)
        {
            w.Step();
            hashes[w.Tick - 1] = w.Hash();
        }
        return (hashes, w);
    }

    /// <summary>
    /// Returns the starting world with every command issued, ready to step,
    /// for callers that want to watch the match tick by tick.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The format version is unsupported, the replay is too long, or its setup,
    /// command list or a command is malformed. Replay files can come from
    /// anyone, so they are checked before use.
    /// </exception>
    /// <exception cref="ContentHashMismatchException">The match was played with other content; it refuses to start.</exception>
    /// <exception cref="MatchSetupException">The setup breaks a content rule.</exception>
    public World Start(ContentSet content, TerrainMap? terrain = null)
    {
        if (Version != CurrentVersion)
        {
            throw new InvalidDataException($"replay: version {Version}, want {CurrentVersion}");
        }
        if (Ticks > MaxTicks)
        {
            throw new InvalidDataException($"replay: {Ticks} ticks, max {MaxTicks}");
        }
        if (Setup is null)
        {
            throw new InvalidDataException("replay: missing setup");
        }
        if (Commands is null)
        {
            throw new InvalidDataException("replay: missing commands");
        }
        var w = NewWorld(content, Setup, terrain);
        for (var i = 0; i < Commands.Count; i++)
        {
            var c = Commands[i] ?? throw new InvalidDataException($"replay: command #{i + 1} is null");
            try
            {
                w.Issue(c);
            }
            catch (CommandRejectedException e)
            {
                throw new InvalidDataException($"replay: command #{i + 1}: {e.Message}", e);
            }
        }
        return w;
    }

    /// <summary>Writes the replay as JSON.</summary>
    public void Encode(Stream s)
    {
        JsonSerializer.Serialize(s, this, JsonOptions);
        s.WriteByte((byte)'\n');
    }

    /// <summary>Reads a JSON replay.</summary>
    /// <exception cref="JsonException">The JSON is malformed.</exception>
    public static Replay Decode(Stream s) =>
        JsonSerializer.Deserialize<Replay>(s, JsonOptions) ?? throw new JsonException("replay: null document");

    /// <summary>Writes the replay to a file.</summary>
    public void Save(string path)
    {
        using var f = File.Create(path);
        Encode(f);
    }

    /// <summary>Reads a replay file.</summary>
    public static Replay Load(string path)
    {
        using var f = File.OpenRead(path);
        return Decode(f);
    }

    /// <summary>
    /// Reads a lobby file: a <see cref="MatchSetup"/> in JSON, usually without
    /// its content hash and seed, which <see cref="MatchSetup.For"/> fills in.
    /// Unknown fields are errors; comments and trailing commas are allowed.
    /// </summary>
    /// <exception cref="JsonException">The JSON is malformed.</exception>
    public static MatchSetup LoadSetup(string path)
    {
        using var f = File.OpenRead(path);
        return JsonSerializer.Deserialize<MatchSetup>(f, SetupOptions) ?? throw new JsonException("setup: null document");
    }
}
