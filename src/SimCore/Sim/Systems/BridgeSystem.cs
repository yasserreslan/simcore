using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Bridges (docs/design.md, "Bridges"; the owner, 2026-10-24: destructible, repairable, never built by players, no weight
// limit). A bridge is a neutral structure (owner 0 for good) the map places over water (TerrainMap.BridgeSites, put up when
// the terrain is attached), or the sandbox. Its footprint's long side is the road: every footprint cell carries its road
// kind and its cells over water are its deck (World.SetDeck): flat ground with that road while the span stands. It blocks
// nothing (no box for movement or routing, no grid block; shots and dives pass over it onto what stands on it) but other
// structures. Nobody targets it on his own (it is neutral); players order attacks on it, and blasts (shells, bombs, rockets,
// in the water beside it too) hurt it like a building.
//
// The bridge step runs after the tick's damage and repair, before cleanup, in ID order:
//  - a standing bridge's stage follows its HP (its spec's stages; the heaviest standing stage down to 0 HP);
//  - at 0 HP its span falls: the deck is lifted (open water again; the ramps on the banks stay ground), every ground unit on
//    it goes into the water and dies with everyone inside it (nobody swims), units whose way crosses it look for another,
//    and the ruin stays at 1 HP, never removed and no target for orders (blasts still set its repair back);
//  - builders (workers, the bulldozer) repair it with the Repair order (AirfieldDamageSystem.cs), whoever's side they are
//    on; at the spec's reopen percent of max HP a fallen span's deck is laid again.
// Derived lists here (the bridges, the events) are not game state; a bridge's own state is hashed (BridgeState).
public sealed partial class World
{
    private readonly List<Unit> _bridgeList = []; // every bridge, by ID (they are never removed); derived from the units
    private readonly List<BridgeEvent> _bridgeEvents = [];

    /// <summary>Every bridge on the map, standing or fallen (a bridge is never removed), by ID.</summary>
    public IReadOnlyList<Unit> Bridges => _bridgeList;

    /// <summary>This tick's bridge changes (stages, spans falling and laid again), for renderers. Not game state.</summary>
    public IReadOnlyList<BridgeEvent> BridgeEvents => _bridgeEvents;

    /// <summary>The bridge whose footprint holds cell c (its deck or its ramps), or null.</summary>
    public Unit? BridgeAt(Cell c)
    {
        foreach (var b in _bridgeList)
        {
            if (InRect(c, FootprintOrigin(b), FootW(b), FootH(b)))
            {
                return b;
            }
        }
        return null;
    }

    /// <summary>Whether a bridge of archetype a may stand with c as its top-left cell at facing (see <see cref="BridgeRefusal"/>).</summary>
    public bool CanPlaceBridge(Archetype a, Cell c, int facing) => BridgeRefusal(a, c, facing) is null;

    /// <summary>
    /// Why a bridge of archetype a can't stand with c as the top-left cell of its footprint at facing (North or South: the
    /// road runs north-south; East or West: east-west), or null when it can: the terrain's rules
    /// (<see cref="BridgeSiteRefusal"/>), its dry cells free (no rock, no building on the grid) and no structure's footprint
    /// overlapping it. The map's bridges and the sandbox's are checked with it; players never build one.
    /// </summary>
    public string? BridgeRefusal(Archetype a, Cell c, int facing)
    {
        if (Ground is not { } g)
        {
            return "the map has no terrain: no water to span";
        }
        if (BridgeSiteRefusal(g, a, c, facing) is { } why)
        {
            return why;
        }
        var (w, h) = Footprint(a, facing);
        for (var y = c.Y; y < c.Y + h; y++)
        {
            for (var x = c.X; x < c.X + w; x++)
            {
                var cell = new Cell(x, y);
                if (!g.Water(cell) && Grid.At(cell) != Terrain.Clear)
                {
                    return $"cell ({x}, {y}) is blocked (rock or a building)";
                }
            }
        }
        foreach (var u in _units)
        {
            if (u.Def.IsStructure)
            {
                var o = FootprintOrigin(u);
                if (o.X < c.X + w && c.X < o.X + FootW(u) && o.Y < c.Y + h && c.Y < o.Y + FootH(u))
                {
                    return $"it overlaps {u.Def.Archetype.Id} {u.Id}";
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The terrain's rules for a bridge of archetype a with c as the top-left cell of its footprint at facing, or null when
    /// they hold: a bridge archetype, a quarter-turn facing, the footprint on the map, both ends (the rows across the road
    /// at each end of its long side) on dry land, the road's centre line crossing water, and no other bridge's deck there.
    /// Map loaders check a map's bridges with it before any world exists.
    /// </summary>
    public static string? BridgeSiteRefusal(TerrainMap g, Archetype a, Cell c, int facing)
    {
        if (a.Bridge is null)
        {
            return $"{a.Id} is not a bridge";
        }
        if (facing < 0 || facing >= Facing.Count || facing % (Facing.Count / 4) != 0)
        {
            return "a bridge faces north, east, south or west";
        }
        var (w, h) = Footprint(a, facing);
        if (c.X < 0 || c.Y < 0 || c.X + w > g.Width || c.Y + h > g.Height)
        {
            return "its footprint leaves the map";
        }
        var alongY = h > w; // the road runs along the long side
        var across = alongY ? w : h;
        for (var k = 0; k < across; k++)
        {
            var e0 = alongY ? new Cell(c.X + k, c.Y) : new Cell(c.X, c.Y + k);
            var e1 = alongY ? new Cell(c.X + k, c.Y + h - 1) : new Cell(c.X + w - 1, c.Y + k);
            if (g.Water(e0) || g.Water(e1))
            {
                var end = g.Water(e0) ? e0 : e1;
                return $"its end at ({end.X}, {end.Y}) is in the water: both ends stand on dry land";
            }
        }
        var spans = false;
        for (var k = 0; k < (alongY ? h : w) && !spans; k++)
        {
            spans = g.Water(alongY ? new Cell(c.X + w / 2, c.Y + k) : new Cell(c.X + k, c.Y + h / 2));
        }
        if (!spans)
        {
            return "it spans no water (its road's centre line crosses none)";
        }
        for (var y = c.Y; y < c.Y + h; y++)
        {
            for (var x = c.X; x < c.X + w; x++)
            {
                if (g.Deck(new Cell(x, y)))
                {
                    return $"another bridge's deck is at ({x}, {y})";
                }
            }
        }
        return null;
    }

    // SpawnBridge puts up a bridge of def with c as the top-left cell of its footprint at facing (unchecked: BridgeRefusal),
    // neutral for good: its road on every footprint cell (trees cleared), its deck over the water cells, intact.
    internal Unit SpawnBridge(UnitDef def, Cell c, int facing)
    {
        var a = def.Archetype;
        var (w, h) = Footprint(a, facing);
        var b = Add(def, FootprintCenter(c, w, h), facing);
        b.Owner = 0;
        b.Turned = TurnsAt(a, facing);
        var deck = new List<Cell>();
        if (Ground is { } g)
        {
            for (var y = c.Y; y < c.Y + h; y++)
            {
                for (var x = c.X; x < c.X + w; x++)
                {
                    var cell = new Cell(x, y);
                    if (!Grid.Contains(cell))
                    {
                        continue;
                    }
                    g.SetRoadKind(cell, a.Bridge!.Road);
                    if (g.Forest(cell) != 0)
                    {
                        g.SetForest(cell, 0);
                    }
                    if (g.Water(cell))
                    {
                        deck.Add(cell);
                    }
                }
            }
        }
        b.Bridge = new BridgeState([.. deck]);
        foreach (var cell in deck)
        {
            SetDeck(cell, true);
        }
        b.Stage = 0;
        return b;
    }

    // PlaceMapBridges puts up the terrain's bridges (its bridges list, in order) when it is attached: each with the definition
    // of the first player whose faction lists its archetype, after the full placement check.
    private void PlaceMapBridges(TerrainMap terrain)
    {
        for (var i = 0; i < terrain.BridgeSites.Count; i++)
        {
            var s = terrain.BridgeSites[i];
            var where = $"map: bridges[{i}] ({s.Kind} at {s.Cell.X}, {s.Cell.Y})";
            if (Content.Archetype(s.Archetype) is not { Bridge: not null } a)
            {
                throw new MatchSetupException($"{where}: no bridge archetype \"{s.Archetype}\"");
            }
            UnitDef? def = null;
            foreach (var p in Match.Players)
            {
                if (p.Defaults[a.Index] is { } d)
                {
                    def = d;
                    break;
                }
            }
            if (def is null)
            {
                throw new MatchSetupException($"{where}: no faction in the match lists \"{a.Id}\"");
            }
            if (BridgeRefusal(a, s.Cell, s.Facing) is { } why)
            {
                throw new MatchSetupException($"{where}: {why}");
            }
            SpawnBridge(def, s.Cell, s.Facing);
        }
    }

    // RepairsFor reports whether player's builders may repair t: their own structures, and any bridge (neutral, anybody's road).
    private static bool RepairsFor(Unit t, byte player) => t.Owner == player || t.Bridge is not null;

    // RunBridges: stages, spans falling, ruins kept, spans laid again (see the top of the file).
    private void RunBridges()
    {
        foreach (var b in _bridgeList)
        {
            var br = b.Bridge!;
            var spec = b.Def.Archetype.Bridge!;
            if (!br.Down)
            {
                if (b.HP <= 0)
                {
                    Fall(b, br, spec);
                    continue;
                }
                var stage = StageOf(b, spec);
                if (stage != b.Stage)
                {
                    b.Stage = stage;
                    _bridgeEvents.Add(new BridgeEvent(BridgeEventKind.Stage, b.Id, stage, b.Pos, []));
                }
                continue;
            }
            if (b.HP <= 0)
            {
                b.HP = 1; // a ruin is never removed; blasts only set its repair back
            }
            else if ((long)b.HP * 100 >= (long)b.Def.MaxHp * spec.ReopenPercent)
            {
                Reopen(b, br, spec);
            }
        }
    }

    // StageOf is a standing bridge's stage by its HP: i while above Stages[i] percent of max HP, the heaviest below the last.
    private static int StageOf(Unit b, BridgeSpec spec)
    {
        long hp = (long)b.HP * 100, max = b.Def.MaxHp;
        for (var i = 0; i < spec.Stages.Count; i++)
        {
            if (hp > max * spec.Stages[i])
            {
                return i;
            }
        }
        return spec.Stages.Count;
    }

    // Fall brings bridge b's span down: the deck lifted, whoever stands on it lost in the water, ways across it re-planned.
    private void Fall(Unit b, BridgeState br, BridgeSpec spec)
    {
        br.Down = true;
        b.HP = 1;
        b.RepairWork = 0;
        b.Stage = spec.DownStage;
        foreach (var c in br.DeckCells)
        {
            SetDeck(c, false);
        }
        var (o, w, h) = (FootprintOrigin(b), FootW(b), FootH(b));
        var lost = new List<uint>();
        foreach (var u in _units)
        {
            if (u == b || !u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Def.IsStructure ||
                (u.Def.Layer != Layers.Ground && u.Heli is not { Phase: HeliPhase.Landed }))
            {
                continue;
            }
            var at = Cell.Of(u.Pos);
            if (InRect(at, o, w, h) && InWater(at))
            {
                Sink(u, lost);
            }
        }
        Reroute(o, w, h);
        _bridgeEvents.Add(new BridgeEvent(BridgeEventKind.Collapsed, b.Id, b.Stage, b.Pos, lost));
    }

    // Sink: u goes into the water with a fallen span, and everyone inside it (its driver, its riders) with it: nobody swims,
    // nobody bails out (they leave it here, dead, so the ejection has nobody to roll for).
    private void Sink(Unit u, List<uint> lost)
    {
        u.HP = 0;
        lost.Add(u.Id);
        if (u.Def.Archetype.Flight is not null && u.KilledBy == KillCause.None)
        {
            u.KilledBy = KillCause.Blast; // a helicopter landed on the deck
        }
        foreach (var o in _units)
        {
            if (o.Inside == u.Id && o.Alive)
            {
                o.HP = 0;
                o.Inside = 0;
                o.Pos = u.Pos;
                lost.Add(o.Id);
            }
        }
        u.Driver = 0;
    }

    // Reroute cuts the way of every ground unit whose path still crosses the fallen span (the open water in the footprint at o,
    // w x h) short of it, and sends a unit on its own route on a new one (a group member follows its group's flow, re-planned
    // once routing sees the change).
    private void Reroute(Cell o, int w, int h)
    {
        foreach (var u in _units)
        {
            if (!u.Alive || u.Def.Layer != Layers.Ground || u.Def.IsStructure || u.Inside != 0 || !u.Moving)
            {
                continue;
            }
            for (var i = u.PathIdx; i < u.Path.Count; i++)
            {
                var c = u.Path[i];
                if (!InRect(c, o, w, h) || !InWater(c))
                {
                    continue;
                }
                var keep = new Cell[i - u.PathIdx];
                for (var k = 0; k < keep.Length; k++)
                {
                    keep[k] = u.Path[u.PathIdx + k];
                }
                u.SetPath(keep);
                if (u.Mode == RouteMode.Direct)
                {
                    RequestRoute(u, u.Dest);
                }
                break;
            }
        }
    }

    // Reopen lays a repaired span's deck again.
    private void Reopen(Unit b, BridgeState br, BridgeSpec spec)
    {
        br.Down = false;
        foreach (var c in br.DeckCells)
        {
            SetDeck(c, true);
        }
        b.Stage = StageOf(b, spec);
        _bridgeEvents.Add(new BridgeEvent(BridgeEventKind.Reopened, b.Id, b.Stage, b.Pos, []));
    }

    private static bool InRect(Cell c, Cell o, int w, int h) => c.X >= o.X && c.X < o.X + w && c.Y >= o.Y && c.Y < o.Y + h;
}
