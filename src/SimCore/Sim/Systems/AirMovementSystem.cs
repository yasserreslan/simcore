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
        _flightUnits = 0;
        foreach (var u in _units)
        {
            if (u.Def.Archetype.Flight is { } scout)
            {
                _flightUnits++;
                if (u.Alive && u.CanAct)
                {
                    FlyScout(u, scout.Speeds is null ? scout : FlightOf(u)); // a scout drone orbits (the scout system); E13: a jet's flight on this map
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
