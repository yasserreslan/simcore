namespace SimCore.Bots;

/// <summary>Optional worker threat avoidance and bounded construction evacuation.</summary>
public sealed record AiBuilderSafetyRules
{
    /// <summary>Nearest visible threats checked for potential compatible weapon reach.</summary>
    public required int ThreatCandidates { get; init; }
    /// <summary>Candidate refuges receiving reachability and threat checks.</summary>
    public required int RefugeCandidates { get; init; }
    /// <summary>Worker evacuation commands per decision.</summary>
    public required int MovesPerDecision { get; init; }
    /// <summary>Cells added to observed enemy weapon reach.</summary>
    public required int RangeBufferCells { get; init; }
    /// <summary>Local escape offsets, in cells.</summary>
    public required int ShelterCells { get; init; }
    /// <summary>Cooldown before evacuated builders may resume other planner work.</summary>
    public required int CooldownTicks { get; init; }
    /// <summary>Known ground-danger ceiling for new/resumed building approaches.</summary>
    public required int MaxDanger { get; init; }
    /// <summary>Intermediate straight-route danger samples for each refuge.</summary>
    public required int RouteSamples { get; init; }

    internal void Validate()
    {
        if (ThreatCandidates is < 1 or > 64 || RefugeCandidates is < 1 or > 32 || MovesPerDecision is < 1 or > 8 ||
            RangeBufferCells is < 0 or > 256 || ShelterCells is < 1 or > 256 || CooldownTicks is < 1 or > 1_000_000 ||
            MaxDanger is < 0 or > 1_000_000 || RouteSamples is < 1 or > 8)
        { throw new ArgumentException("Invalid AI builder safety policy."); }
    }
}
