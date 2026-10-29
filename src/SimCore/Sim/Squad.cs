using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// How a squad moves and stands (docs/design.md, "Infantry squads"). Values are stable (replays store them through
/// <see cref="CommandKind.SetSquadMode"/>'s <see cref="Command.Index"/>): new modes are appended.
/// </summary>
public enum SquadMode : byte
{
    /// <summary>A file along the path, at the slowest member's speed.</summary>
    March,

    /// <summary>A line facing the move, at 80% of it.</summary>
    Assault,

    /// <summary>
    /// Each member in the best cell for his role near the spot, facing a dragged direction (phase S2): it stays there until a new
    /// order and lays no mines. On the way there (and on any move) it walks as March does; the cells are picked on arrival.
    /// </summary>
    Hold,

    /// <summary>
    /// A trap round a kill zone (phase S3; docs/design.md, "Squads: Ambush"): every member on one side of the zone in the best cell
    /// for his role, the line's mines laid in it, everyone holding fire until it springs on its own (<see cref="Squad.AmbushTrigger"/>);
    /// then each fights from his spot and never chases. It stays in Ambush and can spring again.
    /// </summary>
    Ambush,
}

/// <summary>What a squad is doing, for the selection card. Values are stable; new states are appended.</summary>
public enum SquadState : byte
{
    /// <summary>No squad order: it keeps its shape where it stands (or its members carry out orders of their own).</summary>
    Idle,

    /// <summary>On a squad move (Move or Attack-move): walking its path in its mode's shape.</summary>
    Moving,

    /// <summary>Ambush: setting the trap (members walking to their positions, the line laying its mines), holding fire.</summary>
    Setting,

    /// <summary>Ambush: the trap is set (everyone in place, the mines laid), holding fire until it springs.</summary>
    Ready,

    /// <summary>Ambush: sprung (<see cref="Squad.AmbushTrigger"/>): everyone fights from his spot in the squad's stance.</summary>
    Sprung,
}

/// <summary>What sprang an ambush (<see cref="Squad.AmbushTrigger"/>, <see cref="SquadEventKind.Sprung"/>). Values are stable.</summary>
public enum AmbushTrigger : byte
{
    /// <summary>Nothing yet.</summary>
    None,

    /// <summary>An enemy vehicle reached the far end of the zone.</summary>
    FarEnd,

    /// <summary>The zone held enough enemies (3).</summary>
    Crowd,

    /// <summary>A mine (or any trap) in the zone went off.</summary>
    Mine,

    /// <summary>An enemy spotted a member.</summary>
    Spotted,

    /// <summary>A member was hurt.</summary>
    Hurt,

    /// <summary>The player ordered Fire (<see cref="CommandKind.SpringAmbush"/>), or an attack.</summary>
    Order,

    /// <summary>An air ambush (phase S4): an enemy aircraft came within reach of enough of its Iglas (2).</summary>
    Air,
}

/// <summary>What an ambush mine is (<see cref="AmbushMineTask"/>, <see cref="AmbushMine"/>).</summary>
public enum AmbushMineKind : byte
{
    /// <summary>An anti-tank mine: where vehicles drive, the far end first.</summary>
    AntiTank,

    /// <summary>An anti-personnel mine: where infantry would run for cover.</summary>
    AntiPersonnel,

    /// <summary>The engineer's booby trap, laid as the anti-personnel mines are.</summary>
    Trap,
}

/// <summary>Where an ambush mine task stands.</summary>
public enum AmbushTaskState : byte
{
    /// <summary>Not started.</summary>
    Pending,

    /// <summary>The soldier is on his way or laying it.</summary>
    Laying,

    /// <summary>Done: laid, or given up (no charge left, the cell out of reach).</summary>
    Done,
}

/// <summary>
/// One mine the ambush plans (<see cref="Squad.AmbushTasks"/>): who lays it, where, what, and how far he got. The client may mark the
/// planned cells; laid mines are in <see cref="World.SquadAmbushMines"/>.
/// </summary>
/// <param name="Unit">The member who lays it.</param>
/// <param name="Cell">Where.</param>
/// <param name="Kind">What.</param>
/// <param name="Ability">The kit's component index (his <see cref="OrderKind.UseAbility"/> order).</param>
/// <param name="State">How far it got.</param>
public readonly record struct AmbushMineTask(uint Unit, Cell Cell, AmbushMineKind Kind, int Ability, AmbushTaskState State);

/// <summary>A mine an ambushing squad laid that is still on the map (<see cref="World.SquadAmbushMines"/>).</summary>
/// <param name="Mine">The mine (a unit).</param>
/// <param name="Cell">Its cell.</param>
/// <param name="Kind">What it is.</param>
public readonly record struct AmbushMine(uint Mine, Cell Cell, AmbushMineKind Kind);

/// <summary>What happened to a squad in the last tick (<see cref="World.SquadEvents"/>).</summary>
public enum SquadEventKind : byte
{
    /// <summary>A new squad was formed (<see cref="SquadEvent.Unit"/>: its leader).</summary>
    Formed,

    /// <summary>Another squad (<see cref="SquadEvent.Other"/>, gone now) merged into this one.</summary>
    Merged,

    /// <summary>A loose soldier (<see cref="SquadEvent.Unit"/>) joined.</summary>
    Joined,

    /// <summary>A member (<see cref="SquadEvent.Unit"/>) was detached: loose now, with the squad's stance.</summary>
    Detached,

    /// <summary>A member (<see cref="SquadEvent.Unit"/>) died or left the map: the squad shrank.</summary>
    Lost,

    /// <summary>The leader changed (<see cref="SquadEvent.Unit"/>: the new one; <see cref="SquadEvent.Other"/>: the old).</summary>
    Leader,

    /// <summary>The squad is gone: disbanded on an order, or down to one member (everyone loose, in place).</summary>
    Disbanded,

    /// <summary>A Form Squad that would merge squads past the most a squad holds was refused (<see cref="SquadEvent.Other"/>: the total).</summary>
    MergeRefused,

    /// <summary>The squad arrived at the end of its path: its order is done.</summary>
    Arrived,

    /// <summary>A holding squad picked its members' cells (<see cref="SquadEvent.Other"/>: how many candidate cells it scored).</summary>
    Placed,

    /// <summary>An ambush order was taken: the squad sets the trap (<see cref="SquadEvent.Unit"/>: its leader).</summary>
    Setting,

    /// <summary>The trap is set (or set again after it sprang).</summary>
    Ready,

    /// <summary>The ambush sprang (<see cref="SquadEvent.Other"/>: the <see cref="AmbushTrigger"/>).</summary>
    Sprung,

    /// <summary>A member (<see cref="SquadEvent.Unit"/>) laid a mine (<see cref="SquadEvent.Other"/>: the mine's unit ID).</summary>
    MineLaid,

    /// <summary>
    /// An air ambush's Iglas fired together (phase S4): <see cref="SquadEvent.Unit"/> the aircraft, <see cref="SquadEvent.Other"/> how
    /// many missiles left in the tick.
    /// </summary>
    Volley,
}

/// <summary>
/// A member's Hold position (<see cref="Squad.HoldPicks"/>): the cell he holds, or the garrisonable building he holds from inside
/// (then <see cref="Cell"/> is its door nearest the cell scored for it: where he stands while he can't get in).
/// </summary>
/// <param name="Unit">The member.</param>
/// <param name="Cell">His cell (his leash point).</param>
/// <param name="Building">The building he garrisons as part of Hold, or 0.</param>
public readonly record struct HoldPick(uint Unit, Cell Cell, uint Building);

/// <summary>
/// One candidate's score for a member in Hold's position picking (<see cref="World.SquadHoldScores"/>, for the client's debug view):
/// each factor 0 to 100 and the weighted total; <see cref="Allowed"/> false when one of the role's hard limits rules it out.
/// </summary>
/// <param name="Cell">The cell (a building's: the cell its score is taken from, next to it on the threat's side).</param>
/// <param name="Building">The garrisonable building this candidate is, or 0 for a cell.</param>
/// <param name="Score">The weighted total.</param>
/// <param name="Cover">Cover from the threat.</param>
/// <param name="Trees">Under canopy that hides from the air.</param>
/// <param name="Sight">The sight lanes' reach.</param>
/// <param name="Open">Open ground and road in the lanes.</param>
/// <param name="Spacing">Distance from the teammates placed before him.</param>
/// <param name="Safety">Depth back from the threat side.</param>
/// <param name="Range">The threat within weapon reach.</param>
/// <param name="Near">Nearness to the spot.</param>
/// <param name="Allowed">Whether the role's hard limits (back, apart) allow it.</param>
public readonly record struct HoldCellScore(
    Cell Cell, uint Building, int Score, int Cover, int Trees, int Sight, int Open, int Spacing, int Safety, int Range, int Near, bool Allowed);

/// <summary>One squad event of the last tick.</summary>
/// <param name="Squad">The squad.</param>
/// <param name="Owner">Its player.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Unit">The soldier concerned, or 0.</param>
/// <param name="Other">The other squad or soldier, or a number (see <see cref="SquadEventKind"/>), or 0.</param>
public readonly record struct SquadEvent(uint Squad, byte Owner, SquadEventKind Kind, uint Unit, uint Other);

/// <summary>
/// An infantry squad (docs/design.md, "Infantry squads"): 2 to 10 soldiers of one player ordered as one. The squad walks one
/// path; its shape is built round a point on it (the anchor: the leader's slot), and each member follows his slot. Renderers
/// read these properties; only the sim writes them. Hashed by the world.
/// </summary>
public sealed class Squad
{
    internal Squad(uint id, byte owner) => (Id, Owner) = (id, owner);

    /// <summary>The squad's ID: assigned in increasing order, never reused.</summary>
    public uint Id { get; }

    /// <summary>The player.</summary>
    public byte Owner { get; }

    internal List<uint> MemberList { get; } = []; // sorted by ID, ascending

    /// <summary>The members' IDs, ascending.</summary>
    public IReadOnlyList<uint> Members => MemberList;

    /// <summary>
    /// The leader: the lowest-ID member whose role leads (not a medic, sniper or mortar; none: the lowest ID). Not a special unit,
    /// only the one whose slot is the anchor at rest; on the move the slots go by where the members stood when the order came, and
    /// the anchor's slot is the foremost member's who may lead (docs/design.md, "Squads: slots by place").
    /// </summary>
    public uint Leader { get; internal set; }

    /// <summary>The mode.</summary>
    public SquadMode Mode { get; internal set; }

    /// <summary>The stance the whole squad holds (each member's <see cref="Unit.Stance"/> too).</summary>
    public Stance Stance { get; internal set; }

    /// <summary>The order the squad is on: <see cref="OrderKind.Move"/>, <see cref="OrderKind.AttackMove"/> or Idle.</summary>
    public OrderKind Order { get; internal set; }

    /// <summary>Moving (on a squad order), idle, or in Ambush setting, ready or sprung.</summary>
    public SquadState State => Order != OrderKind.Idle ? SquadState.Moving : Mode == SquadMode.Ambush ? AmbushPhase : SquadState.Idle;

    /// <summary>The goal cell of the squad's order (meaningful while moving).</summary>
    public Cell Goal { get; internal set; }

    /// <summary>The point the shape is built round: the leader's slot at rest; on the move, the head's (see <see cref="Leader"/>).</summary>
    public Vec Anchor { get; internal set; }

    /// <summary>The way the shape faces (unit length): the move's direction, the last one when idle.</summary>
    public Vec Facing { get; internal set; }

    internal Cell[] PathCells { get; set; } = [];

    /// <summary>The squad's path (refined a few legs at a time as it walks): the cells the anchor walks through.</summary>
    public IReadOnlyList<Cell> Path => PathCells;

    /// <summary>The index in <see cref="Path"/> of the next cell the anchor walks to.</summary>
    public int PathIdx { get; internal set; }

    // The abstract route still to refine (cell indices) and how far it is refined.
    internal int[] Route { get; set; } = [];

    internal int RouteIdx { get; set; }

    // Whether its path waits to be planned (this tick's squad pass plans it).
    internal bool PathPending { get; set; }

    // The anchor's walked points, oldest first, half a cell apart: the March file lies along them.
    internal List<Vec> Trail { get; } = [];

    // How many ticks in a row it waited for members that fell behind (capped at the rules' regroupTicks).
    internal int Waited { get; set; }

    /// <summary>
    /// Whether its members are on orders of their own (an attack, a building to enter, Guard, Scatter, Stop): it rebuilds its
    /// shape where its leader stands once every member on the map is idle again.
    /// </summary>
    public bool Loose { get; internal set; }

    // The slots (derived from the members, their roles, the leader and the mode; rebuilt when one changes; not hashed).
    // Per member, in MemberList order: how far behind the anchor (negative: ahead), how far to the right, and whether it
    // lies along the trail (March) or fixed to the facing (Assault).
    internal Fixed[] SlotDepth { get; set; } = [];

    internal Fixed[] SlotLateral { get; set; } = [];

    internal bool[] SlotTrail { get; set; } = [];

    internal SquadRole[] Roles { get; set; } = [];

    // Where each member stood when the squad took its order (or a new mode on the move), from the anchor along the facing and
    // across it to the right, in half cells to the nearest; by unit ID, ascending. Empty at rest: the slots then go by role. On the
    // move the slots go by it (the members ahead take the forward slots: docs/design.md, "Squads: slots by place"). Hashed.
    internal List<(uint Unit, int Along, int Across)> Places { get; } = [];

    internal Fixed MaxTrailDepth { get; set; }

    // ---- Hold (phase S2) ----

    /// <summary>The spot it holds round (meaningful in <see cref="SquadMode.Hold"/>).</summary>
    public Cell HoldSpot { get; internal set; }

    /// <summary>The Hold facing (unit length): toward the threat. The client draws it as a short arrow.</summary>
    public Vec HoldFacing { get; internal set; }

    /// <summary>The point the squad holds against: the dragged facing's end, or the rules' threat distance ahead of the spot.</summary>
    public Vec HoldThreat { get; internal set; }

    // Whether the Hold order gave the facing (else it is taken on arrival from the way the squad walked).
    internal bool HoldFaced { get; set; }

    /// <summary>Whether its members' Hold cells are picked (in Hold, once it has arrived and scored them).</summary>
    public bool HoldPlaced { get; internal set; }

    // Whether its cells wait to be picked (a squad pass picks them: a few squads a tick).
    internal bool HoldPending { get; set; }

    internal List<HoldPick> PickList { get; } = []; // sorted by unit ID, ascending

    /// <summary>The members' Hold positions (by unit ID, ascending): empty until placed.</summary>
    public IReadOnlyList<HoldPick> HoldPicks => PickList;

    // ---- Ambush (phase S3) ----

    /// <summary>The kill zone's start (where the enemy comes in; meaningful in <see cref="SquadMode.Ambush"/>).</summary>
    public Cell AmbushStart { get; internal set; }

    /// <summary>The kill zone's far end (where the column is stopped).</summary>
    public Cell AmbushEnd { get; internal set; }

    /// <summary>
    /// The side of the zone the squad sets the trap on: 1 to the right of the way from <see cref="AmbushStart"/> to <see cref="AmbushEnd"/>
    /// (x right, y down: the side a clockwise quarter turn of that way points to), -1 to the left, 0 not picked yet.
    /// </summary>
    public int AmbushSide { get; internal set; }

    /// <summary>The side's spot: out from the zone's middle on that side (where a squad far away marches first).</summary>
    public Cell AmbushSpot { get; internal set; }

    // Setting, Ready or Sprung in Ambush (Idle otherwise).
    internal SquadState AmbushPhase { get; set; }

    /// <summary>What sprang the ambush last (None until it springs).</summary>
    public AmbushTrigger AmbushTrigger { get; internal set; }

    // A trigger noted since the last squad pass (a member hurt, a mine off, the Fire order): the next pass springs it.
    internal AmbushTrigger SpringPending { get; set; }

    // The tick the ambush order was taken (Setting gives up waiting the rules' setTicks after it).
    internal uint AmbushSince { get; set; }

    // Sprung: ticks in a row without a fight (Ready again at the rules' rearmTicks).
    internal int AmbushCalm { get; set; }

    // The side picking's first half (one side a squad pass): the right side scored, its worth and its picks (by unit ID).
    internal bool AmbushRightDone { get; set; }

    internal long AmbushRightScore { get; set; }

    internal List<HoldPick> AltPicks { get; } = [];

    internal List<AmbushMineTask> TaskList { get; } = []; // by member ID, then laying order

    /// <summary>The mines the ambush plans, by member ID then laying order (anti-tank first, far end first).</summary>
    public IReadOnlyList<AmbushMineTask> AmbushTasks => TaskList;

    internal List<uint> MineList { get; } = []; // laying order

    /// <summary>The IDs of the mines the squad laid in this ambush, in laying order (gone ones too: see <see cref="World.SquadAmbushMines"/>).</summary>
    public IReadOnlyList<uint> AmbushMines => MineList;

    // ---- Air ambush (phase S4) ----

    /// <summary>
    /// Whether the ambush is an air ambush (phase S4; docs/design.md, "Squads: Air ambush"): the squad was mostly Iglas when it took
    /// the order. <see cref="AmbushStart"/> and <see cref="AmbushEnd"/> are then the air corridor (where aircraft come from, where they
    /// fly on to); <see cref="AmbushSide"/> is 0 and no mines are planned.
    /// </summary>
    public bool AmbushAir { get; internal set; }

    /// <summary>A sprung air ambush's target: the aircraft its Iglas lock on and fire at together (0: none in reach).</summary>
    public uint VolleyTarget { get; internal set; }

    // The tick the first of its Iglas was locked and loaded on the volley target, waiting for the others (0: none waiting).
    internal uint VolleySince { get; set; }
}
