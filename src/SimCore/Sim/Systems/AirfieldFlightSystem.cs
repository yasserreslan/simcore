using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Airfield flight operations, milestone M2 (the game repo's docs/airfield-design.md, sections 1,
// 1.2 and 2.2): an aircraft whose home is an airfield holds there, lands, taxis to its station,
// parks, and takes off again, keeping its ID and HP. Nothing here is specific to one aircraft:
// every number comes from its aircraft block, its flight block and the airfield type.
//
// Home: the nearest of the owner's airfields (to its hold point; ties by ID) whose type serves the
// aircraft's class, whose runway it can use and with a station free; with none, its ground control
// station as before; with neither, the nearest such airfield anyway (it holds there until a station
// frees), else it orbits on until it runs dry. Joining, it reserves a station (a loading station
// when it needs rearm or repair, else an open stand; then the shortest taxi from its landing exit,
// then the station ID) and joins the landing queue.
//
// The runway is used one aircraft at a time (the lock in the airfield record): landings come from
// the far (east) end toward the facility, takeoffs leave from the westmost connector away from it.
// The airfield system clears the head of the landing queue once it is at the hold point, else the
// longest-waiting aircraft at the line-up. In the air the aircraft steers as a scout does (its
// velocity turns at most its turn rate a tick; a fixed wing within its envelope's a(S), E9); on the
// ground it follows a path of map points with every corner rounded, at taxi speed and at turn speed
// through corners, braking ahead of them.
//
// Jets at the airfield (E9; the plan's 2.6 and 2.12, decision D17): an aircraft with the overhead pattern
// flies from the hold to an initial point, runs in along the runway, breaks at midfield, flies the downwind
// leg (gear down) and a descending base turn onto a short final (FlyOverhead), then the straight-in's glide;
// after touchdown it may hold its nose up (aerobraking) to a speed. It takes off on military power when its
// military roll fits in half the open stretch, else on afterburner; a fixed wing climbs out on its envelope
// and flies free once the runway is free and it has its corner speed. Its corridor need and pattern must fit
// on the map (PatternSide; a displaced threshold where that helps: UsableEast) for a field to be its home.
public sealed partial class World
{
    // The final: it steers at a carrot this many cells ahead on the extended centre line.
    private const int CarrotCells = 10;

    // Go-around: more than this off the centre line at the final fix (raw: 2 cells)...
    private const long GoAroundOffRaw = 2 * Fixed.OneRaw;

    // ... or more than this many facing steps off the landing heading, or this much above the fix height (raw: 2 m).
    private const int GoAroundFacings = 1;
    private const long FixSlackRaw = 2 * 512;

    // A corner turning more than 30 degrees (cos 0.866) is taken at turn speed.
    private const long TurnCosPermille = 866;

    // A corner under about 5 degrees is not rounded.
    private const long StraightCosPermille = 996;

    // Taxi corners are rounded within this distance of the corner (raw: 2 cells), in this many pieces.
    private const long CornerRadiusRaw = 2 * Fixed.OneRaw;
    private const int CornerSteps = 8;

    // The takeoff path runs on past the runway's end by this much (raw: 30 cells); it is airborne well before.
    private const long OverrunRaw = 30 * Fixed.OneRaw;

    // The overhead pattern's legs of a final (AirOps.Leg; E9): the join onto the initial point, the run-in, the
    // break, the downwind leg, the base turn; then the straight-in final's legs 1 (to the fix) and 2 (the glide).
    // On the roll-out, LegAerobrake: the nose held up.
    private const int LegOhJoin = 10, LegOhInitial = 11, LegOhBreak = 12, LegOhDownwind = 13, LegOhBase = 14, LegAerobrake = 9;

    private readonly List<AirfieldEvent> _airfieldEvents = [];
    private readonly List<Vec> _pathScratch = [];

    /// <summary>The airfield events of the last tick (clearances, touchdowns, wheels-up, ...), in order.</summary>
    public IReadOnlyList<AirfieldEvent> AirfieldEvents => _airfieldEvents;

    /// <summary>An aircraft's flight phase: <see cref="FlightPhase.Airborne"/> while it flies free.</summary>
    public static FlightPhase FlightPhaseOf(Unit u) => u.AirOps?.Phase ?? FlightPhase.Airborne;

    /// <summary>Where aircraft u is in its approach and landing (E9; status text, gear, flaps, speed brakes).</summary>
    public static ApproachStep ApproachStepOf(Unit u) => u.AirOps switch
    {
        { Phase: FlightPhase.Final, Leg: 0 or LegOhJoin } => ApproachStep.Join,
        { Phase: FlightPhase.Final, Leg: LegOhInitial } => ApproachStep.Initial,
        { Phase: FlightPhase.Final, Leg: LegOhBreak } => ApproachStep.Break,
        { Phase: FlightPhase.Final, Leg: LegOhDownwind } => ApproachStep.Downwind,
        { Phase: FlightPhase.Final, Leg: LegOhBase } => ApproachStep.Base,
        { Phase: FlightPhase.Final, Leg: 1 } => ApproachStep.Final,
        { Phase: FlightPhase.Final } => ApproachStep.Glide,
        { Phase: FlightPhase.Rollout, Leg: LegAerobrake } => ApproachStep.Aerobrake,
        { Phase: FlightPhase.Rollout } => ApproachStep.Rollout,
        _ => ApproachStep.None,
    };

    /// <summary>
    /// Whether aircraft u has its gear (and flaps) down (E9, for renderers): on the ground at its airfield, on the
    /// final from the fix line in (straight in: from the roll-out on the final; overhead: from the downwind leg's
    /// start, event <see cref="AirfieldEventKind.GearDown"/>), and for the first second of its climb-out.
    /// </summary>
    public bool GearDownOf(Unit u) => u.AirOps switch
    {
        null => false,
        { Grounded: true } => true,
        { Phase: FlightPhase.Final, Leg: 1 or 2 or LegOhDownwind or LegOhBase } => true,
        { Phase: FlightPhase.Climb } o => Tick - o.Since < TicksPerSecond,
        _ => false,
    };

    /// <summary>
    /// Whether aircraft u has its speed brakes out (E9, for renderers): in the overhead's break turn and on the
    /// downwind leg while still above its approach speed, and on the runway after touchdown until it reaches taxi speed.
    /// </summary>
    public static bool SpeedBrakesOf(Unit u) => u.AirOps switch
    {
        { Phase: FlightPhase.Final, Leg: LegOhBreak } => true,
        { Phase: FlightPhase.Final, Leg: LegOhDownwind } o => u.Def.Archetype.Aircraft is { } ac && o.Speed > ac.ApproachSpeed.Raw * TicksPerSecond,
        { Phase: FlightPhase.Rollout } o => u.Def.Archetype.Aircraft is { } ac && o.Speed > ac.TaxiSpeed.Raw * TicksPerSecond,
        _ => false,
    };

    // A runway stretch an aircraft uses, in the airfield's frame (raw): segments K0 to K1, from x
    // West to East, its centre line at y Centre. Landings run from East toward West.
    private readonly record struct RunwayGeo(int K0, int K1, long West, long East, long Centre);

    // ---- The airfield's frame at raw precision ----

    private static Vec LocalToMap(Airfield af, long x, long y) => AirfieldLocalToMap(af, x, y);

    private static (long X, long Y) MapToLocal(Airfield af, Vec p) => AirfieldMapToLocal(af, p);

    // MapFacing turns a heading in the airfield's frame to the map's.
    private static int MapFacing(Airfield af, int local) => Facing.Wrap(local + af.Facing);

    private static long ConnectorX(AirfieldType t, int k) => (long)t.SegmentCells * k * Fixed.OneRaw + t.Connector.Raw;

    // FirstConnector is af's westmost segment holding a connector (H1: a main air base has one every few segments).
    private static int FirstConnector(Airfield af)
    {
        for (var k = af.West; k <= af.East; k++)
        {
            if (af.Type.HasConnector(k))
            {
                return k;
            }
        }
        return af.West;
    }

    // ConnectorTenths is a connector's x in tenths of a cell (as the taxi graph has it).
    private static int ConnectorTenths(AirfieldType t, int k) => t.SegmentCells * k * 10 + (int)(t.Connector.Raw * 10 / Fixed.OneRaw);

    // ---- The runway, the hold, the stations ----

    // RunwayFor finds the stretch of af's runway aircraft ac uses: the longest open stretch (craters
    // close parts of it, M3a) within the run of standing, finished segments, holding a connector, at
    // least its minLandRun long and with its minTakeoffRun from its westmost connector to its east
    // end (ties to the west). K0 and K1 are the segments of its westmost and eastmost connectors.
    private bool RunwayFor(Airfield af, AircraftSpec ac, out RunwayGeo rw)
    {
        rw = default;
        var t = af.Type;
        if (!af.HasRunway || !t.Serves(ac.SizeClass))
        {
            return false;
        }
        var found = false;
        var start = af.West;
        for (var k = af.West; k <= af.East + 1; k++)
        {
            if (k <= af.East && Standing(af.SegmentList[k - af.West]))
            {
                continue;
            }
            if (k > start)
            {
                long lo = (long)t.SegmentCells * start * Fixed.OneRaw, hi = (long)t.SegmentCells * k * Fixed.OneRaw;
                foreach (var s in af.OpenStretches())
                {
                    long west = Math.Max(lo, s.From.Raw), east = UsableEast(af, ac, west, Math.Min(hi, s.To.Raw));
                    if (east - west < ac.MinLandRun.Raw || (found && east - west <= rw.East - rw.West))
                    {
                        continue;
                    }
                    int k0 = -1, k1 = -1;
                    for (var c = af.West; c <= af.East; c++)
                    {
                        if (!t.HasConnector(c))
                        {
                            continue;
                        }
                        var x = ConnectorX(t, c);
                        if (x >= west && x <= east)
                        {
                            k0 = k0 < 0 ? c : k0;
                            k1 = c;
                        }
                    }
                    var geo = new RunwayGeo(k0, k1, west, east, (long)t.RunwayCells * Fixed.OneRaw / 2);
                    if (k0 >= 0 && east - ConnectorX(t, k0) >= ac.MinTakeoffRun.Raw)
                    {
                        rw = geo;
                        found = true;
                    }
                }
            }
            start = k + 1;
        }
        return found;
    }

    // UsableEast is the approach end of the stretch from west to east of af that aircraft ac can use (E9): east
    // itself when its corridor and pattern fit on the map there (PatternSide), else the first whole cell short of
    // it where they do (a displaced threshold: it flies over the rest of the runway), or west - 1 when none
    // leaves its minimum landing run. Flying straight in without a corridor need: where its join circle fits (E11).
    private long UsableEast(Airfield af, AircraftSpec ac, long west, long east)
    {
        var centre = (long)af.Type.RunwayCells * Fixed.OneRaw / 2;
        for (var e = east; e - west >= ac.MinLandRun.Raw; e -= Fixed.OneRaw)
        {
            if (PatternSide(af, ac, new RunwayGeo(0, 0, west, e, centre)) != 0)
            {
                return e;
            }
        }
        return west - 1;
    }

    // PatternFits reports whether aircraft ac could ever use af's runway (its whole run of segments, craters
    // aside) with its corridor and pattern on the map: a field that never fits is never its home (E9) and does
    // not build it.
    private bool PatternFits(Airfield af, AircraftSpec ac)
    {
        var whole = WholeRunway(af);
        return UsableEast(af, ac, whole.West, whole.East) - whole.West >= ac.MinLandRun.Raw;
    }

    // HoldPoint is the centre of af's holding orbit: out on the approach side of the stretch.
    private static Vec HoldPoint(Airfield af, RunwayGeo rw) => LocalToMap(af, rw.East + af.Type.HoldDistance.Raw, rw.Centre);

    // WholeRunway is the geometry of af's whole run of segments (no craters counted).
    private static RunwayGeo WholeRunway(Airfield af) =>
        GeoOf(af, (long)af.Type.SegmentCells * af.West * Fixed.OneRaw, (long)af.Type.SegmentCells * (af.East + 1) * Fixed.OneRaw);

    // PatternSide is the side of the centre line (+1: the rows' side, -1: away from them) aircraft ac flies its
    // overhead pattern on over stretch rw of af, or 0 when it cannot use the stretch (E9, the plan's 2.6 and
    // 2.12): its corridor need (the approach from the initial point and the climb-out beyond the approach end,
    // the runway's width) must lie on the map, and for the overhead the break turn, the downwind leg and the
    // base turn too (from a turn radius short of midfield to a turn radius past the base, two turn radii and a
    // quarter out), away from the rows when that fits, else over them, and its join circle tangent at the
    // initial point on one side or the other. Flying straight in (the drones), its join's turn circle on one side or
    // the other (StraightJoinFits; E11: before, a drone whose join circle reached into the band the join's edge
    // avoidance keeps clear circled there for good, never landing); -1 (unused) when it fits.
    private int PatternSide(Airfield af, AircraftSpec ac, RunwayGeo rw)
    {
        var half = (long)af.Type.RunwayCells * Fixed.OneRaw / 2;
        if (ac.CorridorNeed.Raw > 0 && !LocalBoxOnMap(af, rw.East, rw.Centre - half, rw.East + ac.CorridorNeed.Raw, rw.Centre + half))
        {
            return 0;
        }
        if (ac.Pattern != ApproachPattern.Overhead)
        {
            return StraightJoinFits(af, ac, rw, 1) || StraightJoinFits(af, ac, rw, -1) ? -1 : 0;
        }
        var ip = rw.East + ac.InitialDistance.Raw;
        var jr = ac.JoinRadius.Raw;
        if (!CircleOnMap(af, (ip, ip, rw.Centre + jr, jr)) && !CircleOnMap(af, (ip, ip, rw.Centre - jr, jr)))
        {
            return 0; // its join onto the initial point fits on neither side
        }
        long r = ac.PatternRadius.Raw, mid = BreakX(af, rw), xBase = BaseX(ac, rw);
        for (var side = -1; side <= 1; side += 2)
        {
            var far = rw.Centre + side * (2 * r + r / 4);
            if (LocalBoxOnMap(af, mid - r, Math.Min(rw.Centre, far), xBase + r, Math.Max(rw.Centre, far)))
            {
                return side;
            }
        }
        return 0;
    }

    // BaseX is where an overhead's base turn begins and ends along the runway (raw, the airfield's frame): a turn
    // radius beyond its final fix, so a straight final of a turn radius lines it up before the fix.
    private static long BaseX(AircraftSpec ac, RunwayGeo rw) => rw.East + ac.FixDistance.Raw + ac.PatternRadius.Raw;

    // LocalBoxOnMap reports whether the box x0..x1, y0..y1 of af's frame (raw) lies on the map.
    private bool LocalBoxOnMap(Airfield af, long x0, long y0, long x1, long y1)
    {
        long w = (long)Grid.Width * Fixed.OneRaw, h = (long)Grid.Height * Fixed.OneRaw;
        bool On(Vec p) => p.X.Raw >= 0 && p.Y.Raw >= 0 && p.X.Raw <= w && p.Y.Raw <= h;
        // The frame turns in quarter turns: two opposite corners bound the box on the map.
        return On(LocalToMap(af, x0, y0)) && On(LocalToMap(af, x1, y1));
    }

    // TaxiV and TurnV are aircraft ac's taxi speed and its taxi speed in turns at airfield type t (raw, a tick): its own,
    // or the type's floor when that is higher (2026-10-21, the landing fix: the main air base's wide taxiways).
    private static long TaxiV(AirfieldType t, AircraftSpec ac) => Math.Max(ac.TaxiSpeed.Raw, t.MinTaxi.Raw);

    private static long TurnV(AirfieldType t, AircraftSpec ac) => Math.Max(ac.TurnSpeed.Raw, t.MinTurn.Raw);

    // BreakX is where an overhead's break lies along stretch rw of af (raw, the airfield's frame): midfield, or the
    // type's break distance in from the threshold (rw.East) when that is nearer (the landing fix).
    private static long BreakX(Airfield af, RunwayGeo rw)
    {
        var mid = (rw.West + rw.East) / 2;
        var d = af.Type.BreakDistance.Raw;
        return d > 0 ? Math.Max(mid, rw.East - d) : mid;
    }

    // ExitConnector is the segment whose connector a landing aircraft turns off at: the first one
    // ahead of where its roll-out reaches taxi speed, else the last one of the stretch.
    private static int ExitConnector(AirfieldType t, AircraftSpec ac, RunwayGeo rw)
    {
        long vt = ac.TouchdownSpeed.Raw, vx = TaxiV(t, ac);
        var roll = vt > vx ? (vt * vt - vx * vx) * TicksPerSecond / (2 * ac.RolloutDecel.Raw) : 0;
        var slow = rw.East - ac.Touchdown.Raw - roll;
        for (var k = rw.K1; k >= rw.K0; k--)
        {
            if (t.HasConnector(k) && ConnectorX(t, k) <= slow)
            {
                return k;
            }
        }
        return rw.K0;
    }

    // Fits reports whether station st of af can take aircraft ac: standing, finished (a heavy bay's hangar too), of
    // its class (H1: heavies only in heavy bays, nothing else there), wide enough.
    private bool Fits(Airfield af, AirfieldStation st, AircraftSpec ac) =>
        StationStands(af, st) && Unit(st.Unit)!.Def.Archetype.Airfield is { } spec && spec.Takes(ac.SizeClass) &&
        (spec.MaxSpan.Raw == 0 || ac.Span <= spec.MaxSpan);

    private bool HasFreeStation(Airfield af, AircraftSpec ac)
    {
        foreach (var r in ServedRows(af)) // (H1: a base's linked forward airfields' stations too)
        {
            foreach (var st in r.Stations)
            {
                if (st.Occupant == 0 && st.ReservedBy == 0 && Fits(af, st, ac))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // StationOf finds the row and the record of a station unit of af (or of a forward airfield linked to base af, H1).
    private (AirfieldRow? Row, AirfieldStation? St) StationOf(Airfield af, uint unit)
    {
        foreach (var r in ServedRows(af))
        {
            foreach (var st in r.Stations)
            {
                if (unit != 0 && st.Unit == unit)
                {
                    return (r, st);
                }
            }
        }
        return (null, null);
    }

    // NeedsService reports whether u needs repair or ammunition (it prefers a loading station).
    private static bool NeedsService(Unit u)
    {
        if (u.HP < u.Def.MaxHp)
        {
            return true;
        }
        for (var i = 0; i < u.Charges.Count; i++)
        {
            if (u.Charges[i] < u.Def.StockMax[i])
            {
                return true;
            }
        }
        for (var i = 0; i < u.Salvos.Count; i++)
        {
            if (u.Salvos[i] < u.Def.Archetype.Countermeasures[i].Salvos)
            {
                return true; // countermeasures to reload (E6)
            }
        }
        return false;
    }

    // HomeAirfield picks scout u's airfield home (see the file comment); station: whether its
    // ground control station could take it instead.
    private Airfield? HomeAirfield(Unit u, AircraftSpec ac, bool station)
    {
        if (u.HomeReason == HomeReason.Rearm && CargoShort(u) && LoadingAirfield(u, ac) is { } load)
        {
            return load; // logistics: pallets load only where supply is unlimited (a main air base), even while its stations are busy
        }
        Airfield? best = null, anyBest = null, closed = null;
        long bestD = 0, anyD = 0, closedD = 0;
        foreach (var af in _airfields)
        {
            if (af.Owner != u.Owner || !af.HasRunway || !af.Type.Serves(ac.SizeClass) || !PatternFits(af, ac))
            {
                continue; // (E9) its corridor or its pattern does not fit on the map there: never a home
            }
            if (!RunwayFor(af, ac, out var rw))
            {
                // Its runway closed (craters, M3b): with nowhere else to go it holds there till repaired.
                var dc = (HoldPoint(af, HoldGeo(af, ac)) - u.Pos).LenSq();
                if (closed is null || dc < closedD)
                {
                    (closed, closedD) = (af, dc);
                }
                continue;
            }
            var d = (HoldPoint(af, rw) - u.Pos).LenSq();
            if (anyBest is null || d < anyD)
            {
                (anyBest, anyD) = (af, d);
            }
            if ((best is null || d < bestD) && HasFreeStation(af, ac))
            {
                (best, bestD) = (af, d);
            }
        }
        return best ?? (station ? null : anyBest ?? closed);
    }

    // AirfieldHomeTicks is how long u needs to come home to af: the flight to its hold point, the
    // approach (about the holding orbit and twice the final) and its aircraft's holding reserve.
    private long AirfieldHomeTicks(Unit u, Airfield af, AircraftSpec ac) => AirfieldHomeTicksFrom(u, af, ac, u.Pos);

    // AirfieldHomeTicksFrom is AirfieldHomeTicks for u as if it were at `from` (E13: a strike order's fuel check).
    private long AirfieldHomeTicksFrom(Unit u, Airfield af, AircraftSpec ac, Vec from)
    {
        var rw = HoldGeo(af, ac);
        var t = af.Type;
        var fly = (HoldPoint(af, rw) - from).Len().Raw / Math.Max(1, u.Def.Speed.Raw);
        var way = t.HoldDistance.Raw + 2 * t.HoldRadius.Raw + 2 * ac.FixDistance.Raw;
        if (ac.Pattern == ApproachPattern.Overhead)
        {
            // The overhead (E9): the join and the run-in, the downwind leg back and the two half turns.
            way += ac.InitialDistance.Raw + 2 * ac.JoinRadius.Raw + 2 * (rw.East - rw.West) + 7 * ac.PatternRadius.Raw;
        }
        var approach = way / Math.Max(1, ac.ApproachSpeed.Raw);
        return fly + approach + ac.HoldReserveTicks;
    }

    // JoinAirfield makes scout u head home to af: it holds there with a station reserved and its
    // place in the landing queue (or without, until a station frees).
    private void JoinAirfield(Unit u, Airfield af, AircraftSpec ac)
    {
        var ops = new AirOps(af.Id, Cell.Of(u.OrbitAt))
        {
            Phase = FlightPhase.Holding,
            Speed = u.Wing?.Speed ?? u.Def.Speed.Raw * TicksPerSecond, // a fixed wing keeps its speed (it eases to cruise holding)
        };
        u.AirOps = ops;
        u.Wing = null; // at its airfield the phases fly it (a fixed wing starts free flight afresh off the climb-out)
        u.Homing = true;
        u.Idle();
        ReserveStation(u, af, ac, ops);
    }

    // ReserveStation reserves the best free station of af for u (PickStation, from its landing exit)
    // and, holding, queues it to land (keeping its place when it is queued already).
    private void ReserveStation(Unit u, Airfield af, AircraftSpec ac, AirOps ops)
    {
        if (!RunwayFor(af, ac, out var rw))
        {
            return;
        }
        var t = af.Type;
        var g = AirfieldTaxiGraph(af);
        var exit = FindNode(g, TaxiNodeKind.RunwayConnector, ConnectorTenths(t, ExitConnector(t, ac, rw)), t.RunwayCells * 5);
        if (exit < 0)
        {
            return;
        }
        if (PickStation(u, af, ac, g, exit) is not { } best) // AirfieldServiceSystem.cs
        {
            return;
        }
        best.ReservedBy = u.Id;
        ops.Station = best.Unit;
        if (ops.Phase == FlightPhase.Holding && !af.LandingList.Exists(e => e.Aircraft == u.Id))
        {
            Sim.Airfield.Enqueue(af.LandingList, Tick, u.Id);
        }
    }

    // ---- The taxi graph ----

    private static int FindNode(TaxiGraph g, TaxiNodeKind kind, int x, int y)
    {
        for (var i = 0; i < g.Nodes.Count; i++)
        {
            var n = g.Nodes[i];
            if (n.Kind == kind && n.Local.X == x && n.Local.Y == y)
            {
                return i;
            }
        }
        return -1;
    }

    private static int FindStop(TaxiGraph g, uint station)
    {
        for (var i = 0; i < g.Nodes.Count; i++)
        {
            if (g.Nodes[i].Kind == TaxiNodeKind.Stop && g.Nodes[i].Station == station)
            {
                return i;
            }
        }
        return -1;
    }

    // RowLane is the lane node of a row (a heavy bay's: on the apron in front of its door; a linked airfield's row's
    // in a base's graph, H1), found by its place on the map.
    private static int RowLane(TaxiGraph g, AirfieldType t, AirfieldRow r)
    {
        var (x, y) = RowLaneTenths(r.Field?.Type ?? t, r);
        if (r.Field is null)
        {
            return FindNode(g, TaxiNodeKind.Lane, x, y);
        }
        var at = AirfieldPoint(r.Field, x, y);
        for (var i = 0; i < g.Nodes.Count; i++)
        {
            if (g.Nodes[i].Kind == TaxiNodeKind.Lane && g.Nodes[i].At == at)
            {
                return i;
            }
        }
        return -1;
    }

    private static bool OnRunway(TaxiNode n) => n.Kind is TaxiNodeKind.RunwayEnd or TaxiNodeKind.RunwayConnector;

    // TaxiRoute runs Dijkstra over taxi graph g from node `from`: the distance (tenths of a cell)
    // and the previous node of every node. Edges along the runway's centre line are left out (an
    // aircraft taxis across the runway only on connectors). Ties go to the lower node index.
    private static (long[] Dist, int[] Prev) TaxiRoute(TaxiGraph g, int from)
    {
        var n = g.Nodes.Count;
        var dist = new long[n];
        var prev = new int[n];
        var done = new bool[n];
        Array.Fill(dist, long.MaxValue);
        Array.Fill(prev, -1);
        dist[from] = 0;
        while (true)
        {
            var best = -1;
            for (var i = 0; i < n; i++)
            {
                if (!done[i] && dist[i] != long.MaxValue && (best < 0 || dist[i] < dist[best]))
                {
                    best = i;
                }
            }
            if (best < 0)
            {
                break;
            }
            done[best] = true;
            foreach (var (a, b) in g.Edges)
            {
                if (a != best && b != best)
                {
                    continue;
                }
                var o = a == best ? b : a;
                if (done[o] || (OnRunway(g.Nodes[a]) && OnRunway(g.Nodes[b])))
                {
                    continue;
                }
                var w = Len3(g.Nodes[a].Local.X - g.Nodes[b].Local.X, g.Nodes[a].Local.Y - g.Nodes[b].Local.Y, 0);
                if (dist[best] + w < dist[o])
                {
                    dist[o] = dist[best] + w;
                    prev[o] = best;
                }
            }
        }
        return (dist, prev);
    }

    // AddRoute appends the nodes of the route from Dijkstra's source to node `to` (source
    // included) to pts; it returns false when `to` is unreachable.
    private static bool AddRoute(TaxiGraph g, int[] prev, int from, int to, List<Vec> pts, List<int> nodes)
    {
        var chain = new List<int>();
        for (var i = to; i >= 0; i = prev[i])
        {
            chain.Add(i);
            if (i == from)
            {
                break;
            }
        }
        if (chain[^1] != from)
        {
            return false;
        }
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            pts.Add(g.Nodes[chain[i]].At);
            nodes.Add(chain[i]);
        }
        return true;
    }

    // SlotPoint maps a row-local point (tenths) of row r to the map.
    private static Vec SlotPoint(Airfield af, AirfieldRow r, (int X, int Y) p)
    {
        var f = r.Field ?? af; // (H1: a linked airfield's row is in its own frame)
        var (x, y) = SlotTenths(f.Type, r, p);
        return AirfieldPoint(f, x, y);
    }

    // ---- Paths on the ground ----

    // SetPath lays ops's path through pts with every corner rounded: a quadratic curve of
    // CornerSteps pieces between the points CornerRadiusRaw (at most half a leg) either side of the
    // corner; corners turning more than 30 degrees are marked to be taken at turn speed. at[i] gets
    // the index of input point i's first path point.
    private static void SetPath(AirOps ops, List<Vec> pts, int[] at)
    {
        ops.PathPts.Clear();
        ops.PathTurn.Clear();
        ops.PathIdx = 0;
        void Add(long x, long y, bool turn)
        {
            var p = new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y));
            if (ops.PathPts.Count == 0 || ops.PathPts[^1] != p)
            {
                ops.PathPts.Add(p);
                ops.PathTurn.Add(turn);
            }
            else if (turn)
            {
                ops.PathTurn[^1] = true;
            }
        }
        for (var i = 0; i < pts.Count; i++)
        {
            at[i] = ops.PathPts.Count;
            var b = pts[i];
            if (i == 0 || i == pts.Count - 1)
            {
                Add(b.X.Raw, b.Y.Raw, false);
                continue;
            }
            long ax = pts[i - 1].X.Raw - b.X.Raw, ay = pts[i - 1].Y.Raw - b.Y.Raw;
            long cx = pts[i + 1].X.Raw - b.X.Raw, cy = pts[i + 1].Y.Raw - b.Y.Raw;
            long la = Len3(ax, ay, 0), lc = Len3(cx, cy, 0);
            var dot = -(ax * cx + ay * cy); // the legs' directions: in (b - a) and out (c - b)
            if (la == 0 || lc == 0 || dot * 1000 >= StraightCosPermille * la * lc)
            {
                Add(b.X.Raw, b.Y.Raw, false);
                continue;
            }
            var turn = dot * 1000 < TurnCosPermille * la * lc;
            var r = Math.Min(CornerRadiusRaw, Math.Min(la / 2, lc / 2));
            long p1x = b.X.Raw + ax * r / la, p1y = b.Y.Raw + ay * r / la;
            long p2x = b.X.Raw + cx * r / lc, p2y = b.Y.Raw + cy * r / lc;
            const long nn = (long)CornerSteps * CornerSteps;
            for (var s = 0; s <= CornerSteps; s++)
            {
                long w0 = (long)(CornerSteps - s) * (CornerSteps - s), w1 = 2L * (CornerSteps - s) * s, w2 = (long)s * s;
                Add((w0 * p1x + w1 * b.X.Raw + w2 * p2x) / nn, (w0 * p1y + w1 * b.Y.Raw + w2 * p2y) / nn, turn);
            }
        }
        if (ops.PathPts.Count > 1 && ops.PathPts[0] == pts[0])
        {
            ops.PathIdx = 1; // it stands on the first point
        }
    }

    // Taxi moves grounded aircraft u one tick along its path. Its speed eases toward top
    // (accelerating at its takeoff acceleration, slowing at its roll-out deceleration), never above
    // what lets it slow, at its brake, to the turn speed at every marked corner ahead, to a stop at
    // the path's end (endStop) and at point stopAt (a wait; -1: none). Starting from a standstill
    // it first turns on the spot to face its way, a facing step a tick. Forward it faces the way it
    // moves; pushed back (backward) it keeps its facing. It reports whether it reached the end.
    private bool Taxi(Unit u, AircraftSpec ac, AirOps ops, long top, bool endStop, int stopAt, bool backward, long accel = 0)
    {
        var path = ops.PathPts;
        var n = path.Count;
        var start = u.Pos;
        u.VelZ = Fixed.Zero;
        if (ops.PathIdx >= n)
        {
            u.Vel = default;
            return true;
        }
        if (ops.Speed == 0 && !backward)
        {
            // From a standstill: face the way first.
            for (var i = ops.PathIdx; i < n; i++)
            {
                if (path[i] != u.Pos && Facing.TryFromDirection(path[i] - u.Pos, out var want))
                {
                    if (Math.Abs(Facing.Diff(u.Facing, want)) > 1)
                    {
                        u.Facing = Facing.TurnToward(u.Facing, want, 1);
                        u.TurretFacing = u.Facing;
                        u.Vel = default;
                        return false;
                    }
                    break;
                }
            }
        }
        var at = Airfield(ops.Airfield)?.Type;
        long brake = ac.Brake.Raw, turnV = (at is null ? ac.TurnSpeed.Raw : TurnV(at, ac)) * TicksPerSecond, v = ops.Speed;
        var limit = long.MaxValue;
        var horizon = v * v / (40 * brake) + Fixed.OneRaw;
        long dist = 0;
        var prev = u.Pos;
        for (var i = ops.PathIdx; i < n; i++)
        {
            dist += (path[i] - prev).Len().Raw;
            prev = path[i];
            var cap = i == stopAt || (endStop && i == n - 1) ? 0 : ops.PathTurn[i] ? turnV : -1;
            if (cap >= 0)
            {
                // (20 v)^2 = (20 cap)^2 + 2 (brake / 20) d 400: the speed it can still shed in d.
                limit = Math.Min(limit, Fixed.Sqrt(cap * cap + 40 * brake * dist));
            }
            if (dist > horizon)
            {
                break;
            }
        }
        if (v > limit)
        {
            v = Math.Max(limit, v - brake);
        }
        else if (v > top)
        {
            v = Math.Max(top, v - ac.RolloutDecel.Raw);
        }
        else
        {
            v = Math.Min(Math.Min(top, limit), v + (accel > 0 ? accel : ac.TakeoffAccel.Raw));
        }
        var left = v / TicksPerSecond;
        var pos = u.Pos;
        var idx = ops.PathIdx;
        while (left > 0 && idx < n)
        {
            var p = path[idx];
            long dx = p.X.Raw - pos.X.Raw, dy = p.Y.Raw - pos.Y.Raw;
            var d = Len3(dx, dy, 0);
            if (d > left)
            {
                pos = new Vec(Fixed.FromRaw(pos.X.Raw + dx * left / d), Fixed.FromRaw(pos.Y.Raw + dy * left / d));
                break;
            }
            pos = p;
            left -= d;
            if (idx == stopAt)
            {
                v = 0; // it waits here
                break;
            }
            idx++;
        }
        ops.PathIdx = idx;
        var done = idx >= n;
        if (done && endStop)
        {
            v = 0;
        }
        ops.Speed = v;
        u.Vel = pos - start;
        u.Pos = pos;
        u.Altitude = Fixed.FromRaw(GroundRaw(pos));
        if (!backward && u.Vel != default && Facing.TryFromDirection(u.Vel, out var f))
        {
            u.Facing = f;
            u.TurretFacing = f;
        }
        return done;
    }

    // ---- The phases ----

    // FlyAirfield moves aircraft u one tick through its airfield phases (the air movement system,
    // instead of FlyScout, while it has AirOps).
    private void FlyAirfield(Unit u, ScoutFlight f, AirOps ops)
    {
        var ac = u.Def.Archetype.Aircraft!;
        if (u.Reload > 0)
        {
            u.Reload = Math.Max(0, u.Reload - 100);
        }
        if (Airfield(ops.Airfield) is not { } af)
        {
            u.AirOps = null; // its airfield is gone: it flies free and finds another home
            return;
        }
        AirOpsOrders(u, ops);
        DropFallenStation(u, af, ops); // its station fell: it looks for another (M3b)
        switch (ops.Phase)
        {
            case FlightPhase.Holding:
                FlyHolding(u, f, ac, af, ops);
                break;
            case FlightPhase.Final:
                FlyFinal(u, f, ac, af, ops);
                break;
            case FlightPhase.Rollout:
            case FlightPhase.TaxiIn:
            case FlightPhase.Parking:
                TaxiIn(u, ac, af, ops);
                break;
            case FlightPhase.Parked:
                StayParked(u, ac, af, ops);
                break;
            case FlightPhase.PushBack:
                if (Taxi(u, ac, ops, ac.PushbackSpeed.Raw * TicksPerSecond, true, -1, true))
                {
                    TaxiOutFrom(u, ac, af, ops, [], -1, StationOf(af, ops.Station).Row ?? SlotAt(af, ops.PathPts[0]).Row);
                }
                break;
            case FlightPhase.TaxiOut:
                if (Taxi(u, ac, ops, TaxiV(af.Type, ac) * TicksPerSecond, true, -1, false))
                {
                    ops.Phase = FlightPhase.LineUp;
                    ops.Leg = 0;
                    ops.Since = Tick;
                    Sim.Airfield.Enqueue(af.TakeoffList, Tick, u.Id);
                }
                break;
            case FlightPhase.LineUp:
                if (ops.Leg == 0)
                {
                    u.Vel = default; // waits for the runway
                    break;
                }
                Taxi(u, ac, ops, TaxiV(af.Type, ac) * TicksPerSecond, false, -1, false);
                if (ops.PathIdx > ops.MarkA)
                {
                    ops.Phase = FlightPhase.TakeoffRoll;
                    if (ops.Afterburner)
                    {
                        _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.Afterburner, u.Id, af.Id, u.Pos)); // E9
                    }
                }
                break;
            case FlightPhase.TakeoffRoll:
            {
                if (ops.Leg is LegCraterStop or LegCraterHit || RollCheck(u, ac, af, ops, 1))
                {
                    CraterStop(u, ac, af, ops); // a crater ahead (M3b)
                    break;
                }
                var end = Taxi(u, ac, ops, ac.TakeoffSpeed.Raw * TicksPerSecond, false, -1, false, TakeoffAccelOf(ac, ops));
                if (ops.Speed >= ac.TakeoffSpeed.Raw * TicksPerSecond || end)
                {
                    ops.Phase = FlightPhase.Climb;
                    ops.Since = Tick;
                    _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.WheelsUp, u.Id, af.Id, u.Pos));
                }
                break;
            }
            default:
                FlyClimb(u, f, ac, af, ops);
                break;
        }
    }

    // AirOpsOrders: a Move order sets where it goes after its sortie; Move or Attack while it is
    // landing or parked makes it leave once parked (serviced or not), and ends a Hold. Homing in the
    // air, it takes no attack order; anything else it ignores.
    private void AirOpsOrders(Unit u, AirOps ops)
    {
        var landing = ops.Phase is >= FlightPhase.Rollout and <= FlightPhase.Parked;
        switch (u.Order.Kind)
        {
            case OrderKind.Idle:
                return;
            case OrderKind.Move:
                ops.ReturnCell = u.Order.Cell;
                u.AirMission = null; // an orbit there after its sortie: the end of its mission
                u.Idle();
                break;
            case OrderKind.Attack or OrderKind.AttackGround when ops.Phase is FlightPhase.Holding or FlightPhase.Final:
                Refuse(u, u.HomeReason == HomeReason.Refuel ? OrderRefusalReason.RefuelTrip : OrderRefusalReason.Homing, u.Order.Kind == OrderKind.Attack ? u.Order.Target : 0, u.Order.Cell); // E13, D-ORD1
                u.Idle();
                return;
            case OrderKind.Attack or OrderKind.AttackGround when landing:
                // E13 (D-ORD2): an attack from the ground: it leaves once serviced (a full tank; on a Rearm visit, rearmed
                // too), a Hold ended; the order is kept until it flies free.
                ops.Hold = false;
                return;
            case OrderKind.Attack or OrderKind.AttackGround:
                break; // kept until it flies free again (E13: Attack-ground too; it was dropped here before)
            default:
                u.Idle();
                return;
        }
        if (landing)
        {
            ops.Launch = true;
            ops.Hold = false;
        }
    }

    // Steer turns u's velocity toward aim at speed (raw a tick), at most its turn rate a tick; with
    // the aim nearly straight behind it turns the way `turn` says (+1 clockwise on the map, -1 the
    // other way). It moves u and returns its new position.
    private Vec Steer(Unit u, ScoutFlight f, Vec aim, long speed, int turn)
    {
        long vx = u.Vel.X.Raw, vy = u.Vel.Y.Raw;
        long wx = aim.X.Raw - u.Pos.X.Raw, wy = aim.Y.Raw - u.Pos.Y.Raw;
        var wl = Len3(wx, wy, 0);
        if (vx == 0 && vy == 0)
        {
            // Stopped dead (held against the map's edge): it sets off toward its aim.
            var d0 = Facing.Dir(u.Facing);
            (vx, vy) = wl > 0 ? (wx * speed / wl, wy * speed / wl) : (d0.X.Raw * speed / Fixed.OneRaw, d0.Y.Raw * speed / Fixed.OneRaw);
        }
        // A fixed wing turns within its envelope (E9): its velocity changes by at most a(S) / 20 a second a
        // tick (raw a tick: a(S) / 400); a drone at its turn rate as always.
        var max = f.Wing is { } wg ? Math.Max(1, wg.LateralAccel(speed * TicksPerSecond) / (TicksPerSecond * TicksPerSecond)) : Math.Max(1, speed * f.TurnPermille / 1000);
        // (The overhead's join, E9, is left exact: its circle and the initial point lie on the map, PatternSide.)
        if (u.AirOps is { Phase: FlightPhase.Holding } or { Phase: FlightPhase.Final, Leg: 0 } && EdgeSide(u, f, aim, vx, vy) is var edge and not 0)
        {
            // The fly zone (E3 edge avoidance, E12 fly zone) while it holds (or flies to the hold) and on the final's join (leg 0):
            // the holding orbit may reach over an edge, and the way to the join's turn circle may head
            // off the map. The final itself (from the roll-out onto the centre line) is left exact.
            (vx, vy) = HardTurn(vx, vy, edge, speed, f.Wing is null ? f.TurnPermille : (int)Math.Max(1, max * 1000 / Math.Max(1, speed)));
            wl = 0;
        }
        if (wl > 0)
        {
            long tx = wx * speed / wl, ty = wy * speed / wl;
            var vl = Len3(vx, vy, 0);
            if (turn != 0 && vl > 0 && (tx * vx + ty * vy) * 1000 < -940 * speed * vl)
            {
                (tx, ty) = turn > 0 ? (-vy * speed / vl, vx * speed / vl) : (vy * speed / vl, -vx * speed / vl);
            }
            else if (vl > 0 && tx * vx + ty * vy < 0)
            {
                // Behind the beam (E12): a full turn toward the aim's side. The drone law alone barely turns
                // with its aim astern (the controller's turning-home rule, E2): a Watcher that rolled onto the
                // centre line past its join's entry point flew on low over the runway and beyond for a hundred
                // seconds before it came round.
                (tx, ty) = vx * ty - vy * tx >= 0 ? (-vy * speed / vl, vx * speed / vl) : (vy * speed / vl, -vx * speed / vl);
            }
            long dx = tx - vx, dy = ty - vy;
            var dl = Len3(dx, dy, 0);
            if (dl > max)
            {
                (dx, dy) = (dx * max / dl, dy * max / dl);
            }
            (vx, vy) = (vx + dx, vy + dy);
        }
        var vl2 = Len3(vx, vy, 0);
        if (vl2 > 0)
        {
            (vx, vy) = (vx * speed / vl2, vy * speed / vl2);
        }
        return FlyStep(u, vx, vy); // the exact velocity kept (never stuck at an edge), clamps counted
    }

    // EaseSpeed moves ops's speed toward want at its aircraft's takeoff acceleration a tick.
    private static void EaseSpeed(AirOps ops, AircraftSpec ac, long want) =>
        ops.Speed = ops.Speed < want ? Math.Min(want, ops.Speed + ac.TakeoffAccel.Raw) : Math.Max(want, ops.Speed - ac.TakeoffAccel.Raw);

    // FlyHolding: it orbits the hold point at cruise speed, at the stack height of its place in the
    // landing queue (the top place without one, or past the last place); without a station it
    // looks for one every second.
    private void FlyHolding(Unit u, ScoutFlight f, AircraftSpec ac, Airfield af, AirOps ops)
    {
        if (Tick % TicksPerSecond == 0 && !RunwayFor(af, ac, out _) && Divert(u, ac, af, ops))
        {
            if (u.AirOps is { } next && Airfield(next.Airfield) is { } to)
            {
                FlyHolding(u, f, ac, to, next); // on to the other airfield
            }
            return; // free flight from next tick (its ground control station)
        }
        if (ops.Station == 0 && Tick % TicksPerSecond == 0)
        {
            ReserveStation(u, af, ac, ops);
        }
        var t = af.Type;
        var place = t.HoldPlaces - 1;
        for (var i = 0; i < af.LandingList.Count && i < place; i++)
        {
            if (af.LandingList[i].Aircraft == u.Id)
            {
                place = i;
            }
        }
        var rw = HoldGeo(af, ac); // no usable stretch (craters): it still holds off the runway's east end
        EaseSpeed(ops, ac, u.Def.Speed.Raw * TicksPerSecond);
        var aim = OrbitPoint(u, HoldPoint(af, rw), t.HoldRadius.Raw);
        var pos = Steer(u, f, aim, ops.Speed / TicksPerSecond, 0);
        var want = Math.Max(af.PadRaw + t.HoldHeightRaw + place * t.HoldStepRaw, GroundRaw(pos) + ScoutFloorRaw);
        var alt = u.Altitude.Raw + Math.Clamp(want - u.Altitude.Raw, -f.ClimbPerTick, f.ClimbPerTick);
        u.VelZ = Fixed.FromRaw(alt - u.Altitude.Raw);
        u.Altitude = Fixed.FromRaw(alt);
    }

    // AtHold reports whether a holding aircraft has come to the hold (within twice the orbit's radius).
    private static bool AtHold(Unit u, Airfield af, RunwayGeo rw) => Within(u.Pos, HoldPoint(af, rw), af.Type.HoldRadius + af.Type.HoldRadius);

    // ApproachRadius is its turn radius at its approach speed (raw): a drone's at its turn rate, a fixed wing's
    // its tightest within its envelope (E9).
    private static long ApproachRadius(AircraftSpec ac, ScoutFlight f) => f.RadiusAt(ac.ApproachSpeed.Raw);

    // JoinGeo is the join of a final: the entry point on the extended centre line (twice the larger
    // of the carrot and its approach turn radius out beyond the fix), the turn circle tangent to the
    // final there on the given side (+1: the +y side of the airfield's frame), its radius (half as
    // wide again as its approach turn radius) and its sense (clockwise on the map for the -y side:
    // either way it rolls out at the entry point heading inbound).
    private static (long EntryX, long CentreX, long CentreY, long R) JoinGeo(AircraftSpec ac, ScoutFlight f, RunwayGeo rw, int side)
    {
        if (ac.Pattern == ApproachPattern.Overhead)
        {
            // The overhead (E9): the turn circle (its join radius) is tangent to the centre line at the initial point.
            var ip = rw.East + ac.InitialDistance.Raw;
            return (ip, ip, rw.Centre + side * ac.JoinRadius.Raw, ac.JoinRadius.Raw);
        }
        return StraightJoin(ac, ApproachRadius(ac, f), rw, side);
    }

    // JoinBand is how far inside the map's edges a straight-in join's circle must keep its far and outer sides (E11)
    // for the edge avoidance that runs on the join (leg 0) to let it fly the circle once it flies at its approach
    // speed: that avoidance's narrow band, its turn radius plus a second of travel (the Watcher: about 14.6 cells,
    // the Overseer 11.7).
    private static long JoinBand(AircraftSpec ac) => ac.ApproachRadius.Raw + ac.ApproachSpeed.Raw * TicksPerSecond;

    // StraightJoinFits reports whether a straight-in join's circle on the given side of the centre line fits (E11):
    // on the map with its JoinBand to spare beyond its far side (away from the runway) and its outer side (away from
    // the centre line), where it flies toward the edge; its near side is the centre line itself, where it rolls out
    // heading inbound (along the edge, so no band). The Watcher's: about 79 cells beyond the approach end and 43 to
    // the side; the Overseer's 73 and 35.
    private bool StraightJoinFits(Airfield af, AircraftSpec ac, RunwayGeo rw, int side)
    {
        var j = StraightJoin(ac, ac.ApproachRadius.Raw, rw, side);
        var band = JoinBand(ac);
        var outer = rw.Centre + side * (2 * j.R + band);
        return LocalBoxOnMap(af, j.CentreX - j.R, Math.Min(rw.Centre, outer), j.CentreX + j.R + band, Math.Max(rw.Centre, outer));
    }

    // JoinFits reports whether aircraft ac's join onto the final on the given side fits on the map (ClearToLand).
    private bool JoinFits(Airfield af, AircraftSpec ac, ScoutFlight f, RunwayGeo rw, int side) =>
        ac.Pattern == ApproachPattern.Overhead ? CircleOnMap(af, JoinGeo(ac, f, rw, side)) : StraightJoinFits(af, ac, rw, side);

    // StraightJoin is a straight-in join for approach turn radius r (JoinGeo).
    private static (long EntryX, long CentreX, long CentreY, long R) StraightJoin(AircraftSpec ac, long r, RunwayGeo rw, int side)
    {
        var entry = rw.East + ac.FixDistance.Raw + 2 * Math.Max(CarrotCells * Fixed.OneRaw, r);
        var rc = r * 3 / 2;
        return (entry, entry, rw.Centre + side * rc, rc);
    }

    // JoinAim is where an aircraft steers to join the circle round c of radius r (cw: clockwise on
    // the map): from outside, the circle's tangent point that it meets the right way round (so it
    // flies straight on to it and onto the circle); on it or inside, the orbit's point ahead.
    private static Vec JoinAim(Unit u, Vec c, long r, bool cw)
    {
        long ax = u.Pos.X.Raw - c.X.Raw, ay = u.Pos.Y.Raw - c.Y.Raw;
        var d2 = ax * ax + ay * ay;
        if (d2 <= r * r * 441 / 400)
        {
            return OrbitPoint(u, c, r, cw);
        }
        var s = Fixed.Sqrt(d2 - r * r) * (cw ? 1 : -1);
        return new Vec(Fixed.FromRaw(c.X.Raw + r * (r * ax - s * ay) / d2), Fixed.FromRaw(c.Y.Raw + r * (r * ay + s * ax) / d2));
    }

    // ClearToLand gives u the runway: its final begins with the join on the side of the centre line
    // it is on (the other side when that one does not fit and the other does: JoinFits).
    private void ClearToLand(Unit u, ScoutFlight f, AircraftSpec ac, Airfield af, RunwayGeo rw, AirOps ops)
    {
        af.RunwayLock = u.Id;
        var (_, ly) = MapToLocal(af, u.Pos);
        var side = ly >= rw.Centre ? 1 : -1;
        if (!JoinFits(af, ac, f, rw, side) && JoinFits(af, ac, f, rw, -side))
        {
            side = -side;
        }
        ops.Phase = FlightPhase.Final;
        ops.Side = side;
        ops.Leg = ac.Pattern == ApproachPattern.Overhead ? LegOhJoin : 0;
        (ops.RwWest, ops.RwEast) = (rw.West, rw.East); // a crater cutting it sends it around (M3b)
        _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.Cleared, u.Id, af.Id, u.Pos));
    }

    // CircleOnMap reports whether a join's turn circle lies on the map.
    private bool CircleOnMap(Airfield af, (long EntryX, long CentreX, long CentreY, long R) j)
    {
        var c = LocalToMap(af, j.CentreX, j.CentreY);
        long w = (long)Grid.Width * Fixed.OneRaw, h = (long)Grid.Height * Fixed.OneRaw;
        return c.X.Raw - j.R >= 0 && c.Y.Raw - j.R >= 0 && c.X.Raw + j.R <= w && c.Y.Raw + j.R <= h;
    }

    // GoAround: it gives the runway back and rejoins the landing queue at its head (misaligned at
    // the fix, or a crater cut its stretch).
    private void GoAround(Unit u, Airfield af, AirOps ops)
    {
        if (af.RunwayLock == u.Id)
        {
            af.RunwayLock = 0;
        }
        ops.Phase = FlightPhase.Holding;
        ops.Leg = 0;
        (ops.RwWest, ops.RwEast) = (0, 0);
        Sim.Airfield.Dequeue(af.LandingList, u.Id);
        if (ops.Station != 0)
        {
            Sim.Airfield.Enqueue(af.LandingList, 0, u.Id); // with no station it queues again once it has one
        }
        _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.GoAround, u.Id, af.Id, u.Pos));
    }

    // Glide is the height (raw, over the runway) at distance x (raw) before the touchdown point: a
    // straight descent from the fix (fixX out, fixH up) aimed half the flare short of the touchdown
    // point, rounding off over the flare into a level touchdown (height and slope continuous).
    private static long Glide(long x, long fixH, long fixX, long flare)
    {
        if (x <= 0)
        {
            return 0;
        }
        var span = Math.Max(1, fixX - flare / 2);
        return x >= flare ? fixH * (x - flare / 2) / span : fixH * x * x / (2 * flare * span);
    }

    // FlyFinal: the join (leg 0: it circles onto the turn circle tangent to the final at the entry
    // point, as it orbits a point, until it rolls out there heading inbound near the centre line),
    // then the final (leg 1: a carrot CarrotCells, or its turn radius, ahead on the centre line,
    // descending to the fix height) and from the fix the glide (leg 2). At the fix it must be on the
    // line, on the heading and down at the fix height, else it goes around. It slows to its approach
    // speed, and over the flare to its touchdown speed; it never climbs. Past the touchdown point it
    // is down: the roll-out.
    private void FlyFinal(Unit u, ScoutFlight f, AircraftSpec ac, Airfield af, AirOps ops)
    {
        if (ops.RwEast <= ops.RwWest || !StretchOpen(af, ops.RwWest, ops.RwEast))
        {
            GoAround(u, af, ops); // a crater cut the stretch it was cleared for (M3b)
            FlyHolding(u, f, ac, af, ops);
            return;
        }
        var rw = GeoOf(af, ops.RwWest, ops.RwEast);
        if (ops.Station == 0 && Tick % TicksPerSecond == 0)
        {
            ReserveStation(u, af, ac, ops); // its station fell: another, if one is free
        }
        if (ops.Leg >= LegOhJoin)
        {
            FlyOverhead(u, f, ac, af, ops, rw); // the overhead pattern up to its short final (E9)
            return;
        }
        var (lx, ly) = MapToLocal(af, u.Pos);
        long fix = ac.FixDistance.Raw, td = ac.Touchdown.Raw;
        var carrot = Math.Max(CarrotCells * Fixed.OneRaw, ApproachRadius(ac, f));
        var x = lx - rw.East + td; // to the touchdown point
        EaseSpeed(ops, ac, (x < ac.Flare.Raw ? ac.TouchdownSpeed.Raw : ac.ApproachSpeed.Raw) * TicksPerSecond);
        var speed = ops.Speed / TicksPerSecond;
        var inbound = MapFacing(af, Facing.West);
        var cw = ops.Side < 0;
        if (ops.Leg == 0)
        {
            var j = JoinGeo(ac, f, rw, ops.Side);
            // Round the circle to the entry point: within 20 degrees of its radial, on the circle
            // (half to five quarters of its radius out), heading within three facing steps of inbound.
            long rx = lx - j.CentreX, ry = (ly - j.CentreY) * -ops.Side;
            if (ry >= j.R / 2 && Len3(rx, ry, 0) <= j.R * 5 / 4 && Math.Abs(rx) * 1000 <= 364 * ry && Math.Abs(Facing.Diff(u.Facing, inbound)) <= 3)
            {
                ops.Leg = 1; // rolled out at the entry point
            }
        }
        Vec aim;
        if (ops.Leg == 0)
        {
            var j = JoinGeo(ac, f, rw, ops.Side);
            aim = JoinAim(u, LocalToMap(af, j.CentreX, j.CentreY), j.R, cw);
        }
        else
        {
            aim = LocalToMap(af, lx - carrot, rw.Centre);
        }
        var pos = Steer(u, f, aim, speed, cw ? 1 : -1);
        (lx, ly) = MapToLocal(af, pos);
        x = lx - rw.East + td;
        var pad = af.PadRaw;
        if (ops.Leg == 1 && lx - rw.East <= fix)
        {
            var off = Math.Abs(Facing.Diff(u.Facing, inbound));
            if (Math.Abs(ly - rw.Centre) > GoAroundOffRaw || off > GoAroundFacings || u.Altitude.Raw - pad > ac.FixHeightRaw + FixSlackRaw)
            {
                GoAround(u, af, ops);
                return;
            }
            ops.Leg = 2;
        }
        var want = pad + (ops.Leg == 2 ? Glide(x, ac.FixHeightRaw, fix + td, ac.Flare.Raw) : ac.FixHeightRaw);
        var alt = Math.Clamp(want, u.Altitude.Raw - f.ClimbPerTick, u.Altitude.Raw);
        alt = Math.Max(alt, GroundRaw(pos));
        u.VelZ = Fixed.FromRaw(alt - u.Altitude.Raw);
        u.Altitude = Fixed.FromRaw(alt);
        if (ops.Leg == 2 && x <= 0)
        {
            ops.Leg = 0;
            Touchdown(u, ac, af, rw, ops);
        }
    }

    // FlyOverhead flies the overhead pattern's legs (E9; decision D17) up to the short final: the join onto
    // the initial point (the tangent-circle join of the straight-in, its circle tangent at the initial point,
    // climbing or descending to pattern height and slowing to break speed), the run-in down the centre line
    // to midfield, the level break turn (a half circle of its pattern radius away from the rows, or over them
    // when only that side fits: PatternSide), the downwind leg two radii out slowing to approach speed (the gear
    // comes down at its start, abeam midfield) and from the perch (abeam the base point) the descending base
    // turn (a half circle down to the fix height) that rolls out on the centre line a turn radius before the
    // fix; then the straight-in's final from leg 1 (the go-around check at the fix, the glide, the flare). A
    // fixed wing's turns stay within its envelope (Steer: a(S)); the pattern radius is 5/4 of its tightest.
    private void FlyOverhead(Unit u, ScoutFlight f, AircraftSpec ac, Airfield af, AirOps ops, RunwayGeo rw)
    {
        var (lx, ly) = MapToLocal(af, u.Pos);
        var side = PatternSide(af, ac, rw);
        side = side == 0 ? -1 : side; // the map cannot change under it; a guard all the same
        long r = ac.PatternRadius.Raw, mid = BreakX(af, rw), xBase = BaseX(ac, rw), c = rw.Centre;
        int inbound = MapFacing(af, Facing.West), outbound = MapFacing(af, Facing.East);
        var off = (ly - c) * side; // out from the centre line on the pattern's side
        switch (ops.Leg)
        {
            case LegOhJoin:
            {
                var j = JoinGeo(ac, f, rw, ops.Side);
                long rx = lx - j.CentreX, ry = (ly - j.CentreY) * -ops.Side;
                if (ry >= j.R / 2 && Len3(rx, ry, 0) <= j.R * 5 / 4 && Math.Abs(rx) * 1000 <= 364 * ry && Math.Abs(Facing.Diff(u.Facing, inbound)) <= 3)
                {
                    ops.Leg = LegOhInitial; // rolled out over the initial point, inbound
                    _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.Initial, u.Id, af.Id, u.Pos));
                }
                break;
            }
            case LegOhInitial when lx <= mid:
                ops.Leg = LegOhBreak;
                _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.Break, u.Id, af.Id, u.Pos));
                break;
            case LegOhBreak when off >= r && Math.Abs(Facing.Diff(u.Facing, outbound)) <= 1:
                ops.Leg = LegOhDownwind;
                _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.GearDown, u.Id, af.Id, u.Pos));
                break;
            case LegOhDownwind when lx >= xBase:
                ops.Leg = LegOhBase;
                _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.Perch, u.Id, af.Id, u.Pos));
                break;
            case LegOhBase when off <= r / 2 && Math.Abs(Facing.Diff(u.Facing, inbound)) <= 2:
                ops.Leg = 1; // rolled out on the short final: the straight-in's final from here
                break;
        }
        EaseSpeed(ops, ac, (ops.Leg is LegOhJoin or LegOhInitial ? ac.BreakSpeed.Raw : ac.ApproachSpeed.Raw) * TicksPerSecond);
        var speed = ops.Speed / TicksPerSecond;
        var cw = side < 0; // both half turns run the same way round: clockwise on the map for the -y side
        Vec aim;
        var turn = cw ? 1 : -1;
        var carrot = Math.Max(CarrotCells * Fixed.OneRaw, r);
        switch (ops.Leg)
        {
            case LegOhJoin:
            {
                var j = JoinGeo(ac, f, rw, ops.Side);
                aim = JoinAim(u, LocalToMap(af, j.CentreX, j.CentreY), j.R, ops.Side < 0);
                turn = ops.Side < 0 ? 1 : -1;
                break;
            }
            case LegOhInitial:
                aim = LocalToMap(af, lx - carrot, c);
                break;
            case LegOhBreak:
                aim = OrbitPoint(u, LocalToMap(af, mid, c + side * r), r, cw);
                break;
            case LegOhDownwind:
                aim = LocalToMap(af, lx + carrot, c + side * 2 * r);
                break;
            case LegOhBase:
                aim = OrbitPoint(u, LocalToMap(af, xBase, c + side * r), r, cw);
                break;
            default:
                aim = LocalToMap(af, lx - carrot, c); // rolled out this tick
                break;
        }
        var pos = Steer(u, f, aim, speed, turn);
        (_, ly) = MapToLocal(af, pos);
        var pad = af.PadRaw;
        var want = pad + ac.PatternHeightRaw;
        if (ops.Leg is LegOhBase or 1)
        {
            // The base turn descends with the way round it: from pattern height two radii out to the fix height on the line.
            var o = Math.Clamp((ly - c) * side, 0, 2 * r);
            want = pad + ac.FixHeightRaw + (ac.PatternHeightRaw - ac.FixHeightRaw) * o / Math.Max(1, 2 * r);
        }
        var down = f.Wing is { } g ? Math.Max(f.ClimbPerTick, g.DivePerTick) : f.ClimbPerTick;
        var alt = u.Altitude.Raw + Math.Clamp(want - u.Altitude.Raw, -down, ops.Leg is LegOhBase or 1 ? 0 : f.ClimbPerTick); // never climbs from the base on
        alt = Math.Max(alt, GroundRaw(pos));
        u.VelZ = Fixed.FromRaw(alt - u.Altitude.Raw);
        u.Altitude = Fixed.FromRaw(alt);
    }

    // Touchdown: wheels down, the roll-out begins on the path to its station: along the centre line
    // to its exit connector, the taxi graph's shortest way to its row's lane node, then its
    // station's park-in path. Marks: the exit turn (off the runway), the lane mouth, the park-in.
    private void Touchdown(Unit u, AircraftSpec ac, Airfield af, RunwayGeo rw, AirOps ops)
    {
        var t = af.Type;
        u.Altitude = Fixed.FromRaw(GroundRaw(u.Pos));
        u.VelZ = Fixed.Zero;
        ops.Phase = FlightPhase.Rollout;
        (ops.RwWest, ops.RwEast) = (0, 0);
        if (ac.AerobrakeSpeed.Raw > 0 && ops.Speed > ac.AerobrakeSpeed.Raw * TicksPerSecond)
        {
            ops.Leg = LegAerobrake; // the nose held up (E9) until NoseDown
        }
        _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.Touchdown, u.Id, af.Id, u.Pos));
        var g = AirfieldTaxiGraph(af);
        var exit = FindNode(g, TaxiNodeKind.RunwayConnector, ConnectorTenths(t, ExitConnector(t, ac, rw)), t.RunwayCells * 5);
        var (row, st) = StationOf(af, ops.Station);
        var pts = _pathScratch;
        pts.Clear();
        pts.Add(u.Pos);
        var nodes = new List<int>();
        var lane = row is null ? -1 : RowLane(g, t, row);
        var (_, prev) = TaxiRoute(g, exit);
        if (lane < 0 || !AddRoute(g, prev, exit, lane, pts, nodes))
        {
            // No station to go to (M3: a station gone): it stops on the taxiway past its exit.
            pts.Add(g.Nodes[exit].At);
            pts.Add(LocalToMap(af, ConnectorX(t, ExitConnector(t, ac, rw)), (long)(t.RunwayCells + t.GrassCells) * Fixed.OneRaw + (long)t.TaxiwayCells * Fixed.OneRaw / 2));
            var at0 = new int[pts.Count];
            SetPath(ops, pts, at0);
            (ops.MarkA, ops.MarkB, ops.MarkMouth) = (at0[1], at0[^1] + 1, -1);
            if (st is not null && st.ReservedBy == u.Id)
            {
                st.ReservedBy = 0;
            }
            ops.Station = 0;
            return;
        }
        var mouth = 1;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (g.Nodes[nodes[i]].Kind == TaxiNodeKind.Lane)
            {
                mouth = Math.Max(1, i); // the node before the first lane node (pts is one ahead of nodes)
                break;
            }
        }
        var laneAt = pts.Count - 1;
        var slot = SlotsOf(t, row!)[st!.Slot];
        for (var i = 1; i < slot.ParkIn.Count; i++)
        {
            pts.Add(SlotPoint(af, row!, slot.ParkIn[i]));
        }
        var at = new int[pts.Count];
        SetPath(ops, pts, at);
        (ops.MarkA, ops.MarkMouth, ops.MarkB) = (at[1], at[mouth], at[laneAt]);
    }

    // TaxiIn: the roll-out (down to taxi speed at its roll-out deceleration) releases the runway as
    // it turns onto its exit connector; then it taxis, waiting at its lane mouth while another
    // aircraft leaves that lane (at most the type's taxi wait), and parks at the stop mark.
    private void TaxiIn(Unit u, AircraftSpec ac, Airfield af, AirOps ops)
    {
        if (ops.Phase == FlightPhase.Rollout && (ops.Leg is LegCraterStop or LegCraterHit || RollCheck(u, ac, af, ops, -1)))
        {
            CraterStop(u, ac, af, ops); // a crater ahead (M3b)
            return;
        }
        if (ops.Phase != FlightPhase.Rollout && ops.Station == 0 && ops.MarkMouth >= 0 && !TaxiToStation(u, ac, af, ops, -1))
        {
            NoStationAhead(ops); // its station fell and none is free: it stops short and waits (M3b)
        }
        var stop = -1;
        if (ops.MarkMouth >= 0 && ops.PathIdx <= ops.MarkMouth && ops.Wait < af.Type.TaxiWaitTicks && LaneBusy(u, af, ops))
        {
            stop = ops.MarkMouth;
            if (ops.PathIdx == ops.MarkMouth && u.Pos == ops.PathPts[ops.MarkMouth])
            {
                ops.Wait++;
            }
        }
        var done = Taxi(u, ac, ops, TaxiV(af.Type, ac) * TicksPerSecond, true, stop, false);
        if (ops.Phase == FlightPhase.Rollout && ops.Leg == LegAerobrake && (ops.Speed <= ac.AerobrakeSpeed.Raw * TicksPerSecond || ops.PathIdx > ops.MarkA))
        {
            ops.Leg = 0; // slowed (or turning off): the nose comes down, wheel braking from here (E9)
            _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.NoseDown, u.Id, af.Id, u.Pos));
        }
        if (ops.Phase == FlightPhase.Rollout && ops.PathIdx > ops.MarkA)
        {
            ops.Phase = FlightPhase.TaxiIn;
            if (af.RunwayLock == u.Id)
            {
                af.RunwayLock = 0;
            }
            _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.RunwayVacated, u.Id, af.Id, u.Pos));
        }
        if (ops.Phase == FlightPhase.TaxiIn && ops.PathIdx > ops.MarkB)
        {
            ops.Phase = FlightPhase.Parking;
        }
        if (ops.MarkMouth >= 0 && ops.PathIdx > ops.MarkMouth)
        {
            ops.Wait = 0;
        }
        if (!done)
        {
            return;
        }
        ops.Phase = FlightPhase.Parked;
        ops.Wait = 0;
        var (_, st) = StationOf(af, ops.Station);
        if (st is not null)
        {
            st.Occupant = u.Id;
            st.ReservedBy = 0;
        }
        _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.Parked, u.Id, af.Id, u.Pos));
    }

    // LaneBusy reports whether another aircraft is leaving the lane of u's station: pushing back,
    // or taxiing out and not yet past its lane mouth.
    private bool LaneBusy(Unit u, Airfield af, AirOps ops)
    {
        var (row, _) = StationOf(af, ops.Station);
        if (row is null)
        {
            return false;
        }
        foreach (var o in _units)
        {
            if (o != u && o.Alive && o.AirOps is { } oo &&
                (oo.Phase == FlightPhase.PushBack || (oo.Phase == FlightPhase.TaxiOut && oo.PathIdx <= oo.MarkMouth)) &&
                (oo.Airfield == af.Id ? af : Airfield(oo.Airfield)) is { } oaf && StationOf(oaf, oo.Station).Row is { } orow &&
                orow.Field == row.Field && orow.Col == row.Col)
            {
                return true; // (H1: the same lane of the same airfield, whichever airfield the other uses)
            }
        }
        return false;
    }

    // StayParked: it turns to its slot's heading and takes its station's service with fuel not
    // burning (M3b: refuel, rearm and repair at the station's rates, nothing at a loading station
    // while the facility is down; AirfieldServiceSystem.cs; the owner's rules, E2: an automatic
    // refuel stop or a recall refuels only, a Rearm order gets everything). It leaves on an order,
    // or by itself once serviced (a full tank; on a Rearm order full ammunition and HP too) unless
    // held (a recall, or Stop), back to its previous task. At a fallen station (rubble or gone)
    // it gets nothing and stays put until ordered off. Parked off any station (none was free) it
    // taxis to one as soon as one frees.
    private void StayParked(Unit u, AircraftSpec ac, Airfield af, AirOps ops)
    {
        u.Vel = default;
        u.VelZ = Fixed.Zero;
        ops.Speed = 0;
        var (row, st) = StationOf(af, ops.Station);
        if (st is not null)
        {
            var want = MapFacing(row!.Field ?? af, SlotsOf(af.Type, row)[st.Slot].Heading);
            u.Facing = Facing.TurnToward(u.Facing, want, 1);
            u.TurretFacing = u.Facing;
        }
        var fallen = ops.Station != 0 && (st is null || !StationStands(af, st));
        var serviced = Service(u, ac, ops, fallen ? null : ServingSpec(af, ops), RearmVisit(u), StationStore(af, ops)); // refuel only, unless sent to rearm
        if (ops.Station == 0 && !ops.Launch && Tick % TicksPerSecond == 0 && TaxiToStation(u, ac, af, ops, -1))
        {
            return; // a station freed: it taxis there
        }
        if (u.HomeReason == HomeReason.Deliver && !fallen && !ops.Launch && DeliverParked(u, af, ops))
        {
            return; // unloading its pallets into the field's stock (logistics, AirdropSystem.cs)
        }
        if (!ops.Launch && (fallen || !ac.Sortie || ops.Hold || !serviced))
        {
            return;
        }
        if (!ops.Launch && Lifting(u))
        {
            ops.Hold = true; // vehicles aboard (or going up its ramp): serviced, it waits for an order (decision 315, AirliftSystem.cs)
            return;
        }
        ops.Launch = false;
        (ops.FuelAcc, ops.AmmoAcc, ops.HpAcc) = (0, 0, 0);
        if (st is not null && st.Occupant == u.Id)
        {
            st.Occupant = 0;
        }
        _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.Departed, u.Id, af.Id, u.Pos));
        var slotIdx = st?.Slot ?? -1;
        if (fallen && st is null)
        {
            (row, slotIdx) = SlotAt(af, u.Pos); // its station is gone: out by its slot's way all the same
        }
        if (row is null || slotIdx < 0)
        {
            TaxiOutFrom(u, ac, af, ops, []); // no station: from the nearest taxi node
            return;
        }
        var slot = SlotsOf(af.Type, row)[slotIdx];
        var outPts = new List<Vec>();
        foreach (var p in slot.ParkOut)
        {
            outPts.Add(SlotPoint(af, row, p));
        }
        if (slot.Pushback)
        {
            ops.Phase = FlightPhase.PushBack;
            var at = new int[outPts.Count];
            SetPath(ops, outPts, at);
            return;
        }
        TaxiOutFrom(u, ac, af, ops, outPts, -1, row);
    }

    // TaxiOutFrom starts its taxi out: through `first` (its park-out path), then the taxi graph's
    // shortest way from node `from` (else its row's lane node: row, or its station's; else the taxi
    // node nearest to it) to the holding point beside the line-up connector. Leaving a row, its lane
    // mouth is marked: past it, it has left its lane.
    private void TaxiOutFrom(Unit u, AircraftSpec ac, Airfield af, AirOps ops, List<Vec> first, int from = -1, AirfieldRow? row = null)
    {
        var t = af.Type;
        ops.Phase = FlightPhase.TaxiOut;
        ops.Speed = 0;
        var pts = _pathScratch;
        pts.Clear();
        pts.Add(u.Pos);
        for (var i = 1; i < first.Count; i++)
        {
            pts.Add(first[i]);
        }
        var g = AirfieldTaxiGraph(af);
        row ??= from >= 0 ? null : StationOf(af, ops.Station).Row;
        var mouth = -1;
        var k0 = RunwayFor(af, ac, out var rw) ? rw.K0 : FirstConnector(af);
        var hold = af.HasRunway ? FindNode(g, TaxiNodeKind.TaxiwayConnector, ConnectorTenths(t, k0), (t.RunwayCells + t.GrassCells) * 10 + t.TaxiwayCells * 5) : -1;
        var lane = from >= 0 ? from : row is not null ? RowLane(g, t, row) : NearestTaxiNode(g, u.Pos);
        var routePts = new List<Vec>();
        var nodes = new List<int>();
        if (hold >= 0 && lane >= 0 && AddRoute(g, TaxiRoute(g, lane).Prev, lane, hold, routePts, nodes))
        {
            var before = pts.Count;
            var skip = pts[^1] == routePts[0] ? 1 : 0;
            for (var i = skip; i < routePts.Count; i++)
            {
                pts.Add(routePts[i]);
            }
            for (var i = 0; row is not null && i < nodes.Count; i++)
            {
                if (g.Nodes[nodes[i]].Kind is TaxiNodeKind.LaneMouth or TaxiNodeKind.TaxiwayConnector)
                {
                    mouth = before + i - skip;
                    break;
                }
            }
        }
        else if (hold >= 0)
        {
            pts.Add(g.Nodes[hold].At);
        }
        var at = new int[pts.Count];
        SetPath(ops, pts, at);
        ops.MarkMouth = mouth >= 0 && mouth + 1 < at.Length ? at[mouth + 1] : -1;
    }

    // ClearToLineUp gives u the runway to take off: up the line-up connector onto the centre line,
    // a turn to face down it (away from the facility), then the takeoff roll to the far end and on.
    private void ClearToLineUp(Unit u, AircraftSpec ac, Airfield af, RunwayGeo rw, AirOps ops)
    {
        af.RunwayLock = u.Id;
        var t = af.Type;
        var x = ConnectorX(t, rw.K0);
        var pts = _pathScratch;
        pts.Clear();
        pts.Add(u.Pos);
        pts.Add(LocalToMap(af, x, rw.Centre));
        pts.Add(LocalToMap(af, x + CornerRadiusRaw * 2, rw.Centre));
        pts.Add(LocalToMap(af, rw.East + OverrunRaw, rw.Centre));
        var at = new int[pts.Count];
        SetPath(ops, pts, at);
        ops.MarkA = at[2] - 1; // the last point of the turn onto the runway
        ops.Leg = 1;
        ops.Speed = 0;
        ops.Afterburner = AfterburnerTakeoff(ac, rw, x);
        _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.LinedUp, u.Id, af.Id, u.Pos));
    }

    // AfterburnerTakeoff is the takeoff rule (E9, the plan's 2.12, the USAF rule): an aircraft with a military
    // takeoff acceleration takes off on dry power when its military roll (v^2 / 2a to its takeoff speed) is
    // within half the open stretch ahead of its line-up point (x, raw), else on afterburner (its takeoffAccel).
    private static bool AfterburnerTakeoff(AircraftSpec ac, RunwayGeo rw, long x)
    {
        if (ac.MilitaryAccel.Raw == 0)
        {
            return false;
        }
        var v = ac.TakeoffSpeed.Raw * TicksPerSecond;
        var roll = v * v / (2 * ac.MilitaryAccel.Raw * TicksPerSecond);
        return 2 * roll > rw.East - x;
    }

    // TakeoffAccelOf is u's takeoff roll acceleration: its afterburner's (takeoffAccel) or its military one.
    private static long TakeoffAccelOf(AircraftSpec ac, AirOps ops) =>
        ops.Afterburner || ac.MilitaryAccel.Raw == 0 ? ac.TakeoffAccel.Raw : ac.MilitaryAccel.Raw;

    // FlyClimb: wheels-up, it flies straight on down the runway's heading, climbing at its flight
    // block's rate and speeding up to its cruise speed; the type's release time after wheels-up the
    // runway is free, and once at cruise speed it flies free again: back to its orbit (its return
    // cell), or on to its order.
    private void FlyClimb(Unit u, ScoutFlight f, AircraftSpec ac, Airfield af, AirOps ops)
    {
        if (f.Wing is { } wing)
        {
            ClimbOut(u, f, wing, af, ops);
            return;
        }
        var cruise = u.Def.Speed.Raw * TicksPerSecond;
        EaseSpeed(ops, ac, cruise);
        var step = ops.Speed / TicksPerSecond;
        var (dx, dy) = Turn(af.Facing, step, 0);
        var pos = InFlyZone(f, u.Pos.X.Raw + dx, u.Pos.Y.Raw + dy); // its fly zone (E12), never the map's clamp
        if (pos.X.Raw != u.Pos.X.Raw + dx || pos.Y.Raw != u.Pos.Y.Raw + dy)
        {
            AirEdgeClamps++;
        }
        u.Vel = pos - u.Pos;
        u.Pos = pos;
        u.Facing = MapFacing(af, Facing.East);
        u.TurretFacing = u.Facing;
        var top = GroundRaw(pos) + FlightHeight(f);
        var alt = Math.Max(u.Altitude.Raw, Math.Min(top, u.Altitude.Raw + f.ClimbPerTick));
        u.VelZ = Fixed.FromRaw(alt - u.Altitude.Raw);
        u.Altitude = Fixed.FromRaw(alt);
        var since = Tick - ops.Since;
        if (since < af.Type.LockReleaseTicks)
        {
            return;
        }
        if (af.RunwayLock == u.Id)
        {
            af.RunwayLock = 0;
        }
        if (ops.Speed < cruise)
        {
            return;
        }
        u.AirOps = null;
        u.Homing = false;
        u.HomeReason = HomeReason.Refuel;
        u.OrbitAt = ops.ReturnCell.Center();
        if (ops.Recall)
        {
            Recalled(u, ops); // ReturnToBase while it climbed (M3b), or Rearm while it left: home at once
        }
    }

    // ClimbOut is a fixed wing's climb-out (E9): straight on down the runway's heading on its takeoff throttle
    // (military, or the afterburner after an afterburner takeoff), its speed changing by its envelope (thrust less
    // drag and the climb's gravity term: FixedWingSpec.SpeedChange), climbing at its climb rate scaled by its
    // speed over cruise (none at its minimum speed). The type's release time after wheels-up the runway is free;
    // once it has its corner speed too (its tightest turns are open to it) it flies free on its envelope, whose
    // edge avoidance keeps it off the map's edges: well inside the corridor it needs.
    private void ClimbOut(Unit u, ScoutFlight f, FixedWingSpec g, Airfield af, AirOps ops)
    {
        var th = ops.Afterburner && g.HasAfterburner ? Throttle.Afterburner : Throttle.Military;
        var s = ops.Speed;
        var up = s <= g.Min ? 0 : f.ClimbPerTick * Math.Min(s, g.Cruise) / g.Cruise;
        var top = GroundRaw(u.Pos) + Math.Min(FlightHeight(f), g.IngressRaw > 0 ? g.IngressRaw : FlightHeight(f)); // E13: to its ingress height (free flight climbs on)
        var dz = Math.Clamp(top - u.Altitude.Raw, 0, up);
        var vz = dz * TicksPerSecond;
        var l3 = Len3(s, vz, 0);
        ops.Speed = Math.Clamp(s + g.SpeedChange(s, th, 0, l3 > 0 ? vz * 1000 / l3 : 0), g.Min, g.Dash);
        var step = ops.Speed / TicksPerSecond;
        var (dx, dy) = Turn(af.Facing, step, 0);
        var pos = InFlyZone(f, u.Pos.X.Raw + dx, u.Pos.Y.Raw + dy); // its fly zone (E12), never the map's clamp
        if (pos.X.Raw != u.Pos.X.Raw + dx || pos.Y.Raw != u.Pos.Y.Raw + dy)
        {
            AirEdgeClamps++; // never with its corridor on the map (counted like free flight's)
        }
        u.Vel = pos - u.Pos;
        u.Pos = pos;
        u.Facing = MapFacing(af, Facing.East);
        u.TurretFacing = u.Facing;
        var alt = u.Altitude.Raw + dz;
        u.VelZ = Fixed.FromRaw(dz);
        u.Altitude = Fixed.FromRaw(alt);
        if (Tick - ops.Since < af.Type.LockReleaseTicks)
        {
            return;
        }
        if (af.RunwayLock == u.Id)
        {
            af.RunwayLock = 0;
        }
        if (ops.Speed < g.Corner)
        {
            return;
        }
        u.AirOps = null;
        u.Homing = false;
        u.HomeReason = HomeReason.Refuel;
        u.OrbitAt = ops.ReturnCell.Center();
        if (ops.Recall)
        {
            Recalled(u, ops);
        }
    }

    // Recalled sends u, flying free off a climb-out with a recall, home for the recall's reason: to its RecallTo airfield when set
    // (logistics: a Deliver order's target; after a delivery, the airfield it loads at, held there once loaded), else its best home.
    private void Recalled(Unit u, AirOps ops)
    {
        var to = ops.RecallTo != 0 ? Airfield(ops.RecallTo) : null;
        GoHome(u, to, ops.RecallReason, ops.RecallTo != 0 && ops.RecallReason == HomeReason.Rearm);
    }

    // ---- The airfield system's part: queue and clearance ----

    // AirfieldTraffic (the airfield system, after the pieces): airfields in ID order drop dead or
    // departed aircraft from their queues, lock and stations, then give a free runway to the head of
    // the landing queue once it is at the hold point, otherwise to the longest-waiting aircraft at
    // the line-up.
    private void AirfieldTraffic()
    {
        foreach (var af in _airfields)
        {
            PurgeTraffic(af);
            if (af.RunwayLock != 0)
            {
                continue;
            }
            if (af.LandingList.Count > 0 && Unit(af.LandingList[0].Aircraft) is { AirOps: { } lo } a &&
                a.Def.Archetype.Aircraft is { } lac && a.Def.Archetype.Flight is { } lf &&
                RunwayFor(af, lac, out var lrw) && AtHold(a, af, lrw))
            {
                af.LandingList.RemoveAt(0);
                ClearToLand(a, lf, lac, af, lrw, lo);
                continue;
            }
            if (af.TakeoffList.Count > 0 && Unit(af.TakeoffList[0].Aircraft) is { AirOps: { } to } b &&
                b.Def.Archetype.Aircraft is { } tac && RunwayFor(af, tac, out var trw))
            {
                af.TakeoffList.RemoveAt(0);
                ClearToLineUp(b, tac, af, trw, to);
            }
        }
    }

    // PurgeTraffic keeps af's queues, lock, reservations and occupants to living aircraft that are
    // still in the matching phase at af.
    private void PurgeTraffic(Airfield af)
    {
        bool At(uint id, out AirOps ops)
        {
            ops = null!;
            if (Unit(id) is { Alive: true, AirOps: { } o } && UsesField(af, o.Airfield)) // (H1: a linked base's aircraft too)
            {
                ops = o;
                return true;
            }
            return false;
        }
        af.LandingList.RemoveAll(e => !At(e.Aircraft, out var o) || o.Phase != FlightPhase.Holding);
        af.TakeoffList.RemoveAll(e => !At(e.Aircraft, out var o) || o.Phase != FlightPhase.LineUp || o.Leg != 0);
        if (af.RunwayLock != 0 && (!At(af.RunwayLock, out var lo) ||
            lo.Phase is not (FlightPhase.Final or FlightPhase.Rollout or FlightPhase.LineUp or FlightPhase.TakeoffRoll or FlightPhase.Climb) ||
            (lo.Phase == FlightPhase.LineUp && lo.Leg == 0)))
        {
            af.RunwayLock = 0;
        }
        foreach (var r in af.RowList)
        {
            foreach (var st in r.Stations)
            {
                if (st.ReservedBy != 0 && !HeliKeepsStation(st.ReservedBy, st.Unit) &&
                    (!At(st.ReservedBy, out var ro) || ro.Station != st.Unit || ro.Phase > FlightPhase.Parking || !StationStands(af, st)))
                {
                    st.ReservedBy = 0;
                }
                if (st.Occupant != 0 && !HeliKeepsStation(st.Occupant, st.Unit) && (!At(st.Occupant, out var oo) || oo.Station != st.Unit || oo.Phase != FlightPhase.Parked))
                {
                    st.Occupant = 0;
                }
            }
        }
    }
}
