using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The vision system: every unit is marked with the players that see it this tick, then
// detectors reveal hidden enemies near them (for good). Weapons
// fire only at spotted enemies, so a launcher that outranges its own vision
// needs a friendly spotter.
//
// Without fog of war a unit sees every unit within its vision radius (a 3D distance).
// With fog (MatchSetup.Fog, see Fog.cs) a ground unit is spotted by a player when that
// player's grid shows its cell (line of sight over terrain and through forest), and an
// air unit (a drone) by distance as before (the sky is not hidden by ridges or woods), or
// within an air defence unit's airVision (its radar: a slant distance from its eye).
//
// The distance pass is staggered (see VisionTicks): a viewer looks around every few ticks,
// phased by ID, or as soon as it enters another cell, and each target remembers per player
// until which tick it stays spotted (Unit.SpotUntil, hashed). A target comes into or drops out
// of sight at most VisionTicks - 1 ticks late.
//
// Both passes only OR bits (or one tick number) into the targets they find, so the order of the
// candidates from the spatial index (and a structure listed twice) does not change the result.
public sealed partial class World
{
    // How often a viewer looks around (the distance pass), in ticks: each on its own phase,
    // (tick + ID) % VisionTicks == 0, so spotting by distance lags by at most VisionTicks - 1 ticks.
    internal const uint VisionTicks = 4;

    private void RunVision()
    {
        BuildIndex(); // also used by RunOrders; nothing moves in between
        foreach (var u in _units)
        {
            u.SpottedBy = u.Inside == 0 && u.Garrison == 0 ? Bit(u.Owner) : 0;
        }
        var fog = _fog;
        if (fog is not null)
        {
            RunFog(fog);
            foreach (var t in _units)
            {
                if (t.Inside != 0 || t.Garrison != 0 || InAir(t))
                {
                    continue;
                }
                for (var p = 1; p < fog.Length; p++)
                {
                    if (p != t.Owner && FogSpots(fog[p], t))
                    {
                        t.SpottedBy |= Bit((byte)p);
                    }
                }
            }
        }
        // Each viewer looks around every VisionTicks ticks, phased by ID, and at once when it enters
        // another cell (or has never looked); what it sees stays spotted until its next look.
        var until = Tick + VisionTicks;
        foreach (var o in _units)
        {
            if (o.Inside != 0 || o.Def.Vision.Raw == 0 || !o.IsBuilt || (o.Rubble && o.Def.Archetype.Tunnel is null))
            {
                continue; // a construction site sees nothing yet, rubble nothing any more (H1)
            }
            var cell = Cell.Of(o.Pos);
            if ((Tick + o.Id) % VisionTicks != 0 && o.LookedAt != uint.MaxValue && cell == o.VisionCell)
            {
                continue;
            }
            o.LookedAt = Tick;
            o.VisionCell = cell;
            var owner = o.Owner;
            var sight = SightRange(o);
            foreach (var t in AroundOthers(o, sight.Raw, owner, false, _nearSee))
            {
                if (t.Owner != owner && t.SpotUntil[owner] != until && t.Inside == 0 && t.Garrison == 0 && (fog is null || InAir(t)) &&
                    Within(From(o, t.Pos), AimPoint(o.Pos, t), sight))
                {
                    t.SpotUntil[owner] = until;
                }
            }
        }
        var players = _players.Count;
        foreach (var t in _units)
        {
            var spot = t.SpotUntil;
            if (t.Inside != 0 || t.Garrison != 0)
            {
                Array.Clear(spot); // out of sight inside: whoever saw it must look again
                continue;
            }
            for (var p = 0; p <= players; p++)
            {
                if (spot[p] > Tick)
                {
                    t.SpottedBy |= Bit((byte)p);
                }
            }
        }
        if (_groundRadars)
        {
            RunGroundRadars(); // H1: radar structures spot vehicles anywhere and infantry in the open within their ground reach
        }
        if (fog is not null)
        {
            // Air defence radars (airVision) spot air units within their reach (scaled by the target's
            // signature, E5): what AA fires at, its side sees. The reach is the same slant distance from the
            // sensor's eye that air defence detects with (DetectsAir, CueBody), so a high aircraft (the
            // Overseer 250 m up) is spotted only as far out as it is detected, not by its ground distance.
            foreach (var o in _units)
            {
                var reach = o.Def.Archetype.AirVision;
                if (o.Inside != 0 || reach.Raw == 0 || !o.IsBuilt || !o.Alive || o.Rubble || !SetUp(o))
                {
                    continue; // (H1: a radar as rubble is off; the radar truck senses only set up)
                }
                var bit = Bit(o.Owner);
                var eye = long.MinValue;
                foreach (var t in AroundFrom(o, reach.Raw, _nearSee, false))
                {
                    if ((t.SpottedBy & bit) != 0 || !InAir(t) || t.Inside != 0)
                    {
                        continue;
                    }
                    var r = t.Def.Archetype.Signature.Detect == 100 ? reach.Raw : reach.Raw * t.Def.Archetype.Signature.Detect / 100;
                    if (AirAltitudeRaw(t) is { } alt)
                    {
                        if (eye == long.MinValue)
                        {
                            eye = SensorEyeRaw(o);
                        }
                        long dx = t.Pos.X.Raw - o.Pos.X.Raw, dy = t.Pos.Y.Raw - o.Pos.Y.Raw, dz = alt - eye;
                        if (SlantWithin(dx * dx + dy * dy + dz * dz, r))
                        {
                            t.SpottedBy |= bit;
                        }
                    }
                    else if (Within(o.Pos, t.Pos, Fixed.FromRaw(r)))
                    {
                        t.SpottedBy |= bit;
                    }
                }
            }
            foreach (var u in _units)
            {
                u.SeenBy = u.Owner == 0 ? uint.MaxValue : u.SpottedBy | Bit(u.Owner);
            }
        }
        RunDetection(fog is not null);
    }

    // Whether any unit ever added has a ground radar, or is a structure with an air vision (H1: a radar); while none
    // has, those passes cost nothing. Not hashed (derived from the units).
    private bool _groundRadars, _airRadars;

    // RunGroundRadars (H1, the radar's ground reach; the owner's rule): every built radar standing (not rubble), in ID
    // order, spots for its side every enemy ground unit within its ground radar reach (a level distance from its
    // centre): vehicles anywhere (no ridge or wood hides them), soldiers only in the open (a cell with forest hides
    // them, as it does from sight). Not structures (they need sight), not hidden units (traps need detection), not air
    // units (its air radar is the airVision pass). It only ORs bits, so the order of the candidates does not matter.
    private void RunGroundRadars()
    {
        foreach (var o in _units)
        {
            var reach = o.Def.Archetype.GroundRadar;
            if (reach.Raw == 0 || !o.Alive || !o.IsBuilt || o.Rubble || o.Inside != 0 || o.Owner == 0 || !SetUp(o))
            {
                continue; // (the radar truck senses only set up)
            }
            var bit = Bit(o.Owner);
            foreach (var t in AroundOthers(o, reach.Raw, o.Owner, false, _nearSee))
            {
                if (t.Owner == o.Owner || (t.SpottedBy & bit) != 0 || t.Inside != 0 || t.Garrison != 0 || t.Def.IsStructure || t.Def.Hidden || InAir(t) ||
                    !Within(o.Pos, t.Pos, reach))
                {
                    continue;
                }
                if (t.Def.Archetype.Locomotor == Locomotor.Foot && (o.Def.Archetype.GroundRadarVehicles || (Ground is { } g && g.Forest(Cell.Of(t.Pos)) != 0)))
                {
                    continue; // woods still hide soldiers (a helicopter's mast radar sees only vehicles)
                }
                t.SpottedBy |= bit;
            }
        }
    }

    // AirAltitudeRaw is an air unit's height (absolute, raw) as air defence sees it: a flying aircraft's
    // altitude, an airBody unit's (the FPV drone) fixed height over the ground under it; null for another
    // Air-layer unit (judged by ground distance).
    private long? AirAltitudeRaw(Unit t) =>
        t.Def.Archetype.Flight is not null ? t.Altitude.Raw
        : t.Def.Archetype.AirBody is { } b ? GroundRaw(t.Pos) + b.HeightRaw
        : null;

    // RunDetection: detectors (detect > 0: the sniper, the scout drone, the observation post) reveal
    // hidden enemies (traps, stations, tunnel entrances: the nearest point of a footprint) within
    // their detect radius, for good. With fog of war the
    // hidden unit's cell must also be in sight of the detector's side this tick (SpottedBy): no
    // detecting through a ridge. Built detectors only; garrisoned ones count (a sniper in a post).
    private void RunDetection(bool fog)
    {
        foreach (var d in _units)
        {
            if (d.Inside != 0 || d.Def.Detect.Raw == 0 || !d.IsBuilt || !d.Alive)
            {
                continue;
            }
            var bit = Bit(d.Owner);
            var r = d.Def.Detect.Raw + Fixed.OneRaw;
            foreach (var t in Around(d.Pos.X.Raw - r, d.Pos.Y.Raw - r, d.Pos.X.Raw + r, d.Pos.Y.Raw + r, _nearSee, false))
            {
                if (t.Def.Hidden && t.Owner != d.Owner && (!fog || (t.SpottedBy & bit) != 0) && Within(d.Pos, AimPoint(d.Pos, t), d.Def.Detect))
                {
                    t.RevealedTo |= bit;
                }
            }
        }
    }
}
