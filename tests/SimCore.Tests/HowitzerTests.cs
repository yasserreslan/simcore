using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The howitzer (M777 type; docs/design.md, "Howitzer"; owner's spec 2026-10-24): a 10 x 10 emplacement a worker builds, a crew
// seat any soldier of its side takes (silent without him; he bails out of its destruction), a 155 mm gun with 15 shells (300
// damage, a 3.5-cell splash, 15 to 80 cells, one every 8 s, a slow high arc, scatter 1 to 3 cells) that fires on its own only
// at enemies its side spots and at any point in range on attack ground (Shift: a queue of points), reloaded only by a cargo
// truck that hands over 20 supply a shell and keeps the rest, and a muzzle flash that shows it to every enemy for 10 s.
public class HowitzerTests
{
    private const int Every = 160, Reveal = 200, FeedEvery = 40, Cost = 20, Traverse = 8;

    // A crewed howitzer of player 1 whose footprint's top-left cell is (x, y): its centre is (x + 5, y + 5).
    private static (Unit Gun, Unit Crew) Gun(World w, int x = 10, int y = 60, byte player = 1)
    {
        var g = w.Spawn(player, "howitzer", x, y);
        var c = w.Spawn(player, "Rifleman", x + 5, y + 11);
        World.Board(c, g);
        return (g, c);
    }

    private static Unit Truck(World w, int x, int y, byte player = 1) => w.SpawnCrewed(player, "cargo_truck", x, y, Facing.East).Vehicle;

    private static int Shells(Unit g) => g.Charges[g.Def.Weapons[0].Stock];

    private static void SetShells(Unit g, int n) => g.MutableCharges[g.Def.Weapons[0].Stock] = n;

    // Steps the world and returns (tick, projectile) for every new projectile the gun fired.
    private static List<(uint Tick, Projectile P)> Shots(World w, Unit g, int ticks)
    {
        var seen = new List<(uint, Projectile)>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            foreach (var e in w.EmplacementEvents)
            {
                if (e.Kind == EmplacementEventKind.Fired && e.Unit == g.Id)
                {
                    seen.Add((w.Tick, w.Projectiles.Single(p => p.Id == (uint)e.Value2)));
                }
            }
        }
        return seen;
    }

    private static void Ground(World w, Unit g, Cell at, bool queue = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = g.Owner, Kind = CommandKind.AttackGround, Units = [g.Id], Cell = at, Queue = queue });

    [Fact]
    public void TheContentNumbers()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype("howitzer")!;
        Assert.Equal((10, 10), (a.FootprintW, a.FootprintH));
        Assert.Equal(1200, a.MaxHp);
        Assert.Equal(c.Archetype("supply_depot")!.Armor, a.Armor); // building armour
        Assert.Equal(800, a.BuildTime); // 40 s
        Assert.Equal(1500, a.Cost);
        Assert.Equal(Fixed.FromInt(4), a.Vision); // it relies on spotters
        Assert.True(a.Turret);
        Assert.Equal(Traverse, a.TraverseTicks); // half a turn (16 facings) in 6.4 s
        Assert.NotNull(a.Construction);
        Assert.Equal(0, a.Crew!.Passengers);
        Assert.True(c.Factions.Single(f => f.Id == "resistance").Archetypes[a.Index]);
        Assert.False(c.Factions.Single(f => f.Id == "sentinel").Archetypes[a.Index]);

        var gun = c.Component("howitzer_155")!.Effects.OfType<WeaponEffect>().Single();
        var rockets = c.Component("heavy_artillery_rockets")!.Effects.OfType<WeaponEffect>().Single();
        var mortar = c.Component("mortar_tube")!.Effects.OfType<WeaponEffect>().Single();
        Assert.Equal(300, gun.Damage);
        Assert.Equal(rockets.Warhead, gun.Warhead); // the heavy warhead: 600 to buildings, 360 to armour
        Assert.Equal(Fixed.FromInt(80), gun.Range);
        Assert.Equal(Fixed.FromInt(15), gun.MinRange);
        Assert.True(gun.Range < rockets.Range && gun.Range > mortar.Range); // the same raw scale: 90 / 80 / 30 cells
        Assert.Equal(Every, gun.Reload);
        Assert.Equal(15, gun.Ammo);
        Assert.Equal(Fixed.FromRaw(Fixed.OneRaw * 35 / 10), gun.Splash);
        Assert.Equal((100, 10), (gun.SplashCenter, gun.SplashEdge));
        Assert.Equal(Fixed.FromInt(1), gun.ScatterNear);
        Assert.Equal(Fixed.FromInt(3), gun.Scatter);
        Assert.True(gun.Lobbed); // top attack on directional armour
        Assert.Equal(rockets.Intercept, gun.Intercept); // what intercepts rockets can take its shells
        Assert.Equal(Reveal, gun.RevealTicks);
        Assert.Equal(new TruckReload(Cost, FeedEvery, Fixed.FromInt(15)), gun.TruckReload);
        Assert.Null(gun.DepotReload);
    }

    private static void Build(World w, IEnumerable<Unit> workers, Cell at, string unit = "howitzer") =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [.. workers.Select(u => u.Id)], Cell = at, Unit = unit });

    [Fact]
    public void AWorkerBuildsItInFortySecondsWithFifteenShellsAndNoCrewUntilFinished()
    {
        var w = Arena.New(64);
        var worker = w.Spawn(1, "worker", 19, 20);
        var soldier = w.Spawn(1, "Rifleman", 25, 34);
        Build(w, [worker], new Cell(20, 20));
        w.Step();
        var site = Assert.Single(w.All("howitzer"));
        Assert.False(site.IsBuilt);
        w.Order(1, CommandKind.Enter, [soldier], target: site.Id);
        w.Run(20);
        Assert.Equal(0u, site.Driver); // no crew seat on a site
        Assert.Equal(0u, soldier.Inside);
        var t = 1;
        while (!site.IsBuilt && t < 2000)
        {
            w.Step();
            t++;
        }
        Assert.InRange(t, 760, 830); // 800 ticks of one worker's work (40 s)
        Assert.Equal(15, Shells(site));
        Assert.Equal(site.Def.MaxHp, site.HP);
        w.Order(1, CommandKind.Enter, [soldier], target: site.Id);
        w.Run(100);
        Assert.Equal(soldier.Id, site.Driver);
        Assert.Contains(w.EmplacementOf(site)!.Value.Crew, new[] { soldier.Id });
    }

    [Fact]
    public void SilentWithoutItsCrewmanAndAnySoldierOfItsSideCanTakeTheSeat()
    {
        var w = Arena.New(128);
        var g = w.Spawn(1, "howitzer", 10, 60);
        var target = w.Spawn(2, "cargo_truck", 65, 65);
        w.Spawn(1, "worker", 60, 65); // a spotter
        Ground(w, g, new Cell(65, 65));
        Assert.Empty(Shots(w, g, 300));
        Assert.Equal(OrderKind.Idle, g.Order.Kind); // no orders without a crewman
        var worker = w.Spawn(1, "worker", 15, 71);
        w.Order(1, CommandKind.Enter, [worker], target: g.Id);
        w.Run(60);
        Assert.Equal(0u, g.Driver); // a worker is no gunner
        var enemy = w.Spawn(2, "Rifleman", 15, 72);
        w.Order(2, CommandKind.Enter, [enemy], target: g.Id);
        w.Run(60);
        Assert.Equal(0u, g.Driver); // nor is an enemy
        var marksman = w.Spawn(1, "Marksman", 15, 72);
        w.Order(1, CommandKind.Enter, [marksman], target: g.Id);
        var events = new List<EmplacementEvent>();
        for (var i = 0; i < 60 && g.Driver == 0; i++)
        {
            w.Step();
            events.AddRange(w.EmplacementEvents);
        }
        Assert.Equal(marksman.Id, g.Driver);
        Assert.Contains(events, e => e.Kind == EmplacementEventKind.CrewIn && e.Unit == g.Id && e.Other == marksman.Id);
        var shots = Shots(w, g, 300);
        Assert.NotEmpty(shots); // manned: it fires on its own at the spotted truck
        Assert.Equal(target.Id, shots[0].P.Target);
        w.Order(1, CommandKind.Exit, [g]);
        w.Step();
        Assert.Contains(w.EmplacementEvents, e => e.Kind == EmplacementEventKind.CrewOut && e.Other == marksman.Id);
        Assert.Equal(0u, g.Driver);
        Assert.Equal(0u, marksman.Inside);
        Assert.False(Within(g, marksman.Pos)); // out beside the footprint, not in it
        Assert.Empty(Shots(w, g, 400));
    }

    private static bool Within(Unit g, Vec p) =>
        Math.Abs(p.X.Raw - g.Pos.X.Raw) < 5 * Fixed.OneRaw && Math.Abs(p.Y.Raw - g.Pos.Y.Raw) < 5 * Fixed.OneRaw;

    [Fact]
    public void OnItsOwnItFiresOnlyAtWhatASpotterSees()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w);
        var target = w.Spawn(2, "cargo_truck", 65, 65); // 50 cells east, far past the gun's 4 cells of sight
        Assert.Empty(Shots(w, g, 400));
        Assert.Equal(0u, g.AutoTarget);
        w.Spawn(1, "worker", 60, 65); // a spotter 5 cells from it
        var shots = Shots(w, g, 300);
        Assert.NotEmpty(shots);
        Assert.Equal(target.Id, shots[0].P.Target);
        // The gun turns from north to east at one facing every 8 ticks (it fires within a facing of the target) before its first round.
        Assert.InRange((int)shots[0].Tick - 401, 6 * Traverse + 1, 8 * Traverse + 2 * (int)World.VisionTicks + Traverse);
    }

    [Fact]
    public void AttackGroundFiresBlindAtAnyPointInRangeUntilTheFifteenShellsAreGone()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w);
        Ground(w, g, new Cell(70, 65)); // 55 cells east: nobody sees it
        var shots = Shots(w, g, 100 + 16 * Every);
        Assert.Equal(15, shots.Count);
        for (var i = 1; i < shots.Count; i++)
        {
            Assert.Equal(Every, (int)(shots[i].Tick - shots[i - 1].Tick)); // one every 8 s: 15 last 2 minutes
        }
        Assert.Equal(0, Shells(g));
        Assert.Equal(OrderKind.Idle, g.Order.Kind); // dry: the order ends
        Assert.Equal(0, w.EmplacementOf(g)!.Value.Rounds);
    }

    [Fact]
    public void ItTakesOnlyPointsBetweenItsMinimumAndItsRange()
    {
        var w = Arena.New(200);
        var (g, _) = Gun(w, 20, 95); // centre (25, 100)
        Ground(w, g, new Cell(25 + 10, 100)); // 10.5 cells: inside the 15
        w.Step();
        Assert.Equal(OrderKind.Idle, g.Order.Kind);
        Ground(w, g, new Cell(25 + 81, 100)); // 81.5 cells: past the 80
        w.Step();
        Assert.Equal(OrderKind.Idle, g.Order.Kind);
        Ground(w, g, new Cell(25 + 79, 100));
        w.Step();
        Assert.Equal(OrderKind.AttackGround, g.Order.Kind);
        Ground(w, g, new Cell(25 + 16, 100));
        w.Step();
        Assert.Equal(new Cell(41, 100), g.Order.Cell);
        Assert.Equal(World.GroundShot.TooClose, w.ShotAt(g, new Cell(25 + 14, 100).Center())); // rings round its centre
        Assert.Equal(World.GroundShot.OutOfRange, w.ShotAt(g, new Cell(25, 100 - 81).Center()));
        Assert.Equal(World.GroundShot.InRange, w.ShotAt(g, new Cell(25, 100 + 79).Center()));
    }

    [Fact]
    public void NoAutomaticShotInsideTheMinimumRange()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w);
        w.Spawn(2, "cargo_truck", 15, 77); // 12 cells south of its centre: too close
        w.Spawn(1, "worker", 13, 77);
        Assert.Empty(Shots(w, g, 300));
        var far = w.Spawn(2, "cargo_truck", 15, 85); // 20 cells
        w.Spawn(1, "worker", 13, 85);
        var shots = Shots(w, g, 400);
        Assert.NotEmpty(shots);
        Assert.All(shots, s => Assert.Equal(far.Id, s.P.Target));
    }

    [Fact]
    public void ScatterGrowsFromOneCellNearToThreeAtFullRange()
    {
        foreach (var (dx, max) in new[] { (16, Fixed.OneRaw * 11 / 10), (79, 3 * Fixed.OneRaw) })
        {
            var w = Arena.New(200);
            var (g, _) = Gun(w, 20, 95);
            var aim = new Cell(25 + dx, 100);
            Ground(w, g, aim);
            var lands = new List<Vec>();
            for (var i = 0; i < 100 + 15 * Every; i++)
            {
                w.Step();
                lands.AddRange(w.EmplacementEvents.Where(e => e.Kind == EmplacementEventKind.Fired).Select(e => e.At));
            }
            Assert.Equal(15, lands.Count);
            var spread = lands.Max(p => Math.Max(Math.Abs(p.X.Raw - aim.Center().X.Raw), Math.Abs(p.Y.Raw - aim.Center().Y.Raw)));
            Assert.True(spread <= max, $"{dx}: {spread}");
            Assert.True(spread > max / 2, $"{dx}: {spread}"); // it does scatter
        }
    }

    [Theory]
    [InlineData(16, 30, 40)]
    [InlineData(79, 150, 175)]
    public void TheShellFliesSlowlyOnAHighArc(int dx, int min, int max)
    {
        var w = Arena.New(200);
        var (g, _) = Gun(w, 20, 95);
        Ground(w, g, new Cell(25 + dx, 100));
        uint fired = 0, shell = 0;
        for (var i = 0; i < 600; i++)
        {
            w.Step();
            if (shell == 0 && w.EmplacementEvents.FirstOrDefault(e => e.Kind == EmplacementEventKind.Fired) is { Value2: > 0 } f)
            {
                (fired, shell) = (w.Tick, (uint)f.Value2);
            }
            if (shell != 0 && w.Detonations.Any(d => d.Projectile == shell))
            {
                break;
            }
        }
        Assert.InRange((int)(w.Tick - fired), min, max); // about 10 cells/s: 8 s at full range, time to drive out of it
    }

    [Fact]
    public void AShellKillsWhatItLandsOnAndSparesWhatStandsPastItsSplash()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w);
        var hit = w.Spawn(2, "Rifleman", 40, 65); // 25 cells east of its centre: scatter about 1.3 cells
        var spared = w.Spawn(2, "Rifleman", 40, 72); // 7 cells from the aim: past 3.5 of splash and the scatter
        Ground(w, g, new Cell(40, 65));
        Shots(w, g, 64 + 120);
        Assert.False(hit.Alive); // 300 at the centre, 120 at 2 cells: every soldier dies within about 3 cells
        Assert.Equal(spared.Def.MaxHp, spared.HP);
    }

    [Fact]
    public void TwoShellsBringDownAHowitzer()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w);
        var enemy = w.Spawn(2, "howitzer", 55, 60); // centre (60, 65): 45 cells east
        Ground(w, g, new Cell(60, 65));
        Shots(w, g, 64 + 120);
        Assert.Equal(enemy.Def.MaxHp - 600, enemy.HP); // 300 x 200% to buildings: a landing on its footprint is a full hit
        Shots(w, g, Every);
        Assert.False(enemy.Alive);
    }

    [Fact]
    public void AShiftQueueOfPointsTakesAShellEachInTurn()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w);
        var points = new[] { new Cell(60, 65), new Cell(60, 75), new Cell(70, 70) };
        Ground(w, g, points[0]);
        w.Step();
        Ground(w, g, points[1], queue: true);
        Ground(w, g, points[2], queue: true);
        w.Step();
        Assert.Equal(2, g.GroundQueue.Count);
        var aims = new List<Vec>();
        for (var i = 0; i < 100 + 6 * Every; i++)
        {
            w.Step();
            aims.AddRange(w.EmplacementEvents.Where(e => e.Kind == EmplacementEventKind.Fired).Select(e => e.At));
        }
        Assert.True(aims.Count >= 6);
        for (var i = 0; i < 6; i++)
        {
            var p = points[i % 3].Center();
            Assert.True((aims[i] - p).Len() <= Fixed.FromInt(4), $"shot {i}");
        }
        w.Order(1, CommandKind.Stop, [g]);
        w.Step();
        Assert.Empty(g.GroundQueue);
    }

    [Fact]
    public void OnItsOwnItPrefersTheBiggestGroup()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w);
        var lone = w.Spawn(2, "cargo_truck", 35, 65); // nearer
        var group = new[] { w.Spawn(2, "cargo_truck", 65, 64), w.Spawn(2, "cargo_truck", 66, 66), w.Spawn(2, "cargo_truck", 64, 66) };
        w.Spawn(1, "worker", 35, 68);
        w.Spawn(1, "worker", 65, 69);
        w.Run(10);
        Assert.Contains(g.AutoTarget, group.Select(u => u.Id));
        Assert.NotEqual(lone.Id, g.AutoTarget);
    }

    [Fact]
    public void ItTurnsSlowlyHalfATurnInAboutSixSeconds()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w, 60, 10); // facing north, centre (65, 15)
        Ground(w, g, new Cell(65, 65)); // due south: 16 facings
        var shots = Shots(w, g, 300);
        Assert.InRange((int)shots[0].Tick, 14 * Traverse + 1, 16 * Traverse + 2); // within a facing of the target it fires
        Assert.Equal(Facing.South, g.TurretFacing);
    }

    [Fact]
    public void ACargoTruckHandsOverOnlyWhatItNeedsAndKeepsTheRest()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w);
        SetShells(g, 0);
        var truck = Truck(w, 45, 65); // 30 cells east of its centre
        Assert.Equal(400, truck.Cargo);
        w.Order(1, CommandKind.Unload, [truck], target: g.Id);
        w.Step();
        Assert.Equal(OrderKind.Unload, truck.Order.Kind);
        var ins = new List<(uint Tick, EmplacementEvent E)>();
        var ended = new List<EmplacementEvent>();
        for (var i = 0; i < 1500 && truck.Order.Kind == OrderKind.Unload; i++)
        {
            w.Step();
            ins.AddRange(w.EmplacementEvents.Where(e => e.Kind == EmplacementEventKind.RoundIn).Select(e => (w.Tick, e)));
            ended.AddRange(w.EmplacementEvents.Where(e => e.Kind == EmplacementEventKind.ReloadEnded));
        }
        Assert.Equal(15, ins.Count);
        for (var i = 1; i < ins.Count; i++)
        {
            Assert.Equal(FeedEvery, (int)(ins[i].Tick - ins[i - 1].Tick)); // one shell every 2 s
            Assert.Equal(i + 1, ins[i].E.Value);
            Assert.Equal(400 - (i + 1) * Cost, ins[i].E.Value2);
        }
        Assert.Equal(15, Shells(g));
        Assert.Equal(100, truck.Cargo); // a full refill costs 300: it keeps 100
        Assert.Equal(OrderKind.Idle, truck.Order.Kind);
        Assert.Equal(0u, truck.FeedTo);
        var end = Assert.Single(ended);
        Assert.Equal((g.Id, truck.Id, 15, 100), (end.Unit, end.Other, end.Value, end.Value2));
        Assert.True(World.Within(truck.Pos, World.AimPoint(truck.Pos, g), Fixed.FromInt(15)));

        // The rest still unloads at a depot as before.
        var depot = w.Spawn(1, "supply_depot", 40, 40);
        depot.Stock = 500;
        w.Order(1, CommandKind.Unload, [truck], target: depot.Id);
        w.Run(800);
        Assert.Equal(600, depot.Stock);
        Assert.Equal(0, truck.Cargo);
    }

    [Fact]
    public void ATruckParkedNearbyReloadsItAndOneFartherAwayDoesNot()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w);
        SetShells(g, 12);
        var far = Truck(w, 15, 92); // 22 cells south of the footprint's edge
        w.Run(200);
        Assert.Equal(12, Shells(g));
        Assert.Equal(400, far.Cargo);
        var near = Truck(w, 15, 82); // 12 cells south of its edge
        w.Run(3 * FeedEvery + 5);
        Assert.Equal(15, Shells(g));
        Assert.Equal(340, near.Cargo);
        Assert.Equal(400, far.Cargo);
        Assert.Equal(OrderKind.Idle, near.Order.Kind);
        w.Run(200);
        Assert.Equal(340, near.Cargo); // full: nothing more
    }

    [Fact]
    public void OneTruckServesTwoHowitzersAndTwoTrucksFeedOneGunNoFaster()
    {
        var w = Arena.New(128);
        var (a, _) = Gun(w, 10, 20);
        var (b, _) = Gun(w, 10, 90);
        SetShells(a, 10);
        SetShells(b, 10);
        var truck = Truck(w, 45, 60);
        w.Order(1, CommandKind.Unload, [truck], target: a.Id);
        w.Run(800);
        Assert.Equal((15, 10, 300), (Shells(a), Shells(b), truck.Cargo));
        w.Order(1, CommandKind.Unload, [truck], target: b.Id);
        w.Run(800);
        Assert.Equal((15, 15, 200), (Shells(a), Shells(b), truck.Cargo));

        // Two trucks parked by one empty gun: one shell every 2 s all the same, from one truck.
        var w2 = Arena.New(128);
        var (g, _) = Gun(w2);
        SetShells(g, 0);
        var t1 = Truck(w2, 12, 74);
        var t2 = Truck(w2, 20, 74);
        w2.Run(10 * FeedEvery + 3);
        Assert.InRange(Shells(g), 9, 10);
        Assert.Equal(800 - Shells(g) * Cost, t1.Cargo + t2.Cargo);
        Assert.True(t1.Cargo == 400 || t2.Cargo == 400);
    }

    [Fact]
    public void ATruckShortOfAShellHandsOverWhatItCanAndNothingElseReloadsTheGun()
    {
        var w = Arena.New(128);
        var (g, _) = Gun(w);
        SetShells(g, 0);
        var depot = w.Spawn(1, "supply_depot", 18, 72); // right beside it: no depot reload
        w.Run(1000);
        Assert.Equal(0, Shells(g));
        Assert.Equal(1000, depot.Stock);
        var truck = Truck(w, 30, 65);
        truck.Cargo = 50;
        w.Order(1, CommandKind.Unload, [truck], target: g.Id);
        w.Run(600);
        Assert.Equal(2, Shells(g));
        Assert.Equal(10, truck.Cargo); // short of a shell: it keeps the 10
        Assert.Equal(OrderKind.Idle, truck.Order.Kind);
        w.Order(1, CommandKind.Unload, [truck], target: g.Id); // still short: refused
        w.Step();
        Assert.Equal(OrderKind.Idle, truck.Order.Kind);
    }

    [Fact]
    public void EveryShotShowsItToTheEnemyForTenSeconds()
    {
        var w = Arena.New(128, edit: s => s with { Fog = true });
        var (g, _) = Gun(w);
        w.Spawn(2, "Rifleman", 110, 110);
        SetShells(g, 2);
        Ground(w, g, new Cell(65, 65));
        var revealed = new List<EmplacementEvent>();
        var spotted = new List<uint>(); // the ticks (vision ran at w.Tick - 1) the enemy spotted it
        for (var i = 0; i < 800; i++)
        {
            w.Step();
            revealed.AddRange(w.EmplacementEvents.Where(e => e.Kind == EmplacementEventKind.Revealed));
            if (World.IsSpotted(g, 2))
            {
                spotted.Add(w.Tick - 1);
            }
            Assert.Equal(w.Tick < g.RevealUntil, w.RevealedBy(g, 2));
            Assert.False(w.RevealedBy(g, 1)); // never to its own side
        }
        Assert.Equal(2, revealed.Count);
        Assert.Equal(1, revealed[0].Value2); // hidden until the first
        Assert.Equal(0, revealed[1].Value2); // the second shot, 8 s later, starts the 10 s again
        var first = spotted[0];
        Assert.Equal(Every + Reveal, spotted.Count); // 8 s, then 10 s from the second shot
        Assert.Equal(first + (uint)(Every + Reveal) - 1, spotted[^1]);
        Assert.Equal(0, w.RevealLeft(g));
    }

    [Fact]
    public void TheFlashBringsCounterBatteryFire()
    {
        var w = Arena.New(128, edit: s => s with { Fog = true });
        var (g, _) = Gun(w);
        var mortar = w.Spawn(2, "mortar", 15, 92); // 22 cells south of its edge: in his range, far past his sight
        w.Order(2, CommandKind.Deploy, [mortar]);
        w.Run(150);
        Assert.Equal(0u, mortar.AutoTarget);
        Ground(w, g, new Cell(65, 65));
        w.Run(80);
        Assert.Equal(g.Id, mortar.AutoTarget); // spotted by its flash: a target
    }

    [Fact]
    public void AnExplosiveHitCanKillTheCrewmanAndLeaveTheGunSilentButStanding()
    {
        var w = Arena.New(128);
        var (g, crew) = Gun(w);
        crew.HP = 10;
        var heavy = w.Content.Warhead("heavy_rocket")!;
        w.Hurt(g, heavy, 100, 100); // 15 to the crewman of a 100 hit
        Assert.False(crew.Alive);
        Assert.Equal(g.Def.MaxHp - 200, g.HP);
        w.Step();
        Assert.Contains(w.EmplacementEvents, e => e.Kind == EmplacementEventKind.CrewLost && e.Other == crew.Id);
        Assert.Equal(0u, g.Driver);
        Assert.True(g.Alive);
        Ground(w, g, new Cell(65, 65));
        Assert.Empty(Shots(w, g, 300));
        var bullet = w.Content.Warhead("bullet")!;
        var next = w.Spawn(1, "Rifleman", 15, 72);
        World.Board(next, g);
        w.Hurt(g, bullet, 100, 100); // bullets don't reach him
        Assert.Equal(next.Def.MaxHp, next.HP);
        Ground(w, g, new Cell(65, 65));
        Assert.NotEmpty(Shots(w, g, 300));
    }

    [Fact]
    public void DestroyedItsCrewmanBailsOutByTheCrewRule()
    {
        var lived = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var w = Arena.New(128, seed);
            var (g, crew) = Gun(w);
            g.HP = 0;
            w.Step();
            var e = Assert.Single(w.EmplacementEvents, x => x.Kind == EmplacementEventKind.CrewBailed);
            Assert.Equal((g.Id, crew.Id), (e.Unit, e.Other));
            Assert.Null(w.Unit(g.Id));
            if (e.Value == 1)
            {
                lived++;
                Assert.Equal(crew.Def.MaxHp - crew.Def.MaxHp * 40 / 100, crew.HP);
                Assert.Equal(0u, crew.Inside);
                Assert.NotNull(w.Unit(crew.Id));
            }
            else
            {
                Assert.Null(w.Unit(crew.Id));
            }
        }
        Assert.InRange(lived, 2, 10); // 50%
    }

    [Fact]
    public void TheQueriesForTheCard()
    {
        var w = Arena.New(128);
        var (g, crew) = Gun(w);
        var s = w.EmplacementOf(g)!.Value;
        Assert.Equal((15, 15, crew.Id, true, false, 0u), (s.Rounds, s.MaxRounds, s.Crew, s.Manned, s.Revealed, s.Truck));
        Assert.Null(w.EmplacementOf(crew));
        SetShells(g, 14);
        var truck = Truck(w, 15, 74);
        w.Run(10);
        s = w.EmplacementOf(g)!.Value;
        Assert.Equal(truck.Id, s.Truck);
        Assert.InRange(s.NextRound, 1, FeedEvery);
        Assert.Equal(g.Id, w.FeedingOf(truck));
        Ground(w, g, new Cell(65, 65));
        Shots(w, g, 100);
        s = w.EmplacementOf(g)!.Value;
        Assert.True(s.Revealed);
        Assert.InRange(s.RevealLeft, 1, Reveal);
        Assert.InRange(s.ReloadLeft, 1, Every);
    }

    [Fact]
    public void Deterministic()
    {
        static List<ulong> Run(bool fullScan)
        {
            var w = Arena.New(128, 7, s => s with { Fog = true });
            w.FullScan = fullScan;
            var (a, _) = Gun(w);
            var (b, _) = Gun(w, 10, 90);
            SetShells(b, 3);
            w.Spawn(2, "cargo_truck", 65, 65);
            w.Spawn(2, "cargo_truck", 66, 67);
            w.Spawn(1, "worker", 62, 66);
            var truck = Truck(w, 40, 85);
            Ground(w, b, new Cell(60, 100));
            w.Step();
            Ground(w, b, new Cell(60, 110), queue: true);
            w.Order(1, CommandKind.Unload, [truck], target: b.Id);
            var hashes = new List<ulong>();
            for (var i = 0; i < 1500; i++)
            {
                w.Step();
                hashes.Add(w.Hash());
            }
            return hashes;
        }
        var first = Run(false);
        Assert.Equal(first, Run(false));
        Assert.Equal(first, Run(true));
    }
}
