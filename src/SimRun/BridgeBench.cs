// BridgeBench measures ground units crossing a map's bridges: per bridge and direction, each kind alone, columns of a kind,
// a mixed group and two columns meeting head-on, from 25 cells before the span to 25 beyond it.
//
//   dotnet run -c Release --project src/SimRun -- -bridge-bench DIR [-out FILE]
//
// DIR holds a map.json with bridges (the game's client/data/maps/zuberec_02). Per scenario, for its units: how many got past
// the far ramp in time; the time over what the distance takes at top speed (roads make it less than 1); the ticks moving
// slower than a fifth of top speed and those marked stuck, and where the worst of them happened (approach, deck, beyond).
using System.Globalization;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static class BridgeBench
{
    private static readonly string[] Solo =
    [
        "Rifleman", "humvee", "technical", "sentinel_cargo_truck", "main_battle_tank", "merkava_tank", "heavy_rocket_truck",
        "kub_launcher", "ballistic_truck", "jericho_truck",
    ];
    private static readonly (string Name, string[] Kinds)[] Groups =
    [
        ("8 tanks", Repeat("main_battle_tank", 8)),
        ("8 humvees", Repeat("humvee", 8)),
        ("6 rocket trucks", Repeat("heavy_rocket_truck", 6)),
        ("12 riflemen", Repeat("Rifleman", 12)),
        ("mixed 7", ["humvee", "sentinel_cargo_truck", "main_battle_tank", "Rifleman", "Rifleman", "Rifleman", "Rifleman"]),
    ];

    private static string[] Repeat(string k, int n) => [.. Enumerable.Repeat(k, n)];

    private sealed class Run1
    {
        public string Name = "";
        public int Bridge, Units, Crossed, Stalled, Stuck;
        public double Ratio, Worst;
        public readonly int[] Zone = new int[3];
    }

    public static int Run(string mapDir, string contentDir, string setupPath, string outPath)
    {
        var content = ContentLoader.LoadDirectory(contentDir);
        var setup = Replay.LoadSetup(setupPath).For(content, 42);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var match = MatchCompiler.Compile(content, setup);
        var rules = TerrainLoader.LoadRules(Path.Combine(contentDir, "..", "terrain", "rules.json"));
        var map = TerrainLoader.LoadMap(Path.Combine(mapDir, "map.json"), rules, content);
        var w = new World(new Grid(map.Width, map.Height), match);
        w.AttachTerrain(map);
        w.PrepareRouting(everySector: false);
        var runs = new List<Run1>();
        var parked = 0;
        for (var b = 0; b < map.BridgeSites.Count; b++)
        {
            var site = map.BridgeSites[b];
            var (bw, bh) = site.Kind switch { "stone" => (3, 8), "concrete" => (5, 12), _ => (9, 18) };
            var ns = site.Facing == Facing.South || site.Facing == Facing.North;
            var c = new Cell(site.Cell.X + (ns ? bw : bh) / 2, site.Cell.Y + (ns ? bh : bw) / 2);
            if (Environment.GetEnvironmentVariable("BRIDGE_DUMP") is { Length: > 0 })
            {
                Dump(w, map, content, b, site.Kind, c, ns, bw, bh);
                continue;
            }
            for (var dir = -1; dir <= 1; dir += 2)
            {
                foreach (var kind in Solo)
                {
                    runs.Add(Trial(w, content, $"{kind}", b, c, ns, dir, bh, [kind], null, ref parked));
                }
                foreach (var (name, kinds) in Groups)
                {
                    runs.Add(Trial(w, content, name, b, c, ns, dir, bh, kinds, null, ref parked));
                }
            }
            runs.Add(Trial(w, content, "head-on 4+4 tanks", b, c, ns, 1, bh, Repeat("main_battle_tank", 4), Repeat("main_battle_tank", 4), ref parked));
            runs.Add(Trial(w, content, "head-on 4+4 humvees", b, c, ns, 1, bh, Repeat("humvee", 4), Repeat("humvee", 4), ref parked));
        }
        using var o = outPath == "" ? null : new StreamWriter(outPath);
        o?.WriteLine("bridge,kind,scenario,units,crossed,ratio,worst,stalled,stuck,approach,deck,beyond");
        foreach (var r in runs)
        {
            o?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Bridge},{map.BridgeSites[r.Bridge].Kind},{r.Name},{r.Units},{r.Crossed},{r.Ratio:0.00},{r.Worst:0.00},{r.Stalled},{r.Stuck},{r.Zone[0]},{r.Zone[1]},{r.Zone[2]}"));
        }
        // One line per scenario and bridge kind, the worst first.
        var rows = runs.GroupBy(r => (map.BridgeSites[r.Bridge].Kind, r.Name)).Select(g => (g.Key.Kind, g.Key.Name,
            Lost: g.Sum(r => r.Units - r.Crossed), All: g.Sum(r => r.Units), Ratio: g.Average(r => r.Ratio), Worst: g.Max(r => r.Worst),
            Stalled: g.Sum(r => r.Stalled) / Math.Max(g.Sum(r => r.Units), 1), Stuck: g.Sum(r => r.Stuck) / Math.Max(g.Sum(r => r.Units), 1),
            A: g.Sum(r => r.Zone[0]), D: g.Sum(r => r.Zone[1]), B: g.Sum(r => r.Zone[2])));
        foreach (var r in rows.OrderByDescending(r => r.Lost).ThenByDescending(r => r.Worst).Take(40))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Kind,-8} {r.Name,-22} missed {r.Lost,3}/{r.All,-3} time x{r.Ratio:0.00} worst x{r.Worst:0.00} stalled {r.Stalled,4} stuck {r.Stuck,4} zones a{r.A} d{r.D} b{r.B}"));
        }
        return 0;
    }

    private static Run1 Trial(World w, ContentSet content, string name, int bridge, Cell c, bool ns, int dir, int span,
        string[] kinds, string[]? against, ref int parked)
    {
        var half = span / 2 + 25;
        var units = new List<(uint Id, int Sign)>();
        var drivers = new List<uint>();
        Place(w, content, kinds, c, ns, dir, half, units, drivers);
        if (against is not null)
        {
            Place(w, content, against, c, ns, -dir, half, units, drivers);
        }
        for (var i = 0; i < 200 && drivers.Any(x => w.Unit(x) is { Inside: 0 }); i++)
        {
            w.Step();
        }
        var order = w.Tick + 1;
        foreach (var sign in new[] { dir, -dir })
        {
            var mine = units.Where(u => u.Sign == sign).Select(u => u.Id).ToArray();
            if (mine.Length > 0)
            {
                var player = w.Unit(mine[0])!.Owner;
                w.Issue(new Command { Tick = order, Player = player, Kind = CommandKind.Move, Units = mine, Cell = At(c, ns, sign * half) });
            }
        }
        var slowest = units.Min(u => w.Unit(u.Id)!.Def.Speed.Raw) / (double)Fixed.OneRaw;
        var limit = order + (uint)(2.0 * half * 4 / slowest) + 600;
        var done = new uint[units.Count];
        var r = new Run1 { Name = name, Bridge = bridge, Units = units.Count };
        while (w.Tick < limit && done.Any(t => t == 0))
        {
            w.Step();
            for (var i = 0; i < units.Count; i++)
            {
                if (done[i] != 0)
                {
                    continue;
                }
                var u = w.Unit(units[i].Id);
                if (u is null)
                {
                    Console.WriteLine($"LOST {name} bridge {bridge} dir {dir} unit {units[i].Id} at tick +{w.Tick - order}");
                    done[i] = uint.MaxValue;
                    continue;
                }
                var along = ((ns ? u.Pos.Y.Raw : u.Pos.X.Raw) / (double)Fixed.OneRaw - (ns ? c.Y : c.X)) * units[i].Sign;
                if (along > span / 2 + 6)
                {
                    done[i] = w.Tick - order;
                    continue;
                }
                var speed = Math.Sqrt(Sq(u.Vel.X.Raw) + Sq(u.Vel.Y.Raw)) / Fixed.OneRaw;
                var top = u.Def.Speed.Raw / (double)Fixed.OneRaw;
                if (speed < top / 5 && w.Tick > order + 40)
                {
                    r.Stalled++;
                    r.Zone[along < -span / 2 ? 0 : along <= span / 2 ? 1 : 2]++;
                }
                if (u.Intent.Stuck)
                {
                    r.Stuck++;
                }
            }
        }
        for (var i = 0; i < units.Count; i++)
        {
            if (done[i] == uint.MaxValue)
            {
                continue;
            }
            var u = w.Unit(units[i].Id)!;
            var ideal = (span + 12 + 25.0) / (u.Def.Speed.Raw / (double)Fixed.OneRaw);
            var ratio = (done[i] == 0 ? limit - order : done[i]) / ideal;
            r.Crossed += done[i] != 0 ? 1 : 0;
            r.Ratio += ratio / units.Count;
            r.Worst = Math.Max(r.Worst, ratio);
            u.Pos = new Cell(2 + parked % 200 * 6, w.Grid.Height - 3 - parked / 200 * 6).Center();
            u.ClearPath();
            u.Order = new Order(OrderKind.Idle, default, 0, 0);
            parked++;
        }
        return r;
    }

    private static void Place(World w, ContentSet content, string[] kinds, Cell c, bool ns, int dir, int half, List<(uint, int)> units, List<uint> drivers)
    {
        Facing.TryFromDirection(At(c, ns, dir).Center() - c.Center(), out var face);
        for (var i = 0; i < kinds.Length; i++)
        {
            var player = w.Match.Players.First(q => q.Find(content, kinds[i]) is not null); // one side, or they would fight
            var def = player.Find(content, kinds[i])!;
            var gap = def.Archetype.Locomotor == Locomotor.Foot ? 2 : 5;
            var row = i / 2 * gap;
            var side = (i % 2) * gap - gap / 2;
            var near = At(c, ns, -dir * (half + row));
            var at = Near(w, ns ? new Cell(near.X + (kinds.Length > 1 ? side : 0), near.Y) : new Cell(near.X, near.Y + (kinds.Length > 1 ? side : 0)), def);
            var id = w.Spawn(def, at, face);
            if (def.Archetype.Crew is not null)
            {
                var drv = w.Spawn(player.Find(content, "Rifleman")!, at, face);
                w.Issue(new Command { Tick = w.Tick + 1, Player = player.Id, Kind = CommandKind.Enter, Units = [drv], Target = id });
                drivers.Add(drv);
            }
            units.Add((id, dir));
        }
    }

    // One row per lateral offset is too much to read: the centre line and the deck's edges, from 8 cells before the footprint to 8 beyond.
    private static void Dump(World w, TerrainMap map, ContentSet content, int b, string kind, Cell c, bool ns, int bw, int span)
    {
        var p1 = w.Match.Players[0];
        Console.WriteLine($"bridge {b} {kind} at {c} {(ns ? "ns" : "ew")} footprint {bw}x{span}");
        var half = span / 2 + 8;
        string Row(Func<Cell, string> f, int side) => string.Join(" ", Enumerable.Range(-half, half * 2 + 1).Select(d =>
        {
            var at = At(c, ns, d);
            return f(ns ? new Cell(at.X + side, at.Y) : new Cell(at.X, at.Y + side));
        }));
        Console.WriteLine("  along  " + string.Join(" ", Enumerable.Range(-half, half * 2 + 1).Select(d => $"{d,3}")));
        foreach (var side in new[] { 0, -bw / 2, bw / 2 })
        {
            Console.WriteLine($"  grade{side,2} " + Row(x => $"{map.Grade(x),3}", side));
        }
        Console.WriteLine("  metres " + Row(x => $"{map.GroundHeightAt(x.Center()).Raw / (double)Fixed.OneRaw / 10:0.0}".PadLeft(3)[..3], 0));
        Console.WriteLine("  road   " + Row(x => $"{(int)map.RoadKindAt(x),3}", 0));
        foreach (var name in new[] { "sentinel_cargo_truck", "heavy_rocket_truck", "main_battle_tank", "technical" })
        {
            var def = (p1.Find(content, name) ?? w.Match.Players[^1].Find(content, name))!;
            var k = w.Routing.ClassOf(def);
            var tc = map.Rules.Classes[w.Routing.Nav.TerrainClass(k)].Id;
            Console.WriteLine($"  {name} {tc} width {w.Routing.Nav.Width(k)}");
            Console.WriteLine("  cost   " + Row(x => $"{w.Routing.Nav.Cost(k, x.X, x.Y),3}", 0));
        }
    }

    private static Cell At(Cell c, bool ns, int d) => ns ? new Cell(c.X, c.Y + d) : new Cell(c.X + d, c.Y);

    private static Cell Near(World w, Cell c, UnitDef def)
    {
        var r = w.Routing;
        var k = r.ClassOf(def);
        var i = k < 0 ? -1 : r.NearestPassable(k, c, 16);
        return i < 0 ? c : r.CellAt(i);
    }

    private static double Sq(double v) => v * v;
}
