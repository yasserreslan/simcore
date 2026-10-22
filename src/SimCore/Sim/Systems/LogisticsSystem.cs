using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>What a <see cref="LogisticsEvent"/> reports (logistics: the airlifter, the stores, the cargo trucks).</summary>
public enum LogisticsEventKind : byte
{
    /// <summary>An airdropped pallet landed inside a drop zone: <see cref="LogisticsEvent.Amount"/> went into the store's stock (what did not fit is <see cref="LogisticsEvent.Lost"/>).</summary>
    PalletLanded,

    /// <summary>An airdropped pallet landed outside every drop zone of its side: lost (it burns or lies as debris; no capture yet).</summary>
    PalletLost,

    /// <summary>A landed airlifter unloaded a pallet (container) into its airfield's stock.</summary>
    Unloaded,

    /// <summary>A cargo truck loaded a full load at a store.</summary>
    TruckLoaded,

    /// <summary>A cargo truck unloaded its load into a store.</summary>
    TruckUnloaded,

    /// <summary>An aircraft died with pallets aboard: their cargo is lost.</summary>
    CargoLost,
}

/// <summary>
/// A logistics event of the last tick (<see cref="World.LogisticsEvents"/>), for renderers and the HUD.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Unit">The aircraft or truck (a pallet's: the aircraft that dropped it).</param>
/// <param name="Store">The store (a structure with a stock; an airfield's: its core), or 0.</param>
/// <param name="At">Where (a pallet: where it landed).</param>
/// <param name="Amount">The supply that went into the store (or out of it, a truck's load).</param>
/// <param name="Lost">The supply lost (a pallet outside every zone, the part of a pallet that did not fit, a shot-down aircraft's cargo).</param>
/// <param name="Pallet">A pallet's projectile ID (its fall is in <see cref="World.Projectiles"/> and <see cref="World.BombReleases"/>), or 0.</param>
public readonly record struct LogisticsEvent(LogisticsEventKind Kind, uint Unit, uint Store, Vec At, int Amount, int Lost, uint Pallet);

/// <summary>A store's stock (<see cref="World.StockOf"/>): what it holds and the most it holds (0: unlimited).</summary>
/// <param name="Stock">Supply in it now.</param>
/// <param name="Max">Its cap; 0 means unlimited (a main air base).</param>
public readonly record struct StoreStock(int Stock, int Max);

// Logistics (the airlifter, the field ammo point, the supply stash and the forward airbase; docs/design.md, "Logistics"): the
// shared queries and the events. The supply rules (stores, the ammo point's rearm, the trucks' loads) are in SupplySystem.cs,
// the airdrop in AirStrikeSystem.cs (BombBlast) and AirdropSystem.cs, the airfield stock's draws in AirfieldServiceSystem.cs and
// ServiceSystem.cs.
public sealed partial class World
{
    private readonly List<LogisticsEvent> _logisticsEvents = [];

    /// <summary>The logistics events of the last tick, in order (pallets landed or lost, unloads, truck loads, cargo lost).</summary>
    public IReadOnlyList<LogisticsEvent> LogisticsEvents => _logisticsEvents;

    /// <summary>
    /// The drop zone of structure s (a field ammo point or a supply stash; content <c>dropZone</c>): its centre on the map and its
    /// radius; null for anything without one.
    /// </summary>
    public (Vec Centre, Fixed Radius)? DropZoneOf(Unit s)
    {
        if (s.Def.Archetype.DropZone is not { } dz)
        {
            return null;
        }
        var (x, y) = Turn(s.Facing, (long)dz.OffsetX * Fixed.OneRaw / 10, (long)dz.OffsetY * Fixed.OneRaw / 10);
        return (OnMap(s.Pos.X.Raw + x, s.Pos.Y.Raw + y), dz.Radius);
    }

    /// <summary>
    /// The stock of store s (a supply depot, a field ammo point, a supply stash, an airfield core with a supply block; an airfield
    /// piece: its core's): what it holds and its cap (0: unlimited). Null for a unit without a supply block.
    /// </summary>
    public StoreStock? StockOf(Unit s)
    {
        var store = StoreOf(s);
        return store?.Def.Supply is { } sup ? new StoreStock(store.Stock, sup.Stock) : null;
    }

    /// <summary>The airfield whose core holds a stock that its stations and garage draw on (the forward airbase), else null.</summary>
    public Unit? AirfieldStore(Airfield af) => Unit(af.Core) is { Alive: true, Def.Supply: { Stock: > 0 } } core ? core : null;

    // StoreOf is the unit holding s's stock: s itself with a supply block, else its airfield's core with one, else null.
    private Unit? StoreOf(Unit s)
    {
        if (s.Def.Supply is not null)
        {
            return s;
        }
        return AirfieldOf(s) is { } af && Unit(af.Core) is { Def.Supply: not null } core ? core : null;
    }

    // ClearLogisticsEvents starts a tick's logistics reports.
    private void ClearLogisticsEvents() => _logisticsEvents.Clear();
}
