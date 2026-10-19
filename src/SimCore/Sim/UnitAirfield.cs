namespace SimCore.Sim;

// A structure's repair state (milestone M3a: airfield pieces). Hashed only while set.
public sealed partial class Unit
{
    /// <summary>
    /// Repair work short of the next HP point (builder-rate percent times ticks times max HP, below
    /// the work of a whole bar); 0 while nobody repairs it.
    /// </summary>
    public long RepairWork { get; internal set; }
}
