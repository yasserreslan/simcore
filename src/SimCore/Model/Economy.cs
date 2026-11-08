namespace SimCore.Model;

// The gold economy's content (docs/design.md, "Gold economy"; the game repo's docs/plans/economy-spec.md, owner 2026-11-08): the
// gold mines (neutral map features), the banks (each faction's gold drop-off), who mines and how fast, and the cargo truck's
// Haul and Keep stocked orders. Points are in tenths of a cell from the footprint's unturned top-left corner (x east, y south:
// the front as authored faces south), as the art's sidecars give them (glTF metres from the footprint's centre, x along X,
// y along Z, times 5).

/// <summary>
/// A gold mine (content <c>mine</c>): the gold it holds, one work spot per miner slot, the spot a hauling truck parks on, and
/// the cells its art makes solid (they block movement for good; the rest of its footprint is open ground).
/// </summary>
/// <param name="Gold">The gold it holds when the match starts.</param>
/// <param name="Work">The work spots, one per miner slot, in slot order.</param>
/// <param name="Load">Where a hauling truck parks (its centre).</param>
/// <param name="Solid">The layout cells (x, y from the unturned top-left cell) that block, in row order.</param>
public sealed record MineSpec(int Gold, IReadOnlyList<(int X, int Y)> Work, (int X, int Y) Load, IReadOnlyList<(int X, int Y)> Solid);

/// <summary>
/// A bank (content <c>bank</c>): where trucks unload gold and where miners on foot hand it in. Both spots are the art's; the bank
/// blocks its whole footprint as any building, so units stop at its front edge in line with them (<see cref="Sim.World.UnloadPoint"/>).
/// </summary>
/// <param name="Unload">The art's unload spot (a truck's centre in the loading bay).</param>
/// <param name="WalkIn">The art's walk-in spot (where a miner on foot hands in gold).</param>
public sealed record BankSpec((int X, int Y) Unload, (int X, int Y) WalkIn);

/// <summary>A unit that mines gold (content <c>mining</c>): its rate and the load it carries.</summary>
/// <param name="GoldPerMinute">Gold mined a minute standing at a work spot (the miner 60, 1 a second).</param>
/// <param name="Load">The gold it carries before it delivers (into a hauling truck at the mine, or on foot to a bank).</param>
public sealed record MiningSpec(int GoldPerMinute, int Load);

/// <summary>A cargo truck's gold hauling and supply buying (content <c>haul</c>).</summary>
/// <param name="Capacity">The gold it carries; it leaves the mine for a bank when full.</param>
/// <param name="UnloadTicks">The ticks it stands at a bank's unload spot before its gold is banked.</param>
/// <param name="SupplyGold">The gold a supply costs, loaded at a vehicle garage on Keep stocked.</param>
public sealed record HaulSpec(int Capacity, int UnloadTicks, int SupplyGold);
