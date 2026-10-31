using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The antenna network (docs/design.md, "Antenna network"; the game repo's docs/plans/antenna-network-spec.md, owner 2026-10-30):
// the Sentinel's antenna towers chain to a radar station or a set-up radar truck, report enemy launches (point, class, predicted
// landing point; the launcher seen for 10 s), see drones through the fog, and a linked chain warns the Patriot and THAAD types.
// Player 1 is the Sentinel, player 2 the Resistance.
public class AntennaNetworkTests
{
    private const int Y = 300;

    private static void Order(World w, Unit u, CommandKind kind, Cell cell = default) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell });

    // Antenna: player's antenna tower with its centre at (cx, cy) (its 2 x 2 footprint from (cx - 1, cy - 1)).
    private static Unit Antenna(World w, int cx, int cy, byte player = 1) => w.Spawn(player, "antenna_tower", cx - 1, cy - 1);

    // Station: player 1's radar station with its centre at (cx, cy).
    private static Unit Station(World w, int cx, int cy) => w.Spawn(1, "radar_station", cx - 4, cy - 4);

    private static Unit Crewed(World w, byte player, string kind, int x, int y, int facing = Facing.North) => w.SpawnCrewed(player, kind, x, y, facing).Vehicle;

    // Deploy sets the units up (each side's order) and runs until all are.
    private static void Deploy(World w, params Unit[] units)
    {
        foreach (var g in units.GroupBy(u => u.Owner))
        {
            w.Order(g.Key, CommandKind.Deploy, g);
        }
        w.Run(units.Max(u => u.Def.Archetype.DeployTicks) + 2);
        Assert.All(units, u => Assert.Equal(DeployState.Deployed, u.Deploy));
    }

    // Watch runs ticks and returns, per new interceptor, the tick it appeared and its projectile; the first tick a threat of the
    // given class appeared (-1 for none); and every launch report made meanwhile (they go once their threat is gone).
    private static (List<(uint Tick, Projectile P)> Shots, int Launch, List<LaunchReport> Reports) Watch(World w, int ticks, InterceptClass threat)
    {
        var shots = new List<(uint, Projectile)>();
        var launch = -1;
        var reports = new List<LaunchReport>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            reports.AddRange(w.LaunchReports.Where(r => !reports.Contains(r)));
            foreach (var p in w.Projectiles)
            {
                if (p.Weapon.Effect.Interceptor is not null && shots.All(s => s.Item2.Id != p.Id))
                {
                    shots.Add((w.Tick, p));
                }
                if (launch < 0 && p.Interceptible == threat)
                {
                    launch = (int)w.Tick;
                }
            }
        }
        return (shots, launch, reports);
    }

    // ---- Content ----

    [Fact]
    public void ItIsData()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype("antenna_tower")!;
        Assert.Equal((2, 2, 150, 300, 300), (a.FootprintW, a.FootprintH, a.MaxHp, a.Cost, a.BuildTime)); // 15 s
        Assert.Equal((Fixed.FromInt(50), 30L * 512), (a.Vision, a.HeightRaw));
        var an = a.Network!;
        Assert.False(an.Hub);
        Assert.Equal((Fixed.FromInt(250).Raw, Fixed.FromInt(150).Raw, Fixed.FromInt(200).Raw, 400, 200),
            (an.LaunchReachRaw, an.DroneReachRaw, an.LinkRangeRaw, an.ReportTicks, an.LauncherTicks));
        foreach (var cls in new[] { InterceptClass.Rocket, InterceptClass.Missile, InterceptClass.Ballistic, InterceptClass.Hypersonic, InterceptClass.Cruise, InterceptClass.Drone })
        {
            Assert.True(an.Reports(cls), cls.ToString());
        }
        Assert.False(an.Reports(InterceptClass.Multirole));
        Assert.True(an.SeesDrone(InterceptClass.Drone) && an.SeesDrone(InterceptClass.Scout) && an.SeesDrone(InterceptClass.Kaman));
        Assert.False(an.SeesDrone(InterceptClass.Fighter) || an.SeesDrone(InterceptClass.Cruise));

        var s = c.Archetype("radar_station")!;
        Assert.Equal((8, 8, 1500, 2000, 1200), (s.FootprintW, s.FootprintH, s.MaxHp, s.Cost, s.BuildTime)); // 60 s
        Assert.Equal((Fixed.FromInt(600), Fixed.FromInt(80), Fixed.FromInt(80), 0), (s.AirVision, s.GroundRadar, s.Vision, s.DeployTicks)); // no set-up
        Assert.True(s.Network!.Hub);
        Assert.Equal(Fixed.FromInt(600).Raw, s.Network.LaunchReachRaw);

        var truck = c.Archetype("radar_truck")!.Network!;
        Assert.True(truck.Hub);
        Assert.Equal(Fixed.FromInt(360).Raw, truck.LaunchReachRaw); // its air reach

        var sentinel = c.Factions.Single(f => f.Id == "sentinel");
        Assert.True(sentinel.Archetypes[a.Index] && sentinel.Archetypes[s.Index]);

        var w = Arena.NewSentinel(64);
        var patriot = w.Player(1)!.Setup.Find(w.Content, "patriot_launcher")!.Weapons[0].Effect.Interceptor!;
        var thaad = w.Player(1)!.Setup.Find(w.Content, "thaad_launcher")!.Weapons[0].Effect.Interceptor!;
        Assert.Equal(0, patriot.Network!.Leave);
        Assert.True(thaad.Network!.Leaves(InterceptClass.Missile) && thaad.Network.Leaves(InterceptClass.Rocket) && thaad.Network.Leaves(InterceptClass.Cruise));
        Assert.False(thaad.Network.Leaves(InterceptClass.Ballistic) || thaad.Network.Leaves(InterceptClass.Hypersonic));
    }

    [Fact]
    public void TheTowerIsEasyToDestroy()
    {
        // Light vehicle armour (owner: "easy to destroy"): a rifle round does 40% (4 of 12), so five riflemen bring its 150 HP down
        // in about 5 s; an RPG (the Tank hunter's at_missile, 70 at 120%) takes 84, more than half; a Kornet type missile 126.
        var w = Arena.NewSentinel(120);
        var a = Antenna(w, 60, 60);
        Assert.Equal(TestContent.Mvp.ArmorClasses.ToList().IndexOf("light_vehicle"), a.Def.Armor);
        var riflemen = Enumerable.Range(0, 5).Select(i => w.Spawn(2, "Rifleman", 56 + i * 2, 64)).ToList(); // in reach (a rifle's 5 cells)
        w.Order(2, CommandKind.Attack, riflemen, target: a.Id);
        var start = w.Tick;
        while (a.Alive && w.Tick - start < 400)
        {
            w.Step();
        }
        Assert.False(a.Alive);
        var ticks = (int)(w.Tick - start);
        Assert.InRange(ticks, 80, 120); // 106 ticks, 5.3 s (with building armour they had not brought it down in 20 s)

        var b = Antenna(w, 60, 30);
        var rpg = w.Spawn(2, "Tank hunter", 60, 50); // 20 cells off
        w.Order(2, CommandKind.Attack, [rpg], target: b.Id);
        for (var i = 0; i < 200 && b.HP == b.Def.MaxHp; i++)
        {
            w.Step();
        }
        Assert.Equal(150 - 84, b.HP); // one rocket: 84 of 150
    }

    [Fact]
    public void TheBulldozerBuildsBoth()
    {
        var w = Arena.NewSentinel(120);
        var dozer = w.Spawn(1, "bulldozer", 20, 20);
        w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.Build, Units = [dozer.Id], Cell = new Cell(30, 20), Unit = "antenna_tower" });
        w.Run(2);
        var site = Assert.Single(w.All("antenna_tower"));
        Assert.False(site.IsBuilt);
        Assert.Empty(w.NetworkMembers); // a site is no member yet
        w.Run(400);
        Assert.True(site.IsBuilt);
        Assert.Single(w.NetworkMembers);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [dozer.Id], Cell = new Cell(30, 40), Unit = "radar_station" });
        w.Run(1400);
        Assert.True(Assert.Single(w.All("radar_station")).IsBuilt);
    }

    // ---- Linking ----

    [Fact]
    public void AntennasChainToARadarAndAChainBreaksWhenAnAntennaFalls()
    {
        var w = Arena.NewSentinel(700);
        var st = Station(w, 40, 100);
        var a1 = Antenna(w, 220, 100); // 180 cells from the station
        var a2 = Antenna(w, 400, 100); // 180 from a1
        var a3 = Antenna(w, 580, 100); // 180 from a2
        var lone = Antenna(w, 580, 400); // 300 from a3
        w.Step();
        Assert.Equal([new NetworkLink(st.Id, a1.Id, true), new NetworkLink(a1.Id, a2.Id, false), new NetworkLink(a2.Id, a3.Id, false)], w.NetworkLinks);
        Assert.True(w.NetworkLinked(st) && w.NetworkLinked(a1) && w.NetworkLinked(a2) && w.NetworkLinked(a3));
        Assert.False(w.NetworkLinked(lone));
        Assert.Equal((a1.Id, a1.Id, a1.Id, lone.Id), (w.NetworkChain(a1), w.NetworkChain(a2), w.NetworkChain(a3), w.NetworkChain(lone)));

        // The middle antenna falls: a3 is cut off (it still works, unlinked).
        a2.HP = 0;
        w.Run(2);
        Assert.Equal([new NetworkLink(st.Id, a1.Id, true)], w.NetworkLinks);
        Assert.True(w.NetworkLinked(a1));
        Assert.False(w.NetworkLinked(a3));
        Assert.Equal(a3.Id, w.NetworkChain(a3));
        Assert.Contains(w.NetworkMembers, m => m.Id == a3.Id);

        // A new antenna in the gap links it again.
        var a4 = Antenna(w, 400, 110);
        w.Step();
        Assert.True(w.NetworkLinked(a3) && w.NetworkLinked(a4));
        Assert.Equal(a1.Id, w.NetworkChain(a3));
    }

    [Fact]
    public void ARadarTruckIsAHubOnlySetUp()
    {
        var w = Arena.NewSentinel(400);
        var truck = Crewed(w, 1, "radar_truck", 50, 100);
        var a = Antenna(w, 230, 100);
        w.Step();
        Assert.False(w.NetworkLinked(a));
        Assert.Empty(w.NetworkLinks);
        Deploy(w, truck);
        Assert.True(w.NetworkLinked(a) && w.NetworkLinked(truck));
        Assert.Equal([new NetworkLink(truck.Id, a.Id, true)], w.NetworkLinks);
        Order(w, truck, CommandKind.Stow);
        w.Run(truck.Def.Archetype.StowTicks + 2);
        Assert.False(w.NetworkLinked(a) || w.NetworkLinked(truck));
        Assert.Empty(w.NetworkLinks);
    }

    // ---- Launch reports ----

    // Raid: along y = Y on a 1,000-cell map, a set-up Patriot type launcher at x 500 and a set-up radar truck at x 200 (its 360 cells
    // of air reach end near x 560: a drone flying in from the east is tracked only over its last 60 cells); antennas 20 cells north at
    // x 380 and 740 and, with chain, at 560 (linking the 740 one to the truck); the Resistance's drone truck at x 900 sends a drone at
    // the launcher (a report by the 740 antenna, 161 cells from the launch).
    private static (World W, Unit Patriot, Unit Far, Unit Truck) Raid(bool chain, ulong seed = 1)
    {
        var w = Arena.NewSentinel(1000, seed);
        var patriot = Crewed(w, 1, "patriot_launcher", 500, Y);
        var radar = Crewed(w, 1, "radar_truck", 200, Y);
        Antenna(w, 380, Y - 20);
        if (chain)
        {
            Antenna(w, 560, Y - 20);
        }
        var far = Antenna(w, 740, Y - 20);
        var truck = Crewed(w, 2, "drone_truck", 900, Y, Facing.West);
        Deploy(w, patriot, radar);
        Order(w, truck, CommandKind.AttackGround, new Cell(500, Y + 3));
        return (w, patriot, far, truck);
    }

    [Fact]
    public void ALaunchReportCarriesThePointTheClassAndThePredictedLandingPoint()
    {
        var (w, _, far, truck) = Raid(chain: true);
        Projectile? drone = null;
        for (var i = 0; i < 300 && drone is null; i++)
        {
            w.Step();
            drone = w.Projectiles.FirstOrDefault(p => p.Interceptible == InterceptClass.Drone);
        }
        Assert.NotNull(drone);
        var r = Assert.Single(w.LaunchReports);
        Assert.Same(r, w.LaunchReportOf(1, drone.Id));
        Assert.Null(w.LaunchReportOf(2, drone.Id)); // its own side gets none
        Assert.Equal((drone.Id, (byte)1, InterceptClass.Drone, truck.Id, truck.Pos, drone.Dest), (r.Threat, r.Player, r.Class, r.Launcher, r.LaunchPoint, r.Predicted));
        Assert.Equal((w.Tick - 1, far.Id, true), (r.Tick, r.Detector, r.Linked)); // reported at the end of its launch tick
        Assert.Equal((r.Tick + 201, r.Tick + 401), (r.LauncherUntil, r.Until)); // the launcher seen 10 s, the launch shown 20 s
        Assert.True(r.Nearest <= Fixed.FromInt(4)); // its point is 3 cells from the launcher
        // It stays while the drone flies and goes 20 s after the launch once the drone is gone (the warned launcher shoots it down).
        w.Run(200);
        Assert.Single(w.LaunchReports);
        for (var i = 0; i < 1500 && w.LaunchReports.Count > 0; i++)
        {
            w.Step();
        }
        Assert.Empty(w.LaunchReports);
        Assert.Null(w.ProjectileById(drone.Id));
    }

    [Fact]
    public void AWarnedPatriotEngagesEarlierThanAnUnwarnedOne()
    {
        int FirstShot(bool chain)
        {
            var (w, patriot, far, _) = Raid(chain);
            var (shots, launch, reports) = Watch(w, 1200, InterceptClass.Drone);
            Assert.True(launch > 0);
            var r = reports[0];
            Assert.Equal(InterceptClass.Drone, r.Class);
            Assert.Equal(far.Id, r.Detector); // the far antenna reports it either way
            Assert.Equal(chain, r.Linked); // cut off from the truck it only reports
            var own = shots.Where(s => s.P.Shooter == patriot.Id).ToList();
            return own.Count == 0 ? -1 : (int)own[0].Tick - launch;
        }
        var warned = FirstShot(true);
        var unwarned = FirstShot(false);
        // Warned, it fires once the drone is in its 280 cells (about 120 cells after the launch: 220 ticks); unwarned, only once the
        // radar truck tracks it (60 cells from its point: 588 ticks).
        Assert.True(warned > 0, "the warned launcher never fired");
        Assert.True(unwarned - warned >= 300, $"warned {warned} ticks after the launch, unwarned {unwarned}");
    }

    [Fact]
    public void AnUnlinkedAntennaReportsButTheLauncherIsNotWarned()
    {
        var (w, patriot, far, _) = Raid(chain: false);
        Watch(w, 200, InterceptClass.Drone);
        var r = Assert.Single(w.LaunchReports);
        Assert.False(r.Linked);
        Assert.False(w.NetworkLinked(far));
        Assert.Empty(w.WarnedLaunchers(r));
        Assert.Empty(w.LauncherWarnings(patriot));
        Assert.Equal(NetworkWarning.None, w.WarningFor(patriot, r));
    }

    [Fact]
    public void TheClientReadsTheWarnedLaunchers()
    {
        var (w, patriot, _, _) = Raid(chain: true);
        Watch(w, 200, InterceptClass.Drone);
        var r = Assert.Single(w.LaunchReports);
        Assert.Equal([patriot], w.WarnedLaunchers(r));
        Assert.Equal([r], w.LauncherWarnings(patriot));
        Assert.Equal(NetworkWarning.Engage, w.WarningFor(patriot, r));
    }

    [Fact]
    public void TheReportedLauncherIsSeenThroughTheFogForTenSeconds()
    {
        var w = Arena.NewSentinel(500, 1, s => s with { Fog = true });
        var a = Antenna(w, 200, Y); // its own sight is 50 cells: the truck, 200 cells east, is far out of it
        var truck = Crewed(w, 2, "drone_truck", 400, Y, Facing.West);
        Order(w, truck, CommandKind.AttackGround, new Cell(150, Y));
        LaunchReport? r = null;
        for (var i = 0; i < 300 && r is null; i++)
        {
            Assert.False(World.IsSpotted(truck, 1));
            w.Step();
            r = w.LaunchReports.FirstOrDefault();
        }
        Assert.NotNull(r);
        Assert.Equal(a.Id, r.Detector);
        Assert.False(r.Linked); // no radar: it reports all the same
        w.Step();
        Assert.True(World.IsSpotted(truck, 1), "the reported launcher is seen");
        while (w.Tick < r.LauncherUntil - 1)
        {
            w.Step();
        }
        Assert.True(World.IsSpotted(truck, 1));
        w.Run(2);
        Assert.False(World.IsSpotted(truck, 1), "seen only for 10 s after the report");
    }

    [Fact]
    public void TheRadarTruckReportsLaunchesOnlySetUp()
    {
        foreach (var setUp in new[] { false, true })
        {
            var w = Arena.NewSentinel(400);
            var radar = Crewed(w, 1, "radar_truck", 200, 100);
            if (setUp)
            {
                Deploy(w, radar);
            }
            var truck = Crewed(w, 2, "missile_truck", 200, 300); // 200 cells from the radar truck: inside its 360
            Order(w, truck, CommandKind.AttackGround, new Cell(200, 205));
            var (_, launch, reports) = Watch(w, 500, InterceptClass.Missile);
            Assert.True(launch > 0);
            if (!setUp)
            {
                Assert.Empty(reports);
                continue;
            }
            var r = reports[0];
            Assert.Equal((InterceptClass.Missile, radar.Id, true, truck.Id), (r.Class, r.Detector, r.Linked, r.Launcher));
        }
    }

    // ---- Drones ----

    [Fact]
    public void AnAntennaSeesDronesThroughTheFogAndTracksThemForItsSide()
    {
        var w = Arena.NewSentinel(500, 1, s => s with { Fog = true });
        var a = Antenna(w, 100, Y);
        var near = w.Spawn(2, "scout_drone", 200, Y); // 100 cells out (its 250 m height is 125 cells: about 160 cells of slant)
        var close = w.Spawn(2, "scout_drone", 160, Y); // 60 cells out: about 138 of slant
        var farAway = w.Spawn(2, "scout_drone", 300, Y);
        w.Run(6);
        Assert.True(World.IsSpotted(close, 1), "a drone in its reach is seen through the fog");
        Assert.False(World.IsSpotted(farAway, 1));
        Assert.True(w.AirTracked(1, close.Id));
        Assert.False(w.AirTracked(1, farAway.Id));
        Assert.Contains(close.Id, w.AntennaDrones(a));
        Assert.DoesNotContain(farAway.Id, w.AntennaDrones(a));
        Assert.Equal(World.IsSpotted(near, 1), w.AntennaDrones(a).Contains(near.Id)); // the same rule both ways

        // A one-way attack drone (a projectile) in its reach is tracked for its side.
        var truck = Crewed(w, 2, "drone_truck", 230, Y + 40, Facing.West);
        Order(w, truck, CommandKind.AttackGround, new Cell(60, Y + 40));
        var seen = false;
        for (var i = 0; i < 600 && !seen; i++)
        {
            w.Step();
            seen = w.Projectiles.Any(p => p.Interceptible == InterceptClass.Drone && w.AirTracked(1, p.Id) && w.AntennaDrones(a).Contains(p.Id));
        }
        Assert.True(seen, "the antenna tracked the drone");
    }

    // ---- Warning rules b and c ----

    [Fact]
    public void AWarnedLauncherLetsADroneAimedAtNothingGo()
    {
        // A drone from the south aimed 60 cells north of the battery: it flies right over the launcher. Launched within the radar
        // truck's 360 cells it is reported (warned): its predicted point lies 60 cells from anything of the Sentinel's, so it is
        // not engaged. Launched from 420 cells (no report) the old rule holds: a drone is engaged whatever its point.
        foreach (var warned in new[] { true, false })
        {
            var w = Arena.NewSentinel(800);
            var patriot = Crewed(w, 1, "patriot_launcher", 400, 300);
            var radar = Crewed(w, 1, "radar_truck", 410, 300);
            var truck = Crewed(w, 2, "drone_truck", 400, warned ? 600 : 720);
            Deploy(w, patriot, radar);
            Order(w, truck, CommandKind.AttackGround, new Cell(400, 240));
            var (shots, launch, reports) = Watch(w, 300, InterceptClass.Drone);
            Assert.True(launch > 0);
            if (warned)
            {
                var r = reports[0];
                Assert.True(r.Linked);
                Assert.Equal(Fixed.FromInt(60), r.Nearest);
                Assert.Equal(NetworkWarning.Harmless, w.WarningFor(patriot, r));
            }
            var (more, _, later) = Watch(w, 1000, InterceptClass.Drone);
            shots.AddRange(more);
            reports.AddRange(later);
            if (warned)
            {
                Assert.DoesNotContain(shots, s => s.P.Shooter == patriot.Id);
            }
            else
            {
                Assert.Empty(reports);
                Assert.Contains(shots, s => s.P.Shooter == patriot.Id);
            }
        }
    }

    [Fact]
    public void TheThaadTypeLeavesATwinMissileToThePatriotTypeUnlessNoneCanTakeIt()
    {
        foreach (var withPatriot in new[] { true, false })
        {
            var w = Arena.NewSentinel(500);
            var radar = Crewed(w, 1, "radar_truck", 210, 200);
            var thaad = Crewed(w, 1, "thaad_launcher", 190, 200);
            var setUp = new List<Unit> { radar, thaad };
            Unit? patriot = null;
            if (withPatriot)
            {
                patriot = Crewed(w, 1, "patriot_launcher", 200, 200);
                setUp.Add(patriot);
            }
            Deploy(w, [.. setUp]);
            var truck = Crewed(w, 2, "missile_truck", 200, 320);
            Order(w, truck, CommandKind.AttackGround, new Cell(200, 202));
            LaunchReport? report = null;
            var shots = new List<(uint, Projectile)>();
            for (var i = 0; i < 900; i++)
            {
                var (s, _, _) = Watch(w, 1, InterceptClass.Missile);
                shots.AddRange(s.Where(x => shots.All(y => y.Item2.Id != x.P.Id)));
                if (report is null && w.LaunchReports.FirstOrDefault() is { } r0)
                {
                    report = r0;
                    if (patriot is not null)
                    {
                        Assert.Equal(NetworkWarning.LeftToOthers, w.WarningFor(thaad, r0));
                        Assert.Equal(NetworkWarning.Engage, w.WarningFor(patriot, r0));
                    }
                    else
                    {
                        Assert.Equal(NetworkWarning.Engage, w.WarningFor(thaad, r0));
                    }
                }
            }
            Assert.NotNull(report);
            Assert.True(report.Linked);
            var high = shots.Where(s => s.Item2.Shooter == thaad.Id).ToList();
            if (withPatriot)
            {
                Assert.Empty(high);
                Assert.Contains(shots, s => s.Item2.Shooter == patriot!.Id);
            }
            else
            {
                Assert.NotEmpty(high);
            }
        }
    }

    [Fact]
    public void TheThaadTypeStillTakesABallisticMissileBesideAPatriotType()
    {
        var w = Arena.NewSentinel(500);
        var radar = Crewed(w, 1, "radar_truck", 210, 200);
        var thaad = Crewed(w, 1, "thaad_launcher", 190, 200);
        var patriot = Crewed(w, 1, "patriot_launcher", 200, 200);
        Deploy(w, radar, thaad, patriot);
        var truck = Crewed(w, 2, "ballistic_truck", 200, 350);
        Order(w, truck, CommandKind.AttackGround, new Cell(200, 202));
        var (shots, launch, reports) = Watch(w, 700, InterceptClass.Ballistic);
        Assert.True(launch > 0);
        Assert.True(reports[0].Linked);
        Assert.Contains(shots, s => s.P.Shooter == thaad.Id);
    }

    // ---- Cruise missiles ----

    [Fact]
    public void ACruiseMissilesPredictedPointIsItsNextWaypointRefreshedWhileSeen()
    {
        var w = Arena.NewSentinel(600);
        var radar = Crewed(w, 1, "radar_truck", 300, 300); // within 120 cells of the whole second leg: it sees it low
        Deploy(w, radar);
        var truck = Crewed(w, 2, "cruise_truck", 100, 300, Facing.East);
        Deploy(w, truck);
        var wp = new Cell(300, 250);
        var target = new Cell(400, 300);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Units = [truck.Id], Cell = target, Route = [wp] });
        LaunchReport? r = null;
        for (var i = 0; i < 200 && r is null; i++)
        {
            w.Step();
            r = w.LaunchReports.FirstOrDefault();
        }
        Assert.NotNull(r);
        Assert.Equal(InterceptClass.Cruise, r.Class);
        Assert.Equal(wp.Center(), r.Predicted); // the route's next point, not its target
        var refreshed = false;
        for (var i = 0; i < 400 && !refreshed && w.ProjectileById(r.Threat) is not null; i++)
        {
            w.Step();
            refreshed = r.Predicted == target.Center();
        }
        Assert.True(refreshed, "past the waypoint, seen, its predicted point moved on to the target");
    }

    [Fact]
    public void ACruiseMissileIsNotLetGoOnAWaypointInAnEmptyField()
    {
        // The antenna network client's bug (ANc, 2026-10-31): a reported cruise missile's predicted point is its next waypoint, so
        // rule b judged it on that point: a first waypoint in an empty field made it Harmless and the Patriot type let it go, and it
        // turned and struck the battery unopposed. Now rule b waits for its last leg: on a waypoint it is engaged as any threat.
        // Player 2's truck at (50, 300) sends it through (250, 150), 190 cells from the Patriot type and far from anything of the
        // Sentinel's, on to (445, 150) beside the launcher (a 37-degree turn). The radar truck at (50, 500) reports the launch (200
        // cells off, within its 360) but never sees the missile (beyond its 144 cells at the boost, 120 low), so the predicted
        // point stays on the waypoint; the launcher, warned (rule a), sees it itself within 120 cells.
        var w = Arena.NewSentinel(600);
        var patriot = Crewed(w, 1, "patriot_launcher", 440, 160);
        var radar = Crewed(w, 1, "radar_truck", 50, 500);
        var truck = Crewed(w, 2, "cruise_truck", 50, 300, Facing.East);
        Deploy(w, patriot, radar, truck);
        var wp = new Cell(250, 150);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Units = [truck.Id], Cell = new Cell(445, 150), Route = [wp] });
        var (shots, launch, reports) = Watch(w, 60, InterceptClass.Cruise);
        Assert.True(launch > 0);
        var r = Assert.Single(reports);
        Assert.True(r.Linked);
        Assert.Equal(wp.Center(), r.Predicted);
        Assert.True(r.Nearest > Fixed.FromInt(150)); // nothing of the Sentinel's near the waypoint: before the fix, Harmless
        Assert.Equal(NetworkWarning.Engage, w.WarningFor(patriot, r));
        var (more, _, _) = Watch(w, 700, InterceptClass.Cruise);
        shots.AddRange(more);
        Assert.Equal(wp.Center(), r.Predicted); // never seen by an air sensor: never refreshed
        Assert.Contains(shots, s => s.P.Shooter == patriot.Id); // engaged (the only threat)
    }

    [Fact]
    public void ACruiseMissileOnItsLastLegAtAnEmptyFieldIsStillLetGo()
    {
        // Rule b still holds once the predicted point is the target: straight at an empty field 250 cells from anything, Harmless.
        var w = Arena.NewSentinel(600);
        var patriot = Crewed(w, 1, "patriot_launcher", 440, 160);
        var radar = Crewed(w, 1, "radar_truck", 50, 500);
        var truck = Crewed(w, 2, "cruise_truck", 50, 300, Facing.East);
        Deploy(w, patriot, radar, truck);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Units = [truck.Id], Cell = new Cell(300, 300) });
        var (_, launch, reports) = Watch(w, 60, InterceptClass.Cruise);
        Assert.True(launch > 0);
        var r = Assert.Single(reports);
        Assert.Equal(new Cell(300, 300).Center(), r.Predicted);
        Assert.Equal(NetworkWarning.Harmless, w.WarningFor(patriot, r));
    }

    // ---- Determinism ----

    [Fact]
    public void TheSameRaidTwiceGivesTheSameWorld()
    {
        ulong Play()
        {
            var (w, _, _, _) = Raid(chain: true, seed: 5);
            w.Run(900);
            return w.Hash();
        }
        Assert.Equal(Play(), Play());
    }
}
