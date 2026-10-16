// Records matches as command logs and plays them back.
//
// A replay is just the starting scenario, its seed and every command. Because
// the sim is deterministic, playing it back recreates the match exactly, and
// the per-tick hashes must match the original run.
using System.Text.Json;
using System.Text.Json.Serialization;
using SimCore.Bots;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimCore.Replays;

/// <summary>Thrown for a scenario name not in the registry.</summary>
public sealed class UnknownScenarioException(string name)
    : ArgumentException($"replay: unknown scenario: \"{name}\"");

/// <summary>A recorded match.</summary>
public sealed record Replay
{
    /// <summary>The replay format version.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
#if NET9_0_OR_GREATER
        IndentSize = 1, // the same bytes the Go version wrote
        NewLine = "\n",
#endif // on net8.0 the JSON is equivalent but indented by two spaces
    };

    /// <summary>The format version; must be <see cref="CurrentVersion"/>.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>The scenario name, e.g. "skirmish".</summary>
    [JsonPropertyName("scenario")]
    public string Scenario { get; init; } = "";

    /// <summary>The scenario seed.</summary>
    [JsonPropertyName("seed")]
    public ulong Seed { get; init; }

    /// <summary>How many ticks the match lasts.</summary>
    [JsonPropertyName("ticks")]
    public uint Ticks { get; init; }

    /// <summary>Every command issued, in issue order.</summary>
    [JsonPropertyName("commands")]
    public IReadOnlyList<Command> Commands { get; init; } = [];

    /// <summary>Builds the named scenario's starting world.</summary>
    /// <exception cref="UnknownScenarioException">The name is not registered.</exception>
    public static World NewWorld(string name, ulong seed) => name switch
    {
        "skirmish" => Scenarios.Scenario.Skirmish(seed),
        _ => throw new UnknownScenarioException(name),
    };

    /// <summary>
    /// Plays a bot-vs-bot match for ticks ticks and returns its replay and the
    /// world hash after every tick.
    /// </summary>
    public static (Replay Replay, ulong[] Hashes) Record(string name, ulong seed, uint ticks)
    {
        var w = NewWorld(name, seed);
        var bots = w.Players.Select(p => new Bot(p.Id, seed + p.Id)).ToList();
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
        var r = new Replay { Scenario = name, Seed = seed, Ticks = ticks, Commands = commands };
        return (r, [.. hashes]);
    }

    /// <summary>
    /// Runs the replay from its starting world and returns the world hash
    /// after every tick, plus the final world.
    /// </summary>
    /// <exception cref="InvalidDataException">The format version is unsupported.</exception>
    public (ulong[] Hashes, World World) Play()
    {
        var w = Start();
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
    /// <exception cref="InvalidDataException">The format version is unsupported.</exception>
    public World Start()
    {
        if (Version != CurrentVersion)
        {
            throw new InvalidDataException($"replay: version {Version}, want {CurrentVersion}");
        }
        var w = NewWorld(Scenario, Seed);
        foreach (var c in Commands)
        {
            w.Issue(c);
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
}
