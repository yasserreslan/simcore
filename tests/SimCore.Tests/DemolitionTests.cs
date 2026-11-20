using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// INF-EX: the sapper's building trap and remote demolition charge (owner decisions 33 and 34; docs/design.md, "The sapper's
// building trap and demolition charge").
public class DemolitionTests
{
    private static int Stock(World w, Unit u, string kit) => u.Def.Ability(w.Content.Component(kit)!.Index)!.Stock;

    private static Unit Building(World w, string id, int x, int y) =>
        w.Unit(w.Spawn(w.Player(1)!.Setup.Find(w.Content, id)!, new Cell(x, y), Facing.South))!;

    private static void Use(World w, Unit u, Unit target, string kit, bool timer = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.UseAbility, Units = [u.Id], Target = target.Id, Ability = kit, On = timer });

    private static void Detonate(World w, byte player, Unit u) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.Detonate, Units = [u.Id] });

    // Steps until done (at most max ticks), keeping the demolition events; returns the ticks stepped.
    private static int Until(World w, Func<bool> done, int max, List<DemolitionEvent>? ev = null)
    {
        var n = 0;
        while (n < max && !done())
        {
            w.Step();
            ev?.AddRange(w.DemolitionEvents);
            n++;
        }
        return n;
    }

    // A building trap of owner's in building b, as a sapper leaves it.
    private static Unit PlaceRig(World w, byte owner, Unit b)
    {
        var t = w.Spawn(owner, "building_trap", Cell.Of(b.Pos).X, Cell.Of(b.Pos).Y);
        (t.Host, b.Rig) = (b.Id, t.Id);
        return t;
    }

    [Fact]
    public void TheSapperRigsABuildingFromInsideInEightSecondsAndWalksOut()
    {
        var w = Arena.New(48);
        var house = Building(w, "house_small", 20, 20); // door (22, 25)
        var sapper = w.Spawn(1, "engineer_sapper", 22, 30);
        var stock = Stock(w, sapper, "building_trap_kit");
        Assert.Equal(2, sapper.Charges[stock]);
        var ev = new List<DemolitionEvent>();
        Use(w, sapper, house, "building_trap_kit");
        Until(w, () => sapper.Garrison == house.Id, 300, ev);
        Assert.Equal(1, house.Owner); // in, as a garrison
        Assert.Empty(w.All("building_trap"));
        var inside = Until(w, () => sapper.Garrison == 0, 400, ev);
        Assert.Equal(160, inside);
        var trap = Assert.Single(w.All("building_trap"));
        Assert.Equal((house.Id, trap.Id, (byte)1, sapper.Id), (trap.Host, house.Rig, trap.Owner, trap.Source));
        Assert.Equal(1, sapper.Charges[stock]);
        Assert.Equal(OrderKind.Idle, sapper.Order.Kind);
        Assert.True(w.RigKnown(house, 1));
        Assert.False(w.RigKnown(house, 2));
        Assert.False(World.IsVisibleTo(trap, 2));
        var rigged = Assert.Single(ev);
        Assert.Equal((DemolitionEventKind.Rigged, trap.Id, house.Id, sapper.Id), (rigged.Kind, rigged.Charge, rigged.Host, rigged.By));
        w.Run(20);
        Assert.Equal(700, house.HP); // his own coming out sets nothing off
        Assert.Equal(0, house.Owner);
    }

    [Fact]
    public void TheFirstOneInOfAnySideSetsItOff()
    {
        var w = Arena.New(48);
        var house = Building(w, "house_small", 20, 20);
        var trap = PlaceRig(w, 1, house);
        var near = w.Spawn(1, "Rifleman", 19, 22); // half a cell from the west wall
        var far = w.Spawn(1, "Rifleman", 14, 22); // 5.5 cells out
        var enemy = w.Spawn(2, "Rifleman", 22, 30);
        var ev = new List<DemolitionEvent>();
        w.Order(2, CommandKind.Enter, [enemy], house.Id);
        Until(w, () => w.Unit(trap.Id) is null, 300, ev);
        Assert.Equal(700 - 560, house.HP); // 80% of its maximum
        Assert.True(enemy.HP <= enemy.Def.MaxHp - 80, $"inside: {enemy.HP}");
        Assert.InRange(near.Def.MaxHp - near.HP, 25, 39); // 40 at the wall, falling to 0 at 3 cells
        Assert.Equal(far.Def.MaxHp, far.HP);
        var off = Assert.Single(ev, e => e.Kind == DemolitionEventKind.Triggered);
        Assert.Equal((trap.Id, house.Id, enemy.Id, (byte)1), (off.Charge, off.Host, off.By, off.Owner));
        Assert.Equal(0u, house.Rig);

        // His own side too, on a direct order (the warning cursor is the client's); a building with less than 80% left falls.
        var w2 = Arena.New(48);
        var h2 = Building(w2, "house_small", 20, 20);
        PlaceRig(w2, 1, h2);
        h2.HP = 300;
        var own = w2.Spawn(1, "Rifleman", 22, 30);
        w2.Order(1, CommandKind.Enter, [own], h2.Id);
        Until(w2, () => h2.Rig == 0, 300);
        w2.Run(3);
        Assert.True(h2.Rubble || !h2.Alive);
    }

    [Fact]
    public void AVehicleDrivingIntoARiggedShelterSetsItOff()
    {
        var w = Arena.NewSentinel(64);
        var shed = w.Spawn(2, "machine_shed", 24, 24);
        var trap = PlaceRig(w, 2, shed);
        var d0 = World.ShelterDoorCell(shed, 0);
        var hv = w.Spawn(1, "humvee", d0.X, d0.Y + 10);
        World.Board(w.Spawn(1, "Rifleman", d0.X, d0.Y + 10), hv);
        w.Order(1, CommandKind.Enter, [hv], shed.Id);
        Until(w, () => hv.InShelter || w.Unit(trap.Id) is null, 600);
        Assert.Null(w.Unit(trap.Id));
        Assert.Equal(shed.Def.MaxHp - shed.Def.MaxHp * 80 / 100, shed.HP);
        Assert.True(hv.HP <= hv.Def.MaxHp - 80, $"in its bay: {hv.HP}");
    }

    [Fact]
    public void ThirdRigLiftsTheOldestAndHisOwnSideLiftsOneInFourSeconds()
    {
        var w = Arena.New(64);
        var houses = new[] { Building(w, "house_small", 10, 20), Building(w, "house_small", 20, 20), Building(w, "house_small", 30, 20) };
        var sapper = w.Spawn(1, "engineer_sapper", 20, 30);
        var stock = Stock(w, sapper, "building_trap_kit");
        foreach (var h in houses)
        {
            sapper.MutableCharges[stock] = 1;
            Use(w, sapper, h, "building_trap_kit");
            w.Step();
            Until(w, () => sapper.Order.Kind != OrderKind.UseAbility, 800);
        }
        w.Step();
        Assert.Equal(2, w.All("building_trap").Count(t => t.Alive));
        Assert.Equal(0u, houses[0].Rig); // the oldest lifted
        Assert.True(houses[1].Rig != 0 && houses[2].Rig != 0);

        // The same order on his own rigged building lifts it from the door, and the kit has the charge back.
        Assert.Equal(0, sapper.Charges[stock]);
        var ev = new List<DemolitionEvent>();
        Use(w, sapper, houses[2], "building_trap_kit");
        w.Step();
        var ticks = Until(w, () => sapper.Order.Kind != OrderKind.UseAbility, 400, ev);
        Assert.InRange(ticks, 80, 90); // at the door already: 4 s
        Assert.Equal(0u, houses[2].Rig);
        Assert.Equal(1, sapper.Charges[stock]);
        Assert.Equal(0u, sapper.Garrison); // never went in
        Assert.Equal(houses[2].Def.MaxHp, houses[2].HP);
        Assert.Equal(DemolitionEventKind.Lifted, Assert.Single(ev).Kind);
    }

    [Fact]
    public void DetectorsRevealATrapAndAnEnemySapperDisarmsItInSixSeconds()
    {
        var w = Arena.New(64);
        var house = Building(w, "house_small", 20, 20);
        var trap = PlaceRig(w, 1, house);
        var enemy = w.Spawn(2, "engineer_sapper", 22, 28);
        w.Step();
        Assert.False(w.RigKnown(house, 2));
        var sniper = w.Spawn(2, "Marksman", 28, 22); // 3.5 cells from the east wall: within his 5-cell detect
        w.Step();
        Assert.True(w.RigKnown(house, 2));
        Assert.True(World.IsVisibleTo(trap, 2) || (trap.RevealedTo & 4) != 0);
        var ev = new List<DemolitionEvent>();
        Use(w, enemy, house, "building_trap_kit");
        w.Step();
        var ticks = Until(w, () => enemy.Order.Kind != OrderKind.UseAbility, 400, ev);
        Assert.InRange(ticks, 120, 200); // a few steps to the door, then 6 s
        Assert.Null(w.Unit(trap.Id));
        Assert.Equal(0u, house.Rig);
        Assert.Equal(700, house.HP);
        Assert.Equal(0u, enemy.Garrison);
        Assert.Equal((DemolitionEventKind.Disarmed, enemy.Id), (Assert.Single(ev).Kind, ev[0].By));
        Assert.Equal(sniper.Def.MaxHp, sniper.HP);
    }

    [Fact]
    public void AChargeBringsDownASmallBuildingAndBreachesALargeOne()
    {
        var w = Arena.New(80);
        var house = Building(w, "house_small", 20, 20);
        var sapper = w.Spawn(1, "engineer_sapper", 22, 30);
        var stock = Stock(w, sapper, "demo_charge_kit");
        var ev = new List<DemolitionEvent>();
        Use(w, sapper, house, "demo_charge_kit");
        w.Step();
        var t0 = w.Tick;
        Until(w, () => sapper.Order.Kind != OrderKind.UseAbility, 400, ev);
        var charge = Assert.Single(w.All("demo_charge"));
        Assert.Equal((house.Id, sapper.Id, 0), (charge.Host, charge.Source, charge.Fuse)); // no timer: Detonate only
        Assert.Equal(1, sapper.Charges[stock]);
        Assert.Equal(Fixed.FromInt(25).Raw, charge.Pos.Y.Raw); // on the south wall
        Assert.True(ev.Single().Kind == DemolitionEventKind.Placed && !ev[0].Timer);
        Assert.True(w.Tick - t0 >= 120);
        w.Order(1, CommandKind.Move, [sapper], cell: new Cell(22, 40));
        w.Run(80);
        Detonate(w, 1, sapper);
        ev.Clear();
        Until(w, () => w.Unit(charge.Id) is null, 5, ev);
        w.Run(2);
        Assert.True(house.Rubble || !house.Alive);
        Assert.Equal(DemolitionEventKind.Detonated, Assert.Single(ev).Kind);

        // A large building (above 1300 max HP) loses 60% a charge, with a breach where it went: a new door; two bring it down.
        var block = Building(w, "block_panel_5", 40, 50); // 23 x 6
        var doors = World.DoorCount(block);
        var s2 = w.Spawn(1, "engineer_sapper", 30, 52); // west of it
        Use(w, s2, block, "demo_charge_kit");
        w.Step();
        Until(w, () => s2.Order.Kind != OrderKind.UseAbility, 400);
        w.Order(1, CommandKind.Move, [s2], cell: new Cell(24, 52));
        w.Run(60);
        Detonate(w, 1, s2);
        w.Run(2);
        Assert.Equal(2400 - 1440, block.HP);
        Assert.True(block.Breached);
        Assert.Equal(doors + 1, World.DoorCount(block));
        Assert.Equal(39, block.BreachCell.X); // just outside the west wall, where it went
        Assert.InRange(block.BreachCell.Y, 50, 55);
        Assert.Equal(block.BreachCell, World.DoorCell(block, World.NearestDoor(block, new Cell(30, 52).Center())));
        Use(w, s2, block, "demo_charge_kit");
        w.Step();
        Until(w, () => s2.Order.Kind != OrderKind.UseAbility, 400);
        w.Order(1, CommandKind.Move, [s2], cell: new Cell(24, 52));
        w.Run(60);
        Detonate(w, 1, s2);
        w.Run(3);
        Assert.True(block.Rubble || !block.Alive);
    }

    [Fact]
    public void AChargeDropsABridgeSpanUntilRepaired()
    {
        var w = BridgeTests.River(64, sites: [new BridgeSite("concrete", new Cell(30, 24), Facing.South)]);
        var bridge = Assert.Single(w.Bridges);
        var sapper = w.Spawn(1, "engineer_sapper", 32, 20);
        Use(w, sapper, bridge, "demo_charge_kit", timer: true);
        w.Step();
        Until(w, () => sapper.Order.Kind != OrderKind.UseAbility, 400);
        var charge = Assert.Single(w.All("demo_charge"));
        Assert.Equal(200, charge.Fuse); // the 10 s timer
        w.Order(1, CommandKind.Move, [sapper], cell: new Cell(32, 8));
        Until(w, () => w.Unit(charge.Id) is null, 205);
        w.Run(2);
        Assert.True(bridge.Bridge!.Down);
    }

    [Fact]
    public void AChargeOnAVehicleStandingStillRidesWithItAndHitsIt()
    {
        var w = Arena.New(64);
        var (truck, _) = w.SpawnCrewed(2, "cargo_truck", 30, 30); // unarmed
        var sapper = w.Spawn(1, "engineer_sapper", 30, 34);
        // A vehicle on the move takes none.
        w.Order(2, CommandKind.Move, [truck], cell: new Cell(30, 50));
        w.Run(10);
        Use(w, sapper, truck, "demo_charge_kit");
        w.Step();
        Assert.Equal(OrderKind.Idle, sapper.Order.Kind);
        w.Order(2, CommandKind.Stop, [truck]);
        w.Run(40);
        Assert.Equal(default, truck.Vel);
        Use(w, sapper, truck, "demo_charge_kit");
        w.Step();
        Assert.Equal(OrderKind.UseAbility, sapper.Order.Kind);
        Until(w, () => sapper.Order.Kind != OrderKind.UseAbility, 400);
        var charge = Assert.Single(w.All("demo_charge"));
        Assert.Equal(truck.Id, charge.Host);
        w.Order(1, CommandKind.Move, [sapper], cell: new Cell(10, 10));
        w.Order(2, CommandKind.Move, [truck], cell: new Cell(50, 30));
        w.Run(60);
        Assert.True(World.Within(truck.Pos, charge.Pos, Fixed.One), $"{truck.Pos} {charge.Pos}"); // it rides on it (placed where it stands at the demolition pass)
        var by = w.Spawn(2, "Rifleman", Cell.Of(truck.Pos).X, Cell.Of(truck.Pos).Y + 1);
        var hp = truck.HP;
        Detonate(w, 1, charge); // its own sapper's radio, from the charge
        w.Step();
        Assert.True(truck.HP <= hp - 400 || !truck.Alive, $"{hp} -> {truck.HP}");
        Assert.InRange(by.Def.MaxHp - by.HP, 1, 60); // the blast: 60 at the charge falling to 0 at 3 cells
    }

    [Fact]
    public void DetonateNeedsTheSapperAliveWithinThirtyCells()
    {
        var w = Arena.New(96);
        var house = Building(w, "house_small", 20, 20);
        var sapper = w.Spawn(1, "engineer_sapper", 22, 30);
        Use(w, sapper, house, "demo_charge_kit");
        w.Step();
        Until(w, () => sapper.Order.Kind != OrderKind.UseAbility, 400);
        var charge = Assert.Single(w.All("demo_charge"));
        w.Order(1, CommandKind.Move, [sapper], cell: new Cell(22, 70));
        Until(w, () => !World.Within(sapper.Pos, charge.Pos, Fixed.FromInt(32)), 800);
        Detonate(w, 1, sapper);
        w.Run(3);
        Assert.NotNull(w.Unit(charge.Id)); // out of radio range
        sapper.HP = 0;
        w.Run(2);
        Detonate(w, 1, charge);
        w.Run(600);
        Assert.NotNull(w.Unit(charge.Id)); // his charges stay; no remote detonation
        Assert.Equal(700, house.HP);
    }

    [Fact]
    public void AnEnemyWithinThreeCellsSeesAChargeAndHisSapperDisarmsIt()
    {
        var w = Arena.New(64);
        var house = Building(w, "house_small", 20, 20);
        var sapper = w.Spawn(1, "engineer_sapper", 22, 30);
        Use(w, sapper, house, "demo_charge_kit");
        w.Step();
        Until(w, () => sapper.Order.Kind != OrderKind.UseAbility, 400);
        var charge = Assert.Single(w.All("demo_charge"));
        w.Order(1, CommandKind.Move, [sapper], cell: new Cell(22, 50));
        var enemy = w.Spawn(2, "engineer_sapper", 22, 31); // 6 cells off
        w.Run(5);
        Assert.Equal(0u, charge.RevealedTo & 4);
        w.Order(2, CommandKind.Move, [enemy], cell: new Cell(22, 27));
        Until(w, () => (charge.RevealedTo & 4) != 0, 200);
        Assert.True(World.Within(enemy.Pos, charge.Pos, Fixed.FromInt(3)));
        var ev = new List<DemolitionEvent>();
        Use(w, enemy, charge, "demo_charge_kit");
        w.Step();
        var ticks = Until(w, () => enemy.Order.Kind != OrderKind.UseAbility, 400, ev);
        Assert.InRange(ticks, 120, 160);
        Assert.Null(w.Unit(charge.Id));
        Assert.Equal(700, house.HP);
        Assert.Equal(DemolitionEventKind.Disarmed, Assert.Single(ev).Kind);
    }

    [Fact]
    public void ADepotRefillsBothKitsAtTheirPrices()
    {
        var w = Arena.New(64, content: TestContent.ClassicSupply);
        var depot = w.Spawn(1, "supply_depot", 2, 2);
        var sapper = w.Spawn(1, "engineer_sapper", 10, 5);
        int trap = Stock(w, sapper, "building_trap_kit"), demo = Stock(w, sapper, "demo_charge_kit");
        w.Step();
        var before = depot.Stock;
        sapper.MutableCharges[trap] = 0;
        sapper.MutableCharges[demo] = 0;
        w.Run(500);
        Assert.Equal((2, 2), (sapper.Charges[trap], sapper.Charges[demo]));
        Assert.Equal(before - 2 * 80 - 2 * 100, depot.Stock);
    }

    [Fact]
    public void TheSameOrdersGiveTheSameHashes()
    {
        ulong[] Run()
        {
            var w = Arena.New(64);
            var house = Building(w, "house_small", 20, 20);
            var block = Building(w, "block_panel_5", 30, 40);
            var sapper = w.Spawn(1, "engineer_sapper", 22, 30);
            var enemy = w.Spawn(2, "Rifleman", 40, 30);
            var hashes = new ulong[900];
            for (var i = 0; i < hashes.Length; i++)
            {
                switch (i)
                {
                    case 1: Use(w, sapper, house, "building_trap_kit"); break;
                    case 300: Use(w, sapper, block, "demo_charge_kit", timer: true); break;
                    case 600: w.Order(2, CommandKind.Enter, [enemy], house.Id); break;
                }
                w.Step();
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        var a = Run();
        Assert.Equal(a, Run());
        Assert.NotEqual(a[100], a[299]);
    }
}
