using SimCore.Model;

namespace SimCore.Sim;

// Infantry combat state (INF-E1b-1; docs/design.md, "Infantry combat: the ground and the senses"; the game repo's
// docs/plans/infantry-battle-rules.md sections 4, 5, 9, 21 and 22). A soldier's posture, suppression and what he knows of the
// enemy (SoldierFight, hashed); a squad's picture (SquadMind, hashed); danger zones (World, hashed); the cover data per cell
// (CoverMap: a cache built from the map, not hashed).

/// <summary>A soldier's body posture (brief 6.3).</summary>
public enum Posture : byte
{
    /// <summary>Standing.</summary>
    Stand,
    /// <summary>Crouched (moving low, or ducked in cover).</summary>
    Crouch,
    /// <summary>Kneeling (stationary, firing).</summary>
    Kneel,
    /// <summary>Lying down.</summary>
    Prone,
}

/// <summary>The cover on one side of a cell (5.1): none, low hard, high hard, soft (a trunk).</summary>
public enum CoverKind : byte
{
    /// <summary>No cover.</summary>
    None,
    /// <summary>Low hard cover (a waist-high wall, rubble, a fallen trunk, a crest).</summary>
    Low,
    /// <summary>High hard cover (a building, a tall structure).</summary>
    High,
    /// <summary>Soft cover (a tree trunk).</summary>
    Soft,
}

/// <summary>How a contact is known (4.2), best first.</summary>
public enum ContactSource : byte
{
    /// <summary>Seen this think.</summary>
    SeenNow,
    /// <summary>Seen earlier: its last known cell.</summary>
    SeenBefore,
    /// <summary>Heard: an area.</summary>
    Heard,
    /// <summary>The direction a hit came from.</summary>
    HitFrom,
    /// <summary>Told by a squadmate or a nearby squad.</summary>
    Reported,
}

/// <summary>One enemy a soldier or squad knows about (4.2): a unit, or a heard area (no unit ID).</summary>
public struct Contact
{
    /// <summary>The enemy unit's ID; 0 for a heard area.</summary>
    public uint Enemy;

    /// <summary>Its known cell: exact when seen now, the last known cell, or a heard area's centre.</summary>
    public Cell At;

    /// <summary>How it is known.</summary>
    public ContactSource Source;

    /// <summary>Its danger class (4.4).</summary>
    public CombatClass Class;

    /// <summary>A heard area's merged shot sources, 1 to 3.</summary>
    public byte Sources;

    /// <summary>When it was last refreshed (a reported one: when its source last had it).</summary>
    public uint Tick;

    /// <summary>From this tick he acts on it (his reaction time, 4.6).</summary>
    public uint KnownAt;

    /// <summary>It fired at him or his squad (aimed at a member or landing within 1.5 cells of one) in the last 3 s.</summary>
    public bool FiringAtUs;

    /// <summary>In a squad picture: members take it from this tick (the shout, or the neighbours' radio).</summary>
    public uint ShareAt;

    /// <summary>In a squad picture: told by another squad (never passed on: no echo, 4.3).</summary>
    public bool Foreign;

    /// <summary>A heard area: the last shooter heard in it (a second shooter adds a source).</summary>
    public uint Heard;

    internal readonly void HashInto(ref WordHash h)
    {
        h.Put4(Enemy, unchecked((ulong)(((long)At.X << 20) ^ (At.Y & 0xFFFFF))),
            (ulong)Source | ((ulong)Class << 8) | ((ulong)Sources << 16) | (FiringAtUs ? 1UL << 24 : 0) | (Foreign ? 1UL << 25 : 0), Tick);
        h.Put4(KnownAt, ShareAt, Heard, 0x0C);
    }
}

/// <summary>A soldier's fight state (22.1, the parts INF-E1b-1 builds): posture, suppression, contacts, threat axis, reaction.</summary>
public sealed class SoldierFight
{
    internal readonly List<Contact> ContactList = [];
    internal readonly (uint Shooter, uint Tick)[] Shooters = new (uint, uint)[4]; // the last enemies who shot at or near him

    /// <summary>His posture now (while changing: the one he is leaving).</summary>
    public Posture Posture { get; internal set; }

    /// <summary>The posture he is changing to (his posture when not changing).</summary>
    public Posture PostureTo { get; internal set; }

    /// <summary>Ticks left of a posture change (no firing, no moving meanwhile).</summary>
    public int PostureLeft { get; internal set; }

    /// <summary>The tick he took his posture (it is held at least postureHoldTicks, 9.2).</summary>
    public uint PostureSince { get; internal set; }

    /// <summary>Ducked in cover (pinned hard): cover protects at its ducked value.</summary>
    public bool Ducked { get; internal set; }

    /// <summary>Suppression in hundredths (0 to 10 000).</summary>
    public int Suppression100 { get; internal set; }

    /// <summary>Suppression, 0 to 100 (brief 6.6).</summary>
    public int Suppression => Suppression100 / 100;

    /// <summary>The tick of his last suppression gain (decay waits holdTicks after it).</summary>
    public uint SuppressedAt { get; internal set; }

    /// <summary>What he knows of the enemy, at most contactsPerSoldier.</summary>
    public IReadOnlyList<Contact> Contacts => ContactList;

    /// <summary>His threat axis: one of the 32 directions (<see cref="World.Directions"/>), or -1 for none (4.5).</summary>
    public int Axis { get; internal set; } = -1;

    /// <summary>A second axis when he is caught between two fronts, else -1.</summary>
    public int Axis2 { get; internal set; } = -1;

    internal int AxisNext { get; set; } = -1;
    internal int AxisCount { get; set; }

    /// <summary>The tick his reaction to his first contact ends (0: none pending or done).</summary>
    public uint ReactAt { get; internal set; }

    /// <summary>In contact (section 3), as of his last think.</summary>
    public bool InContact { get; internal set; }

    /// <summary>Alert: not in contact, a contact known within alertRange.</summary>
    public bool Alert { get; internal set; }

    /// <summary>The last two ticks a shot was aimed at him or landed within 1.5 cells of him.</summary>
    public uint ShotNear1 { get; internal set; }

    /// <inheritdoc cref="ShotNear1"/>
    public uint ShotNear2 { get; internal set; }

    /// <summary>The distance of the last shot at him, percent of its shooter's range.</summary>
    public int ShotFromPercent { get; internal set; }

    /// <summary>His last think.</summary>
    public uint LastThink { get; internal set; }

    /// <summary>The last tick an enemy saw him.</summary>
    public uint SpottedAt { get; internal set; }

    /// <summary>Whom he last fired at, and when (a marksman's danger, 4.4).</summary>
    public uint LastTarget { get; internal set; }

    /// <inheritdoc cref="LastTarget"/>
    public uint LastTargetTick { get; internal set; }

    internal bool Urgent { get; set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put4((ulong)Posture | ((ulong)PostureTo << 8) | (Ducked ? 1UL << 16 : 0) | (InContact ? 1UL << 17 : 0) | (Alert ? 1UL << 18 : 0) |
            (Urgent ? 1UL << 19 : 0), unchecked((ulong)PostureLeft), PostureSince, unchecked((ulong)Suppression100));
        h.Put4(SuppressedAt, (ulong)(byte)(Axis + 1) | ((ulong)(byte)(Axis2 + 1) << 8) | ((ulong)(byte)(AxisNext + 1) << 16), unchecked((ulong)AxisCount), ReactAt);
        h.Put4(ShotNear1, ShotNear2, unchecked((ulong)ShotFromPercent), LastThink);
        h.Put4(SpottedAt, LastTarget, LastTargetTick, unchecked((ulong)ContactList.Count));
        foreach (var (s, t) in Shooters)
        {
            h.Put(s);
            h.Put(t);
        }
        foreach (var k in ContactList)
        {
            k.HashInto(ref h);
        }
    }
}

/// <summary>A squad's shared picture (22.2, the parts INF-E1b-1 builds): its contacts, threat axis and last think.</summary>
public sealed class SquadMind
{
    internal readonly List<Contact> PictureList = [];

    /// <summary>The squad picture: at most contactsPerSquad merged contacts (4.3).</summary>
    public IReadOnlyList<Contact> Picture => PictureList;

    /// <summary>The squad's threat axis from its centre, or -1 (4.5).</summary>
    public int Axis { get; internal set; } = -1;

    /// <summary>A second axis (two fronts), or -1.</summary>
    public int Axis2 { get; internal set; } = -1;

    internal int AxisNext { get; set; } = -1;
    internal int AxisCount { get; set; }

    /// <summary>Any able member in contact, as of the last squad think.</summary>
    public bool InContact { get; internal set; }

    /// <summary>The squad's last think (the hearing scan looks at shots since then).</summary>
    public uint LastThink { get; internal set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put4((ulong)(byte)(Axis + 1) | ((ulong)(byte)(Axis2 + 1) << 8) | ((ulong)(byte)(AxisNext + 1) << 16), unchecked((ulong)AxisCount),
            (InContact ? 1UL : 0) | ((ulong)PictureList.Count << 8), LastThink);
        foreach (var k in PictureList)
        {
            k.HashInto(ref h);
        }
    }
}

/// <summary>A short-lived danger on the ground (5.4): an impact's two rings, until a tick.</summary>
public readonly record struct DangerZone(Cell Centre, int InnerRaw, int InnerValue, int OuterRaw, int OuterValue, uint Until);

/// <summary>
/// The cover data per cell (5.1): for each of the 8 sides the best cover kind there (2 bits each), a crest flag per side, the
/// cell's forest density (its concealment). 4 bytes a cell. Built from the map when first needed and rebuilt where it changes.
/// Not game state: a function of the map and of the standing structures.
/// </summary>
internal sealed class CoverMap
{
    internal const int CrestShift = 16;
    internal const int DensityShift = 24;

    internal CoverMap(int w, int h)
    {
        (W, H) = (w, h);
        Data = new uint[w * h];
        Solid = new ulong[(w * h + 63) / 64];
        Tall = new ulong[(w * h + 63) / 64];
        Queued = new ulong[(w * h + 63) / 64];
    }

    internal int W { get; }
    internal int H { get; }
    internal uint[] Data { get; }
    internal ulong[] Solid { get; } // a standing structure's footprint (owned ones don't mark the grid)
    internal ulong[] Tall { get; } // a standing structure at least tallHalfMetres high: high cover
    internal ulong[] Queued { get; } // cells waiting in Queue
    internal List<int> Queue { get; } = [];
    internal int QueueAt { get; set; }
    internal List<(uint Id, int X0, int Y0, int X1, int Y1, bool Tall)> Stamped { get; set; } = [];
    internal long BuildTicksRaw { get; set; } // stopwatch ticks of the first build, set by tools (not by the engine)

    internal long Bytes => (long)Data.Length * 4 + (long)Solid.Length * 8 * 3;

    internal static bool Bit(ulong[] b, int i) => ((b[i >> 6] >> (i & 63)) & 1) != 0;

    internal static void SetBit(ulong[] b, int i, bool on)
    {
        if (on)
        {
            b[i >> 6] |= 1UL << (i & 63);
        }
        else
        {
            b[i >> 6] &= ~(1UL << (i & 63));
        }
    }

    /// <summary>The cover kind on side s (0 east, then clockwise in 45° steps: 1 south-east ... 7 north-east) of cell index i.</summary>
    internal CoverKind Side(int i, int s) => (CoverKind)((Data[i] >> (2 * s)) & 3);

    internal bool Crest(int i, int s) => ((Data[i] >> (CrestShift + s)) & 1) != 0;

    internal int Density(int i) => (int)((Data[i] >> DensityShift) & 3);
}
