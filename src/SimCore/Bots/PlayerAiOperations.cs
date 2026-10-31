using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>The lifecycle of a coordinated group of forces.</summary>
public enum AiOperationPhase
{
    /// <summary>Gather forces at separate approaches.</summary>
    Staging,
    /// <summary>All assigned forces have been released toward the objective.</summary>
    Advancing,
    /// <summary>Expiry or loss of intelligence requires recovery.</summary>
    Withdrawing,
}

/// <summary>A detached view of a coordinated offensive.</summary>
public sealed record AiOperationView(uint Id, uint Subject, Cell Objective, AiOperationPhase Phase,
    uint ReleaseAt, uint Deadline, IReadOnlyList<uint> Forces, AiOperationControlMemory? Control = null);

public sealed partial class PlayerAi
{
    private readonly List<Operation> _operations = [];
    private uint _operationSerial;

    /// <summary>Current operations and their participating mission IDs.</summary>
    public IReadOnlyList<AiOperationView> Operations => _operations.Select(o => new AiOperationView(o.Id, o.Subject,
        o.Objective, o.Phase, o.ReleaseAt, o.Deadline, _missions.Where(m => !m.Done && m.Operation == o.Id).Select(m => m.Id).ToArray(), o.Control)).ToArray();

    private void UpdateOperations(World w)
    {
        if (_rules.Foundation is not { } p) { return; }
        foreach (var o in _operations)
        {
            var forces = _missions.Where(m => !m.Done && m.Operation == o.Id && m.Kind != AiMissionKind.Recover).ToArray();
            if (p.OperationControl is not null) { UpdateControlledOperation(w, o, forces); continue; }
            if (w.Tick >= o.Deadline || !_intel.Any(c => c.Id == o.Subject)) { o.Phase = AiOperationPhase.Withdrawing; }
            if (o.Phase == AiOperationPhase.Withdrawing)
            {
                foreach (var m in forces) { m.Operation = 0; Recover(w, m, "operation-withdraw"); }
            }
            else if (o.Phase == AiOperationPhase.Staging && (w.Tick >= o.ReleaseAt ||
                (forces.Length >= OperationForces && forces.All(m => AtStaging(m)))))
            {
                o.Phase = AiOperationPhase.Advancing;
                foreach (var m in forces)
                {
                    m.Objective = m.FinalObjective ?? o.Objective;
                    m.Phase = AiMissionPhase.Active;
                    m.ProgressAt = w.Tick;
                    m.BestDistance = long.MaxValue;
                    m.LastKind = 0;
                    m.NextOrder = w.Tick;
                }
                Trace(w, 0, "operation-release-" + o.Id);
            }
        }
        _operations.RemoveAll(o => !_missions.Any(m => !m.Done && m.Operation == o.Id && m.Kind != AiMissionKind.Recover));
    }

    private bool AtStaging(Mission m) => m.Members.All(id => Own(id) is { } u && Near(Cell.Of(u.Pos), m.Objective, _rules.ArrivalCells));

    private void JoinOperation(World w, Mission m)
    {
        if (_rules.Foundation is not { } p || m.Kind != AiMissionKind.Assault) { return; }
        Operation? operation = null;
        foreach (var o in _operations)
        {
            if (o.Phase != AiOperationPhase.Staging || w.Tick >= JoinUntil(o) ||
                _missions.Count(f => !f.Done && f.Operation == o.Id) >= OperationCapacity(w, o) ||
                !MissionReachable(w, m, o.Objective)) { continue; }
            operation = o; break;
        }
        if (operation is null && _operations.Count < p.MaxOperations)
        {
            foreach (var objective in _objectives)
            {
                if (objective.Subject == 0 || objective.Kind == AiThreatKind.Air ||
                    !MissionReachable(w, m, objective.Cell)) { continue; }
                operation = new Operation(++_operationSerial, objective.Subject, objective.Cell,
                    w.Tick + (uint)GatherTicks(OperationGather, p.OperationTicks, _operationSerial), w.Tick + (uint)p.OperationTicks);
                if (p.OperationControl is not null) { operation.Control = NewOperationControl(operation); }
                _operations.Add(operation);
                Trace(w, m.Id, "operation-created-" + operation.Id);
                break;
            }
        }
        if (operation is null)
        { if (p.OperationControl is not null) { StrategicRecover(w, m, "operation-capacity-unavailable"); } return; }
        var flank = _missions.Count(f => !f.Done && f.Operation == operation.Id);
        m.FinalObjective = operation.Objective;
        m.Contact = _intel.Any(c => c.Id == operation.Subject && c.Structure) ? operation.Subject : 0;
        m.Operation = operation.Id;
        m.Approach = flank;
        m.Phase = AiMissionPhase.Staging;
        var approach = PickApproach(w, m, operation.Objective, flank);
        if (approach is null) { Recover(w, m, "operation-no-safe-approach"); return; }
        m.Objective = approach.Value;
        m.Deadline = Math.Max(m.Deadline, operation.Deadline);
        Trace(w, m.Id, "operation-stage");
    }

    private Cell? PickApproach(World w, Mission m, Cell target, int lane)
    {
        var p = _rules.Foundation!;
        long dx = target.X - Home.X, dy = target.Y - Home.Y;
        var length = Math.Max(1, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        var side = (lane % 2 == 0 ? -1 : 1) * (lane / 2 + 1) * FlankDirection;
        var flankCells = Math.Max(1, FlankDistance + ApproachOffset(m.Id));
        var wanted = Clamp(w, new Cell(target.X - (int)(dx * StageDistance / length) - (int)(dy * flankCells * side / length),
            target.Y - (int)(dy * StageDistance / length) + (int)(dx * flankCells * side / length)));
        Cell? best = null;
        long bestScore = long.MaxValue;
        for (var i = 0; i < p.ApproachChecks; i++)
        {
            var ring = i / 8 + 1;
            var step = i % 8;
            ReadOnlySpan<int> xs = [0, 1, 1, 1, 0, -1, -1, -1];
            ReadOnlySpan<int> ys = [-1, -1, 0, 1, 1, 1, 0, -1];
            var cell = i == 0 ? wanted : Clamp(w, new Cell(wanted.X + xs[step] * ring * _rules.ArrivalCells, wanted.Y + ys[step] * ring * _rules.ArrivalCells));
            if (!ResolveApproach(w, m, cell, out var at)) { continue; }
            if (p.OperationControl is { } control && Danger(w, at, false) * 100 >
                Math.Max(1, m.Members.Sum(id => (long)Strength(Own(id)!))) * control.ApproachDangerPercent) { continue; }
            var score = ApproachCost(w, m, at, wanted);
            if (score < bestScore || score == bestScore && best is { } previous &&
                (at.Y < previous.Y || at.Y == previous.Y && at.X < previous.X)) { bestScore = score; best = at; }
        }
        return best ?? (p.OperationControl is null ? Cell.Of(Own(m.Members[0])!.Pos) : null);
    }

    private static Cell Clamp(World w, Cell c) => new(Math.Clamp(c.X, 0, w.Grid.Width - 1), Math.Clamp(c.Y, 0, w.Grid.Height - 1));

    private sealed class Operation(uint id, uint subject, Cell objective, uint releaseAt, uint deadline)
    {
        public readonly uint Id = id, Subject = subject, ReleaseAt = releaseAt, Deadline = deadline;
        public Cell Objective = objective;
        public AiOperationControlMemory? Control;
        public AiOperationPhase Phase = AiOperationPhase.Staging;
    }
}
