using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimCore.Tests;

// Turned buildings (docs/design.md, "Turned buildings"): every town building, home, farm building, stone village house,
// garrisonable block and prop, and the observation post, stands at any of the four quarter turns of its layout
// (Unit.Quarter: 0 as authored, the front to the south; 1 west; 2 north; 3 east), spawned (World.Spawn with a quarter,
// SandboxSpawn's facing) or, the post, built (the Build facing). The footprint swaps width and height at an odd quarter;
// the doors, garrison entry, exits, collapse, the cells blocked for good (rubble too) and a rally on its front follow the
// turn. A structure placed without a turn plays and hashes exactly as before.
public class BuildingRotationTests
{
    private const int At = 20; // every building's top-left cell (x and y) as it stands

    private static World Sandbox(int size = 64) => Arena.New(size, edit: s => s with { Scenario = Scenario.SandboxName });

    private static void SandboxSpawn(World w, byte player, string unit, int x, int y, int facing = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.SandboxSpawn, Unit = unit, Cell = new Cell(x, y), Facing = facing });

    private static Unit SpawnTurned(World w, string id, int x, int y, int quarter, byte player = 1)
    {
        var def = w.Player(player)!.Setup.Find(w.Content, id)!;
        return w.Unit(w.Spawn(def, new Cell(x, y), Facing.South, quarter))!;
    }

    // Every building placed neutral (the homes, town buildings, stone village houses, farm buildings, the garrisonable
    // blocks and the props, which hold nobody) at each quarter.
    public static TheoryData<string, int> CivilianAtEveryQuarter()
    {
        var data = new TheoryData<string, int>();
        foreach (var a in TestContent.Mvp.Archetypes.Where(a => a.PlacedNeutral))
        {
            for (var q = 0; q < 4; q++)
            {
                data.Add(a.Id, q);
            }
        }
        return data;
    }

    // Where a door on the front row (content cell (x, h) of a w x h layout) stands with the layout turned quarter, from
    // the top-left cell of the footprint as it stands: below its bottom row, left of its left column, above its top row,
    // right of its right column.
    private static Cell FrontDoor(Archetype a, (int X, int Y) d, int quarter) => quarter switch
    {
        0 => new Cell(At + d.X, At + a.FootprintH),
        1 => new Cell(At - 1, At + d.X),
        2 => new Cell(At + a.FootprintW - 1 - d.X, At - 1),
        _ => new Cell(At + a.FootprintH, At + a.FootprintW - 1 - d.X),
    };

    // One cell further out from the front than the door.
    private static Cell Beyond(Cell door, int quarter) => quarter switch
    {
        0 => new Cell(door.X, door.Y + 1),
        1 => new Cell(door.X - 1, door.Y),
        2 => new Cell(door.X, door.Y - 1),
        _ => new Cell(door.X + 1, door.Y),
    };

    // A cell behind the building (the side away from its front), on the line through its middle.
    private static Cell Behind(Unit b, int quarter)
    {
        var o = World.FootprintOrigin(b);
        int w = World.FootW(b), h = World.FootH(b);
        return quarter switch
        {
            0 => new Cell(o.X + w / 2, o.Y - 4),
            1 => new Cell(o.X + w + 3, o.Y + h / 2),
            2 => new Cell(o.X + w / 2, o.Y + h + 3),
            _ => new Cell(o.X - 4, o.Y + h / 2),
        };
    }

    private static bool InFootprint(Unit b, Cell c)
    {
        var o = World.FootprintOrigin(b);
        return c.X >= o.X && c.X < o.X + World.FootW(b) && c.Y >= o.Y && c.Y < o.Y + World.FootH(b);
    }

    private static bool Within(Vec a, Vec b, long r) => (a - b).LenSq() <= r * r;

    // Every cell of the footprint as it stands (fw x fh from (At, At)) blocks ground units and the ring round it does not.
    private static void BlocksExactly(World w, int fw, int fh)
    {
        for (var y = At - 1; y <= At + fh; y++)
        {
            for (var x = At - 1; x <= At + fw; x++)
            {
                var inside = x >= At && x < At + fw && y >= At && y < At + fh;
                Assert.True(inside != w.Grid.Passable(new Cell(x, y)), $"({x}, {y}) inside {inside}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(CivilianAtEveryQuarter))]
    public void EveryBuildingStandsAtEveryQuarterWithItsFootprintDoorsAndBlockedCellsTurned(string id, int quarter)
    {
        var w = Arena.New(64);
        var a = w.Content.Archetype(id)!;
        var b = SpawnTurned(w, id, At, At, quarter);
        var (fw, fh) = quarter % 2 == 1 ? (a.FootprintH, a.FootprintW) : (a.FootprintW, a.FootprintH);

        Assert.Equal(quarter, b.Quarter);
        Assert.Equal(quarter % 2 == 1 && a.FootprintW != a.FootprintH, b.Turned);
        Assert.Equal((fw, fh), (World.FootW(b), World.FootH(b)));
        Assert.Equal(new Cell(At, At), World.FootprintOrigin(b));
        Assert.Equal(0, b.Owner);
        Assert.Equal(Facing.Wrap(Facing.South + quarter * 8), World.FrontFacing(b));
        BlocksExactly(w, fw, fh);

        // Routing stamps the same cells.
        var r = w.Routing;
        r.SyncStructures(w.Units);
        var k = r.Nav.Class(0, 1);
        Assert.Equal(0, r.Nav.Cost(k, At, At));
        Assert.Equal(0, r.Nav.Cost(k, At + fw - 1, At + fh - 1));
        Assert.NotEqual(0, r.Nav.Cost(k, At + fw + 1, At + fh + 1));

        // The doors: on the front row as content lays them out, turned with the building, each on open ground with open
        // ground beyond it (the clear ground in front).
        if (a.Garrison is { } g)
        {
            for (var i = 0; i < g.Doors.Count; i++)
            {
                var d = g.Doors[i];
                Assert.Equal(a.FootprintH, d.Y); // every civilian door is on the front row
                var door = World.DoorCell(b, i);
                Assert.Equal(FrontDoor(a, d, quarter), door);
                Assert.Equal(door, World.LayoutCell(a, new Cell(At, At), quarter, d.X, d.Y));
                Assert.False(InFootprint(b, door));
                Assert.True(w.Grid.Passable(door));
                Assert.True(w.Grid.Passable(Beyond(door, quarter)));
            }
        }
    }

    [Theory]
    [MemberData(nameof(CivilianAtEveryQuarter))]
    public void ASoldierFromBehindGoesRoundToTheDoorsSideAndInAndOutByTheDoor(string id, int quarter)
    {
        var w = Arena.New(64);
        var a = w.Content.Archetype(id)!;
        var b = SpawnTurned(w, id, At, At, quarter);
        if (a.Garrison is not { } g)
        {
            Assert.False(w.CanGarrison(w.Spawn(1, "Rifleman", 2, 2), b)); // a prop holds nobody
            return;
        }
        var start = Behind(b, quarter);
        var s = w.Spawn(1, "Rifleman", start.X, start.Y);
        w.Order(1, CommandKind.Enter, [s], b.Id);
        var last = s.Pos;
        for (var t = 0; t < 1500 && s.Garrison == 0; t++)
        {
            last = s.Pos;
            w.Step();
            Assert.False(s.Garrison == 0 && InFootprint(b, Cell.Of(s.Pos)), $"tick {t}: inside the footprint at {Cell.Of(s.Pos).X}, {Cell.Of(s.Pos).Y}");
        }
        Assert.Equal(b.Id, s.Garrison);
        Assert.Equal(1, b.Owner);
        // It went in from beside a door, on the front.
        var door = World.DoorCell(b, World.NearestDoor(b, last));
        Assert.True(Within(last, door.Center(), g.EnterRange.Raw + Fixed.OneRaw), $"went in from {Cell.Of(last).X}, {Cell.Of(last).Y}, the door at {door.X}, {door.Y}");

        // Out by the door nearest the clicked cell, onto it.
        w.Order(1, CommandKind.Exit, [s], cell: Beyond(World.DoorCell(b, 0), quarter));
        w.Step();
        Assert.Equal(0u, s.Garrison);
        Assert.Equal(World.DoorCell(b, 0), Cell.Of(s.Pos));
    }

    [Theory]
    [MemberData(nameof(CivilianAtEveryQuarter))]
    public void DestroyedItsOccupantsComeOutAtTheTurnedDoorAndItsRubbleBlocksTheTurnedCells(string id, int quarter)
    {
        var w = Arena.New(64);
        var a = w.Content.Archetype(id)!;
        var b = SpawnTurned(w, id, At, At, quarter);
        var s = w.Spawn(1, "Rifleman", 2, 2);
        if (a.Garrison is not null)
        {
            World.EnterBuilding(s, b);
        }
        w.Step();
        b.HP = 0;
        w.Step();
        Assert.Null(w.Unit(b.Id));
        if (a.Garrison is not null)
        {
            Assert.Equal(0u, s.Garrison);
            Assert.Equal(World.DoorCell(b, 0), Cell.Of(s.Pos)); // the first one out stands on the door (alive or not)
        }
        BlocksExactly(w, World.FootW(b), World.FootH(b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ALongBlockTurnedIsAWallSoldiersGoRound(int quarter)
    {
        // The panel block, 23 x 6: unturned a wall across x, turned a quarter a wall down y.
        var w = Arena.New(64);
        var b = SpawnTurned(w, "block_panel_5", At, At, quarter);
        var o = World.FootprintOrigin(b);
        int fw = World.FootW(b), fh = World.FootH(b);
        var (from, to) = quarter % 2 == 0
            ? (new Cell(o.X + fw / 2, o.Y - 5), new Cell(o.X + fw / 2, o.Y + fh + 5))
            : (new Cell(o.X - 5, o.Y + fh / 2), new Cell(o.X + fw + 5, o.Y + fh / 2));
        var s = w.Spawn(1, "Rifleman", from.X, from.Y);
        w.Order(1, CommandKind.Move, [s], cell: to);
        for (var t = 0; t < 2000 && Cell.Of(s.Pos) != to; t++)
        {
            w.Step();
            Assert.False(InFootprint(b, Cell.Of(s.Pos)));
        }
        Assert.Equal(to, Cell.Of(s.Pos));
    }

    [Theory]
    [InlineData(Facing.North, 0, 5, 4)]
    [InlineData(Facing.East, 1, 4, 5)]
    [InlineData(Facing.South, 2, 5, 4)]
    [InlineData(Facing.West, 3, 4, 5)]
    public void TheObservationPostIsBuiltAtEveryFacingWithItsDoorTurned(int facing, int quarter, int fw, int fh)
    {
        var w = Arena.New(64);
        var workers = Enumerable.Range(0, 2).Select(i => w.Spawn(1, "worker", 14, 20 + i)).ToList();
        var def = w.Player(1)!.Setup.Find(w.Content, "observation_post")!;
        Assert.True(w.CanPlace(def, new Cell(At, At), facing, workers));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [.. workers.Select(u => u.Id)], Cell = new Cell(At, At), Unit = "observation_post", Facing = facing });
        w.Step();
        var post = Assert.Single(w.All("observation_post"));
        Assert.Equal((quarter, facing), (post.Quarter, post.Facing));
        Assert.Equal((fw, fh), (World.FootW(post), World.FootH(post)));
        // Its door is at its back as laid out (content [2, -1]: above its top row, 2 in from the left), turned with it.
        var door = quarter switch
        {
            0 => new Cell(At + 2, At - 1),
            1 => new Cell(At + fw, At + 2),
            2 => new Cell(At + 5 - 1 - 2, At + fh),
            _ => new Cell(At - 1, At + 5 - 1 - 2),
        };
        Assert.Equal(door, World.DoorCell(post, 0));
        w.Run(600);
        Assert.True(post.IsBuilt);

        // Its own soldier, from the side away from the door, goes round and in by the turned door.
        var (cx, cy) = (At + fw / 2, At + fh / 2);
        var start = new Cell(2 * cx - door.X + 2 * Math.Sign(cx - door.X), 2 * cy - door.Y + 2 * Math.Sign(cy - door.Y));
        var s = w.Spawn(1, "Rifleman", start.X, start.Y);
        w.Order(1, CommandKind.Enter, [s], post.Id);
        var last = s.Pos;
        for (var t = 0; t < 600 && s.Garrison == 0; t++)
        {
            last = s.Pos;
            w.Step();
        }
        Assert.Equal(post.Id, s.Garrison);
        Assert.True(Within(last, door.Center(), post.Def.Archetype.Garrison!.EnterRange.Raw + Fixed.OneRaw));
    }

    [Fact]
    public void TheSandboxTurnsABuildingByTheCommandsFacing()
    {
        var w = Sandbox();
        SandboxSpawn(w, 1, "farmhouse_stone", 10, 10, Facing.East);   // 7 x 5 turned: 5 x 7, the front west
        SandboxSpawn(w, 1, "warehouse", 30, 10, Facing.South);        // 12 x 8, a half turn: the front north
        SandboxSpawn(w, 1, "greenhouse", 10, 30, Facing.West);        // a prop, 10 x 4 turned: 4 x 10
        SandboxSpawn(w, 1, "house_small", 30, 30, 4);                 // not a quarter facing: nothing
        w.Step();
        var farm = w.All("farmhouse_stone").Single();
        Assert.Equal((1, true, 5, 7), (farm.Quarter, farm.Turned, World.FootW(farm), World.FootH(farm)));
        Assert.Equal(new Cell(10, 10), World.FootprintOrigin(farm));
        Assert.Equal(new Cell(9, 13), World.DoorCell(farm, 0)); // content [3, 5]: left of the west column, 3 down
        Assert.Equal(Facing.South, farm.Facing); // the unit facing stays as before
        var shed = w.All("warehouse").Single();
        Assert.Equal((2, false), (shed.Quarter, shed.Turned));
        Assert.Equal(new Cell(30 + 12 - 1 - 6, 9), World.DoorCell(shed, 0));
        var glass = w.All("greenhouse").Single();
        Assert.Equal((3, 4, 10), (glass.Quarter, World.FootW(glass), World.FootH(glass)));
        Assert.False(w.Grid.Passable(new Cell(13, 39)));
        Assert.True(w.Grid.Passable(new Cell(14, 30)));
        Assert.Empty(w.All("house_small"));

        // The free-footprint check reads the turned footprint: the 7 x 5 farmhouse at (40, 40) turned reaches y 46, onto
        // a soldier there that the unturned one misses.
        w.Spawn(2, "Rifleman", 40, 46);
        SandboxSpawn(w, 1, "farmhouse_stone", 40, 40, Facing.West);
        w.Step();
        Assert.Single(w.All("farmhouse_stone"));
        SandboxSpawn(w, 1, "farmhouse_stone", 40, 40);
        w.Step();
        Assert.Equal(2, w.All("farmhouse_stone").Count);
    }

    [Fact]
    public void TheSpawnApiRefusesATurnThatCantStand()
    {
        var w = Arena.New(64);
        var p = w.Player(1)!.Setup;
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Spawn(p.Find(w.Content, "house_small")!, new Cell(5, 5), 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Spawn(p.Find(w.Content, "house_small")!, new Cell(5, 5), 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Spawn(p.Find(w.Content, "vehicle_garage")!, new Cell(5, 5), 0, 1)); // a factory
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Spawn(p.Find(w.Content, "tunnel_large")!, new Cell(5, 5), 0, 2)); // turns by its facing
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Spawn(p.Find(w.Content, "Rifleman")!, new Cell(5, 5), 0, 1));
        Assert.Empty(w.Units);
        var depot = w.Unit(w.Spawn(p.Find(w.Content, "supply_depot")!, new Cell(5, 5), Facing.East, 3))!; // square, no doors: it may turn
        Assert.Equal((3, false), (depot.Quarter, depot.Turned));
    }

    [Fact]
    public void AFactorysRallyOnATurnedBuildingGoesToItsFront()
    {
        var w = Arena.New(64);
        var garage = w.Spawn(1, "vehicle_garage", 2, 44);
        var b = SpawnTurned(w, "apartment_3f", At, At, 1); // 10 x 6 turned: 6 x 10, the front west
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetRally, Units = [garage.Id], Target = b.Id });
        w.Step();
        Assert.Equal(new Cell(At - 2, At + 5), w.RallyCell(garage)); // 2 out from the west side, by the front's middle
        var c = SpawnTurned(w, "apartment_3f", 40, At, 0);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetRally, Units = [garage.Id], Target = c.Id });
        w.Step();
        Assert.Equal(new Cell(40 + 5, At + 6 + 1), w.RallyCell(garage)); // unturned: as before
    }

    // A town of every civilian building, each at a quarter by its index (or none), with squads walking into the first few.
    private static List<ulong> Town(bool turned)
    {
        var w = Arena.New(160);
        var kinds = TestContent.Mvp.Archetypes.Where(a => a.PlacedNeutral).ToList();
        var placed = new List<Unit>();
        for (var i = 0; i < kinds.Count; i++)
        {
            placed.Add(SpawnTurned(w, kinds[i].Id, 4 + i % 6 * 26, 4 + i / 6 * 26, turned ? i % 4 : 0));
        }
        var hashes = new List<ulong> { w.Hash() };
        var garrisoned = placed.Where(b => b.Def.Archetype.Garrison is not null).Take(4).ToList();
        for (var j = 0; j < garrisoned.Count; j++)
        {
            var squad = Enumerable.Range(0, 3).Select(i => w.Spawn(1, "Rifleman", 2 + j * 4 + i, 156)).ToList();
            w.Order(1, CommandKind.Enter, squad, garrisoned[j].Id);
        }
        for (var t = 0; t < 400; t++)
        {
            w.Step();
            if (t % 100 == 99)
            {
                hashes.Add(w.Hash());
            }
        }
        return hashes;
    }

    [Fact]
    public void ATurnedTownHashesTheSameOnTwoRunsAndNotAsTheUnturnedOne()
    {
        var a = Town(true);
        Assert.Equal(a, Town(true));
        Assert.NotEqual(a[0], Town(false)[0]);
    }

    [Fact]
    public void TheQuarterIsHashedEvenWhereTheFootprintIsTheSame()
    {
        // A square house at a half turn covers the same cells from the same centre: only its quarter (its door) differs.
        static ulong One(int quarter)
        {
            var w = Arena.New(48);
            SpawnTurned(w, "house_small", 10, 10, quarter);
            return w.Hash();
        }
        Assert.NotEqual(One(0), One(2));
        Assert.Equal(One(2), One(2));
    }

    // A town placed the old way: sandbox spawns without a facing, direct spawns at the unit facings scenarios use, an
    // observation post built at facing 0, squads walking in. Its hashes were taken on simcore main 5f7a725, before
    // building rotation, and re-pinned when the long-range rocket truck was added (a new archetype of the
    // faction shifts every unit's definition index, which the hash covers; nothing else about the town changed), and again
    // for forest fire (the content hash moved: fire.json and the bomb's fire block; the town is flat, nothing burns), and
    // for the hypersonic missile truck (the content hash moved and a new archetype of the faction shifts the definition
    // indices after it; nothing here fires), and for the incendiary bomb (the content hash moved: a new warhead and
    // component; nothing here carries it), and for the S-400 type battery (the content hash moved and two new archetypes of the
    // faction shift the definition indices after them; nothing here searches or fires), and for squads S1, the hull-aimed launchers,
    // squads S2 and squads S3 (the content hash moved each time: the squad block, the launchers' aim fields, the squad block's hold
    // part, its ambush part; nothing here forms a squad or launches; with the content before S3 the values before it reproduce),
    // and for squads S4 (the content hash moved: the ambush's air part; nothing here forms a squad).
    private static (World W, List<ulong> Hashes) OldTown()
    {
        var w = Sandbox();
        foreach (var (id, x, y) in new[]
        {
            ("block_brick_4", 4, 4), ("stone_rowhouse", 22, 4), ("farm_shed", 30, 4), ("greenhouse", 42, 4),
            ("house_small", 4, 20), ("apartment_l", 14, 20), ("kiosk", 30, 20),
        })
        {
            SandboxSpawn(w, 1, id, x, y);
        }
        w.Spawn(1, "farmhouse_stone", 40, 20, Facing.East);
        w.Spawn(1, "warehouse", 4, 40, Facing.South);
        w.Spawn(1, "observation_post", 30, 40, Facing.West);
        var workers = Enumerable.Range(0, 2).Select(i => w.Spawn(1, "worker", 42, 46 + i)).ToList();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [.. workers.Select(u => u.Id)], Cell = new Cell(44, 40), Unit = "observation_post" });
        w.Step();
        var hashes = new List<ulong> { w.Hash() };
        var brick = w.All("block_brick_4").Single();
        var house = w.All("house_small").Single();
        var shed = w.All("farm_shed").Single();
        var blue = Enumerable.Range(0, 8).Select(i => w.Spawn(1, "Rifleman", 4 + i, 32)).ToList();
        var red = Enumerable.Range(0, 3).Select(i => w.Spawn(2, "Rifleman", 34 + i, 14)).ToList();
        w.Order(1, CommandKind.Enter, blue.Take(5), brick.Id);
        w.Order(1, CommandKind.Enter, blue.Skip(5), house.Id);
        w.Order(2, CommandKind.Enter, red, shed.Id);
        for (var t = 0; t < 700; t++)
        {
            w.Step();
            if (t % 100 == 99)
            {
                hashes.Add(w.Hash());
            }
        }
        return (w, hashes);
    }

    [Fact]
    public void OldTownsWithoutAFacingPlayAndHashExactlyAsBefore()
    {
        var (w, hashes) = OldTown();
        Assert.Equal(
            [0x4DC9CA3AB3BAAD72UL, 0x52E881C134987DFCUL, 0x6A7C078E98E432B4UL, 0xB1587DEEE6B1A1A2UL, 0x434483F0E7B7C398UL,
             0x51FDC15CE134B8E7UL, 0x96D78726744B6C0FUL, 0xB5978C8F73823FDDUL],
            hashes);
        Assert.All(w.Units.Where(u => u.Def.IsStructure), u => Assert.Equal((0, false), (u.Quarter, u.Turned)));
        Assert.Equal((5, 3, 3), (w.All("block_brick_4")[0].Occupants, w.All("house_small")[0].Occupants, w.All("farm_shed")[0].Occupants));
        Assert.All(w.All("observation_post"), p => Assert.True(p.IsBuilt));
    }
}
