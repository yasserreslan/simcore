using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// The unit AI numbers (content: the optional top-level <c>unitAi</c> block; the defaults are the
/// Zero Hour player values). Radii that scale with a unit's vision are percents of it; distances
/// are compiled from tenths of a cell; times are ticks. See the engine's design doc, "Unit AI".
/// </summary>
public sealed record UnitAiRules
{
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
