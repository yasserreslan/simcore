using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The vision system: detectors reveal hidden enemies near them (for good),
// then every unit is marked with the players that see it this tick. Weapons
// fire only at spotted enemies, so a launcher that outranges its own vision
// needs a friendly spotter.
//
// Without fog of war a unit sees every unit within its vision radius (a 3D distance).
// With fog (MatchSetup.Fog, see Fog.cs) a ground unit is spotted by a player when that
// player's grid shows its cell (line of sight over terrain and through forest), and an
// air unit (a drone) by distance as before: the sky is not hidden by ridges or woods.
//
// Both passes only OR bits into the targets they find, so the order of the
// candidates from the spatial index (and a structure listed twice) does not
// change the result.
public sealed partial class World
{
    private void RunVision()
    {
        BuildIndex(); // also used by RunOrders; nothing moves in between
        foreach (var d in _units)
        {
            if (d.Inside != 0 || d.Def.Detect.Raw == 0)
            {
                continue;
            }
            var r = d.Def.Detect.Raw + Fixed.OneRaw;
            foreach (var t in Around(d.Pos.X.Raw - r, d.Pos.Y.Raw - r, d.Pos.X.Raw + r, d.Pos.Y.Raw + r, _nearSee, false))
            {
                if (t.Def.Hidden && t.Owner != d.Owner && Within(d.Pos, t.Pos, d.Def.Detect))
                {
                    t.RevealedTo |= Bit(d.Owner);
                }
            }
        }

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
                if (t.Inside != 0 || t.Garrison != 0 || t.Def.Layer == Layers.Air)
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
        foreach (var o in _units)
        {
            if (o.Inside != 0 || o.Def.Vision.Raw == 0 || !o.IsBuilt)
            {
                continue; // a construction site sees nothing yet
            }
            var bit = Bit(o.Owner);
            foreach (var t in AroundFrom(o, (o.Def.Vision + o.VisionBonus).Raw, _nearSee, false))
            {
                if (t.Inside == 0 && t.Garrison == 0 && (t.SpottedBy & bit) == 0 && (fog is null || t.Def.Layer == Layers.Air) && Within(From(o, t.Pos), AimPoint(o.Pos, t), o.Def.Vision + o.VisionBonus))
                {
                    t.SpottedBy |= bit;
                }
            }
        }
        if (fog is not null)
        {
            foreach (var u in _units)
            {
                u.SeenBy = u.Owner == 0 ? uint.MaxValue : u.SpottedBy | Bit(u.Owner);
            }
        }
    }
}
