using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Resolved production intent and blockers from the last friendly observation.</summary>
public sealed record AiProductionGoalView(string Requested, string? Selected, bool ArchetypeFamily, AiRole RequiredRoles,
    int Desired, int Count, bool Essential, bool OperationalProducer, bool PopulationBlocked, string Status);

public sealed partial class PlayerAi
{
    private UnitDef? ResolveGoal(World w, AiUnitGoal goal)
    {
        var player = w.Match.Players[Player - 1];
        if (!goal.ArchetypeFamily)
        {
            var exact = player.Find(w.Content, goal.Unit);
            return exact is not null && HasGoalRoles(exact, goal) ? exact : null;
        }
        var archetype = w.Content.Archetype(goal.Unit);
        if (archetype is null || !player.Faction.Archetypes[archetype.Index]) { return null; }
        var candidates = player.Presets.Where(d => d.Archetype.Index == archetype.Index);
        if (player.Defaults[archetype.Index] is { } standard) { candidates = candidates.Append(standard); }
        return candidates.Where(d => !d.IsStructure && d.Strike is null && !d.Hidden && d.Lifetime == 0 && HasGoalRoles(d, goal))
            .OrderByDescending(d => DoctrinePreference(d))
            .ThenByDescending(d => _rules.Foundation?.Variety is null ? 0UL :
                VarietyBits(Variation.Preset, TextKey(goal.Unit) ^ Mix((ulong)d.Index)))
            .ThenBy(d => d.Index).FirstOrDefault();
    }

    private bool HasGoalRoles(UnitDef def, AiUnitGoal goal)
    {
        if (goal.RequiredRoles == AiRole.None) { return true; }
        var roles = RolesOf(def);
        var usable = def.Weapons.Where(weapon => (def.Archetype.WeaponMode != WeaponMode.PrimaryOnly || weapon.Index == 0) &&
            (weapon.Stock < 0 || def.StockMax[weapon.Stock] > 0));
        if (!usable.Any(weapon => (weapon.Effect.Targets & Layers.Ground) != 0 && weapon.Effect.Interceptor is null))
        { roles &= ~(AiRole.Frontline | AiRole.AntiArmor | AiRole.Artillery | AiRole.Strike); }
        if (!usable.Any(weapon => (weapon.Effect.Targets & Layers.Air) != 0 || weapon.Effect.Interceptor is not null))
        { roles &= ~(AiRole.AntiAir | AiRole.Fighter); }
        return (roles & goal.RequiredRoles) == goal.RequiredRoles;
    }

    private bool MatchesGoal(UnitDef def, UnitDef selected, AiUnitGoal goal) =>
        (goal.ArchetypeFamily ? def.Archetype.Index == selected.Archetype.Index && HasGoalRoles(def, goal) : def.Index == selected.Index);

    private int CountGoal(World w, AiUnitGoal goal, UnitDef selected)
    {
        if (!goal.ArchetypeFamily && !goal.ExcludeDrivers) { return CountDefinition(selected.Index); }
        var count = _own.Count(u => !u.Rubble && MatchesGoal(u.Def, selected, goal) &&
            (!goal.ExcludeDrivers || u.Inside == 0 || Own(u.Inside)?.Driver != u.Id));
        foreach (var facility in _own)
        {
            count += facility.ProductionQueue.Count(index => MatchesGoal(w.Match.Defs[index], selected, goal));
            count += facility.Bays.Count(b => b.Def >= 0 && MatchesGoal(w.Match.Defs[b.Def], selected, goal));
            count += facility.Doors.Count(d => d.Def >= 0 && MatchesGoal(w.Match.Defs[d.Def], selected, goal));
        }
        return count + _pending.Count(p => p.Def >= 0 && p.Kind is CommandKind.Produce or CommandKind.Build && MatchesGoal(w.Match.Defs[p.Def], selected, goal));
    }

    private long ProductionGoalPriority(World w, AiUnitGoal goal, UnitDef def) => GoalPriority(w, goal, def) +
        (goal.Essential && _rules.Foundation?.ProductionControl is { } p ? p.EssentialPriority : 0);

    private static int PopulationCost(UnitDef def) => def.Crew is null ? 1 : 2;

    private int ProductionPopulation(World w)
    {
        var population = _own.Count + _pending.Count(p => p.Kind == CommandKind.Build);
        foreach (var f in _own)
        {
            foreach (var d in f.ProductionQueue) { population += PopulationCost(w.Match.Defs[d]); }
            foreach (var b in f.Bays) { if (b.Def >= 0) { population += PopulationCost(w.Match.Defs[b.Def]); } }
            foreach (var d in f.Doors) { if (d.Def >= 0) { population += PopulationCost(w.Match.Defs[d.Def]); } }
        }
        foreach (var pending in _pending)
        { if (pending.Kind == CommandKind.Produce) { population += PopulationCost(w.Match.Defs[pending.Def]); } }
        return population;
    }

    private int EssentialSlots(World w)
    {
        if (_rules.Foundation?.ProductionControl is not { ReservedSlots: > 0 } p || _plan is null) { return 0; }
        long slots = 0;
        foreach (var goal in _plan.Production)
        {
            if (!goal.Essential || ResolveGoal(w, goal) is not { } def ||
                !(HasOperationalProducer(w, def) || _own.Any(u => Mobile(u) && u.Def.Builder is not null) &&
                    _plan.Construction.Any(g => ResolveGoal(w, g)?.Archetype.Production is { } pr && pr.Builds(def.Tags)))) { continue; }
            slots += Math.Max(0, Desired(goal, w) - CountGoal(w, goal, def)) * (long)PopulationCost(def);
        }
        return (int)Math.Min(p.ReservedSlots, slots);
    }

    private bool ConstructionPopulationAllowed(World w, UnitDef facility)
    {
        if (_rules.Foundation?.ProductionControl is null) { return true; }
        var bootstrap = facility.Archetype.Production is { } producer && _plan is not null && _plan.Production.Any(goal =>
            goal.Essential && ResolveGoal(w, goal) is { } def && CountGoal(w, goal, def) < Desired(goal, w) &&
            producer.Builds(def.Tags) && !HasOperationalProducer(w, def));
        return ProductionPopulation(w) + 1 <= _rules.ArmyCap - (bootstrap ? 0 : EssentialSlots(w));
    }

    /// <summary>Detached resolved preset choices, family counts, real producer availability and cap blockers.</summary>
    public IReadOnlyList<AiProductionGoalView> ProductionPlan
    {
        get
        {
            if (_world is not { } w || _plan is null) { return []; }
            var population = ProductionPopulation(w);
            var reserved = EssentialSlots(w);
            return _plan.Production.Select(goal =>
            {
                var def = ResolveGoal(w, goal);
                var desired = Desired(goal, w);
                var count = def is null ? 0 : CountGoal(w, goal, def);
                var producer = def is not null && HasOperationalProducer(w, def);
                var blocked = desired > count && def is not null && population + PopulationCost(def) > _rules.ArmyCap - (goal.Essential ? 0 : reserved);
                var status = w.Tick < goal.AfterTick ? "delayed" : def is null ? "unavailable-definition" : count >= desired ? "satisfied" :
                    !producer ? "producer-or-station-unavailable" : blocked ? "population-blocked" : "deficit";
                return new AiProductionGoalView(goal.Unit, def?.Name, goal.ArchetypeFamily, goal.RequiredRoles,
                    desired, count, goal.Essential, producer, blocked, status);
            }).ToArray();
        }
    }
}
