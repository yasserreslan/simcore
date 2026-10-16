using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>What a unit is currently told to do.</summary>
public enum OrderKind : byte
{
    /// <summary>No order; the unit defends itself.</summary>
    Idle,

    /// <summary>Drive to <see cref="Order.Cell"/>.</summary>
    Move,

    /// <summary>Chase and shoot <see cref="Order.Target"/>.</summary>
    Attack,
}

/// <summary>A unit's current order.</summary>
/// <param name="Kind">The kind of order.</param>
/// <param name="Cell">The <see cref="OrderKind.Move"/> destination.</param>
/// <param name="Target">The <see cref="OrderKind.Attack"/> target's ID.</param>
public readonly record struct Order(OrderKind Kind, Cell Cell, uint Target);

/// <summary>
/// One unit on the map. Renderers read these properties; only the sim writes them.
/// </summary>
public sealed class Unit
{
    internal Unit(uint id, byte owner, UnitTypeId type, Vec pos, int facing, int hp)
    {
        Id = id;
        Owner = owner;
        Type = type;
        Pos = pos;
        Facing = facing;
        TurretFacing = facing;
        HP = hp;
    }

    /// <summary>The unit's ID. IDs are assigned in increasing order and never reused.</summary>
    public uint Id { get; }

    /// <summary>The owning player. 0 is neutral; real players start at 1.</summary>
    public byte Owner { get; }

    /// <summary>The unit type.</summary>
    public UnitTypeId Type { get; }

    /// <summary>The world position.</summary>
    public Vec Pos { get; internal set; }

    /// <summary>The hull facing, 0..31.</summary>
    public int Facing { get; internal set; }

    /// <summary>The turret facing, 0..31; equals <see cref="Facing"/> for units without a turret.</summary>
    public int TurretFacing { get; internal set; }

    /// <summary>Hit points; the unit is removed at the end of the tick it reaches zero.</summary>
    public int HP { get; internal set; }

    /// <summary>Ticks until the weapon can fire.</summary>
    public int Reload { get; internal set; }

    /// <summary>The current order.</summary>
    public Order Order { get; internal set; }

    /// <summary>The enemy picked automatically when not on an attack order, or 0.</summary>
    public uint AutoTarget { get; internal set; }

    /// <summary>Cells still to visit, from <see cref="PathIdx"/> on.</summary>
    public IReadOnlyList<Cell> Path { get; private set; } = [];

    /// <summary>The index in <see cref="Path"/> of the next cell to visit.</summary>
    public int PathIdx { get; internal set; }

    /// <summary>The tick at which a chasing unit recomputes its path.</summary>
    public uint RepathAt { get; internal set; }

    /// <summary>The unit's static definition.</summary>
    public UnitType Def => UnitTypes.Of(Type);

    /// <summary>Whether the unit still has path to follow.</summary>
    public bool Moving => PathIdx < Path.Count;

    internal void SetPath(Cell[] path)
    {
        Path = path;
        PathIdx = 0;
    }

    internal void ClearPath()
    {
        Path = [];
        PathIdx = 0;
    }
}

/// <summary>Per-player state.</summary>
public sealed class Player
{
    internal Player(byte id) => Id = id;

    /// <summary>The player's ID, starting at 1.</summary>
    public byte Id { get; }

    /// <summary>The player's money.</summary>
    public int Credits { get; internal set; }
}
