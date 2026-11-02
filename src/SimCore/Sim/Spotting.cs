using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// What a ground unit's spotting looks like to its owner now (<see cref="World.SpottingOf"/>; the client's concealment marker
/// and card: "hidden", "moving", "revealed N s").
/// </summary>
/// <param name="Cover">The cover it stands in.</param>
/// <param name="State">Still, moving (last tick) or fired lately.</param>
/// <param name="Percent">
/// Within what percent of a plain observer's sight (optics 100, not in the woods) it is spotted now: its concealment x the
/// table's percent for its cover and state / 100. 100 or more: wherever an enemy sees its cell.
/// </param>
/// <param name="RevealedUntil">While <see cref="SpotState.Fired"/>: the first tick it is no longer revealed by its shot (if it fires no more); else 0.</param>
/// <param name="Spotted">Whether an enemy player spots it now (<see cref="Unit.SpottedBy"/>, any player but its owner).</param>
public readonly record struct GroundSpotting(SpotCover Cover, SpotState State, int Percent, uint RevealedUntil, bool Spotted);

// Spotting (fog phase F4e; the terrain rules' spotting block, Model.SpottingRules; docs/design.md, "Spotting"). With fog of war on
// a map with terrain and the block, an enemy ground unit out on the map (infantry or a vehicle: the units concealment from the
// air applies to) is no longer spotted by the fog grid: each observer decides in the vision system's staggered distance pass.
//  - A ground observer (a soldier, a vehicle, a structure, a garrison, an aircraft on the ground) spots it when the unit's cell
//    is in the observer's own sight (its viewer's mask: the rays of Fog.cs) and the level distance from where it looks (From)
//    is within reach x optics x concealment x percent(cover, state) / 100^3, reach being its sight (SightRange), or WoodsSight
//    percent of it for an observer standing in the woods looking at a unit in the woods. A product of 100% or more: wherever its
//    cell is seen.
//  - An air observer keeps concealment from the air (ConcealmentSystem.cs) for a unit under canopy, and sees the open and scrub
//    as before (its disc); the town row (a unit beside a building) and its optics apply to it.
//  - An observer standing in the woods (its cell's density is woods) sees WoodsSight percent as far over woods cells, and the
//    forest budget does not hold along a ray that has not left the woods (Fog.cs, Ray): the owner's forest rule.
// Only bits are ORed (Unit.SpotUntil, hashed), so the candidates' order does not matter; integers only. Not hashed beyond that:
// the rules are content (the terrain rules' digest), the rest is read from hashed state (Pos, Vel, LastShot) and the terrain.
public sealed partial class World
{
    private SpottingRules? _spotting; // set by InitSpotting when spotting applies (fog, terrain, the block), else null
    private readonly List<(Archetype Archetype, int Percent)> _optics = []; // the rules' optics resolved against the content
    private int _woodsDensities; // bit d: forest density d is the woods

    /// <summary>
    /// The spotting rules when they apply in this match (fog of war on a map with terrain whose rules have a spotting block), else
    /// null: then every side spots what its fog grid shows, as before.
    /// </summary>
    public SpottingRules? Spotting => _spotting;

    // InitSpotting (AttachTerrain): with fog and the terrain rules' block spotting applies; the optics name content archetypes.
    private void InitSpotting(TerrainMap terrain)
    {
        _spotting = null;
        _optics.Clear();
        _woodsDensities = 0;
        if (_fog is null || terrain.Rules.Spotting is not { } sr)
        {
            return;
        }
        foreach (var (id, percent) in sr.Optics)
        {
            var a = Content.Archetype(id) ?? throw new MatchSetupException($"terrain rules: spotting.optics names the archetype \"{id}\", which the content does not have");
            _optics.Add((a, percent));
        }
        for (var d = 0; d < 4; d++)
        {
            _woodsDensities |= sr.Woods[d] ? 1 << d : 0;
        }
        _spotting = sr;
    }

    /// <summary>The optics of observer u, percent (100 unless the spotting rules list its archetype).</summary>
    public int OpticsOf(Unit u)
    {
        foreach (var (a, percent) in _optics)
        {
            if (ReferenceEquals(a, u.Def.Archetype))
            {
                return percent;
            }
        }
        return 100;
    }

    /// <summary>
    /// The concealment of unit t, percent, by its kind (foot, wheeled, tracked; a decoy by its disguise's); 0 when spotting does
    /// not apply or t is not a soldier or a vehicle.
    /// </summary>
    public int ConcealmentOf(Unit t)
    {
        if (_spotting is not { } sr)
        {
            return 0;
        }
        var a = t.Def.Archetype;
        if (a.Disguise >= 0)
        {
            a = Content.Archetypes[a.Disguise]; // a decoy hides as what it passes for
        }
        return sr.ConcealmentOf(a.Locomotor);
    }

    // InWoods reports whether cell index i's forest density is the woods.
    private bool InWoods(int i) => (_woodsDensities >> (Ground!.FlagBytes[i] & 0b11) & 1) != 0;

    // CellIndex is the index of the cell under p, clamped to the map.
    private int CellIndex(Vec p)
    {
        var c = Cell.Of(p);
        return Math.Clamp(c.Y, 0, Grid.Height - 1) * Grid.Width + Math.Clamp(c.X, 0, Grid.Width - 1);
    }

    // WoodsViewer reports whether u looks from the woods (the owner's forest rule): spotting applies, u is a soldier or a vehicle
    // out on the map (not a structure, not garrisoned, not an aircraft) and its cell is woods.
    private bool WoodsViewer(Unit u) =>
        _spotting is not null && _woodsDensities != 0 && Concealable(u) && InWoods(CellIndex(u.Pos));

    // VisionReach is how far u sees anything at all (raw): its sight, WoodsSight percent of it from the woods.
    private long VisionReach(Unit u)
    {
        var r = SightRange(u).Raw;
        return WoodsViewer(u) ? r * _spotting!.WoodsSightPercent / 100 : r;
    }

    // CoverAt is the cover of a unit standing in cell (x, y), index i: its forest density's (open, scrub, grove, dense), but town
    // for open ground or scrub with a standing building (the sight blocker layer's structures) on it or a cell next to it.
    private SpotCover CoverAt(int x, int y, int i)
    {
        var density = Ground!.FlagBytes[i] & 0b11;
        if (density >= 2 || _blockStruct is not { } bs)
        {
            return (SpotCover)density;
        }
        for (var yy = Math.Max(y - 1, 0); yy <= Math.Min(y + 1, Grid.Height - 1); yy++)
        {
            for (var xx = Math.Max(x - 1, 0); xx <= Math.Min(x + 1, Grid.Width - 1); xx++)
            {
                if (bs[yy * Grid.Width + xx] != 0)
                {
                    return SpotCover.Town;
                }
            }
        }
        return (SpotCover)density;
    }

    // StateOf is t's state: fired within the rules' ticks, else moving (last tick), else still.
    private SpotState StateOf(SpottingRules sr, Unit t) =>
        t.LastShot != 0 && Tick - t.LastShot <= (uint)sr.FiredTicks ? SpotState.Fired
        : MovedLastTick(t) ? SpotState.Moving
        : SpotState.Still;

    // ViewerOf is unit id's viewer this tick (RunFog keeps them sorted by ID), or null.
    private Viewer? ViewerOf(uint id)
    {
        int lo = 0, hi = _viewers.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var v = _viewers[mid];
            if (v.Id == id)
            {
                return v;
            }
            if (v.Id < id)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return null;
    }

    // MaskHas reports whether viewer v sees cell (x, y).
    private static bool MaskHas(Viewer v, int x, int y)
    {
        int dx = x - v.Cx, dy = y - v.Cy;
        if (dx < -v.Rc || dx > v.Rc || dy < -v.Rc || dy > v.Rc || v.Mask.Length == 0)
        {
            return false;
        }
        var side = 2 * v.Rc + 1;
        var b = (dy + v.Rc) * side + dx + v.Rc;
        return (v.Mask[b >> 6] >> (b & 63) & 1) != 0;
    }

    // SpotSees: observer o (its viewer v, its sight raw and optics) spots ground unit t (a soldier or vehicle out on the map)
    // by the spotting rule. An air observer's canopy look is the caller's (concealment from the air).
    private bool SpotSees(SpottingRules sr, Unit o, Viewer v, Unit t, long sight, int optics)
    {
        var c = Cell.Of(t.Pos);
        int x = Math.Clamp(c.X, 0, Grid.Width - 1), y = Math.Clamp(c.Y, 0, Grid.Height - 1);
        if (!MaskHas(v, x, y))
        {
            return false;
        }
        var i = y * Grid.Width + x;
        var cover = CoverAt(x, y, i);
        if (v.Air && cover != SpotCover.Town)
        {
            return true; // from the air the open and scrub are seen as before (canopy: concealment from the air)
        }
        long pct = (long)optics * ConcealmentOf(t) * sr.Percent(cover, StateOf(sr, t));
        const long whole = 100L * 100 * 100;
        if (pct >= whole)
        {
            return true; // wherever its cell is seen
        }
        var reach = v.Wood && InWoods(i) ? sight * sr.WoodsSightPercent / 100 : sight;
        var range = reach * pct / whole;
        return (t.Pos - From(o, t.Pos)).LenSq() <= range * range;
    }

    /// <summary>
    /// Spotting: what ground unit u's spotting looks like to its owner now (its cover, state, the percent of a plain observer's
    /// sight within which it is spotted, a shot's reveal, whether an enemy spots it), or null when spotting does not apply (no
    /// fog, no terrain or no spotting block) or u is not a soldier or a vehicle out on the map. Reads the world only.
    /// </summary>
    public GroundSpotting? SpottingOf(Unit u)
    {
        if (_spotting is not { } sr || !Concealable(u))
        {
            return null;
        }
        var c = Cell.Of(u.Pos);
        int x = Math.Clamp(c.X, 0, Grid.Width - 1), y = Math.Clamp(c.Y, 0, Grid.Height - 1);
        var cover = CoverAt(x, y, y * Grid.Width + x);
        var state = StateOf(sr, u);
        var percent = ConcealmentOf(u) * sr.Percent(cover, state) / 100;
        var until = state == SpotState.Fired ? u.LastShot + (uint)sr.FiredTicks + 1 : 0;
        var enemies = ((Bit((byte)_players.Count) << 1) - 2) & ~Bit(u.Owner); // players 1..n but its owner
        return new GroundSpotting(cover, state, percent, until, (u.SpottedBy & enemies) != 0);
    }

    /// <summary>
    /// Spotting: within how far (level) observer <paramref name="viewer"/>, where it stands now, spots a ground unit of this
    /// concealment (percent: 40 infantry, 70 a light vehicle, 100 a tank by default; <see cref="ConcealmentOf"/>) in this cover
    /// and state, if it sees the unit's cell: its sight (<see cref="SightRange"/>; from the woods, over the woods, its woods
    /// sight) x its optics x concealment x the table's percent, at most that sight. For range rings. Without spotting: its sight.
    /// An air observer's ring is its sight but in a town (canopy: <see cref="AirCoverOf"/>).
    /// </summary>
    public Fixed SpotRange(Unit viewer, int concealment, SpotCover cover, SpotState state)
    {
        var sight = SightRange(viewer).Raw;
        if (_spotting is not { } sr)
        {
            return Fixed.FromRaw(sight);
        }
        if (InAir(viewer) && cover != SpotCover.Town)
        {
            return Fixed.FromRaw(sight);
        }
        if (cover is SpotCover.Open or SpotCover.Scrub or SpotCover.Grove or SpotCover.Dense && WoodsViewer(viewer) &&
            (_woodsDensities >> (int)cover & 1) != 0)
        {
            sight = sight * sr.WoodsSightPercent / 100;
        }
        long pct = (long)OpticsOf(viewer) * Math.Max(concealment, 0) * sr.Percent(cover, state);
        const long whole = 100L * 100 * 100;
        return Fixed.FromRaw(pct >= whole ? sight : sight * pct / whole);
    }
}
