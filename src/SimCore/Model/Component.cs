namespace SimCore.Model;

/// <summary>A loadout part: a weapon, ability or perk that goes into a slot.</summary>
public sealed class Component
{
    /// <summary>The position in <see cref="ContentSet.Components"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The stable data key.</summary>
    public required string Id { get; init; }

    /// <summary>The slot it takes.</summary>
    public required SlotKind Slot { get; init; }

    /// <summary>The points it costs against an archetype's budget.</summary>
    public required int Cost { get; init; }

    /// <summary>Tags the archetype must all have.</summary>
    public required TagSet Requires { get; init; }

    /// <summary>What it does, in content order.</summary>
    public required IReadOnlyList<Effect> Effects { get; init; }
}
