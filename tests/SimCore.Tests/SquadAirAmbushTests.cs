using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Infantry squads, phase S4 (docs/design.md, "Squads: Air ambush"): a squad mostly of Iglas given an Ambush sets it round an air
// corridor: the Iglas spread along it under trees and never closer than 8 cells, the riflemen guarding them toward the ground approach;
// it springs on an aircraft within reach of 2 Iglas, a member hurt or the Fire order; the Iglas fire together (a coordinated launch,
// which flares beat less often than single launches); corridor lengths outside 20 to 120 cells are refused; a jet run over a set air
// ambush; determinism and replays.
public class SquadAirAmbushTests(ITestOutputHelper output)
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int Size = 240;

    // The corridor: west to east along row 120, 80 cells; aircraft come from the west. The likely ground approach is west of its start.
    private static readonly Cell Start = new(70, 120);
    private static readonly Cell End = new(150, 120);

    private static void Ambush(World w, byte player, IEnumerable<Unit> units, Cell a, Cell? b) =>
        w.Issue(new Command
        {
            Tick = w.Tick, Player = player, Kind = CommandKind.SetSquadMode, Units = [.. units.Select(u => u.Id)], Index = (int)SquadMode.Ambush,
            Cell = a, Cell2 = b,
        });

    private static void Fire(World w, Unit member) =>
        w.Issue(new Command { Tick = w.Tick, Player = member.Owner, Kind = CommandKind.SpringAmbush, Units = [member.Id] });

    private static Squad Form(World w, IEnumerable<Unit> units)
    {
        var list = units.ToList();
        w.Issue(new Command { Tick = w.Tick, Player = list[0].Owner, Kind = CommandKind.FormSquad, Units = [.. list.Select(u => u.Id)] });
        w.Run(1);
        return w.SquadOf(list[0].Id) ?? throw new InvalidOperationException("no squad formed");
    }

    private static void RunUntil(World w, Squad s, SquadState state, int limit = 2000)
    {
        for (var t = 0; t < limit && s.State != state; t++)
        {
            w.Run(1);
        }
        Assert.Equal(state, s.State);
    }

    private static void Placed(World w, Squad s, int limit = 200)
    {
        for (var t = 0; t < limit && !s.HoldPlaced; t++)
        {
            w.Run(1);
        }
        Assert.True(s.HoldPlaced, "it picked its positions");
    }

    // Woods: 0 none, 1 dense forest in a band along the corridor (rows 112 to 128, columns 50 to 170), 2 groves in patches (4 x 4
    // every 10 cells along the band).
    private static World AirMap(int woods, ulong seed = 1)
    {
        var w = Arena.NewSentinel(Size, seed);
        var corners = new int[(Size + 1) * (Size + 1)];
        var flags = new byte[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var band = x is >= 50 and <= 170 && y is >= 112 and <= 128;
                flags[y * Size + x] = woods switch
                {
                    1 when band => 3,
                    2 when band && x % 10 < 4 && y % 10 < 4 => 2,
                    _ => 0,
                };
            }
        }
        w.AttachTerrain(new TerrainMap(Size, Size, corners, flags, Rules));
        return w;
    }

    // Iglas (player 2, the Resistance's AA soldiers) and riflemen in a block near the corridor's middle.
    private static List<Unit> AirSquad(World w, int iglas, int riflemen, int x = 106, int y = 134)
    {
        var men = new List<Unit>();
        for (var i = 0; i < iglas; i++)
        {
            men.Add(w.Spawn(2, "manpads", x + i % 5, y + i / 5));
        }
        for (var i = 0; i < riflemen; i++)
        {
            men.Add(w.Spawn(2, "Rifleman", x + i % 5, y + 2 + i / 5));
        }
        return men;
    }

    private static Cell PickOf(World w, Unit u) => w.SquadHoldPick(u.Id)!.Value.Cell;

    private static double Dist(Cell a, Cell b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    private static string Dump(World w, Squad s) =>
        string.Join("; ", s.HoldPicks.Select(p => $"{w.SquadRoleOf(w.Unit(p.Unit)!)} {p.Unit} {p.Cell} f{w.Ground!.Forest(p.Cell)}"));

    private static void AssertNeverBunched(World w, IReadOnlyList<Cell> cells, string dump)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            for (var j = i + 1; j < cells.Count; j++)
            {
                Assert.True(Dist(cells[i], cells[j]) >= 8, $"Iglas {cells[i]} and {cells[j]} {Dist(cells[i], cells[j]):0.0} cells apart: {dump}");
            }
        }
    }

    // A squad mostly of Iglas sets an air ambush: the Iglas spread along the corridor 8 to 15 cells apart, never closer than 8, each
    // under trees, all within reach of the corridor; a squad of riflemen given the same order sets a ground one (and is refused: its
    // zone is at most 30 cells).
    [Theory]
    [InlineData(4, 2, 1)]
    [InlineData(6, 2, 1)]
    [InlineData(6, 4, 2)]
    [InlineData(10, 0, 1)]
    public void IglasSpreadUnderTreesAndNeverBunch(int iglas, int riflemen, int woods)
    {
        var w = AirMap(woods);
        var men = AirSquad(w, iglas, riflemen);
        var s = Form(w, men);
        Assert.True(w.AirAmbushSquad(s.Id));
        Ambush(w, 2, [men[0]], Start, End);
        w.Run(1);
        Assert.Equal(SquadMode.Ambush, s.Mode);
        Assert.True(s.AmbushAir);
        Placed(w, s);
        var dump = Dump(w, s);
        var cells = men.Take(iglas).Select(u => PickOf(w, u)).ToList();
        AssertNeverBunched(w, cells, dump);
        var along = cells.OrderBy(c => c.X).ToList();
        for (var i = 1; i < along.Count; i++)
        {
            Assert.True(Dist(along[i - 1], along[i]) <= 15 + 2 * 4, $"spread along the corridor, not strung out: {dump}");
        }
        Assert.All(cells, c => Assert.InRange(Math.Abs(c.Y - Start.Y), 0, 8)); // near the corridor's line: well within reach of it
        var trees = cells.Count(c => w.Ground!.Forest(c) >= 2);
        if (woods == 1)
        {
            Assert.True(trees == iglas, $"every Igla under trees: {dump}");
        }
        else
        {
            Assert.True(trees >= iglas - 1, $"the Iglas under the groves where there are some: {trees} of {iglas}: {dump}");
        }
        Assert.Empty(s.AmbushTasks); // no mines in an air ambush
        Assert.Equal(0, s.AmbushSide);
        RunUntil(w, s, SquadState.Ready);
        Assert.All(men, u => Assert.Equal(Stance.HoldFire, u.Stance));
        foreach (var u in men)
        {
            Assert.True(World.Within(u.Pos, PickOf(w, u).Center(), Fixed.One), $"{u.Id} at {u.Pos}, his position {PickOf(w, u)}");
        }
    }

    // The 8-cell rule holds across squads: two Igla squads ambushing one corridor keep 8 cells between all their Iglas; on a short
    // corridor the Iglas spread past its ends rather than closer.
    [Fact]
    public void TwoSquadsOnOneCorridorNeverBunch()
    {
        var w = AirMap(1);
        var a = AirSquad(w, 5, 1, 100, 134);
        var b = AirSquad(w, 5, 1, 100, 140);
        var sa = Form(w, a);
        var sb = Form(w, b);
        Ambush(w, 2, [a[0]], new Cell(100, 120), new Cell(120, 120));
        w.Run(1);
        Ambush(w, 2, [b[0]], new Cell(100, 120), new Cell(120, 120));
        Placed(w, sa);
        Placed(w, sb);
        var cells = a.Take(5).Concat(b.Take(5)).Select(u => PickOf(w, u)).ToList();
        AssertNeverBunched(w, cells, Dump(w, sa) + " | " + Dump(w, sb));
    }

    // Riflemen guard the Iglas: each 2 to 5 cells from an Igla and nearer the ground approach (west of the corridor's start) than it.
    [Fact]
    public void RiflemenGuardTheIglas()
    {
        var w = AirMap(1);
        var men = AirSquad(w, 4, 3);
        var s = Form(w, men);
        Ambush(w, 2, [men[0]], Start, End);
        Placed(w, s);
        var dump = Dump(w, s);
        var iglas = men.Take(4).Select(u => PickOf(w, u)).ToList();
        var approach = new Cell(Start.X - 10, Start.Y);
        foreach (var r in men.Skip(4))
        {
            var c = PickOf(w, r);
            var guarded = iglas.Where(i => Dist(i, c) is >= 2 and <= 5.01 && Dist(c, approach) < Dist(i, approach)).ToList();
            Assert.True(guarded.Count >= 1, $"rifleman {r.Id} at {c} guards an Igla toward the approach: {dump}");
        }
        // The first guard stands by the Igla nearest the approach.
        var first = iglas.MinBy(i => Dist(i, approach));
        Assert.True(Dist(PickOf(w, men[4]), first) <= 5.01, $"the first guard by the Igla nearest the approach: {dump}");
    }

    // A jet of player 1 flying east along row y from x, on a Move to the far east.
    private static Unit JetRun(World w, int x, int y, string jet = "Multirole 500 lb")
    {
        var j = w.Spawn(1, jet, x, y, Facing.East);
        w.Order(1, CommandKind.Move, [j], cell: new Cell(Size - 10, y));
        return j;
    }

    private static (World W, Squad S, List<Unit> Men) ReadyAmbush(int iglas = 4, int riflemen = 2, ulong seed = 1)
    {
        var w = AirMap(1, seed);
        var men = AirSquad(w, iglas, riflemen);
        var s = Form(w, men);
        Ambush(w, 2, [men[0]], Start, End);
        w.Run(1);
        RunUntil(w, s, SquadState.Ready);
        return (w, s, men);
    }

    // Each trigger springs a set air ambush: an aircraft within reach of 2 Iglas, a member hurt, the Fire order.
    [Theory]
    [InlineData(AmbushTrigger.Air)]
    [InlineData(AmbushTrigger.Hurt)]
    [InlineData(AmbushTrigger.Order)]
    public void EachTriggerSpringsIt(AmbushTrigger trigger)
    {
        var (w, s, men) = ReadyAmbush();
        w.Run(40);
        Assert.Equal(SquadState.Ready, s.State);
        switch (trigger)
        {
            case AmbushTrigger.Air:
                JetRun(w, 10, 120);
                break;
            case AmbushTrigger.Hurt:
                w.Hurt(men[1], w.Content.Warhead("explosive")!, 5, 100);
                break;
            case AmbushTrigger.Order:
                Fire(w, men[2]);
                break;
        }
        for (var t = 0; t < 400 && s.State != SquadState.Sprung; t++)
        {
            w.Run(1);
        }
        Assert.Equal(SquadState.Sprung, s.State);
        Assert.Equal(trigger, s.AmbushTrigger);
        Assert.All(men, u => Assert.Equal(Stance.Guard, u.Stance));
    }

    // One Igla reaching an aircraft is not enough: with every other Igla out of missiles the jet flies through without springing it.
    [Fact]
    public void OneIglaInReachDoesNotSpringIt()
    {
        var (w, s, men) = ReadyAmbush();
        foreach (var u in men.Take(4).Skip(1))
        {
            for (var i = 0; i < u.MutableCharges.Length; i++)
            {
                u.MutableCharges[i] = 0;
            }
        }
        var jet = JetRun(w, 10, 120);
        var near = false;
        for (var t = 0; t < 500; t++)
        {
            w.Run(1);
            near |= jet.Alive && (jet.Pos - men[0].Pos).Len() < Fixed.FromInt(100);
            Assert.Equal(SquadState.Ready, s.State);
        }
        Assert.True(near, "the jet came within the Igla's reach");
    }

    // One missile at the jet: its kill chance (permille, after flares) and roll's outcome, whether its fuse went off by the jet (a
    // miss or a hit, not an overshoot), the share of its chance the flares left it (100: none dispensed at it), the launch tick.
    private readonly record struct Shot(uint Launch, int Pk, bool Fused, bool Hit, int Decoy);

    // Measure runs one jet pass over 4 Iglas set as an air ambush (volley) or the same Iglas at the same cells loose in Guard (single:
    // one missile at the jet at a time, the default rule), and returns every missile fired at the jet and whether it came down.
    private static (List<Shot> Shots, bool Down) Measure(bool volley, ulong seed, int yOff, List<Cell>? cells)
    {
        var w = AirMap(1, seed);
        if (volley)
        {
            var men = AirSquad(w, 4, 0);
            var s = Form(w, men);
            Ambush(w, 2, [men[0]], Start, End);
            w.Run(1);
            RunUntil(w, s, SquadState.Ready);
        }
        else
        {
            foreach (var c in cells!)
            {
                w.Spawn(2, "manpads", c.X, c.Y).Stance = Stance.Guard;
            }
        }
        var jet = JetRun(w, 10, 120 + yOff);
        var flying = new SortedDictionary<uint, (Projectile P, uint Launch)>();
        var shots = new List<(uint Id, Shot Shot)>();
        for (var t = 0; t < 700 && jet.Alive; t++)
        {
            w.Run(1);
            foreach (var p in w.Projectiles)
            {
                if (p.Weapon.Effect.Interceptor is not null && p.Target == jet.Id && p.Owner == 2 && !flying.ContainsKey(p.Id) && shots.All(x => x.Id != p.Id))
                {
                    flying[p.Id] = (p, w.Tick);
                }
            }
            foreach (var b in w.AirBursts)
            {
                if (flying.Remove(b.Missile, out var f))
                {
                    var fused = b.Kind is AirBurstKind.Hit or AirBurstKind.Kill or AirBurstKind.Miss;
                    shots.Add((b.Missile, new Shot(f.Launch, b.KillPermille, fused, b.Kind is AirBurstKind.Hit or AirBurstKind.Kill, f.P.DecoyPercent)));
                }
            }
        }
        return ([.. shots.OrderBy(x => x.Id).Select(x => x.Shot)], !jet.Alive);
    }

    // The Iglas fire together: sprung by a jet, its Iglas launch in one tick (a Volley event), so several missiles fly at the jet at
    // once and its flares and its break face them together; over seeds the coordinated volley's missiles keep more of their kill
    // chance through the flares (the break covers one missile) and bring the jet down more often than the same Iglas firing singly.
    [Fact]
    public void TheIglasFireTogetherAndFlaresBeatSinglesMoreOften()
    {
        var (w0, s0, men0) = ReadyAmbush(4, 0);
        var cells = men0.Select(u => PickOf(w0, u)).ToList();
        JetRun(w0, 10, 120);
        SquadEvent? volley = null;
        for (var t = 0; t < 600 && volley is null; t++)
        {
            w0.Run(1);
            volley = w0.SquadEvents.FirstOrDefault(e => e.Kind == SquadEventKind.Volley && e.Squad == s0.Id) is { Squad: not 0 } e ? e : null;
            if (volley is not null)
            {
                var inFlight = w0.Projectiles.Count(p => p.Weapon.Effect.Interceptor is not null && p.Target == volley.Value.Unit && p.Owner == 2);
                Assert.True(inFlight >= 2, $"{inFlight} missiles in the air together");
            }
        }
        Assert.True(volley is { Other: >= 2 }, $"a volley of 2 or more: {volley}");

        var v = new List<Shot>();
        var sg = new List<Shot>();
        int vDown = 0, sDown = 0;
        double vFirst = 0, sFirst = 0;
        const int seeds = 12;
        for (var k = 0; k < seeds; k++)
        {
            var seed = (ulong)(100 + k);
            var yOff = k % 4 * 3 - 4;
            var (vs, vd) = Measure(true, seed, yOff, null);
            var (ss, sd) = Measure(false, seed, yOff, cells);
            v.AddRange(vs);
            sg.AddRange(ss);
            vDown += vd ? 1 : 0;
            sDown += sd ? 1 : 0;
            // The first engagement: the first volley's missiles against the first single missile (the chance the jet is hit at all).
            if (vs.Count > 0)
            {
                vFirst += 1 - vs.Where(m => m.Launch == vs[0].Launch).Aggregate(1.0, (a, m) => a * (1 - m.Pk / 1000.0));
            }
            if (ss.Count > 0)
            {
                sFirst += ss[0].Pk / 1000.0;
            }
        }
        string Line(string name, List<Shot> m, int down, double first) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{name}: {m.Count} missiles, {m.Count(x => x.Fused)} fused by the jet; flares left {m.Where(x => x.Fused).DefaultIfEmpty().Average(x => x.Decoy):0.0}% of their chance; kill chance {m.Average(x => x.Pk) / 10:0.00}% a missile ({m.Where(x => x.Fused).DefaultIfEmpty().Average(x => x.Pk) / 10:0.00}% fused); {m.Count(x => x.Hit)} hits; first engagement {first / seeds * 100:0.00}%; jet down {down} of {seeds}");
        output.WriteLine(Line("volley", v, vDown, vFirst));
        output.WriteLine(Line("single", sg, sDown, sFirst));
        Assert.True(v.Count > 0 && sg.Count > 0);
        var vDecoy = v.Where(x => x.Fused).Average(x => x.Decoy);
        var sDecoy = sg.Where(x => x.Fused).Average(x => x.Decoy);
        Assert.True(sDecoy < vDecoy, $"flares beat single launches more often: they leave singles {sDecoy:0.0}% of their chance, a volley's {vDecoy:0.0}%");
        Assert.True(vFirst > sFirst, $"the first volley is likelier to hit than a single launch: {vFirst / seeds:0.000} vs {sFirst / seeds:0.000}");
    }

    // A jet flies down the corridor over a set air ambush: the Iglas stay hidden (hold fire under the trees, not spotted from the air)
    // until it comes within reach of two of them, then the trap springs on it, they fire together, and nobody leaves his position.
    [Fact]
    public void AJetRunOverASetAirAmbush()
    {
        var (w, s, men) = ReadyAmbush(6, 2);
        var picks = men.ToDictionary(u => u.Id, u => PickOf(w, u));
        var jet = JetRun(w, 10, 120);
        var volleys = 0;
        var seenReady = false;
        for (var t = 0; t < 700; t++)
        {
            w.Run(1);
            if (s.State == SquadState.Ready)
            {
                Assert.All(men.Take(6), u => Assert.Equal(0u, u.AirTarget)); // holding fire: nobody tracks it yet
                Assert.All(men.Take(6), u => Assert.Equal(0u, u.SpottedBy & (1u << 1))); // hidden from the jet under the trees
                seenReady = true;
            }
            volleys += w.SquadEvents.Count(e => e.Kind == SquadEventKind.Volley && e.Squad == s.Id);
            foreach (var u in men.Where(u => u.Alive))
            {
                Assert.True(World.Within(u.Pos, picks[u.Id].Center(), Fixed.FromInt(2)), $"{u.Id} at {u.Pos}, his spot {picks[u.Id]} (tick {w.Tick})");
            }
        }
        Assert.True(seenReady);
        Assert.Equal(AmbushTrigger.Air, s.AmbushTrigger);
        Assert.True(volleys >= 1, "they fired together");
        output.WriteLine($"jet {(jet.Alive ? $"alive, {jet.HP} HP" : "down")}, {volleys} volleys");
    }

    // A corridor shorter than 20 cells or longer than 120 is refused for an Igla squad; 20 and 120 are taken. A riflemen squad keeps
    // the ground zone's 6 to 30.
    [Theory]
    [InlineData(19, true, false)]
    [InlineData(20, true, true)]
    [InlineData(120, true, true)]
    [InlineData(121, true, false)]
    [InlineData(40, false, false)]
    [InlineData(30, false, true)]
    public void CorridorLengthsOutsideTheLimitsAreRefused(int length, bool iglas, bool taken)
    {
        var w = Arena.NewSentinel(Size);
        var men = iglas ? AirSquad(w, 3, 1, 30, 40) : new List<Unit> { w.Spawn(2, "Rifleman", 30, 40), w.Spawn(2, "Rifleman", 31, 40) };
        var s = Form(w, men);
        Assert.Equal(iglas, w.AirAmbushSquad(s.Id));
        Ambush(w, 2, [men[0]], new Cell(10, 30), new Cell(10 + length, 30));
        w.Run(1);
        if (taken)
        {
            Assert.Equal(SquadMode.Ambush, s.Mode);
            Assert.Equal(iglas, s.AmbushAir);
            Assert.DoesNotContain(w.OrderRefusals, r => r.Reason == OrderRefusalReason.ZoneLength);
            return;
        }
        Assert.Equal(SquadMode.March, s.Mode);
        var refusal = Assert.Single(w.OrderRefusals, r => r.Reason == OrderRefusalReason.ZoneLength);
        Assert.Equal(s.Leader, refusal.Aircraft);
    }

    // Half Iglas is not mostly: a squad of 2 Iglas and 2 riflemen sets a ground ambush.
    [Fact]
    public void HalfIglasIsAGroundAmbush()
    {
        var w = AirMap(1);
        var men = AirSquad(w, 2, 2);
        var s = Form(w, men);
        Assert.False(w.AirAmbushSquad(s.Id));
        Ambush(w, 2, [men[0]], new Cell(100, 120), new Cell(120, 120));
        w.Run(1);
        Assert.Equal(SquadMode.Ambush, s.Mode);
        Assert.False(s.AmbushAir);
    }

    // The same air ambush gives the same positions and hashes on two runs (with a jet over it); the hash agrees with the reference.
    [Fact]
    public void Deterministic()
    {
        static (ulong, List<HoldPick>) Play()
        {
            var w = AirMap(2);
            var men = AirSquad(w, 6, 2);
            var s = Form(w, men);
            Ambush(w, 2, [men[0]], Start, End);
            for (var i = 0; i < 700; i++)
            {
                if (i == 300)
                {
                    JetRun(w, 10, 120);
                }
                w.Run(1);
                Assert.Equal(w.Hash(), w.HashReference());
            }
            return (w.Hash(), s.HoldPicks.ToList());
        }
        var (h1, p1) = Play();
        var (h2, p2) = Play();
        Assert.Equal(h1, h2);
        Assert.Equal(p1, p2);
        Assert.NotEmpty(p1);
    }

    // Replays store the air ambush order (its corridor) and Fire; played again on the same soldiers they give the same hashes.
    [Fact]
    public void ReplayRoundTrip()
    {
        var setup = TestContent.Sentinel(7);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var content = TestContent.Mvp;
        var live = new World(new Grid(Size, Size), MatchCompiler.Compile(content, setup));
        var men = AirSquad(live, 4, 2, 60, 60);
        var issued = new List<Command>
        {
            live.Issue(new Command { Tick = 2, Player = 2, Kind = CommandKind.FormSquad, Units = [.. men.Select(u => u.Id)] }),
            live.Issue(new Command { Tick = 5, Player = 2, Kind = CommandKind.SetSquadMode, Units = [men[0].Id], Index = 3, Cell = new Cell(40, 50), Cell2 = new Cell(100, 50) }),
            live.Issue(new Command { Tick = 300, Player = 2, Kind = CommandKind.SpringAmbush, Units = [men[1].Id] }),
        };
        var hashes = new List<ulong>();
        while (live.Tick < 400)
        {
            live.Step();
            hashes.Add(live.Hash());
        }
        Assert.True(live.SquadOf(men[0].Id) is { AmbushAir: true, State: SquadState.Sprung, AmbushTrigger: AmbushTrigger.Order });
        // Played again from the commands (the spawned soldiers spawned the same way first).
        var again = new World(new Grid(Size, Size), MatchCompiler.Compile(content, setup));
        AirSquad(again, 4, 2, 60, 60);
        foreach (var c in issued)
        {
            again.Issue(c);
        }
        var replayed = new List<ulong>();
        while (again.Tick < 400)
        {
            again.Step();
            replayed.Add(again.Hash());
        }
        Assert.Equal(hashes, replayed);
        var rec = new Replay { Setup = setup, Ticks = 400, Commands = issued };
        using var buf = new MemoryStream();
        rec.Encode(buf);
        buf.Position = 0;
        var loaded = Replay.Decode(buf);
        Assert.Equal(issued.Select(c => (c.Kind, c.Index, c.Cell, c.Cell2)), loaded.Commands.Select(c => (c.Kind, c.Index, c.Cell, c.Cell2)));
        // The decoded commands play back to the same hashes too.
        var decoded = new World(new Grid(Size, Size), MatchCompiler.Compile(content, setup));
        AirSquad(decoded, 4, 2, 60, 60);
        foreach (var c in loaded.Commands)
        {
            decoded.Issue(c);
        }
        var fromFile = new List<ulong>();
        while (decoded.Tick < 400)
        {
            decoded.Step();
            fromFile.Add(decoded.Hash());
        }
        Assert.Equal(hashes, fromFile);
    }

    // Content: the air part is optional (without it every ambush is a ground one), its numbers are hashed, bad weights are refused.
    [Fact]
    public void TheAirBlockIsContent()
    {
        static List<ContentSource> Edit(Func<string, string> f) =>
            [.. TestContent.MvpSources().Select(x => x.Name == "squad.json" ? x with { Json = f(x.Json) } : x)];
        var json = TestContent.MvpSources().Single(x => x.Name == "squad.json").Json;
        var at = json.IndexOf("      // The air ambush (phase S4)", StringComparison.Ordinal);
        var noAir = ContentLoader.Load(Edit(j => j[..at].TrimEnd().TrimEnd(',') + "\n    }\n  }\n}\n"));
        Assert.Null(noAir.Squad!.Ambush!.Air);
        Assert.NotEqual(noAir.Hash, TestContent.Mvp.Hash);
        var tuned = ContentLoader.Load(Edit(j => j.Replace("\"volleyWait\": 20", "\"volleyWait\": 30", StringComparison.Ordinal)));
        Assert.NotEqual(tuned.Hash, TestContent.Mvp.Hash);
        var bad = Edit(j => j.Replace("\"airGuard\": [0, 8, 3, 2]", "\"airGuard\": [0, 8, 3]", StringComparison.Ordinal));
        Assert.Contains("airGuard", Assert.Throws<ContentException>(() => ContentLoader.Load(bad)).Message);
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(noAir, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = new World(new Grid(Size, Size), MatchCompiler.Compile(noAir, setup));
        var men = AirSquad(w, 3, 1, 30, 40);
        var s = Form(w, men);
        Assert.False(w.AirAmbushSquad(s.Id));
        Ambush(w, 2, [men[0]], new Cell(10, 30), new Cell(30, 30));
        w.Run(2);
        Assert.Equal(SquadMode.Ambush, s.Mode);
        Assert.False(s.AmbushAir);
    }
}
