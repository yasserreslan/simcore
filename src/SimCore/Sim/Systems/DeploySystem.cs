namespace SimCore.Sim;

// The deploy system: units that must set up before firing (rocket trucks,
// Archetype.DeployTicks > 0) deploy when they hold an attack or attack-ground
// order in a firing position, or on a Deploy command, and stay deployed until
// they have to move (a path to follow) or get a Stow command; then they pack up
// (StowTicks) before they can drive. Packing up ends a salvo in progress. On a map with
// terrain they set up only on Flat or Gentle ground: on a slope they first move to the nearest
// spot within a few cells where they can (or don't deploy at all).
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
                    if (!u.Moving && u.CanAct && InFiringPosition(u) && CanDeployAt(u, Cell.Of(u.Pos)))
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
                    if (u.Moving)
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
