using SimCore.FixedPoint;
using SimCore.Rng;
using SimCore.Sim;

namespace SimCore.Tests;

// Air defence (the game repo's docs/air-defense-design.md): unit stances, the AA soldier's
// tracking and lock, the 3D pursuit of a one-way attack drone, the deterministic kill roll, ammo,
// reload and the depot refill.
public class AirDefenseTests
{
    private const int N = 600;
    private const int X = 300;

    private static void Ground(World w, Unit u, Cell at) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.AttackGround, Units = [u.Id], Cell = at });

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
        Assert.Equal(2, soldier.Charges[0]);
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
    public void TwoMissilesFourSecondsApartThenEmptyAndADepotRefills()
    {
        var w = Arena.New(N);
        var soldier = w.Spawn(1, "manpads", X, 300);
        var shots = new List<uint>();
        // Two drone trucks, so there is something left to shoot after the first misses or kills.
        foreach (var x in new[] { X - 4, X + 4 })
        {
            var t = w.SpawnCrewed(2, "drone_truck", x, 590, Facing.North).Vehicle;
            Ground(w, t, new Cell(x, 20));
        }
        var left = soldier.Charges[0];
        for (var i = 0; i < 1500; i++)
        {
            w.Step();
            if (soldier.Charges[0] < left)
            {
                shots.Add(w.Tick);
                left = soldier.Charges[0];
            }
        }
        Assert.Equal(2, shots.Count);
        Assert.True(shots[1] - shots[0] >= 80, $"4 s to load the spare: {shots[0]} then {shots[1]}");
        Assert.Equal(0, soldier.Charges[0]);
        // A friendly depot refills him: a missile per 5 s, 60 supply each.
        var depot = w.Spawn(1, "supply_depot", X - 8, 297);
        var stock = depot.Stock;
        w.Run(260);
        Assert.Equal(2, soldier.Charges[0]);
        Assert.Equal(stock - 120, depot.Stock);
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
