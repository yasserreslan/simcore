using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private readonly List<CargoJob> _cargo = [];

    private void Crew(World w)
    {
        foreach (var vehicle in _own)
        {
            if (vehicle.Driver != 0 || vehicle.Def.Crew is null || !vehicle.IsBuilt || vehicle.Rubble ||
                !Near(Cell.Of(vehicle.Pos), Home, _rules.DefendCells) ||
                _own.Any(u => u.Order.Kind == OrderKind.Enter && u.Order.Target == vehicle.Id) ||
                _pending.Any(p => p.Kind == CommandKind.Enter && p.Target == vehicle.Id)) { continue; }
            Unit? driver = null;
            foreach (var u in _own)
            {
                if (!Mobile(u) || !Free(u) || u.Squad != 0 || Reserved(u.Id) || u.Def.Builder is not null ||
                    _ordered.BinarySearch(u.Id) >= 0 || _pending.Any(p => p.Source == u.Id) || !World.CanBoard(u, vehicle) ||
                    !Reachable(w, u, Cell.Of(vehicle.Pos), out _)) { continue; }
                if (driver is null || Distance(Cell.Of(u.Pos), Cell.Of(vehicle.Pos)) < Distance(Cell.Of(driver.Pos), Cell.Of(vehicle.Pos))) { driver = u; }
            }
            if (driver is not null && Emit(w, new Command { Kind = CommandKind.Enter, Units = [driver.Id], Target = vehicle.Id }, 0, "crew-empty-vehicle"))
            { _pending.Add(new Pending(driver.Id, -1, CommandKind.Enter, w.Tick + World.InputDelay, vehicle.Id)); return; }
        }
    }

    private void Support(World w)
    {
        Cargo(w);
        foreach (var u in _own)
        {
            if (!Mobile(u) || Reserved(u.Id) || !Free(u) || u.Def.Layer != Layers.Ground || u.Def.Archetype.Flight is not null) { continue; }
            if (u.Def.Weapons.Count == 0) { continue; }
            if (!u.Def.Weapons.Any(p => p.Stock < 0 || u.Charges[p.Stock] > 0) && u.Def.Weapons.Any(p => p.Stock >= 0))
            {
                if (u.Deploy == DeployState.Deployed)
                { Emit(w, new Command { Kind = CommandKind.Stow, Units = [u.Id] }, 0, "stow-for-resupply"); continue; }
                if (u.Deploy != DeployState.Stowed) { continue; }
                var depot = NearestStore(w, u, s => s.Def.Supply is { Storage: false } sup && (sup.Stock == 0 || s.Stock > 0));
                if (depot is not null && !Near(Cell.Of(u.Pos), Cell.Of(depot.Pos), _rules.ArrivalCells))
                { Emit(w, new Command { Kind = CommandKind.Move, Units = [u.Id], Cell = Cell.Of(depot.Pos) }, 0, "seek-ammunition"); }
                continue;
            }
            // Deployable launchers stay outside mobile assault forces. They use only visible legal ground targets.
            if (u.Def.Archetype.DeployTicks > 0 || !Fighter(u))
            {
                Unit? best = null;
                foreach (var e in _visible)
                {
                    if (e.Def.Layer != Layers.Ground || w.PickWeapon(u, e, true) < 0) { continue; }
                    if (best is null || Distance(Cell.Of(u.Pos), Cell.Of(e.Pos)) < Distance(Cell.Of(u.Pos), Cell.Of(best.Pos))) { best = e; }
                }
                if (best is not null)
                { Emit(w, new Command { Kind = CommandKind.AttackGround, Units = [u.Id], Cell = Cell.Of(best.Pos) }, 0, "support-fire"); }
                else if (u.Stance != Stance.Guard)
                { Emit(w, new Command { Kind = CommandKind.SetStance, Units = [u.Id], Stance = Stance.Guard }, 0, "support-guard"); }
            }
        }
    }

    private Unit? NearestStore(World w, Unit truck, Func<Unit, bool> accepts)
    {
        Unit? best = null;
        foreach (var s in _own)
        {
            if (!s.IsBuilt || s.Rubble || !accepts(s) || !Near(Cell.Of(truck.Pos), Cell.Of(s.Pos), _rules.SupplyTripCells) ||
                !Reachable(w, truck, Cell.Of(s.Pos), out _)) { continue; }
            if (best is null || Distance(Cell.Of(truck.Pos), Cell.Of(s.Pos)) < Distance(Cell.Of(truck.Pos), Cell.Of(best.Pos))) { best = s; }
        }
        return best;
    }

    private void Cargo(World w)
    {
        foreach (var job in _cargo)
        {
            var truck = Own(job.Truck);
            var store = Own(job.Store);
            var finished = truck is null || store is null || !store.IsBuilt || store.Rubble ||
                (job.Kind == CommandKind.Unload ? truck.Cargo == 0 || store.Def.Supply is not { } sup ||
                    (sup.Stock > 0 && store.Stock >= sup.Stock) : truck.Cargo >= truck.Def.Cargo!.Amount ||
                    store.Def.Supply is not { CargoSource: true } source || (source.Stock > 0 && store.Stock == 0));
            if (finished || w.Tick >= job.Until)
            {
                if (!finished && truck is not null && !Emit(w, new Command { Kind = CommandKind.Stop, Units = [truck.Id] }, 0, "cancel-expired-cargo")) { continue; }
                job.Done = true;
                if (!finished && truck is not null) { _rest.Add((truck.Id, w.Tick + (uint)_rules.RestTicks)); }
                Trace(w, 0, finished ? "cargo-job-complete" : "cargo-job-expired");
            }
            else if (w.Tick >= job.NextOrder && truck is not null && Free(truck))
            {
                if (Emit(w, new Command { Kind = job.Kind, Units = [truck.Id], Target = job.Store }, 0, "cargo-trip"))
                { job.NextOrder = w.Tick + World.InputDelay + 1; }
            }
        }
        _cargo.RemoveAll(j => j.Done);
        foreach (var truck in _own)
        {
            if (!Mobile(truck) || truck.Def.Cargo is null || !Free(truck) || Reserved(truck.Id) || Resting(truck.Id) || _cargo.Any(j => j.Truck == truck.Id)) { continue; }
            var kind = truck.Cargo > 0 ? CommandKind.Unload : CommandKind.LoadCargo;
            var store = NearestStore(w, truck, s => s.Id != truck.Id && !_cargo.Any(j => j.Store == s.Id && j.Kind == kind) &&
                (kind == CommandKind.Unload
                    ? s.Def.Supply is { Stock: > 0 } sup && (long)s.Stock * 100 < (long)sup.Stock * _rules.SupplyLowPercent
                    : s.Def.Supply is { CargoSource: true } source && (source.Stock == 0 || s.Stock > 0)));
            if (store is null) { continue; }
            if (Emit(w, new Command { Kind = kind, Units = [truck.Id], Target = store.Id }, 0, "cargo-assigned"))
            { _cargo.Add(new CargoJob(truck.Id, store.Id, kind, w.Tick + (uint)_rules.MissionTicks, w.Tick + World.InputDelay + 1)); }
        }
    }

    private sealed class CargoJob(uint truck, uint store, CommandKind kind, uint until, uint nextOrder)
    {
        public readonly uint Truck = truck, Store = store, Until = until;
        public readonly CommandKind Kind = kind;
        public uint NextOrder = nextOrder;
        public bool Done;
    }
}
