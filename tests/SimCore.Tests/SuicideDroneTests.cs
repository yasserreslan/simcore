using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// One-way attack drones (the drone launcher truck and the engineer's drone station): fired at a
// ground point, they climb to 30 m over the map's highest ground, cruise at 12 cells/s and dive at
// 45 degrees onto the point. A hill in the climb or the first wall, vehicle or soldier in the dive
// sets one off. 250 damage in a 4-cell blast; a building loses 20% of its max HP a drone.
public class SuicideDroneTests
{
    private const long Metre = 512;

    private static void Ground(World w, Unit u, Cell at, bool queue = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.AttackGround, Units = [u.Id], Cell = at, Queue = queue });

    private static Unit Truck(World w, int x = 5, int y = 30) => w.SpawnCrewed(1, "drone_truck", x, y, Facing.East).Vehicle;

    // Flies the world until the first drone goes off (or max ticks); returns its phases seen, its
    // highest altitude, and the detonation.
    private static (List<DronePhase> Phases, long MaxAlt, Detonation? Boom, int Ticks) Fly(World w, int max = 3000)
    {
        var phases = new List<DronePhase>();
        long top = 0;
        for (var t = 0; t < max; t++)
        {
            w.Step();
            if (w.Detonations.Count > 0)
            {
                return (phases, top, w.Detonations[0], t);
            }
            foreach (var p in w.Projectiles.Where(p => p.Phase != DronePhase.None))
            {
                if (phases.Count == 0 || phases[^1] != p.Phase)
                {
                    phases.Add(p.Phase);
                }
                top = Math.Max(top, p.Altitude.Raw);
            }
        }
        return (phases, top, null, max);
    }

    private static TerrainMap Terrain(int n, Func<int, int, int> heightRaw)
    {
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = heightRaw(x, y);
            }
        }
        return new TerrainMap(n, n, corners, new byte[n * n], rules);
    }

    [Fact]
    public void ClimbsCruisesThirtyMetresOverTheHighestGroundAndDives()
    {
        var w = Arena.New(160);
        w.AttachTerrain(Terrain(160, (x, y) => x > 150 && y < 5 ? (int)(40 * Metre) : (int)(2 * Metre))); // a 40 m hill far off the line
        var truck = Truck(w);
        Ground(w, truck, new Cell(105, 30));
        var (phases, top, boom, ticks) = Fly(w);
        Assert.Equal([DronePhase.Climb, DronePhase.Cruise, DronePhase.Dive], phases);
        Assert.Equal(40 * Metre + 30 * Metre, top); // the map's highest ground plus 30 m
        Assert.NotNull(boom);
        Assert.Equal(new Cell(105, 30), Cell.Of(boom!.Value.At));
        Assert.InRange(ticks, 40 + 100 * 20 / 12, 40 + 100 * 20 / 12 + 30); // 2 s deploy, 100 cells at 12 cells/s
    }

    [Fact]
    public void AHillInTheClimbSetsItOff()
    {
        var w = Arena.New(120);
        // A ridge across the flight line 12 cells out, 60 m high (the highest ground: it would cruise at 90 m).
        w.AttachTerrain(Terrain(120, (x, y) => x is >= 17 and <= 19 ? (int)(60 * Metre) : 0));
        var truck = Truck(w);
        Ground(w, truck, new Cell(100, 30));
        var (phases, _, boom, _) = Fly(w);
        Assert.NotNull(boom);
        Assert.True(boom!.Value.Terrain);
        Assert.Equal([DronePhase.Climb], phases);
        Assert.InRange(Cell.Of(boom.Value.At).X, 14, 18);
    }

    [Fact]
    public void InTheDiveTheFirstBuildingStopsItAndTakesTwentyPercent()
    {
        var w = Arena.New(120);
        var truck = Truck(w);
        var brick = w.Spawn(2, "block_brick_4", 80, 27); // x 80-93, y 27-33: 16.5 m high
        Ground(w, truck, new Cell(97, 30));                // just past it: the dive goes through its roof
        var (_, _, boom, _) = Fly(w);
        Assert.Equal(brick.Id, boom!.Value.Hit);
        Assert.InRange(Cell.Of(boom.Value.At).X, 80, 93);
        Assert.Equal(1600 - 320, brick.HP);
    }

    [Fact]
    public void InTheDiveTheFirstVehicleStopsIt()
    {
        var w = Arena.New(120);
        var truck = Truck(w);
        var tech = w.Spawn(2, "technical", 99, 30);
        var behind = w.Spawn(2, "heavy_rocket_truck", 104, 30);
        Ground(w, truck, new Cell(100, 30));
        var (_, _, boom, _) = Fly(w);
        Assert.Equal(tech.Id, boom!.Value.Hit);
        Assert.False(tech.Alive); // 260 against 180 HP
        Assert.True(behind.Alive);
    }

    [Fact]
    public void TwoHundredSixtyDamageKillsTechnicalsLaunchersAndInfantryNotTheBigTrucks()
    {
        var w = Arena.New(64);
        var weapon = w.Match.Players[0].Find(w.Content, "drone_truck")!.Weapons[0];
        var tech = w.Spawn(2, "technical", 30, 30);
        var mrl = w.Spawn(2, "missile_truck", 30, 30);      // 260 HP
        var heavy = w.Spawn(2, "heavy_rocket_truck", 30, 30); // 300 HP
        var soldier = w.Spawn(2, "Rifleman", 33, 30);       // 3 cells out, at the blast's edge
        w.Explode(new Cell(30, 30).Center(), weapon.Damage, weapon.Warhead, weapon.Effect.Splash, 0, weapon.Effect.SplashCenter, weapon.Effect.SplashEdge);
        Assert.False(tech.Alive);
        Assert.False(mrl.Alive);
        Assert.True(heavy.Alive);
        Assert.False(soldier.Alive);
        Assert.Equal(40, weapon.Effect.Splash.Raw * 10 / Fixed.OneRaw);
    }

    [Theory]
    [InlineData("block_panel_5", 2400)]
    [InlineData("block_brick_4", 1600)]
    [InlineData("house_small", 700)]
    public void EachDroneTakesTwentyPercentOfABuildingFiveBringItDown(string id, int hp)
    {
        var w = Arena.New(64);
        var weapon = w.Match.Players[0].Find(w.Content, "drone_truck")!.Weapons[0];
        var b = w.Spawn(2, id, 2, 2);
        for (var i = 1; i <= 4; i++)
        {
            w.Hurt(b, weapon.Warhead, weapon.Damage, i == 2 ? 40 : 100); // a near miss takes the same
            Assert.Equal(hp - hp / 5 * i, b.HP);
        }
        w.Hurt(b, weapon.Warhead, weapon.Damage, 100);
        Assert.False(b.Alive);
    }

    [Fact]
    public void TwoPointsTwoDronesThreeSecondsApartOnePointOneDrone()
    {
        var w = Arena.New(160);
        var truck = Truck(w);
        Ground(w, truck, new Cell(80, 20));
        w.Step();
        Ground(w, truck, new Cell(90, 45)); // a second G while the first is pending
        var launches = new List<(uint Tick, Cell Dest)>();
        for (var t = 0; t < 300; t++)
        {
            w.Step();
            foreach (var p in w.Projectiles.Where(p => !launches.Exists(l => l.Dest == Cell.Of(p.Dest))))
            {
                launches.Add((w.Tick, Cell.Of(p.Dest)));
            }
        }
        Assert.Equal([new Cell(80, 20), new Cell(90, 45)], launches.Select(l => l.Dest));
        Assert.Equal(60, (int)(launches[1].Tick - launches[0].Tick));
        Assert.Equal(OrderKind.Idle, truck.Order.Kind);

        var one = Arena.New(160);
        var t1 = Truck(one);
        Ground(one, t1, new Cell(80, 20));
        one.Run(300);
        Assert.Equal(1, t1.Charges[0]); // one point, one drone; the other stays aboard
        Assert.Equal(OrderKind.Idle, t1.Order.Kind);

        var shift = Arena.New(160);
        var t2 = Truck(shift);
        Ground(shift, t2, new Cell(80, 20));
        Ground(shift, t2, new Cell(70, 40), queue: true);
        shift.Run(300);
        Assert.Equal(0, t2.Charges[0]);
    }

    [Fact]
    public void TheTruckReloadsStowedAtADepotThirtySecondsAndOneFiftyADrone()
    {
        var w = Arena.New(160);
        var depot = w.Spawn(1, "supply_depot", 2, 28);
        var truck = Truck(w, 8, 30);
        Ground(w, truck, new Cell(80, 20));
        Ground(w, truck, new Cell(90, 45), queue: true);
        w.Run(200);
        Assert.Equal(0, truck.Charges[0]);
        w.Run(40 + 600 + 20); // it stows (2 s), then a drone every 30 s
        Assert.Equal(1, truck.Charges[0]);
        Assert.Equal(1000 - 150, depot.Stock);
        w.Run(600);
        Assert.Equal(2, truck.Charges[0]);
        Assert.Equal(1000 - 300, depot.Stock);
    }

    [Fact]
    public void AnEngineerSetsUpAStationInTwoSecondsItFiresAfterHeDiesAndGoes()
    {
        var w = Arena.New(160);
        var engineer = w.Spawn(1, "Sapper", 10, 30);
        var stock = engineer.Def.Abilities.Single(a => a.Effect is PlacerEffect { DepotReload: not null }).Stock;
        Assert.Equal(1, engineer.Charges[stock]);
        w.Order(1, CommandKind.UseAbility, [engineer], cell: new Cell(11, 30), ability: "drone_station_kit");
        w.Run(30);
        Assert.Empty(w.All("drone_station"));
        w.Run(15);
        var station = Assert.Single(w.All("drone_station"));
        Assert.Equal(0, engineer.Charges[stock]);
        engineer.HP = 0; // he is gone; the station is on its own
        w.Run(2);
        Ground(w, station, new Cell(90, 30));
        w.Run(3);
        var drone = Assert.Single(w.Projectiles);
        Assert.Equal(InterceptClass.Drone, drone.Interceptible);
        Assert.Equal(0, station.Charges[0]);
        w.Run(380);
        Assert.NotNull(w.Unit(station.Id)); // the empty rail stays a while
        w.Run(40);
        Assert.Null(w.Unit(station.Id));
    }

    [Fact]
    public void TheEngineersKitRefillsAtADepotTenSecondsForOneFifty()
    {
        var w = Arena.New(64);
        var depot = w.Spawn(1, "supply_depot", 2, 2);
        var engineer = w.Spawn(1, "Sapper", 10, 5);
        var stock = engineer.Def.Abilities.Single(a => a.Effect is PlacerEffect { DepotReload: not null }).Stock;
        w.Step(); // full: nothing counting
        engineer.MutableCharges[stock] = 0;
        w.Run(190);
        Assert.Equal(0, engineer.Charges[stock]);
        w.Run(20);
        Assert.Equal(1, engineer.Charges[stock]);
        Assert.Equal(1000 - 150, depot.Stock);
        // A station never refills, even at a depot.
        var station = w.Spawn(1, "drone_station", 9, 7);
        station.MutableCharges[0] = 0;
        w.Run(400);
        Assert.Equal(0, station.Charges[0]);
    }

    [Fact]
    public void TheSameOrdersGiveTheSameHashes()
    {
        ulong[] Run()
        {
            var w = Arena.New(160);
            w.AttachTerrain(Terrain(160, (x, y) => (int)((x % 17 + y % 11) * Metre / 2)));
            var truck = Truck(w);
            w.Spawn(2, "block_panel_5", 90, 24);
            w.Spawn(2, "technical", 70, 40);
            Ground(w, truck, new Cell(100, 27));
            Ground(w, truck, new Cell(70, 40), queue: true);
            var hashes = new ulong[400];
            for (var i = 0; i < hashes.Length; i++)
            {
                w.Step();
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }
}
