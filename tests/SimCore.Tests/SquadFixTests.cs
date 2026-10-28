using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The client's playtest fixes to squad position picking (docs/design.md, "Squads: playtest fixes", decisions 691 to 695): the
// mortar never takes a cell it can't set up on, the marksman takes a clear hill with long sight, and the riflemen take a good
// building within reach and sight of the threat even when it is not near the spot; an ambush's tail-end RPG is not the first to die.
public class SquadFixTests(ITestOutputHelper output)
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static readonly Cell Spot = new(32, 32);
    private static readonly Cell Toward = new(32, 22); // the facing dragged north: the threat 10 cells ahead

    private static void Hold(World w, Unit member) =>
        w.Issue(new Command
        {
            Tick = w.Tick, Player = member.Owner, Kind = CommandKind.SetSquadMode, Units = [member.Id], Index = (int)SquadMode.Hold,
            Cell = Spot, Cell2 = Toward,
        });

    private static Squad Form(World w, IEnumerable<Unit> units)
    {
        var list = units.ToList();
        w.Issue(new Command { Tick = w.Tick, Player = list[0].Owner, Kind = CommandKind.FormSquad, Units = [.. list.Select(u => u.Id)] });
        w.Run(1);
        return w.SquadOf(list[0].Id) ?? throw new InvalidOperationException("no squad formed");
    }

    private static void Placed(World w, Squad s)
    {
        for (var t = 0; t < 400 && !s.HoldPlaced; t++)
        {
            w.Run(1);
        }
        Assert.True(s.HoldPlaced, "it picked its cells");
    }

    private static Cell PickOf(World w, Unit u) => w.SquadHoldPick(u.Id)!.Value.Cell;

    private static string Dump(World w, Squad s) =>
        string.Join("; ", s.HoldPicks.Select(p => $"{w.SquadRoleOf(w.Unit(p.Unit)!)} {p.Cell}{(p.Building != 0 ? " in " + p.Building : "")}"));

    // Map is 64 x 64 cells of terrain: corner heights from height(x, y) (raw), forest density from forest(x, y).
    private static World Map(Func<int, int, int> height, Func<int, int, int> forest)
    {
        const int size = 64;
        var w = Arena.New(size);
        var corners = new int[(size + 1) * (size + 1)];
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                corners[y * (size + 1) + x] = height(x, y);
            }
        }
        var flags = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                flags[y * size + x] = (byte)forest(x, y);
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, corners, flags, Rules));
        return w;
    }

    // The ridge of SquadHoldTests' crafted map: 2.2 m high across the map 5 cells ahead of the spot (corner rows 23 to 30).
    private static int Ridge(int y)
    {
        int[] ridge = [0, 410, 819, 1126, 1126, 819, 410, 0];
        return y is >= 23 and <= 30 ? ridge[y - 23] : 0;
    }

    // Support on slopes: the ground behind the spot, where the mortar wants to be (10 cells or more behind the line, far from the
    // threat), rises 30% (0.6 m a cell) from row 40 to row 46 across columns 18 to 46; the foot class can't set up from 25%. The
    // mortar takes a cell he can set up on and sets up there.
    [Fact]
    public void TheMortarNeverPicksACellItCannotSetUpOn()
    {
        var w = Map((x, y) => x is >= 18 and <= 46 ? Math.Clamp(y - 40, 0, 6) * 307 : 0, (_, _) => 0);
        var line = new List<Unit> { w.Spawn(1, "Rifleman", 30, 34), w.Spawn(1, "Rifleman", 31, 34), w.Spawn(1, "Rifleman", 32, 34) };
        var mortar = w.Spawn(1, "mortar", 33, 34);
        var s = Form(w, [.. line, mortar]);
        Hold(w, line[0]);
        Placed(w, s);
        var at = PickOf(w, mortar);
        output.WriteLine($"mortar picks {at}, grade {w.Ground!.Grade(at)}%: {Dump(w, s)}");
        Assert.True(w.Ground!.Grade(at) < 25, $"the mortar on ground he can set up on: {at} at {w.Ground.Grade(at)}%");
        w.Run(600);
        Assert.Equal(DeployState.Deployed, mortar.Deploy);
    }

    // Recon on a hilltop: the ridge ahead hides the threat's side from the flat ground; a clear hill (no trees, 5 m, 25% sides) 10
    // cells right and 4 back sees over it; a dense wood behind the spot offers cover and safety. The marksman takes the hill.
    [Fact]
    public void TheMarksmanTakesAClearHillWithLongSight()
    {
        var w = Map((x, y) =>
        {
            var d = (int)Math.Sqrt((x - 42) * (x - 42) + (y - 37) * (y - 37));
            return Math.Max(Ridge(y), Math.Max(0, 2560 - 410 * d));
        }, (x, y) => x is >= 26 and <= 31 && y is >= 36 and <= 40 ? 3 : 0);
        var line = new List<Unit> { w.Spawn(1, "Rifleman", 30, 34), w.Spawn(1, "Rifleman", 31, 34), w.Spawn(1, "Rifleman", 32, 34) };
        var sniper = w.Spawn(1, "Marksman", 33, 34);
        var s = Form(w, [.. line, sniper]);
        Hold(w, line[0]);
        Placed(w, s);
        var at = PickOf(w, sniper);
        var view = w.SquadHoldScores(sniper.Id);
        foreach (var c in view.Where(c => c.Cell == at || w.Ground!.HeightAt(c.Cell.Center()).Raw >= 1536).Where(c => c.Allowed).OrderByDescending(c => c.Score).Take(12))
        {
            output.WriteLine($"{c.Cell} score {c.Score} cover {c.Cover} sight {c.Sight} open {c.Open} spacing {c.Spacing} safety {c.Safety} range {c.Range} near {c.Near} ok {c.Allowed} h {w.Ground!.HeightAt(c.Cell.Center()).Raw}");
        }
        output.WriteLine($"marksman picks {at}, height {w.Ground!.HeightAt(at.Center()).Raw}: {Dump(w, s)}");
        Assert.True(w.Ground!.HeightAt(at.Center()).Raw >= 512, $"the marksman up the hill (1 m or more): {at}");
        Assert.True(view.Single(c => c.Cell == at).Sight >= 80, "he sees over the ridge");
    }

    // The line garrisons a building not near the spot: a small house 6 cells right and 5 ahead, within the riflemen's reach of the
    // threat and with sight of it; the spot itself is open ground with a low wall. Riflemen take the house.
    [Fact]
    public void TheLineGarrisonsAGoodBuildingAwayFromTheSpot()
    {
        var w = Map((_, _) => 0, (x, y) => x is >= 27 and <= 36 && y is >= 31 and <= 33 ? 3 : 0);
        var house = w.Unit(w.Spawn(w.Player(1)!.Setup.Find(w.Content, "house_small")!, new Cell(38, 25), Facing.South))!;
        for (var x = 29; x <= 33; x++)
        {
            w.Grid.Set(new Cell(x, 30), Terrain.Blocked); // a low wall just ahead of the spot
        }
        var line = new List<Unit>
        {
            w.Spawn(1, "Rifleman", 30, 36), w.Spawn(1, "Rifleman", 31, 36), w.Spawn(1, "Rifleman", 32, 36), w.Spawn(1, "Rifleman", 33, 36),
        };
        var s = Form(w, line);
        Hold(w, line[0]);
        Placed(w, s);
        output.WriteLine(Dump(w, s));
        foreach (var c in w.SquadHoldScores(line[3].Id).Where(c => c.Building != 0 || c.Allowed).OrderByDescending(c => c.Score).Take(10))
        {
            output.WriteLine($"{c.Cell} b {c.Building} score {c.Score} cover {c.Cover} sight {c.Sight} spacing {c.Spacing} range {c.Range} near {c.Near} ok {c.Allowed}");
        }
        var inHouse = s.HoldPicks.Count(p => p.Building == house.Id);
        Assert.True(inHouse >= 3, $"riflemen in the house: {Dump(w, s)}");
    }

    // The ambush's start-end RPG (the tail's end, where the column comes in) keeps to the far half of his band off the zone (5.5 cells
    // or more, deeper in the woods), so the column passes him before it sees him: he is not the first to die (decision 694). The
    // map and squad of SquadAmbushTests' column test: a road along rows 30 and 31, dense woods north of it, a full squad of 10.
    [Fact]
    public void TheStartEndRpgIsNotTheFirstToDie()
    {
        const int size = 64;
        var w = Arena.New(size);
        var flags = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                flags[y * size + x] = y is 30 or 31 ? TerrainMap.WithRoad(0, RoadKind.Paved) : (byte)(x is >= 14 and <= 42 && y is >= 21 and <= 27 ? 3 : 0);
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, new int[(size + 1) * (size + 1)], flags, Rules));
        var men = new List<Unit>
        {
            w.Spawn(1, "Rifleman", 26, 14), w.Spawn(1, "Rifleman", 27, 14), w.Spawn(1, "Rifleman", 28, 14), w.Spawn(1, "Tank hunter", 29, 14),
            w.Spawn(1, "Tank hunter", 30, 14), w.Spawn(1, "kornet", 26, 15), w.Spawn(1, "Marksman", 27, 15), w.Spawn(1, "Sapper", 28, 15),
            w.Spawn(1, "mortar", 29, 15), w.Spawn(1, "manpads", 30, 15),
        };
        var s = Form(w, men);
        w.Issue(new Command
        {
            Tick = w.Tick, Player = 1, Kind = CommandKind.SetSquadMode, Units = [men[0].Id], Index = (int)SquadMode.Ambush, Cell = new Cell(16, 30),
            Cell2 = new Cell(40, 30),
        });
        for (var t = 0; t < 1500 && s.State != SquadState.Ready; t++)
        {
            w.Run(1);
        }
        Assert.Equal(SquadState.Ready, s.State);
        var tail = men[4]; // the second RPG takes the tail's end (the first, the lead's)
        var at = PickOf(w, tail);
        output.WriteLine($"tail RPG at {at}: {Dump(w, s)}");
        Assert.True(at.X <= 24 && 30 - at.Y >= 5, $"the tail RPG near the start, 5 cells or more off the road: {at}");
        var column = new List<Unit>();
        for (var i = 0; i < 3; i++)
        {
            column.Add(w.SpawnCrewed(2, "technical", 2 + 3 * i, 30, Facing.East).Vehicle);
        }
        w.Order(2, CommandKind.Move, column, cell: new Cell(62, 30));
        Unit? first = null;
        for (var t = 0; t < 900 && first is null; t++)
        {
            w.Run(1);
            first = men.FirstOrDefault(u => !u.Alive);
        }
        output.WriteLine($"first to die: {(first is null ? "none" : w.SquadRoleOf(first) + " " + first.Id)}");
        Assert.NotEqual(tail, first);
    }
}
