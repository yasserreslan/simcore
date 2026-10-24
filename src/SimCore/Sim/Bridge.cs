using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>
/// A bridge a map places (its <c>bridges</c> list; <see cref="TerrainMap.BridgeSites"/>): the archetype <c>bridge_</c> +
/// <see cref="Kind"/> (concrete, stone, metal), the top-left cell of its footprint as it stands, and its facing:
/// <see cref="FixedPoint.Facing.South"/> for a road running north-south (<c>"dir": "ns"</c>, the footprint as the archetype
/// lays it out), <see cref="FixedPoint.Facing.East"/> for east-west (<c>"ew"</c>, turned a quarter).
/// </summary>
public readonly record struct BridgeSite(string Kind, Cell Cell, int Facing)
{
    /// <summary>The archetype ID of a map's bridge kind.</summary>
    public string Archetype => "bridge_" + Kind;
}

/// <summary>
/// A bridge's state (<see cref="Unit.Bridge"/>; docs/design.md, "Bridges"); its damage stage is <see cref="Unit.Stage"/>
/// (0 intact, then damaged, heavy, and the span down at <see cref="Model.BridgeSpec.DownStage"/>).
/// </summary>
public sealed class BridgeState
{
    internal BridgeState(Cell[] deck) => DeckCells = deck;

    /// <summary>
    /// The footprint's cells over water, row by row: the deck while the span stands (decked, <see cref="TerrainMap.Deck"/>),
    /// open water while it is down. Its other cells (the ramps and abutments on the banks) are ground either way.
    /// </summary>
    public IReadOnlyList<Cell> DeckCells { get; }

    /// <summary>Whether its span is down: the deck is lifted and nothing crosses until builders repair it.</summary>
    public bool Down { get; internal set; }

    /// <summary>Whether builders worked on it this tick (the client shows its repair scaffold).</summary>
    public bool Repairing { get; internal set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put(0xB41D6E);
        h.Put(Down ? 1UL : 0);
        h.Put(Repairing ? 1UL : 0);
    }
}

/// <summary>What happened to a bridge this tick (<see cref="BridgeEvent"/>).</summary>
public enum BridgeEventKind : byte
{
    /// <summary>Its damage stage changed while the span stands (damage or repair).</summary>
    Stage,

    /// <summary>Its span fell: the deck is lifted; whoever stood on it went into the water (<see cref="BridgeEvent.Lost"/>).</summary>
    Collapsed,

    /// <summary>Builders brought a fallen span back: the deck is laid again.</summary>
    Reopened,
}

/// <summary>
/// A bridge's change this tick (<see cref="World.BridgeEvents"/>), for renderers: a stage change (dust, debris), a span
/// falling (debris into the water, the big splash) or laid again. Not game state.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Bridge">The bridge's unit ID.</param>
/// <param name="Stage">Its stage now.</param>
/// <param name="At">Its centre.</param>
/// <param name="Lost">A fallen span: the units that went into the water with it (on its deck, and everyone inside them), by ID.</param>
public readonly record struct BridgeEvent(BridgeEventKind Kind, uint Bridge, int Stage, Vec At, IReadOnlyList<uint> Lost);
