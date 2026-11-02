using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// The scout drone (scout_drone; the game repo's docs/air-defense-design.md, section 10): an unarmed
// fixed wing launched from the scout truck or a sniper's station, flying high and orbiting the point
// it is sent to, spotting for its side. Air defence engages it, but its best kill chance is 20% for
// the air defence vehicle and 5% for the AA soldier; nothing else reaches it.
public class ScoutDroneTests
{
    private const int N = 400;
    private const long Height = 80 * 512; // its cruise height over flat ground, raw

    private static Unit Launch(World w, Unit launcher, Cell at, string ability = "scout_catapult")
    {
        w.Order(launcher.Owner, CommandKind.UseAbility, [launcher], cell: at, ability: ability);
        w.Run(2);
        return w.All("scout_drone").Single(d => d.Source == launcher.Id);
    }

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    [Fact]
    public void FromTheTruckItClimbsFliesToThePointAndOrbitsItHigh()
    {
        var w = Arena.New(N);
        var truck = w.SpawnCrewed(1, "scout_truck", 100, 300).Vehicle;
        var drone = Launch(w, truck, new Cell(250, 120));
        Assert.True(drone.Altitude.Raw < 4 * 512, $"leaves the catapult low: {drone.Altitude.Raw}");
        Assert.Equal(0, truck.Charges[0]);
        w.Run(1600); // 80 s: the climb and the 230-cell flight
        var center = new Cell(250, 120).Center();
        long lo = long.MaxValue, hi = 0;
        var flown = 0L;
        for (var i = 0; i < 600; i++)
        {
            var before = drone.Pos;
            w.Step();
            flown += (drone.Pos - before).Len().Raw;
            var r = (drone.Pos - center).Len().Raw;
            (lo, hi) = (Math.Min(lo, r), Math.Max(hi, r));
            Assert.Equal(Height, drone.Altitude.Raw); // flat ground: it holds 80 m
        }
        Assert.InRange(Cells(lo), 14, 21); // the 20-cell circle (it settles a little inside)
        Assert.InRange(Cells(hi), 15, 23);
        Assert.InRange(flown * 20 / 600 / Fixed.OneRaw, 4, 5); // 5 cells/s: it never stops
        Assert.True(drone.Alive);
    }

    [Fact]
    public void SentElsewhereItOrbitsThere()
    {
        var w = Arena.New(N);
        var drone = w.Spawn(1, "scout_drone", 100, 100);
        Assert.True(drone.Alive);
        w.Order(1, CommandKind.Move, [drone], cell: new Cell(200, 100));
        w.Run(800);
        Assert.InRange(Cells((drone.Pos - new Cell(200, 100).Center()).Len().Raw), 10, 23);
        Assert.Equal(Height, drone.Altitude.Raw);
    }

    // The owner (2026-11-02): it has fuel and is destroyed when the fuel runs out, wherever it is; no trip
    // home, no recovery. It falls as a small wreck (its loss block) and goes off on the ground under it.
    [Fact]
    public void OutOfFuelItCrashesWhereItIsAndNeverComesHome()
    {
        var w = Arena.New(N);
        var truck = w.SpawnCrewed(1, "scout_truck", 100, 300).Vehicle;
        var drone = Launch(w, truck, new Cell(250, 120));
        Assert.Equal(4800, drone.Tank);
        var lostAt = -1;
        AirLoss? loss = null;
        var lastFar = 0L;
        for (var t = 0; t < 4900 && lostAt < 0; t++)
        {
            w.Step();
            Assert.False(drone.Homing, $"it never turns for home (tick {t})");
            if (drone.Alive)
            {
                lastFar = Cells((drone.Pos - truck.Pos).Len().Raw);
                if (t >= 2000)
                {
                    Assert.InRange(Cells((drone.Pos - new Cell(250, 120).Center()).Len().Raw), 10, 24); // on its orbit to the end
                }
            }
            if (w.AirLosses.Count > 0)
            {
                (lostAt, loss) = (t, w.AirLosses.Single());
            }
        }
        Assert.InRange(lostAt, 4790, 4800); // the whole 4 min on station
        Assert.False(drone.Recovered, "lost, not recovered");
        Assert.Equal(KillCause.Fuel, loss!.Value.Cause);
        Assert.True(lastFar > 120, $"it ran dry over its orbit, {lastFar} cells from the truck");
        var wreck = Assert.Single(w.Wrecks);
        Assert.Equal(drone.Id, wreck.Id);
        var impact = -1;
        for (var t = 0; t < 400 && impact < 0; t++)
        {
            w.Step();
            if (w.WreckImpacts.Count > 0)
            {
                impact = t;
                Assert.InRange(Cells((w.WreckImpacts[0].At - new Cell(250, 120).Center()).Len().Raw), 5, 40); // it came down near its orbit
            }
        }
        Assert.True(impact > 0, "the wreck fell and went off");
        Assert.Empty(w.Wrecks);
        Assert.Equal(0, truck.Charges[0]); // the truck has no drone until it gets a new one
        Assert.Empty(w.All("scout_drone"));
    }

    [Fact]
    public void ReturnAndRearmOrdersDoNotBringItHome()
    {
        var w = Arena.New(N);
        var truck = w.SpawnCrewed(1, "scout_truck", 100, 300).Vehicle;
        var drone = Launch(w, truck, new Cell(250, 120));
        w.Run(600);
        w.Order(1, CommandKind.ReturnToBase, [drone]);
        w.Run(20);
        w.Order(1, CommandKind.Rearm, [drone]);
        w.Run(600);
        Assert.False(drone.Homing);
        Assert.InRange(Cells((drone.Pos - new Cell(250, 120).Center()).Len().Raw), 10, 24);
    }

    [Fact]
    public void WithItsLauncherGoneItOrbitsUntilItRunsDry()
    {
        var w = Arena.New(N);
        var truck = w.SpawnCrewed(1, "scout_truck", 100, 300).Vehicle;
        var drone = Launch(w, truck, new Cell(250, 120));
        w.Run(100);
        truck.HP = 0;
        w.Run(4800);
        Assert.False(drone.Alive);
        Assert.False(drone.Recovered);
    }

    // The truck's next drone: none while its own flies, none away from a depot; at a depot a new one
    // comes 30 s after the old one is lost (out of fuel as when shot down), for 300 supply.
    [Fact]
    public void AfterItsDroneIsLostTheTruckNeedsANewOneFromADepot()
    {
        var w = Arena.New(N);
        var truck = w.SpawnCrewed(1, "scout_truck", 100, 300).Vehicle;
        var drone = Launch(w, truck, new Cell(150, 250));
        w.Run(4900); // it runs dry
        Assert.False(drone.Alive);
        Assert.Equal(0, truck.Charges[0]);
        w.Run(1200); // no depot near: no new drone
        Assert.Equal(0, truck.Charges[0]);
        var depot = w.Spawn(1, "supply_depot", 104, 300);
        var stock = depot.Stock;
        w.Run(620);
        Assert.Equal(1, truck.Charges[0]);
        Assert.Equal(stock - 300, depot.Stock);
        Launch(w, truck, new Cell(150, 150));
        Assert.Single(w.All("scout_drone"));
    }

    [Fact]
    public void OneAloftAtATimeAndNoDepotReloadWhileItFlies()
    {
        var w = Arena.New(N);
        var truck = w.SpawnCrewed(1, "scout_truck", 100, 300).Vehicle;
        w.Spawn(1, "supply_depot", 104, 300);
        var drone = Launch(w, truck, new Cell(250, 120));
        w.Run(1500); // far longer than the 30 s reload
        Assert.Equal(0, truck.Charges[0]);
        drone.HP = 0; // lost
        w.Run(700);
        Assert.Equal(1, truck.Charges[0]); // the depot replaces it: 30 s
        Launch(w, truck, new Cell(150, 150));
        Assert.Single(w.All("scout_drone"));
    }

    [Fact]
    public void TheSniperDropsAStationThatLaunchesOne()
    {
        var w = Arena.New(N);
        var sniper = w.Spawn(1, "Marksman", 100, 300);
        w.Order(1, CommandKind.UseAbility, [sniper], cell: new Cell(101, 300), ability: "scout_station_kit");
        w.Run(80);
        var station = Assert.Single(w.All("scout_station"));
        var drone = Launch(w, station, new Cell(150, 250), "scout_station_launch");
        w.Run(1200);
        Assert.True(drone.Alive);
        Assert.Equal(Height, drone.Altitude.Raw);
        drone.HP = 0; // lost: the single-use station goes 20 s later
        w.Run(10);
        Assert.True(station.Alive);
        w.Run(420);
        Assert.False(station.Alive);
    }

    // Its drone running dry is a loss like any other: the single-use station goes 20 s later.
    [Fact]
    public void TheStationGoesWhenItsDroneRunsDry()
    {
        var w = Arena.New(N);
        var sniper = w.Spawn(1, "Marksman", 100, 300);
        w.Order(1, CommandKind.UseAbility, [sniper], cell: new Cell(101, 300), ability: "scout_station_kit");
        w.Run(80);
        var station = Assert.Single(w.All("scout_station"));
        var drone = Launch(w, station, new Cell(150, 250), "scout_station_launch");
        w.Run(4800);
        Assert.False(drone.Alive);
        Assert.False(drone.Recovered);
        Assert.Equal(0, station.Charges[0]);
        Assert.True(station.Alive);
        w.Run(420);
        Assert.False(station.Alive);
    }

    [Fact]
    public void GuardUnitsFireAtWhatItSpots()
    {
        // A Kornet team (9 cells of vision, 28.8 of range) and an enemy technical 20 cells off: unseen,
        // so no shot, until a scout drone orbits over it.
        static int Shots(bool scout)
        {
            var w = Arena.New(N);
            var kornet = w.Spawn(1, "kornet", 200, 220);
            var tech = w.SpawnCrewed(2, "technical", 200, 200).Vehicle;
            if (scout)
            {
                w.Spawn(1, "scout_drone", 200, 200);
            }
            w.Run(400);
            return (int)(tech.Def.MaxHp - tech.HP) + (kornet.LastShot != 0 ? 1 : 0);
        }
        Assert.Equal(0, Shots(scout: false));
        Assert.True(Shots(scout: true) > 0);
    }

    [Fact]
    public void NoGunReachesIt()
    {
        var w = Arena.New(N);
        var drone = w.Spawn(2, "scout_drone", 200, 200);
        w.Spawn(1, "Rifleman", 200, 205);
        w.Spawn(1, "Drone hunter", 202, 205); // the anti-armor soldier's instant AA missile
        w.SpawnCrewed(1, "technical", 198, 205);
        w.Run(600);
        Assert.Equal(drone.Def.MaxHp, drone.HP);
    }

    // The kill chance of the best possible shot: tail-on, from right under it, no crossing, tracked
    // for the full bonus. Every other shot is worse.
    private static int BestShot(string shooter, string weapon, int launchCells = 0)
    {
        var w = Arena.New(N);
        var aa = shooter == "sam_vehicle" ? w.SpawnCrewed(1, shooter, 200, 200).Vehicle : w.Spawn(1, shooter, 200, 200);
        var target = w.Spawn(2, "scout_drone", 200, 200);
        target.Vel = new Vec(Fixed.Zero, Fixed.FromRaw(-256));
        var wd = aa.Def.Weapons[World.InterceptorWeapon(aa.Def)];
        Assert.Equal(weapon == "sam_missiles" ? 2200 : 1400, (int)(wd.Range.Raw * 10 / Fixed.OneRaw));
        var ic = wd.Effect.Interceptor!;
        var m = new Projectile(9999, aa, wd, target.Pos, target.Id)
        {
            Vel = new Vec(Fixed.Zero, Fixed.FromRaw(-wd.Effect.ProjectileSpeed.Raw)),
            LaunchRange = Fixed.FromInt(launchCells),
            LaunchCross = 0,
            Tracked = ic.LockTicks + ic.TrackFull,
        };
        return w.KillPermille(m, new AirBody(target), ic);
    }

    [Fact]
    public void AtBestTheAirDefenceVehicleKillsItOneTimeInFive() => Assert.Equal(200, BestShot("sam_vehicle", "sam_missiles"));

    [Fact]
    public void AtBestTheAaSoldierKillsItOneTimeInTwenty() => Assert.Equal(50, BestShot("manpads", "igla"));

    // AA under (or off to the side of) a scout drone orbiting at 80 m: it detects it, fires (spending
    // missiles) and rolls at most the cap, less from further out.
    private static List<AirBurst> Engage(string shooter, int offset, int ticks = 3000, ulong seed = 1)
    {
        var w = Arena.New(N, seed);
        var aa = shooter == "sam_vehicle" ? w.SpawnCrewed(1, shooter, 200 + offset, 200).Vehicle : w.Spawn(1, shooter, 200 + offset, 200);
        w.Spawn(2, "scout_drone", 200, 200);
        var bursts = new List<AirBurst>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            bursts.AddRange(w.AirBursts);
        }
        Assert.True(aa.Charges[0] < aa.Def.StockMax[0], "it fired at the scout");
        return bursts;
    }

    [Theory]
    [InlineData("sam_vehicle", 200)]
    [InlineData("manpads", 50)]
    public void AirDefenceFiresAtItButRollsAtMostItsCap(string shooter, int cap)
    {
        var near = Engage(shooter, 0).Where(b => b.Kind is AirBurstKind.Kill or AirBurstKind.Miss).ToList();
        Assert.NotEmpty(near);
        Assert.All(near, b => Assert.InRange(b.KillPermille, 1, cap));
        Assert.All(near, b => Assert.Equal(b.Roll < b.KillPermille, b.Kind == AirBurstKind.Kill));
        Assert.True(near.Max(b => b.KillPermille) >= cap * 6 / 10, $"close in it gets near the cap: {near.Max(b => b.KillPermille)}");
    }

    [Theory]
    [InlineData("sam_vehicle", "sam_missiles", 200, 200)]
    [InlineData("manpads", "igla", 130, 50)]
    public void FromFurtherOutTheChanceIsLower(string shooter, string weapon, int cells, int cap)
    {
        var far = BestShot(shooter, weapon, cells);
        Assert.InRange(far, 1, cap * 95 / 100); // the slant range at launch costs it, as against a drone
        Assert.True(BestShot(shooter, weapon, cells / 2) > far);
        // Fired at from 170 cells off its orbit, the vehicle never reaches the cap.
        if (shooter == "sam_vehicle")
        {
            var bursts = Engage(shooter, 170).Where(b => b.KillPermille > 0).ToList();
            Assert.NotEmpty(bursts);
            Assert.All(bursts, b => Assert.InRange(b.KillPermille, 1, cap - 10));
        }
    }

    [Fact]
    public void ItMostlySurvivesAFullLoadOfMissiles()
    {
        // Over 20 seeds the vehicle's ten missiles bring it down far less often than a drone.
        var kills = 0;
        var shots = 0;
        for (var seed = 1UL; seed <= 20; seed++)
        {
            var b = Engage("sam_vehicle", 0, 1200, seed).Where(x => x.Kind is AirBurstKind.Kill or AirBurstKind.Miss).ToList();
            shots += b.Count;
            kills += b.Count(x => x.Kind == AirBurstKind.Kill);
        }
        Assert.True(shots >= 20, $"shots {shots}");
        Assert.InRange(kills * 100 / shots, 3, 25); // per missile: about the cap or less
    }
}
