using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Cargo trucks load at garages (AIR-LOG-1, the owner 2026-11-22: "it should also be able to get supplies from the garage, for rsstnc
// and for the sntl"): an empty cargo truck's Load cargo order on its side's vehicle garage or the Sentinel's military garage drives
// it to the garage's door, where it loads a full load in its cargo's unload time, as Keep stocked's garage stop does: free without
// the economy, haul.loadGold (200 a full load of 2,000) with it, a part load its share; short of gold it waits at the door. The
// military garage's Service refill is bought the same way in an economy game.
public class GarageCargoTests
{
    private const int Full = 2000; // the cargo truck's load (decision 1099)

    private static World Lobby(bool sentinel, bool economy, int gold) =>
        sentinel ? Arena.NewSentinel(160, 1, s => s with { Economy = economy, StartGold = economy ? gold : 0 })
            : Arena.New(160, 1, s => s with { Economy = economy, StartGold = economy ? gold : 0 });

    private static void Order(World w, CommandKind kind, Unit u, uint target) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = kind, Units = [u.Id], Target = target });

    // Setup: a lobby with garage (player 1's, finished) and an empty truck of player 1 some 50 cells from it.
    private static (World W, Unit Garage, Unit Truck) Setup(bool sentinel, string garage, string truck, bool economy, int gold)
    {
        var w = Lobby(sentinel, economy, gold);
        var g = w.Spawn(1, garage, 20, 20);
        w.Step(); // its doors
        var t = w.SpawnCrewed(1, truck, 90, 80).Vehicle;
        t.Cargo = 0;
        return (w, g, t);
    }

    private static int Run(World w, Func<bool> done, int max, List<EconomyEvent>? econ = null, List<LogisticsEvent>? log = null)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            econ?.AddRange(w.EconomyEvents);
            log?.AddRange(w.LogisticsEvents);
            if (done())
            {
                return t;
            }
        }
        return -1;
    }

    [Theory]
    [InlineData(false, "vehicle_garage", "cargo_truck", false)]
    [InlineData(false, "vehicle_garage", "cargo_truck", true)]
    [InlineData(true, "military_garage", "sentinel_cargo_truck", false)]
    [InlineData(true, "military_garage", "sentinel_cargo_truck", true)]
    [InlineData(true, "vehicle_garage", "sentinel_cargo_truck", true)]
    public void AnEmptyTruckLoadsAFullLoadAtItsGaragesDoorPaidOnlyInAnEconomyGame(bool sentinel, string garage, string truck, bool economy)
    {
        var (w, g, t) = Setup(sentinel, garage, truck, economy, 1000);
        Assert.True(World.CanLoadCargoAt(g, t)); // the client's query
        Assert.Equal(economy ? 200 : 0, w.CargoLoadPrice(g, t));
        Order(w, CommandKind.LoadCargo, t, g.Id);
        w.Step();
        Assert.Equal(OrderKind.LoadCargo, t.Order.Kind);
        var econ = new List<EconomyEvent>();
        var log = new List<LogisticsEvent>();
        var started = Run(w, () => t.LoadLeft > 0, 3000);
        Assert.True(started > 0, "it reaches the door and starts loading");
        Assert.Equal(g.Id, t.LoadFrom);
        Assert.InRange((t.Pos - World.ExitPoint(g, 0)).Len().Raw, 0, 2 * Fixed.OneRaw); // at the door, as Keep stocked's stop
        Assert.Equal(0, t.Cargo);
        var took = Run(w, () => t.Cargo > 0, 200, econ, log);
        Assert.InRange(took, 58, 60); // its cargo's unload time, 3 s (as at a depot)
        Assert.Equal(Full, t.Cargo);
        Assert.Equal(economy ? 800 : 0, w.Player(1)!.Credits); // 200 a full load (decision 1102), free without the economy
        Assert.Single(econ, e => e.Kind == EconomyEventKind.SupplyBought && e.Unit == t.Id && e.Other == g.Id && e.Amount == Full);
        Assert.Single(log, e => e.Kind == LogisticsEventKind.TruckLoaded && e.Unit == t.Id && e.Amount == Full);
        Assert.NotEqual(OrderKind.LoadCargo, t.Order.Kind); // loaded, the order ends
        Assert.Equal((0, 0u), (t.LoadLeft, t.LoadFrom));
    }

    [Fact]
    public void ShortOfGoldItWaitsAtTheDoorAndAPartLoadCostsItsShare()
    {
        var (w, g, t) = Setup(false, "vehicle_garage", "cargo_truck", true, 150);
        Order(w, CommandKind.LoadCargo, t, g.Id);
        var econ = new List<EconomyEvent>();
        Assert.True(Run(w, () => t.LoadFrom == g.Id, 3000, econ) > 0);
        w.Run(600);
        Assert.Equal((0, 0, g.Id), (t.Cargo, t.LoadLeft, t.LoadFrom)); // waits at the door
        Assert.Equal(OrderKind.LoadCargo, t.Order.Kind);
        Assert.Single(econ, e => e.Kind == EconomyEventKind.ShortOfGold && e.Other == g.Id && e.Amount == 50); // once a wait
        Assert.Equal(150, w.Player(1)!.Credits);
        w.Player(1)!.Credits = 1000;
        Assert.True(Run(w, () => t.Cargo == Full, 200) > 0);
        Assert.Equal(800, w.Player(1)!.Credits);

        t.Cargo = 1500; // what a gun left it: the 500 it lacks cost their share, 50
        Order(w, CommandKind.LoadCargo, t, g.Id);
        Assert.True(Run(w, () => t.Cargo == Full, 200) > 0);
        Assert.Equal(750, w.Player(1)!.Credits);
    }

    [Fact]
    public void OnlyAnEmptyTruckAtItsOwnSidesGarageThatBuildsIt()
    {
        var w = Lobby(false, false, 0);
        var mine = w.Spawn(1, "vehicle_garage", 20, 20);
        var theirs = w.Spawn(2, "vehicle_garage", 100, 20);
        var barracks = w.Spawn(1, "resistance_barracks", 20, 100);
        w.Step();
        var t = w.SpawnCrewed(1, "cargo_truck", 90, 80).Vehicle;
        t.Cargo = 0;
        Assert.False(World.CanLoadCargoAt(theirs, t) || World.CanLoadCargoAt(barracks, t));
        Assert.True(World.CanLoadCargoAt(mine, t));
        Order(w, CommandKind.LoadCargo, t, theirs.Id);
        w.Step();
        Assert.NotEqual(OrderKind.LoadCargo, t.Order.Kind); // not its side's
        Order(w, CommandKind.LoadCargo, t, barracks.Id);
        w.Step();
        Assert.NotEqual(OrderKind.LoadCargo, t.Order.Kind); // builds no vehicle
        t.Cargo = Full;
        Order(w, CommandKind.LoadCargo, t, mine.Id);
        w.Step();
        Assert.NotEqual(OrderKind.LoadCargo, t.Order.Kind); // full
        var tech = w.SpawnCrewed(1, "technical", 60, 80).Vehicle;
        Order(w, CommandKind.LoadCargo, tech, mine.Id);
        w.Step();
        Assert.NotEqual(OrderKind.LoadCargo, tech.Order.Kind); // no cargo
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(100, false)]
    public void TheMilitaryGaragesServiceRefillIsBoughtInAnEconomyGame(int gold, bool refilled)
    {
        var (w, g, t) = Setup(true, "military_garage", "sentinel_cargo_truck", true, gold);
        Order(w, CommandKind.Service, t, g.Id);
        var econ = new List<EconomyEvent>();
        Assert.True(Run(w, () => t.InGarage, 3000, econ) > 0);
        Assert.Equal(refilled ? gold - 200 : gold, w.Player(1)!.Credits); // paid as it starts
        Assert.True(Run(w, () => !t.InGarage, 400, econ) > 0);
        Assert.Equal(refilled ? Full : 0, t.Cargo);
        Assert.Equal(refilled ? 1 : 0, econ.Count(e => e.Kind == EconomyEventKind.SupplyBought && e.Unit == t.Id && e.Amount == Full));
        Assert.Equal(refilled ? 0 : 1, econ.Count(e => e.Kind == EconomyEventKind.ShortOfGold && e.Unit == t.Id && e.Amount == 100));
    }
}
