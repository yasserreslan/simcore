using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Air defence on the move (owner 2026-11-07; docs/design.md, decisions 974 to 981): every air defence unit works as it drives, and only
// the missile defence launcher and the high-altitude launcher still set up and stand to fire. A radar's way off is the Hide stance.
public sealed class AirDefenseOnTheMoveTests(ITestOutputHelper output)
{
    private const int N = 1000;
    private const int Y = 500;
    private const string Multirole = "Multirole 500 lb";

    // NewWorld: player 1 the Sentinel, player 2 the Resistance; mirror: both the Sentinel (the radar truck under test as player 2's).
    private static World NewWorld(ulong seed = 1, bool mirror = false) => Arena.NewSentinel(N, seed, edit: s =>
        mirror ? s with { Players = [s.Players[0], s.Players[0] with { Id = 2 }] } : s);

    private static World WorldFor(string id) => NewWorld(mirror: id == "radar_truck");

    private static Unit Crewed(World w, string id, int x, int y = Y, byte owner = 2) => w.SpawnCrewed(owner, id, x, y).Vehicle;

    private static void Drive(World w, Unit u, int x, int y = Y) => w.Order(u.Owner, CommandKind.Move, [u], cell: new Cell(x, y));

    private static void Stance(World w, Unit u, Stance s) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = s });

    // Jet: player 1's jet in flight over (x, y) at 170 m, orbiting there.
    private static Unit Jet(World w, string id, int x, int y = Y)
    {
        var j = w.Spawn(1, id, x, y, Facing.West);
        j.Altitude = Fixed.FromRaw(85 * Fixed.OneRaw);
        j.MutableSalvos[0] = 0; // no flares or chaff: what is measured is the launcher
        w.Order(1, CommandKind.Move, [j], cell: new Cell(x, y));
        return j;
    }

    private static IEnumerable<Projectile> Missiles(World w, Unit shooter) =>
        w.Projectiles.Where(p => p.Shooter == shooter.Id && p.Weapon.Effect.Interceptor is not null);

    // The launchers that fire as they drive, each with the radar it needs (null: its own).
    public static TheoryData<string, string?> Drivers => new()
    {
        { "sam_vehicle", null },
        { "kub_launcher", "mobile_radar" },
        { "s400_launcher", "s400_radar" },
    };

    [Theory]
    [InlineData("mobile_radar")]
    [InlineData("s400_radar")]
    [InlineData("radar_truck")]
    public void ARadarSpotsAsItDrives(string id)
    {
        var w = WorldFor(id);
        var r = Crewed(w, id, 200);
        var j = Jet(w, Multirole, 400);
        Drive(w, r, 300);
        w.Run(30);
        Assert.True(r.Moving);
        for (var t = 0; t < 200; t++)
        {
            w.Step();
            Assert.True(w.AirTracked(2, j.Id), $"{id}, tick {t}");
            Assert.Contains(j, w.RadarTracks(r));
            Assert.True(World.RadarOf(r)!.Value.On);
        }
        Assert.True(r.Moving);
        Assert.Equal(DeployState.Stowed, r.Deploy);
    }

    [Fact]
    public void TheRadarTrucksGroundRadarSpotsAsItDrives()
    {
        var w = NewWorld(mirror: true);
        var r = Crewed(w, "radar_truck", 200);
        var enemy = w.SpawnCrewed(1, "humvee", 260, Y).Vehicle;
        Drive(w, r, 300);
        w.Run(20);
        Assert.True(r.Moving);
        Assert.True(w.OwnRadarTrack(r, enemy));
        Assert.True(World.IsSpotted(enemy, 2));
    }

    [Theory]
    [MemberData(nameof(Drivers))]
    public void ALauncherEngagesAsItDrivesAndKeepsDriving(string id, string? radarId)
    {
        var w = NewWorld();
        var l = Crewed(w, id, 200);
        var radar = radarId is null ? null : Crewed(w, radarId, 200, Y - 12);
        Drive(w, l, 600);
        if (radar is not null)
        {
            Drive(w, radar, 600, Y - 12);
        }
        w.Run(30);
        var j = Jet(w, Multirole, 300);
        var startX = l.Pos.X.Raw;
        Projectile? shot = null;
        for (var t = 0; t < 400 && shot is null; t++)
        {
            w.Step();
            shot = Missiles(w, l).FirstOrDefault();
        }
        Assert.NotNull(shot);
        Assert.True(l.Moving, $"{id} stopped to fire");
        Assert.Equal(OrderKind.Move, l.Order.Kind);
        Assert.Equal(j.Id, shot.Target);
        Assert.True(l.Pos.X.Raw > startX, $"{id} did not drive on");
        output.WriteLine($"{id}: fired {(l.Pos.X.Raw - startX) / Fixed.OneRaw} cells down the road");
    }

    [Theory]
    [InlineData("patriot_launcher")]
    [InlineData("thaad_launcher")]
    public void TheMissileDefenceLaunchersRefuseToFireDrivingAndFireSetUp(string id)
    {
        var w = NewWorld();
        var l = Crewed(w, id, 300, owner: 1);
        Crewed(w, "radar_truck", 300, Y - 14, owner: 1);
        Assert.True(l.Def.Archetype.DeployTicks > 0);
        Drive(w, l, 800);
        w.Run(90);
        Assert.True(l.Moving);
        var (shot, away) = id == "thaad_launcher" ? ("ballistic_truck", 150) : ("missile_truck", 90); // what each engages (PatriotTests)
        var at = Cell.Of(l.Pos);
        var truck = w.SpawnCrewed(2, shot, at.X, at.Y + away, Facing.North).Vehicle;
        w.Order(2, CommandKind.AttackGround, [truck], cell: new Cell(at.X, at.Y + 2));
        for (var t = 0; t < 400; t++)
        {
            w.Step();
            Assert.Empty(Missiles(w, l));
            Assert.NotEqual(DeployState.Deployed, l.Deploy);
        }
        Assert.True(l.Moving);

        // Stopped and set up, it does: the same launcher, the same radar, a new shot.
        w.Order(1, CommandKind.Deploy, [l]);
        w.Run(l.Def.Archetype.DeployTicks + 4);
        Assert.Equal(DeployState.Deployed, l.Deploy);
        at = Cell.Of(l.Pos);
        var second = w.SpawnCrewed(2, shot, at.X, at.Y + away, Facing.North).Vehicle;
        w.Order(2, CommandKind.AttackGround, [second], cell: new Cell(at.X, at.Y + 2));
        var fired = false;
        for (var t = 0; t < 1500 && !fired; t++)
        {
            w.Step();
            fired = Missiles(w, l).Any();
        }
        Assert.True(fired, $"{id} never fired set up");
    }

    [Theory]
    [InlineData("sam_vehicle", "mobile_radar", "kub_launcher", "s400_radar", "s400_launcher", "radar_truck")]
    public void ADeployOrPackUpOrderToAUnitWithNoSetUpIsHarmless(params string[] ids)
    {
        var w = NewWorld();
        var units = ids.Select((id, i) => Crewed(w, id, 200 + (i * 30), owner: id == "radar_truck" ? (byte)1 : (byte)2)).ToArray();
        void OrderAll(CommandKind kind)
        {
            foreach (var g in units.GroupBy(u => u.Owner))
            {
                w.Order(g.Key, kind, [.. g]);
            }
        }
        foreach (var kind in new[] { CommandKind.Deploy, CommandKind.Stow })
        {
            OrderAll(kind);
            w.Step();
            Assert.Empty(w.OrderRefusals);
            w.Run(5);
        }
        foreach (var u in units)
        {
            Assert.Equal((DeployState.Stowed, 0), (u.Deploy, u.DeployLeft));
            Assert.Equal(OrderKind.Idle, u.Order.Kind);
        }
        // Not stuck: they drive straight off, on a Deploy order given on the way too.
        foreach (var u in units)
        {
            Drive(w, u, Cell.Of(u.Pos).X, Y + 60);
        }
        w.Run(20);
        OrderAll(CommandKind.Deploy);
        w.Run(20);
        Assert.All(units, u => Assert.True(u.Moving, u.Def.Archetype.Id));
    }

    [Fact]
    public void ARadarDrivingGuidesTheMissileOfALauncherItFeeds()
    {
        var w = NewWorld();
        var r = Crewed(w, "mobile_radar", 200, Y - 10);
        var k = Crewed(w, "kub_launcher", 200);
        Drive(w, r, 800, Y - 10);
        w.Run(30);
        var j = Jet(w, Multirole, 400);
        Projectile? m = null;
        for (var t = 0; t < 600 && m is null; t++)
        {
            w.Step();
            m = Missiles(w, k).FirstOrDefault();
        }
        Assert.NotNull(m);
        Assert.Equal(r, w.BatteryRadar(k));
        AirBurst? burst = null;
        for (var t = 0; t < 800 && burst is null; t++)
        {
            Assert.True(r.Moving, "the radar stays on the road");
            w.Step();
            Assert.DoesNotContain(m.Id, w.BatteryLost);
            burst = w.AirBursts.FirstOrDefault(b => b.Missile == m.Id) is { Missile: > 0 } b ? b : null;
        }
        Assert.NotNull(burst);
        Assert.True(burst.Value.Kind is AirBurstKind.Kill or AirBurstKind.Miss or AirBurstKind.Hit, $"{burst.Value.Kind}: guided to the end");
        Assert.True(j.Alive || burst.Value.Kind != AirBurstKind.Miss);
    }

    [Fact]
    public void ATruckRadarDrivingFeedsAPatriotSetUp()
    {
        // Coordinator default 975: the radar truck feeds the missile defence launchers as it drives.
        var w = NewWorld();
        var l = Crewed(w, "patriot_launcher", 300, owner: 1);
        var truck = Crewed(w, "radar_truck", 290, Y - 14, owner: 1);
        w.Order(1, CommandKind.Deploy, [l]);
        w.Run(l.Def.Archetype.DeployTicks + 4);
        Drive(w, truck, 800, Y - 14);
        w.Run(40);
        Assert.True(truck.Moving);
        var t2 = w.SpawnCrewed(2, "missile_truck", 390, Y).Vehicle;
        w.Order(2, CommandKind.AttackGround, [t2], cell: new Cell(302, Y));
        var fired = false;
        for (var t = 0; t < 1200 && !fired; t++)
        {
            w.Step();
            fired = Missiles(w, l).Any();
        }
        Assert.True(fired);
    }

    [Theory]
    [InlineData("mobile_radar")]
    [InlineData("s400_radar")]
    [InlineData("radar_truck")]
    public void TheSigintJetHearsARadarAsItDrives(string id)
    {
        var w = WorldFor(id);
        var r = Crewed(w, id, 300);
        var jet = w.Spawn(1, "sigint_jet", 100, Y, Facing.East);
        jet.Altitude = Fixed.FromRaw(200 * Fixed.OneRaw);
        w.Order(1, CommandKind.Move, [jet], cell: new Cell(100, Y));
        Drive(w, r, 800);
        var pulse = jet.Def.Archetype.Listen!.PulseTicks;
        w.Run(pulse + 30);
        Assert.True(r.Moving);
        var first = Assert.Single(w.RadarContacts(1));
        Assert.True(first.Live);
        Assert.True(World.IsSpotted(r, 1));
        var was = first.Pos;
        w.Run((pulse * 2) + 1);
        var then = Assert.Single(w.RadarContacts(1));
        Assert.True(then.Live);
        Assert.True(then.Pos.X.Raw > was.X.Raw, "the contact follows it down the road");
    }

    [Theory]
    [InlineData("mobile_radar")]
    [InlineData("s400_radar")]
    [InlineData("radar_truck")]
    public void SwitchedOffAsItDrivesItIsNotHeardAndHasNoPicture(string id)
    {
        var w = WorldFor(id);
        var r = Crewed(w, id, 300);
        var air = Jet(w, Multirole, 500);
        var ears = w.Spawn(1, "sigint_jet", 100, Y, Facing.East);
        ears.Altitude = Fixed.FromRaw(200 * Fixed.OneRaw);
        w.Order(1, CommandKind.Move, [ears], cell: new Cell(100, Y));
        Drive(w, r, 800);
        var pulse = ears.Def.Archetype.Listen!.PulseTicks;
        w.Run(pulse + 30);
        Assert.True(Assert.Single(w.RadarContacts(1)).Live);
        Assert.True(w.AirTracked(2, air.Id));

        Stance(w, r, SimCore.Sim.Stance.Hide);
        w.Run(pulse + 2);
        Assert.True(r.Moving, "the stance does not stop it");
        Assert.False(Assert.Single(w.RadarContacts(1)).Live);
        Assert.False(w.AirTracked(2, air.Id));
        Assert.Empty(w.RadarTracks(r));
        Assert.False(World.RadarOf(r)!.Value.On);
        Assert.False(w.RadarSearching(r));

        Stance(w, r, SimCore.Sim.Stance.Guard);
        w.Run(pulse + 2);
        Assert.True(Assert.Single(w.RadarContacts(1)).Live);
        Assert.True(w.AirTracked(2, air.Id));
    }

    // No setup does not mean a free radar: with no crew aboard it is off, as a radar that never set up was (decision 981).
    [Theory]
    [InlineData("mobile_radar")]
    [InlineData("s400_radar")]
    [InlineData("radar_truck")]
    public void AnEmptyRadarIsOff(string id)
    {
        var w = WorldFor(id);
        var r = w.Spawn(2, id, 300, Y);
        var air = Jet(w, Multirole, 400);
        var ears = w.Spawn(1, "sigint_jet", 100, Y, Facing.East);
        ears.Altitude = Fixed.FromRaw(200 * Fixed.OneRaw);
        w.Order(1, CommandKind.Move, [ears], cell: new Cell(100, Y));
        w.Run(ears.Def.Archetype.Listen!.PulseTicks + 2);
        Assert.Equal(0u, r.Driver);
        Assert.False(World.RadarOf(r)!.Value.On);
        Assert.False(w.AirTracked(2, air.Id));
        Assert.Empty(w.RadarContacts(1));
    }

    [Fact]
    public void TheRadarTrucksGroundPictureGoesOffToo()
    {
        var w = NewWorld(mirror: true); // no eye of its side near the enemy: only the ground radar's 80 cells reach it
        var r = Crewed(w, "radar_truck", 200);
        var enemy = w.SpawnCrewed(1, "humvee", 270, Y).Vehicle;
        w.Run(3);
        Assert.True(w.OwnRadarTrack(r, enemy));
        Stance(w, r, SimCore.Sim.Stance.Hide);
        w.Run(3);
        Assert.False(w.OwnRadarTrack(r, enemy));
    }

    [Fact]
    public void ASwitchedOffRadarFeedsNoLauncherAndAStanceOnALauncherLeavesItsOwnRule()
    {
        var w = NewWorld();
        var r = Crewed(w, "mobile_radar", 200, Y - 10);
        var k = Crewed(w, "kub_launcher", 200);
        Stance(w, r, SimCore.Sim.Stance.Hide);
        Drive(w, k, 600);
        w.Run(10);
        Jet(w, Multirole, 300);
        for (var t = 0; t < 300; t++)
        {
            w.Step();
            Assert.Empty(Missiles(w, k));
        }
        // Hold fire on a driving launcher still holds its fire.
        Stance(w, r, SimCore.Sim.Stance.Guard);
        Stance(w, k, SimCore.Sim.Stance.HoldFire);
        for (var t = 0; t < 200; t++)
        {
            w.Step();
            Assert.Empty(Missiles(w, k));
        }
    }

    // A soldier's missile and a vehicle's ground gun are not air defence vehicles: they stand to engage, as before.
    [Theory]
    [InlineData("manpads")]
    [InlineData("technical")]
    public void AWalkingSoldierAndADrivingGunStillDoNotEngage(string id)
    {
        var w = NewWorld();
        var u = id == "manpads" ? w.Spawn(2, id, 200, Y) : w.SpawnCrewed(2, id, 200, Y).Vehicle;
        Drive(w, u, 800);
        w.Run(20);
        var j = Jet(w, Multirole, 280);
        j.Altitude = Fixed.FromRaw(15 * Fixed.OneRaw); // under a gun's ceiling
        for (var t = 0; t < 150; t++)
        {
            w.Step();
            Assert.True(u.Moving);
            Assert.Equal(0u, u.AirTarget);
            Assert.DoesNotContain(w.Projectiles, p => p.Shooter == u.Id);
        }
    }

    [Fact]
    public void ALauncherWithNoSetUpStillNeverChases()
    {
        foreach (var id in new[] { "sam_vehicle", "kub_launcher", "s400_launcher", "mobile_radar", "s400_radar", "radar_truck" })
        {
            Assert.False(TestContent.Mvp.Archetype(id)!.Pursue, id);
        }
        var w = NewWorld();
        var k = Crewed(w, "kub_launcher", 200);
        var at = k.Pos;
        var attacker = w.SpawnCrewed(1, "humvee", 225, Y).Vehicle;
        w.Run(300);
        Assert.True(attacker.Alive || !k.Alive);
        Assert.Equal(at, k.Pos);
    }

    // The two launchers the owner named set up; nothing else in the air defence does.
    [Fact]
    public void OnlyTheMissileDefenceLaunchersSetUp()
    {
        var c = TestContent.Mvp;
        foreach (var id in new[] { "sam_vehicle", "mobile_radar", "kub_launcher", "s400_radar", "s400_launcher", "radar_truck" })
        {
            Assert.Equal((0, 0), (c.Archetype(id)!.DeployTicks, c.Archetype(id)!.StowTicks));
        }
        Assert.Equal((400, 300), (c.Archetype("patriot_launcher")!.DeployTicks, c.Archetype("patriot_launcher")!.StowTicks));
        Assert.Equal((500, 400), (c.Archetype("thaad_launcher")!.DeployTicks, c.Archetype("thaad_launcher")!.StowTicks));
        foreach (var id in new[] { "mobile_radar", "s400_radar", "radar_truck" })
        {
            Assert.True(c.Archetype(id)!.RadarSwitch, id);
        }
    }

    [Fact]
    public void BadRadarSwitchContentIsRefused()
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Name.EndsWith("archetypes/vehicles.json", StringComparison.Ordinal));
        const string From = "\"radius\": 9, \"turnRate\": 2,";
        Assert.Contains(From, sources[i].Json, StringComparison.Ordinal);
        var bad = sources[i].Json.Replace("\"id\": \"kub_launcher\",", "\"id\": \"kub_launcher\", \"radarSwitch\": true,", StringComparison.Ordinal);
        sources[i] = new Content.ContentSource(sources[i].Name, bad);
        var e = Assert.ThrowsAny<Exception>(() => Content.ContentLoader.Load(sources));
        Assert.Contains("radarSwitch needs an airVision or a groundRadar", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameDriveTwiceGivesTheSameHashes()
    {
        static List<ulong> Run()
        {
            var w = NewWorld(7);
            var r = Crewed(w, "mobile_radar", 200, Y - 10);
            var k = Crewed(w, "kub_launcher", 200);
            var osa = Crewed(w, "sam_vehicle", 200, Y + 10);
            w.Order(2, CommandKind.Move, [r, k, osa], cell: new Cell(700, Y));
            Jet(w, Multirole, 450);
            var hashes = new List<ulong>();
            for (var t = 0; t < 800; t++)
            {
                w.Step();
                hashes.Add(w.Hash());
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }

    // The number for the report: a launcher driving against the same launcher stopped, the same jets in the same places, each seed one
    // missile. Performance is unchanged by the rule, so they should sit close together: the engine gives no penalty and no bonus.
    [Theory]
    [InlineData("sam_vehicle", null)]
    [InlineData("kub_launcher", "mobile_radar")]
    [InlineData("s400_launcher", "s400_radar")]
    public void DrivingOrStoppedTheKillChanceIsTheSameRule(string id, string? radarId)
    {
        const int Seeds = 30; // decision 981 quotes the same run at 200
        var (stopKills, stopTicks, stopPm) = Measure(id, radarId, Seeds, drive: false);
        var (moveKills, moveTicks, movePm) = Measure(id, radarId, Seeds, drive: true);
        output.WriteLine($"{id}: stopped {stopKills}/{Seeds} kills, chance {stopPm} per mille, first shot tick {stopTicks}; " +
            $"driving {moveKills}/{Seeds} kills, chance {movePm} per mille, first shot tick {moveTicks}");
        Assert.InRange(movePm, stopPm - 60, stopPm + 60); // the same kill rule: no penalty, no bonus
    }

    private static (int Kills, int FirstShotTick, int KillPermille) Measure(string id, string? radarId, int seeds, bool drive)
    {
        int kills = 0, ticks = 0;
        long pm = 0;
        for (var s = 1; s <= seeds; s++)
        {
            var w = NewWorld((ulong)s);
            var l = Crewed(w, id, 200);
            var radar = radarId is null ? null : Crewed(w, radarId, 200, Y - 12);
            if (drive)
            {
                Drive(w, l, 800);
                if (radar is not null)
                {
                    Drive(w, radar, 800, Y - 12);
                }
            }
            w.Run(30);
            var j = Jet(w, Multirole, 300);
            var start = w.Tick;
            Projectile? m = null;
            for (var t = 0; t < 400 && m is null; t++)
            {
                w.Step();
                m = Missiles(w, l).FirstOrDefault();
            }
            if (m is null)
            {
                continue;
            }
            ticks = (int)(w.Tick - start);
            for (var t = 0; t < 800; t++)
            {
                w.Step();
                if (w.AirBursts.FirstOrDefault(b => b.Missile == m.Id) is { Missile: > 0 } b)
                {
                    kills += b.Kind == AirBurstKind.Kill ? 1 : 0;
                    pm += b.KillPermille;
                    break;
                }
            }
        }
        return (kills, ticks, (int)(pm / seeds));
    }
}
