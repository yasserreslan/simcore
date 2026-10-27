using SimCore.Model;

namespace SimCore.Sim;

// The radar-cued lock (the mobile radar; docs/design.md, "Mobile radar"). A unit with a cueLock (content: a percent) and an
// air vision is a cueing radar: set up, everything it tracks is tracked for its side like any air sensor's (team cueing,
// CollectAirCues), and on top a ground air defence weapon of its side whose target it tracks locks in cueLock percent of
// its own lock time (the shortest of its side's cueing radars that track the target). It never adds range: the shooter
// still picks and fires only within its own weapon's reach (InSight). Nothing here is state: the cue table is the air
// defence system's scratch, rebuilt each tick it runs; the queries below recompute from hashed state.
public sealed partial class World
{
    // LockTicksFor is the lock time (ticks) ground air defence unit u needs on air target p this tick: its weapon's lock
    // time scaled by the target's signature (LockTicksOn), then by the best cueLock of its side's cueing radars tracking p.
    internal int LockTicksFor(Unit u, Interceptor ic, AirBody p)
    {
        var lockTicks = LockTicksOn(ic, p, BatteryCounterStealth(ic));
        var cue = CueLockOn(u.Owner, p);
        return cue == 100 ? lockTicks : (int)((long)lockTicks * cue / 100);
    }

    // CueLockOn is the lowest cueLock percent among player owner's cueing radars set up and tracking air target p this tick,
    // or 100 when none does (valid after CollectAirCues).
    private int CueLockOn(byte owner, AirBody p)
    {
        if (_cueRadars.Count == 0 || !CuedFor(owner, p.Id, cue: true))
        {
            return 100;
        }
        var best = 100;
        foreach (var (s, eye) in _cueRadars)
        {
            if (s.Owner == owner && s.Def.Archetype.CueLock < best && Senses(s, eye, p, SensorDist2(s, eye, p)))
            {
                best = s.Def.Archetype.CueLock;
            }
        }
        return best;
    }

    /// <summary>
    /// Whether ground air defence unit <paramref name="u"/> is radar-cued now: its current air target is tracked by a
    /// cueing radar of its side (a set-up mobile radar), so it locks in that radar's cueLock percent of its lock time.
    /// For the client's cue line or lock marker; valid after air defence ran this tick (as <see cref="AirTracked"/>).
    /// </summary>
    public bool RadarCued(Unit u) =>
        u.AirTarget != 0 && u.Def.Archetype.Flight is null && InterceptorWeapon(u.Def) >= 0 && CuedFor(u.Owner, u.AirTarget, cue: true);

    /// <summary>
    /// The lock time (ticks) ground air defence unit <paramref name="u"/> needs on its current air target this tick, the
    /// radar cue included (compare <see cref="Unit.TrackTicks"/>); 0 when it tracks nothing. Valid after air defence ran.
    /// </summary>
    public int AirLockTicks(Unit u)
    {
        var wi = u.Def.Archetype.Flight is null ? InterceptorWeapon(u.Def) : -1;
        if (u.AirTarget == 0 || wi < 0 || AirBodyById(u.AirTarget) is not { } p)
        {
            return 0;
        }
        return LockTicksFor(u, u.Def.Weapons[wi].Effect.Interceptor!, p);
    }

    /// <summary>
    /// The ground air defence units of <paramref name="radar"/>'s side whose current air target this cueing radar tracks
    /// now, in ID order: the shooters it cues (they lock faster). Empty when it is not a cueing radar (no cueLock) or is off
    /// (packed up, moving, dead, inside). Read-only; recomputed from the world, valid at any time.
    /// </summary>
    public List<Unit> RadarCuedShooters(Unit radar)
    {
        var list = new List<Unit>();
        if (radar.Def.Archetype.CueLock == 0 || !RadarOn(radar))
        {
            return list;
        }
        var eye = SensorEyeRaw(radar);
        foreach (var u in _units)
        {
            if (u.Owner != radar.Owner || !u.Alive || u.AirTarget == 0 || u.Def.Archetype.Flight is not null || InterceptorWeapon(u.Def) < 0)
            {
                continue;
            }
            if (AirBodyById(u.AirTarget) is { } p && p.Owner != radar.Owner && DetectsAir(radar, eye, p))
            {
                list.Add(u);
            }
        }
        return list;
    }

    /// <summary>
    /// Where <paramref name="radar"/>'s search antenna points now, in thousandths of a turn clockwise from north (0 to 999),
    /// for renderers: one turn every <see cref="Archetype.SweepTicks"/> ticks, phased by its ID, the same on every machine.
    /// -1 when it has no sweep or is off (packed up, moving, dead, inside: the antenna is still).
    /// </summary>
    public int RadarSweep(Unit radar)
    {
        var period = radar.Def.Archetype.SweepTicks;
        if (period == 0 || !RadarOn(radar))
        {
            return -1;
        }
        return (int)((Tick + radar.Id * 7L) % period * 1000 / period);
    }

    /// <summary>
    /// Where <paramref name="radar"/>'s air sensor sees from (absolute height, raw: 512 a metre): a radar's top (its
    /// antenna), an air defence weapon's launcher, a soldier's eye; the sweep's origin for renderers. 0 without an air vision.
    /// </summary>
    public long RadarEyeRaw(Unit radar) => radar.Def.Archetype.AirVision.Raw == 0 ? 0 : SensorEyeRaw(radar);

    // RadarOn: an air sensor's radar works now: alive, built, not rubble, not inside, on the ground set up (a unit that sets
    // up: the radar truck, the mobile radar), not grounded if it flies.
    // An emitter in the Hide stance has switched its radar off (LongRangeRadarSystem.cs).
    private bool RadarOn(Unit s) =>
        s.Alive && s.IsBuilt && !s.Rubble && s.Inside == 0 && SetUp(s) && (s.Def.Archetype.Flight is null || !Grounded(s)) && !RadarSilent(s);
}
