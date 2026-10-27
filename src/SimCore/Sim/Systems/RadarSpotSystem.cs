using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>
/// An enemy radar revealed to a player by radar spotting (<see cref="World.RadarSpots"/>; the stealth multirole's special ability), or
/// an emitter (<see cref="Model.Archetype.Emitter"/>: the S-400 type radar) revealed by the player's sensors that picked it up:
/// while an aircraft of the player with a <see cref="Model.RadarSpotting"/> flies inside the radar's air coverage, and for its
/// <see cref="Model.RadarSpotting.LingerTicks"/> after, the radar and a disc of <see cref="Radius"/> round it are spotted for the
/// player (through the fog, targetable by every unit of its side). Read-only for renderers (the revealed radar's marker); hashed.
/// </summary>
public sealed class RadarSpot
{
    internal RadarSpot(uint radar, byte player, uint since)
    {
        Radar = radar;
        Player = player;
        Since = since;
    }

    /// <summary>The radar unit revealed.</summary>
    public uint Radar { get; }

    /// <summary>The player it is revealed to.</summary>
    public byte Player { get; }

    /// <summary>
    /// The unit that revealed it: the lowest ID of the player's aircraft in its coverage the last tick one was, or for an emitter
    /// (the S-400 type radar) the lowest ID of the player's sensors that picked it up on its last sweep.
    /// </summary>
    public uint Spotter { get; internal set; }

    /// <summary>The tick this reveal began (an aircraft entered the coverage of a radar not revealed to its side).</summary>
    public uint Since { get; internal set; }

    /// <summary>The first tick it is no longer revealed (unless an aircraft of the player is in its coverage again by then).</summary>
    public uint Until { get; internal set; }

    /// <summary>Whether an aircraft of the player is inside its coverage this tick (false: the reveal lingers on).</summary>
    public bool Inside { get; internal set; }

    /// <summary>The revealed disc round the radar (the spotter's <see cref="Model.RadarSpotting.Radius"/>).</summary>
    public Fixed Radius { get; internal set; }
}

// Radar spotting (the stealth multirole, an F-35 type; docs/design.md, "Stealth multirole (F-35 type)"). Each tick, before vision,
// every working enemy radar (alive, built, not rubble, not inside, set up: World.RadarOn) carrying one of a spotter's tags and an
// air vision is checked, in ID order, against every flying aircraft (not grounded at an airfield) with a radarSpotting, in ID
// order: inside the radar's coverage (the slant from its eye within its full air vision, whatever the aircraft's signature, so
// whether or not the radar detects it) the radar is revealed to the aircraft's owner until the tick after next plus the linger
// (RadarSpot, one per radar and player, sorted by radar then player; hashed). Vision then spots the radar and every ground unit
// within the disc round it for that player, and under fog the disc's cells are visible to the player (an air viewer's disc:
// no line of sight needed). A revealed radar that dies, or whose reveal ran out, is dropped. Integer only; nothing else changes.
public sealed partial class World
{
    // The revealed radars, sorted by radar ID then player. Hashed while not empty.
    private readonly List<RadarSpot> _radarSpots = [];

    // Whether any unit ever added has a radar spotting: while none has, the pass costs nothing. Not hashed (derived from the units).
    private bool _radarSpotters;

    // Scratch: this tick's flying spotters, in ID order; the fog's disc viewers of the reveals (by radar ID then player).
    private readonly List<Unit> _spotters = [];
    private List<Viewer?> _spotViewers = [], _spotViewersNext = [];

    /// <summary>
    /// The enemy radars revealed by radar spotting now (the stealth multirole), sorted by radar ID then player: each with the player it
    /// is revealed to, the aircraft that revealed it, since and until when, whether one is in its coverage now and the revealed disc.
    /// </summary>
    public IReadOnlyList<RadarSpot> RadarSpots => _radarSpots;

    /// <summary>The reveal of radar <paramref name="radar"/> to <paramref name="player"/> now, or null.</summary>
    public RadarSpot? RadarSpotOf(uint radar, byte player)
    {
        foreach (var s in _radarSpots)
        {
            if (s.Radar == radar && s.Player == player)
            {
                return s;
            }
        }
        return null;
    }

    // NoteRadarSpotter (World.Add): a unit with a radar spotting turns the pass on; an emitter (the S-400 type radar) its own.
    private void NoteRadarSpotter(Unit u)
    {
        _radarSpotters |= u.Def.Archetype.RadarSpotting is not null;
        _emitters |= u.Def.Archetype.Emitter is not null;
    }

    // UpdateRadarSpots runs the rule above for this tick (RunVision, before the fog is swept).
    private void UpdateRadarSpots()
    {
        if (_radarSpots.Count > 0)
        {
            _radarSpots.RemoveAll(s => s.Until <= Tick || Unit(s.Radar) is not { Alive: true });
            foreach (var s in _radarSpots)
            {
                s.Inside = false;
            }
        }
        if (_emitters)
        {
            UpdateEmitters(); // searching emitters picked up by enemy radars (LongRangeRadarSystem.cs)
        }
        if (!_radarSpotters)
        {
            return;
        }
        var spotters = _spotters;
        spotters.Clear();
        foreach (var u in _units)
        {
            if (u.Def.Archetype.RadarSpotting is not null && u.Alive && u.Inside == 0 && u.Owner != 0 && InAir(u))
            {
                spotters.Add(u);
            }
        }
        if (spotters.Count == 0)
        {
            return;
        }
        foreach (var r in _units)
        {
            var reach = r.Def.Archetype.AirVision.Raw;
            if (reach == 0 || r.Owner == 0 || !RadarOn(r))
            {
                continue;
            }
            var eye = long.MinValue;
            foreach (var a in spotters)
            {
                var rs = a.Def.Archetype.RadarSpotting!;
                if (a.Owner == r.Owner || !r.Def.Tags.Matches(rs.Tags))
                {
                    continue;
                }
                if (eye == long.MinValue)
                {
                    eye = SensorEyeRaw(r);
                }
                long dx = a.Pos.X.Raw - r.Pos.X.Raw, dy = a.Pos.Y.Raw - r.Pos.Y.Raw, dz = a.Altitude.Raw - eye;
                if (SlantWithin(dx * dx + dy * dy + dz * dz, reach))
                {
                    Spot(r, a.Id, a.Owner, rs.Radius, rs.LingerTicks);
                }
            }
        }
    }

    // Spot reveals radar r to player owner: through the tick after this plus the linger, spotter (an aircraft with a radar spotting,
    // or the enemy sensor that picked up an emitter) its spotter (the lowest ID this tick).
    private void Spot(Unit r, uint spotter, byte owner, Fixed radius, int linger)
    {
        var i = 0;
        while (i < _radarSpots.Count && (_radarSpots[i].Radar < r.Id || (_radarSpots[i].Radar == r.Id && _radarSpots[i].Player < owner)))
        {
            i++;
        }
        if (i == _radarSpots.Count || _radarSpots[i].Radar != r.Id || _radarSpots[i].Player != owner)
        {
            _radarSpots.Insert(i, new RadarSpot(r.Id, owner, Tick));
        }
        var s = _radarSpots[i];
        if (!s.Inside)
        {
            (s.Spotter, s.Radius, s.Inside) = (spotter, radius, true);
        }
        s.Until = Math.Max(s.Until, Tick + 1 + (uint)linger);
    }

    // ApplyRadarSpots (RunVision, after the other spotting passes): each revealed radar, and every ground unit not inside anything
    // within its disc, is spotted for the player it is revealed to.
    private void ApplyRadarSpots()
    {
        foreach (var s in _radarSpots)
        {
            if (Unit(s.Radar) is not { } r)
            {
                continue;
            }
            var bit = Bit(s.Player);
            r.SpottedBy |= bit;
            var rr = s.Radius.Raw + Fixed.OneRaw;
            foreach (var t in Around(r.Pos.X.Raw - rr, r.Pos.Y.Raw - rr, r.Pos.X.Raw + rr, r.Pos.Y.Raw + rr, _nearSee, false))
            {
                if (t.Owner != s.Player && t.Inside == 0 && t.Garrison == 0 && !InAir(t) && Within(r.Pos, t.Pos, s.Radius))
                {
                    t.SpottedBy |= bit;
                }
            }
        }
    }

    // StampRadarSpots (RunFog, after the units' viewers): each reveal is an air viewer's disc (no line of sight) of its radius round its
    // radar's cell in its player's grid; a reveal kept where it was keeps its stamp, the rest are swept again or taken off.
    private void StampRadarSpots(FogGrid[] fog)
    {
        var next = _spotViewersNext;
        next.Clear();
        var old = _spotViewers;
        foreach (var s in _radarSpots)
        {
            if (Unit(s.Radar) is not { } r || s.Player < 1 || s.Player >= fog.Length)
            {
                continue;
            }
            var c = Cell.Of(r.Pos);
            var r2 = s.Radius.Raw * s.Radius.Raw;
            Viewer? v = null;
            for (var k = 0; k < old.Count; k++)
            {
                if (old[k] is { } o && o.Id == s.Radar && o.Owner == s.Player)
                {
                    v = o;
                    old[k] = null; // taken
                    break;
                }
            }
            if (v is not null && v.Same(s.Player, c.X, c.Y, r2, -1))
            {
                next.Add(v);
                continue;
            }
            if (v is not null)
            {
                Unstamp(fog, v);
            }
            v ??= new Viewer { Id = s.Radar };
            (v.Owner, v.Cx, v.Cy, v.R2, v.Eye) = (s.Player, c.X, c.Y, r2, -1);
            Sweep(v);
            Stamp(fog, v);
            next.Add(v);
        }
        foreach (var o in old)
        {
            if (o is not null)
            {
                Unstamp(fog, o); // its reveal ended (or its radar is gone)
            }
        }
        old.Clear();
        (_spotViewers, _spotViewersNext) = (next, old);
    }
}
