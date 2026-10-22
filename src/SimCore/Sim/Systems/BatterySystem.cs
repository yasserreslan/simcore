using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The battery link (the SA-6 launcher; docs/design.md, "SA-6 launcher"). An air defence weapon with a battery block has no
// sensor of its own: it engages only an air target that a linked radar tracks. A linked radar is a unit of the launcher's
// side with an air vision and every one of the battery's radar tags (the mobile radar), working (alive, built, not inside,
// set up: RadarOn), standing within the link radius (level distance) of the launcher; it tracks a target within its own air
// vision by the target's signature (as team cueing). With semiActive the missile homes on that radar's illumination: each
// tick of its flight some radar linked to its launch point must still track its target, else it loses its guidance for
// good and flies on unguided to its max flight (it cannot fuse: a miss). Nothing here is state: the radar list is scratch
// rebuilt on the first use in a tick from hashed state; the lost-guidance events are the tick's output, like AirBursts.
public sealed partial class World
{
    // Scratch, not state: the units whose definition could be a battery radar (an air vision, on the ground, the tags of some
    // battery in the content), with their antenna height, in ID order; rebuilt on first use each tick (_batteryRadarsTick).
    private readonly List<(Unit Unit, long Eye)> _batteryRadars = [];
    private long _batteryRadarsTick = -1;

    // Per unit definition (by index): whether it can be a battery radar (1), not (0), or not computed yet (-1). Content only.
    private sbyte[]? _batteryRadarDef;

    // Scratch for the launcher air defence is running now (RunAirDefense): its linked radars this tick.
    private readonly List<(Unit Unit, long Eye)> _links = [];

    // The semi-active missiles that lost their guidance this tick (their projectile IDs, in ID order): the tick's output.
    private readonly List<uint> _batteryLost = [];

    /// <summary>
    /// The semi-active missiles (an SA-6 launcher's) that lost their guidance in the last tick, by projectile ID in ID order: no
    /// radar linked to its launch point tracked its target any more (packed up, destroyed, out of its reach). Each flies on
    /// unguided (<see cref="Projectile.Guided"/> false) to its max flight and blows itself up there (an <see cref="AirBurst"/>
    /// of kind SelfDestruct), or meets the ground.
    /// </summary>
    public IReadOnlyList<uint> BatteryLost => _batteryLost;

    // BatteryRadars is this tick's list of possible battery radars (see _batteryRadars), built on first use.
    private List<(Unit Unit, long Eye)> BatteryRadars()
    {
        if (_batteryRadarsTick == Tick)
        {
            return _batteryRadars;
        }
        _batteryRadarsTick = Tick;
        _batteryRadars.Clear();
        foreach (var s in _units)
        {
            if (s.Def.Archetype.AirVision.Raw != 0 && s.Def.Archetype.Flight is null && IsBatteryRadarDef(s.Def) && RadarOn(s))
            {
                _batteryRadars.Add((s, SensorEyeRaw(s)));
            }
        }
        return _batteryRadars;
    }

    // IsBatteryRadarDef: definition d carries every radar tag of some battery in the content (computed once per definition).
    private bool IsBatteryRadarDef(UnitDef d)
    {
        if (_batteryRadarDef is null)
        {
            _batteryRadarDef = new sbyte[Match.Defs.Count];
            Array.Fill(_batteryRadarDef, (sbyte)-1);
        }
        if (d.Index >= _batteryRadarDef.Length)
        {
            return false;
        }
        if (_batteryRadarDef[d.Index] < 0)
        {
            _batteryRadarDef[d.Index] = 0;
            foreach (var other in Match.Defs)
            {
                foreach (var w in other.Weapons)
                {
                    if (w.Effect.Interceptor?.Battery is { } b && d.Archetype.Tags.HasAll(b.RadarTags))
                    {
                        _batteryRadarDef[d.Index] = 1;
                    }
                }
            }
        }
        return _batteryRadarDef[d.Index] == 1;
    }

    // Linked: radar s (from BatteryRadars) links to a launcher of player owner at `at` under battery b now.
    private static bool Linked(Unit s, byte owner, Vec at, Battery b) =>
        s.Owner == owner && s.Alive && s.Def.Archetype.Tags.HasAll(b.RadarTags) && WithinRaw(at, s.Pos, b.LinkRadiusRaw);

    // CollectLinks fills _links with launcher u's linked radars this tick (battery b); false when it has none (it holds fire).
    private bool CollectLinks(Unit u, Battery b)
    {
        _links.Clear();
        foreach (var r in BatteryRadars())
        {
            if (Linked(r.Unit, u.Owner, u.Pos, b) && RadarOn(r.Unit))
            {
                _links.Add(r);
            }
        }
        return _links.Count > 0;
    }

    // LinksTrack: one of the current launcher's linked radars (_links, CollectLinks) tracks air target p.
    private bool LinksTrack(AirBody p)
    {
        foreach (var (s, eye) in _links)
        {
            if (DetectsAir(s, eye, p))
            {
                return true;
            }
        }
        return false;
    }

    // LinkedTrack: a radar linked to a launcher of player owner at `at` (battery b) tracks air target p now.
    private bool LinkedTrack(byte owner, Vec at, Battery b, AirBody p)
    {
        foreach (var (s, eye) in BatteryRadars())
        {
            if (Linked(s, owner, at, b) && RadarOn(s) && DetectsAir(s, eye, p))
            {
                return true;
            }
        }
        return false;
    }

    // SemiActiveLost: semi-active missile p (battery b) still guided at target t has lost every linked radar's track: it goes
    // unguided for good (StepInterceptor then flies it on straight; it cannot fuse). The link is counted from its launch point.
    private bool SemiActiveLost(Projectile p, Battery b, AirBody t)
    {
        if (LinkedTrack(p.Owner, p.Origin, b, t))
        {
            return false;
        }
        p.Guided = false;
        _batteryLost.Add(p.Id);
        return true;
    }

    /// <summary>
    /// The radar <paramref name="launcher"/> (an SA-6 type launcher: its air defence weapon has a battery link) is linked to now:
    /// the nearest working radar of its side with the battery's radar tags within its link radius (ties to the lower ID), or
    /// null when it has none (it holds fire) or is no battery launcher. Read-only; recomputed from the world, valid at any time.
    /// </summary>
    public Unit? BatteryRadar(Unit launcher)
    {
        if (BatteryOf(launcher.Def) is not { } b)
        {
            return null;
        }
        Unit? best = null;
        long bestD = 0;
        foreach (var s in _units)
        {
            if (s.Def.Archetype.AirVision.Raw == 0 || s.Def.Archetype.Flight is not null || !Linked(s, launcher.Owner, launcher.Pos, b) || !RadarOn(s))
            {
                continue;
            }
            var d = (s.Pos - launcher.Pos).LenSq();
            if (best is null || d < bestD || (d == bestD && s.Id < best.Id))
            {
                (best, bestD) = (s, d);
            }
        }
        return best;
    }

    /// <summary>
    /// The battery launchers of <paramref name="radar"/>'s side it links to now (the radar working, each launcher within its
    /// battery's link radius of it, the radar carrying its battery's radar tags), in ID order: the link lines. Empty when the radar
    /// is off (packed up, moving, dead, inside) or links to none. Read-only; recomputed from the world, valid at any time.
    /// </summary>
    public List<Unit> BatteryLaunchers(Unit radar)
    {
        var list = new List<Unit>();
        if (radar.Def.Archetype.AirVision.Raw == 0 || radar.Def.Archetype.Flight is not null || !RadarOn(radar))
        {
            return list;
        }
        foreach (var u in _units)
        {
            if (u.Alive && u.Owner == radar.Owner && BatteryOf(u.Def) is { } b && Linked(radar, u.Owner, u.Pos, b))
            {
                list.Add(u);
            }
        }
        return list;
    }

    /// <summary>The battery link of unit definition <paramref name="d"/>'s air defence weapon (an SA-6 type launcher), or null.</summary>
    public static Battery? BatteryOf(UnitDef d) => InterceptorWeapon(d) is var wi and >= 0 ? d.Weapons[wi].Effect.Interceptor!.Battery : null;
}
