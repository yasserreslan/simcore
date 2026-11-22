using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-3 part 4: the class plug-ins in the squad fight (battle rules 16): where each class stands in the contact line.
public class SquadClassTests(ITestOutputHelper output)
{
    private static (World W, Squad S, Unit[] Rifles, Unit[] Others) Scene(bool sentinel, Action<World>? map, params string[] others)
    {
        var w = sentinel ? Arena.NewSentinel(48, 1) : Arena.New(48, 1);
        w.CombatOff = false;
        map?.Invoke(w);
        Unit[] rifles = [.. Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 20, 17 + i * 2))];
        Unit[] more = [.. others.Select((n, i) => w.Spawn(1, n, 19 - i, 20 + i))];
        Unit[] red = [.. Enumerable.Range(0, 6).Select(i => w.Spawn(2, "Rifleman", 27, 16 + i * 2))];
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.FormSquad, Units = [.. rifles.Select(u => u.Id), .. more.Select(u => u.Id)] });
        w.Step();
        var s = w.SquadOf(rifles[0].Id)!;
        for (var i = 0; i < 200 && !(s.Mind?.HasLine ?? false); i++)
        {
            w.Step();
        }
        return (w, s, rifles, more);
    }

    private static Cell SlotOf(Squad s, Unit u) => s.Mind!.Line.First(p => p.Unit == u.Id).Slot;

    // 16.3 and 16.7: the launcher stands at a line end (one spacing past the last rifle, a cell back), the Kornet 4 cells behind
    // the line's end; both on their own side of the centre. (The threat is east: across is y, back is -x.)
    [Fact]
    public void TheLauncherAndTheKornetStandAtTheLinesEnd()
    {
        var (_, s, rifles, more) = Scene(false, null, "Tank hunter", "kornet");
        var m = s.Mind!;
        var ys = rifles.Select(u => SlotOf(s, u).Y).ToArray();
        var (rpg, kornet) = (SlotOf(s, more[0]), SlotOf(s, more[1]));
        var lineX = rifles.Max(u => SlotOf(s, u).X);
        output.WriteLine($"axis {m.LineAxis} centre {m.LineCentre} rifles {string.Join(" ", rifles.Select(u => SlotOf(s, u)))} rpg {rpg} kornet {kornet}");
        Assert.True(m.HasLine);
        Assert.True(rpg.Y < ys.Min() || rpg.Y > ys.Max(), $"the launcher at a line end ({rpg})");
        Assert.InRange(lineX - rpg.X, 0, 2);
        Assert.InRange(lineX - kornet.X, 3, 5);
        Assert.True(Math.Min(Math.Abs(kornet.Y - ys.Min()), Math.Abs(kornet.Y - ys.Max())) <= 1, $"the Kornet behind the line's end ({kornet})");
    }

    // 16.5: the medic 2 cells behind the line's middle takes the best cover in the 3 x 3 cells round that point (a rock south-east of
    // the cell below his point gives that cell cover toward the threat; his point itself has none).
    [Fact]
    public void TheMedicTakesTheBestCoverRoundHisPoint()
    {
        var (w, s, _, more) = Scene(true, w => w.Grid.Set(new Cell(19, 22), Terrain.Blocked), "sentinel_medic");
        var m = s.Mind!;
        var slot = SlotOf(s, more[0]);
        output.WriteLine($"axis {m.LineAxis} centre {m.LineCentre} medic {slot} protection {w.Protection(slot, Posture.Kneel, m.LineAxis, false)} at (18, 20) {w.Protection(new Cell(18, 20), Posture.Kneel, m.LineAxis, false)}");
        Assert.Equal(0, w.Protection(new Cell(18, 20), Posture.Kneel, m.LineAxis, false));
        Assert.True(w.Protection(slot, Posture.Kneel, m.LineAxis, false) > 0, $"in cover ({slot})");
        Assert.True(Math.Abs(slot.X - 18) <= 1 && Math.Abs(slot.Y - 20) <= 1, $"round his point ({slot})");
    }
}
