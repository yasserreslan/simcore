using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The Resistance's S-400 type battery (s400_radar, s400_launcher; docs/design.md, "S-400 type air defence"): a radar vehicle that
// searches 800 cells set up, sees stealth better (counterStealth 50), cannot see low targets far out behind the ground (its radar
// horizon) and gives itself away while it searches (emitter), and launchers that fire only on its track within 100 cells (the
// SA-6's battery link) with semi-active missiles. Player 1 is the Sentinel, player 2 the Resistance.
public class S400Tests(ITestOutputHelper output)
{
    private const int W = 1000;
    private const int H = 300;
    private const int Y = 150;
    private const int RX = 50; // where the radar stands
    private const string Multirole = "Multirole 500 lb";

    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    // A W x H open world from the Sentinel lobby (player 1 Sentinel, player 2 Resistance), no armies.
    private static World NewWorld(ulong seed = 1, int w = W, int h = H)
    {
        var setup = TestContent.Sentinel(seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(w, h), MatchCompiler.Compile(TestContent.Mvp, setup));
    }

    private static void SetUp(World w, params Unit[] units)
    {
        w.Order(units[0].Owner, CommandKind.Deploy, units);
        w.Run(units.Max(u => u.Def.Archetype.DeployTicks) + 2);
        Assert.All(units, u => Assert.Equal(DeployState.Deployed, u.Deploy));
    }

    private static Unit Radar(World w, int x = RX, int y = Y) => w.SpawnCrewed(2, "s400_radar", x, y).Vehicle;

    private static Unit Launcher(World w, int x, int y = Y) => w.SpawnCrewed(2, "s400_launcher", x, y).Vehicle;

    // Air: player 1's aircraft id flying over (x, y), ordered to orbit there; metres > 0 puts it at that height.
    private static Unit Air(World w, string id, int x, int y = Y, int metres = 0)
    {
        var j = w.Spawn(1, id, x, y, Facing.East);
        if (metres > 0)
        {
            j.Altitude = Fixed.FromRaw(metres * 512L);
        }
        w.Order(1, CommandKind.Move, [j], cell: new Cell(x, y));
        return j;
    }

    private static long Slant(World w, Unit radar, Unit t) =>
        World.Len3(t.Pos.X.Raw - radar.Pos.X.Raw, t.Pos.Y.Raw - radar.Pos.Y.Raw, t.Altitude.Raw - w.RadarEyeRaw(radar));

    // Place puts flying unit t at slant `slant` (raw) from radar's antenna along +x at row y, keeping its height.
    private static void Place(World w, Unit radar, Unit t, long slant, int y)
    {
        var dz = t.Altitude.Raw - w.RadarEyeRaw(radar);
        long dy = (y - Y) * (long)Fixed.OneRaw;
        var lx = Fixed.Sqrt(Math.Max(0, slant * slant - dz * dz - dy * dy));
        t.Pos = new Vec(Fixed.FromRaw(radar.Pos.X.Raw + lx), Fixed.FromRaw(radar.Pos.Y.Raw + dy));
    }

    private static IEnumerable<Projectile> Missiles(World w, Unit shooter) =>
        w.Projectiles.Where(p => p.Shooter == shooter.Id && p.Weapon.Effect.Interceptor is not null);

    private static Projectile? FirstShot(World w, Unit shooter, int ticks)
    {
        for (var t = 0; t < ticks; t++)
        {
            w.Step();
            if (Missiles(w, shooter).FirstOrDefault() is { } m)
            {
                return m;
            }
        }
        return null;
    }

    // ---- Content ----

    [Fact]
    public void ItIsData()
    {
        var c = TestContent.Mvp;
        var r = c.Archetype("s400_radar")!;
        var l = c.Archetype("s400_launcher")!;
        foreach (var a in new[] { r, l })
        {
            Assert.True(c.Faction("resistance")!.Archetypes[a.Index]);
            Assert.False(c.Faction("sentinel")!.Archetypes[a.Index]);
            Assert.True(a.Tags.Matches(c.Archetype("vehicle_garage")!.Production!.Tags), "built at the vehicle garage");
            Assert.Equal((600, 400), (a.DeployTicks, a.StowTicks)); // 30 s / 20 s
            Assert.Equal(25 * Fixed.OneRaw / 200, a.Speed.Raw); // 2.5 cells/s
        }
        Assert.Equal((320, 1200), (r.MaxHp, r.BuildTime)); // 60 s
        Assert.Equal((340, 1100), (l.MaxHp, l.BuildTime)); // 55 s
        Assert.Equal(Fixed.FromInt(800), r.AirVision);
        Assert.Equal(250 * 512L / 10, r.HeightRaw); // the antenna 25 m up
        Assert.Equal((50, 70, 80), (r.CounterStealth, r.CueLock, r.SweepTicks));
        Assert.Equal(new RadarHorizon(Fixed.FromInt(200).Raw, 50 * 512L), r.RadarHorizon);
        Assert.Equal(Fixed.FromInt(800).Raw, r.Emitter!.RangeRaw);
        Assert.Equal((Fixed.FromInt(2), 200), (r.Emitter.Radius, r.Emitter.LingerTicks));
        Assert.Equal(0, l.AirVision.Raw); // no radar of its own

        var w = NewWorld();
        Assert.Equal(3000, w.Player(2)!.Setup.Find(w.Content, "s400_radar")!.Cost);
        var ld = w.Player(2)!.Setup.Find(w.Content, "s400_launcher")!;
        Assert.Equal(2500, ld.Cost); // missiles included
        var wd = ld.Weapons[0];
        var ic = wd.Effect.Interceptor!;
        Assert.Equal((4, 60), (wd.Effect.Ammo, wd.Reload)); // 4 missiles, one every 3 s
        Assert.Equal((Fixed.FromInt(600), Fixed.FromInt(20)), (wd.Range, wd.Effect.MinRange));
        Assert.Equal(100 * Fixed.OneRaw / 20, wd.Effect.ProjectileSpeed.Raw); // 100 cells/s
        Assert.Equal(Fixed.FromInt(700), wd.Effect.Guided!.MaxFlight);
        Assert.Equal(400, wd.Effect.RevealTicks); // 20 s
        Assert.Equal(new DepotReload(600, 20, 400, true), wd.Effect.DepotReload);
        Assert.Equal((60, 20 * 512L), (ic.LockTicks, ic.MinAltitudeRaw)); // 3 s; nothing below 20 m
        var b = ic.Battery!;
        Assert.Equal(Fixed.FromInt(100).Raw, b.LinkRadiusRaw);
        Assert.True(b.SemiActive);
        Assert.True(r.Tags.HasAll(b.RadarTags));
        Assert.False(c.Archetype("mobile_radar")!.Tags.HasAll(b.RadarTags), "the SA-6's radar is no S-400 battery radar");
        var kub = w.Player(2)!.Setup.Find(w.Content, "kub_launcher")!.Weapons[0].Effect.Interceptor!.Battery!;
        Assert.False(r.Tags.HasAll(kub.RadarTags), "the SA-6 launcher does not link to the S-400 radar");
        Assert.Equal(50, w.CounterStealthOf(ld)); // its lock and kill go by its radars' counter-stealth
        // Aircraft only: no drones, missiles or rockets.
        foreach (var cls in new[] { "drone", "scout", "watcher", "overseer", "fpv", "kaman", "ballistic", "missile", "rocket", "hypersonic" })
        {
            Assert.False(ic.Engages(c.AirClass(cls)!.Class), cls);
        }
        foreach (var cls in new[] { "multirole", "fighter", "attacker", "gunship", "bomber", "stealth", "airlifter", "attackheli", "armoredheli", "lightheli" })
        {
            Assert.True(ic.Engages(c.AirClass(cls)!.Class), cls);
        }
        Assert.Equal((2, 2, 1), (ic.SalvoOn(c.AirClass("bomber")!.Class), ic.SalvoOn(c.AirClass("stealth")!.Class), ic.SalvoOn(c.AirClass("multirole")!.Class)));
    }

    [Theory]
    [InlineData("\"counterStealth\": 50", "\"counterStealth\": 101", "counterStealth")]
    [InlineData("\"radarHorizon\": { \"beyond\": 2000, \"below\": 500 }", "\"radarHorizon\": { \"beyond\": 2000, \"below\": 0 }", "radarHorizon.below")]
    [InlineData("\"excludeTags\": [\"infantry\"]", "\"excludeTags\": [\"no_such_tag\"]", "unknown tag")]
    [InlineData("\"emitter\": { \"range\": 8000,", "\"emitter\": { \"range\": 0,", "emitter.range")]
    public void BadRadarContentIsRefused(string from, string to, string error)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Name.EndsWith("archetypes/vehicles.json", StringComparison.Ordinal));
        Assert.Contains(from, sources[i].Json, StringComparison.Ordinal);
        sources[i] = new ContentSource(sources[i].Name, sources[i].Json.Replace(from, to, StringComparison.Ordinal));
        var e = Assert.ThrowsAny<Exception>(() => ContentLoader.Load(sources));
        Assert.Contains(error, e.Message, StringComparison.Ordinal);
    }

    // ---- Seeing ----

    // Every Sentinel aircraft and drone, at its own height, is tracked out to 800 cells x its signature's detect percent
    // (moved halfway to 100), and not beyond.
    [Theory]
    [InlineData(Multirole)]
    [InlineData("fighter_jet")]
    [InlineData("attack_jet")]
    [InlineData("gunship")]
    [InlineData("strategic_bomber")]
    [InlineData("supersonic_bomber")]
    [InlineData("stealth_bomber")]
    [InlineData("stealth_multirole")]
    [InlineData("airlifter")]
    [InlineData("attack_helicopter")]
    [InlineData("armored_helicopter")]
    [InlineData("light_helicopter")]
    [InlineData("watcher_drone")]
    [InlineData("overseer_drone")]
    public void EachSentinelAircraftIsDetectedAtRange(string id)
    {
        var w = NewWorld();
        var r = Radar(w);
        SetUp(w, r);
        var near = Air(w, id, 400, Y - 60);
        var far = Air(w, id, 400, Y + 60);
        w.Run(2);
        var seen = w.SignatureSeenBy(r, near);
        var reach = r.Def.Archetype.AirVision.Raw * seen.Detect / 100;
        Place(w, r, near, reach * 96 / 100, Y - 60);
        Place(w, r, far, reach * 104 / 100, Y + 60);
        w.Run(1);
        output.WriteLine($"{id}: detect {near.Signature.Detect} -> {seen.Detect}, reach {reach / Fixed.OneRaw} cells; near {Slant(w, r, near) / Fixed.OneRaw}, far {Slant(w, r, far) / Fixed.OneRaw}, height {near.Altitude.Raw / 512} m");
        Assert.True(Slant(w, r, near) < reach && Slant(w, r, far) > reach, "the premise: one inside its reach, one outside");
        Assert.True(w.AirTracked(2, near.Id), "tracked inside its reach");
        Assert.False(w.AirTracked(2, far.Id), "not beyond");
        Assert.True(reach >= Fixed.FromInt(400).Raw, "every Sentinel aircraft is seen out to 400 cells at least");
    }

    [Fact]
    public void TheStealthBomberIsSeenAtAboutFiveHundredCells()
    {
        var w = NewWorld();
        var r = Radar(w);
        var mobile = w.SpawnCrewed(2, "mobile_radar", RX, Y + 5).Vehicle;
        SetUp(w, r, mobile);
        var b = Air(w, "stealth_bomber", 400);
        w.Run(1);
        Assert.Equal(new Signature(25, 300, 70), b.Signature);
        Assert.Equal(new Signature(62, 200, 85), w.SignatureSeenBy(r, b)); // each value halfway to 100
        Assert.Equal(b.Signature, w.SignatureSeenBy(mobile, b)); // other sensors keep the normal signature
        foreach (var (cells, seen) in new[] { (480, true), (490, true), (505, false), (520, false) })
        {
            Place(w, r, b, Fixed.FromInt(cells).Raw, Y);
            w.Run(1);
            output.WriteLine($"{Slant(w, r, b) / Fixed.OneRaw} cells of slant, {b.Altitude.Raw / 512} m up: tracked {w.AirTracked(2, b.Id)}");
            Assert.Equal(seen, w.AirTracked(2, b.Id));
            Assert.Equal(seen, w.RadarTracks(r).Contains(b));
        }
        // Without the counter-stealth it would be seen out to 200 cells: at 700 m (350 cells) up, never.
        Assert.Empty(w.RadarTracks(mobile));
    }

    [Fact]
    public void TheLauncherLocksTheStealthBomberByTheCounteredSignature()
    {
        var w = NewWorld();
        var r = Radar(w);
        var l = Launcher(w, RX + 20);
        SetUp(w, r, l);
        var b = Air(w, "stealth_bomber", 400);
        b.MutableSalvos[0] = 0; // no flares
        w.Run(1);
        Place(w, r, b, Fixed.FromInt(450).Raw, Y);
        var m = FirstShot(w, l, 400);
        Assert.NotNull(m);
        // 3 s x lock 200 (300 moved halfway to 100) x the radar's cue 70% = 84 ticks (4.2 s).
        Assert.Equal(84, m.Tracked);
        output.WriteLine($"launched at {m.LaunchRange.Raw / Fixed.OneRaw} cells after {m.Tracked} ticks of lock");
    }

    // A ridge across x = ridgeX, `height` metres, half `half` cells either side, on an n x n map.
    private static TerrainMap Ridge(int n, int ridgeX, int half, int height)
    {
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                var d = Math.Abs(x - ridgeX);
                corners[y * (n + 1) + x] = d >= half ? 0 : (int)((long)height * 512 * (half - d) / half);
            }
        }
        return new TerrainMap(n, n, corners, new byte[n * n], Rules);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALowHelicopterBehindTheGroundIsUnseenBeyondTwoHundredCells(bool ridge)
    {
        // The radar at x 50, a 40 m ridge at x 100: a helicopter 30 m up behind it at 150 cells is seen (inside the horizon's
        // 200), at 250 cells it is not; a jet high over it is; the mobile radar, without a horizon, sees the far helicopter too.
        var w = NewWorld(w: 400, h: 400);
        if (ridge)
        {
            w.AttachTerrain(Ridge(400, 100, 10, 40));
        }
        var r = Radar(w, RX, 200);
        var mobile = w.SpawnCrewed(2, "mobile_radar", RX, 205).Vehicle;
        SetUp(w, r, mobile);
        var nearHeli = Air(w, "attack_helicopter", RX + 150, 190);
        var farHeli = Air(w, "attack_helicopter", RX + 250, 210);
        var jet = Air(w, Multirole, RX + 250, 230, metres: 200);
        var minAbove = long.MaxValue;
        var maxAbove = 0L;
        for (var t = 0; t < 60; t++)
        {
            w.Step();
            foreach (var h in new[] { nearHeli, farHeli })
            {
                var above = h.Altitude.Raw - w.GroundHeight(h.Pos).Raw;
                (minAbove, maxAbove) = (Math.Min(minAbove, above), Math.Max(maxAbove, above));
            }
            if (t < 10)
            {
                continue;
            }
            Assert.True(w.RadarTracks(r).Contains(nearHeli), $"tick {t}: within 200 cells the ground never hides it");
            Assert.Equal(!ridge, w.RadarTracks(r).Contains(farHeli));
            Assert.Equal(ridge, w.BelowRadarHorizon(r, farHeli));
            Assert.False(w.BelowRadarHorizon(r, nearHeli));
            Assert.True(w.RadarTracks(r).Contains(jet), "high, it is always seen");
            Assert.True(w.RadarTracks(mobile).Contains(farHeli), "other radars: ridges never hide air targets");
        }
        output.WriteLine($"helicopters {minAbove / 512} to {maxAbove / 512} m over the ground");
        Assert.True(maxAbove < 50 * 512L, "the premise: below 50 m");
    }

    // ---- Firing ----

    [Fact]
    public void ALauncherFiresOnlyWithALiveSetUpRadarWithinOneHundredCells()
    {
        var w = NewWorld();
        var r = Radar(w);
        var far = Radar(w, RX + 110, Y + 40); // set up, but 110+ cells from the launcher
        var mobile = w.SpawnCrewed(2, "mobile_radar", RX + 15, Y - 10).Vehicle; // the SA-6's radar: no S-400 battery radar
        var l = Launcher(w, RX, Y + 40);
        SetUp(w, l, far, mobile);
        var j = Air(w, Multirole, 400, metres: 200);
        Assert.Null(FirstShot(w, l, 300));
        Assert.True(w.AirTracked(2, j.Id), "premise: its side tracks the jet");
        Assert.Null(w.BatteryRadar(l));
        Assert.Equal(4, l.Charges[0]);

        SetUp(w, r);
        Assert.Equal(r, w.BatteryRadar(l));
        Assert.Equal([l], w.BatteryLaunchers(r));
        Assert.NotNull(FirstShot(w, l, 300));
        Assert.True(w.RevealedBy(l, 1), "each launch shows it to the enemy");
        Assert.InRange(w.RevealLeft(l), 380, 400);
    }

    [Fact]
    public void TheHideStanceBlindsTheBattery()
    {
        var w = NewWorld();
        var r = Radar(w);
        var l = Launcher(w, RX + 20);
        SetUp(w, r, l);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [r.Id], Stance = Stance.Hide });
        w.Step();
        var j = Air(w, Multirole, 400, metres: 200);
        Assert.Null(FirstShot(w, l, 300));
        Assert.False(w.RadarSearching(r));
        Assert.Equal(-1, w.RadarSweep(r));
        Assert.False(w.AirTracked(2, j.Id));
        Assert.Null(w.BatteryRadar(l));
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [r.Id], Stance = Stance.Guard });
        Assert.NotNull(FirstShot(w, l, 300));
    }

    [Theory]
    [InlineData("destroy")]
    [InlineData("stow")]
    public void MissilesMissWhenTheRadarDiesOrPacksUp(string mode)
    {
        var w = NewWorld();
        var r = Radar(w);
        var l = Launcher(w, RX + 20);
        SetUp(w, r, l);
        var j = Air(w, Multirole, 450, metres: 200);
        var m = FirstShot(w, l, 300);
        Assert.NotNull(m);
        var lostAt = -1;
        AirBurst? burst = null;
        for (var t = 0; t < 400 && burst is null; t++)
        {
            if (t == 10)
            {
                if (mode == "destroy")
                {
                    r.HP = 0;
                }
                else
                {
                    w.Order(2, CommandKind.Stow, [r]);
                }
            }
            w.Step();
            if (w.BatteryLost.Contains(m.Id))
            {
                lostAt = t;
            }
            burst = w.AirBursts.FirstOrDefault(b => b.Missile == m.Id) is { Missile: > 0 } b ? b : null;
        }
        Assert.Equal(10, lostAt);
        Assert.False(m.Guided);
        Assert.NotNull(burst);
        Assert.True(burst.Value.Kind is AirBurstKind.SelfDestruct or AirBurstKind.Ground, $"{burst.Value.Kind}: it cannot fuse");
        Assert.True(j.Alive);
    }

    [Fact]
    public void TwoMissilesFlyAtABomberAndOneAtAJet()
    {
        var w = NewWorld();
        var r = Radar(w);
        var l1 = Launcher(w, RX + 20, Y - 20);
        var l2 = Launcher(w, RX + 20, Y + 20);
        SetUp(w, r, l1, l2);
        var b = Air(w, "strategic_bomber", 450, Y - 40);
        b.MutableSalvos[0] = 0;
        var j = Air(w, Multirole, 450, Y + 40, metres: 200);
        var maxAtB = 0;
        var maxAtJ = 0;
        for (var t = 0; t < 300; t++)
        {
            w.Step();
            var live = w.Projectiles.Where(p => p.Weapon.Effect.Interceptor is not null && p.Guided && !p.Done).ToList();
            maxAtB = Math.Max(maxAtB, live.Count(p => p.Target == b.Id));
            maxAtJ = Math.Max(maxAtJ, live.Count(p => p.Target == j.Id));
        }
        Assert.Equal((2, 1), (maxAtB, maxAtJ));
    }

    [Fact]
    public void ABomberLosesThirtyFivePercentAHitAndAJetDiesToOne()
    {
        var w = NewWorld();
        var ld = w.Player(2)!.Setup.Find(w.Content, "s400_launcher")!;
        var ic = ld.Weapons[0].Effect.Interceptor!;
        var c = w.Content;
        Assert.Equal(35, ic.HitDamageOn(c.AirClass("bomber")!.Class));
        Assert.Equal(35, ic.HitDamageOn(c.AirClass("stealth")!.Class));
        Assert.Equal(100, ic.HitDamageOn(c.AirClass("multirole")!.Class));
        Assert.Equal(100, ic.HitDamageOn(c.AirClass("fighter")!.Class));
        Assert.Equal((85, 70), (ic.Best(c.AirClass("bomber")!.Class), ic.Best(c.AirClass("attackheli")!.Class)));
    }

    // ---- Giving itself away ----

    [Fact]
    public void TheEmitterRevealsTheSearchingRadar()
    {
        var w = NewWorld();
        var r = Radar(w);
        var truck = w.SpawnCrewed(1, "radar_truck", RX + 700, Y).Vehicle; // an enemy radar 700 cells off: picks it up
        var farTruck = w.SpawnCrewed(1, "radar_truck", RX + 860, Y + 50).Vehicle; // past 800 cells: does not
        var humvee = w.SpawnCrewed(1, "humvee", RX + 100, Y + 30).Vehicle; // no radar: does not
        w.Order(1, CommandKind.Deploy, [truck, farTruck]);
        w.Run(Math.Max(truck.Def.Archetype.DeployTicks, 1) + 2);
        Assert.Empty(w.RadarSpots); // packed up it emits nothing
        SetUp(w, r);
        w.Run(r.Def.Archetype.SweepTicks); // a sweep
        var s = w.RadarSpotOf(r.Id, 1);
        Assert.NotNull(s);
        Assert.Equal(truck.Id, s.Spotter);
        Assert.Equal(Fixed.FromInt(2), s.Radius);
        Assert.Single(w.RadarSpots);
        Assert.True((r.SpottedBy & 2u) != 0, "spotted for the Sentinel");
        Assert.True(w.RadarSearching(r));
        _ = humvee;

        // Packed up (or hidden), the reveal lingers 10 s after the last sweep, then ends.
        var until = s.Until;
        Assert.True(until - w.Tick <= 200 && until > w.Tick);
        w.Order(2, CommandKind.Stow, [r]);
        w.Run(201);
        Assert.Null(w.RadarSpotOf(r.Id, 1));
        output.WriteLine($"revealed until {until}; gone by {w.Tick}");
    }

    [Fact]
    public void AnEnemyJetsRadarPicksItUpAndAnEnemySoldierDoesNot()
    {
        var w = NewWorld();
        var r = Radar(w);
        SetUp(w, r);
        var soldier = w.Spawn(1, "Rifleman", RX + 50, Y);
        w.Run(r.Def.Archetype.SweepTicks + 1);
        Assert.Empty(w.RadarSpots);
        _ = soldier;
        var j = Air(w, Multirole, RX + 600, metres: 200);
        w.Run(r.Def.Archetype.SweepTicks + 1);
        Assert.Equal(j.Id, w.RadarSpotOf(r.Id, 1)!.Spotter);
    }

    // ---- Determinism ----

    [Fact]
    public void Deterministic()
    {
        static (List<ulong> Hashes, int Missiles, int Spots) Play()
        {
            var w = NewWorld(seed: 7);
            var r = Radar(w);
            var l1 = Launcher(w, RX + 20, Y - 20);
            var l2 = Launcher(w, RX + 20, Y + 20);
            SetUp(w, r, l1, l2);
            var truck = w.SpawnCrewed(1, "radar_truck", RX + 600, Y).Vehicle;
            w.Order(1, CommandKind.Deploy, [truck]);
            Air(w, Multirole, 450, Y + 40, metres: 200);
            Air(w, "stealth_bomber", 400, Y - 40);
            var hashes = new List<ulong>();
            var ids = new List<uint>();
            var spots = 0;
            for (var t = 0; t < 900; t++)
            {
                if (t == 400)
                {
                    w.Order(2, CommandKind.Stow, [r]);
                }
                w.Step();
                hashes.Add(w.Hash());
                spots += w.RadarSpots.Count;
                ids.AddRange(w.Projectiles.Where(p => p.Weapon.Effect.Interceptor is not null && !ids.Contains(p.Id)).Select(p => p.Id));
            }
            return (hashes, ids.Count, spots);
        }
        var a = Play();
        var b = Play();
        Assert.True(a.Missiles >= 2 && a.Spots > 0, $"premise: {a.Missiles} missiles, {a.Spots} reveal ticks");
        Assert.Equal((a.Missiles, a.Spots), (b.Missiles, b.Spots));
        Assert.Equal(a.Hashes, b.Hashes);
    }
}
