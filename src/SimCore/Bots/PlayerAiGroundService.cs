using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private AiServiceRules ServicePolicy => _rules.Foundation!.ServiceControl!;

    private bool GroundServiceDue(Unit u) => u.Def.Crew is not null && u.Def.Layer == Layers.Ground &&
        u.Def.Archetype.Flight is null && u.Def.Archetype.Aircraft is null && u.Def.Strike is null &&
        ((long)u.HP * 100 < (long)u.Def.MaxHp * ServicePolicy.HpPercent || u.Def.Weapons.Any(weapon =>
            (u.WeaponMode != WeaponMode.PrimaryOnly || weapon.Index == 0) && weapon.Stock >= 0 &&
            weapon.Effect.DepotReload is not { NoService: true } && u.StockMax(weapon.Stock) > 0 &&
            (long)u.Charges[weapon.Stock] * 100 < (long)u.StockMax(weapon.Stock) * ServicePolicy.AmmoPercent));

    private int GarageClaims(Unit garage, uint except) => _own.Count(u => u.Id != except &&
        (u.ServiceAt == garage.Id || _specialists.Any(j => !j.Done && j.Kind == AiSpecialistKind.Service && j.Actor == u.Id && j.Target == garage.Id)));

    // A stock-backed garage charges a complete reload before repair. Quote the same classes of work;
    // never count stock as reserved or assume an in-progress service has not already paid.
    private bool GarageCanHelp(World w, Unit garage, Unit actor)
    {
        var store = w.AirfieldOf(garage) is { } field ? w.AirfieldStore(field) : null;
        if (store is null) { return true; }
        var pools = new List<int>(); long reload = 0;
        foreach (var weapon in actor.Def.Weapons)
        {
            if (weapon.Stock < 0 || pools.Contains(weapon.Stock)) { continue; }
            pools.Add(weapon.Stock);
            if (weapon.Effect.DepotReload is { NoService: true }) { continue; }
            reload += (long)Math.Max(0, actor.StockMax(weapon.Stock) - actor.Charges[weapon.Stock]) * GarageRoundCost(actor, weapon.Stock, store);
        }
        foreach (var pool in new[] { actor.Def.SmokeStock, actor.Def.ApsStock })
        {
            if (pool >= 0) { reload += (long)Math.Max(0, actor.StockMax(pool) - actor.Charges[pool]) * GarageRoundCost(actor, pool, store); }
        }
        var ammoNeeded = actor.Def.Weapons.Any(weapon => weapon.Stock >= 0 && weapon.Effect.DepotReload is not { NoService: true } &&
            (actor.WeaponMode != WeaponMode.PrimaryOnly || weapon.Index == 0) &&
            (long)actor.Charges[weapon.Stock] * 100 < (long)actor.StockMax(weapon.Stock) * ServicePolicy.AmmoPercent);
        var reloadPaid = reload <= store.Stock;
        var repair = (long)(actor.Def.MaxHp - actor.HP) * store.Def.Supply!.RepairCost / 100;
        return ammoNeeded && reloadPaid || (long)actor.HP * 100 < (long)actor.Def.MaxHp * ServicePolicy.HpPercent &&
            repair <= store.Stock - (reloadPaid ? reload : 0);
    }

    private static int GarageRoundCost(Unit actor, int pool, Unit store) =>
        actor.Def.Weapons.FirstOrDefault(weapon => weapon.Stock == pool && weapon.Effect.DepotReload is not null)?.Effect.DepotReload?.Cost ?? store.Def.Supply!.RoundCost;

    private Unit? ChooseGarage(World w, Unit actor)
    {
        var p = ServicePolicy;
        var candidates = _own.Where(g => g.Inside == 0 && w.ServiceWorks(g) && g.Def.Archetype.Service is { } s && actor.Def.Tags.Matches(s.Tags) &&
                Near(Cell.Of(actor.Pos), Cell.Of(g.Pos), _rules.SupplyTripCells) && GarageClaims(g, actor.Id) < p.QueueLimit && GarageCanHelp(w, g, actor))
            .OrderBy(g => Distance(Cell.Of(actor.Pos), Cell.Of(g.Pos))).ThenBy(g => g.Id).Take(p.Candidates);
        Unit? best = null; long score = long.MaxValue;
        foreach (var garage in candidates)
        {
            if (!SupportSafe(w, actor, Cell.Of(garage.Pos)) || !Enumerable.Range(0, garage.Def.Archetype.Production!.Doors)
                .Any(i => Reachable(w, actor, Cell.Of(World.ExitPoint(garage, i)), out _))) { continue; }
            var busy = garage.Doors.Count(d => d.Def >= 0); // production competes for the same doors
            var cost = Fixed.Sqrt(Distance(Cell.Of(actor.Pos), Cell.Of(garage.Pos))) / Fixed.OneRaw * p.TravelWeight +
                (long)(GarageClaims(garage, actor.Id) + busy) * p.QueueWeight / Math.Max(1, garage.Def.Archetype.Production!.Doors);
            if (_rules.Foundation?.CombatControl is { } combat) { cost += Danger(w, Cell.Of(garage.Pos), false) * combat.DangerWeight; }
            if (best is null || cost < score || cost == score && garage.Id < best.Id) { best = garage; score = cost; }
        }
        return best;
    }

    // Reserve only free individual combat vehicles, ahead of strategic recruitment. Existing mission/squad/cargo
    // ownership remains authoritative. During an emergency, an armed usable vehicle stays available to defend.
    private void ReserveGroundServices(World w)
    {
        if (_rules.Foundation?.ServiceControl is not { } p) { return; }
        var assigned = 0;
        foreach (var u in _own.Where(u => Mobile(u) && Free(u) && u.Squad == 0 && u.Def.Cargo is null && GroundServiceDue(u))
            .OrderBy(u => Armed(u) ? 1 : 0).ThenBy(u => (long)u.HP * 100 / Math.Max(1, u.Def.MaxHp)).ThenBy(u => u.Id))
        {
            if (assigned >= p.AssignmentsPerDecision || _specialists.Count >= _rules.Foundation.SpecialistJobs) { break; }
            if (Reserved(u.Id) || Resting(u.Id) || PreserveBuilder(w, u) || _pending.Any(q => q.Source == u.Id) || _ordered.BinarySearch(u.Id) >= 0 ||
                _situation.Posture == AiPosture.Defend && Armed(u) && (long)u.HP * 100 >= (long)u.Def.MaxHp * RetreatHp) { continue; }
            var garage = ChooseGarage(w, u);
            if (garage is null) { continue; }
            _specialists.Add(Job(w, AiSpecialistKind.Service, u, garage.Id, Cell.Of(garage.Pos)));
            assigned++; Trace(w, 0, "ground-service-reserved-before-recruitment");
        }
    }

    private void CancelGroundService(World w, SpecialistJob j, Unit actor, string reason)
    {
        if (actor.InGarage || actor.OutOf != 0) { return; } // the engine must finish safe bay/door exit
        if (actor.CanAct && !Emit(w, new Command { Kind = CommandKind.Stop, Units = [actor.Id] }, 0, "ground-service-cancel")) { return; }
        j.Stage = 3; j.LastKind = CommandKind.Stop; j.NextOrder = w.Tick + World.InputDelay + 1;
        Trace(w, 0, reason);
    }

    private void UpdateGroundService(World w, SpecialistJob j, Unit actor)
    {
        if (j.Stage == 3)
        {
            if (w.Tick >= j.NextOrder && actor.Inside == 0 && actor.OutOf == 0 && (actor.ServiceAt == 0 || !actor.CanAct))
            { FinishJob(w, j, "ground-service-released"); }
            return;
        }
        if (w.Tick >= j.Until || actor.Inside != 0 && !actor.InGarage || !actor.CanAct && !actor.InGarage)
        { CancelGroundService(w, j, actor, "ground-service-expired-or-unavailable"); return; }
        var garage = Own(j.Target);
        if (garage is null || !w.ServiceWorks(garage) || actor.Inside == 0 && !SupportSafe(w, actor, Cell.Of(garage.Pos)))
        { CancelGroundService(w, j, actor, "ground-service-garage-unavailable"); return; }
        if (actor.ServiceAt != 0 || actor.InGarage || actor.OutOf != 0) { return; }
        if (w.Tick < j.NextOrder) { return; } // includes the previously emitted Service input window
        if (!GroundServiceDue(actor))
        {
            if (j.Stage == 0) { FinishJob(w, j, "ground-service-no-longer-needed"); }
            else { CancelGroundService(w, j, actor, "ground-service-ready"); }
            return;
        }
        if (j.Stage > 0 || !GarageCanHelp(w, garage, actor))
        { CancelGroundService(w, j, actor, "ground-service-care-unmet"); return; }
        if (SendJob(w, j, new Command { Kind = CommandKind.Service, Units = [actor.Id], Target = garage.Id }, "ground-vehicle-service")) { j.Stage = 1; }
    }
}
