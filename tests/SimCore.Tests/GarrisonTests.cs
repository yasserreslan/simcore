using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

public class GarrisonTests
{
    // The brick block: 14x7 cells, capacity 6, doors at (2, 7) and (11, 7) from its top-left cell.
    private static Unit Brick(World w, int x = 10, int y = 10) => w.Spawn(1, "block_brick_4", x, y);

    private static List<Unit> Inside(World w, Unit b) => [.. w.Units.Where(u => u.Garrison == b.Id)];

    private static bool NearDoor(Cell c, Cell door) => Math.Abs(c.X - door.X) <= 2 && Math.Abs(c.Y - door.Y) <= 2;

    [Fact]
    public void BuildingSpawnsNeutralAndBlocksItsFootprint()
    {
        var w = Arena.New(48);
        var b = Brick(w);
        Assert.Equal(0, b.Owner);
        Assert.Equal(new Cell(10, 10), World.FootprintOrigin(b));
        Assert.False(w.Grid.Passable(new Cell(10, 10)));
        Assert.False(w.Grid.Passable(new Cell(23, 16)));
        Assert.True(w.Grid.Passable(new Cell(24, 16)));
        Assert.True(w.Grid.Passable(new Cell(12, 17))); // the door cell

        // A soldier told to walk through it goes around.
        var s = w.Spawn(1, "Rifleman", 16, 8);
        w.Order(1, CommandKind.Move, [s], cell: new Cell(16, 19));
        w.Run(400);
        Assert.Equal(new Cell(16, 19), Cell.Of(s.Pos));
    }

    [Fact]
    public void InfantryEnterUpToCapacityAndTakeTheBuilding()
    {
        var w = Arena.New(48);
        var b = Brick(w);
        var squad = Enumerable.Range(0, 8).Select(i => w.Spawn(1, i == 7 ? "worker" : "Rifleman", 8 + i, 22)).ToList();
        w.Order(1, CommandKind.Enter, squad, b.Id);
        w.Run(400);

        var inside = Inside(w, b);
        Assert.Equal(6, inside.Count);
        Assert.Equal(6, b.Occupants);
        Assert.Equal(1, b.Owner);
        Assert.All(inside, u => Assert.Equal(b.Pos, u.Pos));
        Assert.All(inside, u => Assert.False(World.IsVisibleTo(u, 2)));
        Assert.All(squad.Except(inside), u => Assert.Equal(OrderKind.Idle, u.Order.Kind));

        // Soldiers inside can't walk off; they exit first.
        w.Order(1, CommandKind.Move, [inside[0]], cell: new Cell(2, 2));
        w.Run(20);
        Assert.Equal(b.Id, inside[0].Garrison);
    }

    [Fact]
    public void WorkersMayGarrisonButVehiclesMayNot()
    {
        var w = Arena.New(48);
        var b = Brick(w);
        var worker = w.Spawn(1, "worker", 12, 20);
        var (tech, _) = w.SpawnCrewed(1, "technical", 16, 20);
        w.Order(1, CommandKind.Enter, [worker, tech], b.Id);
        w.Run(200);
        Assert.Equal(b.Id, worker.Garrison);
        Assert.Equal(0u, tech.Garrison);
    }

    [Fact]
    public void NobodyEntersABuildingTheEnemyHolds()
    {
        var w = Arena.New(48);
        var b = Brick(w);
        World.EnterBuilding(w.Spawn(1, "Rifleman", 5, 5), b);
        var enemy = w.Spawn(2, "Rifleman", 12, 30);
        w.Order(2, CommandKind.Enter, [enemy], b.Id);
        w.Step();
        Assert.Equal(OrderKind.Idle, enemy.Order.Kind);
        Assert.Equal(0u, enemy.Garrison);
    }

    [Fact]
    public void ExitOneAndEvacuateAllAtTheDoors()
    {
        var w = Arena.New(48);
        var b = Brick(w);
        var squad = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 2 + i, 2)).ToList();
        foreach (var s in squad)
        {
            World.EnterBuilding(s, b);
        }
        w.Step();
        Assert.Equal(4, b.Occupants);

        // One out, at the door nearest the given cell (the right one, (21, 17)).
        w.Order(1, CommandKind.Exit, [squad[1]], cell: new Cell(30, 20));
        w.Step();
        Assert.Equal(0u, squad[1].Garrison);
        Assert.Equal(new Cell(21, 17), Cell.Of(squad[1].Pos));
        Assert.Equal(3, b.Occupants);

        // Everyone out: spread over the doors, the building turns neutral.
        w.Order(1, CommandKind.Exit, [b], cell: new Cell(-1, -1));
        w.Step();
        Assert.Empty(Inside(w, b));
        Assert.Equal(0, b.Owner);
        Assert.All(squad, s => Assert.True(w.Grid.Passable(Cell.Of(s.Pos))));
        Assert.All(squad, s => Assert.True(NearDoor(Cell.Of(s.Pos), new Cell(12, 17)) || NearDoor(Cell.Of(s.Pos), new Cell(21, 17))));
    }

    [Fact]
    public void OccupantsFireFromTheWindowsWithExtraRangeAndCantBeShot()
    {
        var w = Arena.New(48);
        var b = Brick(w);
        var s = w.Spawn(1, "Rifleman", 2, 2);
        World.EnterBuilding(s, b);
        // 6.5 cells south of the front wall: beyond a rifle's 5 cells, inside 5 + 2 from a window.
        var enemy = w.Spawn(2, "Rifleman", 16, 23, Facing.North);
        w.Order(2, CommandKind.Stop, [enemy]);
        w.Run(100);
        Assert.True(enemy.HP < enemy.Def.MaxHp, "the occupant outranges the enemy rifle");
        Assert.Equal(s.Def.MaxHp, s.HP);

        // Close in: the enemy can only shoot the building, and bullets barely scratch it.
        var other = w.Spawn(2, "Rifleman", 16, 19, Facing.North);
        w.Order(2, CommandKind.Attack, [other], b.Id);
        var hp = b.HP;
        w.Run(60);
        Assert.Equal(s.Def.MaxHp, s.HP);
        Assert.InRange(hp - b.HP, 1, 20);
    }

    [Fact]
    public void ExplosivesHurtTheBuildingAndAQuarterToEveryOccupant()
    {
        var w = Arena.New(48);
        var b = Brick(w);
        var a = w.Spawn(1, "Rifleman", 2, 2);
        var c = w.Spawn(1, "Rifleman", 3, 2);
        World.EnterBuilding(a, b);
        World.EnterBuilding(c, b);
        var rocket = w.Content.Warheads.First(x => x.Id == "rocket");
        var bullet = w.Content.Warheads.First(x => x.Id == "bullet");

        w.Hurt(b, bullet, 100, 100);
        Assert.Equal(b.Def.MaxHp - 10, b.HP);
        Assert.Equal(a.Def.MaxHp, a.HP);

        w.Hurt(b, rocket, 100, 100);
        Assert.Equal(b.Def.MaxHp - 10 - 80, b.HP);
        Assert.Equal(a.Def.MaxHp - 10, a.HP); // 25% of the 40 a rocket deals to infantry
        Assert.Equal(c.Def.MaxHp - 10, c.HP);
    }

    [Fact]
    public void StagesFollowHpAndThrowOutTheExcess()
    {
        var w = Arena.New(48);
        var b = Brick(w);
        var squad = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 2 + i, 2)).ToList();
        foreach (var s in squad)
        {
            World.EnterBuilding(s, b);
        }
        w.Step();
        Assert.Equal(0, b.Stage);

        b.HP = b.Def.MaxHp * 70 / 100;
        w.Step();
        Assert.Equal(1, b.Stage);
        Assert.Equal(6, b.Occupants);

        b.HP = b.Def.MaxHp * 50 / 100; // stage 2: 75% of 6 = 4
        w.Step();
        Assert.Equal(2, b.Stage);
        Assert.Equal(4, b.Occupants);
        var out1 = squad.Where(s => s.Garrison == 0).ToList();
        Assert.Equal([squad[4], squad[5]], out1); // highest IDs first
        Assert.All(out1, s => Assert.Equal(s.Def.MaxHp - s.Def.MaxHp * 20 / 100, s.HP));
        Assert.All(out1, s => Assert.True(w.Grid.Passable(Cell.Of(s.Pos))));

        b.HP = b.Def.MaxHp * 20 / 100; // stage 3: half = 3
        w.Step();
        Assert.Equal(3, b.Stage);
        Assert.Equal(3, b.Occupants);
    }

    [Fact]
    public void CollapseEjectsSurvivorsByTheSeedAndLeavesRubble()
    {
        var results = new List<string>();
        for (ulong seed = 1; seed <= 2; seed++)
        {
            var w = Arena.New(48, seed);
            var b = Brick(w);
            var squad = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 2 + i, 2)).ToList();
            foreach (var s in squad)
            {
                World.EnterBuilding(s, b);
            }
            w.Step();
            b.HP = 0;
            w.Step();

            Assert.Null(w.Unit(b.Id));
            Assert.False(w.Grid.Passable(new Cell(15, 12))); // the rubble still blocks
            var alive = squad.Where(s => w.Unit(s.Id) is not null).ToList();
            Assert.All(alive, s => Assert.Equal(0u, s.Garrison));
            Assert.All(alive, s => Assert.Equal(s.Def.MaxHp * 30 / 100, s.HP));
            Assert.All(alive, s => Assert.True(w.Grid.Passable(Cell.Of(s.Pos))));
            results.Add(string.Join(",", alive.Select(s => s.Id)));

            // Same seed, same survivors.
            var again = Arena.New(48, seed);
            var b2 = Brick(again);
            var squad2 = Enumerable.Range(0, 6).Select(i => again.Spawn(1, "Rifleman", 2 + i, 2)).ToList();
            foreach (var s in squad2)
            {
                World.EnterBuilding(s, b2);
            }
            again.Step();
            b2.HP = 0;
            again.Step();
            Assert.Equal(results[^1], string.Join(",", squad2.Where(s => again.Unit(s.Id) is not null).Select(s => s.Id)));
            Assert.Equal(w.Hash(), again.Hash());
        }
    }

    [Fact]
    public void NeutralBuildingsAreNotAutoTargeted()
    {
        var w = Arena.New(48);
        var b = Brick(w);
        var s = w.Spawn(2, "Tank hunter", 16, 19);
        w.Run(100);
        Assert.Equal(b.Def.MaxHp, b.HP);
        Assert.Equal(0u, s.AutoTarget);
    }

    [Fact]
    public void RocketsFiredFromInsideFlyOutThroughTheWalls()
    {
        var w = Arena.New(48);
        var b = Brick(w);
        var hunter = w.Spawn(1, "Tank hunter", 2, 2);
        World.EnterBuilding(hunter, b);
        var (tech, _) = w.SpawnCrewed(2, "technical", 16, 24);
        w.Order(2, CommandKind.Stop, [tech]);
        var booms = new List<Detonation>();
        for (var i = 0; i < 120; i++)
        {
            w.Step();
            booms.AddRange(w.Detonations);
        }
        Assert.True(tech.HP < tech.Def.MaxHp, "the rocket left the building and hit the technical");
        Assert.NotEmpty(booms);
        Assert.DoesNotContain(booms, d => d.Terrain || d.Hit == b.Id);
    }

    [Fact]
    public void SandboxPlacesBuildingsNeutralWhereTheyFit()
    {
        var w = Arena.New(48, edit: m => m with { Scenario = SimCore.Scenarios.Scenario.SandboxName });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SandboxSpawn, Unit = "block_brick_4", Cell = new Cell(5, 5) });
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SandboxSpawn, Unit = "block_panel_5", Cell = new Cell(10, 8) }); // overlaps
        w.Step();
        var b = Assert.Single(w.Units);
        Assert.Equal(0, b.Owner);
        Assert.Equal(new Cell(5, 5), World.FootprintOrigin(b));
    }

    [Fact]
    public void LauncherRocketsComingDownOnTheRoofHitTheBuilding()
    {
        var w = Arena.New(64);
        var b = w.Spawn(1, "block_panel_5", 30, 20);
        var spotter = w.Spawn(2, "Rifleman", 40, 30);
        var (mrl, _) = w.SpawnCrewed(2, "heavy_rocket_launcher", 40, 50);
        w.Order(2, CommandKind.Stop, [spotter]);
        w.Order(2, CommandKind.Attack, [mrl], b.Id);
        var booms = new List<Detonation>();
        for (var i = 0; i < 500; i++)
        {
            w.Step();
            booms.AddRange(w.Detonations);
        }
        Assert.Equal(30, booms.Count);
        Assert.True(booms.Count(d => d.Hit == b.Id) >= 20, $"{booms.Count(d => d.Hit == b.Id)} of 30 hit the roof");
    }
}
