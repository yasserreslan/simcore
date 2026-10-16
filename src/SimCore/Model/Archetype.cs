using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// A kind of entity: infantry, vehicle, aircraft, structure or trap. Players
/// turn archetypes into units through presets (a loadout of components), or
/// use the archetype's default components.
/// </summary>
public sealed class Archetype
{
    /// <summary>The position in <see cref="ContentSet.Archetypes"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The stable data key.</summary>
    public required string Id { get; init; }

    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>The debug-view letter.</summary>
    public required char Glyph { get; init; }

    /// <summary>The tags, which component requirements and effect filters test.</summary>
    public required TagSet Tags { get; init; }

    /// <summary>How it moves; <see cref="Locomotor.Flying"/> puts it on the air layer.</summary>
    public required Locomotor Locomotor { get; init; }

    /// <summary>The layer it is on, implied by <see cref="Locomotor"/>.</summary>
    public Layers Layer => Locomotor == Locomotor.Flying ? Layers.Air : Layers.Ground;

    /// <summary>The armor class.</summary>
    public required Armor Armor { get; init; }

    /// <summary>Starting hit points.</summary>
    public required int MaxHp { get; init; }

    /// <summary>Distance per tick; zero means it cannot move.</summary>
    public required Fixed Speed { get; init; }

    /// <summary>Facings per tick.</summary>
    public required int TurnRate { get; init; }

    /// <summary>Whether the weapon aims independently of the hull.</summary>
    public required bool Turret { get; init; }

    /// <summary>Whether enemies can't see or target it (traps).</summary>
    public required bool Hidden { get; init; }

    /// <summary>
    /// Whether this structure variant may only be built by a player whose
    /// presets include a component that unlocks it.
    /// </summary>
    public required bool Locked { get; init; }

    /// <summary>The build cost in credits.</summary>
    public required int Cost { get; init; }

    /// <summary>The points a preset's components may cost in total.</summary>
    public required int Budget { get; init; }

    /// <summary>How many components of each <see cref="SlotKind"/> a preset may hold, by its value.</summary>
    public required IReadOnlyList<int> Slots { get; init; }

    /// <summary>The components a unit without a preset gets, as indices into <see cref="ContentSet.Components"/>.</summary>
    public required IReadOnlyList<int> DefaultComponents { get; init; }

    /// <summary>The footprint width in cells; zero for anything that is not a structure.</summary>
    public required int FootprintW { get; init; }

    /// <summary>The footprint height in cells.</summary>
    public required int FootprintH { get; init; }

    /// <summary>Ticks to build with one builder at 100% rate.</summary>
    public required int BuildTime { get; init; }

    /// <summary>Whether it is a structure: it has a footprint and can be built.</summary>
    public bool IsStructure => FootprintW > 0;
}
