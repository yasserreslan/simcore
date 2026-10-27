using System.Text;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The Resistance's cruise missile truck (cruise_truck; docs/design.md, "Cruise missile truck"): four slow cruise missiles, one
// every 2 s, each on its own route (up to 8 waypoints and its point, 150 to 2,000 cells), boosting 40 m off the truck, then 15 m
// over the ground and treetops (a 10-cell look-ahead, climbing at most 20 m/s and descending at most 15 m/s, a 6-cell turn at each
// waypoint), a pop-up to 40 m and a dive over the last 4 cells; 900 on the heavy_missile table. Below 30 m a ground sensor sees it
// only with a clear line over the terrain and the treetops and within 120 cells; seen, the enemy is warned for 5 s after; ground
// air defence engages it. It sets up only on ground no steeper than 15% under it. Player 1 is the Sentinel, player 2 the Resistance.
public class CruiseTests(ITestOutputHelper output)
{
    private const int Deploy = 160, Interval = 40, M = 512; // M: raw a metre

    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static WeaponEffect Missile => TestContent.Mvp.Component("cruise_missile")!.Effects.OfType<WeaponEffect>().Single();

    // A w x h world from the Sentinel lobby (player 1 Sentinel, player 2 Resistance), no armies, with the terrain if given.
    private static World NewWorld(int w, int h, ulong seed = 1, TerrainMap? map = null)
    {
        var setup = TestContent.Sentinel(seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var world = new World(new Grid(w, h), MatchCompiler.Compile(TestContent.Mvp, setup));
        if (map is not null)
        {
            world.AttachTerrain(map);
        }
        return world;
    }

    // Map: a w x h terrain whose corner (x, y) is height(x, y) raw high, forest density forest(x, y) on cell (x, y).
    private static TerrainMap Map(int w, int h, Func<int, int, int> height, Func<int, int, byte>? forest = null)
    {
        var corners = new int[(w + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        {
            for (var x = 0; x <= w; x++)
            {
                corners[y * (w + 1) + x] = height(x, y);
            }
        }
        var flags = new byte[w * h];
        if (forest is not null)
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    flags[y * w + x] = forest(x, y);
                }
            }
        }
        return new TerrainMap(w, h, corners, flags, Rules);
    }

    private static Unit Truck(World w, int x, int y, int facing = Facing.East) => w.SpawnCrewed(2, "cruise_truck", x, y, facing).Vehicle;

    private static void Fire(World w, Unit t, Cell target, IReadOnlyList<Cell>? route = null, bool queue = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = t.Owner, Kind = CommandKind.AttackGround, Units = [t.Id], Cell = target, Route = route, Queue = queue });

    private static void SetUp(World w, params Unit[] units)
    {
        w.Order(units[0].Owner, CommandKind.Deploy, units);
        w.Run(units.Max(u => u.Def.Archetype.DeployTicks) + 2);
        Assert.All(units, u => Assert.Equal(DeployState.Deployed, u.Deploy));
    }

    // Flight: what a run of ticks shows: each cruise missile as launched, the events, the detonations, the refusals, per missile the
    // ticks player 1's sensors saw it and the ticks he was warned of it.
    private sealed class Flight
    {
        public List<Projectile> Missiles { get; } = [];
        public List<(uint Tick, IncomingEvent E)> Events { get; } = [];
        public List<(uint Tick, Detonation D)> Blasts { get; } = [];
        public List<OrderRefusal> Refusals { get; } = [];
        public List<(uint Tick, uint Id)> Seen { get; } = [];
        public List<(uint Tick, uint Id)> Warned { get; } = [];
    }

    private static Flight Watch(World w, int ticks, Action<World>? each = null)
    {
        var f = new Flight();
        for (var i = 0; i < ticks; i++)
        {
            var tick = w.Tick;
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (p.Cruise is { } c)
                {
                    if (!f.Missiles.Contains(p))
                    {
                        f.Missiles.Add(p);
                    }
                    if ((c.SeenNow & 2) != 0)
                    {
                        f.Seen.Add((tick, p.Id));
                    }
                }
            }
            foreach (var s in w.IncomingStrikes)
            {
                if ((s.Warned & 2) != 0)
                {
                    f.Warned.Add((tick, s.Projectile));
                }
            }
            f.Events.AddRange(w.IncomingEvents.Select(e => (tick, e)));
            f.Blasts.AddRange(w.Detonations.Select(d => (tick, d)));
            f.Refusals.AddRange(w.OrderRefusals);
            each?.Invoke(w);
        }
        return f;
    }

    // ---- Content ----

    [Fact]
    public void TheContentNumbers()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype("cruise_truck")!;
        Assert.Equal("Cruise missile truck", a.Name);
        Assert.Equal((3500, 1600, 420), (a.Cost, a.BuildTime, a.MaxHp)); // 80 s
        Assert.Equal(c.Archetype("hypersonic_truck")!.Armor, a.Armor); // medium_vehicle
        Assert.Equal(Fixed.Frac(16, 200), a.Speed); // 1.6 cells a second
        Assert.Equal((Deploy, 120, 15), (a.DeployTicks, a.StowTicks, a.DeploySlope));
        Assert.True(c.Factions.Single(f => f.Id == "resistance").Archetypes[a.Index]);
        Assert.False(c.Factions.Single(f => f.Id == "sentinel").Archetypes[a.Index]);
        Assert.Equal(0, c.Archetype("hypersonic_truck")!.DeploySlope); // the slope rule is this truck's only

        var m = Missile;
        Assert.Equal((900, "heavy_missile"), (m.Damage, m.Warhead.Id));
        Assert.Equal((Fixed.FromInt(8), Fixed.FromInt(3), 30), (m.Splash, m.SplashInner, m.SplashOuterPercent));
        Assert.Equal((4, 1, Interval), (m.Ammo, m.SalvoSize, m.Reload));
        Assert.Equal(400, m.RevealTicks); // 20 s
        Assert.Equal(InterceptClass.Cruise, m.Intercept);
        Assert.Equal(new DepotReload(1200, 20, 300, true), m.DepotReload);
        Assert.Equal(Fixed.Frac(350, 200), m.ProjectileSpeed);
        Assert.Null(m.Fire);
        var cf = m.Cruise!;
        Assert.Equal((40, 40L * M, 15L * M, Fixed.FromInt(10)), (cf.BoostTicks, cf.BoostRaw, cf.HeightRaw, cf.LookAhead));
        Assert.Equal((20L * M / 20, 15L * M / 20), (cf.ClimbRaw, cf.DescendRaw)); // 20 and 15 m/s
        Assert.Equal((Fixed.FromInt(6), 40L * M, Fixed.FromInt(4)), (cf.TurnRadius, cf.PopUpRaw, cf.DiveFrom));
        Assert.Equal((Fixed.FromInt(150), Fixed.FromInt(2000), 8, 100), (cf.RouteMin, cf.RouteMax, cf.MaxWaypoints, cf.WarnTicks));
        Assert.Equal(new LowFlightSight(30L * M, Fixed.FromInt(120)), m.LowSight);
        Assert.Equal(new Signature(40, 150, 100), m.Signature);

        // Who engages it: the Patriot type best (85), THAAD only above 30 m, short-range air defence as a drone, never a fighter.
        static Interceptor Ic(string id) => TestContent.Mvp.Component(id)!.Effects.OfType<WeaponEffect>().Single().Interceptor!;
        Assert.Equal(85, Ic("patriot_missiles").Best(InterceptClass.Cruise));
        Assert.Equal(Ic("patriot_missiles").Best(InterceptClass.Missile), Ic("patriot_missiles").Best(InterceptClass.Cruise));
        Assert.True(Ic("thaad_missiles").Engages(InterceptClass.Cruise));
        Assert.Equal(30L * M, Ic("thaad_missiles").MinAltitudeRaw);
        foreach (var id in new[] { "sam_missiles", "igla" })
        {
            var ic = Ic(id);
            Assert.True(ic.Engages(InterceptClass.Cruise), id);
            Assert.Equal((ic.Best(InterceptClass.Drone), ic.SpeedRefOn(InterceptClass.Drone)), (ic.Best(InterceptClass.Cruise), ic.SpeedRefOn(InterceptClass.Cruise)));
        }
        foreach (var id in new[] { "aam_radar", "aam_ir", "aam_ir_light", "f35_aam", "s400_missile", "kub_missiles" })
        {
            Assert.False(Ic(id).Engages(InterceptClass.Cruise), id);
        }
    }

    // ---- The route ----

    [Fact]
    public void RoutesFromOneHundredFiftyToTwoThousandCells()
    {
        var w = NewWorld(2100, 60);
        var t = Truck(w, 5, 30);
        CruiseRouteCheck Check(Cell at, IReadOnlyList<Cell>? route = null) => w.CheckCruiseRoute(t, route, at);
        Assert.Equal(CruiseRouteProblem.TooShort, Check(new Cell(5 + 149, 30)).Problem);
        Assert.True(Check(new Cell(5 + 150, 30)).Ok);
        Assert.Equal(Fixed.FromInt(150), Check(new Cell(5 + 150, 30)).Length);
        Assert.True(Check(new Cell(5 + 2000, 30)).Ok);
        Assert.Equal(CruiseRouteProblem.TooLong, Check(new Cell(5 + 2001, 30)).Problem);
        // The legs count, not the straight line: 100 cells away by a dog-leg of 2 x 50 + 100 is in.
        Assert.Equal(CruiseRouteProblem.TooShort, Check(new Cell(105, 30)).Problem);
        Assert.True(Check(new Cell(105, 30), [new Cell(5, 5), new Cell(105, 5)]).Ok);
        // At most 8 waypoints, all on the map.
        var eight = Enumerable.Range(1, 8).Select(i => new Cell(5 + i * 40, i % 2 == 0 ? 10 : 50)).ToList();
        Assert.True(Check(new Cell(400, 30), eight).Ok);
        Assert.Equal(CruiseRouteProblem.TooManyWaypoints, Check(new Cell(400, 30), [.. eight, new Cell(380, 30)]).Problem);
        Assert.Equal((CruiseRouteProblem.OffMap, 1), (Check(new Cell(300, 30), [new Cell(100, 30), new Cell(150, 70)]).Problem, Check(new Cell(300, 30), [new Cell(100, 30), new Cell(150, 70)]).Leg));
        Assert.Equal(CruiseRouteProblem.NoCruiseWeapon, w.CheckCruiseRoute(w.SpawnCrewed(2, "hypersonic_truck", 5, 40).Vehicle, null, new Cell(300, 30)).Problem);

        // The order: too short or too long is refused (nothing fires); in the band it fires.
        Fire(w, t, new Cell(5 + 140, 30));
        var f = Watch(w, Deploy + 60);
        Assert.Contains(f.Refusals, r => r.Aircraft == t.Id && r.Reason == OrderRefusalReason.RouteInvalid);
        Assert.Empty(f.Missiles);
        Assert.Equal(4, t.Charges[0]);
        Fire(w, t, new Cell(5 + 2001, 30));
        f = Watch(w, Deploy + 60);
        Assert.Contains(f.Refusals, r => r.Reason == OrderRefusalReason.RouteInvalid);
        Assert.Empty(f.Missiles);
        Fire(w, t, new Cell(5 + 160, 30));
        f = Watch(w, Deploy + 200);
        Assert.Single(f.Missiles);
        Assert.Contains(f.Blasts, b => b.D.Projectile == f.Missiles[0].Id && Cell.Of(b.D.At) == new Cell(165, 30));
    }

    [Fact]
    public void FourMissilesOnFourRoutesTwoSecondsApart()
    {
        var w = NewWorld(500, 500);
        var t = Truck(w, 250, 250);
        var plans = new (Cell Target, Cell[] Route)[]
        {
            (new Cell(250, 60), [new Cell(300, 200), new Cell(200, 120)]),
            (new Cell(450, 250), []),
            (new Cell(250, 450), [new Cell(150, 300), new Cell(150, 400), new Cell(200, 430)]),
            (new Cell(40, 250), [new Cell(250, 350), new Cell(100, 350)]),
        };
        for (var i = 0; i < plans.Length; i++)
        {
            Fire(w, t, plans[i].Target, plans[i].Route, queue: i > 0);
        }
        var f = Watch(w, Deploy + 4 * Interval + 900);
        Assert.Equal(4, f.Missiles.Count);
        var launches = f.Events.Where(e => e.E.Kind == IncomingEventKind.Launched).ToList();
        Assert.Equal(4, launches.Count);
        Assert.All(launches, e => Assert.Equal(0u, e.E.Warned)); // no warning until an enemy sees it
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(plans[i].Target, f.Missiles[i].Cruise!.Route.Target);
            Assert.Equal(plans[i].Route, f.Missiles[i].Cruise!.Route.Waypoints);
            Assert.Contains(f.Blasts, b => b.D.Projectile == f.Missiles[i].Id && Cell.Of(b.D.At) == plans[i].Target);
            if (i > 0)
            {
                Assert.Equal((uint)Interval, launches[i].Tick - launches[i - 1].Tick); // one every 2 s
            }
        }
        Assert.Equal(0, t.Charges[0]);
        Assert.Equal(OrderKind.Idle, t.Order.Kind);
        Assert.Empty(t.CruiseRoutes);
    }

    [Fact]
    public void EachLaunchRevealsTheTruckForTwentySeconds()
    {
        var w = NewWorld(400, 100, map: null);
        var t = Truck(w, 20, 50);
        Fire(w, t, new Cell(300, 50));
        var at = 0u;
        Watch(w, Deploy + 60, ww => at = at == 0 && ww.Projectiles.Any(p => p.Cruise is not null) ? ww.Tick : at);
        Assert.NotEqual(0u, at);
        Assert.True(w.RevealedBy(t, 1));
        w.Run((int)(at + 400 - w.Tick - 2));
        Assert.True(w.RevealedBy(t, 1));
        w.Run(4);
        Assert.False(w.RevealedBy(t, 1));
    }

    // ---- The flight over terrain ----

    // Hills: a 400 x 400 map of rolling hills up to 25 m (slopes past 60% here and there) with patches of grove (6 m treetops); the
    // truck's corner flat.
    private static TerrainMap Hills() => Map(400, 400, (x, y) =>
    {
        if (x < 30 && Math.Abs(y - 200) < 30)
        {
            return 0;
        }
        static int Tri(int v, int period) => Math.Abs(v % period - period / 2) * 1000 / (period / 2); // 1000 down to 0 and back
        return 25 * M * Tri(x + 7, 64) / 1000 * Tri(y + 3, 90) / 1000;
    }, (x, y) => (byte)((x / 16 + y / 16) % 3 == 0 ? 2 : 0));

    [Fact]
    public void TerrainClearanceOverTheHillsWithinTheClimbAndDescentLimits()
    {
        var w = NewWorld(400, 400, map: Hills());
        var t = Truck(w, 10, 200);
        Cell[] route = [new Cell(120, 120), new Cell(220, 300), new Cell(330, 180)];
        var target = new Cell(380, 220);
        var check = w.CheckCruiseRoute(t, route, target);
        Assert.True(check.Ok, $"{check}");
        Fire(w, t, target, route);
        Projectile? m = null;
        var predicted = new List<FlightPoint>();
        var flown = new List<(int Age, Vec Pos, long Alt)>();
        var f = Watch(w, Deploy + 600, ww =>
        {
            if (m is null && ww.Projectiles.FirstOrDefault(p => p.Cruise is not null) is { } p0)
            {
                m = p0;
                for (var k = 0; k <= p0.Cruise!.ImpactAge - p0.Age; k++)
                {
                    predicted.Add(ww.FlightPointAt(p0, ww.Tick + (uint)k)!.Value); // the whole flight, drawn ahead
                }
                flown.Add((p0.Age, p0.Pos, p0.Altitude.Raw));
            }
            else if (m is { Done: false })
            {
                flown.Add((m.Age, m.Pos, m.Altitude.Raw));
            }
        });
        Assert.NotNull(m);
        var c = m.Cruise!;
        Assert.Contains(f.Blasts, b => b.D.Projectile == m.Id && Cell.Of(b.D.At) == target); // it got there
        long minAbove = long.MaxValue, sumAbove = 0, n = 0;
        long maxClimb = 0, maxDrop = 0;
        for (var i = 0; i < flown.Count; i++)
        {
            var (age, pos, alt) = flown[i];
            Assert.Equal(predicted[i].Pos, pos); // the sim flies what FlightPointAt draws
            Assert.Equal(predicted[i].Altitude.Raw, alt);
            var above = alt - w.GroundHeight(pos).Raw;
            Assert.True(above > 0, $"age {age}: {above} raw over the ground");
            if (i == 0)
            {
                continue;
            }
            var (_, prev, prevAlt) = flown[i - 1];
            for (var k = 1; k <= 16; k++)
            {
                // Between the ticks, on the straight line from one to the next: never in the ground.
                var q = new Vec(Fixed.FromRaw(prev.X.Raw + (pos.X.Raw - prev.X.Raw) * k / 16), Fixed.FromRaw(prev.Y.Raw + (pos.Y.Raw - prev.Y.Raw) * k / 16));
                Assert.True(prevAlt + (alt - prevAlt) * k / 16 > w.GroundHeight(q).Raw, $"age {age}: in the ground between ticks");
            }
            if (age > c.BoostTicks + 1 && age <= c.DiveAge)
            {
                maxClimb = Math.Max(maxClimb, alt - prevAlt);
                maxDrop = Math.Max(maxDrop, prevAlt - alt);
                Assert.True(above >= 15 * M, $"age {age}: {above / (double)M:F1} m over the ground");
                (minAbove, sumAbove, n) = (Math.Min(minAbove, above), sumAbove + above, n + 1);
                var step = (pos - prev).Len().Raw;
                Assert.InRange(step, 1740, 1793); // 35 cells/s along its path (a chord on the turns)
            }
        }
        Assert.InRange(maxClimb, 1, 20 * M / 20); // 20 m/s
        Assert.InRange(maxDrop, 1, 15 * M / 20); // 15 m/s
        output.WriteLine($"{n} ticks cruising: {minAbove / (double)M:F1} to mean {sumAbove / (double)n / M:F1} m over the ground; climb {maxClimb * 20 / (double)M:F1} m/s, drop {maxDrop * 20 / (double)M:F1} m/s; route {check.Length.Raw / (double)Fixed.OneRaw:F0} cells");
        // The boost: straight up 40 m in 2 s; the pop-up to 40 m over the ground where the dive starts.
        Assert.Equal(predicted[0].Pos, predicted[c.BoostTicks - 1].Pos);
        var top = w.FlightPointAt(m, 0, 0); // long gone: its point
        Assert.NotNull(top);
        var diveStart = predicted[c.DiveAge - 1];
        Assert.InRange(diveStart.Altitude.Raw - w.GroundHeight(diveStart.Pos).Raw, 38 * M, 70 * M);
    }

    [Fact]
    public void ItTurnsAtEachWaypointWithASixCellRadius()
    {
        var w = NewWorld(400, 400);
        var t = Truck(w, 20, 200);
        var corner = new Cell(200, 200);
        Fire(w, t, new Cell(200, 380), [corner]); // a right angle at (200, 200)
        var pts = new List<Vec>();
        Watch(w, Deploy + 300, ww =>
        {
            if (ww.Projectiles.FirstOrDefault(p => p.Cruise is not null) is { } p)
            {
                pts.Add(p.Pos);
            }
        });
        // The nearest it passes the corner: R (1 - 1/sqrt 2) out = 1.76 cells off for 6 cells at 90 degrees (not over it).
        var c = corner.Center();
        var near = pts.Min(p => (p - c).Len().Raw);
        output.WriteLine($"closest to the corner {near / (double)Fixed.OneRaw:F2} cells");
        Assert.InRange(near, 1500, 2600);
    }

    [Fact]
    public void ALegItCannotClearIsRefusedAndNamed()
    {
        // A wall 120 m high across x 100 to 104 for y below 40; beside it (y 40 and up) the ground is flat.
        var map = Map(300, 80, (x, y) => y < 40 && x is >= 100 and <= 104 ? 120 * M : 0);
        var w = NewWorld(300, 80, map: map);
        var t = Truck(w, 5, 20);
        var over = w.CheckCruiseRoute(t, [new Cell(60, 20)], new Cell(200, 20));
        Assert.Equal((CruiseRouteProblem.Terrain, 1), (over.Problem, over.Leg)); // the second leg, from (60, 20) over the wall
        Assert.Equal((CruiseRouteProblem.Terrain, 0), (w.CheckCruiseRoute(t, null, new Cell(200, 20)).Problem, w.CheckCruiseRoute(t, null, new Cell(200, 20)).Leg));
        Assert.True(w.CheckCruiseRoute(t, [new Cell(60, 60), new Cell(150, 60)], new Cell(200, 20)).Ok); // round it
        Fire(w, t, new Cell(200, 20), [new Cell(60, 20)]);
        var f = Watch(w, Deploy + 40);
        Assert.Contains(f.Refusals, r => r.Aircraft == t.Id && r.Reason == OrderRefusalReason.RouteTerrain);
        Assert.Empty(f.Missiles);
        Fire(w, t, new Cell(200, 20), [new Cell(60, 60), new Cell(150, 60)]);
        f = Watch(w, Deploy + 300);
        Assert.Single(f.Missiles);
        Assert.Contains(f.Blasts, b => b.D.Projectile == f.Missiles[0].Id && Cell.Of(b.D.At) == new Cell(200, 20));
    }

    // ---- Being seen ----

    // Exposure: a Sentinel radar truck set up at (280, 200); player 2's truck at (100, 200) on a 60 m hilltop or on the floor of a
    // valley 30 m deep (x 60 to 170), its missile flying to (150, 200) and on north to (150, 40). The radar sees this signature 144
    // cells out (360 x 40%). The boost and the descent to 30 m over the ground under it (about 23 cells) stay farther than that
    // either way. From the hill the missile is still far over the falling ground (more than 30 m: normal sensing) at 130 cells:
    // seen. From the valley it is low long before: beyond 120 cells and behind the valley's rim, hidden.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHilltopLaunchIsSeenAndAValleyLaunchIsHidden(bool hill)
    {
        var map = hill
            ? Map(300, 400, (x, y) => Math.Clamp(22 - (int)Math.Sqrt((x - 100) * (x - 100) + (y - 200) * (y - 200)), 0, 10) * 6 * M) // 60 m, 12 cells of flat top
            : Map(300, 400, (x, _) => x is >= 60 and <= 170 ? 0 : Math.Min(10, Math.Min(Math.Abs(x - 60), Math.Abs(x - 170))) * 3 * M); // a valley 110 cells wide
        var w = NewWorld(300, 400, map: map);
        var r = w.SpawnCrewed(1, "radar_truck", 280, 200).Vehicle;
        SetUp(w, r);
        var t = Truck(w, 100, 200);
        Fire(w, t, new Cell(150, 40), [new Cell(150, 200)]);
        var f = Watch(w, Deploy + 260);
        var m = Assert.Single(f.Missiles);
        Assert.Contains(f.Blasts, b => b.D.Projectile == m.Id);
        var seen = f.Events.Where(e => e.E.Kind == IncomingEventKind.Seen && e.E.Projectile == m.Id).ToList();
        if (hill)
        {
            Assert.NotEmpty(f.Seen);
            Assert.Equal(2u, Assert.Single(seen).E.Warned); // once, for player 1
            Assert.NotEmpty(f.Warned); // his warning
            Assert.Equal(2u, m.Cruise!.Seen);
            output.WriteLine($"seen {f.Seen.Count} ticks, warned {f.Warned.Count}");
        }
        else
        {
            Assert.Empty(f.Seen);
            Assert.Empty(seen);
            Assert.Empty(f.Warned);
            Assert.Equal(0u, m.Cruise!.Seen);
        }
    }

    // Behind a ridge: a radar truck at (150, 40), a missile flying east along y = 150 (110 cells off its line, inside 120) 15 m up;
    // a 40 m ridge along y = 95 hides it; on open ground it is seen while within 120 cells, and its enemy stays warned 5 s after.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARidgeHidesItFromARadarAndTheOpenDoesNot(bool ridge)
    {
        var map = Map(420, 220, (_, y) => ridge ? Math.Max(0, 8 - Math.Abs(y - 95)) * 5 * M : 0);
        var w = NewWorld(420, 220, map: map);
        var r = w.SpawnCrewed(1, "radar_truck", 150, 40).Vehicle;
        SetUp(w, r);
        var t = Truck(w, 10, 150);
        Fire(w, t, new Cell(410, 150));
        var tracked = 0;
        var f = Watch(w, Deploy + 400, ww => tracked += ww.Projectiles.Count(p => p.Cruise is not null && ww.AirTracked(1, p.Id)));
        var m = Assert.Single(f.Missiles);
        Assert.Contains(f.Blasts, b => b.D.Projectile == m.Id);
        if (ridge)
        {
            Assert.Empty(f.Seen);
            Assert.Empty(f.Warned);
            Assert.Equal(0, tracked);
            return;
        }
        Assert.NotEmpty(f.Seen);
        Assert.True(tracked > 0);
        var first = f.Seen.Min(s => s.Tick);
        var last = f.Seen.Max(s => s.Tick);
        Assert.DoesNotContain(f.Warned, x => x.Tick < first); // no warning before it is seen
        Assert.Contains(f.Warned, x => x.Tick == last + 99); // 5 s after the last sighting
        Assert.DoesNotContain(f.Warned, x => x.Tick > last + 100);
        output.WriteLine($"seen from tick {first} to {last}, warned to {f.Warned.Max(x => x.Tick)}");
    }

    // In the woods: an AA soldier (his eyes: 150 x 40% = 60 cells against it) at (200, 105), the missile flying east along y = 150
    // 15 m over open ground; a band of dense forest (10 m treetops) from y 110 to 125 between them hides it, without it he sees it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AForestHidesItFromASoldier(bool forest)
    {
        var map = Map(420, 220, (_, _) => 0, (_, y) => (byte)(forest && y is >= 110 and <= 125 ? 3 : 0));
        var w = NewWorld(420, 220, map: map);
        w.Spawn(1, "manpads", 200, 105);
        var t = Truck(w, 10, 150);
        Fire(w, t, new Cell(410, 150));
        var f = Watch(w, Deploy + 400);
        Assert.Single(f.Missiles);
        Assert.Equal(forest, f.Seen.Count == 0);
    }

    [Fact]
    public void AnAircraftSensorIgnoresTheLowFlightRule()
    {
        // A sensor in the air looks down and ignores the rule: a multirole jet orbiting over the missile's line behind a 40 m ridge
        // from the radar sees it (its own air vision, 110 x 40% = 44 cells level), the radar does not; fighters never engage it.
        var w2 = NewWorld(420, 220, map: Map(420, 220, (_, y) => Math.Max(0, 8 - Math.Abs(y - 95)) * 5 * M));
        var jet = w2.Spawn(1, "Multirole 500 lb", 200, 150, Facing.East);
        w2.Order(1, CommandKind.Move, [jet], cell: new Cell(200, 150));
        w2.Run(200);
        var t2 = Truck(w2, 10, 150);
        Fire(w2, t2, new Cell(410, 150));
        var shots = 0;
        var f2 = Watch(w2, Deploy + 400, ww => shots += ww.Projectiles.Count(p => p.Shooter == jet.Id));
        Assert.Single(f2.Missiles);
        Assert.NotEmpty(f2.Seen); // the jet saw it (the radar alone does not: ARidgeHidesItFromARadarAndTheOpenDoesNot)
        Assert.Equal(0, shots);
        Assert.Contains(f2.Blasts, b => b.D.Projectile == f2.Missiles[0].Id);
    }

    // ---- Being shot down ----

    // Defence: player 1's Patriot type (or THAAD type) and its radar truck set up at (300, 150) on open ground; player 2's missile
    // flies at a point beside them from 290 cells west.
    private static Flight Defend(string kind, ulong seed)
    {
        var w = NewWorld(420, 300, seed, Map(420, 300, (_, _) => 0));
        var launcher = w.SpawnCrewed(1, kind, 300, 150).Vehicle;
        var r = w.SpawnCrewed(1, "radar_truck", 305, 160).Vehicle;
        SetUp(w, launcher, r);
        var t = Truck(w, 10, 150);
        Fire(w, t, new Cell(300, 140));
        return Watch(w, Deploy + 400);
    }

    [Fact]
    public void APatriotKillsOneItSees()
    {
        var kills = 0;
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var f = Defend("patriot_launcher", seed);
            var m = Assert.Single(f.Missiles);
            var seen = f.Events.Single(e => e.E.Kind == IncomingEventKind.Seen).Tick;
            if (f.Events.FirstOrDefault(e => e.E.Kind == IncomingEventKind.Intercepted) is { E.Projectile: var id } down && id == m.Id)
            {
                kills++;
                Assert.True(down.Tick > seen);
                Assert.DoesNotContain(f.Blasts, b => b.D.Projectile == m.Id); // brought down: no blast at its point
            }
            output.WriteLine($"seed {seed}: seen at {seen}, {kills} kills so far");
        }
        Assert.True(kills >= 3, $"{kills} kills in 6");
    }

    [Fact]
    public void TheThaadTypeNeverEngagesItLow()
    {
        var w = NewWorld(420, 300, 1, Map(420, 300, (_, _) => 0));
        var launcher = w.SpawnCrewed(1, "thaad_launcher", 300, 150).Vehicle;
        var r = w.SpawnCrewed(1, "radar_truck", 305, 160).Vehicle;
        SetUp(w, launcher, r);
        var t = Truck(w, 10, 150);
        Fire(w, t, new Cell(300, 140));
        var shots = 0;
        var f = Watch(w, Deploy + 400, ww => shots += ww.Projectiles.Count(p => p.Shooter == launcher.Id));
        Assert.NotEmpty(f.Seen);
        Assert.Equal(0, shots);
        Assert.Contains(f.Blasts, b => b.D.Projectile == f.Missiles.Single().Id);
    }

    // ---- The slope rule ----

    // Slope: grade 12% for x below 20, 18% from x 20 (corner heights rising east).
    private static TerrainMap Slope() => Map(60, 40, (x, _) => Math.Min(x, 20) * 123 + Math.Max(0, x - 20) * 190);

    [Fact]
    public void DeployIsRefusedOnASteepSlopeForThisTruckOnly()
    {
        var w = NewWorld(60, 40, map: Slope());
        var steep = Truck(w, 19, 10); // its own cell 12%, its footprint reaching the 18% cells
        var gentle = Truck(w, 8, 25);
        var other = w.SpawnCrewed(2, "hypersonic_truck", 19, 30).Vehicle; // no slope rule
        Assert.Equal((12, 18), (w.Ground!.Grade(new Cell(19, 10)), w.FootprintGrade(steep.Def, new Cell(19, 10))));
        Assert.False(w.DeploySlopeOk(steep.Def, new Cell(19, 10)));
        Assert.True(w.DeploySlopeOk(steep.Def, new Cell(8, 25)));
        Assert.True(w.DeploySlopeOk(other.Def, new Cell(19, 30)));
        w.Order(2, CommandKind.Deploy, [steep, gentle, other]);
        var f = Watch(w, 240);
        Assert.Contains(f.Refusals, r => r.Aircraft == steep.Id && r.Reason == OrderRefusalReason.Slope);
        Assert.DoesNotContain(f.Refusals, r => r.Aircraft != steep.Id);
        Assert.Equal(DeployState.Stowed, steep.Deploy);
        Assert.Equal(new Cell(19, 10), Cell.Of(steep.Pos)); // refused, not driven off
        Assert.Equal(DeployState.Deployed, gentle.Deploy);
        Assert.Equal(DeployState.Deployed, other.Deploy);
        // On a flat map there is no slope.
        Assert.True(NewWorld(40, 40).DeploySlopeOk(steep.Def, new Cell(5, 5)));
    }

    // ---- Replays and determinism ----

    [Fact]
    public void TheReplayRoundTripsTheRoute()
    {
        var setup = TestContent.Sentinel(1);
        var route = new[] { new Cell(10, 20), new Cell(30, 40) };
        var rp = new Replay
        {
            Setup = setup,
            Ticks = 5,
            Commands =
            [
                new Command { Tick = 1, Player = 2, Seq = 1, Kind = CommandKind.AttackGround, Units = [7], Cell = new Cell(50, 60), Route = route, Queue = true },
                new Command { Tick = 2, Player = 2, Seq = 2, Kind = CommandKind.AttackGround, Units = [7], Cell = new Cell(5, 6) },
            ],
        };
        using var buf = new MemoryStream();
        rp.Encode(buf);
        var json = Encoding.UTF8.GetString(buf.ToArray());
        Assert.Equal(1, json.Split("\"route\"").Length - 1); // only the command with a route writes one
        buf.Position = 0;
        var back = Replay.Decode(buf);
        Assert.Equal(route, back.Commands[0].Route);
        Assert.Null(back.Commands[1].Route);
        var w = back.Start(TestContent.Mvp);
        w.Run(5); // it issues and plays
        Assert.Throws<CommandRejectedException>(() => w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Cell = new Cell(1, 1), Route = [.. Enumerable.Repeat(new Cell(1, 1), 65)] }));
    }

    // Battle: the hills, a radar truck and an air defence vehicle of player 1's along the routes, player 2's truck sending 4 missiles.
    private static List<ulong> Battle(ulong seed, Cell lastTarget)
    {
        var w = NewWorld(400, 400, seed, Hills());
        Unit[] defence = [w.SpawnCrewed(1, "radar_truck", 260, 220).Vehicle, w.SpawnCrewed(1, "sam_vehicle", 250, 230).Vehicle];
        w.Order(1, CommandKind.Deploy, defence);
        var t = Truck(w, 10, 200);
        var hashes = new List<ulong>();
        Fire(w, t, new Cell(380, 220), [new Cell(120, 120), new Cell(220, 300)]);
        Fire(w, t, new Cell(300, 230), [new Cell(150, 260)], queue: true);
        Fire(w, t, new Cell(260, 200), [], queue: true);
        Fire(w, t, lastTarget, [new Cell(100, 330), new Cell(300, 330)], queue: true);
        for (var i = 0; i < Deploy + 4 * Interval + 500; i++)
        {
            w.Step();
            Assert.Equal(w.HashReference(), w.Hash());
            hashes.Add(w.Hash());
        }
        return hashes;
    }

    [Fact]
    public void Deterministic()
    {
        var a = Battle(3, new Cell(250, 250));
        Assert.Equal(a, Battle(3, new Cell(250, 250)));
        Assert.NotEqual(a, Battle(3, new Cell(250, 251))); // the route is hashed
    }
}
