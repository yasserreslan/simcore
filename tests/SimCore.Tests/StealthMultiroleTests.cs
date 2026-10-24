using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The stealth multirole (an F-35 type; the game repo's f35 spec, owner 2026-10-24): built only by the main air base's hangar, no cap.
// Two loadout modes changed only parked at an airfield (CommandKind.SetLoadout, 10 s): "stealth" (2 heavy bombs, 10 air-to-air
// missiles, 1.3x the multirole's speeds, detect 35 / lock 250 / kill 65) and "heavy" (10 bombs, 2 missiles, the multirole's speeds,
// detect 80 / lock 130 / kill 90); heavy bombs 30% stronger than the multirole's 2,000 lb with a bigger blast; 8 flare salvos; radar
// spotting: an enemy air radar whose coverage it flies in is revealed to its side (the radar and 2 cells round it) until 10 s after.
public class StealthMultiroleTests(ITestOutputHelper output)
{
    private const string F35 = "stealth_multirole";
    private const long Cell1 = Fixed.OneRaw;
    private const int M = 512; // raw per metre
    private const int Stealth = 0, Heavy = 1;

    // A core at (40, 120) facing 0 (MainAirBaseTests): the runway on y 120-135 from x 40 to 220, the hangar x 44-148 / y 164-190,
    // the rows' column x 188-216 from y 154.
    private static readonly Cell CoreCell = new(40, 120);

    private static World Sentinel(int n, bool fog = false) => Arena.NewSentinel(n, 1, fog ? s => s with { Fog = true } : null);

    private static UnitDef Def(World w, string id, byte player = 1) => w.Player(player)!.Setup.Find(w.Content, id)!;

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, int index = 0, uint target = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Index = index, Target = target });

    private static int RunUntil(World w, Func<bool> done, int max)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            if (done())
            {
                return t;
            }
        }
        return -1;
    }

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static double Cells(long raw) => raw / (double)Cell1;

    // A flying stealth multirole of player 1 at (x, y), at its height already, in mode `mode` (as if changed on the ground).
    private static Unit Fly(World w, int x, int y, int facing = Facing.East, int mode = Stealth)
    {
        var u = w.Spawn(1, F35, x, y, facing);
        SetMode(u, mode);
        w.Step();
        return u;
    }

    // SetMode puts u in mode m with its full load (what a finished change does).
    private static void SetMode(Unit u, int m)
    {
        u.Loadout = m;
        for (var i = 0; i < u.Charges.Count; i++)
        {
            u.MutableCharges[i] = u.StockMax(i);
        }
    }

    private static (int Bombs, int Missiles) Load(Unit u)
    {
        var bombs = u.Def.Weapons.Single(x => x.Effect.Delivery is not null).Stock;
        var aam = u.Def.Weapons.Single(x => x.Effect.Interceptor is not null).Stock;
        return (u.Charges[bombs], u.Charges[aam]);
    }

    private static (int Bombs, int Missiles) Capacity(Unit u)
    {
        var bombs = u.Def.Weapons.Single(x => x.Effect.Delivery is not null).Stock;
        var aam = u.Def.Weapons.Single(x => x.Effect.Interceptor is not null).Stock;
        return (u.StockMax(bombs), u.StockMax(aam));
    }

    // A main air base of player 1 and a stealth multirole its hangar built, parked at its station (held until its first order).
    private static (World W, Airfield Af, Unit Jet) Parked()
    {
        var w = Sentinel(360);
        var af = w.SpawnAirBase(Def(w, "airbase_core"), CoreCell, 0, []) ?? throw new InvalidOperationException("no base");
        var hangar = w.Unit(af.Hangar)!;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [hangar.Id], Unit = F35 });
        Unit? jet = null;
        Assert.True(RunUntil(w, () => (jet = w.Units.FirstOrDefault(u => u.Def.Archetype.Id == F35)) is { AirOps.Phase: FlightPhase.Parked }, 3000) > 0,
            "the hangar builds it into a station");
        return (w, af, jet!);
    }

    // ---- Content ----

    [Fact]
    public void TheStealthMultiroleIsData()
    {
        var c = TestContent.Mvp;
        var w = Sentinel(64);
        var d = Def(w, F35);
        var a = d.Archetype;
        Assert.Equal("Stealth multirole", a.Name);
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        Assert.Equal((AircraftClass.M, 1100, 3200, 300), (a.Aircraft!.SizeClass, a.BuildTime, a.Cost, d.MaxHp));
        Assert.Equal(-1, a.Exclusive); // no cap
        Assert.Equal(InterceptClass.Fighter, a.Flight!.Class);
        // Two modes, stealth first (the one it is built in); a change takes 10 s.
        Assert.Equal(["stealth", "heavy"], a.Loadouts.Select(x => x.Id));
        Assert.Equal(200, a.LoadoutSwitchTicks);
        Assert.Equal((130, 100), (a.Loadouts[Stealth].SpeedPercent, a.Loadouts[Heavy].SpeedPercent));
        Assert.Equal(new Signature(35, 250, 65), a.Loadouts[Stealth].Signature); // owner: between the fighter's 50 / 200 and the B-2's 25 / 300
        Assert.Equal(new Signature(80, 130, 90), a.Loadouts[Heavy].Signature);
        var u = w.Spawn(1, F35, 30, 30);
        Assert.Equal(Stealth, u.Loadout);
        Assert.Equal((2, 10), Capacity(u));
        Assert.Equal((2, 10), Load(u));
        Assert.Equal(new Signature(35, 250, 65), u.Signature);
        SetMode(u, Heavy);
        Assert.Equal((10, 2), Capacity(u));
        Assert.Equal((10, 2), Load(u));
        Assert.Equal(new Signature(80, 130, 90), u.Signature);
        // Flares in both modes, the multirole's 8 (owner); no chaff.
        var mr = c.Archetype("multirole_jet")!;
        Assert.Equal([(CountermeasureKind.Flare, 8)], a.Countermeasures.Select(x => (x.Kind, x.Salvos)));
        Assert.Equal(8, mr.Countermeasures.Single(x => x.Kind == CountermeasureKind.Flare).Salvos);
        Assert.Equal([8], u.Salvos);
        // Its heavy bombs: the multirole's 2,000 lb with 30% more damage and a bigger blast; tier 6 for renderers.
        var bomb = u.Def.Weapons.Single(x => x.Effect.Delivery is not null).Effect;
        var mk84 = c.Components.Single(x => x.Id == "bombs_2000lb").Effects.OfType<WeaponEffect>().Single();
        Assert.Equal(mk84.Damage * 13 / 10, bomb.Damage);
        Assert.Equal(1690, bomb.Damage);
        Assert.True(bomb.Splash.Raw > mk84.Splash.Raw && bomb.SplashInner.Raw > mk84.SplashInner.Raw, "a bigger blast");
        Assert.Equal((4 * Cell1, 10 * Cell1), (bomb.SplashInner.Raw, bomb.Splash.Raw));
        Assert.Equal(mk84.Warhead, bomb.Warhead);
        Assert.Equal(6, World.StrikeTier(bomb.Damage));
        Assert.Equal(["single", "pair"], World.FireModesOf(u).Select(x => x.Id));
        output.WriteLine($"bomb {bomb.Damage} (2,000 lb {mk84.Damage}), blast {Cells(bomb.SplashInner.Raw)} / {Cells(bomb.Splash.Raw)} cells, tier {World.StrikeTier(bomb.Damage)}");
        // Its air-to-air missiles: the fighter's radar missile.
        var aam = u.Def.Weapons.Single(x => x.Effect.Interceptor is not null).Effect;
        Assert.Equal(Seeker.Radar, aam.Interceptor!.Seeker);
        // Radar spotting: air radars (the mobile radar, the air defence vehicle); not the AA soldier, not the SA-6 launcher (no radar).
        var rs = a.RadarSpotting!;
        Assert.Equal((200, 2 * Cell1), (rs.LingerTicks, rs.Radius.Raw));
        foreach (var (id, radar) in new[] { ("mobile_radar", true), ("sam_vehicle", true), ("manpads", false), ("kub_launcher", false) })
        {
            Assert.Equal(radar, c.Archetype(id)!.Tags.Matches(rs.Tags) && c.Archetype(id)!.AirVision.Raw > 0);
        }
    }

    [Fact]
    public void OnlyTheMainAirBasesHangarBuildsIt()
    {
        var (w, af, jet) = Parked();
        Assert.Equal(StationKind.Normal, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == jet.AirOps!.Station).Kind); // a stand
        Assert.True(jet.AirOps!.Hold);
        var forward = w.SpawnAirfield(Def(w, "airfield_facility"), new Cell(60, 300), 0, [(0, 0)])!;
        var fac = w.Unit(forward.Facility)!;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [fac.Id], Unit = F35 });
        w.Step();
        Assert.Empty(fac.ProductionQueue);
        Assert.DoesNotContain(fac.Bays, b => b.Def >= 0);
        var tags = jet.Def.Archetype.Tags;
        Assert.False(fac.Def.Archetype.Production!.Builds(tags)); // the forward airfield's facility excludes it
        Assert.True(fac.Def.Archetype.Production.Builds(w.Content.Archetype("multirole_jet")!.Tags)); // (it still builds the jets)
        Assert.True(w.Unit(af.Hangar)!.Def.Archetype.Production!.Builds(tags));
    }

    // ---- Speeds ----

    [Fact]
    public void StealthIsTheFastestJetHeavyFliesTheMultirolesSpeeds()
    {
        double Cruise(string unit, int mode)
        {
            var w = Sentinel(500); // up to 512 cells: the content's speeds (E13, D-SPD1)
            var u = unit == F35 ? Fly(w, 40, 250, Facing.East, mode) : w.Spawn(1, unit == "multirole_jet" ? "Multirole 500 lb" : unit, 40, 250, Facing.East);
            Issue(w, CommandKind.Move, u, new Cell(480, 250));
            w.Run(15 * World.TicksPerSecond);
            return u.Wing!.Speed / (double)Cell1;
        }
        var stealth = Cruise(F35, Stealth);
        var heavy = Cruise(F35, Heavy);
        var multirole = Cruise("multirole_jet", 0);
        var fighter = Cruise("fighter_jet", 0);
        output.WriteLine($"cruise: stealth {F(stealth)}, heavy {F(heavy)}, multirole {F(multirole)}, fighter {F(fighter)} cells/s");
        Assert.InRange(stealth, 25.5, 26.5); // 1.3x the multirole's 20
        Assert.InRange(heavy, multirole - 0.3, multirole + 0.3);
        Assert.True(stealth > fighter + 4, "faster than the air superiority fighter");
        // Its dash and attack speeds too (the envelope this map flies), and its fly zone holds its wider turns.
        var w2 = Sentinel(300);
        var s = Fly(w2, 100, 100, Facing.East, Stealth);
        var h = Fly(w2, 150, 100, Facing.East, Heavy);
        var fj = w2.Spawn(1, "fighter_jet", 200, 100, Facing.East);
        var (gs, gh, gf) = (w2.FlightOf(s), w2.FlightOf(h), w2.FlightOf(fj));
        Assert.Equal(gh.Wing!.Dash * 13 / 10, gs.Wing!.Dash);
        Assert.True(gs.Wing.Dash > gf.Wing!.Dash, "the fastest dash in the game");
        Assert.True(gs.FlyZoneMarginRaw >= 2 * gs.Wing.Radius(gs.Wing.Cruise) + gs.Wing.Cruise);
        Assert.True(w2.FlyZoneMargin >= gs.FlyZoneMarginRaw);
        output.WriteLine($"dash: stealth {F(Cells(gs.Wing.Dash))}, heavy {F(Cells(gh.Wing.Dash))}, fighter {F(Cells(gf.Wing.Dash))}; " +
            $"turn at cruise: stealth {F(Cells(gs.Wing.Radius(gs.Wing.Cruise)))}, heavy {F(Cells(gh.Wing.Radius(gh.Wing.Cruise)))} cells");
    }

    // ---- Signature ----

    // TrackedAt: is a stealth multirole in mode `mode` at about dx cells of slant from sensor s (at its eye's height) tracked by s's side?
    private static bool TrackedAt(Func<World, (Unit Sensor, long EyeRaw)> sensor, int dx, int mode, bool duel = false)
    {
        var w = duel ? Duel(600) : Sentinel(600);
        var (s, eye) = sensor(w);
        var b = w.Spawn(1, F35, 150 + dx, 200, Facing.North);
        SetMode(b, mode);
        b.Altitude = Fixed.FromRaw(eye);
        w.Order(1, CommandKind.Move, [b], cell: new Cell(150 + dx, 200));
        w.Run(1);
        return w.AirTracked(s.Owner, b.Id);
    }

    // Both players Sentinel (a fighter as the sensor).
    private static World Duel(int n)
    {
        var content = TestContent.Mvp;
        var setup = TestContent.Sentinel(1).For(content, 1);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        return new World(new Grid(n, n), MatchCompiler.Compile(content, setup with { Players = [p0, p1] }));
    }

    private static (Unit, long) MobileRadar(World w) => MobileRadarAt(w, 150, 200);

    private static (Unit, long) MobileRadarAt(World w, int x, int y)
    {
        var r = w.SpawnCrewed(2, "mobile_radar", x, y).Vehicle;
        w.Order(2, CommandKind.Deploy, [r]);
        w.Run(r.Def.Archetype.DeployTicks + 2);
        return (r, w.RadarEyeRaw(r));
    }

    private static (Unit, long) AirDefenceVehicle(World w)
    {
        var v = w.SpawnCrewed(2, "sam_vehicle", 150, 200).Vehicle;
        w.Run(2);
        return (v, w.RadarEyeRaw(v) > 0 ? w.RadarEyeRaw(v) : 3 * M);
    }

    private static (Unit, long) Fighter(World w)
    {
        var j = w.Spawn(2, "fighter_jet", 150, 200, Facing.North);
        j.Altitude = Fixed.FromRaw(300 * M);
        w.Order(2, CommandKind.Move, [j], cell: new Cell(150, 200));
        return (j, 300 * M);
    }

    [Theory]
    [InlineData("mobile radar", 400)]
    [InlineData("air defence vehicle", 260)]
    [InlineData("fighter jet", 160)]
    public void EachModeIsDetectedAtItsShareOfEverySensorsRange(string sensor, int range)
    {
        Func<World, (Unit, long)> s = sensor switch { "mobile radar" => MobileRadar, "air defence vehicle" => AirDefenceVehicle, _ => Fighter };
        var duel = sensor == "fighter jet";
        foreach (var (mode, pct) in new[] { (Stealth, 35), (Heavy, 80) })
        {
            var at = range * pct / 100;
            Assert.False(TrackedAt(s, at + 4, mode, duel), $"{sensor}: mode {mode} beyond {at}");
            Assert.True(TrackedAt(s, at - 4, mode, duel), $"{sensor}: mode {mode} within {at}");
            output.WriteLine($"{sensor} ({range} cells): {(mode == Stealth ? "stealth" : "heavy")} seen within {at}");
        }
    }

    [Fact]
    public void ItsLockTakesItsModesShareOfTheLockTime()
    {
        // The air defence vehicle's lock: 1 s on a normal aircraft; 2.5 s on stealth mode, 1.3 s on heavy mode.
        foreach (var (mode, want) in new[] { (Stealth, 50), (Heavy, 26) })
        {
            var w = Sentinel(400);
            var v = w.SpawnCrewed(2, "sam_vehicle", 150, 200).Vehicle;
            var b = Fly(w, 160, 205, Facing.East, mode);
            Issue(w, CommandKind.Move, b, new Cell(160, 205));
            var ticks = 0;
            for (var t = 0; t < 600 && ticks == 0; t++)
            {
                w.Step();
                if (v.AirTarget == b.Id)
                {
                    ticks = w.AirLockTicks(v);
                }
            }
            output.WriteLine($"mode {mode}: lock {ticks} ticks");
            Assert.Equal(want, ticks);
        }
    }

    // ---- The mode change ----

    [Fact]
    public void ParkedItChangesModeInTenSecondsWithTheNewModesFullLoad()
    {
        var (w, _, jet) = Parked();
        Assert.Equal((Stealth, (2, 10)), (jet.Loadout, Load(jet)));
        Assert.True(w.LoadoutOf(jet)!.Value.CanSwitch);
        Issue(w, CommandKind.SetLoadout, jet, index: Heavy);
        w.Step();
        var start = w.Tick;
        Assert.Equal((Stealth, Heavy, 199), (jet.Loadout, jet.LoadoutTo, jet.LoadoutLeft));
        Assert.Equal(("stealth", Heavy, 199, 200), (w.LoadoutOf(jet)!.Value.Id, w.LoadoutOf(jet)!.Value.SwitchTo, w.LoadoutOf(jet)!.Value.SwitchLeft, w.LoadoutOf(jet)!.Value.SwitchTicks));
        var done = RunUntil(w, () => jet.Loadout == Heavy, 400);
        output.WriteLine($"heavy after {w.Tick - start + 1} ticks");
        Assert.Equal(200u, w.Tick - start + 1); // the order's tick counts: 10 s
        Assert.Equal((-1, 0), (jet.LoadoutTo, jet.LoadoutLeft));
        Assert.Equal((10, 2), Load(jet));
        Assert.Equal([8], jet.Salvos);
        Assert.Equal(FlightPhase.Parked, jet.AirOps!.Phase);
        // And back: 2 bombs, 10 missiles again.
        Issue(w, CommandKind.SetLoadout, jet, index: Stealth);
        Assert.True(RunUntil(w, () => jet.Loadout == Stealth, 400) > 0);
        Assert.Equal((2, 10), Load(jet));
    }

    [Fact]
    public void NeverInTheAirNorTaxiing()
    {
        var w = Sentinel(400);
        var u = Fly(w, 100, 200);
        Assert.False(w.LoadoutOf(u)!.Value.CanSwitch);
        Issue(w, CommandKind.SetLoadout, u, index: Heavy);
        w.Run(400);
        Assert.Equal((Stealth, -1, 0, (2, 10)), (u.Loadout, u.LoadoutTo, u.LoadoutLeft, Load(u)));
        // Leaving its station (pushed back, taxiing out): refused too.
        var (w2, _, jet) = Parked();
        Issue(w2, CommandKind.Move, jet, new Cell(300, 300));
        Assert.True(RunUntil(w2, () => jet.AirOps is { Phase: FlightPhase.PushBack or FlightPhase.TaxiOut }, 600) > 0);
        Issue(w2, CommandKind.SetLoadout, jet, index: Heavy);
        w2.Run(300);
        Assert.Equal((Stealth, 0), (jet.Loadout, jet.LoadoutLeft));
        // Every other aircraft ignores the order.
        var m = w.Spawn(1, "Multirole 500 lb", 200, 200);
        Issue(w, CommandKind.SetLoadout, m, index: 1);
        w.Step();
        Assert.Null(w.LoadoutOf(m));
        Assert.Equal((0, 0), (m.Loadout, m.LoadoutLeft));
    }

    [Fact]
    public void AnOrderDuringTheChangeLaunchesItOnceDone()
    {
        var (w, _, jet) = Parked();
        Issue(w, CommandKind.SetLoadout, jet, index: Heavy);
        w.Run(50);
        Issue(w, CommandKind.Move, jet, new Cell(300, 300));
        var left = jet.LoadoutLeft;
        var t = RunUntil(w, () => jet.AirOps is not { Phase: FlightPhase.Parked }, 400);
        output.WriteLine($"ordered with {left} ticks to go; it left its station {t} ticks later");
        Assert.True(t >= left - 2, "held at its station until the change is done");
        Assert.Equal((Heavy, (10, 2)), (jet.Loadout, Load(jet)));
    }

    [Fact]
    public void OrderingItsOwnModeCancelsTheChange()
    {
        var (w, _, jet) = Parked();
        Issue(w, CommandKind.SetLoadout, jet, index: Heavy);
        w.Run(100);
        Issue(w, CommandKind.SetLoadout, jet, index: Stealth);
        w.Run(300);
        Assert.Equal((Stealth, -1, 0, (2, 10)), (jet.Loadout, jet.LoadoutTo, jet.LoadoutLeft, Load(jet)));
    }

    [Fact]
    public void AtAForwardAirfieldsStandToo()
    {
        var w = Sentinel(240);
        var af = w.SpawnAirfield(Def(w, "airfield_facility"), new Cell(109, 50), 0, [(0, 0)], 1, 1) ?? throw new InvalidOperationException("no airfield");
        var jet = w.Spawn(1, F35, 215, 70, Facing.East);
        jet.Homing = true; // home at once (a refuel stop)
        Assert.True(RunUntil(w, () => jet.AirOps is { Phase: FlightPhase.Parked }, 9000) > 0, "it lands at the forward airfield");
        Assert.Equal(af.Id, jet.AirOps!.Airfield);
        Issue(w, CommandKind.SetLoadout, jet, index: Heavy);
        Assert.True(RunUntil(w, () => jet.Loadout == Heavy, 400) > 0);
        Assert.Equal((10, 2), Load(jet));
    }

    [Fact]
    public void ARearmFillsToItsModesLoad()
    {
        var (w, _, jet) = Parked();
        Issue(w, CommandKind.SetLoadout, jet, index: Heavy);
        Assert.True(RunUntil(w, () => jet.Loadout == Heavy, 400) > 0);
        var bombs = jet.Def.Weapons.Single(x => x.Effect.Delivery is not null).Stock;
        var aam = jet.Def.Weapons.Single(x => x.Effect.Interceptor is not null).Stock;
        (jet.MutableCharges[bombs], jet.MutableCharges[aam]) = (3, 0);
        Issue(w, CommandKind.Rearm, jet);
        Assert.True(RunUntil(w, () => Load(jet) == (10, 2), 3000) > 0, "rearmed to the heavy mode's 10 and 2");
        w.Run(200);
        Assert.Equal((10, 2), Load(jet));
    }

    // ---- Bombs ----

    [Fact]
    public void ItsHeavyBombHitsForThirtyPercentMoreOverABiggerBlast()
    {
        var w = Sentinel(400);
        var u = Fly(w, 60, 200, Facing.East, Heavy);
        Issue(w, CommandKind.SetFireMode, u, index: 1); // pair: two a pass
        Issue(w, CommandKind.AttackGround, u, new Cell(220, 200));
        var impacts = new List<BombImpact>();
        RunUntil(w, () =>
        {
            impacts.AddRange(w.BombImpacts.Where(x => x.Aircraft == u.Id));
            return impacts.Count >= 2;
        }, 4000);
        Assert.Equal(2, impacts.Count);
        Assert.All(impacts, x => Assert.Equal((1690, 6), (x.Damage, x.Tier)));
        Assert.Equal((8, 2), Load(u));
        output.WriteLine("impacts: " + string.Join(", ", impacts.Select(x => $"({F(Cells(x.At.X.Raw))}, {F(Cells(x.At.Y.Raw))})")));
    }

    // ---- Radar spotting ----

    [Fact]
    public void InsideARadarsCoverageItRevealsTheRadarEvenUndetected()
    {
        // A set-up mobile radar (400 cells of coverage); the stealth multirole orbits 300 cells out: the radar detects it within 140
        // (35%) only, yet under fog its side sees the radar, which is 300 cells from any of its units.
        var w = Sentinel(600, fog: true);
        var (radar, _) = MobileRadarAt(w, 100, 300);
        var u = Fly(w, 400, 300);
        Issue(w, CommandKind.Move, u, new Cell(400, 300));
        w.Run(40);
        Assert.False(w.AirTracked(2, u.Id), "the radar does not detect it");
        var spot = w.RadarSpotOf(radar.Id, 1);
        Assert.NotNull(spot);
        Assert.True(spot.Inside);
        Assert.Equal(u.Id, spot.Spotter);
        Assert.True(World.IsSpotted(radar, 1), "its side sees the radar through the fog");
        Assert.True(w.FogOf(1)!.Visible(100, 300), "the radar's cell is visible");
        Assert.Contains(w.RadarSpots, x => x.Radar == radar.Id && x.Player == 1);
        // A player-1 unit may now target it (spotted): a rocket truck's order would take it; the radar is listed for the client.
        output.WriteLine($"revealed since {spot.Since}, until {spot.Until}, by {spot.Spotter}");
    }

    [Fact]
    public void ItStaysRevealedTwoHundredTicksAfterTheAircraftLeftTheCoverage()
    {
        var w = Sentinel(800, fog: true);
        var (radar, _) = MobileRadarAt(w, 100, 400);
        var u = Fly(w, 470, 400);
        Issue(w, CommandKind.Move, u, new Cell(790, 400));
        uint last = 0;
        var revealedAfter = new List<uint>();
        for (var t = 0; t < 600; t++)
        {
            w.Step();
            if (w.RadarSpotOf(radar.Id, 1) is { Inside: true })
            {
                last = w.Tick;
            }
            else if (last != 0 && World.IsSpotted(radar, 1))
            {
                revealedAfter.Add(w.Tick - last);
            }
        }
        Assert.NotEqual(0u, last);
        output.WriteLine($"last inside at tick {last}; revealed {revealedAfter.Count} ticks after");
        Assert.Equal(200, revealedAfter.Count);
        Assert.Equal(200u, revealedAfter[^1]);
        Assert.False(World.IsSpotted(radar, 1));
        Assert.Null(w.RadarSpotOf(radar.Id, 1));
        Assert.False(w.FogOf(1)!.Visible(100, 400), "the fog closes over it again");
    }

    [Fact]
    public void OnlyTheRadarAndASmallDiscRoundItAreRevealed()
    {
        var w = Sentinel(600, fog: true);
        var (radar, _) = MobileRadarAt(w, 100, 300);
        var near = w.SpawnCrewed(2, "sam_vehicle", 101, 301).Vehicle; // within 2 cells
        var far = w.Spawn(2, "Rifleman", 106, 300); // 6 cells out
        var u = Fly(w, 400, 300);
        Issue(w, CommandKind.Move, u, new Cell(400, 300));
        w.Run(40);
        var fog = w.FogOf(1)!;
        Assert.True(World.IsSpotted(radar, 1));
        Assert.True(World.IsSpotted(near, 1), "a unit beside it, in the disc");
        Assert.False(World.IsSpotted(far, 1), "a soldier 6 cells out stays hidden");
        Assert.True(fog.Visible(100, 300) && fog.Visible(102, 300) && fog.Visible(100, 298));
        Assert.False(fog.Visible(104, 300) || fog.Visible(100, 296) || fog.Visible(103, 303));
        var visible = 0;
        for (var y = 280; y <= 320; y++)
        {
            for (var x = 80; x <= 120; x++)
            {
                visible += fog.Visible(x, y) ? 1 : 0;
            }
        }
        output.WriteLine($"{visible} cells visible round the radar");
        Assert.InRange(visible, 9, 13);
    }

    [Fact]
    public void AnAirDefenceVehicleIsRevealedTooNeverAnAaSoldierOrAPackedUpRadar()
    {
        var w = Sentinel(600, fog: true);
        var osa = w.SpawnCrewed(2, "sam_vehicle", 300, 150).Vehicle; // 260 cells of coverage
        var igla = w.Spawn(2, "manpads", 300, 450); // 150 cells of air vision: eyes, not a radar
        var packed = w.SpawnCrewed(2, "mobile_radar", 300, 300).Vehicle; // never set up
        var kub = w.SpawnCrewed(2, "kub_launcher", 305, 300).Vehicle; // no radar of its own
        var u = Fly(w, 400, 300);
        Issue(w, CommandKind.Move, u, new Cell(400, 300)); // 100 to 180 cells from each
        w.Run(60);
        Assert.NotNull(w.RadarSpotOf(osa.Id, 1));
        Assert.True(World.IsSpotted(osa, 1));
        Assert.Null(w.RadarSpotOf(igla.Id, 1));
        Assert.Null(w.RadarSpotOf(packed.Id, 1));
        Assert.Null(w.RadarSpotOf(kub.Id, 1));
        Assert.False(World.IsSpotted(igla, 1));
        Assert.Single(w.RadarSpots);
    }

    [Fact]
    public void ItIsDeterministic()
    {
        ulong Run()
        {
            var w = Sentinel(600, fog: true);
            var (radar, _) = MobileRadarAt(w, 100, 300);
            w.SpawnCrewed(2, "sam_vehicle", 140, 320);
            var (_, _, jet) = (w, 0, Fly(w, 420, 300));
            Issue(w, CommandKind.Move, jet, new Cell(150, 300));
            var h = 0UL;
            for (var t = 0; t < 1200; t++)
            {
                w.Step();
                h = h * 31 + w.Hash();
            }
            Assert.NotNull(radar);
            return h;
        }
        Assert.Equal(Run(), Run());
    }
}
