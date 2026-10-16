namespace SimCore.Model;

/// <summary>A damage type and its row of the damage table.</summary>
public sealed class Warhead
{
    /// <summary>The position in <see cref="ContentSet.Warheads"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The stable data key.</summary>
    public required string Id { get; init; }

    /// <summary>The percent of damage dealt to each <see cref="Armor"/> class, by its value.</summary>
    public required IReadOnlyList<int> Versus { get; init; }

    /// <summary>
    /// Returns the damage a hit of damage points deals to armor a, scaled by
    /// pct (100 = full hit, less at the edge of a splash). It is at least 1.
    /// </summary>
    public int Damage(long damage, Armor a, long pct)
    {
        var d = damage * Versus[(int)a] / 100 * pct / 100;
        return (int)Math.Max(d, 1);
    }
}
