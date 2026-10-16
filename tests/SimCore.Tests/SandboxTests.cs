using SimCore.FixedPoint;
using SimCore.Replays;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimCore.Tests;

public class SandboxTests
{
    private static World SandboxArena(int size = 32) =>
        Arena.New(size, edit: s => s with { Scenario = Scenario.SandboxName });

    private static Command Spawn(World w, byte player, string unit, int x, int y) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.SandboxSpawn, Unit = unit, Cell = new Cell(x, y) });

    [Fact]
    public void SpawnsPresetsAndArchetypesForTheIssuingPlayer()
    {
        var w = SandboxArena();
        Spawn(w, 2, "Gunner", 10, 12);
        Spawn(w, 1, "technical", 4, 4);
        w.Step();

        // Commands run in player order, so player 1's technical spawns first.
        Assert.Equal(2, w.Units.Count);
        var gunner = w.Units[1];
        Assert.Equal(2, gunner.Owner);
        Assert.Equal("Gunner", gunner.Def.Name);
        Assert.Equal(new Cell(10, 12).Center(), gunner.Pos);
        Assert.Equal(Facing.West, gunner.Facing);
        var tech = w.Units[0];
        Assert.Equal(1, tech.Owner);
        Assert.Equal("technical", tech.Def.Archetype.Id);
        Assert.Equal(Facing.East, tech.Facing);
        Assert.False(tech.CanAct); // vehicles spawn empty
    }

    [Fact]
    public void SpawnOnRockUsesTheNearestFreeCell()
    {
        var w = SandboxArena();
        w.Grid.Set(new Cell(5, 5), Terrain.Blocked);
        Spawn(w, 1, "Rifleman", 5, 5);
        w.Step();
        var c = Cell.Of(Assert.Single(w.Units).Pos);
        Assert.NotEqual(new Cell(5, 5), c);
        Assert.True(Math.Abs(c.X - 5) <= 1 && Math.Abs(c.Y - 5) <= 1);
    }

    [Fact]
    public void IgnoredOutsideSandboxAndForBadUnits()
    {
        var battle = Arena.New(32);
        Spawn(battle, 1, "Rifleman", 5, 5);
        battle.Step();
        Assert.Empty(battle.Units);

        var w = SandboxArena();
        Spawn(w, 1, "nobody", 5, 5);
        Spawn(w, 1, "assault", 5, 5); // has slots: only presets spawn it
        Spawn(w, 1, "fpv_drone", 5, 5); // drones are launched from a kit
        Spawn(w, 1, "Rifleman", -1, 5);
        Spawn(w, 1, "Rifleman", 5, 32);
        w.Step();
        Assert.Empty(w.Units);
    }

    // A sandbox session saved as a replay plays back to the same world.
    [Fact]
    public void SandboxReplayMatchesLiveSession()
    {
        var setup = TestContent.Battle(7) with { Scenario = Scenario.SandboxName };
        var live = Replay.NewWorld(TestContent.Mvp, setup);
        var commands = new List<Command>
        {
            Spawn(live, 1, "Marksman", 30, 40),
            Spawn(live, 2, "Rifleman", 60, 40),
        };
        var hashes = new List<ulong>();
        for (var i = 0; i < 400; i++)
        {
            if (i == 50)
            {
                var ids = live.Units.Where(u => u.Owner == 1 && u.Def.Name == "Marksman").Select(u => u.Id).ToList();
                commands.Add(live.Issue(new Command
                {
                    Tick = live.Tick + World.InputDelay, Player = 1, Kind = CommandKind.Move, Units = ids, Cell = new Cell(40, 40),
                }));
            }
            live.Step();
            hashes.Add(live.Hash());
        }

        var r = new Replay { Setup = setup, Ticks = 400, Commands = commands };
        using var s = new MemoryStream();
        r.Encode(s);
        Assert.Contains("\"unit\": \"Marksman\"", System.Text.Encoding.UTF8.GetString(s.ToArray()));
        s.Position = 0;
        var (played, world) = Replay.Decode(s).Play(TestContent.Mvp);
        Assert.Equal(hashes, played);
        Assert.Equal(live.Units.Count, world.Units.Count);
    }
}
