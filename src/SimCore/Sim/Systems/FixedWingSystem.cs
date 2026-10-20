using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The fixed-wing kinematics (E4; the plan's air-combat model, section 2.4): the jets' flight model,
// plugged under the same mission layer and flight controller as the drones (MissionSystem.cs). A
// flight block with kind "fixedWing" (FixedWingSpec) replaces the drone law in free flight; the
// airfield phases (holding, final, taxi, takeoff) keep their own steering, a fixed wing's turns held
// within a(S) below, and its climb-out runs on this envelope's speed law (E9: AirfieldFlightSystem.cs).
//
// Per tick, all integer, speeds in raw a second (raw a tick times 20, like AirOps.Speed):
//  1. Turn: the controller's demand (the fly zone first; an aim behind the beam and beyond twice
//     its turn radius: a full turn to its side; else the velocity moves toward the aim's direction)
//     changes the velocity by at most a(S) / 20 a tick, a(S) = min(aG, aG x (S / corner)^2): the
//     radius is S^2 / a(S), so the turn rate a / S peaks at corner speed.
//  2. Speed: the controller wants cruise speed (attack speed on a strike order, dash while boosted,
//     corner speed in an edge turn); the throttle is the lowest setting whose thrust beats drag +
//     turn bleed + gravity x sin(climb) plus closing half the speed gap a second (the afterburner
//     only when it wants more than attack speed, or below halfway from its minimum to its corner
//     speed); S changes by (thrust - that resistance) / 20, kept within [minimum, dash].
//  3. It moves by its exact velocity (the twentieth's remainder carried to the next tick); edge
//     clamps to the fly zone are counted (AirEdgeClamps: its turns keep them at 0).
//  4. Height (E12): toward its flight height (its ingress height on a strike order) over the surface
//     the height planner sees along its path (FlightHeight.cs: terrain and structure tops, climbing
//     early), plus (ID mod 5) separation steps; climbing at most the flight's climb rate (scaled down
//     below cruise speed, none at its minimum speed: the stall), its vertical acceleration bounded,
//     never below its floor.
//  5. Fuel: its throttle's burn (percent of nominal) goes into an accumulator; the lifetime system
//     takes whole units off its lifetime (the fuel), so bingo, service and the UI work unchanged.
//     Bingo is still worked out at cruise burn, so an aircraft on afterburner reaches it sooner.
public sealed partial class World
{
    // Standard gravity, raw a second squared (9.81 m/s^2 at 512 raw a metre): the load factor's g.
    private const long GravityRaw = 5023;

    // A quick turn (D-TURN1) is over once its aim is within 10 degrees of its nose (cos, thousandths).
    private const long QuickTurnDoneCos = 985;

    // FlyWing moves fixed-wing aircraft u one tick of free flight steering at aim (home: a ground
    // control station it glides down to, or null).
    private void FlyWing(Unit u, ScoutFlight f, FixedWingSpec g, Vec aim, Unit? home)
    {
        var st = u.Wing ??= NewWing(u, g);
        var s = st.Speed;
        long vx = st.VelX, vy = st.VelY;
        var vl = Math.Max(1, Len3(vx, vy, 0));
        var strike = u.Order.Kind is OrderKind.Attack or OrderKind.AttackGround && !u.Homing;
        var want = st.Boost > 0 && g.HasAfterburner ? g.Dash : strike ? g.Military : g.Cruise;
        if (st.Boost > 0)
        {
            st.Boost--;
        }
        // A fresh player order (E12, D-TURN1): for up to 2 s a max-g turn toward the new aim (never the
        // lazy turn) on idle with its speed brakes out, slowing to its corner speed (its tightest turn,
        // the turn rate's peak); over once the aim is within QuickTurnDoneCos of its nose.
        var quick = st.QuickTurn > 0;
        if (quick)
        {
            st.QuickTurn--;
            long qx = aim.X.Raw - u.Pos.X.Raw, qy = aim.Y.Raw - u.Pos.Y.Raw;
            var ql = Len3(qx, qy, 0);
            if (ql == 0 || (qx * vx + qy * vy) * 1000 >= QuickTurnDoneCos * ql * vl)
            {
                (st.QuickTurn, quick) = (0, false);
            }
            else
            {
                want = Math.Min(want, g.Corner);
            }
        }

        // Evasion (E6): breaking against a missile still flying, the aim is the beam (square to the
        // missile's line of sight, the side nearer its heading; exactly square: the first), pulled at
        // breakG percent of its g, down toward its floor on the afterburner (Boost); else it engages its
        // air target (lead pursuit by SteerAim, the target's height, its speed for the gun close in).
        var threat = Threat(u, st);
        var air = threat is null ? AirEngagement(u) : null;
        if (threat is not null)
        {
            long lx = u.Pos.X.Raw - threat.Pos.X.Raw, ly = u.Pos.Y.Raw - threat.Pos.Y.Raw;
            var (bx, by) = (-ly, lx);
            if (bx * vx + by * vy < 0)
            {
                (bx, by) = (ly, -lx);
            }
            if (bx != 0 || by != 0)
            {
                aim = P(u.Pos.X.Raw + bx, u.Pos.Y.Raw + by);
            }
        }
        else if (air is { } a0)
        {
            want = GunApproachSpeed(u, a0, want, vx, vy);
        }

        // 1. The turn: at most a(S) a second squared, a twentieth of it a tick.
        var aMax = g.LateralAccel(s);
        if (threat is not null && u.Def.Archetype.Evade is { BreakG: < 100 } ev)
        {
            aMax = Math.Max(1, aMax * ev.BreakG / 100);
        }
        var dv = Math.Max(1, aMax / TicksPerSecond);
        var radius = s * s / aMax;
        // The fly zone looks a second ahead plus, above cruise speed, the way it needs to slow
        // to it at idle (at least cruise thrust less idle thrust, its drag only growing faster).
        var brake = s > g.Cruise ? (s * s - g.Cruise * g.Cruise) / (2 * Math.Max(1, g.ThrustCruise - g.ThrustIdle)) : 0;
        var (side, hard) = EdgeTurn(u, f, aim, vx, vy, radius + s, vx + vx * brake / vl, vy + vy * brake / vl);
        if (hard)
        {
            want = Math.Min(want, g.Corner); // at the fly zone's edge: pull hard and let it bleed to its tightest turn
        }
        long nvx = vx, nvy = vy;
        if (side == 0)
        {
            long wx = aim.X.Raw - u.Pos.X.Raw, wy = aim.Y.Raw - u.Pos.Y.Raw;
            var wl = Len3(wx, wy, 0);
            if (wl > 0)
            {
                var behind = wx * vx + wy * vy < 0;
                if (behind && (wl > 2 * radius || quick))
                {
                    side = vx * wy - vy * wx >= 0 ? 1 : -1; // behind the beam: a full turn toward it (exactly behind: right)
                }
                else if (behind)
                {
                    // Behind and within twice its turn radius: the drone law's lazy turn (it swings
                    // wide and comes back over the point instead of circling round it for ever).
                    long dx = wx * s / wl - vx, dy = wy * s / wl - vy;
                    var dl = Len3(dx, dy, 0);
                    if (dl > dv)
                    {
                        (dx, dy) = (dx * dv / dl, dy * dv / dl);
                    }
                    (nvx, nvy) = (vx + dx, vy + dy);
                }
                else
                {
                    // Ahead: a pure lateral turn onto the aim's bearing, the whole of its g while the
                    // bearing is off by more than a tick's turn (s x sin of the error, at most a(S) / 20).
                    var perp = Math.Clamp((vx * wy - vy * wx) / wl, -dv, dv);
                    (nvx, nvy) = (vx - vy * perp / vl, vy + vx * perp / vl);
                }
            }
        }
        if (side != 0)
        {
            (nvx, nvy) = (vx - side * vy * dv / vl, vy + side * vx * dv / vl);
        }
        var nvl = Math.Max(1, Len3(nvx, nvy, 0));
        // The lateral acceleration it pulled: S x the heading change a tick x 20 (signed: + clockwise on the map).
        var lateral = Math.Clamp((vx * nvy - vy * nvx) * TicksPerSecond / vl * s / nvl, -aMax, aMax);

        // 2. The speed: the throttle and the energy balance (last tick's climb angle for gravity).
        var vz = u.VelZ.Raw * TicksPerSecond;
        var l3 = Len3(s, vz, 0);
        var sin = l3 > 0 ? vz * 1000 / l3 : 0;
        var resist = g.Resistance(s, Math.Abs(lateral), sin);
        var need = resist + (want - s) / 2;
        var top = g.HasAfterburner && (want > g.Military || s < (g.Min + g.Corner) / 2) ? Throttle.Afterburner : Throttle.Military;
        var th = top;
        for (var t = Throttle.Idle; t < top; t++)
        {
            if (g.Thrust(t) >= need)
            {
                th = t;
                break;
            }
        }
        var ns = Math.Clamp(s + (g.Thrust(th) - resist) / TicksPerSecond, g.Min, g.Dash);
        if (quick && threat is null && ns > g.Corner)
        {
            // The quick turn's speed brakes (D-TURN1): from cruise down to its corner speed in about a
            // second (cruise less corner a second squared), never below its corner speed.
            ns = Math.Max(g.Corner, ns - Math.Max(1, (g.Cruise - g.Corner) / TicksPerSecond));
        }
        (nvx, nvy) = (nvx * ns / nvl, nvy * ns / nvl);

        // 3. The move: its exact velocity, the twentieth's remainder carried.
        long tx = nvx + st.RemX, ty = nvy + st.RemY;
        long sx = tx / TicksPerSecond, sy = ty / TicksPerSecond;
        (st.RemX, st.RemY) = (tx - sx * TicksPerSecond, ty - sy * TicksPerSecond);
        var pos = FlyStep(u, sx, sy);

        // 4. The height (E12: the planner, FlightHeight.cs): its flight height over the terrain along its
        // path (its floor over roofs); a strike's profile, a break or an air target never below the same
        // profile at its floor, and those may dive at its dive rate and pull its full g; following the ground
        // it descends at half its climb rate and pulls half its g.
        var under = GroundRaw(pos);
        var up = s <= g.Min ? 0 : f.ClimbPerTick * Math.Min(ns, g.Cruise) / g.Cruise;
        var (ox, oy, nx, ny) = (vx / TicksPerSecond, vy / TicksPerSecond, nvx / TicksPerSecond, nvy / TicksPerSecond);
        var band = (strike && g.IngressRaw > 0 ? g.IngressRaw : f.HeightRaw) + u.Id % 5 * g.SeparationRaw;
        var (wantAlt, low) = PlanHeight(pos, ox, oy, nx, ny, f.ClimbPerTick, g.LookAheadTicks, band, g.FloorRaw, g.FloorRaw, g.FloorRaw);
        var pull = Math.Max(1, g.TurnAccel / 400);
        if (u.VelZ.Raw < 0)
        {
            low += u.VelZ.Raw * u.VelZ.Raw / (2 * pull); // diving: what it sinks before its full g stops the descent
        }
        var tactical = false;
        if (u.Strike is { } run && home is null)
        {
            wantAlt = Math.Max(low, StrikeAltitude(u, f, g, run, pos, under, wantAlt)); // an air-to-ground pass's profile (E7)
            tactical = true;
        }
        if (threat is not null)
        {
            (wantAlt, tactical) = (low, true); // a break dives toward its floor
        }
        else if (air is { } a1)
        {
            (wantAlt, tactical) = (Math.Max(low, a1.Alt), true); // engaging: the target's height
        }
        if (home is not null)
        {
            // Homing to a ground control station: it glides down to land there in time (the drone's rule).
            var land = GroundRaw(home.Pos) + f.LaunchHeightRaw;
            var glideTicks = (u.Altitude.Raw - land) / g.DivePerTick;
            if ((home.Pos - pos).Len().Raw * TicksPerSecond <= (glideTicks + 1) * ns)
            {
                (wantAlt, tactical) = (land, true);
            }
        }
        var down = tactical ? g.DivePerTick : Math.Max(1, Math.Min(g.DivePerTick, f.ClimbPerTick) / 2);
        VerticalStep(u, wantAlt, up, down, tactical ? pull : Math.Max(1, g.TurnAccel / 800), under + g.FloorRaw, !tactical);

        // 5. The state, and the fuel its throttle burnt.
        (st.Speed, st.VelX, st.VelY, st.Throttle, st.Lateral) = (ns, nvx, nvy, th, lateral);
        var n = lateral * 1000 / GravityRaw;
        st.LoadPermille = (int)Fixed.Sqrt(1_000_000 + n * n);
        st.FuelAcc += g.Burn(th);
    }

    // Threat is the missile fixed wing u breaks against this tick (its break counts down), or null; a
    // missile gone ends the break and clears it.
    private Projectile? Threat(Unit u, FixedWingState st)
    {
        if (st.EvadeFrom == 0)
        {
            return null;
        }
        if (ProjectileById(st.EvadeFrom) is not { Done: false } m)
        {
            (st.Evade, st.EvadeFrom) = (0, 0);
            return null;
        }
        if (st.Evade == 0)
        {
            return null; // the break is over; the missile is not broken against twice
        }
        st.Evade--;
        return m;
    }

    // NewWing is u's fixed-wing state as it starts free flight: its speed and heading from its last
    // step (off the climb-out, or a ground control station), or cruise speed the way it faces (put
    // down in flight).
    private static FixedWingState NewWing(Unit u, FixedWingSpec g)
    {
        long vx = u.Vel.X.Raw, vy = u.Vel.Y.Raw;
        var l = Len3(vx, vy, 0);
        var s = l == 0 ? g.Cruise : Math.Clamp(l * TicksPerSecond, g.Min, g.Dash);
        if (l == 0)
        {
            var d = Facing.Dir(u.Facing);
            (vx, vy, l) = (d.X.Raw, d.Y.Raw, Math.Max(1, Len3(d.X.Raw, d.Y.Raw, 0)));
        }
        return new FixedWingState { Speed = s, VelX = vx * s / l, VelY = vy * s / l, Throttle = Throttle.Cruise, LoadPermille = 1000 };
    }

    // WingFuel is the fuel (whole lifetime units) fixed wing u burnt since the last call; 1 a tick
    // for everything else that flies on a lifetime (its nominal burn).
    private static int WingFuel(Unit u)
    {
        if (u.Wing is not { } st)
        {
            return 1;
        }
        var n = st.FuelAcc / 100;
        st.FuelAcc -= n * 100;
        return (int)n;
    }

    /// <summary>
    /// The throttle aircraft u flies on, for renderers (the afterburner's flame, the nozzle): a fixed
    /// wing's own in free flight; at an airfield, by phase (idle on the ground and on final, military
    /// on the takeoff roll and the climb-out, or the afterburner after an afterburner takeoff (E9), cruise
    /// holding and on the overhead's join and run-in); cruise for a drone in free flight.
    /// </summary>
    public static Throttle ThrottleOf(Unit u) => u.Wing is { } st ? st.Throttle : u.AirOps switch
    {
        null or { Phase: FlightPhase.Airborne or FlightPhase.Holding } => Throttle.Cruise,
        { Phase: FlightPhase.Final, Leg: LegOhJoin or LegOhInitial } => Throttle.Cruise,
        { Phase: FlightPhase.TakeoffRoll or FlightPhase.Climb, Afterburner: true } => Throttle.Afterburner,
        { Phase: FlightPhase.TakeoffRoll or FlightPhase.Climb } => Throttle.Military,
        _ => Throttle.Idle,
    };
}
