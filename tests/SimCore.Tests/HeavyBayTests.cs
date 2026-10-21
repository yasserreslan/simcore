using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The heavy-air fixes (2026-10-21 night): a heavy aircraft leaves a heavy bay of the main air base quickly (order to
// airborne within 45 s: pushback 3 cells/s, taxi 5.5, turns 3.5; it was 89 to 99 s).
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
}
