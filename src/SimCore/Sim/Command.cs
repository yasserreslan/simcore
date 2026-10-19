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

    /// <summary>
    /// Walk the soldiers to the player's vehicle <see cref="Command.Target"/> and drive it (an empty
    /// driver seat) or ride in it (a free passenger seat), or into a garrisonable building.
    /// </summary>
    Enter,

    /// <summary>
    /// The drivers of the listed vehicles (or the listed drivers) get out. A listed passenger gets out
    /// behind its vehicle; a listed vehicle with passengers lets all of them out (its driver stays).
    /// A listed building lets everyone out; a listed soldier in a building leaves it.
    /// </summary>
    Exit,

    /// <summary>
    /// Use the ability of component <see cref="Command.Ability"/> on unit
    /// <see cref="Command.Target"/>, or on <see cref="Command.Cell"/> when Target is 0.
    /// </summary>
    UseAbility,

    /// <summary>
    /// Sandbox only: spawn the issuing player's preset or slotless archetype
    /// <see cref="Command.Unit"/> at <see cref="Command.Cell"/> (or the nearest
    /// free cell). Ignored unless the match's scenario is
    /// <see cref="Scenarios.Scenario.SandboxName"/>, so it can never change a
    /// real match. Vehicles spawn empty.
    /// </summary>
    SandboxSpawn,

    /// <summary>
    /// Fire at the ground at <see cref="Command.Cell"/> (force-fire): each unit
    /// walks into range and fires its first weapon that can hit ground targets
    /// at the cell's centre until it is given another order or runs dry.
    /// </summary>
    AttackGround,

    /// <summary>Set the listed units' <see cref="Unit.WeaponMode"/> to <see cref="Command.Mode"/>.</summary>
    SetWeaponMode,

    /// <summary>Launchers that deploy: stop and set up where they stand (they stay deployed).</summary>
    Deploy,

    /// <summary>Launchers that deploy: pack up (ends a salvo).</summary>
    Stow,

    /// <summary>Full cargo trucks: drive into the radius of the player's supply depot <see cref="Command.Target"/> and unload there.</summary>
    Unload,

    /// <summary>
    /// The listed workers place a construction site of archetype <see cref="Command.Unit"/> with
    /// <see cref="Command.Cell"/> its top-left cell (turned by <see cref="Command.Facing"/>) and build it.
    /// </summary>
    Build,

    /// <summary>The listed workers go on building site <see cref="Command.Target"/>.</summary>
    Resume,

    /// <summary>The owner cancels construction site <see cref="Command.Target"/> (refunding part of its cost).</summary>
    CancelBuild,

    /// <summary>The listed factories queue one <see cref="Command.Unit"/> (a preset or archetype ID).</summary>
    Produce,

    /// <summary>
    /// Factory <see cref="Command.Target"/> cancels an order: <see cref="Command.Index"/> -1 the last
    /// one; 0 to bays-1 the one in that bay; from bays on the waiting ones in order.
    /// </summary>
    CancelProduce,

    /// <summary>The listed factories send what they produce to <see cref="Command.Cell"/>, or next to unit <see cref="Command.Target"/>.</summary>
    SetRally,

    // Values are stable (replays store them): new kinds are appended with the next free value.
    /// <summary>Set the listed units' <see cref="Unit.Stance"/> to <see cref="Command.Stance"/>.</summary>
    SetStance = 19,

    /// <summary>The listed traps go off now (on demand, whatever their auto setting).</summary>
    Detonate = 20,

    /// <summary>The listed traps go off on their own when an enemy steps in (<see cref="Command.On"/> true) or only on demand (false).</summary>
    SetAutoDetonate = 21,

    /// <summary>
    /// The listed units underground travel to the player's tunnel entrance <see cref="Command.Target"/>
    /// and come out there (<see cref="Command.On"/> true: "exit here", "send to entrance") or wait
    /// below it (false). Units that can't reach it, or can't come out of it when told to, are skipped.
    /// </summary>
    TunnelGo = 22,

    /// <summary>
    /// Evacuate the networks of the listed tunnel entrances: every unit underground in them comes out
    /// by the nearest entrance it may come out of (trapped units stay).
    /// </summary>
    TunnelEvacuate = 23,

    /// <summary>
    /// Attack-move the units to <see cref="Command.Cell"/> (A + click on the ground): they move as for
    /// Move, stop to shoot spotted enemies in reach on the way, then carry on.
    /// </summary>
    AttackMove = 24,

    /// <summary>
    /// The units guard <see cref="Command.Cell"/> (several: each its formation slot there), or the
    /// player's unit <see cref="Command.Target"/> (Option+G): see <see cref="OrderKind.Guard"/>.
    /// </summary>
    Guard = 25,

    /// <summary>
    /// The units scatter (Option+S): each moves away from the group's centre by four times its
    /// radius, at least 2 cells (a unit on the centre goes the way it faces), then idles.
    /// </summary>
    Scatter = 26,
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

    /// <summary>The <see cref="CommandKind.Move"/> destination, the <see cref="CommandKind.AttackGround"/> target, or an ability's target cell.</summary>
    [JsonPropertyName("cell")]
    public Cell Cell { get; init; }

    /// <summary>The target unit's ID for Attack, Enter and UseAbility, or 0.</summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Target { get; init; }

    /// <summary>For <see cref="CommandKind.UseAbility"/>, the ID of the component whose ability to use.</summary>
    [JsonPropertyName("ability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ability { get; init; }

    /// <summary>For <see cref="CommandKind.SetWeaponMode"/>, the mode to set.</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Model.WeaponMode Mode { get; init; }

    /// <summary>For <see cref="CommandKind.SetStance"/>, the stance to set.</summary>
    [JsonPropertyName("stance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Stance Stance { get; init; }

    /// <summary>For <see cref="CommandKind.SandboxSpawn"/>, the preset name or slotless archetype ID to spawn.</summary>
    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    /// <summary>
    /// For <see cref="CommandKind.AttackGround"/>: add the point as a drone launcher's second
    /// target instead of replacing its first (Shift+G).
    /// </summary>
    [JsonPropertyName("queue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Queue { get; init; }

    /// <summary>For <see cref="CommandKind.CancelProduce"/>, which order (-1: the last).</summary>
    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Index { get; init; }

    /// <summary>For <see cref="CommandKind.Build"/>, the site's facing (0 = the front to the south; a quarter turn of a non-square footprint swaps its width and height, see World.TurnsAt).</summary>
    [JsonPropertyName("facing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Facing { get; init; }

    /// <summary>For <see cref="CommandKind.SetAutoDetonate"/>: on (true) or off; for <see cref="CommandKind.TunnelGo"/>: come out there (true) or wait below.</summary>
    [JsonPropertyName("on")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool On { get; init; }
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
}
