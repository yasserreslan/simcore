namespace SimCore.Bots;

/// <summary>Optional readiness, commitment and bounded shared offensive recovery policy.</summary>
public sealed record AiOperationRules
{
    /// <summary>Interval between operation readiness/progress reviews.</summary>
    public required int ReviewTicks { get; init; }
    /// <summary>Members at staging, armed and healthy required to release an entire force.</summary>
    public required int ReadyPercent { get; init; }
    /// <summary>Ready force strength relative to known objective-area danger required for release.</summary>
    public required int ReleaseStrengthPercent { get; init; }
    /// <summary>Extra gathering window granted by one unsuccessful readiness review.</summary>
    public required int GatherExtensionTicks { get; init; }
    /// <summary>Extra whole forces allowed when known objective danger exceeds assigned strength.</summary>
    public required int ExtraForces { get; init; }
    /// <summary>Absolute maximum extension beyond the original release time.</summary>
    public required int MaxGatherExtensionTicks { get; init; }
    /// <summary>Withdraw when surviving committed members fall below this percentage of the peak.</summary>
    public required int WithdrawMembersPercent { get; init; }
    /// <summary>Maximum time without meaningful approach or observed objective damage.</summary>
    public required int StallTicks { get; init; }
    /// <summary>Distance improvement required to count operation movement as progress.</summary>
    public required int ProgressCells { get; init; }
    /// <summary>Frozen-contact displacement required to update the objective.</summary>
    public required int TargetShiftCells { get; init; }
    /// <summary>Maximum objective position updates before abandoning a moving target.</summary>
    public required int MaxRetargets { get; init; }
    /// <summary>Intermediate straight-line known-danger samples per staging candidate.</summary>
    public required int RouteSamples { get; init; }
    /// <summary>Maximum staging danger relative to this force's strength.</summary>
    public required int ApproachDangerPercent { get; init; }
    /// <summary>Travel penalty per cell from the force lead to staging.</summary>
    public required int ApproachTravelWeight { get; init; }
    /// <summary>Preferred minimum distance between different staging forces.</summary>
    public required int SeparationCells { get; init; }
    /// <summary>Maximum soft penalty for overlapping staging points.</summary>
    public required int SeparationPenalty { get; init; }

    internal void Validate(int operationTicks)
    {
        Check(ReviewTicks, 1, 1_000_000); Check(ReadyPercent, 1, 100); Check(ReleaseStrengthPercent, 1, 1000);
        Check(GatherExtensionTicks, 1, 1_000_000); Check(MaxGatherExtensionTicks, 0, operationTicks);
        Check(ExtraForces, 0, 8); Check(WithdrawMembersPercent, 1, 100); Check(StallTicks, 1, operationTicks); Check(ProgressCells, 1, 8192);
        Check(TargetShiftCells, 1, 8192); Check(MaxRetargets, 0, 16); Check(RouteSamples, 1, 16);
        Check(ApproachDangerPercent, 1, 1000); Check(ApproachTravelWeight, 0, 100_000);
        Check(SeparationCells, 0, 128); Check(SeparationPenalty, 0, 100_000);
    }

    private static void Check(int n, int min, int max)
    { if (n < min || n > max) { throw new ArgumentException($"AI operation value must be in {min}..{max}."); } }
}
