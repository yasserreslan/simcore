using SimCore.Model;

namespace SimCore.Sim;

// The supply system: suppliers (supply depots) make friendly units in their
// radius reload faster, and every few ticks refill their launcher salvos and
// drone and trap charges. A supplier with limited stock spends it (a depot's
// stock never refills by itself); one that restocks refills limited suppliers
// in its radius out of its own stock. Launchers that only a depot reloads pay
// each round's cost out of the depot's stock and wait when it is too low.
// Cargo trucks refill depots (RunCargo). Suppliers work without a driver, and
// visit units in ID order, so who gets the last rounds is fixed.
public sealed partial class World
{
    private void RunSupply()
    {
        foreach (var u in _units)
        {
            u.ReloadBonus = 0;
        }
        RunDepotReload();
        foreach (var s in _units)
        {
            if (s.Def.Supply is not { } sup || !s.Alive || s.Inside != 0)
            {
                continue;
            }
            var pulse = Tick % (uint)sup.EveryTicks == 0;
            foreach (var u in _units)
            {
                if (u.Owner != s.Owner || !u.Alive || u.Inside != 0 || !Within(s.Pos, u.Pos, sup.Radius))
                {
                    continue;
                }
                u.ReloadBonus = Math.Max(u.ReloadBonus, sup.ReloadBonus);
                if (!pulse || u == s)
                {
                    continue;
                }
                Rearm(s, sup, u);
                if (sup.Restock > 0 && u.Def.Supply is { Stock: > 0 } limited)
                {
                    var add = Math.Min(sup.Restock, limited.Stock - u.Stock);
                    if (sup.Stock > 0)
                    {
                        add = Math.Min(add, s.Stock); // out of the supplier's own stock
                    }
                    if (add > 0)
                    {
                        u.Stock += add;
                        if (sup.Stock > 0)
                        {
                            s.Stock -= add;
                        }
                    }
                }
            }
        }
    }

    // Rearm gives each of u's stocks up to sup.Amount, out of s's stock if
    // it is limited.
    private static void Rearm(Unit s, SupplyEffect sup, Unit u)
    {
        var charges = u.MutableCharges;
        for (var i = 0; i < charges.Length; i++)
        {
            if (DepotReloaded(u, i) is not null)
            {
                continue; // only a depot reloads it, round by round (RunDepotReload)
            }
            var give = Math.Min(sup.Amount, u.Def.StockMax[i] - charges[i]);
            if (sup.Stock > 0)
            {
                give = Math.Min(give, s.Stock);
            }
            if (give <= 0)
            {
                continue;
            }
            charges[i] += give;
            if (sup.Stock > 0)
            {
                s.Stock -= give;
            }
        }
    }

    // DepotReloaded returns the depot-reloaded weapon that uses stock i, or null.
    private static WeaponDef? DepotReloaded(Unit u, int stock)
    {
        foreach (var w in u.Def.Weapons)
        {
            if (w.Stock == stock && w.Effect.DepotReload is not null)
            {
                return w;
            }
        }
        return null;
    }

    // RunDepotReload reloads weapons that only a depot can reload: never while firing a salvo;
    // AfterFireTicks after the last shot, one round every EveryTicks while a friendly supply
    // depot (a structure with supply) is within its radius and has the round's cost in stock
    // (the first such depot by ID pays). Away from a depot, or while every depot in reach is
    // too low (DepotStarved), the count restarts.
    private void RunDepotReload()
    {
        foreach (var u in _units)
        {
            u.DepotStarved = false;
            foreach (var w in u.Def.Weapons)
            {
                if (w.Effect.DepotReload is not { } dr || w.Stock < 0)
                {
                    continue;
                }
                var full = u.Charges[w.Stock] >= u.Def.StockMax[w.Stock];
                var waiting = u.SalvoLeft > 0 || (u.LastShot != 0 && Tick < u.LastShot + (uint)dr.AfterFireTicks);
                var near = !full && !waiting && u.Alive && u.Inside == 0 && NearDepot(u);
                if (near && dr.Stowed && u.Deploy != DeployState.Stowed)
                {
                    // It reloads only stowed: a deployed launcher with nothing to fire at packs up first.
                    if (u.Deploy == DeployState.Deployed && u.Order.Kind == OrderKind.Idle)
                    {
                        StartStow(u);
                    }
                    u.DepotReloadLeft = dr.EveryTicks;
                    continue;
                }
                var depot = near ? DepotWithStock(u, dr.Cost) : null;
                if (depot is null)
                {
                    u.DepotStarved |= near; // at a depot, but its stock is below the cost
                    u.DepotReloadLeft = dr.EveryTicks;
                    continue;
                }
                if (--u.DepotReloadLeft <= 0)
                {
                    u.MutableCharges[w.Stock]++;
                    if (depot.Def.Supply!.Stock > 0)
                    {
                        depot.Stock -= dr.Cost;
                    }
                    u.DepotReloadLeft = dr.EveryTicks;
                }
            }
        }
    }

    // IsDepot reports whether s is a supply depot of u's side with u in its radius.
    private static bool IsDepot(Unit s, Unit u) =>
        s.Owner == u.Owner && s.Alive && s.Def.IsStructure && s.Def.Supply is { } sup && Within(s.Pos, u.Pos, sup.Radius);

    private bool NearDepot(Unit u)
    {
        foreach (var s in _units)
        {
            if (IsDepot(s, u))
            {
                return true;
            }
        }
        return false;
    }

    // DepotWithStock returns the first friendly depot (by ID) with u in its radius that has at
    // least cost in stock (or unlimited stock), or null.
    private Unit? DepotWithStock(Unit u, int cost)
    {
        foreach (var s in _units)
        {
            if (IsDepot(s, u) && (s.Def.Supply!.Stock == 0 || s.Stock >= cost))
            {
                return s;
            }
        }
        return null;
    }

    // IsCargoDepot reports whether d is a depot u (a cargo truck) can unload into: its side's
    // structure with limited supply stock.
    internal static bool IsCargoDepot(Unit d, Unit u) =>
        d.Alive && d.Owner == u.Owner && d.Def.IsStructure && d.Def.Supply is { Stock: > 0 };

    // RunCargo runs after supply: each full cargo truck (ID order) standing still in the radius of a
    // friendly depot unloads into it: UnloadTicks of standing there, then the whole load goes into the
    // depot's stock at once. It starts only when the depot has room for the whole load (counting other
    // trucks already unloading into it), and otherwise waits beside it. Moving off, leaving the radius
    // or losing the depot cancels the unload; the truck stays full.
    private void RunCargo()
    {
        foreach (var t in _units)
        {
            if (t.Def.Cargo is not { } cargo)
            {
                continue;
            }
            Unit? depot = null;
            if (t.Cargo >= cargo.Amount && t.Alive && t.Inside == 0 && !t.Moving)
            {
                depot = Unit(t.UnloadDepot) is { } d && IsCargoDepot(d, t) && Within(d.Pos, t.Pos, d.Def.Supply!.Radius) ? d : null;
                if (depot is null)
                {
                    foreach (var d2 in _units)
                    {
                        if (IsCargoDepot(d2, t) && Within(d2.Pos, t.Pos, d2.Def.Supply!.Radius) &&
                            (t.Order.Kind != OrderKind.Unload || t.Order.Target == d2.Id))
                        {
                            depot = d2;
                            break;
                        }
                    }
                }
            }
            if (depot is null)
            {
                t.UnloadLeft = 0; // cancelled, or nothing to do
                t.UnloadDepot = 0;
                continue;
            }
            if (t.UnloadDepot != depot.Id)
            {
                t.UnloadDepot = depot.Id;
                t.UnloadLeft = 0;
            }
            if (t.UnloadLeft == 0)
            {
                // Start only when the whole load fits: the depot's free room, less what other trucks
                // already unloading into it will add.
                var free = depot.Def.Supply!.Stock - depot.Stock;
                foreach (var o in _units)
                {
                    if (o != t && o.UnloadDepot == depot.Id && o.UnloadLeft > 0)
                    {
                        free -= o.Cargo;
                    }
                }
                if (free < t.Cargo)
                {
                    continue; // wait beside it
                }
                t.UnloadLeft = cargo.UnloadTicks;
            }
            if (--t.UnloadLeft > 0)
            {
                continue;
            }
            depot.Stock += t.Cargo;
            t.Cargo = 0;
            t.UnloadDepot = 0;
            if (t.Order.Kind == OrderKind.Unload)
            {
                t.Idle();
            }
        }
    }
}
