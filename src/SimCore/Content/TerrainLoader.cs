using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Content;

/// <summary>
/// Loads the terrain rules (<c>content/terrain/rules.json</c>) and terrain maps: a JSON file plus
/// a 16-bit greyscale PNG heightmap of (W+1) x (H+1) corner heights, optional 8-bit greyscale PNG
/// forest density (W x H, 0-63 none, 64-127 scrub, 128-191 grove, 192-255 dense), road polylines
/// and rectangles (each with an optional <c>kind</c>: highway, paved (default), gravel, dirt, and the foot kinds alley and
/// footpath, which a vehicle road overlapping them wins), forest rectangles, flatten pads,
/// and water (rivers as a centre line with a width, lakes and the sea as polygons, each with a surface <c>height</c> in
/// tenths of a metre; see <see cref="Sim.WaterBody"/>). Heights map linearly from pixel 0 to
/// <c>minHeight</c> and 65535 to <c>maxHeight</c> (whole metres, 1 cell = 2 m), in integers. A
/// pad's <c>height</c> is in TENTHS of a metre (finer than the map's range: 15 = 1.5 m), and its
/// <c>blend</c> (default 3) is the ring of cells around it over which the ground eases onto it.
/// </summary>
public static class TerrainLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The cells of ground easing onto a pad around it, unless its <c>blend</c> says otherwise.</summary>
    public const int DefaultPadBlend = 3;

    /// <summary>Raw fixed-point units per metre (1024 per cell of 2 m).</summary>
    public const int RawPerMetre = 512;

    /// <summary>
    /// The terrain map of a setup (its <see cref="MatchSetup.Terrain"/> file name, under mapsDir),
    /// with the rules; null for a flat setup.
    /// </summary>
    /// <exception cref="ContentException">The map is missing or invalid, or the name leaves mapsDir.</exception>
    public static TerrainMap? ForSetup(MatchSetup setup, string mapsDir, TerrainRules rules)
    {
        if (setup.Terrain is not { } name)
        {
            return null;
        }
        var root = Path.GetFullPath(mapsDir);
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ContentException($"setup: terrain \"{name}\" is outside {mapsDir}");
        }
        return LoadMap(path, rules);
    }

    /// <summary>Loads the terrain rules.</summary>
    /// <exception cref="ContentException">The file is missing or invalid.</exception>
    public static TerrainRules LoadRules(string path) => ParseRules(Read(path), path);

    /// <summary>Compiles terrain rules from JSON text.</summary>
    public static TerrainRules ParseRules(string json, string name = "rules.json")
    {
        RulesJson r;
        try
        {
            r = JsonSerializer.Deserialize<RulesJson>(json, JsonOptions) ?? throw new ContentException($"{name}: empty");
        }
        catch (JsonException e)
        {
            throw new ContentException($"{name}: {e.Message}");
        }
        var classes = new List<MoveClass>();
        foreach (var c in r.Classes ?? [])
        {
            var where = $"{name}: class \"{c.Id}\"";
            var loco = c.Locomotor switch
            {
                "foot" => Locomotor.Foot,
                "wheeled" => Locomotor.Wheeled,
                "tracked" => Locomotor.Tracked,
                _ => throw new ContentException($"{where}: unknown locomotor \"{c.Locomotor}\""),
            };
            List<int> Four(List<int>? v, string field)
            {
                if (v is not { Count: 4 } || v.Any(x => x < 0 || x > 1000))
                {
                    throw new ContentException($"{where}: {field} wants 4 values from 0 to 1000");
                }
                return v;
            }
            // RoadTable reads a per-kind road value: 4 values (highway, paved, gravel, dirt) or one
            // number for every kind (the format before road kinds); absent, 100 for every kind.
            List<int> RoadTable(JsonElement? v, string field, int min)
            {
                List<int> list;
                if (v is not { } e || e.ValueKind == JsonValueKind.Null)
                {
                    list = [100, 100, 100, 100];
                }
                else if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var one))
                {
                    list = [one, one, one, one];
                }
                else if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == MoveClass.RoadKinds &&
                    e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)))
                {
                    list = [.. e.EnumerateArray().Select(x => x.GetInt32())];
                }
                else
                {
                    throw new ContentException($"{where}: {field} wants 4 values (highway, paved, gravel, dirt) or one number");
                }
                if (list.Any(x => x < min || x > 1000))
                {
                    throw new ContentException($"{where}: {field} values want {min} to 1000");
                }
                return list;
            }
            if (c.Slopes is not { } s || s.Gentle <= 0 || s.Steep <= s.Gentle || s.Cliff <= s.Steep || s.Cliff > 255)
            {
                throw new ContentException($"{where}: slopes want 0 < gentle < steep < cliff <= 255 (percent grade)");
            }
            var trunks = ParseTrunks(c, r.Trees is not null, where);
            classes.Add(new MoveClass
            {
                Id = c.Id ?? throw new ContentException($"{name}: a class without id"),
                Locomotor = loco,
                MinRadius = Fixed.Frac(c.MinRadius, 10),
                MaxRadius = c.MaxRadius is { } mr ? Fixed.Frac(mr, 10) : null,
                GentleFrom = s.Gentle,
                SteepFrom = s.Steep,
                CliffFrom = s.Cliff,
                SpeedUp = Four(c.SpeedUp, "speedUp"),
                SpeedDown = Four(c.SpeedDown, "speedDown"),
                Cost = Four(c.Cost, "cost"),
                UphillCostPercent = c.UphillCost,
                RoadCost = RoadTable(c.RoadCost, "roadCost", 1),
                RoadSpeed = RoadTable(c.RoadSpeed, "roadSpeed", 0),
                ForestCostPercent = Four(c.ForestCost, "forestCost"),
                ForestSpeedPercent = Four(c.ForestSpeed, "forestSpeed"),
                ForestBlockedFrom = Math.Clamp(c.ForestBlockedFrom, 1, 4),
                DeployMaxGrade = c.DeployMaxGrade is { } dg ? Math.Clamp(dg, 0, 255) : s.Steep,
                Trunks = trunks,
                PushCost = trunks == TrunkMode.Push ? c.PushCost!.Value : 100,
                PushSpeedPercent = trunks == TrunkMode.Push ? c.PushSpeedPercent!.Value : 100,
                PushTicks = trunks == TrunkMode.Push ? c.PushTicks!.Value : 0,
                CrossFallen = c.CrossFallen ?? false,
            });
        }
        if (classes.Count == 0)
        {
            throw new ContentException($"{name}: no movement classes");
        }
        return new TerrainRules
        {
            Classes = classes,
            Vision = r.Vision is { } v ? ParseVision(v, name) : null,
            Trees = r.Trees is { } t ? ParseTrees(t, name) : null,
        };
    }

    // ParseTrunks reads what tree trunks are to a class (docs/design.md, "Trees as obstacles"): with a trees block every class
    // says (trunks: ignore, avoid or push); a pusher gives its push cost, speed and ticks; only a pusher may cross fallen trunks.
    private static TrunkMode ParseTrunks(ClassJson c, bool trees, string where)
    {
        var mode = c.Trunks switch
        {
            null when !trees => TrunkMode.Ignore,
            "ignore" => TrunkMode.Ignore,
            "avoid" => TrunkMode.Avoid,
            "push" => TrunkMode.Push,
            _ => throw new ContentException($"{where}: trunks wants \"ignore\", \"avoid\" or \"push\"" +
                (c.Trunks is null ? " (the rules have a trees block)" : $", got \"{c.Trunks}\"")),
        };
        if (mode != TrunkMode.Push)
        {
            if (c.PushCost is not null || c.PushSpeedPercent is not null || c.PushTicks is not null || c.CrossFallen is not null)
            {
                throw new ContentException($"{where}: pushCost, pushSpeedPercent, pushTicks and crossFallen are for a class whose trunks are \"push\"");
            }
            return mode;
        }
        if (c.PushCost is not (>= 100 and <= 1000))
        {
            throw new ContentException($"{where}: pushCost wants 100 to 1000 (percent of the cell's cost)");
        }
        if (c.PushSpeedPercent is not (>= 1 and <= 100))
        {
            throw new ContentException($"{where}: pushSpeedPercent wants 1 to 100");
        }
        if (c.PushTicks is not (>= 1 and <= 1200))
        {
            throw new ContentException($"{where}: pushTicks wants 1 to 1200 (ticks)");
        }
        return mode;
    }

    // ParseTrees reads the trees block: the trunk chance per forest density (percent per 2 x 2 slot), the length of a fallen
    // trunk (cells) and how long it lies (ticks).
    private static TreeRules ParseTrees(TreesJson t, string name)
    {
        if (t.TrunkChance is not { Count: 4 } chance || chance.Any(x => x < 0 || x > 100))
        {
            throw new ContentException($"{name}: trees.trunkChance wants 4 values from 0 to 100 (percent per 2 x 2 cells: none, scrub, grove, dense)");
        }
        if (t.FallenLengthCells is < 1 or > 16)
        {
            throw new ContentException($"{name}: trees.fallenLengthCells wants 1 to 16");
        }
        if (t.FallenTicks is < 1 or > 72000)
        {
            throw new ContentException($"{name}: trees.fallenTicks wants 1 to 72000 (ticks)");
        }
        var fell = new List<(string, int)>();
        foreach (var (warhead, percent) in t.Fell ?? [])
        {
            if (string.IsNullOrEmpty(warhead) || percent is < 1 or > 400)
            {
                throw new ContentException($"{name}: trees.fell wants warhead IDs with 1 to 400 (percent of the splash radius)");
            }
            fell.Add((warhead, percent));
        }
        fell.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        if (t.FellMinRadius is < 0 or > 200)
        {
            throw new ContentException($"{name}: trees.fellMinRadius wants 0 to 200 (tenths of a cell)");
        }
        if (t.FallDamage is < 0 or > 100)
        {
            throw new ContentException($"{name}: trees.fallDamage wants 0 to 100 (percent of max HP)");
        }
        if ((t.FallDamageTags ?? []).Any(string.IsNullOrEmpty))
        {
            throw new ContentException($"{name}: trees.fallDamageTags wants tag names");
        }
        return new TreeRules
        {
            TrunkChance = chance,
            FallenLengthCells = t.FallenLengthCells,
            FallenTicks = t.FallenTicks,
            Fell = fell,
            FellMinRadius = Fixed.Frac(t.FellMinRadius, 10),
            FallDamagePercent = t.FallDamage,
            FallDamageTags = [.. t.FallDamageTags ?? []],
        };
    }

    /// <summary>Loads a terrain map: the JSON file and the PNGs it names (next to it).</summary>
    /// <exception cref="ContentException">A file is missing or invalid.</exception>
    public static TerrainMap LoadMap(string jsonPath, TerrainRules rules)
    {
        var name = Path.GetFileName(jsonPath);
        MapJson m;
        try
        {
            m = JsonSerializer.Deserialize<MapJson>(Read(jsonPath), JsonOptions) ?? throw new ContentException($"{name}: empty");
        }
        catch (JsonException e)
        {
            throw new ContentException($"{name}: {e.Message}");
        }
        if (m.Width <= 0 || m.Height <= 0 || m.Width > TerrainMap.MaxSide || m.Height > TerrainMap.MaxSide)
        {
            throw new ContentException($"{name}: width and height want 1 to {TerrainMap.MaxSide} cells, got {m.Width}x{m.Height}");
        }
        var dir = Path.GetDirectoryName(jsonPath) ?? ".";
        var (w, h) = (m.Width, m.Height);
        var corners = new int[(w + 1) * (h + 1)];
        if (m.Heightmap is { } hm)
        {
            // The header first: a PNG of the wrong size or depth fails before its pixels are decoded.
            var data = File.ReadAllBytes(Path.Combine(dir, hm));
            var (pw, ph, pd) = Png.Header(data, $"{name}: {hm}");
            if (pw != w + 1 || ph != h + 1 || pd != 16)
            {
                throw new ContentException($"{name}: {hm} wants a 16-bit greyscale PNG of {w + 1}x{h + 1} (corners), got {pw}x{ph} {pd}-bit");
            }
            var png = Png.Read(data, $"{name}: {hm}");
            long lo = (long)m.MinHeight * RawPerMetre, hi = (long)m.MaxHeight * RawPerMetre;
            if (hi <= lo || lo < -TerrainMap.MaxCornerRaw || hi > TerrainMap.MaxCornerRaw)
            {
                throw new ContentException($"{name}: minHeight and maxHeight want -256 <= min < max <= 256 metres");
            }
            for (var i = 0; i < corners.Length; i++)
            {
                corners[i] = (int)(lo + (png.Samples[i] * (hi - lo) + 32767) / 65535);
            }
        }
        var flags = new byte[w * h];
        if (m.ForestMap is { } fm)
        {
            var data = File.ReadAllBytes(Path.Combine(dir, fm));
            var (pw, ph, pd) = Png.Header(data, $"{name}: {fm}");
            if (pw != w || ph != h || pd != 8)
            {
                throw new ContentException($"{name}: {fm} wants an 8-bit greyscale PNG of {w}x{h}");
            }
            var png = Png.Read(data, $"{name}: {fm}");
            for (var i = 0; i < flags.Length; i++)
            {
                flags[i] = (byte)(png.Samples[i] >> 6);
            }
        }
        void Each(List<int>? rect, string what, Action<int> cell)
        {
            if (rect is not { Count: 4 })
            {
                throw new ContentException($"{name}: {what} wants [x, y, w, h]");
            }
            for (var y = Math.Max(rect[1], 0); y < Math.Min(rect[1] + rect[3], h); y++)
            {
                for (var x = Math.Max(rect[0], 0); x < Math.Min(rect[0] + rect[2], w); x++)
                {
                    cell(y * w + x);
                }
            }
        }
        foreach (var f in m.Forest ?? [])
        {
            Each(f.Rect, "forest.rect", i => flags[i] = (byte)((flags[i] & ~3) | Math.Clamp(f.Density, 0, 3)));
        }
        byte[]? flags2 = null; // the foot-only kinds (alley, footpath): a second byte, only on a map that has one
        foreach (var r in m.Roads ?? [])
        {
            var kind = r.Kind switch
            {
                null or "paved" => RoadKind.Paved,
                "highway" => RoadKind.Highway,
                "gravel" => RoadKind.Gravel,
                "dirt" => RoadKind.Dirt,
                "alley" => RoadKind.Alley,
                "footpath" => RoadKind.Footpath,
                _ => throw new ContentException($"{name}: roads.kind \"{r.Kind}\" wants highway, paved, gravel, dirt, alley or footpath"),
            };
            var footOnly = kind is RoadKind.Alley or RoadKind.Footpath;
            if (footOnly)
            {
                flags2 ??= new byte[w * h];
            }
            var second = flags2;
            void Mark(int i)
            {
                if (footOnly)
                {
                    second![i] = TerrainMap.WithPathKind(second[i], kind);
                }
                else
                {
                    flags[i] = TerrainMap.WithRoad(flags[i], kind);
                }
            }
            if (r.Rect is not null)
            {
                Each(r.Rect, "roads.rect", Mark);
            }
            var pts = r.Points ?? [];
            for (var k = 0; k + 1 < pts.Count; k++)
            {
                RoadSegment(pts[k], pts[k + 1], Math.Max(r.Width, 1), w, h, Mark, name);
            }
        }
        var terrain = new TerrainMap(w, h, corners, flags, rules, Water(m.Water, name), flags2);
        foreach (var p in m.Pads ?? [])
        {
            if (p.Rect is not { Count: 4 } rect)
            {
                throw new ContentException($"{name}: pads.rect wants [x, y, w, h]");
            }
            terrain.SetPad(new Cell(rect[0], rect[1]), rect[2], rect[3], Fixed.FromRaw((long)p.Height * RawPerMetre / 10), p.Blend);
        }
        var bi = 0;
        foreach (var b in m.Bridges ?? [])
        {
            var where = $"{name}: bridges[{bi++}]";
            if (b.Kind is not { Length: > 0 } kind || !kind.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                throw new ContentException($"{where}: kind wants a bridge kind (concrete, stone, metal: the archetype bridge_<kind>)");
            }
            if (b.Cell is not { Count: 2 } at || at[0] < 0 || at[1] < 0 || at[0] >= w || at[1] >= h)
            {
                throw new ContentException($"{where}: cell wants [x, y] on the map (the footprint's top-left cell)");
            }
            var facing = b.Dir switch
            {
                "ns" => Facing.South,
                "ew" => Facing.East,
                _ => throw new ContentException($"{where}: dir \"{b.Dir}\" wants ns (the road runs north-south) or ew (east-west)"),
            };
            terrain.AddBridgeSite(new BridgeSite(kind, new Cell(at[0], at[1]), facing));
        }
        return terrain;
    }

    /// <summary>
    /// Loads a terrain map (<see cref="LoadMap(string, TerrainRules)"/>) and checks its bridges against the content
    /// (<see cref="CheckBridges"/>), so a bridge that can't stand where the map puts it fails the load.
    /// </summary>
    /// <exception cref="ContentException">A file is missing or invalid, or a bridge can't stand where it is.</exception>
    public static TerrainMap LoadMap(string jsonPath, TerrainRules rules, ContentSet content)
    {
        var map = LoadMap(jsonPath, rules);
        CheckBridges(map, content, Path.GetFileName(jsonPath));
        return map;
    }

    /// <summary>
    /// Checks a terrain map's bridges against the content: each names a bridge archetype (<c>bridge_</c> + its kind) and
    /// stands where the map puts it by the terrain's rules (<see cref="World.BridgeSiteRefusal"/>: both ends on dry land,
    /// spanning water, on the map), and no two overlap. The world checks them again (with what stands on the map) when the
    /// terrain is attached.
    /// </summary>
    /// <exception cref="ContentException">A bridge can't stand where the map puts it.</exception>
    public static void CheckBridges(TerrainMap map, ContentSet content, string name = "map")
    {
        var rects = new List<(int X, int Y, int W, int H)>();
        for (var i = 0; i < map.BridgeSites.Count; i++)
        {
            var s = map.BridgeSites[i];
            var where = $"{name}: bridges[{i}] ({s.Kind} at {s.Cell.X}, {s.Cell.Y})";
            if (content.Archetype(s.Archetype) is not { Bridge: not null } a)
            {
                throw new ContentException($"{where}: no bridge archetype \"{s.Archetype}\"");
            }
            if (World.BridgeSiteRefusal(map, a, s.Cell, s.Facing) is { } why)
            {
                throw new ContentException($"{where}: {why}");
            }
            var (fw, fh) = World.Footprint(a, s.Facing);
            for (var k = 0; k < rects.Count; k++)
            {
                var r = rects[k];
                if (r.X < s.Cell.X + fw && s.Cell.X < r.X + r.W && r.Y < s.Cell.Y + fh && s.Cell.Y < r.Y + r.H)
                {
                    throw new ContentException($"{where}: it overlaps bridges[{k}]");
                }
            }
            rects.Add((s.Cell.X, s.Cell.Y, fw, fh));
        }
    }

    // Water compiles the map's bodies of water: a river's points are cells [x, y] or [x, y, width] (its width, else the
    // entry's), a lake's or the sea's polygon points [x, y] in map coordinates (cell corners); heights in tenths of a metre.
    private static List<WaterBody> Water(List<WaterJson>? list, string name)
    {
        var bodies = new List<WaterBody>();
        if (list is null)
        {
            return bodies;
        }
        if (list.Count > WaterBody.MaxBodies)
        {
            throw new ContentException($"{name}: {list.Count} water bodies, want {WaterBody.MaxBodies} at most");
        }
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var where = $"{name}: water[{i}]";
            if (e.Height is not { } ht)
            {
                throw new ContentException($"{where}: wants a surface height (tenths of a metre)");
            }
            var raw = (long)ht * RawPerMetre / 10;
            if (raw < -TerrainMap.MaxCornerRaw || raw > TerrainMap.MaxCornerRaw)
            {
                throw new ContentException($"{where}: height wants -2560 to 2560 (tenths of a metre)");
            }
            var surface = Fixed.FromRaw(raw);
            switch (e.Kind)
            {
                case "river":
                {
                    if (e.Polygon is not null || e.Points is not { Count: >= 2 } pts)
                    {
                        throw new ContentException($"{where}: a river wants points (2 or more [x, y] or [x, y, width] cells) and no polygon");
                    }
                    if (e.Width is < 1 or > WaterBody.MaxWidth)
                    {
                        throw new ContentException($"{where}: width wants 1 to {WaterBody.MaxWidth} cells");
                    }
                    var line = new List<Cell>();
                    var widths = new List<int>();
                    foreach (var p in pts)
                    {
                        if (p is not { Count: 2 or 3 } || (p.Count == 3 && p[2] is < 1 or > WaterBody.MaxWidth))
                        {
                            throw new ContentException($"{where}: points want [x, y] or [x, y, width] (width 1 to {WaterBody.MaxWidth})");
                        }
                        line.Add(new Cell(p[0], p[1]));
                        widths.Add(p.Count == 3 ? p[2] : e.Width);
                    }
                    bodies.Add(WaterBody.River(surface, line, widths));
                    break;
                }
                case "lake" or "sea":
                {
                    if (e.Points is not null || e.Polygon is not { Count: >= 3 } poly || poly.Any(p => p is not { Count: 2 }))
                    {
                        throw new ContentException($"{where}: a {e.Kind} wants a polygon (3 or more [x, y] points, map coordinates) and no points");
                    }
                    bodies.Add(WaterBody.Area(e.Kind == "lake" ? WaterKind.Lake : WaterKind.Sea, surface, [.. poly.Select(p => (p[0], p[1]))]));
                    break;
                }
                default:
                    throw new ContentException($"{where}: kind \"{e.Kind}\" wants river, lake or sea");
            }
        }
        return bodies;
    }

    // RoadSegment marks the cells of a road from a to b (cells), width cells wide, with integer
    // steps along the longer axis (no floats).
    private static void RoadSegment(List<int> a, List<int> b, int width, int w, int h, Action<int> mark, string name)
    {
        if (a.Count != 2 || b.Count != 2)
        {
            throw new ContentException($"{name}: roads.points want [x, y] pairs");
        }
        int dx = b[0] - a[0], dy = b[1] - a[1];
        var n = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var half = (width - 1) / 2;
        for (var s = 0; s <= n; s++)
        {
            var x = a[0] + (n == 0 ? 0 : RoundDiv(dx * s, n));
            var y = a[1] + (n == 0 ? 0 : RoundDiv(dy * s, n));
            for (var oy = -half; oy < width - half; oy++)
            {
                for (var ox = -half; ox < width - half; ox++)
                {
                    int cx = x + ox, cy = y + oy;
                    if (cx >= 0 && cy >= 0 && cx < w && cy < h)
                    {
                        mark(cy * w + cx);
                    }
                }
            }
        }
    }

    // RoundDiv divides rounding halves away from zero (the same either way along a road).
    private static int RoundDiv(int p, int q) => p >= 0 ? (2 * p + q) / (2 * q) : -((-2 * p + q) / (2 * q));

    private static string Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new ContentException($"terrain: file not found: {path}");
        }
        return File.ReadAllText(path);
    }

    private sealed class RulesJson
    {
        [JsonPropertyName("classes")]
        public List<ClassJson>? Classes { get; set; }

        [JsonPropertyName("vision")]
        public VisionJson? Vision { get; set; }

        [JsonPropertyName("trees")]
        public TreesJson? Trees { get; set; }
    }

    // TreesJson: trunk chance in percent per 2 x 2 slot per forest density [none, scrub, grove, dense]; a fallen trunk's
    // length in cells and its time on the ground in ticks.
    private sealed class TreesJson
    {
        [JsonPropertyName("trunkChance")]
        public List<int>? TrunkChance { get; set; }

        [JsonPropertyName("fallenLengthCells")]
        public int FallenLengthCells { get; set; }

        [JsonPropertyName("fallenTicks")]
        public int FallenTicks { get; set; }

        // Fell: per warhead ID, the percent of its weapon's splash radius within which a blast blows trees down.
        [JsonPropertyName("fell")]
        public Dictionary<string, int>? Fell { get; set; }

        // FellMinRadius: tenths of a cell, the least radius of a felling blast.
        [JsonPropertyName("fellMinRadius")]
        public int FellMinRadius { get; set; }

        // FallDamage: percent of max HP a falling trunk takes from the units it hurts (fallDamageTags).
        [JsonPropertyName("fallDamage")]
        public int FallDamage { get; set; }

        [JsonPropertyName("fallDamageTags")]
        public List<string>? FallDamageTags { get; set; }
    }

    // VisionJson: heights in tenths of a metre; forest values per density [none, scrub, grove, dense].
    private sealed class VisionJson
    {
        [JsonPropertyName("eyeFoot")]
        public int EyeFoot { get; set; }

        [JsonPropertyName("eyeVehicle")]
        public int EyeVehicle { get; set; }

        [JsonPropertyName("target")]
        public int Target { get; set; }

        [JsonPropertyName("canopy")]
        public List<int>? Canopy { get; set; }

        [JsonPropertyName("forestSight")]
        public List<int>? ForestSight { get; set; }

        [JsonPropertyName("forestBudget")]
        public int ForestBudget { get; set; }
    }

    private static VisionRules ParseVision(VisionJson v, string name)
    {
        List<int> Four(List<int>? l, string field, int max)
        {
            if (l is not { Count: 4 } || l.Any(x => x < 0 || x > max))
            {
                throw new ContentException($"{name}: vision.{field} wants 4 values from 0 to {max}");
            }
            return l;
        }
        static int Raw(int tenths) => tenths * RawPerMetre / 10;
        if (v.EyeFoot is <= 0 or > 1000 || v.EyeVehicle is <= 0 or > 1000 || v.Target is < 0 or > 1000)
        {
            throw new ContentException($"{name}: vision eyeFoot and eyeVehicle want 1 to 1000, target 0 to 1000 (tenths of a metre)");
        }
        if (v.ForestBudget is <= 0 or > 1000)
        {
            throw new ContentException($"{name}: vision.forestBudget wants 1 to 1000");
        }
        return new VisionRules
        {
            EyeFootRaw = Raw(v.EyeFoot),
            EyeVehicleRaw = Raw(v.EyeVehicle),
            TargetRaw = Raw(v.Target),
            CanopyRaw = [.. Four(v.Canopy, "canopy", 1000).Select(Raw)],
            ForestSight = Four(v.ForestSight, "forestSight", 1000),
            ForestBudget = v.ForestBudget,
        };
    }

    private sealed class ClassJson
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("locomotor")]
        public string? Locomotor { get; set; }

        // MinRadius: tenths of a cell; the class applies to units of this radius and up.
        [JsonPropertyName("minRadius")]
        public int MinRadius { get; set; }

        // MaxRadius: tenths of a cell; the class applies to units of this radius and down (none: any).
        [JsonPropertyName("maxRadius")]
        public int? MaxRadius { get; set; }

        [JsonPropertyName("slopes")]
        public SlopesJson? Slopes { get; set; }

        [JsonPropertyName("speedUp")]
        public List<int>? SpeedUp { get; set; }

        [JsonPropertyName("speedDown")]
        public List<int>? SpeedDown { get; set; }

        [JsonPropertyName("cost")]
        public List<int>? Cost { get; set; }

        [JsonPropertyName("uphillCost")]
        public int UphillCost { get; set; }

        // RoadCost: percent of the same cell off-road, per road kind [highway, paved, gravel, dirt],
        // or one number for every kind.
        [JsonPropertyName("roadCost")]
        public JsonElement? RoadCost { get; set; }

        // RoadSpeed: percent of the same cell off-road, per road kind [highway, paved, gravel, dirt].
        [JsonPropertyName("roadSpeed")]
        public JsonElement? RoadSpeed { get; set; }

        [JsonPropertyName("forestCost")]
        public List<int>? ForestCost { get; set; }

        [JsonPropertyName("forestSpeed")]
        public List<int>? ForestSpeed { get; set; }

        [JsonPropertyName("forestBlockedFrom")]
        public int ForestBlockedFrom { get; set; } = 4;

        [JsonPropertyName("deployMaxGrade")]
        public int? DeployMaxGrade { get; set; }

        // Trunks: what a tree trunk is to the class: "ignore", "avoid" or "push" (required with a trees block).
        [JsonPropertyName("trunks")]
        public string? Trunks { get; set; }

        // PushCost: a pusher's cost of a cell with a trunk, percent of the cell's own.
        [JsonPropertyName("pushCost")]
        public int? PushCost { get; set; }

        [JsonPropertyName("pushSpeedPercent")]
        public int? PushSpeedPercent { get; set; }

        [JsonPropertyName("pushTicks")]
        public int? PushTicks { get; set; }

        [JsonPropertyName("crossFallen")]
        public bool? CrossFallen { get; set; }
    }

    private sealed class SlopesJson
    {
        [JsonPropertyName("gentle")]
        public int Gentle { get; set; }

        [JsonPropertyName("steep")]
        public int Steep { get; set; }

        [JsonPropertyName("cliff")]
        public int Cliff { get; set; }
    }

    private sealed class MapJson
    {
        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("heightmap")]
        public string? Heightmap { get; set; }

        [JsonPropertyName("minHeight")]
        public int MinHeight { get; set; } = -16;

        [JsonPropertyName("maxHeight")]
        public int MaxHeight { get; set; } = 48;

        [JsonPropertyName("forestMap")]
        public string? ForestMap { get; set; }

        [JsonPropertyName("forest")]
        public List<ForestJson>? Forest { get; set; }

        [JsonPropertyName("roads")]
        public List<RoadJson>? Roads { get; set; }

        [JsonPropertyName("pads")]
        public List<PadJson>? Pads { get; set; }

        [JsonPropertyName("water")]
        public List<WaterJson>? Water { get; set; }

        [JsonPropertyName("bridges")]
        public List<MapBridgeJson>? Bridges { get; set; }

        // The renderer's biome (its ground textures and plants): visual only, never read here
        // and not in the terrain's digest.
        [JsonPropertyName("biome")]
        public string? Biome { get; set; }

        // The renderer's painted ground (gravel riverbeds, terraces, fields): visual only, never
        // read here and not in the terrain's digest.
        [JsonPropertyName("paint")]
        public JsonElement? Paint { get; set; }
    }

    // MapBridgeJson: a bridge the map places (docs/design.md, "Bridges"): kind (the archetype bridge_<kind>: concrete, stone,
    // metal), cell (the footprint's top-left cell as it stands), dir ("ns": the road runs north-south; "ew": east-west).
    private sealed class MapBridgeJson
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("cell")]
        public List<int>? Cell { get; set; }

        [JsonPropertyName("dir")]
        public string? Dir { get; set; }
    }

    private sealed class ForestJson
    {
        [JsonPropertyName("rect")]
        public List<int>? Rect { get; set; }

        [JsonPropertyName("density")]
        public int Density { get; set; }
    }

    private sealed class RoadJson
    {
        [JsonPropertyName("rect")]
        public List<int>? Rect { get; set; }

        [JsonPropertyName("points")]
        public List<List<int>>? Points { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; } = 2;

        // Kind: the surface, "highway", "paved" (default), "gravel" or "dirt". Where roads overlap,
        // the faster kind wins (highway, paved, gravel, dirt), whatever the order of the entries.
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }
    }

    // WaterJson: a river (kind "river", points, width) or a lake or the sea (kind "lake" / "sea", polygon), with its surface
    // height in tenths of a metre (as pads). Later entries lie over earlier ones where they overlap.
    private sealed class WaterJson
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        // A river's centre line, upstream first (it flows in this order): cells [x, y] or [x, y, width].
        [JsonPropertyName("points")]
        public List<List<int>>? Points { get; set; }

        // A river's width in cells where a point gives none.
        [JsonPropertyName("width")]
        public int Width { get; set; } = 3;

        // A lake's or the sea's outline: [x, y] in map coordinates (cells from the top-left corner of the map).
        [JsonPropertyName("polygon")]
        public List<List<int>>? Polygon { get; set; }

        // The surface height: tenths of a metre.
        [JsonPropertyName("height")]
        public int? Height { get; set; }
    }

    private sealed class PadJson
    {
        [JsonPropertyName("rect")]
        public List<int>? Rect { get; set; }

        // The ring of cells around the pad over which the ground eases onto it (0: a hard step).
        [JsonPropertyName("blend")]
        public int Blend { get; set; } = DefaultPadBlend;

        // Height: tenths of a metre.
        [JsonPropertyName("height")]
        public int Height { get; set; }
    }
}

/// <summary>A minimal PNG reader for greyscale images (8 or 16 bits, not interlaced): the terrain layers.</summary>
public static class Png
{
    /// <summary>A decoded greyscale image: samples row by row (0-255 or 0-65535).</summary>
    public sealed record Image(int Width, int Height, int Depth, int[] Samples);

    /// <summary>
    /// Reads a greyscale PNG's size and bit depth from its header without decoding the pixels (so a map loader can refuse
    /// an image of the wrong size before it allocates for it).
    /// </summary>
    /// <exception cref="ContentException">Not a supported PNG.</exception>
    public static (int Width, int Height, int Depth) Header(byte[] data, string name)
    {
        CheckSignature(data, name);
        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            var body = pos + 8;
            if (len < 0 || body + len > data.Length)
            {
                throw new ContentException($"{name}: truncated PNG");
            }
            if (System.Text.Encoding.ASCII.GetString(data, pos + 4, 4) == "IHDR")
            {
                return ParseHeader(data, body, len, name);
            }
            pos = body + len + 4; // skip the CRC
        }
        throw new ContentException($"{name}: no IHDR");
    }

    private static void CheckSignature(byte[] data, string name)
    {
        ReadOnlySpan<byte> sig = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(sig))
        {
            throw new ContentException($"{name}: not a PNG");
        }
    }

    private static (int Width, int Height, int Depth) ParseHeader(byte[] data, int body, int len, string name)
    {
        if (len < 13)
        {
            throw new ContentException($"{name}: truncated PNG");
        }
        var width = (data[body] << 24) | (data[body + 1] << 16) | (data[body + 2] << 8) | data[body + 3];
        var height = (data[body + 4] << 24) | (data[body + 5] << 16) | (data[body + 6] << 8) | data[body + 7];
        var depth = data[body + 8];
        if (data[body + 9] != 0 || data[body + 12] != 0 || (depth != 8 && depth != 16))
        {
            throw new ContentException($"{name}: want an 8- or 16-bit greyscale PNG without interlacing");
        }
        // The decoded rows must fit one array (the terrain's are far smaller: a map is at most TerrainMap.MaxSide + 1 a side).
        if (width <= 0 || height <= 0)
        {
            throw new ContentException($"{name}: a PNG of {(uint)width}x{(uint)height} pixels");
        }
        if (((long)width * (depth / 8) + 1) * height > Array.MaxLength)
        {
            throw new ContentException($"{name}: a {(uint)width}x{(uint)height} PNG is too large");
        }
        return (width, height, depth);
    }

    /// <summary>Decodes a greyscale PNG.</summary>
    /// <exception cref="ContentException">Not a supported PNG.</exception>
    public static Image Read(byte[] data, string name)
    {
        CheckSignature(data, name);
        int width = 0, height = 0, depth = 0;
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = pos + 8;
            if (len < 0 || body + len > data.Length)
            {
                throw new ContentException($"{name}: truncated PNG");
            }
            if (type == "IHDR")
            {
                (width, height, depth) = ParseHeader(data, body, len, name);
            }
            else if (type == "IDAT")
            {
                idat.Write(data, body, len);
            }
            else if (type == "IEND")
            {
                break;
            }
            pos = body + len + 4; // skip the CRC
        }
        if (width <= 0 || height <= 0)
        {
            throw new ContentException($"{name}: no IHDR");
        }
        var bpp = depth / 8;
        var stride = width * bpp;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var n = z.Read(raw, read, raw.Length - read);
                if (n == 0)
                {
                    throw new ContentException($"{name}: image data too short");
                }
                read += n;
            }
        }
        var prev = new byte[stride];
        var line = new byte[stride];
        var samples = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, line, 0, stride);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? line[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                line[x] = filter switch
                {
                    0 => line[x],
                    1 => (byte)(line[x] + a),
                    2 => (byte)(line[x] + b),
                    3 => (byte)(line[x] + ((a + b) >> 1)),
                    4 => (byte)(line[x] + Paeth(a, b, c)),
                    _ => throw new ContentException($"{name}: bad filter {filter}"),
                };
            }
            for (var x = 0; x < width; x++)
            {
                samples[y * width + x] = depth == 16 ? (line[x * 2] << 8) | line[x * 2 + 1] : line[x];
            }
            (prev, line) = (line, prev);
        }
        return new Image(width, height, depth, samples);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
