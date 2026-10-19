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
}
