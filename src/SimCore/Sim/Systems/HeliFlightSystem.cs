using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The helicopters' flight (the helicopters night, 2026-10-21; docs/design.md, "Helicopters"). A helicopter (a flight of kind
// "rotary") flies three layers like every aircraft, but its kinematics are its own:
//  - the order (what it does): Move, Attack, Attack-ground, Attack-move, Guard, Patrol (an AirTask), Return to base, Rearm,
//    Load and Unload; the attack picks a firing position (the pop-up behind cover, a standoff hover, a gun run);
//  - the controller (pure arithmetic): a velocity toward its goal that it can still stop from (an arrival law), a nose
//    direction (on its target while it attacks, along its path in transit, held when it hovers) and a height (terrain
//    following low over the ground: the height planner in transit, the exact ground under it in a hover);
//  - the rotary kinematics: the nose yaws at most its yaw rate a tick (it turns on the spot); the wanted velocity is
//    clamped in its body frame (forward up to its top speed, sideways and backwards up to its side speed) and the velocity
//    changes by at most its acceleration a tick; it climbs at its climb rate and sinks at its descent rate.
// It needs no runway: it lands straight down on a helipad, an airfield station or open ground and lifts off straight up
// after its rotor spools up. All integer: speeds raw a second with the tick's remainder kept, the heading a vector 16384 long.
public sealed partial class World
{
    // Whether any helicopter was ever added: while none was, every helicopter pass costs nothing (not hashed: derived).
    private bool _helis;

    // In transit the height planner looks this far ahead along its path (5 s, as the jets).
    private const int HeliLookAhead = 5 * TicksPerSecond;

    // It keeps this far over a roof on its path (raw: 5 m) and never lower than its floor over the ground (raw: 0.5 m).
    private const long HeliRoofClearRaw = 5 * 512, HeliFloorRaw = 256;

    // Arrived: within this of its goal (raw: a cell) and slower than this (raw a second: a cell a second).
    private const long HeliArriveRaw = Fixed.OneRaw, HeliStillRaw = Fixed.OneRaw;

    // A gun run's sidestep: its goal swung this far round the target from where it is (cos, sin of 20 degrees in 1/16384).
    private const int RunCos = 15396, RunSin = 5604;

    private readonly List<HeliCrash> _heliCrashes = [];

    /// <summary>The helicopter flights that ended this tick (destroyed, crash-landed, landed hard), in ID order; read after Step.</summary>
    public IReadOnlyList<HeliCrash> HeliCrashes => _heliCrashes;

    // FlyHeli moves helicopter u one tick (the air movement system).
    private void FlyHeli(Unit u, ScoutFlight f, RotarySpec r)
    {
        var h = u.Heli!;
        if (h.Phase == HeliPhase.Flying && u.Altitude.Raw == 0 && h.VelX == 0 && h.VelY == 0)
        {
            // Put down in flight (a test, the sandbox): at its height already, hovering where it is.
            u.Altitude = Fixed.FromRaw(GroundRaw(u.Pos) + f.HeightRaw);
            h.Post = u.Pos;
        }
        if (h.Post == default)
        {
            h.Post = u.Pos;
        }
        CheckPad(u, h);
        switch (h.Phase)
        {
            case HeliPhase.OutOfControl:
                FlyOutOfControl(u, h, f, r);
                return;
            case HeliPhase.Landed:
                OnGround(u, h, f, r);
                return;
            case HeliPhase.Landing:
                Descend(u, h, f, r);
                return;
            case HeliPhase.TakingOff:
                LiftOff(u, h, f, r);
                return;
        }
        h.Rotor = 1000;
        if (!u.Homing && u.Lifetime > 0 && u.Lifetime <= HeliFarthestHome(u) + f.ReserveTicks && u.Lifetime <= HeliHomeTicks(u, f) + f.ReserveTicks)
        {
            HeliGoHome(u, HomeReason.Refuel); // bingo: the only automatic trip (the owner's rules), to refuel; it resumes after
        }
        if (u.Homing)
        {
            if (Unit(h.Pad) is { } pad)
            {
                var at = PadPoint(pad);
                // Home at its return tempo (2026-10-24): its flight there time-scaled (its cruise, its body speed limits and its
                // climb and descent times it, its acceleration times its square: the same way, sooner).
                if (FlyTo(u, h, f, r, at, Cruise(u) * r.TempoReturn / 100, null, f.HeightRaw, -1, true, r.TempoReturn))
                {
                    SetPhase(h, HeliPhase.Landing);
                }
                return;
            }
            u.Homing = false; // nowhere to land: it hovers on until it runs dry
        }
        if (h.Errand != HeliErrand.None && Errand(u, h, f, r))
        {
            return;
        }
        FlyOrder(u, h, f, r);
    }

    // FlyOrder flies helicopter u's order (or its mission, or its hover) for a tick.
    private void FlyOrder(Unit u, HeliState h, ScoutFlight f, RotarySpec r)
    {
        switch (u.Order.Kind)
        {
            case OrderKind.Move:
                var dest = u.Order.Cell.Center();
                if (FlyTo(u, h, f, r, dest, r.MaxSpeed, null, f.HeightRaw, -1, true))
                {
                    u.Idle();
                    h.Post = dest; // it hovers there
                }
                return;
            case OrderKind.Attack:
                if (Unit(u.Order.Target) is { Alive: true } t && IsVisibleTo(t, u.Owner) && HeliCanHit(u, t))
                {
                    Engage(u, h, f, r, t, t.Pos);
                    return;
                }
                EndAttack(u, h);
                return;
            case OrderKind.AttackGround:
                if (HeliGroundWeapon(u) >= 0)
                {
                    Engage(u, h, f, r, null, u.Order.Cell.Center());
                    return;
                }
                EndAttack(u, h);
                return;
            case OrderKind.AttackMove:
                if (Unit(h.Target) is { Alive: true } am)
                {
                    Engage(u, h, f, r, am, am.Pos);
                    return;
                }
                StopAttack(h);
                var to = u.Order.Cell.Center();
                if (FlyTo(u, h, f, r, to, Cruise(u), null, f.HeightRaw, -1, true))
                {
                    u.Idle();
                    h.Post = to;
                }
                return;
            case OrderKind.Guard:
                if (Unit(u.Order.Target) is { Alive: true } ward && ward.Owner == u.Owner)
                {
                    u.Order = u.Order with { Cell = Cell.Of(ward.Pos) }; // the post moves with what it guards
                }
                h.Post = u.Order.Cell.Center();
                if (Unit(h.Target) is { Alive: true } gt)
                {
                    Engage(u, h, f, r, gt, gt.Pos);
                    return;
                }
                StopAttack(h);
                Hover(u, h, f, r, h.Post);
                return;
        }
        if (u.AirMission is { } m && Waypoint(u, m) is { } wp)
        {
            if (Unit(h.Target) is { Alive: true } mt)
            {
                Engage(u, h, f, r, mt, mt.Pos);
                return;
            }
            StopAttack(h);
            if (FlyTo(u, h, f, r, wp, Cruise(u), null, f.HeightRaw, -1, true) || Within(u.Pos, wp, Fixed.FromInt(3)))
            {
                m.Leg++; // on to the next waypoint (the route and the area's corners are loops)
            }
            h.Post = u.Pos;
            return;
        }
        if (Unit(h.Target) is { Alive: true } it)
        {
            // Idle in a firing stance: it fights what comes within its weapons' reach from where it hovers (nose on it).
            StopAttack(h);
            FlyTo(u, h, f, r, h.Post, Cruise(u), it.Pos, r.AttackHeightRaw, -1, true);
            return;
        }
        StopAttack(h);
        Hover(u, h, f, r, h.Post);
    }

    // Hover holds helicopter u over p at its flight height (it flies back there if it drifted off).
    private void Hover(Unit u, HeliState h, ScoutFlight f, RotarySpec r, Vec p) => FlyTo(u, h, f, r, p, Cruise(u), null, f.HeightRaw, -1, true);

    // EndAttack: an attack order is over (its target dead, gone or out of reach): it hovers where it is.
    private static void EndAttack(Unit u, HeliState h)
    {
        u.Idle();
        StopAttack(h);
        h.Post = u.Pos;
    }

    private static void StopAttack(HeliState h)
    {
        if (h.Attack != HeliAttack.None)
        {
            (h.Attack, h.AttackTicks) = (HeliAttack.None, 0);
        }
    }

    // Cruise is u's cruise speed (raw a second).
    private static long Cruise(Unit u) => u.Def.Speed.Raw * TicksPerSecond;

    // Waypoint is where helicopter u flies on its mission now: the route's leg (a loop), or an area's corners in turn.
    private static Vec? Waypoint(Unit u, AirMission m)
    {
        if (m.HasArea)
        {
            var corners = (ReadOnlySpan<Cell>)[m.AreaMin, new Cell(m.AreaMax.X, m.AreaMin.Y), m.AreaMax, new Cell(m.AreaMin.X, m.AreaMax.Y)];
            return corners[(int)((uint)m.Leg % 4)].Center();
        }
        return m.Route.Count == 0 ? null : m.Route[(int)((uint)m.Leg % (uint)m.Route.Count)].Center();
    }

    // ---- The controller and the rotary kinematics ----

    // FlyTo flies helicopter u one tick toward goal at most speedCap (raw a second), its nose on face (or along its path,
    // or held), at band over the ground (in transit: the height planner; slow or hovering: the ground under it) or at
    // the absolute height absAlt when that is 0 or more; body: its sideways and backward limits apply (off: out of
    // control). It reports whether it has arrived (within a cell, nearly still).
    private bool FlyTo(Unit u, HeliState h, ScoutFlight f, RotarySpec r, Vec goal, long speedCap, Vec? face, long band, long absAlt, bool body, int pace = 100)
    {
        long dx = goal.X.Raw - u.Pos.X.Raw, dy = goal.Y.Raw - u.Pos.Y.Raw;
        var dist = Len3(dx, dy, 0);
        long wx = 0, wy = 0;
        if (dist > HeliArriveRaw / 8)
        {
            // The arrival law: no faster than it can still stop from in time (at 80% of its acceleration, a margin).
            var sp = Math.Min(speedCap, Fixed.Sqrt(2 * (r.AccelPerTick * pace * pace / 10000) * TicksPerSecond * dist * 8 / 10));
            (wx, wy) = (dx * sp / dist, dy * sp / dist);
        }
        long fx = 0, fy = 0;
        if (face is { } fp)
        {
            (fx, fy) = (fp.X.Raw - u.Pos.X.Raw, fp.Y.Raw - u.Pos.Y.Raw);
        }
        else if (Len3(wx, wy, 0) > r.SideSpeed)
        {
            (fx, fy) = (wx, wy); // in transit its nose leads
        }
        Fly(u, h, f, r, wx, wy, fx, fy, band, absAlt, body, pace);
        return dist <= HeliArriveRaw && Len3(h.VelX, h.VelY, 0) <= HeliStillRaw;
    }

    // Fly is one tick of the rotary kinematics: the nose yaws toward (fx, fy) (none: held), the wanted velocity (wx, wy, raw a
    // second) is clamped in the body frame (body) and reached at most an acceleration step a tick, it moves, and its height
    // tracks band over the ground (or absAlt) within its climb and descent rates. At a pace (percent; 2026-10-24: its flight home,
    // its descent onto its pad and its lift-off at its tempo) its speed limits and its climb and descent are times it, its
    // acceleration times its square.
    private void Fly(Unit u, HeliState h, ScoutFlight f, RotarySpec r, long wx, long wy, long fx, long fy, long band, long absAlt, bool body, int pace = 100)
    {
        YawToward(h, r.YawStep, fx, fy);
        if (body)
        {
            long hx = h.HeadX, hy = h.HeadY;
            long side = r.SideSpeed * pace / 100, top = r.MaxSpeed * pace / 100; // (at a tempo: 2026-10-24)
            var fwd = Math.Clamp((wx * hx + wy * hy) / 16384, -side, top);
            var lat = Math.Clamp((-wx * hy + wy * hx) / 16384, -side, side);
            (wx, wy) = ((fwd * hx - lat * hy) / 16384, (fwd * hy + lat * hx) / 16384);
        }
        long ex = wx - h.VelX, ey = wy - h.VelY;
        var el = Len3(ex, ey, 0);
        var acc = Math.Max(1, r.AccelPerTick * pace * pace / 10000); // (at a tempo: times its square, the same way sooner)
        if (el > acc)
        {
            (ex, ey) = (ex * acc / el, ey * acc / el);
        }
        h.VelX += ex;
        h.VelY += ey;
        Move(u, h, f, band, absAlt, pace);
    }

    // Move integrates helicopter u's velocity over a tick (kept on the map: a helicopter stops at the edge) and its height
    // (its climb and descent rates times pace, percent).
    private void Move(Unit u, HeliState h, ScoutFlight f, long band, long absAlt, int pace = 100)
    {
        long tx = h.VelX + h.RemX, ty = h.VelY + h.RemY;
        long sx = tx / TicksPerSecond, sy = ty / TicksPerSecond;
        (h.RemX, h.RemY) = (tx - sx * TicksPerSecond, ty - sy * TicksPerSecond);
        var (ox, oy) = (u.Vel.X.Raw, u.Vel.Y.Raw);
        long nx = u.Pos.X.Raw + sx, ny = u.Pos.Y.Raw + sy;
        var pos = OnMap(nx, ny);
        if (pos.X.Raw != nx)
        {
            (h.VelX, h.RemX, sx) = (0, 0, pos.X.Raw - u.Pos.X.Raw);
        }
        if (pos.Y.Raw != ny)
        {
            (h.VelY, h.RemY, sy) = (0, 0, pos.Y.Raw - u.Pos.Y.Raw);
        }
        u.Vel = P(sx, sy);
        u.Pos = pos;
        if (h.HeadX != 0 || h.HeadY != 0)
        {
            Facing.TryFromDirection(P(h.HeadX, h.HeadY), out var facing);
            u.Facing = facing;
        }
        var r = f.Rotor!;
        var ground = GroundRaw(pos);
        long want;
        var floor = ground + HeliFloorRaw;
        if (absAlt >= 0)
        {
            want = Math.Max(absAlt, ground);
            floor = Math.Min(floor, want); // coming down to land: the ground itself
        }
        else if (Len3(sx, sy, 0) * TicksPerSecond > r.SideSpeed)
        {
            want = PlanHeight(pos, ox, oy, sx, sy, f.ClimbPerTick, HeliLookAhead, band, HeliRoofClearRaw, band, HeliRoofClearRaw).Want;
        }
        else
        {
            want = ground + band; // hovering or creeping: the exact ground under it (behind a crest it stays low)
        }
        VerticalStep(u, want, f.ClimbPerTick * pace / 100, r.DescentPerTick * pace / 100, Math.Max(1, f.ClimbPerTick / 10), floor, true);
    }

    // YawToward turns heading h toward (fx, fy) by at most one yaw step (cos, sin in 1/16384); (0, 0): no turn.
    private static void YawToward(HeliState h, (int Cos, int Sin) step, long fx, long fy)
    {
        h.Yaw = 0;
        var fl = Len3(fx, fy, 0);
        if (fl == 0)
        {
            return;
        }
        long wx = fx * 16384 / fl, wy = fy * 16384 / fl;
        long hx = h.HeadX, hy = h.HeadY;
        var cross = (hx * wy - hy * wx) / 16384; // sine of the angle to turn, 1/16384 (positive: clockwise on the map)
        var dot = hx * wx + hy * wy;
        if (dot > 0 && Math.Abs(cross) <= step.Sin)
        {
            (h.HeadX, h.HeadY, h.Yaw) = ((int)wx, (int)wy, (int)cross);
            return;
        }
        var s = cross >= 0 ? step.Sin : -step.Sin;
        Turn(h, step.Cos, s);
    }

    // Turn rotates h's heading by the angle whose cos and sin (1/16384) are given (positive sin: clockwise), kept 16384 long.
    private static void Turn(HeliState h, int cos, int sin)
    {
        long hx = h.HeadX, hy = h.HeadY;
        long nx = (hx * cos - hy * sin) / 16384, ny = (hx * sin + hy * cos) / 16384;
        var l = Math.Max(1, Len3(nx, ny, 0));
        (h.HeadX, h.HeadY, h.Yaw) = ((int)(nx * 16384 / l), (int)(ny * 16384 / l), sin);
    }

    // RotationOf is the rotation (cos, sin in 1/16384) of degrees a tick given in degrees a second (the series, integer).
    internal static (int Cos, int Sin) RotationOf(int degreesPerSecond)
    {
        var th = (long)degreesPerSecond * 16384 * 314159 / (20L * 180 * 100000);
        var sin = th - th * th * th / (6L * 16384 * 16384);
        var cos = 16384 - th * th / (2L * 16384) + th * th / 16384 * th / 16384 * th / (24L * 16384);
        return ((int)cos, (int)sin);
    }

    // ---- The ground: lift-off, landing, its pad ----

    // SetPhase changes helicopter h's phase (and notes the tick).
    private void SetPhase(HeliState h, HeliPhase p)
    {
        h.Phase = p;
        h.Since = Tick;
    }

    // OnGround runs a landed helicopter: its pad's service, its errand (riders boarding or getting out), its rotor, and its
    // lift-off once it has somewhere to fly.
    private void OnGround(Unit u, HeliState h, ScoutFlight f, RotarySpec r)
    {
        (h.VelX, h.VelY, h.RemX, h.RemY, h.Yaw) = (0, 0, 0, 0, 0);
        u.Vel = default;
        u.VelZ = Fixed.Zero;
        u.Altitude = Fixed.FromRaw(GroundRaw(u.Pos));
        var serviced = ServiceOnPad(u, h, f);
        if (u.Homing && serviced)
        {
            // The trip home is over: a refuel stop or a Rearm visit resumes its order; a recall (Return to base) holds it.
            u.Homing = false;
            if (u.HomeReason == HomeReason.Recall)
            {
                h.Hold = true;
            }
            else if (u.HomeReason == HomeReason.Refuel && u.Order.Kind == OrderKind.Idle && u.AirMission is null && !Within(u.Pos, h.Post, Fixed.FromInt(3)))
            {
                u.Order = new Order(OrderKind.Move, Cell.Of(h.Post), 0, 0); // refuelled: back to where it hovered
            }
        }
        var fly = !u.Homing && !h.Hold && WantsAir(u, h);
        var away = h.Errand != HeliErrand.None && !Within(u.Pos, h.LandAt, Fixed.FromInt(2));
        if (away)
        {
            fly = !u.Homing; // its errand's point is elsewhere: it lifts off and flies there
        }
        else if (h.Errand == HeliErrand.Board)
        {
            fly = false;
            if (!Boarding(u))
            {
                h.Errand = HeliErrand.None; // everyone sent to it is aboard (or gave up): it waits here for its next order
            }
        }
        else if (h.Errand == HeliErrand.Drop)
        {
            fly = false;
            DropRiders(u, h, r, true);
        }
        var keep = fly || h.Errand != HeliErrand.None || Tick - h.Since < 2 * TicksPerSecond;
        var spool = Math.Max(1, 1000 / Math.Max(1, r.SpoolTicks));
        var up = fly ? Math.Max(1, 1000 * r.TempoLeave / (100 * Math.Max(1, r.SpoolTicks))) : spool; // (2026-10-24: spooling up to leave at its leave tempo)
        h.Rotor = keep ? Math.Min(1000, h.Rotor + up) : Math.Max(0, h.Rotor - spool / 2);
        if (fly && h.Rotor >= 1000)
        {
            LeavePad(u, h);
            SetPhase(h, HeliPhase.TakingOff);
        }
    }

    // WantsAir: a landed helicopter has an order or a task to fly.
    private static bool WantsAir(Unit u, HeliState h) =>
        u.Order.Kind is OrderKind.Move or OrderKind.Attack or OrderKind.AttackGround or OrderKind.AttackMove or OrderKind.Guard ||
        u.AirMission is not null;

    // LiftOff climbs a helicopter straight up off the ground to its hide height, then it flies.
    private void LiftOff(Unit u, HeliState h, ScoutFlight f, RotarySpec r)
    {
        h.Rotor = 1000;
        Fly(u, h, f, r, 0, 0, 0, 0, 0, GroundRaw(u.Pos) + r.HideHeightRaw, true, r.TempoLeave); // (2026-10-24: at its leave tempo)
        if (u.Altitude.Raw >= GroundRaw(u.Pos) + r.HideHeightRaw - 256)
        {
            SetPhase(h, HeliPhase.Flying);
            h.Post = u.Pos;
        }
    }

    // Descend brings a helicopter straight down onto its pad (or its landing point) and sets it down there.
    private void Descend(Unit u, HeliState h, ScoutFlight f, RotarySpec r)
    {
        var pad = Unit(h.Pad);
        var at = pad is not null ? PadPoint(pad) : h.LandAt;
        var ground = GroundRaw(at);
        FlyTo(u, h, f, r, at, r.SideSpeed * r.TempoReturn / 100, null, 0, ground, true, r.TempoReturn); // (2026-10-24: at its return tempo)
        if (u.Altitude.Raw > ground + 64 || !Within(u.Pos, at, Fixed.FromRaw(Fixed.OneRaw / 2)))
        {
            return;
        }
        (u.Pos, u.Altitude) = (at, Fixed.FromRaw(ground));
        SetPhase(h, HeliPhase.Landed);
        if (pad is not null)
        {
            Park(u, h, pad);
        }
    }

    // Park sets helicopter u down on pad (a helipad, or an airfield station: it takes the station's place).
    private void Park(Unit u, HeliState h, Unit pad)
    {
        h.Pad = pad.Id;
        if (StationFor(pad) is { } st)
        {
            (st.Occupant, st.ReservedBy) = (u.Id, 0);
        }
        (h.FuelAcc, h.AmmoAcc, h.HpAcc) = (0, 0, 0);
    }

    // LeavePad lifts helicopter u off its pad: the pad is free again (unless it only goes round: homing keeps it).
    private void LeavePad(Unit u, HeliState h)
    {
        if (Unit(h.Pad) is { } pad && StationFor(pad) is { } st && st.Occupant == u.Id)
        {
            st.Occupant = 0;
        }
        h.Pad = 0;
    }

    // CheckPad drops helicopter u's pad when it no longer stands (destroyed, rubble) or was never one it may use.
    private void CheckPad(Unit u, HeliState h)
    {
        if (h.Pad == 0)
        {
            return;
        }
        if (Unit(h.Pad) is not { Alive: true } pad || pad.Owner != u.Owner || pad.Rubble || !pad.IsBuilt)
        {
            if (Unit(h.Pad) is { } gone && StationFor(gone) is { } st)
            {
                (st.Occupant, st.ReservedBy) = (st.Occupant == u.Id ? 0 : st.Occupant, st.ReservedBy == u.Id ? 0 : st.ReservedBy);
            }
            h.Pad = 0;
        }
    }

    // ---- Out of control ----

    // FlyOutOfControl flies a helicopter out of control (the owner's standard): it heads for the nearest friendly pad it can
    // take (helipad or airfield station; else the nearest one, to set down beside it), spinning, slower, sinking evenly
    // from where it was toward its end height; over the pad it comes down hard. The drain and the crash are its damage pass's.
    private void FlyOutOfControl(Unit u, HeliState h, ScoutFlight f, RotarySpec r)
    {
        var o = r.OutOfControl;
        h.Rotor = 1000;
        var (cos, sin) = RotationOf(o.SpinRate);
        Turn(h, cos, h.Spin >= 0 ? sin : -sin);
        var home = Unit(h.Pad) is { } pad ? PadPoint(pad) : h.LandAt != default ? h.LandAt : (Vec?)null; // no pad at all: it drifts down where it is
        var goal = home ?? u.Pos;
        var elapsed = o.Ticks - h.OocLeft;
        var ground = GroundRaw(u.Pos);
        var end = o.EndHeightRaw + (h.OocHeight - o.EndHeightRaw) * Math.Max(0, h.OocLeft) / Math.Max(1, o.Ticks);
        long dx = goal.X.Raw - u.Pos.X.Raw, dy = goal.Y.Raw - u.Pos.Y.Raw;
        var dist = Len3(dx, dy, 0);
        var near = home is not null && dist <= 2 * HeliArriveRaw && Len3(h.VelX, h.VelY, 0) <= 4 * HeliStillRaw && elapsed > TicksPerSecond;
        var speed = Cruise(u) * o.SpeedPercent / 100;
        long wx = 0, wy = 0;
        if (dist > HeliArriveRaw / 8)
        {
            var sp = Math.Min(speed, Fixed.Sqrt(2 * r.AccelPerTick * TicksPerSecond * dist * 6 / 10));
            (wx, wy) = (dx * sp / dist, dy * sp / dist);
        }
        long ex = wx - h.VelX, ey = wy - h.VelY;
        var el = Len3(ex, ey, 0);
        if (el > r.AccelPerTick)
        {
            (ex, ey) = (ex * r.AccelPerTick / el, ey * r.AccelPerTick / el);
        }
        h.VelX += ex;
        h.VelY += ey;
        var want = near ? GroundRaw(goal) : ground + end;
        long tx = h.VelX + h.RemX, ty = h.VelY + h.RemY;
        long sx = tx / TicksPerSecond, sy = ty / TicksPerSecond;
        (h.RemX, h.RemY) = (tx - sx * TicksPerSecond, ty - sy * TicksPerSecond);
        var pos = OnMap(u.Pos.X.Raw + sx, u.Pos.Y.Raw + sy);
        (u.Vel, u.Pos) = (P(pos.X.Raw - u.Pos.X.Raw, pos.Y.Raw - u.Pos.Y.Raw), pos);
        Facing.TryFromDirection(P(h.HeadX, h.HeadY), out var facing);
        u.Facing = facing;
        var down = near ? 2 * r.DescentPerTick : r.DescentPerTick;
        VerticalStep(u, want, f.ClimbPerTick, down, Math.Max(1, f.ClimbPerTick / 10), GroundRaw(pos), false);
        if (near && u.Altitude.Raw <= GroundRaw(pos) + 64)
        {
            HardLanding(u, h);
        }
    }

    // HardLanding sets helicopter u down hard where it is (over its pad: on it): it survives with the HP it has left, its
    // pad (if any) services it as on a Rearm order, then holds it there.
    private void HardLanding(Unit u, HeliState h)
    {
        (h.VelX, h.VelY, h.RemX, h.RemY) = (0, 0, 0, 0);
        u.Vel = default;
        u.VelZ = Fixed.Zero;
        u.Altitude = Fixed.FromRaw(GroundRaw(u.Pos));
        SetPhase(h, HeliPhase.Landed);
        (h.OocLeft, h.Crash) = (0, HeliCrashKind.HardLanded);
        h.Hold = true;
        if (Unit(h.Pad) is { } pad && Within(u.Pos, PadPoint(pad), Fixed.FromInt(2)))
        {
            u.Pos = PadPoint(pad);
            Park(u, h, pad);
            (u.Homing, u.HomeReason) = (true, HomeReason.Rearm); // then serviced there (refuel, rearm, repair), held after
        }
        else
        {
            LeavePad(u, h);
        }
        _heliCrashes.Add(new HeliCrash(u.Id, u.Owner, u.Def.Archetype.Index, HeliCrashKind.HardLanded, u.Pos, u.Altitude, [], 0));
    }

    // ---- Queries ----

    // HeliActivity is helicopter u's activity and its progress for AirStatusOf, or null to use the aircraft rules (in
    // flight on an order, a task or a trip home: Striking, Engaging, Patrolling, Returning, ...).
    private (AirActivity, int)? HeliActivity(Unit u, HeliState h, ScoutFlight f, int fuel, int ammo, int max)
    {
        var r = f.Rotor!;
        switch (h.Phase)
        {
            case HeliPhase.OutOfControl:
                return (AirActivity.OutOfControl, h.OocLeft * 100 / Math.Max(1, r.OutOfControl.Ticks));
            case HeliPhase.Landing or HeliPhase.TakingOff:
                return (AirActivity.Vertical, 0);
            case HeliPhase.Landed:
                if (h.Errand == HeliErrand.Board)
                {
                    return (AirActivity.Boarding, 0);
                }
                if (h.Errand == HeliErrand.Drop)
                {
                    return (AirActivity.Unloading, 0);
                }
                var full = u.HomeReason == HomeReason.Rearm || !u.Homing && h.Hold;
                if (Unit(h.Pad) is { } pad && PadRates(pad) is var (fr, rr, pr))
                {
                    if (fuel < 100 && fr > 0)
                    {
                        return (AirActivity.Refuelling, fuel);
                    }
                    if (full && rr > 0 && ammo < max)
                    {
                        return (AirActivity.Rearming, max > 0 ? ammo * 100 / max : 100);
                    }
                    if (full && pr > 0 && u.HP < u.Def.MaxHp)
                    {
                        return (AirActivity.Repairing, u.HP * 100 / Math.Max(1, u.Def.MaxHp));
                    }
                }
                return (h.Hold || !WantsAir(u, h) ? AirActivity.Held : AirActivity.Ready, 100);
        }
        if (h.Errand != HeliErrand.None)
        {
            return (h.Errand == HeliErrand.Board ? AirActivity.Boarding : AirActivity.Unloading, 0);
        }
        if (!u.Homing && u.Order.Kind == OrderKind.Idle && u.AirMission is null && h.Target == 0)
        {
            return (AirActivity.Hovering, 0);
        }
        return null;
    }

    /// <summary>
    /// Helicopter u's state for a client (read-only): its phase, attack step, target, heading (apart from its velocity:
    /// it flies sideways and turns on the spot), velocity, height over the ground, rotor, pad, riders and its out-of-control
    /// clock with the spin and sink rates to draw. Default for anything that is not a helicopter.
    /// </summary>
    public HeliStatus HeliStatusOf(Unit u)
    {
        if (u.Heli is not { } h || u.Def.Archetype.Flight?.Rotor is not { } r)
        {
            return default;
        }
        var ooc = h.Phase == HeliPhase.OutOfControl;
        var o = r.OutOfControl;
        var sink = ooc ? (h.OocHeight - o.EndHeightRaw) / Math.Max(1, o.Ticks) : 0;
        return new HeliStatus(h.Phase, h.Attack, h.Target, h.HeadX, h.HeadY, h.VelX, h.VelY, u.VelZ.Raw, u.Altitude.Raw - GroundRaw(u.Pos), h.Rotor,
            h.Pad, RidersOf(u).Count, r.Riders?.Seats ?? 0, ooc, ooc ? h.OocLeft : 0, ooc ? (h.Spin >= 0 ? o.SpinRate : -o.SpinRate) : 0, sink, h.Errand);
    }
}
