using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private uint _nextReinforcement;
    private int _forceTransfers, _attackReserve, _reinforcementReadyCount;

    private void ObserveForceGroups(World w)
    {
        _forceTransfers = 0; _attackReserve = 0;
        if (_rules.Foundation?.ForceManagement is not { PreserveAttackForce: true }) { return; }
        var fighters = _own.Where(Fighter).Select(u => u.Id).ToArray();
        var largest = WholeGroups(w, fighters).Select(g => g.Length).DefaultIfEmpty(0).Max();
        _attackReserve = Math.Min(fighters.Length, Math.Max(ForceMinimum, largest));
    }

    // Every yielded squad is complete in this ownership set. Incomplete/external squads remain untouched.
    private IEnumerable<uint[]> WholeGroups(World w, IReadOnlyList<uint> members)
    {
        foreach (var id in members)
        {
            if (Own(id) is not { } unit) { continue; }
            if (unit.Squad == 0) { yield return [id]; continue; }
            var squad = w.Squad(unit.Squad);
            if (squad is null || squad.Members.Count == 0 || id != squad.Members.Min() || !squad.Members.All(members.Contains)) { continue; }
            yield return squad.Members.OrderBy(member => member).ToArray();
        }
    }

    private void RebalanceDefenders(World w, AiDirective directive)
    {
        if (_rules.Foundation?.ForceManagement is not { } p || directive.AssaultForces == 0 || Intruder() is not null) { return; }
        foreach (var source in _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Defend && m.Tunnel is null).ToArray())
        foreach (var group in WholeGroups(w, source.Members.ToArray()))
        {
            if (_forceTransfers >= p.GroupTransfers) { return; }
            if (DefenderCount() - group.Length < directive.DesiredDefenders || group.Length > ForceCapacity ||
                !group.All(id => TransferReady(Own(id)!) && _ordered.BinarySearch(id) < 0 && Reachable(w, Own(id)!, Home, out _))) { continue; }
            var recipient = _missions.FirstOrDefault(m => !m.Done && m.Kind == AiMissionKind.Assault && m.Phase == AiMissionPhase.Assemble &&
                m.Members.Count + group.Length <= ForceCapacity);
            if (recipient is null)
            {
                if (AssaultCount() >= directive.AssaultForces || _missions.Count >= _rules.MaxMissions) { continue; }
                recipient = NewMission(w, AiMissionKind.Assault);
            }
            foreach (var id in group) { source.Members.Remove(id); AddMember(recipient, id); }
            source.LastKind = 0; source.NextOrder = w.Tick;
            recipient.LastKind = 0; recipient.NextOrder = w.Tick;
            _forceTransfers++;
            Trace(w, recipient.Id, "defense-group-reassigned");
            if (source.Members.Count == 0) { source.Done = true; source.OutcomeRecorded = true; }
        }
    }

    private bool TransferReady(Unit u) => Fighter(u) && Armed(u) && !Resting(u.Id) &&
        (long)u.HP * 100 >= (long)u.Def.MaxHp * ReadyHp &&
        !_pending.Any(p => p.Source == u.Id) && !_visible.Any(e => Near(Cell.Of(e.Pos), Cell.Of(u.Pos), _rules.AssaultCells));

    private void ReinforceForces(World w)
    {
        if (_rules.Foundation?.ForceManagement is not { } p) { return; }
        var ready = _own.Count(Ready);
        var newReady = ready > _reinforcementReadyCount;
        _reinforcementReadyCount = ready;
        if (w.Tick < _nextReinforcement && !newReady) { return; }
        _nextReinforcement = w.Tick + (uint)p.ReinforcementTicks;
        try
        {
            var forces = _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Assault &&
                    m.Phase is AiMissionPhase.Active or AiMissionPhase.Staging && m.Members.Count > 0 && m.Members.Count < ForceCapacity &&
                    m.RegroupPoint is null && m.Tunnel is null && w.Tick < m.Deadline &&
                    !m.Members.Any(id => _visible.Any(e => Near(Cell.Of(Own(id)!.Pos), Cell.Of(e.Pos), _rules.AssaultCells))))
                .OrderBy(m => CompositionReady(m, false) ? 1 : 0).ThenBy(m => m.Members.Count).ThenBy(m => m.Id).ToArray();
            foreach (var force in forces)
            {
                if (_forceTransfers >= p.GroupTransfers) { return; }
                var lead = Own(force.Members[0])!;
                var available = _own.Where(Ready).Select(u => u.Id).ToArray();
                var groups = WholeGroups(w, available)
                    .Where(g => g.Length <= ForceCapacity - force.Members.Count &&
                        g.All(id => Near(Cell.Of(Own(id)!.Pos), Cell.Of(lead.Pos), p.ReinforceCells)))
                    .OrderBy(g => Distance(Cell.Of(Own(g[0])!.Pos), Cell.Of(lead.Pos))).ThenBy(g => g[0]).Take(p.CandidateGroups);
                foreach (var group in groups)
                {
                    if (_forceTransfers >= p.GroupTransfers) { return; }
                    if (force.Members.Count + group.Length > ForceCapacity || !group.All(id => Ready(Own(id)!) && Reachable(w, Own(id)!, force.Objective, out _))) { continue; }
                    foreach (var id in group) { AddMember(force, id); }
                    force.LaunchMembers = Math.Max(force.LaunchMembers, force.Members.Count);
                    force.LastKind = 0; force.NextOrder = w.Tick;
                    _forceTransfers++;
                    Trace(w, force.Id, "local-force-reinforced");
                }
            }
        }
        finally { _reinforcementReadyCount = _own.Count(Ready); }
    }

    private static void AddMember(Mission mission, uint id)
    { var at = mission.Members.BinarySearch(id); if (at < 0) { mission.Members.Insert(~at, id); } }

    private bool ScoutReady(Unit u)
    {
        if (_rules.Foundation?.ForceManagement is not { UnarmedScouts: true }) { return Ready(u); }
        return Mobile(u) && Free(u) && u.Def.Layer == Layers.Ground && u.Def.Archetype.Flight is null && u.Def.Strike is null &&
            u.Def.Builder is null && u.Def.Cargo is null && u.Def.Archetype.DeployTicks == 0 && (Fighter(u) || u.Def.Weapons.Count == 0) &&
            !Reserved(u.Id) && !Resting(u.Id) && _ordered.BinarySearch(u.Id) < 0 && !_pending.Any(p => p.Source == u.Id) &&
            (long)u.HP * 100 >= (long)u.Def.MaxHp * ReadyHp && (u.Def.Weapons.Count == 0 || Armed(u));
    }
}
