using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The ground movement system: units on the ground drive along their A*
// paths. A vehicle without a driver stays put.
public sealed partial class World
{
    private const int MoveAlignFacings = 2; // hull must be this close to the path direction to drive

    private void RunGroundMovement()
    {
        foreach (var u in _units)
        {
            if (u.Def.Layer == Layers.Ground && u.CanAct)
            {
                Move(u);
            }
        }
    }

    // Move drives a unit along its path. Vehicles turn in place until roughly
    // facing the next cell.
    private static void Move(Unit u)
    {
        var def = u.Def;
        if (def.Speed.Raw == 0 || !u.Moving || (def.Archetype.DeployTicks > 0 && u.Deploy != DeployState.Stowed))
        {
            return; // a deployed launcher packs up before it drives
        }
        var wp = u.Path[u.PathIdx].Center();
        var d = wp - u.Pos;
        if (!Facing.TryFromDirection(d, out var want))
        {
            u.PathIdx++;
            return;
        }
        u.Facing = Facing.TurnToward(u.Facing, want, def.TurnRate);
        if (!def.Turret)
        {
            u.TurretFacing = u.Facing;
        }
        if (Math.Abs(Facing.Diff(u.Facing, want)) > MoveAlignFacings)
        {
            return;
        }
        if (d.Len() <= def.Speed)
        {
            u.Pos = wp;
            u.PathIdx++;
        }
        else
        {
            u.Pos += d.WithLen(def.Speed);
        }
    }
}
