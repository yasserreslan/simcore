namespace SimCore.Bots;

/// <summary>Optional bounded local combat judgment and threat-aware recovery/support policy.</summary>
public sealed record AiCombatRules
{
    /// <summary>Interval between local force assessments.</summary>
    public required int AssessmentTicks { get; init; }
    /// <summary>Radius around force members for local participants.</summary>
    public required int LocalCells { get; init; }
    /// <summary>Participants per side; truncated assessments cannot initiate withdrawal.</summary>
    public required int LocalUnits { get; init; }
    /// <summary>Weapon compatibility checks per assessment, across both sides.</summary>
    public required int PairChecks { get; init; }
    /// <summary>Enemy power percentage of own power required to start pressure confirmation.</summary>
    public required int RetreatRatioPercent { get; init; }
    /// <summary>Sustained local disadvantage before withdrawal.</summary>
    public required int ConfirmTicks { get; init; }
    /// <summary>Grace after first active assessment before a local-power withdrawal.</summary>
    public required int CommitTicks { get; init; }
    /// <summary>Contribution of friendly units outside this mission.</summary>
    public required int SupportPercent { get; init; }
    /// <summary>Committed friendly pressure relative to target strength before a targeting penalty.</summary>
    public required int SaturationPercent { get; init; }
    /// <summary>Maximum penalty for excess mission/specialist target commitment.</summary>
    public required int SaturationPenalty { get; init; }
    /// <summary>Recovery destination/provider candidates, apart from home and current position.</summary>
    public required int RecoveryCandidates { get; init; }
    /// <summary>Interval between recovery destination reevaluations.</summary>
    public required int RecoveryTicks { get; init; }
    /// <summary>Endpoint danger ceiling relative to recovering force strength; fallback remains possible.</summary>
    public required int RecoveryDangerPercent { get; init; }
    /// <summary>Utility improvement required before changing a still-valid recovery destination.</summary>
    public required int RecoverySwitchMargin { get; init; }
    /// <summary>Intermediate straight-route threat samples per destination.</summary>
    public required int RouteSamples { get; init; }
    /// <summary>Penalty per travel cell.</summary>
    public required int TravelWeight { get; init; }
    /// <summary>Penalty per known danger point, including sampled route exposure.</summary>
    public required int DangerWeight { get; init; }
    /// <summary>Weapon ammunition percent below which recovery prefers resupply.</summary>
    public required int RecoveryAmmoPercent { get; init; }
    /// <summary>Bonus for a usable ammunition supplier when ammunition is needed.</summary>
    public required int SupplyBonus { get; init; }
    /// <summary>Bonus for a compatible repair/service provider when repair is needed.</summary>
    public required int RepairBonus { get; init; }
    /// <summary>Rearward distance from ground forces for radar and air-defense support.</summary>
    public required int SupportCells { get; init; }
    /// <summary>Danger ceiling relative to nearby friendly strength for unarmored support work.</summary>
    public required int SupportDangerPercent { get; init; }
    /// <summary>Repairers concurrently assigned to one patient.</summary>
    public required int RepairersPerTarget { get; init; }

    internal void Validate()
    {
        foreach (var n in new[] { AssessmentTicks, ConfirmTicks, CommitTicks, RecoveryTicks }) { Check(n, 1, 1_000_000); }
        Check(LocalCells, 1, 8192); Check(LocalUnits, 1, 64); Check(PairChecks, 2, 8192);
        Check(RetreatRatioPercent, 101, 1000); Check(SupportPercent, 0, 100); Check(SaturationPercent, 100, 1000);
        Check(RecoveryCandidates, 1, 64); Check(RecoveryDangerPercent, 1, 1000); Check(RouteSamples, 1, 16);
        Check(RecoveryAmmoPercent, 1, 100); Check(SupportCells, 1, 8192); Check(SupportDangerPercent, 1, 1000); Check(RepairersPerTarget, 1, 8);
        foreach (var n in new[] { SaturationPenalty, RecoverySwitchMargin, TravelWeight, DangerWeight, SupplyBonus, RepairBonus }) { Check(n, 0, 100_000); }
    }

    private static void Check(int n, int min, int max)
    { if (n < min || n > max) { throw new ArgumentException($"AI combat value must be in {min}..{max}."); } }
}
