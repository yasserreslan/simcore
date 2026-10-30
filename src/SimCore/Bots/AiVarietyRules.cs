namespace SimCore.Bots;

/// <summary>Small per-battle style biases, applied within the ordinary legal/capability rules.</summary>
public sealed record AiStyleRules
{
    /// <summary>Diagnostic style name.</summary>
    public required string Name { get; init; }
    /// <summary>Relative chance of choosing this style for a player/battle seed.</summary>
    public required int Weight { get; init; }
    /// <summary>Combat production priority scale.</summary>
    public required int CombatPercent { get; init; }
    /// <summary>Builder/logistics/repair production priority scale.</summary>
    public required int SupportPercent { get; init; }
    /// <summary>Observer/radar production priority scale.</summary>
    public required int ReconPercent { get; init; }
    /// <summary>Strategic offensive strength threshold scale.</summary>
    public required int AttackThresholdPercent { get; init; }
    /// <summary>Home defense reservation scale; original minimum and live count still apply.</summary>
    public required int DefensePercent { get; init; }
    /// <summary>Operation staging duration scale.</summary>
    public required int GatherPercent { get; init; }
}

/// <summary>Optional controlled variety. Seeded choices never draw from the simulation RNG.</summary>
public sealed record AiVarietyRules
{
    /// <summary>Policy salt, separate from the host's battle seed.</summary>
    public ulong SeedSalt { get; init; }
    /// <summary>Maximum positive/negative assembly timing offset.</summary>
    public required int AssemblyJitterTicks { get; init; }
    /// <summary>Maximum positive/negative operation gathering offset.</summary>
    public required int GatherJitterTicks { get; init; }
    /// <summary>Maximum positive/negative production priority percentage offset.</summary>
    public required int ProductionJitterPercent { get; init; }
    /// <summary>Maximum count offset for nonessential combat goals; at least one and explicit maxima remain.</summary>
    public required int CombatCountJitter { get; init; }
    /// <summary>Bounded utility offset for observed objective alternatives.</summary>
    public required int ObjectiveJitter { get; init; }
    /// <summary>Time bucket for strategic objective preferences; no per-frame rerolls.</summary>
    public required int PreferenceTicks { get; init; }
    /// <summary>Additional exploration travel score in cells, preserving observation-age precedence.</summary>
    public required int ScoutJitterCells { get; init; }
    /// <summary>Small lateral staging offset.</summary>
    public required int ApproachJitterCells { get; init; }
    /// <summary>Weighted styles; one is retained by the stateless seed mapping for the battle.</summary>
    public IReadOnlyList<AiStyleRules> Styles { get; init; } = [];

    internal void Validate()
    {
        Check(AssemblyJitterTicks, 0, 1200); Check(GatherJitterTicks, 0, 1200);
        Check(ProductionJitterPercent, 0, 25); Check(CombatCountJitter, 0, 2); Check(ObjectiveJitter, 0, 1000);
        Check(PreferenceTicks, 100, 100_000); Check(ScoutJitterCells, 0, 32); Check(ApproachJitterCells, 0, 8);
        ValidateStyles(Styles);
    }

    internal static void ValidateStyles(IReadOnlyList<AiStyleRules> styles)
    {
        if (styles is null || styles.Count is < 1 or > 16) { throw new ArgumentException("Invalid AI style list."); }
        var names = new List<string>();
        foreach (var s in styles)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Name) || names.Contains(s.Name)) { throw new ArgumentException("Invalid AI style name."); }
            names.Add(s.Name); Check(s.Weight, 1, 1000);
            foreach (var n in new[] { s.CombatPercent, s.SupportPercent, s.ReconPercent, s.AttackThresholdPercent, s.DefensePercent, s.GatherPercent })
            { Check(n, 80, 120); }
        }
    }

    private static void Check(int n, int min, int max)
    { if (n < min || n > max) { throw new ArgumentException($"AI variety value must be in {min}..{max}."); } }
}
