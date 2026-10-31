namespace SimCore.Bots;

/// <summary>Optional ordinary garage-service selection for depleted free ground vehicles.</summary>
public sealed record AiServiceRules
{
    /// <summary>Maximum compatible garages receiving door/access checks per selection.</summary>
    public required int Candidates { get; init; }
    /// <summary>New priority service reservations per decision.</summary>
    public required int AssignmentsPerDecision { get; init; }
    /// <summary>Other planned/physical vehicles allowed at one garage before refusing a new job.</summary>
    public required int QueueLimit { get; init; }
    /// <summary>Soft cost per other planned/physical vehicle, scaled by garage doors.</summary>
    public required int QueueWeight { get; init; }
    /// <summary>Travel cost per cell.</summary>
    public required int TravelWeight { get; init; }
    /// <summary>Active serviceable weapon-pool percentage below which a free vehicle seeks service.</summary>
    public required int AmmoPercent { get; init; }
    /// <summary>Health percentage below which a free vehicle seeks service.</summary>
    public required int HpPercent { get; init; }

    internal void Validate()
    {
        if (Candidates is < 1 or > 64 || AssignmentsPerDecision is < 1 or > 16 || QueueLimit is < 1 or > 64 ||
            QueueWeight is < 0 or > 100_000 || TravelWeight is < 0 or > 100_000 ||
            AmmoPercent is < 1 or > 100 || HpPercent is < 1 or > 100)
        { throw new ArgumentException("Invalid AI ground service policy."); }
    }
}
