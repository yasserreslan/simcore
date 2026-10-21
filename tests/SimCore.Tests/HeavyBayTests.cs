using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The heavy-air fixes (2026-10-21 night): a gunship that went home out of rounds is rearmed in its heavy bay, and a
// heavy aircraft leaves a heavy bay of the main air base quickly (order to airborne within 45 s: pushback 3 cells/s, taxi
// 5.5, turns 3.5; it was 89 to 99 s).
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
}
