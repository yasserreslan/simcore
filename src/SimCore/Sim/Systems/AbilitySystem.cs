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
            RelaunchDue(u); // a strike drone's station sends it up again (the watcher system)
            if (u.Order.Kind != OrderKind.UseAbility || !u.CanAct || u.Cooldown > 0)
            {
                u.PlaceLeft = 0;
                continue;
            }
            var ab = u.Def.Ability(u.Order.Ability);
            var target = u.Order.Target != 0 ? Unit(u.Order.Target) : null;
            // A launch point (the observation post) uses its abilities only with its launcher inside (a sniper).
            var noLauncher = u.Def.Archetype.Garrison is { LauncherTags.IsEmpty: false } && PostLauncher(u) is null;
            if (ab is null || u.Charges[ab.Stock] == 0 || (u.Order.Target != 0 && target is null) || noLauncher)
            {
                u.Idle();
                continue;
            }
            var goal = target?.Pos ?? u.Order.Cell.Center();
            if (!Within(u.Pos, goal, ab.Range))
            {
                u.PlaceLeft = 0;
                continue;
            }
            if (ab.Effect is PlacerEffect { PlaceTicks: > 0 } slow)
            {
                // It stands at the spot for PlaceTicks setting it up (a drone station: 2 s).
                if (u.PlaceLeft == 0)
                {
                    u.PlaceLeft = slow.PlaceTicks;
                    u.ClearPath();
                    continue;
                }
                if (--u.PlaceLeft > 0)
                {
                    continue;
                }
            }

            var def = Player(u.Owner)!.Setup.Defaults[ab.Archetype]!;
            Facing.TryFromDirection(goal - u.Pos, out var facing);
            if (ab.Effect is PlacerEffect p)
            {
                RetireOldest(u, ab.Archetype, p.MaxActive);
                spawns.Add((def, goal, facing, u.Id, default));
            }
            else if (ab.Effect is LaunchEffect { MaxActive: > 0 } le && Aloft(u, ab.Archetype) >= le.MaxActive)
            {
                u.Idle(); // one aloft already (a scout launcher's one drone): nothing launches
                continue;
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
            if (s.Def.Archetype.Flight is { } fl)
            {
                // A scout drone leaves its launcher's catapult and climbs (the scout system).
                n.Altitude = Fixed.FromRaw(Math.Max(1, GroundRaw(s.Pos) + fl.LaunchHeightRaw));
                n.OrbitAt = s.Order.Cell.Center();
                if (Unit(s.Source) is { KeptStocks: { } kept } src && src.KeptArchetype == s.Def.Archetype.Index)
                {
                    // The drone its station recovered without a Rearm order goes up as it came home:
                    // refuelled, with the stocks and HP it had (the owner's rules).
                    for (var i = 0; i < Math.Min(kept.Length, n.MutableCharges.Length); i++)
                    {
                        n.MutableCharges[i] = Math.Min(kept[i], n.MutableCharges[i]);
                    }
                    n.HP = Math.Clamp(src.KeptHp, 1, n.HP);
                    (src.KeptStocks, src.KeptHp) = (null, 0);
                }
            }
        }
        foreach (var u in _units)
        {
            if (u.Def.Archetype.ExpireWhenEmpty > 0 && u.Lifetime == 0 && u.Alive && ScoutLaunchSpent(u))
            {
                u.Lifetime = u.Def.Archetype.ExpireWhenEmpty; // a scout station whose drone was lost goes after a while
            }
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
