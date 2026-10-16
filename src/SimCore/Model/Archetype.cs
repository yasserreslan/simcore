using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// The crew rules of a vehicle: one driver seat. Without a driver the vehicle
/// can neither move nor fire.
/// </summary>
public sealed class CrewSpec
{
    /// <summary>Only units with one of these tags may drive (any unit if empty).</summary>
    public required TagSet Tags { get; init; }

    /// <summary>How close a unit must walk to climb in.</summary>
    public required Fixed EnterRange { get; init; }

    /// <summary>The percent chance, drawn from the world's generator, that the driver survives the vehicle's destruction.</summary>
    public required int Survival { get; init; }

    /// <summary>The percent of a surviving driver's max HP lost when bailing out.</summary>
    public required int ExitDamage { get; init; }
}

/// <summary>
/// A kind of entity: infantry, vehicle, drone, structure or trap. Players turn
/// archetypes with slots into units through presets; archetypes without slots
/// always carry exactly their fixed <see cref="Components"/>.
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

    /// <summary>The armor class, as an index into <see cref="ContentSet.ArmorClasses"/>.</summary>
    public required int Armor { get; init; }

    /// <summary>Starting hit points.</summary>
    public required int MaxHp { get; init; }

    /// <summary>Distance per tick; zero means it cannot move.</summary>
    public required Fixed Speed { get; init; }

    /// <summary>Facings per tick.</summary>
    public required int TurnRate { get; init; }

    /// <summary>The radius projectiles collide with (a circle; structures use their footprint).</summary>
    public required Fixed Radius { get; init; }

    /// <summary>Ticks to set up (deploy) before it can fire; 0 for units that fire at once.</summary>
    public required int DeployTicks { get; init; }

    /// <summary>Ticks to pack up (stow) before it can move after deploying.</summary>
    public required int StowTicks { get; init; }

    /// <summary>The weapon mode units of this archetype start with (players can change it per unit).</summary>
    public required WeaponMode WeaponMode { get; init; }

    /// <summary>Whether the weapon aims independently of the hull (and can fire on the move).</summary>
    public required bool Turret { get; init; }

    /// <summary>Whether enemies can't see or target it until they reveal it (traps).</summary>
    public required bool Hidden { get; init; }

    /// <summary>
    /// Whether this structure variant may only be built by a player whose
    /// presets include a component that unlocks it.
    /// </summary>
    public required bool Locked { get; init; }

    /// <summary>The build cost in credits.</summary>
    public required int Cost { get; init; }

    /// <summary>How far it sees. Weapons only fire at enemies some friendly unit sees.</summary>
    public required Fixed Vision { get; init; }

    /// <summary>How far it reveals hidden enemy entities such as traps; zero for none.</summary>
    public required Fixed Detect { get; init; }

    /// <summary>Ticks it lives before it is removed (a drone's battery); zero lives forever.</summary>
    public required int Lifetime { get; init; }

    /// <summary>
    /// The components a preset may pick for each slot, by <see cref="SlotKind"/>
    /// value, as indices into <see cref="ContentSet.Components"/>. An empty list
    /// means the archetype has no such slot; an archetype without any slot has
    /// no presets ("no loadout").
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<int>> SlotChoices { get; init; }

    /// <summary>The components every unit of the archetype carries, as indices into <see cref="ContentSet.Components"/>.</summary>
    public required IReadOnlyList<int> Components { get; init; }

    /// <summary>The driver seat, or null for anything that needs no crew.</summary>
    public required CrewSpec? Crew { get; init; }

    /// <summary>The footprint width in cells; zero for anything that is not a structure.</summary>
    public required int FootprintW { get; init; }

    /// <summary>The footprint height in cells.</summary>
    public required int FootprintH { get; init; }

    /// <summary>Ticks to build with one builder at 100% rate.</summary>
    public required int BuildTime { get; init; }

    /// <summary>Whether it is a structure: it has a footprint and can be built.</summary>
    public bool IsStructure => FootprintW > 0;

    /// <summary>Whether players build presets for it: it has at least one slot.</summary>
    public bool HasSlots
    {
        get
        {
            foreach (var s in SlotChoices)
            {
                if (s.Count > 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
