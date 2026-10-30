using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>A detached view of current strategic command permissions and force reservations.</summary>
public sealed record AiDirective(AiPosture Posture, int DesiredDefenders, int AssaultForces, int Scouts,
    bool DistantStrikes, bool Insertions, bool ForwardBuild, uint HoldUntil);

public sealed partial class PlayerAi
{
    private uint _postureUntil;

    private AiPostureRules? PosturePolicy => _rules.Foundation?.Postures.FirstOrDefault(p => p.Posture == _situation.Posture);

    /// <summary>Current policy allocation; null means the optional posture-control layer is disabled.</summary>
    public AiDirective? Directive
    {
        get
        {
            if (PosturePolicy is not { } p) { return null; }
            var combatants = _own.Count(Fighter);
            var guards = Math.Min(combatants, Math.Max(_rules.HomeGuards, (combatants * p.DefenderPercent + 99) / 100));
            return new AiDirective(p.Posture, guards, p.AssaultForces, Math.Min(_rules.ScoutMissions, p.Scouts),
                p.DistantStrikes, p.Insertions, p.ForwardBuild, _postureUntil);
        }
    }

    private int AssaultCount() => _missions.Count(m => !m.Done && m.Kind == AiMissionKind.Assault);
    private int DefenderCount() => _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Defend).Sum(m => m.Members.Count);

    // Runs ahead of ordinary executors so home defense receives commands and free units first.
    private void ApplyStrategicControl(World w)
    {
        if (Directive is not { } directive) { return; }
        var assaults = _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Assault)
            .OrderBy(m => m.Phase == AiMissionPhase.Active ? 0 : m.Phase == AiMissionPhase.Staging ? 1 : 2).ThenBy(m => m.Id).ToArray();
        for (var i = directive.AssaultForces; i < assaults.Length; i++)
        {
            var m = assaults[i];
            if (DefenderCount() < directive.DesiredDefenders && m.Members.All(id => Reachable(w, Own(id)!, Home, out _)))
            { SetDefense(w, m, "strategy-recall-defense"); }
            else { StrategicRecover(w, m, "strategy-recall-offense"); }
        }
        // Even an otherwise permitted offensive gives way when its units are needed for the home reservation.
        foreach (var m in assaults.Take(directive.AssaultForces).Reverse())
        {
            if (DefenderCount() + _own.Count(Ready) >= directive.DesiredDefenders) { break; }
            if (m.Kind == AiMissionKind.Assault && m.Members.All(id => Reachable(w, Own(id)!, Home, out _)))
            { SetDefense(w, m, "strategy-fill-defense-gap"); }
        }
        var scouts = _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Scout).ToArray();
        for (var i = directive.Scouts; i < scouts.Length; i++) { StrategicRecover(w, scouts[i], "strategy-recall-scout"); }

        // Move whole missions between roles; existing squads and their members remain together.
        foreach (var m in _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Defend).ToArray())
        {
            if (DefenderCount() - m.Members.Count < directive.DesiredDefenders) { continue; }
            if (AssaultCount() < directive.AssaultForces)
            {
                m.Kind = AiMissionKind.Assault; m.Phase = AiMissionPhase.Assemble;
                m.Contact = 0; m.Probe = -1; m.Operation = 0; m.FinalObjective = null;
                m.Objective = Home; m.Deadline = w.Tick + (uint)_rules.AssemblyTicks;
                m.NextOrder = w.Tick; m.LastKind = 0; m.OutcomeRecorded = false;
                Trace(w, m.Id, "strategy-release-defense");
            }
            else { Complete(w, m, "strategy-surplus-defense"); }
        }
        foreach (var m in _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Defend))
        { RecruitDefenders(w, m, directive.DesiredDefenders); }
        while (DefenderCount() < directive.DesiredDefenders && _missions.Count < _rules.MaxMissions && _own.Any(Ready))
        {
            var m = NewMission(w, AiMissionKind.Defend);
            RecruitDefenders(w, m, directive.DesiredDefenders);
            if (m.Members.Count == 0) { _missions.Remove(m); break; }
            SetDefense(w, m, "strategy-reserve-defense");
        }
        // Bypass rotating mission update latency for defense and immediate recalls.
        var intruder = Intruder();
        foreach (var m in _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Defend))
        {
            var point = intruder is null ? Home : Cell.Of(intruder.Pos);
            if (m.Objective != point) { m.Objective = point; m.NextOrder = w.Tick; }
            OrderMission(w, m, intruder is null ? CommandKind.Guard : CommandKind.AttackMove,
                intruder is null ? "strategy-home-reserve" : "strategy-home-emergency");
        }
    }

    private void RecruitDefenders(World w, Mission m, int desired)
    {
        foreach (var u in _own)
        {
            if (DefenderCount() >= desired || m.Members.Count >= _rules.ForceSize) { break; }
            if (!Ready(u)) { continue; }
            if (u.Squad == 0)
            { var at = m.Members.BinarySearch(u.Id); m.Members.Insert(~at, u.Id); }
            else if (w.Squad(u.Squad) is { } squad && squad.Leader == u.Id && squad.Members.Count <= _rules.ForceSize - m.Members.Count &&
                squad.Members.All(id => Own(id) is { } member && Ready(member)))
            {
                // A whole squad may exceed the requested reservation; never split it to hit a percentage exactly.
                foreach (var id in squad.Members) { var at = m.Members.BinarySearch(id); m.Members.Insert(~at, id); }
            }
        }
    }

    private void SetDefense(World w, Mission m, string reason)
    {
        m.Kind = AiMissionKind.Defend; m.Phase = AiMissionPhase.Active;
        m.Operation = 0; m.Probe = -1; m.Contact = 0; m.FinalObjective = null;
        m.Objective = Home; m.NextOrder = w.Tick; m.LastKind = 0; m.OutcomeRecorded = false;
        Trace(w, m.Id, reason);
        // The strategic pass orders every defender after allocations, so its command reflects a current intruder.
    }

    private void StrategicRecover(World w, Mission m, string reason)
    {
        m.OutcomeRecorded = true; // a deliberate strategic withdrawal must not penalize the enemy objective as a defeat
        Recover(w, m, reason);
    }

    private void CreateStrategicMissions(World w, AiDirective directive)
    {
        foreach (var m in _missions)
        { if (!m.Done && m.Kind == AiMissionKind.Assault && m.Phase == AiMissionPhase.Assemble) { Recruit(w, m, _rules.ForceSize); } }
        if (_missions.Count < _rules.MaxMissions && _missions.Count(m => !m.Done && m.Kind == AiMissionKind.Scout) < directive.Scouts && HasUnseenProbe(w))
        {
            var scout = _own.Where(u => Ready(u) && u.Squad == 0).OrderByDescending(u => u.Def.Speed.Raw)
                .ThenByDescending(u => u.Def.Vision.Raw).ThenBy(u => u.Id).FirstOrDefault();
            if (scout is not null)
            {
                var m = NewMission(w, AiMissionKind.Scout); m.Members.Add(scout.Id); Launch(w, m);
            }
        }
        if (_missions.Count < _rules.MaxMissions && AssaultCount() < directive.AssaultForces &&
            !_missions.Any(m => !m.Done && m.Kind == AiMissionKind.Assault && m.Phase == AiMissionPhase.Assemble) && _own.Any(Ready))
        {
            var m = NewMission(w, AiMissionKind.Assault); Recruit(w, m, _rules.ForceSize);
            if (m.Members.Count == 0) { _missions.Remove(m); }
        }
    }

    private bool OffensiveTarget(Cell point) => Directive is not { DistantStrikes: false } ||
        _own.Any(u => u.IsBuilt && !u.Rubble && u.Def.IsStructure && Near(Cell.Of(u.Pos), point, _rules.DefendCells));

    private Cell SupportAnchor(World w) => Directive is { AssaultForces: 0 } ? Home :
        Toward(w, Home, _situation.Front, _rules.Foundation!.StageCells);

    private long GoalPriority(World w, AiUnitGoal goal, UnitDef def)
    {
        if (PosturePolicy is not { } p) { return 1; }
        var roles = RolesOf(def);
        var weight = p.CombatPriority;
        if (def.Archetype.Production is not null || def.Builder is not null) { weight = p.InfrastructurePriority; }
        else if (def.IsStructure && def.Weapons.Count > 0) { weight = p.DefensePriority; }
        else if (def.Supply is not null || def.Cargo is not null || def.Archetype.Service is not null ||
                 (roles & AiRole.Repair) != 0) { weight = p.LogisticsPriority; }
        else if ((roles & AiRole.Transport) != 0)
        { weight = def.Weapons.Count > 0 ? Math.Max(p.CombatPriority, p.LogisticsPriority) : p.LogisticsPriority; }
        else if ((roles & AiRole.Radar) != 0 || def.Weapons.Count == 0 && (roles & AiRole.Scout) != 0) { weight = p.ReconPriority; }
        if (goal.Against != AiThreatKind.None && KnownThreats(goal.Against) > 0) { weight += p.CounterPriority; }
        return weight;
    }

    private int KnownThreats(AiThreatKind kind) => kind switch
    {
        AiThreatKind.Air => _situation.AirThreats, AiThreatKind.Armor => _situation.ArmorThreats,
        AiThreatKind.Structures => _situation.KnownStructures, AiThreatKind.Ground => _situation.GroundThreats, _ => 0,
    };
}
