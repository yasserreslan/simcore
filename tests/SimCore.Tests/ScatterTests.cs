using SimCore.FixedPoint;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// Scatter: each unit moves straight away from the group's centre (the unit AI).
public class ScatterTests
{
    private static void Scatter(World w, params Unit[] units) =>
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.Scatter, Units = [.. units.Select(u => u.Id)] });

    // A 3 x 3 block of riflemen: the eight round the centre move 2 cells (4 x 0.3 is less) straight
    // out; the one on the centre goes the way he faces (north).
    [Fact]
    public void EachMovesAwayFromTheCentre()
    {
        var w = Arena.New(64);
        var squad = new List<Unit>();
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                squad.Add(w.Spawn(1, "Rifleman", 30 + x * 2, 30 + y * 2, Facing.North));
            }
        }
        var before = squad.Select(u => u.Pos).ToList();
        var centre = squad[4].Pos;
        Scatter(w, [.. squad]);
        w.Run(1);
        Assert.All(squad, u => Assert.Equal(OrderKind.Move, u.Order.Kind));
        w.Run(200);
        for (var i = 0; i < squad.Count; i++)
        {
            var u = squad[i];
            Assert.Equal(OrderKind.Idle, u.Order.Kind);
            var moved = u.Pos - before[i];
            Assert.InRange(moved.Len().Raw, Fixed.OneRaw, 3 * Fixed.OneRaw); // about 2 cells (to a cell centre)
            if (i != 4)
            {
                var out0 = before[i] - centre;
                Assert.True(moved.X.Raw * out0.X.Raw + moved.Y.Raw * out0.Y.Raw > 0, $"unit {i} moved {moved}");
            }
        }
        var north = Facing.Dir(Facing.North);
        var m4 = squad[4].Pos - before[4];
        Assert.True(m4.X.Raw * north.X.Raw + m4.Y.Raw * north.Y.Raw > 0, $"the centre one moved {m4}");
    }

    // Vehicles go farther: 4 x their radius (a technical: 3.2 cells).
    [Fact]
    public void VehiclesGoFourRadii()
    {
        var w = Arena.New(64);
        var a = w.SpawnCrewed(1, "technical", 28, 30, Facing.West).Vehicle;
        var b = w.SpawnCrewed(1, "technical", 32, 30, Facing.East).Vehicle;
        var (pa, pb) = (a.Pos, b.Pos);
        Scatter(w, a, b);
        w.Run(1);
        Assert.Equal(Cell.Of(pa + new Vec(Fixed.FromRaw(-a.Def.Archetype.Radius.Raw * 4), Fixed.Zero)), a.Order.Cell);
        Assert.Equal(Cell.Of(pb + new Vec(Fixed.FromRaw(b.Def.Archetype.Radius.Raw * 4), Fixed.Zero)), b.Order.Cell);
    }

    // The same command gives the same world, and listing the units in another order changes nothing.
    [Fact]
    public void Deterministic()
    {
        static ulong Play(bool reversed)
        {
            var w = Arena.New(64, seed: 9);
            var squad = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 28 + i % 3, 30 + i / 3)).ToList();
            if (reversed)
            {
                squad.Reverse();
            }
            Scatter(w, [.. squad]);
            w.Run(200);
            return w.Hash();
        }
        Assert.Equal(Play(false), Play(false));
        Assert.Equal(Play(false), Play(true));
    }

    // A chase ends with the scatter; set-up launchers stay put.
    [Fact]
    public void LaunchersSetUpStayPut()
    {
        var w = Arena.New(64);
        var (truck, _) = w.SpawnCrewed(1, "heavy_rocket_launcher", 30, 30);
        var u = w.Spawn(1, "Rifleman", 32, 30);
        w.Order(1, CommandKind.Deploy, [truck]);
        w.Run(80);
        Assert.Equal(DeployState.Deployed, truck.Deploy);
        Scatter(w, truck, u);
        w.Run(1);
        Assert.Equal(OrderKind.Idle, truck.Order.Kind);
        Assert.Equal(OrderKind.Move, u.Order.Kind);
    }
}
