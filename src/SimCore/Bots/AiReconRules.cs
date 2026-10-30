namespace SimCore.Bots;

/// <summary>Optional information-value probe selection and preservation of independent scouts.</summary>
public sealed record AiReconRules
{
    /// <summary>Oldest/nearest available probes scored for information and danger.</summary>
    public required int ScoredProbes { get; init; }
    /// <summary>Best scored endpoints checked for terrain access per selection.</summary>
    public required int RouteChecks { get; init; }
    /// <summary>Nearest currently visible armed threats checked per scout decision.</summary>
    public required int ThreatCandidates { get; init; }
    /// <summary>Cells beyond a compatible enemy weapon's range at which a scout withdraws.</summary>
    public required int RangeBufferCells { get; init; }
    /// <summary>Known heuristic danger above which a probe is deferred.</summary>
    public required int MaxDanger { get; init; }
    /// <summary>Deferral after known-danger rejection, failed access or scout threat withdrawal.</summary>
    public required int AvoidTicks { get; init; }
    /// <summary>Probe-grid neighborhood radius for stale/unseen information value.</summary>
    public required int InformationRadius { get; init; }
    /// <summary>Bonus per stale/unseen neighboring probe.</summary>
    public required int InformationWeight { get; init; }
    /// <summary>Weight per second since observation, capped by AgeCapTicks.</summary>
    public required int AgeWeight { get; init; }
    /// <summary>Maximum age used in scoring; unseen cells receive this age.</summary>
    public required int AgeCapTicks { get; init; }
    /// <summary>Travel penalty per cell, including existing seeded scout preference.</summary>
    public required int TravelWeight { get; init; }
    /// <summary>Penalty per known danger point.</summary>
    public required int DangerWeight { get; init; }
    /// <summary>Age after which a frozen contact is worth revisiting.</summary>
    public required int ContactRefreshTicks { get; init; }
    /// <summary>Radius around a frozen contact for the refresh bonus.</summary>
    public required int ContactCells { get; init; }
    /// <summary>One bonus for a probe near at least one stale remembered contact.</summary>
    public required int ContactBonus { get; init; }

    internal void Validate()
    {
        Check(ScoredProbes, 1, 1024); Check(RouteChecks, 1, ScoredProbes); Check(ThreatCandidates, 1, 128);
        Check(RangeBufferCells, 0, 64); Check(InformationRadius, 0, 3); Check(ContactCells, 1, 8192);
        foreach (var n in new[] { AvoidTicks, AgeCapTicks, ContactRefreshTicks }) { Check(n, 1, 1_000_000); }
        foreach (var n in new[] { MaxDanger, InformationWeight, AgeWeight, TravelWeight, DangerWeight, ContactBonus }) { Check(n, 0, 100_000); }
    }

    private static void Check(int n, int min, int max)
    { if (n < min || n > max) { throw new ArgumentException($"AI reconnaissance value must be in {min}..{max}."); } }
}
