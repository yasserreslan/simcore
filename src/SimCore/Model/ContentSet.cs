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
        IReadOnlyList<AirfieldType>? airfieldTypes = null)
    {
        UnitAi = unitAi ?? UnitAiRules.Default;
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
        Hash = ContentHasher.Compute(this);
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

    /// <summary>The unit AI numbers (stances, retaliation, leash, guard); the defaults without a <c>unitAi</c> block.</summary>
    public UnitAiRules UnitAi { get; }

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
