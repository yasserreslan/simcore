using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// One typed effect of a component. Each kind maps to exactly one behavior
/// system in the sim; adding a mechanic means adding an effect kind and its
/// system.
/// </summary>
public abstract class Effect;

/// <summary>
/// A weapon. Used by the combat system, and by the projectile system for
/// weapons whose shots travel. A unit may carry several; it fires the one
/// that does the most damage per tick to the target's armor class.
/// </summary>
public sealed class WeaponEffect : Effect
{
    /// <summary>Damage per hit before armor.</summary>
    public required int Damage { get; init; }

    /// <summary>The maximum firing distance.</summary>
    public required Fixed Range { get; init; }

    /// <summary>Ticks between shots.</summary>
    public required int Reload { get; init; }

    /// <summary>The damage type.</summary>
    public required Warhead Warhead { get; init; }

    /// <summary>The splash radius; zero hits a single target.</summary>
    public required Fixed Splash { get; init; }

    /// <summary>The maximum landing offset per axis.</summary>
    public required Fixed Scatter { get; init; }

    /// <summary>The layers it can hit; the warhead must also be able to target the armor class.</summary>
    public required Layers Targets { get; init; }

    /// <summary>
    /// How far a shot travels per tick. Zero hits instantly; otherwise each shot
    /// is a projectile flying to where the target was at launch, so it can miss
    /// a moving target.
    /// </summary>
    public required Fixed ProjectileSpeed { get; init; }

    /// <summary>Shots per trigger pull: a launcher fires a salvo of several rockets, then reloads.</summary>
    public required int SalvoSize { get; init; }

    /// <summary>Ticks between the shots of a salvo.</summary>
    public required int SalvoInterval { get; init; }

    /// <summary>Salvos carried before it needs rearming (rockets, with <see cref="AmmoPerRocket"/>); zero means unlimited.</summary>
    public required int Ammo { get; init; }

    /// <summary>The minimum firing distance (artillery can't hit what is too close); zero for none.</summary>
    public Fixed MinRange { get; init; }

    /// <summary>Whether ammo counts single rockets (spent per shot, a salvo stops when empty) rather than salvos.</summary>
    public bool AmmoPerRocket { get; init; }

    /// <summary>Splash damage at the centre and at the edge of the splash, in percent of the damage.</summary>
    public int SplashCenter { get; init; } = 100;

    /// <summary>Splash damage at the edge of the splash, in percent of the damage.</summary>
    public int SplashEdge { get; init; } = 50;

    /// <summary>
    /// A two-ring splash when above zero: everything within this radius takes the full
    /// <see cref="SplashCenter"/> percent (no falloff), and from there out to <see cref="Splash"/>
    /// a flat <see cref="SplashOuterPercent"/>. Zero keeps the linear falloff.
    /// </summary>
    public Fixed SplashInner { get; init; }

    /// <summary>For a two-ring splash: the flat percent of the damage in the outer ring.</summary>
    public int SplashOuterPercent { get; init; }

    /// <summary>
    /// Set when only a supply depot reloads it: one round every
    /// <see cref="DepotReload.EveryTicks"/> while in a depot's radius, starting
    /// <see cref="DepotReload.AfterFireTicks"/> after the unit last fired, never while firing.
    /// </summary>
    public DepotReload? DepotReload { get; init; }

    /// <summary>
    /// Auto-acquire limits per armor class index (Fixed.Zero where unlimited): without an explicit
    /// attack order the unit only engages a target of that class within this distance, and such
    /// targets come after every unrestricted one.
    /// </summary>
    public IReadOnlyList<Fixed> AutoRange { get; init; } = [];

    /// <summary>Without an attack order, never fire at a target of an <see cref="AutoRange"/> class that is moving away.</summary>
    public bool AutoSkipRetreating { get; init; }

    /// <summary>Without an attack order, give up on a target after this many shots in a row that missed it; 0 never.</summary>
    public int MaxMisses { get; init; }

    /// <summary>Ticks the shooter aims, standing still, before every shot (0: fires at once).</summary>
    public int AimTicks { get; init; }

    /// <summary>A ballistic arc's apex in percent of the distance; 0 for the arc by salvo size and range.</summary>
    public int ArcPercent { get; init; }

    /// <summary>
    /// Whether its projectiles are lobbed (launcher salvos, and anything with its own arc): they
    /// fly over what is in the way and come down on the aim point, and need no line of fire.
    /// </summary>
    public bool Lobbed => SalvoSize > 1 || ArcPercent > 0 || Drone is not null;

    /// <summary>What an interceptor would need to stop its projectiles (none yet can).</summary>
    public InterceptClass Intercept { get; init; }

    /// <summary>Set for a one-way attack drone: its flight (climb, cruise above the map, dive).</summary>
    public DroneFlight? Drone { get; init; }

    /// <summary>Whether nothing refills it (a single-use drone station).</summary>
    public bool NoResupply { get; init; }

    /// <summary>Set for a guided missile (an ATGM): it steers toward its target in flight.</summary>
    public Guidance? Guided { get; init; }

    /// <summary>
    /// Set for an air-defence missile (the AA soldier's): it is never picked by ordinary combat;
    /// the air defence system tracks, locks and fires it at air targets of its classes, it flies
    /// as a 3D guided missile (<see cref="Guided"/>'s turn rate and max flight), and a proximity
    /// fuse plus a deterministic kill roll decide the outcome.
    /// </summary>
    public Interceptor? Interceptor { get; init; }

    /// <summary>
    /// Target priority per armor class index (lower first; 0 for every class when empty): without
    /// an attack order the unit picks the nearest target of the best class.
    /// </summary>
    public IReadOnlyList<int> Priority { get; init; } = [];
}

/// <summary>
/// How a guided missile flies. Each tick, while guided, it turns toward its target by at most
/// <see cref="TurnPermille"/> of its speed (the change of its velocity, all integer). Guidance
/// ends for good when it has flown <see cref="MaxFlight"/>, when the target is dead, gone into a
/// vehicle or building, or farther than MaxFlight from the shooter, when the shooter dies, and
/// (with <see cref="RequiresOperatorStill"/>) when the shooter moves or is given another order.
/// It then flies straight on for <see cref="DropDistance"/> and goes off on the ground. It hits
/// only its target (and only while guided) or blocked terrain, never anything else in its path.
/// </summary>
/// <param name="MaxFlight">The farthest it flies guided.</param>
/// <param name="TurnPermille">The largest change of velocity per tick, in thousandths of its speed.</param>
/// <param name="RequiresOperatorStill">Whether the shooter must stand still and keep the attack to guide it (SACLOS).</param>
/// <param name="DropDistance">How far it flies on once unguided before it hits the ground.</param>
public sealed record Guidance(Fixed MaxFlight, int TurnPermille, bool RequiresOperatorStill, Fixed DropDistance);

/// <summary>
/// An air-defence missile's engagement and kill model (see the air defence design). Percents are
/// 0 to 100; the kill chance is worked out in permille when the fuse goes off.
/// </summary>
/// <param name="Classes">Bit (1 &lt;&lt; class) set for each <see cref="InterceptClass"/> it engages.</param>
/// <param name="LockTicks">Ticks of continuous tracking before it may fire.</param>
/// <param name="Fuse">The proximity fuse's radius.</param>
/// <param name="KillTail">Kill chance, percent, with the target flying straight away from the missile.</param>
/// <param name="KillHead">Kill chance, percent, with the target flying straight at the missile.</param>
/// <param name="CrossHalf">The crossing rate seen at launch (milliradians per second) that halves the chance.</param>
/// <param name="SpeedRef">The target speed (per tick) above which the chance falls in proportion.</param>
/// <param name="RangeFar">Percent of the chance left for a shot at the weapon's full range.</param>
/// <param name="TrackBonus">Percent added for tracking TrackFull ticks beyond the lock.</param>
/// <param name="TrackFull">Ticks of tracking beyond the lock for the full bonus.</param>
/// <param name="KillMin">The lowest kill chance, percent.</param>
/// <param name="KillMax">The highest kill chance, percent.</param>
public sealed record Interceptor(int Classes, int LockTicks, Fixed Fuse, int KillTail, int KillHead, int CrossHalf, Fixed SpeedRef,
    int RangeFar, int TrackBonus, int TrackFull, int KillMin, int KillMax)
{
    /// <summary>Whether it engages projectiles of class c.</summary>
    public bool Engages(InterceptClass c) => c != InterceptClass.None && (Classes & (1 << (int)c)) != 0;
}

/// <summary>How a weapon reloads at a supply depot.</summary>
/// <param name="EveryTicks">Ticks per round reloaded.</param>
/// <param name="AfterFireTicks">Ticks after the last shot before reloading starts.</param>
/// <param name="Cost">Supply taken from the depot's stock per round reloaded (a depot with less waits); 0 is free.</param>
/// <param name="Stowed">Whether it reloads only while stowed: a deployed launcher at a depot packs up first.</param>
public sealed record DepotReload(int EveryTicks, int AfterFireTicks, int Cost = 0, bool Stowed = false);

/// <summary>
/// Cargo a truck carries to a friendly supply depot and unloads into its stock, all or nothing:
/// the truck is either full (Amount) or empty. Used by the cargo system.
/// </summary>
public sealed class CargoEffect : Effect
{
    /// <summary>Supply carried when full.</summary>
    public required int Amount { get; init; }

    /// <summary>Ticks of standing still in the depot's radius to unload.</summary>
    public required int UnloadTicks { get; init; }
}

/// <summary>
/// An aura around its unit: stat modifiers for units in the radius, and
/// healing every <see cref="EveryTicks"/> ticks. Loaded, but no system runs it
/// yet; no Phase 1 content uses it.
/// </summary>
public sealed class AuraEffect : Effect
{
    /// <summary>The radius.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>The healing period in ticks.</summary>
    public required int EveryTicks { get; init; }

    /// <summary>Hit points healed per period, never above max HP.</summary>
    public required int Heal { get; init; }

    /// <summary>Buffs (or debuffs) for units in the radius.</summary>
    public required IReadOnlyList<Modifier> Modifiers { get; init; }

    /// <summary>Allies or enemies.</summary>
    public required Relation Affects { get; init; }

    /// <summary>Only units with one of these tags (any unit if empty).</summary>
    public required TagSet AffectsTags { get; init; }
}

/// <summary>Places an entity, like a trap, at a target cell. Used by the ability system.</summary>
public sealed class PlacerEffect : Effect
{
    /// <summary>The archetype placed, as an index into <see cref="ContentSet.Archetypes"/>.</summary>
    public required int Archetype { get; init; }

    /// <summary>How many it can place in a match.</summary>
    public required int Charges { get; init; }

    /// <summary>Ticks between placements.</summary>
    public required int Cooldown { get; init; }

    /// <summary>The maximum distance from the unit to the target cell's center.</summary>
    public required Fixed Range { get; init; }

    /// <summary>The most entities one unit may have placed at a time; placing another removes its oldest.</summary>
    public required int MaxActive { get; init; }

    /// <summary>Ticks the unit stands at the spot placing it (0: at once).</summary>
    public int PlaceTicks { get; init; }

    /// <summary>Set when only a supply depot refills the kit (a round per EveryTicks, for Cost).</summary>
    public DepotReload? DepotReload { get; init; }
}

/// <summary>
/// Launches an autonomous unit, like an FPV drone, that flies to a target unit
/// or cell on its own. Charges are restocked by supply. Used by the ability system.
/// </summary>
public sealed class LaunchEffect : Effect
{
    /// <summary>The archetype launched, as an index into <see cref="ContentSet.Archetypes"/>.</summary>
    public required int Archetype { get; init; }

    /// <summary>How many it carries when full.</summary>
    public required int Charges { get; init; }

    /// <summary>Ticks between launches.</summary>
    public required int Cooldown { get; init; }

    /// <summary>The maximum distance from the unit to the target when launching.</summary>
    public required Fixed Range { get; init; }
}

/// <summary>
/// Makes its unit explode on contact with the target it was launched at. Used
/// by the strike system, for drones.
/// </summary>
public sealed class StrikeEffect : Effect
{
    /// <summary>How close to the target the unit must get to explode.</summary>
    public required Fixed Contact { get; init; }

    /// <summary>Damage before armor.</summary>
    public required int Damage { get; init; }

    /// <summary>The damage type.</summary>
    public required Warhead Warhead { get; init; }

    /// <summary>The blast radius.</summary>
    public required Fixed Splash { get; init; }
}

/// <summary>
/// Rearms friendly units in a radius (launcher salvos, drone and trap charges)
/// and makes them reload faster. A supplier with limited stock spends it and is
/// restocked by suppliers that restock. Used by the supply system.
/// </summary>
public sealed class SupplyEffect : Effect
{
    /// <summary>The radius.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>The rearm period in ticks.</summary>
    public required int EveryTicks { get; init; }

    /// <summary>Salvos or charges given to each stock of each unit per period.</summary>
    public required int Amount { get; init; }

    /// <summary>Salvos and charges carried when full; zero means unlimited.</summary>
    public required int Stock { get; init; }

    /// <summary>The stock a worker-built supplier starts with (a scenario's starts with <see cref="Stock"/>).</summary>
    public int ConstructedStock { get; init; }

    /// <summary>Stock given to each friendly supplier with limited stock in the radius per period.</summary>
    public required int Restock { get; init; }

    /// <summary>Percent added to the reload speed of friendly units in the radius.</summary>
    public required int ReloadBonus { get; init; }
}

/// <summary>
/// Fires once when an enemy ground unit comes within the radius, then removes
/// its entity. Flying units never set it off. Used by the trigger system, for
/// trap archetypes.
/// </summary>
public sealed class TriggerEffect : Effect
{
    /// <summary>The detection radius.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>Damage before armor.</summary>
    public required int Damage { get; init; }

    /// <summary>The damage type.</summary>
    public required Warhead Warhead { get; init; }

    /// <summary>The blast radius; zero hits only the nearest triggering unit.</summary>
    public required Fixed Splash { get; init; }

    /// <summary>Only units with one of these tags set it off (any unit if empty).</summary>
    public required TagSet Affects { get; init; }
}

/// <summary>Builds construction sites. Loaded, but no system runs it until Phase 2.</summary>
public sealed class BuilderEffect : Effect
{
    /// <summary>Construction rate in percent; 100 builds at the archetype's nominal build time.</summary>
    public required int Rate { get; init; }

    /// <summary>The maximum distance from the unit to the site's footprint.</summary>
    public required Fixed Range { get; init; }
}

/// <summary>
/// Speeds up nearby construction and unlocks structure variants. Loaded, but
/// no system runs it until Phase 2.
/// </summary>
public sealed class BuildAssistEffect : Effect
{
    /// <summary>Percent added to the builders' rate on sites in the radius.</summary>
    public required int Bonus { get; init; }

    /// <summary>The maximum distance from the unit to the site's footprint.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>Structure archetypes unlocked for the player, as indices into <see cref="ContentSet.Archetypes"/>.</summary>
    public required IReadOnlyList<int> Unlocks { get; init; }
}

/// <summary>Repairs nearby friendly units, never above max HP. Used by the repair system.</summary>
public sealed class RepairEffect : Effect
{
    /// <summary>Hit points restored per period.</summary>
    public required int Rate { get; init; }

    /// <summary>The repair period in ticks.</summary>
    public required int EveryTicks { get; init; }

    /// <summary>The radius.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>Only units with one of these tags (any unit if empty).</summary>
    public required TagSet Affects { get; init; }
}

/// <summary>A permanent stat change for its unit (perks), applied when the unit definition is compiled.</summary>
public sealed class ModifierEffect : Effect
{
    /// <summary>The change.</summary>
    public required Modifier Modifier { get; init; }
}

/// <summary>
/// The interception class of a weapon's projectiles: a hook for future interceptors (a point
/// defence that can stop rockets, say, but not a fast high ballistic missile).
/// </summary>
public enum InterceptClass : byte
{
    /// <summary>Not marked.</summary>
    None,

    /// <summary>A ballistic missile: fast, from a high apex; hard to intercept.</summary>
    Ballistic,

    /// <summary>A slow one-way attack drone cruising high: air defence's natural prey.</summary>
    Drone,
}

/// <summary>
/// A one-way attack drone's flight: off its rail at LaunchHeight, climbing ClimbPerTick a tick to
/// CruiseAboveMax over the map's highest ground, then a straight dive at DivePermille onto its
/// point. Heights are raw (512 a metre).
/// </summary>
public sealed record DroneFlight(long CruiseAboveMax, long ClimbPerTick, int BoosterTicks, int DivePermille, long LaunchHeight);
