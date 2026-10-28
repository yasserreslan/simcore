using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The squad system (docs/design.md, "Infantry squads", phase S1): 2 to 10 infantry of one player ordered as one. A squad walks
// one path: its anchor (the leader's slot) moves along it at the squad's speed, the slots of the other members are laid out
// round the anchor by mode and role (March: a file along the anchor's trail; Assault: a line across the facing), and each
// member steers to his slot, straight when the way is clear, on a route of his own when it isn't. With no squad order the
// members keep to their slots round the anchor where it stopped; in a fight each one fights as the unit AI says, with his slot
// as his leash point. Commands: FormSquad (form, merge, add), Detach, Disband, SetSquadMode; Move, Attack-move and the other
// orders given to a member go to the whole squad. Everything in ID order, integer distances, no random draws.
public sealed partial class World
{
    private readonly List<Squad> _squads = []; // sorted by ID, ascending
    private uint _nextSquad;
    private readonly List<SquadEvent> _squadEvents = [];
    private int _squadPlans; // squad paths planned this tick (counted in PathRequests)

    private static readonly long TrailStep = Fixed.OneRaw / 2; // the anchor's trail: a point every half cell
    private const int FacingAhead = 3; // cells ahead along the path the shape faces

    /// <summary>The squads, by ID, ascending.</summary>
    public IReadOnlyList<Squad> Squads => _squads;

    /// <summary>The squad events of the last tick, in order (formed, merged, joined, detached, lost, leader, disbanded, refused, arrived).</summary>
    public IReadOnlyList<SquadEvent> SquadEvents => _squadEvents;

    /// <summary>Returns the squad with this ID, or null.</summary>
    public Squad? Squad(uint id)
    {
        int lo = 0, hi = _squads.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_squads[mid].Id < id)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < _squads.Count && _squads[lo].Id == id ? _squads[lo] : null;
    }

    /// <summary>Returns the squad unit belongs to, or null.</summary>
    public Squad? SquadOf(uint unit) => Unit(unit) is { Squad: not 0 } u ? Squad(u.Squad) : null;

    /// <summary>A soldier's squad role, by his class's tags (the content's <c>squad</c> block); Line without one.</summary>
    public SquadRole SquadRoleOf(Unit u) => Content.Squad?.RoleOf(u.Def.Tags) ?? SquadRole.Line;

    /// <summary>
    /// Whether the soldier may join a squad: alive, on the map (not inside a vehicle or building), infantry that moves, of no
    /// excluded class (workers and pilots), and the content has squads.
    /// </summary>
    public bool CanJoinSquad(Unit u) =>
        Content.Squad is { } rules && u.Alive && u.Inside == 0 && u.Garrison == 0 && !u.Def.IsStructure && u.Def.Layer == Layers.Ground &&
        u.Def.Speed.Raw > 0 && u.Def.Archetype.Flight is null && rules.Joins(u.Def.Tags);

    /// <summary>
    /// Where a member's slot is now (the point he walks to; with Hold option held the client draws it as a faint dot), or null
    /// for a unit in no squad, or while its squad is loose (its members on orders of their own).
    /// </summary>
    public Vec? SquadSlotPoint(uint unit)
    {
        if (Unit(unit) is not { Squad: not 0 } u || Squad(u.Squad) is not { Loose: false } s)
        {
            return null;
        }
        var i = s.MemberList.BinarySearch(u.Id);
        return i < 0 ? null : SlotPoint(s, i);
    }

    /// <summary>The cell of a member's slot now (see <see cref="SquadSlotPoint"/>), or null.</summary>
    public Cell? SquadSlotCell(uint unit) => SquadSlotPoint(unit) is { } p ? Cell.Of(p) : null;

    // ---- Commands ----

    // FormSquad: the listed infantry of the player. No squad among them: squads of up to the most in ID order (a last group
    // smaller than the fewest stays loose). One squad: the loose ones join it up to the most (the rest stay loose). Several:
    // they merge into the lowest-ID one if their members together are no more than the most (else nothing changes), then the
    // loose ones join it.
    private void FormSquadCommand(Command c)
    {
        if (Content.Squad is not { } rules)
        {
            return;
        }
        var loose = new List<Unit>();
        var squads = new List<Squad>();
        foreach (var id in c.Units)
        {
            if (Unit(id) is not { Alive: true } u || u.Owner != c.Player)
            {
                continue;
            }
            if (u.Squad != 0)
            {
                if (Squad(u.Squad) is { } s && !squads.Contains(s))
                {
                    squads.Add(s);
                }
                continue;
            }
            if (CanJoinSquad(u) && !loose.Contains(u))
            {
                loose.Add(u);
            }
        }
        loose.Sort((a, b) => a.Id.CompareTo(b.Id));
        squads.Sort((a, b) => a.Id.CompareTo(b.Id));
        if (squads.Count == 0)
        {
            for (var i = 0; i < loose.Count; i += rules.MaxSize)
            {
                var n = Math.Min(rules.MaxSize, loose.Count - i);
                if (n >= rules.MinSize)
                {
                    NewSquad(c.Player, loose.GetRange(i, n), rules);
                }
            }
            return;
        }
        var keep = squads[0];
        if (squads.Count > 1)
        {
            var total = 0;
            foreach (var s in squads)
            {
                total += s.MemberList.Count;
            }
            if (total > rules.MaxSize)
            {
                _squadEvents.Add(new SquadEvent(keep.Id, keep.Owner, SquadEventKind.MergeRefused, 0, (uint)total));
                return;
            }
            for (var i = 1; i < squads.Count; i++)
            {
                var other = squads[i];
                foreach (var id in other.MemberList)
                {
                    var m = Unit(id)!;
                    LeaveSquadOrder(m, other);
                    Join(keep, m);
                }
                other.MemberList.Clear();
                _squads.Remove(other);
                _squadEvents.Add(new SquadEvent(keep.Id, keep.Owner, SquadEventKind.Merged, 0, other.Id));
            }
        }
        foreach (var u in loose)
        {
            if (keep.MemberList.Count >= rules.MaxSize)
            {
                break;
            }
            u.Idle();
            Join(keep, u);
            _squadEvents.Add(new SquadEvent(keep.Id, keep.Owner, SquadEventKind.Joined, u.Id, 0));
        }
        Rebuild(keep, rules);
    }

    // NewSquad forms a squad of members (ID order): everyone stops, takes the leader's stance and walks to his slot round the
    // leader, who faces as he does.
    private void NewSquad(byte owner, List<Unit> members, SquadRules rules)
    {
        var s = new Squad(++_nextSquad, owner) { Mode = SquadMode.March };
        foreach (var u in members)
        {
            u.Idle();
            u.Squad = s.Id;
            s.MemberList.Add(u.Id);
        }
        _squads.Add(s); // IDs only grow: the list stays sorted
        Rebuild(s, rules);
        var leader = Unit(s.Leader)!;
        s.Stance = leader.Stance;
        foreach (var u in members)
        {
            u.Stance = s.Stance;
        }
        Settle(s, leader.Pos, FixedPoint.Facing.Dir(leader.Facing));
        _squadEvents.Add(new SquadEvent(s.Id, owner, SquadEventKind.Formed, s.Leader, 0));
    }

    // Join adds u to s (its stance with it).
    private static void Join(Squad s, Unit u)
    {
        var i = s.MemberList.BinarySearch(u.Id);
        if (i < 0)
        {
            s.MemberList.Insert(~i, u.Id);
        }
        u.Squad = s.Id;
        u.Stance = s.Stance;
    }

    // Detach: each listed member leaves his squad, loose with its stance, where he stands; a squad left with too few disbands.
    private void DetachCommand(Command c)
    {
        if (Content.Squad is not { } rules)
        {
            return;
        }
        foreach (var id in c.Units)
        {
            if (Unit(id) is not { Squad: not 0 } u || u.Owner != c.Player || Squad(u.Squad) is not { } s)
            {
                continue;
            }
            LeaveSquadOrder(u, s);
            s.MemberList.Remove(u.Id);
            u.Squad = 0;
            u.SquadFollow = false;
            _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Detached, u.Id, 0));
            Shrunk(s, rules);
        }
    }

    // Disband: the squads of the listed members are gone; everyone is loose, in place.
    private void DisbandCommand(Command c)
    {
        foreach (var id in c.Units)
        {
            if (Unit(id) is { Squad: not 0 } u && u.Owner == c.Player && Squad(u.Squad) is { } s)
            {
                DisbandSquad(s);
            }
        }
    }

    // SetSquadMode: the squads of the listed members take mode Index (March or Assault); the shape is rebuilt at once.
    private void SetSquadModeCommand(Command c)
    {
        if (Content.Squad is not { } rules || c.Index is < (int)SquadMode.March or > (int)SquadMode.Assault)
        {
            return;
        }
        foreach (var id in c.Units)
        {
            if (Unit(id) is { Squad: not 0 } u && u.Owner == c.Player && Squad(u.Squad) is { } s && s.Mode != (SquadMode)c.Index)
            {
                s.Mode = (SquadMode)c.Index;
                BuildSlots(s, rules);
            }
        }
    }

    // LeaveSquadOrder: a member leaving s (detached, merged away, disbanded) drops the squad's move order: he stops where he is.
    private static void LeaveSquadOrder(Unit u, Squad s)
    {
        if (s.Order != OrderKind.Idle && u.Order.Kind == s.Order && u.Order.Cell == s.Goal)
        {
            u.Order = default;
            u.ClearPath();
            u.MoveOn = false;
        }
    }

    // Shrunk: s lost members; with fewer than the fewest it disbands, else its leader and slots are worked out again.
    private void Shrunk(Squad s, SquadRules rules)
    {
        if (s.MemberList.Count < rules.MinSize)
        {
            DisbandSquad(s);
            return;
        }
        Rebuild(s, rules);
    }

    private void DisbandSquad(Squad s)
    {
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { } m)
            {
                LeaveSquadOrder(m, s);
                m.Squad = 0;
                m.SquadFollow = false;
            }
        }
        s.MemberList.Clear();
        _squads.Remove(s);
        _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Disbanded, 0, 0));
    }

    // ---- Orders given to members ----

    // The orders a member carries out on his own: the squad goes loose (its move ends) and rebuilds its shape once they are done.
    private static bool LoosensSquad(CommandKind k) => k is CommandKind.Attack or CommandKind.AttackGround or CommandKind.Guard or
        CommandKind.Scatter or CommandKind.Enter or CommandKind.Exit or CommandKind.Stop;

    // SquadCommand widens a command given to some members of a squad to all of them (Exit: those in the same vehicle or building),
    // so the order goes to the squad; orders members carry out on their own make the squad loose; a stance becomes the squad's.
    private Command SquadCommand(Command c)
    {
        var loosen = LoosensSquad(c.Kind);
        var widen = loosen || c.Kind is CommandKind.Move or CommandKind.AttackMove or CommandKind.SetStance;
        if (!widen)
        {
            return c;
        }
        List<uint>? units = null;
        foreach (var id in c.Units)
        {
            if (Unit(id) is not { Squad: not 0 } u || u.Owner != c.Player || Squad(u.Squad) is not { } s)
            {
                continue;
            }
            if (loosen)
            {
                Loosen(s);
            }
            if (c.Kind == CommandKind.SetStance && c.Stance is Stance.Guard or Stance.HoldFire or Stance.ReturnFire or Stance.HoldGround or
                Stance.Aggressive or Stance.Hide)
            {
                s.Stance = c.Stance;
            }
            foreach (var mid in s.MemberList)
            {
                if (mid == id || Unit(mid) is not { } m)
                {
                    continue;
                }
                if (c.Kind == CommandKind.Exit && !SameShelter(u, m))
                {
                    continue; // out as one: only the squad's soldiers in there with him
                }
                units ??= [.. c.Units];
                if (!units.Contains(mid))
                {
                    units.Add(mid);
                }
            }
        }
        return units is null ? c : c with { Units = units };
    }

    // SameShelter: m is in the same building as u, or rides in the same vehicle (not as its driver).
    private bool SameShelter(Unit u, Unit m) =>
        (u.Garrison != 0 && m.Garrison == u.Garrison) ||
        (u.Inside != 0 && m.Inside == u.Inside && Unit(m.Inside) is { } v && v.Driver != m.Id);

    // Loosen: the squad's move ends and its members act on their own orders (it rebuilds once they are all idle).
    private static void Loosen(Squad s)
    {
        s.Loose = true;
        s.Order = OrderKind.Idle;
        ClearSquadPath(s);
    }

    private static void ClearSquadPath(Squad s)
    {
        s.PathCells = [];
        s.PathIdx = 0;
        s.Route = [];
        s.RouteIdx = 0;
        s.PathPending = false;
        s.Waited = 0;
    }

    // TakeSquads moves the squads of the listed ground units to c (Move or Attack-move) and returns the units in no squad, for the
    // ordinary group move. Several squads stand side by side across the move at c, the rules' group gap apart, in ID order.
    private List<Unit> TakeSquads(List<Unit> units, Cell c, OrderKind kind)
    {
        if (_squads.Count == 0)
        {
            return units;
        }
        List<Squad>? squads = null;
        var rest = new List<Unit>();
        foreach (var u in units)
        {
            if (u.Squad != 0 && Squad(u.Squad) is { } s)
            {
                squads ??= [];
                if (!squads.Contains(s))
                {
                    squads.Add(s);
                }
                continue;
            }
            rest.Add(u);
        }
        if (squads is null)
        {
            return units;
        }
        squads.Sort((a, b) => a.Id.CompareTo(b.Id));
        long cx = 0, cy = 0;
        foreach (var s in squads)
        {
            if (s.Loose)
            {
                ReAnchor(s);
            }
            cx += s.Anchor.X.Raw;
            cy += s.Anchor.Y.Raw;
        }
        var centre = new Vec(Fixed.FromRaw(cx / squads.Count), Fixed.FromRaw(cy / squads.Count));
        var f = (c.Center() - centre).WithLen(Fixed.One);
        if (f.LenSq() == 0)
        {
            f = squads[0].Facing;
        }
        var right = new Vec(-f.Y, f.X);
        var gap = Content.Squad!.GroupGap;
        for (var i = 0; i < squads.Count; i++)
        {
            var s = squads[i];
            var side = Fixed.FromRaw(gap.Raw * (2 * i - (squads.Count - 1)) / 2);
            var p = c.Center() + Scale(right, side);
            var goal = new Cell(Math.Clamp(Cell.Of(p).X, 0, Grid.Width - 1), Math.Clamp(Cell.Of(p).Y, 0, Grid.Height - 1));
            OrderSquad(s, goal, kind, units);
        }
        return rest;
    }

    // OrderSquad puts s on a move to goal: its path is planned in this tick's squad pass, and each listed member takes the
    // squad's order (the squad, not he, decides when it is done).
    private void OrderSquad(Squad s, Cell goal, OrderKind kind, List<Unit> listed)
    {
        s.Loose = false;
        s.Order = kind;
        s.Goal = goal;
        ClearSquadPath(s);
        s.PathPending = true;
        foreach (var u in listed)
        {
            if (u.Squad != s.Id)
            {
                continue;
            }
            Assign(u, new Order(kind, goal, 0, 0));
            if (kind == OrderKind.AttackMove)
            {
                u.ResetAi();
            }
        }
    }

    // InSquadMove: u carries its squad's move order (the squad pass, not the order system, ends it).
    private bool InSquadMove(Unit u) =>
        u.Squad != 0 && Squad(u.Squad) is { } s && s.Order != OrderKind.Idle && s.Order == u.Order.Kind && s.Goal == u.Order.Cell;

    // ThinkSquadMove: a member on the squad's attack-move fights as on any attack-move (a chase first, a stop to shoot what is
    // in reach), the squad waiting for him; on a move he only walks.
    private void ThinkSquadMove(Unit u)
    {
        if (u.Order.Kind != OrderKind.AttackMove)
        {
            return;
        }
        if (u.Ai != AiState.None)
        {
            RunAi(u);
            if (u.Ai != AiState.None)
            {
                return;
            }
        }
        if (!u.Def.Turret && Unit(u.AutoTarget) is { } cur && CanAutoTarget(u, cur))
        {
            u.ClearPath(); // stop and shoot it (no chase)
            u.SquadHalt = true;
        }
    }

    // SquadHome is a member's leash point: his slot (while the squad keeps its shape), or null.
    private Vec? SquadHome(Unit u)
    {
        if (Squad(u.Squad) is not { Loose: false } s)
        {
            return null;
        }
        var i = s.MemberList.BinarySearch(u.Id);
        return i < 0 ? null : SlotPoint(s, i);
    }

    // ---- The squad pass (before the path system) ----

    private void RunSquads()
    {
        _squadPlans = 0;
        if (_squads.Count == 0)
        {
            return;
        }
        var r = SyncRouting();
        var rules = Content.Squad!;
        foreach (var s in _squads)
        {
            if (s.Loose && LooseDone(s))
            {
                ReAnchor(s);
                s.Loose = false;
            }
            if (s.Order != OrderKind.Idle)
            {
                if (s.PathPending && _squadPlans < RequestBudget)
                {
                    PlanSquad(r, s); // at most the path system's request budget a tick: the rest next tick, the anchor waiting
                }
                if (!s.PathPending && !WaitsForMembers(s, rules))
                {
                    WalkAnchor(r, s, rules);
                }
            }
            for (var i = 0; i < s.MemberList.Count; i++)
            {
                var u = Unit(s.MemberList[i])!;
                u.SquadFollow = Follows(u, s);
                if (u.SquadFollow)
                {
                    u.SquadSlot = SlotPoint(s, i);
                }
            }
        }
    }

    // Follows: u walks to his slot now: on the map, able to move, not fighting on his own (a chase, a walk back, a stop to
    // shoot), set up for nothing, and either idle or on the squad's move.
    private static bool Follows(Unit u, Squad s) =>
        !s.Loose && u.Alive && u.Inside == 0 && u.Garrison == 0 && u.CanAct && u.Def.Speed.Raw > 0 && u.Ai == AiState.None && !u.SquadHalt &&
        u.Deploy == DeployState.Stowed &&
        (u.Order.Kind == OrderKind.Idle || (s.Order != OrderKind.Idle && u.Order.Kind == s.Order && u.Order.Cell == s.Goal));

    // LooseDone: every member of a loose squad on the map is idle again (none, while they are all inside: it stays loose).
    private bool LooseDone(Squad s)
    {
        var any = false;
        foreach (var id in s.MemberList)
        {
            var u = Unit(id)!;
            if (u.Inside != 0 || u.Garrison != 0)
            {
                continue;
            }
            if (u.Order.Kind != OrderKind.Idle || u.Ai != AiState.None)
            {
                return false;
            }
            any = true;
        }
        return any;
    }

    // ReAnchor rebuilds the shape where the leader stands (or, while he is inside something, the first member on the map).
    private void ReAnchor(Squad s)
    {
        var at = Unit(s.Leader) is { Inside: 0, Garrison: 0 } l ? l : null;
        if (at is null)
        {
            foreach (var id in s.MemberList)
            {
                if (Unit(id) is { Inside: 0, Garrison: 0 } m)
                {
                    at = m;
                    break;
                }
            }
        }
        if (at is not null)
        {
            Settle(s, at.Pos, s.Facing);
        }
    }

    // Settle puts the anchor at p facing f, its trail starting over there.
    private static void Settle(Squad s, Vec p, Vec f)
    {
        s.Anchor = p;
        s.Facing = f.LenSq() == 0 ? new Vec(Fixed.Zero, -Fixed.One) : f.WithLen(Fixed.One);
        s.Trail.Clear();
        s.Trail.Add(p);
    }

    // WaitsForMembers: a moving squad waits while a member fights on its attack-move (a chase or a stop to shoot), and for at
    // most the rules' regroup ticks while one is farther than the regroup distance from his slot.
    private bool WaitsForMembers(Squad s, SquadRules rules)
    {
        var lag = false;
        for (var i = 0; i < s.MemberList.Count; i++)
        {
            var u = Unit(s.MemberList[i])!;
            if (s.Order == OrderKind.AttackMove && u.Inside == 0 && u.Garrison == 0 && (u.SquadHalt || u.Ai != AiState.None))
            {
                return true; // fighting: the squad holds its ground until he is done
            }
            if (!lag && Follows(u, s) && !Within(u.Pos, SlotPoint(s, i), rules.Regroup))
            {
                lag = true;
            }
        }
        if (!lag)
        {
            s.Waited = 0;
            return false;
        }
        if (s.Waited >= rules.RegroupTicks)
        {
            return false; // it waited long enough: on, the laggard on his own route
        }
        s.Waited++;
        return true;
    }

    // SquadClass is the routing class the squad's path is planned for: its leader's.
    private int SquadClass(Router r, Squad s)
    {
        if (Unit(s.Leader) is { } l && r.ClassOf(l.Def) is var lk and >= 0)
        {
            return lk;
        }
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { } u && r.ClassOf(u.Def) is var k and >= 0)
            {
                return k;
            }
        }
        return -1;
    }

    // PlanSquad plans the squad's path from its anchor to its goal (as a unit's own route: refined a few legs at a time).
    private void PlanSquad(Router r, Squad s)
    {
        s.PathPending = false;
        _squadPlans++;
        var k = SquadClass(r, s);
        var here = Cell.Of(s.Anchor);
        var from = k < 0 ? -1 : r.NearestPassable(k, here, StartSearch);
        if (from < 0)
        {
            ArriveSquad(s);
            return;
        }
        var d = r.NearestPassable(k, s.Goal, DestSearch);
        if (d < 0)
        {
            d = r.Index(new Cell(Math.Clamp(s.Goal.X, 0, Grid.Width - 1), Math.Clamp(s.Goal.Y, 0, Grid.Height - 1)));
        }
        r.FindRoute(k, from, d, _cells, _route);
        s.Route = [.. _route];
        s.RouteIdx = 0;
        if (_route.Count > 0)
        {
            var idx = 0;
            r.Refine(k, from, s.Route, ref idx, RefineAhead, _cells);
            s.RouteIdx = idx;
        }
        r.Smooth(k, from, _cells, 0);
        if (from != r.Index(here) && (_cells.Count == 0 || _cells[0] != from))
        {
            _cells.Insert(0, from); // first onto ground it fits
        }
        s.PathCells = ToCells(r, _cells, 0);
        s.PathIdx = 0;
    }

    // SquadSpeed: the slowest member's speed times the mode's percent (per tick).
    private Fixed SquadSpeed(Squad s, SquadRules rules)
    {
        long slowest = long.MaxValue;
        foreach (var id in s.MemberList)
        {
            slowest = Math.Min(slowest, Unit(id)!.Def.Speed.Raw);
        }
        var pct = s.Mode == SquadMode.Assault ? rules.AssaultSpeedPercent : rules.MarchSpeedPercent;
        return Fixed.FromRaw(slowest * pct / 100);
    }

    // WalkAnchor moves the anchor along the path at the squad's speed (the terrain's percent under it, for its class), refining
    // the route ahead as it goes; at the end the squad has arrived. Its trail and facing follow.
    private void WalkAnchor(Router r, Squad s, SquadRules rules)
    {
        var k = SquadClass(r, s);
        if (k >= 0 && s.RouteIdx < s.Route.Length && s.PathCells.Length - s.PathIdx < RefineWhen)
        {
            _cells.Clear();
            var from = s.PathCells.Length > 0 ? r.Index(s.PathCells[^1]) : r.Index(Cell.Of(s.Anchor));
            var idx = s.RouteIdx;
            if (r.Refine(k, from, s.Route, ref idx, RefineAhead, _cells))
            {
                r.Smooth(k, from, _cells, 0);
                s.RouteIdx = idx;
                var left = s.PathCells.Length - s.PathIdx;
                var a = new Cell[left + _cells.Count];
                Array.Copy(s.PathCells, s.PathIdx, a, 0, left);
                for (var i = 0; i < _cells.Count; i++)
                {
                    a[left + i] = r.CellAt(_cells[i]);
                }
                s.PathCells = a;
                s.PathIdx = 0;
            }
            else
            {
                s.PathPending = true; // the map changed under the route: planned again next tick
                return;
            }
        }
        if (s.PathIdx >= s.PathCells.Length)
        {
            if (s.RouteIdx >= s.Route.Length)
            {
                ArriveSquad(s);
            }
            return;
        }
        var speed = SquadSpeed(s, rules);
        var leader = Unit(s.Leader)!;
        var cls = Ground?.Rules.ClassOf(leader.Def) ?? -1;
        var budget = speed.Raw * TerrainPercent(cls, s.Anchor, s.PathCells[s.PathIdx].Center() - s.Anchor) / 100;
        var p = s.Anchor;
        while (budget > 0 && s.PathIdx < s.PathCells.Length)
        {
            var wp = s.PathCells[s.PathIdx].Center();
            var d = wp - p;
            var len = d.Len().Raw;
            if (len <= budget)
            {
                p = wp;
                budget -= len;
                s.PathIdx++;
                continue;
            }
            p += d.WithLen(Fixed.FromRaw(budget));
            budget = 0;
        }
        s.Anchor = p;
        if (s.Trail.Count == 0 || (p - s.Trail[^1]).LenSq() >= TrailStep * TrailStep)
        {
            s.Trail.Add(p);
            var cap = (int)(rules.MaxSize * Math.Max(rules.MarchSpacing.Raw, Math.Max(rules.MarchAirGuardGap.Raw, rules.MarchSupportGap.Raw)) / TrailStep) + 8;
            if (s.Trail.Count > cap)
            {
                s.Trail.RemoveAt(0);
            }
        }
        var ahead = Ahead(s, Fixed.FromInt(FacingAhead)) - p;
        if (ahead.LenSq() >= Fixed.OneRaw * Fixed.OneRaw)
        {
            s.Facing = ahead.WithLen(Fixed.One);
        }
        if (s.PathIdx >= s.PathCells.Length && s.RouteIdx >= s.Route.Length && !s.PathPending)
        {
            ArriveSquad(s);
        }
    }

    // ArriveSquad: the squad's order is done; its members are idle, keeping to their slots round where the anchor stopped.
    private void ArriveSquad(Squad s)
    {
        foreach (var id in s.MemberList)
        {
            var u = Unit(id)!;
            if (u.Order.Kind == s.Order && u.Order.Cell == s.Goal)
            {
                u.Order = default;
                u.MoveOn = false;
            }
        }
        s.Order = OrderKind.Idle;
        ClearSquadPath(s);
        _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Arrived, s.Leader, 0));
    }

    // ---- Steering (the path system's loop) ----

    // SteerSquad writes a following member's intent: straight to his slot when its cell fits his class and the way there is
    // clear; otherwise along a route of his own toward it (or toward the anchor when the slot's cell doesn't fit), re-planned at
    // most once a second.
    private void SteerSquad(Router r, Unit u)
    {
        var slot = u.SquadSlot;
        var k = r.ClassOf(u.Def);
        var cell = Cell.Of(slot);
        var fits = k >= 0 && Grid.Contains(cell) && r.Passable(k, r.Index(cell));
        if (fits && r.Clear(k, u.Pos, slot))
        {
            if (u.Mode != RouteMode.None || u.Path.Count > 0)
            {
                u.ClearPath();
            }
            var d = slot - u.Pos;
            var speed = u.Def.Speed;
            var step = d.LenSq() <= speed.Raw * speed.Raw ? d : d.WithLen(speed);
            var pushedOff = u.Intent.Arrived && d.LenSq() > Fixed.OneRaw * Fixed.OneRaw; // shoved a cell off a slot it reached
            u.Intent = slot == u.Intent.Goal && !pushedOff ? u.Intent with { Desired = step, Slot = default } : new MoveIntent(slot, step, default, false);
            return;
        }
        if (!u.Moving && !u.PathPending && Tick >= u.RepathAt)
        {
            var to = fits ? cell : Cell.Of(Squad(u.Squad)!.Anchor);
            RequestRoute(u, to);
            u.RepathAt = Tick + RepathTicks + u.Id % RepathTicks;
        }
        Steer(r, u);
    }

    // ---- Slots ----

    // Rebuild works out the leader (the lowest-ID member whose role leads; none: the lowest ID) and the slots.
    private void Rebuild(Squad s, SquadRules rules)
    {
        uint leader = 0;
        foreach (var id in s.MemberList)
        {
            if (rules.Leads(rules.RoleOf(Unit(id)!.Def.Tags)))
            {
                leader = id;
                break;
            }
        }
        if (leader == 0 && s.MemberList.Count > 0)
        {
            leader = s.MemberList[0];
        }
        if (s.Leader != 0 && leader != s.Leader)
        {
            _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Leader, leader, s.Leader));
        }
        s.Leader = leader;
        BuildSlots(s, rules);
    }

    // BuildSlots lays the members out by mode and role. March, from the front: recon ahead along the path, the leader (the
    // anchor), the flanks, the line with the medics in its middle, then the long flanks, the air guards and the support, each
    // the march spacing behind the one before (air guards behind air guards their gap, support behind support packed).
    // Assault: the leader in the middle of the line, the other line soldiers right and left of him on the shorter side, the
    // flanks at its ends; the other roles behind it, each role spread across the facing by its gap round the middle (recon beyond the
    // line's right end, then its left).
    private void BuildSlots(Squad s, SquadRules rules)
    {
        var n = s.MemberList.Count;
        s.Roles = new SquadRole[n];
        s.SlotDepth = new Fixed[n];
        s.SlotLateral = new Fixed[n];
        s.SlotTrail = new bool[n];
        var lead = -1;
        for (var i = 0; i < n; i++)
        {
            s.Roles[i] = rules.RoleOf(Unit(s.MemberList[i])!.Def.Tags);
            if (s.MemberList[i] == s.Leader)
            {
                lead = i;
            }
        }
        if (lead < 0)
        {
            return;
        }
        List<int> Of(SquadRole role)
        {
            var l = new List<int>();
            for (var i = 0; i < n; i++)
            {
                if (i != lead && s.Roles[i] == role)
                {
                    l.Add(i);
                }
            }
            return l;
        }
        if (s.Mode == SquadMode.March)
        {
            var body = new List<int> { lead };
            body.AddRange(Of(SquadRole.Flank));
            body.AddRange(Of(SquadRole.Line));
            body.InsertRange(Math.Max(1, (body.Count + 1) / 2), Of(SquadRole.Medic));
            body.AddRange(Of(SquadRole.LongFlank));
            body.AddRange(Of(SquadRole.AirGuard));
            body.AddRange(Of(SquadRole.Support));
            var depth = Fixed.Zero;
            for (var j = 0; j < body.Count; j++)
            {
                var i = body[j];
                if (j > 0)
                {
                    var (prev, role) = (s.Roles[body[j - 1]], s.Roles[i]);
                    depth += role == SquadRole.AirGuard && prev == SquadRole.AirGuard ? rules.MarchAirGuardGap
                        : role == SquadRole.Support && prev == SquadRole.Support ? rules.MarchSupportGap
                        : rules.MarchSpacing;
                }
                s.SlotDepth[i] = depth;
                s.SlotTrail[i] = true;
            }
            s.MaxTrailDepth = depth;
            var ahead = Of(SquadRole.Recon);
            for (var j = 0; j < ahead.Count; j++)
            {
                s.SlotDepth[ahead[j]] = -(rules.MarchAhead + Fixed.FromRaw(rules.MarchSpacing.Raw * j));
                s.SlotTrail[ahead[j]] = true;
            }
            return;
        }
        s.MaxTrailDepth = Fixed.Zero;
        var line = Of(SquadRole.Line);
        int lo = 0, hi = 0;
        foreach (var i in (List<int>)[.. line, .. Of(SquadRole.Flank)]) // the line, then the flanks at its ends
        {
            var pos = hi <= -lo ? ++hi : --lo; // on the shorter side, the right first: the leader stays in the middle
            s.SlotLateral[i] = Fixed.FromRaw(rules.AssaultSpacing.Raw * pos);
        }
        foreach (var role in (ReadOnlySpan<SquadRole>)[SquadRole.Medic, SquadRole.LongFlank, SquadRole.AirGuard, SquadRole.Support])
        {
            var place = rules.AssaultPlaces[(int)role];
            var of = Of(role);
            for (var j = 0; j < of.Count; j++)
            {
                var pos = j == 0 ? 0 : j % 2 == 1 ? (j + 1) / 2 : -(j / 2); // the middle, right, left, ...
                s.SlotDepth[of[j]] = place.Back;
                s.SlotLateral[of[j]] = Fixed.FromRaw(place.Gap.Raw * pos);
            }
        }
        var recon = Of(SquadRole.Recon);
        var rp = rules.AssaultPlaces[(int)SquadRole.Recon];
        for (var j = 0; j < recon.Count; j++)
        {
            var off = rules.AssaultReconSide.Raw + rp.Gap.Raw * (j / 2);
            s.SlotDepth[recon[j]] = rp.Back;
            s.SlotLateral[recon[j]] = Fixed.FromRaw(j % 2 == 0 ? rules.AssaultSpacing.Raw * hi + off : rules.AssaultSpacing.Raw * lo - off);
        }
    }

    // SlotPoint is where member i of s stands now: along the trail (March: behind the anchor; ahead along the path when its
    // depth is negative) or fixed to the anchor and facing (Assault), kept on the map.
    private Vec SlotPoint(Squad s, int i)
    {
        Vec p;
        if (s.SlotTrail.Length <= i)
        {
            p = s.Anchor;
        }
        else if (s.SlotTrail[i])
        {
            var d = s.SlotDepth[i];
            p = d.Raw < 0 ? Ahead(s, -d) : Behind(s, d);
        }
        else
        {
            var f = s.Facing;
            p = s.Anchor - Scale(f, s.SlotDepth[i]) + Scale(new Vec(-f.Y, f.X), s.SlotLateral[i]);
        }
        var maxX = (long)Grid.Width * Fixed.OneRaw - 1;
        var maxY = (long)Grid.Height * Fixed.OneRaw - 1;
        return new Vec(Fixed.FromRaw(Math.Clamp(p.X.Raw, 0, maxX)), Fixed.FromRaw(Math.Clamp(p.Y.Raw, 0, maxY)));
    }

    // Behind is the point d back along the anchor's trail; past its oldest point, straight on the way the trail came (or
    // behind the facing).
    private static Vec Behind(Squad s, Fixed d)
    {
        var left = d.Raw;
        var prev = s.Anchor;
        for (var t = s.Trail.Count - 1; t >= 0; t--)
        {
            var seg = s.Trail[t] - prev;
            var len = seg.Len().Raw;
            if (len >= left && len > 0)
            {
                return prev + seg.WithLen(Fixed.FromRaw(left));
            }
            left -= len;
            prev = s.Trail[t];
        }
        var back = s.Trail.Count >= 2 ? s.Trail[0] - s.Trail[1] : new Vec(-s.Facing.X, -s.Facing.Y);
        if (back.LenSq() == 0)
        {
            back = new Vec(-s.Facing.X, -s.Facing.Y);
        }
        return prev + back.WithLen(Fixed.FromRaw(left));
    }

    // Ahead is the point a on along the squad's path from the anchor; past its end (or idle), straight on along the facing.
    private static Vec Ahead(Squad s, Fixed a)
    {
        var left = a.Raw;
        var prev = s.Anchor;
        for (var i = s.PathIdx; i < s.PathCells.Length; i++)
        {
            var seg = s.PathCells[i].Center() - prev;
            var len = seg.Len().Raw;
            if (len >= left && len > 0)
            {
                return prev + seg.WithLen(Fixed.FromRaw(left));
            }
            left -= len;
            prev = s.PathCells[i].Center();
        }
        return prev + s.Facing.WithLen(Fixed.FromRaw(left));
    }

    // Scale returns unit vector v times length l.
    private static Vec Scale(Vec v, Fixed l) => new(v.X * l, v.Y * l);

    // ---- Losses ----

    // PruneSquads (after cleanup): members gone from the world leave their squads the same tick; a squad with too few left
    // disbands, one that lost its leader has the next one at once.
    private void PruneSquads()
    {
        if (_squads.Count == 0)
        {
            return;
        }
        var rules = Content.Squad!;
        for (var i = 0; i < _squads.Count; i++)
        {
            var s = _squads[i];
            var lost = false;
            for (var j = s.MemberList.Count - 1; j >= 0; j--)
            {
                var id = s.MemberList[j];
                if (Unit(id) is { Alive: true })
                {
                    continue;
                }
                s.MemberList.RemoveAt(j);
                lost = true;
                _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Lost, id, 0));
            }
            if (!lost)
            {
                continue;
            }
            var before = _squads.Count;
            Shrunk(s, rules);
            if (_squads.Count < before)
            {
                i--; // disbanded: removed from the list
            }
        }
    }
}
