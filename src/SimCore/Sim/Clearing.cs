namespace SimCore.Sim;

/// <summary>What a bulldozer on the Clear up order is working on (<see cref="ClearJob.Kind"/>).</summary>
public enum ClearJobKind : byte
{
    /// <summary>Between jobs.</summary>
    None,

    /// <summary>A standing tree.</summary>
    Tree,

    /// <summary>A fallen trunk.</summary>
    Trunk,

    /// <summary>A building.</summary>
    Building,

    /// <summary>Undergrowth or forest with no trunk left: last, once the rest is done.</summary>
    Brush,
}

/// <summary>
/// A bulldozer's Clear up order as it stands (<see cref="Unit.Clearing"/>; docs/design.md, "Clear up"): its area, its job, what is left.
/// Game state (<see cref="HashInto"/>). The counts are as of its last job's start.
/// </summary>
public sealed class ClearJob
{
    private readonly List<long> _skipped = []; // ascending

    internal ClearJob(Cell from, Cell to)
    {
        From = from;
        To = to;
    }

    /// <summary>The area's top-left cell (inclusive, on the map).</summary>
    public Cell From { get; }

    /// <summary>The area's bottom-right cell (inclusive, on the map).</summary>
    public Cell To { get; }

    /// <summary>The kind of job it is on.</summary>
    public ClearJobKind Kind { get; internal set; }

    /// <summary>The job's cell (a tree, undergrowth, the fallen trunk's nearest cell in the area).</summary>
    public Cell Cell { get; internal set; }

    /// <summary>The job's building, or 0.</summary>
    public uint Building { get; internal set; }

    /// <summary>The ticks of work on this job so far.</summary>
    public int Work { get; internal set; }

    /// <summary>Whether its blade is on the job this tick (in reach of a tree or a building): the client's dust.</summary>
    public bool Working { get; internal set; }

    /// <summary>The standing trees left in the area.</summary>
    public int Trees { get; internal set; }

    /// <summary>The fallen trunks left in the area.</summary>
    public int Logs { get; internal set; }

    /// <summary>The buildings left in the area.</summary>
    public int Buildings { get; internal set; }

    /// <summary>The cells of undergrowth left in the area.</summary>
    public int Brush { get; internal set; }

    /// <summary>The targets it gave up on, because it could not reach them.</summary>
    public int SkippedCount => _skipped.Count;

    internal FallenTrunk Trunk { get; private set; }

    internal int Carry { get; set; }   // HP worn off a building, the part under 1 HP: a share of its clear ticks

    internal long Best { get; set; }   // the nearest it has got to this job, squared raw

    internal int Stuck { get; set; }   // ticks it has not got nearer

    /// <summary>Whether cell c is in the area.</summary>
    public bool Holds(Cell c) => c.X >= From.X && c.X <= To.X && c.Y >= From.Y && c.Y <= To.Y;

    internal bool Skipped(long key) => _skipped.Count != 0 && _skipped.BinarySearch(key) >= 0;

    internal void Skip(long key)
    {
        var at = _skipped.BinarySearch(key);
        if (at < 0)
        {
            _skipped.Insert(~at, key);
        }
    }

    internal void Start(ClearJobKind kind, Cell cell, FallenTrunk trunk, uint building)
    {
        Kind = kind;
        Cell = cell;
        Trunk = trunk;
        Building = building;
        Work = 0;
        Carry = 0;
        Best = long.MaxValue;
        Stuck = 0;
    }

    internal void HashInto(ref WordHash h)
    {
        h.Put(0xC1EB);
        h.PutSigned(From.X);
        h.PutSigned(From.Y);
        h.PutSigned(To.X);
        h.PutSigned(To.Y);
        h.PutByte((byte)Kind);
        h.PutSigned(Cell.X);
        h.PutSigned(Cell.Y);
        h.Put(Building);
        h.PutSigned(Trunk.Stump.X);
        h.PutSigned(Trunk.Stump.Y);
        h.Put(Trunk.ExpiresAt);
        h.PutSigned(Work);
        h.PutSigned(Carry);
        h.PutSigned(Stuck);
        h.PutSigned(Best);
        h.PutSigned(Trees);
        h.PutSigned(Logs);
        h.PutSigned(Buildings);
        h.PutSigned(Brush);
        h.Put(Working ? 1UL : 0);
        h.PutSigned(_skipped.Count);
        foreach (var k in _skipped)
        {
            h.PutSigned(k);
        }
    }
}
