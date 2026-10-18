using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// The crew rules of a vehicle: one driver seat, and optionally passenger seats in the back.
/// Without a driver the vehicle can neither move nor fire; passengers only ride.
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

    /// <summary>
    /// Passenger seats besides the driver's (0: none). Passengers can't be seen, hit or fire; when
    /// the vehicle is destroyed each bails out by the same <see cref="Survival"/> and
    /// <see cref="ExitDamage"/> rules as the driver.
    /// </summary>
    public int Passengers { get; init; }

    /// <summary>Only units with one of these tags may ride as passengers (any ground unit if empty).</summary>
    public TagSet PassengerTags { get; init; }
}

/// <summary>
/// A factory: it produces the faction's archetypes with one of <see cref="Tags"/> (vehicles), in
/// <see cref="Bays"/> bays at once, from a queue of up to <see cref="QueueMax"/> orders; a finished
/// vehicle drives out crewed through a free one of its <see cref="Doors"/> to the rally point.
/// </summary>
public sealed class ProductionSpec
{
    /// <summary>It produces archetypes with one of these tags.</summary>
    public required TagSet Tags { get; init; }

    /// <summary>Bays producing in parallel (each finished unit then takes a free door).</summary>
    public required int Bays { get; init; }

    /// <summary>Orders waiting and in production, together, at most.</summary>
    public required int QueueMax { get; init; }

    /// <summary>Ticks a door takes to open, and to close.</summary>
    public required int DoorTicks { get; init; }

    /// <summary>Ticks a finished vehicle waits for its exit lane before it goes anyway.</summary>
    public required int PushAfter { get; init; }

    /// <summary>The preset or archetype ID that drives what it produces.</summary>
    public required string Crew { get; init; }

    /// <summary>Per door, where its units come out (tenths of a cell from the footprint's top-left); empty for one door per bay at the default places.</summary>
    public IReadOnlyList<(int X, int Y)> Exits { get; init; } = [];

    /// <summary>Per door, where its units appear inside, the bay floor behind it (tenths of a cell from the top-left); empty for 4 cells inside the front wall.</summary>
    public IReadOnlyList<(int X, int Y)> BayPoints { get; init; } = [];

    /// <summary>The doors: one per exit, or one per bay without exits.</summary>
    public int Doors => Exits.Count > 0 ? Exits.Count : Bays;

    /// <summary>The default rally point (tenths of a cell from the top-left), or null for 5 cells in front.</summary>
    public (int X, int Y)? Rally { get; init; }

    /// <summary>Below this percent of its HP it produces nothing (0: never stops).</summary>
    public int MinHpPercent { get; init; }
}

/// <summary>How workers build a structure: a site that rises with their work.</summary>
public sealed class ConstructionSpec
{
    /// <summary>Percent of max HP a fresh site has (it rises with progress to max HP).</summary>
    public required int StartHp { get; init; }

    /// <summary>The progress percents where construction stages 1 to 4 begin.</summary>
    public required IReadOnlyList<int> Stages { get; init; }

    /// <summary>Percent of the cost a cancelled site refunds.</summary>
    public required int Refund { get; init; }

    /// <summary>Percent of a builder's rate for the 1st, 2nd, ... worker on one site (the last for any more).</summary>
    public required IReadOnlyList<int> Helpers { get; init; }

    /// <summary>On terrain: the cells over which the ground eases back from the site's pad.</summary>
    public required int PadBlend { get; init; }
}

/// <summary>A building soldiers can garrison: they fire from inside, protected, until it falls.</summary>
public sealed class GarrisonSpec
{
    /// <summary>How many units fit inside when intact.</summary>
    public required int Capacity { get; init; }

    /// <summary>Units with one of these tags may enter (any unit if empty).</summary>
    public required TagSet Tags { get; init; }

    /// <summary>How close to a door a unit must walk to get in.</summary>
    public required Fixed EnterRange { get; init; }

    /// <summary>Range added to the weapons of units inside.</summary>
    public required Fixed RangeBonus { get; init; }

    /// <summary>Vision added to units inside.</summary>
    public required Fixed VisionBonus { get; init; }

    /// <summary>Percent of an explosive hit on the building that every unit inside takes too.</summary>
    public required int OccupantDamage { get; init; }

    /// <summary>The warheads (indexes) that reach the units inside.</summary>
    public required IReadOnlyList<int> OccupantWarheads { get; init; }

    /// <summary>HP percent thresholds of the damage stages: above the first is stage 0, and so on; 0 HP is the last stage (rubble).</summary>
    public required IReadOnlyList<int> Stages { get; init; }

    /// <summary>Percent of <see cref="Capacity"/> left at each stage (one per stage before rubble).</summary>
    public required IReadOnlyList<int> StageCapacity { get; init; }

    /// <summary>Percent of max HP a unit loses when thrown out because the building lost room.</summary>
    public required int EjectDamage { get; init; }

    /// <summary>Percent chance (world generator) each unit inside survives the collapse.</summary>
    public required int CollapseSurvival { get; init; }

    /// <summary>Percent of max HP a collapse survivor is left with.</summary>
    public required int CollapseHp { get; init; }

    /// <summary>Door cells, relative to the footprint's top-left cell (outside the footprint).</summary>
    public required IReadOnlyList<(int X, int Y)> Doors { get; init; }

    /// <summary>
    /// An owned post (an observation post): it belongs to its builder for good, only its side may
    /// enter (once it is finished), and it never turns neutral when empty. False for town
    /// buildings, which are placed neutral and belong to whoever is inside.
    /// </summary>
    public bool Owned { get; init; }

    /// <summary>
    /// Occupants with one of these tags make the building a launch point (the observation post with
    /// a sniper inside launches the scout drone): its abilities can be used only while one is inside
    /// (<see cref="Sim.World.PostLauncher"/>). Empty: no such rule.
    /// </summary>
    public TagSet LauncherTags { get; init; }
}

/// <summary>
/// Vision from high ground (the observation post): the vision radius grows by
/// <see cref="PercentPerMetre"/> for every metre the unit stands above the mean ground height
/// within <see cref="RadiusCells"/> cells around it, up to <see cref="MaxPercent"/>. Only on terrain.
/// </summary>
public sealed class HighGroundSpec
{
    /// <summary>Percent more vision per metre above the surroundings.</summary>
    public required int PercentPerMetre { get; init; }

    /// <summary>The largest bonus, in percent.</summary>
    public required int MaxPercent { get; init; }

    /// <summary>The half-size, in cells, of the square whose mean ground height is the reference.</summary>
    public required int RadiusCells { get; init; }
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

    /// <summary>Ticks a vehicle takes to reach top speed from a stop; 0 for the default (5).</summary>
    public int AccelTicks { get; init; }

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

    /// <summary>
    /// How far it detects air targets (one-way attack drones), a slant (3D) distance from its eye;
    /// zero for none. Air defence tracks only what it detects (docs: air defence design).
    /// </summary>
    public Fixed AirVision { get; init; }

    /// <summary>Ticks it lives before it is removed (a drone's battery); zero lives forever.</summary>
    public required int Lifetime { get; init; }

    /// <summary>Ticks it lasts once its weapons are empty (a single-use drone station); 0 never.</summary>
    public int ExpireWhenEmpty { get; init; }

    /// <summary>How high (raw, 512 a metre) what flies down at it is stopped: a drone's dive.</summary>
    public long HeightRaw { get; init; }

    /// <summary>Vision from high ground, or null (most units).</summary>
    public HighGroundSpec? HighGround { get; init; }

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

    /// <summary>The building's garrison rules, or null for anything that can't be garrisoned.</summary>
    public GarrisonSpec? Garrison { get; init; }

    /// <summary>How workers build it, or null for a structure that can't be built.</summary>
    public ConstructionSpec? Construction { get; init; }

    /// <summary>What it produces (a factory), or null.</summary>
    public ProductionSpec? Production { get; init; }

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
