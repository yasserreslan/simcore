namespace SimCore.Model;

/// <summary>
/// A set of tags as a bitmask over <see cref="ContentSet.Tags"/>: bit i is
/// tag i. Content may use at most <see cref="MaxTags"/> distinct tags.
/// </summary>
/// <param name="Bits">The tag bits.</param>
public readonly record struct TagSet(ulong Bits)
{
    /// <summary>The most distinct tags a content set may use.</summary>
    public const int MaxTags = 64;

    /// <summary>Whether the set is empty.</summary>
    public bool IsEmpty => Bits == 0;

    /// <summary>Whether this set has every tag in required (requirements are all-of).</summary>
    public bool HasAll(TagSet required) => (Bits & required.Bits) == required.Bits;

    /// <summary>
    /// Whether this set passes filter: the filter is empty, or this set has at
    /// least one of its tags (filters such as "affects" are any-of).
    /// </summary>
    public bool Matches(TagSet filter) => filter.Bits == 0 || (Bits & filter.Bits) != 0;
}
