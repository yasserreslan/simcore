namespace SimCore.Sim;

/// <summary>Where a miner, a hauling truck or a Keep stocked truck is in its round (<see cref="EconState.Stage"/>).</summary>
public enum EconStage : byte
{
    /// <summary>Starting, or between rounds.</summary>
    None,

    /// <summary>A miner walking to its work spot.</summary>
    ToSpot,

    /// <summary>A miner mining at its work spot.</summary>
    Working,

    /// <summary>A miner carrying its load to a hauling truck or a bank.</summary>
    Delivering,

    /// <summary>Waiting: every work spot taken, no bank to deliver to, a full depot, or too little gold at the garage.</summary>
    Waiting,

    /// <summary>A hauling truck driving to the mine's loading spot.</summary>
    ToMine,

    /// <summary>A hauling truck waiting beside the mine while another holds the loading spot.</summary>
    Queued,

    /// <summary>A hauling truck parked on the loading spot, taking loads.</summary>
    Parked,

    /// <summary>A truck driving its gold to a bank.</summary>
    ToBank,

    /// <summary>A truck unloading gold at a bank (<see cref="EconState.Left"/> ticks to go).</summary>
    Unloading,

    /// <summary>A Keep stocked truck driving to a vehicle garage.</summary>
    ToGarage,

    /// <summary>A Keep stocked truck loading supply at a garage (<see cref="EconState.Left"/> ticks to go).</summary>
    Loading,

    /// <summary>A Keep stocked truck driving its supply to the depot (the cargo system unloads it there).</summary>
    ToDepot,
}

/// <summary>
/// The gold economy's state of one unit (<see cref="Unit.Econ"/>; docs/design.md, "Gold economy"). A mine uses
/// <see cref="GoldLeft"/>, <see cref="SlotHolders"/> and <see cref="Loader"/>; a miner or a truck the rest. Game state
/// (<see cref="HashInto"/>).
/// </summary>
public sealed class EconState
{
    private readonly uint[] _slots;

    internal EconState() => _slots = [];

    internal EconState(int gold, int slots)
    {
        GoldLeft = gold;
        _slots = new uint[slots];
    }

    /// <summary>A mine's gold left (0: dry, a ruin).</summary>
    public int GoldLeft { get; internal set; }

    /// <summary>A mine's work spots: the unit that holds each, or 0 (an entry whose unit left the order is free; checked on use).</summary>
    public IReadOnlyList<uint> SlotHolders => _slots;

    internal uint[] Slots => _slots;

    /// <summary>A mine's hauling truck parked on its loading spot, or 0 (checked on use, as the slots).</summary>
    public uint Loader { get; internal set; }

    /// <summary>A miner's or a truck's stage in its round.</summary>
    public EconStage Stage { get; internal set; }

    /// <summary>The mine it works or hauls from, or 0.</summary>
    public uint Mine { get; internal set; }

    /// <summary>A miner's work spot at <see cref="Mine"/>, or -1.</summary>
    public int Slot { get; internal set; } = -1;

    /// <summary>The gold it carries: a miner's load so far, a truck's gold aboard. Lost if it is destroyed.</summary>
    public int Carry { get; internal set; }

    /// <summary>A miner's work toward its next gold (gold a minute added each tick; a gold every 1,200).</summary>
    public int Work { get; internal set; }

    /// <summary>The bank a truck unloads at, or the garage it loads supply at, or 0.</summary>
    public uint At { get; internal set; }

    /// <summary>Ticks left unloading at a bank or loading at a garage.</summary>
    public int Left { get; internal set; }

    // Whether the event for its current wait (no bank, short of gold) went out.
    internal bool Told { get; set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put4(0xEC0, unchecked((ulong)GoldLeft), Loader, (ulong)_slots.Length);
        foreach (var s in _slots)
        {
            h.Put(s);
        }
        h.Put4((ulong)Stage, Mine, unchecked((ulong)(long)Slot), unchecked((ulong)(long)Carry));
        h.Put4(unchecked((ulong)(long)Work), At, unchecked((ulong)(long)Left), Told ? 1UL : 0);
    }
}

/// <summary>What happened in the gold economy (<see cref="EconomyEvent.Kind"/>).</summary>
public enum EconomyEventKind : byte
{
    /// <summary>
    /// Production or construction refused for want of gold: <see cref="EconomyEvent.Unit"/> the factory (0 for a building site),
    /// <see cref="EconomyEvent.Other"/> the unit definition's index (<c>CompiledMatch.Defs</c>), <see cref="EconomyEvent.Amount"/>
    /// the gold short.
    /// </summary>
    NotEnoughGold,

    /// <summary>A miner put gold into a hauling truck (<see cref="EconomyEvent.Other"/>) at the mine.</summary>
    LoadDropped,

    /// <summary>Gold went into the player's reserve at bank <see cref="EconomyEvent.Other"/>, from a truck or a miner on foot.</summary>
    GoldBanked,

    /// <summary>Mine <see cref="EconomyEvent.Unit"/> ran dry: one for each player with miners or trucks on it.</summary>
    MineEmpty,

    /// <summary>A hauling truck's mine ran dry: it went on to mine <see cref="EconomyEvent.Other"/>, or home (0).</summary>
    HaulMoved,

    /// <summary>A unit with gold to deliver has no bank to take it to; it waits (once a wait).</summary>
    NoBank,

    /// <summary>A Keep stocked truck bought <see cref="EconomyEvent.Amount"/> supply at garage <see cref="EconomyEvent.Other"/>.</summary>
    SupplyBought,

    /// <summary>A Keep stocked truck waits at garage <see cref="EconomyEvent.Other"/>: <see cref="EconomyEvent.Amount"/> gold short (once a wait).</summary>
    ShortOfGold,
}

/// <summary>A gold economy event of the last tick (<see cref="World.EconomyEvents"/>), for the HUD and renderers.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Player">The player it concerns.</param>
/// <param name="Unit">The miner, truck, factory or mine.</param>
/// <param name="Other">The truck, bank, mine, garage or definition it went with, or 0.</param>
/// <param name="Amount">The gold (or supply) it moved, or the gold short.</param>
public readonly record struct EconomyEvent(EconomyEventKind Kind, byte Player, uint Unit, uint Other, int Amount);

/// <summary>A gold mine as the client shows it (<see cref="World.MineStatus"/>).</summary>
/// <param name="GoldLeft">Gold left.</param>
/// <param name="Gold">Gold it started with.</param>
/// <param name="PercentLeft">Gold left in percent of its start, rounded up (0 only when dry).</param>
/// <param name="Stage">Its depletion look: 0 above two thirds left, 1 above a third, 2 above none, 3 dry (a ruin).</param>
/// <param name="Miners">Work spots held now.</param>
/// <param name="Slots">Its work spots.</param>
public readonly record struct MineView(int GoldLeft, int Gold, int PercentLeft, int Stage, int Miners, int Slots);
