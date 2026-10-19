using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The owner's flight rules for today's aircraft (night milestone E2, 2026-10-19; docs/design.md,
// "Flight rules"): an aircraft goes home on its own only to refuel (bingo), refuels only there and
// resumes its previous task; it never goes home because it is out of ammunition; it rearms (and is
// repaired) only on a Rearm order (command 29), then resumes its previous task; ReturnToBase
// refuels it and holds it; a ground control station relaunches a drone that came home without a
// Rearm order with the stocks and HP it had; and nothing picks targets on its own (Guard, the one
// automatic mode, comes with the mission layer).
public class AirRulesTests
{
    private const int N = 200;

    private static Airfield Field(World w) => AirfieldFlightTests.Field(w, [(0, 0)]);

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    private static int RunUntil(World w, Func<bool> done, int max = 12000)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            if (done())
            {
                return t;
            }
        }
        return -1;
    }

    private static AirfieldStation StationOf(Airfield af, uint unit) => af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == unit);

    // Launched: station kind launches its drone at `to` and returns it once it is up at its height.
    private static (Unit Station, Unit Drone) Launched(World w, string station, string ability, Cell at, Cell to)
    {
        var s = w.Spawn(1, station, at.X, at.Y);
        w.Order(1, CommandKind.UseAbility, [s], cell: to, ability: ability);
        w.Run(2);
        var d = w.Units.Single(u => u.Source == s.Id && u.Def.Archetype.Flight is not null);
        w.Run(600);
        return (s, d);
    }

    // ---- Rule 1: home on its own only to refuel ----

    [Fact]
    public void AnEmptyAircraftNeverGoesHomeOnItsOwn()
    {
        // At an airfield and with its ground control station both standing, an empty, damaged
        // Watcher orbits on: neither runs it home (only bingo fuel would).
        var w = Arena.NewSentinel(N);
        Field(w);
        var (station, d) = Launched(w, "watcher_station", "watcher_launch", new Cell(150, 170), new Cell(150, 120));
        d.MutableCharges[0] = 0;
        d.HP = 100;
        var center = w.OrbitCentreOf(d); // E3: (150, 120) kept inside the Watcher's edge margin and orbit radius
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            Assert.False(d.Homing, $"tick {t}: homing");
            Assert.Null(d.AirOps);
        }
        Assert.True(d.Alive);
        Assert.InRange(Cells((d.Pos - center).Len().Raw), 15, 30); // still orbiting its point
        Assert.False(station.RelaunchPending);
    }

    [Fact]
    public void AtBingoItRefuelsOnlyAtTheAirfieldAndResumesItsOrbit()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w);
        var d = w.Spawn(1, "watcher_drone", 170, 160);
        w.Run(5);
        var orbit = d.OrbitAt;
        d.Lifetime = 2400; // bingo soon (the flight home, the approach and its 60 s holding reserve)
        d.MutableCharges[0] = 1;
        d.HP = 150;
        Assert.True(RunUntil(w, () => d.Homing, 1000) > 0);
        Assert.Equal(HomeReason.Refuel, d.HomeReason);
        Assert.False(d.AirOps!.Hold);
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }) > 0);
        Assert.Equal(StationKind.Normal, StationOf(af, d.AirOps!.Station).Kind); // a refuel stop leaves the shelter free
        var s = w.ServiceOf(d);
        Assert.Equal((100, 0, 0), (s.RefuelPercent, s.RearmPercent, s.RepairPercent)); // fuel only
        var parkedAt = w.Tick;
        Assert.True(RunUntil(w, () => d.AirOps is not { Phase: FlightPhase.Parked }) > 0);
        Assert.InRange((int)(w.Tick - parkedAt), 300, 420); // gone once the tank is full (20 s at 1x from what it had)
        Assert.Equal(d.Def.Lifetime, d.Lifetime);
        Assert.Equal((1, 150), (World.StrikesLeft(d), d.HP)); // no rearm, no repair (decision D11)
        Assert.True(RunUntil(w, () => d.AirOps is null, 4000) > 0);
        Assert.False(d.Homing);
        Assert.Equal(Cell.Of(orbit).Center(), d.OrbitAt); // back to its previous task
        Assert.Equal((1, 150), (World.StrikesLeft(d), d.HP));
    }

    [Fact]
    public void AtBingoItGoesToItsGroundControlStationWhichRelaunchesItWithWhatItHad()
    {
        var w = Arena.NewSentinel(N);
        var (station, d) = Launched(w, "watcher_station", "watcher_launch", new Cell(30, 100), new Cell(150, 100));
        w.Run(600); // out over its point, 120 cells from home
        d.MutableCharges[0] = 1;
        d.HP = 150;
        d.Lifetime = 1200; // bingo soon
        Assert.True(RunUntil(w, () => d.Homing, 800) > 0);
        Assert.Equal(HomeReason.Refuel, d.HomeReason);
        Assert.True(RunUntil(w, () => d.Recovered || !d.Alive, 3000) > 0);
        Assert.True(d.Recovered, "back at its station, not lost");
        Assert.True(station.RelaunchPending);
        w.Run(station.Cooldown + 3);
        var again = w.All("watcher_drone").Single(u => u.Source == station.Id);
        Assert.NotEqual(d.Id, again.Id);
        Assert.Equal((1, 150), (World.StrikesLeft(again), again.HP)); // refuelled only: its missile and its damage kept
        Assert.InRange(again.Lifetime, again.Def.Lifetime - 10, again.Def.Lifetime); // a full tank (a few ticks flown)
        Assert.Equal(new Cell(150, 100), Cell.Of(again.OrbitAt)); // back to its previous task
        Assert.Null(station.KeptStocks);
    }

    [Theory]
    [InlineData("watcher_station", "watcher_launch")]
    [InlineData("overseer_station", "overseer_launch")]
    public void ReturnToBaseAtTheGroundControlStationKeepsWhatItHadForTheRelaunch(string kind, string ability)
    {
        var w = Arena.NewSentinel(N);
        var (station, d) = Launched(w, kind, ability, new Cell(100, 160), new Cell(100, 130));
        var stocks = d.Charges.Select(c => c > 0 ? c - 1 : 0).ToArray();
        for (var i = 0; i < stocks.Length; i++)
        {
            d.MutableCharges[i] = stocks[i];
        }
        d.HP = d.Def.MaxHp / 2;
        w.Order(1, CommandKind.ReturnToBase, [d]);
        Assert.True(RunUntil(w, () => d.Recovered, 4000) > 0);
        w.Run(station.Cooldown + 3);
        var again = w.Units.Single(u => u.Source == station.Id && u.Def.Archetype.Flight is not null);
        Assert.Equal(stocks, again.Charges);
        Assert.Equal(d.Def.MaxHp / 2, again.HP);
    }

    // ---- Rule 2: rearm only on command ----

    [Fact]
    public void ARearmOrderRearmsAndRepairsAtTheAirfieldThenItResumesItsOrbit()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w);
        var d = w.Spawn(1, "watcher_drone", 170, 160);
        w.Run(5);
        var orbit = d.OrbitAt;
        d.MutableCharges[0] = 0;
        d.HP = 120;
        w.Order(1, CommandKind.Rearm, [d]);
        w.Step();
        Assert.Equal((FlightPhase.Holding, af.Id, false), (d.AirOps!.Phase, d.AirOps.Airfield, d.AirOps.Hold));
        Assert.Equal(HomeReason.Rearm, d.HomeReason);
        Assert.Equal(StationKind.Loading, StationOf(af, d.AirOps.Station).Kind); // it needs rearming: the shelter
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }) > 0);
        var s = w.ServiceOf(d);
        Assert.Equal((100, 200, 200), (s.RefuelPercent, s.RearmPercent, s.RepairPercent));
        Assert.True(RunUntil(w, () => d.AirOps is null, 6000) > 0);
        Assert.Equal((2, d.Def.MaxHp), (World.StrikesLeft(d), d.HP)); // fully serviced
        Assert.True(d.Lifetime > d.Def.Lifetime - 200, "it left with a full tank");
        Assert.False(d.Homing);
        Assert.Equal(Cell.Of(orbit).Center(), d.OrbitAt); // resumes its previous task (decision D10)
    }

    [Fact]
    public void ARearmOrderSendsItToItsGroundControlStationWhichRelaunchesItRearmed()
    {
        var w = Arena.NewSentinel(N);
        var (station, d) = Launched(w, "watcher_station", "watcher_launch", new Cell(100, 160), new Cell(100, 120));
        d.MutableCharges[0] = 0;
        d.HP = 90;
        w.Order(1, CommandKind.Rearm, [d]);
        w.Step();
        Assert.Equal((true, HomeReason.Rearm), (d.Homing, d.HomeReason));
        Assert.True(RunUntil(w, () => d.Recovered, 3000) > 0);
        Assert.Null(station.KeptStocks);
        w.Run(station.Cooldown + 3);
        var again = w.All("watcher_drone").Single(u => u.Source == station.Id);
        Assert.Equal((2, again.Def.MaxHp), (World.StrikesLeft(again), again.HP)); // rearmed and repaired
        Assert.Equal(new Cell(100, 120), Cell.Of(again.OrbitAt));
    }

    [Fact]
    public void ReturnToBaseRefuelsOnlyAndHoldsUntilARearmOrderThenItRearmsAndLeaves()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w);
        var d = w.Spawn(1, "watcher_drone", 170, 160);
        w.Run(5);
        var orbit = d.OrbitAt;
        d.MutableCharges[0] = 0;
        d.HP = 150;
        d.Lifetime = d.Def.Lifetime / 2;
        w.Order(1, CommandKind.ReturnToBase, [d]);
        w.Step();
        Assert.Equal((HomeReason.Recall, true), (d.HomeReason, d.AirOps!.Hold));
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.Parked }) > 0);
        Assert.Equal(StationKind.Normal, StationOf(af, d.AirOps!.Station).Kind); // a recall needs no shelter
        w.Run(1500);
        Assert.Equal(FlightPhase.Parked, World.FlightPhaseOf(d)); // held
        Assert.Equal((0, 150, d.Def.Lifetime), (World.StrikesLeft(d), d.HP, d.Lifetime)); // refuelled only (rule 2)
        Assert.True(w.ServiceOf(d).Serviced);

        w.Order(1, CommandKind.Rearm, [d]); // parked: the full service where it is, then it resumes
        w.Step();
        Assert.Equal((HomeReason.Rearm, false), (d.HomeReason, d.AirOps!.Hold));
        Assert.False(w.ServiceOf(d).Serviced);
        Assert.True(RunUntil(w, () => d.AirOps is null, 6000) > 0);
        Assert.Equal((2, d.Def.MaxHp), (World.StrikesLeft(d), d.HP));
        Assert.Equal(Cell.Of(orbit).Center(), d.OrbitAt);
    }

    [Fact]
    public void ARearmOrderWhileItLeavesBringsItBackOnceItFliesFree()
    {
        var w = Arena.NewSentinel(N);
        var af = Field(w);
        var d = w.Spawn(1, "watcher_drone", 170, 160);
        w.Run(5);
        d.Lifetime = 2400; // a refuel stop
        d.MutableCharges[0] = 0;
        Assert.True(RunUntil(w, () => d.AirOps is { Phase: FlightPhase.TaxiOut }) > 0);
        Assert.Equal(0, World.StrikesLeft(d)); // refuelled only
        w.Order(1, CommandKind.Rearm, [d]);
        w.Step();
        Assert.Equal((true, HomeReason.Rearm), (d.AirOps!.Recall, d.AirOps.RecallReason));
        Assert.True(RunUntil(w, () => d.AirOps is not { Phase: >= FlightPhase.PushBack }, 6000) > 0);
        Assert.NotNull(d.AirOps);
        Assert.Equal((af.Id, HomeReason.Rearm, false), (d.AirOps!.Airfield, d.HomeReason, d.AirOps.Hold));
        Assert.True(RunUntil(w, () => d.AirOps is null, 12000) > 0);
        Assert.Equal(2, World.StrikesLeft(d));
    }

    [Fact]
    public void RearmIgnoresGroundUnitsAndOtherPlayersAircraft()
    {
        var w = Arena.NewSentinel(N);
        Field(w);
        var d = w.Spawn(1, "watcher_drone", 170, 160);
        var rifleman = w.Spawn(1, "Rifleman", 50, 50);
        w.Run(5);
        d.MutableCharges[0] = 0;
        w.Order(2, CommandKind.Rearm, [d, rifleman]); // not player 2's
        w.Order(1, CommandKind.Rearm, [rifleman]);
        w.Run(3);
        Assert.False(d.Homing);
        Assert.Null(d.AirOps);
        Assert.True(rifleman.Alive);
    }

    // ---- Rule 3: no target picking of its own ----

    [Fact]
    public void ItNeverStrikesOnItsOwnEvenUnderFire()
    {
        var w = Arena.NewSentinel(N, 3);
        var d = w.Spawn(1, "watcher_drone", 100, 100);
        w.Run(5);
        w.SpawnCrewed(2, "technical", 104, 100);
        w.Spawn(2, "Rifleman", 96, 102);
        var aa = w.Spawn(2, "manpads", 102, 104); // shoots at it (10% at best a missile)
        var shotAt = false;
        for (var t = 0; t < 2000 && d.Alive; t++)
        {
            w.Step();
            shotAt |= w.AirBursts.Any(b => b.Target == d.Id);
            Assert.DoesNotContain(w.StrikeReports, r => r.Shooter == d.Id);
            Assert.Equal(OrderKind.Idle, d.Order.Kind);
        }
        Assert.True(shotAt || !d.Alive, "the AA soldier engaged it");
        if (d.Alive)
        {
            Assert.Equal(2, World.StrikesLeft(d));
        }
        Assert.True(aa.Alive);
    }

    // ---- Determinism ----

    [Fact]
    public void TheNewStateIsHashed()
    {
        var w = Arena.NewSentinel(N);
        var (station, d) = Launched(w, "watcher_station", "watcher_launch", new Cell(100, 160), new Cell(100, 120));
        d.Homing = true;
        var h = w.Hash();
        d.HomeReason = HomeReason.Rearm;
        Assert.NotEqual(h, w.Hash());
        d.HomeReason = HomeReason.Refuel;
        Assert.Equal(h, w.Hash());
        station.KeptStocks = [1];
        Assert.NotEqual(h, w.Hash());
        station.KeptStocks = null;
        Assert.Equal(h, w.Hash());

        static ulong Run()
        {
            var w = Arena.NewSentinel(N, 5);
            AirfieldFlightTests.Field(w, [(0, 0)]);
            var a = w.Spawn(1, "watcher_drone", 170, 160);
            var b = w.Spawn(1, "watcher_drone", 60, 160);
            w.Run(5);
            a.Lifetime = 2400;
            b.MutableCharges[0] = 0;
            w.Order(1, CommandKind.Rearm, [b]);
            w.Run(3000);
            return w.Hash();
        }
        Assert.Equal(Run(), Run());
    }
}
