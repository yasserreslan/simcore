// BridgeBench measures ground units crossing a map's bridges: per bridge and direction, each kind alone, columns of a kind,
// a mixed group and two columns meeting head-on, from 25 cells before the span to 25 beyond it.
//
//   dotnet run -c Release --project src/SimRun -- -bridge-bench DIR [-out FILE]
//
// DIR holds a map.json with bridges (the game's client/data/maps/zuberec_02). Per scenario, for its units: how many got past
// the far ramp in time; the time over what the distance takes at top speed (roads make it less than 1); the ticks until the
// last one was past; the ticks moving slower than a fifth of top speed and those marked stuck, and where the worst of them
// happened (approach, deck, beyond); the units that turned round on the footprint, went back off it the way they came, or
// stood in open water. BRIDGE_ONLY=traffic runs just the columns (one way and head-on, 4 and 8 a side, of each traffic mix, and
// a long column against a short one: "last b" is when the short one was over); BRIDGE_ONLY=blocked, 4 tanks one way and head-on
// past something on the deck: a tank standing idle, an empty truck, an enemy tank. BRIDGE_TRACE=name prints where the units of
// the runs named so are, every 40 ticks. "Aslant": the columns start 16 cells off the road's line, on opposite sides; "short hop":
// their goal is 14 cells past the span, where they stop near the other column's queue. BRIDGE_NO_TRAFFIC=1 turns bridge traffic off (decisions 937-945): the "before" of the same runs.
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

    private static readonly (string Name, string[] Mix)[] Traffic =
    [
        ("tanks", ["main_battle_tank"]),
        ("trucks", ["sentinel_cargo_truck"]),
        ("riflemen", ["Rifleman"]),
        ("mixed", ["humvee", "sentinel_cargo_truck", "main_battle_tank", "Rifleman"]),
    ];

    private static string[] Repeat(string k, int n) => [.. Enumerable.Repeat(k, n)];

    private static string[] Column(string[] mix, int n) => [.. Enumerable.Range(0, n).Select(i => mix[i % mix.Length])];

    private sealed class Run1
    {
        public string Name = "";
        public int Bridge, Units, Crossed, Stalled, Stuck, Last, LastB, Turned, Backed, Wet;
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
        w.NoBridgeTraffic = Environment.GetEnvironmentVariable("BRIDGE_NO_TRAFFIC") is { Length: > 0 }; // the same runs as the engine played them before it
        w.AttachTerrain(map);
        w.PrepareRouting(everySector: false);
        var runs = new List<Run1>();
        var entries = new List<Entry1>();
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
            var only = Environment.GetEnvironmentVariable("BRIDGE_ONLY");
            if (only == "entry" && Environment.GetEnvironmentVariable("BRIDGE_PICK") is { Length: > 0 } pick && !pick.Split(',').Contains(b.ToString(CultureInfo.InvariantCulture)))
            {
                continue; // BRIDGE_PICK=n,m: those bridges only
            }
            if (only == "entry")
            {
                foreach (var kind in EntryKinds)
                {
                    for (var dir = -1; dir <= 1; dir += 2)
                    {
                        foreach (var aside in new[] { 0, 14, -14 })
                        {
                            entries.Add(EntryTrial(w, content, b, c, ns, dir, bh, kind, aside, ref parked));
                        }
                    }
                }
                continue;
            }
            if (only == "blocked")
            {
                foreach (var what in new[] { "idle tank", "empty truck", "enemy tank" })
                {
                    runs.Add(Trial(w, content, $"{what}: one way", b, c, ns, 1, bh, Repeat("main_battle_tank", 4), null, ref parked, what));
                    runs.Add(Trial(w, content, $"{what}: head-on", b, c, ns, 1, bh, Repeat("main_battle_tank", 4), Repeat("main_battle_tank", 4), ref parked, what));
                }
                continue;
            }
            runs.Add(Trial(w, content, "stream 24+4 tanks", b, c, ns, 1, bh, Repeat("main_battle_tank", 24), Repeat("main_battle_tank", 4), ref parked));
            runs.Add(Trial(w, content, "aslant 4+4 tanks", b, c, ns, 1, bh, Repeat("main_battle_tank", 4), Repeat("main_battle_tank", 4), ref parked, aside: 16));
            runs.Add(Trial(w, content, "aslant 8+8 mixed", b, c, ns, 1, bh, Column(Traffic[3].Mix, 8), Column(Traffic[3].Mix, 8), ref parked, aside: 16));
            runs.Add(Trial(w, content, "short hop 4+4 tanks", b, c, ns, 1, bh, Repeat("main_battle_tank", 4), Repeat("main_battle_tank", 4), ref parked, hop: 14));
            runs.Add(Trial(w, content, "short hop 8+8 mixed", b, c, ns, 1, bh, Column(Traffic[3].Mix, 8), Column(Traffic[3].Mix, 8), ref parked, hop: 14));
            foreach (var (name, mix) in Traffic)
            {
                foreach (var n in new[] { 4, 8 })
                {
                    runs.Add(Trial(w, content, $"one way {n} {name}", b, c, ns, 1, bh, Column(mix, n), null, ref parked));
                    runs.Add(Trial(w, content, $"one way {n} {name}", b, c, ns, -1, bh, Column(mix, n), null, ref parked));
                    runs.Add(Trial(w, content, $"head-on {n}+{n} {name}", b, c, ns, 1, bh, Column(mix, n), Column(mix, n), ref parked));
                }
            }
            if (only == "traffic")
            {
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
            runs.Add(Trial(w, content, "head-on 4+4 humvees", b, c, ns, 1, bh, Repeat("humvee", 4), Repeat("humvee", 4), ref parked));
        }
        if (entries.Count > 0)
        {
            return ReportEntries(map, entries, outPath);
        }
        using var o = outPath == "" ? null : new StreamWriter(outPath);
        o?.WriteLine("bridge,kind,scenario,units,crossed,ratio,worst,last,lastb,stalled,stuck,approach,deck,beyond,turned,backed,wet");
        foreach (var r in runs)
        {
            o?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Bridge},{map.BridgeSites[r.Bridge].Kind},{r.Name},{r.Units},{r.Crossed},{r.Ratio:0.00},{r.Worst:0.00},{r.Last},{r.LastB},{r.Stalled},{r.Stuck},{r.Zone[0]},{r.Zone[1]},{r.Zone[2]},{r.Turned},{r.Backed},{r.Wet}"));
        }
        // One line per scenario and bridge kind, the worst first.
        var rows = runs.GroupBy(r => (map.BridgeSites[r.Bridge].Kind, r.Name)).Select(g => (g.Key.Kind, g.Key.Name,
            Lost: g.Sum(r => r.Units - r.Crossed), All: g.Sum(r => r.Units), Ratio: g.Average(r => r.Ratio), Worst: g.Max(r => r.Worst),
            Stalled: g.Sum(r => r.Stalled) / Math.Max(g.Sum(r => r.Units), 1), Stuck: g.Sum(r => r.Stuck) / Math.Max(g.Sum(r => r.Units), 1),
            A: g.Sum(r => r.Zone[0]), D: g.Sum(r => r.Zone[1]), B: g.Sum(r => r.Zone[2]),
            Last: g.Average(r => r.Last), LastB: g.Average(r => r.LastB), Turned: g.Sum(r => r.Turned), Backed: g.Sum(r => r.Backed), Wet: g.Sum(r => r.Wet)));
        foreach (var r in rows.OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Kind,-8} {r.Name,-22} missed {r.Lost,3}/{r.All,-3} time x{r.Ratio:0.00} worst x{r.Worst:0.00} last {r.Last,5:0} b {r.LastB,5:0} stalled {r.Stalled,4} stuck {r.Stuck,4} zones a{r.A} d{r.D} b{r.B} turned {r.Turned} backed {r.Backed} wet {r.Wet}"));
        }
        return 0;
    }

    private static Run1 Trial(World w, ContentSet content, string name, int bridge, Cell c, bool ns, int dir, int span,
        string[] kinds, string[]? against, ref int parked, string blocker = "", int aside = 0, int hop = 0)
    {
        var half = span / 2 + 25;
        var extra = Blocker(w, content, blocker, c, ns);
        var units = new List<(uint Id, int Sign)>();
        var drivers = new List<uint>();
        Place(w, content, kinds, c, ns, dir, half, units, drivers, aside);
        if (against is not null)
        {
            Place(w, content, against, c, ns, -dir, half, units, drivers, -aside);
        }
        for (var i = 0; i < 600 && drivers.Any(x => w.Unit(x) is { Inside: 0 }); i++) // (a driver walks round a Merkava's hull to its rear door: over 200 ticks now and then)
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
                w.Issue(new Command { Tick = order, Player = player, Kind = CommandKind.Move, Units = mine, Cell = At(c, ns, sign * (hop > 0 ? span / 2 + hop : half)) });
            }
        }
        var slowest = units.Min(u => w.Unit(u.Id)!.Def.Speed.Raw) / (double)Fixed.OneRaw;
        var limit = order + (uint)(2.0 * half * 4 / slowest) + 600;
        var done = new uint[units.Count];
        var r = new Run1 { Name = name, Bridge = bridge, Units = units.Count };
        var turned = new bool[units.Count];
        var onSpan = new bool[units.Count];
        var backed = new bool[units.Count];
        var wet = new bool[units.Count];
        var trace = Environment.GetEnvironmentVariable("BRIDGE_TRACE") is { Length: > 0 } tn && name.Contains(tn, StringComparison.Ordinal);
        while (w.Tick < limit && done.Any(t => t == 0))
        {
            w.Step();
            if (trace && (w.Tick - order) % 40 == 0)
            {
                Trace(w, name, bridge, order, c, ns, units);
            }
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
                    r.Last = (int)Math.Max(r.Last, done[i]);
                    r.LastB = units[i].Sign == dir ? r.LastB : (int)Math.Max(r.LastB, done[i]);
                    continue;
                }
                var dot = (ns ? u.Heading.Y.Raw : u.Heading.X.Raw) * units[i].Sign;
                if (Math.Abs(along) <= span / 2)
                {
                    onSpan[i] = true;
                    turned[i] |= u.Def.Archetype.Locomotor != Locomotor.Foot && dot < 0;
                }
                else if (onSpan[i] && along < -span / 2 - 2)
                {
                    backed[i] = true;
                }
                wet[i] |= w.Ground!.OpenWater(Cell.Of(u.Pos));
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
            r.Last = done[i] == 0 ? (int)(limit - order) : r.Last;
            r.LastB = done[i] == 0 && units[i].Sign != dir ? (int)(limit - order) : r.LastB;
            r.Turned += turned[i] ? 1 : 0;
            r.Backed += backed[i] ? 1 : 0;
            r.Wet += wet[i] ? 1 : 0;
            r.Ratio += ratio / units.Count;
            r.Worst = Math.Max(r.Worst, ratio);
            u.Pos = new Cell(2 + parked % 200 * 6, w.Grid.Height - 3 - parked / 200 * 6).Center();
            u.ClearPath();
            u.Order = new Order(OrderKind.Idle, default, 0, 0);
            parked++;
        }
        foreach (var id in extra)
        {
            if (w.Unit(id) is { } u)
            {
                u.Pos = new Cell(2 + parked % 200 * 6, w.Grid.Height - 3 - parked / 200 * 6).Center();
                u.ClearPath();
                parked++;
            }
        }
        return r;
    }

    // BRIDGE_ONLY=entry: one unit of each kind over each bridge, both ways, straight on along the road's line and from 14 cells to
    // either side of it (a road turning onto the bridge); where it gets onto the footprint (the ramps) and onto the deck: off the
    // centre line (cells), the angle of its heading to the axis, over a long side of the footprint or its end, and whether it
    // stood in a cell beside the deck's ends (off the span's axis: up to 2 cells aside of the deck, along its length and a cell
    // beyond each end; the cells decision 915 opened). A unit that never reached the footprint had no way there (its start or goal).
    private static readonly string[] EntryKinds = ["sentinel_cargo_truck", "heavy_rocket_truck", "ballistic_truck", "main_battle_tank", "humvee"];

    private sealed class Entry1
    {
        public string Kind = "";
        public int Bridge, Dir, Aside, SideIn, SideOut, Beside;
        public bool Crossed;
        public double FootOff, FootAngle, DeckOff = double.NaN, DeckAngle = double.NaN, Ratio, SideLift;
    }

    private static Entry1 EntryTrial(World w, ContentSet content, int bridge, Cell c, bool ns, int dir, int span, string kind, int aside, ref int parked)
    {
        var half = span / 2 + (aside == 0 ? 25 : 18);
        var units = new List<(uint Id, int Sign)>();
        var drivers = new List<uint>();
        Place(w, content, [kind], c, ns, dir, half, units, drivers, aside);
        for (var i = 0; i < 600 && drivers.Any(x => w.Unit(x) is { Inside: 0 }); i++)
        {
            w.Step();
        }
        var id = units[0].Id;
        var u = w.Unit(id)!;
        var b = w.BridgeAt(c)!;
        var (fo, fw, fh) = (World.FootprintOrigin(b), World.FootW(b), World.FootH(b));
        double f0 = ns ? fo.Y : fo.X, f1 = f0 + (ns ? fh : fw), g0 = ns ? fo.X : fo.Y, g1 = g0 + (ns ? fw : fh);
        int a0 = int.MaxValue, a1 = int.MinValue, c0 = int.MaxValue, c1 = int.MinValue;
        foreach (var d in b.Bridge!.DeckCells)
        {
            var (al, ac) = ns ? (d.Y, d.X) : (d.X, d.Y);
            (a0, a1, c0, c1) = (Math.Min(a0, al), Math.Max(a1, al), Math.Min(c0, ac), Math.Max(c1, ac));
        }
        var mid = (g0 + g1) / 2;
        var order = w.Tick + 1;
        w.Issue(new Command { Tick = order, Player = u.Owner, Kind = CommandKind.Move, Units = [id], Cell = At(c, ns, dir * (span / 2 + 25)) });
        var limit = order + (uint)(2.0 * (span / 2 + 25) * 4 / (u.Def.Speed.Raw / (double)Fixed.OneRaw)) + 600;
        var e = new Entry1 { Kind = kind, Bridge = bridge, Dir = dir, Aside = aside, FootOff = double.NaN, FootAngle = double.NaN };
        bool inFoot = false, wasAlong = false, onDeck = false;
        uint done = 0;
        while (w.Tick < limit && done == 0)
        {
            w.Step();
            double x = u.Pos.X.Raw / (double)Fixed.OneRaw, y = u.Pos.Y.Raw / (double)Fixed.OneRaw;
            double al = ns ? y : x, ac = ns ? x : y;
            var alongIn = al >= f0 && al < f1;
            var acrossIn = ac >= g0 && ac < g1;
            var angle = Math.Atan2(Math.Abs(ns ? u.Heading.X.Raw : u.Heading.Y.Raw), (ns ? u.Heading.Y.Raw : u.Heading.X.Raw) * dir) * 180 / Math.PI;
            if (alongIn && acrossIn && !inFoot)
            {
                e.SideIn += wasAlong ? 1 : 0; // it was beside the footprint, within its length: in over a long side
                // How high the client draws the surface where it came in over a side, of the deck's height: its default profile, the
                // flat deck over the middle 60% of the footprint, the ramps down to the bank over 20% at each end (BridgeView3D).
                e.SideLift = wasAlong ? Math.Max(e.SideLift, Math.Clamp((Math.Min(al - f0, f1 - al)) / (0.2 * (f1 - f0)), 0, 1)) : e.SideLift;
                if (double.IsNaN(e.FootOff))
                {
                    (e.FootOff, e.FootAngle) = (ac - mid, angle);
                }
            }
            else if (inFoot && alongIn && !acrossIn)
            {
                e.SideOut++;
            }
            inFoot = alongIn && acrossIn;
            wasAlong = alongIn && !acrossIn;
            var cell = Cell.Of(u.Pos);
            var (ca, cc) = ns ? (cell.Y, cell.X) : (cell.X, cell.Y);
            if (w.Ground!.Deck(cell) && !onDeck && double.IsNaN(e.DeckOff))
            {
                (e.DeckOff, e.DeckAngle) = (ac - mid, angle);
            }
            onDeck = w.Ground.Deck(cell);
            e.Beside += ((cc >= c0 - 2 && cc < c0) || (cc > c1 && cc <= c1 + 2)) && ca >= a0 - 1 && ca <= a1 + 1 ? 1 : 0;
            if ((al - (ns ? c.Y : c.X)) * dir > span / 2 + 6)
            {
                done = w.Tick - order;
            }
            if (Environment.GetEnvironmentVariable("BRIDGE_TRACE") is { Length: > 0 } tn && kind.Contains(tn, StringComparison.Ordinal) && (w.Tick - order) % 40 == 0)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {kind} dir {dir} aside {aside} +{w.Tick - order}: along {(al - (ns ? c.Y : c.X)) * dir:0.0} side {ac - mid:0.0} mode {u.Mode} stuck {(u.Intent.Stuck ? 1 : 0)} left {u.Path.Count - u.PathIdx} detour {(u.Detouring ? 1 : 0)}"));
            }
        }
        var ideal = (span + 12 + 25.0) / (u.Def.Speed.Raw / (double)Fixed.OneRaw);
        e.Crossed = done != 0;
        e.Ratio = (done == 0 ? limit - order : done) / ideal;
        u.Pos = new Cell(2 + parked % 200 * 6, w.Grid.Height - 3 - parked / 200 * 6).Center();
        u.ClearPath();
        u.Order = new Order(OrderKind.Idle, default, 0, 0);
        parked++;
        return e;
    }

    private static int ReportEntries(TerrainMap map, List<Entry1> entries, string outPath)
    {
        using (var o = outPath == "" ? null : new StreamWriter(outPath))
        {
            o?.WriteLine("bridge,bkind,unit,dir,aside,crossed,ratio,foot_off,foot_angle,deck_off,deck_angle,side_in,side_out,beside,side_lift");
            foreach (var e in entries)
            {
                o?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{e.Bridge},{map.BridgeSites[e.Bridge].Kind},{e.Kind},{e.Dir},{e.Aside},{(e.Crossed ? 1 : 0)},{e.Ratio:0.00},{e.FootOff:0.00},{e.FootAngle:0},{e.DeckOff:0.00},{e.DeckAngle:0},{e.SideIn},{e.SideOut},{e.Beside},{e.SideLift:0.00}"));
            }
        }
        void Line(string name, IEnumerable<Entry1> g)
        {
            var l = g.ToList();
            var deck = l.Where(e => !double.IsNaN(e.DeckOff)).ToList();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name,-24} crossed {l.Count(e => e.Crossed),3}/{l.Count,-3} (never on it {l.Count(e => double.IsNaN(e.FootOff))}) time x{l.Average(e => e.Ratio):0.00} (worst {l.Max(e => e.Ratio):0.00}) " +
                $"deck off {(deck.Count > 0 ? deck.Average(e => Math.Abs(e.DeckOff)) : 0):0.00} (max {(deck.Count > 0 ? deck.Max(e => Math.Abs(e.DeckOff)) : 0):0.00}) " +
                $"angle {(deck.Count > 0 ? deck.Average(e => e.DeckAngle) : 0):0} (max {(deck.Count > 0 ? deck.Max(e => e.DeckAngle) : 0):0}) " +
                $"ramp angle max {l.Where(e => !double.IsNaN(e.FootAngle)).Select(e => e.FootAngle).DefaultIfEmpty(0).Max():0} " +
                $"side in {l.Count(e => e.SideIn > 0)} (lift {l.Max(e => e.SideLift):0.00}) out {l.Count(e => e.SideOut > 0)} beside {l.Count(e => e.Beside > 0)}"));
        }
        foreach (var g in entries.GroupBy(e => e.Bridge))
        {
            Line($"bridge {g.Key} {map.BridgeSites[g.Key].Kind}", g);
        }
        foreach (var g in entries.GroupBy(e => e.Kind))
        {
            Line(g.Key, g);
        }
        foreach (var g in entries.GroupBy(e => e.Aside == 0 ? "straight" : "angled"))
        {
            Line(g.Key, g);
        }
        Line("all", entries);
        return 0;
    }

    // Blocker puts what a run has on the deck in the middle of the span: a tank of the columns' side standing idle, a truck nobody drives
    // (nothing moves it), or a tank of the other side standing idle. (The trial runs a few more ticks than a driver needs to board.)
    private static List<uint> Blocker(World w, ContentSet content, string what, Cell c, bool ns)
    {
        var ids = new List<uint>();
        if (what == "")
        {
            return ids;
        }
        var us = w.Match.Players.First(q => q.Find(content, "main_battle_tank") is not null);
        var them = w.Match.Players.First(q => q != us);
        var def = what switch
        {
            "empty truck" => us.Find(content, "sentinel_cargo_truck")!,
            "enemy tank" => them.Find(content, "merkava_tank") ?? them.Find(content, "main_battle_tank") ?? them.Find(content, "technical")!,
            _ => us.Find(content, "main_battle_tank")!,
        };
        Facing.TryFromDirection(At(c, ns, 1).Center() - c.Center(), out var face);
        var id = w.Spawn(def, c, face);
        ids.Add(id);
        if (what != "empty truck" && def.Archetype.Crew is not null)
        {
            var owner = what == "enemy tank" ? them : us;
            var drv = w.Spawn(owner.Find(content, "Rifleman") ?? owner.Find(content, "Militia") ?? us.Find(content, "Rifleman")!, c, face);
            w.Issue(new Command { Tick = w.Tick + 1, Player = owner.Id, Kind = CommandKind.Enter, Units = [drv], Target = id });
            ids.Add(drv);
        }
        return ids;
    }

    private static void Trace(World w, string name, int bridge, uint order, Cell c, bool ns, List<(uint Id, int Sign)> units)
    {
        Console.WriteLine($"{name} bridge {bridge} +{w.Tick - order}");
        foreach (var (id, sign) in units)
        {
            if (w.Unit(id) is not { } u)
            {
                continue;
            }
            double along = ((ns ? u.Pos.Y.Raw : u.Pos.X.Raw) / (double)Fixed.OneRaw - (ns ? c.Y : c.X)) * sign;
            double side = (ns ? u.Pos.X.Raw : u.Pos.Y.Raw) / (double)Fixed.OneRaw - (ns ? c.X : c.Y);
            double head = (ns ? u.Heading.Y.Raw : u.Heading.X.Raw) * sign / (double)Fixed.OneRaw;
            double speed = Math.Sqrt(Sq(u.Vel.X.Raw) + Sq(u.Vel.Y.Raw)) / u.Def.Speed.Raw;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {id,5} {sign,2} {u.Def.Archetype.Id,-22} along {along,6:0.0} side {side,5:0.0} head {head,5:0.00} speed {speed,4:0.00} stuck {(u.Intent.Stuck ? 1 : 0)} stall {u.StallTicks,4} mode {u.Mode} left {u.Path.Count - u.PathIdx}"));
        }
    }

    private static void Place(World w, ContentSet content, string[] kinds, Cell c, bool ns, int dir, int half, List<(uint, int)> units, List<uint> drivers, int aside = 0)
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
            var off = aside + (kinds.Length > 1 ? side : 0);
            var at = Near(w, ns ? new Cell(near.X + off, near.Y) : new Cell(near.X, near.Y + off), def);
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
            Console.WriteLine($"  {name} {tc} width {w.Routing.Nav.Width(k)} regions {string.Join(" ", new[] { -span / 2 - 25, -span / 2 - 1, 0, span / 2 + 1, span / 2 + 25 }.Select(d => w.Routing.Component(k, w.Routing.Index(At(c, ns, d)))))}");
            Console.WriteLine("  cost   " + Row(x => $"{w.Routing.Nav.Cost(k, x.X, x.Y),3}", 0));
        }
        // The ground round it for a tank, along the road left to right: D deck, ~ water, # blocked, - road, . open.
        var tank = w.Routing.ClassOf((p1.Find(content, "main_battle_tank") ?? w.Match.Players[^1].Find(content, "main_battle_tank"))!);
        for (var side = -14; side <= 14; side++)
        {
            var line = string.Create(Enumerable.Range(-span / 2 - 40, span + 81).Count(), side, (buf, sd) =>
            {
                for (var q = 0; q < buf.Length; q++)
                {
                    var at = At(c, ns, q - span / 2 - 40);
                    var x = ns ? new Cell(at.X + sd, at.Y) : new Cell(at.X, at.Y + sd);
                    buf[q] = map.Deck(x) ? 'D' : map.OpenWater(x) ? '~' : w.Routing.Nav.Cost(tank, x.X, x.Y) == TerrainMap.Impassable ? '#' : map.RoadKindAt(x) != RoadKind.None ? '-' : '.';
                }
            });
            Console.WriteLine($"  {side,3} {line}");
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
