using System.Text.Json;
using SimCore.Bots;
using SimCore.Content;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

public sealed class PlayerAiFoundationTests
{
    private static PlayerAiRules Rules()
    {
        var rules = PlayerAiLoader.Load(Path.Combine(TestPaths.RepoRoot(), "content", "ai", "player.json"));
        return rules with { DecisionTicks = 1, HomeGuards = 0, ScoutMissions = 0, Factions = [], FormSquads = false,
            Foundation = rules.Foundation! with { Composition = [], Aircraft = false, Transports = false } };
    }

    private static World Game()
    {
        var w = Arena.New(96);
        w.Spawn(1, "supply_depot", 8, 8);
        w.Spawn(2, "supply_depot", 80, 80);
        w.Spawn(2, "Rifleman", 88, 88);
        return w;
    }

    private static PlayerAi WithJob(World w, PlayerAiRules rules, Unit actor, AiSpecialistKind kind,
        uint target = 0, uint until = 100, IReadOnlyList<uint>? passengers = null)
    {
        var initial = new PlayerAi(1, rules);
        initial.Think(w);
        var state = initial.CaptureState();
        state = state with { Clock = state.Clock with { NextDecision = w.Tick, JobSerial = 1 }, Missions = [], Operations = [],
            Specialists = [new AiSpecialistMemory(1, kind, actor.Id, target, new Cell(70, 70), default,
                Cell.Of(actor.Pos), w.Tick, until, w.Tick, 0, 0, 0, -1, false, passengers ?? [])] };
        var ai = new PlayerAi(1, rules);
        ai.RestoreState(w, PlayerAiStateLoader.Decode(PlayerAiStateLoader.Encode(state)));
        return ai;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredTransportCancelsPendingBoardingAndRetainsReservations(bool partial)
    {
        var w = Game();
        var (carrier, _) = w.SpawnCrewed(1, "infantry_carrier", 30, 30);
        var waiting = w.Spawn(1, "Rifleman", 15, 15);
        var passengers = new List<uint> { waiting.Id };
        if (partial)
        {
            var rider = w.Spawn(1, "Rifleman", 30, 30);
            World.Ride(rider, carrier);
            passengers.Add(rider.Id);
        }
        w.Issue(new Command { Tick = w.Tick + World.InputDelay, Player = 1, Kind = CommandKind.Enter,
            Units = [waiting.Id], Target = carrier.Id });
        var ai = WithJob(w, Rules(), carrier, AiSpecialistKind.Transport, until: w.Tick, passengers: passengers);
        var commands = ai.Think(w);
        Assert.Contains(commands, c => c.Kind == CommandKind.Stop && c.Units.Contains(waiting.Id));
        Assert.Contains(commands, c => c.Kind == CommandKind.Stop && c.Units.Contains(carrier.Id));
        Assert.Contains(ai.SpecialistJobs, j => j.Passengers.Contains(waiting.Id));
        foreach (var c in commands) { w.Issue(c); }
        for (var i = 0; i < 60; i++)
        {
            w.Step();
            foreach (var c in ai.Think(w)) { w.Issue(c); }
        }
        Assert.NotEqual(OrderKind.Enter, waiting.Order.Kind);
        Assert.All(passengers, id => Assert.Equal(0u, w.Unit(id)!.Inside));
        Assert.DoesNotContain(ai.SpecialistJobs, j => j.Kind == AiSpecialistKind.Transport);
        Assert.NotEqual(0u, carrier.Driver);
    }

    [Fact]
    public void CompletedRepairStopsTheOldFollowOrderBeforeReleasingTheWorker()
    {
        var w = Game();
        var worker = w.Spawn(1, "worker", 15, 15);
        var (patient, _) = w.SpawnCrewed(1, "technical", 40, 40);
        worker.Order = new Order(OrderKind.Move, new Cell(40, 40), 0, 0);
        var ai = WithJob(w, Rules(), worker, AiSpecialistKind.Repair, patient.Id);
        Assert.Contains(ai.Think(w), c => c.Kind == CommandKind.Stop && c.Units.Contains(worker.Id));
        Assert.DoesNotContain(ai.SpecialistJobs, j => j.Unit == worker.Id);
    }

    [Fact]
    public void ReinforcementsReceiveTheExistingAssemblyOrder()
    {
        var w = Game();
        w.Spawn(1, "Rifleman", 15, 15);
        var ai = new PlayerAi(1, Rules() with { ForceMinimum = 6, AssemblyTicks = 100 });
        for (var i = 0; i < 5; i++)
        {
            foreach (var c in ai.Think(w)) { w.Issue(c); }
            w.Step();
        }
        var reinforcement = w.Spawn(1, "Rifleman", 20, 20);
        Assert.Contains(ai.Think(w), c => c.Kind == CommandKind.Guard && c.Units.Contains(reinforcement.Id));
    }

    [Fact]
    public void ServiceUsesGarageDoorsRatherThanTheBlockedBuildingCentre()
    {
        var w = Arena.NewSentinel(96);
        var garage = w.Spawn(1, "military_garage", 30, 12);
        var (truck, _) = w.SpawnCrewed(1, "sentinel_cargo_truck", 20, 35);
        truck.HP = truck.Def.MaxHp / 2;
        var ai = new PlayerAi(1, Rules());
        Assert.Contains(ai.Think(w), c => c.Kind == CommandKind.Service && c.Target == garage.Id && c.Units.Contains(truck.Id));
    }

    [Fact]
    public void CargoInFlightCannotAlsoBeAssignedToGarageService()
    {
        var w = Arena.NewSentinel(96);
        w.Spawn(1, "supply_depot", 8, 8).Stock = 1;
        w.Spawn(2, "Rifleman", 88, 88);
        var (truck, _) = w.SpawnCrewed(1, "sentinel_cargo_truck", 20, 35);
        w.Spawn(1, "military_garage", 30, 12);
        var ai = new PlayerAi(1, Rules() with { ReachSearchCells = 32 });
        var first = ai.Think(w);
        Assert.Contains(first, c => c.Kind == CommandKind.Unload && c.Units.Contains(truck.Id));
        foreach (var c in first) { w.Issue(c); }
        truck.HP = truck.Def.MaxHp / 2;
        w.Step(); // delivery input has not executed yet; the live truck still appears free
        Assert.DoesNotContain(ai.Think(w), c => c.Kind == CommandKind.Service && c.Units.Contains(truck.Id));
        Assert.DoesNotContain(ai.SpecialistJobs, j => j.Unit == truck.Id);
    }

    [Fact]
    public void FoundationCheckpointContinuesExactlyWithPendingCommands()
    {
        var content = TestContent.Mvp;
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "player_ai.json")).For(content, 17);
        var rules = PlayerAiLoader.Load(Path.Combine(TestPaths.RepoRoot(), "content", "ai", "player.json"));
        var original = Replay.NewWorld(content, setup);
        var ai = new PlayerAi(1, rules);
        var commands = new List<Command>();
        for (var i = 0; i < 81; i++)
        {
            foreach (var c in ai.Think(original)) { commands.Add(original.Issue(c)); }
            original.Step();
        }
        var state = PlayerAiStateLoader.Decode(PlayerAiStateLoader.Encode(ai.CaptureState()));
        var replay = new Replay { Setup = setup, Ticks = original.Tick, Commands = commands };
        var resumed = replay.Start(content);
        while (resumed.Tick < original.Tick) { resumed.Step(); }
        var restored = new PlayerAi(1, rules);
        restored.RestoreState(resumed, state);
        for (var i = 0; i < 120; i++)
        {
            var a = ai.Think(original); var b = restored.Think(resumed);
            Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
            foreach (var c in a) { original.Issue(c); }
            foreach (var c in b) { resumed.Issue(c); }
            original.Step(); resumed.Step();
            Assert.Equal(original.Hash(), resumed.Hash());
            Assert.Equal(PlayerAiStateLoader.Encode(ai.CaptureState()), PlayerAiStateLoader.Encode(restored.CaptureState()));
        }
    }
}
