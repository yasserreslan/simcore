using SimCore.Model;

namespace SimCore.Sim;

// Turned structures (docs/design.md, "Turned buildings"). A structure stands at a quarter turn of its archetype's layout
// (Unit.Quarter: 0 as authored, the front to the south; 1, 2, 3 quarter turns clockwise, the front to the west, north,
// east). An odd quarter swaps a non-square footprint's width and height (Unit.Turned); everything laid out from the
// archetype's top-left cell that turns (garrison doors) is mapped through LayoutCell. A built site takes its quarter from
// its Build facing (facing / 8: facing North 0, East 8, South 16, West 24), a spawned structure from the spawn's quarter.
// Factories (doors, bays and exits laid out to the south, not turned) never turn: CanTurn. Tunnel entrances, airfield pieces
// and bridges turn by their own facing rules and keep Quarter 0. Every read of a placed structure's footprint goes through
// FootW / FootH, and of its doors through DoorCell; a structure at quarter 0 keeps the old footprint and doors, so a game
// with no turned structure plays and hashes as before.
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
    /// Whether an archetype's layout may stand turned: nothing in it is laid out from its unturned top-left cell that
    /// does not turn with it (no factory doors, bays and exits, which face south; no tunnel portal, which turns by its own
    /// facing rules). Garrison doors turn with the building (<see cref="DoorCell"/>), so every town building, home, farm
    /// building, prop and the observation post may turn.
    /// </summary>
    public static bool CanTurn(Archetype a) => a.Production is null && a.Tunnel is null;

    /// <summary>
    /// The quarter (0 to 3, <see cref="Unit.Quarter"/>) a facing turns a layout: facing / 8 for the four quarter facings
    /// (North 0 as authored, East 8 a quarter clockwise, South 16 a half turn, West 24 three quarters); -1 for any other facing.
    /// </summary>
    public static int QuarterOf(int facing)
    {
        var f = FixedPoint.Facing.Wrap(facing);
        const int q = FixedPoint.Facing.Count / 4;
        return f % q == 0 ? f / q : -1;
    }

    /// <summary>The facing a structure's front looks to: south for its layout as authored, turning clockwise with <see cref="Unit.Quarter"/>.</summary>
    public static int FrontFacing(Unit u) => FixedPoint.Facing.Wrap(FixedPoint.Facing.South + u.Quarter * (FixedPoint.Facing.Count / 4));

    /// <summary>
    /// Maps a cell of a layout (x, y from the unturned top-left cell of a w x h footprint; it may lie outside it, as a door
    /// does) to the cell it falls on with the layout turned quarter quarter turns clockwise, from the top-left cell of the
    /// footprint as it stands (h x w at an odd quarter).
    /// </summary>
    public static (int X, int Y) TurnCell(int quarter, int w, int h, int x, int y) => (quarter & 3) switch
    {
        0 => (x, y),
        1 => (h - 1 - y, x),
        2 => (w - 1 - x, h - 1 - y),
        _ => (y, w - 1 - x),
    };

    /// <summary>
    /// The map cell of a layout cell of archetype a (x, y from its unturned top-left cell, as content gives doors) for a
    /// structure whose footprint as it stands has origin as its top-left cell, its layout turned quarter (<see cref="Unit.Quarter"/>).
    /// For tools placing buildings before the world exists (a map generator checking a door's cell).
    /// </summary>
    public static Cell LayoutCell(Archetype a, Cell origin, int quarter, int x, int y)
    {
        var (lx, ly) = TurnCell(quarter, a.FootprintW, a.FootprintH, x, y);
        return new Cell(origin.X + lx, origin.Y + ly);
    }

    /// <summary>The map cell of a layout cell of structure u (x, y from its archetype's unturned top-left cell), as it stands.</summary>
    public static Cell LayoutCell(Unit u, int x, int y) => LayoutCell(u.Def.Archetype, FootprintOrigin(u), u.Quarter, x, y);
}
