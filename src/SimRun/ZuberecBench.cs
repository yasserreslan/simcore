// ZuberecBench measures the sim tick on a real map with a town (the game's first real map, Zuberec: 2,000 x 1,500
// cells and a town file of about a thousand neutral buildings) against the same armies without the town, to find what
// the town costs a tick. The map and the town file come from the game (they are not in this repository):
//
//   dotnet run -c Release --project src/SimRun -- -zuberec-bench DIR -town FILE [-battle 200] [-maxticks 2400] [-fog true]
//
// DIR holds the map's map.json (its heightmap and forest map) and, optionally, its layout.json (the client's spawn
// spots: the convoy's start and waypoint, the first long route's end, the square). FILE is a town file
// ({"buildings": [{"archetype", "cell", "facing"}], "streets", "groves"}). The world is made as the client makes it
// (SimSession.Start and Town.Prepare in the game): the town's pads flattened and its forest cleared before the world is
// made, the Sentinel lobby (content/setups/sentinel.json) trimmed to one rifleman each, a sandbox, routing prepared light
// (regions, no sectors), every building spawned neutral by a SandboxSpawn of player 2 at its top-left cell and turn.
// Then a convoy (three Humvees, a cargo truck and an Abrams, each with its driver, and six riflemen) drives from the
// convoy's start to the waypoint and on to the route's end, as the demo's does, and (with -battle N) N units fight in
// the streets round the square. Each scene runs with the town and without it (the same map, no pads, no buildings),
// and the battle also on a synthetic map of the same size; for each the tick's p50 / p99 / max / mean and every
// phase's (StepPhase), measured with a probe outside the sim.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimRun;

internal static class ZuberecBench
{
    private sealed record Building(string Archetype, int X, int Y, int Facing);

    private sealed record Street(int X0, int Y0, int X1, int Y1);

    private sealed record Town(string Map, List<Building> Buildings, List<Street> Streets, List<(int X, int Y, int W, int H)> Groves);

    private sealed record Spots(Cell ConvoyStart, Cell Waypoint, Cell RouteEnd, Cell Square);

    public static int Run(string dir, string townFile, int battle, uint ticks, bool fog, string contentDir, string outPath, string only)
    {
        var content = ContentLoader.LoadDirectory(contentDir);
        var rules = TerrainLoader.LoadRules(Path.Combine(contentDir, "..", "terrain", "rules.json"));
        var mapJson = Path.Combine(dir, "map.json");
        var town = LoadTown(townFile);
        var spots = LoadSpots(Path.Combine(dir, "layout.json"));
        var result = new Dictionary<string, object>
        {
            ["map"] = town.Map != "" ? town.Map : Path.GetFileName(Path.GetFullPath(dir)),
#if DEBUG
            ["build"] = "Debug",
#else
            ["build"] = "Release",
#endif
            ["townBuildings"] = town.Buildings.Count,
            ["battle"] = battle,
            ["ticks"] = ticks,
            ["fog"] = fog,
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var scenes = new List<(string Name, bool Town, bool Synthetic, int Battle)>
        {
            ("convoy_town", true, false, 0),
            ("convoy_notown", false, false, 0),
        };
        if (battle > 0)
        {
            scenes.Add(("battle_town", true, false, battle));
            scenes.Add(("battle_notown", false, false, battle));
            scenes.Add(("battle_synthetic", false, true, battle));
        }
        string? tmp = null;
        try
        {
            foreach (var s in scenes)
            {
                if (only != "" && !only.Split(',').Contains(s.Name))
                {
                    continue;
                }
                TerrainMap map;
                if (s.Synthetic)
                {
                    var probe = TerrainLoader.LoadMap(mapJson, rules, content);
                    if (tmp is null)
                    {
                        tmp = Path.Combine(Path.GetTempPath(), "simrun-zuberec-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tmp);
                        TerrainBench.Synthetic(tmp, probe.Width, probe.Height);
                    }
                    map = TerrainLoader.LoadMap(Path.Combine(tmp, "map.json"), rules, content);
                }
                else
                {
                    map = TerrainLoader.LoadMap(mapJson, rules, content);
                }
                if (s.Town)
                {
                    Prepare(town, map, content);
                }
                Console.Error.WriteLine($"zuberec-bench: {s.Name} ...");
                result[s.Name] = RunScene(content, contentDir, map, s.Town ? town : null, spots, s.Battle, ticks, fog);
            }
        }
        finally
        {
            if (tmp is not null)
            {
                Directory.Delete(tmp, true);
            }
        }
        var text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(text);
        if (outPath != "")
        {
            File.WriteAllText(outPath, text + "\n");
        }
        return 0;
    }

    private static Dictionary<string, object> RunScene(ContentSet content, string contentDir, TerrainMap map, Town? town, Spots spots,
        int battle, uint ticks, bool fog)
    {
        var setup = Replay.LoadSetup(Path.Combine(contentDir, "..", "setups", "sentinel.json")).For(content, 42) with { Fog = fog };
        setup = setup with
        {
            Scenario = Scenario.SandboxName,
            Players = [.. setup.Players.Select(p => p with { Army = [.. p.Army.Where(a => a.Driver is null && a.Unit == "Rifleman").Take(1).Select(a => a with { Count = 1 })] })],
        };
        var sw = Stopwatch.StartNew();
        var w = Replay.NewWorld(content, setup, map);
        w.PrepareRouting(everySector: false); // the client's light preparation on a map over 2,000,000 cells
        var res = new Dictionary<string, object> { ["worldAndRoutingMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1) };
        var p1 = w.Match.Players[0];
        var p2 = w.Match.Players[1];
        if (town is not null)
        {
            foreach (var b in town.Buildings)
            {
                w.Issue(new Command { Tick = w.Tick + World.InputDelay, Player = p2.Id, Kind = CommandKind.SandboxSpawn, Cell = new Cell(b.X, b.Y), Unit = b.Archetype, Facing = b.Facing });
            }
        }
        // The convoy, as the demo's: five vehicles in a column (the leader furthest south), a rifleman each who drives it, six riflemen.
        var rifleman = p1.Find(content, "Rifleman")!;
        string[] fleet = ["humvee", "humvee", "humvee", "sentinel_cargo_truck", "main_battle_tank"];
        var convoy = new List<uint>();
        for (var i = 0; i < fleet.Length; i++)
        {
            var at = Near(w, new Cell(spots.ConvoyStart.X, spots.ConvoyStart.Y + (fleet.Length - 1 - i) * 9));
            var v = w.Spawn(p1.Find(content, fleet[i])!, at, Facing.South);
            var d = w.Spawn(rifleman, at, Facing.South);
            w.Issue(new Command { Tick = w.Tick + 4, Player = p1.Id, Kind = CommandKind.Enter, Units = [d], Target = v });
            convoy.Add(v);
        }
        var squad = new List<uint>();
        for (var i = 0; i < 6; i++)
        {
            squad.Add(w.Spawn(rifleman, Near(w, new Cell(spots.ConvoyStart.X + 4 + i % 3 * 2, spots.ConvoyStart.Y - 8 + i / 3 * 2)), Facing.South));
        }
        const uint orderAt = 90;
        for (var i = 0; i < convoy.Count; i++)
        {
            w.Issue(new Command { Tick = orderAt, Player = p1.Id, Kind = CommandKind.Move, Units = [convoy[i]], Cell = new Cell(spots.Waypoint.X - 4 + i * 4, spots.Waypoint.Y) });
        }
        w.Issue(new Command { Tick = orderAt, Player = p1.Id, Kind = CommandKind.Move, Units = squad, Cell = new Cell(spots.Square.X, spots.Square.Y + 18) });
        if (battle > 0)
        {
            // Two armies either side of the square, attack-moving through each other's side of the town.
            var c = spots.Square;
            var a = Army(w, p1, battle / 2, new Cell(c.X - 60, c.Y + 30), "humvee");
            var b = Army(w, p2, battle - battle / 2, new Cell(c.X + 60, c.Y + 30), "technical");
            w.Issue(new Command { Tick = orderAt, Player = p1.Id, Kind = CommandKind.AttackMove, Units = a, Cell = new Cell(c.X + 60, c.Y + 30) });
            w.Issue(new Command { Tick = orderAt, Player = p2.Id, Kind = CommandKind.AttackMove, Units = b, Cell = new Cell(c.X - 60, c.Y + 30) });
        }
        var leg2 = new bool[convoy.Count];
        var probe = new TickProbe();
        w.Probe = probe;
        var total = new List<double>((int)ticks);
        var phases = Enum.GetValues<StepPhase>().Length;
        var perPhase = Enumerable.Range(0, phases).Select(_ => new List<double>((int)ticks)).ToArray();
        var worst = (Ms: -1.0, Tick: 0u, Phases: "");
        while (w.Tick < ticks)
        {
            // Leg two, per vehicle: at the waypoint (within 24 cells), on to the route's end.
            for (var i = 0; i < convoy.Count; i++)
            {
                if (!leg2[i] && w.Tick > orderAt && w.Unit(convoy[i]) is { } v &&
                    (v.Pos - spots.Waypoint.Center()).Len() < Fixed.FromInt(24))
                {
                    leg2[i] = true;
                    w.Issue(new Command { Tick = w.Tick + World.InputDelay, Player = p1.Id, Kind = CommandKind.Move, Units = [convoy[i]], Cell = new Cell(spots.RouteEnd.X - i * 5, spots.RouteEnd.Y + i % 2 * 3) });
                }
            }
            var t0 = Stopwatch.GetTimestamp();
            w.Step();
            var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            total.Add(ms);
            for (var k = 0; k < phases; k++)
            {
                perPhase[k].Add(probe.Ms[k]);
            }
            if (ms > worst.Ms)
            {
                worst = (ms, w.Tick, string.Join(", ", probe.Ms.Select((x, k) => (x, k)).Where(p => p.x >= 0.5).OrderByDescending(p => p.x)
                    .Select(p => string.Create(CultureInfo.InvariantCulture, $"{(StepPhase)p.k} {p.x:0.0}"))));
            }
        }
        w.Probe = null;
        res["units"] = w.Units.Count(u => u.Alive);
        res["structures"] = w.Units.Count(u => u.Alive && u.Def.IsStructure);
        res["mobileAlive"] = w.Units.Count(u => u.Alive && !u.Def.IsStructure);
        res["convoyAtWaypoint"] = leg2.Count(x => x);
        res["convoyLeaderCellsToGo"] = w.Unit(convoy[0]) is { } lead
            ? (lead.Pos - (leg2[0] ? spots.RouteEnd : spots.Waypoint).Center()).Len().Raw / Fixed.OneRaw : -1;
        res["tickMs"] = Stats(total);
        res["tickMsAfterOrders"] = Stats(total.Skip((int)orderAt + 60).ToList());
        res["worstTick"] = string.Create(CultureInfo.InvariantCulture, $"{worst.Ms:0.0} ms at tick {worst.Tick}: {worst.Phases}");
        var ph = new Dictionary<string, object>();
        for (var k = 0; k < phases; k++)
        {
            var st = Stats(perPhase[k]);
            if (st["mean"] >= 0.005 || st["max"] >= 1)
            {
                ph[((StepPhase)k).ToString()] = st;
            }
        }
        res["phases"] = ph;
        res["hash"] = w.Hash().ToString("x16", CultureInfo.InvariantCulture);
        return res;
    }

    // Near is the nearest passable cell to c.
    private static Cell Near(World w, Cell c) => w.Grid.TrySpiral(c, 0, out var p) ? p : c;

    // Army spawns count units of p round centre (a 30 x 30 box): riflemen, gunners and tank hunters, and one vehicle in ten
    // with a rifleman who drives it. Returns the IDs to order.
    private static List<uint> Army(World w, CompiledPlayer p, int count, Cell centre, string vehicle)
    {
        string[] mix = ["Rifleman", "Rifleman", "Gunner", "Tank hunter"];
        var ids = new List<uint>();
        var veh = p.Find(w.Content, vehicle);
        var driver = p.Find(w.Content, "Rifleman")!;
        var made = 0;
        for (var i = 0; made < count; i++)
        {
            var c = Near(w, new Cell(centre.X - 15 + i * 7 % 30, centre.Y - 15 + i * 13 % 30));
            if (veh is not null && i % 10 == 9 && count - made >= 2)
            {
                var v = w.Spawn(veh, c, p.Id == 1 ? Facing.East : Facing.West);
                var d = w.Spawn(driver, c, p.Id == 1 ? Facing.East : Facing.West);
                w.Issue(new Command { Tick = w.Tick + 4, Player = p.Id, Kind = CommandKind.Enter, Units = [d], Target = v });
                ids.Add(v);
                made += 2;
                continue;
            }
            ids.Add(w.Spawn(p.Find(w.Content, mix[i % mix.Length])!, c, p.Id == 1 ? Facing.East : Facing.West));
            made++;
        }
        return ids;
    }

    // Prepare flattens the town's pads and clears its forest before the world is made, as the client's Town.Prepare does:
    // the streets' pads at their mean height, each building's footprint as it stands and a cell round it (at the street
    // height near a street, else its own mean), 3 cells of blend, the forest cleared 2 cells further; the groves cleared.
    private static void Prepare(Town town, TerrainMap map, ContentSet content)
    {
        const int blend = 3;
        long sum = 0, n = 0;
        foreach (var s in town.Streets)
        {
            for (var y = s.Y0; y <= s.Y1; y++)
            {
                for (var x = s.X0; x <= s.X1; x++)
                {
                    sum += map.Corner(x, y).Raw;
                    n++;
                }
            }
        }
        var streetHeight = Fixed.FromRaw(n > 0 ? sum / n : 0);
        void Clear(int x0, int y0, int x1, int y1)
        {
            for (var y = Math.Max(y0, 0); y < Math.Min(y1, map.Height); y++)
            {
                for (var x = Math.Max(x0, 0); x < Math.Min(x1, map.Width); x++)
                {
                    if (map.Forest(new Cell(x, y)) != 0)
                    {
                        map.SetForest(new Cell(x, y), 0);
                    }
                }
            }
        }
        void Pad(Street r, Fixed height)
        {
            var x0 = Math.Max(r.X0, 0);
            var y0 = Math.Max(r.Y0, 0);
            var pw = Math.Min(r.X1, map.Width) - x0;
            var ph = Math.Min(r.Y1, map.Height) - y0;
            if (pw > 0 && ph > 0)
            {
                map.SetPad(new Cell(x0, y0), pw, ph, height, blend);
            }
            Clear(r.X0 - 2, r.Y0 - 2, r.X1 + 2, r.Y1 + 2);
        }
        Fixed Mean(Street r)
        {
            long s2 = 0, n2 = 0;
            for (var y = r.Y0; y <= r.Y1; y++)
            {
                for (var x = r.X0; x <= r.X1; x++)
                {
                    s2 += map.Corner(x, y).Raw;
                    n2++;
                }
            }
            return Fixed.FromRaw(n2 > 0 ? s2 / n2 : 0);
        }
        foreach (var s in town.Streets)
        {
            Pad(s, streetHeight);
        }
        foreach (var b in town.Buildings)
        {
            if (content.Archetype(b.Archetype) is not { } a)
            {
                continue;
            }
            var (fw, fh) = World.Footprint(a, b.Facing);
            var r = new Street(b.X - 1, b.Y - 1, b.X + fw + 1, b.Y + fh + 1);
            var nearStreet = town.Streets.Any(s => r.X0 <= s.X1 + 3 && r.X1 >= s.X0 - 3 && r.Y0 <= s.Y1 + 3 && r.Y1 >= s.Y0 - 3);
            Pad(r, nearStreet ? streetHeight : Mean(r));
        }
        foreach (var g in town.Groves)
        {
            Clear(g.X, g.Y, g.X + g.W, g.Y + g.H);
        }
    }

    private static Town LoadTown(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var buildings = new List<Building>();
        foreach (var b in root.GetProperty("buildings").EnumerateArray())
        {
            var cell = b.GetProperty("cell");
            buildings.Add(new Building(b.GetProperty("archetype").GetString()!, cell[0].GetInt32(), cell[1].GetInt32(),
                b.TryGetProperty("facing", out var f) ? f.GetInt32() : 0));
        }
        var streets = new List<Street>();
        if (root.TryGetProperty("streets", out var ss))
        {
            foreach (var s in ss.EnumerateArray())
            {
                var from = s.GetProperty("from");
                var to = s.GetProperty("to");
                var side = s.TryGetProperty("sidewalk", out var sw) ? sw.GetInt32() : 0;
                streets.Add(new Street(Math.Min(from[0].GetInt32(), to[0].GetInt32()) - side, Math.Min(from[1].GetInt32(), to[1].GetInt32()) - side,
                    Math.Max(from[0].GetInt32(), to[0].GetInt32()) + side + 1, Math.Max(from[1].GetInt32(), to[1].GetInt32()) + side + 1));
            }
        }
        var groves = new List<(int, int, int, int)>();
        if (root.TryGetProperty("groves", out var gs))
        {
            foreach (var g in gs.EnumerateArray())
            {
                var r = g.GetProperty("rect");
                groves.Add((r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt32()));
            }
        }
        return new Town(root.TryGetProperty("map", out var m) ? m.GetString() ?? "" : "", buildings, streets, groves);
    }

    // LoadSpots reads the client's layout.json (the convoy's start and waypoint, the first route's end, the square), or
    // takes spots across the middle of a 2,000 x 1,500 map when there is none.
    private static Spots LoadSpots(string path)
    {
        if (!File.Exists(path))
        {
            return new Spots(new Cell(700, 375), new Cell(967, 863), new Cell(1981, 765), new Cell(967, 866));
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        static Cell C(JsonElement a) => new((int)a[0].GetDouble(), (int)a[1].GetDouble());
        var convoy = root.GetProperty("convoy");
        return new Spots(C(convoy.GetProperty("start")), C(convoy.GetProperty("waypoint")), C(root.GetProperty("routes")[0].GetProperty("to")),
            C(root.GetProperty("square")));
    }

    private sealed class TickProbe : IStepProbe
    {
        public readonly double[] Ms = new double[Enum.GetValues<StepPhase>().Length];
        private long _last;

        public void Begin()
        {
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
            return new() { ["p50"] = 0, ["p99"] = 0, ["max"] = 0, ["mean"] = 0 };
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
}
