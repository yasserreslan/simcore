using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// The air defence system (docs: the game repo's docs/air-defense-design.md). Units with an
// interceptor weapon (the AA soldier's missile) detect enemy air targets of its classes (one-way
// attack drones) within their own air vision, pick one, turn to it and track it; once locked
// (LockTicks of tracking), loaded and in range they fire a 3D guided missile at it. The missile
// (StepInterceptor, in the projectile system) chases the target with the guided turn-rate limit
// for up to its max flight; if it gets within its proximity fuse, a deterministic roll on the
// engagement's kill chance decides whether the target dies. It runs after combat (which counts
// the reload down) and before the projectiles, so a missile fired this tick flies this tick.
public sealed partial class World
{
    private const int TrackCap = 1 << 20; // tracking ticks stop counting here (no overflow)

    private readonly List<AirBurst> _airBursts = [];

    // Scratch for one tick of air defence, not game state: (owner, target) of the missiles in
    // flight and of the tracks already picked this tick by lower IDs.
    private readonly List<(byte Owner, uint Target)> _inFlight = [];
    private readonly List<(byte Owner, uint Target)> _tracked = [];

    /// <summary>The air-defence missiles that went off in the last tick, in missile ID order.</summary>
    public IReadOnlyList<AirBurst> AirBursts => _airBursts;

    private void RunAirDefense()
    {
        var ready = false;
        foreach (var u in _units)
        {
            var wi = InterceptorWeapon(u.Def);
            if (wi < 0)
            {
                continue;
            }
            if (!ready)
            {
                CollectInFlight();
                ready = true;
            }
            var w = u.Def.Weapons[wi];
            var ic = w.Effect.Interceptor!;
            // He engages in Guard, idle and standing, on foot (never from a vehicle or a building).
            if (!u.Alive || !u.CanAct || u.Garrison != 0 || u.Stance != Stance.Guard || u.Order.Kind != OrderKind.Idle || u.Moving ||
                u.Def.Archetype.AirVision.Raw == 0)
            {
                DropTrack(u);
                continue;
            }
            var eye = EyeHeightRaw(u);
            var p = PickAirTarget(u, ic, eye);
            if (p is null)
            {
                DropTrack(u);
                continue;
            }
            if (p.Id != u.AirTarget)
            {
                (u.AirTarget, u.TrackTicks) = (p.Id, 0); // a new target: the lock starts over
            }
            _tracked.Add((u.Owner, p.Id));
            var aligned = true;
            if (Facing.TryFromDirection(p.Pos - u.Pos, out var want))
            {
                u.Facing = Facing.TurnToward(u.Facing, want, u.Def.TurnRate);
                u.TurretFacing = u.Facing;
                aligned = Math.Abs(Facing.Diff(u.Facing, want)) <= FireAlignFacings;
            }
            if (aligned)
            {
                u.TrackTicks = Math.Min(u.TrackTicks + 1, TrackCap);
            }
            if (!aligned || u.TrackTicks < ic.LockTicks || u.Reload > 0 || !HasAmmo(u, w) || InFlightAt(u.Owner, p.Id))
            {
                continue;
            }
            var slant = Slant(u.Pos, eye, p);
            if (slant > w.Range.Raw || slant < w.Effect.MinRange.Raw)
            {
                continue;
            }
            LaunchInterceptor(u, w, p, eye, slant);
        }
        _tracked.Clear();
    }

    // InterceptorWeapon is the index of u's air-defence weapon, or -1.
    internal static int InterceptorWeapon(UnitDef d)
    {
        var weapons = d.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Effect.Interceptor is not null)
            {
                return i;
            }
        }
        return -1;
    }

    private static void DropTrack(Unit u) => (u.AirTarget, u.TrackTicks) = (0, 0);

    // EyeHeightRaw is where an air-defence soldier sees and launches from (absolute, raw).
    private long EyeHeightRaw(Unit u) => GroundRaw(u.Pos) + EyeRaw;

    private void CollectInFlight()
    {
        _inFlight.Clear();
        foreach (var p in _projectiles)
        {
            if (p.Weapon.Effect.Interceptor is not null && p.Guided && !p.Done)
            {
                _inFlight.Add((p.Owner, p.Target));
            }
        }
    }

    private bool InFlightAt(byte owner, uint target) => _inFlight.Contains((owner, target));

    // DetectsAir reports whether u detects air target p: today its own air vision, a slant
    // distance from its eye. The seam for radar and team spotting (the vision milestone).
    private bool DetectsAir(Unit u, long eye, Projectile p) => Slant(u.Pos, eye, p) <= u.Def.Archetype.AirVision.Raw;

    // Slant is the 3D distance (raw) from an eye at (pos, eye) to air target p.
    private static long Slant(Vec pos, long eye, Projectile p) => Len3(p.Pos.X.Raw - pos.X.Raw, p.Pos.Y.Raw - pos.Y.Raw, p.Altitude.Raw - eye);

    internal static long Len3(long x, long y, long z) => Fixed.Sqrt(x * x + y * y + z * z);

    // IsAirTarget reports whether p is something u's interceptor may engage: an enemy projectile
    // of one of its classes, still flying, that u detects.
    private bool IsAirTarget(Unit u, Interceptor ic, long eye, Projectile p) =>
        !p.Done && p.Owner != u.Owner && ic.Engages(p.Interceptible) && DetectsAir(u, eye, p);

    // PickAirTarget keeps u's current target while it still detects it, unless a friendly missile
    // already flies at it and a free one is about; otherwise the nearest free target (no friendly
    // missile at it, no friendly lower ID tracking it), else the nearest; ties go to the lower ID.
    private Projectile? PickAirTarget(Unit u, Interceptor ic, long eye)
    {
        Projectile? free = null, any = null;
        long freeD = 0, anyD = 0;
        foreach (var p in _projectiles)
        {
            if (!IsAirTarget(u, ic, eye, p))
            {
                continue;
            }
            var d = Slant(u.Pos, eye, p);
            if (any is null || d < anyD)
            {
                (any, anyD) = (p, d); // projectiles are in ID order: a tie keeps the lower ID
            }
            if (!InFlightAt(u.Owner, p.Id) && !_tracked.Contains((u.Owner, p.Id)) && (free is null || d < freeD))
            {
                (free, freeD) = (p, d);
            }
        }
        if (u.AirTarget != 0 && ProjectileById(u.AirTarget) is { } cur && IsAirTarget(u, ic, eye, cur) &&
            (!InFlightAt(u.Owner, cur.Id) || free is null))
        {
            return cur;
        }
        return free ?? any;
    }

    // ProjectileById returns the projectile in flight with this ID, or null.
    internal Projectile? ProjectileById(uint id)
    {
        int lo = 0, hi = _projectiles.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_projectiles[mid].Id < id)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < _projectiles.Count && _projectiles[lo].Id == id ? _projectiles[lo] : null;
    }

    // LaunchInterceptor fires u's missile at air target p: one round, the reload starts, and the
    // missile leaves his eye straight at the target, carrying the engagement as he saw it.
    private void LaunchInterceptor(Unit u, WeaponDef w, Projectile p, long eye, long slant)
    {
        if (w.Stock >= 0)
        {
            u.MutableCharges[w.Stock]--;
        }
        u.Reload = w.Reload * 100;
        u.LastShot = Tick;
        _nextId++;
        var m = new Projectile(_nextId, u, w, p.Pos, p.Id)
        {
            Origin = u.Pos,
            LaunchHeight = Fixed.FromRaw(eye),
            Altitude = Fixed.FromRaw(eye),
            LaunchRange = Fixed.FromRaw(slant),
            LaunchCross = LosRate(u.Pos, eye, p),
            Tracked = u.TrackTicks,
        };
        var speed = w.Effect.ProjectileSpeed.Raw;
        long dx = p.Pos.X.Raw - u.Pos.X.Raw, dy = p.Pos.Y.Raw - u.Pos.Y.Raw, dz = p.Altitude.Raw - eye;
        var len = Math.Max(Len3(dx, dy, dz), 1);
        m.Vel = new Vec(Fixed.FromRaw(dx * speed / len), Fixed.FromRaw(dy * speed / len));
        m.VelZ = Fixed.FromRaw(dz * speed / len);
        _projectiles.Add(m);
        _inFlight.Add((u.Owner, p.Id));
    }

    // LosRate is how fast the line of sight from an eye at (pos, eye) to air target p turns, in
    // milliradians per second: |r x v| / |r|^2 with r the line of sight and v the target's velocity.
    private int LosRate(Vec pos, long eye, Projectile p)
    {
        long rx = p.Pos.X.Raw - pos.X.Raw, ry = p.Pos.Y.Raw - pos.Y.Raw, rz = p.Altitude.Raw - eye;
        var (vx, vy, vz) = AirVelocity(p);
        long cx = ry * vz - rz * vy, cy = rz * vx - rx * vz, cz = rx * vy - ry * vx;
        var r2 = rx * rx + ry * ry + rz * rz;
        return r2 == 0 ? 0 : (int)Math.Min(int.MaxValue, Len3(cx, cy, cz) * 1000 * TicksPerSecond / r2);
    }

    // AirVelocity is an air target's velocity per tick (raw, x y z) from its flight model: a
    // one-way attack drone flies toward its point at its speed while it climbs, cruises or dives.
    // The one place that reads an air target's motion (rockets and missiles later: their arcs).
    internal (long X, long Y, long Z) AirVelocity(Projectile p)
    {
        if (p.Weapon.Effect.Drone is not { } d)
        {
            return (0, 0, 0);
        }
        var speed = p.Weapon.Effect.ProjectileSpeed;
        var to = p.Dest - p.Pos;
        var step = to.Len().Raw > speed.Raw ? to.WithLen(speed) : to;
        long z = p.Phase switch
        {
            DronePhase.Climb => Math.Clamp(MaxGroundRaw() + d.CruiseAboveMax - p.Altitude.Raw, 0, d.ClimbPerTick),
            DronePhase.Dive when p.DiveDist.Raw > 0 => -(p.DiveFrom.Raw - GroundRaw(p.Dest)) * step.Len().Raw / p.DiveDist.Raw,
            _ => 0,
        };
        return (step.X.Raw, step.Y.Raw, z);
    }

    // StepInterceptor moves an air-defence missile one tick: while its target flies it steers its
    // 3D velocity toward it (the change capped by the turn rate: the Kornet's homing in 3D), then
    // flies one step. Within its fuse of the target it goes off and the kill roll decides; on the
    // ground it goes off there; at its max flight it blows itself up. Once its target is gone it
    // flies on straight to its max flight. Its target always has a lower ID: it moved already.
    private void StepInterceptor(Projectile p, Interceptor ic, Guidance g)
    {
        var speed = p.Weapon.Effect.ProjectileSpeed.Raw;
        var t = p.Guided ? ProjectileById(p.Target) : null;
        if (t is null || t.Done)
        {
            p.Guided = false;
            t = null;
        }
        long vx = p.Vel.X.Raw, vy = p.Vel.Y.Raw, vz = p.VelZ.Raw;
        if (t is not null)
        {
            long wx = t.Pos.X.Raw - p.Pos.X.Raw, wy = t.Pos.Y.Raw - p.Pos.Y.Raw, wz = t.Altitude.Raw - p.Altitude.Raw;
            var wl = Len3(wx, wy, wz);
            if (wl > 0)
            {
                long dx = wx * speed / wl - vx, dy = wy * speed / wl - vy, dz = wz * speed / wl - vz;
                var max = speed * g.TurnPermille / 1000;
                var dl = Len3(dx, dy, dz);
                if (dl > max)
                {
                    (dx, dy, dz) = (dx * max / dl, dy * max / dl, dz * max / dl);
                }
                long nx = vx + dx, ny = vy + dy, nz = vz + dz;
                var nl = Len3(nx, ny, nz);
                (vx, vy, vz) = nl > 0 ? (nx * speed / nl, ny * speed / nl, nz * speed / nl) : (wx * speed / wl, wy * speed / wl, wz * speed / wl);
            }
            p.Vel = new Vec(Fixed.FromRaw(vx), Fixed.FromRaw(vy));
            p.VelZ = Fixed.FromRaw(vz);
            // The proximity fuse: the point of this tick's step closest to the target.
            long rx = t.Pos.X.Raw - p.Pos.X.Raw, ry = t.Pos.Y.Raw - p.Pos.Y.Raw, rz = t.Altitude.Raw - p.Altitude.Raw;
            var ss = vx * vx + vy * vy + vz * vz;
            var k = ss == 0 ? 0 : Math.Clamp(rx * vx + ry * vy + rz * vz, 0, ss);
            long cx = ss == 0 ? 0 : vx * k / ss, cy = ss == 0 ? 0 : vy * k / ss, cz = ss == 0 ? 0 : vz * k / ss;
            long ex = rx - cx, ey = ry - cy, ez = rz - cz;
            var fuse = ic.Fuse.Raw;
            if (ex * ex + ey * ey + ez * ez <= fuse * fuse)
            {
                Fuse(p, t, ic, new Vec(Fixed.FromRaw(p.Pos.X.Raw + cx), Fixed.FromRaw(p.Pos.Y.Raw + cy)), p.Altitude.Raw + cz);
                return;
            }
        }
        // The ground: sampled along the step (hills in its way, or a dive into the ground).
        var len = Math.Max(1, Len3(vx, vy, 0));
        var samples = (int)Math.Max(1, (len * TerrainSteps + Fixed.OneRaw - 1) / Fixed.OneRaw);
        for (var s = 1; s <= samples; s++)
        {
            var q = new Vec(Fixed.FromRaw(p.Pos.X.Raw + vx * s / samples), Fixed.FromRaw(p.Pos.Y.Raw + vy * s / samples));
            var a = p.Altitude.Raw + vz * s / samples;
            if (a <= GroundRaw(q))
            {
                AirBurstAt(p, q, GroundRaw(q), AirBurstKind.Ground);
                return;
            }
        }
        p.Pos = new Vec(Fixed.FromRaw(p.Pos.X.Raw + vx), Fixed.FromRaw(p.Pos.Y.Raw + vy));
        p.Altitude = Fixed.FromRaw(p.Altitude.Raw + vz);
        p.Flown += Fixed.FromRaw(speed);
        if (p.Flown >= g.MaxFlight)
        {
            AirBurstAt(p, p.Pos, p.Altitude.Raw, AirBurstKind.SelfDestruct);
        }
    }

    // Fuse sets off a missile's proximity fuse at `at` (height alt) beside its target: the kill
    // roll decides whether the target dies there (removed in the air, no blast) or flies on.
    private void Fuse(Projectile p, Projectile t, Interceptor ic, Vec at, long alt)
    {
        var pk = KillPermille(p, t, ic);
        var roll = (int)(Rand.Hash(Match.Setup.Seed, Tick, p.Shooter, t.Id) % 1000);
        var kill = roll < pk;
        if (kill)
        {
            t.Done = true; // broken up in the air: no detonation, nothing on the ground
        }
        AirBurstAt(p, at, alt, kill ? AirBurstKind.Kill : AirBurstKind.Miss, pk, roll);
    }

    private void AirBurstAt(Projectile p, Vec at, long alt, AirBurstKind kind, int pk = 0, int roll = -1)
    {
        p.Pos = at;
        p.Altitude = Fixed.FromRaw(alt);
        p.Done = true;
        _airBursts.Add(new AirBurst(p.Id, p.Target, p.Shooter, at, p.Altitude, kind, pk, roll));
    }

    /// <summary>
    /// The kill chance (permille) of air-defence missile p going off beside target t: the base by
    /// aspect (tail-on best, head-on worst), times the target's speed, the crossing rate and
    /// slant range seen at launch, and the tracking time (the formula in the air defence design).
    /// </summary>
    internal int KillPermille(Projectile p, Projectile t, Interceptor ic)
    {
        long mx = p.Vel.X.Raw, my = p.Vel.Y.Raw, mz = p.VelZ.Raw;
        var (dx, dy, dz) = AirVelocity(t);
        var ms = Len3(mx, my, mz);
        var ds = Len3(dx, dy, dz);
        var cos = ms == 0 || ds == 0 ? 0 : Math.Clamp((mx * dx + my * dy + mz * dz) * 1000 / (ms * ds), -1000, 1000);
        var pk = ic.KillHead * 10L + (ic.KillTail - ic.KillHead) * 10L * (1000 + cos) / 2000;
        if (ds > ic.SpeedRef.Raw)
        {
            pk = pk * ic.SpeedRef.Raw / ds; // faster than the reference target: harder
        }
        pk = pk * ic.CrossHalf / (ic.CrossHalf + p.LaunchCross);
        var range = p.Weapon.Range.Raw;
        pk = pk * Math.Max(0, 1000 - (1000 - 10L * ic.RangeFar) * Math.Min(p.LaunchRange.Raw, range) / range) / 1000;
        var extra = Math.Clamp(p.Tracked - ic.LockTicks, 0, ic.TrackFull);
        pk = pk * (1000 + 10L * ic.TrackBonus * extra / ic.TrackFull) / 1000;
        return (int)Math.Clamp(pk, ic.KillMin * 10L, ic.KillMax * 10L);
    }
}
