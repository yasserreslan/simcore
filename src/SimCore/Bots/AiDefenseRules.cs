namespace SimCore.Bots;

/// <summary>Optional bounded base-asset threat grouping and whole-force defense allocation.</summary>
public sealed record AiDefenseRules
{
    /// <summary>Core asset radius around the oldest surviving production/supply anchor.</summary>
    public required int CoreCells { get; init; }
    /// <summary>Enemy proximity to a protected asset.</summary>
    public required int ProtectCells { get; init; }
    /// <summary>Maximum distance from a threat group's original seed.</summary>
    public required int ClusterCells { get; init; }
    /// <summary>Highest-value friendly assets considered.</summary>
    public required int Assets { get; init; }
    /// <summary>Visible enemy candidates receiving compatibility checks.</summary>
    public required int Contacts { get; init; }
    /// <summary>Maximum independent threat groups.</summary>
    public required int Clusters { get; init; }
    /// <summary>Desired upper bound on separately allocated defense missions.</summary>
    public required int Forces { get; init; }
    /// <summary>Whole squad/loose-unit groups considered when distributing a reserve.</summary>
    public required int CandidateGroups { get; init; }
    /// <summary>Whole-group transfers per decision, also subject to force-management limits.</summary>
    public required int GroupTransfers { get; init; }
    /// <summary>Desired defense coverage relative to threat strength.</summary>
    public required int StrengthPercent { get; init; }
    /// <summary>Minimum assignment commitment while its threat remains current.</summary>
    public required int HoldTicks { get; init; }
    /// <summary>Uncovered threat priority required to override an assignment hold.</summary>
    public required int OverridePercent { get; init; }
    /// <summary>Maximum ground travel distance for an assignment.</summary>
    public required int TravelCells { get; init; }
    /// <summary>Assignment travel penalty per cell.</summary>
    public required int TravelWeight { get; init; }
    /// <summary>Weight of compatible armor-adjusted fire in assignment utility.</summary>
    public required int WeaponWeight { get; init; }
    /// <summary>Protected producer value bonus.</summary>
    public required int ProducerValue { get; init; }
    /// <summary>Protected builder value bonus.</summary>
    public required int BuilderValue { get; init; }
    /// <summary>Protected ammunition supplier value bonus.</summary>
    public required int SupplyValue { get; init; }
    /// <summary>Bonus when a visible enemy explicitly targets the protected asset.</summary>
    public required int TargetedValue { get; init; }

    internal void Validate(int maxMissions)
    {
        foreach (var n in new[] { CoreCells, ProtectCells, ClusterCells, TravelCells }) { Check(n, 1, 8192); }
        Check(Assets, 1, 64); Check(Contacts, 1, 128); Check(Clusters, 1, 16); Check(Forces, 1, maxMissions);
        Check(CandidateGroups, 1, 64); Check(GroupTransfers, 1, 32); Check(StrengthPercent, 1, 1000);
        Check(HoldTicks, 1, 1_000_000); Check(OverridePercent, 100, 1000);
        foreach (var n in new[] { TravelWeight, WeaponWeight, ProducerValue, BuilderValue, SupplyValue, TargetedValue }) { Check(n, 0, 100_000); }
    }

    private static void Check(int n, int min, int max)
    { if (n < min || n > max) { throw new ArgumentException($"AI defense value must be in {min}..{max}."); } }
}
