using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Truck launchers aim with the hull (docs/design.md, "Launchers aim with the hull"): the box is fixed on the truck and only
// elevates, so the truck turns in place to face before it sets up, and a set-up truck whose next target is off its arc (one
// facing, 11.25 degrees, either side) packs up, turns and sets up again (archetype hullAims). Vertical launchers
// (verticalLaunch: the S-400 type launcher, the hypersonic truck) fire without turning. The air defence vehicle turns its hull
// (no turret, no set-up). Before this change every one of them turned a turret, its hull standing as it was.
public class HullAimTests(ITestOutputHelper output)
{
    private const int N = 460;
    private const int X = 30;
    private const int Y = 230;

    private static readonly string[] HullAimers =
    [
        "heavy_rocket_launcher", "heavy_rocket_truck", "longrange_rocket_truck", "rocket_jeep", "missile_truck", "ballistic_truck",
        "drone_truck", "kaman_truck", "cruise_truck",
    ];

    private static readonly string[] Vertical = ["s400_launcher", "hypersonic_truck"];

    // The ground launchers, a range (cells) in the middle of each one's reach, and the measured ticks from the order to the first
    // round (charge spent) with the point dead ahead, 90 and 180 degrees off a stowed truck, and 90 degrees off a set-up one. The
    // turret's times before this change, the same order: heavy_rocket_launcher 51 54 58 4, heavy_rocket_truck 81 84 88 4,
    // longrange_rocket_truck 121 124 128 4, rocket_jeep 61 64 68 4, missile_truck 121 124 128 4, ballistic_truck 201 204 208 4,
    // drone_truck 41 44 48 4, kaman_truck 41 44 48 4, cruise_truck 161 164 168 4, hypersonic_truck 201 204 208 4.
    public static TheoryData<string, int, int, int, int, int> Ground => new()
    {
        { "heavy_rocket_launcher", 40, 51, 58, 66, 99 },
        { "heavy_rocket_truck", 70, 81, 88, 96, 149 },
        { "longrange_rocket_truck", 90, 121, 128, 136, 229 },
        { "rocket_jeep", 35, 61, 64, 68, 105 },
        { "missile_truck", 70, 121, 128, 136, 229 },
        { "ballistic_truck", 110, 201, 208, 216, 329 },
        { "drone_truck", 100, 41, 44, 48, 1 },
        { "kaman_truck", 100, 41, 44, 48, 85 },
        { "cruise_truck", 200, 161, 168, 176, 1 },
        { "hypersonic_truck", 200, 201, 201, 201, 1 },
    };

    private readonly record struct Shot(int Ticks, int Facing, int Box, bool Stowed, bool TurnedSetUp);

    // FirstShot spawns id facing `facing` at (X, Y), orders an attack on the ground `range` cells due east (with setUp: the truck
    // first set up facing east and the point due north) and returns the ticks to its first round (its first charge spent, -1 for
    // none), its hull and box facings then, whether it packed up on the way and whether its hull ever turned while set up.
    private static Shot FirstShot(string id, int range, int facing, bool setUp = false)
    {
        var w = Arena.New(N);
        var truck = w.SpawnCrewed(1, id, X, Y, facing).Vehicle;
        var full = truck.Charges[0];
        if (setUp)
        {
            w.Order(1, CommandKind.Deploy, [truck]);
            w.Run(truck.Def.Archetype.DeployTicks + 2);
            Assert.Equal(DeployState.Deployed, truck.Deploy);
        }
        w.Order(1, CommandKind.AttackGround, [truck], cell: setUp ? new Cell(X, Y - range) : new Cell(X + range, Y));
        var start = w.Tick;
        var (stowed, turned) = (false, false);
        for (var i = 0; i < 1500; i++)
        {
            var (was, wasSetUp) = (truck.Facing, truck.Deploy == DeployState.Deployed);
            w.Step();
            stowed |= truck.Deploy == DeployState.Stowing;
            turned |= wasSetUp && truck.Deploy == DeployState.Deployed && truck.Facing != was;
            if (truck.Charges[0] < full)
            {
                return new Shot((int)(w.Tick - start), truck.Facing, truck.TurretFacing, stowed, turned);
            }
        }
        return new Shot(-1, truck.Facing, truck.TurretFacing, stowed, turned);
    }

    [Fact]
    public void TheTruckLaunchersAreData()
    {
        var c = TestContent.Mvp;
        foreach (var id in HullAimers)
        {
            var a = c.Archetype(id)!;
            Assert.True(a.HullAims && !a.Turret && !a.VerticalLaunch && a.DeployTicks > 0, id);
        }
        foreach (var id in Vertical)
        {
            var a = c.Archetype(id)!;
            Assert.True(a.VerticalLaunch && !a.Turret && !a.HullAims, id);
        }
        var sam = c.Archetype("sam_vehicle")!;
        Assert.False(sam.Turret || sam.HullAims || sam.VerticalLaunch); // the hull turns, no set-up
        foreach (var (id, weapon) in new[] { ("patriot_launcher", "patriot_missiles"), ("thaad_launcher", "thaad_missiles") })
        {
            var a = c.Archetype(id)!; // unchanged: no turret, the fixed box of a radar-linked launcher (the missile turns)
            Assert.False(a.Turret || a.HullAims || a.VerticalLaunch, id);
            Assert.True(c.Component(weapon)!.Effects.OfType<WeaponEffect>().Single().Interceptor!.RadarLinked, id);
        }
        foreach (var id in new[] { "light_rocket_truck", "medium_rocket_launcher" })
        {
            var a = c.Archetype(id)!; // unchanged: a fixed weapon, the hull turns (no set-up)
            Assert.False(a.Turret || a.HullAims || a.VerticalLaunch || a.DeployTicks > 0, id);
        }
        Assert.True(c.Archetype("kub_launcher")!.Turret); // unchanged: the SA-6 type's turntable
    }

    [Theory]
    [InlineData("\"turnRate\": 1, \"hullAims\": true,\n      \"deployTicks\": 50", "\"turnRate\": 1, \"hullAims\": true, \"turret\": true,\n      \"deployTicks\": 50", "hullAims needs no turret")]
    [InlineData("\"turnRate\": 1, \"hullAims\": true,\n      \"deployTicks\": 50, \"stowTicks\": 40", "\"turnRate\": 1, \"hullAims\": true,\n      \"stowTicks\": 40", "hullAims needs a deployTicks")]
    [InlineData("\"turnRate\": 1, \"hullAims\": true,\n      \"deployTicks\": 50", "\"turnRate\": 1, \"hullAims\": true, \"verticalLaunch\": true,\n      \"deployTicks\": 50", "exclude each other")]
    [InlineData("\"turnRate\": 1, \"verticalLaunch\": true,\n      \"deployTicks\": 600", "\"turnRate\": 1, \"verticalLaunch\": true, \"hullArc\": 90,\n      \"deployTicks\": 600", "hullArc needs hullAims")]
    [InlineData("\"hullAims\": true, \"hullArc\": 180", "\"hullAims\": true, \"hullArc\": 181", "hullArc")]
    [InlineData("\"turnRate\": 1, \"verticalLaunch\": true,\n      \"deployTicks\": 600", "\"turnRate\": 1, \"verticalLaunch\": true, \"turret\": true,\n      \"deployTicks\": 600", "verticalLaunch needs no turret")]
    public void BadAimContentIsRefused(string from, string to, string error)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Name.EndsWith("archetypes/vehicles.json", StringComparison.Ordinal));
        var json = sources[i].Json.Replace("\r\n", "\n", StringComparison.Ordinal);
        var at = json.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0, from);
        sources[i] = new ContentSource(sources[i].Name, string.Concat(json.AsSpan(0, at), to, json.AsSpan(at + from.Length)));
        var e = Assert.ThrowsAny<Exception>(() => ContentLoader.Load(sources));
        Assert.Contains(error, e.Message, StringComparison.Ordinal);
    }

    // Per launcher class: the first round leaves with the hull on the launch line (within the one-facing arc) and the box drawn
    // along the hull, the hull never turning with the jacks down; a vertical launcher fires as it stands. The times are the
    // measured ones (see Ground).
    [Theory]
    [MemberData(nameof(Ground))]
    public void TheHullFacesThePointWhenItFires(string id, int range, int ahead, int side, int behind, int reAim)
    {
        var vertical = Array.IndexOf(Vertical, id) >= 0;
        var arc = TestContent.Mvp.Archetype(id)!.HullArc; // facings either side; 16 (any way) for a missile that turns after launch
        var a = FirstShot(id, range, Facing.East);
        var s = FirstShot(id, range, Facing.North);
        var b = FirstShot(id, range, Facing.West);
        var r = FirstShot(id, range, Facing.East, setUp: true);
        output.WriteLine($"{id}: ahead {a.Ticks}, 90 degrees {s.Ticks}, 180 degrees {b.Ticks}, set up 90 degrees off {r.Ticks} ticks");
        Assert.Equal((ahead, side, behind, reAim), (a.Ticks, s.Ticks, b.Ticks, r.Ticks));
        foreach (var (shot, from, want) in new[] { (a, Facing.East, Facing.East), (s, Facing.North, Facing.East), (b, Facing.West, Facing.East), (r, Facing.East, Facing.North) })
        {
            Assert.Equal(shot.Facing, shot.Box); // the box is drawn along the hull
            Assert.False(shot.TurnedSetUp, $"{id}: the hull turned with its jacks down");
            if (vertical)
            {
                Assert.Equal(from, shot.Facing); // it never turns
                Assert.False(shot.Stowed);
            }
            else
            {
                Assert.InRange(Math.Abs(Facing.Diff(shot.Facing, want)), 0, arc);
            }
        }
        Assert.Equal(!vertical && arc < 8, r.Stowed); // off its arc a set-up hull-aiming truck packs up, turns and sets up again
        Assert.Equal(!vertical && arc < 8 ? Facing.North : Facing.East, r.Facing);
    }

    // Within its arc (the point one facing off the hull) a set-up truck fires as it stands: no pack-up, no turn.
    [Theory]
    [InlineData("heavy_rocket_launcher", 40)]
    [InlineData("missile_truck", 70)]
    public void WithinItsArcASetUpTruckFiresAsItStands(string id, int range)
    {
        var w = Arena.New(N);
        var truck = w.SpawnCrewed(1, id, X, Y, Facing.East).Vehicle;
        var full = truck.Charges[0];
        w.Order(1, CommandKind.Deploy, [truck]);
        w.Run(truck.Def.Archetype.DeployTicks + 2);
        // About 8 degrees north of east: the 32-facing direction is one facing off the hull's.
        var at = new Cell(X + range, Y - range / 7);
        Assert.True(Facing.TryFromDirection(at.Center() - truck.Pos, out var want));
        Assert.Equal(1, Math.Abs(Facing.Diff(truck.Facing, want)));
        w.Order(1, CommandKind.AttackGround, [truck], cell: at);
        for (var i = 0; i < 10; i++)
        {
            w.Step();
            Assert.Equal(DeployState.Deployed, truck.Deploy);
            Assert.Equal(Facing.East, truck.Facing);
        }
        Assert.True(truck.Charges[0] < full);
    }

    // A set-up truck ordered at a unit off its arc: the truck packs up, turns, sets up and fires again with its hull on the
    // target.
    [Fact]
    public void ATargetOffTheArcIsFollowedByPackingUpAndTurning()
    {
        var w = Arena.New(N);
        var truck = w.SpawnCrewed(1, "rocket_jeep", X, Y, Facing.East).Vehicle;
        var target = w.Spawn(2, "technical", X + 35, Y);
        target.HP = 1_000_000;
        w.Spawn(1, "worker", X + 35, Y + 4); // the spotter
        w.Order(1, CommandKind.Attack, [truck], target.Id);
        w.Run(200);
        Assert.Equal(0, truck.Charges[0]); // both rockets at it, facing east
        Assert.Equal(Facing.East, truck.Facing);
        truck.MutableCharges[0] = 2;
        target.Pos = new Cell(X, Y - 35).Center(); // now due north
        w.Spawn(1, "worker", X + 4, Y - 35);
        Assert.Equal(DeployState.Deployed, truck.Deploy); // an empty launcher ends its attack; still set up
        w.Order(1, CommandKind.Attack, [truck], target.Id);
        var stowed = false;
        for (var i = 0; i < 400 && truck.Charges[0] == 2; i++)
        {
            w.Step();
            stowed |= truck.Deploy == DeployState.Stowing;
        }
        Assert.True(stowed);
        Assert.Equal(1, truck.Charges[0]);
        Assert.Equal(Facing.North, truck.Facing);
    }

    // The air defence vehicle turns its hull (at its turn rate, 2 facings a tick, the old turret's): it fires on the same tick as
    // before, whichever way it faced (tick 159 in this engagement, facing the drone or with its back to it).
    [Theory]
    [InlineData(Facing.South)]
    [InlineData(Facing.North)]
    [InlineData(Facing.East)]
    public void TheAirDefenceVehicleTurnsItsHullAndLosesNoTime(int facing)
    {
        var w = Arena.New(700);
        var sam = w.SpawnCrewed(1, "sam_vehicle", 350, 350, facing).Vehicle;
        var truck = w.SpawnCrewed(2, "drone_truck", 350, 640, Facing.North).Vehicle;
        w.Order(2, CommandKind.AttackGround, [truck], cell: new Cell(350, 60));
        var tick = -1;
        for (var i = 0; i < 1500 && tick < 0; i++)
        {
            w.Step();
            if (w.Projectiles.Any(p => p.Shooter == sam.Id))
            {
                tick = (int)w.Tick;
            }
        }
        Assert.Equal(159, tick);
        Assert.Equal(Facing.South, sam.Facing); // the drone comes from the south
        Assert.Equal(sam.Facing, sam.TurretFacing);
    }

    // The S-400 type launcher fires its vertical canisters as it stands: 42 ticks from the jet's arrival either way (with the old
    // turret 42 facing east and 45 facing north).
    [Theory]
    [InlineData(Facing.East)]
    [InlineData(Facing.North)]
    [InlineData(Facing.West)]
    public void TheS400LauncherFiresWithoutTurning(int facing)
    {
        var setup = TestContent.Sentinel(1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = new World(new Grid(1000, 300), MatchCompiler.Compile(TestContent.Mvp, setup));
        var r = w.SpawnCrewed(2, "s400_radar", 50, 150).Vehicle;
        var l = w.SpawnCrewed(2, "s400_launcher", 70, 150, facing).Vehicle;
        w.Order(2, CommandKind.Deploy, [r, l]);
        w.Run(602);
        Assert.Equal(DeployState.Deployed, l.Deploy);
        var j = w.Spawn(1, "Multirole 500 lb", 450, 150, Facing.West);
        w.Order(1, CommandKind.Move, [j], cell: new Cell(450, 150));
        var start = w.Tick;
        var ticks = -1;
        for (var i = 0; i < 600 && ticks < 0; i++)
        {
            w.Step();
            Assert.Equal((facing, facing), (l.Facing, l.TurretFacing));
            if (w.Projectiles.Any(p => p.Shooter == l.Id))
            {
                ticks = (int)(w.Tick - start);
            }
        }
        Assert.Equal(42, ticks);
    }

    // Every hull-aiming truck facing some other way, ordered at a point, then all re-ordered off their arcs: the same hashes
    // twice.
    [Fact]
    public void Deterministic()
    {
        static List<ulong> Play()
        {
            var w = Arena.New(N, seed: 9);
            var trucks = new List<Unit>();
            for (var i = 0; i < HullAimers.Length; i++)
            {
                trucks.Add(w.SpawnCrewed(1, HullAimers[i], X, 20 + i * 40, i * 5 % 32).Vehicle);
            }
            var hs = w.SpawnCrewed(1, "hypersonic_truck", X, 420, Facing.South).Vehicle;
            for (var i = 0; i < trucks.Count; i++)
            {
                var e = trucks[i].Def.Weapons[0].Effect;
                var d = (int)((e.MinRange.Raw + Math.Min(e.Range.Raw, Fixed.FromInt(200).Raw)) / 2 / Fixed.OneRaw);
                w.Order(1, CommandKind.AttackGround, [trucks[i]], cell: new Cell(X + d, 20 + i * 40));
            }
            w.Order(1, CommandKind.AttackGround, [hs], cell: new Cell(X + 200, 420));
            var hashes = new List<ulong>();
            for (var t = 0; t < 500; t++)
            {
                if (t == 250)
                {
                    w.Order(1, CommandKind.AttackGround, [.. trucks], cell: new Cell(X + 160, 5)); // off every arc
                }
                w.Step();
                hashes.Add(w.Hash());
            }
            return hashes;
        }
        Assert.Equal(Play(), Play());
    }
}
