using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>
/// An aircraft's flight phase at an airfield (the game repo's docs/airfield-design.md, section
/// 2.2). <see cref="Airborne"/> is free flight (the scout system); the rest run the airfield's
/// landing, taxi and takeoff rules. Grounded: <see cref="Rollout"/> to <see cref="TakeoffRoll"/>.
/// </summary>
public enum FlightPhase : byte
{
    /// <summary>Free flight: orbiting, striking, flying home (no airfield business).</summary>
    Airborne,

    /// <summary>Waiting at its airfield's hold point, stacked by its place in the landing queue.</summary>
    Holding,

    /// <summary>Cleared to land (it holds the runway lock): the join, then the final approach and flare.</summary>
    Final,

    /// <summary>On the runway after touchdown, slowing toward its exit connector.</summary>
    Rollout,

    /// <summary>Taxiing in along the taxi graph to its station's row.</summary>
    TaxiIn,

    /// <summary>Following its station's park-in path to the stop mark.</summary>
    Parking,

    /// <summary>At its station: fuel does not burn; it refuels (M2) and waits for its sortie or an order.</summary>
    Parked,

    /// <summary>Pushed back out of a nose-in station, its facing held.</summary>
    PushBack,

    /// <summary>Taxiing out to the runway's holding point.</summary>
    TaxiOut,

    /// <summary>At the holding point waiting for the runway, then lining up on it once cleared.</summary>
    LineUp,

    /// <summary>Accelerating down the runway to its takeoff speed.</summary>
    TakeoffRoll,

    /// <summary>Climbing out straight ahead; it keeps the runway lock for the type's release time.</summary>
    Climb,
}

/// <summary>
/// An aircraft's airfield state (<see cref="Unit.AirOps"/>): null while it flies free. Hashed while
/// present. Read-only for renderers; the position, height and heading are the unit's own
/// (<see cref="Unit.Pos"/>, <see cref="Unit.Altitude"/>, <see cref="Unit.Facing"/>, and per tick
/// <see cref="Unit.Vel"/> and <see cref="Unit.VelZ"/>).
/// </summary>
public sealed class AirOps
{
    internal AirOps(uint airfield, Cell returnCell)
    {
        Airfield = airfield;
        ReturnCell = returnCell;
    }

    /// <summary>Its phase.</summary>
    public FlightPhase Phase { get; internal set; }

    /// <summary>The airfield it uses (<see cref="World.Airfield(uint)"/>).</summary>
    public uint Airfield { get; internal set; }

    /// <summary>The station unit it is reserved for, parked at or leaving; 0 while it holds without one.</summary>
    public uint Station { get; internal set; }

    /// <summary>Its speed along its way, raw units a tick times <see cref="World.TicksPerSecond"/> (exact integer accelerations).</summary>
    public long Speed { get; internal set; }

    /// <summary>Its speed along its way, per tick.</summary>
    public Fixed GroundSpeed => Fixed.FromRaw(Speed / World.TicksPerSecond);

    /// <summary>A step within the phase (the join and the final; waiting or cleared at the line-up; a pivot).</summary>
    public int Leg { get; internal set; }

    /// <summary>The side of the extended centre line it joins the final from (+1 or -1).</summary>
    public int Side { get; internal set; }

    /// <summary>Ticks it has waited at a lane mouth.</summary>
    public int Wait { get; internal set; }

    /// <summary>The tick of wheels-up (in the climb); the tick it reached the holding point (at the line-up).</summary>
    public uint Since { get; internal set; }

    /// <summary>Where it goes back to orbit after its sortie (its last orbit centre, or its new Move order).</summary>
    public Cell ReturnCell { get; internal set; }

    /// <summary>Stop while parked: no automatic sortie until its next order.</summary>
    public bool Hold { get; internal set; }

    /// <summary>An order came while it was landing: it leaves as soon as it is parked, serviced or not.</summary>
    public bool Launch { get; internal set; }

    // The refuel remainder (Lifetime units times RefuelTicks).
    internal long FuelAcc { get; set; }

    /// <summary>Its way on the ground (map points; corners rounded), from <see cref="PathIdx"/> on still ahead.</summary>
    public IReadOnlyList<Vec> Path => PathPts;

    internal List<Vec> PathPts { get; } = [];

    // Per path point: a point of a corner turning more than 30 degrees (it is taken at turn speed).
    internal List<bool> PathTurn { get; } = [];

    /// <summary>The next point of <see cref="Path"/> it moves to.</summary>
    public int PathIdx { get; internal set; }

    // Path marks (indices into the path): where the next phase begins (MarkA: off the runway onto
    // the exit connector, or onto the runway at the line-up; MarkB: the turn into the station's
    // park-in path) and the lane mouth (MarkMouth: where an inbound aircraft waits, the point an
    // outbound one has left its lane by). -1 when unused.
    internal int MarkA { get; set; } = -1;

    internal int MarkB { get; set; } = -1;

    internal int MarkMouth { get; set; } = -1;

    /// <summary>Whether it is on the ground (<see cref="FlightPhase.Rollout"/> to <see cref="FlightPhase.TakeoffRoll"/>).</summary>
    public bool Grounded => Phase is >= FlightPhase.Rollout and <= FlightPhase.TakeoffRoll;
}

/// <summary>What an <see cref="AirfieldEvent"/> reports.</summary>
public enum AirfieldEventKind : byte
{
    /// <summary>Cleared to land: it took the runway lock and began its final.</summary>
    Cleared,

    /// <summary>It went around (misaligned at the final fix) and rejoined the landing queue at its head.</summary>
    GoAround,

    /// <summary>Wheels on the runway.</summary>
    Touchdown,

    /// <summary>It turned off the runway onto its exit connector (the lock is free).</summary>
    RunwayVacated,

    /// <summary>It stopped at its station's stop mark.</summary>
    Parked,

    /// <summary>It left its station.</summary>
    Departed,

    /// <summary>Cleared to take off: it took the runway lock to line up.</summary>
    LinedUp,

    /// <summary>Wheels off the runway.</summary>
    WheelsUp,
}

/// <summary>One airfield event of the last tick, for renderers and logs (a tyre puff at touchdown). Not game state.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Aircraft">The aircraft.</param>
/// <param name="Airfield">Its airfield.</param>
/// <param name="At">Where.</param>
public readonly record struct AirfieldEvent(AirfieldEventKind Kind, uint Aircraft, uint Airfield, Vec At);
