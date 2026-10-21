using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// The main air base and the radar (H1, 2026-10-21; the game repo's docs/heavy-air-plan.md, section 2.1, and the
// shared layout H1-layout.md): the core placed as one unit lays the base (the built-in runway, the command building,
// the hangar with 4 heavy bays, the radar, 4 rows); rows and segments attach up to the cap; heavies (test fixtures
// of class L and XL: the A-10, AC-130 and B-52 types come in H2 to H4) home, land and park only in heavy bays, and
// jets and drones never take one; the hangar produces every air kind, a heavy only while a bay is free; the command
// building down stops production and heavy rearm (refuel goes on); the hangar is one target; airfields link; the
// radar (built in and the standalone site) tracks air far, spots ground nearer and clears the fog, and as rubble it
// is off.
public class MainAirBaseTests
{
    private const int N = 360;

    // A core at (40, 120) facing 0: the runway on y 120-135 from x 40 to 220 (9 segments, connectors at x 50, 130,
    // 210), the taxiway y 142-152, the apron y 152-164, the hangar x 44-148 / y 164-190 (bay i at x 44 + 26i), the
    // command building x 152-182 / y 166-192, the radar x 152-164 / y 194-206 (its centre (158, 200)), the rows'
    // column x 188-216 from y 154 (row r at 154 + 10r). Landings come from the east (hold point (260, 127.5)).
    private static readonly Cell CoreCell = new(40, 120);

    // Two made-up heavies (content only; the real ones are H2 to H4): an L (the A-10 type's size) and an XL (the B-52
    // type's at 0.8). They fly the drone law (a flight block) so they need no fixed-wing tuning; their runs fit only
    // the base's runway (the forward airfield could never take them anyway: classes S and M).
    private const string HeavyFixture = """
        {"archetypes":[
          {"id":"test_attacker","name":"Test attacker","glyph":"y","tags":["jet"],"locomotor":"flying","armor":"drone","hp":600,"speed":100,"turnRate":2,
           "vision":200,"lifetime":24000,"cost":0,"buildTime":200,
           "flight":{"height":1500,"climbRate":100,"launchHeight":10,"orbitRadius":300,"turnRate":30,"interceptible":"watcher","reserve":300,"recoverRange":40,"turnaround":600},
           "aircraft":{"sizeClass":"L","span":88,"length":80,"landing":"runway","minLandRun":900,"minTakeoffRun":700,
             "approach":{"fixDistance":300,"fixHeight":150,"touchdown":100,"flare":50,"rolloutDecel":10,"brake":20},
             "speeds":{"approach":60,"touchdown":55,"taxi":30,"turn":15,"pushback":5,"takeoff":80,"takeoffAccel":15},
             "service":{"refuelTicks":600,"rearmTicksPerRound":200,"repairHpPerSecond":15},
             "groundVision":60,"groundEye":30,"holdReserve":1200,"sortie":true}},
          {"id":"test_bomber","name":"Test bomber","glyph":"y","tags":["jet"],"locomotor":"flying","armor":"drone","hp":1200,"speed":100,"turnRate":2,
           "vision":200,"lifetime":24000,"cost":0,"buildTime":300,
           "flight":{"height":1500,"climbRate":100,"launchHeight":10,"orbitRadius":300,"turnRate":30,"interceptible":"watcher","reserve":300,"recoverRange":40,"turnaround":600},
           "aircraft":{"sizeClass":"XL","span":225,"length":195,"landing":"runway","minLandRun":1400,"minTakeoffRun":1200,
             "approach":{"fixDistance":300,"fixHeight":150,"touchdown":100,"flare":50,"rolloutDecel":10,"brake":20},
             "speeds":{"approach":60,"touchdown":55,"taxi":30,"turn":15,"pushback":5,"takeoff":80,"takeoffAccel":15},
             "service":{"refuelTicks":600,"rearmTicksPerRound":200,"repairHpPerSecond":15},
             "groundVision":60,"groundEye":30,"holdReserve":1200,"sortie":true}},
          {"id":"test_drone","name":"Test drone","glyph":"y","tags":["drone"],"locomotor":"flying","armor":"drone","hp":200,"speed":80,"turnRate":2,
           "vision":200,"lifetime":24000,"cost":0,"buildTime":20,
           "flight":{"height":1500,"climbRate":80,"launchHeight":10,"orbitRadius":250,"turnRate":30,"interceptible":"watcher","reserve":300,"recoverRange":40,"turnaround":600},
           "aircraft":{"sizeClass":"S","span":71,"length":40,"landing":"runway","minLandRun":360,"minTakeoffRun":240,
             "approach":{"fixDistance":300,"fixHeight":150,"touchdown":80,"flare":40,"rolloutDecel":5,"brake":10},
             "speeds":{"approach":50,"touchdown":45,"taxi":30,"turn":15,"pushback":5,"takeoff":60,"takeoffAccel":10},
             "service":{"refuelTicks":400,"rearmTicksPerRound":300,"repairHpPerSecond":10},
             "groundVision":60,"groundEye":25,"holdReserve":1200,"sortie":true}}
        ]}
        """;

    private static readonly Lazy<ContentSet> HeavyContent = new(() =>
    {
        var sources = TestContent.MvpSources();
        sources.Add(new ContentSource("fixtures/heavies.json", HeavyFixture));
        var i = sources.FindIndex(s => s.Name.EndsWith("factions/sentinel.json", StringComparison.Ordinal));
        sources[i] = new ContentSource(sources[i].Name, sources[i].Json.Replace("\"watcher_drone\",", "\"watcher_drone\", \"test_attacker\", \"test_bomber\", \"test_drone\",", StringComparison.Ordinal));
        return ContentLoader.Load(sources);
    });

    // HeavyWorld is the Sentinel lobby (player 1 Sentinel, player 2 Resistance) on the MVP content plus the heavies.
    private static World HeavyWorld(int n = N, bool fog = false, ulong seed = 1, bool sandbox = false)
    {
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(HeavyContent.Value, seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })], Fog = fog };
        if (sandbox)
        {
            setup = setup with { Scenario = Scenarios.Scenario.SandboxName };
        }
        return new World(new Grid(n, n), MatchCompiler.Compile(HeavyContent.Value, setup));
    }

    private static UnitDef Def(World w, string id, byte player = 1) => w.Player(player)!.Setup.Find(w.Content, id)!;

    private static Airfield Base(World w, Cell? at = null, int facing = 0, IReadOnlyList<(int, int)>? rows = null, int west = 0, int east = 0) =>
        w.SpawnAirBase(Def(w, "airbase_core"), at ?? CoreCell, facing, rows ?? [], west, east) ?? throw new InvalidOperationException("no base");

    private static Unit Homebound(World w, string kind, int x, int y)
    {
        var d = w.Spawn(1, kind, x, y);
        d.Homing = true;
        return d;
    }

    private static int RunUntil(World w, Func<bool> done, int max = 8000)
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

    private static List<AirfieldStation> Stations(Airfield af) => [.. af.Rows.SelectMany(r => r.Stations)];

    private static AirfieldStation StationOf(Airfield af, uint unit) => Stations(af).Single(s => s.Unit == unit);

    private static void Produce(World w, Unit hangar, string unit) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [hangar.Id], Unit = unit });

    // ---- Content ----

    [Fact]
    public void TheMainAirBaseIsData()
    {
        var c = TestContent.Mvp;
        var t = c.AirfieldType("main_air_base")!;
        Assert.Equal(AircraftClass.XL, t.MaxClass);
        Assert.True(t.Serves(AircraftClass.S) && t.Serves(AircraftClass.L) && t.Serves(AircraftClass.XL));
        Assert.False(c.AirfieldType("forward_airfield")!.Serves(AircraftClass.L)); // a forward airfield never serves L or XL
        Assert.Equal(["airbase_core", "airbase_command", "airbase_hangar", "airbase_radar", "airbase_bay", "airbase_runway", "airfield_row", "airfield_shelter", "airfield_stand"],
            new[] { t.Core, t.Facility, t.Hangar, t.Radar, t.Heavy, t.Segment, t.Row, t.Loading, t.Normal }.Select(i => c.Archetypes[i].Id));
        Assert.Equal((20, 15, 7, 10, 32), (t.SegmentCells, t.RunwayCells, t.GrassCells, t.TaxiwayCells, t.SegmentDepth));
        Assert.Equal((9, 4), (t.MinSegments, t.ConnectorEvery));
        Assert.Equal([true, false, false, false, true, false, false, false, true], Enumerable.Range(0, 9).Select(t.HasConnector));
        var core = t.CoreLayout!;
        Assert.Equal((9, (112, 46), (4, 44), (112, 74), (32, 44), 4), (core.Segments, core.Facility, core.Hangar, core.Radar, core.Apron, core.Rows));
        Assert.Equal([0, 26, 52, 78], core.Bays.Select(b => b.X));
        Assert.All(core.Bays, b => Assert.Equal((26, 26, StationKind.Heavy), (b.W, b.H, b.Slot.Kind)));
        Assert.Equal(((148, 34), 1, 12), (t.RowOrigin, t.MaxColumns, t.MaxRows));
        Assert.True(t.RefuelWithoutFacility);
        Assert.Equal(40, t.LinkCells);
        var coreA = c.Archetype("airbase_core")!;
        Assert.Equal((180, 86, 2400), (coreA.FootprintW, coreA.FootprintH, coreA.BuildTime)); // 120 s (the owner's placeholder)
        var bay = c.Archetype("airbase_bay")!.Airfield!;
        Assert.Equal((AircraftClass.L, AircraftClass.XL), (bay.MinClass, bay.MaxClass));
        Assert.Equal(AircraftClass.M, c.Archetype("airfield_shelter")!.Airfield!.MaxClass);
        Assert.Equal(AircraftClass.M, c.Archetype("airfield_stand")!.Airfield!.MaxClass);
        var hangar = c.Archetype("airbase_hangar")!;
        Assert.True(hangar.Production is { IntoStation: true, Bays: 2, QueueMax: 10 });
        // The radar's three ranges (the owner's for the base, the defaults for the site), in cells.
        var radar = c.Archetype("airbase_radar")!;
        var site = c.Archetype("radar_site")!;
        Assert.Equal((Fixed.FromInt(400), Fixed.FromInt(80), Fixed.FromInt(60)), (radar.AirVision, radar.GroundRadar, radar.Vision)); // air 400 (owner 2026-10-21; was 300)
        Assert.Equal((Fixed.FromInt(200), Fixed.FromInt(60), Fixed.FromInt(40), 600), (site.AirVision, site.GroundRadar, site.Vision, site.BuildTime)); // 30 s
        Assert.Equal((site.FootprintW, site.FootprintH), (radar.FootprintW, radar.FootprintH)); // one radar model
        var sentinel = c.Factions.Single(f => f.Id == "sentinel");
        foreach (var id in new[] { "airbase_core", "airbase_command", "airbase_hangar", "airbase_bay", "airbase_radar", "airbase_runway", "radar_site" })
        {
            Assert.True(sentinel.Archetypes[c.Archetypes.Single(a => a.Id == id).Index], id);
        }
    }

    // ---- Placement and the core ----

    [Fact]
    public void TheCoreIsOneSiteAndLaysTheBaseWhenFinished()
    {
        var w = HeavyWorld();
        var dozer = w.Spawn(1, "bulldozer", 30, 125);
        w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.Build, Units = [dozer.Id], Cell = CoreCell, Unit = "airbase_core" });
        w.Step();
        var af = Assert.Single(w.Airfields);
        var site = w.Unit(af.Core)!;
        Assert.Equal(af.Id, site.Id);
        Assert.False(site.IsBuilt);
        Assert.Equal((0u, 0u, 0u, false), (af.Facility, af.Hangar, af.Radar, af.HasRunway)); // nothing of the base works yet
        Assert.Empty(af.Rows);
        Assert.Equal(CoreCell, World.FootprintOrigin(site));
        var intruder = w.Spawn(1, "worker", 60, 175); // walked onto the hangar's slot while the core was a site
        var t = RunUntil(w, () => site.IsBuilt, 3000);
        Assert.InRange(t, 2300, 2500); // 120 s at the bulldozer's rate (it drives into reach first)
        w.Step();
        w.Run(60);
        var ho = World.FootprintOrigin(w.Unit(af.Hangar)!);
        var at = Cell.Of(intruder.Pos);
        Assert.False(at.X >= ho.X && at.X < ho.X + 104 && at.Y >= ho.Y && at.Y < ho.Y + 26, $"pushed out of the hangar: at {at}");
        // The pieces on their slots, finished.
        Assert.Equal((0, 8, 9), (af.West, af.East, af.Segments.Count));
        Assert.All(af.Segments, id => Assert.True(w.Unit(id) is { IsBuilt: true } s && s.Def.Archetype.Id == "airbase_runway"));
        var command = w.Unit(af.Facility)!;
        var hangar = w.Unit(af.Hangar)!;
        var radar = w.Unit(af.Radar)!;
        Assert.Equal(("airbase_command", "airbase_hangar", "airbase_radar"), (command.Def.Archetype.Id, hangar.Def.Archetype.Id, radar.Def.Archetype.Id));
        Assert.Equal(new Cell(44, 164), World.FootprintOrigin(hangar));
        Assert.Equal(new Cell(152, 166), World.FootprintOrigin(command));
        Assert.Equal(new Cell(152, 194), World.FootprintOrigin(radar));
        Assert.Equal(new Cell(44, 164), w.AirfieldHangarCell(af));
        Assert.Equal(new Cell(152, 194), w.AirfieldRadarCell(af));
        Assert.Equal(new Cell(152, 166), w.AirfieldFacilityCell(af));
        var bays = World.HeavyBays(af);
        Assert.Equal([0, 1, 2, 3], bays.Select(b => b.Bay));
        Assert.All(bays, b => Assert.Equal(StationKind.Heavy, b.Station.Kind));
        Assert.Equal([44, 70, 96, 122], bays.Select(b => World.FootprintOrigin(w.Unit(b.Station.Unit)!).X));
        var rows = af.Rows.Where(r => !r.IsBay).ToList();
        Assert.Equal([(0, 0), (0, 1), (0, 2), (0, 3)], rows.Select(r => (r.Col, r.Row)));
        Assert.Equal(new Cell(188, 154), World.AirfieldRowCell(af, 0, 0));
        Assert.Equal(8, rows.SelectMany(r => r.Stations).Count(s => s.Unit != 0));
        Assert.Equal(4, Stations(af).Count(s => s.Kind == StationKind.Loading));
        Assert.Equal(4, Stations(af).Count(s => s.Kind == StationKind.Heavy));
        // The taxi graph: three runway connectors (x 10, 90, 170), a lane node per bay on the apron, stops in the bays.
        var g = w.AirfieldTaxiGraph(af);
        Assert.Equal([100, 900, 1700], g.Nodes.Where(n => n.Kind == TaxiNodeKind.RunwayConnector).Select(n => n.Local.X));
        Assert.Equal([(170, 380), (430, 380), (690, 380), (950, 380)], g.Nodes.Where(n => n.Kind == TaxiNodeKind.Lane && n.Local.Y == 380).Select(n => n.Local).OrderBy(p => p.X));
        Assert.Equal([(170, 570), (430, 570), (690, 570), (950, 570)], bays.Select(b => g.Nodes.Single(n => n.Station == b.Station.Unit).Local));
    }

    [Fact]
    public void TheCoreNeedsRoomAndItsCorridors()
    {
        var w = HeavyWorld();
        var core = Def(w, "airbase_core");
        var dozer = w.Spawn(1, "bulldozer", 30, 125);
        Assert.True(w.CanPlace(core, CoreCell, 0, [dozer]));
        Assert.False(w.CanPlace(core, new Cell(10, 120), 0, [dozer]));  // the west corridor (30 cells) runs off the map
        Assert.False(w.CanPlace(core, new Cell(160, 120), 0, [dozer])); // the core runs off the map
        Assert.False(w.CanPlace(core, CoreCell, 4, [dozer]));           // quarter turns only
        w.Spawn(2, "technical", 60, 175); // where the hangar will stand
        Assert.False(w.CanPlace(core, CoreCell, 0, [dozer]));
        // A forward airfield's facility can't found a base, nor a command building on its own.
        Assert.False(w.CanPlace(Def(w, "airbase_command"), new Cell(100, 40), 0, [dozer]));
    }

    [Fact]
    public void TheSandboxSpawnsAFinishedBaseAndARadarSite()
    {
        var w = HeavyWorld(sandbox: true);
        w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "airbase_core", Cell = CoreCell });
        w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "radar_site", Cell = new Cell(300, 40) });
        w.Step();
        var af = Assert.Single(w.Airfields);
        Assert.True(w.Unit(af.Core) is { IsBuilt: true });
        Assert.Equal(4, World.HeavyBays(af).Count);
        Assert.True(w.Units.Single(u => u.Def.Archetype.Id == "radar_site").IsBuilt);
    }

    [Fact]
    public void ARotatedBaseTurnsEveryPiece()
    {
        var w = HeavyWorld(400);
        var af = Base(w, new Cell(150, 40), 8); // a quarter turn: the runway runs down the map (+x local is +y on the map)
        var hangar = w.Unit(af.Hangar)!;
        Assert.Equal((26, 104), (World.FootW(hangar), World.FootH(hangar)));
        var core = w.Unit(af.Core)!;
        Assert.Equal((86, 180), (World.FootW(core), World.FootH(core)));
        // Every piece's centre is its slot's centre turned with the field.
        Assert.Equal(World.AirfieldPoint(af, 560, 570), hangar.Pos);
        Assert.Equal(World.AirfieldPoint(af, 1270, 590), w.Unit(af.Facility)!.Pos);
        Assert.Equal(World.AirfieldPoint(af, 1180, 800), w.Unit(af.Radar)!.Pos);
        foreach (var (_, st) in World.HeavyBays(af))
        {
            var bay = w.Unit(st.Unit)!;
            var o = World.FootprintOrigin(bay);
            var ho = World.FootprintOrigin(hangar);
            Assert.True(o.X >= ho.X && o.Y >= ho.Y && o.X + World.FootW(bay) <= ho.X + 26 && o.Y + World.FootH(bay) <= ho.Y + 104, "a bay lies in its hangar");
        }
        // A heavy lands there all the same and parks in a bay.
        var a = Homebound(w, "test_attacker", 200, 330);
        Assert.True(RunUntil(w, () => a.AirOps is { Phase: FlightPhase.Parked }, 9000) > 0, "it lands at the turned base");
        Assert.Equal(StationKind.Heavy, StationOf(af, a.AirOps!.Station).Kind);
    }

    [Fact]
    public void RowsAndSegmentsAttachUpToTheCap()
    {
        var w = HeavyWorld(420);
        var extra = Enumerable.Range(4, 8).Select(r => (0, r)).ToList();
        var af = Base(w, new Cell(70, 120), rows: extra, west: 1, east: 4); // room for a west segment and its corridor
        Assert.Equal(12, af.Rows.Count(r => !r.IsBay)); // the 4 built-in rows and 8 more: the cap
        Assert.Equal((-1, 12), (af.West, af.East));
        Assert.Equal("airbase_runway", w.Unit(af.Segments[0])!.Def.Archetype.Id);
        var g = w.AirfieldTaxiGraph(af);
        Assert.Equal([100, 900, 1700, 2500], g.Nodes.Where(n => n.Kind == TaxiNodeKind.RunwayConnector).Select(n => n.Local.X)); // a connector every 4th: k 0, 4, 8, 12
        var dozer = w.Spawn(1, "bulldozer", 30, 300);
        Assert.False(w.CanPlace(Def(w, "airfield_row"), World.AirfieldRowCell(af, 0, 12), 0, [dozer])); // over the cap of 12
        Assert.False(w.CanPlace(Def(w, "airfield_row"), World.AirfieldRowCell(af, 1, 0), 0, [dozer]));  // one column only
        Assert.Throws<ArgumentException>(() => Base(HeavyWorld(420), new Cell(70, 120), rows: [(0, 4), (0, 6)])); // a row needs the one below it
    }

    // ---- Heavy bays ----

    [Fact]
    public void HeaviesTakeOnlyHeavyBaysAndJetsAndDronesNever()
    {
        var w = HeavyWorld(400);
        var af = Base(w);
        var forward = w.SpawnAirfield(Def(w, "airfield_facility"), new Cell(60, 300), 0, [(0, 0)])!;
        var c = w.Content;
        var heavy = Stations(af).Where(s => s.Kind == StationKind.Heavy).ToList();
        var others = Stations(af).Where(s => s.Kind != StationKind.Heavy).ToList();
        foreach (var id in new[] { "test_attacker", "test_bomber" })
        {
            var ac = c.Archetype(id)!.Aircraft!;
            Assert.Equal(heavy, w.StationsFor(af, ac));
            Assert.Empty(w.StationsFor(forward, ac)); // a forward airfield never serves L or XL
        }
        foreach (var id in new[] { "watcher_drone", "overseer_drone", "multirole_jet", "fighter_jet" })
        {
            Assert.Equal(others, w.StationsFor(af, c.Archetype(id)!.Aircraft!)); // jets and drones never take a heavy bay
        }
    }

    [Fact]
    public void AHeavyHomesToTheBaseAndParksInABayAWatcherNeverDoes()
    {
        var w = HeavyWorld(400);
        var af = Base(w);
        var forward = w.SpawnAirfield(Def(w, "airfield_facility"), new Cell(260, 300), 0, [(0, 0)])!; // nearer to them both
        var heavy = Homebound(w, "test_bomber", 330, 330);
        var watcher = Homebound(w, "watcher_drone", 330, 335);
        w.Step();
        Assert.Equal(af.Id, heavy.AirOps!.Airfield);      // only the base takes an XL
        Assert.Equal(forward.Id, watcher.AirOps!.Airfield); // the nearest that takes it
        Assert.True(RunUntil(w, () => heavy.AirOps is { Phase: FlightPhase.Parked }, 9000) > 0, "the heavy lands and parks");
        var st = StationOf(af, heavy.AirOps!.Station);
        Assert.Equal(StationKind.Heavy, st.Kind);
        Assert.Equal(heavy.Id, st.Occupant);
        // A Watcher sent to the base parks at a row's station, never in a bay (three bays are free).
        var second = w.Spawn(1, "watcher_drone", 330, 335);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.ReturnToBase, Units = [second.Id], Target = af.Facility });
        Assert.True(RunUntil(w, () => second.AirOps is { Phase: FlightPhase.Parked } wo && wo.Airfield == af.Id, 9000) > 0, "the Watcher lands at the base");
        Assert.NotEqual(StationKind.Heavy, StationOf(af, second.AirOps!.Station).Kind);
    }

    // ---- Production ----

    [Fact]
    public void TheHangarProducesEveryAirKindIntoItsStations()
    {
        var w = HeavyWorld();
        var af = Base(w);
        var hangar = w.Unit(af.Hangar)!;
        foreach (var unit in new[] { "Multirole 500 lb", "fighter_jet", "watcher_drone", "overseer_drone", "test_attacker", "test_bomber" })
        {
            Produce(w, hangar, unit);
        }
        w.Step();
        Assert.Equal(6, hangar.ProductionQueue.Count + hangar.Bays.Count(b => b.Def >= 0));
        var made = new List<Unit>();
        RunUntil(w, () =>
        {
            made.Clear();
            made.AddRange(w.Units.Where(u => u.Owner == 1 && u.Def.Archetype.Aircraft is not null));
            return made.Count == 6;
        }, 6000);
        Assert.Equal(6, made.Count);
        foreach (var u in made)
        {
            Assert.Equal(FlightPhase.Parked, u.AirOps!.Phase);
            var kind = StationOf(af, u.AirOps.Station).Kind;
            Assert.Equal(u.Def.Archetype.Aircraft!.SizeClass >= AircraftClass.L, kind == StationKind.Heavy); // heavies at a bay, the rest at a row
            Assert.True(u.AirOps.Hold, "held until its first order");
        }
        Assert.Equal(StationKind.Normal, StationOf(af, made.First(u => u.Def.Archetype.Id == "multirole_jet").AirOps!.Station).Kind); // an open stand first
        // The forward airfield's facility does not build heavies (it serves S and M).
        var forward = w.SpawnAirfield(Def(w, "airfield_facility"), new Cell(60, 300), 0, [(0, 0)])!;
        var fac = w.Unit(forward.Facility)!;
        Produce(w, fac, "test_attacker");
        w.Step();
        Assert.Empty(fac.ProductionQueue);
    }

    [Fact]
    public void AHeavyIsQueuedOnlyWhileABayIsFree()
    {
        var w = HeavyWorld();
        var af = Base(w);
        var hangar = w.Unit(af.Hangar)!;
        for (var i = 0; i < 5; i++)
        {
            Produce(w, hangar, "test_attacker");
        }
        Produce(w, hangar, "Multirole 500 lb"); // jets are not limited by the bays
        w.Step();
        Assert.Equal(5, hangar.ProductionQueue.Count + hangar.Bays.Count(b => b.Def >= 0)); // 4 heavies (4 bays) and the jet
        Assert.Equal(4, w.FreeHeavyBays(af));
        RunUntil(w, () => w.FreeHeavyBays(af) == 0, 4000);
        Assert.Equal(0, w.FreeHeavyBays(af));
        RunUntil(w, () => hangar.ProductionQueue.Count == 0 && hangar.Bays.All(b => b.Def < 0), 2000);
        Produce(w, hangar, "test_bomber"); // every bay full: refused
        w.Step();
        Assert.Empty(hangar.ProductionQueue);
        Assert.All(hangar.Bays, b => Assert.True(b.Def < 0));
        // One heavy leaves its bay: one more may be queued (and only one).
        var parked = w.Units.First(u => u.Def.Archetype.Id == "test_attacker");
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [parked.Id], Cell = new Cell(200, 60) });
        RunUntil(w, () => w.FreeHeavyBays(af) == 1, 2000);
        Produce(w, hangar, "test_bomber");
        Produce(w, hangar, "test_bomber");
        w.Step();
        Assert.Equal(1, hangar.ProductionQueue.Count + hangar.Bays.Count(b => b.Def >= 0));
    }

    // ---- The command building and the hangar as targets ----

    [Fact]
    public void TheCommandBuildingDownStopsProductionAndHeavyRearmButNotRefuel()
    {
        var w = HeavyWorld();
        var af = Base(w);
        var hangar = w.Unit(af.Hangar)!;
        var command = w.Unit(af.Facility)!;
        // A heavy parked in a bay on a Rearm visit, short of fuel, ammunition-less (it has none) and hurt.
        Produce(w, hangar, "test_attacker");
        RunUntil(w, () => w.Units.Any(u => u.Def.Archetype.Id == "test_attacker"), 1000);
        var heavy = w.Units.Single(u => u.Def.Archetype.Id == "test_attacker");
        heavy.Lifetime = heavy.Tank / 2;
        heavy.HP = 300;
        heavy.HomeReason = HomeReason.Rearm;
        heavy.AirOps!.Hold = true;
        Produce(w, hangar, "Multirole 500 lb");
        w.Run(20);
        var left = hangar.Bays.Single(b => b.Def >= 0).Left;
        command.HP = 0;
        w.Step();
        Assert.True(command.Rubble, "the command building is rubble");
        var bayLeft = hangar.Bays.Single(b => b.Def >= 0).Left;
        var fuel = heavy.Lifetime;
        var hp = heavy.HP;
        w.Run(200);
        Assert.Equal(bayLeft, hangar.Bays.Single(b => b.Def >= 0).Left); // production stops
        Assert.True(bayLeft <= left);
        var service = w.ServiceOf(heavy);
        Assert.Equal((true, 100, 0, 0), (service.Working, service.RefuelPercent, service.RearmPercent, service.RepairPercent)); // refuel only
        Assert.True(heavy.Lifetime > fuel, "it still refuels");
        Assert.Equal(hp, heavy.HP); // no repair
        // A bulldozer repairs the command building: production and the full service go on.
        var dozer = w.Spawn(1, "bulldozer", 167, 196);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Repair, Units = [dozer.Id], Target = command.Id });
        Assert.True(RunUntil(w, () => !command.Rubble, 2000) > 0, "repaired");
        w.Run(20);
        Assert.True(hangar.Bays.All(b => b.Def < 0 || b.Left < bayLeft), "production goes on");
        Assert.Equal(200, w.ServiceOf(heavy).RepairPercent);
        Assert.True(RunUntil(w, () => heavy.HP == heavy.Def.MaxHp, 2000) > 0, "repaired at 2x");
    }

    [Fact]
    public void TheHangarIsOneTargetItsRubbleClosesEveryBay()
    {
        var w = HeavyWorld();
        var af = Base(w);
        var hangar = w.Unit(af.Hangar)!;
        Produce(w, hangar, "test_attacker");
        RunUntil(w, () => w.Units.Any(u => u.Def.Archetype.Id == "test_attacker"), 1000);
        var heavy = w.Units.Single(u => u.Def.Archetype.Id == "test_attacker");
        var ac = heavy.Def.Archetype.Aircraft!;
        Assert.Equal(4, w.StationsFor(af, ac).Count);
        hangar.HP = 0;
        w.Step();
        Assert.True(hangar.Rubble);
        Assert.Empty(w.StationsFor(af, ac));               // all 4 bays closed
        Assert.Equal(0, w.FreeHeavyBays(af));
        Assert.Equal(600 - 240, heavy.HP);                 // the collapse: 40% of its max HP
        Assert.Equal(8, w.StationsFor(af, w.Content.Archetype("multirole_jet")!.Aircraft!).Count); // the rows are not the hangar
        Produce(w, hangar, "Multirole 500 lb");
        w.Step();
        w.Run(10);
        Assert.Single(hangar.ProductionQueue); // rubble produces nothing: the order waits
        Assert.All(hangar.Bays, b => Assert.True(b.Def < 0));
        // Hit again it is gone: the bays go with it; rebuilt on its slot, they come back.
        hangar.HP = 0;
        w.Step();
        Assert.Null(w.Unit(hangar.Id));
        Assert.Equal(0u, af.Hangar);
        Assert.Empty(World.HeavyBays(af));
        Assert.DoesNotContain(w.Units, u => u.Def.Archetype.Id == "airbase_bay");
        var dozer = w.Spawn(1, "bulldozer", 40, 175);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [dozer.Id], Cell = w.AirfieldHangarCell(af), Unit = "airbase_hangar" });
        w.Step();
        Assert.NotEqual(0u, af.Hangar);
        Assert.True(RunUntil(w, () => w.Unit(af.Hangar) is { IsBuilt: true }, 3000) > 0, "rebuilt");
        w.Step();
        Assert.Equal(4, World.HeavyBays(af).Count);
        Assert.Equal(4, w.FreeHeavyBays(af) + (StationsFor(af, heavy) ? 1 : 0));
    }

    private static bool StationsFor(Airfield af, Unit heavy) => af.Rows.Any(r => r.IsBay && r.Stations[0].Occupant == heavy.Id);

    // ---- Linking ----

    [Fact]
    public void AForwardAirfieldNearTheApronIsLinkedAndServiceIsShared()
    {
        var w = HeavyWorld(420);
        var af = Base(w);
        // A facility at (240, 152): 20 cells east of the apron band (x 40-220, y 152-164).
        var near = w.SpawnAirfield(Def(w, "airfield_facility"), new Cell(240, 152), 0, [(0, 0)])!;
        var far = w.SpawnAirfield(Def(w, "airfield_facility"), new Cell(60, 300), 0, [(0, 0)])!; // 136 cells off
        Assert.Equal([near], w.AirfieldLinks(af));
        Assert.Equal([af], w.AirfieldLinks(near));
        Assert.Empty(w.AirfieldLinks(far));
        // Shared service: a Watcher of the base's sent to rearm at the linked field lands, rearms and is repaired there;
        // a heavy sent there goes to the base instead (the forward field can't take it).
        var watcher = Homebound(w, "watcher_drone", 300, 60);
        watcher.Homing = false;
        watcher.HP = 120;
        var heavy = Homebound(w, "test_attacker", 300, 70);
        heavy.Homing = false;
        w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.Rearm, Units = [watcher.Id, heavy.Id], Target = near.Facility });
        w.Step();
        Assert.Equal(near.Id, watcher.AirOps!.Airfield);
        Assert.Equal(af.Id, heavy.AirOps!.Airfield);
        Assert.True(RunUntil(w, () => watcher.HP == watcher.Def.MaxHp && watcher.AirOps is { Phase: FlightPhase.Parked } or { Phase: FlightPhase.PushBack }, 9000) > 0,
            "serviced at the linked field");
    }

    [Fact]
    public void ALinkedForwardAirfieldJoinsTheBasesTaxiGraph()
    {
        var w = HeavyWorld(420);
        var af = Base(w);
        var near = w.SpawnAirfield(Def(w, "airfield_facility"), new Cell(240, 152), 0, [(0, 0)])!;
        var g = w.AirfieldTaxiGraph(af);
        var nearStations = near.Rows.SelectMany(r => r.Stations).Select(s => s.Unit).ToList();
        Assert.All(nearStations, id => Assert.Contains(g.Nodes, n => n.Kind == TaxiNodeKind.Stop && n.Station == id));
        Assert.DoesNotContain(w.AirfieldTaxiGraph(near).Nodes, n => n.Station == af.Rows[0].Stations[0].Unit); // one way: the base's graph
        var link = Assert.Single(w.AirfieldLinkTaxiways(af));
        Assert.Same(near, link.Linked);
        Assert.True((link.From - link.To).Len() < Fixed.FromInt(60), "a short joining taxiway"); // (261, 148) to the base's easternmost connector (210, 147)
        var again = w.AirfieldTaxiGraph(af); // derived: the same pieces give the same graph
        Assert.Equal(g.Nodes, again.Nodes);
        Assert.Equal(g.Edges, again.Edges);
        // The hangar fills the base's 8 S-M stations; the 9th aircraft parks at the linked field (still the base's).
        var hangar = w.Unit(af.Hangar)!;
        for (var i = 0; i < 9; i++)
        {
            Produce(w, hangar, "test_drone");
        }
        Assert.True(RunUntil(w, () => w.Units.Count(u => u.Def.Archetype.Id == "test_drone") == 9, 2000) > 0, "nine built");
        var atNear = w.Units.Where(u => u.Def.Archetype.Id == "test_drone" && nearStations.Contains(u.AirOps!.Station)).ToList();
        Assert.Single(atNear);
        Assert.Equal(af.Id, atNear[0].AirOps!.Airfield);
        // A Watcher homing to the base (its own stations full, the link's one free) lands there, taxis over the link and
        // parks at the linked field; ordered off, it taxis back and takes off from the base's runway.
        var watcher = Homebound(w, "watcher_drone", 230, 40);
        w.Step();
        Assert.Equal(af.Id, watcher.AirOps!.Airfield);
        Assert.Contains(watcher.AirOps.Station, nearStations);
        Assert.True(RunUntil(w, () => watcher.AirOps is { Phase: FlightPhase.Parked }, 9000) > 0, "parked over the link");
        Assert.Contains(watcher.AirOps!.Station, nearStations);
        Assert.Equal(af.Id, watcher.AirOps.Airfield);
        var st = near.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == watcher.AirOps.Station);
        Assert.Equal(watcher.Id, st.Occupant);
        w.Run(5);
        Assert.Equal(watcher.Id, st.Occupant); // the linked field keeps the base's aircraft on its station
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [watcher.Id], Cell = new Cell(100, 40) });
        Assert.True(RunUntil(w, () => watcher.AirOps is { Phase: FlightPhase.TakeoffRoll }, 9000) > 0, "it takes off");
        Assert.Equal(af.Id, watcher.AirOps!.Airfield);
        var (lx, ly) = World.AirfieldMapToLocal(af, watcher.Pos);
        Assert.InRange(ly, 0, 15 * Fixed.OneRaw); // on the base's runway strip
        Assert.InRange(lx, 0, 180 * Fixed.OneRaw);
    }

    // ---- The radar ----

    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    // OnTerrain attaches a terrain to w: flat but for a ridge (corner height `ridge` raw) on the corner column x =
    // ridgeX, and forest density 3 on the cells forest picks.
    private static void OnTerrain(World w, int n, int ridgeX, int ridge, Func<int, int, bool> forest)
    {
        var corners = new int[(n + 1) * (n + 1)];
        var flags = new byte[n * n];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = x == ridgeX ? ridge : 0;
                if (x < n && y < n && forest(x, y))
                {
                    flags[y * n + x] = 3;
                }
            }
        }
        w.AttachTerrain(new TerrainMap(n, n, corners, flags, Rules));
    }

    [Fact]
    public void TheRadarSiteTracksAirFarSpotsGroundNearerAndClearsTheFog()
    {
        const int n = 500;
        var w = HeavyWorld(n, fog: true);
        // A 40 m ridge 20 cells east of the site (x 270); woods round (250, 300).
        OnTerrain(w, n, 270, 40 * 512, (x, y) => Math.Abs(x - 250) <= 3 && Math.Abs(y - 300) <= 3);
        var site = w.Spawn(1, "radar_site", 244, 244); // footprint 244-256: its centre (250, 250)
        Assert.True(site.IsBuilt);
        var behindRidge = w.Spawn(2, "technical", 300, 250); // 50 cells, behind the ridge: the ground radar (60) sees it
        var farVehicle = w.Spawn(2, "technical", 250, 315);  // 65 cells: beyond the ground radar
        var inWoods = w.Spawn(2, "Rifleman", 250, 300);      // 50 cells, in the woods: hidden
        var inOpen = w.Spawn(2, "Rifleman", 200, 250);       // 50 cells, in the open: spotted
        var near = w.Spawn(2, "scout_drone", 250, 100);      // 150 cells out
        var farAir = w.Spawn(2, "scout_drone", 250, 15);     // 235 cells out
        near.Altitude = Fixed.FromInt(40);                   // 80 m: a slant of about 155 cells
        farAir.Altitude = Fixed.FromInt(40);
        w.Run(2);
        Assert.True(World.IsSpotted(behindRidge, 1), "a vehicle behind a ridge within the ground reach");
        Assert.False(World.IsSpotted(farVehicle, 1), "a vehicle beyond the ground reach");
        Assert.False(World.IsSpotted(inWoods, 1), "woods still hide soldiers");
        Assert.True(World.IsSpotted(inOpen, 1), "a soldier in the open");
        Assert.True(w.AirTracked(1, near.Id), "air within 200 cells is tracked");
        Assert.False(w.AirTracked(1, farAir.Id), "air beyond 200 cells is not");
        Assert.True(World.IsSpotted(near, 1));
        var fog = w.FogOf(1)!;
        Assert.True(fog.Visible(250, 290), "the fog is clear 40 cells out (from its edge)"); // 40 from the centre
        Assert.False(fog.Visible(250, 200), "50 cells out is fogged");
        Assert.Equal(new RadarStatus(true, Fixed.FromInt(200), Fixed.FromInt(60), Fixed.FromInt(40)), World.RadarOf(site));
        // Rubble: off.
        site.HP = 0;
        w.Run(2);
        Assert.True(site.Rubble);
        Assert.False(World.RadarOf(site)!.Value.On);
        Assert.False(World.IsSpotted(behindRidge, 1));
        Assert.False(World.IsSpotted(inOpen, 1));
        Assert.False(w.AirTracked(1, near.Id));
        Assert.False(fog.Visible(250, 270));
        // Repaired: on again.
        var dozer = w.Spawn(1, "bulldozer", 238, 250);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Repair, Units = [dozer.Id], Target = site.Id });
        var rt = RunUntil(w, () => !site.Rubble, 1000);
        Assert.True(rt > 0, $"repaired: hp {site.HP} order {dozer.Order.Kind} at {dozer.Pos.X.Raw / 1024},{dozer.Pos.Y.Raw / 1024}");
        w.Run(2);
        Assert.True(World.IsSpotted(behindRidge, 1));
        Assert.True(w.AirTracked(1, near.Id));
        Assert.True(fog.Visible(250, 270));
        // Hit to 0 as rubble it is gone.
        site.HP = 0;
        w.Run(1);
        site.HP = 0;
        w.Run(1);
        Assert.Null(w.Unit(site.Id));
    }

    [Fact]
    public void TheBaseRadarReachesFartherAndAsRubbleIsOff()
    {
        const int n = 480;
        var w = HeavyWorld(n, fog: true);
        var af = Base(w);
        var radar = w.Unit(af.Radar)!; // centre (158, 200)
        var inReach = w.Spawn(2, "technical", 158, 275);  // 75 cells: within the ground reach (80)
        var outReach = w.Spawn(2, "technical", 158, 288); // 88 cells: beyond it
        var air = w.Spawn(2, "scout_drone", 428, 200);    // 270 cells
        var farAir = w.Spawn(2, "scout_drone", 475, 470); // about 416 cells (the air reach is 400)
        air.Altitude = Fixed.FromInt(40);
        farAir.Altitude = Fixed.FromInt(40);
        w.Run(2);
        Assert.True(World.IsSpotted(inReach, 1));
        Assert.False(World.IsSpotted(outReach, 1));
        Assert.True(w.AirTracked(1, air.Id));
        Assert.False(w.AirTracked(1, farAir.Id));
        Assert.True(w.FogOf(1)!.Visible(158, 264), "the fog is clear 60 cells out from its edge");
        // The command building down does not touch the radar (its own piece).
        w.Unit(af.Facility)!.HP = 0;
        w.Run(2);
        Assert.True(World.IsSpotted(inReach, 1));
        radar.HP = 0;
        w.Run(2);
        Assert.True(radar.Rubble);
        Assert.False(World.IsSpotted(inReach, 1));
        Assert.False(w.AirTracked(1, air.Id));
    }

    // ---- Determinism ----

    [Fact]
    public void TheSameCommandsGiveTheSameHashes()
    {
        ulong[] Run(bool reversed)
        {
            var w = HeavyWorld(seed: 9);
            var a = w.Spawn(1, "bulldozer", 30, 125);
            var b = w.Spawn(1, "bulldozer", 30, 130);
            var hashes = new List<ulong>();
            w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.Build, Units = reversed ? [b.Id, a.Id] : [a.Id, b.Id], Cell = CoreCell, Unit = "airbase_core" });
            for (var t = 0; t < 3300; t++) // H3: the Watcher takes its 60 s build time at the hangar
            {
                if (t == 2000 && w.Airfields[0].Hangar != 0)
                {
                    var hangar = w.Unit(w.Airfields[0].Hangar)!;
                    Produce(w, hangar, "test_attacker");
                    Produce(w, hangar, "watcher_drone");
                }
                w.Step();
                hashes.Add(w.Hash());
            }
            Assert.Equal(2, w.Units.Count(u => u.Def.Archetype.Aircraft is not null));
            return [.. hashes];
        }
        var one = Run(false);
        Assert.Equal(one, Run(false));
        Assert.Equal(Run(true), Run(true));
    }

    [Fact]
    public void TheBaseIsHashed()
    {
        var w = HeavyWorld();
        var af = Base(w);
        var h = w.Hash();
        var hangar = af.Hangar;
        af.Hangar = 0;
        Assert.NotEqual(h, w.Hash());
        af.Hangar = hangar;
        Assert.Equal(h, w.Hash());
        af.Radar = 1;
        Assert.NotEqual(h, w.Hash());
    }
}
