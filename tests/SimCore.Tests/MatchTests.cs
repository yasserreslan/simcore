using SimCore.Model;
using SimCore.Replays;
using SimCore.Scenarios;
using SimCore.Sim;

namespace SimCore.Tests;

public class MatchTests
{
    [Fact]
    public void PlayerWithoutSoldiersLoses()
    {
        var w = Arena.New(32);
        w.Spawn(1, "Rifleman", 2, 2);
        var enemy = w.Spawn(2, "Rifleman", 28, 28);
        w.Spawn(2, "technical", 25, 25); // empty vehicles don't count
        w.Spawn(2, "supply_depot", 20, 28);
        w.Step();
        Assert.False(w.Result.Over);

        enemy.HP = 0;
        w.Step();
        Assert.True(w.Player(2)!.Defeated);
        Assert.False(w.Player(1)!.Defeated);
        Assert.Equal(new MatchResult(true, 1, w.Tick), w.Result);
    }

    [Fact]
    public void ADriverKeepsHisSideInTheGame()
    {
        var w = Arena.New(32);
        w.Spawn(1, "Rifleman", 2, 2);
        w.SpawnCrewed(2, "technical", 25, 25);
        w.Run(2);
        Assert.False(w.Player(2)!.Defeated);
        Assert.False(w.Result.Over);
    }

    [Fact]
    public void LastSoldiersFallingTogetherIsADraw()
    {
        var w = Arena.New(32);
        var a = w.Spawn(1, "Rifleman", 2, 2);
        var b = w.Spawn(2, "Rifleman", 28, 28);
        a.HP = 0;
        b.HP = 0;
        w.Step();
        Assert.Equal(new MatchResult(true, 0, 1), w.Result);
    }

    private static PlayerSetup EditPlayer(PlayerSetup p, string name) => name switch
    {
        "fourth preset for a class" => p with { Presets = [.. p.Presets, new Preset { Name = "Rifleman 2", Archetype = "assault", Components = ["rifle", "pistol"] }] },
        "wrong slot" => p with { Presets = [.. p.Presets.Select(x => x.Name == "Gunner" ? x with { Components = ["lmg", "lmg"] } : x)] },
        "missing slot" => p with { Presets = [.. p.Presets.Select(x => x.Name == "Gunner" ? x with { Components = ["lmg"] } : x)] },
        "unknown component" => p with { Presets = [.. p.Presets.Select(x => x.Name == "Gunner" ? x with { Components = ["laser", "pistol"] } : x)] },
        "preset for a vehicle" => p with { Presets = [.. p.Presets, new Preset { Name = "Truck", Archetype = "technical", Components = [] }] },
        "duplicate preset name" => p with { Presets = [.. p.Presets.Select(x => x.Name == "Marksman" ? x with { Name = "Rifleman" } : x)] },
        "name clashes with an archetype" => p with { Presets = [.. p.Presets.Select(x => x.Name == "Marksman" ? x with { Name = "sniper" } : x)] },
        "army names a class" => p with { Army = [.. p.Army, new ArmyEntry { Unit = "assault" }] },
        "army of drones" => p with { Army = [.. p.Army, new ArmyEntry { Unit = "fpv_drone" }] },
        "unknown army unit" => p with { Army = [.. p.Army, new ArmyEntry { Unit = "Ghost" }] },
        "zero count" => p with { Army = [.. p.Army, new ArmyEntry { Unit = "Rifleman", Count = 0 }] },
        "driver for a soldier" => p with { Army = [.. p.Army, new ArmyEntry { Unit = "Rifleman", Driver = "Gunner" }] },
        "worker can't drive" => p with { Army = [.. p.Army, new ArmyEntry { Unit = "technical", Driver = "worker" }] },
        "unknown faction" => p with { Faction = "empire" },
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [InlineData("fourth preset for a class", "more than 3 presets for archetype \"assault\"")]
    [InlineData("wrong slot", "\"lmg\" does not fit the secondary slot of \"assault\" (want one of pistol)")]
    [InlineData("missing slot", "want 2 components (one per slot of \"assault\"), got 1")]
    [InlineData("unknown component", "unknown component \"laser\"")]
    [InlineData("preset for a vehicle", "archetype \"technical\" has no loadout slots")]
    [InlineData("duplicate preset name", "preset \"Rifleman\": name used twice")]
    [InlineData("name clashes with an archetype", "preset \"sniper\": name clashes with an archetype ID")]
    [InlineData("army names a class", "archetype \"assault\" has loadout slots; name one of the player's presets")]
    [InlineData("army of drones", "\"fpv_drone\" is placed or launched by other units")]
    [InlineData("unknown army unit", "\"Ghost\" is neither a preset nor an archetype")]
    [InlineData("zero count", "count is 0")]
    [InlineData("driver for a soldier", "\"Rifleman\" has no driver seat")]
    [InlineData("worker can't drive", "\"worker\" cannot drive \"technical\"")]
    [InlineData("unknown faction", "unknown faction \"empire\"")]
    public void RejectsBadSetups(string name, string want)
    {
        var setup = TestContent.Battle(1);
        setup = setup with { Players = [EditPlayer(setup.Players[0], name), setup.Players[1]] };
        var e = Assert.Throws<MatchSetupException>(() => MatchCompiler.Compile(TestContent.Mvp, setup));
        Assert.Contains("player 1", e.Message);
        Assert.True(e.Message.Contains(want, StringComparison.Ordinal), $"\"{e.Message}\" lacks \"{want}\"");
    }

    [Fact]
    public void RejectsBadPlayerIdsAndOtherContent()
    {
        var setup = TestContent.Battle(1);
        var swapped = setup with { Players = [setup.Players[1], setup.Players[0]] };
        Assert.Contains("has id 2, want 1", Assert.Throws<MatchSetupException>(() => MatchCompiler.Compile(TestContent.Mvp, swapped)).Message);

        var other = setup with { ContentHash = "abc" };
        Assert.Throws<ContentHashMismatchException>(() => MatchCompiler.Compile(TestContent.Mvp, other));

        var unknown = setup with { Scenario = "moon" };
        Assert.Throws<UnknownScenarioException>(() => Replay.NewWorld(TestContent.Mvp, unknown));
    }

    [Fact]
    public void BattleSpawnsBothArmies()
    {
        var setup = TestContent.Battle(3);
        var w = Replay.NewWorld(TestContent.Mvp, setup);
        Assert.Equal((Scenario.BattleSize, Scenario.BattleSize), (w.Grid.Width, w.Grid.Height));
        foreach (var p in w.Players)
        {
            var want = setup.Players[p.Id - 1].Army.Sum(e => e.Count * (e.Driver is null ? 1 : 2));
            var units = w.Units.Where(u => u.Owner == p.Id).ToList();
            Assert.Equal(want, units.Count);
            Assert.Single(units, u => u.Def.Archetype.Id == "supply_depot");
            var vehicles = units.Where(u => u.Def.Crew is not null).ToList();
            Assert.Equal(8, vehicles.Count);
            Assert.Equal(6, vehicles.Count(v => v.Driver != 0));
            Assert.All(vehicles.Where(v => v.Driver != 0), v => Assert.Equal(v.Id, w.Unit(v.Driver)!.Inside));
            Assert.All(units, u => Assert.True(u.Def.IsStructure || w.Grid.Passable(Sim.Cell.Of(u.Pos))));
        }
        // The map and the armies are mirrored, so the start is fair.
        var p1 = w.Units.Where(u => u.Owner == 1 && u.Inside == 0).Select(u => (u.Def.Name, Scenario.BattleSize * 1024 - u.Pos.X.Raw, u.Pos.Y.Raw));
        var p2 = w.Units.Where(u => u.Owner == 2 && u.Inside == 0).Select(u => (u.Def.Name, u.Pos.X.Raw, u.Pos.Y.Raw));
        Assert.Equal(p1, p2);
    }

    // The setup travels in the replay: decoding gives back the same presets
    // and army, and the replay plays back to the same hashes.
    [Fact]
    public void SetupRoundTripsThroughReplay()
    {
        var setup = TestContent.Battle(11);
        var (rec, live) = Replay.Record(TestContent.Mvp, setup, 400);
        using var buf = new MemoryStream();
        rec.Encode(buf);
        var json = System.Text.Encoding.UTF8.GetString(buf.ToArray());
        Assert.Contains("\"version\": 2", json);
        Assert.Contains(TestContent.Mvp.Hash, json);
        buf.Position = 0;
        var back = Replay.Decode(buf);

        Assert.Equal(setup.Digest(), back.Setup.Digest());
        Assert.Equal(setup.Players.SelectMany(p => p.Presets.Select(x => (x.Name, x.Archetype, string.Join(",", x.Components)))),
            back.Setup.Players.SelectMany(p => p.Presets.Select(x => (x.Name, x.Archetype, string.Join(",", x.Components)))));
        Assert.Equal(live, back.Play(TestContent.Mvp).Hashes);
    }
}
