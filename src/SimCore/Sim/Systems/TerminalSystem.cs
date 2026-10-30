using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Missiles with a terminal profile (weapon terminal; the hypersonic missile, docs/design.md, "Hypersonic missile truck"). The
// whole flight is a closed form of the missile's age (ticks since launch), so the sim and a renderer drawing it agree exactly
// (FlightPointAt) and the impact tick is known from the launch (the incoming warning). One smooth arc along the straight line from
// the launcher to its point, at the weapon's projectile speed over the ground (its slope is continuous everywhere):
//  - the rise: from its launch height up to its peak (PeakPercent of the ground range over the higher of the launcher and its
//    point, between PeakMinRaw and PeakMaxRaw), PeakAtPercent of the way to the dive's start; a cubic easing out, so it leaves
//    the launcher steeply (three times the rise over its length) and is level at the peak. Its first ClimbTicks are the Climb
//    phase, the rest of the arc the Cruise phase;
//  - the fall: from the peak down to the separation point, DiveFrom from its point and DiveFrom x DiveSlopePermille / 1000 over
//    it, a parabola level at the peak;
//  - the terminal dive: over the last DiveFrom of the way, at DiveSpeed (the tick that crosses into it covers the rest of the tick
//    at the dive speed), a parabola from the fall's slope down onto its point, steeper than its chord at the end, weaving across
//    its line: a swing of up to WeaveRaw in Weaves whole waves under an envelope that is 0 where the dive starts and at the impact
//    (parabolas, integers; the side and 50 to 100% of the swing seeded per shot from the match seed and its ID, no RNG draw).
//    Nothing can engage it once it dives (Evades, the air defence system).
// A vertical start (BoostTicks, the Jericho type's missile): over its first T ticks (BoostTicks, fewer on a short shot so the
// boost covers at most half the way to the peak) its way along is vc t^3 / (3 T^2) (its speed over the ground grows from 0 to
// the projectile speed vc as the square of the time) and its height rises B (t / T)^2: straight up off the launcher, pitching
// over onto the arc; the rise then starts where the boost ends (vc T / 3 along, B up) and B is set so the velocity is
// continuous there (B = k vc T / 2, k the rise's first slope; Boost). Derived from the hashed state: nothing new is hashed.
// On terrain the arc can meet a hill (it goes off there); within DescentCells of its point it hits the first unit or structure
// under it, like any lobbed round, else the ground at its point. Hashed state: its age and weave seed, its origin, its launch and
// land heights and its peak (ArcRise: the peak's absolute height for this profile; hashed as any arcing projectile's); everything
// else is derived.
public sealed partial class World
{
    private readonly List<IncomingEvent> _incomingEvents = [];
    private readonly List<IncomingStrike> _incoming = []; // scratch for IncomingStrikes (not state)

    /// <summary>What the missiles with a terminal profile did this tick (launches, warnings, dives, impacts, interceptions), in order.</summary>
    public IReadOnlyList<IncomingEvent> IncomingEvents => _incomingEvents;

    /// <summary>
    /// The incoming strikes being warned of now (a missile with a terminal profile within its warning time of the impact; a cruise
    /// missile an enemy player's sensors saw, while they see it and its warning time after; a bomb whose delivery warns, the MOAB type
    /// bomb, from its release to its impact), in projectile ID order: its point, its
    /// blast radius, the tick it lands and the players warned (every player but the shooter's; for a cruise missile only those whose
    /// sensors saw it). Rebuilt on each call from the missiles in flight; nothing here is game state.
    /// </summary>
    public IReadOnlyList<IncomingStrike> IncomingStrikes
    {
        get
        {
            _incoming.Clear();
            foreach (var p in _projectiles)
            {
                if (p.Weapon.Effect.Terminal is { } tf && !p.Done && TerminalImpactAge(p, tf) - p.Age is var left && left <= tf.WarnTicks)
                {
                    _incoming.Add(new IncomingStrike(p.Id, p.Owner, p.Shooter, p.Dest, p.Weapon.Effect.Splash, Tick + (uint)Math.Max(0, left - 1),
                        WarnedOf(p.Owner)));
                }
                else if (p.Bomb is { } b && !p.Done && p.Weapon.Effect.Delivery is { Warn: true })
                {
                    // A bomb that warns (the MOAB type bomb): every enemy player from its release to its impact.
                    _incoming.Add(new IncomingStrike(p.Id, p.Owner, p.Shooter, p.Dest, p.Weapon.Effect.Splash, b.ImpactTick, WarnedOf(p.Owner)));
                }
                else if (p.Cruise is { } c && !p.Done && WarnedNow(c) is var warned && warned != 0)
                {
                    // A cruise missile: only the players whose sensors saw it, while they do and its warning time after (CruiseSystem.cs).
                    _incoming.Add(new IncomingStrike(p.Id, p.Owner, p.Shooter, p.Dest, p.Weapon.Effect.Splash,
                        Tick + (uint)Math.Max(0, c.ImpactAge - p.Age - 1), warned));
                }
            }
            return _incoming;
        }
    }

    /// <summary>
    /// Where a missile with a terminal profile is (or will be) at tick <paramref name="tick"/>: its position (the weave in it),
    /// its height (absolute, raw: 512 a metre) and its phase, the same closed form its flight steps through; for a cruise missile the
    /// same from the flight planned at its launch (the boost is Climb, the final dive Dive; between ticks the straight line from one
    /// to the next); or null for any other projectile. Tick <see cref="Tick"/> is where it is now; <paramref name="permille"/> thousandths of a tick past
    /// <paramref name="tick"/> give the points between ticks on the same curve (a smooth weave). Ticks before its launch give the
    /// launch point, ticks past its impact its point.
    /// </summary>
    public FlightPoint? FlightPointAt(Projectile p, uint tick, int permille = 0)
    {
        if (p.Cruise is { } c)
        {
            return CruisePoint(c, ((long)p.Age + tick - Tick) * 1000 + Math.Clamp(permille, 0, 999)); // a cruise missile's plan (CruiseSystem.cs)
        }
        if (p.Weapon.Effect.Terminal is not { } tf)
        {
            return null;
        }
        var am = ((long)p.Age + tick - Tick) * 1000 + Math.Clamp(permille, 0, 999);
        return TerminalPoint(p, tf, Math.Clamp(am, 0, TerminalImpactAge(p, tf) * 1000L));
    }

    /// <summary>The tick a missile with a terminal profile lands (if nothing stops it), or null for any other projectile.</summary>
    public uint? ImpactTickOf(Projectile p) =>
        p.Cruise is { } c ? Tick + (uint)Math.Max(0, c.ImpactAge - p.Age - 1)
        : p.Weapon.Effect.Terminal is { } tf ? Tick + (uint)Math.Max(0, TerminalImpactAge(p, tf) - p.Age - 1) : null;

    /// <summary>A missile with a terminal profile's phase now (its climb off the launcher, the rest of its arc, the terminal dive), or null for any other projectile.</summary>
    public static TerminalPhase? TerminalPhaseOf(Projectile p) =>
        p.Cruise is { } c ? CruisePhase(c, p.Age) : p.Weapon.Effect.Terminal is { } tf ? PhaseAt(p, tf, p.Age) : null;

    // Evades: air target b is a missile in its terminal dive: nothing engages it, and missiles already at it lose it.
    internal static bool Evades(AirBody b) => b.P is { Weapon.Effect.Terminal: { } tf } p && PhaseAt(p, tf, p.Age) == TerminalPhase.Dive;

    private uint WarnedOf(byte owner)
    {
        uint bits = 0;
        foreach (var pl in _players)
        {
            if (pl.Id != owner)
            {
                bits |= Bit(pl.Id);
            }
        }
        return bits;
    }

    // LaunchTerminal sets a new missile with a terminal profile off from shooter: its line, its launch and land heights, its peak
    // (absolute, in ArcRise) and its weave seed.
    private void LaunchTerminal(Projectile p, Unit shooter, TerminalFlight tf)
    {
        p.Origin = shooter.Pos;
        p.LaunchHeight = Fixed.FromRaw(GroundRaw(shooter.Pos) + LaunchHeightRaw(p.Weapon));
        p.LandHeight = Fixed.FromRaw(GroundRaw(p.Dest) + LandRaw);
        p.ArcRise = Fixed.FromRaw(PeakRaw(tf, (p.Dest - p.Origin).Len().Raw, p.LaunchHeight.Raw, p.LandHeight.Raw));
        p.Arcs = true;
        p.Altitude = p.LaunchHeight;
        p.WeaveSeed = (int)(Rng.Rand.Hash(Match.Setup.Seed, p.Id, 0x7E5, 0x5EED) & 0x7FFF_FFFF);
    }

    /// <summary>
    /// A terminal profile's peak (absolute, raw) over a ground range of <paramref name="totalRaw"/> from a launch height of
    /// <paramref name="launchRaw"/> to a point at <paramref name="landRaw"/>: PeakPercent of the range over the higher of the two,
    /// at least PeakMinRaw and at most PeakMaxRaw (the lowest peak is above the dive's start: the compiler checks it).
    /// </summary>
    internal static long PeakRaw(TerminalFlight tf, long totalRaw, long launchRaw, long landRaw) =>
        Math.Max(launchRaw, landRaw) + Math.Clamp(totalRaw * tf.PeakPercent / 100, tf.PeakMinRaw, tf.PeakMaxRaw);

    // Boost is a vertical start's ticks T (0: none), its way along at its end (raw) and its rise B (raw) on a line of length
    // total (see the file's header): T at most BoostTicks and at most what keeps its way (vc T / 3) within half the way to the
    // peak; B = 3 R D / (2 L + 3 D), R the peak over the launch height, D = vc T, L the rise's length after the boost (so the
    // boost's last vertical speed, 2 B / T, is the rise's first, 3 (R - B) / L x vc).
    private static (long T, long Along, long Rise) Boost(Projectile p, TerminalFlight tf, long total)
    {
        if (tf.BoostTicks == 0)
        {
            return (0, 0, 0);
        }
        var vc = Math.Max(1, p.Weapon.Effect.ProjectileSpeed.Raw);
        var apex = (total - Math.Min(tf.DiveFrom.Raw, total)) * tf.PeakAtPercent / 100;
        var t = Math.Min(tf.BoostTicks, apex * 3 / 2 / vc);
        if (t <= 0)
        {
            return (0, 0, 0);
        }
        var along = vc * t / 3;
        var d = vc * t;
        var rise = (long)((Int128)3 * (p.ArcRise.Raw - p.LaunchHeight.Raw) * d / (2 * (apex - along) + 3 * d));
        return (t, along, rise);
    }

    // TerminalAlong is how far along its line (raw, from the launcher) a missile with a terminal profile is at age am (in
    // thousandths of a tick): a vertical start's growing speed over its boost (Boost), the projectile speed up to the dive's start
    // and the dive speed after it (a tick crossing into the dive covers the rest at the dive speed), at most the whole way. At
    // whole ticks it is exact integers.
    private static long TerminalAlong(Projectile p, TerminalFlight tf, long am, long total)
    {
        if (am <= 0)
        {
            return 0;
        }
        long vc = Math.Max(1, p.Weapon.Effect.ProjectileSpeed.Raw), vd = tf.DiveSpeed.Raw;
        var arc = total - Math.Min(tf.DiveFrom.Raw, total);
        var (t, along, _) = Boost(p, tf, total);
        var reach = t > 0 && am < t * 1000
            ? (long)((Int128)vc * am * am * am / ((Int128)3 * t * t * 1_000_000_000))
            : along + (am - t * 1000) * vc / 1000;
        var s = reach <= arc ? reach : arc + (reach - arc) * vd / vc;
        return Math.Min(s, total);
    }

    // TerminalImpactAge is the age (ticks) at which it reaches its point: the first tick its way along reaches the whole way.
    internal static int TerminalImpactAge(Projectile p, TerminalFlight tf)
    {
        var total = (p.Dest - p.Origin).Len().Raw;
        long vc = Math.Max(1, p.Weapon.Effect.ProjectileSpeed.Raw), vd = tf.DiveSpeed.Raw;
        var dive = Math.Min(tf.DiveFrom.Raw, total);
        var (t, along, _) = Boost(p, tf, total);
        var u = Math.Max(1, (total - dive + (dive * vc + vd - 1) / vd + vc - 1) / vc + t - along / vc); // an estimate, then exact by the closed form
        while (u > 1 && TerminalAlong(p, tf, (u - 1) * 1000, total) >= total)
        {
            u--;
        }
        while (TerminalAlong(p, tf, u * 1000, total) < total)
        {
            u++;
        }
        return (int)u;
    }

    // PhaseAt is its phase at a whole age: the dive over its last DiveFrom, else the Climb for its first ClimbTicks, else Cruise.
    private static TerminalPhase PhaseAt(Projectile p, TerminalFlight tf, int age)
    {
        var total = (p.Dest - p.Origin).Len().Raw;
        var dive = Math.Min(tf.DiveFrom.Raw, total);
        if (dive > 0 && TerminalAlong(p, tf, age * 1000L, total) >= total - dive)
        {
            return TerminalPhase.Dive;
        }
        return age <= tf.ClimbTicks ? TerminalPhase.Climb : TerminalPhase.Cruise;
    }

    // TerminalHeight is its height (absolute, raw) s along its line of length total (see the file's header): the rise to the
    // peak (a cubic easing out), the fall to the separation point (a parabola), the dive onto its point (a parabola from the
    // fall's slope). Integers (Int128 for the products): monotonic up to the peak and down after it, exact at the joins. With a
    // vertical start the rise starts where the boost ends (its way along, its rise over the launch height); within the boost's
    // way it gives the boost's top (the boost's height is a function of the age: TerminalPoint).
    private static long TerminalHeight(Projectile p, TerminalFlight tf, long s, long total)
    {
        var (_, along, rise) = Boost(p, tf, total);
        long h0 = p.LaunchHeight.Raw + rise, g = p.LandHeight.Raw, peak = p.ArcRise.Raw;
        if (s >= total)
        {
            return g;
        }
        var dive = Math.Min(tf.DiveFrom.Raw, total);
        var arc = total - dive;
        if (arc == 0)
        {
            return h0 + (g - h0) * Math.Max(0, s) / dive; // shorter than the dive: a straight line down
        }
        var top = g + dive * tf.DiveSlopePermille / 1000; // the separation point's height
        var apex = arc * tf.PeakAtPercent / 100;
        if (s > arc)
        {
            // The dive: h = top - m x - c x^2 / dive^2, m the fall's slope at the separation point, c bringing it onto its point.
            var x = s - arc;
            long mNum = arc > apex ? 2 * (peak - top) : 0, mDen = Math.Max(1, arc - apex);
            var lin = (long)((Int128)x * mNum / mDen);
            var c = top - g - (long)((Int128)dive * mNum / mDen);
            return c < 0 ? top - (top - g) * x / dive : top - lin - (long)((Int128)c * x * x / ((Int128)dive * dive));
        }
        if (s <= apex)
        {
            var r = apex - Math.Max(along, s);
            var l = apex - along;
            return peak - (long)((Int128)(peak - h0) * r * r * r / ((Int128)l * l * l));
        }
        var v = s - apex;
        var len = arc - apex;
        return peak - (long)((Int128)(peak - top) * v * v / ((Int128)len * len));
    }

    // FlightHeightAt is its height (absolute, raw) s along its line (TerminalHeight; for tests and tools).
    internal static long FlightHeightAt(Projectile p, TerminalFlight tf, long s) => TerminalHeight(p, tf, s, (p.Dest - p.Origin).Len().Raw);

    // TerminalPoint is the flight's closed form at age am (thousandths of a tick; see the file's header).
    private static FlightPoint TerminalPoint(Projectile p, TerminalFlight tf, long am)
    {
        var d = p.Dest - p.Origin;
        var total = d.Len().Raw;
        if (total == 0)
        {
            return new FlightPoint(p.Dest, p.LandHeight, TerminalPhase.Dive);
        }
        var s = TerminalAlong(p, tf, am, total);
        var (t, _, rise) = Boost(p, tf, total);
        var alt = t > 0 && am < t * 1000
            ? p.LaunchHeight.Raw + (long)((Int128)rise * am * am / ((Int128)t * t * 1_000_000)) // the boost: B (t / T)^2
            : TerminalHeight(p, tf, s, total);
        long x = p.Origin.X.Raw + d.X.Raw * s / total, y = p.Origin.Y.Raw + d.Y.Raw * s / total;
        var dive = Math.Min(tf.DiveFrom.Raw, total);
        var phase = am <= tf.ClimbTicks * 1000L ? TerminalPhase.Climb : TerminalPhase.Cruise;
        if (dive > 0 && s >= total - dive)
        {
            phase = TerminalPhase.Dive;
            var off = WeaveOffset(p, tf, s - (total - dive), dive);
            (x, y) = (x - d.Y.Raw * off / total, y + d.X.Raw * off / total); // across its line (to its left for a positive swing)
        }
        return new FlightPoint(new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y)), Fixed.FromRaw(alt), phase);
    }

    // WeaveOffset is the dive's swing across its line (raw) at x into a dive of length dive: the swing (50 to 100% of WeaveRaw by
    // the seed, its side by the seed) times an envelope 4t(1-t) and a wave of Weaves whole periods, each half a parabola.
    private static long WeaveOffset(Projectile p, TerminalFlight tf, long x, long dive)
    {
        if (tf.Weaves == 0 || tf.WeaveRaw == 0)
        {
            return 0;
        }
        var t = Math.Clamp(x * 1000 / dive, 0, 1000);
        var envelope = 4 * t * (1000 - t) / 1000;
        var phase = t * tf.Weaves % 1000;
        var half = phase * 2 % 1000;
        var wave = 4 * half * (1000 - half) / 1000 * (phase < 500 ? 1 : -1);
        var swing = tf.WeaveRaw * (50 + p.WeaveSeed % 51) / 100 * ((p.WeaveSeed >> 8 & 1) == 0 ? 1 : -1);
        return swing * envelope / 1000 * wave / 1000;
    }

    // StepTerminal moves a missile with a terminal profile one tick along its closed form: its new point, height and last step
    // (air defence's fuse and lead read them), the events, a hill in its way, then (in its last DescentCells) the first unit or
    // structure under it, else the ground at its point when its way is done.
    private void StepTerminal(Projectile p, TerminalFlight tf)
    {
        var impact = TerminalImpactAge(p, tf);
        var a0 = p.Age;
        var a1 = a0 + 1;
        var was = TerminalPoint(p, tf, a0 * 1000L);
        var now = TerminalPoint(p, tf, a1 * 1000L);
        p.Age = a1;
        (p.Vel, p.VelZ) = (now.Pos - was.Pos, now.Altitude - was.Altitude);
        var left = impact - a1;
        if (a0 == 0)
        {
            _incomingEvents.Add(new IncomingEvent(IncomingEventKind.Launched, p.Id, p.Owner, p.Dest, Tick + (uint)left, WarnedOf(p.Owner)));
        }
        if (left <= tf.WarnTicks && (a0 == 0 || impact - a0 > tf.WarnTicks))
        {
            _incomingEvents.Add(new IncomingEvent(IncomingEventKind.Warned, p.Id, p.Owner, p.Dest, Tick + (uint)left, WarnedOf(p.Owner)));
        }
        if (now.Phase == TerminalPhase.Dive && was.Phase != TerminalPhase.Dive)
        {
            _incomingEvents.Add(new IncomingEvent(IncomingEventKind.Diving, p.Id, p.Owner, p.Dest, Tick + (uint)left, WarnedOf(p.Owner)));
        }
        var total = (p.Dest - p.Origin).Len().Raw;
        var s0 = TerminalAlong(p, tf, a0 * 1000L, total);
        var s1 = TerminalAlong(p, tf, a1 * 1000L, total);
        if (TerminalHill(p, tf, s0, s1, total) is { } hill)
        {
            p.Altitude = Fixed.FromRaw(GroundRaw(hill));
            Detonate(p, hill, 0, false); // into the hillside
            return;
        }
        var zone = total - (long)DescentCells * Fixed.OneRaw;
        if (s1 >= zone && FirstHit(p, LinePoint(p, Math.Max(s0, zone), total), LinePoint(p, s1, total), out var at, out var hit, out var terrain))
        {
            p.Altitude = now.Altitude;
            Detonate(p, at, hit, terrain);
            return;
        }
        p.Pos = now.Pos;
        p.Altitude = now.Altitude;
        if (a1 >= impact)
        {
            Detonate(p, p.Dest, 0, false); // the ground at its point
        }
    }

    private static Vec LinePoint(Projectile p, long s, long total)
    {
        var d = p.Dest - p.Origin;
        return total == 0 ? p.Dest
            : new Vec(Fixed.FromRaw(p.Origin.X.Raw + d.X.Raw * s / total), Fixed.FromRaw(p.Origin.Y.Raw + d.Y.Raw * s / total));
    }

    // TerminalHill: on terrain, the first point of its line from s0 to s1 (sampled TerrainSteps times a cell) where its arc is at
    // or below the ground, or null (never within a vertical start's boost: it climbs off its launcher).
    private Vec? TerminalHill(Projectile p, TerminalFlight tf, long s0, long s1, long total)
    {
        if (Ground is not { } g || total == 0)
        {
            return null;
        }
        s0 = Math.Max(s0, Boost(p, tf, total).Along);
        if (s1 <= s0)
        {
            return null;
        }
        var steps = (int)(((s1 - s0) * TerrainSteps + Fixed.OneRaw - 1) / Fixed.OneRaw);
        for (var k = 1; k <= steps; k++)
        {
            var s = s0 + (s1 - s0) * k / steps;
            var q = LinePoint(p, s, total);
            if (TerminalHeight(p, tf, s, total) <= g.HeightAt(q).Raw)
            {
                return q;
            }
        }
        return null;
    }

    // TerminalVelocity is its next tick's step (raw x y z): where the closed form takes it.
    internal static (long X, long Y, long Z) TerminalVelocity(Projectile p, TerminalFlight tf)
    {
        var a = TerminalPoint(p, tf, p.Age * 1000L);
        var b = TerminalPoint(p, tf, (p.Age + 1) * 1000L);
        return (b.Pos.X.Raw - a.Pos.X.Raw, b.Pos.Y.Raw - a.Pos.Y.Raw, b.Altitude.Raw - a.Altitude.Raw);
    }

    // NoteTerminalEnd records a missile with a terminal profile that went off (struck) or was shot down (intercepted).
    private void NoteTerminalEnd(Projectile p, bool intercepted)
    {
        if (p.Weapon.Effect.Terminal is null && p.Cruise is null)
        {
            return;
        }
        _incomingEvents.Add(new IncomingEvent(intercepted ? IncomingEventKind.Intercepted : IncomingEventKind.Struck, p.Id, p.Owner,
            intercepted ? p.Pos : p.Dest, Tick, p.Cruise is { } c ? WarnedNow(c) : WarnedOf(p.Owner)));
    }
}

/// <summary>A missile with a terminal profile's phase (<see cref="World.TerminalPhaseOf"/>).</summary>
public enum TerminalPhase : byte
{
    /// <summary>Off its launcher, the first ticks up its arc (a cruise missile: its boost straight up).</summary>
    Climb,

    /// <summary>On its arc toward its point (over its peak and down to the separation point): the Patriot and THAAD types may engage it.</summary>
    Cruise,

    /// <summary>The terminal dive, weaving: nothing can engage it.</summary>
    Dive,
}

/// <summary>A point of a missile's drawn flight (<see cref="World.FlightPointAt"/>).</summary>
/// <param name="Pos">Its map position (the weave in it).</param>
/// <param name="Altitude">Its height (absolute, raw: 512 a metre).</param>
/// <param name="Phase">Its phase there.</param>
public readonly record struct FlightPoint(Vec Pos, Fixed Altitude, TerminalPhase Phase);

/// <summary>An incoming strike being warned of (<see cref="World.IncomingStrikes"/>).</summary>
/// <param name="Projectile">The missile's projectile ID.</param>
/// <param name="Owner">The shooter's player.</param>
/// <param name="Shooter">The unit that fired it.</param>
/// <param name="At">Its point (where it lands if nothing stops it).</param>
/// <param name="Radius">Its blast's radius (the weapon's splash).</param>
/// <param name="ImpactTick">The tick it lands on (<see cref="World.Tick"/> during that step).</param>
/// <param name="Warned">A bit (1 &lt;&lt; player) for each player warned: every player but the shooter's.</param>
public readonly record struct IncomingStrike(uint Projectile, byte Owner, uint Shooter, Vec At, Fixed Radius, uint ImpactTick, uint Warned);

/// <summary>What happened to a missile with a terminal profile (<see cref="World.IncomingEvents"/>).</summary>
public enum IncomingEventKind : byte
{
    /// <summary>It left its launcher.</summary>
    Launched,

    /// <summary>Its warning starts (its warning time before the impact; at the launch for a shorter flight).</summary>
    Warned,

    /// <summary>It started its terminal dive: nothing can engage it now.</summary>
    Diving,

    /// <summary>It went off (its point, a unit or structure under it, or a hill on the way).</summary>
    Struck,

    /// <summary>An interceptor brought it down.</summary>
    Intercepted,

    /// <summary>A cruise missile: the sensors of the players in <see cref="IncomingEvent.Warned"/> saw it for the first time (their warning starts).</summary>
    Seen,
}

/// <summary>An event of a missile with a terminal profile (<see cref="World.IncomingEvents"/>).</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Projectile">The missile's projectile ID.</param>
/// <param name="Owner">The shooter's player.</param>
/// <param name="At">Its point (for Intercepted: where it was brought down).</param>
/// <param name="ImpactTick">The tick it lands on (for Struck and Intercepted: this tick).</param>
/// <param name="Warned">A bit (1 &lt;&lt; player) for each player warned.</param>
public readonly record struct IncomingEvent(IncomingEventKind Kind, uint Projectile, byte Owner, Vec At, uint ImpactTick, uint Warned);
