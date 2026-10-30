using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private void Reconcile(World w)
    {
        foreach (var m in _missions)
        {
            m.Members.RemoveAll(id => Own(id) is not { } u || !Mobile(u) && !(m.Tunnel is not null && u.InTunnel));
            if (m.Done || m.Members.Count == 0) { Complete(w, m, "force-lost"); }
        }
        _missions.RemoveAll(m => m.Done);
    }

    private Mission NewMission(World w, AiMissionKind kind)
    {
        var m = new Mission(++_serial, kind, w.Tick)
        { Objective = Home, Deadline = w.Tick + (uint)AssemblyTicks(w, _serial), ProgressAt = w.Tick };
        _missions.Add(m);
        Trace(w, m.Id, "mission-created");
        return m;
    }

    private void CreateMissions(World w)
    {
        ReinforceForces(w);
        if (Directive is { } directive) { CreateStrategicMissions(w, directive); return; }
        foreach (var m in _missions)
        {
            if (m.Phase == AiMissionPhase.Assemble) { Recruit(w, m, ForceCapacity); }
        }
        var available = _own.Count(Ready);
        var defenders = _missions.FirstOrDefault(m => m.Kind == AiMissionKind.Defend);
        var combatants = _own.Count(Fighter);
        if (defenders is not null && combatants <= defenders.Members.Count)
        {
            defenders.Kind = AiMissionKind.Assault;
            defenders.Phase = AiMissionPhase.Assemble;
            defenders.Deadline = w.Tick; // the last defenders must not wait at home forever
            Trace(w, defenders.Id, "last-defenders-release");
        }
        else if (defenders is null && available > _rules.HomeGuards && _rules.HomeGuards > 0 && _missions.Count < _rules.MaxMissions)
        {
            var m = NewMission(w, AiMissionKind.Defend);
            Recruit(w, m, _rules.HomeGuards);
            m.Phase = AiMissionPhase.Active;
            if (m.Members.Count == 0) { m.Done = true; }
        }
        // One independent scout; reserve it before assembling another attack force.
        if (_missions.Count < _rules.MaxMissions && _missions.Count(m => m.Kind == AiMissionKind.Scout) < ScoutLimit && HasUnseenProbe(w))
        {
            Unit? scout = null;
            foreach (var u in _own)
            {
                if (!ScoutReady(u) || u.Squad != 0) { continue; }
                if (scout is null || u.Def.Speed.Raw > scout.Def.Speed.Raw ||
                    (u.Def.Speed.Raw == scout.Def.Speed.Raw && u.Def.Vision.Raw > scout.Def.Vision.Raw)) { scout = u; }
            }
            if (scout is not null)
            {
                var m = NewMission(w, AiMissionKind.Scout);
                m.Members.Add(scout.Id);
                Launch(w, m);
            }
        }
        if (_missions.Count < _rules.MaxMissions && !_missions.Any(m => m.Kind == AiMissionKind.Assault && m.Phase == AiMissionPhase.Assemble) && _own.Any(Ready))
        {
            var m = NewMission(w, AiMissionKind.Assault);
            Recruit(w, m, ForceCapacity);
            if (m.Members.Count == 0) { m.Done = true; }
        }
    }

    private void Recruit(World w, Mission m, int capacity)
    {
        var previousCount = m.Members.Count;
        if (_rules.Foundation is { } policy && m.Kind == AiMissionKind.Assault)
        {
            foreach (var need in Composition)
            {
                foreach (var u in _own)
                {
                    if (m.Members.Count >= capacity || m.Members.Count(id => Own(id) is { } member && (RolesOf(member.Def) & need.Role) != 0) >= need.Minimum) { break; }
                    if (!Ready(u) || u.Squad != 0 || (RolesOf(u.Def) & need.Role) == 0) { continue; }
                    var at = m.Members.BinarySearch(u.Id); m.Members.Insert(~at, u.Id);
                }
            }
        }
        foreach (var u in _own.OrderByDescending(u => DoctrinePreference(u.Def)).ThenBy(u => u.Id))
        {
            if (m.Members.Count >= capacity) { break; }
            if (!Ready(u)) { continue; }
            if (u.Squad == 0)
            {
                var at = m.Members.BinarySearch(u.Id);
                m.Members.Insert(~at, u.Id);
            }
            else if (w.Squad(u.Squad) is { } s && s.Leader == u.Id && (s.Members.Count <= capacity - m.Members.Count || m.Kind == AiMissionKind.Assault && m.Members.Count == 0 && s.Members.Count <= 64) &&
                     s.Members.All(id => Own(id) is { } member && Ready(member)))
            {
                foreach (var id in s.Members)
                { var at = m.Members.BinarySearch(id); m.Members.Insert(~at, id); }
            }
        }
        if (m.Members.Count != previousCount) { m.LastKind = 0; }
    }

    private void UpdateMission(World w, Mission m)
    {
        if (m.Done || m.Members.Count == 0) { return; }
        if (m.Tunnel is not null) { UpdateTunnel(w, m); return; }
        if (m.Kind == AiMissionKind.Defend)
        {
            var intruder = Intruder();
            m.Objective = intruder is null ? Home : Cell.Of(intruder.Pos);
            if (!TacticalEngage(w, m)) { OrderMission(w, m, intruder is null ? CommandKind.Guard : CommandKind.AttackMove, "defend-home"); }
            return;
        }
        if (m.Phase == AiMissionPhase.Assemble)
        {
            if (m.Members.Count >= ForceMinimum && (CompositionReady(m, false) ||
                _rules.Foundation?.ForceManagement is not null && m.Members.Count >= ForceCapacity && CompositionReady(m, true)))
            { Launch(w, m); }
            else if (w.Tick >= m.Deadline)
            {
                if (CompositionReady(m, true)) { Launch(w, m); }
                else { Recover(w, m, "essential-role-missing"); }
            }
            else { OrderMission(w, m, CommandKind.Guard, "assemble"); }
            return;
        }
        if (m.Phase == AiMissionPhase.Staging)
        {
            if (!_operations.Any(o => o.Id == m.Operation)) { Recover(w, m, "operation-unavailable"); }
            else if (TryTunnel(w, m)) { return; }
            else { OrderMission(w, m, AtStaging(m) ? CommandKind.Guard : CommandKind.AttackMove, "stage-force"); }
            return;
        }
        var lead = Own(m.Members[0])!;
        var arrived = m.Members.All(id => Own(id) is { } u && Near(Cell.Of(u.Pos), m.Objective, _rules.ArrivalCells));
        if (m.Phase == AiMissionPhase.Returning)
        {
            if (arrived || w.Tick >= m.Deadline) { Complete(w, m, arrived ? "recovered" : "recovery-expired"); }
            else if (!TryTunnel(w, m)) { OrderMission(w, m, CommandKind.Move, "return-to-safety"); }
            return;
        }
        long hp = 0, max = 0;
        foreach (var id in m.Members) { var u = Own(id)!; hp += u.HP; max += u.Def.MaxHp; }
        if (hp * 100 < max * RetreatHp || m.Members.Count * 100 < m.LaunchMembers * _rules.RetreatMembersPercent ||
            m.Members.All(id => !Armed(Own(id)!)) && !(m.Kind == AiMissionKind.Scout &&
                _rules.Foundation?.ForceManagement is { UnarmedScouts: true } && m.Members.All(id => Own(id)!.Def.Weapons.Count == 0)))
        { Recover(w, m, "force-depleted"); return; }
        if (m.Contact != 0 && !_memory.Any(c => c.Id == m.Contact))
        { Complete(w, m, "objective-observed-empty"); return; }
        if (m.Kind == AiMissionKind.Scout && Sees(w, m.Objective))
        { Complete(w, m, "probe-observed"); return; }
        var fighting = m.Members.Any(id => Own(id) is { } u && _visible.Any(e => e.Id == u.AutoTarget || e.Id == u.AiTarget));
        var distance = Distance(Cell.Of(lead.Pos), m.Objective);
        var d = Fixed.Sqrt(distance);
        if (m.BestDistance == long.MaxValue || d + (long)_rules.ProgressCells * Fixed.OneRaw <= m.BestDistance)
        { m.BestDistance = d; m.ProgressAt = w.Tick; }
        if (fighting) { m.ProgressAt = w.Tick; }
        if (w.Tick >= m.Deadline || w.Tick >= m.ProgressAt + (uint)StallTicks)
        { Recover(w, m, w.Tick >= m.Deadline ? "mission-expired" : "no-progress"); return; }
        if (arrived && !fighting && m.Contact == 0)
        { Complete(w, m, "destination-reached"); return; }
        if (TryTunnel(w, m)) { return; }
        Formation(w, m);
        if (TacticalEngage(w, m) || Regroup(w, m, fighting)) { return; }
        // Passive structures require explicit attacks; only currently visible, weapon-compatible targets qualify.
        Unit? target = null;
        foreach (var e in _visible)
        {
            if (!e.Def.IsStructure || !m.Members.Any(id => w.PickWeapon(Own(id)!, e, true) >= 0)) { continue; }
            if (target is null || Distance(Cell.Of(lead.Pos), Cell.Of(e.Pos)) < Distance(Cell.Of(lead.Pos), Cell.Of(target.Pos))) { target = e; }
        }
        if (target is not null) { OrderMission(w, m, CommandKind.Attack, "visible-structure", target.Id); }
        else { OrderMission(w, m, ScoutAdvanceKind(m), "advance"); }
    }

    private void Launch(World w, Mission m)
    {
        if (!ChooseObjective(w, m)) { Recover(w, m, "no-reachable-objective"); return; }
        m.Phase = AiMissionPhase.Active;
        m.Deadline = w.Tick + (uint)MissionTicks;
        m.ProgressAt = w.Tick;
        m.LaunchMembers = m.Members.Count;
        m.BestDistance = long.MaxValue;
        m.FinalObjective = m.Objective;
        JoinOperation(w, m);
        Trace(w, m.Id, "mission-launched");
        if (_rules.FormSquads && m.Kind == AiMissionKind.Assault && w.Content.Squad is { } rules &&
            m.Members.Count >= rules.MinSize && m.Members.Count <= rules.MaxSize && m.Members.All(id => Own(id) is { Squad: 0 } u && w.CanJoinSquad(u)))
        {
            if (Emit(w, new Command { Kind = CommandKind.FormSquad, Units = m.Members }, m.Id, "form-mixed-force"))
            { m.NextOrder = w.Tick + World.InputDelay + 1; return; }
        }
        OrderMission(w, m, ScoutAdvanceKind(m), "launch");
    }

    private CommandKind ScoutAdvanceKind(Mission m) => m.Kind == AiMissionKind.Scout &&
        m.Members.All(id => Own(id)!.Def.Weapons.Count == 0) ? CommandKind.Move : CommandKind.AttackMove;

    private void OrderMission(World w, Mission m, CommandKind kind, string reason, uint target = 0, Cell? point = null)
    {
        if (w.Tick < m.NextOrder) { return; }
        var destination = point ?? m.Objective;
        if (m.LastKind == kind && m.LastCell == destination && m.LastTarget == target &&
            m.Members.Any(id => Own(id) is { } u && (u.Order.Kind != OrderKind.Idle || u.Ai != AiState.None))) { return; }
        if (Emit(w, new Command { Kind = kind, Units = m.Members, Cell = destination, Target = target }, m.Id, reason))
        {
            m.LastKind = kind; m.LastCell = destination; m.LastTarget = target;
            m.NextOrder = w.Tick + (uint)Math.Max(World.InputDelay + 1,
                _rules.Foundation?.Tactics is { } tactics && reason is "focus-visible-target" or "regroup-force" ? tactics.OrderTicks : 0);
        }
    }

    private void Formation(World w, Mission m)
    {
        if (w.Tick < m.NextOrder || Own(m.Members[0]) is not { Squad: not 0 } lead || w.Squad(lead.Squad) is not { } squad) { return; }
        var near = _visible.Any(e => e.Def.Layer == Layers.Ground && Near(Cell.Of(lead.Pos), Cell.Of(e.Pos),
            squad.Mode == SquadMode.Assault ? _rules.MarchCells : _rules.AssaultCells));
        var want = near ? SquadMode.Assault : SquadMode.March;
        if (squad.Mode != want)
        { Emit(w, new Command { Kind = CommandKind.SetSquadMode, Units = [lead.Id], Index = (int)want }, m.Id, "combat-formation", exclusive: false); }
    }

    private void Recover(World w, Mission m, string reason)
    {
        if (m.Probe >= 0) { _retry[m.Probe] = w.Tick + (uint)_rules.ProbeRetryTicks; }
        m.Probe = -1; m.Contact = 0;
        RecordOutcome(w, m, reason, false);
        m.Operation = 0; m.RegroupPoint = null; m.RegroupUntil = 0;
        AbortTunnel(w, m);
        m.Kind = AiMissionKind.Recover;
        m.Phase = AiMissionPhase.Returning;
        m.NextOrder = w.Tick; // recovery supersedes an earlier attack without waiting for its retry cooldown
        m.Deadline = w.Tick + (uint)_rules.RecoveryTicks;
        m.Objective = Home;
        var lead = m.Members.Count > 0 ? Own(m.Members[0]) : null;
        Unit? supply = null;
        foreach (var u in _own)
        {
            if (lead is null || !u.IsBuilt || u.Def.Supply is not { Storage: false } sup || (sup.Stock > 0 && u.Stock == 0) ||
                !m.Members.All(id => Reachable(w, Own(id)!, Cell.Of(u.Pos), out _))) { continue; }
            if (supply is null || Distance(Cell.Of(lead.Pos), Cell.Of(u.Pos)) < Distance(Cell.Of(lead.Pos), Cell.Of(supply.Pos))) { supply = u; }
        }
        if (supply is not null) { m.Objective = Cell.Of(supply.Pos); }
        Trace(w, m.Id, reason);
        if (m.Tunnel is null) { OrderMission(w, m, CommandKind.Move, "recover"); }
    }

    private void Complete(World w, Mission m, string reason)
    {
        if (m.Done) { return; }
        if (m.Tunnel is not null && m.Members.Count > 0) { AbortTunnel(w, m); return; }
        if (m.Members.Any(id => Own(id) is { } u && u.Order.Kind != OrderKind.Idle) &&
            !Emit(w, new Command { Kind = CommandKind.Stop, Units = m.Members }, m.Id, "release-force")) { return; }
        m.Done = true;
        RecordOutcome(w, m, reason, reason is "recovered" or "probe-observed" or "objective-observed-empty" or "destination-reached");
        foreach (var id in m.Members) { if (Own(id) is not null) { _rest.Add((id, w.Tick + (uint)_rules.RestTicks)); } }
        Trace(w, m.Id, reason);
    }

    private Unit? Intruder()
    {
        Unit? best = null;
        foreach (var e in _visible)
        {
            if (e.Def.Layer != Layers.Ground || !_own.Any(u => u.Def.IsStructure && Near(Cell.Of(u.Pos), Cell.Of(e.Pos), _rules.DefendCells))) { continue; }
            if (best is null || Distance(Home, Cell.Of(e.Pos)) < Distance(Home, Cell.Of(best.Pos))) { best = e; }
        }
        return best;
    }

    private bool HasUnseenProbe(World w) => Enumerable.Range(0, _probes.Length).Any(i => ProbeAvailable(w, i));
    private bool ProbeAvailable(World w, int i) => w.Tick >= _retry[i] &&
        (_seen[i] == 0 || w.Tick + 1 - _seen[i] >= _rules.RescoutTicks) && !_missions.Any(m => !m.Done && m.Probe == i);

    private bool ChooseObjective(World w, Mission m)
    {
        var lead = Own(m.Members[0])!;
        if (_rules.Foundation is not null && m.Kind != AiMissionKind.Scout)
        {
            foreach (var objective in _objectives)
            {
                if (objective.Subject == 0 || objective.Kind == AiThreatKind.Air || !MissionReachable(w, m, objective.Cell)) { continue; }
                m.Objective = objective.Cell;
                m.Contact = _intel.Any(c => c.Id == objective.Subject && c.Structure) ? objective.Subject : 0;
                return true;
            }
        }
        if (m.Kind != AiMissionKind.Scout)
        {
            Contact? best = null;
            foreach (var c in _memory)
            {
                if (!m.Members.All(id => Reachable(w, Own(id)!, c.Cell, out _))) { continue; }
                if (best is null || Distance(Cell.Of(lead.Pos), c.Cell) < Distance(Cell.Of(lead.Pos), best.Value.Cell)) { best = c; }
            }
            if (best is { } remembered) { m.Objective = remembered.Cell; m.Contact = remembered.Id; return true; }
            foreach (var e in _visible)
            {
                if (e.Def.Layer == Layers.Ground && m.Members.All(id => Reachable(w, Own(id)!, Cell.Of(e.Pos), out _)))
                { m.Objective = Cell.Of(e.Pos); return true; }
            }
        }
        var probe = -1;
        var bestDistance = long.MaxValue;
        var destination = Home;
        for (var i = 0; i < _probes.Length; i++)
        {
            if (!ProbeAvailable(w, i)) { continue; }
            var distance = ScoutTravel(Cell.Of(lead.Pos), i, m.Id);
            if (probe >= 0 && (_seen[i] > _seen[probe] || (_seen[i] == _seen[probe] && distance >= bestDistance))) { continue; }
            if (!Reachable(w, lead, _probes[i], out var to) || !m.Members.All(id => Reachable(w, Own(id)!, to, out _))) { continue; }
            probe = i; bestDistance = distance; destination = to;
        }
        if (probe < 0) { return false; }
        m.Probe = probe; m.Objective = destination;
        _probes[probe] = destination;
        return true;
    }

    private bool Reachable(World w, Unit u, Cell cell, out Cell destination)
    {
        destination = cell;
        var r = w.Routing;
        var k = r.ClassOf(u.Def);
        if (k < 0) { return false; }
        var start = r.NearestPassable(k, Cell.Of(u.Pos), _rules.ReachSearchCells);
        var end = r.NearestPassable(k, cell, _rules.ReachSearchCells);
        if (start < 0 || end < 0 || r.Component(k, start) == 0 || r.Component(k, start) != r.Component(k, end)) { return false; }
        destination = r.CellAt(end);
        return true;
    }
}
