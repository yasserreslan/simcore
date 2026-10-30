using System.Diagnostics;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Multi-target ground attack (MT; docs/design.md, "Multi-target ground attack"): one objective of ordered points and enemy units
// for a whole selection; every point its own shot (a munition, a ripple launcher's volley, a bomber's group), the nearest free unit
// first, the spare shots spread evenly, re-assignment on deaths and dry units, movement and fire through each unit's own
// Attack-ground path, deterministic.
public class MultiTargetTests(ITestOutputHelper output)
{
    private const long Cell1 = Fixed.OneRaw;

    private static MultiAim P(int x, int y) => new(new Cell(x, y));

    private static MultiAim U(Unit t) => new(Cell.Of(t.Pos), t.Id);

    private static void Multi(World w, IEnumerable<Unit> units, params MultiAim[] aims) =>
        w.Issue(new Command
        {
            Tick = w.Tick, Player = units.First().Owner, Kind = CommandKind.MultiTarget, Units = [.. units.Select(u => u.Id)], Aims = aims,
        });

    // The rounds that left (by projectile ID) landing nearest each point of m (within 8 cells), by point index.
    private sealed class Rounds(World w, MultiTarget m)
    {
        private readonly HashSet<uint> _seen = [];
        public readonly int[] At = new int[m.Points.Count];

        public void Take()
        {
            foreach (var p in w.Projectiles)
            {
                if (p.Owner != m.Owner || !_seen.Add(p.Id))
                {
                    continue;
                }
                var best = -1;
                var bd = long.MaxValue;
                for (var i = 0; i < m.Points.Count; i++)
                {
                    var c = m.Points[i].Cell.Center();
                    var d = World.Len3(p.Dest.X.Raw - c.X.Raw, p.Dest.Y.Raw - c.Y.Raw, 0);
                    if (d < bd)
                    {
                        (best, bd) = (i, d);
                    }
                }
                if (best >= 0 && bd <= 8 * Cell1)
                {
                    At[best]++;
                }
            }
        }
    }

    private static Rounds RunRounds(World w, MultiTarget m, int ticks, Func<bool>? until = null)
    {
        var r = new Rounds(w, m);
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            r.Take();
            if (until?.Invoke() == true)
            {
                break;
            }
        }
        return r;
    }

    private static int AmmoOf(Unit u)
    {
        var n = 0;
        foreach (var wd in u.Def.Weapons)
        {
            if (wd.Stock >= 0 && (wd.Effect.Targets & Layers.Ground) != 0)
            {
                n += u.Charges[wd.Stock];
            }
        }
        return n;
    }

    // ---- The order ----

    [Fact]
    public void TheOrderCleansItsAimsAndMakesOneObjective()
    {
        var w = Arena.New(64);
        var a = w.Spawn(1, "mortar", 10, 30, Facing.East);
        var b = w.Spawn(1, "mortar", 10, 34, Facing.East);
        var own = w.Spawn(1, "worker", 30, 10);
        var foe = w.Spawn(2, "technical", 30, 50);
        Multi(w, [a, b], P(30, 30), P(30, 30), P(500, 500), U(own), U(foe), U(foe), P(32, 36));
        w.Step();
        var m = Assert.Single(w.MultiTargets);
        Assert.Equal(1, m.Owner);
        Assert.Equal([new Cell(30, 30), Cell.Of(foe.Pos), new Cell(32, 36)], m.Points.Select(p => p.Cell));
        Assert.Equal([0u, foe.Id, 0u], m.Points.Select(p => p.Target));
        Assert.Equal([a.Id, b.Id], m.Members.Select(x => x.Unit));
        Assert.Same(m, w.MultiTargetOf(a.Id));
        Assert.Null(w.MultiTargetOf(own.Id));
        Assert.Throws<CommandRejectedException>(() => Multi(w, [a], [.. Enumerable.Range(0, World.MaxMultiAims + 1).Select(i => P(i % 60, 1 + i / 60))]));
    }

    [Fact]
    public void EveryPointIsCoveredFirstByTheNearestFreeUnit()
    {
        var w = Arena.New(64);
        // Three mortars in a column, three points level with them: the nearest pairs (no crossing).
        var ms = new[] { w.Spawn(1, "mortar", 10, 20, Facing.East), w.Spawn(1, "mortar", 10, 30, Facing.East), w.Spawn(1, "mortar", 10, 40, Facing.East) };
        Multi(w, ms, P(30, 41), P(30, 19), P(30, 31));
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.Equal([1, 2, 0], m.Members.Select(x => x.Point)); // the mortar at y 20 takes the point at y 19, and so on
        Assert.All(m.Points, p => Assert.Equal(1, p.Assigned));
        Assert.All(ms, u => Assert.Equal(OrderKind.AttackGround, u.Order.Kind));
        Assert.Equal([new Cell(30, 19), new Cell(30, 31), new Cell(30, 41)], ms.Select(u => u.Order.Cell));
    }

    [Fact]
    public void ThePreviewIsTheStartingAssignment()
    {
        var w = Arena.New(64);
        var ms = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "mortar", 10, 16 + 6 * i, Facing.East)).ToList();
        MultiAim[] aims = [P(30, 41), P(30, 19), P(30, 31)];
        var preview = w.PreviewMultiTarget(1, [.. ms.Select(u => u.Id)], aims);
        Multi(w, ms, aims);
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.Equal(preview.OrderBy(p => p.Unit).Select(p => (p.Point, p.Unit)), m.Members.Select(x => (x.Point, x.Unit)));
    }

    [Fact]
    public void MoreUnitsThanPointsSpreadAndAllAttack()
    {
        var w = Arena.New(64);
        var ms = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "mortar", 10, 20 + 4 * i, Facing.East)).ToList();
        Multi(w, ms, P(30, 22), P(30, 34));
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.All(m.Members, x => Assert.True(x.Point >= 0));
        Assert.Equal([3, 2], m.Points.Select(p => p.Assigned)); // 5 over 2 points: 3 and 2, none idle
        RunRounds(w, m, 120);
        Assert.All(ms, u => Assert.NotEqual(0u, u.LastShot)); // everyone fired
        Assert.True(m.Points.All(p => p.Hit));
        Assert.NotEqual(0u, m.CompleteTick);
    }

    [Fact]
    public void MorePointsThanUnitsTakeFurtherPointsInTurnAndSpreadTheSpareShots()
    {
        var w = Arena.New(64);
        var mortar = w.Spawn(1, "mortar", 10, 30, Facing.East);
        Multi(w, [mortar], P(30, 30), P(30, 22), P(30, 38), P(25, 30));
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.Equal(3, m.Members[0].Point); // the nearest first
        var order = new List<int> { m.Members[0].Point };
        var r = new Rounds(w, m);
        for (var t = 0; t < 1200 && w.MultiTargets.Count > 0; t++)
        {
            w.Step();
            r.Take();
            if (w.MultiTargets.Count > 0 && m.Members[0].Point != order[^1])
            {
                order.Add(m.Members[0].Point);
            }
        }
        Assert.Equal(0, AmmoOf(mortar));
        Assert.Empty(w.MultiTargets); // dry: it left, the objective is over
        Assert.All(m.Points, p => Assert.True(p.Hit));
        Assert.Equal(4, order.Take(4).Distinct().Count()); // every point once before any twice
        Assert.Equal(10, m.Points.Sum(p => p.Shots)); // its 10 shells
        Assert.True(m.Points.Max(p => p.Shots) - m.Points.Min(p => p.Shots) <= 1, "spread evenly");
        Assert.Equal(m.Points.Select(p => p.Shots), r.At);
        w.Step();
        Assert.Equal(OrderKind.Idle, mortar.Order.Kind);
    }

    [Fact]
    public void AUnitOutOfRangeMovesIntoRangeFirst()
    {
        var w = Arena.New(160);
        var mortar = w.Spawn(1, "mortar", 10, 60, Facing.East); // its tube reaches 30 cells
        var t = w.SpawnCrewed(1, "longrange_rocket_truck", 10, 70, Facing.East).Vehicle; // 50 to 130 cells
        Multi(w, [mortar, t], P(60, 60), P(2, 72), P(150, 70));
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.Equal(1, m.Members.Single(x => x.Unit == mortar.Id).Point); // (2, 72) is inside the truck's minimum range: the mortar's
        for (var i = 0; i < 4000 && !m.Points.All(p => p.Hit); i++)
        {
            w.Step();
            Assert.NotEqual(1, m.Members.SingleOrDefault(x => x.Unit == t.Id)?.Point ?? 0); // never the truck's
        }
        Assert.All(m.Points, p => Assert.True(p.Hit)); // (60, 60) and (150, 70) beyond the mortar's 30 cells: walked or driven into reach
        Assert.True(mortar.Pos.X.Raw > 25 * Cell1 || t.Pos.X.Raw > 20 * Cell1, "someone moved");
        output.WriteLine($"all hit at tick {m.CompleteTick}: mortar at {Cell.Of(mortar.Pos)}, truck at {Cell.Of(t.Pos)}, shots {string.Join(",", m.Points.Select(p => p.Shots))}");
    }

    // ---- The volley ----

    [Fact]
    public void ARippleLauncherPutsItsWholeVolleyOnOnePoint()
    {
        var w = Arena.New(200);
        var t1 = w.SpawnCrewed(1, "longrange_rocket_truck", 10, 90, Facing.East).Vehicle;
        var t2 = w.SpawnCrewed(1, "longrange_rocket_truck", 10, 110, Facing.East).Vehicle;
        Assert.True(t1.Def.Weapons.Single(x => x.Effect.SalvoSize > 1).Effect.VolleyPerTarget);
        Multi(w, [t1, t2], P(100, 88), P(100, 112));
        w.Step();
        var m = w.MultiTargets.Single();
        var r = RunRounds(w, m, 1500, () => w.MultiTargets.Count == 0);
        Assert.Equal([10, 10], r.At);
        Assert.Equal([1, 1], m.Points.Select(p => p.Shots));
        Assert.Empty(w.MultiTargets); // both dry
    }

    [Fact]
    public void ASplitSalvoGivesEachRoundItsOwnPoint()
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(c => c.Name.EndsWith("components/vehicle_weapons.json", StringComparison.Ordinal));
        sources[i] = new ContentSource(sources[i].Name, sources[i].Json.Replace(
            "\"salvoSize\": 10, \"salvoInterval\": 30, \"volleyPerTarget\": true,", "\"salvoSize\": 10, \"salvoInterval\": 30, \"volleyPerTarget\": false,", StringComparison.Ordinal));
        var content = ContentLoader.Load(sources);
        Assert.NotEqual(TestContent.Mvp.Hash, content.Hash); // off the default: hashed
        var setup = TestContent.Battle(1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = new World(new Grid(200, 200), MatchCompiler.Compile(content, setup.For(content, 1)));
        var t = w.SpawnCrewed(1, "longrange_rocket_truck", 10, 100, Facing.East).Vehicle;
        Multi(w, [t], P(100, 92), P(100, 100), P(100, 108));
        w.Step();
        var m = w.MultiTargets.Single();
        var r = RunRounds(w, m, 1500, () => w.MultiTargets.Count == 0);
        Assert.Equal(10, r.At.Sum()); // (the rounds scatter up to 6 cells: which point each landed nearest is not its aim)
        Assert.Equal([4, 3, 3], m.Points.Select(p => p.Shots).OrderByDescending(x => x)); // one round a point in turn
    }

    [Fact]
    public void TheVolleyFlagDefaultKeepsTheContentHash()
    {
        var sources = TestContent.MvpSources()
            .Select(s => s with { Json = s.Json.Replace(" \"volleyPerTarget\": true,", "", StringComparison.Ordinal) }).ToList();
        Assert.DoesNotContain(sources, s => s.Json.Contains("\"volleyPerTarget\": true", StringComparison.Ordinal));
        var bare = ContentLoader.Load(sources);
        Assert.Equal(TestContent.Mvp.Hash, bare.Hash); // the launchers say what a salvo's default is: the golden replay's content
        Assert.True(bare.Component("longrange_rockets")!.Effects.OfType<WeaponEffect>().Single().VolleyPerTarget);
        Assert.False(bare.Component("howitzer_155")!.Effects.OfType<WeaponEffect>().Single().VolleyPerTarget);
        Assert.False(bare.Component("bombs_500lb")!.Effects.OfType<WeaponEffect>().Single().VolleyPerTarget);
    }

    // ---- Re-assignment ----

    [Fact]
    public void ADeadUnitsPointGoesToTheNextFreeUnit()
    {
        var w = Arena.New(64);
        var a = w.Spawn(1, "mortar", 10, 22, Facing.East);
        var b = w.Spawn(1, "mortar", 10, 38, Facing.East);
        Multi(w, [a, b], P(30, 22), P(30, 38));
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.Equal(1, m.Members.Single(x => x.Unit == b.Id).Point);
        b.HP = 0; // dies before its shot
        var r = RunRounds(w, m, 400, () => m.Points[1].Hit);
        Assert.DoesNotContain(m.Members, x => x.Unit == b.Id);
        Assert.True(m.Points[0].Hit && m.Points[1].Hit, "the survivor takes the dead one's point after its own");
        Assert.True(r.At[1] >= 1);
    }

    [Fact]
    public void AUnitOutOfAmmoLeavesAndItsPointIsTaken()
    {
        var w = Arena.New(64);
        var a = w.Spawn(1, "mortar", 10, 22, Facing.East);
        var b = w.Spawn(1, "mortar", 10, 38, Facing.East);
        var stock = b.Def.Weapons.Single(x => x.Effect.Targets.HasFlag(Layers.Ground) && x.Stock >= 0 && x.Effect.ArcPercent > 0).Stock;
        b.MutableCharges[stock] = 0; // dry before it fires
        a.MutableCharges[stock] = 3;
        Multi(w, [a, b], P(30, 22), P(30, 38), P(30, 30));
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.Equal([a.Id], m.Members.Select(x => x.Unit)); // a dry unit is no member
        RunRounds(w, m, 800, () => w.MultiTargets.Count == 0);
        Assert.All(m.Points, p => Assert.Equal(1, p.Shots));
        Assert.Empty(w.MultiTargets);
    }

    [Fact]
    public void AnotherOrderTakesAUnitOff()
    {
        var w = Arena.New(64);
        var a = w.Spawn(1, "mortar", 10, 22, Facing.East);
        var b = w.Spawn(1, "mortar", 10, 38, Facing.East);
        Multi(w, [a, b], P(30, 22), P(30, 38));
        w.Step();
        var m = w.MultiTargets.Single();
        w.Order(1, CommandKind.Move, [b], cell: new Cell(5, 50));
        w.Order(1, CommandKind.SetStance, [a]); // not an order: it stays (an invalid stance, ignored)
        w.Step();
        Assert.Equal([a.Id], m.Members.Select(x => x.Unit));
        Assert.Equal(OrderKind.Move, b.Order.Kind);
        Assert.Equal(0, m.Points[1].Assigned);
        w.Order(1, CommandKind.Stop, [a]);
        w.Step();
        Assert.Empty(w.MultiTargets);
    }

    // ---- Enemy units ----

    [Fact]
    public void EnemyUnitsMixWithPointsAndAUnitGoneBeforeItsShotIsDone()
    {
        var w = Arena.New(64);
        var ms = Enumerable.Range(0, 2).Select(i => w.Spawn(1, "mortar", 10, 26 + 8 * i, Facing.East)).ToList();
        var tough = w.SpawnCrewed(2, "technical", 30, 26).Vehicle;
        tough.HP = 100_000;
        var doomed = w.SpawnCrewed(2, "technical", 34, 44).Vehicle;
        Multi(w, ms, U(tough), P(30, 34), U(doomed));
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.Equal(OrderKind.Attack, ms[0].Order.Kind);
        Assert.Equal(tough.Id, ms[0].Order.Target);
        doomed.HP = 0;
        RunRounds(w, m, 600, () => m.Done == 3 && m.Points[0].Hit && m.Points[1].Hit);
        Assert.True(m.Points[0].Hit, "the technical took its shell");
        Assert.True(m.Points[1].Hit);
        Assert.True(m.Points[2].Gone);
        Assert.False(m.Points[2].Hit);
        Assert.Equal(3, m.Done);
        Assert.NotEqual(0u, m.CompleteTick);
    }

    // ---- Unit kinds ----

    [Fact]
    public void AnImmobileGunTakesOnlyPointsInItsRange()
    {
        var w = Arena.New(128);
        var gun = w.Spawn(1, "howitzer", 5, 55); // its centre (10, 60)
        World.Board(w.Spawn(1, "Rifleman", 10, 66), gun);
        var mortar = w.Spawn(1, "mortar", 85, 60, Facing.East);
        Multi(w, [gun, mortar], P(60, 60), P(110, 60)); // 110 is 100 cells from the gun: beyond its 80
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.Equal(0, m.Members.Single(x => x.Unit == gun.Id).Point);
        Assert.Equal(1, m.Members.Single(x => x.Unit == mortar.Id).Point);
        for (var t = 0; t < 1200 && !m.Points.All(p => p.Shots >= 2); t++)
        {
            w.Step();
            Assert.Equal(0, m.Members.SingleOrDefault(x => x.Unit == gun.Id)?.Point ?? 0); // never the point beyond its reach
        }
        Assert.True(m.Points[0].Shots >= 2 && m.Points[1].Shots >= 2, "each keeps firing at what it can reach");
    }

    [Fact]
    public void AJetBombsEachPointOnItsOwnPass()
    {
        var w = Arena.NewSentinel(320);
        var jet = w.Spawn(1, "Multirole 500 lb", 40, 160, Facing.East);
        w.Step();
        Multi(w, [jet], P(200, 150), P(215, 170), P(230, 150), P(245, 170));
        w.Step();
        var m = w.MultiTargets.Single();
        var landings = new List<Vec>();
        for (var t = 0; t < 6000 && !m.Points.All(p => p.Hit); t++)
        {
            w.Step();
            landings.AddRange(w.BombReleases.Where(r => r.Aircraft == jet.Id).Select(r => r.Predicted));
        }
        Assert.Equal(4, landings.Count);
        Assert.All(m.Points, p => Assert.Equal(1, p.Shots));
        foreach (var p in m.Points)
        {
            var c = p.Cell.Center();
            Assert.Contains(landings, l => World.Len3(l.X.Raw - c.X.Raw, l.Y.Raw - c.Y.Raw, 0) < 4 * Cell1);
        }
        output.WriteLine($"4 points bombed by tick {m.CompleteTick} (order at {m.Since})");
    }

    [Fact]
    public void ABomberInACarpetModeDropsASalvoGroupOnEachPoint()
    {
        var w = Arena.NewSentinel(400);
        var b = w.Spawn(1, "strategic_bomber", 30, 200, Facing.East);
        w.Step();
        Assert.Equal(0, b.FireMode); // carpet20
        Multi(w, [b], P(250, 170), P(250, 230));
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.Equal(1, b.FireMode); // salvo4 while on the objective
        var landings = new List<Vec>();
        for (var t = 0; t < 8000 && !m.Points.All(p => p.Hit); t++)
        {
            w.Step();
            landings.AddRange(w.BombReleases.Where(r => r.Aircraft == b.Id).Select(r => r.Predicted));
        }
        Assert.All(m.Points, p => Assert.Equal(1, p.Shots));
        foreach (var p in m.Points)
        {
            var c = p.Cell.Center();
            Assert.Equal(4, landings.Count(l => World.Len3(l.X.Raw - c.X.Raw, l.Y.Raw - c.Y.Raw, 0) < 12 * Cell1)); // a group of 4 each
        }
        w.Order(1, CommandKind.Stop, [b]);
        w.Step();
        Assert.Equal(0, b.FireMode); // its own mode back
    }

    [Fact]
    public void AHelicopterAndGroundUnitsShareTheObjective()
    {
        var w = Arena.NewSentinel(128);
        var heli = w.Spawn(1, "attack_helicopter", 20, 60, Facing.East);
        var tank = w.SpawnCrewed(1, "main_battle_tank", 20, 70, Facing.East).Vehicle;
        w.Step();
        Multi(w, [heli, tank], P(45, 50), P(40, 72), P(60, 60));
        w.Step();
        var m = w.MultiTargets.Single();
        Assert.Equal(2, m.Members.Count);
        RunRounds(w, m, 2000, () => m.CompleteTick != 0);
        Assert.NotEqual(0u, m.CompleteTick);
        Assert.All(m.Points, p => Assert.True(p.Hit));
    }

    // ---- Determinism and cost ----

    private static List<ulong> MixedRun(ulong seed)
    {
        var w = Arena.NewSentinel(320, seed);
        var units = new List<Unit>
        {
            w.Spawn(1, "Multirole 500 lb", 40, 150, Facing.East),
            w.Spawn(1, "Multirole 500 lb", 40, 170, Facing.East),
            w.Spawn(1, "attack_helicopter", 150, 160, Facing.East),
            w.SpawnCrewed(1, "main_battle_tank", 170, 150, Facing.East).Vehicle,
            w.SpawnCrewed(1, "main_battle_tank", 170, 170, Facing.East).Vehicle,
        };
        var foe = w.SpawnCrewed(2, "technical", 192, 175).Vehicle;
        w.Step();
        Multi(w, units, P(190, 150), P(195, 165), U(foe), P(200, 140), P(185, 160));
        var hashes = new List<ulong>();
        for (var t = 0; t < 2400; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
            if (t % 600 == 0)
            {
                Assert.Equal(w.HashReference(), w.Hash());
            }
        }
        Assert.True(w.Units.Count(u => u.Owner == 1 && u.Def.Archetype.Flight is not null) >= 1);
        return hashes;
    }

    [Fact]
    public void TheSameOrderGivesTheSameHashOnTwoRuns()
    {
        var a = MixedRun(7);
        var b = MixedRun(7);
        Assert.Equal(a, b);
    }

    // The phase times of one step (the commands' phase holds the order's assignment).
    private sealed class Phases : IStepProbe
    {
        private readonly Stopwatch _sw = new();
        public double Commands;

        public void Begin() => _sw.Restart();

        public void After(StepPhase phase)
        {
            if (phase == StepPhase.Commands)
            {
                Commands = _sw.Elapsed.TotalMilliseconds;
            }
        }
    }

    private static (World W, List<Unit> Units, MultiAim[] Aims) FiftyByFifty()
    {
        var w = Arena.New(256);
        var units = new List<Unit>();
        for (var i = 0; i < 50; i++)
        {
            units.Add(w.Spawn(1, "mortar", 20 + (i % 10) * 4, 60 + (i / 10) * 30, Facing.East));
        }
        var aims = Enumerable.Range(0, 50).Select(i => P(50 + (i % 10) * 3, 55 + (i / 10) * 30)).ToArray();
        w.Step();
        return (w, units, aims);
    }

    [Fact]
    public void FiftyUnitsOnFiftyPointsAreCheap()
    {
        // Warm up the code paths (the JIT) on a first world, then time a second.
        var (w0, u0, a0) = FiftyByFifty();
        Multi(w0, u0, a0);
        w0.Run(5);
        // The baseline: the same 50 units given one Attack-ground each, to the point the objective would give them.
        var (wb, ub, ab) = FiftyByFifty();
        var plan = wb.PreviewMultiTarget(1, [.. ub.Select(u => u.Id)], ab);
        foreach (var (pt, unit) in plan)
        {
            wb.Order(1, CommandKind.AttackGround, [wb.Unit(unit)!], cell: ab[pt].Cell);
        }
        var pb = new Phases();
        wb.Probe = pb;
        var swb = Stopwatch.StartNew();
        wb.Step();
        var plain = swb.Elapsed.TotalMilliseconds;
        var (w, units, aims) = FiftyByFifty();
        Multi(w, units, aims);
        var probe = new Phases();
        w.Probe = probe;
        var sw = Stopwatch.StartNew();
        w.Step(); // the order: 50 x 50 assigned
        var order = sw.Elapsed.TotalMilliseconds;
        w.Probe = null;
        output.WriteLine($"the order's commands phase {probe.Commands:F2} ms (50 plain Attack-ground orders: {pb.Commands:F2} ms); its whole tick {order:F2} ms (plain: {plain:F2} ms, the first routes)");
        var m = w.MultiTargets.Single();
        Assert.Equal(50, m.Members.Count);
        Assert.All(m.Points, p => Assert.Equal(1, p.Assigned));
        var ticks = new List<double>();
        for (var t = 0; t < 600; t++)
        {
            sw.Restart();
            w.Step();
            ticks.Add(sw.Elapsed.TotalMilliseconds);
        }
        ticks.Sort();
        output.WriteLine($"order tick {order:F2} ms; ticks after: median {ticks[300]:F3} ms, p99 {ticks[594]:F3} ms; done {m.Done}/50");
        Assert.True(probe.Commands < 100, $"the order's commands phase took {probe.Commands:F1} ms");
    }
}
