// The compiled content model: archetypes, components, factions and the stats
// pipeline. Content files are parsed and checked by SimCore.Content; what this
// namespace holds is immutable, sorted by ID and read inside the tick, so it
// follows the determinism rules (see README.md).
namespace SimCore.Model;

/// <summary>How an archetype moves.</summary>
public enum Locomotor : byte
{
    /// <summary>Does not move: structures and traps.</summary>
    None,

    /// <summary>Infantry.</summary>
    Foot,

    /// <summary>Wheeled vehicles.</summary>
    Wheeled,

    /// <summary>Tracked vehicles.</summary>
    Tracked,

    /// <summary>Drones and planes; on the air layer.</summary>
    Flying,
}

/// <summary>The layers an entity can be on, or a weapon can hit.</summary>
[Flags]
public enum Layers : byte
{
    /// <summary>No layer.</summary>
    None = 0,

    /// <summary>The ground: everything that does not fly.</summary>
    Ground = 1,

    /// <summary>The air: flying units.</summary>
    Air = 2,
}

/// <summary>An armor class, the column of the warhead damage table.</summary>
public enum Armor : byte
{
    /// <summary>Infantry.</summary>
    None,

    /// <summary>Light vehicles.</summary>
    Light,

    /// <summary>Tanks and heavy vehicles.</summary>
    Heavy,

    /// <summary>Structures.</summary>
    Building,
}

/// <summary>The loadout slot a component goes into.</summary>
public enum SlotKind : byte
{
    /// <summary>A weapon.</summary>
    Weapon,

    /// <summary>An ability: auras, placers, building, repair.</summary>
    Ability,

    /// <summary>A passive stat perk.</summary>
    Perk,
}

/// <summary>A stat the modifier pipeline can change.</summary>
public enum Stat : byte
{
    /// <summary>Maximum hit points.</summary>
    MaxHp,

    /// <summary>Distance per tick, in raw <see cref="FixedPoint.Fixed"/> units.</summary>
    Speed,

    /// <summary>Facings per tick.</summary>
    TurnRate,

    /// <summary>The build cost in credits.</summary>
    Cost,

    /// <summary>Weapon damage per hit before armor.</summary>
    Damage,

    /// <summary>Weapon range, in raw <see cref="FixedPoint.Fixed"/> units.</summary>
    Range,

    /// <summary>Ticks between shots.</summary>
    Reload,

    /// <summary>Construction rate, in percent of the nominal build speed.</summary>
    BuildRate,
}

/// <summary>Who an aura affects, relative to its source's owner.</summary>
public enum Relation : byte
{
    /// <summary>The source's own units.</summary>
    Ally,

    /// <summary>Other players' units; never neutral ones.</summary>
    Enemy,
}
