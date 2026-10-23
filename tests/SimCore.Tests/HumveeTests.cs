using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The Sentinel's Humvee (humvee; docs/design.md, "Humvee"): it replaces the technical in the Sentinel roster; its .50 cal
// (humvee_mg, heavy_bullet: 125% against infantry, ground only), a driver plus 5 infantry passengers who get out one every
// 0.4 s (crew unloadInterval), and the smoke launchers (smoke_launchers, CommandKind.Smoke): a screen that blocks sight
// through it and breaks guided missiles' locks, 2 charges refilled where weapons rearm.
public class HumveeTests(ITestOutputHelper output)
{
    private const string Humvee = "humvee";

    private static World Sentinel(int n = 64, ulong seed = 1, bool fog = false) =>
        Arena.NewSentinel(n, seed, fog ? s => s with { Fog = true } : null);

    private static void HoldFire(World w, params Unit[] units)
    {
        foreach (var u in units)
        {
            w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = Stance.HoldFire });
        }
    }

    private static void Smoke(World w, params Unit[] units) =>
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.Smoke, Units = [.. units.Select(u => u.Id)] });

    private static int Charges(World w, Unit u) => w.SmokeOf(u)!.Value.Charges;

    // A crewed Humvee of player 1 with 5 riflemen aboard.
    private static (Unit V, List<Unit> Riders) Loaded(World w, int x, int y, int facing = Facing.North)
    {
        var (v, _) = w.SpawnCrewed(1, Humvee, x, y, facing);
        var riders = Enumerable.Range(0, 5).Select(_ => w.Spawn(1, "Rifleman", x, y)).ToList();
        foreach (var r in riders)
        {
            World.Ride(r, v);
        }
        return (v, riders);
    }

    // ---- Content ----

    [Fact]
    public void ItReplacesTheTechnicalInTheSentinelRoster()
    {
        var c = TestContent.Mvp;
        var h = c.Archetype(Humvee)!;
        var tech = c.Archetype("technical")!;
        var sentinel = c.Faction("sentinel")!;
        var resistance = c.Faction("resistance")!;
        Assert.True(sentinel.Archetypes[h.Index]);
        Assert.False(sentinel.Archetypes[tech.Index]); // the technical left the Sentinel roster
        Assert.True(resistance.Archetypes[tech.Index]); // the Resistance keeps it
        Assert.False(resistance.Archetypes[h.Index]);

        // The Sentinel lobby fields two Humvees and no technical; the Resistance's still fields its two technicals.
        var setup = TestContent.Sentinel(1);
        var sArmy = setup.Players.Single(p => p.Faction == "sentinel").Army;
        Assert.Equal(2, sArmy.Count(e => e.Unit == Humvee));
        Assert.DoesNotContain(sArmy, e => e.Unit == "technical");
        Assert.Equal(2, setup.Players.Single(p => p.Faction == "resistance").Army.Count(e => e.Unit == "technical"));
        var w = Sentinel();
        Assert.Null(w.Player(1)!.Setup.Find(w.Content, "technical"));
        Assert.Null(w.Player(2)!.Setup.Find(w.Content, Humvee));

        // Built at the military garage (and the vehicle garage, as the Sentinel cargo truck).
        Assert.True(h.Tags.Matches(c.Archetype("military_garage")!.Production!.Tags));
        Assert.True(h.Tags.Matches(c.Archetype("vehicle_garage")!.Production!.Tags));
    }

    [Fact]
    public void ItsStatsAreTheSpecs()
    {
        var c = TestContent.Mvp;
        var h = c.Archetype(Humvee)!;
        var tech = c.Archetype("technical")!;
        Assert.Equal(400, h.MaxHp);
        Assert.Equal(c.ArmorClass("medium_vehicle"), h.Armor);
        Assert.Equal(Locomotor.Wheeled, h.Locomotor);
        Assert.Equal(500, h.Cost);
        Assert.Equal(300, h.BuildTime); // 15 s
        Assert.Equal(tech.Radius, h.Radius); // 5.5 m against the technical's 5.4 m
        Assert.Equal(Fixed.FromInt(8), h.Vision);
        Assert.Equal(3, h.TurnRate);
        Assert.True(h.Turret);
        Assert.True(h.Speed.Raw > tech.Speed.Raw);

        // The fastest Sentinel ground vehicle.
        var sentinel = c.Faction("sentinel")!;
        var fastest = c.Archetypes.Where(a => sentinel.Archetypes[a.Index] && a.Layer == Layers.Ground && a.Tags.Matches(c.TagsOf("vehicle")) && a.Flight is null && a.Aircraft is null)
            .OrderByDescending(a => a.Speed.Raw).ThenBy(a => a.Id, StringComparer.Ordinal).First();
        Assert.Equal(Humvee, fastest.Id);

        // A soldier drives; 5 passenger seats for infantry, out one every 8 ticks.
        var crew = h.Crew!;
        Assert.Equal(c.TagsOf("soldier"), crew.Tags);
        Assert.Equal(5, crew.Passengers);
        Assert.Equal(c.TagsOf("infantry"), crew.PassengerTags);
        Assert.Equal(8, crew.UnloadInterval);
        Assert.Equal(0, c.Archetype("infantry_carrier")!.Crew!.UnloadInterval); // the carrier keeps its all-at-once unload

        // Its gun: ground only, 14 at 7 cells every 5 ticks, heavy_bullet (125% infantry, 40% light and medium vehicles).
        var w = Sentinel();
        var def = w.Player(1)!.Setup.Find(w.Content, Humvee)!;
        var mg = def.Weapons.Single();
        Assert.Equal("humvee_mg", def.Components[0].Id);
        Assert.Equal((14, Fixed.FromInt(7), 5), (mg.Damage, mg.Range, mg.Reload));
        Assert.Equal(Layers.Ground, mg.Effect.Targets);
        Assert.Null(mg.Effect.AirGun);
        Assert.Equal(-1, mg.Stock); // unlimited
        var hb = c.Warhead("heavy_bullet")!;
        Assert.Same(hb, mg.Warhead);
        Assert.Equal(125, hb.Versus[c.ArmorClass("infantry")]);
        Assert.Equal(40, hb.Versus[c.ArmorClass("light_vehicle")]);
        Assert.Equal(40, hb.Versus[c.ArmorClass("medium_vehicle")]);
        Assert.False(hb.CanHit(c.ArmorClass("aircraft")));

        // Its smoke launchers: 2 pops, 12 cells across, 10 s, 2 s apart.
        var sm = def.Smoke!;
        Assert.Equal((2, Fixed.FromInt(6), 200, 40), (sm.Charges, sm.Radius, sm.DurationTicks, sm.Cooldown));
        Assert.Equal(2, def.StockMax[def.SmokeStock]);
        Assert.Equal(500, def.Cost);
    }

    // ---- The gun ----

    // FirstHit: the damage the first burst of shooter (ordered at target) deals to it.
    private static int FirstHit(World w, Unit shooter, Unit target)
    {
        target.HP = 100_000;
        w.Order(shooter.Owner, CommandKind.Attack, [shooter], target.Id);
        for (var i = 0; i < 200 && target.HP == 100_000; i++)
        {
            w.Step();
        }
        return 100_000 - target.HP;
    }

    [Fact]
    public void TheMgShredsInfantryAndScratchesVehicles()
    {
        var w = Sentinel();
        var (h, _) = w.SpawnCrewed(1, Humvee, 20, 20, Facing.East);
        var soldier = w.Spawn(2, "Rifleman", 25, 20);
        HoldFire(w, soldier);
        var vsSoldier = FirstHit(w, h, soldier);
        Assert.Equal(17, vsSoldier); // 14 x 125%

        var w2 = Sentinel();
        var (h2, _) = w2.SpawnCrewed(1, Humvee, 20, 20, Facing.East);
        var (car, _) = w2.SpawnCrewed(2, "technical", 25, 20);
        HoldFire(w2, car);
        Assert.Equal(5, FirstHit(w2, h2, car)); // 14 x 40%

        // The technical's heavy MG on the same soldier: 12. A rifleman of 60 HP falls to 4 bursts (1 s) instead of 5.
        var w3 = Sentinel();
        var (t3, _) = w3.SpawnCrewed(2, "technical", 20, 20, Facing.East);
        var s3 = w3.Spawn(1, "Rifleman", 25, 20);
        HoldFire(w3, s3);
        var tech = FirstHit(w3, t3, s3);
        Assert.Equal(12, tech);
        var hp = w.Player(2)!.Setup.Find(w.Content, "Rifleman")!.MaxHp;
        output.WriteLine($"a burst: soldier {vsSoldier} (technical {tech}), technical {5}; a {hp}-HP rifleman in {(hp + vsSoldier - 1) / vsSoldier} bursts");
        Assert.True((hp + vsSoldier - 1) / vsSoldier < (hp + tech - 1) / tech);
    }

    [Fact]
    public void ItFiresOnTheMove()
    {
        var w = Sentinel();
        var (h, _) = w.SpawnCrewed(1, Humvee, 20, 10, Facing.South);
        var soldier = w.Spawn(2, "Rifleman", 24, 20);
        soldier.HP = 100_000;
        HoldFire(w, soldier);
        w.Order(1, CommandKind.Move, [h], cell: new Cell(20, 40)); // past him, 4 cells off its path
        var hitMoving = false;
        for (var i = 0; i < 200; i++)
        {
            var before = soldier.HP;
            w.Step();
            hitMoving |= soldier.HP < before && h.Moving;
        }
        Assert.True(hitMoving, "the turret fires while it drives");
    }

    [Fact]
    public void ItNeverFiresAtAircraft()
    {
        var w = Sentinel();
        var (h, _) = w.SpawnCrewed(1, Humvee, 20, 20);
        var drone = w.Spawn(2, "scout_drone", 23, 20);
        drone.HP = 10_000;
        w.Order(1, CommandKind.Attack, [h], drone.Id);
        w.Run(100);
        Assert.Equal(10_000, drone.HP);
    }

    // ---- Passengers and the fast dismount ----

    [Fact]
    public void FiveInfantryRideAndASixthIsRefused()
    {
        var w = Sentinel();
        var (h, _) = w.SpawnCrewed(1, Humvee, 20, 20);
        var men = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 16 + i, 24)).ToList();
        w.Order(1, CommandKind.Enter, men, h.Id);
        w.Run(200);
        Assert.Equal(5, w.PassengersOf(h).Count);
        var left = Assert.Single(men, m => m.Inside == 0);
        Assert.False(w.CanEnter(left, h));
        var worker = w.Spawn(1, "worker", 20, 22); // a worker is infantry too: refused only for want of a seat
        Assert.False(w.CanRide(worker, h));
        Assert.True(w.CanRide(left, w.SpawnCrewed(1, Humvee, 30, 20).Vehicle));
    }

    [Fact]
    public void FastDismountLetsOneOutEveryEightTicks()
    {
        var w = Sentinel();
        var (h, riders) = Loaded(w, 20, 20);
        w.Order(1, CommandKind.Exit, [h]);
        var outAt = new List<int>();
        for (var t = 0; t < 60; t++)
        {
            var before = w.PassengersOf(h).Count;
            w.Step();
            for (var k = w.PassengersOf(h).Count; k < before; k++)
            {
                outAt.Add(t);
            }
        }
        output.WriteLine("out at ticks " + string.Join(", ", outAt));
        Assert.Equal([0, 8, 16, 24, 32], outAt); // all 5 out in 1.6 s (0.4 s apart), the first at once
        Assert.Equal(h.Id, w.Unit(h.Driver)!.Inside); // the driver stays at the wheel
        Assert.All(riders, r => Assert.Equal(0u, r.Inside));
        Assert.Equal(riders.Count, riders.Select(r => Cell.Of(r.Pos)).Distinct().Count()); // each on its own cell
        Assert.Equal(0, h.DismountWait);

        // The infantry carrier keeps its own: all at once.
        var w2 = Arena.New(64);
        var (c, _) = w2.SpawnCrewed(1, "infantry_carrier", 20, 20);
        for (var i = 0; i < 5; i++)
        {
            World.Ride(w2.Spawn(1, "Rifleman", 20, 20), c);
        }
        w2.Order(1, CommandKind.Exit, [c]);
        w2.Step();
        Assert.Empty(w2.PassengersOf(c));
    }

    [Fact]
    public void TheDismountGoesOnWhileItDrivesAndEjectionTakesTheRest()
    {
        var w = Sentinel();
        var (h, _) = Loaded(w, 20, 20, Facing.East);
        w.Order(1, CommandKind.Move, [h], cell: new Cell(50, 20));
        w.Run(10);
        w.Order(1, CommandKind.Exit, [h]);
        w.Run(10); // two out
        Assert.Equal(3, w.PassengersOf(h).Count);
        Assert.True(h.Moving); // a dismount does not stop it
        w.Order(1, CommandKind.Exit, [h]); // again: the unload goes on as it was
        w.Run(1);
        Assert.Equal(3, w.PassengersOf(h).Count);
        h.HP = 0; // destroyed: the rest bail out by the crew's survival rule
        w.Run(2);
        Assert.DoesNotContain(w.Units, u => u.Inside == h.Id);
    }

    // ---- Smoke ----

    [Fact]
    public void ItPopsTwoChargesTwoSecondsApartAndTheScreenLastsTenSeconds()
    {
        var w = Sentinel();
        var (h, _) = w.SpawnCrewed(1, Humvee, 20, 20);
        var empty = w.Spawn(1, Humvee, 30, 20); // no driver: no smoke
        Assert.Equal(new SmokeStatus(2, 2, 0), w.SmokeOf(h));
        Assert.Null(w.SmokeOf(w.Spawn(1, "Rifleman", 25, 25)));
        Smoke(w, h, empty);
        w.Step();
        var popped = w.Tick - 1;
        var ev = Assert.Single(w.SmokeEvents);
        Assert.Equal((SmokeEventKind.Popped, h.Id, h.Pos, Fixed.FromInt(6)), (ev.Kind, ev.Unit, ev.At, ev.Radius));
        var cloud = Assert.Single(w.Smokes);
        Assert.Equal((h.Id, (byte)1, popped, popped + 200), (cloud.Source, cloud.Owner, cloud.Since, cloud.Until));
        Assert.Equal(new SmokeStatus(1, 2, 39), w.SmokeOf(h));
        Assert.Equal(2, Charges(w, empty));

        Smoke(w, h); // still reloading: nothing
        w.Step();
        Assert.Single(w.Smokes);
        w.Run(38);
        Smoke(w, h); // 40 ticks after the first: the second
        w.Step();
        Assert.Equal(2, w.Smokes.Count);
        Assert.Equal(0, Charges(w, h));
        w.Run(60);
        Smoke(w, h); // empty
        w.Step();
        Assert.Equal(2, w.Smokes.Count);

        // The first screen is gone 200 ticks after it was popped.
        while (w.Tick < popped + 200)
        {
            Assert.Contains(w.Smokes, s => s.Id == cloud.Id);
            w.Step();
        }
        w.Step();
        Assert.DoesNotContain(w.Smokes, s => s.Id == cloud.Id);
        var gone = w.SmokeEvents.Single();
        Assert.Equal((SmokeEventKind.Expired, cloud.Id), (gone.Kind, gone.Cloud));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheScreenBlocksSightThroughIt(bool fog)
    {
        var w = Sentinel(64, 1, fog);
        var (h, _) = w.SpawnCrewed(1, Humvee, 20, 30); // the screen: x 14 to 26 on row 30
        var sniper = w.Spawn(2, "Marksman", 27, 30); // 14 cells of vision, 7 east of the Humvee: outside the screen
        var behind = w.Spawn(1, "Rifleman", 13, 30); // 14 west of the sniper, beyond the screen
        var aside = w.Spawn(1, "Rifleman", 27, 36); // 6 south of the sniper, clear of the screen
        HoldFire(w, h, sniper, behind, aside);
        w.Run(8);
        Assert.True(World.IsSpotted(h, 2) && World.IsSpotted(behind, 2) && World.IsSpotted(aside, 2));
        Assert.True(World.IsSpotted(sniper, 1));

        Smoke(w, h);
        w.Run(1);
        Assert.True(w.InSmoke(h));
        Assert.False(World.IsSpotted(h, 2), "inside the screen: not seen");
        Assert.False(World.IsSpotted(behind, 2), "behind the screen: not seen");
        Assert.True(World.IsSpotted(aside, 2), "clear of the screen: seen");
        Assert.True(World.IsSpotted(sniper, 1), "the rifleman aside still sees the sniper");
        Assert.Equal(!fog, World.IsVisibleTo(behind, 2)); // without fog the map still shows what is behind smoke, not what is in it
        Assert.False(World.IsVisibleTo(h, 2));

        // An enemy in the same screen sees it.
        var close = w.Spawn(2, "Rifleman", 22, 30);
        HoldFire(w, close);
        w.Run(4);
        Assert.True(World.IsSpotted(h, 2));
        close.HP = 0;
        w.Run(4);
        Assert.False(World.IsSpotted(h, 2));

        w.Run(200);
        Assert.Empty(w.Smokes);
        Assert.True(World.IsSpotted(h, 2) && World.IsSpotted(behind, 2), "seen again once it is gone");
        Assert.True(World.IsVisibleTo(h, 2));
    }

    [Fact]
    public void AnEnemyOrderedToAttackItHoldsFireFromOutsideTheSmoke()
    {
        var w = Sentinel();
        var (h, _) = w.SpawnCrewed(1, Humvee, 20, 30);
        h.HP = 100_000;
        HoldFire(w, h);
        var sniper = w.Spawn(2, "kornet", 30, 30); // 10 cells: outside the screen, well within its 28.8
        w.Order(2, CommandKind.Attack, [sniper], h.Id);
        for (var i = 0; i < 200 && h.HP == 100_000; i++)
        {
            w.Step();
        }
        Assert.True(h.HP < 100_000, "it hits before the smoke");
        Smoke(w, h);
        w.Run(2);
        var hp = h.HP;
        var outside = 0;
        // The ordered Kornet team walks toward it (it no longer sees it); until it stands in the screen too it never fires.
        for (var i = 0; i < 100 && !w.InSmoke(sniper); i++)
        {
            w.Step();
            outside++;
            Assert.Equal(hp, h.HP);
        }
        output.WriteLine($"{outside} ticks outside the screen without a shot");
        Assert.True(outside > 40, "it walks in at its pace (4 cells), never firing on the way");
    }

    // A Kornet team of player 2 at (20, 40) with a spotter, at a Humvee 14 cells east (its HP huge); the missile in flight.
    private static (World W, Unit Kornet, Unit Target) KornetAt(bool fog = false)
    {
        var w = Sentinel(64, 1, fog);
        var k = w.Spawn(2, "kornet", 20, 40, Facing.East);
        var (h, _) = w.SpawnCrewed(1, Humvee, 34, 40);
        h.HP = 100_000;
        var spotter = w.Spawn(2, "Marksman", 34, 50);
        HoldFire(w, h, spotter);
        w.Order(2, CommandKind.Attack, [k], h.Id);
        for (var i = 0; i < 200 && w.Projectiles.Count == 0; i++)
        {
            w.Step();
        }
        Assert.NotEmpty(w.Projectiles);
        return (w, k, h);
    }

    // RunMissile runs until the missile is gone; whether it lost its lock to smoke.
    private static bool RunMissile(World w, out Projectile last)
    {
        last = w.Projectiles[0];
        var broken = false;
        for (var i = 0; i < 200 && w.Projectiles.Count > 0; i++)
        {
            last = w.Projectiles[0];
            w.Step();
            broken |= w.SmokeEvents.Any(e => e.Kind == SmokeEventKind.LockBroken);
        }
        Assert.Empty(w.Projectiles);
        return broken;
    }

    [Fact]
    public void AKornetWithoutSmokeHits()
    {
        var (w, _, h) = KornetAt();
        Assert.False(RunMissile(w, out var last));
        Assert.True(last.Guided);
        Assert.Equal(100_000 - 69, h.HP); // 126 x 55% (medium_vehicle)
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmokeBreaksTheKornetsLock(bool fog)
    {
        var (w, k, h) = KornetAt(fog);
        Smoke(w, h);
        w.Step();
        var ev = w.SmokeEvents.Where(e => e.Kind == SmokeEventKind.LockBroken).ToList();
        Assert.Equal(h.Id, Assert.Single(ev).Unit);
        Assert.False(w.Projectiles[0].Guided); // it flies on unguided and drops
        RunMissile(w, out _);
        Assert.Equal(100_000, h.HP);
        Assert.Equal(0u, k.Guiding); // the operator is free (and reloads)
    }

    [Fact]
    public void SmokeAcrossTheOperatorsLineBreaksTheLockToo()
    {
        var (w, _, target) = KornetAt();
        var (screen, _) = w.SpawnCrewed(1, Humvee, 27, 44); // 4 cells off the line, 8 from either end
        Smoke(w, screen);
        w.Step();
        Assert.False(w.InSmoke(target));
        Assert.Contains(w.SmokeEvents, e => e.Kind == SmokeEventKind.LockBroken && e.Unit == target.Id);
        RunMissile(w, out _);
        Assert.Equal(100_000, target.HP);
    }

    // ---- Rearm ----

    // Spent: a crewed Humvee of player 1 at (x, y) that popped both charges (the screens gone).
    private static Unit Spent(World w, int x, int y)
    {
        var (h, _) = w.SpawnCrewed(1, Humvee, x, y);
        Smoke(w, h);
        w.Run(41);
        Smoke(w, h);
        w.Run(201);
        Assert.Equal(0, Charges(w, h));
        Assert.Empty(w.Smokes);
        return h;
    }

    [Fact]
    public void TheMilitaryGarageRefillsItsCharges()
    {
        var w = Arena.NewSentinel(96);
        var g = w.Spawn(1, "military_garage", 20, 10);
        w.Step();
        var h = Spent(w, 40, 36); // full HP and nothing else to do: it goes in for the smoke alone
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Service, Units = [h.Id], Target = g.Id });
        var inBay = false;
        for (var t = 0; t < 1200 && !(inBay && !h.InGarage && h.ServiceAt == 0); t++)
        {
            w.Step();
            inBay |= h.InGarage;
        }
        Assert.True(inBay);
        Assert.Equal(2, Charges(w, h));
        Assert.False(h.InGarage);
    }

    // Emptied: a crewed Humvee of player 1 at (x, y) with no smoke charge left.
    private static Unit Emptied(World w, int x, int y)
    {
        var (h, _) = w.SpawnCrewed(1, Humvee, x, y);
        h.MutableCharges[h.Def.SmokeStock] = 0;
        return h;
    }

    [Fact]
    public void TheFieldAmmoPointAndADepotRefillThem()
    {
        var w = Sentinel(96);
        var facp = w.Spawn(1, "facp", 40, 40);
        facp.Stock = 100;
        var h = Emptied(w, 44, 50);
        w.Run(1); // a pulse (tick 0): one charge a pulse, 20 of stock each
        Assert.Equal(1, Charges(w, h));
        w.Run(40);
        Assert.Equal(2, Charges(w, h));
        Assert.Equal(60, facp.Stock);

        var w2 = Sentinel(96);
        w2.Spawn(1, "supply_depot", 40, 40);
        var h2 = Emptied(w2, 44, 46);
        w2.Run(100);
        Assert.Equal(2, Charges(w2, h2));
    }

    // ---- Determinism ----

    // A scripted fight: two loaded Humvees race at enemy infantry and a Kornet team, dismount, pop smoke; the commands in
    // order or reversed; the world's hash every tick.
    private static List<ulong> Script(bool reversed, bool fog)
    {
        var w = Sentinel(64, 5, fog);
        var (h1, _) = Loaded(w, 10, 20, Facing.East);
        var (h2, _) = Loaded(w, 10, 40, Facing.East);
        for (var i = 0; i < 6; i++)
        {
            w.Spawn(2, "Rifleman", 40 + i % 3, 28 + i);
        }
        var k = w.Spawn(2, "kornet", 50, 30, Facing.West);
        w.Spawn(2, "Marksman", 46, 34);
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.Move, Units = [h1.Id], Cell = new Cell(32, 26) },
            new Command { Tick = 1, Player = 1, Seq = 2, Kind = CommandKind.Move, Units = [h2.Id], Cell = new Cell(32, 36) },
            new Command { Tick = 100, Player = 1, Seq = 3, Kind = CommandKind.Exit, Units = [h1.Id, h2.Id] },
            new Command { Tick = 110, Player = 1, Seq = 4, Kind = CommandKind.Smoke, Units = [h1.Id, h2.Id] },
            new Command { Tick = 140, Player = 1, Seq = 5, Kind = CommandKind.AttackMove, Units = [h1.Id, h2.Id], Cell = new Cell(44, 31) },
            new Command { Tick = 160, Player = 1, Seq = 6, Kind = CommandKind.Smoke, Units = [h2.Id, h1.Id] },
            new Command { Tick = 2, Player = 2, Seq = 1, Kind = CommandKind.Attack, Units = [k.Id], Target = h1.Id },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        var events = 0;
        for (var t = 0; t < 600; t++)
        {
            w.Step();
            events += w.SmokeEvents.Count;
            hashes.Add(w.Hash());
        }
        Assert.True(events >= 4, "screens popped and expired");
        Assert.Empty(w.PassengersOf(h1));
        return hashes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ItIsDeterministic(bool fog)
    {
        var a = Script(false, fog);
        Assert.Equal(a, Script(false, fog));
        Assert.Equal(a, Script(true, fog));
    }

    [Fact]
    public void SegmentMeetsIsExact()
    {
        Vec P(int x, int y) => new(Fixed.FromInt(x), Fixed.FromInt(y));
        var r = Fixed.FromInt(2).Raw;
        Assert.True(World.SegmentMeets(P(5, 0), r, P(0, 0), P(10, 0))); // through the centre
        Assert.True(World.SegmentMeets(P(5, 2), r, P(0, 0), P(10, 0))); // tangent
        Assert.False(World.SegmentMeets(P(5, 3), r, P(0, 0), P(10, 0)));
        Assert.False(World.SegmentMeets(P(13, 0), r, P(0, 0), P(10, 0))); // beyond the end
        Assert.True(World.SegmentMeets(P(12, 0), r, P(0, 0), P(10, 0))); // the end touches
        Assert.True(World.SegmentMeets(P(0, 0), r, P(0, 0), P(0, 0))); // a point inside
        Assert.True(World.SegmentMeets(P(1000, 1000), r, P(0, 0), P(1500, 1500))); // far out: no overflow
        Assert.False(World.SegmentMeets(P(1000, 1004), r, P(0, 0), P(1500, 1500)));
    }
}
