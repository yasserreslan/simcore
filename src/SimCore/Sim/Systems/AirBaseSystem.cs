using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// A radar's ranges and state (<see cref="World.RadarOf"/>, H1), for the client's range rings and the radar's
/// animation. Not game state.
/// </summary>
/// <param name="On">Whether it works now: built, alive and not rubble.</param>
/// <param name="Air">Its air reach (slant from its top, scaled by each target's signature), feeding the side's air tracking.</param>
/// <param name="Ground">Its ground reach: enemy vehicles anywhere, infantry in the open.</param>
/// <param name="Sight">Its sight radius: it clears the fog like any viewer (line of sight from its top).</param>
public readonly record struct RadarStatus(bool On, Fixed Air, Fixed Ground, Fixed Sight);

// The main air base's queries (H1; the game repo's docs/heavy-air-plan.md, section 2.1): the radar's ranges and the
// links between airfields. The base itself is the airfield system with a core (AirfieldSystem.cs: the core lays the
// command building, the hangar with its heavy bays, the radar, the built-in runway and rows); its rules are the
// airfield's (AirfieldServiceSystem.cs, AirfieldDamageSystem.cs, ProductionSystem.cs). Nothing here changes state.
public sealed partial class World
{
    /// <summary>
    /// A radar's ranges and whether it is on (H1: the main air base's built-in radar and the standalone radar site; any
    /// structure with a ground radar or an air vision). Null for a unit without either.
    /// </summary>
    public static RadarStatus? RadarOf(Unit u)
    {
        var a = u.Def.Archetype;
        if (a.GroundRadar.Raw == 0 && (a.AirVision.Raw == 0 || !u.Def.IsStructure))
        {
            return null;
        }
        var sight = u.Def.Vision;
        return new RadarStatus(u.Alive && u.IsBuilt && !u.Rubble, a.AirVision, a.GroundRadar, sight);
    }

    /// <summary>
    /// The airfields linked to af (H1, the owner's linking): for a type with a link reach (the main air base), every
    /// other airfield of the same owner whose facility's footprint lies within that reach of af's apron band; for any
    /// other airfield, the bases it is linked to. In ID order. Service and home choice are shared by every own
    /// airfield anyway (an aircraft uses any of its owner's airfields that takes its class); a link marks where the
    /// client draws the joining taxiway. Not yet: the taxi graphs are not joined (an aircraft taxis only on the
    /// airfield it landed at: docs/design.md, "Main air base and radar (H1)").
    /// </summary>
    public List<Airfield> AirfieldLinks(Airfield af)
    {
        var list = new List<Airfield>();
        foreach (var o in _airfields)
        {
            if (o != af && o.Owner == af.Owner && (Linked(af, o) || Linked(o, af)))
            {
                list.Add(o);
            }
        }
        return list;
    }

    // Linked: airfield o's facility lies within base b's link reach of b's apron band (both standing or rubble: a link
    // is the pavement, not the service).
    private bool Linked(Airfield b, Airfield o)
    {
        var t = b.Type;
        if (t.LinkCells == 0 || t.CoreLayout is not { } core || b.Core == 0 || Unit(o.Facility) is not { } fac || o.Type.HasCore)
        {
            return false;
        }
        var apron = MapRect(AirfieldRole.Core, b.Origin, b.Facing, (0, core.Apron.Y0, Content.Archetypes[t.Core].FootprintW, core.Apron.Y1));
        var fo = FootprintOrigin(fac);
        long dx = Math.Max(0, Math.Max(apron.Cell.X - (fo.X + FootW(fac)), fo.X - (apron.Cell.X + apron.W)));
        long dy = Math.Max(0, Math.Max(apron.Cell.Y - (fo.Y + FootH(fac)), fo.Y - (apron.Cell.Y + apron.H)));
        return dx * dx + dy * dy <= (long)t.LinkCells * t.LinkCells;
    }

    /// <summary>The heavy bays of a main air base (H1), west to east: (bay index, station); empty for other airfields.</summary>
    public static List<(int Bay, AirfieldStation Station)> HeavyBays(Airfield af)
    {
        var list = new List<(int, AirfieldStation)>();
        foreach (var r in af.Rows)
        {
            if (r.IsBay && r.Stations.Count > 0)
            {
                list.Add((-1 - r.Col, r.Stations[0]));
            }
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }
}
