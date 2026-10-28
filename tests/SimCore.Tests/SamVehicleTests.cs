using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// The air defence vehicle (sam_vehicle, the game repo's docs/air-defense-design.md): the AA
// soldier's engagement on a 6x6 with a radar turret, against the same drones. It must see and
// reach further, kill more often, carry 10 missiles and reload them at a depot.
public class SamVehicleTests
{
    private const int N = 700;
    private const int X = 350;

    private static void Ground(World w, Unit u, Cell at, bool queue = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.AttackGround, Units = [u.Id], Cell = at, Queue = queue });

    private static void Stance(World w, Unit u, Stance s) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = s });

    // A drone truck of player 2 at (X, 640) fires one drone due north at (X, 60); player 1's air
    // defence unit (the vehicle with a driver, or the soldier) stands under its path at (X, y).
    private static (World W, Unit Shooter) Range(bool vehicle, int y = 350, ulong seed = 1, int facing = Facing.North)
    {
        var w = Arena.New(N, seed);
        var shooter = vehicle ? w.SpawnCrewed(1, "sam_vehicle", X, y, facing).Vehicle : w.Spawn(1, "manpads", X, y);
        var truck = w.SpawnCrewed(2, "drone_truck", X, 640, Facing.North).Vehicle;
        Ground(w, truck, new Cell(X, 60));
        return (w, shooter);
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

    // FirstShot plays the head-on engagement and returns the first missile's launch range (cells,
    // slant) and the first burst.
    private static (int LaunchCells, AirBurst Burst) FirstShot(bool vehicle, ulong seed = 1)
    {
        var (w, _) = Range(vehicle, seed: seed);
        var launch = -1;
        var bursts = Bursts(w, 1500, w =>
        {
            if (launch < 0 && w.Projectiles.FirstOrDefault(p => p.Weapon.Effect.Interceptor is not null) is { } m)
            {
                launch = (int)(m.LaunchRange.Raw / Fixed.OneRaw);
            }
        });
        Assert.NotEmpty(bursts);
        return (launch, bursts[0]);
    }

    [Fact]
    public void ItOutRangesTheSoldierAndCatchesTheDroneFurtherOut()
    {
        var (vehicleLaunch, vehicleBurst) = FirstShot(vehicle: true);
        var (soldierLaunch, soldierBurst) = FirstShot(vehicle: false);
        Assert.InRange(soldierLaunch, 100, 140); // the soldier: 140 cells at most
        Assert.InRange(vehicleLaunch, 180, 220); // the vehicle: out to 220 cells
        Assert.True(vehicleBurst.Kind is AirBurstKind.Kill or AirBurstKind.Miss, $"reached: {vehicleBurst.Kind}");
        // The faster missile meets the drone further from home.
        static long Away(AirBurst b) => Math.Abs(b.At.Y.Raw - (long)X * Fixed.OneRaw) / Fixed.OneRaw;
        Assert.True(Away(vehicleBurst) > Away(soldierBurst) + 40, $"vehicle {Away(vehicleBurst)} cells, soldier {Away(soldierBurst)}");
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    public void HeadOnItsKillChanceBeatsTheSoldiersEvenFromFurtherOut(ulong seed)
    {
        var vehicle = FirstShot(vehicle: true, seed).Burst;
        var soldier = FirstShot(vehicle: false, seed).Burst;
        Assert.InRange(soldier.KillPermille, 50, 450); // head-on, IR: the soldier's worst aspect
        Assert.InRange(vehicle.KillPermille, 550, 950); // head-on, radar, from about 200 cells
        Assert.True(vehicle.KillPermille >= soldier.KillPermille + 200, $"vehicle {vehicle.KillPermille}, soldier {soldier.KillPermille}");
        Assert.Equal(vehicle.Roll < vehicle.KillPermille, vehicle.Kind == AirBurstKind.Kill); // the roll decides
    }

    [Fact]
    public void TailChaseItsKillChanceBeatsTheSoldiers()
    {
        static AirBurst Chase(bool vehicle)
        {
            var (w, shooter) = Range(vehicle);
            Stance(w, shooter, Sim.Stance.HoldFire);
            var guard = false;
            var bursts = Bursts(w, 1600, w =>
            {
                if (!guard && Drone(w) is { } d && d.Pos.Y.Raw < (X - 25) * Fixed.OneRaw)
                {
                    Stance(w, shooter, Sim.Stance.Guard); // it passed over 25 cells ago: fire after it
                    guard = true;
                }
            });
            Assert.NotEmpty(bursts);
            Assert.True(bursts[0].Kind is AirBurstKind.Kill or AirBurstKind.Miss, $"tail chase reached: {bursts[0].Kind}");
            return bursts[0];
        }
        var v = Chase(vehicle: true);
        var s = Chase(vehicle: false);
        Assert.InRange(v.KillPermille, 700, 950);
        Assert.True(v.KillPermille > s.KillPermille + 100, $"vehicle {v.KillPermille}, soldier {s.KillPermille}");
    }

    [Fact]
    public void ADroneFlyingAwayIsReachedWhereTheSoldiersMissileGivesUp()
    {
        // Switched on 112 cells behind a drone flying away: the soldier's missile self-destructs
        // (AirDefenseTests); the vehicle's, at 45 cells/s for 300 cells, catches it.
        var (w, shooter) = Range(vehicle: true, y: 550);
        Stance(w, shooter, Sim.Stance.HoldFire);
        var guard = false;
        var bursts = Bursts(w, 1600, w =>
        {
            if (!guard && Drone(w) is { } d && d.Pos.Y.Raw < (550 - 112) * Fixed.OneRaw)
            {
                Stance(w, shooter, Sim.Stance.Guard);
                guard = true;
            }
        });
        Assert.NotEmpty(bursts);
        Assert.True(bursts[0].Kind is AirBurstKind.Kill or AirBurstKind.Miss, $"reached: {bursts[0].Kind}");
    }

    [Fact]
    public void TheHullTurnsToTheTargetAndLaunchesFromItsRails()
    {
        // The launcher is fixed on the hull (docs/design.md, "Launchers aim with the hull"): the hull turns to the drone coming
        // from the south, at the old turret's two facings a tick.
        var (w, osa) = Range(vehicle: true, facing: Facing.East);
        long launchHeight = -1;
        var bursts = Bursts(w, 1200, w =>
        {
            if (launchHeight < 0 && w.Projectiles.FirstOrDefault(p => p.Weapon.Effect.Interceptor is not null) is { } m)
            {
                launchHeight = m.LaunchHeight.Raw;
            }
        });
        Assert.NotEmpty(bursts);
        Assert.Equal(Facing.South, osa.Facing); // the hull turned to the drone
        Assert.Equal(osa.Facing, osa.TurretFacing); // the box along it
        Assert.Equal(33 * 512 / 10, launchHeight); // 3.3 m up, flat map
    }

    [Fact]
    public void WithoutADriverItDoesNothing()
    {
        var w = Arena.New(N, 1);
        var empty = w.Spawn(1, "sam_vehicle", X, 350);
        var truck = w.SpawnCrewed(2, "drone_truck", X, 640, Facing.North).Vehicle;
        Ground(w, truck, new Cell(X, 60));
        var bursts = Bursts(w, 1200, w => Assert.Equal(0u, empty.AirTarget));
        Assert.Empty(bursts);
        Assert.Equal(10, empty.Charges[0]);
    }

    [Fact]
    public void TenMissilesThenSilenceAndADepotRefillsAll()
    {
        var w = Arena.New(N, 3);
        var osa = w.SpawnCrewed(1, "sam_vehicle", X, 350).Vehicle;
        Assert.Equal(10, osa.Charges[0]);
        // Eight drone trucks, two drones each: more drones than missiles.
        foreach (var x in new[] { X - 28, X - 20, X - 12, X - 4, X + 4, X + 12, X + 20, X + 28 })
        {
            var t = w.SpawnCrewed(2, "drone_truck", x, 640, Facing.North).Vehicle;
            Ground(w, t, new Cell(x, 60));
            Ground(w, t, new Cell(x, 80), queue: true);
        }
        var shots = new List<uint>();
        var left = osa.Charges[0];
        for (var i = 0; i < 3000; i++)
        {
            w.Step();
            if (osa.Charges[0] < left)
            {
                shots.Add(w.Tick);
                left = osa.Charges[0];
            }
        }
        Assert.Equal(10, shots.Count); // all 10, and no 11th
        Assert.Equal(0, osa.Charges[0]);
        for (var i = 1; i < shots.Count; i++)
        {
            Assert.True(shots[i] - shots[i - 1] >= 30, $"1.5 s between launches: {string.Join(", ", shots)}");
        }
        // A friendly depot refills it: a missile per 4 s, 60 supply each.
        var depot = w.Spawn(1, "supply_depot", X - 9, 347);
        var stock = depot.Stock;
        w.Run(80 * 10 + 60);
        Assert.Equal(10, osa.Charges[0]);
        Assert.Equal(stock - 60 * 10, depot.Stock);
    }

    [Fact]
    public void TheEngagementIsDeterministic()
    {
        static (List<AirBurst> Bursts, ulong Hash) Play()
        {
            var (w, _) = Range(vehicle: true, seed: 7);
            w.SpawnCrewed(1, "sam_vehicle", X + 10, 360);
            return (Bursts(w, 1400), w.Hash());
        }
        var a = Play();
        var b = Play();
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Bursts, b.Bursts);
    }
}
