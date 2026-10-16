namespace SimCore.Model;

/// <summary>A side a player picks: what it may field, and its rules.</summary>
public sealed class Faction
{
    /// <summary>The position in <see cref="ContentSet.Factions"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The stable data key.</summary>
    public required string Id { get; init; }

    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether each archetype is allowed, by archetype index.</summary>
    public required IReadOnlyList<bool> Archetypes { get; init; }

    /// <summary>Whether each component may be picked for a preset slot, by component index.</summary>
    public required IReadOnlyList<bool> Components { get; init; }

    /// <summary>Stat and cost modifiers by tag, applied to every unit of the faction.</summary>
    public required IReadOnlyList<FactionModifier> Modifiers { get; init; }

    /// <summary>How many presets a player may save per archetype.</summary>
    public required int MaxPresetsPerArchetype { get; init; }

    /// <summary>A player is defeated once they own no unit with any of these tags.</summary>
    public required TagSet DefeatedWithout { get; init; }
}
