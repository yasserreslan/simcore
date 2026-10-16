using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>A unit's armor class.</summary>
public enum Armor : byte
{
    /// <summary>Infantry.</summary>
    None,

    /// <summary>Light vehicles.</summary>
    Light,

    /// <summary>Tanks and heavy vehicles.</summary>
    Heavy,

    /// <summary>Structures.</summary>
    Building,
}

/// <summary>The damage type a weapon deals.</summary>
public enum Warhead : byte
{
    /// <summary>Small arms.</summary>
    Bullet,

    /// <summary>Armor-piercing shells.</summary>
    AP,

    /// <summary>Rockets.</summary>
    Rocket,

    /// <summary>High explosive.</summary>
    HE,
}

/// <summary>
/// A unit's weapon. Shots hit instantly; <see cref="Scatter"/> offsets where a
/// splash shell lands.
/// </summary>
public sealed class Weapon
{
    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>Damage per hit before armor.</summary>
    public required int Damage { get; init; }

    /// <summary>The maximum firing distance.</summary>
    public required Fixed Range { get; init; }

    /// <summary>Ticks between shots.</summary>
    public required int ReloadTicks { get; init; }

    /// <summary>The damage type.</summary>
    public required Warhead Warhead { get; init; }

    /// <summary>The splash radius; zero hits a single target.</summary>
    public Fixed Splash { get; init; }

    /// <summary>The maximum landing offset per axis.</summary>
    public Fixed Scatter { get; init; }
}

/// <summary>Identifies a unit type.</summary>
public enum UnitTypeId : byte
{
    /// <summary>Basic infantry.</summary>
    Rifleman,

    /// <summary>Anti-armor infantry.</summary>
    RocketTrooper,

    /// <summary>Fast light vehicle.</summary>
    ScoutBuggy,

    /// <summary>Main battle tank.</summary>
    BattleTank,

    /// <summary>Long-range splash.</summary>
    Artillery,

    /// <summary>Gathers resources.</summary>
    Harvester,

    /// <summary>Deploys into a base.</summary>
    ConstructionVehicle,
}

/// <summary>The static definition of a kind of unit.</summary>
public sealed class UnitType
{
    /// <summary>The stable data key.</summary>
    public required string Key { get; init; }

    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>The debug-view letter.</summary>
    public required char Glyph { get; init; }

    /// <summary>The build cost in credits.</summary>
    public required int Cost { get; init; }

    /// <summary>Starting hit points.</summary>
    public required int MaxHP { get; init; }

    /// <summary>Distance per tick; zero means the unit cannot move.</summary>
    public required Fixed Speed { get; init; }

    /// <summary>Facings per tick.</summary>
    public required int TurnRate { get; init; }

    /// <summary>The armor class.</summary>
    public required Armor Armor { get; init; }

    /// <summary>Whether the weapon aims independently of the hull.</summary>
    public bool Turret { get; init; }

    /// <summary>The weapon, or null for unarmed units.</summary>
    public Weapon? Weapon { get; init; }
}

/// <summary>Every unit definition. Values are the starting stats from the design doc, for tuning.</summary>
public static class UnitTypes
{
    // DamageTable[warhead][armor] is the percent of weapon damage dealt.
    private static readonly long[][] DamageTable =
    [
        [100, 60, 20, 15], // Bullet
        [30, 75, 100, 50], // AP
        [25, 90, 100, 60], // Rocket
        [90, 70, 40, 100], // HE
    ];

    internal static readonly Weapon Rifle = new()
    {
        Name = "Rifle", Damage = 15, Range = Cells(40), ReloadTicks = 20, Warhead = Warhead.Bullet,
    };

    internal static readonly Weapon Rocket = new()
    {
        Name = "Rocket", Damage = 60, Range = Cells(50), ReloadTicks = 60, Warhead = Warhead.Rocket,
    };

    internal static readonly Weapon MG = new()
    {
        Name = "MG", Damage = 10, Range = Cells(40), ReloadTicks = 10, Warhead = Warhead.Bullet,
    };

    internal static readonly Weapon Cannon = new()
    {
        Name = "Cannon", Damage = 60, Range = Cells(45), ReloadTicks = 40, Warhead = Warhead.AP,
    };

    internal static readonly Weapon Shell = new()
    {
        Name = "Shell", Damage = 80, Range = Cells(90), ReloadTicks = 100, Warhead = Warhead.HE,
        Splash = Cells(10), Scatter = Cells(5),
    };

    private static readonly UnitType[] All =
    [
        new()
        {
            Key = "rifleman", Name = "Rifleman", Glyph = 'R', Cost = 100, MaxHP = 50,
            Speed = CellsPerSecond(10), TurnRate = 8, Armor = Armor.None, Weapon = Rifle,
        },
        new()
        {
            Key = "rocket_trooper", Name = "Rocket trooper", Glyph = 'K', Cost = 300, MaxHP = 45,
            Speed = CellsPerSecond(9), TurnRate = 8, Armor = Armor.None, Weapon = Rocket,
        },
        new()
        {
            Key = "scout_buggy", Name = "Scout buggy", Glyph = 'B', Cost = 400, MaxHP = 150,
            Speed = CellsPerSecond(30), TurnRate = 2, Armor = Armor.Light, Turret = true, Weapon = MG,
        },
        new()
        {
            Key = "battle_tank", Name = "Battle tank", Glyph = 'T', Cost = 800, MaxHP = 400,
            Speed = CellsPerSecond(16), TurnRate = 1, Armor = Armor.Heavy, Turret = true, Weapon = Cannon,
        },
        new()
        {
            Key = "artillery", Name = "Artillery", Glyph = 'A', Cost = 900, MaxHP = 150,
            Speed = CellsPerSecond(12), TurnRate = 1, Armor = Armor.Light, Weapon = Shell,
        },
        new()
        {
            Key = "harvester", Name = "Harvester", Glyph = 'H', Cost = 1400, MaxHP = 600,
            Speed = CellsPerSecond(12), TurnRate = 1, Armor = Armor.Heavy,
        },
        new()
        {
            Key = "construction_vehicle", Name = "Construction vehicle", Glyph = 'M', Cost = 2500, MaxHP = 800,
            Speed = CellsPerSecond(8), TurnRate = 1, Armor = Armor.Heavy,
        },
    ];

    /// <summary>Returns the definition for a unit type.</summary>
    public static UnitType Of(UnitTypeId id) => All[(int)id];

    // Cells converts tenths of a cell to Fixed.
    private static Fixed Cells(int tenths) => Fixed.Frac(tenths, 10);

    // CellsPerSecond converts a speed in tenths of a cell per second to Fixed
    // per tick.
    private static Fixed CellsPerSecond(int tenths) => Fixed.Frac(tenths, 10 * World.TicksPerSecond);

    /// <summary>
    /// Returns the damage weapon w deals to armor a, scaled by pct (100 = full
    /// hit, less at the edge of a splash). It is at least 1.
    /// </summary>
    internal static int Damage(Weapon w, Armor a, long pct)
    {
        var d = (long)w.Damage * DamageTable[(int)w.Warhead][(int)a] / 100 * pct / 100;
        return (int)Math.Max(d, 1);
    }
}
