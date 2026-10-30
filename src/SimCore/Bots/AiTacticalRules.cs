namespace SimCore.Bots;

/// <summary>Optional integer policy for mobile ground force targeting and bounded cohesion pauses.</summary>
public sealed record AiTacticalRules
{
    /// <summary>Nearest visible targets checked per force, with ID ties.</summary>
    public required int TargetCandidates { get; init; }
    /// <summary>Local target search radius from any member.</summary>
    public required int EngageCells { get; init; }
    /// <summary>Members with a legal in-range weapon required for a focus order, as a percentage.</summary>
    public required int EngagePercent { get; init; }
    /// <summary>Minimum ticks between tactical orders, also bounded by input delay.</summary>
    public required int OrderTicks { get; init; }
    /// <summary>Weight of armor-adjusted weapon effectiveness.</summary>
    public required int WeaponWeight { get; init; }
    /// <summary>Bonus for an enemy visibly targeting a force member.</summary>
    public required int ReturnFireBonus { get; init; }
    /// <summary>Bonus for the force's persistent objective subject.</summary>
    public required int ObjectiveBonus { get; init; }
    /// <summary>Bonus for retaining the current visible focus target.</summary>
    public required int FocusBonus { get; init; }
    /// <summary>Weight per missing HP percentage point.</summary>
    public required int FinishWeight { get; init; }
    /// <summary>Travel penalty per cell from the force's lead unit.</summary>
    public required int DistanceWeight { get; init; }
    /// <summary>Maximum distance from the force center before regrouping is considered.</summary>
    public required int RegroupCells { get; init; }
    /// <summary>Maximum duration of one regroup attempt.</summary>
    public required int RegroupTicks { get; init; }
    /// <summary>Delay after an attempt before another may start.</summary>
    public required int RegroupRetryTicks { get; init; }
    /// <summary>Actual member positions checked for a reachable rally point.</summary>
    public required int RallyCandidates { get; init; }

    internal void Validate()
    {
        Check(TargetCandidates, 1, 64); Check(RallyCandidates, 1, 64);
        Check(EngageCells, 1, 8192); Check(RegroupCells, 1, 8192); Check(EngagePercent, 1, 100);
        foreach (var n in new[] { OrderTicks, RegroupTicks, RegroupRetryTicks }) { Check(n, 1, 1_000_000); }
        foreach (var n in new[] { WeaponWeight, ReturnFireBonus, ObjectiveBonus, FocusBonus, FinishWeight, DistanceWeight }) { Check(n, 0, 100_000); }
    }

    private static void Check(int n, int min, int max)
    { if (n < min || n > max) { throw new ArgumentException($"AI tactical value must be in {min}..{max}."); } }
}
