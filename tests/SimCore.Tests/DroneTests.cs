using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

public class DroneTests
{
    private const string Kit = "drone_kit";

    // Runs until the world has no drones left, or the limit.
    private static int RunUntilNoDrones(World w, int limit, Action? eachTick = null)
    {
        for (var i = 0; i < limit; i++)
        {
            w.Step();
            eachTick?.Invoke();
            if (w.All("fpv_drone").Count == 0)
            {
                return i + 1;
            }
        }
        return limit;
    }

    [Fact]
    public void DroneFliesToItsTargetAndExplodes()
    {
        var w = Arena.New(32);
        var pilot = w.Spawn(1, "Drone pilot", 5, 16);
        var target = w.Spawn(2, "technical", 15, 16);
        w.Order(1, CommandKind.UseAbility, [pilot], target.Id, ability: Kit);
        w.Step();

        var drone = Assert.Single(w.All("fpv_drone"));
        Assert.Equal(1, drone.Owner);
        Assert.Equal(pilot.Id, drone.Source);
        Assert.Equal(Model.Layers.Air, drone.Def.Layer);
        Assert.Equal([1, 1, 1], pilot.Charges); // the drone kit, the drone station kit and the Kaman station kit
        Assert.Equal(new Cell(5, 16).Center(), pilot.Pos); // in range: launched without walking

        Assert.True(RunUntilNoDrones(w, 100) < 100);
        Assert.InRange(target.HP, target.Def.MaxHp - 70, target.Def.MaxHp - 50);
    }

    [Fact]
    public void DroneChasesAMovingTarget()
    {
        var w = Arena.New(48);
        var pilot = w.Spawn(1, "Drone pilot", 5, 30);
        var (tech, _) = w.SpawnCrewed(2, "cargo_truck", 15, 30, Facing.North); // unarmed, so it can't shoot back
        w.Order(1, CommandKind.UseAbility, [pilot], tech.Id, ability: Kit);
        w.Order(2, CommandKind.Move, [tech], cell: new Cell(15, 2));
        Assert.True(RunUntilNoDrones(w, 200) < 200);
        Assert.True(tech.HP < tech.Def.MaxHp);
        Assert.True(Cell.Of(tech.Pos).Y <= 27, $"the truck should have been caught on the move, at {tech.Pos}");
    }

    [Fact]
    public void DroneSentToACellExplodesThere()
    {
        var w = Arena.New(32);
        var pilot = w.Spawn(1, "Drone pilot", 5, 16);
        var victim = w.Spawn(2, "Rifleman", 15, 10);
        w.Order(1, CommandKind.UseAbility, [pilot], cell: new Cell(15, 10), ability: Kit);
        Assert.True(RunUntilNoDrones(w, 100) < 100);
        Assert.True(victim.HP < victim.Def.MaxHp - 40);
    }

    [Fact]
    public void BatteryRunsOut()
    {
        var w = Arena.New(32);
        var pilot = w.Spawn(1, "Drone pilot", 5, 16);
        var target = w.Spawn(2, "technical", 15, 16);
        w.Order(1, CommandKind.UseAbility, [pilot], target.Id, ability: Kit);
        w.Step();
        var drone = Assert.Single(w.All("fpv_drone"));
        Assert.Equal(drone.Def.Lifetime - 1, drone.Lifetime);
        drone.Lifetime = 3;
        w.Run(3);
        Assert.Empty(w.All("fpv_drone"));
        Assert.Equal(target.Def.MaxHp, target.HP);
    }

    // AA missiles are the hard counter: one hit downs a drone. Bullets hit
    // drones for 15%, so a rifleman barely scratches one before it lands.
    [Theory]
    [InlineData("Drone hunter", true)]
    [InlineData("Rifleman", false)]
    public void DronesCanBeShotDown(string defender, bool downed)
    {
        var w = Arena.New(32);
        var pilot = w.Spawn(1, "Drone pilot", 2, 16);
        var target = w.Spawn(2, "technical", 15, 16);
        var guard = w.Spawn(2, defender, 13, 13, Facing.West);
        w.Order(1, CommandKind.UseAbility, [pilot], target.Id, ability: Kit);
        w.Step();
        var drone = Assert.Single(w.All("fpv_drone"));
        var lowest = drone.HP;
        RunUntilNoDrones(w, 100, () => lowest = w.Unit(drone.Id) is null ? lowest : Math.Min(lowest, drone.HP));

        Assert.Empty(w.All("fpv_drone"));
        Assert.Equal(guard.Def.MaxHp, guard.HP);
        if (downed)
        {
            Assert.Equal(target.Def.MaxHp, target.HP); // one missile, before it got there
        }
        else
        {
            Assert.InRange(lowest, 1, drone.Def.MaxHp - 1); // hit, but not downed
            Assert.True(target.HP < target.Def.MaxHp);
        }
    }

    [Fact]
    public void ChargesRestockAtADepot()
    {
        var w = Arena.New(32);
        w.Spawn(1, "supply_depot", 3, 3);
        var pilot = w.Spawn(1, "Drone pilot", 6, 6);
        pilot.MutableCharges[0] = 0;
        w.Order(1, CommandKind.UseAbility, [pilot], cell: new Cell(20, 20), ability: Kit);
        w.Step();
        Assert.Equal(OrderKind.Idle, pilot.Order.Kind); // no charges, so the order is refused
        Assert.Empty(w.All("fpv_drone"));
        w.Run(40); // pulses on ticks 0 and 40
        Assert.Equal([2, 1, 1], pilot.Charges);
    }
}
