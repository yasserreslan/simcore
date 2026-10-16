using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// The heavy rocket truck (heavy_rocket_truck): 12 big rockets, one every 2 s, from 50 to 90
// cells, deploy 4 s / stow 3 s, depot reload one rocket every 5 s; heavy_rocket warhead.
public class HeavyRocketTruckTests
{
    private const int Deploy = 80, Stow = 60, Interval = 40;

    private static (World W, Unit Truck, Unit Target) Range(int targetX, bool depot = false)
    {
        var w = Arena.New(128);
        if (depot)
        {
            w.Spawn(1, "supply_depot", 3, 30);
        }
        var (t, _) = w.SpawnCrewed(1, "heavy_rocket_truck", 5, 31, Facing.East);
        var target = w.Spawn(2, "technical", targetX, 31);
        target.HP = 1_000_000;
        w.Spawn(1, "worker", targetX, 27); // the spotter
        return (w, t, target);
    }

    private static List<(uint Id, uint Tick)> Launches(World w, int ticks)
    {
        var seen = new Dictionary<uint, uint>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                seen.TryAdd(p.Id, w.Tick);
            }
        }
        return [.. seen.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key)];
    }

    [Fact]
    public void TwelveRocketsTwoSecondsApartAfterAFourSecondDeploy()
    {
        var (w, t, target) = Range(75);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        var shots = Launches(w, Deploy + 12 * Interval + 200);
        Assert.Equal(12, shots.Count);
        Assert.True(shots[0].Tick > Deploy, $"first rocket on tick {shots[0].Tick}, before deploying");
        Assert.All(shots.Zip(shots.Skip(1)), p => Assert.Equal(Interval, (int)(p.Second.Tick - p.First.Tick)));
        Assert.Equal(0, t.Charges[0]);
    }

    [Fact]
    public void NothingCloserThanFiftyCells()
    {
        var (w, t, target) = Range(45);
        w.Order(1, CommandKind.Deploy, [t]);
        w.Run(Deploy + 2);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        Assert.Empty(Launches(w, 300));
        Assert.Equal(12, t.Charges[0]);
    }

    [Fact]
    public void NothingBeyondNinetyCells()
    {
        var (w, t, target) = Range(5 + 92);
        w.Order(1, CommandKind.Deploy, [t]);
        w.Run(Deploy + 2);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        // Deployed, it can't drive closer without stowing first; it never fires from here.
        Assert.Empty(Launches(w, 60));
    }

    [Fact]
    public void ReloadsAtADepotOneRocketEveryFiveSeconds()
    {
        var (w, t, target) = Range(70, depot: true);
        w.Order(1, CommandKind.Attack, [t], target.Id);
        w.Run(Deploy + 12 * Interval + 10);
        Assert.Equal(0, t.Charges[0]);
        w.Order(1, CommandKind.Stop, [t]);
        w.Run(20 + 100 * 3 + 5);
        Assert.InRange(t.Charges[0], 3, 4);
        w.Run(100 * 12);
        Assert.Equal(12, t.Charges[0]);
    }

    [Fact]
    public void HitsDemolishBlocksAndTechnicals()
    {
        var w = Arena.New(64);
        var h = w.Content.Warhead("heavy_rocket")!;
        var panel = w.Spawn(2, "block_panel_5", 2, 2);
        var brick = w.Spawn(2, "block_brick_4", 30, 2);
        var tech = w.Spawn(2, "technical", 10, 30);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(panel.Alive, $"the panel block fell after {i} hits");
            w.Hurt(panel, h, 400, 100);
        }
        Assert.False(panel.Alive);
        w.Hurt(brick, h, 400, 100);
        Assert.True(brick.Alive);
        w.Hurt(brick, h, 400, 100);
        Assert.False(brick.Alive);
        w.Hurt(tech, h, 400, 100);
        Assert.False(tech.Alive);
    }

    [Fact]
    public void ThreeRocketsBringDownAPanelBlockSeventyCellsAway()
    {
        var w = Arena.New(128);
        var (t, _) = w.SpawnCrewed(1, "heavy_rocket_truck", 5, 40, Facing.East);
        var block = w.Spawn(2, "block_panel_5", 70, 37);
        w.Spawn(1, "worker", 80, 46); // the spotter
        w.Order(1, CommandKind.Attack, [t], block.Id);
        var hits = 0;
        for (var i = 0; i < Deploy + 12 * Interval + 200 && w.Unit(block.Id) is not null; i++)
        {
            w.Step();
            hits += w.Detonations.Count(d => d.Hit == block.Id);
        }
        Assert.Null(w.Unit(block.Id));
        Assert.InRange(hits, 1, 3); // near misses splash it too
        Assert.True(t.Charges[0] >= 12 - 6, $"took {12 - t.Charges[0]} rockets");
    }

    [Fact]
    public void DeployAndStowTakeFourAndThreeSeconds()
    {
        var w = Arena.New(32);
        var (t, _) = w.SpawnCrewed(1, "heavy_rocket_truck", 5, 16);
        w.Order(1, CommandKind.Deploy, [t]);
        w.Run(Deploy - 5);
        Assert.Equal(DeployState.Deploying, t.Deploy);
        w.Run(10);
        Assert.Equal(DeployState.Deployed, t.Deploy);
        w.Order(1, CommandKind.Stow, [t]);
        w.Run(Stow + 2);
        Assert.Equal(DeployState.Stowed, t.Deploy);
    }
}
