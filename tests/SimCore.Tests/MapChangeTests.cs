using SimCore.Sim;

namespace SimCore.Tests;

// Map changes in play (a building, rubble, a knocked-down forest) update the caches derived from
// the map only where they reach: the movement system's wall distances, and group flows whose
// corridor the change doesn't touch keep their plan.
public class MapChangeTests
{
    [Fact]
    public void WallDistancesUpdateIncrementallyAsAFullRebuildWould()
    {
        var w = Arena.New(96);
        var fresh = Arena.New(96);
        var before = w.WallDistancesFor(-1).ToArray();
        var rng = new Random(5);
        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < 12; i++)
            {
                var c = new Cell(rng.Next(96), rng.Next(96));
                var t = rng.Next(3) == 0 ? Terrain.Clear : Terrain.Blocked;
                w.Grid.Set(c, t);
                fresh.Grid.Set(c, t);
            }
            var inc = w.WallDistancesFor(-1);
            var full = Arena.New(96);
            for (var y = 0; y < 96; y++)
            {
                for (var x = 0; x < 96; x++)
                {
                    full.Grid.Set(new Cell(x, y), fresh.Grid.At(new Cell(x, y)));
                }
            }
            Assert.Equal(full.WallDistancesFor(-1), inc);
        }
        Assert.NotEqual(before, w.WallDistancesFor(-1));
    }

    [Fact]
    public void AGroupReplansOnlyWhenAChangeReachesItsCorridor()
    {
        var w = Arena.New(192);
        var army = Enumerable.Range(0, 8).Select(i => w.Spawn(1, "Rifleman", 10 + i % 4, 20 + i / 4)).ToList();
        w.Order(1, CommandKind.Move, army, cell: new Cell(170, 24));
        w.Run(3);
        var group = w.GroupsForTests[0];
        var flow = group.Flows[0];
        Assert.False(w.Routing.IsStale(flow));
        // Far from the corridor (the south edge): still valid.
        w.Grid.Set(new Cell(100, 180), Terrain.Blocked);
        w.Run(2);
        Assert.False(w.Routing.IsStale(flow));
        Assert.False(group.Queued);
        // On the corridor: it re-plans (and the members keep going meanwhile).
        w.Grid.Set(new Cell(100, 22), Terrain.Blocked);
        w.Step();
        Assert.True(group.Queued || !w.Routing.IsStale(flow));
        w.Run(400);
        Assert.All(army, u => Assert.True(u.Pos.X.Raw > 35 * 1024, $"unit {u.Id} stalled at {u.Pos}"));
    }
}
