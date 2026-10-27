using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Cruise missiles (weapon cruise; docs/design.md, "Cruise missile truck"). A missile flies the route its launch order drew
// (Command.Route: up to MaxWaypoints whole cells, then its point), low over the ground, and the whole flight is worked out at
// launch from the route and the terrain (PlanCruise), so the sim steps through it and a renderer draws it from the same numbers
// (FlightPointAt):
//  - the boost: BoostTicks straight up off the launcher to BoostRaw over the launch point's ground;
//  - the path: the straight legs launcher -> waypoints -> point, each waypoint rounded by an arc of TurnRadius tangent to both legs
//    (a shorter one where a leg is too short for it: the arc never takes more than half a leg), walked at the weapon's projectile
//    speed;
//  - the height, tick by tick along the path: HeightRaw over the highest ground and treetops within LookAhead ahead (sampled every
//    half cell), PopUpRaw over the ground where the final dive starts. A backward pass starts each climb early enough to make it at
//    ClimbRaw a tick; a forward pass from the top of the boost descends at most DescendRaw a tick (over a slope too steep to follow
//    it stays higher) and climbs at most ClimbRaw. The final DiveFrom of the path is a straight line down onto its point.
// A route whose path cannot keep above the ground at those rates (a wall right off the launcher) is refused at the order
// (CheckCruiseRoute names the leg); at launch the same plan is made from where the truck stands, and a missile whose plan meets
// the ground goes off there (a hill in its way). In its last DescentCells it hits the first unit or structure under it.
// Being seen: its weapon's lowFlight rule (Senses, below 30 m: ground sensors need a clear line over the terrain and the treetops
// and 120 cells) and signature; once an enemy player's sensors see it he is warned (IncomingStrikes) while they do and WarnTicks
// after, and IncomingEvents says Seen. Hashed state: its age and height, the digest of its route and planned flight, who saw it,
// the warnings; the truck's queued routes.
public sealed partial class World
{
    private const long HalfCellRaw = Fixed.OneRaw / 2;

    // Scratch, not state: the air sensors that look for cruise missiles this tick (CruiseSightings), in ID order.
    private readonly List<(Unit Unit, long Eye)> _cruiseSensors = [];

    // Scratch, not state: this tick's low-flight sight checks (sensor, projectile, both positions and the target's height, the
    // answer), sorted by key; cleared when the tick changes.
    private readonly List<(ulong Key, long Sx, long Sy, long Px, long Py, long Alt, bool Hidden)> _lowSight = [];
    private long _lowSightTick = -1;

    /// <summary>
    /// Whether truck <paramref name="truck"/> (a unit with a cruise weapon) would take a launch at <paramref name="target"/> through
    /// <paramref name="waypoints"/> from where it stands now: the HUD's check while a route is drawn, the same one the order runs.
    /// The result's <see cref="CruiseRouteCheck.Length"/> is the route's length (the straight legs) and, for
    /// <see cref="CruiseRouteProblem.Terrain"/>, <see cref="CruiseRouteCheck.Leg"/> the first leg (0: launcher to the first point)
    /// whose ground its flight cannot clear at its climb and descent limits.
    /// </summary>
    public CruiseRouteCheck CheckCruiseRoute(Unit truck, IReadOnlyList<Cell>? waypoints, Cell target) => CheckCruiseRoute(truck, truck.Pos, waypoints, target);

    /// <summary>As <see cref="CheckCruiseRoute(Unit, IReadOnlyList{Cell}?, Cell)"/>, with the truck set up at <paramref name="from"/> instead.</summary>
    public CruiseRouteCheck CheckCruiseRoute(Unit truck, Vec from, IReadOnlyList<Cell>? waypoints, Cell target)
    {
        var wi = CruiseWeapon(truck.Def);
        if (wi < 0)
        {
            return new CruiseRouteCheck(CruiseRouteProblem.NoCruiseWeapon, -1, Fixed.Zero);
        }
        var w = truck.Def.Weapons[wi];
        var cf = w.Effect.Cruise!;
        var wps = waypoints ?? [];
        var length = RouteLength(from, wps, target);
        if (wps.Count > cf.MaxWaypoints)
        {
            return new CruiseRouteCheck(CruiseRouteProblem.TooManyWaypoints, -1, length);
        }
        for (var i = 0; i <= wps.Count; i++)
        {
            if (!Grid.Contains(i < wps.Count ? wps[i] : target))
            {
                return new CruiseRouteCheck(CruiseRouteProblem.OffMap, i, length);
            }
        }
        if (length < cf.RouteMin)
        {
            return new CruiseRouteCheck(CruiseRouteProblem.TooShort, -1, length);
        }
        if (length > cf.RouteMax)
        {
            return new CruiseRouteCheck(CruiseRouteProblem.TooLong, -1, length);
        }
        var plan = PlanCruise(w, from, wps, target);
        return plan.FailLeg >= 0 ? new CruiseRouteCheck(CruiseRouteProblem.Terrain, plan.FailLeg, length) : new CruiseRouteCheck(CruiseRouteProblem.None, -1, length);
    }

    /// <summary>The route's length: the straight legs from <paramref name="from"/> through the waypoints to the target's centre.</summary>
    public static Fixed RouteLength(Vec from, IReadOnlyList<Cell> waypoints, Cell target)
    {
        long total = 0;
        var at = from;
        for (var i = 0; i <= waypoints.Count; i++)
        {
            var next = (i < waypoints.Count ? waypoints[i] : target).Center();
            total += (next - at).Len().Raw;
            at = next;
        }
        return Fixed.FromRaw(total);
    }

    /// <summary>The players warned of cruise missile <paramref name="p"/> now (a bit, 1 &lt;&lt; player, each); 0 for any other projectile.</summary>
    public uint CruiseWarned(Projectile p) => p.Cruise is { } c ? WarnedNow(c) : 0;

    // CruiseWeapon is the index of d's cruise weapon, or -1.
    internal static int CruiseWeapon(UnitDef d)
    {
        var weapons = d.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Effect.Cruise is not null)
            {
                return i;
            }
        }
        return -1;
    }

    // OrderCruise takes attack-ground command c for cruise truck u: its route checked from where u stands (refused with the reason
    // otherwise), then the order (its routes start over) or, with Shift on an attack-ground order, one more route at the back.
    private void OrderCruise(Unit u, Command c)
    {
        var check = CheckCruiseRoute(u, c.Route, c.Cell);
        if (!check.Ok)
        {
            Refuse(u, check.Problem == CruiseRouteProblem.Terrain ? OrderRefusalReason.RouteTerrain : OrderRefusalReason.RouteInvalid, 0, c.Cell);
            return;
        }
        var route = new CruiseRoute(c.Cell, [.. c.Route ?? []]);
        if (c.Queue && u.Order.Kind == OrderKind.AttackGround && u.CruiseRoutes.Count > 0)
        {
            if (u.CruiseRoutes.Count <= MaxGroundQueue)
            {
                u.MutableCruiseRoutes.Add(route); // Shift: the next missile's route
            }
            return;
        }
        (u.NextGround, u.HasNextGround) = (default, false);
        Assign(u, new Order(OrderKind.AttackGround, c.Cell, 0, 0));
        u.MutableCruiseRoutes.Add(route);
    }

    // TakeCruiseRoute is the route u's next missile at point `at` flies: its first queued route when that route's target is at's cell
    // (taken off the queue), else straight there.
    private static CruiseRoute TakeCruiseRoute(Unit u, Vec at)
    {
        var routes = u.MutableCruiseRoutes;
        if (routes.Count > 0 && routes[0].Target == Cell.Of(at))
        {
            var r = routes[0];
            routes.RemoveAt(0);
            return r;
        }
        return new CruiseRoute(Cell.Of(at), []);
    }

    // NextCruise moves cruise truck u on to its next route's target once a missile is away, or ends the order when none is left.
    private static void NextCruise(Unit u)
    {
        if (u.Order.Kind != OrderKind.AttackGround)
        {
            return;
        }
        if (u.CruiseRoutes.Count > 0)
        {
            u.Order = u.Order with { Cell = u.CruiseRoutes[0].Target };
            u.AimAt = 0;
            u.AimLeft = 0;
        }
        else
        {
            u.Idle();
        }
    }

    // LaunchCruise sets cruise missile p off from shooter along route: its whole flight planned now from where the shooter stands.
    private void LaunchCruise(Projectile p, Unit shooter, CruiseRoute route)
    {
        var plan = PlanCruise(p.Weapon, shooter.Pos, route.Waypoints, route.Target);
        p.Origin = shooter.Pos;
        p.Altitude = Fixed.FromRaw(plan.Track[2]);
        var cf = p.Weapon.Effect.Cruise!;
        p.Cruise = new CruiseState(route, plan.Track, cf.BoostTicks, plan.DiveAge, plan.ImpactAge, plan.FailAge, cf.WarnTicks, Digest(route, plan));
    }

    // Digest folds a cruise missile's route and planned flight into one word (the flight is a pure function of them and the terrain
    // at launch; hashing this is hashing the flight).
    private static ulong Digest(CruiseRoute route, CruisePlan plan)
    {
        var d = 14695981039346656037UL;
        void Mix(long v) => d = unchecked((d ^ (ulong)v) * 1099511628211UL);
        Mix(route.Target.X);
        Mix(route.Target.Y);
        Mix(route.Waypoints.Count);
        foreach (var c in route.Waypoints)
        {
            Mix(c.X);
            Mix(c.Y);
        }
        Mix(plan.DiveAge);
        Mix(plan.ImpactAge);
        Mix(plan.FailAge);
        foreach (var v in plan.Track)
        {
            Mix(v);
        }
        return d;
    }

    // HashCruiseRoutes mixes in a cruise truck's routes still to fly (World.Hash; HashReference does the same word by word).
    private static void HashCruiseRoutes(ref WordHash h, IReadOnlyList<CruiseRoute> routes)
    {
        h.Put(0xC2017E);
        h.PutSigned(routes.Count);
        foreach (var r in routes)
        {
            h.PutSigned(r.Target.X);
            h.PutSigned(r.Target.Y);
            h.PutSigned(r.Waypoints.Count);
            foreach (var q in r.Waypoints)
            {
                h.PutSigned(q.X);
                h.PutSigned(q.Y);
            }
        }
    }

    // ---- The plan ----

    // A planned flight: per age 0 to ImpactAge its x, y and height (absolute, raw), three longs each; the age the final dive starts;
    // the first leg (0 from the launcher) whose ground it cannot clear and the age it meets it, or -1.
    private sealed record CruisePlan(long[] Track, int DiveAge, int ImpactAge, int FailLeg, int FailAge);

    // SurfaceRaw is what a cruise missile keeps its height over at q: the ground (or water) plus the treetops (raw).
    private long SurfaceRaw(Vec q) => Ground is { } g ? g.HeightAt(q).Raw + SightRules.CanopyRaw[g.Forest(Cell.Of(q))] : 0;

    // PlanCruise works out weapon w's cruise missile's whole flight from `from` through waypoints to target (see the header).
    private CruisePlan PlanCruise(WeaponDef w, Vec from, IReadOnlyList<Cell> waypoints, Cell target)
    {
        var cf = w.Effect.Cruise!;
        var speed = Math.Max(1, w.Effect.ProjectileSpeed.Raw);
        var pts = new List<Vec>(waypoints.Count + 2) { from };
        foreach (var c in waypoints)
        {
            pts.Add(c.Center());
        }
        pts.Add(target.Center());
        var path = CruisePath.Build(pts, cf.TurnRadius.Raw);
        var total = path.Total;

        // The ticks along the path (k = 0 at the top of the boost) and the half-cell samples of the surface and the ground.
        var ticks = (int)((total + speed - 1) / speed);
        var sk = new long[ticks + 1];
        var xs = new long[ticks + 1];
        var ys = new long[ticks + 1];
        var walk = 0;
        for (var k = 0; k <= ticks; k++)
        {
            sk[k] = Math.Min(k * speed, total);
            (xs[k], ys[k]) = path.At(sk[k], ref walk);
        }
        var samples = (int)(total / HalfCellRaw);
        var surface = new long[samples + 1];
        var ground = new long[samples + 1];
        var sx = new long[samples + 1];
        var sy = new long[samples + 1];
        walk = 0;
        for (var j = 0; j <= samples; j++)
        {
            (sx[j], sy[j]) = path.At(j * HalfCellRaw, ref walk);
            var q = P(sx[j], sy[j]);
            surface[j] = SurfaceRaw(q);
            ground[j] = GroundRaw(q);
        }

        // The final dive starts at the first tick at or past DiveFrom from the end.
        var diveAt = Math.Max(0, total - cf.DiveFrom.Raw);
        var kDive = 0;
        while (kDive < ticks && sk[kDive] < diveAt)
        {
            kDive++;
        }
        var look = cf.LookAhead.Raw;
        var req = new long[kDive + 1];
        for (var k = 0; k <= kDive; k++)
        {
            var lo = (int)(sk[k] / HalfCellRaw);
            var hi = (int)Math.Min(samples, (sk[k] + look) / HalfCellRaw);
            var top = long.MinValue;
            for (var j = lo; j <= hi; j++)
            {
                top = Math.Max(top, surface[j]);
            }
            req[k] = top + cf.HeightRaw;
        }
        req[kDive] = Math.Max(req[kDive], surface[(int)Math.Min(samples, sk[kDive] / HalfCellRaw)] + cf.PopUpRaw);
        for (var k = kDive - 1; k >= 0; k--)
        {
            req[k] = Math.Max(req[k], req[k + 1] - cf.ClimbRaw); // a climb ahead starts early enough to make it
        }
        var alt = new long[ticks + 1];
        var launch = GroundRaw(from) + LaunchHeightRaw(w);
        alt[0] = GroundRaw(from) + cf.BoostRaw;
        for (var k = 1; k <= kDive; k++)
        {
            alt[k] = Math.Min(Math.Max(req[k], alt[k - 1] - cf.DescendRaw), alt[k - 1] + cf.ClimbRaw);
        }
        var land = GroundRaw(target.Center()) + LandRaw;
        for (var k = kDive + 1; k <= ticks; k++)
        {
            alt[k] = alt[kDive] + (land - alt[kDive]) * (sk[k] - sk[kDive]) / Math.Max(1, total - sk[kDive]);
        }
        if (ticks > kDive)
        {
            alt[ticks] = land;
        }

        // Clearance: every half-cell sample (but the last cell before its point) must be above the ground.
        int failLeg = -1, failAge = -1;
        for (var j = 1; j <= samples && failLeg < 0; j++)
        {
            var s = j * HalfCellRaw;
            if (s >= total - Fixed.OneRaw)
            {
                break;
            }
            var k = (int)Math.Min(ticks - 1, s / speed);
            while (k < ticks - 1 && sk[k + 1] < s)
            {
                k++;
            }
            var span = Math.Max(1, sk[k + 1] - sk[k]);
            var a = alt[k] + (alt[k + 1] - alt[k]) * (s - sk[k]) / span;
            if (a <= ground[j])
            {
                failLeg = path.LegAt(s);
                failAge = cf.BoostTicks + k + 1;
            }
        }

        // The track: the boost (straight up), then the path.
        var impact = cf.BoostTicks + ticks;
        var track = new long[(impact + 1) * 3];
        for (var a = 0; a <= cf.BoostTicks; a++)
        {
            (track[a * 3], track[a * 3 + 1], track[a * 3 + 2]) = (from.X.Raw, from.Y.Raw, launch + (alt[0] - launch) * a / cf.BoostTicks);
        }
        for (var k = 1; k <= ticks; k++)
        {
            var a = cf.BoostTicks + k;
            (track[a * 3], track[a * 3 + 1], track[a * 3 + 2]) = (xs[k], ys[k], alt[k]);
        }
        return new CruisePlan(track, cf.BoostTicks + kDive, impact, failLeg, failAge);
    }

    // ---- The flight ----

    // CruisePoint is the planned flight at age am (thousandths of a tick), between the ticks on the straight line from one to the next.
    private static FlightPoint CruisePoint(CruiseState c, long am)
    {
        am = Math.Clamp(am, 0, c.ImpactAge * 1000L);
        var a = (int)(am / 1000);
        var f = am % 1000;
        var t = c.Track;
        var i = a * 3;
        long x = t[i], y = t[i + 1], z = t[i + 2];
        if (f > 0 && a < c.ImpactAge)
        {
            x += (t[i + 3] - x) * f / 1000;
            y += (t[i + 4] - y) * f / 1000;
            z += (t[i + 5] - z) * f / 1000;
        }
        var age = f > 0 ? a + 1 : a;
        return new FlightPoint(new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y)), Fixed.FromRaw(z), CruisePhase(c, age));
    }

    private static TerminalPhase CruisePhase(CruiseState c, int age) =>
        age <= c.BoostTicks ? TerminalPhase.Climb : age > c.DiveAge ? TerminalPhase.Dive : TerminalPhase.Cruise;

    // CruiseVelocity is its next tick's step (raw x y z) on its plan.
    internal static (long X, long Y, long Z) CruiseVelocity(Projectile p, CruiseState c)
    {
        var a = Math.Min(p.Age, c.ImpactAge);
        var b = Math.Min(p.Age + 1, c.ImpactAge);
        var t = c.Track;
        return (t[b * 3] - t[a * 3], t[b * 3 + 1] - t[a * 3 + 1], t[b * 3 + 2] - t[a * 3 + 2]);
    }

    // StepCruise moves cruise missile p one tick along its plan: the events, the ground in its way (a plan that meets it), then in
    // its last DescentCells the first unit or structure under it, else its point when its flight is done.
    private void StepCruise(Projectile p, CruiseState c)
    {
        var a0 = p.Age;
        var a1 = a0 + 1;
        var t = c.Track;
        long x0 = t[a0 * 3], y0 = t[a0 * 3 + 1], z0 = t[a0 * 3 + 2];
        long x1 = t[a1 * 3], y1 = t[a1 * 3 + 1], z1 = t[a1 * 3 + 2];
        p.Age = a1;
        (p.Vel, p.VelZ) = (new Vec(Fixed.FromRaw(x1 - x0), Fixed.FromRaw(y1 - y0)), Fixed.FromRaw(z1 - z0));
        if (a0 == 0)
        {
            _incomingEvents.Add(new IncomingEvent(IncomingEventKind.Launched, p.Id, p.Owner, p.Dest, Tick + (uint)(c.ImpactAge - a1), 0));
        }
        var from = P(x0, y0);
        var to = P(x1, y1);
        if (Ground is { } g && (x1 != x0 || y1 != y0))
        {
            var len = (to - from).Len().Raw;
            var steps = (int)Math.Max(1, (len * TerrainSteps + Fixed.OneRaw - 1) / Fixed.OneRaw);
            for (var k = 1; k <= steps; k++)
            {
                var q = P(x0 + (x1 - x0) * k / steps, y0 + (y1 - y0) * k / steps);
                var zq = z0 + (z1 - z0) * k / steps;
                var gq = g.HeightAt(q).Raw;
                if (zq <= gq && (a1 < c.ImpactAge || k < steps))
                {
                    p.Altitude = Fixed.FromRaw(gq);
                    Detonate(p, q, 0, false); // into the ground in its way
                    return;
                }
            }
        }
        var zone = (long)DescentCells * Fixed.OneRaw;
        if (a1 > c.BoostTicks && (to - p.Dest).LenSq() <= zone * zone && FirstHit(p, from, to, out var at, out var hit, out var terrain))
        {
            p.Altitude = Fixed.FromRaw(z1);
            Detonate(p, at, hit, terrain);
            return;
        }
        p.Pos = to;
        p.Altitude = Fixed.FromRaw(z1);
        if (a1 >= c.ImpactAge)
        {
            Detonate(p, p.Dest, 0, false); // the ground at its point
        }
    }

    // ---- Being seen ----

    // CruiseSightings (after the projectiles moved): for each cruise missile in flight, which enemy players' air sensors see it now
    // (Senses: its signature, the low-flight rule, a radar horizon); a player seeing it for the first time gets a Seen event; every
    // player seeing it is warned for WarnTicks more.
    private void CruiseSightings()
    {
        var any = false;
        foreach (var p in _projectiles)
        {
            if (p.Cruise is not null && !p.Done)
            {
                any = true;
                break;
            }
        }
        if (!any)
        {
            return;
        }
        _cruiseSensors.Clear();
        foreach (var s in _units)
        {
            if (s.Def.Archetype.AirVision.Raw != 0 && s.Owner != 0 && s.Alive && s.IsBuilt && !s.Rubble && s.Inside == 0 &&
                (s.Def.Archetype.Flight is null || !Grounded(s)) && SetUp(s) && !RadarSilent(s))
            {
                _cruiseSensors.Add((s, SensorEyeRaw(s)));
            }
        }
        foreach (var p in _projectiles)
        {
            if (p.Cruise is not { } c || p.Done)
            {
                continue;
            }
            uint now = 0;
            var b = new AirBody(p);
            foreach (var (s, eye) in _cruiseSensors)
            {
                var bit = Bit(s.Owner);
                if (s.Owner != p.Owner && (now & bit) == 0 && Senses(s, eye, b, SensorDist2(s, eye, b)))
                {
                    now |= bit;
                }
            }
            c.SeenNow = now;
            var fresh = now & ~c.Seen;
            c.Seen |= now;
            if (fresh != 0)
            {
                _incomingEvents.Add(new IncomingEvent(IncomingEventKind.Seen, p.Id, p.Owner, p.Dest, ImpactTickOf(p) ?? Tick, fresh));
            }
            for (byte pl = 1; pl < 32 && now >> pl != 0; pl++)
            {
                if ((now & Bit(pl)) != 0)
                {
                    c.WarnFor(pl, Tick + (uint)c.WarnTicks);
                }
            }
        }
    }

    // WarnedNow is the players warned of cruise missile c now: seen by their sensors within its warning time.
    private uint WarnedNow(CruiseState c)
    {
        uint bits = 0;
        foreach (var (pl, until) in c.Warn)
        {
            if (until >= Tick)
            {
                bits |= Bit(pl);
            }
        }
        return bits | c.SeenNow;
    }

    // LowHidden: the low-flight rule hides air target p from sensor s (eye at eye): p's weapon has the rule, s is not an aircraft, p
    // flies lower than the rule's height over the ground, and it is farther than the rule's sight (level) or the terrain or the
    // treetops cut the straight line from s's eye to it.
    private bool LowHidden(Unit s, long eye, AirBody p)
    {
        if (p.P is not { Weapon.Effect.LowSight: { } ls } pr || s.Def.Archetype.Flight is not null || p.Alt - GroundRaw(p.Pos) >= ls.BelowRaw)
        {
            return false;
        }
        long dx = p.Pos.X.Raw - s.Pos.X.Raw, dy = p.Pos.Y.Raw - s.Pos.Y.Raw;
        if (dx * dx + dy * dy > ls.Sight.Raw * ls.Sight.Raw)
        {
            return true;
        }
        if (Ground is null)
        {
            return false;
        }
        if (_lowSightTick != Tick)
        {
            _lowSightTick = Tick;
            _lowSight.Clear();
        }
        var key = (ulong)s.Id << 32 | pr.Id;
        int lo = 0, hi = _lowSight.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_lowSight[mid].Key < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        var found = lo < _lowSight.Count && _lowSight[lo].Key == key;
        if (found && _lowSight[lo] is var e && e.Sx == s.Pos.X.Raw && e.Sy == s.Pos.Y.Raw && e.Px == p.Pos.X.Raw && e.Py == p.Pos.Y.Raw && e.Alt == p.Alt)
        {
            return e.Hidden;
        }
        var hidden = !ClearSight(s.Pos, eye, p.Pos, p.Alt);
        var entry = (key, s.Pos.X.Raw, s.Pos.Y.Raw, p.Pos.X.Raw, p.Pos.Y.Raw, p.Alt, hidden);
        if (found)
        {
            _lowSight[lo] = entry;
        }
        else
        {
            _lowSight.Insert(lo, entry);
        }
        return hidden;
    }

    /// <summary>
    /// Whether the straight line from a (at height ha) to b (at hb), heights absolute raw, clears the terrain and the treetops:
    /// sampled every half cell, a sample blocks when the ground plus its forest's canopy is above the line there (the low-flight
    /// sight rule's line). Always true on a flat map.
    /// </summary>
    public bool ClearSight(Vec a, long ha, Vec b, long hb)
    {
        if (Ground is not { } g)
        {
            return true;
        }
        long dx = b.X.Raw - a.X.Raw, dy = b.Y.Raw - a.Y.Raw;
        var n = (int)(Math.Max(Math.Abs(dx), Math.Abs(dy)) * 2 / Fixed.OneRaw);
        var canopy = SightRules.CanopyRaw;
        for (var i = 1; i < n; i++)
        {
            var q = P(a.X.Raw + dx * i / n, a.Y.Raw + dy * i / n);
            if (g.HeightAt(q).Raw + canopy[g.Forest(Cell.Of(q))] > ha + (hb - ha) * i / n)
            {
                return false;
            }
        }
        return true;
    }

    // ---- The slope rule ----

    /// <summary>
    /// The steepest grade (percent, <see cref="TerrainMap.Grade"/>) under the footprint of a unit of <paramref name="def"/> standing
    /// on cell <paramref name="c"/>'s centre: every cell its radius's square touches. 0 on a flat map.
    /// </summary>
    public int FootprintGrade(UnitDef def, Cell c)
    {
        if (Ground is not { } g)
        {
            return 0;
        }
        var ctr = c.Center();
        var r = Math.Max(0, def.Archetype.Radius.Raw);
        int x0 = (int)((ctr.X.Raw - r) >> 10), x1 = (int)((ctr.X.Raw + r - 1) >> 10);
        int y0 = (int)((ctr.Y.Raw - r) >> 10), y1 = (int)((ctr.Y.Raw + r - 1) >> 10);
        var worst = 0;
        for (var y = Math.Min(y0, c.Y); y <= Math.Max(y1, c.Y); y++)
        {
            for (var x = Math.Min(x0, c.X); x <= Math.Max(x1, c.X); x++)
            {
                worst = Math.Max(worst, g.Grade(new Cell(x, y)));
            }
        }
        return worst;
    }

    /// <summary>
    /// Whether a unit of <paramref name="def"/> may set up on cell <paramref name="c"/> by its slope rule
    /// (<see cref="Archetype.DeploySlope"/>: no cell under its footprint steeper; the HUD tints the ground where not). Always true for
    /// a unit without the rule and on a flat map; the movement class's grade rule still applies on top.
    /// </summary>
    public bool DeploySlopeOk(UnitDef def, Cell c) => def.Archetype.DeploySlope == 0 || FootprintGrade(def, c) <= def.Archetype.DeploySlope;
}

/// <summary>A cruise missile's route (<see cref="Command.Route"/>, <see cref="Unit.CruiseRoutes"/>): the waypoints in order, then its target.</summary>
/// <param name="Target">The point it dives onto.</param>
/// <param name="Waypoints">The cells it flies through first, in order.</param>
public sealed record CruiseRoute(Cell Target, IReadOnlyList<Cell> Waypoints);

/// <summary>Why <see cref="World.CheckCruiseRoute(Unit, IReadOnlyList{Cell}?, Cell)"/> refuses a route.</summary>
public enum CruiseRouteProblem : byte
{
    /// <summary>It takes it.</summary>
    None,

    /// <summary>The unit has no cruise weapon.</summary>
    NoCruiseWeapon,

    /// <summary>More waypoints than its weapon takes.</summary>
    TooManyWaypoints,

    /// <summary>A waypoint or the target is off the map (<see cref="CruiseRouteCheck.Leg"/>: which, the target last).</summary>
    OffMap,

    /// <summary>Shorter than its weapon's shortest route.</summary>
    TooShort,

    /// <summary>Longer than its weapon's longest route.</summary>
    TooLong,

    /// <summary>A leg's terrain cannot be cleared at its climb and descent limits (<see cref="CruiseRouteCheck.Leg"/>).</summary>
    Terrain,
}

/// <summary>A cruise route's check (<see cref="World.CheckCruiseRoute(Unit, IReadOnlyList{Cell}?, Cell)"/>).</summary>
/// <param name="Problem">Why it is refused, or None.</param>
/// <param name="Leg">For Terrain: the first leg that fails (0 from the launcher to the first point, then one per waypoint); for
/// OffMap the point; -1 otherwise.</param>
/// <param name="Length">The route's length (the straight legs).</param>
public readonly record struct CruiseRouteCheck(CruiseRouteProblem Problem, int Leg, Fixed Length)
{
    /// <summary>Whether the route is taken.</summary>
    public bool Ok => Problem == CruiseRouteProblem.None;
}

/// <summary>A cruise missile in flight (<see cref="Projectile.Cruise"/>): its route, its planned flight and who has seen it.</summary>
public sealed class CruiseState
{
    internal CruiseState(CruiseRoute route, long[] track, int boostTicks, int diveAge, int impactAge, int failAge, int warnTicks, ulong digest)
    {
        Route = route;
        Track = track;
        BoostTicks = boostTicks;
        WarnTicks = warnTicks;
        DiveAge = diveAge;
        ImpactAge = impactAge;
        FailAge = failAge;
        Digest = digest;
    }

    /// <summary>The route it flies.</summary>
    public CruiseRoute Route { get; }

    /// <summary>The age (ticks since launch) its boost ends.</summary>
    public int BoostTicks { get; }

    /// <summary>The age its final dive starts.</summary>
    public int DiveAge { get; }

    /// <summary>The age it reaches its point.</summary>
    public int ImpactAge { get; }

    /// <summary>The age its plan meets the ground (a route its launch could not clear), or -1.</summary>
    public int FailAge { get; }

    /// <summary>The players (a bit, 1 &lt;&lt; player, each) whose sensors have seen it.</summary>
    public uint Seen { get; internal set; }

    /// <summary>The players whose sensors see it now.</summary>
    public uint SeenNow { get; internal set; }

    /// <summary>Per player who saw it (by ID): the last tick of his warning.</summary>
    public IReadOnlyList<(byte Player, uint Until)> Warn => _warn;

    internal int WarnTicks { get; }

    internal long[] Track { get; }

    internal ulong Digest { get; }

    private readonly List<(byte Player, uint Until)> _warn = [];

    internal void WarnFor(byte player, uint until)
    {
        var i = 0;
        while (i < _warn.Count && _warn[i].Player < player)
        {
            i++;
        }
        if (i < _warn.Count && _warn[i].Player == player)
        {
            _warn[i] = (player, Math.Max(until, _warn[i].Until));
        }
        else
        {
            _warn.Insert(i, (player, until));
        }
    }
}

// CruisePath: a cruise route's horizontal path: the straight legs through its points with each corner rounded by an arc (a dense
// polyline of nodes, about a quarter cell apart on the arcs), integer and fixed order.
internal sealed class CruisePath
{
    private const long S = 1 << 16; // unit vectors' scale

    private readonly List<long> _x = [], _y = [], _s = [];
    private readonly List<long> _legEnd = [];

    public long Total => _s.Count == 0 ? 0 : _s[^1];

    public static CruisePath Build(IReadOnlyList<Vec> pts, long radius)
    {
        var p = new CruisePath();
        p.Add(pts[0].X.Raw, pts[0].Y.Raw);
        for (var i = 1; i < pts.Count - 1; i++)
        {
            p.Corner(pts[i - 1], pts[i], pts[i + 1], radius);
        }
        p.Add(pts[^1].X.Raw, pts[^1].Y.Raw);
        p._legEnd.Add(p.Total);
        return p;
    }

    private void Add(long x, long y)
    {
        if (_x.Count > 0)
        {
            long dx = x - _x[^1], dy = y - _y[^1];
            if (dx == 0 && dy == 0)
            {
                return;
            }
            _s.Add(_s[^1] + Fixed.Sqrt(dx * dx + dy * dy));
        }
        else
        {
            _s.Add(0);
        }
        _x.Add(x);
        _y.Add(y);
    }

    // Corner rounds the corner at b (from a, on to c): an arc of the radius tangent to both legs, its tangent points at most half
    // of either leg from b (a tighter arc then); straight on (or a leg of nothing) leaves b a plain node. The leg into b ends at the
    // arc's middle.
    private void Corner(Vec a, Vec b, Vec c, long radius)
    {
        long ix = b.X.Raw - a.X.Raw, iy = b.Y.Raw - a.Y.Raw, ox = c.X.Raw - b.X.Raw, oy = c.Y.Raw - b.Y.Raw;
        long li = Fixed.Sqrt(ix * ix + iy * iy), lo = Fixed.Sqrt(ox * ox + oy * oy);
        if (li == 0 || lo == 0 || radius == 0)
        {
            Add(b.X.Raw, b.Y.Raw);
            _legEnd.Add(Total);
            return;
        }
        long ux = ix * S / li, uy = iy * S / li, vx = ox * S / lo, vy = oy * S / lo;
        var dot = (ux * vx + uy * vy) / S;
        var cross = (ux * vy - uy * vx) / S;
        var ac = Math.Abs(cross);
        if (ac < 16 && dot > 0)
        {
            Add(b.X.Raw, b.Y.Raw); // straight on
            _legEnd.Add(Total);
            return;
        }
        if (S + dot <= S / 64 || ac == 0)
        {
            Add(b.X.Raw, b.Y.Raw); // turning back on itself (within about 10 degrees): a sharp turn on the spot
            _legEnd.Add(Total);
            return;
        }
        // The tangent length d = R tan(turn / 2) = R sin / (1 + cos), capped at half of either leg.
        var d = Math.Min(radius * ac / (S + dot), Math.Min(li, lo) / 2);
        var r = Math.Max(1, d * (S + dot) / ac); // the arc's radius (the content's, or tighter)
        long axp = b.X.Raw - ux * d / S, ayp = b.Y.Raw - uy * d / S; // where the arc starts
        long bxp = b.X.Raw + vx * d / S, byp = b.Y.Raw + vy * d / S; // where it ends
        long nx = cross > 0 ? -uy : uy, ny = cross > 0 ? ux : -ux; // toward the turn
        long cx = axp + nx * r / S, cy = ayp + ny * r / S;
        long mx = b.X.Raw - cx, my = b.Y.Raw - cy;
        var ml = Math.Max(1, Fixed.Sqrt(mx * mx + my * my));
        (mx, my) = (mx * r / ml, my * r / ml); // the arc's middle, from its centre
        Add(axp, ayp);
        Arc(cx, cy, axp - cx, ayp - cy, mx, my, r);
        _legEnd.Add(Total);
        Arc(cx, cy, mx, my, bxp - cx, byp - cy, r);
    }

    // Arc adds the nodes of the arc of radius r round (cx, cy) from radius vector (ax, ay) to (bx, by) (at most a quarter turn
    // apart), about a quarter cell apart, ending on (bx, by).
    private void Arc(long cx, long cy, long ax, long ay, long bx, long by, long r)
    {
        long dx = bx - ax, dy = by - ay;
        var chord = Fixed.Sqrt(dx * dx + dy * dy);
        var n = (int)Math.Clamp(chord / (Fixed.OneRaw / 4), 1, 256);
        for (var k = 1; k <= n; k++)
        {
            long qx = ax + dx * k / n, qy = ay + dy * k / n;
            var ql = Math.Max(1, Fixed.Sqrt(qx * qx + qy * qy));
            Add(cx + qx * r / ql, cy + qy * r / ql);
        }
    }

    // At is the point s along the path (clamped to it); walk is the node index to start from (s never goes back between calls).
    public (long X, long Y) At(long s, ref int walk)
    {
        if (_x.Count == 1 || s <= 0)
        {
            return (_x[0], _y[0]);
        }
        while (walk < _s.Count - 2 && _s[walk + 1] < s)
        {
            walk++;
        }
        if (s >= Total)
        {
            return (_x[^1], _y[^1]);
        }
        var span = Math.Max(1, _s[walk + 1] - _s[walk]);
        var f = s - _s[walk];
        return (_x[walk] + (_x[walk + 1] - _x[walk]) * f / span, _y[walk] + (_y[walk + 1] - _y[walk]) * f / span);
    }

    // LegAt is the leg (0 from the launcher) that point s along the path belongs to.
    public int LegAt(long s)
    {
        for (var i = 0; i < _legEnd.Count; i++)
        {
            if (s <= _legEnd[i])
            {
                return i;
            }
        }
        return _legEnd.Count - 1;
    }
}
