using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The tactical missile's reach and dive (WPN-1, owner 2026-11-22: "the ballistic missiles falls down in a steep way, when it reaches
// its destination, it just goes down horizontally ... we need to give it 10% more range than the twin missile"): 275 cells (the
// twin missile's 250 x 1.1), its speed kept; the last tenth of its path comes down at 60 degrees or steeper on the engine's own
// arc (World.ArcHeightRaw, which air defence reads too); and what the Patriot and THAAD types make of it near and far.
public class BallisticReachTests(ITestOutputHelper output)
{
    private const int N = 500, X = 200, Y = 200;

    private static WeaponDef Weapon(World w, string unit) => w.Match.Players[0].Find(w.Content, unit)!.Weapons[0];

    [Fact]
    public void ItReachesTenPercentFartherThanTheTwinMissileAtTheSameSpeed()
    {
        var w = Arena.New(64, content: TestContent.ClassicSupply);
        var ballistic = Weapon(w, "ballistic_truck");
        var twin = Weapon(w, "missile_truck");
        Assert.Equal(twin.Range.Raw * 11 / 10, ballistic.Range.Raw); // 250 x 1.1 = 275 cells
        Assert.Equal(Fixed.FromInt(60), ballistic.Effect.MinRange); // the minimum range stays 60 cells
        Assert.InRange(ballistic.Effect.ProjectileSpeed.Raw * 10, twin.Effect.ProjectileSpeed.Raw * 18 - 18, twin.Effect.ProjectileSpeed.Raw * 18 + 18); // its speed kept
    }

    // Flight: the truck fires at a technical `cells` east of it on open ground; every tick of the missile's flight, its ground
    // distance from launch and its height on the engine's arc (raw).
    private static List<(long S, long H)> Flight(int cells, out long total)
    {
        var w = Arena.New(N, content: TestContent.ClassicSupply);
        var (t, _) = w.SpawnCrewed(1, "ballistic_truck", 5, 31, Facing.East);
        var target = w.Spawn(2, "technical", 5 + cells, 31);
        target.HP = 1_000_000;
        w.Spawn(1, "worker", 5 + cells, 27); // the spotter
        w.Order(1, CommandKind.Attack, [t], target.Id);
        var path = new List<(long, long)>();
        total = 0;
        for (var i = 0; i < 1200; i++)
        {
            w.Step();
            if (w.Projectiles.FirstOrDefault(p => p.Shooter == t.Id) is not { } p)
            {
                if (path.Count > 0)
                {
                    break;
                }
                continue;
            }
            total = (p.Dest - p.Origin).Len().Raw;
            path.Add(((p.Pos - p.Origin).Len().Raw, World.ArcHeightRaw(p, p.Pos)));
        }
        return path;
    }

    [Theory]
    [InlineData(70)]
    [InlineData(160)]
    [InlineData(272)]
    public void TheLastTenthOfItsPathComesDownAtSixtyDegreesOrSteeper(int cells)
    {
        var path = Flight(cells, out var total);
        Assert.True(path.Count > 10, $"in flight {path.Count} ticks");
        var (s0, h0) = path.First(q => q.S * 10 >= total * 9); // the first tick into the last tenth
        var (s1, h1) = path[^1];
        var drop = h0 - h1;
        var run = s1 - s0;
        var degrees = Math.Atan2(drop, run) * 180 / Math.PI;
        output.WriteLine($"{cells} cells: the last tenth ({run / Fixed.OneRaw} cells of ground) falls {drop / Fixed.OneRaw} cells: {degrees:F1} degrees");
        Assert.True(drop * 1000 >= run * 1732, $"{degrees:F1} degrees over the last tenth: not 60 or steeper");
        // Steeper all the way down: each tick's fall over the last tenth is at least the one before it.
        var tail = path.Where(q => q.S >= s0).ToList();
        for (var i = 2; i < tail.Count; i++)
        {
            Assert.True((tail[i - 1].H - tail[i].H) * (tail[i - 1].S - tail[i - 2].S) >= (tail[i - 2].H - tail[i - 1].H) * (tail[i].S - tail[i - 1].S) - Fixed.OneRaw,
                $"tick {i} of the last tenth flattens out");
        }
    }

    // Battery: player 1's launcher of `kind` at (X, Y) and its radar truck 10 cells east, crewed and set up.
    private static (World W, Unit Launcher) Battery(ulong seed, string kind)
    {
        var w = Arena.NewSentinel(N, seed);
        var launcher = w.SpawnCrewed(1, kind, X, Y).Vehicle;
        w.SpawnCrewed(1, "radar_truck", X + 10, Y);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Deploy, Units = [launcher.Id] });
        w.Run(kind == "thaad_launcher" ? 520 : 420);
        return (w, launcher);
    }

    // Engage: player 2's tactical missile truck `distance` cells due south fires at the ground 2 cells from the battery; whether
    // an interceptor killed the missile, how many were fired at it and their best kill chance (permille).
    private static (bool Killed, int Fired, int BestPermille) Engage(ulong seed, string kind, int distance)
    {
        var (w, launcher) = Battery(seed, kind);
        var truck = w.SpawnCrewed(2, "ballistic_truck", X, Y + distance, Facing.North).Vehicle;
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Units = [truck.Id], Cell = new Cell(X, Y + 2) });
        var fired = new HashSet<uint>();
        var killed = false;
        var best = 0;
        for (var i = 0; i < 1000; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (p.Shooter == launcher.Id)
                {
                    fired.Add(p.Id);
                }
            }
            foreach (var b in w.AirBursts)
            {
                if (b.Shooter == launcher.Id && b.Kind is AirBurstKind.Kill or AirBurstKind.Miss)
                {
                    killed |= b.Kind == AirBurstKind.Kill;
                    best = Math.Max(best, b.KillPermille);
                }
            }
        }
        return (killed, fired.Count, best);
    }

    [Theory]
    [InlineData("patriot_launcher")]
    [InlineData("thaad_launcher")]
    public void AirDefenceCatchRatesNearAndFar(string kind)
    {
        const int Seeds = 20;
        var reach = (int)(Weapon(Arena.New(64, content: TestContent.ClassicSupply), "ballistic_truck").Range.Raw / Fixed.OneRaw);
        foreach (var distance in new[] { 130, 158, 220, 272 }.Where(d => d < reach))
        {
            int kills = 0, fired = 0, best = 0;
            for (var seed = 1UL; seed <= Seeds; seed++)
            {
                var (k, f, b) = Engage(seed, kind, distance);
                kills += k ? 1 : 0;
                fired += f;
                best = Math.Max(best, b);
            }
            output.WriteLine($"{kind} at {distance} cells: {kills}/{Seeds} stopped, {fired} interceptors, best chance {best / 10.0}%");
            Assert.True(fired > 0, $"{kind} never fires at a missile from {distance} cells");
        }
    }
}
