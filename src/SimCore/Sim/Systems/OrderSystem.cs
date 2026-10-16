using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The order system: each unit updates its order, plans its path toward
// whatever it must reach, and picks a target to shoot.
public sealed partial class World
{
    private const uint RepathTicks = TicksPerSecond; // chasing units re-plan once a second

    private void RunOrders()
    {
        foreach (var u in _units)
        {
            Think(u);
        }
    }

    // Think updates a unit's order and target.
    private void Think(Unit u)
    {
        if (u.Inside != 0)
        {
            return;
        }
        if (u.Def.Strike is not null)
        {
            Guide(u);
            return;
        }
        if (u.Garrison != 0)
        {
            Hold(u);
        }
        switch (u.Order.Kind)
        {
            case OrderKind.Move:
                var arrived = u.Def.Layer == Layers.Air ? u.Pos == u.Order.Cell.Center() : !u.Moving;
                if (arrived)
                {
                    u.Idle();
                }
                break;
            case OrderKind.Attack:
                var target = Unit(u.Order.Target);
                var w = target is null || !IsVisibleTo(target, u.Owner) ? -1 : PickWeapon(u, target, false);
                if (w < 0)
                {
                    u.Idle(); // gone, hidden again or out of ammo
                    break;
                }
                Approach(u, AimPoint(u.Pos, target!), Reach(u, u.Def.Weapons[w]));
                break;
            case OrderKind.Enter:
                var v = Unit(u.Order.Target);
                if (v?.Def.Archetype.Garrison is { } g)
                {
                    if (!CanGarrison(u, v))
                    {
                        u.Idle(); // full, taken by the enemy, or gone
                        break;
                    }
                    Approach(u, DoorCell(v, NearestDoor(v, u.Pos)).Center(), g.EnterRange);
                    break;
                }
                if (v is null || !CanBoard(u, v))
                {
                    u.Idle();
                    break;
                }
                Approach(u, v.Pos, v.Def.Crew!.EnterRange);
                break;
            case OrderKind.UseAbility:
                var ab = u.Def.Ability(u.Order.Ability);
                Unit? t = null;
                if (ab is null || u.Charges[ab.Stock] == 0 ||
                    (u.Order.Target != 0 && ((t = Unit(u.Order.Target)) is null || !IsVisibleTo(t, u.Owner))))
                {
                    u.Idle();
                    break;
                }
                Approach(u, t?.Pos ?? u.Order.Cell.Center(), ab.Range);
                break;
            case OrderKind.AttackGround:
                var gw = GroundWeapon(u, false);
                if (gw < 0)
                {
                    u.Idle(); // out of ammo for every weapon that can hit the ground
                    break;
                }
                Approach(u, u.Order.Cell.Center(), Reach(u, u.Def.Weapons[gw]));
                break;
            case OrderKind.Idle:
                break;
        }

        if (u.Order.Kind is OrderKind.Attack or OrderKind.AttackGround || u.Def.Weapons.Count == 0 || !u.CanAct)
        {
            u.AutoTarget = 0;
            return;
        }
        // Keep the current auto target while it stays valid, so fire doesn't
        // flicker between enemies.
        if (Unit(u.AutoTarget) is { } cur && CanAutoTarget(u, cur))
        {
            return;
        }
        u.AutoTarget = NearestTarget(u);
    }

    // Approach stops a unit once goal is within r, and otherwise keeps a
    // path toward it, re-planned once a second as the goal moves.
    private void Approach(Unit u, Vec goal, Fixed r)
    {
        if (Within(u.Pos, goal, r) || u.Garrison != 0)
        {
            u.ClearPath(); // a soldier in a building never walks
            return;
        }
        if (u.Def.Speed.Raw > 0 && u.CanAct && u.Def.Layer == Layers.Ground && Tick >= u.RepathAt)
        {
            SetPath(u, Cell.Of(goal));
            u.RepathAt = Tick + RepathTicks;
        }
    }

    // Hold drops the orders a garrisoned soldier can't carry out from inside:
    // moving, entering, abilities, and attacks on targets out of reach.
    private void Hold(Unit u)
    {
        var keep = u.Order.Kind switch
        {
            OrderKind.Idle => true,
            OrderKind.Attack => Unit(u.Order.Target) is { } t && IsVisibleTo(t, u.Owner) && PickWeapon(u, t, true) >= 0,
            OrderKind.AttackGround => GroundWeapon(u, true) >= 0,
            _ => false,
        };
        if (!keep)
        {
            u.Idle();
        }
    }

    // Guide keeps a launched drone's goal current: it follows its target
    // unit, and flies on to the last cell it saw it in once the target is gone.
    private void Guide(Unit u)
    {
        if (u.Order.Kind != OrderKind.Attack)
        {
            return;
        }
        if (Unit(u.Order.Target) is { Inside: 0 } t)
        {
            u.Order = u.Order with { Cell = Cell.Of(t.Pos) };
        }
        else
        {
            u.Order = u.Order with { Kind = OrderKind.Move, Target = 0 };
        }
    }

    // CanAutoTarget reports whether u may keep shooting t without orders.
    // Launchers that deploy (rocket trucks) never pick targets themselves: they fire on orders only.
    private static bool CanAutoTarget(Unit u, Unit t)
    {
        if (u.Def.Archetype.DeployTicks > 0 || t.Owner == u.Owner || t.Owner == 0 || !t.Alive || !IsSpotted(t, u.Owner))
        {
            return false;
        }
        var wi = PickWeapon(u, t, true);
        if (wi < 0)
        {
            return false;
        }
        var e = u.Def.Weapons[wi].Effect;
        if (e.MaxMisses > 0 && u.MissTarget == t.Id && u.MissStreak >= e.MaxMisses)
        {
            return false; // it kept missing this one: leave it
        }
        if (Restricted(e, t) is { } limit)
        {
            if (!Within(From(u, t.Pos), AimPoint(u.Pos, t), limit + u.RangeBonus))
            {
                return false;
            }
            if (e.AutoSkipRetreating && Velocity(t) is { } v && v.X.Raw * (t.Pos.X.Raw - u.Pos.X.Raw) + v.Y.Raw * (t.Pos.Y.Raw - u.Pos.Y.Raw) > 0)
            {
                return false; // moving away
            }
        }
        return true;
    }

    // Restricted returns the auto-acquire distance limit of weapon e against t's armor class, or null.
    private static Fixed? Restricted(WeaponEffect e, Unit t) =>
        t.Def.Armor < e.AutoRange.Count && e.AutoRange[t.Def.Armor].Raw > 0 ? e.AutoRange[t.Def.Armor] : null;

    // Lesser reports whether t is a target u engages only when nothing better is around.
    private static bool Lesser(Unit u, Unit t) =>
        PickWeapon(u, t, true) is var wi and >= 0 && Restricted(u.Def.Weapons[wi].Effect, t) is not null;

    // NearestTarget returns the closest spotted enemy that one of u's
    // weapons can hit from where u stands, ties going to the lowest ID, or 0.
    private uint NearestTarget(Unit u)
    {
        uint best = 0;
        long bestD = 0;
        foreach (var o in _units)
        {
            if (!CanAutoTarget(u, o))
            {
                continue;
            }
            // Restricted targets (infantry for a rocket) come after everything else, then nearest first.
            var d = (o.Pos - u.Pos).LenSq() + (Lesser(u, o) ? long.MaxValue / 2 : 0);
            if (best == 0 || d < bestD)
            {
                best = o.Id;
                bestD = d;
            }
        }
        return best;
    }
}
