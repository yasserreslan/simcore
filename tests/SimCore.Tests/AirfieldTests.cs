using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The Sentinel airfield, milestone M1 (the game repo's docs/airfield-design.md): a worker builds
// the facility, which founds the airfield and reserves its first block's plan; rows and runway
// segments grow it on the type's grid by the growth rules, through the ordinary construction flow;
// the first row lays the minimum runway on its one bar; a finished row spawns its two stations.
// The field turns in quarter turns with the facility. Nothing here is specific to the Watcher.
public class AirfieldTests
{
    private const string Facility = "airfield_facility";
    private const string Row = "airfield_row";
    private const string Segment = "airfield_runway";

    // At facing 0 a facility at (30, 40) puts the airfield's origin at (41, 16): the runway on
    // y 16-26, the taxiway on y 32-40, row r on y 40 + 10r, column c on x 41 + 28c.
    private static readonly Cell FacilityCell = new(30, 40);
    private static readonly Cell Origin = new(41, 16);

    private static UnitDef Def(World w, string id, byte player = 1) => w.Player(player)!.Setup.Find(w.Content, id)!;

    private static (World W, Unit Worker) Field(int size = 160)
    {
        var w = Arena.NewSentinel(size);
        return (w, w.Spawn(1, "worker", 28, 38));
    }

    private static void Build(World w, Unit worker, string unit, Cell at, int facing = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = worker.Owner, Kind = CommandKind.Build, Units = [worker.Id], Cell = at, Unit = unit, Facing = facing });

    private static bool CanPlace(World w, Unit worker, string unit, Cell at, int facing = 0) =>
        w.CanPlace(Def(w, unit, worker.Owner), at, facing, [worker]);

    private static Airfield Spawned(World w, IReadOnlyList<(int, int)> rows, int west = 0, int east = 0, Cell? at = null, int facing = 0) =>
        w.SpawnAirfield(Def(w, Facility), at ?? FacilityCell, facing, rows, west, east) ?? throw new InvalidOperationException("no airfield");

    private static int RunUntil(World w, Func<bool> done, int max = 4000)
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

    // ---- Founding ----

    [Fact]
    public void TheFacilityFoundsAnAirfieldAndReservesItsPlan()
    {
        var (w, worker) = Field();
        Assert.True(CanPlace(w, worker, Facility, FacilityCell));
        Build(w, worker, Facility, FacilityCell);
        w.Step();
        var site = Assert.Single(w.All(Facility));
        Assert.False(site.IsBuilt);
        Assert.Equal(FacilityCell, World.FootprintOrigin(site));
        var af = Assert.Single(w.Airfields);
        Assert.Equal((site.Id, site.Id, (byte)1, Origin, 0), (af.Id, af.Facility, af.Owner, af.Origin, af.Facing));
        Assert.Equal(site.Id, site.Airfield);
        Assert.False(af.HasRunway);
        Assert.Empty(af.Rows);
        Assert.Equal("forward_airfield", af.Type.Id);

        // The plan (3 segments and row (0, 0)) is reserved against every player's structures.
        var enemy = w.Spawn(2, "worker", 120, 120);
        Assert.False(CanPlace(w, worker, "supply_depot", new Cell(60, 20)));  // on segment 1
        Assert.False(CanPlace(w, enemy, "supply_depot", new Cell(60, 20)));
        Assert.False(CanPlace(w, worker, "supply_depot", new Cell(50, 44)));  // on row (0, 0)
        Assert.True(CanPlace(w, worker, "supply_depot", new Cell(80, 44)));   // row (1, 0): not reserved
        Assert.True(CanPlace(w, enemy, "supply_depot", new Cell(60, 60)));

        var plan = w.AirfieldPlan(Def(w, Facility), FacilityCell, 0);
        Assert.Equal(7, plan.Count); // facility, 3 segments, row (0, 0), 2 corridors
        Assert.Equal(new AirfieldRect(AirfieldRole.Facility, FacilityCell, 10, 10, false), plan[0]);
        Assert.Equal(new AirfieldRect(AirfieldRole.Segment, new Cell(41, 16), 20, 24, false), plan[1]);
        Assert.Equal(new AirfieldRect(AirfieldRole.Row, new Cell(41, 40), 28, 10, false), plan[4]);
        Assert.Equal(new AirfieldRect(AirfieldRole.Segment, new Cell(11, 16), 30, 10, true), plan[5]);
        Assert.Equal(new AirfieldRect(AirfieldRole.Segment, new Cell(101, 16), 30, 10, true), plan[6]);
    }

    [Fact]
    public void TheFacilityIsRefusedOffTheMapOrOverlapping()
    {
        var (w, worker) = Field();
        Assert.False(CanPlace(w, worker, Facility, new Cell(10, 40)));  // the west corridor leaves the map
        Assert.False(CanPlace(w, worker, Facility, new Cell(80, 40)));  // the east corridor leaves the map
        Assert.False(CanPlace(w, worker, Facility, new Cell(30, 10)));  // the runway would be off the map
        Assert.True(CanPlace(w, worker, Facility, new Cell(19, 40)));   // corridor to x 0 exactly
        Assert.True(CanPlace(w, worker, Facility, new Cell(59, 40)));   // corridor to x 160 exactly
        Assert.False(CanPlace(w, worker, Facility, new Cell(30, 40), Facing.North + 1)); // not a quarter turn

        w.Spawn(1, "supply_depot", 70, 30); // on the planned runway
        Assert.False(CanPlace(w, worker, Facility, FacilityCell));
        Assert.True(CanPlace(w, worker, Facility, new Cell(30, 90)));

        w.Grid.Set(new Cell(50, 95), Terrain.Blocked); // a rock in row (0, 0) of that plan
        Assert.False(CanPlace(w, worker, Facility, new Cell(30, 90)));
    }

    [Fact]
    public void UnitsBlockOnlyTheFacilityNotThePavementPlan()
    {
        var (w, worker) = Field();
        var soldier = w.Spawn(1, "Rifleman", 60, 30); // on the planned runway: pavement, fine
        Assert.True(CanPlace(w, worker, Facility, FacilityCell));
        soldier.Pos = new Cell(35, 45).Center(); // in the facility's footprint
        Assert.False(CanPlace(w, worker, Facility, FacilityCell));
    }

    [Fact]
    public void AnotherAirfieldsPlanIsReserved()
    {
        var (w, worker) = Field();
        Build(w, worker, Facility, FacilityCell);
        w.Step();
        // A second airfield 20 cells further down would lay its runway over the first's row (0, 0).
        Assert.False(CanPlace(w, worker, Facility, new Cell(30, 60)));
        Assert.True(CanPlace(w, worker, Facility, new Cell(30, 90)));
    }

    [Fact]
    public void TooMuchReliefRefusesTheFacility()
    {
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        TerrainMap Ramp(int rawPerCell)
        {
            var n = 160;
            var corners = new int[(n + 1) * (n + 1)];
            for (var y = 0; y <= n; y++)
            {
                for (var x = 0; x <= n; x++)
                {
                    corners[y * (n + 1) + x] = x * rawPerCell;
                }
            }
            return new TerrainMap(n, n, corners, new byte[n * n], rules);
        }
        // A 2% ramp spreads the 71-cell plan over 2.8 m: within 4 m of the pad. A 9% ramp (still
        // gentle ground) spreads it over 12.8 m: refused.
        foreach (var (grade, ok) in new[] { (20, true), (92, false) })
        {
            var (w, worker) = Field();
            w.AttachTerrain(Ramp(grade));
            Assert.Equal(ok, CanPlace(w, worker, Facility, FacilityCell));
        }
    }

    // ---- Growth ----

    [Fact]
    public void RowsGrowByTheRules()
    {
        var (w, worker) = Field();
        var af = Spawned(w, []);
        bool RowOk(int col, int row) => CanPlace(w, worker, Row, World.AirfieldRowCell(af, col, row));
        Assert.True(RowOk(0, 0));
        Assert.False(RowOk(1, 0));   // must touch a row: (0, 0) comes first
        Assert.False(RowOk(0, 1));   // a floating row: nothing below it
        Assert.False(CanPlace(w, worker, Segment, World.AirfieldSegmentCell(af, 3))); // no runway yet

        Build(w, worker, Row, World.AirfieldRowCell(af, 0, 0));
        w.Step();
        Assert.Single(af.Rows);
        Assert.True(af.HasRunway);
        Assert.True(RowOk(1, 0));    // lane mouth at x 42, over the runway's taxiway (0-60)
        Assert.True(RowOk(0, 1));
        Assert.False(RowOk(0, 0));   // taken
        Assert.False(RowOk(1, 1));   // nothing below it
        Assert.False(RowOk(2, 0));   // touches nothing yet
        Assert.False(RowOk(-1, 0));  // columns start at 0
        var next = World.AirfieldRowCell(af, 1, 0);
        Assert.False(CanPlace(w, worker, Row, new Cell(next.X + 1, next.Y))); // off the grid
        // While the first row is laying the runway, the runway can't be extended.
        Assert.NotEqual(0u, af.LeadRow);
        Assert.False(CanPlace(w, worker, Segment, World.AirfieldSegmentCell(af, 3)));
    }

    [Fact]
    public void AThirdColumnNeedsAFourthSegment()
    {
        var (w, worker) = Field();
        var af = Spawned(w, [(0, 0), (1, 0)]);
        bool RowOk(int col, int row) => CanPlace(w, worker, Row, World.AirfieldRowCell(af, col, row));
        Assert.False(RowOk(2, 0)); // its lane mouth (x 70) is past the 3 segments (x 0-60)
        var af2World = Field();
        var af2 = Spawned(af2World.W, [(0, 0), (1, 0)], east: 1);
        Assert.Equal((0, 3), (af2.West, af2.East));
        Assert.True(af2World.W.CanPlace(Def(af2World.W, Row), World.AirfieldRowCell(af2, 2, 0), 0, [af2World.Worker]));
    }

    [Fact]
    public void AnyRectangleOfRows()
    {
        var (w, _) = Field();
        var af = Spawned(w, [(0, 0), (1, 0), (0, 1), (1, 1), (0, 2), (1, 2)]);
        Assert.Equal(6, af.Rows.Count);
        Assert.Equal([(0, 0), (0, 1), (0, 2), (1, 0), (1, 1), (1, 2)], af.Rows.Select(r => (r.Col, r.Row)));
        Assert.Equal(12, af.Rows.Sum(r => r.Stations.Count(s => s.Unit != 0)));
        Assert.Throws<ArgumentException>(() => Spawned(Field().W, [(0, 0), (0, 2)])); // floating
    }

    [Fact]
    public void SegmentsExtendEitherEndWhileTheCorridorIsOnTheMap()
    {
        // Origin at x 50: segment -1 (x 30-50) leaves a 30-cell corridor to x 0; segment 3 (x 110-130)
        // one to x 160.
        var (w, worker) = Field();
        var af = Spawned(w, [(0, 0)], at: new Cell(39, 40));
        Assert.Equal(new Cell(50, 16), af.Origin);
        bool SegOk(int k) => CanPlace(w, worker, Segment, World.AirfieldSegmentCell(af, k));
        Assert.True(SegOk(-1));
        Assert.True(SegOk(3));
        Assert.False(SegOk(1));  // taken
        Assert.False(SegOk(4));  // not an end
        var end = World.AirfieldSegmentCell(af, 3);
        Assert.False(CanPlace(w, worker, Segment, new Cell(end.X, end.Y + 1)));

        Build(w, worker, Segment, World.AirfieldSegmentCell(af, -1));
        w.Step();
        Assert.Equal((-1, 2), (af.West, af.East));
        Assert.False(SegOk(-2)); // its corridor would leave the map
        Assert.True(SegOk(3));
        var west = w.Unit(af.Segments[0])!;
        Assert.False(west.IsBuilt);
        Assert.Equal(new Cell(30, 16), World.FootprintOrigin(west));

        // Rows and segments need a standing facility (rubble does not stand: M3a).
        w.Unit(af.Facility)!.HP = 0;
        w.Step();
        Assert.True(w.Unit(af.Facility)!.Rubble);
        Assert.False(SegOk(3));
        Assert.False(CanPlace(w, worker, Row, World.AirfieldRowCell(af, 1, 0)));
    }

    [Fact]
    public void ARowNeedsAFinishedFacility()
    {
        var (w, worker) = Field();
        Build(w, worker, Facility, FacilityCell);
        w.Step();
        var af = Assert.Single(w.Airfields);
        Assert.False(CanPlace(w, worker, Row, World.AirfieldRowCell(af, 0, 0)));
    }

    // ---- Quarter turns ----

    // Facility cells that put the origin where the whole field (2 columns of 3 rows, corridors
    // included) fits a 160-cell map, for each quarter turn.
    public static TheoryData<int, Cell, Cell> Turns => new()
    {
        { Facing.North, new Cell(35 - 11, 50 + 24), new Cell(35, 50) },
        { Facing.East, new Cell(105 - 34, 35 - 11), new Cell(105, 35) },
        { Facing.South, new Cell(125 + 1, 110 - 34), new Cell(125, 110) },
        { Facing.West, new Cell(50 + 24, 125 + 1), new Cell(50, 125) },
    };

    [Theory]
    [MemberData(nameof(Turns))]
    public void EveryQuarterTurnLaysTheSameField(int facing, Cell facility, Cell origin)
    {
        var (w, _) = Field();
        var worker = w.Spawn(1, "worker", 2, 2);
        var af = Spawned(w, [(0, 0), (1, 0), (0, 1), (1, 1), (0, 2), (1, 2)], at: facility, facing: facing);
        Assert.Equal(origin, af.Origin);
        var turned = facing % 16 == 8;
        var pieces = w.Units.Where(u => u.Airfield == af.Id).ToList();
        Assert.Equal(1 + 6 + 3 + 12, pieces.Count);
        Assert.All(pieces, u => Assert.Equal(facing, u.Facing));
        foreach (var r in af.Rows)
        {
            var u = w.Unit(r.Unit)!;
            Assert.Equal(turned ? (10, 28) : (28, 10), (World.FootW(u), World.FootH(u)));
            Assert.Equal(World.AirfieldRowCell(af, r.Col, r.Row), World.FootprintOrigin(u));
        }
        foreach (var s in af.Segments)
        {
            var u = w.Unit(s)!;
            Assert.Equal(turned ? (24, 20) : (20, 24), (World.FootW(u), World.FootH(u)));
        }
        // No two pieces overlap but a station on its row; everything is on the map.
        var big = pieces.Where(u => u.Def.Archetype.Airfield!.Role != AirfieldRole.Station).ToList();
        for (var i = 0; i < big.Count; i++)
        {
            for (var j = i + 1; j < big.Count; j++)
            {
                Assert.False(Overlap(big[i], big[j]), $"{big[i].Def.Name} {big[i].Id} overlaps {big[j].Def.Name} {big[j].Id}");
            }
        }
        foreach (var st in pieces.Where(u => u.Def.Archetype.Airfield!.Role == AirfieldRole.Station))
        {
            Assert.Single(big, b => Overlap(b, st));
            Assert.True(Inside(st, w.Unit(af.Rows.Single(r => r.Stations.Any(s => s.Unit == st.Id)).Unit)!));
        }
        Assert.All(pieces, u =>
        {
            var o = World.FootprintOrigin(u);
            Assert.True(o.X >= 0 && o.Y >= 0 && o.X + World.FootW(u) <= 160 && o.Y + World.FootH(u) <= 160);
        });
        // The facility stands west of row (0, 0) in the field's frame, beside the taxiway.
        var fac = w.Unit(af.Facility)!;
        Assert.Equal(World.AirfieldPoint(af, -60, 290), fac.Pos);
        // Build commands snap to the turned grid too.
        Assert.True(w.CanPlace(Def(w, Row), World.AirfieldRowCell(af, 0, 3), facing, [worker]));
        Assert.False(w.CanPlace(Def(w, Row), World.AirfieldRowCell(af, 2, 0), facing, [worker]));
    }

    private static bool Overlap(Unit a, Unit b)
    {
        Cell oa = World.FootprintOrigin(a), ob = World.FootprintOrigin(b);
        return oa.X < ob.X + World.FootW(b) && ob.X < oa.X + World.FootW(a) && oa.Y < ob.Y + World.FootH(b) && ob.Y < oa.Y + World.FootH(a);
    }

    private static bool Inside(Unit inner, Unit outer)
    {
        Cell i = World.FootprintOrigin(inner), o = World.FootprintOrigin(outer);
        return i.X >= o.X && i.Y >= o.Y && i.X + World.FootW(inner) <= o.X + World.FootW(outer) && i.Y + World.FootH(inner) <= o.Y + World.FootH(outer);
    }

    [Fact]
    public void EighteenStationsFitA160CellMap()
    {
        // The design's largest fit: 3 columns of 3 rows on 4 segments, 95 x 54 cells, 140 of 160
        // cells along the runway with its corridors.
        var (w, worker) = Field();
        var af = Spawned(w, [(0, 0), (1, 0), (0, 1), (1, 1), (0, 2), (1, 2)], east: 1, at: new Cell(25, 40));
        foreach (var (col, row) in new[] { (2, 0), (2, 1), (2, 2) })
        {
            Assert.True(CanPlace(w, worker, Row, World.AirfieldRowCell(af, col, row)), $"row ({col}, {row})");
            Build(w, worker, Row, World.AirfieldRowCell(af, col, row));
            w.Step();
        }
        Assert.Equal(9, af.Rows.Count);
        Assert.Equal(4, af.Segments.Count);
        var all = w.Units.Where(u => u.Airfield == af.Id).ToList();
        int x0 = all.Min(u => World.FootprintOrigin(u).X), x1 = all.Max(u => World.FootprintOrigin(u).X + World.FootW(u));
        int y0 = all.Min(u => World.FootprintOrigin(u).Y), y1 = all.Max(u => World.FootprintOrigin(u).Y + World.FootH(u));
        Assert.Equal((95, 54), (x1 - x0, y1 - y0));
    }

    // ---- Construction ----

    [Fact]
    public void WorkersBuildTheFacilityThenTheFirstRowLaysTheRunwayOnOneBar()
    {
        var (w, worker) = Field();
        Build(w, worker, Facility, FacilityCell);
        w.Step();
        var af = Assert.Single(w.Airfields);
        var facility = w.Unit(af.Facility)!;
        Assert.InRange(RunUntil(w, () => facility.IsBuilt), 900, 1000);
        Assert.Equal(facility.Def.MaxHp, facility.HP);

        Build(w, worker, Row, World.AirfieldRowCell(af, 0, 0));
        w.Step();
        var row = w.Unit(af.Rows.Single().Unit)!;
        Assert.False(row.IsBuilt);
        Assert.Equal(row.Id, af.LeadRow);
        Assert.Equal((400 + 3 * 300) * 100, row.BuildTotal);
        Assert.Equal(3, af.Segments.Count);
        var segments = af.Segments.Select(id => w.Unit(id)!).ToList();
        Assert.All(segments, s => Assert.False(s.IsBuilt));
        Assert.Equal([new Cell(41, 16), new Cell(61, 16), new Cell(81, 16)], segments.Select(World.FootprintOrigin));

        // The runway follows the row's bar: every segment at the row's fraction of its own build.
        var ticks = RunUntil(w, () =>
        {
            foreach (var s in segments)
            {
                Assert.Equal(s.IsBuilt ? 0 : (int)((long)row.Work * 30000 / 130000), s.Work);
                Assert.Equal(row.IsBuilt, s.IsBuilt);
            }
            return row.IsBuilt;
        });
        Assert.InRange(ticks, 1300, 1450); // 65 s with one worker, after a short walk
        Assert.Equal(0u, af.LeadRow);
        Assert.Equal(0, row.BuildTotal);
        Assert.All(segments, s => Assert.Equal(s.Def.MaxHp, s.HP));
        Assert.Equal(OrderKind.Idle, worker.Order.Kind);

        // The finished row spawned its loading station (west of the lane) and its open stand (east).
        var st = af.Rows.Single().Stations;
        Assert.Equal([StationKind.Loading, StationKind.Normal], st.Select(s => s.Kind));
        var shelter = w.Unit(st[0].Unit)!;
        var stand = w.Unit(st[1].Unit)!;
        Assert.Equal(("airfield_shelter", "airfield_stand"), (shelter.Def.Archetype.Id, stand.Def.Archetype.Id));
        Assert.True(shelter.IsBuilt && stand.IsBuilt);
        Assert.Equal((new Cell(41, 40), new Cell(59, 40)), (World.FootprintOrigin(shelter), World.FootprintOrigin(stand)));
        Assert.Equal((af.Id, af.Id), (shelter.Airfield, stand.Airfield));
        Assert.All(st, s => Assert.Equal((0u, 0u), (s.Occupant, s.ReservedBy)));

        // A second row: an ordinary 20-second site, no runway this time.
        Build(w, worker, Row, World.AirfieldRowCell(af, 1, 0));
        w.Step();
        var second = w.Unit(af.RowAt(1, 0)!.Unit)!;
        Assert.Equal(0, second.BuildTotal);
        Assert.InRange(RunUntil(w, () => second.IsBuilt), 400, 800); // 20 s of work after the walk over
        Assert.Equal(3, af.Segments.Count);
        Assert.All(af.RowAt(1, 0)!.Stations, s => Assert.NotEqual(0u, s.Unit));
    }

    [Fact]
    public void TheRunwaySitesArePassiveCancelTheRowToCancelThem()
    {
        var (w, worker) = Field();
        var af = Spawned(w, []);
        Build(w, worker, Row, World.AirfieldRowCell(af, 0, 0));
        w.Run(50);
        var row = w.Unit(af.LeadRow)!;
        var seg = w.Unit(af.Segments[1])!;

        // Sent to a runway site, a worker builds the row's bar.
        var helper = w.Spawn(1, "worker", 60, 52);
        w.Order(1, CommandKind.Resume, [helper], seg.Id);
        w.Step();
        Assert.Equal(row.Id, helper.Order.Target);

        // A runway site can't be cancelled on its own; cancelling the row cancels the runway.
        w.Order(1, CommandKind.CancelBuild, [], seg.Id);
        w.Step();
        Assert.NotNull(w.Unit(seg.Id));
        w.Order(1, CommandKind.CancelBuild, [], row.Id);
        w.Step();
        Assert.Null(w.Unit(row.Id));
        Assert.Null(w.Unit(seg.Id));
        Assert.Empty(af.Rows);
        Assert.False(af.HasRunway);
        Assert.Equal(0u, af.LeadRow);
        Assert.Equal(OrderKind.Idle, helper.Order.Kind);
        Assert.True(CanPlace(w, worker, Row, World.AirfieldRowCell(af, 0, 0)));
    }

    [Fact]
    public void CancellingTheFacilitySiteRemovesTheAirfieldAndItsPlan()
    {
        var (w, worker) = Field();
        Build(w, worker, Facility, FacilityCell);
        w.Step();
        var af = Assert.Single(w.Airfields);
        w.Order(1, CommandKind.CancelBuild, [], af.Facility);
        w.Step();
        Assert.Empty(w.Airfields);
        Assert.True(CanPlace(w, worker, "supply_depot", new Cell(60, 20)));
    }

    // ---- Pavement and flags ----

    [Fact]
    public void PavementBlocksNoRouteButTheFacilityAndShelterDo()
    {
        var (w, _) = Field();
        Spawned(w, [(0, 0)]);
        w.Step(); // routing takes in the structures
        var r = w.Routing;
        var k = r.Nav.Class(0, 1);
        Assert.NotEqual(0, r.Nav.Cost(k, 55, 45)); // the lane
        Assert.NotEqual(0, r.Nav.Cost(k, 64, 45)); // the open stand
        Assert.NotEqual(0, r.Nav.Cost(k, 60, 20)); // the runway
        Assert.Equal(0, r.Nav.Cost(k, 35, 45));    // the facility
        Assert.Equal(0, r.Nav.Cost(k, 46, 45));    // the shelter
    }

    [Fact]
    public void OnTerrainPiecesShareOnePadAndPavementIsRoad()
    {
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        var n = 160;
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = x * 20; // a 2% ramp
            }
        }
        var (w, _) = Field();
        w.AttachTerrain(new TerrainMap(n, n, corners, new byte[n * n], rules));
        var af = Spawned(w, [(0, 0)]);
        var g = w.Ground!;
        Assert.NotEqual(0, af.PadRaw);
        foreach (var (x, y) in new[] { (30, 40), (40, 50), (41, 16), (101, 40), (69, 50), (55, 45) })
        {
            Assert.Equal(af.PadRaw, g.Corner(x, y).Raw);
        }
        Assert.True(g.Road(new Cell(55, 45)));  // the lane
        Assert.True(g.Road(new Cell(60, 20)));  // the runway
        Assert.True(g.Road(new Cell(45, 45)));  // the row under the shelter
        Assert.False(g.Road(new Cell(35, 45))); // the facility
    }

    [Fact]
    public void OnlyTheFacilityAndShelterAreTargetsAndDamageable()
    {
        var (w, _) = Field();
        var af = Spawned(w, [(0, 0)]);
        var gun = w.Spawn(1, "Tank hunter", 100, 100);
        var weapon = gun.Def.Weapons[0];
        var row = af.Rows[0];
        var targets = new[] { af.Facility, row.Unit, af.Segments[0], row.Stations[0].Unit, row.Stations[1].Unit }.Select(id => w.Unit(id)!).ToList();
        Assert.Equal([true, false, false, true, false], targets.Select(t => World.CanHit(weapon, t)));
        // Attack orders at pavement or an open stand are refused.
        var enemy = w.Spawn(2, "Rifleman", 70, 60);
        w.Order(2, CommandKind.Attack, [enemy], row.Stations[1].Unit);
        w.Step();
        Assert.Equal(OrderKind.Idle, enemy.Order.Kind);
        w.Order(2, CommandKind.Attack, [enemy], af.Facility);
        w.Step();
        Assert.Equal(OrderKind.Attack, enemy.Order.Kind);
        foreach (var t in targets)
        {
            var hp = t.HP;
            w.Hurt(t, weapon.Warhead, 100, 100);
            Assert.Equal(t.Def.Archetype.Invulnerable, t.HP == hp);
        }
    }

    // ---- Derived data for M2 ----

    [Fact]
    public void TheTaxiGraphJoinsRunwayTaxiwayLanesAndStops()
    {
        var (w, _) = Field();
        var af = Spawned(w, [(0, 0), (0, 1)]);
        var g = w.AirfieldTaxiGraph(af);
        int Count(TaxiNodeKind k) => g.Nodes.Count(n => n.Kind == k);
        Assert.Equal((2, 3, 3, 1, 2, 4),
            (Count(TaxiNodeKind.RunwayEnd), Count(TaxiNodeKind.RunwayConnector), Count(TaxiNodeKind.TaxiwayConnector),
             Count(TaxiNodeKind.LaneMouth), Count(TaxiNodeKind.Lane), Count(TaxiNodeKind.Stop)));
        // Runway 4 + connectors 3 + taxiway 3 + lane 2 + stops 4.
        Assert.Equal(16, g.Edges.Count);
        // Connected: every node reachable from the runway's west end.
        var seen = new bool[g.Nodes.Count];
        var stack = new Stack<int>([0]);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (seen[i])
            {
                continue;
            }
            seen[i] = true;
            foreach (var (a, b) in g.Edges)
            {
                if (a == i || b == i)
                {
                    stack.Push(a == i ? b : a);
                }
            }
        }
        Assert.All(seen, Assert.True);
        // Positions: the west end on the centre line, the lane mouth at x 14 over the taxiway.
        Assert.Equal(new Vec(Fixed.FromInt(41), Fixed.FromInt(21)), g.Nodes[0].At);
        var mouth = g.Nodes.Single(n => n.Kind == TaxiNodeKind.LaneMouth);
        Assert.Equal((140, 200), mouth.Local);
        Assert.Equal(new Vec(Fixed.FromInt(55), Fixed.FromInt(36)), mouth.At);
        // A stop is its station's: the shelter's at its stop mark.
        var shelter = af.Rows[0].Stations[0].Unit;
        Assert.Equal((50, 290), g.Nodes.Single(n => n.Station == shelter).Local);
        var again = w.AirfieldTaxiGraph(af); // the same pieces give the same graph
        Assert.Equal(g.Nodes, again.Nodes);
        Assert.Equal(g.Edges, again.Edges);
    }

    // A made-up class-M jet and a class-L transport (test fixtures, content only) prove nothing is
    // Watcher-specific: the forward airfield serves S and M, never L, and a station takes only
    // aircraft no wider than its span limit.
    [Fact]
    public void AMadeUpClassMAircraftUsesTheSameRules()
    {
        const string Fixture = """
            {"archetypes":[
              {"id":"test_jet","name":"Test jet","glyph":"y","tags":["drone"],"locomotor":"flying","armor":"drone","hp":300,"speed":150,"turnRate":2,
               "aircraft":{"sizeClass":"M","span":60,"length":70,"landing":"runway","minLandRun":500,"minTakeoffRun":400,
                 "approach":{"fixDistance":400,"fixHeight":200,"touchdown":100,"flare":50,"rolloutDecel":10,"brake":20},
                 "speeds":{"approach":80,"touchdown":70,"taxi":30,"turn":15,"pushback":5,"takeoff":90,"takeoffAccel":15},
                 "service":{"refuelTicks":600,"rearmTicksPerRound":200,"repairHpPerSecond":15},
                 "groundVision":60,"groundEye":30,"holdReserve":1200,"sortie":true}},
              {"id":"test_wide_jet","name":"Wide jet","glyph":"y","tags":["drone"],"locomotor":"flying","armor":"drone","hp":300,"speed":150,"turnRate":2,
               "aircraft":{"sizeClass":"M","span":95,"length":70,"landing":"runway","minLandRun":500,"minTakeoffRun":400,
                 "approach":{"fixDistance":400,"fixHeight":200,"touchdown":100,"flare":50,"rolloutDecel":10,"brake":20},
                 "speeds":{"approach":80,"touchdown":70,"taxi":30,"turn":15,"pushback":5,"takeoff":90,"takeoffAccel":15},
                 "service":{"refuelTicks":600,"rearmTicksPerRound":200,"repairHpPerSecond":15},
                 "groundVision":60,"groundEye":30,"holdReserve":1200,"sortie":false}},
              {"id":"test_transport","name":"Transport","glyph":"y","tags":["drone"],"locomotor":"flying","armor":"drone","hp":900,"speed":120,"turnRate":1,
               "aircraft":{"sizeClass":"L","span":60,"length":120,"landing":"runway","minLandRun":900,"minTakeoffRun":800,
                 "approach":{"fixDistance":500,"fixHeight":250,"touchdown":150,"flare":60,"rolloutDecel":8,"brake":15},
                 "speeds":{"approach":70,"touchdown":60,"taxi":25,"turn":12,"pushback":4,"takeoff":80,"takeoffAccel":8},
                 "service":{"refuelTicks":1200,"rearmTicksPerRound":400,"repairHpPerSecond":20},
                 "groundVision":60,"groundEye":40,"holdReserve":1200,"sortie":false}},
              {"id":"test_helicopter","name":"Helicopter","glyph":"y","tags":["drone"],"locomotor":"flying","armor":"drone","hp":250,"speed":100,"turnRate":3,
               "aircraft":{"sizeClass":"S","span":50,"length":60,"landing":"vertical","minLandRun":0,"minTakeoffRun":0,
                 "approach":{"fixDistance":200,"fixHeight":100,"touchdown":0,"flare":20,"rolloutDecel":10,"brake":10},
                 "speeds":{"approach":40,"touchdown":10,"taxi":20,"turn":10,"pushback":5,"takeoff":40,"takeoffAccel":10},
                 "service":{"refuelTicks":400,"rearmTicksPerRound":200,"repairHpPerSecond":10},
                 "groundVision":60,"groundEye":30,"holdReserve":600,"sortie":true}}
            ]}
            """;
        var sources = TestContent.MvpSources();
        sources.Add(new ContentSource("fixtures/aircraft.json", Fixture));
        var content = ContentLoader.Load(sources);
        var jet = content.Archetype("test_jet")!.Aircraft!;
        var wide = content.Archetype("test_wide_jet")!.Aircraft!;
        var transport = content.Archetype("test_transport")!.Aircraft!;
        var heli = content.Archetype("test_helicopter")!.Aircraft!;
        Assert.Equal((AircraftClass.M, LandingKind.Runway, Fixed.FromInt(50)), (jet.SizeClass, jet.Landing, jet.MinLandRun));
        Assert.Equal((LandingKind.Vertical, Fixed.Zero), (heli.Landing, heli.MinLandRun));

        var (w, _) = Field();
        var af = Spawned(w, [(0, 0), (1, 0)]);
        var watcher = w.Content.Archetype("watcher_drone")!.Aircraft!;
        var all = af.Rows.SelectMany(r => r.Stations).ToList();
        Assert.Equal(all, w.StationsFor(af, watcher));
        Assert.Equal(all, w.StationsFor(af, jet));                 // class M: served; 12 m fits every station
        Assert.Equal(all.Where(s => s.Kind == StationKind.Normal), w.StationsFor(af, wide)); // 19 m: wider than a shelter (18 m)
        Assert.Empty(w.StationsFor(af, transport));                 // class L: needs a larger airfield type
        Assert.True(af.Type.Serves(AircraftClass.S) && af.Type.Serves(AircraftClass.M) && !af.Type.Serves(AircraftClass.L));
    }

    // ---- Hashing and determinism ----

    [Fact]
    public void AirfieldStateIsHashed()
    {
        var (w, _) = Field();
        var before = w.Hash();
        var af = Spawned(w, [(0, 0)]);
        var with = w.Hash();
        Assert.NotEqual(before, with);
        var st = af.Rows[0].Stations[1];
        st.Occupant = 12345;
        Assert.NotEqual(with, w.Hash());
        st.Occupant = 0;
        Assert.Equal(with, w.Hash());
        st.ReservedBy = 7;
        Assert.NotEqual(with, w.Hash());
        st.ReservedBy = 0;
        af.LeadRow = 1;
        Assert.NotEqual(with, w.Hash());
        af.LeadRow = 0;
        Assert.Equal(with, w.Hash());
    }

    [Fact]
    public void TheSameCommandsGiveTheSameHashes()
    {
        ulong[] Run(bool reversed)
        {
            var (w, a) = Field();
            var b = w.Spawn(1, "worker", 26, 38);
            var hashes = new List<ulong>();
            var first = reversed ? b : a;
            var second = reversed ? a : b;
            w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.Build, Units = [first.Id, second.Id], Cell = FacilityCell, Unit = Facility });
            for (var t = 0; t < 1400; t++)
            {
                if (t == 800)
                {
                    var af = w.Airfields[0];
                    w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [a.Id, b.Id], Cell = World.AirfieldRowCell(af, 0, 0), Unit = Row });
                }
                w.Step();
                hashes.Add(w.Hash());
            }
            Assert.Single(w.Airfields[0].Rows);
            return [.. hashes];
        }
        var one = Run(false);
        Assert.Equal(one, Run(false));
        Assert.Equal(one.Length, Run(true).Length); // a different builder order is a different game, and still deterministic
        Assert.Equal(Run(true), Run(true));
    }
}
