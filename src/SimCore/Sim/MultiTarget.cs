namespace SimCore.Sim;

/// <summary>
/// A multi-target objective (<see cref="CommandKind.MultiTarget"/>; docs/design.md, "Multi-target ground attack"): one player's
/// ordered points and enemy units, shared by the units the order was given to. Renderers read it (the markers, the lines from
/// each unit to its point, "points hit x / y"); only the sim writes it. Hashed by the world.
/// </summary>
public sealed class MultiTarget
{
    internal MultiTarget(uint id, byte owner, uint since) => (Id, Owner, Since) = (id, owner, since);

    /// <summary>The objective's ID: assigned in increasing order, never reused.</summary>
    public uint Id { get; }

    /// <summary>The player.</summary>
    public byte Owner { get; }

    /// <summary>The tick the order ran.</summary>
    public uint Since { get; }

    internal List<MultiPoint> PointList { get; } = [];

    internal List<MultiMember> MemberList { get; } = []; // sorted by unit ID, ascending

    /// <summary>The points in the order they were placed (their numbers are the index plus one).</summary>
    public IReadOnlyList<MultiPoint> Points => PointList;

    /// <summary>The units still on the objective, by ID, each with the point it is on now.</summary>
    public IReadOnlyList<MultiMember> Members => MemberList;

    /// <summary>The tick every point was first done (hit, or its unit gone), or 0 while one is not.</summary>
    public uint CompleteTick { get; internal set; }

    /// <summary>How many points are done: hit by their own shot, or an aimed-at unit gone before one.</summary>
    public int Done
    {
        get
        {
            var n = 0;
            foreach (var p in PointList)
            {
                n += p.Done ? 1 : 0;
            }
            return n;
        }
    }

    // Member is unit's membership, or null.
    internal MultiMember? Member(uint unit)
    {
        int lo = 0, hi = MemberList.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (MemberList[mid].Unit < unit)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < MemberList.Count && MemberList[lo].Unit == unit ? MemberList[lo] : null;
    }
}

/// <summary>One point of a <see cref="MultiTarget"/>: a ground point or an enemy unit.</summary>
public sealed class MultiPoint
{
    internal MultiPoint(Cell cell, uint target) => (Cell, Target) = (cell, target);

    /// <summary>The point (for a unit: its last known cell).</summary>
    public Cell Cell { get; internal set; }

    /// <summary>The enemy unit aimed at, or 0 for a ground point.</summary>
    public uint Target { get; }

    /// <summary>The shots delivered at it: munitions, volleys or bomb groups released (a point counts as hit on release).</summary>
    public int Shots { get; internal set; }

    /// <summary>How many units are on it now.</summary>
    public int Assigned { get; internal set; }

    /// <summary>The tick of its first shot, or 0.</summary>
    public uint HitTick { get; internal set; }

    /// <summary>The aimed-at unit is gone (destroyed, or inside something): it takes no more shots.</summary>
    public bool Gone { get; internal set; }

    /// <summary>Hit by a shot of its own.</summary>
    public bool Hit => Shots > 0;

    /// <summary>Hit, or its unit gone: the marker ticks off.</summary>
    public bool Done => Shots > 0 || Gone;
}

/// <summary>One unit on a <see cref="MultiTarget"/>.</summary>
public sealed class MultiMember
{
    internal MultiMember(uint unit) => Unit = unit;

    /// <summary>The unit.</summary>
    public uint Unit { get; }

    /// <summary>The index of the point it is on now, or -1 while it waits for one it can take.</summary>
    public int Point { get; internal set; } = -1;

    /// <summary>The first tick a shot of the unit counts for its point (uint.MaxValue: a volley of an earlier order still firing).</summary>
    internal uint From { get; set; }

    /// <summary>The points (bit i: point i) its order was refused or given up at: never given to it again.</summary>
    internal ulong Refused { get; set; }

    /// <summary>A bomber's fire mode before the objective switched it from a spread mode to salvo aims (-1: not switched).</summary>
    internal int ModeBefore { get; set; } = -1;
}
