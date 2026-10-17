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

    // DeployTicks and StowTicks: ticks to set up before firing and to pack up before moving (0: none).
    [JsonPropertyName("deployTicks")]
    public int DeployTicks { get; set; }

    [JsonPropertyName("stowTicks")]
    public int StowTicks { get; set; }

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

    [JsonPropertyName("footprint")]
    public FootprintJson? Footprint { get; set; }

    // BuildTime is in ticks.
    [JsonPropertyName("buildTime")]
    public int BuildTime { get; set; }
}

// CrewJson is a vehicle's driver seat. EnterRange is in tenths of a cell;
// Survival and ExitDamage are percents.
// Garrison: a building soldiers can enter. Ranges in tenths of a cell; percents 0-100.
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
