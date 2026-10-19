using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Vision from high ground (an archetype's HighGround spec: the observation post). The radius
// grows by PercentPerMetre for every metre the unit's ground stands above the mean ground height
// of the square of RadiusCells around it (sampled every 2 cells), up to MaxPercent; never below
// the plain radius. Only on terrain. Integer, cached per unit until it moves or the terrain changes
// (derived from hashed state, so not hashed itself).
public sealed partial class World
{
    /// <summary>
    /// How far u sees: its vision plus any garrison bonus, widened by its high-ground bonus
    /// (<see cref="HighGroundPercent"/>). Fog of war and spotting both use it.
    /// </summary>
    public Fixed SightRange(Unit u)
    {
        if (u.Def.Archetype.Aircraft is { } ac && Grounded(u))
        {
            return ac.GroundVision; // an aircraft on the ground sees as a ground unit (M3b)
        }
        var r = u.Def.Vision + u.VisionBonus;
        if (u.Def.Archetype.HighGround is null)
        {
            return r;
        }
        return Fixed.FromRaw(r.Raw * (100 + HighGroundPercent(u)) / 100);
    }

    /// <summary>The percent u's vision grows from high ground: 0 without a HighGround spec or terrain.</summary>
    public int HighGroundPercent(Unit u)
    {
        if (u.Def.Archetype.HighGround is not { } hg || Ground is not { } g)
        {
            return 0;
        }
        if (u.HighGroundVersion == g.Version && u.HighGroundPos == u.Pos)
        {
            return u.HighGroundCached;
        }
        var c = Cell.Of(u.Pos);
        long sum = 0, n = 0;
        for (var y = c.Y - hg.RadiusCells; y <= c.Y + hg.RadiusCells; y += 2)
        {
            for (var x = c.X - hg.RadiusCells; x <= c.X + hg.RadiusCells; x += 2)
            {
                if (x >= 0 && y >= 0 && x < Grid.Width && y < Grid.Height)
                {
                    sum += g.HeightAt(new Cell(x, y).Center()).Raw;
                    n++;
                }
            }
        }
        var above = n == 0 ? 0 : g.HeightAt(u.Pos).Raw - sum / n; // raw, 512 a metre
        var pct = (int)Math.Clamp(above * hg.PercentPerMetre / 512, 0, hg.MaxPercent);
        (u.HighGroundVersion, u.HighGroundPos, u.HighGroundCached) = (g.Version, u.Pos, pct);
        return pct;
    }
}
