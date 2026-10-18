// A simple skirmish opponent for testing fog of war: not smart, just honest. It holds its base,
// sends a wave at the enemy units its side spots about once a minute, keeps its air defence and
// launchers at home on Guard, and fires its rocket trucks and drones at what its side spots. It
// reads the world only through World.IsVisibleTo (with fog of war: what its side spots this
// tick), never an enemy it cannot see, and outputs commands for the normal queue, so a replay
// never needs it and it plays the same on every peer.
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>A fog-honest skirmish AI for one player; see the file comment for what it does.</summary>
/// <param name="player">The player the AI controls.</param>
public sealed class SkirmishAi(byte player)
{
    private Cell _home;
    private bool _started;
    private uint _nextWave;

    /// <summary>The player the AI controls.</summary>
    public byte Player { get; } = player;

    /// <summary>Ticks between decisions; one second by default.</summary>
    public uint Every { get; init; } = World.TicksPerSecond;

    /// <summary>Ticks between attack waves; 60 seconds by default (the first goes out after one wave period).</summary>
    public uint WaveEvery { get; init; } = 60 * World.TicksPerSecond;

    /// <summary>How far from its base (cells) the AI defends with everything it has at home.</summary>
    public int HoldRadius { get; init; } = 30;

    /// <summary>The AI's base: the centre of its structures when it first thought (its units' without any).</summary>
    public Cell Home => _home;

    /// <summary>The tick the next wave goes out.</summary>
    public uint NextWave => _nextWave;

    /// <summary>
    /// Returns the commands the AI issues this tick, scheduled <see cref="World.InputDelay"/> ticks
    /// ahead like a real player's.
    /// </summary>
    public IReadOnlyList<Command> Think(World w)
    {
        if (w.Tick % Every != 0 || w.Result.Over || w.Player(Player) is null)
        {
            return [];
        }
        var cmds = new List<Command>();
        var at = w.Tick + World.InputDelay;
        var launcherTag = w.Content.Tags.Contains("launcher") ? w.Content.TagsOf("launcher") : default;

        // What the AI knows: its own units, and the enemies its side sees now (ID order).
        var enemies = new List<Unit>();
        var guards = new List<Unit>();    // air defence: stays home on Guard
        var launchers = new List<Unit>(); // rocket trucks, missile and drone trucks: home, firing at what is spotted
        var fighters = new List<Unit>();  // everything else that moves and shoots: the waves
        long sx = 0, sy = 0, n = 0;
        foreach (var u in w.Units)
        {
            if (u.Owner != Player)
            {
                if (u.Owner != 0 && u.Alive && World.IsVisibleTo(u, Player))
                {
                    enemies.Add(u);
                }
                continue;
            }
            if (!_started)
            {
                // The base: the structures' centre (they weigh a thousand units each), or the units'.
                var weight = u.Def.IsStructure ? 1000L : 1L;
                sx += u.Pos.X.Raw * weight;
                sy += u.Pos.Y.Raw * weight;
                n += weight;
            }
            if (!u.Alive || !u.CanAct || u.Def.IsStructure || u.Def.Speed.Raw == 0 || u.Def.Weapons.Count == 0 || u.Def.Strike is not null)
            {
                continue;
            }
            if (u.Def.Archetype.AirVision.Raw > 0)
            {
                guards.Add(u);
            }
            else if (!launcherTag.IsEmpty && (u.Def.Tags.Bits & launcherTag.Bits) != 0)
            {
                launchers.Add(u);
            }
            else
            {
                fighters.Add(u);
            }
        }
        if (!_started)
        {
            if (n == 0)
            {
                return [];
            }
            _started = true;
            _home = new Cell((int)(sx / n / Fixed.OneRaw), (int)(sy / n / Fixed.OneRaw));
            _nextWave = w.Tick + WaveEvery;
            var hold = new List<uint>();
            foreach (var u in guards.Concat(launchers))
            {
                hold.Add(u.Id);
            }
            if (hold.Count > 0)
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.SetStance, Units = hold, Stance = Stance.Guard });
            }
        }
        var home = _home.Center();

        // Air defence and launchers stay home: back they go when idle and away.
        var goHome = new List<uint>();
        foreach (var u in guards.Concat(launchers))
        {
            if (u.Order.Kind == OrderKind.Idle && !Near(u.Pos, home, HoldRadius / 2) && u.SalvoLeft == 0)
            {
                goHome.Add(u.Id);
            }
        }
        if (goHome.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Move, Units = goHome, Cell = _home });
        }

        // Launchers (rocket trucks, drone trucks) fire at the nearest spotted enemy they can reach.
        foreach (var l in launchers)
        {
            if (l.Order.Kind != OrderKind.Idle || l.SalvoLeft > 0)
            {
                continue;
            }
            if (Nearest(enemies, l.Pos, e => CanReach(l, e)) is { } t)
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Attack, Units = [l.Id], Target = t.Id });
            }
        }

        // Hold the base: an enemy spotted near it is attacked by every idle fighter at home.
        var intruder = Nearest(enemies, home, e => e.Def.Layer == Layers.Ground && Near(e.Pos, home, HoldRadius));
        var defenders = new List<uint>();
        if (intruder is not null)
        {
            foreach (var f in fighters)
            {
                if (f.Order.Kind == OrderKind.Idle && Near(f.Pos, home, HoldRadius))
                {
                    defenders.Add(f.Id);
                }
            }
            if (defenders.Count > 0)
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Attack, Units = defenders, Target = intruder.Id });
            }
        }

        // The wave: every idle fighter at home goes for the spotted enemy nearest the base; with
        // nothing spotted it advances toward the far side of the map (where an enemy may be) to look.
        if (w.Tick >= _nextWave)
        {
            _nextWave = w.Tick + WaveEvery;
            var wave = new List<uint>();
            foreach (var f in fighters)
            {
                if (f.Order.Kind == OrderKind.Idle && Near(f.Pos, home, HoldRadius) && !defenders.Contains(f.Id))
                {
                    wave.Add(f.Id);
                }
            }
            if (wave.Count > 0)
            {
                if (Nearest(enemies, home, e => e.Def.Layer == Layers.Ground) is { } t)
                {
                    cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Attack, Units = wave, Target = t.Id });
                }
                else
                {
                    var far = new Cell(Math.Clamp(w.Grid.Width - 1 - _home.X, 0, w.Grid.Width - 1), Math.Clamp(w.Grid.Height - 1 - _home.Y, 0, w.Grid.Height - 1));
                    cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Move, Units = wave, Cell = far });
                }
            }
        }

        // Fighters out in the field: when idle (target lost or killed, or arrived), they go for a
        // spotted enemy near them, or come home.
        var back = new List<uint>();
        foreach (var f in fighters)
        {
            if (f.Order.Kind != OrderKind.Idle || Near(f.Pos, home, HoldRadius))
            {
                continue;
            }
            if (Nearest(enemies, f.Pos, e => e.Def.Layer == Layers.Ground && Near(e.Pos, f.Pos, HoldRadius)) is { } t)
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Attack, Units = [f.Id], Target = t.Id });
            }
            else
            {
                back.Add(f.Id);
            }
        }
        if (back.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Move, Units = back, Cell = _home });
        }
        return cmds;
    }

    // CanReach reports whether launcher l can fire at t from where it stands: a weapon with ammo
    // that hits t's layer, with t inside its range and beyond its minimum range.
    private static bool CanReach(Unit l, Unit t)
    {
        var d2 = (t.Pos - l.Pos).LenSq();
        foreach (var wpn in l.Def.Weapons)
        {
            if ((wpn.Effect.Targets & t.Def.Layer) == 0 || (wpn.Stock >= 0 && l.Charges[wpn.Stock] <= 0))
            {
                continue;
            }
            var max = wpn.Range.Raw;
            var min = wpn.Effect.MinRange.Raw;
            if (d2 <= max * max && d2 >= min * min)
            {
                return true;
            }
        }
        return false;
    }

    private static bool Near(Vec a, Vec b, int cells)
    {
        var r = (long)cells * Fixed.OneRaw;
        return (a - b).LenSq() <= r * r;
    }

    // Nearest returns the unit nearest p that passes the filter, ties to the lower ID (the list is in ID order).
    private static Unit? Nearest(List<Unit> units, Vec p, Func<Unit, bool> ok)
    {
        Unit? best = null;
        long bestD = 0;
        foreach (var u in units)
        {
            if (!ok(u))
            {
                continue;
            }
            var d = (u.Pos - p).LenSq();
            if (best is null || d < bestD)
            {
                (best, bestD) = (u, d);
            }
        }
        return best;
    }
}
