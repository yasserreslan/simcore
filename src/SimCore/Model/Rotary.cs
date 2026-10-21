using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// A helicopter's rotary flight (content <c>flight.kind: "rotary"</c> and its <c>flight.rotary</c> block; the
/// helicopters night, 2026-10-21): it hovers, turns on the spot, flies sideways and backwards, speeds up and slows
/// within its limits, follows the terrain low and never needs a runway. Speeds are raw a second (raw a tick times 20,
/// so accelerations are exact integers), heights raw (512 a metre), distances raw (1024 a cell).
/// </summary>
public sealed record RotarySpec
{
    /// <summary>Its top speed (raw a second; its cruise is the archetype's speed).</summary>
    public required long MaxSpeed { get; init; }

    /// <summary>The most it flies sideways or backwards (raw a second).</summary>
    public required long SideSpeed { get; init; }

    /// <summary>How much its velocity may change in a tick (raw a second): its acceleration over 20.</summary>
    public required long AccelPerTick { get; init; }

    /// <summary>Its yaw rate (degrees a second): it turns on the spot at this.</summary>
    public required int YawRate { get; init; }

    /// <summary>The yaw of one tick as a rotation (cos, sin in 1/16384), derived from <see cref="YawRate"/>.</summary>
    public required (int Cos, int Sin) YawStep { get; init; }

    /// <summary>How fast it sinks (raw a tick); it climbs at its flight's climb rate.</summary>
    public required long DescentPerTick { get; init; }

    /// <summary>Its height over the ground (raw) while it attacks with guns and rockets, or hovers in standoff.</summary>
    public required long AttackHeightRaw { get; init; }

    /// <summary>Its height over the ground (raw) hiding behind cover (terrain masking) and flying nap-of-the-earth to it.</summary>
    public required long HideHeightRaw { get; init; }

    /// <summary>The most it rises over the ground (raw) to clear a line of sight from cover (the pop-up).</summary>
    public required long PopMaxRaw { get; init; }

    /// <summary>Where it fires missiles from: this percent of the missile's range from the target.</summary>
    public required int StandoffPercent { get; init; }

    /// <summary>How far (raw) from where it is it looks for a firing position behind cover.</summary>
    public required long CoverSearchRaw { get; init; }

    /// <summary>Gun and rocket runs: it closes to this percent of the weapon's range.</summary>
    public required int GunPercent { get; init; }

    /// <summary>How far (raw) from its post or path it engages on Guard, attack-move and patrol.</summary>
    public required long LeashRaw { get; init; }

    /// <summary>Ticks its rotor takes to spool up before it lifts off (and to run down after it lands).</summary>
    public required int SpoolTicks { get; init; }

    /// <summary>What happens when it is brought down (the owner's standard: out of control).</summary>
    public required OutOfControlSpec OutOfControl { get; init; }

    /// <summary>Its crew: how many, their survival chance in a crash landing and the unit a survivor becomes.</summary>
    public required HeliCrewSpec Crew { get; init; }

    /// <summary>Its rider benches (soldiers carried outside), or null.</summary>
    public RidersSpec? Riders { get; init; }
}

/// <summary>
/// Out of control (the owner's standard for every helicopter): the hit that would bring it down leaves it at
/// <paramref name="HpPercent"/> of its max HP draining to nothing over <paramref name="Ticks"/>; it flies itself to the
/// nearest friendly helipad or airfield at <paramref name="SpeedPercent"/> of its cruise, spinning at
/// <paramref name="SpinRate"/> degrees a second and sinking toward <paramref name="EndHeightRaw"/> over the ground; it
/// lands hard there and survives, or crash-lands where the time runs out; any further hit destroys it in the air.
/// </summary>
/// <param name="Ticks">How long it stays in the air out of control.</param>
/// <param name="HpPercent">The HP it is left with (percent of its max), draining evenly to 0.</param>
/// <param name="SpeedPercent">Its speed toward a pad, percent of its cruise.</param>
/// <param name="SpinRate">Its yaw spin, degrees a second (the client's wobble follows it).</param>
/// <param name="EndHeightRaw">The height over the ground it sinks to by the end of the time (raw).</param>
public sealed record OutOfControlSpec(int Ticks, int HpPercent, int SpeedPercent, int SpinRate, long EndHeightRaw);

/// <summary>A helicopter's crew: <paramref name="Count"/> soldiers who each survive a crash landing with <paramref name="Survive"/>
/// percent and step out as <paramref name="Unit"/> (an archetype index; -1: nobody).</summary>
public sealed record HeliCrewSpec(int Count, int Survive, int Unit);

/// <summary>
/// Rider benches (the light helicopter): up to <paramref name="Seats"/> friendly soldiers with one of
/// <paramref name="Tags"/> ride outside; they never fire. They board within <paramref name="BoardRange"/> of it on the
/// ground; it drops them by landing, or hovering <paramref name="DropHeightRaw"/> over the ground, one every
/// <paramref name="DropTicks"/>. Each survives a crash landing with <paramref name="Survive"/> percent.
/// </summary>
public sealed record RidersSpec(int Seats, TagSet Tags, Fixed BoardRange, long DropHeightRaw, int DropTicks, int Survive);

/// <summary>
/// A helipad (content <c>helipad</c>; a structure): one helicopter at a time builds on it (its production), parks,
/// and takes its service: fuel, ammunition and HP at these percents of the helicopter's own 1x rates.
/// </summary>
public sealed record HelipadSpec(int RefuelPercent, int RearmPercent, int RepairPercent);
