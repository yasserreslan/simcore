// Reads content files (JSON) and compiles them into a SimCore.Model.ContentSet.
//
// This namespace does IO and runs once before a match, never inside a tick,
// so it is exempt from the determinism rules. Its output must not depend on
// file order: every collection is sorted by ID with ordinal comparison.
using System.Text.Json.Serialization;

namespace SimCore.Content;

// ContentFile is one JSON file. A file may define any mix of kinds, one
// definition or many.
internal sealed class ContentFile
{
    [JsonPropertyName("warheads")]
    public List<WarheadJson>? Warheads { get; set; }

    [JsonPropertyName("archetypes")]
    public List<ArchetypeJson>? Archetypes { get; set; }

    [JsonPropertyName("components")]
    public List<ComponentJson>? Components { get; set; }

    [JsonPropertyName("factions")]
    public List<FactionJson>? Factions { get; set; }
}

internal sealed class WarheadJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    // Versus maps armor class to the percent of damage dealt.
    [JsonPropertyName("versus")]
    public Dictionary<string, int>? Versus { get; set; }
}

internal sealed class FootprintJson
{
    [JsonPropertyName("w")]
    public int W { get; set; }

    [JsonPropertyName("h")]
    public int H { get; set; }
}

internal sealed class ArchetypeJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("glyph")]
    public string? Glyph { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("locomotor")]
    public string? Locomotor { get; set; }

    [JsonPropertyName("armor")]
    public string? Armor { get; set; }

    [JsonPropertyName("hp")]
    public int Hp { get; set; }

    // Speed is in tenths of a cell per second.
    [JsonPropertyName("speed")]
    public int Speed { get; set; }

    [JsonPropertyName("turnRate")]
    public int TurnRate { get; set; }

    [JsonPropertyName("turret")]
    public bool Turret { get; set; }

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("cost")]
    public int Cost { get; set; }

    [JsonPropertyName("budget")]
    public int Budget { get; set; }

    [JsonPropertyName("slots")]
    public Dictionary<string, int>? Slots { get; set; }

    [JsonPropertyName("defaultComponents")]
    public List<string>? DefaultComponents { get; set; }

    [JsonPropertyName("footprint")]
    public FootprintJson? Footprint { get; set; }

    // BuildTime is in ticks.
    [JsonPropertyName("buildTime")]
    public int BuildTime { get; set; }
}

internal sealed class ComponentJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    [JsonPropertyName("cost")]
    public int Cost { get; set; }

    [JsonPropertyName("requires")]
    public List<string>? Requires { get; set; }

    [JsonPropertyName("effects")]
    public List<EffectJson>? Effects { get; set; }
}

// EffectJson holds exactly one effect, keyed by its kind: {"weapon": {...}}.
internal sealed class EffectJson
{
    [JsonPropertyName("weapon")]
    public WeaponJson? Weapon { get; set; }

    [JsonPropertyName("aura")]
    public AuraJson? Aura { get; set; }

    [JsonPropertyName("placer")]
    public PlacerJson? Placer { get; set; }

    [JsonPropertyName("trigger")]
    public TriggerJson? Trigger { get; set; }

    [JsonPropertyName("builder")]
    public BuilderJson? Builder { get; set; }

    [JsonPropertyName("buildAssist")]
    public BuildAssistJson? BuildAssist { get; set; }

    [JsonPropertyName("repair")]
    public RepairJson? Repair { get; set; }

    [JsonPropertyName("modifier")]
    public ModifierJson? Modifier { get; set; }
}

// Distances (range, radius, splash, scatter) are in tenths of a cell; times
// (reload, everyTicks, cooldown) are in ticks.
internal sealed class WeaponJson
{
    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("range")]
    public int Range { get; set; }

    [JsonPropertyName("reload")]
    public int Reload { get; set; }

    [JsonPropertyName("warhead")]
    public string? Warhead { get; set; }

    [JsonPropertyName("splash")]
    public int Splash { get; set; }

    [JsonPropertyName("scatter")]
    public int Scatter { get; set; }

    [JsonPropertyName("targets")]
    public List<string>? Targets { get; set; }
}

internal sealed class AffectsJson
{
    [JsonPropertyName("relation")]
    public string? Relation { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}

internal sealed class AuraJson
{
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("everyTicks")]
    public int EveryTicks { get; set; }

    [JsonPropertyName("heal")]
    public int Heal { get; set; }

    [JsonPropertyName("modifiers")]
    public List<ModifierJson>? Modifiers { get; set; }

    [JsonPropertyName("affects")]
    public AffectsJson? Affects { get; set; }
}

internal sealed class PlacerJson
{
    [JsonPropertyName("archetype")]
    public string? Archetype { get; set; }

    [JsonPropertyName("charges")]
    public int Charges { get; set; }

    [JsonPropertyName("cooldown")]
    public int Cooldown { get; set; }

    [JsonPropertyName("range")]
    public int Range { get; set; }
}

internal sealed class TriggerJson
{
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("warhead")]
    public string? Warhead { get; set; }

    [JsonPropertyName("splash")]
    public int Splash { get; set; }

    [JsonPropertyName("affects")]
    public List<string>? Affects { get; set; }
}

internal sealed class BuilderJson
{
    [JsonPropertyName("rate")]
    public int Rate { get; set; }

    [JsonPropertyName("range")]
    public int Range { get; set; }
}

internal sealed class BuildAssistJson
{
    [JsonPropertyName("bonus")]
    public int Bonus { get; set; }

    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("unlocks")]
    public List<string>? Unlocks { get; set; }
}

internal sealed class RepairJson
{
    [JsonPropertyName("rate")]
    public int Rate { get; set; }

    [JsonPropertyName("everyTicks")]
    public int EveryTicks { get; set; }

    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("affects")]
    public List<string>? Affects { get; set; }
}

// ModifierJson changes one stat. Flat values use the stat's content units:
// speed in tenths of a cell per second, range in tenths of a cell.
internal sealed class ModifierJson
{
    [JsonPropertyName("stat")]
    public string? Stat { get; set; }

    [JsonPropertyName("percent")]
    public int Percent { get; set; }

    [JsonPropertyName("flat")]
    public int Flat { get; set; }
}

internal sealed class FactionModifierJson
{
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("stat")]
    public string? Stat { get; set; }

    [JsonPropertyName("percent")]
    public int Percent { get; set; }

    [JsonPropertyName("flat")]
    public int Flat { get; set; }
}

internal sealed class FactionJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("archetypes")]
    public List<string>? Archetypes { get; set; }

    [JsonPropertyName("components")]
    public List<string>? Components { get; set; }

    [JsonPropertyName("modifiers")]
    public List<FactionModifierJson>? Modifiers { get; set; }
}
