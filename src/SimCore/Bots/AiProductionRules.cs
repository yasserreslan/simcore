namespace SimCore.Bots;

/// <summary>Optional priority and population headroom for essential replacement goals.</summary>
public sealed record AiProductionRules
{
    /// <summary>Priority bonus for an unmet essential production goal.</summary>
    public required int EssentialPriority { get; init; }
    /// <summary>Extra construction priority for a missing producer serving an essential deficit.</summary>
    public required int EssentialProducerBonus { get; init; }
    /// <summary>Maximum population slots held for outstanding essential replacement deficits.</summary>
    public required int ReservedSlots { get; init; }

    internal void Validate()
    {
        if (EssentialPriority is < 0 or > 100_000 || EssentialProducerBonus is < 0 or > 100_000 || ReservedSlots is < 0 or > 64)
        { throw new ArgumentException("Invalid AI production control."); }
    }
}
