using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// HB, the helicopter balance (owner 2026-10-29; docs/design.md, "Helicopters"): the AH-64 type (attack_helicopter) is the
// special pick, firing its 10 missiles at its own mast radar's tracks from behind cover (lockAfterLaunch: lofted over the
// crest) in ripples at up to 4 vehicles; the Mi-28 type (armored_helicopter) is the cheap armoured brawler: missiles at 75% of
// the damage and 30% shorter range on command guidance (it holds its line of sight until impact), slower, a shorter sight.
public class HeliBalanceTests(ITestOutputHelper output)
{
    private const string Apache = "attack_helicopter", Havoc = "armored_helicopter";
    private const long Cell1 = Fixed.OneRaw, Metre = 512;

    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    // Player 1 Sentinel, player 2 Resistance, no armies.
    private static World Sentinel(int n, ulong seed = 1, bool fog = false) => Arena.NewSentinel(n, seed, fog ? s => s with { Fog = true } : null);

    // Both players Sentinel (so player 2 may field the Booker), no armies.
    private static World Duel(int n, ulong seed = 1, bool fog = false)
    {
        var setup = TestContent.Sentinel(seed).For(TestContent.Mvp, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        return new World(new Grid(n, n), MatchCompiler.Compile(TestContent.Mvp, setup with { Players = [p0, p1], Fog = fog }));
    }

    private static Unit Fly(World w, string id, int x, int y, byte p = 1)
    {
        var u = w.Spawn(p, id, x, y, Facing.East);
        w.Step();
        return u;
    }

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, uint target = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Target = target });

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static double Metres(long raw) => raw / (double)Metre;

    // A flat map of n cells with a ridge across x = ridgeX: rising `height` metres over `half` cells either side.
    private static TerrainMap Ridge(int n, int ridgeX, int half, int height)
    {
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                var d = Math.Abs(x - ridgeX);
                corners[y * (n + 1) + x] = d >= half ? 0 : (int)((long)height * Metre * (half - d) / half);
            }
        }
        return new TerrainMap(n, n, corners, new byte[n * n], Rules);
    }

    private static WeaponDef Missile(World w, string heli) =>
        w.Player(1)!.Setup.Find(w.Content, heli)!.Weapons.Single(x => x.Effect.Guided is not null);

    // ---- Content ----

    [Fact]
    public void TheBalanceIsData()
    {
        var c = TestContent.Mvp;
        var w = Sentinel(64);
        var ap = c.Archetype(Apache)!;
        var hv = c.Archetype(Havoc)!;
        var (am, hm) = (Missile(w, Apache), Missile(w, Havoc));
        // Costs unchanged (owner): 3,500 / 60 s and 3,000 / 55 s.
        Assert.Equal((3500, 60 * World.TicksPerSecond), (ap.Cost, ap.BuildTime));
        Assert.Equal((3000, 55 * World.TicksPerSecond), (hv.Cost, hv.BuildTime));
        // The Apache type: 10 missiles (2 x 5), its own seeker that locks on after launch, a ripple at up to 4 targets 0.25 s apart.
        Assert.Equal(10, am.Effect.Ammo);
        Assert.Equal((true, true), (am.Effect.Guided!.FireAndForget, am.Effect.Guided.LockAfterLaunch));
        Assert.Equal(new Ripple(4, 5), am.Effect.Ripple);
        // The Mi-28 type: 8 missiles at 75% of the damage, 30% shorter range, command guidance, one target a shot.
        Assert.Equal(8, hm.Effect.Ammo);
        Assert.Equal(am.Damage * 75 / 100, hm.Damage);
        Assert.Equal(am.Range.Raw * 70 / 100, hm.Range.Raw);
        Assert.Equal((false, false, false), (hm.Effect.Guided!.FireAndForget, hm.Effect.Guided.LockAfterLaunch, hm.Effect.Guided.RequiresOperatorStill));
        Assert.Null(hm.Effect.Ripple);
        // Slower and shorter-sighted: cruise 34 m/s, dash 64 m/s, 70 degrees a second, a 12 m/s climb, sight 30 cells.
        var (hr, ar) = (hv.Flight!.Rotor!, ap.Flight!.Rotor!);
        Assert.Equal((170, 320L * Cell1 / 10), ((int)((hv.Speed.Raw * 200 + Cell1 / 2) / Cell1), hr.MaxSpeed)); // (per tick, rounded)
        Assert.Equal((200, 375L * Cell1 / 10), ((int)((ap.Speed.Raw * 200 + Cell1 / 2) / Cell1), ar.MaxSpeed));
        Assert.Equal((70, 90), (hr.YawRate, ar.YawRate));
        Assert.True(hv.Flight.ClimbPerTick < ap.Flight.ClimbPerTick, "it climbs slower");
        Assert.Equal((Fixed.FromInt(30), Fixed.FromInt(35)), (hv.Vision, ap.Vision));
        // Kept: 650 HP against 600, the mast radar only on the Apache type.
        Assert.Equal((650, 600), (hv.MaxHp, ap.MaxHp));
        Assert.Equal((Fixed.Zero, Fixed.FromInt(120)), (hv.GroundRadar, ap.GroundRadar));
    }

    [Theory]
    [InlineData("\"guided\": { \"maxFlight\": 600, \"turnRate\": 60, \"fireAndForget\": true, \"lockAfterLaunch\": true,",
        "\"guided\": { \"maxFlight\": 600, \"turnRate\": 60, \"lockAfterLaunch\": true,", "lockAfterLaunch needs its own seeker")]
    [InlineData("\"ripple\": { \"targets\": 4, \"interval\": 5 }", "\"ripple\": { \"targets\": 1, \"interval\": 5 }", "weapon.ripple.targets")]
    [InlineData("\"ripple\": { \"targets\": 4, \"interval\": 5 }", "\"ripple\": { \"targets\": 4 }", "weapon.ripple.interval is required")]
    [InlineData("\"targets\": [\"ground\"], \"ammo\": 60, \"noResupply\": true, \"cone\": 110 } }] },",
        "\"targets\": [\"ground\"], \"ammo\": 60, \"noResupply\": true, \"cone\": 110, \"ripple\": { \"targets\": 2, \"interval\": 5 } } }] },",
        "weapon.ripple is for a helicopter's guided missile")]
    public void RejectsBadHelicopterMissileContent(string find, string replace, string want)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Name.Replace('\\', '/').EndsWith("components/sentinel.json", StringComparison.Ordinal));
        var json = sources[i].Json.Replace("\r\n", "\n");
        var at = json.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the content has {find}");
        sources[i] = sources[i] with { Json = json[..at] + replace + json[(at + find.Length)..] };
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.Contains(want, e.Message, StringComparison.Ordinal);
    }

    // ---- The Apache type: radar-cued fire from cover ----

    [Fact]
    public void TheApacheFiresFromCoverAtItsOwnRadarsTrackWithoutRising()
    {
        var w = Sentinel(160, fog: true);
        w.AttachTerrain(Ridge(160, 80, 10, 30)); // a 30 m ridge along x = 80
        var u = Fly(w, Apache, 20, 80);
        var t = w.Spawn(2, "technical", 100, 80); // 20 cells beyond the crest
        w.Run(5);
        Assert.True(World.IsSpotted(t, 1), "its mast radar spots the vehicle for its side");
        Issue(w, CommandKind.Attack, u, target: t.Id);
        var phases = new List<HeliAttack>();
        Projectile? first = null;
        long launchAlt = 0, launchNeed = 0;
        for (var i = 0; i < 1200 && t.Alive; i++)
        {
            w.Step();
            if (phases.Count == 0 || phases[^1] != u.Heli!.Attack)
            {
                phases.Add(u.Heli!.Attack);
            }
            if (first is null && w.Projectiles.FirstOrDefault(p => p.Shooter == u.Id && p.Weapon.Effect.Guided is not null) is { } p0)
            {
                first = p0;
                (launchAlt, launchNeed) = (u.Altitude.Raw, w.EyeNeed(u.Pos, t.Pos, w.GroundHeight(t.Pos).Raw + Metre));
                Assert.Equal(HeliAttack.Hide, u.Heli!.Attack);
            }
        }
        output.WriteLine($"phases {string.Join(" ", phases)}; launched at {F(Metres(launchAlt))} m, the line needs {F(Metres(launchNeed))} m; " +
            $"lofted {first?.Lofted}; target {(t.Alive ? "alive" : "destroyed")}");
        Assert.NotNull(first);
        Assert.True(launchNeed > launchAlt, "it fired with no line of sight (behind the crest)");
        Assert.True(first!.Lofted, "the missile lofts over the crest");
        Assert.DoesNotContain(HeliAttack.Rise, phases);
        Assert.False(t.Alive, "the lofted missile destroyed the technical beyond the crest");
    }

    [Fact]
    public void ItsOwnRadarTracksEnemyVehiclesWithinItsReachOnly()
    {
        var w = Sentinel(160);
        w.AttachTerrain(Ridge(160, 80, 10, 30));
        var u = Fly(w, Apache, 20, 80);
        var t = w.Spawn(2, "technical", 100, 80);
        Assert.False(w.OwnRadarTrack(u, u), "not its own side");
        w.Run(2);
        Assert.True(w.OwnRadarTrack(u, t));
        // Out of its reach (120 cells) it is no track.
        var far = w.Spawn(2, "technical", 150, 150);
        w.Run(1);
        Assert.Equal(World.Len3(far.Pos.X.Raw - u.Pos.X.Raw, far.Pos.Y.Raw - u.Pos.Y.Raw, 0) <= 120 * Cell1, w.OwnRadarTrack(u, far));
        var man = w.Spawn(2, "Rifleman", 100, 84);
        w.Run(1);
        Assert.False(w.OwnRadarTrack(u, man), "a soldier is no track");
    }

    [Fact]
    public void ItsRippleTakesUpToFourVehiclesAPopUp()
    {
        var w = Sentinel(128);
        var u = Fly(w, Apache, 20, 64);
        var trucks = Enumerable.Range(0, 6).Select(i => w.Spawn(2, "infantry_carrier", 60, 59 + i * 2)).ToList(); // 1,200 HP: none dies to one
        Issue(w, CommandKind.Attack, u, target: trucks[2].Id);
        var launches = new List<(uint Tick, uint Target)>();
        var seen = new HashSet<uint>();
        for (var i = 0; i < 400; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (p.Shooter == u.Id && p.Weapon.Effect.Guided is not null && seen.Add(p.Id))
                {
                    launches.Add((w.Tick, p.Target));
                }
            }
        }
        var t0 = launches[0].Tick;
        var ripple = launches.Where(l => l.Tick < t0 + 40).ToList();
        output.WriteLine($"launches (tick from the first, target): {string.Join(", ", launches.Select(l => $"{l.Tick - t0}:{l.Target}"))}");
        Assert.Equal(trucks[2].Id, launches[0].Target);
        Assert.Equal(4, ripple.Count);
        Assert.Equal(4, ripple.Select(l => l.Target).Distinct().Count());
        Assert.Equal([0u, 5u, 10u, 15u], ripple.Select(l => l.Tick - t0));
        Assert.True(launches.Count > 4 && launches[4].Tick - t0 >= 40, "then the 2 s reload");
        Assert.True(launches.Count <= 10, "10 missiles");
    }

    [Fact]
    public void AloneTheTargetGetsOneMissileAPopUp()
    {
        var w = Sentinel(128);
        var u = Fly(w, Apache, 20, 64);
        var t = w.Spawn(2, "infantry_carrier", 60, 64);
        Issue(w, CommandKind.Attack, u, target: t.Id);
        var launches = new List<uint>();
        var seen = new HashSet<uint>();
        for (var i = 0; i < 300; i++)
        {
            w.Step();
            launches.AddRange(w.Projectiles.Where(p => p.Shooter == u.Id && p.Weapon.Effect.Guided is not null && seen.Add(p.Id)).Select(_ => w.Tick));
        }
        Assert.True(launches.Count >= 2);
        Assert.True(launches[1] - launches[0] >= 40, "no second missile at the same target within the ripple");
    }

    // ---- The Mi-28 type: command guidance ----

    // The Mi-28 type against a technical 18 cells beyond a 20 m ridge: (podsEmpty) its rocket pods spent, its missile is its
    // only weapon beyond its cannon's 20 cells, so it hides and pops up (its standoff, 85% of 28 cells, lies beyond its
    // cannon); with its pods (25 cells) the standoff lies within their reach and it charges in on a run instead. Either way,
    // while its missile is guided it holds its line of sight, exposed (never hiding or ducking).
    private (List<HeliAttack> Phases, int Guided, long HiddenAlt, bool Killed) HavocOverARidge(bool podsEmpty, int height = 20, int tx = 98)
    {
        var w = Sentinel(160);
        w.AttachTerrain(Ridge(160, 80, 10, height));
        var u = Fly(w, Havoc, 20, 80);
        if (podsEmpty)
        {
            var pods = u.Def.Weapons.Single(x => x.Effect.SalvoSize > 1);
            u.MutableCharges[pods.Stock] = 0;
        }
        var t = w.Spawn(2, "technical", tx, 80);
        Issue(w, CommandKind.Attack, u, target: t.Id);
        var phases = new List<HeliAttack>();
        var guidedTicks = 0;
        long hiddenAlt = -1;
        for (var i = 0; i < 1500 && t.Alive; i++)
        {
            w.Step();
            var h = u.Heli!;
            if (phases.Count == 0 || phases[^1] != h.Attack)
            {
                phases.Add(h.Attack);
            }
            if (h.Attack == HeliAttack.Hide && hiddenAlt < 0 && h.AttackTicks > 5)
            {
                hiddenAlt = u.Altitude.Raw;
            }
            if (w.Projectiles.FirstOrDefault(p => p.Shooter == u.Id && p.Weapon.Effect.Guided is not null && p.Guided) is { } m)
            {
                guidedTicks++;
                Assert.Equal(m.Id, u.Guiding); // it guides its missile
                Assert.True(h.Attack is HeliAttack.Fire or HeliAttack.Run or HeliAttack.Standoff, $"exposed while guiding, not {h.Attack}");
                Assert.True(w.EyeNeed(u.Pos, t.Pos, w.GroundHeight(t.Pos).Raw + Metre) <= u.Altitude.Raw, "with its line of sight");
                Assert.False(m.Lofted);
            }
        }
        output.WriteLine($"pods {(podsEmpty ? "empty" : "full")}: phases {string.Join(" ", phases)}; hid at {F(Metres(hiddenAlt))} m; " +
            $"guided {guidedTicks} ticks; target {(t.Alive ? "alive" : "destroyed")}; guiding {u.Guiding}");
        Assert.Equal(0u, u.Guiding);
        return (phases, guidedTicks, hiddenAlt, !t.Alive);
    }

    [Fact]
    public void WithoutARadarTheHavocPopsUpAndHoldsTheLineUntilImpact()
    {
        var (phases, guided, hiddenAlt, killed) = HavocOverARidge(podsEmpty: true);
        Assert.True(hiddenAlt >= 0, "it hid behind the ridge first");
        Assert.Contains(HeliAttack.Rise, phases);
        Assert.True(guided > ExposedTicks, "it held the line longer than a fire-and-forget launch is exposed");
        Assert.True(killed);
    }

    [Fact]
    public void WithItsRocketPodsTheHavocChargesInAndHoldsTheLine()
    {
        var (phases, guided, _, killed) = HavocOverARidge(podsEmpty: false);
        Assert.Contains(HeliAttack.Run, phases);
        Assert.DoesNotContain(HeliAttack.Hide, phases);
        Assert.True(killed);
        Assert.True(guided >= 0);
    }

    private const int ExposedTicks = World.TicksPerSecond / 2;

    [Theory]
    [InlineData(Havoc, false)]
    [InlineData(Apache, true)]
    public void ACommandGuidedMissileGoesWildWhenItsHelicopterFalls(string heli, bool hits)
    {
        var w = Sentinel(128);
        var u = Fly(w, heli, 20, 64);
        var t = w.Spawn(2, "infantry_carrier", 52, 64); // 1,200 HP: one missile shows, none kills
        Issue(w, CommandKind.Attack, u, target: t.Id);
        Projectile? m = null;
        for (var i = 0; i < 400 && m is null; i++)
        {
            w.Step();
            m = w.Projectiles.FirstOrDefault(p => p.Shooter == u.Id && p.Weapon.Effect.Guided is not null);
        }
        Assert.NotNull(m);
        u.HP = 0; // shot down the moment it fired
        var hp = t.HP;
        w.Run(60);
        output.WriteLine($"{heli}: the carrier lost {hp - t.HP} HP");
        Assert.Equal(hits, t.HP < hp);
    }

    [Fact]
    public void OneCommandGuidedMissileAtATime()
    {
        var w = Sentinel(128);
        var u = Fly(w, Havoc, 20, 64);
        var trucks = Enumerable.Range(0, 3).Select(i => w.Spawn(2, "infantry_carrier", 46, 62 + i * 2)).ToList();
        Issue(w, CommandKind.Attack, u, target: trucks[1].Id);
        var maxInFlight = 0;
        var launched = 0;
        var seen = new HashSet<uint>();
        for (var i = 0; i < 600; i++)
        {
            w.Step();
            maxInFlight = Math.Max(maxInFlight, w.Projectiles.Count(p => p.Shooter == u.Id && p.Weapon.Effect.Guided is not null));
            launched += w.Projectiles.Count(p => p.Shooter == u.Id && p.Weapon.Effect.Guided is not null && seen.Add(p.Id));
        }
        Assert.True(launched >= 3);
        Assert.Equal(1, maxInFlight);
    }

    // ---- The duel: each type against the same tank group ----

    // A helicopter of `heli` against four Bookers (player 2) parked 18 to 24 cells beyond a 30 m ridge, attack-moving onto
    // them from the far side; fog on (each sees for itself; the Apache type's radar spots for it). The ticks until all four are
    // destroyed (or the limit), the missiles fired, and the ticks it was exposed (a line of sight to a live Booker).
    private (int Ticks, int Missiles, int Exposed, int Killed) TankDuel(string heli, int limit = 4800)
    {
        var w = Duel(200, 3, fog: true);
        w.AttachTerrain(Ridge(200, 100, 10, 30));
        var u = Fly(w, heli, 30, 100);
        var tanks = new[] { (118, 96), (118, 104), (124, 92), (124, 108) }.Select(p => w.Spawn(2, "light_tank", p.Item1, p.Item2, Facing.West)).ToList();
        foreach (var tk in tanks)
        {
            w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.Stop, Units = [tk.Id] });
        }
        Issue(w, CommandKind.AttackMove, u, new Cell(121, 100));
        var seen = new HashSet<uint>();
        var (missiles, exposed) = (0, 0);
        var start = w.Tick;
        while (w.Tick - start < limit && tanks.Any(t => t.Alive) && u.Alive)
        {
            w.Step();
            missiles += w.Projectiles.Count(p => p.Shooter == u.Id && p.Weapon.Effect.Guided is not null && seen.Add(p.Id));
            if (tanks.Any(t => t.Alive && w.EyeNeed(u.Pos, t.Pos, w.GroundHeight(t.Pos).Raw + Metre) <= u.Altitude.Raw))
            {
                exposed++;
            }
        }
        return ((int)(w.Tick - start), missiles, exposed, tanks.Count(t => !t.Alive));
    }

    [Fact]
    public void TheDuelTheApacheKillsMoreTanksFromCoverThanTheHavoc()
    {
        var a = TankDuel(Apache);
        var h = TankDuel(Havoc);
        output.WriteLine($"Apache: {a.Killed} of 4 Bookers in {F(a.Ticks / 20.0)} s, {a.Missiles} missiles, exposed {F(a.Exposed / 20.0)} s");
        output.WriteLine($"Havoc:  {h.Killed} of 4 Bookers in {F(h.Ticks / 20.0)} s, {h.Missiles} missiles, exposed {F(h.Exposed / 20.0)} s");
        Assert.True(a.Killed >= h.Killed, "the Apache type kills at least as many");
        Assert.True(a.Exposed < h.Exposed, "and is exposed less");
    }

    // ---- Determinism ----

    // Both types on both sides of a ridge in fog: the Apache type's radar ripple at a column, the Mi-28 type's held line at a
    // vehicle; the commands in order or reversed; the world's hash every tick, checked against the reference hash.
    private static List<ulong> Script(bool reversed)
    {
        var w = Duel(160, 11, fog: true);
        w.AttachTerrain(Ridge(160, 80, 10, 30));
        var a1 = w.Spawn(1, Apache, 20, 60, Facing.East);
        var h1 = w.Spawn(1, Havoc, 20, 110, Facing.East);
        var column = Enumerable.Range(0, 5).Select(i => w.Spawn(2, "humvee", 100, 52 + i * 4)).ToList();
        var lone = w.Spawn(2, "humvee", 96, 110);
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.Attack, Units = [a1.Id], Target = column[2].Id },
            new Command { Tick = 1, Player = 1, Seq = 2, Kind = CommandKind.Attack, Units = [h1.Id], Target = lone.Id },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        for (var t = 0; t < 900; t++)
        {
            w.Step();
            var hash = w.Hash();
            Assert.Equal(w.HashReference(), hash);
            hashes.Add(hash);
        }
        Assert.True(column.Count(c => !c.Alive) >= 2 && !lone.Alive, "the ripple and the held line both killed");
        return hashes;
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var a = Script(false);
        Assert.Equal(a, Script(false));
        Assert.Equal(a, Script(true));
    }
}
