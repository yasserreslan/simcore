using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>A shot that landed this tick.</summary>
/// <param name="Weapon">The weapon that fired.</param>
/// <param name="Target">The unit aimed at, or 0 for a shot at a point.</param>
/// <param name="At">Where the shot landed.</param>
internal readonly record struct Hit(WeaponDef Weapon, uint Target, Vec At);

// The combat system: weapons aim and fire, then damage lands. A unit fires
// its best weapon for the target that is in range and has ammo, and only at
// enemies its side spots. Launchers fire salvos at the point the target stood
// on when the salvo began; travelling shots become projectiles.
public sealed partial class World
{
    private const int TurretTurnRate = 2; // facings per tick
    private const int FireAlignFacings = 1; // weapon must be this close to the target to fire

    private void RunCombat() => ApplyHits(Fire());

    // Fire aims weapons and returns the instant shots fired this tick. Damage
    // is applied afterwards, so units that shoot each other on the same tick
    // both fire.
    private List<Hit> Fire()
    {
        var hits = new List<Hit>();
        foreach (var u in _units)
        {
            var def = u.Def;
            if (def.Weapons.Count == 0 || !u.Alive || !u.CanAct)
            {
                continue;
            }
            u.Reload = Math.Max(0, u.Reload - (100 + u.ReloadBonus));
            if (u.SalvoLeft > 0)
            {
                ContinueSalvo(u, hits);
                continue;
            }
            if (u.Order.Kind == OrderKind.AttackGround)
            {
                FireAtGround(u, hits);
                continue;
            }
            var target = Unit(u.Order.Kind == OrderKind.Attack ? u.Order.Target : u.AutoTarget);
            var wi = target is { Alive: true } && IsSpotted(target, u.Owner) ? PickWeapon(u, target, true) : -1;
            if (wi < 0)
            {
                if (def.Turret)
                {
                    u.TurretFacing = Facing.TurnToward(u.TurretFacing, u.Facing, TurretTurnRate);
                }
                continue;
            }

            Facing.TryFromDirection(target!.Pos - u.Pos, out var want);
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

            var w = def.Weapons[wi];
            if (w.Stock >= 0)
            {
                u.MutableCharges[w.Stock]--;
            }
            if (w.Effect.SalvoSize > 1 || w.Effect.ProjectileSpeed.Raw > 0)
            {
                u.SalvoWeapon = wi;
                u.SalvoAim = target.Pos;
                u.SalvoLeft = w.Effect.SalvoSize;
                u.SalvoWait = 0;
                ContinueSalvo(u, hits);
                continue;
            }
            u.Reload = w.Reload * 100;
            hits.Add(new Hit(w, target.Id, Scatter(w, target.Pos)));
        }
        return hits;
    }

    // FireAtGround aims u's ground weapon at the centre of its target cell and
    // fires when in range, aligned and loaded, exactly like a shot at a unit:
    // instant shots land at the point (with scatter; splash hits whoever is
    // there, friends included), salvos and projectiles fly to it.
    private void FireAtGround(Unit u, List<Hit> hits)
    {
        var def = u.Def;
        var wi = GroundWeapon(u, true);
        if (wi < 0)
        {
            if (def.Turret)
            {
                u.TurretFacing = Facing.TurnToward(u.TurretFacing, u.Facing, TurretTurnRate);
            }
            return;
        }
        var point = u.Order.Cell.Center();
        var aligned = !Facing.TryFromDirection(point - u.Pos, out var want); // standing on the point: no need to turn
        int aim;
        if (def.Turret)
        {
            if (!aligned)
            {
                u.TurretFacing = Facing.TurnToward(u.TurretFacing, want, TurretTurnRate);
            }
            aim = u.TurretFacing;
        }
        else
        {
            if (u.Moving)
            {
                return; // fixed weapons can't fire on the move
            }
            if (!aligned)
            {
                u.Facing = Facing.TurnToward(u.Facing, want, def.TurnRate);
            }
            u.TurretFacing = u.Facing;
            aim = u.Facing;
        }
        if (u.Reload > 0 || (!aligned && Math.Abs(Facing.Diff(aim, want)) > FireAlignFacings))
        {
            return;
        }
        var w = def.Weapons[wi];
        if (w.Stock >= 0)
        {
            u.MutableCharges[w.Stock]--;
        }
        if (w.Effect.SalvoSize > 1 || w.Effect.ProjectileSpeed.Raw > 0)
        {
            u.SalvoWeapon = wi;
            u.SalvoAim = point;
            u.SalvoLeft = w.Effect.SalvoSize;
            u.SalvoWait = 0;
            ContinueSalvo(u, hits);
            return;
        }
        u.Reload = w.Reload * 100;
        hits.Add(new Hit(w, 0, Scatter(w, point)));
    }

    // ContinueSalvo fires the salvo's next shot when it is due: a projectile
    // for travelling weapons, otherwise an instant splash at the aim point.
    // The weapon reloads after the last shot.
    private void ContinueSalvo(Unit u, List<Hit> hits)
    {
        if (u.SalvoWait > 0 && --u.SalvoWait > 0)
        {
            return;
        }
        var w = u.Def.Weapons[u.SalvoWeapon];
        var at = Scatter(w, u.SalvoAim);
        if (w.Effect.ProjectileSpeed.Raw > 0)
        {
            _nextId++;
            _projectiles.Add(new Projectile(_nextId, u, w, at));
        }
        else
        {
            hits.Add(new Hit(w, 0, at));
        }
        u.SalvoLeft--;
        u.SalvoWait = w.Effect.SalvoInterval;
        if (u.SalvoLeft == 0)
        {
            u.Reload = w.Reload * 100;
        }
    }

    /// <summary>
    /// Deals damage. A shot without splash hits only its target; a splash
    /// hits everyone in the radius, see <see cref="Explode"/>.
    /// </summary>
    internal void ApplyHits(List<Hit> hits)
    {
        foreach (var h in hits)
        {
            var w = h.Weapon;
            if (w.Effect.Splash.Raw > 0)
            {
                Explode(h.At, w.Damage, w.Warhead, w.Effect.Splash);
            }
            else if (Unit(h.Target) is { Inside: 0 } t)
            {
                t.HP -= w.Warhead.Damage(w.Damage, t.Def.Armor, 100);
            }
        }
    }
}
