using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private bool DefenseTargetAllowed(Mission m, Unit enemy)
    {
        if (DefensePolicy is null)
        { return _own.Any(u => u.IsBuilt && !u.Rubble && u.Def.IsStructure && Near(Cell.Of(u.Pos), Cell.Of(enemy.Pos), _rules.DefendCells)); }
        var returnFire = m.Members.BinarySearch(enemy.AutoTarget) >= 0 || m.Members.BinarySearch(enemy.AiTarget) >= 0 || m.Members.BinarySearch(enemy.Order.Target) >= 0;
        if (returnFire) { return true; }
        var target = m.Defense?.Threat ?? 0;
        return _defenseThreats.Any(t => (target == 0 || Array.BinarySearch(t.Contacts, target) >= 0) && Array.BinarySearch(t.Contacts, enemy.Id) >= 0);
    }

    private long DefenseStrength(Mission m) => m.Members.Sum(id => Own(id) is { } u && Armed(u) ? (long)Strength(u) : 0);

    private void RecoverDepletedDefenders(World w)
    {
        if (DefensePolicy is null) { return; }
        foreach (var m in _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Defend && m.Tunnel is null && m.Members.Count > 0).ToArray())
        {
            var hp = m.Members.Sum(id => (long)Own(id)!.HP);
            var max = m.Members.Sum(id => (long)Own(id)!.Def.MaxHp);
            if (hp * 100 < max * RetreatHp || m.Members.All(id => !Armed(Own(id)!)))
            { StrategicRecover(w, m, "defense-force-needs-recovery"); }
        }
    }

    private bool DefenseTransferReady(Unit u) => Fighter(u) && Armed(u) && !Resting(u.Id) &&
        (long)u.HP * 100 >= (long)u.Def.MaxHp * ReadyHp && !_pending.Any(order => order.Source == u.Id);

    private void SpreadDefenseReserve(World w, AiDefenseRules p)
    {
        var target = Math.Min(p.Forces, _defenseThreats.Count);
        var transfers = 0;
        while (_missions.Count(m => !m.Done && m.Kind == AiMissionKind.Defend && m.Tunnel is null) < target && _missions.Count < _rules.MaxMissions)
        {
            Mission? source = null; uint[][] groups = [];
            var canTransfer = transfers < p.GroupTransfers && (_rules.Foundation?.ForceManagement is not { } policy || _forceTransfers < policy.GroupTransfers);
            foreach (var candidate in _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Defend && m.Tunnel is null && m.Members.Count > 1 && canTransfer)
                .OrderByDescending(m => m.Members.Count).ThenBy(m => m.Id))
            {
                groups = WholeGroups(w, candidate.Members).Where(g => g.Length < candidate.Members.Count &&
                        g.All(id => DefenseTransferReady(Own(id)!) && _ordered.BinarySearch(id) < 0))
                    .OrderByDescending(g => g.Length).ThenBy(g => g[0]).Take(p.CandidateGroups).ToArray();
                if (groups.Length > 0) { source = candidate; break; }
            }
            if (source is null)
            {
                if (!_own.Any(Ready)) { return; }
                var extra = NewMission(w, AiMissionKind.Defend);
                RecruitDefenders(w, extra, DefenderCount() + Math.Max(1, ForceMinimum));
                if (extra.Members.Count == 0) { _missions.Remove(extra); return; }
                SetDefense(w, extra, "defense-extra-threat-cover");
                continue;
            }
            Mission? recipient = null;
            var wanted = Math.Max(1, source.Members.Count / 2);
            foreach (var group in groups)
            {
                if (transfers >= p.GroupTransfers || _rules.Foundation?.ForceManagement is { } f && _forceTransfers >= f.GroupTransfers) { break; }
                if (source.Members.Count <= group.Length || group.Length > ForceCapacity - (recipient?.Members.Count ?? 0)) { continue; }
                if (recipient is null) { recipient = NewMission(w, AiMissionKind.Defend); SetDefense(w, recipient, "defense-reserve-distributed"); }
                foreach (var id in group) { source.Members.Remove(id); AddMember(recipient, id); }
                source.LastKind = 0; source.NextOrder = w.Tick; source.Defense = null;
                transfers++; _forceTransfers++;
                if (recipient.Members.Count >= wanted) { break; }
            }
            if (recipient is null || transfers >= p.GroupTransfers) { return; }
        }
    }

    private void PlanBaseDefense(World w)
    {
        var p = DefensePolicy!;
        SpreadDefenseReserve(w, p);
        var coverage = new long[_defenseThreats.Count];
        var guarded = new List<uint>();
        foreach (var m in _missions.Where(m => !m.Done && m.Kind == AiMissionKind.Defend && m.Tunnel is null && m.Members.Count > 0)
            .OrderByDescending(DefenseStrength).ThenBy(m => m.Id))
        {
            var best = -1; var bestScore = long.MinValue; var bestNeeds = false; Cell bestPoint = default;
            var locked = m.Defense is { } assignment && w.Tick < assignment.HoldUntil ?
                _defenseThreats.FindIndex(t => Array.BinarySearch(t.Contacts, assignment.Threat) >= 0) : -1;
            var held = -1; Cell heldPoint = default;
            for (var i = 0; i < _defenseThreats.Count; i++)
            {
                var t = _defenseThreats[i];
                var distance = Fixed.Sqrt(Distance(Cell.Of(Own(m.Members[0])!.Pos), t.Center)) / Fixed.OneRaw;
                if (distance > p.TravelCells || !Reachable(w, Own(m.Members[0])!, t.Center, out var point) ||
                    !m.Members.All(id => Reachable(w, Own(id)!, point, out _))) { continue; }
                long fire = 0;
                foreach (var id in m.Members)
                {
                    var u = Own(id)!; long power = 0;
                    foreach (var subject in t.Contacts)
                    {
                        var enemy = VisibleContact(subject);
                        if (enemy is null) { continue; }
                        var weapon = w.PickWeapon(u, enemy, false);
                        if (weapon >= 0) { power = Math.Max(power, Math.Min(1_000_000, u.Def.Weapons[weapon].Score(enemy.Def.Armor) / 1000)); }
                    }
                    fire += power;
                }
                if (fire <= 0) { continue; }
                var required = Math.Max(1, t.Strength * p.StrengthPercent / 100);
                var need = Math.Max(0, required - coverage[i]);
                var score = (t.Priority + Math.Min(t.Priority / 4, fire * p.WeaponWeight)) * need / required - distance * p.TravelWeight;
                if (i == locked) { held = i; heldPoint = point; }
                if (best < 0 || need > 0 && !bestNeeds || (need > 0) == bestNeeds &&
                    (score > bestScore || score == bestScore && t.Subject < _defenseThreats[best].Subject))
                { best = i; bestScore = score; bestNeeds = need > 0; bestPoint = point; }
            }
            if (held >= 0 && coverage[held] < Math.Max(1, _defenseThreats[held].Strength * p.StrengthPercent / 100) && (best < 0 || _defenseThreats[best].Priority * 100 <= _defenseThreats[held].Priority * p.OverridePercent))
            { best = held; bestPoint = heldPoint; }
            if (best >= 0)
            {
                var t = _defenseThreats[best]; coverage[best] += DefenseStrength(m);
                SetDefenseAssignment(w, m, t.Subject, t.Asset, bestPoint, p);
                if (!TacticalEngage(w, m)) { OrderMission(w, m, CommandKind.AttackMove, "defense-protect-threatened-asset"); }
            }
            else
            {
                var site = _defenseAssets.Select(Own).Where(u => u is not null).Select(u => u!)
                    .OrderBy(u => guarded.Contains(u.Id) ? 1 : 0).ThenByDescending(u => AssetValue(u, p)).ThenBy(u => Distance(Cell.Of(Own(m.Members[0])!.Pos), Cell.Of(u.Pos))).ThenBy(u => u.Id)
                    .FirstOrDefault(u => m.Members.All(id => Reachable(w, Own(id)!, Cell.Of(u.Pos), out _)));
                if (site is not null) { guarded.Add(site.Id); }
                var point = site is null ? _defenseAnchor : Cell.Of(site.Pos);
                if (Reachable(w, Own(m.Members[0])!, point, out var actual)) { point = actual; }
                SetDefenseAssignment(w, m, 0, site?.Id ?? 0, point, p);
                OrderMission(w, m, CommandKind.Guard, "defense-guard-core-asset");
            }
        }
    }

    private void SetDefenseAssignment(World w, Mission m, uint threat, uint asset, Cell point, AiDefenseRules p)
    {
        var same = m.Defense is { } old && old.Asset == asset && (old.Threat == threat || threat != 0 &&
            _defenseThreats.Any(t => t.Subject == threat && Array.BinarySearch(t.Contacts, old.Threat) >= 0));
        if (!same || m.Objective != point) { m.Objective = point; m.LastKind = 0; m.NextOrder = w.Tick; }
        if (!same) { m.Defense = new AiDefenseMemory(threat, asset, w.Tick + (uint)p.HoldTicks); Trace(w, m.Id, "defense-assignment-changed"); }
    }
}
