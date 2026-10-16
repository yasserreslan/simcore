using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>A shot that landed this tick.</summary>
/// <param name="Weapon">The weapon that fired.</param>
/// <param name="Target">The unit aimed at.</param>
/// <param name="At">Where the shot landed.</param>
internal readonly record struct Hit(Weapon Weapon, uint Target, Vec At);

public sealed partial class World
{
    private const uint RepathTicks = TicksPerSecond; // chasing units re-plan once a second
    private const int TurretTurnRate = 2; // facings per tick
    private const int MoveAlignFacings = 2; // hull must be this close to the path direction to drive
    private const int FireAlignFacings = 1; // weapon must be this close to the target to fire
    private const long SplashEdgePercent = 50; // damage at the edge of a splash, in percent

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

    // Move drives a unit along its path. Vehicles turn in place until roughly
    // facing the next cell.
    private static void Move(Unit u)
    {
        var def = u.Def;
        if (def.Speed.Raw == 0 || !u.Moving)
        {
            return;
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

    // Fire aims weapons and returns the shots fired this tick. Damage is
    // applied afterwards, so units that shoot each other on the same tick
    // both fire.
    private List<Hit> Fire()
    {
        var hits = new List<Hit>();
        foreach (var u in _units)
        {
            var def = u.Def;
            var wpn = def.Weapon;
            if (wpn is null)
            {
                continue;
            }
            if (u.Reload > 0)
            {
                u.Reload--;
            }
            var id = u.Order.Kind == OrderKind.Attack ? u.Order.Target : u.AutoTarget;
            var target = Unit(id);
            if (target is null || !InRange(u, target))
            {
                if (def.Turret)
                {
                    u.TurretFacing = Facing.TurnToward(u.TurretFacing, u.Facing, TurretTurnRate);
                }
                continue;
            }

            Facing.TryFromDirection(target.Pos - u.Pos, out var want);
            int aim;
            if (def.Turret)
            {
                u.TurretFacing = Facing.TurnToward(u.TurretFacing, want, TurretTurnRate);
                aim = u.TurretFacing;
            }
            else
            {
                if (u.Moving)
                {
                    continue; // fixed weapons can't fire on the move
                }
                u.Facing = Facing.TurnToward(u.Facing, want, def.TurnRate);
                u.TurretFacing = u.Facing;
                aim = u.Facing;
            }
            if (u.Reload > 0 || Math.Abs(Facing.Diff(aim, want)) > FireAlignFacings)
            {
                continue;
            }

            u.Reload = wpn.ReloadTicks;
            var at = target.Pos;
            if (wpn.Scatter.Raw > 0)
            {
                var s = (int)wpn.Scatter.Raw;
                var dx = _rng.Range(-s, s); // x is drawn before y
                var dy = _rng.Range(-s, s);
                at += new Vec(Fixed.FromRaw(dx), Fixed.FromRaw(dy));
            }
            hits.Add(new Hit(wpn, target.Id, at));
        }
        return hits;
    }

    /// <summary>
    /// Deals damage. Splash hits everyone in the radius, friends included,
    /// falling off to <see cref="SplashEdgePercent"/> at the edge.
    /// </summary>
    internal void ApplyHits(List<Hit> hits)
    {
        foreach (var h in hits)
        {
            if (h.Weapon.Splash.Raw == 0)
            {
                if (Unit(h.Target) is { } t)
                {
                    t.HP -= UnitTypes.Damage(h.Weapon, t.Def.Armor, 100);
                }
                continue;
            }
            var r = h.Weapon.Splash.Raw;
            foreach (var u in _units)
            {
                var d = (u.Pos - h.At).Len().Raw;
                if (d > r)
                {
                    continue;
                }
                var pct = 100 - (100 - SplashEdgePercent) * d / r;
                u.HP -= UnitTypes.Damage(h.Weapon, u.Def.Armor, pct);
            }
        }
    }
}
