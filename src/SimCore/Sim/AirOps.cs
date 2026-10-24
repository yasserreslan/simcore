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

    /// <summary>At its station: fuel does not burn; it is serviced (refuelled; rearmed and repaired only on a Rearm order) and waits for its sortie or an order.</summary>
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
/// Why an aircraft is heading home or at its airfield (<see cref="Unit.HomeReason"/>; the owner's
/// flight rules, 2026-10-19). Only <see cref="Refuel"/> is automatic: an aircraft never goes home
/// because it is out of ammunition, and it rearms only on a Rearm order.
/// </summary>
public enum HomeReason : byte
{
    /// <summary>Bingo fuel (automatic): it refuels only (no rearm, no repair) and resumes its previous task.</summary>
    Refuel,

    /// <summary>A Rearm order (command 29): refuelled, rearmed and repaired, then it resumes its previous task.</summary>
    Rearm,

    /// <summary>A ReturnToBase order (command 28): refuelled only, then held parked until its next order.</summary>
    Recall,

    /// <summary>
    /// A Deliver order (command 39, logistics): it lands at an airfield with a stock, unloads its pallets into it, takes on fuel
    /// and flies home to load again.
    /// </summary>
    Deliver,
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

    /// <summary>
    /// The pace its phase runs at, percent of its own (2026-10-24, the owner's faster airfield: the airfield type's tempo
    /// while it leaves or returns, else 100): its <see cref="Speed"/> is paced already; for renderers (wheel spin, flap and
    /// gear timing) and the speed brakes' thresholds.
    /// </summary>
    public int Pace { get; internal set; } = 100;

    /// <summary>A step within the phase (the join and the final; waiting or cleared at the line-up; a pivot).</summary>
    public int Leg { get; internal set; }

    /// <summary>The side of the extended centre line it joins the final from (+1 or -1).</summary>
    public int Side { get; internal set; }

    /// <summary>Ticks it has waited at a lane mouth.</summary>
    public int Wait { get; internal set; }

    /// <summary>
    /// The tick of wheels-up (in the climb); the tick it reached the holding point (at the line-up); the tick the
    /// airfield's producer rolled it out into its station (parked: a renderer opens a heavy bay's door for it then).
    /// </summary>
    public uint Since { get; internal set; }

    /// <summary>Where it goes back to orbit after its sortie (its last orbit centre, or its new Move order).</summary>
    public Cell ReturnCell { get; internal set; }

    /// <summary>Stop while parked: no automatic sortie until its next order.</summary>
    public bool Hold { get; internal set; }

    /// <summary>An order came while it was landing: it leaves as soon as it is parked, serviced or not.</summary>
    public bool Launch { get; internal set; }

    /// <summary>
    /// A ReturnToBase order came while it climbed out (or a Rearm order while it was leaving): once it
    /// flies free it heads home at once, for <see cref="RecallReason"/>.
    /// </summary>
    public bool Recall { get; internal set; }

    /// <summary>Why a <see cref="Recall"/> brings it back: <see cref="HomeReason.Recall"/> or <see cref="HomeReason.Rearm"/>.</summary>
    public HomeReason RecallReason { get; internal set; }

    /// <summary>
    /// The airfield a <see cref="Recall"/> takes it to (logistics: a Deliver order's target, or where an airlifter goes to load after
    /// a delivery); 0: its best home.
    /// </summary>
    public uint RecallTo { get; internal set; }

    /// <summary>Ticks left of unloading its current pallet into the airfield's stock (logistics: a Deliver order, parked); 0 otherwise.</summary>
    public int UnloadLeft { get; internal set; }

    // The stock's remainders (logistics: a field whose services draw on a limited stock): fuel added times the stock's fuel cost
    // (a whole supply at the aircraft's tank) and HP added times its repair cost (a whole supply at 100).
    internal long FuelCostAcc { get; set; }

    internal long HpCostAcc { get; set; }

    /// <summary>Its takeoff roll and climb-out are on afterburner (E9: the runway's open stretch was too short for a military takeoff).</summary>
    public bool Afterburner { get; internal set; }

    // The service remainders (M3b): fuel (Lifetime units times the station's refuel percent, a
    // full tank at max times RefuelTicks times 100), the next round (the station's rearm percent a
    // tick, one round at RearmTicksPerRound times 100) and HP (repairHpPerSecond times the station's
    // repair percent a tick, one HP at TicksPerSecond times 100).
    internal long FuelAcc { get; set; }

    internal long AmmoAcc { get; set; }

    internal long HpAcc { get; set; }

    // The runway stretch it was cleared to land on (raw, the airfield's frame): a crater cutting it
    // sends it around (M3b). Both 0 outside a final.
    internal long RwWest { get; set; }

    internal long RwEast { get; set; }

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

    /// <summary>A crater opened ahead of it rolling on the runway: it stopped short of it (M3b).</summary>
    CraterStop,

    /// <summary>A crater opened too close ahead of it rolling on the runway: it ran into its edge, damaged (M3b).</summary>
    CraterHit,

    /// <summary>Its runway closed: it left for another airfield, or its ground control station (M3b).</summary>
    Diverted,

    /// <summary>Overhead (E9): over the initial point it rolled out inbound on the extended centre line (the run-in begins).</summary>
    Initial,

    /// <summary>Overhead (E9): at midfield it broke into its level 180-degree turn (speed brakes out, idle).</summary>
    Break,

    /// <summary>Overhead (E9): rolled out on the downwind leg abeam midfield, the gear comes down (and the flaps).</summary>
    GearDown,

    /// <summary>Overhead (E9): at the perch it began its descending base turn onto the short final.</summary>
    Perch,

    /// <summary>After touchdown the nose came down (aerobraking over, E9): wheel braking from here.</summary>
    NoseDown,

    /// <summary>The takeoff roll began on afterburner (E9: its military roll was longer than half the open stretch).</summary>
    Afterburner,
}

/// <summary>
/// Where an aircraft is in its approach and landing (<see cref="World.ApproachStepOf"/>; E9), for status text and
/// the client's gear, flaps and speed brakes. Derived from its phase and leg; not stored.
/// </summary>
public enum ApproachStep : byte
{
    /// <summary>Not on an approach (free flight, holding, on the ground past the roll-out, taking off).</summary>
    None,

    /// <summary>Straight in: the join onto the long final. Overhead: the join onto the initial point.</summary>
    Join,

    /// <summary>Overhead: the run-in from the initial point along the runway at pattern height and break speed.</summary>
    Initial,

    /// <summary>Overhead: the level 180-degree break turn at midfield.</summary>
    Break,

    /// <summary>Overhead: the downwind leg (gear and flaps down from its start, slowing to approach speed).</summary>
    Downwind,

    /// <summary>Overhead: the descending 180-degree base turn from the perch.</summary>
    Base,

    /// <summary>The final: on the centre line to the fix.</summary>
    Final,

    /// <summary>From the fix: the glide, the flare and the touchdown.</summary>
    Glide,

    /// <summary>On the runway with its nose held up (aerobraking).</summary>
    Aerobrake,

    /// <summary>Rolling out on its wheels to its exit connector.</summary>
    Rollout,
}

/// <summary>One airfield event of the last tick, for renderers and logs (a tyre puff at touchdown). Not game state.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Aircraft">The aircraft.</param>
/// <param name="Airfield">Its airfield.</param>
/// <param name="At">Where.</param>
public readonly record struct AirfieldEvent(AirfieldEventKind Kind, uint Aircraft, uint Airfield, Vec At);
