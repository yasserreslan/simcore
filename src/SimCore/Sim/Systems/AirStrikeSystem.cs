using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// Air-to-ground (E7; the plan's air-combat model, section 2.10): bombs, strike runs and strafing, for
// any aircraft whose weapons carry a delivery block (a bomb) or an airGun that reaches the ground (the
// jets'). All integer; rolls through Rand.Hash (no generator state); units by ID.
//
//  - Who attacks: the owner's rules. On an attack order at a ground unit, or an attack-ground order at a
//    point (a Strike), it attacks only that; on Guard with ground kinds in its filter it picks the
//    nearest spotted enemy of those kinds inside the area plus its leash (the mission system's hunt) and
//    attacks it the same way. Patrol, Search, Roam and Orbit never attack the ground.
//  - The weapon of a pass: its first bomb with ammunition that can hit the target, else its gun (a
//    strafing pass). Out of both for the target, the strike ends and its task resumes.
//  - A pass (Unit.Strike, hashed): Approach (to the initial point, IP: on the line from the aim point
//    toward where it is when the pass starts, ipDistance before the release point; low at its ingress
//    height, a level or dive run pops up on the way) -> RunIn (the profile: level holds the release
//    height; dive flies down a line at the dive angle through the release point; toss runs in low on
//    the afterburner and pulls up all out; guided holds the release height and releases once the target
//    is within the bomb's reach; strafe flies down a shallow 12-degree line to its floor) -> the release
//    (or bursts) -> Egress (the afterburner, a 60-degree turn away from the run-in heading toward the map's
//    centre, a climb to its flight height, for 2 s) -> a new pass while the order holds and it is armed.
//  - The release solution: each run-in tick it predicts where a bomb released now would land, by the
//    bomb's own integer fall (the same step the bomb flies, so the prediction is exact), and releases
//    when that point is within half a tick's travel of the aim point along its track (the target's lead
//    point: its position plus its step times the fall time). Too far past it, or too far off the line,
//    it drops nothing on this pass (an abort) and comes round again. Scatter: per axis, at most the
//    profile's scatter, seeded (Rand.Hash of the seed, the tick, the aircraft and the bomb), applied to
//    the release velocity so the bomb lands that far off.
//  - The bomb (Projectile.Bomb, hashed): an integer parabola at the delivery's gravity; it goes off where
//    it meets the ground (interpolated within the tick) or the first roof, vehicle or soldier it reaches
//    on the way down: a direct hit on that unit, the splash (two rings), the runway effect (craters on a
//    runway), the building rules. A guided bomb rolls its hit at release (the Watcher's model) and glides
//    straight at its target (a hit) or beside it (a miss) at the weapon's speed.
//  - Strafing: within the gun's range (slant) and its cone in azimuth (the floor stops the sim's nose from
//    pointing down, so the cone is measured level), one burst a reload: a splash hit (the gun's splash)
//    at a point that walks along its track from short (at full range) to long (overhead), with a seeded
//    half-cell jitter across it. Passed over the target, it pulls off (egress).
//  - Events for renderers: World.BombReleases, World.BombImpacts (position, damage, airstrike tier,
//    warhead, direct hit, building, runway crater), World.StrafeBursts.
public sealed partial class World
{
    // A strike goes on at the last seen position for at most this long with its target out of sight (10 s).
    private const int UnseenTicks = 10 * TicksPerSecond;

    // The egress after a pass: the afterburner and a turn away, for this long.
    private const int EgressTicks = 2 * TicksPerSecond;

    // The egress heading: the run-in heading turned this many degrees (toward the map's centre).
    private const int EgressTurnDeg = 60;

    // A run-in starts with the target within 45 degrees of the nose (cos in thousandths).
    private const int RunInCos = 707;

    // A guided bomb is released with the target within 30 degrees of the nose.
    private const int GuidedReleaseCos = 866;

    // A strafing pass dives down a line this steep (degrees) toward its floor.
    private const int StrafeAngleDeg = 12;

    // A strafing pass's IP lies this far before its gun's range (raw: 16 cells, the bombs' ipDistance).
    private const long StrafeIpRaw = 16 * Fixed.OneRaw;

    // A strafing burst lands up to this far short (at full range) or long (overhead) of the aim point (raw: 2 cells).
    private const long StrafeWalkRaw = 2 * Fixed.OneRaw;

    // And up to this far across its track (raw: half a cell).
    private const long StrafeJitterRaw = Fixed.OneRaw / 2;

    // A toss pulls up this long before its release distance (a second's travel).
    private const int TossPullUpTicks = TicksPerSecond;

    // An abort: the release point more than this past the aim point along its track (raw: 2 cells) ...
    private const long AbortPastRaw = 2 * Fixed.OneRaw;

    // ... or more than this off its line, plus the profile's scatter (raw: 2 cells).
    private const long AbortCrossRaw = 2 * Fixed.OneRaw;

    // The release solution looks at most this far ahead (ticks of fall).
    private const int MaxFallTicks = 30 * TicksPerSecond;

    // Bingo fuel on the run-in: it finishes the release if that is at most this far off (3 s).
    private const int BingoFinishTicks = 3 * TicksPerSecond;

    // Salts for the scatter and guided rolls, so they never match another roll on the same tick and pair.
    private const ulong ScatterXSalt = 0x5CA7_0001_0000_0000UL, ScatterYSalt = 0x5CA7_0002_0000_0000UL;
    private const ulong GuidedSalt = 0x61DE_0000_0000_0000UL, GuidedDirSalt = 0x61DE_0001_0000_0000UL, GuidedFarSalt = 0x61DE_0002_0000_0000UL;
    private const ulong StrafeSalt = 0x57AF_0000_0000_0000UL;

    private readonly List<BombRelease> _bombReleases = [];
    private readonly List<BombImpact> _bombImpacts = [];
    private readonly List<StrafeBurst> _strafeBursts = [];

    /// <summary>The bombs released in the last tick, in order (E7): draw them falling (their projectiles carry the position each tick).</summary>
    public IReadOnlyList<BombRelease> BombReleases => _bombReleases;

    /// <summary>The bombs that went off in the last tick, in projectile ID order (E7): the airstrike visuals' input (tier, damage, warhead).</summary>
    public IReadOnlyList<BombImpact> BombImpacts => _bombImpacts;

    /// <summary>The strafing bursts fired in the last tick, in order (E7).</summary>
    public IReadOnlyList<StrafeBurst> StrafeBursts => _strafeBursts;

    // ClearStrikeEvents starts a tick's air-to-ground reports (the air movement system calls it first).
    private void ClearStrikeEvents()
    {
        _bombReleases.Clear();
        _bombImpacts.Clear();
        _strafeBursts.Clear();
    }

    /// <summary>
    /// The airstrike tier (1 to 7) of a blast of this much damage (the plan's section 6.3: T1 under 250,
    /// T2 to 449, T3 to 599, T4 to 899, T5 to 1,199, T6 to 1,799, T7 from 1,800): the client's smoke size.
    /// </summary>
    public static int StrikeTier(int damage) =>
        damage < 250 ? 1 : damage < 450 ? 2 : damage < 600 ? 3 : damage < 900 ? 4 : damage < 1200 ? 5 : damage < 1800 ? 6 : 7;

    /// <summary>
    /// Aircraft u's weapon for an air-to-ground attack on t (null: a point): its first bomb (a delivery
    /// block) with ammunition that can hit t, else its first gun (an airGun reaching the ground) that can;
    /// null when none can (E7).
    /// </summary>
    internal static WeaponDef? AirGroundWeapon(Unit u, Unit? t)
    {
        WeaponDef? gun = null;
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if ((w.Effect.Delivery is null && w.Effect.AirGun is null) || (w.Effect.Targets & Layers.Ground) == 0 || !HasAmmo(u, w) ||
                (t is not null && !CanHit(w, t)))
            {
                continue;
            }
            if (w.Effect.Delivery is not null)
            {
                return w;
            }
            gun ??= w;
        }
        return gun;
    }

    // GuardStrikes: Guard's ground hunt may send u (strike weapon w: the Watcher's, or a jet's first
    // air-to-ground weapon) at t: an enemy it sees, out in the open, that one of its weapons can hit.
    private bool GuardStrikes(Unit u, WeaponDef w, Unit t) =>
        w.Effect.AirStrike is not null ? StrikeTarget(u, w, t)
            : t.Alive && t.Owner != u.Owner && t.Inside == 0 && IsVisibleTo(t, u.Owner) && !u.Homing && AirGroundWeapon(u, t) is not null;

    // KindOfPass is how weapon w's pass is flown.
    private static PassKind KindOfPass(WeaponDef w) => w.Effect.Delivery switch
    {
        null => PassKind.Strafe,
        { Guided: not null } => PassKind.Guided,
        { Profile: DeliveryProfile.Dive } => PassKind.Dive,
        { Profile: DeliveryProfile.Toss } => PassKind.Toss,
        _ => PassKind.Level,
    };

    // TanPermille is tan(deg) in thousandths (0 to 89 degrees).
    private static long TanPermille(int deg) => (long)CosDeg(90 - Math.Clamp(deg, 0, 89)) * 1000 / Math.Max(1, CosDeg(Math.Clamp(deg, 0, 89)));

    // AirSpeedOf is aircraft u's level speed, raw a second (a fixed wing's own; else its speed x 20).
    private static long AirSpeedOf(Unit u) => u.Wing?.Speed ?? u.Def.Speed.Raw * TicksPerSecond;

    // ---- The strike order ----

    // StrikeRunOrder runs aircraft u's air-to-ground order (called by the scout system each tick it flies
    // free, for an aircraft without an airStrike weapon): an attack order at a ground unit or an
    // attack-ground order at a point. It reports whether it handled u's order (false: not air-to-ground).
    private bool StrikeRunOrder(Unit u, ScoutFlight f)
    {
        var kind = u.Order.Kind;
        if (kind is not (OrderKind.Attack or OrderKind.AttackGround))
        {
            u.Strike = null;
            return false;
        }
        Unit? t = null;
        if (kind == OrderKind.Attack)
        {
            t = Unit(u.Order.Target);
            if (t is not null && (t.Def.Archetype.Flight is not null || t.Def.Archetype.AirBody is not null))
            {
                u.Strike = null;
                return false; // an air target: air-to-air (E6)
            }
            if (t is not { Alive: true, Inside: 0 } || t.Owner == u.Owner)
            {
                EndStrike(u); // dead or inside something: the strike ends, its task resumes
                return true;
            }
            if (!IsVisibleTo(t, u.Owner))
            {
                // Out of its side's sight (its runs take it away): it attacks where the target was last
                // seen, for at most UnseenTicks in a row (a strike ordered at an unseen target never starts).
                if (u.Strike is not { } r0 || r0.Target != t.Id || ++r0.Unseen > UnseenTicks)
                {
                    EndStrike(u);
                    return true;
                }
            }
            else if (u.Strike is { } r1)
            {
                r1.Unseen = 0;
            }
        }
        var w = AirGroundWeapon(u, t);
        if (u.Homing || w is null)
        {
            EndStrike(u); // home to refuel, or out of the weapons it needs: back to its task afterwards
            return true;
        }
        if (u.Reload > 0 && !HasAirWeapons(u.Def))
        {
            u.Reload = Math.Max(0, u.Reload - 100); // an aircraft with air weapons reloads in the air defence system
        }
        var run = u.Strike;
        if (run is null || run.Target != (t?.Id ?? 0) || (t is null && run.Cell != u.Order.Cell))
        {
            run = u.Strike = new StrikeRun { Target = t?.Id ?? 0, Cell = t is null ? u.Order.Cell : default };
            run.Aim = AimOf(run, t);
            StartPass(u, run, w);
        }
        else if (run.Weapon != w.Index && run.Phase != StrikePhase.Egress)
        {
            StartPass(u, run, w); // its bombs are gone: the gun for the next pass
        }
        run.Aim = AimOf(run, t);
        switch (run.Phase)
        {
            case StrikePhase.Approach:
                StrikeApproach(u, f, run);
                break;
            case StrikePhase.RunIn:
                StrikeRunIn(u, run, w, t);
                break;
            default:
                if (--run.Ticks <= 0)
                {
                    run.Passes++;
                    StartPass(u, run, w); // a re-attack while ordered and armed
                }
                break;
        }
        return true;
    }

    // EndStrike ends u's air-to-ground order: back to its task (the mission resumes).
    private static void EndStrike(Unit u)
    {
        u.Strike = null;
        u.Idle();
    }

    // AimOf is the point the run aims at: the point ordered; a unit's position, a moving one led by its
    // step times the fall time of the last release solution.
    private Vec AimOf(StrikeRun run, Unit? t)
    {
        if (t is null)
        {
            return run.Target == 0 ? run.Cell.Center() : run.Aim;
        }
        if (run.Unseen > 0)
        {
            return run.Aim; // out of sight: where it was last seen
        }
        if (t.Vel == default || t.Def.IsStructure || run.Kind == PassKind.Strafe)
        {
            return t.Pos;
        }
        return OnMap(t.Pos.X.Raw + t.Vel.X.Raw * run.FallTicks, t.Pos.Y.Raw + t.Vel.Y.Raw * run.FallTicks);
    }

    // StartPass begins a pass with weapon w: the profile's release distance, the IP's distance, the run-in
    // line from the aim point toward where u is now (straight away from its heading when it is over it).
    private void StartPass(Unit u, StrikeRun run, WeaponDef w)
    {
        run.Weapon = w.Index;
        run.Kind = KindOfPass(w);
        run.Phase = StrikePhase.Approach;
        run.Released = 0;
        run.Ticks = 0;
        run.ReleaseDist = NominalRelease(u, w, run.Kind);
        run.IpDist = w.Effect.Delivery is { } d ? d.IpDistance.Raw : StrafeIpRaw;
        long dx = u.Pos.X.Raw - run.Aim.X.Raw, dy = u.Pos.Y.Raw - run.Aim.Y.Raw;
        var l = Len3(dx, dy, 0);
        if (l == 0)
        {
            (dx, dy) = (-u.Vel.X.Raw, -u.Vel.Y.Raw);
            l = Len3(dx, dy, 0);
        }
        (run.DirX, run.DirY) = l == 0 ? (1000, 0) : ((int)(dx * 1000 / l), (int)(dy * 1000 / l));
    }

    // NominalRelease is the horizontal distance from the aim point at which weapon w's profile releases
    // (raw): a guided bomb's reach, a gun's range; a free-fall bomb's fall over flat ground from its
    // release height (a toss: its ingress height) at its attack speed (a toss: its dash), level, down the
    // dive angle, or climbing at the toss angle (at most its climb rate).
    private static long NominalRelease(Unit u, WeaponDef w, PassKind kind)
    {
        if (kind == PassKind.Strafe)
        {
            return StrafeRangeOf(w);
        }
        if (kind == PassKind.Guided || w.Effect.Delivery is not { } d)
        {
            return w.Range.Raw;
        }
        var f = u.Def.Archetype.Flight!;
        var g = f.Wing;
        var s = g is null ? u.Def.Speed.Raw * TicksPerSecond : kind == PassKind.Toss && g.HasAfterburner ? g.Dash : g.Military;
        var h = kind == PassKind.Toss ? (g is { IngressRaw: > 0 } ? g.IngressRaw : f.HeightRaw) : d.ReleaseHeightRaw;
        var vz = kind switch
        {
            PassKind.Dive => -s * TanPermille(d.DiveAngle) / 1000,
            PassKind.Toss => Math.Min(s * TanPermille(d.TossAngle) / 1000, f.ClimbPerTick * TicksPerSecond),
            _ => 0,
        };
        var k = new BombKin { Z = h, VX = s, VZ = vz };
        for (var i = 0; i < MaxFallTicks && k.Z > 0; i++)
        {
            k.Step(d.GravityRaw / TicksPerSecond);
        }
        return Math.Max(0, k.X);
    }

    // IpOf is the run's initial point: on its run-in line, its release distance plus the IP's distance from the aim point.
    private static Vec IpOf(StrikeRun run)
    {
        var r = run.ReleaseDist + run.IpDist;
        return P(run.Aim.X.Raw + run.DirX * r / 1000, run.Aim.Y.Raw + run.DirY * r / 1000);
    }

    // StrikeApproach: to the IP; within its capture of it, at the aim point. The run-in starts once the
    // aim point is within the release and IP distances (plus the capture) and within 45 degrees of the nose.
    private static void StrikeApproach(Unit u, ScoutFlight f, StrikeRun run)
    {
        long ax = run.Aim.X.Raw - u.Pos.X.Raw, ay = run.Aim.Y.Raw - u.Pos.Y.Raw;
        var d = Len3(ax, ay, 0);
        if (d <= run.ReleaseDist + run.IpDist + StrikeCapture(u, f) && NoseCos(u, ax, ay, d) >= RunInCos)
        {
            run.Phase = StrikePhase.RunIn;
            run.Released = 0;
        }
    }

    // StrikeCapture is how near the IP counts as reached: its turn radius at its speed plus 2 cells.
    private static long StrikeCapture(Unit u, ScoutFlight f) =>
        (f.Wing is { } g ? g.Radius(AirSpeedOf(u)) : ScoutFlight.TurnRadiusRaw(u.Def.Speed.Raw, f.TurnPermille)) + CaptureSlackRaw;

    // NoseCos is the cosine (thousandths) between u's level heading and the offset (ax, ay) of length d.
    private static long NoseCos(Unit u, long ax, long ay, long d)
    {
        var (vx, vy) = u.Wing is { } st ? (st.VelX, st.VelY) : (u.Vel.X.Raw, u.Vel.Y.Raw);
        var vl = Len3(vx, vy, 0);
        return vl == 0 || d == 0 ? 1000 : (vx * ax + vy * ay) * 1000 / (vl * d);
    }

    // StrikeRunIn flies the run-in's release (a bomb's release solution, a guided bomb's reach, the gun's bursts).
    private void StrikeRunIn(Unit u, StrikeRun run, WeaponDef w, Unit? t)
    {
        long ax = run.Aim.X.Raw - u.Pos.X.Raw, ay = run.Aim.Y.Raw - u.Pos.Y.Raw;
        var d = Len3(ax, ay, 0);
        var cos = NoseCos(u, ax, ay, d);
        switch (run.Kind)
        {
            case PassKind.Strafe:
                if (cos < 0 || d < Fixed.OneRaw)
                {
                    StartEgress(u, run); // over the target and past it: pull off
                    return;
                }
                var slant = Len3(ax, ay, u.Altitude.Raw - GroundRaw(run.Aim));
                var gun = w.Effect.AirGun!;
                var every = gun.StrafeEvery > 0 ? gun.StrafeEvery : w.Reload;
                if (slant <= StrafeRangeOf(w) && cos >= CosDeg(gun.Cone) && (run.Released == 0 ? u.Reload == 0 : Tick - run.LastRelease >= every))
                {
                    FireStrafe(u, run, w, t, slant);
                }
                return;
            case PassKind.Guided:
                if (cos < 0)
                {
                    StartEgress(u, run); // passed it without a release: come round again
                    return;
                }
                if (u.Reload == 0 && d <= w.Range.Raw && cos >= GuidedReleaseCos)
                {
                    ReleaseGuided(u, run, w, t);
                    StartEgress(u, run);
                }
                return;
        }
        if (run.Kind == PassKind.Toss && u.Wing is { } st)
        {
            st.Boost = Math.Max(st.Boost, 2); // low and fast: the afterburner on the run-in
        }
        var dl = w.Effect.Delivery!;
        if (run.Released > 0)
        {
            // A stick: the next bomb once it has flown the spacing since the last.
            if (run.Released < dl.Stick && (Tick - run.LastRelease) * AirSpeedOf(u) / TicksPerSecond >= dl.StickSpacing.Raw &&
                HasAmmo(u, w) && Predict(KinOf(u), dl.GravityRaw / TicksPerSecond, out var sp, out var st2))
            {
                ReleaseBomb(u, run, w, t, sp, st2);
            }
            if (run.Released >= dl.Stick || !HasAmmo(u, w))
            {
                StartEgress(u, run);
            }
            return;
        }
        if (cos < 0)
        {
            StartEgress(u, run); // the target fell behind: an abort, come round again
            return;
        }
        var k = KinOf(u);
        if (!Predict(k, dl.GravityRaw / TicksPerSecond, out var imp, out var ticks))
        {
            return;
        }
        run.FallTicks = ticks;
        run.Aim = AimOf(run, t);
        var vl = Math.Max(1, Len3(k.VX, k.VY, 0));
        long ex = run.Aim.X.Raw - imp.X.Raw, ey = run.Aim.Y.Raw - imp.Y.Raw;
        var along = (ex * k.VX + ey * k.VY) / vl;
        var cross = Math.Abs(ex * k.VY - ey * k.VX) / vl;
        var half = vl / TicksPerSecond / 2;
        if (along < -half - AbortPastRaw)
        {
            StartEgress(u, run); // past it: no bomb this pass, come round again
            return;
        }
        // The profile must be flown: a dive releases diving, a toss climbing, a level run at (most of)
        // its release height over the aim point's ground.
        var ready = run.Kind switch
        {
            PassKind.Dive => u.VelZ.Raw < 0,
            PassKind.Toss => u.VelZ.Raw > 0,
            _ => (u.Altitude.Raw - GroundRaw(run.Aim)) * 10 >= dl.ReleaseHeightRaw * 8,
        };
        if (along > half || !ready)
        {
            return; // short of the aim point still (or not yet in its profile): fly on
        }
        if (cross > AbortCrossRaw + ScatterOf(dl, run.Kind).Raw)
        {
            StartEgress(u, run); // off the line: no bomb this pass
            return;
        }
        ReleaseBomb(u, run, w, t, imp, ticks);
        if (run.Released >= dl.Stick || !HasAmmo(u, w))
        {
            StartEgress(u, run);
        }
    }

    // StrafeRangeOf is the slant range gun w opens fire from on a strafing pass.
    private static long StrafeRangeOf(WeaponDef w) => w.Effect.AirGun is { StrafeRange.Raw: > 0 } g ? g.StrafeRange.Raw : w.Range.Raw;

    // ScatterOf is delivery d's scatter for a pass of kind k.
    private static Fixed ScatterOf(Delivery d, PassKind k) => k switch
    {
        PassKind.Dive => d.ScatterDive,
        PassKind.Toss => d.ScatterToss,
        _ => d.ScatterLevel,
    };

    // StartEgress ends the pass: the afterburner, the egress heading (the run-in heading turned away
    // toward the map's centre) and its countdown.
    private void StartEgress(Unit u, StrikeRun run)
    {
        run.Phase = StrikePhase.Egress;
        run.Ticks = EgressTicks;
        var (vx, vy) = u.Wing is { } st ? (st.VelX, st.VelY) : (u.Vel.X.Raw, u.Vel.Y.Raw);
        var vl = Len3(vx, vy, 0);
        if (vl == 0)
        {
            (vx, vy, vl) = (-run.DirX, -run.DirY, 1000);
        }
        long cx = (long)Grid.Width * Fixed.OneRaw / 2 - u.Pos.X.Raw, cy = (long)Grid.Height * Fixed.OneRaw / 2 - u.Pos.Y.Raw;
        var side = vx * cy - vy * cx >= 0 ? 1 : -1; // toward the centre (clockwise positive); exactly zero: right
        long c = CosDeg(EgressTurnDeg), s = CosDeg(90 - EgressTurnDeg) * side;
        long ex = (vx * c - vy * s) / 1000, ey = (vx * s + vy * c) / 1000;
        var el = Math.Max(1, Len3(ex, ey, 0));
        (run.DirX, run.DirY) = ((int)(ex * 1000 / el), (int)(ey * 1000 / el)); // the egress heading (the next pass sets the run-in line again)
        if (u.Wing is { } w)
        {
            w.Boost = Math.Max(w.Boost, EgressTicks);
        }
    }

    // StrikeReleaseDue: on bingo fuel during a run-in it finishes the release when that is at most BingoFinishTicks away.
    private static bool StrikeReleaseDue(Unit u)
    {
        if (u.Strike is not { Phase: StrikePhase.RunIn } run)
        {
            return false;
        }
        var d = Len3(run.Aim.X.Raw - u.Pos.X.Raw, run.Aim.Y.Raw - u.Pos.Y.Raw, 0);
        return d - run.ReleaseDist <= AirSpeedOf(u) * BingoFinishTicks / TicksPerSecond;
    }

    // ---- Steering and height (the mission layer's aim, the fixed wing's height) ----

    // StrikeSteer is where u steers on its strike: the IP on the approach (the aim point once within its
    // capture), the aim point on the run-in, the egress heading on the egress.
    private Vec StrikeSteer(Unit u, ScoutFlight f, StrikeRun run)
    {
        switch (run.Phase)
        {
            case StrikePhase.Approach:
                var ip = IpOf(run);
                return Within(u.Pos, ip, Fixed.FromRaw(StrikeCapture(u, f))) ? run.Aim : OnMap(ip.X.Raw, ip.Y.Raw);
            case StrikePhase.RunIn:
                return run.Aim;
            default:
                var far = 10L * Fixed.OneRaw;
                return OnMap(u.Pos.X.Raw + run.DirX * far / 1000, u.Pos.Y.Raw + run.DirY * far / 1000);
        }
    }

    // StrikeAltitude is the height (absolute, raw) fixed wing u wants on its strike, at pos over ground
    // `under`; want is its ordinary ingress height. Level and guided: the release height over the aim
    // point's ground from within a pop-up's reach of the IP; dive: the roll-in height (the release height
    // plus the IP's distance up the dive line), then down the line at the dive angle through the release
    // point; toss: low, then all out up from a second before its release distance; strafe: down a
    // 12-degree line toward the target (the floor keeps it up); egress: its flight height.
    private long StrikeAltitude(Unit u, ScoutFlight f, FixedWingSpec g, StrikeRun run, Vec pos, long under, long want)
    {
        if (run.Phase == StrikePhase.Egress)
        {
            return Math.Max(want, under + f.HeightRaw);
        }
        var w = u.Def.Weapons[run.Weapon];
        var aimGround = GroundRaw(run.Aim);
        var d = Len3(run.Aim.X.Raw - pos.X.Raw, run.Aim.Y.Raw - pos.Y.Raw, 0);
        var step = Math.Max(1, AirSpeedOf(u) / TicksPerSecond);
        var reach = run.ReleaseDist + run.IpDist;
        if (run.Kind == PassKind.Strafe)
        {
            if (run.Phase != StrikePhase.RunIn)
            {
                return want;
            }
            return Math.Min(want, aimGround + d * TanPermille(StrafeAngleDeg) / 1000);
        }
        var dl = w.Effect.Delivery!;
        if (run.Kind == PassKind.Toss)
        {
            return run.Phase == StrikePhase.RunIn && d <= run.ReleaseDist + step * TossPullUpTicks ? under + 100L * 512 : want;
        }
        var top = aimGround + dl.ReleaseHeightRaw;
        if (run.Kind == PassKind.Dive)
        {
            top += run.IpDist * TanPermille(dl.DiveAngle) / 1000; // the IP's distance up the dive line (raw: 512 a metre both ways)
        }
        var pop = Math.Max(0, top - want) / Math.Max(1, f.ClimbPerTick) * step; // the run-up a pop-up needs
        if (run.Phase == StrikePhase.Approach && d > reach + pop)
        {
            return want;
        }
        if (run.Kind == PassKind.Dive && run.Phase == StrikePhase.RunIn)
        {
            var line = aimGround + dl.ReleaseHeightRaw + (d - run.ReleaseDist) * TanPermille(dl.DiveAngle) / 1000;
            return Math.Min(top, line);
        }
        return top;
    }

    // ---- The release solution and the bombs ----

    // BombKin is a bomb's kinematics (raw; velocity raw a second, the twentieth's remainders carried).
    private struct BombKin
    {
        public long X, Y, Z, VX, VY, VZ, RX, RY, RZ;

        // Step: gravity off the vertical velocity, then a twentieth of the velocity (remainders carried).
        public void Step(long gravityTick)
        {
            VZ -= gravityTick;
            long tx = VX + RX, ty = VY + RY, tz = VZ + RZ;
            long sx = tx / TicksPerSecond, sy = ty / TicksPerSecond, sz = tz / TicksPerSecond;
            (RX, RY, RZ) = (tx - sx * TicksPerSecond, ty - sy * TicksPerSecond, tz - sz * TicksPerSecond);
            (X, Y, Z) = (X + sx, Y + sy, Z + sz);
        }
    }

    // KinOf is a bomb released by u now: its position and height, its velocity (a fixed wing's exact one).
    private static BombKin KinOf(Unit u)
    {
        var (vx, vy) = u.Wing is { } st ? (st.VelX, st.VelY) : (u.Vel.X.Raw * TicksPerSecond, u.Vel.Y.Raw * TicksPerSecond);
        return new BombKin { X = u.Pos.X.Raw, Y = u.Pos.Y.Raw, Z = u.Altitude.Raw, VX = vx, VY = vy, VZ = u.VelZ.Raw * TicksPerSecond };
    }

    // Predict follows a bomb from k by the bomb's own step until it meets the ground: where (interpolated
    // within the tick) and after how many ticks. False when it never does within MaxFallTicks.
    private bool Predict(BombKin k, long gravityTick, out Vec at, out int ticks)
    {
        for (var i = 1; i <= MaxFallTicks; i++)
        {
            var prev = k;
            k.Step(gravityTick);
            if (k.Z <= GroundRaw(OnMap(k.X, k.Y)))
            {
                at = GroundPoint(prev, k, out _);
                ticks = i;
                return true;
            }
        }
        (at, ticks) = (default, 0);
        return false;
    }

    // GroundPoint is where the step from a to b meets the ground (linear within the tick) and the ground's height there.
    private Vec GroundPoint(BombKin a, BombKin b, out long ground)
    {
        var h0 = Math.Max(0, a.Z - GroundRaw(OnMap(a.X, a.Y)));
        var h1 = Math.Min(0, b.Z - GroundRaw(OnMap(b.X, b.Y)));
        var span = h0 - h1;
        var fr = span > 0 ? h0 * 1024 / span : 1024;
        var at = OnMap(a.X + (b.X - a.X) * fr / 1024, a.Y + (b.Y - a.Y) * fr / 1024);
        ground = GroundRaw(at);
        return at;
    }

    // ReleaseBomb drops one free-fall bomb of w from u: the predicted point and fall time from the release
    // solution, the profile's scatter (seeded, on the release velocity), a round, the reload, the event.
    private void ReleaseBomb(Unit u, StrikeRun run, WeaponDef w, Unit? t, Vec predicted, int ticks)
    {
        var dl = w.Effect.Delivery!;
        var k = KinOf(u);
        _nextId++;
        var id = _nextId;
        var s = ScatterOf(dl, run.Kind).Raw;
        long dx = 0, dy = 0;
        if (s > 0)
        {
            var seed = Match.Setup.Seed;
            dx = (long)(Rand.Hash(seed, Tick, u.Id, id | ScatterXSalt) % (ulong)(2 * s + 1)) - s;
            dy = (long)(Rand.Hash(seed, Tick, u.Id, id | ScatterYSalt) % (ulong)(2 * s + 1)) - s;
            k.VX += dx * TicksPerSecond / Math.Max(1, ticks);
            k.VY += dy * TicksPerSecond / Math.Max(1, ticks);
        }
        var landing = OnMap(predicted.X.Raw + dx, predicted.Y.Raw + dy);
        var tier = StrikeTier(w.Damage);
        var p = new Projectile(id, u, w, landing, t?.Id ?? 0)
        {
            Altitude = u.Altitude,
            Vel = P(k.VX / TicksPerSecond, k.VY / TicksPerSecond),
            VelZ = Fixed.FromRaw(k.VZ / TicksPerSecond),
            Bomb = new BombFall
            {
                VelX = k.VX, VelY = k.VY, VelZ = k.VZ, GravityTick = dl.GravityRaw / TicksPerSecond,
                Predicted = landing, ImpactTick = Tick + (uint)ticks, Tier = tier,
            },
        };
        _projectiles.Add(p);
        SpendRound(u, w);
        run.Released++;
        run.LastRelease = Tick;
        _bombReleases.Add(new BombRelease(id, u.Id, w.Index, run.Kind, t?.Id ?? 0, u.Pos, u.Altitude, k.VX, k.VY, k.VZ, landing, Tick + (uint)ticks, tier));
    }

    // SpendRound takes a round of w off u and starts its reload.
    private void SpendRound(Unit u, WeaponDef w)
    {
        if (w.Stock >= 0)
        {
            u.MutableCharges[w.Stock]--;
        }
        u.Reload = w.Reload * 100;
        u.LastShot = Tick;
    }

    // ReleaseGuided drops one guided bomb of w from u at t (or the point): the hit roll at release (the
    // Watcher's model: hitIdle on a target that stood still, hitMoving on one that moved; a point stands
    // still), then it glides at its target, or beside it (missNear to missFar off: behind a moving target,
    // else a seeded direction).
    private void ReleaseGuided(Unit u, StrikeRun run, WeaponDef w, Unit? t)
    {
        var gb = w.Effect.Delivery!.Guided!;
        _nextId++;
        var id = _nextId;
        var seed = Match.Setup.Seed;
        var moving = t is not null && t.Vel != default;
        var chance = moving ? gb.HitMoving : gb.HitIdle;
        var hit = (int)(Rand.Hash(seed, Tick, u.Id, id | GuidedSalt) % 100) < chance;
        var aim = t?.Pos ?? run.Aim;
        if (!hit)
        {
            var f0 = t is not null && Facing.TryFromDirection(new Vec(-t.Vel.X, -t.Vel.Y), out var back) ? back
                : (int)(Rand.Hash(seed, Tick, u.Id, id | GuidedDirSalt) % Facing.Count);
            var spread = (int)(Rand.Hash(seed, Tick, u.Id, id | GuidedDirSalt) % 9) - 4;
            var dir = Facing.Dir(moving ? f0 + spread : f0);
            var span = gb.MissFar.Raw - gb.MissNear.Raw;
            var dist = gb.MissNear.Raw + (long)(Rand.Hash(seed, Tick, u.Id, id | GuidedFarSalt) % (ulong)(span + 1));
            aim = OnMap(aim.X.Raw + dir.X.Raw * dist / Fixed.OneRaw, aim.Y.Raw + dir.Y.Raw * dist / Fixed.OneRaw);
        }
        var speed = Math.Max(1, w.Effect.ProjectileSpeed.Raw);
        var eta = (uint)(Len3(aim.X.Raw - u.Pos.X.Raw, aim.Y.Raw - u.Pos.Y.Raw, u.Altitude.Raw - GroundRaw(aim)) / speed + 1);
        var tier = StrikeTier(w.Damage);
        var (vx, vy) = u.Wing is { } st ? (st.VelX, st.VelY) : (u.Vel.X.Raw * TicksPerSecond, u.Vel.Y.Raw * TicksPerSecond);
        var p = new Projectile(id, u, w, aim, t?.Id ?? 0)
        {
            Altitude = u.Altitude,
            Bomb = new BombFall { VelX = vx, VelY = vy, Guided = true, Hit = hit, Predicted = aim, ImpactTick = Tick + eta, Tier = tier },
        };
        _projectiles.Add(p);
        SpendRound(u, w);
        run.Released++;
        run.LastRelease = Tick;
        _bombReleases.Add(new BombRelease(id, u.Id, w.Index, PassKind.Guided, t?.Id ?? 0, u.Pos, u.Altitude, vx, vy, u.VelZ.Raw * TicksPerSecond, aim, Tick + eta, tier));
    }

    // StepBomb moves a bomb one tick (the projectile system): a free fall by the bomb's step, going off
    // where it meets the ground or on the first roof, vehicle or soldier it reaches; a guided bomb glides.
    private void StepBomb(Projectile p, BombFall b)
    {
        if (b.Guided)
        {
            StepGlide(p, b);
            return;
        }
        var a = new BombKin { X = p.Pos.X.Raw, Y = p.Pos.Y.Raw, Z = p.Altitude.Raw, VX = b.VelX, VY = b.VelY, VZ = b.VelZ, RX = b.RemX, RY = b.RemY, RZ = b.RemZ };
        var k = a;
        k.Step(b.GravityTick);
        var q = OnMap(k.X, k.Y);
        (p.Vel, p.VelZ) = (P(k.X - a.X, k.Y - a.Y), Fixed.FromRaw(k.Z - a.Z));
        if (k.Z <= GroundRaw(q))
        {
            var at = GroundPoint(a, k, out var ground);
            BombBlast(p, b, at, ground, DiveHit(p, at, ground));
            return;
        }
        if (DiveHit(p, q, k.Z) is var hit and not 0)
        {
            BombBlast(p, b, q, k.Z, hit); // a roof, a vehicle or a soldier on the way down
            return;
        }
        p.Pos = q;
        p.Altitude = Fixed.FromRaw(k.Z);
        (b.VelX, b.VelY, b.VelZ, b.RemX, b.RemY, b.RemZ) = (k.VX, k.VY, k.VZ, k.RX, k.RY, k.RZ);
    }

    // StepGlide moves a guided bomb one tick along a straight 3D line at its weapon's speed: at its target
    // while the release roll hit and the target is out on the map, else at its aim point; there it goes off.
    private void StepGlide(Projectile p, BombFall b)
    {
        var t = b.Hit ? Unit(p.Target) : null;
        if (t is { Alive: true, Inside: 0, Garrison: 0 })
        {
            b.Predicted = t.Pos;
        }
        else
        {
            t = null;
        }
        var aim = b.Predicted;
        var aimAlt = GroundRaw(aim);
        long dx = aim.X.Raw - p.Pos.X.Raw, dy = aim.Y.Raw - p.Pos.Y.Raw, dz = aimAlt - p.Altitude.Raw;
        var len = Len3(dx, dy, dz);
        var speed = Math.Max(1, p.Weapon.Effect.ProjectileSpeed.Raw);
        if (len <= speed)
        {
            (p.Vel, p.VelZ) = (P(dx, dy), Fixed.FromRaw(dz));
            BombBlast(p, b, aim, aimAlt, t?.Id ?? DiveHit(p, aim, aimAlt));
            return;
        }
        (p.Vel, p.VelZ) = (P(dx * speed / len, dy * speed / len), Fixed.FromRaw(dz * speed / len));
        (b.VelX, b.VelY, b.VelZ) = (p.Vel.X.Raw * TicksPerSecond, p.Vel.Y.Raw * TicksPerSecond, p.VelZ.Raw * TicksPerSecond);
        p.Pos = OnMap(p.Pos.X.Raw + p.Vel.X.Raw, p.Pos.Y.Raw + p.Vel.Y.Raw);
        p.Altitude = Fixed.FromRaw(p.Altitude.Raw + p.VelZ.Raw);
    }

    // BombBlast sets bomb p off at `at` (height alt), on unit hit (0: the ground): the direct hit, the
    // splash, the runway effect, the reports.
    private void BombBlast(Projectile p, BombFall b, Vec at, long alt, uint hit)
    {
        p.Pos = at;
        p.Altitude = Fixed.FromRaw(alt);
        p.Done = true;
        var w = p.Weapon;
        _hurtBy = Unit(p.Shooter); // for Return fire and retaliation (Hurt)
        var t = Unit(hit);
        if (t is not null && w.Warhead.CanHit(ArmorOf(t)))
        {
            Hurt(t, w.Warhead, w.Damage, 100);
        }
        if (w.Effect.Splash.Raw > 0)
        {
            Explode(at, w.Damage, w.Warhead, w.Effect.Splash, hit, w.Effect.SplashCenter, w.Effect.SplashEdge, w.Effect.SplashInner, w.Effect.SplashOuterPercent);
        }
        _hurtBy = null;
        var crater = CraterKind.None;
        if (w.Effect.Runway is { } re)
        {
            foreach (var af in _airfields)
            {
                if (OnRunwayStrip(af, at, out _, out _))
                {
                    crater = re.Crater;
                    break;
                }
            }
        }
        _detonations.Add(new Detonation(p.Id, at, hit, false));
        RunwayBlast(at, w.Effect.Runway); // craters and wear (AirfieldDamageSystem.cs)
        _bombImpacts.Add(new BombImpact(p.Id, p.Shooter, p.Owner, w.Index, at, Fixed.FromRaw(alt), w.Damage, b.Tier, w.Warhead.Id, hit,
            t?.Def.IsStructure ?? false, crater, b.Guided));
    }

    // ---- Strafing ----

    // FireStrafe fires one burst of u's gun w on its strafing pass: a splash hit (the gun's splash) where
    // the burst lands, walking along its track from StrafeWalkRaw short (at full range) to as far long
    // (overhead), with a seeded jitter across it; a round, the reload, the event.
    private void FireStrafe(Unit u, StrikeRun run, WeaponDef w, Unit? t, long slant)
    {
        var (vx, vy) = u.Wing is { } st ? (st.VelX, st.VelY) : (u.Vel.X.Raw, u.Vel.Y.Raw);
        var vl = Math.Max(1, Len3(vx, vy, 0));
        var range = Math.Max(1, StrafeRangeOf(w));
        var along = StrafeWalkRaw * (2 * (range - Math.Min(slant, range)) * 1000 / range - 1000) / 1000;
        var j = (long)(Rand.Hash(Match.Setup.Seed, Tick, u.Id, (t?.Id ?? 0) | StrafeSalt) % (ulong)(2 * StrafeJitterRaw + 1)) - StrafeJitterRaw;
        var at = OnMap(run.Aim.X.Raw + (vx * along - vy * j) / vl, run.Aim.Y.Raw + (vy * along + vx * j) / vl);
        _hurtBy = u;
        if (w.Effect.Splash.Raw > 0)
        {
            Explode(at, w.Damage, w.Warhead, w.Effect.Splash, 0, w.Effect.SplashCenter, w.Effect.SplashEdge, w.Effect.SplashInner, w.Effect.SplashOuterPercent);
        }
        else if (t is not null && Within(t.Pos, at, t.Def.Archetype.Radius) && w.Warhead.CanHit(ArmorOf(t)))
        {
            Hurt(t, w.Warhead, w.Damage, 100);
        }
        _hurtBy = null;
        SpendRound(u, w);
        run.Released++;
        run.LastRelease = Tick;
        _strafeBursts.Add(new StrafeBurst(u.Id, t?.Id ?? 0, u.Pos, u.Altitude, at, Fixed.FromRaw(GroundRaw(at)), w.Index));
    }
}
