// TerrainBench measures loading a terrain map: decoding the heightmap PNG, building the
// per-class grade and cost grids, and the memory the layers take. With a size (N for N x N,
// or W x H such as 6000x3000) it makes a synthetic map (rolling hills, ridges, value noise,
// roads of three kinds, a forest map, a river into a lake, a pad) and encodes it to PNG in
// memory first, so the decode is measured too; with a .json path it loads that map file.
//
// With -battle N it also runs a world on the map (the map-size check, docs/design.md "Map
// size"): the world built and the terrain attached, routing's caches (-prepare true), fog of
// war (-fog true), ticks of the bare map (no units), then N units (half a side) fighting near
// the map's far corner while one technical drives from the near corner to the far one; it
// reports load, memory, tick p50/p99/max and the hash with and without a terrain change.
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static class TerrainBench
{
    public sealed record WorldOptions(int Battle, uint Ticks, bool Prepare, bool Fog, string Content, string Setup);

    public static int Run(string arg, string outPath, WorldOptions? world = null)
    {
        var rules = TerrainLoader.LoadRules(Path.Combine("content", "terrain", "rules.json"));
        string json;
        string? tmp = null;
        string label;
        if (ParseSize(arg) is { } size)
        {
            tmp = Path.Combine(Path.GetTempPath(), "simrun-terrain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            Synthetic(tmp, size.W, size.H);
            json = Path.Combine(tmp, "map.json");
            label = $"synthetic {size.W}x{size.H}";
        }
        else
        {
            json = arg;
            label = arg;
        }
        try
        {
            var before = Live();
            var allocBefore = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            var map = TerrainLoader.LoadMap(json, rules);
            var loadMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var digest = map.Digest();
            var digestMs = sw.Elapsed.TotalMilliseconds;
            var after = Live();
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
                ["map"] = label,
                ["date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["machine"] = $"{Environment.ProcessorCount} cpus, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}",
                ["width"] = map.Width,
                ["height"] = map.Height,
                ["loadMs"] = Math.Round(loadMs, 1),
                ["digestMs"] = Math.Round(digestMs, 1),
                ["layerBytes"] = map.Bytes,
                ["layerBytesPerCell"] = Math.Round((double)map.Bytes / ((long)map.Width * map.Height), 2),
                ["retainedBytes"] = after - before,
                ["allocatedBytes"] = alloc,
                ["passablePercent"] = rules.Classes.Select((c, k) => $"{c.Id} {100.0 * passable[k] / ((long)map.Width * map.Height):0.0}").ToArray(),
                ["digest"] = digest.ToString("x16", CultureInfo.InvariantCulture),
            };
            if (world is not null)
            {
                result["world"] = RunWorld(map, world, before);
            }
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

    // ParseSize reads "N" (N x N) or "WxH"; null for anything else (a map file).
    private static (int W, int H)? ParseSize(string arg)
    {
        var parts = arg.Split('x');
        if (parts.Length is 1 or 2 && parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            var w = int.Parse(parts[0], CultureInfo.InvariantCulture);
            return (w, parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : w);
        }
        return null;
    }

    // RunWorld runs the world phase (see the file comment) and returns its numbers.
    private static Dictionary<string, object> RunWorld(TerrainMap map, WorldOptions o, long baseBytes)
    {
        var content = ContentLoader.LoadDirectory(o.Content);
        var setup = Replay.LoadSetup(o.Setup).For(content, 42) with { Fog = o.Fog };
        var match = MatchCompiler.Compile(content, setup);
        int W = map.Width, H = map.Height;
        var sw = Stopwatch.StartNew();
        var w = new World(new Grid(W, H), match);
        w.AttachTerrain(map);
        var worldMs = sw.Elapsed.TotalMilliseconds;
        double prepareMs = 0;
        if (o.Prepare)
        {
            sw.Restart();
            w.PrepareRouting();
            prepareMs = sw.Elapsed.TotalMilliseconds;
        }
        var res = new Dictionary<string, object>
        {
            ["fog"] = o.Fog,
            ["worldMs"] = Math.Round(worldMs, 1),
            ["prepareRouting"] = o.Prepare,
            ["prepareRoutingMs"] = Math.Round(prepareMs, 1),
        };

        // The bare map: no units (the first tick builds the fog's sight tables).
        res["firstTickMs"] = Math.Round(Ticks(w, 1)[0], 2);
        res["firstTickSystems"] = Worst;
        res["bareTickMs"] = Stats(Ticks(w, Math.Min(o.Ticks, 200)));

        // The hash: once in full, once cached, once after a terrain change (a pad: the terrain's digest again).
        sw.Restart();
        w.Hash();
        res["hashMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
        sw.Restart();
        w.Hash();
        res["hashCachedMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 3);
        map.SetPad(new Cell(W / 2, H / 4), 6, 6, map.GroundHeightAt(new Cell(W / 2, H / 4).Center()), 2);
        res["tickAfterTerrainChangeMs"] = Math.Round(Ticks(w, 1)[0], 2); // routing, fog and the clearance grid catch up with the change
        res["tickAfterTerrainChangeSystems"] = Worst;
        sw.Restart();
        w.Hash();
        res["hashAfterTerrainChangeMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

        if (!o.Prepare)
        {
            // The loading step for a big map: the clearance grids and every class's regions, no sectors (World.PrepareRouting(false)).
            sw.Restart();
            w.PrepareRouting(everySector: false);
            res["prepareRegionsMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        }
        if (Environment.GetEnvironmentVariable("ROUTING_COLD_SAMPLE") == "1")
        {
            var samples = new List<object>();
            var router = w.Routing;
            router.LegacyRouting = Environment.GetEnvironmentVariable("ROUTING_LEGACY") == "1";
            router.TraceAbstract = true;
            var rng = new Random(42);
            var sampleCount = int.TryParse(Environment.GetEnvironmentVariable("ROUTING_SAMPLES"), out var count) ? count : 20;
            foreach (var tc in new[] { "foot", "wheeled", "tracked" })
            {
                var k = router.Nav.Class(map.Rules.Classes.Select(c => c.Id).ToList().IndexOf(tc), tc == "foot" ? 1 : 2);
                router.Component(k, 0);
                for (var i = 0; i < sampleCount; i++)
                {
                    router.ClearGraphsForTests();
                    var from = router.NearestPassable(k, new Cell(2 + rng.Next(100), 2 + rng.Next(100)), 128);
                    var to = router.NearestPassable(k, new Cell(W - 3 - rng.Next(100), H - 3 - rng.Next(100)), 128);
                    for (var attempt = 0; router.Component(k, from) == 0 || router.Component(k, from) != router.Component(k, to); attempt++)
                    {
                        if (attempt == 256) { throw new InvalidOperationException("No connected far sample found"); }
                        from = router.NearestPassable(k, new Cell(2 + rng.Next(160), 2 + rng.Next(160)), 128);
                        to = router.NearestPassable(k, new Cell(W - 3 - rng.Next(160), H - 3 - rng.Next(160)), 128);
                    }
                    var path = new List<int>();
                    var route = new List<int>();
                    sw.Restart();
                    router.FindRoute(k, from, to, path, route);
                    var elapsed = sw.Elapsed.TotalMilliseconds;
                    Console.Error.WriteLine($"{tc} sample {i}: {elapsed:0.0} ms, {router.AbstractCaps} caps, {router.Graph(k).Builds} sectors: {string.Join("; ", router.AbstractRuns)}");
                    samples.Add(new { terrainClass = tc, from, to, milliseconds = elapsed, router.AbstractCaps,
                        router.AbstractExpanded, builds = router.Graph(k).Builds, arrived = route.Count > 0 && route[^1] == to });
                }
            }
            res["coldRouteSamples"] = samples;
            res["coldRouteLegacy"] = router.LegacyRouting;
        }
        if (o.Battle > 0)
        {
            // Two armies near the far corner (bottom right), attack-moving into each other, and one technical
            // driving from the near corner (top left) to the far one.
            var p1 = match.Players[0];
            var p2 = match.Players[1];
            var half = o.Battle / 2;
            var a = Bench.Army(w, p1, half, W - 170, W - 140, H - 110, H - 10, Facing.East, vehicles: true);
            var b = Bench.Army(w, p2, o.Battle - half, W - 40, W - 10, H - 110, H - 10, Facing.West, vehicles: true);
            w.Issue(new Command { Tick = w.Tick + World.InputDelay, Player = p1.Id, Kind = CommandKind.AttackMove, Units = a, Cell = new Cell(W - 25, H - 60) });
            w.Issue(new Command { Tick = w.Tick + World.InputDelay, Player = p2.Id, Kind = CommandKind.AttackMove, Units = b, Cell = new Cell(W - 155, H - 60) });
            var tech = w.Spawn(p1.Find(content, "technical")!, new Cell(12, 12), Facing.East);
            var driver = w.Spawn(p1.Find(content, "Rifleman")!, new Cell(12, 12), Facing.East);
            w.Issue(new Command { Tick = w.Tick, Player = p1.Id, Kind = CommandKind.Enter, Units = [driver], Target = tech });
            var goal = new Cell(W - 20, H - 140);
            w.Issue(new Command { Tick = w.Tick + 40, Player = p1.Id, Kind = CommandKind.Move, Units = [tech], Cell = goal });
            var start = w.Unit(tech)!.Pos;
            var times = Ticks(w, o.Ticks);
            var t = w.Unit(tech);
            res["battleUnits"] = o.Battle;
            res["battleTicks"] = times.Count;
            res["battleTickMs"] = Stats(times);
            if (times.Count > 40) { res["crossMapOrderTickMs"] = Math.Round(times[40], 3); }
            res["battleWorstTick"] = Worst;
            res["battleMeanSystems"] = Means;
            res["aliveAfter"] = w.Units.Count(u => u.Alive && u.Inside == 0 && u.Id != tech);
            var ends = t is not null && t.Route.Length > 0 && t.Route[^1] == goal.Y * W + goal.X ? "ending at the goal" : "NOT ending at the goal";
            res["crossMapRoute"] = t is null ? "lost" : string.Create(CultureInfo.InvariantCulture,
                $"{(t.Pos - start).Len().Raw / Fixed.OneRaw} cells driven toward {goal}, {Math.Max(Math.Abs(t.Pos.X.Floor() - goal.X), Math.Abs(t.Pos.Y.Floor() - goal.Y))} left, route {t.Route.Length} waypoints {ends}");
            if (t is not null)
            {
                // A goal beside the first: nearly the same corridor, over sectors the first route built (a warm cross-map route).
                var again = new Cell(goal.X - 40, goal.Y - 20);
                w.Issue(new Command { Tick = w.Tick, Player = p1.Id, Kind = CommandKind.Move, Units = [tech], Cell = again });
                res["crossMapRouteWarmMs"] = Math.Round(Ticks(w, 40).Max(), 1);
                res["crossMapRouteWarm"] = w.Unit(tech) is { } back ? $"route {back.Route.Length} waypoints, " +
                    (back.Route.Length > 0 && back.Route[^1] == again.Y * W + again.X ? "ending at the goal" : "NOT ending at the goal") : "lost";
                res["crossMapRouteWarmSystems"] = Worst;
                // A group of 20 soldiers from the near corner to the far one (a group order: flow fields along a corridor).
                var squad = Bench.Army(w, p1, 20, 20, 30, 40, 60, Facing.East, vehicles: false);
                w.Issue(new Command { Tick = w.Tick, Player = p1.Id, Kind = CommandKind.Move, Units = squad, Cell = new Cell(W - 60, H - 200) });
                res["crossMapGroupMs"] = Math.Round(Ticks(w, 40).Max(), 1);
                res["crossMapGroupSystems"] = Worst;
            }
        }
        var (nav, graphs, regions, buffers) = w.Routing.Bytes();
        var navs = w.Routing.Nav;
        res["navClasses"] = Enumerable.Range(0, navs.Classes)
            .Select(k => $"{map.Rules.Classes[navs.TerrainClass(k)].Id} w{navs.Width(k)}{(navs.NoRoads(k) ? " crowd" : "")}").ToArray();
        long nodes = 0, edges = 0, portals = 0;
        for (var k = 0; k < navs.Classes; k++)
        {
            var (n, e, p) = w.Routing.Graph(k).Counts();
            (nodes, edges, portals) = (nodes + n, edges + e, portals + p);
        }
        res["graphCounts"] = new Dictionary<string, long> { ["nodes"] = nodes, ["edges"] = edges, ["portals"] = portals };
        res["routingBytes"] = new Dictionary<string, long>
        {
            ["nav"] = nav,
            ["graphs"] = graphs,
            ["regions"] = regions,
            ["buffers"] = buffers,
        };
        var retained = Live() - baseBytes;
        res["retainedBytesWithWorld"] = retained;
        res["retainedBytesPerCell"] = Math.Round((double)retained / ((long)W * H), 2);
        res["workingSetBytes"] = Environment.WorkingSet;
        GC.KeepAlive(w);
        return res;
    }

    // Live is the bytes the managed heap holds after a full, compacting collection (the large object heap too).
    private static long Live()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.HeapSizeBytes - info.FragmentedBytes;
    }

    // Ticks steps the world n times and returns each tick's wall time (ms); the slowest tick's systems go to Worst.
    private static List<double> Ticks(World w, uint n)
    {
        var times = new List<double>((int)n);
        var probe = new PhaseProbe();
        w.Probe = probe;
        var worst = -1.0;
        for (var i = 0; i < n; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            w.Step();
            var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            times.Add(ms);
            if (ms > worst)
            {
                worst = ms;
                Worst = string.Join(", ", probe.Ms.Select((v, k) => (v, k)).Where(p => p.v >= 0.5).OrderByDescending(p => p.v)
                    .Select(p => string.Create(CultureInfo.InvariantCulture, $"{(StepPhase)p.k} {p.v:0.0}"))) + $" (tick {w.Tick})";
            }
        }
        w.Probe = null;
        Means = string.Join(", ", probe.Total.Select((v, k) => (v: v / Math.Max(1, n), k)).Where(p => p.v >= 0.05).OrderByDescending(p => p.v)
            .Select(p => string.Create(CultureInfo.InvariantCulture, $"{(StepPhase)p.k} {p.v:0.00}")));
        return times;
    }

    // The systems' mean time a tick over the last Ticks call (ms, those over 0.05 ms).
    private static string Means = "";

    // The systems of the slowest tick of the last Ticks call (ms, those over half a millisecond).
    private static string Worst = "";

    private sealed class PhaseProbe : IStepProbe
    {
        public readonly double[] Ms = new double[Enum.GetValues<StepPhase>().Length];
        private long _last;

        public readonly double[] Total = new double[Enum.GetValues<StepPhase>().Length];

        public void Begin()
        {
            for (var k = 0; k < Ms.Length; k++)
            {
                Total[k] += Ms[k];
            }
            Array.Clear(Ms);
            _last = Stopwatch.GetTimestamp();
        }

        public void After(StepPhase phase)
        {
            var now = Stopwatch.GetTimestamp();
            Ms[(int)phase] += (now - _last) * 1000.0 / Stopwatch.Frequency;
            _last = now;
        }
    }

    private static Dictionary<string, double> Stats(List<double> xs)
    {
        if (xs.Count == 0)
        {
            return [];
        }
        var s = xs.OrderBy(x => x).ToList();
        double P(int p) => s[Math.Min(s.Count - 1, s.Count * p / 100)];
        return new()
        {
            ["p50"] = Math.Round(P(50), 3),
            ["p99"] = Math.Round(P(99), 3),
            ["max"] = Math.Round(s[^1], 3),
            ["mean"] = Math.Round(s.Average(), 3),
        };
    }

    // Synthetic writes a w x h map as map.json + map.png + map_forest.png: hills and ridges from a
    // few sine waves and three octaves of value noise (heights from -20 to 120 m; the far corner
    // fades to gentle ground for the battle), a highway across, a paved diagonal and a dirt track,
    // a forest map, a river from the north edge into a lake, and a pad.
    private static void Synthetic(string dir, int w, int h)
    {
        var corners = new int[(w + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        {
            for (var x = 0; x <= w; x++)
            {
                var v = 0.25 + 0.15 * Math.Sin(x * 0.006) * Math.Cos(y * 0.007) + 0.05 * Math.Sin((x + 2 * y) * 0.019)
                        + 0.01 * Math.Sin(x * 0.11 + y * 0.05) + 0.12 * Noise(x, y, 256) + 0.05 * Noise(x, y, 64) + 0.02 * Noise(x, y, 16);
                // The far corner (the battle's) fades to the mean height within 400 cells, so the armies stand on gentle ground.
                var far = Math.Clamp(Math.Max(w - x, h - y) / 400.0, 0, 1);
                v = 0.25 + (v - 0.25) * (0.15 + 0.85 * far);
                corners[y * (w + 1) + x] = (int)Math.Clamp(v * 65535, 0, 65535);
            }
        }
        var forest = new int[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var f = Math.Sin(x * 0.02 + 1) * Math.Sin(y * 0.025) + 0.4 * Noise(x, y, 48);
                var far = Math.Max(w - x, h - y) < 300;
                forest[y * w + x] = far ? 0 : f > 0.7 ? 255 : f > 0.5 ? 160 : f > 0.3 ? 90 : 0;
            }
        }
        File.WriteAllBytes(Path.Combine(dir, "map.png"), Grey(w + 1, h + 1, 16, corners));
        File.WriteAllBytes(Path.Combine(dir, "map_forest.png"), Grey(w, h, 8, forest));
        string P(double fx, double fy) => $"[{(int)(fx * (w - 1))}, {(int)(fy * (h - 1))}]";
        File.WriteAllText(Path.Combine(dir, "map.json"), $$"""
            { "width": {{w}}, "height": {{h}}, "heightmap": "map.png", "forestMap": "map_forest.png", "minHeight": -20, "maxHeight": 120,
              "roads": [
                { "kind": "highway", "points": [{{P(0, 0.5)}}, {{P(0.33, 0.33)}}, {{P(0.66, 0.6)}}, {{P(1, 0.5)}}], "width": 4 },
                { "kind": "paved", "points": [{{P(0, 0)}}, {{P(0.5, 0.5)}}, {{P(1, 1)}}], "width": 3 },
                { "kind": "dirt", "points": [{{P(0.2, 1)}}, {{P(0.25, 0.6)}}, {{P(0.4, 0.1)}}], "width": 2 }
              ],
              "water": [
                { "kind": "river", "points": [{{P(0.7, 0)}}, {{P(0.72, 0.1)}}, {{P(0.68, 0.2)}}], "width": 9, "height": 300 },
                { "kind": "lake", "polygon": [{{P(0.66, 0.2)}}, {{P(0.72, 0.21)}}, {{P(0.71, 0.27)}}, {{P(0.65, 0.26)}}], "height": 300 }
              ],
              "pads": [{ "rect": [{{w / 4}}, {{h / 4}}, 12, 8], "height": 100 }] }
            """);
    }

    // Noise is smooth value noise in -1..1 with cells of the given size (a fixed hash of the lattice points).
    private static double Noise(int x, int y, int cell)
    {
        int gx = x / cell, gy = y / cell;
        double fx = (double)(x % cell) / cell, fy = (double)(y % cell) / cell;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        double L(int i, int j)
        {
            var n = (uint)(i * 374761393 + j * 668265263 + cell * 2147483647);
            n = (n ^ (n >> 13)) * 1274126177u;
            return (n ^ (n >> 16)) / (double)uint.MaxValue * 2 - 1;
        }
        var top = L(gx, gy) + (L(gx + 1, gy) - L(gx, gy)) * fx;
        var bottom = L(gx, gy + 1) + (L(gx + 1, gy + 1) - L(gx, gy + 1)) * fx;
        return top + (bottom - top) * fy;
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
