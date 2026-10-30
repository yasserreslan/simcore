using System.Text.Json;
using System.Text.Json.Serialization;
using SimCore.Bots;

namespace SimCore.Content;

/// <summary>IO for host planner checkpoints. The world, replay position and pending commands remain the host's responsibility.</summary>
public static class PlayerAiStateLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    /// <summary>Serializes a detached planner state.</summary>
    public static string Encode(AiPlannerState state) => JsonSerializer.Serialize(state, Options);
    /// <summary>Reads a checkpoint; RestoreState performs compatibility and invariant validation.</summary>
    public static AiPlannerState Decode(string json) => JsonSerializer.Deserialize<AiPlannerState>(json, Options) ?? throw new ArgumentException("Empty AI checkpoint.");
    /// <summary>Writes planner state; not a world save.</summary>
    public static void Save(string path, PlayerAi bot) => File.WriteAllText(path, Encode(bot.CaptureState()));
    /// <summary>Reads planner state; pass it to a fresh bot's RestoreState with the matching world.</summary>
    public static AiPlannerState Load(string path) => Decode(File.ReadAllText(path));
    /// <summary>Writes all host-controlled planners at one world checkpoint.</summary>
    public static void SaveAll(string path, IReadOnlyList<PlayerAi> bots) =>
        File.WriteAllText(path, JsonSerializer.Serialize(bots.Select(b => b.CaptureState()).ToArray(), Options));
    /// <summary>Reads a multi-player checkpoint. Each state must be validated against its world by RestoreState.</summary>
    public static IReadOnlyList<AiPlannerState> LoadAll(string path) =>
        JsonSerializer.Deserialize<AiPlannerState[]>(File.ReadAllText(path), Options) ?? throw new ArgumentException("Empty AI checkpoints.");
}
