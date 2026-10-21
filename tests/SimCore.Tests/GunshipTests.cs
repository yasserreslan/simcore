using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The gunship (H3, 2026-10-21; the game repo's docs/heavy-air-plan.md, section 1.2): an AC-130 type of class XL built by
// the main air base's hangar and parked only in its heavy bays; it orbits high above the AA soldier's ceiling (the air
// defence vehicle and the jets reach it, 8 missile hits bring it down); given an area it flies a left-hand pylon turn
// round it and fires its three side cannons (100 light, 20 heavy, 1 very big round) only while on that circle, scattered,
// at what its side sees inside the area; 12 angel-wing flare salvos; all of it deterministic. Also the H1 follow-ups:
// the drones' hangar build times and World.RadarTracks.
public class GunshipTests(ITestOutputHelper output)
{
    private const string Gunship = "gunship";
    private const long Cell1 = Fixed.OneRaw;

    private static World Sentinel(int n, ulong seed = 1) => Arena.NewSentinel(n, seed);

    // Both players Sentinel (aircraft on both sides), no starting army; content c (the MVP's by default).
    private static World Duel(int n, ulong seed, ContentSet? c = null)
    {
        var content = c ?? TestContent.Mvp;
        var setup = TestContent.Sentinel(seed).For(content, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        return new World(new Grid(n, n), MatchCompiler.Compile(content, setup with { Players = [p0, p1] }));
    }

    // The Sentinel lobby (player 2 Resistance) on content c.
    private static World SentinelOn(ContentSet c, int n, ulong seed = 1)
    {
        var setup = TestContent.Sentinel(seed).For(c, seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(n, n), MatchCompiler.Compile(c, setup));
    }

    // The MVP content with one text replaced in the file that has it (exactly once).
    private static ContentSet Edited(params (string From, string To)[] edits)
    {
        var sources = TestContent.MvpSources();
        foreach (var (from, to) in edits)
        {
            var i = sources.FindIndex(x => x.Json.Contains(from, StringComparison.Ordinal));
            Assert.True(i >= 0, $"no file has {from}");
            var json = sources[i].Json;
            var at = json.IndexOf(from, StringComparison.Ordinal);
            sources[i] = sources[i] with { Json = string.Concat(json.AsSpan(0, at), to, json.AsSpan(at + from.Length)) };
        }
        return ContentLoader.Load(sources);
    }

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, uint target = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Target = target });

    private static double Cells(long raw) => raw / (double)Cell1;

    private static long Dist(Vec a, Vec b) => World.Len3(a.X.Raw - b.X.Raw, a.Y.Raw - b.Y.Raw, 0);

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static GunshipSpec Spec(World w) => w.Content.Archetype(Gunship)!.Gunship!;

    // A flying gunship of player 1 at (x, y), at its height already (the sandbox's put-down rule), heading east.
    private static Unit Fly(World w, int x, int y)
    {
        var g = w.Spawn(1, Gunship, x, y, Facing.East);
        w.Step();
        return g;
    }

    // ---- Content ----

    [Fact]
    public void TheGunshipIsData()
    {
        var c = TestContent.Mvp;
        var w = Sentinel(64);
        var d = w.Player(1)!.Setup.Find(c, Gunship)!;
        var a = d.Archetype;
        Assert.Equal("Gunship", a.Name);
        Assert.Equal((AircraftClass.XL, 1800, 1600), (a.Aircraft!.SizeClass, a.BuildTime, d.MaxHp));
        Assert.Equal("gunship", c.AirClasses[(int)InterceptClass.Gunship - 1].Id); // appended at the end (H3; the bomber after it, H4)
        Assert.Equal(InterceptClass.Gunship, a.Flight!.Class);
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        var g = a.Flight.Wing!;
        var jet = c.Archetype("attack_jet")!;
        var j = jet.Flight!.Wing!;
        Assert.False(g.HasAfterburner);
        Assert.True(g.Cruise < j.Cruise && g.Military < j.Military, "slower than the attack jet");
        Assert.True(g.Radius(g.Cruise) > j.Radius(j.Cruise), "wider turns");
        Assert.True(a.Flight.HeightRaw > c.Archetype("multirole_jet")!.Flight!.HeightRaw, "high");
        // The longest endurance of any aircraft.
        Assert.All(c.Archetypes.Where(x => x.Flight is not null && x != a), x => Assert.True(a.Lifetime > x.Lifetime, $"longer than {x.Id}"));
        output.WriteLine($"endurance {a.Lifetime / World.TicksPerSecond / 60} min, cruise {F(g.Cruise / (double)Cell1)} cells/s, turn at cruise {F(Cells(g.Radius(g.Cruise)))} cells");
        // Its three side cannons: 100 light, 20 heavy (half the 500 lb's damage, its look), 1 very big (the heavy bomb's).
        var sides = d.Weapons.Where(x => x.Effect.SideFire is not null).Select(x => x.Effect).ToList();
        Assert.Equal([100, 20, 1], sides.Select(x => x.Ammo));
        Assert.Equal(d.Weapons.Count, sides.Count); // no air-to-air weapons
        var lb500 = w.Player(1)!.Setup.Find(c, "Multirole 500 lb")!.Weapons.Single(x => x.Effect.Delivery is not null).Effect;
        var heavyBombs = w.Player(1)!.Setup.Find(c, "attack_jet")!.Weapons.Single(x => x.Effect.Delivery is not null).Effect;
        Assert.Equal(lb500.Damage / 2, sides[1].Damage);
        Assert.Equal(World.StrikeTier(lb500.Damage), sides[1].SideFire!.Tier);
        Assert.Equal((heavyBombs.Damage, heavyBombs.Splash, heavyBombs.SplashInner), (sides[2].Damage, sides[2].Splash, sides[2].SplashInner));
        Assert.Equal(6, World.StrikeTier(sides[2].Damage));
        Assert.True(sides[2].SideFire!.Biggest && !sides[2].SideFire!.AreaFire, "the very big round waits for the biggest target");
        // 12 flare salvos, no chaff, no break.
        Assert.Equal([(CountermeasureKind.Flare, 12)], a.Countermeasures.Select(x => (x.Kind, x.Salvos)));
        Assert.Null(a.Evade);
        // Every interceptor has values for it (the owner's 8 hits' worth from what reaches it).
        Interceptor Ic(string unit) => w.Player(2)!.Setup.Find(c, unit)!.Weapons.Single(x => x.Effect.Interceptor is not null).Effect.Interceptor!;
        var igla = Ic("manpads");
        Assert.True(igla.Engages(InterceptClass.Gunship));
        Assert.True(igla.CeilingRaw > 0 && igla.CeilingRaw < a.Flight.HeightRaw, "the AA soldier's ceiling is below its orbit");
        Assert.True(igla.CeilingRaw >= c.Archetype("multirole_jet")!.Flight!.HeightOn(4096), "the ceiling never cuts a jet's cruise");
        Assert.Equal(13, Ic("sam_vehicle").HitDamageOn(InterceptClass.Gunship));
        foreach (var jetName in new[] { "fighter_jet", "Multirole 500 lb", "attack_jet" })
        {
            foreach (var x in w.Player(1)!.Setup.Find(c, jetName)!.Weapons)
            {
                if (x.Effect.Interceptor is { } ic)
                {
                    Assert.True(ic.Engages(InterceptClass.Gunship));
                    Assert.Equal(13, ic.HitDamageOn(InterceptClass.Gunship));
                }
                if (x.Effect.AirGun is { } ag)
                {
                    Assert.True(ag.HitOn(InterceptClass.Gunship) > 0);
                    Assert.True(x.Effect.Damage * 8 < d.MaxHp, "a gun burst is a scratch");
                }
            }
        }
        var mg = w.Player(2)!.Setup.Find(c, "technical")!.Weapons.Single(x => x.Effect.AirGun is not null).Effect;
        Assert.True(mg.AirGun!.HitOn(InterceptClass.Gunship) > 0);
    }

    // ---- The orbit ----

    // Flies g's orbit for ticks: its distance from the centre while joined, its shots (with the tick and whether it was on
    // its circle then) and its state each tick.
    private static (List<long> Radii, List<(uint Tick, bool On, SideFireShot Shot)> Shots, List<(uint Tick, bool On, bool Joined)> States) Orbit(
        World w, Unit g, int ticks, Func<bool>? until = null)
    {
        var radii = new List<long>();
        var shots = new List<(uint, bool, SideFireShot)>();
        var states = new List<(uint, bool, bool)>();
        for (var t = 0; t < ticks && until?.Invoke() != true; t++)
        {
            var tick = w.Tick;
            w.Step();
            var on = g.Gunship?.OnCircle ?? false;
            shots.AddRange(w.SideFires.Where(s => s.Aircraft == g.Id).Select(s => (tick, on, s)));
            if (g.Gunship is { } o)
            {
                states.Add((tick, o.OnCircle, o.Joined));
                if (o.Joined)
                {
                    radii.Add(Dist(g.Pos, o.Centre));
                }
            }
        }
        return (radii, shots, states);
    }

    [Fact]
    public void ItOrbitsLeftHandAtItsRadius()
    {
        var w = Sentinel(320);
        var g = Fly(w, 40, 160);
        var centre = new Cell(200, 160);
        Issue(w, CommandKind.AttackGround, g, centre);
        var (radii, _, states) = Orbit(w, g, 3000);
        var spec = Spec(w);
        var joined = states.FindIndex(s => s.Joined);
        Assert.True(joined >= 0, "it reaches its circle");
        var settled = radii.Skip(20 * World.TicksPerSecond).ToList(); // after a lap
        output.WriteLine($"joined at tick {states[joined].Tick}; radius mean {F(Cells((long)settled.Average()))}, min {F(Cells(settled.Min()))}, max {F(Cells(settled.Max()))} (orbit {F(Cells(spec.RadiusRaw))})");
        Assert.InRange(Cells((long)settled.Average()), Cells(spec.RadiusRaw) - 1.5, Cells(spec.RadiusRaw) + 1.5);
        Assert.All(settled, r => Assert.InRange(r, spec.RadiusRaw - spec.OnCircleRaw, spec.RadiusRaw + spec.OnCircleRaw));
        var on = states.Skip(joined + 20 * World.TicksPerSecond).Count(s => s.On) * 100 / Math.Max(1, states.Count - joined - 20 * World.TicksPerSecond);
        output.WriteLine($"on the circle {on}% of the settled orbit");
        Assert.True(on >= 95, "it holds its circle");
        // Left-hand: counter-clockwise on the map (y down): the centre on its left.
        var c = centre.Center();
        long rx = g.Pos.X.Raw - c.X.Raw, ry = g.Pos.Y.Raw - c.Y.Raw;
        var v = g.Wing!;
        Assert.True(rx * v.VelY - ry * v.VelX < 0, "counter-clockwise on the map");
    }

    // ---- The side cannons ----

    // Enemies round the cell (cx, cy) for player 2: a technical, a cargo truck, three soldiers and a supply depot.
    private static List<Unit> Targets(World w, int cx, int cy) =>
    [
        w.Spawn(2, "technical", cx - 4, cy),
        w.Spawn(2, "cargo_truck", cx + 5, cy + 3),
        w.Spawn(2, "Rifleman", cx, cy + 6),
        w.Spawn(2, "Rifleman", cx + 2, cy + 7),
        w.Spawn(2, "Rifleman", cx - 3, cy - 6),
        w.Spawn(2, "supply_depot", cx + 2, cy - 10),
    ];

    [Fact]
    public void ItFiresOnlyOnItsCircleNeverOnTheWayIn()
    {
        var w = Sentinel(320);
        var g = Fly(w, 40, 160);
        var centre = new Cell(200, 160);
        Targets(w, 200, 160);
        Issue(w, CommandKind.AttackGround, g, centre);
        var (_, shots, states) = Orbit(w, g, 1600);
        var spec = Spec(w);
        var joined = states.First(s => s.Joined).Tick;
        output.WriteLine($"shots {shots.Count}, the first at tick {shots[0].Tick}, joined at {joined}; by weapon " +
            string.Join(", ", shots.GroupBy(x => x.Shot.Weapon).Select(x => $"{x.Key}: {x.Count()}")));
        Assert.True(shots.Count > 20, "it fires from its orbit");
        Assert.True(shots[0].Tick >= joined, "nothing on the way in");
        var c = centre.Center();
        Assert.All(shots, x => Assert.True(x.On, "fired on the circle"));
        Assert.All(shots, x => Assert.InRange(Dist(x.Shot.From, c), spec.RadiusRaw - spec.OnCircleRaw, spec.RadiusRaw + spec.OnCircleRaw));
        Assert.Contains(states, s => !s.On); // the way in was off the circle
    }

    [Fact]
    public void ItFires100Light20HeavyAnd1BigRoundThenGoesHome()
    {
        var w = Sentinel(400);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var g = Fly(w, 120, 300);
        var ts = Targets(w, 300, 300);
        var depot = ts[^1];
        Issue(w, CommandKind.AttackGround, g, new Cell(300, 300));
        var refusals = new List<OrderRefusal>();
        var (_, shots, _) = Orbit(w, g, 6000, () =>
        {
            refusals.AddRange(w.OrderRefusals.Where(r => r.Aircraft == g.Id));
            return g.Homing;
        });
        var by = shots.GroupBy(x => x.Shot.Weapon).OrderBy(x => x.Key).Select(x => x.Count()).ToList();
        output.WriteLine($"rounds by weapon {string.Join(", ", by)}; homing {g.Homing} ({g.HomeReason}); refusals {string.Join(", ", refusals.Select(r => r.Reason))}");
        Assert.Equal([100, 20, 1], by);
        Assert.All(g.Charges, n => Assert.Equal(0, n));
        // The very big round went at the biggest target in the area: the depot (a structure).
        var big = shots.Single(x => x.Shot.Weapon == 2).Shot;
        Assert.Equal(depot.Id, big.Target);
        Assert.Equal(6, big.Tier);
        Assert.All(shots.Where(x => x.Shot.Weapon == 1), x => Assert.Equal(4, x.Shot.Tier)); // the 500 lb's look
        // Then home on its own (a recall: refuelled, held parked until its next order), saying why.
        Assert.True(g.Homing && g.HomeReason == HomeReason.Recall, "home on its own when it is out of rounds");
        Assert.Contains(refusals, r => r.Reason == OrderRefusalReason.NoAmmo);
        Assert.Equal(OrderKind.Idle, g.Order.Kind);
        Assert.Null(g.Gunship);
        _ = af;
    }

    [Fact]
    public void ItsRoundsLandScatteredWithinTheirBound()
    {
        var w = Sentinel(320);
        var g = Fly(w, 40, 160);
        var ts = Targets(w, 200, 160);
        Issue(w, CommandKind.AttackGround, g, new Cell(200, 160));
        var spec = Spec(w);
        var off = new List<(int Weapon, long Dx, long Dy)>();
        var area = new List<SideFireShot>();
        for (var t = 0; t < 1600; t++)
        {
            w.Step();
            foreach (var s in w.SideFires.Where(s => s.Aircraft == g.Id))
            {
                if (s.Target != 0 && w.Unit(s.Target) is { } u && u.Vel == default)
                {
                    off.Add((s.Weapon, s.Aim.X.Raw - u.Pos.X.Raw, s.Aim.Y.Raw - u.Pos.Y.Raw));
                }
                else if (s.AreaFire)
                {
                    area.Add(s);
                }
            }
        }
        var weapons = g.Def.Weapons;
        foreach (var grp in off.GroupBy(o => o.Weapon))
        {
            var bound = weapons[grp.Key].Effect.SideFire!.ScatterRaw;
            output.WriteLine($"weapon {grp.Key}: {grp.Count()} aimed rounds, scatter bound {F(Cells(bound))} cells, max off {F(Cells(grp.Max(o => Math.Max(Math.Abs(o.Dx), Math.Abs(o.Dy)))))}, mean {F(Cells((long)grp.Average(o => Math.Sqrt((double)o.Dx * o.Dx + (double)o.Dy * o.Dy))))}");
            Assert.All(grp, o => Assert.True(Math.Abs(o.Dx) <= bound && Math.Abs(o.Dy) <= bound, "within the scatter"));
            Assert.Contains(grp, o => o.Dx != 0 || o.Dy != 0); // inaccurate
        }
        Assert.True(off.Count > 10, "aimed rounds");
        var c = new Cell(200, 160).Center();
        Assert.All(area, s => Assert.True(Dist(s.Aim, c) <= spec.AreaRaw + 1, "area fire inside the area"));
        _ = ts;
    }

    [Fact]
    public void AnAttackOrderOrbitsTheUnitAndWorksTheAreaOnWhenItDies()
    {
        var w = Sentinel(320);
        var g = Fly(w, 40, 160);
        var tech = w.Spawn(2, "technical", 200, 160);
        var others = new[] { w.Spawn(2, "Rifleman", 204, 166), w.Spawn(2, "Rifleman", 196, 154) };
        Issue(w, CommandKind.Attack, g, target: tech.Id);
        var refusals = new List<OrderRefusal>();
        var (_, shots, _) = Orbit(w, g, 2400, () =>
        {
            refusals.AddRange(w.OrderRefusals.Where(r => r.Aircraft == g.Id));
            return !tech.Alive && g.Order.Kind == OrderKind.AttackGround && others.All(o => !o.Alive);
        });
        output.WriteLine($"technical alive {tech.Alive}; order {g.Order.Kind}; shots {shots.Count}; refusals {string.Join(", ", refusals.Select(r => r.Reason))}");
        Assert.False(tech.Alive);
        Assert.Contains(shots, x => x.Shot.Target == tech.Id);
        Assert.Equal(OrderKind.AttackGround, g.Order.Kind); // it works the area on
        Assert.Contains(refusals, r => r.Reason == OrderRefusalReason.TargetDead);
        Assert.NotNull(g.Gunship);
        Assert.Equal(AirActivity.GunOrbit, w.AirStatusOf(g).Activity);
    }

    [Fact]
    public void TwoGunshipsGivenOneAreaSpaceThemselvesRoundItAndBothFire()
    {
        var w = Sentinel(320);
        var a = w.Spawn(1, Gunship, 40, 150, Facing.East);
        var b = w.Spawn(1, Gunship, 40, 172, Facing.East);
        w.Step();
        Targets(w, 200, 160);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AttackGround, Units = [a.Id, b.Id], Cell = new Cell(200, 160) });
        var shots = new List<(uint Aircraft, bool On)>();
        var gaps = new List<long>();
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            shots.AddRange(w.SideFires.Select(x => (x.Aircraft, (x.Aircraft == a.Id ? a : b).Gunship!.OnCircle)));
            if (a.Gunship is { Joined: true } && b.Gunship is { Joined: true })
            {
                gaps.Add(World.Len3(a.Pos.X.Raw - b.Pos.X.Raw, a.Pos.Y.Raw - b.Pos.Y.Raw, 0));
            }
        }
        var spec = Spec(w);
        var late = gaps.Skip(gaps.Count - 20 * World.TicksPerSecond).ToList();
        output.WriteLine($"rounds {shots.Count(x => x.Aircraft == a.Id)} and {shots.Count(x => x.Aircraft == b.Id)}; gap at the join {F(Cells(gaps[0]))}, " +
            $"closest {F(Cells(gaps.Min()))}, over the last 20 s {F(Cells(late.Min()))} to {F(Cells(late.Max()))} cells (the diameter {F(Cells(2 * spec.RadiusRaw))})");
        Assert.True(shots.Count(x => x.Aircraft == a.Id) > 20 && shots.Count(x => x.Aircraft == b.Id) > 20, "both fire");
        Assert.All(shots, x => Assert.True(x.On));
        Assert.All(late, d => Assert.True(d > spec.RadiusRaw * 3 / 2, "they space themselves round the circle (half a lap apart)"));
    }

    // ---- Toughness and threats ----

    [Fact]
    public void TheAaSoldierCannotReachItsOrbit()
    {
        var w = Sentinel(320);
        var g = Fly(w, 40, 160);
        var spec = Spec(w);
        var r = (int)(spec.RadiusRaw / Cell1);
        // AA soldiers under its circle and round the area, a little off it (outside the area: the cannons leave them be).
        var aa = new List<Unit>();
        for (var i = 0; i < 8; i++)
        {
            var (dx, dy) = (i % 4 - 2, i / 4);
            aa.Add(w.Spawn(2, "manpads", 200 + r + dx * 3, 140 + dy * 40));
        }
        Issue(w, CommandKind.AttackGround, g, new Cell(200, 160));
        for (var t = 0; t < 2400; t++)
        {
            w.Step();
            Assert.DoesNotContain(w.Projectiles, p => p.Weapon.Effect.Interceptor is not null && p.Target == g.Id);
        }
        output.WriteLine($"orbit height {F((g.Altitude.Raw - 0) / 512.0)} m; AA ammo {string.Join(",", aa.Select(a => a.Charges[0]))}");
        Assert.True(g.Alive && g.HP == g.Def.MaxHp);
        Assert.All(aa, a => Assert.Equal(a.Def.StockMax[0], a.Charges[0])); // not one missile fired
    }

    [Fact]
    public void TheCeilingKeepsTheAaSoldierOffAGunshipAboveIt()
    {
        // At 240 m (inside the Igla's 280 m of slant), the AA soldier engages a gunship flying under its ceiling (320 m) and
        // never one with the ceiling below it (200 m): the ceiling alone decides.
        int Launches(int ceiling)
        {
            var c = Edited(("\"height\": 3600, \"climbRate\": 100", "\"height\": 2400, \"climbRate\": 100"), ("\"ceiling\": 3200", $"\"ceiling\": {ceiling}"));
            var w = SentinelOn(c, 240);
            var g = w.Spawn(1, Gunship, 120, 120, Facing.East);
            w.Step();
            g.MutableSalvos[0] = 0;
            w.Spawn(2, "manpads", 122, 124);
            var missiles = new SortedSet<uint>();
            for (var t = 0; t < 600; t++)
            {
                w.Step();
                missiles.UnionWith(w.Projectiles.Where(p => p.Weapon.Effect.Interceptor is not null && p.Target == g.Id).Select(p => p.Id));
            }
            return missiles.Count;
        }
        var under = Launches(3200);
        var over = Launches(2000);
        output.WriteLine($"missiles at it under the ceiling {under}, over it {over}");
        Assert.True(under > 0, "engaged under its ceiling");
        Assert.Equal(0, over);
    }

    [Fact]
    public void EightAirDefenceMissileHitsBringItDown()
    {
        var w = Sentinel(320);
        var g = Fly(w, 140, 160);
        g.MutableSalvos[0] = 0; // no flares: every fuse is a straight roll
        for (var i = 0; i < 4; i++)
        {
            w.SpawnCrewed(2, "sam_vehicle", 140 + 4 * i, 100); // 60 cells off the area: the cannons leave them be
        }
        Issue(w, CommandKind.AttackGround, g, new Cell(150, 220));
        var hp = new List<int>();
        var bursts = new List<AirBurstKind>();
        for (var t = 0; t < 9000 && g.Alive; t++)
        {
            w.Step();
            foreach (var b in w.AirBursts.Where(b => b.Target == g.Id && b.Kind is AirBurstKind.Hit or AirBurstKind.Kill))
            {
                bursts.Add(b.Kind);
                hp.Add(g.HP);
            }
        }
        output.WriteLine("hits: " + string.Join(", ", bursts.Zip(hp, (k, h) => $"{k} {h}")));
        Assert.False(g.Alive);
        Assert.Equal([.. Enumerable.Repeat(AirBurstKind.Hit, 7), AirBurstKind.Kill], bursts);
        Assert.Equal([1392, 1184, 976, 768, 560, 352, 144, 0], hp); // 13% of its 1,600 HP a hit
        Assert.Equal(KillCause.Missile, g.KilledBy);
    }

    [Fact]
    public void ItSpendsItsFlaresAgainstIrMissiles()
    {
        var w = Duel(320, 3);
        var g = w.Spawn(1, Gunship, 160, 160, Facing.East);
        var j = w.Spawn(2, "attack_jet", 40, 160, Facing.East); // two IR missiles (aam_ir_light)
        w.Step();
        Issue(w, CommandKind.AttackGround, g, new Cell(160, 200));
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.Attack, Units = [j.Id], Target = g.Id });
        var salvos = new List<CountermeasureSalvo>();
        for (var t = 0; t < 4000 && g.Alive && salvos.Count < 2; t++)
        {
            w.Step();
            salvos.AddRange(w.Countermeasures.Where(x => x.Aircraft == g.Id));
        }
        output.WriteLine($"salvos {salvos.Count}: left {string.Join(", ", salvos.Select(x => x.Left))}; flares {g.Salvos[0]}");
        Assert.NotEmpty(salvos);
        Assert.All(salvos, x => Assert.Equal(CountermeasureKind.Flare, x.Kind));
        Assert.Equal(12 - salvos.Count, g.Salvos[0]);
        Assert.Equal(Enumerable.Range(0, salvos.Count).Select(i => 11 - i), salvos.Select(x => x.Left));
    }

    // ---- The main air base ----

    [Fact]
    public void ItIsBuiltAtTheMainAirBaseAndParksOnlyInAHeavyBay()
    {
        var w = Sentinel(360);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var hangar = w.Unit(af.Hangar)!;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [hangar.Id], Unit = Gunship });
        Unit? g = null;
        for (var t = 0; t < 2000 && g is null; t++)
        {
            w.Step();
            g = w.Units.FirstOrDefault(u => u.Def.Archetype.Id == Gunship);
        }
        Assert.NotNull(g);
        Assert.InRange((int)w.Tick, 1800, 1900); // 90 s
        Assert.Equal(FlightPhase.Parked, g!.AirOps!.Phase);
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == g.AirOps.Station).Kind);
        Assert.True(w.StationsFor(af, g.Def.Archetype.Aircraft!).All(s => s.Kind == StationKind.Heavy), "only heavy bays take it");
        var fwd = w.SpawnAirfield(w.Player(1)!.Setup.Find(w.Content, "airfield_facility")!, new Cell(60, 300), 0, [(0, 0)])!;
        Assert.Empty(w.StationsFor(fwd, g.Def.Archetype.Aircraft!)); // a forward airfield never takes it
        // Out to an orbit and back: it lands on the base's runway and parks in a heavy bay again.
        Issue(w, CommandKind.AttackGround, g, new Cell(250, 250));
        for (var t = 0; t < 4000 && g.AirOps is not null; t++)
        {
            w.Step();
        }
        Assert.Null(g.AirOps); // taxied out and took off
        w.Run(400);
        Assert.NotNull(g.Gunship);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.ReturnToBase, Units = [g.Id], Target = af.Facility });
        var parked = false;
        for (var t = 0; t < 12000 && !parked; t++)
        {
            w.Step();
            parked = g.AirOps is { Phase: FlightPhase.Parked };
        }
        Assert.True(parked, "it lands and parks");
        Assert.Equal(StationKind.Heavy, af.Rows.SelectMany(r => r.Stations).Single(s => s.Unit == g.AirOps!.Station).Kind);
        // Parked out of rounds, an attack order is an implicit Rearm (D-ORD2): rearmed in its bay, then it leaves.
        for (var i = 0; i < g.Charges.Count; i++)
        {
            g.MutableCharges[i] = 0;
        }
        Issue(w, CommandKind.AttackGround, g, new Cell(250, 250));
        w.Step();
        Assert.Contains(w.OrderRefusals, r => r.Aircraft == g.Id && r.Reason == OrderRefusalReason.Rearming);
        for (var t = 0; t < 6000 && g.AirOps is not null; t++)
        {
            w.Step();
        }
        Assert.Null(g.AirOps);
        Assert.Equal([100, 20, 1], g.Charges);
    }

    // ---- The H1 follow-ups ----

    [Fact]
    public void TheHangarBuildsTheDronesInTheirBuildTimes()
    {
        var c = TestContent.Mvp;
        Assert.Equal(1200, c.Archetype("watcher_drone")!.BuildTime); // 60 s (its station's own replacement time)
        Assert.Equal(900, c.Archetype("overseer_drone")!.BuildTime); // 45 s
        var w = Sentinel(360);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var hangar = w.Unit(af.Hangar)!;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [hangar.Id], Unit = "overseer_drone" });
        Unit? d = null;
        for (var t = 0; t < 1400 && d is null; t++)
        {
            w.Step();
            d = w.Units.FirstOrDefault(u => u.Def.Archetype.Id == "overseer_drone");
        }
        Assert.NotNull(d);
        Assert.InRange((int)w.Tick, 900, 1000);
    }

    [Fact]
    public void RadarTracksAreTheAirUnitsTheRadarItselfDetects()
    {
        var w = Duel(600, 1);
        var radar = w.Spawn(1, "radar_site", 100, 300);
        w.Step();
        var near1 = w.Spawn(2, "Multirole 500 lb", 200, 300, Facing.East);
        var far = w.Spawn(2, "Multirole 500 lb", 500, 300, Facing.East); // 400 cells: beyond its 200-cell air reach
        var own = w.Spawn(1, "Multirole 500 lb", 150, 320, Facing.East);
        var near2 = w.Spawn(2, Gunship, 120, 260, Facing.East); // 45 cells off, 180 up: 185 of slant
        w.Step();
        var tracks = w.RadarTracks(radar);
        output.WriteLine("tracks " + string.Join(", ", tracks.Select(t => $"{t.Id} {t.Def.Archetype.Id}")));
        Assert.Equal([near1.Id, near2.Id], tracks.Select(t => t.Id));
        Assert.All(tracks, t => Assert.True(w.AirTracked(1, t.Id), "its side tracks what it detects"));
        Assert.DoesNotContain(far, tracks);
        Assert.DoesNotContain(own, tracks);
        Assert.Empty(w.RadarTracks(w.Spawn(1, "technical", 20, 20))); // no air vision: nothing
    }

    // ---- Determinism ----

    // A scripted orbit on both sides (a Sentinel duel world): player 1's gunship works an area, player 2's attacks a unit;
    // the commands issued in order or reversed; the world's hash every tick.
    private static List<ulong> Script(bool reversed)
    {
        var w = Duel(320, 11);
        var g1 = w.Spawn(1, Gunship, 40, 200, Facing.East);
        var g2 = w.Spawn(2, Gunship, 280, 120, Facing.West);
        var t2 = w.Spawn(2, "technical", 170, 200);
        w.Spawn(2, "Rifleman", 172, 205);
        var t1 = w.Spawn(1, "technical", 140, 120);
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.AttackGround, Units = [g1.Id], Cell = new Cell(170, 200) },
            new Command { Tick = 1, Player = 2, Seq = 1, Kind = CommandKind.Attack, Units = [g2.Id], Target = t1.Id },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        var shots = 0;
        for (var t = 0; t < 1500; t++)
        {
            w.Step();
            shots += w.SideFires.Count;
            hashes.Add(w.Hash());
        }
        Assert.True(shots > 40, "both fire");
        Assert.False(t2.Alive && t1.Alive, "a technical is destroyed");
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
