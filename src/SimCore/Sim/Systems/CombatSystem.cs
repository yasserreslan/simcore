using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>A shot that landed this tick.</summary>
/// <param name="Weapon">The weapon that fired.</param>
/// <param name="Target">The unit aimed at, or 0 for a shot at a point.</param>
/// <param name="At">Where the shot landed.</param>
/// <param name="Shooter">The unit that fired it.</param>
internal readonly record struct Hit(WeaponDef Weapon, uint Target, Vec At, uint Shooter);

// The combat system: weapons aim and fire, then damage lands. A unit fires
// its best weapon for the target that is in range and has ammo, and only at
// enemies its side spots. Launchers fire salvos at the point the target stood
// on when the salvo began; travelling shots become projectiles.
public sealed partial class World
{
    private const int TurretTurnRate = 2; // facings per tick
    private const int FireAlignFacings = 1; // weapon must be this close to the target to fire

    /// <summary>The <see cref="Unit.AimAt"/> of a unit aiming at a point on the ground (attack ground).</summary>
    public const uint GroundAim = uint.MaxValue;

    private readonly List<Hit> _hits = []; // scratch, reused every tick

    // The unit whose shot is dealing damage right now (looked up once per hit, for every unit its
    // splash hurts), for Return fire and retaliation (Hurt); scratch, not state.
    private Unit? _hurtBy;

    // RunCombat builds the spatial index for the combat, projectile, strike
    // and trigger systems (none of them moves a unit), runs vehicles over infantry (crushing), then
    // fires and deals damage.
    private void RunCombat()
    {
        BuildIndex();
        RunCrush();
        ApplyHits(Fire());
        LaunchKamans(); // the Kaman drones its launchers fired (KamanSystem.cs)
        RunHeliCombat(); // the helicopters' guns, rockets and missiles (HeliCombatSystem.cs)
    }

    // Fire aims weapons and returns the instant shots fired this tick. Damage
    // is applied afterwards, so units that shoot each other on the same tick
    // both fire.
    private List<Hit> Fire()
    {
        var hits = _hits;
        hits.Clear();
        foreach (var u in _units)
        {
            var def = u.Def;
            if (def.Weapons.Count == 0 || !u.Alive || !u.CanAct || def.Archetype.Flight is not null)
            {
                continue; // a strike drone fires from the watcher system
            }
            u.Reload = Math.Max(0, u.Reload - (100 + u.ReloadBonus));
            if (def.HasCoaxial)
            {
                u.CoaxReload = Math.Max(0, u.CoaxReload - (100 + u.ReloadBonus)); // its own clock (a tank's machine guns)
            }
            if (def.Archetype.DeployTicks > 0 && u.Deploy != DeployState.Deployed)
            {
                continue; // a launcher that deploys fires only when set up
            }
            if (u.SalvoLeft > 0)
            {
                ContinueSalvo(u, hits);
                continue;
            }
            if (u.Order.Kind == OrderKind.AttackGround)
            {
                if (u.Moving && u.AimAt != 0)
                {
                    u.AimAt = 0; // walking into range: the aim starts again once it stands
                    u.AimLeft = 0;
                }
                FireAtGround(u, hits);
                continue;
            }
            // An ordered attack fires at any target the player sees on screen, while units picking
            // targets on their own need a spotter. With fog of war (MatchSetup.Fog) IsVisibleTo
            // means "spotted by your side", so ordered attacks need a spotter too.
            var ordered = u.Order.Kind == OrderKind.Attack;
            var target = Unit(ordered ? u.Order.Target : u.AutoTarget);
            var wi = target is { Alive: true } && (ordered ? IsVisibleTo(target, u.Owner) : IsSpotted(target, u.Owner))
                ? PickWeapon(u, target, true)
                : -1;
            if (u.AimAt != 0 && (wi < 0 || target!.Id != u.AimAt || u.Moving))
            {
                u.AimAt = 0; // the aim is lost: another target, out of reach, or on the move
                u.AimLeft = 0;
            }
            if (wi < 0)
            {
                if (def.Turret && u.AirTarget == 0 && def.Archetype.TraverseTicks == 0) // an air-defence turret stays on its air track
                {
                    u.TurretFacing = Facing.TurnToward(u.TurretFacing, u.Facing, TurretTurnRate); // (a slow gun stays laid where it is)
                }
                continue;
            }

            Facing.TryFromDirection(target!.Pos - u.Pos, out var want);
            int aim;
            if (def.Turret)
            {
                TurnTurret(u, want);
                aim = u.TurretFacing;
            }
            else
            {
                if (u.Moving)
                {
                    continue; // fixed weapons can't fire on the move
                }
                aim = AimHull(u, want);
            }
            if (def.HasCoaxial && Math.Abs(Facing.Diff(aim, want)) <= FireAlignFacings)
            {
                FireCoaxial(u, target, hits); // a tank's machine guns at its target, alongside the main gun
                if (def.Weapons[wi].Effect.Coaxial)
                {
                    continue; // the main gun can't fire at it (empty, or out of its reach): the machine guns only
                }
            }
            if (u.Reload > 0 || Math.Abs(Facing.Diff(aim, want)) > FireAlignFacings)
            {
                continue;
            }

            var w = def.Weapons[wi];
            if (w.Effect.AimTicks > 0)
            {
                // Aim first, standing still, before every shot; none while guiding a missile.
                if (u.Guiding != 0)
                {
                    continue;
                }
                if (u.AimAt != target.Id)
                {
                    u.AimAt = target.Id;
                    u.AimLeft = w.Effect.AimTicks;
                }
                if (u.AimLeft > 0)
                {
                    u.AimLeft--;
                    continue;
                }
                u.AimAt = 0;
            }
            if (w.Stock >= 0 && !w.Effect.AmmoPerRocket)
            {
                u.MutableCharges[w.Stock]--;
            }
            if (w.Effect.SalvoSize > 1 || w.Effect.ProjectileSpeed.Raw > 0 || w.Effect.Launches >= 0)
            {
                u.SalvoWeapon = wi;
                u.SalvoAim = w.Effect.Guided is not null ? target.Pos : Lead(u, target, w);
                u.SalvoTarget = target.Id;
                u.SalvoLeft = w.Effect.SalvoSize;
                u.SalvoWait = 0;
                ContinueSalvo(u, hits);
                continue;
            }
            u.Reload = w.Reload * 100;
            u.LastShot = Tick;
            var hitAt = Scatter(w, target.Pos, u);
            hits.Add(new Hit(w, target.Id, hitAt, u.Id));
            NoteShot(u, w, target.Id, hitAt, 0);
        }
        return hits;
    }

    // AimHull aims a unit without a turret at facing want and returns the facing its weapon points along: the hull turns at its
    // turn rate (a gun on the hull, the light rocket trucks); a set-up hull-aiming launcher (hullAims) stays as it stands, its
    // jacks down, and fires at what lies within its arc (the deploy system turned it before it set up and packs it up when the
    // target leaves its arc); a vertical launcher never turns: it points wherever the target is.
    private static int AimHull(Unit u, int want)
    {
        var a = u.Def.Archetype;
        if (a.VerticalLaunch || (a.HullAims && Math.Abs(Facing.Diff(u.Facing, want)) <= a.HullArc))
        {
            return want;
        }
        if (!a.HullAims)
        {
            u.Facing = Facing.TurnToward(u.Facing, want, u.Def.TurnRate);
        }
        u.TurretFacing = u.Facing;
        return u.Facing;
    }

    // TurnTurret turns u's turret toward facing want: two facings a tick, or a slow turret's one facing every traverseTicks ticks
    // (the howitzer), on its ID's phase.
    private void TurnTurret(Unit u, int want)
    {
        var slow = u.Def.Archetype.TraverseTicks;
        if (slow == 0)
        {
            u.TurretFacing = Facing.TurnToward(u.TurretFacing, want, TurretTurnRate);
        }
        else if ((Tick + u.Id) % (uint)slow == 0)
        {
            u.TurretFacing = Facing.TurnToward(u.TurretFacing, want, 1);
        }
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
            if (def.Turret && def.Archetype.TraverseTicks == 0)
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
                TurnTurret(u, want);
            }
            aim = u.TurretFacing;
        }
        else
        {
            if (u.Moving)
            {
                return; // fixed weapons can't fire on the move
            }
            aim = aligned ? u.Facing : AimHull(u, want);
            u.TurretFacing = u.Facing;
        }
        if (u.Reload > 0 || (!aligned && Math.Abs(Facing.Diff(aim, want)) > FireAlignFacings))
        {
            return;
        }
        var w = def.Weapons[wi];
        if (w.Effect.AimTicks > 0)
        {
            // The same aim before every shot as at a unit (the point stands for the target), and
            // none while guiding a missile.
            if (u.Guiding != 0)
            {
                return;
            }
            if (u.AimAt != GroundAim)
            {
                u.AimAt = GroundAim;
                u.AimLeft = w.Effect.AimTicks;
            }
            if (u.AimLeft > 0)
            {
                u.AimLeft--;
                return;
            }
            u.AimAt = 0;
        }
        if (w.Stock >= 0 && !w.Effect.AmmoPerRocket)
        {
            u.MutableCharges[w.Stock]--;
        }
        if (w.Effect.SalvoSize > 1 || w.Effect.ProjectileSpeed.Raw > 0 || w.Effect.Launches >= 0)
        {
            u.SalvoWeapon = wi;
            u.SalvoAim = point;
            u.SalvoTarget = 0;
            u.SalvoLeft = w.Effect.SalvoSize;
            u.SalvoWait = 0;
            ContinueSalvo(u, hits);
            return;
        }
        u.Reload = w.Reload * 100;
        u.LastShot = Tick;
        var at = Scatter(w, point, u);
        hits.Add(new Hit(w, 0, at, u.Id));
        NoteShot(u, w, 0, at, 0);
        NextGroundPoint(u);
    }

    // NextGroundPoint moves a ground unit on an attack-ground order with a Shift queue of points on to the next one once a salvo
    // is done (the point it just fired at goes to the back): one salvo at each point in turn, round and round.
    private static void NextGroundPoint(Unit u)
    {
        if (u.SalvoLeft > 0 || u.Order.Kind != OrderKind.AttackGround || u.GroundQueue.Count == 0)
        {
            return;
        }
        var q = u.MutableGroundQueue;
        q.Add(u.Order.Cell);
        u.Order = u.Order with { Cell = q[0] };
        q.RemoveAt(0);
        u.AimAt = 0;
        u.AimLeft = 0;
    }

    // FireCoaxial fires u's coaxial weapons (a tank's machine guns) at target on their own reload clock: each that can hit it,
    // reaches it and has a line of fire, its turret on the target (the caller checked). Instant shots, like any gun's.
    private void FireCoaxial(Unit u, Unit target, List<Hit> hits)
    {
        if (u.CoaxReload > 0)
        {
            return;
        }
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            var at = AimPoint(u.Pos, target);
            if (!w.Effect.Coaxial || !CanHit(w, target) || !HasAmmo(u, w) || !InReach(u, at, w) || TooClose(u.Pos, at, w) ||
                !LineOfFire(u, at, w, target))
            {
                continue;
            }
            if (w.Stock >= 0)
            {
                u.MutableCharges[w.Stock]--;
            }
            u.CoaxReload = w.Reload * 100;
            u.CoaxShot = Tick;
            hits.Add(new Hit(w, target.Id, Scatter(w, target.Pos, u), u.Id));
            return; // one coaxial weapon a shot
        }
    }

    // Empty reports whether every weapon stock of u is spent.
    private static bool Empty(Unit u)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if (w.Stock >= 0 && u.Charges[w.Stock] > 0)
            {
                return false;
            }
        }
        return true;
    }

    // Lead aims a travelling shot where a moving target will be when it lands: its velocity
    // (toward its next path cell at its speed) times the flight time to where it stands now.
    // A target that turns or stops still dodges. Instant weapons aim at the target itself.
    internal static Vec Lead(Unit u, Unit t, WeaponDef w)
    {
        var speed = w.Effect.ProjectileSpeed.Raw;
        if (speed == 0 || Velocity(t) is not { } v)
        {
            return t.Pos;
        }
        var ticks = (t.Pos - u.Pos).Len().Raw / speed;
        return t.Pos + new Vec(Fixed.FromRaw(v.X.Raw * ticks), Fixed.FromRaw(v.Y.Raw * ticks));
    }

    // Velocity is a ground unit's movement per tick along its path, or null when it stands still.
    internal static Vec? Velocity(Unit t)
    {
        if (!t.Moving || t.Def.Speed.Raw == 0 || t.Def.Layer != Layers.Ground || !t.CanAct)
        {
            return null;
        }
        var d = t.Path[t.PathIdx].Center() - t.Pos;
        return d.Len().Raw == 0 ? null : d.WithLen(t.Def.Speed);
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
        if (w.Effect.AmmoPerRocket && w.Stock >= 0)
        {
            if (u.Charges[w.Stock] == 0)
            {
                u.SalvoLeft = 0; // out of rockets: the salvo ends early
                u.Reload = w.Reload * 100;
                return;
            }
            u.MutableCharges[w.Stock]--;
        }
        u.LastShot = Tick;
        var at = Scatter(w, u.SalvoAim, u);
        if (w.Effect.ProjectileSpeed.Raw > 0)
        {
            _nextId++;
            var shot = new Projectile(_nextId, u, w, at, u.SalvoTarget);
            if (w.Effect.Terminal is { } tf)
            {
                LaunchTerminal(shot, u, tf); // its climb, arc and dive (TerminalSystem.cs)
            }
            else if (w.Effect.Cruise is not null)
            {
                LaunchCruise(shot, u, TakeCruiseRoute(u, at)); // its route and flight, planned now (CruiseSystem.cs)
            }
            else
            {
                Launch(shot, u); // its arc over the terrain (maps with terrain)
            }
            _projectiles.Add(shot);
            if (w.Effect.Guided is not null)
            {
                u.Guiding = _nextId; // it reloads once it stops guiding (EndGuidance)
            }
            NoteShot(u, w, u.SalvoTarget, at, _nextId);
        }
        else if (w.Effect.Launches >= 0)
        {
            _kamanLaunches.Add((u, w, u.SalvoTarget, at)); // the round is a unit: launched once the fire loop is over
        }
        else
        {
            hits.Add(new Hit(w, 0, at, u.Id));
            NoteShot(u, w, u.SalvoTarget, at, 0);
        }
        u.SalvoLeft--;
        u.SalvoWait = w.Effect.SalvoInterval;
        if (u.SalvoLeft == 0 && u.Guiding == 0)
        {
            u.Reload = w.Reload * 100;
        }
        if (w.Effect.Launches >= 0 && u.Order.Kind == OrderKind.Attack)
        {
            u.Idle(); // a Kaman launcher: one drone per target
        }
        if ((w.Effect.Drone is not null || w.Effect.Launches >= 0) && u.Order.Kind == OrderKind.AttackGround)
        {
            // One drone per designated point: on to the second point if one is set, else done.
            if (u.HasNextGround)
            {
                u.Order = new Order(OrderKind.AttackGround, u.NextGround, 0, 0);
                (u.HasNextGround, u.NextGround) = (false, default);
            }
            else
            {
                u.Idle();
            }
        }
        if (w.Effect.Cruise is not null)
        {
            NextCruise(u); // a cruise truck: on to its next route, or done (CruiseSystem.cs)
        }
        else if (w.Effect.Drone is null && w.Effect.Launches < 0)
        {
            NextGroundPoint(u); // a Shift queue of ground points: on to the next once the salvo is done
        }
        if (u.Def.Archetype.ExpireWhenEmpty > 0 && u.Lifetime == 0 && Empty(u))
        {
            u.Lifetime = u.Def.Archetype.ExpireWhenEmpty; // a spent single-use launcher goes after a while
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
            _hurtBy = Unit(h.Shooter); // for Return fire and retaliation (Hurt)
            RunwayBlast(h.At, w.Effect.Runway);
            if (w.Effect.Splash.Raw > 0)
            {
                Explode(h.At, w.Damage, w.Warhead, w.Effect.Splash, 0, w.Effect.SplashCenter, w.Effect.SplashEdge, w.Effect.SplashInner, w.Effect.SplashOuterPercent);
            }
            else if (Unit(h.Target) is { Inside: 0, Garrison: 0 } t)
            {
                Hurt(t, w.Warhead, w.Damage, 100);
            }
        }
        _hurtBy = null;
    }
}
