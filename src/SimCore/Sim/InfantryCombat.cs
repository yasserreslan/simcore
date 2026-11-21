using SimCore.FixedPoint;
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

/// <summary>How a soldier moves (the battle rules' 7.2), set by the movement each tick he moves.</summary>
public enum Gait : byte
{
    /// <summary>Not moving.</summary>
    None,
    /// <summary>Walking toward his facing, firing (in contact).</summary>
    Walk,
    /// <summary>Side-stepping across his facing, firing.</summary>
    Strafe,
    /// <summary>Backing away from his facing, firing.</summary>
    Backpedal,
    /// <summary>A dash, crouched, facing the move (a bound, close cover).</summary>
    CrouchRun,
    /// <summary>Running, facing the move (out of contact or out of reach).</summary>
    Run,
    /// <summary>Sprinting (out of contact, 10 s at most).</summary>
    Sprint,
    /// <summary>Crawling (prone).</summary>
    Crawl,
}

/// <summary>A grenade thrown (14.1): where it lands, when it lands and goes off, who threw it.</summary>
public readonly record struct Grenade(Vec At, uint Thrower, byte Owner, uint LandsAt, uint GoesOff);

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

    // ---- INF-E1b-2: the soldier's think ----

    /// <summary>His body facing in contact (7.1): one of the 32 directions, or -1 (he faces where he moves).</summary>
    public int Face { get; internal set; } = -1;

    /// <summary>His move direction as a <see cref="FixedPoint.Facing"/> (the client's walk clip: strafe, backpedal).</summary>
    public int MoveFacing { get; internal set; }

    /// <summary>His gait (7.2) as of his last step (None when he stood).</summary>
    public Gait Gait { get; internal set; }

    /// <summary>Ticks left of a turn over 90 degrees (no firing).</summary>
    public int TurnLeft { get; internal set; }

    /// <summary>Beyond the reach of every dangerous contact he knows (7.3's first exception), as of his last think.</summary>
    public bool OutOfReach { get; internal set; }

    /// <summary>Sprinting since this tick (0: not); no sprint again before SprintRest.</summary>
    public uint SprintFrom { get; internal set; }

    /// <inheritdoc cref="SprintFrom"/>
    public uint SprintRest { get; internal set; }

    /// <summary>His chosen position (6): the cell, claimed while HasSpot.</summary>
    public Cell Spot { get; internal set; }

    /// <summary>He holds a chosen position (claimed: no squadmate takes it; the squad's slot doesn't pull him).</summary>
    public bool HasSpot { get; internal set; }

    /// <summary>On his way to Spot.</summary>
    public bool SpotMoving { get; internal set; }

    /// <summary>The tick he got to his spot (or chose to stay).</summary>
    public uint SpotSince { get; internal set; }

    /// <summary>His last position search, the exposure where he stood then (hundredths), and a cell found unreachable until a tick.</summary>
    public uint LastSearch { get; internal set; }

    /// <inheritdoc cref="LastSearch"/>
    public long SearchExposure { get; internal set; }

    /// <inheritdoc cref="LastSearch"/>
    public Cell Unreachable { get; internal set; }

    /// <inheritdoc cref="LastSearch"/>
    public uint UnreachableUntil { get; internal set; }

    internal bool SearchWaits { get; set; }

    /// <summary>No shot before this tick (a burst's wait, a new target's 4 to 8 ticks: 15.3, 15.5).</summary>
    public uint FireAt { get; internal set; }

    /// <summary>Shots left in his burst.</summary>
    public int BurstLeft { get; internal set; }

    /// <summary>The tick he last had no target (a tactical reload, 15.6).</summary>
    public uint NoTargetSince { get; internal set; }

    /// <summary>Grenades carried (14.1).</summary>
    public int Grenades { get; internal set; }

    /// <summary>A throw in progress: it leaves his hand at this tick (0: none), at this point.</summary>
    public uint ThrowAt { get; internal set; }

    /// <inheritdoc cref="ThrowAt"/>
    public Vec ThrowTo { get; internal set; }

    /// <summary>His last throw (a loose soldier's own cooldown; a squad's is the squad's).</summary>
    public uint LastThrow { get; internal set; }

    /// <summary>A grenade he saw land: he reacts at ReflexAt (0: none); Reflex while he dashes from it (7.3's reflex).</summary>
    public uint ReflexAt { get; internal set; }

    /// <inheritdoc cref="ReflexAt"/>
    public Vec ReflexFrom { get; internal set; }

    /// <inheritdoc cref="ReflexAt"/>
    public bool Reflex { get; internal set; }

    /// <summary>The friendly vehicle he moves with (7.6), 0 for none, and his slot behind it.</summary>
    public uint Vehicle { get; internal set; }

    /// <inheritdoc cref="Vehicle"/>
    public int VehicleSlot { get; internal set; }

    /// <summary>The posture table's row 2 (a stance or order that forces one): none today, a hook (null).</summary>
    public Posture? Forced { get; internal set; }

    internal bool Urgent { get; set; }

    // INF-E1b-3, the LMG's deploy (16.2) and the squad's suppressive fire (11.6).
    internal bool Lmg { get; set; } // his main arm is an LMG (from the content: not state)

    /// <summary>The LMG gunner is set up (16.2): on his bipod over low cover or prone, his arc round <see cref="DeployDir"/>.</summary>
    public bool Deployed { get; internal set; }

    /// <summary>Ticks left setting up or turning his gun (no fire meanwhile).</summary>
    public int DeployLeft { get; internal set; }

    /// <summary>The direction (of the 32) his deployed arc is centred on.</summary>
    public int DeployDir { get; internal set; } = -1;

    internal uint StillSince { get; set; }
    internal uint ArcOutSince { get; set; }

    /// <summary>He fires at a place, not a man (11.6): <see cref="SuppressAt"/>, the squad's order.</summary>
    public bool Suppressing { get; internal set; }

    /// <inheritdoc cref="Suppressing"/>
    public Cell SuppressAt { get; internal set; }

    internal void HashTactics(ref WordHash h) =>
        h.Put4((Deployed ? 1UL : 0) | (Suppressing ? 2UL : 0) | ((ulong)(byte)(DeployDir + 1) << 8), unchecked((ulong)DeployLeft), StillSince | ((ulong)ArcOutSince << 32),
            unchecked((ulong)(((long)SuppressAt.X << 20) ^ (SuppressAt.Y & 0xFFFFF))));

    internal void HashThink(ref WordHash h)
    {
        h.Put4((ulong)(byte)(Face + 1) | ((ulong)(byte)MoveFacing << 8) | ((ulong)Gait << 16) | (OutOfReach ? 1UL << 24 : 0) | (HasSpot ? 1UL << 25 : 0) |
            (SpotMoving ? 1UL << 26 : 0) | (SearchWaits ? 1UL << 27 : 0) | (Reflex ? 1UL << 28 : 0) | ((ulong)(Forced is { } fp ? (byte)fp + 1 : 0) << 32),
            unchecked((ulong)TurnLeft), SprintFrom, SprintRest);
        h.Put4(unchecked((ulong)(((long)Spot.X << 20) ^ (Spot.Y & 0xFFFFF))), SpotSince, LastSearch, unchecked((ulong)SearchExposure));
        h.Put4(unchecked((ulong)(((long)Unreachable.X << 20) ^ (Unreachable.Y & 0xFFFFF))), UnreachableUntil, FireAt, unchecked((ulong)BurstLeft));
        h.Put4(NoTargetSince, unchecked((ulong)Grenades), ThrowAt, unchecked((ulong)ThrowTo.X.Raw));
        h.Put4(unchecked((ulong)ThrowTo.Y.Raw), ReflexAt, unchecked((ulong)ReflexFrom.X.Raw), unchecked((ulong)ReflexFrom.Y.Raw));
        h.Put4(Vehicle, unchecked((ulong)VehicleSlot), 0x1CB2, LastThrow);
    }

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
        HashThink(ref h);
        HashTactics(ref h);
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

    /// <summary>The squad's focus target (15.4), 0 for none, and the last tick a member saw it.</summary>
    public uint Focus { get; internal set; }

    /// <inheritdoc cref="Focus"/>
    public uint FocusSeen { get; internal set; }

    // The last grenades thrown by members (14.1's squad cooldown): target, tick; at most 4, the oldest replaced.
    internal readonly (uint Target, uint Tick)[] Thrown = new (uint, uint)[4];

    // ---- INF-E1b-3, the squad think (10, 11, 15.4, 15.7) ----

    /// <summary>The squad's plan in a fight (10.3), <see cref="SquadChoice.None"/> out of contact.</summary>
    public SquadChoice Choice { get; internal set; }

    internal uint ChoiceTick { get; set; }
    internal int ChoiceR { get; set; } // the R threshold that put it in its choice (10.3's hysteresis), 0 for none

    /// <summary>The force ratio R in percent (10.2), 0 before the first think in contact.</summary>
    public int Ratio { get; internal set; }

    /// <summary>A contact known within alertRange of the squad's centre, none of its members in contact yet (section 3).</summary>
    public bool Alert { get; internal set; }

    internal uint HotTick { get; set; } // the last think alert or in contact

    /// <summary>The main enemy (10.1): a contact's enemy ID (0 for a heard area), and the enemy group's mean cell and size.</summary>
    public uint Main { get; internal set; }

    /// <inheritdoc cref="Main"/>
    public Cell GroupAt { get; internal set; }

    /// <inheritdoc cref="Main"/>
    public int GroupCount { get; internal set; }

    internal int GroupPinned { get; set; }
    internal uint GroupSeen { get; set; } // the newest contact tick of the group (11.6's "no sign of the enemy")

    /// <summary>The squad stands in its contact line (11.1), built on <see cref="LineAxis"/>.</summary>
    public bool HasLine { get; internal set; }

    /// <inheritdoc cref="HasLine"/>
    public int LineAxis { get; internal set; } = -1;

    internal Cell LineCentre { get; set; }
    internal readonly List<LinePlace> LineList = []; // by member ID

    /// <summary>Each member's place in the line: slot, goal, element, bounding (11.1, 11.2).</summary>
    public IReadOnlyList<LinePlace> Line => LineList;

    /// <summary>The element bounding now (11.3): 0 none, 1 A, 2 B.</summary>
    public byte Moving { get; internal set; }

    internal byte Last { get; set; }
    internal uint BoundTick { get; set; }
    internal uint SettledTick { get; set; }

    /// <summary>The whole line walks forward firing (11.4).</summary>
    public bool Marching { get; internal set; }

    internal uint MarchSince { get; set; }

    /// <summary>A member fired or was fired at (15.7): set by the first shot by or at a member, cleared when the squad is calm.</summary>
    public bool OpenedFire { get; internal set; }

    internal bool Urgent { get; set; }
    internal bool OrderNew { get; set; }
    internal int AbleCount { get; set; }

    // ---- INF-E1b-3 part 2: flank (11.5), assault (11.7) ----

    /// <summary>The flank's stage (11.5): 0 none, 1 base of fire suppressing before the go, 2 flankers on the way, 3 there.</summary>
    public byte FlankStage { get; internal set; }

    internal uint FipSince { get; set; } // the tick the squad went into Fight in place (11.5's 120 ticks), 0 out of it
    internal int HitsNow { get; set; } // 11.5's two hit counters (hits on enemy soldiers), flankWindowTicks each
    internal int HitsPrev { get; set; }
    internal uint HitWindow { get; set; }
    internal Cell FlankTo { get; set; } // the chosen flank cell
    internal Cell FlankFrom { get; set; } // the enemy group's cell when the plan was made
    internal uint FlankTick { get; set; } // the plan's tick (the go is flankSuppressTicks later)
    internal uint FlankNoUntil { get; set; } // no new plan before (an abort's flankRetryTicks, a rejected plan's flankRejectTicks)

    /// <summary>The flankers (11.5) or the assault's runners (11.7) by ID, 0 for none.</summary>
    internal readonly uint[] Movers = new uint[8];

    internal int MoverCount { get; set; }
    internal uint AssaultTick { get; set; } // the assault's start, 0 for none
    internal uint RushAt { get; set; } // the rush waits for the grenades until
    internal uint AssaultNoUntil { get; set; }

    internal bool IsMover(uint id)
    {
        for (var i = 0; i < MoverCount; i++)
        {
            if (Movers[i] == id)
            {
                return true;
            }
        }
        return false;
    }

    internal void HashTactics(ref WordHash h)
    {
        h.Put4((ulong)FlankStage | ((ulong)(uint)MoverCount << 8) | ((ulong)FipSince << 32), (ulong)(uint)HitsNow | ((ulong)(uint)HitsPrev << 32),
            HitWindow | ((ulong)FlankTick << 32), FlankNoUntil | ((ulong)AssaultTick << 32));
        h.Put4(unchecked((ulong)(((long)FlankTo.X << 20) ^ (FlankTo.Y & 0xFFFFF))), unchecked((ulong)(((long)FlankFrom.X << 20) ^ (FlankFrom.Y & 0xFFFFF))),
            RushAt | ((ulong)AssaultNoUntil << 32), 0x1CB4);
        for (var i = 0; i < MoverCount; i++)
        {
            h.Put(Movers[i]);
        }
        h.Put4((ulong)Choice | ((ulong)Moving << 8) | ((ulong)Last << 16) | (Alert ? 1UL << 24 : 0) | (HasLine ? 1UL << 25 : 0) | (Marching ? 1UL << 26 : 0) |
            (OpenedFire ? 1UL << 27 : 0) | (Urgent ? 1UL << 28 : 0) | (OrderNew ? 1UL << 29 : 0) | ((ulong)(byte)(LineAxis + 1) << 32), ChoiceTick | ((ulong)HotTick << 32),
            (ulong)(uint)ChoiceR | ((ulong)(uint)Ratio << 32), Main);
        h.Put4(unchecked((ulong)(((long)GroupAt.X << 20) ^ (GroupAt.Y & 0xFFFFF))), (ulong)(uint)GroupCount | ((ulong)(uint)GroupPinned << 32), GroupSeen | ((ulong)BoundTick << 32),
            SettledTick | ((ulong)MarchSince << 32));
        h.Put4(unchecked((ulong)(((long)LineCentre.X << 20) ^ (LineCentre.Y & 0xFFFFF))), unchecked((ulong)AbleCount), (ulong)LineList.Count, 0x1CB3);
        foreach (var p in LineList)
        {
            h.Put4(p.Unit, unchecked((ulong)(((long)p.Slot.X << 20) ^ (p.Slot.Y & 0xFFFFF))), unchecked((ulong)(((long)p.Goal.X << 20) ^ (p.Goal.Y & 0xFFFFF))),
                (ulong)p.Element | (p.Bounding ? 1UL << 8 : 0));
        }
    }

    internal void HashInto(ref WordHash h)
    {
        HashTactics(ref h);
        h.Put4(Focus, FocusSeen, 0x1CB2, 0);
        foreach (var (t, k) in Thrown)
        {
            h.Put(t);
            h.Put(k);
        }
        h.Put4((ulong)(byte)(Axis + 1) | ((ulong)(byte)(Axis2 + 1) << 8) | ((ulong)(byte)(AxisNext + 1) << 16), unchecked((ulong)AxisCount),
            (InContact ? 1UL : 0) | ((ulong)PictureList.Count << 8), LastThink);
        foreach (var k in PictureList)
        {
            k.HashInto(ref h);
        }
    }
}

/// <summary>The squad's plan in a fight (10.3).</summary>
public enum SquadChoice : byte
{
    /// <summary>Calm or alert: no fight yet.</summary>
    None,
    /// <summary>Fight from its line.</summary>
    FightInPlace,
    /// <summary>Close on the enemy, by bounds or marching fire.</summary>
    Advance,
    /// <summary>Part of the squad works round (11.5).</summary>
    Flank,
    /// <summary>The last rush (11.7).</summary>
    Assault,
    /// <summary>Give ground (12, part 2).</summary>
    Withdraw,
}

/// <summary>A member's place in the contact line (11.1): his slot (his frontage box's centre), the cell he walks to while he has
/// no spot of his own, his element (11.2: 0 none, 1 A, 2 B) and whether he is on a bound (11.3) or marching (11.4).</summary>
public readonly record struct LinePlace(uint Unit, Cell Slot, Cell Goal, byte Element, bool Bounding);

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
