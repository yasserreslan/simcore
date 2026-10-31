using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private AiLogisticsRules CargoPolicy => _rules.Foundation!.LogisticsControl!;

    /// <summary>Detached cargo trips, including bounded progress and cancellation state.</summary>
    public IReadOnlyList<AiCargoMemory> CargoTrips => _cargo.Select(j =>
        new AiCargoMemory(j.Truck, j.Store, j.Kind, j.Until, j.NextOrder, j.Done, j.Control)).ToArray();

    private static bool CargoStore(Unit s) => s.Inside == 0 && s.IsBuilt && !s.Rubble && s.Def.IsStructure && s.Def.Supply is not null;

    // Planner and ordinary owned orders share one claim per truck. Never count both representations twice.
    private Unit[] CargoClaimants(uint store, CommandKind kind, uint except) => _own.Where(t => t.Id != except && t.Def.Cargo is not null &&
        (_cargo.Any(j => !j.Done && j.Truck == t.Id && j.Store == store && j.Kind == kind) ||
            t.Order.Target == store && t.Order.Kind == (kind == CommandKind.Unload ? OrderKind.Unload : OrderKind.LoadCargo)))
        .ToArray();

    private long DeliveryRoom(Unit store, uint except) => Math.Max(0, (long)store.Def.Supply!.Stock - store.Stock -
        CargoClaimants(store.Id, CommandKind.Unload, except).Sum(t => (long)t.Cargo));

    private long SourceStock(Unit store, uint except)
    {
        var supply = store.Def.Supply!;
        if (supply.Stock == 0) { return long.MaxValue; }
        return Math.Max(0, (long)store.Stock - (long)supply.Stock * CargoPolicy.SourceReservePercent / 100 -
            CargoClaimants(store.Id, CommandKind.LoadCargo, except).Sum(t => (long)t.Def.Cargo!.Amount - t.Cargo));
    }

    private int CargoConsumers(Unit store) => _own.Count(u => u.Id != store.Id && u.Inside == 0 && u.IsBuilt && !u.Rubble &&
        u.Def.Cargo is null && !u.Def.IsStructure && Near(Cell.Of(u.Pos), Cell.Of(store.Pos), CargoPolicy.DemandCells) &&
        (!store.Def.Supply!.GroundOnly || u.Def.Layer != Layers.Air && u.Def.Archetype.Flight is null && u.Def.Archetype.Aircraft is null) &&
        ((long)u.HP * 100 < (long)u.Def.MaxHp * ReadyHp || u.Def.Weapons.Any(weapon => weapon.Stock >= 0 &&
            (u.WeaponMode != WeaponMode.PrimaryOnly || weapon.Index == 0) && u.StockMax(weapon.Stock) > 0 &&
            (long)u.Charges[weapon.Stock] * 100 < (long)u.StockMax(weapon.Stock) * _rules.SupplyLowPercent)));

    private long DeliveryValue(Unit store, uint truck)
    {
        var room = DeliveryRoom(store, truck);
        var consumers = Math.Min(CargoPolicy.Candidates, CargoConsumers(store));
        if (room <= 0 || (long)store.Stock * 100 >= (long)store.Def.Supply!.Stock * _rules.SupplyLowPercent && consumers == 0) { return 0; }
        return room * CargoPolicy.DeficitWeight / Math.Max(1, store.Def.Supply!.Stock) + (long)consumers * CargoPolicy.ConsumerWeight;
    }

    private bool CargoAccess(World w, Unit truck, Unit store) => Near(Cell.Of(truck.Pos), Cell.Of(store.Pos), _rules.SupplyTripCells) &&
        Reachable(w, truck, Cell.Of(store.Pos), out var actual) && World.Within(actual.Center(), store.Pos, store.Def.Supply!.Radius) &&
        SupportSafe(w, truck, actual);

    private Unit? DeliveryStore(World w, Unit truck, uint source)
    {
        var choices = _own.Where(s => CargoStore(s) && s.Id != source && s.Def.Supply is { Stock: > 0 } &&
                DeliveryRoom(s, truck.Id) >= truck.Cargo && CargoClaimants(s.Id, CommandKind.Unload, truck.Id).Length < CargoPolicy.TrucksPerStore)
            .Select(s => (Store: s, Value: DeliveryValue(s, truck.Id)))
            .Where(x => x.Value > 0).OrderByDescending(x => x.Value).ThenBy(x => Distance(Cell.Of(truck.Pos), Cell.Of(x.Store.Pos)))
            .ThenBy(x => x.Store.Id).Take(CargoPolicy.Candidates);
        Unit? best = null; long score = long.MinValue;
        foreach (var (store, value) in choices)
        {
            if (!CargoAccess(w, truck, store)) { continue; }
            var cost = _rules.Foundation?.CombatControl is null ? Fixed.Sqrt(Distance(Cell.Of(truck.Pos), Cell.Of(store.Pos))) / Fixed.OneRaw :
                RecoveryCost(w, Cell.Of(truck.Pos), Cell.Of(store.Pos), false, null, [truck]);
            var utility = value - cost;
            if (best is null || utility > score || utility == score && store.Id < best.Id) { best = store; score = utility; }
        }
        return best;
    }

    private Unit? LoadingStore(World w, Unit truck)
    {
        // Require a whole unclaimed load. The engine waits indefinitely for one, rather than loading a partial source.
        foreach (var source in _own.Where(s => CargoStore(s) && s.Def.Supply is { CargoSource: true } &&
                SourceStock(s, truck.Id) >= truck.Def.Cargo!.Amount - truck.Cargo &&
                CargoClaimants(s.Id, CommandKind.LoadCargo, truck.Id).Length < CargoPolicy.TrucksPerStore)
            .OrderBy(s => Distance(Cell.Of(truck.Pos), Cell.Of(s.Pos))).ThenBy(s => s.Id).Take(CargoPolicy.Candidates))
        {
            if (!CargoAccess(w, truck, source)) { continue; }
            // Cheap opportunity check only; the delivery leg confirms access after loading. Never undo a load into its donor.
            if (_own.Any(s => s.Id != source.Id && CargoStore(s) && s.Def.Supply is { Stock: > 0 } &&
                DeliveryRoom(s, truck.Id) >= truck.Def.Cargo!.Amount && DeliveryValue(s, truck.Id) > 0)) { return source; }
        }
        return null;
    }

    private void EndCargoFlow(World w, CargoJob job, Unit? truck, string reason)
    {
        if (truck is null) { job.Done = true; return; }
        if (truck.CanAct && !Emit(w, new Command { Kind = CommandKind.Stop, Units = [truck.Id] }, 0, reason)) { return; }
        // Retain ownership until Stop supersedes any delayed old cargo input. An uncommandable actor cannot start new work.
        job.Control = job.Control! with { ReleaseAt = w.Tick + World.InputDelay + 1 };
        Trace(w, 0, reason);
    }

    private void PlanCargoFlow(World w)
    {
        var p = CargoPolicy; var selections = 0; var reviews = 0;
        foreach (var job in _cargo.OrderBy(j => j.Control!.NextReview).ThenBy(j => j.Truck))
        {
            var c = job.Control!; var truck = Own(job.Truck);
            if (c.ReleaseAt != 0)
            {
                if (truck is null || w.Tick >= c.ReleaseAt)
                { job.Done = true; if (truck is not null) { _rest.Add((truck.Id, w.Tick + (uint)_rules.RestTicks)); } }
                continue;
            }
            if (truck is null || truck.Def.Cargo is null) { job.Done = true; continue; }
            if (!truck.CanAct || truck.Inside != 0 || truck.Rubble || w.Tick >= job.Until)
            { EndCargoFlow(w, job, truck, "cargo-flow-expired-or-unavailable"); continue; }
            if (job.Kind == CommandKind.Unload && truck.Cargo == 0)
            { EndCargoFlow(w, job, truck, "cargo-delivery-complete"); continue; }
            if (job.Kind == CommandKind.LoadCargo && truck.Cargo > c.LastCargo)
            {
                job.Kind = CommandKind.Unload; job.Store = 0; job.NextOrder = w.Tick;
                c = c with { ProgressAt = w.Tick, BestDistance = long.MaxValue, LastCargo = truck.Cargo, LastWork = 0, NextReview = w.Tick };
                job.Control = c;
                Trace(w, 0, "cargo-load-to-delivery");
            }
            var store = Own(job.Store);
            var work = job.Kind == CommandKind.LoadCargo ? truck.LoadLeft : truck.UnloadLeft;
            var distance = store is null ? long.MaxValue : Fixed.Sqrt(Distance(Cell.Of(truck.Pos), Cell.Of(store.Pos)));
            if (truck.Cargo != c.LastCargo || work > 0 && (c.LastWork == 0 || work < c.LastWork) ||
                distance < long.MaxValue && (c.BestDistance == long.MaxValue || distance + (long)_rules.ProgressCells * Fixed.OneRaw <= c.BestDistance))
            { c = c with { ProgressAt = w.Tick, BestDistance = Math.Min(c.BestDistance, distance) }; }
            c = c with { LastCargo = truck.Cargo, LastWork = work }; job.Control = c;
            if (w.Tick >= c.ProgressAt + (uint)p.StallTicks)
            { EndCargoFlow(w, job, truck, "cargo-flow-no-progress"); continue; }
            if (w.Tick < c.NextReview || reviews >= p.ReviewsPerDecision) { continue; }
            reviews++; job.Control = c with { NextReview = w.Tick + (uint)p.ReviewTicks };
            if (job.Store == 0)
            {
                if (selections >= p.SelectionsPerDecision) { continue; }
                selections++; store = DeliveryStore(w, truck, c.Source);
                if (store is null) { continue; }
                job.Store = store.Id; job.NextOrder = w.Tick;
                job.Control = job.Control with { BestDistance = long.MaxValue };
            }
            var valid = store is not null && CargoStore(store) && CargoAccess(w, truck, store) &&
                (job.Kind == CommandKind.LoadCargo ? store.Def.Supply is { CargoSource: true } &&
                    (work > 0 || SourceStock(store, truck.Id) >= truck.Def.Cargo.Amount - truck.Cargo) :
                    store.Id != c.Source && store.Def.Supply is { Stock: > 0 } && (work > 0 || DeliveryRoom(store, truck.Id) >= truck.Cargo));
            if (!valid) { EndCargoFlow(w, job, truck, "cargo-flow-store-unavailable"); continue; }
            if (w.Tick >= job.NextOrder && (Free(truck) || truck.Order.Target != job.Store ||
                truck.Order.Kind != (job.Kind == CommandKind.Unload ? OrderKind.Unload : OrderKind.LoadCargo)) &&
                Emit(w, new Command { Kind = job.Kind, Units = [truck.Id], Target = job.Store }, 0, "cargo-flow-trip"))
            { job.NextOrder = w.Tick + World.InputDelay + 1; }
        }
        _cargo.RemoveAll(j => j.Done);
        foreach (var truck in _own)
        {
            if (selections >= p.SelectionsPerDecision) { break; }
            if (!Mobile(truck) || truck.Def.Cargo is null || !Free(truck) || Reserved(truck.Id) || Resting(truck.Id) ||
                _ordered.BinarySearch(truck.Id) >= 0 || _pending.Any(q => q.Source == truck.Id)) { continue; }
            selections++;
            var kind = truck.Cargo > 0 ? CommandKind.Unload : CommandKind.LoadCargo;
            var store = kind == CommandKind.Unload ? DeliveryStore(w, truck, 0) : LoadingStore(w, truck);
            if (store is null)
            { _rest.Add((truck.Id, w.Tick + (uint)p.ReviewTicks)); continue; } // failed low IDs must not starve later trucks
            if (!Emit(w, new Command { Kind = kind, Units = [truck.Id], Target = store.Id }, 0, "cargo-flow-assigned")) { continue; }
            _cargo.Add(new CargoJob(truck.Id, store.Id, kind, w.Tick + (uint)_rules.MissionTicks, w.Tick + World.InputDelay + 1)
            { Control = new AiCargoControlMemory(kind == CommandKind.LoadCargo ? store.Id : 0, w.Tick, w.Tick + (uint)p.ReviewTicks,
                long.MaxValue, truck.Cargo, 0, 0) });
        }
    }
}
