using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The airlift (the light tank night, 2026-10-24; docs/design.md, "Light tank"): the airlifter (a C-5 type) parked at the main air
// base takes 2 airliftable vehicles (the Booker light tank) up its rear ramp, 10 s each, carrying them instead of its 4 cargo
// containers (never both); on a Deliver to a forward airbase it lands and they drive off to the field garage's rally point; shot
// down with them aboard, they are lost.
public class AirliftTests(ITestOutputHelper output)
{
    private const int Tps = World.TicksPerSecond;
    private const string Booker = "light_tank";

    // The main air base at (40, 120); a forward airbase at (150, 360) on a 500-cell map (LogisticsTests' layout).
    private static readonly Cell BaseCell = new(40, 120);
    private static readonly Cell ForwardCell = new(150, 360);

    private static UnitDef Def(World w, string id) => w.Player(1)!.Setup.Find(w.Content, id)!;

    private static void Issue(World w, CommandKind kind, IEnumerable<Unit> us, uint target = 0, Cell cell = default) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = kind, Units = [.. us.Select(u => u.Id)], Target = target, Cell = cell });

    // A Sentinel world with the main air base and an airlifter its hangar built, parked (held) in a heavy bay, full of pallets.
    private static (World W, Airfield Base, Unit C5) Based(ulong seed = 1)
    {
        var w = Arena.NewSentinel(500, seed);
        var af = w.SpawnAirBase(Def(w, "airbase_core"), BaseCell, 0, []) ?? throw new InvalidOperationException("no base");
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [af.Hangar], Unit = "airlifter" });
        Unit? c5 = null;
        for (var t = 0; t < 4000 && c5 is null; t++)
        {
            w.Step();
            c5 = w.Units.FirstOrDefault(x => x.Def.Archetype.Id == "airlifter");
        }
        Assert.NotNull(c5);
        Assert.Equal(FlightPhase.Parked, c5!.AirOps!.Phase);
        Assert.Equal((4, 10000), World.CargoAboard(c5));
        return (w, af, c5);
    }

    private static Airfield Forward(World w) =>
        w.SpawnAirBase(Def(w, "forward_airbase"), ForwardCell, 0, []) ?? throw new InvalidOperationException("no forward airbase");

    // Two crewed Bookers south of the base (60 cells from the bay's door).
    private static (Unit, Unit) Bookers(World w) =>
        (w.SpawnCrewed(1, Booker, 130, 240, Facing.North).Vehicle, w.SpawnCrewed(1, Booker, 134, 240, Facing.North).Vehicle);

    // Board sends the tanks to the airlifter and runs until they are all aboard (at most max ticks); the events seen.
    private static List<(uint Tick, AirliftEvent E)> Board(World w, Unit c5, IReadOnlyList<Unit> tanks, int max = 2400)
    {
        Issue(w, CommandKind.Enter, tanks, c5.Id);
        var ev = new List<(uint, AirliftEvent)>();
        for (var t = 0; t < max && !tanks.All(x => x.Inside == c5.Id); t++)
        {
            w.Step();
            ev.AddRange(w.AirliftEvents.Select(e => (w.Tick, e)));
        }
        return ev;
    }

    [Fact]
    public void TheHoldIsData()
    {
        var c = TestContent.Mvp;
        var al = c.Archetype("airlifter")!.Airlift!;
        Assert.Equal((2, c.TagsOf("airliftable")), (al.Seats, al.Tags));
        Assert.Equal((Fixed.Frac(115, 10), Fixed.FromInt(4), 200, 100), (al.Ramp, al.BoardRange, al.BoardTicks, al.UnloadTicks));
        Assert.Equal(["light_tank"], c.Archetypes.Where(a => a.Tags.Matches(al.Tags)).Select(a => a.Id)); // only the Booker
        Assert.Null(c.Archetype("main_battle_tank")!.Airlift);
    }

    // Two Bookers drive to the ramp behind its tail and go up one at a time, 10 s each; the first takes the pallets off.
    [Fact]
    public void TwoBookersBoardAtTheMainBaseTenSecondsEach()
    {
        var (w, _, c5) = Based();
        var (a, b) = Bookers(w);
        var ev = Board(w, c5, [a, b]);
        foreach (var (t, e) in ev)
        {
            output.WriteLine($"t{t} {e.Kind} {e.Vehicle}");
        }
        Assert.Equal(c5.Id, a.Inside);
        Assert.Equal(c5.Id, b.Inside);
        Assert.Equal(new AirliftStatus(2, 2, 0, 0), w.AirliftOf(c5));
        Assert.Equal([a, b], w.AirliftAboard(c5).OrderBy(x => x.Id));
        Assert.Equal((0, 0), World.CargoAboard(c5)); // either the tanks or the pallets
        var kinds = ev.Select(x => x.E.Kind).ToList();
        Assert.Equal([AirliftEventKind.PalletsOff, AirliftEventKind.Boarding, AirliftEventKind.Aboard, AirliftEventKind.Boarding, AirliftEventKind.Aboard], kinds);
        var boarding = ev.Where(x => x.E.Kind == AirliftEventKind.Boarding).ToList();
        var aboard = ev.Where(x => x.E.Kind == AirliftEventKind.Aboard).ToList();
        Assert.Equal(10 * Tps, (int)(aboard[0].Tick - boarding[0].Tick));
        Assert.Equal(10 * Tps, (int)(aboard[1].Tick - boarding[1].Tick));
        Assert.True(boarding[1].Tick > aboard[0].Tick, "one at a time up the ramp");
        Assert.True(a.Inside != 0 && !a.CanAct, "aboard: off the map");
        Assert.Equal(FlightPhase.Parked, c5.AirOps!.Phase);
        Assert.True(c5.AirOps.Hold, "held for its next order");
        w.Run(30 * Tps);
        Assert.Equal(FlightPhase.Parked, c5.AirOps.Phase); // it waits loaded
        Assert.Equal(2, c5.Lift);
    }

    // Night fix (2026-10-24, decision 314): two Bookers sent together from any side both board. In the heavy bay the ramp point is
    // on ground the routing keeps a tank off, so a route ends 3 cells short of it; before, the board range counted from the ramp
    // point itself and the second tank could stop 4.1 to 4.4 cells from it, past the 4-cell range, and wait for ever (from the north
    // and the north-west every time). The range now counts from the nearest cell the tank can stand on (AirliftBoardSpot).
    [Theory]
    [InlineData(20, 60, 4)]   // north-west: stalled before
    [InlineData(100, 30, 4)]  // north: stalled before
    [InlineData(140, 60, 4)]  // north-east: the second stalled before
    [InlineData(220, 60, 8)]  // east-north-east, spread: stalled before
    [InlineData(10, 160, 3)]  // west
    [InlineData(250, 160, 4)] // east
    [InlineData(130, 240, 4)] // south (the layout of the other tests)
    public void TwoBookersBoardFromAnyApproach(int x, int y, int gap)
    {
        var (w, _, c5) = Based();
        var a = w.SpawnCrewed(1, Booker, x, y, Facing.North).Vehicle;
        var b = w.SpawnCrewed(1, Booker, x + gap, y, Facing.North).Vehicle;
        var al = c5.Def.Archetype.Airlift!;
        var ramp = World.AirliftRamp(c5, al);
        var spot = w.AirliftBoardSpot(a, c5);
        Assert.True(World.Within(spot, ramp, al.BoardRange), "the spot is inside the board range of the ramp point");
        Assert.Equal(spot, w.AirliftBoardSpot(b, c5));
        var t0 = w.Tick;
        var ev = Board(w, c5, [a, b], 150 * Tps);
        var aboard = ev.Where(e => e.E.Kind == AirliftEventKind.Aboard).Select(e => e.Tick).ToList();
        var boarding = ev.Where(e => e.E.Kind == AirliftEventKind.Boarding).Select(e => e.Tick).ToList();
        output.WriteLine($"from ({x}, {y}): spot {(spot - ramp).Len().Raw / (double)Fixed.OneRaw:F2} cells from the ramp; ordered t{t0}, boarding t{string.Join(", t", boarding)}, " +
            $"aboard t{string.Join(", t", aboard)}; first up {(boarding[0] - t0) / (double)Tps:F1} s after the order");
        Assert.Equal((c5.Id, c5.Id), (a.Inside, b.Inside));
        Assert.Equal(2, aboard.Count);
        Assert.InRange((int)(boarding[1] - aboard[0]), 0, 2 * Tps); // the second was waiting in range, not stalled
    }

    // Night fix (decision 315): a Rearm order on an airlifter with tanks aboard gives it its service (fuel, HP; no pallets) and it
    // stays parked, held, with them; it leaves only on an order (Deliver, Move, an air task), here a Move.
    [Fact]
    public void ARearmOrderKeepsItParkedWithItsTanks()
    {
        var (w, home, c5) = Based();
        var (a, b) = Bookers(w);
        Board(w, c5, [a, b]);
        c5.Lifetime = c5.Def.Lifetime / 2;
        c5.HP = c5.Def.MaxHp / 2;
        Issue(w, CommandKind.Rearm, [c5]);
        var departed = false;
        for (var t = 0; t < 120 * Tps; t++)
        {
            w.Step();
            departed |= w.AirfieldEvents.Any(e => e.Kind == AirfieldEventKind.Departed && e.Aircraft == c5.Id);
        }
        Assert.False(departed);
        Assert.Equal((FlightPhase.Parked, home.Id), (c5.AirOps!.Phase, c5.AirOps.Airfield));
        Assert.Equal(new AirliftStatus(2, 2, 0, 0), w.AirliftOf(c5));
        Assert.Equal((0, 0), World.CargoAboard(c5)); // still tanks, not pallets
        Assert.Equal((c5.Def.MaxHp, c5.Def.Lifetime), (c5.HP, c5.Lifetime)); // serviced: repaired and refuelled
        Assert.True(c5.AirOps.Hold, "held for its next order");
        Issue(w, CommandKind.Move, [c5], cell: new Cell(300, 300));
        Assert.True(Enumerable.Range(0, 120 * Tps).Any(_ =>
        {
            w.Step();
            return c5.AirOps is null or { Phase: FlightPhase.Climb };
        }), "a Move order takes it off");
        Assert.Equal(2, w.AirliftAboard(c5).Count);
    }

    // Only airliftable vehicles, only 2, only while it is parked at the main air base.
    [Fact]
    public void ItTakesOnlyTwoBookersParkedAtTheMainBase()
    {
        var (w, _, c5) = Based();
        var (a, b) = Bookers(w);
        var c = w.SpawnCrewed(1, Booker, 138, 240, Facing.North).Vehicle;
        var abrams = w.SpawnCrewed(1, "main_battle_tank", 142, 240, Facing.North).Vehicle;
        var humvee = w.SpawnCrewed(1, "humvee", 146, 240, Facing.North).Vehicle;
        var soldier = w.Spawn(1, "Rifleman", 150, 240);
        Issue(w, CommandKind.Enter, [abrams, humvee, soldier], c5.Id);
        w.Step();
        Assert.All(new[] { abrams, humvee, soldier }, u => Assert.NotEqual(OrderKind.Enter, u.Order.Kind));
        Board(w, c5, [a, b]);
        Issue(w, CommandKind.Enter, [c], c5.Id); // full
        w.Step();
        Assert.NotEqual(OrderKind.Enter, c.Order.Kind);
        Assert.Equal(0u, c.Inside);

        // Flying: no boarding.
        var w2 = Arena.NewSentinel(200);
        var fly = w2.Spawn(1, "airlifter", 100, 100, Facing.East);
        w2.Step();
        var t = w2.SpawnCrewed(1, Booker, 100, 120).Vehicle;
        Issue(w2, CommandKind.Enter, [t], fly.Id);
        w2.Step();
        Assert.NotEqual(OrderKind.Enter, t.Order.Kind);
        Assert.False(w2.AirliftDocked(fly));
    }

    // With tanks aboard it loads no pallets and takes no airdrop; Exit (parked) lets them out behind it and the pallets come back.
    [Fact]
    public void TanksOrContainersNeverBoth()
    {
        var (w, _, c5) = Based();
        var (a, b) = Bookers(w);
        var stash = w.Spawn(1, "supply_stash", 300, 300);
        Board(w, c5, [a, b]);
        c5.HomeReason = HomeReason.Rearm; // held in its bay on the full service (a Rearm visit)
        w.Run(60 * Tps);
        Assert.Equal((0, 0), World.CargoAboard(c5)); // no pallets load with tanks aboard
        Issue(w, CommandKind.Airdrop, [c5], stash.Id);
        w.Step();
        Assert.Contains(w.OrderRefusals, r => r.Aircraft == c5.Id && r.Reason == OrderRefusalReason.NoAmmo);
        Assert.Equal(FlightPhase.Parked, c5.AirOps!.Phase);

        // The tank and its driver stay in on an Exit of their own; Exit on the parked airlifter lets both tanks out behind it.
        Issue(w, CommandKind.Exit, [a, w.Unit(a.Driver)!]);
        w.Step();
        Assert.Equal(c5.Id, a.Inside);
        Issue(w, CommandKind.Exit, [c5]);
        w.Step();
        Assert.Equal((0u, 0u), (a.Inside, b.Inside));
        Assert.All(new[] { a, b }, u => Assert.True(World.Within(u.Pos, World.AirliftRamp(c5, c5.Def.Archetype.Airlift!), Fixed.FromInt(3))));
        Assert.True(a.CanAct && b.CanAct);
        w.Run(60 * Tps);
        Assert.Equal((4, 10000), World.CargoAboard(c5)); // empty again: the pallets load (4 in 30 s)
    }

    // Deliver: it flies to the forward airbase, lands, parks on a stand and the tanks drive off 5 s apart (the first once the ramp is
    // down) to the field garage's rally point; then it flies home and loads its pallets.
    [Fact]
    public void ItDeliversThemToAForwardAirbaseAndTheyDriveOff()
    {
        var (w, home, c5) = Based();
        var fwd = Forward(w);
        var (a, b) = Bookers(w);
        Board(w, c5, [a, b]);
        Issue(w, CommandKind.Deliver, [c5], fwd.Core);
        var ordered = w.Tick;
        var rally = w.RallyCell(w.Unit(fwd.Facility)!);
        uint parked = 0;
        var off = new List<(uint Tick, AirliftEvent E)>();
        for (var t = 0; t < 8000 && !(a.Order.Kind == OrderKind.Idle && b.Order.Kind == OrderKind.Idle && a.Inside == 0 && b.Inside == 0); t++)
        {
            w.Step();
            if (parked == 0 && c5.AirOps is { Phase: FlightPhase.Parked } p && p.Airfield == fwd.Id)
            {
                parked = w.Tick;
            }
            off.AddRange(w.AirliftEvents.Where(e => e.Kind == AirliftEventKind.DroveOff).Select(e => (w.Tick, e)));
        }
        output.WriteLine($"parked at the forward airbase {(parked - ordered) / (double)Tps:F1} s after the order (t{parked}); drove off at t{string.Join(", t", off.Select(x => x.Tick))}; " +
            $"at the rally {Cell.Of(a.Pos)} {Cell.Of(b.Pos)} (rally {rally}) t{w.Tick}");
        Assert.True(parked > 0);
        Assert.Equal(2, off.Count);
        Assert.Equal(5 * Tps, (int)(off[0].Tick - parked));
        Assert.Equal(5 * Tps, (int)(off[1].Tick - off[0].Tick));
        Assert.All(new[] { a, b }, u => Assert.True(World.Within(u.Pos, rally.Center(), Fixed.FromInt(4)), $"{u.Id} at {Cell.Of(u.Pos)}"));
        Assert.True(a.CanAct && b.CanAct);

        // Home, it loads its 4 containers again.
        for (var t = 0; t < 8000 && !(c5.AirOps is { Phase: FlightPhase.Parked } o && o.Airfield == home.Id && World.CargoAboard(c5).Pallets == 4); t++)
        {
            w.Step();
        }
        Assert.Equal((4, 10000), World.CargoAboard(c5));
        Assert.Equal(0, c5.Lift);
    }

    // Shot down with tanks aboard: they and their drivers are lost.
    [Fact]
    public void ShotDownTheTanksAreLost()
    {
        var w = Arena.NewSentinel(320);
        var c5 = w.Spawn(1, "airlifter", 160, 160, Facing.East);
        w.Step();
        c5.MutableSalvos[0] = 0; // no flares
        var (a, da) = w.SpawnCrewed(1, Booker, 20, 20);
        var (b, db) = w.SpawnCrewed(1, Booker, 24, 20);
        foreach (var t in new[] { a, b })
        {
            t.Inside = c5.Id; // aboard (as boarding leaves them)
            t.Pos = c5.Pos;
        }
        c5.MutableCharges[World.AirdropWeapon(c5)!.Stock] = 0;
        for (var i = 0; i < 4; i++)
        {
            w.SpawnCrewed(2, "sam_vehicle", 150 + 6 * i, 150);
        }
        Issue(w, CommandKind.Move, [c5], cell: new Cell(160, 160));
        var lost = new List<AirliftEvent>();
        var cargo = 0;
        for (var t = 0; t < 9000 && c5.Alive; t++)
        {
            w.Step();
            lost.AddRange(w.AirliftEvents.Where(e => e.Kind == AirliftEventKind.Lost));
            cargo += w.LogisticsEvents.Count(e => e.Kind == LogisticsEventKind.CargoLost);
            Assert.True(t > 0 || c5.Lift == 2);
        }
        Assert.False(c5.Alive);
        Assert.Equal([a.Id, b.Id], lost.Select(e => e.Vehicle));
        Assert.Equal(0, cargo); // no containers aboard
        Assert.All(new[] { a, b, da, db }, u => Assert.Null(w.Unit(u.Id)));
        Assert.Empty(w.All(Booker));
    }

    // ---- Determinism ----

    // Board, deliver and drive off; the commands in order or reversed; the hash every tick.
    private static List<ulong> Script(bool reversed)
    {
        var (w, _, c5) = Based(7);
        var fwd = Forward(w);
        var (a, b) = Bookers(w);
        var t0 = w.Tick;
        var cmds = new[]
        {
            new Command { Tick = t0 + 1, Player = 1, Seq = 1, Kind = CommandKind.Enter, Units = [a.Id], Target = c5.Id },
            new Command { Tick = t0 + 1, Player = 1, Seq = 2, Kind = CommandKind.Enter, Units = [b.Id], Target = c5.Id },
            new Command { Tick = t0 + 1200, Player = 1, Seq = 3, Kind = CommandKind.Deliver, Units = [c5.Id], Target = fwd.Core },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        for (var t = 0; t < 4500; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
        }
        Assert.Equal(0u, a.Inside);
        return hashes;
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var x = Script(false);
        Assert.Equal(x, Script(false));
        Assert.Equal(x, Script(true));
    }
}
