using SimCore.Model;

namespace SimCore.Sim;

// Alleys (docs/design.md, "Alleys and footpaths"): the old town's narrow streets, open to the foot movement class only. For
// every other ground class an alley cell costs 0 in the terrain's cost grids (TerrainMap.Rebuild), so routing (HPA* sectors,
// flow fields, the clearance blocks) and movement (a hard wall) keep vehicles out with no special case. The grid does not mark
// them (infantry walk them), so the places that put things down ask here: a structure is refused on an alley (the build, the
// sandbox, airfield pieces, tunnels, bridges), a vehicle spawned or produced on one stands on the nearest cell that is not.
// A map without alleys changes nothing.
public sealed partial class World
{
    /// <summary>Reports whether cell c is an alley, open to the foot class only (false without terrain or off the map).</summary>
    public bool IsAlley(Cell c) => Ground is { } g && g.IsFootOnly(c);

    // KeepsOffAlleys: whether a unit of def is a ground vehicle (a mover whose movement class is not the foot class), which
    // no alley may hold.
    private bool KeepsOffAlleys(UnitDef def) =>
        Ground is { } g && !def.IsStructure && def.Layer == Layers.Ground && def.Archetype.Flight is null &&
        g.Rules.ClassOf(def) is var k and >= 0 && !g.Rules.Classes[k].EntersAlleys;

    // TrySpiralFor finds the n-th passable cell (0 = nearest) walking outward from c as Grid.TrySpiral does, for a unit of def:
    // a vehicle skips the alleys on the way.
    internal bool TrySpiralFor(UnitDef def, Cell c, int n, out Cell cell) =>
        KeepsOffAlleys(def) ? Grid.TrySpiralWhere(c, n, p => !IsAlley(p), out cell) : Grid.TrySpiral(c, n, out cell);

    // FootprintOnAlley reports whether a structure of w x h cells with c as its top-left cell would cover an alley.
    private bool FootprintOnAlley(Cell c, int w, int h)
    {
        if (Ground is not { } g)
        {
            return false;
        }
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (g.IsFootOnly(new Cell(c.X + x, c.Y + y)))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
