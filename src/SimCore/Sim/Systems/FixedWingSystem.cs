using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The fixed-wing kinematics (E4; the plan's air-combat model, section 2.4): the jets' flight model,
// plugged under the same mission layer and flight controller as the drones (MissionSystem.cs). A
// flight block with kind "fixedWing" (FixedWingSpec) replaces the drone law in free flight; the
// airfield phases (holding, final, taxi, takeoff, climb-out) keep their own steering at the drone-law
// rate of its max-g turn at cruise speed (the overhead pattern and the afterburner takeoff are E9).
//
// Per tick, all integer, speeds in raw a second (raw a tick times 20, like AirOps.Speed):
//  1. Turn: the controller's demand (edge avoidance first; an aim behind the beam and beyond twice
//     its turn radius: a full turn to its side; else the velocity moves toward the aim's direction)
//     changes the velocity by at most a(S) / 20 a tick, a(S) = min(aG, aG x (S / corner)^2): the
//     radius is S^2 / a(S), so the turn rate a / S peaks at corner speed.
//  2. Speed: the controller wants cruise speed (attack speed on a strike order, dash while boosted,
//     corner speed in an edge turn); the throttle is the lowest setting whose thrust beats drag +
//     turn bleed + gravity x sin(climb) plus closing half the speed gap a second (the afterburner
//     only when it wants more than attack speed, or below halfway from its minimum to its corner
//     speed); S changes by (thrust - that resistance) / 20, kept within [minimum, dash].
//  3. It moves by its exact velocity (the twentieth's remainder carried to the next tick); edge
//     clamps are counted (AirEdgeClamps: edge avoidance keeps them at 0).
//  4. Height: toward its flight height (its ingress height on a strike order) over the highest of
//     the ground under it and LookAheadTicks of its velocity ahead, plus (ID mod 5) separation
//     steps; climbing at most the flight's climb rate (scaled down below cruise speed, none at its
//     minimum speed: the stall), sinking at most its dive rate, never below its floor.
//  5. Fuel: its throttle's burn (percent of nominal) goes into an accumulator; the lifetime system
//     takes whole units off its lifetime (the fuel), so bingo, service and the UI work unchanged.
//     Bingo is still worked out at cruise burn, so an aircraft on afterburner reaches it sooner.
public sealed partial class World
{
    // Standard gravity, raw a second squared (9.81 m/s^2 at 512 raw a metre): the load factor's g.
    private const long GravityRaw = 5023;

    // FlyWing moves fixed-wing aircraft u one tick of free flight steering at aim (home: a ground
    // control station it glides down to, or null).
    private void FlyWing(Unit u, ScoutFlight f, FixedWingSpec g, Vec aim, Unit? home)
    {
        var st = u.Wing ??= NewWing(u, g);
        var s = st.Speed;
        long vx = st.VelX, vy = st.VelY;
        var vl = Math.Max(1, Len3(vx, vy, 0));
        var strike = u.Order.Kind == OrderKind.Attack && !u.Homing;
        var want = st.Boost > 0 && g.HasAfterburner ? g.Dash : strike ? g.Military : g.Cruise;
        if (st.Boost > 0)
        {
            st.Boost--;
        }

        // 1. The turn: at most a(S) a second squared, a twentieth of it a tick.
        var aMax = g.LateralAccel(s);
        var dv = Math.Max(1, aMax / TicksPerSecond);
        var radius = s * s / aMax;
        // Edge avoidance looks a second ahead plus, above cruise speed, the way it needs to slow
        // to it at idle (at least cruise thrust less idle thrust, its drag only growing faster).
        var brake = s > g.Cruise ? (s * s - g.Cruise * g.Cruise) / (2 * Math.Max(1, g.ThrustCruise - g.ThrustIdle)) : 0;
        var side = EdgeSide(u, f, aim, vx, vy, radius + s, vx + vx * brake / vl, vy + vy * brake / vl);
        if (side != 0)
        {
            want = Math.Min(want, g.Corner); // an edge turn: pull hard and let it bleed to its tightest turn
        }
        long nvx = vx, nvy = vy;
        if (side == 0)
        {
            long wx = aim.X.Raw - u.Pos.X.Raw, wy = aim.Y.Raw - u.Pos.Y.Raw;
            var wl = Len3(wx, wy, 0);
            if (wl > 0)
            {
                var behind = wx * vx + wy * vy < 0;
                if (behind && wl > 2 * radius)
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
        (nvx, nvy) = (nvx * ns / nvl, nvy * ns / nvl);

        // 3. The move: its exact velocity, the twentieth's remainder carried.
        long tx = nvx + st.RemX, ty = nvy + st.RemY;
        long sx = tx / TicksPerSecond, sy = ty / TicksPerSecond;
        (st.RemX, st.RemY) = (tx - sx * TicksPerSecond, ty - sy * TicksPerSecond);
        var pos = FlyStep(u, sx, sy);

        // 4. The height.
        var under = GroundRaw(pos);
        var band = strike && g.IngressRaw > 0 ? g.IngressRaw : f.HeightRaw;
        var wantAlt = Math.Max(under, GroundRaw(OnMap(pos.X.Raw + sx * g.LookAheadTicks, pos.Y.Raw + sy * g.LookAheadTicks))) + band +
            u.Id % 5 * g.SeparationRaw;
        if (home is not null)
        {
            // Homing to a ground control station: it glides down to land there in time (the drone's rule).
            var land = GroundRaw(home.Pos) + f.LaunchHeightRaw;
            var glideTicks = (u.Altitude.Raw - land) / g.DivePerTick;
            if ((home.Pos - pos).Len().Raw * TicksPerSecond <= (glideTicks + 1) * ns)
            {
                wantAlt = land;
            }
        }
        var up = s <= g.Min ? 0 : f.ClimbPerTick * Math.Min(ns, g.Cruise) / g.Cruise;
        var alt = u.Altitude.Raw + Math.Clamp(wantAlt - u.Altitude.Raw, -g.DivePerTick, up);
        alt = Math.Max(alt, under + g.FloorRaw);
        u.VelZ = Fixed.FromRaw(alt - u.Altitude.Raw);
        u.Altitude = Fixed.FromRaw(alt);

        // 5. The state, and the fuel its throttle burnt.
        (st.Speed, st.VelX, st.VelY, st.Throttle, st.Lateral) = (ns, nvx, nvy, th, lateral);
        var n = lateral * 1000 / GravityRaw;
        st.LoadPermille = (int)Fixed.Sqrt(1_000_000 + n * n);
        st.FuelAcc += g.Burn(th);
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
    /// on the takeoff roll and the climb-out, cruise holding; E9 adds the afterburner takeoff); cruise
    /// for a drone in free flight.
    /// </summary>
    public static Throttle ThrottleOf(Unit u) => u.Wing is { } st ? st.Throttle : u.AirOps?.Phase switch
    {
        null or FlightPhase.Airborne or FlightPhase.Holding => Throttle.Cruise,
        FlightPhase.TakeoffRoll or FlightPhase.Climb => Throttle.Military,
        _ => Throttle.Idle,
    };
}
