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

    private const int MaxFootprint = 32;

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

        var tags = CollectTags(archetypes);
        var c = new Context(
            Index(warheads), Index(archetypes), Index(components), Index(tags.Select(t => (t, t)).ToList()),
            Index(armorClasses.Select(a => (a, a)).ToList()), armorClasses, archetypes);

        var compiledWarheads = warheads.Select((w, i) => CompileWarhead(c, w, i)).ToList();
        var compiledComponents = components.Select((s, i) => CompileComponent(c, s, i, compiledWarheads)).ToList();
        var compiledArchetypes = archetypes.Select((s, i) => CompileArchetype(c, s, i, compiledComponents)).ToList();
        CheckUnlocks(components, compiledComponents, compiledArchetypes);
        CheckLaunches(components, compiledComponents, compiledArchetypes);
        var compiledFactions = factions.Select((s, i) => CompileFaction(c, s, i)).ToList();
        return new ContentSet(tags, armorClasses, compiledWarheads, compiledArchetypes, compiledComponents, compiledFactions);
    }

    // Context holds the ID → index maps every pass needs.
    private sealed record Context(
        Dictionary<string, int> Warheads,
        Dictionary<string, int> Archetypes,
        Dictionary<string, int> Components,
        Dictionary<string, int> Tags,
        Dictionary<string, int> ArmorIndex,
        List<string> ArmorClasses,
        List<Src<ArchetypeJson>> ArchetypeSources);

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

    // InterceptorOf compiles an air-defence missile's intercept block: it must be a guided missile
    // (its turn rate and max flight), fired by the operator and forgotten, at the air.
    private static Interceptor InterceptorOf(string f, string where, WeaponJson w, InterceptJson ij)
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
        foreach (var name in ij.Classes)
        {
            classes |= name switch
            {
                "drone" => 1 << (int)InterceptClass.Drone,
                "scout" => 1 << (int)InterceptClass.Scout,
                // Rockets and ballistic missiles need their flight's height and velocity first
                // (see the air defence design, "Hooks for later").
                _ => throw Fail(f, $"{where}: weapon.intercept.classes: \"{name}\" can't be intercepted yet (want drone or scout)"),
            };
        }
        var head = Num(f, where, "weapon.intercept.killHead", ij.KillHead, 0, 100);
        var min = Num(f, where, "weapon.intercept.killMin", ij.KillMin, 0, 100);
        var scoutBest = 0;
        foreach (var (name, best) in (ij.KillBest ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (name != "scout" || (classes & (1 << (int)InterceptClass.Scout)) == 0)
            {
                throw Fail(f, $"{where}: weapon.intercept.killBest: \"{name}\" must be scout, and one of its classes");
            }
            scoutBest = Num(f, where, "weapon.intercept.killBest.scout", best, 1, 100);
        }
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
            scoutBest);
    }

    private static Effect CompileEffect(Context c, Src<ComponentJson> s, EffectJson? e, string where, List<Warhead> warheads)
    {
        if (e is null)
        {
            throw Fail(s.File, $"{where}: null");
        }
        object?[] kinds = [e.Weapon, e.Aura, e.Placer, e.Launch, e.Strike, e.Supply, e.Cargo, e.Trigger, e.Builder, e.BuildAssist, e.Repair, e.Modifier];
        if (kinds.Count(k => k is not null) != 1)
        {
            throw Fail(s.File, $"{where}: want exactly one of weapon, aura, placer, launch, strike, supply, cargo, trigger, builder, buildAssist, repair, modifier");
        }
        var f = s.File;
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
            if ((projectile > 0 || salvo > 1) && splash == 0 && w.Guided is null) // a guided missile hits its target unit
            {
                throw Fail(f, $"{where}: a weapon with projectileSpeed or a salvo needs a splash radius (its shots land on a point, not a unit)");
            }
            return new WeaponEffect
            {
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
                Intercept = w.Interceptible switch
                {
                    null => InterceptClass.None,
                    "ballistic" => InterceptClass.Ballistic,
                    "drone" => InterceptClass.Drone,
                    var other => throw Fail(f, $"{where}: weapon.interceptible \"{other}\" (want ballistic)"),
                },
                Guided = w.Guided is { } gd
                    ? new Guidance(Tenths(Num(f, where, "weapon.guided.maxFlight", gd.MaxFlight, 1, Limit)),
                        // Degrees per second to the chord of one tick's turn, in thousandths of the speed:
                        // about the angle in radians for small turns (pi / 180 / 20 per degree per second).
                        Num(f, where, "weapon.guided.turnRate", gd.TurnRate, 1, 720) * 8727 / 10000,
                        gd.RequiresOperatorStill,
                        Tenths(Num(f, where, "weapon.guided.dropDistance", gd.DropDistance, 0, Limit)))
                    : null,
                Priority = Priorities(c, f, where, w.Priority),
                Interceptor = w.Intercept is { } ij ? InterceptorOf(f, where, w, ij) : null,
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
            };
        }
        if (e.Supply is { } su)
        {
            return new SupplyEffect
            {
                Radius = Tenths(Num(f, where, "supply.radius", su.Radius, 1, Limit)),
                EveryTicks = Num(f, where, "supply.everyTicks", su.EveryTicks, 1, Limit),
                Amount = Num(f, where, "supply.amount", su.Amount, 1, Limit),
                Stock = Num(f, where, "supply.stock", su.Stock, 0, Limit),
                Restock = Num(f, where, "supply.restock", su.Restock, 0, Limit),
                ReloadBonus = Num(f, where, "supply.reloadBonus", su.ReloadBonus, 0, 1000),
                ConstructedStock = Num(f, where, "supply.constructedStock", su.ConstructedStock ?? su.Stock, 0, Limit),
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
        if (d.Production is { } pj)
        {
            production = new ProductionSpec
            {
                Tags = Tags(c, f, where, "production.tags", pj.Tags),
                Bays = Num(f, where, "production.bays", pj.Bays, 1, 8),
                QueueMax = Num(f, where, "production.queueMax", pj.QueueMax, 1, 50),
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
            if (locomotor == Locomotor.None)
            {
                throw Fail(f, $"{where}: only moving archetypes can have a crew");
            }
            crew = new CrewSpec
            {
                Tags = Tags(c, f, where, "crew.tags", cr.Tags),
                EnterRange = Tenths(Num(f, where, "crew.enterRange", cr.EnterRange, 1, Limit)),
                Survival = Num(f, where, "crew.survival", cr.Survival, 0, 100),
                ExitDamage = Num(f, where, "crew.exitDamage", cr.ExitDamage, 0, 99),
                Passengers = Num(f, where, "crew.passengers", cr.Passengers, 0, 64),
                PassengerTags = Tags(c, f, where, "crew.passengerTags", cr.PassengerTags),
            };
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

        return new Archetype
        {
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
            Lifetime = Num(f, where, "lifetime", d.Lifetime, 0, Limit),
            ExpireWhenEmpty = Num(f, where, "expireWhenEmpty", d.ExpireWhenEmpty, 0, Limit),
            HighGround = d.HighGround is { } hg ? new HighGroundSpec
            {
                PercentPerMetre = Num(f, where, "highGround.percentPerMetre", hg.PercentPerMetre, 1, 100),
                MaxPercent = Num(f, where, "highGround.maxPercent", hg.MaxPercent, 1, 400),
                RadiusCells = Num(f, where, "highGround.radius", hg.Radius, 1, 64),
            } : null,
            Flight = d.Flight is { } fl ? FlightOf(f, where, fl, locomotor, speed, d.Lifetime) : null,
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
    private static ScoutFlight FlightOf(string f, string where, FlightJson fl, Locomotor locomotor, int speed, int lifetime)
    {
        if (locomotor != Locomotor.Flying || speed == 0 || lifetime == 0)
        {
            throw Fail(f, $"{where}: flight needs a flying archetype with a speed and a lifetime (its endurance)");
        }
        var cls = fl.Interceptible switch
        {
            "scout" => InterceptClass.Scout,
            _ => throw Fail(f, $"{where}: flight.interceptible must be \"scout\""),
        };
        return new ScoutFlight(
            Num(f, where, "flight.height", fl.Height, 10, 5000) * 512L / 10,
            Math.Max(1, Num(f, where, "flight.climbRate", fl.ClimbRate, 1, 1000) * 512L / (10 * Sim.World.TicksPerSecond)),
            Num(f, where, "flight.launchHeight", fl.LaunchHeight, 0, 500) * 512L / 10,
            Tenths(Num(f, where, "flight.orbitRadius", fl.OrbitRadius, 10, Limit)),
            Num(f, where, "flight.turnRate", fl.TurnRate, 1, 720) * 8727 / 10000, // as guided.turnRate: per tick, in thousandths of the speed
            cls,
            Num(f, where, "flight.reserve", fl.Reserve, 0, Limit),
            Tenths(Num(f, where, "flight.recoverRange", fl.RecoverRange, 1, Limit)),
            Num(f, where, "flight.turnaround", fl.Turnaround, 0, Limit));
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
