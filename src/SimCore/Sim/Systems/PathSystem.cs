using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The path system (routing): it decides where each moving ground unit wants to go this tick
// and writes its MoveIntent; the movement system moves it. STUB (contract commit): Desired
// points at the next cell of the unit's existing A* path, at most its speed; HPA*, the road
// layer, group flow fields, formation slots and the request queue replace it.
public sealed partial class World
{
    private void RunPaths()
    {
        foreach (var u in _units)
        {
            if (u.Def.Layer != Layers.Ground)
            {
                continue;
            }
            // TEMP (pathing-movement only; dropped at the merge for agent 1's PathSystem): advance
            // past a pass-through waypoint within a cell, and past the last one once Arrived.
            if (u.Moving && (u.Intent.Stuck || u.Intent.Arrived) && u.Intent.Goal == u.Path[u.PathIdx].Center() &&
                Within(u.Pos, u.Path[^1].Center(), Fixed.FromInt(3)))
            {
                u.PathIdx = u.Path.Count; // stuck or arrived behind friendlies already at the end: done
            }
            while (u.Moving)
            {
                // A waypoint is passed once within a cell, or when the movement system reports
                // Arrived at it (the last one only so; a crowd arrival short of a waypoint too).
                var c = u.Path[u.PathIdx].Center();
                if ((u.Intent.Arrived && u.Intent.Goal == c) || (u.PathIdx < u.Path.Count - 1 && Within(u.Pos, c, Fixed.One)))
                {
                    u.PathIdx++;
                    continue;
                }
                break;
            }
            if (!u.CanAct || !u.Moving || u.Def.Speed.Raw == 0)
            {
                u.Intent = u.Intent with { Desired = default };
                continue;
            }
            var wp = u.Path[u.PathIdx].Center();
            var d = wp - u.Pos;
            var last = u.PathIdx == u.Path.Count - 1;
            // Full base speed toward pass-through waypoints; capped by the distance only at the end.
            var step = last && d.Len().Raw <= u.Def.Speed.Raw ? d : d.LenSq() == 0 ? d : d.WithLen(u.Def.Speed);
            // Arrived and Stuck belong to the movement system for the same Goal; a new Goal resets them.
            u.Intent = wp == u.Intent.Goal ? u.Intent with { Desired = step } : new MoveIntent(wp, step, default, false);
        }
    }
}
