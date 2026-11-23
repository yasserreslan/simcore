using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// Turned factories (BUILD-ROTATE, 2026-11-23): a factory built or spawned a quarter turn stands with its whole layout turned
// (Unit.Quarter: 1 the front to the west, 2 north, 3 east): its doors' exits and bay floors, the exit lanes, the step-aside
// and its default rally turn with it, so a finished unit leaves through the turned door and drives to the turned rally;
// a non-square one swaps its footprint at an odd quarter. Quarter 0 is exactly the unturned factory.
public class FactoryRotationTests
{
    private static Unit SpawnTurned(World w, string id, int x, int y, int quarter)
    {
        var def = w.Player(1)!.Setup.Find(w.Content, id)!;
        return w.Unit(w.Spawn(def, new Cell(x, y), Facing.North, quarter))!;
    }

    // ArenaFor: a 128-cell arena whose player 1 has factory id (the barracks is the Resistance's, the military garage the Sentinel's).
    private static World ArenaFor(string id) => id == "military_garage" ? Arena.NewSentinel(128) : Arena.New(128);

    private static void Produce(World w, Unit f, string unit) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [f.Id], Unit = unit });

    private static List<Unit> Made(World w, string arch) => [.. w.Units.Where(u => u.Def.Archetype.Id == arch && u.Alive)];

    private static bool Within(Vec a, Vec b, int cells) => (a - b).LenSq() <= (long)cells * cells * Fixed.OneRaw * Fixed.OneRaw;

    private static void RunUntil(World w, Func<bool> done, int ticks, string what)
    {
        for (var t = 0; t < ticks && !done(); t++)
        {
            w.Step();
        }
        Assert.True(done(), $"not {what} in {ticks} ticks");
    }

    // Out is how far point p lies past f's front wall as it stands (raw; negative inside), the front for its quarter:
    // 0 south, 1 west, 2 north, 3 east.
    private static long Out(Unit f, Vec p)
    {
        var o = World.FootprintOrigin(f);
        long x0 = (long)o.X * Fixed.OneRaw, y0 = (long)o.Y * Fixed.OneRaw;
        long x1 = x0 + (long)World.FootW(f) * Fixed.OneRaw, y1 = y0 + (long)World.FootH(f) * Fixed.OneRaw;
        return f.Quarter switch { 0 => p.Y.Raw - y1, 1 => x0 - p.X.Raw, 2 => y0 - p.Y.Raw, _ => p.X.Raw - x1 };
    }

    // Across is p's offset along f's front wall (raw) from point e, the other axis to Out.
    private static long Across(Unit f, Vec p, Vec e) => f.Quarter % 2 == 0 ? p.X.Raw - e.X.Raw : p.Y.Raw - e.Y.Raw;

    [Theory]
    [InlineData("vehicle_garage", 0)]
    [InlineData("vehicle_garage", 1)]
    [InlineData("vehicle_garage", 2)]
    [InlineData("vehicle_garage", 3)]
    [InlineData("resistance_barracks", 1)]
    [InlineData("resistance_barracks", 2)]
    [InlineData("resistance_barracks", 3)]
    [InlineData("military_garage", 1)]
    [InlineData("military_garage", 2)]
    [InlineData("military_garage", 3)]
    public void EveryQuarterTurnsItsExitsBaysAndRallyToItsFront(string id, int quarter)
    {
        var w = ArenaFor(id);
        var f = SpawnTurned(w, id, 40, 40, quarter);
        var a = f.Def.Archetype;
        Assert.Equal(quarter, f.Quarter);
        Assert.Equal(quarter % 2 == 1 ? (a.FootprintH, a.FootprintW) : (a.FootprintW, a.FootprintH), (World.FootW(f), World.FootH(f)));
        Assert.Equal(new Cell(40, 40), World.FootprintOrigin(f));
        for (var i = 0; i < a.Production!.Doors; i++)
        {
            var e = World.ExitPoint(f, i);
            var b = World.BayPoint(f, i);
            Assert.True(Out(f, e) > 0, $"door {i}: exit {e} not past the front");
            Assert.True(Out(f, b) < 0, $"door {i}: bay {b} not inside");
            Assert.True(Math.Abs(Across(f, b, e)) < Fixed.OneRaw, $"door {i}: bay {b} not behind exit {e}");
            // The same distances as the unturned layout's.
            Assert.Equal((long)a.Production.Exits[i].Y * Fixed.OneRaw / 10 - (long)a.FootprintH * Fixed.OneRaw, Out(f, e));
        }
        Assert.True(Out(f, w.RallyCell(f).Center()) > 0);
        Assert.Equal(World.FrontFacing(f), Facing.Wrap(Facing.South + quarter * 8));
    }

    [Theory]
    [InlineData("vehicle_garage")]
    [InlineData("resistance_barracks")]
    [InlineData("military_garage")]
    public void QuarterZeroIsTheUnturnedFactory(string id)
    {
        var w = ArenaFor(id);
        var f = SpawnTurned(w, id, 40, 40, 0);
        var g = w.Spawn(1, id, 40, 80);
        for (var i = 0; i < f.Def.Archetype.Production!.Doors; i++)
        {
            var d = new Vec(Fixed.Zero, Fixed.FromInt(40));
            Assert.Equal(World.ExitPoint(f, i) + d, World.ExitPoint(g, i));
            Assert.Equal(World.BayPoint(f, i) + d, World.BayPoint(g, i));
        }
        Assert.Equal(w.RallyCell(f) with { Y = w.RallyCell(f).Y + 40 }, w.RallyCell(g));
    }

    [Fact]
    public void AGarageTurnedEastBuildsATruckThatLeavesByItsEastDoorAndReachesItsRally()
    {
        var w = Arena.New(64);
        var g = SpawnTurned(w, "vehicle_garage", 20, 20, 3); // the front to the east: x 20-30, y 20-39, the east wall at x 31
        Assert.Equal(Facing.East, World.FrontFacing(g));
        Assert.Equal(new Cell(36, 31), w.RallyCell(g)); // the art's rally (80, 160) turned: cell (8, 16) laid out, 5 cells out of the east wall
        Produce(w, g, "cargo_truck");
        RunUntil(w, () => Made(w, "cargo_truck").Count == 1, 600, "made");
        var truck = Made(w, "cargo_truck")[0];
        var door = Array.FindIndex(g.Doors, d => d.Vehicle == truck.Id);
        var exit = World.ExitPoint(g, door);
        Assert.True(Within(truck.Pos, World.BayPoint(g, door), 1)); // on its bay floor inside
        Assert.Equal(Facing.East, truck.Facing); // facing out of its door
        Assert.Equal(g.Id, truck.OutOf);
        var wall = 31L * Fixed.OneRaw;
        var passed = false;
        for (var t = 0; t < 300 && truck.OutOf != 0; t++)
        {
            w.Step();
            Assert.True(Math.Abs(truck.Pos.Y.Raw - exit.Y.Raw) < Fixed.OneRaw, $"tick {t}: {truck.Pos} off its lane"); // straight out east
            passed |= truck.Pos.X.Raw >= wall;
        }
        Assert.True(passed);
        Assert.Equal(0u, truck.OutOf);
        RunUntil(w, () => g.Doors[door].State == DoorState.Closed, 200, "the door closed");
        RunUntil(w, () => Within(truck.Pos, new Cell(36, 31).Center(), 3), 400, "at the rally");
    }

    [Fact]
    public void ATurnedBarracksTrainsASoldierWhoWalksOutOfItsNorthGate()
    {
        var w = Arena.New(64);
        var b = SpawnTurned(w, "resistance_barracks", 20, 30, 2); // the front to the north: x 20-35, y 30-41
        Assert.Equal(new Cell(27, 24), w.RallyCell(b)); // (80, 170) turned a half: as far north of the wall as unturned south of it
        Assert.Equal(new Vec(Fixed.FromInt(28), Fixed.FromInt(30) - Fixed.FromRaw(Fixed.OneRaw * 3 / 2)), World.ExitPoint(b, 0));
        Produce(w, b, "Rifleman");
        RunUntil(w, () => Made(w, "assault").Count == 1, 300, "trained");
        var s = Made(w, "assault")[0];
        Assert.True(Within(s.Pos, World.BayPoint(b, 0), 1)); // in the yard behind the gate
        Assert.Equal(Facing.North, s.Facing);
        RunUntil(w, () => s.OutOf == 0, 300, "out of the gate");
        Assert.True(s.Pos.Y.Raw < 30L * Fixed.OneRaw); // north of the wall
        RunUntil(w, () => Within(s.Pos, new Cell(27, 24).Center(), 3), 400, "at the rally");
        // Its wicket doors turned with it: on the north side too.
        for (var i = 0; i < b.Def.Archetype.Garrison!.Doors.Count; i++)
        {
            Assert.True(World.DoorCell(b, i).Y < 30, $"door {World.DoorCell(b, i)}");
        }
    }

    [Fact]
    public void ATurnedMilitaryGarageServicesThroughItsWestDoorsAndSendsItOutWest()
    {
        var w = Arena.NewSentinel(96, content: TestContent.ClassicSupply);
        var g = SpawnTurned(w, "military_garage", 50, 20, 1); // the front to the west: x 50-67, y 20-59
        w.Step();
        var car = w.SpawnCrewed(1, "humvee", 38, 40, Facing.North).Vehicle;
        car.HP = 150;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Service, Units = [car.Id], Target = g.Id });
        RunUntil(w, () => car.InGarage, 600, "in its bay");
        var (_, door, use) = w.DoorOf(car);
        Assert.Equal(DoorUse.Service, use);
        Assert.True(Out(g, World.ExitPoint(g, door)) > 0);
        RunUntil(w, () => !car.InGarage, 600, "out of its bay");
        RunUntil(w, () => car.OutOf == 0, 300, "out of the door");
        Assert.True(car.Pos.X.Raw < 50L * Fixed.OneRaw); // west of the wall
        Assert.Equal(car.Def.MaxHp, car.HP);
        RunUntil(w, () => (car.Pos - w.RallyCell(g).Center()).Len() < Fixed.FromInt(3), 400, "at the rally");
        Assert.True(w.RallyCell(g).X < 50);
    }

    [Theory]
    [InlineData(Facing.East)]
    [InlineData(Facing.West)]
    public void ANonSquareFactoryBuiltTurnedStandsAndBlocksItsTurnedCells(int facing)
    {
        var w = Arena.NewSentinel(64);
        var worker = w.Spawn(1, "worker", 14, 14);
        var garage = w.Player(1)!.Setup.Find(w.Content, "vehicle_garage")!;
        // 20 x 11 laid out, 11 x 20 turned: at (48, 20) only the turned one fits on a 64-cell map.
        Assert.False(w.CanPlace(garage, new Cell(48, 20), Facing.North, [worker]));
        Assert.True(w.CanPlace(garage, new Cell(48, 20), facing, [worker]));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = new Cell(20, 20), Unit = "vehicle_garage", Facing = facing });
        w.Step();
        var site = Assert.Single(w.Units, u => u.Def.Archetype.Id == "vehicle_garage");
        Assert.Equal(facing / 8, site.Quarter);
        Assert.True(site.Turned);
        Assert.Equal((11, 20), (World.FootW(site), World.FootH(site)));
        var depot = w.Player(1)!.Setup.Find(w.Content, "supply_depot")!;
        Assert.False(w.CanPlace(depot, new Cell(20, 34), 0, [worker])); // inside the turned footprint only
        Assert.True(w.CanPlace(depot, new Cell(33, 20), 0, [worker]));  // inside the unturned footprint only
        var r = w.Routing;
        r.SyncStructures(w.Units);
        var k = r.Nav.Class(0, 1);
        for (var y = 14; y < 46; y++)
        {
            for (var x = 14; x < 40; x++)
            {
                var inside = x >= 20 && x < 31 && y >= 20 && y < 40;
                if (inside)
                {
                    Assert.Equal(0, r.Nav.Cost(k, x, y));
                }
                else if (x < 19 || x > 31 || y < 19 || y > 40)
                {
                    Assert.NotEqual(0, r.Nav.Cost(k, x, y));
                }
            }
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(Facing.East, 1)]
    [InlineData(Facing.South, 2)]
    [InlineData(Facing.West, 3)]
    public void ASandboxSpawnTurnsAFactoryByItsFacing(int facing, int quarter)
    {
        var w = Arena.New(64, edit: s => s with { Scenario = SimCore.Scenarios.Scenario.SandboxName });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "vehicle_garage", Cell = new Cell(20, 20), Facing = facing });
        w.Step();
        var g = Assert.Single(w.Units, u => u.Def.Archetype.Id == "vehicle_garage");
        Assert.Equal(quarter, g.Quarter);
        if (quarter != 0)
        {
            Assert.Equal(new Cell(20, 20), World.FootprintOrigin(g)); // exactly where asked, its footprint as it stands
        }
    }

    [Fact]
    public void TurnedFactoriesProduceDeterministically()
    {
        static ulong Play()
        {
            var w = Arena.NewSentinel(96, 7);
            var g = SpawnTurned(w, "vehicle_garage", 20, 20, 1);
            var m = SpawnTurned(w, "military_garage", 50, 30, 2);
            for (var i = 0; i < 3; i++)
            {
                Produce(w, g, "technical");
                Produce(w, m, "humvee");
            }
            w.Run(1500);
            return w.Hash();
        }
        Assert.Equal(Play(), Play());
    }
}
