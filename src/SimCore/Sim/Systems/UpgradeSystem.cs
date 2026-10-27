using SimCore.Model;

namespace SimCore.Sim;

// Upgrades (docs/design.md, "Incendiary bomb"): a weapon may need a named upgrade (content weapon `upgrade`; the incendiary bomb's
// "incendiary"). A unit without it carries none of that weapon: its stock holds nothing (Unit.StockMax is 0), so it starts and
// rearms empty and every system that looks for ammunition passes it by. A player's upgrades (Player.Upgrades) come from the lobby
// (a player's `upgrades`; none in every shipped lobby) and from GrantUpgrade, the hook the upgrade system (bought at a building,
// later) will call; the sandbox grants one with CommandKind.SandboxUpgrade. Each unit keeps its own bits (Unit.Upgrades: its
// owner's when made, and every grant to its owner since), so a per-unit upgrade needs no other state. Both are hashed when not 0.
public sealed partial class World
{
    /// <summary>
    /// Grants player <paramref name="player"/> the upgrade named <paramref name="name"/> (<see cref="ContentSet.Upgrades"/>): its units
    /// alive now and every unit it gets from now on carry the weapons that need it. A unit's weapon fills at its next rearm (an
    /// aircraft at a station, a loadout change); nothing is loaded at once. Reports whether the name is an upgrade of the content
    /// and the player exists. Granting one the player has changes nothing. The hook for the upgrade system and scripted demos.
    /// </summary>
    public bool GrantUpgrade(byte player, string name)
    {
        var k = Content.UpgradeIndex(name);
        if (k < 0 || Player(player) is not { } p)
        {
            return false;
        }
        var bit = 1u << k;
        p.Upgrades |= bit;
        foreach (var u in _units)
        {
            if (u.Owner == player)
            {
                u.Upgrades |= bit;
            }
        }
        return true;
    }

    // ApplyUpgrades gives a new unit its owner's upgrades and empties every stock whose upgrade it lacks.
    private void ApplyUpgrades(Unit u)
    {
        u.Upgrades = Player(u.Owner)?.Upgrades ?? 0;
        if (u.Def.StockUpgrades is null)
        {
            return;
        }
        var charges = u.MutableCharges;
        for (var i = 0; i < charges.Length; i++)
        {
            charges[i] = Math.Min(charges[i], u.StockMax(i));
        }
    }
}
