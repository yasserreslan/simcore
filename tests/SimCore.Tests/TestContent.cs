using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// TestContent loads the MVP content and the battle lobby that ship with the engine.
internal static class TestContent
{
    private static readonly Lazy<ContentSet> MvpSet = new(() => ContentLoader.LoadDirectory(MvpDir));

    /// <summary>The repository's content/mvp directory.</summary>
    public static string MvpDir => Path.Combine(TestPaths.RepoRoot(), "content", "mvp");

    /// <summary>The compiled MVP content, loaded once.</summary>
    public static ContentSet Mvp => MvpSet.Value;

    /// <summary>The MVP content files, as they are on disk.</summary>
    public static List<ContentSource> MvpSources() =>
        [.. Directory.EnumerateFiles(MvpDir, "*.json", SearchOption.AllDirectories)
            .Select(p => new ContentSource(Path.GetRelativePath(MvpDir, p).Replace('\\', '/'), File.ReadAllText(p)))];

    /// <summary>The battle lobby (content/setups/battle.json), pinned to the MVP content and seed.</summary>
    public static MatchSetup Battle(ulong seed) =>
        Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "battle.json")).For(Mvp, seed);

    /// <summary>The Sentinel lobby (content/setups/sentinel.json: player 1 Sentinel, player 2 Resistance).</summary>
    public static MatchSetup Sentinel(ulong seed) =>
        Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(Mvp, seed);
}

// Arena builds small open test worlds with the battle lobby's presets and no
// starting army; tests spawn exactly the units they need.
internal static class Arena
{
    public static World New(int size = 32, ulong seed = 1, Func<MatchSetup, MatchSetup>? edit = null)
    {
        var setup = TestContent.Battle(seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        if (edit is not null)
        {
            setup = edit(setup);
        }
        return new World(new Grid(size, size), MatchCompiler.Compile(TestContent.Mvp, setup));
    }

    /// <summary>A world like <see cref="New"/> from the Sentinel lobby: player 1 Sentinel, player 2 Resistance.</summary>
    public static World NewSentinel(int size = 32, ulong seed = 1, Func<MatchSetup, MatchSetup>? edit = null)
    {
        var setup = TestContent.Sentinel(seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        if (edit is not null)
        {
            setup = edit(setup);
        }
        return new World(new Grid(size, size), MatchCompiler.Compile(TestContent.Mvp, setup));
    }

    /// <summary>Spawns player's preset or archetype name at cell (x, y).</summary>
    public static Unit Spawn(this World w, byte player, string name, int x, int y, int facing = Facing.North)
    {
        var def = w.Player(player)!.Setup.Find(w.Content, name) ?? throw new ArgumentException($"no def \"{name}\"");
        return w.Unit(w.Spawn(def, new Cell(x, y), facing))!;
    }

    /// <summary>Spawns a vehicle with a driver already in the seat.</summary>
    public static (Unit Vehicle, Unit Driver) SpawnCrewed(this World w, byte player, string vehicle, int x, int y, int facing = Facing.North)
    {
        var v = w.Spawn(player, vehicle, x, y, facing);
        var d = w.Spawn(player, "Rifleman", x, y, facing);
        World.Board(d, v);
        return (v, d);
    }

    public static void Run(this World w, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
        }
    }

    /// <summary>Issues a command for the next tick.</summary>
    public static void Order(this World w, byte player, CommandKind kind, IEnumerable<Unit> units,
        uint target = 0, Cell cell = default, string? ability = null) =>
        w.Issue(new Command
        {
            Tick = w.Tick, Player = player, Kind = kind, Units = [.. units.Select(u => u.Id)],
            Target = target, Cell = cell, Ability = ability,
        });

    /// <summary>The live units of an archetype.</summary>
    public static List<Unit> All(this World w, string archetype) =>
        [.. w.Units.Where(u => u.Def.Archetype.Id == archetype)];
}
