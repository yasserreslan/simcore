using System.Text.Json;
using System.Text.Json.Serialization;
using SimCore.Bots;

namespace SimCore.Content;

/// <summary>Loads host-side AI policy separately from hashed simulation content. Replays record its commands.</summary>
public static class PlayerAiLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Loads and validates a policy file.</summary>
    public static PlayerAiRules Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses a strict policy, rejecting unknown fields and invalid limits.</summary>
    public static PlayerAiRules Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<PlayerAiRules>(json, Options) ?? throw new ArgumentException("Empty AI policy.");
        rules.Validate();
        return rules;
    }
}
