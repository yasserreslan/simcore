using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private sealed record RecoveryChoice(Cell Point, uint Provider, long Cost, bool Safe);

    private bool HasAmmunition(Unit provider) => provider.IsBuilt && !provider.Rubble && provider.Def.Supply is { Storage: false } s &&
        (s.Stock == 0 || provider.Stock > 0);

    private static bool RepairsMembers(World w, Unit provider, IReadOnlyList<Unit> members) =>
        provider.IsBuilt && !provider.Rubble && (provider.Def.Repairs.Any(r => members.Any(u => u.HP < u.Def.MaxHp && u.Def.Tags.Matches(r.Affects))) ||
        w.ServiceWorks(provider) && provider.Def.Archetype.Service is { } service && members.Any(u => u.HP < u.Def.MaxHp && u.Def.Tags.Matches(service.Tags)));

    private long RecoveryCost(World w, Cell from, Cell point, bool air, Unit? provider, IReadOnlyList<Unit> members)
    {
        var p = _rules.Foundation!.CombatControl!;
        long exposure = Math.Max(Danger(w, point, air), Danger(w, point, false));
        for (var sample = 1; sample <= p.RouteSamples; sample++)
        {
            var at = new Cell(from.X + (int)((long)(point.X - from.X) * sample / (p.RouteSamples + 1)),
                from.Y + (int)((long)(point.Y - from.Y) * sample / (p.RouteSamples + 1)));
            exposure = Math.Max(exposure, Danger(w, at, air));
        }
        var score = exposure * p.DangerWeight + Fixed.Sqrt(Distance(from, point)) / Fixed.OneRaw * p.TravelWeight;
        if (provider is not null)
        {
            if (HasAmmunition(provider) && World.Within(point.Center(), provider.Pos, provider.Def.Supply!.Radius) && members.Any(u => u.Def.Weapons.Count > 0 && SortieAmmo(u) < p.RecoveryAmmoPercent)) { score -= p.SupplyBonus; }
            if (RepairsMembers(w, provider, members) && (w.ServiceWorks(provider) || provider.Def.Repairs.Any(r =>
                World.Within(point.Center(), provider.Pos, r.Radius) && members.Any(u => u.HP < u.Def.MaxHp && u.Def.Tags.Matches(r.Affects)))))
            { score -= p.RepairBonus; }
        }
        return score;
    }

    private RecoveryChoice ChooseRecovery(World w, IReadOnlyList<Unit> members, Cell from, bool air = false)
    {
        var p = _rules.Foundation!.CombatControl!;
        var strength = members.Sum(u => (long)Strength(u));
        var candidates = _own.Where(u => u.Inside == 0 && u.IsBuilt && !u.Rubble &&
                (HasAmmunition(u) || RepairsMembers(w, u, members) || u.Def.IsStructure && u.Def.Weapons.Count > 0))
            .OrderBy(u => RecoveryCost(w, from, Cell.Of(u.Pos), air, u, members)).ThenBy(u => u.Id).Take(p.RecoveryCandidates)
            .Select(u => (Cell.Of(u.Pos), u.Id)).ToList();
        candidates.Add((Home, 0)); candidates.Add((from, 0));
        RecoveryChoice? best = null;
        foreach (var (wanted, provider) in candidates)
        {
            var point = wanted;
            if (!air)
            {
                if (members.Count == 0 || !Reachable(w, members[0], point, out point) ||
                    !members.All(u => Reachable(w, u, point, out _))) { continue; }
            }
            if (air && !LandingPoint(w, members, point, out point)) { continue; }
            var safe = Math.Max(Danger(w, point, air), Danger(w, point, false)) * 100 <= Math.Max(1, strength) * p.RecoveryDangerPercent;
            var choice = new RecoveryChoice(point, provider, RecoveryCost(w, from, point, air, Own(provider), members), safe);
            if (best is null || choice.Safe && !best.Safe || choice.Safe == best.Safe &&
                (choice.Cost < best.Cost || choice.Cost == best.Cost && (choice.Provider < best.Provider || choice.Provider == best.Provider &&
                    (choice.Point.Y < best.Point.Y || choice.Point.Y == best.Point.Y && choice.Point.X < best.Point.X)))) { best = choice; }
        }
        // No shared terrain component: retain the current position rather than inventing a reachable destination.
        return best ?? new RecoveryChoice(from, 0, RecoveryCost(w, from, from, air, null, members), false);
    }

    private bool LandingPoint(World w, IReadOnlyList<Unit> members, Cell wanted, out Cell point)
    {
        point = wanted;
        if (members.Count == 0) { return false; }
        var r = w.Routing;
        var first = r.ClassOf(members[0].Def);
        var at = first < 0 ? -1 : r.NearestPassable(first, wanted, _rules.ReachSearchCells);
        if (at < 0) { return false; }
        point = r.CellAt(at);
        foreach (var u in members)
        {
            var k = r.ClassOf(u.Def);
            if (k < 0 || r.NearestPassable(k, point, 0) < 0) { return false; }
        }
        return true;
    }

    private void SelectRecovery(World w, Mission m, bool initial)
    {
        if (_rules.Foundation?.CombatControl is not { } p || m.Members.Count == 0 || m.Tunnel is not null) { return; }
        var memory = m.Combat ?? EmptyCombat(w.Tick);
        if (!initial && w.Tick < memory.NextRecovery) { return; }
        var members = m.Members.Select(id => Own(id)!).ToArray();
        var from = Cell.Of(members[0].Pos);
        var choice = ChooseRecovery(w, members, from);
        var current = Own(memory.RecoveryTarget);
        var invalid = memory.RecoveryTarget != 0 && (current is null || !HasAmmunition(current) && !RepairsMembers(w, current, members)) ||
            !members.All(u => Reachable(w, u, m.Objective, out _));
        var currentCost = RecoveryCost(w, from, m.Objective, false, current, members);
        var unsafeNow = Danger(w, m.Objective, false) * 100 > Math.Max(1, members.Sum(u => (long)Strength(u))) * p.RecoveryDangerPercent;
        if (initial || invalid || unsafeNow && choice.Safe || choice.Cost + p.RecoverySwitchMargin < currentCost)
        {
            if (m.Objective != choice.Point) { m.LastKind = 0; m.NextOrder = w.Tick; }
            m.Objective = choice.Point;
            memory = memory with { RecoveryTarget = choice.Provider };
            Trace(w, m.Id, choice.Safe ? "recovery-safe-destination" : "recovery-least-exposed-fallback");
        }
        // Replanning must not extend the mission's absolute recovery deadline or its progress timer.
        m.Combat = memory with { NextRecovery = w.Tick + (uint)p.RecoveryTicks, PressureSince = 0 };
    }

    private bool SupportSafe(World w, Unit actor, Cell point)
    {
        if (_rules.Foundation?.CombatControl is not { } p) { return true; }
        var strength = _own.Where(u => u.Inside == 0 && u.IsBuilt && !u.Rubble &&
            Near(Cell.Of(u.Pos), point, p.LocalCells)).Sum(u => (long)Strength(u));
        return Danger(w, point, actor.Def.Archetype.Flight is not null) * 100 <= Math.Max(1, strength) * p.SupportDangerPercent;
    }

    private Cell GroundSupportPoint(World w, Unit actor, uint operation)
    {
        var p = _rules.Foundation!.CombatControl!;
        var front = operation == 0 ? SupportAnchor(w) : AirOperationPoint(w, operation);
        var point = Toward(w, front, Home, p.SupportCells);
        if (Reachable(w, actor, point, out var actual) && SupportSafe(w, actor, actual)) { return actual; }
        var recovery = ChooseRecovery(w, [actor], Cell.Of(actor.Pos));
        return recovery.Point;
    }

    private void UpdateGroundSupport(World w, SpecialistJob j, Unit actor)
    {
        if (_rules.Foundation?.CombatControl is null || j.Kind != AiSpecialistKind.Radar || w.Tick < j.NextOrder) { return; }
        var operation = _operations.FirstOrDefault(o => o.Id == j.Operation && o.Phase != AiOperationPhase.Withdrawing) ??
            _operations.Where(o => o.Phase != AiOperationPhase.Withdrawing).OrderBy(o => Distance(Cell.Of(actor.Pos), o.Objective)).ThenBy(o => o.Id).FirstOrDefault();
        j.Operation = operation?.Id ?? 0;
        var point = GroundSupportPoint(w, actor, j.Operation);
        if (!Near(j.Point, point, _rules.ArrivalCells)) { j.Point = point; j.LastKind = 0; }
    }
}
