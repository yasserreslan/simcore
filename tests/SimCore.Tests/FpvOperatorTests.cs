using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// INF-EE2 (owner decision 32, 2026-11-20): the drone engineer flies his FPV himself. From the launch until the drone is gone he
// is the operator: he refuses orders and doesn't move, fire or repair; the FPV takes its side's orders directly; Release crashes
// it; if he dies the link is lost. The Kaman is unchanged (docs/design.md, "Flying the FPV").
public class FpvOperatorTests
{
    private const string Kit = "drone_kit";

    private static Unit Launch(World w, Unit pilot, Cell at, uint target = 0)
    {
        w.Issue(new Command
        {
            Tick = w.Tick, Player = pilot.Owner, Kind = CommandKind.UseAbility, Units = [pilot.Id], Cell = at, Target = target,
            Ability = Kit, On = target == 0,
        });
        for (var i = 0; i < 200 && w.All("fpv_drone").Count == 0; i++)
        {
            w.Step();
        }
        var fpv = Assert.Single(w.All("fpv_drone"));
        Assert.True(pilot.Operating);
        Assert.Equal((fpv.Id, pilot.Id), (pilot.Fpv, fpv.Operator)); // the client's operator flag and his FPV's ID
        Assert.False(pilot.CanAct);
        return fpv;
    }

    // Runs until fpv is gone, checking every tick that the lock holds exactly while it flies and that he stays where he is.
    private static void RunUntilGone(World w, Unit pilot, Unit fpv, int max)
    {
        var at = pilot.Pos;
        for (var i = 0; i < max && w.Unit(fpv.Id) is not null; i++)
        {
            w.Step();
            Assert.Equal(w.Unit(fpv.Id) is not null, pilot.Operating);
            Assert.Equal(at, pilot.Pos);
            if (i % 40 == 0)
            {
                Assert.Equal(w.HashReference(), w.Hash());
            }
        }
        Assert.Null(w.Unit(fpv.Id));
        Assert.Equal(0u, pilot.Fpv);
        Assert.True(pilot.CanAct);
    }

    private static void AssertFreeAgain(World w, Unit pilot)
    {
        var at = pilot.Pos;
        w.Order(1, CommandKind.Move, [pilot], cell: new Cell(Cell.Of(at).X, Cell.Of(at).Y + 6));
        w.Step();
        Assert.Empty(w.OrderRefusals);
        w.Run(40);
        Assert.NotEqual(at, pilot.Pos); // he takes orders again
    }

    [Fact]
    public void TheLockHoldsFromTheLaunchToTheHit()
    {
        var w = Arena.New(48);
        var pilot = w.Spawn(1, "engineer_drone", 5, 30);
        var truck = w.Spawn(2, "cargo_truck", 15, 30); // unarmed: it can't shoot him
        var fpv = Launch(w, pilot, default, truck.Id);
        Assert.Equal((OrderKind.Attack, truck.Id), (fpv.Order.Kind, fpv.Order.Target)); // attack mode, as today
        RunUntilGone(w, pilot, fpv, 200);
        Assert.True(truck.HP < truck.Def.MaxHp); // it hit
        AssertFreeAgain(w, pilot);
    }

    [Theory]
    [InlineData("battery")]
    [InlineData("shot down")]
    [InlineData("release")]
    [InlineData("release by the operator")]
    public void TheLockEnds(string how)
    {
        var w = Arena.New(48);
        var pilot = w.Spawn(1, "engineer_drone", 5, 20);
        if (how == "shot down")
        {
            w.Spawn(2, "Drone hunter", 18, 20, Facing.West);
        }
        var fpv = Launch(w, pilot, new Cell(15, 20));
        w.Run(60);
        if (how.StartsWith("release", StringComparison.Ordinal))
        {
            w.Order(1, CommandKind.ReturnToBase, [how == "release" ? fpv : pilot]);
            w.Step();
            Assert.Empty(w.OrderRefusals); // Release is the one order the operator takes
            Assert.Null(w.Unit(fpv.Id)); // it crashed at once (no way home)
            Assert.Equal(KillCause.Crash, fpv.KilledBy);
        }
        RunUntilGone(w, pilot, fpv, 3700);
        Assert.True(pilot.Alive);
        if (how == "shot down")
        {
            Assert.NotEqual(KillCause.Crash, fpv.KilledBy);
        }
        AssertFreeAgain(w, pilot);
    }

    [Fact]
    public void TheOperatorRefusesOrders()
    {
        var w = Arena.New(48);
        var pilot = w.Spawn(1, "engineer_drone", 10, 20);
        var car = w.Spawn(1, "technical", 12, 20);
        var enemy = w.Spawn(2, "cargo_truck", 14, 24);
        var fpv = Launch(w, pilot, new Cell(20, 20));
        var at = pilot.Pos;
        w.Order(1, CommandKind.Move, [pilot], cell: new Cell(10, 30));
        w.Order(1, CommandKind.Stop, [pilot]);
        w.Order(1, CommandKind.Attack, [pilot], target: enemy.Id);
        w.Order(1, CommandKind.Repair, [pilot], target: car.Id);
        w.Order(1, CommandKind.UseAbility, [pilot], target: enemy.Id, ability: Kit); // his second FPV
        w.Step();
        Assert.Equal(5, w.OrderRefusals.Count);
        Assert.All(w.OrderRefusals, r => Assert.Equal((pilot.Id, (byte)1, OrderRefusalReason.Operating, fpv.Id), (r.Aircraft, r.Player, r.Reason, r.Target)));
        w.Run(40);
        Assert.Equal(at, pilot.Pos);
        Assert.Single(w.All("fpv_drone"));
        Assert.Equal(OrderKind.Idle, pilot.Order.Kind);
        Assert.Equal(1, pilot.Charges[pilot.Def.Ability(w.Content.Component(Kit)!.Index)!.Stock]); // one spent, one kept
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeDoesntFireOrRepairWhileHeFlies(bool repair)
    {
        var w = Arena.New(48);
        var pilot = w.Spawn(1, "engineer_drone", 10, 20);
        pilot.Stance = Stance.HoldGround; // he would fire at what comes in reach
        Unit victim;
        if (repair)
        {
            victim = w.Spawn(1, "technical", 12, 20); // within 3 cells: an idle engineer repairs it by himself
            victim.HP = victim.Def.MaxHp / 2;
        }
        else
        {
            victim = w.Spawn(2, "Rifleman", 10, 23); // in his carbine's reach (4 cells)
            victim.Stance = Stance.HoldFire; // he never shoots back
        }
        var hp = victim.HP;
        var fpv = Launch(w, pilot, new Cell(20, 20));
        w.Run(200);
        Assert.True(pilot.Operating);
        Assert.Equal(hp, victim.HP);
        w.Order(1, CommandKind.ReturnToBase, [fpv]);
        w.Run(200);
        Assert.False(pilot.Operating);
        Assert.True(repair ? victim.HP > hp : victim.HP < hp); // free again: he repairs, he fires
    }

    [Fact]
    public void TheFpvTakesDirectOrders()
    {
        var w = Arena.New(64);
        var pilot = w.Spawn(1, "engineer_drone", 10, 30);
        var truck = w.Spawn(2, "cargo_truck", 29, 32);
        var far = w.Spawn(2, "cargo_truck", 55, 30);
        var fpv = Launch(w, pilot, new Cell(20, 30));
        var radius = fpv.Def.Archetype.Surveil!.Radius;

        // Move: the circle moves to the point.
        var point = new Cell(28, 30);
        w.Order(1, CommandKind.Move, [fpv], cell: point);
        w.Step();
        Assert.Equal((true, point.Center(), OrderKind.Idle), (fpv.Surveil, fpv.OrbitAt, fpv.Order.Kind));
        w.Run(150);
        Assert.InRange((fpv.Pos - point.Center()).Len().Raw, radius.Raw - (Fixed.OneRaw / 2), radius.Raw + (Fixed.OneRaw / 2));
        Assert.Equal(w.HashReference(), w.Hash());

        // Attack: not on an enemy it doesn't see (refused), then on one it sees: it dives on it.
        w.Order(1, CommandKind.Attack, [fpv], target: far.Id);
        w.Step();
        var r = Assert.Single(w.OrderRefusals);
        Assert.Equal((fpv.Id, OrderRefusalReason.NotVisible, far.Id), (r.Aircraft, r.Reason, r.Target));
        Assert.True(fpv.Surveil);
        w.Order(1, CommandKind.Attack, [fpv], target: truck.Id);
        w.Step();
        Assert.Equal((false, OrderKind.Attack, truck.Id), (fpv.Surveil, fpv.Order.Kind, fpv.Order.Target));
        RunUntilGone(w, pilot, fpv, 200);
        Assert.True(truck.HP < truck.Def.MaxHp);
    }

    [Fact]
    public void AnAttackingFpvBreaksOffOnAMoveOrder()
    {
        var w = Arena.New(48);
        var pilot = w.Spawn(1, "engineer_drone", 5, 30);
        var truck = w.Spawn(2, "cargo_truck", 19, 30);
        var fpv = Launch(w, pilot, default, truck.Id);
        var left = fpv.Lifetime;
        w.Order(1, CommandKind.Move, [fpv], cell: new Cell(8, 20));
        w.Step();
        Assert.Equal((true, OrderKind.Idle), (fpv.Surveil, fpv.Order.Kind)); // it watches the point
        Assert.Equal(fpv.Def.Archetype.Surveil!.Vision, w.SightRange(fpv));
        Assert.Equal(left - 1, fpv.Lifetime); // its battery as it was (an attack launch's)
        RunUntilGone(w, pilot, fpv, 300);
        Assert.Equal(truck.Def.MaxHp, truck.HP); // it never went off on the truck
    }

    [Fact]
    public void TheLinkIsLostWhenTheOperatorDies()
    {
        var w = Arena.New(48);
        var pilot = w.Spawn(1, "engineer_drone", 5, 20);
        var fpv = Launch(w, pilot, new Cell(15, 20));
        w.Run(30);
        pilot.HP = 0;
        w.Step();
        Assert.Null(w.Unit(fpv.Id)); // it crashed at once
        Assert.Equal(KillCause.Crash, fpv.KilledBy);
        Assert.Equal(w.HashReference(), w.Hash());
    }

    [Fact]
    public void TheKamanDoesntLockTheEngineer()
    {
        var w = Arena.New(320, content: TestContent.ClassicSupply);
        var engineer = w.Spawn(1, "engineer_drone", 10, 150);
        w.Order(1, CommandKind.UseAbility, [engineer], cell: new Cell(11, 150), ability: "kaman_station_kit");
        w.Run(65);
        var station = Assert.Single(w.All("kaman_station"));
        var tech = w.Spawn(2, "technical", 150, 150);
        w.Spawn(1, "Marksman", 140, 150);
        w.Run(20);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Attack, Units = [station.Id], Target = tech.Id });
        w.Run(5);
        var kaman = Assert.Single(w.All("kaman_drone"));
        Assert.Equal((false, 0u, 0u), (engineer.Operating, engineer.Fpv, kaman.Operator)); // its station flies it
        AssertFreeAgain(w, engineer);
    }
}
