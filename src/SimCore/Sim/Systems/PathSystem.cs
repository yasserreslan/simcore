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
                u.Intent = u.Intent with { Desired = default, Arrived = !u.Moving };
                continue;
            }
            var wp = u.Path[u.PathIdx].Center();
            var d = wp - u.Pos;
            var len = d.Len();
            var step = len.Raw <= u.Def.Speed.Raw ? d : d.WithLen(u.Def.Speed);
            u.Intent = new MoveIntent(wp, step, default, false);
        }
    }
}
