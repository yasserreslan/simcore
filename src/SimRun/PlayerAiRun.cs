using System.Text.Json;
using SimCore.Bots;
using SimCore.Content;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

// Host integration example: only commands are shared/replayed. Bot memory stays outside World.
internal static class PlayerAiRun
{
    public static int Run(string contentDir, string setupPath, string policyPath, ulong seed, uint ticks, bool fog, string save, string trace,
        string statePath = "", string resumePath = "", string replayPath = "")
    {
        if (ticks > Replay.MaxTicks) { throw new ArgumentException("Player AI run exceeds replay tick limit."); }
        var content = ContentLoader.LoadDirectory(contentDir);
        var recorded = resumePath == "" ? null : replayPath != "" ? Replay.Load(replayPath) : throw new ArgumentException("AI resume needs a matching replay.");
        var setup = recorded?.Setup ?? Replay.LoadSetup(setupPath).For(content, seed);
        seed = setup.Seed;
        if (fog && recorded is null) { setup = setup with { Fog = true }; }
        if (setup.Terrain is not null) { throw new ArgumentException("The player AI runner currently takes flat lobby setups; hosts may supply terrain directly to PlayerAi.Think."); }
        var rules = PlayerAiLoader.Load(policyPath);
        var w = recorded?.Start(content) ?? Replay.NewWorld(content, setup);
        var bots = w.Players.Select(p => new PlayerAi(p.Id, rules)).ToArray();
        var commands = recorded?.Commands.ToList() ?? [];
        if (recorded is not null)
        {
            var states = PlayerAiStateLoader.LoadAll(resumePath);
            if (states.Count != bots.Length || states.Count == 0 || states.Any(s => s.Identity.Tick != states[0].Identity.Tick) ||
                states[0].Identity.Tick != recorded.Ticks || commands.Any(c => c.Tick > recorded.Ticks + World.InputDelay))
            { throw new ArgumentException("Resume needs all players at the end of the matching checkpoint replay, including pending commands."); }
            while (w.Tick < recorded.Ticks) { w.Step(); }
            foreach (var bot in bots)
            {
                var state = states.Single(s => s.Identity.Player == bot.Player);
                bot.RestoreState(w, state);
            }
        }
        while (w.Tick < ticks && !w.Result.Over)
        {
            foreach (var bot in bots)
            foreach (var c in bot.Think(w)) { commands.Add(w.Issue(c)); }
            w.Step();
        }
        // Retain commands in flight: exact planner continuation needs the same pending input queue.
        var replay = new Replay { Setup = setup, Ticks = w.Tick, Commands = commands.ToArray() };
        if (save != "") { replay.Save(save); }
        if (statePath != "") { PlayerAiStateLoader.SaveAll(statePath, bots); }
        var summary = new
        {
            policyVersion = rules.Version, seed, ticks = w.Tick, result = w.Result,
            commands = replay.Commands.Count, hash = w.Hash().ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            players = bots.Select(b => new { player = b.Player, home = b.Home, situation = b.Situation, missions = b.Missions,
                operations = b.Operations, specialists = b.SpecialistJobs, objectives = b.Objectives, outcomes = b.Outcomes, decisions = b.Decisions }),
        };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        if (trace != "") { File.WriteAllText(trace, json); }
        Console.WriteLine(json);
        return 0;
    }
}
