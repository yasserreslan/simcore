using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The ability system: units on a UseAbility order that are in range place a
// trap on the target cell, or launch a drone at the target unit or cell.
// Each use spends a charge; supply restocks them.
public sealed partial class World
{
    private void RunAbilities()
    {
        var spawns = new List<(UnitDef Def, Vec Pos, int Facing, uint Source, Order Order)>();
        foreach (var u in _units)
        {
            if (u.Cooldown > 0)
            {
                u.Cooldown--;
            }
            if (u.Order.Kind != OrderKind.UseAbility || !u.CanAct || u.Cooldown > 0)
            {
                continue;
            }
            var ab = u.Def.Ability(u.Order.Ability);
            var target = u.Order.Target != 0 ? Unit(u.Order.Target) : null;
            if (ab is null || u.Charges[ab.Stock] == 0 || (u.Order.Target != 0 && target is null))
            {
                u.Idle();
                continue;
            }
            var goal = target?.Pos ?? u.Order.Cell.Center();
            if (!Within(u.Pos, goal, ab.Range))
            {
                continue;
            }

            var def = Player(u.Owner)!.Setup.Defaults[ab.Archetype]!;
            Facing.TryFromDirection(goal - u.Pos, out var facing);
            if (ab.Effect is PlacerEffect p)
            {
                RetireOldest(u, ab.Archetype, p.MaxActive);
                spawns.Add((def, goal, facing, u.Id, default));
            }
            else
            {
                var order = target is not null
                    ? new Order(OrderKind.Attack, Cell.Of(target.Pos), target.Id, 0)
                    : new Order(OrderKind.Move, u.Order.Cell, 0, 0);
                spawns.Add((def, u.Pos, facing, u.Id, order));
            }
            u.MutableCharges[ab.Stock]--;
            u.Cooldown = ab.Cooldown;
            u.Idle();
        }
        foreach (var s in spawns)
        {
            var n = Add(s.Def, s.Pos, s.Facing);
            n.Source = s.Source;
            n.Order = s.Order;
        }
    }

    // RetireOldest removes the oldest entities of archetype that u placed
    // until it has room for one more under max.
    private void RetireOldest(Unit u, int archetype, int max)
    {
        var n = 0;
        foreach (var o in _units)
        {
            if (o.Alive && o.Source == u.Id && o.Def.Archetype.Index == archetype)
            {
                n++;
            }
        }
        foreach (var o in _units)
        {
            if (n < max)
            {
                return;
            }
            if (o.Alive && o.Source == u.Id && o.Def.Archetype.Index == archetype)
            {
                o.HP = 0;
                n--;
            }
        }
    }
}
