using SimCore.FixedPoint;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

public class ZzScratchTests(ITestOutputHelper output)
{
    [Fact]
    public void Walk()
    {
        foreach (var east in new[] { true, false })
        {
            var w = Arena.New(128);
            var u = w.Spawn(1, "Rifleman", east ? 20 : 107, 30);
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [u.Id], Cell = new Cell(east ? 100 : 27, 30) });
            for (var t = 0; t < 200; t++) { w.Step(); }
            output.WriteLine($"east {east}: x {u.Pos.X.Raw} y {u.Pos.Y.Raw} moved {(east ? u.Pos.X.Raw - (20 * 1024 + 512) : (107 * 1024 + 512) - u.Pos.X.Raw)}");
        }
    }

    [Fact]
    public void Duel()
    {
        int left = 0, right = 0;
        for (ulong seed = 1; seed <= 10; seed++)
        {
            foreach (var p1Left in new[] { true, false })
            {
                var w = Arena.New(128, seed);
                var rot = Environment.GetEnvironmentVariable("ROT") == "1";
                byte leftP = p1Left ? (byte)1 : (byte)2, rightP = p1Left ? (byte)2 : (byte)1;
                for (var i = 0; i < 6; i++)
                {
                    w.Spawn(leftP, "Rifleman", 30, 40 + i * 2, Facing.East).Stance = Stance.Aggressive;
                    w.Spawn(rightP, "Rifleman", 97, rot ? 127 - (40 + i * 2) : 40 + i * 2, Facing.West).Stance = Stance.Aggressive;
                }
                var lu = w.Units.Where(u => u.Owner == leftP).Select(u => u.Id).ToList();
                var ru = w.Units.Where(u => u.Owner == rightP).Select(u => u.Id).ToList();
                w.Issue(new Command { Tick = w.Tick, Player = leftP, Kind = CommandKind.AttackMove, Units = lu, Cell = new Cell(97, rot ? 82 : 45) });
                w.Issue(new Command { Tick = w.Tick, Player = rightP, Kind = CommandKind.AttackMove, Units = ru, Cell = new Cell(30, 45) });
                uint f1 = 0, f2 = 0;
                for (var t = 0; t < 1200; t++)
                {
                    w.Step();
                    foreach (var u in w.Units)
                    {
                        if (u.LastShot != 0 && u.Owner == 1 && f1 == 0) { f1 = w.Tick; output.WriteLine($"  P1 first shot t{w.Tick} #{u.Id} at x {u.Pos.X.Raw} y {u.Pos.Y.Raw} target {u.AutoTarget}/{u.AiTarget} {(w.Unit(u.AiTarget == 0 ? u.AutoTarget : u.AiTarget) is { } e ? $"{e.Pos.X.Raw},{e.Pos.Y.Raw} d {(e.Pos - u.Pos).Len().Raw}" : "")}"); }
                        if (u.LastShot != 0 && u.Owner == 2 && f2 == 0) { f2 = w.Tick; output.WriteLine($"  P2 first shot t{w.Tick} #{u.Id} at x {u.Pos.X.Raw} y {u.Pos.Y.Raw} target {u.AutoTarget}/{u.AiTarget} {(w.Unit(u.AiTarget == 0 ? u.AutoTarget : u.AiTarget) is { } e ? $"{e.Pos.X.Raw},{e.Pos.Y.Raw} d {(e.Pos - u.Pos).Len().Raw}" : "")}"); }
                    }
                }
                var a1 = w.Units.Count(u => u.Alive && u.Owner == 1);
                var a2 = w.Units.Count(u => u.Alive && u.Owner == 2);
                var l = p1Left ? a1 : a2;
                var r = p1Left ? a2 : a1;
                left += l > r ? 1 : 0;
                right += r > l ? 1 : 0;
                output.WriteLine($"seed {seed} p1Left {p1Left}: P1 {a1} P2 {a2} (left {l}, right {r})");
            }
        }
        output.WriteLine($"left wins {left}, right wins {right}");
    }
}
