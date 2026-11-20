using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The air movement system: flying units skip pathfinding and fly straight
// at their goal, over rock and units alike, turning as they go.
public sealed partial class World
{
    // Scratch (not state): how many units with a flight block air movement saw this tick (0: no aircraft
    // anywhere, so the ground guns against low aircraft have nothing to look for; E10).
    private int _flightUnits;

    private void RunAirMovement()
    {
        _strikeReports.Clear();
        _airfieldEvents.Clear();
        ClearAirCombatEvents();
        ClearStrikeEvents();
        _heliCrashes.Clear();
        _kamanEvents.Clear();
        _flightUnits = 0;
        foreach (var u in _units)
        {
            if (u.Def.Archetype.Flight is { } scout)
            {
                _flightUnits++;
                if (u.Def.Archetype.Kaman is { } kaman)
                {
                    if (u.Alive)
                    {
                        FlyKaman(u, scout, kaman); // a Kaman drone: missiles, then the dive into its target (KamanSystem.cs)
                    }
                    continue;
                }
                if (scout.Rotor is { } rotor)
                {
                    if (u.Alive)
                    {
                        HeliWantsRiders(u, u.Heli!); // soldiers sent to board an idle one: it lands where it is (HeliPadSystem.cs)
                        FlyHeli(u, scout, rotor); // a helicopter (HeliFlightSystem.cs)
                    }
                    continue;
                }
                if (u.Alive && u.CanAct)
                {
                    FlyScout(u, scout.Speeds is null && u.Def.Archetype.Loadouts.Count == 0 ? scout : FlightOf(u)); // a scout drone orbits (the scout system); E13: a jet's flight on this map (and its loadout mode's speed)
                }
                continue;
            }
            if (u.Surveil && u.Def.Archetype.Surveil is { } sv)
            {
                if (u.Alive && u.CanAct)
                {
                    FlySurveil(u, sv); // INF-EE: an FPV drone on surveillance circles its point
                }
                continue;
            }
            if (u.Def.Layer != Layers.Air || u.Def.Speed.Raw == 0 || !u.CanAct || !AirGoal(u, out var goal))
            {
                continue;
            }
            var d = goal - u.Pos;
            if (Facing.TryFromDirection(d, out var want))
            {
                u.Facing = Facing.TurnToward(u.Facing, want, u.Def.TurnRate);
                u.TurretFacing = u.Facing;
            }
            u.Pos = d.Len() <= u.Def.Speed ? goal : u.Pos + d.WithLen(u.Def.Speed);
        }
    }

    // FlySurveil (INF-EE) flies an FPV drone on surveillance: straight at its point while further than twice the circle's radius,
    // then round it counterclockwise (screen space) the radius out: each tick it heads for the point on the circle a step
    // further round from where it is (integer vectors only), at its speed.
    private static void FlySurveil(Unit u, SurveilSpec sv)
    {
        var c = u.OrbitAt;
        var r = u.Pos - c;
        var aim = c;
        if (r.Len().Raw <= 2 * sv.Radius.Raw)
        {
            if (r.LenSq() == 0)
            {
                r = Facing.Dir(u.Facing); // right over the point: out along its heading
            }
            var round = new Vec(r.Y, -r.X).WithLen(u.Def.Speed); // a quarter turn of r, one step long
            aim = c + (r.WithLen(sv.Radius) + round).WithLen(sv.Radius);
        }
        var d = aim - u.Pos;
        if (Facing.TryFromDirection(d, out var want))
        {
            u.Facing = Facing.TurnToward(u.Facing, want, u.Def.TurnRate);
            u.TurretFacing = u.Facing;
        }
        u.Pos = d.Len() <= u.Def.Speed ? aim : u.Pos + d.WithLen(u.Def.Speed);
    }

    // AirGoal returns where a flying unit is headed: its attack target, or
    // the cell of its move order. False when it has nowhere to go.
    private bool AirGoal(Unit u, out Vec goal)
    {
        goal = default;
        switch (u.Order.Kind)
        {
            case OrderKind.Attack when Unit(u.Order.Target) is { Inside: 0 } t:
                goal = t.Pos;
                return true;
            case OrderKind.Attack or OrderKind.Move:
                goal = u.Order.Cell.Center();
                return true;
            default:
                return false;
        }
    }
}
