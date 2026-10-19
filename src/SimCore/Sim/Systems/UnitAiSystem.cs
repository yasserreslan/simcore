using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The unit AI: what units do on their own besides firing at what is in reach (Zero Hour's player
// rules, the engine's design doc "Unit AI"). A unit in Guard or Aggressive that an enemy hurts calls
// its idle friends nearby, and each that can hit the shooter goes after it (Engage) on a leash from
// where it stood (its guard point) for at most the chase time; then it looks for something else in
// its inner guard circle, or walks back (Return), engaging what comes within that circle on the way.
// Everything runs in the order system, in ID order, with integer distances and no random draws.
public sealed partial class World
{
    private readonly List<Unit> _nearAi = []; // retaliation and guard scans

    private static readonly Fixed ArriveRadius = Fixed.FromInt(1); // back at the guard point within a cell

    // How many calls for help (retaliation searches) ran since the world began: a measurement for
    // tests and tools, not game state (not hashed).
    internal int RetaliateSearches { get; private set; }

    // RecordHit notes that enemy by hurt t this tick (any stance): Return fire shoots back at it,
    // and the next tick's orders may retaliate.
    private void RecordHit(Unit t, Unit by)
    {
        t.LastHitBy = by.Id;
        t.LastHitTick = Tick;
        t.LastHitFrom = by.Pos;
    }

    private static readonly Fixed AttackMoveArrive = Fixed.FromInt(2); // an attack-move that stopped to fight is there within 2 cells

    private static readonly Fixed FollowRadius = Fixed.FromInt(3); // a guard keeps within 3 cells of the unit it guards

    // IdleLike reports whether a unit with this order counts as idle for the unit AI: it may chase
    // and retaliate.
    private static bool IdleLike(OrderKind k) => k is OrderKind.Idle or OrderKind.AttackMove or OrderKind.Guard;

    // CanChase reports whether u can go after targets on its own at all: an armed ground unit that
    // moves, whose archetype pursues, that doesn't set up to fire, on foot or driving (not in a
    // building or a vehicle).
    private static bool CanChase(Unit u) =>
        u.Def.Archetype.Pursue && u.Def.Archetype.DeployTicks == 0 && u.Def.Layer == Layers.Ground && u.Def.Speed.Raw > 0 &&
        u.Garrison == 0 && u.Inside == 0 && u.CanAct && u.Def.Weapons.Count > 0;

    // Pursues reports whether u moves on its own to retaliate or (Aggressive) to hunt: it can chase,
    // in Guard or Aggressive.
    private static bool Pursues(Unit u) => u.Stance is Stance.Guard or Stance.Aggressive && CanChase(u);

    // AiAllowed reports whether a chase or walk back may go on under u's order and stance: idle and
    // on attack-move it needs a stance that pursues; on a Guard order any stance but Hold fire.
    private static bool AiAllowed(Unit u) => u.Order.Kind == OrderKind.Guard
        ? u.Stance != Stance.HoldFire && CanChase(u)
        : IdleLike(u.Order.Kind) && Pursues(u);

    // CanSeek reports whether u can follow an attack target it lost in the fog to where it was last
    // seen: a ground unit that moves and doesn't set up to fire.
    private static bool CanSeek(Unit u) =>
        u.Def.Layer == Layers.Ground && u.Def.Speed.Raw > 0 && u.Def.Archetype.DeployTicks == 0 && u.Garrison == 0 &&
        u.Def.Strike is null && u.Def.Archetype.Flight is null;

    // AggressiveRange is how far an Aggressive unit acquires on its own: its vision times the rules' percent.
    private Fixed AggressiveRange(Unit u) => Fixed.FromRaw(u.Def.Vision.Raw * Content.UnitAi.AggressivePercent / 100);

    // Leash is how far from its guard point u follows a target: the archetype's, or its vision times
    // the outer guard percent.
    private Fixed Leash(Unit u) =>
        u.Def.Archetype.Leash.Raw > 0 ? u.Def.Archetype.Leash : Fixed.FromRaw(u.Def.Vision.Raw * Content.UnitAi.GuardOuterPercent / 100);

    // InnerGuard is the circle round its guard point in which u engages on its own after a chase.
    private Fixed InnerGuard(Unit u) => Fixed.FromRaw(u.Def.Vision.Raw * Content.UnitAi.GuardInnerPercent / 100);

    private Fixed RetaliateMaxOf(Unit u) =>
        u.Def.Archetype.RetaliateMax.Raw > 0 ? u.Def.Archetype.RetaliateMax : Content.UnitAi.RetaliateMax;

    // HomeOf is where u's leash counts from and where it walks back to: on a Guard order the post
    // (the guarded unit while it is on the map), otherwise where the chase began.
    private Vec HomeOf(Unit u) => u.Order.Kind != OrderKind.Guard ? u.GuardPoint
        : Unit(u.Order.Target) is { Alive: true, Inside: 0 } ward ? ward.Pos
        : u.Order.Cell.Center();

    // AttackMove runs an attack-move: a chase first (retaliation), then fixed weapons stop for any
    // target in reach (turrets fire on the move), then on along the route; once it is done (or it
    // stopped near the end) the order ends.
    private void AttackMove(Unit u)
    {
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
            u.MoveOn = true;
            return;
        }
        if (!RouteDone(u))
        {
            return;
        }
        if (!u.MoveOn || Within(u.Pos, u.Order.Cell.Center(), AttackMoveArrive))
        {
            u.Idle(); // there (or as close as its route gets)
            return;
        }
        if (Tick >= u.RepathAt)
        {
            RequestRoute(u, u.Order.Cell); // on again
            u.RepathAt = Tick + RepathTicks + u.Id % RepathTicks;
            u.MoveOn = false;
        }
    }

    // GuardPost runs a Guard order: a chase or the walk back first; every scan it engages the best
    // spotted enemy in the inner circle round the post (unless it never chases or holds its fire);
    // otherwise it keeps to the post.
    private void GuardPost(Unit u)
    {
        if (u.Ai != AiState.None)
        {
            RunAi(u);
            if (u.Ai != AiState.None)
            {
                return;
            }
        }
        var post = HomeOf(u);
        if (u.Stance != Stance.HoldFire && CanChase(u) && (Tick + u.Id) % (uint)Content.UnitAi.ScanTicks == 0 &&
            GuardScan(u, post, InnerGuard(u)) is { } t)
        {
            Engage(u, t, t.Pos);
            Chase(u);
            return;
        }
        GoTo(u, post, u.Order.Target != 0 ? FollowRadius : ArriveRadius);
    }

    // Retaliate runs when an enemy's shot or blast hurts v (Hurt, in the combat and projectile
    // systems, where nobody moves): v (if it lives) and its idle friends within the friends radius
    // (ID order) go after the shooter if they can hit it, at most once per cooldown per victim.
    private void Retaliate(Unit v)
    {
        if (Tick < v.RetaliateAt || v.Stance is not (Stance.Guard or Stance.Aggressive))
        {
            return;
        }
        if (Unit(v.LastHitBy) is not { Alive: true } by || by.Owner == v.Owner || by.Owner == 0 ||
            by.Def.Layer == Layers.Air || by.Def.Strike is not null || by.Def.Archetype.Flight is not null)
        {
            return; // gone, or flying: nobody runs after aircraft
        }
        if (by.Garrison != 0 && Unit(by.Garrison) is { } building)
        {
            by = building; // fire from a window: they go for the building
        }
        var rules = Content.UnitAi;
        v.RetaliateAt = Tick + (uint)rules.RetaliateCooldownTicks;
        RetaliateSearches++;
        var r = rules.RetaliateFriends.Raw;
        foreach (var f in Around(v.Pos.X.Raw - r, v.Pos.Y.Raw - r, v.Pos.X.Raw + r, v.Pos.Y.Raw + r, _nearAi, true))
        {
            if (f.Owner != v.Owner || (f != v && !Within(f.Pos, v.Pos, rules.RetaliateFriends)) || !CanRetaliate(f, v, by))
            {
                continue;
            }
            Engage(f, by, v.LastHitFrom);
        }
    }

    // CanRetaliate reports whether f joins the fight against by, who hurt v: it pursues, is idle (or
    // already walking back), isn't chasing something already, the shooter stood within its
    // retaliation distance of v, and one of its weapons can hit it.
    private bool CanRetaliate(Unit f, Unit v, Unit by) =>
        f.Alive && Pursues(f) && IdleLike(f.Order.Kind) && f.Ai != AiState.Engage &&
        Within(v.Pos, v.LastHitFrom, RetaliateMaxOf(f)) && PickWeapon(f, by, false) >= 0;

    // Engage starts (or renews) a chase of t. A unit walking back keeps its guard point; otherwise
    // the point is where it stands now. If it can't see t, it heads for seek (where the shot came from).
    private void Engage(Unit u, Unit t, Vec seek)
    {
        if (u.Ai == AiState.None)
        {
            u.GuardPoint = u.Pos;
        }
        u.Ai = AiState.Engage;
        u.AiTarget = t.Id;
        u.GiveUpTick = Tick + (uint)Content.UnitAi.ChaseTicks;
        u.SeekPoint = seek;
        u.RepathAt = Tick;
    }

    // RunAi moves a unit that is chasing or walking back (its order counts as idle).
    private void RunAi(Unit u)
    {
        if (u.Ai == AiState.Engage)
        {
            if (Chase(u))
            {
                return;
            }
            EndChase(u);
        }
        if (u.Ai != AiState.Return)
        {
            return;
        }
        var home = HomeOf(u);
        if ((Tick + u.Id) % (uint)Content.UnitAi.ReturnScanTicks == 0 && GuardScan(u, home, InnerGuard(u)) is { } t)
        {
            Engage(u, t, t.Pos); // something came into the circle on the way back
            Chase(u);
            return;
        }
        if (Tick >= u.GiveUpTick || (RouteDone(u) && Within(u.Pos, home, ArriveRadius)))
        {
            u.ClearPath(); // back (or it can't get there): the stance rules take over
            u.ResetAi();
            return;
        }
        GoTo(u, home, ArriveRadius);
    }

    // Chase keeps u after its target and reports whether the chase goes on: the target lives, is an
    // enemy on the map, the timer runs and one of u's weapons can hit it; seen, it must stay within
    // the leash of the guard point (u closes to weapon reach); unseen, u heads for where it was last
    // seen (or where its shot came from) while that point is within the leash, stopping for targets
    // in reach on the way (an attack-move).
    private bool Chase(Unit u)
    {
        var t = Unit(u.AiTarget);
        if (t is not { Alive: true } || t.Owner == u.Owner || t.Owner == 0 || t.Inside != 0 || t.Garrison != 0 ||
            Tick >= u.GiveUpTick || PickWeapon(u, t, false) is not (var wi and >= 0))
        {
            return false;
        }
        var home = HomeOf(u);
        var leash = Leash(u);
        if (IsSpotted(t, u.Owner))
        {
            u.SeekPoint = t.Pos;
            if (!Within(home, t.Pos, leash))
            {
                return false; // it got away
            }
            if (u.Guiding != 0 || u.AimAt != 0)
            {
                u.ClearPath(); // aiming or guiding a missile: it stands still
                return true;
            }
            var aim = AimPoint(u.Pos, t);
            var w = u.Def.Weapons[wi];
            Approach(u, aim, ReachTo(u, aim, w), LineOfFire(u, aim, w, t));
            return true;
        }
        if (!Within(home, u.SeekPoint, leash) || (RouteDone(u) && Within(u.Pos, u.SeekPoint, ArriveRadius)))
        {
            return false; // beyond the leash, or there and nothing to see
        }
        if (!u.Def.Turret && Unit(u.AutoTarget) is { } cur && CanAutoTarget(u, cur))
        {
            u.ClearPath(); // something in reach on the way: stop and shoot it first
            return true;
        }
        GoTo(u, u.SeekPoint, ArriveRadius);
        return true;
    }

    // EndChase ends a chase: something else spotted in the inner guard circle is next, otherwise
    // u walks back to its guard point.
    private void EndChase(Unit u)
    {
        if (u.Order.Kind == OrderKind.AttackMove)
        {
            u.ResetAi(); // on attack-move it carries on toward its destination
            u.MoveOn = true;
            u.ClearPath();
            return;
        }
        if (GuardScan(u, HomeOf(u), InnerGuard(u)) is { } t && t.Id != u.AiTarget)
        {
            Engage(u, t, t.Pos);
            if (Chase(u))
            {
                return;
            }
        }
        u.Ai = AiState.Return;
        u.AiTarget = 0;
        u.GiveUpTick = Tick + 2 * (uint)Content.UnitAi.ChaseTicks; // it gives up walking back if it never gets there
        u.RepathAt = Tick;
        u.ClearPath();
    }

    // GuardScan returns the best spotted enemy within r of center that u can hit from anywhere (by
    // the same order as targets in reach, nearest to u first), or null. Structures, and targets a
    // weapon engages only up close (autoRange), are not chased.
    private Unit? GuardScan(Unit u, Vec center, Fixed r)
    {
        Unit? best = null;
        long bestD = 0;
        int bestRank = 0;
        var m = r.Raw + Fixed.OneRaw;
        foreach (var o in Around(center.X.Raw - m, center.Y.Raw - m, center.X.Raw + m, center.Y.Raw + m, _nearAi, false))
        {
            if (o.Owner == u.Owner || o.Owner == 0 || !o.Alive || o.Inside != 0 || o.Garrison != 0 || o.Def.IsStructure ||
                !IsSpotted(o, u.Owner) || !Within(center, o.Pos, r) || PickWeapon(u, o, false) is not (var wi and >= 0))
            {
                continue;
            }
            var e = u.Def.Weapons[wi].Effect;
            if (Restricted(e, o) is not null || (e.MaxMisses > 0 && u.MissTarget == o.Id && u.MissStreak >= e.MaxMisses))
            {
                continue;
            }
            var rank = Rank(u, o);
            var d = (o.Pos - u.Pos).LenSq();
            if (best is null || rank < bestRank || (rank == bestRank && (d < bestD || (d == bestD && o.Id < best.Id))))
            {
                (best, bestD, bestRank) = (o, d, rank);
            }
        }
        return best;
    }

    // RouteDone reports whether u has nothing left to walk or wait for on its own route.
    private static bool RouteDone(Unit u) => !u.Moving && !u.PathPending && u.Mode != RouteMode.Field && u.RouteIdx >= u.Route.Length;

    // GoTo routes u toward a fixed point until it is within r: one route, re-planned only when it
    // ran out short of the point (at most once a second, staggered by ID).
    private void GoTo(Unit u, Vec p, Fixed r)
    {
        if (Within(u.Pos, p, r) && RouteDone(u))
        {
            return;
        }
        if (RouteDone(u) && Tick >= u.RepathAt)
        {
            RequestRoute(u, Cell.Of(p));
            u.RepathAt = Tick + RepathTicks + u.Id % RepathTicks;
        }
    }
}
