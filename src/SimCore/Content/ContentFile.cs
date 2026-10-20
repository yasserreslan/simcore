// Reads content files (JSON) and compiles them into a SimCore.Model.ContentSet.
//
// This namespace does IO and runs once before a match, never inside a tick,
// so it is exempt from the determinism rules. Its output must not depend on
// file order: every collection is sorted by ID with ordinal comparison.
using System.Text.Json.Serialization;

namespace SimCore.Content;

// ContentFile is one JSON file. A file may define any mix of kinds, one
// definition or many.
internal sealed class ContentFile
{
    // ArmorClasses names the columns of the damage table.
    [JsonPropertyName("armorClasses")]
    public List<string>? ArmorClasses { get; set; }

    [JsonPropertyName("warheads")]
    public List<WarheadJson>? Warheads { get; set; }

    [JsonPropertyName("archetypes")]
    public List<ArchetypeJson>? Archetypes { get; set; }

    [JsonPropertyName("components")]
    public List<ComponentJson>? Components { get; set; }

    [JsonPropertyName("factions")]
    public List<FactionJson>? Factions { get; set; }

    // UnitAi: the unit AI numbers (at most one file may carry it); see Model.UnitAiRules.
    [JsonPropertyName("unitAi")]
    public UnitAiJson? UnitAi { get; set; }

    // AirfieldTypes: airfield types (the grid, the classes served, the pieces); see AirfieldTypeJson.
    [JsonPropertyName("airfieldTypes")]
    public List<AirfieldTypeJson>? AirfieldTypes { get; set; }

    // AirClasses: the air classes (E5), in order: a class's value is its position (1-based) across the
    // files in file-name order. See AirClassJson.
    [JsonPropertyName("airClasses")]
    public List<AirClassJson>? AirClasses { get; set; }
}

// AirClassJson: one air class: what air defence and air-to-air weapons engage a target as.
internal sealed class AirClassJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    // Kind: "projectile" (a weapon's projectiles: weapon.interceptible) or "aircraft" (a flying unit:
    // flight.interceptible).
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }
}

// SignatureJson: a target's signature, percents of a normal one (each 0 for 100).
internal sealed class SignatureJson
{
    [JsonPropertyName("detect")]
    public int Detect { get; set; }

    [JsonPropertyName("lock")]
    public int Lock { get; set; }

    [JsonPropertyName("kill")]
    public int Kill { get; set; }
}

// CountermeasureJson: a dispenser (flares or chaff); see Model.Countermeasure.
internal sealed class CountermeasureJson
{
    // Kind: "flare" or "chaff".
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("salvos")]
    public int Salvos { get; set; }

    // Vs: the seeker it decoys, "ir" or "radar".
    [JsonPropertyName("vs")]
    public string? Vs { get; set; }

    [JsonPropertyName("pk")]
    public int Pk { get; set; }

    [JsonPropertyName("window")]
    public int Window { get; set; }

    [JsonPropertyName("perMissile")]
    public int PerMissile { get; set; }

    [JsonPropertyName("rearmTicks")]
    public int RearmTicks { get; set; }
}

// EvadeJson: a fixed wing's break on a launch warning (E6); see Model.Evade.
internal sealed class EvadeJson
{
    [JsonPropertyName("breakG")]
    public int BreakG { get; set; }

    [JsonPropertyName("warn")]
    public int Warn { get; set; }

    [JsonPropertyName("maxTicks")]
    public int MaxTicks { get; set; }
}

// LossJson: what happens when an aircraft is lost in the air (E10); see Model.LossSpec.
internal sealed class LossJson
{
    // Crash: the wreck's fall and impact (see CrashJson).
    [JsonPropertyName("crash")]
    public CrashJson? Crash { get; set; }

    // Eject: the pilot's ejection on a gun kill (see EjectJson).
    [JsonPropertyName("eject")]
    public EjectJson? Eject { get; set; }
}

// CrashJson: a wreck's crash path and impact (E10); see Model.CrashSpec. Damage in HP, splash in tenths
// of a cell, gravity in tenths of a metre a second squared, drag and break-up in percent.
internal sealed class CrashJson
{
    [JsonPropertyName("warhead")]
    public string? Warhead { get; set; }

    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("splash")]
    public int Splash { get; set; }

    [JsonPropertyName("gravity")]
    public int Gravity { get; set; }

    [JsonPropertyName("drag")]
    public int Drag { get; set; }

    [JsonPropertyName("breakUp")]
    public int BreakUp { get; set; }

    [JsonPropertyName("breakUpDrag")]
    public int BreakUpDrag { get; set; }

    // Runway: what the impact does to a runway it lands on (see RunwayEffectJson); absent: nothing.
    [JsonPropertyName("runway")]
    public RunwayEffectJson? Runway { get; set; }
}

// EjectJson: the pilot's ejection on a gun kill (E10); see Model.EjectSpec. Heights in tenths of a metre,
// rates in tenths of a metre a second, drift in tenths of a cell a second, times in ticks.
internal sealed class EjectJson
{
    // Unit: the archetype he becomes on landing (the pilot).
    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("seatRise")]
    public int SeatRise { get; set; }

    [JsonPropertyName("riseTicks")]
    public int RiseTicks { get; set; }

    [JsonPropertyName("chuteTicks")]
    public int ChuteTicks { get; set; }

    [JsonPropertyName("chuteRate")]
    public int ChuteRate { get; set; }

    [JsonPropertyName("drift")]
    public int Drift { get; set; }

    [JsonPropertyName("driftTicks")]
    public int DriftTicks { get; set; }
}

// AirBodyJson: an Air-layer unit without a flight that is an air target (E6: the FPV drone); see Model.AirBodySpec.
internal sealed class AirBodyJson
{
    // Class: its air class (an "aircraft" kind).
    [JsonPropertyName("class")]
    public string? Class { get; set; }

    // Height: how high it flies over the ground, tenths of a metre.
    [JsonPropertyName("height")]
    public int Height { get; set; }
}

// UnitAiJson: every field optional (0 keeps the default); distances in tenths of a cell, times in ticks.
internal sealed class UnitAiJson
{
    [JsonPropertyName("guardInnerPercent")]
    public int GuardInnerPercent { get; set; }

    [JsonPropertyName("guardOuterPercent")]
    public int GuardOuterPercent { get; set; }

    [JsonPropertyName("aggressivePercent")]
    public int AggressivePercent { get; set; }

    [JsonPropertyName("chaseTicks")]
    public int ChaseTicks { get; set; }

    [JsonPropertyName("scanTicks")]
    public int ScanTicks { get; set; }

    [JsonPropertyName("returnScanTicks")]
    public int ReturnScanTicks { get; set; }

    [JsonPropertyName("retaliateMax")]
    public int RetaliateMax { get; set; }

    [JsonPropertyName("retaliateFriends")]
    public int RetaliateFriends { get; set; }

    [JsonPropertyName("retaliateCooldownTicks")]
    public int RetaliateCooldownTicks { get; set; }
}

internal sealed class WarheadJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    // Versus maps every armor class to the percent of damage dealt, or to
    // null when the warhead cannot target that class.
    [JsonPropertyName("versus")]
    public Dictionary<string, int?>? Versus { get; set; }

    // MaxHpPercent: for these armor classes a hit takes this percent of the target's max HP,
    // whatever the damage and the distance (a drone's charge on a building: 20%).
    [JsonPropertyName("maxHpPercent")]
    public Dictionary<string, int>? MaxHpPercent { get; set; }
}

internal sealed class FootprintJson
{
    [JsonPropertyName("w")]
    public int W { get; set; }

    [JsonPropertyName("h")]
    public int H { get; set; }
}

internal sealed class ArchetypeJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("glyph")]
    public string? Glyph { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("locomotor")]
    public string? Locomotor { get; set; }

    [JsonPropertyName("armor")]
    public string? Armor { get; set; }

    [JsonPropertyName("hp")]
    public int Hp { get; set; }

    // Speed is in tenths of a cell per second.
    [JsonPropertyName("speed")]
    public int Speed { get; set; }

    [JsonPropertyName("turnRate")]
    public int TurnRate { get; set; }

    [JsonPropertyName("turret")]
    public bool Turret { get; set; }

    // Radius is the projectile collision radius in tenths of a cell; 0 picks a default by locomotor
    // (foot 3, flying 2, none 2, wheeled and tracked 8). Structures collide with their footprint.
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    // WeaponMode is "auto" (default) or "primaryOnly".
    [JsonPropertyName("weaponMode")]
    public string? WeaponMode { get; set; }

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("cost")]
    public int Cost { get; set; }

    // Vision and Detect are in tenths of a cell.
    [JsonPropertyName("vision")]
    public int Vision { get; set; }

    [JsonPropertyName("detect")]
    public int Detect { get; set; }

    // AirVision: tenths of a cell (slant) within which it detects air targets; 0 for none.
    [JsonPropertyName("airVision")]
    public int AirVision { get; set; }

    // Stance: the stance its units start in: "guard" (default), "aggressive", "holdGround",
    // "returnFire" or "holdFire".
    [JsonPropertyName("stance")]
    public string? Stance { get; set; }

    // Pursue: whether it moves on its own to retaliate, chase and guard (default: see Archetype.Pursue).
    [JsonPropertyName("pursue")]
    public bool? Pursue { get; set; }

    // Leash and RetaliateMax: tenths of a cell; 0 for the unitAi defaults.
    [JsonPropertyName("leash")]
    public int Leash { get; set; }

    [JsonPropertyName("retaliateMax")]
    public int RetaliateMax { get; set; }

    // DeployTicks and StowTicks: ticks to set up before firing and to pack up before moving (0: none).
    [JsonPropertyName("deployTicks")]
    public int DeployTicks { get; set; }

    [JsonPropertyName("stowTicks")]
    public int StowTicks { get; set; }

    // Crusher and Crushable: a moving vehicle kills an enemy it drives into when its crusher level
    // is above the victim's crushable level (0: never). Infantry 1, trucks and the carrier 2.
    [JsonPropertyName("crusher")]
    public int Crusher { get; set; }

    [JsonPropertyName("crushable")]
    public int Crushable { get; set; }

    // AutoFireDeployed: a unit that deploys picks targets on its own once set up (the mortar); it
    // never sets up by itself. Without it, units that deploy fire on orders only (launchers).
    [JsonPropertyName("autoFireDeployed")]
    public bool AutoFireDeployed { get; set; }

    // AccelTicks: ticks a vehicle takes to reach its top speed from a stop (0: the default, 5).
    [JsonPropertyName("accelTicks")]
    public int AccelTicks { get; set; }

    // Lifetime is in ticks; 0 lives forever.
    [JsonPropertyName("lifetime")]
    public int Lifetime { get; set; }

    // ExpireWhenEmpty: ticks it lasts once its weapons are empty (a single-use drone station); 0 never.
    [JsonPropertyName("expireWhenEmpty")]
    public int ExpireWhenEmpty { get; set; }

    // Height in tenths of a metre: how high what flies down at it is stopped (a drone's dive);
    // 0 for the default (structures 3 m, infantry 1.8 m, anything else 2.5 m).
    [JsonPropertyName("height")]
    public int Height { get; set; }

    // Flight: a scout drone's flight (see FlightJson); flying archetypes only.
    [JsonPropertyName("flight")]
    public FlightJson? Flight { get; set; }

    // Mission: its mission-layer numbers (see MissionJson); flying archetypes with a flight only.
    [JsonPropertyName("mission")]
    public MissionJson? Mission { get; set; }

    // Signature: its signature as an air target (see SignatureJson); flying archetypes with a flight only.
    [JsonPropertyName("signature")]
    public SignatureJson? Signature { get; set; }

    // Countermeasures: its dispensers (see CountermeasureJson); flying archetypes with a flight only.
    [JsonPropertyName("countermeasures")]
    public List<CountermeasureJson>? Countermeasures { get; set; }

    // Evade: its break on a launch warning (see EvadeJson); fixed-wing flights only.
    [JsonPropertyName("evade")]
    public EvadeJson? Evade { get; set; }

    // AirBody: an air target without a flight block (see AirBodyJson); flying archetypes without a flight only.
    [JsonPropertyName("airBody")]
    public AirBodyJson? AirBody { get; set; }

    // Loss: what happens when it is lost in the air (see LossJson; E10); flying archetypes with a flight only.
    [JsonPropertyName("loss")]
    public LossJson? Loss { get; set; }

    // Slots maps a slot name (primary, secondary) to the components a preset
    // may pick for it.
    [JsonPropertyName("slots")]
    public Dictionary<string, List<string>>? Slots { get; set; }

    // Components are carried by every unit of the archetype.
    [JsonPropertyName("components")]
    public List<string>? Components { get; set; }

    [JsonPropertyName("crew")]
    public CrewJson? Crew { get; set; }

    [JsonPropertyName("garrison")]
    public GarrisonJson? Garrison { get; set; }

    // HighGround: vision from high ground (see HighGroundJson).
    [JsonPropertyName("highGround")]
    public HighGroundJson? HighGround { get; set; }

    // Production: what this structure produces and how (see ProductionJson).
    [JsonPropertyName("production")]
    public ProductionJson? Production { get; set; }

    // Construction: how a site of this structure is built (see ConstructionJson).
    [JsonPropertyName("construction")]
    public ConstructionJson? Construction { get; set; }

    // Tunnel: a tunnel entrance (see TunnelJson); structures only.
    [JsonPropertyName("tunnel")]
    public TunnelJson? Tunnel { get; set; }

    // TunnelSize: its size underground, 1 infantry, 2 light and medium vehicles, 3 heavy; 0 (the
    // default for anything not on foot) never goes in. Foot units default to 1.
    [JsonPropertyName("tunnelSize")]
    public int TunnelSize { get; set; }

    // TunnelSlots: the slots it takes in a tunnel network (default 1 on foot).
    [JsonPropertyName("tunnelSlots")]
    public int TunnelSlots { get; set; }

    [JsonPropertyName("footprint")]
    public FootprintJson? Footprint { get; set; }

    // BuildTime is in ticks.
    [JsonPropertyName("buildTime")]
    public int BuildTime { get; set; }

    // Aircraft: an aircraft's airfield data (see AircraftJson); flying archetypes only.
    [JsonPropertyName("aircraft")]
    public AircraftJson? Aircraft { get; set; }

    // Airfield: its role in an airfield type (see AirfieldPieceJson); structures only.
    [JsonPropertyName("airfield")]
    public AirfieldPieceJson? Airfield { get; set; }

    // Pavement: blocks no routing or movement (ground units drive across; road on terrain), still
    // blocks other structures.
    [JsonPropertyName("pavement")]
    public bool Pavement { get; set; }

    // Targetable: false for what is never a target (attack orders, automatic targeting); default true.
    [JsonPropertyName("targetable")]
    public bool? Targetable { get; set; }

    // Invulnerable: never damaged.
    [JsonPropertyName("invulnerable")]
    public bool Invulnerable { get; set; }

    // Rubble: at 0 HP a standing piece becomes rubble at hpPercent of its max HP.
    [JsonPropertyName("rubble")]
    public RubbleJson? Rubble { get; set; }

    // Repairable: workers repair it; one worker restores the whole bar in fullPercentOfBuild
    // percent of the build time.
    [JsonPropertyName("repairable")]
    public RepairableJson? Repairable { get; set; }
}

internal sealed class RubbleJson
{
    [JsonPropertyName("hpPercent")]
    public int HpPercent { get; set; }
}

internal sealed class RepairableJson
{
    [JsonPropertyName("fullPercentOfBuild")]
    public int FullPercentOfBuild { get; set; }
}

// The generic aircraft block (the game repo's docs/airfield-design.md, section 1.2). Distances in
// tenths of a cell, heights in tenths of a metre, speeds in tenths of a cell per second,
// accelerations in tenths of a cell per second per second, times in ticks.
internal sealed class AircraftJson
{
    // SizeClass: "S", "M", "L" or "XL".
    [JsonPropertyName("sizeClass")]
    public string? SizeClass { get; set; }

    [JsonPropertyName("span")]
    public int Span { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }

    // Landing: "runway" or "vertical".
    [JsonPropertyName("landing")]
    public string? Landing { get; set; }

    // GroundArmor: the armor class it takes while on the ground at an airfield (none: its own).
    [JsonPropertyName("groundArmor")]
    public string? GroundArmor { get; set; }

    [JsonPropertyName("minLandRun")]
    public int MinLandRun { get; set; }

    [JsonPropertyName("minTakeoffRun")]
    public int MinTakeoffRun { get; set; }

    [JsonPropertyName("approach")]
    public ApproachJson? Approach { get; set; }

    [JsonPropertyName("speeds")]
    public AircraftSpeedsJson? Speeds { get; set; }

    [JsonPropertyName("service")]
    public AircraftServiceJson? Service { get; set; }

    [JsonPropertyName("groundVision")]
    public int GroundVision { get; set; }

    // GroundEye: eye height on the ground, tenths of a metre.
    [JsonPropertyName("groundEye")]
    public int GroundEye { get; set; }

    // HoldReserve: ticks of endurance kept for the approach and holding when home is an airfield.
    [JsonPropertyName("holdReserve")]
    public int HoldReserve { get; set; }

    [JsonPropertyName("sortie")]
    public bool Sortie { get; set; }

    // CorridorNeed: tenths of a cell beyond the runway's approach end it needs on the map (E9); 0: the type's.
    [JsonPropertyName("corridorNeed")]
    public int CorridorNeed { get; set; }
}

// The approach profile: the final fix (distance out, height), the touchdown point past the
// threshold, the flare, roll-out deceleration and hard braking. The glide slope is the fix height
// over the distance from the fix to the touchdown point.
internal sealed class ApproachJson
{
    [JsonPropertyName("fixDistance")]
    public int FixDistance { get; set; }

    [JsonPropertyName("fixHeight")]
    public int FixHeight { get; set; }

    [JsonPropertyName("touchdown")]
    public int Touchdown { get; set; }

    [JsonPropertyName("flare")]
    public int Flare { get; set; }

    [JsonPropertyName("rolloutDecel")]
    public int RolloutDecel { get; set; }

    [JsonPropertyName("brake")]
    public int Brake { get; set; }

    // Pattern (E9): "straightIn" (default) or "overhead"; the overhead's initial point (tenths of a cell
    // beyond the approach end), pattern height (tenths of a metre) and break speed (tenths of a cell a second).
    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    [JsonPropertyName("initialDistance")]
    public int InitialDistance { get; set; }

    [JsonPropertyName("patternHeight")]
    public int PatternHeight { get; set; }

    [JsonPropertyName("breakSpeed")]
    public int BreakSpeed { get; set; }

    // AerobrakeSpeed: after touchdown the nose stays up until this speed (tenths of a cell a second); 0: none.
    [JsonPropertyName("aerobrakeSpeed")]
    public int AerobrakeSpeed { get; set; }
}

internal sealed class AircraftSpeedsJson
{
    [JsonPropertyName("approach")]
    public int Approach { get; set; }

    [JsonPropertyName("touchdown")]
    public int Touchdown { get; set; }

    [JsonPropertyName("taxi")]
    public int Taxi { get; set; }

    [JsonPropertyName("turn")]
    public int Turn { get; set; }

    [JsonPropertyName("pushback")]
    public int Pushback { get; set; }

    [JsonPropertyName("takeoff")]
    public int Takeoff { get; set; }

    [JsonPropertyName("takeoffAccel")]
    public int TakeoffAccel { get; set; }

    // MilitaryAccel: the takeoff roll's acceleration on dry power (E9); 0: takeoffAccel is the only one.
    [JsonPropertyName("militaryAccel")]
    public int MilitaryAccel { get; set; }
}

// Service needs at 1x: a full tank, one round, HP a second.
internal sealed class AircraftServiceJson
{
    [JsonPropertyName("refuelTicks")]
    public int RefuelTicks { get; set; }

    [JsonPropertyName("rearmTicksPerRound")]
    public int RearmTicksPerRound { get; set; }

    [JsonPropertyName("repairHpPerSecond")]
    public int RepairHpPerSecond { get; set; }
}

// An archetype's role in an airfield type: "facility", "row", "segment" or "station". Stations
// also give their kind ("loading" or "normal"), whether they need the facility standing, their
// service percents and the widest span they take (tenths of a cell; 0 for any).
internal sealed class AirfieldPieceJson
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("needsFacility")]
    public bool NeedsFacility { get; set; }

    [JsonPropertyName("rearm")]
    public int Rearm { get; set; }

    [JsonPropertyName("repair")]
    public int Repair { get; set; }

    [JsonPropertyName("refuel")]
    public int Refuel { get; set; }

    [JsonPropertyName("maxSpan")]
    public int MaxSpan { get; set; }
}

// An airfield type (the game repo's docs/airfield-design.md). Grid sizes in tenths of a cell, whole
// cells only; heights in tenths of a metre; times in ticks.
internal sealed class AirfieldTypeJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // MaxClass: the largest aircraft class it serves ("S", "M", "L", "XL"); it serves every smaller one.
    [JsonPropertyName("maxClass")]
    public string? MaxClass { get; set; }

    [JsonPropertyName("pieces")]
    public AirfieldPiecesJson? Pieces { get; set; }

    [JsonPropertyName("station")]
    public int Station { get; set; }

    [JsonPropertyName("lane")]
    public int Lane { get; set; }

    [JsonPropertyName("runway")]
    public AirfieldRunwayJson? Runway { get; set; }

    [JsonPropertyName("facilityGap")]
    public int FacilityGap { get; set; }

    // MaxRelief: the largest height difference (tenths of a metre) of a piece's ground from the pad.
    [JsonPropertyName("maxRelief")]
    public int MaxRelief { get; set; }

    [JsonPropertyName("rowSlots")]
    public List<RowSlotJson>? RowSlots { get; set; }

    [JsonPropertyName("holding")]
    public HoldingJson? Holding { get; set; }

    [JsonPropertyName("lockRelease")]
    public int LockRelease { get; set; }

    [JsonPropertyName("taxiWait")]
    public int TaxiWait { get; set; }

    [JsonPropertyName("collapseDamage")]
    public int CollapseDamage { get; set; }

    // CraterHitDamage: percent of max HP an aircraft loses running into a crater it could not stop short of.
    [JsonPropertyName("craterHitDamage")]
    public int CraterHitDamage { get; set; }

    [JsonPropertyName("craters")]
    public CratersJson? Craters { get; set; }
}

internal sealed class AirfieldPiecesJson
{
    [JsonPropertyName("facility")]
    public string? Facility { get; set; }

    [JsonPropertyName("row")]
    public string? Row { get; set; }

    [JsonPropertyName("segment")]
    public string? Segment { get; set; }

    [JsonPropertyName("loading")]
    public string? Loading { get; set; }

    [JsonPropertyName("normal")]
    public string? Normal { get; set; }
}

internal sealed class AirfieldRunwayJson
{
    [JsonPropertyName("segment")]
    public int Segment { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("grass")]
    public int Grass { get; set; }

    [JsonPropertyName("taxiway")]
    public int Taxiway { get; set; }

    [JsonPropertyName("connector")]
    public int Connector { get; set; }

    [JsonPropertyName("minSegments")]
    public int MinSegments { get; set; }

    [JsonPropertyName("corridor")]
    public int Corridor { get; set; }
}

// A row's station slot: kind, left edge (tenths, whole cells), the parked heading (a facing in the
// airfield's frame), pushback or not, and the park-in and park-out paths ([x, y] tenths, row-local).
internal sealed class RowSlotJson
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("heading")]
    public int Heading { get; set; }

    [JsonPropertyName("pushback")]
    public bool Pushback { get; set; }

    [JsonPropertyName("parkIn")]
    public List<List<int>>? ParkIn { get; set; }

    [JsonPropertyName("parkOut")]
    public List<List<int>>? ParkOut { get; set; }
}

internal sealed class HoldingJson
{
    [JsonPropertyName("distance")]
    public int Distance { get; set; }

    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("step")]
    public int Step { get; set; }

    [JsonPropertyName("places")]
    public int Places { get; set; }
}

internal sealed class CraterJson
{
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("repairTicks")]
    public int RepairTicks { get; set; }
}

internal sealed class CratersJson
{
    [JsonPropertyName("small")]
    public CraterJson? Small { get; set; }

    [JsonPropertyName("large")]
    public CraterJson? Large { get; set; }

    [JsonPropertyName("margin")]
    public int Margin { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }

    [JsonPropertyName("wearPatch")]
    public int WearPatch { get; set; }

    [JsonPropertyName("wearToCrater")]
    public int WearToCrater { get; set; }

    [JsonPropertyName("neighbourShare")]
    public int NeighbourShare { get; set; }

    [JsonPropertyName("wearRepairPerSecond")]
    public int WearRepairPerSecond { get; set; }
}

// A scout drone's flight (docs: the game repo's docs/air-defense-design.md, section 10). Heights in
// tenths of a metre, distances in tenths of a cell, times in ticks.
internal sealed class FlightJson
{
    // Height: how high it flies above the ground under and just ahead of it.
    [JsonPropertyName("height")]
    public int Height { get; set; }

    // ClimbRate: tenths of a metre a second it climbs or sinks toward that height.
    [JsonPropertyName("climbRate")]
    public int ClimbRate { get; set; }

    // LaunchHeight: tenths of a metre above the launcher's ground it leaves the catapult at.
    [JsonPropertyName("launchHeight")]
    public int LaunchHeight { get; set; }

    // OrbitRadius: the circle it flies around the point it is sent to.
    [JsonPropertyName("orbitRadius")]
    public int OrbitRadius { get; set; }

    // TurnRate: degrees a second it can turn.
    [JsonPropertyName("turnRate")]
    public int TurnRate { get; set; }

    // Interceptible: the class air defence engages it as ("scout", "watcher" for a strike drone,
    // "overseer" for a surveillance drone).
    [JsonPropertyName("interceptible")]
    public string? Interceptible { get; set; }

    // Reserve: ticks of endurance to spare when it turns for home.
    [JsonPropertyName("reserve")]
    public int Reserve { get; set; }

    // RecoverRange: how close to its launcher it is recovered.
    [JsonPropertyName("recoverRange")]
    public int RecoverRange { get; set; }

    // Turnaround: ticks a launcher needs after a recovery before the next launch (refuel, checks).
    [JsonPropertyName("turnaround")]
    public int Turnaround { get; set; }

    // HomeWhenEmpty: deprecated (2026-10-19, the owner's flight rules: an aircraft never goes home
    // because it is empty; it rearms only on a Rearm order). Still accepted; the engine ignores it.
    [JsonPropertyName("homeWhenEmpty")]
    public bool HomeWhenEmpty { get; set; }

    // Relaunch: its launcher sends it up again, to where it orbited, once the turnaround is over.
    [JsonPropertyName("relaunch")]
    public bool Relaunch { get; set; }

    // EdgeMargin: how far from the map's edge (tenths of a cell) it starts turning back (the mission
    // layer's edge avoidance); at least twice its turn radius plus a second of travel. 0: that minimum.
    [JsonPropertyName("edgeMargin")]
    public int EdgeMargin { get; set; }

    // Kind: its kinematics, "drone" (the default: constant speed, turning at turnRate) or "fixedWing"
    // (an energy envelope: the fixedWing block; turnRate is then derived and must be left out).
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    // FixedWing: the envelope of a fixed-wing flight (kind "fixedWing").
    [JsonPropertyName("fixedWing")]
    public FixedWingJson? FixedWing { get; set; }
}

// FixedWingJson: a fixed-wing aircraft's energy envelope (E4). Speeds in tenths of a cell a second
// (its cruise speed is the archetype's speed), radii in tenths of a cell, accelerations in tenths
// of a cell a second squared, heights in tenths of a metre, rates in tenths of a metre a second.
internal sealed class FixedWingJson
{
    // Dash: its top speed, reached level on afterburner (without one: its attack speed).
    [JsonPropertyName("dash")]
    public int Dash { get; set; }

    // Attack: the speed full dry power (military) holds level; flown on a strike order.
    [JsonPropertyName("attack")]
    public int Attack { get; set; }

    // MinSpeed: the stall: it never flies slower, and at it it cannot climb.
    [JsonPropertyName("minSpeed")]
    public int MinSpeed { get; set; }

    // CornerSpeed: the slowest speed at which it pulls its full g (its turn rate peaks there).
    [JsonPropertyName("cornerSpeed")]
    public int CornerSpeed { get; set; }

    // CornerRadius: its tightest turn, at corner speed (below it the radius stays this).
    [JsonPropertyName("cornerRadius")]
    public int CornerRadius { get; set; }

    // SustainedRadius: the level turn military power holds at cruise speed without slowing (at
    // least its max-g radius at cruise speed).
    [JsonPropertyName("sustainedRadius")]
    public int SustainedRadius { get; set; }

    // Accel: the afterburner's thrust (the drag at dash speed); without an afterburner, military
    // power's thrust at dash speed would be this.
    [JsonPropertyName("accel")]
    public int Accel { get; set; }

    // Gravity: the energy exchange, the deceleration of a vertical climb (a dive gains as much).
    [JsonPropertyName("gravity")]
    public int Gravity { get; set; }

    // DiveRate: the most it sinks (tenths of a metre a second); it climbs at the flight's climbRate.
    [JsonPropertyName("diveRate")]
    public int DiveRate { get; set; }

    // Floor: the least height over the ground under it (tenths of a metre).
    [JsonPropertyName("floor")]
    public int Floor { get; set; }

    // IngressHeight: its height over the ground on a strike order (tenths of a metre); 0 keeps its flight height.
    [JsonPropertyName("ingressHeight")]
    public int IngressHeight { get; set; }

    // LookAhead: ticks of its velocity ahead whose ground counts for its height (40: 2 s).
    [JsonPropertyName("lookAhead")]
    public int LookAhead { get; set; }

    // Separation: tenths of a metre its height steps up by ID (ID mod 5 steps) so wingmen never overlap.
    [JsonPropertyName("separation")]
    public int Separation { get; set; }

    // Burn: fuel burn by throttle, percent of nominal (cruise 100: one unit of lifetime a tick).
    [JsonPropertyName("burn")]
    public BurnJson? Burn { get; set; }
}

// BurnJson: fuel burn by throttle, percent of nominal; afterburner 0: it has none.
internal sealed class BurnJson
{
    [JsonPropertyName("idle")]
    public int Idle { get; set; }

    [JsonPropertyName("cruise")]
    public int Cruise { get; set; }

    [JsonPropertyName("military")]
    public int Military { get; set; }

    [JsonPropertyName("afterburner")]
    public int Afterburner { get; set; }
}

// MissionJson: an aircraft's mission-layer numbers (Guard, the one automatic mode).
internal sealed class MissionJson
{
    // HuntLeash: how far (tenths of a cell) beyond its guarded area or route it chases a target.
    [JsonPropertyName("huntLeash")]
    public int HuntLeash { get; set; }

    // Guard: the target kinds it engages on Guard when the order names none ("aircraft", "drones",
    // "vehicles", "infantry", "structures").
    [JsonPropertyName("guard")]
    public List<string>? Guard { get; set; }
}

// CrewJson is a vehicle's driver seat. EnterRange is in tenths of a cell;
// Survival and ExitDamage are percents.
// Garrison: a building soldiers can enter. Ranges in tenths of a cell; percents 0-100.
internal sealed class ProductionJson
{
    // Tags: it produces the faction's archetypes with one of these tags (vehicles).
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    // Bays: how many it produces at once (a finished one takes any free door).
    [JsonPropertyName("bays")]
    public int Bays { get; set; }

    // QueueMax: the most orders waiting and in production together.
    [JsonPropertyName("queueMax")]
    public int QueueMax { get; set; }

    // DoorTicks: ticks a door takes to open, and to close.
    [JsonPropertyName("doorTicks")]
    public int DoorTicks { get; set; }

    // PushAfter: ticks a finished vehicle waits for its exit lane before it goes anyway.
    [JsonPropertyName("pushAfter")]
    public int PushAfter { get; set; }

    // Crew: the preset (or archetype) that drives what it produces.
    [JsonPropertyName("crew")]
    public string? Crew { get; set; }

    // Exits: one per door, where its units come out: [x, y] in tenths of a cell from the
    // footprint's top-left corner (the front faces south, +y). Default: one door per bay, spread
    // along the front, 1.5 cells out.
    [JsonPropertyName("exits")]
    public List<List<int>>? Exits { get; set; }

    // BayPoints: one per door, where its units appear inside (the bay floor behind the door), in
    // the same units; they drive from there through the door. Default: 4 cells inside the front.
    [JsonPropertyName("bayPoints")]
    public List<List<int>>? BayPoints { get; set; }

    // Rally: the default rally point, [x, y] in tenths of a cell from the top-left corner.
    [JsonPropertyName("rally")]
    public List<int>? Rally { get; set; }

    // MinHpPercent: below this share of its HP it produces nothing (its doors are wreckage).
    [JsonPropertyName("minHpPercent")]
    public int MinHpPercent { get; set; }

    // IntoStation: an airfield facility's production (E8): each finished aircraft is parked into a
    // free station of its airfield instead of driving out of a door (doorTicks, pushAfter, crew,
    // exits and bayPoints do not apply).
    [JsonPropertyName("intoStation")]
    public bool IntoStation { get; set; }
}

// A tunnel entrance (docs/design.md, "Tunnels"). Distances in tenths of a cell, speed in tenths of
// a cell per second, heights in tenths of a metre, times in ticks, percents 0-100.
internal sealed class TunnelJson
{
    // Size: the largest unit size that goes in and comes out here (1 infantry, 2 medium, 3 heavy).
    [JsonPropertyName("size")]
    public int Size { get; set; }

    // Capacity: slots it adds to its network.
    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    // Throughput: ticks between two units passing its door, in or out.
    [JsonPropertyName("throughput")]
    public int Throughput { get; set; }

    // RubbleHp: the HP of what is left of it once destroyed.
    [JsonPropertyName("rubbleHp")]
    public int RubbleHp { get; set; }

    // Link: entrances within this distance link up (the smaller of the two ranges).
    [JsonPropertyName("link")]
    public int Link { get; set; }

    // Spacing: the closest another entrance may be built, centre to centre.
    [JsonPropertyName("spacing")]
    public int Spacing { get; set; }

    // Speed: travel underground.
    [JsonPropertyName("speed")]
    public int Speed { get; set; }

    // ExposeTicks: how long a unit passing its door reveals it to enemies that see it.
    [JsonPropertyName("exposeTicks")]
    public int ExposeTicks { get; set; }

    // FlatGrade: the steepest grade its footprint and apron may have (0: any walkable ground).
    [JsonPropertyName("flatGrade")]
    public int FlatGrade { get; set; }

    // HillGrade: the rise behind the site from which it is built into the hillside.
    [JsonPropertyName("hillGrade")]
    public int HillGrade { get; set; }

    // Depth: footprint rows under its headwall, from the back.
    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    // Walls: the footprint's outer columns are retaining walls along the ramp.
    [JsonPropertyName("walls")]
    public bool Walls { get; set; }

    // Mound and HillMound: the ground's height over its headwall, standing alone and in a hillside.
    [JsonPropertyName("mound")]
    public int Mound { get; set; }

    [JsonPropertyName("hillMound")]
    public int HillMound { get; set; }

    // Apron: cells of flat ground in front; Blend: cells over which the ground eases back.
    [JsonPropertyName("apron")]
    public int Apron { get; set; }

    [JsonPropertyName("blend")]
    public int Blend { get; set; }

    // EmergeDamage: percent of max HP lost by a unit coming out when it is destroyed.
    [JsonPropertyName("emergeDamage")]
    public int EmergeDamage { get; set; }

    // RestoreTicks: how long the ground takes to return to its old shape once it is gone.
    [JsonPropertyName("restoreTicks")]
    public int RestoreTicks { get; set; }
}

internal sealed class ConstructionJson
{
    // StartHp: percent of max HP a fresh site has; it rises with progress to 100.
    [JsonPropertyName("startHp")]
    public int StartHp { get; set; }

    // Stages: the progress percents where construction stages 1 to 4 begin (strictly increasing).
    [JsonPropertyName("stages")]
    public List<int>? Stages { get; set; }

    // Refund: percent of the cost a cancelled site gives back.
    [JsonPropertyName("refund")]
    public int Refund { get; set; }

    // Helpers: percent of a builder's rate for the 1st, 2nd, 3rd ... worker on a site (the last
    // value for any more).
    [JsonPropertyName("helpers")]
    public List<int>? Helpers { get; set; }

    // PadBlend: on a map with terrain the site's ground is flattened to a pad at its mean
    // height when it is placed, easing back over this many cells.
    [JsonPropertyName("padBlend")]
    public int PadBlend { get; set; }
}

internal sealed class GarrisonJson
{
    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("enterRange")]
    public int EnterRange { get; set; }

    [JsonPropertyName("rangeBonus")]
    public int RangeBonus { get; set; }

    [JsonPropertyName("visionBonus")]
    public int VisionBonus { get; set; }

    [JsonPropertyName("occupantDamage")]
    public int OccupantDamage { get; set; }

    [JsonPropertyName("occupantWarheads")]
    public List<string>? OccupantWarheads { get; set; }

    [JsonPropertyName("stages")]
    public List<int>? Stages { get; set; }

    [JsonPropertyName("stageCapacity")]
    public List<int>? StageCapacity { get; set; }

    [JsonPropertyName("ejectDamage")]
    public int EjectDamage { get; set; }

    [JsonPropertyName("collapseSurvival")]
    public int CollapseSurvival { get; set; }

    [JsonPropertyName("collapseHp")]
    public int CollapseHp { get; set; }

    // Doors: [x, y] cells relative to the footprint's top-left cell.
    [JsonPropertyName("doors")]
    public List<List<int>>? Doors { get; set; }

    // Owned: a post that stays its builder's (only its side enters, never neutral when empty).
    [JsonPropertyName("owned")]
    public bool Owned { get; set; }

    // LauncherTags: occupants with one of these tags make it a launch point for its abilities.
    [JsonPropertyName("launcherTags")]
    public List<string>? LauncherTags { get; set; }

    // DeployTags: occupants that must set up to fire (deployTicks) may fire from inside only with one
    // of these tags (the mortar in an observation post); none: they hold fire inside.
    [JsonPropertyName("deployTags")]
    public List<string>? DeployTags { get; set; }
}

// HighGroundJson: percentPerMetre more vision per metre above the mean ground within radius
// cells, up to maxPercent.
internal sealed class HighGroundJson
{
    [JsonPropertyName("percentPerMetre")]
    public int PercentPerMetre { get; set; }

    [JsonPropertyName("maxPercent")]
    public int MaxPercent { get; set; }

    [JsonPropertyName("radius")]
    public int Radius { get; set; }
}

internal sealed class CrewJson
{
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("enterRange")]
    public int EnterRange { get; set; }

    [JsonPropertyName("survival")]
    public int Survival { get; set; }

    [JsonPropertyName("exitDamage")]
    public int ExitDamage { get; set; }

    // Passengers: seats in the back besides the driver's; PassengerTags: who may ride there.
    [JsonPropertyName("passengers")]
    public int Passengers { get; set; }

    [JsonPropertyName("passengerTags")]
    public List<string>? PassengerTags { get; set; }
}

internal sealed class ComponentJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("placeholder")]
    public bool Placeholder { get; set; }

    [JsonPropertyName("cost")]
    public int Cost { get; set; }

    [JsonPropertyName("requires")]
    public List<string>? Requires { get; set; }

    [JsonPropertyName("effects")]
    public List<EffectJson>? Effects { get; set; }
}

// EffectJson holds exactly one effect, keyed by its kind: {"weapon": {...}}.
internal sealed class EffectJson
{
    [JsonPropertyName("weapon")]
    public WeaponJson? Weapon { get; set; }

    [JsonPropertyName("aura")]
    public AuraJson? Aura { get; set; }

    [JsonPropertyName("placer")]
    public PlacerJson? Placer { get; set; }

    [JsonPropertyName("launch")]
    public LaunchJson? Launch { get; set; }

    [JsonPropertyName("strike")]
    public StrikeJson? Strike { get; set; }

    [JsonPropertyName("supply")]
    public SupplyJson? Supply { get; set; }

    [JsonPropertyName("cargo")]
    public CargoJson? Cargo { get; set; }

    [JsonPropertyName("trigger")]
    public TriggerJson? Trigger { get; set; }

    [JsonPropertyName("builder")]
    public BuilderJson? Builder { get; set; }

    [JsonPropertyName("buildAssist")]
    public BuildAssistJson? BuildAssist { get; set; }

    [JsonPropertyName("repair")]
    public RepairJson? Repair { get; set; }

    [JsonPropertyName("modifier")]
    public ModifierJson? Modifier { get; set; }
}

// Distances (range, radius, splash, scatter, contact) are in tenths of a
// cell, speeds in tenths of a cell per second; times (reload, everyTicks,
// cooldown, salvoInterval) are in ticks.
internal sealed class WeaponJson
{
    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("range")]
    public int Range { get; set; }

    [JsonPropertyName("reload")]
    public int Reload { get; set; }

    [JsonPropertyName("warhead")]
    public string? Warhead { get; set; }

    [JsonPropertyName("splash")]
    public int Splash { get; set; }

    [JsonPropertyName("scatter")]
    public int Scatter { get; set; }

    // ScatterNear: when above zero the scatter grows with the distance, from this at the minimum
    // range (or point blank) to scatter at full range (a mortar's). Tenths of a cell; 0: a flat scatter.
    [JsonPropertyName("scatterNear")]
    public int ScatterNear { get; set; }

    [JsonPropertyName("targets")]
    public List<string>? Targets { get; set; }

    [JsonPropertyName("projectileSpeed")]
    public int ProjectileSpeed { get; set; }

    [JsonPropertyName("salvoSize")]
    public int SalvoSize { get; set; } = 1;

    [JsonPropertyName("salvoInterval")]
    public int SalvoInterval { get; set; }

    // Ammo is in salvos (rockets with ammoPerRocket); 0 is unlimited.
    [JsonPropertyName("ammo")]
    public int Ammo { get; set; }

    // MinRange is in tenths of a cell; 0 is none.
    [JsonPropertyName("minRange")]
    public int MinRange { get; set; }

    [JsonPropertyName("ammoPerRocket")]
    public bool AmmoPerRocket { get; set; }

    // Splash damage in percent of the damage at the centre and at the edge (defaults 100 and 50).
    [JsonPropertyName("splashCenter")]
    public int? SplashCenter { get; set; }

    [JsonPropertyName("splashEdge")]
    public int? SplashEdge { get; set; }

    // A two-ring splash: full damage (splashCenter) within splashInner (tenths of a cell),
    // then a flat splashOuterPercent out to splash.
    [JsonPropertyName("splashInner")]
    public int SplashInner { get; set; }

    [JsonPropertyName("splashOuterPercent")]
    public int SplashOuterPercent { get; set; }

    // AutoRange: per armor class, the farthest (tenths of a cell) it engages such targets on its own.
    [JsonPropertyName("autoRange")]
    public Dictionary<string, int>? AutoRange { get; set; }

    [JsonPropertyName("autoSkipRetreating")]
    public bool AutoSkipRetreating { get; set; }

    // MaxMisses: without orders, stop shooting at a target after this many misses in a row (0: never).
    [JsonPropertyName("maxMisses")]
    public int MaxMisses { get; set; }

    // DepotReload: only a supply depot reloads it, one round per everyTicks, starting afterFireTicks after the last shot.
    [JsonPropertyName("depotReload")]
    public DepotReloadJson? DepotReload { get; set; }

    // AimTicks: ticks the shooter aims, standing still, before every shot.
    [JsonPropertyName("aimTicks")]
    public int AimTicks { get; set; }

    // Arc: a ballistic arc's apex over the line from launch to landing, in percent of the
    // distance (0: by salvo size and range, see TerrainPhysics.ArcRiseRaw).
    [JsonPropertyName("arc")]
    public int Arc { get; set; }

    // Interceptible: the interception class of its projectiles ("ballistic", "drone"), for
    // future interceptors; none by default.
    [JsonPropertyName("interceptible")]
    public string? Interceptible { get; set; }

    // Drone: a one-way attack drone's flight (see DroneJson).
    [JsonPropertyName("drone")]
    public DroneJson? Drone { get; set; }

    // NoResupply: nothing refills it (a single-use drone station).
    [JsonPropertyName("noResupply")]
    public bool NoResupply { get; set; }

    // Guided: a guided missile (see GuidedJson).
    [JsonPropertyName("guided")]
    public GuidedJson? Guided { get; set; }

    // Priority: armor classes in the order the unit picks targets on its own (unlisted come last).
    [JsonPropertyName("priority")]
    public List<string>? Priority { get; set; }

    // Intercept: an air-defence missile (see InterceptJson); needs guided.
    [JsonPropertyName("intercept")]
    public InterceptJson? Intercept { get; set; }

    // AirStrike: an air-to-ground strike missile fired by a flying unit (see AirStrikeJson).
    [JsonPropertyName("airStrike")]
    public AirStrikeJson? AirStrike { get; set; }

    // AirGun: an aircraft's gun against air targets (see AirGunJson; data E8, behaviour E6).
    [JsonPropertyName("airGun")]
    public AirGunJson? AirGun { get; set; }

    // Delivery: how an aircraft delivers this bomb (see DeliveryJson; data E8, behaviour E7).
    [JsonPropertyName("delivery")]
    public DeliveryJson? Delivery { get; set; }

    // Runway: what its blast does to a runway it lands on (see RunwayEffectJson); absent: nothing.
    [JsonPropertyName("runway")]
    public RunwayEffectJson? Runway { get; set; }
}

// RunwayEffectJson: either a crater on impact ("small" or "large") or wear added to the patch it
// lands in (and a share of it to the neighbouring patches), never both.
internal sealed class RunwayEffectJson
{
    [JsonPropertyName("crater")]
    public string? Crater { get; set; }

    [JsonPropertyName("wear")]
    public int Wear { get; set; }
}

// An air-to-ground strike (the Watcher drone's missile; docs: the game repo's docs/watcher-design.md).
// Percents 0 to 100, distances in tenths of a cell.
internal sealed class AirStrikeJson
{
    // HitIdle, HitMoving: the hit chance against a target that stood still, or moved, last tick.
    [JsonPropertyName("hitIdle")]
    public int HitIdle { get; set; } = 100;

    [JsonPropertyName("hitMoving")]
    public int HitMoving { get; set; } = 100;

    // Kills: tags of the targets a hit destroys outright.
    [JsonPropertyName("kills")]
    public List<string>? Kills { get; set; }

    // HeavyTags: tags of the targets a hit destroys only heavyKill percent of the time; a survivor
    // keeps at most heavyHpLeft percent of its max HP.
    [JsonPropertyName("heavyTags")]
    public List<string>? HeavyTags { get; set; }

    [JsonPropertyName("heavyKill")]
    public int HeavyKill { get; set; } = 100;

    [JsonPropertyName("heavyHpLeft")]
    public int HeavyHpLeft { get; set; }

    // MissNear, MissFar: how far from the target a miss lands.
    [JsonPropertyName("missNear")]
    public int MissNear { get; set; }

    [JsonPropertyName("missFar")]
    public int MissFar { get; set; }
}

// An air-defence missile's engagement and kill model (docs: the air defence design). Percents
// 0 to 100, distances in tenths of a cell, speeds in tenths of a cell per second, times in ticks.
// AirGunJson: an aircraft's gun against air targets (the plan's air-combat model, 2.9). Degrees,
// percents, milliradians per second, tenths of a metre.
internal sealed class AirGunJson
{
    // Cone: degrees off the shooter's velocity (an aircraft) within which a target may be fired at.
    [JsonPropertyName("cone")]
    public int Cone { get; set; }

    // HitBase: per air class, the hit chance (percent) of one burst at its best.
    [JsonPropertyName("hitBase")]
    public Dictionary<string, int>? HitBase { get; set; }

    // CrossHalf: the crossing rate (milliradians per second) that halves the chance.
    [JsonPropertyName("crossHalf")]
    public int CrossHalf { get; set; }

    // RangeFar: percent of the chance left at the weapon's full range.
    [JsonPropertyName("rangeFar")]
    public int RangeFar { get; set; }

    // Ceiling: a ground gun engages air targets only below this height over its ground (0: no limit).
    [JsonPropertyName("ceiling")]
    public int Ceiling { get; set; }

    // StrafeRange: on a strafing pass (E7) it opens fire at ground targets within this slant range
    // (tenths of a cell; 0: the weapon's range).
    [JsonPropertyName("strafeRange")]
    public int StrafeRange { get; set; }

    // StrafeEvery: on a strafing pass, ticks between bursts (0: the weapon's reload).
    [JsonPropertyName("strafeEvery")]
    public int StrafeEvery { get; set; }
}

// DeliveryJson: how an aircraft delivers a bomb (the plan's air-combat model, 2.10). Heights in
// tenths of a metre, distances in tenths of a cell, angles in degrees.
internal sealed class DeliveryJson
{
    // Profile: "level", "dive" or "toss".
    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    [JsonPropertyName("releaseHeight")]
    public int ReleaseHeight { get; set; }

    [JsonPropertyName("diveAngle")]
    public int DiveAngle { get; set; }

    [JsonPropertyName("tossAngle")]
    public int TossAngle { get; set; }

    [JsonPropertyName("ipDistance")]
    public int IpDistance { get; set; }

    [JsonPropertyName("scatterLevel")]
    public int ScatterLevel { get; set; }

    [JsonPropertyName("scatterDive")]
    public int ScatterDive { get; set; }

    [JsonPropertyName("scatterToss")]
    public int ScatterToss { get; set; }

    // Guided: a guided bomb's hit roll at release (the Watcher's model); absent: free-fall.
    [JsonPropertyName("guided")]
    public GuidedBombJson? Guided { get; set; }

    // Stick: bombs released per pass, StickSpacing apart.
    [JsonPropertyName("stick")]
    public int Stick { get; set; }

    [JsonPropertyName("stickSpacing")]
    public int StickSpacing { get; set; }

    // Gravity: a free-fall bomb's fall, tenths of a metre a second squared (E7: the game's gravity for
    // bombs, compressed like its speeds); required without guided.
    [JsonPropertyName("gravity")]
    public int Gravity { get; set; }
}

// GuidedBombJson: percents; the miss distances in tenths of a cell.
internal sealed class GuidedBombJson
{
    [JsonPropertyName("hitIdle")]
    public int HitIdle { get; set; }

    [JsonPropertyName("hitMoving")]
    public int HitMoving { get; set; }

    [JsonPropertyName("missNear")]
    public int MissNear { get; set; }

    [JsonPropertyName("missFar")]
    public int MissFar { get; set; }
}

internal sealed class InterceptJson
{
    // Classes: the interception classes it engages ("drone"; rockets and ballistic missiles later).
    [JsonPropertyName("classes")]
    public List<string>? Classes { get; set; }

    [JsonPropertyName("lockTicks")]
    public int LockTicks { get; set; }

    [JsonPropertyName("fuse")]
    public int Fuse { get; set; }

    [JsonPropertyName("killTail")]
    public int KillTail { get; set; }

    [JsonPropertyName("killHead")]
    public int KillHead { get; set; }

    // CrossHalf: milliradians per second of crossing seen at launch that halve the kill chance.
    [JsonPropertyName("crossHalf")]
    public int CrossHalf { get; set; }

    [JsonPropertyName("speedRef")]
    public int SpeedRef { get; set; }

    [JsonPropertyName("rangeFar")]
    public int RangeFar { get; set; }

    [JsonPropertyName("trackBonus")]
    public int TrackBonus { get; set; }

    [JsonPropertyName("trackFull")]
    public int TrackFull { get; set; }

    [JsonPropertyName("killMin")]
    public int KillMin { get; set; }

    [JsonPropertyName("killMax")]
    public int KillMax { get; set; } = 100;

    // LeadTicks: lead pursuit, at most this many ticks of the target's flight ahead (0: pure pursuit).
    [JsonPropertyName("leadTicks")]
    public int LeadTicks { get; set; }

    // LaunchHeight: tenths of a metre above the shooter's ground it sees from and launches from (0: a
    // soldier's eye, or a window in a building). A vehicle's launcher rails.
    [JsonPropertyName("launchHeight")]
    public int LaunchHeight { get; set; }

    // KillBest: per class, the kill chance (percent) of its best shot, scaling its whole range of
    // chances down ("scout" or "watcher": drones that fly high are hard to bring down).
    [JsonPropertyName("killBest")]
    public Dictionary<string, int>? KillBest { get; set; }

    // Hits: per class, a flat hit chance and the share of the target's max HP a hit takes, instead
    // of the kill model ("overseer": a weak drone two soldier hits bring down).
    [JsonPropertyName("hits")]
    public Dictionary<string, InterceptHitJson>? Hits { get; set; }

    // SpeedRefs: per class, the target speed (tenths of a cell a second) above which the chance falls,
    // instead of speedRef (jets fly faster than any drone).
    [JsonPropertyName("speedRefs")]
    public Dictionary<string, int>? SpeedRefs { get; set; }

    // HitDamage: per aircraft class, the share (percent) of its max HP a successful kill roll takes
    // (100 or more, or unlisted: it kills).
    [JsonPropertyName("hitDamage")]
    public Dictionary<string, int>? HitDamage { get; set; }

    // Seeker: "ir" or "radar": what countermeasures decoy it (E6).
    [JsonPropertyName("seeker")]
    public string? Seeker { get; set; }

    // Boresight: degrees off an aircraft shooter's velocity within which it may lock (E6); 0 for none.
    [JsonPropertyName("boresight")]
    public int Boresight { get; set; }
}

// One class's hit rule in an intercept block: percents.
internal sealed class InterceptHitJson
{
    // Chance: percent chance the fuse hits.
    [JsonPropertyName("chance")]
    public int Chance { get; set; }

    // Damage: percent of the target's max HP a hit takes (100 or more kills).
    [JsonPropertyName("damage")]
    public int Damage { get; set; }
}

internal sealed class GuidedJson
{
    // MaxFlight: tenths of a cell flown guided.
    [JsonPropertyName("maxFlight")]
    public int MaxFlight { get; set; }

    // TurnRate: degrees per second it can turn (compiled to a per-tick change of velocity).
    [JsonPropertyName("turnRate")]
    public int TurnRate { get; set; }

    [JsonPropertyName("requiresOperatorStill")]
    public bool RequiresOperatorStill { get; set; }

    // DropDistance: tenths of a cell it flies on once unguided before hitting the ground.
    [JsonPropertyName("dropDistance")]
    public int DropDistance { get; set; }
}

internal sealed class DroneJson
{
    // CruiseAboveMax: tenths of a metre above the map's highest ground it cruises at.
    [JsonPropertyName("cruiseAboveMax")]
    public int CruiseAboveMax { get; set; }

    // ClimbRate: tenths of a metre per second it climbs after the launch.
    [JsonPropertyName("climbRate")]
    public int ClimbRate { get; set; }

    // BoosterTicks: how long its launch booster burns (the renderer drops it then).
    [JsonPropertyName("boosterTicks")]
    public int BoosterTicks { get; set; }

    // Dive: the dive's slope in permille (metres down per metre forward; 1000 is 45 degrees).
    [JsonPropertyName("dive")]
    public int Dive { get; set; }

    // LaunchHeight: tenths of a metre above the launcher's ground it leaves the rail.
    [JsonPropertyName("launchHeight")]
    public int LaunchHeight { get; set; }

    // RoundOff: ticks over which the climb rounds off into level cruise (0: a corner).
    [JsonPropertyName("roundOff")]
    public int RoundOff { get; set; }

    // PitchOver: ticks of flight the pitch-over from cruise into the dive takes (0: a corner).
    [JsonPropertyName("pitchOver")]
    public int PitchOver { get; set; }
}

internal sealed class DepotReloadJson
{
    [JsonPropertyName("everyTicks")]
    public int EveryTicks { get; set; }

    [JsonPropertyName("afterFireTicks")]
    public int AfterFireTicks { get; set; }

    // Cost: supply taken from the depot's stock per round reloaded; 0 is free.
    [JsonPropertyName("cost")]
    public int Cost { get; set; }

    // Stowed: it reloads only while stowed (a deployed launcher at a depot packs up first).
    [JsonPropertyName("stowed")]
    public bool Stowed { get; set; }
}

// Cargo: supply a truck carries (full or empty) and the ticks it takes to unload at a depot.
internal sealed class CargoJson
{
    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("unloadTicks")]
    public int UnloadTicks { get; set; }
}

internal sealed class AffectsJson
{
    [JsonPropertyName("relation")]
    public string? Relation { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}

internal sealed class AuraJson
{
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("everyTicks")]
    public int EveryTicks { get; set; }

    [JsonPropertyName("heal")]
    public int Heal { get; set; }

    [JsonPropertyName("modifiers")]
    public List<ModifierJson>? Modifiers { get; set; }

    [JsonPropertyName("affects")]
    public AffectsJson? Affects { get; set; }
}

internal sealed class PlacerJson
{
    [JsonPropertyName("archetype")]
    public string? Archetype { get; set; }

    [JsonPropertyName("charges")]
    public int Charges { get; set; }

    [JsonPropertyName("cooldown")]
    public int Cooldown { get; set; }

    [JsonPropertyName("range")]
    public int Range { get; set; }

    [JsonPropertyName("maxActive")]
    public int MaxActive { get; set; }

    // PlaceTicks: ticks it stands at the spot placing it (0: at once).
    [JsonPropertyName("placeTicks")]
    public int PlaceTicks { get; set; }

    // DepotReload: only a supply depot refills the kit, like a launcher's depotReload.
    [JsonPropertyName("depotReload")]
    public DepotReloadJson? DepotReload { get; set; }
}

internal sealed class LaunchJson
{
    [JsonPropertyName("archetype")]
    public string? Archetype { get; set; }

    [JsonPropertyName("charges")]
    public int Charges { get; set; }

    [JsonPropertyName("cooldown")]
    public int Cooldown { get; set; }

    [JsonPropertyName("range")]
    public int Range { get; set; }

    // MaxActive: the most launched units aloft at a time (0: no limit).
    [JsonPropertyName("maxActive")]
    public int MaxActive { get; set; }

    // DepotReload: only a supply depot refills it, like a launcher's depotReload.
    [JsonPropertyName("depotReload")]
    public DepotReloadJson? DepotReload { get; set; }

    // NoResupply: nothing refills it (a single-use scout station; a scout coming home still does).
    [JsonPropertyName("noResupply")]
    public bool NoResupply { get; set; }
}

internal sealed class StrikeJson
{
    [JsonPropertyName("contact")]
    public int Contact { get; set; }

    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("warhead")]
    public string? Warhead { get; set; }

    [JsonPropertyName("splash")]
    public int Splash { get; set; }

    // Runway: what its blast does to a runway it lands on (see RunwayEffectJson); absent: nothing.
    [JsonPropertyName("runway")]
    public RunwayEffectJson? Runway { get; set; }
}

internal sealed class SupplyJson
{
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("everyTicks")]
    public int EveryTicks { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    // Stock is in salvos or charges; 0 is unlimited.
    [JsonPropertyName("stock")]
    public int Stock { get; set; }

    [JsonPropertyName("restock")]
    public int Restock { get; set; }

    // ReloadBonus is a percent.
    [JsonPropertyName("reloadBonus")]
    public int ReloadBonus { get; set; }

    // ConstructedStock: the stock a worker-built supplier starts with (default: stock).
    [JsonPropertyName("constructedStock")]
    public int? ConstructedStock { get; set; }
}

internal sealed class TriggerJson
{
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("warhead")]
    public string? Warhead { get; set; }

    [JsonPropertyName("splash")]
    public int Splash { get; set; }

    [JsonPropertyName("affects")]
    public List<string>? Affects { get; set; }

    // Edge: the damage at the blast's edge, in percent of the centre's (default 50).
    [JsonPropertyName("edge")]
    public int? Edge { get; set; }

    // Kills: tags of the one unit its blast destroys outright (the one that set it off, or the
    // nearest enemy with them in its radius); its blast never kills another unit with them.
    [JsonPropertyName("kills")]
    public List<string>? Kills { get; set; }

    // ChainTicks: the delay before a trap inside its blast goes off in turn (default 2).
    [JsonPropertyName("chainTicks")]
    public int? ChainTicks { get; set; }
}

internal sealed class BuilderJson
{
    [JsonPropertyName("rate")]
    public int Rate { get; set; }

    [JsonPropertyName("range")]
    public int Range { get; set; }
}

internal sealed class BuildAssistJson
{
    [JsonPropertyName("bonus")]
    public int Bonus { get; set; }

    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("unlocks")]
    public List<string>? Unlocks { get; set; }
}

internal sealed class RepairJson
{
    [JsonPropertyName("rate")]
    public int Rate { get; set; }

    [JsonPropertyName("everyTicks")]
    public int EveryTicks { get; set; }

    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("affects")]
    public List<string>? Affects { get; set; }
}

// ModifierJson changes one stat. Flat values use the stat's content units:
// speed in tenths of a cell per second, range in tenths of a cell.
internal sealed class ModifierJson
{
    [JsonPropertyName("stat")]
    public string? Stat { get; set; }

    [JsonPropertyName("percent")]
    public int Percent { get; set; }

    [JsonPropertyName("flat")]
    public int Flat { get; set; }
}

internal sealed class FactionModifierJson
{
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("stat")]
    public string? Stat { get; set; }

    [JsonPropertyName("percent")]
    public int Percent { get; set; }

    [JsonPropertyName("flat")]
    public int Flat { get; set; }
}

internal sealed class FactionJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("archetypes")]
    public List<string>? Archetypes { get; set; }

    [JsonPropertyName("components")]
    public List<string>? Components { get; set; }

    [JsonPropertyName("modifiers")]
    public List<FactionModifierJson>? Modifiers { get; set; }

    [JsonPropertyName("maxPresetsPerArchetype")]
    public int MaxPresetsPerArchetype { get; set; }

    [JsonPropertyName("defeatedWithout")]
    public List<string>? DefeatedWithout { get; set; }
}
