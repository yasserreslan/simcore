using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-10: the battle rules still not built: 7.3's sidearm on a long move away, 7.4's reload on the move, 13.5's vehicles answering
// calls, 14.4's Hold re-pick away from an impact zone.
public class BattleRulesLeftTests(ITestOutputHelper output)
{
    private static World New(int size = 64, ulong seed = 1)
    {
        var w = Arena.New(size, seed);
        w.CombatOff = false;
        return w;
    }

    private static Squad Form(World w, params Unit[] units)
    {
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.FormSquad, Units = [.. units.Select(u => u.Id)] });
        w.Step();
        return w.SquadOf(units[0].Id)!;
    }

    private static void Move(World w, Unit u, Cell to) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.Move, Units = [u.Id], Cell = to });

    private static void Until(World w, Func<bool> done, int limit)
    {
        for (var i = 0; i < limit && !done(); i++)
        {
            w.Step();
        }
    }

    // 7.4: "He doesn't reload while moving faster than Walk; under Walk, Strafe, Backpedal or Crouch-walk he reloads while moving at
    // half that gait's speed."
    [Fact]
    public void HeDoesNotReloadRunningAndWalksAtHalfPaceReloading()
    {
        var w = New();
        var runner = w.Spawn(1, "Rifleman", 10, 10, Facing.East);
        Move(w, runner, new Cell(40, 10));
        Until(w, () => runner.Fight is { Gait: Gait.Run } && runner.Moving, 60);
        Assert.Equal(Gait.Run, runner.Fight!.Gait);
        var a = runner.Arms!;
        (a.Reloading, a.ReloadLeft) = (0, 30);
        for (var i = 0; i < 10; i++)
        {
            w.Step();
        }
        output.WriteLine($"running: gait {runner.Fight.Gait}, reload left {a.ReloadLeft} of 30 after 10 ticks");
        Assert.Equal(30, a.ReloadLeft);

        var blue = w.Spawn(1, "Rifleman", 20, 30, Facing.East);
        var red = w.Spawn(2, "Rifleman", 25, 30, Facing.West);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [red.Id], Stance = Stance.HoldFire });
        Until(w, () => blue.Fight is { InContact: true, Face: >= 0 }, 200);
        Assert.True(blue.Fight!.InContact, "in contact");
        Move(w, blue, new Cell(23, 30));
        Until(w, () => blue.Fight.Gait == Gait.Walk, 60);
        Assert.Equal(Gait.Walk, blue.Fight.Gait);
        var r = w.Content.InfantryCombat!;
        blue.Arms!.Reloading = -1;
        var plain = w.GaitPercent(blue, blue.Fight, r);
        (blue.Arms.Reloading, blue.Arms.ReloadLeft) = (0, 30);
        var reloading = w.GaitPercent(blue, blue.Fight, r);
        output.WriteLine($"walking in contact: {plain}% of his speed, reloading {reloading}%");
        Assert.Equal(plain * r[CombatValue.GaitReloadMove] / 100, reloading);
    }

    // 7.3: "A soldier who must move away inside the enemy's reach for more than 20 ticks first draws his sidearm ... and backpedals
    // firing it" (a man whose main arm can't fire on the move: the marksman rifle). At the backpedal's pace (35% of 1.7 cells/s) any
    // ordered move of a cell or more is longer than 20 ticks.
    [Fact]
    public void AMarksmanBackingAwayInReachDrawsHisSidearmFirst()
    {
        var w = New();
        var m = w.Spawn(1, "Marksman", 30, 30, Facing.East);
        var red = Enumerable.Range(0, 3).Select(i => w.Spawn(2, "Rifleman", 35, 29 + i, Facing.West)).ToArray();
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        Assert.True(m.Def.Sidearm >= 0, "the marksman has a sidearm");
        Until(w, () => m.Fight is { InContact: true, Face: >= 0, OutOfReach: false }, 200);
        Assert.True(m.Fight!.InContact, "in contact");
        Assert.Equal(0, m.Arms!.InHand);
        Move(w, m, new Cell(20, 30));
        int back = 0, sidearm = 0;
        for (var i = 0; i < 60; i++)
        {
            w.Step();
            if (m.Fight.Gait == Gait.Backpedal)
            {
                back++;
                sidearm += m.Arms.InHand == m.Def.Sidearm ? 1 : 0;
            }
        }
        output.WriteLine($"10 cells back: {back} ticks backpedalling in reach, {sidearm} with the sidearm in hand (its draw {m.Def.Weapons[m.Def.Sidearm].Effect.DrawTicks} ticks)");
        Assert.True(back > 0, "he backpedals");
        Assert.True(sidearm * 2 >= back, "the sidearm drawn for most of his move away");
    }

    // 13.5: a friendly ground vehicle with no order, on Guard or Aggressive, with a weapon that hurts the call's enemy, answers a call
    // of U 60+ within 20 cells: it attack-moves 4 to 6 cells behind the caller's line, offset 3 cells to the side. A vehicle on Hold
    // ground (a still stance) doesn't, even nearer.
    [Fact]
    public void AnIdleVehicleOnGuardAnswersAnUrgentCall()
    {
        var w = New();
        var a = Form(w, [.. Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 20, 28 + i, Facing.East))]);
        var guard = w.Spawn(1, "technical", 14, 44, Facing.North);
        var still = w.Spawn(1, "technical", 16, 40, Facing.North);
        foreach (var v in new[] { guard, still })
        {
            World.Board(w.Spawn(1, "Rifleman", Cell.Of(v.Pos).X, Cell.Of(v.Pos).Y), v); // its crew aboard (a vehicle with none can't act)
        }
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetStance, Units = [guard.Id], Stance = Stance.Guard });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetStance, Units = [still.Id], Stance = Stance.HoldGround });
        var red = Enumerable.Range(0, 10).Select(i => w.Spawn(2, "Rifleman", 29, 25 + i, Facing.West)).ToArray();
        Form(w, red);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackMove, Units = [red[0].Id], Cell = new Cell(14, 29) });
        var maxU = 0;
        for (var i = 0; i < 300 && guard.Order.Kind == OrderKind.Idle; i++)
        {
            w.Step();
            maxU = Math.Max(maxU, a.Mind!.Urgency);
        }
        var m = a.Mind!;
        var to = guard.Order.Cell;
        var axis = World.Directions[m.Axis & 31];
        long ox = to.X - m.CallAt.X, oy = to.Y - m.CallAt.Y;
        var along = (ox * axis.X + oy * axis.Y) / 1024.0;
        output.WriteLine($"tick {w.Tick}: U {m.Urgency} (max {maxU}), axis {m.Axis}, call at {m.CallAt}; the guard's order {guard.Order.Kind} to {to} ({along:0.0} cells along the axis), the still one's {still.Order.Kind}");
        Assert.Equal(OrderKind.AttackMove, guard.Order.Kind);
        Assert.True(along is <= -3.5 and >= -6.5, $"behind the caller's line: {along:0.0} cells along its axis");
        Assert.Equal(OrderKind.Idle, still.Order.Kind);
    }

    // 14.4: "Squads in Hold stay unless the zone covers half or more of their positions, then they re-pick positions (S2) within their
    // Hold radius avoiding the zone."
    [Fact]
    public void AHoldSquadRepicksItsPositionsAwayFromAnImpactZone()
    {
        var w = New();
        var men = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 30 + i, 34, Facing.North)).ToArray();
        var s = Form(w, men);
        var spot = new Cell(32, 32);
        w.Issue(new Command
        {
            Tick = w.Tick, Player = 1, Kind = CommandKind.SetSquadMode, Units = [.. men.Select(u => u.Id)], Index = (int)SquadMode.Hold,
            Cell = spot, Cell2 = new Cell(32, 22),
        });
        Until(w, () => s.HoldPlaced && s.Order == OrderKind.Idle && men.All(u => !u.Moving), 600);
        Assert.True(s.HoldPlaced, "placed");
        var before = s.HoldPicks.ToList();
        output.WriteLine($"picks {string.Join(" ", before.Select(p => p.Cell))}");
        foreach (var p in before.Take(2))
        {
            w.NoteBlast(p.Cell.Center(), Fixed.FromInt(2)); // shells on half its positions
        }
        w.Step();
        var m = s.Mind!;
        Assert.True(m.ImpactUntil > w.Tick, "under indirect fire");
        var zoned = before.Count(p => w.ZoneValue(p.Cell) > 0);
        Assert.True(zoned * 2 >= before.Count, $"the zone covers {zoned} of {before.Count} positions");
        Until(w, () => s.HoldPlaced && s.HoldPicks.All(p => w.ZoneValue(p.Cell) == 0) && men.All(u => !u.Moving && w.ZoneValue(Cell.Of(u.Pos)) == 0), 200);
        var radius = w.Content.Squad!.Hold!.Radius.Raw;
        output.WriteLine($"tick {w.Tick}: picks {string.Join(" ", s.HoldPicks.Select(p => p.Cell))}; men {string.Join(" ", men.Select(u => Cell.Of(u.Pos)))}");
        Assert.True(s.HoldPicks.All(p => w.ZoneValue(p.Cell) == 0), "its positions avoid the zone");
        Assert.True(s.HoldPicks.All(p => (p.Cell.Center() - spot.Center()).LenSq() <= radius * radius), "within its Hold radius");
        Assert.True(men.All(u => w.ZoneValue(Cell.Of(u.Pos)) == 0), "its men are out of the zone");
        Assert.Equal(SquadMode.Hold, s.Mode);
    }
}
