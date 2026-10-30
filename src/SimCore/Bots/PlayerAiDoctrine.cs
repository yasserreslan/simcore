using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Selected faction identity and nominal force settings; diagnostics do not drive decisions.</summary>
public sealed record AiDoctrineView(string Faction, string Name, string Intent, int ForceSize, int ForceMinimum,
    int Scouts, int OperationForces, bool Tunnels, bool AirOperations);

public sealed partial class PlayerAi
{
    private AiDoctrineRules? Doctrine => _rules.Foundation is null ? null : _plan?.Doctrine;
    private int ForceCapacity => Doctrine?.ForceSize ?? _rules.ForceSize;
    private int ForceMinimum => Doctrine?.ForceMinimum ?? _rules.ForceMinimum;
    private int MissionTicks => Doctrine?.MissionTicks ?? _rules.MissionTicks;
    private int StallTicks => Doctrine?.StallTicks ?? _rules.StallTicks;
    private int ScoutLimit => Doctrine?.ScoutMissions ?? _rules.ScoutMissions;
    private int ReadyHp => Doctrine?.ReadyHpPercent ?? _rules.ReadyHpPercent;
    private int RetreatHp => Doctrine?.RetreatHpPercent ?? _rules.RetreatHpPercent;
    private int OperationForces => Doctrine?.OperationForces ?? _rules.Foundation!.OperationForces;
    private int OperationGather => Doctrine?.GatherTicks ?? _rules.Foundation!.OperationGatherTicks;
    private int StageDistance => Doctrine?.StageCells ?? _rules.Foundation!.StageCells;
    private int FlankDistance => Doctrine?.FlankCells ?? _rules.Foundation!.FlankCells;
    private IReadOnlyList<AiForceRequirement> Composition => Doctrine?.Composition ?? _rules.Foundation?.Composition ?? [];

    /// <summary>The current faction doctrine after binding to a world.</summary>
    public AiDoctrineView? FactionDoctrine => Doctrine is { } d && _plan is { } p ?
        new(p.Faction, d.Name, d.Intent, d.ForceSize, d.ForceMinimum, d.ScoutMissions, d.OperationForces, d.Tunnels is not null, d.Air is not null) : null;

    private static AiDoctrineRules? CopyDoctrine(AiDoctrineRules? d) => d is null ? null : d with
    {
        Composition = d.Composition?.ToArray(), Styles = d.Styles?.ToArray(), Roles = d.Roles.ToArray(), TargetRoles = d.TargetRoles.ToArray(),
        Tags = d.Tags.Select(t => t with { AnyTags = t.AnyTags.ToArray() }).ToArray(),
    };

    private int DoctrinePreference(UnitDef def)
    {
        if (Doctrine is not { } d || _world is not { } w) { return 100; }
        if (d.Air is { } air && (def.Archetype.Airfield is not null || def.Archetype.Helipad is not null)) { return air.InfrastructurePercent; }
        foreach (var t in d.Tags)
        { if (t.AnyTags.Any(tag => w.Content.Tags.Contains(tag) && def.Tags.Matches(w.Content.TagsOf(tag)))) { return t.Percent; } }
        var role = RolesOf(def);
        foreach (var r in d.Roles) { if ((role & r.Role) != 0) { return r.Percent; } }
        return 100;
    }

    private long DoctrineTargetValue(int value, AiRole role)
    {
        if (Doctrine is { } d)
        { foreach (var preference in d.TargetRoles) { if ((role & preference.Role) != 0) { return (long)value * preference.Percent / 100; } } }
        return value;
    }

    private bool SavedForceFits(World w, AiMissionMemory m)
    {
        var limit = _rules.Foundation is null ? _rules.ForceSize : PlanForState(w)?.Doctrine?.ForceSize ?? _rules.ForceSize;
        if (m.Members.Count <= limit) { return true; }
        // Preserve an existing complete friendly squad instead of silently dropping or splitting it to fit a doctrine.
        if (m.Members.Count > 64 || m.Members.Count == 0) { return false; }
        var lead = w.Units.FirstOrDefault(u => u.Owner == Player && u.Id == m.Members[0]);
        return lead is { Squad: not 0 } && w.Squad(lead.Squad) is { } squad && squad.Owner == Player && squad.Members.OrderBy(id => id).SequenceEqual(m.Members);
    }
}
