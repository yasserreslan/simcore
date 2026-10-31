namespace SimCore.Bots;

/// <summary>Optional bounded cargo-flow selection and trip progress policy.</summary>
public sealed record AiLogisticsRules
{
    /// <summary>Shortlisted store candidates receiving access and danger checks per selection.</summary>
    public required int Candidates { get; init; }
    /// <summary>New selections, including a loaded truck's delivery leg, per decision.</summary>
    public required int SelectionsPerDecision { get; init; }
    /// <summary>Existing trip access/stock reviews per decision, oldest review first.</summary>
    public required int ReviewsPerDecision { get; init; }
    /// <summary>Interval between destination/access/stock reviews.</summary>
    public required int ReviewTicks { get; init; }
    /// <summary>No travel, transfer or loading/unloading progress before cancellation.</summary>
    public required int StallTicks { get; init; }
    /// <summary>Concurrent planned trucks per source or receiving store.</summary>
    public required int TrucksPerStore { get; init; }
    /// <summary>Radius for observing friendly service demand around a store.</summary>
    public required int DemandCells { get; init; }
    /// <summary>Value of an uncovered full-capacity stock deficit.</summary>
    public required int DeficitWeight { get; init; }
    /// <summary>Value per nearby needy friendly consumer, capped by the candidate budget.</summary>
    public required int ConsumerWeight { get; init; }
    /// <summary>Finite source stock percentage protected from new loading assignments.</summary>
    public required int SourceReservePercent { get; init; }

    internal void Validate()
    {
        if (SelectionsPerDecision is < 1 or > 16 || ReviewsPerDecision is < 1 or > 64 || Candidates is < 1 or > 64 || ReviewTicks is < 1 or > 1_000_000 || StallTicks < ReviewTicks || StallTicks > 1_000_000 ||
            TrucksPerStore is < 1 or > 8 || DemandCells is < 1 or > 8192 || DeficitWeight is < 0 or > 100_000 ||
            ConsumerWeight is < 0 or > 100_000 || SourceReservePercent is < 0 or > 100)
        { throw new ArgumentException("Invalid AI logistics policy."); }
    }
}
