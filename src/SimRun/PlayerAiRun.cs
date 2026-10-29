using System.Text.Json;
using SimCore.Bots;
using SimCore.Content;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

// Host integration example: only commands are shared/replayed. Bot memory stays outside World.
internal static class PlayerAiRun
{
    public static int Run(string contentDir, string setupPath, string policyPath, ulong seed, uint ticks, bool fog, string save, string trace)
    {
        if (ticks > Replay.MaxTicks) { throw new ArgumentException("Player AI run exceeds replay tick limit."); }
        var content = ContentLoader.LoadDirectory(contentDir);
        var setup = Replay.LoadSetup(setupPath).For(content, seed);
        if (fog) { setup = setup with { Fog = true }; }
        if (setup.Terrain is not null) { throw new ArgumentException("The player AI runner currently takes flat lobby setups; hosts may supply terrain directly to PlayerAi.Think."); }
        var rules = PlayerAiLoader.Load(policyPath);
        var w = Replay.NewWorld(content, setup);
        var bots = w.Players.Select(p => new PlayerAi(p.Id, rules)).ToArray();
        var commands = new List<Command>();
        while (w.Tick < ticks && !w.Result.Over)
        {
            foreach (var bot in bots)
            foreach (var c in bot.Think(w)) { commands.Add(w.Issue(c)); }
            w.Step();
        }
        // Commands scheduled after an early victory are harmless, but fall outside a finished replay.
        var replay = new Replay { Setup = setup, Ticks = w.Tick, Commands = commands.Where(c => c.Tick < w.Tick).ToArray() };
        if (save != "") { replay.Save(save); }
        var summary = new
        {
            policyVersion = rules.Version, seed, ticks = w.Tick, result = w.Result,
            commands = replay.Commands.Count, hash = w.Hash().ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            players = bots.Select(b => new { player = b.Player, home = b.Home, missions = b.Missions, decisions = b.Decisions }),
        };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        if (trace != "") { File.WriteAllText(trace, json); }
        Console.WriteLine(json);
        return 0;
    }
}
