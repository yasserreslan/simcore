using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// Wheeled climb (docs/design.md, "Wheeled climb"; decisions 1058 to 1064): trucks drive Steep ground up to 38% grade and light
/// 4x4s up to 41%, slower and dearer the steeper; tracked and infantry keep 45%, and nobody drives a cliff.
/// </summary>
public sealed class WheeledClimbTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static int Class(string id) => Rules.Classes.Select((c, i) => (c, i)).First(x => x.c.Id == id).i;

    private const int Foot = 12, Top = 27; // the ramp: from corner x = 12 up to corner x = 27, the map's whole height (no way round)

    // Hill: 40 x 40 cells, flat to the ramp's foot, rising grade percent a cell to its top, flat after (a plateau); 0: flat.
    private static TerrainMap Hill(int grade)
    {
        var rise = (grade * (int)Fixed.OneRaw + 99) / 100;
        var corners = new short[41 * 41];
        for (var y = 0; y <= 40; y++)
        {
            for (var x = 0; x <= 40; x++)
            {
                corners[y * 41 + x] = (short)(Math.Clamp(x - Foot, 0, Top - Foot) * rise);
            }
        }
        return new TerrainMap(40, 40, corners, new byte[40 * 40], Rules);
    }

    // Drive: a cargo truck from x to x2 on row 20 over a hill; its ticks with its cell from 17 to 23 (the ramp's middle, its
    // own grade there) and whether it got there.
    private static (int Ticks, bool There) Drive(int grade, int from, int to)
    {
        var w = Arena.NewSentinel(40);
        w.AttachTerrain(Hill(grade));
        var u = w.SpawnCrewed(1, "sentinel_cargo_truck", from, 20, from < to ? Facing.East : Facing.West).Vehicle;
        Assert.Equal(Class("heavy_wheeled"), Rules.ClassOf(u.Def));
        w.Order(1, CommandKind.Move, [u], cell: new Cell(to, 20));
        var ticks = 0;
        for (var t = 0; t < 2400 && (t < 5 || u.Moving); t++)
        {
            w.Step();
            var x = Cell.Of(u.Pos).X;
            ticks += x is >= 17 and <= 23 ? 1 : 0;
            Assert.True(w.Ground!.MoveGrade(Cell.Of(u.Pos)) < 38, $"a truck on {w.Ground.MoveGrade(Cell.Of(u.Pos))}% ground at {u.Pos}");
        }
        return (ticks, Cell.Of(u.Pos) == new Cell(to, 20));
    }

    [Fact]
    public void ATruckClimbsAThirtyPercentRampSlowerUpThanDownAndThanOnTheFlat()
    {
        var up = Drive(30, 5, 35);
        var down = Drive(30, 35, 5);
        var flat = Drive(0, 5, 35);
        Assert.True(up.There && down.There && flat.There);
        // 30%: up at 38% of its speed, down at 63% (heavy_wheeled: 45 and 70 at 25%, ramping to 25 and 50 at 38%).
        Assert.InRange(up.Ticks * 100 / flat.Ticks, 220, 300);
        Assert.InRange(down.Ticks * 100 / flat.Ticks, 140, 190);
    }

    [Fact]
    public void ATruckIsRefusedAFortyFivePercentRamp()
    {
        var w = Arena.NewSentinel(40);
        w.AttachTerrain(Hill(45));
        Assert.False(w.Ground!.Passable(Class("heavy_wheeled"), new Cell(20, 20)));
        var u = w.SpawnCrewed(1, "sentinel_cargo_truck", 5, 20, Facing.East).Vehicle;
        w.Order(1, CommandKind.Move, [u], cell: new Cell(35, 20));
        for (var t = 0; t < 600; t++)
        {
            w.Step();
            Assert.True(Cell.Of(u.Pos).X < Foot, $"a truck on the 45% ramp at {u.Pos}");
        }
    }

    [Theory]
    [InlineData("heavy_wheeled", 38)]
    [InlineData("wheeled", 38)]
    [InlineData("light_wheeled", 41)]
    [InlineData("tracked", 45)]
    [InlineData("foot", 45)]
    public void EachClassDrivesARampUpToItsCliff(string id, int cliff)
    {
        var k = Class(id);
        Assert.True(Hill(cliff - 1).Passable(k, new Cell(20, 20)));
        Assert.False(Hill(cliff).Passable(k, new Cell(20, 20)));
    }

    [Fact]
    public void TheSteepBandRampsSpeedAndCostByGrade()
    {
        var heavy = Rules.Classes[Class("heavy_wheeled")];
        Assert.Equal((45, 70, 24), (heavy.SpeedAt(25, true), heavy.SpeedAt(25, false), heavy.CostAt(25)));
        Assert.Equal((27, 52, 38), (heavy.SpeedAt(37, true), heavy.SpeedAt(37, false), heavy.CostAt(37)));
        for (var g = 25; g < 37; g++)
        {
            Assert.True(heavy.SpeedAt(g + 1, true) <= heavy.SpeedAt(g, true) && heavy.CostAt(g + 1) >= heavy.CostAt(g));
        }
        Assert.Equal(0, heavy.SpeedAt(38, true));
        Assert.Equal(70, heavy.SpeedAt(24, true)); // Gentle: no ramp
        // A class without steepTo keeps its Steep values across the band (the rules before).
        var tracked = Rules.Classes[Class("tracked")];
        Assert.Null(tracked.SteepTo);
        Assert.Equal(tracked.SpeedUp[2], tracked.SpeedAt(44, true));
        Assert.Equal(tracked.Cost[2], tracked.CostAt(26));
    }

    [Fact]
    public void TheRampIsHashed()
    {
        var plain = new TerrainRules { Classes = [Strip(Rules.Classes[Class("heavy_wheeled")])] };
        var ramped = new TerrainRules { Classes = [Rules.Classes[Class("heavy_wheeled")]] };
        Assert.NotEqual(plain.Digest(), ramped.Digest());
    }

    // Strip: the class without its ramp.
    private static MoveClass Strip(MoveClass c) => new()
    {
        Id = c.Id, Locomotor = c.Locomotor, MinRadius = c.MinRadius, MaxRadius = c.MaxRadius, GentleFrom = c.GentleFrom, SteepFrom = c.SteepFrom,
        CliffFrom = c.CliffFrom, SpeedUp = c.SpeedUp, SpeedDown = c.SpeedDown, Cost = c.Cost, UphillCostPercent = c.UphillCostPercent,
        RoadCost = c.RoadCost, RoadSpeed = c.RoadSpeed, ForestCostPercent = c.ForestCostPercent, ForestSpeedPercent = c.ForestSpeedPercent,
        ForestBlockedFrom = c.ForestBlockedFrom, DeployMaxGrade = c.DeployMaxGrade,
    };
}
