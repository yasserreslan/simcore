using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The infantry carrier (infantry_carrier): a 6x6 troop truck with a driver seat and 15 passenger
// seats for infantry, who can't fire out; armored_vehicle armour and 1200 HP so it lives through
// any single first strike but the tactical ballistic missile and a full heavy rocket salvo.
public class CarrierTests
{
    private const string Carrier = "infantry_carrier";
    private const int Seats = 15, MaxHp = 1200;

    // A crewed carrier of player 2 with 15 riflemen in the back, and a worker of player 1 a few
    // cells away to spot it for the shooters.
    private static (Unit Carrier, List<Unit> Riders) Loaded(World w, int x, int y, int facing = Facing.North)
    {
        var (c, _) = w.SpawnCrewed(2, Carrier, x, y, facing);
        var riders = new List<Unit>();
        for (var i = 0; i < Seats; i++)
        {
            var r = w.Spawn(2, "Rifleman", x, y);
            World.Ride(r, c);
            riders.Add(r);
        }
        w.Spawn(1, "worker", x, y + 4);
        return (c, riders);
    }

    // Runs until no projectile is in flight (at most ticks).
    private static void Settle(World w, int ticks = 600)
    {
        for (var i = 0; i < ticks && w.Projectiles.Count > 0; i++)
        {
            w.Step();
        }
        Assert.Empty(w.Projectiles);
    }

    // Runs until shooter has sent off n rounds since now (at most ticks), returning how many it sent.
    private static int UntilFired(World w, int n, int ticks)
    {
        var seen = new List<uint>();
        for (var i = 0; i < ticks && seen.Count < n; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (!seen.Contains(p.Id))
                {
                    seen.Add(p.Id);
                }
            }
        }
        return seen.Count;
    }

    private static void AssertRidersUnhurt(World w, List<Unit> riders)
    {
        foreach (var r in riders)
        {
            Assert.Same(r, w.Unit(r.Id));
            Assert.Equal(r.Def.MaxHp, r.HP);
            Assert.NotEqual(0u, r.Inside);
        }
    }

    [Fact]
    public void ContentIsATroopTruckForFifteen()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype(Carrier)!;
        Assert.Equal(Seats, a.Crew!.Passengers);
        Assert.Equal(c.ArmorClass("armored_vehicle"), a.Armor);
        Assert.Equal(MaxHp, a.MaxHp);
        Assert.InRange(a.Speed.Raw, c.Archetype("cargo_truck")!.Speed.Raw + 1, c.Archetype("technical")!.Speed.Raw - 1);
        Assert.Empty(a.Components); // no weapon of its own
        Assert.True(c.Faction("resistance")!.Archetypes[a.Index]);
    }

    [Fact]
    public void ASquadBoardsTheFirstDrivesTheRestRide()
    {
        var w = Arena.New(48);
        var carrier = w.Spawn(1, Carrier, 20, 20);
        var squad = new List<Unit>();
        for (var i = 0; i < Seats + 2; i++)
        {
            squad.Add(w.Spawn(1, "Rifleman", 8 + i % 6, 14 + i / 6));
        }
        w.Order(1, CommandKind.Enter, squad, carrier.Id);
        w.Run(400);

        Assert.NotEqual(0u, carrier.Driver);
        Assert.Equal(Seats, w.PassengersOf(carrier).Count);
        Assert.DoesNotContain(w.PassengersOf(carrier), p => p.Id == carrier.Driver);
        var left = Assert.Single(squad, u => u.Inside == 0); // 1 + 15 seats for 17
        Assert.Equal(OrderKind.Idle, left.Order.Kind);
        Assert.All(w.PassengersOf(carrier), p => Assert.False(World.IsVisibleTo(p, 1)));
        Assert.All(w.PassengersOf(carrier), p => Assert.False(p.CanAct));
        Assert.True(carrier.CanAct);

        // A full carrier takes no one more: the order is dropped at once.
        w.Order(1, CommandKind.Enter, [left], carrier.Id);
        w.Step();
        Assert.Equal(OrderKind.Idle, left.Order.Kind);

        // Driving: the riders come along and get out at the other end.
        w.Order(1, CommandKind.Move, [carrier], cell: new Cell(40, 30));
        w.Run(400);
        Assert.Equal(new Cell(40, 30), Cell.Of(carrier.Pos));
        w.Order(1, CommandKind.Exit, [carrier]);
        w.Step();
        Assert.Empty(w.PassengersOf(carrier));
        Assert.NotEqual(0u, carrier.Driver); // the driver stays at the wheel
        Assert.All(squad.Where(u => u.Id != carrier.Driver && u != left), u => Assert.True(World.Within(u.Pos, carrier.Pos, Fixed.FromInt(5))));
    }

    [Fact]
    public void WorkersRideButOnlySoldiersDriveAndEnemiesStayOut()
    {
        var w = Arena.New(32);
        var carrier = w.Spawn(1, Carrier, 10, 10);
        var worker = w.Spawn(1, "worker", 8, 10);
        var enemy = w.Spawn(2, "Rifleman", 12, 12);
        var tech = w.Spawn(1, "technical", 20, 20);

        w.Order(2, CommandKind.Enter, [enemy], carrier.Id);
        w.Order(1, CommandKind.Enter, [worker], carrier.Id);
        w.Order(1, CommandKind.Enter, [tech], carrier.Id); // vehicles don't ride
        w.Run(100);
        Assert.Equal(OrderKind.Idle, enemy.Order.Kind);
        Assert.Equal(0u, enemy.Inside);
        Assert.Equal(carrier.Id, worker.Inside); // a passenger, not the driver
        Assert.Equal(0u, carrier.Driver);
        Assert.Equal(0u, tech.Inside);
        Assert.False(carrier.CanAct);
    }

    [Fact]
    public void RidersCannotFireOutOrBeHit()
    {
        var w = Arena.New(32);
        var (carrier, riders) = Loaded(w, 10, 10);
        var enemy = w.Spawn(1, "Rifleman", 13, 10); // 3 cells away, well inside rifle range
        enemy.HP = 1_000_000;
        w.Run(200);
        Assert.Equal(1_000_000, enemy.HP); // nobody fired from the carrier
        Assert.All(riders, r => Assert.Equal(0u, r.AutoTarget));
        Assert.True(carrier.HP < MaxHp, "the enemy rifleman shot at the carrier");
        AssertRidersUnhurt(w, riders);
    }

    [Fact]
    public void UnloadOneChosenRiderThenAll()
    {
        var w = Arena.New(32);
        var (carrier, riders) = Loaded(w, 16, 16, Facing.East);
        var chosen = riders[7];
        w.Order(2, CommandKind.Exit, [chosen]);
        w.Step();
        Assert.Equal(0u, chosen.Inside);
        Assert.True(chosen.CanAct);
        Assert.True(chosen.Pos.X < carrier.Pos.X, "out at the back (it faces east)");
        Assert.Equal(Seats - 1, w.PassengersOf(carrier).Count);

        w.Order(2, CommandKind.Exit, [carrier]);
        w.Step();
        Assert.Empty(w.PassengersOf(carrier));
        Assert.All(riders, r => Assert.Equal(0u, r.Inside));
        Assert.NotEqual(0u, carrier.Driver);
        Assert.Equal(Seats, riders.Select(r => r.Pos).Distinct().Count()); // each on a cell of its own

        // Empty in the back, Exit lets the driver out as on any vehicle.
        w.Order(2, CommandKind.Exit, [carrier]);
        w.Step();
        Assert.Equal(0u, carrier.Driver);
    }

    // When the carrier is destroyed, the driver and then each rider roll the crew's survival chance
    // (50%) on the world's generator; survivors lose 40% of max HP and land behind the wreck.
    [Fact]
    public void RidersOfADestroyedCarrierBailOutByTheCrewRule()
    {
        (int Survivors, ulong Hash) Destroy(ulong seed)
        {
            var w = Arena.New(32, seed);
            var (carrier, riders) = Loaded(w, 16, 16);
            w.Step();
            carrier.HP = 0;
            w.Step();
            Assert.Null(w.Unit(carrier.Id));
            var alive = riders.Select(r => w.Unit(r.Id)).OfType<Unit>().ToList();
            Assert.All(alive, r => Assert.Equal(0u, r.Inside));
            Assert.All(alive, r => Assert.Equal(r.Def.MaxHp - r.Def.MaxHp * 40 / 100, r.HP));
            return (alive.Count, w.Hash());
        }

        var total = 0;
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var first = Destroy(seed);
            Assert.Equal(first, Destroy(seed));
            Assert.InRange(first.Survivors, 1, Seats - 1);
            total += first.Survivors;
        }
        Assert.InRange(total, 50, 100); // about half of 150
    }

    // The worst any weapon's first strike can do to the carrier: its whole salvo (one shot for
    // a weapon without salvos), every round a direct hit (or inside the full-damage ring).
    private static long WorstStrike(Warhead w, int damage, int salvo, int splashCenter, int armor) =>
        (long)w.Damage(damage, armor, Math.Max(100, splashCenter)) * salvo;

    [Fact]
    public void NoWeaponsFirstStrikeCanKillItButTheBallisticMissileAndTheHeavySalvo()
    {
        var c = TestContent.Mvp;
        var armor = c.ArmorClass("armored_vehicle");
        var killers = new List<string>();
        var checkedAny = 0;
        foreach (var comp in c.Components)
        {
            foreach (var e in comp.Effects)
            {
                var worst = e switch
                {
                    WeaponEffect we => WorstStrike(we.Warhead, we.Damage, we.SalvoSize, we.SplashCenter, armor),
                    TriggerEffect te => WorstStrike(te.Warhead, te.Damage, 1, 100, armor),
                    StrikeEffect se => WorstStrike(se.Warhead, se.Damage, 1, 100, armor),
                    _ => -1,
                };
                if (worst < 0)
                {
                    continue;
                }
                checkedAny++;
                if (worst >= MaxHp)
                {
                    killers.Add(comp.Id);
                }
            }
        }
        Assert.True(checkedAny > 15);
        // H4: a direct hit of the strategic bomber's very heavy bomb (2,000, tier 7) kills it too. The Kaman's dive (its rails
        // carry its blast: 520 x 250% = 1300) kills it too: the owner's "kills tanks and armour".
        // The stealth bomber's bunker buster (6,000, 100% against armour) too; its precision bomb (1,600 x 60%) does not.
        Assert.Equal(["strategic_bombs", "kaman_station_rail", "kaman_rail", "heavy_artillery_rockets", "bunker_busters", "ballistic_missile"], killers.Order().Reverse());

        // One heavy rocket, one twin missile salvo, the jeep's pair: survived with room to spare.
        Assert.Equal(432, c.Warhead("heavy_rocket")!.Damage(360, armor, 100));
        Assert.Equal(1100, 2 * c.Warhead("heavy_missile")!.Damage(550, armor, 100));
        Assert.Equal(1080, 2 * c.Warhead("heavy_rocket")!.Damage(450, armor, 100));
        Assert.Equal(1170, 30 * c.Warhead("mrl_rocket")!.Damage(113, armor, 100));
        // The ballistic missile kills it from anywhere in its inner ring; its outer ring does not.
        Assert.True(c.Warhead("ballistic")!.Damage(730, armor, 100) >= MaxHp);
        Assert.True(c.Warhead("ballistic")!.Damage(730, armor, 30) < MaxHp);
        // Everyone else's ballistic numbers are the heavy missile's.
        for (var i = 0; i < c.ArmorClasses.Count; i++)
        {
            if (i != armor)
            {
                Assert.Equal(c.Warhead("heavy_missile")!.Versus[i], c.Warhead("ballistic")!.Versus[i]);
            }
        }
    }

    // Live fire: each launcher's whole first salvo at a parked, loaded carrier leaves it standing
    // (hurt) and its riders untouched.
    [Theory]
    [InlineData("missile_truck", 70, 2, 900)]
    [InlineData("rocket_jeep", 35, 2, 500)]
    [InlineData("heavy_rocket_launcher", 40, 30, 600)]
    [InlineData("medium_rocket_launcher", 10, 6, 300)]
    [InlineData("light_rocket_truck", 5, 4, 300)]
    public void SurvivesALaunchersWholeFirstSalvo(string launcher, int cells, int salvo, int ticks)
    {
        var w = Arena.New(128);
        var (shooter, _) = w.SpawnCrewed(1, launcher, 5, 31, Facing.East);
        var (carrier, riders) = Loaded(w, 5 + cells, 31);
        w.Order(1, CommandKind.Attack, [shooter], carrier.Id);
        Assert.Equal(salvo, UntilFired(w, salvo, ticks));
        w.Order(1, CommandKind.Stop, [shooter]); // only the first salvo
        Settle(w);
        Assert.True(carrier.Alive);
        Assert.True(carrier.HP < MaxHp, $"{launcher}: no damage at all");
        AssertRidersUnhurt(w, riders);
    }

    // Single-shot weapons: the first round only.
    [Theory]
    [InlineData("Tank hunter", 6)]
    [InlineData("kornet", 20)]
    [InlineData("heavy_rocket_truck", 75)]
    public void SurvivesTheFirstRound(string shooterName, int cells)
    {
        var w = Arena.New(128);
        var shooter = shooterName == "heavy_rocket_truck"
            ? w.SpawnCrewed(1, shooterName, 5, 31, Facing.East).Vehicle
            : w.Spawn(1, shooterName, 5, 31, Facing.East);
        var (carrier, riders) = Loaded(w, 5 + cells, 31);
        w.Order(1, CommandKind.Attack, [shooter], carrier.Id);
        Assert.Equal(1, UntilFired(w, 1, 400));
        w.Order(1, CommandKind.Stop, [shooter]);
        Settle(w);
        Assert.True(carrier.Alive);
        AssertRidersUnhurt(w, riders);
    }

    [Fact]
    public void SurvivesAOneWayAttackDrone()
    {
        var w = Arena.New(128);
        var (truck, _) = w.SpawnCrewed(1, "drone_truck", 5, 31, Facing.East);
        var (carrier, riders) = Loaded(w, 60, 31);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AttackGround, Units = [truck.Id], Cell = Cell.Of(carrier.Pos) });
        Assert.Equal(1, UntilFired(w, 1, 200));
        Settle(w, 1200);
        Assert.True(carrier.Alive);
        Assert.InRange(carrier.HP, MaxHp - 260, MaxHp - 104); // hit by the blast (40% at its edge)
        AssertRidersUnhurt(w, riders);
    }

    // The ballistic missile lands up to 3 cells off per axis: inside its 3.3-cell inner ring
    // (16 of 20 seeds here) it destroys the carrier; in the outer ring it takes 30% (372).
    private static (bool Killed, int DistRaw, int Dead) Ballistic(ulong seed)
    {
        var w = Arena.New(256, seed);
        var (truck, _) = w.SpawnCrewed(1, "ballistic_truck", 5, 31, Facing.East);
        var (carrier, riders) = Loaded(w, 125, 31);
        w.Order(1, CommandKind.Attack, [truck], carrier.Id);
        Assert.Equal(1, UntilFired(w, 1, 400));
        var last = default(Vec);
        while (w.Projectiles.Count > 0)
        {
            w.Step();
            if (w.Detonations.Count > 0)
            {
                last = w.Detonations[0].At;
            }
        }
        return (w.Unit(carrier.Id) is null, (int)(last - carrier.Pos).Len().Raw, riders.Count(r => w.Unit(r.Id) is null));
    }

    [Fact]
    public void TheTacticalBallisticMissileDestroysIt()
    {
        var kills = 0;
        var log = new List<string>();
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (killed, dist, dead) = Ballistic(seed);
            log.Add($"seed {seed}: {(killed ? "killed" : "survived")} {dist * 100 / Fixed.OneRaw / 100.0} cells off, {dead} riders dead");
            Assert.True(killed == dist <= 33 * Fixed.OneRaw / 10, log[^1]);
            kills += killed ? 1 : 0;
        }
        Assert.True(kills >= 14, string.Join("; ", log));
    }

    [Fact]
    public void AFullHeavyRocketSalvoDestroysIt()
    {
        var w = Arena.New(128);
        var (truck, _) = w.SpawnCrewed(1, "heavy_rocket_truck", 5, 31, Facing.East);
        var (carrier, _) = Loaded(w, 80, 31);
        w.Order(1, CommandKind.Attack, [truck], carrier.Id);
        w.Run(80 + 12 * 40 + 300);
        Settle(w);
        Assert.Equal(0, truck.Charges[0]);
        Assert.Null(w.Unit(carrier.Id));
    }
}
