using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Logistics (2026-10-22; the owner's brief, the game repo's logistics spec; docs/design.md, "Logistics"): the airlifter (a C-5 type:
// class XL, 4 containers of 2,500 supply, loaded free at the main air base) airdrops on a field ammo point's or a supply stash's drop
// zone (all 4 in one pass or one a pass; scatter; pallets outside the zone lost) or lands at a forward airbase and unloads into its
// stock; the field ammo point rearms its side's ground units out of its stock; the supply stash stores; cargo trucks load at a stash,
// a forward airbase or the main base and unload at an ammo point, a stash or a forward airbase; the forward airbase (a compact core:
// runway, 2 stands, a 2-bay field garage) services 2 aircraft and 2 vehicles at once out of its limited stock, produces nothing and
// stands away from the main air base; cargo is lost when the airlifter is shot down; all of it deterministic.
public class LogisticsTests(ITestOutputHelper output)
{
    private const long Cell1 = Fixed.OneRaw;
    private const int Tps = World.TicksPerSecond;
    private const string Airlifter = "airlifter";

    // The main air base at (40, 120) (x 40-220, y 120-206); a forward airbase at (150, 360) on a 500-cell map (y 360-424: 154 cells
    // from the base's footprint).
    private static readonly Cell BaseCell = new(40, 120);
    private static readonly Cell ForwardCell = new(150, 360);

    private static World Sentinel(int n, ulong seed = 1) => Arena.NewSentinel(n, seed);

    // Both players Sentinel, no starting army.
    private static World Duel(int n, ulong seed = 1)
    {
        var content = TestContent.Mvp;
        var setup = TestContent.Sentinel(seed).For(content, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        return new World(new Grid(n, n), MatchCompiler.Compile(content, setup with { Players = [p0, p1] }));
    }

    private static UnitDef Def(World w, string id, byte player = 1) => w.Player(player)!.Setup.Find(w.Content, id)!;

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static double Cells(long raw) => raw / (double)Cell1;

    private static string P(Vec v) => $"({F(Cells(v.X.Raw))}, {F(Cells(v.Y.Raw))})";

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, uint target = 0, int index = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Target = target, Index = index });

    private static void Issue(World w, CommandKind kind, IEnumerable<Unit> us, uint target) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = kind, Units = [.. us.Select(u => u.Id)], Target = target });

    // A flying airlifter of player 1 at (x, y), at its height already (the sandbox's put-down rule), full of cargo.
    private static Unit Fly(World w, int x, int y, int facing)
    {
        var b = w.Spawn(1, Airlifter, x, y, facing);
        w.Step();
        return b;
    }

    private static Airfield Base(World w) => w.SpawnAirBase(Def(w, "airbase_core"), BaseCell, 0, []) ?? throw new InvalidOperationException("no base");

    private static Airfield Forward(World w, Cell? at = null, int facing = 0) =>
        w.SpawnAirBase(Def(w, "forward_airbase"), at ?? ForwardCell, facing, []) ?? throw new InvalidOperationException("no forward airbase");

    private static int RunUntil(World w, Func<bool> done, int max)
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

    // Airdrop flies u's airdrop until it heads home with every pallet down (at most max ticks), collecting its pallets' releases and
    // the logistics events.
    private static (List<(uint Tick, BombRelease R)> Releases, List<LogisticsEvent> Events) Airdrop(World w, Unit u, int max = 8000)
    {
        var rel = new List<(uint, BombRelease)>();
        var ev = new List<LogisticsEvent>();
        for (var t = 0; t < max; t++)
        {
            w.Step();
            rel.AddRange(w.BombReleases.Where(r => r.Aircraft == u.Id).Select(r => (w.Tick, r)));
            ev.AddRange(w.LogisticsEvents.Where(e => e.Unit == u.Id));
            if ((u.Homing || u.Order.Kind == OrderKind.Idle) && w.Projectiles.All(p => p.Bomb is null))
            {
                break;
            }
        }
        return (rel, ev);
    }

    // ---- Content ----

    [Fact]
    public void TheLogisticsPiecesAreData()
    {
        var c = TestContent.Mvp;
        var w = Sentinel(64);
        var d = Def(w, Airlifter);
        var a = d.Archetype;
        Assert.Equal(("Airlifter", AircraftClass.XL, 1800, 1800, 3000), (a.Name, a.Aircraft!.SizeClass, a.BuildTime, d.MaxHp, a.Cost));
        Assert.Equal("airlifter", c.AirClasses[(int)a.Flight!.Class - 1].Id);
        Assert.Equal(300 * 512L, a.Flight.HeightRaw); // 300 m
        Assert.InRange(a.Flight.Wing!.Cruise, 13 * Cell1 - 20, 13 * Cell1); // 13 cells/s
        Assert.True(a.Aircraft.Span < Fixed.FromInt(c.Archetype("airbase_bay")!.FootprintW), "through the heavy bay's door");
        Assert.Equal([(CountermeasureKind.Flare, 30)], a.Countermeasures.Select(x => (x.Kind, x.Salvos)));
        var pallets = Assert.Single(d.Weapons);
        Assert.Equal((4, 2500, 150), (pallets.Effect.Ammo, pallets.Effect.Airdrop!.Amount, pallets.Effect.Airdrop.UnloadTicks));
        Assert.Equal(["all4", "single"], pallets.Effect.Delivery!.FireModes.Select(m => m.Id));
        Assert.Equal((4, 1), (pallets.Effect.Delivery.SalvoOf(0), pallets.Effect.Delivery.SalvoOf(1)));
        Assert.Equal(150 * 512L, pallets.Effect.Delivery.ReleaseHeightRaw); // a run at 150 m
        Assert.Equal((4, 10000), World.CargoAboard(w.Spawn(1, Airlifter, 30, 30)));
        Assert.Null(a.Mission); // no Guard: it never picks targets
        output.WriteLine($"cruise {F(a.Flight.Wing.Cruise / (double)Cell1)} cells/s, turn at cruise {F(Cells(a.Flight.Wing.Radius(a.Flight.Wing.Cruise)))} cells, " +
            $"fuel {a.Lifetime / Tps / 60} min, fly zone {F(Cells(a.Flight.FlyZoneMarginRaw))} cells");
        // The stores.
        Assert.Equal((6000, 0, 0, 20, true, false), Supply(c, "facp"));
        Assert.Equal((15000, 0, 0, 1, false, true), Supply(c, "supply_stash"));
        Assert.Equal((20000, 8000, 8000, 20, false, true), Supply(c, "forward_airbase"));
        Assert.Equal((0, 0, 0, 1, false, true), Supply(c, "airbase_core")); // unlimited
        Assert.True(c.Archetype("supply_stash")!.Components.Select(i => c.Components[i]).SelectMany(x => x.Effects).OfType<SupplyEffect>().Single().Storage);
        Assert.Equal((300, 8, 8), (c.Archetype("facp")!.BuildTime, c.Archetype("facp")!.FootprintW, c.Archetype("facp")!.FootprintH));
        Assert.Equal((20, 14), (c.Archetype("supply_stash")!.FootprintW, c.Archetype("supply_stash")!.FootprintH));
        Assert.Equal((140, 64, 600), (c.Archetype("forward_airbase")!.FootprintW, c.Archetype("forward_airbase")!.FootprintH, c.Archetype("forward_airbase")!.BuildTime));
        Assert.Equal(120, c.Archetype("forward_airbase")!.AwayFrom!.Cells);
        foreach (var id in new[] { Airlifter, "facp", "supply_stash", "forward_airbase", "forward_airbase_hut", "forward_airbase_apron", "forward_airbase_stand" })
        {
            Assert.True(c.Faction("sentinel")!.Archetypes[c.Archetype(id)!.Index], id);
        }
        // The forward airbase's garage produces nothing; its doors only service.
        var hut = c.Archetype("forward_airbase_hut")!;
        Assert.Equal((0, 2, 0), (hut.Production!.Bays, hut.Production.Doors, hut.Production.QueueMax));
        Assert.NotNull(hut.Service);

        static (int Stock, int Built, int Start, int Round, bool GroundOnly, bool Source) Supply(ContentSet c, string id)
        {
            var s = c.Archetype(id)!.Components.Select(i => c.Components[i]).SelectMany(x => x.Effects).OfType<SupplyEffect>().Single();
            return (s.Stock, s.ConstructedStock, s.StartStock, s.RoundCost, s.GroundOnly, s.CargoSource);
        }
    }

    // ---- The forward airbase ----

    [Fact]
    public void TheForwardAirbaseLaysItsRunwayStandsAndGarage()
    {
        var w = Sentinel(500);
        var af = Forward(w);
        Assert.Equal("forward_airbase", af.Type.Id);
        Assert.Equal(7, af.Segments.Count);
        Assert.Equal("forward_airbase_hut", w.Unit(af.Facility)!.Def.Archetype.Id);
        Assert.Equal("forward_airbase_apron", w.Unit(af.Hangar)!.Def.Archetype.Id);
        Assert.Equal(0u, af.Radar);
        var stations = af.Rows.SelectMany(r => r.Stations).ToList();
        Assert.Equal(2, stations.Count);
        Assert.All(stations, s => Assert.Equal("forward_airbase_stand", w.Unit(s.Unit)!.Def.Archetype.Id));
        var core = w.Unit(af.Core)!;
        Assert.Equal(new StoreStock(8000, 20000), w.StockOf(core));
        Assert.Equal(new StoreStock(8000, 20000), w.StockOf(w.Unit(af.Facility)!)); // any piece: the field's stock
        Assert.Same(core, w.AirfieldStore(af));
        // Every aircraft class takes a stand; the jets, the attack jet and the airlifter fit its runway.
        foreach (var id in new[] { "watcher_drone", "multirole_jet", "fighter_jet", "attack_jet", Airlifter })
        {
            var ac = w.Content.Archetype(id)!.Aircraft!;
            Assert.Equal(2, w.StationsFor(af, ac).Count);
            Assert.True(af.Type.Serves(ac.SizeClass), id);
        }
        // No rows may be added (2 stands only).
        Assert.Throws<ArgumentException>(() => w.SpawnAirBase(Def(w, "forward_airbase"), new Cell(150, 30), 0, [(0, 0)]));
        // A forward airbase turned a quarter lays the same pieces.
        var w2 = Sentinel(500);
        var af2 = Forward(w2, new Cell(300, 100), Facing.East);
        Assert.Equal((7, 2), (af2.Segments.Count, af2.Rows.SelectMany(r => r.Stations).Count()));
        // Its garage's doors turn with it (exits west of it now) and it services there.
        var hut = w2.Unit(af2.Facility)!;
        var exit = World.ExitPoint(hut, 0);
        output.WriteLine($"turned garage at {P(hut.Pos)}, door 0's exit {P(exit)}");
        Assert.True(exit.X.Raw < hut.Pos.X.Raw - 6 * Cell1, "the doors face west (its south front turned a quarter)");
        var v = w2.SpawnCrewed(1, "sam_vehicle", (int)Cells(exit.X.Raw) - 8, (int)Cells(exit.Y.Raw)).Vehicle;
        v.HP = v.Def.MaxHp / 2;
        Issue(w2, CommandKind.Service, v, target: hut.Id);
        var inside = false;
        for (var t = 0; t < 1500 && (t == 0 || v.ServiceAt != 0); t++)
        {
            w2.Step();
            inside |= v.InGarage;
        }
        Assert.True(inside, "in its bay");
        Assert.Equal(v.Def.MaxHp, v.HP);
    }

    [Fact]
    public void ItStandsAwayFromTheMainAirBase()
    {
        var w = Sentinel(500);
        Base(w); // x 40-220, y 120-206
        var def = Def(w, "forward_airbase");
        Assert.Null(w.SpawnAirBase(def, new Cell(150, 300), 0, [])); // 94 cells from the base
        Assert.Null(w.SpawnAirBase(def, new Cell(150, 325), 0, [])); // 119
        Assert.False(w.AwayFromAllows(def, new Cell(150, 325), 140, 64));
        Assert.True(w.AwayFromAllows(def, new Cell(150, 326), 140, 64)); // 120
        Assert.NotNull(w.SpawnAirBase(def, new Cell(150, 326), 0, []));
        // The other side's main air base does not matter; a worker's Build is refused too close.
        var w2 = Duel(500);
        var p2 = w2.SpawnAirBase(Def(w2, "airbase_core", 2), BaseCell, 0, []);
        Assert.NotNull(p2);
        Assert.NotNull(w2.SpawnAirBase(Def(w2, "forward_airbase"), new Cell(150, 300), 0, []));
        var w3 = Sentinel(500);
        Base(w3);
        var dozer = w3.SpawnCrewed(1, "bulldozer", 220, 300).Vehicle;
        w3.Issue(new Command { Tick = w3.Tick, Player = 1, Kind = CommandKind.Build, Units = [dozer.Id], Unit = "forward_airbase", Cell = new Cell(150, 300) });
        w3.Step();
        Assert.DoesNotContain(w3.Units, u => u.Def.Archetype.Id == "forward_airbase");
        w3.Issue(new Command { Tick = w3.Tick, Player = 1, Kind = CommandKind.Build, Units = [dozer.Id], Unit = "forward_airbase", Cell = new Cell(150, 330) });
        w3.Step();
        var site = Assert.Single(w3.Units, u => u.Def.Archetype.Id == "forward_airbase");
        Assert.False(site.IsBuilt);
        var built = RunUntil(w3, () => site.IsBuilt, 120 * Tps);
        output.WriteLine($"built by one bulldozer in {F(built / (double)Tps)} s");
        Assert.True(built > 0);
        Assert.Equal(new StoreStock(8000, 20000), w3.StockOf(site)); // built: 8,000
        var af = w3.AirfieldOf(site)!;
        Assert.Equal((7, 2), (af.Segments.Count, af.Rows.SelectMany(r => r.Stations).Count()));
    }

    [Fact]
    public void ItProducesNothing()
    {
        var w = Sentinel(500);
        var af = Forward(w);
        foreach (var piece in new[] { af.Core, af.Facility, af.Hangar })
        {
            foreach (var what in new[] { "multirole_jet", Airlifter, "technical", "sentinel_cargo_truck" })
            {
                w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [piece], Unit = what });
            }
        }
        w.Run(2 * Tps);
        Assert.All(new[] { af.Core, af.Facility, af.Hangar }, id => Assert.Empty(w.Unit(id)!.ProductionQueue));
        Assert.All(w.Unit(af.Facility)!.Bays, b => Assert.True(b.Def < 0));
    }

    // Two jets sent to rearm there land, park on the 2 stands and are refuelled, rearmed and repaired out of the stock; a third holds
    // until a stand frees. Two vehicles are serviced in the field garage at once (a third waits for a door), paid from the stock.
    [Fact]
    public void ItServicesTwoAircraftAndTwoVehiclesAtOnceFromItsStock()
    {
        var w = Sentinel(500);
        var af = Forward(w);
        var core = w.Unit(af.Core)!;
        var jets = new[] { w.Spawn(1, "Multirole 500 lb", 380, 300, Facing.West), w.Spawn(1, "Multirole 500 lb", 390, 320, Facing.West),
            w.Spawn(1, "Multirole 500 lb", 400, 340, Facing.West) };
        w.Step();
        foreach (var j in jets)
        {
            j.MutableCharges[0] = 0;
            j.HP = j.Def.MaxHp / 2;
        }
        Issue(w, CommandKind.Rearm, jets, af.Core);
        w.Step();
        foreach (var j in jets)
        {
            j.AirOps!.Hold = true; // held once serviced (as a recall), so both stay parked
        }
        var parked = RunUntil(w, () => jets.Count(j => j.AirOps is { Phase: FlightPhase.Parked }) == 2, 20000);
        Assert.True(parked > 0, "two jets park");
        var third = jets.Single(j => j.AirOps is not { Phase: FlightPhase.Parked });
        output.WriteLine($"two parked after {F(parked / (double)Tps)} s; the third: {third.AirOps?.Phase}");
        Assert.True(third.AirOps is { Phase: FlightPhase.Holding or FlightPhase.Final } || third.AirOps?.Station == 0 || third.AirOps is { Phase: FlightPhase.Parked } == false);
        var stock0 = core.Stock;
        var both = jets.Where(j => j != third).ToArray();
        var serviced = RunUntil(w, () => both.All(j => w.ServiceOf(j).Serviced || j.AirOps is not { Phase: FlightPhase.Parked }), 6000);
        Assert.True(serviced > 0);
        Assert.All(both, j => Assert.Equal((j.Def.StockMax[0], j.Def.MaxHp), (j.Charges[0], j.HP)));
        var used = stock0 - core.Stock;
        output.WriteLine($"two jets rearmed and repaired in {F(serviced / (double)Tps)} s for {used} supply");
        Assert.True(used > 0);
        // The vehicles: three damaged trucks and an empty air defence vehicle sent to the garage; two doors.
        var hut = w.Unit(af.Facility)!;
        var vs = new List<Unit>();
        for (var i = 0; i < 3; i++)
        {
            var v = w.SpawnCrewed(1, "sam_vehicle", 160 + 10 * i, 440).Vehicle;
            v.MutableCharges[0] = 0;
            v.HP = v.Def.MaxHp / 2;
            vs.Add(v);
        }
        var s0 = core.Stock;
        Issue(w, CommandKind.Service, vs, hut.Id);
        var inBay = 0;
        var maxIn = 0;
        for (var t = 0; t < 3000 && (t == 0 || vs.Any(v => v.ServiceAt != 0)); t++)
        {
            w.Step();
            inBay = vs.Count(v => v.InGarage);
            maxIn = Math.Max(maxIn, inBay);
        }
        output.WriteLine($"vehicles: at most {maxIn} in its bays at once, {s0 - core.Stock} supply for 3 (10 missiles at 60 and 140 HP at 10 per 100 each)");
        Assert.Equal(2, maxIn);
        Assert.All(vs, v => Assert.Equal((10, v.Def.MaxHp), (v.Charges[0], v.HP)));
        Assert.Equal(3 * (10 * 60 + 140 * 10 / 100), s0 - core.Stock);
    }

    // With its stock empty a parked aircraft gets nothing (it only parks) and a vehicle comes out of the garage as it went in;
    // the stock refilled, both are served.
    [Fact]
    public void EmptyItOnlyParks()
    {
        var w = Sentinel(500);
        var af = Forward(w);
        var core = w.Unit(af.Core)!;
        core.Stock = 0;
        var jet = w.Spawn(1, "Multirole 500 lb", 380, 300, Facing.West);
        w.Step();
        jet.MutableCharges[0] = 0;
        Issue(w, CommandKind.Rearm, jet, target: af.Core);
        Assert.True(RunUntil(w, () => jet.AirOps is { Phase: FlightPhase.Parked }, 12000) > 0);
        var fuel = jet.Lifetime;
        w.Run(30 * Tps);
        Assert.Equal((0, fuel, 0), (jet.Charges[0], jet.Lifetime, core.Stock)); // nothing given, nothing drawn
        Assert.Equal(FlightPhase.Parked, jet.AirOps!.Phase); // waits, unserviced
        var v = w.SpawnCrewed(1, "sam_vehicle", 160, 440).Vehicle;
        v.MutableCharges[0] = 0;
        Issue(w, CommandKind.Service, v, target: af.Facility);
        Assert.True(RunUntil(w, () => v.ServiceAt == 0, 2000) > 0);
        Assert.Equal(0, v.Charges[0]); // in and out, nothing paid for
        core.Stock = 5000;
        Assert.True(RunUntil(w, () => jet.Charges[0] == jet.Def.StockMax[0], 3000) > 0);
        Assert.True(core.Stock < 5000);
    }

    // ---- Airdrops ----

    // All 4 in one pass on a supply stash: released at 150 m, a stick of 4, every pallet inside the zone (10,000 into the stock);
    // then home on its own to the main air base, loaded free (4 pallets, 30 s in a heavy bay) and held there.
    [Fact]
    public void AllFourOnASupplyStashThenHomeToLoad()
    {
        var w = Sentinel(500);
        var af = Base(w);
        var stash = w.Spawn(1, "supply_stash", 300, 330);
        Assert.Equal(new StoreStock(0, 15000), w.StockOf(stash));
        var c5 = Fly(w, 60, 330, Facing.East);
        Issue(w, CommandKind.Airdrop, c5, target: stash.Id);
        w.Step();
        Assert.Equal((OrderKind.AttackGround, stash.Id), (c5.Order.Kind, c5.AirdropAt));
        var (rel, ev) = Airdrop(w, c5);
        var zone = w.DropZoneOf(stash)!.Value;
        foreach (var (t, r) in rel)
        {
            output.WriteLine($"t{t} released at {P(r.At)}, {r.Altitude.Raw / 512} m; predicted {P(r.Predicted)}");
        }
        foreach (var e in ev)
        {
            output.WriteLine($"{e.Kind} at {P(e.At)}: {F(Cells(World.Len3(e.At.X.Raw - zone.Centre.X.Raw, e.At.Y.Raw - zone.Centre.Y.Raw, 0)))} cells from the zone's centre");
        }
        Assert.Equal(4, rel.Count);
        Assert.InRange(rel[^1].Tick - rel[0].Tick, 1u, 20u); // one pass (a stick 2 cells apart)
        Assert.All(rel, r => Assert.InRange(r.R.Altitude.Raw / 512, 145, 160));
        Assert.Equal(4, ev.Count(e => e.Kind == LogisticsEventKind.PalletLanded && e.Store == stash.Id && e.Amount == 2500));
        Assert.Equal(new StoreStock(10000, 15000), w.StockOf(stash));
        Assert.Equal((0, 0), World.CargoAboard(c5));
        Assert.True(c5.Homing);
        Assert.Equal(HomeReason.Rearm, c5.HomeReason);
        Assert.Equal(0u, c5.AirdropAt);
        Assert.True(RunUntil(w, () => c5.AirOps is { Phase: FlightPhase.Parked }, 12000) > 0);
        Assert.Equal(af.Id, c5.AirOps!.Airfield);
        var load = RunUntil(w, () => w.ServiceOf(c5).Serviced, 120 * Tps);
        output.WriteLine($"loaded in {F(load / (double)Tps)} s");
        Assert.InRange(load, 29 * Tps, 31 * Tps); // 4 containers at 15 s each, 2x in a heavy bay
        Assert.Equal((4, 10000), World.CargoAboard(c5));
        w.Run(30 * Tps);
        Assert.Equal(FlightPhase.Parked, c5.AirOps?.Phase); // held until its next order
    }

    // One a pass on a field ammo point: 4 passes, a pallet each; the point's 6,000 fills with the first two and a part of the
    // third (the rest of it lost), the fourth is lost whole.
    [Fact]
    public void OneAPassOnAFieldAmmoPoint()
    {
        var w = Sentinel(500);
        Base(w);
        var facp = w.Spawn(1, "facp", 300, 330);
        Assert.Equal(new StoreStock(0, 6000), w.StockOf(facp));
        var c5 = Fly(w, 60, 330, Facing.East);
        Issue(w, CommandKind.SetFireMode, c5, index: 1);
        Issue(w, CommandKind.Airdrop, c5, target: facp.Id);
        var (rel, ev) = Airdrop(w, c5, 20000);
        var ticks = rel.Select(r => r.Tick).ToList();
        output.WriteLine($"releases at t{string.Join(", t", ticks)}; {string.Join("; ", ev.Select(e => $"{e.Kind} {e.Amount} (lost {e.Lost})"))}");
        Assert.Equal(4, rel.Count);
        Assert.All(ticks.Zip(ticks.Skip(1)), p => Assert.True(p.Second - p.First > 10 * Tps, "a pass each"));
        Assert.Equal([2500, 2500, 1000, 0], ev.Where(e => e.Kind == LogisticsEventKind.PalletLanded).Select(e => e.Amount));
        Assert.Equal([0, 0, 1500, 2500], ev.Where(e => e.Kind == LogisticsEventKind.PalletLanded).Select(e => e.Lost));
        Assert.Equal(new StoreStock(6000, 6000), w.StockOf(facp));
        Assert.True(c5.Homing);
    }

    // Pallets land with scatter (seeded): over 12 seeds all4 on a stash, every pallet within the delivery's reach of the zone's centre;
    // dropped on a point outside every zone (an airdrop order whose zone is elsewhere) every pallet is lost.
    [Fact]
    public void PalletsScatterAndThoseOutsideTheZoneAreLost()
    {
        var far = 0.0;
        var landed = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var w = Sentinel(500, seed);
            var stash = w.Spawn(1, "supply_stash", 300, 330);
            var c5 = Fly(w, 60, 330, Facing.East);
            Issue(w, CommandKind.Airdrop, c5, target: stash.Id);
            var (_, ev) = Airdrop(w, c5);
            var zone = w.DropZoneOf(stash)!.Value;
            foreach (var e in ev.Where(e => e.Kind is LogisticsEventKind.PalletLanded or LogisticsEventKind.PalletLost))
            {
                far = Math.Max(far, Cells(World.Len3(e.At.X.Raw - zone.Centre.X.Raw, e.At.Y.Raw - zone.Centre.Y.Raw, 0)));
                landed += e.Kind == LogisticsEventKind.PalletLanded ? 1 : 0;
            }
        }
        output.WriteLine($"12 sorties of 4: {landed} of 48 inside the 8-cell zone, the farthest {F(far)} cells from its centre");
        Assert.InRange(landed, 46, 48);
        Assert.InRange(far, 2.0, 8.5);
        // Off the zone: the order's point 30 cells east of the stash's zone (set directly; the command always aims at the zone).
        var w2 = Sentinel(500);
        var s2 = w2.Spawn(1, "supply_stash", 300, 330);
        var b = Fly(w2, 60, 330, Facing.East);
        Issue(w2, CommandKind.Airdrop, b, target: s2.Id);
        w2.Step();
        b.Order = new Order(OrderKind.AttackGround, new Cell(335, 330), 0, 0);
        var (_, ev2) = Airdrop(w2, b);
        Assert.Equal(4, ev2.Count(e => e.Kind == LogisticsEventKind.PalletLost && e.Lost == 2500 && e.Store == 0));
        Assert.Equal(0, s2.Stock);
    }

    // An airdrop order is only for a finished drop zone of the player's and an aircraft with pallets; an attack order never drops
    // pallets (and an attack-ground order on the airlifter is refused: it has nothing that hurts the ground).
    [Fact]
    public void OnlyAnAirdropOrderDropsPallets()
    {
        var w = Duel(500);
        var c5 = Fly(w, 60, 330, Facing.East);
        var enemyStash = w.Spawn(2, "supply_stash", 300, 330);
        var site = w.Spawn(1, "facp", 300, 250);
        site.Construction = ConstructionState.Building;
        var depot = w.Spawn(1, "supply_depot", 300, 200); // a store without a drop zone
        foreach (var t in new[] { enemyStash.Id, site.Id, depot.Id })
        {
            Issue(w, CommandKind.Airdrop, c5, target: t);
            w.Step();
            Assert.Equal((OrderKind.Idle, 0u), (c5.Order.Kind, c5.AirdropAt));
        }
        Issue(w, CommandKind.AttackGround, c5, new Cell(300, 330));
        w.Step();
        Assert.Contains(w.OrderRefusals, r => r.Aircraft == c5.Id);
        Assert.Equal(OrderKind.Idle, c5.Order.Kind);
        w.Run(60 * Tps);
        Assert.Equal(4, c5.Charges[0]);
        Assert.Empty(w.BombReleases);
        // A jet's ordinary attack never takes the pallets' path, and the airlifter on Guard picks nothing: it has no mission block.
        Assert.Null(World.AirdropWeapon(w.Spawn(1, "Multirole 500 lb", 50, 50)));
    }

    // ---- Land and unload ----

    // Sent to the forward airbase it lands, taxis to a stand, unloads its 4 containers (7.5 s each) into the stock, takes on fuel
    // and flies home to the main air base, where it loads again for free and is held.
    [Fact]
    public void ItLandsAtTheForwardAirbaseUnloadsAndFliesHomeToLoad()
    {
        var w = Sentinel(500);
        var home = Base(w);
        var fwd = Forward(w);
        var core = w.Unit(fwd.Core)!;
        var c5 = Fly(w, 300, 250, Facing.East);
        Issue(w, CommandKind.Deliver, c5, target: fwd.Facility); // any piece names the field
        var ev = new List<(uint Tick, LogisticsEvent E)>();
        uint parkedAt = 0, departed = 0;
        for (var t = 0; t < 20000; t++)
        {
            w.Step();
            ev.AddRange(w.LogisticsEvents.Select(e => (w.Tick, e)));
            if (parkedAt == 0 && c5.AirOps is { Phase: FlightPhase.Parked } p && p.Airfield == fwd.Id)
            {
                parkedAt = w.Tick;
            }
            if (departed == 0 && w.AirfieldEvents.Any(e => e.Kind == AirfieldEventKind.Departed && e.Aircraft == c5.Id))
            {
                departed = w.Tick;
            }
            if (c5.AirOps is { Phase: FlightPhase.Parked } o && o.Airfield == home.Id && w.ServiceOf(c5).Serviced)
            {
                break;
            }
        }
        var unloads = ev.Where(x => x.E.Kind == LogisticsEventKind.Unloaded).ToList();
        output.WriteLine($"parked at the forward airbase t{parkedAt}; unloads at t{string.Join(", t", unloads.Select(x => x.Tick))}; departed t{departed}; " +
            $"home and loaded t{w.Tick}; stock {core.Stock}");
        Assert.True(parkedAt > 0);
        Assert.Equal(4, unloads.Count);
        Assert.All(unloads, x => Assert.Equal((2500, core.Id), (x.E.Amount, x.E.Store)));
        Assert.InRange((int)(unloads[^1].Tick - parkedAt), 29 * Tps, 31 * Tps); // about 30 s for all 4
        Assert.True(departed > unloads[^1].Tick);
        Assert.InRange(core.Stock, 17900, 18000); // 8,000 + 10,000 less the fuel it took (200 a tank, pro rata)
        Assert.Equal(home.Id, c5.AirOps!.Airfield);
        Assert.Equal((4, 10000), World.CargoAboard(c5));
        w.Run(30 * Tps);
        Assert.Equal(FlightPhase.Parked, c5.AirOps?.Phase); // held at home
    }

    // Unloading stops when the stock has no room for another container: the rest stays aboard and it flies home with it.
    [Fact]
    public void WhatDoesNotFitStaysAboard()
    {
        var w = Sentinel(500);
        Base(w);
        var fwd = Forward(w);
        var core = w.Unit(fwd.Core)!;
        core.Stock = 15000;
        var c5 = Fly(w, 300, 250, Facing.East);
        Issue(w, CommandKind.Deliver, c5, target: fwd.Core);
        Assert.True(RunUntil(w, () => c5.AirOps is { Phase: FlightPhase.Climb }, 20000) > 0);
        Assert.Equal(2, World.CargoAboard(c5).Pallets);
        Assert.InRange(core.Stock, 19900, 20000);
    }

    // ---- The field ammo point ----

    // Its side's ground units within 12 cells are rearmed out of its stock (the air defence vehicle's missiles at their depot cost
    // of 60, one each 4 s) until it runs dry; nothing that flies, nothing beyond 12 cells, nothing of the other side.
    [Fact]
    public void TheFieldAmmoPointRearmsFromItsStockAndStopsWhenEmpty()
    {
        var w = Sentinel(128);
        var facp = w.Spawn(1, "facp", 60, 60);
        facp.Stock = 200;
        var near = w.SpawnCrewed(1, "sam_vehicle", 66, 66).Vehicle;
        var far = w.SpawnCrewed(1, "sam_vehicle", 80, 60).Vehicle; // 20 cells
        var enemy = w.SpawnCrewed(2, "sam_vehicle", 54, 60).Vehicle;
        foreach (var v in new[] { near, far, enemy })
        {
            v.MutableCharges[0] = 0;
        }
        var watcher = w.Spawn(1, "watcher_drone", 60, 62);
        watcher.MutableCharges[0] = 0;
        w.Run(20 * Tps);
        output.WriteLine($"near {near.Charges[0]}, far {far.Charges[0]}, enemy {enemy.Charges[0]}, watcher {watcher.Charges[0]}, stock {facp.Stock}");
        Assert.Equal((3, 20), (near.Charges[0], facp.Stock)); // 200 pays for 3 at 60
        Assert.True(near.DepotStarved);
        Assert.Equal((0, 0, 0), (far.Charges[0], enemy.Charges[0], watcher.Charges[0]));
        facp.Stock = 0;
        w.Run(10 * Tps);
        Assert.Equal(3, near.Charges[0]); // empty: it does nothing
        // An empty ammo point gives no reload bonus and no repair either.
        near.HP = near.Def.MaxHp / 2;
        facp.Stock = 6000;
        w.Run(10 * Tps);
        Assert.Equal(near.Def.MaxHp / 2, near.HP);
        Assert.Equal(0, near.ReloadBonus);
    }

    // ---- The supply stash and the trucks ----

    // The stash stores (no rearm, no depot reloads); a cargo truck loads a full load there (out of its stock) and unloads it at a
    // field ammo point; full, standing at the stash, it never unloads back into it unless ordered; at the main air base it loads
    // free; at the forward airbase it loads out of the field's stock and unloads into it on an order.
    [Fact]
    public void TheStashStoresAndTrucksShuttleItsSupply()
    {
        var w = Sentinel(500);
        var af = Base(w);
        var stash = w.Spawn(1, "supply_stash", 100, 300);
        stash.Stock = 10000;
        var facp = w.Spawn(1, "facp", 160, 300);
        var sam = w.SpawnCrewed(1, "sam_vehicle", 104, 290).Vehicle;
        sam.MutableCharges[0] = 0;
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 115, 300).Vehicle;
        truck.Cargo = 0;
        Issue(w, CommandKind.LoadCargo, truck, target: stash.Id);
        Assert.True(RunUntil(w, () => truck.Cargo == 400, 30 * Tps) > 0);
        Assert.Equal((9600, OrderKind.Idle), (stash.Stock, truck.Order.Kind));
        w.Run(10 * Tps);
        Assert.Equal((9600, 400), (stash.Stock, truck.Cargo)); // full at the stash: no unload back into it
        Assert.Equal(0, sam.Charges[0]); // a store rearms nothing
        Issue(w, CommandKind.Unload, truck, target: facp.Id);
        Assert.True(RunUntil(w, () => truck.Cargo == 0, 60 * Tps) > 0);
        Assert.Equal(400, facp.Stock);
        // A full truck ordered to unload into the stash does; an empty one loads at the main air base for free.
        truck.Cargo = 400;
        Issue(w, CommandKind.Unload, truck, target: stash.Id);
        Assert.True(RunUntil(w, () => truck.Cargo == 0, 60 * Tps) > 0);
        Assert.Equal(10000, stash.Stock);
        Issue(w, CommandKind.LoadCargo, truck, target: af.Core);
        Assert.True(RunUntil(w, () => truck.Cargo == 400, 120 * Tps) > 0);
        Assert.Equal(new StoreStock(0, 0), w.StockOf(w.Unit(af.Core)!)); // unlimited: nothing drawn
        // An empty stash: the truck waits beside it until a load is there.
        var w2 = Sentinel(128);
        var s2 = w2.Spawn(1, "supply_stash", 40, 40);
        var t2 = w2.SpawnCrewed(1, "sentinel_cargo_truck", 55, 40).Vehicle;
        t2.Cargo = 0;
        Issue(w2, CommandKind.LoadCargo, t2, target: s2.Id);
        w2.Run(15 * Tps);
        Assert.Equal((0, OrderKind.LoadCargo), (t2.Cargo, t2.Order.Kind));
        s2.Stock = 400;
        Assert.True(RunUntil(w2, () => t2.Cargo == 400, 10 * Tps) > 0);
        Assert.Equal(0, s2.Stock);
        Assert.Contains(w2.LogisticsEvents, e => e.Kind == LogisticsEventKind.TruckLoaded && e.Unit == t2.Id && e.Store == s2.Id);
    }

    [Fact]
    public void TrucksLoadAndUnloadAtTheForwardAirbase()
    {
        var w = Sentinel(500);
        var fwd = Forward(w);
        var core = w.Unit(fwd.Core)!;
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 220, 440).Vehicle;
        truck.Cargo = 0;
        Issue(w, CommandKind.LoadCargo, truck, target: fwd.Core);
        Assert.True(RunUntil(w, () => truck.Cargo == 400, 60 * Tps) > 0);
        Assert.Equal(7600, core.Stock);
        Issue(w, CommandKind.Unload, truck, target: fwd.Core);
        Assert.True(RunUntil(w, () => truck.Cargo == 0, 60 * Tps) > 0);
        Assert.Equal(8000, core.Stock);
        // Refilled in the field garage, its load comes out of the stock too.
        Issue(w, CommandKind.Service, truck, target: fwd.Facility);
        Assert.True(RunUntil(w, () => truck.ServiceAt == 0 && truck.Cargo == 400, 60 * Tps) > 0);
        Assert.Equal(7600, core.Stock);
    }

    // ---- Losses ----

    // Shot down with its cargo aboard (4 air defence missile hits: 25% a hit), the cargo is lost: no store gets it.
    [Fact]
    public void ShotDownItsCargoIsLost()
    {
        var w = Sentinel(320);
        var stash = w.Spawn(1, "supply_stash", 300, 300);
        var c5 = Fly(w, 160, 160, Facing.East);
        c5.MutableSalvos[0] = 0; // no flares
        for (var i = 0; i < 4; i++)
        {
            w.SpawnCrewed(2, "sam_vehicle", 150 + 6 * i, 150);
        }
        Issue(w, CommandKind.Move, c5, new Cell(160, 160)); // it orbits over them
        var hp = new List<int>();
        var lost = new List<LogisticsEvent>();
        for (var t = 0; t < 9000 && c5.Alive; t++)
        {
            w.Step();
            hp.AddRange(w.AirBursts.Where(x => x.Target == c5.Id && x.Kind is AirBurstKind.Hit or AirBurstKind.Kill).Select(_ => c5.HP));
            lost.AddRange(w.LogisticsEvents.Where(e => e.Kind == LogisticsEventKind.CargoLost));
        }
        output.WriteLine("hits: " + string.Join(", ", hp));
        Assert.False(c5.Alive);
        Assert.Equal([1350, 900, 450, 0], hp);
        var e = Assert.Single(lost);
        Assert.Equal((c5.Id, 10000), (e.Unit, e.Lost));
        Assert.Equal(0, stash.Stock);
    }

    // ---- Determinism ----

    // A scripted logistics match: an airdrop on a stash, an airlifter delivering to the forward airbase, a truck shuttling from the
    // stash to an ammo point, jets rearming at the forward airbase; the commands in order or reversed; the hash every tick.
    private static List<ulong> Script(bool reversed)
    {
        var w = Sentinel(500, 7);
        Base(w);
        var fwd = Forward(w);
        var stash = w.Spawn(1, "supply_stash", 300, 300);
        stash.Stock = 2000;
        var facp = w.Spawn(1, "facp", 340, 300);
        var a = w.Spawn(1, Airlifter, 60, 300, Facing.East);
        var b = w.Spawn(1, Airlifter, 300, 250, Facing.East);
        var jet = w.Spawn(1, "Multirole 500 lb", 400, 330, Facing.West);
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 315, 300).Vehicle;
        truck.Cargo = 0;
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.Airdrop, Units = [a.Id], Target = stash.Id },
            new Command { Tick = 1, Player = 1, Seq = 2, Kind = CommandKind.Deliver, Units = [b.Id], Target = fwd.Core },
            new Command { Tick = 1, Player = 1, Seq = 3, Kind = CommandKind.LoadCargo, Units = [truck.Id], Target = stash.Id },
            new Command { Tick = 1, Player = 1, Seq = 4, Kind = CommandKind.Rearm, Units = [jet.Id], Target = fwd.Core },
            new Command { Tick = 400, Player = 1, Seq = 5, Kind = CommandKind.Unload, Units = [truck.Id], Target = facp.Id },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
        }
        return hashes;
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var a = Script(false);
        Assert.Equal(a, Script(false));
        Assert.Equal(a, Script(true));
    }
}
