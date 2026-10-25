using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>
/// Where a map's tree trunks stand (docs/design.md, "Trees as obstacles"): derived from the forest, never stored in a map.
/// The map is cut into 2 x 2 blocks of cells (slots; slot (sx, sy) holds cells 2sx..2sx+1 x 2sy..2sy+1). Each slot's
/// <see cref="SlotHash"/> picks one of its four cells (<see cref="SlotCell"/>) and a roll from 0 to 99
/// (<see cref="Roll"/>): a trunk stands in that cell when the roll is under the trunk chance of the cell's forest density
/// (<see cref="Model.TreeRules.TrunkChance"/>). So no two trunks share a slot (they keep some room between them, as trees
/// do) and a forest of one density is the same wherever it lies on any map. The hash also places the trunk off the cell's
/// centre (<see cref="Offset"/>), inside the cell. Integer only: the same on every machine; pinned by
/// <c>TreeTrunkTests</c> so it never drifts (the game client draws its trees from it, through
/// <see cref="TerrainMap.HasTrunk"/> and <see cref="TerrainMap.TrunkOffset"/>).
/// </summary>
public static class TreeTrunks
{
    /// <summary>The side of a slot, in cells: one trunk at most per 2 x 2 cells.</summary>
    public const int SlotSide = 2;

    /// <summary>The fixed salt mixed into every slot's hash.</summary>
    public const uint Salt = 0x7EE5_10C5;

    /// <summary>The farthest a trunk stands from its cell's centre along x or y, raw (0.31 of a cell): inside the cell with its bark.</summary>
    public const int MaxOffsetRaw = 320;

    /// <summary>
    /// The hash of slot (sx, sy): bits 0-1 pick the cell (<see cref="SlotCell"/>), bits 2-15 the roll (<see cref="Roll"/>),
    /// bits 16-23 and 24-31 the trunk's offset along x and y (<see cref="Offset"/>). A 32-bit integer mix (multiply, xor-shift),
    /// wrapping.
    /// </summary>
    public static uint SlotHash(int sx, int sy)
    {
        unchecked
        {
            var h = ((uint)sx * 0x8DA6B343u) ^ ((uint)sy * 0xD8163841u) ^ Salt;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }
    }

    /// <summary>The cell of slot (sx, sy) that its hash h picks for a trunk.</summary>
    public static Cell SlotCell(int sx, int sy, uint h) => new(sx * SlotSide + (int)(h & 1), sy * SlotSide + (int)((h >> 1) & 1));

    /// <summary>The roll of a slot's hash h, 0 to 99: a trunk stands when it is under the chance of its cell's density.</summary>
    public static int Roll(uint h) => (int)((((h >> 2) & 0x3FFF) * 100) >> 14);

    /// <summary>The offset of a slot's trunk from its cell's centre, raw, each of x and y from -320 to 317 (<see cref="MaxOffsetRaw"/>).</summary>
    public static (int X, int Y) Offset(uint h) => (((int)((h >> 16) & 0xFF) - 128) * 5 / 2, ((int)((h >> 24) & 0xFF) - 128) * 5 / 2);

    /// <summary>
    /// Whether forest with this trunk chance (percent) puts a trunk in cell (x, y), felled or not: the slot's picked cell is
    /// (x, y) and its roll is under the chance. Cells are on the map (x, y from 0).
    /// </summary>
    public static bool Stands(int x, int y, int chancePercent)
    {
        if (chancePercent <= 0)
        {
            return false;
        }
        int sx = x / SlotSide, sy = y / SlotSide;
        var h = SlotHash(sx, sy);
        return SlotCell(sx, sy, h) == new Cell(x, y) && Roll(h) < chancePercent;
    }

    /// <summary>The cell step of one of the 8 fall directions (0 north, then clockwise: 1 north-east, 2 east, ... 7 north-west).</summary>
    public static (int X, int Y) Step(int dir8) => (dir8 & 7) switch
    {
        0 => (0, -1),
        1 => (1, -1),
        2 => (1, 0),
        3 => (1, 1),
        4 => (0, 1),
        5 => (-1, 1),
        6 => (-1, 0),
        _ => (-1, -1),
    };

    /// <summary>The 8-way direction (0 north, clockwise) nearest a facing (<see cref="Facing"/>, 32 a turn).</summary>
    public static int DirOf(int facing) => ((Facing.Wrap(facing) + 2) / 4) & 7;

    /// <summary>The 8-way direction (0 north, clockwise) nearest a vector; north for the zero vector.</summary>
    public static int DirOf(Vec d) => Facing.TryFromDirection(d, out var f) ? DirOf(f) : 0;
}

/// <summary>Why a tree fell (<see cref="FallenTrunk.Cause"/>, <see cref="TreeEvent.Cause"/>).</summary>
public enum TreeFallCause : byte
{
    /// <summary>A tank or a heavy truck pushed it over (it falls the way the pusher faces).</summary>
    Pushed,
}

/// <summary>
/// A tree that fell and lies on the ground (<see cref="World.FallenTrunks"/>; docs/design.md, "Trees as obstacles"): from
/// the cell beside its stump, <see cref="Length"/> cells along <see cref="Dir"/>. Its cells block every vehicle but those
/// that roll over fallen trunks (<see cref="Model.MoveClass.CrossFallen"/>; infantry walk over it) until tick
/// <see cref="ExpiresAt"/>, when it is gone and the ground is clear. Its stump's cell is open from the fall.
/// </summary>
/// <param name="Stump">The cell where it stood (open now).</param>
/// <param name="Dir">The way it fell, 0 north then clockwise to 7 north-west (<see cref="TreeTrunks.Step"/>).</param>
/// <param name="Length">The cells it covers, from the one beside its stump (0: it fell against a building or into water at once).</param>
/// <param name="ExpiresAt">The tick it is gone (in that tick's tree step, after movement).</param>
/// <param name="By">The unit that felled it (its pusher), or 0.</param>
/// <param name="Cause">Why it fell.</param>
public readonly record struct FallenTrunk(Cell Stump, int Dir, int Length, uint ExpiresAt, uint By, TreeFallCause Cause)
{
    /// <summary>Its k-th cell, k from 1 to <see cref="Length"/>.</summary>
    public Cell CellAt(int k)
    {
        var (dx, dy) = TreeTrunks.Step(Dir);
        return new Cell(Stump.X + dx * k, Stump.Y + dy * k);
    }

    /// <summary>Whether it covers cell c.</summary>
    public bool Covers(Cell c)
    {
        var (dx, dy) = TreeTrunks.Step(Dir);
        int ox = c.X - Stump.X, oy = c.Y - Stump.Y;
        var k = dx != 0 ? ox * dx : oy * dy; // the step count along its direction (each step is 1 along every axis it moves)
        return k >= 1 && k <= Length && ox == dx * k && oy == dy * k;
    }
}

/// <summary>A push in progress (<see cref="World.TreePushes"/>): a pusher pressing on the trunk in <see cref="Cell"/>.</summary>
/// <param name="Cell">The trunk's cell.</param>
/// <param name="Pusher">The pusher (the lowest unit ID in contact this tick).</param>
/// <param name="Ticks">The ticks of contact so far (the tree falls at its pusher's <see cref="Model.MoveClass.PushTicks"/>).</param>
public readonly record struct TreePush(Cell Cell, uint Pusher, int Ticks);

/// <summary>What happened to a tree this tick (<see cref="TreeEvent"/>).</summary>
public enum TreeEventKind : byte
{
    /// <summary>A pusher started pushing a standing trunk (the client leans it).</summary>
    PushStarted,

    /// <summary>A tree fell: its stump's cell opened and its trunk lies along <see cref="TreeEvent.Dir"/>.</summary>
    Fell,

    /// <summary>A fallen trunk's time ran out: it is gone and its cells are clear.</summary>
    Expired,

    /// <summary>A fallen trunk was cleared away early (a building, a bridge, an airfield or a tunnel laid over it).</summary>
    Cleared,
}

/// <summary>
/// A tree's change this tick (<see cref="World.TreeEvents"/>), for renderers: a push starting (lean the tree), a fall
/// (topple it along <see cref="Dir"/> and lay the log), a fallen trunk gone. Not game state.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Stump">The tree's cell (where it stands or stood).</param>
/// <param name="Dir">The way it falls or fell (0 north, clockwise; <see cref="TreeTrunks.Step"/>); a push's: the pusher's heading now.</param>
/// <param name="Length">A fall: the cells its trunk covers; otherwise 0.</param>
/// <param name="By">The unit pushing or that felled it, or 0.</param>
/// <param name="Cause">Why it fell (a fall; <see cref="TreeFallCause.Pushed"/> otherwise).</param>
/// <param name="From">Where the push came from: the pusher's position.</param>
public readonly record struct TreeEvent(TreeEventKind Kind, Cell Stump, int Dir, int Length, uint By, TreeFallCause Cause, Vec From);
