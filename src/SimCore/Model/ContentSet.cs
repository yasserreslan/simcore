namespace SimCore.Model;

/// <summary>
/// A complete, checked and immutable set of game content. Every collection is
/// sorted by ID (ordinal), and definitions refer to each other by index, so
/// the tick never looks anything up by name. Build one with
/// <c>SimCore.Content.ContentLoader</c>.
/// </summary>
public sealed class ContentSet
{
    /// <summary>Wraps compiled definitions and computes their <see cref="Hash"/>.</summary>
    /// <exception cref="ArgumentException">A collection is not sorted by ID, or an index is wrong.</exception>
    public ContentSet(
        IReadOnlyList<string> tags,
        IReadOnlyList<string> armorClasses,
        IReadOnlyList<Warhead> warheads,
        IReadOnlyList<Archetype> archetypes,
        IReadOnlyList<Component> components,
        IReadOnlyList<Faction> factions,
        UnitAiRules? unitAi = null,
        IReadOnlyList<AirfieldType>? airfieldTypes = null,
        IReadOnlyList<AirClass>? airClasses = null,
        IReadOnlyList<ExclusiveGroup>? exclusiveGroups = null,
        ConcealmentRules? concealment = null,
        FireRules? fire = null,
        SquadRules? squad = null)
    {
        UnitAi = unitAi ?? UnitAiRules.Default;
        Concealment = concealment;
        Fire = fire;
        Squad = squad;
        CheckSorted(tags, t => t, "tags");
        if (tags.Count > TagSet.MaxTags)
        {
            throw new ArgumentException($"content: {tags.Count} tags, max {TagSet.MaxTags}");
        }
        CheckSorted(armorClasses, a => a, "armor classes");
        CheckSorted(warheads, w => w.Id, "warheads");
        CheckSorted(archetypes, a => a.Id, "archetypes");
        CheckSorted(components, c => c.Id, "components");
        CheckSorted(factions, f => f.Id, "factions");
        CheckSorted(airfieldTypes ?? [], t => t.Id, "airfield types");
        CheckIndices(airfieldTypes ?? [], t => t.Index, "airfield types");
        CheckIndices(warheads, w => w.Index, "warheads");
        CheckIndices(archetypes, a => a.Index, "archetypes");
        CheckIndices(components, c => c.Index, "components");
        CheckIndices(factions, f => f.Index, "factions");
        Tags = [.. tags];
        ArmorClasses = [.. armorClasses];
        Warheads = [.. warheads];
        Archetypes = [.. archetypes];
        Components = [.. components];
        Factions = [.. factions];
        AirfieldTypes = [.. airfieldTypes ?? []];
        AirClasses = [.. airClasses ?? []];
        ExclusiveGroups = [.. exclusiveGroups ?? []];
        CheckIndices(ExclusiveGroups, g => g.Index, "exclusive groups");
        for (var i = 0; i < AirClasses.Count; i++)
        {
            if ((int)AirClasses[i].Class != i + 1)
            {
                throw new ArgumentException($"content: air class \"{AirClasses[i].Id}\" has value {(int)AirClasses[i].Class}, want {i + 1}");
            }
        }
        Upgrades = UpgradesOf(Components);
        Hash = ContentHasher.Compute(this);
    }

    /// <summary>The most upgrade names content may use (a unit's and a player's upgrades are bits of a word).</summary>
    public const int MaxUpgrades = 32;

    /// <summary>
    /// The upgrade names the weapons need (<see cref="WeaponEffect.Upgrade"/>), sorted (ordinal); upgrade i is bit i of
    /// <see cref="Sim.Unit.Upgrades"/> and <see cref="Sim.Player.Upgrades"/>. Empty without any.
    /// </summary>
    public IReadOnlyList<string> Upgrades { get; }

    /// <summary>The bit index of an upgrade name in <see cref="Upgrades"/>, or -1.</summary>
    public int UpgradeIndex(string name) => IndexOf(Upgrades, u => u, name);

    // UpgradesOf collects the weapons' upgrade names, sorted (ordinal) without repeats.
    private static List<string> UpgradesOf(IReadOnlyList<Component> components)
    {
        var names = new List<string>();
        foreach (var c in components)
        {
            foreach (var e in c.Effects)
            {
                if (e is WeaponEffect { Upgrade: { } u })
                {
                    names.Add(u);
                }
            }
        }
        names.Sort(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var n in names)
        {
            if (kept.Count == 0 || kept[^1] != n)
            {
                kept.Add(n);
            }
        }
        if (kept.Count > MaxUpgrades)
        {
            throw new ArgumentException($"content: {kept.Count} upgrades, max {MaxUpgrades}");
        }
        return kept;
    }

    /// <summary>Every tag used, sorted; tag i is bit i of a <see cref="TagSet"/>.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>The armor classes, sorted; an archetype's armor and a warhead's columns index this list.</summary>
    public IReadOnlyList<string> ArmorClasses { get; }

    /// <summary>The warheads, sorted by ID.</summary>
    public IReadOnlyList<Warhead> Warheads { get; }

    /// <summary>The archetypes, sorted by ID.</summary>
    public IReadOnlyList<Archetype> Archetypes { get; }

    /// <summary>The components, sorted by ID.</summary>
    public IReadOnlyList<Component> Components { get; }

    /// <summary>The factions, sorted by ID.</summary>
    public IReadOnlyList<Faction> Factions { get; }

    /// <summary>The airfield types, sorted by ID (empty without airfields).</summary>
    public IReadOnlyList<AirfieldType> AirfieldTypes { get; }

    /// <summary>
    /// The air classes in content order (<c>airClasses</c>): class i + 1 is entry i (<see cref="InterceptClass"/>
    /// values are these positions; 0 is none). Empty without air classes.
    /// </summary>
    public IReadOnlyList<AirClass> AirClasses { get; }

    /// <summary>
    /// The exclusive groups (content <c>exclusiveGroups</c>, sorted by ID): archetypes with <see cref="Archetype.Exclusive"/> set belong to
    /// one. Empty without any.
    /// </summary>
    public IReadOnlyList<ExclusiveGroup> ExclusiveGroups { get; }

    /// <summary>Returns the air class with this id, or null.</summary>
    public AirClass? AirClass(string id)
    {
        foreach (var c in AirClasses)
        {
            if (c.Id == id)
            {
                return c;
            }
        }
        return null;
    }

    /// <summary>The unit AI numbers (stances, retaliation, leash, guard); the defaults without a <c>unitAi</c> block.</summary>
    public UnitAiRules UnitAi { get; }

    /// <summary>
    /// Concealment from the air under trees (the <c>concealment</c> block), or null: without it air units see under every canopy,
    /// as before it.
    /// </summary>
    public ConcealmentRules? Concealment { get; }

    /// <summary>
    /// Forest fire (the <c>fire</c> block; docs/design.md, "Forest fire"), or null: nothing burns, whatever the warheads say.
    /// </summary>
    public FireRules? Fire { get; }

    /// <summary>
    /// The infantry squad rules (the <c>squad</c> block; docs/design.md, "Infantry squads"), or null: no squad can be formed.
    /// </summary>
    public SquadRules? Squad { get; }

    /// <summary>
    /// The SHA-256 of the canonical serialization of everything above, as 64
    /// lowercase hex digits. File layout, order and whitespace don't change it.
    /// </summary>
    public string Hash { get; }

    /// <summary>Returns the archetype with this ID, or null.</summary>
    public Archetype? Archetype(string id) => Find(Archetypes, a => a.Id, id);

    /// <summary>Returns the component with this ID, or null.</summary>
    public Component? Component(string id) => Find(Components, c => c.Id, id);

    /// <summary>Returns the faction with this ID, or null.</summary>
    public Faction? Faction(string id) => Find(Factions, f => f.Id, id);

    /// <summary>Returns the airfield type with this ID, or null.</summary>
    public AirfieldType? AirfieldType(string id) => Find(AirfieldTypes, t => t.Id, id);

    /// <summary>Returns the warhead with this ID, or null.</summary>
    public Warhead? Warhead(string id) => Find(Warheads, w => w.Id, id);

    /// <summary>Returns the index of the armor class with this ID, or -1.</summary>
    public int ArmorClass(string id) => IndexOf(ArmorClasses, a => a, id);

    /// <summary>Returns the bits for the named tags; unknown names are ignored.</summary>
    public TagSet TagsOf(params string[] names)
    {
        ulong bits = 0;
        foreach (var n in names)
        {
            var i = IndexOf(Tags, t => t, n);
            if (i >= 0)
            {
                bits |= 1UL << i;
            }
        }
        return new TagSet(bits);
    }

    /// <summary>Returns the names of the tags in set, in tag order.</summary>
    public IReadOnlyList<string> TagNames(TagSet set)
    {
        var output = new List<string>();
        for (var i = 0; i < Tags.Count; i++)
        {
            if ((set.Bits & (1UL << i)) != 0)
            {
                output.Add(Tags[i]);
            }
        }
        return output;
    }

    // Find binary-searches a list sorted by ordinal key.
    private static T? Find<T>(IReadOnlyList<T> items, Func<T, string> key, string id)
        where T : class
    {
        var i = IndexOf(items, key, id);
        return i >= 0 ? items[i] : null;
    }

    private static int IndexOf<T>(IReadOnlyList<T> items, Func<T, string> key, string id)
    {
        int lo = 0, hi = items.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (string.CompareOrdinal(key(items[mid]), id) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < items.Count && string.CompareOrdinal(key(items[lo]), id) == 0 ? lo : -1;
    }

    private static void CheckSorted<T>(IReadOnlyList<T> items, Func<T, string> key, string what)
    {
        for (var i = 1; i < items.Count; i++)
        {
            if (string.CompareOrdinal(key(items[i - 1]), key(items[i])) >= 0)
            {
                throw new ArgumentException($"content: {what} not sorted by ID at \"{key(items[i])}\"");
            }
        }
    }

    private static void CheckIndices<T>(IReadOnlyList<T> items, Func<T, int> index, string what)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (index(items[i]) != i)
            {
                throw new ArgumentException($"content: {what}[{i}] has index {index(items[i])}");
            }
        }
    }
}

/// <summary>
/// An exclusive group of archetypes (content <c>exclusiveGroups</c>; the heavy bombers' "choose 1 of 3"): a player may have at most
/// <see cref="Max"/> units of the group at a time, counting those alive (on the ground or in the air) and those queued or being built;
/// with <see cref="LockType"/> the first one a player's production finishes locks the group to that archetype for the match (a lost one
/// may be rebuilt as the same type). Production refuses an order that would break either rule (<see cref="Sim.World.ExclusiveOf"/>).
/// </summary>
/// <param name="Id">The group's content ID.</param>
/// <param name="Index">Its position in <see cref="ContentSet.ExclusiveGroups"/>.</param>
/// <param name="Max">The most units of the group a player may have at once (at least 1).</param>
/// <param name="LockType">Whether the first one built locks the type for the match.</param>
public sealed record ExclusiveGroup(string Id, int Index, int Max, bool LockType);
