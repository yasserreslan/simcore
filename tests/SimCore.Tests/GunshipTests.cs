using SimCore.FixedPoint;
using SimCore.Model;
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
        Assert.Equal("gunship", c.AirClasses[^1].Id); // appended at the end
        Assert.Equal(c.AirClasses.Count, (int)a.Flight!.Class);
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

    // Flies g's attack-ground orbit at cell for ticks, logging its distance from the centre while joined.
    private (List<long> Radii, List<SideFireShot> Shots, List<(uint Tick, bool On, bool Joined)> States) Orbit(World w, Unit g, int ticks)
    {
        var radii = new List<long>();
        var shots = new List<SideFireShot>();
        var states = new List<(uint, bool, bool)>();
        for (var t = 0; t < ticks; t++)
        {
            w.Step();
            shots.AddRange(w.SideFires.Where(s => s.Aircraft == g.Id));
            if (g.Gunship is { } o)
            {
                states.Add((w.Tick, o.OnCircle, o.Joined));
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
}
