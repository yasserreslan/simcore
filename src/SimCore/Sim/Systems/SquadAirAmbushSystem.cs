using System.Runtime.InteropServices;
using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The air ambush (docs/design.md, "Squads: Air ambush", phase S4). A squad whose members are mostly Iglas (air guards; the content's
// `mostly`) given an Ambush sets it round an air corridor instead of a kill zone: the line the player expects aircraft to fly, from
// its start (where they come from) to its far end. The same order, the mode chosen by the squad's make-up.
//  1. The Iglas spread along the corridor: their slot points on its line, the corridor's length shared out between them but at
//     least `gap` (8 cells) and at most `gapMax` (15) apart, centred on its middle; each takes the best cell within `search` of his
//     slot (twice, then four times that when none is left) for the air guard's weights: canopy that hides from the air, spacing,
//     cover from the ground approach, near the slot. Never closer than `gap` to another Igla (the owner's rule: they never bunch,
//     so one bomb kills one), counting the owner's other holding and ambushing squads' Iglas too.
//  2. Everyone else guards them from the ground: the likely ground approach is `approach` before the corridor's start; the guards
//     are dealt round the Iglas nearest the approach first, each 2 to 5 cells (`guard`) from his Igla and nearer the approach than
//     it (the medic farther from it, behind his Igla), for the guards' weights.
//  3. No mines. Everyone holds fire (under trees that also hides them from aircraft), Setting until in place, then Ready.
//  4. It springs when an enemy aircraft is within reach of `trigger` (2) of its Iglas, each seeing it himself (his own air vision,
//     the slant within his weapon's reach and over its minimum, under its ceiling), when a member is hurt, or on the player's Fire.
//  5. Sprung, its Iglas fire together (a coordinated launch): the squad picks one target (the aircraft most of its Iglas reach,
//     ties to the lowest ID); each Igla that can engage it locks on, and the locked ones hold their missiles until every Igla that
//     reaches it, loaded, is locked too or `volleyWait` has passed since the first was ready; then they all fire in the same tick.
//     The missiles fly at once, so the target's flares and its break (one missile at a time) face several together.
// Integers only, no random draws, ID order and cell-index tie-breaks.
public sealed partial class World
{
    // Scratch: the aircraft flying this tick (built once a tick, when an air ambush needs them), in ID order.
    private readonly List<Unit> _airAmbushAir = [];
    private uint _airAmbushAirTick = uint.MaxValue;

    // Scratch for a picking: the positions taken (a point, and whether an Igla holds it).
    private readonly List<(Vec P, bool Igla)> _airTaken = [];
    private (int, int)[][] _airOffsets = [];
    private long _airOffsetsFor = -1;

    // Scratch for one tick of air defence: the Iglas of sprung air ambushes on their volley target (squad, Igla, ready to fire, and
    // what the launch needs), gathered in unit ID order; the volleys fire after every unit has been run.
    private readonly List<(uint Squad, Unit U, bool Ready, WeaponDef W, AirBody P, long Eye, long Slant)> _volleys = [];

    // ---- Picking ----

    // PickAirAmbush picks the air ambush's positions: the Iglas along the corridor, then the guards; members who already have a
    // position keep it (a soldier who joined picks the best one left). The first time, a squad farther than the ambush's radius from
    // the corridor's middle marches there first.
    private void PickAirAmbush(Router r, Squad s, SquadHoldRules hold, SquadAmbushRules amb, SquadAirAmbushRules air)
    {
        s.HoldPending = false;
        var first = s.PickList.Count == 0;
        s.PickList.RemoveAll(p => s.MemberList.BinarySearch(p.Unit) < 0);
        var looked = AirPicks(r, s, hold, amb, air);
        var approach = _ambA - Scale(_ambZ, air.Approach);
        if (first)
        {
            var m = ZonePoint(_ambLen / 2);
            var spot = Cell.Of(m);
            s.AmbushSpot = new Cell(Math.Clamp(spot.X, 0, Grid.Width - 1), Math.Clamp(spot.Y, 0, Grid.Height - 1));
            s.HoldSpot = s.AmbushSpot;
            s.HoldFacing = _ambZ;
            s.HoldThreat = approach;
            s.HoldFaced = true;
        }
        foreach (var p in s.PickList)
        {
            if (Unit(p.Unit) is { Garrison: not 0 } u && Unit(u.Garrison) is { } b)
            {
                LeaveBuilding(u, b, NearestDoor(b, p.Cell.Center()), 0); // in a building: out, to his position
            }
        }
        s.HoldPlaced = true;
        if (first)
        {
            if (Within(s.Anchor, s.AmbushSpot.Center(), amb.Radius))
            {
                Settle(s, s.AmbushSpot.Center(), s.HoldFacing);
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
                OrderSquad(s, s.AmbushSpot, OrderKind.Move, listed); // it marches to the corridor first (as March), then takes its positions
            }
        }
        _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Placed, s.Leader, (uint)looked));
    }

    // BenchAirAmbushPick (benchmarks only): picks air ambushing squad s's positions again as its order did (its picks are kept) and
    // returns the cells it looked at.
    internal int BenchAirAmbushPick(Squad s)
    {
        if (!s.AmbushAir || Content.Squad is not { Hold: { } hold, Ambush: { Air: { } air } amb })
        {
            return 0;
        }
        var kept = new List<HoldPick>(s.PickList);
        s.PickList.Clear();
        var looked = AirPicks(SyncRouting(), s, hold, amb, air);
        s.PickList.Clear();
        s.PickList.AddRange(kept);
        return looked;
    }

    // AirPicks gives each member without a position one (the Iglas, then the guards) and returns the cells it looked at.
    private int AirPicks(Router r, Squad s, SquadHoldRules hold, SquadAmbushRules amb, SquadAirAmbushRules air)
    {
        ZoneSet(s, 1);
        var approach = _ambA - Scale(_ambZ, air.Approach);
        var iglas = new List<Unit>();
        var guards = new List<Unit>();
        foreach (var id in s.MemberList)
        {
            var u = Unit(id)!;
            (SquadRoleOf(u) == SquadRole.AirGuard ? iglas : guards).Add(u);
        }
        AirTaken(s, air);
        var k = SquadClass(r, s);
        var comp = 0;
        if (k >= 0 && HoldFrom(s) is { } from)
        {
            var at = r.NearestPassable(k, Cell.Of(from.Pos), StartSearch);
            comp = at < 0 ? 0 : r.Component(k, at);
        }
        var looked = 0;
        // 1. The Iglas: slot points along the corridor, the gap between them its length shared out (within the rules' gap).
        var n = iglas.Count;
        var gap = n <= 1 ? 0 : Math.Clamp(_ambLen / (n - 1), air.Gap.Raw, air.GapMax.Raw);
        var start = _ambLen / 2 - (n - 1) * gap / 2;
        var apart = amb.Roles[(int)SquadRole.AirGuard].Apart.Raw;
        for (var i = 0; i < n; i++)
        {
            var u = iglas[i];
            if (HoldPickIndex(s, u.Id) >= 0)
            {
                continue;
            }
            var slot = ZonePoint(start + i * gap);
            var c = AirCell(r, k, comp, u, slot, approach, hold, air.AirGuard, air.GapMax.Raw, apart, air.Gap.Raw, null, false, air, ref looked);
            AddAirPick(s, new HoldPick(u.Id, c, 0), true);
        }
        // 2. The guards, dealt round the Iglas nearest the approach first; each between his Igla and the approach (the medic behind it).
        var posts = new List<(long D, uint Id, Vec P)>();
        foreach (var u in iglas)
        {
            if (HoldPickIndex(s, u.Id) is var pi and >= 0)
            {
                var p = s.PickList[pi].Cell.Center();
                posts.Add(((p - approach).Len().Raw, u.Id, p));
            }
        }
        posts.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.Id.CompareTo(b.Id));
        var mid = (air.GuardNear.Raw + air.GuardFar.Raw) / 2;
        var gi = 0;
        foreach (var u in guards)
        {
            if (HoldPickIndex(s, u.Id) >= 0)
            {
                continue;
            }
            Vec post, toward;
            if (posts.Count == 0)
            {
                post = ZonePoint(_ambLen / 2);
                toward = approach - post;
            }
            else
            {
                post = posts[gi++ % posts.Count].P;
                toward = approach - post;
            }
            var behind = SquadRoleOf(u) == SquadRole.Medic;
            var dir = toward.LenSq() == 0 ? new Vec(-_ambZ.X, -_ambZ.Y) : toward.WithLen(Fixed.One);
            if (behind)
            {
                dir = new Vec(-dir.X, -dir.Y);
            }
            var slot = post + Scale(dir, Fixed.FromRaw(mid));
            var role = SquadRoleOf(u);
            var c = AirCell(r, k, comp, u, slot, approach, hold, air.Guards, air.GuardFar.Raw, amb.Roles[(int)role].Apart.Raw, 0, post, behind,
                air, ref looked);
            AddAirPick(s, new HoldPick(u.Id, c, 0), false);
        }
        HoldCellsScored += looked;
        return looked;
    }

    // AddAirPick records a member's position (picks by unit ID) and takes its cell for those who pick after him.
    private void AddAirPick(Squad s, HoldPick pick, bool igla)
    {
        s.PickList.Insert(~s.PickList.BinarySearch(pick, PickById), pick);
        _airTaken.Add((pick.Cell.Center(), igla));
    }

    // AirTaken gathers the positions already held that a picking must keep clear of: the squad's own picks and those of the owner's
    // other holding and ambushing squads near the corridor (an Igla's counted as one: Iglas never bunch, whichever squad they are in).
    private void AirTaken(Squad s, SquadAirAmbushRules air)
    {
        _airTaken.Clear();
        var m = 4 * air.Search.Raw + air.GapMax.Raw + air.GuardFar.Raw;
        long x0 = Math.Min(_ambA.X.Raw, _ambB.X.Raw) - m, x1 = Math.Max(_ambA.X.Raw, _ambB.X.Raw) + m;
        long y0 = Math.Min(_ambA.Y.Raw, _ambB.Y.Raw) - m, y1 = Math.Max(_ambA.Y.Raw, _ambB.Y.Raw) + m;
        foreach (var o in _squads)
        {
            if (o.Owner != s.Owner || o.Mode is not (SquadMode.Hold or SquadMode.Ambush) || o.PickList.Count == 0)
            {
                continue;
            }
            foreach (var p in o.PickList)
            {
                var c = p.Cell.Center();
                if (p.Building != 0 || Unit(p.Unit) is not { } u || c.X.Raw < x0 || c.X.Raw > x1 || c.Y.Raw < y0 || c.Y.Raw > y1)
                {
                    continue;
                }
                _airTaken.Add((c, SquadRoleOf(u) == SquadRole.AirGuard));
            }
        }
    }

    // AirCell is the best cell for member u near slot (his weights; spread: where spacing scores in full) within the search radius,
    // then twice it while none is left (while the squad's cap allows): clear ground his squad's class can reach, at least `apart`
    // from anyone placed and `gap` from any Igla (0: none); a guard (post: his Igla) between `guard` limits of it and nearer the
    // approach than it (behind: farther). Then a guard drops those limits (twice the radius, capped); an Igla never drops his and
    // looks on past the cap, 4 and 8 times the radius out (crowded by other squads' Iglas). Ties to the lowest cell index. None at
    // all: where he stands.
    private Cell AirCell(Router r, int k, int comp, Unit u, Vec slot, Vec approach, SquadHoldRules hold, SquadAirWeights wt, long spread,
        long apart, long gap, Vec? post, bool behind, SquadAirAmbushRules air, ref int looked)
    {
        var sc = Cell.Of(slot);
        var postToApproach = post is { } q ? (approach - q).LenSq() : 0;
        var passes = post is null ? 4 : 3;
        for (var pass = 0; pass < passes; pass++)
        {
            var strict = post is null || pass < 2; // an Igla keeps his limits always (never bunch); a guard drops his last
            var shift = post is null ? pass : Math.Min(pass, 1);
            var capped = pass < 2 || post is not null; // an Igla's last two passes look on past the cap: he must not bunch
            var radius = air.Search.Raw << shift;
            var best = -1L;
            var bestKey = 0;
            Cell bestCell = default;
            foreach (var (dx, dy) in AirOffsets(air.Search.Raw, shift))
            {
                if (capped && looked >= air.Cells)
                {
                    break;
                }
                var c = new Cell(sc.X + dx, sc.Y + dy);
                if (!Grid.Contains(c))
                {
                    continue;
                }
                looked++;
                if (Grid.At(c) != Terrain.Clear || InWater(c) || (k >= 0 && (!r.Passable(k, r.Index(c)) || (comp != 0 && r.Component(k, r.Index(c)) != comp))) ||
                    !SetsUpOn(u, c))
                {
                    continue;
                }
                var p = c.Center();
                var dmin = long.MaxValue;
                var ok = true;
                foreach (var (tp, igla) in _airTaken)
                {
                    var d2 = (tp - p).LenSq();
                    dmin = Math.Min(dmin, d2);
                    ok &= d2 >= (long)Fixed.OneRaw * Fixed.OneRaw && (!strict || (d2 >= apart * apart && (!igla || d2 >= gap * gap)));
                }
                if (ok && strict && post is { } pp)
                {
                    var dp = (p - pp).LenSq();
                    var toApproach = (approach - p).LenSq();
                    ok = dp >= air.GuardNear.Raw * air.GuardNear.Raw && dp <= air.GuardFar.Raw * air.GuardFar.Raw &&
                        (behind ? toApproach > postToApproach : toApproach < postToApproach);
                }
                if (!ok)
                {
                    continue;
                }
                var g = Ground;
                var density = g?.Forest(c) ?? 0;
                var cover = wt.Cover == 0 ? 0 : CoverPoints(c, approach, hold, density);
                var trees = (Content.Concealment is { } cr ? cr.Covers(density) : density >= 2) ? 100 : 0;
                var spacing = dmin == long.MaxValue || dmin >= spread * spread ? 100 : (int)(Fixed.Sqrt(dmin) * 100 / Math.Max(1, spread));
                var near = (int)Math.Clamp(100 - (p - slot).Len().Raw * 100 / radius, 0, 100);
                var score = (long)wt.Cover * cover + wt.Trees * trees + wt.Spacing * spacing + wt.Near * near;
                var key = c.Y * Grid.Width + c.X;
                if (score > best || (score == best && key < bestKey))
                {
                    (best, bestKey, bestCell) = (score, key, c);
                }
            }
            if (best >= 0)
            {
                return bestCell;
            }
        }
        return Cell.Of(u.Pos); // nothing left: he holds where he stands
    }

    // AirOffsets lists the cell offsets within the search radius times 2^shift (raw), nearest first (HoldOffsets), cached per search.
    private (int, int)[] AirOffsets(long search, int shift)
    {
        if (_airOffsetsFor != search)
        {
            _airOffsets = new (int, int)[4][];
            _airOffsetsFor = search;
        }
        return _airOffsets[shift] ??= HoldOffsets(search << shift);
    }

    // ---- Triggers ----

    // AirTriggered: an enemy aircraft within reach of the rules' number of s's Iglas springs it (Air).
    private AmbushTrigger AirTriggered(Squad s, SquadAirAmbushRules air)
    {
        foreach (var t in AirAmbushAircraft())
        {
            if (t.Owner != s.Owner && IglasReaching(s, t, air.Trigger) >= air.Trigger)
            {
                return AmbushTrigger.Air;
            }
        }
        return AmbushTrigger.None;
    }

    // AirInReach counts the enemy aircraft within reach of at least one of s's Iglas.
    private int AirInReach(Squad s, SquadAirAmbushRules air)
    {
        var n = 0;
        foreach (var t in AirAmbushAircraft())
        {
            if (t.Owner != s.Owner && IglasReaching(s, t, 1) >= 1)
            {
                n++;
            }
        }
        return n;
    }

    // AirVolleyTarget keeps a sprung air ambush's target while one of its Iglas reaches it; else the aircraft most of them reach (ties to
    // the lowest ID), or none.
    private void AirVolleyTarget(Squad s)
    {
        if (s.VolleyTarget != 0 && Unit(s.VolleyTarget) is { } cur && IsAirAmbushAircraft(cur) && IglasReaching(s, cur, 1) >= 1)
        {
            return;
        }
        uint pick = 0;
        var most = 0;
        foreach (var t in AirAmbushAircraft())
        {
            if (t.Owner == s.Owner)
            {
                continue;
            }
            var c = IglasReaching(s, t, int.MaxValue);
            if (c > most)
            {
                (pick, most) = (t.Id, c);
            }
        }
        if (pick != s.VolleyTarget)
        {
            s.VolleyTarget = pick;
            s.VolleySince = 0;
        }
    }

    // IglasReaching counts s's Iglas (members with an interceptor, on the map, with a missile) that reach aircraft t (stopping at
    // enough): it engages t's class, t flies under its ceiling, the slant from his eye is within his weapon's reach and not inside its
    // minimum, and he sees it himself (his air vision, t's signature).
    private int IglasReaching(Squad s, Unit t, int enough)
    {
        var n = 0;
        var p = new AirBody(t);
        foreach (var id in s.MemberList)
        {
            var u = Unit(id)!;
            if (!u.Alive || u.Inside != 0 || u.Garrison != 0 || InterceptorWeapon(u.Def) is not (var wi and >= 0))
            {
                continue;
            }
            var w = u.Def.Weapons[wi];
            var ic = w.Effect.Interceptor!;
            if (!HasAmmo(u, w) || !ic.Engages(p.Class) || !UnderCeiling(ic, p))
            {
                continue;
            }
            var eye = EyeHeightRaw(u, ic);
            var n2 = Slant2(u.Pos, eye, p);
            var min = w.Effect.MinRange.Raw;
            if (!SlantWithin(n2, w.Range.Raw) || n2 < min * min || !Senses(u, eye, p, n2))
            {
                continue;
            }
            if (++n >= enough)
            {
                break;
            }
        }
        return n;
    }

    // AirAmbushAircraft is this tick's flying aircraft (alive, not on the ground), in ID order: built once a tick.
    private List<Unit> AirAmbushAircraft()
    {
        if (_airAmbushAirTick != Tick)
        {
            _airAmbushAirTick = Tick;
            _airAmbushAir.Clear();
            foreach (var t in _units)
            {
                if (IsAirAmbushAircraft(t))
                {
                    _airAmbushAir.Add(t);
                }
            }
        }
        return _airAmbushAir;
    }

    private bool IsAirAmbushAircraft(Unit t) => t.Alive && t.Def.Archetype.Flight is not null && !Grounded(t);

    // ---- The volley (the air defence system) ----

    // VolleySquad is the sprung air ambush of u's squad with a target, when u's interceptor may take part, or null.
    private Squad? VolleySquad(Unit u) =>
        u.Squad != 0 && Squad(u.Squad) is { Mode: SquadMode.Ambush, AmbushAir: true, AmbushPhase: SquadState.Sprung, VolleyTarget: not 0 } s ? s : null;

    // FireVolleys fires each sprung air ambush's volley (after every air defence unit has been run): its Iglas on the volley target that
    // are locked and loaded fire together, in ID order, once every one of them that reaches it loaded is locked too, or the rules'
    // wait has passed since the first was ready. A squad whose Iglas are not ready waits.
    private void FireVolleys()
    {
        if (_volleys.Count == 0)
        {
            return;
        }
        _volleys.Sort((a, b) => a.Squad != b.Squad ? a.Squad.CompareTo(b.Squad) : a.U.Id.CompareTo(b.U.Id));
        var air = Content.Squad?.Ambush?.Air;
        var span = CollectionsMarshal.AsSpan(_volleys);
        for (var i = 0; i < span.Length;)
        {
            var j = i;
            var ready = 0;
            while (j < span.Length && span[j].Squad == span[i].Squad)
            {
                ready += span[j].Ready ? 1 : 0;
                j++;
            }
            var s = Squad(span[i].Squad)!;
            if (ready == 0)
            {
                s.VolleySince = 0;
            }
            else
            {
                if (s.VolleySince == 0)
                {
                    s.VolleySince = Tick + 1; // (0 is none)
                }
                if (ready == j - i || air is null || Tick + 1 - s.VolleySince >= (uint)air.VolleyWait)
                {
                    for (var x = i; x < j; x++)
                    {
                        ref readonly var v = ref span[x];
                        if (v.Ready)
                        {
                            LaunchInterceptor(v.U, v.W, v.P, v.Eye, v.Slant);
                        }
                    }
                    s.VolleySince = 0;
                    _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Volley, span[i].P.Id, (uint)ready));
                }
            }
            i = j;
        }
        _volleys.Clear();
    }
}
