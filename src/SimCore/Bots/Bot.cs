// A scripted player that issues random orders. It stands in for real players
// when recording test matches. It reads the world the way a player would and
// only outputs commands, so a replay never needs it.
using SimCore.Rng;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Issues random orders for one player.</summary>
/// <param name="player">The player the bot controls.</param>
/// <param name="seed">The seed for the bot's own generator.</param>
public sealed class Bot(byte player, ulong seed)
{
    private Rand _rng = new(seed);

    /// <summary>The player the bot controls.</summary>
    public byte Player { get; } = player;

    /// <summary>Ticks between decisions; two seconds by default.</summary>
    public uint Every { get; init; } = 2 * World.TicksPerSecond;

    /// <summary>
    /// Returns the commands the bot issues this tick, scheduled
    /// <see cref="World.InputDelay"/> ticks ahead like a real player's.
    /// </summary>
    public IReadOnlyList<Command> Think(World w)
    {
        if (w.Tick % Every != 0)
        {
            return [];
        }
        var own = new List<uint>();
        var enemies = new List<uint>();
        foreach (var u in w.Units)
        {
            if (u.Owner == Player)
            {
                own.Add(u.Id);
            }
            else if (u.Owner != 0)
            {
                enemies.Add(u.Id);
            }
        }
        if (own.Count == 0)
        {
            return [];
        }

        // Pick 1–5 distinct units with a partial shuffle.
        var n = 1 + _rng.Intn(Math.Min(5, own.Count));
        for (var i = 0; i < n; i++)
        {
            var j = i + _rng.Intn(own.Count - i);
            (own[i], own[j]) = (own[j], own[i]);
        }
        var c = new Command { Tick = w.Tick + World.InputDelay, Player = Player, Units = own.GetRange(0, n) };

        var roll = _rng.Intn(10);
        if (roll < 5)
        {
            var x = _rng.Intn(w.Grid.Width); // x is drawn before y
            var y = _rng.Intn(w.Grid.Height);
            c = c with { Kind = CommandKind.Move, Cell = new Cell(x, y) };
        }
        else if (roll < 9 && enemies.Count > 0)
        {
            c = c with { Kind = CommandKind.Attack, Target = enemies[_rng.Intn(enemies.Count)] };
        }
        else
        {
            c = c with { Kind = CommandKind.Stop };
        }
        return [c];
    }
}
