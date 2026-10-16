namespace SimCore.Model;

/// <summary>
/// A change to one stat. Percents and flats from every source are summed
/// before they are applied, so the order of modifiers never matters; see
/// <see cref="StatPipeline"/>.
/// </summary>
/// <param name="Stat">The stat changed.</param>
/// <param name="Percent">Percent added to the base, e.g. -20.</param>
/// <param name="Flat">Amount added after the percent, in the stat's internal units.</param>
public readonly record struct Modifier(Stat Stat, int Percent, long Flat);

/// <summary>A faction rule: a modifier for every archetype with one of the tags.</summary>
/// <param name="Tags">The archetypes affected: any of these tags (any archetype if empty).</param>
/// <param name="Modifier">The change.</param>
public readonly record struct FactionModifier(TagSet Tags, Modifier Modifier);
