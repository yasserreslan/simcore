using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// One typed effect of a component. Each kind maps to exactly one behavior
/// system in the sim; adding a mechanic means adding an effect kind and its
/// system.
/// </summary>
public abstract class Effect;

/// <summary>A weapon; shots hit instantly. Used by the combat system.</summary>
public sealed class WeaponEffect : Effect
{
    /// <summary>Damage per hit before armor.</summary>
    public required int Damage { get; init; }

    /// <summary>The maximum firing distance.</summary>
    public required Fixed Range { get; init; }

    /// <summary>Ticks between shots.</summary>
    public required int Reload { get; init; }

    /// <summary>The damage type.</summary>
    public required Warhead Warhead { get; init; }

    /// <summary>The splash radius; zero hits a single target.</summary>
    public required Fixed Splash { get; init; }

    /// <summary>The maximum landing offset per axis.</summary>
    public required Fixed Scatter { get; init; }

    /// <summary>The layers it can hit.</summary>
    public required Layers Targets { get; init; }
}

/// <summary>
/// An aura around its unit. Stat modifiers apply every tick to units in the
/// radius; healing lands every <see cref="EveryTicks"/> ticks. Used by the
/// aura system.
/// </summary>
public sealed class AuraEffect : Effect
{
    /// <summary>The radius.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>The healing period in ticks.</summary>
    public required int EveryTicks { get; init; }

    /// <summary>Hit points healed per period, never above max HP.</summary>
    public required int Heal { get; init; }

    /// <summary>Buffs (or debuffs) for units in the radius.</summary>
    public required IReadOnlyList<Modifier> Modifiers { get; init; }

    /// <summary>Allies or enemies.</summary>
    public required Relation Affects { get; init; }

    /// <summary>Only units with one of these tags (any unit if empty).</summary>
    public required TagSet AffectsTags { get; init; }
}

/// <summary>Places an entity, like a trap, at a target cell. Used by the ability system.</summary>
public sealed class PlacerEffect : Effect
{
    /// <summary>The archetype placed, as an index into <see cref="ContentSet.Archetypes"/>.</summary>
    public required int Archetype { get; init; }

    /// <summary>How many it can place in a match.</summary>
    public required int Charges { get; init; }

    /// <summary>Ticks between placements.</summary>
    public required int Cooldown { get; init; }

    /// <summary>The maximum distance from the unit to the target cell's center.</summary>
    public required Fixed Range { get; init; }
}

/// <summary>
/// Fires once when an enemy ground unit comes within the radius, then removes
/// its entity. Used by the trigger system, for trap archetypes.
/// </summary>
public sealed class TriggerEffect : Effect
{
    /// <summary>The detection radius.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>Damage before armor.</summary>
    public required int Damage { get; init; }

    /// <summary>The damage type.</summary>
    public required Warhead Warhead { get; init; }

    /// <summary>The blast radius; zero hits only the nearest triggering unit.</summary>
    public required Fixed Splash { get; init; }

    /// <summary>Only units with one of these tags set it off (any unit if empty).</summary>
    public required TagSet Affects { get; init; }
}

/// <summary>Builds construction sites. Used by the construction system.</summary>
public sealed class BuilderEffect : Effect
{
    /// <summary>Construction rate in percent; 100 builds at the archetype's nominal build time.</summary>
    public required int Rate { get; init; }

    /// <summary>The maximum distance from the unit to the site's footprint.</summary>
    public required Fixed Range { get; init; }
}

/// <summary>
/// Speeds up nearby construction and unlocks structure variants. Used by the
/// construction system and by match setup.
/// </summary>
public sealed class BuildAssistEffect : Effect
{
    /// <summary>Percent added to the builders' rate on sites in the radius.</summary>
    public required int Bonus { get; init; }

    /// <summary>The maximum distance from the unit to the site's footprint.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>Structure archetypes unlocked for the player, as indices into <see cref="ContentSet.Archetypes"/>.</summary>
    public required IReadOnlyList<int> Unlocks { get; init; }
}

/// <summary>Repairs nearby friendly units. Used by the repair system.</summary>
public sealed class RepairEffect : Effect
{
    /// <summary>Hit points restored per period.</summary>
    public required int Rate { get; init; }

    /// <summary>The repair period in ticks.</summary>
    public required int EveryTicks { get; init; }

    /// <summary>The radius.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>Only units with one of these tags (any unit if empty).</summary>
    public required TagSet Affects { get; init; }
}

/// <summary>A permanent stat change for its unit (perks), applied when the unit definition is compiled.</summary>
public sealed class ModifierEffect : Effect
{
    /// <summary>The change.</summary>
    public required Modifier Modifier { get; init; }
}
