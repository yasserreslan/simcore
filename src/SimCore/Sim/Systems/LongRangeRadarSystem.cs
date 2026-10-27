using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The long-range radar's rules (the S-400 type battery; docs/design.md, "S-400 type air defence"). Three generic, opt-in archetype
// fields on an air sensor, each inert without its content:
//  - counterStealth (a percent): against this sensor every value of a target's signature moves that percent of the way to 100, so
//    the stealth bomber's detect 25 / lock 300 / kill 70 count as 62 / 200 / 85 at 50. A battery launcher (the interceptor's
//    battery block) locks and kills by the signature its radars see: the highest counterStealth among the archetypes that carry
//    the battery's radar tags (content only, worked out once per battery);
//  - radarHorizon: beyond its `beyond` (level distance) it does not see an air target lower than `below` over the ground under it
//    while the ground cuts the straight line from its antenna to the target (TerrainPhysics.LineOfSight); a flat map hides nothing;
//  - emitter: while its radar searches (RadarOn), on each sweep (the tick its antenna passes north: RadarSweep 0), every working
//    enemy unit with an air vision or a ground radar (not one with an excluded tag: the soldiers' eyes) whose sensor is within the
//    emitter's range (slant from its antenna) reveals it to its side: a RadarSpot (the stealth multirole's radar spotting, hashed)
//    with the emitter's disc, lasting the linger after the sweep. In the Hide stance an emitter's radar is off: it does not search,
//    cue, guide or give itself away (RadarSilent).
// Nothing here is new state: the reveals are RadarSpots; the horizon cache is per-tick scratch keyed by both positions.
public sealed partial class World
{
    // Whether any unit ever added is an emitter: while none is, the pass costs nothing. Not hashed (derived from the units).
    private bool _emitters;

    // Scratch, not state: per battery block (by reference), the counter-stealth of the radars it links to (content only).
    private readonly List<(Battery Battery, int Cs)> _batteryCs = [];

    // Scratch, not state: this tick's horizon checks (sensor, target, both positions and heights, the answer), so the cueing, the
    // battery, the semi-active guidance and the fog do not walk the same line again. Cleared when the tick changes.
    private readonly List<(uint S, long Sx, long Sy, long Tx, long Ty, long Alt, bool Masked)> _horizon = [];
    private long _horizonTick = -1;

    // Countered moves signature value v (a percent) cs percent of the way to 100 (integer, truncated toward zero).
    internal static int Countered(int v, int cs) => cs == 0 ? v : v + (100 - v) * cs / 100;

    // DetectRange is sensor s's air vision (raw) against a target of signature sig: the detect percent of it, moved toward 100 by
    // s's counter-stealth.
    private static long DetectRange(Unit s, Signature sig)
    {
        var reach = s.Def.Archetype.AirVision.Raw;
        var d = Countered(sig.Detect, s.Def.Archetype.CounterStealth);
        return d == 100 ? reach : reach * d / 100;
    }

    // Senses: sensor s (eye at eye) detects air target p, n its squared sensor distance to p: within its reach by the signature it
    // sees, and not hidden behind the ground by its radar horizon.
    private bool Senses(Unit s, long eye, AirBody p, long n) => SlantWithin(n, DetectRange(s, p)) && !Masked(s, eye, p.Pos, p.Alt);

    // RadarSilent: an emitter (the S-400 type radar) in the Hide stance has its radar off.
    internal static bool RadarSilent(Unit s) => s.Def.Archetype.Emitter is not null && s.Stance == Stance.Hide;

    // Masked: sensor s's radar horizon hides an air target at pos (height alt, absolute raw) from its eye now.
    private bool Masked(Unit s, long eye, Vec pos, long alt)
    {
        if (s.Def.Archetype.RadarHorizon is not { } h || Ground is null)
        {
            return false;
        }
        long dx = pos.X.Raw - s.Pos.X.Raw, dy = pos.Y.Raw - s.Pos.Y.Raw;
        if (dx * dx + dy * dy <= h.BeyondRaw * h.BeyondRaw || alt - GroundRaw(pos) >= h.BelowRaw)
        {
            return false; // near enough, or high enough: the ground never hides it
        }
        if (_horizonTick != Tick)
        {
            _horizonTick = Tick;
            _horizon.Clear();
        }
        foreach (var c in _horizon)
        {
            if (c.Sx == s.Pos.X.Raw && c.Sy == s.Pos.Y.Raw && c.Tx == pos.X.Raw && c.Ty == pos.Y.Raw && c.Alt == alt && c.S == s.Id)
            {
                return c.Masked;
            }
        }
        var masked = !LineOfSight(s.Pos, Fixed.FromRaw(eye), pos, Fixed.FromRaw(alt));
        _horizon.Add((s.Id, s.Pos.X.Raw, s.Pos.Y.Raw, pos.X.Raw, pos.Y.Raw, alt, masked));
        return masked;
    }

    // BatteryCounterStealth is the counter-stealth interceptor ic locks and kills by: for a battery launcher the highest
    // counterStealth among the air sensors that carry its radar tags (content only), else 0.
    private int BatteryCounterStealth(Interceptor ic)
    {
        if (ic.Battery is not { } b)
        {
            return 0;
        }
        foreach (var (bb, cs) in _batteryCs)
        {
            if (ReferenceEquals(bb, b))
            {
                return cs;
            }
        }
        var best = 0;
        foreach (var d in Match.Defs)
        {
            if (d.Archetype.AirVision.Raw != 0 && d.Archetype.Tags.HasAll(b.RadarTags))
            {
                best = Math.Max(best, d.Archetype.CounterStealth);
            }
        }
        _batteryCs.Add((b, best));
        return best;
    }

    // UpdateEmitters runs the emitter rule above for this tick (UpdateRadarSpots, before vision): emitters in ID order, enemy
    // sensors in ID order.
    private void UpdateEmitters()
    {
        foreach (var r in _units)
        {
            if (r.Def.Archetype.Emitter is not { } em || r.Owner == 0 || !RadarOn(r))
            {
                continue;
            }
            var period = r.Def.Archetype.SweepTicks;
            if (period > 0 && (Tick + r.Id * 7L) % period != 0)
            {
                continue; // between sweeps (RadarSweep's phase)
            }
            var eye = SensorEyeRaw(r);
            foreach (var s in _units)
            {
                var a = s.Def.Archetype;
                if (s.Owner == r.Owner || s.Owner == 0 || (a.AirVision.Raw == 0 && a.GroundRadar.Raw == 0) ||
                    (!em.ExcludeTags.IsEmpty && s.Def.Tags.Matches(em.ExcludeTags)) || !RadarOn(s))
                {
                    continue;
                }
                long dx = s.Pos.X.Raw - r.Pos.X.Raw, dy = s.Pos.Y.Raw - r.Pos.Y.Raw, dz = SensorEyeRaw(s) - eye;
                if (SlantWithin(dx * dx + dy * dy + dz * dz, em.RangeRaw))
                {
                    Spot(r, s.Id, s.Owner, em.Radius, em.LingerTicks);
                }
            }
        }
    }

    /// <summary>
    /// The counter-stealth percent unit definition <paramref name="d"/> sees air targets by: an air sensor's own
    /// <see cref="Archetype.CounterStealth"/> (the S-400 type radar: 50), or for a battery launcher the one its radars give its
    /// lock and kill chance (the S-400 type launcher: 50); 0 for everything else.
    /// </summary>
    public int CounterStealthOf(UnitDef d)
    {
        if (d.Archetype.CounterStealth != 0)
        {
            return d.Archetype.CounterStealth;
        }
        var wi = InterceptorWeapon(d);
        return wi >= 0 ? BatteryCounterStealth(d.Weapons[wi].Effect.Interceptor!) : 0;
    }

    /// <summary>
    /// The signature air target <paramref name="target"/> shows sensor (or battery launcher) <paramref name="sensor"/>: its own,
    /// each value moved toward 100 by <see cref="CounterStealthOf"/> (the stealth bomber to an S-400 type radar: 62 / 200 / 85).
    /// </summary>
    public Signature SignatureSeenBy(Unit sensor, Unit target)
    {
        var cs = CounterStealthOf(sensor.Def);
        var s = target.Signature;
        return cs == 0 ? s : new Signature(Countered(s.Detect, cs), Countered(s.Lock, cs), Countered(s.Kill, cs));
    }

    /// <summary>
    /// Whether <paramref name="radar"/>'s radar horizon hides air unit <paramref name="target"/> from it now: farther out than the
    /// horizon's reach, lower over the ground than its height, the line from the antenna cut by the ground. False for a radar
    /// without a horizon, a target that is not flying, or on a flat map. Read-only; for the client's marker.
    /// </summary>
    public bool BelowRadarHorizon(Unit radar, Unit target) =>
        radar.Def.Archetype.RadarHorizon is not null && AirBodyOfUnit(target) is { } b && Masked(radar, SensorEyeRaw(radar), b.Pos, b.Alt);

    /// <summary>
    /// Whether <paramref name="radar"/> searches now: an air sensor working (alive, built, not inside, set up, not in the Hide stance
    /// for an emitter). For the S-400 type radar this is when its array turns, it tracks, guides its battery and gives itself away.
    /// </summary>
    public bool RadarSearching(Unit radar) => radar.Def.Archetype.AirVision.Raw != 0 && RadarOn(radar);
}
