namespace SimCore.Model;

/// <summary>Thrown when a match setup does not fit the content; the message names the player, preset or entry at fault.</summary>
public class MatchSetupException(string message) : Exception(message);

/// <summary>Thrown when a match setup was made for different content than the content loaded.</summary>
public sealed class ContentHashMismatchException(string setupHash, string contentHash)
    : MatchSetupException(
        $"match: the setup's content hash \"{setupHash}\" does not match the loaded content ({contentHash}); " +
        "every peer must load the content the match was set up with");

/// <summary>A starting unit: its definition, and the definition of the driver it starts with, if any.</summary>
/// <param name="Def">The unit.</param>
/// <param name="Driver">The driver spawned into its seat, or null.</param>
public readonly record struct ArmyUnit(UnitDef Def, UnitDef? Driver);

/// <summary>A player as compiled at match start.</summary>
public sealed class CompiledPlayer
{
    /// <summary>The player's ID.</summary>
    public required byte Id { get; init; }

    /// <summary>The faction.</summary>
    public required Faction Faction { get; init; }

    /// <summary>
    /// The definition of each archetype without slots that the faction allows,
    /// by archetype index (null elsewhere): vehicles, structures, traps, drones.
    /// </summary>
    public required IReadOnlyList<UnitDef?> Defaults { get; init; }

    /// <summary>The preset definitions, in setup order.</summary>
    public required IReadOnlyList<UnitDef> Presets { get; init; }

    /// <summary>The starting units, in spawn order.</summary>
    public required IReadOnlyList<ArmyUnit> Army { get; init; }

    /// <summary>Returns the preset with this name, or the default definition of the archetype with this ID, or null.</summary>
    public UnitDef? Find(ContentSet content, string name)
    {
        foreach (var d in Presets)
        {
            if (d.Name == name)
            {
                return d;
            }
        }
        return content.Archetype(name) is { } a ? Defaults[a.Index] : null;
    }
}

/// <summary>A match setup checked against content and compiled into unit definitions.</summary>
public sealed class CompiledMatch
{
    /// <summary>The content.</summary>
    public required ContentSet Content { get; init; }

    /// <summary>The setup.</summary>
    public required MatchSetup Setup { get; init; }

    /// <summary>Every unit definition: per player in ID order, the defaults in archetype order, then the presets.</summary>
    public required IReadOnlyList<UnitDef> Defs { get; init; }

    /// <summary>The players, in ID order.</summary>
    public required IReadOnlyList<CompiledPlayer> Players { get; init; }

    /// <summary>The setup's <see cref="MatchSetup.Digest"/>.</summary>
    public required ulong Digest { get; init; }
}

/// <summary>Checks a <see cref="MatchSetup"/> against a <see cref="ContentSet"/> and compiles its unit definitions.</summary>
public static class MatchCompiler
{
    private const int MaxNameLength = 40;

    /// <summary>Validates setup and compiles every player's unit definitions.</summary>
    /// <exception cref="ContentHashMismatchException">The setup was made for other content.</exception>
    /// <exception cref="MatchSetupException">The setup is malformed or breaks a faction or loadout rule.</exception>
    public static CompiledMatch Compile(ContentSet content, MatchSetup setup)
    {
        if (setup is null)
        {
            throw new MatchSetupException("match: missing setup");
        }
        if (setup.ContentHash != content.Hash)
        {
            throw new ContentHashMismatchException(setup.ContentHash ?? "", content.Hash);
        }
        if (setup.Players is null || setup.Players.Count == 0 || setup.Players.Count > MatchSetup.MaxPlayers)
        {
            throw new MatchSetupException($"match: want 1..{MatchSetup.MaxPlayers} players, got {setup.Players?.Count ?? 0}");
        }

        var defs = new List<UnitDef>();
        var players = new List<CompiledPlayer>();
        for (var i = 0; i < setup.Players.Count; i++)
        {
            var p = setup.Players[i] ?? throw new MatchSetupException($"match: player #{i + 1} is null");
            if (p.Id != i + 1)
            {
                throw new MatchSetupException($"match: player #{i + 1} has id {p.Id}, want {i + 1}");
            }
            players.Add(CompilePlayer(content, p, defs));
        }
        return new CompiledMatch { Content = content, Setup = setup, Defs = defs, Players = players, Digest = setup.Digest() };
    }

    private static CompiledPlayer CompilePlayer(ContentSet content, PlayerSetup p, List<UnitDef> defs)
    {
        var who = $"match: player {p.Id}";
        var faction = content.Faction(p.Faction ?? "") ?? throw new MatchSetupException($"{who}: unknown faction \"{p.Faction}\"");
        if (p.Presets is null || p.Army is null)
        {
            throw new MatchSetupException($"{who}: missing presets or army");
        }

        var defaults = new UnitDef?[content.Archetypes.Count];
        foreach (var a in content.Archetypes)
        {
            if (faction.Archetypes[a.Index] && !a.HasSlots)
            {
                var comps = a.Components.Select(ci => content.Components[ci]).ToList();
                defaults[a.Index] = Build(content, faction, p.Id, a, comps, a.Id, false, $"{who}: archetype \"{a.Id}\"", defs);
            }
        }

        if (p.Presets.Count > MatchSetup.MaxPresets)
        {
            throw new MatchSetupException($"{who}: {p.Presets.Count} presets, max {MatchSetup.MaxPresets}");
        }
        var presets = new List<UnitDef>();
        var perArchetype = new int[content.Archetypes.Count];
        foreach (var pr in p.Presets)
        {
            if (pr is null)
            {
                throw new MatchSetupException($"{who}: null preset");
            }
            var where = $"{who} preset \"{pr.Name}\"";
            CheckName(content, pr.Name, where);
            if (presets.Exists(d => d.Name == pr.Name))
            {
                throw new MatchSetupException($"{where}: name used twice");
            }
            var a = content.Archetype(pr.Archetype ?? "") ?? throw new MatchSetupException($"{where}: unknown archetype \"{pr.Archetype}\"");
            if (!faction.Archetypes[a.Index])
            {
                throw new MatchSetupException($"{where}: faction \"{faction.Id}\" does not allow archetype \"{a.Id}\"");
            }
            if (!a.HasSlots)
            {
                throw new MatchSetupException($"{where}: archetype \"{a.Id}\" has no loadout slots; put \"{a.Id}\" in the army directly");
            }
            if (++perArchetype[a.Index] > faction.MaxPresetsPerArchetype)
            {
                throw new MatchSetupException(
                    $"{where}: more than {faction.MaxPresetsPerArchetype} presets for archetype \"{a.Id}\" (faction \"{faction.Id}\")");
            }
            var comps = CheckLoadout(content, faction, a, pr, where);
            comps.AddRange(a.Components.Select(ci => content.Components[ci]));
            presets.Add(Build(content, faction, p.Id, a, comps, pr.Name, true, where, defs));
        }

        var army = new List<ArmyUnit>();
        for (var i = 0; i < p.Army.Count; i++)
        {
            var e = p.Army[i] ?? throw new MatchSetupException($"{who}: army entry #{i + 1} is null");
            var where = $"{who} army entry #{i + 1} \"{e.Unit}\"";
            var def = ArmyDef(content, faction, presets, defaults, e.Unit, where);
            UnitDef? driver = null;
            if (e.Driver is not null)
            {
                driver = ArmyDef(content, faction, presets, defaults, e.Driver, $"{where} driver");
                if (def.Crew is not { } crew)
                {
                    throw new MatchSetupException($"{where}: has a driver, but \"{def.Name}\" has no driver seat");
                }
                if (driver.Crew is not null || !driver.Tags.Matches(crew.Tags))
                {
                    throw new MatchSetupException($"{where}: \"{driver.Name}\" cannot drive \"{def.Name}\"");
                }
            }
            if (e.Count < 1 || e.Count > MatchSetup.MaxArmy)
            {
                throw new MatchSetupException($"{where}: count is {e.Count}, want 1..{MatchSetup.MaxArmy}");
            }
            for (var n = 0; n < e.Count; n++)
            {
                army.Add(new ArmyUnit(def, driver));
            }
            if (army.Count + army.Count(u => u.Driver is not null) > MatchSetup.MaxArmy)
            {
                throw new MatchSetupException($"{who}: army has more than {MatchSetup.MaxArmy} units");
            }
        }
        return new CompiledPlayer { Id = p.Id, Faction = faction, Defaults = defaults, Presets = presets, Army = army };
    }

    // ArmyDef resolves an army entry's unit: a preset name, or an archetype
    // without slots. Traps and drones come from abilities, never the army.
    private static UnitDef ArmyDef(
        ContentSet content, Faction faction, List<UnitDef> presets, UnitDef?[] defaults, string? name, string where)
    {
        var def = presets.Find(d => d.Name == name);
        if (def is null)
        {
            var a = content.Archetype(name ?? "") ?? throw new MatchSetupException($"{where}: \"{name}\" is neither a preset nor an archetype");
            if (!faction.Archetypes[a.Index])
            {
                throw new MatchSetupException($"{where}: faction \"{faction.Id}\" does not allow archetype \"{a.Id}\"");
            }
            def = defaults[a.Index] ?? throw new MatchSetupException(
                $"{where}: archetype \"{a.Id}\" has loadout slots; name one of the player's presets for it instead");
        }
        if (def.Hidden || def.Lifetime > 0)
        {
            throw new MatchSetupException($"{where}: \"{def.Name}\" is placed or launched by other units, not fielded");
        }
        return def;
    }

    // CheckLoadout enforces the preset rules: one component per slot, each
    // from the archetype's list for that slot and allowed by the faction.
    private static List<Component> CheckLoadout(ContentSet content, Faction faction, Archetype a, Preset pr, string where)
    {
        var slots = a.SlotChoices.Where(s => s.Count > 0).ToList();
        if (pr.Components is null || pr.Components.Count != slots.Count)
        {
            throw new MatchSetupException(
                $"{where}: want {slots.Count} components (one per slot of \"{a.Id}\"), got {pr.Components?.Count ?? 0}");
        }
        var comps = new List<Component>();
        for (var i = 0; i < slots.Count; i++)
        {
            var id = pr.Components[i];
            var slot = SlotName((SlotKind)i);
            var c = content.Component(id ?? "") ?? throw new MatchSetupException($"{where}: unknown component \"{id}\"");
            if (!slots[i].Contains(c.Index))
            {
                var allowed = string.Join(", ", slots[i].Select(ci => content.Components[ci].Id));
                throw new MatchSetupException($"{where}: \"{c.Id}\" does not fit the {slot} slot of \"{a.Id}\" (want one of {allowed})");
            }
            if (!faction.Components[c.Index])
            {
                throw new MatchSetupException($"{where}: faction \"{faction.Id}\" does not allow component \"{c.Id}\"");
            }
            comps.Add(c);
        }
        return comps;
    }

    private static void CheckName(ContentSet content, string? name, string where)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            throw new MatchSetupException($"{where}: bad name (want 1..{MaxNameLength} printable characters)");
        }
        if (content.Archetype(name) is not null)
        {
            throw new MatchSetupException($"{where}: name clashes with an archetype ID");
        }
    }

    private static string SlotName(SlotKind s) => s == SlotKind.Primary ? "primary" : "secondary";

    // Build runs the stats pipeline for one loadout and adds the definition to defs.
    private static UnitDef Build(
        ContentSet content, Faction faction, byte player, Archetype a, List<Component> comps, string name, bool preset,
        string where, List<UnitDef> defs)
    {
        var effects = comps.SelectMany(c => c.Effects).ToList();
        foreach (var (kind, n) in new[]
                 {
                     ("trigger", effects.Count(e => e is TriggerEffect)),
                     ("strike", effects.Count(e => e is StrikeEffect)),
                     ("supply", effects.Count(e => e is SupplyEffect)),
                     ("smoke", effects.Count(e => e is SmokeEffect)),
                 })
        {
            if (n > 1)
            {
                throw new MatchSetupException($"{where}: carries {n} {kind} effects, max 1");
            }
        }

        var mods = faction.Modifiers.Where(m => a.Tags.Matches(m.Tags)).Select(m => m.Modifier)
            .Concat(effects.OfType<ModifierEffect>().Select(m => m.Modifier)).ToList();
        var (pct, flat) = StatPipeline.Sum(mods);
        long Mod(Stat s, long v) => StatPipeline.Apply(s, v, pct[(int)s], flat[(int)s]);

        var stockMax = new List<int>();
        var weapons = new List<WeaponDef>();
        var abilities = new List<AbilityDef>();
        var smokeStock = -1;
        foreach (var c in comps)
        {
            foreach (var e in c.Effects)
            {
                switch (e)
                {
                    case WeaponEffect w:
                        weapons.Add(new WeaponDef
                        {
                            Index = weapons.Count,
                            Effect = w,
                            Damage = (int)Mod(Stat.Damage, w.Damage),
                            Range = FixedPoint.Fixed.FromRaw(Mod(Stat.Range, w.Range.Raw)),
                            Reload = (int)Mod(Stat.Reload, w.Reload),
                            Stock = w.Ammo > 0 ? AddStock(stockMax, w.Ammo) : -1,
                        });
                        break;
                    case PlacerEffect pl:
                        CheckSpawnable(content, faction, c, pl.Archetype, where);
                        abilities.Add(new AbilityDef
                        {
                            Component = c.Index, Effect = pl, Stock = AddStock(stockMax, pl.Charges),
                            Range = pl.Range, Cooldown = pl.Cooldown, Archetype = pl.Archetype,
                        });
                        break;
                    case LaunchEffect l:
                        CheckSpawnable(content, faction, c, l.Archetype, where);
                        abilities.Add(new AbilityDef
                        {
                            Component = c.Index, Effect = l, Stock = AddStock(stockMax, l.Charges),
                            Range = l.Range, Cooldown = l.Cooldown, Archetype = l.Archetype,
                        });
                        break;
                    case SmokeEffect sm:
                        smokeStock = AddStock(stockMax, sm.Charges);
                        break;
                }
            }
        }
        for (var i = 1; i < abilities.Count; i++)
        {
            if (abilities[i].Component == abilities[i - 1].Component)
            {
                throw new MatchSetupException($"{where}: component \"{content.Components[abilities[i].Component].Id}\" carries more than one placer or launch effect");
            }
        }

        var def = new UnitDef
        {
            Index = defs.Count,
            Player = player,
            Name = name,
            IsPreset = preset,
            Archetype = a,
            Components = comps,
            MaxHp = (int)Mod(Stat.MaxHp, a.MaxHp),
            Speed = FixedPoint.Fixed.FromRaw(Mod(Stat.Speed, a.Speed.Raw)),
            TurnRate = (int)Mod(Stat.TurnRate, a.TurnRate),
            Cost = (int)Mod(Stat.Cost, a.Cost + comps.Sum(c => c.Cost)),
            Vision = FixedPoint.Fixed.FromRaw(Mod(Stat.Vision, a.Vision.Raw)),
            Weapons = weapons,
            Abilities = abilities,
            StockMax = stockMax,
            Trigger = effects.OfType<TriggerEffect>().FirstOrDefault(),
            Strike = effects.OfType<StrikeEffect>().FirstOrDefault(),
            Supply = effects.OfType<SupplyEffect>().FirstOrDefault(),
            Cargo = effects.OfType<CargoEffect>().FirstOrDefault(),
            Builder = effects.OfType<BuilderEffect>().FirstOrDefault() is { } be
                ? new BuilderEffect { Rate = (int)Math.Max(1, Mod(Stat.BuildRate, be.Rate)), Range = be.Range }
                : null,
            Repairs = [.. effects.OfType<RepairEffect>()],
            Auras = [.. effects.OfType<AuraEffect>().Where(e => e.Heal > 0)],
            Smoke = effects.OfType<SmokeEffect>().FirstOrDefault(),
            SmokeStock = smokeStock,
        };
        defs.Add(def);
        return def;
    }

    private static int AddStock(List<int> stockMax, int max)
    {
        stockMax.Add(max);
        return stockMax.Count - 1;
    }

    // CheckSpawnable makes sure what a unit places or launches is an
    // archetype the faction allows and that has no slots.
    private static void CheckSpawnable(ContentSet content, Faction faction, Component c, int archetype, string where)
    {
        var a = content.Archetypes[archetype];
        if (!faction.Archetypes[a.Index] || a.HasSlots)
        {
            throw new MatchSetupException(
                $"{where}: component \"{c.Id}\" spawns \"{a.Id}\", which faction \"{faction.Id}\" does not allow as a fixed-loadout archetype");
        }
    }
}
