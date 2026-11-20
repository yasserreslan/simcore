using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// INF-EE (the owner, 2026-11-20): the Resistance's two engineers, the drone engineer and the sapper, on one engineer base, and
// the FPV drone's surveillance mode (docs/design.md, "Engineers: two types").
public class EngineerTypesTests
{
    private const string Kit = "drone_kit";

    private static void Surveil(World w, Unit pilot, Cell at) =>
        w.Issue(new Command { Tick = w.Tick, Player = pilot.Owner, Kind = CommandKind.UseAbility, Units = [pilot.Id], Cell = at, Ability = Kit, On = true });

    private static Unit LaunchSurveil(World w, Unit pilot, Cell at)
    {
        Surveil(w, pilot, at);
        for (var i = 0; i < 200 && w.All("fpv_drone").Count == 0; i++)
        {
            w.Step();
        }
        return Assert.Single(w.All("fpv_drone"));
    }

    [Fact]
    public void BothEngineersLoadWithTheirTools()
    {
        var w = Arena.New(32);
        var c = w.Content;
        Assert.Null(c.Archetype("engineer")); // the old single engineer is gone (the Sentinel never had him)
        var res = c.Factions.Single(f => f.Id == "resistance");
        var sentinel = c.Factions.Single(f => f.Id == "sentinel");
        var barracks = c.Archetype("resistance_barracks")!.Production!;
        var carbine = c.Component("res_carbine")!.Effects.OfType<WeaponEffect>().Single();
        var pistol = c.Component("res_pistol")!.Effects.OfType<WeaponEffect>().Single();
        string[] drone = ["drone_kit", "drone_station_kit", "kaman_station_kit"], sapper = ["trap_kit", "ap_mine_kit", "at_mine_kit"];
        foreach (var (id, name, cost, own, other) in new[] { ("engineer_drone", "Drone engineer", 195, drone, sapper), ("engineer_sapper", "Sapper", 225, sapper, drone) })
        {
            var a = c.Archetype(id)!;
            Assert.Equal(name, a.Name);
            Assert.True(res.Archetypes[a.Index], id);
            Assert.False(sentinel.Archetypes[a.Index], id);
            Assert.True(barracks.Builds(a.Tags), id);
            var def = w.Player(1)!.Setup.Find(c, id)!;
            Assert.Equal(cost, def.Cost);
            Assert.Same(carbine, def.Weapons[0].Effect); // the carbine is his main arm
            Assert.Same(pistol, def.Weapons[def.Sidearm].Effect); // the pistol his sidearm
            Assert.Single(def.Repairs); // both repair
            Assert.All(own, k => Assert.NotNull(def.Ability(c.Component(k)!.Index)));
            Assert.All(other, k => Assert.Null(def.Ability(c.Component(k)!.Index)));
        }
        // The sapper's mines have the assault's limits; his trap kit is today's.
        var sap = w.Player(1)!.Setup.Find(c, "engineer_sapper")!;
        var rifleman = w.Player(1)!.Setup.Find(c, "Rifleman")!;
        foreach (var (kit, max) in new[] { ("trap_kit", 3), ("ap_mine_kit", 8), ("at_mine_kit", 4) })
        {
            var ab = (PlacerEffect)sap.Ability(c.Component(kit)!.Index)!.Effect;
            Assert.Equal(max, ab.MaxActive);
            if (kit != "trap_kit")
            {
                Assert.Same(rifleman.Ability(c.Component(kit)!.Index)!.Effect, ab);
            }
        }
    }

    [Theory]
    [InlineData("engineer_drone")]
    [InlineData("engineer_sapper")]
    public void BothEngineersRepair(string id)
    {
        var w = Arena.New(32);
        var car = w.Spawn(1, "technical", 14, 10);
        car.HP = car.Def.MaxHp - 60;
        var eng = w.Spawn(1, id, 8, 10);
        w.Order(1, CommandKind.Repair, [eng], target: car.Id);
        for (var i = 0; i < 600 && car.HP < car.Def.MaxHp; i++)
        {
            w.Step();
        }
        Assert.Equal(car.Def.MaxHp, car.HP);
    }

    // Laying one more than his limit lifts his oldest: at most 3 traps, 8 AP and 4 AT mines of his on the map.
    [Fact]
    public void TheSapperLaysTrapsAndMinesWithinHisLimits()
    {
        var w = Arena.New(48);
        var sapper = w.Spawn(1, "engineer_sapper", 10, 10);
        var y = 16;
        foreach (var (kit, laid, max) in new[] { ("trap_kit", "booby_trap", 3), ("ap_mine_kit", "ap_mine", 8), ("at_mine_kit", "at_mine", 4) })
        {
            var stock = sapper.Def.Ability(w.Content.Component(kit)!.Index)!.Stock;
            for (var i = 0; i <= max; i++)
            {
                sapper.MutableCharges[stock] = 1; // a depot's refill, by hand
                w.Order(1, CommandKind.UseAbility, [sapper], cell: new Cell(4 + 2 * i, y), ability: kit);
                w.Step(); // the order lands
                for (var t = 0; t < 400 && sapper.Order.Kind == OrderKind.UseAbility; t++)
                {
                    w.Step();
                }
                Assert.Equal(0, sapper.Charges[stock]);
            }
            w.Step();
            Assert.Equal(max, w.All(laid).Count(m => m.Alive && m.Source == sapper.Id));
            y += 6;
        }
    }

    [Fact]
    public void TheDroneEngineerLaunchesToAttackAndForSurveillance()
    {
        var w = Arena.New(48);
        var pilot = w.Spawn(1, "engineer_drone", 5, 30);
        var target = w.Spawn(2, "technical", 15, 30);
        w.Order(1, CommandKind.UseAbility, [pilot], target.Id, ability: Kit);
        w.Run(100);
        Assert.True(target.HP < target.Def.MaxHp); // attack, as today: it flew there and went off
        Assert.Empty(w.All("fpv_drone"));

        var point = new Cell(15, 20);
        var fpv = LaunchSurveil(w, pilot, point);
        Assert.True(fpv.Surveil);
        Assert.Equal(OrderKind.Idle, fpv.Order.Kind);
        Assert.InRange(fpv.Lifetime, 3590, 3600); // the 3-minute battery
        Assert.NotNull(fpv.Def.Archetype.AirBody); // an air target (the fpv class) as in attack mode
        var radius = fpv.Def.Archetype.Surveil!.Radius;
        var seen = new HashSet<int>();
        for (var i = 0; i < 300; i++)
        {
            w.Step();
            if (i >= 100)
            {
                var off = (fpv.Pos - point.Center()).Len();
                Assert.InRange(off.Raw, radius.Raw - (Fixed.OneRaw / 2), radius.Raw + (Fixed.OneRaw / 2)); // round the point, 3 cells out
                seen.Add(fpv.Facing);
            }
            if (i % 50 == 0)
            {
                Assert.Equal(w.HashReference(), w.Hash());
            }
        }
        Assert.True(fpv.Alive && fpv.Surveil); // circling, not gone off at the point
        Assert.True(seen.Count >= 16, $"it flies round: {seen.Count} headings"); // all the way round
    }

    [Fact]
    public void SurveillanceGivesVisionAndSwitchesToAttackOnAnEnemyItSees()
    {
        var w = Arena.New(64, edit: s => s with { Fog = true });
        var pilot = w.Spawn(1, "engineer_drone", 14, 30, Facing.East);
        var truck = w.Spawn(2, "cargo_truck", 32, 30); // empty and unarmed: it can't shoot back
        var far = w.Spawn(2, "cargo_truck", 44, 30); // 17 cells from the point: out of the drone's sight
        w.Run(10);
        Assert.False(World.IsVisibleTo(truck, 1)); // 18 cells from the engineer (he sees 9)
        var fpv = LaunchSurveil(w, pilot, new Cell(27, 30));
        Assert.Equal(Fixed.FromInt(8), w.SightRange(fpv)); // the surveil reach
        w.Run(120);
        Assert.True(World.IsVisibleTo(truck, 1)); // 5 cells from the point: the drone sees it
        Assert.False(World.IsVisibleTo(far, 1));

        w.Order(1, CommandKind.Attack, [fpv], target: far.Id); // not one it sees: nothing changes
        w.Step();
        Assert.True(fpv.Surveil);
        w.Order(1, CommandKind.Attack, [fpv], target: truck.Id);
        w.Step();
        Assert.False(fpv.Surveil);
        Assert.Equal((OrderKind.Attack, truck.Id), (fpv.Order.Kind, fpv.Order.Target));
        Assert.Equal(fpv.Def.Vision, w.SightRange(fpv)); // its own sight again
        for (var i = 0; i < 200 && w.Unit(fpv.Id) is not null; i++)
        {
            w.Step();
        }
        Assert.Empty(w.All("fpv_drone"));
        Assert.True(truck.HP < truck.Def.MaxHp); // it dived on it
    }

    [Fact]
    public void SurveillanceEndsAtTheBattery()
    {
        var w = Arena.New(48);
        var pilot = w.Spawn(1, "engineer_drone", 5, 20);
        var fpv = LaunchSurveil(w, pilot, new Cell(15, 20));
        var left = fpv.Lifetime;
        w.Run(left - 2);
        Assert.True(fpv.Alive && fpv.Surveil);
        w.Run(4);
        Assert.Empty(w.All("fpv_drone")); // the battery ran out: it crashed
        Assert.Equal(1, pilot.Charges[pilot.Def.Ability(w.Content.Component(Kit)!.Index)!.Stock]); // it spent a drone
    }

    [Fact]
    public void ASurveillingDroneCanBeShotDown()
    {
        var w = Arena.New(48);
        var pilot = w.Spawn(1, "engineer_drone", 5, 20);
        var guard = w.Spawn(2, "Drone hunter", 18, 20, Facing.West);
        var fpv = LaunchSurveil(w, pilot, new Cell(15, 20));
        for (var i = 0; i < 600 && w.Unit(fpv.Id) is not null; i++)
        {
            w.Step();
        }
        Assert.Empty(w.All("fpv_drone"));
        Assert.Equal(guard.Def.MaxHp, guard.HP); // shot down, not gone off on him
    }
}
