// SimRun runs a match headless and prints the map as text.
//
//   dotnet run --project src/SimRun                            # bot vs bot, 10 minutes
//   dotnet run --project src/SimRun -- -every 20 -ticks 2400   # watch every second for 2 minutes
//   dotnet run --project src/SimRun -- -save match.json        # also save the replay
//   dotnet run --project src/SimRun -- -replay match.json      # play a saved replay
//
// Player 1 units are uppercase, player 2 lowercase: T tank, R rifleman,
// K rocket trooper, B buggy, A artillery. # is rock.
using System.Globalization;
using System.Text;
using SimCore.Replays;
using SimCore.Sim;

namespace SimRun;

internal static class Program
{
    private const string Usage = """
        Usage of simrun:
          -every uint
                print the map every N ticks (0 = final only) (default 1200)
          -replay string
                play this replay file instead of recording bots
          -save string
                write the recorded replay to this file
          -seed uint
                match seed (default 42)
          -ticks uint
                ticks to simulate (default 12000)
        """;

    private static int Main(string[] args)
    {
        ulong seed = 42;
        uint ticks = 10 * 60 * World.TicksPerSecond;
        uint every = 60 * World.TicksPerSecond;
        string save = "", load = "";
        try
        {
            foreach (var (name, value) in ParseFlags(args))
            {
                switch (name)
                {
                    case "seed": seed = ulong.Parse(value, CultureInfo.InvariantCulture); break;
                    case "ticks": ticks = uint.Parse(value, CultureInfo.InvariantCulture); break;
                    case "every": every = uint.Parse(value, CultureInfo.InvariantCulture); break;
                    case "save": save = value; break;
                    case "replay": load = value; break;
                    default: throw new FormatException($"flag provided but not defined: -{name}");
                }
            }
        }
        catch (HelpRequestedException)
        {
            Console.Error.WriteLine(Usage);
            return 0;
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            Run(seed, ticks, every, save, load);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                      or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"simrun: {e.Message}");
            return 1;
        }
    }

    private sealed class HelpRequestedException : Exception;

    // ParseFlags accepts the same forms as Go's flag package: -name value,
    // -name=value, and the same with two dashes.
    private static IEnumerable<(string Name, string Value)> ParseFlags(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith('-') || a == "-" || a == "--")
            {
                break;
            }
            var name = a.TrimStart('-');
            if (name is "h" or "help")
            {
                throw new HelpRequestedException();
            }
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                yield return (name[..eq], name[(eq + 1)..]);
                continue;
            }
            if (i + 1 >= args.Length)
            {
                throw new FormatException($"flag needs an argument: -{name}");
            }
            yield return (name, args[++i]);
        }
    }

    private static void Run(ulong seed, uint ticks, uint every, string save, string load)
    {
        var r = load != "" ? Replay.Load(load) : Replay.Record("skirmish", seed, ticks).Replay;
        if (save != "")
        {
            r.Save(save);
        }

        // Play the replay tick by tick so we can print along the way.
        var w = r.Start();
        var stdout = Console.Out;
        while (w.Tick < r.Ticks)
        {
            w.Step();
            if (every > 0 && w.Tick % every == 0 && w.Tick != r.Ticks)
            {
                stdout.Write(Render(w));
            }
        }
        stdout.Write(Render(w));
        stdout.Flush();
    }

    private static string Render(World w)
    {
        var b = new StringBuilder();
        var counts = new Dictionary<byte, int>();
        var g = w.Grid;
        var glyphs = new char[g.Width * g.Height];
        foreach (var u in w.Units)
        {
            counts[u.Owner] = counts.GetValueOrDefault(u.Owner) + 1;
            var c = Cell.Of(u.Pos);
            if (!g.Contains(c))
            {
                continue;
            }
            var glyph = u.Def.Glyph;
            if (u.Owner != 1)
            {
                glyph = char.ToLowerInvariant(glyph);
            }
            var i = c.Y * g.Width + c.X;
            if (glyphs[i] == 0)
            {
                glyphs[i] = glyph;
            }
        }

        var secs = w.Tick / World.TicksPerSecond;
        b.Append(CultureInfo.InvariantCulture,
            $"\ntick {w.Tick} ({secs / 60}:{secs % 60:00})  P1 {counts.GetValueOrDefault((byte)1)} units  P2 {counts.GetValueOrDefault((byte)2)} units  hash {w.Hash():x16}\n");
        for (var y = 0; y < g.Height; y++)
        {
            for (var x = 0; x < g.Width; x++)
            {
                var glyph = glyphs[y * g.Width + x];
                if (glyph != 0)
                {
                    b.Append(glyph);
                }
                else
                {
                    b.Append(g.Passable(new Cell(x, y)) ? '.' : '#');
                }
            }
            b.Append('\n');
        }
        return b.ToString();
    }
}
