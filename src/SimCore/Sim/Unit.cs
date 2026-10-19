using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>How routing moves a unit.</summary>
internal enum RouteMode : byte
{
    /// <summary>Not routed.</summary>
    None,

    /// <summary>Along its own route (planned, then refined a few legs at a time).</summary>
    Direct,

    /// <summary>Down its group's flow field, until near its formation slot.</summary>
    Field,
}

/// <summary>What a unit is currently told to do.</summary>
public enum OrderKind : byte
{
    /// <summary>No order; the unit defends itself.</summary>
    Idle,

    /// <summary>Go to <see cref="Order.Cell"/>.</summary>
    Move,

    /// <summary>Chase and shoot <see cref="Order.Target"/>; a launched drone flies into it.</summary>
    Attack,

    /// <summary>Walk to vehicle <see cref="Order.Target"/> and take its driver seat, or to a door of building <see cref="Order.Target"/> and garrison it.</summary>
    Enter,

    /// <summary>
    /// Use ability <see cref="Order.Ability"/> on unit <see cref="Order.Target"/>,
    /// or on <see cref="Order.Cell"/> when the target is 0, after getting in range.
    /// </summary>
    UseAbility,

    /// <summary>Fire at the centre of <see cref="Order.Cell"/> with the first weapon that can hit the ground, after getting in range.</summary>
    AttackGround,

    /// <summary>Drive a full cargo truck into the radius of depot <see cref="Order.Target"/> and unload there.</summary>
    Unload,

    /// <summary>Walk to construction site <see cref="Order.Target"/> and build it.</summary>
    Build,

    /// <summary>
    /// Move to <see cref="Order.Cell"/>, stopping to shoot spotted enemies that come in weapon reach
    /// (turrets fire on the move), then carry on; it retaliates on the way. Ends on arrival.
    /// </summary>
    AttackMove,

    /// <summary>
    /// Guard <see cref="Order.Cell"/>, or unit <see cref="Order.Target"/> (the circle moves with it;
    /// the cell follows it): engage spotted enemies within the inner guard circle, chase on the leash,
    /// walk back. It lasts until another order.
    /// </summary>
    Guard,

    /// <summary>
    /// Repair <see cref="Order.Target"/>: a repairable structure (standing or rubble) up to full HP,
    /// or a runway segment: its airfield's craters, nearest first, then its worn patches.
    /// </summary>
    Repair,
}

/// <summary>
/// How a unit engages without orders (orders are always obeyed). Values are stable (replays store
/// them): new stances are appended.
/// </summary>
public enum Stance : byte
{
    /// <summary>
    /// The default (Zero Hour's player units): engages spotted enemies that come in reach (air
    /// defence: air targets), and when hurt it and its idle friends nearby go after the attacker on
    /// a leash, then walk back.
    /// </summary>
    Guard,

    /// <summary>Never fires on its own and tracks nothing; fires only on attack or attack-ground orders.</summary>
    HoldFire,

    /// <summary>Fires on its own only at the enemy that last hurt it (<see cref="Unit.LastHitBy"/>), while in reach.</summary>
    ReturnFire,

    /// <summary>Engages what comes in reach from where it stands and never moves on its own (the stance once called Guard).</summary>
    HoldGround,

    /// <summary>Like Guard, but it also goes after spotted enemies out to its vision times the aggressive percent, on the leash.</summary>
    Aggressive,
}

/// <summary>What a unit does on its own, without orders (the unit AI): nothing special, a chase, or the walk back.</summary>
public enum AiState : byte
{
    /// <summary>The stance rules alone: it fires at what is in reach.</summary>
    None,

    /// <summary>Going after <see cref="Unit.AiTarget"/> (retaliation, Aggressive or a guard): on the leash from <see cref="Unit.GuardPoint"/>, until <see cref="Unit.GiveUpTick"/>.</summary>
    Engage,

    /// <summary>Walking back to its guard point after a chase, engaging what comes within the inner guard circle.</summary>
    Return,
}

/// <summary>Whether a structure is finished or still a construction site.</summary>
public enum ConstructionState : byte
{
    /// <summary>Finished (and every structure a scenario places).</summary>
    Complete,

    /// <summary>A site workers are building: not functional yet.</summary>
    Building,
}

/// <summary>Where a deploying unit (a rocket truck) is in setting up or packing up.</summary>
public enum DeployState : byte
{
    /// <summary>Packed up: it can move, not fire.</summary>
    Stowed,

    /// <summary>Setting up to fire.</summary>
    Deploying,

    /// <summary>Set up: it can fire, not move.</summary>
    Deployed,

    /// <summary>Packing up to move.</summary>
    Stowing,
}

/// <summary>A unit's current order.</summary>
/// <param name="Kind">The kind of order.</param>
/// <param name="Cell">The destination, an ability's or a ground attack's target cell; a drone's last known target cell.</param>
/// <param name="Target">The target unit's ID, or 0.</param>
/// <param name="Ability">For <see cref="OrderKind.UseAbility"/>, the ability's component index in the content.</param>
public readonly record struct Order(OrderKind Kind, Cell Cell, uint Target, int Ability);

/// <summary>
/// One entity on the map: a soldier, vehicle, drone, trap or structure.
/// Renderers read these properties; only the sim writes them.
/// </summary>
public sealed partial class Unit
{
    private readonly int[] _charges;

    internal Unit(uint id, UnitDef def, Vec pos, int facing)
    {
        Id = id;
        Def = def;
        Pos = pos;
        Facing = facing;
        TurretFacing = facing;
        Heading = FixedPoint.Facing.Dir(facing);
        HP = def.MaxHp;
        Owner = def.Player;
        Lifetime = def.Lifetime;
        WeaponMode = def.Archetype.WeaponMode;
        Stance = (Stance)def.Archetype.Stance;
        Stock = def.Supply?.Stock ?? 0;
        Cargo = def.Cargo?.Amount ?? 0; // cargo trucks start full
        _charges = [.. def.StockMax];
    }

    /// <summary>The unit's ID. IDs are assigned in increasing order and never reused.</summary>
    public uint Id { get; }

    /// <summary>The compiled definition: archetype, loadout and stats.</summary>
    public UnitDef Def { get; }

    /// <summary>
    /// The owning player, starting at 1. A garrisonable building is neutral (0) while empty and
    /// belongs to the player whose soldiers are inside.
    /// </summary>
    public byte Owner { get; internal set; }

    /// <summary>
    /// The building this soldier is garrisoning, or 0. An occupant stands at the building's
    /// position, can't be seen or hit directly (shots hit the building), fires from inside with
    /// <see cref="RangeBonus"/> and sees with <see cref="VisionBonus"/>, and can't move until it exits.
    /// </summary>
    public uint Garrison { get; internal set; }

    /// <summary>Range added to every weapon while garrisoned (from the building's garrison spec), otherwise 0.</summary>
    public Fixed RangeBonus { get; internal set; }

    /// <summary>Vision added while garrisoned, otherwise 0.</summary>
    public Fixed VisionBonus { get; internal set; }

    /// <summary>
    /// While garrisoned: the building's footprint in cells (width, height), centred on <see cref="Pos"/>.
    /// Range and vision count from its edge nearest the target (the soldier fires from a window).
    /// </summary>
    public (int W, int H) Cover { get; internal set; }

    /// <summary>For a garrisonable building: its damage stage, 0 (intact) up to the number of stages in its spec, from its HP.</summary>
    public int Stage { get; internal set; }

    /// <summary>For a garrisonable building: how many soldiers are inside (as of the end of the last tick).</summary>
    public int Occupants { get; internal set; }

    /// <summary>The world position. A driver keeps the spot where it climbed in until it gets out.</summary>
    public Vec Pos { get; internal set; }

    /// <summary>
    /// A structure placed a quarter turn from its archetype's layout (a non-square footprint built
    /// facing East or West): its footprint stands with width and height swapped
    /// (<see cref="World.FootW"/>, <see cref="World.FootH"/>). False for everything else.
    /// </summary>
    public bool Turned { get; internal set; }

    /// <summary>An airfield piece's airfield (<see cref="World.Airfield(uint)"/>), or 0 for anything else.</summary>
    public uint Airfield { get; internal set; }

    /// <summary>An aircraft's airfield state (holding, landing, taxiing, parked, taking off), or null while it flies free.</summary>
    public AirOps? AirOps { get; internal set; }

    /// <summary>
    /// A site whose bar covers more than its own build (an airfield's first row, which lays the
    /// runway on its bar): the work it needs in all; 0 for its archetype's build time.
    /// </summary>
    public int BuildTotal { get; internal set; }

    /// <summary>The hull facing, 0..31.</summary>
    public int Facing { get; internal set; }

    /// <summary>The turret facing, 0..31; equals <see cref="Facing"/> for units without a turret.</summary>
    public int TurretFacing { get; internal set; }

    /// <summary>
    /// How far the movement system moved the unit last tick (raw units per tick), pushes included;
    /// zero when it stood still. Renderers can interpolate with it.
    /// </summary>
    public Vec Vel { get; internal set; }

    /// <summary>
    /// The hull heading as a vector of length 1024, finer than <see cref="Facing"/> (its nearest
    /// facing). Vehicles turn it at their turn rate while rolling; infantry face where they walk.
    /// </summary>
    public Vec Heading { get; internal set; }

    /// <summary>A vehicle's forward speed (raw units per tick); vehicles accelerate and brake.</summary>
    public Fixed DriveSpeed { get; internal set; }

    // Stuck detection (movement system): the goal being tracked, the best distance to it so far,
    // and the ticks since that distance last improved.
    internal Vec StallGoal { get; set; }

    internal Fixed StallBest { get; set; }

    internal int StallTicks { get; set; }

    /// <summary>Hit points; the unit is removed at the end of the tick it reaches zero.</summary>
    public int HP { get; internal set; }

    /// <summary>Whether the unit still has hit points.</summary>
    public bool Alive => HP > 0;

    /// <summary>Hundredths of a tick until a weapon can fire; supply makes it count down faster.</summary>
    public int Reload { get; internal set; }

    /// <summary>The current order.</summary>
    public Order Order { get; internal set; }

    /// <summary>For units that deploy (<see cref="Archetype.DeployTicks"/>): stowed, deploying, deployed or stowing.</summary>
    public DeployState Deploy { get; internal set; }

    /// <summary>Ticks left of deploying or stowing.</summary>
    public int DeployLeft { get; internal set; }

    /// <summary>The unit the current salvo is aimed at, or 0 for the ground.</summary>
    public uint SalvoTarget { get; internal set; }

    /// <summary>The unit this one's last shots missed, and how many in a row.</summary>
    public uint MissTarget { get; internal set; }

    /// <summary>How many shots in a row missed <see cref="MissTarget"/>.</summary>
    public int MissStreak { get; internal set; }

    /// <summary>The tick the unit last fired a shot (0 if never).</summary>
    public uint LastShot { get; internal set; }

    /// <summary>Ticks until the next round is reloaded at a depot (weapons with a depot reload).</summary>
    public int DepotReloadLeft { get; internal set; }

    /// <summary>Which weapons the unit may fire; starts as its archetype's, changed by <see cref="CommandKind.SetWeaponMode"/>.</summary>
    public WeaponMode WeaponMode { get; internal set; }

    /// <summary>The enemy picked automatically when not on an attack order, or 0.</summary>
    public uint AutoTarget { get; internal set; }

    /// <summary>Cells still to visit, from <see cref="PathIdx"/> on.</summary>
    public IReadOnlyList<Cell> Path { get; private set; } = [];

    /// <summary>The index in <see cref="Path"/> of the next cell to visit.</summary>
    public int PathIdx { get; internal set; }

    /// <summary>The tick at which a chasing unit recomputes its path.</summary>
    public uint RepathAt { get; internal set; }

    /// <summary>
    /// The vehicle this unit is driving or riding in (a passenger: not its <see cref="Driver"/>), or 0.
    /// A unit inside is off the map: it can't be seen, hit, fire or be ordered except to exit.
    /// </summary>
    public uint Inside { get; internal set; }

    /// <summary>The unit in this vehicle's driver seat, or 0.</summary>
    public uint Driver { get; internal set; }

    /// <summary>What is left of each stock in <see cref="UnitDef.StockMax"/>: launcher salvos, drone and trap charges.</summary>
    public IReadOnlyList<int> Charges => _charges;

    /// <summary>Ticks until an ability can be used again.</summary>
    public int Cooldown { get; internal set; }

    /// <summary>Shots left in the salvo being fired, or 0.</summary>
    public int SalvoLeft { get; internal set; }

    /// <summary>The weapon firing the salvo, as an index into <see cref="UnitDef.Weapons"/>.</summary>
    public int SalvoWeapon { get; internal set; }

    /// <summary>Where the salvo is aimed: the target's position when the salvo began.</summary>
    public Vec SalvoAim { get; internal set; }

    /// <summary>Ticks until the salvo's next shot.</summary>
    public int SalvoWait { get; internal set; }

    /// <summary>Bit p set: player p has a unit that sees this one this tick. Weapons only fire at spotted enemies.</summary>
    public uint SpottedBy { get; internal set; }

    // The vision system's memory (VisionSystem.cs), hashed: SpotUntil[p] is the first tick player p's
    // viewers no longer spot this unit by distance unless one looks again (each viewer looks every
    // World.VisionTicks ticks, phased by ID, or when it enters another cell); VisionCell is the cell
    // this unit last looked from as a viewer, LookedAt the tick it last looked (uint.MaxValue: never).
    internal uint[] SpotUntil { get; } = new uint[MatchSetup.MaxPlayers + 1];
    internal Cell VisionCell { get; set; }
    internal uint LookedAt { get; set; } = uint.MaxValue;

    /// <summary>
    /// Bit p set: player p may see this unit on screen this tick. Every bit without fog of war; with
    /// fog, its owner's and <see cref="SpottedBy"/>'s (neutral units: every bit). Set by the vision
    /// system and derived from hashed state, so not hashed itself. See <see cref="World.IsVisibleTo"/>.
    /// </summary>
    public uint SeenBy { get; internal set; } = uint.MaxValue;

    // The high-ground vision bonus cache (World.HighGroundPercent): derived, not hashed.
    internal int HighGroundVersion { get; set; } = -1;
    internal Vec HighGroundPos { get; set; }
    internal int HighGroundCached { get; set; }

    /// <summary>A trap's fuse: ticks until it goes off (set by its owner's order or a nearby blast); 0 when not lit.</summary>
    public int Fuse { get; internal set; }

    /// <summary>A trap its owner set to go off only on demand: enemies stepping in no longer set it off.</summary>
    public bool AutoOff { get; internal set; }

    /// <summary>A trap that went off (it is removed this tick); false for one shot or cleared.</summary>
    public bool Detonated { get; internal set; }

    /// <summary>Bit p set: player p has revealed this hidden unit, for good.</summary>
    public uint RevealedTo { get; internal set; }

    /// <summary>The unit that placed or launched this one, or 0.</summary>
    public uint Source { get; internal set; }

    /// <summary>Ticks left before it expires, for units with a <see cref="UnitDef.Lifetime"/>; otherwise 0.</summary>
    public int Lifetime { get; internal set; }

    /// <summary>A supplier's remaining stock of salvos and charges; unused when its stock is unlimited.</summary>
    public int Stock { get; internal set; }

    /// <summary>A factory's waiting orders (unit definitions: indices into the match's Defs), oldest first.</summary>
    public List<int> ProductionQueue { get; } = [];

    /// <summary>A factory's production bays (they work at once); empty for anything else.</summary>
    public Bay[] Bays { get; internal set; } = [];

    /// <summary>A factory's doors (each finished unit drives out through a free one); empty for anything else.</summary>
    public FactoryDoor[] Doors { get; internal set; } = [];

    /// <summary>The door a factory's last finished unit took, or -1.</summary>
    public int LastDoor { get; internal set; } = -1;

    /// <summary>How many finished units a factory has given a door (the serial seeding the door pick).</summary>
    public int DoorSerial { get; internal set; }

    /// <summary>The factory a new vehicle is driving out of (its own footprint doesn't block it until it is out), or 0.</summary>
    public uint OutOf { get; internal set; }

    /// <summary>The exit cell in front of the door it drives out of (its route starts there), while <see cref="OutOf"/>.</summary>
    public Cell OutVia { get; internal set; }

    /// <summary>Where a factory sends what it produces: a cell, or next to <see cref="RallyTarget"/>.</summary>
    public Cell Rally { get; internal set; }

    /// <summary>The unit or building a factory's rally point is on, or 0.</summary>
    public uint RallyTarget { get; internal set; }

    /// <summary>A drone launcher's second designated ground point (attack ground), if <see cref="HasNextGround"/>.</summary>
    public Cell NextGround { get; internal set; }

    /// <summary>Whether a second ground point waits for the next drone.</summary>
    public bool HasNextGround { get; internal set; }

    /// <summary>Ticks left placing something (an engineer's drone station); 0 when not placing.</summary>
    public int PlaceLeft { get; internal set; }

    /// <summary>Whether it is a finished structure (or not a structure) or a construction site.</summary>
    public ConstructionState Construction { get; internal set; }

    /// <summary>A site's work done: builder rate percent times ticks (done at BuildTime x 100).</summary>
    public int Work { get; internal set; }

    /// <summary>Whether this worker added work to a site this tick (the client's "work" animation).</summary>
    public bool Building { get; internal set; }

    /// <summary>Whether it is finished: anything but a construction site.</summary>
    public bool IsBuilt => Construction == ConstructionState.Complete;

    /// <summary>For a cargo truck: the supply aboard, its full <see cref="CargoEffect.Amount"/> or 0.</summary>
    public int Cargo { get; internal set; }

    /// <summary>For a cargo truck: ticks left of unloading, or 0 when not unloading.</summary>
    public int UnloadLeft { get; internal set; }

    /// <summary>For a cargo truck: the depot it is unloading into or waiting for room at, or 0.</summary>
    public uint UnloadDepot { get; internal set; }

    /// <summary>Whether a launcher at a depot is waiting because the depot's stock is below a round's cost.</summary>
    public bool DepotStarved { get; internal set; }

    /// <summary>Percent added to the reload speed this tick, from friendly supply in range.</summary>
    public int ReloadBonus { get; internal set; }

    /// <summary>The unit it is aiming a weapon with <see cref="Model.WeaponEffect.AimTicks"/> at, or 0.</summary>
    public uint AimAt { get; internal set; }

    /// <summary>Ticks of aiming left before it fires at <see cref="AimAt"/>.</summary>
    public int AimLeft { get; internal set; }

    /// <summary>The guided missile it is guiding (a projectile ID), or 0; it stands still meanwhile.</summary>
    public uint Guiding { get; internal set; }

    /// <summary>How it engages on its own: Guard (the default), Hold fire or Return fire (<see cref="CommandKind.SetStance"/>).</summary>
    public Stance Stance { get; internal set; }

    /// <summary>The enemy unit whose shot or blast last hurt it (any stance), or 0. Return fire shoots back at it; Guard and Aggressive retaliate.</summary>
    public uint LastHitBy { get; internal set; }

    /// <summary>The tick it was last hurt by <see cref="LastHitBy"/>.</summary>
    public uint LastHitTick { get; internal set; }

    /// <summary>Where <see cref="LastHitBy"/> stood when its shot landed: retaliators go there when they can't see it.</summary>
    public Vec LastHitFrom { get; internal set; }

    /// <summary>What it does on its own: nothing special, a chase, or the walk back (the unit AI).</summary>
    public AiState Ai { get; internal set; }

    /// <summary>The enemy it is going after while <see cref="Ai"/> is Engage, or 0.</summary>
    public uint AiTarget { get; internal set; }

    /// <summary>Where it stood when the chase began: the leash counts from here, and it walks back here.</summary>
    public Vec GuardPoint { get; internal set; }

    /// <summary>When the chase (or the walk back) gives up.</summary>
    public uint GiveUpTick { get; internal set; }

    /// <summary>Where it goes while it can't see <see cref="AiTarget"/>: where the shot came from, or where it was last seen.</summary>
    public Vec SeekPoint { get; internal set; }

    // The first tick it may call its friends for help again (a salvo's splash calls once).
    internal uint RetaliateAt { get; set; }

    /// <summary>On attack-move: it stopped to fight and goes on to <see cref="Order.Cell"/> once done.</summary>
    public bool MoveOn { get; internal set; }

    /// <summary>The air target (a projectile ID: a one-way attack drone; or a scout drone's unit ID) an air-defence unit is tracking, or 0.</summary>
    public uint AirTarget { get; internal set; }

    /// <summary>Ticks it has tracked <see cref="AirTarget"/> facing it; it may fire once this reaches the weapon's lock time.</summary>
    public int TrackTicks { get; internal set; }

    /// <summary>A scout drone's height (absolute, raw: 512 a metre); 0 before its first flight tick. Other units: 0.</summary>
    public Fixed Altitude { get; internal set; }

    /// <summary>A scout drone's climb (or sink, negative) over its last tick, raw; its level step is <see cref="Vel"/>.</summary>
    public Fixed VelZ { get; internal set; }

    /// <summary>The point a scout drone orbits (the last one it was sent to).</summary>
    public Vec OrbitAt { get; internal set; }

    /// <summary>
    /// A scout drone or aircraft heading home (to its ground control station or an airfield) or at
    /// its airfield until it flies free again; it takes no attack order. <see cref="HomeReason"/> says why.
    /// </summary>
    public bool Homing { get; internal set; }

    /// <summary>
    /// Why it is <see cref="Homing"/> (the owner's flight rules, 2026-10-19): an automatic refuel trip
    /// (bingo fuel: it refuels only, then resumes), a Rearm order (rearmed and repaired too, then it
    /// resumes) or a ReturnToBase recall (refuelled and held). <see cref="Sim.HomeReason.Refuel"/> while not homing.
    /// </summary>
    public HomeReason HomeReason { get; internal set; }

    /// <summary>
    /// Its mission (the mission layer, E3: Patrol, Guard, Search, Roam), or null: it orbits
    /// <see cref="OrbitAt"/>. Kept through strikes, refuel trips and Rearm visits; a Move order ends it.
    /// </summary>
    public AirMission? AirMission { get; internal set; }

    // A ground control station's recovered drone's mission, given to the relaunched drone (E3).
    internal AirMission? KeptMission { get; set; }

    /// <summary>A scout drone that landed at its launcher this tick: removed, but not shot down or lost.</summary>
    public bool Recovered { get; internal set; }

    /// <summary>
    /// A launcher whose drone came home with <see cref="ScoutFlight.Relaunch"/> (the Watcher's ground
    /// control station): it sends a fresh drone up to <see cref="RelaunchCell"/> once its turnaround
    /// (its cooldown) is over and it has no other order.
    /// </summary>
    public bool RelaunchPending { get; internal set; }

    /// <summary>Where a relaunch sends the drone: the centre its drone orbited when it came home.</summary>
    public Cell RelaunchCell { get; internal set; }

    /// <summary>
    /// The archetype (index into <see cref="Model.ContentSet.Archetypes"/>) a pending relaunch sends up:
    /// the drone that came home, for a station that launches more than one kind (the Watcher and the Overseer).
    /// </summary>
    public int RelaunchArchetype { get; internal set; }

    // A ground control station's drone recovered without a Rearm order (a refuel trip or a recall):
    // its stocks and HP, given back to the next drone of archetype KeptArchetype it launches (the
    // owner's rule: only a Rearm order rearms or repairs). Null: the next launch is a fresh drone.
    internal int[]? KeptStocks { get; set; }

    internal int KeptHp { get; set; }

    internal int KeptArchetype { get; set; }

    /// <summary>
    /// Underground (<see cref="Inside"/> is <see cref="World.Underground"/>): the tunnel entrance it
    /// is at, or the one its current leg started from; 0 on the surface. Its riders and driver stay
    /// inside it as usual.
    /// </summary>
    public uint Tunnel { get; internal set; }

    /// <summary>Underground and travelling: the entrance its current leg goes to; 0 when it waits at <see cref="Tunnel"/>.</summary>
    public uint TunnelNext { get; internal set; }

    /// <summary>Ticks left of its current leg underground.</summary>
    public int TunnelLeft { get; internal set; }

    /// <summary>Ticks its current leg takes in all (renderers interpolate with <see cref="TunnelLeft"/>).</summary>
    public int TunnelLeg { get; internal set; }

    /// <summary>Underground: the entrance it is heading for, or 0 (it waits where it is).</summary>
    public uint TunnelTo { get; internal set; }

    /// <summary>Underground: whether it comes out at <see cref="TunnelTo"/> once there (otherwise it waits below).</summary>
    public bool TunnelOut { get; internal set; }

    /// <summary>Underground: the tick it got where it waits (units come out in that order, then by ID).</summary>
    public uint TunnelSince { get; internal set; }

    /// <summary>The entrance it is coming up out of, or 0 (see <see cref="EmergeLeft"/>).</summary>
    public uint EmergeFrom { get; internal set; }

    /// <summary>Ticks it is still coming up out of <see cref="EmergeFrom"/>: hurt if the entrance is destroyed meanwhile.</summary>
    public int EmergeLeft { get; internal set; }

    /// <summary>A tunnel entrance destroyed once: rubble with its own HP (<see cref="Model.TunnelSpec.RubbleHp"/>), a junction nobody passes in or out of.</summary>
    public bool Rubble { get; internal set; }

    /// <summary>A tunnel entrance's look, picked from the ground when it was placed: 0 standing alone (a flat hatch), 1 built into a hillside.</summary>
    public int Variant { get; internal set; }

    /// <summary>A tunnel entrance: ticks before the next unit may pass its door.</summary>
    public int Gate { get; internal set; }

    /// <summary>A tunnel entrance: ticks it stays revealed to enemies that see it after a unit passed its door.</summary>
    public int ExposeLeft { get; internal set; }

    /// <summary>Whether it is underground in a tunnel network.</summary>
    public bool InTunnel => Inside == World.Underground;

    /// <summary>Whether the unit still has path to follow.</summary>
    public bool Moving => PathIdx < Path.Count;

    /// <summary>Whether the unit can fire and use abilities (a garrisoned soldier fires from inside but can't move): it is not driving, and if it has a driver seat it is filled.</summary>
    public bool CanAct => Inside == 0 && (Def.Crew is null || Driver != 0);

    internal int[] MutableCharges => _charges;

    /// <summary>What routing wants this unit to do this tick (see <see cref="MoveIntent"/>).</summary>
    public MoveIntent Intent { get; internal set; }

    /// <summary>The group move this unit is part of, or 0 (see <see cref="World.GroupGoal"/>).</summary>
    public uint Group { get; internal set; }

    /// <summary>Where routing is taking the unit: its order's cell, a chase goal or its formation slot.</summary>
    public Cell Dest { get; internal set; }

    // How routing moves it: its own route to Dest, or its group's flow field (then Dest is its slot).
    internal RouteMode Mode { get; set; }

    // Whether a route request for it waits in the world's queue.
    internal bool PathPending { get; set; }

    // The waypoints of its own route still to refine into Path (cell indices), from RouteIdx on.
    internal int[] Route { get; set; } = [];

    internal int RouteIdx { get; set; }

    // Its formation slot's offset from the group's goal.
    internal Vec SlotOffset { get; set; }

    internal void SetPath(Cell[] path)
    {
        Path = path;
        PathIdx = 0;
    }

    // Whether it sets up once it arrives (a Deploy command on a slope sent it to flatter ground).
    internal bool DeployAfterMove { get; set; }

    // ClearPath stops the unit: no path, no route, no request, out of its group.
    internal void ClearPath()
    {
        DeployAfterMove = false;
        Path = [];
        PathIdx = 0;
        Route = [];
        RouteIdx = 0;
        PathPending = false;
        Mode = RouteMode.None;
        Group = 0;
        SlotOffset = default;
    }

    internal void Idle()
    {
        Order = default;
        ClearPath();
        ResetAi();
    }

    // ResetAi ends a chase or a walk back (any order or Stop does); the path is the caller's business.
    internal void ResetAi()
    {
        Ai = AiState.None;
        AiTarget = 0;
        GuardPoint = default;
        GiveUpTick = 0;
        SeekPoint = default;
        MoveOn = false;
    }
}

/// <summary>A factory door's state.</summary>
public enum DoorState : byte
{
    /// <summary>Shut.</summary>
    Closed,

    /// <summary>Rolling up (Left ticks to go).</summary>
    Opening,

    /// <summary>Up: the finished vehicle drives out.</summary>
    Open,

    /// <summary>Rolling down (Left ticks to go).</summary>
    Closing,
}

/// <summary>One production bay of a factory: what it makes and how long to go; finished, it waits for a door.</summary>
public sealed class Bay
{
    /// <summary>The unit definition in production (an index into the match's Defs), or -1 for none.</summary>
    public int Def { get; internal set; } = -1;

    /// <summary>Ticks of production left (0: finished, waiting for a free door).</summary>
    public int Left { get; internal set; }

    /// <summary>Ticks the finished unit has waited for a door.</summary>
    public int Wait { get; internal set; }
}

/// <summary>One door of a factory: its state, the finished unit behind it, what is driving out.</summary>
public sealed class FactoryDoor
{
    /// <summary>The door's state.</summary>
    public DoorState State { get; internal set; }

    /// <summary>Ticks left opening or closing.</summary>
    public int Left { get; internal set; }

    /// <summary>The finished unit (an index into the match's Defs) that comes out here, or -1 for none.</summary>
    public int Def { get; internal set; } = -1;

    /// <summary>The vehicle driving out through it, or 0.</summary>
    public uint Vehicle { get; internal set; }

    /// <summary>Ticks the finished unit has waited for the exit lane (or, out, to clear it).</summary>
    public int Wait { get; internal set; }
}

/// <summary>Where a one-way attack drone is in its flight.</summary>
public enum DronePhase : byte
{
    /// <summary>Not a drone.</summary>
    None,

    /// <summary>Off the rail, climbing to its cruise height: a hill in its way stops it.</summary>
    Climb,

    /// <summary>Cruising above everything.</summary>
    Cruise,

    /// <summary>Diving onto its point: the first wall, vehicle, soldier or the ground stops it.</summary>
    Dive,
}

/// <summary>A shot in flight: it lands on a point after travelling, then explodes.</summary>
public sealed class Projectile
{
    internal Projectile(uint id, Unit shooter, WeaponDef weapon, Vec dest, uint target = 0)
    {
        Id = id;
        Cover = shooter.Garrison;
        Target = target;
        Owner = shooter.Owner;
        Shooter = shooter.Id;
        ShooterDef = shooter.Def;
        Weapon = weapon;
        Pos = shooter.Pos;
        Dest = dest;
        Interceptible = weapon.Effect.Intercept;
        Phase = weapon.Effect.Drone is not null ? DronePhase.Climb : DronePhase.None;
        if (weapon.Effect.Guided is not null)
        {
            Guided = true;
            var d = dest - shooter.Pos;
            Vel = d.X.Raw == 0 && d.Y.Raw == 0 ? Facing.Dir(shooter.TurretFacing).WithLen(weapon.Effect.ProjectileSpeed) : d.WithLen(weapon.Effect.ProjectileSpeed);
        }
    }

    /// <summary>A drone's flight phase (none for other projectiles).</summary>
    public DronePhase Phase { get; internal set; }

    /// <summary>A drone's or an air-defence missile's height (absolute, raw: 512 a metre).</summary>
    public Fixed Altitude { get; internal set; }

    /// <summary>An air-defence missile's vertical velocity per tick (raw); <see cref="Vel"/> is its horizontal part.</summary>
    public Fixed VelZ { get; internal set; }

    /// <summary>An air-defence missile: the slant range to its target at launch.</summary>
    public Fixed LaunchRange { get; internal set; }

    /// <summary>An air-defence missile: the crossing rate the shooter saw at launch, milliradians per second.</summary>
    public int LaunchCross { get; internal set; }

    /// <summary>An air-defence missile: how many ticks the shooter had tracked its target at launch.</summary>
    public int Tracked { get; internal set; }

    /// <summary>A drone's height when its dive began.</summary>
    public Fixed DiveFrom { get; internal set; }

    /// <summary>A drone's distance to its point when its dive began.</summary>
    public Fixed DiveDist { get; internal set; }

    /// <summary>A drone's slope (permille, up positive) when its dive (the pitch-over) began.</summary>
    public int DiveSlope { get; internal set; }

    /// <summary>Ticks since a drone left its rail (its booster drops after the weapon's BoosterTicks).</summary>
    public int Age { get; internal set; }

    /// <summary>Its interception class (from its weapon): the hook for future interceptors.</summary>
    public InterceptClass Interceptible { get; internal set; }

    /// <summary>A guided missile's velocity per tick (zero for other projectiles).</summary>
    public Vec Vel { get; internal set; }

    /// <summary>Whether a guided missile is still steering toward its target.</summary>
    public bool Guided { get; internal set; }

    /// <summary>How far a guided missile has flown.</summary>
    public Fixed Flown { get; internal set; }

    /// <summary>How far an unguided guided missile still flies before it hits the ground.</summary>
    public Fixed DropLeft { get; internal set; }

    /// <summary>Whether it flies a ballistic arc over terrain (a map with terrain, not guided): see <see cref="World.ArcHeightRaw"/>.</summary>
    public bool Arcs { get; internal set; }

    /// <summary>Whether it is a guided missile flying low over terrain (a map with terrain): crests stop it.</summary>
    public bool LowFlight { get; internal set; }

    /// <summary>On terrain: where it was launched from.</summary>
    public Vec Origin { get; internal set; }

    /// <summary>On terrain: its launch height (absolute).</summary>
    public Fixed LaunchHeight { get; internal set; }

    /// <summary>On terrain: the height it comes down to over its aim point (absolute).</summary>
    public Fixed LandHeight { get; internal set; }

    /// <summary>On terrain: how far its arc rises over the straight line from launch to landing.</summary>
    public Fixed ArcRise { get; internal set; }

    /// <summary>An air strike missile (<see cref="Model.WeaponEffect.AirStrike"/>): whether its launch roll hit.</summary>
    public bool StrikeHit { get; internal set; }

    /// <summary>An air strike missile: the point it flies at (its target's, or where a miss lands).</summary>
    public Vec StrikeAim { get; internal set; }

    /// <summary>An air strike missile: whether its target moved at launch (the lower hit chance).</summary>
    public bool StrikeMoving { get; internal set; }

    /// <summary>The projectile's ID, from the same sequence as unit IDs.</summary>
    public uint Id { get; }

    /// <summary>The unit it was aimed at, or 0 for the ground.</summary>
    public uint Target { get; }

    /// <summary>The player who fired it.</summary>
    public byte Owner { get; }

    /// <summary>The building it was fired from (by a garrisoned soldier), or 0; it flies out through that building's walls.</summary>
    public uint Cover { get; }

    /// <summary>The ID of the unit that fired it (which may be dead by now).</summary>
    public uint Shooter { get; }

    /// <summary>The shooter's definition.</summary>
    public UnitDef ShooterDef { get; }

    /// <summary>The weapon that fired it.</summary>
    public WeaponDef Weapon { get; }

    /// <summary>The current position.</summary>
    public Vec Pos { get; internal set; }

    /// <summary>Where it will land if it hits nothing on the way.</summary>
    public Vec Dest { get; }

    // Set when it went off this tick; it is removed at the end of the projectile system.
    internal bool Done { get; set; }
}

/// <summary>How an air-defence missile ended.</summary>
public enum AirBurstKind : byte
{
    /// <summary>The proximity fuse went off and the roll killed the target: it breaks up and falls (no blast).</summary>
    Kill,

    /// <summary>The proximity fuse went off but the roll failed: the target flies on.</summary>
    Miss,

    /// <summary>It flew its max flight without reaching its target and blew itself up in the air.</summary>
    SelfDestruct,

    /// <summary>It met the ground.</summary>
    Ground,

    /// <summary>
    /// The proximity fuse went off and the hit roll hit (a class with a hit rule: the Overseer), but
    /// the target survived its share of damage and flies on.
    /// </summary>
    Hit,
}

/// <summary>An air-defence missile going off this tick (for renderers and logs).</summary>
/// <param name="Missile">The missile's projectile ID.</param>
/// <param name="Target">The projectile it was fired at.</param>
/// <param name="Shooter">The unit that fired it.</param>
/// <param name="At">Where it went off (map position).</param>
/// <param name="Altitude">Its height there (absolute, raw: 512 a metre).</param>
/// <param name="Kind">How it ended.</param>
/// <param name="KillPermille">For a fuse (Kill, Miss or Hit): the kill chance (or, for a class with a hit rule, the hit chance) in permille; otherwise 0.</param>
/// <param name="Roll">For a fuse: the roll, 0 to 999 (a kill when below the chance); otherwise -1.</param>
public readonly record struct AirBurst(uint Missile, uint Target, uint Shooter, Vec At, Fixed Altitude, AirBurstKind Kind, int KillPermille, int Roll);

/// <summary>A projectile going off this tick.</summary>
/// <param name="Projectile">The projectile's ID.</param>
/// <param name="At">Where it went off.</param>
/// <param name="Hit">The unit it struck directly, or 0 for the ground (its aim point) or terrain.</param>
/// <param name="Terrain">Whether it struck blocked terrain (a rock) on the way.</param>
public readonly record struct Detonation(uint Projectile, Vec At, uint Hit, bool Terrain);

/// <summary>Per-player state.</summary>
public sealed class Player
{
    internal Player(CompiledPlayer setup) => Setup = setup;

    /// <summary>The player's ID, starting at 1.</summary>
    public byte Id => Setup.Id;

    /// <summary>The faction, presets and starting army.</summary>
    public CompiledPlayer Setup { get; }

    /// <summary>The player's money (no economy until Phase 2).</summary>
    public int Credits { get; internal set; }

    /// <summary>Whether the player has lost: they own no unit with one of their faction's <see cref="Faction.DefeatedWithout"/> tags.</summary>
    public bool Defeated { get; internal set; }
}

/// <summary>How the match stands.</summary>
/// <param name="Over">Whether at most one player is left.</param>
/// <param name="Winner">The last player standing, or 0 while the match runs and for a draw.</param>
/// <param name="EndTick">The tick count when the match ended, or 0.</param>
public readonly record struct MatchResult(bool Over, byte Winner, uint EndTick);
