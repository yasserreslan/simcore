namespace SimCore.Sim;

// The order system: each unit updates its order and picks a target.
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
        var def = u.Def;
        switch (u.Order.Kind)
        {
            case OrderKind.Move:
                if (!u.Moving)
                {
                    u.Order = default;
                    u.ClearPath();
                }
                break;
            case OrderKind.Attack:
                var target = Unit(u.Order.Target);
                if (target is null)
                {
                    u.Order = default;
                    u.ClearPath();
                    break;
                }
                if (InRange(u, target))
                {
                    u.ClearPath();
                    break;
                }
                if (def.Speed.Raw > 0 && (!u.Moving || Tick >= u.RepathAt))
                {
                    SetPath(u, Cell.Of(target.Pos));
                    u.RepathAt = Tick + RepathTicks;
                }
                break;
            case OrderKind.Idle:
                break;
        }

        if (u.Order.Kind == OrderKind.Attack || def.Weapon is null)
        {
            u.AutoTarget = 0;
            return;
        }
        // Keep the current auto target while it stays in range, so fire
        // doesn't flicker between enemies.
        if (Unit(u.AutoTarget) is { } t && InRange(u, t))
        {
            return;
        }
        u.AutoTarget = NearestEnemyInRange(u);
    }

    // NearestEnemyInRange returns the closest enemy in weapon range, ties
    // going to the lowest ID, or 0.
    private uint NearestEnemyInRange(Unit u)
    {
        var r = u.Def.Weapon!.Range.Raw;
        uint best = 0;
        long bestD = 0;
        foreach (var o in _units)
        {
            if (o.Owner == u.Owner || o.Owner == 0)
            {
                continue;
            }
            var d = (o.Pos - u.Pos).LenSq();
            if (d > r * r)
            {
                continue;
            }
            if (best == 0 || d < bestD)
            {
                best = o.Id;
                bestD = d;
            }
        }
        return best;
    }

    private static bool InRange(Unit u, Unit target)
    {
        var r = u.Def.Weapon!.Range.Raw;
        return (target.Pos - u.Pos).LenSq() <= r * r;
    }
}
