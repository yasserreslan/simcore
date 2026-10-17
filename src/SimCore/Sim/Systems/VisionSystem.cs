using SimCore.FixedPoint;

namespace SimCore.Sim;

// The vision system: detectors reveal hidden enemies near them (for good),
// then every unit is marked with the players that see it this tick. Weapons
// fire only at spotted enemies, so a launcher that outranges its own vision
// needs a friendly spotter. Real fog of war comes in Phase 3.
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
        foreach (var o in _units)
        {
            if (o.Inside != 0 || o.Def.Vision.Raw == 0)
            {
                continue;
            }
            var bit = Bit(o.Owner);
            foreach (var t in AroundFrom(o, (o.Def.Vision + o.VisionBonus).Raw, _nearSee, false))
            {
                if (t.Inside == 0 && t.Garrison == 0 && (t.SpottedBy & bit) == 0 && Within(From(o, t.Pos), AimPoint(o.Pos, t), o.Def.Vision + o.VisionBonus))
                {
                    t.SpottedBy |= bit;
                }
            }
        }
    }
}
