using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>How an air strike ended (<see cref="StrikeReport"/>).</summary>
public enum StrikeOutcome : byte
{
    /// <summary>Just launched: the hit roll is made, the missile is on its way.</summary>
    Launched,

    /// <summary>It hit and destroyed its target.</summary>
    Destroyed,

    /// <summary>It hit a heavy target that survived (the heavy roll), or a building, which took its damage.</summary>
    Damaged,

    /// <summary>The launch roll missed: it went off on the ground beside the target.</summary>
    Missed,

    /// <summary>It was meant to hit, but its target was gone (dead, or inside something) when it arrived.</summary>
    Gone,
}

/// <summary>
/// One event of an air strike, for renderers, demos and logs: a launch (with the hit roll) or the
/// impact (with the outcome and, for a heavy target, the second roll). Not game state.
/// </summary>
/// <param name="Missile">The missile's projectile ID.</param>
/// <param name="Shooter">The drone that fired it.</param>
/// <param name="Target">The unit it was fired at.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Moving">Whether the target moved at launch (the lower hit chance).</param>
/// <param name="Chance">The hit chance at launch (percent), or for a heavy target's impact its kill chance.</param>
/// <param name="Roll">The roll, 0 to 99 (below the chance succeeds), or -1 when none was made.</param>
/// <param name="At">Where the missile is (launch) or went off (impact).</param>
public readonly record struct StrikeReport(uint Missile, uint Shooter, uint Target, StrikeOutcome Outcome, bool Moving, int Chance, int Roll, Vec At);

// The watcher system (docs: the game repo's docs/watcher-design.md): a strike drone (a flying unit
// with a flight block and a weapon with an airStrike block, the Sentinel's Watcher) orbits like a
// scout and, on an attack order, fires a missile from its height at an enemy ground unit within its
// weapon's range of the point under it (its sight). The hit roll is made at launch (one draw of the
// world's generator: HitIdle percent against a target that stood still last tick, HitMoving against
// one that moved); the missile then flies a straight 3D line at its speed, homing on the target when
// it hits, else to a point beside it. On impact a hit destroys what the outcome table says it
// destroys (heavy targets on a second draw), or deals its damage (buildings); the splash hurts what
// is around. Empty, the drone flies home on its own (HomeWhenEmpty) and its launcher rearms it.
public sealed partial class World
{
    // A strike missile flies at the target's centre this high above the ground (raw: 1 m).
    private const long StrikeAimHeightRaw = 512;

    private readonly List<StrikeReport> _strikeReports = [];

    /// <summary>The air strikes launched and the ones that went off in the last tick, in order.</summary>
    public IReadOnlyList<StrikeReport> StrikeReports => _strikeReports;

    /// <summary>u's air strike weapon (its first weapon with an airStrike block), or null.</summary>
    public static WeaponDef? StrikeWeapon(Unit u)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Effect.AirStrike is not null)
            {
                return weapons[i];
            }
        }
        return null;
    }

    /// <summary>The strikes u has left (its strike weapon's ammo), or -1 when it carries none.</summary>
    public static int StrikesLeft(Unit u) => StrikeWeapon(u) is { Stock: >= 0 } w ? u.Charges[w.Stock] : -1;

    /// <summary>
    /// Whether strike drone u could fire at t now if ordered: an enemy ground unit its player sees,
    /// that its strike weapon can hit, within the weapon's range of the point under u, with a strike
    /// left and the reload done.
    /// </summary>
    public bool CanStrikeNow(Unit u, Unit t) =>
        StrikeWeapon(u) is { } w && StrikeTarget(u, w, t) && Within(u.Pos, AimPoint(u.Pos, t), w.Range) && u.Reload == 0;

    // StrikeTarget reports whether t is something u's strike weapon w may be ordered at.
    private static bool StrikeTarget(Unit u, WeaponDef w, Unit t) =>
        t.Alive && t.Owner != u.Owner && IsVisibleTo(t, u.Owner) && CanHit(w, t) && HasAmmo(u, w) && !u.Homing;

    // StrikeOrder runs strike drone u's attack order (called by the scout system each tick it flies):
    // the target in reach, it fires and goes back to orbiting where it was; out of reach, it orbits
    // over the target until it is. A target gone, hidden again or out of ammo ends the order.
    private void StrikeOrder(Unit u, WeaponDef w)
    {
        if (u.Reload > 0)
        {
            u.Reload = Math.Max(0, u.Reload - 100);
        }
        if (u.Order.Kind != OrderKind.Attack)
        {
            return;
        }
        var t = Unit(u.Order.Target);
        if (t is null || !StrikeTarget(u, w, t))
        {
            u.Idle();
            return;
        }
        var aim = AimPoint(u.Pos, t);
        if (!Within(u.Pos, aim, w.Range))
        {
            u.OrbitAt = t.Pos; // fly over to it
            return;
        }
        if (u.Reload > 0)
        {
            return;
        }
        FireStrike(u, w, t);
        u.Idle();
    }

    // FireStrike launches u's strike missile at t: one round, the reload, and the hit roll.
    private void FireStrike(Unit u, WeaponDef w, Unit t)
    {
        var st = w.Effect.AirStrike!;
        u.MutableCharges[w.Stock]--;
        u.Reload = w.Reload * 100;
        u.LastShot = Tick;
        var moving = t.Vel != default;
        var chance = moving ? st.HitMoving : st.HitIdle;
        var roll = _rng.Intn(100);
        var hit = roll < chance;
        var aim = t.Pos;
        if (!hit)
        {
            // A miss lands beside it, MissNear to MissFar from where it stood: in a seeded direction,
            // or behind a moving target (within 45 degrees of straight behind it: the missile lagged).
            var f0 = Facing.TryFromDirection(new Vec(-t.Vel.X, -t.Vel.Y), out var back) ? back : _rng.Intn(Facing.Count);
            var dir = Facing.Dir(moving ? f0 + _rng.Intn(9) - 4 : f0);
            var d = st.MissNear.Raw + _rng.Intn((int)(st.MissFar.Raw - st.MissNear.Raw) + 1);
            aim = OnMap(t.Pos.X.Raw + dir.X.Raw * d / Fixed.OneRaw, t.Pos.Y.Raw + dir.Y.Raw * d / Fixed.OneRaw);
        }
        _nextId++;
        var m = new Projectile(_nextId, u, w, aim, t.Id)
        {
            Origin = u.Pos,
            Altitude = Fixed.FromRaw(u.Altitude.Raw - StrikeAimHeightRaw),
            StrikeHit = hit,
            StrikeAim = aim,
            StrikeMoving = moving,
        };
        _projectiles.Add(m);
        _strikeReports.Add(new StrikeReport(m.Id, u.Id, t.Id, StrikeOutcome.Launched, moving, chance, roll, u.Pos));
    }

    // StepStrike moves an air strike missile one tick along a straight 3D line at its speed: at
    // its target while the launch roll hit and the target is out on the map, else at its aim point;
    // reaching it, it goes off.
    private void StepStrike(Projectile p, AirStrike st)
    {
        var t = p.StrikeHit ? Unit(p.Target) : null;
        if (t is { Alive: true, Inside: 0, Garrison: 0 })
        {
            p.StrikeAim = t.Pos;
        }
        else
        {
            t = null;
        }
        var aim = p.StrikeAim;
        var aimAlt = GroundRaw(aim) + StrikeAimHeightRaw;
        long dx = aim.X.Raw - p.Pos.X.Raw, dy = aim.Y.Raw - p.Pos.Y.Raw, dz = aimAlt - p.Altitude.Raw;
        var len = Len3(dx, dy, dz);
        var speed = p.Weapon.Effect.ProjectileSpeed.Raw;
        if (len <= speed)
        {
            p.Vel = new Vec(Fixed.FromRaw(dx), Fixed.FromRaw(dy));
            p.VelZ = Fixed.FromRaw(dz);
            p.Altitude = Fixed.FromRaw(aimAlt);
            StrikeImpact(p, st, aim, t);
            return;
        }
        p.Vel = new Vec(Fixed.FromRaw(dx * speed / len), Fixed.FromRaw(dy * speed / len));
        p.VelZ = Fixed.FromRaw(dz * speed / len);
        p.Pos = OnMap(p.Pos.X.Raw + p.Vel.X.Raw, p.Pos.Y.Raw + p.Vel.Y.Raw);
        p.Altitude = Fixed.FromRaw(p.Altitude.Raw + p.VelZ.Raw);
    }

    // StrikeImpact sets air strike missile p off at `at`, on target t (null for the ground): the
    // outcome table on a hit, then the splash around the point.
    private void StrikeImpact(Projectile p, AirStrike st, Vec at, Unit? t)
    {
        p.Pos = at;
        p.Done = true;
        var w = p.Weapon;
        _hurtBy = Unit(p.Shooter); // for Return fire and retaliation (Hurt)
        var outcome = p.StrikeHit ? StrikeOutcome.Gone : StrikeOutcome.Missed;
        int chance = 0, roll = -1;
        if (t is not null)
        {
            if (!st.HeavyTags.IsEmpty && t.Def.Tags.Matches(st.HeavyTags))
            {
                chance = st.HeavyKill;
                roll = _rng.Intn(100);
                if (roll < chance)
                {
                    t.HP = 0;
                }
                else
                {
                    t.HP = Math.Min(t.HP, Math.Max(1, (int)((long)t.Def.MaxHp * st.HeavyHpLeft / 100)));
                }
            }
            else if (!st.Kills.IsEmpty && t.Def.Tags.Matches(st.Kills))
            {
                t.HP = 0;
            }
            else if (w.Warhead.CanHit(ArmorOf(t)))
            {
                Hurt(t, w.Warhead, w.Damage, 100);
            }
            outcome = t.Alive ? StrikeOutcome.Damaged : StrikeOutcome.Destroyed;
        }
        if (w.Effect.Splash.Raw > 0)
        {
            Explode(at, w.Damage, w.Warhead, w.Effect.Splash, t?.Id ?? 0, w.Effect.SplashCenter, w.Effect.SplashEdge, w.Effect.SplashInner, w.Effect.SplashOuterPercent);
        }
        _hurtBy = null;
        _detonations.Add(new Detonation(p.Id, at, t?.Id ?? 0, false));
        RunwayBlast(at, w.Effect.Runway);
        _strikeReports.Add(new StrikeReport(p.Id, p.Shooter, p.Target, outcome, p.StrikeMoving, chance, roll, at));
    }

    // RelaunchDue sends a fresh drone up from launcher u (a station whose drone came home with
    // Relaunch) once its turnaround is over and it has no other order: the same kind of drone
    // (RelaunchArchetype), back to where its drone orbited, through the ordinary launch (the ability
    // system, this tick).
    private void RelaunchDue(Unit u)
    {
        if (!u.RelaunchPending || u.Cooldown > 0 || u.Order.Kind != OrderKind.Idle || !u.CanAct)
        {
            return;
        }
        u.RelaunchPending = false;
        foreach (var ab in u.Def.Abilities)
        {
            if (ab.Effect is LaunchEffect l && l.Archetype == u.RelaunchArchetype && Content.Archetypes[l.Archetype].Flight is { Relaunch: true } &&
                u.Charges[ab.Stock] > 0)
            {
                u.Order = new Order(OrderKind.UseAbility, u.RelaunchCell, 0, ab.Component);
                return;
            }
        }
    }
}
