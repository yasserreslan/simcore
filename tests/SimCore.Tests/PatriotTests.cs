using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The missile defence launcher (patriot_launcher), the radar truck and the reload pad (the game repo's
// docs/patriot-design.md, owner 2026-10-21): the launcher has no sensor and fires only on a friendly radar's track,
// set up; it engages arced missiles, heavy rockets, ballistic missiles (two at each), drones and aircraft, never what
// will land harmlessly; it reloads only on its pad near an airfield, all 4 in 10 s.
public class PatriotTests
{
    private const int N = 400;
    private const int X = 200;
    private const int Y = 200;

    private static void Order(World w, Unit u, CommandKind kind, Cell cell = default) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell });

    private static UnitDef Def(World w, string id, byte player = 1) => w.Player(player)!.Setup.Find(w.Content, id)!;

    // Battery: player 1's launcher at (X, Y) and (with radar) its radar truck 10 cells east, both crewed; set up unless told
    // otherwise (the launcher 20 s, the radar 15 s).
    private static (World W, Unit Launcher, Unit? Radar) Battery(ulong seed = 1, bool radar = true, bool setUpLauncher = true, bool setUpRadar = true,
        string kind = "patriot_launcher")
    {
        var w = Arena.NewSentinel(N, seed);
        var launcher = w.SpawnCrewed(1, kind, X, Y).Vehicle;
        var r = radar ? w.SpawnCrewed(1, "radar_truck", X + 10, Y).Vehicle : null;
        if (setUpLauncher)
        {
            Order(w, launcher, CommandKind.Deploy);
        }
        if (r is not null && setUpRadar)
        {
            Order(w, r, CommandKind.Deploy);
        }
        w.Run(kind == "thaad_launcher" ? 520 : 420);
        return (w, launcher, r);
    }

    // Fire: player 2's launcher of the given kind, crewed, distance cells due south facing north (its box on the battery: the
    // truck launchers aim with the hull), attacks the ground at aim.
    private static Unit Fire(World w, string kind, int distance, Cell aim)
    {
        var truck = w.SpawnCrewed(2, kind, X, Y + distance, Facing.North).Vehicle;
        Order(w, truck, CommandKind.AttackGround, aim);
        return truck;
    }

    // Watch runs ticks and returns the interceptors launched (one per new projectile ID) and the bursts.
    private static (List<Projectile> Launched, List<AirBurst> Bursts) Watch(World w, int ticks)
    {
        var launched = new List<Projectile>();
        var bursts = new List<AirBurst>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (p.Weapon.Effect.Interceptor is not null && launched.All(l => l.Id != p.Id))
                {
                    launched.Add(p);
                }
            }
            bursts.AddRange(w.AirBursts);
        }
        return (launched, bursts);
    }

    [Fact]
    public void ItIsData()
    {
        var c = TestContent.Mvp;
        var launcher = c.Archetype("patriot_launcher")!;
        var radar = c.Archetype("radar_truck")!;
        var pad = c.Archetype("patriot_pad")!;
        var w = Arena.NewSentinel(64);
        var wd = Def(w, "patriot_launcher").Weapons[0];
        var ic = wd.Effect.Interceptor!;
        Assert.True(ic.RadarLinked);
        Assert.Equal((4, Fixed.FromInt(280), 2), (wd.Effect.Ammo, wd.Range, ic.SalvoOn(InterceptClass.Ballistic)));
        Assert.Equal(1, ic.SalvoOn(InterceptClass.Missile));
        Assert.Equal(0, ic.MaxPerTarget); // no cap: only the salvo limits it
        Assert.Equal((85, 65, 50, 40, 12), (ic.Best(InterceptClass.Missile), ic.Best(InterceptClass.Rocket), ic.Best(InterceptClass.Drone),
            ic.Best(InterceptClass.Fighter), ic.Best(InterceptClass.Ballistic)));
        Assert.Equal(0, launcher.AirVision.Raw); // no sensor of its own
        Assert.Equal((400, 300), (launcher.DeployTicks, launcher.StowTicks));
        Assert.Equal((Fixed.FromInt(360), Fixed.FromInt(80)), (radar.AirVision, radar.GroundRadar));
        Assert.True(radar.AirVision > wd.Range, "the radar out-ranges the launcher");
        Assert.True(c.Archetype("airbase_radar")!.AirVision > radar.AirVision, "the air base's radar reaches farthest");
        Assert.Equal((200, Fixed.FromInt(40)), (pad.ReloadPad!.ReloadTicks, pad.ReloadPad.AirfieldRange));
        var sentinel = c.Faction("sentinel")!;
        Assert.True(sentinel.Archetypes[launcher.Index] && sentinel.Archetypes[radar.Index] && sentinel.Archetypes[pad.Index]);
        // Never the Kornet, the light rockets, the mortar: not marked.
        Assert.Equal(InterceptClass.None, Def(w, "kornet", 2).Weapons[0].Effect.Intercept);
        Assert.Equal(InterceptClass.None, Def(w, "heavy_rocket_launcher", 2).Weapons[0].Effect.Intercept);
        Assert.Equal(InterceptClass.Rocket, Def(w, "heavy_rocket_truck", 2).Weapons[0].Effect.Intercept);
        Assert.Equal(InterceptClass.Rocket, Def(w, "rocket_jeep", 2).Weapons[0].Effect.Intercept);
    }

    [Fact]
    public void WithARadarItInterceptsATwinMissileAimedAtIt()
    {
        var (w, launcher, _) = Battery();
        Assert.Equal(DeployState.Deployed, launcher.Deploy);
        Fire(w, "missile_truck", 90, new Cell(X, Y + 2));
        var (launched, bursts) = Watch(w, 900);
        Assert.NotEmpty(launched);
        Assert.All(launched, m => Assert.Equal(launcher.Id, m.Shooter));
        var fused = bursts.Where(b => b.Kind is AirBurstKind.Kill or AirBurstKind.Miss).ToList();
        Assert.NotEmpty(fused);
        // The arced missile's base is 85%; its speed, crossing and range take a little off.
        Assert.All(fused, b => Assert.InRange(b.KillPermille, 550, 850));
    }

    [Fact]
    public void WithoutARadarOrWithTheRadarPackedUpItNeverFires()
    {
        foreach (var (radar, setUpRadar) in new[] { (false, false), (true, false) })
        {
            var (w, _, r) = Battery(radar: radar, setUpRadar: setUpRadar);
            if (r is not null)
            {
                Assert.False(World.RadarOf(r)!.Value.On, "a packed-up radar truck is off");
            }
            Fire(w, "missile_truck", 90, new Cell(X, Y + 2));
            Assert.Empty(Watch(w, 900).Launched);
        }
    }

    [Fact]
    public void ASoldiersEyesAreNotARadar()
    {
        var (w, _, _) = Battery(radar: false);
        w.Spawn(1, "manpads", X - 5, Y); // sees the sky out to 150 cells, but is no radar
        Fire(w, "missile_truck", 90, new Cell(X, Y + 2));
        Assert.DoesNotContain(Watch(w, 900).Launched, m => m.Weapon.Effect.Interceptor!.RadarLinked);
    }

    [Fact]
    public void PackedUpItHoldsFire()
    {
        var (w, launcher, _) = Battery(setUpLauncher: false);
        Assert.Equal(DeployState.Stowed, launcher.Deploy);
        Fire(w, "missile_truck", 90, new Cell(X, Y + 2));
        Assert.Empty(Watch(w, 900).Launched);
    }

    [Fact]
    public void ItLetsAMissileThatWillLandHarmlesslyGo()
    {
        var (w, _, _) = Battery();
        Fire(w, "missile_truck", 90, new Cell(X + 60, Y + 30)); // 60 cells off: nothing of its side within 15
        Assert.Empty(Watch(w, 900).Launched);
    }

    [Fact]
    public void ItFiresTwoAtABallisticMissileAndRarelyStopsIt()
    {
        var (w, launcher, _) = Battery();
        Fire(w, "ballistic_truck", 130, new Cell(X, Y + 2));
        var (launched, bursts) = Watch(w, 700);
        var target = launched.GroupBy(m => m.Target).OrderByDescending(g => g.Count()).First();
        Assert.Equal(2, target.Count()); // shoot-shoot
        Assert.Equal(2, launcher.Charges[0]);
        var fused = bursts.Where(b => b.Kind is AirBurstKind.Kill or AirBurstKind.Miss).ToList();
        Assert.All(fused, b => Assert.InRange(b.KillPermille, 50, 120)); // 12% at best, a little less for its speed
    }

    [Fact]
    public void ItInterceptsHeavyRockets()
    {
        var (w, _, _) = Battery();
        Fire(w, "heavy_rocket_truck", 70, new Cell(X, Y + 2));
        var (launched, bursts) = Watch(w, 900);
        Assert.Equal(4, launched.Count); // a salvo of 12: it empties its box
        Assert.All(bursts.Where(b => b.Kind is AirBurstKind.Kill or AirBurstKind.Miss), b => Assert.InRange(b.KillPermille, 400, 650));
    }

    [Fact]
    public void TheSameEngagementTwiceGivesTheSameWorld()
    {
        ulong Play()
        {
            var (w, _, _) = Battery(seed: 7);
            Fire(w, "ballistic_truck", 130, new Cell(X, Y + 2));
            Fire(w, "missile_truck", 90, new Cell(X + 3, Y));
            w.Run(800);
            return w.Hash();
        }
        Assert.Equal(Play(), Play());
    }

    // ---- The reload pad ----

    // At facing 0 a facility at (30, 40) lays its airfield's runway on map y 16-26, x 41-101 (AirfieldCraterTests).
    private static (World W, Airfield Af) Field()
    {
        var w = Arena.NewSentinel(160);
        var af = w.SpawnAirfield(Def(w, "airfield_facility"), new Cell(30, 40), 0, [(0, 0)]) ?? throw new InvalidOperationException("no airfield");
        return (w, af);
    }

    [Fact]
    public void ThePadGoesOnlyNearItsSidesAirfield()
    {
        var (w, _) = Field();
        var worker = w.Spawn(1, "worker", 20, 70);
        var pad = Def(w, "patriot_pad");
        Assert.True(w.CanPlace(pad, new Cell(14, 60), 0, [worker]));   // about 15 cells from the facility
        Assert.False(w.CanPlace(pad, new Cell(100, 120), 0, [worker])); // about 100 cells away
        var enemy = w.Spawn(2, "worker", 20, 70);
        Assert.False(w.CanPlace(Def(w, "patriot_pad", 1), new Cell(14, 60), 0, [enemy])); // not by the other side's
    }

    [Fact]
    public void OnThePadAPackedUpLauncherReloadsAllFourInTenSeconds()
    {
        var (w, _) = Field();
        var pad = w.Spawn(1, "patriot_pad", 21, 64);
        var launcher = w.SpawnCrewed(1, "patriot_launcher", 21, 64).Vehicle;
        launcher.MutableCharges[0] = 0;
        w.Run(200);
        Assert.Equal(0, launcher.Charges[0]);
        Assert.Equal(launcher.Id, pad.PadUnit);
        w.Run(2);
        Assert.Equal(4, launcher.Charges[0]); // 10 s (200 ticks) after it was taken on
        Assert.Equal(0u, pad.PadUnit);
    }

    [Fact]
    public void NoAirfieldNoPadReloadAndNoDepotReload()
    {
        var (w, af) = Field();
        var pad = w.Spawn(1, "patriot_pad", 21, 64);
        var launcher = w.SpawnCrewed(1, "patriot_launcher", 21, 64).Vehicle;
        launcher.MutableCharges[0] = 1;
        w.Unit(af.Facility)!.HP = 0; // the airfield is down
        w.Run(400);
        Assert.Equal(1, launcher.Charges[0]);
        Assert.Equal(0u, pad.PadUnit);
        // A supply depot never reloads it (noResupply).
        var w2 = Arena.NewSentinel(64);
        w2.Spawn(1, "supply_depot", 30, 30);
        var other = w2.SpawnCrewed(1, "patriot_launcher", 32, 36).Vehicle;
        other.MutableCharges[0] = 1;
        w2.Run(1200);
        Assert.Equal(1, other.Charges[0]);
    }

    // ---- The high-altitude interceptor launcher (THAAD type) ----

    [Fact]
    public void TheHighAltitudeInterceptorIsAntiMissileOnly()
    {
        var w = Arena.NewSentinel(64);
        var ic = Def(w, "thaad_launcher").Weapons[0].Effect.Interceptor!;
        Assert.True(ic.RadarLinked);
        Assert.True(ic.Engages(InterceptClass.Ballistic) && ic.Engages(InterceptClass.Missile) && ic.Engages(InterceptClass.Rocket));
        Assert.False(ic.Engages(InterceptClass.Drone) || ic.Engages(InterceptClass.Fighter) || ic.Engages(InterceptClass.Scout));
        Assert.Equal((50, 70, 55, 2), (ic.Best(InterceptClass.Ballistic), ic.Best(InterceptClass.Missile), ic.Best(InterceptClass.Rocket), ic.SalvoOn(InterceptClass.Ballistic)));
        Assert.Equal((30L * 512, 120L * 512, 60), (ic.MinAltitudeRaw, ic.AltitudeFullRaw, ic.AltitudeLow));
        Assert.Equal(Fixed.FromInt(450), Def(w, "thaad_launcher").Weapons[0].Range);
        Assert.Equal(2, ic.MaxPerTarget); // at most 2 at any one target, ever (owner 2026-10-21)
        Assert.Equal(Fixed.FromInt(6), Def(w, "thaad_launcher").Weapons[0].Effect.ProjectileSpeed); // 120 cells/s: 6 a tick
    }

    [Fact]
    public void ItFiresTwoAtABallisticMissileHighUpAndOftenStopsIt()
    {
        var (w, launcher, _) = Battery(kind: "thaad_launcher");
        Assert.Equal(DeployState.Deployed, launcher.Deploy);
        Fire(w, "ballistic_truck", 150, new Cell(X, Y + 2));
        var (launched, bursts) = Watch(w, 700);
        Assert.Equal(2, launched.Count(m => m.Shooter == launcher.Id));
        var fused = bursts.Where(b => b.Kind is AirBurstKind.Kill or AirBurstKind.Miss).ToList();
        Assert.NotEmpty(fused);
        Assert.All(fused, b => Assert.InRange(b.KillPermille, 250, 500)); // 50% whole from 120 m, 60% of it at 30 m
        Assert.All(fused, b => Assert.True(b.Altitude.Raw >= 30 * 512, $"burst at {b.Altitude.Raw / 512} m: it engages only above 30 m"));
    }

    [Fact]
    public void ItFiresAtMostTwoAtOneTargetEvenWhenBothMiss()
    {
        // The owner (2026-10-21): 2 at any one target, ever. Its salvo caps only those in flight at once; without the cap
        // (maxPerTarget 0) seeds 1, 2, 4, 5 and 6 fire all 4 at the one ballistic missile after the pair misses.
        var bothMissed = 0;
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var (w, launcher, _) = Battery(seed, kind: "thaad_launcher");
            Fire(w, "ballistic_truck", 150, new Cell(X, Y + 2));
            var (launched, bursts) = Watch(w, 900);
            var own = launched.Where(m => m.Shooter == launcher.Id).ToList();
            Assert.Equal(2, own.Count);
            Assert.Single(own.Select(m => m.Target).Distinct());
            Assert.Equal(2, launcher.Charges[0]); // it keeps the other two
            var fused = bursts.Where(b => own.Any(m => m.Id == b.Missile) && b.Kind is AirBurstKind.Kill or AirBurstKind.Miss).ToList();
            if (fused.Count == 2 && fused.All(b => b.Kind == AirBurstKind.Miss))
            {
                bothMissed++;
            }
        }
        Assert.True(bothMissed >= 3, $"the pair missed in only {bothMissed} of 6 seeds: the cap is not exercised");
    }

    [Fact]
    public void ThePatriotTypeStillEngagesWhatTheHighAltitudeInterceptorGaveUpOn()
    {
        // The cap is the THAAD type's alone: its two at a ballistic missile do not count against the Patriot type (no cap),
        // which fires on at the missile after the THAAD type's pair misses.
        var after = 0;
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var (w, patriot, _) = Battery(seed);
            var thaad = w.SpawnCrewed(1, "thaad_launcher", X - 10, Y).Vehicle;
            Order(w, thaad, CommandKind.Deploy);
            w.Run(520);
            Fire(w, "ballistic_truck", 150, new Cell(X, Y + 2));
            var (launched, bursts) = Watch(w, 900);
            var high = launched.Where(m => m.Shooter == thaad.Id).ToList();
            Assert.InRange(high.Count, 1, 2);
            var target = high[0].Target;
            Assert.All(high, m => Assert.Equal(target, m.Target));
            var missed = high.Count == 2 && bursts.Count(b => high.Any(m => m.Id == b.Missile) && b.Kind == AirBurstKind.Miss) == 2;
            if (missed && launched.Any(m => m.Shooter == patriot.Id && m.Target == target && m.Id > high[1].Id))
            {
                after++;
            }
        }
        Assert.True(after > 0, "the Patriot type never fired at a missile the THAAD type's pair missed");
    }

    [Fact]
    public void TheSameCappedEngagementTwiceGivesTheSameWorld()
    {
        ulong Play()
        {
            var (w, _, _) = Battery(seed: 4, kind: "thaad_launcher");
            Fire(w, "ballistic_truck", 150, new Cell(X, Y + 2));
            Fire(w, "missile_truck", 120, new Cell(X + 3, Y));
            w.Run(900);
            return w.Hash();
        }
        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void ItLeavesLowThreatsAndDronesAlone()
    {
        // A heavy rocket fired from 52 cells tops out under 30 m: below the floor. A drone: not its class.
        var (w, launcher, _) = Battery(kind: "thaad_launcher");
        Fire(w, "heavy_rocket_truck", 52, new Cell(X, Y + 2));
        Fire(w, "drone_truck", 120, new Cell(X, Y + 2));
        Assert.DoesNotContain(Watch(w, 900).Launched, m => m.Shooter == launcher.Id);
    }

    [Fact]
    public void ThePadReloadsItToo()
    {
        var (w, _) = Field();
        var pad = w.Spawn(1, "patriot_pad", 21, 64);
        var launcher = w.SpawnCrewed(1, "thaad_launcher", 21, 64).Vehicle;
        launcher.MutableCharges[0] = 0;
        w.Run(202);
        Assert.Equal(4, launcher.Charges[0]);
        Assert.Equal(0u, pad.PadUnit);
    }
}
