namespace SimCore.Bots;

/// <summary>Optional ownership-preserving force redistribution and local reinforcement policy.</summary>
public sealed record AiForceRules
{
    /// <summary>Ticks between local reinforcement passes.</summary>
    public required int ReinforcementTicks { get; init; }
    /// <summary>Maximum distance of every reinforcement from its recipient's lead unit.</summary>
    public required int ReinforceCells { get; init; }
    /// <summary>Whole groups transferred at most per decision, defense rebalancing first.</summary>
    public required int GroupTransfers { get; init; }
    /// <summary>Nearest eligible groups examined per recipient before reachability checks.</summary>
    public required int CandidateGroups { get; init; }
    /// <summary>In safe Attack posture, lower the home reservation enough to leave one minimum/whole-squad attack force.</summary>
    public bool PreserveAttackForce { get; init; }
    /// <summary>Permit free unarmed ground observers to scout; builders, cargo and dedicated weapon specialists stay excluded.</summary>
    public bool UnarmedScouts { get; init; }

    /// <summary>Maximum duration of a reserved vehicle-crewing attempt.</summary>
    public required int CrewTicks { get; init; }
    /// <summary>Concurrent vehicle-crewing assignments at most.</summary>
    public required int CrewAssignments { get; init; }

    internal void Validate()
    {
        Check(CrewTicks, 1, 1_000_000); Check(CrewAssignments, 1, 256);
        Check(ReinforcementTicks, 1, 1_000_000); Check(ReinforceCells, 1, 8192);
        Check(GroupTransfers, 1, 64); Check(CandidateGroups, 1, 256);
    }

    private static void Check(int n, int min, int max)
    { if (n < min || n > max) { throw new ArgumentException($"AI force-management value must be in {min}..{max}."); } }
}
