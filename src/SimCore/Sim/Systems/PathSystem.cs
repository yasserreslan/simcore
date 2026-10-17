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
