using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// The terrain of a map with elevation, roads and forest (a flat map without one has none, and
/// plays and hashes exactly as before). Compact, one array per layer, no per-cell objects:
/// <list type="bullet">
/// <item>heights per cell corner, (W+1) x (H+1) values in the sim's fixed point (raw units, 1024
/// per cell = 2 m, so 512 per metre), 4 bytes each (up to ±256 m: room for mountains);</item>
/// <item>per-cell flags, 1 byte: forest density 0 to 3 (bits 0-1), road (bit 2), building pad (bit 3),
/// the road's kind (bits 4-5, read only with the road bit: 0 paved, 1 highway, 2 gravel, 3 dirt, so
/// a plain road is paved and hashes as before road kinds), water (bit 6: the cell is in a river, lake or sea) and a
/// deck over the water (bit 7: a bridge's deck, <see cref="SetDeck"/>; a decked cell is flat, grade 0);</item>
/// <item>on a map with alleys or footpaths, a second flags byte per cell (<c>_flags2</c>; the first is full): the foot-only road
/// kind in bits 0-1 (0 none, 1 alley, 2 footpath; bits 2-7 are spare for later per-cell flags) and nothing else yet. A cell a
/// vehicle road (the first byte's road bit) covers has no such kind: the vehicle road wins the cell. A map without
/// either takes no second byte and hashes as before;</item>
/// <item>on a map with water, per cell the water body it belongs to (1 byte, 0 for none) and the bodies themselves
/// (<see cref="WaterBodies"/>: kind, surface height, outline), so a map without water takes nothing more;</item>
/// <item>per-cell grade (percent, the largest corner height difference over the cell's width), 1 byte;</item>
/// <item>per movement class, a path cost grid, 1 byte a cell: 0 = impassable, flat open ground 10,
/// with slope, road (by kind), forest and tree trunks folded in. Built when the map loads and rebuilt per cell when a
/// pad, the forest or a trunk changes;</item>
/// <item>on a map whose rules have trunks (<see cref="TerrainRules.Trees"/>), the standing tree trunks, a bit a cell, worked
/// out from the forest when the map loads (<see cref="TreeTrunks"/>; not stored in the map, not in the digest: the world hashes
/// what fell), and from the first fall the fallen trunks lying, a bit a cell (docs/design.md, "Trees as obstacles");</item>
/// <item>from the first fire or the first blast that thins the forest, a bit a cell each for the cells burning and burnt out, the
/// cells the costs treat as burning and whose forest fire dropped (applied in batches), and the cells a blast thinned
/// (docs/design.md, "Forest fire"; the world hashes its fires and blasts, so they stay out of the digest, which reads a burnt or
/// thinned cell's forest as it was before).</item>
/// </list>
/// About 3 + 4 + classes bytes a cell: 1,500 x 1,500 cells with 4 classes take about 25 MB. The
/// cost grids are what the pathfinding milestone (HPA* sectors, the road layer, flow fields)
/// reads; <see cref="StepCost"/> adds the climbing penalty for a directed step.
/// </summary>
public sealed class TerrainMap
{
    /// <summary>The largest corner height either way, raw (256 m: room for mountains; the products in <see cref="HeightAt"/> stay in range).</summary>
    public const int MaxCornerRaw = 256 * 512;

    /// <summary>
    /// The most cells a map has on a side (width and height alike; 16.4 km at 2 m a cell): what the map loader, the
    /// terrain and <see cref="Grid"/> accept. Every cell index (y x width + x, below 2^26), position (raw, 64-bit) and
    /// squared distance across the whole map and its fly zone stays far inside its type there (docs/design.md, "Map size").
    /// </summary>
    public const int MaxSide = 8192;

    /// <summary>Path cost of an impassable cell.</summary>
    public const byte Impassable = 0;

    /// <summary>Path cost of open flat ground (the unit of the cost grids).</summary>
    public const int BaseCost = 10;

    private const byte ForestMask = 0b11, RoadBit = 1 << 2, PadBit = 1 << 3, KindShift = 4, KindMask = 0b11 << KindShift;
    private const byte WaterBit = 1 << 6, DeckBit = 1 << 7;

    // The second flags byte (alleys and footpaths): the foot-only kind in bits 0-1; bits 2-7 are spare.
    private const byte PathMask = 0b11, AlleyCode = 1, FootpathCode = 2;

    // The road kind per kind code in flag bits 4-5 (code 0 is paved: a road without a kind).
    private static readonly RoadKind[] KindOfCode = [RoadKind.Paved, RoadKind.Highway, RoadKind.Gravel, RoadKind.Dirt];

    // Which kind wins where roads overlap: the faster, whatever the order they were laid.
    private static int Rank(RoadKind k) => k switch
    {
        RoadKind.Highway => 4,
        RoadKind.Paved => 3,
        RoadKind.Gravel => 2,
        RoadKind.Dirt => 1,
        _ => 0,
    };

    private static int CodeOf(RoadKind k) => k switch
    {
        RoadKind.Highway => 1,
        RoadKind.Gravel => 2,
        RoadKind.Dirt => 3,
        _ => 0,
    };

    private static RoadKind KindOf(byte flags) => (flags & RoadBit) == 0 ? RoadKind.None : KindOfCode[(flags & KindMask) >> KindShift];

    /// <summary>
    /// A cell's flags with a road of this kind laid on it: where a road is already, the faster
    /// kind of the two stays (highway, paved, gravel, dirt). For map loaders building the flags.
    /// </summary>
    public static byte WithRoad(byte flags, RoadKind kind)
    {
        if (kind is RoadKind.None or RoadKind.Alley or RoadKind.Footpath)
        {
            return flags; // alleys and footpaths live in the second flags byte (WithPathKind)
        }
        if (Rank(KindOf(flags)) >= Rank(kind))
        {
            return flags;
        }
        return (byte)((flags & ~KindMask) | RoadBit | (CodeOf(kind) << KindShift));
    }

    /// <summary>
    /// A cell's second flags byte with an alley or a footpath laid on it: where one is already, the alley stays (it is the
    /// stronger: it shuts the cell to vehicles). Any other kind leaves it as it is. For map loaders building the second byte
    /// (the constructor's <c>flags2</c>), which drops it again where a vehicle road covers the cell.
    /// </summary>
    public static byte WithPathKind(byte flags2, RoadKind kind)
    {
        var code = kind == RoadKind.Alley ? AlleyCode : kind == RoadKind.Footpath ? FootpathCode : 0;
        var have = flags2 & PathMask;
        if (code == 0 || have == AlleyCode || have == code)
        {
            return flags2;
        }
        return (byte)((flags2 & ~PathMask) | code);
    }

    private int _roadKinds; // bit per road kind present on the map (never cleared): the cheapest road cost the pathfinder's heuristic can meet

    private readonly int[] _corners;
    private readonly byte[] _flags;
    private byte[]? _flags2; // the foot-only road kind per cell (alley, footpath), or null: no cell has one
    private readonly byte[] _grade;
    private readonly int[] _centreSum; // per cell: the sum of its four corner heights (raw), kept by Rebuild
    private readonly byte[][] _cost;
    private readonly byte[]? _water; // per cell its water body's ID (1-based), or 0; null on a map without water
    private readonly WaterBody[] _bodies;
    private ulong[]? _trunk;  // a standing tree trunk per cell, a bit each; null: none on the map
    private ulong[]? _fallen; // a fallen trunk lying across the cell, a bit each; null until a tree falls
    private ulong[]? _burning; // the cell is burning (forest fire), a bit each; null until a fire is lit
    private ulong[]? _burnt;   // the cell burnt out (embers, then ash), a bit each; null until one did
    private ulong[]? _fireWall; // the costs treat the cell as burning (the world applies fire to the costs in batches)
    private ulong[]? _dropped;  // fire burnt the cell's forest one level down (applied with the costs), a bit each
    private ulong[]? _thinned; // a blast blew the cell's tree down and it lost one forest level, a bit each; null until one did
    private ulong? _digest;

    /// <summary>A terrain from corner heights (raw fixed point, row by row) and per-cell flags.</summary>
    /// <exception cref="ArgumentException">The arrays do not match the size.</exception>
    public TerrainMap(int width, int height, short[] corners, byte[] flags, TerrainRules rules)
        : this(width, height, Array.ConvertAll(corners, v => (int)v), flags, rules)
    {
    }

    /// <summary>
    /// A terrain from corner heights (raw fixed point, row by row; 512 per metre, kept within
    /// <see cref="MaxCornerRaw"/>) and per-cell flags, with its bodies of water (none: a map without water, exactly as
    /// before). A water cell is every cell whose centre lies in a body or on its edge (a later body over an earlier one
    /// where they overlap); its forest is cleared and no ground class can enter it. The flags' water and deck bits are
    /// ignored (the bodies set the water bit; only <see cref="SetDeck"/> sets a deck). <paramref name="flags2"/> (optional,
    /// width x height) is the second flags byte: the cell's alley or footpath in bits 0-1 (1 alley, 2 footpath; see
    /// <see cref="WithPathKind"/>); a cell with a vehicle road drops it and unknown bits are ignored; none left, no second
    /// byte is kept and the map is exactly as one without.
    /// </summary>
    /// <exception cref="ArgumentException">The arrays do not match the size, a height is out of range, or too many bodies.</exception>
    public TerrainMap(int width, int height, int[] corners, byte[] flags, TerrainRules rules, IReadOnlyList<WaterBody>? water = null, byte[]? flags2 = null)
    {
        if (Array.Exists(corners, v => v < -MaxCornerRaw || v > MaxCornerRaw))
        {
            throw new ArgumentException($"terrain: corner heights want -{MaxCornerRaw} to {MaxCornerRaw} raw (±256 m)");
        }
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
        {
            throw new ArgumentException($"terrain: width and height want 1 to {MaxSide} cells, got {width}x{height}");
        }
        if (corners.Length != (width + 1) * (height + 1) || flags.Length != width * height ||
            (flags2 is not null && flags2.Length != width * height))
        {
            throw new ArgumentException($"terrain: want {width + 1}x{height + 1} corners and {width}x{height} flags");
        }
        Width = width;
        Height = height;
        Rules = rules;
        _bodies = water is { Count: > 0 } ? [.. water] : [];
        if (_bodies.Length > WaterBody.MaxBodies)
        {
            throw new ArgumentException($"terrain: {_bodies.Length} bodies of water, want {WaterBody.MaxBodies} at most");
        }
        if (Array.Exists(_bodies, b => b.Surface.Raw < -MaxCornerRaw || b.Surface.Raw > MaxCornerRaw))
        {
            throw new ArgumentException($"terrain: water surface heights want -{MaxCornerRaw} to {MaxCornerRaw} raw (±256 m)");
        }
        if (_bodies.Length > 0)
        {
            _water = new byte[width * height];
            for (var b = 0; b < _bodies.Length; b++)
            {
                _bodies[b].Paint((byte)(b + 1), width, height, _water);
            }
        }
        for (var i = 0; i < flags.Length; i++)
        {
            flags[i] &= unchecked((byte)~(WaterBit | DeckBit)); // only the bodies make water
            if (_water is not null && _water[i] != 0)
            {
                flags[i] = (byte)((flags[i] & ~ForestMask) | WaterBit); // no trees in the water
            }
            if ((flags[i] & RoadBit) == 0)
            {
                flags[i] &= unchecked((byte)~KindMask); // a kind without a road means nothing
            }
            else
            {
                _roadKinds |= 1 << (int)KindOf(flags[i]);
            }
            if (flags2 is not null)
            {
                var code = flags2[i] & PathMask;
                flags2[i] = (byte)((flags[i] & RoadBit) != 0 || code > FootpathCode ? 0 : code); // a vehicle road wins the cell
                if (flags2[i] != 0)
                {
                    _flags2 = flags2;
                    _roadKinds |= 1 << (int)(flags2[i] == AlleyCode ? RoadKind.Alley : RoadKind.Footpath);
                }
            }
        }
        _corners = corners;
        _flags = flags;
        _grade = new byte[width * height];
        _centreSum = new int[width * height];
        _cost = new byte[rules.Classes.Count][];
        for (var k = 0; k < _cost.Length; k++)
        {
            _cost[k] = new byte[width * height];
        }
        PlantTrunks();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Rebuild(x, y);
            }
        }
        _built = true;
    }

    /// <summary>A flat terrain at height 0 with no roads or forest.</summary>
    public static TerrainMap Flat(int width, int height, TerrainRules rules) =>
        new(width, height, new int[(width + 1) * (height + 1)], new byte[width * height], rules);

    /// <summary>The width in cells.</summary>
    public int Width { get; }

    /// <summary>The height in cells.</summary>
    public int Height { get; }

    /// <summary>The movement classes and their rules.</summary>
    public TerrainRules Rules { get; }

    /// <summary>Counts changes (forest, pads), so caches derived from the layers (not game state) know when to rebuild.</summary>
    public int Version { get; private set; }

    // The cells rebuilt by each change (version, cell, whether sight can differ there: not for a tree trunk standing or lying),
    // oldest first, for renderers catching up.
    private readonly List<(int Version, int Cell, bool Sight)> _journal = [];
    private int _journalFloor; // changes up to this version were dropped
    private bool _built;
    private const int JournalMax = 1 << 16;

    /// <summary>
    /// Adds to cells every cell rebuilt (forest, pads) after version since, and returns true; or
    /// returns false when the changes reach back further than it remembers (redraw everything).
    /// For renderers, not game state.
    /// </summary>
    public bool ChangedSince(int since, List<Cell> cells) => ChangedSince(since, cells, sightOnly: false);

    /// <summary>
    /// <see cref="ChangedSince(int, List{Cell})"/>, with sightOnly only the cells where sight lines may differ: not those whose
    /// tree trunk alone fell or whose fallen trunk came or went (forest density and heights are what sight reads).
    /// </summary>
    internal bool ChangedSince(int since, List<Cell> cells, bool sightOnly)
    {
        if (since >= Version)
        {
            return true;
        }
        if (since < _journalFloor)
        {
            return false;
        }
        foreach (var (v, i, sight) in _journal)
        {
            if (v > since && (sight || !sightOnly))
            {
                cells.Add(new Cell(i % Width, i / Width));
            }
        }
        return true;
    }

    /// <summary>The memory the layers take, in bytes.</summary>
    public long Bytes => (long)_corners.Length * 4 + _flags.Length + _grade.Length + 4L * _centreSum.Length + (long)_cost.Length * _flags.Length +
        (_water?.Length ?? 0) + (_flags2?.Length ?? 0) + 8L * ((_trunk?.Length ?? 0) + (_fallen?.Length ?? 0) +
        (_burning?.Length ?? 0) + (_burnt?.Length ?? 0) + (_thinned?.Length ?? 0) + (_fireWall?.Length ?? 0) +
        (_dropped?.Length ?? 0));

    /// <summary>
    /// Per cell, row by row, the sum of its four corner heights (raw): four times its ground height at the centre, which is
    /// <c>(sum + 2) &gt;&gt; 2</c> (<see cref="GroundHeightAt"/> there, exactly). Routing compares them for climbing steps and fog
    /// reads its sight lines' ground from them; kept up to date whenever a cell is rebuilt. Not game state of its own.
    /// </summary>
    internal int[] CentreSums => _centreSum;

    /// <summary>The flags byte of every cell, row by row (the forest density in bits 0-1): for fog's sight lines.</summary>
    internal byte[] FlagBytes => _flags;

    /// <summary>The water body of every cell (its ID, 0 for none), row by row, or null on a map without water.</summary>
    internal byte[]? WaterIds => _water;

    private bool Contains(Cell c) => c.X >= 0 && c.Y >= 0 && c.X < Width && c.Y < Height;

    private int Index(Cell c) => c.Y * Width + c.X;

    /// <summary>The height of corner (x, y), 0 to Width and 0 to Height (clamped).</summary>
    public Fixed Corner(int x, int y) =>
        Fixed.FromRaw(_corners[Math.Clamp(y, 0, Height) * (Width + 1) + Math.Clamp(x, 0, Width)]);

    /// <summary>
    /// The height of the surface at p: the ground (<see cref="GroundHeightAt"/>), or on a water cell the water's
    /// surface where the ground lies below it. Everything that meets the surface reads this: sight lines and fog,
    /// shells and bombs coming down, wrecks, aircraft heights. Points off the map take the nearest edge.
    /// </summary>
    public Fixed HeightAt(Vec p)
    {
        var ground = GroundHeightAt(p);
        if (_water is null)
        {
            return ground;
        }
        var cx = (int)Math.Min(Math.Clamp(p.X.Raw, 0, (long)Width * Fixed.OneRaw) >> 10, Width - 1);
        var cy = (int)Math.Min(Math.Clamp(p.Y.Raw, 0, (long)Height * Fixed.OneRaw) >> 10, Height - 1);
        var id = _water[cy * Width + cx];
        return id == 0 ? ground : Fixed.FromRaw(Math.Max(ground.Raw, _bodies[id - 1].Surface.Raw));
    }

    /// <summary>
    /// The ground height at p (under water, the bed): bilinear between the four corners of its cell, rounded to the
    /// nearest raw unit (halves up). Points off the map take the nearest edge.
    /// </summary>
    public Fixed GroundHeightAt(Vec p)
    {
        var max = (long)Width * Fixed.OneRaw;
        var maxY = (long)Height * Fixed.OneRaw;
        var px = Math.Clamp(p.X.Raw, 0, max);
        var py = Math.Clamp(p.Y.Raw, 0, maxY);
        var cx = (int)Math.Min(px >> 10, Width - 1);
        var cy = (int)Math.Min(py >> 10, Height - 1);
        long fx = px - ((long)cx << 10), fy = py - ((long)cy << 10);
        long h00 = Corner(cx, cy).Raw, h10 = Corner(cx + 1, cy).Raw, h01 = Corner(cx, cy + 1).Raw, h11 = Corner(cx + 1, cy + 1).Raw;
        var top = h00 * (Fixed.OneRaw - fx) + h10 * fx;
        var bottom = h01 * (Fixed.OneRaw - fx) + h11 * fx;
        var v = top * (Fixed.OneRaw - fy) + bottom * fy; // in raw * 2^20
        return Fixed.FromRaw((v + (1L << 19)) >> 20);
    }

    /// <summary>The grade of a cell in percent (the largest corner height difference over its width); off-map 255.</summary>
    public int Grade(Cell c) => Contains(c) ? _grade[Index(c)] : 255;

    /// <summary>The slope class of a cell for a movement class; off-map Cliff.</summary>
    public SlopeClass Slope(int moveClass, Cell c) => Contains(c) ? Rules.Classes[moveClass].Classify(_grade[Index(c)]) : SlopeClass.Cliff;

    /// <summary>The path cost of a cell for a movement class (0: impassable; flat open ground 10).</summary>
    public int Cost(int moveClass, Cell c) => Contains(c) ? _cost[moveClass][Index(c)] : Impassable;

    /// <summary>The raw cost grid of a movement class, row by row (for the pathfinder).</summary>
    public ReadOnlySpan<byte> CostGrid(int moveClass) => _cost[moveClass];

    /// <summary>Reports whether a movement class can enter a cell.</summary>
    public bool Passable(int moveClass, Cell c) => Cost(moveClass, c) != Impassable;

    /// <summary>
    /// The cost of stepping from one cell into a neighbour: the target cell's cost, plus the
    /// class's climbing penalty when the step goes uphill (centre to centre); 0 when impassable.
    /// </summary>
    public int StepCost(int moveClass, Cell from, Cell to)
    {
        var cost = Cost(moveClass, to);
        if (cost == Impassable)
        {
            return 0;
        }
        if (HeightAt(to.Center()).Raw > HeightAt(from.Center()).Raw)
        {
            cost += cost * Rules.Classes[moveClass].UphillCostPercent / 100;
        }
        return cost;
    }

    /// <summary>The speed percent of a movement class in a cell, going up or not (0: impassable, open water too).</summary>
    public int SpeedPercent(int moveClass, Cell c, bool uphill)
    {
        if (!Contains(c))
        {
            return 0;
        }
        var mc = Rules.Classes[moveClass];
        var forest = _flags[Index(c)] & ForestMask;
        if (mc.ThinnedForestAsBefore)
        {
            forest = ForestBefore(Index(c), forest); // the forest it had before fire or a blast thinned it
        }
        var road = KindAt(Index(c));
        if (forest >= mc.ForestBlockedFrom || OpenWater(c) || (road == RoadKind.Alley && !mc.EntersAlleys))
        {
            return 0; // dense forest it can't enter, open water (nobody swims), or an alley (foot only)
        }
        var slope = (int)mc.Classify(_grade[Index(c)]);
        var pct = (uphill ? mc.SpeedUp[slope] : mc.SpeedDown[slope]) * mc.ForestSpeedPercent[forest] / 100;
        pct = road == RoadKind.None ? pct : pct * mc.RoadSpeedOf(road) / 100;
        if (mc.Trunks != TrunkMode.Ignore && pct > 0)
        {
            var i = Index(c);
            var fallen = FallenAt(i);
            if (fallen || TrunkAt(i))
            {
                // A trunk: a wall to a class that avoids them; a pusher pushes a standing one, or rolls over a fallen one, slowly.
                var pass = fallen ? mc.EntersFallen : mc.Trunks == TrunkMode.Push;
                return pass ? Math.Min(pct, mc.PushSpeedPercent) : 0;
            }
        }
        if (pct > 0 && mc.FireCost == 0 && FireWallAt(Index(c)))
        {
            return 0; // a burning cell is a wall to it
        }
        return pct;
    }

    /// <summary>The forest density of a cell, 0 (none) to 3 (dense).</summary>
    public int Forest(Cell c) => Contains(c) ? _flags[Index(c)] & ForestMask : 0;

    /// <summary>
    /// Whether the map has standing tree trunks (or had: the layer stays once trees were felled), so a renderer can skip
    /// <see cref="HasTrunk"/> on a map without.
    /// </summary>
    public bool HasTrunks => _trunk is not null;

    /// <summary>
    /// Whether a standing tree trunk is in a cell (docs/design.md, "Trees as obstacles"): what vehicles steer between or push
    /// over, and where the client draws a tree (at <see cref="TrunkPoint"/>). False off the map, once the tree is felled or
    /// its forest cleared. When it changes the cell is in <see cref="ChangedSince(int, List{Cell})"/>.
    /// </summary>
    public bool HasTrunk(Cell c) => Contains(c) && TrunkAt(Index(c));

    /// <summary>
    /// Where in its cell a cell's trunk stands (standing or felled): the offset from the cell's centre, raw (each of x and y
    /// -320 to 317, so inside the cell; <see cref="TreeTrunks.Offset"/>). A pure function of the cell.
    /// </summary>
    public static Vec TrunkOffset(Cell c)
    {
        var (ox, oy) = TreeTrunks.Offset(TreeTrunks.SlotHash(c.X / TreeTrunks.SlotSide, c.Y / TreeTrunks.SlotSide));
        return new Vec(Fixed.FromRaw(ox), Fixed.FromRaw(oy));
    }

    /// <summary>Where a cell's trunk stands: its centre plus <see cref="TrunkOffset"/>.</summary>
    public static Vec TrunkPoint(Cell c) => c.Center() + TrunkOffset(c);

    /// <summary>Whether a fallen trunk lies across a cell (<see cref="World.FallenTrunks"/> says which and for how long). False off the map.</summary>
    public bool Fallen(Cell c) => Contains(c) && FallenAt(Index(c));

    internal bool TrunkAt(int i) => _trunk is { } t && ((t[i >> 6] >> (i & 63)) & 1) != 0;

    internal bool FallenAt(int i) => _fallen is { } f && ((f[i >> 6] >> (i & 63)) & 1) != 0;

    internal bool BurningAt(int i) => _burning is { } f && ((f[i >> 6] >> (i & 63)) & 1) != 0;

    internal bool BurntAt(int i) => _burnt is { } f && ((f[i >> 6] >> (i & 63)) & 1) != 0;

    private bool FireWallAt(int i) => _fireWall is { } f && ((f[i >> 6] >> (i & 63)) & 1) != 0;

    private bool DroppedAt(int i) => _dropped is { } f && ((f[i >> 6] >> (i & 63)) & 1) != 0;

    private bool ThinnedAt(int i) => _thinned is { } t && ((t[i >> 6] >> (i & 63)) & 1) != 0;

    // ForestBefore is cell i's forest density before fire burnt it or a blast thinned it (its density now plus the levels it
    // lost, at most dense).
    private int ForestBefore(int i, int forest) => _dropped is null && _thinned is null ? forest
        : Math.Min(forest + (DroppedAt(i) ? 1 : 0) + (ThinnedAt(i) ? 1 : 0), 3);

    /// <summary>
    /// Whether a cell is burning (forest fire: <see cref="World.Fires"/> says since when and how long). False off the map. Its
    /// costs follow within the fire rules' terrain ticks.
    /// </summary>
    public bool Burning(Cell c) => Contains(c) && BurningAt(Index(c));

    /// <summary>
    /// Whether a cell burnt out (embers, then ash; <see cref="World.FireAt"/> says which): it lost one forest level and never
    /// burns again. False off the map.
    /// </summary>
    public bool Burnt(Cell c) => Contains(c) && BurntAt(Index(c));

    /// <summary>Whether a blast blew a cell's tree down and the cell lost one forest level (sight opens there). False off the map.</summary>
    public bool Thinned(Cell c) => Contains(c) && ThinnedAt(Index(c));

    /// <summary>
    /// Whether fire can catch in a cell: on the map, some forest, not open water, not burning and never burnt. The grid's
    /// blocked cells (buildings, rock) are the world's to check.
    /// </summary>
    public bool Burnable(Cell c)
    {
        if (!Contains(c))
        {
            return false;
        }
        var i = Index(c);
        return (_flags[i] & ForestMask) != 0 && (_flags[i] & (WaterBit | DeckBit)) == 0 && !BurningAt(i) && !BurntAt(i);
    }

    /// <summary>
    /// Whether a ground fire can catch in a cell (an incendiary's: <see cref="Model.WarheadFire.GroundTicks"/>): on the map, open
    /// land without forest (a road too), not water or a bridge deck, not burning and never burnt (scorched ground burns once).
    /// The grid's blocked cells (buildings, rock) are the world's to check.
    /// </summary>
    public bool GroundBurnable(Cell c)
    {
        if (!Contains(c))
        {
            return false;
        }
        var i = Index(c);
        return (_flags[i] & ForestMask) == 0 && (_flags[i] & (WaterBit | DeckBit)) == 0 && !OpenWater(c) && !BurningAt(i) && !BurntAt(i);
    }

    /// <summary>
    /// Marks a cell burning (on) or burnt out (off: then it never burns again). Its costs and forest change only when the world
    /// applies the fire to them (<see cref="ApplyFire"/>, in batches). Not in the digest: the world hashes its fires.
    /// </summary>
    internal void SetBurning(Cell c, bool on)
    {
        if (!Contains(c))
        {
            return;
        }
        var i = Index(c);
        SetBit(ref _burning, i, on);
        if (!on)
        {
            SetBit(ref _burnt, i, true);
        }
    }

    /// <summary>
    /// Applies a cell's fire to its costs and forest: lit, a burning cell is a wall or dearer, by class
    /// (<see cref="Model.MoveClass.FireCost"/>; sight does not change: its smoke is the world's); burnt out, the wall is gone and
    /// the cell's forest is one level down (sight opens a little; its standing trunk, if any, stays: a black snag). Not in the
    /// digest, which reads a burnt cell's forest as it was (the world hashes its fires).
    /// </summary>
    internal void ApplyFire(Cell c, bool burntOut)
    {
        if (!Contains(c))
        {
            return;
        }
        var i = Index(c);
        if (!burntOut)
        {
            if (SetBit(ref _fireWall, i, true))
            {
                Rebuild(c.X, c.Y, sight: false);
                Version++;
            }
            return;
        }
        SetBit(ref _fireWall, i, false);
        if ((_flags[i] & ForestMask) != 0 && SetBit(ref _dropped, i, true))
        {
            _flags[i]--; // one forest level down (the density is the low bits)
        }
        Rebuild(c.X, c.Y);
        Version++;
    }

    /// <summary>
    /// Thins a cell whose tree a blast blew down: it loses one forest level, once (later blasts leave it). Reports whether it
    /// did. Not in the digest (the world hashes the blasted cells), which reads the cell's forest as it was.
    /// </summary>
    internal bool Thin(Cell c)
    {
        if (!Contains(c))
        {
            return false;
        }
        var i = Index(c);
        if ((_flags[i] & ForestMask) == 0 || ThinnedAt(i))
        {
            return false;
        }
        SetBit(ref _thinned, i, true);
        _flags[i]--;
        Rebuild(c.X, c.Y);
        Version++;
        return true;
    }

    // SetBit sets or clears cell i's bit in a layer, allocating the layer the first time a bit is set; reports whether it changed.
    private bool SetBit(ref ulong[]? layer, int i, bool on)
    {
        if (layer is null)
        {
            if (!on)
            {
                return false;
            }
            layer = new ulong[((long)Width * Height + 63) >> 6];
        }
        var mask = 1UL << (i & 63);
        var was = (layer[i >> 6] & mask) != 0;
        if (was == on)
        {
            return false;
        }
        layer[i >> 6] = on ? layer[i >> 6] | mask : layer[i >> 6] & ~mask;
        return true;
    }

    // PlantTrunks works out the standing trunks from the forest when the map loads: a bit per cell (TreeTrunks).
    private void PlantTrunks()
    {
        if (Rules.Trees is not { } trees || !trees.Any)
        {
            return;
        }
        var chance = trees.TrunkChance;
        int sw = (Width + TreeTrunks.SlotSide - 1) / TreeTrunks.SlotSide, sh = (Height + TreeTrunks.SlotSide - 1) / TreeTrunks.SlotSide;
        for (var sy = 0; sy < sh; sy++)
        {
            for (var sx = 0; sx < sw; sx++)
            {
                var h = TreeTrunks.SlotHash(sx, sy);
                var c = TreeTrunks.SlotCell(sx, sy, h);
                if (c.X >= Width || c.Y >= Height)
                {
                    continue;
                }
                var i = c.Y * Width + c.X;
                var p = chance[_flags[i] & ForestMask];
                if (p > 0 && TreeTrunks.Roll(h) < p)
                {
                    SetBit(ref _trunk, i, true);
                }
            }
        }
    }

    /// <summary>
    /// Fells the standing trunk of a cell: the cell opens (its costs rebuilt). Reports whether a trunk stood there. Not in
    /// the digest: the world hashes what fell (<see cref="World.Stumps"/>).
    /// </summary>
    internal bool FellTrunk(Cell c)
    {
        if (!Contains(c) || !SetBit(ref _trunk, Index(c), false))
        {
            return false;
        }
        Rebuild(c.X, c.Y, sight: false);
        Version++;
        return true;
    }

    /// <summary>
    /// Lays (or lifts) the fallen-trunk mark of a cell and rebuilds it when it changed; reports whether it did. Not in the
    /// digest: the world hashes its fallen trunks (<see cref="World.FallenTrunks"/>).
    /// </summary>
    internal bool SetFallen(Cell c, bool on)
    {
        if (!Contains(c) || !SetBit(ref _fallen, Index(c), on))
        {
            return false;
        }
        Rebuild(c.X, c.Y, sight: false);
        Version++;
        return true;
    }

    /// <summary>
    /// Reports whether a cell is a vehicle road (highway, paved, gravel or dirt: the road bit). An alley or a footpath is not:
    /// <see cref="RoadKindAt"/> and <see cref="IsFootOnly"/> read those.
    /// </summary>
    public bool Road(Cell c) => Contains(c) && (_flags[Index(c)] & RoadBit) != 0;

    /// <summary>
    /// The road kind of a cell (<see cref="RoadKind.None"/> off-road or off the map): what the
    /// speed and cost by road kind read, and what a renderer picks trails and dust by. A vehicle road's kind where there is
    /// one, else <see cref="RoadKind.Alley"/> or <see cref="RoadKind.Footpath"/> (a vehicle road wins the cell).
    /// </summary>
    public RoadKind RoadKindAt(Cell c) => Contains(c) ? KindAt(Index(c)) : RoadKind.None;

    /// <summary>
    /// Reports whether a cell is an alley: open to the foot movement class only (cost and speed 0 for every other ground
    /// class). False off the map. Structures and vehicles are not put on one.
    /// </summary>
    public bool IsFootOnly(Cell c) => Contains(c) && KindAt(Index(c)) == RoadKind.Alley;

    // KindAt is a cell's road kind by index: a vehicle road's, else the alley or footpath of the second flags byte.
    private RoadKind KindAt(int i)
    {
        var k = KindOf(_flags[i]);
        if (k != RoadKind.None || _flags2 is null)
        {
            return k;
        }
        return (_flags2[i] & PathMask) switch
        {
            AlleyCode => RoadKind.Alley,
            FootpathCode => RoadKind.Footpath,
            _ => RoadKind.None,
        };
    }

    /// <summary>
    /// The lowest road cost percent (of the same cell off-road) of a movement class over paved
    /// roads and the other road kinds on this map, 100 at most: a bound on the cheapest cell a path
    /// can meet (paved always counts, as the single road cost did before road kinds, so a map
    /// with plain roads or none routes as before).
    /// </summary>
    public int MinRoadCostPercent(int moveClass)
    {
        var mc = Rules.Classes[moveClass];
        var min = Math.Min(mc.RoadCostOf(RoadKind.Paved), 100);
        for (var k = RoadKind.Highway; k <= RoadKind.Footpath; k++)
        {
            if ((_roadKinds & (1 << (int)k)) != 0)
            {
                min = Math.Min(min, mc.RoadCostOf(k));
            }
        }
        return min;
    }

    /// <summary>
    /// The lowest road cost percent a crowd's route can meet (routing's "no roads" classes: soldiers in a group, who route as
    /// if roads were the ground beside them): 100, but on a map with a footpath the foot class's footpath cost, the one kind a
    /// crowd keeps preferring.
    /// </summary>
    public int MinCrowdRoadCostPercent(int moveClass) =>
        (_roadKinds & (1 << (int)RoadKind.Footpath)) != 0 ? Math.Min(Rules.Classes[moveClass].RoadCostOf(RoadKind.Footpath), 100) : 100;

    /// <summary>Reports whether a cell is on a building pad.</summary>
    public bool Pad(Cell c) => Contains(c) && (_flags[Index(c)] & PadBit) != 0;

    /// <summary>Whether the map has any water.</summary>
    public bool HasWater => _water is not null;

    /// <summary>The bodies of water, in the map's order: a body's ID is its place in this list plus one.</summary>
    public IReadOnlyList<WaterBody> WaterBodies => _bodies;

    /// <summary>Reports whether a cell is water (a river, lake or sea), bridged by a deck or not.</summary>
    public bool Water(Cell c) => Contains(c) && (_flags[Index(c)] & WaterBit) != 0;

    /// <summary>
    /// Reports whether a cell is open water: water without a deck over it. No ground unit enters it or is put there,
    /// and a blast there goes off in the water (no crater; the impact events' Water flag).
    /// </summary>
    public bool OpenWater(Cell c) => Contains(c) && (_flags[Index(c)] & (WaterBit | DeckBit)) == WaterBit;

    /// <summary>Reports whether a water cell has a deck over it (a standing bridge's span: <see cref="SetDeck"/>).</summary>
    public bool Deck(Cell c) => Contains(c) && (_flags[Index(c)] & DeckBit) != 0;

    /// <summary>The ID of the body of water a cell is in (its place in <see cref="WaterBodies"/> plus one), or 0.</summary>
    public int WaterIdAt(Cell c) => _water is not null && Contains(c) ? _water[Index(c)] : 0;

    /// <summary>The body of water a cell is in, or null.</summary>
    public WaterBody? WaterBodyAt(Cell c) => WaterIdAt(c) is var id and > 0 ? _bodies[id - 1] : null;

    /// <summary>The water surface height of a cell (absolute, raw), or the ground at its centre on a dry cell.</summary>
    public Fixed WaterSurfaceAt(Cell c) => WaterBodyAt(c) is { } b ? b.Surface : GroundHeightAt(c.Center());

    /// <summary>The direction the water flows at a cell (length 1): a river's, along its line; zero on a lake, the sea or dry ground.</summary>
    public Vec WaterFlowAt(Cell c) => WaterBodyAt(c)?.FlowAt(c) ?? default;

    /// <summary>
    /// Lays (or lifts) a deck over a water cell and rebuilds it: a bridge's deck (docs/design.md, "Bridges"). A decked cell
    /// is flat (grade 0, whatever the bed under it), costs what the same flat dry cell would (with its road: a bridge lays
    /// its own) and stops counting as open water; lifting the deck makes it open water again. Ignored on a dry cell. The
    /// world keeps its grid in step (<see cref="World.SetDeck"/>).
    /// </summary>
    public void SetDeck(Cell c, bool on)
    {
        if (!Water(c) || Deck(c) == on)
        {
            return;
        }
        var i = Index(c);
        _flags[i] = (byte)(on ? _flags[i] | DeckBit : _flags[i] & ~DeckBit);
        Rebuild(c.X, c.Y);
        _digest = null;
        Version++;
    }

    /// <summary>
    /// Sets the forest density of a cell (0 to 3) and rebuilds its costs. Its standing trunk follows the new density
    /// (<see cref="TreeTrunks"/>): cleared to 0 the cell has none; a felled tree's stump stays a stump while the cell keeps
    /// some forest. A fallen trunk lying across the cell is the world's to clear (<see cref="World.ClearTrees"/>).
    /// </summary>
    public void SetForest(Cell c, int density)
    {
        if (!Contains(c))
        {
            return;
        }
        var i = Index(c);
        var old = _flags[i] & ForestMask;
        var now = Math.Clamp(density, 0, 3);
        _flags[i] = (byte)((_flags[i] & ~ForestMask) | now);
        if (Rules.Trees is { } trees)
        {
            // A trunk differs from what the forest gives the cell only where a tree was felled: keep that stump.
            var had = TrunkAt(i);
            var felled = !had && TreeTrunks.Stands(c.X, c.Y, trees.TrunkChance[old]);
            SetBit(ref _trunk, i, !felled && TreeTrunks.Stands(c.X, c.Y, trees.TrunkChance[now]));
        }
        Rebuild(c.X, c.Y);
        _digest = null;
        Version++;
    }

    /// <summary>
    /// Flattens a building pad: every corner of the w x h cells from origin to height, the cells
    /// marked as pad, and the costs of the cells around rebuilt (the hook for buildings and depots).
    /// </summary>
    public void SetPad(Cell origin, int w, int h, Fixed height) => SetPad(origin, w, h, height, 0);

    /// <summary>
    /// Flattens a building pad as <see cref="SetPad(Cell, int, int, Fixed)"/>, with a ring of blend
    /// cells around it: a corner at ring distance d (1 to blend, Chebyshev, from the pad's corners)
    /// moves (blend + 1 - d) / (blend + 1) of the way to the pad height, rounded toward zero, so the
    /// ground eases onto the pad instead of stepping (integer, in a fixed order).
    /// </summary>
    public void SetPad(Cell origin, int w, int h, Fixed height, int blend)
    {
        var raw = (int)Math.Clamp(height.Raw, -MaxCornerRaw, MaxCornerRaw);
        blend = Math.Clamp(blend, 0, 32);
        for (var y = origin.Y - blend; y <= origin.Y + h + blend; y++)
        {
            for (var x = origin.X - blend; x <= origin.X + w + blend; x++)
            {
                var d = Math.Max(Math.Max(origin.X - x, x - (origin.X + w)), Math.Max(origin.Y - y, y - (origin.Y + h)));
                if (d <= 0 || x < 0 || y < 0 || x > Width || y > Height)
                {
                    continue;
                }
                var i = y * (Width + 1) + x;
                _corners[i] += (raw - _corners[i]) * (blend + 1 - d) / (blend + 1);
            }
        }
        for (var y = origin.Y; y <= origin.Y + h; y++)
        {
            for (var x = origin.X; x <= origin.X + w; x++)
            {
                if (x >= 0 && y >= 0 && x <= Width && y <= Height)
                {
                    _corners[y * (Width + 1) + x] = raw;
                }
            }
        }
        for (var y = origin.Y - 1 - blend; y <= origin.Y + h + blend; y++)
        {
            for (var x = origin.X - 1 - blend; x <= origin.X + w + blend; x++)
            {
                var c = new Cell(x, y);
                if (!Contains(c))
                {
                    continue;
                }
                if (x >= origin.X && y >= origin.Y && x < origin.X + w && y < origin.Y + h)
                {
                    _flags[Index(c)] |= PadBit;
                }
                Rebuild(x, y);
            }
        }
        _digest = null;
        Version++;
    }

    /// <summary>
    /// Reads the heights (raw) of the corners in the box from corner (x0, y0), cw x ch corners, row
    /// by row; corners off the map read as 0.
    /// </summary>
    public int[] ReadCorners(int x0, int y0, int cw, int ch)
    {
        var v = new int[cw * ch];
        for (var j = 0; j < ch; j++)
        {
            for (var i = 0; i < cw; i++)
            {
                int x = x0 + i, y = y0 + j;
                if (x >= 0 && y >= 0 && x <= Width && y <= Height)
                {
                    v[j * cw + i] = _corners[y * (Width + 1) + x];
                }
            }
        }
        return v;
    }

    /// <summary>
    /// Sets the corners of the box from corner (x0, y0), cw x ch corners, row by row, where
    /// <paramref name="mask"/> is true (every corner without one), skipping corners off the map;
    /// rebuilds the cells touching them (the hook for tunnel entrances reshaping the ground).
    /// </summary>
    public void WriteCorners(int x0, int y0, int cw, int ch, int[] heights, bool[]? mask)
    {
        var any = false;
        for (var j = 0; j < ch; j++)
        {
            for (var i = 0; i < cw; i++)
            {
                int x = x0 + i, y = y0 + j, k = j * cw + i;
                if (x < 0 || y < 0 || x > Width || y > Height || (mask is not null && !mask[k]))
                {
                    continue;
                }
                _corners[y * (Width + 1) + x] = Math.Clamp(heights[k], -MaxCornerRaw, MaxCornerRaw);
                any = true;
            }
        }
        if (!any)
        {
            return;
        }
        for (var y = y0 - 1; y < y0 + ch; y++)
        {
            for (var x = x0 - 1; x < x0 + cw; x++)
            {
                if (Contains(new Cell(x, y)))
                {
                    Rebuild(x, y);
                }
            }
        }
        _digest = null;
        Version++;
    }

    /// <summary>
    /// Marks the w x h cells from origin as paved road (an airfield's pavement; a faster road
    /// already there stays) or unmarks them (no road of any kind), and rebuilds them.
    /// </summary>
    public void SetRoad(Cell origin, int w, int h, bool on)
    {
        if (on)
        {
            _roadKinds |= 1 << (int)RoadKind.Paved;
        }
        for (var y = origin.Y; y < origin.Y + h; y++)
        {
            for (var x = origin.X; x < origin.X + w; x++)
            {
                var c = new Cell(x, y);
                if (Contains(c))
                {
                    var i = Index(c);
                    _flags[i] = on ? WithRoad(_flags[i], RoadKind.Paved) : (byte)(_flags[i] & ~(RoadBit | KindMask));
                    if (_flags2 is not null)
                    {
                        _flags2[i] &= unchecked((byte)~PathMask); // pavement over an alley or footpath: the vehicle road wins; unmarking clears every kind
                    }
                    Rebuild(x, y);
                }
            }
        }
        _digest = null;
        Version++;
    }

    /// <summary>
    /// Lays a road of exactly this kind on a cell (whatever road was there: a bridge's deck and ramps carry its own) and
    /// rebuilds it; <see cref="RoadKind.None"/> clears the road. An alley or a footpath replaces a vehicle road, as any kind
    /// replaces another here; a vehicle road replaces an alley or footpath. Off the map: nothing.
    /// </summary>
    public void SetRoadKind(Cell c, RoadKind kind)
    {
        if (!Contains(c))
        {
            return;
        }
        var i = Index(c);
        var f = (byte)(_flags[i] & ~(RoadBit | KindMask));
        _flags[i] = kind == RoadKind.None ? f : TerrainMap.WithRoad(f, kind);
        if (kind is RoadKind.Alley or RoadKind.Footpath)
        {
            _flags2 ??= new byte[Width * Height];
            _flags2[i] = (byte)((_flags2[i] & ~PathMask) | (kind == RoadKind.Alley ? AlleyCode : FootpathCode));
        }
        else if (_flags2 is not null)
        {
            _flags2[i] &= unchecked((byte)~PathMask);
        }
        if (kind != RoadKind.None)
        {
            _roadKinds |= 1 << (int)kind;
        }
        Rebuild(c.X, c.Y);
        _digest = null;
        Version++;
    }

    /// <summary>
    /// The bridges the map places (its <c>bridges</c> list, in the file's order); the world puts them up when the terrain is
    /// attached (<see cref="World.AttachTerrain"/>). Empty for a map without bridges.
    /// </summary>
    public IReadOnlyList<BridgeSite> BridgeSites => _bridgeSites;

    private readonly List<BridgeSite> _bridgeSites = [];

    /// <summary>Adds a bridge for the world to put up when the terrain is attached (map loaders; checked then).</summary>
    public void AddBridgeSite(BridgeSite site)
    {
        _bridgeSites.Add(site);
        _digest = null;
    }

    /// <summary>Marks (or unmarks) a cell as pad ground and rebuilds it.</summary>
    public void SetPadFlag(Cell c, bool on)
    {
        if (!Contains(c))
        {
            return;
        }
        var i = Index(c);
        _flags[i] = (byte)(on ? _flags[i] | PadBit : _flags[i] & ~PadBit);
        Rebuild(c.X, c.Y);
        _digest = null;
        Version++;
    }

    /// <summary>The cells rebuilt since routing last looked (null until routing starts). Not game state.</summary>
    internal List<int>? ChangeLog { get; set; }

    // Rebuild recomputes a cell's grade and every class's cost from its corners and flags (sight: whether sight lines may
    // differ there, for fog: not when only a tree trunk fell or a fallen trunk came or went).
    private void Rebuild(int x, int y, bool sight = true)
    {
        var i = y * Width + x;
        ChangeLog?.Add(i);
        if (_built)
        {
            if (_journal.Count >= JournalMax)
            {
                _journalFloor = _journal[JournalMax / 2 - 1].Version;
                _journal.RemoveRange(0, JournalMax / 2);
            }
            _journal.Add((Version + 1, i, sight));
        }
        int a = _corners[y * (Width + 1) + x], b = _corners[y * (Width + 1) + x + 1];
        int c = _corners[(y + 1) * (Width + 1) + x], d = _corners[(y + 1) * (Width + 1) + x + 1];
        var diff = Math.Max(Math.Max(a, b), Math.Max(c, d)) - Math.Min(Math.Min(a, b), Math.Min(c, d));
        _centreSum[i] = a + b + c + d;
        var flags = _flags[i];
        // A bridge's deck is flat whatever the riverbed under it (its corners are the bed's).
        _grade[i] = (flags & DeckBit) != 0 ? (byte)0 : (byte)Math.Min(diff * 100 / Fixed.OneRaw, 255);
        var now = flags & ForestMask;
        var wet = (flags & (WaterBit | DeckBit)) == WaterBit; // open water: nobody on the ground crosses it
        var road = KindAt(i);
        var fallen = FallenAt(i);
        var trunk = fallen || TrunkAt(i);
        var burning = FireWallAt(i);
        var before = ForestBefore(i, now);
        for (var k = 0; k < _cost.Length; k++)
        {
            var mc = Rules.Classes[k];
            var forest = mc.ThinnedForestAsBefore ? before : now;
            var cost = wet || (road == RoadKind.Alley && !mc.EntersAlleys) ? 0 : mc.Cost[(int)mc.Classify(_grade[i])]; // an alley is foot only
            if (cost > 0 && forest >= mc.ForestBlockedFrom)
            {
                cost = 0;
            }
            if (cost > 0)
            {
                if (road != RoadKind.None)
                {
                    cost = cost * mc.RoadCostOf(road) / 100;
                }
                cost = cost * mc.ForestCostPercent[forest] / 100;
                cost = Math.Clamp(cost, 1, 255);
            }
            if (cost > 0 && trunk && mc.Trunks != TrunkMode.Ignore)
            {
                // A tree trunk, standing or fallen: a wall to a class that avoids them, dearer to a pusher (a fallen one only
                // to a pusher that rolls over them).
                var pass = fallen ? mc.EntersFallen : mc.Trunks == TrunkMode.Push;
                cost = pass ? Math.Clamp(cost * mc.PushCost / 100, 1, 255) : 0;
            }
            if (cost > 0 && burning)
            {
                // A burning cell: a wall to a class with no fire cost, dearer to the rest (forest fire).
                cost = mc.FireCost == 0 ? 0 : Math.Clamp(cost * mc.FireCost / 100, 1, 255);
            }
            _cost[k][i] = (byte)cost;
        }
    }

    private const ulong FnvPrime = 1099511628211UL;

    // PutBytes runs FNV-1a over the bytes (each corner little-endian, 4 bytes): the per-cell layers' tight loop.
    private static ulong PutBytes(ulong h, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            h = (h ^ b) * FnvPrime;
        }
        return h;
    }

    // PutFlagsAsBefore runs FNV-1a over the flags with each burnt or thinned cell's forest as it was before (its density plus
    // the levels it lost, at most dense), so fire and blasts never change the digest: the world hashes them.
    private ulong PutFlagsAsBefore(ulong h)
    {
        for (var i = 0; i < _flags.Length; i++)
        {
            var f = _flags[i];
            if (DroppedAt(i) || ThinnedAt(i))
            {
                f = (byte)((f & ~ForestMask) | ForestBefore(i, f & ForestMask));
            }
            h = (h ^ f) * FnvPrime;
        }
        return h;
    }

    private static ulong PutBytes(ulong h, int[] values)
    {
        foreach (var v in values)
        {
            h = (h ^ (byte)v) * FnvPrime;
            h = (h ^ (byte)(v >> 8)) * FnvPrime;
            h = (h ^ (byte)(v >> 16)) * FnvPrime;
            h = (h ^ (byte)(v >> 24)) * FnvPrime;
        }
        return h;
    }

    /// <summary>A 64-bit FNV-1a digest of the terrain and its rules (in the game hash when a map has terrain).</summary>
    public ulong Digest()
    {
        if (_digest is { } cached)
        {
            return cached;
        }
        var h = Rules.Digest();
        void Put(byte v)
        {
            h ^= v;
            h *= 1099511628211UL;
        }
        for (var k = 0; k < 4; k++)
        {
            Put((byte)(Width >> (8 * k)));
        }
        for (var k = 0; k < 4; k++)
        {
            Put((byte)(Height >> (8 * k)));
        }
        h = PutBytes(h, _corners);
        h = _dropped is null && _thinned is null ? PutBytes(h, _flags) : PutFlagsAsBefore(h);
        if (_water is not null)
        {
            // Only a map with water: the bodies (kind, surface, shape) and which body each cell is in.
            void PutInt(long v)
            {
                for (var k = 0; k < 8; k++)
                {
                    Put((byte)(v >> (8 * k)));
                }
            }
            PutInt(_bodies.Length);
            foreach (var b in _bodies)
            {
                Put((byte)b.Kind);
                PutInt(b.Surface.Raw);
                PutInt(b.Line.Count);
                for (var k = 0; k < b.Line.Count; k++)
                {
                    PutInt(b.Line[k].X);
                    PutInt(b.Line[k].Y);
                    PutInt(b.Widths[k]);
                }
                PutInt(b.Outline.Count);
                foreach (var (x, y) in b.Outline)
                {
                    PutInt(x);
                    PutInt(y);
                }
            }
            h = PutBytes(h, _water);
        }
        if (_bridgeSites.Count > 0)
        {
            // Only a map with bridges: each site's kind, cell and facing.
            void PutWord(long v)
            {
                for (var k = 0; k < 8; k++)
                {
                    Put((byte)(v >> (8 * k)));
                }
            }
            PutWord(_bridgeSites.Count);
            foreach (var b in _bridgeSites)
            {
                PutWord(b.Kind.Length);
                foreach (var ch in b.Kind)
                {
                    PutWord(ch);
                }
                PutWord(b.Cell.X);
                PutWord(b.Cell.Y);
                PutWord(b.Facing);
            }
        }
        if (_flags2 is not null && Array.Exists(_flags2, f => f != 0))
        {
            // Only a map with alleys or footpaths: the second flags byte of every cell.
            Put(0xA2);
            h = PutBytes(h, _flags2);
        }
        _digest = h;
        return h;
    }
}
