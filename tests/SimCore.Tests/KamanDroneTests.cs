using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The Kaman drone (docs/design.md, "Kaman drone"; the owner's spec, 2026-10-22): the Resistance's Kaman-22 type long-range
// attack drone, a flying unit launched by the Kaman truck's rail or an engineer's Kaman station at a unit (it follows it)
// or a point; a target its side stops seeing is flown to where it was last seen; two missiles from 30 cells a second
// apart, then the dive into the target (520 through kaman_charge, a 6-cell blast; armour dies, a building loses 45%).
// Shootable by all air defence and the jets: 2 AA-soldier hits, 1 air defence vehicle hit.
public class KamanDroneTests(ITestOutputHelper output)
{
    private const long Cell1 = Fixed.OneRaw, Metre = 512;

    private static Unit Truck(World w, int x, int y, byte p = 1) => w.SpawnCrewed(p, "kaman_truck", x, y, Facing.East).Vehicle;

    private static void Attack(World w, Unit launcher, Unit target) =>
        w.Issue(new Command { Tick = w.Tick, Player = launcher.Owner, Kind = CommandKind.Attack, Units = [launcher.Id], Target = target.Id });

    private static void Ground(World w, Unit launcher, Cell at) =>
        w.Issue(new Command { Tick = w.Tick, Player = launcher.Owner, Kind = CommandKind.AttackGround, Units = [launcher.Id], Cell = at });

    private static double Cells(long raw) => raw / (double)Cell1;

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static long Level(Vec a, Vec b) => World.Len3(a.X.Raw - b.X.Raw, a.Y.Raw - b.Y.Raw, 0);

    // A flight's record: every Kaman event with its tick and the drone's position then, and the drone's highest height.
    private sealed record Flight(List<(uint Tick, KamanEvent E, Vec DronePos)> Events, long Top, uint Drone)
    {
        public KamanEvent? Blast => Events.Find(e => e.E.Kind == KamanEventKind.Blast) is var b && b.E.Kind == KamanEventKind.Blast ? b.E : null;

        public List<(uint Tick, KamanEvent E, Vec DronePos)> Of(KamanEventKind k) => Events.FindAll(e => e.E.Kind == k);
    }

    // Steps the world until the first Kaman goes off or is lost (or max ticks), each tick calling `each`.
    private static Flight Fly(World w, int max = 4000, Action<World>? each = null)
    {
        var events = new List<(uint, KamanEvent, Vec)>();
        long top = 0;
        uint drone = 0;
        for (var t = 0; t < max; t++)
        {
            w.Step();
            each?.Invoke(w);
            var k = w.All("kaman_drone").FirstOrDefault();
            if (k is not null)
            {
                drone = k.Id;
                top = Math.Max(top, k.Altitude.Raw);
            }
            foreach (var e in w.KamanEvents)
            {
                events.Add((w.Tick, e, w.Unit(e.Drone)?.Pos ?? e.At));
            }
            if (w.KamanEvents.Any(e => e.Kind == KamanEventKind.Blast) || (drone != 0 && w.Unit(drone) is null))
            {
                break;
            }
        }
        return new Flight(events, top, drone);
    }

    // ---- Content ----

    [Fact]
    public void TheKamanIsData()
    {
        var c = TestContent.Mvp;
        var res = c.Faction("resistance")!;
        var drone = c.Archetype("kaman_drone")!;
        var truck = c.Archetype("kaman_truck")!;
        var station = c.Archetype("kaman_station")!;
        foreach (var a in new[] { drone, truck, station })
        {
            Assert.True(res.Archetypes[a.Index], $"{a.Id} is the Resistance's");
            Assert.False(c.Faction("sentinel")!.Archetypes[a.Index], $"{a.Id} is not the Sentinel's");
        }
        // The drone: a flying unit of the "kaman" air class (appended last), 150 m up, 15 cells/s, 40 cells of sight.
        Assert.Equal("kaman", c.AirClasses[^1].Id);
        Assert.Equal(InterceptClass.Kaman, drone.Flight!.Class);
        Assert.Equal((150 * Metre, 150L * Cell1 / 10 / World.TicksPerSecond, Fixed.FromInt(40)), (drone.Flight.HeightRaw, drone.Speed.Raw, drone.Vision));
        var k = drone.Kaman!;
        Assert.Equal((520, Fixed.FromInt(6), 1000), (k.Damage, k.Splash, k.DivePermille));
        var wh = c.Warheads[k.Warhead];
        Assert.Equal("kaman_charge", wh.Id);
        // About twice a one-way drone: 260 in 4 cells, 20% of a building; the Kaman 520 in 6 cells, 45%.
        var oneWay = c.Component("drone_rails")!.Effects.OfType<WeaponEffect>().Single();
        Assert.Equal((2 * oneWay.Damage, oneWay.Splash.Raw * 3 / 2), (k.Damage, k.Splash.Raw));
        // The launchers: the truck's rail and the station's rail launch it; their target choice uses its blast.
        foreach (var rail in new[] { "kaman_rail", "kaman_station_rail" })
        {
            var w = c.Component(rail)!.Effects.OfType<WeaponEffect>().Single();
            Assert.Equal(drone.Index, w.Launches);
            Assert.Equal((k.Damage, wh.Id, k.Splash), (w.Damage, w.Warhead.Id, w.Splash));
            Assert.Equal((Fixed.FromInt(1200), Fixed.FromInt(60), 1), (w.Range, w.MinRange, w.Ammo));
        }
        Assert.Equal((1800, 40 * World.TicksPerSecond, 220), (truck.Cost, truck.BuildTime, truck.MaxHp));
        // The engineer's Kaman option: a second station kit beside the drone station kit.
        var eng = c.Archetype("engineer")!;
        Assert.Contains(c.Component("drone_station_kit")!.Index, eng.Components);
        Assert.Contains(c.Component("kaman_station_kit")!.Index, eng.Components);
        var kit = c.Component("kaman_station_kit")!.Effects.OfType<PlacerEffect>().Single();
        Assert.Equal((station.Index, 1, 600, 400), (kit.Archetype, kit.Charges, kit.DepotReload!.EveryTicks, kit.DepotReload.Cost));
        // Every air defence and air-to-air weapon that engages drones engages it.
        foreach (var id in new[] { "igla", "sam_missiles", "patriot_missiles", "aam_radar", "aam_ir", "aam_ir_light" })
        {
            Assert.True(c.Component(id)!.Effects.OfType<WeaponEffect>().Single().Interceptor!.Engages(InterceptClass.Kaman), id);
        }
        foreach (var id in new[] { "fighter_gun", "multirole_gun", "attack_gun" })
        {
            Assert.True(c.Component(id)!.Effects.OfType<WeaponEffect>().Single().AirGun!.HitOn(InterceptClass.Kaman) > 0, id);
        }
        Assert.Equal(50, c.Component("igla")!.Effects.OfType<WeaponEffect>().Single().Interceptor!.HitDamageOn(InterceptClass.Kaman));
        Assert.Equal(100, c.Component("sam_missiles")!.Effects.OfType<WeaponEffect>().Single().Interceptor!.HitDamageOn(InterceptClass.Kaman));
    }

    [Theory]
    [InlineData("\"launches\": \"kaman_drone\",\n                                \"ammo\": 1, \"ammoPerRocket\": true,\n", "\"launches\": \"technical\",\n                                \"ammo\": 1, \"ammoPerRocket\": true,\n", "must have a kaman block")]
    [InlineData("\"dive\": 1000, \"pitchOver\": 40", "\"dive\": 1000, \"pitchOver\": 0", "kaman.pitchOver")]
    [InlineData("\"warhead\": \"kaman_charge\", \"splash\": 60, \"splashCenter\": 100, \"splashEdge\": 40,\n                 \"dive\"", "\"warhead\": \"nope\", \"splash\": 60, \"splashCenter\": 100, \"splashEdge\": 40,\n                 \"dive\"", "unknown warhead")]
    public void BadKamanContentIsRefused(string from, string to, string error)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Json.Contains(from, StringComparison.Ordinal));
        Assert.True(i >= 0, $"no file has {from}");
        sources[i] = sources[i] with { Json = sources[i].Json.Replace(from, to, StringComparison.Ordinal) };
        var ex = Assert.ThrowsAny<Exception>(() => ContentLoader.Load(sources));
        Assert.Contains(error, ex.Message);
    }

    // ---- Flight and attack ----

    [Fact]
    public void ItFollowsAMovingVehicleFiresTwoMissilesThenDivesIntoIt()
    {
        var w = Arena.New(400);
        var truck = Truck(w, 10, 150);
        var (carrier, _) = w.SpawnCrewed(2, "infantry_carrier", 320, 120, Facing.South);
        w.Order(2, CommandKind.Move, [carrier], cell: new Cell(320, 390)); // driving away across the drone's line
        Attack(w, truck, carrier);
        var f = Fly(w);
        foreach (var (tick, e, at) in f.Events)
        {
            output.WriteLine($"{tick}: {e.Kind} at ({F(Cells(e.At.X.Raw))}, {F(Cells(e.At.Y.Raw))}) {F(e.Altitude.Raw / (double)Metre)} m; drone ({F(Cells(at.X.Raw))}, {F(Cells(at.Y.Raw))})");
        }
        output.WriteLine($"top {F(f.Top / (double)Metre)} m; carrier HP {carrier.HP}");
        Assert.InRange(f.Top, 150 * Metre - 512, 150 * Metre + 512); // its flight height over the flat ground (within the planner's hold band)
        var kinds = f.Events.Select(e => e.E.Kind).ToList();
        Assert.Equal([KamanEventKind.Launch, KamanEventKind.Dive, KamanEventKind.Missile, KamanEventKind.Missile, KamanEventKind.Blast], kinds);
        var m = f.Of(KamanEventKind.Missile);
        Assert.Equal(20u, m[1].Tick - m[0].Tick); // a second apart
        Assert.All(m, x => Assert.Equal(carrier.Id, x.E.Target)); // at the vehicle, followed
        var blast = f.Blast!.Value;
        Assert.Equal(carrier.Id, blast.Other); // the dive ends in it
        Assert.False(carrier.Alive); // armour dies
        Assert.True(Level(blast.At, new Vec(Fixed.FromInt(320), Fixed.FromInt(120))) > 20 * Cell1, "the carrier had driven on: it was followed");
    }

    [Fact]
    public void TheMissilesLeaveFromAboutThirtyCellsASecondApartAndTrackTheTarget()
    {
        var w = Arena.New(320);
        var truck = Truck(w, 10, 150);
        var (tech, _) = w.SpawnCrewed(2, "technical", 220, 150, Facing.North);
        w.Order(2, CommandKind.Move, [tech], cell: new Cell(220, 20));
        Attack(w, truck, tech);
        var hits = new List<Detonation>();
        var f = Fly(w, each: x => hits.AddRange(x.Detonations));
        var m = f.Of(KamanEventKind.Missile);
        Assert.Equal(2, m.Count);
        var d0 = Cells(Level(m[0].DronePos, m[0].E.At));
        output.WriteLine($"missile 1 from {F(d0)} cells, {F(m[0].E.Altitude.Raw / (double)Metre)} m up; missile 2 from {F(Cells(Level(m[1].DronePos, m[1].E.At)))} cells");
        Assert.InRange(d0, 28.0, 30.0);
        Assert.True(f.Events.FindIndex(e => e.E.Kind == KamanEventKind.Blast) > f.Events.FindLastIndex(e => e.E.Kind == KamanEventKind.Missile));
        // The technical (180 HP: a missile takes 126) dies to the missiles before the drone arrives: the dive goes to where it was.
        Assert.Contains(hits, h => h.Hit == tech.Id);
        Assert.False(tech.Alive);
    }

    [Fact]
    public void ALostTargetIsFlownToWhereItWasLastSeen()
    {
        var w = Arena.New(320, edit: s => s with { Fog = true });
        var truck = Truck(w, 10, 150);
        var (tech, _) = w.SpawnCrewed(2, "infantry_carrier", 230, 150, Facing.North); // unarmed (it never shoots its spotter), tough
        var spotter = w.Spawn(1, "Marksman", 222, 150); // 8 cells off: his side sees it
        w.Run(5);
        Attack(w, truck, tech);
        var last = tech.Pos;
        var hp = 0;
        var f = Fly(w, each: x =>
        {
            if (x.All("kaman_drone").FirstOrDefault() is { } k && k.Pos.X.Raw > 100 * Cell1 && spotter.Alive)
            {
                spotter.HP = 0; // the spotter falls
                (last, hp) = (tech.Pos, tech.HP);
                tech.Pos = new Vec(Fixed.FromInt(230), Fixed.FromInt(300)); // and the technical is far off, out of the drone's sight
            }
        });
        foreach (var (tick, e, at) in f.Events)
        {
            output.WriteLine($"{tick}: {e.Kind} at ({F(Cells(e.At.X.Raw))}, {F(Cells(e.At.Y.Raw))}) target {e.Target}");
        }
        Assert.Single(f.Of(KamanEventKind.Lost));
        Assert.Empty(f.Of(KamanEventKind.Reacquired));
        Assert.Equal(last, f.Of(KamanEventKind.Lost)[0].E.At);
        var m = f.Of(KamanEventKind.Missile);
        Assert.Equal(2, m.Count);
        Assert.All(m, x => Assert.Equal((0u, last), (x.E.Target, x.E.At))); // at the last known point
        var blast = f.Blast!.Value;
        Assert.True(Level(blast.At, last) <= Cell1, "it dives onto the last known position");
        Assert.Equal(hp, tech.HP); // untouched: everything went to where it was
    }

    [Fact]
    public void ItReacquiresATargetItsSideSeesAgain()
    {
        var w = Arena.New(320, edit: s => s with { Fog = true });
        var truck = Truck(w, 10, 150);
        var (tech, _) = w.SpawnCrewed(2, "infantry_carrier", 230, 150, Facing.North); // unarmed (it never shoots its spotter), tough
        var spotter = w.Spawn(1, "Marksman", 222, 150);
        w.Run(5);
        Attack(w, truck, tech);
        var moved = false;
        var f = Fly(w, each: x =>
        {
            if (!moved && x.All("kaman_drone").FirstOrDefault() is { } k && k.Pos.X.Raw > 100 * Cell1)
            {
                moved = true;
                spotter.HP = 0;
                tech.Pos = new Vec(Fixed.FromInt(230), Fixed.FromInt(170)); // 20 cells off: in the drone's own sight once it comes
            }
        });
        Assert.Single(f.Of(KamanEventKind.Lost));
        Assert.Single(f.Of(KamanEventKind.Reacquired));
        Assert.Equal(tech.Id, f.Blast!.Value.Other);
    }

    [Fact]
    public void ItsDiveKillsArmourAndTakesFortyFivePercentOfABuilding()
    {
        var c = TestContent.Mvp;
        var k = c.Archetype("kaman_drone")!.Kaman!;
        var wh = c.Warheads[k.Warhead];
        var w = Arena.New(64);
        // The blast alone: a direct hit on the infantry carrier (1200 HP armour) kills it; any building loses 45% of its max HP
        // whatever its size, near miss or direct hit.
        var carrier = w.Spawn(2, "infantry_carrier", 10, 10);
        w.Hurt(carrier, wh, k.Damage, 100);
        Assert.False(carrier.Alive);
        foreach (var id in new[] { "block_panel_5", "house_small", "supply_depot" })
        {
            var b = w.Spawn(2, id, 30, 30);
            var hp = b.HP;
            w.Hurt(b, wh, k.Damage, 40);
            Assert.Equal(hp - hp * 45 / 100, b.HP);
            b.HP = 0;
            w.Step();
        }

        // In flight, at a panel block: its two missiles and the dive.
        var f = Arena.New(320);
        var truck = Truck(f, 10, 150);
        var block = f.Spawn(2, "block_panel_5", 230, 145);
        var max = block.HP;
        var missile = c.Component("kaman_missiles")!.Effects.OfType<WeaponEffect>().Single();
        var perMissile = missile.Warhead.Damage(missile.Damage, block.Def.Armor, 100);
        Attack(f, truck, block);
        var flight = Fly(f);
        output.WriteLine($"panel block: {max} -> {block.HP} (a missile {perMissile}, the dive {max * 45 / 100})");
        Assert.Equal(block.Id, flight.Blast!.Value.Other);
        Assert.InRange(max - block.HP, max * 45 / 100, max * 45 / 100 + 2 * perMissile + 2);
    }

    [Fact]
    public void ItKillsInfantryAndVehiclesInItsSixCellBlast()
    {
        var w = Arena.New(320);
        var truck = Truck(w, 10, 150);
        var near = new[] { w.Spawn(2, "Rifleman", 204, 154), w.Spawn(2, "Rifleman", 200, 144), w.Spawn(2, "technical", 196, 150) };
        var far = w.Spawn(2, "Rifleman", 207, 150);
        Ground(w, truck, new Cell(200, 150));
        var f = Fly(w);
        Assert.True(Level(f.Blast!.Value.At, new Cell(200, 150).Center()) <= Cell1);
        Assert.All(near, u => Assert.False(u.Alive));
        Assert.True(far.Alive); // 7 cells out
    }

    // ---- Survivability ----

    // A Kaman of player 1 sent across a ring of `count` of player 2's `shooter` (AA units) to a point beyond it: the air
    // defence hits on it (each checked: a hit leaves it at maxHp - share), and whether they brought it down.
    private (int Hits, bool Down) HitsToBringItDown(string shooter, int count, ulong seed, int share)
    {
        var w = Arena.New(320, seed);
        var truck = Truck(w, 10, 150);
        for (var i = 0; i < count; i++)
        {
            var (dx, dy) = ((int)(15 * Math.Cos(i * 2 * Math.PI / count)), (int)(15 * Math.Sin(i * 2 * Math.PI / count)));
            if (w.Player(2)!.Setup.Find(w.Content, shooter)!.Archetype.Crew is not null)
            {
                w.SpawnCrewed(2, shooter, 160 + dx, 150 + dy);
            }
            else
            {
                w.Spawn(2, shooter, 160 + dx, 150 + dy);
            }
        }
        Ground(w, truck, new Cell(300, 150));
        var hits = 0;
        Unit? k = null;
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            k ??= w.All("kaman_drone").FirstOrDefault();
            if (k is null)
            {
                continue;
            }
            foreach (var b in w.AirBursts.Where(b => b.Target == k.Id && b.Kind is AirBurstKind.Hit or AirBurstKind.Kill))
            {
                hits++;
                Assert.Equal(b.Kind == AirBurstKind.Kill ? 0 : k.Def.MaxHp - hits * k.Def.MaxHp * share / 100, Math.Max(0, k.HP));
            }
            if (w.AirLosses.Any(l => l.Aircraft == k.Id))
            {
                Assert.Equal(KillCause.Missile, w.AirLosses.Single(l => l.Aircraft == k.Id).Cause);
                return (hits, true);
            }
            if (w.KamanEvents.Any(e => e.Kind == KamanEventKind.Blast))
            {
                return (hits, false);
            }
        }
        return (hits, false);
    }

    [Theory]
    [InlineData("manpads", 8, 2, 50)]     // the AA soldier: a hit takes half, the second brings it down
    [InlineData("sam_vehicle", 3, 1, 100)] // the air defence vehicle: a hit kills
    public void AirDefenceBringsItDown(string shooter, int count, int hits, int share)
    {
        var downs = 0;
        for (var seed = 1UL; seed <= 6; seed++)
        {
            var (n, down) = HitsToBringItDown(shooter, count, seed, share);
            output.WriteLine($"{shooter} x{count}, seed {seed}: {n} hits, {(down ? "down" : "got through")}");
            if (down)
            {
                downs++;
                Assert.Equal(hits, n);
            }
            else
            {
                Assert.True(n < hits);
            }
        }
        Assert.True(downs >= 2, $"down {downs} times in 6");
    }

    [Fact]
    public void AJetOnGuardShootsItDown()
    {
        var w = Arena.NewSentinel(240); // player 1 Sentinel, player 2 Resistance
        var jet = w.Spawn(1, "Multirole 500 lb", 120, 60, Facing.East);
        var truck = Truck(w, 120, 230, 2);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Units = [truck.Id], Cell = new Cell(120, 10) });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [jet.Id], Mission = AirTaskKind.Guard, Cell = new Cell(60, 40),
            Cell2 = new Cell(180, 200), Filter = (byte)AirTargetKinds.Drones });
        uint drone = 0;
        var down = false;
        for (var t = 0; t < 1500 && !down; t++)
        {
            w.Step();
            drone = drone != 0 ? drone : w.All("kaman_drone").FirstOrDefault()?.Id ?? 0;
            down = drone != 0 && w.AirLosses.Any(l => l.Aircraft == drone);
            Assert.DoesNotContain(w.KamanEvents, e => e.Kind == KamanEventKind.Blast);
        }
        Assert.True(down, "the jet brings the Kaman down");
    }

    [Fact]
    public void ItIsAnAirTargetTrackedAndSeenAndItSpotsForItsSide()
    {
        var w = Arena.New(320, edit: s => s with { Fog = true });
        var truck = Truck(w, 10, 150);
        var hunter = w.Spawn(2, "manpads", 120, 150);
        var hidden = w.Spawn(2, "Rifleman", 100, 165); // 15 cells off the drone's line: nobody of player 1 is near
        Ground(w, truck, new Cell(300, 150));
        var tracked = false;
        var spotted = false;
        var seenByEnemy = false;
        Fly(w, each: x =>
        {
            if (x.All("kaman_drone").FirstOrDefault() is { } k)
            {
                tracked |= x.AirTracked(2, k.Id);
                seenByEnemy |= World.IsVisibleTo(k, 2);
                spotted |= World.IsSpotted(hidden, 1);
            }
        });
        Assert.True(tracked, "the AA soldier's side tracks it (team cueing)");
        Assert.True(seenByEnemy, "the enemy sees it in the sky");
        Assert.True(spotted, "it spots for its side on its way");
        Assert.True(hunter.Alive);
    }

    [Fact]
    public void ItTakesNoOrders()
    {
        var w = Arena.New(320);
        var truck = Truck(w, 10, 150);
        Ground(w, truck, new Cell(200, 150));
        w.Run(60);
        var k = Assert.Single(w.All("kaman_drone"));
        w.Order(1, CommandKind.Move, [k], cell: new Cell(10, 10));
        var f = Fly(w);
        Assert.True(Level(f.Blast!.Value.At, new Cell(200, 150).Center()) <= Cell1);
    }

    // ---- Range ----

    [Fact]
    public void ItReachesTwelveHundredCellsAndNoFurther()
    {
        var w = Arena.New(1250);
        var truck = Truck(w, 20, 600);
        Ground(w, truck, new Cell(1218, 600)); // 1,198 cells
        var f = Fly(w, 3000);
        Assert.NotNull(f.Blast);
        Assert.True(Level(f.Blast!.Value.At, new Cell(1218, 600).Center()) <= Cell1);
        var lifeLeft = f.Events[^1].Tick - f.Events[0].Tick;
        output.WriteLine($"1,198 cells in {lifeLeft} ticks ({F(lifeLeft / 20.0)} s); fuel {w.Content.Archetype("kaman_drone")!.Lifetime} ticks");
        Assert.True(lifeLeft < w.Content.Archetype("kaman_drone")!.Lifetime);

        // Beyond 1,200 cells it is not launched (the truck drives closer); inside 60 cells neither.
        var far = Arena.New(1250);
        var t2 = Truck(far, 20, 600);
        Ground(far, t2, new Cell(1240, 600));
        far.Run(200);
        Assert.Empty(far.All("kaman_drone"));
        Assert.Equal(1, t2.Charges[0]);
        var near = Arena.New(160);
        var t3 = Truck(near, 20, 80);
        Ground(near, t3, new Cell(70, 80));
        near.Run(200);
        Assert.Empty(near.All("kaman_drone"));
    }

    // ---- Launchers ----

    [Fact]
    public void TheTruckReloadsStowedAtADepotSixtySecondsAndFourHundred()
    {
        var w = Arena.New(320);
        var depot = w.Spawn(1, "supply_depot", 2, 148);
        var truck = Truck(w, 9, 150);
        Ground(w, truck, new Cell(200, 150));
        w.Run(60);
        Assert.Equal(0, truck.Charges[0]);
        Assert.Single(w.All("kaman_drone"));
        Assert.Equal(OrderKind.Idle, truck.Order.Kind);
        w.Run(40 + 1200 - 60 + 20); // it stows (2 s), then 60 s
        Assert.Equal(0, truck.Charges[0]);
        w.Run(40);
        Assert.Equal(1, truck.Charges[0]);
        Assert.Equal(1000 - 400, depot.Stock);
    }

    [Fact]
    public void TheEngineersKamanStationFiresAfterHeLeavesAndGoesWhenSpent()
    {
        var w = Arena.New(320);
        var engineer = w.Spawn(1, "Sapper", 10, 150);
        var kit = engineer.Def.Abilities.Single(a => a.Effect is PlacerEffect pe && w.Content.Archetypes[pe.Archetype].Id == "kaman_station");
        var droneKit = engineer.Def.Abilities.Single(a => a.Effect is PlacerEffect pe && w.Content.Archetypes[pe.Archetype].Id == "drone_station");
        w.Order(1, CommandKind.UseAbility, [engineer], cell: new Cell(11, 150), ability: "kaman_station_kit");
        w.Run(50);
        Assert.Empty(w.All("kaman_station"));
        w.Run(15);
        var station = Assert.Single(w.All("kaman_station"));
        Assert.Equal((0, 1), (engineer.Charges[kit.Stock], engineer.Charges[droneKit.Stock])); // the drone station kit is still his
        engineer.HP = 0;
        // It never fires on its own (hold fire), even at an enemy in plain sight.
        var tech = w.Spawn(2, "technical", 150, 150);
        w.Spawn(1, "Marksman", 140, 150);
        w.Run(100);
        Assert.Empty(w.All("kaman_drone"));
        Attack(w, station, tech);
        w.Run(5);
        Assert.Single(w.All("kaman_drone"));
        Assert.Equal(0, station.Charges[0]);
        w.Run(380);
        Assert.NotNull(w.Unit(station.Id)); // the empty rail stays a while
        w.Run(40);
        Assert.Null(w.Unit(station.Id));
    }

    [Fact]
    public void TheEngineersKamanKitRefillsAtADepotThirtySecondsForFourHundred()
    {
        var w = Arena.New(64);
        var depot = w.Spawn(1, "supply_depot", 2, 2);
        var engineer = w.Spawn(1, "Sapper", 10, 5);
        var kit = engineer.Def.Abilities.Single(a => a.Effect is PlacerEffect pe && w.Content.Archetypes[pe.Archetype].Id == "kaman_station").Stock;
        var droneKit = engineer.Def.Abilities.Single(a => a.Effect is PlacerEffect pe && w.Content.Archetypes[pe.Archetype].Id == "drone_station").Stock;
        w.Step();
        engineer.MutableCharges[kit] = 0;
        engineer.MutableCharges[droneKit] = 0;
        w.Run(210);
        Assert.Equal(1, engineer.Charges[droneKit]); // one kit at a time: the drone station's first (10 s, 150)
        Assert.Equal(0, engineer.Charges[kit]);
        w.Run(570);
        Assert.Equal(0, engineer.Charges[kit]);
        w.Run(30);
        Assert.Equal(1, engineer.Charges[kit]);
        Assert.Equal(1000 - 150 - 400, depot.Stock);
    }

    // ---- Determinism ----

    [Fact]
    public void TheSameOrdersGiveTheSameHashes()
    {
        ulong[] Run()
        {
            var w = Arena.New(320, edit: s => s with { Fog = true });
            w.AttachTerrain(Terrain(320, (x, y) => (int)((x % 23 + y % 13) * Metre)));
            var truck = Truck(w, 10, 150);
            var (tech, _) = w.SpawnCrewed(2, "technical", 220, 150);
            w.Spawn(1, "Marksman", 212, 150);
            w.Spawn(2, "manpads", 150, 160);
            w.Order(2, CommandKind.Move, [tech], cell: new Cell(220, 30));
            Attack(w, truck, tech);
            var hashes = new ulong[900];
            for (var i = 0; i < hashes.Length; i++)
            {
                w.Step();
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }

    private static TerrainMap Terrain(int n, Func<int, int, int> heightRaw)
    {
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = heightRaw(x, y);
            }
        }
        return new TerrainMap(n, n, corners, new byte[n * n], rules);
    }
}
