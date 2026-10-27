using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>One weapon of a <see cref="UnitDef"/>, with the unit's modifiers applied.</summary>
public sealed class WeaponDef
{
    /// <summary>The position in <see cref="UnitDef.Weapons"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The weapon as defined in content.</summary>
    public required WeaponEffect Effect { get; init; }

    /// <summary>Damage per hit before armor.</summary>
    public required int Damage { get; init; }

    /// <summary>The maximum firing distance.</summary>
    public required Fixed Range { get; init; }

    /// <summary>Ticks between shots (between salvos for a launcher).</summary>
    public required int Reload { get; init; }

    /// <summary>The index of its ammo in <see cref="UnitDef.StockMax"/>, or -1 for unlimited ammo.</summary>
    public required int Stock { get; init; }

    /// <summary>The warhead.</summary>
    public Warhead Warhead => Effect.Warhead;

    /// <summary>
    /// How much damage per tick it deals to armor class armor, in arbitrary
    /// integer units, for picking the right weapon; 0 if it cannot hit the class.
    /// </summary>
    public long Score(int armor)
    {
        var v = Warhead.Versus[armor];
        return v == Warhead.CannotTarget ? 0 : (long)Damage * v * Effect.SalvoSize * 1000 / Reload;
    }
}

/// <summary>One targeted ability of a <see cref="UnitDef"/>: a placer or a launcher.</summary>
public sealed class AbilityDef
{
    /// <summary>The component it comes from, as an index into <see cref="ContentSet.Components"/>; commands name it by ID.</summary>
    public required int Component { get; init; }

    /// <summary>The <see cref="PlacerEffect"/> or <see cref="LaunchEffect"/>.</summary>
    public required Effect Effect { get; init; }

    /// <summary>The index of its charges in <see cref="UnitDef.StockMax"/>.</summary>
    public required int Stock { get; init; }

    /// <summary>The maximum distance from the unit to the target.</summary>
    public required Fixed Range { get; init; }

    /// <summary>Ticks between uses.</summary>
    public required int Cooldown { get; init; }

    /// <summary>The archetype placed or launched, as an index into <see cref="ContentSet.Archetypes"/>.</summary>
    public required int Archetype { get; init; }

    /// <summary>Whether it targets a cell only (placers) rather than a unit or a cell (launchers).</summary>
    public bool CellOnly => Effect is PlacerEffect;
}

/// <summary>
/// A per-player unit definition, compiled once at match start from an
/// archetype and its components (a preset's picks plus the archetype's fixed
/// components) with the faction's modifiers and the components' perks
/// applied. Units point at one of these; nothing in it changes during the match.
/// </summary>
public sealed class UnitDef
{
    /// <summary>The position in <see cref="CompiledMatch.Defs"/>; hashed per unit.</summary>
    public required int Index { get; init; }

    /// <summary>The owning player.</summary>
    public required byte Player { get; init; }

    /// <summary>The preset name, or the archetype ID for an archetype without slots.</summary>
    public required string Name { get; init; }

    /// <summary>Whether this is a player preset.</summary>
    public required bool IsPreset { get; init; }

    /// <summary>The archetype.</summary>
    public required Archetype Archetype { get; init; }

    /// <summary>The components: the slot picks in slot order, then the archetype's fixed components.</summary>
    public required IReadOnlyList<Component> Components { get; init; }

    /// <summary>Maximum hit points.</summary>
    public required int MaxHp { get; init; }

    /// <summary>Distance per tick; zero means the unit cannot move.</summary>
    public required Fixed Speed { get; init; }

    /// <summary>Facings per tick.</summary>
    public required int TurnRate { get; init; }

    /// <summary>The cost in credits: the archetype's plus its components'.</summary>
    public required int Cost { get; init; }

    /// <summary>How far the unit sees.</summary>
    public required Fixed Vision { get; init; }

    /// <summary>The weapons, in component order.</summary>
    public required IReadOnlyList<WeaponDef> Weapons { get; init; }

    /// <summary>The targeted abilities, in component order.</summary>
    public required IReadOnlyList<AbilityDef> Abilities { get; init; }

    /// <summary>
    /// The capacity of each limited stock the unit carries (launcher salvos,
    /// drone and trap charges); units start full and supply refills them.
    /// </summary>
    public required IReadOnlyList<int> StockMax { get; init; }

    /// <summary>
    /// For an aircraft with loadout modes (<see cref="Model.Archetype.Loadouts"/>: the stealth multirole), each mode's capacity of
    /// every stock, by mode then stock (each at most <see cref="StockMax"/>); null for every other unit. Read it through
    /// <see cref="Sim.Unit.StockMax"/>, which picks the unit's mode.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>>? LoadoutStocks { get; init; }

    /// <summary>
    /// Per stock in <see cref="StockMax"/>: the upgrade its weapon needs (a bit index into <see cref="ContentSet.Upgrades"/>), or
    /// -1; null when no weapon of the unit needs one. A stock whose upgrade the unit lacks holds nothing
    /// (<see cref="Sim.Unit.StockMax"/>).
    /// </summary>
    public IReadOnlyList<int>? StockUpgrades { get; init; }

    /// <summary>The trap trigger, or null.</summary>
    public required TriggerEffect? Trigger { get; init; }

    /// <summary>The contact strike of a launched drone, or null.</summary>
    public required StrikeEffect? Strike { get; init; }

    /// <summary>The supply it gives out, or null.</summary>
    public required SupplyEffect? Supply { get; init; }

    /// <summary>The cargo it carries to depots (a cargo truck), or null.</summary>
    public CargoEffect? Cargo { get; init; }

    /// <summary>How it builds structures (a worker), its rate after perks (Stat.BuildRate), or null.</summary>
    public BuilderEffect? Builder { get; init; }

    /// <summary>The repair effects.</summary>
    public required IReadOnlyList<RepairEffect> Repairs { get; init; }

    /// <summary>The aura effects that heal (the aura system), in component order.</summary>
    public IReadOnlyList<AuraEffect> Auras { get; init; } = [];

    /// <summary>Its smoke launchers (the smoke system), or null.</summary>
    public SmokeEffect? Smoke { get; init; }

    /// <summary>The index of its smoke charges in <see cref="StockMax"/>, or -1 without smoke launchers.</summary>
    public int SmokeStock { get; init; } = -1;

    /// <summary>Its active protection system (the APS system: the tank's Trophy), or null.</summary>
    public ApsEffect? Aps { get; init; }

    /// <summary>The index of its APS charges in <see cref="StockMax"/>, or -1 without one.</summary>
    public int ApsStock { get; init; } = -1;

    /// <summary>Whether a weapon of it is a coaxial mount (<see cref="WeaponEffect.Coaxial"/>: a tank's machine guns).</summary>
    public bool HasCoaxial { get; init; }

    /// <summary>
    /// The index in <see cref="Weapons"/> of its weapon that only cargo trucks reload (<see cref="WeaponEffect.TruckReload"/>: the
    /// howitzer's gun), or -1.
    /// </summary>
    public int TruckReloadWeapon { get; init; } = -1;

    /// <summary>The driver seat, or null.</summary>
    public CrewSpec? Crew => Archetype.Crew;

    /// <summary>How far it reveals hidden enemies.</summary>
    public Fixed Detect => Archetype.Detect;

    /// <summary>Ticks it lives; zero lives forever.</summary>
    public int Lifetime => Archetype.Lifetime;

    /// <summary>The layer the unit is on.</summary>
    public Layers Layer => Archetype.Layer;

    /// <summary>The armor class.</summary>
    public int Armor => Archetype.Armor;

    /// <summary>Whether the weapon aims independently of the hull.</summary>
    public bool Turret => Archetype.Turret;

    /// <summary>The tags.</summary>
    public TagSet Tags => Archetype.Tags;

    /// <summary>Whether enemies can't see or target the unit until they reveal it.</summary>
    public bool Hidden => Archetype.Hidden;

    /// <summary>Whether it is a structure with a footprint.</summary>
    public bool IsStructure => Archetype.IsStructure;

    /// <summary>The debug-view letter.</summary>
    public char Glyph => Archetype.Glyph;

    /// <summary>Returns the ability from the component with this content index, or null.</summary>
    public AbilityDef? Ability(int component)
    {
        for (var i = 0; i < Abilities.Count; i++)
        {
            if (Abilities[i].Component == component)
            {
                return Abilities[i];
            }
        }
        return null;
    }
}
