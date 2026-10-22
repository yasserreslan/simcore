using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// Air classes as content, signatures and team cueing (E5; the plan's air-combat model, section 2.8):
// the classes come from content/mvp/air_classes.json and every per-class rule from the interceptors'
// content; a target's signature scales its detection range, the lock time on it and the kill chance
// against it (the air superiority jet's 50 / 200 / 60: harder to see, lock and bring down, not
// invisible); an air target any of a player's sensors detects is tracked for his whole side, so a
// shooter in range may lock it without detecting it itself. The drones' outcomes are unchanged (their
// own suites: AirDefenseTests, ScoutDroneTests, WatcherTests, OverseerTests, SamVehicleTests).
public class AirClassTests
{
    private const int N = 400;
    private const string Jet = "test_fixed_wing";
    private const string Stealth = "test_stealth_wing";

    // The MVP content plus the test fixed wing (content/fixtures/fixed_wing_test.json: the multirole's
    // numbers, class "multirole") and a copy of it with the air superiority jet's signature.
    private static readonly Lazy<ContentSet> JetContent = new(() => ContentLoader.Load(JetSources()));

    internal static List<ContentSource> JetSources()
    {
        var sources = TestContent.MvpSources();
        var fixture = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "content", "fixtures", "fixed_wing_test.json"));
        sources.Add(new ContentSource("fixtures/fixed_wing_test.json", fixture));
        sources.Add(new ContentSource("fixtures/stealth_test.json", fixture
            .Replace($"\"{Jet}\"", $"\"{Stealth}\"", StringComparison.Ordinal)
            .Replace("\"glyph\": \"j\"", "\"glyph\": \"k\"", StringComparison.Ordinal)
            .Replace("\"mission\":", "\"signature\": { \"detect\": 50, \"lock\": 200, \"kill\": 60 }, " +
                "\"countermeasures\": [{ \"kind\": \"flare\", \"salvos\": 4, \"vs\": \"ir\", \"pk\": 55, \"window\": 40, \"rearmTicks\": 40 }, " +
                "{ \"kind\": \"chaff\", \"salvos\": 4, \"vs\": \"radar\", \"pk\": 60, \"window\": 40, \"perMissile\": 1, \"rearmTicks\": 40 }], \"mission\":",
                StringComparison.Ordinal)));
        var i = sources.FindIndex(s => s.Name.EndsWith("factions/sentinel.json", StringComparison.Ordinal));
        sources[i] = new ContentSource(sources[i].Name,
            sources[i].Json.Replace("\"watcher_drone\",", $"\"watcher_drone\", \"{Jet}\", \"{Stealth}\",", StringComparison.Ordinal));
        return sources;
    }

    // JetWorld: the Sentinel lobby (player 1 Sentinel with the test jets, player 2 Resistance), no army.
    private static World JetWorld(int size = N, ulong seed = 1)
    {
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(JetContent.Value, seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(size, size), MatchCompiler.Compile(JetContent.Value, setup));
    }

    private static Interceptor Intercept(ContentSet c, string component) =>
        c.Component(component)!.Effects.OfType<WeaponEffect>().Single().Interceptor!;

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    // ---- Classes are content ----

    [Fact]
    public void TheAirClassesAreContentInTheOrderOfTheEnginesAliases()
    {
        var classes = TestContent.Mvp.AirClasses;
        Assert.Equal(["ballistic", "drone", "scout", "watcher", "overseer", "fpv", "multirole", "fighter", "attacker", "gunship", "bomber", "missile", "rocket",
            "attackheli", "armoredheli", "lightheli", "kaman"], classes.Select(c => c.Id));
        foreach (var c in classes)
        {
            Assert.Equal(c.Id, Enum.GetName(c.Class)!.ToLowerInvariant()); // the aliases are the content's positions
        }
        Assert.Equal([AirClassKind.Projectile, AirClassKind.Projectile], classes.Take(2).Select(c => c.Kind));
        Assert.All(classes.Skip(2).Take(9), c => Assert.Equal(AirClassKind.Aircraft, c.Kind));
        Assert.All(classes.Skip(11).Take(2), c => Assert.Equal(AirClassKind.Projectile, c.Kind)); // the arcing missile and rocket (appended)
        Assert.All(classes.Skip(13), c => Assert.Equal(AirClassKind.Aircraft, c.Kind)); // the three helicopters (appended)
        Assert.Equal(InterceptClass.Drone, TestContent.Mvp.AirClass("drone")!.Class);
        Assert.Null(TestContent.Mvp.AirClass("helicopter"));
    }

    [Fact]
    public void EveryFlyingUnitAndInterceptibleProjectileNamesAClassOfItsKind()
    {
        var c = TestContent.Mvp;
        foreach (var a in c.Archetypes.Where(a => a.Flight is not null))
        {
            Assert.Equal(AirClassKind.Aircraft, c.AirClasses[(int)a.Flight!.Class - 1].Kind);
        }
        foreach (var w in c.Components.SelectMany(x => x.Effects.OfType<WeaponEffect>()).Where(w => w.Intercept != InterceptClass.None))
        {
            Assert.Equal(AirClassKind.Projectile, c.AirClasses[(int)w.Intercept - 1].Kind);
        }
        // Every interceptor's per-class rules name one of its classes; every signature is in range.
        foreach (var ic in c.Components.SelectMany(x => x.Effects.OfType<WeaponEffect>()).Select(w => w.Interceptor).OfType<Interceptor>())
        {
            Assert.All(ic.Rules, r => Assert.True(ic.Engages(r.Class)));
            Assert.All(ic.Hits ?? [], h => Assert.True(ic.Engages(h.Class)));
            Assert.NotEqual(Seeker.None, ic.Seeker);
        }
        foreach (var a in c.Archetypes)
        {
            Assert.InRange(a.Signature.Detect, 10, 200);
            Assert.InRange(a.Signature.Lock, 25, 1000);
            Assert.InRange(a.Signature.Kill, 1, 100);
        }
    }

    [Fact]
    public void TheDronesTermsAreTheSameAsBefore()
    {
        var c = TestContent.Mvp;
        var igla = Intercept(c, "igla");
        var sam = Intercept(c, "sam_missiles");
        Assert.Equal((5, 10, 95, 95), (igla.Best(InterceptClass.Scout), igla.Best(InterceptClass.Watcher), igla.Best(InterceptClass.Drone), igla.Best(InterceptClass.Overseer)));
        Assert.Equal((20, 40, 95), (sam.Best(InterceptClass.Scout), sam.Best(InterceptClass.Watcher), sam.Best(InterceptClass.Drone)));
        foreach (var ic in new[] { igla, sam })
        {
            foreach (var cls in new[] { InterceptClass.Drone, InterceptClass.Scout, InterceptClass.Watcher, InterceptClass.Overseer })
            {
                Assert.Equal(ic.SpeedRef, ic.SpeedRefOn(cls)); // the drones keep the weapon's reference speed
                Assert.Equal(100, ic.HitDamageOn(cls));        // and a successful kill roll still kills
            }
            Assert.True(ic.EngagesUnits);
        }
        Assert.Equal(new InterceptHit(InterceptClass.Overseer, 45, 55), igla.HitOn(InterceptClass.Overseer));
        Assert.Equal(new InterceptHit(InterceptClass.Overseer, 65, 100), sam.HitOn(InterceptClass.Overseer));
        Assert.All(c.Archetypes.Where(a => a.Id != "fighter_jet"), a => Assert.Same(Signature.Normal, a.Signature)); // only the air superiority jet has a signature of its own (E8)
    }

    [Fact]
    public void TheJetsClassesHaveThePlansAirDefenceNumbers()
    {
        var c = TestContent.Mvp;
        var igla = Intercept(c, "igla");
        var sam = Intercept(c, "sam_missiles");
        Assert.Equal((Seeker.Ir, Seeker.Radar), (igla.Seeker, sam.Seeker));
        foreach (var jet in new[] { InterceptClass.Multirole, InterceptClass.Fighter })
        {
            Assert.True(igla.Engages(jet) && sam.Engages(jet));
            Assert.Equal((35, 60), (igla.Best(jet), igla.HitDamageOn(jet))); // an Igla hit takes 60%: a damaged jet limps home
            Assert.Equal((70, 100), (sam.Best(jet), sam.HitDamageOn(jet)));  // a SAM hit kills
            Assert.True(igla.SpeedRefOn(jet).Raw > igla.SpeedRef.Raw, "jets are judged against their own speed, not a drone's");
        }
        Assert.Equal((70, 85), (igla.Best(InterceptClass.Fpv), sam.Best(InterceptClass.Fpv)));
        var fixture = JetContent.Value.Archetype(Jet)!;
        Assert.Equal(InterceptClass.Multirole, fixture.Flight!.Class);
        Assert.Equal(new Signature(50, 200, 60), JetContent.Value.Archetype(Stealth)!.Signature);
        // The countermeasures' data shape (E5; dispensing is E6): one dispenser per kind, perMissile 1 by default.
        Assert.Equal(
            [new Countermeasure(CountermeasureKind.Flare, 4, Seeker.Ir, 55, 40, 1, 40), new Countermeasure(CountermeasureKind.Chaff, 4, Seeker.Radar, 60, 40, 1, 40)],
            JetContent.Value.Archetype(Stealth)!.Countermeasures);
        Assert.Empty(fixture.Countermeasures);
    }

    [Theory]
    [InlineData("\"classes\": [\"drone\", \"scout\", \"watcher\", \"overseer\", \"fpv\", \"multirole\", \"fighter\", \"attacker\", \"gunship\", \"bomber\", \"attackheli\", \"armoredheli\", \"lightheli\", \"kaman\"], \"seeker\": \"ir\"",
        "\"classes\": [\"drone\", \"helicopter\"], \"seeker\": \"ir\"", "unknown air class")]
    [InlineData("\"seeker\": \"ir\"", "\"seeker\": \"laser\"", "seeker")]
    [InlineData("\"hitDamage\": { \"multirole\": 60, \"fighter\": 60, \"attacker\": 25, \"gunship\": 13, \"bomber\": 17, \"attackheli\": 25, \"armoredheli\": 20, \"lightheli\": 50, \"kaman\": 50 }", "\"hitDamage\": { \"drone\": 60 }", "not an aircraft class")]
    [InlineData("\"interceptible\": \"drone\", \"ammo\": 2", "\"interceptible\": \"watcher\", \"ammo\": 2", "projectile air class")]
    [InlineData("\"interceptible\": \"scout\"", "\"interceptible\": \"drone\"", "aircraft air class")]
    [InlineData("\"mission\": { \"huntLeash\": 300", "\"signature\": { \"detect\": 5 }, \"mission\": { \"huntLeash\": 300", "signature.detect")]
    [InlineData("\"components\": [\"igla\"]", "\"signature\": { \"kill\": 50 }, \"components\": [\"igla\"]", "needs a flight block")]
    [InlineData("{ \"id\": \"fpv\", \"kind\": \"aircraft\" }", "{ \"id\": \"fpv\", \"kind\": \"rocket\" }", "kind must be")]
    [InlineData("{ \"id\": \"fpv\", \"kind\": \"aircraft\" }", "{ \"id\": \"drone\", \"kind\": \"aircraft\" }", "already defined")]
    public void BadAirClassContentIsRefused(string from, string to, string error)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Json.Contains(from, StringComparison.Ordinal));
        Assert.True(i >= 0, $"no file has {from}");
        var json = sources[i].Json;
        var at = json.IndexOf(from, StringComparison.Ordinal);
        sources[i] = sources[i] with { Json = string.Concat(json.AsSpan(0, at), to, json.AsSpan(at + from.Length)) };
        var e = Assert.ThrowsAny<Exception>(() => ContentLoader.Load(sources));
        Assert.Contains(error, e.Message, StringComparison.Ordinal);
    }

    // ---- Signatures ----

    // A jet put down in flight over (x, y) by player 1, orbiting there; an AA soldier of player 2 at (ax, ay).
    private static (World W, Unit Jet, Unit Aa) Duel(string jet, int x, int y, int ax = 200, int ay = 200, ulong seed = 1)
    {
        var w = JetWorld(N, seed);
        var aa = w.Spawn(2, "manpads", ax, ay);
        var j = w.Spawn(1, jet, x, y);
        return (w, j, aa);
    }

    [Theory]
    [InlineData(Jet, 100, true)]     // about 104 cells of slant: inside the soldier's 150
    [InlineData(Stealth, 100, false)] // the stealth jet is detected at half that: 75
    [InlineData(Stealth, 60, true)]   // about 67 cells: inside 75
    public void TheSignatureScalesHowFarASensorDetectsIt(string jet, int dx, bool seen)
    {
        var (w, j, aa) = Duel(jet, 200 + dx, 200);
        w.Run(2);
        Assert.Equal(seen, aa.AirTarget == j.Id);
    }

    // FirstLaunch: the tick the soldier fires his first missile at a jet orbiting 30 cells east of him.
    private static int FirstLaunch(string jet)
    {
        var (w, _, aa) = Duel(jet, 230, 200);
        var full = aa.Charges[0];
        for (var t = 1; t <= 400; t++)
        {
            w.Step();
            if (aa.Charges[0] < full)
            {
                return t;
            }
        }
        return -1;
    }

    [Fact]
    public void ALockOnTheStealthJetTakesTwiceTheLockTime()
    {
        var normal = FirstLaunch(Jet);
        var stealth = FirstLaunch(Stealth);
        Assert.True(normal > 0 && stealth > 0, $"{normal} {stealth}");
        var lockTicks = Intercept(JetContent.Value, "igla").LockTicks;
        Assert.Equal(lockTicks, stealth - normal); // 1.5 s more: twice the soldier's 1.5 s lock
    }

    // BestShot: the kill chance of the soldier's best possible shot at a jet flying at speed (raw a tick: 1024 is a cell, so 1024 is 20 cells/s).
    private static int BestShot(string jet, long speed)
    {
        var (w, j, aa) = Duel(jet, 200, 200);
        j.Vel = new Vec(Fixed.Zero, Fixed.FromRaw(-speed));
        var wd = aa.Def.Weapons[World.InterceptorWeapon(aa.Def)];
        var ic = wd.Effect.Interceptor!;
        var body = new AirBody(j);
        var m = new Projectile(9999, aa, wd, j.Pos, j.Id)
        {
            Vel = new Vec(Fixed.Zero, Fixed.FromRaw(-wd.Effect.ProjectileSpeed.Raw)),
            LaunchRange = Fixed.Zero,
            LaunchCross = 0,
            Tracked = World.LockTicksOn(ic, body) + ic.TrackFull,
        };
        return w.KillPermille(m, body, ic);
    }

    [Fact]
    public void TheStealthJetsKillChanceIsSixTenthsAndCruiseSpeedCostsAJetNothing()
    {
        Assert.Equal(350, BestShot(Jet, 512));     // the class's best: 35% (10 cells/s)
        Assert.Equal(210, BestShot(Stealth, 512)); // x 0.6
        Assert.Equal(350, BestShot(Jet, 1024));    // at cruise (20 cells/s) still its best: judged against 23 cells/s
        Assert.True(BestShot(Jet, 1434) < 350, "on afterburner (28 cells/s) it is harder to hit");
    }

    [Fact]
    public void AnIglaHitTakesSixtyPercentOfAJetAndItFliesOn()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var (w, j, _) = Duel(Jet, 230, 200, seed: seed);
            for (var t = 0; t < 1500 && j.Alive; t++)
            {
                var hp = j.HP;
                w.Step();
                if (w.AirBursts.Where(b => b.Target == j.Id && b.Roll >= 0 && b.Roll < b.KillPermille).Select(b => (AirBurst?)b).FirstOrDefault() is { } hit)
                {
                    Assert.Equal(AirBurstKind.Hit, hit.Kind);
                    Assert.Equal(hp - j.Def.MaxHp * 60 / 100, j.HP);
                    Assert.True(j.Alive);
                    return;
                }
            }
        }
        Assert.Fail("no hit in 40 seeds");
    }

    // ---- Team cueing ----

    [Fact]
    public void AnAlliedRadarCuesASoldierOntoAStealthJetHeCannotSeeHimself()
    {
        // The stealth jet orbits 80 to 120 cells east of the soldier: inside his 140-cell missile but
        // beyond the 75 cells at which he detects it himself.
        var (w, j, aa) = Duel(Stealth, 300, 200);
        w.Run(100);
        Assert.Equal(0u, aa.AirTarget);

        (w, j, aa) = Duel(Stealth, 300, 200);
        var radar = w.SpawnCrewed(2, "sam_vehicle", 300, 260).Vehicle; // its radar sees the stealth jet at 130 cells
        radar.Stance = Stance.HoldFire; // a sensor only: it never fires
        var full = aa.Charges[0];
        var radarFull = radar.Charges[0];
        var tracked = false;
        for (var t = 0; t < 400 && aa.Charges[0] == full; t++)
        {
            w.Step();
            tracked |= aa.AirTarget == j.Id;
        }
        Assert.True(tracked, "the soldier tracks the cued jet");
        Assert.True(aa.Charges[0] < full, "and fires at it once locked");
        Assert.True(w.AirTracked(2, j.Id));
        Assert.False(w.AirTracked(1, j.Id)); // a player never tracks his own
        Assert.Equal(radarFull, radar.Charges[0]);
    }

    [Fact]
    public void ACueNeverReachesBeyondTheShootersOwnRange()
    {
        var (w, j, aa) = Duel(Stealth, 300, 200, ax: 100);
        var radar = w.SpawnCrewed(2, "sam_vehicle", 300, 260).Vehicle;
        radar.Stance = Stance.HoldFire;
        w.Run(100);
        Assert.True(w.AirTracked(2, j.Id), "the radar tracks it");
        Assert.Equal(0u, aa.AirTarget); // 180 to 220 cells away: beyond the soldier's 140
    }

    // ---- How far air defence reaches the real jets (no map-wide awareness) ----

    // The air defence vehicle's radar detects the multirole (signature 100) out to its 260-cell air vision and
    // the air superiority jet (detect 50) out to 130 cells, slant from its launcher; nothing beyond, and its
    // side tracks only what it detects.
    [Theory]
    [InlineData("Multirole 500 lb", 250, true)]
    [InlineData("Multirole 500 lb", 270, false)]
    [InlineData("fighter_jet", 120, true)]
    [InlineData("fighter_jet", 140, false)]
    public void TheAirDefenceVehiclesRadarReachesAJetOnlyWithinItsAirVision(string jet, int dx, bool seen)
    {
        var w = Arena.NewSentinel(N);
        var sam = w.SpawnCrewed(2, "sam_vehicle", 50, 200).Vehicle;
        var j = w.Spawn(1, jet, 50 + dx, 200);
        j.Altitude = Fixed.FromRaw((jet == "fighter_jet" ? 70 : 60) * 512L); // the E8 patrol heights (E13 flies them higher: see the design)
        w.Run(2);
        Assert.Equal(seen, sam.AirTarget == j.Id);
        Assert.Equal(seen, w.AirTracked(2, j.Id));
    }

    // It tracks a multirole circling 225 to 260 cells out (inside its radar) but never fires at it: its
    // missile reaches 220 cells. Brought within reach, it fires.
    [Theory]
    [InlineData(255, false)]
    [InlineData(150, true)]
    public void TheAirDefenceVehicleFiresAtAJetOnlyWithinItsMissilesRange(int dx, bool fires)
    {
        var w = Arena.NewSentinel(N);
        var sam = w.SpawnCrewed(2, "sam_vehicle", 50, 200).Vehicle;
        var j = w.Spawn(1, "Multirole 500 lb", 50 + dx, 200);
        var range = sam.Def.Weapons[World.InterceptorWeapon(sam.Def)].Range.Raw;
        var full = sam.Charges[0];
        var tracked = false;
        var nearest = long.MaxValue;
        for (var t = 0; t < 600 && sam.Charges[0] == full; t++)
        {
            w.Step();
            tracked |= sam.AirTarget == j.Id;
            nearest = Math.Min(nearest, (j.Pos - sam.Pos).Len().Raw);
        }
        Assert.True(tracked);
        Assert.Equal(fires, sam.Charges[0] < full);
        if (!fires)
        {
            Assert.True(nearest > range, $"the jet came within {Cells(nearest)} cells"); // the premise: it stayed beyond 220 cells
        }
    }

    // ---- Who can target whom (the plan's section 2.11, the rows that exist today) ----

    [Fact]
    public void OnlyAirDefenceOfItsClassReachesAJet()
    {
        var w = JetWorld();
        var jet = w.Spawn(1, Jet, 200, 200);
        var shooters = new[]
        {
            w.Spawn(2, "Rifleman", 200, 205), w.Spawn(2, "Gunner", 201, 205), w.Spawn(2, "Tank hunter", 202, 205),
            w.SpawnCrewed(2, "technical", 198, 205).Vehicle, w.SpawnCrewed(2, "light_rocket_truck", 196, 205).Vehicle,
        };
        foreach (var s in shooters)
        {
            Assert.All(s.Def.Weapons.Where(x => x.Effect.Interceptor is null), wd => Assert.False(World.CanHit(wd, jet), $"{s.Def.Archetype.Id} {wd.Warhead.Id}"));
        }
        var watcher = w.Spawn(1, "watcher_drone", 220, 200);
        Assert.All(watcher.Def.Weapons, wd => Assert.False(World.CanHit(wd, jet))); // the Watcher's missile is air-to-ground only
        var mvp = TestContent.Mvp;
        foreach (var ic in new[] { Intercept(mvp, "igla"), Intercept(mvp, "sam_missiles") })
        {
            foreach (var cls in new[] { InterceptClass.Drone, InterceptClass.Scout, InterceptClass.Watcher, InterceptClass.Overseer, InterceptClass.Multirole, InterceptClass.Fighter })
            {
                Assert.True(ic.Engages(cls), $"{cls}");
            }
            Assert.False(ic.Engages(InterceptClass.Ballistic));
        }
    }
}
