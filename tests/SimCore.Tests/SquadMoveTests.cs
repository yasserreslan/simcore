using System.Globalization;
using System.Text;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-MOVE (docs/design.md, "Squads: coherent moves"; the owner's words of 2026-11-21 after watching the ambush-road demo): a
// squad's move is coherent: every member knows his final position at the order and walks to it from his first step, the squad
// keeps together by pace, not by stopping, and nobody reshuffles on arrival. The bench measures a squad of 8 given Move (March
// and Assault), Hold and Ambush orders on open ground, through a gap and round a building, per member; the tests hold the rules.
public class SquadMoveTests(ITestOutputHelper output)
{
    public enum Layout { Open, Gap, Building }

    public enum Kind { March, MarchShort, Assault, Hold, Ambush }

    private const int Size = 96;
    private static readonly Cell From = new(20, 48);

    private static Cell GoalOf(Kind k) => k == Kind.MarchShort ? new Cell(24, 38) : new Cell(56, 46);

    // A squad of 8 just formed in a loose block of 4 x 2 round (20, 48), 2 cells apart, facing east: five riflemen, two tank
    // hunters and the mortar (the slowest: the squad's pace), the leader the first rifleman.
    internal static (World W, Squad S, List<Unit> Men) Setup(Layout layout, SquadMode mode)
    {
        var w = Arena.New(Size);
        if (layout == Layout.Gap)
        {
            for (var y = 0; y < Size; y++)
            {
                if (y is < 55 or > 57)
                {
                    w.Grid.Set(new Cell(38, y), Terrain.Blocked); // a wall across the way with a gap of 3 cells, off the straight way
                }
            }
        }
        else if (layout == Layout.Building)
        {
            for (var y = 42; y <= 52; y++)
            {
                for (var x = 34; x <= 41; x++)
                {
                    w.Grid.Set(new Cell(x, y), Terrain.Blocked); // a building of 8 x 11 cells on the straight way
                }
            }
        }
        string[] kinds = ["Rifleman", "Rifleman", "Tank hunter", "Rifleman", "mortar", "Rifleman", "Tank hunter", "Rifleman"];
        var men = new List<Unit>();
        for (var i = 0; i < kinds.Length; i++)
        {
            men.Add(w.Spawn(1, kinds[i], From.X - 3 + i % 4 * 2, From.Y - 1 + i / 4 * 2, Facing.East));
        }
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.FormSquad, Units = [.. men.Select(u => u.Id)] });
        w.Run(1);
        var s = w.SquadOf(men[0].Id)!;
        if (mode != SquadMode.March)
        {
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetSquadMode, Units = [men[0].Id], Index = (int)mode });
        }
        w.Run(60); // settled in its shape
        return (w, s, men);
    }

    internal static void Give(World w, List<Unit> men, Kind k)
    {
        var g = GoalOf(k);
        var c = k switch
        {
            Kind.Hold => new Command { Kind = CommandKind.SetSquadMode, Index = (int)SquadMode.Hold, Cell = g, Cell2 = new Cell(g.X + 10, g.Y) },
            Kind.Ambush => new Command { Kind = CommandKind.SetSquadMode, Index = (int)SquadMode.Ambush, Cell = new Cell(g.X + 8, g.Y - 12), Cell2 = new Cell(g.X + 8, g.Y + 12) },
            _ => new Command { Kind = CommandKind.Move, Cell = g },
        };
        w.Issue(c with { Tick = w.Tick, Player = 1, Units = [men[0].Id] });
    }

    // Per member: ticks stalled (he will still move but stepped under 10% of his walk), of those next to a squadmate (1 cell);
    // ticks with path left and no step (what the client played a gait on); times his target jumped (over 1.5 cells in a tick);
    // the path walked over the straight way to his rest, the angle of his first half cell to it, the way walked after the squad
    // arrived (over the way he had left then). Per squad: crossings of the last legs (straight, to the rest), ticks with two members closer than 0.4 cell, the ticks it took.
    internal sealed record Member(uint Id, string Def, int Stalled, int Jammed, int GaitStill, int Jumps, double Ratio, double Angle, double After);

    internal sealed record Result(List<Member> Men, int Crossings, int Close, int Took);

    internal static Result Measure(World w, Squad s, List<Unit> men, Kind k, int limit = 2400, bool give = true)
    {
        var n = men.Count;
        var start = men.Select(u => u.Pos).ToArray();
        var pos = new List<Vec[]>();
        var moving = new List<bool[]>();
        var own = new List<bool[]>();
        var targets = new List<Vec?[]>();
        var close = 0;
        var arrivedAt = -1;
        var hadOrder = s.Order != OrderKind.Idle;
        var still = 0;
        if (give)
        {
            Give(w, men, k);
        }
        var t = 0;
        var debug = Environment.GetEnvironmentVariable("SQUAD_MOVE_DEBUG") == k.ToString();
        for (; t < limit; t++)
        {
            w.Run(1);
            if (debug && t % 50 == 0)
            {
                Console.WriteLine($"t{t} {s.State} order {s.Order} placed {s.HoldPlaced} anchor {s.Anchor.X.Raw / 1024},{s.Anchor.Y.Raw / 1024} goal {s.Goal} " +
                    string.Join(" ", men.Select(u => $"{u.Pos.X.Raw / 1024},{u.Pos.Y.Raw / 1024}{(u.Moving ? "m" : "")}{u.Order.Kind.ToString()[0]}")));
            }
            pos.Add(men.Select(u => u.Pos).ToArray());
            moving.Add(men.Select(u => u.Moving).ToArray());
            own.Add(men.Select(u => u.Order.Kind is not (OrderKind.Idle or OrderKind.Move or OrderKind.AttackMove)).ToArray()); // (an ambush's mine to lay)
            targets.Add(men.Select(u => u.SquadFollow ? u.SquadSlot : (Vec?)null).ToArray());
            hadOrder |= s.Order != OrderKind.Idle;
            if (hadOrder && arrivedAt < 0 && s.Order == OrderKind.Idle)
            {
                arrivedAt = t;
            }
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    if ((men[i].Pos - men[j].Pos).LenSq() < (long)Fixed.OneRaw * Fixed.OneRaw * 16 / 100)
                    {
                        close++;
                    }
                }
            }
            var settled = s.Order == OrderKind.Idle && (k is not (Kind.Hold or Kind.Ambush) || s.HoldPlaced);
            var stepped = t > 0 && men.Select((u, i) => (pos[t][i] - pos[t - 1][i]).LenSq()).Max() > 4;
            still = settled && !stepped ? still + 1 : 0;
            if (still >= 60)
            {
                break;
            }
        }
        var end = pos[^1];
        var result = new List<Member>();
        static double D(Vec a, Vec b) => (b - a).Len().Raw / (double)Fixed.OneRaw;
        for (var i = 0; i < n; i++)
        {
            var walk = men[i].Def.Speed.Raw;
            int stalled = 0, jammed = 0, gait = 0, jumps = 0;
            double walked = 0, after = 0, angle = double.NaN;
            Vec? last = null;
            for (var x = 0; x < pos.Count; x++)
            {
                var prev = x == 0 ? start[i] : pos[x - 1][i];
                var step = (pos[x][i] - prev).Len().Raw;
                walked += step / (double)Fixed.OneRaw;
                if (arrivedAt >= 0 && x > arrivedAt && !own[x][i])
                {
                    after += step / (double)Fixed.OneRaw;
                }
                if (arrivedAt >= 0 && x == arrivedAt)
                {
                    after -= D(pos[x][i], end[i]); // (the way left to his rest at arrival is no reshuffle: the excess is)
                }
                if (own[x][i])
                {
                    continue; // on an order of his own: not the squad's move
                }
                if (step * 10 < walk && D(pos[x][i], end[i]) > 0.5)
                {
                    stalled++;
                    if (Enumerable.Range(0, n).Any(j => j != i && D(pos[x][i], pos[x][j]) < 1))
                    {
                        jammed++;
                    }
                }
                if (moving[x][i] && step * 10 < walk)
                {
                    gait++;
                }
                if (targets[x][i] is { } tg)
                {
                    if (last is { } l && D(l, tg) > 1.5)
                    {
                        jumps++;
                    }
                    last = tg;
                }
                if (double.IsNaN(angle) && D(start[i], pos[x][i]) >= 0.5 && D(start[i], end[i]) >= 1)
                {
                    var a = pos[x][i] - start[i];
                    var b = end[i] - start[i];
                    var dot = (double)a.X.Raw * b.X.Raw + (double)a.Y.Raw * b.Y.Raw;
                    var crs = (double)a.X.Raw * b.Y.Raw - (double)a.Y.Raw * b.X.Raw;
                    angle = Math.Abs(Math.Atan2(crs, dot)) * 180 / Math.PI;
                }
            }
            var straight = D(start[i], end[i]);
            result.Add(new Member(men[i].Id, men[i].Def.Archetype.Id, stalled, jammed, gait, jumps, straight >= 1 ? walked / straight : double.NaN,
                angle, arrivedAt < 0 ? double.NaN : after));
        }
        // The crossings are of the last legs: from where each first walked to his rest (his target within 0.75 cell of it).
        var leg = new Vec[n];
        for (var i = 0; i < n; i++)
        {
            leg[i] = start[i];
            for (var x = 0; x < pos.Count; x++)
            {
                if (targets[x][i] is { } tg && D(tg, end[i]) <= 0.75)
                {
                    leg[i] = x == 0 ? start[i] : pos[x - 1][i];
                    break;
                }
            }
        }
        var crossings = 0;
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (Cross(leg[i], end[i], leg[j], end[j]))
                {
                    crossings++;
                }
            }
        }
        if (Environment.GetEnvironmentVariable("SQUAD_MOVE_DEBUG") == k.ToString())
        {
            for (var i = 0; i < n; i++)
            {
                var f = s.FinalPts.Length == n ? s.FinalPts[i] : default;
                Console.WriteLine($"#{men[i].Id} {men[i].Def.Archetype.Id}: start {start[i].X.Raw / 1024.0:0.0},{start[i].Y.Raw / 1024.0:0.0} final {f.X.Raw / 1024.0:0.0},{f.Y.Raw / 1024.0:0.0} end {end[i].X.Raw / 1024.0:0.0},{end[i].Y.Raw / 1024.0:0.0} stalled {result[i].Stalled} at {string.Join(",", Enumerable.Range(0, pos.Count).Where(x => (pos[x][i] - (x == 0 ? start[i] : pos[x - 1][i])).Len().Raw * 10 < men[i].Def.Speed.Raw && D(pos[x][i], end[i]) > 0.5).Take(12))} depth {s.SlotDepth[i].Raw / 1024.0:0.0} pick {s.HoldPicks.FirstOrDefault(p => p.Unit == men[i].Id).Cell}");
            }
        }
        return new Result(result, crossings, close, t);
    }

    // Cross: segments ab and cd cross properly.
    private static bool Cross(Vec a, Vec b, Vec c, Vec d)
    {
        static double O(Vec p, Vec q, Vec r) => (double)(q.X.Raw - p.X.Raw) * (r.Y.Raw - p.Y.Raw) - (double)(q.Y.Raw - p.Y.Raw) * (r.X.Raw - p.X.Raw);
        double o1 = O(a, b, c), o2 = O(a, b, d), o3 = O(c, d, a), o4 = O(c, d, b);
        return o1 * o2 < 0 && o3 * o4 < 0;
    }

    private static SquadMode ModeOf(Kind k) => k switch { Kind.Assault => SquadMode.Assault, _ => SquadMode.March };

    internal static string Row(Layout l, Kind k, Result r)
    {
        var m = r.Men;
        string F(double v) => double.IsNaN(v) ? "-" : v.ToString("0.00", CultureInfo.InvariantCulture);
        double Max(Func<Member, double> f) => m.Select(f).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
        return $"| {l} | {k} | {m.Sum(x => x.Stalled)} ({m.Sum(x => x.Jammed)}) | {m.Sum(x => x.GaitStill)} | {m.Sum(x => x.Jumps)} (max {m.Max(x => x.Jumps)}) | " +
            $"{F(Max(x => x.Ratio))} | {F(Max(x => x.Angle))} | {F(m.Select(x => x.After).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Sum())} | " +
            $"{r.Crossings} | {r.Close} | {r.Took} |";
    }

    private Result Run(Layout l, Kind k)
    {
        var (w, s, men) = Setup(l, ModeOf(k));
        var r = Measure(w, s, men, k);
        output.WriteLine(Row(l, k, r));
        return r;
    }

    private static int MemberTicks(Result r) => r.Men.Count * r.Took;

    // A move within the direct range (11 cells): every member walks straight to his final position from his first step (within 15
    // degrees of it, no detour), at the pace that brings them in together; nobody stalls, no target changes, no ways cross, no
    // reshuffle. (Before INF-MOVE: 296 stalled member-ticks, first steps up to 124 degrees off, 9 crossings, 17.5 cells walked
    // after arrival.)
    [Fact]
    public void AShortMoveGoesStraightToTheFinalPositions()
    {
        var r = Run(Layout.Open, Kind.MarchShort);
        Assert.Equal(0, r.Men.Sum(m => m.Stalled));
        Assert.All(r.Men, m => Assert.Equal(0, m.Jumps));
        Assert.All(r.Men, m => Assert.True(m.Angle <= 15, $"#{m.Id}'s first step {m.Angle:0} degrees off his final position"));
        Assert.All(r.Men, m => Assert.True(m.Ratio <= 1.1, $"#{m.Id} walked {m.Ratio:0.00} times the straight way"));
        Assert.Equal(0, r.Crossings);
        Assert.All(r.Men, m => Assert.True(m.After <= 0.5, $"#{m.Id} reshuffled {m.After:0.00} cells after arrival"));
    }

    // A long move (36 cells) in March and Assault: the shape on the way, nobody stands with way left (a member ahead of his slot
    // walks on slowly, a laggard slows the squad), each changes target at most once (his slot to his final position, within the
    // direct range), the last legs never cross, and the rest is the final position (no reshuffle into role order on arrival).
    [Theory]
    [InlineData(Kind.March, 0)]
    [InlineData(Kind.Assault, 4)]
    public void ALongMoveNeverStandsNorReshuffles(Kind k, int stalls)
    {
        var r = Run(Layout.Open, k);
        Assert.InRange(r.Men.Sum(m => m.Stalled), 0, stalls);
        Assert.All(r.Men, m => Assert.InRange(m.Jumps, 0, 1));
        Assert.Equal(0, r.Crossings);
        Assert.All(r.Men, m => Assert.True(m.After <= 0.5, $"#{m.Id} reshuffled {m.After:0.00} cells after arrival"));
    }

    // Round a building, a March keeps to its file and then to the final positions: under 2% of member-ticks stalled (5% before),
    // no crossings, no reshuffle.
    [Fact]
    public void RoundABuildingTheMarchKeepsMoving()
    {
        var r = Run(Layout.Building, Kind.March);
        Assert.True(r.Men.Sum(m => m.Stalled) * 50 < MemberTicks(r), $"{r.Men.Sum(m => m.Stalled)} stalled of {MemberTicks(r)}");
        Assert.Equal(0, r.Crossings);
        Assert.All(r.Men, m => Assert.True(m.After <= 0.75, $"#{m.Id} reshuffled {m.After:0.00} cells after arrival"));
    }

    // Hold far away: its cells are picked at the order (placed while the squad still walks), each member walks to his own once
    // within the direct range (one change of target at most) and stays there: no reshuffle on arrival.
    [Fact]
    public void HoldPicksItsCellsAtTheOrder()
    {
        var (w, s, men) = Setup(Layout.Open, SquadMode.March);
        Give(w, men, Kind.Hold);
        w.Run(5);
        Assert.Equal(SquadState.Moving, s.State);
        Assert.True(s.HoldPlaced, "picked at the order");
        var picks = s.HoldPicks.ToList();
        var r = Measure(w, s, men, Kind.Hold, give: false);
        output.WriteLine(Row(Layout.Open, Kind.Hold, r));
        Assert.Equal(picks, s.HoldPicks.ToList()); // not picked again on arrival
        Assert.True(r.Men.Sum(m => m.Stalled) * 100 < MemberTicks(r));
        Assert.All(r.Men, m => Assert.InRange(m.Jumps, 0, 1));
        Assert.All(r.Men, m => Assert.True(m.After <= 0.5, $"#{m.Id} reshuffled {m.After:0.00} cells after arrival"));
    }

    // An ambush walks to its picked spots without standing (mines laid on orders of their own aside): under 1% of member-ticks
    // stalled (8% before).
    [Fact]
    public void AnAmbushWalksToItsSpots()
    {
        var r = Run(Layout.Open, Kind.Ambush);
        Assert.True(r.Men.Sum(m => m.Stalled) * 100 < MemberTicks(r), $"{r.Men.Sum(m => m.Stalled)} stalled of {MemberTicks(r)}");
    }

    // LeastWays: twelve men in a ragged block sent to a file across their way: the foremost take the foremost places, no two
    // straight ways cross, and the same input gives the same answer.
    [Fact]
    public void LeastWaysNeverCross()
    {
        var from = new Vec[12];
        var to = new Vec[12];
        for (var i = 0; i < 12; i++)
        {
            from[i] = new Vec(Fixed.FromInt(10 + i % 4 * 2 + i * 7 % 3), Fixed.FromInt(10 + i / 4 * 2 + i * 5 % 2));
            to[i] = new Vec(Fixed.FromInt(30 + i * 3 % 7), Fixed.FromInt(4 + i * 2));
        }
        int[] Hand()
        {
            var of = Enumerable.Range(0, 12).ToArray();
            World.LeastWays(12, from, from, i => to[of[i]], (_, _) => true, (a, b) => (of[a], of[b]) = (of[b], of[a]));
            return of;
        }
        var of = Hand();
        Assert.Equal(of, Hand());
        for (var a = 0; a < 12; a++)
        {
            for (var b = a + 1; b < 12; b++)
            {
                Assert.False(World.Crosses(from[a], to[of[a]], from[b], to[of[b]]), $"{a} and {b} cross");
            }
        }
    }

    // The bench: every layout and order, one row each (sums over the 8 members; the worst member for the ratio and angle).
    // SQUAD_MOVE_OUT=<file> writes the table there.
    [Fact]
    public void Bench()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| layout | order | stalled (by a mate) | gait still | target jumps | worst path/straight | worst first angle | walked after arrival, over the way left | crossings | close ticks | ticks |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var l in Enum.GetValues<Layout>())
        {
            foreach (var k in Enum.GetValues<Kind>())
            {
                if (k == Kind.MarchShort && l != Layout.Open)
                {
                    continue;
                }
                var (w, s, men) = Setup(l, ModeOf(k));
                var r = Measure(w, s, men, k);
                sb.AppendLine(Row(l, k, r));
            }
        }
        output.WriteLine(sb.ToString());
        if (Environment.GetEnvironmentVariable("SQUAD_MOVE_OUT") is { Length: > 0 } path)
        {
            File.WriteAllText(path, sb.ToString());
        }
    }
}
