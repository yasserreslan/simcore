using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The faster airfield (2026-10-24, the owner: "make all aircraft get in and get out 30% faster ... just make the planes
// return faster to the airbase", "returning is faster than leaving"): every airfield type's tempo (content: leave 130,
// return 150) flies leaving (the pushback to the takeoff roll) 30% and returning (the flight home to the park-in) 50% faster
// than the aircraft's own numbers, the same paths and pattern sooner; the approach sequence clears the next aircraft from the
// hold as soon as the one ahead has left its join and lets it on to the runway once the one ahead is down (it lands once that
// one has turned off); the holds are tighter and closer. Each case is measured against the same world at the old pace (the
// tempo at 100 and the old holds, content edited in the test).
public class AirfieldTempoTests(ITestOutputHelper output)
{
    private const int N = 400;
    private static readonly double S = World.TicksPerSecond;

    // The MVP content at the old pace: the tempo at 100 (the aircraft's own numbers) and the holds as they were.
    private static readonly Lazy<ContentSet> OldPace = new(() =>
    {
        var sources = TestContent.MvpSources().Select(s => s.Name is "airfields.json" or "archetypes/sentinel.json" ? s with
        {
            Json = s.Json
                .Replace("\"tempo\": { \"leave\": 130, \"return\": 150 }", "\"tempo\": { \"leave\": 100, \"return\": 100 }", StringComparison.Ordinal)
                .Replace("\"distance\": 200, \"radius\": 160,", "\"distance\": 250, \"radius\": 250,", StringComparison.Ordinal)
                .Replace("\"distance\": 300, \"radius\": 240,", "\"distance\": 400, \"radius\": 300,", StringComparison.Ordinal),
        } : s).ToList();
        return ContentLoader.Load(sources);
    });

    private static World NewWorld(bool old)
    {
        if (!old)
        {
            return Arena.NewSentinel(N);
        }
        var content = OldPace.Value;
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(content, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(N, N), MatchCompiler.Compile(content, setup));
    }

    private static Airfield Base(World w)
    {
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        return w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
    }

    private static Airfield Forward(World w) =>
        AirfieldFlightTests.Field(w, [(0, 0), (0, 1)], at: new Cell(60, 200), east: 1); // 4 segments, 2 rows

    private static Unit Air(World w, string id, int x, int y) =>
        w.Spawn(1, id == "multirole_jet" ? "Multirole 500 lb" : id, x, y, Facing.West);

    private static void Order(World w, CommandKind kind, IEnumerable<Unit> us, Cell cell = default, uint target = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = kind, Units = [.. us.Select(u => u.Id)], Cell = cell, Target = target });

    // Runs w until done (checked before each step), at most max ticks; the ticks taken, or -1.
    private static int Until(World w, Func<bool> done, int max)
    {
        for (var t = 0; t <= max; t++)
        {
            if (done())
            {
                return t;
            }
            w.Step();
        }
        return -1;
    }

    // Runs until done, logging each phase and leg change (seconds from the start); the ticks taken.
    private static int Logged(World w, Unit u, Func<bool> done, int max, List<string> log)
    {
        var from = w.Tick;
        (FlightPhase?, int) last = (null, -9);
        return Until(w, () =>
        {
            var k = (u.AirOps?.Phase, u.AirOps?.Leg ?? -1);
            if (k != last)
            {
                log.Add($"{k.Item1?.ToString() ?? "free"}/{k.Item2}@{(w.Tick - from) / S:F1}");
                last = k;
            }
            return done();
        }, max);
    }

    // One sortie of aircraft id at airfield type `field` (the main air base, else a forward airfield): put down in flight 130 to
    // 200 cells out, recalled home (ReturnToBase) and timed to parked; refuelled and held there, ordered out and timed to wheels-up.
    private (int Return, int Leave) Sortie(string id, bool old, bool forward = false)
    {
        var w = NewWorld(old);
        var af = forward ? Forward(w) : Base(w);
        var u = Air(w, id, 300, 300);
        w.Run(5 * World.TicksPerSecond);
        Order(w, CommandKind.ReturnToBase, [u], target: af.Facility);
        var log = new List<string>();
        var back = Logged(w, u, () => u.AirOps is { Phase: FlightPhase.Parked }, 300 * World.TicksPerSecond, log);
        output.WriteLine($"{id}{(forward ? " (forward)" : "")}{(old ? " old pace" : "")}: return to parked {back / S:F1} s: {string.Join(" ", log)}");
        Assert.True(back > 0, string.Join(" ", log));
        Assert.True(Until(w, () => w.ServiceOf(u).Serviced, 120 * World.TicksPerSecond) >= 0);
        w.Run(20);
        Assert.Equal(FlightPhase.Parked, u.AirOps?.Phase); // recalled: held once serviced
        Order(w, CommandKind.Move, [u], new Cell(300, 300));
        log.Clear();
        var leave = Logged(w, u, () => u.AirOps is null or { Phase: FlightPhase.Climb }, 300 * World.TicksPerSecond, log);
        output.WriteLine($"{id}{(forward ? " (forward)" : "")}{(old ? " old pace" : "")}: order to wheels-up {leave / S:F1} s: {string.Join(" ", log)}");
        Assert.True(leave > 0, string.Join(" ", log));
        Assert.Equal(0, w.AirEdgeClamps);
        return (back, leave);
    }

    // Leaving 30% faster, returning 50% faster (measured, the old pace in the same world): a jet at a row's stand of the main
    // air base, a heavy in a heavy bay, a jet at a forward airfield. Measured on this head (the old pace / the new; a7a2ef5 had
    // the old pace's numbers to the tick):
    //   multirole_jet (base stand):    order to wheels-up 45.0 / 34.0 s (1.32x), return to parked 73.8 / 49.1 s (1.50x)
    //   strategic_bomber (heavy bay):  order to wheels-up 42.5 / 32.1 s (1.32x), return to parked 73.9 / 49.0 s (1.51x)
    //   multirole_jet (forward field): see the test output.
    [Theory]
    [InlineData("multirole_jet", false)]
    [InlineData("strategic_bomber", false)]
    [InlineData("multirole_jet", true)]
    public void LeavingIs30AndReturning50PercentFaster(string id, bool forward)
    {
        var (oldBack, oldLeave) = Sortie(id, true, forward);
        var (back, leave) = Sortie(id, false, forward);
        var lr = oldLeave * 100 / leave;
        var rr = oldBack * 100 / back;
        output.WriteLine($"{id}: leaving {oldLeave / S:F1} -> {leave / S:F1} s ({lr / 100.0:F2}x), returning {oldBack / S:F1} -> {back / S:F1} s ({rr / 100.0:F2}x)");
        Assert.InRange(lr, 124, 136); // about 1.3x
        Assert.InRange(rr, 142, 160); // about 1.5x
        Assert.True(rr > lr, "returning is faster than leaving (the owner)");
    }

    // Four jets recalled together land one behind another: the next is cleared from the hold as soon as the one ahead has left
    // its join, leaves its own join once the one ahead is down and lands once that one has turned off (never two on the runway,
    // no go-around), and nobody waits at the hold while the runway lies idle. Measured: all four parked 200.5 s after the recall
    // on a7a2ef5 (73.8, 113.0, 158.2, 200.5: one landing every 40 to 45 s), now about 104 s (49, 68, 86, 104: one every 18 s).
    [Fact]
    public void FourJetsReturningTogetherLandOneBehindAnother()
    {
        var w = NewWorld(false);
        var af = Base(w);
        var jets = Enumerable.Range(0, 4).Select(i => Air(w, "multirole_jet", 300 + 6 * i, 300 + 6 * i)).ToList();
        w.Run(5 * World.TicksPerSecond);
        Order(w, CommandKind.ReturnToBase, jets, target: af.Facility);
        var from = w.Tick;
        var parked = new int[4];
        var onRunway = new List<uint>();
        int idle = 0, goArounds = 0, maxApproaches = 0;
        var touchdowns = new List<(uint Id, uint Tick)>();
        var vacated = new List<(uint Id, uint Tick)>();
        var all = Until(w, () =>
        {
            foreach (var e in w.AirfieldEvents)
            {
                switch (e.Kind)
                {
                    case AirfieldEventKind.GoAround:
                        goArounds++;
                        break;
                    case AirfieldEventKind.Touchdown:
                        Assert.Empty(onRunway); // the one ahead has turned off
                        onRunway.Add(e.Aircraft);
                        touchdowns.Add((e.Aircraft, w.Tick));
                        break;
                    case AirfieldEventKind.RunwayVacated:
                        onRunway.Remove(e.Aircraft);
                        vacated.Add((e.Aircraft, w.Tick));
                        break;
                }
            }
            maxApproaches = Math.Max(maxApproaches, af.Approaches.Count);
            // The runway idle (nobody on it, nobody cleared) while the head of the queue waits at the hold.
            if (af.RunwayLock == 0 && af.Approaches.Count == 0 && af.Landings.Count > 0 && w.Unit(af.Landings[0].Aircraft) is { } head &&
                (head.Pos - HoldPoint(af)).Len() <= af.Type.HoldReach)
            {
                idle++;
            }
            for (var i = 0; i < 4; i++)
            {
                if (parked[i] == 0 && jets[i].AirOps is { Phase: FlightPhase.Parked })
                {
                    parked[i] = (int)(w.Tick - from);
                }
            }
            return parked.All(p => p > 0);
        }, 400 * World.TicksPerSecond);
        output.WriteLine($"4 jets: all parked {all / S:F1} s ({string.Join(", ", parked.Select(p => (p / S).ToString("F1")))}); " +
            $"touchdowns {string.Join(", ", touchdowns.Select(t => ((t.Tick - from) / S).ToString("F1")))}; go-arounds {goArounds}; idle at the hold {idle} ticks");
        Assert.InRange(all, 1, 115 * World.TicksPerSecond); // was 200.5 s
        Assert.Equal(0, goArounds);
        Assert.Equal(0, idle);
        Assert.Equal(4, touchdowns.Count);
        Assert.InRange(maxApproaches, 2, 2); // one on its way to the runway, the next waiting on its join
        for (var i = 1; i < 4; i++)
        {
            Assert.True(touchdowns[i].Tick > vacated[i - 1].Tick, "lands only once the one ahead has turned off");
            Assert.InRange((int)(touchdowns[i].Tick - touchdowns[i - 1].Tick), 1, 25 * World.TicksPerSecond); // one every 18 s or so (was 40 to 45)
        }
        Assert.Equal(0, w.AirEdgeClamps);
    }

    // A mixed stream (two heavies and two jets recalled together to the main air base) and four drones at a forward airfield
    // (straight in) land without a go-around, one at a time on the runway.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedAndStraightInStreamsLandWithoutAGoAround(bool drones)
    {
        var w = NewWorld(false);
        var af = drones ? Forward(w) : Base(w);
        string[] ids = drones ? ["watcher_drone", "watcher_drone", "overseer_drone", "watcher_drone"] : ["strategic_bomber", "multirole_jet", "gunship", "fighter_jet"];
        var us = ids.Select((id, i) => Air(w, id, 300 + 8 * i, 300 - 8 * i)).ToList();
        w.Run(5 * World.TicksPerSecond);
        Order(w, CommandKind.ReturnToBase, us, target: af.Facility);
        int goArounds = 0, down = 0;
        var t = Until(w, () =>
        {
            foreach (var e in w.AirfieldEvents)
            {
                goArounds += e.Kind == AirfieldEventKind.GoAround ? 1 : 0;
                down += e.Kind == AirfieldEventKind.Touchdown ? 1 : 0;
                down -= e.Kind == AirfieldEventKind.RunwayVacated ? 1 : 0;
                Assert.InRange(down, 0, 1);
            }
            return us.All(u => u.AirOps is { Phase: FlightPhase.Parked });
        }, 400 * World.TicksPerSecond);
        output.WriteLine($"{string.Join(", ", ids)}: all parked {t / S:F1} s, go-arounds {goArounds}");
        Assert.True(t > 0, "all parked");
        Assert.Equal(0, goArounds);
        Assert.Equal(0, w.AirEdgeClamps);
    }

    // A helicopter at a helipad (its rotary block's tempo, 2026-10-24): put down in flight 120 cells out and recalled, home and down onto its pad
    // about 1.5x as fast (the flight there and the descent time-scaled); its rotor run down, ordered out, the spool-up and the
    // lift-off about 1.3x as fast.
    [Theory]
    [InlineData("attack_helicopter")]
    [InlineData("light_helicopter")]
    public void AHelicopterLeavesAndReturnsToItsPadFaster(string id)
    {
        (int Back, int Leave) Run(bool old)
        {
            var w = NewWorld(old);
            var pad = w.Spawn(1, "helipad", 60, 60);
            var u = w.Spawn(1, id, 180, 60);
            w.Run(20);
            Order(w, CommandKind.ReturnToBase, [u], target: pad.Id);
            var back = Until(w, () => u.Heli is { Phase: HeliPhase.Landed }, 120 * World.TicksPerSecond);
            Assert.True(Until(w, () => w.AirStatusOf(u).Activity == AirActivity.Held, 120 * World.TicksPerSecond) >= 0, $"{w.AirStatusOf(u).Activity}");
            w.Run(20 * World.TicksPerSecond); // its rotor runs down
            Assert.Equal(0, u.Heli!.Rotor);
            Order(w, CommandKind.Move, [u], new Cell(180, 160));
            var leave = Until(w, () => u.Heli is { Phase: HeliPhase.Flying }, 60 * World.TicksPerSecond);
            output.WriteLine($"{id}{(old ? " old pace" : "")}: order to flying {leave / S:F1} s, recall to down on its pad {back / S:F1} s");
            Assert.True(leave > 0 && back > 0);
            return (back, leave);
        }
        var (ob, ol) = Run(true);
        var (nb, nl) = Run(false);
        output.WriteLine($"{id}: leaving {ol / S:F1} -> {nl / S:F1} s ({ol * 100 / nl / 100.0:F2}x), returning {ob / S:F1} -> {nb / S:F1} s ({ob * 100 / nb / 100.0:F2}x)");
        Assert.InRange(ol * 100 / nl, 120, 140);
        Assert.InRange(ob * 100 / nb, 135, 160);
    }

    // The hold point of the main air base's whole runway (its centre line, the type's distance beyond the approach end).
    private static Vec HoldPoint(Airfield af) =>
        World.AirfieldLocalToMap(af, (long)af.Type.SegmentCells * (af.East + 1) * Fixed.OneRaw + af.Type.HoldDistance.Raw,
            (long)af.Type.RunwayCells * Fixed.OneRaw / 2);

    [Fact]
    public void EveryAirfieldTypeLeaves30AndReturns50PercentFasterWithTighterHolds()
    {
        foreach (var t in TestContent.Mvp.AirfieldTypes)
        {
            Assert.Equal(130, t.TempoLeave);
            Assert.Equal(150, t.TempoReturn);
        }
        var widest = TestContent.Mvp.Archetypes.Where(a => a.Aircraft is { Landing: LandingKind.Runway })
            .Max(a => a.Flight!.RadiusAt(a.Speed.Raw));
        var main = TestContent.Mvp.AirfieldTypes.Single(t => t.Id == "main_air_base");
        Assert.Equal((30, 24), ((int)(main.HoldDistance.Raw / Fixed.OneRaw), (int)(main.HoldRadius.Raw / Fixed.OneRaw)));
        Assert.True(main.HoldRadius.Raw >= widest, "the base's hold is wider than every aircraft's turn at cruise");
    }
}
