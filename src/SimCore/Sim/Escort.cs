using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>
/// An escort's state (<see cref="CommandKind.Escort"/>, EscortSystem.cs): which spot round the unit it escorts it holds, and where
/// that unit was last seen outside. Null on every unit that isn't escorting (games without escorts hash as before).
/// </summary>
public sealed class EscortState
{
    /// <summary>The unit it escorts (the order's target).</summary>
    public uint Ward { get; internal set; }

    /// <summary>Its spot in the group's layout (<see cref="World.EscortSpot"/>), or -1 until the spots are given out.</summary>
    public int Spot { get; internal set; } = -1;

    /// <summary>How many escorted the unit when the spots were given out (another number gives them out again).</summary>
    public int Count { get; internal set; }

    /// <summary>The escorted unit's facing when the spots were given out (a turn of 45 degrees gives them out again).</summary>
    public int Facing { get; internal set; }

    /// <summary>Where its spot is this tick: where it steers while it is not chasing.</summary>
    public Vec Point { get; internal set; }

    /// <summary>Where the escorted unit was last seen outside (it waits there while the unit is inside; guards it once it falls).</summary>
    public Vec Last { get; internal set; }

    /// <summary>The escorted unit's heading there (length 1024; zero: its facing).</summary>
    public Vec LastHeading { get; internal set; }

    /// <summary>The escorted unit's facing there.</summary>
    public int LastFacing { get; internal set; }

    /// <summary>The enemy that hurt the escorted unit it went after, or 0.</summary>
    public uint Threat { get; internal set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put4(0xE5C, Ward, unchecked((ulong)(long)Spot), unchecked((ulong)(long)Count));
        h.Put4(unchecked((ulong)(long)Facing), unchecked((ulong)Point.X.Raw), unchecked((ulong)Point.Y.Raw), Threat);
        h.Put4(unchecked((ulong)Last.X.Raw), unchecked((ulong)Last.Y.Raw), unchecked((ulong)LastHeading.X.Raw), unchecked((ulong)LastHeading.Y.Raw));
        h.Put(unchecked((ulong)(long)LastFacing));
    }
}

/// <summary>Why a unit may not escort another (<see cref="World.EscortRefusalOf"/>).</summary>
public enum EscortRefusal : byte
{
    /// <summary>An aircraft: aircraft are not escorts for now.</summary>
    Aircraft = 1,

    /// <summary>It doesn't move on the ground (a building, a fixed piece, a launched drone).</summary>
    CannotMove,

    /// <summary>The unit to escort is gone.</summary>
    Gone,

    /// <summary>The unit to escort is not the escort's side's.</summary>
    NotOwn,

    /// <summary>A unit can't escort itself.</summary>
    Self,

    /// <summary>The unit to escort can't be escorted: a building, an aircraft, a drone in flight.</summary>
    NotEscortable,

    /// <summary>The escort is inside a building, a vehicle, a tunnel or a garage.</summary>
    Inside,

    /// <summary>The unit to escort escorts the escort (itself, or through others): no loops.</summary>
    Loop,
}
