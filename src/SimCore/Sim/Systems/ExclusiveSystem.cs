using SimCore.Model;

namespace SimCore.Sim;

// Exclusive groups (content exclusiveGroups; the owner's "choose 1 of 3" for the heavy bombers, 2026-10-22). A player may have at
// most a group's max of its archetypes at a time: alive (parked, taxiing or in the air) plus queued or in a factory's bay or door.
// With lockType, the first one the player's production finishes locks the group to that archetype for the match (Player.ExclusiveLocks,
// hashed): a lost one may be rebuilt as the same type, never as another. Production refuses an order that would break either rule
// (Produce, ProductionSystem.cs). Units spawned outside production (a setup's army, the sandbox) count but lock nothing.
public sealed partial class World
{
    /// <summary>
    /// The exclusive group rule for <paramref name="player"/> building archetype <paramref name="a"/> (the client greys out what is
    /// refused and shows the locked type), or null when <paramref name="a"/> belongs to no group.
    /// </summary>
    public ExclusiveState? ExclusiveOf(byte player, Archetype a)
    {
        if (a.Exclusive < 0 || a.Exclusive >= Content.ExclusiveGroups.Count || Player(player) is not { } p)
        {
            return null;
        }
        var g = Content.ExclusiveGroups[a.Exclusive];
        var count = ExclusiveCount(player, g.Index);
        var locked = g.Index < p.ExclusiveLocks.Count ? p.ExclusiveLocks[g.Index] : -1;
        var reason = count >= g.Max ? ExclusiveRefusal.AtMax
            : g.LockType && locked >= 0 && locked != a.Index ? ExclusiveRefusal.Locked
            : ExclusiveRefusal.None;
        return new ExclusiveState(g.Index, g.Max, count, locked, reason);
    }

    // ExclusiveCount is how many units of exclusive group g the player has: alive, and queued or being built in its factories.
    private int ExclusiveCount(byte player, int g)
    {
        var n = 0;
        foreach (var u in _units)
        {
            if (u.Owner != player || !u.Alive)
            {
                continue;
            }
            if (u.Def.Archetype.Exclusive == g)
            {
                n++;
            }
            if (u.Def.Archetype.Production is null)
            {
                continue;
            }
            foreach (var d in u.ProductionQueue)
            {
                n += Match.Defs[d].Archetype.Exclusive == g ? 1 : 0;
            }
            foreach (var b in u.Bays)
            {
                n += b.Def >= 0 && Match.Defs[b.Def].Archetype.Exclusive == g ? 1 : 0;
            }
            foreach (var d in u.Doors)
            {
                n += d.Def >= 0 && Match.Defs[d.Def].Archetype.Exclusive == g ? 1 : 0;
            }
        }
        return n;
    }

    // ExclusiveBuilt: the player's production finished a unit of archetype a: a group with lockType is locked to it, once.
    private void ExclusiveBuilt(byte player, Archetype a)
    {
        if (a.Exclusive < 0 || a.Exclusive >= Content.ExclusiveGroups.Count || Player(player) is not { } p ||
            !Content.ExclusiveGroups[a.Exclusive].LockType || a.Exclusive >= p.MutableLocks.Length || p.MutableLocks[a.Exclusive] >= 0)
        {
            return;
        }
        p.MutableLocks[a.Exclusive] = a.Index;
    }
}

/// <summary>Why production refuses an archetype of an exclusive group (<see cref="ExclusiveState.Refusal"/>).</summary>
public enum ExclusiveRefusal : byte
{
    /// <summary>Not refused: it may be built.</summary>
    None,

    /// <summary>The player already has the group's max (alive, queued or being built).</summary>
    AtMax,

    /// <summary>The group is locked to another archetype for the match (its first one built).</summary>
    Locked,
}

/// <summary>A player's standing in an exclusive group for one archetype (<see cref="World.ExclusiveOf"/>).</summary>
/// <param name="Group">The group's index in <see cref="ContentSet.ExclusiveGroups"/>.</param>
/// <param name="Max">The most the player may have at once.</param>
/// <param name="Count">How many the player has now: alive, queued or being built.</param>
/// <param name="Locked">The archetype the group is locked to for this player (an index into <see cref="ContentSet.Archetypes"/>), or -1.</param>
/// <param name="Refusal">Why production refuses this archetype now, or None.</param>
public readonly record struct ExclusiveState(int Group, int Max, int Count, int Locked, ExclusiveRefusal Refusal)
{
    /// <summary>Whether production takes an order for this archetype now.</summary>
    public bool Allowed => Refusal == ExclusiveRefusal.None;
}
