using SimCore.Model;

namespace SimCore.Sim;

// The supply system: ammo trucks and depots make friendly units in their
// radius reload faster, and every few ticks refill their launcher salvos and
// drone and trap charges. A supplier with limited stock spends it; one that
// restocks refills limited suppliers in its radius. Suppliers work without a
// driver, and visit units in ID order, so who gets the last rounds is fixed.
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
                    u.Stock = Math.Min(limited.Stock, u.Stock + sup.Restock);
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
    // depot (a structure with supply) is within its radius. Away from a depot the count restarts.
    private void RunDepotReload()
    {
        foreach (var u in _units)
        {
            foreach (var w in u.Def.Weapons)
            {
                if (w.Effect.DepotReload is not { } dr || w.Stock < 0)
                {
                    continue;
                }
                var full = u.Charges[w.Stock] >= u.Def.StockMax[w.Stock];
                var waiting = u.SalvoLeft > 0 || (u.LastShot != 0 && Tick < u.LastShot + (uint)dr.AfterFireTicks);
                if (full || waiting || !u.Alive || u.Inside != 0 || !NearDepot(u))
                {
                    u.DepotReloadLeft = dr.EveryTicks;
                    continue;
                }
                if (--u.DepotReloadLeft <= 0)
                {
                    u.MutableCharges[w.Stock]++;
                    u.DepotReloadLeft = dr.EveryTicks;
                }
            }
        }
    }

    private bool NearDepot(Unit u)
    {
        foreach (var s in _units)
        {
            if (s.Owner == u.Owner && s.Alive && s.Def.IsStructure && s.Def.Supply is { } sup && Within(s.Pos, u.Pos, sup.Radius))
            {
                return true;
            }
        }
        return false;
    }
}
