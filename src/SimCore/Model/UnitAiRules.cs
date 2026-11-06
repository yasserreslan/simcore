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

    /// <summary>
    /// Straight routes (docs/design.md, "Straight routes"): a route's smoothing takes a straight line, clear for the unit's
    /// class, in place of a stretch of the planned cells when it costs at most this percent more than the stretch...
    /// </summary>
    public int SmoothTolerance { get; init; } = 2;

    /// <summary>
    /// How fast a rolling wheeled vehicle's heading may turn, in degrees a second (docs/design.md, "Straight routes"): it drives
    /// round a bend, it doesn't snap to it. 0: its archetype's turn rate, in facings a tick.
    /// </summary>
    public int WheeledTurn { get; init; } = 90;

    /// <inheritdoc cref="WheeledTurn"/>
    public int TrackedTurn { get; init; } = 60;

    /// <summary>
    /// A vehicle keeps its heading while it would pass within this distance of its aim (a pass-through waypoint: it counts as
    /// reached within a cell), as it is going: a raster's small corrections are no reason to steer. 0: it always steers for its aim.
    /// </summary>
    public Fixed DriveHold { get; init; } = Fixed.FromRaw(819);

    /// <summary>The look-ahead takes a farther aim while the straight way there keeps within this distance of the path's cells it passes.</summary>
    public Fixed AimWithin { get; init; } = Fixed.FromInt(2);

    /// <summary>... and never more than this much more on any one stretch (path cost: a cell of open flat ground costs 10).</summary>
    public int SmoothSlack { get; init; } = 5;

    /// <summary>
    /// The steering look-ahead: a moving ground unit aims at the farthest cell of its path with a clear straight way within
    /// its top speed times this many ticks plus its radius, at least <see cref="AimMin"/> and at most <see cref="AimMax"/>.
    /// </summary>
    public int AimTicks { get; init; } = 100;

    /// <inheritdoc cref="AimTicks"/>
    public Fixed AimMin { get; init; } = Fixed.FromInt(16);

    /// <inheritdoc cref="AimTicks"/>
    public Fixed AimMax { get; init; } = Fixed.FromInt(24);

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
