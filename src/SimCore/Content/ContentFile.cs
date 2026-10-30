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

    // Concealment: concealment from the air under trees (at most one file may carry it); see ConcealmentJson.
    [JsonPropertyName("concealment")]
    public ConcealmentJson? Concealment { get; set; }

    // Fire: forest fire (at most one file may carry it); see FireJson.
    [JsonPropertyName("fire")]
    public FireJson? Fire { get; set; }

    // Squad: the infantry squad rules (at most one file may carry it); see SquadJson.
    [JsonPropertyName("squad")]
    public SquadJson? Squad { get; set; }

    // AirfieldTypes: airfield types (the grid, the classes served, the pieces); see AirfieldTypeJson.
    [JsonPropertyName("airfieldTypes")]
    public List<AirfieldTypeJson>? AirfieldTypes { get; set; }

    // AirClasses: the air classes (E5), in order: a class's value is its position (1-based) across the
    // files in file-name order. See AirClassJson.
    [JsonPropertyName("airClasses")]
    public List<AirClassJson>? AirClasses { get; set; }

    // ExclusiveGroups: groups of archetypes a player may only have so many of at a time (the heavy bombers' "choose 1 of 3"); see
    // ExclusiveGroupJson. Archetypes join one with "exclusive".
    [JsonPropertyName("exclusiveGroups")]
    public List<ExclusiveGroupJson>? ExclusiveGroups { get; set; }
}

// ExclusiveGroupJson: an exclusive group (Model.ExclusiveGroup).
internal sealed class ExclusiveGroupJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    // Max: the most units of the group a player may have at once (alive, queued or being built), at least 1.
    [JsonPropertyName("max")]
    public int Max { get; set; }

    // LockType: the first one a player builds locks the group to that archetype for the match.
    [JsonPropertyName("lockType")]
    public bool LockType { get; set; }
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

// LoadoutJson: one loadout mode of an aircraft (Model.Loadout).
internal sealed class LoadoutJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    // Ammo: rounds by component id (a component of the archetype carrying a weapon with ammunition; at most that ammunition).
    // A component not listed carries its full ammunition.
    [JsonPropertyName("ammo")]
    public Dictionary<string, int>? Ammo { get; set; }

    // Speed: its cruise, attack and dash speeds, percent of its flight's; 0 for 100.
    [JsonPropertyName("speed")]
    public int Speed { get; set; }

    // Signature: its signature in this mode (see SignatureJson); none: the archetype's.
    [JsonPropertyName("signature")]
    public SignatureJson? Signature { get; set; }

    // Sensors: its sensors in this mode (as the archetype's sensors); none: the archetype's.
    [JsonPropertyName("sensors")]
    public List<string>? Sensors { get; set; }
}

// FireJson: forest fire (Model.FireRules). Every field is required. Ticks are 20 a second; spread is per mille per spread look,
// per forest density [none, scrub, grove, dense]; the smoke radius in tenths of a cell.
internal sealed class FireJson
{
    [JsonPropertyName("burnTicks")]
    public List<int>? BurnTicks { get; set; }

    [JsonPropertyName("spread")]
    public List<int>? Spread { get; set; }

    [JsonPropertyName("diagonal")]
    public int? Diagonal { get; set; }

    [JsonPropertyName("spreadEveryTicks")]
    public int? SpreadEveryTicks { get; set; }

    [JsonPropertyName("maxBurning")]
    public int? MaxBurning { get; set; }

    [JsonPropertyName("maxIgnitionsPerTick")]
    public int? MaxIgnitionsPerTick { get; set; }

    [JsonPropertyName("emberTicks")]
    public int? EmberTicks { get; set; }

    [JsonPropertyName("terrainEveryTicks")]
    public int? TerrainEveryTicks { get; set; }

    [JsonPropertyName("smoke")]
    public FireSmokeJson? Smoke { get; set; }

    [JsonPropertyName("damage")]
    public FireDamageJson? Damage { get; set; }

    [JsonPropertyName("fleeCells")]
    public int? FleeCells { get; set; }
}

// FireSmokeJson: the smoke screens burning forest pops: radius (tenths of a cell), how long each stands, the most at once.
internal sealed class FireSmokeJson
{
    [JsonPropertyName("radius")]
    public int? Radius { get; set; }

    [JsonPropertyName("ticks")]
    public int? Ticks { get; set; }

    [JsonPropertyName("max")]
    public int? Max { get; set; }
}

// FireDamageJson: what fire does to the units standing in it: percent of max HP every so many ticks, to the units with the tags.
internal sealed class FireDamageJson
{
    [JsonPropertyName("percent")]
    public int? Percent { get; set; }

    [JsonPropertyName("everyTicks")]
    public int? EveryTicks { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}

// WarheadFireJson: what a warhead's blast sets on fire (Model.WarheadFire): the forest cells within radius (tenths of a cell)
// or radiusPercent of its weapon's splash (the larger; one at least), at most count [min, max] of them (left out: every one),
// and whether the fires spread (spreadPercent of the fire rules' chances, 100 when left out; only with spreads true); ground:
// the ticks open ground within the radius burns too (absent: only forest).
internal sealed class WarheadFireJson
{
    [JsonPropertyName("radius")]
    public int? Radius { get; set; }

    [JsonPropertyName("radiusPercent")]
    public int? RadiusPercent { get; set; }

    [JsonPropertyName("count")]
    public List<int>? Count { get; set; }

    [JsonPropertyName("spreads")]
    public bool? Spreads { get; set; }

    [JsonPropertyName("spreadPercent")]
    public int? SpreadPercent { get; set; }

    // Ground: ticks open ground (land without forest) within the radius burns; absent: only forest (the incendiary bomb's).
    [JsonPropertyName("ground")]
    public int? Ground { get; set; }
}

// ConcealmentJson: concealment from the air under trees (Model.ConcealmentRules). Every field is required. Camera reaches per
// forest density [none, scrub, grove, dense], tenths of a cell (level, from the air unit): -1 seen as in the open, 0 hidden.
internal sealed class ConcealmentJson
{
    [JsonPropertyName("infantry")]
    public ConcealmentKindJson? Infantry { get; set; }

    [JsonPropertyName("vehicle")]
    public ConcealmentKindJson? Vehicle { get; set; }

    // FireReveal: ticks a shot shows the shooter to air sensors as in the open.
    [JsonPropertyName("fireReveal")]
    public int? FireReveal { get; set; }

    [JsonPropertyName("thermal")]
    public ConcealmentThermalJson? Thermal { get; set; }

    [JsonPropertyName("heliLow")]
    public ConcealmentHeliJson? HeliLow { get; set; }
}

// ConcealmentKindJson: a camera's reaches for one kind of unit, still and moving.
internal sealed class ConcealmentKindJson
{
    [JsonPropertyName("still")]
    public List<int>? Still { get; set; }

    [JsonPropertyName("moving")]
    public List<int>? Moving { get; set; }
}

// ConcealmentThermalJson: what thermal sees under canopy.
internal sealed class ConcealmentThermalJson
{
    // EngineTicks: a vehicle that moved within this many ticks.
    [JsonPropertyName("engineTicks")]
    public int? EngineTicks { get; set; }

    // MovingInfantry: moving infantry, per forest density.
    [JsonPropertyName("movingInfantry")]
    public List<bool>? MovingInfantry { get; set; }
}

// ConcealmentHeliJson: a low helicopter's camera.
internal sealed class ConcealmentHeliJson
{
    // Height: at most this high over the ground, tenths of a metre.
    [JsonPropertyName("height")]
    public int? Height { get; set; }

    // Bonus: added to every limited camera reach, tenths of a cell.
    [JsonPropertyName("bonus")]
    public int? Bonus { get; set; }
}

// RadarHorizonJson: a radar's horizon (Model.RadarHorizon).
internal sealed class RadarHorizonJson
{
    // Beyond: tenths of a cell of level distance past which low targets can hide behind the ground.
    [JsonPropertyName("beyond")]
    public int Beyond { get; set; }

    // Below: tenths of a metre over the ground under which a target can hide.
    [JsonPropertyName("below")]
    public int Below { get; set; }
}

// NetworkJson: an archetype's place in the antenna network (Model.NetworkNode; docs/design.md, "Antenna network").
internal sealed class NetworkJson
{
    // Role: "antenna" (sees drones, links to antennas and hubs) or "hub" (a radar: the chain's end; needs an airVision).
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    // LaunchReach: tenths of a cell (level) from it within which an enemy launch is reported to its side.
    [JsonPropertyName("launchReach")]
    public int LaunchReach { get; set; }

    // LaunchClasses: the projectile air classes it reports launches of.
    [JsonPropertyName("launchClasses")]
    public List<string>? LaunchClasses { get; set; }

    // DroneReach: an antenna's reach for drones in the air, tenths of a cell (slant from its top, by signature).
    [JsonPropertyName("droneReach")]
    public int DroneReach { get; set; }

    // DroneClasses: the air classes an antenna sees as drones.
    [JsonPropertyName("droneClasses")]
    public List<string>? DroneClasses { get; set; }

    // LinkRange: an antenna's link reach to other antennas and hubs of its side, tenths of a cell (level).
    [JsonPropertyName("linkRange")]
    public int LinkRange { get; set; }

    // ReportTicks: ticks a launch report is shown.
    [JsonPropertyName("reportTicks")]
    public int ReportTicks { get; set; }

    // LauncherTicks: ticks a reported launcher is seen by the side.
    [JsonPropertyName("launcherTicks")]
    public int LauncherTicks { get; set; }
}

// InterceptNetworkJson: a radar-linked interceptor's antenna network warning rules (Model.InterceptNetwork).
internal sealed class InterceptNetworkJson
{
    // Leave: classes it leaves to the launchers with the leaveTo tags while one of them can take the threat (some of its classes).
    [JsonPropertyName("leave")]
    public List<string>? Leave { get; set; }

    // LeaveTo: the tags (all of them) of those launchers; needed with leave.
    [JsonPropertyName("leaveTo")]
    public List<string>? LeaveTo { get; set; }
}

// EmitterJson: a searching radar that gives itself away (Model.Emitter).
internal sealed class EmitterJson
{
    // Range: tenths of a cell (slant from its antenna) within which enemy radar sensors pick it up.
    [JsonPropertyName("range")]
    public int Range { get; set; }

    // Radius: the revealed disc round it, tenths of a cell.
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    // Linger: ticks a reveal lasts after the sweep that renewed it.
    [JsonPropertyName("linger")]
    public int Linger { get; set; }

    // ExcludeTags: sensors with one of these tags do not pick it up (the soldiers' eyes).
    [JsonPropertyName("excludeTags")]
    public List<string>? ExcludeTags { get; set; }
}

// RadarSpottingJson: an aircraft's radar spotting (Model.RadarSpotting).
internal sealed class RadarSpottingJson
{
    // Tags: the radars that count (any-of): units with an airVision and one of these tags.
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    // Linger: ticks a radar stays revealed after the aircraft left its coverage.
    [JsonPropertyName("linger")]
    public int Linger { get; set; }

    // Radius: the revealed disc round the radar, tenths of a cell.
    [JsonPropertyName("radius")]
    public int Radius { get; set; }
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

    // BreakPk: percent of the kill chance left after a salvo dispensed in a break (E13); 0: pk.
    [JsonPropertyName("breakPk")]
    public int BreakPk { get; set; }
}

// AttackRunJson: a fixed wing's attack-run geometry (E13); see Model.AttackRunSpec. Distances in tenths of a cell.
internal sealed class AttackRunJson
{
    [JsonPropertyName("align")]
    public int Align { get; set; }

    [JsonPropertyName("breakTurn")]
    public int BreakTurn { get; set; }

    [JsonPropertyName("break")]
    public int Break { get; set; }

    [JsonPropertyName("extend")]
    public int Extend { get; set; }

    [JsonPropertyName("releaseWindow")]
    public int ReleaseWindow { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }
}

// GunshipJson: a gunship's orbit attack (H3); see Model.GunshipSpec. Distances in tenths of a cell.
internal sealed class GunshipJson
{
    // Radius: the orbit's radius round the area's centre.
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    // Area: the area's radius (targets and area fire inside it).
    [JsonPropertyName("area")]
    public int Area { get; set; }

    // OnCircle: how far off the radius it still counts as on the circle (and fires).
    [JsonPropertyName("onCircle")]
    public int OnCircle { get; set; }

    // Bearing: degrees the centre may lie off its left wing for it to fire.
    [JsonPropertyName("bearing")]
    public int Bearing { get; set; }
}

// KamanJson: a Kaman drone's one-way attack (its missiles come from its weapon); see Model.KamanSpec. Damage in HP,
// splash in tenths of a cell, percents 0 to 100, dive in metres down per 1000 forward, pitchOver in ticks.
internal sealed class KamanJson
{
    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("warhead")]
    public string? Warhead { get; set; }

    [JsonPropertyName("splash")]
    public int Splash { get; set; }

    [JsonPropertyName("splashCenter")]
    public int SplashCenter { get; set; } = 100;

    [JsonPropertyName("splashEdge")]
    public int SplashEdge { get; set; } = 50;

    [JsonPropertyName("dive")]
    public int Dive { get; set; }

    [JsonPropertyName("pitchOver")]
    public int PitchOver { get; set; }

    // Runway: what its blast does to a runway it lands on (see RunwayEffectJson); absent: nothing.
    [JsonPropertyName("runway")]
    public RunwayEffectJson? Runway { get; set; }
}

// SideFireJson: a gunship's side cannon (H3); see Model.SideFire.
internal sealed class SideFireJson
{
    // Scatter: per axis round its aim point, tenths of a cell.
    [JsonPropertyName("scatter")]
    public int Scatter { get; set; }

    // Tier: the airstrike tier its impact looks like (1 to 7; 0: by its damage).
    [JsonPropertyName("tier")]
    public int Tier { get; set; }

    // Priority: the armour classes it fires at, best first (unlisted: never).
    [JsonPropertyName("priority")]
    public List<string>? Priority { get; set; }

    // AreaFire: with no target, fire at seeded points inside the area (attack-ground only).
    [JsonPropertyName("areaFire")]
    public bool AreaFire { get; set; }

    // Pick: "nearest" (to the area's centre; the default) or "biggest" (the most max HP).
    [JsonPropertyName("pick")]
    public string? Pick { get; set; }
}

// ElementJson: how fixed wings given the same order fly together (E13); see Model.ElementSpec.
internal sealed class ElementJson
{
    [JsonPropertyName("slots")]
    public List<SlotJson>? Slots { get; set; }

    [JsonPropertyName("stagger")]
    public int Stagger { get; set; }
}

// SlotJson: one formation slot (tenths of a cell back along the lead's path, aside to its right; negative: left).
internal sealed class SlotJson
{
    [JsonPropertyName("back")]
    public int Back { get; set; }

    [JsonPropertyName("aside")]
    public int Aside { get; set; }
}

// PursuitJson: air-to-air pursuit separation (E13); see Model.PursuitSpec. Distances in tenths of a cell,
// the climb in tenths of a metre, times in ticks.
internal sealed class PursuitJson
{
    [JsonPropertyName("separation")]
    public int Separation { get; set; }

    [JsonPropertyName("breakOff")]
    public int BreakOff { get; set; }

    [JsonPropertyName("reattack")]
    public int Reattack { get; set; }

    [JsonPropertyName("climb")]
    public int Climb { get; set; }

    [JsonPropertyName("maxTicks")]
    public int MaxTicks { get; set; }
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
    [JsonPropertyName("detourBudget")]
    public int DetourBudget { get; set; }
    [JsonPropertyName("detourRadiusCells")]
    public int DetourRadiusCells { get; set; }
    [JsonPropertyName("detourCost")]
    public int DetourCost { get; set; }
    [JsonPropertyName("detourRetryTicks")]
    public int DetourRetryTicks { get; set; }

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

    // MaxHpBySplash: the maxHpPercent share falls off with the splash (the inner ring all of it, the outer ring its percent).
    [JsonPropertyName("maxHpBySplash")]
    public bool MaxHpBySplash { get; set; }

    // Fire: what its blast sets on fire (forest fire); none: nothing.
    [JsonPropertyName("fire")]
    public WarheadFireJson? Fire { get; set; }
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

    // HullAims: a launcher whose box is fixed on the hull (it only elevates): the truck turns to face its target before it
    // sets up, and a set-up truck whose target is off its arc packs up, turns and sets up again. Needs a deployTicks and no
    // turret.
    [JsonPropertyName("hullAims")]
    public bool HullAims { get; set; }

    // HullArc: degrees either side of a hull-aiming launcher's hull it fires at once set up, rounded down to whole facings of
    // 11.25 degrees, at least one (the default, absent or 0): a guided missile that turns after its boost (the cruise truck, the
    // drone rails) takes a wide arc, 180 for any way. Needs hullAims.
    [JsonPropertyName("hullArc")]
    public int HullArc { get; set; }

    // VerticalLaunch: a launcher that fires straight up and lets the missile turn (the S-400 type, the hypersonic truck): it
    // never turns to aim. No turret.
    [JsonPropertyName("verticalLaunch")]
    public bool VerticalLaunch { get; set; }

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

    // GroundRadar (H1, a radar structure): tenths of a cell within which it spots enemy vehicles anywhere and
    // enemy infantry in the open (not in woods) for its side's fog of war; 0 for none.
    [JsonPropertyName("groundRadar")]
    public int GroundRadar { get; set; }

    // GroundRadarVehicles: its ground radar spots only vehicles (a helicopter's mast radar).
    [JsonPropertyName("groundRadarVehicles")]
    public bool GroundRadarVehicles { get; set; }

    // CueLock (a radar that cues its side's air defence: the mobile radar): its side's ground air defence weapons lock on an
    // air target it tracks (set up) in this percent of their own lock time (1 to 100); 0 for none. Needs an airVision.
    [JsonPropertyName("cueLock")]
    public int CueLock { get; set; }

    // DirectionalArmor: damage scaled by where a hit lands on the hull (see DirectionalArmorJson); absent: none.
    [JsonPropertyName("directionalArmor")]
    public DirectionalArmorJson? DirectionalArmor { get; set; }

    // Airlift: an airlifter's vehicle hold (see AirliftJson); absent: none. Needs a runway aircraft.
    [JsonPropertyName("airlift")]
    public AirliftJson? Airlift { get; set; }

    // TraverseTicks: a slow turret turns one facing every this many ticks (the howitzer); 0 for the default. Needs turret.
    [JsonPropertyName("traverseTicks")]
    public int TraverseTicks { get; set; }

    // SweepTicks: one turn of its search antenna, in ticks (for renderers: World.RadarSweep); 0 for none. Needs an airVision.
    [JsonPropertyName("sweepTicks")]
    public int SweepTicks { get; set; }

    // Helipad: one helicopter builds, parks and is serviced on it (see HelipadJson); structures only.
    [JsonPropertyName("helipad")]
    public HelipadJson? Helipad { get; set; }

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

    // Exclusive: the exclusive group it belongs to (an exclusiveGroups id): production keeps a player within the group's rule.
    [JsonPropertyName("exclusive")]
    public string? Exclusive { get; set; }

    // Loadouts: its loadout modes (see LoadoutJson; the stealth multirole), the first the one it is built in; fixed-wing aircraft only.
    [JsonPropertyName("loadouts")]
    public List<LoadoutJson>? Loadouts { get; set; }

    // LoadoutSwitch: the ticks a mode change takes, parked at a working station; needs loadouts.
    [JsonPropertyName("loadoutSwitch")]
    public int LoadoutSwitch { get; set; }

    // Sensors: what it looks under trees with as an air unit (concealment from the air): "camera", "thermal", "groundRadar";
    // flying archetypes only; none: a camera.
    [JsonPropertyName("sensors")]
    public List<string>? Sensors { get; set; }

    // RadarSpotting: its radar spotting (see RadarSpottingJson; the stealth multirole); flying archetypes with a flight only.
    [JsonPropertyName("radarSpotting")]
    public RadarSpottingJson? RadarSpotting { get; set; }

    // CounterStealth: against its air vision a target's signature values move this percent of the way to 100 (the S-400 type
    // radar: 50); 0 for none. Needs an airVision.
    [JsonPropertyName("counterStealth")]
    public int CounterStealth { get; set; }

    // DeploySlope: the steepest grade (percent) under its footprint it sets up on (the cruise missile truck: 15); 0 for no rule.
    // Needs deployTicks.
    [JsonPropertyName("deploySlope")]
    public int DeploySlope { get; set; }

    // RadarHorizon: low targets far out hide behind the ground from its air vision (see RadarHorizonJson); needs an airVision.
    [JsonPropertyName("radarHorizon")]
    public RadarHorizonJson? RadarHorizon { get; set; }

    // Emitter: its searching radar gives it away to enemy radars (see EmitterJson); needs an airVision, ground units only.
    [JsonPropertyName("emitter")]
    public EmitterJson? Emitter { get; set; }

    // Network: its place in the antenna network (see NetworkJson): an antenna or a hub that reports launches.
    [JsonPropertyName("network")]
    public NetworkJson? Network { get; set; }

    // Countermeasures: its dispensers (see CountermeasureJson); flying archetypes with a flight only.
    [JsonPropertyName("countermeasures")]
    public List<CountermeasureJson>? Countermeasures { get; set; }

    // Evade: its break on a launch warning (see EvadeJson); fixed-wing flights only.
    [JsonPropertyName("evade")]
    public EvadeJson? Evade { get; set; }

    // AttackRun: its attack-run geometry (see AttackRunJson; E13); fixed-wing flights only.
    [JsonPropertyName("attackRun")]
    public AttackRunJson? AttackRun { get; set; }

    // Gunship: its orbit attack (see GunshipJson; H3); fixed-wing flights only.
    [JsonPropertyName("gunship")]
    public GunshipJson? Gunship { get; set; }

    // Kaman: its one-way attack, missiles then a dive into its target (see KamanJson); drone flights only.
    [JsonPropertyName("kaman")]
    public KamanJson? Kaman { get; set; }

    // Element: its formation slots and staggered runs (see ElementJson; E13); fixed-wing flights only.
    [JsonPropertyName("element")]
    public ElementJson? Element { get; set; }

    // Pursuit: its air-to-air pursuit separation (see PursuitJson; E13); flying archetypes with a flight only.
    [JsonPropertyName("pursuit")]
    public PursuitJson? Pursuit { get; set; }

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

    // Neutral: a civilian structure without a garrison (the farm buildings), placed neutral (owner 0)
    // and nobody's for good; it blocks its footprint like a town building, its rubble too.
    [JsonPropertyName("neutral")]
    public bool Neutral { get; set; }

    // Rubble: at 0 HP a standing piece becomes rubble at hpPercent of its max HP.
    [JsonPropertyName("rubble")]
    public RubbleJson? Rubble { get; set; }

    // Repairable: workers repair it; one worker restores the whole bar in fullPercentOfBuild
    // percent of the build time.
    [JsonPropertyName("repairable")]
    public RepairableJson? Repairable { get; set; }

    // ReloadPad (the Patriot-type launcher's): a structure whose pad reloads, one at a time, a packed-up unit with one
    // of its tags standing on it; placed only near a finished airfield of its side.
    [JsonPropertyName("reloadPad")]
    public ReloadPadJson? ReloadPad { get; set; }

    // Service (the military garage's): its doors are shared between production and servicing its side's vehicles (see
    // ServiceJson); it needs production with doors.
    [JsonPropertyName("service")]
    public ServiceJson? Service { get; set; }

    // DropZone (logistics: the field ammo point, the supply stash): a marked zone pallets are airdropped on (see DropZoneJson).
    [JsonPropertyName("dropZone")]
    public DropZoneJson? DropZone { get; set; }

    // AwayFrom (logistics: the forward airbase): it may not be placed within this far of its side's structures of these
    // archetypes (see AwayFromJson).
    [JsonPropertyName("awayFrom")]
    public AwayFromJson? AwayFrom { get; set; }

    // Bridge: a neutral structure the map places over water (never built by players): its road, damage stages and the
    // HP at which a fallen span is laid again (see BridgeJson; docs/design.md, "Bridges").
    [JsonPropertyName("bridge")]
    public BridgeJson? Bridge { get; set; }
}

// BridgeJson: road is the kind every footprint cell carries (highway, paved, gravel, dirt); stages the HP percents above which
// it is in stage 0, 1, ... (descending, 1 to 99; below the last, down to 0 HP, the heaviest standing stage); reopen the percent
// of max HP at which a fallen span's deck is laid again while builders repair it; helpers (optional) the share each further
// builder repairing it adds, as a construction's.
internal sealed class BridgeJson
{
    [JsonPropertyName("road")]
    public string? Road { get; set; }

    [JsonPropertyName("stages")]
    public List<int>? Stages { get; set; }

    [JsonPropertyName("reopen")]
    public int Reopen { get; set; }

    [JsonPropertyName("helpers")]
    public List<int>? Helpers { get; set; }
}

// DropZoneJson: a circle pallets land in to count: its centre [x, y] in tenths of a cell from the footprint's centre (the
// structure's own frame, turned with it), its radius in tenths of a cell.
internal sealed class DropZoneJson
{
    [JsonPropertyName("offset")]
    public List<int>? Offset { get; set; }

    [JsonPropertyName("radius")]
    public int Radius { get; set; }
}

// AwayFromJson: archetype IDs and the least distance in tenths of a cell (whole cells) between its footprint and theirs.
internal sealed class AwayFromJson
{
    [JsonPropertyName("archetypes")]
    public List<string>? Archetypes { get; set; }

    [JsonPropertyName("distance")]
    public int Distance { get; set; }
}

internal sealed class ServiceJson
{
    // Tags: the vehicles it services (any one of them).
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    // ReloadTicks: in its bay, every weapon is refilled to its full ammo after this long.
    [JsonPropertyName("reloadTicks")]
    public int ReloadTicks { get; set; }

    // RepairTicks: in its bay, its HP rises evenly to full over this long.
    [JsonPropertyName("repairTicks")]
    public int RepairTicks { get; set; }

    // CargoTicks: in its bay, an empty cargo truck is refilled after this long.
    [JsonPropertyName("cargoTicks")]
    public int CargoTicks { get; set; }
}

internal sealed class ReloadPadJson
{
    // Tags: the units it reloads (any one of them).
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    // ReloadTicks: standing on the pad, packed up, this long fills every weapon to its full ammo.
    [JsonPropertyName("reloadTicks")]
    public int ReloadTicks { get; set; }

    // AirfieldRange: tenths of a cell; it is placed only within this of a finished airfield facility (or air base core)
    // of its side, and reloads only while that facility stands.
    [JsonPropertyName("airfieldRange")]
    public int AirfieldRange { get; set; }
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

    // MinClass, MaxClass (H1, stations): the aircraft classes it takes ("S" to "XL"; default all of them).
    [JsonPropertyName("minClass")]
    public string? MinClass { get; set; }

    [JsonPropertyName("maxClass")]
    public string? MaxClass { get; set; }
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

    // Rows (H1): the row grid's origin [x, y] (tenths, whole cells; default [0, the segment depth]), the most
    // columns and the most rows in a column (0: no limit).
    [JsonPropertyName("rows")]
    public AirfieldRowsJson? Rows { get; set; }

    // Core (H1, a main air base): what its core lays when finished.
    [JsonPropertyName("core")]
    public CoreJson? Core { get; set; }

    // FacilityDown (H1): what stations needing the facility give while it is down: "nothing" (default) or "refuel".
    [JsonPropertyName("facilityDown")]
    public string? FacilityDown { get; set; }

    // Link (H1): tenths of a cell (whole cells) within which another of the owner's airfields' facility links to its apron.
    [JsonPropertyName("link")]
    public int Link { get; set; }

    // Landing fix (2026-10-21): tenths of a cell a second; the floor on taxi and turn speeds at this type (0: the aircraft's).
    [JsonPropertyName("minTaxi")]
    public int MinTaxi { get; set; }

    [JsonPropertyName("minTurn")]
    public int MinTurn { get; set; }

    // Landing fix (2026-10-21): tenths of a cell (whole cells); the overhead break at most this far from the threshold (0: midfield).
    [JsonPropertyName("breakDistance")]
    public int BreakDistance { get; set; }

    // Tempo (2026-10-24, the owner's faster airfield): percent; how much faster than the aircraft's own numbers it leaves and returns.
    [JsonPropertyName("tempo")]
    public AirfieldTempoJson? Tempo { get; set; }
}

// An airfield type's tempo (2026-10-24): percents of the aircraft's own pace (100: as authored).
internal sealed class AirfieldTempoJson
{
    // Leave: the pushback, the taxi out, the line-up and the takeoff roll.
    [JsonPropertyName("leave")]
    public int Leave { get; set; }

    // Return: the flight home and the hold, the approach and the final, the roll-out, the taxi in and the park-in.
    [JsonPropertyName("return")]
    public int Return { get; set; }
}

internal sealed class AirfieldRowsJson
{
    [JsonPropertyName("origin")]
    public List<int>? Origin { get; set; }

    [JsonPropertyName("columns")]
    public int Columns { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }

    // Extra (logistics): false: no rows may be added to the core's built-in ones (the forward airbase: its 2 stands only).
    [JsonPropertyName("extra")]
    public bool? Extra { get; set; }
}

// A core's layout (H1): tenths of a cell, whole cells, in the airfield's frame.
internal sealed class CoreJson
{
    [JsonPropertyName("segments")]
    public int Segments { get; set; }

    [JsonPropertyName("facility")]
    public List<int>? Facility { get; set; }

    [JsonPropertyName("hangar")]
    public List<int>? Hangar { get; set; }

    [JsonPropertyName("radar")]
    public List<int>? Radar { get; set; }

    [JsonPropertyName("apron")]
    public List<int>? Apron { get; set; }

    [JsonPropertyName("rows")]
    public int Rows { get; set; }

    [JsonPropertyName("bays")]
    public List<BayJson>? Bays { get; set; }
}

// A heavy bay (H1): its left edge and width in the hangar (tenths, whole cells), its lane node and its station
// slot's heading, pushback and paths (tenths in the bay's frame).
internal sealed class BayJson
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("w")]
    public int W { get; set; }

    [JsonPropertyName("lane")]
    public List<int>? Lane { get; set; }

    [JsonPropertyName("heading")]
    public int Heading { get; set; }

    [JsonPropertyName("pushback")]
    public bool Pushback { get; set; }

    [JsonPropertyName("parkIn")]
    public List<List<int>>? ParkIn { get; set; }

    [JsonPropertyName("parkOut")]
    public List<List<int>>? ParkOut { get; set; }
}

internal sealed class AirfieldPiecesJson
{
    [JsonPropertyName("core")]
    public string? Core { get; set; }

    [JsonPropertyName("hangar")]
    public string? Hangar { get; set; }

    [JsonPropertyName("radar")]
    public string? Radar { get; set; }

    [JsonPropertyName("heavy")]
    public string? Heavy { get; set; }

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

    // ConnectorEvery (H1): a connector on every n-th segment (k a multiple of n; default 1: every segment).
    [JsonPropertyName("connectorEvery")]
    public int ConnectorEvery { get; set; }

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

    // Reach (2026-10-24): tenths of a cell; the head of the landing queue is cleared to land within it of the hold point
    // (0: twice the radius, as before).
    [JsonPropertyName("reach")]
    public int Reach { get; set; }
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

    // EdgeMargin: retired (E12: aircraft turn in the fly zone past the map's edge, flyZoneMargin).
    // Still accepted; the engine ignores it.
    [JsonPropertyName("edgeMargin")]
    public int EdgeMargin { get; set; }

    // FlyZoneMargin: how far past the map's edge (tenths of a cell, every side) it may fly: the fly
    // zone (E12). At least twice its turn radius plus a second of travel; 0: 48 cells, or that minimum if larger.
    [JsonPropertyName("flyZoneMargin")]
    public int FlyZoneMargin { get; set; }

    // Kind: its kinematics, "drone" (the default: constant speed, turning at turnRate) or "fixedWing"
    // (an energy envelope: the fixedWing block; turnRate is then derived and must be left out).
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    // FixedWing: the envelope of a fixed-wing flight (kind "fixedWing").
    [JsonPropertyName("fixedWing")]
    public FixedWingJson? FixedWing { get; set; }

    // HeightByMap: its flight height grows with the map (E13, D-ALT1); see HeightByMapJson.
    [JsonPropertyName("heightByMap")]
    public HeightByMapJson? HeightByMap { get; set; }

    // EnduranceByMap: its tank grows with the map (E13, D-FUEL1); see EnduranceByMapJson.
    [JsonPropertyName("enduranceByMap")]
    public EnduranceByMapJson? EnduranceByMap { get; set; }

    // SpeedByMap: its transit speeds grow with the map (E13, D-SPD1); see SpeedByMapJson.
    [JsonPropertyName("speedByMap")]
    public SpeedByMapJson? SpeedByMap { get; set; }

    // Rotary: a helicopter's flight (kind "rotary"); see RotaryJson.
    [JsonPropertyName("rotary")]
    public RotaryJson? Rotary { get; set; }
}

// HelipadJson: a helipad's service rates, percents of the helicopter's own 1x rates (its aircraft.service block).
internal sealed class HelipadJson
{
    [JsonPropertyName("refuel")]
    public int Refuel { get; set; }

    [JsonPropertyName("rearm")]
    public int Rearm { get; set; }

    [JsonPropertyName("repair")]
    public int Repair { get; set; }
}

// RotaryJson: a helicopter's flight (flight.kind "rotary"). Speeds in tenths of a cell a second (its cruise is the
// archetype's speed), acceleration in tenths of a cell a second squared, heights in tenths of a metre, distances in
// tenths of a cell, the yaw in degrees a second.
internal sealed class RotaryJson
{
    [JsonPropertyName("maxSpeed")]
    public int MaxSpeed { get; set; }

    [JsonPropertyName("sideSpeed")]
    public int SideSpeed { get; set; }

    [JsonPropertyName("accel")]
    public int Accel { get; set; }

    [JsonPropertyName("yawRate")]
    public int YawRate { get; set; }

    // DescentRate: tenths of a metre a second it sinks (it climbs at flight.climbRate).
    [JsonPropertyName("descentRate")]
    public int DescentRate { get; set; }

    [JsonPropertyName("attackHeight")]
    public int AttackHeight { get; set; }

    [JsonPropertyName("hideHeight")]
    public int HideHeight { get; set; }

    [JsonPropertyName("popMax")]
    public int PopMax { get; set; }

    // Standoff: it fires missiles from this percent of the missile's range.
    [JsonPropertyName("standoff")]
    public int Standoff { get; set; }

    [JsonPropertyName("coverSearch")]
    public int CoverSearch { get; set; }

    // GunRange: gun and rocket runs close to this percent of the weapon's range.
    [JsonPropertyName("gunRange")]
    public int GunRange { get; set; }

    [JsonPropertyName("leash")]
    public int Leash { get; set; }

    // Spool: ticks the rotor takes to spool up before lift-off.
    [JsonPropertyName("spool")]
    public int Spool { get; set; }

    // Tempo (2026-10-24, the owner's faster airfield): percents; leaving (the spool-up and the lift-off) and returning (the flight
    // home to its pad and the descent onto it) faster than its own numbers (100: as authored).
    [JsonPropertyName("tempo")]
    public AirfieldTempoJson? Tempo { get; set; }

    [JsonPropertyName("outOfControl")]
    public OutOfControlJson? OutOfControl { get; set; }

    [JsonPropertyName("crew")]
    public HeliCrewJson? Crew { get; set; }

    [JsonPropertyName("riders")]
    public RidersJson? Riders { get; set; }
}

internal sealed class OutOfControlJson
{
    [JsonPropertyName("ticks")]
    public int Ticks { get; set; }

    [JsonPropertyName("hpPercent")]
    public int HpPercent { get; set; }

    [JsonPropertyName("speedPercent")]
    public int SpeedPercent { get; set; }

    // SpinRate: degrees a second.
    [JsonPropertyName("spinRate")]
    public int SpinRate { get; set; }

    // EndHeight: tenths of a metre over the ground it sinks to by the end of the time.
    [JsonPropertyName("endHeight")]
    public int EndHeight { get; set; }
}

internal sealed class HeliCrewJson
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    // Survive: percent each survives a crash landing.
    [JsonPropertyName("survive")]
    public int Survive { get; set; }

    // Unit: the archetype a survivor steps out as (the pilot).
    [JsonPropertyName("unit")]
    public string? Unit { get; set; }
}

internal sealed class RidersJson
{
    [JsonPropertyName("seats")]
    public int Seats { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("boardRange")]
    public int BoardRange { get; set; }

    [JsonPropertyName("dropHeight")]
    public int DropHeight { get; set; }

    [JsonPropertyName("dropTicks")]
    public int DropTicks { get; set; }

    [JsonPropertyName("survive")]
    public int Survive { get; set; }
}

// SpeedByMapJson: a fixed wing's cruise and attack speeds grow with the map (E13, D-SPD1): its own up to `from` cells
// across, rising linearly to `percent` (the dash to `dashPercent`) at `to` cells and no further.
internal sealed class SpeedByMapJson
{
    [JsonPropertyName("from")]
    public int From { get; set; }

    [JsonPropertyName("to")]
    public int To { get; set; }

    [JsonPropertyName("percent")]
    public int Percent { get; set; }

    [JsonPropertyName("dashPercent")]
    public int DashPercent { get; set; }
}

// EnduranceByMapJson: a tank (lifetime) that grows with the map (E13, D-FUEL1): the archetype's lifetime on maps up
// to `from` cells across (the longer side), rising linearly to `percent` of it at `to` cells and no further.
internal sealed class EnduranceByMapJson
{
    [JsonPropertyName("from")]
    public int From { get; set; }

    [JsonPropertyName("to")]
    public int To { get; set; }

    [JsonPropertyName("percent")]
    public int Percent { get; set; }
}

// HeightByMapJson: a flight height that grows with the map (E13, D-ALT1): flight.height on maps up to `from`
// cells across (the longer side), rising linearly to `height` (tenths of a metre) at `to` cells and on at the
// same slope beyond, never above `max`.
internal sealed class HeightByMapJson
{
    [JsonPropertyName("from")]
    public int From { get; set; }

    [JsonPropertyName("to")]
    public int To { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }
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

    // LookAhead: ticks of travel along its path the height planner looks ahead for terrain and structure tops (E12; 100: 5 s).
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

    // ExcludeTags: but never archetypes with one of these (the vehicle garage: no tanks).
    [JsonPropertyName("excludeTags")]
    public List<string>? ExcludeTags { get; set; }

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

    // UnloadInterval: ticks between passengers on an unload-all (0: all at once).
    [JsonPropertyName("unloadInterval")]
    public int UnloadInterval { get; set; }

    // PassengerExcludeTags: units with one of these tags never ride, even with a passengerTags tag (the heavy weapon soldiers).
    [JsonPropertyName("passengerExcludeTags")]
    public List<string>? PassengerExcludeTags { get; set; }

    // Door: where passengers get in ("rear": a point behind the hull by its facing, where they always get out); absent: at the
    // vehicle itself.
    [JsonPropertyName("door")]
    public string? Door { get; set; }

    // PassengerSurvive: survival bands for the passengers when the vehicle is destroyed, by the killing hit's damage before
    // armour, in rising order; the last has no "below" (the rest).
    [JsonPropertyName("passengerSurvive")]
    public List<SurviveBandJson>? PassengerSurvive { get; set; }

    // OccupantDamage: percent of a hit on the unit (one of occupantWarheads) its driver takes too (an open emplacement's crewman).
    [JsonPropertyName("occupantDamage")]
    public int OccupantDamage { get; set; }

    [JsonPropertyName("occupantWarheads")]
    public List<string>? OccupantWarheads { get; set; }
}

// SurviveBandJson: one passenger survival band (crew.passengerSurvive): a killing hit under `below` (damage before armour; 0 on
// the last band: any) gives each passenger `survival` percent to bail out.
internal sealed class SurviveBandJson
{
    [JsonPropertyName("below")]
    public int Below { get; set; }

    [JsonPropertyName("survival")]
    public int Survival { get; set; }
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

    [JsonPropertyName("smoke")]
    public SmokeJson? Smoke { get; set; }

    [JsonPropertyName("aps")]
    public ApsJson? Aps { get; set; }
}

// An active protection system (the tank's Trophy): charges; range (tenths of a cell) it intercepts at; cooldown (ticks) between
// engagements; the chance (percent) against a slow threat launched from afar; fastFrom and fastStep (tenths of a cell per
// second) and fastPenalty (percent a step, rounded up); closeRange (tenths of a cell) and closePenalty (percent at point blank);
// minChance (percent).
internal sealed class ApsJson
{
    [JsonPropertyName("charges")]
    public int Charges { get; set; }

    [JsonPropertyName("range")]
    public int Range { get; set; }

    [JsonPropertyName("cooldown")]
    public int Cooldown { get; set; }

    [JsonPropertyName("chance")]
    public int Chance { get; set; }

    [JsonPropertyName("fastFrom")]
    public int FastFrom { get; set; }

    [JsonPropertyName("fastStep")]
    public int FastStep { get; set; }

    [JsonPropertyName("fastPenalty")]
    public int FastPenalty { get; set; }

    [JsonPropertyName("closeRange")]
    public int CloseRange { get; set; }

    [JsonPropertyName("closePenalty")]
    public int ClosePenalty { get; set; }

    [JsonPropertyName("minChance")]
    public int MinChance { get; set; }
}

// Directional armour (a tank's): percent of the damage taken by a hit on the front, the sides, the rear and from above
// (top attack); frontArc and rearArc are the whole arcs in degrees (90: 45 degrees either side of the nose or the tail).
internal sealed class DirectionalArmorJson
{
    [JsonPropertyName("front")]
    public int Front { get; set; }

    [JsonPropertyName("side")]
    public int Side { get; set; }

    [JsonPropertyName("rear")]
    public int Rear { get; set; }

    [JsonPropertyName("top")]
    public int Top { get; set; }

    [JsonPropertyName("frontArc")]
    public int FrontArc { get; set; }

    [JsonPropertyName("rearArc")]
    public int RearArc { get; set; }
}

// An airlifter's vehicle hold: seats, the tags a vehicle needs to board, the ramp point behind its centre and the board range
// (tenths of a cell), the ticks a vehicle takes to board and between vehicles driving off.
internal sealed class AirliftJson
{
    [JsonPropertyName("seats")]
    public int Seats { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("ramp")]
    public int Ramp { get; set; }

    [JsonPropertyName("boardRange")]
    public int BoardRange { get; set; }

    [JsonPropertyName("boardTicks")]
    public int BoardTicks { get; set; }

    [JsonPropertyName("unloadTicks")]
    public int UnloadTicks { get; set; }
}

// Smoke launchers: charges (pops), radius (tenths of a cell), duration and cooldown (ticks).
internal sealed class SmokeJson
{
    [JsonPropertyName("charges")]
    public int Charges { get; set; }

    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("cooldown")]
    public int Cooldown { get; set; }
}

// Distances (range, radius, splash, scatter, contact) are in tenths of a
// cell, speeds in tenths of a cell per second; times (reload, everyTicks,
// cooldown, salvoInterval) are in ticks.
internal sealed class WeaponJson
{
    // Cone: degrees off a helicopter's nose it fires within (fixed missiles and rocket pods: a few; a chin turret: wide);
    // 0 anywhere.
    [JsonPropertyName("cone")]
    public int Cone { get; set; }

    // Ripple: a helicopter's ripple at several targets (HB; see RippleJson); absent: one target a shot.
    [JsonPropertyName("ripple")]
    public RippleJson? Ripple { get; set; }

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

    // A multi-target order's shot at one point: the whole salvo (true) or one round (false); unset: true for a salvo (salvoSize
    // above 1), false otherwise.
    [JsonPropertyName("volleyPerTarget")]
    public bool? VolleyPerTarget { get; set; }

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

    // Terminal: a missile's terminal profile (see TerminalJson; the hypersonic missile).
    [JsonPropertyName("terminal")]
    public TerminalJson? Terminal { get; set; }

    // Cruise: a cruise missile's route and terrain-following flight (see CruiseJson; the cruise missile).
    [JsonPropertyName("cruise")]
    public CruiseJson? Cruise { get; set; }

    // LowFlight: the low-flight sight rule for its projectiles (see LowFlightJson; the cruise missile).
    [JsonPropertyName("lowFlight")]
    public LowFlightJson? LowFlight { get; set; }

    // Signature: its projectiles' signature as air targets (see SignatureJson); absent: a normal target.
    [JsonPropertyName("signature")]
    public SignatureJson? Signature { get; set; }

    // Fire: what its blast sets on fire on top of its warhead's fire block (see WarheadFireJson; the hypersonic missile).
    [JsonPropertyName("fire")]
    public WarheadFireJson? Fire { get; set; }

    // Launches: the archetype each round is (a flying unit with a kaman block: the Kaman truck's rail); absent: none.
    [JsonPropertyName("launches")]
    public string? Launches { get; set; }

    // NoResupply: nothing refills it (a single-use drone station).
    [JsonPropertyName("noResupply")]
    public bool NoResupply { get; set; }

    // PointsOnly: a bomb dropped only on ground points (attack ground), never on a unit (the incendiary bomb).
    [JsonPropertyName("pointsOnly")]
    public bool PointsOnly { get; set; }

    // Upgrade: the upgrade a unit needs to carry it (its stock holds nothing without it); absent: always carried.
    [JsonPropertyName("upgrade")]
    public string? Upgrade { get; set; }

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

    // SideFire: a gunship's side cannon (see SideFireJson; H3).
    [JsonPropertyName("sideFire")]
    public SideFireJson? SideFire { get; set; }

    // Penetrator: a penetrating bomb's effect where it goes off (see PenetratorJson; the stealth bomber's bunker buster).
    [JsonPropertyName("penetrator")]
    public PenetratorJson? Penetrator { get; set; }

    // Blast: a ring blast where the bomb goes off (see BlastJson; the MOAB type bomb): damage by rings instead of the splash
    // falloff, tunnels collapsed and forest flattened within a radius.
    [JsonPropertyName("blast")]
    public BlastJson? Blast { get; set; }

    // Airdrop: a cargo pallet (see AirdropJson; the airlifter's): it carries supply to a drop zone instead of exploding.
    [JsonPropertyName("airdrop")]
    public AirdropJson? Airdrop { get; set; }

    // Runway: what its blast does to a runway it lands on (see RunwayEffectJson); absent: nothing.
    [JsonPropertyName("runway")]
    public RunwayEffectJson? Runway { get; set; }

    // SharedAmmo: it draws on the ammo of the weapon before it in the same component (its own ammo 0): two rounds of one gun.
    [JsonPropertyName("sharedAmmo")]
    public bool SharedAmmo { get; set; }

    // Coaxial: a coaxial mount (a tank's machine guns) firing on its own reload alongside the main weapon.
    [JsonPropertyName("coaxial")]
    public bool Coaxial { get; set; }

    // ApsThreat: an active protection system (aps) can engage its projectiles (guided anti-tank missiles, direct-fire rockets).
    [JsonPropertyName("apsThreat")]
    public bool ApsThreat { get; set; }

    // TruckReload: only cargo trucks reload it (the howitzer's shells): cost supply a round, one every everyTicks, from a truck
    // within range (tenths of a cell) of the receiver's footprint. Nothing else refills it.
    [JsonPropertyName("truckReload")]
    public TruckReloadJson? TruckReload { get; set; }

    // Reveal: ticks every shot shows the shooter to every enemy (the muzzle flash); 0 for none.
    [JsonPropertyName("reveal")]
    public int Reveal { get; set; }

    // GroupRadius: automatic targets with the most enemies around them within this (tenths of a cell) go first; GroupStructure:
    // what a structure counts for in that tally (1 when not set).
    [JsonPropertyName("groupRadius")]
    public int GroupRadius { get; set; }

    [JsonPropertyName("groupStructure")]
    public int GroupStructure { get; set; }
}

// RunwayEffectJson: either a crater on impact ("small" or "large") or wear added to the patch it
// lands in (and a share of it to the neighbouring patches), never both.
// AirdropJson: a cargo pallet (WeaponEffect.Airdrop; the airlifter's containers): the supply one carries, and the ticks the
// aircraft takes to unload one into an airfield's stock when it lands there (Deliver).
internal sealed class AirdropJson
{
    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("unloadTicks")]
    public int UnloadTicks { get; set; }
}

// BlastJson: a ring blast (WeaponEffect.Blast; the MOAB type bomb): its damage rings from the centre out (the last ring's radius is
// the weapon's splash), the radius within which every tunnel collapses (tenths of a cell; 0: none) and the radius within which the
// forest is knocked flat (tenths of a cell; 0: none) down to flattenTo (a forest density, 0 to 2; left out: 1, scrub).
internal sealed class BlastJson
{
    [JsonPropertyName("rings")]
    public List<BlastRingJson>? Rings { get; set; }

    [JsonPropertyName("tunnels")]
    public int Tunnels { get; set; }

    [JsonPropertyName("flatten")]
    public int Flatten { get; set; }

    [JsonPropertyName("flattenTo")]
    public int? FlattenTo { get; set; }
}

// BlastRingJson: one ring of a ring blast: its outer radius (tenths of a cell), the percent of the weapon's damage at its inner edge
// and at its outer edge (edge; left out: the same, a flat ring), and destroys: everything on the ground in it is destroyed whatever
// its armour, and whoever is inside (the penetrator's rule).
internal sealed class BlastRingJson
{
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("percent")]
    public int Percent { get; set; }

    [JsonPropertyName("edge")]
    public int? Edge { get; set; }

    [JsonPropertyName("destroys")]
    public bool Destroys { get; set; }
}

// PenetratorJson: a bunker buster's effect (WeaponEffect.Penetrator): the radius it destroys everything in (and kills underground),
// tenths of a cell; its deep crater's radius (tenths of a cell) and how long it stays (ticks).
internal sealed class PenetratorJson
{
    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("craterRadius")]
    public int CraterRadius { get; set; }

    [JsonPropertyName("craterTicks")]
    public int CraterTicks { get; set; }
}

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

    // Modes (H2): the fire modes, the first the default: { "id", "salvo" } (bombs a pass at each aim point).
    [JsonPropertyName("modes")]
    public List<FireModeJson>? Modes { get; set; }

    // Group (H2): queued aim points within this many tenths of a cell of a pass's first, along its line, share the pass.
    [JsonPropertyName("group")]
    public int Group { get; set; }

    // GroupAside (H2): and at most this far (tenths of a cell) off the line.
    [JsonPropertyName("groupAside")]
    public int GroupAside { get; set; }

    // Retard (H2): a high-drag bomb's horizontal speed lost a second, percent (0: a slick bomb).
    [JsonPropertyName("retard")]
    public int Retard { get; set; }

    // Overfly (H4): after a pass's last drop it flies on down its run line over that drop before it breaks (a high release).
    [JsonPropertyName("overfly")]
    public bool Overfly { get; set; }

    // Precision (the stealth bomber): no scatter, each bomb steered onto its own drop; queued aims share a pass's bombs out.
    [JsonPropertyName("precision")]
    public bool Precision { get; set; }

    // Aims: the most aims an aircraft carrying it may Shift-queue (0: 8).
    [JsonPropertyName("aims")]
    public int Aims { get; set; }

    // Tier: its blast's airstrike tier for renderers, 1 to 7 (0: by its damage).
    [JsonPropertyName("tier")]
    public int Tier { get; set; }

    // Straight (the MOAB type bomb): no parachute and no forward throw: the bomb leaves its aircraft with no horizontal speed and
    // falls straight down onto the point under the release (the aircraft releases over its aim).
    [JsonPropertyName("straight")]
    public bool Straight { get; set; }

    // Warn: from its release every enemy player is warned of the bomb's point and its blast (World.IncomingStrikes) until it lands.
    [JsonPropertyName("warn")]
    public bool Warn { get; set; }
}

// FireModeJson: a bomb weapon's fire mode (H2).
internal sealed class FireModeJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("salvo")]
    public int Salvo { get; set; }

    // Spread (H4, a carpet): the salvo is one stick spread along the whole pass (its aims, a line), not one at each aim.
    [JsonPropertyName("spread")]
    public bool Spread { get; set; }

    // Across (the supersonic bomber): bombs side by side in a row across the flight path (0 or 1: none); the salvo is whole rows.
    [JsonPropertyName("across")]
    public int Across { get; set; }

    // AcrossSpacing: tenths of a cell between a row's bombs (0: the delivery's stickSpacing).
    [JsonPropertyName("acrossSpacing")]
    public int AcrossSpacing { get; set; }
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

    // Ceiling (H3): tenths of a metre over the ground under a target above which it never engages it (0: none).
    [JsonPropertyName("ceiling")]
    public int Ceiling { get; set; }

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

    // RadarLinked (the Patriot-type launcher): no sensor of its own; it engages only what a friendly radar (a unit
    // with a ground radar: the radar truck, the radar site, the air base's radar) tracks within its weapon range.
    [JsonPropertyName("radarLinked")]
    public bool RadarLinked { get; set; }

    // Battery (the SA-6 type launcher): no sensor of its own; it engages only what a set-up radar of its side (a unit with an air
    // vision and the battery's radar tags) within the link radius of it tracks, and its missile may need that track in flight.
    [JsonPropertyName("battery")]
    public BatteryJson? Battery { get; set; }

    // Outright: a hit that brings an aircraft down destroys it outright (never out of control: the Patriot type against a
    // helicopter).
    [JsonPropertyName("outright")]
    public bool Outright { get; set; }

    // Salvo: per class, how many of the side's missiles may fly at one target at once (unlisted: 1); "ballistic": 2,
    // the shoot-shoot doctrine against a ballistic missile.
    [JsonPropertyName("salvo")]
    public Dictionary<string, int>? Salvo { get; set; }

    // DefendRadius: tenths of a cell. An arcing projectile (a missile or rocket on its arc) is engaged only when it will
    // come down within this of a unit or structure of the shooter's side (0: always).
    [JsonPropertyName("defendRadius")]
    public int DefendRadius { get; set; }

    // MinAltitude: tenths of a metre over the ground under a target below which it never engages it (the high-altitude
    // interceptor: lower threats are left to the Patriot type); 0: none.
    [JsonPropertyName("minAltitude")]
    public int MinAltitude { get; set; }

    // AltitudeFull: tenths of a metre over the ground at and above which its kill chance is whole; between minAltitude and
    // this it rises from altitudeLow percent of it. 0: no altitude term.
    [JsonPropertyName("altitudeFull")]
    public int AltitudeFull { get; set; }

    // AltitudeLow: the percent of its kill chance at minAltitude (with altitudeFull).
    [JsonPropertyName("altitudeLow")]
    public int AltitudeLow { get; set; } = 100;

    // MaxPerTarget: the most of its side's capped missiles (weapons with a maxPerTarget) launched at any one target over the
    // target's life, wherever it flies (salvo caps only those in flight at once); 0: no cap.
    [JsonPropertyName("maxPerTarget")]
    public int MaxPerTarget { get; set; }

    // Network: it takes the antenna network's warnings (see InterceptNetworkJson); radarLinked only. An empty block takes them with
    // no classes left to others.
    [JsonPropertyName("network")]
    public InterceptNetworkJson? Network { get; set; }
}

// An intercept block's battery link (the SA-6 type launcher).
internal sealed class BatteryJson
{
    // LinkRadius: tenths of a cell; a linked radar stands at most this far (level) from the launcher.
    [JsonPropertyName("linkRadius")]
    public int LinkRadius { get; set; }

    // RadarTags: the tags a linked radar carries (all of them); it must also have an air vision.
    [JsonPropertyName("radarTags")]
    public List<string>? RadarTags { get; set; }

    // SemiActive: the missile homes on the linked radar's illumination: it loses guidance (and misses) once no radar linked to
    // its launch point tracks its target.
    [JsonPropertyName("semiActive")]
    public bool SemiActive { get; set; }
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

    // FireAndForget: its own seeker homes it (a helicopter's anti-tank missile): no operator, no line from the shooter.
    [JsonPropertyName("fireAndForget")]
    public bool FireAndForget { get; set; }

    // LockAfterLaunch (HB, with fireAndForget): fired by a helicopter at a vehicle its own mast radar tracks, it needs no line
    // of sight: it lofts over the crests from behind cover and locks on after launch.
    [JsonPropertyName("lockAfterLaunch")]
    public bool LockAfterLaunch { get; set; }

    // DropDistance: tenths of a cell it flies on once unguided before hitting the ground.
    [JsonPropertyName("dropDistance")]
    public int DropDistance { get; set; }
}

// RippleJson: a helicopter's ripple (HB, Model.Ripple): one round at each of up to `targets` different targets (its target
// first), `interval` ticks apart, then the weapon's reload, the later rounds within `cone` degrees of the nose. targets and
// interval are required.
internal sealed class RippleJson
{
    [JsonPropertyName("targets")]
    public int? Targets { get; set; }

    [JsonPropertyName("interval")]
    public int? Interval { get; set; }

    // Cone: degrees off the nose its later rounds may go (optional; default and least: the weapon's cone).
    [JsonPropertyName("cone")]
    public int? Cone { get; set; }
}

// TerminalJson: a missile's terminal profile (Model.TerminalFlight). Every field is required but weave, weaves and boostTicks.
internal sealed class TerminalJson
{
    // BoostTicks: a vertical start (the Jericho type's missile): over its first boostTicks it leaves the launcher straight up and
    // pitches over onto its arc, its speed over the ground growing from 0 to the projectile speed. Optional; 0 (the default):
    // none, the arc starts at the launcher.
    [JsonPropertyName("boostTicks")]
    public int BoostTicks { get; set; }

    // ClimbTicks: ticks of its Climb phase off the launcher (the boost, up its arc).
    [JsonPropertyName("climbTicks")]
    public int? ClimbTicks { get; set; }

    // Peak: its peak's height over the ground, a percent of the ground range; peakMin and peakMax: the lowest and highest peak,
    // tenths of a metre; peakAt: where the peak is, a percent of the way from the launcher to the dive's start.
    [JsonPropertyName("peak")]
    public int? Peak { get; set; }

    [JsonPropertyName("peakMin")]
    public int? PeakMin { get; set; }

    [JsonPropertyName("peakMax")]
    public int? PeakMax { get; set; }

    [JsonPropertyName("peakAt")]
    public int? PeakAt { get; set; }

    // From: tenths of a cell from its point (horizontally) the terminal dive starts.
    [JsonPropertyName("from")]
    public int? From { get; set; }

    // Slope: the dive's chord, its height at the start over its length, a thousandth (1000: 45 degrees).
    [JsonPropertyName("slope")]
    public int? Slope { get; set; }

    // Speed: its speed in the dive, tenths of a cell per second.
    [JsonPropertyName("speed")]
    public int? Speed { get; set; }

    // Weave: the weave's largest swing across its line in the dive, tenths of a cell; weaves: whole waves over the dive.
    [JsonPropertyName("weave")]
    public int Weave { get; set; }

    [JsonPropertyName("weaves")]
    public int Weaves { get; set; }

    // Warn: ticks before the impact every enemy player is warned.
    [JsonPropertyName("warn")]
    public int? Warn { get; set; }
}

// CruiseJson: a cruise missile's route and terrain-following flight (Model.CruiseFlight). Every field is required.
internal sealed class CruiseJson
{
    // BoostTicks and Boost: ticks it climbs straight up off the launcher, and tenths of a metre above the launch point it reaches.
    [JsonPropertyName("boostTicks")]
    public int? BoostTicks { get; set; }

    [JsonPropertyName("boost")]
    public int? Boost { get; set; }

    // Height: tenths of a metre it flies above the ground and treetops (the look-ahead's highest).
    [JsonPropertyName("height")]
    public int? Height { get; set; }

    // LookAhead: tenths of a cell along its path ahead whose highest ground it keeps its height over.
    [JsonPropertyName("lookAhead")]
    public int? LookAhead { get; set; }

    // ClimbRate and DescendRate: the most it climbs and descends, tenths of a metre per second.
    [JsonPropertyName("climbRate")]
    public int? ClimbRate { get; set; }

    [JsonPropertyName("descendRate")]
    public int? DescendRate { get; set; }

    // TurnRadius: tenths of a cell, its turn at each waypoint.
    [JsonPropertyName("turnRadius")]
    public int? TurnRadius { get; set; }

    // PopUp: tenths of a metre above the ground it pops up to where the dive starts; DiveFrom: tenths of a cell from its point.
    [JsonPropertyName("popUp")]
    public int? PopUp { get; set; }

    [JsonPropertyName("diveFrom")]
    public int? DiveFrom { get; set; }

    // RouteMin and RouteMax: tenths of a cell, the shortest and longest route (launcher, waypoints, target) it takes.
    [JsonPropertyName("routeMin")]
    public int? RouteMin { get; set; }

    [JsonPropertyName("routeMax")]
    public int? RouteMax { get; set; }

    // Waypoints: the most waypoints a route has before its target.
    [JsonPropertyName("waypoints")]
    public int? Waypoints { get; set; }

    // Warn: ticks an enemy player stays warned of it after his sensors last saw it.
    [JsonPropertyName("warn")]
    public int? Warn { get; set; }
}

// LowFlightJson: the low-flight sight rule (Model.LowFlightSight): below `below` tenths of a metre over the ground a ground or
// vehicle sensor sees it only with a clear line over the terrain and treetops and within `sight` tenths of a cell.
internal sealed class LowFlightJson
{
    [JsonPropertyName("below")]
    public int? Below { get; set; }

    [JsonPropertyName("sight")]
    public int? Sight { get; set; }
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

    // NoService: a garage's service (the military garage, a forward airbase's field garage) never refills it: only a depot does
    // (the Jericho type's missile). Optional, default false.
    [JsonPropertyName("noService")]
    public bool NoService { get; set; }
}

// TruckReload: a weapon reloaded only by cargo trucks (see WeaponJson.TruckReload).
internal sealed class TruckReloadJson
{
    [JsonPropertyName("cost")]
    public int Cost { get; set; }

    [JsonPropertyName("everyTicks")]
    public int EveryTicks { get; set; }

    [JsonPropertyName("range")]
    public int Range { get; set; }
}

// Cargo: supply a truck carries (0 to amount: a howitzer takes part of it) and the ticks it takes to unload at a depot.
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

    // StartStock: the stock one placed finished (a scenario, the sandbox) starts with (default: stock).
    [JsonPropertyName("startStock")]
    public int? StartStock { get; set; }

    // Storage (logistics): a store only: no rearm, no reload bonus, no depot reloads; cargo trucks unload into it only on
    // an Unload order aimed at it.
    [JsonPropertyName("storage")]
    public bool Storage { get; set; }

    // CargoSource (logistics): cargo trucks load here (LoadCargo), out of the stock (an unlimited one: free).
    [JsonPropertyName("cargoSource")]
    public bool CargoSource { get; set; }

    // GroundOnly (logistics): it rearms only ground units (never anything that flies).
    [JsonPropertyName("groundOnly")]
    public bool GroundOnly { get; set; }

    // RoundCost (logistics): the stock a charge it gives costs (default 1; a depot-reloaded round costs its weapon's own).
    [JsonPropertyName("roundCost")]
    public int? RoundCost { get; set; }

    // RepairCost (logistics, an airfield's stock): the stock 100 HP of repair costs at its stations and garage.
    [JsonPropertyName("repairCost")]
    public int RepairCost { get; set; }

    // FuelCost (logistics, an airfield's stock): the stock a full tank costs at its stations.
    [JsonPropertyName("fuelCost")]
    public int FuelCost { get; set; }
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

// SquadJson: the infantry squad rules (Model.SquadRules). Every field is required. Distances in tenths of a cell, speeds in
// percent of the slowest member's, times in ticks.
internal sealed class SquadJson
{
    [JsonPropertyName("size")]
    public List<int>? Size { get; set; }

    [JsonPropertyName("need")]
    public List<string>? Need { get; set; }

    [JsonPropertyName("never")]
    public List<string>? Never { get; set; }

    [JsonPropertyName("roles")]
    public List<SquadRoleJson>? Roles { get; set; }

    [JsonPropertyName("noLead")]
    public List<string>? NoLead { get; set; }

    [JsonPropertyName("march")]
    public SquadMarchJson? March { get; set; }

    [JsonPropertyName("assault")]
    public SquadAssaultJson? Assault { get; set; }

    [JsonPropertyName("regroup")]
    public int? Regroup { get; set; }

    [JsonPropertyName("regroupTicks")]
    public int? RegroupTicks { get; set; }

    [JsonPropertyName("groupGap")]
    public int? GroupGap { get; set; }

    [JsonPropertyName("hold")]
    public SquadHoldJson? Hold { get; set; }

    [JsonPropertyName("ambush")]
    public SquadAmbushJson? Ambush { get; set; }
}

// SquadAmbushJson: the ambush (phase S3; optional, it needs hold: without it squads can't ambush). Every field is required.
internal sealed class SquadAmbushJson
{
    [JsonPropertyName("length")]
    public List<int>? Length { get; set; }

    [JsonPropertyName("radius")]
    public int? Radius { get; set; }

    [JsonPropertyName("cells")]
    public int? Cells { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("spot")]
    public int? Spot { get; set; }

    [JsonPropertyName("approach")]
    public int? Approach { get; set; }

    [JsonPropertyName("farEnd")]
    public int? FarEnd { get; set; }

    [JsonPropertyName("crowd")]
    public int? Crowd { get; set; }

    [JsonPropertyName("vehicles")]
    public List<string>? Vehicles { get; set; }

    [JsonPropertyName("rearmTicks")]
    public int? RearmTicks { get; set; }

    [JsonPropertyName("setTicks")]
    public int? SetTicks { get; set; }

    [JsonPropertyName("mines")]
    public SquadAmbushMinesJson? Mines { get; set; }

    [JsonPropertyName("roles")]
    public SquadAmbushRolesJson? Roles { get; set; }

    [JsonPropertyName("air")]
    public SquadAirAmbushJson? Air { get; set; }
}

// SquadAirAmbushJson: the air ambush (phase S4; optional: without it every ambush is a ground one). Every field is required; distances
// in tenths of a cell, times in ticks, weights [cover, trees, spacing, near].
internal sealed class SquadAirAmbushJson
{
    [JsonPropertyName("mostly")]
    public int? Mostly { get; set; }

    [JsonPropertyName("length")]
    public List<int>? Length { get; set; }

    [JsonPropertyName("gap")]
    public List<int>? Gap { get; set; }

    [JsonPropertyName("search")]
    public int? Search { get; set; }

    [JsonPropertyName("cells")]
    public int? Cells { get; set; }

    [JsonPropertyName("approach")]
    public int? Approach { get; set; }

    [JsonPropertyName("guard")]
    public List<int>? Guard { get; set; }

    [JsonPropertyName("trigger")]
    public int? Trigger { get; set; }

    [JsonPropertyName("volleyWait")]
    public int? VolleyWait { get; set; }

    [JsonPropertyName("airGuard")]
    public List<int>? AirGuard { get; set; }

    [JsonPropertyName("guards")]
    public List<int>? Guards { get; set; }
}

// SquadAmbushMinesJson: who lays, what and where (tenths of a cell).
internal sealed class SquadAmbushMinesJson
{
    [JsonPropertyName("layers")]
    public List<string>? Layers { get; set; }

    [JsonPropertyName("at")]
    public string? At { get; set; }

    [JsonPropertyName("ap")]
    public string? Ap { get; set; }

    [JsonPropertyName("trap")]
    public string? Trap { get; set; }

    [JsonPropertyName("trapsEach")]
    public int? TrapsEach { get; set; }

    [JsonPropertyName("atGap")]
    public int? AtGap { get; set; }

    [JsonPropertyName("apGap")]
    public int? ApGap { get; set; }

    [JsonPropertyName("apBand")]
    public List<int>? ApBand { get; set; }
}

// SquadAmbushRolesJson: each role's habits in the ambush (all seven required).
internal sealed class SquadAmbushRolesJson
{
    [JsonPropertyName("line")]
    public SquadAmbushRoleJson? Line { get; set; }

    [JsonPropertyName("medic")]
    public SquadAmbushRoleJson? Medic { get; set; }

    [JsonPropertyName("recon")]
    public SquadAmbushRoleJson? Recon { get; set; }

    [JsonPropertyName("flank")]
    public SquadAmbushRoleJson? Flank { get; set; }

    [JsonPropertyName("longFlank")]
    public SquadAmbushRoleJson? LongFlank { get; set; }

    [JsonPropertyName("airGuard")]
    public SquadAmbushRoleJson? AirGuard { get; set; }

    [JsonPropertyName("support")]
    public SquadAmbushRoleJson? Support { get; set; }
}

// SquadAmbushRoleJson: one role's weights (Hold's eight, then end, lane, approach, hidden) and limits (tenths of a cell).
internal sealed class SquadAmbushRoleJson
{
    [JsonPropertyName("weights")]
    public List<int>? Weights { get; set; }

    [JsonPropertyName("lateral")]
    public List<int>? Lateral { get; set; }

    [JsonPropertyName("back")]
    public int? Back { get; set; }

    [JsonPropertyName("apart")]
    public List<int>? Apart { get; set; }

    [JsonPropertyName("spread")]
    public int? Spread { get; set; }

    [JsonPropertyName("garrison")]
    public bool? Garrison { get; set; }

    [JsonPropertyName("end")]
    public string? End { get; set; }

    [JsonPropertyName("hide")]
    public bool? Hide { get; set; }
}

// SquadHoldJson: Hold and its position picking (phase S2; optional: without it squads can't hold). Every field is required.
internal sealed class SquadHoldJson
{
    [JsonPropertyName("radius")]
    public int? Radius { get; set; }

    [JsonPropertyName("cells")]
    public int? Cells { get; set; }

    [JsonPropertyName("threat")]
    public int? Threat { get; set; }

    [JsonPropertyName("threatRange")]
    public List<int>? ThreatRange { get; set; }

    [JsonPropertyName("laneMax")]
    public int? LaneMax { get; set; }

    [JsonPropertyName("laneSpread")]
    public int? LaneSpread { get; set; }

    [JsonPropertyName("cover")]
    public SquadHoldCoverJson? Cover { get; set; }

    [JsonPropertyName("order")]
    public List<string>? Order { get; set; }

    [JsonPropertyName("roles")]
    public SquadHoldRolesJson? Roles { get; set; }
}

// SquadHoldCoverJson: the cover points of each kind of cover (0 to 100; a cell's cover is their sum, at most 100).
internal sealed class SquadHoldCoverJson
{
    [JsonPropertyName("wall")]
    public int? Wall { get; set; }

    [JsonPropertyName("trunk")]
    public int? Trunk { get; set; }

    [JsonPropertyName("grove")]
    public int? Grove { get; set; }

    [JsonPropertyName("dense")]
    public int? Dense { get; set; }

    [JsonPropertyName("crest")]
    public int? Crest { get; set; }
}

// SquadHoldRolesJson: each role's habits in Hold (all seven required).
internal sealed class SquadHoldRolesJson
{
    [JsonPropertyName("line")]
    public SquadHoldRoleJson? Line { get; set; }

    [JsonPropertyName("medic")]
    public SquadHoldRoleJson? Medic { get; set; }

    [JsonPropertyName("recon")]
    public SquadHoldRoleJson? Recon { get; set; }

    [JsonPropertyName("flank")]
    public SquadHoldRoleJson? Flank { get; set; }

    [JsonPropertyName("longFlank")]
    public SquadHoldRoleJson? LongFlank { get; set; }

    [JsonPropertyName("airGuard")]
    public SquadHoldRoleJson? AirGuard { get; set; }

    [JsonPropertyName("support")]
    public SquadHoldRoleJson? Support { get; set; }
}

// SquadHoldRoleJson: one role's weights (cover, trees, sight, open, spacing, safety, range, near) and limits (tenths of a cell).
internal sealed class SquadHoldRoleJson
{
    [JsonPropertyName("weights")]
    public List<int>? Weights { get; set; }

    [JsonPropertyName("back")]
    public int? Back { get; set; }

    [JsonPropertyName("apart")]
    public List<int>? Apart { get; set; }

    [JsonPropertyName("spread")]
    public int? Spread { get; set; }

    [JsonPropertyName("garrison")]
    public bool? Garrison { get; set; }
}

// SquadRoleJson: the role of the classes with a tag (the first matching entry wins).
internal sealed class SquadRoleJson
{
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

// SquadMarchJson: March, a file along the path.
internal sealed class SquadMarchJson
{
    [JsonPropertyName("spacing")]
    public int? Spacing { get; set; }

    [JsonPropertyName("speed")]
    public int? Speed { get; set; }

    [JsonPropertyName("ahead")]
    public int? Ahead { get; set; }

    [JsonPropertyName("airGuardGap")]
    public int? AirGuardGap { get; set; }

    [JsonPropertyName("supportGap")]
    public int? SupportGap { get; set; }
}

// SquadAssaultJson: Assault, a line facing the move; the roles behind it ([back, gap] each).
internal sealed class SquadAssaultJson
{
    [JsonPropertyName("spacing")]
    public int? Spacing { get; set; }

    [JsonPropertyName("speed")]
    public int? Speed { get; set; }

    [JsonPropertyName("medic")]
    public List<int>? Medic { get; set; }

    [JsonPropertyName("recon")]
    public List<int>? Recon { get; set; }

    [JsonPropertyName("reconSide")]
    public int? ReconSide { get; set; }

    [JsonPropertyName("longFlank")]
    public List<int>? LongFlank { get; set; }

    [JsonPropertyName("airGuard")]
    public List<int>? AirGuard { get; set; }

    [JsonPropertyName("support")]
    public List<int>? Support { get; set; }
}
