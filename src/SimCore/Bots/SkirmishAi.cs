// A fog-honest skirmish test opponent: guard the base, then send normal timed waves.
// Visible enemies come first; otherwise remember seen structures and explore reachable
// region probes using only the side's own fog history. Idle wave members reserve separate
// probes, and explicitly attack passive structures once visible and in weapon range.
// Commands use the normal queue; replay playback never needs this AI's internal memory.
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>A fog-honest skirmish AI for one player; see the file comment for what it does.</summary>
/// <param name="player">The player the AI controls.</param>
public sealed partial class SkirmishAi(byte player)
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

    /// <summary>The AI's base: the centre of its structures at its last decision (its units' without any).</summary>
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
        var outposts = new List<Unit>();  // its structures: soldiers by any of them are at home too
        var guards = new List<Unit>();    // air defence: stays home on Guard
        var launchers = new List<Unit>(); // rocket trucks, missile and drone trucks: home, firing at what is spotted
        var fighters = new List<Unit>();  // everything else that moves and shoots: the waves
        long sx = 0, sy = 0, n = 0, ux = 0, uy = 0, un = 0;
        foreach (var u in w.Units)
        {
            if (u.Owner != Player)
            {
                if (u.Owner != 0 && World.IsVisibleTo(u, Player) && u.Alive)
                {
                    enemies.Add(u);
                }
                continue;
            }
            // The base: the structures' centre, or the units' while it has none.
            if (u.Def.IsStructure)
            {
                (sx, sy, n) = (sx + u.Pos.X.Raw, sy + u.Pos.Y.Raw, n + 1);
                outposts.Add(u);
            }
            else if (u.Inside == 0)
            {
                (ux, uy, un) = (ux + u.Pos.X.Raw, uy + u.Pos.Y.Raw, un + 1);
            }
            if (!u.Alive || !u.CanAct || u.Garrison != 0 || u.Def.IsStructure || u.Def.Speed.Raw == 0 || u.Def.Weapons.Count == 0 || u.Def.Strike is not null)
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
        if (n == 0)
        {
            (sx, sy, n) = (ux, uy, un);
        }
        if (n == 0)
        {
            return [];
        }
        _home = new Cell((int)(sx / n / Fixed.OneRaw), (int)(sy / n / Fixed.OneRaw));
        if (!_started)
        {
            _started = true;
            _nextWave = w.Tick + WaveEvery;
        }

        UpdateScouting(w, enemies);

        // Air defence and launchers stay on Guard (they engage on their own; launchers fire on our
        // orders); fighters are Aggressive.
        var hold = new List<uint>();
        foreach (var u in guards.Concat(launchers))
        {
            if (u.Stance != Stance.Guard)
            {
                hold.Add(u.Id);
            }
        }
        if (hold.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.SetStance, Units = hold, Stance = Stance.Guard });
        }
        var hunt = new List<uint>();
        foreach (var f in fighters)
        {
            if (f.Stance != Stance.Aggressive)
            {
                hunt.Add(f.Id);
            }
        }
        if (hunt.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.SetStance, Units = hunt, Stance = Stance.Aggressive });
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

        // Launchers (rocket trucks, drone trucks) fire at the ground under the nearest spotted enemy
        // they can reach (a salvo, or a drone, on where it was seen).
        foreach (var l in launchers)
        {
            if (l.Order.Kind != OrderKind.Idle || l.SalvoLeft > 0)
            {
                continue;
            }
            if (Nearest(enemies, l.Pos, e => e.Def.Layer == Layers.Ground && CanReach(l, e)) is { } t)
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.AttackGround, Units = [l.Id], Cell = Cell.Of(t.Pos) });
            }
        }

        // defenders and waved below list fighters in ID order (fighters is), so membership is a
        // binary search, not a scan per fighter (O(n log n) instead of O(n^2) at 2,500 units).

        // Hold the base: an enemy spotted near it is attacked by every free fighter at home.
        var intruder = Nearest(enemies, home, e => e.Def.Layer == Layers.Ground && Near(e.Pos, home, HoldRadius));
        var defenders = new List<uint>();
        if (intruder is not null)
        {
            foreach (var f in fighters)
            {
                if (Free(f) && Near(f.Pos, home, HoldRadius))
                {
                    defenders.Add(f.Id);
                }
            }
            if (defenders.Count > 0)
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Attack, Units = defenders, Target = intruder.Id });
            }
        }

        // Passive structures are deliberately excluded from the unit AI's auto-acquisition.
        // A wave that discovers one therefore needs an explicit, currently-visible attack.
        var assaulting = new List<uint>();
        foreach (var f in fighters)
        {
            if (!Searching(f) || f.Order.Kind is not (OrderKind.Idle or OrderKind.AttackMove) || defenders.BinarySearch(f.Id) >= 0) { continue; }
            if (Nearest(enemies, f.Pos, e => e.Def.IsStructure && e.Def.Layer == Layers.Ground && w.PickWeapon(f, e, true) >= 0) is { } structure)
            {
                assaulting.Add(f.Id);
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Attack, Units = [f.Id], Target = structure.Id });
            }
        }

        // The wave: every free fighter at home attack-moves to the spotted enemy nearest the base
        // (fighting what it meets on the way); otherwise a remembered structure, then an unseen
        // or least recently seen reachable region. Wave timing and membership stay unchanged.
        var waved = new List<uint>();
        if (w.Tick >= _nextWave)
        {
            _nextWave = w.Tick + WaveEvery;
            foreach (var f in fighters)
            {
                if (Free(f) && Near(f.Pos, home, HoldRadius) && defenders.BinarySearch(f.Id) < 0 && assaulting.BinarySearch(f.Id) < 0)
                {
                    waved.Add(f.Id);
                }
            }
            if (waved.Count > 0)
            {
                var to = Nearest(enemies, home, e => e.Def.Layer == Layers.Ground) is { } t ? Cell.Of(t.Pos)
                    : Objective(w, w.Unit(waved[0])!);
                TrackWave(waved);
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.AttackMove, Units = waved, Cell = to });
            }
        }

        // Fighters at home with nothing to do guard posts round the base (Aggressive: they hunt
        // what comes near, on the leash, and come back).
        var posts = new List<uint>();
        foreach (var f in fighters)
        {
            if (!Searching(f) && f.Order.Kind == OrderKind.Idle && f.Ai == AiState.None && Near(f.Pos, home, HoldRadius) && defenders.BinarySearch(f.Id) < 0 && waved.BinarySearch(f.Id) < 0 && assaulting.BinarySearch(f.Id) < 0)
            {
                posts.Add(f.Id);
            }
        }
        if (posts.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Guard, Units = posts, Cell = _home });
        }

        // Fighters out in the field: when idle (target lost or killed, or arrived), they attack-move
        // to a spotted enemy, remembered structure, or a separate reserved search probe.
        // Non-wave soldiers by a post (within a sixth of the hold radius) stay at home.
        foreach (var f in fighters)
        {
            if (f.Order.Kind != OrderKind.Idle || f.Ai != AiState.None || defenders.BinarySearch(f.Id) >= 0 || waved.BinarySearch(f.Id) >= 0 || assaulting.BinarySearch(f.Id) >= 0 ||
                (!Searching(f) && (Near(f.Pos, home, HoldRadius) || NearAny(outposts, f.Pos, HoldRadius / 6 + 3))))
            {
                continue;
            }
            if (Nearest(enemies, f.Pos, e => e.Def.Layer == Layers.Ground && Near(e.Pos, f.Pos, HoldRadius)) is { } t)
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.AttackMove, Units = [f.Id], Cell = Cell.Of(t.Pos) });
            }
            else
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.AttackMove,
                    Units = [f.Id], Cell = Objective(w, f) });
            }
        }
        return cmds;
    }

    // Free reports whether fighter f may be sent somewhere: idle or guarding a post, and not in the
    // middle of a chase.
    private static bool Free(Unit f) => (f.Order.Kind is OrderKind.Idle or OrderKind.Guard) && f.Ai == AiState.None;

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

    // NearAny reports whether p is within cells of any of the units (a loop: no closure per call).
    private static bool NearAny(List<Unit> units, Vec p, int cells)
    {
        foreach (var o in units)
        {
            if (Near(p, o.Pos, cells))
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
