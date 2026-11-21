using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// INF-PERF (docs/design.md, decision 1220): World.WarmUp plays a squad's orders on a world of its own, so the order-time code is
// compiled before a player gives one; the world it is called on must stay exactly as it was, and play exactly as without it.
public class WarmUpTests
{
    private static World New() =>
        Replay.NewWorld(TestContent.Mvp, TestContent.Battle(42) with { Scenario = Scenarios.Scenario.SandboxName, Terrain = "fog_town" }, FogGoldenTests.Map());

    private static List<ulong> Play(World w)
    {
        var p = w.Player(1)!.Setup;
        string[] kinds = ["Rifleman", "Rifleman", "Gunner", "Tank hunter", "Marksman", "Rifleman"];
        var men = kinds.Select((k, i) => w.Spawn(p.Find(w.Content, k)!, new Cell(10 + i % 3, 20 + i / 3), Facing.East)).ToList();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.FormSquad, Units = men });
        var hashes = new List<ulong>();
        for (var t = 0; t < 160; t++)
        {
            if (t == 20)
            {
                w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetSquadMode, Index = (int)SquadMode.Hold, Cell = new Cell(26, 22), Cell2 = new Cell(36, 22), Units = [men[0]] });
            }
            w.Step();
            hashes.Add(w.Hash());
        }
        return hashes;
    }

    [Fact]
    public void WarmUpLeavesTheWorldAsItWas()
    {
        var plain = New();
        var warmed = New();
        warmed.WarmUp();
        Assert.Equal(plain.Hash(), warmed.Hash());
        Assert.Equal(0u, warmed.Tick);
        Assert.Equal(Play(plain), Play(warmed));
    }
}
