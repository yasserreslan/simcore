namespace SimCore.Model;

/// <summary>A damage type and its row of the damage table.</summary>
public sealed class Warhead
{
    /// <summary>The <see cref="Versus"/> entry of an armor class the warhead cannot target ("—" in the design).</summary>
    public const int CannotTarget = -1;

    /// <summary>The position in <see cref="ContentSet.Warheads"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The stable data key.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The percent of damage dealt to each armor class, by its index in
    /// <see cref="ContentSet.ArmorClasses"/>, or <see cref="CannotTarget"/>.
    /// </summary>
    public required IReadOnlyList<int> Versus { get; init; }

    /// <summary>
    /// Per armor class index: the percent of the target's max HP every hit takes, whatever the
    /// damage and distance (0: the usual damage).
    /// </summary>
    public IReadOnlyList<int> MaxHpPercent { get; init; } = [];

    /// <summary>
    /// What its blast sets on fire (content <c>fire</c>; docs/design.md, "Forest fire"), or null: nothing (shells, missiles,
    /// bullets). Only on a map with terrain whose rules have trees, with the content's fire rules.
    /// </summary>
    public WarheadFire? Fire { get; init; }

    /// <summary>The percent of max HP a hit takes from armor class armor, or 0 for the usual damage.</summary>
    public int MaxHpPercentOf(int armor) => armor < MaxHpPercent.Count ? MaxHpPercent[armor] : 0;

    /// <summary>Whether the warhead can target (and damage) armor class armor.</summary>
    public bool CanHit(int armor) => Versus[armor] != CannotTarget;

    /// <summary>
    /// Returns the damage a hit of damage points deals to armor class armor,
    /// scaled by pct (100 = full hit, less at the edge of a splash). It is at
    /// least 1, or 0 when the warhead cannot target the class.
    /// </summary>
    public int Damage(long damage, int armor, long pct)
    {
        var v = Versus[armor];
        if (v == CannotTarget)
        {
            return 0;
        }
        var d = damage * v / 100 * pct / 100;
        return (int)Math.Max(d, 1);
    }
}
