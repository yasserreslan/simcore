using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// A fixed wing flying as a wingman in an element (<see cref="Unit.Wingman"/>, E13; hashed while present;
/// read-only for renderers): its lead (the lowest ID of the aircraft given the same order), its slot, and the
/// lead's recent path (positions, velocities and strike phases, newest last), off which it flies its slot: the
/// lead's position a slot's delay ago, offset aside, so it never cuts the lead's turns. The lead itself has none.
/// </summary>
public sealed class Wingman
{
    // The lead's path, a ring of Capacity entries: positions (raw), velocities (raw a second) and strike phases
    // (0: none; else the phase + 1, with 0x80 set when its break side is clockwise).
    internal long[] X = [], Y = [], VX = [], VY = [];
    internal byte[] Phase = [];

    internal Wingman(uint lead, int slot, int capacity)
    {
        Lead = lead;
        Slot = slot;
        X = new long[capacity];
        Y = new long[capacity];
        VX = new long[capacity];
        VY = new long[capacity];
        Phase = new byte[capacity];
    }

    /// <summary>Its lead's unit ID.</summary>
    public uint Lead { get; internal set; }

    /// <summary>Its slot: 1 to 3 (the element spec's slots in order; the lead is slot 0).</summary>
    public int Slot { get; internal set; }

    /// <summary>Whether it flew its slot (or its staggered run's lead-in) over the last tick; false: on its own (its own run, an air fight, the element broken).</summary>
    public bool Following { get; internal set; }

    /// <summary>How far it was from its slot over the last tick while following (raw, level): the slot error.</summary>
    public long SlotError { get; internal set; }

    // The newest entry's index, and how many entries are valid (0: none recorded since it last flew with its lead).
    internal int Head { get; set; }

    internal int Count { get; set; }

    internal int Capacity => X.Length;

    internal void HashInto(ref WordHash h)
    {
        h.Put(0xE1E);
        h.Put(Lead);
        h.PutSigned(Slot);
        h.Put(Following ? 1UL : 0);
        h.PutSigned(SlotError);
        h.PutSigned(Head);
        h.PutSigned(Count);
        for (var i = 0; i < Count; i++)
        {
            var k = (Head - i + Capacity) % Capacity;
            h.PutSigned(X[k]);
            h.PutSigned(Y[k]);
            h.PutSigned(VX[k]);
            h.PutSigned(VY[k]);
            h.PutByte(Phase[k]);
        }
    }
}
