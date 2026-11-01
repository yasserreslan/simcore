using SimCore.FixedPoint;

namespace SimCore.Sim;

// Sight blockers (fog of war only; docs/design.md, "Sight blockers"): two layers of one byte a cell, the height over the
// ground that blocks sight there in half metres: the tallest standing structure's top (its archetype's sight height:
// content sightHeight, else height) and the tallest smoke screen over the cell (SmokeCloud.Height, a cell counting when
// its centre is inside the screen). The fog's rays (Fog.cs, Ray) add them to the ground horizon. A structure blocks
// while it stands built (not a site, not rubble, not dead), unless it is hidden (it would show as a shadow), pavement or
// a bridge's deck. Neutral town buildings count like any other.
//
// The layers are derived, not state: once a tick (at the start of RunFog) the standing blockers (by unit ID) and the
// screens (by ID) are compared with those stamped last tick; a new or raised one is stamped (the cells' max), a gone or
// lowered one has its box rebuilt from every blocker over it. The boxes of the cells that changed are the tick's blocker
// journal: only the viewers whose box reaches one are swept again, as with a burnt forest. Nothing built, collapsed or
// smoked: one pass over the structures and screens, no sweep. Without fog none of this runs. Not hashed (a function of
// hashed units and screens and of content); FogMatchesRebuild rebuilds both layers from scratch and compares.
public sealed partial class World
{
    private const int HalfMetreRaw = 256; // raw height (512 a metre) of one step of the layers
    private const int BlockTileShift = 4; // the tiles that count blocker cells: 16 x 16 cells

    // A standing structure or a screen as stamped: its box of cells (inclusive) and its height in half metres. A screen
    // also keeps its centre and radius squared (raw) for the disc test.
    private readonly record struct BlockStamp(uint Id, int X0, int Y0, int X1, int Y1, byte H, long Cx = 0, long Cy = 0, long R2 = 0);

    private byte[]? _blockStruct, _blockSmoke; // per cell, row by row; null until fog's first tick
    private ushort[] _blockTiles = []; // per tile: how many of its cells have a blocker in either layer
    private int _blockTilesW;
    private List<BlockStamp> _blockers = [], _blockersNext = []; // standing structures that block, by unit ID
    private List<BlockStamp> _smokeStamps = [], _smokeStampsNext = []; // screens, by ID
    private readonly List<(int X0, int Y0, int X1, int Y1)> _blockChanged = []; // the boxes of cells whose blocker changed this tick
    private readonly List<BlockStamp> _blockLowered = [], _blockRaised = []; // scratch for one layer's update
    private byte[] _blockScratch = [];

    /// <summary>
    /// The sight blocker over cell (x, y) under fog of war, in half metres over the ground: the taller of the structure
    /// standing there and the smoke screen over it (0: none, off the map, or a match without fog). For views and tools.
    /// </summary>
    public int SightBlocker(int x, int y)
    {
        if (_blockStruct is not { } s || _blockSmoke is not { } m || !InMap(x, y))
        {
            return 0;
        }
        var i = y * Grid.Width + x;
        return Math.Max(s[i], m[i]);
    }

    // StructureBlock is how high u blocks sight now, in half metres (0: not at all).
    private static int StructureBlock(Unit u)
    {
        var a = u.Def.Archetype;
        if (!a.IsStructure || !u.Alive || !u.IsBuilt || u.Rubble || a.Hidden || a.Pavement || a.Bridge is not null)
        {
            return 0;
        }
        return (int)Math.Min(255, a.SightBlockRaw / HalfMetreRaw);
    }

    // StandingBlockers lists the structures that block sight now, by ID, into list.
    private void StandingBlockers(List<BlockStamp> list)
    {
        list.Clear();
        foreach (var u in _units)
        {
            if (!u.Def.IsStructure)
            {
                continue;
            }
            var h = StructureBlock(u);
            if (h == 0)
            {
                continue;
            }
            var o = FootprintOrigin(u);
            list.Add(new BlockStamp(u.Id, o.X, o.Y, o.X + FootW(u) - 1, o.Y + FootH(u) - 1, (byte)h));
        }
    }

    // SmokeBlockers lists the screens, by ID, into list.
    private void SmokeBlockers(List<BlockStamp> list)
    {
        list.Clear();
        foreach (var s in _smokes)
        {
            var h = (int)Math.Min(255, s.Height.Raw / HalfMetreRaw);
            var r = s.Radius.Raw;
            if (h == 0 || r <= 0)
            {
                continue;
            }
            long cx = s.At.X.Raw, cy = s.At.Y.Raw;
            list.Add(new BlockStamp(s.Id, (int)((cx - r) >> Fixed.Shift), (int)((cy - r) >> Fixed.Shift),
                (int)((cx + r) >> Fixed.Shift), (int)((cy + r) >> Fixed.Shift), (byte)h, cx, cy, r * r));
        }
    }

    // UpdateBlockers brings both layers up to date with the structures and screens standing now and fills the tick's
    // blocker journal (_blockChanged).
    private void UpdateBlockers()
    {
        _blockChanged.Clear();
        if (_blockStruct is null)
        {
            var n = Grid.Width * Grid.Height;
            (_blockStruct, _blockSmoke) = (new byte[n], new byte[n]);
            _blockTilesW = (Grid.Width + (1 << BlockTileShift) - 1) >> BlockTileShift;
            _blockTiles = new ushort[_blockTilesW * ((Grid.Height + (1 << BlockTileShift) - 1) >> BlockTileShift)];
        }
        StandingBlockers(_blockersNext);
        UpdateLayer(_blockStruct, _blockSmoke!, ref _blockers, ref _blockersNext);
        SmokeBlockers(_smokeStampsNext);
        UpdateLayer(_blockSmoke!, _blockStruct, ref _smokeStamps, ref _smokeStampsNext);
    }

    // UpdateLayer compares the stamps of last tick (old, by ID) with now's (next) and brings layer up to date: a gone or
    // lowered stamp's box rebuilt, then a new or raised one stamped. other is the other layer (for the tile counts).
    private void UpdateLayer(byte[] layer, byte[] other, ref List<BlockStamp> old, ref List<BlockStamp> next)
    {
        _blockLowered.Clear();
        _blockRaised.Clear();
        int i = 0, j = 0;
        while (i < old.Count || j < next.Count)
        {
            if (j >= next.Count || (i < old.Count && old[i].Id < next[j].Id))
            {
                _blockLowered.Add(old[i++]);
            }
            else if (i >= old.Count || next[j].Id < old[i].Id)
            {
                _blockRaised.Add(next[j++]);
            }
            else
            {
                if (old[i] != next[j])
                {
                    _blockLowered.Add(old[i]);
                    _blockRaised.Add(next[j]);
                }
                (i, j) = (i + 1, j + 1);
            }
        }
        (old, next) = (next, old);
        foreach (var s in _blockLowered)
        {
            RebuildBlocks(layer, other, old, s);
        }
        foreach (var s in _blockRaised)
        {
            RaiseBlocks(layer, other, s);
        }
    }

    // RaiseBlocks stamps s into layer (each cell the higher of the two) and journals the cells it raised.
    private void RaiseBlocks(byte[] layer, byte[] other, BlockStamp s)
    {
        var (x0, y0, x1, y1) = Clip(s);
        (int X0, int Y0, int X1, int Y1) box = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var c = y * Grid.Width + x;
                if (layer[c] < s.H && Covers(s, x, y))
                {
                    SetBlock(layer, other, c, s.H, x, y, ref box);
                }
            }
        }
        Journal(box);
    }

    // RebuildBlocks recomputes layer over the box of a gone or lowered stamp gone from every stamp now (stamps), and
    // journals the cells that changed.
    private void RebuildBlocks(byte[] layer, byte[] other, List<BlockStamp> stamps, BlockStamp gone)
    {
        var (x0, y0, x1, y1) = Clip(gone);
        if (x0 > x1 || y0 > y1)
        {
            return;
        }
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        if (_blockScratch.Length < w * h)
        {
            _blockScratch = new byte[w * h];
        }
        var fresh = _blockScratch;
        Array.Clear(fresh, 0, w * h);
        foreach (var s in stamps)
        {
            if (s.X1 < x0 || s.X0 > x1 || s.Y1 < y0 || s.Y0 > y1)
            {
                continue;
            }
            for (var y = Math.Max(y0, s.Y0); y <= Math.Min(y1, s.Y1); y++)
            {
                for (var x = Math.Max(x0, s.X0); x <= Math.Min(x1, s.X1); x++)
                {
                    var k = (y - y0) * w + x - x0;
                    if (fresh[k] < s.H && Covers(s, x, y))
                    {
                        fresh[k] = s.H;
                    }
                }
            }
        }
        (int X0, int Y0, int X1, int Y1) box = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var c = y * Grid.Width + x;
                var v = fresh[(y - y0) * w + x - x0];
                if (layer[c] != v)
                {
                    SetBlock(layer, other, c, v, x, y, ref box);
                }
            }
        }
        Journal(box);
    }

    // SetBlock sets one cell of layer, keeping the tile counts, and grows box round it.
    private void SetBlock(byte[] layer, byte[] other, int c, byte v, int x, int y, ref (int X0, int Y0, int X1, int Y1) box)
    {
        var was = layer[c] != 0 || other[c] != 0;
        layer[c] = v;
        var now = v != 0 || other[c] != 0;
        if (was != now)
        {
            var t = (y >> BlockTileShift) * _blockTilesW + (x >> BlockTileShift);
            _blockTiles[t] = (ushort)(now ? _blockTiles[t] + 1 : _blockTiles[t] - 1);
        }
        box = (Math.Min(box.X0, x), Math.Min(box.Y0, y), Math.Max(box.X1, x), Math.Max(box.Y1, y));
    }

    private void Journal((int X0, int Y0, int X1, int Y1) box)
    {
        if (box.X0 <= box.X1)
        {
            _blockChanged.Add(box);
        }
    }

    // Clip is a stamp's box clipped to the map (empty: X0 > X1 or Y0 > Y1).
    private (int X0, int Y0, int X1, int Y1) Clip(BlockStamp s) =>
        (Math.Max(s.X0, 0), Math.Max(s.Y0, 0), Math.Min(s.X1, Grid.Width - 1), Math.Min(s.Y1, Grid.Height - 1));

    // Covers reports whether a stamp covers cell (x, y) of its box: every cell of a structure's footprint, the cells of
    // a screen whose centre is inside it.
    private static bool Covers(BlockStamp s, int x, int y)
    {
        if (s.R2 == 0)
        {
            return true;
        }
        var dx = ((long)x << Fixed.Shift) + Fixed.OneRaw / 2 - s.Cx;
        var dy = ((long)y << Fixed.Shift) + Fixed.OneRaw / 2 - s.Cy;
        return dx * dx + dy * dy <= s.R2;
    }

    // BlockersIn reports whether any cell of the box (clipped to the map) has a blocker in either layer (by tiles: a
    // tile of the box's with one counts).
    private bool BlockersIn(int x0, int y0, int x1, int y1)
    {
        if (_blockStruct is null)
        {
            return false;
        }
        x0 = Math.Max(x0, 0) >> BlockTileShift;
        y0 = Math.Max(y0, 0) >> BlockTileShift;
        x1 = Math.Min(x1, Grid.Width - 1) >> BlockTileShift;
        y1 = Math.Min(y1, Grid.Height - 1) >> BlockTileShift;
        for (var ty = y0; ty <= y1; ty++)
        {
            for (var tx = x0; tx <= x1; tx++)
            {
                if (_blockTiles[ty * _blockTilesW + tx] != 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ReachesBlockChange reports whether a viewer's box holds a cell whose blocker changed this tick.
    private bool ReachesBlockChange(Viewer v)
    {
        foreach (var b in _blockChanged)
        {
            if (Reaches(v, b))
            {
                return true;
            }
        }
        return false;
    }

    // BlockersMatchRebuild rebuilds both layers and the tile counts from scratch and compares (FogMatchesRebuild).
    private bool BlockersMatchRebuild()
    {
        if (_blockStruct is not { } st || _blockSmoke is not { } sm)
        {
            return true;
        }
        var n = Grid.Width * Grid.Height;
        byte[] freshStruct = new byte[n], freshSmoke = new byte[n];
        var stamps = new List<BlockStamp>();
        StandingBlockers(stamps);
        StampAll(freshStruct, stamps);
        SmokeBlockers(stamps);
        StampAll(freshSmoke, stamps);
        var tiles = new ushort[_blockTiles.Length];
        for (var c = 0; c < n; c++)
        {
            if (freshStruct[c] != st[c] || freshSmoke[c] != sm[c])
            {
                return false;
            }
            if (st[c] != 0 || sm[c] != 0)
            {
                int x = c % Grid.Width, y = c / Grid.Width;
                tiles[(y >> BlockTileShift) * _blockTilesW + (x >> BlockTileShift)]++;
            }
        }
        return tiles.AsSpan().SequenceEqual(_blockTiles);
    }

    private void StampAll(byte[] layer, List<BlockStamp> stamps)
    {
        foreach (var s in stamps)
        {
            var (x0, y0, x1, y1) = Clip(s);
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var c = y * Grid.Width + x;
                    if (layer[c] < s.H && Covers(s, x, y))
                    {
                        layer[c] = s.H;
                    }
                }
            }
        }
    }
}
