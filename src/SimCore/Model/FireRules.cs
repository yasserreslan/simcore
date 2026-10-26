using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// What a warhead's blast sets on fire (warhead <c>fire</c>; docs/design.md, "Forest fire"): the forest cells within
/// <see cref="Radius"/> of the impact, at most a rolled <see cref="CountMin"/> to <see cref="CountMax"/> of them (the cells with a
/// standing trunk first, then the nearest), or every one when <see cref="CountMin"/> is <see cref="Every"/>. The fires it lights
/// spread at <see cref="SpreadPercent"/> of the fire rules' chances (0: they burn out where they are). A normal bomb lights a
/// tree or two that do not spread; an incendiary bomb a wide area that spreads hard; the hypersonic missile its ring.
/// </summary>
public sealed class WarheadFire
{
    /// <summary>A <see cref="CountMin"/> meaning every forest cell within the radius.</summary>
    public const int Every = -1;

    /// <summary>The smallest radius it lights cells in, whatever its weapon's splash (raw).</summary>
    public required Fixed Radius { get; init; }

    /// <summary>The radius in percent of its weapon's splash radius (the larger of the two counts; 0: only <see cref="Radius"/>).</summary>
    public required int RadiusPercent { get; init; }

    /// <summary>The fewest cells it lights (<see cref="Every"/>: every forest cell within the radius).</summary>
    public required int CountMin { get; init; }

    /// <summary>The most cells it lights (with <see cref="CountMin"/> the world rolls between them per blast).</summary>
    public required int CountMax { get; init; }

    /// <summary>The percent of the fire rules' spread chances the fires it lights spread at; 0: they do not spread.</summary>
    public required int SpreadPercent { get; init; }

    /// <summary>The radius a blast of this warhead lights within, for a weapon with this splash radius (raw).</summary>
    public long RadiusFor(Fixed splash) => Math.Max(Radius.Raw, splash.Raw * RadiusPercent / 100);

    internal void Digest(Action<long> put)
    {
        put(Radius.Raw);
        put(RadiusPercent);
        put(CountMin);
        put(CountMax);
        put(SpreadPercent);
    }
}

/// <summary>
/// Forest fire (content: the optional top-level <c>fire</c> block, <c>content/mvp/fire.json</c>; docs/design.md, "Forest
/// fire"). It burns only on a map with terrain: a forest cell lit by a warhead with a <see cref="WarheadFire"/>
/// burns for <see cref="BurnTicksMin"/> to <see cref="BurnTicksMax"/> ticks (its trunk with it), may light its neighbours
/// meanwhile by their forest density (<see cref="SpreadPerMille"/>), and burnt out drops one forest level: embers for
/// <see cref="EmberTicks"/>, then ash, its trunk a black snag. At most <see cref="MaxBurning"/> cells burn at once on the whole
/// map. Burning forest pops sight-blocking smoke (the smoke system's screens), hurts the units with <see cref="DamageTags"/> in
/// it, who walk out on their own, and is a wall or a dearer cell to the movement classes (the terrain rules' <c>fireCost</c>,
/// applied every <see cref="TerrainEveryTicks"/>).
/// </summary>
public sealed class FireRules
{
    /// <summary>The shortest a cell burns (ticks).</summary>
    public required int BurnTicksMin { get; init; }

    /// <summary>The longest a cell burns (ticks): each lit cell's length is rolled between the two.</summary>
    public required int BurnTicksMax { get; init; }

    /// <summary>
    /// The chance, per mille, that a burning cell lights a side neighbour at each spread look (<see cref="SpreadEveryTicks"/>),
    /// by the neighbour's forest density [none, scrub, grove, dense]: dense pine highest, the open never.
    /// </summary>
    public required IReadOnlyList<int> SpreadPerMille { get; init; }

    /// <summary>The percent of <see cref="SpreadPerMille"/> a corner (diagonal) neighbour is lit with.</summary>
    public required int DiagonalPercent { get; init; }

    /// <summary>How often each burning cell looks at its neighbours (ticks), staggered by cell so the looks spread over the ticks.</summary>
    public required int SpreadEveryTicks { get; init; }

    /// <summary>The most cells burning at once on the whole map: nothing more catches fire while it is reached.</summary>
    public required int MaxBurning { get; init; }

    /// <summary>The most cells fire spreading lights in one tick (a blast's own cells do not count).</summary>
    public required int MaxIgnitionsPerTick { get; init; }

    /// <summary>
    /// How often the fires lit and burnt out reach the terrain (ticks; at most <see cref="BurnTicksMin"/>): the costs of the cells
    /// lit (walls to vehicles, dearer to infantry) and the forest of the cells burnt out, in one batch, so routing re-plans once
    /// for a batch, not once a cell.
    /// </summary>
    public required int TerrainEveryTicks { get; init; }

    /// <summary>The ticks a burnt-out cell glows (embers) before it is cold ash.</summary>
    public required int EmberTicks { get; init; }

    /// <summary>The radius of a smoke screen burning forest pops.</summary>
    public required Fixed SmokeRadius { get; init; }

    /// <summary>The ticks a fire's smoke screen stands (a fire still burning there pops another).</summary>
    public required int SmokeTicks { get; init; }

    /// <summary>The most fire smoke screens on the map at once.</summary>
    public required int SmokeMax { get; init; }

    /// <summary>The percent of its max HP a unit with <see cref="DamageTags"/> standing in a burning cell loses at each burn (<see cref="DamageEveryTicks"/>).</summary>
    public required int DamagePercent { get; init; }

    /// <summary>How often fire burns the units in it (ticks).</summary>
    public required int DamageEveryTicks { get; init; }

    /// <summary>The tags of the units fire hurts and that walk out of it on their own (infantry).</summary>
    public required IReadOnlyList<string> DamageTags { get; init; }

    /// <summary>How far (cells, a square ring at a time) a unit in a burning cell looks for the nearest cell not burning to walk to.</summary>
    public required int FleeCells { get; init; }

    internal void Digest(Action<long> put)
    {
        put(BurnTicksMin);
        put(BurnTicksMax);
        foreach (var v in SpreadPerMille)
        {
            put(v);
        }
        put(DiagonalPercent);
        put(SpreadEveryTicks);
        put(MaxBurning);
        put(MaxIgnitionsPerTick);
        put(EmberTicks);
        put(TerrainEveryTicks);
        put(SmokeRadius.Raw);
        put(SmokeTicks);
        put(SmokeMax);
        put(DamagePercent);
        put(DamageEveryTicks);
        put(DamageTags.Count);
        foreach (var t in DamageTags)
        {
            put(t.Length);
            foreach (var ch in t)
            {
                put(ch);
            }
        }
        put(FleeCells);
    }
}
