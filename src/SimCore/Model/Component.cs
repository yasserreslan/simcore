namespace SimCore.Model;

/// <summary>
/// A loadout part: a weapon, kit or perk. Which slot of which archetype it can
/// go into is listed on the archetype (<see cref="Archetype.SlotChoices"/>),
/// so one part can be a primary for one class and a secondary for another.
/// </summary>
public sealed class Component
{
    /// <summary>The position in <see cref="ContentSet.Components"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The stable data key.</summary>
    public required string Id { get; init; }

    /// <summary>The cost in credits (no economy yet; kept for later phases).</summary>
    public required int Cost { get; init; }

    /// <summary>Tags the archetype must all have.</summary>
    public required TagSet Requires { get; init; }

    /// <summary>Whether the part stands in for an undecided design choice; it works like any other.</summary>
    public required bool Placeholder { get; init; }

    /// <summary>What it does, in content order.</summary>
    public required IReadOnlyList<Effect> Effects { get; init; }
}
