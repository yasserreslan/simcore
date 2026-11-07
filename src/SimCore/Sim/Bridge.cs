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
    internal BridgeState(Cell[] deck, int footW, int footH)
    {
        DeckCells = deck;
        AlongY = footH >= footW;
        Traffic = deck.Length > 0;
        if (!Traffic)
        {
            return;
        }
        // The deck's middle, half its length and one cell of ramp each end (the cells beside the water are the tight part too);
        // the footprint's short side is the width.
        int a0 = int.MaxValue, a1 = int.MinValue, c0 = int.MaxValue, c1 = int.MinValue;
        foreach (var c in deck)
        {
            var (along, across) = AlongY ? (c.Y, c.X) : (c.X, c.Y);
            (a0, a1) = (Math.Min(a0, along), Math.Max(a1, along));
            (c0, c1) = (Math.Min(c0, across), Math.Max(c1, across));
        }
        Mid = (a0 + a1 + 1L) * Fixed.OneRaw / 2;
        HalfLength = (a1 - a0 + 1L) * Fixed.OneRaw / 2 + Fixed.OneRaw;
        Cross = (c0 + c1 + 1L) * Fixed.OneRaw / 2;
        Width = (long)Math.Min(footW, footH) * Fixed.OneRaw;
        HalfWidth = Width / 2;
    }

    /// <summary>
    /// The footprint's cells over water, row by row: the deck while the span stands (decked, <see cref="TerrainMap.Deck"/>),
    /// open water while it is down. Its other cells (the ramps and abutments on the banks) are ground either way.
    /// </summary>
    public IReadOnlyList<Cell> DeckCells { get; }

    /// <summary>Whether its span is down: the deck is lifted and nothing crosses until builders repair it.</summary>
    public bool Down { get; internal set; }

    /// <summary>Whether builders worked on it this tick (the client shows its repair scaffold).</summary>
    public bool Repairing { get; internal set; }

    // Which way the traffic has the span (BridgeTrafficSystem.cs): +1 along the road, -1 back, 0 no contest; and since when.
    internal sbyte Flow { get; set; }

    internal uint TurnSince { get; set; }

    internal void EndFlow()
    {
        Flow = 0;
        TurnSince = 0;
    }

    internal void PassTurn(uint tick)
    {
        Flow = (sbyte)-Flow;
        TurnSince = tick;
    }

    // Derived from the deck (not game state): the road's axis and the span in raw units, and this tick's ground units in reach.
    internal readonly List<Unit> Near = [];

    internal bool Traffic { get; }

    internal bool AlongY { get; }

    internal long Mid { get; }

    internal long Cross { get; }

    internal long HalfLength { get; }

    internal long Width { get; }

    internal long HalfWidth { get; }

    internal long Along(Vec p) => (AlongY ? p.Y.Raw : p.X.Raw) - Mid;

    internal long Side(Vec p) => (AlongY ? p.X.Raw : p.Y.Raw) - Cross;

    internal Vec At(long along, long side) => AlongY
        ? new Vec(Fixed.FromRaw(Cross + side), Fixed.FromRaw(Mid + along))
        : new Vec(Fixed.FromRaw(Mid + along), Fixed.FromRaw(Cross + side));

    // The sign of the Side coordinate to the right of someone going dir along the road (right of (x, y) is (-y, x): y grows down).
    internal int Right(int dir) => AlongY ? -dir : dir;

    internal void HashInto(ref WordHash h)
    {
        h.Put(0xB41D6E);
        h.Put(Down ? 1UL : 0);
        h.Put(Repairing ? 1UL : 0);
        if (Flow != 0)
        {
            h.Put((ulong)(long)Flow); // only in a contest: a world without one hashes as before
            h.Put(TurnSince);
        }
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
