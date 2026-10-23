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

    /// <summary>The maximum landing offset per axis (at full range when <see cref="ScatterNear"/> is set).</summary>
    public required Fixed Scatter { get; init; }

    /// <summary>
    /// When above zero the scatter grows with the distance: this at the minimum range (or point
    /// blank), rising linearly to <see cref="Scatter"/> at full range (see World.ScatterAt).
    /// </summary>
    public Fixed ScatterNear { get; init; }

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
    public bool Lobbed => SalvoSize > 1 || ArcPercent > 0 || Drone is not null || Launches >= 0;

    /// <summary>What an interceptor would need to stop its projectiles (none yet can).</summary>
    public InterceptClass Intercept { get; init; }

    /// <summary>Set for a one-way attack drone: its flight (climb, cruise above the map, dive).</summary>
    public DroneFlight? Drone { get; init; }

    /// <summary>
    /// The archetype (an index into <see cref="ContentSet.Archetypes"/>) of the unit each round is, or -1: a launcher
    /// whose round is a flying unit with a <see cref="Archetype.Kaman"/> block (the Kaman truck's rail, content
    /// <c>launches</c>). It fires like a one-way drone's rail (an attack or attack-ground order, a round per target), but
    /// the round leaves as that unit, sent at the target unit or the point (the Kaman system).
    /// </summary>
    public int Launches { get; init; } = -1;

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
    /// Set for an air-to-ground strike missile (the Watcher drone's): it is never picked by ordinary
    /// combat; a flying unit with a flight block fires it on an attack order at a ground target in
    /// its own sight (the watcher system), and its seeded hit roll and outcome table decide the rest.
    /// </summary>
    public AirStrike? AirStrike { get; init; }

    /// <summary>What its blast does to a runway it lands on (a crater or wear), or null for nothing.</summary>
    public RunwayEffect? Runway { get; init; }

    /// <summary>
    /// Set for an aircraft's gun against air targets (the jets', E8): the cone, the per-class hit chance
    /// of a burst and its terms. Data now; the air-to-air system (E6) fires it. Never picked by ordinary combat.
    /// </summary>
    public AirGun? AirGun { get; init; }

    /// <summary>
    /// Set for an aircraft's bomb (the jets', E8): its delivery profile, release, scatter and guidance.
    /// Data now; the air-to-ground system (E7) drops it. Never picked by ordinary combat.
    /// </summary>
    public Delivery? Delivery { get; init; }

    /// <summary>
    /// Set for a gunship's side cannon (H3): it fires sideways only while its aircraft orbits its area on the circle, at
    /// targets inside the area (or seeded points there), its rounds scattered. Never picked by ordinary combat.
    /// </summary>
    public SideFire? SideFire { get; init; }

    /// <summary>
    /// Set for a penetrating bomb (the stealth bomber's bunker buster; content <c>penetrator</c>): where it goes off it destroys
    /// everything on the ground within <see cref="Model.Penetrator.RadiusRaw"/> whatever its armour (structures, vehicles, soldiers,
    /// parked aircraft, and whoever is inside them), kills every unit underground in a tunnel there (the only weapon that can) and
    /// collapses the tunnel entrances it reaches into rubble; it leaves a deep crater (<see cref="CraterKind.Deep"/>). Null for
    /// every other weapon.
    /// </summary>
    public Penetrator? Penetrator { get; init; }

    /// <summary>
    /// Set for a cargo pallet (the airlifter's containers; content <c>airdrop</c>): dropped by an Airdrop order on a drop zone
    /// (<see cref="Sim.CommandKind.Airdrop"/>), it lands under its parachute and adds <see cref="Model.Airdrop.Amount"/> supply to
    /// the stock of the drop zone it lands in, or is lost outside every zone; never a blast. Unloaded on the ground by a Deliver
    /// order (<see cref="Sim.CommandKind.Deliver"/>) at an airfield with a stock. Null for every other weapon.
    /// </summary>
    public Airdrop? Airdrop { get; init; }

    /// <summary>
    /// Target priority per armor class index (lower first; 0 for every class when empty): without
    /// an attack order the unit picks the nearest target of the best class.
    /// </summary>
    public IReadOnlyList<int> Priority { get; init; } = [];

    /// <summary>
    /// A helicopter's mount (content <c>cone</c>, degrees): it fires only at a target within this of its nose (fixed
    /// missiles and rocket pods: a few degrees; a chin turret: wide). 0: anywhere (every other weapon).
    /// </summary>
    public int Cone { get; init; }

    /// <summary>
    /// Whether it draws on the ammo of the weapon before it in the same component (content <c>sharedAmmo</c>; its own
    /// <see cref="Ammo"/> is 0): two rounds of one gun from one magazine (the tank's armour-piercing and high-explosive rounds,
    /// picked by target from the same 40).
    /// </summary>
    public bool SharedAmmo { get; init; }

    /// <summary>
    /// A coaxial mount (content <c>coaxial</c>: a tank's machine guns): it fires on its own reload clock
    /// (<see cref="Sim.Unit.CoaxReload"/>) at the unit's current target alongside its main weapon, whenever it can hit it, is in
    /// reach and the turret is on it; it is picked as the unit's weapon only when no other weapon can fire at the target (the
    /// main gun empty or out of reach).
    /// </summary>
    public bool Coaxial { get; init; }

    /// <summary>
    /// Whether an active protection system (<see cref="ApsEffect"/>: the tank's Trophy) can engage its projectiles (content
    /// <c>apsThreat</c>): guided anti-tank missiles and direct-fire rockets. Never bullets, shells, bombs or artillery.
    /// </summary>
    public bool ApsThreat { get; init; }
}

/// <summary>
/// An active protection system (the tank's Trophy type; reusable by any ground unit; content <c>aps</c>): it engages the
/// incoming projectiles of <see cref="WeaponEffect.ApsThreat"/> weapons aimed at its unit once they come within
/// <see cref="Range"/> (plus their step that tick), one engagement every <see cref="Cooldown"/> ticks, each spending a charge
/// whether it succeeds or not. The chance to stop the threat starts at <see cref="Chance"/> and falls for fast threats
/// (<see cref="FastPenalty"/> per <see cref="FastStep"/> of speed above <see cref="FastFrom"/>, rounded up) and for threats
/// launched within <see cref="CloseRange"/> (up to <see cref="ClosePenalty"/> at point blank, in proportion), never below
/// <see cref="MinChance"/>; a seeded roll decides. Charges refill where weapons rearm. Run by the APS system.
/// </summary>
public sealed class ApsEffect : Effect
{
    /// <summary>Charges carried when full.</summary>
    public required int Charges { get; init; }

    /// <summary>How far out it intercepts a threat (from the unit's centre).</summary>
    public required Fixed Range { get; init; }

    /// <summary>Ticks after an engagement before it can engage again (a salvo overwhelms it).</summary>
    public required int Cooldown { get; init; }

    /// <summary>The chance (percent) to stop a slow threat launched from afar.</summary>
    public required int Chance { get; init; }

    /// <summary>The threat speed (per tick) above which the chance falls.</summary>
    public required Fixed FastFrom { get; init; }

    /// <summary>Each step of speed (per tick) above <see cref="FastFrom"/>, rounded up, costs <see cref="FastPenalty"/>.</summary>
    public required Fixed FastStep { get; init; }

    /// <summary>Percent lost per speed step.</summary>
    public required int FastPenalty { get; init; }

    /// <summary>A threat launched closer than this has less time to be stopped.</summary>
    public required Fixed CloseRange { get; init; }

    /// <summary>Percent lost for a threat launched at point blank, in proportion out to <see cref="CloseRange"/>.</summary>
    public required int ClosePenalty { get; init; }

    /// <summary>The lowest chance, percent.</summary>
    public required int MinChance { get; init; }
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
/// <param name="FireAndForget">Its own seeker homes it (a helicopter's anti-tank missile): it needs no operator, no line from
/// the shooter and no shooter alive once fired; it steers at its target until it hits, the target is gone or its flight ends.</param>
public sealed record Guidance(Fixed MaxFlight, int TurnPermille, bool RequiresOperatorStill, Fixed DropDistance, bool FireAndForget = false);

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
/// <param name="LeadTicks">Lead pursuit: the missile steers at the target's position plus its velocity times the time to go, at most this many ticks ahead (0: pure pursuit).</param>
/// <param name="LaunchHeight">Height above the shooter's ground (raw) it sees from and launches from: a vehicle's launcher; 0 is a soldier's eye (or a window in a building).</param>
/// <param name="Hits">Per-class hit rules (<see cref="InterceptHit"/>): for a class listed here the fuse rolls the flat hit chance
/// and a hit deals a share of the target's max HP, instead of the kill model above. Null or empty: none (every class uses the kill model).</param>
public sealed record Interceptor(int Classes, int LockTicks, Fixed Fuse, int KillTail, int KillHead, int CrossHalf, Fixed SpeedRef,
    int RangeFar, int TrackBonus, int TrackFull, int KillMin, int KillMax, int LeadTicks = 0, long LaunchHeight = 0,
    IReadOnlyList<InterceptHit>? Hits = null)
{
    /// <summary>Whether it engages air targets of class c.</summary>
    public bool Engages(InterceptClass c) => c != InterceptClass.None && (Classes & (1 << (int)c)) != 0;

    /// <summary>
    /// Whether it engages flying units (an aircraft class: scout, strike or surveillance drones, jets), not
    /// only projectiles. Set by the loader from the air classes' kinds.
    /// </summary>
    public bool EngagesUnits { get; init; }

    /// <summary>
    /// Per-class terms of the kill model (content <c>killBest</c>, <c>speedRefs</c>, <c>hitDamage</c>), at
    /// most one per class, in class order; a class not listed takes the defaults (its best shot KillMax,
    /// the weapon's SpeedRef, a successful roll kills).
    /// </summary>
    public IReadOnlyList<InterceptClassRule> Rules { get; init; } = [];

    /// <summary>Its seeker (content <c>seeker</c>): what countermeasures decoy it (E6). None: not declared.</summary>
    public Seeker Seeker { get; init; }

    /// <summary>
    /// A launch cone, degrees off the shooter's velocity (content <c>boresight</c>): an aircraft's missile
    /// may lock only a target within it (E6); 0 for a ground launcher (it turns to the target).
    /// </summary>
    public int Boresight { get; init; }

    /// <summary>
    /// Its height ceiling (content <c>ceiling</c>, H3; raw, 512 a metre): it never engages an air target flying higher than
    /// this over the ground under it (the AA soldier against the gunship's orbit). 0: none (only its slant range limits it).
    /// </summary>
    public long CeilingRaw { get; init; }

    /// <summary>
    /// Whether it has no sensor of its own (content <c>radarLinked</c>: the Patriot-type launcher): it engages only what a
    /// friendly radar (a unit with a ground radar) tracks within its weapon range.
    /// </summary>
    public bool RadarLinked { get; init; }

    /// <summary>
    /// The battery link (content <c>battery</c>: the SA-6 type launcher), or null. With it the launcher has no sensor of its own: it
    /// engages only a target that a set-up radar of its side carrying the battery's radar tags tracks, and only while that radar
    /// stands within the link radius of it; a semi-active missile loses its guidance once no such radar tracks its target.
    /// </summary>
    public Battery? Battery { get; init; }

    /// <summary>Per class, how many of the side's missiles may fly at one target at once (content <c>salvo</c>), in class order; unlisted: 1.</summary>
    public IReadOnlyList<(InterceptClass Class, int Count)> Salvo { get; init; } = [];

    /// <summary>
    /// An arcing projectile is engaged only when it will come down within this (raw) of a unit or structure of the
    /// shooter's side (content <c>defendRadius</c>); 0: always.
    /// </summary>
    public long DefendRadiusRaw { get; init; }

    /// <summary>
    /// Whether a hit that brings an aircraft down destroys it outright (content <c>outright</c>: the Patriot type against a
    /// helicopter): it never leaves a helicopter out of control. Without it a helicopter brought down goes out of control.
    /// </summary>
    public bool Outright { get; init; }

    /// <summary>The lowest height over the ground (raw) of a target it engages (content <c>minAltitude</c>); 0: none.</summary>
    public long MinAltitudeRaw { get; init; }

    /// <summary>
    /// The height over the ground (raw) at and above which its kill chance is whole (content <c>altitudeFull</c>); below it, down
    /// to <see cref="MinAltitudeRaw"/>, the chance falls to <see cref="AltitudeLow"/> percent. 0: no altitude term.
    /// </summary>
    public long AltitudeFullRaw { get; init; }

    /// <summary>The percent of its kill chance at <see cref="MinAltitudeRaw"/> (content <c>altitudeLow</c>).</summary>
    public int AltitudeLow { get; init; } = 100;

    /// <summary>
    /// The most of its side's capped missiles (those of weapons with a cap) launched at any one target over the target's life
    /// (content <c>maxPerTarget</c>: the high-altitude interceptor fires at most 2 at a ballistic missile, ever); a target at the
    /// cap is left to other launchers. 0: no cap (only <see cref="Salvo"/> limits it).
    /// </summary>
    public int MaxPerTarget { get; init; }

    /// <summary>How many of the side's missiles may fly at one target of class c at once (<see cref="Salvo"/>).</summary>
    public int SalvoOn(InterceptClass c)
    {
        for (var i = 0; i < Salvo.Count; i++)
        {
            if (Salvo[i].Class == c)
            {
                return Salvo[i].Count;
            }
        }
        return 1;
    }

    /// <summary>The per-class rule for c, or null (the defaults).</summary>
    public InterceptClassRule? RuleOn(InterceptClass c)
    {
        for (var i = 0; i < Rules.Count; i++)
        {
            if (Rules[i].Class == c)
            {
                return Rules[i];
            }
        }
        return null;
    }

    /// <summary>The target speed (raw a tick) above which its chance against class c falls in proportion.</summary>
    public Fixed SpeedRefOn(InterceptClass c) => RuleOn(c) is { SpeedRef.Raw: > 0 } r ? r.SpeedRef : SpeedRef;

    /// <summary>The share (percent) of a flying unit's max HP a successful kill roll takes against class c (100: it kills).</summary>
    public int HitDamageOn(InterceptClass c) => RuleOn(c) is { HitDamage: > 0 } r ? r.HitDamage : 100;

    /// <summary>The hit rule for class c (<see cref="Hits"/>), or null when that class uses the kill model.</summary>
    public InterceptHit? HitOn(InterceptClass c)
    {
        if (Hits is null)
        {
            return null;
        }
        for (var i = 0; i < Hits.Count; i++)
        {
            if (Hits[i].Class == c)
            {
                return Hits[i];
            }
        }
        return null;
    }

    /// <summary>The best kill chance (percent) against class c: its own KillMax unless a cap is set for it.</summary>
    public int Best(InterceptClass c) => RuleOn(c) is { Best: > 0 } r ? r.Best : KillMax;
}

/// <summary>
/// An air defence launcher's battery link (content <c>battery</c>; docs/design.md, "SA-6 launcher"): the launcher fires only at an
/// air target that a linked radar tracks. A linked radar is a unit of the launcher's side with an air vision whose tags include
/// every one of <paramref name="RadarTags"/>, set up (a unit that sets up), alive, built and not inside, standing within
/// <paramref name="LinkRadiusRaw"/> (level distance) of the launcher; it tracks a target within its own air vision, by the target's
/// signature (as team cueing). With <paramref name="SemiActive"/> the missile homes on the radar's illumination: once no radar
/// linked to its launch point tracks its target it flies on unguided and cannot fuse (it misses).
/// </summary>
/// <param name="LinkRadiusRaw">The radio link's reach (raw): a linked radar stands at most this far from the launcher.</param>
/// <param name="RadarTags">The tags a linked radar carries (all of them).</param>
/// <param name="SemiActive">Whether the missile needs a linked radar's track all the way to the fuse.</param>
public sealed record Battery(long LinkRadiusRaw, TagSet RadarTags, bool SemiActive);

/// <summary>
/// One air class's terms in an interceptor's kill model (content: the class's entries in <c>killBest</c>,
/// <c>speedRefs</c> and <c>hitDamage</c>; E5, the plan's air-combat model, section 2.8).
/// </summary>
/// <param name="Class">The air class.</param>
/// <param name="Best">The kill chance, percent, of its best shot at this class: its whole range of chances is scaled so that
/// KillMax becomes this (the scout drone: 5% for the AA soldier); 0 for KillMax.</param>
/// <param name="SpeedRef">The target speed (raw a tick) above which the chance falls in proportion, for this class; 0 for the weapon's.</param>
/// <param name="HitDamage">The share, percent, of a flying unit's max HP a successful roll takes (a jet survives an IR missile
/// hit at 60%); 0 or 100 and above: it kills.</param>
public sealed record InterceptClassRule(InterceptClass Class, int Best, Fixed SpeedRef, int HitDamage);

/// <summary>A guided missile's seeker: what countermeasures decoy it (flares an IR seeker, chaff a radar one).</summary>
public enum Seeker : byte
{
    /// <summary>Not declared.</summary>
    None,

    /// <summary>Infrared (the AA soldier's missile, a jet's short-range missile): decoyed by flares.</summary>
    Ir,

    /// <summary>Radar (the air defence vehicle, a jet's long-range missile): decoyed by chaff.</summary>
    Radar,
}

/// <summary>
/// A per-target hit rule of an air-defence missile (content <c>intercept.hits</c>): against air targets
/// of <see cref="Class"/> its proximity fuse rolls a flat <see cref="Chance"/> percent (the kill model's
/// aspect, speed, crossing, range and tracking terms do not apply), and a hit takes
/// <see cref="Damage"/> percent of the target's max HP (100 or more: it kills; a projectile target,
/// which has no HP, is always killed by a hit). The Overseer surveillance drone: the AA soldier's
/// missile hits 45% and takes 55% (two hits down it), the air defence vehicle's hits 65% and kills.
/// </summary>
/// <param name="Class">The interception class it applies to.</param>
/// <param name="Chance">Percent chance a fuse that goes off beside such a target hits it.</param>
/// <param name="Damage">Percent of the target's max HP a hit takes.</param>
public sealed record InterceptHit(InterceptClass Class, int Chance, int Damage);

/// <summary>
/// An air-to-ground strike (the Watcher drone's missile; docs: the game repo's docs/watcher-design.md).
/// Fired from the drone's height at an enemy ground unit within the weapon's range of the point under
/// the drone (its sight), it flies a straight 3D line at the weapon's projectile speed. Whether it hits
/// is rolled once at launch from the world's generator: <see cref="HitIdle"/> percent against a target
/// that stood still last tick, <see cref="HitMoving"/> against one that moved. A hit homes on the target
/// and destroys it outright when it has one of <see cref="Kills"/>, unless it also has one of
/// <see cref="HeavyTags"/>: then a second roll destroys it <see cref="HeavyKill"/> percent of the time,
/// else leaves it with at most <see cref="HeavyHpLeft"/> percent of its max HP. Anything else (a
/// building) takes the weapon's damage through its warhead. Either way the splash hurts what is
/// around the point it goes off. A miss goes off on the ground <see cref="MissNear"/> to
/// <see cref="MissFar"/> from where the target was at launch, in a seeded direction.
/// </summary>
/// <param name="HitIdle">Percent chance to hit a target standing still.</param>
/// <param name="HitMoving">Percent chance to hit a moving target.</param>
/// <param name="Kills">Tags of the targets a hit destroys outright.</param>
/// <param name="HeavyTags">Tags of the targets a hit destroys only with <see cref="HeavyKill"/>.</param>
/// <param name="HeavyKill">Percent chance a hit destroys a heavy target.</param>
/// <param name="HeavyHpLeft">Percent of its max HP, at most, a heavy target that survives a hit keeps.</param>
/// <param name="MissNear">The nearest a miss lands from the target.</param>
/// <param name="MissFar">The farthest a miss lands from the target.</param>
public sealed record AirStrike(int HitIdle, int HitMoving, TagSet Kills, TagSet HeavyTags, int HeavyKill, int HeavyHpLeft, Fixed MissNear, Fixed MissFar);

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
/// An aura around its unit: healing every <see cref="EveryTicks"/> ticks for the units it
/// affects in the radius (the aura system; the Sentinel's assault medic), and stat modifiers,
/// which load but nothing applies yet. A unit takes at most one aura heal per interval
/// however many auras reach it, and never heals itself.
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

    /// <summary>The most launched units it may have aloft at a time (0: no limit): a scout launcher's one drone.</summary>
    public int MaxActive { get; init; }

    /// <summary>Set when only a supply depot refills it (a round per EveryTicks, for Cost), never while one is aloft.</summary>
    public DepotReload? DepotReload { get; init; }

    /// <summary>Nothing refills it but a launched scout coming home (a single-use scout station).</summary>
    public bool NoResupply { get; init; }
}

/// <summary>
/// Smoke launchers (the Humvee's; reusable by any ground vehicle): the <see cref="Sim.CommandKind.Smoke"/> order pops a
/// smoke screen of <see cref="Radius"/> around the unit where it stands, for <see cref="DurationTicks"/>. A sight line that
/// crosses a screen is blocked (unless both ends stand in that screen), so enemies outside can't see units inside or
/// behind it; a guided missile steering at a unit inside it, or whose operator's line crosses it, loses guidance. Each pop
/// spends a charge; charges refill where weapons rearm (a supply depot, the field ammo point, a garage's service: the
/// military garage, the forward airbase's field garage). Used by the smoke system.
/// </summary>
public sealed class SmokeEffect : Effect
{
    /// <summary>Pops carried when full.</summary>
    public required int Charges { get; init; }

    /// <summary>The screen's radius around where it was popped.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>Ticks the screen lasts.</summary>
    public required int DurationTicks { get; init; }

    /// <summary>Ticks after a pop before the next may go (the launchers reload).</summary>
    public required int Cooldown { get; init; }
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

    /// <summary>What its blast does to a runway it lands on (a crater or wear), or null for nothing.</summary>
    public RunwayEffect? Runway { get; init; }
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

    /// <summary>The stock one placed finished (a scenario, the sandbox) starts with (default <see cref="Stock"/>).</summary>
    public int StartStock { get; init; }

    /// <summary>
    /// A store only (logistics: the supply stash, an airfield's stock): it rearms nothing, gives no reload bonus and is no depot
    /// for depot reloads; cargo trucks unload into it only on an Unload order aimed at it.
    /// </summary>
    public bool Storage { get; init; }

    /// <summary>Cargo trucks load here (<see cref="Sim.CommandKind.LoadCargo"/>), out of its stock (an unlimited one: free).</summary>
    public bool CargoSource { get; init; }

    /// <summary>It rearms only ground units (nothing that flies: the field ammo point).</summary>
    public bool GroundOnly { get; init; }

    /// <summary>The stock one charge it gives costs (1 for a depot; a depot-reloaded round costs its weapon's own cost).</summary>
    public int RoundCost { get; init; } = 1;

    /// <summary>An airfield's stock (logistics): what 100 HP of repair costs at its stations and its garage (0: free).</summary>
    public int RepairCost { get; init; }

    /// <summary>An airfield's stock (logistics): what a full tank costs at its stations (0: free).</summary>
    public int FuelCost { get; init; }

    /// <summary>Stock given to each friendly supplier with limited stock in the radius per period.</summary>
    public required int Restock { get; init; }

    /// <summary>Percent added to the reload speed of friendly units in the radius.</summary>
    public required int ReloadBonus { get; init; }
}

/// <summary>
/// Fires once when an enemy ground unit comes within the radius (unless its owner turned that
/// off), when its owner orders it, or when another trap's blast reaches it; then removes its
/// entity. Flying units never set it off. Used by the trigger system, for trap archetypes.
/// </summary>
public sealed class TriggerEffect : Effect
{
    /// <summary>The default <see cref="Edge"/>: half the centre's damage at the edge.</summary>
    public const int DefaultEdge = 50;

    /// <summary>The default <see cref="ChainTicks"/>: 0.1 s.</summary>
    public const int DefaultChainTicks = 2;

    /// <summary>The trigger radius: an enemy this close sets it off.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>Damage before armor.</summary>
    public required int Damage { get; init; }

    /// <summary>The damage type.</summary>
    public required Warhead Warhead { get; init; }

    /// <summary>The blast radius; zero hits only the nearest triggering unit.</summary>
    public required Fixed Splash { get; init; }

    /// <summary>Only units with one of these tags set it off (any unit if empty).</summary>
    public required TagSet Affects { get; init; }

    /// <summary>The blast's damage at its edge, in percent of the centre's.</summary>
    public int Edge { get; init; } = DefaultEdge;

    /// <summary>
    /// Tags of the one unit it destroys outright (an anti-tank mine's vehicle): the one that set it
    /// off, or else the nearest enemy with them within <see cref="Radius"/>. Its blast never kills
    /// any other unit with these tags (they keep at least 1 HP). Empty: none.
    /// </summary>
    public TagSet Kills { get; init; }

    /// <summary>Ticks before a trap inside its blast (<see cref="Splash"/>) goes off in turn.</summary>
    public int ChainTicks { get; init; } = DefaultChainTicks;
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
/// An air class (the plan's air-combat model, section 2.8, E5): what air defence and air-to-air
/// weapons engage a target as. Classes are content (<c>airClasses</c>, <see cref="ContentSet.AirClasses"/>):
/// the value is the class's 1-based position in that list (0: none), and every rule about a class
/// (who engages it, the kill model's per-class terms, hit rules) is read from content. The named
/// members are the positions of the MVP content's list (<c>content/mvp/air_classes.json</c>), kept as
/// readable aliases for renderers and tests; the engine never branches on them.
/// </summary>
public enum InterceptClass : byte
{
    /// <summary>Not marked.</summary>
    None,

    /// <summary>A ballistic missile: fast, from a high apex; hard to intercept.</summary>
    Ballistic,

    /// <summary>A slow one-way attack drone cruising high: air defence's natural prey.</summary>
    Drone,

    /// <summary>A scout drone: a flying unit orbiting high (<see cref="ScoutFlight"/>); hard to bring down.</summary>
    Scout,

    /// <summary>A strike drone (the Watcher): a big flying unit orbiting higher still; easier to hit than a scout.</summary>
    Watcher,

    /// <summary>A surveillance drone (the Overseer): unarmed, orbiting highest of all; weak once hit.</summary>
    Overseer,

    /// <summary>An FPV drone unit (no flight block yet: an air target from E6).</summary>
    Fpv,

    /// <summary>A multirole jet (an F-16 type).</summary>
    Multirole,

    /// <summary>An air superiority jet (an F-22 type).</summary>
    Fighter,

    /// <summary>A heavy ground attack jet (an A-10 type, H2): low, slow and tough.</summary>
    Attacker,

    /// <summary>A gunship (an AC-130 type, H3): orbiting high above the AA soldier's ceiling, slow and very tough.</summary>
    Gunship,

    /// <summary>A strategic bomber (a B-52 type, H4): high above the AA soldier's ceiling, big and tough (6 missile hits).</summary>
    Bomber,

    /// <summary>An arced missile (the twin missile truck's): a projectile on its high arc.</summary>
    Missile,

    /// <summary>A heavy rocket (the heavy rocket truck's, the rocket jeep's): a projectile on its arc.</summary>
    Rocket,

    /// <summary>The attack helicopter (an AH-64 type): low, missiles and a cannon, a mast radar.</summary>
    AttackHeli,

    /// <summary>The armoured helicopter (a Mi-28 type): low, armoured against guns.</summary>
    ArmoredHeli,

    /// <summary>The light helicopter (an AH-6 type): fast, light, rider benches.</summary>
    LightHeli,

    /// <summary>The Kaman drone (a Kaman-22 type long-range attack drone): a flying unit that fires its missiles and dives into its target.</summary>
    Kaman,

    /// <summary>The stealth bomber (a B-2 type): very high, a very low signature; fragile once seen (3 missile hits).</summary>
    Stealth,

    /// <summary>The airlifter (a C-5 type, logistics): big, 300 m up and 150 m on its airdrops; about 4 missile hits.</summary>
    Airlifter,
}

/// <summary>
/// A cargo pallet (<see cref="WeaponEffect.Airdrop"/>; the airlifter's containers).
/// </summary>
/// <param name="Amount">The supply one pallet (container) carries.</param>
/// <param name="UnloadTicks">Ticks the aircraft takes to unload one into an airfield's stock when it lands there (a Deliver order).</param>
public sealed record Airdrop(int Amount, int UnloadTicks);

/// <summary>
/// A penetrating bomb's effect where it goes off (<see cref="WeaponEffect.Penetrator"/>; the stealth bomber's bunker buster).
/// </summary>
/// <param name="RadiusRaw">Everything on the ground (and underground) within this distance of the impact (raw; a structure by its
/// footprint) is destroyed, whatever its armour.</param>
/// <param name="CraterTicks">How long its deep crater stays on the map (ticks; <see cref="Sim.World.Craters"/>).</param>
/// <param name="CraterRadiusRaw">The deep crater's radius (raw), for renderers.</param>
public sealed record Penetrator(long RadiusRaw, int CraterTicks, long CraterRadiusRaw);

/// <summary>What carries an air class: a projectile (a one-way attack drone, a ballistic missile) or a flying unit.</summary>
public enum AirClassKind : byte
{
    /// <summary>A weapon's projectile (<c>weapon.interceptible</c>).</summary>
    Projectile,

    /// <summary>A flying unit (<c>flight.interceptible</c>).</summary>
    Aircraft,
}

/// <summary>An air class as content defines it (<c>airClasses</c>).</summary>
/// <param name="Id">Its content id ("drone", "watcher", "multirole", ...).</param>
/// <param name="Class">Its value: the 1-based position in the content's list.</param>
/// <param name="Kind">What carries it.</param>
public sealed record AirClass(string Id, InterceptClass Class, AirClassKind Kind);

/// <summary>
/// A target's signature (archetype <c>signature</c>; the plan's air-combat model, section 2.8): percents
/// of a normal target. A sensor detects it at its air vision x Detect / 100, a lock on it takes the
/// weapon's lock time x Lock / 100, and every kill or hit chance against it is multiplied by Kill / 100.
/// The air superiority jet's 50 / 200 / 60: harder to see, lock and bring down, not invisible.
/// </summary>
/// <param name="Detect">Percent of a sensor's air vision at which it is detected.</param>
/// <param name="Lock">Percent of a weapon's lock time it takes to lock it.</param>
/// <param name="Kill">Percent of the kill (or hit) chance left against it.</param>
public sealed record Signature(int Detect, int Lock, int Kill)
{
    /// <summary>A normal target: 100 / 100 / 100.</summary>
    public static readonly Signature Normal = new(100, 100, 100);
}

/// <summary>A countermeasure's kind.</summary>
public enum CountermeasureKind : byte
{
    /// <summary>Flares: decoy IR seekers.</summary>
    Flare,

    /// <summary>Chaff: decoys radar seekers.</summary>
    Chaff,
}

/// <summary>
/// A countermeasure dispenser (archetype <c>countermeasures</c>; the plan's air-combat model, section 2.9;
/// the data shape is E5, the behaviour E6): Salvos dispenses per sortie (a stock like ammunition, refilled
/// only on a Rearm), fired one per hostile missile of seeker Vs homing on the aircraft within Window ticks
/// of impact (at most PerMissile each), each multiplying that missile's kill chance by Pk percent;
/// RearmTicks per salvo at a 1x station.
/// </summary>
/// <param name="Kind">Flares or chaff.</param>
/// <param name="Salvos">Salvos carried per sortie.</param>
/// <param name="Vs">The seeker it decoys.</param>
/// <param name="Pk">Percent of the missile's kill chance left after a salvo.</param>
/// <param name="Window">Ticks to go at which it fires against a homing missile.</param>
/// <param name="PerMissile">Salvos at most per missile.</param>
/// <param name="RearmTicks">Ticks to reload one salvo at a 1x station.</param>
/// <param name="BreakPk">
/// Percent of the missile's kill chance left after a salvo dispensed while the aircraft breaks against that
/// missile (E13, decision D-BRK1: a fixed wing with an <see cref="Evade"/> block dispenses at its launch
/// warning and turns away as the decoy blooms, so the seeker keeps the decoy); -1 (the default): <see cref="Pk"/>.
/// </param>
public sealed record Countermeasure(CountermeasureKind Kind, int Salvos, Seeker Vs, int Pk, int Window, int PerMissile, int RearmTicks, int BreakPk = -1)
{
    /// <summary>The percent left after a salvo dispensed in a break (<see cref="BreakPk"/>, or <see cref="Pk"/> when not set).</summary>
    public int PkInBreak => BreakPk < 0 ? Pk : BreakPk;
}

/// <summary>
/// A fixed-wing aircraft's evasion (archetype <c>evade</c>; the plan's air-combat model, section 2.9; E6): on a
/// launch warning (a hostile guided missile homing on it within <see cref="Warn"/> ticks to go) it breaks: a
/// turn at <see cref="BreakG"/> percent of its max g toward the beam (square to the missile's line of sight),
/// a descent toward its floor and the afterburner, for at most <see cref="MaxTicks"/>, then it resumes its task.
/// No random number: the missile's turn-rate cap and its fuse see the real geometry.
/// </summary>
/// <param name="BreakG">Percent of its max-g turn it pulls in the break.</param>
/// <param name="Warn">Ticks to go of a homing missile at which it breaks.</param>
/// <param name="MaxTicks">The longest break, ticks.</param>
public sealed record Evade(int BreakG, int Warn, int MaxTicks);

/// <summary>
/// A fixed wing's attack-run geometry against the ground (archetype <c>attackRun</c>; E13): every strike is the
/// state machine Ingress, Align, Run, Break, Extend, Reposition (then Align again while ordered and armed, or
/// Egress). The align leg is <see cref="AlignRaw"/> long, wings level on the run line (locked from the aim point
/// through where it is when it starts); the run (the delivery's profile from its initial point) follows; after the
/// release it breaks at least <see cref="BreakTurn"/> degrees and <see cref="BreakRaw"/> of travel to one side,
/// extends straight at least <see cref="ExtendRaw"/> and until it has room to turn in and align again (or the fly
/// zone's band), then turns back the same way until the target is on its nose. A release more than
/// <see cref="WindowRaw"/> past the aim point (or off the line by that plus the profile's scatter) aborts the pass.
/// <see cref="Offset"/> spreads aircraft: the run line turns (ID mod 5 - 2) x Offset / 2 degrees round the aim point
/// and the extend grows by (ID mod 3) half seconds, so a pair given the same order never stacks. Zeros take the
/// derived defaults (the air-feel research: align 3 s, break max(turn radius, 2 s), extend 2 s at attack speed).
/// </summary>
/// <param name="AlignRaw">The align leg's length (raw), or 0: 3 s at its attack speed.</param>
/// <param name="BreakTurn">The break's least heading change, degrees.</param>
/// <param name="BreakRaw">The break's least travel (raw), or 0: the larger of its turn radius and 2 s.</param>
/// <param name="ExtendRaw">The extend's least travel (raw), or 0: 2 s at its attack speed.</param>
/// <param name="WindowRaw">The release window (raw): how far past the aim point (or off the line) a release still counts.</param>
/// <param name="Offset">The per-aircraft run line spread, degrees (see above).</param>
public sealed record AttackRunSpec(long AlignRaw, int BreakTurn, long BreakRaw, long ExtendRaw, long WindowRaw, int Offset)
{
    /// <summary>The defaults: everything derived from the aircraft's speeds.</summary>
    public static readonly AttackRunSpec Default = new(0, 60, 0, 0, 2 * Fixed.OneRaw, 0);
}

/// <summary>
/// A gunship's orbit attack (archetype <c>gunship</c>, H3; the game repo's docs/heavy-air-plan.md, section 1.2): given an
/// area (an attack-ground order at a cell, or an attack order at a unit: the area follows it), it flies a left-hand
/// pylon turn of <see cref="RadiusRaw"/> round the area's centre and fires its side cannons (<see cref="Model.SideFire"/>)
/// only while on that circle: within <see cref="OnCircleRaw"/> of the radius with the centre within <see cref="Bearing"/>
/// degrees of its left wing. It picks its targets inside <see cref="AreaRaw"/> of the centre among what its side sees.
/// </summary>
/// <param name="RadiusRaw">The orbit's radius round the area's centre (raw).</param>
/// <param name="AreaRaw">The area's radius (raw): targets and area fire inside it.</param>
/// <param name="OnCircleRaw">How far off the radius (raw) it still counts as on the circle.</param>
/// <param name="Bearing">How far (degrees) the centre may lie off its left wing's direction for it to fire.</param>
public sealed record GunshipSpec(long RadiusRaw, long AreaRaw, long OnCircleRaw, int Bearing);

/// <summary>
/// A Kaman drone's attack (a Kaman-22 type long-range attack drone; content <c>kaman</c>; the Kaman system): launched at a
/// unit (it follows it) or a point, it climbs to its flight height and cruises at it; from its missile weapon's range it
/// fires its missiles at the target a reload apart, and it pitches over into a dive that ends in the target, where its
/// warhead goes off. A target its side stops seeing is flown to where it was last seen.
/// </summary>
/// <param name="Damage">The dive's blast damage (before the warhead's table).</param>
/// <param name="Warhead">The blast's warhead, an index into <see cref="ContentSet.Warheads"/>.</param>
/// <param name="Splash">The blast radius.</param>
/// <param name="SplashCenter">The blast's percent of the damage at its centre.</param>
/// <param name="SplashEdge">The blast's percent of the damage at its edge.</param>
/// <param name="DivePermille">The dive's slope: metres down per 1000 forward (1000: 45 degrees).</param>
/// <param name="PitchOverTicks">How long the pitch-over from level flight into the dive takes (its vertical acceleration).</param>
/// <param name="Runway">What its blast does to a runway, or null.</param>
public sealed record KamanSpec(int Damage, int Warhead, Fixed Splash, int SplashCenter, int SplashEdge, int DivePermille, int PitchOverTicks,
    RunwayEffect? Runway);

/// <summary>
/// A gunship's side cannon (weapon <c>sideFire</c>, H3): a round flies straight from the aircraft at the weapon's
/// projectile speed to its aim point, scattered up to <see cref="ScatterRaw"/> per axis (seeded), and goes off there
/// like a bomb (the direct hit, the splash, the runway effect, the impact event). Its target: the ordered unit when its
/// armour class is listed, else the best seen enemy inside the area: the earliest listed class, then the nearest to the
/// centre (or, <see cref="Biggest"/>, the most max HP), ties to the lower ID. With no target, <see cref="AreaFire"/>
/// fires at a seeded point inside the area (on an attack-ground order only); else it holds its fire.
/// </summary>
/// <param name="ScatterRaw">The scatter per axis round its aim point (raw).</param>
/// <param name="Tier">The airstrike tier its impact looks like (1 to 7; 0: by its damage, <c>World.StrikeTier</c>).</param>
/// <param name="Priority">Per armour class index, its rank (0 best) among the classes it fires at; -1: never at that class.</param>
/// <param name="AreaFire">With no target it fires at seeded points inside the area (attack-ground only).</param>
/// <param name="Biggest">It picks the target with the most max HP instead of the nearest to the centre (the very big round).</param>
public sealed record SideFire(long ScatterRaw, int Tier, IReadOnlyList<int> Priority, bool AreaFire, bool Biggest);

/// <summary>A formation slot off the lead's path (E13): this far back along it and this far to its right (negative: left), raw.</summary>
/// <param name="BackRaw">Distance back along the lead's path (raw): the slot's delay at cruise speed.</param>
/// <param name="AsideRaw">Distance to the right of the lead's path (raw; negative: to its left).</param>
public readonly record struct FormationSlot(long BackRaw, long AsideRaw);

/// <summary>
/// How fixed wings given the same order fly together (archetype <c>element</c>; E13): the lowest ID leads, the
/// others take <see cref="Slots"/> in ID order (an element is at most one more than the slots), each flying its
/// slot off the lead's own path (the lead's positions <c>back / cruise</c> ago, offset aside: it never cuts the
/// lead's turns) with its speed trimmed to hold it. On a strike each wingman runs in <see cref="Stagger"/> ticks
/// times its slot number behind the lead on the lead's path offset aside (staggered runs), breaks the way the
/// lead broke and rejoins with it.
/// </summary>
/// <param name="Slots">The wingmen's slots, in order (1 to 3).</param>
/// <param name="Stagger">Ticks between successive runs of an element on a strike.</param>
public sealed record ElementSpec(IReadOnlyList<FormationSlot> Slots, int Stagger);

/// <summary>
/// An aircraft's air-to-air pursuit separation (archetype <c>pursuit</c>; E13, decision D-PUR1): closing on its air
/// target with their closest approach under <see cref="SeparationRaw"/> within <see cref="BreakOffTicks"/>, it breaks
/// off (a hard turn away from the target's side, <see cref="ClimbRaw"/> above it), extends until
/// <see cref="ReattackRaw"/> away (at most <see cref="MaxTicks"/>) and re-attacks: it never flies through its target.
/// </summary>
/// <param name="SeparationRaw">The least closest approach it accepts (raw, 3D).</param>
/// <param name="BreakOffTicks">How far ahead it looks for the closest approach (ticks).</param>
/// <param name="ReattackRaw">The range (raw) at which the break-off ends and it attacks again.</param>
/// <param name="ClimbRaw">How far above the target it flies while breaking off (raw height).</param>
/// <param name="MaxTicks">The longest break-off (ticks).</param>
public sealed record PursuitSpec(long SeparationRaw, int BreakOffTicks, long ReattackRaw, long ClimbRaw, int MaxTicks)
{
    /// <summary>The defaults: 6 cells, 2 s ahead, re-attack 30 cells out, 10 m above, at most 6 s.</summary>
    public static readonly PursuitSpec Default = new(6 * Fixed.OneRaw, 40, 30 * Fixed.OneRaw, 10 * 512, 120);
}

/// <summary>
/// What happens when an aircraft is lost in the air (archetype <c>loss</c>; the plan's section 5; E10): any
/// kill but a gun's sends a burning wreck down a crash path (<see cref="Crash"/>); a gun kill sends the
/// wreck down too and the pilot ejects (<see cref="Eject"/>). An aircraft without a loss block simply
/// breaks up and falls (the drones, as before).
/// </summary>
/// <param name="Crash">The wreck's fall and its impact, or null: no wreck.</param>
/// <param name="Eject">The ejection on a gun kill, or null: nobody ejects.</param>
public sealed record LossSpec(CrashSpec? Crash, EjectSpec? Eject);

/// <summary>
/// A wreck's crash path and impact (archetype <c>loss.crash</c>; E10): it keeps its velocity, falls under
/// <see cref="GravityRaw"/>, loses <see cref="Drag"/> percent of its horizontal speed a second, never steers,
/// and goes off where it meets the ground or a ground unit's top (a small blast through
/// <see cref="Warhead"/>: <see cref="Damage"/>, <see cref="Splash"/>, and the runway effect on a runway).
/// A missile kill breaks the airframe up in the air with <see cref="BreakUp"/> percent chance (seeded): it
/// then loses <see cref="BreakUpDrag"/> percent a second instead (a steeper fall).
/// </summary>
/// <param name="Warhead">The impact's warhead (an index into <see cref="ContentSet.Warheads"/>).</param>
/// <param name="Damage">The impact's damage (a direct hit on the unit it meets, and the splash's centre).</param>
/// <param name="Splash">The splash radius (full at the centre, half at the edge).</param>
/// <param name="GravityRaw">The fall's gravity, raw a second squared (512 a metre).</param>
/// <param name="Drag">Percent of its horizontal speed it loses a second in one piece.</param>
/// <param name="BreakUp">Percent chance a missile kill breaks it up in the air.</param>
/// <param name="BreakUpDrag">Percent of its horizontal speed a broken-up wreck loses a second.</param>
/// <param name="Runway">What its impact does to a runway it lands on, or null.</param>
public sealed record CrashSpec(int Warhead, int Damage, Fixed Splash, long GravityRaw, int Drag, int BreakUp, int BreakUpDrag, RunwayEffect? Runway);

/// <summary>
/// The pilot's ejection on a gun kill (archetype <c>loss.eject</c>; the plan's section 5.3; E10): the seat
/// rises <see cref="SeatRiseRaw"/> in <see cref="RiseTicks"/>, then he comes down at <see cref="ChuteRateRaw"/>
/// (the drogue, then the main chute at <see cref="ChuteTicks"/>), drifting along the aircraft's last heading at
/// <see cref="Drift"/> for <see cref="DriftTicks"/>; untargetable until he lands (decision D8), where he
/// becomes a unit of <see cref="Unit"/> of the aircraft's owner.
/// </summary>
/// <param name="Unit">The archetype he becomes on landing (an index into <see cref="ContentSet.Archetypes"/>: the pilot).</param>
/// <param name="SeatRiseRaw">How high the seat throws him (raw, 512 a metre).</param>
/// <param name="RiseTicks">How long the seat's rise lasts.</param>
/// <param name="ChuteTicks">When the main chute opens (ticks after the ejection; an event for renderers).</param>
/// <param name="ChuteRateRaw">His descent rate after the rise, raw a second.</param>
/// <param name="Drift">His drift along the aircraft's last heading, a tick.</param>
/// <param name="DriftTicks">How long he drifts (ticks after the ejection).</param>
public sealed record EjectSpec(int Unit, long SeatRiseRaw, int RiseTicks, int ChuteTicks, long ChuteRateRaw, Fixed Drift, int DriftTicks);

/// <summary>
/// An Air-layer unit without a flight block that air defence and air-to-air weapons engage as an air target
/// (archetype <c>airBody</c>; E6): the FPV drone. Its class and the height it flies at over the ground.
/// </summary>
/// <param name="Class">Its air class (an aircraft class).</param>
/// <param name="HeightRaw">How high it flies over the ground under it (raw, 512 a metre).</param>
public sealed record AirBodySpec(InterceptClass Class, long HeightRaw);

/// <summary>
/// A one-way attack drone's flight: off its rail at LaunchHeight, climbing up to ClimbPerTick a tick
/// toward CruiseAboveMax over the map's highest ground and rounding off into level cruise (the climb
/// rate falls to zero over RoundOffTicks, a constant deceleration); then a pitch-over: its slope
/// turns evenly from level (or its climb) to a dive at DivePermille (metres down per metre forward)
/// over PitchOverTicks of flight, started so that the dive ends on its point. Heights are raw (512
/// a metre). RoundOffTicks and PitchOverTicks 0 give the old corners.
/// </summary>
public sealed record DroneFlight(long CruiseAboveMax, long ClimbPerTick, int BoosterTicks, int DivePermille, long LaunchHeight,
    int RoundOffTicks = 0, int PitchOverTicks = 0);

/// <summary>
/// A scout drone's flight (a flying unit; docs: the game repo's docs/air-defense-design.md, section 10).
/// It climbs off its launcher at ClimbPerTick to HeightRaw above the ground under and just ahead of it,
/// flies to the point it is ordered to and orbits it at OrbitRadius, turning its velocity at most
/// TurnPermille of its speed a tick (a fixed wing never stops). Short of endurance (its lifetime) it
/// flies home on its own with ReserveTicks to spare and is recovered within RecoverRange of its
/// launcher, which gets the drone back and needs TurnaroundTicks before the next launch. Only
/// interceptor weapons of its class engage it; no gun, rocket or blast reaches it up there.
/// HomeWhenEmpty is deprecated and ignored (the owner's flight rules, 2026-10-19: bingo fuel is the
/// only automatic way home; an aircraft rearms only on a Rearm order). With Relaunch its launcher
/// sends it up again to where it orbited once the turnaround is over: refuelled, with the stocks and
/// HP it came home with, or rearmed and repaired when a Rearm order sent it home. Heights are raw (512 a metre).
/// EdgeMarginRaw is retired (E12, always 0): the map's edge no longer turns it back. FlyZoneMarginRaw
/// is how far past the map's edge it may fly, on every side (the fly zone, E12: the map grown by this
/// margin; aircraft turn in it, ground units never leave the map); the loader keeps it at least twice its
/// turn radius plus a second of travel and makes it 48 cells (or that minimum, if larger) when content leaves it out.
/// </summary>
public sealed record ScoutFlight(long HeightRaw, long ClimbPerTick, long LaunchHeightRaw, Fixed OrbitRadius, int TurnPermille,
    InterceptClass Class, int ReserveTicks, Fixed RecoverRange, int TurnaroundTicks, bool HomeWhenEmpty = false, bool Relaunch = false,
    long EdgeMarginRaw = 0, FixedWingSpec? Wing = null, long FlyZoneMarginRaw = 0, HeightByMap? ByMap = null, EnduranceByMap? Endurance = null, SpeedByMap? Speeds = null,
    RotarySpec? Rotor = null)
{
    /// <summary>
    /// This flight on a map whose longer side is <paramref name="cells"/> cells (E13, D-SPD1): with <see cref="Speeds"/> and a
    /// fixed wing, its envelope's cruise and attack speeds grow linearly from its From cells to its Percent at To cells (no
    /// further), its dash to its DashPercent (never under the attack speed), the thrusts re-derived from them; its fly zone
    /// margin at least twice its max-g turn at that cruise plus a second of it. Otherwise itself.
    /// </summary>
    public ScoutFlight On(int cells)
    {
        if (Speeds is not { } sp || Wing is not { } g || cells <= sp.FromCells)
        {
            return this;
        }
        var span = Math.Min(cells, sp.ToCells) - sp.FromCells;
        var whole = Math.Max(1, sp.ToCells - sp.FromCells);
        var pct = 100 + (sp.Percent - 100) * span / whole;
        var dpct = 100 + (sp.DashPercent - 100) * span / whole;
        var w = g.ScaledSpeeds(pct, dpct);
        return this with { Wing = w, FlyZoneMarginRaw = Math.Max(FlyZoneMarginRaw, 2 * w.Radius(w.Cruise) + w.Cruise) };
    }

    /// <summary>
    /// Its full tank (ticks of lifetime) on a map whose longer side is <paramref name="cells"/> cells: <paramref name="lifetime"/>
    /// (the archetype's), or with <see cref="Endurance"/> (E13, D-FUEL1) that up to its From cells, rising linearly to its
    /// Percent at To cells and no further.
    /// </summary>
    public int TankOn(int lifetime, int cells)
    {
        if (Endurance is not { } e || cells <= e.FromCells)
        {
            return lifetime;
        }
        var pct = 100 + (long)(e.Percent - 100) * (Math.Min(cells, e.ToCells) - e.FromCells) / Math.Max(1, e.ToCells - e.FromCells);
        return (int)Math.Min(int.MaxValue, lifetime * pct / 100);
    }

    /// <summary>
    /// Its flight height (raw) on a map whose longer side is <paramref name="cells"/> cells: <see cref="HeightRaw"/>,
    /// or with <see cref="ByMap"/> (E13, D-ALT1) that up to its From cells, rising linearly to its height at To
    /// cells and on at that slope, never above its max.
    /// </summary>
    public long HeightOn(int cells)
    {
        if (ByMap is not { } m || cells <= m.FromCells)
        {
            return HeightRaw;
        }
        var h = HeightRaw + (m.HeightRaw - HeightRaw) * (cells - m.FromCells) / Math.Max(1, m.ToCells - m.FromCells);
        return Math.Min(h, m.MaxRaw);
    }

    /// <summary>Its turn radius at its speed (raw a tick): speed / (TurnPermille / 1000).</summary>
    public static long TurnRadiusRaw(long speedRaw, int turnPermille) => speedRaw * 1000 / Math.Max(1, turnPermille);

    /// <summary>
    /// Its tightest turn radius (raw) at a speed in raw a tick: a fixed wing's within its envelope (E4: S^2 / a(S)),
    /// a drone's at its turn rate.
    /// </summary>
    public long RadiusAt(long speedRaw) => Wing is { } w ? w.Radius(speedRaw * 20) : TurnRadiusRaw(speedRaw, TurnPermille);

    /// <summary>
    /// Its kinematics kind: <see cref="FlightKind.Rotary"/> with a <see cref="Rotor"/> (a helicopter), <see cref="FlightKind.FixedWing"/>
    /// with a <see cref="Wing"/> envelope, else <see cref="FlightKind.Drone"/>.
    /// </summary>
    public FlightKind Kind => Rotor is not null ? FlightKind.Rotary : Wing is null ? FlightKind.Drone : FlightKind.FixedWing;
}

/// <summary>Transit speeds that grow with the map (flight <c>speedByMap</c>; E13, D-SPD1): see <see cref="ScoutFlight.On"/>.</summary>
/// <param name="FromCells">Up to this many cells across (the longer side) the speeds are its own.</param>
/// <param name="ToCells">At this many cells and beyond they are at their percents.</param>
/// <param name="Percent">Cruise and attack speeds on the largest maps, percent.</param>
/// <param name="DashPercent">Dash speed on the largest maps, percent (never under the attack speed).</param>
public sealed record SpeedByMap(int FromCells, int ToCells, int Percent, int DashPercent);

/// <summary>An endurance that grows with the map (flight <c>enduranceByMap</c>; E13, D-FUEL1): see <see cref="ScoutFlight.TankOn"/>.</summary>
/// <param name="FromCells">Up to this many cells across (the longer side) the tank is the archetype's lifetime.</param>
/// <param name="ToCells">At this many cells and beyond it is <paramref name="Percent"/> of it.</param>
/// <param name="Percent">The tank on the largest maps, percent of the archetype's lifetime.</param>
public sealed record EnduranceByMap(int FromCells, int ToCells, int Percent);

/// <summary>A flight height that grows with the map (flight <c>heightByMap</c>; E13, D-ALT1): see <see cref="ScoutFlight.HeightOn"/>.</summary>
/// <param name="FromCells">Up to this many cells across (the longer side) the flight height is its own.</param>
/// <param name="ToCells">At this many cells it reaches <paramref name="HeightRaw"/>.</param>
/// <param name="HeightRaw">The height at <paramref name="ToCells"/> (raw).</param>
/// <param name="MaxRaw">The most it ever flies (raw).</param>
public sealed record HeightByMap(int FromCells, int ToCells, long HeightRaw, long MaxRaw);

/// <summary>
/// The kinematics of a flying unit's kind (the plan's air-combat model, section 2.4): the mission layer
/// and the flight controller are the same for every kind; only how a turn, speed and height demand
/// becomes motion differs.
/// </summary>
public enum FlightKind : byte
{
    /// <summary>The drone law: constant speed, the velocity turned at most TurnPermille of the speed a tick.</summary>
    Drone,

    /// <summary>A fixed wing with an energy envelope (<see cref="FixedWingSpec"/>, E4): the jets.</summary>
    FixedWing,

    /// <summary>A helicopter (<see cref="RotarySpec"/>): it hovers, turns on the spot, flies sideways and lands anywhere flat.</summary>
    Rotary,
}

/// <summary>A fixed-wing aircraft's throttle (<see cref="Sim.FixedWingState.Throttle"/>): the thrust and fuel-burn level.</summary>
public enum Throttle : byte
{
    /// <summary>Idle: the least thrust (level flight slows to its minimum speed) and burn.</summary>
    Idle,

    /// <summary>Cruise: level flight holds its cruise speed; burns its nominal rate (one unit of lifetime a tick).</summary>
    Cruise,

    /// <summary>Military (full dry power): level flight holds its attack speed; holds its sustained turn at cruise speed.</summary>
    Military,

    /// <summary>Afterburner: level flight reaches its dash speed; burns the most. Only for a flight that has one.</summary>
    Afterburner,
}

/// <summary>
/// A fixed-wing aircraft's energy envelope (E4; the plan's air-combat model, section 2.4), compiled from
/// a flight block with <c>kind: "fixedWing"</c>. All speeds are raw a second (raw a tick times 20, the
/// unit of <see cref="Sim.AirOps.Speed"/>: exact integer accelerations), accelerations raw a second
/// squared, heights raw (512 a metre; a cell is 1024 raw, 2 m, so heights and distances share the raw unit).
/// <para>Lateral acceleration available at speed S: a(S) = min(TurnAccel, TurnAccel x (S / Corner)^2): below
/// its corner speed the wing cannot pull its g limit (the radius S^2 / a stays at the corner radius); at the
/// corner speed the turn rate a / S peaks; faster, the radius grows with S^2.</para>
/// <para>Speed change a second: thrust(throttle) - Drag x (S / Dash)^2 - Bleed x (aUsed / TurnAccel)^2 -
/// Gravity x sin(climb angle). Each throttle's thrust is the drag at its equilibrium speed (idle: the
/// minimum speed; cruise: the archetype's speed; military: its attack speed; afterburner: its dash speed),
/// and the bleed is set so that Military holds a level turn of its sustained radius at cruise speed.</para>
/// </summary>
/// <param name="Cruise">Cruise speed (the archetype's speed).</param>
/// <param name="Dash">Dash speed: the afterburner's level equilibrium, and the most it ever flies.</param>
/// <param name="Military">Attack speed: full dry power's level equilibrium.</param>
/// <param name="Min">Minimum speed (the stall): it never flies slower; at it, it cannot climb.</param>
/// <param name="Corner">Corner speed: the slowest speed at which it can pull its full lateral acceleration.</param>
/// <param name="TurnAccel">Its g limit as a lateral acceleration (corner speed squared over its corner radius).</param>
/// <param name="Drag">The drag at dash speed (the afterburner's thrust).</param>
/// <param name="Bleed">The extra drag of a turn at its full lateral acceleration.</param>
/// <param name="Gravity">The energy exchange: the deceleration of a vertical climb (a dive gains as much).</param>
/// <param name="ThrustIdle">Idle thrust (the drag at minimum speed).</param>
/// <param name="ThrustCruise">Cruise thrust (the drag at cruise speed).</param>
/// <param name="ThrustMilitary">Military thrust (the drag at attack speed).</param>
/// <param name="ThrustAfterburner">Afterburner thrust (the drag at dash speed), 0 without an afterburner.</param>
/// <param name="DivePerTick">The most it sinks a tick (raw).</param>
/// <param name="FloorRaw">The least height over the ground under it (raw).</param>
/// <param name="IngressRaw">Its height over the ground on a strike order (raw); 0 keeps its flight height.</param>
/// <param name="LookAheadTicks">How far ahead (ticks of its velocity) the ground counts for its height.</param>
/// <param name="SeparationRaw">Height step by ID (ID mod 5 steps up, raw) so wingmen never overlap.</param>
/// <param name="BurnIdle">Fuel burn at idle, percent of nominal (100 = one unit of lifetime a tick).</param>
/// <param name="BurnCruise">Fuel burn at cruise, percent.</param>
/// <param name="BurnMilitary">Fuel burn at military power, percent.</param>
/// <param name="BurnAfterburner">Fuel burn on afterburner, percent.</param>
public sealed record FixedWingSpec(long Cruise, long Dash, long Military, long Min, long Corner, long TurnAccel, long Drag, long Bleed,
    long Gravity, long ThrustIdle, long ThrustCruise, long ThrustMilitary, long ThrustAfterburner, long DivePerTick, long FloorRaw,
    long IngressRaw, int LookAheadTicks, long SeparationRaw, int BurnIdle, int BurnCruise, int BurnMilitary, int BurnAfterburner)
{
    /// <summary>Whether it has an afterburner.</summary>
    public bool HasAfterburner => ThrustAfterburner > 0;

    /// <summary>
    /// This envelope with its cruise and attack speeds at <paramref name="percent"/> and its dash at <paramref name="dashPercent"/>
    /// (never under the attack speed), each throttle's thrust re-derived as the drag at its equilibrium speed (E13, D-SPD1).
    /// </summary>
    public FixedWingSpec ScaledSpeeds(int percent, int dashPercent)
    {
        var cruise = Cruise * percent / 100;
        var mil = Math.Max(cruise + 1, Military * percent / 100);
        var dash = Math.Max(mil, Dash * dashPercent / 100);
        long DragAtNew(long s) => Drag * s / dash * s / dash;
        return this with
        {
            Cruise = cruise, Military = mil, Dash = dash, ThrustIdle = DragAtNew(Min), ThrustCruise = DragAtNew(cruise),
            ThrustMilitary = DragAtNew(mil), ThrustAfterburner = HasAfterburner ? Drag : 0,
        };
    }

    /// <summary>The lateral acceleration it can pull at speed s (raw a second squared): min(TurnAccel, TurnAccel x (s / Corner)^2).</summary>
    public long LateralAccel(long s) => Math.Max(1, Math.Min(TurnAccel, TurnAccel * s / Corner * s / Corner));

    /// <summary>Its tightest turn radius at speed s (raw): s^2 / a(s).</summary>
    public long Radius(long s) => s * s / LateralAccel(s);

    /// <summary>The drag at speed s (raw a second squared): Drag x (s / Dash)^2.</summary>
    public long DragAt(long s) => Drag * s / Dash * s / Dash;

    /// <summary>The thrust of a throttle setting (raw a second squared).</summary>
    public long Thrust(Throttle t) => t switch
    {
        Throttle.Idle => ThrustIdle,
        Throttle.Cruise => ThrustCruise,
        Throttle.Military => ThrustMilitary,
        _ => ThrustAfterburner,
    };

    /// <summary>The fuel burn of a throttle setting, percent of nominal.</summary>
    public int Burn(Throttle t) => t switch
    {
        Throttle.Idle => BurnIdle,
        Throttle.Cruise => BurnCruise,
        Throttle.Military => BurnMilitary,
        _ => BurnAfterburner,
    };

    /// <summary>
    /// The speed change over one tick (raw a second) at speed s on throttle t, pulling aUsed (raw a
    /// second squared) and climbing at sinPermille (thousandths; negative: a dive): thrust - drag -
    /// turn bleed - gravity, a twentieth a tick (truncated toward zero).
    /// </summary>
    public long SpeedChange(long s, Throttle t, long aUsed, long sinPermille) => (Thrust(t) - Resistance(s, aUsed, sinPermille)) / 20;

    /// <summary>What thrust must beat at speed s pulling aUsed and climbing at sinPermille: drag + turn bleed + gravity (raw a second squared).</summary>
    public long Resistance(long s, long aUsed, long sinPermille)
    {
        var f = Math.Min(aUsed, TurnAccel) * 1000 / TurnAccel; // thousandths of its g limit
        return DragAt(s) + Bleed * f / 1000 * f / 1000 + Gravity * sinPermille / 1000;
    }
}

/// <summary>The kinds of target a Guard order engages (a mask; the command's filter byte).</summary>
[Flags]
public enum AirTargetKinds : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Manned aircraft (jets, later helicopters): flying units not tagged drone.</summary>
    Aircraft = 1,

    /// <summary>Drones: flying units tagged drone.</summary>
    Drones = 2,

    /// <summary>Vehicles: ground units that are neither soldiers nor structures.</summary>
    Vehicles = 4,

    /// <summary>Infantry: units on foot.</summary>
    Infantry = 8,

    /// <summary>Structures: units that never move (buildings, stations).</summary>
    Structures = 16,

    /// <summary>Everything.</summary>
    All = Aircraft | Drones | Vehicles | Infantry | Structures,
}

/// <summary>
/// An aircraft's mission-layer numbers: on Guard (the one automatic mode) it leaves its area or
/// route by at most HuntLeash to engage, and engages DefaultGuard kinds unless the order names others.
/// </summary>
public sealed record MissionSpec(Fixed HuntLeash, AirTargetKinds DefaultGuard);

/// <summary>
/// An aircraft's gun against air targets (content <c>airGun</c>; the plan's air-combat model, section 2.9;
/// data E8, behaviour E6): it fires a burst (a round) at an air target within the weapon's range and
/// <see cref="Cone"/> degrees of the shooter's velocity, no lock needed; each burst rolls the class's
/// hit chance x the crossing and range terms x the target's signature, and a hit deals the weapon's damage.
/// </summary>
/// <param name="Cone">Degrees off the shooter's velocity.</param>
/// <param name="HitBase">Per air class, the hit chance (percent) of a burst at its best, in class order.</param>
/// <param name="CrossHalf">The crossing rate (milliradians per second) that halves the chance.</param>
/// <param name="RangeFar">Percent of the chance left at full range.</param>
/// <param name="CeilingRaw">A ground gun's ceiling over its ground (raw, 512 a metre); 0: none.</param>
/// <param name="StrafeRange">On a strafing pass (E7): the slant range it opens fire at ground targets from; zero: the weapon's range.</param>
/// <param name="StrafeEvery">On a strafing pass: ticks between bursts; 0: the weapon's reload.</param>
public sealed record AirGun(int Cone, IReadOnlyList<(InterceptClass Class, int Chance)> HitBase, int CrossHalf, int RangeFar, long CeilingRaw,
    Fixed StrafeRange = default, int StrafeEvery = 0)
{
    /// <summary>The best hit chance (percent) of a burst against class c; 0 when it cannot engage it.</summary>
    public int HitOn(InterceptClass c)
    {
        for (var i = 0; i < HitBase.Count; i++) // indexed: foreach over the interface would box an enumerator per call
        {
            if (HitBase[i].Class == c)
            {
                return HitBase[i].Chance;
            }
        }
        return 0;
    }
}

/// <summary>A bomb's delivery profile (the plan's air-combat model, section 2.10).</summary>
public enum DeliveryProfile : byte
{
    /// <summary>Level: it holds the release height and speed.</summary>
    Level,

    /// <summary>Dive: pop up, roll in and dive at the dive angle.</summary>
    Dive,

    /// <summary>Toss: low and fast, a pull-up and release at the toss angle.</summary>
    Toss,
}

/// <summary>A guided bomb's hit roll at release (the Watcher's proven model): percents; the miss distances.</summary>
/// <param name="HitIdle">Percent chance to hit a target that stood still.</param>
/// <param name="HitMoving">Percent chance to hit a moving target.</param>
/// <param name="MissNear">A miss lands at least this far off.</param>
/// <param name="MissFar">And at most this far.</param>
public sealed record GuidedBomb(int HitIdle, int HitMoving, Fixed MissNear, Fixed MissFar);

/// <summary>
/// How an aircraft delivers a bomb (content <c>delivery</c>; the plan's air-combat model, section 2.10;
/// data E8, behaviour E7): the profile, the release height, the dive and toss angles, the initial point's
/// distance, the scatter per profile, a guided bomb's hit roll, the stick.
/// </summary>
/// <param name="Profile">Level, dive or toss.</param>
/// <param name="ReleaseHeightRaw">The release height over the ground (raw, 512 a metre).</param>
/// <param name="DiveAngle">The dive's angle, degrees.</param>
/// <param name="TossAngle">The toss's release angle, degrees.</param>
/// <param name="IpDistance">The initial point's distance from the target on the run-in line.</param>
/// <param name="ScatterLevel">Scatter (per axis, at most) on a level release.</param>
/// <param name="ScatterDive">On a dive.</param>
/// <param name="ScatterToss">On a toss.</param>
/// <param name="Guided">A guided bomb's hit roll, or null for free fall.</param>
/// <param name="Stick">Bombs released a pass.</param>
/// <param name="StickSpacing">The distance between a stick's bombs.</param>
/// <param name="GravityRaw">A free-fall bomb's gravity, raw a second squared (512 a metre; E7); 0 for a guided bomb without one.</param>
/// <param name="Modes">Its fire modes (H2; content <c>modes</c>, in order: the first is the default): how many bombs a pass releases at each
/// aim point; empty: <paramref name="Stick"/> a pass (the jets' bombs before H2).</param>
/// <param name="GroupRaw">H2: queued aim points within this distance (raw) of a pass's first one, along its line, are bombed in the same pass; 0: no grouping.</param>
/// <param name="GroupAsideRaw">H2: and at most this far off that line (raw).</param>
/// <param name="Retard">H2: a high-drag (retarded) bomb loses this permille of its horizontal speed a tick (0: a slick bomb).</param>
/// <param name="Overfly">H4: after a planned pass's last drop it flies on down its run line until it has passed over that drop, then breaks
/// (a high release: the bombs fall a long way ahead of it); false: it breaks at its last release (the jets).</param>
/// <param name="Precision">The stealth bomber's precision bombs: each bomb is steered onto its own point (no scatter: it lands exactly on its
/// drop, aside of the run line too, within groupAside), every drop due in a tick leaves in that tick, and Shift-queued aims share the
/// pass's bombs out, each aim its own (<see cref="Sim.World"/>'s pass plan).</param>
/// <param name="MaxAims">The most aims an aircraft carrying it may queue (Shift; 0: the default 8).</param>
/// <param name="Tier">Its blast's airstrike tier for renderers (1 to 7; 0: by its damage, <see cref="Sim.World.StrikeTier"/>).</param>
public sealed record Delivery(DeliveryProfile Profile, long ReleaseHeightRaw, int DiveAngle, int TossAngle, Fixed IpDistance,
    Fixed ScatterLevel, Fixed ScatterDive, Fixed ScatterToss, GuidedBomb? Guided, int Stick, Fixed StickSpacing, long GravityRaw = 0,
    IReadOnlyList<FireModeDef>? Modes = null, long GroupRaw = 0, long GroupAsideRaw = 0, int Retard = 0, bool Overfly = false,
    bool Precision = false, int MaxAims = 0, int Tier = 0)
{
    /// <summary>Its fire modes (never null; empty without any).</summary>
    public IReadOnlyList<FireModeDef> FireModes => Modes ?? [];

    /// <summary>How many bombs a pass releases at an aim point in fire mode <paramref name="mode"/> (an index; out of range: the first), or the stick.</summary>
    public int SalvoOf(int mode) => FireModes.Count == 0 ? Stick : FireModes[mode >= 0 && mode < FireModes.Count ? mode : 0].Salvo;

    /// <summary>Whether fire mode <paramref name="mode"/> (an index; out of range: the first) spreads its salvo along the pass (H4: a carpet).</summary>
    public bool SpreadOf(int mode) => FireModes.Count > 0 && FireModes[mode >= 0 && mode < FireModes.Count ? mode : 0].Spread;

    /// <summary>Fire mode <paramref name="mode"/> (an index; out of range: the first), or null without modes.</summary>
    public FireModeDef? ModeOf(int mode) => FireModes.Count == 0 ? null : FireModes[mode >= 0 && mode < FireModes.Count ? mode : 0];

    /// <summary>Whether any of its fire modes lays rows across the path (the supersonic bomber).</summary>
    public bool HasRows
    {
        get
        {
            foreach (var m in FireModes)
            {
                if (m.Across > 1)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Whether any of its fire modes is a carpet (H4).</summary>
    public bool HasSpread
    {
        get
        {
            foreach (var m in FireModes)
            {
                if (m.Spread)
                {
                    return true;
                }
            }
            return false;
        }
    }
}

/// <summary>A bomb weapon's fire mode (H2; content <c>delivery.modes</c>): its ID (for the card) and the bombs a pass releases at each aim point.</summary>
/// <param name="Id">The mode's content ID ("single", "salvo4", ...).</param>
/// <param name="Salvo">Bombs released a pass at each aim point (a stick along its path, <see cref="Delivery.StickSpacing"/> apart, centred on it).</param>
/// <param name="Spread">H4 (a carpet): the salvo is one stick for the whole pass, not one at each aim: spread evenly from its first aim to its
/// last (aims bombed together, a line's ends), at least <see cref="Delivery.StickSpacing"/> apart (shorter: stretched about the middle).</param>
/// <param name="Across">The supersonic bomber (B-1 type): bombs side by side in a row <b>across</b> the flight path (1: none, a stick along it). The salvo
/// is laid as Salvo / Across rows (each row where a stick's bomb would go: at each aim, or spread), each row of Across bombs centred on its
/// place, <paramref name="AcrossSpacingRaw"/> apart; a row's bombs leave the aircraft in the same tick.</param>
/// <param name="AcrossSpacingRaw">The distance between a row's bombs (raw); 0: the delivery's <see cref="Delivery.StickSpacing"/>.</param>
public sealed record FireModeDef(string Id, int Salvo, bool Spread = false, int Across = 1, long AcrossSpacingRaw = 0)
{
    /// <summary>Rows a pass lays at an aim (or spreads): the salvo over the bombs a row (at least 1).</summary>
    public int Rows => Math.Max(1, Salvo / Math.Max(1, Across));
}
