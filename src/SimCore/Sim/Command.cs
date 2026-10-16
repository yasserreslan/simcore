using System.Text.Json.Serialization;

namespace SimCore.Sim;

/// <summary>The type of a player command.</summary>
public enum CommandKind : byte
{
    /// <summary>Drive the units to <see cref="Command.Cell"/>.</summary>
    Move = 1,

    /// <summary>Attack <see cref="Command.Target"/>.</summary>
    Attack,

    /// <summary>Drop the current order.</summary>
    Stop,
}

/// <summary>
/// A player's order, scheduled for a tick. Commands are the only way to change
/// the world, and the only thing peers exchange in multiplayer.
/// </summary>
public sealed record Command
{
    /// <summary>The tick the command runs on.</summary>
    [JsonPropertyName("tick")]
    public uint Tick { get; init; }

    /// <summary>The issuing player.</summary>
    [JsonPropertyName("player")]
    public byte Player { get; init; }

    /// <summary>The per-player order; assigned by <see cref="World.Issue"/> when 0.</summary>
    [JsonPropertyName("seq")]
    public uint Seq { get; init; }

    /// <summary>What to do.</summary>
    [JsonPropertyName("kind")]
    public CommandKind Kind { get; init; }

    /// <summary>The IDs of the units to command.</summary>
    [JsonPropertyName("units")]
    public IReadOnlyList<uint> Units { get; init; } = [];

    /// <summary>The <see cref="CommandKind.Move"/> destination.</summary>
    [JsonPropertyName("cell")]
    public Cell Cell { get; init; }

    /// <summary>The <see cref="CommandKind.Attack"/> target's ID.</summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Target { get; init; }
}

/// <summary>Thrown by <see cref="World.Issue"/> for a command that can never run.</summary>
public class CommandRejectedException(string message) : InvalidOperationException(message);

/// <summary>The command is scheduled for a past tick.</summary>
public sealed class LateCommandException(uint tick, uint now)
    : CommandRejectedException($"sim: command scheduled for a past tick: tick {tick}, now {now}");

/// <summary>The command's player is not in the match.</summary>
public sealed class UnknownPlayerException(byte player)
    : CommandRejectedException($"sim: unknown player: {player}");

public sealed partial class World
{
    /// <summary>
    /// Schedules a command and returns it with its sequence number set.
    /// Commands for the same tick run ordered by player, then sequence, no
    /// matter what order they arrived in.
    /// </summary>
    /// <exception cref="LateCommandException">The tick has already run.</exception>
    /// <exception cref="UnknownPlayerException">The player is not in the match.</exception>
    public Command Issue(Command c)
    {
        if (c.Tick < Tick)
        {
            throw new LateCommandException(c.Tick, Tick);
        }
        if (c.Player == 0 || c.Player >= _nextSeq.Length)
        {
            throw new UnknownPlayerException(c.Player);
        }
        uint seq = c.Seq;
        if (seq == 0)
        {
            _nextSeq[c.Player]++;
            seq = _nextSeq[c.Player];
        }
        else
        {
            _nextSeq[c.Player] = Math.Max(_nextSeq[c.Player], seq);
        }
        c = c with { Seq = seq, Units = [.. c.Units ?? []] };
        _pending.Insert(LowerBound(c), c);
        return c;
    }

    // LowerBound returns the index of the first pending command not ordered
    // before c.
    private int LowerBound(Command c)
    {
        int lo = 0, hi = _pending.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (CompareCommands(_pending[mid], c) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private static int CompareCommands(Command a, Command b)
    {
        var d = a.Tick.CompareTo(b.Tick);
        if (d != 0)
        {
            return d;
        }
        d = a.Player.CompareTo(b.Player);
        return d != 0 ? d : a.Seq.CompareTo(b.Seq);
    }

    private void ApplyCommands()
    {
        var n = 0;
        while (n < _pending.Count && _pending[n].Tick == Tick)
        {
            n++;
        }
        for (var i = 0; i < n; i++)
        {
            Apply(_pending[i]);
        }
        _pending.RemoveRange(0, n);
    }

    // Apply runs one command. Units the player doesn't own, dead units and
    // invalid targets are skipped, the same way on every machine.
    private void Apply(Command c)
    {
        switch (c.Kind)
        {
            case CommandKind.Move:
                var dest = 0;
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is null || u.Owner != c.Player || u.Def.Speed.Raw == 0)
                    {
                        continue;
                    }
                    // Each unit in the group gets its own cell around the target.
                    var ok = Grid.TrySpiral(c.Cell, dest, out var cell);
                    dest++;
                    if (!ok)
                    {
                        continue;
                    }
                    u.Order = new Order(OrderKind.Move, cell, 0);
                    SetPath(u, cell);
                }
                break;
            case CommandKind.Attack:
                var target = Unit(c.Target);
                if (target is null || target.Owner == c.Player)
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is null || u.Owner != c.Player || u.Def.Weapon is null)
                    {
                        continue;
                    }
                    u.Order = new Order(OrderKind.Attack, default, target.Id);
                    u.ClearPath();
                    u.RepathAt = Tick;
                }
                break;
            case CommandKind.Stop:
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is not null && u.Owner == c.Player)
                    {
                        u.Order = default;
                        u.ClearPath();
                    }
                }
                break;
        }
    }
}
