using System.Text.RegularExpressions;
using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Content;

// ContentCompiler checks parsed content files and builds the immutable
// ContentSet. Definitions are sorted by ID (ordinal) first, so the result is
// the same whatever order the files came in.
internal static partial class ContentCompiler
{
    // Limit keeps every number far from overflowing the sim's integer math.
    private const int Limit = 1_000_000;

    private const int MaxFootprint = 16;

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex IdPattern();

    private readonly record struct Src<T>(string File, string Id, T Def);

    public static ContentSet Compile(IReadOnlyList<(string File, ContentFile Data)> files)
    {
        var warheads = Collect(files, f => f.Warheads, w => w.Id, "warhead");
        var archetypes = Collect(files, f => f.Archetypes, a => a.Id, "archetype");
        var components = Collect(files, f => f.Components, c => c.Id, "component");
        var factions = Collect(files, f => f.Factions, f => f.Id, "faction");

        var tags = CollectTags(archetypes);
        var c = new Context(
            Index(warheads), Index(archetypes), Index(components), Index(tags.Select(t => (t, t)).ToList()), archetypes);

        var compiledWarheads = warheads.Select((w, i) => CompileWarhead(w, i)).ToList();
        var compiledComponents = components.Select((s, i) => CompileComponent(c, s, i, compiledWarheads)).ToList();
        var compiledArchetypes = archetypes.Select((s, i) => CompileArchetype(c, s, i, compiledComponents)).ToList();
        CheckUnlocks(components, compiledComponents, compiledArchetypes);
        var compiledFactions = factions.Select((s, i) => CompileFaction(c, s, i)).ToList();
        return new ContentSet(tags, compiledWarheads, compiledArchetypes, compiledComponents, compiledFactions);
    }

    // Context holds the ID → index maps every pass needs.
    private sealed record Context(
        Dictionary<string, int> Warheads,
        Dictionary<string, int> Archetypes,
        Dictionary<string, int> Components,
        Dictionary<string, int> Tags,
        List<Src<ArchetypeJson>> ArchetypeSources);

    // Collect gathers one kind of definition from every file, checks IDs and
    // duplicates, and sorts by ID.
    private static List<Src<T>> Collect<T>(
        IReadOnlyList<(string File, ContentFile Data)> files,
        Func<ContentFile, List<T>?> pick,
        Func<T, string?> id,
        string kind)
    {
        var output = new List<Src<T>>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, data) in files)
        {
            var defs = pick(data);
            if (defs is null)
            {
                continue;
            }
            for (var i = 0; i < defs.Count; i++)
            {
                var d = defs[i] ?? throw Fail(file, $"{kind} #{i + 1}: null definition");
                var key = id(d);
                if (string.IsNullOrEmpty(key))
                {
                    throw Fail(file, $"{kind} #{i + 1}: missing id");
                }
                if (!IdPattern().IsMatch(key))
                {
                    throw Fail(file, $"{kind} \"{key}\": bad id (want lowercase letters, digits and _)");
                }
                if (seen.TryGetValue(key, out var other))
                {
                    throw Fail(file, $"{kind} \"{key}\": already defined in {other}");
                }
                seen[key] = file;
                output.Add(new Src<T>(file, key, d));
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return output;
    }

    private static List<string> CollectTags(List<Src<ArchetypeJson>> archetypes)
    {
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var a in archetypes)
        {
            foreach (var t in a.Def.Tags ?? [])
            {
                if (t is null || !IdPattern().IsMatch(t))
                {
                    throw Fail(a.File, $"archetype \"{a.Id}\": bad tag \"{t}\"");
                }
                tags.Add(t);
            }
        }
        if (tags.Count > TagSet.MaxTags)
        {
            throw new ContentException($"content: {tags.Count} distinct tags, max {TagSet.MaxTags}");
        }
        return [.. tags];
    }

    private static Dictionary<string, int> Index<T>(List<Src<T>> items)
    {
        var m = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            m[items[i].Id] = i;
        }
        return m;
    }

    private static Dictionary<string, int> Index(List<(string Id, string)> items)
    {
        var m = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            m[items[i].Id] = i;
        }
        return m;
    }

    private static Warhead CompileWarhead(Src<WarheadJson> s, int index)
    {
        var versus = new int[Enum.GetValues<Armor>().Length];
        var given = s.Def.Versus ?? [];
        foreach (var a in Enum.GetValues<Armor>())
        {
            var key = ArmorName(a);
            if (!given.TryGetValue(key, out var pct))
            {
                throw Fail(s.File, $"warhead \"{s.Id}\": versus is missing armor \"{key}\"");
            }
            versus[(int)a] = Num(s, "warhead", $"versus.{key}", pct, 0, 1000);
        }
        foreach (var key in given.Keys.Order(StringComparer.Ordinal))
        {
            if (!TryParseArmor(key, out _))
            {
                throw Fail(s.File, $"warhead \"{s.Id}\": unknown armor \"{key}\" in versus");
            }
        }
        return new Warhead { Index = index, Id = s.Id, Versus = versus };
    }

    private static Component CompileComponent(Context c, Src<ComponentJson> s, int index, List<Warhead> warheads)
    {
        var d = s.Def;
        var where = $"component \"{s.Id}\"";
        if (!TryParseSlot(d.Slot, out var slot))
        {
            throw Fail(s.File, $"{where}: unknown slot \"{d.Slot}\" (want weapon, ability or perk)");
        }
        if (d.Effects is null || d.Effects.Count == 0)
        {
            throw Fail(s.File, $"{where}: no effects");
        }
        var effects = new List<Effect>();
        for (var i = 0; i < d.Effects.Count; i++)
        {
            effects.Add(CompileEffect(c, s, d.Effects[i], $"{where}: effect #{i + 1}", warheads));
        }
        return new Component
        {
            Index = index,
            Id = s.Id,
            Slot = slot,
            Cost = Num(s, "component", "cost", d.Cost, 0, Limit),
            Requires = Tags(c, s.File, where, "requires", d.Requires),
            Effects = effects,
        };
    }

    private static Effect CompileEffect(Context c, Src<ComponentJson> s, EffectJson? e, string where, List<Warhead> warheads)
    {
        if (e is null)
        {
            throw Fail(s.File, $"{where}: null");
        }
        object?[] kinds = [e.Weapon, e.Aura, e.Placer, e.Trigger, e.Builder, e.BuildAssist, e.Repair, e.Modifier];
        if (kinds.Count(k => k is not null) != 1)
        {
            throw Fail(s.File, $"{where}: want exactly one of weapon, aura, placer, trigger, builder, buildAssist, repair, modifier");
        }
        var f = s.File;
        if (e.Weapon is { } w)
        {
            return new WeaponEffect
            {
                Damage = Num(f, where, "weapon.damage", w.Damage, 1, Limit),
                Range = Tenths(Num(f, where, "weapon.range", w.Range, 1, Limit)),
                Reload = Num(f, where, "weapon.reload", w.Reload, 1, Limit),
                Warhead = WarheadRef(c, f, where, w.Warhead, warheads),
                Splash = Tenths(Num(f, where, "weapon.splash", w.Splash, 0, Limit)),
                Scatter = Tenths(Num(f, where, "weapon.scatter", w.Scatter, 0, Limit)),
                Targets = Targets(f, where, w.Targets),
            };
        }
        if (e.Aura is { } a)
        {
            var mods = (a.Modifiers ?? []).Select((m, i) => ModifierOf(f, $"{where}: aura.modifiers #{i + 1}", m?.Stat, m?.Percent ?? 0, m?.Flat ?? 0)).ToList();
            var heal = Num(f, where, "aura.heal", a.Heal, 0, Limit);
            if (heal == 0 && mods.Count == 0)
            {
                throw Fail(f, $"{where}: aura has neither heal nor modifiers");
            }
            var relation = a.Affects?.Relation switch
            {
                "ally" => Relation.Ally,
                "enemy" => Relation.Enemy,
                var other => throw Fail(f, $"{where}: aura.affects.relation \"{other}\" (want ally or enemy)"),
            };
            return new AuraEffect
            {
                Radius = Tenths(Num(f, where, "aura.radius", a.Radius, 1, Limit)),
                EveryTicks = Num(f, where, "aura.everyTicks", a.EveryTicks, 1, Limit),
                Heal = heal,
                Modifiers = mods,
                Affects = relation,
                AffectsTags = Tags(c, f, where, "aura.affects.tags", a.Affects?.Tags),
            };
        }
        if (e.Placer is { } p)
        {
            var target = ArchetypeRef(c, f, where, "placer.archetype", p.Archetype);
            var t = c.ArchetypeSources[target].Def;
            if (t.Locomotor != "none" || t.Footprint is not null)
            {
                throw Fail(f, $"{where}: placer.archetype \"{p.Archetype}\" must have locomotor none and no footprint");
            }
            return new PlacerEffect
            {
                Archetype = target,
                Charges = Num(f, where, "placer.charges", p.Charges, 1, Limit),
                Cooldown = Num(f, where, "placer.cooldown", p.Cooldown, 0, Limit),
                Range = Tenths(Num(f, where, "placer.range", p.Range, 1, Limit)),
            };
        }
        if (e.Trigger is { } tr)
        {
            return new TriggerEffect
            {
                Radius = Tenths(Num(f, where, "trigger.radius", tr.Radius, 1, Limit)),
                Damage = Num(f, where, "trigger.damage", tr.Damage, 1, Limit),
                Warhead = WarheadRef(c, f, where, tr.Warhead, warheads),
                Splash = Tenths(Num(f, where, "trigger.splash", tr.Splash, 0, Limit)),
                Affects = Tags(c, f, where, "trigger.affects", tr.Affects),
            };
        }
        if (e.Builder is { } b)
        {
            return new BuilderEffect
            {
                Rate = Num(f, where, "builder.rate", b.Rate, 1, Limit),
                Range = Tenths(Num(f, where, "builder.range", b.Range, 1, Limit)),
            };
        }
        if (e.BuildAssist is { } ba)
        {
            var unlocks = (ba.Unlocks ?? []).Select(u => ArchetypeRef(c, f, where, "buildAssist.unlocks", u)).Distinct().Order().ToList();
            return new BuildAssistEffect
            {
                Bonus = Num(f, where, "buildAssist.bonus", ba.Bonus, 0, Limit),
                Radius = Tenths(Num(f, where, "buildAssist.radius", ba.Radius, 1, Limit)),
                Unlocks = unlocks,
            };
        }
        if (e.Repair is { } r)
        {
            return new RepairEffect
            {
                Rate = Num(f, where, "repair.rate", r.Rate, 1, Limit),
                EveryTicks = Num(f, where, "repair.everyTicks", r.EveryTicks, 1, Limit),
                Radius = Tenths(Num(f, where, "repair.radius", r.Radius, 1, Limit)),
                Affects = Tags(c, f, where, "repair.affects", r.Affects),
            };
        }
        var m = e.Modifier!;
        return new ModifierEffect { Modifier = ModifierOf(f, $"{where}: modifier", m.Stat, m.Percent, m.Flat) };
    }

    private static Archetype CompileArchetype(Context c, Src<ArchetypeJson> s, int index, List<Component> components)
    {
        var d = s.Def;
        var f = s.File;
        var where = $"archetype \"{s.Id}\"";
        if (string.IsNullOrEmpty(d.Name))
        {
            throw Fail(f, $"{where}: missing name");
        }
        if (d.Glyph is not { Length: 1 } || d.Glyph[0] is < '!' or > '~' or '.' or '#' or '+')
        {
            throw Fail(f, $"{where}: glyph must be one printable ASCII character other than . # +");
        }
        var locomotor = d.Locomotor switch
        {
            "none" => Locomotor.None,
            "foot" => Locomotor.Foot,
            "wheeled" => Locomotor.Wheeled,
            "tracked" => Locomotor.Tracked,
            "flying" => Locomotor.Flying,
            _ => throw Fail(f, $"{where}: unknown locomotor \"{d.Locomotor}\" (want none, foot, wheeled, tracked or flying)"),
        };
        if (!TryParseArmor(d.Armor, out var armor))
        {
            throw Fail(f, $"{where}: unknown armor \"{d.Armor}\" (want none, light, heavy or building)");
        }
        var speed = Num(f, where, "speed", d.Speed, 0, Limit);
        var turnRate = Num(f, where, "turnRate", d.TurnRate, 0, Facing.Count);
        if ((locomotor == Locomotor.None) != (speed == 0))
        {
            throw Fail(f, $"{where}: speed must be 0 exactly when locomotor is none");
        }
        if (speed > 0 && turnRate == 0)
        {
            throw Fail(f, $"{where}: a moving archetype needs a turnRate");
        }

        var slots = new int[Enum.GetValues<SlotKind>().Length];
        foreach (var (key, n) in (d.Slots ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!TryParseSlot(key, out var slot))
            {
                throw Fail(f, $"{where}: unknown slot \"{key}\" (want weapon, ability or perk)");
            }
            slots[(int)slot] = Num(f, where, $"slots.{key}", n, 0, 16);
        }

        int fw = 0, fh = 0, buildTime = 0;
        if (d.Footprint is { } fp)
        {
            if (locomotor != Locomotor.None)
            {
                throw Fail(f, $"{where}: only archetypes with locomotor none can have a footprint");
            }
            fw = Num(f, where, "footprint.w", fp.W, 1, MaxFootprint);
            fh = Num(f, where, "footprint.h", fp.H, 1, MaxFootprint);
            buildTime = Num(f, where, "buildTime", d.BuildTime, 1, Limit);
        }
        else if (d.BuildTime != 0 || d.Locked)
        {
            throw Fail(f, $"{where}: buildTime and locked need a footprint");
        }

        var tags = Tags(c, f, where, "tags", d.Tags);
        var defaults = new List<int>();
        foreach (var id in d.DefaultComponents ?? [])
        {
            if (id is null || !c.Components.TryGetValue(id, out var ci))
            {
                throw Fail(f, $"{where}: unknown component \"{id}\" in defaultComponents");
            }
            if (defaults.Contains(ci))
            {
                throw Fail(f, $"{where}: component \"{id}\" listed twice in defaultComponents");
            }
            if (!tags.HasAll(components[ci].Requires))
            {
                throw Fail(f, $"{where}: default component \"{id}\" requires tags this archetype lacks");
            }
            defaults.Add(ci);
        }
        if (defaults.Sum(i => components[i].Effects.Count(e => e is WeaponEffect)) > 1)
        {
            throw Fail(f, $"{where}: default components carry more than one weapon");
        }

        return new Archetype
        {
            Index = index,
            Id = s.Id,
            Name = d.Name,
            Glyph = d.Glyph[0],
            Tags = tags,
            Locomotor = locomotor,
            Armor = armor,
            MaxHp = Num(f, where, "hp", d.Hp, 1, Limit),
            Speed = Fixed.Frac(speed, 10 * Sim.World.TicksPerSecond),
            TurnRate = turnRate,
            Turret = d.Turret,
            Hidden = d.Hidden,
            Locked = d.Locked,
            Cost = Num(f, where, "cost", d.Cost, 0, Limit),
            Budget = Num(f, where, "budget", d.Budget, 0, Limit),
            Slots = slots,
            DefaultComponents = defaults,
            FootprintW = fw,
            FootprintH = fh,
            BuildTime = buildTime,
        };
    }

    // CheckUnlocks makes sure buildAssist only unlocks locked structures.
    private static void CheckUnlocks(List<Src<ComponentJson>> sources, List<Component> components, List<Archetype> archetypes)
    {
        for (var i = 0; i < components.Count; i++)
        {
            foreach (var e in components[i].Effects.OfType<BuildAssistEffect>())
            {
                foreach (var u in e.Unlocks)
                {
                    if (!archetypes[u].Locked)
                    {
                        throw Fail(sources[i].File, $"component \"{components[i].Id}\": unlocks \"{archetypes[u].Id}\", which is not a locked structure");
                    }
                }
            }
        }
    }

    private static Faction CompileFaction(Context c, Src<FactionJson> s, int index)
    {
        var d = s.Def;
        var f = s.File;
        var where = $"faction \"{s.Id}\"";
        if (string.IsNullOrEmpty(d.Name))
        {
            throw Fail(f, $"{where}: missing name");
        }
        var archetypes = new bool[c.Archetypes.Count];
        foreach (var id in d.Archetypes ?? [])
        {
            archetypes[ArchetypeRef(c, f, where, "archetypes", id)] = true;
        }
        var components = new bool[c.Components.Count];
        foreach (var id in d.Components ?? [])
        {
            if (id is null || !c.Components.TryGetValue(id, out var ci))
            {
                throw Fail(f, $"{where}: unknown component \"{id}\" in components");
            }
            components[ci] = true;
        }
        var mods = new List<FactionModifier>();
        var list = d.Modifiers ?? [];
        for (var i = 0; i < list.Count; i++)
        {
            var m = list[i] ?? throw Fail(f, $"{where}: modifiers #{i + 1}: null");
            var at = $"{where}: modifiers #{i + 1}";
            mods.Add(new FactionModifier(Tags(c, f, at, "tags", m.Tags), ModifierOf(f, at, m.Stat, m.Percent, m.Flat)));
        }
        return new Faction { Index = index, Id = s.Id, Name = d.Name, Archetypes = archetypes, Components = components, Modifiers = mods };
    }

    private static int ArchetypeRef(Context c, string file, string where, string field, string? id)
    {
        if (id is null || !c.Archetypes.TryGetValue(id, out var i))
        {
            throw Fail(file, $"{where}: unknown archetype \"{id}\" in {field}");
        }
        return i;
    }

    private static Warhead WarheadRef(Context c, string file, string where, string? id, List<Warhead> warheads)
    {
        if (id is null || !c.Warheads.TryGetValue(id, out var i))
        {
            throw Fail(file, $"{where}: unknown warhead \"{id}\"");
        }
        return warheads[i];
    }

    private static TagSet Tags(Context c, string file, string where, string field, List<string>? names)
    {
        ulong bits = 0;
        foreach (var t in names ?? [])
        {
            if (t is null || !c.Tags.TryGetValue(t, out var i))
            {
                throw Fail(file, $"{where}: unknown tag \"{t}\" in {field} (no archetype has it)");
            }
            bits |= 1UL << i;
        }
        return new TagSet(bits);
    }

    private static Layers Targets(string file, string where, List<string>? names)
    {
        if (names is null)
        {
            return Layers.Ground;
        }
        var l = Layers.None;
        foreach (var n in names)
        {
            l |= n switch
            {
                "ground" => Layers.Ground,
                "air" => Layers.Air,
                _ => throw Fail(file, $"{where}: unknown layer \"{n}\" in weapon.targets (want ground or air)"),
            };
        }
        if (l == Layers.None)
        {
            throw Fail(file, $"{where}: weapon.targets is empty");
        }
        return l;
    }

    private static Modifier ModifierOf(string file, string where, string? stat, int percent, int flat)
    {
        var s = stat switch
        {
            "maxHp" => Stat.MaxHp,
            "speed" => Stat.Speed,
            "turnRate" => Stat.TurnRate,
            "cost" => Stat.Cost,
            "damage" => Stat.Damage,
            "range" => Stat.Range,
            "reload" => Stat.Reload,
            "buildRate" => Stat.BuildRate,
            _ => throw Fail(file, $"{where}: unknown stat \"{stat}\""),
        };
        Num(file, where, "percent", percent, -100, 1000);
        Num(file, where, "flat", flat, -Limit, Limit);
        if (percent == 0 && flat == 0)
        {
            throw Fail(file, $"{where}: modifier changes nothing");
        }
        // Flat values are given in content units; convert to the stat's internal units.
        long internalFlat = s switch
        {
            Stat.Speed => Fixed.Frac(flat, 10 * Sim.World.TicksPerSecond).Raw,
            Stat.Range => Tenths(flat).Raw,
            _ => flat,
        };
        return new Modifier(s, percent, internalFlat);
    }

    // Tenths converts tenths of a cell to Fixed.
    private static Fixed Tenths(int tenths) => Fixed.Frac(tenths, 10);

    private static int Num<T>(Src<T> s, string kind, string field, int v, int min, int max) =>
        Num(s.File, $"{kind} \"{s.Id}\"", field, v, min, max);

    private static int Num(string file, string where, string field, int v, int min, int max)
    {
        if (v < min || v > max)
        {
            throw Fail(file, $"{where}: {field} is {v}, want {min}..{max}");
        }
        return v;
    }

    private static bool TryParseSlot(string? s, out SlotKind slot)
    {
        (var ok, slot) = s switch
        {
            "weapon" => (true, SlotKind.Weapon),
            "ability" => (true, SlotKind.Ability),
            "perk" => (true, SlotKind.Perk),
            _ => (false, SlotKind.Weapon),
        };
        return ok;
    }

    private static bool TryParseArmor(string? s, out Armor armor)
    {
        foreach (var a in Enum.GetValues<Armor>())
        {
            if (ArmorName(a) == s)
            {
                armor = a;
                return true;
            }
        }
        armor = Armor.None;
        return false;
    }

    private static string ArmorName(Armor a) => a switch
    {
        Armor.None => "none",
        Armor.Light => "light",
        Armor.Heavy => "heavy",
        _ => "building",
    };

    private static ContentException Fail(string file, string message) => new($"content: {file}: {message}");
}
