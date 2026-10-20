using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>What a piece of an airfield is (an archetype's <see cref="Archetype.Airfield"/>).</summary>
public enum AirfieldRole : byte
{
    /// <summary>The facility building: it founds the airfield and carries its loading stations.</summary>
    Facility,

    /// <summary>A row: one loading and one normal station with a slice of lane between them.</summary>
    Row,

    /// <summary>A runway segment: runway, grass strip with a connector, parallel taxiway.</summary>
    Segment,

    /// <summary>A station one aircraft parks at (spawned by its row).</summary>
    Station,

    /// <summary>A main air base's core (H1): placed as one unit, it founds the airfield and, finished, lays its built-in pieces.</summary>
    Core,

    /// <summary>A core's hangar (H1): a target piece holding the heavy bays and the production queue.</summary>
    Hangar,

    /// <summary>A core's built-in radar (H1): a target piece on its own slot.</summary>
    Radar,
}

/// <summary>A station's kind.</summary>
public enum StationKind : byte
{
    /// <summary>A loading station (a shelter): faster rearm and repair, needs the facility standing.</summary>
    Loading,

    /// <summary>A normal station (an open stand).</summary>
    Normal,

    /// <summary>A heavy bay in a main air base's hangar (H1): for the classes its station block allows (L and XL).</summary>
    Heavy,
}

/// <summary>An archetype's role in an airfield (content <c>airfield</c>).</summary>
public sealed class AirfieldPieceSpec
{
    /// <summary>The piece's role.</summary>
    public required AirfieldRole Role { get; init; }

    /// <summary>The airfield type it belongs to (an index into <see cref="ContentSet.AirfieldTypes"/>).</summary>
    public required int Type { get; init; }

    /// <summary>A station's kind.</summary>
    public StationKind Kind { get; init; }

    /// <summary>A station that works only while its airfield's facility stands.</summary>
    public bool NeedsFacility { get; init; }

    /// <summary>A station's rearm rate, percent of the aircraft's 1x.</summary>
    public int RearmPercent { get; init; }

    /// <summary>A station's repair rate, percent of the aircraft's 1x.</summary>
    public int RepairPercent { get; init; }

    /// <summary>A station's refuel rate, percent of the aircraft's 1x.</summary>
    public int RefuelPercent { get; init; }

    /// <summary>The widest span a station takes (zero: any the airfield type serves).</summary>
    public Fixed MaxSpan { get; init; }

    /// <summary>The smallest aircraft class a station takes (H1: the heavy bays take only L and XL).</summary>
    public AircraftClass MinClass { get; init; } = AircraftClass.S;

    /// <summary>The largest aircraft class a station takes (H1: shelters and stands take S and M, so no heavy parks there).</summary>
    public AircraftClass MaxClass { get; init; } = AircraftClass.XL;

    /// <summary>Whether a station takes aircraft of class c.</summary>
    public bool Takes(AircraftClass c) => c >= MinClass && c <= MaxClass;

    /// <summary>
    /// A station's block as it serves while its airfield's facility is down on a type that keeps refuelling then
    /// (<see cref="AirfieldType.RefuelWithoutFacility"/>, H1): refuel only. Derived, not content.
    /// </summary>
    internal AirfieldPieceSpec? RefuelOnly { get; set; }
}

/// <summary>
/// One heavy bay of a main air base's hangar (H1; content <c>core.bays</c>): its rectangle in the hangar
/// (cells from the hangar's left edge, the hangar's full depth), its lane node on the apron and its station slot.
/// The bay's own frame (tenths) has its origin at the bay's top-left corner; the lane node may lie outside it
/// (in front of the door, on the apron).
/// </summary>
public sealed class BaySlot
{
    /// <summary>The bay's left edge in the hangar, in cells.</summary>
    public required int X { get; init; }

    /// <summary>The bay's width, in cells.</summary>
    public required int W { get; init; }

    /// <summary>The bay's depth (the hangar's), in cells.</summary>
    public required int H { get; init; }

    /// <summary>The lane node (where it leaves the apron's taxi line for its door), tenths in the bay's frame.</summary>
    public required (int X, int Y) Lane { get; init; }

    /// <summary>The station slot: kind heavy, heading, pushback and the park-in and park-out paths in the bay's frame.</summary>
    public required RowSlot Slot { get; init; }

    /// <summary>The bay's slots as a row's: its one slot.</summary>
    public required IReadOnlyList<RowSlot> Slots { get; init; }
}

/// <summary>
/// A main air base's core layout (H1; content <c>core</c>): what the core lays when it is finished, in cells of
/// the airfield's frame (the core's footprint has its top-left corner at the local origin): its built-in runway
/// segments (0 to Segments - 1), the facility (the command building), the hangar and its bays, the radar, the
/// apron band (the linking reference) and its built-in rows (rows 0 to Rows - 1 of the row grid's column 0).
/// </summary>
public sealed class CoreLayout
{
    /// <summary>The built-in runway segments.</summary>
    public required int Segments { get; init; }

    /// <summary>The facility's top-left cell.</summary>
    public required (int X, int Y) Facility { get; init; }

    /// <summary>The hangar's top-left cell.</summary>
    public required (int X, int Y) Hangar { get; init; }

    /// <summary>The radar's top-left cell.</summary>
    public required (int X, int Y) Radar { get; init; }

    /// <summary>The apron band (y from, y to) along the core's whole length.</summary>
    public required (int Y0, int Y1) Apron { get; init; }

    /// <summary>The built-in rows (stacked in column 0 of the row grid).</summary>
    public required int Rows { get; init; }

    /// <summary>The hangar's heavy bays, west to east.</summary>
    public required IReadOnlyList<BaySlot> Bays { get; init; }
}

/// <summary>
/// One station slot of a row (row-local: tenths of a cell from the row's top-left corner in the
/// airfield's frame, +x along the runway, +y away from it).
/// </summary>
public sealed class RowSlot
{
    /// <summary>The station kind parked here.</summary>
    public required StationKind Kind { get; init; }

    /// <summary>The station's left edge in the row, in cells.</summary>
    public required int X { get; init; }

    /// <summary>The parked aircraft's facing in the airfield's frame (0 north, 8 east, as <see cref="Facing"/>).</summary>
    public required int Heading { get; init; }

    /// <summary>Whether it leaves by pushback (it parked nose in) rather than taxiing forward.</summary>
    public required bool Pushback { get; init; }

    /// <summary>The park-in path from the lane node to the stop mark (its last point), tenths of a cell.</summary>
    public required IReadOnlyList<(int X, int Y)> ParkIn { get; init; }

    /// <summary>The way out from the stop mark back to the lane node, tenths of a cell.</summary>
    public required IReadOnlyList<(int X, int Y)> ParkOut { get; init; }
}

/// <summary>A crater tier: its radius and the work one worker needs to fill it.</summary>
/// <param name="Radius">The crater's radius.</param>
/// <param name="RepairTicks">Ticks one worker takes to fill it.</param>
public sealed record CraterSpec(Fixed Radius, int RepairTicks);

/// <summary>What a blast does to a runway it lands on (a weapon's or strike's <c>runway</c>).</summary>
public enum CraterKind : byte
{
    /// <summary>No crater (it may still wear the runway).</summary>
    None,

    /// <summary>A small crater on impact.</summary>
    Small,

    /// <summary>A large crater on impact.</summary>
    Large,
}

/// <summary>A weapon's or strike's effect on a runway: a crater on impact, or wear that adds up.</summary>
/// <param name="Crater">The crater it digs on impact, or none.</param>
/// <param name="Wear">The wear it adds to the patch it lands in (0 for a cratering weapon).</param>
public sealed record RunwayEffect(CraterKind Crater, int Wear);

/// <summary>
/// An airfield type (content <c>airfieldTypes</c>; the game repo's docs/airfield-design.md): the
/// grid its pieces snap to, the classes it serves, its pieces and the numbers the landing, service
/// and crater rules read. Grid sizes are whole cells. The airfield's frame: +x along the runway,
/// +y away from it; the runway strip is y 0 to <see cref="RunwayCells"/>, rows start at
/// <see cref="SegmentDepth"/>.
/// </summary>
public sealed class AirfieldType
{
    /// <summary>The position in <see cref="ContentSet.AirfieldTypes"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The stable data key.</summary>
    public required string Id { get; init; }

    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>The largest aircraft class it serves (and every smaller one).</summary>
    public required AircraftClass MaxClass { get; init; }

    /// <summary>The facility archetype (an index into <see cref="ContentSet.Archetypes"/>).</summary>
    public required int Facility { get; init; }

    /// <summary>The row archetype.</summary>
    public required int Row { get; init; }

    /// <summary>The runway segment archetype.</summary>
    public required int Segment { get; init; }

    /// <summary>The loading station archetype.</summary>
    public required int Loading { get; init; }

    /// <summary>The normal station archetype.</summary>
    public required int Normal { get; init; }

    /// <summary>The core archetype (H1: a main air base's), or -1: the facility founds the airfield instead.</summary>
    public int Core { get; init; } = -1;

    /// <summary>The hangar archetype (H1), or -1.</summary>
    public int Hangar { get; init; } = -1;

    /// <summary>The radar archetype (H1), or -1.</summary>
    public int Radar { get; init; } = -1;

    /// <summary>The heavy bay archetype (H1), or -1.</summary>
    public int Heavy { get; init; } = -1;

    /// <summary>The core's layout (H1), or null for a type its facility founds.</summary>
    public CoreLayout? CoreLayout { get; init; }

    /// <summary>Whether this type is founded by a core (a main air base).</summary>
    public bool HasCore => CoreLayout is not null;

    /// <summary>The row grid's origin in cells of the airfield's frame (the forward airfield: (0, the segment depth)).</summary>
    public required (int X, int Y) RowOrigin { get; init; }

    /// <summary>The most row columns (0: no limit).</summary>
    public int MaxColumns { get; init; }

    /// <summary>The most rows in a column, built-in ones included (0: no limit).</summary>
    public int MaxRows { get; init; }

    /// <summary>A runway segment holds a connector every this many segments (segment k when k is a multiple of it).</summary>
    public int ConnectorEvery { get; init; } = 1;

    /// <summary>Whether runway segment k holds a connector.</summary>
    public bool HasConnector(int k) => ((k % ConnectorEvery) + ConnectorEvery) % ConnectorEvery == 0;

    /// <summary>
    /// Whether its stations still refuel while the facility is down (H1, the main air base: the owner's "landing and
    /// refuel go on"); a station that needs the facility then only refuels. The forward airfield gives nothing then.
    /// </summary>
    public bool RefuelWithoutFacility { get; init; }

    /// <summary>
    /// The link reach (cells, H1): another of the owner's airfields whose facility lies within it of this type's apron
    /// band is linked to it (<see cref="Sim.World.AirfieldLinks"/>); 0: no links.
    /// </summary>
    public int LinkCells { get; init; }

    /// <summary>A station's side, in cells (stations are square).</summary>
    public required int StationCells { get; init; }

    /// <summary>The lane's width between a row's stations, in cells.</summary>
    public required int LaneCells { get; init; }

    /// <summary>A row's length along x: its stations and its lane slice, in cells.</summary>
    public int RowW => 2 * StationCells + LaneCells;

    /// <summary>A row's depth along y, in cells.</summary>
    public int RowH => StationCells;

    /// <summary>A runway segment's length along the runway, in cells.</summary>
    public required int SegmentCells { get; init; }

    /// <summary>The runway strip's width, in cells.</summary>
    public required int RunwayCells { get; init; }

    /// <summary>The grass strip's width between runway and taxiway, in cells.</summary>
    public required int GrassCells { get; init; }

    /// <summary>The parallel taxiway's width, in cells.</summary>
    public required int TaxiwayCells { get; init; }

    /// <summary>A segment's depth (runway, grass, taxiway), in cells: where the rows begin.</summary>
    public int SegmentDepth => RunwayCells + GrassCells + TaxiwayCells;

    /// <summary>Where a segment's connector taxiway crosses the grass, from the segment's start.</summary>
    public required Fixed Connector { get; init; }

    /// <summary>The fewest segments of a runway (the first row lays them).</summary>
    public required int MinSegments { get; init; }

    /// <summary>The approach corridor beyond each runway end that must be on the map, in cells.</summary>
    public required int CorridorCells { get; init; }

    /// <summary>The gap between the facility and the first row, in cells.</summary>
    public required int FacilityGapCells { get; init; }

    /// <summary>The largest height difference (raw) of the ground under a piece from the airfield's pad height.</summary>
    public required long MaxReliefRaw { get; init; }

    /// <summary>A row's station slots (equal numbers of loading and normal stations).</summary>
    public required IReadOnlyList<RowSlot> RowSlots { get; init; }

    /// <summary>Holding: the hold point's distance out on the approach side.</summary>
    public required Fixed HoldDistance { get; init; }

    /// <summary>Holding: the orbit's radius round the hold point.</summary>
    public required Fixed HoldRadius { get; init; }

    /// <summary>Holding: the first queue place's height (raw).</summary>
    public required long HoldHeightRaw { get; init; }

    /// <summary>Holding: the height added per queue place (raw).</summary>
    public required long HoldStepRaw { get; init; }

    /// <summary>Holding: the stacked places (more aircraft share the top one).</summary>
    public required int HoldPlaces { get; init; }

    /// <summary>Ticks the runway lock is kept after wheels-up.</summary>
    public required int LockReleaseTicks { get; init; }

    /// <summary>Ticks an aircraft waits at a lane mouth before it goes ahead anyway.</summary>
    public required int TaxiWaitTicks { get; init; }

    /// <summary>Percent of max HP a parked aircraft loses when its station collapses on it.</summary>
    public required int CollapseDamage { get; init; }

    /// <summary>Percent of max HP an aircraft loses running into a crater it could not stop short of.</summary>
    public required int CraterHitDamage { get; init; }

    /// <summary>A small crater.</summary>
    public required CraterSpec SmallCrater { get; init; }

    /// <summary>A large crater.</summary>
    public required CraterSpec LargeCrater { get; init; }

    /// <summary>The runway closed beyond a crater's edge on either side.</summary>
    public required Fixed CraterMargin { get; init; }

    /// <summary>The most craters an airfield keeps (past it a new one enlarges the nearest).</summary>
    public required int MaxCraters { get; init; }

    /// <summary>The side of a wear patch, in cells.</summary>
    public required int WearPatchCells { get; init; }

    /// <summary>The wear at which a patch craters.</summary>
    public required int WearToCrater { get; init; }

    /// <summary>Percent of a wearing hit each of the 4 neighbouring patches takes.</summary>
    public required int WearNeighbourPercent { get; init; }

    /// <summary>Wear one worker takes away a second.</summary>
    public required int WearRepairPerSecond { get; init; }

    /// <summary>Whether it serves an aircraft of class c (its own class and every smaller one).</summary>
    public bool Serves(AircraftClass c) => c <= MaxClass;
}
