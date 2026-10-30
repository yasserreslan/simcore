namespace SimCore.Bots;

/// <summary>Optional bounded construction scheduling and recovery policy.</summary>
public sealed record AiBuildRules
{
    /// <summary>Different construction goals examined per decision.</summary>
    public required int GoalsPerDecision { get; init; }
    /// <summary>Placement candidates per goal slice, still sharing BuildChecksPerDecision.</summary>
    public required int CandidatesPerGoal { get; init; }
    /// <summary>Delay between slices of one goal, allowing lower priority goals to progress.</summary>
    public required int SliceRetryTicks { get; init; }
    /// <summary>Backoff after a complete unsuccessful placement sweep.</summary>
    public required int FailedSweepTicks { get; init; }
    /// <summary>No work progress for this duration permits retrying a site's worker assignment.</summary>
    public required int SiteStallTicks { get; init; }
    /// <summary>Delay between resume attempts for one stalled site.</summary>
    public required int SiteRetryTicks { get; init; }
    /// <summary>No work progress for this duration permits cancellation, when enabled.</summary>
    public required int AbandonTicks { get; init; }
    /// <summary>Maximum simultaneous unfinished sites, including stalled and unrelated sites.</summary>
    public required int MaxSites { get; init; }
    /// <summary>Lowest-ID mobile builders preserved from unrelated specialist assignments while construction is needed.</summary>
    public required int BuilderReserve { get; init; }
    /// <summary>Added construction priority for a facility that unlocks unmet production goals.</summary>
    public required int MissingProducerBonus { get; init; }
    /// <summary>Allow CancelBuild on a configured unfinished site with no recent work progress.</summary>
    public bool CancelAbandoned { get; init; }

    internal void Validate()
    {
        Check(GoalsPerDecision, 1, 64); Check(CandidatesPerGoal, 1, 256); Check(MaxSites, 1, 16); Check(BuilderReserve, 0, 64);
        foreach (var n in new[] { SliceRetryTicks, FailedSweepTicks, SiteStallTicks, SiteRetryTicks, AbandonTicks }) { Check(n, 1, 1_000_000); }
        Check(MissingProducerBonus, 0, 100_000);
        if (AbandonTicks <= SiteStallTicks) { throw new ArgumentException("Construction abandonment must exceed its stall timeout."); }
    }

    private static void Check(int n, int min, int max)
    { if (n < min || n > max) { throw new ArgumentException($"AI construction value must be in {min}..{max}."); } }
}
