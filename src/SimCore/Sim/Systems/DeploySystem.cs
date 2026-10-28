using SimCore.FixedPoint;

namespace SimCore.Sim;

// The deploy system: units that must set up before firing (rocket trucks,
// Archetype.DeployTicks > 0) deploy when they hold an attack or attack-ground
// order in a firing position, or on a Deploy command, and stay deployed until
// they have to move (a path to follow) or get a Stow command; then they pack up
// (StowTicks) before they can drive. Packing up ends a salvo in progress. On a map with
// terrain they set up only on Flat or Gentle ground: on a slope they first move to the nearest
// spot within a few cells where they can (or don't deploy at all). A launcher whose box is fixed on the hull (hullAims) first
// turns in place to face what its order fires at and sets up only facing it; set up, it packs up when its target is off its arc
// (hullArc: one facing, 11.25 degrees, either side by default) between salvos, then turns and sets up again.
public sealed partial class World
{
    private void RunDeploy()
    {
        foreach (var u in _units)
        {
            var a = u.Def.Archetype;
            if (a.DeployTicks == 0 || !u.Alive || u.Inside != 0)
            {
                continue;
            }
            switch (u.Deploy)
            {
                case DeployState.Stowed:
                    // In an observation post it sets up whatever the ground: the post is a prepared position.
                    if (!u.Moving && u.CanAct && InFiringPosition(u) && (u.Garrison != 0 || CanDeployAt(u, Cell.Of(u.Pos))) && FacesAim(u))
                    {
                        StartDeploy(u);
                    }
                    break;
                case DeployState.Deploying:
                    if (u.Moving)
                    {
                        StartStow(u);
                    }
                    else if (--u.DeployLeft <= 0)
                    {
                        (u.Deploy, u.DeployLeft) = (DeployState.Deployed, 0);
                    }
                    break;
                case DeployState.Deployed:
                    if (u.Moving || (a.HullAims && u.SalvoLeft == 0 && OffArc(u)))
                    {
                        StartStow(u);
                    }
                    break;
                case DeployState.Stowing:
                    if (--u.DeployLeft <= 0)
                    {
                        (u.Deploy, u.DeployLeft) = (DeployState.Stowed, 0);
                    }
                    break;
            }
        }
    }

    // InFiringPosition reports whether u's order wants it to fire from where it stands.
    private bool InFiringPosition(Unit u) => u.Order.Kind switch
    {
        OrderKind.Attack => Unit(u.Order.Target) is { } t && PickWeapon(u, t, true) >= 0,
        OrderKind.AttackGround => GroundWeapon(u, true) >= 0,
        _ => false,
    };

    // FacesAim turns a hull-aiming launcher (hullAims) in place toward what its order fires at, at its turn rate, and reports
    // whether it faces it now (always true for any other unit).
    private bool FacesAim(Unit u)
    {
        if (!u.Def.Archetype.HullAims || !AimFacing(u, out var want) || u.Facing == want)
        {
            return true;
        }
        u.Facing = Facing.TurnToward(u.Facing, want, u.Def.TurnRate);
        u.TurretFacing = u.Facing;
        return u.Facing == want;
    }

    // OffArc reports whether set-up u's order fires at something off its hull's arc (archetype hullArc, in facings either side).
    private bool OffArc(Unit u) =>
        u.Order.Kind is OrderKind.Attack or OrderKind.AttackGround && AimFacing(u, out var want) &&
        Math.Abs(Facing.Diff(u.Facing, want)) > u.Def.Archetype.HullArc && InFiringPosition(u);

    // AimFacing is the facing from u to what its attack order fires at: the target unit or the centre of the ground cell; false
    // when u stands on it (or the target is gone).
    private bool AimFacing(Unit u, out int want)
    {
        want = u.Facing;
        var at = u.Order.Kind == OrderKind.Attack ? Unit(u.Order.Target)?.Pos : u.Order.Cell.Center();
        return at is { } p && Facing.TryFromDirection(p - u.Pos, out want);
    }

    private static void StartDeploy(Unit u) =>
        (u.Deploy, u.DeployLeft) = (DeployState.Deploying, u.Def.Archetype.DeployTicks);

    private static void StartStow(Unit u)
    {
        (u.Deploy, u.DeployLeft) = (DeployState.Stowing, Math.Max(1, u.Def.Archetype.StowTicks));
        if (u.SalvoLeft > 0)
        {
            u.SalvoLeft = 0;
            u.Reload = u.Def.Weapons[u.SalvoWeapon].Reload * 100;
        }
    }
}
