using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>What happened to a Track order this tick (<see cref="World.TrackEvents"/>).</summary>
public enum TrackEventKind : byte
{
    /// <summary>It locked onto its target (at the order, or when it came within its sight).</summary>
    Locked,

    /// <summary>It lost its target (<see cref="TrackEvent.Why"/>) and searches round <see cref="TrackEvent.Cell"/>, the last known position.</summary>
    Lost,

    /// <summary>Searching, it spotted the same unit again and locked onto it.</summary>
    Relocked,

    /// <summary>The search found nothing in its time: the track is over and it holds a normal orbit over the last known position.</summary>
    GaveUp,

    /// <summary>The target died (or is gone): the track is over and it orbits where it was.</summary>
    TargetGone,

    /// <summary>It turned for home (bingo fuel, Rearm, Return to base): the track is over; it comes back to orbit where it was.</summary>
    Home,
}

/// <summary>Why a Track lost its target (<see cref="TrackEvent.Why"/>).</summary>
public enum TrackLoss : byte
{
    /// <summary>Not a loss.</summary>
    None,

    /// <summary>The target left its sight (level distance from the point under it beyond its sight range).</summary>
    OutOfSight,

    /// <summary>The target went inside a building, tunnel or vehicle (or into a garrison).</summary>
    Inside,

    /// <summary>Still flying to it, its side stopped spotting the target before it was in its own sight.</summary>
    Unspotted,
}

/// <summary>One thing a Track order did this tick (for the client's toasts, voice lines and demos).</summary>
/// <param name="Drone">The tracking drone.</param>
/// <param name="Player">Its player.</param>
/// <param name="Target">The tracked unit.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Why">Why it lost the target (<see cref="TrackEventKind.Lost"/>), else <see cref="TrackLoss.None"/>.</param>
/// <param name="Cell">The target's cell (locked), the last known position (lost, gave up) or where it orbits now.</param>
public readonly record struct TrackEvent(uint Drone, byte Player, uint Target, TrackEventKind Kind, TrackLoss Why, Cell Cell);

/// <summary>
/// A tracking drone's state (<see cref="World.TrackOf"/>): for the card ("Tracking ...", "Searching · lost 12 s ago"), the lock
/// brackets and the search circle.
/// </summary>
/// <param name="Target">The unit it tracks.</param>
/// <param name="Phase">Approaching, locked or searching.</param>
/// <param name="LastSeen">Where the target was last spotted (the search's centre).</param>
/// <param name="LostAt">Searching: the tick it lost the target; else 0.</param>
/// <param name="SearchFrom">Searching: the tick the search began to widen (once the last known position was within its sight); 0 before.</param>
/// <param name="SearchEnds">Searching: the tick it gives up (0 until the search began to widen).</param>
/// <param name="Radius">The orbit radius it flies now: its orbit radius, wider while searching.</param>
/// <param name="InSight">The target stands within its sight now (level distance from the point under it).</param>
public readonly record struct TrackStatus(uint Target, TrackPhase Phase, Vec LastSeen, uint LostAt, uint SearchFrom, uint SearchEnds, Fixed Radius,
    bool InSight);

// Overseer Track (docs/design.md, "Overseer Track"; the owner's request of 2026-11-03): a Track order (CommandKind.Track) locks a
// drone with a track block (the Overseer) onto an enemy soldier or vehicle its side spots, as the drone's mission (AirTaskKind.Track
// on Unit.AirMission, hashed with it):
//  - Approach: out of its own sight still, its orbit centre is the target's position while its side spots it; in its sight: Locked.
//    Its side losing the target first: Searching.
//  - Locked: its orbit centre (Unit.OrbitAt) glides after the target at the block's followSpeed, never past it, so the target stays
//    inside its sight circle (the orbit radius is its own, the flight its own: turn rate, height planner, fly zone). While the
//    target is within its sight (a level distance from the point under it, as an air viewer sees: the whole disc), it is spotted
//    for the drone's side every tick whatever would hide it from the air (canopy, a cold engine, standing still; smoke neither).
//    It is lost when it leaves that sight or goes inside a building, tunnel or vehicle; a dead target ends the track.
//  - Searching: it orbits the last known position, its radius searchPercent of its orbit radius at first and searchStepPercent
//    wider every searchStepTicks up to searchMaxPercent; the clock starts once the last known position is within its sight. Its
//    own look (the vision system's rule for it alone: concealment from the air, spotting, the grid) finding the same unit again
//    locks it again; after searchTicks it gives up and holds a normal orbit there.
// A trip home (bingo, Rearm, Return to base) ends the track; a Move, a Stop or an air task replaces it as any order ends a mission.
// RunTracks runs inside the vision system after every other sensor (so the lock's spot is in this tick's SpottedBy and SeenBy),
// visiting units by ID; it only ORs its side's bit, so the order among drones does not matter.
public sealed partial class World
{
    // Whether any Track order was ever taken: until then the vision system skips the tracks. Not hashed (the missions are).
    private bool _tracking;

    private readonly List<TrackEvent> _trackEvents = [];

    /// <summary>What Track orders did in the last tick, in drone ID order (locks, losses, re-locks, the ends of a track).</summary>
    public IReadOnlyList<TrackEvent> TrackEvents => _trackEvents;

    /// <summary>Whether unit u can take a Track order at all (its archetype has a <c>track</c> block: the Overseer).</summary>
    public static bool CanTrack(Unit u) => u.Def.Archetype.Track is not null && u.Def.Archetype.Flight is not null;

    /// <summary>
    /// Why drone u would refuse a Track order at target t now (null: it would take it): <see cref="OrderRefusalReason.NotTrackable"/>
    /// for anything but an enemy soldier or vehicle out on the map, <see cref="OrderRefusalReason.NotVisible"/> when its side does
    /// not spot it, <see cref="OrderRefusalReason.Homing"/> or <see cref="OrderRefusalReason.RefuelTrip"/> while it flies home to
    /// land. For the client's cursor; the command applies the same rule. A unit that cannot track at all: NotTrackable.
    /// </summary>
    public OrderRefusalReason? TrackRefusalOf(Unit u, Unit? t)
    {
        if (!CanTrack(u) || t is null || !t.Alive || t.Owner == 0 || t.Owner == u.Owner || !Trackable(t))
        {
            return OrderRefusalReason.NotTrackable;
        }
        if (!IsSpotted(t, u.Owner))
        {
            return OrderRefusalReason.NotVisible;
        }
        return AirOrderRefusal(u);
    }

    /// <summary>
    /// Drone u's Track (<see cref="CommandKind.Track"/>) as it stands now, or null when it is not tracking: its target, its phase,
    /// the last known position, the search's clock and the radius it flies.
    /// </summary>
    public TrackStatus? TrackOf(Unit u)
    {
        if (u.AirMission is not { Kind: AirTaskKind.Track } m || u.Def.Archetype.Flight is not { } f)
        {
            return null;
        }
        var spec = u.Def.Archetype.Track;
        var r = TrackRadius(u, f, m);
        var ends = m.Phase == TrackPhase.Searching && m.SearchFrom != 0 && spec is not null ? m.SearchFrom + (uint)spec.SearchTicks : 0;
        var t = Unit(m.Target);
        var inSight = t is { Alive: true } && Trackable(t) && InTrackSight(u, t);
        return new TrackStatus(m.Target, m.Phase, m.LastSeen, m.LostAt, m.SearchFrom, ends, Fixed.FromRaw(r), inSight);
    }

    // TrackSearchPercent is how much of its search time drone u has used (0 to 100; 0 until the search began to widen).
    private int TrackSearchPercent(Unit u, AirMission m) =>
        m.SearchFrom == 0 || u.Def.Archetype.Track is not { } spec ? 0 : (int)Math.Min(100, (Tick - m.SearchFrom) * 100L / Math.Max(1, spec.SearchTicks));

    // Trackable: t is a soldier or vehicle out on the map (not a structure, a trap, an aircraft; not inside anything).
    private static bool Trackable(Unit t) => Concealable(t) && !t.Def.Hidden;

    // InTrackSight: t stands within drone u's sight, a level distance from the point under it (an air viewer sees its whole disc).
    private bool InTrackSight(Unit u, Unit t) => Within(u.Pos, t.Pos, SightRange(u));

    // TrackRadius is the orbit radius (raw) drone u flies on its Track now: its orbit radius, the search's percent of it while searching.
    private long TrackRadius(Unit u, ScoutFlight f, AirMission m)
    {
        var r = f.OrbitRadius.Raw;
        if (m.Phase != TrackPhase.Searching || u.Def.Archetype.Track is not { } spec)
        {
            return r;
        }
        return r * spec.SearchPercentAt(m.SearchFrom == 0 ? 0 : Tick - m.SearchFrom) / 100;
    }

    // A target moving faster than this share of the drone's speed (permille) is followed by S-turns, slower ones by the orbit.
    private const long WeavePermille = 500;

    // The most of the drone's speed a target's velocity counts as (permille): the moving frame needs some speed of its own.
    private const long MovingFramePermille = 900;

    // TrackAim is where a tracking drone steers. Searching or on its way: the plain orbit round its orbit centre at its radius.
    // Locked, it flies in the target's frame (a way relative to the target, and the ground velocity at its own speed that makes it
    // so: the target's velocity plus the most of that way its speed leaves), at its full rate whenever that velocity is more than
    // 30 degrees off its own (the lazy drone law's wide swing took it 20 cells past its circle, measured), the short way:
    //  - a still target, or one at most half its speed (soldiers, trucks): it circles it as it goes, the orbit's aim (30 degrees
    //    ahead) the way, the circle tightened by the target's speed (radius x (1 - its speed / the drone's), at least half);
    //  - a faster one (a technical, a dirt bike): a circle's back leg would fly it straight against the target and the U-turn
    //    after it cost about 5 s x the target's speed (a technical: 18 cells, out of its 50-cell sight, measured), so it snakes
    //    over the target in full-rate S-turns (Snake); out of the snake's reach it makes for a point a third of its orbit radius
    //    behind the target (never against it: across its track instead), and flying against the target (it turned) it comes
    //    round to the side its look-ahead finds keeps the target nearer (TurnGap), committed (AirMission.Turn) until round.
    private Vec TrackAim(Unit u, ScoutFlight f, AirMission m)
    {
        var r = TrackRadius(u, f, m);
        var s = u.Def.Speed.Raw;
        if (m.Phase != TrackPhase.Locked || Unit(m.Target) is not { } t || s <= 0)
        {
            m.Turn = 0;
            return OrbitPoint(u, Inner(f, u.OrbitAt, r), r);
        }
        var tv = t.Vel;
        long vx = tv.X.Raw, vy = tv.Y.Raw;
        var vl = Len3(vx, vy, 0);
        var weave = vl * 1000 > s * WeavePermille;
        long px = -vy, py = vx; // across the target's track (a unit vector once divided by vl)
        if (!weave)
        {
            m.Turn = 0;
        }
        if (!weave && vl > 0)
        {
            r = r * Math.Max(1000 - WeavePermille, 1000 - vl * 1000 / s) / 1000; // a tighter circle round a target on the move
        }
        var p = OrbitPoint(u, Inner(f, u.OrbitAt, r), r);
        if (weave)
        {
            long ex = u.Pos.X.Raw - t.Pos.X.Raw, ey = u.Pos.Y.Raw - t.Pos.Y.Raw;
            var reachSnake = r * 2 / 3;
            if (ex * ex + ey * ey <= reachSnake * reachSnake && Snake(u, m, s, vx, vy, vl, px, py, ex, ey) is { } snake)
            {
                return snake; // full-rate S-turns over the target
            }
            if (u.Vel.X.Raw * vx + u.Vel.Y.Raw * vy < 0)
            {
                // Flying against the target (it turned): round at its full rate, to the side whose look-ahead keeps the target
                // nearer (TurnGap), committed (AirMission.Turn +-2) until it flies the target's way again.
                if (Math.Abs((int)m.Turn) != 2)
                {
                    m.Turn = (sbyte)(TurnGap(u, t, 1, f) <= TurnGap(u, t, -1, f) ? 2 : -2);
                }
                return HardTurnAim(u, f, Math.Sign(m.Turn));
            }
            // Out of the snake's reach: for the point a third of the orbit radius behind the target (it trails it).
            var w = r / 3;
            p = new Vec(Fixed.FromRaw(t.Pos.X.Raw - w * vx / vl), Fixed.FromRaw(t.Pos.Y.Raw - w * vy / vl));
        }
        long dx = p.X.Raw - u.Pos.X.Raw, dy = p.Y.Raw - u.Pos.Y.Raw;
        var dl = Len3(dx, dy, 0);
        if (dl == 0)
        {
            return p;
        }
        var cap = s * MovingFramePermille / 1000;
        if (vl > cap)
        {
            (vx, vy) = (vx * cap / vl, vy * cap / vl);
        }
        // |v + k d^| = s: k = -(v.d^) + sqrt((v.d^)^2 - |v|^2 + s^2), positive since |v| < s.
        var dot = (vx * dx + vy * dy) / dl;
        var k = -dot + Fixed.Sqrt(Math.Max(0, dot * dot - (vx * vx + vy * vy) + s * s));
        long gx = vx + k * dx / dl, gy = vy + k * dy / dl;
        if (weave && gx * vx + gy * vy < 0)
        {
            // Never against the target while it weaves: straight across its track instead (it drifts back along it as fast as the
            // target drives, with no U-turn), on the side that way leans to (else the side it flies to now).
            var gp = gx * px + gy * py;
            var lean = gp != 0 ? gp : u.Vel.X.Raw * px + u.Vel.Y.Raw * py;
            var sg = lean >= 0 ? 1 : -1;
            (gx, gy) = (sg * px * s / vl, sg * py * s / vl);
        }
        var gl = Math.Max(1, Len3(gx, gy, 0));
        var reach = 3 * ScoutFlight.TurnRadiusRaw(s, f.TurnPermille);
        long ux = u.Vel.X.Raw, uy = u.Vel.Y.Raw;
        var ul = Len3(ux, uy, 0);
        if (ul > 0 && (ux * gx + uy * gy) * 1000 < ul * gl * HardTurnCosPermille)
        {
            return HardTurnAim(u, f, ux * gy - uy * gx >= 0 ? 1 : -1); // more than 30 degrees off: its full rate, the short way
        }
        return new Vec(Fixed.FromRaw(u.Pos.X.Raw + gx * reach / gl), Fixed.FromRaw(u.Pos.Y.Raw + gy * reach / gl));
    }

    // Locked, a wanted velocity more than this far off its own (the cosine, permille: 30 degrees) turns it at its full rate.
    private const long HardTurnCosPermille = 866;

    // How far ahead TurnGap looks (ticks: about a U-turn and a half).
    private const int TurnLookTicks = 8 * TicksPerSecond;

    // TurnGap: the farthest (squared, raw) drone u gets from target t over the next TurnLookTicks if it turns to side at its full
    // rate now and then flies at the target (the target driving straight on as it does now): a pure function of the two units.
    private static long TurnGap(Unit u, Unit t, int side, ScoutFlight f)
    {
        var s = u.Def.Speed.Raw;
        long px = u.Pos.X.Raw, py = u.Pos.Y.Raw, vx = u.Vel.X.Raw, vy = u.Vel.Y.Raw;
        long tx = t.Pos.X.Raw, ty = t.Pos.Y.Raw, tvx = t.Vel.X.Raw, tvy = t.Vel.Y.Raw;
        var turning = true;
        long worst = 0;
        for (var i = 0; i < TurnLookTicks; i++)
        {
            long cx = tx - px, cy = ty - py;
            var vl = Math.Max(1, Len3(vx, vy, 0));
            var cl = Len3(cx, cy, 0);
            if (turning && (vx * cx + vy * cy) * 1000 >= vl * cl * HardTurnCosPermille)
            {
                turning = false; // round: from here it flies at the target
            }
            if (turning || (vx * cx + vy * cy) * 1000 < vl * cl * 996)
            {
                (vx, vy) = HardTurn(vx, vy, turning ? side : vx * cy - vy * cx >= 0 ? 1 : -1, s, f.TurnPermille);
            }
            (px, py, tx, ty) = (px + vx, py + vy, tx + tvx, ty + tvy);
            worst = Math.Max(worst, (tx - px) * (tx - px) + (ty - py) * (ty - py));
        }
        return worst;
    }

    // The snake's half-swing: the cosine (permille) of the heading off the target's at which it turns back, by the target's
    // speed as a share of its own (permille, 300 to 1,000 by 100): continuous full-rate S-turns of half-swing phi make good
    // sin(phi) / phi of its speed along the track, so this table matches the target's speed; feedback adds to it.
    private static readonly int[] SnakeCos = [-706, -526, -319, -89, 160, 418, 704, 1000];

    // Snake: drone u (speed s) over a fast target (velocity (vx, vy), length vl; (px, py) across its track; the drone (ex, ey)
    // from it) turns at its full rate to the side it committed to (AirMission.Turn), and back once its heading is the half-swing
    // off the target's heading on that side: wider while it is ahead of the target, narrower behind, sooner on the side it already
    // is. Null when it is flying against the target (it comes round by the moving frame first).
    private Vec? Snake(Unit u, AirMission m, long s, long vx, long vy, long vl, long px, long py, long ex, long ey)
    {
        long ux = u.Vel.X.Raw, uy = u.Vel.Y.Raw;
        var ul = Len3(ux, uy, 0);
        if (ul == 0 || ux * vx + uy * vy < -ul * vl / 2)
        {
            m.Turn = 0;
            return null;
        }
        var along = (ex * vx + ey * vy) / vl; // ahead of the target (+) or behind (raw)
        var lat = (ex * px + ey * py) / vl;   // to its right (+) or left (raw)
        var ratio = Math.Clamp(vl * 1000 / s, 300, 1000);
        var k = (int)Math.Min(SnakeCos.Length - 2, (ratio - 300) / 100);
        var cosPhi = SnakeCos[k] + (SnakeCos[k + 1] - SnakeCos[k]) * (ratio - 300 - k * 100) / 100;
        if (m.Turn == 0)
        {
            m.Turn = (sbyte)(ux * px + uy * py >= 0 ? 1 : -1);
        }
        else if (Math.Abs((int)m.Turn) == 2)
        {
            m.Turn = (sbyte)Math.Sign(m.Turn); // out of a U-turn: the S-turns go on the way it was turning
        }
        cosPhi = Math.Clamp(cosPhi - along * SnakeAheadPermille / Fixed.OneRaw + m.Turn * lat * SnakeSidePermille / Fixed.OneRaw, -450, 950);
        var cosT = (ux * vx + uy * vy) * 1000 / (ul * vl);
        var outward = (ux * px + uy * py) * m.Turn > 0;
        if (outward && cosT < cosPhi)
        {
            m.Turn = (sbyte)-m.Turn;
        }
        return HardTurnAim(u, u.Def.Archetype.Flight!, m.Turn); // +1 turns its velocity toward (-vy, vx): toward +p along the target's way
    }

    // HardTurnAim is an aim that turns drone u at its full rate to side (+1 toward (-vy, vx)): behind its beam on that side,
    // beyond its turning circles (the flight controller's turning rule).
    private static Vec HardTurnAim(Unit u, ScoutFlight f, int side)
    {
        long ux = u.Vel.X.Raw, uy = u.Vel.Y.Raw;
        var reach = 3 * ScoutFlight.TurnRadiusRaw(u.Def.Speed.Raw, f.TurnPermille);
        long wx = -side * uy - ux / 10, wy = side * ux - uy / 10;
        var wl = Math.Max(1, Len3(wx, wy, 0));
        return new Vec(Fixed.FromRaw(u.Pos.X.Raw + wx * reach / wl), Fixed.FromRaw(u.Pos.Y.Raw + wy * reach / wl));
    }

    // The snake's feedback, permille of the cosine a cell: ahead of the target it swings wider, on its side it turns back sooner.
    private const long SnakeAheadPermille = 30, SnakeSidePermille = 20;

    // TrackCommand gives the listed drones of the player a Track on c.Target (CommandKind.Track): refused (World.OrderRefusals) unless
    // the target is an enemy soldier or vehicle out on the map its side spots now and the drone is not flying home to land. Within
    // its sight it locks at once; else it flies to it. At its airfield it leaves (as a Move would) and tracks once it flies free.
    private void TrackCommand(Command c)
    {
        var t = Unit(c.Target);
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || !CanTrack(u!) || !u!.Alive)
            {
                continue;
            }
            var cell = t is not null ? Cell.Of(t.Pos) : c.Cell;
            if (TrackRefusalOf(u, t) is { } why)
            {
                Refuse(u, why, c.Target, cell);
                continue;
            }
            var m = new AirMission { Kind = AirTaskKind.Track, Target = t!.Id, LastSeen = t.Pos, Phase = TrackPhase.Approach };
            u.AirMission = m;
            _tracking = true;
            if (u.AirOps is { } ops)
            {
                m.Away = true; // at its airfield: it takes the track once it flies free
                if (ops.Phase is >= FlightPhase.Rollout and <= FlightPhase.Parked)
                {
                    (ops.Launch, ops.Hold) = (true, false); // as a Move order: it leaves once parked
                }
                continue;
            }
            u.Idle(); // a new mission drops an order in progress
            if (InTrackSight(u, t))
            {
                m.Phase = TrackPhase.Locked; // its orbit centre glides over from where it was
                _trackEvents.Add(new TrackEvent(u.Id, u.Owner, t.Id, TrackEventKind.Locked, TrackLoss.None, cell));
            }
            else
            {
                u.OrbitAt = t.Pos; // it flies to the target
            }
        }
    }

    // RunTracks (the vision system, after every other sensor): each tracking drone in ID order follows, locks, loses, searches or ends
    // its Track, and a locked target within its sight is spotted for its side this tick.
    private void RunTracks()
    {
        if (!_tracking)
        {
            return;
        }
        foreach (var u in _units)
        {
            if (u.AirMission is not { Kind: AirTaskKind.Track } m || !u.Alive)
            {
                continue;
            }
            if (u.Def.Archetype.Track is not { } spec || AirOrderRefusal(u) is not null)
            {
                EndTrack(u, m, TrackEventKind.Home); // flying home to land: the track is over (it comes back to orbit here)
                continue;
            }
            if (u.AirOps is not null || u.Order.Kind == OrderKind.Move)
            {
                continue; // leaving its airfield (it tracks once free), or a Move this tick ends the mission in the air movement
            }
            var t = Unit(m.Target);
            if (t is null || !t.Alive || t.Owner == 0 || t.Owner == u.Owner)
            {
                EndTrack(u, m, TrackEventKind.TargetGone); // dead (or no longer an enemy): it orbits where it was
                continue;
            }
            var bit = Bit(u.Owner);
            switch (m.Phase)
            {
                case TrackPhase.Approach:
                    if (!Trackable(t) || (t.SpottedBy & bit) == 0)
                    {
                        Lose(u, m, t.Inside != 0 || t.Garrison != 0 ? TrackLoss.Inside : TrackLoss.Unspotted);
                        break;
                    }
                    m.LastSeen = t.Pos;
                    u.OrbitAt = t.Pos;
                    if (InTrackSight(u, t))
                    {
                        m.Phase = TrackPhase.Locked;
                        t.SpottedBy |= bit;
                        _trackEvents.Add(new TrackEvent(u.Id, u.Owner, t.Id, TrackEventKind.Locked, TrackLoss.None, Cell.Of(t.Pos)));
                    }
                    break;
                case TrackPhase.Locked:
                    if (!Trackable(t) || !InTrackSight(u, t))
                    {
                        Lose(u, m, Trackable(t) ? TrackLoss.OutOfSight : TrackLoss.Inside);
                        break;
                    }
                    m.LastSeen = t.Pos;
                    Glide(u, spec, t.Pos);
                    t.SpottedBy |= bit; // the lock: spotted for its side whatever hides it from the air
                    break;
                default:
                    if (m.SearchFrom == 0 && Within(u.Pos, m.LastSeen, SightRange(u)))
                    {
                        m.SearchFrom = Tick; // over the last known position: the search widens from now
                    }
                    if (Trackable(t) && OwnLook(u, t))
                    {
                        (m.Phase, m.LostAt, m.SearchFrom, m.LastSeen) = (TrackPhase.Locked, 0, 0, t.Pos);
                        Glide(u, spec, t.Pos);
                        t.SpottedBy |= bit;
                        _trackEvents.Add(new TrackEvent(u.Id, u.Owner, t.Id, TrackEventKind.Relocked, TrackLoss.None, Cell.Of(t.Pos)));
                    }
                    else if (m.SearchFrom != 0 && Tick - m.SearchFrom >= (uint)spec.SearchTicks)
                    {
                        EndTrack(u, m, TrackEventKind.GaveUp); // a normal orbit over the last known position
                    }
                    break;
            }
        }
    }

    // Lose: drone u lost its target: it searches round the last known position (its orbit centre now).
    private void Lose(Unit u, AirMission m, TrackLoss why)
    {
        (m.Phase, m.LostAt) = (TrackPhase.Searching, Tick);
        m.SearchFrom = Within(u.Pos, m.LastSeen, SightRange(u)) ? Tick : 0;
        u.OrbitAt = m.LastSeen;
        _trackEvents.Add(new TrackEvent(u.Id, u.Owner, m.Target, TrackEventKind.Lost, why, Cell.Of(m.LastSeen)));
    }

    // EndTrack: the track is over; drone u keeps orbiting its orbit centre (the target's last position, or the last known one).
    private void EndTrack(Unit u, AirMission m, TrackEventKind kind)
    {
        u.AirMission = null;
        _trackEvents.Add(new TrackEvent(u.Id, u.Owner, m.Target, kind, TrackLoss.None, Cell.Of(u.OrbitAt)));
    }

    // Glide moves drone u's orbit centre toward p by at most the track's follow speed a tick, never past it.
    private static void Glide(Unit u, TrackSpec spec, Vec p)
    {
        long dx = p.X.Raw - u.OrbitAt.X.Raw, dy = p.Y.Raw - u.OrbitAt.Y.Raw;
        var d = Len3(dx, dy, 0);
        u.OrbitAt = d <= spec.FollowRaw ? p
            : new Vec(Fixed.FromRaw(u.OrbitAt.X.Raw + dx * spec.FollowRaw / d), Fixed.FromRaw(u.OrbitAt.Y.Raw + dy * spec.FollowRaw / d));
    }

    // OwnLook: drone o's own eye spots ground unit t now, by the vision system's rule for that pair (not staggered): without fog
    // within its sight; with fog, under canopy its sensors (concealment from the air), else with spotting the spotting rule on its
    // own sight mask, else its own disc on the grid.
    private bool OwnLook(Unit o, Unit t)
    {
        if (t.Inside != 0 || t.Garrison != 0)
        {
            return false;
        }
        var sight = SightRange(o);
        if (_fog is null || InAir(t))
        {
            return Within(From(o, t.Pos), AimPoint(o.Pos, t), sight);
        }
        var cover = Concealing;
        var canopy = cover is not null && o.Garrison == 0 && InAir(o);
        if (canopy && CanopyCell(t) is var ci && ci >= 0)
        {
            return CanopySees(cover!, t, ci, SensorsOf(o), CameraBonus(cover!, o), (t.Pos - o.Pos).LenSq(), sight.Raw);
        }
        if (ViewerOf(o.Id) is not { } view)
        {
            return false;
        }
        if (_spotting is { } sr && Concealable(t))
        {
            return SpotSees(sr, o, view, t, sight.Raw, OpticsOf(o));
        }
        var c = Cell.Of(t.Pos);
        return MaskHas(view, Math.Clamp(c.X, 0, Grid.Width - 1), Math.Clamp(c.Y, 0, Grid.Height - 1));
    }
}
