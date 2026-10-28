using SimCore.Bots;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The skirmish AI's squads (docs/design.md, "Squads: the skirmish AI", phase S5): it forms mixed squads of its infantry, sets a
// road ambush toward its base (inside its territory only), an air ambush with its Iglas, holds the key spot, marches its wave
// squads out and turns them to Assault near the enemy; its ambush springs on a column; everything deterministic; a full match.
public class SkirmishSquadTests(ITestOutputHelper output)
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    // RoadMap is 96 x 96 cells of flat terrain with a paved road two cells wide along rows `road` and road + 1 and dense woods north
    // of it (5 rows, columns 20 to 40, 3 rows off the road).
    private static World RoadMap(int road = 48)
    {
        const int size = 96;
        var w = Arena.New(size);
        var corners = new int[(size + 1) * (size + 1)];
        var flags = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var wood = x is >= 20 and <= 40 && y >= road - 8 && y <= road - 4;
                flags[y * size + x] = y == road || y == road + 1 ? TerrainMap.WithRoad(0, RoadKind.Paved) : (byte)(wood ? 3 : 0);
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, corners, flags, Rules));
        return w;
    }

    // Army: the AI's (player 2) base, a supply depot at (8, 40), and soldiers by it: 10 riflemen, 2 tank hunters, a marksman, a
    // sapper, a mortar and (iglas) Iglas; player 1 one rifleman far off, holding fire.
    private static List<Unit> Army(World w, int iglas = 0, int depotY = 40)
    {
        w.Spawn(2, "supply_depot", 8, depotY);
        var men = new List<Unit>();
        for (var i = 0; i < 10; i++)
        {
            men.Add(w.Spawn(2, "Rifleman", 10 + i % 5, depotY + 6 + i / 5));
        }
        men.Add(w.Spawn(2, "Tank hunter", 15, depotY + 6));
        men.Add(w.Spawn(2, "Tank hunter", 15, depotY + 7));
        men.Add(w.Spawn(2, "Marksman", 16, depotY + 6));
        men.Add(w.Spawn(2, "Sapper", 16, depotY + 7));
        men.Add(w.Spawn(2, "mortar", 17, depotY + 6));
        for (var i = 0; i < iglas; i++)
        {
            men.Add(w.Spawn(2, "manpads", 10 + i, depotY + 9));
        }
        w.Spawn(1, "Rifleman", 90, 90).Stance = Stance.HoldFire;
        return men;
    }

    private static List<Command> Play(World w, SkirmishAi ai, int ticks, List<Command>? into = null)
    {
        var all = into ?? [];
        for (var t = 0; t < ticks; t++)
        {
            foreach (var c in ai.Think(w))
            {
                all.Add(c);
                w.Issue(c);
            }
            w.Step();
        }
        return all;
    }

    private static List<Squad> SquadsOf(World w, byte player) => [.. w.Squads.Where(s => s.Owner == player)];

    // The cells of the line from a to b, a cell a step along its longer axis (the zone's cells).
    private static IEnumerable<Cell> Along(Cell a, Cell b)
    {
        var n = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        for (var i = 0; i <= n; i++)
        {
            yield return n == 0 ? a : new Cell(a.X + (b.X - a.X) * i / n, a.Y + (b.Y - a.Y) * i / n);
        }
    }

    // It forms its infantry at home into mixed squads of up to 8 (each class dealt round them), and gives them jobs: a road ambush,
    // the rest with the waves (at most half its ground squads stay home).
    [Fact]
    public void ItFormsMixedSquadsAndGivesThemJobs()
    {
        var w = RoadMap();
        var men = Army(w);
        var ai = new SkirmishAi(2);
        var cmds = Play(w, ai, 60);
        var squads = SquadsOf(w, 2);
        output.WriteLine(string.Join("; ", squads.Select(s => $"#{s.Id} {s.Members.Count} {s.Mode} {string.Join(",", s.Members.Select(id => w.Unit(id)!.Def.Archetype.Id).Distinct())}")));
        Assert.Equal(2, squads.Count); // 15 soldiers: 8 and 7
        Assert.All(squads, s => Assert.InRange(s.Members.Count, 4, 8));
        Assert.All(squads, s => Assert.True(s.Members.Select(id => w.Unit(id)!.Def.Archetype.Id).Distinct().Count() >= 3, "mixed"));
        Assert.Contains(cmds, c => c.Kind == CommandKind.FormSquad);
        Assert.Single(squads, s => s.Mode == SquadMode.Ambush); // half of 2 at home: the road ambush
        Assert.Contains(ai.SquadTasksForTests, t => t.Job == SkirmishAi.SquadJob.Wave);
        Assert.All(squads, s => Assert.Equal(Stance.Aggressive, s.Stance));
        _ = men;
    }

    // It sets a road ambush toward its base: the zone on the road, 6 to 30 cells, its start (where the enemy comes in) farther from
    // the base than its far end, in its territory; the trap gets set (Ready).
    [Fact]
    public void ItSetsARoadAmbushTowardItsBase()
    {
        var w = RoadMap();
        Army(w);
        var ai = new SkirmishAi(2);
        Play(w, ai, 60);
        var s = Assert.Single(SquadsOf(w, 2), q => q.Mode == SquadMode.Ambush);
        output.WriteLine($"zone {s.AmbushStart} -> {s.AmbushEnd}, home {ai.Home}, side {s.AmbushSide}");
        Assert.False(s.AmbushAir);
        Assert.All(Along(s.AmbushStart, s.AmbushEnd), c => Assert.True(w.Ground!.Road(c), $"{c} on the road"));
        Assert.All(Along(s.AmbushStart, s.AmbushEnd), c => Assert.True(ai.InTerritory(c), $"{c} in the territory"));
        var len = (s.AmbushEnd.Center() - s.AmbushStart.Center()).Len();
        Assert.InRange(len.Raw, 6 * Fixed.OneRaw, 30 * Fixed.OneRaw);
        Assert.True((s.AmbushStart.Center() - ai.Home.Center()).LenSq() > (s.AmbushEnd.Center() - ai.Home.Center()).LenSq(), "the enemy comes in at the far side");
        for (var t = 0; t < 2400 && s.State != SquadState.Ready; t++)
        {
            Play(w, ai, 1);
        }
        Assert.Equal(SquadState.Ready, s.State);
    }

    // A column of technicals drives down the road toward the base: the AI's ambush springs on it.
    [Fact]
    public void ItsAmbushSpringsOnAColumn()
    {
        var w = RoadMap();
        Army(w);
        var ai = new SkirmishAi(2);
        Play(w, ai, 60);
        var s = Assert.Single(SquadsOf(w, 2), q => q.Mode == SquadMode.Ambush);
        for (var t = 0; t < 2400 && s.State != SquadState.Ready; t++)
        {
            Play(w, ai, 1);
        }
        Assert.Equal(SquadState.Ready, s.State);
        var column = new List<Unit>();
        for (var i = 0; i < 3; i++)
        {
            column.Add(w.SpawnCrewed(1, "technical", 70 + 4 * i, 48, Facing.West).Vehicle);
        }
        w.Order(1, CommandKind.Move, column, cell: new Cell(4, 48));
        for (var t = 0; t < 1200 && s.State != SquadState.Sprung; t++)
        {
            Play(w, ai, 1);
        }
        output.WriteLine($"sprung by {s.AmbushTrigger} at {w.Tick}");
        Assert.Equal(SquadState.Sprung, s.State);
        Play(w, ai, 600);
        Assert.True(column.Count(v => !v.Alive) >= 1, "the trap hurt the column");
    }

    // With 3 Iglas or more at home it forms an Igla-heavy squad and sets an air ambush on the corridor from the enemy's side to its
    // base, in its territory.
    [Fact]
    public void ItSetsAnAirAmbushWithItsIglas()
    {
        var w = RoadMap();
        Army(w, iglas: 4);
        var ai = new SkirmishAi(2);
        Play(w, ai, 60);
        var air = Assert.Single(SquadsOf(w, 2), q => q.AmbushAir);
        var roles = air.Members.Select(id => w.SquadRoleOf(w.Unit(id)!)).ToList();
        Assert.True(roles.Count(r => r == SquadRole.AirGuard) * 2 > roles.Count, "mostly Iglas");
        Assert.Equal(4, roles.Count(r => r == SquadRole.AirGuard));
        Assert.All(Along(air.AmbushStart, air.AmbushEnd), c => Assert.True(ai.InTerritory(c), $"{c} in the territory"));
        Assert.True((air.AmbushStart.Center() - ai.Home.Center()).LenSq() > (air.AmbushEnd.Center() - ai.Home.Center()).LenSq());
        Assert.Equal(Stance.Guard, air.Stance);
    }

    // With more squads it also holds the key spot in front of its base, in its territory.
    [Fact]
    public void ItHoldsTheKeySpot()
    {
        var w = RoadMap();
        Army(w);
        for (var i = 0; i < 17; i++)
        {
            w.Spawn(2, "Rifleman", 10 + i % 6, 52 + i / 6);
        }
        var ai = new SkirmishAi(2);
        Play(w, ai, 60);
        var squads = SquadsOf(w, 2);
        Assert.Equal(4, squads.Count); // 32 soldiers: 4 squads of 8; two of them stay home (an ambush, the hold)
        var hold = Assert.Single(squads, q => q.Mode == SquadMode.Hold);
        Assert.True(ai.InTerritory(hold.HoldSpot), $"{hold.HoldSpot} in the territory");
        Assert.Single(squads, q => q.Mode == SquadMode.Ambush);
    }

    // It never orders an ambush outside its territory: a road only beyond it gets no ground ambush; with a small territory the zone
    // shrinks to fit or is not set; every ambush order's cells lie in the territory.
    [Theory]
    [InlineData(48, 30)]
    [InlineData(48, 14)]
    [InlineData(90, 30)]
    public void ItNeverOrdersAnAmbushOutsideItsTerritory(int road, int territory)
    {
        var w = RoadMap(road);
        Army(w, iglas: 3);
        var ai = new SkirmishAi(2) { TerritoryCells = territory, WaveEvery = 400 };
        var cmds = Play(w, ai, 2400);
        var ambushes = cmds.Where(c => c.Kind == CommandKind.SetSquadMode && c.Index == (int)SquadMode.Ambush).ToList();
        output.WriteLine(string.Join("; ", ambushes.Select(c => $"{c.Cell} -> {c.Cell2}")));
        foreach (var c in ambushes)
        {
            Assert.All(Along(c.Cell, c.Cell2!.Value), x => Assert.True(ai.InTerritory(x), $"{x} of {c.Cell} -> {c.Cell2} in the territory"));
        }
        var ground = ambushes.Count(c => w.Ground!.Road(c.Cell));
        Assert.True(road == 90 ? ground == 0 : ground >= 1, $"{ground} road ambushes");
    }

    // The same game plays the same twice: the same commands, the same world.
    [Fact]
    public void Deterministic()
    {
        (List<Command>, ulong) Game()
        {
            var w = RoadMap();
            Army(w, iglas: 3);
            var ai = new SkirmishAi(2) { WaveEvery = 300 };
            var cmds = Play(w, ai, 1200);
            return (cmds, w.Hash());
        }
        var (a, ha) = Game();
        var (b, hb) = Game();
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.True(a[i].Tick == b[i].Tick && a[i].Kind == b[i].Kind && a[i].Cell == b[i].Cell && a[i].Cell2 == b[i].Cell2 && a[i].Index == b[i].Index &&
                a[i].Units.SequenceEqual(b[i].Units), $"command {i}");
        }
        Assert.Equal(ha, hb);
        Assert.Contains(a, c => c.Kind == CommandKind.FormSquad);
        Assert.Contains(a, c => c.Kind == CommandKind.SetSquadMode);
    }

    // A full match of the skirmish AI against itself (the battle lobby with 4 Iglas and a mortar a side, on SimRun -skirmish's map of
    // roads and woods, seed 1): the squads form, set their traps, march out, and the match ends.
    [Fact]
    public void AFullMatchRunsAndEnds()
    {
        var content = TestContent.Mvp;
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "battle.json"));
        setup = setup with
        {
            Players = [.. setup.Players.Select(p => p with { Army = [.. p.Army, new ArmyEntry { Unit = "manpads", Count = 4 }, new ArmyEntry { Unit = "mortar" }] })],
        };
        setup = setup.For(content, 1);
        var w = Replay.NewWorld(content, setup, SkirmishMap());
        var ais = w.Players.Select(p => new SkirmishAi(p.Id)).ToList();
        int formed = 0, ambushes = 0;
        while (w.Tick < 20 * 60 * World.TicksPerSecond && !w.Result.Over)
        {
            foreach (var ai in ais)
            {
                foreach (var c in ai.Think(w))
                {
                    w.Issue(c);
                    ambushes += c.Kind == CommandKind.SetSquadMode && c.Index == (int)SquadMode.Ambush ? 1 : 0;
                }
            }
            w.Step();
            formed += w.SquadEvents.Count(e => e.Kind == SquadEventKind.Formed);
        }
        output.WriteLine($"{w.Tick / 20} s, winner {w.Result.Winner}, squads formed {formed}, ambushes {ambushes}");
        Assert.True(w.Result.Over, "the match ended");
        Assert.True(formed >= 4, $"{formed} squads formed");
        Assert.True(ambushes >= 2, $"{ambushes} ambushes ordered");
    }

    // SkirmishMap is SimRun -skirmish's map (src/SimRun/SkirmishRun.cs).
    private static TerrainMap SkirmishMap()
    {
        const int w = 128, h = 96;
        var corners = new int[(w + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        {
            for (var x = 0; x <= w; x++)
            {
                var d1 = (int)Math.Sqrt((x - 45) * (x - 45) + (y - 30) * (y - 30));
                var d2 = (int)Math.Sqrt((x - 82) * (x - 82) + (y - 66) * (y - 66));
                corners[y * (w + 1) + x] = Math.Max(Math.Max(0, 2048 - 205 * d1), Math.Max(0, 2048 - 205 * d2));
            }
        }
        var flags = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                byte f = 0;
                if (y is 47 or 48)
                {
                    f = TerrainMap.WithRoad(0, RoadKind.Paved);
                }
                else if (x is 30 or 97)
                {
                    f = TerrainMap.WithRoad(0, RoadKind.Gravel);
                }
                else if (y is >= 40 and <= 44 && (x is >= 18 and <= 42 || x is >= 85 and <= 109))
                {
                    f = 3;
                }
                else if (y is >= 51 and <= 54 && (x is >= 22 and <= 40 || x is >= 87 and <= 105))
                {
                    f = 2;
                }
                flags[y * w + x] = f;
            }
        }
        return new TerrainMap(w, h, corners, flags, Rules);
    }
}
