using SimCore.FixedPoint;

namespace SimCore.Sim;

// The spatial index (SpatialIndex) as the systems use it. It is built twice a tick,
// from the units in ID order: before vision (used by vision and target acquisition,
// which move nobody) and before combat (used by combat, projectiles, strikes and
// triggers, which move nobody either: they only change HP, facings and flags).
// Outside those windows every query falls back to the whole unit list, so a system
// called from anywhere else (or a test) still sees every unit. Queries only narrow
// the candidates: each system still runs its exact test on every candidate, in an
// order that gives the same result as a scan of all units in ID order.
public sealed partial class World
{
    private readonly SpatialIndex _index = new();
    private readonly List<Unit> _nearSee = []; // vision and target acquisition
    private readonly List<Unit> _nearHit = []; // projectile sweeps
    private readonly List<Unit> _nearBlast = []; // splash
    private bool _indexLive;

    /// <summary>
    /// Test switch: true makes every system scan all units, as before the spatial index,
    /// so tests can check that both give the same world. Not game state.
    /// </summary>
    internal bool FullScan { get; set; }

    private void BuildIndex()
    {
        _indexLive = !FullScan;
        if (_indexLive)
        {
            _index.Build(_units, Grid);
        }
    }

    private void DropIndex() => _indexLive = false;

    // Around returns the units that may overlap the box [x0, x1] x [y0, y1] (raw units):
    // the index's candidates while it is live (in ID order without repeats when ordered),
    // otherwise every unit in ID order.
    private List<Unit> Around(long x0, long y0, long x1, long y1, List<Unit> scratch, bool ordered)
    {
        if (!_indexLive)
        {
            return _units;
        }
        if (ordered)
        {
            _index.Query(x0, y0, x1, y1, scratch);
        }
        else
        {
            _index.QueryUnordered(x0, y0, x1, y1, scratch);
        }
        return scratch;
    }

    // AroundFrom returns the candidates within r of anything From(u, ...) can return: u's
    // window box when garrisoned, its footprint when it is a structure, its position
    // otherwise (all three are inside the box used here), plus a cell of margin.
    private List<Unit> AroundFrom(Unit u, long r, List<Unit> scratch, bool ordered)
    {
        long hw = 0, hh = 0;
        if (u.Garrison != 0)
        {
            hw = (long)u.Cover.W * Fixed.OneRaw / 2;
            hh = (long)u.Cover.H * Fixed.OneRaw / 2;
        }
        if (u.Def.IsStructure)
        {
            hw = Math.Max(hw, (long)u.Def.Archetype.FootprintW * Fixed.OneRaw / 2);
            hh = Math.Max(hh, (long)u.Def.Archetype.FootprintH * Fixed.OneRaw / 2);
        }
        r += Fixed.OneRaw;
        return Around(u.Pos.X.Raw - hw - r, u.Pos.Y.Raw - hh - r, u.Pos.X.Raw + hw + r, u.Pos.Y.Raw + hh + r, scratch, ordered);
    }
}
