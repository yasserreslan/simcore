using SimCore.Sim;

namespace SimCore.Tests;

// The town buildings (house_small, villa_walled, shop_row, skeleton_frame), the countryside homes
// (farmhouse_stone, house_unfinished, house_courtyard, villa_tiled, shack_tin; second batch shepherd_hut,
// mountain_house, mudbrick_house), the third batch of town buildings (corner_building, workshop, apartment_3f,
// apartment_l, office_block), the fourth batch (school, clinic, warehouse, fuel_station, factory), the farm shed
// and the house over a shop: garrisonable like the blocks, footprints from the art rounded up to whole cells, doors
// on the front (south). The other farm buildings (greenhouse, hay_store, chicken_coop, farm_water_tower), the kiosk
// and the fourth batch's market_stalls, water_tower_town and cell_tower hold nobody: plain neutral civilian structures.
// The stone village houses (stone_rowhouse, stone_house_arch, stone_tower_house) are homes with the homes' rules;
// the rowhouses are built to stand wall to wall.
public class TownBuildingTests
{
    public static TheoryData<string, int, int, int, int> Buildings => new()
    {
        { "house_small", 5, 5, 4, 700 },
        { "villa_walled", 8, 9, 6, 1000 },
        { "shop_row", 7, 6, 6, 1100 },
        { "skeleton_frame", 8, 6, 8, 1300 },
    };

    // The countryside homes (first batch 2026-10-24, second batch and the stone village houses 2026-10-25): id,
    // footprint, capacity, HP.
    public static TheoryData<string, int, int, int, int> Homes => new()
    {
        { "farmhouse_stone", 7, 5, 5, 900 },
        { "house_unfinished", 6, 5, 5, 800 },
        { "house_courtyard", 9, 9, 5, 800 },
        { "villa_tiled", 7, 6, 6, 900 },
        { "shack_tin", 4, 4, 2, 350 },
        { "shepherd_hut", 4, 4, 2, 400 },
        { "mountain_house", 6, 6, 5, 900 },
        { "mudbrick_house", 6, 5, 4, 600 },
        { "stone_rowhouse", 4, 6, 4, 900 },
        { "stone_house_arch", 6, 6, 5, 1000 },
        { "stone_tower_house", 5, 5, 5, 1300 },
    };

    // The stone village houses' doors: id, door cell x, y (relative to the footprint's top-left cell; the front row
    // centre, just outside the footprint), the art's door_01 depth z in centimetres (x is 0 for all three; the glTF
    // origin is the footprint centre, 1 cell = 2 m, the front is the south row) and the cell centre's offset from the
    // art's door_01 in centimetres across and along.
    public static TheoryData<string, int, int, int, int, int> StoneDoors => new()
    {
        { "stone_rowhouse", 2, 6, 680, 100, 20 },
        { "stone_house_arch", 3, 6, 680, 100, 20 },
        { "stone_tower_house", 2, 5, 580, 0, 20 },
    };

    // The third batch of town buildings (2026-10-25): id, footprint, capacity, HP; the homes' garrison numbers and
    // door. The kiosk of the batch is a prop (FarmProps).
    public static TheoryData<string, int, int, int, int> TownBatch => new()
    {
        { "corner_building", 7, 7, 6, 1100 },
        { "workshop", 7, 6, 4, 800 },
        { "apartment_3f", 10, 6, 8, 1400 },
        { "apartment_l", 12, 10, 10, 1800 },
        { "office_block", 8, 8, 8, 1600 },
    };

    // The fourth batch of town buildings (2026-10-25): id, footprint, capacity, HP; the homes' garrison numbers and
    // door. Its market stalls, water tower and cell tower are props (FarmProps).
    public static TheoryData<string, int, int, int, int> CivicBatch => new()
    {
        { "school", 12, 10, 10, 1600 },
        { "clinic", 8, 7, 6, 1200 },
        { "warehouse", 12, 8, 8, 1400 },
        { "fuel_station", 10, 8, 3, 700 },
        { "factory", 14, 10, 10, 2000 },
    };

    // The fourth batch's doors: id, door cell x, y (relative to the footprint's top-left cell; the front row
    // centre, just outside the footprint) and the cell's offset from the art's door_01 in centimetres along the
    // width and the depth (zero where the art's door sits on the cell; the half cell of an even width is 100).
    public static TheoryData<string, int, int, int, int> CivicDoors => new()
    {
        { "school", 6, 10, 100, 20 },
        { "clinic", 4, 7, 100, 790 },
        { "warehouse", 6, 8, 100, 370 },
        { "fuel_station", 5, 8, 100, 20 },
        { "factory", 7, 10, 100, 20 },
    };

    // The second, third and fourth batches' and the stone village houses' heights (tenths of a metre): id, height.
    public static TheoryData<string, int> SecondBatchHeights => new()
    {
        { "shepherd_hut", 35 },
        { "mountain_house", 92 },
        { "mudbrick_house", 43 },
        { "corner_building", 105 },
        { "workshop", 60 },
        { "apartment_3f", 105 },
        { "apartment_l", 105 },
        { "office_block", 130 },
        { "school", 80 },
        { "clinic", 75 },
        { "warehouse", 80 },
        { "fuel_station", 55 },
        { "factory", 90 },
        { "stone_rowhouse", 80 },
        { "stone_house_arch", 75 },
        { "stone_tower_house", 110 },
    };

    // The garrisonable farm building and town filler (task 2, 2026-10-24): id, footprint, capacity, HP; the
    // homes' garrison numbers and door.
    public static TheoryData<string, int, int, int, int> FarmAndFiller => new()
    {
        { "farm_shed", 8, 5, 4, 600 },
        { "house_over_shop", 6, 5, 6, 1000 },
    };

    // The buildings nobody garrisons (the farm buildings, the kiosk and the fourth batch's props): id, footprint, height (tenths of a metre), HP.
    public static TheoryData<string, int, int, int, int> FarmProps => new()
    {
        { "greenhouse", 10, 4, 30, 150 },
        { "hay_store", 6, 4, 50, 300 },
        { "chicken_coop", 3, 3, 20, 100 },
        { "farm_water_tower", 3, 3, 120, 400 },
        { "kiosk", 2, 2, 30, 150 },
        { "market_stalls", 8, 6, 35, 200 },
        { "water_tower_town", 4, 4, 180, 600 },
        { "cell_tower", 3, 3, 300, 400 },
    };

    [Theory]
    [MemberData(nameof(Buildings))]
    [MemberData(nameof(Homes))]
    [MemberData(nameof(FarmAndFiller))]
    [MemberData(nameof(TownBatch))]
    [MemberData(nameof(CivicBatch))]
    public void FootprintCapacityAndHp(string id, int w, int h, int capacity, int hp)
    {
        var a = TestContent.Mvp.Archetype(id)!;
        Assert.Equal((w, h), (a.FootprintW, a.FootprintH));
        Assert.Equal(capacity, a.Garrison!.Capacity);
        Assert.Equal(hp, a.MaxHp);
        Assert.All(a.Garrison.Doors, d => Assert.Equal(h, d.Y)); // just outside the front
    }

    [Theory]
    [MemberData(nameof(Buildings))]
    [MemberData(nameof(Homes))]
    [MemberData(nameof(FarmAndFiller))]
    [MemberData(nameof(TownBatch))]
    [MemberData(nameof(CivicBatch))]
    public void ASquadGarrisonsItUpToCapacity(string id, int w, int h, int capacity, int hp)
    {
        _ = (w, hp);
        var world = Arena.New(48);
        var b = world.Spawn(1, id, 10, 10);
        Assert.Equal(0, b.Owner);
        var squad = Enumerable.Range(0, capacity + 2).Select(i => world.Spawn(1, "Rifleman", 6 + i, 10 + h + 6)).ToList();
        world.Order(1, CommandKind.Enter, squad, b.Id);
        world.Run(600);
        Assert.Equal(capacity, world.Units.Count(u => u.Garrison == b.Id));
        Assert.Equal(1, b.Owner);
    }

    [Fact]
    public void TheBallisticMissileBringsDownEveryOne()
    {
        var w = Arena.New(64);
        var weapon = w.Match.Players[0].Find(w.Content, "ballistic_truck")!.Weapons[0];
        foreach (var (id, x) in new[] { ("house_small", 2), ("villa_walled", 10), ("shop_row", 22), ("skeleton_frame", 32) })
        {
            var b = w.Spawn(2, id, x, 2);
            w.Hurt(b, weapon.Warhead, weapon.Damage, 100);
            Assert.False(b.Alive, id);
        }
    }

    [Theory]
    [MemberData(nameof(Homes))]
    [MemberData(nameof(FarmAndFiller))]
    [MemberData(nameof(TownBatch))]
    [MemberData(nameof(CivicBatch))]
    public void HomesAreFreeNeutralCivilianBuildingsWithOneFrontDoor(string id, int w, int h, int capacity, int hp)
    {
        _ = (capacity, hp);
        var house = TestContent.Mvp.Archetype("house_small")!;
        var a = TestContent.Mvp.Archetype(id)!;
        Assert.Equal((0, 600, house.Armor), (a.Cost, a.BuildTime, a.Armor));
        Assert.Equal([(w / 2, h)], a.Garrison!.Doors); // the front row centre
        Assert.Equal(house.Garrison!.StageCapacity, a.Garrison.StageCapacity);
        Assert.True(TestContent.Mvp.Factions[0].Archetypes[a.Index]); // the Resistance list: sandbox spawns

        var world = Arena.New(48);
        var b = world.Spawn(1, id, 10, 10);
        Assert.Equal(0, b.Owner);
        Assert.False(world.Grid.Passable(new Cell(10, 10)));
        Assert.False(world.Grid.Passable(new Cell(10 + w - 1, 10 + h - 1)));
        var door = new Cell(10 + w / 2, 10 + h);
        Assert.True(world.Grid.Passable(door));

        // In, then out by the door.
        var s = world.Spawn(1, "Rifleman", 4, 4);
        World.EnterBuilding(s, b);
        world.Step();
        Assert.Equal(b.Id, s.Garrison);
        world.Order(1, CommandKind.Exit, [s], cell: new Cell(10 + w / 2, 40));
        world.Step();
        Assert.Equal(0u, s.Garrison);
        Assert.Equal(door, Cell.Of(s.Pos));
        Assert.Equal(0, b.Owner);
    }

    [Theory]
    [MemberData(nameof(Homes))]
    [MemberData(nameof(FarmAndFiller))]
    [MemberData(nameof(TownBatch))]
    [MemberData(nameof(CivicBatch))]
    public void HomesThrowOutTheExcessByStageAndCollapseToRubble(string id, int w, int h, int capacity, int hp)
    {
        _ = hp;
        var world = Arena.New(48);
        var b = world.Spawn(1, id, 10, 10);
        var squad = Enumerable.Range(0, capacity).Select(i => world.Spawn(1, "Rifleman", 2 + i, 2)).ToList();
        foreach (var s in squad)
        {
            World.EnterBuilding(s, b);
        }
        world.Step();
        Assert.Equal(capacity, b.Occupants);

        b.HP = b.Def.MaxHp * 50 / 100; // stage 2 holds 75%
        world.Step();
        Assert.Equal(2, b.Stage);
        Assert.Equal(capacity * 75 / 100, b.Occupants);

        b.HP = b.Def.MaxHp * 20 / 100; // stage 3 holds half
        world.Step();
        Assert.Equal(3, b.Stage);
        Assert.Equal(capacity / 2, b.Occupants);
        Assert.All(squad.Where(s => s.Garrison == 0), s => Assert.Equal(s.Def.MaxHp - s.Def.MaxHp * 20 / 100, s.HP));

        var inside = squad.Where(s => s.Garrison == b.Id).ToList();
        b.HP = 0;
        world.Step();
        Assert.Null(world.Unit(b.Id));
        Assert.False(world.Grid.Passable(new Cell(10 + w / 2, 10 + h / 2))); // the rubble still blocks
        Assert.All(inside.Where(s => world.Unit(s.Id) is not null), s =>
        {
            Assert.Equal(0u, s.Garrison);
            Assert.Equal(s.Def.MaxHp * 30 / 100, s.HP);
        });
    }

    [Theory]
    [MemberData(nameof(SecondBatchHeights))]
    public void TheLaterBatchBuildingsHaveTheirHeightsAndAreNeutralByTheirGarrison(string id, int height)
    {
        var a = TestContent.Mvp.Archetype(id)!;
        Assert.Equal(height * 512 / 10, a.HeightRaw);
        Assert.False(a.Neutral); // neutral by its garrison, not the flag
        Assert.True(a.PlacedNeutral);
    }

    [Fact]
    public void TheLShapedBlockBlocksItsWholeFootprintYardIncludedAndItsDoorIsTheYardGate()
    {
        var a = TestContent.Mvp.Archetype("apartment_l")!;
        Assert.Equal((12, 10), (a.FootprintW, a.FootprintH));
        Assert.Equal([(6, 10)], a.Garrison!.Doors); // the yard gate, on the front row centre
        var world = Arena.New(48);
        world.Spawn(1, "apartment_l", 10, 10);
        for (var y = 0; y < 10; y++)
        {
            for (var x = 0; x < 12; x++)
            {
                Assert.False(world.Grid.Passable(new Cell(10 + x, 10 + y)), $"({x}, {y})");
            }
        }
        Assert.True(world.Grid.Passable(new Cell(10 + 6, 10 + 10))); // the gate cell, just outside
        Assert.True(world.Grid.Passable(new Cell(10 + 12, 10)));
    }

    [Theory]
    [MemberData(nameof(CivicDoors))]
    public void TheFourthBatchHasOneDoorOnTheFrontRowCentreAndItsWholeFootprintBlocks(string id, int doorX, int doorY, int acrossCm, int alongCm)
    {
        var a = TestContent.Mvp.Archetype(id)!;
        var (w, h) = (a.FootprintW, a.FootprintH);
        Assert.Equal([(doorX, doorY)], a.Garrison!.Doors);
        Assert.Equal((w / 2, h), (doorX, doorY)); // the front row centre, the first cell outside the footprint

        // The cell's centre against the art's door_01 (glTF origin on the footprint centre, 1 cell = 2 m, the front
        // is the footprint's south row): the half cell across, and along as the art places it.
        var (cellX, cellZ) = (100 * (2 * doorX + 1 - w), 100 * (2 * doorY + 1 - h)); // centimetres
        var art = id switch
        {
            "school" => (0, 1080),
            "clinic" => (0, 10),
            "warehouse" => (0, 530),
            "fuel_station" => (0, 880),
            _ => (0, 1080), // factory
        };
        Assert.Equal((acrossCm, alongCm), (Math.Abs(cellX - art.Item1), Math.Abs(cellZ - art.Item2)));

        var world = Arena.New(48);
        world.Spawn(1, id, 10, 10);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                Assert.False(world.Grid.Passable(new Cell(10 + x, 10 + y)), $"{id} ({x}, {y})");
            }
        }
        Assert.True(world.Grid.Passable(new Cell(10 + doorX, 10 + doorY)));
        Assert.True(world.Grid.Passable(new Cell(10 + w, 10)));
    }

    [Theory]
    [MemberData(nameof(StoneDoors))]
    public void TheStoneVillageHousesHaveOneDoorOnTheFrontRowCentreNearTheArtsDoor(string id, int doorX, int doorY, int artZcm, int acrossCm, int alongCm)
    {
        var a = TestContent.Mvp.Archetype(id)!;
        var (w, h) = (a.FootprintW, a.FootprintH);
        Assert.Equal([(doorX, doorY)], a.Garrison!.Doors);
        Assert.Equal((w / 2, h), (doorX, doorY)); // the front row centre, the first cell outside the footprint

        // The cell's centre against the art's door_01 at (0, artZcm): the half cell an even width leaves across, and
        // the 0.2 m the art's door stands inside the cell's centre along.
        var (cellX, cellZ) = (100 * (2 * doorX + 1 - w), 100 * (2 * doorY + 1 - h)); // centimetres
        Assert.Equal((acrossCm, alongCm), (Math.Abs(cellX), Math.Abs(cellZ - artZcm)));

        var world = Arena.New(48);
        world.Spawn(1, id, 10, 10);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                Assert.False(world.Grid.Passable(new Cell(10 + x, 10 + y)), $"{id} ({x}, {y})");
            }
        }
        Assert.True(world.Grid.Passable(new Cell(10 + doorX, 10 + doorY)));
        Assert.True(world.Grid.Passable(new Cell(10 + w, 10)));
        Assert.True(world.Grid.Passable(new Cell(9, 10)));
    }

    // The glyphs of the stone village houses are letters that only a unit uses (the free single characters are gone):
    // no other building shares them, and they differ from each other. Units may share them on purpose, so unlike the
    // town buildings' test this one counts buildings only.
    [Fact]
    public void TheStoneVillageHousesHaveGlyphsNoOtherBuildingUses()
    {
        string[] ids = ["stone_rowhouse", "stone_house_arch", "stone_tower_house"];
        var all = TestContent.Mvp.Archetypes;
        var glyphs = ids.Select(id => TestContent.Mvp.Archetype(id)!.Glyph).ToList();
        Assert.True(glyphs[0] != glyphs[1] && glyphs[0] != glyphs[2] && glyphs[1] != glyphs[2]);
        Assert.All(all.Where(x => x.IsStructure && !ids.Contains(x.Id)), x => Assert.DoesNotContain(x.Glyph, glyphs));
    }

    // Rowhouses stand wall to wall: n of them placed with touching footprints (4 cells apart) all load, block as one
    // wall, each garrison and empty through its own door, and one destroyed leaves its neighbours as they were.
    private static List<Unit> Row(World world, int n)
    {
        var row = Enumerable.Range(0, n).Select(i => world.Spawn(1, "stone_rowhouse", 10 + 4 * i, 10)).ToList();
        Assert.Equal(n, world.All("stone_rowhouse").Count);
        for (var i = 0; i < n; i++)
        {
            Assert.True(row[i].Alive);
            Assert.Equal(0, row[i].Owner);
            Assert.Equal(new Cell(10 + 4 * i, 10), World.FootprintOrigin(row[i]));
        }
        return row;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void RowhousesPlacedSideBySideBlockMovementAsOneWall(int n)
    {
        var world = Arena.New(48);
        Row(world, n);
        var (x0, x1) = (10, 10 + 4 * n - 1);

        // Every cell of the wall blocks, the seams between the houses included; the cells around it do not.
        for (var y = 10; y < 16; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                Assert.False(world.Grid.Passable(new Cell(x, y)), $"({x}, {y})");
            }
            Assert.True(world.Grid.Passable(new Cell(x0 - 1, y)));
            Assert.True(world.Grid.Passable(new Cell(x1 + 1, y)));
        }
        for (var x = x0; x <= x1; x++)
        {
            Assert.True(world.Grid.Passable(new Cell(x, 9)));
            Assert.True(world.Grid.Passable(new Cell(x, 16))); // the front row, doors and all
        }

        // A soldier sent from behind the middle of the wall to the front goes round an end of it, never through it.
        var mid = (x0 + x1) / 2;
        var s = world.Spawn(1, "Rifleman", mid, 5);
        var goal = new Cell(mid, 22);
        world.Order(1, CommandKind.Move, [s], cell: goal);
        int minX = mid, maxX = mid;
        for (var tick = 0; tick < 1500 && Cell.Of(s.Pos) != goal; tick++)
        {
            world.Step();
            var c = Cell.Of(s.Pos);
            Assert.True(world.Grid.Passable(c), $"tick {tick}: ({c.X}, {c.Y})");
            minX = Math.Min(minX, c.X);
            maxX = Math.Max(maxX, c.X);
        }
        Assert.Equal(goal, Cell.Of(s.Pos));
        Assert.True(minX < x0 || maxX > x1, $"it crossed only between x = {minX} and {maxX}");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void RowhousesPlacedSideBySideGarrisonAndEmptyEachThroughItsOwnDoor(int n)
    {
        var world = Arena.New(48);
        var row = Row(world, n);
        var capacity = TestContent.Mvp.Archetype("stone_rowhouse")!.Garrison!.Capacity;
        Assert.Equal(4, capacity);

        // A squad of capacity + 1 per house, each starting in front of its own door and ordered into its own house:
        // each house takes its capacity and nobody goes into a neighbour.
        var squads = new List<List<Unit>>();
        for (var i = 0; i < n; i++)
        {
            var squad = Enumerable.Range(0, capacity + 1).Select(k => world.Spawn(1, "Rifleman", 10 + 4 * i + (k % 3), 22 + k / 3)).ToList();
            squads.Add(squad);
            world.Order(1, CommandKind.Enter, squad, row[i].Id);
        }
        world.Run(600);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(capacity, world.Units.Count(u => u.Garrison == row[i].Id));
            Assert.All(squads[i], u => Assert.True(u.Garrison == 0 || u.Garrison == row[i].Id, $"house {i}"));
            Assert.Equal(1, row[i].Owner);
        }

        // Everyone out of each house at once: the first one stands on its own door and the rest within a cell of it
        // (the next house's door is 4 cells away).
        var inside = row.Select(b => squads[row.IndexOf(b)].Where(u => u.Garrison == b.Id).ToList()).ToList();
        world.Order(1, CommandKind.Exit, row, cell: new Cell(10 + 2 * n, 40));
        world.Step();
        for (var i = 0; i < n; i++)
        {
            var door = new Cell(10 + 4 * i + 2, 16);
            Assert.Equal(capacity, inside[i].Count);
            Assert.Equal(0, world.Units.Count(u => u.Garrison == row[i].Id));
            Assert.All(inside[i], u =>
            {
                var c = Cell.Of(u.Pos);
                Assert.Equal(0u, u.Garrison);
                Assert.True(Math.Abs(c.X - door.X) <= 1 && Math.Abs(c.Y - door.Y) <= 1, $"house {i}: ({c.X}, {c.Y})");
            });
            Assert.Contains(inside[i], u => Cell.Of(u.Pos) == door);
            Assert.Equal(0, row[i].Owner); // empty again, so nobody's
        }
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(4, 1)]
    [InlineData(4, 3)]
    public void DestroyingOneRowhouseLeavesItsNeighboursIntact(int n, int doomed)
    {
        var world = Arena.New(48);
        var row = Row(world, n);
        var squads = new List<List<Unit>>();
        for (var i = 0; i < n; i++)
        {
            var squad = Enumerable.Range(0, 4).Select(k => world.Spawn(1, "Rifleman", 2 + k, 2 + 2 * i)).ToList();
            foreach (var s in squad)
            {
                World.EnterBuilding(s, row[i]);
            }
            squads.Add(squad);
        }
        world.Step();
        Assert.All(row, b => Assert.Equal(4, b.Occupants));

        row[doomed].HP = 0;
        world.Step();
        Assert.Null(world.Unit(row[doomed].Id));
        Assert.All(squads[doomed], s => Assert.Equal(0u, s.Garrison)); // thrown out of the ruin
        for (var i = 0; i < n; i++)
        {
            for (var y = 10; y < 16; y++)
            {
                for (var x = 10 + 4 * i; x < 14 + 4 * i; x++)
                {
                    Assert.False(world.Grid.Passable(new Cell(x, y)), $"house {i} ({x}, {y})"); // the ruin blocks too
                }
            }
            if (i == doomed)
            {
                continue;
            }
            Assert.True(row[i].Alive);
            Assert.Equal(row[i].Def.MaxHp, row[i].HP);
            Assert.Equal(0, row[i].Stage);
            Assert.Equal(4, row[i].Occupants);
            Assert.Equal(1, row[i].Owner);
            Assert.All(squads[i], s => Assert.Equal(row[i].Id, s.Garrison));
            Assert.True(world.Grid.Passable(new Cell(10 + 4 * i + 2, 16))); // its door is still open
        }
    }

    // The glyph is one printable character the sim shows on its text map; the town buildings of the third and
    // fourth batches share theirs with no archetype (the older units share glyphs on purpose, so this is not a rule
    // for all archetypes).
    [Fact]
    public void TheTownBuildingsOfTheLaterBatchesHaveGlyphsNoOtherArchetypeUses()
    {
        string[] ids =
        [
            "corner_building", "workshop", "apartment_3f", "apartment_l", "office_block", "kiosk",
            "school", "clinic", "warehouse", "fuel_station", "factory", "market_stalls", "water_tower_town", "cell_tower",
        ];
        var all = TestContent.Mvp.Archetypes;
        foreach (var id in ids)
        {
            var glyph = TestContent.Mvp.Archetype(id)!.Glyph;
            Assert.Equal(1, all.Count(x => x.Glyph == glyph));
        }
    }

    [Fact]
    public void TheFarmShedAndTheHouseOverAShopTakeTheHomesHeights()
    {
        Assert.Equal(55 * 512 / 10, TestContent.Mvp.Archetype("farm_shed")!.HeightRaw); // 5.5 m
        Assert.Equal(95 * 512 / 10, TestContent.Mvp.Archetype("house_over_shop")!.HeightRaw); // 9.5 m
        Assert.False(TestContent.Mvp.Archetype("farm_shed")!.Neutral); // neutral by its garrison, not the flag
    }

    [Theory]
    [MemberData(nameof(FarmProps))]
    public void FarmPropsAreFreeNeutralStructuresNobodyGarrisons(string id, int w, int h, int height, int hp)
    {
        var house = TestContent.Mvp.Archetype("house_small")!;
        var a = TestContent.Mvp.Archetype(id)!;
        Assert.Equal((w, h), (a.FootprintW, a.FootprintH));
        Assert.Equal(hp, a.MaxHp);
        Assert.Equal(height * 512 / 10, a.HeightRaw);
        Assert.Equal((0, 600, house.Armor), (a.Cost, a.BuildTime, a.Armor));
        Assert.Null(a.Garrison);
        Assert.True(a.Neutral);
        Assert.True(a.PlacedNeutral);
        Assert.True(TestContent.Mvp.Factions[0].Archetypes[a.Index]); // the Resistance list: sandbox spawns

        // Placed neutral, blocking its whole footprint and nothing outside it.
        var world = Arena.New(48);
        var b = world.Spawn(1, id, 10, 10);
        Assert.Equal(0, b.Owner);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                Assert.False(world.Grid.Passable(new Cell(10 + x, 10 + y)), $"{id} ({x}, {y})");
            }
        }
        Assert.True(world.Grid.Passable(new Cell(10 + w / 2, 10 + h)));
        Assert.True(world.Grid.Passable(new Cell(10 + w, 10)));

        // A garrison order is refused: the soldier never goes in and the structure stays nobody's.
        var s = world.Spawn(1, "Rifleman", 10 + w / 2, 10 + h + 3);
        world.Order(1, CommandKind.Enter, [s], b.Id);
        world.Run(200);
        Assert.Equal(0u, s.Garrison);
        Assert.Equal(0u, s.Inside);
        Assert.Equal(0, b.Owner);
        Assert.Equal(0, b.Occupants);

        // Bullets scratch it (building armour); the ballistic missile brings it down. Its footprint stays blocked (rubble).
        var rifle = world.Match.Players[0].Find(world.Content, "Rifleman")!.Weapons[0];
        world.Hurt(b, rifle.Warhead, rifle.Damage, 100);
        Assert.InRange(b.HP, 1, b.Def.MaxHp - 1);
        var missile = world.Match.Players[0].Find(world.Content, "ballistic_truck")!.Weapons[0];
        world.Hurt(b, missile.Warhead, missile.Damage, 100);
        Assert.False(b.Alive, id);
        world.Step();
        Assert.Null(world.Unit(b.Id));
        Assert.False(world.Grid.Passable(new Cell(10 + w / 2, 10 + h / 2))); // the rubble still blocks
        Assert.False(world.Grid.Passable(new Cell(10, 10)));
    }

    [Fact]
    public void TheSandboxPlacesAFarmPropNeutralWhereItFits()
    {
        var w = Arena.New(48, edit: m => m with { Scenario = SimCore.Scenarios.Scenario.SandboxName });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "greenhouse", Cell = new Cell(5, 5) });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "chicken_coop", Cell = new Cell(14, 8) }); // overlaps
        w.Step();
        var b = Assert.Single(w.Units);
        Assert.Equal("greenhouse", b.Def.Archetype.Id);
        Assert.Equal(0, b.Owner);
        Assert.Equal(new Cell(5, 5), World.FootprintOrigin(b));
        Assert.False(w.Grid.Passable(new Cell(14, 8)));
    }
}
