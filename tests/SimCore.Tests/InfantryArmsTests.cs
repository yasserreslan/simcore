using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// INF-E1a (docs/design.md "Infantry weapons and ammo"): the Resistance's res_* arms, the hashed hit roll, magazines and carried
// ammo, the sidearm switch, resupply at supply points paid from their stock, and the Resupply order (CommandKind.Rearm).
public class InfantryArmsTests
{
    private const int Rifle = 0, Pistol = 1;

    // A target that never fights back and never dies: an enemy rifleman with no ammo, healed every tick by Hold.
    private static Unit Dummy(World w, int x, int y)
    {
        var t = w.Spawn(2, "Rifleman", x, y, Facing.West);
        Array.Fill(t.MutableCharges, 0);
        Array.Fill(t.Arms!.MutableMag, 0);
        return t;
    }

    private static void Hold(World w, Unit t, int ticks, Action? each = null)
    {
        for (var i = 0; i < ticks; i++)
        {
            t.HP = t.Def.MaxHp;
            w.Step();
            each?.Invoke();
        }
    }

    private static int Left(Unit u, int weapon) => u.Charges[u.Def.Weapons[weapon].Stock];

    private static void Empty(Unit u, int weapon)
    {
        u.MutableCharges[u.Def.Weapons[weapon].Stock] = 0;
        u.Arms!.MutableMag[weapon] = 0;
    }

    [Fact]
    public void TheResistanceFieldsTheResArmsAndTheSentinelKeepsItsOwn()
    {
        var w = Arena.New(32);
        string[] Ids(World x, byte p, string name) => [.. x.Player(p)!.Setup.Find(x.Content, name)!.Components.Select(c => c.Id)];
        Assert.Equal(["res_rifle", "res_pistol", "ap_mine_kit", "at_mine_kit"], Ids(w, 1, "Rifleman"));
        Assert.Equal(["res_lmg", "res_pistol", "ap_mine_kit", "at_mine_kit"], Ids(w, 1, "Gunner"));
        Assert.Equal(["at_missile", "res_carbine", "booby_trap"], Ids(w, 1, "Tank hunter"));
        Assert.Equal(["trap_kit", "res_carbine", "repair_kit", "res_pistol", "drone_station_kit", "kaman_station_kit"], Ids(w, 1, "Sapper"));
        Assert.Equal(["repair_kit", "res_carbine", "res_pistol", "drone_station_kit", "kaman_station_kit"], Ids(w, 1, "Mechanic")); // once
        Assert.Equal(["res_sniper", "res_smg", "scout_station_kit"], Ids(w, 1, "Skirmisher"));
        Assert.Equal(["kornet_atgm", "res_carbine"], Ids(w, 1, "kornet"));
        Assert.Equal(["mortar_tube", "res_carbine"], Ids(w, 1, "mortar"));
        Assert.Equal(["igla", "res_carbine"], Ids(w, 1, "manpads"));
        var rifleman = w.Spawn(1, "Rifleman", 5, 5);
        Assert.Equal((1, WeaponMode.Auto, 200, 10), (rifleman.Def.Sidearm, rifleman.WeaponMode, Left(rifleman, Rifle), rifleman.Arms!.Mag[Rifle]));
        var rpg = w.Spawn(1, "Tank hunter", 7, 5);
        Assert.Equal((1, WeaponMode.Auto, 6, 1), (rpg.Def.Sidearm, rpg.WeaponMode, Left(rpg, 0), rpg.Arms!.Mag[0])); // the carbine switches by the rule

        var s = Arena.NewSentinel(32);
        Assert.Equal(["rifle", "pistol", "ap_mine_kit", "at_mine_kit"], Ids(s, 1, "Rifleman"));
        Assert.Equal(["at_missile", "short_rifle"], Ids(s, 1, "Tank hunter"));
        Assert.Equal(["igla"], Ids(s, 1, "manpads"));
        var sentinel = s.Spawn(1, "Rifleman", 5, 5);
        Assert.Equal((-1, false, -1), (sentinel.Def.Sidearm, sentinel.Def.HasArms, sentinel.Def.Weapons[0].Stock)); // today's rifle: unlimited
        Assert.Equal(WeaponMode.PrimaryOnly, s.Spawn(1, "Tank hunter", 7, 5).WeaponMode);

        // The Resistance no longer allows today's ids in a preset.
        var bad = Assert.Throws<MatchSetupException>(() => Arena.New(32, edit: m => m with
        {
            Players = [.. m.Players.Select(p => p.Id == 1 ? p with { Presets = [.. p.Presets.Select(x => x.Name == "Rifleman" ? x with { Components = ["rifle", "res_pistol"] } : x)] } : p)],
        }));
        Assert.Contains("does not allow component \"rifle\"", bad.Message);
    }

    [Fact]
    public void TheHitRollIsHashedAndFollowsTheAccuracyAndTheRange()
    {
        static (List<bool> Rolls, int Near, int Far) Rolls(ulong seed)
        {
            var w = Arena.New(32, seed);
            w.SureHits = false;
            var u = w.Spawn(1, "Rifleman", 5, 10);
            var near = w.Spawn(1, "Rifleman", 7, 10); // 2 cells: within 60% of the 5-cell range, 74%
            var far = w.Spawn(1, "Rifleman", 10, 10); // 5 cells: full range, 60% of it
            var rifle = u.Def.Weapons[Rifle];
            var rolls = new List<bool>();
            int n = 0, f = 0;
            for (var i = 0; i < 2000; i++)
            {
                w.Step();
                var a = w.HitRoll(u, near, rifle);
                var b = w.HitRoll(u, far, rifle);
                Assert.Equal(a, w.HitRoll(u, near, rifle)); // the same tick and IDs: the same roll
                rolls.Add(a);
                n += a ? 1 : 0;
                f += b ? 1 : 0;
            }
            return (rolls, n, f);
        }
        var (r1, near1, far1) = Rolls(7);
        var (r2, _, _) = Rolls(7);
        Assert.Equal(r1, r2); // reproducible
        Assert.NotEqual(r1, Rolls(8).Rolls); // another seed, other rolls
        Assert.InRange(near1, 1480 - 90, 1480 + 90); // 74% of 2000
        Assert.InRange(far1, 888 - 90, 888 + 90); // 44.4%

        var w2 = Arena.New(32);
        w2.SureHits = false;
        var shooter = w2.Spawn(1, "Rifleman", 5, 10);
        var depot = w2.Spawn(2, "supply_depot", 10, 10);
        Assert.True(w2.HitRoll(shooter, depot, shooter.Def.Weapons[Rifle])); // a structure is never missed
    }

    [Fact]
    public void TheRifleFiresItsMagazineReloadsAndMatchesTodaysRifleOver30Seconds()
    {
        var w = Arena.New(32);
        w.SureHits = false;
        var u = w.Spawn(1, "Rifleman", 5, 10, Facing.East);
        var t = Dummy(w, 9, 10); // 4 cells: in range, beyond the 3 cells that would make him draw the pistol while reloading
        var a = u.Arms!;
        var shots = new List<uint>();
        var reloadSeen = false;
        var last = Left(u, Rifle);
        Hold(w, t, 600, () =>
        {
            if (Left(u, Rifle) < last)
            {
                shots.Add(w.Tick);
                last = Left(u, Rifle);
            }
            if (a.Reloading == Rifle)
            {
                reloadSeen = true;
                Assert.Equal(0, a.Mag[Rifle]);
            }
        });
        Assert.True(reloadSeen);
        Assert.True(shots.Count >= 10);
        for (var i = 1; i < 10; i++)
        {
            Assert.Equal(16u, shots[i] - shots[i - 1]); // 0.8 s between shots within a magazine
        }
        Assert.Equal(50u, shots[10] - shots[9]); // 2.5 s to replace the magazine (alongside the shot's 0.8 s)
        Assert.Equal(Rifle, a.InHand); // no enemy close: he keeps his rifle while reloading
        // Fast and deadly (decision 1108): 30 s of fire x accuracy within 10% of today's rifle (12 every 15 ticks: 480).
        var span = shots.Count(s => s - shots[0] < 600);
        var dmg = u.Def.Weapons[Rifle].Damage;
        var acc = u.Def.Weapons[Rifle].Effect.Accuracy;
        Assert.Equal(32, span); // magazines start at 0, 194, 388 and 582 (10 + 10 + 10 + 2 shots)
        Assert.InRange(span * dmg * acc / 100, 432, 528);
        Assert.Equal(200 - shots.Count, Left(u, Rifle));
    }

    [Fact]
    public void AnEmptyMainArmSwitchesToThePistolInItsDrawTimeAndBackOnceResupplied()
    {
        var w = Arena.New(32);
        var u = w.Spawn(1, "Rifleman", 5, 10, Facing.East);
        var t = Dummy(w, 9, 10);
        Empty(u, Rifle);
        Hold(w, t, 1);
        Assert.Equal((Rifle, Pistol, 12), (u.Arms!.InHand, u.Arms.SwitchTo, u.Arms.SwitchLeft)); // 0.6 s to the pistol
        var pistol = Left(u, Pistol);
        Hold(w, t, 11);
        Assert.Equal((Rifle, Pistol, 1), (u.Arms.InHand, u.Arms.SwitchTo, u.Arms.SwitchLeft));
        Hold(w, t, 1);
        Assert.Equal((Pistol, -1), (u.Arms.InHand, u.Arms.SwitchTo));
        Assert.Equal(pistol, Left(u, Pistol)); // no shot while switching
        Hold(w, t, 40); // (the dummy at 4 cells is beyond the pistol's 3)
        Assert.True(World.LowAmmo(u));
        Assert.Equal(Pistol, u.Arms.InHand); // no rifle ammo: he keeps the pistol

        u.MutableCharges[u.Def.Weapons[Rifle].Stock] = 200; // rifle ammo again, no enemy within 3 cells for 2 s: back to it
        Hold(w, t, 1);
        Assert.Equal(Rifle, u.Arms.SwitchTo);
        Hold(w, t, 12);
        Assert.Equal((Rifle, -1), (u.Arms.InHand, u.Arms.SwitchTo));
        Assert.False(World.LowAmmo(u));
    }

    [Fact]
    public void TheRpgDrawsHisCarbineWhenInfantryComesWithin3CellsUnlessHeldToTheLauncher()
    {
        var w = Arena.New(32);
        var u = w.Spawn(1, "Tank hunter", 5, 10, Facing.East);
        var t = Dummy(w, 8, 10); // 3 cells
        Hold(w, t, 1);
        Assert.Equal((0, 1, 20), (u.Arms!.InHand, u.Arms.SwitchTo, u.Arms.SwitchLeft)); // 1 s to the carbine
        Hold(w, t, 20);
        Assert.Equal(1, u.Arms.InHand);
        var carbine = Left(u, 1);
        Hold(w, t, 60);
        Assert.True(Left(u, 1) < carbine);
        Assert.Equal(6, Left(u, 0)); // no rocket at infantry this close

        var held = w.Spawn(1, "Tank hunter", 5, 12, Facing.East);
        held.WeaponMode = WeaponMode.PrimaryOnly; // the player's override keeps the launcher
        Hold(w, t, 30);
        Assert.Equal((0, -1), (held.Arms!.InHand, held.Arms.SwitchTo));

        t.Pos = Cell.Of(new Vec(Fixed.FromInt(20), Fixed.FromInt(10))).Center(); // the enemy goes beyond 4 cells: back after 2 s, 1 s to switch
        Hold(w, t, 40 + 20 + 2);
        Assert.Equal((0, -1), (u.Arms.InHand, u.Arms.SwitchTo));
    }

    [Fact]
    public void ADepotRefillsAMagazineEvery10TicksPaidFromItsStock()
    {
        var w = Arena.New(48);
        var depot = w.Spawn(1, "supply_depot", 20, 20);
        var dc = Cell.Of(depot.Pos);
        var u = w.Spawn(1, "Rifleman", dc.X, dc.Y + 4); // a cell off the depot's 6 x 6 footprint
        var rpg = w.Spawn(1, "Tank hunter", dc.X + 2, dc.Y + 4);
        Empty(u, Rifle);
        Empty(rpg, 0);
        var stock = depot.Stock;
        w.Run(20 * 10 + 10);
        Assert.Equal(200, Left(u, Rifle)); // 20 magazines, one every 10 ticks
        Assert.Equal(6, Left(rpg, 0)); // 6 rockets
        Assert.Equal(stock - 20 - 6 * 15, depot.Stock); // a full rifle load 20, a rocket 15

        Assert.True(Within2(depot, u));
        var far = w.Spawn(1, "Rifleman", dc.X, dc.Y + 7); // 4 cells off it
        far.MutableCharges[far.Def.Weapons[Rifle].Stock] = 50;
        w.Run(30);
        Assert.Equal(50, Left(far, Rifle));
    }

    [Fact]
    public void ACrateRefillsUntilItRunsDry()
    {
        var w = Arena.New(48);
        var crate = w.Spawn(1, World.CrateArchetype, 20, 20);
        crate.Stock = 3;
        var u = w.Spawn(1, "Rifleman", 21, 21);
        Empty(u, Rifle);
        w.Run(100);
        Assert.Equal((30, 0), (Left(u, Rifle), crate.Stock)); // 3 magazines at 1 supply each, then it stops
    }

    [Fact]
    public void TheResupplyOrderWalksToThePointAndAnEmptySoldierGoesOnHisOwn()
    {
        var w = Arena.New(64);
        var depot = w.Spawn(1, "supply_depot", 40, 20);
        var ordered = w.Spawn(1, "Rifleman", 10, 20);
        ordered.MutableCharges[ordered.Def.Weapons[Rifle].Stock] = 40;
        w.Order(1, CommandKind.Rearm, [ordered], target: depot.Id);
        w.Run(2);
        Assert.Equal(OrderKind.Move, ordered.Order.Kind);
        var empty = w.Spawn(1, "Rifleman", 40, 40); // 20 cells off, nothing left, no order
        Empty(empty, Rifle);
        Empty(empty, Pistol);
        w.Run(60 * 20);
        Assert.Equal(200, Left(ordered, Rifle));
        Assert.True(Within2(depot, ordered));
        Assert.Equal((200, 48), (Left(empty, Rifle), Left(empty, Pistol)));
        Assert.True(Within2(depot, empty));

        var stranded = w.Spawn(1, "Rifleman", 5, 60); // more than 40 cells from any supply point: he stays
        Empty(stranded, Rifle);
        Empty(stranded, Pistol);
        var at = stranded.Pos;
        w.Run(100);
        Assert.Equal((at, OrderKind.Idle), (stranded.Pos, stranded.Order.Kind));
    }

    [Fact]
    public void TheRepairOrderWalksToTheVehicleRepairsItStacksAndStops()
    {
        var w = Arena.New(48);
        var car = w.Spawn(1, "technical", 20, 10);
        car.HP = car.Def.MaxHp - 60;
        var a = w.Spawn(1, "Sapper", 8, 10);
        var b = w.Spawn(1, "Drone pilot", 8, 12);
        Assert.Single(a.Def.Repairs); // every engineer carries the repair kit
        w.Order(1, CommandKind.Repair, [a, b], target: car.Id);
        var events = new List<RepairEvent>();
        int? both = null;
        for (var i = 0; i < 600 && car.HP < car.Def.MaxHp; i++)
        {
            w.Step();
            events.AddRange(w.RepairEvents);
            if (both is null && a.Repairing && b.Repairing)
            {
                both = car.HP;
            }
        }
        Assert.Equal(car.Def.MaxHp, car.HP);
        Assert.NotNull(both);
        Assert.True(Within(a, car) && Within(b, car));
        Assert.Equal(2, events.Count(e => e.Kind == RepairEventKind.Started && e.Target == car.Id));
        w.Step();
        events.AddRange(w.RepairEvents);
        Assert.Equal(2, events.Count(e => e.Kind == RepairEventKind.Stopped && e.Target == car.Id)); // full: both stop
        Assert.Equal((0u, false), (a.RepairTarget, a.Repairing));

        // Ordered away, he stops at once; idle, he repairs a damaged vehicle within 3 cells by himself, not one farther off.
        car.HP -= 30;
        w.Order(1, CommandKind.Repair, [a], target: car.Id);
        w.Run(10);
        Assert.True(a.Repairing);
        w.Order(1, CommandKind.Move, [a], cell: new Cell(8, 30));
        w.Run(2);
        Assert.Equal((0u, false), (a.RepairTarget, a.Repairing));
        var far = w.Spawn(1, "technical", 8, 36);
        far.HP -= 30;
        w.Run(30 * 20);
        Assert.True(b.Repairing || car.HP == car.Def.MaxHp); // b stood idle beside the car: he took it on by himself
        Assert.Equal(car.Def.MaxHp, car.HP);
        Assert.True(far.HP < far.Def.MaxHp); // 6 cells from a: out of his 3
    }

    private static bool Within(Unit r, Unit t) => (t.Pos - r.Pos).LenSq() <= 225L * Fixed.OneRaw * Fixed.OneRaw / 100;

    private static bool Within2(Unit s, Unit u) => (World.AimPoint(u.Pos, s) - u.Pos).LenSq() <= 4L * Fixed.OneRaw * Fixed.OneRaw;
}
