using System.Text;
using System.Text.Json.Serialization;

namespace SimCore.Model;

/// <summary>
/// Everything players agree on before a match: the scenario, the seed, the
/// content (by hash) and each player's faction, presets and starting army. It
/// is stored in the replay, and its <see cref="Digest"/> goes into the world
/// hash, so peers with different setups desync on the first tick.
/// </summary>
public sealed record MatchSetup
{
    /// <summary>The most players a match may have.</summary>
    public const int MaxPlayers = 8;

    /// <summary>The most presets one player may bring, over all archetypes.</summary>
    public const int MaxPresets = 64;

    /// <summary>The most units (drivers included) a player may start with.</summary>
    public const int MaxArmy = 256;

    /// <summary>The scenario name, e.g. "battle".</summary>
    [JsonPropertyName("scenario")]
    public string Scenario { get; init; } = "";

    /// <summary>The match seed: the map and every random roll in the sim come from it.</summary>
    [JsonPropertyName("seed")]
    public ulong Seed { get; init; }

    /// <summary>The <see cref="ContentSet.Hash"/> of the content the match is played with.</summary>
    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = "";

    /// <summary>
    /// The terrain map the match is played on, as a map file name the host resolves against its
    /// maps directory (<c>Content.TerrainLoader.ForSetup</c>); null for a flat map without terrain.
    /// Its digest goes into every world hash, so peers on another map desync at once.
    /// </summary>
    [JsonPropertyName("terrain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Terrain { get; init; }

    /// <summary>
    /// Fog of war: each player sees only what its units see (line of sight over terrain and through
    /// forest, see <c>Sim.FogGrid</c>), and enemy units nobody on its side spots are invisible to it:
    /// not shown, not targetable, not commandable. Off by default, so older setups play and hash as before.
    /// </summary>
    [JsonPropertyName("fog")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Fog { get; init; }

    /// <summary>The players, with IDs 1, 2, … in order.</summary>
    [JsonPropertyName("players")]
    public IReadOnlyList<PlayerSetup> Players { get; init; } = [];

    /// <summary>Returns a copy pinned to content and seed, ready to start.</summary>
    public MatchSetup For(ContentSet content, ulong seed) => this with { ContentHash = content.Hash, Seed = seed };

    /// <summary>Returns a 64-bit FNV-1a digest of the whole setup.</summary>
    /// <remarks>
    /// Integers are 8 little-endian bytes, strings a length and their UTF-8
    /// bytes, lists a count and their items, absent strings the empty string.
    /// </remarks>
    public ulong Digest()
    {
        var h = new Fnv64();
        h.Str(ContentHash);
        h.Str(Scenario);
        h.Put(Seed);
        if (Terrain is not null)
        {
            h.Str(Terrain); // only with terrain: flat setups digest as before
        }
        if (Fog)
        {
            h.Str("fog"); // only with fog: setups without it digest as before
        }
        var players = Players ?? [];
        h.Put((ulong)players.Count);
        foreach (var p in players)
        {
            h.Put(p?.Id ?? 0);
            h.Str(p?.Faction);
            var presets = p?.Presets ?? [];
            h.Put((ulong)presets.Count);
            foreach (var pr in presets)
            {
                h.Str(pr?.Name);
                h.Str(pr?.Archetype);
                var comps = pr?.Components ?? [];
                h.Put((ulong)comps.Count);
                foreach (var c in comps)
                {
                    h.Str(c);
                }
            }
            var army = p?.Army ?? [];
            h.Put((ulong)army.Count);
            foreach (var a in army)
            {
                h.Str(a?.Unit);
                h.Put((ulong)(a?.Count ?? 0));
                h.Str(a?.Driver);
            }
        }
        return h.Sum;
    }

    // Fnv64 is a streaming FNV-1a 64-bit hash.
    private struct Fnv64()
    {
        private const ulong Prime = 1099511628211;

        public ulong Sum { get; private set; } = 14695981039346656037;

        public void Put(ulong v)
        {
            for (var i = 0; i < 8; i++)
            {
                Sum = unchecked((Sum ^ (byte)(v >> (8 * i))) * Prime);
            }
        }

        public void Str(string? s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            Put((ulong)b.Length);
            foreach (var x in b)
            {
                Sum = unchecked((Sum ^ x) * Prime);
            }
        }
    }
}

/// <summary>One player's lobby choices.</summary>
public sealed record PlayerSetup
{
    /// <summary>The player's ID, starting at 1.</summary>
    [JsonPropertyName("id")]
    public byte Id { get; init; }

    /// <summary>The faction ID.</summary>
    [JsonPropertyName("faction")]
    public string Faction { get; init; } = "";

    /// <summary>The player's saved loadouts.</summary>
    [JsonPropertyName("presets")]
    public IReadOnlyList<Preset> Presets { get; init; } = [];

    /// <summary>The units the scenario spawns for the player, in spawn (and ID) order.</summary>
    [JsonPropertyName("army")]
    public IReadOnlyList<ArmyEntry> Army { get; init; } = [];
}

/// <summary>A named loadout: an archetype and one component per slot.</summary>
public sealed record Preset
{
    /// <summary>The name shown in the lobby; unique per player and never an archetype ID.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The archetype ID.</summary>
    [JsonPropertyName("archetype")]
    public string Archetype { get; init; } = "";

    /// <summary>The component IDs in slot order: primary, then secondary.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyList<string> Components { get; init; } = [];
}

/// <summary>A group of identical starting units.</summary>
public sealed record ArmyEntry
{
    /// <summary>A preset name, or the ID of an archetype without slots (vehicles, structures, workers).</summary>
    [JsonPropertyName("unit")]
    public string Unit { get; init; } = "";

    /// <summary>How many to spawn.</summary>
    [JsonPropertyName("count")]
    public int Count { get; init; } = 1;

    /// <summary>For vehicles: a preset or archetype that starts in the driver seat of each; null for an empty vehicle.</summary>
    [JsonPropertyName("driver")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Driver { get; init; }
}
