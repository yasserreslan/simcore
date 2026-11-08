using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Vehicle shelters (decisions 1000-1014): the machine shed, the repair workshop and the loading warehouse.
public class ShelterTests(ITestOutputHelper output)
{
    private const int SX = 30, SY = 30; // where the shed is spawned

    private static Unit Vehicle(World w, byte player, string name, int x, int y)
    {
        var v = w.Spawn(player, name, x, y);
        if (v.Def.Crew is not null)
        {
            World.Board(w.Spawn(player, "Rifleman", x, y), v);
        }
        v.Stance = Stance.HoldFire;
        return v;
    }

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

    // Refusals collects every order refusal of the next ticks until done (or max ticks).
    private static List<OrderRefusal> RunCollecting(World w, Func<bool> done, int max)
    {
        var all = new List<OrderRefusal>();
        for (var t = 0; t < max; t++)
        {
            w.Step(); // at least one: the command runs in it
            all.AddRange(w.OrderRefusals);
            if (done())
            {
                break;
            }
        }
        return all;
    }

    private static Cell Door(Unit b, int i) => World.ShelterDoorCell(b, i);

    private static World Fogged(ulong seed = 1) => Arena.NewSentinel(64, seed, s => s with { Fog = true });

    // The fit lists: a vehicle fits when its real size is no longer than the 9.6 m bay and no wider (3.6 m) or taller (4.6 m)
    // than the door. Every vehicle has a size; the three buildings take the same list.
    [Fact]
    public void FitListsComeFromRealSizes()
    {
        string[] fit =
        [
            "cargo_truck", "decoy_cargo_truck", "decoy_mobile_radar", "dirt_bike", "drone_truck", "humvee", "infantry_carrier",
            "kaman_truck", "kub_launcher", "light_rocket_truck", "light_tank", "medium_rocket_launcher", "mobile_radar",
            "rocket_jeep", "sam_vehicle", "scout_truck", "sentinel_cargo_truck", "technical",
        ];
        var c = TestContent.Mvp;
        var sized = c.Archetypes.Where(a => a.Size is not null).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        Assert.Equal(38, sized.Count);
        Assert.All(c.Archetypes.Where(a => a.Locomotor is Locomotor.Wheeled or Locomotor.Tracked), a => Assert.NotNull(a.Size));
        foreach (var id in new[] { "machine_shed", "repair_workshop", "loading_warehouse" })
        {
            var s = c.Archetype(id)!.Shelter!;
            var ins = sized.Where(a => s.Fits(a.Size!.Value)).Select(a => a.Id).ToArray();
            Assert.Equal(fit, ins);
            output.WriteLine($"{id}: {s.Bays} bays; fit {string.Join(", ", ins)}");
            output.WriteLine($"  too big: {string.Join(", ", sized.Where(a => !s.Fits(a.Size!.Value)).Select(a => $"{a.Id} {a.Size}"))}");
        }
        Assert.Equal(2, c.Archetype("machine_shed")!.Shelter!.Bays);
        Assert.True(c.Archetype("machine_shed")!.Shelter!.Open);
        Assert.Equal(2, c.Archetype("repair_workshop")!.Shelter!.Bays);
        Assert.Equal(4, c.Archetype("loading_warehouse")!.Shelter!.Bays);
        Assert.Equal(new VehicleSize(49, 19, 13), c.Archetype("main_battle_tank")!.Size); // 9.8 x 3.8 m: too long and too wide
        Assert.Equal(new VehicleSize(46, 14, 21), c.Archetype("sam_vehicle")!.Size);      // the Osa, 9.2 x 2.8 m: fits
    }

    // In: 60 ticks (3 s) through the door, on the map; then off it. Out: 60 ticks in the door, then at the door.
    [Fact]
    public void EnteringAndLeavingTakeTheDoorTime()
    {
        var w = Arena.NewSentinel(64);
        var shed = w.Spawn(2, "machine_shed", SX, SY);
        Assert.Equal(0, shed.Owner);
        var d0 = Door(shed, 0);
        var hv = Vehicle(w, 1, "humvee", d0.X, d0.Y + 10);
        w.Order(1, CommandKind.Enter, [hv], shed.Id);
        var approach = RunUntil(w, () => hv.ShelterStage == ShelterStage.Entering, 400);
        Assert.True(approach > 0);
        Assert.Equal(0u, hv.Inside); // in the door: still on the map
        var through = RunUntil(w, () => hv.InShelter, 200);
        Assert.Equal(60, through);
        Assert.Equal(ShelterStage.In, hv.ShelterStage);
        Assert.Equal(0, hv.ShelterBay); // the bay whose door is nearest
        Assert.Equal(1, shed.ShelterHolder);
        Assert.Equal(0, shed.Owner); // not the colour: nobody else sees who holds it
        output.WriteLine($"humvee 10 cells out: {approach} ticks to the door, {through} through it");

        w.Order(1, CommandKind.Exit, [hv]);
        var leave = RunUntil(w, () => hv.Inside == 0, 200);
        Assert.Equal(61, leave); // the order's own tick, then 60 in the door
        Assert.Equal(0u, hv.Shelter);
        Assert.True(Math.Abs(Cell.Of(hv.Pos).X - d0.X) <= 1 && Math.Abs(Cell.Of(hv.Pos).Y - d0.Y) <= 1);
        w.Step();
        Assert.Equal(0, shed.ShelterHolder);
        output.WriteLine($"out in {leave} ticks");
    }

    // A move order given inside: out through the door first (60 ticks), then on to the cell.
    [Fact]
    public void AMoveOrderInsideExitsFirst()
    {
        var w = Arena.NewSentinel(64);
        var shed = w.Spawn(2, "machine_shed", SX, SY);
        var d1 = Door(shed, 1);
        var t = Vehicle(w, 1, "humvee", d1.X, d1.Y + 1);
        w.Order(1, CommandKind.Enter, [t], shed.Id);
        Assert.True(RunUntil(w, () => t.InShelter, 300) > 0);
        Assert.Equal(1, t.ShelterBay);
        var dest = new Cell(d1.X, 60);
        w.Order(1, CommandKind.Move, [t], cell: dest);
        Assert.Equal(61, RunUntil(w, () => t.Inside == 0, 200)); // the order's tick, then 60 in the door
        Assert.Equal(OrderKind.Move, t.Order.Kind);
        Assert.True(RunUntil(w, () => Cell.Of(t.Pos).Y >= 58, 600) > 0);
    }

    // A group order fills the free bays nearest door first; the rest is refused with a reason: full, too big, enemy-held.
    [Fact]
    public void RefusalsAreFullTooBigAndEnemyHeld()
    {
        var w = Arena.NewSentinel(64);
        var shed = w.Spawn(2, "machine_shed", SX, SY);
        var d0 = Door(shed, 0);
        var group = new[] { Vehicle(w, 1, "humvee", d0.X + 6, d0.Y + 4), Vehicle(w, 1, "humvee", d0.X - 3, d0.Y + 4), Vehicle(w, 1, "humvee", d0.X, d0.Y + 12) };
        var tank = Vehicle(w, 1, "main_battle_tank", d0.X + 10, d0.Y + 10);
        w.Order(1, CommandKind.Enter, [.. group, tank], shed.Id);
        w.Step();
        var why = w.OrderRefusals.ToList();
        Assert.Contains(why, r => r.Aircraft == tank.Id && r.Reason == OrderRefusalReason.ShelterTooBig && r.Target == shed.Id);
        Assert.Contains(why, r => r.Aircraft == group[2].Id && r.Reason == OrderRefusalReason.ShelterFull); // the farthest
        Assert.Equal(1, group[0].ShelterBay); // east: door 1 is nearer
        Assert.Equal(0, group[1].ShelterBay);
        Assert.True(RunUntil(w, () => group[0].InShelter && group[1].InShelter, 600) > 0);

        // Held: the other side's vehicle is refused (it finds out at the door) and its soldiers too.
        var tech = Vehicle(w, 2, "technical", d0.X, d0.Y + 3);
        w.Order(2, CommandKind.Enter, [tech], shed.Id);
        var seen = RunCollecting(w, () => tech.Order.Kind != OrderKind.Shelter, 300);
        Assert.Contains(seen, r => r.Aircraft == tech.Id && r.Reason == OrderRefusalReason.ShelterEnemyHeld);
        Assert.False(tech.InShelter);
        Assert.True(shed.ShelterMarkedFor(2)); // found at the door
        var man = w.Spawn(2, "Rifleman", Door(shed, 1).X, Door(shed, 1).Y + 3); // at the other door (the technical stands at this one)
        w.Order(2, CommandKind.Enter, [man], shed.Id);
        seen = RunCollecting(w, () => man.Order.Kind != OrderKind.Enter, 300);
        Assert.Contains(seen, r => r.Aircraft == man.Id && r.Reason == OrderRefusalReason.ShelterEnemyHeld);
        Assert.Equal(0u, man.Garrison);
    }

    // Two sides go for one neutral shed: the first in holds it; the other is turned away at its door; once the holder's
    // vehicle is out the other side gets in. Soldiers garrisoning it hold it too.
    [Fact]
    public void TwoSidesContestOneShelter()
    {
        var w = Arena.NewSentinel(64);
        var shed = w.Spawn(2, "machine_shed", SX, SY);
        var (d0, d1) = (Door(shed, 0), Door(shed, 1));
        var hv = Vehicle(w, 1, "humvee", d0.X, d0.Y + 4);
        var tech = Vehicle(w, 2, "technical", d1.X, d1.Y + 12);
        w.Order(1, CommandKind.Enter, [hv], shed.Id);
        w.Order(2, CommandKind.Enter, [tech], shed.Id);
        w.Step();
        Assert.Equal(ShelterStage.Approach, tech.ShelterStage); // nothing tells it yet
        var seen = RunCollecting(w, () => tech.Order.Kind != OrderKind.Shelter, 600);
        Assert.True(hv.InShelter);
        Assert.Contains(seen, r => r.Aircraft == tech.Id && r.Reason == OrderRefusalReason.ShelterEnemyHeld);
        Assert.False(tech.InShelter);
        Assert.Equal(1, shed.ShelterHolder);

        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Exit, Units = [shed.Id] });
        Assert.True(RunUntil(w, () => hv.Inside == 0, 100) > 0);
        w.Step();
        Assert.Equal(0, shed.ShelterHolder);
        w.Order(2, CommandKind.Enter, [tech], shed.Id);
        Assert.True(RunUntil(w, () => tech.InShelter, 600) > 0);
        Assert.Equal(2, shed.ShelterHolder);

        // Soldiers of player 2's in it too; out goes the technical; the building still shows them: player 1 is refused at once.
        var men = new[] { w.Spawn(2, "Rifleman", d0.X, d0.Y + 2) };
        w.Order(2, CommandKind.Enter, men, shed.Id);
        Assert.True(RunUntil(w, () => men[0].Garrison == shed.Id, 300) > 0);
        Assert.Equal(2, shed.Owner);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.Exit, Units = [tech.Id] });
        Assert.True(RunUntil(w, () => tech.Inside == 0, 100) > 0);
        hv.Pos = new Cell(d0.X, d0.Y + 6).Center();
        w.Order(1, CommandKind.Enter, [hv], shed.Id);
        w.Step();
        Assert.Contains(w.OrderRefusals, r => r.Aircraft == hv.Id && r.Reason == OrderRefusalReason.ShelterEnemyHeld);
    }

    // Inside it is seen by nothing: soldiers at the door, a camera drone, a thermal drone over it, an F-35's radar spotting
    // (which finds an air radar by its emissions); and nothing can shoot it.
    [Theory]
    [InlineData("ground")]
    [InlineData("camera")]
    [InlineData("thermal")]
    [InlineData("radar")]
    public void HiddenFromEverySensorInside(string sensor)
    {
        var w = Fogged();
        var shed = w.Spawn(2, "repair_workshop", SX, SY);
        var d0 = Door(shed, 0);
        var target = Vehicle(w, 2, sensor == "radar" ? "mobile_radar" : "technical", d0.X, d0.Y + 1);
        var eye = sensor switch
        {
            "ground" => w.Spawn(1, "Rifleman", d0.X + 2, d0.Y + 6),
            "camera" => w.Spawn(1, "watcher_drone", 2, 2),
            "thermal" => w.Spawn(1, "overseer_drone", 2, 2),
            _ => w.Spawn(1, "stealth_multirole", 2, 2),
        };
        eye.Stance = Stance.HoldFire;
        void Look()
        {
            if (sensor != "ground")
            {
                eye.Pos = new Cell(d0.X + 3, d0.Y + 3).Center();
                eye.LookedAt = uint.MaxValue;
            }
            target.Vel = target.InShelter ? target.Vel : new Vec(Fixed.FromRaw(20), Fixed.Zero); // its engine warm (thermal)
            w.Step();
        }
        for (var i = 0; i < 20; i++)
        {
            Look();
        }
        Assert.True(World.IsSpotted(target, 1), $"{sensor}: seen in the open");
        w.Order(2, CommandKind.Enter, [target], shed.Id);
        for (var i = 0; i < 200 && !target.InShelter; i++)
        {
            Look();
        }
        Assert.True(target.InShelter);
        Assert.True(shed.ShelterMarkedFor(1)); // it saw it go in
        for (var i = 0; i < 100; i++)
        {
            Look();
            Assert.False(World.IsVisibleTo(target, 1));
            Assert.False(World.IsSpotted(target, 1));
        }
        var hp = target.HP;
        var gun = Vehicle(w, 1, "humvee", d0.X - 2, d0.Y + 4);
        gun.Stance = Stance.Aggressive;
        w.Order(1, CommandKind.Attack, [gun], target.Id);
        for (var i = 0; i < 100; i++)
        {
            Look();
        }
        Assert.Equal(hp, target.HP);
        Assert.True(shed.ShelterMarkedFor(1)); // it keeps the mark while it sees the doors
    }

    // The mark: a side that saw it go in keeps it while it sees one of the doors; a side that did not never has it.
    [Fact]
    public void TheSeenEnteringMarkLastsWhileTheDoorsAreWatched()
    {
        var w = Fogged();
        var shed = w.Spawn(2, "loading_warehouse", SX, SY);
        var d2 = Door(shed, 2);
        var watcher = w.Spawn(1, "Rifleman", d2.X, d2.Y + 7);
        var tech = Vehicle(w, 2, "technical", d2.X, d2.Y + 2);
        w.Run(10);
        w.Order(2, CommandKind.Enter, [tech], shed.Id);
        Assert.True(RunUntil(w, () => tech.InShelter, 300) > 0);
        Assert.True(shed.ShelterMarkedFor(1));
        Assert.False(shed.ShelterMarkedFor(2));
        w.Run(100);
        Assert.True(shed.ShelterMarkedFor(1));
        w.Order(1, CommandKind.Move, [watcher], cell: new Cell(2, 62));
        var gone = RunUntil(w, () => !shed.ShelterMarkedFor(1), 600);
        Assert.True(gone > 0);
        output.WriteLine($"mark lost {gone} ticks after the watcher walked off");

        // Unwatched: a second technical goes in; player 1 has no mark.
        var t2 = Vehicle(w, 2, "technical", Door(shed, 0).X, Door(shed, 0).Y + 2);
        w.Order(2, CommandKind.Enter, [t2], shed.Id);
        Assert.True(RunUntil(w, () => t2.InShelter, 300) > 0);
        Assert.False(shed.ShelterMarkedFor(1));
    }

    // A blast on the building takes 30% of the HP it loses off each vehicle inside; a bullet nothing.
    [Theory]
    [InlineData("explosive", 300)]
    [InlineData("heavy_rocket", 400)]
    [InlineData("rocket", 120)]
    public void BlastsReachTheVehiclesAt30Percent(string warhead, int damage)
    {
        var w = Arena.NewSentinel(64);
        var shed = w.Spawn(2, "loading_warehouse", SX, SY);
        var vs = Enumerable.Range(0, 2).Select(i => Vehicle(w, 1, "humvee", Door(shed, i).X, Door(shed, i).Y + 2)).ToList();
        w.Order(1, CommandKind.Enter, vs, shed.Id);
        Assert.True(RunUntil(w, () => vs.All(v => v.InShelter), 400) > 0);
        var wh = w.Content.Warhead(warhead)!;
        var (b0, v0) = (shed.HP, vs[0].HP);
        w.Hurt(shed, wh, damage, 100);
        var lost = b0 - shed.HP;
        Assert.True(lost > 0);
        Assert.All(vs, v => Assert.Equal(v0 - lost * 30 / 100, v.HP));
        output.WriteLine($"{warhead} {damage}: building -{lost}, each vehicle -{v0 - vs[0].HP}");
        var bullet = w.Content.Warhead("heavy_bullet")!;
        var (b1, v1) = (shed.HP, vs[0].HP);
        w.Hurt(shed, bullet, 200, 100);
        Assert.Equal(v1, vs[0].HP);
        Assert.True(shed.HP <= b1);
    }

    // The building falls with vehicles inside: each comes out at its door, half (by the dice) survive at 30% HP at most.
    [Fact]
    public void CollapseOutcomesOverSeeds()
    {
        var (survived, total) = (0, 0);
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var w = Arena.NewSentinel(64, seed);
            var shed = w.Spawn(2, "repair_workshop", SX, SY);
            var vs = Enumerable.Range(0, 2).Select(i => Vehicle(w, 1, "sentinel_cargo_truck", Door(shed, i).X, Door(shed, i).Y + 2)).ToList();
            w.Order(1, CommandKind.Enter, vs, shed.Id);
            Assert.True(RunUntil(w, () => vs.All(v => v.InShelter), 400) > 0);
            shed.HP = 1;
            w.Hurt(shed, w.Content.Warhead("explosive")!, 500, 100);
            Assert.False(shed.Alive);
            w.Step();
            foreach (var v in vs)
            {
                total++;
                Assert.Equal(0u, v.Inside);
                Assert.Equal(0u, v.Shelter);
                var door = Door(shed, vs.IndexOf(v));
                Assert.True(Math.Abs(Cell.Of(v.Pos).X - door.X) <= 1 && Math.Abs(Cell.Of(v.Pos).Y - door.Y) <= 1);
                if (v.Alive)
                {
                    survived++;
                    Assert.True(v.HP <= v.Def.MaxHp * 30 / 100);
                }
            }
        }
        output.WriteLine($"collapse: {survived} of {total} vehicles survived over 30 seeds");
        Assert.InRange(survived, 18, 42);
    }

    // A bulldozer starting to clear the building puts the vehicles inside out at their doors first.
    [Fact]
    public void ClearUpPutsTheVehiclesOutFirst()
    {
        var w = Arena.NewSentinel(64);
        var shed = w.Spawn(2, "machine_shed", SX, SY);
        var tech = Vehicle(w, 2, "technical", Door(shed, 0).X, Door(shed, 0).Y + 2);
        w.Order(2, CommandKind.Enter, [tech], shed.Id);
        Assert.True(RunUntil(w, () => tech.InShelter, 300) > 0);
        var hp = tech.HP;
        var dozer = Vehicle(w, 1, "bulldozer", SX - 5, SY + 2);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.ClearArea, Units = [dozer.Id], Cell = new Cell(SX - 2, SY - 2), Cell2 = new Cell(SX + 8, SY + 8) });
        Assert.True(RunUntil(w, () => shed.HP < shed.Def.MaxHp || tech.Inside == 0, 1200) > 0);
        Assert.Equal(0u, tech.Inside);
        Assert.Equal(0u, tech.Shelter);
        Assert.Equal(hp, tech.HP);
        w.Step();
        Assert.Equal(0, shed.ShelterHolder);
    }

    // A radar vehicle inside is off: no air picture from it, and nothing to find by its emissions (the F-35 case above).
    [Fact]
    public void ARadarInsideIsOff()
    {
        var w = Arena.NewSentinel(64);
        var shed = w.Spawn(2, "repair_workshop", SX, SY);
        var radar = Vehicle(w, 2, "mobile_radar", Door(shed, 1).X, Door(shed, 1).Y + 2);
        w.Run(5);
        Assert.True(w.RadarSearching(radar));
        w.Order(2, CommandKind.Enter, [radar], shed.Id);
        Assert.True(RunUntil(w, () => radar.InShelter, 300) > 0);
        Assert.False(w.RadarSearching(radar));
        w.Run(100);
        Assert.False(w.RadarSearching(radar));
    }
}
