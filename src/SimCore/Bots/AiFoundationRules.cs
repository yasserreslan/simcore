namespace SimCore.Bots;

/// <summary>Capabilities used by recruitment, production and specialist assignment. Values may be combined.</summary>
[Flags]
public enum AiRole
{
    /// <summary>No role requirement.</summary>
    None = 0,
    /// <summary>A mobile ground weapon.</summary>
    Frontline = 1,
    /// <summary>A ground guided weapon or a policy-designated counter.</summary>
    AntiArmor = 2,
    /// <summary>A weapon that can engage air.</summary>
    AntiAir = 4,
    /// <summary>A deployable or long-range ground weapon.</summary>
    Artillery = 8,
    /// <summary>A mobile observation platform.</summary>
    Scout = 16,
    /// <summary>A builder.</summary>
    Engineer = 32,
    /// <summary>A cargo carrier.</summary>
    Logistics = 64,
    /// <summary>A radar platform.</summary>
    Radar = 128,
    /// <summary>An aircraft with an air weapon.</summary>
    Fighter = 256,
    /// <summary>An aircraft with a ground weapon.</summary>
    Strike = 512,
    /// <summary>A passenger carrier.</summary>
    Transport = 1024,
    /// <summary>A unit with repair effects.</summary>
    Repair = 2048,
}

/// <summary>Threat classes for adaptive production goals.</summary>
public enum AiThreatKind
{
    /// <summary>Unconditional goal.</summary>
    None,
    /// <summary>Observed ground combat units.</summary>
    Ground,
    /// <summary>Observed aircraft.</summary>
    Air,
    /// <summary>Observed ground crewed vehicles.</summary>
    Armor,
    /// <summary>Known enemy structures.</summary>
    Structures,
}

/// <summary>A minimum composition preference. Essential requirements block launch until available or the plan is cancelled.</summary>
public sealed record AiForceRequirement(AiRole Role, int Minimum, bool Essential);

/// <summary>Content tags can supplement inferred capabilities without hardcoding unit names.</summary>
public sealed record AiRoleOverride(AiRole Role, IReadOnlyList<string> AnyTags);

/// <summary>Optional strategic layer. Null on PlayerAiRules preserves the original v1 policy behavior.</summary>
public sealed record AiFoundationRules
{
    /// <summary>Strategic assessment interval.</summary>
    public required int StrategyTicks { get; init; }
    /// <summary>Mobile-contact memory lifetime.</summary>
    public required int ContactTicks { get; init; }
    /// <summary>Maximum remembered mobile contacts.</summary>
    public required int MaxContacts { get; init; }
    /// <summary>Maximum ranked objective candidates.</summary>
    public required int MaxObjectives { get; init; }
    /// <summary>Threat influence radius in cells.</summary>
    public required int ThreatCells { get; init; }
    /// <summary>Enemy strength percent required to choose defense at home.</summary>
    public required int DefendStrengthPercent { get; init; }
    /// <summary>Own strength percent required for an offensive posture.</summary>
    public required int AttackStrengthPercent { get; init; }
    /// <summary>Travel cost weight in objective utility.</summary>
    public required int DistanceWeight { get; init; }
    /// <summary>Observed danger weight in objective utility.</summary>
    public required int DangerWeight { get; init; }
    /// <summary>Additional utility for a known structure.</summary>
    public required int StructureValue { get; init; }
    /// <summary>Utility bonus for a visible home threat.</summary>
    public required int DefenseValue { get; init; }
    /// <summary>Penalty per force already assigned to an objective.</summary>
    public required int AssignmentPenalty { get; init; }
    /// <summary>Simultaneous coordinated operations.</summary>
    public required int MaxOperations { get; init; }
    /// <summary>Forces desired in one operation.</summary>
    public required int OperationForces { get; init; }
    /// <summary>Staging window before an operation may launch with fewer forces.</summary>
    public required int OperationGatherTicks { get; init; }
    /// <summary>Operation lifetime.</summary>
    public required int OperationTicks { get; init; }
    /// <summary>Distance from the enemy objective to the staging line.</summary>
    public required int StageCells { get; init; }
    /// <summary>Sideways offset between operation approaches.</summary>
    public required int FlankCells { get; init; }
    /// <summary>Maximum active specialist jobs.</summary>
    public required int SpecialistJobs { get; init; }
    /// <summary>Specialist job timeout.</summary>
    public required int SpecialistTicks { get; init; }
    /// <summary>Specialist command retry interval.</summary>
    public required int SpecialistOrderTicks { get; init; }
    /// <summary>Recall aircraft below this fuel percent.</summary>
    public required int AirFuelPercent { get; init; }
    /// <summary>Service aircraft below this ammunition percent.</summary>
    public required int AirAmmoPercent { get; init; }
    /// <summary>Half-width of air search/guard areas.</summary>
    public required int AirAreaCells { get; init; }
    /// <summary>Distance to stand off from a ground objective with artillery.</summary>
    public required int ArtilleryCells { get; init; }
    /// <summary>Repair/service threshold.</summary>
    public required int ServiceHpPercent { get; init; }
    /// <summary>Expansion distance from the current home.</summary>
    public required int ExpansionCells { get; init; }
    /// <summary>How long a transport waits for passengers.</summary>
    public required int BoardingTicks { get; init; }
    /// <summary>Maximum passengers requested per transport job.</summary>
    public required int TransportPassengers { get; init; }
    /// <summary>Completed mission outcome history capacity.</summary>
    public required int OutcomeCapacity { get; init; }
    /// <summary>Penalty for a recently failed objective.</summary>
    public required int FailurePenalty { get; init; }
    /// <summary>Age after which a failed objective stops receiving a penalty.</summary>
    public required int OutcomeTicks { get; init; }
    /// <summary>Maximum endpoint/approach candidates evaluated per force.</summary>
    public required int ApproachChecks { get; init; }
    /// <summary>Force role requirements, in recruitment priority order.</summary>
    public IReadOnlyList<AiForceRequirement> Composition { get; init; } = [];
    /// <summary>Additional role classifications from tags.</summary>
    public IReadOnlyList<AiRoleOverride> Roles { get; init; } = [];
    /// <summary>Allow aircraft task and strike planning.</summary>
    public bool Aircraft { get; init; }
    /// <summary>Allow passenger insertion jobs.</summary>
    public bool Transports { get; init; }
    /// <summary>Allow forward construction goals.</summary>
    public bool Expansion { get; init; }

    /// <summary>Minimum duration of a strategic posture; visible home emergencies bypass this hold.</summary>
    public int PostureHoldTicks { get; init; }
    /// <summary>Optional posture-to-command profiles. Empty preserves the first foundation behavior.</summary>
    public IReadOnlyList<AiPostureRules> Postures { get; init; } = [];

    /// <summary>Optional mobile ground-force tactics; null preserves the previous mission executor.</summary>
    public AiTacticalRules? Tactics { get; init; }

    /// <summary>Optional construction scheduling and stalled-project recovery.</summary>
    public AiBuildRules? BuildPlanning { get; init; }

    /// <summary>Optional squad-safe redistribution, local reinforcement and unarmed reconnaissance.</summary>
    public AiForceRules? ForceManagement { get; init; }

    /// <summary>Optional seed-driven battle personalities and bounded tactical/strategic variation.</summary>
    public AiVarietyRules? Variety { get; init; }

    internal void Validate(int maxMissions, int forceSize)
    {
        Tactics?.Validate();
        BuildPlanning?.Validate();
        ForceManagement?.Validate();
        Variety?.Validate();
        Check(PostureHoldTicks, 0, 1_000_000);
        if (Postures is null || Postures.Count is not (0 or 5)) { throw new ArgumentException("Posture control needs all five profiles or none."); }
        var seen = new List<AiPosture>();
        foreach (var p in Postures)
        {
            if (p is null || (int)p.Posture is < 0 or > 4 || seen.Contains(p.Posture)) { throw new ArgumentException("Invalid posture profile."); }
            seen.Add(p.Posture); Check(p.DefenderPercent, 0, 100); Check(p.AssaultForces, 0, maxMissions); Check(p.Scouts, 0, maxMissions);
            foreach (var n in new[] { p.CombatPriority, p.CounterPriority, p.InfrastructurePriority, p.LogisticsPriority, p.ReconPriority, p.DefensePriority })
            { Check(n, 0, 1000); }
            if (p.CombatPriority + p.InfrastructurePriority + p.LogisticsPriority + p.ReconPriority + p.DefensePriority == 0)
            { throw new ArgumentException("Posture profile needs at least one spending priority."); }
        }
        foreach (var n in new[] { StrategyTicks, ContactTicks, OperationGatherTicks, OperationTicks, SpecialistTicks, SpecialistOrderTicks, BoardingTicks, OutcomeTicks })
        { Check(n, 1, 1_000_000); }
        foreach (var n in new[] { ThreatCells, StageCells, FlankCells, AirAreaCells, ArtilleryCells, ExpansionCells }) { Check(n, 1, 8192); }
        Check(MaxContacts, 1, 4096); Check(MaxObjectives, 1, 256); Check(MaxOperations, 1, maxMissions);
        Check(OperationForces, 1, maxMissions); Check(SpecialistJobs, 1, 256); Check(OutcomeCapacity, 1, 4096);
        Check(ApproachChecks, 1, 64); Check(TransportPassengers, 1, 64);
        foreach (var n in new[] { DefendStrengthPercent, AttackStrengthPercent }) { Check(n, 1, 1000); }
        foreach (var n in new[] { AirFuelPercent, AirAmmoPercent, ServiceHpPercent }) { Check(n, 1, 100); }
        foreach (var n in new[] { DistanceWeight, DangerWeight, StructureValue, DefenseValue, AssignmentPenalty, FailurePenalty }) { Check(n, 0, 100_000); }
        if (Composition is null || Roles is null || Composition.Count > 16 || Roles.Count > 32) { throw new ArgumentException("Invalid foundation role lists."); }
        foreach (var r in Composition)
        { if (r is null || r.Role == AiRole.None || ((int)r.Role & ~4095) != 0) { throw new ArgumentException("Invalid composition role."); } Check(r.Minimum, 1, forceSize); }
        foreach (var r in Roles)
        { if (r is null || r.AnyTags is null || r.AnyTags.Count > 64 || ((int)r.Role & ~4095) != 0 || r.AnyTags.Any(string.IsNullOrWhiteSpace)) { throw new ArgumentException("Invalid role override."); } }
    }

    private static void Check(int n, int min, int max)
    { if (n < min || n > max) { throw new ArgumentException($"AI foundation value must be in {min}..{max}."); } }
}
