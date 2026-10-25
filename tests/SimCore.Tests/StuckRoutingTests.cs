using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

public class StuckRoutingTests
{
    private static (World W, Unit Blocker, List<Unit> Squad) Corridor(string kind, int width)
    {
        var w = Arena.NewSentinel(64);
        var y0 = 30 - width / 2;
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            if (y < y0 || y >= y0 + width) { w.Grid.Set(new Cell(x, y), Terrain.Blocked); }
        }
        var blocker = kind == "tank" ? w.SpawnCrewed(1, "main_battle_tank", 30, 30).Vehicle :
            kind == "empty" ? w.Spawn(2, "cargo_truck", 30, 30) :
            w.SpawnCrewed(2, "heavy_rocket_launcher", 30, 30).Vehicle;
        if (kind == "deployed") { blocker.Deploy = DeployState.Deployed; }
        var squad = Enumerable.Range(0, 3).Select(i => w.Spawn(2, "Rifleman", 18 - i * 2, 30)).ToList();
        foreach (var u in w.Units) { u.Stance = Stance.HoldFire; }
        w.Order(2, CommandKind.Move, squad, cell: new Cell(52, 30));
        return (w, blocker, squad);
    }

    [Theory]
    [InlineData("tank")]
    [InlineData("empty")]
    [InlineData("deployed")]
    public void SquadPassesStationaryBodyWhenThereIsRoom(string kind)
    {
        var (w, blocker, squad) = Corridor(kind, 7);
        var at = blocker.Pos;
        w.Run(2400); // two minutes, including a stuck detection and a bounded detour
        Assert.All(squad, u => Assert.True(u.Pos.X.Raw > 45 * Fixed.OneRaw, $"{kind}: {u.Id} at {u.Pos}, path {u.PathIdx}/{u.Path.Count}"));
        Assert.Equal(at, blocker.Pos); // enemies and anchored vehicles are never pushed
    }

    [Theory]
    [InlineData("tank")]
    [InlineData("empty")]
    [InlineData("deployed")]
    public void ImpassableThreeCellCorridorDoesNotOscillate(string kind)
    {
        // A centred radius-1.2 tank leaves 0.3 cells on either side; a radius-0.3
        // soldier needs 0.6. There is physically no passage in this three-cell corridor.
        var (w, blocker, squad) = Corridor(kind, 3);
        var initial = blocker.Pos;
        w.Run(2300);
        var last = squad.Select(_ => new List<Vec>()).ToArray();
        for (var i = 0; i < 100; i++)
        {
            w.Step();
            for (var j = 0; j < squad.Count; j++) { last[j].Add(squad[j].Pos); }
        }
        Assert.Equal(initial, blocker.Pos);
        Assert.All(squad, u => Assert.True(u.Pos.X.Raw < 32 * Fixed.OneRaw));
        foreach (var points in last)
        {
            Assert.True(points.Max(p => p.X.Raw) - points.Min(p => p.X.Raw) < Fixed.OneRaw / 2);
            Assert.True(points.Max(p => p.Y.Raw) - points.Min(p => p.Y.Raw) < Fixed.OneRaw / 2);
        }
    }

    [Fact]
    public void VehicleColumnPassesParkedTruckOnRoad()
    {
        var w = Arena.New(96);
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        var terrain = TerrainMap.Flat(96, 96, rules);
        terrain.SetRoad(new Cell(0, 40), 96, 9, true);
        w.AttachTerrain(terrain);
        var parked = w.Spawn(1, "cargo_truck", 40, 44);
        var column = Enumerable.Range(0, 4).Select(i => w.SpawnCrewed(1, "technical", 30 - 4 * i, 44, Facing.East).Vehicle).ToList();
        w.Order(1, CommandKind.Move, column, cell: new Cell(80, 44));
        w.Run(2400);
        Assert.All(column, u => Assert.True(u.Pos.X.Raw > 65 * Fixed.OneRaw, $"truck column: {u.Id} at {u.Pos}"));
        Assert.Equal(new Cell(40, 44).Center(), parked.Pos);
    }

    [Fact]
    public void DetoursAreDeterministicAndKeepFormationMembership()
    {
        var a = Corridor("deployed", 7);
        var b = Corridor("deployed", 7);
        a.W.Run(1); b.W.Run(1);
        var group = a.Squad[0].Group;
        Assert.NotEqual(0u, group);
        // Exercise the stuck-recovery branch directly as well as movement's natural triggers.
        foreach (var pair in a.Squad.Zip(b.Squad))
        {
            pair.First.Intent = pair.First.Intent with { Stuck = true };
            pair.Second.Intent = pair.Second.Intent with { Stuck = true };
        }
        var attempted = false;
        for (var t = 0; t < 1200; t++)
        {
            a.W.Step(); b.W.Step();
            attempted |= a.Squad.Any(u => u.DetourRetryAt > 0);
            Assert.Equal(a.W.Hash(), b.W.Hash());
            foreach (var u in a.Squad.Where(u => u.Order.Kind == OrderKind.Move)) { Assert.Equal(group, u.Group); }
        }
        Assert.True(attempted);
    }

    [Fact]
    public void DetourRulesAreValidatedAndHashed()
    {
        const string file = """{"unitAi":{"detourBudget":5,"detourRadiusCells":10,"detourCost":90,"detourRetryTicks":60}}""";
        var changed = ContentLoader.Load(TestContent.MvpSources().Select(s => s.Name == "unit_ai.json" ? new ContentSource(s.Name, file) : s));
        Assert.Equal(5, changed.UnitAi.DetourBudget);
        Assert.Equal(10, changed.UnitAi.DetourRadiusCells);
        Assert.NotEqual(TestContent.Mvp.Hash, changed.Hash);
        Assert.Throws<ContentException>(() => ContentLoader.Load(TestContent.MvpSources().Select(s => s.Name == "unit_ai.json" ? new ContentSource(s.Name, """{"unitAi":{"detourBudget":-1}}""") : s)));
    }
}
