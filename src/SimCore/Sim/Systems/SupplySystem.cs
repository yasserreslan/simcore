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
            if (s.Def.Supply is not { } sup || !s.Alive || s.Inside != 0 || !s.IsBuilt)
            {
                continue; // a construction site gives nothing yet
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
            if (DepotReloaded(u, i) is not null || NeverResupplied(u, i))
            {
                continue; // only a depot reloads it, round by round (RunDepotReload), or nothing does
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

    // NeverResupplied reports whether stock i is a weapon nothing refills, or a kit only a depot
    // refills (RunDepotReload).
    private static bool NeverResupplied(Unit u, int stock)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Stock == stock && weapons[i].Effect.NoResupply)
            {
                return true;
            }
        }
        var abilities = u.Def.Abilities;
        for (var i = 0; i < abilities.Count; i++)
        {
            var ab = abilities[i];
            if (ab.Stock == stock && ab.Effect is PlacerEffect { DepotReload: not null } or LaunchEffect { DepotReload: not null } or LaunchEffect { NoResupply: true })
            {
                return true;
            }
        }
        return false;
    }

    // DepotReloaded returns the depot-reloaded weapon that uses stock i, or null.
    private static WeaponDef? DepotReloaded(Unit u, int stock)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Stock == stock && weapons[i].Effect.DepotReload is not null)
            {
                return weapons[i];
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
            // One stock at a time (they share the unit's one count, DepotReloadLeft): the first that is not full, in
            // the order weapons then abilities; all full, the first (its count restarts). A unit with one such stock
            // (every one but the engineer, whose drone station kit and Kaman station kit both refill at a depot)
            // reloads as it always did.
            (int Stock, DepotReload Dr) first = (-1, null!), pick = (-1, null!);
            var count = 0;
            // Index loops: foreach over an IReadOnlyList allocates an enumerator, per unit per tick.
            var weapons = u.Def.Weapons;
            for (var i = 0; i < weapons.Count; i++)
            {
                var w = weapons[i];
                if (w.Effect.DepotReload is { } dr && w.Stock >= 0)
                {
                    Candidate(u, w.Stock, dr, ref first, ref pick, ref count);
                }
            }
            var abilities = u.Def.Abilities;
            for (var i = 0; i < abilities.Count; i++)
            {
                var ab = abilities[i];
                if (ab.Effect is PlacerEffect { DepotReload: { } dr })
                {
                    Candidate(u, ab.Stock, dr, ref first, ref pick, ref count); // a kit only a depot refills (an engineer's drone station)
                }
                else if (ab.Effect is LaunchEffect { DepotReload: { } ldr } l && (l.MaxActive == 0 || Aloft(u, l.Archetype) < l.MaxActive))
                {
                    Candidate(u, ab.Stock, ldr, ref first, ref pick, ref count); // a scout launcher's replacement drone, never while its own is aloft
                }
            }
            if (pick.Stock < 0)
            {
                pick = first;
            }
            if (count > 1 && u.DepotReloadStock != pick.Stock + 1)
            {
                // Another of its stocks now: its count starts over at that one's period (DepotReloadStock: only units with
                // several, so the hash of every other unit is as before).
                u.DepotReloadStock = pick.Stock + 1;
                u.DepotReloadLeft = pick.Dr.EveryTicks;
            }
            if (pick.Stock >= 0)
            {
                DepotReload(u, pick.Stock, pick.Dr);
            }
        }
    }

    // Candidate notes stock (reloaded by dr) as u's first depot-reloaded stock, and as the one to reload if it is the first
    // not full (RunDepotReload).
    private static void Candidate(Unit u, int stock, DepotReload dr, ref (int Stock, DepotReload Dr) first, ref (int Stock, DepotReload Dr) pick,
        ref int count)
    {
        count++;
        if (first.Stock < 0)
        {
            first = (stock, dr);
        }
        if (pick.Stock < 0 && u.Charges[stock] < u.Def.StockMax[stock])
        {
            pick = (stock, dr);
        }
    }

    // DepotReload counts u's reload of stock toward its next round at a depot (see RunDepotReload).
    private void DepotReload(Unit u, int stock, DepotReload dr)
    {
        var full = u.Charges[stock] >= u.Def.StockMax[stock];
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
            return;
        }
        var depot = near ? DepotWithStock(u, dr.Cost) : null;
        if (depot is null)
        {
            u.DepotStarved |= near; // at a depot, but its stock is below the cost
            u.DepotReloadLeft = dr.EveryTicks;
            return;
        }
        if (--u.DepotReloadLeft <= 0)
        {
            u.MutableCharges[stock]++;
            if (depot.Def.Supply!.Stock > 0)
            {
                depot.Stock -= dr.Cost;
            }
            u.DepotReloadLeft = dr.EveryTicks;
        }
    }

    // IsDepot reports whether s is a supply depot of u's side with u in its radius.
    private static bool IsDepot(Unit s, Unit u) =>
        s.Owner == u.Owner && s.Alive && s.IsBuilt && s.Def.IsStructure && s.Def.Supply is { } sup && Within(s.Pos, u.Pos, sup.Radius);

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
        d.Alive && d.IsBuilt && d.Owner == u.Owner && d.Def.IsStructure && d.Def.Supply is { Stock: > 0 };

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
