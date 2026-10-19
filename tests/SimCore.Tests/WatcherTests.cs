using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// The Watcher (watcher_drone; the Sentinel faction's MQ-9-type strike drone, docs/design.md,
// "Sentinel faction"): launched by its ground control station, it orbits high,
// sees 35 cells round the point under it and fires two missiles on orders at enemy ground units in
// that sight: 100% hits on a target standing still, 80% on a moving one; a hit destroys soldiers,
// light and medium vehicles, a heavy vehicle 80% of the time, and hurts a building badly. Empty, it
// orbits on (the owner's flight rules, E2); a Rearm order sends it home, the station rearms it and
// sends it back. Air defence engages it as the "watcher"
// class: 40% at best for the air defence vehicle, 10% for the AA soldier.
public class WatcherTests
{
    private const int N = 200;
    private const long Height = 150 * 512; // its cruise height over flat ground, raw

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    // Overhead: a Watcher put down in flight over (x, y) (the sandbox's way), settled into its orbit.
    private static Unit Overhead(World w, int x, int y)
    {
        var d = w.Spawn(1, "watcher_drone", x, y);
        w.Run(5);
        return d;
    }

    // Strike orders drone d at target t and runs until its missile has gone off (or maxTicks pass);
    // it returns the strike reports seen on the way (the launch, then the impact).
    private static List<StrikeReport> Strike(World w, Unit d, Unit t, int maxTicks = 400)
    {
        w.Order(1, CommandKind.Attack, [d], target: t.Id);
        var reports = new List<StrikeReport>();
        for (var i = 0; i < maxTicks; i++)
        {
            w.Step();
            reports.AddRange(w.StrikeReports);
            if (reports.Any(r => r.Outcome != StrikeOutcome.Launched))
            {
                break;
            }
        }
        return reports;
    }

    [Fact]
    public void TheStationLaunchesItAndItOrbitsHighWithTwoStrikes()
    {
        var w = Arena.NewSentinel(N);
        var station = w.Spawn(1, "watcher_station", 20, 100);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(120, 100), ability: "watcher_launch");
        w.Run(2);
        var drone = Assert.Single(w.All("watcher_drone"));
        Assert.True(drone.Altitude.Raw < 3 * 512, $"takes off from the apron: {drone.Altitude.Raw}");
        Assert.Equal(2, World.StrikesLeft(drone));
        Assert.Equal(0, station.Charges[0]);
        w.Run(1200); // the climb (19 s) and the 95-cell flight
        var center = new Cell(120, 100).Center();
        long lo = long.MaxValue, hi = 0, flown = 0;
        for (var i = 0; i < 600; i++)
        {
            var before = drone.Pos;
            w.Step();
            flown += (drone.Pos - before).Len().Raw;
            var r = (drone.Pos - center).Len().Raw;
            (lo, hi) = (Math.Min(lo, r), Math.Max(hi, r));
            Assert.Equal(Height, drone.Altitude.Raw); // flat ground: it holds 150 m
        }
        Assert.InRange(Cells(lo), 18, 26); // the 25-cell circle (it settles a little inside)
        Assert.InRange(Cells(hi), 20, 28);
        Assert.InRange(flown * 20 / 600 / Fixed.OneRaw, 7, 8); // 8 cells/s
        Assert.Equal(1, drone.Source == station.Id ? 1 : 0);
    }

    [Fact]
    public void AnIdleTargetIsAlwaysHit()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var w = Arena.NewSentinel(N, seed);
            var d = Overhead(w, 100, 100);
            var tech = w.SpawnCrewed(2, "technical", 105, 100).Vehicle;
            var reports = Strike(w, d, tech);
            var launch = reports.First(r => r.Outcome == StrikeOutcome.Launched);
            Assert.False(launch.Moving);
            Assert.Equal(100, launch.Chance);
            Assert.Equal(StrikeOutcome.Destroyed, reports.Last().Outcome);
            Assert.False(tech.Alive);
        }
    }

    [Fact]
    public void AMovingTargetIsHitEightyPercentOfTheTime()
    {
        int hits = 0, misses = 0;
        for (ulong seed = 1; seed <= 150; seed++)
        {
            var w = Arena.NewSentinel(N, seed);
            var d = Overhead(w, 100, 100);
            var tech = w.SpawnCrewed(2, "technical", 100, 95).Vehicle;
            w.Order(2, CommandKind.Move, [tech], cell: new Cell(100, 140)); // drives south under it
            w.Run(20);
            Assert.NotEqual(default, tech.Vel);
            var reports = Strike(w, d, tech);
            var launch = reports.First(r => r.Outcome == StrikeOutcome.Launched);
            Assert.True(launch.Moving);
            Assert.Equal(80, launch.Chance);
            var end = reports.Last();
            if (launch.Roll < 80)
            {
                Assert.Equal(StrikeOutcome.Destroyed, end.Outcome);
                Assert.False(tech.Alive);
                hits++;
            }
            else
            {
                Assert.Equal(StrikeOutcome.Missed, end.Outcome);
                Assert.True(tech.Alive, "a miss lands 4 to 7 cells off, out of its splash");
                Assert.Equal(tech.Def.MaxHp, tech.HP);
                var off = Cells((end.At - tech.Pos).Len().Raw);
                Assert.InRange(off, 1, 20); // behind it, and it drove on
                misses++;
            }
        }
        Assert.InRange(hits * 100 / (hits + misses), 72, 88); // 80% of 150 rolls
    }

    [Theory]
    [InlineData("rocket_jeep")]
    [InlineData("medium_rocket_launcher")]
    [InlineData("drone_truck")]
    [InlineData("light_rocket_truck")]
    public void OneStrikeDestroysAMediumVehicle(string vehicle)
    {
        var w = Arena.NewSentinel(N, 3);
        var d = Overhead(w, 100, 100);
        var v = w.SpawnCrewed(2, vehicle, 104, 102).Vehicle;
        Assert.Equal("medium_vehicle", w.Content.ArmorClasses[v.Def.Archetype.Armor]);
        var reports = Strike(w, d, v);
        Assert.Equal(StrikeOutcome.Destroyed, reports.Last().Outcome);
        Assert.False(v.Alive);
    }

    [Theory]
    [InlineData("ballistic_truck")]
    [InlineData("heavy_rocket_launcher")]
    public void AHeavyVehicleIsDestroyedEightyPercentOfTheTime(string vehicle)
    {
        int destroyed = 0, survived = 0;
        for (ulong seed = 1; seed <= 120; seed++)
        {
            var w = Arena.NewSentinel(N, seed);
            var d = Overhead(w, 100, 100);
            var v = w.SpawnCrewed(2, vehicle, 104, 102).Vehicle;
            var reports = Strike(w, d, v);
            var end = reports.Last();
            Assert.Equal(80, end.Chance); // the heavy roll
            if (end.Roll < 80)
            {
                Assert.Equal(StrikeOutcome.Destroyed, end.Outcome);
                Assert.False(v.Alive);
                destroyed++;
            }
            else
            {
                Assert.Equal(StrikeOutcome.Damaged, end.Outcome);
                Assert.True(v.Alive);
                Assert.Equal(v.Def.MaxHp / 4, v.HP); // left with 25% of its HP
                survived++;
            }
        }
        Assert.InRange(destroyed * 100 / (destroyed + survived), 70, 90);
    }

    [Fact]
    public void SoldiersAndLightVehiclesDieAndABuildingTakesHeavyDamage()
    {
        var w = Arena.NewSentinel(N, 2);
        var d = Overhead(w, 100, 100);
        var rifleman = w.Spawn(2, "Rifleman", 104, 100);
        var buddy = w.Spawn(2, "Rifleman", 105, 101); // beside him: the splash
        var bike = w.SpawnCrewed(2, "dirt_bike", 90, 106).Vehicle;
        var depot = w.Spawn(2, "supply_depot", 96, 88);
        Assert.Equal(StrikeOutcome.Destroyed, Strike(w, d, rifleman).Last().Outcome);
        Assert.False(buddy.Alive, "the 2.5-cell splash kills the soldier beside him");
        w.Run(70); // the 3 s reload
        Assert.Equal(StrikeOutcome.Destroyed, Strike(w, d, bike).Last().Outcome);
        Assert.False(bike.Alive);
        Assert.Equal(0, World.StrikesLeft(d));

        var w2 = Arena.NewSentinel(N, 2);
        var d2 = Overhead(w2, 100, 100);
        var block = w2.Spawn(2, "supply_depot", 100, 96);
        Assert.Equal(StrikeOutcome.Damaged, Strike(w2, d2, block).Last().Outcome);
        Assert.Equal(1000 - 720, block.HP); // 600 x 120%
        Assert.True(depot.Alive);
    }

    [Fact]
    public void ItStrikesOnlyWhatIsInItsSightAndFliesOverToTheRest()
    {
        var w = Arena.NewSentinel(N, 4);
        var d = Overhead(w, 50, 100);
        var far = w.SpawnCrewed(2, "technical", 150, 100).Vehicle; // 100 cells off, far outside its 35
        w.Order(1, CommandKind.Attack, [d], target: far.Id);
        var launchedAt = -1L;
        for (var i = 0; i < 900 && launchedAt < 0; i++)
        {
            w.Step();
            if (w.StrikeReports.Any(r => r.Outcome == StrikeOutcome.Launched))
            {
                launchedAt = Cells((d.Pos - far.Pos).Len().Raw);
                Assert.True(i > 100, $"it had to fly over first: fired at tick {i}");
            }
        }
        Assert.InRange(launchedAt, 0, 35);
        Assert.Equal(1, World.StrikesLeft(d));
    }

    [Fact]
    public void TwoStrikesThenItOrbitsOnUntilARearmOrderAndIsRearmedAndRelaunched()
    {
        var w = Arena.NewSentinel(N, 5);
        var station = w.Spawn(1, "watcher_station", 30, 100);
        var targets = new[] { w.SpawnCrewed(2, "technical", 100, 100).Vehicle, w.SpawnCrewed(2, "technical", 104, 100).Vehicle,
                              w.SpawnCrewed(2, "technical", 108, 100).Vehicle };
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(100, 100), ability: "watcher_launch");
        w.Run(2);
        var drone = Assert.Single(w.All("watcher_drone"));
        w.Run(900); // up and over the targets
        Strike(w, drone, targets[0]);
        w.Run(70);
        Strike(w, drone, targets[1]);
        Assert.Equal(0, World.StrikesLeft(drone));
        Assert.False(targets[0].Alive || targets[1].Alive);
        Assert.DoesNotContain(Strike(w, drone, targets[2], 100), r => r.Outcome == StrikeOutcome.Launched); // nothing left to fire
        w.Run(600);
        Assert.False(drone.Homing, "empty: it orbits on (E2: never home because it is empty)");
        w.Order(1, CommandKind.Rearm, [drone]);
        w.Step();
        Assert.True(drone.Homing, "the Rearm order sends it home");
        Assert.Equal(HomeReason.Rearm, drone.HomeReason);
        var recovered = false;
        for (var i = 0; i < 2400 && !recovered; i++)
        {
            var low = drone.Altitude.Raw;
            w.Step();
            recovered = drone.Recovered;
            if (recovered)
            {
                Assert.True(low <= 4 * 512, $"it lands on the apron, not from 150 m: {low}");
            }
        }
        Assert.True(recovered);
        Assert.Equal(1, station.Charges[0]);
        Assert.True(station.RelaunchPending);
        Assert.True(station.Cooldown > 500, "30 s to refuel and rearm");
        w.Run(station.Cooldown + 3);
        var fresh = Assert.Single(w.All("watcher_drone"));
        Assert.NotEqual(drone.Id, fresh.Id);
        Assert.Equal(2, World.StrikesLeft(fresh));
        Assert.False(station.RelaunchPending);
        Assert.Equal(new Cell(100, 100), Cell.Of(fresh.OrbitAt));
        w.Run(900);
        Assert.Equal(Height, fresh.Altitude.Raw);
        Assert.Equal(StrikeOutcome.Destroyed, Strike(w, fresh, targets[2]).Last().Outcome);
    }

    [Fact]
    public void EnduranceIsTwentyMinutesThenItGoesHomeToRefuel()
    {
        var w = Arena.NewSentinel(N, 6);
        var station = w.Spawn(1, "watcher_station", 30, 100);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(120, 100), ability: "watcher_launch");
        w.Run(2);
        var drone = Assert.Single(w.All("watcher_drone"));
        Assert.InRange(drone.Lifetime, 20 * 60 * World.TicksPerSecond - 3, 20 * 60 * World.TicksPerSecond);
        var homingAt = -1;
        for (var t = 0; t < 24100 && drone.Alive; t++)
        {
            w.Step();
            if (homingAt < 0 && drone.Homing)
            {
                homingAt = t;
            }
        }
        Assert.True(drone.Recovered, "back at its station, not lost");
        Assert.InRange(homingAt, 22000, 23800);
        Assert.True(station.RelaunchPending);
    }

    [Fact]
    public void ItSeesThirtyFiveCellsRoundThePointUnderItOverEverything()
    {
        var w = Arena.NewSentinel(N, 7, s => s with { Fog = true });
        var d = Overhead(w, 100, 100);
        var reds = new List<Unit>();
        for (var r = 0; r <= 60; r += 4)
        {
            reds.Add(w.Spawn(2, "Rifleman", 100 + r, 100));
            reds.Add(w.Spawn(2, "Rifleman", 100, 100 + r));
        }
        int seenNear = 0, checkedFar = 0;
        for (var i = 0; i < 400; i++)
        {
            var at = d.Pos; // vision runs first in a tick, from where it was
            w.Step();
            foreach (var u in reds)
            {
                var cells = Cells((u.Pos - at).Len().Raw);
                var seen = World.IsVisibleTo(u, 1);
                if (cells <= 32)
                {
                    Assert.True(seen, $"tick {i}: {cells} cells from the point under it, unseen");
                    seenNear++;
                }
                else if (cells >= 38)
                {
                    Assert.False(seen, $"tick {i}: {cells} cells off, seen");
                    checkedFar++;
                }
            }
        }
        Assert.True(seenNear > 0 && checkedFar > 0);
    }

    [Theory]
    [InlineData("sam_vehicle", 250, 400)]
    [InlineData("manpads", 40, 100)]
    public void AirDefenceEngagesItAsTheWatcherClass(string shooter, int lo, int hi)
    {
        // The air defence vehicle (40% at best) and the AA soldier (10%) fire at it; every chance
        // stays under its cap, and the best shots come near it.
        var w = Arena.NewSentinel(N, 8);
        var d = Overhead(w, 100, 100);
        if (shooter == "sam_vehicle")
        {
            w.SpawnCrewed(2, shooter, 110, 100);
        }
        else
        {
            w.Spawn(2, shooter, 105, 105);
        }
        int best = 0, shots = 0;
        for (var i = 0; i < 3000 && d.Alive; i++)
        {
            w.Step();
            foreach (var b in w.AirBursts.Where(b => b.Target == d.Id && b.Kind is AirBurstKind.Kill or AirBurstKind.Miss))
            {
                shots++;
                best = Math.Max(best, b.KillPermille);
                Assert.True(b.KillPermille <= hi * 10, $"{shooter}: {b.KillPermille} permille");
            }
        }
        Assert.True(shots > 0, $"{shooter} never fired");
        Assert.InRange(best, lo, hi * 10);
    }

    [Fact]
    public void ItIsEasierToHitThanTheOrlanAndHarderThanAnAttackDrone()
    {
        static int Best(World w, Unit target, int ticks)
        {
            var best = 0;
            for (var i = 0; i < ticks && target.Alive; i++)
            {
                w.Step();
                foreach (var b in w.AirBursts.Where(b => b.Target == target.Id && b.Kind is AirBurstKind.Kill or AirBurstKind.Miss))
                {
                    best = Math.Max(best, b.KillPermille);
                }
            }
            return best;
        }
        var w1 = Arena.NewSentinel(N, 9);
        var watcher = Overhead(w1, 100, 100);
        w1.SpawnCrewed(2, "sam_vehicle", 110, 100);
        var w2 = Arena.NewSentinel(N, 9);
        var orlan = w2.Spawn(2, "scout_drone", 100, 100);
        w2.SpawnCrewed(1, "sam_vehicle", 110, 100);
        var watcherBest = Best(w1, watcher, 1500);
        var orlanBest = Best(w2, orlan, 1500);
        Assert.True(watcherBest > orlanBest, $"watcher {watcherBest} vs orlan {orlanBest}");
        Assert.True(orlanBest > 0);
        Assert.True(watcherBest <= 400, "an attack drone's best is 950");
    }

    [Fact]
    public void StrikesAreDeterministic()
    {
        static ulong Run()
        {
            var w = Arena.NewSentinel(N, 11);
            var d = Overhead(w, 100, 100);
            var a = w.SpawnCrewed(2, "heavy_rocket_launcher", 104, 100).Vehicle;
            var b = w.SpawnCrewed(2, "technical", 100, 95).Vehicle;
            w.Order(2, CommandKind.Move, [b], cell: new Cell(100, 140));
            Strike(w, d, a);
            w.Run(70);
            Strike(w, d, b);
            w.Run(50);
            return w.Hash();
        }
        Assert.Equal(Run(), Run());
    }
}
