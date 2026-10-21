using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The trigger system: traps (units with a trigger) go off, once each. The rules (docs/design.md,
// "Traps"):
//  1. Fuses first. A lit fuse counts down (its owner's Detonate order lights it at 1, so the trap
//     goes off this tick; another trap's blast lights it at that trap's chainTicks). Every lit fuse
//     is counted before any due trap goes off, so a fuse lit this tick is first counted next tick.
//  2. Contact. A trap that is not lit and not set to on-demand (AutoOff) goes off when an enemy
//     ground unit it affects comes within its trigger radius; the first in ID order sets it off.
//     Flying units never do.
//  3. Going off. Its kill target, if it has kill tags (the anti-tank mine: vehicles), is destroyed
//     outright: the unit that set it off if it has the tags, else the nearest enemy with them within
//     the trigger radius (ties: the lower ID). Then the blast hits everything its warhead can within
//     its splash (falling to edge percent at the splash edge) except traps, and never kills another
//     unit with the kill tags: one anti-tank mine never destroys two vehicles. Every other trap
//     within the splash that is not lit is lit with this trap's chainTicks: a field goes off link by
//     link, so traps laid together act as one bigger trap.
public sealed partial class World
{
    private readonly List<Unit> _dueTraps = [];
    private readonly List<Unit> _nearTrap = [];

    private void RunTriggers()
    {
        foreach (var trap in _units)
        {
            if (trap.Fuse > 0 && trap.Alive && trap.Def.Trigger is not null && --trap.Fuse == 0)
            {
                trap.HP = 0; // due: no chain can light it again
                _dueTraps.Add(trap);
            }
        }
        foreach (var trap in _dueTraps)
        {
            GoOff(trap, null);
        }
        _dueTraps.Clear();

        foreach (var trap in _units)
        {
            if (trap.Def.Trigger is not { } t || !trap.Alive || trap.Fuse != 0 || trap.AutoOff)
            {
                continue;
            }
            // The combat index's candidates in ID order (nobody has moved since it was built): the first
            // match is the same unit a scan of every unit finds.
            var reach = t.Radius.Raw + Fixed.OneRaw;
            foreach (var o in Around(trap.Pos.X.Raw - reach, trap.Pos.Y.Raw - reach, trap.Pos.X.Raw + reach, trap.Pos.Y.Raw + reach, _nearTrap, true))
            {
                if (!o.Alive || o.Inside != 0 || o.Garrison != 0 || o.Owner == trap.Owner || o.Def.Layer != Layers.Ground ||
                    !o.Def.Tags.Matches(t.Affects) || !Within(trap.Pos, o.Pos, t.Radius))
                {
                    continue;
                }
                trap.HP = 0;
                GoOff(trap, o);
                break;
            }
        }
        DropIndex(); // the index built for combat ends here
    }

    // GoOff detonates trap (already at 0 HP), set off by unit by (null: on demand or by a chain).
    private void GoOff(Unit trap, Unit? by)
    {
        var t = trap.Def.Trigger!;
        trap.Detonated = true;
        if (!t.Kills.IsEmpty && KillTarget(trap, t, by) is { } victim)
        {
            victim.HP = 0;
        }
        Explode(trap.Pos, t.Damage, t.Warhead, t.Splash, edge: t.Edge, trapBlast: true, spare: t.Kills);
        foreach (var o in _units)
        {
            if (o != trap && o.Alive && o.Fuse == 0 && o.Def.Trigger is not null && Within(trap.Pos, o.Pos, t.Splash))
            {
                o.Fuse = t.ChainTicks;
            }
        }
    }

    // KillTarget returns the unit trap destroys outright: by if it has the kill tags, else the
    // nearest enemy ground unit with them within the trigger radius (ties: the lower ID), or null.
    private Unit? KillTarget(Unit trap, TriggerEffect t, Unit? by)
    {
        if (by is not null && by.Def.Tags.Matches(t.Kills))
        {
            return by;
        }
        Unit? best = null;
        var bestD = long.MaxValue;
        foreach (var o in _units)
        {
            if (!o.Alive || o.Inside != 0 || o.Garrison != 0 || o.Owner == trap.Owner || o.Def.Layer != Layers.Ground ||
                !o.Def.Tags.Matches(t.Kills) || !Within(trap.Pos, o.Pos, t.Radius))
            {
                continue;
            }
            var d = (o.Pos - trap.Pos).LenSq();
            if (d < bestD)
            {
                (best, bestD) = (o, d);
            }
        }
        return best;
    }
}
