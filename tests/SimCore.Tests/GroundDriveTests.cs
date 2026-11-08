using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

/// <summary>Ground drive (docs/design.md, "Straight routes"; decisions 916 to 927): roads, lips, the map's edge, spinning.</summary>
public sealed class GroundDriveTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static int Class(string id) => Rules.Classes.Select((c, i) => (c, i)).First(x => x.c.Id == id).i;

    private static TerrainMap Ground(int w, int h, Func<int, int, int> f)
    {
        var corners = new short[(w + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        {
            for (var x = 0; x <= w; x++)
            {
                corners[y * (w + 1) + x] = (short)f(x, y);
            }
        }
        return new TerrainMap(w, h, corners, new byte[w * h], Rules);
    }

    private const int RawPerMetre = 512;

    private static int Step(int x, int at, int metres100) => x > at ? (metres100 * RawPerMetre + 99) / 100 : 0;

    // ---- 1. Road pull ------------------------------------------------------------------------------------

    [Fact]
    public void AVehiclesRoadCostIsTheTimeTheRoadSaves()
    {
        foreach (var mc in Rules.Classes.Where(c => c.Locomotor != Locomotor.Foot))
        {
            for (var kind = 0; kind < MoveClass.RoadKinds; kind++)
            {
                Assert.InRange(mc.RoadCost[kind] * mc.RoadSpeed[kind], 9900, 10100); // within rounding of 100 / speed
            }
            Assert.Equal(180, mc.ShortTrip);
        }
        Assert.Equal(0, Rules.Classes[Class("foot")].ShortTrip);
    }

    // RoadMap: flat, a highway 3 wide along row y.
    private static TerrainMap RoadMap(int w, int h, int y)
    {
        var t = TerrainMap.Flat(w, h, Rules);
        for (var x = 0; x < w; x++)
        {
            for (var r = y - 1; r <= y + 1; r++)
            {
                t.SetRoadKind(new Cell(x, r), RoadKind.Highway);
            }
        }
        return t;
    }

    private static int RowsOff(List<Cell> cells, int row) => cells.Count == 0 ? -1 : cells.Max(c => Math.Abs(c.Y - row));

    [Fact]
    public void AShortTripIgnoresARoadAndUsesOneNearlyOnTheWay()
    {
        var w = Arena.NewSentinel(400);
        w.AttachTerrain(RoadMap(400, 400, 22));
        var humvee = w.Player(1)!.Setup.Find(w.Content, "humvee")!;
        // 12 rows off the line: 150 cells straight, whatever a highway would save.
        var off = w.RouteCellsFor(humvee, new Cell(10, 10), new Cell(160, 10));
        Assert.Equal(0, RowsOff(off, 10));
        // Its edge 4 rows off the line: nearly on the way, and 25% faster: taken.
        var near = w.RouteCellsFor(humvee, new Cell(10, 17), new Cell(160, 17));
        Assert.Contains(near, c => w.Ground!.RoadKindAt(c) == RoadKind.Highway);
        // A long trip takes the far road when the time it saves pays for the way there.
        var far = w.RouteCellsFor(humvee, new Cell(10, 10), new Cell(380, 10));
        Assert.Contains(far, c => w.Ground!.RoadKindAt(c) == RoadKind.Highway);
    }

    [Fact]
    public void AShortTripGoesRoundAHillNotOverIt()
    {
        // A gentle hill (15%) across a short trip's line down to row 43, flat beyond: the line is clear, but dearer than the way round.
        static int H(int x, int y) => y >= 44 || x < 40 || x > 70 ? 0 : (15 - Math.Abs(x - 55)) * 154;
        var t = Ground(120, 120, H);
        Assert.True(t.Passable(Class("heavy_wheeled"), new Cell(50, 30)));
        var w = Arena.NewSentinel(120);
        w.AttachTerrain(t);
        var truck = w.Player(1)!.Setup.Find(w.Content, "sentinel_cargo_truck")!;
        var cells = w.RouteCellsFor(truck, new Cell(20, 30), new Cell(90, 30));
        Assert.Equal(new Cell(90, 30), cells[^1]);
        Assert.True(cells.Max(c => c.Y) >= 44, $"over the hill, row {cells.Max(c => c.Y)} at most");
    }

    [Fact]
    public void AGroupsVehiclesIgnoreARoadOnAShortTrip()
    {
        var w = Arena.NewSentinel(200);
        w.AttachTerrain(RoadMap(200, 200, 24));
        var column = new[] { w.SpawnCrewed(1, "humvee", 10, 8, Facing.East).Vehicle, w.SpawnCrewed(1, "humvee", 10, 12, Facing.East).Vehicle };
        w.Order(1, CommandKind.Move, column, cell: new Cell(120, 10));
        var far = 0;
        for (var t = 0; t < 1200 && column.Any(u => u.Moving || u.PathPending || t < 5); t++)
        {
            w.Step();
            far = Math.Max(far, column.Max(u => Cell.Of(u.Pos).Y));
        }
        Assert.True(far <= 18, $"strayed to row {far}");
    }

    // ---- 2. Lips -----------------------------------------------------------------------------------------

    [Fact]
    public void AnEightyCentimetreLipReadsFlatForEveryClass()
    {
        var t = Ground(40, 20, (x, _) => Step(x, 20, 80));
        var lip = new Cell(20, 10);
        Assert.Equal(40, t.Grade(lip)); // its own grade: 80 cm over its 2 m
        Assert.True(t.MoveGrade(lip) < 10, $"reads {t.MoveGrade(lip)}");
        for (var k = 0; k < Rules.Classes.Count; k++)
        {
            Assert.Equal(10, t.Cost(k, lip));
            Assert.Equal(100, t.SpeedPercent(k, lip, uphill: true));
            Assert.Equal(100, t.SpeedPercent(k, lip, uphill: false));
            // No climb either, going onto it or off it: its cost is its flat neighbours'.
            Assert.Equal(10, t.StepCost(k, new Cell(19, 10), lip));
            Assert.Equal(10, t.StepCost(k, lip, new Cell(21, 10)));
        }
    }

    [Fact]
    public void WhatBuildingAndDeployingReadIsStillTheCellsOwnGrade()
    {
        var t = Ground(40, 20, (x, _) => Step(x, 20, 80));
        var lip = new Cell(20, 10);
        Assert.Equal(40, t.Grade(lip));
        Assert.Equal(SlopeClass.Steep, t.Slope(Class("tracked"), lip));
        Assert.Equal(SlopeClass.Cliff, t.Slope(Class("heavy_wheeled"), lip)); // past the trucks' 38%
    }

    [Fact]
    public void AStepOverTheLipStaysAWall()
    {
        var t = Ground(40, 20, (x, _) => Step(x, 20, 100)); // a metre: 50%
        Assert.Equal(50, t.MoveGrade(new Cell(20, 10)));
        for (var k = 0; k < Rules.Classes.Count; k++)
        {
            Assert.False(t.Passable(k, new Cell(20, 10)));
        }
    }

    [Fact]
    public void ASustainedHillsideKeepsItsClassAndANoisyOneItsMean()
    {
        // 40% for 30 cells: steep inside, a wall to trucks (their cliff is 38%).
        var steep = Ground(60, 20, (x, _) => Math.Clamp(x - 15, 0, 30) * 410);
        for (var x = 17; x < 43; x++)
        {
            Assert.True(steep.MoveGrade(new Cell(x, 10)) >= 25, $"x {x} reads {steep.MoveGrade(new Cell(x, 10))}");
            Assert.False(steep.Passable(Class("heavy_wheeled"), new Cell(x, 10)));
        }
        Assert.True(steep.Passable(Class("foot"), new Cell(30, 10)));
        Assert.Equal(60, steep.SpeedPercent(Class("foot"), new Cell(30, 10), uphill: true));

        // 20% with a bump on it: a cell 28% by itself reads as the slope it is on.
        var noisy = Ground(60, 20, (x, _) => x * 205 + (x == 30 ? 82 : 0));
        Assert.True(noisy.Grade(new Cell(29, 10)) >= 25);
        Assert.InRange(noisy.MoveGrade(new Cell(29, 10)), 10, 24);
        Assert.True(noisy.Passable(Class("heavy_wheeled"), new Cell(29, 10)));
    }

    [Fact]
    public void AShortBankStaysSteep()
    {
        // 2.4 m in 4 cells at 30%: the wide window reads 24% (5 cells), the narrower one 30% less the slack: not a lip, Steep to
        // movement (the trucks crawl it since the wheeled climb).
        var t = Ground(40, 20, (x, _) => Math.Clamp(x - 18, 0, 4) * 307);
        Assert.Contains(Enumerable.Range(18, 4), x => Rules.Classes[Class("heavy_wheeled")].Classify(t.MoveGrade(new Cell(x, 10))) == SlopeClass.Steep);
    }

    [Fact]
    public void ATruckDrivesOverALipAtFullSpeedBothWays()
    {
        foreach (var (from, to, face) in new[] { (5, 35, Facing.East), (35, 5, Facing.West) })
        {
            var w = Arena.NewSentinel(40);
            w.AttachTerrain(Ground(40, 40, (x, _) => Step(x, 20, 80)));
            var u = w.SpawnCrewed(1, "sentinel_cargo_truck", from, 20, face).Vehicle;
            w.Order(1, CommandKind.Move, [u], cell: new Cell(to, 20));
            var least = long.MaxValue;
            for (var t = 0; t < 800 && (t < 5 || u.Moving); t++)
            {
                w.Step();
                var x = Cell.Of(u.Pos).X;
                if (Math.Abs(x - 20) <= 3)
                {
                    least = Math.Min(least, u.DriveSpeed.Raw);
                }
                Assert.False(u.Intent.Stuck, $"stuck at {u.Pos}");
            }
            Assert.Equal(new Cell(to, 20), Cell.Of(u.Pos));
            Assert.InRange(least, u.Def.Speed.Raw * 95 / 100, u.Def.Speed.Raw);
        }
    }

    [Fact]
    public void AChangedHeightReadsAsAFreshMapWould()
    {
        // WriteCorners and SetPad rebuild what movement reads round the change: as a map built with those heights reads it.
        var changed = Ground(40, 40, (x, _) => Step(x, 20, 60));
        changed.WriteCorners(10, 10, 3, 3, [50, 50, 50, 50, 300, 50, 50, 50, 50], null);
        var fresh = Ground(40, 40, (x, y) => (x, y) == (11, 11) ? 300 : x is >= 10 and <= 12 && y is >= 10 and <= 12 ? 50 : Step(x, 20, 60));
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                var c = new Cell(x, y);
                Assert.Equal(fresh.MoveGrade(c), changed.MoveGrade(c));
                Assert.Equal(fresh.Cost(Class("heavy_wheeled"), c), changed.Cost(Class("heavy_wheeled"), c));
            }
        }
    }

    [Fact]
    public void TheLipRuleIsHashedAndOffWithoutIt()
    {
        const string Off = """{ "classes": [ { "id": "wheeled", "locomotor": "wheeled", "slopes": { "gentle": 10, "steep": 25, "cliff": 45 }, "speedUp": [100, 75, 0, 0], "speedDown": [100, 100, 0, 0], "cost": [10, 14, 0, 0], "uphillCost": 50, "forestCost": [100, 130, 170, 250], "forestSpeed": [100, 85, 60, 40] } ] }""";
        var off = TerrainLoader.ParseRules(Off);
        var on = TerrainLoader.ParseRules(Off.Replace("{ \"classes\"", "{ \"lips\": { \"window\": 5, \"rise\": 40, \"slack\": 4 }, \"classes\""));
        Assert.NotEqual(off.Digest(), on.Digest());
        var corners = new short[41 * 21];
        for (var y = 0; y <= 20; y++)
        {
            for (var x = 21; x <= 40; x++)
            {
                corners[y * 41 + x] = 410; // 80 cm
            }
        }
        Assert.Equal(40, new TerrainMap(40, 20, corners, new byte[800], off).MoveGrade(new Cell(20, 10)));
        Assert.True(new TerrainMap(40, 20, corners, new byte[800], on).MoveGrade(new Cell(20, 10)) < 10);
        Assert.Throws<ContentException>(() => TerrainLoader.ParseRules(Off.Replace("{ \"classes\"", "{ \"lips\": { \"window\": 4, \"rise\": 40 }, \"classes\"")));
    }

    // ---- 3. The map's edge -------------------------------------------------------------------------------

    [Fact]
    public void TheEdgeOfTheMapIsOpenToWhatFitsThere()
    {
        var t = TerrainMap.Flat(100, 100, Rules);
        var w = Arena.NewSentinel(100);
        w.AttachTerrain(t);
        var r = w.Routing;
        int Rows(string kind, Cell c) => r.Nav.Cost(r.ClassOf(w.Player(1)!.Setup.Find(w.Content, kind)!), c.X, c.Y);
        // A soldier stands on any edge cell; a truck (2 wide, centred on its cell's far corner) on the north and west ones, a tank (3 wide) one in.
        foreach (var c in new[] { new Cell(0, 50), new Cell(99, 50), new Cell(50, 0), new Cell(50, 99) })
        {
            Assert.NotEqual(0, Rows("Rifleman", c));
            Assert.Equal(0, Rows("main_battle_tank", c));
        }
        Assert.NotEqual(0, Rows("sentinel_cargo_truck", new Cell(0, 50)));
        Assert.NotEqual(0, Rows("sentinel_cargo_truck", new Cell(50, 0)));
        Assert.NotEqual(0, Rows("sentinel_cargo_truck", new Cell(98, 50)));
        Assert.NotEqual(0, Rows("main_battle_tank", new Cell(1, 50)));
        Assert.NotEqual(0, Rows("main_battle_tank", new Cell(98, 50)));
    }

    [Theory]
    [InlineData("Rifleman", 1.1)]
    [InlineData("humvee", 2.0)]
    [InlineData("sentinel_cargo_truck", 2.0)]
    [InlineData("main_battle_tank", 2.5)]
    public void AUnitOrderedToTheEdgeOfTheMapGetsThere(string kind, double within)
    {
        foreach (var (start, goal, face) in new[]
        {
            (new Cell(50, 30), new Cell(50, 0), Facing.North), (new Cell(50, 70), new Cell(50, 99), Facing.South),
            (new Cell(30, 50), new Cell(0, 50), Facing.West), (new Cell(70, 50), new Cell(99, 50), Facing.East),
        })
        {
            var w = Arena.NewSentinel(100);
            w.AttachTerrain(TerrainMap.Flat(100, 100, Rules));
            var u = kind == "Rifleman" ? w.Spawn(1, kind, start.X, start.Y, face) : w.SpawnCrewed(1, kind, start.X, start.Y, face).Vehicle;
            w.Order(1, CommandKind.Move, [u], cell: goal);
            for (var t = 0; t < 800 && (t < 5 || u.Moving); t++)
            {
                w.Step();
            }
            double x = u.Pos.X.Raw / (double)Fixed.OneRaw, y = u.Pos.Y.Raw / (double)Fixed.OneRaw;
            var edge = Math.Min(Math.Min(x, y), Math.Min(100 - x, 100 - y));
            Assert.True(edge <= within, $"{kind} stopped {edge:F2} cells from the edge, going to {goal}");
            Assert.True(u.Def.Archetype.Radius.Raw / (double)Fixed.OneRaw <= edge + 0.01, $"{kind} overhangs the edge at {edge:F2}");
        }
    }

    // ---- 4. Spinning -------------------------------------------------------------------------------------

    private static double Degrees(Vec a, Vec b)
    {
        var d = Math.Atan2(b.Y.Raw, b.X.Raw) - Math.Atan2(a.Y.Raw, a.X.Raw);
        while (d > Math.PI)
        {
            d -= 2 * Math.PI;
        }
        while (d < -Math.PI)
        {
            d += 2 * Math.PI;
        }
        return Math.Abs(d) * 180 / Math.PI;
    }

    [Theory]
    [InlineData("humvee", 5.2)]      // 90 degrees a second: 4.5 a tick, and what it snaps onto the line by
    [InlineData("sentinel_cargo_truck", 5.2)]
    [InlineData("main_battle_tank", 3.6)] // 60: 3
    public void AVehicleTurnsRoundABendNotOnTheSpot(string kind, double most)
    {
        var w = Arena.NewSentinel(100);
        var u = w.SpawnCrewed(1, kind, 50, 50, Facing.East).Vehicle;
        w.Order(1, CommandKind.Move, [u], cell: new Cell(20, 50)); // behind it
        var h = u.Heading;
        double turned = 0;
        var flips = 0;
        var last = 0;
        for (var t = 0; t < 900 && (t < 5 || u.Moving); t++)
        {
            w.Step();
            var d = Degrees(h, u.Heading);
            Assert.True(d <= most, $"{d:F1} degrees in one tick at tick {t}");
            var side = Math.Sign(h.X.Raw * u.Heading.Y.Raw - h.Y.Raw * u.Heading.X.Raw);
            flips += d > 0.5 && side != 0 && last == -side ? 1 : 0;
            last = d > 0.5 && side != 0 ? side : last;
            turned += d;
            h = u.Heading;
            Assert.False(u.Intent.Stuck);
        }
        Assert.Equal(new Cell(20, 50), Cell.Of(u.Pos));
        Assert.InRange(turned, 150, 300); // a half turn and what it takes to come back to the line it swung off
        Assert.True(flips <= 4, $"{flips} flips");
    }

    [Theory]
    [InlineData("humvee")]
    [InlineData("main_battle_tank")]
    public void AVehicleNeverOrbitsAPointInsideItsTurningCircle(string kind)
    {
        // Two cells off its beam at full speed: it must slow into the turn, not circle.
        var w = Arena.NewSentinel(100);
        var u = w.SpawnCrewed(1, kind, 20, 50, Facing.East).Vehicle;
        w.Order(1, CommandKind.Move, [u], cell: new Cell(70, 50));
        w.Run(120);
        var at = Cell.Of(u.Pos);
        w.Order(1, CommandKind.Move, [u], cell: new Cell(at.X + 1, at.Y + 2));
        var turned = 0.0;
        var h = u.Heading;
        for (var t = 0; t < 400 && (t < 5 || u.Moving); t++)
        {
            w.Step();
            turned += Degrees(h, u.Heading);
            h = u.Heading;
        }
        Assert.False(u.Moving, "it never got there");
        Assert.True(Math.Abs(u.Pos.X.Raw / 1024.0 - at.X - 1.5) <= 1 && Math.Abs(u.Pos.Y.Raw / 1024.0 - at.Y - 2.5) <= 1, $"it stopped at {u.Pos}");
        Assert.True(turned < 400, $"turned {turned:F0} degrees");
    }

    [Fact]
    public void AGroupsVehiclesCrossOpenGroundWithoutASwing()
    {
        // A new stretch of the flow field used to take a tick to look ahead: the heading swung toward its first cell and back.
        var w = Arena.NewSentinel(200);
        var column = Enumerable.Range(0, 4).Select(i => w.SpawnCrewed(1, i % 2 == 0 ? "humvee" : "main_battle_tank", 10, 80 + i * 8, Facing.East).Vehicle).ToArray();
        w.Order(1, CommandKind.Move, column, cell: new Cell(180, 92));
        var heads = column.Select(u => u.Heading).ToArray();
        var flips = new int[column.Length];
        var lastTurn = new int[column.Length];
        var facing = column.Select(u => u.Facing).ToArray();
        for (var t = 0; t < 1800 && (t < 5 || column.Any(u => u.Moving || u.PathPending)); t++)
        {
            w.Step();
            for (var i = 0; i < column.Length; i++)
            {
                var u = column[i];
                if (t > 40 && t < 500)
                {
                    Assert.True(Degrees(heads[i], u.Heading) <= 5.2, $"unit {i}: a swing of {Degrees(heads[i], u.Heading):F1} at tick {t}");
                }
                if (u.Facing != facing[i])
                {
                    var dir = ((u.Facing - facing[i] + 48) % 32) - 16 > 0 ? 1 : -1;
                    flips[i] += lastTurn[i] == -dir ? 1 : 0;
                    lastTurn[i] = dir;
                    facing[i] = u.Facing;
                }
                heads[i] = u.Heading;
            }
        }
        Assert.All(flips, f => Assert.True(f <= 2, $"{f} flips crossing open ground"));
    }

    [Fact]
    public void AVehicleKeepsItsCourseForASoldierCrossingAndAVehicleAhead()
    {
        var w = Arena.NewSentinel(160);
        var u = w.SpawnCrewed(1, "main_battle_tank", 10, 50, Facing.East).Vehicle;
        var ahead = w.SpawnCrewed(1, "sentinel_cargo_truck", 22, 52, Facing.East).Vehicle;
        var walkers = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 40, 44 + i, Facing.South)).ToArray();
        w.Order(1, CommandKind.Move, [u], cell: new Cell(120, 50));
        w.Order(1, CommandKind.Move, [ahead], cell: new Cell(120, 52));
        w.Order(1, CommandKind.Move, walkers, cell: new Cell(40, 70));
        var h = u.Heading;
        double turned = 0;
        for (var t = 0; t < 500; t++)
        {
            w.Step();
            if (t > 30)
            {
                turned += Degrees(h, u.Heading);
            }
            h = u.Heading;
        }
        Assert.True(turned < 12, $"turned {turned:F1} degrees on a straight line past them");
    }

    [Fact]
    public void AVehicleStillGoesRoundOneStandingInItsWay()
    {
        var w = Arena.NewSentinel(120);
        var u = w.SpawnCrewed(1, "humvee", 10, 50, Facing.East).Vehicle;
        w.SpawnCrewed(1, "main_battle_tank", 40, 50, Facing.North);
        w.Order(1, CommandKind.Move, [u], cell: new Cell(80, 50));
        for (var t = 0; t < 800 && (t < 5 || u.Moving); t++)
        {
            w.Step();
        }
        Assert.Equal(new Cell(80, 50), Cell.Of(u.Pos));
    }

    private static (ulong Hash, double FlipsPerVehicle, double Turned) Battle()
    {
        var w = Arena.NewSentinel(160);
        var mine = new List<Unit>();
        for (var i = 0; i < 8; i++)
        {
            mine.Add(w.SpawnCrewed(1, i < 4 ? "humvee" : "main_battle_tank", i < 4 ? 30 : 22, 50 + (i % 4) * 7, Facing.East).Vehicle);
        }
        for (var i = 0; i < 10; i++)
        {
            w.Spawn(2, "Rifleman", 95 + (i % 3) * 5, 50 + (i % 4) * 6, Facing.West);
        }
        w.Order(1, CommandKind.AttackMove, mine, cell: new Cell(110, 60));
        var facing = mine.Select(u => u.Facing).ToArray();
        var last = new int[8];
        var flips = 0;
        double turned = 0;
        for (var t = 0; t < 900; t++)
        {
            w.Step();
            for (var i = 0; i < 8; i++)
            {
                var u = mine[i];
                if (u.Alive && u.Facing != facing[i])
                {
                    var d = ((u.Facing - facing[i] + 48) % 32) - 16;
                    var dir = d > 0 ? 1 : -1;
                    flips += last[i] == -dir ? 1 : 0;
                    last[i] = dir;
                    turned += Math.Abs(d) * 11.25;
                    facing[i] = u.Facing;
                }
            }
        }
        return (w.Hash(), flips / 8.0, turned / 8);
    }

    [Fact]
    public void EightVehiclesAttackMovingHoldTheirHeadingsAndPlayTheSameTwice()
    {
        var a = Battle();
        Assert.Equal(a.Hash, Battle().Hash);
        // 45 s of a fight on open ground: a vehicle flips about once in 10 s at the worst (it was once in 3).
        Assert.True(a.FlipsPerVehicle <= 5, $"{a.FlipsPerVehicle:F1} flips a vehicle");
        Assert.True(a.Turned <= 400, $"{a.Turned:F0} degrees a vehicle");
    }
}
