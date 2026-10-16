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
}
