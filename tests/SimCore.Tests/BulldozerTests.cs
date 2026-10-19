using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The Sentinel bulldozer: a builder vehicle. It carries the worker's builder (worker_build, by its
// worker tag) on a tracked, crewless, unarmed hull, so it builds what a worker builds (the
// airfield pieces included) at the worker's rate, and the vehicle garage makes it. Sentinel only.
public class BulldozerTests
{
    private static readonly ContentSet C = TestContent.Mvp;

    private static void Build(World w, Unit builder, string unit, Cell at, int facing = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = builder.Owner, Kind = CommandKind.Build, Units = [builder.Id], Cell = at, Unit = unit, Facing = facing });

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

    [Fact]
    public void ItIsATrackedCrewlessUnarmedBuilderOfTheSentinelOnly()
    {
        var dozer = C.Archetype("bulldozer")!;
        var worker = C.Archetype("worker")!;
        Assert.Equal((Locomotor.Tracked, 300, 400, 400), (dozer.Locomotor, dozer.MaxHp, dozer.Cost, dozer.BuildTime));
        Assert.True(worker.Speed < dozer.Speed && dozer.Speed < C.Archetype("cargo_truck")!.Speed); // between the worker and the truck
        Assert.Null(dozer.Crew); // it acts on its own, like a worker
        Assert.False(dozer.HasSlots);

        var w = Arena.NewSentinel();
        var d = w.Player(1)!.Setup.Find(w.Content, "bulldozer")!;
        var wd = w.Player(1)!.Setup.Find(w.Content, "worker")!;
        Assert.Empty(d.Weapons);
        Assert.NotNull(d.Builder);
        Assert.Equal(wd.Builder!.Rate, d.Builder!.Rate); // the worker's rate, a cell more of reach (a vehicle keeps clear of footprints)
        Assert.Equal(wd.Builder.Range + Fixed.FromInt(1), d.Builder.Range);

        Assert.True(C.Faction("sentinel")!.Archetypes[dozer.Index]);
        Assert.False(C.Faction("resistance")!.Archetypes[dozer.Index]);
        Assert.True(dozer.Tags.Matches(C.Archetype("vehicle_garage")!.Production!.Tags)); // the garage makes it
    }

    [Fact]
    public void TheGarageMakesOneThatDrivesOffWithoutADriver()
    {
        var w = Arena.NewSentinel(64);
        var g = w.Spawn(1, "vehicle_garage", 20, 10);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [g.Id], Unit = "bulldozer" });
        var t = RunUntil(w, () => w.Units.Any(u => u.Def.Archetype.Id == "bulldozer" && u.Order.Kind == OrderKind.Move), 1000);
        Assert.InRange(t, 400, 500);
        var dozer = w.Units.Single(u => u.Def.Archetype.Id == "bulldozer");
        Assert.Equal(0u, dozer.Driver);
        Assert.True(dozer.CanAct);
    }

    [Fact]
    public void ItBuildsADepotInTheWorkersTime()
    {
        var w = Arena.NewSentinel(48);
        var dozer = w.Spawn(1, "bulldozer", 19, 20);
        Build(w, dozer, "supply_depot", new Cell(20, 20));
        w.Step();
        var site = w.Units.Single(u => u.Def.Archetype.Id == "supply_depot");
        Assert.False(site.IsBuilt);
        Assert.InRange(RunUntil(w, () => site.IsBuilt), 590, 620); // 30 s for one builder
        Assert.Equal(site.Def.MaxHp, site.HP);
        Assert.Equal(OrderKind.Idle, dozer.Order.Kind);
    }

    [Fact]
    public void ItBuildsTheAirfieldFacilityAndARow()
    {
        var w = Arena.NewSentinel(160);
        var dozer = w.Spawn(1, "bulldozer", 20, 30); // it drives up to the site's north-west corner
        Build(w, dozer, "airfield_facility", new Cell(30, 40));
        w.Step();
        var af = Assert.Single(w.Airfields);
        var facility = w.Unit(af.Facility)!;
        Assert.InRange(RunUntil(w, () => facility.IsBuilt), 950, 1050); // 45 s after a 5-second drive

        Build(w, dozer, "airfield_row", World.AirfieldRowCell(af, 0, 0));
        w.Step();
        var row = w.Unit(af.Rows.Single().Unit)!;
        Assert.InRange(RunUntil(w, () => row.IsBuilt), 1300, 1450); // the first row and its runway: 65 s
        Assert.All(af.Segments, s => Assert.True(w.Unit(s)!.IsBuilt));
        Assert.All(af.Rows.Single().Stations, s => Assert.NotEqual(0u, s.Unit));
        Assert.Equal(OrderKind.Idle, dozer.Order.Kind);
    }
}
