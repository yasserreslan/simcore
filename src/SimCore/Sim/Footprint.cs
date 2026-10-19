using SimCore.Model;

namespace SimCore.Sim;

// Rotated footprints: a structure placed a quarter turn from its archetype's layout stands with its
// width and height swapped (Unit.Turned). Only non-square footprints turn, only by an odd quarter
// turn (facing East or West), and only where nothing in the archetype is laid out from its
// unturned top-left cell (factory doors, garrison doors, tunnel portals); every read of a placed
// structure's footprint goes through FootW / FootH. Square footprints and everything spawned
// unturned keep their old footprint, so nothing that existed before changes.
public sealed partial class World
{
    /// <summary>A placed structure's footprint width in cells, as it stands on the map (<see cref="Unit.Turned"/> swaps width and height).</summary>
    public static int FootW(Unit u) => u.Turned ? u.Def.Archetype.FootprintH : u.Def.Archetype.FootprintW;

    /// <summary>A placed structure's footprint height in cells, as it stands on the map.</summary>
    public static int FootH(Unit u) => u.Turned ? u.Def.Archetype.FootprintW : u.Def.Archetype.FootprintH;

    /// <summary>
    /// Whether a structure of archetype a placed at facing stands turned: a non-square footprint at an
    /// odd quarter turn (East or West). Other facings, and square footprints, keep the layout.
    /// </summary>
    public static bool TurnsAt(Archetype a, int facing) =>
        a.FootprintW != a.FootprintH && FixedPoint.Facing.Wrap(facing) % (FixedPoint.Facing.Count / 2) == FixedPoint.Facing.Count / 4;

    /// <summary>The footprint (width, height) in cells of a structure of archetype a placed at facing.</summary>
    public static (int W, int H) Footprint(Archetype a, int facing) =>
        TurnsAt(a, facing) ? (a.FootprintH, a.FootprintW) : (a.FootprintW, a.FootprintH);

    /// <summary>
    /// Whether an archetype's footprint may be placed turned a quarter: nothing in it is laid out from
    /// its unturned top-left cell (no factory doors, garrison doors or tunnel portal).
    /// </summary>
    public static bool CanTurn(Archetype a) => a.Production is null && a.Garrison is null && a.Tunnel is null;
}
