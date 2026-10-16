using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// One typed effect of a component. Each kind maps to exactly one behavior
/// system in the sim; adding a mechanic means adding an effect kind and its
/// system.
/// </summary>
public abstract class Effect;

/// <summary>
/// A weapon. Used by the combat system, and by the projectile system for
/// weapons whose shots travel. A unit may carry several; it fires the one
/// that does the most damage per tick to the target's armor class.
/// </summary>
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

    /// <summary>The layers it can hit; the warhead must also be able to target the armor class.</summary>
    public required Layers Targets { get; init; }

    /// <summary>
    /// How far a shot travels per tick. Zero hits instantly; otherwise each shot
    /// is a projectile flying to where the target was at launch, so it can miss
    /// a moving target.
    /// </summary>
    public required Fixed ProjectileSpeed { get; init; }

    /// <summary>Shots per trigger pull: a launcher fires a salvo of several rockets, then reloads.</summary>
    public required int SalvoSize { get; init; }

    /// <summary>Ticks between the shots of a salvo.</summary>
    public required int SalvoInterval { get; init; }

    /// <summary>Salvos carried before it needs rearming (rockets, with <see cref="AmmoPerRocket"/>); zero means unlimited.</summary>
    public required int Ammo { get; init; }

    /// <summary>The minimum firing distance (artillery can't hit what is too close); zero for none.</summary>
    public Fixed MinRange { get; init; }

    /// <summary>Whether ammo counts single rockets (spent per shot, a salvo stops when empty) rather than salvos.</summary>
    public bool AmmoPerRocket { get; init; }

    /// <summary>Splash damage at the centre and at the edge of the splash, in percent of the damage.</summary>
    public int SplashCenter { get; init; } = 100;

    /// <summary>Splash damage at the edge of the splash, in percent of the damage.</summary>
    public int SplashEdge { get; init; } = 50;

    /// <summary>
    /// Set when only a supply depot reloads it (not ammo trucks): one round every
    /// <see cref="DepotReload.EveryTicks"/> while in a depot's radius, starting
    /// <see cref="DepotReload.AfterFireTicks"/> after the unit last fired, never while firing.
    /// </summary>
    public DepotReload? DepotReload { get; init; }
}

/// <summary>How a weapon reloads at a supply depot.</summary>
/// <param name="EveryTicks">Ticks per round reloaded.</param>
/// <param name="AfterFireTicks">Ticks after the last shot before reloading starts.</param>
public sealed record DepotReload(int EveryTicks, int AfterFireTicks);

/// <summary>
/// An aura around its unit: stat modifiers for units in the radius, and
/// healing every <see cref="EveryTicks"/> ticks. Loaded, but no system runs it
/// yet; no Phase 1 content uses it.
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

    /// <summary>The most entities one unit may have placed at a time; placing another removes its oldest.</summary>
    public required int MaxActive { get; init; }
}

/// <summary>
/// Launches an autonomous unit, like an FPV drone, that flies to a target unit
/// or cell on its own. Charges are restocked by supply. Used by the ability system.
/// </summary>
public sealed class LaunchEffect : Effect
{
    /// <summary>The archetype launched, as an index into <see cref="ContentSet.Archetypes"/>.</summary>
    public required int Archetype { get; init; }

    /// <summary>How many it carries when full.</summary>
    public required int Charges { get; init; }

    /// <summary>Ticks between launches.</summary>
    public required int Cooldown { get; init; }

    /// <summary>The maximum distance from the unit to the target when launching.</summary>
    public required Fixed Range { get; init; }
}

/// <summary>
/// Makes its unit explode on contact with the target it was launched at. Used
/// by the strike system, for drones.
/// </summary>
public sealed class StrikeEffect : Effect
{
    /// <summary>How close to the target the unit must get to explode.</summary>
    public required Fixed Contact { get; init; }

    /// <summary>Damage before armor.</summary>
    public required int Damage { get; init; }

    /// <summary>The damage type.</summary>
    public required Warhead Warhead { get; init; }

    /// <summary>The blast radius.</summary>
    public required Fixed Splash { get; init; }
}

/// <summary>
/// Rearms friendly units in a radius (launcher salvos, drone and trap charges)
/// and makes them reload faster. A supplier with limited stock spends it and is
/// restocked by suppliers that restock. Used by the supply system.
/// </summary>
public sealed class SupplyEffect : Effect
{
    /// <summary>The radius.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>The rearm period in ticks.</summary>
    public required int EveryTicks { get; init; }

    /// <summary>Salvos or charges given to each stock of each unit per period.</summary>
    public required int Amount { get; init; }

    /// <summary>Salvos and charges carried when full; zero means unlimited.</summary>
    public required int Stock { get; init; }

    /// <summary>Stock given to each friendly supplier with limited stock in the radius per period.</summary>
    public required int Restock { get; init; }

    /// <summary>Percent added to the reload speed of friendly units in the radius.</summary>
    public required int ReloadBonus { get; init; }
}

/// <summary>
/// Fires once when an enemy ground unit comes within the radius, then removes
/// its entity. Flying units never set it off. Used by the trigger system, for
/// trap archetypes.
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

/// <summary>Builds construction sites. Loaded, but no system runs it until Phase 2.</summary>
public sealed class BuilderEffect : Effect
{
    /// <summary>Construction rate in percent; 100 builds at the archetype's nominal build time.</summary>
    public required int Rate { get; init; }

    /// <summary>The maximum distance from the unit to the site's footprint.</summary>
    public required Fixed Range { get; init; }
}

/// <summary>
/// Speeds up nearby construction and unlocks structure variants. Loaded, but
/// no system runs it until Phase 2.
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

/// <summary>Repairs nearby friendly units, never above max HP. Used by the repair system.</summary>
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
