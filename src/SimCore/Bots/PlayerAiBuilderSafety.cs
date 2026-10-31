using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private AiBuilderSafetyRules? BuilderSafety => _rules.Foundation?.BuildPlanning?.Safety;

    // Potential reach, deliberately conservative about walls/line of fire. All enemies came through Observe's
    // current visibility boundary; no remembered ID is resolved to learn its live position or weapon state.
    private bool BuilderThreatAt(Unit worker, Cell point)
    {
        if (BuilderSafety is not { } p) { return false; }
        foreach (var enemy in _visible.Where(e => e.CanAct && e.Inside == 0 && e.Garrison == 0 && e.IsBuilt && !e.Rubble && e.Def.Weapons.Count > 0)
            .OrderBy(e => Distance(Cell.Of(e.Pos), point)).ThenBy(e => e.Id).Take(p.ThreatCandidates))
        foreach (var weapon in enemy.Def.Weapons)
        {
            if ((enemy.WeaponMode != WeaponMode.PrimaryOnly || weapon.Index == 0) && weapon.Effect.Interceptor is null && weapon.Effect.Cruise is null &&
                World.CanHit(weapon, worker) && World.HasAmmo(enemy, weapon) && World.Within(World.From(enemy, point.Center()), point.Center(),
                    World.Reach(enemy, weapon) + Fixed.FromInt(p.RangeBufferCells))) { return true; }
        }
        return false;
    }

    private bool BuilderSiteSafe(World w, Unit worker, Cell point) => BuilderSafety is not { } p ||
        Danger(w, point, false) <= p.MaxDanger && !BuilderThreatAt(worker, point);

    private Cell? BuilderRefuge(World w, Unit worker)
    {
        var p = BuilderSafety!; var from = Cell.Of(worker.Pos);
        var points = new List<Cell> { Home, DefensePolicy is null ? Home : _defenseAnchor };
        points.AddRange(_own.Where(u => u.Inside == 0 && u.IsBuilt && !u.Rubble && u.Def.IsStructure)
            .OrderBy(u => Distance(from, Cell.Of(u.Pos))).ThenBy(u => u.Id).Take(p.RefugeCandidates).Select(u => Cell.Of(u.Pos)));
        for (var y = -1; y <= 1; y++)
        for (var x = -1; x <= 1; x++)
        { if (x != 0 || y != 0) { points.Add(Clamp(w, new Cell(from.X + x * p.ShelterCells, from.Y + y * p.ShelterCells))); } }
        var unique = new List<Cell>();
        foreach (var point in points) { if (!unique.Contains(point) && Sees(w, point)) { unique.Add(point); } }
        Cell? best = null; long bestExposure = long.MaxValue; long bestDistance = long.MaxValue;
        foreach (var wanted in unique.OrderBy(point => Danger(w, point, false)).ThenBy(point => Distance(from, point))
            .ThenBy(point => point.Y).ThenBy(point => point.X).Take(p.RefugeCandidates))
        {
            if (!Reachable(w, worker, wanted, out var point) || !Sees(w, point) || point == from || BuilderThreatAt(worker, point)) { continue; }
            long exposure = Danger(w, point, false);
            for (var sample = 1; sample <= p.RouteSamples; sample++)
            {
                var at = new Cell(from.X + (int)((long)(point.X - from.X) * sample / (p.RouteSamples + 1)),
                    from.Y + (int)((long)(point.Y - from.Y) * sample / (p.RouteSamples + 1)));
                exposure = Math.Max(exposure, Danger(w, at, false));
            }
            var distance = Distance(from, point);
            if (best is null || exposure < bestExposure || exposure == bestExposure && (distance < bestDistance ||
                distance == bestDistance && (point.Y < best.Value.Y || point.Y == best.Value.Y && point.X < best.Value.X)))
            { best = point; bestExposure = exposure; bestDistance = distance; }
        }
        return best;
    }

    private void ProtectBuilders(World w)
    {
        if (BuilderSafety is not { } p) { return; }
        var moves = 0;
        foreach (var worker in _own.Where(u => Mobile(u) && u.Def.Builder is not null && u.Squad == 0)
            .OrderBy(u => (long)u.HP * 100 / Math.Max(1, u.Def.MaxHp)).ThenBy(u => u.Id))
        {
            if (moves >= p.MovesPerDecision) { break; }
            if (Reserved(worker.Id) || Resting(worker.Id) || _ordered.BinarySearch(worker.Id) >= 0 ||
                !(Free(worker) || worker.Order.Kind == OrderKind.Build || _pending.Any(q => q.Source == worker.Id && q.Kind is CommandKind.Build or CommandKind.Resume)) ||
                !BuilderThreatAt(worker, Cell.Of(worker.Pos))) { continue; }
            var refuge = BuilderRefuge(w, worker);
            if (!Emit(w, new Command { Kind = refuge is null ? CommandKind.Stop : CommandKind.Move, Units = [worker.Id],
                Cell = refuge ?? Cell.Of(worker.Pos) }, 0, refuge is null ? "builder-no-safe-refuge-stop-work" : "builder-evacuate-visible-threat")) { continue; }
            // Keep the unfinished site; ordinary stalled-site scheduling can resume it after safety returns.
            // This cooldown also spans pending Build/Resume inputs superseded by the newer escape command.
            _rest.Add((worker.Id, w.Tick + (uint)Math.Max(p.CooldownTicks, World.InputDelay + 1)));
            moves++;
        }
    }
}
