namespace SimCore.Sim;

// The vision system: detectors reveal hidden enemies near them (for good),
// then every unit is marked with the players that see it this tick. Weapons
// fire only at spotted enemies, so a launcher that outranges its own vision
// needs a friendly spotter. Real fog of war comes in Phase 3.
public sealed partial class World
{
    private void RunVision()
    {
        foreach (var d in _units)
        {
            if (d.Inside != 0 || d.Def.Detect.Raw == 0)
            {
                continue;
            }
            foreach (var t in _units)
            {
                if (t.Def.Hidden && t.Owner != d.Owner && Within(d.Pos, t.Pos, d.Def.Detect))
                {
                    t.RevealedTo |= Bit(d.Owner);
                }
            }
        }

        foreach (var u in _units)
        {
            u.SpottedBy = u.Inside == 0 ? Bit(u.Owner) : 0;
        }
        foreach (var o in _units)
        {
            if (o.Inside != 0 || o.Def.Vision.Raw == 0)
            {
                continue;
            }
            var bit = Bit(o.Owner);
            foreach (var t in _units)
            {
                if (t.Inside == 0 && (t.SpottedBy & bit) == 0 && Within(o.Pos, t.Pos, o.Def.Vision))
                {
                    t.SpottedBy |= bit;
                }
            }
        }
    }
}
