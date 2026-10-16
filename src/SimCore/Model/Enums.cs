// The compiled content model: archetypes, components, factions, match setups
// and the stats pipeline. Content files are parsed and checked by
// SimCore.Content; what this namespace holds is immutable, sorted by ID and
// read inside the tick, so it follows the determinism rules (see README.md).
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

    /// <summary>Drones and planes; on the air layer, no ground pathfinding.</summary>
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

/// <summary>Which of its weapons a unit may fire.</summary>
public enum WeaponMode : byte
{
    /// <summary>Any weapon: the best one for the target (the default).</summary>
    Auto,

    /// <summary>Only the first weapon (the primary): an anti-armor soldier keeps to his launcher and never switches to a sidearm.</summary>
    PrimaryOnly,
}

/// <summary>A loadout slot an archetype offers to presets, in firing-preference order.</summary>
public enum SlotKind : byte
{
    /// <summary>The main pick: a weapon or a kit.</summary>
    Primary,

    /// <summary>The second pick.</summary>
    Secondary,
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

    /// <summary>Weapon damage per hit before armor, for every weapon of the unit.</summary>
    Damage,

    /// <summary>Weapon range, in raw <see cref="FixedPoint.Fixed"/> units, for every weapon of the unit.</summary>
    Range,

    /// <summary>Ticks between shots, for every weapon of the unit.</summary>
    Reload,

    /// <summary>Construction rate, in percent of the nominal build speed.</summary>
    BuildRate,

    /// <summary>The vision radius, in raw <see cref="FixedPoint.Fixed"/> units.</summary>
    Vision,
}

/// <summary>Who an aura affects, relative to its source's owner.</summary>
public enum Relation : byte
{
    /// <summary>The source's own units.</summary>
    Ally,

    /// <summary>Other players' units; never neutral ones.</summary>
    Enemy,
}
