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
public readonly record struct RadarStatus(bool On, Fixed Air, Fixed Ground, Fixed Sight)
{
    /// <summary>The radar-cued lock percent (the mobile radar: its side's ground air defence locks on what it tracks in this
    /// percent of their lock time); 0 for a radar without one.</summary>
    public int CueLock { get; init; }
}

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
        if (a.GroundRadar.Raw == 0 && (a.AirVision.Raw == 0 || !(u.Def.IsStructure || a.CueLock != 0)))
        {
            return null;
        }
        var sight = u.Def.Vision;
        return new RadarStatus(u.Alive && u.IsBuilt && !u.Rubble && SetUp(u), a.AirVision, a.GroundRadar, sight) // the radar truck, the mobile radar only set up
        {
            CueLock = a.CueLock,
        };
    }

    /// <summary>
    /// The airfields linked to af (H1, the owner's linking): for a type with a link reach (the main air base), every
    /// other airfield of the same owner whose facility's slot lies within that reach of af's apron band; for any
    /// other airfield, the bases it is linked to. In ID order. Service and home choice are shared by every own
    /// airfield anyway (an aircraft uses any of its owner's airfields that takes its class); a link marks where the
    /// client draws the joining taxiway: the base's taxi graph includes a linked field's taxiway, lanes and stops, so an
    /// aircraft of the base may park at the linked field's stations (it lands and takes off on the base's runway).
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

    // Linked: airfield o's facility slot lies within base b's link reach of b's apron band (whatever stands there: a
    // link is the pavement, not the service).
    private bool Linked(Airfield b, Airfield o)
    {
        var t = b.Type;
        if (t.LinkCells == 0 || t.CoreLayout is not { } core || b.Core == 0 || o.Type.HasCore || o.Owner != b.Owner || o == b)
        {
            return false;
        }
        var apron = MapRect(AirfieldRole.Core, b.Origin, b.Facing, (0, core.Apron.Y0, Content.Archetypes[t.Core].FootprintW, core.Apron.Y1));
        var fac = MapRect(AirfieldRole.Facility, o.Origin, o.Facing, FacilityLocal(o.Type, Content.Archetypes[o.Type.Facility]));
        long dx = Math.Max(0, Math.Max(apron.Cell.X - (fac.Cell.X + fac.W), fac.Cell.X - (apron.Cell.X + apron.W)));
        long dy = Math.Max(0, Math.Max(apron.Cell.Y - (fac.Cell.Y + fac.H), fac.Cell.Y - (apron.Cell.Y + apron.H)));
        return dx * dx + dy * dy <= (long)t.LinkCells * t.LinkCells;
    }

    private static readonly IReadOnlyList<Airfield> NoLinks = [];

    // LinkedFields is the forward airfields linked to base af (its taxi graph joins theirs), in ID order; empty for
    // any other airfield (no allocation).
    private IReadOnlyList<Airfield> LinkedFields(Airfield af)
    {
        if (af.Type.LinkCells == 0 || !af.Type.HasCore)
        {
            return NoLinks;
        }
        List<Airfield>? list = null;
        foreach (var o in _airfields)
        {
            if (Linked(af, o))
            {
                (list ??= []).Add(o);
            }
        }
        return list ?? NoLinks;
    }

    // ServedRows is the rows whose stations aircraft of af may take: af's own and, for a main air base, those of the
    // forward airfields linked to it (H1, the taxiway join); af's own list itself when it has no links.
    private IReadOnlyList<AirfieldRow> ServedRows(Airfield af)
    {
        var links = LinkedFields(af);
        if (links.Count == 0)
        {
            return af.RowList;
        }
        var list = new List<AirfieldRow>(af.RowList);
        foreach (var o in links)
        {
            list.AddRange(o.RowList);
        }
        return list;
    }

    // UsesField reports whether an aircraft whose airfield is `home` may hold a station of airfield af: af itself, or a
    // base af is linked to.
    private bool UsesField(Airfield af, uint home) => home == af.Id || (Airfield(home) is { } b && Linked(b, af));

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
