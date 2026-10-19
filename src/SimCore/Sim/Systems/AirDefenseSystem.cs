using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// The air defence system (docs: the game repo's docs/air-defense-design.md). Units with an
// interceptor weapon (the AA soldier's missile, the air defence vehicle's) detect enemy air targets of its classes (one-way
// attack drones, which are projectiles; scout drones, which are units: an AirBody either way) within their own air vision, pick one, turn to it and track it; once locked
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

    // Scratch: the scout drones flying this tick, in ID order.
    private readonly List<Unit> _scouts = [];

    // Scratch, rebuilt each tick air defence runs (not state): team cueing (E5). For each air target
    // some sensor detects, its ID and a bit per player whose sensors detect it, sorted by ID.
    private readonly List<(uint Id, uint Owners)> _airCues = [];
    private readonly List<(Unit Unit, long Eye)> _airSensors = [];

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
                CollectScouts();
                CollectAirCues();
                ready = true;
            }
            var w = u.Def.Weapons[wi];
            var ic = w.Effect.Interceptor!;
            // He engages in Guard, idle and standing, on foot; inside a building he guards on his own
            // (any stance but Hold fire), from a window; never from a vehicle. An aircraft carrying air
            // missiles (the jets, E8) never fires them here: air-to-air from aircraft is E6.
            if (!u.Alive || !u.CanAct || u.Def.Archetype.Flight is not null || !GuardsAir(u) || u.Def.Archetype.AirVision.Raw == 0)
            {
                DropTrack(u);
                continue;
            }
            var eye = EyeHeightRaw(u, ic);
            if (PickAirTarget(u, w, ic, eye) is not { } p)
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
                if (u.Def.Turret)
                {
                    // A vehicle's launcher turret turns to the target; the hull stays as it stands.
                    u.TurretFacing = Facing.TurnToward(u.TurretFacing, want, TurretTurnRate);
                    aligned = Math.Abs(Facing.Diff(u.TurretFacing, want)) <= FireAlignFacings;
                }
                else
                {
                    u.Facing = Facing.TurnToward(u.Facing, want, u.Def.TurnRate);
                    u.TurretFacing = u.Facing;
                    aligned = Math.Abs(Facing.Diff(u.Facing, want)) <= FireAlignFacings;
                }
            }
            if (aligned)
            {
                u.TrackTicks = Math.Min(u.TrackTicks + 1, TrackCap);
            }
            if (!aligned || u.TrackTicks < LockTicksOn(ic, p) || u.Reload > 0 || !HasAmmo(u, w) || InFlightAt(u.Owner, p.Id))
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

    // GuardsAir reports whether u's stance and order let it engage air targets: in a building,
    // any stance but Hold fire; on foot, Guard (or Hold ground, or Aggressive) while idle or on a
    // Guard order, standing.
    private static bool GuardsAir(Unit u) => u.Garrison != 0
        ? u.Stance != Stance.HoldFire
        : (u.Stance is Stance.Guard or Stance.HoldGround or Stance.Aggressive) && (u.Order.Kind is OrderKind.Idle or OrderKind.Guard) && !u.Moving;

    // EyeHeightRaw is where an air-defence unit sees and launches from (absolute, raw): a soldier's
    // eye on foot, a window in a building, a vehicle's launcher (its weapon's launchHeight).
    private long EyeHeightRaw(Unit u, Interceptor ic) =>
        GroundRaw(u.Pos) + (u.Garrison != 0 ? WindowRaw : ic.LaunchHeight > 0 ? ic.LaunchHeight : EyeRaw);

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

    private void CollectScouts()
    {
        _scouts.Clear();
        foreach (var t in _units)
        {
            if (t.Def.Archetype.Flight is not null && t.Alive && !Grounded(t)) // never an aircraft on the ground (M3b)
            {
                _scouts.Add(t);
            }
        }
    }

    // DetectsAir reports whether u detects air target p with its own sensor: its air vision, a slant
    // distance from its eye, scaled by the target's signature (detect percent; E5).
    private static bool DetectsAir(Unit u, long eye, AirBody p) => Slant(u.Pos, eye, p) <= DetectRange(u.Def.Archetype.AirVision.Raw, p);

    // DetectRange is an air vision (raw) against air target p: its signature's detect percent of it.
    private static long DetectRange(long airVision, AirBody p) => p.Signature.Detect == 100 ? airVision : airVision * p.Signature.Detect / 100;

    /// <summary>The lock time (ticks) of interceptor ic on air target p: its lock ticks scaled by the target's signature (lock percent).</summary>
    internal static int LockTicksOn(Interceptor ic, AirBody p) => p.Signature.Lock == 100 ? ic.LockTicks : (int)((long)ic.LockTicks * p.Signature.Lock / 100);

    // CollectAirCues builds the team cueing table (the plan's air-combat model, section 2.8): every air
    // target (an interceptible projectile, a flying unit) that a player's sensor detects (any unit of
    // his with an air vision, alive, built and not inside: air defence radars, the soldiers' eyes, a
    // flying aircraft's radar from its height) is tracked for that player this tick. A shooter in range
    // of a tracked target may start its lock on it without detecting it itself (the lock time is still
    // its own). Scratch, rebuilt each tick: no state, nothing hashed.
    private void CollectAirCues()
    {
        _airCues.Clear();
        _airSensors.Clear();
        foreach (var s in _units)
        {
            if (s.Def.Archetype.AirVision.Raw != 0 && s.Alive && s.IsBuilt && s.Inside == 0 && (s.Def.Archetype.Flight is null || !Grounded(s)))
            {
                _airSensors.Add((s, SensorEyeRaw(s)));
            }
        }
        if (_airSensors.Count == 0)
        {
            return;
        }
        foreach (var p in _projectiles)
        {
            if (p.Interceptible != InterceptClass.None && !p.Done)
            {
                CueBody(new AirBody(p));
            }
        }
        foreach (var t in _scouts)
        {
            CueBody(new AirBody(t));
        }
        _airCues.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    private void CueBody(AirBody b)
    {
        uint owners = 0;
        foreach (var (s, eye) in _airSensors)
        {
            if (s.Owner != b.Owner && (owners & Bit(s.Owner)) == 0 && Slant(s.Pos, eye, b) <= DetectRange(s.Def.Archetype.AirVision.Raw, b))
            {
                owners |= Bit(s.Owner);
            }
        }
        if (owners != 0)
        {
            _airCues.Add((b.Id, owners));
        }
    }

    // SensorEyeRaw is where unit s's air sensor sees from (absolute, raw): a flying aircraft's height,
    // a window in a building, an air-defence weapon's launcher, else a soldier's eye.
    private long SensorEyeRaw(Unit s)
    {
        if (s.Def.Archetype.Flight is not null)
        {
            return s.Altitude.Raw;
        }
        var wi = InterceptorWeapon(s.Def);
        return wi >= 0 ? EyeHeightRaw(s, s.Def.Weapons[wi].Effect.Interceptor!) : GroundRaw(s.Pos) + (s.Garrison != 0 ? WindowRaw : EyeRaw);
    }

    // CuedFor reports whether player owner's sensors track air target id this tick (CollectAirCues).
    private bool CuedFor(byte owner, uint id)
    {
        int lo = 0, hi = _airCues.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_airCues[mid].Id < id)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < _airCues.Count && _airCues[lo].Id == id && (_airCues[lo].Owners & Bit(owner)) != 0;
    }

    /// <summary>Whether player owner's air sensors track air unit or projectile id this tick (team cueing, E5; valid after air defence ran).</summary>
    public bool AirTracked(byte owner, uint id) => CuedFor(owner, id);

    // Slant is the 3D distance (raw) from an eye at (pos, eye) to air target p.
    private static long Slant(Vec pos, long eye, AirBody p) => Len3(p.Pos.X.Raw - pos.X.Raw, p.Pos.Y.Raw - pos.Y.Raw, p.Alt - eye);

    internal static long Len3(long x, long y, long z) => Fixed.Sqrt(x * x + y * y + z * z);

    // IsAirTarget reports whether p is something u's interceptor may engage: an enemy air target
    // of one of its classes, still flying, that u detects, or that its side's sensors track while it
    // is within u's weapon range (team cueing, E5).
    private bool IsAirTarget(Unit u, WeaponDef w, Interceptor ic, long eye, AirBody p) =>
        !p.Gone && p.Owner != u.Owner && ic.Engages(p.Class) &&
        (DetectsAir(u, eye, p) || (CuedFor(u.Owner, p.Id) && Slant(u.Pos, eye, p) <= w.Range.Raw));

    // PickAirTarget keeps u's current target while it still detects it, unless a friendly missile
    // already flies at it and a free one is about; otherwise the nearest free target (no friendly
    // missile at it, no friendly lower ID tracking it), else the nearest; ties go to the lower ID.
    private AirBody? PickAirTarget(Unit u, WeaponDef w, Interceptor ic, long eye)
    {
        var pick = new AirPick();
        foreach (var p in _projectiles)
        {
            Consider(u, w, ic, eye, new AirBody(p), ref pick);
        }
        if (ic.EngagesUnits)
        {
            foreach (var t in _scouts)
            {
                Consider(u, w, ic, eye, new AirBody(t), ref pick);
            }
        }
        if (u.AirTarget != 0 && AirBodyById(u.AirTarget) is { } cur && IsAirTarget(u, w, ic, eye, cur) &&
            (!InFlightAt(u.Owner, cur.Id) || pick.Free is null))
        {
            return cur;
        }
        return pick.Free ?? pick.Any;
    }

    // The nearest candidate and the nearest free one so far (PickAirTarget).
    private struct AirPick
    {
        public AirBody? Free, Any;
        public long FreeD, AnyD;
    }

    private void Consider(Unit u, WeaponDef w, Interceptor ic, long eye, AirBody p, ref AirPick pick)
    {
        if (!IsAirTarget(u, w, ic, eye, p))
        {
            return;
        }
        var d = Slant(u.Pos, eye, p);
        if (pick.Any is not { } a || d < pick.AnyD || (d == pick.AnyD && p.Id < a.Id))
        {
            (pick.Any, pick.AnyD) = (p, d);
        }
        if (!InFlightAt(u.Owner, p.Id) && !_tracked.Contains((u.Owner, p.Id)) &&
            (pick.Free is not { } f || d < pick.FreeD || (d == pick.FreeD && p.Id < f.Id)))
        {
            (pick.Free, pick.FreeD) = (p, d);
        }
    }

    // AirBodyById returns the air target with this ID still in the world (a projectile in flight,
    // or a scout drone), or null.
    internal AirBody? AirBodyById(uint id)
    {
        if (ProjectileById(id) is { } p)
        {
            return new AirBody(p);
        }
        return Unit(id) is { Def.Archetype.Flight: not null } t && !Grounded(t) ? new AirBody(t) : null; // landed: no air target (M3b)
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
    // missile leaves his eye toward where the target will be (lead), carrying the engagement as he saw it.
    private void LaunchInterceptor(Unit u, WeaponDef w, AirBody p, long eye, long slant)
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
        var (dx, dy, dz) = LeadPoint(m, p, w.Effect.Interceptor!, speed); // off toward where the target will be
        var len = Math.Max(Len3(dx, dy, dz), 1);
        m.Vel = new Vec(Fixed.FromRaw(dx * speed / len), Fixed.FromRaw(dy * speed / len));
        m.VelZ = Fixed.FromRaw(dz * speed / len);
        _projectiles.Add(m);
        _inFlight.Add((u.Owner, p.Id));
    }

    // LosRate is how fast the line of sight from an eye at (pos, eye) to air target p turns, in
    // milliradians per second: |r x v| / |r|^2 with r the line of sight and v the target's velocity.
    private int LosRate(Vec pos, long eye, AirBody p)
    {
        long rx = p.Pos.X.Raw - pos.X.Raw, ry = p.Pos.Y.Raw - pos.Y.Raw, rz = p.Alt - eye;
        var (vx, vy, vz) = AirVelocity(p);
        long cx = ry * vz - rz * vy, cy = rz * vx - rx * vz, cz = rx * vy - ry * vx;
        var r2 = rx * rx + ry * ry + rz * rz;
        return r2 == 0 ? 0 : (int)Math.Min(int.MaxValue, Len3(cx, cy, cz) * 1000 * TicksPerSecond / r2);
    }

    // AirVelocity is an air target's velocity per tick (raw, x y z) from its flight model: a
    // one-way attack drone flies toward its point at its speed while it climbs, cruises or dives;
    // a scout drone's last step stands for its next (it turns slowly). The one place that reads
    // an air target's motion (rockets and missiles later: their arcs).
    internal (long X, long Y, long Z) AirVelocity(AirBody b) => b.P is { } p ? AirVelocity(p) : b.Step;

    internal (long X, long Y, long Z) AirVelocity(Projectile p)
    {
        if (p.Weapon.Effect.Drone is not { } d)
        {
            return (0, 0, 0);
        }
        var n = DroneNext(p, d); // its flight model's next tick: the climb, cruise, pitch-over or dive
        return (n.Step.X.Raw, n.Step.Y.Raw, n.Altitude - p.Altitude.Raw);
    }

    // StepInterceptor moves an air-defence missile one tick: while its target flies it steers its
    // 3D velocity toward where the target will be (lead pursuit: the target's position plus its
    // velocity times the time to go, the change capped by the turn rate), then flies one step.
    // Within its fuse of the target (their closest approach over the tick, both moving) it goes
    // off and the kill roll decides; on the ground it goes off there; at its max flight it blows
    // itself up. Once its target is gone it flies on straight to its max flight. Its target always
    // has a lower ID: it moved already this tick.
    private void StepInterceptor(Projectile p, Interceptor ic, Guidance g)
    {
        var speed = p.Weapon.Effect.ProjectileSpeed.Raw;
        var t = p.Guided ? AirBodyById(p.Target) : null;
        if (t is not { Gone: false })
        {
            p.Guided = false;
            t = null;
        }
        long vx = p.Vel.X.Raw, vy = p.Vel.Y.Raw, vz = p.VelZ.Raw;
        if (t is not null)
        {
            var (wx, wy, wz) = LeadPoint(p, t.Value, ic, speed);
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
            if (FuseCheck(p, t.Value, ic, vx, vy, vz))
            {
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

    // LeadPoint is where missile p steers this tick, relative to it (raw x y z): target t's
    // position plus its velocity times the time to go, the range over the closing speed (the
    // missile's speed less the target's speed away along the line of sight), at most LeadTicks
    // (0: pure pursuit, straight at the target).
    private (long X, long Y, long Z) LeadPoint(Projectile p, AirBody t, Interceptor ic, long speed)
    {
        long rx = t.Pos.X.Raw - p.Pos.X.Raw, ry = t.Pos.Y.Raw - p.Pos.Y.Raw, rz = t.Alt - p.Altitude.Raw;
        var rl = Len3(rx, ry, rz);
        if (ic.LeadTicks == 0 || rl == 0)
        {
            return (rx, ry, rz);
        }
        var (tx, ty, tz) = AirVelocity(t);
        var closing = speed - (rx * tx + ry * ty + rz * tz) / rl;
        var tgo = closing <= 0 ? ic.LeadTicks : Math.Min(ic.LeadTicks, rl / closing);
        return (rx + tx * tgo, ry + ty * tgo, rz + tz * tgo);
    }

    // FuseCheck sets missile p's proximity fuse off if it passes within its fuse of target t this
    // tick: both move over the tick (the target its own step, already taken; the missile its new
    // velocity v), so it is their closest approach over the tick, relative motion. True if it went off.
    private bool FuseCheck(Projectile p, AirBody t, Interceptor ic, long vx, long vy, long vz)
    {
        var (tx, ty, tz) = t.Step;
        // The target relative to the missile at the start of the tick, and their relative velocity.
        long rx = t.Pos.X.Raw - tx - p.Pos.X.Raw, ry = t.Pos.Y.Raw - ty - p.Pos.Y.Raw, rz = t.Alt - tz - p.Altitude.Raw;
        long ux = tx - vx, uy = ty - vy, uz = tz - vz;
        var uu = ux * ux + uy * uy + uz * uz;
        var k = uu == 0 ? 0 : Math.Clamp(-(rx * ux + ry * uy + rz * uz), 0, uu); // the closest moment: k / uu of the tick
        long ex = rx + (uu == 0 ? 0 : ux * k / uu), ey = ry + (uu == 0 ? 0 : uy * k / uu), ez = rz + (uu == 0 ? 0 : uz * k / uu);
        var fuse = ic.Fuse.Raw;
        if (ex * ex + ey * ey + ez * ez > fuse * fuse)
        {
            return false;
        }
        long mx = uu == 0 ? 0 : vx * k / uu, my = uu == 0 ? 0 : vy * k / uu, mz = uu == 0 ? 0 : vz * k / uu;
        Fuse(p, t, ic, new Vec(Fixed.FromRaw(p.Pos.X.Raw + mx), Fixed.FromRaw(p.Pos.Y.Raw + my)), p.Altitude.Raw + mz);
        return true;
    }

    // Fuse sets off a missile's proximity fuse at `at` (height alt) beside its target: the kill
    // roll decides whether the target dies there (removed in the air, no blast) or flies on. For a
    // class with a hit rule (Interceptor.Hits: the Overseer) the roll is that flat hit chance and a
    // hit takes its share of the target's max HP: a kill once nothing is left, else a hit it survives.
    private void Fuse(Projectile p, AirBody t, Interceptor ic, Vec at, long alt)
    {
        var rule = ic.HitOn(t.Class);
        var kill100 = t.Signature.Kill;
        var pk = rule is { } r ? r.Chance * 10 * kill100 / 100 : KillPermille(p, t, ic);
        var roll = (int)(Rand.Hash(Match.Setup.Seed, Tick, p.Shooter, t.Id) % 1000);
        var kill = roll < pk;
        var kind = kill ? AirBurstKind.Kill : AirBurstKind.Miss;
        if (kill && t.P is { } drone)
        {
            drone.Done = true; // broken up in the air: no detonation, nothing on the ground
        }
        else if (kill && (rule?.Damage ?? ic.HitDamageOn(t.Class)) is var share && (rule is not null || share < 100))
        {
            // A hit rule (the Overseer), or a kill-model class whose hit takes a share (a jet: E5's hitDamage).
            var u = t.U!;
            u.HP -= (int)Math.Min(u.HP, Math.Max(1, (long)u.Def.MaxHp * share / 100));
            kind = u.Alive ? AirBurstKind.Hit : AirBurstKind.Kill; // brought down by its last share, or flying on hurt
        }
        else if (kill)
        {
            t.U!.HP = 0; // a scout drone shot down: it falls (the renderer's), no blast
        }
        AirBurstAt(p, at, alt, kind, pk, roll);
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
    /// slant range seen at launch, and the tracking time (the formula in the air defence design);
    /// against a scout drone the whole range scales down so its best is the weapon's ScoutBest.
    /// </summary>
    internal int KillPermille(Projectile p, Projectile t, Interceptor ic) => KillPermille(p, new AirBody(t), ic);

    internal int KillPermille(Projectile p, AirBody t, Interceptor ic)
    {
        long mx = p.Vel.X.Raw, my = p.Vel.Y.Raw, mz = p.VelZ.Raw;
        var (dx, dy, dz) = AirVelocity(t);
        var ms = Len3(mx, my, mz);
        var ds = Len3(dx, dy, dz);
        var cos = ms == 0 || ds == 0 ? 0 : Math.Clamp((mx * dx + my * dy + mz * dz) * 1000 / (ms * ds), -1000, 1000);
        var pk = ic.KillHead * 10L + (ic.KillTail - ic.KillHead) * 10L * (1000 + cos) / 2000;
        var speedRef = ic.SpeedRefOn(t.Class).Raw;
        if (ds > speedRef)
        {
            pk = pk * speedRef / ds; // faster than the reference target (per class: jets fly faster than drones): harder
        }
        pk = pk * ic.CrossHalf / (ic.CrossHalf + p.LaunchCross);
        var range = p.Weapon.Range.Raw;
        pk = pk * Math.Max(0, 1000 - (1000 - 10L * ic.RangeFar) * Math.Min(p.LaunchRange.Raw, range) / range) / 1000;
        var extra = Math.Clamp(p.Tracked - LockTicksOn(ic, t), 0, ic.TrackFull);
        pk = pk * (1000 + 10L * ic.TrackBonus * extra / ic.TrackFull) / 1000;
        pk = Math.Clamp(pk, ic.KillMin * 10L, ic.KillMax * 10L);
        var best = ic.Best(t.Class);
        if (best != ic.KillMax && ic.KillMax > 0)
        {
            pk = pk * best / ic.KillMax; // a scout drone: hard to bring down at the best of times
        }
        if (t.Signature.Kill != 100)
        {
            pk = pk * t.Signature.Kill / 100; // a low signature (E5): harder to bring down
        }
        return (int)pk;
    }
}

/// <summary>
/// An air target as air defence sees it: a one-way attack drone (a projectile) or a scout drone (a
/// unit). Exactly one of P and U is set.
/// </summary>
internal readonly struct AirBody
{
    public AirBody(Projectile p) => P = p;

    public AirBody(Unit u) => U = u;

    public Projectile? P { get; }

    public Unit? U { get; }

    public uint Id => P?.Id ?? U!.Id;

    public byte Owner => P?.Owner ?? U!.Owner;

    public Vec Pos => P?.Pos ?? U!.Pos;

    /// <summary>Its height, absolute, raw.</summary>
    public long Alt => P?.Altitude.Raw ?? U!.Altitude.Raw;

    /// <summary>Its own step over this tick (raw x y z), which the fuse reads.</summary>
    public (long X, long Y, long Z) Step => P is { } p ? (p.Vel.X.Raw, p.Vel.Y.Raw, p.VelZ.Raw) : (U!.Vel.X.Raw, U.Vel.Y.Raw, U.VelZ.Raw);

    public InterceptClass Class => P?.Interceptible ?? U!.Def.Archetype.Flight?.Class ?? InterceptClass.None;

    /// <summary>Its signature (E5): a flying unit's archetype's; a projectile is a normal target.</summary>
    public Signature Signature => U?.Def.Archetype.Signature ?? Signature.Normal;

    /// <summary>Whether it is gone: the projectile done, the unit dead.</summary>
    public bool Gone => P?.Done ?? !U!.Alive;
}
