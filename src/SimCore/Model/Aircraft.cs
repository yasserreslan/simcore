using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// An aircraft's size class (the game repo's docs/airfield-design.md, section 1.2). Ordered: an
/// airfield type serves its largest class and every smaller one, never a larger one.
/// </summary>
public enum AircraftClass : byte
{
    /// <summary>Small: drones such as the Watcher (forward airfield).</summary>
    S,

    /// <summary>Medium: light jets (forward airfield, with a longer runway minimum).</summary>
    M,

    /// <summary>Large: needs a main air base.</summary>
    L,

    /// <summary>Extra large: transports and bombers, an airport.</summary>
    XL,
}

/// <summary>How an aircraft lands.</summary>
public enum LandingKind : byte
{
    /// <summary>On a runway, then it taxis to a station.</summary>
    Runway,

    /// <summary>Straight down on a station or helipad, no runway (helicopters; reserved, not built yet).</summary>
    Vertical,
}

/// <summary>
/// The generic aircraft block (content <c>aircraft</c>): what an airfield needs to know about an
/// aircraft to land, park, service and launch it. Nothing in the airfield rules is specific to
/// one aircraft; every aircraft is this data. Distances are <see cref="Fixed"/> (cells), speeds
/// raw units per tick, accelerations the change of that speed per second, heights raw (512 a metre).
/// </summary>
public sealed class AircraftSpec
{
    /// <summary>Its size class: which airfield types may serve it.</summary>
    public required AircraftClass SizeClass { get; init; }

    /// <summary>Its wingspan.</summary>
    public required Fixed Span { get; init; }

    /// <summary>Its length, nose to tail.</summary>
    public required Fixed Length { get; init; }

    /// <summary>How it lands.</summary>
    public required LandingKind Landing { get; init; }

    /// <summary>
    /// The armor class it has while on the ground at an airfield (an index into
    /// <see cref="ContentSet.ArmorClasses"/>), or -1: its own armor everywhere.
    /// </summary>
    public int GroundArmor { get; init; } = -1;

    /// <summary>The shortest open stretch of runway it can land on (0 for vertical landing).</summary>
    public required Fixed MinLandRun { get; init; }

    /// <summary>The shortest open stretch of runway it can take off from (0 for vertical landing).</summary>
    public required Fixed MinTakeoffRun { get; init; }

    /// <summary>Approach: how far out along the extended centre line the final approach begins (the final fix).</summary>
    public required Fixed FixDistance { get; init; }

    /// <summary>Approach: its height over the runway at the final fix (raw). The glide slope is this over the distance from the fix to the touchdown point.</summary>
    public required long FixHeightRaw { get; init; }

    /// <summary>Approach: how far past the threshold it touches down.</summary>
    public required Fixed Touchdown { get; init; }

    /// <summary>Approach: the length of the flare before touchdown (the descent rounds off over it).</summary>
    public required Fixed Flare { get; init; }

    /// <summary>Roll-out deceleration after touchdown (per-tick speed lost per second).</summary>
    public required Fixed RolloutDecel { get; init; }

    /// <summary>Hard braking, to stop short of a fresh crater (per-tick speed lost per second).</summary>
    public required Fixed Brake { get; init; }

    /// <summary>Speed on the final approach.</summary>
    public required Fixed ApproachSpeed { get; init; }

    /// <summary>Speed at touchdown.</summary>
    public required Fixed TouchdownSpeed { get; init; }

    /// <summary>Taxi speed.</summary>
    public required Fixed TaxiSpeed { get; init; }

    /// <summary>Taxi speed through turns.</summary>
    public required Fixed TurnSpeed { get; init; }

    /// <summary>Pushback speed (out of a nose-in station).</summary>
    public required Fixed PushbackSpeed { get; init; }

    /// <summary>The speed at which it leaves the ground (rotation).</summary>
    public required Fixed TakeoffSpeed { get; init; }

    /// <summary>Acceleration on the takeoff roll (per-tick speed gained per second).</summary>
    public required Fixed TakeoffAccel { get; init; }

    /// <summary>Service: ticks to fill an empty tank at a station at 1x.</summary>
    public required int RefuelTicks { get; init; }

    /// <summary>Service: ticks to load one round at 1x.</summary>
    public required int RearmTicksPerRound { get; init; }

    /// <summary>Service: HP repaired a second at 1x.</summary>
    public required int RepairHpPerSecond { get; init; }

    /// <summary>How far it sees while on the ground.</summary>
    public required Fixed GroundVision { get; init; }

    /// <summary>Its eye height while on the ground (raw).</summary>
    public required long GroundEyeRaw { get; init; }

    /// <summary>Ticks of endurance it keeps for the approach and holding when its home is an airfield.</summary>
    public required int HoldReserveTicks { get; init; }

    /// <summary>Whether, serviced, it takes off again by itself and flies back to where it orbited.</summary>
    public required bool Sortie { get; init; }

    /// <summary>Its approach pattern (E9): straight in from the hold (the drones), or the overhead break (the jets).</summary>
    public ApproachPattern Pattern { get; init; }

    /// <summary>Overhead: how far beyond the runway's approach end the initial point lies on the extended centre line.</summary>
    public Fixed InitialDistance { get; init; }

    /// <summary>Overhead: its height over the runway from the initial point to the perch (raw).</summary>
    public long PatternHeightRaw { get; init; }

    /// <summary>Overhead: its speed from the initial point to the break (raw a tick).</summary>
    public Fixed BreakSpeed { get; init; }

    /// <summary>After touchdown it holds its nose up (aerobraking) until it slows to this (raw a tick); 0: no aerobraking.</summary>
    public Fixed AerobrakeSpeed { get; init; }

    /// <summary>
    /// Takeoff acceleration on military (dry) power (per-tick speed gained per second); 0: one takeoff
    /// acceleration (<see cref="TakeoffAccel"/>, military). With it, <see cref="TakeoffAccel"/> is the
    /// afterburner's, used when the military roll is longer than half the open stretch (E9).
    /// </summary>
    public Fixed MilitaryAccel { get; init; }

    /// <summary>
    /// The corridor it needs beyond the runway's approach end (its approach from the initial point and its
    /// climb-out; E9): it uses an airfield only where that much lies on the map. 0: the airfield type's own.
    /// </summary>
    public Fixed CorridorNeed { get; init; }

    /// <summary>
    /// Overhead (derived from its flight): the radius of its break and base turns, the larger of its tightest
    /// turns at its break and approach speeds (the downwind leg lies twice this off the centre line).
    /// </summary>
    public Fixed PatternRadius { get; init; }

    /// <summary>Overhead (derived from its flight): the radius of its join onto the initial point (5/4 of its tightest turn at cruise).</summary>
    public Fixed JoinRadius { get; init; }

    /// <summary>
    /// Derived from its flight (E11): its turn radius at its approach speed (a fixed wing's tightest within its
    /// envelope), which sizes a straight-in join onto the final; 0 without a flight block. Not hashed (its inputs are).
    /// </summary>
    public Fixed ApproachRadius { get; init; }
}

/// <summary>An aircraft's approach pattern (E9).</summary>
public enum ApproachPattern : byte
{
    /// <summary>Straight in: from the hold a tangent-circle join onto a long final (the drones).</summary>
    StraightIn,

    /// <summary>
    /// The overhead break (decision D17): from the hold to an initial point on the extended centre line at
    /// pattern height and break speed, a run-in along the runway, a level 180-degree break turn at midfield
    /// away from the rows, a downwind leg slowing to approach speed with the gear coming down, a descending
    /// 180-degree base turn from the perch onto a short final, then the glide, flare and touchdown.
    /// </summary>
    Overhead,
}
