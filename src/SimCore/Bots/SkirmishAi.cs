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
        var squads = UseSquads && w.Content.Squad is not null; // squad members are the squads' part's (SkirmishSquads.cs)

        // What the AI knows: its own units, and the enemies its side sees now (ID order).
        var enemies = new List<Unit>();
        var outposts = new List<Unit>();  // its structures: soldiers by any of them are at home too
        var guards = new List<Unit>();    // air defence: stays home on Guard
        var launchers = new List<Unit>(); // rocket trucks, missile and drone trucks: home, firing at what is spotted
        var fighters = new List<Unit>();  // everything else that moves and shoots: the waves
        var riders = new List<uint>();    // INF-MOVE (1214): soldiers riding in a vehicle (or driving an unarmed one): out to fight in the endgame
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
            else if (squads && _endgame && u.Alive && u.Def.Crew is null && u.Def.Weapons.Count > 0 && GroundGun(u) && w.Unit(u.Inside) is { } v &&
                !v.Def.IsStructure && (v.Driver != u.Id || v.Def.Weapons.Count == 0))
            {
                riders.Add(u.Id);
            }
            if (!u.Alive || !u.CanAct || u.Garrison != 0 || u.Def.IsStructure || u.Def.Speed.Raw == 0 || u.Def.Weapons.Count == 0 || u.Def.Strike is not null ||
                (squads && u.Squad != 0))
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
        _outposts.Clear();
        _outposts.AddRange(outposts);
        if (squads)
        {
            SquadsThink(w, cmds, at, enemies, fighters, guards);
        }

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

        // Air defence and launchers stay home: back they go when idle and away. (In the endgame they stay out, unless empty: back to
        // the depot to reload, and the endgame wave sends them out again once they hold rockets; INF-E1b-2, decision 1201: an empty rocket truck had stood idle
        // at the enemy's base for good, and the match never ended.)
        // INF-MOVE, decision 1214: a soldier who can shoot only aircraft (the Igla) goes home in the endgame too, and is not sent out
        // with the last wave: the lone Igla had stood idle at the enemy's base with nothing on the ground to shoot, the match never
        // ending. And a soldier riding in a vehicle, or driving one with no weapon, gets out to fight: player 1's last soldier
        // had sat at the wheel of a cargo truck at home.
        if (riders.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Exit, Units = riders });
        }
        var goHome = new List<uint>();
        foreach (var u in guards.Concat(launchers))
        {
            if (u.Order.Kind == OrderKind.Idle && !Near(u.Pos, home, HoldRadius / 2) && u.SalvoLeft == 0 && !(squads && _endgame && !Empty(u) && GroundGun(u)))
            {
                goHome.Add(u.Id);
            }
        }
        if (goHome.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Move, Units = goHome, Cell = Park(w, w.Unit(goHome[0])!.Pos) });
        }
        EndgameVehicles(w, cmds, at, enemies, fighters, guards, launchers); // INF-E1b-4 part 5 (decision 1282)

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
            if (squads)
            {
                WaveSquadsDefend(w, cmds, at, intruder);
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
            Cell? to = null;
            Cell Target(Unit first) => to ??= Nearest(enemies, home, e => e.Def.Layer == Layers.Ground) is { } t ? Cell.Of(t.Pos) : Objective(w, first);
            if (waved.Count > 0)
            {
                TrackWave(waved);
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.AttackMove, Units = waved, Cell = Target(w.Unit(waved[0])!) });
            }
            if (squads)
            {
                if (WaveSquads(w, cmds, at, Target, fighters.Count > 0)) // the wave squads go with them, marching
                {
                    // Nothing else left to send: the air defence and the launchers go too (an endgame, squads only).
                    var last = new List<uint>();
                    Unit? lead = null;
                    foreach (var u in guards.Concat(launchers))
                    {
                        if (u.Order.Kind == OrderKind.Idle && u.SalvoLeft == 0 && !Empty(u) && GroundGun(u)) // an empty one reloads at home first
                        {
                            lead ??= u;
                            last.Add(u.Id);
                        }
                    }
                    if (lead is not null)
                    {
                        cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.AttackMove, Units = last, Cell = Target(lead) });
                    }
                }
            }
        }
        if (squads)
        {
            WaveSquadsOut(w, cmds, at, enemies);
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
                // INF-E1b-10 (row 1435, the full match's stall): the arm in his hands can't hit it but another of his can (an Igla man's rifle,
                // his sidearm): an explicit attack, which draws it (ArmsSystem's OrderNeedsSidearm). An attack-move had left p1's last
                // soldier lying idle by p2's last vehicle, his Igla in his hands, and the match never ended.
                cmds.Add(w.PickWeapon(f, t, false) < 0 && f.Def.Weapons.Any(x => World.CanHit(x, t) && World.HasAmmo(f, x))
                    ? new Command { Tick = at, Player = Player, Kind = CommandKind.Attack, Units = [f.Id], Target = t.Id }
                    : new Command { Tick = at, Player = Player, Kind = CommandKind.AttackMove, Units = [f.Id], Cell = Cell.Of(t.Pos) });
            }
            else
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.AttackMove,
                    Units = [f.Id], Cell = Objective(w, f) });
            }
        }
        return cmds;
    }

    // ---- The end game's vehicles (INF-E1b-4 part 5, decision 1282, row 1213/1214 style: a bot fault, fixed in the bot) ----
    // On this head the full match stalled with each side's last soldier driving its rocket launcher: p1's on a Move home whose cell
    // (the base's centre) lay in its depot's box, so it never arrived; p2's held fast on its way out (the movement's Stuck) with its
    // order kept; nothing re-ordered either, and with no infantry left neither bot sent anything at the other. Now:
    // - Park: a unit sent home stops parkCells short of the base's centre, toward where it is (never inside a structure's box).
    // - A unit of the AI's own (not a squad's) held fast on an order (Stuck for stuckTicks) is stopped: idle, it is ordered afresh.
    // - With no soldier on foot left who can shoot the ground, every idle vehicle that can hurt a structure, with ammunition, attacks
    //   the nearest known enemy structure: a launcher attack-moves to a cell inside its band (between its minimum and its range) and
    //   fires there (the launchers' rule above), the others attack it.
    // - A launcher with a spotted enemy inside its minimum range moves off, away from it, to beyond that range.
    // - A unit that can shoot only aircraft, out on an attack-move, goes home (1214's Igla rule, for any unit).
    private const int ParkCells = 5;
    private const int StuckTicks = 10 * World.TicksPerSecond;

    private Cell Park(World w, Vec from)
    {
        var home = _home.Center();
        long dx = from.X.Raw - home.X.Raw, dy = from.Y.Raw - home.Y.Raw;
        var len = Fixed.Sqrt(dx * dx + dy * dy);
        if (len <= (long)ParkCells * Fixed.OneRaw)
        {
            return Cell.Of(from);
        }
        var c = Cell.Of(new Vec(Fixed.FromRaw(home.X.Raw + dx * ParkCells * Fixed.OneRaw / len), Fixed.FromRaw(home.Y.Raw + dy * ParkCells * Fixed.OneRaw / len)));
        return w.Grid.Contains(c) ? c : _home;
    }

    private void EndgameVehicles(World w, List<Command> cmds, uint at, List<Unit> enemies, List<Unit> fighters, List<Unit> guards, List<Unit> launchers)
    {
        var stop = new List<uint>();
        var footGun = false;
        foreach (var u in w.Units)
        {
            if (u.Owner != Player || !u.Alive || u.Def.IsStructure)
            {
                continue;
            }
            footGun |= u.Def.Archetype.Locomotor == Locomotor.Foot && u.Inside == 0 && u.CanAct && GroundGun(u);
            if (u.Squad == 0 && u.CanAct && u.Order.Kind != OrderKind.Idle && u.Intent.Stuck && u.StallTicks >= StuckTicks)
            {
                stop.Add(u.Id);
            }
        }
        if (stop.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Stop, Units = stop });
        }
        var home = new List<uint>();
        foreach (var g in guards)
        {
            if (g.Order.Kind == OrderKind.AttackMove && !GroundGun(g) && stop.BinarySearch(g.Id) < 0)
            {
                home.Add(g.Id);
            }
        }
        if (home.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Move, Units = home, Cell = Park(w, w.Unit(home[0])!.Pos) });
        }
        foreach (var l in launchers)
        {
            if (l.Order.Kind != OrderKind.Idle || l.SalvoLeft > 0 || MinRangeOf(l) is not (> 0 and var min) ||
                Nearest(enemies, l.Pos, e => e.Def.Layer == Layers.Ground && !CanReach(l, e) && (e.Pos - l.Pos).LenSq() < min * min) is not { } e)
            {
                continue;
            }
            long dx = l.Pos.X.Raw - e.Pos.X.Raw, dy = l.Pos.Y.Raw - e.Pos.Y.Raw;
            var len = Math.Max(1, Fixed.Sqrt(dx * dx + dy * dy));
            var off = min + 2L * Fixed.OneRaw;
            var c = Cell.Of(new Vec(Fixed.FromRaw(e.Pos.X.Raw + dx * off / len), Fixed.FromRaw(e.Pos.Y.Raw + dy * off / len)));
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Move, Units = [l.Id], Cell = w.Grid.Contains(c) ? c : Park(w, l.Pos) });
        }
        if (footGun)
        {
            return;
        }
        foreach (var v in fighters.Concat(launchers))
        {
            if (v.Order.Kind is not (OrderKind.Idle or OrderKind.Guard) || v.SalvoLeft > 0 || Empty(v) || !GroundGun(v) || stop.BinarySearch(v.Id) >= 0 ||
                v.Def.Archetype.Locomotor == Locomotor.Foot)
            {
                continue;
            }
            var launcher = launchers.Contains(v);
            if (launcher && Nearest(enemies, v.Pos, e => e.Def.Layer == Layers.Ground && CanReach(v, e)) is not null)
            {
                continue; // it fires from here (the launchers' rule)
            }
            if (!launcher && Nearest(enemies, v.Pos, e => e.Def.IsStructure && e.Def.Layer == Layers.Ground && w.PickWeapon(v, e, true) >= 0) is { } seen)
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Attack, Units = [v.Id], Target = seen.Id });
                continue;
            }
            if (NearestStructure(enemies, v.Pos) is not { } to)
            {
                continue;
            }
            // A cell in its band, on the line from the structure toward it (a launcher between its minimum range and its range; the
            // others three quarters of their range): it attack-moves there.
            var (min, max) = BandOf(v);
            var stand = min > 0 ? (min + max) / 2 : max * 3 / 4;
            var tc = to.Center();
            long dx = v.Pos.X.Raw - tc.X.Raw, dy = v.Pos.Y.Raw - tc.Y.Raw;
            var len = Math.Max(1, Fixed.Sqrt(dx * dx + dy * dy));
            var c = len <= stand ? Cell.Of(v.Pos) : Cell.Of(new Vec(Fixed.FromRaw(tc.X.Raw + dx * stand / len), Fixed.FromRaw(tc.Y.Raw + dy * stand / len)));
            if (c != Cell.Of(v.Pos) && w.Grid.Contains(c))
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.AttackMove, Units = [v.Id], Cell = c });
            }
        }
    }

    // NearestStructure is the cell of the enemy structure nearest p: a visible one, else one remembered (ID order breaks ties).
    private Cell? NearestStructure(List<Unit> enemies, Vec p)
    {
        Cell? best = null;
        var bestD = long.MaxValue;
        foreach (var e in enemies)
        {
            if (e.Def.IsStructure && (e.Pos - p).LenSq() is var d && d < bestD)
            {
                (best, bestD) = (Cell.Of(e.Pos), d);
            }
        }
        foreach (var m in _memory)
        {
            if ((m.Cell.Center() - p).LenSq() is var d && d < bestD)
            {
                (best, bestD) = (m.Cell, d);
            }
        }
        return best;
    }

    // MinRangeOf is the largest minimum range (raw) of u's ground weapons with ammunition; BandOf the band its ground weapons cover.
    private static long MinRangeOf(Unit u) => BandOf(u).Min;

    private static (long Min, long Max) BandOf(Unit u)
    {
        long min = 0, max = 0;
        foreach (var x in u.Def.Weapons)
        {
            if ((x.Effect.Targets & Layers.Ground) == 0 || (x.Stock >= 0 && u.Charges[x.Stock] <= 0))
            {
                continue;
            }
            (min, max) = (Math.Max(min, x.Effect.MinRange.Raw), Math.Max(max, x.Range.Raw));
        }
        return (min, max);
    }

    // GroundGun reports whether u has a weapon that hits the ground (not only aircraft: the Igla, the air defence).
    private static bool GroundGun(Unit u) => u.Def.Weapons.Any(x => (x.Effect.Targets & Layers.Ground) != 0);

    // Empty reports whether every weapon of u with a stock has none left.
    private static bool Empty(Unit u) => u.Def.Weapons.Count > 0 && u.Def.Weapons.All(x => x.Stock >= 0 && u.Charges[x.Stock] == 0);

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
