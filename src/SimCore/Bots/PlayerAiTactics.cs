using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private bool TacticalEngage(World w, Mission m)
    {
        if (_rules.Foundation?.Tactics is not { } p || m.Kind is not (AiMissionKind.Assault or AiMissionKind.Defend) ||
            m.Phase != AiMissionPhase.Active || m.Members.Count == 0) { return false; }
        var lead = Own(m.Members[0])!;
        // Bound expensive weapon/line-of-fire checks; the observation scan itself still scales with visible units.
        var candidates = _visible.Where(e => e.Def.Layer == Layers.Ground &&
                m.Members.Any(id => Near(Cell.Of(Own(id)!.Pos), Cell.Of(e.Pos), p.EngageCells)) &&
                (m.Kind != AiMissionKind.Defend || _own.Any(u => u.IsBuilt && !u.Rubble && u.Def.IsStructure &&
                    Near(Cell.Of(u.Pos), Cell.Of(e.Pos), _rules.DefendCells))))
            .OrderBy(e => Distance(Cell.Of(lead.Pos), Cell.Of(e.Pos))).ThenBy(e => e.Id).Take(p.TargetCandidates);
        Unit? best = null;
        long bestScore = long.MinValue;
        foreach (var enemy in candidates)
        {
            var shooters = 0;
            long fire = 0;
            foreach (var id in m.Members)
            {
                var member = Own(id)!;
                var slot = w.PickWeapon(member, enemy, true);
                if (slot < 0) { continue; }
                shooters++;
                fire += Math.Min(1_000_000, Math.Max(0, member.Def.Weapons[slot].Score(enemy.Def.Armor) / 1000));
            }
            if (shooters * 100 < m.Members.Count * p.EngagePercent) { continue; }
            var threatening = m.Members.BinarySearch(enemy.AutoTarget) >= 0 || m.Members.BinarySearch(enemy.AiTarget) >= 0 ||
                m.Members.BinarySearch(enemy.Order.Target) >= 0;
            var score = fire * p.WeaponWeight + (threatening ? p.ReturnFireBonus : 0) +
                (enemy.Id == m.Contact ? p.ObjectiveBonus : 0) +
                (m.LastKind == CommandKind.Attack && m.LastTarget == enemy.Id ? p.FocusBonus : 0) +
                (100 - (long)Math.Max(0, enemy.HP) * 100 / Math.Max(1, enemy.Def.MaxHp)) * p.FinishWeight -
                Fixed.Sqrt(Distance(Cell.Of(lead.Pos), Cell.Of(enemy.Pos))) / Fixed.OneRaw * p.DistanceWeight - TargetCommitmentPenalty(w, m.Id, enemy);
            // Candidates already have stable ordering, but target score ties explicitly prefer the smaller ID.
            if (best is null || score > bestScore || score == bestScore && enemy.Id < best.Id)
            { best = enemy; bestScore = score; }
        }
        if (best is null) { return false; }
        if (m.RegroupPoint is not null)
        {
            EndRegroup(w, m, "regroup-interrupted-contact");
            m.NextOrder = w.Tick;
        }
        OrderMission(w, m, CommandKind.Attack, "focus-visible-target", best.Id);
        return true; // keep this tactical intent even when the command budget/retry clock defers emission
    }

    private bool Regroup(World w, Mission m, bool fighting)
    {
        if (_rules.Foundation?.Tactics is not { } p || m.Kind != AiMissionKind.Assault || m.Phase != AiMissionPhase.Active ||
            m.Members.Count < 2) { return false; }
        if (fighting || _visible.Any(e => m.Members.Any(id => Near(Cell.Of(Own(id)!.Pos), Cell.Of(e.Pos), p.EngageCells))))
        {
            if (m.RegroupPoint is not null) { EndRegroup(w, m, "regroup-interrupted-contact"); }
            return false;
        }
        if (m.RegroupPoint is { } rally)
        {
            if (w.Tick >= m.RegroupUntil || m.Members.All(id => Near(Cell.Of(Own(id)!.Pos), rally, _rules.ArrivalCells)))
            { EndRegroup(w, m, w.Tick >= m.RegroupUntil ? "regroup-expired" : "regroup-complete"); return false; }
            OrderMission(w, m, CommandKind.AttackMove, "regroup-force", point: rally);
            return true;
        }
        if (w.Tick < m.NextRegroup) { return false; }
        long x = 0, y = 0;
        foreach (var id in m.Members) { var at = Cell.Of(Own(id)!.Pos); x += at.X; y += at.Y; }
        var center = new Cell((int)(x / m.Members.Count), (int)(y / m.Members.Count));
        if (m.Members.All(id => Near(Cell.Of(Own(id)!.Pos), center, p.RegroupCells))) { return false; }
        // Prefer a real member position near the center, and verify all movement classes can reach it.
        foreach (var id in m.Members.OrderBy(id => Distance(Cell.Of(Own(id)!.Pos), center)).ThenBy(id => id).Take(p.RallyCandidates))
        {
            var at = Cell.Of(Own(id)!.Pos);
            if (!m.Members.All(member => Reachable(w, Own(member)!, at, out _))) { continue; }
            m.RegroupPoint = at; m.RegroupUntil = w.Tick + (uint)p.RegroupTicks;
            m.NextRegroup = m.RegroupUntil + (uint)p.RegroupRetryTicks;
            m.LastKind = 0; m.NextOrder = w.Tick;
            Trace(w, m.Id, "regroup-started");
            OrderMission(w, m, CommandKind.AttackMove, "regroup-force", point: at);
            return true;
        }
        m.NextRegroup = w.Tick + (uint)p.RegroupRetryTicks;
        Trace(w, m.Id, "regroup-no-common-rally");
        return false;
    }

    private void EndRegroup(World w, Mission m, string reason)
    {
        m.RegroupPoint = null; m.RegroupUntil = 0;
        m.NextRegroup = w.Tick + (uint)_rules.Foundation!.Tactics!.RegroupRetryTicks;
        m.LastKind = 0; m.NextOrder = w.Tick;
        Trace(w, m.Id, reason);
    }
}
