using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The Sentinel cruise missile truck (sentinel_cruise_truck; docs/design.md, "Sentinel cruise missile truck"; the owner's spec of
// 2026-10-30): the Resistance's cruise truck (CruiseTests) with its own missile, 25% less destructive (675) and 20% shorter (150
// to 1,600 cells of legs), the same route rule (at most 3 waypoints, at most 45 degrees a turn, the first leg free); 4 missiles one
// every 2 s; reloaded only at a supply depot or a field ammo point, stowed (60 s and 300 a missile), never by a garage's service;
// from the military garage. Player 1 is the Sentinel, player 2 the Resistance.
public class SentinelCruiseTests(ITestOutputHelper output)
{
    private const string Truck = "sentinel_cruise_truck";
    private const int Deploy = 160, Stow = 120, Interval = 40, Reload = 1200, Cost = 300, Y = 40;

    private static WeaponEffect Missile => TestContent.Mvp.Component("sentinel_cruise_missile")!.Effects.OfType<WeaponEffect>().Single();

    private static WeaponEffect Theirs => TestContent.Mvp.Component("cruise_missile")!.Effects.OfType<WeaponEffect>().Single();

    // A w x h world from the Sentinel lobby (player 1 Sentinel, player 2 Resistance), no armies.
    private static World NewWorld(int w, int h, ulong seed = 1)
    {
        var setup = TestContent.Sentinel(seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(w, h), MatchCompiler.Compile(TestContent.Mvp, setup));
    }

    private static Unit Crewed(World w, int x, int y, byte player = 1, string kind = Truck) => w.SpawnCrewed(player, kind, x, y, Facing.East).Vehicle;

    private static void Fire(World w, Unit t, Cell target, IReadOnlyList<Cell>? route = null, bool queue = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = t.Owner, Kind = CommandKind.AttackGround, Units = [t.Id], Cell = target, Route = route, Queue = queue });

    // Missiles: the cruise missiles seen over a run (each once, in launch order), with the tick each first appeared, the blasts and
    // the refusals.
    private static (List<(uint Tick, Projectile P)> Missiles, List<Detonation> Blasts, List<OrderRefusal> Refusals) Watch(World w, int ticks)
    {
        var missiles = new List<(uint, Projectile)>();
        var blasts = new List<Detonation>();
        var refusals = new List<OrderRefusal>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (p.Cruise is not null && missiles.All(m => m.Item2.Id != p.Id))
                {
                    missiles.Add((w.Tick, p));
                }
            }
            blasts.AddRange(w.Detonations);
            refusals.AddRange(w.OrderRefusals);
        }
        return (missiles, blasts, refusals);
    }

    // ---- Content ----

    [Fact]
    public void TheContentNumbers()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype(Truck)!;
        var r = c.Archetype("cruise_truck")!;
        Assert.Equal("Cruise missile truck", a.Name);
        Assert.Equal((3500, 1600, 420), (a.Cost, a.BuildTime, a.MaxHp)); // 80 s
        Assert.Equal(c.Archetype("hypersonic_truck")!.Armor, a.Armor); // medium_vehicle
        Assert.Equal(Fixed.Frac(16, 200), a.Speed);
        Assert.Equal((Deploy, Stow, 15), (a.DeployTicks, a.StowTicks, a.DeploySlope));
        Assert.True(a.HullAims);
        Assert.Equal((r.Radius, r.TurnRate, r.Locomotor, r.HullArc), (a.Radius, a.TurnRate, a.Locomotor, a.HullArc));
        Assert.Equal((r.Crew!.Tags, r.Crew.EnterRange, r.Crew.Survival, r.Crew.ExitDamage, 0), (a.Crew!.Tags, a.Crew.EnterRange, a.Crew.Survival, a.Crew.ExitDamage, a.Crew.Passengers));
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        Assert.False(c.Faction("resistance")!.Archetypes[a.Index]);
        Assert.True(c.Archetype("military_garage")!.Production!.Builds(a.Tags));

        var m = Missile;
        var t = Theirs;
        Assert.Equal((675, "heavy_missile"), (m.Damage, m.Warhead.Id)); // 900 x 0.75
        Assert.Equal(t.Damage * 3 / 4, m.Damage);
        Assert.Equal((t.Splash, t.SplashInner, t.SplashOuterPercent), (m.Splash, m.SplashInner, m.SplashOuterPercent));
        Assert.Equal((4, 1, Interval), (m.Ammo, m.SalvoSize, m.Reload));
        Assert.Equal(400, m.RevealTicks);
        Assert.Equal(InterceptClass.Cruise, m.Intercept);
        Assert.Equal(new DepotReload(Reload, 20, Cost, true, true), m.DepotReload); // stowed, no garage service
        Assert.Equal((t.ProjectileSpeed, t.LowSight, t.Signature, t.Fire), (m.ProjectileSpeed, m.LowSight, m.Signature, m.Fire));
        Assert.Equal(Fixed.FromInt(1600), m.Range);
        var cf = m.Cruise!;
        Assert.Equal((Fixed.FromInt(150), Fixed.FromInt(1600), 3, 45), (cf.RouteMin, cf.RouteMax, cf.MaxWaypoints, cf.MaxTurn));
        Assert.Equal(t.Cruise! with { RouteMax = Fixed.FromInt(1600) }, cf); // the flight is the Resistance's: only the range differs
    }

    // ---- The route ----

    [Fact]
    public void RoutesFromOneHundredFiftyToSixteenHundredCells()
    {
        var w = NewWorld(1700, 400);
        var t = Crewed(w, 5, 30);
        CruiseRouteCheck Check(Cell at, IReadOnlyList<Cell>? route = null) => w.CheckCruiseRoute(t, route, at);
        Assert.Equal(CruiseRouteProblem.TooShort, Check(new Cell(5 + 149, 30)).Problem);
        Assert.True(Check(new Cell(5 + 150, 30)).Ok);
        Assert.True(Check(new Cell(5 + 1600, 30)).Ok);
        Assert.Equal(CruiseRouteProblem.TooLong, Check(new Cell(5 + 1601, 30)).Problem); // the Resistance's goes to 2,000
        Assert.True(w.CheckCruiseRoute(Crewed(w, 5, 50, 2, "cruise_truck"), null, new Cell(5 + 1601, 50)).Ok);
        // The legs count: 1,560 cells away by a dog-leg of 341 + 1,000 + 315 is past 1,600.
        Assert.True(Check(new Cell(5 + 1560, 30)).Ok);
        Assert.Equal(CruiseRouteProblem.TooLong, Check(new Cell(5 + 1560, 30), [new Cell(300, 200), new Cell(1300, 200)]).Problem);

        // The order beyond 1,600 is refused (nothing fires).
        Fire(w, t, new Cell(5 + 1601, 30));
        var (missiles, _, refusals) = Watch(w, Deploy + 60);
        Assert.Contains(refusals, x => x.Aircraft == t.Id && x.Reason == OrderRefusalReason.RouteInvalid);
        Assert.Empty(missiles);
        Assert.Equal(4, t.Charges[0]);
    }

    [Fact]
    public void ThreeWaypointsAtMostFortyFiveDegreesATurn()
    {
        var w = NewWorld(500, 400);
        var t = Crewed(w, 100, 200);
        CruiseRouteCheck Check(Cell at, params Cell[] route) => w.CheckCruiseRoute(t, route, at);
        // East 100 cells to (200, 200): on at 45 degrees is taken, at 50 it is refused and the waypoint named.
        Assert.True(Check(new Cell(300, 300), new Cell(200, 200)).Ok);
        var sharp = Check(new Cell(300, 319), new Cell(200, 200)); // 50.0 degrees
        Assert.Equal((CruiseRouteProblem.SharpTurn, 0), (sharp.Problem, sharp.Leg));
        // Three waypoints, not four.
        Cell[] three = [new Cell(200, 200), new Cell(300, 300), new Cell(300, 340)];
        Assert.True(Check(new Cell(260, 380), three).Ok);
        Assert.Equal(CruiseRouteProblem.TooManyWaypoints, Check(new Cell(220, 390), [.. three, new Cell(260, 380)]).Problem);

        // The orders: the 50-degree route refused with RouteTurn, the 45-degree one flies and lands.
        Fire(w, t, new Cell(300, 319), [new Cell(200, 200)]);
        var (missiles, _, refusals) = Watch(w, Deploy + 60);
        Assert.Contains(refusals, x => x.Aircraft == t.Id && x.Reason == OrderRefusalReason.RouteTurn);
        Assert.Empty(missiles);
        Fire(w, t, new Cell(300, 300), [new Cell(200, 200)]);
        var (flown, blasts, _) = Watch(w, Deploy + 400);
        var m = Assert.Single(flown).P;
        Assert.Contains(blasts, b => b.Projectile == m.Id && Cell.Of(b.At) == new Cell(300, 300));
    }

    // ---- Firing ----

    [Fact]
    public void FourMissilesTwoSecondsApart()
    {
        var w = NewWorld(500, 500);
        var t = Crewed(w, 250, 250);
        var targets = new[] { new Cell(250, 60), new Cell(450, 250), new Cell(250, 450), new Cell(40, 250) };
        for (var i = 0; i < targets.Length; i++)
        {
            Fire(w, t, targets[i], queue: i > 0);
        }
        var (missiles, blasts, _) = Watch(w, Deploy + 4 * Interval + 900);
        Assert.Equal(4, missiles.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(targets[i], missiles[i].P.Cruise!.Route.Target);
            Assert.Contains(blasts, b => b.Projectile == missiles[i].P.Id && Cell.Of(b.At) == targets[i]);
            if (i > 0)
            {
                Assert.Equal((uint)Interval, missiles[i].Tick - missiles[i - 1].Tick);
            }
        }
        Assert.Equal(0, t.Charges[0]);
        Assert.Equal(OrderKind.Idle, t.Order.Kind);
    }

    [Fact]
    public void ItDoesThreeQuartersOfTheResistancesDamage()
    {
        // Each side's truck fires one missile into a building of the other's (a vehicle garage, 2,500 HP, the building armour: the
        // heavy_missile table's 220%): 1,485 from the Sentinel's, 1,980 from the Resistance's.
        var lost = new int[2];
        for (var side = 0; side < 2; side++)
        {
            var w = NewWorld(400, 120);
            var (me, them) = side == 0 ? ((byte)1, (byte)2) : ((byte)2, (byte)1);
            var t = Crewed(w, 10, 60, me, side == 0 ? Truck : "cruise_truck");
            var g = w.Spawn(them, "vehicle_garage", 300, 50);
            var hp = g.HP;
            Fire(w, t, Cell.Of(g.Pos));
            var (missiles, blasts, _) = Watch(w, Deploy + 300);
            var m = Assert.Single(missiles).P;
            Assert.Contains(blasts, b => b.Projectile == m.Id);
            lost[side] = hp - g.HP;
        }
        output.WriteLine($"Sentinel {lost[0]}, Resistance {lost[1]}");
        var bld = TestContent.Mvp.Archetype("vehicle_garage")!.Armor;
        Assert.Equal(Missile.Warhead.Damage(675, bld, 100), lost[0]);
        Assert.Equal((1485, 1980), (lost[0], lost[1]));
    }

    // ---- Reload ----

    [Theory]
    [InlineData("supply_depot")]
    [InlineData("facp")]
    public void ItReloadsStowedAtADepotOrAFieldAmmoPoint(string store)
    {
        var w = NewWorld(420, 80);
        var t = Crewed(w, 5, Y);
        var depot = w.Spawn(1, store, 3, Y - 10);
        depot.Stock = 3000;
        for (var i = 0; i < 4; i++)
        {
            Fire(w, t, new Cell(400, Y), queue: i > 0);
        }
        w.Run(Deploy + 4 * Interval + 60);
        Assert.Equal(0, t.Charges[0]);
        Assert.NotEqual(DeployState.Stowed, t.Deploy);
        // Empty beside a depot it stows first (6 s), never reloading set up; then 60 s a missile.
        var packing = 0;
        while (t.Deploy != DeployState.Stowed)
        {
            w.Step();
            packing++;
            Assert.Equal(0, t.Charges[0]);
        }
        Assert.InRange(packing, 1, Stow + 40);
        var stowed = w.Tick;
        while (t.Charges[0] == 0 && w.Tick < stowed + Reload + 100)
        {
            w.Step();
        }
        Assert.InRange(w.Tick - stowed, (uint)Reload - 1, (uint)Reload);
        Assert.Equal(1, t.Charges[0]);
        Assert.Equal(3000 - Cost, depot.Stock);
        w.Run(3 * Reload + 10);
        Assert.Equal(4, t.Charges[0]);
        Assert.Equal(3000 - 4 * Cost, depot.Stock);
    }

    [Fact]
    public void TheMilitaryGarageServiceDoesNotReloadIt()
    {
        var w = Arena.NewSentinel(96);
        var g = w.Spawn(1, "military_garage", 20, 10);
        w.Step();
        var t = w.SpawnCrewed(1, Truck, 40, 36).Vehicle;
        t.MutableCharges[0] = 0;
        t.HP = 300;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Service, Units = [t.Id], Target = g.Id });
        var wasIn = false;
        for (var i = 0; i < 3000 && (!wasIn || t.InGarage); i++)
        {
            w.Step();
            wasIn |= t.InGarage;
        }
        Assert.True(wasIn, "it went in (to be repaired)");
        Assert.Equal(t.Def.MaxHp, t.HP); // repaired
        Assert.Equal(0, t.Charges[0]);   // not reloaded
    }

    [Fact]
    public void ItIsBuiltAtTheMilitaryGarage()
    {
        var w = Arena.NewSentinel(96);
        var mg = w.Spawn(1, "military_garage", 20, 10);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [mg.Id], Unit = Truck });
        var built = 0u;
        for (var i = 0; i < 2000 && built == 0; i++)
        {
            w.Step();
            if (w.All(Truck).Count > 0)
            {
                built = w.Tick;
            }
        }
        output.WriteLine($"built at tick {built}");
        Assert.InRange(built, 1600u, 1700u); // 80 s in a bay, then out of the door
        Assert.Equal(1, w.All(Truck).Single().Owner);
    }
}
