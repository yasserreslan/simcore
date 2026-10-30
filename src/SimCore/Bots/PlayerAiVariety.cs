using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Detached battle variation identity. The host can record Seed to reproduce AI generation.</summary>
public sealed record AiBattleProfile(ulong Seed, string Style, int CombatPercent, int SupportPercent, int ReconPercent,
    int AttackThresholdPercent, int DefensePercent, int GatherPercent);

public sealed partial class PlayerAi
{
    private readonly ulong? _requestedVarietySeed;
    private ulong _varietySeed;

    /// <summary>Chosen per-player style, stable for this seed/policy. Null disables variation.</summary>
    public AiBattleProfile? BattleProfile => _world is not null && StylePolicy is { } s ?
        new(_varietySeed, s.Name, s.CombatPercent, s.SupportPercent, s.ReconPercent, s.AttackThresholdPercent, s.DefensePercent, s.GatherPercent) : null;

    private AiStyleRules? StylePolicy
    {
        get
        {
            if (_rules.Foundation?.Variety is not { } p) { return null; }
            var pick = (int)(VarietyBits(Variation.Style, 0) % (ulong)p.Styles.Sum(s => s.Weight));
            foreach (var style in p.Styles) { if (pick < style.Weight) { return style; } pick -= style.Weight; }
            return p.Styles[0];
        }
    }

    // Independent domains prevent adding a diagnostic/query from consuming another decision's random draw.
    private enum Variation : ulong { Style = 1, Assembly, Gathering, Production, Count, Objective, Scout, Flank, Approach }

    private ulong VarietyBits(Variation domain, ulong context)
    {
        var salt = _rules.Foundation?.Variety?.SeedSalt ?? 0;
        return Mix(unchecked(_varietySeed + salt + (ulong)Player * 0x9e3779b97f4a7c15UL)) ^
            Mix(unchecked(context + (ulong)domain * 0xd1b54a32d192ed03UL));
    }

    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value ^= value >> 30; value *= 0xbf58476d1ce4e5b9UL;
            value ^= value >> 27; value *= 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }
    }

    private int Noise(Variation domain, ulong context, int limit) => limit == 0 ? 0 :
        (int)(VarietyBits(domain, context) % (ulong)(2 * limit + 1)) - limit;

    private static ulong TextKey(string text)
    {
        ulong key = 14695981039346656037;
        foreach (var c in text) { key = unchecked((key ^ c) * 1099511628211); }
        return key;
    }

    private int AssemblyTicks(World w, uint mission) => _rules.Foundation?.Variety is { } p ?
        Math.Max(1, _rules.AssemblyTicks + Noise(Variation.Assembly, mission, p.AssemblyJitterTicks)) : _rules.AssemblyTicks;

    private int GatherTicks(int ticks, int lifetime, uint operation) => _rules.Foundation?.Variety is { } p ?
        Math.Clamp(ticks * StylePolicy!.GatherPercent / 100 + Noise(Variation.Gathering, operation, p.GatherJitterTicks), 1, lifetime) : ticks;

    private int AttackThreshold(int threshold) => StylePolicy is { } s ? Math.Max(1, threshold * s.AttackThresholdPercent / 100) : threshold;
    private int DefenseShare(int share) => StylePolicy is { } s ? Math.Clamp(share * s.DefensePercent / 100, 0, 100) : share;

    private long ProductionPreference(AiUnitGoal goal, UnitDef def, long priority)
    {
        if (_rules.Foundation?.Variety is not { } p || StylePolicy is not { } s) { return priority; }
        var role = RolesOf(def);
        var percent = def.Builder is not null || def.Cargo is not null || def.Supply is not null || (role & AiRole.Repair) != 0 ? s.SupportPercent :
            def.Weapons.Count == 0 || (role & AiRole.Radar) != 0 ? s.ReconPercent : s.CombatPercent;
        return priority * Math.Max(1, percent + Noise(Variation.Production, TextKey(goal.Unit), p.ProductionJitterPercent)) / 100;
    }

    private int CombatGoalCount(World w, AiUnitGoal goal, int count)
    {
        if (_rules.Foundation?.Variety is not { } p || count == 0 || goal.Against != AiThreatKind.None || _plan is null ||
            !_plan.Production.Any(g => g.Unit == goal.Unit)) { return count; }
        var def = w.Match.Players[Player - 1].Find(w.Content, goal.Unit);
        if (def is null || def.IsStructure || def.Builder is not null || def.Cargo is not null || def.Supply is not null || def.Weapons.Count == 0) { return count; }
        return Math.Clamp(count + Noise(Variation.Count, TextKey(goal.Unit), p.CombatCountJitter), 1,
            goal.Maximum > 0 ? goal.Maximum : _rules.ArmyCap);
    }

    private long ObjectivePreference(World w, uint subject, Cell cell)
    {
        if (_rules.Foundation?.Variety is not { } p) { return 0; }
        var context = Mix(subject) ^ Mix(unchecked((ulong)(uint)cell.X << 32 | (uint)cell.Y)) ^ Mix(w.Tick / (uint)p.PreferenceTicks);
        return Noise(Variation.Objective, context, p.ObjectiveJitter);
    }

    private long ScoutTravel(Cell from, int probe, uint mission)
    {
        var distance = Distance(from, _probes[probe]);
        if (_rules.Foundation?.Variety is not { } p) { return distance; }
        var offset = (int)(VarietyBits(Variation.Scout, ((ulong)mission << 32) | (uint)probe) % (ulong)(p.ScoutJitterCells + 1));
        return Fixed.Sqrt(distance) + (long)offset * Fixed.OneRaw;
    }

    private int FlankDirection => _rules.Foundation?.Variety is null || (VarietyBits(Variation.Flank, 0) & 1) == 0 ? 1 : -1;
    private int ApproachOffset(uint mission) => _rules.Foundation?.Variety is { } p ? Noise(Variation.Approach, mission, p.ApproachJitterCells) : 0;
}
