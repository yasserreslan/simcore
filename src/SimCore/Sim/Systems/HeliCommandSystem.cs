using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The helicopters' orders (the helicopters night; docs/design.md, "Helicopters"): Move, Attack, Attack-ground,
// Attack-move, Guard (a post or a unit), Stop, Return to base, Rearm, Exit (drop its riders where it is), Load and Unload;
// AirTask Patrol and Guard (and Search and Roam, flown as a loop round the area) go through the mission layer as for every
// aircraft. A helicopter out of control takes no orders (Commandable). A new order lifts a landed one off (unless it is
// held by a refuel trip: then the order waits for the trip to end), ends an errand and drops a hold on its pad.
public sealed partial class World
{
    // HeliCommand runs the helicopters' part of command c and returns c without them (for the other units), or c itself for a
    // command the helicopters take like every unit (stances, weapon modes, air tasks after a reset).
    private Command HeliCommand(Command c)
    {
        var any = false;
        foreach (var id in c.Units)
        {
            if (Unit(id) is { Heli: not null })
            {
                any = true;
                break;
            }
        }
        if (!any)
        {
            return c;
        }
        var handled = c.Kind is CommandKind.Move or CommandKind.AttackMove or CommandKind.Guard or CommandKind.Attack or CommandKind.AttackGround or
            CommandKind.Stop or CommandKind.ReturnToBase or CommandKind.Rearm or CommandKind.Exit or CommandKind.HeliLoad or CommandKind.HeliUnload or
            CommandKind.AirTask or CommandKind.AttackLine;
        if (!handled)
        {
            return c;
        }
        var rest = new List<uint>();
        var k = 0;
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (u?.Heli is not { } h)
            {
                rest.Add(id);
                continue;
            }
            if (!Commandable(u, c.Player) || !u.Alive)
            {
                continue;
            }
            if (c.Kind == CommandKind.AirTask)
            {
                Ordered(u, h, false);
                rest.Add(id); // the mission layer gives it its task
                continue;
            }
            HeliOrder(u, h, c, k++);
        }
        return c with { Units = rest };
    }

    // Ordered resets helicopter u for a new player order: its errand, its attack, its hold on a pad and (unless the order
    // sends it home, or it is on its automatic refuel trip) its trip home.
    private void Ordered(Unit u, HeliState h, bool home)
    {
        (h.Errand, h.Hold, h.Target) = (HeliErrand.None, false, 0);
        StopAttack(h);
        if (!home && u.Homing && u.HomeReason != HomeReason.Refuel)
        {
            u.Homing = false; // a recall or a Rearm visit ends; its pad stays its own until it lifts off
        }
    }

    // HeliOrder gives helicopter u (the k-th of the command's helicopters) its part of command c.
    private void HeliOrder(Unit u, HeliState h, Command c, int k)
    {
        switch (c.Kind)
        {
            case CommandKind.Move:
                if (Grid.TrySpiral(c.Cell, k, out var cell))
                {
                    Ordered(u, h, false);
                    u.AirMission = null; // a move ends its task
                    Assign(u, new Order(OrderKind.Move, cell, 0, 0));
                }
                return;
            case CommandKind.AttackMove:
                if (Grid.Contains(c.Cell))
                {
                    Ordered(u, h, false);
                    u.AirMission = null;
                    Assign(u, new Order(OrderKind.AttackMove, c.Cell, 0, 0));
                }
                return;
            case CommandKind.Guard:
                var ward = c.Target != 0 ? Unit(c.Target) : null;
                if ((c.Target != 0 && (ward is null || ward.Owner != c.Player || !ward.Alive || ward == u)) || (ward is null && !Grid.Contains(c.Cell)))
                {
                    return;
                }
                Ordered(u, h, false);
                u.AirMission = null;
                Assign(u, new Order(OrderKind.Guard, ward is not null ? Cell.Of(ward.Pos) : c.Cell, ward?.Id ?? 0, 0));
                h.Post = (ward?.Pos ?? c.Cell.Center());
                return;
            case CommandKind.Attack:
                if (Unit(c.Target) is not { } t || t.Owner == c.Player || t.Def.Archetype.Untargetable)
                {
                    return;
                }
                if (!IsVisibleTo(t, c.Player))
                {
                    Refuse(u, OrderRefusalReason.NotVisible, t.Id, Cell.Of(t.Pos));
                    return;
                }
                if (!HeliCanHit(u, t) || t.Def.Archetype.Flight is not null)
                {
                    Refuse(u, PrimaryWeaponAny(u, t) ? OrderRefusalReason.NoAmmo : OrderRefusalReason.NoWeaponForTarget, t.Id, Cell.Of(t.Pos));
                    return;
                }
                Ordered(u, h, false);
                Assign(u, new Order(OrderKind.Attack, Cell.Of(t.Pos), t.Id, 0));
                return;
            case CommandKind.AttackGround or CommandKind.AttackLine:
                if (!Grid.Contains(c.Cell))
                {
                    return;
                }
                if (HeliGroundWeapon(u) < 0)
                {
                    Refuse(u, HasAnyRound(u) ? OrderRefusalReason.NoWeaponForTarget : OrderRefusalReason.NoAmmo, 0, c.Cell);
                    return;
                }
                Ordered(u, h, false);
                Assign(u, new Order(OrderKind.AttackGround, c.Cell, 0, 0));
                return;
            case CommandKind.Stop:
                Ordered(u, h, false);
                u.Idle();
                u.AirMission = null;
                h.SalvoLeft = 0;
                h.Post = u.Pos;
                if (h.Phase == HeliPhase.Landed)
                {
                    h.Hold = true; // on the ground it stays there
                }
                return;
            case CommandKind.ReturnToBase or CommandKind.Rearm:
                Ordered(u, h, true);
                var to = Unit(c.Target) is { } tu && tu.Owner == c.Player ? tu : null;
                if (HeliGoHome(u, c.Kind == CommandKind.Rearm ? HomeReason.Rearm : HomeReason.Recall, to) && h.Phase is HeliPhase.Flying or HeliPhase.TakingOff)
                {
                    u.Idle(); // it keeps its task (resumed after a Rearm), not its order
                }
                return;
            case CommandKind.Exit:
                if (RidersOf(u).Count > 0)
                {
                    Ordered(u, h, false);
                    (h.Errand, h.LandAt, h.DropHover, h.DropLeft) = (HeliErrand.Drop, LandingSpot(u.Pos), false, 0);
                    u.Idle();
                }
                return;
            case CommandKind.HeliLoad:
                if (u.Def.Archetype.Flight!.Rotor!.Riders is null)
                {
                    return;
                }
                Ordered(u, h, false);
                u.Idle();
                u.AirMission = null;
                var at = Grid.Contains(c.Cell) && c.Cell != default ? c.Cell.Center() : u.Pos;
                (h.Errand, h.LandAt) = (HeliErrand.Board, LandingSpot(at));
                if (h.Phase == HeliPhase.Landed && Within(u.Pos, h.LandAt, Fixed.FromInt(2)))
                {
                    h.LandAt = u.Pos; // already down there (on the ground elsewhere it lifts off and flies there first)
                }
                return;
            case CommandKind.HeliUnload:
                if (RidersOf(u).Count == 0)
                {
                    return;
                }
                Ordered(u, h, false);
                u.Idle();
                var drop = Grid.Contains(c.Cell) && c.Cell != default ? c.Cell.Center() : u.Pos;
                (h.Errand, h.LandAt, h.DropHover, h.DropLeft) = (HeliErrand.Drop, c.On ? drop : LandingSpot(drop), c.On, 0);
                return;
        }
    }

    // PrimaryWeaponAny: helicopter u carries a weapon that could hurt t once rearmed (its stock empty).
    private static bool PrimaryWeaponAny(Unit u, Unit t)
    {
        foreach (var w in u.Def.Weapons)
        {
            if ((w.Effect.Targets & Layers.Ground) != 0 && w.Effect.Interceptor is null && w.Effect.AirGun is null && w.Effect.Delivery is null &&
                w.Warhead.CanHit(t.Def.Armor))
            {
                return true;
            }
        }
        return false;
    }

    // HasAnyRound: some weapon of u has ammunition left.
    private static bool HasAnyRound(Unit u)
    {
        foreach (var w in u.Def.Weapons)
        {
            if (HasRound(u, w))
            {
                return true;
            }
        }
        return false;
    }
}
