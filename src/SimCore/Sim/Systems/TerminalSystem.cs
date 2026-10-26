using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Missiles with a terminal profile (weapon terminal; the hypersonic missile, docs/design.md, "Hypersonic missile truck"). The
// whole flight is a closed form of the missile's age (ticks since launch), so the sim and a renderer drawing it agree exactly
// (FlightPointAt) and the impact tick is known from the launch (the incoming warning):
//  - the climb: ClimbTicks straight up off the launcher, rising ClimbRaw evenly;
//  - the arc: from the top of the climb toward its point (the weapon's arc percent over the chord, the ballistic missile's
//    parabola), levelled off at CruiseRaw over the launcher's ground once it gets there and kept level until the dive (a short
//    shot whose arc stays lower flies the arc), along the straight line from the launcher to the point, at the weapon's
//    projectile speed;
//  - the terminal dive: over the last DiveFrom of the way, a straight line from its height there down onto its point, at
//    DiveSpeed (the tick that crosses into it covers the rest of the tick at the dive speed), weaving across its line: a
//    swing of up to WeaveRaw in Weaves whole waves under an envelope that
//    is 0 where the dive starts and at the impact (parabolas, integers; the side and 50 to 100% of the swing seeded per shot
//    from the match seed and its ID, no RNG draw). Nothing can engage it once it dives (Evades, the air defence system).
// On terrain the arc can meet a hill (it goes off there); within DescentCells of its point it hits the first unit or
// structure under it, like any lobbed round, else the ground at its point. Hashed state: its age and weave seed (and the arc's
// origin and heights, as any arcing projectile); everything else is derived.
public sealed partial class World
{
    private readonly List<IncomingEvent> _incomingEvents = [];
    private readonly List<IncomingStrike> _incoming = []; // scratch for IncomingStrikes (not state)

    /// <summary>What the missiles with a terminal profile did this tick (launches, warnings, dives, impacts, interceptions), in order.</summary>
    public IReadOnlyList<IncomingEvent> IncomingEvents => _incomingEvents;

    /// <summary>
    /// The incoming strikes being warned of now (a missile with a terminal profile within its warning time of the impact), in
    /// projectile ID order: its point, its blast radius, the tick it lands and the players warned (every player but the
    /// shooter's). Rebuilt on each call from the missiles in flight; nothing here is game state.
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
            }
            return _incoming;
        }
    }

    /// <summary>
    /// Where a missile with a terminal profile is (or will be) at tick <paramref name="tick"/>: its position (the weave in it),
    /// its height (absolute, raw: 512 a metre) and its phase, the same closed form its flight steps through; or null for any other
    /// projectile. Tick <see cref="Tick"/> is where it is now; <paramref name="permille"/> thousandths of a tick past
    /// <paramref name="tick"/> give the points between ticks on the same curve (a smooth weave). Ticks before its launch give the
    /// launch point, ticks past its impact its point.
    /// </summary>
    public FlightPoint? FlightPointAt(Projectile p, uint tick, int permille = 0)
    {
        if (p.Weapon.Effect.Terminal is not { } tf)
        {
            return null;
        }
        var am = ((long)p.Age + tick - Tick) * 1000 + Math.Clamp(permille, 0, 999);
        return TerminalPoint(p, tf, Math.Clamp(am, 0, TerminalImpactAge(p, tf) * 1000L));
    }

    /// <summary>The tick a missile with a terminal profile lands (if nothing stops it), or null for any other projectile.</summary>
    public uint? ImpactTickOf(Projectile p) =>
        p.Weapon.Effect.Terminal is { } tf ? Tick + (uint)Math.Max(0, TerminalImpactAge(p, tf) - p.Age - 1) : null;

    /// <summary>A missile with a terminal profile's phase now (climb, cruise on its arc, terminal dive), or null for any other projectile.</summary>
    public static TerminalPhase? TerminalPhaseOf(Projectile p) => p.Weapon.Effect.Terminal is { } tf ? PhaseAt(p, tf, p.Age) : null;

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

    // LaunchTerminal sets a new missile with a terminal profile off from shooter: its line, the arc's heights (from the top of
    // its climb, absolute) and its weave seed.
    private void LaunchTerminal(Projectile p, Unit shooter, TerminalFlight tf)
    {
        p.Origin = shooter.Pos;
        p.LaunchHeight = Fixed.FromRaw(GroundRaw(shooter.Pos) + LaunchHeightRaw(p.Weapon) + tf.ClimbRaw);
        p.LandHeight = Fixed.FromRaw(GroundRaw(p.Dest) + LandRaw);
        p.ArcRise = Fixed.FromRaw(ArcRiseRaw(p.Weapon, (p.Dest - p.Origin).Len().Raw));
        p.Arcs = true;
        p.Altitude = Fixed.FromRaw(p.LaunchHeight.Raw - tf.ClimbRaw);
        p.WeaveSeed = (int)(Rng.Rand.Hash(Match.Setup.Seed, p.Id, 0x7E5, 0x5EED) & 0x7FFF_FFFF);
    }

    // TerminalAlong is how far along its line (raw, from the launcher) a missile with a terminal profile is at age am (in
    // thousandths of a tick): 0 in the climb, then the cruise speed up to the dive's start and the dive speed after it (a tick
    // crossing into the dive covers the rest at the dive speed), at most the whole way. At whole ticks it is exact integers.
    private static long TerminalAlong(Projectile p, TerminalFlight tf, long am, long total)
    {
        var um = am - tf.ClimbTicks * 1000L;
        if (um <= 0)
        {
            return 0;
        }
        long vc = Math.Max(1, p.Weapon.Effect.ProjectileSpeed.Raw), vd = tf.DiveSpeed.Raw;
        var cruise = total - Math.Min(tf.DiveFrom.Raw, total);
        var reach = um * vc / 1000;
        var s = reach <= cruise ? reach : cruise + (reach - cruise) * vd / vc;
        return Math.Min(s, total);
    }

    // TerminalImpactAge is the age (ticks) at which it reaches its point: the first tick its way along reaches the whole way.
    internal static int TerminalImpactAge(Projectile p, TerminalFlight tf)
    {
        var total = (p.Dest - p.Origin).Len().Raw;
        long vc = Math.Max(1, p.Weapon.Effect.ProjectileSpeed.Raw), vd = tf.DiveSpeed.Raw;
        var dive = Math.Min(tf.DiveFrom.Raw, total);
        var u = Math.Max(1, (total - dive + (dive * vc + vd - 1) / vd + vc - 1) / vc); // an estimate, then exact by the closed form
        while (u > 1 && TerminalAlong(p, tf, (tf.ClimbTicks + u - 1) * 1000, total) >= total)
        {
            u--;
        }
        while (TerminalAlong(p, tf, (tf.ClimbTicks + u) * 1000, total) < total)
        {
            u++;
        }
        return (int)(tf.ClimbTicks + u);
    }

    private static TerminalPhase PhaseAt(Projectile p, TerminalFlight tf, int age)
    {
        if (age <= tf.ClimbTicks)
        {
            return TerminalPhase.Climb;
        }
        var total = (p.Dest - p.Origin).Len().Raw;
        var dive = Math.Min(tf.DiveFrom.Raw, total);
        return dive > 0 && TerminalAlong(p, tf, age * 1000L, total) >= total - dive ? TerminalPhase.Dive : TerminalPhase.Cruise;
    }

    // TerminalHeight is its height (absolute, raw) s along its line: before the dive its arc (the ballistic parabola from the top
    // of its climb to its point) but never above its cruise height; in the dive a straight line from where the dive starts down
    // to its point.
    private static long TerminalHeight(Projectile p, TerminalFlight tf, long s, long total)
    {
        var dive = Math.Min(tf.DiveFrom.Raw, total);
        var cruise = total - dive;
        if (s > cruise && dive > 0)
        {
            var from = TerminalHeight(p, tf, cruise, total);
            return from + (p.LandHeight.Raw - from) * (s - cruise) / dive;
        }
        var arc = ArcAt(p, s, total);
        if (tf.CruiseRaw == 0)
        {
            return arc;
        }
        // Levelled off: once the arc reaches its cruise height it stays there until the dive (a short shot whose arc stays
        // below it flies the whole arc).
        var top = p.LaunchHeight.Raw;
        var ceiling = Math.Max(top, top - tf.ClimbRaw - LaunchHeightRaw(p.Weapon) + tf.CruiseRaw); // over the launcher's ground
        var rise = p.ArcRise.Raw;
        var apex = rise <= 0 ? 0 : Math.Clamp(total / 2 + (p.LandHeight.Raw - top) * total / (8 * rise), 0, total);
        return s <= apex ? Math.Min(arc, ceiling) : ArcAt(p, apex, total) >= ceiling ? ceiling : arc;
    }

    // ArcAt is the ballistic parabola's height s along a line of length total: from the top of its climb to its point.
    private static long ArcAt(Projectile p, long s, long total)
    {
        var top = p.LaunchHeight.Raw;
        return total == 0 ? p.LandHeight.Raw : top + (p.LandHeight.Raw - top) * s / total + 4 * p.ArcRise.Raw * s / total * (total - s) / total;
    }

    // TerminalPoint is the flight's closed form at age am (thousandths of a tick; see the file's header).
    private static FlightPoint TerminalPoint(Projectile p, TerminalFlight tf, long am)
    {
        var top = p.LaunchHeight.Raw;
        if (am <= tf.ClimbTicks * 1000L)
        {
            var bottom = top - tf.ClimbRaw;
            return new FlightPoint(p.Origin, Fixed.FromRaw(bottom + tf.ClimbRaw * Math.Max(0, am) / Math.Max(1, tf.ClimbTicks * 1000L)), TerminalPhase.Climb);
        }
        var d = p.Dest - p.Origin;
        var total = d.Len().Raw;
        if (total == 0)
        {
            return new FlightPoint(p.Dest, p.LandHeight, TerminalPhase.Dive);
        }
        var s = TerminalAlong(p, tf, am, total);
        var alt = TerminalHeight(p, tf, s, total);
        long x = p.Origin.X.Raw + d.X.Raw * s / total, y = p.Origin.Y.Raw + d.Y.Raw * s / total;
        var dive = Math.Min(tf.DiveFrom.Raw, total);
        var phase = TerminalPhase.Cruise;
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
        if (a1 > tf.ClimbTicks)
        {
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
    // or below the ground, or null.
    private Vec? TerminalHill(Projectile p, TerminalFlight tf, long s0, long s1, long total)
    {
        if (Ground is not { } g || s1 <= s0 || total == 0)
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
        if (p.Weapon.Effect.Terminal is null)
        {
            return;
        }
        _incomingEvents.Add(new IncomingEvent(intercepted ? IncomingEventKind.Intercepted : IncomingEventKind.Struck, p.Id, p.Owner,
            intercepted ? p.Pos : p.Dest, Tick, WarnedOf(p.Owner)));
    }
}

/// <summary>A missile with a terminal profile's phase (<see cref="World.TerminalPhaseOf"/>).</summary>
public enum TerminalPhase : byte
{
    /// <summary>Straight up off its launcher.</summary>
    Climb,

    /// <summary>On its arc toward its point: the Patriot and THAAD types may engage it.</summary>
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
}

/// <summary>An event of a missile with a terminal profile (<see cref="World.IncomingEvents"/>).</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Projectile">The missile's projectile ID.</param>
/// <param name="Owner">The shooter's player.</param>
/// <param name="At">Its point (for Intercepted: where it was brought down).</param>
/// <param name="ImpactTick">The tick it lands on (for Struck and Intercepted: this tick).</param>
/// <param name="Warned">A bit (1 &lt;&lt; player) for each player warned.</param>
public readonly record struct IncomingEvent(IncomingEventKind Kind, uint Projectile, byte Owner, Vec At, uint ImpactTick, uint Warned);
