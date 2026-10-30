namespace SimCore.Bots;

/// <summary>Allocation and spending priorities for one strategic posture. Scores are relative integer weights.</summary>
public sealed record AiPostureRules
{
    /// <summary>Strategic posture to which this profile applies.</summary>
    public required AiPosture Posture { get; init; }
    /// <summary>Share of mobile fighters reserved for home defense, rounded up.</summary>
    public required int DefenderPercent { get; init; }
    /// <summary>Concurrent assault missions, including assembly and staging. Zero prohibits offensive commitments.</summary>
    public required int AssaultForces { get; init; }
    /// <summary>Independent ground scouts allowed, bounded by the outer ScoutMissions limit.</summary>
    public required int Scouts { get; init; }
    /// <summary>Relative priority of combat reinforcements.</summary>
    public required int CombatPriority { get; init; }
    /// <summary>Additional priority of reinforcements countering a currently known threat class.</summary>
    public required int CounterPriority { get; init; }
    /// <summary>Relative priority of production facilities and builders.</summary>
    public required int InfrastructurePriority { get; init; }
    /// <summary>Relative priority of supply, transport, repairs and service facilities.</summary>
    public required int LogisticsPriority { get; init; }
    /// <summary>Relative priority of reconnaissance platforms.</summary>
    public required int ReconPriority { get; init; }
    /// <summary>Relative priority of armed structures.</summary>
    public required int DefensePriority { get; init; }
    /// <summary>Permit aircraft and artillery strikes away from a visible home threat.</summary>
    public bool DistantStrikes { get; init; }
    /// <summary>Permit new forward passenger insertions.</summary>
    public bool Insertions { get; init; }
    /// <summary>Permit forward construction rather than home construction for forward goals.</summary>
    public bool ForwardBuild { get; init; }
}
