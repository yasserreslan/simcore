using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// One airfield (the game repo's docs/airfield-design.md): a grid of pieces laid out from its
/// facility. Its frame: +x along the runway, +y away from it, turned in quarter turns with
/// <see cref="Facing"/>; local (0, 0) is the runway's corner at segment 0's start, map corner
/// <see cref="Origin"/>. Every piece is an ordinary structure unit with <see cref="Unit.Airfield"/>
/// set to <see cref="Id"/>. Hashed, only while airfields exist.
/// </summary>
public sealed partial class Airfield
{
    internal Airfield(uint id, byte owner, AirfieldType type, Cell origin, int facing, long padRaw)
    {
        Id = id;
        Owner = owner;
        Type = type;
        Origin = origin;
        Facing = facing;
        PadRaw = padRaw;
        Facility = id;
        West = 0;
        East = -1;
    }

    /// <summary>Its ID: the founding facility's unit ID (it stays when the facility is gone).</summary>
    public uint Id { get; }

    /// <summary>The owning player.</summary>
    public byte Owner { get; }

    /// <summary>Its airfield type: the grid, the pieces, the rules.</summary>
    public AirfieldType Type { get; }

    /// <summary>The map corner of the airfield's local (0, 0).</summary>
    public Cell Origin { get; }

    /// <summary>The facing it was founded with (0, 8, 16 or 24): the field turns with it.</summary>
    public int Facing { get; }

    /// <summary>The height (raw) every piece's pad is flattened to; 0 on a flat map.</summary>
    public long PadRaw { get; }

    /// <summary>The facility's unit ID while it stands (a site or finished), or 0.</summary>
    public uint Facility { get; internal set; }

    /// <summary>The westmost runway segment index (segments run <see cref="West"/> to <see cref="East"/>).</summary>
    public int West { get; internal set; }

    /// <summary>The eastmost runway segment index; below <see cref="West"/> while it has no runway.</summary>
    public int East { get; internal set; }

    /// <summary>Whether it has runway segments (sites or finished).</summary>
    public bool HasRunway => East >= West;

    /// <summary>The runway segments' unit IDs, from <see cref="West"/> to <see cref="East"/> (0: an empty slot).</summary>
    public IReadOnlyList<uint> Segments => SegmentList;

    internal List<uint> SegmentList { get; } = [];

    /// <summary>The rows, sorted by (column, row).</summary>
    public IReadOnlyList<AirfieldRow> Rows => RowList;

    internal List<AirfieldRow> RowList { get; } = [];

    /// <summary>
    /// The first row's site while it lays the first runway segments on its one bar (they are passive
    /// sites that follow its work), or 0.
    /// </summary>
    public uint LeadRow { get; internal set; }

    /// <summary>The aircraft holding the runway (one at a time: from its clearance to land until it turns
    /// off onto a connector, or from its line-up until the release time after wheels-up), or 0.</summary>
    public uint RunwayLock { get; internal set; }

    /// <summary>The landing queue: (request tick, aircraft), sorted; a go-around rejoins at tick 0.</summary>
    public IReadOnlyList<(uint Tick, uint Aircraft)> Landings => LandingList;

    internal List<(uint Tick, uint Aircraft)> LandingList { get; } = [];

    /// <summary>The aircraft waiting at the holding point to take off: (arrival tick, aircraft), sorted.</summary>
    public IReadOnlyList<(uint Tick, uint Aircraft)> Takeoffs => TakeoffList;

    internal List<(uint Tick, uint Aircraft)> TakeoffList { get; } = [];

    // Enqueue inserts (tick, id) into a queue kept sorted by tick, then ID.
    internal static void Enqueue(List<(uint Tick, uint Aircraft)> q, uint tick, uint id)
    {
        var i = 0;
        while (i < q.Count && (q[i].Tick < tick || (q[i].Tick == tick && q[i].Aircraft < id)))
        {
            i++;
        }
        q.Insert(i, (tick, id));
    }

    // Dequeue removes an aircraft from a queue; it reports whether it was there.
    internal static bool Dequeue(List<(uint Tick, uint Aircraft)> q, uint id)
    {
        for (var i = 0; i < q.Count; i++)
        {
            if (q[i].Aircraft == id)
            {
                q.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Returns the row at (column, row), or null.</summary>
    public AirfieldRow? RowAt(int col, int row)
    {
        foreach (var r in RowList)
        {
            if (r.Col == col && r.Row == row)
            {
                return r;
            }
        }
        return null;
    }

    // InsertRow keeps the rows sorted by (column, row).
    internal void InsertRow(AirfieldRow r)
    {
        var i = 0;
        while (i < RowList.Count && (RowList[i].Col < r.Col || (RowList[i].Col == r.Col && RowList[i].Row < r.Row)))
        {
            i++;
        }
        RowList.Insert(i, r);
    }
}

/// <summary>One row of an airfield: its unit and its stations (one per row slot of the type).</summary>
public sealed class AirfieldRow
{
    internal AirfieldRow(int col, int row, uint unit, IReadOnlyList<RowSlot> slots)
    {
        Col = col;
        Row = row;
        Unit = unit;
        var stations = new AirfieldStation[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            stations[i] = new AirfieldStation(i, slots[i].Kind);
        }
        Stations = stations;
    }

    /// <summary>Its column (x = row width times this).</summary>
    public int Col { get; }

    /// <summary>Its row in the column, 0 by the taxiway.</summary>
    public int Row { get; }

    /// <summary>The row's unit ID (a site or finished), or 0 once gone.</summary>
    public uint Unit { get; internal set; }

    /// <summary>Its stations, in the type's slot order; spawned when the row is finished.</summary>
    public IReadOnlyList<AirfieldStation> Stations { get; }
}

/// <summary>A station of a row: its slot, kind, unit, and the aircraft parked at or bound for it.</summary>
public sealed class AirfieldStation
{
    internal AirfieldStation(int slot, StationKind kind)
    {
        Slot = slot;
        Kind = kind;
    }

    /// <summary>Its slot in the row (an index into <see cref="AirfieldType.RowSlots"/>).</summary>
    public int Slot { get; }

    /// <summary>Loading or normal.</summary>
    public StationKind Kind { get; }

    /// <summary>The station's unit ID, or 0 (not spawned yet, or gone).</summary>
    public uint Unit { get; internal set; }

    /// <summary>The aircraft parked here, or 0 (one aircraft per station).</summary>
    public uint Occupant { get; internal set; }

    /// <summary>The aircraft this station is reserved for (landing or taxiing to it), or 0.</summary>
    public uint ReservedBy { get; internal set; }
}

/// <summary>What a taxi graph node is.</summary>
public enum TaxiNodeKind : byte
{
    /// <summary>A runway end, on the centre line.</summary>
    RunwayEnd,

    /// <summary>The runway centre line where a connector leaves it.</summary>
    RunwayConnector,

    /// <summary>The parallel taxiway where a connector meets it.</summary>
    TaxiwayConnector,

    /// <summary>The parallel taxiway at a column's lane mouth.</summary>
    LaneMouth,

    /// <summary>A row's lane node, at the row's centre.</summary>
    Lane,

    /// <summary>A station's stop mark (its park-in path's last point).</summary>
    Stop,
}

/// <summary>A node of an airfield's taxi graph.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="At">Where it is on the map.</param>
/// <param name="Local">Where it is in the airfield's frame, tenths of a cell.</param>
/// <param name="Station">For a stop: the station's unit ID; otherwise 0.</param>
public readonly record struct TaxiNode(TaxiNodeKind Kind, Vec At, (int X, int Y) Local, uint Station);

/// <summary>
/// An airfield's taxi graph: derived from its hashed pieces, never hashed itself (rebuilt on
/// demand, in a fixed order). Edges join node indices, smaller index first.
/// </summary>
/// <param name="Nodes">The nodes.</param>
/// <param name="Edges">The edges.</param>
public sealed record TaxiGraph(IReadOnlyList<TaxiNode> Nodes, IReadOnlyList<(int A, int B)> Edges);

/// <summary>A rectangle of an airfield's plan or a piece, in map cells (top-left, size).</summary>
/// <param name="Role">The piece it is for (a corridor is the runway segment role).</param>
/// <param name="Cell">The top-left cell.</param>
/// <param name="W">Width in cells.</param>
/// <param name="H">Height in cells.</param>
/// <param name="Corridor">Whether it is an approach corridor (only checked to be on the map).</param>
public readonly record struct AirfieldRect(AirfieldRole Role, Cell Cell, int W, int H, bool Corridor);
