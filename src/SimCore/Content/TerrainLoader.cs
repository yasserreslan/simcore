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
/// and rectangles, forest rectangles, and flatten pads. Heights map linearly from pixel 0 to
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
            if (c.Slopes is not { } s || s.Gentle <= 0 || s.Steep <= s.Gentle || s.Cliff <= s.Steep || s.Cliff > 255)
            {
                throw new ContentException($"{where}: slopes want 0 < gentle < steep < cliff <= 255 (percent grade)");
            }
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
                RoadCostPercent = c.RoadCost,
                ForestCostPercent = Four(c.ForestCost, "forestCost"),
                ForestSpeedPercent = Four(c.ForestSpeed, "forestSpeed"),
                ForestBlockedFrom = Math.Clamp(c.ForestBlockedFrom, 1, 4),
                DeployMaxGrade = c.DeployMaxGrade is { } dg ? Math.Clamp(dg, 0, 255) : s.Steep,
            });
        }
        if (classes.Count == 0)
        {
            throw new ContentException($"{name}: no movement classes");
        }
        return new TerrainRules { Classes = classes };
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
        if (m.Width <= 0 || m.Height <= 0 || m.Width > 4096 || m.Height > 4096)
        {
            throw new ContentException($"{name}: width and height want 1 to 4096 cells");
        }
        var dir = Path.GetDirectoryName(jsonPath) ?? ".";
        var (w, h) = (m.Width, m.Height);
        var corners = new int[(w + 1) * (h + 1)];
        if (m.Heightmap is { } hm)
        {
            var png = Png.Read(File.ReadAllBytes(Path.Combine(dir, hm)), $"{name}: {hm}");
            if (png.Width != w + 1 || png.Height != h + 1 || png.Depth != 16)
            {
                throw new ContentException($"{name}: {hm} wants a 16-bit greyscale PNG of {w + 1}x{h + 1} (corners), got {png.Width}x{png.Height} {png.Depth}-bit");
            }
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
            var png = Png.Read(File.ReadAllBytes(Path.Combine(dir, fm)), $"{name}: {fm}");
            if (png.Width != w || png.Height != h || png.Depth != 8)
            {
                throw new ContentException($"{name}: {fm} wants an 8-bit greyscale PNG of {w}x{h}");
            }
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
        foreach (var r in m.Roads ?? [])
        {
            if (r.Rect is not null)
            {
                Each(r.Rect, "roads.rect", i => flags[i] |= 4);
            }
            var pts = r.Points ?? [];
            for (var k = 0; k + 1 < pts.Count; k++)
            {
                RoadSegment(pts[k], pts[k + 1], Math.Max(r.Width, 1), w, h, flags, name);
            }
        }
        var terrain = new TerrainMap(w, h, corners, flags, rules);
        foreach (var p in m.Pads ?? [])
        {
            if (p.Rect is not { Count: 4 } rect)
            {
                throw new ContentException($"{name}: pads.rect wants [x, y, w, h]");
            }
            terrain.SetPad(new Cell(rect[0], rect[1]), rect[2], rect[3], Fixed.FromRaw((long)p.Height * RawPerMetre / 10), p.Blend);
        }
        return terrain;
    }

    // RoadSegment marks the cells of a road from a to b (cells), width cells wide, with integer
    // steps along the longer axis (no floats).
    private static void RoadSegment(List<int> a, List<int> b, int width, int w, int h, byte[] flags, string name)
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
                        flags[cy * w + cx] |= 4;
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

        [JsonPropertyName("roadCost")]
        public int RoadCost { get; set; } = 100;

        [JsonPropertyName("forestCost")]
        public List<int>? ForestCost { get; set; }

        [JsonPropertyName("forestSpeed")]
        public List<int>? ForestSpeed { get; set; }

        [JsonPropertyName("forestBlockedFrom")]
        public int ForestBlockedFrom { get; set; } = 4;

        [JsonPropertyName("deployMaxGrade")]
        public int? DeployMaxGrade { get; set; }
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

        // The renderer's biome (its ground textures and plants): visual only, never read here
        // and not in the terrain's digest.
        [JsonPropertyName("biome")]
        public string? Biome { get; set; }

        // The renderer's painted ground (gravel riverbeds, terraces, fields): visual only, never
        // read here and not in the terrain's digest.
        [JsonPropertyName("paint")]
        public JsonElement? Paint { get; set; }
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

    /// <summary>Decodes a greyscale PNG.</summary>
    /// <exception cref="ContentException">Not a supported PNG.</exception>
    public static Image Read(byte[] data, string name)
    {
        ReadOnlySpan<byte> sig = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(sig))
        {
            throw new ContentException($"{name}: not a PNG");
        }
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
                width = (data[body] << 24) | (data[body + 1] << 16) | (data[body + 2] << 8) | data[body + 3];
                height = (data[body + 4] << 24) | (data[body + 5] << 16) | (data[body + 6] << 8) | data[body + 7];
                depth = data[body + 8];
                if (data[body + 9] != 0 || data[body + 12] != 0 || (depth != 8 && depth != 16))
                {
                    throw new ContentException($"{name}: want an 8- or 16-bit greyscale PNG without interlacing");
                }
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
