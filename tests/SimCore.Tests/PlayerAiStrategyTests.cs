using SimCore.Bots;
using SimCore.Content;
using SimCore.Sim;

namespace SimCore.Tests;

public sealed class PlayerAiStrategyTests
{
    private static PlayerAiRules Rules()
    {
        var r = PlayerAiLoader.Load(Path.Combine(TestPaths.RepoRoot(), "content", "ai", "player.json"));
        return r with { DecisionTicks = 1, HomeGuards = 0, ScoutMissions = 0, Factions = [], FormSquads = false,
            Foundation = r.Foundation! with { Composition = [], Aircraft = false, Transports = false,
                Postures = r.Foundation!.Postures.Select(p => p with
                { DefenderPercent = 0, AssaultForces = 0, Scouts = 0, DistantStrikes = false, Insertions = false }).ToArray() } };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportFireRespectsTheDistantStrikePermission(bool allowed)
    {
        var w = Arena.New(128);
        w.Spawn(1, "supply_depot", 8, 8);
        var (launcher, _) = w.SpawnCrewed(1, "heavy_rocket_launcher", 60, 60);
        var target = w.Spawn(2, "supply_depot", 90, 90);
        w.Spawn(2, "Rifleman", 120, 120);
        w.Spawn(1, "worker", 88, 88);
        w.Run(2);
        Assert.True(World.IsVisibleTo(target, 1));
        Assert.True(w.PickWeapon(launcher, target, true) >= 0);
        var rules = Rules();
        rules = rules with { Foundation = rules.Foundation! with
        { Postures = rules.Foundation!.Postures.Select(p => p with { DistantStrikes = allowed }).ToArray() } };
        var ai = new PlayerAi(1, rules);
        var commands = ai.Think(w);
        Assert.Equal(allowed, commands.Any(c => c.Kind == CommandKind.AttackGround && c.Units.Contains(launcher.Id)));
    }

    [Fact]
    public void StrategyCancelsBoardingBeforeTheSpecialistRetryClock()
    {
        var w = Arena.New(96);
        w.Spawn(1, "supply_depot", 8, 8);
        var (carrier, _) = w.SpawnCrewed(1, "infantry_carrier", 30, 30);
        var passenger = w.Spawn(1, "Rifleman", 15, 15);
        var rules = Rules();
        var initial = new PlayerAi(1, rules);
        initial.Think(w);
        var state = initial.CaptureState();
        state = state with { Clock = state.Clock with { NextDecision = w.Tick, JobSerial = 1 }, Missions = [],
            Specialists = [new AiSpecialistMemory(1, AiSpecialistKind.Transport, carrier.Id, 0, new Cell(70, 70),
                default, new Cell(15, 15), w.Tick, w.Tick + 500, w.Tick + 100, 0, CommandKind.Move, 0, -1, false, [passenger.Id])] };
        w.Issue(new Command { Tick = w.Tick + World.InputDelay, Player = 1, Kind = CommandKind.Enter,
            Units = [passenger.Id], Target = carrier.Id });
        var ai = new PlayerAi(1, rules);
        ai.RestoreState(w, state);
        var commands = ai.Think(w);
        Assert.Contains(commands, c => c.Kind == CommandKind.Stop && c.Units.Contains(passenger.Id) && c.Units.Contains(carrier.Id));
        Assert.Contains(ai.SpecialistJobs, j => j.Stage == 3 && j.Passengers.Contains(passenger.Id));
    }

    [Fact]
    public void DefensiveReinforcementsReceiveTheAssignedAssetOrderEvenWhenAlreadyGuarding()
    {
        var w = Arena.New(96);
        w.Spawn(1, "supply_depot", 8, 8);
        w.Spawn(1, "Rifleman", 15, 15);
        w.Spawn(2, "Rifleman", 88, 88);
        var rules = Rules();
        rules = rules with { Foundation = rules.Foundation! with { Postures = rules.Foundation!.Postures.Select(p => p with
        { DefenderPercent = 100 }).ToArray() } };
        var ai = new PlayerAi(1, rules);
        for (var i = 0; i < 5; i++)
        {
            foreach (var c in ai.Think(w)) { w.Issue(c); }
            w.Step();
        }
        var recruit = w.Spawn(1, "Rifleman", 30, 30);
        recruit.Order = new Order(OrderKind.Guard, new Cell(30, 30), 0, 0);
        var commands = ai.Think(w);
        var defense = Assert.Single(ai.Missions);
        Assert.Equal(Assert.Single(w.Units, u => u.Owner == 1 && u.Def.IsStructure).Id, defense.Defense!.Asset);
        // A structure's center may be blocked terrain: guard its reachable approach, including new members.
        Assert.Contains(commands, c => c.Kind == CommandKind.Guard && c.Cell == defense.Objective && c.Units.Contains(recruit.Id));
    }

    [Theory]
    [InlineData(false, "cargo_truck")]
    [InlineData(true, "technical")]
    public void PostureWeightsChooseBetweenLegalProductionGoals(bool combat, string expected)
    {
        var w = Arena.New(96);
        w.Spawn(1, "vehicle_garage", 20, 12);
        var rules = Rules();
        rules = rules with { Factions = [new AiFactionPlan { Faction = "resistance", Production =
            [new AiUnitGoal { Unit = "technical", Count = 6 }, new AiUnitGoal { Unit = "cargo_truck", Count = 2 }] }],
            Foundation = rules.Foundation! with { Postures = rules.Foundation!.Postures.Select(p => p with
            { CombatPriority = combat ? 100 : 1, LogisticsPriority = combat ? 1 : 100 }).ToArray() } };
        var ai = new PlayerAi(1, rules);
        Assert.Equal(expected, Assert.Single(ai.Think(w), c => c.Kind == CommandKind.Produce).Unit);
    }

    [Fact]
    public void HomeEmergencyBypassesPostureHoldAndRecallsWholeForce()
    {
        var w = Arena.New(96);
        w.Spawn(1, "supply_depot", 8, 8);
        var soldiers = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 14 + i, 18)).ToArray();
        w.Spawn(2, "Rifleman", 88, 88);
        w.Order(1, CommandKind.FormSquad, soldiers);
        w.Run(2);
        var rules = Rules();
        rules = rules with { Foundation = rules.Foundation! with { Postures = rules.Foundation!.Postures.Select(p => p with
        { DefenderPercent = 100 }).ToArray() } };
        var initial = new PlayerAi(1, rules);
        initial.Think(w);
        var intruder = w.Spawn(2, "Rifleman", 20, 22);
        w.Run(2);
        var state = initial.CaptureState();
        Assert.Single(state.Missions);
        // The unmodified format-11 defense assignment is a valid checkpoint, including its hold clock.
        var resumedDefense = new PlayerAi(1, rules);
        resumedDefense.RestoreState(w, PlayerAiStateLoader.Decode(PlayerAiStateLoader.Encode(state)));
        Assert.Equal(PlayerAiStateLoader.Encode(state), PlayerAiStateLoader.Encode(resumedDefense.CaptureState()));
        state = state with { Clock = state.Clock with { NextDecision = w.Tick, NextStrategy = 1000, PostureUntil = 1000 },
            Situation = state.Situation with { Posture = AiPosture.Attack },
            Missions = state.Missions.Select(m => m with { Kind = AiMissionKind.Assault, Phase = AiMissionPhase.Active, Defense = null }).ToArray() };
        var ai = new PlayerAi(1, rules);
        ai.RestoreState(w, state);
        var commands = ai.Think(w);
        Assert.Equal(AiPosture.Defend, ai.Situation.Posture);
        var defense = Assert.Single(ai.Missions);
        Assert.Equal(AiMissionKind.Defend, defense.Kind);
        Assert.Equal(soldiers.Select(u => u.Id), defense.Members);
        Assert.Contains(commands, c => c.Units.Count == 4 &&
            (c.Kind == CommandKind.AttackMove && c.Cell == Cell.Of(intruder.Pos) || c.Kind == CommandKind.Attack && c.Target == intruder.Id));
        Assert.Equal(intruder.Id, defense.Defense!.Threat);
    }
}
