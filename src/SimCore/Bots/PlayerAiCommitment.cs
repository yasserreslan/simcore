using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Retained readiness/progress clocks and bounded objective tracking for a shared offensive.</summary>
public sealed record AiOperationControlMemory(uint NextReview, uint GatherUntil, uint CommittedAt, uint ProgressAt,
    long BestDistance, int PeakMembers, int LastTargetHp, int Retargets, string Reason, bool ObjectiveObservedEmpty = false);

public sealed partial class PlayerAi
{
    private static AiOperationControlMemory NewOperationControl(Operation o) =>
        new(0, o.ReleaseAt, 0, 0, long.MaxValue, 0, -1, 0, "gathering");

    private uint JoinUntil(Operation o) => o.Control?.GatherUntil ?? o.ReleaseAt;

    private int DesiredReadyForces => Math.Min(OperationForces, Math.Max(1, Directive?.AssaultForces ?? OperationForces));

    private int OperationCapacity(World w, Operation o)
    {
        if (_rules.Foundation?.OperationControl is not { } p) { return OperationForces; }
        var assigned = _missions.Where(m => !m.Done && m.Operation == o.Id && m.Kind == AiMissionKind.Assault)
            .Sum(m => m.Members.Sum(id => Own(id) is { } u ? (long)Strength(u) : 0));
        var needsMore = assigned * 100 < Danger(w, o.Objective, false) * p.ReleaseStrengthPercent;
        var wanted = DesiredReadyForces + (needsMore ? p.ExtraForces : 0);
        return Math.Min(_rules.MaxMissions, Math.Min(wanted, Math.Max(1, Directive?.AssaultForces ?? wanted)));
    }

    private int ReadyAtStaging(World w, Mission m)
    {
        if (m.Tunnel is not null) { return 0; }
        return m.Members.Count(id => Own(id) is { } u && StagedMember(m, u));
    }

    private bool StagedMember(Mission m, Unit u) => u.Inside == 0 && Mobile(u) && Armed(u) &&
        (long)u.HP * 100 >= (long)u.Def.MaxHp * ReadyHp && Near(Cell.Of(u.Pos), m.Objective, _rules.ArrivalCells);

    private bool ForceAtStaging(World w, Mission m, AiOperationRules p) => m.Members.Count > 0 && m.Tunnel is null &&
        ReadyAtStaging(w, m) * 100 >= m.Members.Count * p.ReadyPercent;

    private void UpdateControlledOperation(World w, Operation o, Mission[] forces)
    {
        var p = _rules.Foundation!.OperationControl!;
        var memory = o.Control ?? NewOperationControl(o);
        var contact = _intel.FirstOrDefault(c => c.Id == o.Subject);
        if (memory.ObjectiveObservedEmpty || contact is null && Sees(w, o.Objective) && !_visible.Any(u => u.Id == o.Subject))
        { CompleteObservedOperation(w, o, forces); return; }
        if (w.Tick >= o.Deadline || contact is null)
        { WithdrawOperation(w, o, forces, w.Tick >= o.Deadline ? "operation-expired" : "operation-intelligence-lost"); return; }
        if (o.Phase == AiOperationPhase.Withdrawing)
        { WithdrawOperation(w, o, forces, memory.Reason); return; }
        if (w.Tick < memory.NextReview) { return; }
        memory = memory with { NextReview = w.Tick + (uint)p.ReviewTicks };
        o.Control = memory;
        // A trip retains its destination/ownership until arrival or evacuation. Reconsider the latest observation afterwards.
        if (!forces.Any(m => m.Tunnel is not null) && !Near(o.Objective, contact.Cell, p.TargetShiftCells))
        {
            if (memory.Retargets >= p.MaxRetargets)
            { WithdrawOperation(w, o, forces, "operation-target-keeps-moving"); return; }
            if (forces.Any(m => !MissionReachable(w, m, contact.Cell)))
            { WithdrawOperation(w, o, forces, "operation-objective-access-lost"); return; }
            o.Objective = contact.Cell;
            memory = memory with { Retargets = memory.Retargets + 1, BestDistance = long.MaxValue, LastTargetHp = -1, Reason = "objective-observation-updated" };
            o.Control = memory;
            foreach (var m in forces)
            {
                m.FinalObjective = o.Objective;
                if (o.Phase == AiOperationPhase.Staging)
                {
                    var approach = PickApproach(w, m, o.Objective, m.Approach);
                    if (approach is null) { WithdrawOperation(w, o, forces, "operation-no-safe-restaging"); return; }
                    m.Objective = approach.Value;
                }
                else { m.Objective = o.Objective; }
                m.LastKind = 0; m.NextOrder = w.Tick;
            }
            Trace(w, 0, "operation-observed-target-shift");
        }
        if (o.Phase == AiOperationPhase.Staging)
        { ReviewStaging(w, o, forces, memory, p); return; }
        var members = forces.Sum(m => m.Members.Count);
        var peak = Math.Max(memory.PeakMembers, members);
        if (members * 100 < peak * p.WithdrawMembersPercent)
        { WithdrawOperation(w, o, forces, "operation-force-losses"); return; }
        if (forces.Any(m => m.Tunnel is { Stage: 0 or 2 } trip && w.Tick < trip.Until))
        { o.Control = memory with { PeakMembers = peak, ProgressAt = w.Tick, Reason = "bounded-force-transit" }; return; }
        var distance = OperationDistance(o, forces);
        var target = _visible.FirstOrDefault(u => u.Id == o.Subject);
        var targetHp = target?.HP ?? memory.LastTargetHp;
        var moved = distance != long.MaxValue && (memory.BestDistance == long.MaxValue || distance + (long)p.ProgressCells * Fixed.OneRaw <= memory.BestDistance);
        var damaged = target is not null && memory.LastTargetHp >= 0 && target.HP < memory.LastTargetHp;
        memory = memory with { PeakMembers = peak, BestDistance = moved ? distance : memory.BestDistance,
            ProgressAt = moved || damaged ? w.Tick : memory.ProgressAt, LastTargetHp = targetHp,
            Reason = moved ? "advancing" : damaged ? "objective-damaged" : "holding-commitment" };
        o.Control = memory;
        if (w.Tick >= memory.ProgressAt + (uint)p.StallTicks)
        { WithdrawOperation(w, o, forces, "operation-no-shared-progress"); }
    }

    private void ReviewStaging(World w, Operation o, Mission[] forces, AiOperationControlMemory memory, AiOperationRules p)
    {
        if (forces.Any(m => m.Tunnel is null && Danger(w, m.Objective, false) * 100 >
            Math.Max(1, m.Members.Sum(id => (long)Strength(Own(id)!))) * p.ApproachDangerPercent))
        { WithdrawOperation(w, o, forces, "operation-staging-compromised"); return; }
        var ready = forces.Where(m => ForceAtStaging(w, m, p)).ToArray();
        var strength = ready.Sum(m => m.Members.Sum(id => Own(id) is { } u && StagedMember(m, u) ? (long)Strength(u) : 0));
        var viable = ready.Length > 0 && strength > 0 && strength * 100 >= Danger(w, o.Objective, false) * p.ReleaseStrengthPercent;
        if (viable && ready.Length >= DesiredReadyForces)
        { CommitOperation(w, o, forces, ready, memory, "operation-ready-commit"); return; }
        if (w.Tick < memory.GatherUntil) { return; }
        var limit = Math.Min(o.Deadline, o.ReleaseAt + (uint)p.MaxGatherExtensionTicks);
        if (w.Tick < limit)
        {
            o.Control = memory with { GatherUntil = Math.Min(limit, w.Tick + (uint)p.GatherExtensionTicks), Reason = "waiting-for-ready-forces" };
            Trace(w, 0, "operation-gather-extended");
            return;
        }
        if (viable) { CommitOperation(w, o, forces, ready, memory, "operation-bounded-partial-commit"); }
        else { WithdrawOperation(w, o, forces, "operation-insufficient-ready-strength"); }
    }

    private void CommitOperation(World w, Operation o, Mission[] forces, Mission[] ready, AiOperationControlMemory memory, string reason)
    {
        foreach (var m in forces)
        {
            if (!ready.Contains(m)) { StrategicRecover(w, m, "operation-staging-missed"); continue; }
            m.Objective = m.FinalObjective ?? o.Objective; m.Phase = AiMissionPhase.Active;
            m.ProgressAt = w.Tick; m.BestDistance = long.MaxValue; m.Combat = null;
            m.LastKind = 0; m.NextOrder = w.Tick;
        }
        o.Phase = AiOperationPhase.Advancing;
        o.Control = memory with { CommittedAt = w.Tick, ProgressAt = w.Tick, BestDistance = OperationDistance(o, ready),
            PeakMembers = ready.Sum(m => m.Members.Count), LastTargetHp = _visible.FirstOrDefault(u => u.Id == o.Subject)?.HP ?? -1, Reason = reason };
        Trace(w, 0, reason);
    }

    private long OperationDistance(Operation o, IReadOnlyList<Mission> forces)
    {
        var best = long.MaxValue;
        foreach (var m in forces)
        foreach (var id in m.Members)
        { if (Own(id) is { Inside: 0 } u) { best = Math.Min(best, Fixed.Sqrt(Distance(Cell.Of(u.Pos), o.Objective))); } }
        return best;
    }

    private void CompleteObservedOperation(World w, Operation o, IReadOnlyList<Mission> forces)
    {
        o.Phase = AiOperationPhase.Withdrawing;
        o.Control = (o.Control ?? NewOperationControl(o)) with { Reason = "objective-observed-empty", ObjectiveObservedEmpty = true };
        foreach (var m in forces)
        {
            if (m.Tunnel is not null)
            {
                RecordOutcome(w, m, "objective-observed-empty", true);
                Recover(w, m, "operation-observed-empty-return");
            }
            else { Complete(w, m, "objective-observed-empty"); }
        }
    }

    private void WithdrawOperation(World w, Operation o, IReadOnlyList<Mission> forces, string reason)
    {
        o.Phase = AiOperationPhase.Withdrawing;
        o.Control = (o.Control ?? NewOperationControl(o)) with { Reason = reason };
        foreach (var m in forces) { Recover(w, m, reason); }
        Trace(w, 0, reason);
    }
}
