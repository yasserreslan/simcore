using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// The task of an <see cref="CommandKind.AirTask"/> command (the mission layer, E3). Orbit is a Move
/// order and Strike an Attack order; these are the tasks that need a mission record.
/// </summary>
public enum AirTaskKind : byte
{
    /// <summary>No mission: it orbits the point it was sent to (<see cref="Unit.OrbitAt"/>).</summary>
    Orbit,

    /// <summary>Patrol a route: an orbit for one waypoint, a racetrack between two, a closed loop through 3 to 8. It spots, never fires on its own.</summary>
    Patrol,

    /// <summary>
    /// Guard (the hunt): patrol an area (a racetrack across it) or a route and engage spotted enemies
    /// of the filter's kinds inside it plus the archetype's leash. The only mode that picks targets.
    /// </summary>
    Guard,

    /// <summary>Search an area: lawnmower lanes 1.6 times its sight apart, then an orbit over the centre (or again: loop).</summary>
    Search,

    /// <summary>Roam and spot: seeded waypoints over an area, toward the sectors it has not flown over lately. It never fires on its own.</summary>
    Roam,
}

/// <summary>
/// An aircraft's mission (the mission layer, E3; <see cref="Unit.AirMission"/>, hashed while present):
/// the task, its route or area, the Guard filter and where it is in the task. A drone without one
/// orbits <see cref="Unit.OrbitAt"/> exactly as before. Attack orders (Strike) run on top of it and
/// the task resumes when they end; refuel trips and Rearm visits keep it, and it resumes at the
/// nearest leg when the aircraft flies free again.
/// </summary>
public sealed class AirMission
{
    /// <summary>The most waypoints a route holds (Shift+click appends up to this many).</summary>
    public const int MaxRoute = 8;

    // The roam memory: 8 x 8 sectors of the area.
    internal const int Sectors = 8;

    internal readonly List<Cell> RouteList = [];

    /// <summary>The task (never <see cref="AirTaskKind.Orbit"/>: no mission means orbit).</summary>
    public AirTaskKind Kind { get; internal set; }

    /// <summary>The route's waypoints as ordered (Patrol, Guard over a route); empty for an area task.</summary>
    public IReadOnlyList<Cell> Route => RouteList;

    /// <summary>Whether the task is over an area (Guard over an area, Search, Roam): <see cref="AreaMin"/> to <see cref="AreaMax"/>.</summary>
    public bool HasArea { get; internal set; }

    /// <summary>The area's corner cells (inclusive), as ordered; the flight keeps inside the map's edge margin.</summary>
    public Cell AreaMin { get; internal set; }

    /// <summary>The area's far corner cell (inclusive).</summary>
    public Cell AreaMax { get; internal set; }

    /// <summary>Guard's target kinds.</summary>
    public AirTargetKinds Filter { get; internal set; }

    /// <summary>The waypoint it flies to (route index; a search lane's end, 0 its start, 1 its end; the roam leg count).</summary>
    public int Leg { get; internal set; }

    /// <summary>Search: the lane it is on (lanes done before it).</summary>
    public int Lane { get; internal set; }

    /// <summary>Search: start over when done instead of orbiting the centre.</summary>
    public bool Loop { get; internal set; }

    /// <summary>Search: the sweep is complete; it orbits the area's centre.</summary>
    public bool Done { get; internal set; }

    /// <summary>Roam: the sectors (8 x 8 over the area, bit y * 8 + x) flown over since the memory last filled.</summary>
    public ulong Visited { get; internal set; }

    /// <summary>Roam: the waypoint it flies to now.</summary>
    public Vec RoamAim { get; internal set; }

    /// <summary>Guard: the target it is engaging now (its Attack order came from the hunt), or 0.</summary>
    public uint Hunt { get; internal set; }

    /// <summary>It was away (a refuel trip, a Rearm visit, a recall): it rejoins at the nearest leg when it flies free again.</summary>
    public bool Away { get; internal set; }

    internal AirMission Clone()
    {
        var copy = new AirMission
        {
            Kind = Kind, HasArea = HasArea, AreaMin = AreaMin, AreaMax = AreaMax, Filter = Filter, Leg = Leg, Lane = Lane,
            Loop = Loop, Done = Done, Visited = Visited, RoamAim = RoamAim, Hunt = Hunt, Away = Away,
        };
        copy.RouteList.AddRange(RouteList);
        return copy;
    }

    internal void HashInto(ref WordHash h)
    {
        h.Put(0xA15);
        h.PutByte((byte)Kind);
        h.PutSigned(RouteList.Count);
        foreach (var c in RouteList)
        {
            h.PutSigned(c.X);
            h.PutSigned(c.Y);
        }
        h.Put(HasArea ? 1UL : 0);
        h.PutSigned(AreaMin.X);
        h.PutSigned(AreaMin.Y);
        h.PutSigned(AreaMax.X);
        h.PutSigned(AreaMax.Y);
        h.PutByte((byte)Filter);
        h.PutSigned(Leg);
        h.PutSigned(Lane);
        h.Put(Loop ? 1UL : 0);
        h.Put(Done ? 1UL : 0);
        h.Put(Visited);
        h.PutSigned(RoamAim.X.Raw);
        h.PutSigned(RoamAim.Y.Raw);
        h.Put(Hunt);
        h.Put(Away ? 1UL : 0);
    }
}

/// <summary>What an aircraft is doing, for the aircraft sidebar and the mission UI (<see cref="World.AirStatusOf"/>).</summary>
public enum AirActivity : byte
{
    /// <summary>Not an aircraft (or not a station holding one).</summary>
    None,

    /// <summary>Orbiting the point it was sent to.</summary>
    Orbiting,

    /// <summary>On Patrol.</summary>
    Patrolling,

    /// <summary>On Guard, no target.</summary>
    Guarding,

    /// <summary>On Guard, engaging a target it picked (<see cref="AirStatus.Target"/>).</summary>
    Engaging,

    /// <summary>Searching (<see cref="AirStatus.Percent"/>: lanes swept).</summary>
    Searching,

    /// <summary>Roaming and spotting.</summary>
    Roaming,

    /// <summary>On an attack order (Strike).</summary>
    Striking,

    /// <summary>Heading home at bingo fuel (the only automatic trip).</summary>
    Returning,

    /// <summary>Heading home on a Rearm order.</summary>
    RearmEnRoute,

    /// <summary>Heading home on a ReturnToBase order.</summary>
    Recalled,

    /// <summary>Holding at its airfield (<see cref="AirStatus.QueuePlace"/>: its place in the landing queue, 1 first; 0 none yet).</summary>
    Holding,

    /// <summary>On final, or rolling out.</summary>
    Landing,

    /// <summary>Taxiing in to its station.</summary>
    Taxiing,

    /// <summary>Parked, refuelling (<see cref="AirStatus.Percent"/>: the tank).</summary>
    Refuelling,

    /// <summary>Parked on a Rearm visit, rearming (<see cref="AirStatus.Percent"/>: the stocks).</summary>
    Rearming,

    /// <summary>Parked on a Rearm visit, repairing (<see cref="AirStatus.Percent"/>: HP).</summary>
    Repairing,

    /// <summary>Parked and serviced, about to leave (or waiting for its station to work).</summary>
    Ready,

    /// <summary>Parked and held (ReturnToBase, Stop) until its next order.</summary>
    Held,

    /// <summary>Leaving: pushback, taxi out, line-up, takeoff roll, climb-out.</summary>
    Departing,

    /// <summary>A ground control station holding its recovered drone for the relaunch (<see cref="AirStatus.Percent"/>: the turnaround).</summary>
    Docked,

    /// <summary>A gunship on its orbit attack (H3: <see cref="Unit.Gunship"/>; <see cref="AirStatus.Target"/> the ordered unit, if any).</summary>
    GunOrbit,

    /// <summary>A helicopter out of control (<see cref="AirStatus.Percent"/>: the time it has left in the air, of its whole).</summary>
    OutOfControl,

    /// <summary>A helicopter on its Load errand: flying to its point, or down there waiting for riders.</summary>
    Boarding,

    /// <summary>A helicopter on its Unload errand: flying to its point, or dropping its riders there.</summary>
    Unloading,

    /// <summary>A helicopter hovering where it was sent (no task).</summary>
    Hovering,

    /// <summary>A helicopter coming straight down to land, or lifting off.</summary>
    Vertical,
}

/// <summary>
/// An aircraft's status (<see cref="World.AirStatusOf"/>): one read-only engine query for the
/// sidebar and the mission UI, so their state text never diverges from the simulation.
/// </summary>
/// <param name="Activity">What it is doing.</param>
/// <param name="Mission">Its mission's task (<see cref="AirTaskKind.Orbit"/> without a mission): what it resumes after a strike or a trip home.</param>
/// <param name="Phase">Its flight phase (<see cref="FlightPhase.Airborne"/> in free flight).</param>
/// <param name="Reason">Why it is homing or at its airfield (meaningful while homing or parked).</param>
/// <param name="Percent">The activity's progress, 0 to 100 (search lanes, the tank, stocks, HP, the turnaround); 0 otherwise.</param>
/// <param name="QueuePlace">Holding: its place in the landing queue (1 is next); 0 otherwise.</param>
/// <param name="FuelPercent">Its fuel (lifetime) as a share of the tank, 0 to 100.</param>
/// <param name="Ammo">The rounds it carries in all its stocks (-1: it carries no weapon).</param>
/// <param name="NoAmmo">It carries weapons and every stock is empty (the "No ammo" status).</param>
/// <param name="Target">The unit it is attacking (a strike order or a Guard engagement), or 0.</param>
public readonly record struct AirStatus(AirActivity Activity, AirTaskKind Mission, FlightPhase Phase, HomeReason Reason, int Percent,
    int QueuePlace, int FuelPercent, int Ammo, bool NoAmmo, uint Target);
