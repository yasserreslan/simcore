using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The reload pad (the Patriot-type launcher's; docs: the game repo's docs/patriot-design.md): a packed-up unit with one of
// the pad's tags, standing still on its footprint and short of ammo, is reloaded in full after the pad's reload time, one
// at a time (the lowest ID waiting goes first), while a finished airfield facility (or air base core) of the pad's side
// stands within its airfield range. Moving off, setting up, leaving or losing the pad cancels it. It runs after supply.
public sealed partial class World
{
    private void RunReloadPads()
    {
        foreach (var pad in _units)
        {
            if (pad.Def.Archetype.ReloadPad is not { } rp)
            {
                continue;
            }
            if (!pad.Alive || !pad.IsBuilt || (rp.AirfieldRange.Raw > 0 && PadAirfield(pad.Owner, pad.Pos, rp.AirfieldRange) is null))
            {
                (pad.PadUnit, pad.PadLeft) = (0, 0); // down, unfinished or cut off from its airfield: nothing reloads
                continue;
            }
            if (pad.PadUnit != 0 && !(Unit(pad.PadUnit) is { } cur && Waits(pad, rp, cur)))
            {
                (pad.PadUnit, pad.PadLeft) = (0, 0); // it drove off, set up or died
            }
            if (pad.PadUnit == 0)
            {
                foreach (var u in _units)
                {
                    if (Waits(pad, rp, u))
                    {
                        (pad.PadUnit, pad.PadLeft) = (u.Id, rp.ReloadTicks);
                        break;
                    }
                }
                continue; // the reload starts counting next tick
            }
            if (--pad.PadLeft > 0)
            {
                continue;
            }
            var unit = Unit(pad.PadUnit)!;
            var full = unit.Def.StockMax;
            var charges = unit.MutableCharges;
            var weapons = unit.Def.Weapons;
            for (var i = 0; i < weapons.Count; i++)
            {
                if (weapons[i].Stock >= 0)
                {
                    charges[weapons[i].Stock] = full[weapons[i].Stock];
                }
            }
            (pad.PadUnit, pad.PadLeft) = (0, 0);
        }
    }

    // Waits: u is a unit pad reloads: its side's, one of the pad's tags, alive, packed up and standing still on the pad's
    // footprint (its position inside it), short of ammo in some weapon.
    private bool Waits(Unit pad, ReloadPad rp, Unit u)
    {
        if (u.Owner != pad.Owner || !u.Alive || u.Def.IsStructure || !u.Def.Tags.Matches(rp.Tags) || u.Inside != 0 || u.Moving ||
            u.Deploy != DeployState.Stowed)
        {
            return false;
        }
        long hw = (long)FootW(pad) * Fixed.OneRaw / 2, hh = (long)FootH(pad) * Fixed.OneRaw / 2;
        if (Math.Abs(u.Pos.X.Raw - pad.Pos.X.Raw) > hw || Math.Abs(u.Pos.Y.Raw - pad.Pos.Y.Raw) > hh)
        {
            return false;
        }
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Stock >= 0 && u.Charges[weapons[i].Stock] < u.Def.StockMax[weapons[i].Stock])
            {
                return true;
            }
        }
        return false;
    }

    // PadAirfield returns the first airfield (in ID order) of player owner whose facility (or air base core) stands
    // finished within range of at (its footprint's nearest point), or null.
    internal Airfield? PadAirfield(byte owner, Vec at, Fixed range)
    {
        foreach (var af in _airfields)
        {
            if (af.Owner != owner)
            {
                continue;
            }
            foreach (var id in (ReadOnlySpan<uint>)[af.Facility, af.Core])
            {
                if (id != 0 && Unit(id) is { Alive: true, IsBuilt: true, Rubble: false } f && Within(at, AimPoint(at, f), range))
                {
                    return af;
                }
            }
        }
        return null;
    }
}
