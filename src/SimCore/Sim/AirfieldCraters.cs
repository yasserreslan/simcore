using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// A crater in an airfield's runway (the game repo's docs/airfield-design.md, 1.1): dug by a blast
/// on the runway strip, or formed where light rockets wore a patch through. Open, it closes the
/// runway for its diameter plus the type's margin either side; builders on a Repair order fill it. Filled, it stays in the list as a repaired patch of fresh
/// slabs the client draws (it no longer closes anything).
/// </summary>
public sealed class RunwayCrater
{
    internal RunwayCrater(uint id, Fixed x, Fixed y, Vec at, CraterKind kind, CraterSpec spec)
    {
        Id = id;
        X = x;
        Y = y;
        At = at;
        Resize(kind, spec);
    }

    /// <summary>Its ID within its airfield (1, 2, ...: never reused).</summary>
    public uint Id { get; }

    /// <summary>Its centre along the runway: the airfield frame's x, in cells from its local origin.</summary>
    public Fixed X { get; }

    /// <summary>Its centre across the runway strip: the airfield frame's y, in cells (0 to the strip's width).</summary>
    public Fixed Y { get; }

    /// <summary>Its centre on the map.</summary>
    public Vec At { get; }

    /// <summary>Small or large.</summary>
    public CraterKind Kind { get; private set; }

    /// <summary>Its radius.</summary>
    public Fixed Radius { get; private set; }

    /// <summary>The work filled so far (builder-rate percent times ticks; full at <see cref="WorkTotal"/>).</summary>
    public int Work { get; internal set; }

    /// <summary>The work that fills it: its tier's repair ticks times 100.</summary>
    public int WorkTotal { get; private set; }

    /// <summary>Whether it was filled: a repaired patch now (it closes nothing).</summary>
    public bool Repaired { get; internal set; }

    // Resize sets its tier (a new crater, or the nearest one enlarged past the cap): its fill starts over.
    internal void Resize(CraterKind kind, CraterSpec spec)
    {
        Kind = kind;
        Radius = spec.Radius;
        WorkTotal = spec.RepairTicks * 100;
        Work = 0;
    }
}

/// <summary>An open stretch of runway between craters, in the airfield frame's x (cells).</summary>
/// <param name="From">Its west end.</param>
/// <param name="To">Its east end.</param>
public readonly record struct RunwayStretch(Fixed From, Fixed To)
{
    /// <summary>Its length.</summary>
    public Fixed Length => To - From;
}

// The airfield's runway damage (milestone M3a): its craters and the wear of its 2 x 2 cell patches.
// Hashed only while any of it is present.
public sealed partial class Airfield
{
    internal List<RunwayCrater> CraterList { get; } = []; // sorted by (X, Id)

    /// <summary>The runway's craters, open and repaired, sorted by position along the runway, then ID.</summary>
    public IReadOnlyList<RunwayCrater> Craters => CraterList;

    /// <summary>The last crater ID handed out (0: none yet).</summary>
    public uint NextCrater { get; internal set; }

    /// <summary>
    /// The runway's wear, one value per patch (0 to the type's wearToCrater), or empty while nothing
    /// has worn it. Patch i is <see cref="WearAcross"/> patches across (index i % WearAcross, from the
    /// strip's runway edge y 0) and along the runway i / WearAcross patches from segment
    /// <see cref="WearWest"/>'s start. Patches are <see cref="AirfieldType.WearPatchCells"/> on a side.
    /// </summary>
    public IReadOnlyList<byte> Wear => WearArray;

    /// <summary>The runway segment index the wear array starts at.</summary>
    public int WearWest { get; internal set; }

    /// <summary>Patches across the runway strip.</summary>
    public int WearAcross => Type.WearPatchCells > 0 ? Type.RunwayCells / Type.WearPatchCells : 0;

    internal byte[] WearArray { get; set; } = [];

    internal int[] WearFill { get; set; } = []; // repair work toward taking the next wear point off each patch

    /// <summary>The craters still open (not repaired), in list order.</summary>
    public IEnumerable<RunwayCrater> OpenCraters
    {
        get
        {
            foreach (var c in CraterList)
            {
                if (!c.Repaired)
                {
                    yield return c;
                }
            }
        }
    }

    /// <summary>The runway's west end, in the airfield frame's x (cells).</summary>
    public Fixed RunwayWestX => Fixed.FromInt(Type.SegmentCells * West);

    /// <summary>The runway's east end, in the airfield frame's x (cells).</summary>
    public Fixed RunwayEastX => Fixed.FromInt(Type.SegmentCells * (East + 1));

    /// <summary>The connectors' x along the runway (one per segment, west to east), in cells.</summary>
    public List<Fixed> ConnectorXs()
    {
        var list = new List<Fixed>();
        for (var k = West; k <= East; k++)
        {
            list.Add(Fixed.FromInt(Type.SegmentCells * k) + Type.Connector);
        }
        return list;
    }

    /// <summary>
    /// The runway's open stretches, west to east: the runway minus what each open crater closes (its
    /// diameter plus <see cref="AirfieldType.CraterMargin"/> either side). Empty without a runway. Only
    /// craters count here; whether every segment is finished is the caller's concern.
    /// </summary>
    public List<RunwayStretch> OpenStretches()
    {
        var list = new List<RunwayStretch>();
        if (!HasRunway)
        {
            return list;
        }
        var closed = new List<(long A, long B)>();
        foreach (var c in CraterList)
        {
            if (!c.Repaired)
            {
                var r = c.Radius.Raw + Type.CraterMargin.Raw;
                closed.Add((c.X.Raw - r, c.X.Raw + r));
            }
        }
        closed.Sort();
        var at = RunwayWestX.Raw;
        var end = RunwayEastX.Raw;
        foreach (var (a, b) in closed)
        {
            if (a > at)
            {
                list.Add(new RunwayStretch(Fixed.FromRaw(at), Fixed.FromRaw(Math.Min(a, end))));
            }
            at = Math.Max(at, b);
            if (at >= end)
            {
                break;
            }
        }
        if (at < end)
        {
            list.Add(new RunwayStretch(Fixed.FromRaw(at), Fixed.FromRaw(end)));
        }
        list.RemoveAll(s => s.To <= s.From);
        return list;
    }

    /// <summary>
    /// The longest open stretch at least minLength long that holds a connector (where an aircraft
    /// can turn off or line up), or null when none does: the runway is closed for that aircraft. Ties
    /// go to the westmost stretch.
    /// </summary>
    public RunwayStretch? LongestOpenStretch(Fixed minLength)
    {
        RunwayStretch? best = null;
        var connectors = ConnectorXs();
        foreach (var s in OpenStretches())
        {
            if (s.Length < minLength || (best is { } b && s.Length <= b.Length))
            {
                continue;
            }
            foreach (var x in connectors)
            {
                if (x >= s.From && x <= s.To)
                {
                    best = s;
                    break;
                }
            }
        }
        return best;
    }

    /// <summary>The wear of the patch at index i (along * <see cref="WearAcross"/> + across), or 0.</summary>
    public int WearAt(int i) => i >= 0 && i < WearArray.Length ? WearArray[i] : 0;
}
