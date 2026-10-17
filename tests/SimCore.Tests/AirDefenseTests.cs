using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Rng;
using SimCore.Sim;

namespace SimCore.Tests;

// Air defence (the game repo's docs/air-defense-design.md): unit stances, the AA soldier's
// tracking and lock, the 3D lead pursuit of a one-way attack drone, the deterministic kill roll,
// ammo, reload and the depot refill.
public class AirDefenseTests
{
    private const int N = 600;
    private const int X = 300;

    private static void Ground(World w, Unit u, Cell at, bool queue = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.AttackGround, Units = [u.Id], Cell = at, Queue = queue });

    private static void Stance(World w, Unit u, Stance s) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = s });

    // A drone truck of player 2 at (X, truckY) fires one drone due north at (X, aimY); an AA
    // soldier of player 1 stands at (X, soldierY), under its path.
    private static (World W, Unit Soldier, Unit Truck) Range(int soldierY, int truckY = 590, int aimY = 20, ulong seed = 1)
    {
        var w = Arena.New(N, seed);
        var soldier = w.Spawn(1, "manpads", X, soldierY);
        var truck = w.SpawnCrewed(2, "drone_truck", X, truckY, Facing.North).Vehicle;
        Ground(w, truck, new Cell(X, aimY));
        return (w, soldier, truck);
    }

    private static List<AirBurst> Bursts(World w, int ticks, Action<World>? each = null)
    {
        var list = new List<AirBurst>();
        for (var i = 0; i < ticks; i++)
        {
            each?.Invoke(w);
            w.Step();
            list.AddRange(w.AirBursts);
        }
        return list;
    }

    private static Projectile? Drone(World w) => w.Projectiles.FirstOrDefault(p => p.Phase != DronePhase.None);

    [Fact]
    public void HeadOnDroneIsTrackedLockedAndEngagedWithAKillRoll()
    {
        var (w, soldier, _) = Range(300);
        uint firstTrack = 0, firstShot = 0;
        var bursts = Bursts(w, 1200, w =>
        {
            if (firstTrack == 0 && soldier.AirTarget != 0)
            {
                firstTrack = w.Tick;
            }
            if (firstShot == 0 && w.Projectiles.Any(p => p.Weapon.Effect.Interceptor is not null))
            {
                firstShot = w.Tick;
            }
        });
        Assert.NotEqual(0u, firstTrack);
        Assert.True(firstShot >= firstTrack + 30, $"locked 30 ticks before firing: track {firstTrack}, shot {firstShot}");
        Assert.NotEmpty(bursts);
        var fuse = bursts[0];
        Assert.True(fuse.Kind is AirBurstKind.Kill or AirBurstKind.Miss, $"head-on: the missile reaches the drone ({fuse.Kind})");
        Assert.InRange(fuse.KillPermille, 50, 450); // head-on: the worst aspect
        Assert.InRange(fuse.Roll, 0, 999);
    }

    [Fact]
    public void TailChaseAfterThePassOverReachesTheDroneWithABetterChance()
    {
        var (w, soldier, _) = Range(300);
        Stance(w, soldier, Sim.Stance.HoldFire);
        var guard = false;
        var bursts = Bursts(w, 1400, w =>
        {
            if (!guard && Drone(w) is { } d && d.Pos.Y.Raw < (X - 25) * Fixed.OneRaw)
            {
                Stance(w, soldier, Sim.Stance.Guard); // it passed over 25 cells ago: fire after it
                guard = true;
            }
        });
        Assert.True(guard);
        Assert.NotEmpty(bursts);
        Assert.True(bursts[0].Kind is AirBurstKind.Kill or AirBurstKind.Miss, $"tail chase: reached ({bursts[0].Kind})");
        Assert.InRange(bursts[0].KillPermille, 400, 950); // tail-on: the best aspect
    }

    // Lead pursuit: on a map whose highest ground is 80 m (far off the line), the drone climbs for
    // seconds to 110 m. Missiles fired across its path, or up at it while it climbs out of the launch,
    // used to fall behind it in the endgame with pure pursuit (45 degrees a second) and self-destruct;
    // steering at where it will be, they reach it, and the roll decides.
    [Theory]
    [InlineData(240, 300)] // 60 cells off its path
    [InlineData(200, 300)] // 100 cells off its path: a long shot
    [InlineData(300, 480)] // under the climb, 110 cells from the launcher
    public void LongAndClimbingShotsReachTheDroneWithLeadPursuit(int soldierX, int soldierY)
    {
        var w = Arena.New(N, 1);
        w.AttachTerrain(Hill(N, 80));
        w.Spawn(1, "manpads", soldierX, soldierY);
        var truck = w.SpawnCrewed(2, "drone_truck", X, 590, Facing.North).Vehicle;
        Ground(w, truck, new Cell(X, 20));
        var bursts = Bursts(w, 1200);
        Assert.NotEmpty(bursts);
        Assert.True(bursts[0].Kind is AirBurstKind.Kill or AirBurstKind.Miss, $"reached: {bursts[0].Kind}");
        Assert.InRange(bursts[0].KillPermille, 50, 950);
        Assert.InRange(bursts[0].Roll, 0, 999);
        Assert.Equal(bursts[0].Roll < bursts[0].KillPermille, bursts[0].Kind == AirBurstKind.Kill); // the roll decides
    }

    // Hill is a flat map with one corner raised to `metres` far from the line of flight: the
    // drones' cruise height is 30 m above it.
    private static TerrainMap Hill(int n, int metres)
    {
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = x > n - 10 && y < 5 ? metres * 512 : 0;
            }
        }
        return new TerrainMap(n, n, corners, new byte[n * n], rules);
    }

    [Fact]
    public void ADroneFlyingAwayTooFarOutrunsTheMissileWhichSelfDestructs()
    {
        var (w, soldier, _) = Range(450);
        Stance(w, soldier, Sim.Stance.HoldFire);
        var guard = false;
        var bursts = Bursts(w, 1600, w =>
        {
            if (!guard && Drone(w) is { } d && d.Pos.Y.Raw < (450 - 112) * Fixed.OneRaw)
            {
                Stance(w, soldier, Sim.Stance.Guard); // switched on too late: 112 cells behind and flying away
                guard = true;
            }
        });
        Assert.NotEmpty(bursts);
        Assert.Equal(AirBurstKind.SelfDestruct, bursts[0].Kind);
        Assert.Equal(-1, bursts[0].Roll);
    }

    // An AA soldier inside a building, under the drone's path: the brick block's top-left cell
    // at (X - 7, soldierY - 3), so its centre is about under the path.
    private static (World W, Unit Soldier, Unit Block) InBuilding(int soldierY)
    {
        var (w, soldier, _) = Range(soldierY);
        var block = w.Spawn(1, "block_brick_4", X - 7, soldierY - 3);
        Assert.True(w.CanGarrison(soldier, block), "an AA soldier may go into a building");
        World.EnterBuilding(soldier, block);
        return (w, soldier, block);
    }

    [Theory]
    [InlineData(Sim.Stance.Guard)]
    [InlineData(Sim.Stance.ReturnFire)]
    public void InABuildingHeGuardsTheSkyOnHisOwnFromAWindow(Stance stance)
    {
        var (w, soldier, block) = InBuilding(300);
        Stance(w, soldier, stance);
        var bursts = Bursts(w, 1200);
        Assert.Equal(block.Id, soldier.Garrison);
        Assert.NotEmpty(bursts);
        var fuse = bursts[0];
        Assert.True(fuse.Kind is AirBurstKind.Kill or AirBurstKind.Miss, $"from a window: the missile reaches the drone ({fuse.Kind})");
        Assert.InRange(fuse.KillPermille, 50, 450); // head-on, the same chances as on foot
        Assert.True(soldier.Charges[0] < 5, "he spent a missile");
    }

    [Fact]
    public void InABuildingHoldFireStillHolds()
    {
        var (w, soldier, _) = InBuilding(300);
        Stance(w, soldier, Sim.Stance.HoldFire);
        var bursts = Bursts(w, 900, w => Assert.Equal(0u, soldier.AirTarget));
        Assert.Empty(bursts);
        Assert.Equal(5, soldier.Charges[0]);
    }

    [Fact]
    public void HoldFireNeverTracksOrFires()
    {
        var (w, soldier, _) = Range(300);
        Stance(w, soldier, Sim.Stance.HoldFire);
        var rifleman = w.Spawn(1, "Rifleman", 100, 100);
        Stance(w, rifleman, Sim.Stance.HoldFire);
        var enemy = w.Spawn(2, "Rifleman", 100, 103);
        Stance(w, enemy, Sim.Stance.HoldFire);
        var bursts = Bursts(w, 900, w => Assert.Equal(0u, soldier.AirTarget));
        Assert.Empty(bursts);
        Assert.Equal(5, soldier.Charges[0]);
        Assert.Equal(0u, rifleman.AutoTarget);
        Assert.Equal(enemy.Def.MaxHp, enemy.HP); // both hold their fire
    }

    [Fact]
    public void ReturnFireShootsOnlyTheEnemyThatHurtIt()
    {
        var w = Arena.New(40);
        var ours = w.Spawn(1, "Rifleman", 10, 10);
        Stance(w, ours, Sim.Stance.ReturnFire);
        var shooter = w.Spawn(2, "Rifleman", 10, 14);
        var bystander = w.Spawn(2, "Rifleman", 12, 12);
        Stance(w, bystander, Sim.Stance.HoldFire);
        w.Run(60);
        Assert.Equal(shooter.Id, ours.HitBy);
        Assert.True(shooter.HP < shooter.Def.MaxHp || !shooter.Alive);
        Assert.Equal(bystander.Def.MaxHp, bystander.HP);
    }

    [Fact]
    public void KillRollIsAPureHashOfSeedTickShooterAndTarget()
    {
        Assert.Equal(Rand.Hash(42, 100, 7, 9), Rand.Hash(42, 100, 7, 9));
        Assert.NotEqual(Rand.Hash(42, 100, 7, 9), Rand.Hash(43, 100, 7, 9));
        Assert.NotEqual(Rand.Hash(42, 100, 7, 9), Rand.Hash(42, 100, 9, 7));
    }

    [Fact]
    public void SameSeedSameOutcomeAndTheRollChangesWithTheSeed()
    {
        static (AirBurst First, ulong Hash) Play(ulong seed)
        {
            var (w, _, _) = Range(300, seed: seed);
            var bursts = Bursts(w, 1200);
            return (bursts[0], w.Hash());
        }
        var a = Play(5);
        var b = Play(5);
        Assert.Equal(a, b);
        var rolls = Enumerable.Range(1, 6).Select(s => Play((ulong)s).First.Roll).ToList();
        Assert.True(rolls.Distinct().Count() > 1, string.Join(",", rolls));
    }

    [Fact]
    public void FiveMissilesFourSecondsApartAndADepotRefills()
    {
        var w = Arena.New(N);
        var soldier = w.Spawn(1, "manpads", X, 300);
        Assert.Equal(5, soldier.Charges[0]); // 1 loaded + 4 spare
        var shots = new List<uint>();
        // Four drone trucks (8 drones), so there is something left to shoot after misses or kills.
        foreach (var x in new[] { X - 12, X - 4, X + 4, X + 12 })
        {
            var t = w.SpawnCrewed(2, "drone_truck", x, 590, Facing.North).Vehicle;
            Ground(w, t, new Cell(x, 20));
            Ground(w, t, new Cell(x, 40), queue: true);
        }
        var left = soldier.Charges[0];
        for (var i = 0; i < 1800; i++)
        {
            w.Step();
            if (soldier.Charges[0] < left)
            {
                shots.Add(w.Tick);
                left = soldier.Charges[0];
            }
        }
        Assert.True(shots.Count >= 3, $"{shots.Count} shots");
        for (var i = 1; i < shots.Count; i++)
        {
            Assert.True(shots[i] - shots[i - 1] >= 80, $"4 s to load the next: {string.Join(", ", shots)}");
        }
        Assert.Equal(5 - shots.Count, soldier.Charges[0]);
        // A friendly depot refills him: a missile per 5 s, 60 supply each.
        var depot = w.Spawn(1, "supply_depot", X - 8, 297);
        var stock = depot.Stock;
        w.Run(100 * shots.Count + 80);
        Assert.Equal(5, soldier.Charges[0]);
        Assert.Equal(stock - 60 * shots.Count, depot.Stock);
    }

    [Fact]
    public void ThreeSoldiersSpreadOverASwarmAndTheEngagementIsDeterministic()
    {
        static (List<AirBurst> Bursts, ulong Hash) Swarm()
        {
            var w = Arena.New(N, 9);
            for (var i = 0; i < 3; i++)
            {
                w.Spawn(1, "manpads", X - 10 + 10 * i, 300);
            }
            for (var i = 0; i < 6; i++)
            {
                var t = w.SpawnCrewed(2, "drone_truck", X - 25 + 10 * i, 590, Facing.North).Vehicle;
                Ground(w, t, new Cell(X - 25 + 10 * i, 290));
            }
            var bursts = Bursts(w, 1400);
            return (bursts, w.Hash());
        }
        var a = Swarm();
        var b = Swarm();
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Bursts, b.Bursts);
        Assert.True(a.Bursts.Select(x => x.Target).Distinct().Count() >= 3, "three soldiers engage at least three drones");
    }
}
