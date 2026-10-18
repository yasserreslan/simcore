using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>How steep a cell is, for one movement class (thresholds in <see cref="MoveClass"/>).</summary>
public enum SlopeClass : byte
{
    /// <summary>Under the gentle threshold.</summary>
    Flat,

    /// <summary>From the gentle threshold.</summary>
    Gentle,

    /// <summary>From the steep threshold.</summary>
    Steep,

    /// <summary>From the cliff threshold: impassable to everyone.</summary>
    Cliff,
}

/// <summary>
/// How one class of mover deals with terrain: slope thresholds (percent grade), speed and path
/// cost per slope class, the extra cost of climbing, roads and forest. Speeds and percents are
/// 0 to any positive value; a speed or cost of 0 means impassable. Path costs are per cell, with
/// open flat ground at 10.
/// </summary>
public sealed class MoveClass
{
    /// <summary>The class ID (content).</summary>
    public required string Id { get; init; }

    /// <summary>The locomotor it applies to.</summary>
    public required Locomotor Locomotor { get; init; }

    /// <summary>The smallest unit radius it applies to (a heavier class of the same locomotor).</summary>
    public required Fixed MinRadius { get; init; }

    /// <summary>The largest unit radius it applies to (a lighter class of the same locomotor), or none.</summary>
    public Fixed? MaxRadius { get; init; }

    /// <summary>The grade (percent) where Gentle, Steep and Cliff begin.</summary>
    public required int GentleFrom { get; init; }

    /// <inheritdoc cref="GentleFrom"/>
    public required int SteepFrom { get; init; }

    /// <inheritdoc cref="GentleFrom"/>
    public required int CliffFrom { get; init; }

    /// <summary>Speed percent going up and going down, per <see cref="SlopeClass"/> (0: impassable).</summary>
    public required IReadOnlyList<int> SpeedUp { get; init; }

    /// <inheritdoc cref="SpeedUp"/>
    public required IReadOnlyList<int> SpeedDown { get; init; }

    /// <summary>Path cost of a cell per <see cref="SlopeClass"/> (0: impassable; flat open ground 10).</summary>
    public required IReadOnlyList<int> Cost { get; init; }

    /// <summary>Extra cost, in percent, of a step that climbs.</summary>
    public required int UphillCostPercent { get; init; }

    /// <summary>The cost of a road cell, in percent of the same cell off-road.</summary>
    public required int RoadCostPercent { get; init; }

    /// <summary>Cost and speed percent per forest density 0 to 3.</summary>
    public required IReadOnlyList<int> ForestCostPercent { get; init; }

    /// <inheritdoc cref="ForestCostPercent"/>
    public required IReadOnlyList<int> ForestSpeedPercent { get; init; }

    /// <summary>The forest density from which it cannot enter (4: never blocked).</summary>
    public required int ForestBlockedFrom { get; init; }

    /// <summary>
    /// Launchers that deploy (rocket trucks) set up only on cells of a lower grade (percent);
    /// by default the steep threshold (Flat and Gentle ground).
    /// </summary>
    public int DeployMaxGrade { get; init; } = int.MaxValue;

    /// <summary>The slope class of a cell with this grade.</summary>
    public SlopeClass Classify(int grade) =>
        grade >= CliffFrom ? SlopeClass.Cliff : grade >= SteepFrom ? SlopeClass.Steep : grade >= GentleFrom ? SlopeClass.Gentle : SlopeClass.Flat;
}

/// <summary>
/// The terrain rules: the movement classes, in order. A unit takes the last class whose
/// locomotor matches, whose <see cref="MoveClass.MinRadius"/> it reaches and whose
/// <see cref="MoveClass.MaxRadius"/> (if any) it doesn't pass. Loaded from
/// <c>content/terrain/rules.json</c>; they only take part in a game (and its hash) with a terrain
/// map, so flat maps without one are unchanged.
/// </summary>
public sealed class TerrainRules
{
    /// <summary>The movement classes, in content order.</summary>
    public required IReadOnlyList<MoveClass> Classes { get; init; }

    /// <summary>How terrain and forest block sight under fog of war, or null (the map's rules have no vision block: <see cref="VisionRules.Open"/>).</summary>
    public VisionRules? Vision { get; init; }

    /// <summary>The class index for a unit of this locomotor and radius, or -1 (structures, aircraft).</summary>
    public int ClassOf(Locomotor locomotor, Fixed radius)
    {
        var best = -1;
        for (var i = 0; i < Classes.Count; i++)
        {
            if (Classes[i].Locomotor == locomotor && radius.Raw >= Classes[i].MinRadius.Raw &&
                (Classes[i].MaxRadius is not { } max || radius.Raw <= max.Raw))
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>The class index for a unit definition, or -1.</summary>
    public int ClassOf(UnitDef d) => ClassOf(d.Archetype.Locomotor, d.Archetype.Radius);

    /// <summary>A 64-bit FNV-1a digest of every rule, for the game hash.</summary>
    public ulong Digest()
    {
        var h = 14695981039346656037UL;
        void Put(long v)
        {
            for (var i = 0; i < 8; i++)
            {
                h ^= (byte)(v >> (8 * i));
                h *= 1099511628211UL;
            }
        }
        Put(Classes.Count);
        foreach (var c in Classes)
        {
            Put((long)c.Locomotor);
            Put(c.MinRadius.Raw);
            if (c.MaxRadius is { } maxR)
            {
                Put(maxR.Raw); // only for classes that have one: older rules digest as before
            }
            Put(c.GentleFrom);
            Put(c.SteepFrom);
            Put(c.CliffFrom);
            foreach (var list in new[] { c.SpeedUp, c.SpeedDown, c.Cost, c.ForestCostPercent, c.ForestSpeedPercent })
            {
                Put(list.Count);
                foreach (var v in list)
                {
                    Put(v);
                }
            }
            Put(c.UphillCostPercent);
            Put(c.RoadCostPercent);
            Put(c.ForestBlockedFrom);
            Put(c.DeployMaxGrade);
        }
        Vision?.Digest(Put); // only with a vision block: older rules digest as before
        return h;
    }
}
