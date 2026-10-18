using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The air movement system: flying units skip pathfinding and fly straight
// at their goal, over rock and units alike, turning as they go.
public sealed partial class World
{
    private void RunAirMovement()
    {
        foreach (var u in _units)
        {
            if (u.Def.Archetype.Flight is { } scout)
            {
                if (u.Alive && u.CanAct)
                {
                    FlyScout(u, scout); // a scout drone orbits (the scout system)
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
