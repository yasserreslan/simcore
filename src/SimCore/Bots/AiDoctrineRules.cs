namespace SimCore.Bots;

/// <summary>First matching role preference supplies the faction's production/recruitment multiplier.</summary>
public sealed record AiRolePreference(AiRole Role, int Percent);
/// <summary>Tag preferences take precedence over role preferences, in declaration order.</summary>
public sealed record AiTagPreference(IReadOnlyList<string> AnyTags, int Percent);

/// <summary>Faction identity above the shared player planner. Null on a faction plan retains global behavior.</summary>
public sealed record AiDoctrineRules
{
    /// <summary>Diagnostic doctrine name.</summary>
    public required string Name { get; init; }
    /// <summary>Plain-language intent.</summary>
    public required string Intent { get; init; }
    /// <summary>Maximum mobile force size.</summary>
    public required int ForceSize { get; init; }
    /// <summary>Preferred launch minimum.</summary>
    public required int ForceMinimum { get; init; }
    /// <summary>Base assembly deadline before battle variation.</summary>
    public required int AssemblyTicks { get; init; }
    /// <summary>Launched mission lifetime.</summary>
    public required int MissionTicks { get; init; }
    /// <summary>Lack-of-progress timeout.</summary>
    public required int StallTicks { get; init; }
    /// <summary>Independent ground reconnaissance cap.</summary>
    public required int ScoutMissions { get; init; }
    /// <summary>Health required for recruitment/service readiness.</summary>
    public required int ReadyHpPercent { get; init; }
    /// <summary>Aggregate force health retreat threshold.</summary>
    public required int RetreatHpPercent { get; init; }
    /// <summary>Offensive strength ratio before battle-style bias.</summary>
    public required int AttackStrengthPercent { get; init; }
    /// <summary>Scale of posture home-defense reservations.</summary>
    public required int DefensePercent { get; init; }
    /// <summary>Desired forces per coordinated operation.</summary>
    public required int OperationForces { get; init; }
    /// <summary>Staging window before battle-style bias.</summary>
    public required int GatherTicks { get; init; }
    /// <summary>Objective-to-staging distance.</summary>
    public required int StageCells { get; init; }
    /// <summary>Lateral approach separation.</summary>
    public required int FlankCells { get; init; }
    /// <summary>Optional complete composition override; null retains global preferences.</summary>
    public IReadOnlyList<AiForceRequirement>? Composition { get; init; }
    /// <summary>Optional faction-specific battle styles; null retains global choices.</summary>
    public IReadOnlyList<AiStyleRules>? Styles { get; init; }
    /// <summary>Ordered role production/recruitment preferences.</summary>
    public IReadOnlyList<AiRolePreference> Roles { get; init; } = [];
    /// <summary>Observed target-value preferences, first matching role wins.</summary>
    public IReadOnlyList<AiRolePreference> TargetRoles { get; init; } = [];
    /// <summary>Ordered tag preferences before role preferences.</summary>
    public IReadOnlyList<AiTagPreference> Tags { get; init; } = [];
    /// <summary>Optional tunnel routing executor.</summary>
    public AiTunnelRules? Tunnels { get; init; }
    /// <summary>Optional air operations executor.</summary>
    public AiAirDoctrineRules? Air { get; init; }

    internal void Validate(int maxMissions)
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Intent)) { throw new ArgumentException("Doctrine needs a name and intent."); }
        Check(ForceSize, 1, 64); Check(ForceMinimum, 1, ForceSize); Check(ScoutMissions, 0, maxMissions);
        Check(OperationForces, 1, maxMissions); Check(StageCells, 1, 8192); Check(FlankCells, 1, 8192);
        foreach (var n in new[] { AssemblyTicks, MissionTicks, StallTicks, GatherTicks }) { Check(n, 1, 1_000_000); }
        Check(ReadyHpPercent, 1, 100); Check(RetreatHpPercent, 1, ReadyHpPercent - 1);
        Check(AttackStrengthPercent, 1, 1000); Check(DefensePercent, 50, 150);
        if (Composition is { } composition)
        {
            if (composition.Count > 16) { throw new ArgumentException("Too many doctrine role requirements."); }
            foreach (var r in composition)
            { if (r is null || r.Role == AiRole.None || ((int)r.Role & ~4095) != 0) { throw new ArgumentException("Invalid doctrine role."); } Check(r.Minimum, 1, ForceSize); }
        }
        if (Styles is not null) { AiVarietyRules.ValidateStyles(Styles); }
        if (Roles is null || TargetRoles is null || Tags is null || Roles.Count > 32 || TargetRoles.Count > 32 || Tags.Count > 32) { throw new ArgumentException("Invalid doctrine preferences."); }
        foreach (var r in Roles.Concat(TargetRoles))
        { if (r is null || r.Role == AiRole.None || ((int)r.Role & ~4095) != 0) { throw new ArgumentException("Invalid doctrine preference."); } Check(r.Percent, 50, 200); }
        foreach (var t in Tags)
        { if (t is null || t.AnyTags is null || t.AnyTags.Count is < 1 or > 64 || t.AnyTags.Any(string.IsNullOrWhiteSpace)) { throw new ArgumentException("Invalid doctrine tags."); } Check(t.Percent, 50, 200); }
        Tunnels?.Validate(); Air?.Validate();
    }

    private static void Check(int n, int min, int max)
    { if (n < min || n > max) { throw new ArgumentException($"AI doctrine value must be in {min}..{max}."); } }
}

/// <summary>Bounded route planning, boarding and evacuation for friendly tunnel networks.</summary>
public sealed record AiTunnelRules
{
    /// <summary>Boarding deadline.</summary>
    public required int BoardTicks { get; init; }
    /// <summary>Trip lifetime before evacuation.</summary>
    public required int TravelTicks { get; init; }
    /// <summary>Order retry interval.</summary>
    public required int RetryTicks { get; init; }
    /// <summary>Maximum approach distance to an entry.</summary>
    public required int MaxEntryCells { get; init; }
    /// <summary>Estimated time saved over straight surface travel before using a tunnel.</summary>
    public required int MinSavingTicks { get; init; }
    /// <summary>Exit danger ceiling as percent of force strength.</summary>
    public required int DangerPercent { get; init; }
    /// <summary>Owned nodes examined by route search.</summary>
    public required int MaxNodes { get; init; }
    /// <summary>Nearest entry/exit candidates.</summary>
    public required int Candidates { get; init; }
    /// <summary>Pairs evaluated per planning attempt.</summary>
    public required int MaxPairs { get; init; }

    internal void Validate()
    {
        if (BoardTicks is < 1 or > 1_000_000 || TravelTicks < BoardTicks || TravelTicks > 1_000_000 || RetryTicks is < 1 or > 1_000_000 ||
            MaxEntryCells is < 1 or > 8192 || MinSavingTicks is < 0 or > 100_000 || DangerPercent is < 1 or > 1000 ||
            MaxNodes is < 2 or > 64 || Candidates is < 1 or > 8 || MaxPairs is < 1 or > 64) { throw new ArgumentException("Invalid tunnel doctrine."); }
    }
}

/// <summary>Readiness, cover, threat and assignment limits for faction air operations.</summary>
public sealed record AiAirDoctrineRules
{
    /// <summary>Minimum fuel percent before assigning a new air job.</summary>
    public required int ReadyFuelPercent { get; init; }
    /// <summary>Minimum ammunition percent before armed sorties.</summary>
    public required int ReadyAmmoPercent { get; init; }
    /// <summary>Minimum health percent before new sorties.</summary>
    public required int ReadyHpPercent { get; init; }
    /// <summary>Known air-defense danger ceiling relative to aircraft strength.</summary>
    public required int DangerPercent { get; init; }
    /// <summary>Simultaneous fighter-cover jobs.</summary>
    public required int CoverJobs { get; init; }
    /// <summary>Simultaneous reconnaissance jobs.</summary>
    public required int ReconJobs { get; init; }
    /// <summary>Simultaneous strikes per target.</summary>
    public required int StrikesPerTarget { get; init; }
    /// <summary>How far covered forces move before the area is updated.</summary>
    public required int CoverMoveCells { get; init; }
    /// <summary>Observation platforms up to this cost may scout; costly aircraft wait for suitable jobs.</summary>
    public required int ReconMaxCost { get; init; }
    /// <summary>Construction priority multiplier for airfields and helipads.</summary>
    public required int InfrastructurePercent { get; init; }
    /// <summary>Nonfighter attackers wait for fighter cover when enemy aircraft are known.</summary>
    public bool RequireCover { get; init; }

    internal void Validate()
    {
        if (ReadyFuelPercent is < 1 or > 100 || ReadyAmmoPercent is < 1 or > 100 || ReadyHpPercent is < 1 or > 100 ||
            DangerPercent is < 1 or > 1000 || CoverJobs is < 1 or > 32 || ReconJobs is < 0 or > 32 || StrikesPerTarget is < 1 or > 16 ||
            CoverMoveCells is < 1 or > 8192 || ReconMaxCost is < 0 or > 1_000_000 || InfrastructurePercent is < 50 or > 200) { throw new ArgumentException("Invalid aircraft doctrine."); }
    }
}
