// TerrainBench measures loading a terrain map: decoding the heightmap PNG, building the
// per-class grade and cost grids, and the memory the layers take. With a size it makes a
// synthetic hilly map (rolling hills, ridges, roads, forest) and encodes it to PNG in memory
// first, so the decode is measured too; with a .json path it loads that map file.
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using SimCore.Content;
using SimCore.Sim;

namespace SimRun;

internal static class TerrainBench
{
    public static int Run(string arg, string outPath)
    {
        var rules = TerrainLoader.LoadRules(Path.Combine("content", "terrain", "rules.json"));
        string json;
        string? tmp = null;
        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
        {
            tmp = Path.Combine(Path.GetTempPath(), "simrun-terrain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            Synthetic(tmp, size);
            json = Path.Combine(tmp, "map.json");
        }
        else
        {
            json = arg;
        }
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var before = GC.GetTotalMemory(true);
            var allocBefore = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            var map = TerrainLoader.LoadMap(json, rules);
            var loadMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var digest = map.Digest();
            var digestMs = sw.Elapsed.TotalMilliseconds;
            var after = GC.GetTotalMemory(true);
            var alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;
            var passable = new long[rules.Classes.Count];
            for (var k = 0; k < passable.Length; k++)
            {
                foreach (var c in map.CostGrid(k))
                {
                    passable[k] += c != 0 ? 1 : 0;
                }
            }
            var result = new Dictionary<string, object>
            {
                ["map"] = tmp is null ? json : $"synthetic {size}x{size}",
                ["width"] = map.Width,
                ["height"] = map.Height,
                ["loadMs"] = Math.Round(loadMs, 1),
                ["digestMs"] = Math.Round(digestMs, 1),
                ["layerBytes"] = map.Bytes,
                ["retainedBytes"] = after - before,
                ["allocatedBytes"] = alloc,
                ["passablePercent"] = rules.Classes.Select((c, k) => $"{c.Id} {100.0 * passable[k] / ((long)map.Width * map.Height):0.0}").ToArray(),
                ["digest"] = digest.ToString("x16", CultureInfo.InvariantCulture),
            };
            var text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(text);
            if (outPath != "")
            {
                File.WriteAllText(outPath, text + "\n");
            }
            return 0;
        }
        finally
        {
            if (tmp is not null)
            {
                Directory.Delete(tmp, true);
            }
        }
    }

    // Synthetic writes a size x size map: hills and ridges from a few sine waves (heights from
    // -16 to 48 m), a road across and a forest band, as map.json + map.png + map_forest.png.
    private static void Synthetic(string dir, int size)
    {
        var corners = new int[(size + 1) * (size + 1)];
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                var v = 0.4 + 0.3 * Math.Sin(x * 0.006) * Math.Cos(y * 0.007) + 0.1 * Math.Sin((x + 2 * y) * 0.019)
                        + 0.02 * Math.Sin(x * 0.11 + y * 0.05);
                corners[y * (size + 1) + x] = (int)Math.Clamp(v * 65535, 0, 65535);
            }
        }
        var forest = new int[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var f = Math.Sin(x * 0.02 + 1) * Math.Sin(y * 0.025);
                forest[y * size + x] = f > 0.6 ? 255 : f > 0.4 ? 160 : f > 0.25 ? 90 : 0;
            }
        }
        File.WriteAllBytes(Path.Combine(dir, "map.png"), Grey(size + 1, size + 1, 16, corners));
        File.WriteAllBytes(Path.Combine(dir, "map_forest.png"), Grey(size, size, 8, forest));
        File.WriteAllText(Path.Combine(dir, "map.json"), $$"""
            { "width": {{size}}, "height": {{size}}, "heightmap": "map.png", "forestMap": "map_forest.png",
              "roads": [{ "points": [[0, {{size / 2}}], [{{size / 3}}, {{size / 3}}], [{{size - 1}}, {{size / 2}}]], "width": 3 }],
              "pads": [{ "rect": [{{size / 4}}, {{size / 4}}, 12, 8], "height": 100 }] }
            """);
    }

    private static byte[] Grey(int w, int h, int depth, int[] samples)
    {
        var bpp = depth / 8;
        var raw = new byte[(w * bpp + 1) * h];
        for (var y = 0; y < h; y++)
        {
            var row = y * (w * bpp + 1);
            for (var x = 0; x < w; x++)
            {
                var v = samples[y * w + x];
                if (depth == 16)
                {
                    raw[row + 1 + x * 2] = (byte)(v >> 8);
                    raw[row + 2 + x * 2] = (byte)v;
                }
                else
                {
                    raw[row + 1 + x] = (byte)v;
                }
            }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
        {
            zs.Write(raw);
        }
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        Be(ihdr, 0, w);
        Be(ihdr, 4, h);
        ihdr[8] = (byte)depth;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Be(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        var len = new byte[4];
        Be(len, 0, body.Length);
        s.Write(len);
        s.Write(System.Text.Encoding.ASCII.GetBytes(type));
        s.Write(body);
        s.Write(new byte[4]); // CRC: the loader does not check it
    }
}
