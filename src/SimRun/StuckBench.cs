using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Rng;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimRun;

// Movement QA, not a strength test: hold fire keeps both armies alive for the full ten minutes.
// Small random groups cross opposing crowds; stationary empty/deployed bodies stay as obstacles.
internal static class StuckBench
{
    private const int Window = 600;
    private static readonly string[] Causes = ["enemyStill", "emptyVehicle", "deployedLauncher", "friendlyCrowd", "terrainOther"];

    private sealed class Track
    {
        public int Epoch = -1, Age, Head, Count;
        public Order Order;
        public bool Counted;
        public readonly long[] Distance = new long[Window];
        public readonly (int Age, long Distance)[] Min = new (int, long)[Window + 1];

        public long? Sample(Unit u, int epoch)
        {
            if (Epoch != epoch || Order != u.Order)
            {
                Epoch = epoch; Order = u.Order; Age = Head = Count = 0; Counted = false;
            }
            var distance = (u.Order.Cell.Center() - u.Pos).Len().Raw;
            var old = Distance[Age % Distance.Length];
            Distance[Age % Distance.Length] = distance;
            while (Count > 0 && Min[Head].Age < Age - Window) { Head = (Head + 1) % Min.Length; Count--; }
            while (Count > 0 && Min[(Head + Count - 1) % Min.Length].Distance >= distance) { Count--; }
            Min[(Head + Count++) % Min.Length] = (Age, distance);
            var improvement = Age >= Window ? old - Min[Head].Distance : long.MaxValue;
            Age++;
            if (Counted || improvement >= Fixed.OneRaw / 2) { return null; }
            Counted = true;
            return improvement;
        }
    }

    public static int Run(string seeds, string map, uint ticks, string output, string contentDir, string setupPath)
    {
        if (ticks == 0) { throw new ArgumentException("stuck-bench: ticks must be positive"); }
        var content = ContentLoader.LoadDirectory(contentDir);
        var results = new JsonArray();
        foreach (var seed in seeds.Split(',').Select(x => ulong.Parse(x, CultureInfo.InvariantCulture)))
        {
            var setup = Replay.LoadSetup(setupPath).For(content, seed);
            var match = MatchCompiler.Compile(content, setup);
            var w = Scenario.Create(match);
            if (map == "flat")
            {
                for (var y = 0; y < w.Grid.Height; y++)
                for (var x = 0; x < w.Grid.Width; x++) { w.Grid.Set(new Cell(x, y), Terrain.Clear); }
            }
            else if (map != "battle") { throw new ArgumentException("stuck-bench: -map wants battle or flat"); }
            var r = new Rand(seed);
            foreach (var p in match.Players)
            {
                var rifle = p.Find(content, "Rifleman")!;
                var tech = p.Find(content, "technical")!;
                for (var i = 0; i < 90; i++)
                {
                    var c = new Cell(p.Id == 1 ? r.Range(10, 35) : r.Range(60, 85), r.Range(15, 80));
                    if (!w.Grid.Passable(c)) { continue; }
                    var def = i % 6 == 0 ? tech : rifle;
                    var u = w.Unit(w.Spawn(def, c, Facing.East))!;
                    if (def == tech) { World.Board(w.Unit(w.Spawn(rifle, c, Facing.East))!, u); }
                }
                for (var i = 0; i < 6; i++)
                {
                    var c = new Cell(p.Id == 1 ? 40 : 55, 20 + i * 10);
                    var v = w.Unit(w.Spawn(p.Find(content, i % 2 == 0 ? "cargo_truck" : "heavy_rocket_launcher")!, c, Facing.East))!;
                    if (i % 2 != 0)
                    {
                        World.Board(w.Unit(w.Spawn(rifle, c, Facing.East))!, v);
                        v.Deploy = DeployState.Deployed;
                    }
                }
            }
            foreach (var u in w.Units) { u.Stance = Stance.HoldFire; }
            w.PrepareRouting();
            var tracks = new Dictionary<uint, Track>();
            var epochs = new Dictionary<uint, int>();
            var counts = Causes.ToDictionary(c => c, _ => 0);
            var worst = new List<(long Improvement, uint Id, JsonObject Data)>();
            var orders = 0;
            uint nextOrder = 0;
            var times = new List<double>();
            var sw = new Stopwatch();
            while (w.Tick < ticks)
            {
                if (w.Tick >= nextOrder)
                {
                    foreach (var p in match.Players)
                    {
                        var available = w.Units.Where(u => u.Owner == p.Id && u.Alive && u.CanAct && !u.Def.IsStructure &&
                            u.Def.Layer == Layers.Ground && u.Def.Speed.Raw > 0 && u.Deploy == DeployState.Stowed).ToList();
                        var selected = new List<uint>();
                        var count = 3 + r.Intn(6);
                        for (var i = 0; i < count && available.Count > 0; i++)
                        {
                            var at = r.Intn(available.Count);
                            var u = available[at]; available.RemoveAt(at); selected.Add(u.Id);
                            epochs[u.Id] = epochs.GetValueOrDefault(u.Id) + 1;
                            orders++;
                        }
                        // Most targets cross the enemy's crowd; some orders circulate around home.
                        var enemyHalf = r.Intn(4) != 0;
                        var right = (p.Id == 1) == enemyHalf;
                        var to = new Cell(right ? r.Range(60, 85) : r.Range(10, 35), r.Range(15, 80));
                        w.Issue(new Command { Tick = w.Tick, Player = p.Id, Kind = r.Intn(2) == 0 ? CommandKind.Move : CommandKind.AttackMove,
                            Units = [.. selected], Cell = to });
                    }
                    nextOrder = w.Tick + (uint)r.Range(100, 300);
                }
                sw.Restart(); w.Step(); times.Add(sw.Elapsed.TotalMilliseconds);
                foreach (var u in w.Units)
                {
                    if (!u.Alive || u.Def.Layer != Layers.Ground || !u.CanAct || u.Order.Kind is not (OrderKind.Move or OrderKind.AttackMove))
                    {
                        tracks.Remove(u.Id); continue;
                    }
                    if (!tracks.TryGetValue(u.Id, out var track)) { tracks[u.Id] = track = new Track(); }
                    if (track.Sample(u, epochs.GetValueOrDefault(u.Id)) is not { } improvement) { continue; }
                    var (cause, blocker) = Cause(w, u);
                    counts[cause]++;
                    worst.Add((improvement, u.Id, new JsonObject { ["unit"] = u.Id, ["tick"] = w.Tick, ["cause"] = cause,
                        ["blocker"] = blocker, ["xRaw"] = u.Pos.X.Raw, ["yRaw"] = u.Pos.Y.Raw,
                        ["goalX"] = u.Order.Cell.X, ["goalY"] = u.Order.Cell.Y, ["improvementRaw"] = improvement }));
                }
            }
            var byCause = new JsonObject();
            foreach (var cause in Causes) { byCause[cause] = counts[cause]; }
            var ten = new JsonArray();
            foreach (var row in worst.OrderBy(x => x.Improvement).ThenBy(x => x.Id).Take(10)) { ten.Add(row.Data); }
            times.Sort();
            var result = new JsonObject { ["seed"] = seed, ["ticks"] = ticks, ["stuckUnits"] = worst.Count, ["totalMoveOrders"] = orders,
                ["byCause"] = byCause, ["worst10"] = ten, ["tickP50Ms"] = times[times.Count / 2],
                ["tickP99Ms"] = times[(times.Count - 1) * 99 / 100], ["finalHash"] = w.Hash().ToString("x16", CultureInfo.InvariantCulture) };
            results.Add(result);
            Console.WriteLine($"seed {seed}: stuck={worst.Count}, unit orders={orders}; {byCause.ToJsonString()}");
            Console.WriteLine(ten.ToJsonString());
        }
        if (output != "")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, new JsonObject { ["map"] = map, ["windowTicks"] = Window, ["minImprovementRaw"] = Fixed.OneRaw / 2,
                ["holdFire"] = true, ["machine"] = $"{Environment.ProcessorCount} cpus, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}",
                ["results"] = results }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        return 0;
    }

    private static (string Cause, uint Blocker) Cause(World w, Unit u)
    {
        var dir = u.Moving ? u.Path[u.PathIdx].Center() - u.Pos : u.Order.Cell.Center() - u.Pos;
        var len = dir.Len().Raw;
        Unit? best = null;
        long nearest = long.MaxValue;
        foreach (var v in w.Units)
        {
            if (v == u || !v.Alive || v.Inside != 0 || v.Def.IsStructure || v.Def.Layer != Layers.Ground) { continue; }
            var d = v.Pos - u.Pos;
            var along = len == 0 ? 0 : (d.X.Raw * dir.X.Raw + d.Y.Raw * dir.Y.Raw) / len;
            var across = len == 0 ? 0 : Math.Abs(d.X.Raw * dir.Y.Raw - d.Y.Raw * dir.X.Raw) / len;
            if (along <= 0 || along > 6 * Fixed.OneRaw || across > u.Def.Archetype.Radius.Raw + v.Def.Archetype.Radius.Raw + Fixed.OneRaw) { continue; }
            var distance = d.LenSq();
            if (distance < nearest) { nearest = distance; best = v; }
        }
        return best is null ? ("terrainOther", 0) :
            (best.Def.Crew is not null && best.Driver == 0 ? "emptyVehicle" : best.Deploy == DeployState.Deployed ? "deployedLauncher" :
             best.Owner != u.Owner && best.Vel == default ? "enemyStill" : best.Owner == u.Owner ? "friendlyCrowd" : "terrainOther", best.Id);
    }
}
