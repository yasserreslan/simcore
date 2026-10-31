namespace SimCore.Bots;

/// <summary>Optional service-aware recovery using existing mission ownership and deadlines.</summary>
public sealed record AiRecoveryRules
{
    /// <summary>Soft cost per other needy unit near or reserved to a provider.</summary>
    public required int QueuePenalty { get; init; }
    /// <summary>Maximum soft cost when finite stock cannot cover estimated weapon demand.</summary>
    public required int StockPenalty { get; init; }
    /// <summary>Health target while waiting at a compatible radius repairer.</summary>
    public required int ReadyHpPercent { get; init; }

    internal void Validate()
    {
        if (QueuePenalty is < 0 or > 100_000 || StockPenalty is < 0 or > 100_000 || ReadyHpPercent is < 1 or > 100)
        { throw new ArgumentException("Invalid AI recovery service policy."); }
    }
}
