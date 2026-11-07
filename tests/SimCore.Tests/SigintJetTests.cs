using System.Diagnostics;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The SIGINT jet (an RC-135 type; docs/design.md, "SIGINT jet"; ListenSystem.cs): it flies a left-hand circle anywhere and hears enemy
// radars that are switched on within 1,000 cells, on a pulse every 2 s: exact place and type for its side, the unit spotted and
// nothing round it. What stops being heard stays a last-seen mark for 60 s. Decoys emit nothing. An S-400 type battery brings it down.
public class SigintJetTests(ITestOutputHelper output)
{
    private const string Jet = "sigint_jet";
    private const int Pulse = 40;
    private const int Fade = 1200;

    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static long Cells(int n) => n * Fixed.OneRaw;

    // A w x h open world: player 1 Sentinel, player 2 Resistance, or both Sentinel with mirror. No armies.
    private static World Open(int w = 400, int h = 200, ulong seed = 1, bool mirror = false)
    {
        var setup = TestContent.Sentinel(seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        if (mirror)
        {
            setup = setup with { Players = [setup.Players[0], setup.Players[0] with { Id = 2 }] };
        }
        return new World(new Grid(w, h), MatchCompiler.Compile(TestContent.Mvp, setup));
    }

    // A flat world with fog, size x size, on terrain: the same lobby.
    private static World Fogged(int size, bool mirror = false)
    {
        var w = Arena.NewSentinel(size, edit: s => mirror
            ? s with { Fog = true, Players = [s.Players[0], s.Players[0] with { Id = 2 }] }
            : s with { Fog = true });
        w.AttachTerrain(new TerrainMap(size, size, new int[(size + 1) * (size + 1)], new byte[size * size], Rules));
        return w;
    }

    // Player p's jet in the air over (x, y), sent to orbit there.
    private static Unit Flying(World w, int x, int y, byte p = 1)
    {
        var j = w.Spawn(p, Jet, x, y, Facing.East);
        w.Order(p, CommandKind.Move, [j], cell: new Cell(x, y));
        return j;
    }

    private static void SetUp(World w, Unit u)
    {
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = Stance.Guard });
        w.Run(2);
    }

    private static void PackUp(World w, Unit u)
    {
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = Stance.Hide });
        w.Step();
    }

    // Runs to just before the next pulse's step, with the world's tick on it: the next Step pulses.
    private static void ToPulse(World w)
    {
        while (w.Tick % Pulse != 0)
        {
            w.Step();
        }
    }

    // Runs through the next pulse.
    private static void Pulses(World w, int n = 1)
    {
        for (var i = 0; i < n; i++)
        {
            ToPulse(w);
            w.Step();
        }
    }

    // Puts the jet (raw) off radar r along x, exactly: nothing moves it between now and the next pulse's check.
    private static void Hold(World w, Unit jet, Unit r, long dx)
    {
        ToPulse(w);
        jet.Pos = new Vec(Fixed.FromRaw(r.Pos.X.Raw + dx), r.Pos.Y);
    }

    private static RadarContact Only(World w, byte player = 1) => Assert.Single(w.RadarContacts(player));

    // ---- Content ----

    [Fact]
    public void ItIsData()
    {
        var c = TestContent.Mvp;
        var w = Open();
        var d = w.Player(1)!.Setup.Find(c, Jet)!;
        var a = d.Archetype;
        Assert.Equal("SIGINT jet", a.Name);
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        Assert.False(c.Faction("resistance")!.Archetypes[a.Index]);
        Assert.Equal((3000, 1800, 900), (d.Cost, a.BuildTime, d.MaxHp));
        Assert.Equal(AircraftClass.XL, a.Aircraft!.SizeClass);
        var gun = c.Archetype("gunship")!;
        Assert.Equal(gun.Flight!.Wing!.Cruise, a.Flight!.Wing!.Cruise);
        Assert.InRange(a.Flight.Wing.Cruise, Cells(12) - 20, Cells(12)); // 12 cells/s, to a whole raw a tick
        Assert.Equal(360 * 512L, a.Flight.HeightRaw);
        Assert.Equal(gun.Flight.HeightRaw, a.Flight.HeightRaw);
        Assert.Equal(c.Archetype("overseer_drone")!.Lifetime, a.Lifetime);
        // It sees nothing, fires nothing and carries nothing against a missile.
        Assert.Equal((0, 0, 0), (d.Vision.Raw, a.AirVision.Raw, a.GroundRadar.Raw));
        Assert.Empty(d.Weapons);
        Assert.Empty(a.Countermeasures);
        Assert.Null(a.Evade);
        Assert.Null(a.Gunship);
        Assert.Equal(new Listen(Cells(1000), Pulse, Fade, a.Listen!.ExcludeTags), a.Listen);
        // Air class airlifter: S-400 3 hits, the air defence vehicle 4 (a hit is a share of max HP: the gunship's class takes 8).
        Assert.Equal(InterceptClass.Airlifter, a.Flight.Class);
        Interceptor Ic(string unit) => w.Player(2)!.Setup.Find(c, unit)!.Weapons.Single(x => x.Effect.Interceptor is not null).Effect.Interceptor!;
        Assert.Equal(35, Ic("s400_launcher").HitDamageOn(a.Flight.Class));
        Assert.Equal(25, Ic("sam_vehicle").HitDamageOn(a.Flight.Class));
        Assert.Equal(13, Ic("sam_vehicle").HitDamageOn(gun.Flight.Class));
    }

    [Fact]
    public void ARadarIsASensorThatIsNoSoldiersOrAircraftsEyes()
    {
        var c = TestContent.Mvp;
        var listen = c.Archetype(Jet)!.Listen!;
        var radars = c.Archetypes.Where(a => (a.AirVision.Raw != 0 || a.GroundRadar.Raw != 0) && !a.Tags.Matches(listen.ExcludeTags)).Select(a => a.Id).Order();
        Assert.Equal(["airbase_radar", "mobile_radar", "radar_site", "radar_station", "radar_truck", "s400_radar", "sam_vehicle"], radars);
        // Decoys have no sensor at all, the launchers of a battery neither.
        Assert.All(c.Archetypes.Where(a => a.Disguise >= 0), a => Assert.Equal((0, 0), (a.AirVision.Raw, a.GroundRadar.Raw)));
        foreach (var id in new[] { "s400_launcher", "kub_launcher", "patriot_launcher", "thaad_launcher", "antenna_tower" })
        {
            Assert.Equal((0, 0), (c.Archetype(id)!.AirVision.Raw, c.Archetype(id)!.GroundRadar.Raw));
        }
    }

    // ---- The emitter rule ----

    [Theory]
    [InlineData("s400_radar", false)]
    [InlineData("mobile_radar", false)]
    [InlineData("radar_truck", true)]
    public void ARadarIsHeardOnlyWithItsRadarOn(string id, bool mirror)
    {
        var w = Open(mirror: mirror);
        var r = w.SpawnCrewed(2, id, 300, 100).Vehicle;
        PackUp(w, r);
        Flying(w, 100, 100);
        Pulses(w, 2);
        Assert.Empty(w.RadarContacts(1)); // switched off
        Assert.False(w.RadarSearching(r));
        SetUp(w, r);
        Pulses(w);
        var c = Only(w);
        Assert.True(c.Live);
        Assert.Equal(r.Id, c.Unit);
        Assert.Equal(id, c.Type);
        Assert.Equal(r.Pos, c.Pos);
        Assert.True(World.IsSpotted(r, 1));
        PackUp(w, r);
        Pulses(w);
        c = Only(w);
        Assert.False(c.Live);
        Assert.Equal(0u, c.Unit);
        Assert.Equal(id, c.Type);
    }

    [Fact]
    public void TheHideStanceSwitchesAnS400TypeRadarOff()
    {
        var w = Open();
        var r = w.SpawnCrewed(2, "s400_radar", 300, 100).Vehicle;
        Flying(w, 100, 100);
        SetUp(w, r);
        Pulses(w);
        Assert.True(Only(w).Live);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [r.Id], Stance = Stance.Hide });
        w.Step();
        Pulses(w);
        Assert.False(Only(w).Live);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [r.Id], Stance = Stance.Guard });
        w.Step();
        Pulses(w);
        Assert.True(Only(w).Live);
    }

    [Theory]
    [InlineData("radar_station")]
    [InlineData("radar_site")]
    public void ARadarBuildingIsHeardWhileItStandsAndNotAsRubble(string id)
    {
        var w = Open(mirror: true);
        var r = w.Spawn(2, id, 300, 100);
        Flying(w, 100, 100);
        Pulses(w);
        Assert.True(Only(w).Live);
        Assert.Equal(id, Only(w).Type);
        r.HP = 0; // brought down: rubble while it has a rubble block, else gone
        w.Step();
        Pulses(w);
        Assert.Equal(id != "radar_station", r.Alive && r.Rubble); // the station has no rubble stage: it is gone
        var c = Only(w);
        Assert.False(c.Live);
        Assert.Equal(0u, c.Unit);
        if (id != "radar_station")
        {
            // Repaired, it is heard again.
            (r.Rubble, r.HP) = (false, r.Def.MaxHp);
            Pulses(w);
            Assert.True(Only(w).Live);
        }
    }

    [Fact]
    public void TheBaseRadarIsHeardWhileItStandsAndNotAsRubble()
    {
        var w = Open(800, 400, mirror: true);
        var core = w.Player(2)!.Setup.Find(w.Content, "airbase_core")!;
        Assert.NotNull(w.SpawnAirBase(core, new Cell(500, 120), 0, []));
        var r = Assert.Single(w.All("airbase_radar"));
        Flying(w, 100, 100);
        Pulses(w);
        Assert.Equal(("airbase_radar", r.Id), (Only(w).Type, Only(w).Unit));
        r.HP = 0;
        w.Step();
        Pulses(w);
        Assert.True(r.Alive && r.Rubble);
        Assert.False(Only(w).Live);
    }

    [Fact]
    public void TheAirDefenceVehicleHasNoSwitchItIsHeardWhileItIsOut()
    {
        var w = Open();
        var r = w.SpawnCrewed(2, "sam_vehicle", 300, 100).Vehicle;
        Flying(w, 100, 100);
        Pulses(w);
        Assert.Equal(r.Id, Only(w).Unit);
    }

    [Fact]
    public void SoldiersEyesAndOtherAircraftAreNotHeard()
    {
        var w = Open(mirror: true);
        w.Spawn(2, "manpads", 300, 100);
        w.Spawn(2, "Multirole 500 lb", 300, 60, Facing.East); // an air vision (the pilot's eyes)
        w.Spawn(2, "attack_helicopter", 300, 140); // a mast radar, ground vehicles only
        Flying(w, 100, 100);
        Pulses(w, 3);
        Assert.Empty(w.RadarContacts(1));
    }

    [Fact]
    public void DecoysAreNeverHeardARealOneIs()
    {
        var w = Open();
        var decoys = w.Content.Archetypes.Where(a => a.Disguise >= 0).Select((a, i) => w.Spawn(2, a.Id, 300, 40 + 15 * i)).ToList();
        Assert.Contains(decoys, d => d.Def.Archetype.Id == "decoy_mobile_radar");
        Flying(w, 100, 100);
        Pulses(w, 3);
        Assert.Empty(w.RadarContacts(1));
        Assert.All(decoys, d => Assert.Equal(0u, d.SpottedBy & (1u << 1)));
        // The real mobile radar beside them, set up: only that one.
        var real = w.SpawnCrewed(2, "mobile_radar", 320, 100).Vehicle;
        SetUp(w, real);
        Pulses(w);
        Assert.Equal(real.Id, Only(w).Unit);
    }

    // ---- The range edge ----

    [Fact]
    public void ItHearsToExactlyAThousandCellsOfLevelDistance()
    {
        var w = Open(1400, 200);
        var r = w.Spawn(2, "sam_vehicle", 1300, 100);
        var j = w.Spawn(1, Jet, 100, 100, Facing.East);
        w.Run(2);
        Hold(w, j, r, -(Cells(1000) + 1));
        w.Step();
        Assert.Empty(w.RadarContacts(1));
        Hold(w, j, r, -Cells(1000));
        w.Step();
        Assert.Equal(r.Id, Only(w).Unit);
        // The height does not count: at 360 m the slant is over a thousand; on the map the ring is what it hears.
        Assert.True(j.Altitude.Raw > 0);
        // Beyond it again: last-seen.
        Hold(w, j, r, -Cells(1001));
        w.Step();
        Assert.False(Only(w).Live);
    }

    // ---- The pulse ----

    [Fact]
    public void AMarkComesOnlyOnThePulse()
    {
        var w = Open();
        Flying(w, 100, 100);
        Pulses(w);
        // A radar that comes on just after a pulse waits for the next one: no mark for 39 ticks.
        var r = w.Spawn(2, "sam_vehicle", 300, 100);
        var born = w.Tick;
        var first = uint.MaxValue;
        for (var t = 0; t < 2 * Pulse && first == uint.MaxValue; t++)
        {
            w.Step();
            if (w.RadarContacts(1).Count > 0)
            {
                first = w.Tick - 1;
            }
        }
        Assert.Equal(0u, first % Pulse);
        Assert.InRange(first - born, 1u, (uint)Pulse);
        Assert.Equal(0, Only(w).Age);
        Assert.Equal(Fade, Only(w).Fade);
        // It stays live, and spotted, every tick between pulses.
        for (var t = 0; t < 3 * Pulse; t++)
        {
            w.Step();
            var c = Only(w);
            Assert.True(c.Live);
            Assert.InRange(c.Age, 0, Pulse - 1);
            Assert.True(World.IsSpotted(r, 1));
        }
    }

    // ---- Last seen ----

    [Fact]
    public void ALastSeenMarkHoldsItsPlaceAndFadesSixtySecondsAfterItWasLastHeard()
    {
        var w = Open();
        var r = w.SpawnCrewed(2, "mobile_radar", 300, 100).Vehicle;
        var j = Flying(w, 100, 100);
        SetUp(w, r);
        Pulses(w);
        var at = r.Pos;
        PackUp(w, r);
        ToPulse(w);
        var lastHeard = w.Tick - Pulse;
        w.Step();
        var c = Only(w);
        Assert.False(c.Live);
        Assert.Equal(at, c.Pos);
        Assert.Equal(Pulse, c.Age);
        Assert.Equal(Fade - Pulse, c.Fade);
        // It drives off: the mark stays where it was heard, and it is no longer spotted.
        w.Order(2, CommandKind.Move, [r], cell: new Cell(330, 150));
        w.Run(200);
        Assert.NotEqual(at, r.Pos);
        Assert.Equal(at, Only(w).Pos);
        Assert.Equal(0u, r.SpottedBy & (1u << 1));
        // Gone 60 s after it was last heard, not before.
        while (w.Tick - 1 < lastHeard + Fade - 1)
        {
            Assert.Equal((int)(lastHeard + Fade - (w.Tick - 1)), Only(w).Fade);
            w.Step();
        }
        Assert.Equal(1, Only(w).Fade);
        w.Step();
        Assert.Empty(w.RadarContacts(1));
        Assert.True(j.Alive);
    }

    [Fact]
    public void HeardAgainBeforeItFadesItIsLiveWhereItIsNow()
    {
        var w = Open();
        var r = w.SpawnCrewed(2, "mobile_radar", 300, 100).Vehicle;
        Flying(w, 100, 100);
        SetUp(w, r);
        Pulses(w);
        var was = r.Pos;
        PackUp(w, r);
        w.Order(2, CommandKind.Move, [r], cell: new Cell(330, 150));
        w.Run(500); // it has arrived: the contact below reads where it stands
        Assert.False(r.Moving);
        SetUp(w, r);
        Pulses(w);
        var c = Only(w);
        Assert.True(c.Live);
        Assert.Equal(r.Pos, c.Pos);
        Assert.NotEqual(was, c.Pos);
        Assert.Equal(Fade, c.Fade);
    }

    [Fact]
    public void ARadarThatDiesAndAJetThatGoesLeaveAMarkToo()
    {
        var w = Open();
        var dead = w.Spawn(2, "sam_vehicle", 300, 100);
        var other = w.Spawn(2, "sam_vehicle", 300, 150);
        var j = Flying(w, 100, 100);
        Pulses(w);
        Assert.Equal(2, w.RadarContacts(1).Count);
        dead.HP = 0;
        w.Step();
        Pulses(w);
        var cs = w.RadarContacts(1);
        Assert.False(cs[0].Live);
        Assert.Equal(other.Id, cs[1].Unit);
        j.HP = 0;
        w.Step();
        Pulses(w);
        Assert.All(w.RadarContacts(1), c => Assert.False(c.Live));
        var checks = w.ListenChecks;
        w.Run(Fade);
        Assert.Empty(w.RadarContacts(1));
        Assert.Equal(checks, w.ListenChecks);
    }

    // ---- Spotting and the fog ----

    [Fact]
    public void ItLiftsNoFogAUnitBesideACaughtRadarStaysHidden()
    {
        var w = Fogged(400);
        var r = w.SpawnCrewed(2, "s400_radar", 300, 300).Vehicle; // an emitter: its own rule shows it only to radars, and this jet has none
        var soldier = w.Spawn(2, "Rifleman", 303, 300);
        var truck = w.SpawnCrewed(2, "cargo_truck", 300, 306).Vehicle;
        var building = w.Spawn(2, "supply_depot", 310, 310);
        var j = Flying(w, 60, 60);
        SetUp(w, r);
        Assert.Empty(w.RadarSpots);
        Pulses(w, 3);
        Assert.Equal(r.Id, Only(w).Unit);
        Assert.Equal(r.Pos, Only(w).Pos);
        Assert.Equal("s400_radar", Only(w).Type);
        Assert.Empty(w.RadarSpots);
        Assert.True(World.IsVisibleTo(r, 1));
        Assert.True(World.IsSpotted(r, 1));
        Assert.False(World.IsVisibleTo(soldier, 1));
        Assert.False(World.IsVisibleTo(truck, 1));
        Assert.False(World.IsVisibleTo(building, 1));
        // Not one cell of player 1's fog is lifted: not the radar's, not under the jet.
        var fog = w.FogOf(1)!;
        Assert.False(fog.Visible(r.Pos.X.Floor(), r.Pos.Y.Floor()));
        Assert.False(fog.Explored(r.Pos.X.Floor(), r.Pos.Y.Floor()));
        Assert.False(fog.Visible(j.Pos.X.Floor(), j.Pos.Y.Floor()));
        Assert.All(fog.ExploredCells.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ALastSeenMarkShowsNothingInTheFog()
    {
        var w = Fogged(400);
        var r = w.SpawnCrewed(2, "mobile_radar", 300, 300).Vehicle;
        Flying(w, 60, 60);
        SetUp(w, r);
        Pulses(w);
        Assert.True(World.IsVisibleTo(r, 1));
        PackUp(w, r);
        Pulses(w);
        w.Step();
        Assert.False(Only(w).Live);
        Assert.False(World.IsVisibleTo(r, 1));
    }

    [Fact]
    public void ACaughtRadarIsTheWholeSidesToAttackAndNoOtherPlayersList()
    {
        var w = Fogged(400, mirror: true);
        var r = w.Spawn(2, "radar_station", 300, 300);
        var rifleman = w.Spawn(1, "Rifleman", 40, 340); // far from anything: he sees nothing of it
        var mine = w.Spawn(1, "radar_station", 40, 40);
        // Before the jet, his side does not see it: a ground unit's attack order is dropped.
        w.Run(2);
        w.Order(1, CommandKind.Attack, [rifleman], target: r.Id);
        w.Step();
        Assert.NotEqual(OrderKind.Attack, rifleman.Order.Kind);
        Flying(w, 60, 60);
        Pulses(w);
        Assert.True(World.IsVisibleTo(r, 1));
        w.Order(1, CommandKind.Attack, [rifleman], target: r.Id);
        w.Step();
        Assert.Equal((OrderKind.Attack, r.Id), (rifleman.Order.Kind, rifleman.Order.Target));
        // Its list is its own: the enemy has none, and viewer 0 none; a jet of its own hears only the other side's.
        Assert.Equal(r.Id, Only(w).Unit);
        Assert.Empty(w.RadarContacts(2));
        Assert.Empty(w.RadarContacts(0));
        Flying(w, 340, 340, 2);
        Pulses(w);
        Assert.Equal(r.Id, Only(w).Unit);
        Assert.Equal(mine.Id, Only(w, 2).Unit);
        Assert.False(World.IsVisibleTo(rifleman, 2));
    }

    // ---- Who hits it ----

    [Fact]
    public void AnS400TypeBatteryBringsItDownAndItHearsTheBatteryFromOutsideItsReach()
    {
        var w = Open(1200, 200);
        var r = w.SpawnCrewed(2, "s400_radar", 100, 100).Vehicle;
        var l1 = w.SpawnCrewed(2, "s400_launcher", 120, 100).Vehicle;
        var l2 = w.SpawnCrewed(2, "s400_launcher", 120, 120).Vehicle;
        w.Order(2, CommandKind.Deploy, [r, l1, l2]);
        w.Run(602);
        // 900 cells away, out of the missile's 600: heard, not fired at.
        var far = Flying(w, 1000, 100);
        for (var t = 0; t < 600; t++)
        {
            w.Step();
            Assert.DoesNotContain(w.Projectiles, p => p.Shooter == l1.Id || p.Shooter == l2.Id);
        }
        Assert.Equal(r.Id, Only(w).Unit);
        Assert.Equal(far.Def.MaxHp, far.HP);
        // Flown too close: 400 cells. It is down well within a minute and a half.
        var tick = w.Tick;
        w.Order(1, CommandKind.Move, [far], cell: new Cell(500, 100));
        var hits = 0;
        for (var t = 0; t < 4000 && far.Alive; t++)
        {
            var hp = far.HP;
            w.Step();
            hits += far.HP < hp ? 1 : 0;
        }
        output.WriteLine($"down {(w.Tick - tick) / World.TicksPerSecond} s after the order, {hits} hits");
        Assert.False(far.Alive);
        Assert.Equal(KillCause.Missile, far.KilledBy);
        Assert.Equal(3, hits);
    }

    [Fact]
    public void TheAaSoldierCannotReachItsOrbitTheAirDefenceVehicleCanRightUnderIt()
    {
        var w = Open();
        var soldier = w.Spawn(2, "manpads", 100, 100);
        Flying(w, 100, 100);
        w.Run(600);
        Assert.DoesNotContain(w.Projectiles, p => p.Shooter == soldier.Id);
        var sam = w.SpawnCrewed(2, "sam_vehicle", 100, 100).Vehicle;
        var fired = false;
        for (var t = 0; t < 600 && !fired; t++)
        {
            w.Step();
            fired = w.Projectiles.Any(p => p.Shooter == sam.Id);
        }
        Assert.True(fired);
    }

    // ---- Flight ----

    [Fact]
    public void ItFliesALeftHandCircleAnywhereAtTheGunshipsSpeedAndHeight()
    {
        var w = Open(800, 800);
        var j = w.Spawn(1, Jet, 40, 400, Facing.East);
        var centre = new Cell(400, 400); // the middle of the map
        w.Order(1, CommandKind.Move, [j], cell: centre);
        w.Run(2000); // about 44 s there at 12 cells/s: 2 settled laps left
        var c = centre.Center();
        var radii = new List<long>();
        for (var t = 0; t < 800; t++)
        {
            w.Step();
            radii.Add(World.Len3(j.Pos.X.Raw - c.X.Raw, j.Pos.Y.Raw - c.Y.Raw, 0));
            long rx = j.Pos.X.Raw - c.X.Raw, ry = j.Pos.Y.Raw - c.Y.Raw;
            var v = j.Wing!;
            Assert.True(rx * v.VelY - ry * v.VelX < 0, "counter-clockwise on the map: the centre on its left");
        }
        output.WriteLine($"radius {radii.Min() / (double)Fixed.OneRaw:F1} to {radii.Max() / (double)Fixed.OneRaw:F1} cells");
        Assert.All(radii, r => Assert.InRange(r, Cells(27), Cells(33)));
        Assert.Equal(360 * 512L + 5 * 512L * (j.Id % 5), j.Altitude.Raw);
        Assert.InRange(World.Len3(j.Wing!.VelX, j.Wing.VelY, 0), Cells(11), Cells(13));
    }

    [Fact]
    public void TheHangarBuildsItItFliesOutListensAndGoesHomeWhenLow()
    {
        var w = Open(1200, 400);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var r = w.Spawn(2, "sam_vehicle", 700, 200);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [w.Unit(af.Hangar)!.Id], Unit = Jet });
        Unit? j = null;
        for (var t = 0; t < 2000 && j is null; t++)
        {
            w.Step();
            j = w.Units.FirstOrDefault(u => u.Def.Archetype.Id == Jet);
        }
        Assert.NotNull(j);
        Assert.InRange((int)w.Tick, 1800, 1900); // 90 s
        Assert.Equal(FlightPhase.Parked, j!.AirOps!.Phase);
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(s => s.Stations).Single(s => s.Unit == j.AirOps.Station).Kind);
        // Parked within range it hears nothing: it costs nothing.
        Pulses(w, 2);
        Assert.Empty(w.RadarContacts(1));
        Assert.Equal(0, w.ListenChecks);
        w.Order(1, CommandKind.Move, [j], cell: new Cell(300, 200));
        for (var t = 0; t < 4000 && j.AirOps is not null; t++)
        {
            w.Step();
        }
        Assert.Null(j.AirOps);
        w.Run(1800);
        Assert.Equal(r.Id, Only(w).Unit);
        // Low on fuel: home on its own.
        j.Lifetime = 800;
        var homing = false;
        for (var t = 0; t < 400 && !homing; t++)
        {
            w.Step();
            homing = j.Homing;
        }
        Assert.True(homing);
        // It lands, parks, and listens no more; what it heard fades.
        for (var t = 0; t < 4000 && j.AirOps?.Phase != FlightPhase.Parked; t++)
        {
            w.Step();
        }
        Assert.Equal(FlightPhase.Parked, j.AirOps?.Phase);
        var c = w.ListenChecks;
        w.Run(2 * Pulse);
        Assert.Equal(c, w.ListenChecks);
        Assert.All(w.RadarContacts(1), x => Assert.False(x.Live));
        // Refuelled, it takes off and goes back to its circle on its own (the E2 rule).
        w.Run(3000);
        Assert.Null(j.AirOps);
        Assert.Equal(new Cell(300, 200).Center(), j.OrbitAt);
        Assert.InRange(World.Len3(j.Pos.X.Raw - j.OrbitAt.X.Raw, j.Pos.Y.Raw - j.OrbitAt.Y.Raw, 0), 0, Cells(40));
    }

    // ---- The cost ----

    [Fact]
    public void WithNoJetUpAPulseCostsNothing()
    {
        var w = Open();
        for (var i = 0; i < 20; i++)
        {
            w.Spawn(2, "sam_vehicle", 200 + 8 * i, 100);
        }
        var plain = w.Hash();
        Assert.NotEqual(0UL, plain);
        Pulses(w, 3);
        Assert.Equal(0, w.ListenChecks);
    }

    [Fact]
    public void APulseIsRadarsTimesJetsChecksAtMost()
    {
        const int radars = 200, jets = 8;
        var w = Open(1600, 1600);
        for (var i = 0; i < radars; i++)
        {
            w.Spawn(2, "sam_vehicle", 1500 - 4 * (i % 50), 1500 - 4 * (i / 50));
        }
        // 2,000 other units that are no radars: a pulse must not walk them.
        for (var i = 0; i < 2000; i++)
        {
            w.Spawn(2, "Rifleman", 40 + i % 100, 1000 + i / 100);
        }
        for (var i = 0; i < jets; i++)
        {
            Flying(w, 100 + 20 * i, 100); // all out of range: the worst case, every check is made
        }
        Pulses(w);
        var before = w.ListenChecks;
        Pulses(w);
        Assert.Equal(radars * jets, w.ListenChecks - before);
        var sw = new Stopwatch();
        const int runs = 2000;
        ToPulse(w);
        sw.Start();
        for (var i = 0; i < runs; i++)
        {
            w.RunListening();
        }
        sw.Stop();
        var outOfRange = sw.Elapsed.TotalMicroseconds / runs;
        // In range: all 200 heard by one side: fewer checks, a contact each.
        foreach (var j in w.All(Jet))
        {
            j.Pos = new Vec(Fixed.FromInt(1300), Fixed.FromInt(1300));
        }
        w.RunListening();
        Assert.Equal(radars, w.RadarContacts(1).Count);
        sw.Restart();
        for (var i = 0; i < runs; i++)
        {
            w.RunListening();
        }
        sw.Stop();
        output.WriteLine($"a pulse, {radars} radars x {jets} jets, {w.Units.Count} units: all out of range {outOfRange:F1} us; all in range, {radars} contacts {sw.Elapsed.TotalMicroseconds / runs:F1} us");
    }

    // ---- Determinism ----

    private static ulong[] Script(bool reading)
    {
        var w = Fogged(600, mirror: true);
        var a = w.SpawnCrewed(2, "radar_truck", 300, 300);
        var b = w.SpawnCrewed(2, "radar_truck", 340, 300);
        w.Spawn(2, "radar_station", 320, 340);
        Flying(w, 100, 100);
        Flying(w, 500, 100);
        Flying(w, 300, 500, 2);
        w.Spawn(1, "sam_vehicle", 100, 400);
        w.Order(2, CommandKind.Deploy, [a.Vehicle, b.Vehicle]);
        var hashes = new List<ulong>();
        for (var t = 0; t < 1500; t++)
        {
            if (t == 400)
            {
                a.Vehicle.Stance = Stance.Hide;
            }
            w.Step();
            hashes.Add(w.Hash());
            _ = reading ? w.RadarContacts(1) : null; // reading changes nothing
        }
        Assert.Equal(3, w.RadarContacts(1).Count);
        Assert.Single(w.RadarContacts(2));
        return [.. hashes];
    }

    [Fact]
    public void ItIsDeterministicAndHashesItsContacts()
    {
        var a = Script(false);
        Assert.Equal(a, Script(false));
        Assert.Equal(a, Script(true));
        // Contacts are in the hash, both ways of hashing.
        var w = Open();
        w.Spawn(2, "sam_vehicle", 300, 100);
        Flying(w, 100, 100);
        Pulses(w);
        var with = w.Hash();
        Assert.Equal(w.HashReference(), with);
        w.RadarContactStates[0].Until++;
        Assert.NotEqual(with, w.Hash());
        Assert.Equal(w.HashReference(), w.Hash());
    }
}
