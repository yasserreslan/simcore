using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

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
public sealed class Unit
{
    private readonly int[] _charges;

    internal Unit(uint id, UnitDef def, Vec pos, int facing)
    {
        Id = id;
        Def = def;
        Pos = pos;
        Facing = facing;
        TurretFacing = facing;
        HP = def.MaxHp;
        Owner = def.Player;
        Lifetime = def.Lifetime;
        WeaponMode = def.Archetype.WeaponMode;
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

    /// <summary>The hull facing, 0..31.</summary>
    public int Facing { get; internal set; }

    /// <summary>The turret facing, 0..31; equals <see cref="Facing"/> for units without a turret.</summary>
    public int TurretFacing { get; internal set; }

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

    /// <summary>The vehicle this unit is driving, or 0. A driver is off the map: it can't be seen, hit or ordered except to exit.</summary>
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

    /// <summary>Bit p set: player p has revealed this hidden unit, for good.</summary>
    public uint RevealedTo { get; internal set; }

    /// <summary>The unit that placed or launched this one, or 0.</summary>
    public uint Source { get; internal set; }

    /// <summary>Ticks left before it expires, for units with a <see cref="UnitDef.Lifetime"/>; otherwise 0.</summary>
    public int Lifetime { get; internal set; }

    /// <summary>A supplier's remaining stock of salvos and charges; unused when its stock is unlimited.</summary>
    public int Stock { get; internal set; }

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

    /// <summary>Whether the unit still has path to follow.</summary>
    public bool Moving => PathIdx < Path.Count;

    /// <summary>Whether the unit can fire and use abilities (a garrisoned soldier fires from inside but can't move): it is not driving, and if it has a driver seat it is filled.</summary>
    public bool CanAct => Inside == 0 && (Def.Crew is null || Driver != 0);

    internal int[] MutableCharges => _charges;

    /// <summary>What routing wants this unit to do this tick (see <see cref="MoveIntent"/>).</summary>
    public MoveIntent Intent { get; internal set; }

    internal void SetPath(Cell[] path)
    {
        Path = path;
        PathIdx = 0;
    }

    internal void ClearPath()
    {
        Path = [];
        PathIdx = 0;
    }

    internal void Idle()
    {
        Order = default;
        ClearPath();
    }
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
        if (weapon.Effect.Guided is not null)
        {
            Guided = true;
            var d = dest - shooter.Pos;
            Vel = d.X.Raw == 0 && d.Y.Raw == 0 ? Facing.Dir(shooter.TurretFacing).WithLen(weapon.Effect.ProjectileSpeed) : d.WithLen(weapon.Effect.ProjectileSpeed);
        }
    }

    /// <summary>A guided missile's velocity per tick (zero for other projectiles).</summary>
    public Vec Vel { get; internal set; }

    /// <summary>Whether a guided missile is still steering toward its target.</summary>
    public bool Guided { get; internal set; }

    /// <summary>How far a guided missile has flown.</summary>
    public Fixed Flown { get; internal set; }

    /// <summary>How far an unguided guided missile still flies before it hits the ground.</summary>
    public Fixed DropLeft { get; internal set; }

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
