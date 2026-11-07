using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The cluster missile truck (cluster_truck; docs/design.md, "Cluster missile truck"; owner 2026-10-27, 2026-11-07): one missile that
// opens 60 cells over its point and rains composed shells over an ellipse. Air defence engages it only before the burst; the shells
// are never targets. The balance table (TheSeededBalanceTable) measures real blasts over many seeded bursts.
public class ClusterMissileTests(ITestOutputHelper output)
{
    private static WeaponEffect Missile => TestContent.Mvp.Component("cluster_missile")!.Effects.OfType<WeaponEffect>().Single();
    private static Submunitions Sm => Missile.Submunitions!;
    private static readonly Vec East = new(Fixed.One, Fixed.Zero);
    private const int Deploy = 200;

    private static BurstPattern Compose(uint missile, uint tick = 1000, ulong seed = 7, Submunitions? sm = null) =>
        World.ComposeBurst(sm ?? Sm, seed, tick, missile, new Vec(Fixed.FromInt(500), Fixed.FromInt(500)), East, 1000, 1000);

    // Rho2: a spot's place in the weapon's ordered ellipse centred on (cx, cy), long along x: squared, in thousandths of a radius (1000000 its edge).
    private static long Rho2(long x, long y, long a, long b) => (x * 1000 / a) * (x * 1000 / a) + (y * 1000 / b) * (y * 1000 / b);

    // ---- Content ----

    [Fact]
    public void TheContentNumbers()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype("cluster_truck")!;
        var tactical = c.Archetype("ballistic_truck")!;
        Assert.Equal((3500, 1400), (a.Cost, a.BuildTime)); // 70 s
        Assert.Equal((tactical.MaxHp, tactical.Armor, tactical.Speed, tactical.Radius), (a.MaxHp, a.Armor, a.Speed, a.Radius));
        Assert.Equal((tactical.DeployTicks, tactical.StowTicks, tactical.TunnelSize), (a.DeployTicks, a.StowTicks, a.TunnelSize));
        Assert.True(c.Factions.Single(f => f.Id == "resistance").Archetypes[a.Index]);
        Assert.False(c.Factions.Single(f => f.Id == "sentinel").Archetypes[a.Index]);

        var m = Missile;
        Assert.Equal((Fixed.FromInt(100), Fixed.FromInt(1200)), (m.MinRange, m.Range));
        Assert.Equal((1, 1, 400), (m.Ammo, m.SalvoSize, m.RevealTicks)); // one missile; 20 s of reveal
        Assert.Equal(InterceptClass.Ballistic, m.Intercept);
        Assert.Equal(new DepotReload(2400, 20, 500, true), m.DepotReload); // 120 s, 500 supply, stowed
        Assert.Equal(40, m.Terminal!.WarnTicks); // 2 s (5 s before 2026-11-07: the shells now take 4 s down, so the time to scatter is about what it was)
        Assert.Equal(Fixed.Zero, m.Terminal.DiveFrom); // no dive: engaged until the burst
        Assert.Equal(Fixed.Frac(30, 20), m.ProjectileSpeed); // 30 cells/s (60 before 2026-11-08: a higher opening needs more time in reach)
        var tacticalMissile = c.Component("ballistic_missile")!.Effects.OfType<WeaponEffect>().Single();
        Assert.Equal(tacticalMissile.ProjectileSpeed, m.InterceptSpeed); // air defence's kill model counts it at the tactical missile's speed
        Assert.Equal(Fixed.Zero, tacticalMissile.InterceptSpeed); // and no other missile has the field

        var sm = Sm;
        Assert.Equal(60, sm.Count);
        Assert.Equal((Fixed.FromInt(250), Fixed.FromInt(44), Fixed.FromInt(30)), (sm.BurstHeight, sm.Length, sm.Width)); // 500 m up
        Assert.Equal((50, 80), (sm.ReleaseTicks, sm.FallTicks)); // 2.5 s; 250 cells at 62.5 cells/s: 4 s down
        Assert.Equal(("bomblet", 200), (sm.Warhead.Id, sm.Damage));
        Assert.Equal((Fixed.FromInt(5), Fixed.FromInt(2), 100, 15), (sm.Splash, sm.SplashInner, sm.SplashCenter, sm.SplashOuterPercent)); // 10 m blast, 4 m direct hit
    }

    private const string Shell = "\"warhead\": \"bomblet\", \"damage\": 200, \"splash\": 25, \"splashInner\": 10, \"splashCenter\": 100, \"splashOuterPercent\": 15, " +
        "\"canopy\": 50, \"turn\": 15, \"sizeVariation\": 15, \"pocketShare\": 30, \"candidates\": 8, \"centreLean\": 25, \"strayLean\": 20, \"jitter\": 8, \"count\": 200, " +
        "\"burstHeight\": 600, \"length\": 440, \"width\": 300, \"releaseTicks\": 30, \"fallSpeed\": 1000";

    private const string Fly = "\"terminal\":{\"climbTicks\":30,\"peak\":30,\"peakMin\":800,\"peakMax\":6000,\"peakAt\":55,\"from\":0,\"fall\":250,\"speed\":600,\"warn\":100},";

    public static TheoryData<string, string?> Blocks => new()
    {
        { Fly + "\"submunitions\":{" + Shell + ",\"pockets\":[6,9],\"pocketSize\":[30,50],\"crackle\":[6,12]}", null },
        { "\"submunitions\":{" + Shell + ",\"pockets\":[6,9],\"pocketSize\":[30,50],\"crackle\":[6,12]}", "needs a terminal profile with no dive" },
        { Fly.Replace("\"from\":0", "\"from\":300") + "\"submunitions\":{" + Shell + ",\"pockets\":[6,9],\"pocketSize\":[30,50],\"crackle\":[6,12]}", "needs a terminal profile with no dive" },
        { Fly + "\"submunitions\":{" + Shell + ",\"pockets\":[9,6],\"pocketSize\":[30,50],\"crackle\":[6,12]}", "pockets wants [lowest, highest]" },
        { Fly + "\"submunitions\":{" + Shell + ",\"pockets\":[6,9],\"pocketSize\":[30,250],\"crackle\":[6,12]}", "a pocket wants to fit" },
        { Fly + "\"submunitions\":{" + Shell + ",\"pockets\":[6,9],\"pocketSize\":[30,50],\"crackle\":[6,99]}", "weapon.submunitions.crackle" },
        { Fly + "\"submunitions\":{" + Shell.Replace("\"count\": 200, ", "") + ",\"pockets\":[6,9],\"pocketSize\":[30,50],\"crackle\":[6,12]}", "weapon.submunitions.count is required" },
    };

    [Theory]
    [MemberData(nameof(Blocks))]
    public void TheLoaderChecksASubmunitionsBlock(string blocks, string? error)
    {
        var json = "{\"components\":[{\"id\":\"bad_cluster\",\"requires\":[\"launcher\"],\"effects\":[{\"weapon\":{\"damage\":10,\"range\":15000," +
            "\"minRange\":1500,\"reload\":80,\"warhead\":\"bomblet\",\"splash\":100,\"projectileSpeed\":600,\"arc\":40,\"targets\":[\"ground\"]," + blocks + "}}]}]}";
        var sources = TestContent.MvpSources();
        sources.Add(new ContentSource("extra/cluster.json", json));
        if (error is null)
        {
            Assert.Equal(200, ContentLoader.Load(sources).Component("bad_cluster")!.Effects.OfType<WeaponEffect>().Single().Submunitions!.Count);
            return;
        }
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.Contains(error, e.Message);
    }

    // ---- The pattern ----

    private const int Many = 2000;

    [Fact]
    public void TheOutlineStaysInItsLimitsAndUsesThem()
    {
        var sm = Sm;
        long loA = long.MaxValue, hiA = 0, loB = long.MaxValue, hiB = 0, loTurn = long.MaxValue, hiTurn = long.MinValue;
        for (var i = 0; i < Many; i++)
        {
            var p = Compose((uint)i);
            (loA, hiA) = (Math.Min(loA, p.SemiLength), Math.Max(hiA, p.SemiLength));
            (loB, hiB) = (Math.Min(loB, p.SemiWidth), Math.Max(hiB, p.SemiWidth));
            (loTurn, hiTurn) = (Math.Min(loTurn, p.AxisY), Math.Max(hiTurn, p.AxisY));
            Assert.Equal(65536.0, Math.Sqrt((double)p.AxisX * p.AxisX + (double)p.AxisY * p.AxisY), 0.002 * 65536);
        }
        var (a, b) = (sm.Length.Raw / 2, sm.Width.Raw / 2);
        Assert.InRange(loA, a * 85 / 100, a * 87 / 100);
        Assert.InRange(hiA, a * 113 / 100, a * 115 / 100);
        Assert.InRange(loB, b * 85 / 100, b * 87 / 100);
        Assert.InRange(hiB, b * 113 / 100, b * 115 / 100);
        var sin15 = (long)(0.2588 * 65536);
        Assert.InRange(hiTurn, sin15 * 97 / 100, sin15 * 101 / 100);
        Assert.InRange(loTurn, -sin15 * 101 / 100, -sin15 * 97 / 100);
    }

    // Local: a spot in the burst's own outline frame, from the point at (500, 500).
    private static (long U, long V) Local(BurstPattern p, Vec at)
    {
        long dx = at.X.Raw - 500 * Fixed.OneRaw, dy = at.Y.Raw - 500 * Fixed.OneRaw;
        return ((dx * p.AxisX + dy * p.AxisY) / 65536, (dy * p.AxisX - dx * p.AxisY) / 65536);
    }

    [Fact]
    public void EveryShellFallsInsideItsOutlineAndItsReleaseInOrder()
    {
        var sm = Sm;
        var late = (uint)(1000 + sm.FallTicks + sm.ReleaseTicks);
        for (var i = 0; i < Many; i++)
        {
            var p = Compose((uint)i);
            Assert.Equal(sm.Count, p.Bomblets.Length);
            uint last = 0;
            foreach (var s in p.Bomblets)
            {
                var (u, v) = Local(p, s.At);
                Assert.True(Rho2(u, v, p.SemiLength, p.SemiWidth) <= 1_010_000, $"burst {i}: ({u}, {v}) outside {p.SemiLength} x {p.SemiWidth}");
                Assert.InRange(s.ImpactTick, 1000u + (uint)sm.FallTicks, late);
                Assert.True(s.ImpactTick >= last);
                last = s.ImpactTick;
            }
        }
    }

    [Fact]
    public void ABurstByTheMapsEdgeStaysOnTheMap()
    {
        var corner = new Vec(Fixed.FromInt(3), Fixed.FromInt(3));
        var p = World.ComposeBurst(Sm, 7, 1000, 1, corner, East, 60, 60);
        Assert.All(p.Bomblets, s => Assert.True(s.At.X.Raw >= 0 && s.At.Y.Raw >= 0 && s.At.X.Raw < 60 * Fixed.OneRaw && s.At.Y.Raw < 60 * Fixed.OneRaw));
    }

    [Fact]
    public void ThePocketsTakeTheirShareAndKeepApart()
    {
        var sm = Sm;
        var nearest = new List<long>();
        var counts = new HashSet<int>();
        for (var i = 0; i < Many; i++)
        {
            var p = Compose((uint)i);
            counts.Add(p.Pockets.Length);
            Assert.InRange(p.Pockets.Length, sm.PocketsMin, sm.PocketsMax);
            Assert.Equal(sm.Count * sm.PocketPercent / 100, p.Pockets.Sum(q => q.Shells));
            var least = long.MaxValue;
            for (var x = 0; x < p.Pockets.Length; x++)
            {
                Assert.InRange(p.Pockets[x].Radius, sm.PocketSizeMin.Raw / 2, sm.PocketSizeMax.Raw / 2);
                for (var y = 0; y < x; y++)
                {
                    long du = p.Pockets[x].U - p.Pockets[y].U, dv = p.Pockets[x].V - p.Pockets[y].V;
                    least = Math.Min(least, du * du + dv * dv);
                }
            }
            nearest.Add(Fixed.Sqrt(least));
        }
        Assert.Equal(sm.PocketsMax - sm.PocketsMin + 1, counts.Count);
        nearest.Sort();
        output.WriteLine($"pockets' closest pair: least {nearest[0] / 1024.0:F1} cells, median {nearest[Many / 2] / 1024.0:F1}");
        Assert.True(nearest[0] >= PocketsAtLeast * Fixed.OneRaw, $"two pockets {nearest[0] / 1024.0:F1} cells apart");
        Assert.True(nearest[Many / 2] >= PocketsMedian * Fixed.OneRaw, $"median {nearest[Many / 2] / 1024.0:F1}");
    }

    private const int PocketsAtLeast = 2, PocketsMedian = 6;

    [Fact]
    public void ThePocketsWalkAlongTheFlightLineWithJitterAndEachCrackles()
    {
        var sm = Sm;
        long sumEarly = 0, sumLate = 0, nEarly = 0, nLate = 0;
        var ranges = new HashSet<int>();
        for (var i = 0; i < Many; i++)
        {
            var p = Compose((uint)i);
            foreach (var q in p.Pockets)
            {
                Assert.InRange(q.Crackle, sm.CrackleMinTicks, sm.CrackleMaxTicks);
                Assert.InRange(q.Start + q.Crackle, 0, sm.ReleaseTicks);
                ranges.Add(q.Start);
                if (q.U < 0)
                {
                    sumEarly += q.Start;
                    nEarly++;
                }
                else
                {
                    sumLate += q.Start;
                    nLate++;
                }
            }
        }
        Assert.True(sumEarly / nEarly + 5 < sumLate / nLate, "pockets near the launcher's side go off first, on the whole");
        Assert.True(ranges.Count > 15, "not a metronome");
    }

    [Fact]
    public void NoTwoBurstsAreAlike()
    {
        // Alike: every pocket of each within 2 cells of a pocket of the other (a layout a player could learn).
        var bursts = Enumerable.Range(0, 300).Select(i => Compose((uint)(i * 7 + 1), (uint)(500 + i * 3))).ToArray();
        var close = 2L * Fixed.OneRaw * 2 * Fixed.OneRaw;
        static bool Near(BurstPattern one, BurstPattern other, long close) =>
            one.Pockets.All(q => other.Pockets.Any(o => (q.U - o.U) * (q.U - o.U) + (q.V - o.V) * (q.V - o.V) <= close));
        for (var x = 0; x < bursts.Length; x++)
        {
            for (var y = 0; y < x; y++)
            {
                Assert.False(Near(bursts[x], bursts[y], close) && Near(bursts[y], bursts[x], close), $"bursts {x} and {y}");
            }
        }
        // And a seed, a tick or a missile alone changes it.
        var one = Compose(5, 1000, 7).Bomblets;
        Assert.NotEqual(one, Compose(6, 1000, 7).Bomblets);
        Assert.NotEqual(one, Compose(5, 1001, 7).Bomblets);
        Assert.NotEqual(one, Compose(5, 1000, 8).Bomblets);
        Assert.Equal(one, Compose(5, 1000, 7).Bomblets);
    }

    [Fact]
    public void NoBigHoleIsLeftInTheMiddle()
    {
        // Over many bursts, anywhere in the middle half of the ordered ellipse: how far the nearest shell lands.
        var worst = 0L;
        var hole = new List<long>();
        for (var i = 0; i < 500; i++)
        {
            var p = Compose((uint)i);
            var most = 0L;
            for (var x = -11; x <= 11; x++)
            {
                for (var y = -7; y <= 7; y++)
                {
                    long qx = (500 + x) * (long)Fixed.OneRaw, qy = (500 + y) * (long)Fixed.OneRaw;
                    var near = long.MaxValue;
                    foreach (var s in p.Bomblets)
                    {
                        long dx = s.At.X.Raw - qx, dy = s.At.Y.Raw - qy;
                        near = Math.Min(near, dx * dx + dy * dy);
                    }
                    most = Math.Max(most, near);
                }
            }
            hole.Add(Fixed.Sqrt(most));
            worst = Math.Max(worst, most);
        }
        hole.Sort();
        output.WriteLine($"biggest hole in the middle: worst {Fixed.Sqrt(worst) / 1024.0:F1} cells, median {hole[250] / 1024.0:F1}");
        Assert.True(worst <= (long)HoleCells * Fixed.OneRaw * HoleCells * Fixed.OneRaw, $"a hole of {Fixed.Sqrt(worst) / 1024.0:F1} cells");
    }

    private const int HoleCells = 9; // the blast reaches 5

    // ---- Damage ----

    // Point: a one-shell weapon landing almost exactly on the point (the warhead's numbers, without the pattern).
    private static Submunitions Point => Sm with
    {
        Count = 1, PocketPercent = 0, StrayLeanPercent = 0, Length = Fixed.Frac(1, 10), Width = Fixed.Frac(1, 10), PocketSizeMin = Fixed.Frac(1, 100),
        PocketSizeMax = Fixed.Frac(1, 100),
    };

    [Theory]
    [InlineData("Rifleman", 2, 200, 30)]
    [InlineData("cargo_truck", 2, 90, 13)]
    [InlineData("technical", 2, 90, 13)]
    [InlineData("ballistic_truck", 2, 140, 21)]
    [InlineData("humvee", 1, 140, 21)]
    [InlineData("main_battle_tank", 1, 690, 103)] // the top armour's 150% on the table's 230%
    [InlineData("merkava_tank", 1, 690, 103)]
    [InlineData("light_tank", 1, 736, 110)] // top 160%
    public void AShellsDamagePerClass(string kind, byte player, int direct, int blast)
    {
        var w = Arena.NewSentinel(40);
        var (hit, near, far) = (w.Spawn(player, kind, 10, 20), w.Spawn(player, kind, 14, 20), w.Spawn(player, kind, 16, 20));
        foreach (var u in new[] { hit, near, far })
        {
            u.HP = 100_000;
            u.Facing = Facing.East; // the top armour, wherever the hull points and wherever the shell lands
        }
        w.Step();
        w.ReleaseBombletsForTest(99, 3, new Vec(Fixed.FromInt(10) + Fixed.Frac(1, 2), Fixed.FromInt(20) + Fixed.Frac(1, 2)), East, Point);
        w.LandBombletsForTest();
        Assert.Equal(direct, 100_000 - hit.HP);
        Assert.Equal(blast, 100_000 - near.HP); // 4 cells off: the minimal blast (out to 5)
        Assert.Equal(100_000, far.HP); // 6 cells off: nothing
    }

    [Fact]
    public void TheTableAgainstBuildingsAircraftAndDrones()
    {
        var c = TestContent.Mvp;
        var t = Sm.Warhead;
        Assert.Equal((40, 6), (t.Damage(200, c.ArmorClass("building"), 100), t.Damage(200, c.ArmorClass("building"), 15)));
        Assert.Equal((800, 120), (t.Damage(200, c.ArmorClass("parked_aircraft"), 100), t.Damage(200, c.ArmorClass("parked_aircraft"), 15))); // 400% (was 200%)
        Assert.False(t.CanHit(c.ArmorClass("drone")));
        Assert.False(t.CanHit(c.ArmorClass("aircraft")));

        var w = Arena.NewSentinel(40);
        var flying = w.Spawn(1, "watcher_drone", 10, 20);
        var hp = flying.HP;
        w.Step();
        flying.Pos = new Vec(Fixed.FromInt(10) + Fixed.Frac(1, 2), Fixed.FromInt(20) + Fixed.Frac(1, 2));
        w.ReleaseBombletsForTest(99, 3, flying.Pos, East, Point);
        w.LandBombletsForTest();
        Assert.Equal(hp, flying.HP);
    }

    [Theory]
    [InlineData(3, 100, 15)] // dense forest: half (the shell bursts in the treetops)
    [InlineData(2, 200, 30)] // a grove: as in the open
    public void DenseForestTakesHalf(int density, int direct, int blast)
    {
        const int Size = 64;
        var w = Arena.NewSentinel(Size);
        var flags = new byte[Size * Size];
        Array.Fill(flags, (byte)density);
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        w.AttachTerrain(new TerrainMap(Size, Size, new short[(Size + 1) * (Size + 1)], flags, rules));
        var (hit, near) = (w.Spawn(2, "Rifleman", 30, 30), w.Spawn(2, "Rifleman", 34, 30));
        hit.HP = near.HP = 100_000;
        w.Step();
        w.ReleaseBombletsForTest(99, 3, new Vec(Fixed.FromInt(30) + Fixed.Frac(1, 2), Fixed.FromInt(30) + Fixed.Frac(1, 2)), East, Point);
        w.LandBombletsForTest();
        Assert.Equal((direct, blast), (100_000 - hit.HP, 100_000 - near.HP));
    }

    // ---- The seeded balance table ----

    private sealed class Row(string name, int maxHp)
    {
        public readonly string Name = name;
        public readonly int MaxHp = maxHp;
        public readonly long[] N = new long[3], Dead = new long[3], Lost = new long[3], Raw = new long[3];

        public double LostPercent(int ring) => 100.0 * Lost[ring] / (N[ring] * MaxHp);

        public double DeadPercent(int ring) => 100.0 * Dead[ring] / N[ring];

        public override string ToString()
        {
            string P(int k) => $"{LostPercent(k),5:F1}% hp {DeadPercent(k),5:F1}% dead";
            return $"{Name,-18} hp {MaxHp,4} | average {P(0)} (uncapped {(double)Raw[0] / N[0],5:F0}) | centre {P(1)} | rim {P(2)}";
        }
    }

    // Table: real blasts over many seeded bursts, the pattern on. A grid of each class over the ordered ellipse (the average spot: every
    // spot of the area alike), with HP that can't run out; what it would have lost is capped by its true HP, so pockets' overkill doesn't count.
    // Ring 0 the whole ellipse, 1 the centre (within 35% of the way out), 2 the rim (from 85%).
    private static List<Row> Table(int bursts)
    {
        const int Cx = 100, Cy = 100, Spacing = 3, Huge = 100_000_000;
        var w = Arena.NewSentinel(200, 11);
        var classes = new (string Id, byte Player)[]
        {
            ("Rifleman", 2), ("cargo_truck", 2), ("technical", 2), ("ballistic_truck", 2), ("humvee", 1), ("hypersonic_truck", 2),
            ("main_battle_tank", 1), ("merkava_tank", 1), ("light_tank", 1),
        };
        var units = new List<(int Row, int Ring, Unit U)>();
        var rows = new List<Row>();
        for (var c = 0; c < classes.Length; c++)
        {
            var row = -1;
            for (var x = -22 + c % Spacing; x <= 22; x += Spacing)
            {
                for (var y = -15 + c / Spacing; y <= 15; y += Spacing)
                {
                    var r2 = Rho2(x * Fixed.OneRaw, y * Fixed.OneRaw, 22 * Fixed.OneRaw, 15 * Fixed.OneRaw);
                    if (r2 > 1_000_000)
                    {
                        continue;
                    }
                    var u = w.Spawn(classes[c].Player, classes[c].Id, Cx + x, Cy + y);
                    if (row < 0)
                    {
                        row = rows.Count;
                        rows.Add(new Row(classes[c].Id, u.HP));
                    }
                    units.Add((row, r2 <= 122_500 ? 1 : r2 >= 722_500 ? 2 : 0, u));
                    u.HP = Huge;
                }
            }
        }
        w.Step();
        var centre = new Vec(Fixed.FromInt(Cx), Fixed.FromInt(Cy));
        for (var i = 0; i < bursts; i++)
        {
            w.ReleaseBombletsForTest((uint)(9000 + i), 3, centre, East, Sm);
            w.LandBombletsForTest();
            foreach (var (row, ring, u) in units)
            {
                var r = rows[row];
                var d = Huge - (long)u.HP;
                u.HP = Huge;
                foreach (var k in ring == 0 ? [0] : new[] { 0, ring })
                {
                    r.N[k]++;
                    r.Raw[k] += d;
                    r.Lost[k] += Math.Min(d, r.MaxHp);
                    r.Dead[k] += d >= r.MaxHp ? 1 : 0;
                }
            }
        }
        return rows;
    }

    [Fact]
    public void TheSeededBalanceTable()
    {
        var rows = Table(400);
        foreach (var r in rows)
        {
            output.WriteLine(r.ToString());
        }
        double Lost(string id) => rows.Single(r => r.Name == id).LostPercent(0);
        var rifle = rows.Single(r => r.Name == "Rifleman");
        Assert.InRange(rifle.DeadPercent(0), 80, 95); // infantry in the open die
        Assert.True(rifle.DeadPercent(1) > rifle.DeadPercent(0) && rifle.DeadPercent(2) < rifle.DeadPercent(0), "more in the middle, fewer at the rim");
        Assert.InRange(Lost("ballistic_truck"), 40, 60); // launchers about half
        Assert.InRange(Lost("cargo_truck"), 40, 60);
        Assert.InRange(Lost("technical"), 40, 60);
        Assert.InRange(Lost("main_battle_tank"), 25, 35); // tanks about 30%
        Assert.InRange(Lost("merkava_tank"), 20, 35);
        Assert.True(Lost("light_tank") > Lost("main_battle_tank") + 5, "lighter armour pays");
    }

    // ---- The flight ----

    private static (World W, Unit Truck) Range(int size, bool fog = false, ulong seed = 1)
    {
        var w = Arena.New(size, seed, fog ? s => s with { Fog = true } : null);
        var (t, _) = w.SpawnCrewed(1, "cluster_truck", 5, 40, Facing.East);
        return (w, t);
    }

    private static List<SubmunitionBurst> RunWatching(World w, int ticks, Action<World>? each = null)
    {
        var bursts = new List<SubmunitionBurst>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            bursts.AddRange(w.SubmunitionBursts);
            each?.Invoke(w);
        }
        return bursts;
    }

    [Fact]
    public void ItFliesItsArcToABurstOverThePointWithAWarningThenRains()
    {
        var (w, truck) = Range(1100);
        var soldier = w.Spawn(2, "Rifleman", 1000, 40);
        var hp = soldier.HP;
        w.Order(1, CommandKind.AttackGround, [truck], cell: new Cell(1005, 40));
        uint launch = 0, warned = 0, struck = 0, flyingAt = 0;
        var top = 0L;
        var strikes = new List<IncomingStrike>();
        var bursts = RunWatching(w, 1100, x =>
        {
            foreach (var e in x.IncomingEvents)
            {
                switch (e.Kind)
                {
                    case IncomingEventKind.Launched: launch = x.Tick; break;
                    case IncomingEventKind.Warned: warned = x.Tick; break;
                    case IncomingEventKind.Struck: struck = x.Tick; break;
                    case IncomingEventKind.Diving: Assert.Fail("no dive: it can be engaged until it opens"); break;
                }
            }
            strikes.AddRange(x.IncomingStrikes);
            foreach (var p in x.Projectiles.Where(p => p.Weapon.Effect.Submunitions is not null && !p.Done))
            {
                top = Math.Max(top, p.Altitude.Raw);
                flyingAt = x.Tick;
            }
        });
        var b = Assert.Single(bursts);
        Assert.InRange((int)(struck - launch), 1000 * 20 / 30, 1000 * 20 / 30 + 40); // 30 cells/s, and the vertical start (the first 2 s) is slower over the ground
        Assert.Equal(40u, struck - warned);
        Assert.True(flyingAt < struck);
        Assert.Equal(250L * Fixed.OneRaw, b.Altitude.Raw); // right over the point, at its burst height (a flat map's ground is 0)
        Assert.Equal(new Vec(Fixed.FromInt(1005) + Fixed.Frac(1, 2), Fixed.FromInt(40) + Fixed.Frac(1, 2)), b.At);
        Assert.True(top > 200L * Fixed.OneRaw, "a high arc");
        Assert.True(b.Heading.X.Raw > 1000 && Math.Abs(b.Heading.Y.Raw) < 30, "its flight line, east");
        Assert.Equal(Sm.Count, b.Bomblets.Count);
        Assert.True(soldier.HP < hp || !soldier.Alive || b.Bomblets.All(s => (s.At - soldier.Pos).Len().Raw > Sm.Splash.Raw));
        Assert.Equal(Sm.FallTicks, b.FallTicks);

        // The warning: the weapon's area along its line, the same from its first tick to its last, whatever the burst did after.
        Assert.NotEmpty(strikes);
        Assert.All(strikes, s => Assert.Equal((Sm.Length, Sm.Width, b.Heading, b.At), (s.Length, s.Width, s.Heading, s.At)));
        Assert.Equal(40, strikes.Count);
    }

    [Fact]
    public void TheWarningIsTheSameAreaOnEverySeed()
    {
        var seen = new HashSet<(Fixed, Fixed, Vec, Fixed)>();
        for (ulong seed = 1; seed <= 4; seed++)
        {
            var (w, truck) = Range(400, seed: seed);
            w.Order(1, CommandKind.AttackGround, [truck], cell: new Cell(305, 40));
            var bursts = RunWatching(w, 800, x =>
            {
                foreach (var s in x.IncomingStrikes)
                {
                    seen.Add((s.Length, s.Width, s.Heading, s.Radius));
                }
            });
            Assert.Single(bursts);
        }
        Assert.Single(seen);
    }

    [Fact]
    public void ALaunchShowsTheTruckToEveryEnemy()
    {
        var (w, truck) = Range(400, fog: true);
        w.Spawn(2, "Rifleman", 390, 390); // far away, nothing of player 2's near the truck
        w.Order(1, CommandKind.AttackGround, [truck], cell: new Cell(305, 40));
        var shown = 0;
        RunWatching(w, 800, x => shown += World.IsVisibleTo(truck, 2) ? 1 : 0);
        Assert.InRange(shown, 395, 405); // 20 s of reveal
    }

    [Fact]
    public void AnUnseenBurstIsNotToldToAPlayerWhoCantSeeIt()
    {
        // Player 2 has a soldier far away: no fog cell of the area shows. The shooter is always told.
        var (w, truck) = Range(400, fog: true);
        w.Spawn(2, "Rifleman", 390, 390);
        w.Order(1, CommandKind.AttackGround, [truck], cell: new Cell(305, 40));
        var b = Assert.Single(RunWatching(w, 800));
        Assert.True(b.SeenByPlayer(1));
        Assert.False(b.SeenByPlayer(2));

        // A soldier beside the rim sees part of it: he is told; the spots he can't see are SeesPoint's to filter.
        (w, truck) = Range(400, fog: true);
        w.Spawn(2, "Rifleman", 305, 52);
        w.Order(1, CommandKind.AttackGround, [truck], cell: new Cell(305, 40));
        var seen = Assert.Single(RunWatching(w, 800));
        Assert.True(seen.SeenByPlayer(2));

        // No fog: everybody.
        (w, truck) = Range(400);
        w.Order(1, CommandKind.AttackGround, [truck], cell: new Cell(305, 40));
        var open = Assert.Single(RunWatching(w, 800));
        Assert.True(open.SeenByPlayer(1) && open.SeenByPlayer(2));
    }

    [Fact]
    public void ItOnlyReloadsAtADepotAndHasOneMissile()
    {
        var (w, truck) = Range(400);
        w.Order(1, CommandKind.AttackGround, [truck], cell: new Cell(305, 40));
        var bursts = RunWatching(w, 1600);
        Assert.Single(bursts); // one missile, no reload out here
    }

    // ---- Interception ----

    private const int X = 200, Yb = 200;

    // Battery: a THAAD type and its radar deployed at (X + offset, Yb), and the truck's shot at the point (X, Yb + 2) from distance cells north of it.
    private static (World W, Unit Soldier) Battery(int distance, ulong seed, int offset = 0, string type = "thaad_launcher")
    {
        var w = Arena.NewSentinel(X + distance + 80, seed);
        var launcher = w.SpawnCrewed(1, type, X + offset, Yb).Vehicle;
        var r = w.SpawnCrewed(1, "radar_truck", X + offset + 10, Yb).Vehicle;
        w.Order(1, CommandKind.Deploy, [launcher, r]);
        w.Run(520);
        var soldier = w.Spawn(1, "humvee", X, Yb + 2); // air defence guards its side's units: this is what it guards
        soldier.HP = 100_000;
        var truck = w.SpawnCrewed(2, "cluster_truck", X, Yb + distance, Facing.South).Vehicle;
        w.Order(2, CommandKind.AttackGround, [truck], cell: new Cell(X, Yb + 2));
        return (w, soldier);
    }

    // The kill chance of an interceptor in flight at the cluster missile, against the same chance from a copy of that interceptor whose speed
    // reference is out of reach (no cut by speed at all): equal (the missile flies above the Patriot type's reference of 26 cells/s, not the THAAD type's 40).
    [Theory]
    [InlineData("patriot_launcher")]
    [InlineData("thaad_launcher")]
    public void TheKillChancePerFuseIsNotCutBySpeed(string type)
    {
        var (w, _) = Battery(600, 1, type: type);
        for (var i = 0; i < Deploy + 500; i++)
        {
            w.Step();
            foreach (var m in w.Projectiles.Where(p => p.Weapon.Effect.Interceptor is not null && !p.Done && p.Guided))
            {
                if (w.Projectiles.FirstOrDefault(q => q.Id == m.Target) is not { } target || target.Weapon.Effect.Interceptor is not null)
                {
                    continue;
                }
                var ic = m.Weapon.Effect.Interceptor!;
                var body = new AirBody(target);
                var (vx, vy, vz) = w.AirVelocity(body);
                if (type == "patriot_launcher")
                {
                    Assert.True(World.Len3(vx, vy, vz) > ic.SpeedRefOn(InterceptClass.Ballistic).Raw, "the missile flies above the Patriot type's reference"); // not above the THAAD type's since it flies at 30 cells/s
                }
                var uncut = ic with { SpeedRef = Fixed.FromInt(1000), Rules = [.. ic.Rules.Select(r => r with { SpeedRef = Fixed.FromInt(1000) })] };
                var chance = w.KillPermille(m, body, ic);
                Assert.True(chance > 0);
                Assert.Equal(w.KillPermille(m, body, uncut), chance);
                return;
            }
        }
        Assert.Fail($"{type} never fired at the cluster missile");
    }

    [Fact]
    public void AKillBeforeTheBurstDropsNothing()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var (w, soldier) = Battery(600, seed);
            var events = new List<IncomingEvent>();
            var bursts = RunWatching(w, Deploy + 500, x => events.AddRange(x.IncomingEvents));
            if (!events.Exists(e => e.Kind == IncomingEventKind.Intercepted))
            {
                continue;
            }
            Assert.Empty(bursts);
            Assert.DoesNotContain(events, e => e.Kind == IncomingEventKind.Struck);
            Assert.Equal(100_000, soldier.HP);
            return;
        }
        Assert.Fail("no THAAD kill in 40 seeds");
    }

    [Fact]
    public void AnInterceptorStillFlyingAtTheBurstMissesAndTheShellsAreNeverTargets()
    {
        // The burst with one of the battery's missiles still on its way: it loses its target and flies on; the rain lands all the same.
        // A long shot at a battery 200 cells off the point (the first setup, seed 1: two missiles in the air): it opens 500 m up, and the battery reaches it late, so its missiles are still in the air when this one opens.
        foreach (var (distance, offset) in new[] { (740, 200), (740, 100), (740, 300), (500, 200) })
        {
            for (ulong seed = 1; seed <= 6; seed++)
            {
                var (w, soldier) = Battery(distance, seed, offset);
                uint missile = 0;
                var flyingAtBurst = new List<Projectile>();
                var killed = false;
                var after = 0;
                var bursts = new List<SubmunitionBurst>();
                for (var i = 0; i < Deploy + 900; i++)
                {
                    var flying = w.Projectiles.Where(p => p.Weapon.Effect.Interceptor is not null && !p.Done).ToList();
                    w.Step();
                    killed |= w.IncomingEvents.Any(e => e.Kind == IncomingEventKind.Intercepted);
                    foreach (var b in w.SubmunitionBursts)
                    {
                        bursts.Add(b);
                        missile = b.Missile;
                        flyingAtBurst = flying.Where(p => !p.Done && p.Target == missile).ToList();
                    }
                    if (bursts.Count > 0)
                    {
                        after++;
                        // From the burst on nothing engages anything of it: no projectile is launched at it, no fuse goes off on it.
                        Assert.DoesNotContain(w.Projectiles, p => p.Weapon.Effect.Interceptor is not null && p.Target == missile && p.Guided);
                        Assert.DoesNotContain(w.AirBursts, b => b.Target == missile && b.Kind is AirBurstKind.Kill or AirBurstKind.Hit);
                    }
                }
                if (flyingAtBurst.Count == 0 || killed)
                {
                    continue;
                }
                Assert.Single(bursts);
                Assert.True(after > Sm.FallTicks + Sm.ReleaseTicks);
                Assert.All(flyingAtBurst, p => Assert.False(p.Guided));
                Assert.True(soldier.HP < 100_000, "the rain lands all the same");
                return;
            }
        }
        Assert.Fail("no interceptor in the air at a burst in any setup");
    }

    // ---- Determinism ----

    [Fact]
    public void TheSameMatchHashesTheSameThroughTheRainAndPendingShellsAreState()
    {
        static (List<ulong> Hashes, BombletImpact[] Spots) Run(ulong seed)
        {
            var (w, truck) = Range(400, seed: seed);
            w.Spawn(2, "Rifleman", 300, 40);
            w.Order(1, CommandKind.AttackGround, [truck], cell: new Cell(305, 40));
            var hashes = new List<ulong>();
            BombletImpact[] spots = [];
            for (var i = 0; i < 700; i++)
            {
                w.Step();
                hashes.Add(w.Hash());
                Assert.Equal(w.HashReference(), w.Hash());
                foreach (var b in w.SubmunitionBursts)
                {
                    spots = [.. b.Bomblets];
                }
            }
            return (hashes, spots);
        }
        var (a, sa) = Run(3);
        var (b, sb) = Run(3);
        var (c, sc) = Run(4);
        Assert.Equal(a, b);
        Assert.Equal(sa, sb);
        Assert.NotEmpty(sa);
        Assert.NotEqual(sa, sc);
    }

    [Fact]
    public void PendingShellsChangeTheHash()
    {
        static ulong Hash(bool burst)
        {
            var w = Arena.New(60, 5);
            w.Step();
            if (burst)
            {
                w.ReleaseBombletsForTest(99, 1, new Vec(Fixed.FromInt(30), Fixed.FromInt(30)), East, Sm);
            }
            return w.Hash();
        }
        Assert.NotEqual(Hash(false), Hash(true));
        Assert.Equal(Hash(false), Hash(false));
    }
}
