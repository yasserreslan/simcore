using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// The unit AI numbers (content: the optional top-level <c>unitAi</c> block; the defaults are the
/// Zero Hour player values). Radii that scale with a unit's vision are percents of it; distances
/// are compiled from tenths of a cell; times are ticks. See the engine's design doc, "Unit AI".
/// </summary>
public sealed record UnitAiRules
{
    /// <summary>Stuck-unit local searches allowed per tick, in unit ID order.</summary>
    public int DetourBudget { get; init; } = 4;

    /// <summary>Local detour search radius, in cells.</summary>
    public int DetourRadiusCells { get; init; } = 12;

    /// <summary>Extra local step cost near a stationary body's clearance circle.</summary>
    public int DetourCost { get; init; } = 100;

    /// <summary>Ticks between attempts by the same unit.</summary>
    public int DetourRetryTicks { get; init; } = 80;

    /// <summary>
    /// Rally slots at a factory's rally point (World.RallySlot): a unit sent there from the factory takes the first free one
    /// in a block of <see cref="RallyRanks"/> lines of this many slots, the flag's slot in the middle.
    /// </summary>
    public int RallyColumns { get; init; } = 5;

    /// <summary>The lines in the block of rally slots (the farthest from the factory fills first); the block full, the nearest free cell round the flag.</summary>
    public int RallyRanks { get; init; } = 3;

    /// <summary>The room between two bodies' edges in the rally slots (slot spacing: a body's width plus this, in whole cells).</summary>
    public Fixed RallyGap { get; init; } = Fixed.FromRaw(4 * Fixed.OneRaw / 10);

    /// <summary>The defaults, used when the content has no <c>unitAi</c> block.</summary>
    public static readonly UnitAiRules Default = new();

    /// <summary>The inner guard circle in percent of vision: after a chase, and on a Guard order, it engages what is in it.</summary>
    public int GuardInnerPercent { get; init; } = 180;

    /// <summary>The leash in percent of vision (unless the archetype overrides it): a chase ends once the target is farther than this from the guard point.</summary>
    public int GuardOuterPercent { get; init; } = 220;

    /// <summary>How far an Aggressive unit acquires on its own, in percent of vision.</summary>
    public int AggressivePercent { get; init; } = 133;

    /// <summary>How long a chase lasts at most (ticks): then it gives up and returns.</summary>
    public int ChaseTicks { get; init; } = 300;

    /// <summary>Ticks between scans of the guard circle (Guard order, Aggressive acquisition, after a chase).</summary>
    public int ScanTicks { get; init; } = 10;

    /// <summary>Ticks between scans while returning to the guard point.</summary>
    public int ReturnScanTicks { get; init; } = 20;

    /// <summary>The farthest shooter (from its victim) that sets off retaliation, unless the archetype overrides it.</summary>
    public Fixed RetaliateMax { get; init; } = Fixed.FromInt(20);

    /// <summary>Idle friends within this distance of a victim join its retaliation.</summary>
    public Fixed RetaliateFriends { get; init; } = Fixed.FromInt(8);

    /// <summary>A victim calls for help at most once per this many ticks (a salvo's splash calls once).</summary>
    public int RetaliateCooldownTicks { get; init; } = 20;
}
