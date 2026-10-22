using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The heavy-air fixes (2026-10-21 night): a gunship that went home out of rounds is rearmed in its heavy bay, and a
// heavy aircraft leaves a heavy bay of the main air base quickly (order to airborne within 45 s: pushback 3 cells/s, taxi
// 5.5, turns 3.5; it was 89 to 99 s); landing at the main air base is quicker too (decision 144).
public class HeavyBayTests(ITestOutputHelper output)
{
    private static World Sentinel(int n, ulong seed = 1) => Arena.NewSentinel(n, seed);

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, uint target = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Target = target });

    // The base of player 1 and one aircraft of type id built by its hangar, parked in a heavy bay.
    private static (World W, Airfield Af, Unit U) Built(string id)
    {
        var w = Sentinel(400);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var hangar = w.Unit(af.Hangar)!;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [hangar.Id], Unit = id });
        Unit? u = null;
        for (var t = 0; t < 4000 && u is null; t++)
        {
            w.Step();
            u = w.Units.FirstOrDefault(x => x.Def.Archetype.Id == id);
        }
        Assert.NotNull(u);
        Assert.Equal(FlightPhase.Parked, u!.AirOps!.Phase);
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == u.AirOps.Station).Kind);
        return (w, af, u);
    }

    // Steps w until u flies (past its takeoff roll: climbing out or free), at most max ticks; the ticks taken, each phase's start.
    private int ToAirborne(World w, Unit u, int max, List<string> phases)
    {
        var from = w.Tick;
        FlightPhase? last = null;
        for (var t = 0; t < max; t++)
        {
            var p = u.AirOps?.Phase;
            if (p != last)
            {
                phases.Add($"{p?.ToString() ?? "free"}@{(w.Tick - from) / (double)World.TicksPerSecond:F1}s");
                last = p;
            }
            if (p is null or FlightPhase.Climb or FlightPhase.Airborne)
            {
                return (int)(w.Tick - from);
            }
            w.Step();
        }
        return -1;
    }

    [Theory]
    [InlineData("attack_jet")]
    [InlineData("gunship")]
    [InlineData("strategic_bomber")]
    [InlineData("supersonic_bomber")]
    [InlineData("stealth_bomber")]
    public void AHeavyIsAirborneWithin45SecondsOfAnOrderFromAHeavyBay(string id)
    {
        var (w, _, u) = Built(id);
        w.Run(40);
        Issue(w, CommandKind.AttackGround, u, new Cell(300, 300));
        var phases = new List<string>();
        var ticks = ToAirborne(w, u, 120 * World.TicksPerSecond, phases);
        output.WriteLine($"{id}: airborne after {ticks / (double)World.TicksPerSecond:F1} s: {string.Join(", ", phases)}");
        Assert.InRange(ticks, 1, 45 * World.TicksPerSecond);
    }

    [Fact]
    public void AGunshipHomeOutOfRoundsIsRearmedInItsBayAndOrbitsAgain()
    {
        var (w, af, g) = Built("gunship");
        var full = g.Charges.ToList();
        Assert.Equal([100, 20, 1], full);
        w.Spawn(2, "technical", 296, 300);
        w.Spawn(2, "Rifleman", 300, 306);
        w.Spawn(2, "supply_depot", 302, 290);
        Issue(w, CommandKind.AttackGround, g, new Cell(300, 300));
        // Out to the area, every round fired, then home on its own to rearm.
        for (var t = 0; t < 12000 && !(g.Homing && g.Charges.All(n => n == 0)); t++)
        {
            w.Step();
        }
        Assert.All(g.Charges, n => Assert.Equal(0, n));
        Assert.True(g.Homing, "home on its own when it is out of rounds");
        Assert.Equal(HomeReason.Rearm, g.HomeReason);
        Assert.Equal(OrderKind.Idle, g.Order.Kind);
        var parked = false;
        for (var t = 0; t < 8000 && !parked; t++)
        {
            w.Step();
            parked = g.AirOps is { Phase: FlightPhase.Parked };
        }
        Assert.True(parked, "it lands and parks");
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == g.AirOps!.Station).Kind);
        // Rearmed in its heavy bay (2x: a round every 0.25 s, 121 rounds in about 30 s; the tank's 45 s at 1x), then held.
        var at = w.Tick;
        var ac = g.Def.Archetype.Aircraft!;
        for (var t = 0; t < 60 * World.TicksPerSecond && g.Charges.Zip(full).Any(x => x.First < x.Second); t++)
        {
            w.Step();
        }
        var took = (int)(w.Tick - at);
        output.WriteLine($"rearmed {string.Join("/", g.Charges)} in {took / (double)World.TicksPerSecond:F1} s");
        Assert.Equal(full, g.Charges);
        Assert.InRange(took, 1, 121 * ac.RearmTicksPerRound / 2 + World.TicksPerSecond);
        w.Run(60 * World.TicksPerSecond);
        Assert.Equal(FlightPhase.Parked, g.AirOps?.Phase); // held in its bay until its next order (never out with nothing)
        Assert.True(w.ServiceOf(g).Serviced);
        // Ordered out again: airborne within 45 s, back on its circle and firing.
        Issue(w, CommandKind.AttackGround, g, new Cell(300, 300));
        var phases = new List<string>();
        var ticks = ToAirborne(w, g, 120 * World.TicksPerSecond, phases);
        output.WriteLine($"out again: airborne after {ticks / (double)World.TicksPerSecond:F1} s: {string.Join(", ", phases)}");
        Assert.InRange(ticks, 1, 45 * World.TicksPerSecond);
        var fired = 0;
        for (var t = 0; t < 6000 && fired == 0; t++)
        {
            w.Step();
            fired += w.SideFires.Count(s => s.Aircraft == g.Id);
        }
        Assert.True(fired > 0, "it orbits and fires again");
        Assert.NotNull(g.Gunship);
    }
    // Lands the aircraft u (airborne) at its base on a Rearm order; each phase's start (s from the order), then the
    // final (Final), rollout (touchdown to the exit) and taxi-and-park (exit to parked) times in ticks.
    private (int Final, int Rollout, int Taxi) Landing(World w, Airfield af, Unit u, List<string> phases)
    {
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.Rearm, Units = [u.Id], Target = af.Facility });
        var from = w.Tick;
        FlightPhase? last = null;
        var lastLeg = -1;
        var at = new Dictionary<FlightPhase, long>();
        for (var t = 0; t < 400 * World.TicksPerSecond; t++)
        {
            var p = u.AirOps?.Phase;
            var leg = u.AirOps?.Leg ?? -1;
            if (p != last || leg != lastLeg)
            {
                phases.Add($"{p?.ToString() ?? "free"}/{leg}@{(w.Tick - from) / (double)World.TicksPerSecond:F1}s v{(u.AirOps?.Speed ?? 0) * 10 / Fixed.OneRaw / World.TicksPerSecond}");
                lastLeg = leg;
                if (p is { } q && !at.ContainsKey(q))
                {
                    at[q] = w.Tick;
                }
                last = p;
            }
            if (p == FlightPhase.Parked && at.ContainsKey(FlightPhase.Final))
            {
                break;
            }
            w.Step();
        }
        Assert.True(at.ContainsKey(FlightPhase.Parked) && at.ContainsKey(FlightPhase.Rollout), string.Join(", ", phases));
        var taxi = at.TryGetValue(FlightPhase.TaxiIn, out var ti) ? ti : at[FlightPhase.Parking];
        return ((int)(at[FlightPhase.Rollout] - at[FlightPhase.Final]), (int)(taxi - at[FlightPhase.Rollout]), (int)(at[FlightPhase.Parked] - taxi));
    }

    private static Unit BuiltAny(World w, Airfield af, string id)
    {
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [af.Hangar], Unit = id == "multirole_jet" ? "Multirole 500 lb" : id });
        Unit? u = null;
        for (var t = 0; t < 4000 && u is null; t++)
        {
            w.Step();
            u = w.Units.FirstOrDefault(x => x.Def.Archetype.Id == id && x.AirOps is { Phase: FlightPhase.Parked });
        }
        Assert.NotNull(u);
        return u!;
    }

    // The landing fix (2026-10-21, decision 144): at the main air base every aircraft taxis at least 5.5 cells/s (3.5
    // in turns) and the overhead break comes 30 cells in from the threshold (was midfield, 90 cells in). Measured from a
    // Rearm order 20 s out (final: the join to touchdown; touchdown-to-parked: the rollout, the taxi and the park-in):
    //   aircraft (station)         final before -> after   rollout   taxi+park   touchdown-to-parked
    //   attack_jet (heavy bay)     38.9 -> 25.4 s          14.2      12.6        26.8 -> 26.8 s
    //   multirole_jet (row stand)  36.9 -> 25.6 s          20.3 -> 14.0   45.5 -> 26.5   65.8 -> 40.5 s
    //   gunship (heavy bay)        55.4 -> 39.4 s          14.4      13.3        27.8 -> 27.8 s
    //   strategic_bomber (bay)     54.1 -> 39.5 s          14.3      13.3        27.7 -> 27.7 s
    // The multirole stays over 30 s on the ground: the nearest exit past its touchdown is the connector 80 cells down
    // the runway (x 90; the connectors at x 10, 90, 170 are in the base's art) and its row is 58 cells back east.
    [Theory]
    [InlineData("attack_jet", 27, 30)]
    [InlineData("multirole_jet", 27, 43)]
    [InlineData("gunship", 42, 30)]
    [InlineData("strategic_bomber", 42, 30)]
    [InlineData("supersonic_bomber", 42, 30)]
    [InlineData("stealth_bomber", 42, 30)]
    [InlineData("airlifter", 42, 30)]
    public void LandingAtTheMainBaseIsQuick(string id, int maxFinal, int maxGround)
    {
        var w = Sentinel(400);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var u = BuiltAny(w, af, id);
        w.Run(40);
        Issue(w, CommandKind.Move, u, new Cell(300, 300));
        Assert.True(ToAirborne(w, u, 120 * World.TicksPerSecond, []) > 0);
        w.Run(20 * World.TicksPerSecond);
        var phases = new List<string>();
        var (fin, roll, taxi) = Landing(w, af, u, phases);
        var s = (double)World.TicksPerSecond;
        output.WriteLine($"{id}: final {fin / s:F1} s, rollout {roll / s:F1} s, taxi+park {taxi / s:F1} s, touchdown-to-parked {(roll + taxi) / s:F1} s: {string.Join(", ", phases)}");
        Assert.InRange(fin, 1, maxFinal * World.TicksPerSecond);
        Assert.InRange(roll + taxi, 1, maxGround * World.TicksPerSecond);
        var kind = af.Rows.SelectMany(r => r.Stations).Single(st => st.Unit == u.AirOps!.Station).Kind;
        Assert.Equal(u.Def.Archetype.Aircraft!.SizeClass >= AircraftClass.L, kind == StationKind.Heavy); // a bay or a row's stand
    }

    // A hangar-built heavy is recorded as rolled out into its bay at the tick it appears there (AirOps.Since), so a
    // renderer can open the bay's door for it from the sim's state.
    [Fact]
    public void AHangarBuiltHeavyIsRecordedArrivingInItsBay()
    {
        var w = Sentinel(400);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [af.Hangar], Unit = "attack_jet" });
        Unit? u = null;
        for (var t = 0; t < 4000 && u is null; t++)
        {
            w.Step();
            u = w.Units.FirstOrDefault(x => x.Def.Archetype.Id == "attack_jet");
        }
        Assert.NotNull(u);
        Assert.Equal(FlightPhase.Parked, u!.AirOps!.Phase);
        Assert.Equal(w.Tick - 1, u.AirOps.Since); // the tick it appeared (stepped past)
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == u.AirOps.Station).Kind);
    }

    [Fact]
    public void TheLandingFixIsTheMainBasesOnly()
    {
        var c = TestContent.Mvp;
        var fwd = c.AirfieldTypes.Single(t => t.Id == "forward_airfield");
        var main = c.AirfieldTypes.Single(t => t.Id == "main_air_base");
        Assert.Equal(0, fwd.MinTaxi.Raw); // a forward airfield keeps every aircraft's own taxi speeds and the midfield break
        Assert.Equal(0, fwd.MinTurn.Raw);
        Assert.Equal(0, fwd.BreakDistance.Raw);
        Assert.Equal(c.Archetype("attack_jet")!.Aircraft!.TaxiSpeed, main.MinTaxi); // the heavies' speeds (decision 142)
        Assert.Equal(c.Archetype("attack_jet")!.Aircraft!.TurnSpeed, main.MinTurn);
        Assert.Equal(30 * Fixed.OneRaw, main.BreakDistance.Raw);
    }
}
