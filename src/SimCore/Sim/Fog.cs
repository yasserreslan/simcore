using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// One player's view of the map under fog of war, per cell: visible (at least one of its units
/// sees the cell this tick) and explored (it was ever visible). The renderer and the future
/// minimap read it; the sim reads only the visible counts (to spot units), never explored.
/// Not hashed: the grid is a pure function of the units' positions and the terrain, and what
/// it decides (<see cref="Unit.SpottedBy"/>) is hashed.
/// </summary>
public sealed class FogGrid
{
    private readonly ushort[] _count;
    private readonly byte[] _explored;

    internal FogGrid(byte player, int width, int height)
    {
        Player = player;
        Width = width;
        Height = height;
        _count = new ushort[width * height];
        _explored = new byte[width * height];
        ClearDirty();
    }

    /// <summary>The player whose view this is.</summary>
    public byte Player { get; }

    /// <summary>The width in cells.</summary>
    public int Width { get; }

    /// <summary>The height in cells.</summary>
    public int Height { get; }

    /// <summary>How many of the player's viewers see each cell, row by row (0: not visible now).</summary>
    public ReadOnlySpan<ushort> Counts => _count;

    /// <summary>1 for every cell the player has ever seen, row by row, else 0.</summary>
    public ReadOnlySpan<byte> ExploredCells => _explored;

    /// <summary>Bumped on every tick some cell turned visible, hidden or explored: redraw when it changes.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// The cells changed since <see cref="ClearDirty"/>, as an inclusive box (empty when X0 &gt; X1).
    /// For renderers uploading only what changed; not game state.
    /// </summary>
    public (int X0, int Y0, int X1, int Y1) Dirty { get; private set; }

    /// <summary>Forgets the dirty box (the renderer has drawn it).</summary>
    public void ClearDirty() => Dirty = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);

    /// <summary>Whether the player sees cell (x, y) now; false off the map.</summary>
    public bool Visible(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && _count[y * Width + x] != 0;

    /// <summary>Whether the player has ever seen cell (x, y); false off the map.</summary>
    public bool Explored(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && _explored[y * Width + x] != 0;

    internal bool VisibleAt(int i) => _count[i] != 0;

    private bool _changed;

    internal void Add(int i)
    {
        if (_count[i]++ == 0)
        {
            _explored[i] = 1;
            Touch(i);
        }
    }

    internal void Remove(int i)
    {
        if (--_count[i] == 0)
        {
            Touch(i);
        }
    }

    private void Touch(int i)
    {
        _changed = true;
        int x = i % Width, y = i / Width;
        var (x0, y0, x1, y1) = Dirty;
        Dirty = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
    }

    internal void EndTick()
    {
        if (_changed)
        {
            Version++;
            _changed = false;
        }
    }
}

// Fog of war (MatchSetup.Fog). Every unit that sees (vision > 0, built, not riding inside a
// vehicle) is a viewer: the cells it sees, a disc of its vision radius cut by line of sight,
// are stamped into its owner's FogGrid (+1 per viewer). A viewer is swept again only when its
// key changes (its cell, owner, radius or eye), so a standing army costs nothing; when the
// terrain changes (a forest burnt down, a pad) every viewer is swept again.
//
// Line of sight (ground viewers): rays from the eye cell to every cell on the edge of the
// viewer's box, stepped one cell at a time. Along a ray a cell is seen when the line from the eye
// to a target standing in it (ground + TargetRaw) is at least as steep as the steepest blocker
// before it (ground + canopy, compared by cross-multiplying, no division) and the forest crossed
// on the way (ForestSight per cell, the viewer's own cell not counted) is under ForestBudget. A
// cell is seen when any ray sees it. Air viewers (drones) see the whole disc: ridges and woods
// do not block them. Integers only, every loop in a fixed order.
public sealed partial class World
{
    private FogGrid[]? _fog; // by player ID (index 0 unused); null without fog
    private List<Viewer> _viewers = [], _viewersNext = []; // sorted by unit ID
    private int _fogTerrain = -1; // the terrain version the cell tables were built for
    private int[] _sightGround = [], _sightBlock = []; // per cell: ground at the centre, and plus canopy
    private byte[] _sightForest = [];

    /// <summary>Whether this match plays with fog of war (<see cref="MatchSetup.Fog"/>).</summary>
    public bool Fog => _fog is not null;

    /// <summary>The player's fog of war view, or null when the match has no fog (everything is visible).</summary>
    public FogGrid? FogOf(byte player) => _fog is { } f && player >= 1 && player < f.Length ? f[player] : null;

    private void InitFog()
    {
        if (!Match.Setup.Fog)
        {
            return;
        }
        _fog = new FogGrid[_players.Count + 1];
        for (var p = 1; p <= _players.Count; p++)
        {
            _fog[p] = new FogGrid((byte)p, Grid.Width, Grid.Height);
        }
    }

    private VisionRules SightRules => Ground?.Rules.Vision ?? VisionRules.Open;

    // A viewer: the unit's key when it was last swept and the cells it stamped (a bit mask over its box).
    private sealed class Viewer
    {
        public uint Id;
        public byte Owner;
        public int Cx, Cy, Rc; // eye cell, box half-size in cells
        public long R2; // vision radius squared, raw
        public long Eye; // eye height above the eye cell's ground, raw (-1: an air viewer, no line of sight)
        public ulong[] Mask = [];

        public bool Same(byte owner, int cx, int cy, long r2, long eye) =>
            Owner == owner && Cx == cx && Cy == cy && R2 == r2 && Eye == eye;
    }

    // RunFog brings every viewer up to date, then each player's grid is current for this tick.
    private void RunFog(FogGrid[] fog)
    {
        var resweep = false;
        var version = Ground?.Version ?? 0;
        if (version != _fogTerrain)
        {
            BuildSightTables();
            _fogTerrain = version;
            resweep = true;
        }
        var next = _viewersNext;
        next.Clear();
        var vi = 0;
        foreach (var u in _units)
        {
            while (vi < _viewers.Count && _viewers[vi].Id < u.Id)
            {
                Unstamp(fog, _viewers[vi++]); // its unit is gone
            }
            var old = vi < _viewers.Count && _viewers[vi].Id == u.Id ? _viewers[vi++] : null;
            if (!ViewerKey(u, out var cx, out var cy, out var r2, out var eye))
            {
                if (old is not null)
                {
                    Unstamp(fog, old);
                }
                continue;
            }
            if (old is not null && !resweep && old.Same(u.Owner, cx, cy, r2, eye))
            {
                next.Add(old);
                continue;
            }
            if (old is not null)
            {
                Unstamp(fog, old);
            }
            var v = old ?? new Viewer { Id = u.Id };
            (v.Owner, v.Cx, v.Cy, v.R2, v.Eye) = (u.Owner, cx, cy, r2, eye);
            Sweep(v);
            Stamp(fog, v);
            next.Add(v);
        }
        while (vi < _viewers.Count)
        {
            Unstamp(fog, _viewers[vi++]);
        }
        (_viewers, _viewersNext) = (next, _viewers);
        for (var p = 1; p < fog.Length; p++)
        {
            fog[p].EndTick();
        }
    }

    // ViewerKey reports whether u is a viewer now and, if so, its eye cell, vision radius (squared,
    // raw) and eye height (raw above the cell's ground; -1 for air viewers).
    private bool ViewerKey(Unit u, out int cx, out int cy, out long r2, out long eye)
    {
        (cx, cy, r2, eye) = (0, 0, 0, 0);
        if (u.Inside != 0 || !u.Alive || u.Def.Vision.Raw == 0 || !u.IsBuilt || u.Owner == 0 || u.Owner >= _fog!.Length)
        {
            return false;
        }
        var rules = SightRules;
        var r = (u.Def.Vision + u.VisionBonus).Raw;
        if (u.Garrison != 0)
        {
            // A soldier in a building sees from its windows: from the roof, out past the walls.
            r += (long)Math.Max(u.Cover.W, u.Cover.H) * Fixed.OneRaw / 2;
            eye = Unit(u.Garrison) is { } b ? Math.Max(b.Def.Archetype.HeightRaw, rules.EyeFootRaw) : rules.EyeFootRaw;
        }
        else if (u.Def.IsStructure)
        {
            r += (long)Math.Max(u.Def.Archetype.FootprintW, u.Def.Archetype.FootprintH) * Fixed.OneRaw / 2;
            eye = Math.Max(u.Def.Archetype.HeightRaw, rules.EyeVehicleRaw);
        }
        else if (u.Def.Layer == Layers.Air)
        {
            eye = -1;
        }
        else
        {
            eye = u.Def.Archetype.Locomotor == Locomotor.Foot ? rules.EyeFootRaw : rules.EyeVehicleRaw;
        }
        var c = Cell.Of(u.Pos);
        cx = Math.Clamp(c.X, 0, Grid.Width - 1);
        cy = Math.Clamp(c.Y, 0, Grid.Height - 1);
        r2 = r * r;
        return true;
    }

    private void BuildSightTables()
    {
        var n = Grid.Width * Grid.Height;
        if (_sightGround.Length != n)
        {
            _sightGround = new int[n];
            _sightBlock = new int[n];
            _sightForest = new byte[n];
        }
        if (Ground is not { } g)
        {
            return; // flat: all zero, nothing blocks
        }
        var rules = SightRules;
        for (var y = 0; y < Grid.Height; y++)
        {
            for (var x = 0; x < Grid.Width; x++)
            {
                var i = y * Grid.Width + x;
                var c = new Cell(x, y);
                var ground = (int)g.HeightAt(c.Center()).Raw;
                var forest = g.Forest(c);
                _sightGround[i] = ground;
                _sightBlock[i] = ground + rules.CanopyRaw[forest];
                _sightForest[i] = (byte)Math.Min(rules.ForestSight[forest], 255);
            }
        }
    }

    // Sweep fills v.Mask with the cells v sees (its box is (2 Rc + 1)^2, row by row).
    private void Sweep(Viewer v)
    {
        var rc = (int)((Isqrt(v.R2) + Fixed.OneRaw - 1) / Fixed.OneRaw);
        var side = 2 * rc + 1;
        var words = (side * side + 63) / 64;
        if (v.Mask.Length != words)
        {
            v.Mask = new ulong[words];
        }
        else
        {
            Array.Clear(v.Mask);
        }
        v.Rc = rc;
        Mark(v, 0, 0);
        const long cellSq = (long)Fixed.OneRaw * Fixed.OneRaw;
        if (v.Eye < 0 || Ground is null)
        {
            // An air viewer or a flat map: the whole disc.
            for (var dy = -rc; dy <= rc; dy++)
            {
                for (var dx = -rc; dx <= rc; dx++)
                {
                    if (((long)dx * dx + (long)dy * dy) * cellSq <= v.R2 && InMap(v.Cx + dx, v.Cy + dy))
                    {
                        Mark(v, dx, dy);
                    }
                }
            }
            return;
        }
        var rules = SightRules;
        var eye = _sightGround[v.Cy * Grid.Width + v.Cx] + v.Eye;
        // Rays to the box's edge cells, in a fixed order: the top and bottom rows, then the sides.
        for (var px = -rc; px <= rc; px++)
        {
            Ray(v, px, -rc, rc, eye, rules);
            Ray(v, px, rc, rc, eye, rules);
        }
        for (var py = -rc + 1; py <= rc - 1; py++)
        {
            Ray(v, -rc, py, rc, eye, rules);
            Ray(v, rc, py, rc, eye, rules);
        }
    }

    // Ray steps from the eye cell toward edge cell (px, py), n cells, marking what it sees. Two
    // horizons, the steepest (height - eye) / steps so far: the ground's, and the treetops' (ground
    // plus canopy). A target under the ground horizon is behind a crest: hidden. One under the
    // treetops' horizon is behind or among trees: seen only while the forest crossed is under budget.
    private void Ray(Viewer v, int px, int py, int n, long eye, VisionRules rules)
    {
        const long cellSq = (long)Fixed.OneRaw * Fixed.OneRaw;
        long groundNum = 0, groundDen = 0, topNum = 0, topDen = 0; // none yet while the denominator is 0
        var forest = 0;
        for (var i = 1; i <= n; i++)
        {
            var dx = RoundDiv(px * i, n);
            var dy = RoundDiv(py * i, n);
            if (((long)dx * dx + (long)dy * dy) * cellSq > v.R2)
            {
                return; // beyond the vision radius, and the ray only goes farther
            }
            int x = v.Cx + dx, y = v.Cy + dy;
            if (!InMap(x, y))
            {
                return;
            }
            var c = y * Grid.Width + x;
            var target = _sightGround[c] + rules.TargetRaw - eye;
            if ((groundDen == 0 || target * groundDen >= groundNum * i) &&
                (forest < rules.ForestBudget || topDen == 0 || target * topDen >= topNum * i))
            {
                Mark(v, dx, dy);
            }
            var ground = _sightGround[c] - eye;
            if (groundDen == 0 || ground * groundDen > groundNum * i)
            {
                (groundNum, groundDen) = (ground, i);
            }
            var top = _sightBlock[c] - eye;
            if (topDen == 0 || top * topDen > topNum * i)
            {
                (topNum, topDen) = (top, i);
            }
            forest += _sightForest[c];
        }
    }

    // RoundDiv is a / b rounded to the nearest integer, halves away from zero (b > 0).
    private static int RoundDiv(int a, int b) => a >= 0 ? (2 * a + b) / (2 * b) : -((-2 * a + b) / (2 * b));

    // Isqrt is the integer square root, rounded down (Newton's method).
    private static long Isqrt(long v)
    {
        if (v <= 0)
        {
            return 0;
        }
        long x = v, y = (v + 1) / 2;
        while (y < x)
        {
            x = y;
            y = (x + v / x) / 2;
        }
        return x;
    }

    private bool InMap(int x, int y) => x >= 0 && y >= 0 && x < Grid.Width && y < Grid.Height;

    private static void Mark(Viewer v, int dx, int dy)
    {
        var side = 2 * v.Rc + 1;
        var b = (dy + v.Rc) * side + dx + v.Rc;
        v.Mask[b >> 6] |= 1UL << (b & 63);
    }

    private void Stamp(FogGrid[] fog, Viewer v) => Each(v, fog[v.Owner], 1, null);

    private void Unstamp(FogGrid[] fog, Viewer v) => Each(v, fog[v.Owner], -1, null);

    // Each adds delta to every cell v sees: in grid g, or in counts when g is null.
    private void Each(Viewer v, FogGrid? g, int delta, int[]? counts)
    {
        var side = 2 * v.Rc + 1;
        for (var w = 0; w < v.Mask.Length; w++)
        {
            var bits = v.Mask[w];
            while (bits != 0)
            {
                var b = w * 64 + System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                int x = v.Cx + b % side - v.Rc, y = v.Cy + b / side - v.Rc;
                var i = y * Grid.Width + x;
                if (g is null)
                {
                    counts![i] += delta;
                }
                else if (delta > 0)
                {
                    g.Add(i);
                }
                else
                {
                    g.Remove(i);
                }
            }
        }
    }

    // FogSpots reports whether player's grid shows t: its cell, or any cell of a structure's footprint.
    private bool FogSpots(FogGrid g, Unit t)
    {
        if (!t.Def.IsStructure)
        {
            var c = Cell.Of(t.Pos);
            return g.VisibleAt(Math.Clamp(c.Y, 0, Grid.Height - 1) * Grid.Width + Math.Clamp(c.X, 0, Grid.Width - 1));
        }
        var o = FootprintOrigin(t);
        for (var y = Math.Max(o.Y, 0); y < Math.Min(o.Y + t.Def.Archetype.FootprintH, Grid.Height); y++)
        {
            for (var x = Math.Max(o.X, 0); x < Math.Min(o.X + t.Def.Archetype.FootprintW, Grid.Width); x++)
            {
                if (g.VisibleAt(y * Grid.Width + x))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Test and tool hook: brings the grids up to date with the units as they stand (the vision
    /// system does it at the start of a tick), then rebuilds every player's visible counts from
    /// scratch and compares. Changes no game state. Not used by the sim.
    /// </summary>
    internal bool FogMatchesRebuild()
    {
        if (_fog is not { } fog)
        {
            return true;
        }
        RunFog(fog);
        var fresh = new int[fog.Length][];
        for (var p = 1; p < fog.Length; p++)
        {
            fresh[p] = new int[Grid.Width * Grid.Height];
        }
        foreach (var u in _units)
        {
            if (!ViewerKey(u, out var cx, out var cy, out var r2, out var eye))
            {
                continue;
            }
            var v = new Viewer { Id = u.Id, Owner = u.Owner, Cx = cx, Cy = cy, R2 = r2, Eye = eye };
            Sweep(v);
            Each(v, null, 1, fresh[v.Owner]);
        }
        for (var p = 1; p < fog.Length; p++)
        {
            var counts = fog[p].Counts;
            for (var i = 0; i < counts.Length; i++)
            {
                if (counts[i] != fresh[p][i])
                {
                    return false;
                }
            }
        }
        return true;
    }
}
