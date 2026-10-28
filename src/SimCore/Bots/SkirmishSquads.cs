// The skirmish AI's squads (docs/design.md, "Squads: the skirmish AI", phase S5). The AI forms its loose infantry at home into
// mixed squads (8 at most, each class dealt round them), and gives each squad one job, in ID order: an Igla-heavy squad
// sets an air ambush on the likely air approach; then, while at most half its ground squads stay home, one squad sets a ground
// ambush on a road toward the base and one holds the key spot in front of the base (a wave squad back home takes over a job that
// lost its squad); the rest go out with the waves, in March on the way (the squad path takes the roads) and in Assault once an
// enemy is near; with nothing else left to fight, everything goes. Every place it orders lies inside its territory: within
// TerritoryCells of its base or of a structure of its own. The enemy's likely way in is the remembered enemy structure nearest the base, else the point opposite
// the base across the map's centre (the side's own knowledge only, as the waves'). No random draws: integer scores over cells
// in row order, ties to the first; squads in ID order. The world never sees this memory: replays need only the commands.
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class SkirmishAi
{
    /// <summary>Whether the AI forms squads and gives them jobs (ambushes, a hold, the waves); off, it plays as before phase S5.</summary>
    public bool UseSquads { get; init; } = true;

    /// <summary>The soldiers a squad the AI forms holds at most (the engine's limit is 10).</summary>
    public int SquadSize { get; init; } = 8;

    /// <summary>Loose soldiers at home the AI waits for before it forms a squad of them.</summary>
    public int SquadMin { get; init; } = 4;

    /// <summary>The AI's territory: cells within this many cells of its base or of one of its structures.</summary>
    public int TerritoryCells { get; init; } = 30;

    /// <summary>Squads the AI keeps in road ambushes at once.</summary>
    public int AmbushSquads { get; init; } = 1;

    /// <summary>Squads the AI keeps holding the key spot at once.</summary>
    public int HoldSquads { get; init; } = 1;

    /// <summary>The kill zone the AI wants (cells along the road; the engine takes 6 to 30).</summary>
    public int AmbushCells { get; init; } = 16;

    /// <summary>How far from its base (cells) the AI wants a road ambush's middle, toward the enemy.</summary>
    public int AmbushAt { get; init; } = 16;

    /// <summary>How far from its base (cells) the key spot a squad holds lies, toward the enemy.</summary>
    public int HoldAt { get; init; } = 10;

    /// <summary>Iglas at home the AI needs before it forms an air ambush squad.</summary>
    public int AirIglas { get; init; } = 3;

    /// <summary>A marching wave squad turns to Assault with a spotted enemy this near (cells); back to March past it plus 10.</summary>
    public int AssaultCells { get; init; } = 20;

    /// <summary>What the AI uses a squad for.</summary>
    internal enum SquadJob : byte
    {
        Wave,
        Ambush,
        AirAmbush,
        Hold,
    }

    /// <summary>A squad's job and where (a kill zone or corridor from A to B; a held spot A facing B).</summary>
    internal readonly record struct SquadTask(uint Squad, SquadJob Job, Cell A, Cell B, uint Ordered);

    private const int ReorderTicks = 10 * World.TicksPerSecond; // a squad that lost its mode is ordered again at most this often
    private const int AirLongest = 26; // the air corridor: from this many cells out toward the enemy ...
    private const int AirShortest = 4; // ... in to this many cells from the base
    private const int SquadIglas = 6;  // Iglas in the air ambush squad at most (with fewer line soldiers: mostly Iglas)

    private readonly List<SquadTask> _tasks = []; // by squad ID
    private readonly List<Unit> _outposts = [];   // this decision's structures (the territory)
    private readonly List<uint> _wavedNow = [];   // the squads sent out with this decision's wave
    private int _ground;                          // this decision's ground squads (not air ambush ones)
    private bool _endgame;                        // a wave found nothing else to send: no more jobs at home (ambushes, the hold)

    internal IReadOnlyList<SquadTask> SquadTasksForTests => _tasks;

    /// <summary>Whether cell c is in the AI's territory (within <see cref="TerritoryCells"/> of its base or of a structure of its own) at its last decision.</summary>
    public bool InTerritory(Cell c)
    {
        if (Near(c.Center(), _home.Center(), TerritoryCells))
        {
            return true;
        }
        foreach (var o in _outposts)
        {
            if (Near(c.Center(), o.Pos, TerritoryCells))
            {
                return true;
            }
        }
        return false;
    }

    // SquadsThink is the squad part of a decision: forget squads that are gone, give new squads their jobs, keep the jobs going,
    // and form squads of the loose soldiers at home (taken out of fighters and guards: the rest of the decision leaves them alone).
    private void SquadsThink(World w, List<Command> cmds, uint at, List<Unit> enemies, List<Unit> fighters, List<Unit> guards)
    {
        if (w.Content.Squad is not { } rules)
        {
            return;
        }
        for (var i = _tasks.Count - 1; i >= 0; i--)
        {
            if (w.Squad(_tasks[i].Squad) is not { } s || s.Owner != Player)
            {
                _tasks.RemoveAt(i);
            }
        }
        _wavedNow.Clear();
        var way = EnemyWay(w);
        var stance = new List<uint>();
        var guardStance = new List<uint>();
        _ground = 0;
        foreach (var s in w.Squads)
        {
            _ground += s.Owner == Player && !w.AirAmbushSquad(s.Id) ? 1 : 0;
        }
        foreach (var s in w.Squads)
        {
            if (s.Owner != Player)
            {
                continue;
            }
            var i = TaskIndex(s.Id);
            if (i < 0)
            {
                var task = NewTask(w, s, rules, way);
                i = ~i;
                _tasks.Insert(i, task);
                Order(cmds, at, s, task);
            }
            else if (_tasks[i].Job == SquadJob.Wave && s.Order == OrderKind.Idle && !s.Loose && !Out(s) && Near(s.Anchor, _home.Center(), HoldRadius) &&
                !AnyFighting(w, s) && DefenceTask(w, s, rules, way) is { } task)
            {
                _tasks[i] = task; // a wave squad at home takes over an ambush or the hold that lost its squad
                Order(cmds, at, s, task);
            }
            var t = _tasks[i];
            if (t.Job != SquadJob.Wave && s.Mode != ModeOf(t.Job) && s.Order == OrderKind.Idle && !s.Loose && w.Tick >= t.Ordered + ReorderTicks)
            {
                _tasks[i] = t with { Ordered = w.Tick };
                Order(cmds, at, s, t); // its mode was lost (an order it took on its own): set it again
            }
            var want = t.Job == SquadJob.AirAmbush ? Stance.Guard : Stance.Aggressive;
            if (s.Stance != want)
            {
                (want == Stance.Guard ? guardStance : stance).Add(s.Leader);
            }
            if (t.Job == SquadJob.Wave)
            {
                WaveShape(cmds, at, s, enemies);
            }
        }
        if (stance.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.SetStance, Units = stance, Stance = Stance.Aggressive });
        }
        if (guardStance.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.SetStance, Units = guardStance, Stance = Stance.Guard });
        }
        FormSquads(w, cmds, at, rules, fighters, guards);
    }

    private int TaskIndex(uint squad)
    {
        int lo = 0, hi = _tasks.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_tasks[mid].Squad < squad)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < _tasks.Count && _tasks[lo].Squad == squad ? lo : ~lo;
    }

    private int Count(SquadJob job)
    {
        var n = 0;
        foreach (var t in _tasks)
        {
            n += t.Job == job ? 1 : 0;
        }
        return n;
    }

    private static SquadMode ModeOf(SquadJob job) => job switch
    {
        SquadJob.Ambush or SquadJob.AirAmbush => SquadMode.Ambush,
        SquadJob.Hold => SquadMode.Hold,
        _ => SquadMode.March,
    };

    // NewTask picks a new squad's job: an Igla-heavy squad an air ambush (when a corridor fits the territory), then a road ambush
    // while fewer than AmbushSquads are set (when a road segment fits), a hold of the key spot while fewer than HoldSquads hold,
    // else the waves.
    private SquadTask NewTask(World w, Squad s, SquadRules rules, (long X, long Y) way)
    {
        if (rules.Ambush is { Air: not null } && w.AirAmbushSquad(s.Id))
        {
            return AirCorridor(w, rules.Ambush, way) is { } air ? new SquadTask(s.Id, SquadJob.AirAmbush, air.A, air.B, w.Tick)
                : new SquadTask(s.Id, SquadJob.Wave, default, default, w.Tick);
        }
        return DefenceTask(w, s, rules, way) ?? new SquadTask(s.Id, SquadJob.Wave, default, default, w.Tick);
    }

    // DefenceTask is a ground squad's job at home, while at most half the ground squads have one (the rest go with the waves): a
    // road ambush while fewer than AmbushSquads are set (when a road segment fits), else the hold of the key spot while fewer than
    // HoldSquads hold; null for none.
    private SquadTask? DefenceTask(World w, Squad s, SquadRules rules, (long X, long Y) way)
    {
        if (_endgame || Count(SquadJob.Ambush) + Count(SquadJob.Hold) >= _ground / 2)
        {
            return null;
        }
        if (rules.Ambush is { } amb && Count(SquadJob.Ambush) < AmbushSquads && RoadZone(w, amb, way) is { } zone)
        {
            return new SquadTask(s.Id, SquadJob.Ambush, zone.A, zone.B, w.Tick);
        }
        if (rules.Hold is not null && Count(SquadJob.Hold) < HoldSquads && KeySpot(w, way) is { } spot)
        {
            return new SquadTask(s.Id, SquadJob.Hold, spot.A, spot.B, w.Tick);
        }
        return null;
    }

    // Order gives squad s its job's order (the waves' come with the waves).
    private void Order(List<Command> cmds, uint at, Squad s, SquadTask t)
    {
        if (t.Job == SquadJob.Wave)
        {
            return;
        }
        var mode = t.Job == SquadJob.Hold ? SquadMode.Hold : SquadMode.Ambush;
        cmds.Add(new Command
        {
            Tick = at, Player = Player, Kind = CommandKind.SetSquadMode, Units = [s.Leader], Index = (int)mode, Cell = t.A, Cell2 = t.B,
        });
    }

    // WaveShape: a wave squad marches (a file on the squad's path, which takes the roads) and turns to Assault (a line, every gun
    // forward) once a spotted ground enemy is within AssaultCells of it; it marches again when none is within AssaultCells + 10.
    private void WaveShape(List<Command> cmds, uint at, Squad s, List<Unit> enemies)
    {
        if (s.Mode is SquadMode.Hold or SquadMode.Ambush)
        {
            return;
        }
        var near = Nearest(enemies, s.Anchor, e => e.Def.Layer == Layers.Ground && Near(e.Pos, s.Anchor, AssaultCells + 10));
        var want = near is not null && (s.Mode == SquadMode.Assault || Near(near.Pos, s.Anchor, AssaultCells)) ? SquadMode.Assault : SquadMode.March;
        if (s.Mode != want)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.SetSquadMode, Units = [s.Leader], Index = (int)want });
        }
    }

    // FormSquads forms squads of the free soldiers at home: first, with AirIglas Iglas or more and no air ambush squad yet, one of up
    // to SquadIglas Iglas and fewer line soldiers (mostly Iglas); then, with SquadMin or more left, as few squads of SquadSize at
    // most as hold them all, the soldiers dealt round them by role and ID (each squad a mix of every class). Iglas left over stay
    // loose on Guard, the base's air defence.
    private void FormSquads(World w, List<Command> cmds, uint at, SquadRules rules, List<Unit> fighters, List<Unit> guards)
    {
        var home = _home.Center();
        var loose = new List<Unit>();
        foreach (var f in fighters)
        {
            if (Free(f) && !Searching(f) && Near(f.Pos, home, HoldRadius) && w.CanJoinSquad(f) && w.SquadRoleOf(f) != SquadRole.AirGuard)
            {
                loose.Add(f);
            }
        }
        var taken = new List<uint>();
        if (rules.Ambush is { Air: not null } && Count(SquadJob.AirAmbush) == 0 && !_endgame)
        {
            var iglas = new List<Unit>();
            foreach (var g in guards)
            {
                if (iglas.Count < SquadIglas && Free(g) && Near(g.Pos, home, HoldRadius) && w.CanJoinSquad(g) && w.SquadRoleOf(g) == SquadRole.AirGuard)
                {
                    iglas.Add(g);
                }
            }
            if (iglas.Count >= AirIglas)
            {
                var units = new List<uint>();
                foreach (var g in iglas)
                {
                    units.Add(g.Id);
                }
                var line = 0;
                foreach (var f in loose)
                {
                    if (line < iglas.Count - 1 && units.Count < rules.MaxSize && w.SquadRoleOf(f) == SquadRole.Line)
                    {
                        units.Add(f.Id);
                        line++;
                    }
                }
                units.Sort();
                taken.AddRange(units);
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.FormSquad, Units = units });
            }
        }
        // The rest are dealt round the squads by role (then ID), so each squad gets a share of every class: mixed squads.
        var rest = new List<Unit>();
        foreach (var f in loose)
        {
            if (taken.BinarySearch(f.Id) < 0)
            {
                rest.Add(f);
            }
        }
        var size = Math.Clamp(SquadSize, rules.MinSize, rules.MaxSize);
        if (rest.Count >= Math.Max(SquadMin, rules.MinSize))
        {
            rest.Sort((a, b) => w.SquadRoleOf(a) != w.SquadRoleOf(b) ? w.SquadRoleOf(a).CompareTo(w.SquadRoleOf(b)) : a.Id.CompareTo(b.Id));
            var k = (rest.Count + size - 1) / size;
            for (var q = 0; q < k; q++)
            {
                var units = new List<uint>();
                for (var i = q; i < rest.Count; i += k)
                {
                    units.Add(rest[i].Id);
                }
                units.Sort();
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.FormSquad, Units = units });
                foreach (var id in units)
                {
                    taken.Insert(~taken.BinarySearch(id), id);
                }
            }
        }
        if (taken.Count > 0)
        {
            fighters.RemoveAll(u => taken.BinarySearch(u.Id) >= 0);
            guards.RemoveAll(u => taken.BinarySearch(u.Id) >= 0);
        }
    }

    // ---- Squads in the waves ----

    // WaveSquads sends the wave squads at home with nothing to do out with the wave: March, attack-moving to `to`. With nothing
    // else left to fight (no loose fighter and no wave squad, at home or out), the ground squads on the base's jobs (the road ambush, the
    // hold) go instead and become wave squads, and the air ambush squad disbands (its guards go with the next wave, its Iglas stay
    // home on Guard): the AI never sits out a match behind its traps. From then on it gives no more jobs at home, and its air
    // defence and launchers go with the waves too (SkirmishAi.Think): a match never stalls between two bases that only guard.
    // Returns whether this wave found nothing else to send.
    private bool WaveSquads(World w, List<Command> cmds, uint at, Func<Unit, Cell> target, bool othersLeft)
    {
        var leaders = new List<uint>();
        var marching = new List<uint>();
        Unit? first = null;
        var all = !othersLeft && !AnyWaveSquad(w);
        _endgame |= all;
        var disband = new List<uint>();
        for (var i = 0; i < _tasks.Count; i++)
        {
            var t = _tasks[i];
            if (all && t.Job is SquadJob.Ambush or SquadJob.Hold)
            {
                t = _tasks[i] = t with { Job = SquadJob.Wave };
            }
            if (all && t.Job == SquadJob.AirAmbush && w.Squad(t.Squad) is { } air)
            {
                disband.Add(air.Leader); // its guards go with the next wave as loose fighters, its Iglas stay home on Guard
            }
            if (t.Job != SquadJob.Wave || w.Squad(t.Squad) is not { } s || (s.Order != OrderKind.Idle && !all) || s.Loose ||
                !Near(s.Anchor, _home.Center(), all ? TerritoryCells : HoldRadius) || w.Unit(s.Leader) is not { } leader)
            {
                continue;
            }
            first ??= leader;
            leaders.Add(s.Leader);
            _wavedNow.Add(s.Id);
            if (s.Mode != SquadMode.March)
            {
                marching.Add(s.Leader);
            }
            TrackWave([.. s.Members]);
        }
        if (disband.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Disband, Units = disband });
        }
        if (first is null)
        {
            return all;
        }
        if (marching.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.SetSquadMode, Units = marching, Index = (int)SquadMode.March });
        }
        cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.AttackMove, Units = leaders, Cell = target(first) });
        return all;
    }

    private bool AnyWaveSquad(World w)
    {
        foreach (var t in _tasks)
        {
            if (t.Job == SquadJob.Wave && w.Squad(t.Squad) is not null)
            {
                return true;
            }
        }
        return false;
    }

    // WaveSquadsOut: a wave squad out in the field (searching, or away from the base) with nothing to do (it arrived, or its fight
    // is over) attacks a passive enemy structure it can hit, else attack-moves to a spotted enemy near it, a remembered structure,
    // its own search probe, or (every probe seen lately) home, to go out with the next wave.
    private void WaveSquadsOut(World w, List<Command> cmds, uint at, List<Unit> enemies)
    {
        foreach (var t in _tasks)
        {
            if (t.Job != SquadJob.Wave || w.Squad(t.Squad) is not { } s || s.Loose || w.Unit(s.Leader) is not { } leader ||
                (!Out(s) && Near(s.Anchor, _home.Center(), HoldRadius)) || _wavedNow.Contains(s.Id))
            {
                continue;
            }
            if (s.Order is OrderKind.Idle or OrderKind.AttackMove &&
                Nearest(enemies, leader.Pos, e => e.Def.IsStructure && e.Def.Layer == Layers.Ground && w.PickWeapon(leader, e, true) >= 0) is { } structure)
            {
                cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Attack, Units = [leader.Id], Target = structure.Id });
                continue;
            }
            if (s.Order != OrderKind.Idle || AnyFighting(w, s))
            {
                continue;
            }
            Cell to;
            if (Nearest(enemies, s.Anchor, e => e.Def.Layer == Layers.Ground && Near(e.Pos, s.Anchor, HoldRadius)) is { } e)
            {
                to = Cell.Of(e.Pos);
            }
            else
            {
                to = Objective(w, leader, s.Anchor);
                if (!Searching(leader))
                {
                    foreach (var id in s.Members)
                    {
                        _searching.Remove(id); // every probe seen lately: the squad goes home, out again with the next wave
                    }
                }
            }
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.AttackMove, Units = [leader.Id], Cell = to });
        }
    }

    // WaveSquadsDefend: an intruder near the base is attacked by the wave squads at home with nothing to do.
    private void WaveSquadsDefend(World w, List<Command> cmds, uint at, Unit intruder)
    {
        var leaders = new List<uint>();
        foreach (var t in _tasks)
        {
            if (t.Job == SquadJob.Wave && w.Squad(t.Squad) is { Order: OrderKind.Idle, Loose: false } s && Near(s.Anchor, _home.Center(), HoldRadius) &&
                !AnyFighting(w, s))
            {
                leaders.Add(s.Leader);
            }
        }
        if (leaders.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Attack, Units = leaders, Target = intruder.Id });
        }
    }

    // Out: a member of the squad went out with a wave and is still searching.
    private bool Out(Squad s)
    {
        foreach (var id in s.Members)
        {
            if (_searching.BinarySearch(id) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool AnyFighting(World w, Squad s)
    {
        foreach (var id in s.Members)
        {
            if (w.Unit(id) is { Ai: not AiState.None })
            {
                return true;
            }
        }
        return false;
    }

    // ---- Where ----

    // EnemyWay is the way (in cells, not normalised) from the base toward the enemy: to the remembered enemy structure nearest the
    // base, else to the point opposite the base across the map's centre.
    private (long X, long Y) EnemyWay(World w)
    {
        SeenStructure? best = null;
        var bestD = long.MaxValue;
        foreach (var m in _memory)
        {
            var d = (m.Cell.Center() - _home.Center()).LenSq();
            if (d < bestD)
            {
                (best, bestD) = (m, d);
            }
        }
        var to = best is { } b ? b.Cell : new Cell(w.Grid.Width - 1 - _home.X, w.Grid.Height - 1 - _home.Y);
        long x = to.X - _home.X, y = to.Y - _home.Y;
        return x == 0 && y == 0 ? (1, 0) : (x, y);
    }

    // Toward is the cell `cells` from the base along the way (rounded toward zero), on the map or null.
    private Cell? Toward(World w, (long X, long Y) way, int cells)
    {
        var len = Math.Max(1, Fixed.Sqrt(way.X * way.X + way.Y * way.Y));
        var c = new Cell(_home.X + (int)(way.X * cells / len), _home.Y + (int)(way.Y * cells / len));
        return w.Grid.Contains(c) ? c : null;
    }

    // KeySpot is where a squad holds: HoldAt cells from the base toward the enemy, facing that way (10 cells on), in the territory.
    private (Cell A, Cell B)? KeySpot(World w, (long X, long Y) way)
    {
        if (Toward(w, way, HoldAt) is not { } spot || !InTerritory(spot) || !w.Grid.Passable(spot))
        {
            return null;
        }
        var face = Toward(w, way, HoldAt + 10) ?? spot;
        return (spot, face);
    }

    // AirCorridor is the air ambush's corridor: the line aircraft from the enemy's side fly to the base, from AirLongest cells out
    // toward the enemy (or less, to stay in the territory and on the map) in to AirShortest; null when no corridor of the rules'
    // least length fits.
    private (Cell A, Cell B)? AirCorridor(World w, SquadAmbushRules amb, (long X, long Y) way)
    {
        if (amb.Air is not { } air || Toward(w, way, AirShortest) is not { } end)
        {
            return null;
        }
        for (var far = AirLongest; far > AirShortest; far--)
        {
            if (Toward(w, way, far) is not { } start || !InTerritory(start))
            {
                continue;
            }
            var len = (start.Center() - end.Center()).LenSq();
            if (len < air.MinLength.Raw * air.MinLength.Raw)
            {
                return null; // shorter from here on
            }
            if (len <= air.MaxLength.Raw * air.MaxLength.Raw)
            {
                return (start, end);
            }
        }
        return null;
    }

    private static readonly (int Dx, int Dy)[] Steps = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];

    // RoadZone is the best road ambush: a straight run of road cells in the territory, AmbushCells long at most (the rules' least
    // length at least), from its start A on the enemy's side of the base toward the base (the enemy comes in at A, the column is
    // stopped at B). Score (integers): how well it points from the enemy to the base, its length, its middle near AmbushAt cells
    // from the base, and woods beside it (cover to set the trap in); not within 10 cells of another squad's zone. Cells in row
    // order, then the 8 directions in order; ties to the first. Null without terrain or a road that fits.
    private (Cell A, Cell B)? RoadZone(World w, SquadAmbushRules amb, (long X, long Y) way)
    {
        if (w.Ground is not { } g)
        {
            return null;
        }
        var wayLen = Math.Max(1, Fixed.Sqrt(way.X * way.X + way.Y * way.Y));
        var r = TerritoryCells;
        long bestScore = long.MinValue;
        (Cell A, Cell B)? best = null;
        for (var y = _home.Y - r; y <= _home.Y + r; y++)
        {
            for (var x = _home.X - r; x <= _home.X + r; x++)
            {
                var a = new Cell(x, y);
                long ax = x - _home.X, ay = y - _home.Y;
                if (ax * way.X + ay * way.Y <= 0 || !g.Road(a) || w.Grid.At(a) != Terrain.Clear || !InTerritory(a))
                {
                    continue; // on the enemy's side of the base only
                }
                foreach (var (dx, dy) in Steps)
                {
                    if (-ax * dx - ay * dy <= 0)
                    {
                        continue; // the zone runs toward the base
                    }
                    var diagonal = dx != 0 && dy != 0;
                    var want = diagonal ? AmbushCells * 10 / 14 : AmbushCells;
                    var k = 0;
                    while (k < want)
                    {
                        var n = new Cell(x + (k + 1) * dx, y + (k + 1) * dy);
                        if (!g.Road(n) || w.Grid.At(n) != Terrain.Clear || !InTerritory(n))
                        {
                            break;
                        }
                        k++;
                    }
                    var b = new Cell(x + k * dx, y + k * dy);
                    var len = (b.Center() - a.Center()).LenSq();
                    if (len < amb.MinLength.Raw * amb.MinLength.Raw || len > amb.MaxLength.Raw * amb.MaxLength.Raw)
                    {
                        continue;
                    }
                    var mid = new Cell(x + k / 2 * dx, y + k / 2 * dy);
                    if (TooClose(mid))
                    {
                        continue;
                    }
                    var align = (-dx * way.X - dy * way.Y) * 1000 * 1000 / (wayLen * (diagonal ? 1414 : 1000)); // -1000 to 1000
                    var midCells = Fixed.Sqrt((long)(mid.X - _home.X) * (mid.X - _home.X) + (long)(mid.Y - _home.Y) * (mid.Y - _home.Y));
                    var lenCells = Fixed.Sqrt(len) / Fixed.OneRaw;
                    var cover = 0;
                    for (var i = 0; i <= k; i += 2)
                    {
                        foreach (var side in (ReadOnlySpan<int>)[-5, -3, 3, 5])
                        {
                            cover += g.Forest(new Cell(x + i * dx - side * dy, y + i * dy + side * dx)) >= 2 ? 1 : 0;
                        }
                    }
                    var score = align * 2 + lenCells * 20 - Math.Abs(midCells - AmbushAt) * 30 + cover * 5;
                    if (score > bestScore)
                    {
                        (bestScore, best) = (score, (a, b));
                    }
                }
            }
        }
        return best;
    }

    // TooClose: within 10 cells of the middle of another squad's kill zone.
    private bool TooClose(Cell mid)
    {
        foreach (var t in _tasks)
        {
            if (t.Job == SquadJob.Ambush && Near(mid.Center(), new Cell((t.A.X + t.B.X) / 2, (t.A.Y + t.B.Y) / 2).Center(), 10))
            {
                return true;
            }
        }
        return false;
    }
}
