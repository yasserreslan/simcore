using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// The crew rules of a vehicle: one driver seat, and optionally passenger seats in the back.
/// Without a driver the vehicle can neither move nor fire; passengers only ride.
/// </summary>
public sealed class CrewSpec
{
    /// <summary>Only units with one of these tags may drive (any unit if empty).</summary>
    public required TagSet Tags { get; init; }

    /// <summary>How close a unit must walk to climb in.</summary>
    public required Fixed EnterRange { get; init; }

    /// <summary>The percent chance, drawn from the world's generator, that the driver survives the vehicle's destruction.</summary>
    public required int Survival { get; init; }

    /// <summary>The percent of a surviving driver's max HP lost when bailing out.</summary>
    public required int ExitDamage { get; init; }

    /// <summary>
    /// Passenger seats besides the driver's (0: none). Passengers can't be seen, hit or fire; when
    /// the vehicle is destroyed each bails out by the same <see cref="Survival"/> and
    /// <see cref="ExitDamage"/> rules as the driver.
    /// </summary>
    public int Passengers { get; init; }

    /// <summary>Only units with one of these tags may ride as passengers (any ground unit if empty).</summary>
    public TagSet PassengerTags { get; init; }

    /// <summary>
    /// Ticks between passengers getting out on an unload-all (the Humvee's fast dismount: 8, 0.4 s): the first gets
    /// out at once, the next one every this many ticks. 0: all of them at once (the infantry carrier).
    /// </summary>
    public int UnloadInterval { get; init; }

    /// <summary>
    /// Percent of a hit on the unit (one of <see cref="OccupantWarheads"/>) its driver takes too, as if hit himself (content
    /// <c>occupantDamage</c>; 0 for none): an open emplacement's crewman (the howitzer's) is not behind armour. A driver killed
    /// this way leaves the seat empty and the unit standing.
    /// </summary>
    public int OccupantDamage { get; init; }

    /// <summary>The warheads (indexes) whose hits reach the driver (<see cref="OccupantDamage"/>).</summary>
    public IReadOnlyList<int> OccupantWarheads { get; init; } = [];
}

/// <summary>
/// A factory: it produces the faction's archetypes with one of <see cref="Tags"/> (vehicles), in
/// <see cref="Bays"/> bays at once, from a queue of up to <see cref="QueueMax"/> orders; a finished
/// vehicle drives out crewed through a free one of its <see cref="Doors"/> to the rally point.
/// </summary>
public sealed class ProductionSpec
{
    /// <summary>It produces archetypes with one of these tags.</summary>
    public required TagSet Tags { get; init; }

    /// <summary>Bays producing in parallel (each finished unit then takes a free door).</summary>
    public required int Bays { get; init; }

    /// <summary>Orders waiting and in production, together, at most.</summary>
    public required int QueueMax { get; init; }

    /// <summary>Ticks a door takes to open, and to close.</summary>
    public required int DoorTicks { get; init; }

    /// <summary>Ticks a finished vehicle waits for its exit lane before it goes anyway.</summary>
    public required int PushAfter { get; init; }

    /// <summary>The preset or archetype ID that drives what it produces.</summary>
    public required string Crew { get; init; }

    /// <summary>Per door, where its units come out (tenths of a cell from the footprint's top-left); empty for one door per bay at the default places.</summary>
    public IReadOnlyList<(int X, int Y)> Exits { get; init; } = [];

    /// <summary>Per door, where its units appear inside, the bay floor behind it (tenths of a cell from the top-left); empty for 4 cells inside the front wall.</summary>
    public IReadOnlyList<(int X, int Y)> BayPoints { get; init; } = [];

    /// <summary>The doors: one per exit, or one per bay without exits; none for <see cref="IntoStation"/>.</summary>
    public int Doors => IntoStation ? 0 : Exits.Count > 0 ? Exits.Count : Bays;

    /// <summary>
    /// An airfield facility's production (E8, decision D9): a finished aircraft is parked into a free
    /// station of the facility's airfield (no doors; it waits in its bay while none is free).
    /// </summary>
    public bool IntoStation { get; init; }

    /// <summary>The default rally point (tenths of a cell from the top-left), or null for 5 cells in front.</summary>
    public (int X, int Y)? Rally { get; init; }

    /// <summary>Below this percent of its HP it produces nothing (0: never stops).</summary>
    public int MinHpPercent { get; init; }

    /// <summary>Archetypes with one of these tags are never built here (the vehicle garage: no tanks); empty for none.</summary>
    public TagSet ExcludeTags { get; init; }

    /// <summary>Whether it builds an archetype with these tags: one of <see cref="Tags"/> and none of <see cref="ExcludeTags"/>.</summary>
    public bool Builds(TagSet tags) => !Tags.IsEmpty && tags.Matches(Tags) && (ExcludeTags.IsEmpty || !tags.Matches(ExcludeTags));
}

/// <summary>How workers build a structure: a site that rises with their work.</summary>
public sealed class ConstructionSpec
{
    /// <summary>Percent of max HP a fresh site has (it rises with progress to max HP).</summary>
    public required int StartHp { get; init; }

    /// <summary>The progress percents where construction stages 1 to 4 begin.</summary>
    public required IReadOnlyList<int> Stages { get; init; }

    /// <summary>Percent of the cost a cancelled site refunds.</summary>
    public required int Refund { get; init; }

    /// <summary>Percent of a builder's rate for the 1st, 2nd, ... worker on one site (the last for any more).</summary>
    public required IReadOnlyList<int> Helpers { get; init; }

    /// <summary>On terrain: the cells over which the ground eases back from the site's pad.</summary>
    public required int PadBlend { get; init; }
}

/// <summary>
/// A tunnel entrance (docs/design.md, "Tunnels"): a node of its builder's tunnel network. Units
/// that fit (<see cref="Archetype.TunnelSize"/> up to <see cref="Size"/>) go in at its door, travel
/// underground to any linked entrance and come out where they fit. Entrances within
/// <see cref="LinkRange"/> of each other link up; a connected network shares the sum of its
/// entrances' <see cref="Capacity"/>. Destroyed, it becomes rubble (<see cref="RubbleHp"/>): still
/// a junction holding its capacity, but nobody passes in or out; destroying the rubble removes the
/// node (the survival rules apply) and the ground slowly returns to what it was.
/// </summary>
public sealed class TunnelSpec
{
    /// <summary>The largest unit size that may go in and come out here: 1 infantry, 2 light and medium vehicles, 3 heavy vehicles.</summary>
    public required int Size { get; init; }

    /// <summary>Slots it adds to its network.</summary>
    public required int Capacity { get; init; }

    /// <summary>Ticks between two units passing its door, in or out (its throughput).</summary>
    public required int Throughput { get; init; }

    /// <summary>The HP of its rubble.</summary>
    public required int RubbleHp { get; init; }

    /// <summary>How far it links to other entrances (two link within the smaller of their ranges).</summary>
    public required Fixed LinkRange { get; init; }

    /// <summary>How close another entrance may be built (centre to centre).</summary>
    public required Fixed Spacing { get; init; }

    /// <summary>How fast units travel underground (raw units per tick).</summary>
    public required Fixed Speed { get; init; }

    /// <summary>Ticks a unit passing its door reveals it to enemies that see it (it is otherwise hidden when <see cref="Archetype.Hidden"/>).</summary>
    public required int ExposeTicks { get; init; }

    /// <summary>The steepest grade (percent) its footprint and apron may have; 0: any ground its builders can walk.</summary>
    public required int FlatGrade { get; init; }

    /// <summary>The rise behind the site (percent) from which it is built into the hillside instead of standing alone.</summary>
    public required int HillGrade { get; init; }

    /// <summary>
    /// Rows of its footprint, from the back, under its headwall (the portal's face is at the end of
    /// them, its recess in the last one): the ground stands at the mound height on the back edge
    /// and on the floor from the second row line on.
    /// </summary>
    public required int Depth { get; init; }

    /// <summary>Whether the outer columns of its footprint are retaining walls (the ramp runs between them).</summary>
    public required bool Walls { get; init; }

    /// <summary>The height (raw) of the ground over its headwall when it stands alone; 0 for none (a flat hatch).</summary>
    public required int Mound { get; init; }

    /// <summary>The same when built into a hillside.</summary>
    public required int HillMound { get; init; }

    /// <summary>Cells of flat apron in front of its footprint.</summary>
    public required int Apron { get; init; }

    /// <summary>Cells over which the ground eases from its shape back to the land around.</summary>
    public required int Blend { get; init; }

    /// <summary>Percent of max HP a unit coming out of it loses when it is destroyed under it.</summary>
    public required int EmergeDamage { get; init; }

    /// <summary>Ticks the ground takes to return to its old shape once the entrance is gone.</summary>
    public required int RestoreTicks { get; init; }
}

/// <summary>A building soldiers can garrison: they fire from inside, protected, until it falls.</summary>
public sealed class GarrisonSpec
{
    /// <summary>How many units fit inside when intact.</summary>
    public required int Capacity { get; init; }

    /// <summary>Units with one of these tags may enter (any unit if empty).</summary>
    public required TagSet Tags { get; init; }

    /// <summary>How close to a door a unit must walk to get in.</summary>
    public required Fixed EnterRange { get; init; }

    /// <summary>Range added to the weapons of units inside.</summary>
    public required Fixed RangeBonus { get; init; }

    /// <summary>Vision added to units inside.</summary>
    public required Fixed VisionBonus { get; init; }

    /// <summary>Percent of an explosive hit on the building that every unit inside takes too.</summary>
    public required int OccupantDamage { get; init; }

    /// <summary>The warheads (indexes) that reach the units inside.</summary>
    public required IReadOnlyList<int> OccupantWarheads { get; init; }

    /// <summary>HP percent thresholds of the damage stages: above the first is stage 0, and so on; 0 HP is the last stage (rubble).</summary>
    public required IReadOnlyList<int> Stages { get; init; }

    /// <summary>Percent of <see cref="Capacity"/> left at each stage (one per stage before rubble).</summary>
    public required IReadOnlyList<int> StageCapacity { get; init; }

    /// <summary>Percent of max HP a unit loses when thrown out because the building lost room.</summary>
    public required int EjectDamage { get; init; }

    /// <summary>Percent chance (world generator) each unit inside survives the collapse.</summary>
    public required int CollapseSurvival { get; init; }

    /// <summary>Percent of max HP a collapse survivor is left with.</summary>
    public required int CollapseHp { get; init; }

    /// <summary>Door cells, relative to the footprint's top-left cell (outside the footprint).</summary>
    public required IReadOnlyList<(int X, int Y)> Doors { get; init; }

    /// <summary>
    /// An owned post (an observation post): it belongs to its builder for good, only its side may
    /// enter (once it is finished), and it never turns neutral when empty. False for town
    /// buildings, which are placed neutral and belong to whoever is inside.
    /// </summary>
    public bool Owned { get; init; }

    /// <summary>
    /// Occupants with one of these tags make the building a launch point (the observation post with
    /// a sniper inside launches the scout drone): its abilities can be used only while one is inside
    /// (<see cref="Sim.World.PostLauncher"/>). Empty: no such rule.
    /// </summary>
    public TagSet LauncherTags { get; init; }

    /// <summary>
    /// Occupants that must set up to fire (<see cref="Archetype.DeployTicks"/>, the mortar) may set up
    /// and fire from inside only with one of these tags (the observation post lists "mortar"); empty:
    /// they hold fire inside (every other building). See <see cref="Sim.World.FiresFromHere"/>.
    /// </summary>
    public TagSet DeployTags { get; init; }
}

/// <summary>
/// Vision from high ground (the observation post): the vision radius grows by
/// <see cref="PercentPerMetre"/> for every metre the unit stands above the mean ground height
/// within <see cref="RadiusCells"/> cells around it, up to <see cref="MaxPercent"/>. Only on terrain.
/// </summary>
public sealed class HighGroundSpec
{
    /// <summary>Percent more vision per metre above the surroundings.</summary>
    public required int PercentPerMetre { get; init; }

    /// <summary>The largest bonus, in percent.</summary>
    public required int MaxPercent { get; init; }

    /// <summary>The half-size, in cells, of the square whose mean ground height is the reference.</summary>
    public required int RadiusCells { get; init; }
}

/// <summary>
/// A kind of entity: infantry, vehicle, drone, structure or trap. Players turn
/// archetypes with slots into units through presets; archetypes without slots
/// always carry exactly their fixed <see cref="Components"/>.
/// </summary>
public sealed class Archetype
{
    /// <summary>The position in <see cref="ContentSet.Archetypes"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The stable data key.</summary>
    public required string Id { get; init; }

    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>The debug-view letter.</summary>
    public required char Glyph { get; init; }

    /// <summary>The tags, which component requirements and effect filters test.</summary>
    public required TagSet Tags { get; init; }

    /// <summary>How it moves; <see cref="Locomotor.Flying"/> puts it on the air layer.</summary>
    public required Locomotor Locomotor { get; init; }

    /// <summary>The layer it is on, implied by <see cref="Locomotor"/>.</summary>
    public Layers Layer => Locomotor == Locomotor.Flying ? Layers.Air : Layers.Ground;

    /// <summary>The armor class, as an index into <see cref="ContentSet.ArmorClasses"/>.</summary>
    public required int Armor { get; init; }

    /// <summary>Starting hit points.</summary>
    public required int MaxHp { get; init; }

    /// <summary>Distance per tick; zero means it cannot move.</summary>
    public required Fixed Speed { get; init; }

    /// <summary>Facings per tick.</summary>
    public required int TurnRate { get; init; }

    /// <summary>The radius projectiles collide with (a circle; structures use their footprint).</summary>
    public required Fixed Radius { get; init; }

    /// <summary>Ticks to set up (deploy) before it can fire; 0 for units that fire at once.</summary>
    public required int DeployTicks { get; init; }

    /// <summary>Ticks to pack up (stow) before it can move after deploying.</summary>
    public required int StowTicks { get; init; }

    /// <summary>
    /// For units that deploy: whether it picks targets on its own once set up (the mortar). It never
    /// sets up by itself. Launchers without it fire on orders only.
    /// </summary>
    public bool AutoFireDeployed { get; init; }

    /// <summary>Its crushing level: driving forward into an enemy whose <see cref="Crushable"/> is lower (and above 0) kills it. 0: it never crushes.</summary>
    public int Crusher { get; init; }

    /// <summary>How hard it is to crush (infantry 1); 0: never crushed.</summary>
    public int Crushable { get; init; }

    /// <summary>Ticks a vehicle takes to reach top speed from a stop; 0 for the default (5).</summary>
    public int AccelTicks { get; init; }

    /// <summary>The weapon mode units of this archetype start with (players can change it per unit).</summary>
    public required WeaponMode WeaponMode { get; init; }

    /// <summary>Whether the weapon aims independently of the hull (and can fire on the move).</summary>
    public required bool Turret { get; init; }

    /// <summary>Whether enemies can't see or target it until they reveal it (traps).</summary>
    public required bool Hidden { get; init; }

    /// <summary>
    /// Whether this structure variant may only be built by a player whose
    /// presets include a component that unlocks it.
    /// </summary>
    public required bool Locked { get; init; }

    /// <summary>The build cost in credits.</summary>
    public required int Cost { get; init; }

    /// <summary>How far it sees. Weapons only fire at enemies some friendly unit sees.</summary>
    public required Fixed Vision { get; init; }

    /// <summary>How far it reveals hidden enemy entities such as traps; zero for none.</summary>
    public required Fixed Detect { get; init; }

    /// <summary>
    /// How far it detects air targets (one-way attack drones), a slant (3D) distance from its eye;
    /// zero for none. Air defence tracks only what it detects (docs: air defence design).
    /// </summary>
    public Fixed AirVision { get; init; }

    /// <summary>
    /// The ground radar's reach (H1, a radar structure; 0 for none): within it the side spots enemy vehicles
    /// anywhere (no ridge or wood hides them) and enemy infantry in the open (woods still hide soldiers). Off as rubble.
    /// </summary>
    public Fixed GroundRadar { get; init; }

    /// <summary>
    /// Whether its ground radar spots only vehicles (H-night: a helicopter's mast radar; content <c>groundRadarVehicles</c>):
    /// enemy ground vehicles within <see cref="GroundRadar"/> for its side, never soldiers.
    /// </summary>
    public bool GroundRadarVehicles { get; init; }

    /// <summary>
    /// The radar-cued lock (the mobile radar; content <c>cueLock</c>, a percent, 0 for none): an air target this unit tracks
    /// with its air vision while set up is locked by its side's ground air defence weapons in this percent of their own
    /// lock time. It never adds range: a weapon still fires only within its own.
    /// </summary>
    public int CueLock { get; init; }

    /// <summary>
    /// Directional armour (the tank's; content <c>directionalArmor</c>), or null: every hit on the unit is scaled by where it
    /// lands on the hull, comparing where it comes from with the hull's facing (<see cref="Sim.World.HitFacingOf"/>).
    /// </summary>
    public DirectionalArmor? DirectionalArmor { get; init; }

    /// <summary>
    /// The airlift (an airlifter's vehicle hold; content <c>airlift</c>), or null: parked at an airfield of its side with unlimited
    /// supply (a main air base) it takes vehicles with one of <see cref="AirliftSpec.Tags"/> up its ramp, carrying them instead of its
    /// cargo pallets, and lets them drive off where it delivers (<see cref="Sim.World.AirliftAboard"/>).
    /// </summary>
    public AirliftSpec? Airlift { get; init; }

    /// <summary>
    /// A slow turret (content <c>traverseTicks</c>; 0 for the default two facings a tick): its turret turns one facing every this
    /// many ticks (the howitzer's: 8, half a turn in 6.4 s).
    /// </summary>
    public int TraverseTicks { get; init; }

    /// <summary>One turn of its search antenna in ticks (content <c>sweepTicks</c>; 0 for none): <see cref="Sim.World.RadarSweep"/>, for renderers.</summary>
    public int SweepTicks { get; init; }

    /// <summary>
    /// The stance its units start in, as a <see cref="Sim.Stance"/> value (content: <c>stance</c>;
    /// default Guard, 0).
    /// </summary>
    public int Stance { get; init; }

    /// <summary>
    /// Whether its units move on their own to retaliate, chase (Aggressive) or defend a guard point.
    /// Content <c>pursue</c>; by default true for armed ground units that move and don't set up to
    /// fire or watch the sky (no deploy, no air vision). Others only fire from where they stand.
    /// </summary>
    public bool Pursue { get; init; }

    /// <summary>The leash (a chase ends once its target is farther than this from the guard point); zero: vision times the rules' outer percent.</summary>
    public Fixed Leash { get; init; }

    /// <summary>The farthest shooter that sets off its retaliation; zero: the rules' value.</summary>
    public Fixed RetaliateMax { get; init; }

    /// <summary>Ticks it lives before it is removed (a drone's battery); zero lives forever.</summary>
    public required int Lifetime { get; init; }

    /// <summary>Ticks it lasts once its weapons are empty (a single-use drone station); 0 never.</summary>
    public int ExpireWhenEmpty { get; init; }

    /// <summary>How high (raw, 512 a metre) what flies down at it is stopped: a drone's dive.</summary>
    public long HeightRaw { get; init; }

    /// <summary>Vision from high ground, or null (most units).</summary>
    public HighGroundSpec? HighGround { get; init; }

    /// <summary>A scout drone's flight (height, orbit, endurance and recovery), or null for anything else.</summary>
    public ScoutFlight? Flight { get; init; }

    /// <summary>Its mission-layer numbers (Guard's leash and default filter), or null (no leash, ground kinds).</summary>
    public MissionSpec? Mission { get; init; }

    /// <summary>Its signature as an air target (detect, lock, kill percents; E5); <see cref="Model.Signature.Normal"/> without a <c>signature</c> block.</summary>
    public Signature Signature { get; init; } = Signature.Normal;

    /// <summary>
    /// The exclusive group it belongs to (an index into <see cref="ContentSet.ExclusiveGroups"/>; content <c>exclusive</c>), or -1:
    /// production keeps each player within the group's rule (the heavy bombers' "choose 1 of 3").
    /// </summary>
    public int Exclusive { get; init; } = -1;

    /// <summary>Its countermeasure dispensers (flares, chaff; the data shape is E5, the behaviour E6), in content order; empty for none.</summary>
    public IReadOnlyList<Countermeasure> Countermeasures { get; init; } = [];

    /// <summary>Its evasion (a fixed wing's break on a launch warning; E6), or null: it never breaks.</summary>
    public Evade? Evade { get; init; }

    /// <summary>Its attack-run geometry against the ground (E13), or null: the derived defaults.</summary>
    public AttackRunSpec? AttackRun { get; init; }

    /// <summary>How it flies in an element with others given the same order (E13), or null: the default slots.</summary>
    public ElementSpec? Element { get; init; }

    /// <summary>Its air-to-air pursuit separation (E13), or null: the defaults.</summary>
    public PursuitSpec? Pursuit { get; init; }

    /// <summary>Its orbit attack (a gunship's left-hand pylon turn and side fire; H3), or null: attack orders are strike runs.</summary>
    public GunshipSpec? Gunship { get; init; }

    /// <summary>
    /// Its one-way attack (the Kaman drone: missiles, then a dive into its target; the Kaman system), or null. Such a unit
    /// is launched by a weapon that <see cref="WeaponEffect.Launches"/> it and takes no orders.
    /// </summary>
    public KamanSpec? Kaman { get; init; }

    /// <summary>What happens when it is lost in the air (a wreck's crash path, the pilot's ejection; E10), or null: it breaks up and falls.</summary>
    public LossSpec? Loss { get; init; }

    /// <summary>
    /// An Air-layer unit without a flight block that is an air target all the same (the FPV drone; E6): its
    /// class and height, or null.
    /// </summary>
    public AirBodySpec? AirBody { get; init; }

    /// <summary>
    /// The components a preset may pick for each slot, by <see cref="SlotKind"/>
    /// value, as indices into <see cref="ContentSet.Components"/>. An empty list
    /// means the archetype has no such slot; an archetype without any slot has
    /// no presets ("no loadout").
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<int>> SlotChoices { get; init; }

    /// <summary>The components every unit of the archetype carries, as indices into <see cref="ContentSet.Components"/>.</summary>
    public required IReadOnlyList<int> Components { get; init; }

    /// <summary>The driver seat, or null for anything that needs no crew.</summary>
    public required CrewSpec? Crew { get; init; }

    /// <summary>The building's garrison rules, or null for anything that can't be garrisoned.</summary>
    public GarrisonSpec? Garrison { get; init; }

    /// <summary>
    /// A civilian structure without a garrison (the farm buildings; content <c>neutral</c>): placed
    /// neutral (owner 0) and nobody's for good, it blocks its footprint for ground units like a town
    /// building, its rubble too.
    /// </summary>
    public bool Neutral { get; init; }

    /// <summary>
    /// Placed neutral (owner 0), blocking its footprint for good: a town building (a garrison that is
    /// not <see cref="GarrisonSpec.Owned"/>) or a <see cref="Neutral"/> structure.
    /// </summary>
    public bool PlacedNeutral => Neutral || Garrison is { Owned: false };

    /// <summary>How workers build it, or null for a structure that can't be built.</summary>
    public ConstructionSpec? Construction { get; init; }

    /// <summary>What it produces (a factory), or null.</summary>
    public ProductionSpec? Production { get; init; }

    /// <summary>A tunnel entrance's rules, or null for anything else.</summary>
    public TunnelSpec? Tunnel { get; init; }

    /// <summary>
    /// Its size underground: 1 infantry, 2 light and medium vehicles, 3 heavy vehicles; 0 for what
    /// never goes into a tunnel (aircraft, structures, traps). See <see cref="TunnelSpec.Size"/>.
    /// </summary>
    public int TunnelSize { get; init; }

    /// <summary>The slots it takes in a tunnel network (a carrier adds its riders' slots).</summary>
    public int TunnelSlots { get; init; }

    /// <summary>An aircraft's airfield data (size class, span, runway minimums, approach, service), or null.</summary>
    public AircraftSpec? Aircraft { get; init; }

    /// <summary>Its role in an airfield (facility, row, runway segment, station), or null.</summary>
    public AirfieldPieceSpec? Airfield { get; init; }

    /// <summary>
    /// Pavement (an airfield's apron and runway): it blocks no routing and no movement (ground units
    /// drive across it; on terrain its cells become road) but still blocks other structures.
    /// </summary>
    public bool Pavement { get; init; }

    /// <summary>Never a target: no attack orders, no automatic targeting (content <c>targetable: false</c>).</summary>
    public bool Untargetable { get; init; }

    /// <summary>Never damaged.</summary>
    public bool Invulnerable { get; init; }

    /// <summary>At 0 HP a standing piece becomes rubble at this percent of max HP (0: no rubble stage).</summary>
    public int RubbleHpPercent { get; init; }

    /// <summary>Workers may repair it: one worker restores the whole bar in this percent of the build time (0: not repairable).</summary>
    public int RepairPercentOfBuild { get; init; }

    /// <summary>A reload pad (the Patriot-type launcher's; content <c>reloadPad</c>), or null.</summary>
    public ReloadPad? ReloadPad { get; init; }

    /// <summary>A helipad (one helicopter builds, parks and is serviced on it; content <c>helipad</c>), or null.</summary>
    public HelipadSpec? Helipad { get; init; }

    /// <summary>A factory whose doors also service its side's vehicles (the military garage; content <c>service</c>), or null.</summary>
    public ServiceSpec? Service { get; init; }

    /// <summary>A drop zone pallets are airdropped on (logistics: the field ammo point, the supply stash; content <c>dropZone</c>), or null.</summary>
    public DropZoneSpec? DropZone { get; init; }

    /// <summary>A placement rule: never within a distance of its side's structures of some archetypes (the forward airbase; content <c>awayFrom</c>), or null.</summary>
    public AwayFromSpec? AwayFrom { get; init; }

    /// <summary>The footprint width in cells; zero for anything that is not a structure.</summary>
    public required int FootprintW { get; init; }

    /// <summary>The footprint height in cells.</summary>
    public required int FootprintH { get; init; }

    /// <summary>Ticks to build with one builder at 100% rate.</summary>
    public required int BuildTime { get; init; }

    /// <summary>Whether it is a structure: it has a footprint and can be built.</summary>
    public bool IsStructure => FootprintW > 0;

    /// <summary>Whether players build presets for it: it has at least one slot.</summary>
    public bool HasSlots
    {
        get
        {
            foreach (var s in SlotChoices)
            {
                if (s.Count > 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}

/// <summary>
/// A reload pad (content <c>reloadPad</c>): a packed-up unit with one of <see cref="Tags"/> standing still on its footprint
/// has every weapon filled to its full ammo after <see cref="ReloadTicks"/>, one unit at a time, while a finished airfield
/// facility (or air base core) of the pad's side stands within <see cref="AirfieldRange"/>; it is placed only there.
/// </summary>
public sealed record ReloadPad(TagSet Tags, int ReloadTicks, Fixed AirfieldRange);

/// <summary>
/// Vehicle service through a factory's doors (content <c>service</c>; the military garage): its doors are shared, each a bay
/// slot that production (at most <see cref="ProductionSpec.Bays"/> of them) or service uses. A vehicle of its side with one of
/// <see cref="Tags"/> sent to it drives in through a free door; inside, every weapon is refilled after
/// <see cref="ReloadTicks"/>, its HP rises evenly to full over <see cref="RepairTicks"/> and an empty cargo truck is refilled
/// after <see cref="CargoTicks"/> (in parallel), then it drives out to the rally point.
/// </summary>
public sealed record ServiceSpec(TagSet Tags, int ReloadTicks, int RepairTicks, int CargoTicks);

/// <summary>
/// A drop zone (content <c>dropZone</c>; logistics): a circle of <see cref="Radius"/> whose centre lies (<see cref="OffsetX"/>,
/// <see cref="OffsetY"/>) tenths of a cell from the structure's footprint centre in its own frame (turned with it). A pallet that
/// lands inside it adds its supply to the structure's stock (<see cref="Sim.World.DropZoneOf"/>).
/// </summary>
public sealed record DropZoneSpec(int OffsetX, int OffsetY, Fixed Radius);

/// <summary>
/// A placement rule (content <c>awayFrom</c>; logistics: the forward airbase): its footprint may not come within
/// <see cref="Cells"/> of the footprint of any of its side's structures (finished or sites) of <see cref="Archetypes"/>.
/// </summary>
public sealed record AwayFromSpec(IReadOnlyList<int> Archetypes, int Cells);

/// <summary>
/// An airlifter's vehicle hold (content <c>airlift</c>; the light tank night, 2026-10-24): <see cref="Seats"/> vehicles with one of
/// <see cref="Tags"/>, boarded one at a time up the ramp (<see cref="BoardTicks"/> each, from within <see cref="BoardRange"/> of the
/// ramp point <see cref="Ramp"/> behind its centre) while it is parked at an airfield of its side with unlimited supply, and let out
/// one every <see cref="UnloadTicks"/> where it delivers. It carries vehicles or cargo pallets, never both.
/// </summary>
/// <param name="Seats">How many vehicles it carries.</param>
/// <param name="Tags">The tags a vehicle needs (any of them) to board.</param>
/// <param name="Ramp">The ramp point's distance behind its centre (along its tail).</param>
/// <param name="BoardRange">How near the ramp point a vehicle starts up the ramp.</param>
/// <param name="BoardTicks">Ticks one vehicle takes to board.</param>
/// <param name="UnloadTicks">Ticks between vehicles driving off (the first after the ramp opens).</param>
public sealed record AirliftSpec(int Seats, TagSet Tags, Fixed Ramp, Fixed BoardRange, int BoardTicks, int UnloadTicks);

/// <summary>Where a hit lands on a unit with <see cref="DirectionalArmor"/>.</summary>
public enum HitFacing : byte
{
    /// <summary>The front arc.</summary>
    Front,

    /// <summary>Either side.</summary>
    Side,

    /// <summary>The rear arc.</summary>
    Rear,

    /// <summary>From above (top attack: drones, bombs, artillery and mortar rounds, aircraft).</summary>
    Top,
}

/// <summary>
/// Directional armour (the tank's; content <c>directionalArmor</c>): the percent of a hit's damage the unit takes by where it
/// lands. A hit from above (top attack) takes <see cref="Top"/>; otherwise the direction it comes from, against the hull's
/// facing, is front within <see cref="FrontHalf"/> facings of the nose, rear within <see cref="RearHalf"/> of the tail, else
/// side. Integer: percents and facing counts.
/// </summary>
/// <param name="Front">Percent taken on the front.</param>
/// <param name="Side">Percent taken on a side (and by a hit whose direction is unknown).</param>
/// <param name="Rear">Percent taken on the rear.</param>
/// <param name="Top">Percent taken from above.</param>
/// <param name="FrontHalf">Facings (11.25 degrees each) either side of the nose that are front.</param>
/// <param name="RearHalf">Facings either side of the tail that are rear.</param>
public sealed record DirectionalArmor(int Front, int Side, int Rear, int Top, int FrontHalf, int RearHalf)
{
    /// <summary>The percent for a hit on facing f.</summary>
    public int PercentOf(HitFacing f) => f switch
    {
        HitFacing.Front => Front,
        HitFacing.Rear => Rear,
        HitFacing.Top => Top,
        _ => Side,
    };
}
