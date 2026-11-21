// The firefight bench (-bench-battle <name>; docs/design.md, "Infantry combat: the ground and the senses"; the game repo's
// docs/plans/infantry-battle-rules.md 25 and 8.4): deterministic, seeded scenes that print their pass lines.
//   1      scene 1, meeting in the open: 6 rifles against 6, both Attack-move, 10 cells apart
//   20     scene 20, fast and deadly: (a) scene 1; (b) 6 v 6 at 5 cells both behind low walls; (c) 6 in the open against 6 behind a
//          wall; (d) an LMG on 6 idle in the open at 5 cells; then 8.4's worked hit chances from the engine's own formula
//   500    scene 21's skeleton: 500 soldiers in 50 squads all in contact, tick p99 against the same scene with the rules off
//   cover  the cover data's build time and size on a 256 x 256 hilly wooded map, and on a map folder (cover=<dir with map.json>)
// Until INF-E1b-2 (the soldier think) and E1b-3 (the squad think) exist, soldiers use today's behaviour (unit AI and squads)
// plus E1b-1's posture, suppression and perception: lines that need the later jobs print as "not reachable yet".
using System.Diagnostics;
using System.Globalization;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static partial class BattleBench
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static (long Searches, long SearchWaited, long Moves, long Throws, long Reflexes) _lastThink;

    private static bool _off; // "<scene>-off": the same scene without the infantry combat rules (today's behaviour)

    public static int Run(string name, string contentDir, int seeds, uint maxTicks)
    {
        if (name.EndsWith("-off", StringComparison.Ordinal))
        {
            (_off, name) = (true, name[..^4]);
            Console.WriteLine("(infantry combat rules off)");
        }
        var content = ContentLoader.LoadDirectory(contentDir);
        var setupPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir))!, "setups", "battle.json");
        CompiledMatch Match(ulong seed) => MatchCompiler.Compile(content, Replay.LoadSetup(setupPath).For(content, seed));
        if (name == "1")
        {
            Scene("scene 1, meeting in the open", seeds, Match, maxTicks, Layout.Meeting, 4.5, 9.0, true);
            return 0;
        }
        if (name == "20")
        {
            Scene("scene 20a, 6 v 6 in the open, both closing", seeds, Match, maxTicks, Layout.Meeting, 4.5, 8.0, true);
            Scene("scene 20b, 6 v 6 at 5 cells, both behind low walls", seeds, Match, maxTicks, Layout.BothWalls, 12.0, 25.0, false);
            Scene("scene 20c, 6 in the open (blue) against 6 behind a wall (red)", seeds, Match, maxTicks, Layout.OpenVsWall, 6.0, 12.0, false);
            Pin(seeds, Match, maxTicks);
            Worked(Match(42));
            return 0;
        }
        if (name == "500")
        {
            return Scale(Match(42), 500, maxTicks);
        }
        if (name == "cover" || name.StartsWith("cover=", StringComparison.Ordinal))
        {
            return Cover(content, contentDir, Match(42), name.Length > 6 ? name[6..] : "");
        }
        if (name is "2" or "3" or "34")
        {
            Cover2(seeds, Match, maxTicks, Scene("scene 1, meeting in the open (scene 2's comparison)", seeds, Match, maxTicks, Layout.Meeting, 4.5, 9.0, true));
            return 0;
        }
        if (name == "think")
        {
            // INF-E1b-2's scenes, one after another.
            Cover2(seeds, Match, maxTicks, Scene("scene 1, meeting in the open (scene 2's comparison)", seeds, Match, maxTicks, Layout.Meeting, 4.5, 9.0, true));
            GrenadeScene(seeds, Match, maxTicks);
            VehicleScene(seeds, Match, maxTicks);
            SpreadScene(seeds, Match, maxTicks);
            MidBurstScene(seeds, Match, maxTicks);
            DitherScene(seeds, Match, maxTicks);
            return 0;
        }
        Console.Error.WriteLine($"bench-battle: unknown scene \"{name}\" (1, 2, 3, 34, think, 20, 500, cover, cover=<map dir>)");
        return 2;
    }

    private enum Layout
    {
        Meeting,
        BothWalls,
        OpenVsWall,
    }

    // World: a flat 64 x 40 field; blue's 6 riflemen in a column at x 20, red's at x 30 (Meeting) or 25 (walls), formed into
    // squads at tick 1; Meeting: both Attack-move through the other at tick 5; walls: rock cells (low hard cover) one cell in
    // front of the walled side, nobody ordered.
    private static (World W, List<uint> Blue, List<uint> Red) Field(CompiledMatch match, Layout layout, bool redIdle = false)
    {
        var w = new World(new Grid(64, 40), match) { CombatOff = _off };
        var (bx, rx) = layout == Layout.Meeting ? (20, 30) : (20, 25);
        if (layout == Layout.BothWalls)
        {
            Wall(w, bx + 1);
        }
        if (layout is Layout.BothWalls or Layout.OpenVsWall)
        {
            Wall(w, rx - 1);
        }
        var blue = new List<uint>();
        var red = new List<uint>();
        for (var i = 0; i < 6; i++)
        {
            blue.Add(w.Spawn(match.Players[0].Find(match.Content, "Rifleman")!, new Cell(bx, 17 + i), Facing.East));
            red.Add(w.Spawn(match.Players[1].Find(match.Content, "Rifleman")!, new Cell(rx, 17 + i), Facing.West));
        }
        if (layout != Layout.Meeting)
        {
            // Holding their ground: today's unit AI would walk round a wall's end after a target out of reach.
            w.Issue(new Command { Tick = 1, Player = match.Players[0].Id, Kind = CommandKind.SetStance, Units = blue, Stance = Stance.HoldGround });
            w.Issue(new Command { Tick = 1, Player = match.Players[1].Id, Kind = CommandKind.SetStance, Units = red, Stance = Stance.HoldGround });
        }
        if (layout == Layout.Meeting)
        {
            // Squads only when they move: a formed squad walks its members into its slots, which would take them off the walls.
            w.Issue(new Command { Tick = 1, Player = match.Players[0].Id, Kind = CommandKind.FormSquad, Units = blue });
            w.Issue(new Command { Tick = 1, Player = match.Players[1].Id, Kind = CommandKind.FormSquad, Units = red });
            w.Issue(new Command { Tick = 5, Player = match.Players[0].Id, Kind = CommandKind.AttackMove, Units = [blue[0]], Cell = new Cell(44, 19) });
            if (!redIdle)
            {
                w.Issue(new Command { Tick = 5, Player = match.Players[1].Id, Kind = CommandKind.AttackMove, Units = [red[0]], Cell = new Cell(6, 19) });
            }
        }
        return (w, blue, red);
    }

    private static void Wall(World w, int x)
    {
        for (var y = 16; y <= 23; y++)
        {
            w.Grid.Set(new Cell(x, y), Terrain.Blocked);
        }
    }

    private static bool Able(Unit? u) => u is { Alive: true } && u.Life is not { Wound: WoundState.Down };

    private static (int X, int Y, int N) Centre(World w, List<uint> ids)
    {
        long sx = 0, sy = 0, n = 0;
        foreach (var id in ids)
        {
            if (w.Unit(id) is { } u && Able(u))
            {
                (sx, sy, n) = (sx + u.Pos.X.Raw, sy + u.Pos.Y.Raw, n + 1);
            }
        }
        return n == 0 ? (0, 0, 0) : ((int)(sx / n), (int)(sy / n), (int)n);
    }

    // Decided (25): one side has no able member, or no member inside the other's reach (rifle 5 x 1.2 = 6 cells) for 100 ticks.
    private static bool Within(World w, List<uint> a, List<uint> b, long reach)
    {
        foreach (var i in a)
        {
            if (w.Unit(i) is not { } u || !Able(u))
            {
                continue;
            }
            foreach (var j in b)
            {
                if (w.Unit(j) is { } v && Able(v))
                {
                    var d = u.Pos - v.Pos;
                    if (d.X.Raw * d.X.Raw + d.Y.Raw * d.Y.Raw <= reach * reach)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private sealed record RunResult(uint FirstShot, uint Decided, string Loser, double GapAtShot, double MinGap, int StandViolations,
        bool BothPinned, int BlueLeft, int RedLeft, int MaxSuppression, int BackTurned = 0, int Running = 0, int Dashes = 0);

    private static RunResult Play(World w, List<uint> blue, List<uint> red, uint maxTicks)
    {
        uint first = 0, decided = 0, apart = 0, quiet = 0;
        double gapAtShot = 0, minGap = double.MaxValue;
        var standing = new Dictionary<uint, int>();
        var violations = 0;
        var maxSup = 0;
        int backTurned = 0, running = 0, dashes = 0;
        var reach = 6 * Fixed.OneRaw;
        string loser = "none";
        var all = blue.Concat(red).ToList();
        var debug = Environment.GetEnvironmentVariable("BATTLE_DEBUG") == "1";
        while (w.Tick < maxTicks)
        {
            w.Step();
            if (debug && w.Tick % 10 == 0 && w.Tick <= 200 && w.Unit(blue[0]) is { Fight: { } df } du)
            {
                Console.WriteLine($"    t{w.Tick} pos {du.Pos.X.Raw / 1024.0:0.0},{du.Pos.Y.Raw / 1024.0:0.0} moving {du.Moving} path {du.Path.Count} order {du.Order.Kind} posture {df.Posture}->{df.PostureTo} left {df.PostureLeft} sup {df.Suppression} contact {df.InContact} alert {df.Alert} contacts {df.Contacts.Count} axis {df.Axis} react {df.ReactAt} pct {w.PosturePercent(du)}");
            }
            if (Environment.GetEnvironmentVariable("BATTLE_DEBUG") == "2" && w.Tick is 30 or 60 or 120 or 200)
            {
                foreach (var id in all)
                {
                    if (w.Unit(id) is { Fight: { } g } v)
                    {
                        Console.WriteLine($"    t{w.Tick} {(blue.Contains(id) ? "B" : "R")}{id} at {v.Pos.X.Raw / 1024.0:0.0},{v.Pos.Y.Raw / 1024.0:0.0} hp {v.HP} {g.Posture}->{g.PostureTo} d{g.Ducked} sup {g.Suppression} axis {g.Axis} cover {(g.Axis >= 0 ? w.CoverToward(Cell.Of(v.Pos), g.Axis, w.Content.InfantryCombat!) : CoverKind.None)} shot {v.LastShot} contact {g.InContact}");
                    }
                }
            }
            var (bx, by, bn) = Centre(w, blue);
            var (rx, ry, rn) = Centre(w, red);
            if (bn > 0 && rn > 0)
            {
                var gap = Math.Sqrt(Math.Pow(bx - rx, 2) + Math.Pow(by - ry, 2)) / Fixed.OneRaw;
                minGap = Math.Min(minGap, gap);
                if (first == 0 && w.ShotEvents.Count > 0)
                {
                    (first, gapAtShot) = (w.Tick, gap);
                }
            }
            foreach (var id in all)
            {
                if (w.Unit(id) is not { Fight: { } f } u || !Able(u))
                {
                    continue;
                }
                maxSup = Math.Max(maxSup, f.Suppression);
                if (World.Walking(u) && f.InContact && !f.OutOfReach && f.Axis >= 0)
                {
                    backTurned += World.DirSteps(World.FacingToDir(u.Facing), f.Axis) > 8 ? 1 : 0; // INF-E1b-2: the back rule
                    running += f.Gait is Gait.Run or Gait.Sprint or Gait.CrouchRun && !f.SpotMoving ? 1 : 0;
                    dashes += f.Gait == Gait.CrouchRun && f.SpotMoving ? 1 : 0;
                }
                var high = f.Axis >= 0 && w.CoverToward(Cell.Of(u.Pos), f.Axis, w.Content.InfantryCombat!) is CoverKind.High or CoverKind.Soft;
                var bad = !World.Walking(u) && f.InContact && u.Life is not { Carrying: not 0 } &&
                    !(f.PostureTo is Posture.Kneel or Posture.Prone || (f.PostureTo == Posture.Crouch && f.Ducked) || (f.PostureTo == Posture.Stand && high));
                standing[id] = bad ? standing.GetValueOrDefault(id) + 1 : 0;
                if (standing[id] == 21)
                {
                    violations++;
                }
            }
            if (first == 0)
            {
                continue;
            }
            if (bn == 0 || rn == 0)
            {
                (decided, loser) = (w.Tick, bn == 0 && rn == 0 ? "both" : bn == 0 ? "blue" : "red");
                break;
            }
            apart = Within(w, blue, red, reach) ? 0 : apart + 1;
            if (apart >= 100)
            {
                (decided, loser) = (w.Tick - 100, "apart");
                break;
            }
            quiet = w.ShotEvents.Count > 0 ? 0 : quiet + 1;
            if (quiet >= 100)
            {
                // Stalled: nobody fired for 5 s (the survivors out of each other's range, holding their ground): ended at the last
                // shot, lost by the side with fewer able men.
                (decided, loser) = (w.Tick - 100, bn < rn ? "blue, stalled" : rn < bn ? "red, stalled" : "even, stalled");
                break;
            }
        }
        bool Pinned(List<uint> ids)
        {
            int n = 0, p = 0;
            foreach (var id in ids)
            {
                if (w.Unit(id) is { Fight: { } f } u && Able(u))
                {
                    n++;
                    p += f.Suppression >= 60 ? 1 : 0;
                }
            }
            return n > 0 && p * 2 >= n;
        }
        return new RunResult(first, decided, loser, gapAtShot, minGap == double.MaxValue ? 0 : minGap, violations, Pinned(blue) && Pinned(red),
            Centre(w, blue).N, Centre(w, red).N, maxSup, backTurned, running, dashes);
    }

    private static double Scene(string title, int seeds, Func<ulong, CompiledMatch> match, uint maxTicks, Layout layout, double lo, double hi, bool closing)
    {
        Console.WriteLine($"== {title} ({seeds} seeds)");
        var times = new List<double>();
        int pinned = 0, violations = 0, closed = 0, undecided = 0, stalled = 0, back = 0, runs = 0, dashes = 0;
        foreach (var seed in Seeds(seeds))
        {
            var (w, blue, red) = Field(match(seed), layout);
            var r = Play(w, blue, red, maxTicks);
            var t = r.Decided == 0 ? double.NaN : (r.Decided - r.FirstShot) / 20.0;
            if (r.Decided == 0)
            {
                undecided++;
            }
            else
            {
                times.Add(t);
            }
            pinned += r.BothPinned ? 1 : 0;
            stalled += r.Loser.EndsWith("stalled", StringComparison.Ordinal) ? 1 : 0;
            violations += r.StandViolations;
            (back, runs, dashes) = (back + r.BackTurned, runs + r.Running, dashes + r.Dashes);
            closed += Math.Abs(r.MinGap - 3.5) <= 1 ? 1 : 0;
            Console.WriteLine(string.Create(Inv, $"  seed {seed}: first shot tick {r.FirstShot}, decided {(r.Decided == 0 ? "no" : $"{t:0.00} s")} (lost: {r.Loser}; left blue {r.BlueLeft}, red {r.RedLeft}); gap at first shot {r.GapAtShot:0.0} cells, nearest {r.MinGap:0.0}; max suppression {r.MaxSuppression}; stood still in contact > 20 ticks: {r.StandViolations}"));
        }
        times.Sort();
        var median = times.Count == 0 ? double.NaN : times[times.Count / 2];
        var inBand = times.Count(t => t >= lo && t <= hi);
        Console.WriteLine(string.Create(Inv, $"PASS-LINE decided {lo:0.#} to {hi:0.#} s: median {median:0.00} s, {inBand} of {seeds} in band, {undecided} undecided, {stalled} stalled: {(inBand * 2 >= seeds && median >= lo && median <= hi ? "met" : "NOT MET")}"));
        if (closing)
        {
            Console.WriteLine($"PASS-LINE both close to 3.5 cells (+-1) before they stop: {closed} of {seeds}: {(closed == seeds ? "met" : "NOT MET")} (the Advance to 70% of range is INF-E1b-3's; today's squads stop at weapon range)");
            Console.WriteLine($"PASS-LINE no man in reach moves with his body more than 90 deg off his threat axis: {back} soldier-ticks: {(back == 0 ? "met" : "NOT MET")}");
            Console.WriteLine($"PASS-LINE nobody runs or crouch-runs except in a bound: {runs} soldier-ticks ({dashes} more in dashes to close cover, rules 7.2; bounds are INF-E1b-3's): {(runs == 0 ? "met" : "NOT MET")}");
        }
        Console.WriteLine($"PASS-LINE every soldier who stops in contact kneels or goes prone within 20 ticks: {violations} soldier-stops over: {(violations == 0 ? "met" : "NOT MET")}");
        Console.WriteLine($"REPORT both sides pinned when decided: {pinned} of {seeds} runs (the rules' alarm: more than 1 in 10)");
        return median;
    }

    // Pin: scene 20d / scene 7's start: blue's LMG gunner at 5 cells from 6 red riflemen idle in the open.
    private static void Pin(int seeds, Func<ulong, CompiledMatch> match, uint maxTicks)
    {
        Console.WriteLine($"== scene 20d, the LMG pin: an LMG at 5 cells from 6 idle in the open ({seeds} seeds)");
        var met = 0;
        foreach (var seed in Seeds(seeds))
        {
            var m = match(seed);
            var w = new World(new Grid(64, 40), m);
            List<uint> blue = [w.Spawn(m.Players[0].Find(w.Content, "Gunner")!, new Cell(20, 19), Facing.East)];
            if (w.Unit(blue[0])!.Fight is { } gf)
            {
                (gf.Posture, gf.PostureTo) = (Posture.Prone, Posture.Prone); // deployed (the scene's "LMG deployed": prone on its bipod)
            }
            var red = new List<uint>();
            for (var i = 0; i < 6; i++)
            {
                red.Add(w.Spawn(m.Players[1].Find(w.Content, "Rifleman")!, new Cell(25, 17 + i), Facing.West));
            }
            w.Issue(new Command { Tick = 1, Player = 2, Kind = CommandKind.FormSquad, Units = red });
            w.Issue(new Command { Tick = 2, Player = 2, Kind = CommandKind.SetStance, Units = red, Stance = Stance.HoldGround });
            w.Issue(new Command { Tick = 2, Player = 1, Kind = CommandKind.SetStance, Units = blue, Stance = Stance.HoldGround });
            uint first = 0, half = 0, prone = 0;
            while (w.Tick < Math.Min(maxTicks, 400u))
            {
                w.Step();
                if (first == 0 && w.ShotEvents.Any(e => e.Shooter == blue[0]))
                {
                    first = w.Tick;
                }
                if (Environment.GetEnvironmentVariable("BATTLE_DEBUG") == "3" && w.Tick % 10 == 0 && w.Tick <= 80 && w.Unit(blue[0]) is { Fight: { } gf2 } g2)
                {
                    Console.WriteLine($"    t{w.Tick} gunner hp {g2.HP} {gf2.Posture}->{gf2.PostureTo} left {gf2.PostureLeft} sup {gf2.Suppression} shot {g2.LastShot} red sup {string.Join(",", red.Select(id => w.Unit(id) is { Fight: { } rf } ru ? $"{rf.Suppression}{(ru.Alive ? "" : "x")}" : "-"))}");
                }
                var at60 = red.Count(id => w.Unit(id) is { Fight: { Suppression: >= 60 } } u && Able(u));
                var down = red.Count(id => w.Unit(id) is { Fight: { Suppression: >= 60, Posture: Posture.Prone } } u && Able(u));
                if (first != 0 && half == 0 && at60 >= 3)
                {
                    half = w.Tick;
                }
                if (half != 0 && prone == 0 && down >= 3)
                {
                    prone = w.Tick;
                }
            }
            var ok = first != 0 && half != 0 && half - first <= 60;
            met += ok ? 1 : 0;
            Console.WriteLine(string.Create(Inv, $"  seed {seed}: first burst tick {first}, half of red at 60+ {(half == 0 ? "never" : $"{(half - first) / 20.0:0.00} s after")}, those prone {(prone == 0 ? "never" : $"{(prone - half) / 20.0:0.00} s later")}"));
        }
        Console.WriteLine($"PASS-LINE half the squad at 60+ within 3 s of the first burst: {met} of {seeds}: {(met == seeds ? "met" : "NOT MET")}");
    }

    // Worked: 8.4's hit chances from the engine's own formula (HitChance), two Resistance riflemen face to face.
    private static void Worked(CompiledMatch match)
    {
        Console.WriteLine("== 8.4 worked through: one rifle shot's hit chance (engine formula; rules' figures in brackets)");
        void Line(string what, double cells, Posture shooter, Posture target, int suppression, bool wall, string expect)
        {
            var w = new World(new Grid(40, 20), match);
            var tx = 10 + (int)Math.Ceiling(cells);
            if (wall)
            {
                w.Grid.Set(new Cell(tx - 1, 10), Terrain.Blocked);
            }
            var a = w.Unit(w.Spawn(match.Players[0].Find(match.Content, "Rifleman")!, new Cell(10, 10), Facing.East))!;
            var b = w.Unit(w.Spawn(match.Players[1].Find(match.Content, "Rifleman")!, new Cell(tx, 10), Facing.West))!;
            a.Pos = new Vec(Fixed.FromRaw(b.Pos.X.Raw - (long)(cells * Fixed.OneRaw)), b.Pos.Y);
            (a.Fight!.Posture, a.Fight.PostureTo, a.Fight.Suppression100) = (shooter, shooter, suppression * 100);
            (b.Fight!.Posture, b.Fight.PostureTo) = (target, target);
            var weapon = a.Def.Weapons[0];
            var chance = w.HitChance(a, b, weapon) / 10.0;
            Console.WriteLine(string.Create(Inv, $"  {what}: {chance:0.0}% [{expect}]"));
        }
        Line("both kneeling at 3.5 cells", 3.5, Posture.Kneel, Posture.Kneel, 0, false, "61%");
        Line("the same, shooter's suppression 30+", 3.5, Posture.Kneel, Posture.Kneel, 35, false, "52%");
        Line("at 5 cells, both prone", 5, Posture.Prone, Posture.Prone, 0, false, "24%, with 85% for suppression: 28% without it");
        Line("at 5 cells, prone at a kneeler behind a low wall", 5, Posture.Prone, Posture.Kneel, 0, true, "about 20%");
        Line("at 5 cells, kneeling behind the wall at a prone man in the open", 5, Posture.Kneel, Posture.Prone, 0, false, "about 26%");
        Line("at 5 cells, kneeling behind low walls both", 5, Posture.Kneel, Posture.Kneel, 0, true, "about 18% firing");
    }

    // Scale: 50 squads of 10 riflemen, 25 a side in lines 12 cells apart, Attack-move through each other; the tick time with the
    // rules against the same scene with them off (CombatOff), after a warm-up run of each.
    private static int Scale(CompiledMatch match, int soldiers, uint maxTicks)
    {
        var ticks = Math.Min(maxTicks, 600u);
        (double P99, double Mean, long Capped, long Urgent, long Thinks, long Heard, int CappedTicks) One(bool off)
        {
            var w = new World(new Grid(256, 160), match) { CombatOff = off };
            var squads = soldiers / 10;
            for (var g = 0; g < squads; g++)
            {
                var side = g % 2;
                var row = g / 2;
                var player = match.Players[side];
                var ids = new List<uint>();
                var (x0, y0) = (side == 0 ? 100 : 112, 10 + row * 5);
                for (var i = 0; i < 10; i++)
                {
                    ids.Add(w.Spawn(player.Find(match.Content, "Rifleman")!, new Cell(x0 + i % 2 * (side == 0 ? -1 : 1), y0 + i / 2), side == 0 ? Facing.East : Facing.West));
                }
                w.Issue(new Command { Tick = 1, Player = player.Id, Kind = CommandKind.FormSquad, Units = ids });
                w.Issue(new Command { Tick = 5, Player = player.Id, Kind = CommandKind.AttackMove, Units = [ids[0]], Cell = new Cell(side == 0 ? 150 : 60, y0 + 2) });
            }
            var times = new List<double>();
            var sw = new Stopwatch();
            var capped = 0;
            long lastCapped = 0;
            while (w.Tick < ticks)
            {
                sw.Restart();
                w.Step();
                times.Add(sw.Elapsed.TotalMilliseconds);
                if (w.CombatStats.UrgentCapped != lastCapped)
                {
                    capped++;
                    lastCapped = w.CombatStats.UrgentCapped;
                }
            }
            times.Sort();
            _lastThink = w.ThinkStats;
            return (times[(int)(times.Count * 0.99)], times.Average(), w.CombatStats.UrgentCapped, w.CombatStats.Urgent, w.CombatStats.Thinks, w.CombatStats.Heard, capped);
        }
        One(true);
        One(false);
        var basis = One(true);
        var run = One(false);
        Console.WriteLine($"== scene 21 skeleton: {soldiers} soldiers in {soldiers / 10} squads, {ticks} ticks");
        Console.WriteLine(string.Create(Inv, $"  rules off: tick p99 {basis.P99:0.000} ms, mean {basis.Mean:0.000} ms; rules on: p99 {run.P99:0.000} ms, mean {run.Mean:0.000} ms"));
        Console.WriteLine(string.Create(Inv, $"  soldier thinks {run.Thinks}, urgent {run.Urgent}, urgent over the cap {run.Capped} (in {run.CappedTicks} ticks), heard areas {run.Heard}"));
        Console.WriteLine(string.Create(Inv, $"PASS-LINE at most 2 ms added to tick p99: {run.P99 - basis.P99:+0.000;-0.000} ms: {(run.P99 - basis.P99 <= 2 ? "met" : "NOT MET")} (E1b-1's senses and E1b-2's soldier think; E1b-3's squad think comes on top)"));
        Console.WriteLine(string.Create(Inv, $"PASS-LINE urgent think cap hit in under 5% of ticks: {100.0 * run.CappedTicks / ticks:0.0}%: {(run.CappedTicks * 20 < ticks ? "met" : "NOT MET")}"));
        var st = _lastThink;
        Console.WriteLine($"  position searches {st.Searches}, spots reached {st.Moves}, grenades thrown {st.Throws}, reflexes {st.Reflexes}");
        Console.WriteLine(string.Create(Inv, $"PASS-LINE under 5% of searches wait over 2 s: {st.SearchWaited} of {st.Searches} waited for the cap at all ({(st.Searches == 0 ? 0 : 100.0 * st.SearchWaited / st.Searches):0.0}%; a waiting search is first in line the next tick): {(st.SearchWaited * 20 <= st.Searches ? "met" : "NOT MET")}"));
        Console.WriteLine("PASS-LINE the hearing scan under 0.2 ms of tick p99: not measured apart (it runs inside the squad thinks; see the rules-on p99)");
        return 0;
    }

    // Cover: the cover data's first build (5.1) timed (best of 3) and its size.
    private static int Cover(ContentSet content, string contentDir, CompiledMatch match, string mapDir)
    {
        var rules = TerrainLoader.LoadRules(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir))!, "terrain", "rules.json"));
        var ic = content.InfantryCombat!;
        void Time(string what, Func<World> make)
        {
            var best = double.MaxValue;
            long bytes = 0;
            for (var i = 0; i < 3; i++)
            {
                var w = make();
                var sw = Stopwatch.StartNew();
                var m = w.BuildCover(ic);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                bytes = m.Bytes;
            }
            Console.WriteLine(string.Create(Inv, $"  {what}: build {best:0.0} ms, {bytes / 1024.0 / 1024.0:0.00} MB"));
        }
        Console.WriteLine("== cover data (5.1): first build, best of 3");
        Time("256 x 256, hills, forest stripes, walls", () =>
        {
            const int s = 256;
            var w = new World(new Grid(s, s), match);
            var corners = new int[(s + 1) * (s + 1)];
            for (var y = 0; y <= s; y++)
            {
                for (var x = 0; x <= s; x++)
                {
                    corners[y * (s + 1) + x] = 1536 * Math.Abs(x % 32 - 16) / 16 + 768 * Math.Abs(y % 32 - 16) / 16;
                }
            }
            var flags = new byte[s * s];
            for (var i = 0; i < flags.Length; i++)
            {
                flags[i] = (byte)((i % s + i / s / 2) / 12 % 4);
            }
            w.AttachTerrain(new TerrainMap(s, s, corners, flags, rules));
            for (var y = 4; y < s; y += 16)
            {
                for (var x = 8; x < s; x += 16)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        w.Grid.Set(new Cell(x, y + k), Terrain.Blocked);
                    }
                }
            }
            return w;
        });
        if (mapDir != "")
        {
            var map = TerrainLoader.LoadMap(Path.Combine(mapDir, "map.json"), rules, content);
            Time($"{Path.GetFileName(Path.GetFullPath(mapDir))} ({map.Width} x {map.Height})", () =>
            {
                var w = new World(new Grid(map.Width, map.Height), match);
                w.AttachTerrain(map);
                return w;
            });
        }
        Console.WriteLine("PASS-LINE targets: under 50 ms for 256 x 256, under 2 s for zuberec_01 (rules 23)");
        return 0;
    }

    private static IEnumerable<ulong> Seeds(int n)
    {
        for (var i = 0; i < n; i++)
        {
            yield return 42 + (ulong)i * 7;
        }
    }
}
