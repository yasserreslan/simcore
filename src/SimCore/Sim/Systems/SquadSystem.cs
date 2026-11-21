using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The squad system (docs/design.md, "Infantry squads", phase S1): 2 to 10 infantry of one player ordered as one. A squad walks
// one path: its anchor (the leader's slot) moves along it at the squad's speed, the slots of the other members are laid out
// round the anchor by mode and role (March: a file along the anchor's trail; Assault: a line across the facing), and each
// member steers to his slot, straight when the way is clear, on a route of his own when it isn't. On a new order the slots go
// to the members by where they stand along the move (SQ-ORDER: the members ahead take the forward slots), and a member whose
// slot is behind him never walks back to it: he waits for it, and the squad does not wait for him. With no squad order the
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

    /// <summary>
    /// The squad events of the last tick, in order (formed, merged, joined, detached, lost, leader, disbanded, refused, arrived, placed;
    /// an ambush's setting, ready, sprung and mine laid).
    /// </summary>
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
    /// Where a member's slot is now (the point he walks to; with Hold option held the client draws it as a faint dot; in Hold, once
    /// placed, his Hold cell's centre), or null for a unit in no squad, or while its squad is loose (its members on orders of their own).
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
        u.Stance = AmbushHoldsFire(s) ? Stance.HoldFire : s.Stance;
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
            LeaveAmbushTask(s, u); // his mine (if he was laying one) is given up, the squad's stance his again
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

    // SetSquadMode: the squads of the listed members take mode Index (March, Assault or Hold); the shape is rebuilt at once.
    // Hold (phase S2) is an order: at Cell (the spot), facing toward Cell2 (none: the way the squad walked); several squads hold
    // side by side across the facing, the group gap apart, in ID order.
    private void SetSquadModeCommand(Command c)
    {
        if (Content.Squad is not { } rules || c.Index is < (int)SquadMode.March or > (int)SquadMode.Ambush)
        {
            return;
        }
        var mode = (SquadMode)c.Index;
        var squads = new List<Squad>();
        foreach (var id in c.Units)
        {
            if (Unit(id) is { Squad: not 0 } u && u.Owner == c.Player && Squad(u.Squad) is { } s && !squads.Contains(s))
            {
                squads.Add(s);
            }
        }
        squads.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var s in squads)
        {
            PlayerOrdered(s); // INF-E1b-3 (19.4)
        }
        if (mode == SquadMode.Hold)
        {
            if (rules.Hold is { } hold && squads.Count > 0)
            {
                HoldSquads(squads, c, rules, hold);
            }
            return;
        }
        if (mode == SquadMode.Ambush)
        {
            if (rules.Ambush is { } amb && squads.Count > 0)
            {
                AmbushSquads(squads, c, rules, amb); // SquadAmbushSystem.cs
            }
            return;
        }
        foreach (var s in squads)
        {
            if (s.Mode != mode)
            {
                LeaveAmbush(s);
                LeaveHold(s);
                s.Mode = mode;
                if (s.Order != OrderKind.Idle)
                {
                    Rank(s); // a new shape on the move: the slots go by where the members stand now
                }
                BuildSlots(s, rules);
            }
        }
    }

    // HoldSquads gives the Hold order c to squads (ID order): one squad at the spot, several side by side across the facing.
    private void HoldSquads(List<Squad> squads, Command c, SquadRules rules, SquadHoldRules hold)
    {
        var spot = new Cell(Math.Clamp(c.Cell.X, 0, Grid.Width - 1), Math.Clamp(c.Cell.Y, 0, Grid.Height - 1));
        var f = c.Cell2 is { } to && to != spot ? to.Center() - spot.Center() : default;
        if (f.LenSq() == 0)
        {
            long cx = 0, cy = 0;
            foreach (var s in squads)
            {
                cx += s.Anchor.X.Raw;
                cy += s.Anchor.Y.Raw;
            }
            f = spot.Center() - new Vec(Fixed.FromRaw(cx / squads.Count), Fixed.FromRaw(cy / squads.Count));
        }
        f = f.LenSq() == 0 ? squads[0].Facing : f.WithLen(Fixed.One);
        var right = new Vec(-f.Y, f.X);
        for (var i = 0; i < squads.Count; i++)
        {
            var side = Scale(right, Fixed.FromRaw(rules.GroupGap.Raw * (2 * i - (squads.Count - 1)) / 2));
            var p = Cell.Of(spot.Center() + side);
            var at = new Cell(Math.Clamp(p.X, 0, Grid.Width - 1), Math.Clamp(p.Y, 0, Grid.Height - 1));
            Cell? toward = c.Cell2 is { } t && t != spot ? Cell.Of(t.Center() + side) : null;
            HoldOrder(squads[i], at, toward, rules, hold);
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
        LeaveAmbushTasks(s); // an ambush's mines being laid are given up, its hold fire lifted (SquadAmbushSystem.cs)
        s.PickList.Clear(); // (who is in a building as part of Hold stays there: everyone loose, in place)
        s.HoldPlaced = false;
        s.HoldPending = false;
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
        CommandKind.Scatter or CommandKind.Enter or CommandKind.Exit or CommandKind.Stop or CommandKind.Escort;

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
            PlayerOrdered(s); // INF-E1b-3 (19.4): any order of the player ends the squad's automatic behaviour
            if (loosen)
            {
                Loosen(s);
                if (c.Kind is CommandKind.Attack or CommandKind.AttackGround && AmbushHoldsFire(s) && s.SpringPending == AmbushTrigger.None)
                {
                    s.SpringPending = AmbushTrigger.Order; // an attack ordered from a set (or setting) ambush springs it
                }
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
        if (c.Kind == CommandKind.SetStance)
        {
            // A set (or setting) ambush holds fire until it springs: the stance is the squad's, its members take it then.
            List<uint>? kept = null;
            var list = units ?? (IReadOnlyList<uint>)c.Units;
            foreach (var id in list)
            {
                if (Unit(id) is { Squad: not 0 } m && m.Owner == c.Player && Squad(m.Squad) is { } ms && AmbushHoldsFire(ms))
                {
                    kept ??= [.. list.TakeWhile(x => x != id)];
                    continue;
                }
                kept?.Add(id);
            }
            if (kept is not null)
            {
                return c with { Units = kept };
            }
        }
        return units is null ? c : c with { Units = units };
    }

    // SameShelter: m is in the same building as u, or rides in the same vehicle (not as its driver).
    private bool SameShelter(Unit u, Unit m) =>
        (u.Garrison != 0 && m.Garrison == u.Garrison) ||
        (u.Inside != 0 && m.Inside == u.Inside && Unit(m.Inside) is { } v && v.Driver != m.Id);

    // Loosen: the squad's move ends and its members act on their own orders (it rebuilds once they are all idle).
    private void Loosen(Squad s)
    {
        s.Loose = true;
        s.Order = OrderKind.Idle;
        ClearSquadPath(s);
    }

    // ClearSquadPath ends the squad's path; the slots go by role again (until the next order's path ranks them), unless keep: on
    // arrival (INF-MOVE) the slots stay by place, every member's rest his final position (no reshuffle into role order).
    private void ClearSquadPath(Squad s, bool keep = false)
    {
        s.PathCells = [];
        s.PathIdx = 0;
        s.Route = [];
        s.RouteIdx = 0;
        s.PathPending = false;
        s.Waited = 0;
        if (s.Places.Count > 0 && !keep)
        {
            s.Places.Clear();
            if (Content.Squad is { } rules)
            {
                BuildSlots(s, rules);
            }
        }
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
            if (s.Mode == SquadMode.Ambush)
            {
                LeaveAmbush(s); // the kill zone belongs to its place: a move ends the ambush, the squad marches
                s.Mode = SquadMode.March;
                BuildSlots(s, Content.Squad!);
            }
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
        if (s.Mode == SquadMode.Hold)
        {
            LeaveHold(s); // it holds again at the goal, facing the way it walked (picked on arrival)
            s.HoldSpot = goal;
            s.HoldFaced = false;
        }
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
        if (Squad(u.Squad) is { Mind.HasLine: true })
        {
            return; // INF-E1b-3: no stop of his own: the squad's line places him (he fires from there or on the move)
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
        _holdPicks = 0;
        if (_squads.Count == 0)
        {
            return;
        }
        var r = SyncRouting();
        var rules = Content.Squad!;
        foreach (var s in _squads)
        {
            if (s.Loose && !s.Retreating && LooseDone(s)) // a retreat stays loose until it is there (RetreatSystem.cs)
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
                if (!s.PathPending && WaitsForMembers(s, rules) is var pace and > 0)
                {
                    WalkAnchor(r, s, rules, pace);
                }
            }
            RunHold(r, s, rules); // Hold: its cells picked once, its members holding them (SquadHoldSystem.cs)
            if (s.FinalsStale)
            {
                PlaceFinals(r, s);
            }
            FollowSlots(r, s, rules);
        }
    }

    private readonly List<long> _finalLeft = [];

    // FollowSlots writes each member's word for this tick (INF-MOVE, the owner's words of 2026-11-21): one who follows walks to his
    // final position (decided at the order: FinalOf) when the squad is at rest or he is within the rules' direct range of it, and
    // either sees it straight or the anchor is within that range of the goal (then by a route of his own if he must; so one
    // behind a building keeps to the file round it, never switching back and forth); farther, on the move, to his slot along the squad's way, and one whose slot is behind him walks
    // on level with its line at the rules' creep percent of the squad's pace (never standing) while it comes to him. The squad
    // keeps together by pace: on the way to their final positions members walk at the squad's pace, and once all who follow are
    // on that way, each at the pace that brings them in together (his way left over the longest, from a quarter of the pace). A
    // slot off the squad's line (Assault's, in a wall or beyond one from the anchor, as the squad goes through a gap) gives way
    // to a place in file on the anchor's trail, as far back as the slot lies from the anchor (left of it, half a spacing more).
    private void FollowSlots(Router r, Squad s, SquadRules rules)
    {
        var pace = SquadSpeed(s, rules).Raw;
        var all = true;
        long most = 0;
        _finalLeft.Clear();
        for (var i = 0; i < s.MemberList.Count; i++)
        {
            var u = Unit(s.MemberList[i])!;
            u.SquadFollow = Follows(u, s);
            u.SquadAbreast = false;
            u.SquadPace = 0;
            _finalLeft.Add(-1);
            if (!u.SquadFollow)
            {
                continue;
            }
            if (LineGoalOf(s, u) is { } lg)
            {
                // INF-E1b-3: the squad's line, bound or march (11.1 to 11.4): his goal picked at the squad's think, walked to
                // together by pace like a final position.
                u.SquadSlot = lg;
                var lleft = (lg - u.Pos).Len().Raw;
                _finalLeft[i] = lleft;
                most = Math.Max(most, lleft);
                continue;
            }
            if (FinalOf(s, i) is { } fin && (s.Order == OrderKind.Idle || (Within(u.Pos, fin, rules.DirectRange) &&
                (Within(s.Anchor, s.Goal.Center(), rules.DirectRange) || r.ClassOf(u.Def) is not (>= 0 and var fk) || r.Clear(fk, u.Pos, fin)))))
            {
                u.SquadSlot = fin;
                var left = (fin - u.Pos).Len().Raw;
                _finalLeft[i] = left;
                most = Math.Max(most, left);
                continue;
            }
            all = false;
            var slot = SlotPoint(s, i);
            if (s.Order != OrderKind.Idle && i < s.SlotTrail.Length && !s.SlotTrail[i] && r.ClassOf(u.Def) is var k and >= 0 &&
                (!r.Passable(k, r.Index(Cell.Of(slot))) || !r.Clear(k, s.Anchor, slot)))
            {
                var lat = s.SlotLateral[i].Raw;
                slot = Behind(s, Fixed.FromRaw(Math.Max(0, s.SlotDepth[i].Raw) + Math.Abs(lat) + (lat < 0 ? rules.MarchSpacing.Raw / 2 : 0)));
            }
            if (Abreast(s, i, u.Pos, slot) is { } level)
            {
                // His slot is behind him: he closes its line sideways and walks on slowly along it, letting it come to him.
                slot = OnMap(level + SlotHeading(s, i));
                u.SquadAbreast = true;
                u.SquadPace = Math.Max(1, pace * rules.CreepPercent / 100);
            }
            u.SquadSlot = slot;
        }
        var closing = false;
        for (var i = 0; i < _finalLeft.Count; i++)
        {
            if (_finalLeft[i] >= 0)
            {
                var u = Unit(s.MemberList[i])!;
                u.SquadPace = all && most > 0 ? Math.Max(pace / 4, pace * _finalLeft[i] / most) : pace;
                closing |= _finalLeft[i] > Fixed.OneRaw && !u.Intent.Stuck; // INF-MOVE-4: still walking in, the squad not at rest yet
            }
        }
        s.Closing = s.Order == OrderKind.Idle && closing;
    }

    // FinalOf is member i's final position: his Hold or Ambush cell once picked; else, after an order, the squad's layout at its
    // goal (PlaceFinals); else null (a squad that never took an order rests in its shape round the anchor).
    private Vec? FinalOf(Squad s, int i)
    {
        if (s.Mode is SquadMode.Hold or SquadMode.Ambush && s.HoldPlaced && i < s.MemberList.Count && HoldPickIndex(s, s.MemberList[i]) is >= 0 and var k)
        {
            return s.PickList[k].Cell.Center();
        }
        return i < s.FinalPts.Length && s.FinalPts.Length == s.MemberList.Count ? s.FinalPts[i] : null;
    }

    // LayoutAt is the point of a slot (depth behind, lateral to the right) in the squad's layout at its goal, facing the order's way.
    private static Vec LayoutAt(Squad s, Fixed depth, Fixed lateral)
    {
        var f = s.OrderFacing;
        return s.Goal.Center() - Scale(f, depth) + Scale(new Vec(-f.Y, f.X), lateral);
    }

    private readonly List<Cell> _finalCells = [];

    // PlaceFinals works out every member's final position once the slots are rebuilt (INF-MOVE): his slot in the layout at the goal
    // facing the order's way (the March file straight back from the goal, recon ahead of it; the Assault line across it), on a
    // cell his class fits that no squadmate's final position took, seen straight from the goal; when it is not, the nearest such
    // cell within 6 (ties: the cell index), else the point itself. Members in MemberList order: deterministic.
    private void PlaceFinals(Router r, Squad s)
    {
        s.FinalsStale = false;
        var n = s.MemberList.Count;
        if (s.Places.Count == 0 || s.SlotDepth.Length != n)
        {
            s.FinalPts = [];
            return;
        }
        var pts = new Vec[n];
        _finalCells.Clear();
        var g = s.Goal.Center();
        for (var i = 0; i < n; i++)
        {
            var u = Unit(s.MemberList[i])!;
            var k = r.ClassOf(u.Def);
            var p = OnMap(LayoutAt(s, s.SlotDepth[i], s.SlotTrail[i] ? Fixed.Zero : s.SlotLateral[i]));
            var gk = k >= 0 && Grid.Contains(s.Goal) && r.Passable(k, r.Index(s.Goal));
            bool Ok(Cell c, Vec at) => k >= 0 && Grid.Contains(c) && r.Passable(k, r.Index(c)) && !_finalCells.Contains(c) && (!gk || r.Clear(k, g, at));
            var at = Cell.Of(p);
            if (!Ok(at, p))
            {
                var found = false;
                for (var ring = 1; ring <= 6 && !found; ring++)
                {
                    long bestD = long.MaxValue;
                    var best = at;
                    for (var dy = -ring; dy <= ring; dy++)
                    {
                        for (var dx = -ring; dx <= ring; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                            {
                                continue;
                            }
                            var c = new Cell(at.X + dx, at.Y + dy);
                            if (!Grid.Contains(c))
                            {
                                continue;
                            }
                            var d = (c.Center() - p).LenSq();
                            if ((d < bestD || (d == bestD && r.Index(c) < r.Index(best))) && Ok(c, c.Center()))
                            {
                                (bestD, best) = (d, c);
                            }
                        }
                    }
                    if (bestD != long.MaxValue)
                    {
                        (found, at, p) = (true, best, best.Center());
                    }
                }
            }
            _finalCells.Add(at);
            pts[i] = p;
        }
        s.FinalPts = pts;
    }

    // Follows: u walks to his slot now: on the map, able to move, not fighting on his own (a chase, a walk back, a stop to
    // shoot), set up for nothing, and either idle or on the squad's move.
    private static bool Follows(Unit u, Squad s) =>
        !s.Loose && u.Alive && u.Inside == 0 && u.Garrison == 0 && u.CanAct && u.Def.Speed.Raw > 0 && u.Ai == AiState.None && !u.SquadHalt &&
        u.Deploy == DeployState.Stowed && u.Life is not { Busy: true } && // INF-E2a: carrying, treating or scavenging: his own walk
        u.Fight is not ({ HasSpot: true } or { Vehicle: not 0 } or { Reflex: true }) && // INF-E1b-2: his own spot, a vehicle's lee, the reflex

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

    // WaitsForMembers is the percent of its pace a moving squad walks at: 0 (it waits) while a member fights on its attack-move
    // (a chase or a stop to shoot); else by the worst laggard behind his slot (not one ahead of it, nor one near his final
    // position): 100 within half a cell; from there down to the rules' regroup pace at the regroup distance and beyond (1213: a
    // laggard short of the regroup distance once kept his lag for good, the squad walking at his own, the slowest, speed: the
    // Kornet team 1.4 cells off his slot; within half the regroup distance only one who cannot outwalk the pace counts), for at most the rules' regroup ticks counted while one is over half the regroup
    // distance behind (INF-MOVE: it slows, never stops); then 100 until all are within half of it again.
    private int WaitsForMembers(Squad s, SquadRules rules)
    {
        if (s.Mind is { HasLine: true })
        {
            return 0; // INF-E1b-3: the squad fights from its line; the line, not the anchor, moves in an Advance
        }
        var half = Fixed.FromRaw(Fixed.OneRaw / 2);
        var pace = SquadSpeed(s, rules).Raw;
        long worst = 0;
        for (var i = 0; i < s.MemberList.Count; i++)
        {
            var u = Unit(s.MemberList[i])!;
            if (s.Order == OrderKind.AttackMove && u.Inside == 0 && u.Garrison == 0 && (u.SquadHalt || u.Ai != AiState.None))
            {
                return 0; // fighting: the squad holds its ground until he is done
            }
            if (Follows(u, s) && SlotPoint(s, i) is var slot && !Within(u.Pos, slot, half) && Abreast(s, i, u.Pos, slot) is null &&
                !(FinalOf(s, i) is { } fin && Within(u.Pos, fin, rules.DirectRange)))
            {
                var behind = (u.Pos - slot).Len().Raw; // behind his slot (one ahead of it walks on slowly instead)
                if (behind > rules.Regroup.Raw / 2 || u.Def.Speed.Raw * 10 < pace * 11)
                {
                    worst = Math.Max(worst, behind); // (closer, only one who cannot outwalk the squad's pace by a tenth to catch up)
                }
            }
        }
        var near = worst <= rules.Regroup.Raw / 2;
        if (near)
        {
            s.Waited = 0; // (INF-MOVE: only once all are within half the regroup distance, so a slowed squad is not slowed again at once)
        }
        if (worst <= half.Raw || s.Waited >= rules.RegroupTicks)
        {
            return 100; // together; or it slowed long enough: on, the laggard on his own route
        }
        if (!near)
        {
            s.Waited++;
        }
        var span = rules.Regroup.Raw - half.Raw;
        var over = Math.Min(worst - half.Raw, span);
        return (int)(100 - over * (100 - rules.RegroupPercent) / Math.Max(1, span));
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
        SmoothRoute(r, k, from, _cells);
        if (from != r.Index(here) && (_cells.Count == 0 || _cells[0] != from))
        {
            _cells.Insert(0, from); // first onto ground it fits
        }
        s.PathCells = ToCells(r, _cells, 0);
        s.PathIdx = 0;
        if (s.Places.Count == 0)
        {
            // A new order: the shape faces the goal (straight: the first legs of a route may jog round a sector's entrance), and
            // the slots go by where the members stand along that way.
            var way = s.Goal.Center() - s.Anchor;
            if (way.LenSq() >= Fixed.OneRaw * Fixed.OneRaw)
            {
                s.Facing = way.WithLen(Fixed.One);
            }
            var tail = Behind(s, Fixed.FromRaw(Math.Max(s.MaxTrailDepth.Raw, Content.Squad!.MarchSpacing.Raw))) - s.Anchor;
            if (s.Trail.Count > 1 && tail.X.Raw * s.Facing.X.Raw + tail.Y.Raw * s.Facing.Y.Raw > 0)
            {
                // The old trail lies ahead (sent back the way it came): the file would fold back along it, so it starts over,
                // straight back from the anchor.
                s.Trail.Clear();
                s.Trail.Add(s.Anchor);
            }
            Rank(s);
            BuildSlots(s, Content.Squad!); // by place, then by the least total way to the final positions (ByPlace)
            LeadOff(s);
        }
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
    private void WalkAnchor(Router r, Squad s, SquadRules rules, int pace)
    {
        var k = SquadClass(r, s);
        if (k >= 0 && s.RouteIdx < s.Route.Length && s.PathCells.Length - s.PathIdx < RefineWhen)
        {
            _cells.Clear();
            var from = s.PathCells.Length > 0 ? r.Index(s.PathCells[^1]) : r.Index(Cell.Of(s.Anchor));
            var idx = s.RouteIdx;
            if (r.Refine(k, from, s.Route, ref idx, RefineAhead, _cells))
            {
                s.RouteIdx = idx;
                var left = s.PathCells.Length - s.PathIdx;
                if (left > 0)
                {
                    // Straightened again from the anchor's next waypoint, the last chunk's end with it (ExtendStraight).
                    for (var i = left - 1; i >= 1; i--)
                    {
                        _cells.Insert(0, r.Index(s.PathCells[s.PathIdx + i]));
                    }
                    SmoothRoute(r, k, r.Index(s.PathCells[s.PathIdx]), _cells);
                    _cells.Insert(0, r.Index(s.PathCells[s.PathIdx]));
                    left = 0;
                }
                else
                {
                    SmoothRoute(r, k, from, _cells);
                }
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
        var speed = Fixed.FromRaw(Math.Max(1, SquadSpeed(s, rules).Raw * pace / 100));
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
        ClearSquadPath(s, keep: true); // INF-MOVE: at rest everyone keeps his final position
        _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Arrived, s.Leader, 0));
        if (s.Mode == SquadMode.Hold && s.HoldPlaced)
        {
            Settle(s, s.HoldSpot.Center(), s.HoldFacing); // INF-MOVE: its cells were picked at the order, its members on their way to them
        }
        else if (s.Mode == SquadMode.Hold && Content.Squad?.Hold is { } hold)
        {
            StartHoldPick(s, hold); // picked in a squad pass (this one, or a later one when many wait)
        }
    }

    // ---- Steering (the path system's loop) ----

    // SteerSquad writes a following member's intent: straight to his slot when its cell fits his class and the way there is
    // clear; otherwise along a route of his own toward it (or toward the anchor when the slot's cell doesn't fit), re-planned at
    // most once a second.
    private void SteerSquad(Router r, Unit u)
    {
        if (u.SquadPace > 0)
        {
            // INF-MOVE: the squad's pace for him this tick, on his own route too (the movement's convoy cap: the lower one).
            (u.ConvoyCap, u.ConvoyTick) = (u.ConvoyTick == Tick ? Math.Min(u.ConvoyCap, u.SquadPace) : u.SquadPace, Tick);
        }
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
            var speed = u.SquadPace > 0 && u.SquadPace < u.Def.Speed.Raw ? Fixed.FromRaw(u.SquadPace) : u.Def.Speed;
            var step = d.LenSq() <= speed.Raw * speed.Raw ? d : d.WithLen(speed);
            var pushedOff = u.Intent.Arrived && d.LenSq() > Fixed.OneRaw * Fixed.OneRaw; // shoved a cell off a slot it reached
            u.Intent = slot == u.Intent.Goal && !pushedOff ? u.Intent with { Desired = step, Slot = default } : new MoveIntent(slot, step, default, false);
            return;
        }
        // (INF-MOVE, 1213: one ahead of his slot whose way to its line is not clear, a wall or a squadmate's back in the way, no longer
        // stands for it: he takes his own route to his point on the line, a cell ahead of level with him, at the creep pace; so
        // the Assault line's men at a wall file through its gap one after another instead of waiting at it.)
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
        var leader = LeaderOf(s, rules); // INF-E2a: the highest rank among those who may lead (ties: the lowest ID; SoldierSystem.cs)
        if (s.Leader != 0 && leader != s.Leader)
        {
            _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Leader, leader, s.Leader));
        }
        s.Leader = leader;
        BuildSlots(s, rules);
        if (s.PickList.Count > 0 || s.HoldPlaced)
        {
            // Hold: the picks of soldiers gone stay behind; one who joined picks the best cell left (the others keep theirs).
            s.PickList.RemoveAll(p => s.MemberList.BinarySearch(p.Unit) < 0);
            s.TaskList.RemoveAll(t => s.MemberList.BinarySearch(t.Unit) < 0); // an ambush's mines a lost soldier was to lay
            foreach (var id in s.MemberList)
            {
                if (HoldPickIndex(s, id) < 0)
                {
                    s.HoldPending = true;
                    break;
                }
            }
        }
    }

    // BuildSlots lays the members out by mode and role. March, from the front: recon ahead along the path, the leader (the
    // anchor), the flanks, the line with the medics in its middle, then the long flanks, the air guards and the support, each
    // the march spacing behind the one before (air guards behind air guards their gap, support behind support packed).
    // Assault: the leader in the middle of the line, the other line soldiers right and left of him on the shorter side, the
    // flanks at its ends; the other roles behind it, each role spread across the facing by its gap round the middle (recon beyond the
    // line's right end, then its left). On the move, once ranked, the members swap these slots by place (ByPlace).
    private void BuildSlots(Squad s, SquadRules rules)
    {
        s.FinalsStale = true; // the final positions follow the slots (PlaceFinals, in the squad pass)
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
        if (s.Mode is SquadMode.March or SquadMode.Hold or SquadMode.Ambush) // Hold and Ambush walk as March; their cells are picked on arrival
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
            ByPlace(s, rules);
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
        ByPlace(s, rules);
    }

    // ---- Slots by place (SQ-ORDER) ----

    // Rank notes where every member stands now from the anchor, along the squad's facing and across it (Squad.Places): the
    // slots then go by it until the order ends.
    private void Rank(Squad s)
    {
        s.Places.Clear();
        s.OrderFrom = s.Anchor;
        s.OrderFacing = s.Facing;
        foreach (var id in s.MemberList)
        {
            s.Places.Add(PlaceOf(s, Unit(id)!));
        }
    }

    // PlaceOf is where u stands from the anchor: along the facing (ahead positive) and across it (right positive), raw (INF-MOVE:
    // exact, for the least total way; ByPlace ranks by them in half cells to the nearest, so that soldiers about level along the
    // move, a file standing across it, tie and role order decides).
    private static (uint Unit, int Along, int Across) PlaceOf(Squad s, Unit u)
    {
        var d = u.Pos - s.Anchor;
        var f = s.Facing;
        var along = (d.X.Raw * f.X.Raw + d.Y.Raw * f.Y.Raw) >> Fixed.Shift;
        var across = (d.X.Raw * -f.Y.Raw + d.Y.Raw * f.X.Raw) >> Fixed.Shift;
        return (u.Id, (int)along, (int)across);
    }

    // HalfCells rounds a raw length to half cells, to the nearest.
    private static int HalfCells(int raw)
    {
        const int half = Fixed.Shift - 1;
        return (int)((raw + (1L << (half - 1))) >> half);
    }

    // ByPlace hands the slots BuildSlots laid out by role to the members by where they stood (Squad.Places), once the squad
    // is ranked (on the move). Front to back, the members most ahead take the forward slots, with three limits that keep the
    // roles sensible: the recon slots go to the recon; the anchor's slot (the head of the March file, the middle of the Assault
    // line) only to a soldier who may lead (no medic, recon or support at the very front); the rest of the Assault line only to
    // a soldier whose role stands in it (no rear role in the line). Slots at one depth go right to left to the members from the
    // right. Ties: role order (the slot his role would have), then ID. The depths and gaps stay the role layout's: the members
    // swap slots. A soldier who joined on the move ranks last.
    private void ByPlace(Squad s, SquadRules rules)
    {
        if (s.Places.Count == 0)
        {
            return;
        }
        var n = s.MemberList.Count;
        s.Places.RemoveAll(p => s.MemberList.BinarySearch(p.Unit) < 0); // members gone
        foreach (var id in s.MemberList)
        {
            if (PlaceIndex(s, id) is < 0 and var at)
            {
                s.Places.Insert(~at, PlaceOf(s, Unit(id)!) with { Along = int.MinValue }); // joined on the move: last
            }
        }
        var depth = (Fixed[])s.SlotDepth.Clone();
        var lateral = (Fixed[])s.SlotLateral.Clone();
        var trail = (bool[])s.SlotTrail.Clone();
        var along = new int[n];
        var across = new int[n];
        var rawAlong = new int[n];
        var rawAcross = new int[n];
        var slots = new List<int>(n);
        var people = new List<int>(n);
        for (var i = 0; i < n; i++)
        {
            (_, rawAlong[i], rawAcross[i]) = s.Places[PlaceIndex(s, s.MemberList[i])];
            (along[i], across[i]) = rawAlong[i] == int.MinValue ? (int.MinValue, 0) : (HalfCells(rawAlong[i]), HalfCells(rawAcross[i]));
            slots.Add(i);
            people.Add(i);
        }
        bool Recon(int i) => s.Roles[i] == SquadRole.Recon;
        // Slots front to back (recon before the others at one depth), right to left; members most ahead first.
        slots.Sort((a, b) => depth[a] != depth[b] ? depth[a].Raw.CompareTo(depth[b].Raw)
            : Recon(a) != Recon(b) ? Recon(b).CompareTo(Recon(a))
            : lateral[a] != lateral[b] ? lateral[b].Raw.CompareTo(lateral[a].Raw) : a.CompareTo(b));
        people.Sort((a, b) => along[a] != along[b] ? along[b].CompareTo(along[a])
            : depth[a] != depth[b] ? depth[a].Raw.CompareTo(depth[b].Raw)
            : lateral[a] != lateral[b] ? lateral[b].Raw.CompareTo(lateral[a].Raw) : a.CompareTo(b));
        var assault = s.Mode == SquadMode.Assault;
        bool May(int i, int slot) =>
            Recon(slot) ? Recon(i)
            : depth[slot].Raw > 0 ? !Recon(i)
            : !Recon(i) && rules.Leads(s.Roles[i]) && (!assault || depth[i].Raw <= 0);
        var taken = new bool[n];
        var slotOf = new int[n];
        var group = new List<int>();
        for (var g = 0; g < slots.Count;)
        {
            var e = g;
            while (e < slots.Count && depth[slots[e]] == depth[slots[g]] && Recon(slots[e]) == Recon(slots[g]))
            {
                e++;
            }
            group.Clear();
            for (var pass = 0; pass < 2 && group.Count < e - g; pass++)
            {
                foreach (var i in people)
                {
                    if (group.Count == e - g)
                    {
                        break;
                    }
                    if (!taken[i] && (pass == 1 || May(i, slots[g])))
                    {
                        taken[i] = true; // (the second pass, never needed by the role layout: anyone left)
                        group.Add(i);
                    }
                }
            }
            group.Sort((a, b) => across[a] != across[b] ? across[b].CompareTo(across[a])
                : lateral[a] != lateral[b] ? lateral[b].Raw.CompareTo(lateral[a].Raw) : a.CompareTo(b));
            for (var k = 0; k < group.Count; k++)
            {
                slotOf[group[k]] = slots[g + k];
            }
            g = e;
        }
        // INF-MOVE: then the final positions (the slots in the layout at the goal) by the least ways from where each stood at the
        // order (LeastWays; the roles' limits kept; a soldier who joined on the move keeps his). Swaps stay within a role group
        // (1213): the line and the flank among themselves, the rear roles (medic, recon, the long flank, air guard, support)
        // among themselves, never into the line; the head still only to one who may lead (May). And only between members who walk
        // straight to their final positions from the order (within the direct range of the goal, or all once the anchor is): one
        // farther joins the March or Assault shape on the trail first, so his straight way is not the one he walks and the shape's
        // order by place is his (1213: swapping by those ways put the support ahead of the air guard who stood nearer the path).
        var f = s.OrderFacing;
        var right = new Vec(-f.Y, f.X);
        var from = new Vec[n];
        for (var i = 0; i < n; i++)
        {
            from[i] = s.OrderFrom + Scale(f, Fixed.FromRaw(rawAlong[i])) + Scale(right, Fixed.FromRaw(rawAcross[i]));
        }
        var goal = s.Goal.Center();
        var allNear = Within(s.OrderFrom, goal, rules.DirectRange);
        bool Near(int i) => allNear || Within(from[i], goal, rules.DirectRange);
        LeastWays(n, from, from, i => LayoutAt(s, depth[slotOf[i]], trail[slotOf[i]] ? Fixed.Zero : lateral[slotOf[i]]),
            (a, b) => along[a] != int.MinValue && along[b] != int.MinValue && Rear(s.Roles[a]) == Rear(s.Roles[b]) && Near(a) && Near(b) &&
                May(a, slotOf[b]) && May(b, slotOf[a]),
            (a, b) => (slotOf[a], slotOf[b]) = (slotOf[b], slotOf[a]));
        for (var i = 0; i < n; i++)
        {
            var slot = slotOf[i];
            s.SlotDepth[i] = depth[slot];
            s.SlotLateral[i] = lateral[slot];
            s.SlotTrail[i] = trail[slot];
        }
    }

    // Rear: a rear role (1213: the medic, recon, the long flank, the air guard, the support), against the line and the flank,
    // who form the file's front and the Assault line. LeastWays swaps members only within one of the two groups.
    internal static bool Rear(SquadRole r) => r is not (SquadRole.Line or SquadRole.Flank);

    // LeastWays hands n members' targets round (INF-MOVE: the owner's "go to the intended position from the first move"): pairs
    // a < b that may swap do while that lowers the sum of their squared straight ways from `ranked` (where they stood, as ranked:
    // so nobody overtakes another, the foremost take the foremost targets, as by place) by a 64th of a cell or more; then while
    // their straight ways from `from` (exactly where they stood) cross (uncrossing shortens them, so it ends). Each pass in pair
    // order, until
    // none swaps (at most n x n passes each): deterministic.
    internal static void LeastWays(int n, Vec[] ranked, Vec[] from, Func<int, Vec> target, Func<int, int, bool> may, Action<int, int> swap)
    {
        long Sq(int i, Vec t) => (t - ranked[i]).LenSq();
        for (var phase = 0; phase < 2; phase++)
        {
            for (var pass = 0; pass < n * n; pass++)
            {
                var swapped = false;
                for (var a = 0; a < n; a++)
                {
                    for (var b = a + 1; b < n; b++)
                    {
                        if (!may(a, b))
                        {
                            continue;
                        }
                        var (ta, tb) = (target(a), target(b));
                        var better = phase == 0
                            ? Sq(a, tb) + Sq(b, ta) + Fixed.OneRaw * Fixed.OneRaw / 64 <= Sq(a, ta) + Sq(b, tb)
                            : Crosses(from[a], ta, from[b], tb);
                        if (better)
                        {
                            swap(a, b);
                            swapped = true;
                        }
                    }
                }
                if (!swapped)
                {
                    break;
                }
            }
        }
    }

    // Crosses: the segments ab and cd cross (each one's ends strictly on both sides of the other's line).
    internal static bool Crosses(Vec a, Vec b, Vec c, Vec d)
    {
        static int Side(Vec p, Vec q, Vec r) =>
            Math.Sign((q.X.Raw - p.X.Raw) * (r.Y.Raw - p.Y.Raw) - (q.Y.Raw - p.Y.Raw) * (r.X.Raw - p.X.Raw));
        return Side(a, b, c) * Side(a, b, d) < 0 && Side(c, d, a) * Side(c, d, b) < 0;
    }

    // LeadOff: on a new order the anchor starts as far along its path as it can without leaving any member behind his slot (the
    // least that a following member stands ahead of his slot along the facing, from half a cell up): a squad sent back the way it
    // came turns about where it stands, its rear man the new head, instead of standing while the anchor walks through it.
    private void LeadOff(Squad s)
    {
        var lead = long.MaxValue;
        var f = s.Facing;
        for (var i = 0; i < s.MemberList.Count; i++)
        {
            var u = Unit(s.MemberList[i])!;
            if (!Follows(u, s))
            {
                continue;
            }
            var d = u.Pos - SlotPoint(s, i);
            lead = Math.Min(lead, (d.X.Raw * f.X.Raw + d.Y.Raw * f.Y.Raw) >> Fixed.Shift);
        }
        if (lead == long.MaxValue || lead < Fixed.OneRaw / 2)
        {
            return;
        }
        var p = s.Anchor;
        while (lead > 0 && s.PathIdx < s.PathCells.Length)
        {
            var leg = s.PathCells[s.PathIdx].Center() - p;
            var len = leg.Len().Raw;
            if (len <= lead)
            {
                p = s.PathCells[s.PathIdx].Center();
                lead -= len;
                s.PathIdx++;
                continue;
            }
            p += leg.WithLen(Fixed.FromRaw(lead));
            lead = 0;
        }
        s.Anchor = p;
        s.Trail.Clear();
        s.Trail.Add(p);
    }

    // PlaceIndex finds unit in s.Places (by ID), or the bitwise complement of where it would go.
    private static int PlaceIndex(Squad s, uint unit)
    {
        int lo = 0, hi = s.Places.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (s.Places[mid].Unit < unit)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < s.Places.Count && s.Places[lo].Unit == unit ? lo : ~lo;
    }

    // Abreast: while the squad moves, a member whose slot is behind him along the way the slot moves (SlotHeading) does not
    // walk back to it: he goes to the point level with him on the slot's line and lets the slot come to him. Returns that
    // point, or null when the slot is not behind him (or the squad is not moving).
    private Vec? Abreast(Squad s, int i, Vec pos, Vec slot)
    {
        if (s.Order == OrderKind.Idle || i >= s.SlotTrail.Length)
        {
            return null;
        }
        var h = SlotHeading(s, i);
        var d = slot - pos;
        var along = (d.X.Raw * h.X.Raw + d.Y.Raw * h.Y.Raw) >> Fixed.Shift;
        if (along >= 0)
        {
            return null;
        }
        return OnMap(slot - Scale(h, Fixed.FromRaw(along)));
    }

    // SlotHeading is the way member i's slot moves (unit length): along the trail toward the anchor where his March slot lies;
    // the facing for the anchor, recon ahead and Assault's slots.
    private static Vec SlotHeading(Squad s, int i)
    {
        if (!s.SlotTrail[i] || s.SlotDepth[i].Raw <= 0)
        {
            return s.Facing;
        }
        BehindAt(s, s.SlotDepth[i], out var dir);
        return dir;
    }

    // SlotPoint is where member i of s stands now: along the trail (March: behind the anchor; ahead along the path when its
    // depth is negative) or fixed to the anchor and facing (Assault), kept on the map.
    private Vec SlotPoint(Squad s, int i)
    {
        Vec p;
        if (s.Order == OrderKind.Idle && FinalOf(s, i) is { } fin)
        {
            return fin; // at rest: his Hold (or Ambush) cell, or his final position from the last order (INF-MOVE)
        }
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
        return OnMap(p);
    }

    // OnMap keeps p on the map.
    private Vec OnMap(Vec p)
    {
        var maxX = (long)Grid.Width * Fixed.OneRaw - 1;
        var maxY = (long)Grid.Height * Fixed.OneRaw - 1;
        return new Vec(Fixed.FromRaw(Math.Clamp(p.X.Raw, 0, maxX)), Fixed.FromRaw(Math.Clamp(p.Y.Raw, 0, maxY)));
    }

    // Behind is the point d back along the anchor's trail; past its oldest point, straight on the way the trail came (or
    // behind the facing).
    private static Vec Behind(Squad s, Fixed d) => BehindAt(s, d, out _);

    // BehindAt is Behind, with the way the trail runs there toward the anchor (unit length): the way a slot there moves.
    private static Vec BehindAt(Squad s, Fixed d, out Vec dir)
    {
        var left = d.Raw;
        var prev = s.Anchor;
        for (var t = s.Trail.Count - 1; t >= 0; t--)
        {
            var seg = s.Trail[t] - prev;
            var len = seg.Len().Raw;
            if (len >= left && len > 0)
            {
                dir = new Vec(-seg.X, -seg.Y).WithLen(Fixed.One);
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
        dir = new Vec(-back.X, -back.Y).WithLen(Fixed.One);
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
