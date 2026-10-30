using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Retained confirmation, diagnostic estimates and recovery reservation clocks.</summary>
public sealed record AiCombatMemory(uint NextAssessment, uint PressureSince, uint ActiveSince, uint NextRecovery,
    uint RecoveryTarget, long OwnPower, long EnemyPower, bool Complete);

public sealed partial class PlayerAi
{
    private static AiCombatMemory EmptyCombat(uint tick) => new(0, 0, tick, 0, 0, 0, 0, false);

    private bool LocalWithdrawal(World w, Mission m)
    {
        if (_rules.Foundation?.CombatControl is not { } p || m.Kind != AiMissionKind.Assault ||
            m.Phase != AiMissionPhase.Active || m.Tunnel is not null || m.Members.Count == 0) { return false; }
        var memory = m.Combat ?? EmptyCombat(w.Tick);
        if (w.Tick < memory.NextAssessment) { return false; }
        var lead = Own(m.Members[0])!;
        var enemies = _visible.Where(e => e.Def.Layer == Layers.Ground && e.CanAct && e.Inside == 0 && e.IsBuilt && !e.Rubble &&
                m.Members.Any(id => Near(Cell.Of(Own(id)!.Pos), Cell.Of(e.Pos), p.LocalCells)))
            .OrderBy(e => Distance(Cell.Of(lead.Pos), Cell.Of(e.Pos))).ThenBy(e => e.Id).Take(p.LocalUnits + 1).ToArray();
        var friends = _own.Where(u => u.CanAct && u.Inside == 0 && u.IsBuilt && !u.Rubble && u.Def.Layer == Layers.Ground &&
                u.Def.Archetype.Flight is null &&
                (m.Members.BinarySearch(u.Id) >= 0 || m.Members.Any(id => Near(Cell.Of(Own(id)!.Pos), Cell.Of(u.Pos), p.LocalCells))))
            .OrderBy(u => m.Members.BinarySearch(u.Id) >= 0 ? 0 : 1)
            .ThenBy(u => Distance(Cell.Of(lead.Pos), Cell.Of(u.Pos))).ThenBy(u => u.Id).Take(p.LocalUnits + 1).ToArray();
        // Never withdraw based on a partial estimate whose omitted participants could reverse the conclusion.
        var complete = enemies.Length <= p.LocalUnits && friends.Length <= p.LocalUnits &&
            2 * enemies.Length * friends.Length <= p.PairChecks;
        long own = 0, enemy = 0;
        if (complete && enemies.Length > 0)
        {
            foreach (var u in friends)
            { own += MatchupPower(w, u, enemies) * (m.Members.BinarySearch(u.Id) >= 0 ? 100 : p.SupportPercent) / 100; }
            foreach (var e in enemies) { enemy += MatchupPower(w, e, friends); }
        }
        var pressured = complete && enemy > 0 && enemy * 100 > Math.Max(1, own) * p.RetreatRatioPercent;
        var since = pressured ? memory.PressureSince == 0 ? w.Tick + 1 : memory.PressureSince : 0;
        m.Combat = memory with { NextAssessment = w.Tick + (uint)p.AssessmentTicks, PressureSince = since,
            OwnPower = own, EnemyPower = enemy, Complete = complete };
        if (!pressured || w.Tick < memory.ActiveSince + (uint)p.CommitTicks || w.Tick + 1 < since + (uint)p.ConfirmTicks) { return false; }
        Recover(w, m, "local-force-outmatched");
        return true;
    }

    // Potential armor-adjusted fire, not a combat simulation: one best legal stocked weapon per participant.
    private static long MatchupPower(World w, Unit attacker, IReadOnlyList<Unit> targets)
    {
        long best = 0;
        foreach (var target in targets)
        {
            var slot = w.PickWeapon(attacker, target, false);
            if (slot >= 0) { best = Math.Max(best, Math.Min(1_000_000, attacker.Def.Weapons[slot].Score(target.Def.Armor) / 1000)); }
        }
        return best * Math.Max(0, attacker.HP) / Math.Max(1, attacker.Def.MaxHp);
    }

    // Soft pressure sharing preserves a legal fallback when every available target is already committed.
    private long TargetCommitmentPenalty(World w, uint mission, Unit target, uint specialist = 0)
    {
        if (_rules.Foundation?.CombatControl is not { } p) { return 0; }
        long committed = 0;
        foreach (var force in _missions)
        {
            if (force.Done || force.Id == mission || force.Tunnel is not null || force.Phase != AiMissionPhase.Active ||
                force.LastKind != CommandKind.Attack || force.LastTarget != target.Id) { continue; }
            foreach (var id in force.Members)
            {
                if (Own(id) is not { Inside: 0 } u || !Near(Cell.Of(u.Pos), Cell.Of(target.Pos), p.LocalCells) ||
                    w.PickWeapon(u, target, true) < 0) { continue; }
                committed += Strength(u);
            }
        }
        foreach (var job in _specialists)
        {
            if (job.Done || job.Id == specialist || job.Target != target.Id ||
                job.Kind is not (AiSpecialistKind.AirStrike or AiSpecialistKind.Artillery) || Own(job.Actor) is not { } actor ||
                !Near(Cell.Of(actor.Pos), Cell.Of(target.Pos), p.LocalCells) || w.PickWeapon(actor, target, true) < 0) { continue; }
            committed += Strength(actor);
        }
        var capacity = Math.Max(1, Strength(target)) * (long)p.SaturationPercent / 100;
        if (target.Def.Weapons.Count == 0) { capacity = Math.Max(1, (long)target.HP * World.TicksPerSecond / Math.Max(1, p.ConfirmTicks)); }
        return committed <= capacity ? 0 : Math.Min(p.SaturationPenalty,
            (committed - capacity) * p.SaturationPenalty / Math.Max(1, capacity));
    }
}
