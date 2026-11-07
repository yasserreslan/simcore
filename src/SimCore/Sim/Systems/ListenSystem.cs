using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// One radar a player's SIGINT jets heard (<see cref="World.RadarContacts"/>): plain data for the map, the minimap and the card.
/// </summary>
/// <param name="Unit">The radar's unit ID while it is <paramref name="Live"/> (it may have died since the last pulse: look it up); 0 once last-seen.</param>
/// <param name="Type">Its archetype's content ID: the truth (a decoy is never heard).</param>
/// <param name="Pos">Its exact place when it was last heard.</param>
/// <param name="Live">Whether the last pulse heard it; false: a last-seen mark.</param>
/// <param name="Age">Ticks since it was last heard.</param>
/// <param name="Fade">Ticks until the contact is gone, unless it is heard again by then.</param>
public readonly record struct RadarContact(uint Unit, string Type, Vec Pos, bool Live, int Age, int Fade);

// Listening (the SIGINT jet, an RC-135 type; docs/design.md, "SIGINT jet"; archetype `listen`). It hears and does not see.
//
//  - The pulse: on each tick divisible by its listen.pulse (so every jet with one pulse together), every flying jet is checked against
//    every radar that is switched on, radars and jets in ID order. A radar is a unit with an air vision or a ground radar and none of
//    the jet's excludeTags (the soldiers' and the aircrafts' eyes); switched on is World.RadarOn, the rule its own searching,
//    tracking and battery link use. So a radar emits as it drives (decision 974), a radar in the Hide stance that can switch
//    off (archetype radarSwitch) is silent, rubble is silent, and a decoy (no sensor at all) never emits. Within listen.range, level distance, it is heard by the jet's
//    owner: its contact is made or renewed with the exact place and type, live until the next pulse.
//  - The contacts (RadarContactState, one per radar and player, sorted by player then radar; hashed while not empty): not heard on
//    the next pulse (switched off, out of range, dead, the jet gone), a contact is a last-seen mark holding its last place, and goes
//    listen.fade after it was last heard. The jet cannot tell a radar killed from a radar switched off: both leave a mark.
//  - Spotting (ApplyListening, late in RunVision): a live contact's unit is spotted for that player and for nothing else: no disc,
//    no fog grid stamp, no unit round it. The bit gives it what any spotted unit has: seen by that player and fair game for every
//    unit of its side, each weapon by its own rules. A last-seen mark spots nothing.
//  - Sides: the sim has no teams; a player's side is its units, so a contact is its owner's (every unit of that player may use it)
//    and no other player's. RadarContacts takes a player and gives only that player's.
//  - The cost: while no unit with a listen block was ever added, or none flies with nothing listed, one branch a tick. A pulse is
//    radars x jets level-distance checks over two lists kept by World.Add (ListenChecks counts them); it never walks all the units.
public sealed partial class World
{
    // The contacts, sorted by player then radar ID. Hashed while not empty.
    private readonly List<RadarContactState> _radarContacts = [];

    // Every unit with a listen block and every unit with a radar sensor, in ID order (World.Add); the dead are dropped on a pulse.
    // Not hashed (derived from the units).
    private readonly List<Unit> _listeners = [], _radarUnits = [];

    // Scratch: this pulse's flying jets, in ID order.
    private readonly List<Unit> _hearing = [];

    // The contacts as state, for the tests.
    internal IReadOnlyList<RadarContactState> RadarContactStates => _radarContacts;

    /// <summary>
    /// How many radar-to-jet range checks every pulse so far has made: a measurement for tools, not game state (not hashed).
    /// It stays 0 while no SIGINT jet flies.
    /// </summary>
    public long ListenChecks { get; private set; }

    /// <summary>
    /// The radars <paramref name="player"/>'s SIGINT jets hear or heard, sorted by radar ID: live ones with their unit, last-seen
    /// marks with their fade. A new list each call, that player's own and no one else's; empty for viewer 0 (no player).
    /// </summary>
    public List<RadarContact> RadarContacts(byte player)
    {
        var list = new List<RadarContact>();
        var now = Tick - 1; // the tick just played: between steps Tick is the one about to run, whose pulse has not happened yet
        foreach (var c in _radarContacts)
        {
            if (c.Player == player)
            {
                var live = c.LiveUntil > now;
                list.Add(new RadarContact(live ? c.Radar : 0, c.Type.Id, c.Pos, live, (int)(now - c.Heard), (int)(c.Until - now)));
            }
        }
        return list;
    }

    // NoteListening (World.Add): the two lists a pulse walks. IDs only grow, so both stay in ID order.
    private void NoteListening(Unit u)
    {
        var a = u.Def.Archetype;
        if (a.Listen is not null)
        {
            _listeners.Add(u);
        }
        if (a.AirVision.Raw != 0 || a.GroundRadar.Raw != 0)
        {
            _radarUnits.Add(u);
        }
    }

    // RunListening (RunVision, before anything is spotted this tick): contacts that have faded go, then the pulse.
    internal void RunListening()
    {
        if (_radarContacts.Count > 0)
        {
            _radarContacts.RemoveAll(c => c.Until <= Tick);
        }
        if (_listeners.Count == 0)
        {
            return;
        }
        var jets = _hearing;
        jets.Clear();
        foreach (var j in _listeners)
        {
            if (Tick % j.Def.Archetype.Listen!.PulseTicks == 0 && j.Alive && j.Inside == 0 && j.Owner != 0 && InAir(j))
            {
                jets.Add(j);
            }
        }
        if (jets.Count == 0)
        {
            return;
        }
        _listeners.RemoveAll(j => !j.Alive);
        _radarUnits.RemoveAll(r => !r.Alive);
        foreach (var r in _radarUnits)
        {
            if (r.Owner == 0 || !RadarOn(r))
            {
                continue;
            }
            var heard = Bit(r.Owner); // its own side, then every side that has heard it on this pulse
            foreach (var j in jets)
            {
                var bit = Bit(j.Owner);
                if ((heard & bit) != 0)
                {
                    continue;
                }
                ListenChecks++;
                var l = j.Def.Archetype.Listen!;
                long dx = r.Pos.X.Raw - j.Pos.X.Raw, dy = r.Pos.Y.Raw - j.Pos.Y.Raw;
                if (dx * dx + dy * dy <= l.RangeRaw * l.RangeRaw && !r.Def.Tags.Matches(l.ExcludeTags))
                {
                    heard |= bit;
                    Hear(r, j.Owner, l);
                }
            }
        }
    }

    // Hear makes or renews player's contact with radar r.
    private void Hear(Unit r, byte player, Listen l)
    {
        int lo = 0, hi = _radarContacts.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var m = _radarContacts[mid];
            if (m.Player < player || (m.Player == player && m.Radar < r.Id))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        if (lo == _radarContacts.Count || _radarContacts[lo].Player != player || _radarContacts[lo].Radar != r.Id)
        {
            _radarContacts.Insert(lo, new RadarContactState(r.Id, player, r.Def.Archetype));
        }
        var c = _radarContacts[lo];
        (c.Pos, c.Heard, c.LiveUntil, c.Until) = (r.Pos, Tick, Tick + (uint)l.PulseTicks, Tick + (uint)l.FadeTicks);
    }

    // ApplyListening: each live contact's unit is spotted for its player, and nothing else is (the rule above).
    private void ApplyListening()
    {
        foreach (var c in _radarContacts)
        {
            if (c.LiveUntil > Tick && Unit(c.Radar) is { Inside: 0, Garrison: 0 } r)
            {
                r.SpottedBy |= Bit(c.Player);
            }
        }
    }
}

// One radar and the player who heard it (World._radarContacts).
internal sealed class RadarContactState(uint radar, byte player, Archetype type)
{
    public uint Radar { get; } = radar;

    public byte Player { get; } = player;

    public Archetype Type { get; } = type;

    public Vec Pos { get; set; }

    // The tick it was last heard.
    public uint Heard { get; set; }

    // The first tick it no longer counts as live (the next pulse, unless that pulse hears it).
    public uint LiveUntil { get; set; }

    // The first tick the contact is gone.
    public uint Until { get; set; }
}
