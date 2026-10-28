using System.Numerics;
using System.Runtime.InteropServices;
using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Hold and its position picking (docs/design.md, "Squads: Hold and position picking", phase S2). A holding squad scores every free
// cell within the rules' radius of its spot once per order (at most the rules' cell cap, the nearest the spot first), and the
// garrisonable buildings near it as positions too; each member, in the rules' role order and ID order within a role, takes the
// best candidate left for his role: the sum of the factors (cover, trees, sight, open ground, spacing, safety, range, nearness,
// each 0 to 100) times his role's weights, within his role's hard limits (depth behind the spot, distance from teammates). Ties go
// to the lowest cell index, buildings after cells by ID. Integers only, no random draws, a fixed order everywhere. The sight lanes
// use the fog's line of sight (the ground's and the treetops' horizons, the forest crossed); a lane also ends at rock, a building
// or rubble (shots stop there). A few squads pick a tick (HoldPicksPerTick); the others wait their turn.
public sealed partial class World
{
    private const int HoldPicksPerTick = 2; // squads whose cells are picked in one squad pass, in ID order; the rest wait a tick
    private const int HoldEnterTicks = 20; // a garrison pick's member tries his building again every second (phased by ID)

    private int _holdPicks; // squads picked this tick

    /// <summary>Candidate cells scored by Hold's position picking since the world began (benchmarks; not game state).</summary>
    internal long HoldCellsScored { get; private set; }

    // A candidate position: a cell, or a garrisonable building (its point the footprint cell nearest the threat).
    private struct HoldCand
    {
        public Cell C;        // the cell (a building's: the cell next to it on the threat's side, where its lanes start)
        public int Key;       // the tie-break: the cell index (a building: past every cell, by ID)
        public uint Building; // 0 for a cell
        public Vec P;         // where distances are taken from
        public int Cover, Trees, Open, Safety, Near;
        public ulong Mask0, Mask1, Mask2; // each sight lane's cells seen (bit i: step i + 1)
        public int Steps0, Steps1, Steps2; // each lane's steps (out to the squad's longest lane)
        public long AimSq0, AimSq1, AimSq2; // the squared distance to each lane's aim (raw)
        public int AimAt0, AimAt1, AimAt2; // the step of each lane at its aim (0: the cell itself)
        public long DistT;    // to the threat (raw)
        public int Room;      // a building's free places
        public int Range;     // a building's range bonus (raw), else 0
    }

    private readonly List<HoldCand> _holdCands = [];
    private readonly List<(Vec P, SquadRole Role, bool Cell)> _holdTaken = [];
    private (int Dx, int Dy)[] _holdOffsets = [];
    private long _holdOffsetsFor = -1; // the radius (raw) the offsets were built for

    /// <summary>
    /// The member's Hold position (<see cref="Squad.HoldPicks"/>): his cell and whether he holds it from inside a building, or null for
    /// a unit in no holding squad or one whose cells are not picked yet.
    /// </summary>
    public HoldPick? SquadHoldPick(uint unit)
    {
        if (Unit(unit) is not { Squad: not 0 } u || Squad(u.Squad) is not { Mode: SquadMode.Hold, HoldPlaced: true } s)
        {
            return null;
        }
        var i = HoldPickIndex(s, unit);
        return i < 0 ? null : s.PickList[i];
    }

    /// <summary>Whether the member is inside a building as part of his squad's Hold (the building his pick names).</summary>
    public bool SquadHoldGarrisoned(uint unit) =>
        SquadHoldPick(unit) is { Building: not 0 } p && Unit(unit) is { } u && u.Garrison == p.Building;

    /// <summary>
    /// The client's debug view of Hold's position picking: every candidate the member's squad scores (its cells and buildings, in
    /// scoring order) as the member's role scores it, with the teammates' current picks taken (his own left out). Empty for a
    /// unit not in a squad in Hold or content without Hold. Reads the world only.
    /// </summary>
    public IReadOnlyList<HoldCellScore> SquadHoldScores(uint member)
    {
        var res = new List<HoldCellScore>();
        if (Content.Squad is not { Hold: { } hold } || Unit(member) is not { Squad: not 0 } u || Squad(u.Squad) is not { Mode: SquadMode.Hold } s ||
            s.HoldFacing.LenSq() == 0)
        {
            return res;
        }
        var r = SyncRouting();
        HoldCandidates(r, s, hold);
        HoldTaken(s, member);
        var role = SquadRoleOf(u);
        var hr = hold.Roles[(int)role];
        HoldMember(u, out var reach, out var minR, out var lane);
        var back = LineDepth(s, member);
        foreach (ref readonly var c in CollectionsMarshal.AsSpan(_holdCands))
        {
            TakenDist(c, role, out var dmin, out var dsame);
            var (score, f, ok) = HoldScore(u, hr, c, reach, minR, lane, back, strict: true, all: true, dmin, dsame);
            res.Add(new HoldCellScore(c.C, c.Building, score, f.Cover, f.Trees, f.Sight, f.Open, f.Spacing, f.Safety, f.Range, f.Near, ok));
        }
        return res;
    }

    // BenchHoldPick (benchmarks only): picks holding squad s's cells again now, as its order did, and returns the cells it scored.
    internal int BenchHoldPick(Squad s)
    {
        s.PickList.Clear();
        var before = HoldCellsScored;
        PickHold(SyncRouting(), s, Content.Squad!.Hold!);
        return (int)(HoldCellsScored - before);
    }

    // ---- The order ----

    // HoldOrder: squad s holds round spot facing toward `to` (null or the spot itself: the squad's facing when it gets there). Near
    // the spot already it stops and picks now; farther, it walks there first (as March) and picks on arrival.
    private void HoldOrder(Squad s, Cell spot, Cell? to, SquadRules rules, SquadHoldRules hold)
    {
        if (s.Loose)
        {
            ReAnchor(s);
            s.Loose = false;
        }
        LeaveHold(s);
        s.Mode = SquadMode.Hold;
        BuildSlots(s, rules);
        var near = Within(s.Anchor, spot.Center(), hold.Radius);
        if (near)
        {
            if (s.Order != OrderKind.Idle)
            {
                EndSquadOrder(s);
            }
        }
        else
        {
            var listed = new List<Unit>();
            foreach (var id in s.MemberList)
            {
                if (Unit(id) is { Inside: 0, Garrison: 0 } m)
                {
                    listed.Add(m);
                }
            }
            OrderSquad(s, spot, OrderKind.Move, listed);
        }
        s.HoldSpot = spot;
        s.HoldFaced = false;
        if (to is { } t && t != spot)
        {
            var d = t.Center() - spot.Center();
            var len = Fixed.FromRaw(Math.Clamp(d.Len().Raw, hold.ThreatMin.Raw, hold.ThreatMax.Raw));
            s.HoldFacing = d.WithLen(Fixed.One);
            s.HoldThreat = spot.Center() + Scale(s.HoldFacing, len);
            s.HoldFaced = true;
        }
        if (near)
        {
            StartHoldPick(s, hold);
        }
    }

    // EndSquadOrder: the squad's move ends where it is; its members drop the squad's order.
    private void EndSquadOrder(Squad s)
    {
        foreach (var id in s.MemberList)
        {
            var u = Unit(id)!;
            if (u.Order.Kind == s.Order && u.Order.Cell == s.Goal)
            {
                u.Order = default;
                u.ClearPath();
                u.MoveOn = false;
            }
        }
        s.Order = OrderKind.Idle;
        ClearSquadPath(s);
    }

    // StartHoldPick: the squad (in Hold, no squad order) waits for its cells to be picked. Its spot is where it stands when it
    // ended up away from it (a spot it could not reach); without a facing from the order it takes the way it walked.
    private static void StartHoldPick(Squad s, SquadHoldRules hold)
    {
        if (!Within(s.Anchor, s.HoldSpot.Center(), hold.Radius))
        {
            s.HoldSpot = Cell.Of(s.Anchor);
        }
        if (!s.HoldFaced)
        {
            s.HoldFacing = s.Facing.LenSq() == 0 ? new Vec(Fixed.Zero, -Fixed.One) : s.Facing;
            s.HoldThreat = s.HoldSpot.Center() + Scale(s.HoldFacing, hold.Threat);
        }
        s.PickList.Clear();
        s.HoldPlaced = false;
        s.HoldPending = true;
    }

    // LeaveHold: the squad stops holding (a new order, a new mode): the members it put in buildings come out at the doors, its
    // set-up mortars pack up, and its picks are dropped.
    private void LeaveHold(Squad s)
    {
        if (s.PickList.Count == 0 && !s.HoldPlaced && !s.HoldPending)
        {
            return;
        }
        var n = 0;
        foreach (var p in s.PickList)
        {
            if (p.Building == 0 || Unit(p.Unit) is not { Alive: true } u || u.Squad != s.Id)
            {
                continue;
            }
            if (u.Order.Kind == OrderKind.Enter && u.Order.Target == p.Building)
            {
                u.Idle();
            }
            if (u.Garrison == p.Building && Unit(p.Building) is { } b)
            {
                LeaveBuilding(u, b, NearestDoor(b, s.HoldSpot.Center()), n++);
            }
        }
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { } m && m.Def.Archetype.DeployTicks > 0 && m.Deploy is DeployState.Deployed or DeployState.Deploying && m.Inside == 0)
            {
                StartStow(m);
            }
        }
        s.PickList.Clear();
        s.HoldPlaced = false;
        s.HoldPending = false;
    }

    // HoldPickIndex is the index of unit's pick in s, or -1.
    private static int HoldPickIndex(Squad s, uint unit)
    {
        int lo = 0, hi = s.PickList.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (s.PickList[mid].Unit < unit)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < s.PickList.Count && s.PickList[lo].Unit == unit ? lo : -1;
    }

    // ---- The squad pass ----

    // RunHold: in the squad pass, a squad waiting for its cells picks them (a few a tick); once placed, its members hold: one
    // whose pick is a building goes in again when he is idle outside it and may, a set-up weapon (the mortar) sets up on its cell.
    private void RunHold(Router r, Squad s, SquadRules rules)
    {
        if (rules.Hold is not { } hold || s.Mode != SquadMode.Hold || s.Loose || s.Order != OrderKind.Idle)
        {
            return;
        }
        if (!s.HoldPlaced && !s.HoldPending)
        {
            StartHoldPick(s, hold); // back from orders of its own, or a Hold that never got there
        }
        if (s.HoldPending)
        {
            if (_holdPicks >= HoldPicksPerTick)
            {
                return;
            }
            _holdPicks++;
            PickHold(r, s, hold);
            return;
        }
        foreach (var p in s.PickList)
        {
            if (Unit(p.Unit) is not { Alive: true, Inside: 0, Garrison: 0 } u || u.Squad != s.Id || u.Order.Kind != OrderKind.Idle ||
                u.Ai != AiState.None || !u.CanAct)
            {
                continue;
            }
            if (p.Building != 0)
            {
                if ((Tick + u.Id) % HoldEnterTicks == 0 && Unit(p.Building) is { } b && CanGarrison(u, b))
                {
                    Assign(u, new Order(OrderKind.Enter, default, b.Id, 0));
                }
                continue;
            }
            if (u.Def.Archetype.DeployTicks > 0 && u.Deploy == DeployState.Stowed && !u.Moving && Within(u.Pos, p.Cell.Center(), Fixed.FromRaw(Fixed.OneRaw / 2)) &&
                CanDeployAt(u, p.Cell))
            {
                StartDeploy(u); // the support sets up on its cell and fires on its own
            }
        }
    }

    // PickHold scores the squad's candidates once and gives each member without a pick the best one left for his role, in the
    // rules' role order (ID order within a role); members in a building pick it while it has room. Members on orders of their own
    // or inside a vehicle are placed too (they come to it when done). Buildings picked: the member is sent in.
    private void PickHold(Router r, Squad s, SquadHoldRules hold)
    {
        s.HoldPending = false;
        var scored = HoldCandidates(r, s, hold);
        _holdLanes.Clear();
        HoldCellsScored += scored;
        HoldTaken(s, 0);
        var n = _holdCands.Count;
        if (_holdDmin.Length < n)
        {
            _holdDmin = new long[n];
            _holdDsame = new long[RoleCount * n];
        }
        Array.Fill(_holdDmin, long.MaxValue, 0, n);
        Array.Fill(_holdDsame, long.MaxValue, 0, RoleCount * n);
        foreach (var (at, taker, _) in _holdTaken)
        {
            TakeDist(at, taker, n);
        }
        foreach (var role in hold.Order)
        {
            var hr = hold.Roles[(int)role];
            foreach (var id in s.MemberList)
            {
                var u = Unit(id)!;
                if (SquadRoleOf(u) != role || HoldPickIndex(s, id) >= 0)
                {
                    continue;
                }
                HoldMember(u, out var reach, out var minR, out var lane);
                var back = LineDepth(s, 0);
                var col = SightColumn(lane);
                var best = -1;
                var bestScore = 0;
                for (var pass = 0; pass < 2 && best < 0; pass++)
                {
                    var cands = CollectionsMarshal.AsSpan(_holdCands);
                    for (var i = 0; i < cands.Length; i++)
                    {
                        ref readonly var c = ref cands[i];
                        if (c.Building != 0 && c.Room <= 0)
                        {
                            continue;
                        }
                        var sight = 0;
                        if (hr.Weights.Sight != 0)
                        {
                            ref var memo = ref _holdSight[col * cands.Length + i];
                            if (memo < 0)
                            {
                                memo = Sight(c, lane);
                            }
                            sight = memo;
                        }
                        var (score, _, ok) = HoldScore(u, hr, c, reach, minR, lane, back, strict: pass == 0, all: false, _holdDmin[i],
                            _holdDsame[(int)role * cands.Length + i], sight);
                        if (ok && (best < 0 || score > bestScore || (score == bestScore && c.Key < cands[best].Key)))
                        {
                            (best, bestScore) = (i, score);
                        }
                    }
                }
                HoldPick pick;
                if (best < 0)
                {
                    pick = new HoldPick(id, Cell.Of(u.Pos), 0); // nothing left: he holds where he stands
                }
                else
                {
                    var c = _holdCands[best];
                    if (c.Building != 0)
                    {
                        c.Room--;
                        _holdCands[best] = c;
                        var b = Unit(c.Building)!;
                        pick = new HoldPick(id, DoorCell(b, NearestDoor(b, c.P)), c.Building);
                    }
                    else
                    {
                        pick = new HoldPick(id, c.C, 0);
                    }
                }
                s.PickList.Insert(~s.PickList.BinarySearch(pick, PickById), pick);
                if (pick.Building == 0)
                {
                    TakeDist(pick.Cell.Center(), role, n);
                }
                if (u.Garrison != 0 && u.Garrison != pick.Building && Unit(u.Garrison) is { } other)
                {
                    LeaveBuilding(u, other, NearestDoor(other, pick.Cell.Center()), 0); // in another building: out, to his position
                }
                if (pick.Building != 0 && u.Garrison == 0 && u.Inside == 0 && u.Order.Kind == OrderKind.Idle && Unit(pick.Building) is { } pb &&
                    CanGarrison(u, pb))
                {
                    Assign(u, new Order(OrderKind.Enter, default, pb.Id, 0));
                }
            }
        }
        s.HoldPlaced = true;
        Settle(s, s.HoldSpot.Center(), s.HoldFacing);
        _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Placed, s.Leader, (uint)scored));
    }

    // The sight of each candidate for each distinct lane of a squad's members, worked out once in a picking (-1: not yet).
    private int[] _holdSight = [];
    private readonly List<long> _holdLanes = [];

    // SightColumn is the column of the sight memo for members whose lanes reach `lane`, cleared when it is new.
    private int SightColumn(long lane)
    {
        var col = _holdLanes.IndexOf(lane);
        if (col >= 0)
        {
            return col;
        }
        col = _holdLanes.Count;
        _holdLanes.Add(lane);
        var n = _holdCands.Count;
        if (_holdSight.Length < (col + 1) * n)
        {
            Array.Resize(ref _holdSight, Math.Max((col + 1) * n, _holdSight.Length * 2));
        }
        Array.Fill(_holdSight, -1, col * n, n);
        return col;
    }

    // Per candidate in a picking: the squared distance to the nearest cell taken, and per role to the nearest a member of it took.
    private long[] _holdDmin = [], _holdDsame = [];
    private const int RoleCount = 7; // SquadRole's values

    // TakeDist notes a cell taken at p by a member of role in the picking's distances (n candidates).
    private void TakeDist(Vec p, SquadRole role, int n)
    {
        var cands = CollectionsMarshal.AsSpan(_holdCands);
        var same = (int)role * n;
        for (var i = 0; i < n; i++)
        {
            var d = (p - cands[i].P).LenSq();
            if (d < _holdDmin[i])
            {
                _holdDmin[i] = d;
            }
            if (d < _holdDsame[same + i])
            {
                _holdDsame[same + i] = d;
            }
        }
    }

    // LineDepth is the mean depth (raw, behind the spot along the facing) of s's line soldiers placed so far (but skip), or 0 (the
    // spot) before any: what the roles' least depth behind is measured from.
    private long LineDepth(Squad s, uint skip)
    {
        long sum = 0;
        var n = 0;
        foreach (var p in s.PickList)
        {
            if (p.Unit != skip && Unit(p.Unit) is { } m && SquadRoleOf(m) == SquadRole.Line)
            {
                sum += Depth(p.Cell.Center());
                n++;
            }
        }
        return n == 0 ? 0 : sum / n;
    }

    private static readonly Comparer<HoldPick> PickById = Comparer<HoldPick>.Create((a, b) => a.Unit.CompareTo(b.Unit));

    // HoldTaken gathers the positions already held near s that can matter to its candidates: its own picks (but skip's) and those of
    // the owner's other holding squads, within the radius plus the farthest any role keeps apart or spreads of its spot.
    private void HoldTaken(Squad s, uint skip)
    {
        _holdTaken.Clear();
        var hold = Content.Squad!.Hold!;
        var reach = Fixed.OneRaw;
        foreach (var r in hold.Roles)
        {
            reach = Math.Max(reach, Math.Max(r.Spread.Raw, Math.Max(r.Apart.Raw, r.ApartSame.Raw)));
        }
        var near = Fixed.FromRaw(hold.Radius.Raw + reach);
        var sc = s.HoldSpot.Center();
        foreach (var o in _squads)
        {
            if (o.Owner != s.Owner || o.Mode != SquadMode.Hold || o.PickList.Count == 0 || !Within(o.HoldSpot.Center(), sc, Fixed.FromRaw(3 * near.Raw)))
            {
                continue;
            }
            foreach (var p in o.PickList)
            {
                if (p.Unit == skip || p.Building != 0 || Unit(p.Unit) is not { } m || !Within(p.Cell.Center(), sc, near))
                {
                    continue;
                }
                _holdTaken.Add((p.Cell.Center(), SquadRoleOf(m), true));
            }
        }
    }

    // HoldMember: a member's ground weapon reach (the longest of his weapons that hit the ground) and its minimum, and how far his
    // own sight lanes count (his sight or that reach, the longer; at most the rules' lane), raw.
    private void HoldMember(Unit u, out long reach, out long minR, out long lane)
    {
        reach = 0;
        minR = 0;
        foreach (var w in u.Def.Weapons)
        {
            if ((w.Effect.Targets & Layers.Ground) != 0 && w.Range.Raw > reach)
            {
                reach = w.Range.Raw;
                minR = w.Effect.MinRange.Raw;
            }
        }
        lane = Math.Clamp(Math.Max(u.Def.Vision.Raw, reach), Fixed.OneRaw, Content.Squad!.Hold!.LaneMax.Raw);
    }

    // The factors of one candidate for one member.
    private readonly record struct HoldFactors(int Cover, int Trees, int Sight, int Open, int Spacing, int Safety, int Range, int Near);

    // TakenDist is the squared distance from candidate c to the nearest position taken (HoldTaken, cells only), and to the nearest
    // taken by a member of role.
    private void TakenDist(in HoldCand c, SquadRole role, out long dmin, out long dsame)
    {
        dmin = long.MaxValue;
        dsame = long.MaxValue;
        foreach (var (p, r, cell) in _holdTaken)
        {
            if (!cell)
            {
                continue;
            }
            var d = (p - c.P).LenSq();
            dmin = Math.Min(dmin, d);
            if (r == role)
            {
                dsame = Math.Min(dsame, d);
            }
        }
    }

    // HoldScore scores candidate c for member u (his role's habits hr; dmin and dsame from TakenDist; sight when worked out already,
    // else -1): each factor 0 to 100 times its weight. ok is false when a hard limit rules it out: strict, the role's depth behind
    // backFrom (the line's depth, see LineDepth) and its distances apart; not strict, only another's cell. With all false a candidate
    // ruled out is not scored. A building is only for roles that garrison, soldiers it takes, within the member's reach (plus its
    // bonus) of the threat.
    private (int Score, HoldFactors F, bool Ok) HoldScore(Unit u, SquadHoldRole hr, in HoldCand c, long reach, long minR, long lane, long backFrom,
        bool strict, bool all, long dmin, long dsame, int sight = -1)
    {
        var ok = true;
        var building = c.Building != 0;
        if (building)
        {
            var b = Unit(c.Building)!;
            ok = hr.Garrison && reach > 0 && c.DistT <= reach + c.Range && u.Def.Tags.Matches(b.Def.Archetype.Garrison!.Tags);
        }
        else
        {
            if (dmin < (long)Fixed.OneRaw * Fixed.OneRaw)
            {
                ok = false; // someone holds this cell
            }
            else if (strict)
            {
                ok = dmin >= hr.Apart.Raw * hr.Apart.Raw && dsame >= hr.ApartSame.Raw * hr.ApartSame.Raw;
            }
        }
        if (strict && ok && Depth(c) < backFrom + hr.Back.Raw)
        {
            ok = false;
        }
        if (!ok && !all)
        {
            return (0, default, false); // ruled out: no need to score it
        }
        var spacing = 100;
        if (!building && dmin < hr.Spread.Raw * hr.Spread.Raw)
        {
            spacing = (int)(Fixed.Sqrt(dmin) * 100 / hr.Spread.Raw);
        }
        if (sight < 0)
        {
            sight = Sight(c, lane);
        }
        var range = 0;
        var w = reach + c.Range;
        if (reach > 0 && c.DistT <= w && c.DistT >= minR)
        {
            var best = w * 3 / 4;
            range = (int)Math.Max(0, 100 - Math.Abs(c.DistT - best) * 100 / Math.Max(1, best));
        }
        var f = new HoldFactors(c.Cover, c.Trees, sight, c.Open, spacing, c.Safety, range, c.Near);
        var k = hr.Weights;
        var score = k.Cover * f.Cover + k.Trees * f.Trees + k.Sight * f.Sight + k.Open * f.Open + k.Spacing * f.Spacing + k.Safety * f.Safety +
            k.Range * f.Range + k.Near * f.Near;
        return (score, f, ok);
    }

    // Depth is how far candidate c stands behind the spot along the facing (raw; negative: ahead), for the squad whose candidates
    // the scratch holds.
    private long Depth(in HoldCand c) => Depth(c.P);

    private long Depth(Vec p) => ((_holdSc.X.Raw - p.X.Raw) * _holdF.X.Raw + (_holdSc.Y.Raw - p.Y.Raw) * _holdF.Y.Raw) >> Fixed.Shift;

    private Vec _holdSc, _holdF; // the scratch's squad: its spot's centre and its facing

    // ---- Candidates ----

    // HoldCandidates fills the scratch with s's candidates: the free cells within the radius of its spot (at most the rules' cap
    // of cells looked at, the nearest first: by distance, then row, then column) that its members can reach from where its
    // leader stands, then the garrisonable buildings near the spot that its side may enter. Returns how many cells it looked at.
    private int HoldCandidates(Router r, Squad s, SquadHoldRules hold)
    {
        _holdCands.Clear();
        if (_holdOffsetsFor != hold.Radius.Raw)
        {
            _holdOffsets = HoldOffsets(hold.Radius.Raw);
            _holdOffsetsFor = hold.Radius.Raw;
        }
        var spot = s.HoldSpot;
        var sc = spot.Center();
        var f = s.HoldFacing;
        var t = s.HoldThreat;
        var radius = hold.Radius.Raw;
        (_holdSc, _holdF) = (sc, f);
        var right = new Vec(-f.Y, f.X);
        var aims = (t, t + Scale(right, hold.LaneSpread), t - Scale(right, hold.LaneSpread));
        long laneMax = Fixed.OneRaw;
        foreach (var id in s.MemberList)
        {
            HoldMember(Unit(id)!, out _, out _, out var lane);
            laneMax = Math.Max(laneMax, lane);
        }
        _holdLane = laneMax;
        HoldSightRules();
        var k = SquadClass(r, s);
        var comp = 0;
        if (k >= 0 && HoldFrom(s) is { } from)
        {
            var at = r.NearestPassable(k, Cell.Of(from.Pos), StartSearch);
            comp = at < 0 ? 0 : r.Component(k, at);
        }
        var looked = 0;
        foreach (var (dx, dy) in _holdOffsets)
        {
            if (looked >= hold.Cells)
            {
                break;
            }
            var c = new Cell(spot.X + dx, spot.Y + dy);
            if (!Grid.Contains(c))
            {
                continue;
            }
            looked++;
            if (Grid.At(c) != Terrain.Clear || (k >= 0 && (!r.Passable(k, r.Index(c)) || (comp != 0 && r.Component(k, r.Index(c)) != comp))))
            {
                continue;
            }
            var cand = new HoldCand { C = c, Key = c.Y * Grid.Width + c.X, P = c.Center() };
            HoldFill(ref cand, c, hold, aims, laneMax, sc, radius, true);
            _holdCands.Add(cand);
        }
        foreach (var b in _units)
        {
            if (b.Def.Archetype.Garrison is not { } g || !b.Alive || !b.IsBuilt || (b.Owner != s.Owner && (b.Owner != 0 || g.Owned)))
            {
                continue;
            }
            var o = FootprintOrigin(b);
            int w = FootW(b), h = FootH(b);
            var tc = Cell.Of(t);
            var edge = new Cell(Math.Clamp(tc.X, o.X, o.X + w - 1), Math.Clamp(tc.Y, o.Y, o.Y + h - 1));
            var nearSpot = new Cell(Math.Clamp(spot.X, o.X, o.X + w - 1), Math.Clamp(spot.Y, o.Y, o.Y + h - 1));
            if (!Within(nearSpot.Center(), sc, hold.Radius))
            {
                continue;
            }
            var p = edge.Center();
            var g0 = Ground;
            var eye = (g0?.HeightAt(p).Raw ?? 0) + WindowRaw;
            var aim = (g0?.HeightAt(t).Raw ?? 0) + BodyRaw;
            if (!LineOfSight(p, Fixed.FromRaw(eye), t, Fixed.FromRaw(aim)))
            {
                continue; // no sight of the threat from its windows on that side
            }
            var room = CapacityOf(b, g) - CountOccupants(b);
            foreach (var p2 in s.PickList)
            {
                if (p2.Building == b.Id && Unit(p2.Unit) is { } m && m.Garrison != b.Id)
                {
                    room--; // promised to a member on his way in
                }
            }
            if (room <= 0)
            {
                continue;
            }
            var (sx, sy) = Octant(t - p);
            var outside = new Cell(edge.X + sx, edge.Y + sy);
            var lanesFrom = Grid.Contains(outside) && Grid.At(outside) == Terrain.Clear ? outside : edge;
            var cand = new HoldCand
            {
                C = lanesFrom, Key = int.MaxValue / 2 + (int)Math.Min(b.Id, int.MaxValue / 2), Building = b.Id, P = p, Room = room,
                Range = (int)g.RangeBonus.Raw,
            };
            HoldFill(ref cand, lanesFrom, hold, aims, laneMax, sc, radius, false);
            cand.P = p;
            cand.Cover = 100;
            cand.Trees = 0;
            cand.DistT = (t - p).Len().Raw;
            cand.Safety = Safety(p, sc, f, radius);
            cand.Near = Near(p, sc, radius);
            _holdCands.Add(cand);
        }
        return looked;
    }

    // HoldFrom is the member the squad's reachable cells are taken from: its leader on the map, else the first member on it.
    private Unit? HoldFrom(Squad s)
    {
        if (Unit(s.Leader) is { Inside: 0, Garrison: 0 } l)
        {
            return l;
        }
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Inside: 0, Garrison: 0 } m)
            {
                return m;
            }
        }
        return null;
    }

    private long _holdLane = Fixed.OneRaw; // the scratch's lanes: how far they were walked (raw)

    // Sight scores a candidate's lanes for a member whose own lanes reach `lane` (raw): half how much of the lanes out to his reach
    // he sees, half how many of the three aims (the threat and either side of it) he sees within his reach.
    private int Sight(in HoldCand c, long lane)
    {
        var scale = (lane << 16) / _holdLane; // his share of the scratch's lanes, 16 fraction bits
        var laneSq = lane * lane;
        long seen = 0, total = 0;
        var aims = 0;
        void One(ulong mask, int steps, long aimSq, int aimAt)
        {
            if (steps == 0)
            {
                return;
            }
            var k = (int)Math.Clamp((steps * scale + (1 << 15)) >> 16, 1, steps);
            seen += BitOperations.PopCount(k >= 64 ? mask : mask & ((1UL << k) - 1));
            total += k;
            if (aimSq <= laneSq && (aimAt == 0 || (aimAt <= steps && (mask >> (aimAt - 1) & 1) != 0)))
            {
                aims++;
            }
        }
        One(c.Mask0, c.Steps0, c.AimSq0, c.AimAt0);
        One(c.Mask1, c.Steps1, c.AimSq1, c.AimAt1);
        One(c.Mask2, c.Steps2, c.AimSq2, c.AimAt2);
        var lanes = total == 0 ? 0 : (int)(seen * 100 / total);
        return (lanes + aims * 100 / 3) / 2;
    }

    // HoldFill works out a candidate's own factors (whoever takes it): cover from the threat, canopy, the sight lanes and how open
    // they are, safety and nearness.
    private void HoldFill(ref HoldCand cand, Cell c, SquadHoldRules hold, (Vec, Vec, Vec) aims, long laneMax, Vec sc, long radius, bool cover)
    {
        var p = c.Center();
        var t = aims.Item1;
        cand.DistT = (t - p).Len().Raw;
        var g = Ground;
        var density = g?.Forest(c) ?? 0;
        if (cover)
        {
            var points = 0;
            var (sx, sy) = Octant(t - p);
            if (sx != 0 || sy != 0)
            {
                bool wall = false, trunk = false;
                ReadOnlySpan<(int, int)> near = [(sx, sy), (Math.Sign(sx - sy), Math.Sign(sx + sy)), (Math.Sign(sx + sy), Math.Sign(sy - sx))];
                foreach (var (nx, ny) in near)
                {
                    var n = new Cell(c.X + nx, c.Y + ny);
                    if (!Grid.Contains(n))
                    {
                        continue;
                    }
                    wall |= Grid.At(n) == Terrain.Blocked;
                    trunk |= g is not null && (g.HasTrunk(n) || g.Fallen(n));
                }
                points += (wall ? hold.CoverWall : 0) + (trunk ? hold.CoverTrunk : 0);
            }
            points += density == 3 ? hold.CoverDense : density == 2 ? hold.CoverGrove : 0;
            if (CrestHides(c, t))
            {
                points += hold.CoverCrest;
            }
            cand.Cover = Math.Min(100, points);
            cand.Trees = (Content.Concealment is { } cr ? cr.Covers(density) : density >= 2) ? 100 : 0;
        }
        var (m0, s0, o0, a0, q0) = HoldLane(c, aims.Item1, laneMax, cand.DistT);
        var (m1, s1, o1, a1, q1) = HoldLane(c, aims.Item2, laneMax, -1);
        var (m2, s2, o2, a2, q2) = HoldLane(c, aims.Item3, laneMax, -1);
        (cand.Mask0, cand.Mask1, cand.Mask2) = (m0, m1, m2);
        (cand.Steps0, cand.Steps1, cand.Steps2) = (s0, s1, s2);
        (cand.AimAt0, cand.AimAt1, cand.AimAt2) = (a0, a1, a2);
        (cand.AimSq0, cand.AimSq1, cand.AimSq2) = (q0, q1, q2);
        var steps = s0 + s1 + s2;
        cand.Open = steps == 0 ? 0 : Math.Min(100, (o0 + o1 + o2) * 100 / (2 * steps));
        cand.Safety = Safety(p, sc, _holdF, radius);
        cand.Near = Near(p, sc, radius);
    }

    // Safety: 0 at the radius ahead of the spot along the facing, 100 at the radius behind it.
    private static int Safety(Vec p, Vec sc, Vec f, long radius)
    {
        var depth = ((sc.X.Raw - p.X.Raw) * f.X.Raw + (sc.Y.Raw - p.Y.Raw) * f.Y.Raw) >> Fixed.Shift;
        return (int)Math.Clamp((depth + radius) * 100 / (2 * radius), 0, 100);
    }

    // Near: 100 on the spot, 0 at the radius.
    private static int Near(Vec p, Vec sc, long radius) => (int)Math.Clamp(100 - (p - sc).Len().Raw * 100 / radius, 0, 100);

    // Octant is the step (each -1, 0 or 1) of the 8 directions nearest to v; (0, 0) for none.
    private static (int, int) Octant(Vec v)
    {
        long ax = Math.Abs(v.X.Raw), ay = Math.Abs(v.Y.Raw);
        var sx = ax * 5 >= ay * 2 ? Math.Sign(v.X.Raw) : 0;
        var sy = ay * 5 >= ax * 2 ? Math.Sign(v.Y.Raw) : 0;
        return (sx, sy);
    }

    // HoldOffsets lists the cell offsets within radius (raw) of a cell's centre, nearest first (then by row, then by column).
    private static (int, int)[] HoldOffsets(long radius)
    {
        var rc = (int)(radius / Fixed.OneRaw);
        var list = new List<(int, int)>();
        for (var dy = -rc; dy <= rc; dy++)
        {
            for (var dx = -rc; dx <= rc; dx++)
            {
                if (((long)dx * dx + (long)dy * dy) * Fixed.OneRaw * Fixed.OneRaw <= radius * radius)
                {
                    list.Add((dx, dy));
                }
            }
        }
        list.Sort((a, b) =>
        {
            var d = ((long)a.Item1 * a.Item1 + (long)a.Item2 * a.Item2).CompareTo((long)b.Item1 * b.Item1 + (long)b.Item2 * b.Item2);
            return d != 0 ? d : a.Item2 != b.Item2 ? a.Item2.CompareTo(b.Item2) : a.Item1.CompareTo(b.Item1);
        });
        return [.. list];
    }

    // ---- Line of sight ----

    // HoldLane walks a sight lane from cell c toward point aim, out to `reach` (raw; at most 64 steps), with the fog's line of sight
    // (a soldier's eye over the cell; the ground's horizon and the treetops' with the forest crossed under its budget), ending at
    // rock, a building or rubble and the map's edge. Returns the steps seen (bit i: step i + 1), the steps, how many of the cells
    // seen are open ground (one point) or road (another), the step at the aim and the squared distance to it. dist is the distance
    // to the aim when the caller has it (else -1: worked out here).
    private (ulong Mask, int Steps, int Open, int AimAt, long AimSq) HoldLane(Cell c, Vec aim, long reach, long dist)
    {
        var p = c.Center();
        var d = aim - p;
        var aimSq = d.LenSq();
        if (aimSq == 0)
        {
            d = _holdF;
        }
        var len = aimSq == 0 ? Fixed.OneRaw : dist >= 0 ? dist : Fixed.Sqrt(aimSq);
        long ex = d.X.Raw * reach / len, ey = d.Y.Raw * reach / len; // the lane: reach long, toward the aim
        var major = Math.Max(Math.Abs(ex), Math.Abs(ey));
        var steps = (int)Math.Min(64, (major + Fixed.OneRaw / 2) / Fixed.OneRaw);
        if (steps <= 0)
        {
            return (0, 0, 0, 0, aimSq);
        }
        var aimAt = aimSq == 0 ? 0 : (int)Math.Min(int.MaxValue, (Math.Max(Math.Abs(d.X.Raw), Math.Abs(d.Y.Raw)) * steps + major / 2) / major);
        long stepX = ex / steps, stepY = ey / steps;
        long x = p.X.Raw, y = p.Y.Raw;
        var g = Ground;
        var w = Grid.Width;
        var h = Grid.Height;
        var cells = Grid.Cells;
        ulong seen = 0;
        var open = 0;
        if (g is null)
        {
            // A flat map: nothing but rock, buildings and rubble stops sight.
            for (var i = 1; i <= steps; i++)
            {
                x += stepX;
                y += stepY;
                int cx = (int)(x >> Fixed.Shift), cy = (int)(y >> Fixed.Shift);
                if (cx < 0 || cy < 0 || cx >= w || cy >= h || cells[cy * w + cx] == Terrain.Blocked)
                {
                    break;
                }
                seen |= 1UL << (i - 1);
                open++;
            }
            return (seen, steps, open, aimAt, aimSq);
        }
        var sums = g.CentreSums;
        var flags = g.FlagBytes;
        var water = g.WaterIds;
        var canopy = _holdCanopy;
        var cost = _holdForest;
        var targetRaw = _holdTarget;
        var budget = _holdBudget;
        var eye = Surface(g, sums, water, c.Y * w + c.X) + _holdEye;
        long groundNum = 0, groundDen = 0, topNum = 0, topDen = 0;
        var forest = 0;
        for (var i = 1; i <= steps; i++)
        {
            x += stepX;
            y += stepY;
            int cx = (int)(x >> Fixed.Shift), cy = (int)(y >> Fixed.Shift);
            if (cx < 0 || cy < 0 || cx >= w || cy >= h)
            {
                break;
            }
            var idx = cy * w + cx;
            if (cells[idx] == Terrain.Blocked)
            {
                break; // rock, a building or rubble: shots stop there
            }
            var surface = Surface(g, sums, water, idx);
            var target = surface + targetRaw - eye;
            if ((groundDen == 0 || target * groundDen >= groundNum * i) && (forest < budget || topDen == 0 || target * topDen >= topNum * i))
            {
                seen |= 1UL << (i - 1);
                var f = flags[idx];
                open += ((f & 0b11) == 0 ? 1 : 0) + ((f & TerrainMap.RoadFlag) != 0 ? 1 : 0);
            }
            var ground = surface - eye;
            if (groundDen == 0 || ground * groundDen > groundNum * i)
            {
                (groundNum, groundDen) = (ground, i);
            }
            var density = flags[idx] & 0b11;
            var top = surface + canopy[density] - eye;
            if (topDen == 0 || top * topDen > topNum * i)
            {
                (topNum, topDen) = (top, i);
            }
            forest += cost[density];
        }
        return (seen, steps, open, aimAt, aimSq);
    }

    // The scratch's sight rules (HoldSightRules): per forest density the canopy (raw) and the forest a lane crossing it adds; a
    // soldier's eye and a target's height (raw); the forest budget.
    private readonly long[] _holdCanopy = new long[4];
    private readonly int[] _holdForest = new int[4];
    private long _holdEye, _holdTarget;
    private int _holdBudget;

    private void HoldSightRules()
    {
        var rules = SightRules;
        for (var d = 0; d < 4; d++)
        {
            _holdCanopy[d] = rules.CanopyRaw[d];
            _holdForest[d] = Math.Min(rules.ForestSight[d], 255);
        }
        (_holdEye, _holdTarget, _holdBudget) = (rules.EyeFootRaw, rules.TargetRaw, rules.ForestBudget);
    }

    // CrestHides reports whether the ground between cell c and point t rises above the line from a soldier standing in c to an
    // eye at t (the treetops do not count: a crest, not a wood). Never on a flat map or with t off the map.
    private bool CrestHides(Cell c, Vec t)
    {
        if (Ground is not { } g || !Grid.Contains(Cell.Of(t)))
        {
            return false;
        }
        var p = c.Center();
        var d = t - p;
        var steps = (int)((Math.Max(Math.Abs(d.X.Raw), Math.Abs(d.Y.Raw)) + Fixed.OneRaw / 2) / Fixed.OneRaw);
        if (steps <= 1)
        {
            return false;
        }
        var w = Grid.Width;
        var sums = g.CentreSums;
        var water = g.WaterIds;
        long baseH = Surface(g, sums, water, c.Y * w + c.X) + _holdTarget;
        var tc = Cell.Of(t);
        long top = Surface(g, sums, water, tc.Y * w + tc.X) + _holdEye;
        long stepX = d.X.Raw / steps, stepY = d.Y.Raw / steps;
        long x = p.X.Raw, y = p.Y.Raw;
        var rise = top - baseH;
        for (var i = 1; i < steps; i++)
        {
            x += stepX;
            y += stepY;
            var idx = (int)(y >> Fixed.Shift) * w + (int)(x >> Fixed.Shift);
            if ((Surface(g, sums, water, idx) - baseH) * steps > rise * i)
            {
                return true;
            }
        }
        return false;
    }

    // Surface is the sight surface of the cell at index i (raw): its centre's ground (sums: the terrain's corner sums), or the water's
    // surface over it.
    private static long Surface(TerrainMap g, int[] sums, byte[]? water, int i)
    {
        long ground = (sums[i] + 2) >> 2;
        return water is not null && water[i] is var id and > 0 ? Math.Max(ground, g.WaterBodies[id - 1].Surface.Raw) : ground;
    }
}
