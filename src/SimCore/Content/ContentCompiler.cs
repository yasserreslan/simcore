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

    private const int MaxFootprint = 200; // H1: a main air base's core is 180 x 86 cells

    // An aircraft's fly zone past the map's edge when content leaves flyZoneMargin out (E12, D-FZ1): 48 cells.
    private const int DefaultFlyZoneCells = 48;

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex IdPattern();

    private readonly record struct Src<T>(string File, string Id, T Def);

    public static ContentSet Compile(IReadOnlyList<(string File, ContentFile Data)> files)
    {
        var armorClasses = CollectArmorClasses(files);
        var warheads = Collect(files, f => f.Warheads, w => w.Id, "warhead");
        var archetypes = Collect(files, f => f.Archetypes, a => a.Id, "archetype");
        var components = Collect(files, f => f.Components, c => c.Id, "component");
        var factions = Collect(files, f => f.Factions, f => f.Id, "faction");
        var airfieldTypes = Collect(files, f => f.AirfieldTypes, t => t.Id, "airfield type");
        var exclusives = Collect(files, f => f.ExclusiveGroups, g => g.Id, "exclusive group");

        var airClasses = CollectAirClasses(files);
        var tags = CollectTags(archetypes);
        var archetypeIndex = Index(archetypes);
        var c = new Context(
            Index(warheads), archetypeIndex, Index(components), Index(tags.Select(t => (t, t)).ToList()),
            Index(armorClasses.Select(a => (a, a)).ToList()), armorClasses, archetypes,
            AirfieldPieces(airfieldTypes, archetypeIndex), airClasses, Index(exclusives));

        var compiledWarheads = warheads.Select((w, i) => CompileWarhead(c, w, i)).ToList();
        var compiledComponents = components.Select((s, i) => CompileComponent(c, s, i, compiledWarheads)).ToList();
        var compiledArchetypes = archetypes.Select((s, i) => CompileArchetype(c, s, i, compiledComponents)).ToList();
        CheckUnlocks(components, compiledComponents, compiledArchetypes);
        CheckLaunches(components, compiledComponents, compiledArchetypes);
        var compiledFactions = factions.Select((s, i) => CompileFaction(c, s, i)).ToList();
        var compiledAirfields = airfieldTypes.Select((s, i) => CompileAirfieldType(c, s, i, compiledArchetypes)).ToList();
        var compiledExclusives = exclusives.Select((g, i) => new ExclusiveGroup(g.Id, i,
            Num(g.File, $"exclusive group \"{g.Id}\"", "max", g.Def.Max, 1, 100), g.Def.LockType)).ToList();
        return new ContentSet(tags, armorClasses, compiledWarheads, compiledArchetypes, compiledComponents, compiledFactions, CompileUnitAi(files),
            compiledAirfields, airClasses, compiledExclusives);
    }

    // CompileUnitAi reads the one optional unitAi block (two are an error); fields left at 0 keep
    // their defaults.
    private static UnitAiRules CompileUnitAi(IReadOnlyList<(string File, ContentFile Data)> files)
    {
        string? at = null;
        UnitAiJson? j = null;
        foreach (var (file, data) in files)
        {
            if (data.UnitAi is null)
            {
                continue;
            }
            if (at is not null)
            {
                throw Fail(file, $"unitAi: already defined in {at}");
            }
            (at, j) = (file, data.UnitAi);
        }
        var d = UnitAiRules.Default;
        if (j is null)
        {
            return d;
        }
        int Or(string field, int v, int def, int min, int max) => v == 0 ? def : Num(at!, "unitAi", field, v, min, max);
        return new UnitAiRules
        {
            GuardInnerPercent = Or("guardInnerPercent", j.GuardInnerPercent, d.GuardInnerPercent, 10, 1000),
            GuardOuterPercent = Or("guardOuterPercent", j.GuardOuterPercent, d.GuardOuterPercent, 10, 1000),
            AggressivePercent = Or("aggressivePercent", j.AggressivePercent, d.AggressivePercent, 10, 1000),
            ChaseTicks = Or("chaseTicks", j.ChaseTicks, d.ChaseTicks, 1, Limit),
            ScanTicks = Or("scanTicks", j.ScanTicks, d.ScanTicks, 1, 200),
            ReturnScanTicks = Or("returnScanTicks", j.ReturnScanTicks, d.ReturnScanTicks, 1, 200),
            RetaliateMax = j.RetaliateMax == 0 ? d.RetaliateMax : Tenths(Num(at!, "unitAi", "retaliateMax", j.RetaliateMax, 1, Limit)),
            RetaliateFriends = j.RetaliateFriends == 0 ? d.RetaliateFriends : Tenths(Num(at!, "unitAi", "retaliateFriends", j.RetaliateFriends, 1, 1000)),
            RetaliateCooldownTicks = Or("retaliateCooldownTicks", j.RetaliateCooldownTicks, d.RetaliateCooldownTicks, 1, Limit),
        };
    }

    // Context holds the ID → index maps every pass needs.
    private sealed record Context(
        Dictionary<string, int> Warheads,
        Dictionary<string, int> Archetypes,
        Dictionary<string, int> Components,
        Dictionary<string, int> Tags,
        Dictionary<string, int> ArmorIndex,
        List<string> ArmorClasses,
        List<Src<ArchetypeJson>> ArchetypeSources,
        Dictionary<string, (int Type, AirfieldRole Role, StationKind Kind)> AirfieldPieces,
        List<AirClass> AirClasses,
        Dictionary<string, int> Exclusives)
    {
        // AirClassOf is the air class named id, or null.
        public AirClass? AirClassOf(string? id) => AirClasses.Find(a => a.Id == id);
    }

    // The most air classes: an interceptor's classes are bits 1..30 of an int.
    private const int MaxAirClasses = 30;

    // CollectAirClasses gathers the air classes in order (files in file-name order, entries in file
    // order): a class's value is its 1-based position, so appending a class keeps every other's value.
    private static List<AirClass> CollectAirClasses(IReadOnlyList<(string File, ContentFile Data)> files)
    {
        var list = new List<AirClass>();
        foreach (var (file, data) in files)
        {
            foreach (var a in data.AirClasses ?? [])
            {
                if (a?.Id is not { } id || !IdPattern().IsMatch(id))
                {
                    throw Fail(file, $"air class \"{a?.Id}\": bad id (want lowercase letters, digits and _)");
                }
                if (list.Exists(x => x.Id == id))
                {
                    throw Fail(file, $"air class \"{id}\": already defined");
                }
                var kind = a.Kind switch
                {
                    "projectile" => AirClassKind.Projectile,
                    "aircraft" => AirClassKind.Aircraft,
                    _ => throw Fail(file, $"air class \"{id}\": kind must be \"projectile\" or \"aircraft\""),
                };
                if (list.Count == MaxAirClasses)
                {
                    throw Fail(file, $"air class \"{id}\": at most {MaxAirClasses} air classes");
                }
                list.Add(new AirClass(id, (InterceptClass)(list.Count + 1), kind));
            }
        }
        return list;
    }

    // CollectArmorClasses gathers the armor class names from every file.
    private static List<string> CollectArmorClasses(IReadOnlyList<(string File, ContentFile Data)> files)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, data) in files)
        {
            foreach (var a in data.ArmorClasses ?? [])
            {
                if (a is null || !IdPattern().IsMatch(a))
                {
                    throw Fail(file, $"armor class \"{a}\": bad id (want lowercase letters, digits and _)");
                }
                if (seen.TryGetValue(a, out var other))
                {
                    throw Fail(file, $"armor class \"{a}\": already defined in {other}");
                }
                seen[a] = file;
            }
        }
        if (seen.Count == 0)
        {
            throw new ContentException("content: no armorClasses defined");
        }
        return [.. seen.Keys.Order(StringComparer.Ordinal)];
    }

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

    private static Warhead CompileWarhead(Context c, Src<WarheadJson> s, int index)
    {
        var versus = new int[c.ArmorClasses.Count];
        var given = s.Def.Versus ?? [];
        for (var i = 0; i < c.ArmorClasses.Count; i++)
        {
            var key = c.ArmorClasses[i];
            if (!given.TryGetValue(key, out var pct))
            {
                throw Fail(s.File, $"warhead \"{s.Id}\": versus is missing armor class \"{key}\" (use null for cannot target)");
            }
            versus[i] = pct is { } p ? Num(s, "warhead", $"versus.{key}", p, 0, 1000) : Warhead.CannotTarget;
        }
        foreach (var key in given.Keys.Order(StringComparer.Ordinal))
        {
            if (!c.ArmorIndex.ContainsKey(key))
            {
                throw Fail(s.File, $"warhead \"{s.Id}\": unknown armor class \"{key}\" in versus");
            }
        }
        if (versus.All(v => v == Warhead.CannotTarget))
        {
            throw Fail(s.File, $"warhead \"{s.Id}\": cannot target any armor class");
        }
        var maxHp = new int[c.ArmorClasses.Count];
        foreach (var (key, pct) in (s.Def.MaxHpPercent ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!c.ArmorIndex.TryGetValue(key, out var ai))
            {
                throw Fail(s.File, $"warhead \"{s.Id}\": unknown armor class \"{key}\" in maxHpPercent");
            }
            maxHp[ai] = Num(s, "warhead", $"maxHpPercent.{key}", pct, 1, 100);
        }
        return new Warhead { Index = index, Id = s.Id, Versus = versus, MaxHpPercent = maxHp };
    }

    private static Component CompileComponent(Context c, Src<ComponentJson> s, int index, List<Warhead> warheads)
    {
        var d = s.Def;
        var where = $"component \"{s.Id}\"";
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
            Cost = Num(s, "component", "cost", d.Cost, 0, Limit),
            Requires = Tags(c, s.File, where, "requires", d.Requires),
            Placeholder = d.Placeholder,
            Effects = effects,
        };
    }

    private static List<Fixed> AutoRanges(Context c, string f, string where, Dictionary<string, int>? map)
    {
        var list = Enumerable.Repeat(Fixed.Zero, c.ArmorIndex.Count).ToList();
        foreach (var (armor, tenths) in (map ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!c.ArmorIndex.TryGetValue(armor, out var i))
            {
                throw Fail(f, $"{where}: weapon.autoRange: unknown armor class \"{armor}\"");
            }
            list[i] = Tenths(Num(f, where, $"weapon.autoRange.{armor}", tenths, 1, Limit));
        }
        return list;
    }

    // Priorities: a rank per armor class index from a list of class names (first is 0); unlisted
    // classes rank after every listed one; an absent list ranks everything 0.
    private static List<int> Priorities(Context c, string f, string where, List<string>? order)
    {
        if (order is null || order.Count == 0)
        {
            return [];
        }
        var list = Enumerable.Repeat(order.Count, c.ArmorIndex.Count).ToList();
        for (var k = 0; k < order.Count; k++)
        {
            if (!c.ArmorIndex.TryGetValue(order[k], out var i))
            {
                throw Fail(f, $"{where}: weapon.priority: unknown armor class \"{order[k]}\"");
            }
            list[i] = Math.Min(list[i], k);
        }
        return list;
    }

    // SideFireOf compiles a gunship's side cannon (H3): a ground weapon with a round's speed and no other aircraft
    // behaviour (no delivery, airGun, intercept, airStrike or guidance); its priority names armour classes (at least one).
    private static SideFire SideFireOf(Context c, string f, string where, WeaponJson w, SideFireJson j)
    {
        if (w.Targets?.Contains("ground") != true || w.ProjectileSpeed <= 0 || w.Delivery is not null || w.AirGun is not null ||
            w.Intercept is not null || w.AirStrike is not null || w.Guided is not null)
        {
            throw Fail(f, $"{where}: weapon.sideFire needs a ground weapon with a projectileSpeed (no delivery, airGun, intercept, airStrike or guided)");
        }
        if (j.Priority is not { Count: > 0 } order)
        {
            throw Fail(f, $"{where}: weapon.sideFire.priority is empty");
        }
        var ranks = Enumerable.Repeat(-1, c.ArmorIndex.Count).ToList();
        for (var k = 0; k < order.Count; k++)
        {
            if (!c.ArmorIndex.TryGetValue(order[k], out var i))
            {
                throw Fail(f, $"{where}: weapon.sideFire.priority: unknown armor class \"{order[k]}\"");
            }
            ranks[i] = ranks[i] < 0 ? k : ranks[i];
        }
        var biggest = j.Pick switch
        {
            null or "nearest" => false,
            "biggest" => true,
            _ => throw Fail(f, $"{where}: weapon.sideFire.pick must be \"nearest\" or \"biggest\""),
        };
        return new SideFire(Tenths(Num(f, where, "weapon.sideFire.scatter", j.Scatter, 0, Limit)).Raw,
            Num(f, where, "weapon.sideFire.tier", j.Tier, 0, 7), ranks, j.AreaFire, biggest);
    }

    // GunshipOf compiles a gunship's orbit attack (H3): a fixed wing's; the area lies inside the circle.
    private static GunshipSpec GunshipOf(string f, string where, GunshipJson j, FlightJson? flight)
    {
        if (flight?.Kind != "fixedWing")
        {
            throw Fail(f, $"{where}: gunship needs a fixedWing flight");
        }
        var radius = Num(f, where, "gunship.radius", j.Radius, 10, Limit);
        return new GunshipSpec(Tenths(radius).Raw,
            Tenths(Num(f, where, "gunship.area", j.Area, 1, radius)).Raw,
            Tenths(Num(f, where, "gunship.onCircle", j.OnCircle, 1, radius)).Raw,
            Num(f, where, "gunship.bearing", j.Bearing, 1, 90));
    }

    // InterceptorOf compiles an air-defence missile's intercept block: it must be a guided missile
    // (its turn rate and max flight), fired by the operator and forgotten, at the air. Its classes are
    // content air classes; the per-class terms (killBest, speedRefs, hitDamage, hits) name some of them.
    private static Interceptor InterceptorOf(Context c, string f, string where, WeaponJson w, InterceptJson ij)
    {
        if (w.Guided is null || w.Guided.RequiresOperatorStill || w.ProjectileSpeed <= 0 || w.Targets?.Contains("air") != true)
        {
            throw Fail(f, $"{where}: weapon.intercept needs a guided missile (projectileSpeed, guided without requiresOperatorStill) that targets the air");
        }
        if (ij.Classes is null || ij.Classes.Count == 0)
        {
            throw Fail(f, $"{where}: weapon.intercept.classes is empty");
        }
        var classes = 0;
        var units = false;
        foreach (var name in ij.Classes)
        {
            var ac = c.AirClassOf(name) ?? throw Fail(f, $"{where}: weapon.intercept.classes: unknown air class \"{name}\"");
            classes |= 1 << (int)ac.Class;
            units |= ac.Kind == AirClassKind.Aircraft;
        }
        InterceptClass Own(string field, string name) =>
            c.AirClassOf(name) is { } ac && (classes & (1 << (int)ac.Class)) != 0 ? ac.Class
                : throw Fail(f, $"{where}: weapon.intercept.{field}: \"{name}\" must be one of its classes");
        var head = Num(f, where, "weapon.intercept.killHead", ij.KillHead, 0, 100);
        var min = Num(f, where, "weapon.intercept.killMin", ij.KillMin, 0, 100);
        var rules = new List<InterceptClassRule>();
        void Set(InterceptClass cls, Func<InterceptClassRule, InterceptClassRule> edit)
        {
            var i = rules.FindIndex(r => r.Class == cls);
            if (i < 0)
            {
                rules.Add(edit(new InterceptClassRule(cls, 0, Fixed.Zero, 0)));
            }
            else
            {
                rules[i] = edit(rules[i]);
            }
        }
        foreach (var (name, best) in (ij.KillBest ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var v = Num(f, where, $"weapon.intercept.killBest.{name}", best, 1, 100);
            Set(Own("killBest", name), r => r with { Best = v });
        }
        foreach (var (name, sr) in (ij.SpeedRefs ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var v = PerSecond(Num(f, where, $"weapon.intercept.speedRefs.{name}", sr, 1, Limit));
            Set(Own("speedRefs", name), r => r with { SpeedRef = v });
        }
        foreach (var (name, hd) in (ij.HitDamage ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var cls = Own("hitDamage", name);
            if (c.AirClasses[(int)cls - 1].Kind != AirClassKind.Aircraft)
            {
                throw Fail(f, $"{where}: weapon.intercept.hitDamage: \"{name}\" is not an aircraft class (a projectile has no HP)");
            }
            var v = Num(f, where, $"weapon.intercept.hitDamage.{name}", hd, 1, 1000);
            Set(cls, r => r with { HitDamage = v });
        }
        rules.Sort((a, b) => a.Class.CompareTo(b.Class));
        var hits = new List<InterceptHit>();
        foreach (var (name, hj) in (ij.Hits ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var cls = Own("hits", name);
            if (hj is null)
            {
                throw Fail(f, $"{where}: weapon.intercept.hits.{name} is empty");
            }
            hits.Add(new InterceptHit(cls,
                Num(f, where, $"weapon.intercept.hits.{name}.chance", hj.Chance, 0, 100),
                Num(f, where, $"weapon.intercept.hits.{name}.damage", hj.Damage, 1, 1000)));
        }
        hits.Sort((a, b) => a.Class.CompareTo(b.Class));
        var salvo = new List<(InterceptClass Class, int Count)>();
        foreach (var (name, n) in (ij.Salvo ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            salvo.Add((Own("salvo", name), Num(f, where, $"weapon.intercept.salvo.{name}", n, 1, 8)));
        }
        salvo.Sort((a, b) => a.Class.CompareTo(b.Class));
        return new Interceptor(classes,
            Num(f, where, "weapon.intercept.lockTicks", ij.LockTicks, 0, Limit),
            Tenths(Num(f, where, "weapon.intercept.fuse", ij.Fuse, 1, Limit)),
            Num(f, where, "weapon.intercept.killTail", ij.KillTail, head, 100),
            head,
            Num(f, where, "weapon.intercept.crossHalf", ij.CrossHalf, 1, Limit),
            PerSecond(Num(f, where, "weapon.intercept.speedRef", ij.SpeedRef, 1, Limit)),
            Num(f, where, "weapon.intercept.rangeFar", ij.RangeFar, 0, 100),
            Num(f, where, "weapon.intercept.trackBonus", ij.TrackBonus, 0, 100),
            Num(f, where, "weapon.intercept.trackFull", ij.TrackFull, 1, Limit),
            min,
            Num(f, where, "weapon.intercept.killMax", ij.KillMax, min, 100),
            Num(f, where, "weapon.intercept.leadTicks", ij.LeadTicks, 0, Limit),
            Num(f, where, "weapon.intercept.launchHeight", ij.LaunchHeight, 0, 500) * 512L / 10,
            hits.Count > 0 ? hits : null)
        {
            EngagesUnits = units,
            Rules = rules,
            Seeker = SeekerOf(f, where, "weapon.intercept.seeker", ij.Seeker, true),
            Boresight = Num(f, where, "weapon.intercept.boresight", ij.Boresight, 0, 180),
            CeilingRaw = Num(f, where, "weapon.intercept.ceiling", ij.Ceiling, 0, 50000) * 512L / 10,
            RadarLinked = ij.RadarLinked,
            Battery = ij.Battery is { } bj ? BatteryOf(c, f, where, ij, bj) : null,
            Outright = ij.Outright,
            Salvo = salvo,
            DefendRadiusRaw = Tenths(Num(f, where, "weapon.intercept.defendRadius", ij.DefendRadius, 0, Limit)).Raw,
            MinAltitudeRaw = Num(f, where, "weapon.intercept.minAltitude", ij.MinAltitude, 0, 50000) * 512L / 10,
            AltitudeFullRaw = ij.AltitudeFull == 0 ? 0
                : Num(f, where, "weapon.intercept.altitudeFull", ij.AltitudeFull, ij.MinAltitude + 1, 50000) * 512L / 10,
            AltitudeLow = Num(f, where, "weapon.intercept.altitudeLow", ij.AltitudeLow, 0, 100),
            MaxPerTarget = Num(f, where, "weapon.intercept.maxPerTarget", ij.MaxPerTarget, 0, 64),
        };
    }

    // BatteryOf compiles an intercept block's battery link (the SA-6 type launcher): a link radius and the tags of the radars it
    // links to; never with radarLinked (the Patriot type's other way of firing on a radar's tracks).
    private static Battery BatteryOf(Context c, string f, string where, InterceptJson ij, BatteryJson bj)
    {
        if (ij.RadarLinked)
        {
            throw Fail(f, $"{where}: weapon.intercept.battery and radarLinked are exclusive");
        }
        if (bj.RadarTags is not { Count: > 0 })
        {
            throw Fail(f, $"{where}: weapon.intercept.battery.radarTags is empty");
        }
        return new Battery(Tenths(Num(f, where, "weapon.intercept.battery.linkRadius", bj.LinkRadius, 1, Limit)).Raw,
            Tags(c, f, where, "weapon.intercept.battery.radarTags", bj.RadarTags), bj.SemiActive);
    }

    // AirGunOf compiles an aircraft gun's airGun block: a weapon that targets the air, its hit chances
    // naming air classes (E8 data; E6 fires it).
    private static AirGun AirGunOf(Context c, string f, string where, WeaponJson w, AirGunJson j)
    {
        if (w.Targets?.Contains("air") != true || w.Intercept is not null || w.AirStrike is not null || w.Guided is not null)
        {
            throw Fail(f, $"{where}: weapon.airGun needs a gun that targets the air (no intercept, airStrike or guided)");
        }
        var hits = new List<(InterceptClass, int)>();
        foreach (var (name, v) in (j.HitBase ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var ac = c.AirClassOf(name) ?? throw Fail(f, $"{where}: weapon.airGun.hitBase: unknown air class \"{name}\"");
            hits.Add((ac.Class, Num(f, where, $"weapon.airGun.hitBase.{name}", v, 0, 100)));
        }
        if (hits.Count == 0)
        {
            throw Fail(f, $"{where}: weapon.airGun.hitBase is empty");
        }
        hits.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new AirGun(Num(f, where, "weapon.airGun.cone", j.Cone, 1, 180), hits,
            Num(f, where, "weapon.airGun.crossHalf", j.CrossHalf, 1, Limit),
            Num(f, where, "weapon.airGun.rangeFar", j.RangeFar, 0, 100),
            Num(f, where, "weapon.airGun.ceiling", j.Ceiling, 0, 50000) * 512L / 10,
            Tenths(Num(f, where, "weapon.airGun.strafeRange", j.StrafeRange, 0, Limit)),
            Num(f, where, "weapon.airGun.strafeEvery", j.StrafeEvery, 0, 10000));
    }

    // DeliveryOf compiles a bomb's delivery block: a ground weapon (E8 data; E7 drops it); a free-fall bomb needs its gravity.
    private static Delivery DeliveryOf(string f, string where, WeaponJson w, DeliveryJson j)
    {
        if (w.Targets?.Contains("ground") != true || w.Intercept is not null || w.AirStrike is not null || w.Guided is not null)
        {
            throw Fail(f, $"{where}: weapon.delivery needs a bomb that targets the ground (no intercept, airStrike or guided)");
        }
        var profile = j.Profile switch
        {
            "level" => DeliveryProfile.Level,
            "dive" => DeliveryProfile.Dive,
            "toss" => DeliveryProfile.Toss,
            _ => throw Fail(f, $"{where}: weapon.delivery.profile must be \"level\", \"dive\" or \"toss\""),
        };
        GuidedBomb? guided = null;
        if (j.Guided is { } g)
        {
            var near = Num(f, where, "weapon.delivery.guided.missNear", g.MissNear, 0, Limit);
            guided = new GuidedBomb(Num(f, where, "weapon.delivery.guided.hitIdle", g.HitIdle, 0, 100),
                Num(f, where, "weapon.delivery.guided.hitMoving", g.HitMoving, 0, 100),
                Tenths(near), Tenths(Num(f, where, "weapon.delivery.guided.missFar", g.MissFar, near, Limit)));
        }
        return new Delivery(profile,
            Num(f, where, "weapon.delivery.releaseHeight", j.ReleaseHeight, 0, 50000) * 512L / 10,
            Num(f, where, "weapon.delivery.diveAngle", j.DiveAngle, 0, 90),
            Num(f, where, "weapon.delivery.tossAngle", j.TossAngle, 0, 90),
            Tenths(Num(f, where, "weapon.delivery.ipDistance", j.IpDistance, 0, Limit)),
            Tenths(Num(f, where, "weapon.delivery.scatterLevel", j.ScatterLevel, 0, Limit)),
            Tenths(Num(f, where, "weapon.delivery.scatterDive", j.ScatterDive, 0, Limit)),
            Tenths(Num(f, where, "weapon.delivery.scatterToss", j.ScatterToss, 0, Limit)),
            guided,
            j.Stick == 0 ? 1 : Num(f, where, "weapon.delivery.stick", j.Stick, 1, 20),
            Tenths(Num(f, where, "weapon.delivery.stickSpacing", j.StickSpacing, 0, Limit)),
            guided is null || j.Gravity != 0 ? Num(f, where, "weapon.delivery.gravity", j.Gravity, 1, 100000) * 512L / 10 : 0,
            ModesOf(f, where, j, guided is not null),
            Tenths(Num(f, where, "weapon.delivery.group", j.Group, 0, Limit)).Raw,
            Tenths(Num(f, where, "weapon.delivery.groupAside", j.GroupAside, 0, Limit)).Raw,
            Num(f, where, "weapon.delivery.retard", j.Retard, 0, 90) * 10 / Sim.World.TicksPerSecond,
            j.Overfly && guided is null,
            j.Precision && guided is null ? true
                : j.Precision ? throw Fail(f, $"{where}: weapon.delivery.precision is for a free-fall bomb (no guided block)") : false,
            j.Aims == 0 ? 0 : Num(f, where, "weapon.delivery.aims", j.Aims, 1, 32),
            j.Tier == 0 ? 0 : Num(f, where, "weapon.delivery.tier", j.Tier, 1, 7));
    }

    // ModesOf compiles a bomb's fire modes (H2): unique IDs, 1 to 40 bombs each; a guided bomb has none.
    private static IReadOnlyList<FireModeDef>? ModesOf(string f, string where, DeliveryJson j, bool guided)
    {
        if (j.Modes is not { } ms)
        {
            return null;
        }
        if (guided || ms.Count == 0 || ms.Count > 8)
        {
            throw Fail(f, $"{where}: weapon.delivery.modes needs 1 to 8 modes on a free-fall bomb");
        }
        var list = new List<FireModeDef>();
        foreach (var m in ms)
        {
            if (string.IsNullOrEmpty(m.Id) || list.Exists(x => x.Id == m.Id))
            {
                throw Fail(f, $"{where}: weapon.delivery.modes: every mode needs a unique id");
            }
            var salvo = Num(f, where, "weapon.delivery.modes.salvo", m.Salvo, 1, 40);
            var across = m.Across == 0 ? 1 : Num(f, where, "weapon.delivery.modes.across", m.Across, 1, 8);
            if (salvo % across != 0)
            {
                throw Fail(f, $"{where}: weapon.delivery.modes: mode {m.Id}'s salvo must be whole rows of across");
            }
            list.Add(new FireModeDef(m.Id, salvo, m.Spread, across,
                Tenths(Num(f, where, "weapon.delivery.modes.acrossSpacing", m.AcrossSpacing, 0, Limit)).Raw));
        }
        return list;
    }

    // SeekerOf reads a seeker name ("ir", "radar"; null is None when allowed).
    private static Seeker SeekerOf(string f, string where, string field, string? name, bool optional) => name switch
    {
        null when optional => Seeker.None,
        "ir" => Seeker.Ir,
        "radar" => Seeker.Radar,
        _ => throw Fail(f, $"{where}: {field} must be \"ir\" or \"radar\""),
    };

    // SignatureOf compiles an aircraft's signature block (each field 0 for 100).
    private static Signature SignatureOf(string f, string where, SignatureJson? j, bool flight)
    {
        if (j is null)
        {
            return Signature.Normal;
        }
        if (!flight)
        {
            throw Fail(f, $"{where}: signature needs a flight block (an air target)");
        }
        var sig = new Signature(
            j.Detect == 0 ? 100 : Num(f, where, "signature.detect", j.Detect, 10, 200),
            j.Lock == 0 ? 100 : Num(f, where, "signature.lock", j.Lock, 25, 1000),
            j.Kill == 0 ? 100 : Num(f, where, "signature.kill", j.Kill, 1, 100));
        return sig == Signature.Normal ? Signature.Normal : sig;
    }

    // The most loadout modes an aircraft may have.
    private const int MaxLoadouts = 4;

    // LoadoutsOf compiles an aircraft's loadout modes (the stealth multirole's stealth and heavy): 2 to MaxLoadouts of them, ids
    // unique; each mode's ammo names its fixed components that carry a weapon with ammunition, at most that ammunition (listed in
    // component order: content order does not matter). A fixed-wing flight with a loadoutSwitch only.
    private static List<Loadout> LoadoutsOf(Context c, string f, string where, ArchetypeJson d, List<int> fixedComponents, List<Component> components)
    {
        if (d.Loadouts is not { } list)
        {
            return [];
        }
        if (d.Flight?.Kind != "fixedWing" || d.Aircraft is null)
        {
            throw Fail(f, $"{where}: loadouts need a fixedWing flight and an aircraft block");
        }
        if (list.Count is < 2 or > MaxLoadouts)
        {
            throw Fail(f, $"{where}: loadouts: want 2 to {MaxLoadouts} modes");
        }
        if (d.LoadoutSwitch == 0)
        {
            throw Fail(f, $"{where}: loadouts need a loadoutSwitch");
        }
        var output = new List<Loadout>();
        foreach (var j in list)
        {
            if (j?.Id is not { } id || !IdPattern().IsMatch(id) || output.Exists(x => x.Id == id))
            {
                throw Fail(f, $"{where}: loadout \"{j?.Id}\": bad or repeated id");
            }
            var at = $"loadouts.{id}";
            var ammo = new List<(int, int)>();
            foreach (var (name, n) in j.Ammo ?? [])
            {
                if (!c.Components.TryGetValue(name, out var ci) || !fixedComponents.Contains(ci))
                {
                    throw Fail(f, $"{where}: {at}.ammo: \"{name}\" is not one of its components");
                }
                var max = components[ci].Effects.OfType<WeaponEffect>().FirstOrDefault(w => w.Ammo > 0)?.Ammo
                    ?? throw Fail(f, $"{where}: {at}.ammo: \"{name}\" carries no weapon with ammo");
                ammo.Add((ci, Num(f, where, $"{at}.ammo.{name}", n, 0, max)));
            }
            ammo.Sort((a, b) => fixedComponents.IndexOf(a.Item1).CompareTo(fixedComponents.IndexOf(b.Item1)));
            output.Add(new Loadout(id, ammo, j.Speed == 0 ? 100 : Num(f, where, $"{at}.speed", j.Speed, 50, 200),
                j.Signature is null ? SignatureOf(f, where, d.Signature, true) : SignatureOf(f, $"{where} {at}", j.Signature, true)));
        }
        return output;
    }

    // CountermeasuresOf compiles an aircraft's dispensers: one per kind at most.
    private static List<Countermeasure> CountermeasuresOf(string f, string where, List<CountermeasureJson>? list, bool flight)
    {
        var output = new List<Countermeasure>();
        if (list is null || list.Count == 0)
        {
            return output;
        }
        if (!flight)
        {
            throw Fail(f, $"{where}: countermeasures need a flight block (an aircraft)");
        }
        foreach (var j in list)
        {
            var kind = j?.Kind switch
            {
                "flare" => CountermeasureKind.Flare,
                "chaff" => CountermeasureKind.Chaff,
                _ => throw Fail(f, $"{where}: countermeasures: kind must be \"flare\" or \"chaff\""),
            };
            if (output.Exists(x => x.Kind == kind))
            {
                throw Fail(f, $"{where}: countermeasures: two \"{j!.Kind}\" dispensers");
            }
            var k = $"countermeasures.{j!.Kind}";
            output.Add(new Countermeasure(kind,
                Num(f, where, $"{k}.salvos", j.Salvos, 1, 100),
                SeekerOf(f, where, $"{k}.vs", j.Vs, false),
                Num(f, where, $"{k}.pk", j.Pk, 0, 100),
                Num(f, where, $"{k}.window", j.Window, 1, Limit),
                j.PerMissile == 0 ? 1 : Num(f, where, $"{k}.perMissile", j.PerMissile, 1, 10),
                Num(f, where, $"{k}.rearmTicks", j.RearmTicks, 1, Limit),
                j.BreakPk == 0 ? -1 : Num(f, where, $"{k}.breakPk", j.BreakPk, 1, 100)));
        }
        return output;
    }

    // EvadeOf compiles a fixed wing's evade block (E6).
    private static Evade EvadeOf(string f, string where, EvadeJson j, FlightJson? flight)
    {
        if (flight?.Kind != "fixedWing")
        {
            throw Fail(f, $"{where}: evade needs a fixedWing flight");
        }
        return new Evade(Num(f, where, "evade.breakG", j.BreakG, 1, 100), Num(f, where, "evade.warn", j.Warn, 1, Limit),
            Num(f, where, "evade.maxTicks", j.MaxTicks, 1, Limit));
    }

    // AttackRunOf compiles a fixed wing's attack-run geometry (E13).
    private static AttackRunSpec AttackRunOf(string f, string where, AttackRunJson j, FlightJson? flight)
    {
        if (flight?.Kind != "fixedWing")
        {
            throw Fail(f, $"{where}: attackRun needs a fixedWing flight");
        }
        var d = AttackRunSpec.Default;
        return new AttackRunSpec(
            j.Align == 0 ? 0 : Tenths(Num(f, where, "attackRun.align", j.Align, 1, Limit)).Raw,
            j.BreakTurn == 0 ? d.BreakTurn : Num(f, where, "attackRun.breakTurn", j.BreakTurn, 10, 150),
            j.Break == 0 ? 0 : Tenths(Num(f, where, "attackRun.break", j.Break, 1, Limit)).Raw,
            j.Extend == 0 ? 0 : Tenths(Num(f, where, "attackRun.extend", j.Extend, 1, Limit)).Raw,
            j.ReleaseWindow == 0 ? d.WindowRaw : Tenths(Num(f, where, "attackRun.releaseWindow", j.ReleaseWindow, 1, 200)).Raw,
            Num(f, where, "attackRun.offset", j.Offset, 0, 30));
    }

    // ElementOf compiles a fixed wing's element block (E13): 1 to 3 slots, each back along the lead's path.
    private static ElementSpec ElementOf(string f, string where, ElementJson j, FlightJson? flight)
    {
        if (flight?.Kind != "fixedWing")
        {
            throw Fail(f, $"{where}: element needs a fixedWing flight");
        }
        if (j.Slots is not { Count: >= 1 and <= 3 } list)
        {
            throw Fail(f, $"{where}: element.slots wants 1 to 3 slots");
        }
        var slots = new List<FormationSlot>();
        foreach (var sj in list)
        {
            if (sj is null)
            {
                throw Fail(f, $"{where}: element.slots: a slot is null");
            }
            slots.Add(new FormationSlot(Tenths(Num(f, where, "element.slots.back", sj.Back, 1, 2000)).Raw,
                Tenths(Num(f, where, "element.slots.aside", sj.Aside, -1000, 1000)).Raw));
        }
        return new ElementSpec(slots, Num(f, where, "element.stagger", j.Stagger, 1, 400));
    }

    // PursuitOf compiles an aircraft's air-to-air pursuit separation (E13).
    private static PursuitSpec PursuitOf(string f, string where, PursuitJson j, bool flight)
    {
        if (!flight)
        {
            throw Fail(f, $"{where}: pursuit needs a flight block");
        }
        return new PursuitSpec(Tenths(Num(f, where, "pursuit.separation", j.Separation, 1, 500)).Raw,
            Num(f, where, "pursuit.breakOff", j.BreakOff, 1, 200), Tenths(Num(f, where, "pursuit.reattack", j.Reattack, 1, Limit)).Raw,
            Num(f, where, "pursuit.climb", j.Climb, 0, 1000) * 512L / 10, Num(f, where, "pursuit.maxTicks", j.MaxTicks, 1, Limit));
    }

    // LossOf compiles an aircraft's loss block (E10): the wreck's crash path and impact, the pilot's ejection.
    private static LossSpec LossOf(Context c, string f, string where, LossJson j, bool hasFlight)
    {
        if (!hasFlight)
        {
            throw Fail(f, $"{where}: loss needs a flight block");
        }
        if (j.Crash is null && j.Eject is null)
        {
            throw Fail(f, $"{where}: loss is empty (give crash, eject or both)");
        }
        CrashSpec? crash = null;
        if (j.Crash is { } cj)
        {
            if (cj.Warhead is null || !c.Warheads.TryGetValue(cj.Warhead, out var wh))
            {
                throw Fail(f, $"{where}: loss.crash: unknown warhead \"{cj.Warhead}\"");
            }
            crash = new CrashSpec(wh, Num(f, where, "loss.crash.damage", cj.Damage, 0, Limit),
                Tenths(Num(f, where, "loss.crash.splash", cj.Splash, 0, 1000)),
                Num(f, where, "loss.crash.gravity", cj.Gravity, 1, 100000) * 512L / 10,
                Num(f, where, "loss.crash.drag", cj.Drag, 0, 100),
                Num(f, where, "loss.crash.breakUp", cj.BreakUp, 0, 100),
                Num(f, where, "loss.crash.breakUpDrag", cj.BreakUpDrag, 0, 100),
                RunwayOf(f, $"{where}: loss.crash", cj.Runway));
        }
        EjectSpec? eject = null;
        if (j.Eject is { } ej)
        {
            var unit = ArchetypeRef(c, f, where, "loss.eject.unit", ej.Unit);
            if (c.ArchetypeSources[unit].Def.Locomotor != "foot")
            {
                throw Fail(f, $"{where}: loss.eject.unit \"{ej.Unit}\" must be a soldier on foot");
            }
            var rise = Num(f, where, "loss.eject.riseTicks", ej.RiseTicks, 1, Limit);
            eject = new EjectSpec(unit, Num(f, where, "loss.eject.seatRise", ej.SeatRise, 0, 50000) * 512L / 10, rise,
                Num(f, where, "loss.eject.chuteTicks", ej.ChuteTicks, rise, Limit),
                Num(f, where, "loss.eject.chuteRate", ej.ChuteRate, 1, 10000) * 512L / 10,
                PerSecond(Num(f, where, "loss.eject.drift", ej.Drift, 0, 1000)),
                Num(f, where, "loss.eject.driftTicks", ej.DriftTicks, 0, Limit));
        }
        return new LossSpec(crash, eject);
    }

    // KamanOf compiles a Kaman drone's kaman block: a drone flight (not a fixed wing or a rotor), its blast and its dive.
    private static KamanSpec KamanOf(Context c, string f, string where, KamanJson j, FlightJson? flight)
    {
        if (flight is null || flight.Kind is not (null or "drone"))
        {
            throw Fail(f, $"{where}: kaman needs a drone flight block");
        }
        if (j.Warhead is null || !c.Warheads.TryGetValue(j.Warhead, out var wh))
        {
            throw Fail(f, $"{where}: kaman: unknown warhead \"{j.Warhead}\"");
        }
        return new KamanSpec(Num(f, where, "kaman.damage", j.Damage, 1, Limit), wh,
            Tenths(Num(f, where, "kaman.splash", j.Splash, 0, 1000)),
            Num(f, where, "kaman.splashCenter", j.SplashCenter, 1, 100),
            Num(f, where, "kaman.splashEdge", j.SplashEdge, 0, 100),
            Num(f, where, "kaman.dive", j.Dive, 100, 10000),
            Num(f, where, "kaman.pitchOver", j.PitchOver, 1, 400),
            RunwayOf(f, $"{where}: kaman", j.Runway));
    }

    // AirBodyOf compiles an airBody block (E6): a flying archetype without a flight, engaged as an aircraft class.
    private static AirBodySpec AirBodyOf(Context c, string f, string where, AirBodyJson j, bool flight, Locomotor locomotor)
    {
        if (flight || locomotor != Locomotor.Flying)
        {
            throw Fail(f, $"{where}: airBody is for a flying archetype without a flight block");
        }
        var cls = c.AirClassOf(j.Class) is { Kind: AirClassKind.Aircraft } ac ? ac.Class
            : throw Fail(f, $"{where}: airBody.class \"{j.Class}\": want an aircraft air class");
        return new AirBodySpec(cls, Num(f, where, "airBody.height", j.Height, 1, 10000) * 512L / 10);
    }

    // LaunchesOf resolves a weapon's launches (the archetype its rounds are): a unit with a kaman block, fired by an
    // instant, single-round weapon at the ground (no projectile, salvo, guidance, drone, interception or air role).
    private static int LaunchesOf(Context c, string f, string where, WeaponJson w)
    {
        var a = ArchetypeRef(c, f, where, "weapon.launches", w.Launches);
        if (c.ArchetypeSources[a].Def.Kaman is null)
        {
            throw Fail(f, $"{where}: weapon.launches \"{w.Launches}\" must have a kaman block");
        }
        if (w.ProjectileSpeed != 0 || w.SalvoSize > 1 || w.Guided is not null || w.Drone is not null || w.Intercept is not null ||
            w.AirStrike is not null || w.AirGun is not null || w.Delivery is not null || w.SideFire is not null ||
            w.Targets is not null && (w.Targets.Count != 1 || w.Targets[0] != "ground"))
        {
            throw Fail(f, $"{where}: weapon.launches needs ground targets only and no projectileSpeed, salvo, guided, drone, intercept, airStrike, airGun, delivery or sideFire");
        }
        return a;
    }

    // AirStrikeOf compiles a strike missile's airStrike block: a travelling shot at the ground with
    // ammo nothing but its launcher's turnaround refills, never guided, salvoed or intercepting.
    private static AirStrike AirStrikeOf(Context c, string f, string where, WeaponJson w, AirStrikeJson sj)
    {
        if (w.ProjectileSpeed <= 0 || w.Ammo <= 0 || !w.NoResupply || w.Guided is not null || w.Intercept is not null ||
            w.Drone is not null || w.SalvoSize > 1 || w.Targets is not null && (w.Targets.Count != 1 || w.Targets[0] != "ground"))
        {
            throw Fail(f, $"{where}: weapon.airStrike needs a projectile (projectileSpeed), ammo with noResupply, ground targets only, and no guided, intercept, drone or salvo");
        }
        var near = Num(f, where, "weapon.airStrike.missNear", sj.MissNear, 0, Limit);
        return new AirStrike(
            Num(f, where, "weapon.airStrike.hitIdle", sj.HitIdle, 0, 100),
            Num(f, where, "weapon.airStrike.hitMoving", sj.HitMoving, 0, 100),
            Tags(c, f, where, "weapon.airStrike.kills", sj.Kills),
            Tags(c, f, where, "weapon.airStrike.heavyTags", sj.HeavyTags),
            Num(f, where, "weapon.airStrike.heavyKill", sj.HeavyKill, 0, 100),
            Num(f, where, "weapon.airStrike.heavyHpLeft", sj.HeavyHpLeft, 0, 100),
            Tenths(near),
            Tenths(Num(f, where, "weapon.airStrike.missFar", sj.MissFar, near, Limit)));
    }

    private static Effect CompileEffect(Context c, Src<ComponentJson> s, EffectJson? e, string where, List<Warhead> warheads)
    {
        if (e is null)
        {
            throw Fail(s.File, $"{where}: null");
        }
        object?[] kinds = [e.Weapon, e.Aura, e.Placer, e.Launch, e.Strike, e.Supply, e.Cargo, e.Trigger, e.Builder, e.BuildAssist, e.Repair, e.Modifier, e.Smoke, e.Aps];
        if (kinds.Count(k => k is not null) != 1)
        {
            throw Fail(s.File, $"{where}: want exactly one of weapon, aura, placer, launch, strike, supply, cargo, trigger, builder, buildAssist, repair, modifier, smoke, aps");
        }
        var f = s.File;
        if (e.Aps is { } ap)
        {
            var chance = Num(f, where, "aps.chance", ap.Chance, 1, 100);
            return new ApsEffect
            {
                Charges = Num(f, where, "aps.charges", ap.Charges, 1, 100),
                Range = Tenths(Num(f, where, "aps.range", ap.Range, 1, 500)),
                Cooldown = Num(f, where, "aps.cooldown", ap.Cooldown, 1, Limit),
                Chance = chance,
                FastFrom = PerSecond(Num(f, where, "aps.fastFrom", ap.FastFrom, 0, Limit)),
                FastStep = PerSecond(Num(f, where, "aps.fastStep", ap.FastStep, 1, Limit)),
                FastPenalty = Num(f, where, "aps.fastPenalty", ap.FastPenalty, 0, 100),
                CloseRange = Tenths(Num(f, where, "aps.closeRange", ap.CloseRange, 0, Limit)),
                ClosePenalty = Num(f, where, "aps.closePenalty", ap.ClosePenalty, 0, 100),
                MinChance = Num(f, where, "aps.minChance", ap.MinChance, 0, chance),
            };
        }
        if (e.Smoke is { } sm)
        {
            return new SmokeEffect
            {
                Charges = Num(f, where, "smoke.charges", sm.Charges, 1, 100),
                Radius = Tenths(Num(f, where, "smoke.radius", sm.Radius, 1, 500)),
                DurationTicks = Num(f, where, "smoke.duration", sm.Duration, 1, Limit),
                Cooldown = Num(f, where, "smoke.cooldown", sm.Cooldown, 0, Limit),
            };
        }
        if (e.Weapon is { } w)
        {
            var salvo = Num(f, where, "weapon.salvoSize", w.SalvoSize, 1, 100);
            var interval = Num(f, where, "weapon.salvoInterval", w.SalvoInterval, 0, Limit);
            if (salvo > 1 && interval == 0)
            {
                throw Fail(f, $"{where}: weapon.salvoInterval must be at least 1 when salvoSize is above 1");
            }
            var projectile = Num(f, where, "weapon.projectileSpeed", w.ProjectileSpeed, 0, Limit);
            var splash = Num(f, where, "weapon.splash", w.Splash, 0, Limit);
            if ((projectile > 0 || salvo > 1) && splash == 0 && w.Guided is null && w.Airdrop is null) // a guided missile hits its target unit; a pallet lands
            {
                throw Fail(f, $"{where}: a weapon with projectileSpeed or a salvo needs a splash radius (its shots land on a point, not a unit)");
            }
            if (w.SharedAmmo && (w.Ammo != 0 || w.DepotReload is not null || w.NoResupply))
            {
                throw Fail(f, $"{where}: weapon.sharedAmmo draws on the weapon before it: no ammo, depotReload or noResupply of its own");
            }
            return new WeaponEffect
            {
                SharedAmmo = w.SharedAmmo,
                Coaxial = w.Coaxial,
                ApsThreat = w.ApsThreat,
                TruckReload = w.TruckReload is not { } trj ? null
                    : w.Ammo == 0 || w.DepotReload is not null || w.NoResupply || w.SharedAmmo
                        ? throw Fail(f, $"{where}: weapon.truckReload needs ammo of its own and no depotReload or noResupply")
                    : new TruckReload(Num(f, where, "weapon.truckReload.cost", trj.Cost, 1, Limit),
                        Num(f, where, "weapon.truckReload.everyTicks", trj.EveryTicks, 1, Limit),
                        Tenths(Num(f, where, "weapon.truckReload.range", trj.Range, 1, Limit))),
                RevealTicks = Num(f, where, "weapon.reveal", w.Reveal, 0, Limit),
                GroupRadius = Tenths(Num(f, where, "weapon.groupRadius", w.GroupRadius, 0, Limit)),
                StructureWeight = w.GroupStructure == 0 ? 1
                    : w.GroupRadius == 0 ? throw Fail(f, $"{where}: weapon.groupStructure needs a groupRadius")
                    : Num(f, where, "weapon.groupStructure", w.GroupStructure, 1, 100),
                Damage = Num(f, where, "weapon.damage", w.Damage, 1, Limit),
                Range = Tenths(Num(f, where, "weapon.range", w.Range, 1, Limit)),
                Reload = Num(f, where, "weapon.reload", w.Reload, 1, Limit),
                Warhead = WarheadRef(c, f, where, w.Warhead, warheads),
                Splash = Tenths(splash),
                Scatter = Tenths(Num(f, where, "weapon.scatter", w.Scatter, 0, Limit)),
                ScatterNear = Tenths(Num(f, where, "weapon.scatterNear", w.ScatterNear, 0, w.Scatter)),
                Targets = Targets(f, where, w.Targets),
                ProjectileSpeed = PerSecond(projectile),
                SalvoSize = salvo,
                SalvoInterval = interval,
                Ammo = Num(f, where, "weapon.ammo", w.Ammo, 0, Limit),
                MinRange = Tenths(Num(f, where, "weapon.minRange", w.MinRange, 0, Limit)),
                AmmoPerRocket = w.AmmoPerRocket,
                SplashCenter = Num(f, where, "weapon.splashCenter", w.SplashCenter ?? 100, 1, 100),
                SplashEdge = Num(f, where, "weapon.splashEdge", w.SplashEdge ?? 50, 0, 100),
                SplashInner = Tenths(Num(f, where, "weapon.splashInner", w.SplashInner, 0, w.Splash > 0 ? w.Splash : 0)),
                SplashOuterPercent = Num(f, where, "weapon.splashOuterPercent", w.SplashOuterPercent, 0, 100),
                AutoRange = AutoRanges(c, f, where, w.AutoRange),
                AutoSkipRetreating = w.AutoSkipRetreating,
                MaxMisses = Num(f, where, "weapon.maxMisses", w.MaxMisses, 0, 100),
                DepotReload = w.DepotReload is { } dr
                    ? new DepotReload(Num(f, where, "weapon.depotReload.everyTicks", dr.EveryTicks, 1, Limit),
                        Num(f, where, "weapon.depotReload.afterFireTicks", dr.AfterFireTicks, 0, Limit),
                        Num(f, where, "weapon.depotReload.cost", dr.Cost, 0, Limit), dr.Stowed)
                    : null,
                AimTicks = Num(f, where, "weapon.aimTicks", w.AimTicks, 0, Limit),
                ArcPercent = Num(f, where, "weapon.arc", w.Arc, 0, 100),
                Drone = w.Drone is { } dj
                    ? new DroneFlight(Num(f, where, "weapon.drone.cruiseAboveMax", dj.CruiseAboveMax, 0, 5000) * 512L / 10,
                        Num(f, where, "weapon.drone.climbRate", dj.ClimbRate, 1, 2000) * 512L / 10 / 20,
                        Num(f, where, "weapon.drone.boosterTicks", dj.BoosterTicks, 0, Limit),
                        Num(f, where, "weapon.drone.dive", dj.Dive, 100, 10000),
                        Num(f, where, "weapon.drone.launchHeight", dj.LaunchHeight, 0, 500) * 512L / 10,
                        Num(f, where, "weapon.drone.roundOff", dj.RoundOff, 0, 400),
                        Num(f, where, "weapon.drone.pitchOver", dj.PitchOver, 0, 400))
                    : null,
                NoResupply = w.NoResupply,
                Launches = w.Launches is null ? -1 : LaunchesOf(c, f, where, w),
                Intercept = w.Interceptible is null ? InterceptClass.None
                    : c.AirClassOf(w.Interceptible) is { Kind: AirClassKind.Projectile } pc ? pc.Class
                    : throw Fail(f, $"{where}: weapon.interceptible \"{w.Interceptible}\": want a projectile air class"),
                Guided = w.Guided is { } gd
                    ? new Guidance(Tenths(Num(f, where, "weapon.guided.maxFlight", gd.MaxFlight, 1, Limit)),
                        // Degrees per second to the chord of one tick's turn, in thousandths of the speed:
                        // about the angle in radians for small turns (pi / 180 / 20 per degree per second).
                        Num(f, where, "weapon.guided.turnRate", gd.TurnRate, 1, 720) * 8727 / 10000,
                        gd.RequiresOperatorStill,
                        Tenths(Num(f, where, "weapon.guided.dropDistance", gd.DropDistance, 0, Limit)),
                        gd.FireAndForget && !gd.RequiresOperatorStill ? true
                            : gd.FireAndForget ? throw Fail(f, $"{where}: weapon.guided.fireAndForget needs no operator (requiresOperatorStill false)") : false)
                    : null,
                Priority = Priorities(c, f, where, w.Priority),
                Cone = Num(f, where, "weapon.cone", w.Cone, 0, 180),
                Interceptor = w.Intercept is { } ij ? InterceptorOf(c, f, where, w, ij) : null,
                AirStrike = w.AirStrike is { } sj ? AirStrikeOf(c, f, where, w, sj) : null,
                AirGun = w.AirGun is { } gj ? AirGunOf(c, f, where, w, gj) : null,
                Delivery = w.Delivery is { } dlj ? DeliveryOf(f, where, w, dlj) : null,
                SideFire = w.SideFire is { } sfj ? SideFireOf(c, f, where, w, sfj) : null,
                Penetrator = w.Penetrator is not { } pj ? null
                    : w.Delivery is null ? throw Fail(f, $"{where}: weapon.penetrator needs a bomb (a delivery block)")
                    : new Penetrator(Tenths(Num(f, where, "weapon.penetrator.radius", pj.Radius, 1, 200)).Raw,
                        Num(f, where, "weapon.penetrator.craterTicks", pj.CraterTicks, 0, Limit),
                        Tenths(Num(f, where, "weapon.penetrator.craterRadius", pj.CraterRadius, 0, 200)).Raw),
                Runway = RunwayOf(f, $"{where}: weapon", w.Runway),
                Airdrop = w.Airdrop is not { } aj ? null
                    : w.Delivery is null || w.Splash != 0 || w.Penetrator is not null || w.Runway is not null
                        ? throw Fail(f, $"{where}: weapon.airdrop needs a bomb (a delivery block) without splash, penetrator or runway effect")
                    : new Airdrop(Num(f, where, "weapon.airdrop.amount", aj.Amount, 1, Limit), Num(f, where, "weapon.airdrop.unloadTicks", aj.UnloadTicks, 1, Limit)),
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
                MaxActive = Num(f, where, "placer.maxActive", p.MaxActive, 1, Limit),
                PlaceTicks = Num(f, where, "placer.placeTicks", p.PlaceTicks, 0, Limit),
                DepotReload = p.DepotReload is { } pdr
                    ? new DepotReload(Num(f, where, "placer.depotReload.everyTicks", pdr.EveryTicks, 1, Limit),
                        Num(f, where, "placer.depotReload.afterFireTicks", pdr.AfterFireTicks, 0, Limit),
                        Num(f, where, "placer.depotReload.cost", pdr.Cost, 0, Limit), pdr.Stowed)
                    : null,
            };
        }
        if (e.Launch is { } l)
        {
            if (l.NoResupply && l.DepotReload is not null)
            {
                throw Fail(f, $"{where}: launch: noResupply and depotReload exclude each other");
            }
            return new LaunchEffect
            {
                Archetype = ArchetypeRef(c, f, where, "launch.archetype", l.Archetype),
                Charges = Num(f, where, "launch.charges", l.Charges, 1, Limit),
                Cooldown = Num(f, where, "launch.cooldown", l.Cooldown, 0, Limit),
                Range = Tenths(Num(f, where, "launch.range", l.Range, 1, Limit)),
                MaxActive = Num(f, where, "launch.maxActive", l.MaxActive, 0, Limit),
                DepotReload = l.DepotReload is { } ldr
                    ? new DepotReload(Num(f, where, "launch.depotReload.everyTicks", ldr.EveryTicks, 1, Limit),
                        Num(f, where, "launch.depotReload.afterFireTicks", ldr.AfterFireTicks, 0, Limit),
                        Num(f, where, "launch.depotReload.cost", ldr.Cost, 0, Limit), ldr.Stowed)
                    : null,
                NoResupply = l.NoResupply,
            };
        }
        if (e.Strike is { } st)
        {
            return new StrikeEffect
            {
                Contact = Tenths(Num(f, where, "strike.contact", st.Contact, 1, Limit)),
                Damage = Num(f, where, "strike.damage", st.Damage, 1, Limit),
                Warhead = WarheadRef(c, f, where, st.Warhead, warheads),
                Splash = Tenths(Num(f, where, "strike.splash", st.Splash, 0, Limit)),
                Runway = RunwayOf(f, $"{where}: strike", st.Runway),
            };
        }
        if (e.Supply is { } su)
        {
            return new SupplyEffect
            {
                Radius = Tenths(Num(f, where, "supply.radius", su.Radius, 1, Limit)),
                EveryTicks = Num(f, where, "supply.everyTicks", su.EveryTicks, 1, Limit),
                Amount = Num(f, where, "supply.amount", su.Amount, su.Storage ? 0 : 1, Limit),
                Stock = Num(f, where, "supply.stock", su.Stock, 0, Limit),
                Restock = Num(f, where, "supply.restock", su.Restock, 0, Limit),
                ReloadBonus = Num(f, where, "supply.reloadBonus", su.ReloadBonus, 0, 1000),
                ConstructedStock = Num(f, where, "supply.constructedStock", su.ConstructedStock ?? su.Stock, 0, su.Stock == 0 ? Limit : su.Stock),
                StartStock = Num(f, where, "supply.startStock", su.StartStock ?? su.Stock, 0, su.Stock == 0 ? Limit : su.Stock),
                Storage = su.Storage,
                CargoSource = su.CargoSource,
                GroundOnly = su.GroundOnly,
                RoundCost = Num(f, where, "supply.roundCost", su.RoundCost ?? 1, 1, Limit),
                RepairCost = Num(f, where, "supply.repairCost", su.RepairCost, 0, Limit),
                FuelCost = Num(f, where, "supply.fuelCost", su.FuelCost, 0, Limit),
            };
        }
        if (e.Cargo is { } cg)
        {
            return new CargoEffect
            {
                Amount = Num(f, where, "cargo.amount", cg.Amount, 1, Limit),
                UnloadTicks = Num(f, where, "cargo.unloadTicks", cg.UnloadTicks, 1, Limit),
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
                Edge = Num(f, where, "trigger.edge", tr.Edge ?? TriggerEffect.DefaultEdge, 0, 100),
                Kills = Tags(c, f, where, "trigger.kills", tr.Kills),
                ChainTicks = Num(f, where, "trigger.chainTicks", tr.ChainTicks ?? TriggerEffect.DefaultChainTicks, 1, Limit),
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
        if (d.Glyph is not { Length: 1 } || d.Glyph[0] is < '!' or > '~' or '.' or '#' or '+' or '*')
        {
            throw Fail(f, $"{where}: glyph must be one printable ASCII character other than . # + *");
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
        var weaponMode = d.WeaponMode switch
        {
            null or "auto" => WeaponMode.Auto,
            "primaryOnly" => WeaponMode.PrimaryOnly,
            _ => throw Fail(f, $"{where}: unknown weaponMode \"{d.WeaponMode}\" (want auto or primaryOnly)"),
        };
        if (d.Armor is null || !c.ArmorIndex.TryGetValue(d.Armor, out var armor))
        {
            throw Fail(f, $"{where}: unknown armor class \"{d.Armor}\" (want one of {string.Join(", ", c.ArmorClasses)})");
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

        var tags = Tags(c, f, where, "tags", d.Tags);
        var slots = new IReadOnlyList<int>[Enum.GetValues<SlotKind>().Length];
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = [];
        }
        foreach (var (key, ids) in (d.Slots ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!TryParseSlot(key, out var slot))
            {
                throw Fail(f, $"{where}: unknown slot \"{key}\" (want primary or secondary)");
            }
            if (ids is null || ids.Count == 0)
            {
                throw Fail(f, $"{where}: slot \"{key}\" lists no components");
            }
            slots[(int)slot] = ComponentRefs(c, f, where, $"slots.{key}", ids, tags, components);
        }
        if (slots[(int)SlotKind.Secondary].Count > 0 && slots[(int)SlotKind.Primary].Count == 0)
        {
            throw Fail(f, $"{where}: a secondary slot needs a primary slot");
        }
        var fixedComponents = ComponentRefs(c, f, where, "components", d.Components ?? [], tags, components);

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
        else if (d.Locked || d.Construction is not null || d.Production is not null)
        {
            throw Fail(f, $"{where}: locked, construction and production need a footprint");
        }
        else
        {
            buildTime = Num(f, where, "buildTime", d.BuildTime, 0, Limit); // a unit's: the ticks a factory takes to produce it
        }
        ProductionSpec? production = null;
        if (d.Production is { IntoStation: true } sj)
        {
            if ((d.Airfield is not null && d.Airfield.Role is not ("facility" or "hangar")) || sj.Exits is not null || sj.BayPoints is not null || sj.Crew is not null || sj.Rally is not null)
            {
                throw Fail(f, $"{where}: production.intoStation is an airfield facility's or hangar's (no exits, bayPoints, crew or rally)");
            }
            production = new ProductionSpec
            {
                Tags = Tags(c, f, where, "production.tags", sj.Tags),
                ExcludeTags = Tags(c, f, where, "production.excludeTags", sj.ExcludeTags), // (the forward airfield: not the stealth multirole)
                Bays = Num(f, where, "production.bays", sj.Bays, 1, 8),
                QueueMax = Num(f, where, "production.queueMax", sj.QueueMax, 1, 50),
                DoorTicks = 0,
                PushAfter = 0,
                Crew = "",
                MinHpPercent = Num(f, where, "production.minHpPercent", sj.MinHpPercent, 0, 100),
                IntoStation = true,
            };
        }
        else if (d.Production is { } pj)
        {
            production = new ProductionSpec
            {
                Tags = Tags(c, f, where, "production.tags", pj.Tags),
                ExcludeTags = Tags(c, f, where, "production.excludeTags", pj.ExcludeTags),
                // Bays 0 (logistics: the forward airbase's field hangar): doors only, for a service block; it produces nothing.
                Bays = Num(f, where, "production.bays", pj.Bays, d.Service is not null && pj.Exits is { Count: > 0 } ? 0 : 1, 8),
                QueueMax = Num(f, where, "production.queueMax", pj.QueueMax, pj.Bays == 0 ? 0 : 1, 50),
                DoorTicks = Num(f, where, "production.doorTicks", pj.DoorTicks, 1, Limit),
                PushAfter = Num(f, where, "production.pushAfter", pj.PushAfter, 1, Limit),
                Crew = pj.Crew is { Length: > 0 } driver ? driver : throw Fail(f, $"{where}: production.crew names who drives what it produces"),
                Exits = [.. (pj.Exits ?? []).Select(e => e.Count == 2 ? (e[0], e[1]) : throw Fail(f, $"{where}: production.exits entries are [x, y]"))],
                BayPoints = [.. (pj.BayPoints ?? []).Select(e => e.Count == 2 ? (e[0], e[1]) : throw Fail(f, $"{where}: production.bayPoints entries are [x, y]"))],
                Rally = pj.Rally is { Count: 2 } r ? (pj.Rally[0], pj.Rally[1]) : null,
                MinHpPercent = Num(f, where, "production.minHpPercent", pj.MinHpPercent, 0, 100),
            };
            if (production.BayPoints.Count != 0 && production.BayPoints.Count != production.Doors)
            {
                throw Fail(f, $"{where}: production.bayPoints needs one entry per door (per exit)");
            }
            if (production.Doors > 8)
            {
                throw Fail(f, $"{where}: production.exits lists at most 8 doors");
            }
        }
        ConstructionSpec? construction = null;
        if (d.Construction is { } cj)
        {
            var stages = cj.Stages ?? [];
            if (stages.Count != 4)
            {
                throw Fail(f, $"{where}: construction.stages needs 4 percents (where stages 1 to 4 begin)");
            }
            for (var i = 0; i < stages.Count; i++)
            {
                Num(f, where, "construction.stages", stages[i], i == 0 ? 1 : stages[i - 1] + 1, 99);
            }
            var helpers = cj.Helpers ?? [];
            if (helpers.Count == 0)
            {
                throw Fail(f, $"{where}: construction.helpers needs at least one percent");
            }
            construction = new ConstructionSpec
            {
                StartHp = Num(f, where, "construction.startHp", cj.StartHp, 1, 100),
                Stages = [.. stages],
                Refund = Num(f, where, "construction.refund", cj.Refund, 0, 100),
                Helpers = [.. helpers.Select(h => Num(f, where, "construction.helpers", h, 0, 1000))],
                PadBlend = Num(f, where, "construction.padBlend", cj.PadBlend, 0, 8),
            };
        }

        TunnelSpec? tunnel = null;
        if (d.Tunnel is { } tj)
        {
            if (fw == 0 || fw != fh || construction is null)
            {
                throw Fail(f, $"{where}: a tunnel entrance is a buildable structure with a square footprint");
            }
            tunnel = new TunnelSpec
            {
                Size = Num(f, where, "tunnel.size", tj.Size, 1, 3),
                Capacity = Num(f, where, "tunnel.capacity", tj.Capacity, 1, 1000),
                Throughput = Num(f, where, "tunnel.throughput", tj.Throughput, 1, Limit),
                RubbleHp = Num(f, where, "tunnel.rubbleHp", tj.RubbleHp, 1, Limit),
                LinkRange = Tenths(Num(f, where, "tunnel.link", tj.Link, 10, Limit)),
                Spacing = Tenths(Num(f, where, "tunnel.spacing", tj.Spacing, 0, Limit)),
                Speed = PerSecond(Num(f, where, "tunnel.speed", tj.Speed, 1, Limit)),
                ExposeTicks = Num(f, where, "tunnel.exposeTicks", tj.ExposeTicks, 0, Limit),
                FlatGrade = Num(f, where, "tunnel.flatGrade", tj.FlatGrade, 0, 100),
                HillGrade = Num(f, where, "tunnel.hillGrade", tj.HillGrade, 1, 255),
                Depth = Num(f, where, "tunnel.depth", tj.Depth, 0, fw),
                Walls = tj.Walls,
                Mound = Num(f, where, "tunnel.mound", tj.Mound, 0, 300) * 512 / 10,
                HillMound = Num(f, where, "tunnel.hillMound", tj.HillMound, 0, 300) * 512 / 10,
                Apron = Num(f, where, "tunnel.apron", tj.Apron, 0, 16),
                Blend = Num(f, where, "tunnel.blend", tj.Blend, 0, 16),
                EmergeDamage = Num(f, where, "tunnel.emergeDamage", tj.EmergeDamage, 0, 100),
                RestoreTicks = Num(f, where, "tunnel.restoreTicks", tj.RestoreTicks, 1, Limit),
            };
            if (tunnel.Walls && fw < 3)
            {
                throw Fail(f, $"{where}: tunnel.walls need a footprint at least 3 cells wide");
            }
        }
        var tunnelSize = Num(f, where, "tunnelSize", d.TunnelSize, 0, 3);
        var tunnelSlots = Num(f, where, "tunnelSlots", d.TunnelSlots, 0, 1000);
        if (locomotor == Locomotor.Foot)
        {
            tunnelSize = tunnelSize == 0 ? 1 : tunnelSize;
            tunnelSlots = tunnelSlots == 0 ? 1 : tunnelSlots;
        }
        if ((tunnelSize == 0) != (tunnelSlots == 0) || (tunnelSize > 0 && (locomotor is Locomotor.None or Locomotor.Flying)))
        {
            throw Fail(f, $"{where}: tunnelSize and tunnelSlots go together, on ground units only");
        }

        CrewSpec? crew = null;
        if (d.Crew is { } cr)
        {
            if (locomotor == Locomotor.None && (fw == 0 || cr.Passengers > 0))
            {
                throw Fail(f, $"{where}: only moving archetypes and structures (a driver seat, no passengers: a crewed emplacement) can have a crew");
            }
            crew = new CrewSpec
            {
                Tags = Tags(c, f, where, "crew.tags", cr.Tags),
                EnterRange = Tenths(Num(f, where, "crew.enterRange", cr.EnterRange, 1, Limit)),
                Survival = Num(f, where, "crew.survival", cr.Survival, 0, 100),
                ExitDamage = Num(f, where, "crew.exitDamage", cr.ExitDamage, 0, 99),
                Passengers = Num(f, where, "crew.passengers", cr.Passengers, 0, 64),
                PassengerTags = Tags(c, f, where, "crew.passengerTags", cr.PassengerTags),
                UnloadInterval = Num(f, where, "crew.unloadInterval", cr.UnloadInterval, 0, Limit),
                OccupantDamage = Num(f, where, "crew.occupantDamage", cr.OccupantDamage, 0, 100),
                OccupantWarheads = [.. (cr.OccupantWarheads ?? []).Select(w => c.Warheads.TryGetValue(w, out var wi) ? wi : throw Fail(f, $"{where}: crew.occupantWarheads: unknown warhead \"{w}\""))],
            };
            if (crew.OccupantDamage > 0 != crew.OccupantWarheads.Count > 0)
            {
                throw Fail(f, $"{where}: crew.occupantDamage and crew.occupantWarheads go together");
            }
            if (crew.Passengers == 0 && crew.UnloadInterval != 0)
            {
                throw Fail(f, $"{where}: crew.unloadInterval without crew.passengers");
            }
            if (crew.Passengers == 0 && crew.PassengerTags.Bits != 0)
            {
                throw Fail(f, $"{where}: crew.passengerTags without crew.passengers");
            }
        }

        GarrisonSpec? garrison = null;
        if (d.Garrison is { } g)
        {
            if (fw == 0)
            {
                throw Fail(f, $"{where}: garrison needs a footprint (a structure)");
            }
            var stages = g.Stages ?? [80, 60, 35];
            var stageCap = g.StageCapacity ?? [100, 100, 75, 50];
            if (stageCap.Count != stages.Count + 1)
            {
                throw Fail(f, $"{where}: garrison.stageCapacity wants one entry per stage before rubble ({stages.Count + 1})");
            }
            var doors = (g.Doors ?? []).Select(p => p is { Count: 2 } ? (p[0], p[1]) : throw Fail(f, $"{where}: garrison.doors entries are [x, y]")).ToList();
            if (doors.Count == 0)
            {
                throw Fail(f, $"{where}: garrison needs at least one door");
            }
            foreach (var (dx, dy) in doors)
            {
                var inside = dx >= 0 && dx < fw && dy >= 0 && dy < fh;
                if (inside || dx < -1 || dx > fw || dy < -1 || dy > fh)
                {
                    throw Fail(f, $"{where}: garrison door [{dx}, {dy}] must be a cell just outside the footprint");
                }
            }
            garrison = new GarrisonSpec
            {
                Capacity = Num(f, where, "garrison.capacity", g.Capacity, 1, 100),
                Tags = Tags(c, f, where, "garrison.tags", g.Tags),
                EnterRange = Tenths(Num(f, where, "garrison.enterRange", g.EnterRange, 1, Limit)),
                RangeBonus = Tenths(Num(f, where, "garrison.rangeBonus", g.RangeBonus, 0, Limit)),
                VisionBonus = Tenths(Num(f, where, "garrison.visionBonus", g.VisionBonus, 0, Limit)),
                OccupantDamage = Num(f, where, "garrison.occupantDamage", g.OccupantDamage, 0, 100),
                OccupantWarheads = [.. (g.OccupantWarheads ?? []).Select(w => c.Warheads.TryGetValue(w, out var wi) ? wi : throw Fail(f, $"{where}: garrison.occupantWarheads: unknown warhead \"{w}\""))],
                Stages = [.. stages.Select(v => Num(f, where, "garrison.stages", v, 0, 100))],
                StageCapacity = [.. stageCap.Select(v => Num(f, where, "garrison.stageCapacity", v, 0, 100))],
                EjectDamage = Num(f, where, "garrison.ejectDamage", g.EjectDamage, 0, 99),
                CollapseSurvival = Num(f, where, "garrison.collapseSurvival", g.CollapseSurvival, 0, 100),
                CollapseHp = Num(f, where, "garrison.collapseHp", g.CollapseHp, 1, 100),
                Doors = doors,
                Owned = g.Owned,
                LauncherTags = g.LauncherTags is { Count: > 0 } lt ? Tags(c, f, where, "garrison.launcherTags", lt) : default,
                DeployTags = g.DeployTags is { Count: > 0 } dt ? Tags(c, f, where, "garrison.deployTags", dt) : default,
            };
        }

        var stance = d.Stance switch
        {
            null or "guard" => Sim.Stance.Guard,
            "aggressive" => Sim.Stance.Aggressive,
            "holdGround" => Sim.Stance.HoldGround,
            "returnFire" => Sim.Stance.ReturnFire,
            "holdFire" => Sim.Stance.HoldFire,
            _ => throw Fail(f, $"{where}: unknown stance \"{d.Stance}\" (want guard, aggressive, holdGround, returnFire or holdFire)"),
        };
        // By default a unit that moves on the ground pursues, unless it sets up to fire (launchers,
        // the mortar) or watches the sky (air defence). Only armed units ever use it.
        if (d.AutoFireDeployed && d.DeployTicks == 0)
        {
            throw Fail(f, $"{where}: autoFireDeployed needs deployTicks");
        }
        var pursue = d.Pursue ?? (speed > 0 && fw == 0 && locomotor != Locomotor.Flying && d.DeployTicks == 0 && d.AirVision == 0);
        if ((d.Pavement || d.Rubble is not null || d.Repairable is not null) && fw == 0)
        {
            throw Fail(f, $"{where}: pavement, rubble and repairable need a footprint (a structure)");
        }
        if (d.Invulnerable && (d.Rubble is not null || d.Repairable is not null))
        {
            throw Fail(f, $"{where}: an invulnerable archetype has no rubble and needs no repair");
        }
        if (d.Neutral && (fw == 0 || d.Garrison is not null))
        {
            throw Fail(f, $"{where}: neutral is for a structure (a footprint) without a garrison (a town building is placed neutral already)");
        }
        var bridge = d.Bridge is { } bj ? BridgeOf(f, where, bj, d, fw, fh) : null;
        var flightSpec = d.Flight is { } fl ? FlightOf(c, f, where, fl, locomotor, speed, d.Lifetime) : null;
        return new Archetype
        {
            Bridge = bridge,
            Aircraft = d.Aircraft is { } acj ? AircraftOf(f, where, acj, locomotor, GroundArmorOf(c, f, where, acj.GroundArmor), flightSpec, PerSecond(speed)) : null,
            Airfield = AirfieldPieceOf(c, f, s.Id, where, d.Airfield, fw),
            Pavement = d.Pavement,
            Untargetable = d.Targetable == false,
            Invulnerable = d.Invulnerable,
            Neutral = d.Neutral,
            RubbleHpPercent = d.Rubble is { } rb ? Num(f, where, "rubble.hpPercent", rb.HpPercent, 1, 100) : 0,
            RepairPercentOfBuild = d.Repairable is { } rp ? Num(f, where, "repairable.fullPercentOfBuild", rp.FullPercentOfBuild, 1, 1000) : 0,
            ReloadPad = d.ReloadPad is { } rpj
                ? fw == 0 ? throw Fail(f, $"{where}: reloadPad needs a footprint (a structure)")
                : new ReloadPad(Tags(c, f, where, "reloadPad.tags", rpj.Tags), Num(f, where, "reloadPad.reloadTicks", rpj.ReloadTicks, 1, Limit),
                    Tenths(Num(f, where, "reloadPad.airfieldRange", rpj.AirfieldRange, 0, Limit)))
                : null,
            Service = d.Service is { } svj
                ? production is not { IntoStation: false } ? throw Fail(f, $"{where}: service needs production with doors")
                : new ServiceSpec(Tags(c, f, where, "service.tags", svj.Tags), Num(f, where, "service.reloadTicks", svj.ReloadTicks, 1, Limit),
                    Num(f, where, "service.repairTicks", svj.RepairTicks, 1, Limit), Num(f, where, "service.cargoTicks", svj.CargoTicks, 1, Limit))
                : null,
            DropZone = d.DropZone is not { } dz ? null
                : fw == 0 ? throw Fail(f, $"{where}: dropZone needs a footprint (a structure)")
                : dz.Offset is not { Count: 2 } dzo ? throw Fail(f, $"{where}: dropZone.offset is [x, y]")
                : new DropZoneSpec(Num(f, where, "dropZone.offset x", dzo[0], -Limit, Limit), Num(f, where, "dropZone.offset y", dzo[1], -Limit, Limit),
                    Tenths(Num(f, where, "dropZone.radius", dz.Radius, 1, Limit))),
            AwayFrom = d.AwayFrom is not { } awj ? null
                : fw == 0 ? throw Fail(f, $"{where}: awayFrom needs a footprint (a structure)")
                : new AwayFromSpec([.. (awj.Archetypes ?? []).Select(id => c.Archetypes.TryGetValue(id, out var ai) ? ai
                        : throw Fail(f, $"{where}: awayFrom names unknown archetype \"{id}\""))],
                    Cells(f, where, "awayFrom.distance", awj.Distance, 1, 10000)),
            Stance = (int)stance,
            AutoFireDeployed = d.AutoFireDeployed,
            Crusher = Num(f, where, "crusher", d.Crusher, 0, 10),
            Crushable = Num(f, where, "crushable", d.Crushable, 0, 10),
            Pursue = pursue,
            Leash = Tenths(Num(f, where, "leash", d.Leash, 0, Limit)),
            RetaliateMax = Tenths(Num(f, where, "retaliateMax", d.RetaliateMax, 0, Limit)),
            Garrison = garrison,
            Construction = construction,
            Production = production,
            Tunnel = tunnel,
            TunnelSize = tunnelSize,
            TunnelSlots = tunnelSlots,
            Index = index,
            Id = s.Id,
            Name = d.Name,
            Glyph = d.Glyph[0],
            Tags = tags,
            Locomotor = locomotor,
            Armor = armor,
            MaxHp = Num(f, where, "hp", d.Hp, 1, Limit),
            Speed = PerSecond(speed),
            TurnRate = turnRate,
            Turret = d.Turret,
            WeaponMode = weaponMode,
            DeployTicks = Num(f, where, "deployTicks", d.DeployTicks, 0, Limit),
            StowTicks = Num(f, where, "stowTicks", d.StowTicks, 0, Limit),
            AccelTicks = Num(f, where, "accelTicks", d.AccelTicks, 0, 400),
            Radius = Tenths(d.Radius > 0 ? Num(f, where, "radius", d.Radius, 1, 100) : locomotor switch
            {
                Locomotor.Foot => 3,
                Locomotor.Wheeled or Locomotor.Tracked => 8,
                _ => 2,
            }),
            Hidden = d.Hidden,
            Locked = d.Locked,
            Cost = Num(f, where, "cost", d.Cost, 0, Limit),
            Vision = Tenths(Num(f, where, "vision", d.Vision, 0, Limit)),
            Detect = Tenths(Num(f, where, "detect", d.Detect, 0, Limit)),
            AirVision = Tenths(Num(f, where, "airVision", d.AirVision, 0, Limit)),
            GroundRadar = Tenths(Num(f, where, "groundRadar", d.GroundRadar, 0, Limit)),
            GroundRadarVehicles = d.GroundRadarVehicles && d.GroundRadar > 0 ? true
                : d.GroundRadarVehicles ? throw Fail(f, $"{where}: groundRadarVehicles needs a groundRadar") : false,
            DirectionalArmor = d.DirectionalArmor is not { } da ? null
                : new DirectionalArmor(Num(f, where, "directionalArmor.front", da.Front, 1, 1000), Num(f, where, "directionalArmor.side", da.Side, 1, 1000),
                    Num(f, where, "directionalArmor.rear", da.Rear, 1, 1000), Num(f, where, "directionalArmor.top", da.Top, 1, 1000),
                    // Whole arcs in degrees to facings (11.25 degrees each) either side: 90 -> 4 (45 degrees).
                    Num(f, where, "directionalArmor.frontArc", da.FrontArc, 0, 180) * 4 / 90, Num(f, where, "directionalArmor.rearArc", da.RearArc, 0, 180) * 4 / 90),
            Airlift = d.Airlift is not { } al ? null
                : d.Aircraft is not { Landing: "runway" } ? throw Fail(f, $"{where}: airlift needs a runway aircraft")
                : new AirliftSpec(Num(f, where, "airlift.seats", al.Seats, 1, 16), Tags(c, f, where, "airlift.tags", al.Tags),
                    Tenths(Num(f, where, "airlift.ramp", al.Ramp, 0, Limit)), Tenths(Num(f, where, "airlift.boardRange", al.BoardRange, 1, Limit)),
                    Num(f, where, "airlift.boardTicks", al.BoardTicks, 1, Limit), Num(f, where, "airlift.unloadTicks", al.UnloadTicks, 1, Limit)),
            CueLock = d.CueLock == 0 ? 0
                : d.AirVision == 0 ? throw Fail(f, $"{where}: cueLock needs an airVision") : Num(f, where, "cueLock", d.CueLock, 1, 100),
            TraverseTicks = d.TraverseTicks == 0 ? 0
                : !d.Turret ? throw Fail(f, $"{where}: traverseTicks needs a turret") : Num(f, where, "traverseTicks", d.TraverseTicks, 1, Limit),
            SweepTicks = d.SweepTicks == 0 ? 0
                : d.AirVision == 0 ? throw Fail(f, $"{where}: sweepTicks needs an airVision") : Num(f, where, "sweepTicks", d.SweepTicks, 1, Limit),
            Helipad = d.Helipad is { } hp
                ? fw == 0 ? throw Fail(f, $"{where}: helipad needs a footprint (a structure)")
                : new HelipadSpec(Num(f, where, "helipad.refuel", hp.Refuel, 0, 1000), Num(f, where, "helipad.rearm", hp.Rearm, 0, 1000),
                    Num(f, where, "helipad.repair", hp.Repair, 0, 1000))
                : null,
            Lifetime = Num(f, where, "lifetime", d.Lifetime, 0, Limit),
            ExpireWhenEmpty = Num(f, where, "expireWhenEmpty", d.ExpireWhenEmpty, 0, Limit),
            HighGround = d.HighGround is { } hg ? new HighGroundSpec
            {
                PercentPerMetre = Num(f, where, "highGround.percentPerMetre", hg.PercentPerMetre, 1, 100),
                MaxPercent = Num(f, where, "highGround.maxPercent", hg.MaxPercent, 1, 400),
                RadiusCells = Num(f, where, "highGround.radius", hg.Radius, 1, 64),
            } : null,
            Flight = flightSpec,
            Mission = d.Mission is { } mj ? MissionOf(f, where, mj, d.Flight is not null) : null,
            Signature = SignatureOf(f, where, d.Signature, d.Flight is not null),
            Loadouts = LoadoutsOf(c, f, where, d, fixedComponents, components),
            LoadoutSwitchTicks = d.LoadoutSwitch == 0 ? 0
                : d.Loadouts is not { Count: > 0 } ? throw Fail(f, $"{where}: loadoutSwitch needs loadouts") : Num(f, where, "loadoutSwitch", d.LoadoutSwitch, 1, Limit),
            RadarSpotting = d.RadarSpotting is not { } rs ? null
                : d.Flight is null ? throw Fail(f, $"{where}: radarSpotting needs a flight block")
                : rs.Tags is not { Count: > 0 } ? throw Fail(f, $"{where}: radarSpotting.tags is empty")
                : new RadarSpotting(Tags(c, f, where, "radarSpotting.tags", rs.Tags), Num(f, where, "radarSpotting.linger", rs.Linger, 0, Limit),
                    Tenths(Num(f, where, "radarSpotting.radius", rs.Radius, 1, 100))),
            Exclusive = d.Exclusive is not { } ex ? -1
                : c.Exclusives.TryGetValue(ex, out var exi) ? exi : throw Fail(f, $"{where}: exclusive \"{ex}\": no such exclusive group"),
            Countermeasures = CountermeasuresOf(f, where, d.Countermeasures, d.Flight is not null),
            Evade = d.Evade is { } ev ? EvadeOf(f, where, ev, d.Flight) : null,
            AttackRun = d.AttackRun is { } arj ? AttackRunOf(f, where, arj, d.Flight) : null,
            Element = d.Element is { } elj ? ElementOf(f, where, elj, d.Flight) : null,
            Pursuit = d.Pursuit is { } puj ? PursuitOf(f, where, puj, d.Flight is not null) : null,
            Gunship = d.Gunship is { } gsj ? GunshipOf(f, where, gsj, d.Flight) : null,
            Kaman = d.Kaman is { } kmj ? KamanOf(c, f, where, kmj, d.Flight) : null,
            Loss = d.Loss is { } lj ? LossOf(c, f, where, lj, d.Flight is not null) : null,
            AirBody = d.AirBody is { } ab ? AirBodyOf(c, f, where, ab, d.Flight is not null, locomotor) : null,
            HeightRaw = (d.Height > 0 ? Num(f, where, "height", d.Height, 1, 1000) : fw > 0 ? 30 : locomotor == Locomotor.Foot ? 18 : 25) * 512L / 10,
            SlotChoices = slots,
            Components = fixedComponents,
            Crew = crew,
            FootprintW = fw,
            FootprintH = fh,
            BuildTime = buildTime,
        };
    }

    // FlightOf compiles a scout drone's flight block: a flying archetype that moves and has an
    // endurance (its lifetime).
    private static ScoutFlight FlightOf(Context c, string f, string where, FlightJson fl, Locomotor locomotor, int speed, int lifetime)
    {
        if (locomotor != Locomotor.Flying || speed == 0 || lifetime == 0)
        {
            throw Fail(f, $"{where}: flight needs a flying archetype with a speed and a lifetime (its endurance)");
        }
        var cls = c.AirClassOf(fl.Interceptible) is { Kind: AirClassKind.Aircraft } ac ? ac.Class
            : throw Fail(f, $"{where}: flight.interceptible \"{fl.Interceptible}\": want an aircraft air class");
        var wing = fl.Kind switch
        {
            null or "drone" or "rotary" => fl.FixedWing is null ? null
                : throw Fail(f, $"{where}: flight.fixedWing needs flight.kind \"fixedWing\""),
            "fixedWing" => WingOf(f, where, fl, speed),
            _ => throw Fail(f, $"{where}: flight.kind must be \"drone\", \"fixedWing\" or \"rotary\""),
        };
        var rotor = fl.Kind == "rotary" ? RotaryOf(c, f, where, fl, speed)
            : fl.Rotary is null ? null : throw Fail(f, $"{where}: flight.rotary needs flight.kind \"rotary\"");
        int turn;
        if (rotor is not null)
        {
            if (fl.TurnRate != 0)
            {
                throw Fail(f, $"{where}: a rotary flight turns by its rotary.yawRate and accel: leave flight.turnRate out");
            }
            // The drone-law rate its acceleration gives at cruise (the airfield code's turn radius reads it): a velocity
            // change of accel / 400 raw a tick on a speed of cruise / 20 raw a tick, in thousandths.
            turn = (int)Math.Clamp(50 * rotor.AccelPerTick * 20 / Math.Max(1, PerSecond(speed).Raw * Sim.World.TicksPerSecond), 1, 1000);
        }
        else if (wing is null)
        {
            turn = Num(f, where, "flight.turnRate", fl.TurnRate, 1, 720) * 8727 / 10000; // as guided.turnRate: per tick, in thousandths of the speed
        }
        else
        {
            if (fl.TurnRate != 0)
            {
                throw Fail(f, $"{where}: a fixedWing flight's turn comes from its envelope: leave flight.turnRate out");
            }
            // The drone-law rate of its max-g turn at cruise speed (the airfield phases and the
            // waypoint capture use it): a velocity change of a(cruise) / 400 raw a tick on a
            // speed of cruise / 20 raw a tick, in thousandths.
            turn = (int)Math.Max(1, 50 * wing.LateralAccel(wing.Cruise) / wing.Cruise);
            if (Tenths(Num(f, where, "flight.orbitRadius", fl.OrbitRadius, 10, Limit)).Raw < wing.Radius(wing.Cruise))
            {
                throw Fail(f, $"{where}: flight.orbitRadius {fl.OrbitRadius} is under its max-g turn radius at cruise speed " +
                    $"({(wing.Radius(wing.Cruise) * 10 + Fixed.OneRaw - 1) / Fixed.OneRaw} tenths of a cell)");
            }
        }
        // The fly zone (E12): the map grown by this margin on every side; it must hold twice its turn
        // radius plus a second of travel (a turn back toward the map from a corner run), checked once,
        // here. 0 takes 48 cells, or that minimum if it is larger (the jets).
        var step = PerSecond(speed).Raw;
        var minZone = rotor is not null ? 0 : 2 * ScoutFlight.TurnRadiusRaw(step, turn) + step * Sim.World.TicksPerSecond;
        var zone = rotor is not null ? Tenths(Num(f, where, "flight.flyZoneMargin", fl.FlyZoneMargin, 0, Limit)).Raw // a helicopter stops: it needs no turning room
            : fl.FlyZoneMargin == 0 ? Math.Max(DefaultFlyZoneCells * Fixed.OneRaw, minZone)
            : Tenths(Num(f, where, "flight.flyZoneMargin", fl.FlyZoneMargin, 1, Limit)).Raw;
        if (zone < minZone)
        {
            throw Fail(f, $"{where}: flight.flyZoneMargin {fl.FlyZoneMargin} is under twice its turn radius plus a second of travel " +
                $"({(minZone * 10 + Fixed.OneRaw - 1) / Fixed.OneRaw} tenths of a cell)");
        }
        return new ScoutFlight(
            Num(f, where, "flight.height", fl.Height, 10, 10000) * 512L / 10,
            Math.Max(1, Num(f, where, "flight.climbRate", fl.ClimbRate, 1, 1000) * 512L / (10 * Sim.World.TicksPerSecond)),
            Num(f, where, "flight.launchHeight", fl.LaunchHeight, 0, 500) * 512L / 10,
            Tenths(Num(f, where, "flight.orbitRadius", fl.OrbitRadius, 10, Limit)),
            turn,
            cls,
            Num(f, where, "flight.reserve", fl.Reserve, 0, Limit),
            Tenths(Num(f, where, "flight.recoverRange", fl.RecoverRange, 1, Limit)),
            Num(f, where, "flight.turnaround", fl.Turnaround, 0, Limit),
            fl.HomeWhenEmpty,
            fl.Relaunch,
            0,
            wing,
            zone,
            fl.HeightByMap is { } hm ? HeightByMapOf(f, where, hm, fl.Height) : null,
            fl.EnduranceByMap is { } em ? new EnduranceByMap(Num(f, where, "flight.enduranceByMap.from", em.From, 16, 100_000),
                Num(f, where, "flight.enduranceByMap.to", em.To, em.From + 1, 100_000), Num(f, where, "flight.enduranceByMap.percent", em.Percent, 100, 1000)) : null,
            fl.SpeedByMap is { } sm ? (wing is null ? throw Fail(f, $"{where}: flight.speedByMap needs a fixedWing flight") :
                new SpeedByMap(Num(f, where, "flight.speedByMap.from", sm.From, 16, 100_000), Num(f, where, "flight.speedByMap.to", sm.To, sm.From + 1, 100_000),
                    Num(f, where, "flight.speedByMap.percent", sm.Percent, 100, 300), Num(f, where, "flight.speedByMap.dashPercent", sm.DashPercent, 100, 300))) : null,
            rotor);
    }

    // RotaryOf compiles a helicopter's flight.rotary block (kind "rotary"): its speeds, acceleration and yaw, the heights
    // of its attacks, its pop-up and out-of-control numbers, its crew and its rider benches.
    private static RotarySpec RotaryOf(Context c, string f, string where, FlightJson fl, int speed)
    {
        var r = fl.Rotary ?? throw Fail(f, $"{where}: flight.kind \"rotary\" needs a flight.rotary block");
        static long PerS(int tenths) => (long)tenths * Fixed.OneRaw / 10; // tenths of a cell a second -> raw a second
        var cruise = PerSecond(speed).Raw * Sim.World.TicksPerSecond;
        var max = PerS(Num(f, where, "flight.rotary.maxSpeed", r.MaxSpeed, 1, Limit));
        if (max < cruise)
        {
            throw Fail(f, $"{where}: flight.rotary.maxSpeed is under its speed (cruise)");
        }
        var accel = PerS(Num(f, where, "flight.rotary.accel", r.Accel, 1, 1000));
        var yaw = Num(f, where, "flight.rotary.yawRate", r.YawRate, 1, 720);
        // One tick's yaw (yaw / 20 degrees) in radians x 16384, then its sine and cosine by their series (all integer).
        var th = (long)yaw * 16384 * 314159 / (20L * 180 * 100000);
        var sin = th - th * th * th / (6L * 16384 * 16384);
        var cos = 16384 - th * th / (2L * 16384) + th * th / 16384 * th / 16384 * th / (24L * 16384);
        var o = r.OutOfControl ?? throw Fail(f, $"{where}: flight.rotary needs an outOfControl block");
        var cr = r.Crew ?? throw Fail(f, $"{where}: flight.rotary needs a crew block");
        var crewUnit = cr.Unit is null ? -1 : ArchetypeRef(c, f, where, "flight.rotary.crew.unit", cr.Unit);
        if (crewUnit >= 0 && c.ArchetypeSources[crewUnit].Def.Locomotor != "foot")
        {
            throw Fail(f, $"{where}: flight.rotary.crew.unit \"{cr.Unit}\" must be a soldier on foot");
        }
        var hide = Num(f, where, "flight.rotary.hideHeight", r.HideHeight, 10, 5000) * 512L / 10;
        var pop = Num(f, where, "flight.rotary.popMax", r.PopMax, 10, 5000) * 512L / 10;
        if (pop <= hide)
        {
            throw Fail(f, $"{where}: flight.rotary.popMax must be above its hideHeight");
        }
        RidersSpec? riders = null;
        if (r.Riders is { } rj)
        {
            riders = new RidersSpec(Num(f, where, "flight.rotary.riders.seats", rj.Seats, 1, 16), Tags(c, f, where, "flight.rotary.riders.tags", rj.Tags),
                Tenths(Num(f, where, "flight.rotary.riders.boardRange", rj.BoardRange, 1, Limit)),
                Num(f, where, "flight.rotary.riders.dropHeight", rj.DropHeight, 1, 1000) * 512L / 10,
                Num(f, where, "flight.rotary.riders.dropTicks", rj.DropTicks, 1, Limit),
                Num(f, where, "flight.rotary.riders.survive", rj.Survive, 0, 100));
        }
        return new RotarySpec
        {
            MaxSpeed = max,
            SideSpeed = PerS(Num(f, where, "flight.rotary.sideSpeed", r.SideSpeed, 1, Limit)),
            AccelPerTick = Math.Max(1, accel / Sim.World.TicksPerSecond),
            YawRate = yaw,
            YawStep = ((int)cos, (int)sin),
            DescentPerTick = Math.Max(1, Num(f, where, "flight.rotary.descentRate", r.DescentRate, 1, 1000) * 512L / (10 * Sim.World.TicksPerSecond)),
            AttackHeightRaw = Num(f, where, "flight.rotary.attackHeight", r.AttackHeight, 10, 5000) * 512L / 10,
            HideHeightRaw = hide,
            PopMaxRaw = pop,
            StandoffPercent = Num(f, where, "flight.rotary.standoff", r.Standoff, 10, 100),
            CoverSearchRaw = Tenths(Num(f, where, "flight.rotary.coverSearch", r.CoverSearch, 0, Limit)).Raw,
            GunPercent = Num(f, where, "flight.rotary.gunRange", r.GunRange, 10, 100),
            LeashRaw = Tenths(Num(f, where, "flight.rotary.leash", r.Leash, 0, Limit)).Raw,
            SpoolTicks = Num(f, where, "flight.rotary.spool", r.Spool, 0, 400),
            TempoLeave = r.Tempo is null ? 100 : Num(f, where, "flight.rotary.tempo.leave", r.Tempo.Leave, 50, 300),
            TempoReturn = r.Tempo is null ? 100 : Num(f, where, "flight.rotary.tempo.return", r.Tempo.Return, 50, 300),
            OutOfControl = new OutOfControlSpec(Num(f, where, "flight.rotary.outOfControl.ticks", o.Ticks, 1, Limit),
                Num(f, where, "flight.rotary.outOfControl.hpPercent", o.HpPercent, 1, 100),
                Num(f, where, "flight.rotary.outOfControl.speedPercent", o.SpeedPercent, 1, 200),
                Num(f, where, "flight.rotary.outOfControl.spinRate", o.SpinRate, 0, 1440),
                Num(f, where, "flight.rotary.outOfControl.endHeight", o.EndHeight, 0, 5000) * 512L / 10),
            Crew = new HeliCrewSpec(Num(f, where, "flight.rotary.crew.count", cr.Count, 0, 16), Num(f, where, "flight.rotary.crew.survive", cr.Survive, 0, 100), crewUnit),
            Riders = riders,
        };
    }

    // HeightByMapOf compiles a flight's height-by-map block (E13, D-ALT1).
    private static HeightByMap HeightByMapOf(string f, string where, HeightByMapJson j, int height)
    {
        var from = Num(f, where, "flight.heightByMap.from", j.From, 16, 100_000);
        var to = Num(f, where, "flight.heightByMap.to", j.To, from + 1, 100_000);
        var h = Num(f, where, "flight.heightByMap.height", j.Height, height, 5000);
        var max = Num(f, where, "flight.heightByMap.max", j.Max, h, 5000);
        return new HeightByMap(from, to, h * 512L / 10, max * 512L / 10);
    }

    // WingOf compiles a fixedWing flight's envelope (E4; FixedWingSpec): speeds to raw a second,
    // each throttle's thrust as the drag at its equilibrium speed, the turn bleed from the sustained
    // turn, checked for an envelope that holds together (min <= corner, min < cruise < attack <= dash,
    // the sustained turn no tighter than the max-g turn at cruise).
    private static FixedWingSpec WingOf(string f, string where, FlightJson fl, int speed)
    {
        var w = fl.FixedWing ?? throw Fail(f, $"{where}: flight.kind \"fixedWing\" needs a flight.fixedWing block");
        var b = w.Burn ?? throw Fail(f, $"{where}: flight.fixedWing.burn is missing");
        static long PerS(int tenths) => (long)tenths * Fixed.OneRaw / 10; // tenths of a cell a second -> raw a second
        var cruise = PerSecond(speed).Raw * Sim.World.TicksPerSecond; // exactly the archetype's speed (AirOps.Speed's unit)
        var dash = PerS(Num(f, where, "flight.fixedWing.dash", w.Dash, 1, Limit));
        var attack = PerS(Num(f, where, "flight.fixedWing.attack", w.Attack, 1, Limit));
        var min = PerS(Num(f, where, "flight.fixedWing.minSpeed", w.MinSpeed, 1, Limit));
        var corner = PerS(Num(f, where, "flight.fixedWing.cornerSpeed", w.CornerSpeed, 1, Limit));
        var cornerR = PerS(Num(f, where, "flight.fixedWing.cornerRadius", w.CornerRadius, 1, Limit));
        var sustR = PerS(Num(f, where, "flight.fixedWing.sustainedRadius", w.SustainedRadius, 1, Limit));
        var accel = PerS(Num(f, where, "flight.fixedWing.accel", w.Accel, 1, 1000));
        var gravity = PerS(Num(f, where, "flight.fixedWing.gravity", w.Gravity, 0, 1000));
        if (!(min < cruise && cruise < attack && attack <= dash && min <= corner && corner <= dash))
        {
            throw Fail(f, $"{where}: flight.fixedWing speeds must keep minSpeed < speed (cruise) < attack <= dash and minSpeed <= cornerSpeed <= dash");
        }
        var burnAb = Num(f, where, "flight.fixedWing.burn.afterburner", b.Afterburner, 0, 1000);
        if (burnAb == 0 && attack != dash)
        {
            throw Fail(f, $"{where}: without an afterburner (burn.afterburner 0) flight.fixedWing.dash must equal attack");
        }
        var turnAccel = corner * corner / cornerR;
        var drag = accel;
        long DragAt(long s) => drag * s / dash * s / dash;
        var aCruise = Math.Min(turnAccel, turnAccel * cruise / corner * cruise / corner);
        var aSus = cruise * cruise / sustR;
        if (aSus > aCruise)
        {
            throw Fail(f, $"{where}: flight.fixedWing.sustainedRadius {w.SustainedRadius} is tighter than its max-g turn at cruise speed " +
                $"({(cruise * cruise / Math.Max(1, aCruise) * 10 + Fixed.OneRaw - 1) / Fixed.OneRaw} tenths of a cell)");
        }
        var (tIdle, tCruise, tMil) = (DragAt(min), DragAt(cruise), DragAt(attack));
        var sf = aSus * 1000 / turnAccel; // the sustained turn in thousandths of its g limit
        var bleed = Math.Max(0, (tMil - tCruise) * 1000 / Math.Max(1, sf) * 1000 / Math.Max(1, sf));
        return new FixedWingSpec(cruise, dash, attack, min, corner, turnAccel, drag, bleed, gravity, tIdle, tCruise, tMil,
            burnAb > 0 ? drag : 0,
            Math.Max(1, Num(f, where, "flight.fixedWing.diveRate", w.DiveRate, 1, 2000) * 512L / (10 * Sim.World.TicksPerSecond)),
            Num(f, where, "flight.fixedWing.floor", w.Floor, 0, 5000) * 512L / 10,
            Num(f, where, "flight.fixedWing.ingressHeight", w.IngressHeight, 0, 10000) * 512L / 10,
            Num(f, where, "flight.fixedWing.lookAhead", w.LookAhead, 0, 400),
            Num(f, where, "flight.fixedWing.separation", w.Separation, 0, 200) * 512L / 10,
            Num(f, where, "flight.fixedWing.burn.idle", b.Idle, 0, 1000),
            Num(f, where, "flight.fixedWing.burn.cruise", b.Cruise, 1, 1000),
            Num(f, where, "flight.fixedWing.burn.military", b.Military, 0, 1000),
            burnAb);
    }

    // MissionOf compiles an aircraft's mission block (a flying archetype with a flight block).
    private static MissionSpec MissionOf(string f, string where, MissionJson mj, bool flight)
    {
        if (!flight)
        {
            throw Fail(f, $"{where}: mission needs a flight block");
        }
        var kinds = AirTargetKinds.None;
        foreach (var k in mj.Guard ?? [])
        {
            kinds |= k switch
            {
                "aircraft" => AirTargetKinds.Aircraft,
                "drones" => AirTargetKinds.Drones,
                "vehicles" => AirTargetKinds.Vehicles,
                "infantry" => AirTargetKinds.Infantry,
                "structures" => AirTargetKinds.Structures,
                _ => throw Fail(f, $"{where}: mission.guard \"{k}\" must be aircraft, drones, vehicles, infantry or structures"),
            };
        }
        return new MissionSpec(Tenths(Num(f, where, "mission.huntLeash", mj.HuntLeash, 0, Limit)),
            kinds == AirTargetKinds.None ? AirTargetKinds.Vehicles | AirTargetKinds.Infantry | AirTargetKinds.Structures : kinds);
    }

    // ComponentRefs resolves a list of component IDs an archetype names,
    // checking each exists, is listed once and has its requirements met.
    private static List<int> ComponentRefs(
        Context c, string file, string where, string field, List<string> ids, TagSet tags, List<Component> components)
    {
        var output = new List<int>();
        foreach (var id in ids)
        {
            if (id is null || !c.Components.TryGetValue(id, out var ci))
            {
                throw Fail(file, $"{where}: unknown component \"{id}\" in {field}");
            }
            if (output.Contains(ci))
            {
                throw Fail(file, $"{where}: component \"{id}\" listed twice in {field}");
            }
            if (!tags.HasAll(components[ci].Requires))
            {
                throw Fail(file, $"{where}: component \"{id}\" in {field} requires tags this archetype lacks");
            }
            output.Add(ci);
        }
        return output;
    }

    // CheckLaunches makes sure every launched archetype can fly to its target
    // on its own: it flies, has a battery lifetime and strikes on contact.
    private static void CheckLaunches(List<Src<ComponentJson>> sources, List<Component> components, List<Archetype> archetypes)
    {
        for (var i = 0; i < components.Count; i++)
        {
            foreach (var e in components[i].Effects.OfType<LaunchEffect>())
            {
                var a = archetypes[e.Archetype];
                var strikes = a.Components.Any(ci => components[ci].Effects.Any(x => x is StrikeEffect));
                if (a.Locomotor != Locomotor.Flying || a.Lifetime == 0 || !(strikes || a.Flight is not null) || a.HasSlots)
                {
                    throw Fail(sources[i].File,
                        $"component \"{components[i].Id}\": launches \"{a.Id}\", which must be flying, have a lifetime, carry a strike component or a flight block (a scout) and have no slots");
                }
            }
        }
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
        var defeated = Tags(c, f, where, "defeatedWithout", d.DefeatedWithout);
        if (defeated.IsEmpty)
        {
            throw Fail(f, $"{where}: defeatedWithout needs at least one tag");
        }
        return new Faction
        {
            Index = index,
            Id = s.Id,
            Name = d.Name,
            Archetypes = archetypes,
            Components = components,
            Modifiers = mods,
            MaxPresetsPerArchetype = Num(f, where, "maxPresetsPerArchetype", d.MaxPresetsPerArchetype, 1, 16),
            DefeatedWithout = defeated,
        };
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
            "vision" => Stat.Vision,
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
            Stat.Speed => PerSecond(flat).Raw,
            Stat.Range or Stat.Vision => Tenths(flat).Raw,
            _ => flat,
        };
        return new Modifier(s, percent, internalFlat);
    }

    // Tenths converts tenths of a cell to Fixed.
    private static Fixed Tenths(int tenths) => Fixed.Frac(tenths, 10);

    // PerSecond converts tenths of a cell per second to Fixed per tick.
    private static Fixed PerSecond(int tenths) => Fixed.Frac(tenths, 10 * Sim.World.TicksPerSecond);

    private static int Num<T>(Src<T> s, string kind, string field, int v, int min, int max) =>
        Num(s.File, $"{kind} \"{s.Id}\"", field, v, min, max);

    // BridgeOf compiles a bridge block (docs/design.md, "Bridges"): a structure whose long side h is the road, placed neutral by
    // the map and never built by players (no construction), never blocking (no garrison, neutral or pavement), always a target
    // that takes damage, and repairable.
    private static BridgeSpec BridgeOf(string f, string where, BridgeJson b, ArchetypeJson d, int fw, int fh)
    {
        if (fw == 0 || fh <= fw)
        {
            throw Fail(f, $"{where}: a bridge is a structure whose footprint is longer (h, the road) than it is wide (w)");
        }
        if (d.Construction is not null || d.Production is not null || d.Garrison is not null || d.Tunnel is not null || d.Airfield is not null ||
            d.Locked || d.Neutral || d.Pavement || d.Rubble is not null || d.Invulnerable || d.Targetable == false)
        {
            throw Fail(f, $"{where}: a bridge has no construction (players never build one), production, garrison, tunnel, airfield, " +
                "locked, neutral (it is neutral by its own rule and blocks nothing), pavement, rubble (its span falls instead), " +
                "invulnerable or targetable");
        }
        if (d.Repairable is null)
        {
            throw Fail(f, $"{where}: a bridge is repairable (repairable.fullPercentOfBuild)");
        }
        var road = b.Road switch
        {
            "highway" => RoadKind.Highway,
            "paved" => RoadKind.Paved,
            "gravel" => RoadKind.Gravel,
            "dirt" => RoadKind.Dirt,
            _ => throw Fail(f, $"{where}: bridge.road \"{b.Road}\" wants highway, paved, gravel or dirt"),
        };
        var stages = b.Stages ?? [];
        if (stages.Count == 0)
        {
            throw Fail(f, $"{where}: bridge.stages needs at least one percent (stage 0 above the first)");
        }
        for (var i = 0; i < stages.Count; i++)
        {
            Num(f, where, "bridge.stages (descending)", stages[i], 1, i == 0 ? 99 : stages[i - 1] - 1);
        }
        return new BridgeSpec(road, [.. stages], Num(f, where, "bridge.reopen", b.Reopen, 1, 100),
            [.. (b.Helpers ?? []).Select(h => Num(f, where, "bridge.helpers", h, 0, 1000))]);
    }

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
            "primary" => (true, SlotKind.Primary),
            "secondary" => (true, SlotKind.Secondary),
            _ => (false, SlotKind.Primary),
        };
        return ok;
    }

    private static ContentException Fail(string file, string message) => new($"content: {file}: {message}");
}
