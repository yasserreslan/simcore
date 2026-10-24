using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// An aircraft's loadout mode (<see cref="World.LoadoutOf"/>, the stealth multirole), for the card and its mode switch. Not game state.
/// </summary>
/// <param name="Mode">Its mode now (an index into <see cref="Archetype.Loadouts"/>).</param>
/// <param name="Id">Its mode's content id ("stealth", "heavy").</param>
/// <param name="SwitchTo">The mode it is changing to, or -1.</param>
/// <param name="SwitchLeft">Ticks left of that change (0: not changing).</param>
/// <param name="SwitchTicks">The ticks a change takes (its archetype's).</param>
/// <param name="CanSwitch">Whether a <see cref="CommandKind.SetLoadout"/> would start a change now: parked at a station that rearms now
/// with free supply (a stand or a shelter of a forward airfield or a main air base; never in the air).</param>
public readonly record struct LoadoutStatus(int Mode, string Id, int SwitchTo, int SwitchLeft, int SwitchTicks, bool CanSwitch);

// Loadout modes (the stealth multirole, an F-35 type; docs/design.md, "Stealth multirole (F-35 type)"): an aircraft whose archetype
// has loadouts is always in one of them (Unit.Loadout, the first at birth). A mode sets each weapon's capacity (Unit.StockMax: what a
// rearm fills to), its speeds (World.FlightOf: the flight at the mode's speed percent) and its signature (Unit.Signature: detection,
// lock and kill against it). CommandKind.SetLoadout changes it only parked at a station of its side's airfield that rearms now and
// draws on no limited stock; the change takes the archetype's LoadoutSwitchTicks (the airfield system counts it down while it stays
// parked: StayParked holds it there, an order given meanwhile launches it once done), is cancelled if the station stops serving, and
// ends with the new mode's full load of every weapon (the flares are the same in every mode: left to the Rearm). All integer, in
// command order then the airfield system's ID order; Loadout, LoadoutTo and LoadoutLeft are hashed while not at rest.
public sealed partial class World
{
    /// <summary>Aircraft u's loadout modes (its archetype's, in content order; empty for anything else).</summary>
    public static IReadOnlyList<Loadout> LoadoutsOf(Unit u) => u.Def.Archetype.Loadouts;

    /// <summary>Aircraft u's loadout mode and its switch (null without loadout modes).</summary>
    public LoadoutStatus? LoadoutOf(Unit u)
    {
        var a = u.Def.Archetype;
        if (a.Loadouts.Count == 0)
        {
            return null;
        }
        return new LoadoutStatus(u.Loadout, a.Loadouts[u.Loadout].Id, u.LoadoutTo, u.LoadoutLeft, a.LoadoutSwitchTicks, CanSwitchLoadout(u));
    }

    // CanSwitchLoadout: u may change mode now: alive, parked at a station of its airfield that rearms now (it stands, its facility
    // too if it needs one) and draws on no limited stock (a forward airbase's).
    private bool CanSwitchLoadout(Unit u) =>
        u.Alive && u.AirOps is { Phase: FlightPhase.Parked, Station: not 0 } ops && Airfield(ops.Airfield) is { } af &&
        ServingSpec(af, ops) is { RearmPercent: > 0 } && StationStore(af, ops) is null;

    // SetLoadout runs CommandKind.SetLoadout: each listed aircraft of the player with loadout modes starts its change to mode c.Index
    // where it can change now; ordering the mode it has (or is changing from) cancels a change; another mode restarts it.
    private void SetLoadout(Command c)
    {
        foreach (var id in c.Units)
        {
            if (Unit(id) is not { Alive: true } u || u.Owner != c.Player || u.Def.Archetype.Loadouts is not { Count: > 0 } modes ||
                c.Index < 0 || c.Index >= modes.Count)
            {
                continue;
            }
            if (c.Index == u.Loadout)
            {
                (u.LoadoutTo, u.LoadoutLeft) = (-1, 0); // the mode it has: a change in progress is cancelled
                continue;
            }
            if (c.Index == u.LoadoutTo || !CanSwitchLoadout(u))
            {
                continue; // already on its way there, or not parked where it can change (in the air, taxiing, a refuel-only station)
            }
            (u.LoadoutTo, u.LoadoutLeft) = (c.Index, u.Def.Archetype.LoadoutSwitchTicks);
        }
    }

    // LoadoutSwitching runs a tick of parked aircraft u's mode change (StayParked, AirfieldFlightSystem.cs): true while it goes on (it
    // holds the aircraft at its station). Cancelled once its station stops serving; done, u takes the new mode with its full load.
    private bool LoadoutSwitching(Unit u)
    {
        if (u.LoadoutLeft == 0)
        {
            return false;
        }
        if (!CanSwitchLoadout(u))
        {
            (u.LoadoutTo, u.LoadoutLeft) = (-1, 0); // its station fell (or its facility): it keeps its mode
            return false;
        }
        if (--u.LoadoutLeft > 0)
        {
            return true;
        }
        u.Loadout = u.LoadoutTo;
        u.LoadoutTo = -1;
        var charges = u.MutableCharges;
        for (var i = 0; i < charges.Length; i++)
        {
            charges[i] = u.StockMax(i); // the new mode's full load (what it carried in the old one is taken off)
        }
        return false;
    }
}
