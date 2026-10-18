using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Tunnels (docs/design.md, "Tunnels"; the owner's spec of 2026-10-18): three entrances built by
// workers (large: every ground unit, 40 slots; medium: infantry, light and medium vehicles, 20;
// hatch: infantry, 8); a network per side linking entrances within 60 cells, travel at 3 cells a
// second, capacity shared per connected network; vehicles only through medium and large entrances
// (a medium one a junction heavies pass but can't leave by); throughput at the doors; rubble that
// still counts and connects; the survival table when rubble goes; trapped heavies freed by a new
// large entrance; emerging units hurt; stealth and exposure; the ground shaped and given back.
public class TunnelTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int M = 512; // raw per metre

    // Map returns an arena n cells square, on terrain from height (raw; flat 0 without), with fog when asked.
    private static World Map(int n = 128, Func<int, int, int>? height = null, bool fog = false, bool terrain = true)
    {
        var w = Arena.New(n, edit: fog ? s => s with { Fog = true } : null);
        if (terrain)
        {
            var corners = new int[(n + 1) * (n + 1)];
            for (var y = 0; y <= n; y++)
            {
                for (var x = 0; x <= n; x++)
                {
                    corners[y * (n + 1) + x] = height?.Invoke(x, y) ?? 0;
                }
            }
            w.AttachTerrain(new TerrainMap(n, n, corners, new byte[n * n], Rules));
        }
        return w;
    }

    private static Unit Entrance(World w, string id, int x, int y, int facing = 0, byte player = 1) => w.Spawn(player, id, x, y, facing);

    private static void Enter(World w, IEnumerable<Unit> units, Unit e, byte player = 1) =>
        w.Order(player, CommandKind.Enter, units, target: e.Id);

    private static void Go(World w, IEnumerable<Unit> units, Unit e, bool exit, byte player = 1) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.TunnelGo, Units = [.. units.Select(u => u.Id)], Target = e.Id, On = exit });

    // Bury puts u underground at entrance e at once (tests of what happens below).
    private static void Bury(Unit u, Unit e)
    {
        u.Idle();
        u.Inside = World.Underground;
        u.Tunnel = e.Id;
        u.Pos = e.Pos;
    }

    private static int RunUntil(World w, Func<bool> done, int max = 3000)
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

    // Wreck destroys an entrance (one call: rubble; two: gone).
    private static void Wreck(World w, Unit e)
    {
        e.HP = 0;
        w.Step();
    }

    private static Vec Door(Unit e) => World.TunnelDoor(e);

    [Fact]
    public void ContentHasTheThreeEntrancesAndEveryGroundUnitsSize()
    {
        var c = TestContent.Mvp;
        var large = c.Archetype("tunnel_large")!.Tunnel!;
        var medium = c.Archetype("tunnel_medium")!.Tunnel!;
        var hatch = c.Archetype("tunnel_hatch")!.Tunnel!;
        Assert.Equal((3, 40, 20), (large.Size, large.Capacity, large.Throughput));
        Assert.Equal((2, 20, 30), (medium.Size, medium.Capacity, medium.Throughput));
        Assert.Equal((1, 8, 40), (hatch.Size, hatch.Capacity, hatch.Throughput));
        Assert.Equal((1500, 1000, 500), (c.Archetype("tunnel_large")!.MaxHp, c.Archetype("tunnel_medium")!.MaxHp, c.Archetype("tunnel_hatch")!.MaxHp));
        Assert.Equal((800, 500, 200), (c.Archetype("tunnel_large")!.BuildTime, c.Archetype("tunnel_medium")!.BuildTime, c.Archetype("tunnel_hatch")!.BuildTime));
        Assert.False(c.Archetype("tunnel_large")!.Hidden);
        Assert.True(c.Archetype("tunnel_medium")!.Hidden);
        Assert.True(c.Archetype("tunnel_hatch")!.Hidden);
        Assert.Equal(Fixed.FromInt(60), large.LinkRange);
        Assert.Equal(Fixed.FromInt(12), large.Spacing);
        foreach (var id in new[] { "assault", "engineer", "anti_armor", "kornet", "manpads", "sniper", "mortar", "worker" })
        {
            Assert.Equal((1, 1), (c.Archetype(id)!.TunnelSize, c.Archetype(id)!.TunnelSlots));
        }
        foreach (var id in new[] { "technical", "rocket_jeep", "dirt_bike" })
        {
            Assert.Equal((2, 3), (c.Archetype(id)!.TunnelSize, c.Archetype(id)!.TunnelSlots));
        }
        foreach (var id in new[] { "cargo_truck", "infantry_carrier", "sam_vehicle", "drone_truck", "scout_truck" })
        {
            Assert.Equal((2, 5), (c.Archetype(id)!.TunnelSize, c.Archetype(id)!.TunnelSlots));
        }
        foreach (var id in new[] { "heavy_rocket_launcher", "heavy_rocket_truck", "missile_truck", "ballistic_truck" })
        {
            Assert.Equal((3, 8), (c.Archetype(id)!.TunnelSize, c.Archetype(id)!.TunnelSlots));
        }
        foreach (var id in new[] { "scout_drone", "fpv_drone", "ap_mine", "supply_depot" })
        {
            Assert.Equal(0, c.Archetype(id)!.TunnelSize); // aircraft, traps and buildings never go in
        }
    }

    [Theory]
    [InlineData("tunnel_large", 800)]
    [InlineData("tunnel_medium", 500)]
    [InlineData("tunnel_hatch", 200)]
    public void AWorkerBuildsEachEntranceInItsBuildTime(string id, int ticks)
    {
        var w = Map(64);
        var worker = w.Spawn(1, "worker", 20, 32);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = new Cell(20, 20), Unit = id });
        w.Step();
        var site = Assert.Single(w.All(id));
        Assert.False(site.IsBuilt);
        Assert.Empty(w.TunnelNodes(1)); // a site is not a node yet
        var t = RunUntil(w, () => site.IsBuilt, 2000);
        Assert.InRange(t, ticks - 60, ticks + 200); // walking to it, then BuildTime of work
        Assert.Single(w.TunnelNodes(1));
    }

    [Fact]
    public void TheLargeNeedsFlatGroundTheOthersWalkableAndEntrancesKeepTwelveCellsApart()
    {
        // A 15% slope everywhere: too steep for the large, fine for the medium and the hatch.
        var w = Map(96, (x, y) => y * 154);
        var worker = w.Spawn(1, "worker", 40, 30);
        var c = w.Content;
        var large = w.Player(1)!.Setup.Find(c, "tunnel_large")!;
        var medium = w.Player(1)!.Setup.Find(c, "tunnel_medium")!;
        var hatch = w.Player(1)!.Setup.Find(c, "tunnel_hatch")!;
        Assert.False(w.CanPlace(large, new Cell(20, 20), 0, [worker]));
        Assert.True(w.CanPlace(medium, new Cell(20, 20), 0, [worker]));
        Assert.True(w.CanPlace(hatch, new Cell(20, 20), 0, [worker]));
        Entrance(w, "tunnel_hatch", 20, 20);
        Assert.False(w.CanPlace(hatch, new Cell(28, 20), 0, [worker])); // centres 8 cells apart
        Assert.True(w.CanPlace(hatch, new Cell(34, 20), 0, [worker]));  // 14 apart, shaped ground clear
        Assert.False(w.CanPlace(medium, new Cell(23, 26), 0, [worker])); // on its shaped ground
        // Flat ground takes the large; the enemy's entrances count for spacing too.
        var flat = Map(96);
        var wk = flat.Spawn(1, "worker", 40, 30);
        Assert.True(flat.CanPlace(large, new Cell(20, 20), 0, [wk]));
        Entrance(flat, "tunnel_hatch", 26, 12, player: 2);
        Assert.False(flat.CanPlace(large, new Cell(20, 20), 0, [wk]));
    }

    [Fact]
    public void TheGroundIsShapedFlatFootprintAndApronMoundOverTheHeadwallWingWallsAndABlend()
    {
        var w = Map(96);
        var e = Entrance(w, "tunnel_large", 40, 40); // mouth south: back row y 40, front y 46, apron to y 49
        var g = w.Ground!;
        var mound = 70 * M / 10;
        Assert.Equal(0, e.Variant); // flat land: standing alone
        for (var x = 41; x <= 45; x++)
        {
            Assert.Equal(mound, g.Corner(x, 40).Raw); // the earth behind the headwall
            Assert.Equal(0, g.Corner(x, 41).Raw);     // the floor under its recess
            Assert.Equal(0, g.Corner(x, 42).Raw);     // the ramp
            Assert.Equal(0, g.Corner(x, 49).Raw);     // the apron
        }
        Assert.Equal(mound * 4 / 6, g.Corner(40, 42).Raw); // the wing walls fall toward the mouth
        Assert.Equal(mound * 1 / 6, g.Corner(46, 45).Raw);
        Assert.Equal(0, g.Corner(40, 46).Raw);
        Assert.Equal(mound * 4 / 5, g.Corner(43, 39).Raw); // easing back behind: 4/5, 3/5 ... over 4 cells
        Assert.Equal(mound * 1 / 5, g.Corner(43, 36).Raw);
        Assert.Equal(0, g.Corner(43, 35).Raw);             // and the land beyond is untouched
        Assert.True(g.Pad(new Cell(43, 47)));               // pad ground on the apron
        // The hatch on flat ground is a flat shaft: no mound.
        var h = Entrance(w, "tunnel_hatch", 10, 10);
        Assert.Equal(0, h.Variant);
        Assert.Equal(0, g.Corner(10, 10).Raw);
    }

    [Fact]
    public void TheLookIsPickedFromTheLandBehindAndTheFacingDownhill()
    {
        // Flat south of y = 38, rising 25% to the north of it.
        static int Hill(int x, int y) => y < 38 ? (38 - y) * 256 : 0;
        var w = Map(96, Hill);
        var large = w.Player(1)!.Setup.Find(w.Content, "tunnel_large")!;
        var hatch = w.Player(1)!.Setup.Find(w.Content, "tunnel_hatch")!;
        Assert.Equal(0, w.TunnelFacingAt(large, new Cell(40, 40))); // mouth south, away from the hill
        Assert.Equal(1, w.TunnelVariantAt(large, new Cell(40, 40), 0));
        Assert.Equal(0, w.TunnelVariantAt(large, new Cell(40, 40), 16)); // turned to face the hill: nothing rises behind
        var e = Entrance(w, "tunnel_large", 40, 40, 0);
        Assert.Equal(1, e.Variant);
        Assert.Equal(80 * M / 10, w.Ground!.Corner(43, 40).Raw); // the hillside mound height
        Assert.Equal(1, w.TunnelVariantAt(hatch, new Cell(20, 39), 0)); // a hatch on the slope: a small tilted entrance
        Assert.Equal(0, w.TunnelVariantAt(hatch, new Cell(20, 60), 0)); // on flat ground: a shaft
    }

    [Fact]
    public void EntrancesWithinSixtyCellsLinkAndOnlyTheSameSides()
    {
        var w = Map(160, terrain: false);
        var a = Entrance(w, "tunnel_hatch", 10, 10);
        var b = Entrance(w, "tunnel_hatch", 68, 10);  // 58 cells
        var c = Entrance(w, "tunnel_hatch", 130, 10); // 62 from b
        var enemy = Entrance(w, "tunnel_hatch", 40, 40, player: 2);
        Assert.True(World.TunnelLinked(a, b));
        Assert.False(World.TunnelLinked(b, c));
        Assert.False(World.TunnelLinked(a, enemy));
        Assert.Equal([a, b], w.TunnelNetwork(a));
        Assert.Equal([c], w.TunnelNetwork(c));
        Assert.Equal((0, 16), w.TunnelLoad(a));
    }

    [Fact]
    public void EachEntranceTakesOnlyWhatFitsAndOnlyItsOwnSide()
    {
        var w = Map(96, terrain: false);
        var hatch = Entrance(w, "tunnel_hatch", 10, 10);
        var medium = Entrance(w, "tunnel_medium", 30, 10);
        var large = Entrance(w, "tunnel_large", 50, 10);
        var (tech, _) = w.SpawnCrewed(1, "technical", 32, 22);
        var (heavy, _) = w.SpawnCrewed(1, "heavy_rocket_truck", 52, 24);
        var (heavy2, _) = w.SpawnCrewed(1, "missile_truck", 36, 24);
        var rifle = w.Spawn(1, "Rifleman", 11, 15);
        var foe = w.Spawn(2, "Rifleman", 12, 15);
        Enter(w, [tech], hatch);
        Enter(w, [heavy2], medium);
        Enter(w, [foe], hatch, player: 2);
        w.Step();
        Assert.Equal(OrderKind.Idle, tech.Order.Kind);   // a vehicle never fits a hatch
        Assert.Equal(OrderKind.Idle, heavy2.Order.Kind); // a heavy never goes into a medium entrance
        Assert.Equal(OrderKind.Idle, foe.Order.Kind);    // only the builder's side uses its entrances
        Enter(w, [rifle], hatch);
        Enter(w, [tech], medium);
        Enter(w, [heavy], large);
        Assert.True(RunUntil(w, () => rifle.InTunnel && tech.InTunnel && heavy.InTunnel, 600) > 0);
        Assert.Equal(hatch.Id, rifle.Tunnel);
        Assert.Equal(medium.Id, tech.Tunnel);
        Assert.Equal(large.Id, heavy.Tunnel);
        Assert.False(foe.InTunnel);
    }

    [Theory]
    [InlineData("tunnel_hatch", 40)]
    [InlineData("tunnel_medium", 30)]
    [InlineData("tunnel_large", 20)]
    public void UnitsGoInOneAtATimeAtTheDoorsThroughput(string id, int gap)
    {
        var w = Map(64, terrain: false);
        var e = Entrance(w, id, 20, 10);
        var door = Cell.Of(Door(e));
        var squad = Enumerable.Range(0, 3).Select(i => w.Spawn(1, "Rifleman", door.X - 1 + i, door.Y)).ToList();
        Enter(w, squad, e);
        var inAt = new int[3];
        for (var t = 1; t <= 400 && inAt.Any(v => v == 0); t++)
        {
            w.Step();
            for (var i = 0; i < 3; i++)
            {
                if (inAt[i] == 0 && squad[i].InTunnel)
                {
                    inAt[i] = t;
                }
            }
        }
        var order = inAt.Order().ToArray();
        Assert.All(inAt, v => Assert.NotEqual(0, v));
        Assert.Equal(gap, order[1] - order[0]);
        Assert.Equal(gap, order[2] - order[1]);
        Assert.True(e.ExposeLeft > 0);
    }

    [Fact]
    public void ANetworkSharesItsCapacityAndOverflowWaitsWithoutDying()
    {
        var w = Map(96, terrain: false);
        var a = Entrance(w, "tunnel_hatch", 20, 10);
        var door = Cell.Of(Door(a));
        var squad = Enumerable.Range(0, 10).Select(i => w.Spawn(1, "Rifleman", door.X - 5 + i, door.Y + 2)).ToList();
        Enter(w, squad, a);
        w.Run(800);
        Assert.Equal(8, squad.Count(u => u.InTunnel)); // a hatch holds 8
        Assert.Equal((8, 8), w.TunnelLoad(a));
        Assert.All(squad, u => Assert.True(u.Alive));
        Assert.All(squad.Where(u => !u.InTunnel), u => Assert.Equal(OrderKind.Enter, u.Order.Kind)); // still waiting
        var b = Entrance(w, "tunnel_hatch", 50, 10); // linked: the network holds 16
        Assert.Equal((8, 16), w.TunnelLoad(b));
        w.Run(200);
        Assert.Equal(10, squad.Count(u => u.InTunnel));
    }

    [Fact]
    public void ACarrierTakesFiveSlotsAndOneAPerRider()
    {
        var w = Map(96, terrain: false);
        var large = Entrance(w, "tunnel_large", 20, 10);
        var (carrier, _) = w.SpawnCrewed(1, "infantry_carrier", 23, 22);
        for (var i = 0; i < 6; i++)
        {
            World.Ride(w.Spawn(1, "Rifleman", 23, 22), carrier);
        }
        Assert.Equal(11, w.TunnelSlotsOf(carrier));
        Enter(w, [carrier], large);
        Assert.True(RunUntil(w, () => carrier.InTunnel, 600) > 0);
        Assert.Equal((11, 40), w.TunnelLoad(large));
        Assert.Equal(6, w.PassengersOf(carrier).Count); // the riders stay inside it
    }

    [Fact]
    public void UnitsTravelAtThreeCellsASecondAlongTheLinks()
    {
        var w = Map(160, terrain: false);
        var a = Entrance(w, "tunnel_hatch", 10, 10);
        var b = Entrance(w, "tunnel_hatch", 60, 10); // 50 cells
        var c = Entrance(w, "tunnel_hatch", 110, 10); // 50 more; 100 from a: no direct link
        var rifle = w.Spawn(1, "Rifleman", 10, 20);
        Bury(rifle, a);
        Go(w, [rifle], c, false);
        w.Step();
        Assert.Equal(b.Id, rifle.TunnelNext); // the way runs through b
        var leg = (int)((50L * Fixed.OneRaw + 152) / 153); // 3 cells a second = 153 raw a tick
        Assert.Equal(leg, rifle.TunnelLeg);
        var t = RunUntil(w, () => rifle.Tunnel == c.Id && rifle.TunnelNext == 0, 2000);
        Assert.InRange(t, 2 * leg - 3, 2 * leg + 3); // 100 cells in about 33 s
        Assert.True(rifle.InTunnel); // told to wait below: it stays there
    }

    [Fact]
    public void VehiclesUseOnlyMediumAndLargeEntrancesAndAMediumJunctionPassesAHeavyTruck()
    {
        var w = Map(160, terrain: false);
        var l1 = Entrance(w, "tunnel_large", 10, 10);
        var m = Entrance(w, "tunnel_medium", 60, 10);
        var l2 = Entrance(w, "tunnel_large", 110, 10);
        var (heavy, driver) = w.SpawnCrewed(1, "heavy_rocket_truck", 13, 30);
        Bury(heavy, l1);
        Go(w, [heavy], m, true); // a heavy can't come out of a medium entrance
        w.Step();
        Assert.Equal(0u, heavy.TunnelTo);
        Go(w, [heavy], l2, true); // but passes through it to the far large one
        w.Step();
        Assert.Equal(m.Id, heavy.TunnelNext);
        Assert.True(RunUntil(w, () => !heavy.InTunnel, 2000) > 0);
        Assert.True(Within(heavy.Pos, Door(l2), 1));
        Assert.Equal(heavy.Id, driver.Inside); // its driver came up with it
        // A hatch never links vehicles: medium - hatch - medium has no way for a technical.
        var w2 = Map(160, terrain: false);
        var m1 = Entrance(w2, "tunnel_medium", 10, 10);
        var h = Entrance(w2, "tunnel_hatch", 60, 10);
        var m2 = Entrance(w2, "tunnel_medium", 110, 10);
        var (tech, _) = w2.SpawnCrewed(1, "technical", 12, 30);
        var rifle = w2.Spawn(1, "Rifleman", 12, 30);
        Bury(tech, m1);
        Bury(rifle, m1);
        Go(w2, [tech, rifle], m2, true);
        w2.Step();
        Assert.Equal(0u, tech.TunnelTo);   // no way for it
        Assert.Equal(m2.Id, rifle.TunnelTo); // infantry go through the hatch
        Assert.Equal(h.Id, rifle.TunnelNext);
    }

    private static bool Within(Vec a, Vec b, int cells) => (a - b).LenSq() <= (long)cells * cells * Fixed.OneRaw * Fixed.OneRaw;

    [Fact]
    public void UnitsComeOutOneByOneInTheOrderTheyArrivedAndABlockedDoorPauses()
    {
        var w = Map(64, terrain: false);
        var e = Entrance(w, "tunnel_hatch", 20, 10);
        var squad = Enumerable.Range(0, 3).Select(_ => w.Spawn(1, "Rifleman", 5, 40)).ToList();
        foreach (var u in squad)
        {
            Bury(u, e);
        }
        squad[0].TunnelSince = 5; // the first ID arrived last
        squad[1].TunnelSince = 1;
        squad[2].TunnelSince = 2;
        var door = Cell.Of(Door(e));
        var foe = w.Spawn(2, "Rifleman", door.X, door.Y);
        foe.Stance = Stance.HoldFire;
        Go(w, squad, e, true);
        w.Run(100);
        Assert.All(squad, u => Assert.True(u.InTunnel)); // an enemy stands in the door
        foe.Pos = new Cell(50, 50).Center();
        var outAt = new int[3];
        for (var t = 1; t <= 300 && outAt.Any(v => v == 0); t++)
        {
            w.Step();
            for (var i = 0; i < 3; i++)
            {
                if (outAt[i] == 0 && !squad[i].InTunnel)
                {
                    outAt[i] = t;
                }
            }
        }
        Assert.True(outAt[1] < outAt[2] && outAt[2] < outAt[0]);
        Assert.True(outAt[2] - outAt[1] >= 40 && outAt[0] - outAt[2] >= 40); // one per 2 s at a hatch
        Assert.Equal(OrderKind.Move, squad[0].Order.Kind); // each walks clear of the door
    }

    [Fact]
    public void ADestroyedEntranceIsRubbleThatStillCountsAndConnectsButLetsNobodyThrough()
    {
        var w = Map(160, terrain: false);
        var a = Entrance(w, "tunnel_hatch", 10, 10);
        var b = Entrance(w, "tunnel_hatch", 60, 10);
        var c = Entrance(w, "tunnel_hatch", 110, 10);
        var rifle = w.Spawn(1, "Rifleman", 10, 30);
        var outside = w.Spawn(1, "Rifleman", 61, 17);
        Bury(rifle, a);
        Wreck(w, b);
        Assert.True(b.Rubble);
        Assert.Equal(250, b.HP);
        Assert.Equal((1, 24), w.TunnelLoad(a)); // its capacity still counts
        Go(w, [rifle], c, true);                // and the way runs through it
        Enter(w, [outside], b);                 // but nobody goes in there
        w.Step();
        Assert.Equal(b.Id, rifle.TunnelNext);
        Assert.Equal(OrderKind.Idle, outside.Order.Kind);
        Go(w, [rifle], b, true); // nor comes out
        w.Step();
        Assert.Equal(c.Id, rifle.TunnelTo);
        Assert.True(RunUntil(w, () => !rifle.InTunnel, 2000) > 0);
        Assert.True(Within(rifle.Pos, Door(c), 2));
    }

    [Fact]
    public void UnitsHeadingOutAtAnEntranceThatFallsWaitBelow()
    {
        var w = Map(96, terrain: false);
        var a = Entrance(w, "tunnel_hatch", 10, 10);
        var b = Entrance(w, "tunnel_hatch", 60, 10);
        var rifle = w.Spawn(1, "Rifleman", 10, 30);
        Bury(rifle, a);
        Go(w, [rifle], b, true);
        w.Run(20);
        Wreck(w, b);
        Assert.Equal(0u, rifle.TunnelTo);
        w.Run(600);
        Assert.True(rifle.InTunnel);
        Assert.Equal(b.Id, rifle.Tunnel); // it finished its leg and waits under the rubble
    }

    [Fact]
    public void WithALargeEntranceLeftEveryoneSurvivesAndComesOut()
    {
        var w = Map(160, terrain: false);
        var l = Entrance(w, "tunnel_large", 10, 10);
        var m = Entrance(w, "tunnel_medium", 60, 10);
        var (heavy, _) = w.SpawnCrewed(1, "heavy_rocket_truck", 5, 60);
        var (tech, _) = w.SpawnCrewed(1, "technical", 6, 60);
        var rifle = w.Spawn(1, "Rifleman", 7, 60);
        foreach (var u in new[] { heavy, tech, rifle })
        {
            Bury(u, m);
        }
        Wreck(w, m);
        Wreck(w, m); // rubble gone
        Assert.All(new[] { heavy, tech, rifle }, u => Assert.True(u.Alive && u.InTunnel && u.Tunnel == l.Id));
        Assert.False(w.TunnelTrapped(heavy));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.TunnelEvacuate, Units = [l.Id] });
        Assert.True(RunUntil(w, () => !heavy.InTunnel && !tech.InTunnel && !rifle.InTunnel, 600) > 0);
    }

    [Fact]
    public void WithOnlyMediumLeftHeaviesSurviveTrappedUntilALargeEntranceReconnects()
    {
        var w = Map(160, terrain: false);
        var l = Entrance(w, "tunnel_large", 10, 10);
        var m = Entrance(w, "tunnel_medium", 60, 10);
        var (heavy, driver) = w.SpawnCrewed(1, "heavy_rocket_truck", 5, 60);
        var (tech, _) = w.SpawnCrewed(1, "technical", 6, 60);
        var rifle = w.Spawn(1, "Rifleman", 7, 60);
        foreach (var u in new[] { heavy, tech, rifle })
        {
            Bury(u, l);
        }
        Wreck(w, l);
        Wreck(w, l);
        Assert.All(new[] { heavy, tech, rifle }, u => Assert.True(u.Alive && u.InTunnel && u.Tunnel == m.Id));
        Assert.True(w.TunnelTrapped(heavy));
        Assert.False(w.TunnelTrapped(tech));
        Assert.False(w.TunnelTrapped(rifle));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.TunnelEvacuate, Units = [m.Id] });
        Assert.True(RunUntil(w, () => !tech.InTunnel && !rifle.InTunnel, 600) > 0);
        Assert.True(heavy.InTunnel && heavy.Alive); // trapped: it stays
        // A new large entrance within reach frees it.
        var l2 = Entrance(w, "tunnel_large", 110, 10);
        Assert.False(w.TunnelTrapped(heavy));
        Go(w, [heavy], l2, true);
        Assert.True(RunUntil(w, () => !heavy.InTunnel, 2000) > 0);
        Assert.Equal(heavy.Id, driver.Inside);
    }

    [Fact]
    public void WithOnlyHatchesLeftVehiclesAreDestroyedAndTheirCrewsLiveOnBelow()
    {
        var w = Map(160, terrain: false);
        var m = Entrance(w, "tunnel_medium", 10, 10);
        var h = Entrance(w, "tunnel_hatch", 60, 10);
        var (carrier, driver) = w.SpawnCrewed(1, "infantry_carrier", 5, 60);
        var riders = Enumerable.Range(0, 3).Select(_ => w.Spawn(1, "Rifleman", 5, 60)).ToList();
        foreach (var r in riders)
        {
            World.Ride(r, carrier);
        }
        var (tech, techDriver) = w.SpawnCrewed(1, "technical", 6, 60);
        var rifle = w.Spawn(1, "Rifleman", 7, 60);
        foreach (var u in new[] { carrier, tech, rifle })
        {
            Bury(u, m);
        }
        Wreck(w, m);
        Wreck(w, m);
        Assert.False(carrier.Alive);
        Assert.False(tech.Alive);
        Assert.True(rifle.Alive && rifle.InTunnel && rifle.Tunnel == h.Id);
        foreach (var crew in riders.Append(driver).Append(techDriver))
        {
            Assert.True(crew.Alive && crew.InTunnel && crew.Tunnel == h.Id); // infantry survive
        }
        Assert.DoesNotContain(carrier, w.Units);
    }

    [Fact]
    public void WithNothingLeftEveryoneIsDestroyed()
    {
        var w = Map(96, terrain: false);
        var h = Entrance(w, "tunnel_hatch", 10, 10);
        var far = Entrance(w, "tunnel_hatch", 80, 10); // 70 cells: not linked
        var squad = Enumerable.Range(0, 3).Select(_ => w.Spawn(1, "Rifleman", 5, 60)).ToList();
        foreach (var u in squad)
        {
            Bury(u, h);
        }
        Wreck(w, h);
        Assert.All(squad, u => Assert.True(u.Alive)); // rubble: still below, alive
        Wreck(w, h);
        Assert.All(squad, u => Assert.False(u.Alive));
        Assert.True(far.Alive);
    }

    [Fact]
    public void UnitsComingOutAreHurtWhenTheEntranceIsDestroyed()
    {
        var w = Map(64, terrain: false);
        var e = Entrance(w, "tunnel_large", 20, 10);
        var rifle = w.Spawn(1, "Rifleman", 5, 50);
        var far = w.Spawn(1, "Rifleman", 6, 50);
        Bury(rifle, e);
        Go(w, [rifle], e, true);
        Assert.True(RunUntil(w, () => !rifle.InTunnel, 100) > 0);
        Assert.True(rifle.EmergeLeft > 0);
        Wreck(w, e);
        Assert.Equal(rifle.Def.MaxHp * 60 / 100, rifle.HP); // 40% of its HP
        Assert.Equal(far.Def.MaxHp, far.HP);
    }

    [Fact]
    public void UndergroundUnitsCantBeSeenOrderedOrHit()
    {
        var w = Map(64, terrain: false);
        var e = Entrance(w, "tunnel_large", 20, 10);
        var rifle = w.Spawn(1, "Rifleman", 23, 20);
        var foe = w.Spawn(2, "Rifleman", 26, 20);
        Bury(rifle, e);
        w.Order(1, CommandKind.Move, [rifle], cell: new Cell(5, 5));
        w.Run(60);
        Assert.True(rifle.InTunnel);
        Assert.Equal(OrderKind.Idle, rifle.Order.Kind);
        Assert.False(World.IsVisibleTo(rifle, 2));
        Assert.Equal(rifle.Def.MaxHp, rifle.HP);
        Assert.NotEqual(rifle.Id, foe.AutoTarget);
    }

    [Fact]
    public void MediumAndHatchesHideUntilDetectedAndPassingTheDoorShowsThemToWhoSeesThere()
    {
        var w = Map(96, fog: true);
        var m = Entrance(w, "tunnel_medium", 20, 20);
        var large = Entrance(w, "tunnel_large", 60, 20);
        var watcher = w.Spawn(2, "Rifleman", 26, 29);   // 6 cells from the medium entrance: sees it
        watcher.Stance = Stance.HoldFire;
        var blind = w.Spawn(2, "Rifleman", 63, 32);     // sees the large
        blind.Stance = Stance.HoldFire;
        w.Run(5);
        Assert.False(World.IsVisibleTo(m, 2));    // hidden
        Assert.True(World.IsVisibleTo(large, 2)); // the large stands like a building
        var rifle = w.Spawn(1, "Rifleman", 22, 29);
        Enter(w, [rifle], m);
        Assert.True(RunUntil(w, () => rifle.InTunnel, 200) > 0);
        w.Step();
        Assert.True(World.IsVisibleTo(m, 2)); // seen going in
        w.Run(200);
        Assert.False(World.IsVisibleTo(m, 2)); // about 10 s later it is hidden again
        // A detector finds it for good.
        var sniper = w.Spawn(2, "Marksman", 24, 27); // within its 5-cell detect radius
        sniper.Stance = Stance.HoldFire;
        w.Run(3);
        Assert.True(World.IsVisibleTo(m, 2));
        Assert.True((m.RevealedTo & (1u << 2)) != 0);
    }

    [Fact]
    public void ExposureShowsAnEntranceOnlyWhereTheEnemyHasVision()
    {
        var w = Map(96, fog: true);
        var h = Entrance(w, "tunnel_hatch", 20, 20);
        var faraway = w.Spawn(2, "Rifleman", 85, 85);
        faraway.Stance = Stance.HoldFire;
        var rifle = w.Spawn(1, "Rifleman", 21, 24);
        Enter(w, [rifle], h);
        Assert.True(RunUntil(w, () => rifle.InTunnel, 200) > 0);
        w.Step();
        Assert.True(h.ExposeLeft > 0);
        Assert.False(World.IsVisibleTo(h, 2)); // nobody of player 2 sees there: not through fog
    }

    [Fact]
    public void TheGroundSlowlyReturnsOnceTheRubbleIsGone()
    {
        static int Land(int x, int y) => (x * 37 + y * 11) % 300;
        var w = Map(96, Land);
        var before = w.Ground!.ReadCorners(30, 30, 20, 20);
        var e = Entrance(w, "tunnel_medium", 38, 36);
        Assert.NotEqual(before, w.Ground!.ReadCorners(30, 30, 20, 20));
        Wreck(w, e);
        var shaped = w.Ground!.ReadCorners(30, 30, 20, 20);
        Wreck(w, e);
        w.Run(300);
        var half = w.Ground!.ReadCorners(30, 30, 20, 20);
        Assert.NotEqual(shaped, half);
        Assert.NotEqual(before, half); // half way back after 15 s
        w.Run(320);
        Assert.Equal(before, w.Ground!.ReadCorners(30, 30, 20, 20)); // back after 30 s
        Assert.False(w.Ground!.Pad(new Cell(40, 43)));
    }

    [Fact]
    public void ACancelledSiteGivesItsGroundBack()
    {
        var w = Map(64);
        var worker = w.Spawn(1, "worker", 20, 32);
        var before = w.Ground!.ReadCorners(10, 10, 30, 30);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = new Cell(20, 20), Unit = "tunnel_large" });
        w.Step();
        var site = Assert.Single(w.All("tunnel_large"));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.CancelBuild, Target = site.Id });
        w.Run(640);
        Assert.Equal(before, w.Ground!.ReadCorners(10, 10, 30, 30));
    }

    [Fact]
    public void TheSameCommandsGiveTheSameHashes()
    {
        ulong[] Run()
        {
            var w = Map(128);
            var a = Entrance(w, "tunnel_large", 10, 10);
            var b = Entrance(w, "tunnel_medium", 60, 10);
            var squad = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 11 + i, 22)).ToList();
            var (tech, _) = w.SpawnCrewed(1, "technical", 13, 26);
            Enter(w, [.. squad, tech], a);
            var hashes = new ulong[900];
            for (var i = 0; i < hashes.Length; i++)
            {
                if (i == 300)
                {
                    Go(w, [.. squad, tech], b, true);
                }
                if (i == 700)
                {
                    a.HP = 0;
                }
                w.Step();
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }
}
