namespace SimCore.Bots;

/// <summary>Host-side opponent policy, loaded separately from simulation content. Distances are whole cells; times are ticks.</summary>
public sealed record PlayerAiRules
{
    /// <summary>Policy format; currently 1.</summary>
    public required int Version { get; init; }
    /// <summary>Ticks between decisions.</summary>
    public required int DecisionTicks { get; init; }
    /// <summary>Maximum command records emitted by one decision.</summary>
    public required int CommandsPerDecision { get; init; }
    /// <summary>Maximum simultaneous combat missions, including the home guard.</summary>
    public required int MaxMissions { get; init; }
    /// <summary>Maximum combat missions updated per decision, in rotating ID order.</summary>
    public required int MissionsPerDecision { get; init; }
    /// <summary>Members of an ordinary attack force at most.</summary>
    public required int ForceSize { get; init; }
    /// <summary>Members desired before launching a force.</summary>
    public required int ForceMinimum { get; init; }
    /// <summary>Assembly deadline; a smaller nonempty force launches then.</summary>
    public required int AssemblyTicks { get; init; }
    /// <summary>Maximum duration of a launched combat mission.</summary>
    public required int MissionTicks { get; init; }
    /// <summary>Ticks without progress or a visible fight before recovery.</summary>
    public required int StallTicks { get; init; }
    /// <summary>Ticks allowed for returning to base or supply.</summary>
    public required int RecoveryTicks { get; init; }
    /// <summary>Ticks before a recovered unit may join another mission.</summary>
    public required int RestTicks { get; init; }
    /// <summary>Units retained on home defense; released when no other combat force remains.</summary>
    public required int HomeGuards { get; init; }
    /// <summary>Independent scouting missions at most; zero disables them.</summary>
    public required int ScoutMissions { get; init; }
    /// <summary>Radius around friendly structures to defend.</summary>
    public required int DefendCells { get; init; }
    /// <summary>Distance counting as arrival.</summary>
    public required int ArrivalCells { get; init; }
    /// <summary>Minimum reduction in distance that resets a progress deadline.</summary>
    public required int ProgressCells { get; init; }
    /// <summary>Health required to join a new mission.</summary>
    public required int ReadyHpPercent { get; init; }
    /// <summary>A force below this aggregate health retreats.</summary>
    public required int RetreatHpPercent { get; init; }
    /// <summary>A force with fewer than this percent of its launch members retreats.</summary>
    public required int RetreatMembersPercent { get; init; }
    /// <summary>Ground search region side.</summary>
    public required int RegionCells { get; init; }
    /// <summary>How long to wait before revisiting a seen region.</summary>
    public required int RescoutTicks { get; init; }
    /// <summary>Retry delay after an unsuccessful mission to a probe.</summary>
    public required int ProbeRetryTicks { get; init; }
    /// <summary>Nearest-passable search radius.</summary>
    public required int ReachSearchCells { get; init; }
    /// <summary>Distance to a visible enemy for a squad to switch to Assault.</summary>
    public required int AssaultCells { get; init; }
    /// <summary>Distance past which it returns to March; must exceed AssaultCells.</summary>
    public required int MarchCells { get; init; }
    /// <summary>Produce at most this many queued/in-progress units per factory, limited by its capacity.</summary>
    public required int FactoryQueue { get; init; }
    /// <summary>Additional ticks before retrying a construction order that produced no site.</summary>
    public required int BuildRetryTicks { get; init; }
    /// <summary>Site search radius around home.</summary>
    public required int BuildRadiusCells { get; init; }
    /// <summary>Build candidate spacing.</summary>
    public required int BuildSpacingCells { get; init; }
    /// <summary>Candidate placement checks at most per decision.</summary>
    public required int BuildChecksPerDecision { get; init; }
    /// <summary>Bound on the total alive and pending units the AI deliberately produces.</summary>
    public required int ArmyCap { get; init; }
    /// <summary>Resupply a finite supplier below this stock percent.</summary>
    public required int SupplyLowPercent { get; init; }
    /// <summary>Maximum length of an automatic cargo trip.</summary>
    public required int SupplyTripCells { get; init; }
    /// <summary>Lifecycle trace capacity; zero disables tracing.</summary>
    public required int TraceCapacity { get; init; }
    /// <summary>Form infantry squads when an assembled force can all join one.</summary>
    public bool FormSquads { get; init; }
    /// <summary>Faction-specific production and construction choices, in priority order.</summary>
    public IReadOnlyList<AiFactionPlan> Factions { get; init; } = [];
    /// <summary>Strategic, operational and specialist policies; null selects the original ground policy.</summary>
    public AiFoundationRules? Foundation { get; init; }

    /// <summary>Rejects invalid or unbounded policy inputs before the bot starts.</summary>
    public void Validate()
    {
        if (Version != 1) { throw new ArgumentException("AI policy version must be 1."); }
        Foundation?.Validate(MaxMissions, ForceSize);
        Positive(DecisionTicks, 1200, nameof(DecisionTicks));
        Positive(CommandsPerDecision, 256, nameof(CommandsPerDecision));
        Positive(MaxMissions, 128, nameof(MaxMissions));
        Positive(MissionsPerDecision, MaxMissions, nameof(MissionsPerDecision));
        Positive(ForceSize, 64, nameof(ForceSize));
        Positive(ForceMinimum, ForceSize, nameof(ForceMinimum));
        foreach (var n in new[] { AssemblyTicks, MissionTicks, StallTicks, RecoveryTicks, RestTicks, RescoutTicks, ProbeRetryTicks, BuildRetryTicks })
        { Positive(n, 1_000_000, "duration"); }
        if (HomeGuards < 0 || HomeGuards > ForceSize) { throw new ArgumentException("Invalid home guard count."); }
        if (ScoutMissions < 0 || ScoutMissions > MaxMissions) { throw new ArgumentException("Invalid scout mission count."); }
        foreach (var n in new[] { DefendCells, ArrivalCells, ProgressCells, RegionCells, ReachSearchCells, AssaultCells, MarchCells, BuildRadiusCells, SupplyTripCells })
        { Positive(n, 8192, "distance"); }
        if (MarchCells <= AssaultCells) { throw new ArgumentException("MarchCells must exceed AssaultCells."); }
        foreach (var n in new[] { ReadyHpPercent, RetreatHpPercent, RetreatMembersPercent, SupplyLowPercent })
        { Positive(n, 100, "percent"); }
        if (RetreatHpPercent >= ReadyHpPercent) { throw new ArgumentException("Retreat health must be below ready health."); }
        Positive(FactoryQueue, 64, nameof(FactoryQueue));
        Positive(BuildSpacingCells, BuildRadiusCells, nameof(BuildSpacingCells));
        Positive(BuildChecksPerDecision, 256, nameof(BuildChecksPerDecision));
        Positive(ArmyCap, 10_000, nameof(ArmyCap));
        if (TraceCapacity is < 0 or > 4096 || Factions is null || Factions.Count > 64) { throw new ArgumentException("Invalid policy collections."); }
        var names = new List<string>();
        foreach (var f in Factions)
        {
            if (f is null || string.IsNullOrWhiteSpace(f.Faction) || names.Contains(f.Faction) || f.Production is null || f.Construction is null ||
                f.Production.Count > 64 || f.Construction.Count > 64) { throw new ArgumentException("Invalid or duplicate faction plan."); }
            names.Add(f.Faction);
            foreach (var list in new[] { f.Production, f.Construction })
            {
                var units = new List<string>();
                foreach (var g in list)
                {
                    if (g is null || string.IsNullOrWhiteSpace(g.Unit) || units.Contains(g.Unit)) { throw new ArgumentException("Invalid or duplicate unit goal."); }
                    Positive(g.Count, ArmyCap, "goal count");
                    if (g.ExtraPerThreat < 0 || g.ExtraPerThreat > ArmyCap || g.Maximum < 0 || g.Maximum > ArmyCap ||
                        (int)g.Against is < 0 or > 4) { throw new ArgumentException("Invalid adaptive production goal."); }
                    units.Add(g.Unit);
                }
            }
        }
    }

    private static void Positive(int n, int max, string name)
    {
        if (n <= 0 || n > max) { throw new ArgumentException($"AI {name} must be in 1..{max}."); }
    }
}

/// <summary>Ordered goals for a faction; unavailable lobby presets are skipped.</summary>
public sealed record AiFactionPlan
{
    /// <summary>Faction ID.</summary>
    public required string Faction { get; init; }
    /// <summary>Desired alive plus pending units, first unsatisfied legal goal first.</summary>
    public IReadOnlyList<AiUnitGoal> Production { get; init; } = [];
    /// <summary>Desired structures, including unfinished sites.</summary>
    public IReadOnlyList<AiUnitGoal> Construction { get; init; } = [];
}

/// <summary>A preset or slotless archetype and desired count.</summary>
public sealed record AiUnitGoal
{
    /// <summary>Preset name or archetype ID.</summary>
    public required string Unit { get; init; }
    /// <summary>Desired count.</summary>
    public required int Count { get; init; }
    /// <summary>Threat class that increases the desired count.</summary>
    public AiThreatKind Against { get; init; }
    /// <summary>Extra units per observed threat of Against.</summary>
    public int ExtraPerThreat { get; init; }
    /// <summary>Cap for an adaptive goal; zero uses Count.</summary>
    public int Maximum { get; init; }
    /// <summary>Do not select this goal before this match tick.</summary>
    public uint AfterTick { get; init; }
    /// <summary>Place this structure toward the known front when expansion is enabled.</summary>
    public bool Forward { get; init; }
}
