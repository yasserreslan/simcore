// SimRun runs a match headless and prints the map as text. Run it from the
// repository root, or point -content and -setup at the right files.
//
//   dotnet run --project src/SimRun                            # bot vs bot, 10 minutes
//   dotnet run --project src/SimRun -- -every 20 -ticks 2400   # watch every second for 2 minutes
//   dotnet run --project src/SimRun -- -save match.json        # also save the replay
//   dotnet run --project src/SimRun -- -replay match.json      # play a saved replay
//   dotnet run -c Release --project src/SimRun -- -bench 100,500 -out bench/run.json   # headless benchmark (Bench.cs)
//   dotnet run -c Release --project src/SimRun -- -terrain-bench 1500        # terrain map load time and memory (TerrainBench.cs)
//   dotnet run -c Release --project src/SimRun -- -terrain-bench maps/x.json # the same for a map file
//   dotnet run -c Release --project src/SimRun -- -terrain-bench 6000x3000 -battle 200 -fog true -prepare true  # plus a world on it
//   dotnet run -c Release --project src/SimRun -- -move-bench all -map maps/bench/map.json -out bench/move.json  # movement at 10,000 units (MoveBench.cs)
//
// Each unit is drawn with its archetype's glyph from the content: player 1
// uppercase, player 2 lowercase. Drones are drawn over the ground, rockets in
// flight are *, every trap is ^ (this is an observer's view), # is rock.
using System.Globalization;
using System.Text;
using SimCore.Content;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static class Program
{
    private const string Usage = """
        Usage of simrun:
          -bench string
                benchmark instead: comma-separated unit counts, e.g. 100,500,1000 (see Bench.cs)
          -bench-sam int
                the missile defence bench: N raiding launchers against N/2 missile defence launchers and N/4 radar trucks
          -bench-air int
                air benchmark instead: that many drones in mission modes over 300 ground units, against the ground alone (Bench.cs)
          -bench-heli int
                helicopter benchmark instead: that many helicopters (the three kinds) attacking a ground army of 300 with its air
                defence, on a hilly 256 x 256 map, against the ground alone (Bench.cs)
          -budget float
                bench: wall-clock seconds per workload at most (default 8; move-bench 600)
          -content string
                the content directory (default "content/mvp")
          -label string
                bench: a label stored in the output (default "bench")
          -map string
                move-bench: a map .json made with make_heightmap.py --bench-layout (with its _layout.json), or "flat" (default)
          -maxticks uint
                bench: ticks per workload at most (default 400; move-bench 1200, the pass workload at least 12000)
          -move-bench string
                movement benchmark instead: comma-separated workloads or "all" (idle10k,moving10k,group500,group2000,pass,forest; see MoveBench.cs)
          -out string
                bench: write the JSON results to this file
          -every uint
                print the map every N ticks (0 = final only) (default 1200)
          -replay string
                play this replay file instead of recording bots
          -save string
                write the recorded replay to this file
          -seed uint
                match seed (default 42)
          -setup string
                the lobby file with each player's presets and army (default "content/setups/battle.json")
          -routing-bench string
                routing benchmark instead: comma-separated workloads, group (2,000 units in one group
                order) and many (10,000 units in 40 groups), on a synthetic map (see RoutingBench.cs)
          -spike-bench string
                single-tick spikes instead: comma-separated workloads, move (38 technicals get one Move) and land
                (a shot-down jet's pilot lands), on -map or a synthetic 256 x 256 map (see SpikeBench.cs)
          -prepare bool
                spike bench: build routing's caches before the workload (World.PrepareRouting)
          -fog bool
                spike bench: play with fog of war
          -size int
                routing bench: the map size (default 1500)
          -terrain-bench string
                terrain load benchmark: a map size (a synthetic hilly map, e.g. 1500 or 6000x3000) or a map .json file
          -battle int
                terrain bench: also run a world on the map (bare ticks, the hash, a terrain change), then this many units
                fighting at its far corner while a technical drives corner to corner (0: the bare map only); with -fog,
                -prepare and -maxticks (default 600)
          -ticks uint
                ticks to simulate (default 12000)
          -workloads string
                bench: comma-separated workloads (default all: stationary,movement,combat,projectiles,churn,obstacles)
        """;

    private static int Main(string[] args)
    {
        ulong seed = 42;
        uint ticks = 10 * 60 * World.TicksPerSecond;
        uint every = 60 * World.TicksPerSecond;
        string save = "", load = "", content = "content/mvp", setup = "content/setups/battle.json";
        string terrainBench = "", routingBench = "", spikeBench = "";
        bool prepare = false, fog = false;
        var size = 1500;
        string bench = "", workloads = string.Join(',', Bench.Workloads), outPath = "", label = "bench";
        uint? maxTicks = null;
        double? budget = null;
        string moveBench = "", map = "flat";
        var benchAir = 0;
        var benchHeli = 0;
        var benchSam = 0;
        var battle = -1;
        try
        {
            foreach (var (name, value) in ParseFlags(args))
            {
                switch (name)
                {
                    case "seed": seed = ulong.Parse(value, CultureInfo.InvariantCulture); break;
                    case "ticks": ticks = uint.Parse(value, CultureInfo.InvariantCulture); break;
                    case "every": every = uint.Parse(value, CultureInfo.InvariantCulture); break;
                    case "save": save = value; break;
                    case "replay": load = value; break;
                    case "content": content = value; break;
                    case "setup": setup = value; break;
                    case "bench": bench = value; break;
                    case "bench-air": benchAir = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "bench-heli": benchHeli = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "bench-sam": benchSam = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "workloads": workloads = value; break;
                    case "maxticks": maxTicks = uint.Parse(value, CultureInfo.InvariantCulture); break;
                    case "budget": budget = double.Parse(value, CultureInfo.InvariantCulture); break;
                    case "out": outPath = value; break;
                    case "label": label = value; break;
                    case "terrain-bench": terrainBench = value; break;
                    case "battle": battle = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "routing-bench": routingBench = value; break;
                    case "spike-bench": spikeBench = value; break;
                    case "prepare": prepare = value is "1" or "true"; break;
                    case "fog": fog = value is "1" or "true"; break;
                    case "size": size = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "move-bench": moveBench = value; break;
                    case "map": map = value; break;
                    default: throw new FormatException($"flag provided but not defined: -{name}");
                }
            }
        }
        catch (HelpRequestedException)
        {
            Console.Error.WriteLine(Usage);
            return 0;
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            if (spikeBench != "")
            {
                return SpikeBench.Run(spikeBench, map, prepare, fog, content);
            }
            if (routingBench != "")
            {
                return RoutingBench.Run(routingBench, size, maxTicks ?? 600, budget ?? 300, outPath, content, setup);
            }
            if (terrainBench != "")
            {
                return TerrainBench.Run(terrainBench, outPath,
                    battle < 0 ? null : new TerrainBench.WorldOptions(battle, maxTicks ?? 600, prepare, fog, content, setup));
            }
            if (moveBench != "")
            {
                var wls = moveBench == "all" ? MoveBench.Workloads : moveBench.Split(',');
                return MoveBench.Run(new MoveBench.Options(map, wls, (int)(maxTicks ?? 1200), budget ?? 600, outPath, content, setup));
            }
            if (benchSam > 0)
            {
                return Bench.RunSam(benchSam, maxTicks ?? 900, budget ?? 20, outPath, label, content);
            }
            if (benchHeli > 0)
            {
                return Bench.RunHeli(benchHeli, maxTicks ?? 400, budget ?? 8, outPath, label, content);
            }
            if (benchAir > 0)
            {
                return Bench.RunAir(benchAir, maxTicks ?? 400, budget ?? 8, outPath, label, content);
            }
            if (bench != "")
            {
                var units = bench.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                return Bench.Run(new Bench.Options(units, workloads.Split(','), maxTicks ?? 400, budget ?? 8, outPath, label, content, setup));
            }
            Run(ContentLoader.LoadDirectory(content), setup, seed, ticks, every, save, load);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                      or InvalidDataException or ArgumentException or InvalidOperationException
                                      or ContentException or MatchSetupException)
        {
            Console.Error.WriteLine($"simrun: {e.Message}");
            return 1;
        }
    }

    private sealed class HelpRequestedException : Exception;

    // ParseFlags accepts the same forms as Go's flag package: -name value,
    // -name=value, and the same with two dashes.
    private static IEnumerable<(string Name, string Value)> ParseFlags(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith('-') || a == "-" || a == "--")
            {
                break;
            }
            var name = a.TrimStart('-');
            if (name is "h" or "help")
            {
                throw new HelpRequestedException();
            }
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                yield return (name[..eq], name[(eq + 1)..]);
                continue;
            }
            if (i + 1 >= args.Length)
            {
                throw new FormatException($"flag needs an argument: -{name}");
            }
            yield return (name, args[++i]);
        }
    }

    private static void Run(ContentSet content, string setup, ulong seed, uint ticks, uint every, string save, string load)
    {
        var r = load != ""
            ? Replay.Load(load)
            : Replay.Record(content, Replay.LoadSetup(setup).For(content, seed), ticks).Replay;
        if (save != "")
        {
            r.Save(save);
        }

        // Play the replay tick by tick so we can print along the way.
        var w = r.Start(content);
        var stdout = Console.Out;
        while (w.Tick < r.Ticks)
        {
            w.Step();
            if (every > 0 && w.Tick % every == 0 && w.Tick != r.Ticks)
            {
                stdout.Write(Render(w));
            }
        }
        stdout.Write(Render(w));
        stdout.Flush();
    }

    private static string Render(World w)
    {
        var g = w.Grid;
        var ground = new char[g.Width * g.Height];
        var air = new char[g.Width * g.Height];
        var soldiers = new int[w.Players.Count + 1];
        var units = new int[w.Players.Count + 1];
        foreach (var u in w.Units)
        {
            units[u.Owner]++;
            if ((u.Def.Tags.Bits & w.Player(u.Owner)!.Setup.Faction.DefeatedWithout.Bits) != 0)
            {
                soldiers[u.Owner]++;
            }
            if (u.Inside != 0)
            {
                continue;
            }
            var glyph = u.Owner == 1 ? u.Def.Glyph : char.ToLowerInvariant(u.Def.Glyph);
            var c = Cell.Of(u.Pos);
            var (fw, fh) = u.Def.IsStructure ? (World.FootW(u), World.FootH(u)) : (1, 1);
            var origin = u.Def.IsStructure ? new Cell(c.X - fw / 2, c.Y - fh / 2) : c;
            for (var dy = 0; dy < fh; dy++)
            {
                for (var dx = 0; dx < fw; dx++)
                {
                    var at = new Cell(origin.X + dx, origin.Y + dy);
                    if (!g.Contains(at))
                    {
                        continue;
                    }
                    var layer = u.Def.Layer == Layers.Air ? air : ground;
                    var i = at.Y * g.Width + at.X;
                    if (layer[i] == 0)
                    {
                        layer[i] = glyph;
                    }
                }
            }
        }
        foreach (var p in w.Projectiles)
        {
            var c = Cell.Of(p.Pos);
            if (g.Contains(c))
            {
                air[c.Y * g.Width + c.X] = '*';
            }
        }

        var b = new StringBuilder();
        var secs = w.Tick / World.TicksPerSecond;
        b.Append(CultureInfo.InvariantCulture, $"\ntick {w.Tick} ({secs / 60}:{secs % 60:00})");
        foreach (var p in w.Players)
        {
            b.Append(CultureInfo.InvariantCulture, $"  P{p.Id} {soldiers[p.Id]} soldiers {units[p.Id]} units{(p.Defeated ? " (defeated)" : "")}");
        }
        if (w.Result.Over)
        {
            b.Append(w.Result.Winner == 0 ? "  draw" : $"  P{w.Result.Winner} wins");
        }
        b.Append(CultureInfo.InvariantCulture, $"  hash {w.Hash():x16}\n");
        for (var y = 0; y < g.Height; y++)
        {
            for (var x = 0; x < g.Width; x++)
            {
                var i = y * g.Width + x;
                b.Append(air[i] != 0 ? air[i] : ground[i] != 0 ? ground[i] : g.Passable(new Cell(x, y)) ? '.' : '#');
            }
            b.Append('\n');
        }
        return b.ToString();
    }
}
