// A scripted player that issues random but legal orders. It stands in for
// real players when recording test matches. It reads the world the way a
// player would and only outputs commands, so a replay never needs it.
using SimCore.Model;
using SimCore.Rng;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Issues random orders for one player: move, attack, enter and exit vehicles, launch drones, plant traps.</summary>
/// <param name="player">The player the bot controls.</param>
/// <param name="seed">The seed for the bot's own generator.</param>
public sealed class Bot(byte player, ulong seed)
{
    private const int TrapReach = 5; // how far from a soldier, in cells, the bot plants its traps

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
        if (w.Tick % Every != 0 || w.Result.Over)
        {
            return [];
        }
        var own = new List<Unit>();
        var enemies = new List<uint>();
        var emptyVehicles = new List<Unit>();
        foreach (var u in w.Units)
        {
            if (u.Owner != Player)
            {
                if (World.IsVisibleTo(u, Player))
                {
                    enemies.Add(u.Id);
                }
                continue;
            }
            if (u.Def.Crew is not null && u.Driver == 0)
            {
                emptyVehicles.Add(u);
            }
            if (u.CanAct && u.Def.Strike is null && (u.Def.Speed.Raw > 0 || u.Def.Weapons.Count > 0))
            {
                own.Add(u);
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
        var sel = own.GetRange(0, n);
        var c = new Command { Tick = w.Tick + World.InputDelay, Player = Player, Units = [.. sel.Select(u => u.Id)] };

        var roll = _rng.Intn(100);
        var picked = roll switch
        {
            < 40 => null,
            < 70 => Attack(c, enemies),
            < 78 => Enter(c, sel, emptyVehicles),
            < 83 => Exit(c, sel),
            < 91 => Launch(w, c, sel, enemies),
            < 97 => Plant(w, c, sel),
            _ => c with { Kind = CommandKind.Stop },
        };
        return [picked ?? MoveSomewhere(w, c)];
    }

    private Command MoveSomewhere(World w, Command c)
    {
        var x = _rng.Intn(w.Grid.Width); // x is drawn before y
        var y = _rng.Intn(w.Grid.Height);
        return c with { Kind = CommandKind.Move, Cell = new Cell(x, y) };
    }

    private Command? Attack(Command c, List<uint> enemies) =>
        enemies.Count == 0 ? null : c with { Kind = CommandKind.Attack, Target = enemies[_rng.Intn(enemies.Count)] };

    // Enter sends the selected soldiers that can drive to one empty vehicle.
    private Command? Enter(Command c, List<Unit> sel, List<Unit> empty)
    {
        if (empty.Count == 0)
        {
            return null;
        }
        var v = empty[_rng.Intn(empty.Count)];
        var drivers = sel.Where(u => u.Def.Crew is null && u.Def.Tags.Matches(v.Def.Crew!.Tags)).Select(u => u.Id).ToList();
        return drivers.Count == 0 ? null : c with { Kind = CommandKind.Enter, Units = drivers, Target = v.Id };
    }

    // Exit gets the drivers out of the selected crewed vehicles.
    private static Command? Exit(Command c, List<Unit> sel)
    {
        var vehicles = sel.Where(u => u.Driver != 0).Select(u => u.Id).ToList();
        return vehicles.Count == 0 ? null : c with { Kind = CommandKind.Exit, Units = vehicles };
    }

    // Launch sends a drone from the selected units that have one at an
    // enemy, or at a random cell.
    private Command? Launch(World w, Command c, List<Unit> sel, List<uint> enemies)
    {
        var (ability, users) = Ready(w, sel, a => a.Effect is LaunchEffect);
        if (ability is null)
        {
            return null;
        }
        c = c with { Kind = CommandKind.UseAbility, Units = users, Ability = ability };
        if (enemies.Count > 0 && _rng.Intn(4) > 0)
        {
            return c with { Target = enemies[_rng.Intn(enemies.Count)] };
        }
        var x = _rng.Intn(w.Grid.Width); // x is drawn before y
        var y = _rng.Intn(w.Grid.Height);
        return c with { Cell = new Cell(x, y) };
    }

    // Plant has the first selected unit with a trap plant one near itself.
    private Command? Plant(World w, Command c, List<Unit> sel)
    {
        var (ability, users) = Ready(w, sel, a => a.CellOnly);
        if (ability is null)
        {
            return null;
        }
        var at = Cell.Of(w.Unit(users[0])!.Pos);
        var x = Math.Clamp(at.X + _rng.Range(-TrapReach, TrapReach), 0, w.Grid.Width - 1); // x is drawn before y
        var y = Math.Clamp(at.Y + _rng.Range(-TrapReach, TrapReach), 0, w.Grid.Height - 1);
        if (!w.Grid.Passable(new Cell(x, y)))
        {
            return null;
        }
        return c with { Kind = CommandKind.UseAbility, Units = [users[0]], Ability = ability, Cell = new Cell(x, y) };
    }

    // Ready returns the component ID of the first matching ability with
    // charges among the selected units, and every selected unit that has it ready.
    private static (string? Ability, List<uint> Users) Ready(World w, List<Unit> sel, Func<AbilityDef, bool> match)
    {
        int component = -1;
        var users = new List<uint>();
        foreach (var u in sel)
        {
            foreach (var a in u.Def.Abilities)
            {
                if (match(a) && u.Charges[a.Stock] > 0 && (component < 0 || component == a.Component))
                {
                    component = a.Component;
                    users.Add(u.Id);
                    break;
                }
            }
        }
        return component < 0 ? (null, users) : (w.Content.Components[component].Id, users);
    }
}
