using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The Overseer (overseer_drone; the Sentinel's unarmed Heron-type surveillance drone, the game repo's
// docs/overseer-design.md): launched by its own Overseer control station, it orbits 250 m up (the
// Watcher: 150), at 6 cells/s (8), for 40 minutes (20), seeing 50 cells round the point under it (35).
// Weak: air defence engages it as the "overseer" class with a flat hit roll and a share of its HP a
// hit (the AA soldier 45% and 55%: two hits; the air defence vehicle 65% and a kill); at its height
// the soldier's 140-cell slant range reaches it only within about 63 cells of the point under it. It
// homes at its station or at an airfield (class S) like the Watcher.
public class OverseerTests
{
    private const int N = 260;
    private const long Height = 250 * 512; // its cruise height over flat ground, raw

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    // Overhead: an Overseer put down in flight over (x, y) (the sandbox's way), settled into its orbit.
    private static Unit Overhead(World w, int x, int y)
    {
        var d = w.Spawn(1, "overseer_drone", x, y);
        w.Run(5);
        return d;
    }

    // Fuses runs until target d is down (or maxTicks pass) and returns the fuses that went off beside it.
    private static List<AirBurst> Fuses(World w, Unit d, int maxTicks)
    {
        var fuses = new List<AirBurst>();
        for (var i = 0; i < maxTicks && d.Alive; i++)
        {
            w.Step();
            fuses.AddRange(w.AirBursts.Where(b => b.Target == d.Id && b.Kind is AirBurstKind.Kill or AirBurstKind.Miss or AirBurstKind.Hit));
        }
        return fuses;
    }

    [Fact]
    public void TheContentHasTheOwnersNumbers()
    {
        var c = TestContent.Mvp;
        var a = c.Archetypes.Single(x => x.Id == "overseer_drone");
        var watcher = c.Archetypes.Single(x => x.Id == "watcher_drone");
        var fl = a.Flight!;
        Assert.Equal(Height, fl.HeightRaw);
        Assert.Equal(InterceptClass.Overseer, fl.Class);
        Assert.True(fl.Relaunch && !fl.HomeWhenEmpty);
        Assert.Equal(2 * watcher.Lifetime, a.Lifetime); // 40 minutes, twice the Watcher's
        Assert.Equal(40 * 60 * World.TicksPerSecond, a.Lifetime);
        Assert.Equal(Fixed.FromInt(50), a.Vision);
        Assert.Equal(150, a.MaxHp);
        var ac = a.Aircraft!;
        Assert.Equal((AircraftClass.S, LandingKind.Runway), (ac.SizeClass, ac.Landing));
        Assert.Equal(watcher.Aircraft!.Span, ac.Span); // the Watcher's 14.1 m span
        Assert.Equal(Fixed.FromRaw(34 * Fixed.OneRaw / 10), ac.Length);
        Assert.Equal((Fixed.FromInt(30), Fixed.FromInt(20)), (ac.MinLandRun, ac.MinTakeoffRun));

        var w = Arena.NewSentinel(32);
        var d = w.Spawn(1, "overseer_drone", 10, 10);
        Assert.Empty(d.Def.Weapons); // unarmed
        Assert.Equal(-1, World.StrikesLeft(d));
        Assert.InRange(d.Def.Speed.Raw * World.TicksPerSecond, 6 * Fixed.OneRaw - 20, 6 * Fixed.OneRaw); // 6 cells/s
        var igla = c.Components.Single(x => x.Id == "igla");
        var sam = c.Components.Single(x => x.Id == "sam_missiles");
        static InterceptHit? Hit(Component comp) =>
            comp.Effects.OfType<WeaponEffect>().Single().Interceptor!.HitOn(InterceptClass.Overseer);
        Assert.Equal(new InterceptHit(InterceptClass.Overseer, 45, 55), Hit(igla));
        Assert.Equal(new InterceptHit(InterceptClass.Overseer, 65, 100), Hit(sam));
        Assert.Null(igla.Effects.OfType<WeaponEffect>().Single().Interceptor!.HitOn(InterceptClass.Watcher)); // the Watcher keeps its kill model
    }

    [Fact]
    public void ItsOwnStationLaunchesItAndItOrbitsHigherAndSlower()
    {
        var w = Arena.NewSentinel(N);
        var station = w.Spawn(1, "overseer_station", 20, 100);
        var watcherStation = w.Spawn(1, "watcher_station", 20, 130);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(120, 100), ability: "overseer_launch");
        w.Run(2);
        var drone = Assert.Single(w.All("overseer_drone"));
        Assert.Equal(station.Id, drone.Source);
        Assert.True(drone.Altitude.Raw < 3 * 512, $"takes off from the apron: {drone.Altitude.Raw}");
        Assert.Equal([0], station.Charges); // one launch, the Overseer's, used
        w.Run(station.Cooldown + 2);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(60, 160), ability: "watcher_launch");
        w.Run(2);
        Assert.Empty(w.All("watcher_drone")); // one drone type per station: no Watcher from here
        w.Order(1, CommandKind.UseAbility, [watcherStation], cell: new Cell(60, 160), ability: "overseer_launch");
        w.Run(2);
        Assert.Single(w.All("overseer_drone")); // nor an Overseer from the Watcher's station
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(60, 160), ability: "overseer_launch");
        w.Run(2);
        Assert.Single(w.All("overseer_drone")); // the one Overseer is aloft: nothing more launches
        w.Order(1, CommandKind.UseAbility, [watcherStation], cell: new Cell(60, 160), ability: "watcher_launch");
        w.Run(2);
        Assert.Single(w.All("watcher_drone")); // the Watcher comes from its own station
        w.Run(2000); // the climb (31 s) and the 100-cell flight
        var center = new Cell(120, 100).Center();
        long lo = long.MaxValue, hi = 0, flown = 0;
        for (var i = 0; i < 600; i++)
        {
            var before = drone.Pos;
            w.Step();
            flown += (drone.Pos - before).Len().Raw;
            var r = (drone.Pos - center).Len().Raw;
            (lo, hi) = (Math.Min(lo, r), Math.Max(hi, r));
            Assert.Equal(Height, drone.Altitude.Raw); // flat ground: it holds 250 m
        }
        Assert.InRange(Cells(lo), 22, 31); // the 30-cell circle (it settles a little inside)
        Assert.InRange(Cells(hi), 25, 33);
        Assert.InRange(flown * 20 / 600 / Fixed.OneRaw, 5, 6); // 6 cells/s
        var watcher = Assert.Single(w.All("watcher_drone"));
        Assert.True(drone.Altitude.Raw > watcher.Altitude.Raw + 90 * 512, "100 m above the Watcher");
    }

    [Fact]
    public void EnduranceIsFortyMinutesThenItGoesHomeAndTheStationRelaunchesAnOverseer()
    {
        var w = Arena.NewSentinel(N, 6);
        var station = w.Spawn(1, "overseer_station", 30, 100);
        w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(120, 100), ability: "overseer_launch");
        w.Run(2);
        var drone = Assert.Single(w.All("overseer_drone"));
        Assert.InRange(drone.Lifetime, 40 * 60 * World.TicksPerSecond - 3, 40 * 60 * World.TicksPerSecond);
        var homingAt = -1;
        var recovered = false;
        for (var t = 0; t < 48100 && !recovered; t++)
        {
            var low = drone.Altitude.Raw;
            w.Step();
            if (homingAt < 0 && drone.Homing)
            {
                homingAt = t;
            }
            recovered = drone.Recovered;
            if (recovered)
            {
                Assert.True(low <= 4 * 512, $"it lands on the apron, not from 250 m: {low}");
            }
        }
        Assert.True(recovered, "back at its station, not lost");
        Assert.InRange(homingAt, 45000, 47800); // 40 minutes less the flight home and the descent
        Assert.True(station.RelaunchPending);
        Assert.Equal(drone.Def.Archetype.Index, station.RelaunchArchetype);
        Assert.Equal([1], station.Charges); // the drone is the station's again
        w.Run(station.Cooldown + 3);
        var fresh = Assert.Single(w.All("overseer_drone"));
        Assert.NotEqual(drone.Id, fresh.Id);
        Assert.Empty(w.All("watcher_drone")); // the Overseer is relaunched, nothing else
        Assert.Equal([0], station.Charges);
        Assert.Equal(new Cell(120, 100), Cell.Of(fresh.OrbitAt));
        Assert.False(station.RelaunchPending);
    }

    [Fact]
    public void ItSeesFiftyCellsRoundThePointUnderItOverEverything()
    {
        var w = Arena.NewSentinel(N, 7, s => s with { Fog = true });
        var d = Overhead(w, 130, 130);
        var reds = new List<Unit>();
        for (var r = 0; r <= 80; r += 4)
        {
            reds.Add(w.Spawn(2, "Rifleman", 130 + r, 130));
            reds.Add(w.Spawn(2, "Rifleman", 130, 130 + r));
        }
        int seenNear = 0, seen45 = 0, checkedFar = 0;
        for (var i = 0; i < 400; i++)
        {
            var at = d.Pos; // vision runs first in a tick, from where it was
            w.Step();
            foreach (var u in reds)
            {
                var cells = Cells((u.Pos - at).Len().Raw);
                var seen = World.IsVisibleTo(u, 1);
                if (cells <= 47)
                {
                    Assert.True(seen, $"tick {i}: {cells} cells from the point under it, unseen");
                    seenNear++;
                    seen45 += cells >= 40 ? 1 : 0; // beyond the Watcher's 35
                }
                else if (cells >= 53)
                {
                    Assert.False(seen, $"tick {i}: {cells} cells off, seen");
                    checkedFar++;
                }
            }
        }
        Assert.True(seenNear > 0 && seen45 > 0 && checkedFar > 0);
    }

    [Fact]
    public void AnAaSoldierGetsAShotOnlyWithinAboutSixtyCellsOfThePointUnderIt()
    {
        // It orbits 30 cells round (100, 130): a soldier 60 cells east of the centre is 30 to 90
        // cells from the point under it, and fires only on the near side; one 110 cells east (80 to
        // 140) sees it with his 150 cells of air vision now and then but never has it in reach.
        var w = Arena.NewSentinel(N, 12);
        var d = Overhead(w, 100, 130);
        d.HP = 100_000; // it must not come down: this test measures where he fires from
        var near = w.Spawn(2, "manpads", 160, 130);
        var far = w.Spawn(2, "manpads", 210, 130);
        int nearAmmo = near.Charges[0], farAmmo = far.Charges[0];
        var shots = new List<long>();
        var farTracked = false;
        for (var i = 0; i < 4000; i++)
        {
            w.Step();
            farTracked |= far.AirTarget == d.Id;
            if (near.Charges[0] < nearAmmo)
            {
                nearAmmo = near.Charges[0];
                shots.Add((d.Pos - near.Pos).Len().Raw);
            }
        }
        Assert.NotEmpty(shots);
        Assert.All(shots, s => Assert.True(s <= 65 * Fixed.OneRaw, $"fired at {Cells(s)} cells horizontal"));
        Assert.Equal(farAmmo, far.Charges[0]); // never in his slant range
        Assert.True(farTracked, "he sees it (air vision) but it flies too high for his missile");
    }

    [Fact]
    public void TwoAaSoldierHitsBringItDown()
    {
        int hits = 0, fuses = 0, downed = 0;
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var w = Arena.NewSentinel(N, seed);
            var d = Overhead(w, 100, 100);
            w.Spawn(2, "manpads", 104, 100);
            w.Spawn(2, "manpads", 100, 104);
            w.Spawn(2, "manpads", 96, 100);
            var hp = new List<int>();
            var bursts = new List<AirBurst>();
            for (var i = 0; i < 4000 && d.Alive; i++)
            {
                w.Step();
                foreach (var b in w.AirBursts.Where(b => b.Target == d.Id && b.Kind is AirBurstKind.Kill or AirBurstKind.Miss or AirBurstKind.Hit))
                {
                    bursts.Add(b);
                    hp.Add(d.HP);
                }
            }
            Assert.All(bursts, b => Assert.Equal(450, b.KillPermille)); // a flat 45% whatever the geometry
            Assert.All(bursts, b => Assert.Equal(b.Roll < 450, b.Kind != AirBurstKind.Miss));
            var landed = bursts.Where(b => b.Kind != AirBurstKind.Miss).ToList();
            hits += landed.Count;
            fuses += bursts.Count;
            if (landed.Count > 0)
            {
                Assert.Equal(AirBurstKind.Hit, landed[0].Kind); // the first hit leaves it flying
                Assert.Equal(150 - 82, hp[bursts.IndexOf(landed[0])]); // 55% of 150
            }
            if (landed.Count > 1)
            {
                Assert.Equal(AirBurstKind.Kill, landed[1].Kind); // the second brings it down
                Assert.False(d.Alive);
                Assert.Equal(2, landed.Count);
                downed++;
            }
        }
        Assert.True(downed >= 3, $"downed in {downed} of 6");
        Assert.True(hits > 0 && fuses > hits);
    }

    [Fact]
    public void TheSoldiersHitChanceIsAboutFortyFivePercent()
    {
        // A tough test Overseer (it never comes down) under six soldiers: about 45% of the fuses hit.
        int hits = 0, fuses = 0;
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var w = Arena.NewSentinel(N, seed);
            var d = Overhead(w, 100, 100);
            d.HP = 100_000;
            for (var k = 0; k < 6; k++)
            {
                w.Spawn(2, "manpads", 94 + 2 * k, 100 + k % 2 * 4);
            }
            var bursts = Fuses(w, d, 5000);
            fuses += bursts.Count;
            hits += bursts.Count(b => b.Kind == AirBurstKind.Hit);
            Assert.All(bursts.Where(b => b.Kind == AirBurstKind.Hit), b => Assert.True(b.Roll < 450));
        }
        Assert.True(fuses >= 40, $"{fuses} fuses");
        Assert.InRange(hits * 100 / fuses, 33, 57);
    }

    [Fact]
    public void OneAirDefenceVehicleHitBringsItDown()
    {
        var downed = 0;
        for (ulong seed = 1; seed <= 4; seed++)
        {
            var w = Arena.NewSentinel(N, seed);
            var d = Overhead(w, 100, 100);
            w.SpawnCrewed(2, "sam_vehicle", 110, 100);
            var bursts = Fuses(w, d, 4000);
            Assert.NotEmpty(bursts);
            Assert.All(bursts, b => Assert.Equal(650, b.KillPermille));
            Assert.DoesNotContain(bursts, b => b.Kind == AirBurstKind.Hit); // a hit kills
            var first = bursts.FindIndex(b => b.Kind == AirBurstKind.Kill);
            if (first >= 0)
            {
                Assert.Equal(bursts.Count - 1, first); // the first hit is the last fuse
                Assert.False(d.Alive);
                downed++;
            }
        }
        Assert.True(downed >= 3, $"downed in {downed} of 4");
    }

    [Fact]
    public void RiflesAndSmallArmsCannotReachIt()
    {
        var w = Arena.NewSentinel(N, 3);
        var d = Overhead(w, 100, 100);
        var reds = new List<Unit>();
        foreach (var name in new[] { "Rifleman", "Gunner", "Drone hunter" })
        {
            for (var k = 0; k < 3; k++)
            {
                var u = w.Spawn(2, name, 96 + 4 * k, 100 + (reds.Count % 3) * 3);
                u.Stance = Stance.Aggressive;
                reds.Add(u);
            }
        }
        foreach (var u in reds)
        {
            Assert.All(u.Def.Weapons, wpn => Assert.False(World.CanHit(wpn, d), $"{u.Def.Archetype.Id}: weapon {wpn.Index}"));
        }
        w.Order(2, CommandKind.Attack, [.. reds], target: d.Id);
        w.Run(600);
        Assert.Equal(150, d.HP);
        Assert.All(reds, u => Assert.Equal(0u, u.LastShot));
    }

    [Fact]
    public void ItHomesLandsParksAndTakesOffAtAnAirfieldLikeTheWatcher()
    {
        var w = Arena.NewSentinel(200);
        var af = AirfieldFlightTests.Field(w, [(0, 0)]);
        var d = w.Spawn(1, "overseer_drone", 170, 160);
        w.Run(5);
        d.Lifetime = 3000; // bingo soon: home to refuel (it has 250 m to come down)
        d.HP = 140;
        var id = d.Id;
        var orbit = d.OrbitAt;
        var phases = new List<FlightPhase>();
        var t = 0;
        for (; t < 14000; t++)
        {
            w.Step();
            var phase = World.FlightPhaseOf(d);
            if (phases.Count == 0 || phases[^1] != phase)
            {
                phases.Add(phase);
            }
            if (phases.Count > 2 && phase == FlightPhase.Airborne)
            {
                break;
            }
        }
        Assert.True(t < 14000, $"back in the air (phases: {string.Join(", ", phases)})");
        Assert.Equal(FlightPhase.Airborne, phases[0]);
        var order = new[] { FlightPhase.Holding, FlightPhase.Final, FlightPhase.Rollout, FlightPhase.TaxiIn, FlightPhase.Parked,
                            FlightPhase.TaxiOut, FlightPhase.LineUp, FlightPhase.TakeoffRoll, FlightPhase.Climb, FlightPhase.Airborne };
        var at = 0;
        foreach (var p in phases)
        {
            at += at < order.Length && p == order[at] ? 1 : 0;
        }
        Assert.True(at == order.Length, $"phases: {string.Join(", ", phases)}");
        Assert.Same(d, w.Unit(id)); // the same unit, its ID kept
        Assert.True(d.Lifetime > d.Def.Lifetime - 300, "it left with a full tank");
        Assert.Equal(0u, af.RunwayLock);
        Assert.Null(d.AirOps);
        w.Run(2500);
        Assert.Equal(Height, d.Altitude.Raw); // back up at 250 m
        Assert.InRange(Cells((d.Pos - orbit).Len().Raw), 18, 36); // orbiting where it was
    }

    [Fact]
    public void ItIsDeterministic()
    {
        static ulong Run()
        {
            var w = Arena.NewSentinel(N, 11, s => s with { Fog = true });
            var station = w.Spawn(1, "overseer_station", 30, 100);
            w.Order(1, CommandKind.UseAbility, [station], cell: new Cell(140, 100), ability: "overseer_launch");
            var d = Overhead(w, 100, 100);
            w.Spawn(2, "manpads", 104, 100);
            w.SpawnCrewed(2, "sam_vehicle", 180, 100);
            w.Spawn(2, "Rifleman", 100, 120);
            w.Run(1500);
            return w.Hash() ^ (ulong)d.HP;
        }
        Assert.Equal(Run(), Run());
    }

    [Theory]
    [InlineData("\"hits\": { \"overseer\": { \"chance\": 45, \"damage\": 55 } }", "\"hits\": { \"tank\": { \"chance\": 45, \"damage\": 55 } }", "\"tank\" must be one of its classes")]
    [InlineData("\"hits\": { \"overseer\": { \"chance\": 45, \"damage\": 55 } }", "\"hits\": { \"overseer\": { \"chance\": 145, \"damage\": 55 } }", "hits.overseer.chance")]
    [InlineData("\"hits\": { \"overseer\": { \"chance\": 45, \"damage\": 55 } }", "\"hits\": { \"overseer\": { \"chance\": 45, \"damage\": 0 } }", "hits.overseer.damage")]
    public void RejectsBadHitRules(string find, string replace, string want)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Name == "components/infantry_weapons.json");
        var json = sources[i].Json.Replace("\r\n", "\n");
        Assert.Contains(find, json, StringComparison.Ordinal);
        sources[i] = sources[i] with { Json = json.Replace(find, replace, StringComparison.Ordinal) };
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.Contains(want, e.Message, StringComparison.Ordinal);
    }
}
