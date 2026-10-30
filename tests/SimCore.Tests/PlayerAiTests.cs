using System.Text.Json;
using SimCore.Bots;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Replays;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// Small policy/execution checks. Full match quality and long replays belong to CI or later playtesting.
public sealed class PlayerAiTests
{
    private static PlayerAiRules Rules() => PlayerAiLoader.Load(Path.Combine(TestPaths.RepoRoot(), "content", "ai", "player.json")) with
    { DecisionTicks = 1, HomeGuards = 0, ScoutMissions = 0, AssemblyTicks = 3, ForceMinimum = 4, Factions = [], FormSquads = false, Foundation = null };

    private static World Game(int count = 3, bool fog = false)
    {
        var w = Arena.New(96, edit: s => s with { Fog = fog });
        w.Spawn(1, "supply_depot", 8, 8);
        for (var i = 0; i < count; i++) { w.Spawn(1, "Rifleman", 14 + i, 18); }
        w.Spawn(2, "supply_depot", 78, 78);
        w.Spawn(2, "Rifleman", 84, 84);
        return w;
    }

    private static List<Command> Play(World w, PlayerAi ai, int ticks)
    {
        var all = new List<Command>();
        for (var i = 0; i < ticks; i++)
        {
            foreach (var c in ai.Think(w)) { all.Add(w.Issue(c)); }
            w.Step();
        }
        return all;
    }

    [Fact]
    public void ThinkOnlyReturnsDelayedCommandsAndDoesNotChangeWorldOrRng()
    {
        var w = Game();
        var before = w.Hash();
        var ai = new PlayerAi(1, Rules());
        var commands = ai.Think(w);
        Assert.NotEmpty(commands);
        Assert.All(commands, c =>
        {
            Assert.Equal(w.Tick + World.InputDelay, c.Tick);
            Assert.Equal(1, c.Player);
            Assert.All(c.Units, id => Assert.Equal(1, w.Unit(id)!.Owner));
        });
        Assert.Equal(before, w.Hash());
        Assert.Empty(ai.Think(w));
    }

    [Fact]
    public void SmallForceLaunchesAtAssemblyDeadline()
    {
        var w = Game(2);
        var ai = new PlayerAi(1, Rules());
        Play(w, ai, 6);
        Assert.Contains(ai.Decisions, d => d.Reason == "mission-launched");
        Assert.Contains(ai.Missions, m => m.Kind == AiMissionKind.Assault && m.Phase == AiMissionPhase.Active && m.Members.Count == 2);
    }

    [Fact]
    public void MissionsReleaseStalledObjectivesAndReturn()
    {
        var w = Game(2);
        var ai = new PlayerAi(1, Rules() with { MissionTicks = 4, RecoveryTicks = 3, RestTicks = 20 });
        Play(w, ai, 12);
        Assert.Contains(ai.Decisions, d => d.Reason == "mission-expired");
        Assert.Contains(ai.Decisions, d => d.Reason is "recovered" or "recovery-expired");
    }

    [Fact]
    public void DepletedForceRetreatsRatherThanReceivingAnotherAttack()
    {
        var w = Game(2);
        var ai = new PlayerAi(1, Rules());
        Play(w, ai, 5);
        foreach (var u in w.Units.Where(u => u.Owner == 1 && !u.Def.IsStructure)) { u.HP = 1; }
        var commands = ai.Think(w);
        Assert.Contains(ai.Decisions, d => d.Reason == "force-depleted");
        Assert.Contains(commands, c => c.Kind == CommandKind.Move);
        Assert.DoesNotContain(commands, c => c.Kind == CommandKind.Attack);
    }

    [Fact]
    public void HiddenEnemyMovementHealthAndOrdersDoNotChangeDecisions()
    {
        var a = Game(3, true);
        var b = Game(3, true);
        a.Run(2); b.Run(2);
        var hidden = b.Units.Single(u => u.Owner == 2 && !u.Def.IsStructure);
        Assert.False(World.IsVisibleTo(hidden, 1));
        hidden.Pos = new Cell(75, 82).Center();
        hidden.HP = 1;
        hidden.Order = new Order(OrderKind.Guard, new Cell(75, 82), 0, 0);
        var aa = new PlayerAi(1, Rules() with { ScoutMissions = 1 });
        var bb = new PlayerAi(1, Rules() with { ScoutMissions = 1 });
        for (var tick = 0; tick < 60; tick++)
        {
            var ac = aa.Think(a); var bc = bb.Think(b);
            Assert.Equal(JsonSerializer.Serialize(ac), JsonSerializer.Serialize(bc));
            Assert.Equal(JsonSerializer.Serialize(aa.Missions), JsonSerializer.Serialize(bb.Missions));
            foreach (var c in ac) { a.Issue(c); }
            foreach (var c in bc) { b.Issue(c); }
            a.Step(); b.Step();
        }
    }

    [Fact]
    public void ProductionCountsCommandsStillInFlight()
    {
        var w = Game(1);
        var garage = w.Spawn(1, "vehicle_garage", 30, 12);
        var ai = new PlayerAi(1, Rules() with { Factions = [new AiFactionPlan
        { Faction = "resistance", Production = [new AiUnitGoal { Unit = "technical", Count = 1 }] }] });
        var commands = Play(w, ai, 8);
        Assert.Single(commands, c => c.Kind == CommandKind.Produce);
        Assert.Equal(1, garage.ProductionQueue.Count + garage.Bays.Count(b => b.Def >= 0));
    }

    [Fact]
    public void HiddenDestructionDoesNotEraseARememberedObjective()
    {
        var w = Game(1, true);
        var scout = w.Units.Single(u => u.Owner == 1 && !u.Def.IsStructure);
        var depot = w.Units.Single(u => u.Owner == 2 && u.Def.IsStructure);
        scout.Pos = new Cell(76, 76).Center();
        w.Run(2);
        Assert.True(World.IsVisibleTo(depot, 1));
        var ai = new PlayerAi(1, Rules() with { ForceMinimum = 1 });
        ai.Think(w);
        Assert.Contains(ai.Missions, m => m.Objective == Cell.Of(depot.Pos));
        scout.Pos = new Cell(14, 18).Center();
        w.Run(2);
        Assert.False(World.IsVisibleTo(depot, 1));
        depot.HP = 0;
        w.Run(2);
        ai.Think(w);
        Assert.DoesNotContain(ai.Decisions, d => d.Reason == "objective-observed-empty");
        Assert.Contains(ai.Missions, m => m.Objective == Cell.Of(depot.Pos));
        scout.Pos = new Cell(78, 78).Center();
        w.Run(2);
        ai.Think(w);
        Assert.Contains(ai.Decisions, d => d.Reason == "objective-observed-empty");
    }

    [Fact]
    public void ConstructionUsesNormalPlacementAndReplacesAMissingBuilder()
    {
        var w = Game(1);
        var worker = w.Spawn(1, "worker", 18, 22);
        var rules = Rules() with { BuildRadiusCells = 16, BuildSpacingCells = 4, BuildChecksPerDecision = 16, Factions = [new AiFactionPlan
        { Faction = "resistance", Construction = [new AiUnitGoal { Unit = "supply_depot", Count = 2 }] }] };
        var ai = new PlayerAi(1, rules);
        var commands = Play(w, ai, 80);
        Assert.Single(commands, c => c.Kind == CommandKind.Build);
        var site = Assert.Single(w.Units, u => u.Owner == 1 && !u.IsBuilt);
        worker.HP = 0;
        w.Spawn(1, "worker", 19, 22);
        w.Run(2);
        Assert.Contains(ai.Think(w), c => c.Kind == CommandKind.Resume && c.Target == site.Id);
    }

    [Fact]
    public void ArmyCapIncludesTheDriverOfAProducedVehicle()
    {
        var w = Game(1); // two own units, plus a garage: three; a crewed vehicle needs two more
        w.Spawn(1, "vehicle_garage", 30, 12);
        var ai = new PlayerAi(1, Rules() with { ArmyCap = 4, Factions = [new AiFactionPlan
        { Faction = "resistance", Production = [new AiUnitGoal { Unit = "technical", Count = 1 }] }] });
        Assert.DoesNotContain(ai.Think(w), c => c.Kind == CommandKind.Produce);
    }

    [Fact]
    public void CargoReservationsSendOnlyOneTruckToEachLowDepot()
    {
        var w = Game(0);
        var depot = w.Units.Single(u => u.Owner == 1);
        depot.Stock = 1;
        w.SpawnCrewed(1, "cargo_truck", 20, 20);
        w.SpawnCrewed(1, "cargo_truck", 24, 20);
        var ai = new PlayerAi(1, Rules());
        var commands = ai.Think(w);
        var unload = Assert.Single(commands, c => c.Kind == CommandKind.Unload);
        Assert.Equal(depot.Id, unload.Target);
        Assert.Single(unload.Units);
    }

    [Fact]
    public void EmptyVehicleGetsOneEligibleDriverWithoutDoubleAssigningHim()
    {
        var w = Game(3);
        var vehicle = w.Spawn(1, "technical", 16, 19);
        var ai = new PlayerAi(1, Rules());
        var commands = Play(w, ai, 8);
        var enter = Assert.Single(commands, c => c.Kind == CommandKind.Enter);
        Assert.Equal(vehicle.Id, enter.Target);
        Assert.DoesNotContain(ai.Missions.SelectMany(m => m.Members), id => enter.Units.Contains(id));
    }

    [Fact]
    public void MissionReservationsNeverOverlapAndExistingSquadsAreNotSplit()
    {
        var w = Game(8);
        var soldiers = w.Units.Where(u => u.Owner == 1 && !u.Def.IsStructure).Take(4).ToArray();
        w.Order(1, CommandKind.FormSquad, soldiers);
        w.Run(2);
        var ai = new PlayerAi(1, Rules() with { HomeGuards = 2, ScoutMissions = 1 });
        ai.Think(w);
        var all = ai.Missions.SelectMany(m => m.Members).ToArray();
        Assert.Equal(all.Length, all.Distinct().Count());
        var mission = Assert.Single(ai.Missions, m => m.Members.Contains(soldiers[0].Id));
        Assert.All(soldiers, u => Assert.Contains(u.Id, mission.Members));
    }

    [Fact]
    public void DiagnosticsDoNotAffectCommandsAndRecordedCommandsReplayWithoutTheBot()
    {
        var content = TestContent.Mvp;
        var setup = TestContent.Battle(4) with { Fog = true };
        var a = Replay.NewWorld(content, setup); var b = Replay.NewWorld(content, setup);
        var aa = new PlayerAi(1, Rules()); var bb = new PlayerAi(1, Rules() with { TraceCapacity = 0 });
        var commands = new List<Command>();
        var hashes = new List<ulong>();
        for (var i = 0; i < 60; i++)
        {
            var ac = aa.Think(a); var bc = bb.Think(b);
            Assert.Equal(JsonSerializer.Serialize(ac), JsonSerializer.Serialize(bc));
            foreach (var c in ac) { commands.Add(a.Issue(c)); }
            foreach (var c in bc) { b.Issue(c); }
            a.Step(); b.Step();
            Assert.Equal(a.Hash(), b.Hash());
            hashes.Add(a.Hash());
        }
        var replay = new Replay { Setup = setup, Ticks = a.Tick, Commands = commands };
        var playback = replay.Start(content);
        for (var i = 0; i < hashes.Count; i++) { playback.Step(); Assert.Equal(hashes[i], playback.Hash()); }
        Assert.Empty(bb.Decisions);
    }

    [Theory]
    [InlineData("decisionTicks", "0")]
    [InlineData("forceMinimum", "99")]
    [InlineData("retreatHpPercent", "90")]
    [InlineData("traceCapacity", "999999")]
    public void RejectsInvalidPolicyLimits(string name, string value)
    {
        var json = JsonSerializer.Serialize(Rules(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node[name] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => PlayerAiLoader.Parse(node.ToJsonString()));
    }

    [Fact]
    public void PolicyRejectsUnknownFieldsAndBotRejectsAnotherWorld()
    {
        Assert.Throws<JsonException>(() => PlayerAiLoader.Parse("{\"unknown\":true}"));
        var ai = new PlayerAi(1, Rules());
        ai.Think(Game());
        Assert.Throws<ArgumentException>(() => ai.Think(Game()));
    }
}
