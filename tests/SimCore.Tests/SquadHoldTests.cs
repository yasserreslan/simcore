using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// Infantry squads, phase S2 (docs/design.md, "Squads: Hold and position picking"): the Hold order and its facing, each role on a
// sensible cell of a crafted map (the line behind a wall, the sniper on a hill, Iglas under trees 8 or more cells apart, the medic
// behind the line, the mortar set up far back), garrisons taken as positions, the pick order, the cell cap, the leash to the Hold
// cell, leaving Hold, the debug scores, determinism and replays.
public class SquadHoldTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static readonly Cell Spot = new(32, 32);
    private static readonly Cell Toward = new(32, 22); // the facing dragged north: the threat 10 cells ahead

    private static void Hold(World w, byte player, IEnumerable<Unit> units, Cell spot, Cell? toward) =>
        w.Issue(new Command
        {
            Tick = w.Tick, Player = player, Kind = CommandKind.SetSquadMode, Units = [.. units.Select(u => u.Id)], Index = (int)SquadMode.Hold,
            Cell = spot, Cell2 = toward,
        });

    private static Squad Form(World w, IEnumerable<Unit> units)
    {
        var list = units.ToList();
        w.Issue(new Command { Tick = w.Tick, Player = list[0].Owner, Kind = CommandKind.FormSquad, Units = [.. list.Select(u => u.Id)] });
        w.Run(1);
        return w.SquadOf(list[0].Id) ?? throw new InvalidOperationException("no squad formed");
    }

    // Placed runs until s has picked its cells (at most limit ticks).
    private static void Placed(World w, Squad s, int limit = 400)
    {
        for (var t = 0; t < limit && !s.HoldPlaced; t++)
        {
            w.Run(1);
        }
        Assert.True(s.HoldPlaced, "it picked its cells");
    }

    // Crafted is the battle lobby (the Sentinel's with sentinel: it has the medic) on 64 x 64 cells of terrain: a low wall (rock)
    // of 5 cells just ahead of the spot, a ridge 2.2 m high across the map 5 cells ahead (it hides the ground behind it from the
    // threat, and the threat's side from the ground behind it), a hill 6 m high 8 cells to the right of the spot (it sees over the
    // ridge), and two small dense woods to the left and behind, 10 cells apart.
    private static World Crafted(bool sentinel = false)
    {
        const int size = 64;
        var w = sentinel ? Arena.NewSentinel(size) : Arena.New(size);
        var corners = new int[(size + 1) * (size + 1)];
        int[] ridge = [0, 410, 819, 1126, 1126, 819, 410, 0]; // corner rows 23 to 30 (0.8 m steps to 2.2 m)
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                var h = y is >= 23 and <= 30 ? ridge[y - 23] : 0;
                var d = (int)Math.Sqrt((x - 40) * (x - 40) + (y - 33) * (y - 33)); // the hill: 6 m, 0.8 m down a cell (40%)
                h = Math.Max(h, Math.Max(0, 3072 - 410 * d));
                corners[y * (size + 1) + x] = h;
            }
        }
        var flags = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var woodA = x is >= 21 and <= 24 && y is >= 31 and <= 34;
                var woodB = x is >= 26 and <= 29 && y is >= 40 and <= 43;
                flags[y * size + x] = (byte)(woodA || woodB ? 3 : 0);
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, corners, flags, Rules));
        for (var x = 26; x <= 30; x++)
        {
            w.Grid.Set(new Cell(x, 31), Terrain.Blocked); // the wall
        }
        return w;
    }

    private static (Squad S, List<Unit> Line, Unit Sniper, List<Unit> Iglas, Unit Mortar) CraftedSquad(World w)
    {
        var line = new List<Unit> { w.Spawn(1, "Rifleman", 30, 36), w.Spawn(1, "Rifleman", 31, 36), w.Spawn(1, "Rifleman", 32, 36) };
        var sniper = w.Spawn(1, "Marksman", 33, 36);
        var iglas = new List<Unit> { w.Spawn(1, "manpads", 34, 36), w.Spawn(1, "manpads", 30, 37) };
        var mortar = w.Spawn(1, "mortar", 32, 37);
        var s = Form(w, [.. line, sniper, .. iglas, mortar]);
        return (s, line, sniper, iglas, mortar);
    }

    private static Cell PickOf(World w, Unit u) => w.SquadHoldPick(u.Id)!.Value.Cell;

    // Each role lands on a sensible cell of the crafted map: the riflemen right behind the wall, the sniper up the hill, the two
    // Iglas in the woods 8 or more cells apart, the mortar 10 or more cells behind the line; then they walk there and hold, and
    // the mortar sets up.
    [Fact]
    public void EachRoleTakesASensibleCell()
    {
        var w = Crafted();
        var (s, line, sniper, iglas, mortar) = CraftedSquad(w);
        Hold(w, 1, [line[0]], Spot, Toward);
        w.Run(1);
        Assert.Equal(SquadMode.Hold, s.Mode);
        Assert.Equal(Spot, s.HoldSpot);
        Assert.Equal(new Vec(Fixed.Zero, -Fixed.One), s.HoldFacing);
        Assert.Equal(Toward.Center(), s.HoldThreat);
        Placed(w, s);
        Assert.Contains(w.SquadEvents, e => e.Kind == SquadEventKind.Placed && e.Squad == s.Id);
        var dump = string.Join("; ", s.HoldPicks.Select(p => $"{w.SquadRoleOf(w.Unit(p.Unit)!)} {p.Cell}"));
        Assert.True(line.Count(u => PickOf(w, u) is { Y: 32, X: >= 25 and <= 31 }) >= 2, $"riflemen behind the wall: {dump}");
        Assert.All(line, u => Assert.True(w.SquadHoldScores(u.Id).Single(c => c.Cell == PickOf(w, u)).Cover >= 60, $"rifleman {u.Id} in cover: {dump}"));
        var hill = PickOf(w, sniper);
        // Up the hill, his eye over the ridge: he sees the threat's side, which the flat ground behind the ridge does not.
        Assert.True(w.Ground!.HeightAt(hill.Center()).Raw >= 384, $"the sniper up the hill: {dump}");
        var view = w.SquadHoldScores(sniper.Id);
        Assert.True(view.Single(c => c.Cell == hill).Sight >= 80, $"he sees: {dump}");
        Assert.True(view.Single(c => c.Cell == new Cell(28, 34)).Sight < 30, "the ground behind the ridge does not");
        var (a, b) = (PickOf(w, iglas[0]), PickOf(w, iglas[1]));
        Assert.True(w.Ground!.Forest(a) == 3 && w.Ground!.Forest(b) == 3, $"Iglas in the woods: {dump}");
        Assert.True((a.Center() - b.Center()).LenSq() >= 64L * Fixed.OneRaw * Fixed.OneRaw, $"Iglas 8 cells apart: {dump}");
        var lineY = line.Average(u => PickOf(w, u).Y);
        Assert.True(PickOf(w, mortar).Y >= lineY + 10, $"the mortar 10 cells back: {dump}");
        w.Run(600);
        foreach (var id in s.Members)
        {
            var u = w.Unit(id)!;
            Assert.True(World.Within(u.Pos, w.SquadHoldPick(id)!.Value.Cell.Center(), Fixed.One), $"{id} at {u.Pos}, his cell {w.SquadHoldPick(id)}");
            Assert.Equal(w.SquadHoldPick(id)!.Value.Cell, w.SquadSlotCell(id)); // the slot query shows the Hold cell
        }
        Assert.Equal(DeployState.Deployed, mortar.Deploy); // the support sets up on its cell
        Assert.Equal(SquadState.Idle, s.State);
    }

    // The medic (the Sentinel's) holds behind cover, behind the line (2 cells or more), near the middle; the riflemen behind the wall.
    [Fact]
    public void TheMedicHoldsBehindTheLine()
    {
        var w = Crafted(sentinel: true);
        var line = new List<Unit> { w.Spawn(1, "Rifleman", 30, 36), w.Spawn(1, "Rifleman", 31, 36), w.Spawn(1, "Rifleman", 32, 36) };
        var medic = w.Spawn(1, "sentinel_medic", 33, 36);
        var s = Form(w, [.. line, medic]);
        Hold(w, 1, [medic], Spot, Toward);
        Placed(w, s);
        var dump = string.Join("; ", s.HoldPicks.Select(p => $"{w.SquadRoleOf(w.Unit(p.Unit)!)} {p.Cell}"));
        Assert.True(line.Count(u => PickOf(w, u) is { Y: 32, X: >= 25 and <= 31 }) >= 2, $"riflemen behind the wall: {dump}");
        var m = PickOf(w, medic);
        Assert.True(m.Y >= line.Min(u => PickOf(w, u).Y) + 2 && Math.Abs(m.X - Spot.X) <= 5, $"the medic behind the line, central: {dump}");
        Assert.True(w.SquadHoldScores(medic.Id).Single(c => c.Cell == m).Cover > 0, $"behind cover: {dump}");
    }

    // The debug view scores every candidate for a member's role, with the factors, and its best allowed cell is his pick.
    [Fact]
    public void TheDebugScoresExplainThePick()
    {
        var w = Crafted();
        var (s, line, sniper, _, mortar) = CraftedSquad(w);
        Hold(w, 1, [line[0]], Spot, Toward);
        Placed(w, s);
        var scores = w.SquadHoldScores(sniper.Id);
        Assert.True(scores.Count > 300, $"{scores.Count} candidates");
        // The mortar picks last: the view (everyone else's picks taken) is what he saw, so its best allowed cell is his.
        var last = w.SquadHoldScores(mortar.Id);
        var best = last.Where(c => c.Allowed && c.Building == 0).OrderByDescending(c => c.Score).ThenBy(c => c.Cell.Y * 64 + c.Cell.X).First();
        Assert.Equal(PickOf(w, mortar), best.Cell);
        Assert.All(scores, c => Assert.InRange(c.Sight, 0, 100));
        var wall = w.SquadHoldScores(line[0].Id).Single(c => c.Cell == new Cell(28, 32));
        Assert.True(wall.Cover >= 60, $"cover behind the wall {wall.Cover}");
        Assert.Empty(w.SquadHoldScores(w.Spawn(1, "Rifleman", 5, 5).Id)); // no squad
    }

    // Garrisons are positions: a small house ahead of the spot within reach of the threat takes riflemen and anti-armour soldiers
    // (up to its room); the Igla and the mortar stay out. They hold from inside; a move brings them out with the squad.
    [Fact]
    public void GarrisonsAreUsed()
    {
        var w = Arena.New(64);
        var house = w.Unit(w.Spawn(w.Player(1)!.Setup.Find(w.Content, "house_small")!, new Cell(30, 24), Facing.South))!;
        var men = new List<Unit>
        {
            w.Spawn(1, "Rifleman", 30, 38), w.Spawn(1, "Rifleman", 31, 38), w.Spawn(1, "Rifleman", 32, 38), w.Spawn(1, "Tank hunter", 33, 38),
            w.Spawn(1, "Marksman", 34, 38), w.Spawn(1, "manpads", 30, 39), w.Spawn(1, "mortar", 31, 39),
        };
        var s = Form(w, men);
        Hold(w, 1, [men[0]], Spot, Toward);
        Placed(w, s);
        var inHouse = s.HoldPicks.Where(p => p.Building == house.Id).Select(p => w.Unit(p.Unit)!).ToList();
        Assert.True(inHouse.Count is >= 2 and <= 4, $"{inHouse.Count} picked the house");
        Assert.All(inHouse, u => Assert.Contains(w.SquadRoleOf(u), new[] { SquadRole.Line, SquadRole.Flank, SquadRole.Recon }));
        Assert.Equal(0u, w.SquadHoldPick(men[5].Id)!.Value.Building);
        Assert.Equal(0u, w.SquadHoldPick(men[6].Id)!.Value.Building);
        w.Run(400);
        Assert.All(inHouse, u => Assert.Equal(house.Id, u.Garrison));
        Assert.All(inHouse, u => Assert.True(w.SquadHoldGarrisoned(u.Id)));
        Assert.Equal(0u, men[5].Garrison);
        Assert.Equal(0u, men[6].Garrison);
        Assert.False(s.Loose);
        // A move given to one inside: the squad comes out and walks off; it holds again where it arrives, facing the way it went.
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [inHouse[0].Id], Cell = new Cell(32, 56) });
        w.Run(1);
        Assert.All(men, u => Assert.Equal(0u, u.Garrison));
        Assert.Equal(SquadState.Moving, s.State);
        Assert.False(s.HoldPlaced);
        Assert.Empty(s.HoldPicks);
        w.Run(800);
        Assert.Equal(SquadState.Idle, s.State);
        Assert.True(s.HoldPlaced);
        Assert.True(s.HoldFacing.Y.Raw > 0, $"facing south {s.HoldFacing}");
        Assert.Equal(SquadMode.Hold, s.Mode);
    }

    // Members already in a building that is not their Hold position come out to their cells.
    [Fact]
    public void MembersInAnotherBuildingComeOut()
    {
        var w = Arena.New(64);
        var house = w.Unit(w.Spawn(w.Player(1)!.Setup.Find(w.Content, "house_small")!, new Cell(10, 40), Facing.South))!;
        var men = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 10 + i, 50)).ToList();
        var s = Form(w, men);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Enter, Units = [men[0].Id], Target = house.Id });
        w.Run(300);
        Assert.All(men, u => Assert.Equal(house.Id, u.Garrison));
        Hold(w, 1, [men[0]], new Cell(20, 44), new Cell(32, 44));
        Placed(w, s);
        Assert.All(s.HoldPicks, p => Assert.NotEqual(house.Id, p.Building)); // the house is out of their reach of the threat: no position
        w.Run(400);
        Assert.All(men, u => Assert.Equal(0u, u.Garrison));
        Assert.All(men, u => Assert.True(World.Within(u.Pos, w.SquadHoldPick(u.Id)!.Value.Cell.Center(), Fixed.One), $"{u.Id} on his cell"));
    }

    // The pick order is the content's (recon, long flank, flank, line, air guard, support, medic): on open ground with one best
    // cell for everyone (the weights made all equal, nearness only), the first role in the order takes it.
    [Theory]
    [InlineData(new[] { "recon", "longFlank", "flank", "line", "airGuard", "support", "medic" }, "Marksman")]
    [InlineData(new[] { "line", "recon", "longFlank", "flank", "airGuard", "support", "medic" }, "Rifleman")]
    [InlineData(new[] { "flank", "recon", "longFlank", "line", "airGuard", "support", "medic" }, "Tank hunter")]
    public void MembersPickInTheContentsOrder(string[] order, string first)
    {
        var w = NearOnly(order);
        var men = new List<Unit> { w.Spawn(1, "Rifleman", 20, 40), w.Spawn(1, "Tank hunter", 21, 40), w.Spawn(1, "Marksman", 22, 40) };
        var s = Form(w, men);
        Hold(w, 1, [men[0]], Spot, Toward);
        Placed(w, s);
        var at = s.HoldPicks.Single(p => p.Cell == Spot);
        Assert.Equal(first, w.Unit(at.Unit)!.Def.Name);
    }

    // NearOnly is the battle lobby's world with content whose Hold weights are nearness only for every role, no limits, and the
    // pick order given.
    private static World NearOnly(string[] order, int cells = 600, int radius = 120)
    {
        var sources = TestContent.MvpSources().Select(x =>
        {
            if (x.Name != "squad.json")
            {
                return x;
            }
            var json = System.Text.RegularExpressions.Regex.Replace(x.Json, "\"weights\": \\[[^\\]]*\\], \"back\":\\s*-?\\d+,\\s*\"apart\": \\[\\d+, \\d+\\]",
                "\"weights\": [0, 0, 0, 0, 0, 0, 0, 1], \"back\": -500, \"apart\": [10, 0]");
            json = json.Replace("\"order\": [\"recon\", \"longFlank\", \"flank\", \"line\", \"airGuard\", \"support\", \"medic\"]",
                "\"order\": [" + string.Join(", ", order.Select(o => $"\"{o}\"")) + "]", StringComparison.Ordinal);
            json = json.Replace("\"cells\": 600", $"\"cells\": {cells}", StringComparison.Ordinal).Replace("\"radius\": 120", $"\"radius\": {radius}", StringComparison.Ordinal);
            return x with { Json = json };
        }).ToList();
        var content = ContentLoader.Load(sources);
        Assert.Equal(1, content.Squad!.Hold!.Roles[(int)SquadRole.Line].Weights.Near);
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "battle.json")).For(content, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(64, 64), MatchCompiler.Compile(content, setup));
    }

    // Scoring looks at the cell cap at most (the nearest the spot first): a radius of 20 cells has 1,257 cells, the content caps
    // them at 600 (and at 50 the cells picked are among the 50 nearest).
    [Fact]
    public void ScoringIsCappedAtTheCells()
    {
        var order = new[] { "recon", "longFlank", "flank", "line", "airGuard", "support", "medic" };
        foreach (var (cells, want) in new[] { (600, 600), (50, 50) })
        {
            var w = NearOnly(order, cells, radius: 200);
            var men = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 28 + i, 40)).ToList();
            var s = Form(w, men);
            Hold(w, 1, [men[0]], Spot, Toward);
            Placed(w, s);
            var placed = w.SquadEvents.Single(e => e.Kind == SquadEventKind.Placed);
            Assert.Equal((uint)want, placed.Other);
            Assert.Equal(want, w.SquadHoldScores(men[0].Id).Count);
            Assert.All(s.HoldPicks, p => Assert.True((p.Cell.Center() - Spot.Center()).LenSq() <= 25L * Fixed.OneRaw * Fixed.OneRaw));
        }
    }

    // In a fight a member fights as the unit AI says, his leash point his Hold cell: he chases an enemy that hurt him and walks back
    // to his cell.
    [Fact]
    public void TheLeashPointIsTheHoldCell()
    {
        var w = Arena.New(96);
        var men = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 40 + i, 40)).ToList();
        var s = Form(w, men);
        Hold(w, 1, [men[0]], new Cell(42, 42), new Cell(42, 30));
        Placed(w, s);
        w.Run(200);
        var enemy = w.Spawn(2, "Marksman", 46, 52, Facing.North);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.Attack, Units = [enemy.Id], Target = men[0].Id });
        var chased = false;
        for (var t = 0; t < 600 && enemy.Alive; t++)
        {
            w.Run(1);
            chased |= men.Any(u => u.Ai == AiState.Engage);
        }
        Assert.True(chased, "they went after him");
        Assert.False(enemy.Alive);
        w.Run(400);
        foreach (var u in men.Where(m => m.Alive))
        {
            Assert.Equal(AiState.None, u.Ai);
            Assert.True(World.Within(u.Pos, w.SquadHoldPick(u.Id)!.Value.Cell.Center(), Fixed.One), $"{u.Id} back on his cell");
        }
        Assert.True(s.HoldPlaced);
    }

    // A Hold far from the squad: it marches there first (as March), then picks; without a dragged facing it faces the way it went.
    // March again ends Hold: the picks go, the mortar packs up.
    [Fact]
    public void AFarHoldMarchesThereFirstAndMarchEndsIt()
    {
        var w = Arena.New(96);
        var men = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 10 + i, 10)).ToList();
        men.Add(w.Spawn(1, "mortar", 14, 10));
        var s = Form(w, men);
        Hold(w, 1, [men[0]], new Cell(60, 10), null);
        w.Run(1);
        Assert.Equal(SquadState.Moving, s.State);
        Assert.False(s.HoldPlaced);
        Placed(w, s, 1200);
        Assert.True(s.HoldFacing.X.Raw > 900, $"facing east {s.HoldFacing}");
        Assert.Equal(new Cell(60, 10), s.HoldSpot);
        w.Run(400);
        Assert.Equal(DeployState.Deployed, men[4].Deploy);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetSquadMode, Units = [men[1].Id], Index = (int)SquadMode.March });
        w.Run(1);
        Assert.Equal(SquadMode.March, s.Mode);
        Assert.False(s.HoldPlaced);
        Assert.Empty(s.HoldPicks);
        Assert.Equal(DeployState.Stowing, men[4].Deploy);
        Assert.Null(w.SquadHoldPick(men[0].Id));
    }

    // Two squads in one Hold hold side by side across the facing, the group gap apart, and never pick the same cell.
    [Fact]
    public void TwoSquadsHoldSideBySide()
    {
        var w = Arena.New(96);
        var a = Form(w, Enumerable.Range(0, 5).Select(i => w.Spawn(1, "Rifleman", 40 + i, 60)));
        var b = Form(w, Enumerable.Range(0, 5).Select(i => w.Spawn(1, "Rifleman", 40 + i, 62)));
        Hold(w, 1, [w.Unit(a.Members[0])!, w.Unit(b.Members[0])!], new Cell(48, 48), new Cell(48, 30));
        w.Run(1);
        Assert.Equal(new Cell(42, 48), a.HoldSpot);
        Assert.Equal(new Cell(54, 48), b.HoldSpot);
        Placed(w, a, 800);
        Placed(w, b, 800);
        var cells = a.HoldPicks.Concat(b.HoldPicks).Select(p => p.Cell).ToList();
        Assert.Equal(cells.Count, cells.Distinct().Count());
    }

    // A soldier who joins a holding squad picks the best cell left; the others keep theirs. A lost one's pick goes.
    [Fact]
    public void JoinersPickTheCellsLeft()
    {
        var w = Arena.New(64);
        var men = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 28 + i, 40)).ToList();
        var s = Form(w, men);
        Hold(w, 1, [men[0]], Spot, Toward);
        Placed(w, s);
        var before = s.HoldPicks.ToList();
        var late = w.Spawn(1, "Rifleman", 20, 40);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.FormSquad, Units = [late.Id, men[0].Id] });
        w.Run(2);
        Assert.Equal(5, s.HoldPicks.Count);
        Assert.All(before, p => Assert.Contains(p, s.HoldPicks));
        Assert.DoesNotContain(s.HoldPicks.Where(p => p.Unit != late.Id), p => p.Cell == w.SquadHoldPick(late.Id)!.Value.Cell);
        men[3].HP = 0;
        w.Run(1);
        Assert.Equal(4, s.HoldPicks.Count);
        Assert.Null(w.SquadHoldPick(men[3].Id));
    }

    // Content: the hold block is optional (without it Hold is ignored); a bad role order is refused; its numbers are hashed.
    [Fact]
    public void TheHoldBlockIsContent()
    {
        static List<ContentSource> Edit(Func<string, string> f) =>
            [.. TestContent.MvpSources().Select(x => x.Name == "squad.json" ? x with { Json = f(x.Json) } : x)];
        var noHold = ContentLoader.Load(Edit(j => j[..j.IndexOf(",      // the squads of one move order", StringComparison.Ordinal)]
            .Replace("\"groupGap\": 120", "\"groupGap\": 120 } }", StringComparison.Ordinal)));
        Assert.Null(noHold.Squad!.Hold);
        Assert.NotEqual(noHold.Hash, TestContent.Mvp.Hash);
        var twice = Edit(j => j.Replace("\"order\": [\"recon\"", "\"order\": [\"medic\"", StringComparison.Ordinal));
        Assert.Contains("\"medic\" twice", Assert.Throws<ContentException>(() => ContentLoader.Load(twice)).Message);
        var tuned = ContentLoader.Load(Edit(j => j.Replace("\"radius\": 120", "\"radius\": 110", StringComparison.Ordinal)));
        Assert.NotEqual(tuned.Hash, TestContent.Mvp.Hash);
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "battle.json")).For(noHold, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = new World(new Grid(64, 64), MatchCompiler.Compile(noHold, setup));
        var men = Enumerable.Range(0, 3).Select(i => w.Spawn(1, "Rifleman", 28 + i, 40)).ToList();
        var s = Form(w, men);
        Hold(w, 1, [men[0]], Spot, Toward);
        w.Run(5);
        Assert.Equal(SquadMode.March, s.Mode);
    }

    // The same Hold gives the same cells on two runs and the hash agrees with the reference hash.
    [Fact]
    public void Deterministic()
    {
        static (ulong, List<HoldPick>) Play()
        {
            var w = Crafted();
            var (s, line, _, _, _) = CraftedSquad(w);
            Hold(w, 1, [line[1]], Spot, Toward);
            for (var i = 0; i < 300; i++)
            {
                w.Run(1);
                Assert.Equal(w.Hash(), w.HashReference());
            }
            return (w.Hash(), s.HoldPicks.ToList());
        }
        var (h1, p1) = Play();
        var (h2, p2) = Play();
        Assert.Equal(h1, h2);
        Assert.Equal(p1, p2);
        Assert.NotEmpty(p1);
    }

    // Replays store the Hold order (its spot and facing) and play it back to the same hashes.
    [Fact]
    public void ReplayRoundTrip()
    {
        var setup = TestContent.Battle(11);
        var preview = Replay.NewWorld(TestContent.Mvp, setup);
        var blue = preview.Units.Where(u => u.Owner == 1 && preview.CanJoinSquad(u)).Select(u => u.Id).ToList();
        var start = preview.Unit(blue[0])!.Pos;
        var spot = new Cell(Cell.Of(start).X + 4, Cell.Of(start).Y + 4);
        var commands = new List<Command>
        {
            new() { Tick = 5, Player = 1, Kind = CommandKind.FormSquad, Units = blue },
            new() { Tick = 10, Player = 1, Kind = CommandKind.SetSquadMode, Units = [blue[0]], Index = 2, Cell = spot, Cell2 = new Cell(spot.X + 10, spot.Y) },
            new() { Tick = 200, Player = 1, Kind = CommandKind.Move, Units = [blue[1]], Cell = new Cell(40, 40) },
            new() { Tick = 300, Player = 1, Kind = CommandKind.SetSquadMode, Units = [blue[0]], Index = 2, Cell = new Cell(42, 42) },
        };
        var live = Replay.NewWorld(TestContent.Mvp, setup);
        var issued = commands.Select(c => live.Issue(c)).ToList();
        var hashes = new List<ulong>();
        var placed = 0;
        while (live.Tick < 450)
        {
            live.Step();
            hashes.Add(live.Hash());
            placed += live.SquadEvents.Count(e => e.Kind == SquadEventKind.Placed);
        }
        Assert.True(placed >= 1, $"placed {placed} times");
        var rec = new Replay { Setup = setup, Ticks = 450, Commands = issued };
        using var buf = new MemoryStream();
        rec.Encode(buf);
        buf.Position = 0;
        var loaded = Replay.Decode(buf);
        Assert.Equal(issued.Select(c => c.Cell2), loaded.Commands.Select(c => c.Cell2));
        var (replayed, _) = loaded.Play(TestContent.Mvp);
        Assert.Equal(hashes, replayed);
    }
}
