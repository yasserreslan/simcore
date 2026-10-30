using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private static int SortieAmmo(Unit u)
    {
        var pools = new List<int>();
        foreach (var weapon in u.Def.Weapons)
        { if (weapon.Stock >= 0 && !pools.Contains(weapon.Stock)) { pools.Add(weapon.Stock); } }
        if (pools.Count == 0) { return 100; }
        return (int)(pools.Sum(pool => (long)u.Charges[pool] * 100 / Math.Max(1, u.StockMax(pool))) / pools.Count);
    }

    private bool SortieReady(World w, Unit u) => Doctrine?.Air is not { } p ||
        w.AirStatusOf(u).FuelPercent >= p.ReadyFuelPercent && SortieAmmo(u) >= p.ReadyAmmoPercent &&
        (long)u.HP * 100 >= (long)u.Def.MaxHp * p.ReadyHpPercent;

    private static bool AirAvailable(Unit u) => u.AirOps is { Phase: FlightPhase.Parked } || u.Heli is { Phase: HeliPhase.Landed } ||
        !u.Homing && u.AirOps is null;

    private bool StrikeSafe(World w, Unit u, Unit target)
    {
        if (Doctrine?.Air is not { } p) { return true; }
        if (!SortieReady(w, u) || Danger(w, Cell.Of(target.Pos), true) * 100 > Math.Max(1, (long)Strength(u)) * p.DangerPercent) { return false; }
        var others = _specialists.Where(j => !j.Done && j.Actor != u.Id).ToArray();
        if (others.Count(j => j.Kind == AiSpecialistKind.AirStrike && j.Target == target.Id) >= p.StrikesPerTarget) { return false; }
        return !p.RequireCover || _situation.AirThreats == 0 || (RolesOf(u.Def) & AiRole.Fighter) != 0 ||
            others.Any(j => j.Kind == AiSpecialistKind.AirCover && Own(j.Actor) is { } cover && !cover.Homing && !w.Grounded(cover) &&
                (Near(Cell.Of(cover.Pos), Cell.Of(u.Pos), _rules.Foundation!.AirAreaCells + StageDistance) ||
                    _operations.Any(o => o.Id == j.Operation && Near(o.Objective, Cell.Of(target.Pos), _rules.Foundation!.AirAreaCells + StageDistance))));
    }

    private SpecialistJob? AirDoctrineJob(World w, Unit u, AiRole role)
    {
        var p = Doctrine!.Air!;
        if (!SortieReady(w, u)) { return Job(w, AiSpecialistKind.Rearm, u, 0, Home); }
        var coverCount = _specialists.Count(j => !j.Done && j.Kind == AiSpecialistKind.AirCover);
        var operation = _operations.Where(o => o.Phase != AiOperationPhase.Withdrawing)
            .OrderBy(o => _specialists.Count(j => !j.Done && j.Kind == AiSpecialistKind.AirCover && j.Operation == o.Id)).ThenBy(o => o.Id).FirstOrDefault();
        var wantsCover = (role & AiRole.Fighter) != 0 && coverCount < p.CoverJobs &&
            ((role & AiRole.Strike) == 0 || _situation.AirThreats > 0 || operation is not null &&
                !_specialists.Any(j => !j.Done && j.Kind == AiSpecialistKind.AirCover && j.Operation == operation.Id));
        if (wantsCover)
        {
            var job = Job(w, AiSpecialistKind.AirCover, u, 0, operation is null ? SupportAnchor(w) : AirOperationPoint(w, operation.Id));
            job.Operation = operation?.Id ?? 0;
            return job;
        }
        if ((role & AiRole.Strike) != 0 && VisibleStrikeTarget(w, u) is { } target)
        {
            var job = Job(w, AiSpecialistKind.AirStrike, u, target.Id, Cell.Of(target.Pos));
            job.Operation = _operations.Where(o => o.Phase != AiOperationPhase.Withdrawing)
                .OrderBy(o => Distance(o.Objective, job.Point)).ThenBy(o => o.Id).FirstOrDefault()?.Id ?? 0;
            return job;
        }
        if (_specialists.Count(j => !j.Done && j.Kind == AiSpecialistKind.AirRecon) < p.ReconJobs &&
            u.Def.Cost <= p.ReconMaxCost)
        {
            var probe = AirProbe(w, u);
            if (probe >= 0)
            { var job = Job(w, AiSpecialistKind.AirRecon, u, 0, _probes[probe]); job.Probe = probe; return job; }
        }
        return null; // costly attackers wait for a suitable observed target instead of becoming expendable scouts
    }

    private Cell AirOperationPoint(World w, uint operation)
    {
        var members = _missions.Where(m => !m.Done && m.Operation == operation).SelectMany(m => m.Members)
            .Select(Own).Where(u => u is { Inside: 0 }).Select(u => u!).ToArray();
        return members.Length == 0 ? SupportAnchor(w) : Clamp(w, new Cell(
            (int)(members.Sum(u => (long)Cell.Of(u.Pos).X) / members.Length),
            (int)(members.Sum(u => (long)Cell.Of(u.Pos).Y) / members.Length)));
    }

    private bool UpdateAirDoctrine(World w, SpecialistJob j, Unit u)
    {
        if (Doctrine?.Air is not { } p || j.Kind is not (AiSpecialistKind.AirCover or AiSpecialistKind.AirStrike or AiSpecialistKind.AirRecon)) { return false; }
        if (j.Operation != 0 && !_operations.Any(o => o.Id == j.Operation && o.Phase != AiOperationPhase.Withdrawing))
        { CancelJob(w, j, "air-operation-ended"); return true; }
        if (!SortieReady(w, u))
        {
            j.Kind = AiSpecialistKind.Rearm; j.Operation = 0; j.LastKind = 0; j.NextOrder = w.Tick;
            Trace(w, 0, "air-sortie-service-needed");
            return false;
        }
        if (j.Kind == AiSpecialistKind.AirStrike && _visible.FirstOrDefault(e => e.Id == j.Target) is { } target && !StrikeSafe(w, u, target))
        { CancelJob(w, j, "air-strike-threat-changed"); return true; }
        if (j.Kind == AiSpecialistKind.AirCover && j.Operation != 0)
        {
            var point = AirOperationPoint(w, j.Operation);
            if (!Near(j.Point, point, p.CoverMoveCells)) { j.Point = point; j.LastKind = 0; j.NextOrder = w.Tick; }
        }
        return false;
    }
}
