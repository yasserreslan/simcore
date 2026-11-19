using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The Samad (samad_drone) and its launch truck (samad_truck; docs/design.md, "Samad recon drone"; the game repo's
// docs/plans/samad-spec.md, the owner 2026-11-16): a high-altitude recon drone, 450 m up at 30 cells/s with 6 min of fuel and
// no way home; only the Patriot and THAAD types and the jets' air-to-air reach it; every sensor sees it at half range. The
// truck sets up in 2.5 s, launches 0.4 s into the launch, feeds its second drone in 3 s and reloads at a depot, packed up.
public class SamadTests(ITestOutputHelper output)
{
    private const int N = 400;
    private const int M = 512; // raw per metre
    private const long Height = 450L * M; // its cruise height over flat ground, raw

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    private static void Give(World w, Unit d, AirTaskKind task, Cell cell, bool queue = false, Cell? cell2 = null, AirTargetKinds filter = 0) =>
        w.Issue(new Command
        {
            Tick = w.Tick, Player = d.Owner, Kind = CommandKind.AirTask, Units = [d.Id], Mission = task, Cell = cell, Cell2 = cell2,
            Queue = queue, Filter = (byte)filter,
        });

    // Launch orders truck to launch at cell and steps until its new drone leaves the rail: the ticks it took and the drone.
    private static (int Ticks, Unit Drone) Launch(World w, Unit truck, Cell at, int max = 400)
    {
        var before = w.All("samad_drone").Select(d => d.Id).ToList();
        w.Order(truck.Owner, CommandKind.UseAbility, [truck], cell: at, ability: "samad_rail");
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            if (w.All("samad_drone").FirstOrDefault(d => d.Source == truck.Id && !before.Contains(d.Id)) is { } d)
            {
                return (t, d);
            }
        }
        throw new Xunit.Sdk.XunitException("the truck never launched");
    }

    // ---- The data ----

    [Fact]
    public void TheSamadAndItsTruckAreData()
    {
        var w = Arena.New(N, content: TestContent.ClassicSupply);
        var c = w.Content;
        var a = c.Archetype("samad_drone")!;
        var fl = a.Flight!;
        Assert.Equal(Height, fl.HeightRaw);
        Assert.Equal(InterceptClass.Samad, fl.Class); // its own class, the last in air_classes.json
        Assert.False(fl.Recover);
        Assert.Equal(7200, a.Lifetime); // 6 min of fuel
        Assert.Equal(new Signature(50, 100, 100), a.Signature);
        var truck = c.Archetype("samad_truck")!;
        Assert.Equal((50, 50, 1500), (truck.DeployTicks, truck.StowTicks, truck.Cost));
        var def = w.Player(1)!.Setup.Find(c, "samad_truck")!;
        var ab = def.Abilities.Single();
        var l = Assert.IsType<LaunchEffect>(ab.Effect);
        Assert.Equal((2, 72, 2, true, 8), (l.Charges, l.Cooldown, l.MaxActive, l.SetUp, l.LaunchTicks));
        Assert.Equal(new DepotReload(600, 0, 400, true), l.DepotReload);
        // On the Resistance roster, built where the scout drone's truck is built (the vehicle garage).
        Assert.NotNull(w.Player(1)!.Setup.Find(c, "samad_drone"));
        var garage = c.Archetype("vehicle_garage")!.Production!;
        Assert.True(garage.Builds(truck.Tags) && garage.Builds(c.Archetype("scout_truck")!.Tags));
    }

    // ---- The truck: set-up, launch, feed ----

    [Fact]
    public void TheTruckSetsUpLaunchesAtTheArtsEventAndFeedsTheSecondIn3S()
    {
        var w = Arena.New(N, content: TestContent.ClassicSupply);
        var truck = w.SpawnCrewed(1, "samad_truck", 100, 300).Vehicle;
        Assert.Equal(DeployState.Stowed, truck.Deploy);
        w.Order(1, CommandKind.UseAbility, [truck], cell: new Cell(300, 100), ability: "samad_rail");
        int deploying = -1, deployed = -1, first = -1;
        Unit? d1 = null;
        for (var t = 0; t < 200 && first < 0; t++)
        {
            w.Step();
            deploying = deploying < 0 && truck.Deploy == DeployState.Deploying ? t : deploying;
            deployed = deployed < 0 && truck.Deploy == DeployState.Deployed ? t : deployed;
            if (w.All("samad_drone").SingleOrDefault() is { } d)
            {
                (first, d1) = (t, d);
            }
        }
        Assert.Equal(50, deployed - deploying); // the rail's 2.5 s
        Assert.Equal(8, first - deployed); // it leaves the rail 0.4 s into the launch (the art's event, frame 12 at 30 fps)
        Assert.Equal(1, truck.Charges[0]);
        Assert.Equal(DeployState.Deployed, truck.Deploy); // it stays set up
        Assert.True(d1!.Altitude.Raw < 4 * M, $"off the rail low: {d1.Altitude.Raw}");
        // The second at once: the rest of the launch (0.6 s) and the 3 s feed, then its own 0.4 s: 4 s after the first.
        var (ticks, d2) = Launch(w, truck, new Cell(300, 300));
        Assert.Equal(80, ticks + 0);
        Assert.Equal(0, truck.Charges[0]);
        Assert.NotEqual(d1.Id, d2.Id);
        Assert.Equal(2, w.All("samad_drone").Count(d => d.Alive)); // both fly
        // Empty: nothing more launches.
        w.Order(1, CommandKind.UseAbility, [truck], cell: new Cell(200, 200), ability: "samad_rail");
        w.Run(200);
        Assert.Equal(2, w.All("samad_drone").Count);
        // Ordered away it packs up (2.5 s) before it drives.
        w.Order(1, CommandKind.Move, [truck], cell: new Cell(100, 340));
        w.Run(3);
        Assert.Equal(DeployState.Stowing, truck.Deploy);
        var at = truck.Pos;
        w.Run(45);
        Assert.Equal(at, truck.Pos);
        w.Run(60);
        Assert.Equal(DeployState.Stowed, truck.Deploy);
        Assert.NotEqual(at, truck.Pos);
        output.WriteLine($"set-up 50 ticks, launch +8, second +{ticks}");
    }

    // ---- Flight, orders and fuel ----

    [Fact]
    public void ItClimbsTo450MFliesAt30CellsASecondAndOrbitsThePoint()
    {
        var w = Arena.New(N, content: TestContent.ClassicSupply);
        var truck = w.SpawnCrewed(1, "samad_truck", 50, 350).Vehicle;
        var (_, drone) = Launch(w, truck, new Cell(300, 100));
        var climbed = -1;
        for (var t = 0; t < 1200 && climbed < 0; t++)
        {
            w.Step();
            climbed = drone.Altitude.Raw >= Height ? t : -1;
        }
        Assert.InRange(climbed, 590, 620); // 15 m/s from 3 m: about 30 s (it rounds off at the top)
        w.Run(600);
        var center = new Cell(300, 100).Center();
        long lo = long.MaxValue, hi = 0, flown = 0;
        for (var i = 0; i < 600; i++)
        {
            var before = drone.Pos;
            w.Step();
            flown += (drone.Pos - before).Len().Raw;
            var r = (drone.Pos - center).Len().Raw;
            (lo, hi) = (Math.Min(lo, r), Math.Max(hi, r));
            Assert.Equal(Height, drone.Altitude.Raw);
        }
        Assert.InRange(flown * 20 / 600 / Fixed.OneRaw, 29, 30); // 30 cells/s (chords of its circle): twice the Kaman drone's 15
        Assert.InRange(Cells(lo), 45, 62); // the 60-cell circle
        Assert.InRange(Cells(hi), 50, 66);
        output.WriteLine($"climbed in {climbed} ticks; orbit {Cells(lo)} to {Cells(hi)} cells");
    }

    [Fact]
    public void ItTakesTheScoutDronesOrdersANewPointAndAPatrolRoute()
    {
        var w = Arena.New(N, content: TestContent.ClassicSupply);
        var drone = w.Spawn(1, "samad_drone", 100, 100);
        drone.Altitude = Fixed.FromRaw(Height);
        w.Order(1, CommandKind.Move, [drone], cell: new Cell(300, 300));
        w.Run(600);
        Assert.InRange(Cells((drone.Pos - new Cell(300, 300).Center()).Len().Raw), 40, 66); // a new point in the air: it orbits there
        Give(w, drone, AirTaskKind.Patrol, new Cell(100, 200));
        Give(w, drone, AirTaskKind.Patrol, new Cell(300, 200), queue: true);
        long nearA = long.MaxValue, nearB = long.MaxValue;
        var legs = new List<int>();
        for (var t = 0; t < 1600; t++)
        {
            w.Step();
            if (legs.Count == 0 || legs[^1] != drone.AirMission!.Leg)
            {
                legs.Add(drone.AirMission!.Leg);
            }
            nearA = Math.Min(nearA, (drone.Pos - new Cell(100, 200).Center()).Len().Raw);
            nearB = Math.Min(nearB, (drone.Pos - new Cell(300, 200).Center()).Len().Raw);
        }
        Assert.True(legs.Count >= 4, $"back and forth: {string.Join(",", legs)}");
        Assert.InRange(Cells(nearA), 0, 60);
        Assert.InRange(Cells(nearB), 0, 60);
        Assert.True(drone.Alive);
    }

    // The owner's scout drone rule (2026-11-02): no trip home; out of fuel it crashes where it is and the truck needs a new drone.
    // 6 min at 30 cells/s: 10,800 cells, far beyond the largest map's diagonal and its fly zone.
    [Fact]
    public void OutOfFuelItCrashesWhereItIsAndNeverComesHome()
    {
        var w = Arena.New(N, content: TestContent.ClassicSupply);
        var truck = w.SpawnCrewed(1, "samad_truck", 30, 370).Vehicle;
        var (_, drone) = Launch(w, truck, new Cell(300, 100));
        Assert.Equal(7200, drone.Tank);
        var lostAt = -1;
        AirLoss? loss = null;
        for (var t = 0; t < 7300 && lostAt < 0; t++)
        {
            w.Step();
            Assert.False(drone.Homing, $"it never turns for home (tick {t})");
            if (t >= 1200 && drone.Alive)
            {
                Assert.InRange(Cells((drone.Pos - new Cell(300, 100).Center()).Len().Raw), 40, 70); // on its orbit to the end
            }
            if (w.AirLosses.Count > 0)
            {
                (lostAt, loss) = (t, w.AirLosses.Single());
            }
        }
        Assert.InRange(lostAt, 7190, 7200); // the whole 6 min
        Assert.Equal(KillCause.Fuel, loss!.Value.Cause);
        Assert.False(drone.Recovered);
        Assert.Single(w.Wrecks);
        w.Run(600);
        Assert.Empty(w.All("samad_drone"));
        Assert.Equal(1, truck.Charges[0]); // its second drone; the lost one comes only from a depot
    }

    // ---- Its sight: an air unit's, by the concealment rules ----

    // A soldier standing still under dense trees stays hidden from it even straight below; a soldier in the open 90 cells off is seen (its
    // 100-cell sight, over the ground at its height); one at 110 cells is not.
    [Theory]
    [InlineData(3, 0, false)]
    [InlineData(0, 90, true)]
    [InlineData(0, 110, false)]
    public void ItSeesAWideCircleButNotUnderTheTrees(int density, int cells, bool seen)
    {
        const int size = 256, tx = 128, ty = 128;
        var w = Arena.New(size, edit: s => s with { Fog = true }, content: TestContent.ClassicSupply);
        var flags = new byte[size * size];
        for (var i = 0; i < flags.Length; i++)
        {
            int x = i % size, y = i / size;
            flags[i] = (byte)(x is >= 118 and < 138 && y is >= 118 and < 138 ? density : 0);
        }
        w.AttachTerrain(new TerrainMap(size, size, new int[(size + 1) * (size + 1)], flags,
            Content.TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"))));
        var eye = w.Spawn(1, "samad_drone", 2, 2);
        eye.Altitude = Fixed.FromRaw(Height);
        var t = w.Spawn(2, "Rifleman", tx, ty);
        t.Stance = Stance.HoldFire;
        eye.Pos = new Cell(tx + cells, ty).Center();
        eye.LookedAt = uint.MaxValue;
        w.Step();
        Assert.True(w.InAir(eye));
        Assert.Equal(seen, World.IsSpotted(t, 1));
    }

    // ---- The depot reload ----

    [Fact]
    public void ItReloadsAtADepotPackedUpOneDroneIn30SFor400SupplyNeverWithTwoAloft()
    {
        var w = Arena.New(N, content: TestContent.ClassicSupply);
        var truck = w.SpawnCrewed(1, "samad_truck", 100, 300).Vehicle;
        var depot = w.Spawn(1, "supply_depot", 104, 300);
        var stock = depot.Stock;
        var (_, d1) = Launch(w, truck, new Cell(300, 100));
        var (_, d2) = Launch(w, truck, new Cell(300, 300));
        w.Run(1500); // both aloft: no reload
        Assert.Equal(0, truck.Charges[0]);
        Assert.Equal(stock, depot.Stock);
        d1.HP = 0; // one lost
        int stowed = -1, one = -1;
        for (var t = 0; t < 800 && one < 0; t++)
        {
            w.Step();
            stowed = stowed < 0 && truck.Deploy == DeployState.Stowed ? t : stowed;
            one = truck.Charges[0] == 1 ? t : -1;
        }
        Assert.InRange(stowed, 49, 52); // set up and idle at a depot, it packs up first (2.5 s)
        Assert.InRange(one - stowed, 599, 601); // then 30 s
        Assert.Equal(stock - 400, depot.Stock);
        d2.HP = 0;
        w.Run(610);
        Assert.Equal(2, truck.Charges[0]);
        Assert.Equal(stock - 800, depot.Stock);
        // A depot short of 400 reloads nothing.
        Launch(w, truck, new Cell(300, 100));
        w.All("samad_drone").First(d => d.Alive).HP = 0;
        depot.Stock = 399;
        w.Run(1300);
        Assert.Equal(1, truck.Charges[0]);
        Assert.Equal(399, depot.Stock);
    }

    // ---- Seen at half range ----

    // TrackedAt: is unit (player 2's) at slant distance about dx cells from sensor s (at its eye height) tracked by s's side after a tick?
    private bool TrackedAt(string sensor, int dx, string unit)
    {
        var w = Arena.NewSentinel(N, content: TestContent.ClassicSupply);
        var s = sensor == "radar_station" ? w.Spawn(1, sensor, 150, 200) : w.SpawnCrewed(1, sensor, 150, 200).Vehicle;
        w.Run(2);
        var eye = w.RadarEyeRaw(s);
        var b = w.Spawn(2, unit, 150 + dx, 200, Facing.North);
        b.Altitude = Fixed.FromRaw(eye);
        w.Order(2, CommandKind.Move, [b], cell: new Cell(150 + dx, 100));
        w.Run(1);
        return w.AirTracked(1, b.Id);
    }

    [Theory]
    [InlineData("radar_truck", 360)]
    [InlineData("radar_station", 600)]
    public void RadarsSeeItAtHalfTheirRange(string sensor, int range)
    {
        Assert.True(TrackedAt(sensor, range / 2 + 60, "scout_drone"), "a normal drone (the scout drone) beyond half the range");
        Assert.False(TrackedAt(sensor, range / 2 + 4, "samad_drone"), "the Samad beyond half");
        Assert.True(TrackedAt(sensor, range / 2 - 4, "samad_drone"), "the Samad within half");
        var up = Height / Fixed.OneRaw;
        var ground = range / 2 > up ? (long)Math.Sqrt((range / 2.0) * (range / 2.0) - up * up) : 0;
        output.WriteLine($"{sensor}: {range} cells of slant, the Samad {range / 2}; on its orbit {up} cells up: {ground} cells of ground");
    }

    // ---- Who can engage it ----

    // Engaged: shooter (and its radar, if it needs one) under a Samad orbiting at height metres; whether over a minute the shooter
    // picks it (its air target), fires at it (a launch or a gun burst at it) or hurts it.
    private (bool Engaged, int Launches) Engaged(string shooter, int metres, bool sentinel)
    {
        // A Sentinel shooter is player 1 of the Sentinel lobby against the Resistance's Samad; a Resistance one player 2 of the mirror.
        var w = sentinel ? Arena.NewSentinel(N, content: TestContent.ClassicSupply) : Arena.New(N, content: TestContent.ClassicSupply);
        byte us = sentinel ? (byte)1 : (byte)2, them = sentinel ? (byte)2 : (byte)1;
        Unit Crewed(string id, int x) => w.Content.Archetype(id)!.Crew is not null ? w.SpawnCrewed(us, id, x, 200).Vehicle : w.Spawn(us, id, x, 200);
        var radar = shooter switch
        {
            "patriot_launcher" or "thaad_launcher" => "radar_station",
            "kub_launcher" => "mobile_radar",
            "s400_launcher" => "s400_radar",
            "korkut_gun" => "korkut_command",
            _ => null,
        };
        var jet = shooter.EndsWith("_jet", StringComparison.Ordinal) || shooter == "stealth_multirole";
        Unit u;
        if (jet)
        {
            u = w.Spawn(us, shooter == "multirole_jet" ? "Multirole 500 lb" : shooter, 120, 200, Facing.East);
            Give(w, u, AirTaskKind.Guard, new Cell(100, 100), cell2: new Cell(300, 300), filter: AirTargetKinds.Drones | AirTargetKinds.Aircraft);
        }
        else
        {
            u = Crewed(shooter, 200);
            if (radar is not null)
            {
                Crewed(radar, 215);
            }
            foreach (var x in w.All(shooter).Concat(radar is null ? [] : w.All(radar)))
            {
                if (x.Def.Archetype.DeployTicks > 0)
                {
                    w.Order(us, CommandKind.Deploy, [x]);
                }
            }
            w.Run(560); // every set-up done (the THAAD type's 26 s the longest)
        }
        var d = w.Spawn(them, "samad_drone", 200, 200);
        d.Altitude = Fixed.FromRaw(metres * (long)M);
        w.Order(them, CommandKind.Move, [d], cell: new Cell(200, 200));
        var engaged = false;
        var launches = 0;
        var hp = d.HP;
        for (var t = 0; t < 1200 && !engaged && d.Alive; t++)
        {
            w.Step();
            launches += w.AirLaunches.Count(l => l.Target == d.Id);
            engaged |= w.Units.Any(x => x.Owner == us && x.AirTarget == d.Id) || launches > 0 || d.HP < hp ||
                w.AirBursts.Any(b => b.Target == d.Id);
        }
        return (engaged || !d.Alive, launches);
    }

    [Theory]
    [InlineData("patriot_launcher")]
    [InlineData("thaad_launcher")]
    [InlineData("fighter_jet")]
    [InlineData("multirole_jet")]
    [InlineData("stealth_multirole")]
    public void ThePatriotAndThaadTypesAndTheJetsReachItAt450M(string shooter)
    {
        var (engaged, launches) = Engaged(shooter, 450, sentinel: true);
        Assert.True(engaged, $"{shooter} engages the Samad on its orbit");
        output.WriteLine($"{shooter}: engaged, {launches} missiles in the first minute");
    }

    [Theory]
    [InlineData("sam_vehicle", false)] // the Osa type: its 440 m ceiling (and 220-cell slant)
    [InlineData("manpads", false)] // the AA soldier's Igla: 320 m
    [InlineData("zu23_carrier", false)] // the ZU-23: 300 m (AA-RANGE)
    [InlineData("technical", false)] // the heavy MG: 40 m
    [InlineData("kub_launcher", false)] // the SA-6 type: not its class
    [InlineData("s400_launcher", false)] // the S-400 type: aircraft classes only
    [InlineData("korkut_gun", true)] // the Korkut: not its class
    public void NoGunAndNoShortRangeMissileReachesItAt450M(string shooter, bool sentinel)
    {
        Assert.False(Engaged(shooter, 450, sentinel).Engaged, $"{shooter} never engages it on its orbit");
    }

    // By the ceiling, not the class: climbing out low it is in reach.
    [Theory]
    [InlineData("sam_vehicle", 150)]
    [InlineData("manpads", 100)]
    [InlineData("zu23_carrier", 100)]
    public void LowOnItsClimbOutTheCeilingGatedWeaponsDoEngageIt(string shooter, int metres)
    {
        Assert.True(Engaged(shooter, metres, sentinel: false).Engaged, $"{shooter} engages it {metres} m up");
    }
}
