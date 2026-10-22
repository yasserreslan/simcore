using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// The gunship's orbit attack (H3; the game repo's docs/heavy-air-plan.md, sections 1.2 and 2.2; archetype `gunship`,
// weapons with a `sideFire` block). All integer; rolls through Rand.Hash (no generator state); units by ID.
//
//  - The order: an attack-ground order at a cell (the area's centre) or an attack order at a ground unit (the area
//    follows it while its side sees it, else its last known cell). A gunship never flies strike runs: it orbits.
//  - The orbit (Unit.Gunship, hashed while present): a left-hand pylon turn of the archetype's radius round the
//    centre (counter-clockwise on the map, y down: the centre on its left wing), the flight controller chasing the
//    circle's point 30 degrees ahead (the drones' orbit law). It is on the circle within `onCircle` of the radius with
//    the centre within `bearing` degrees of its left wing; on its way in, or swung wide, it holds its fire.
//  - The side cannons: each fires on its own reload, only on the circle, at the ordered unit when its armour class is
//    in the weapon's priority, else at the best enemy its side sees inside the area (the earliest listed class, then
//    the nearest to the centre, or the most max HP for a "biggest" pick, ties to the lower ID); with none, a weapon
//    with `areaFire` fires at a seeded point inside the area on an attack-ground order. A moving target is led by the
//    round's flight time; every round is scattered (seeded, per axis). The round flies straight at the weapon's speed
//    (Projectile.Bomb with Shell set) and goes off at its point like a bomb: the direct hit, the splash, the runway
//    effect, World.BombImpacts (with the weapon's look tier).
//  - The end: it orbits and fires until every side stock is empty (then home on its own to rearm: refuelled, rearmed
//    and repaired, then held parked until its next order; NoAmmo reported), another order, or bingo fuel (the order ends: Bingo; it refuels and
//    resumes its task, the E2 rule). The ordered unit destroyed, it works the area on as an attack-ground order there
//    (TargetDead reported).
//  - Events for renderers: World.SideFires (each round: the weapon, the muzzle position and height, the aim, the impact
//    tick, the look tier, area fire or not); the impacts in World.BombImpacts (Shell set).
public sealed partial class World
{
    // With nothing to fire at, a side cannon looks again this often (ticks): twice a second.
    private const int SideScanTicks = TicksPerSecond / 2;

    // Salts for the side cannons' scatter and area-fire rolls.
    private const ulong SideXSalt = 0x51DE_0001_0000_0000UL, SideYSalt = 0x51DE_0002_0000_0000UL,
        AreaXSalt = 0x51DE_0003_0000_0000UL, AreaYSalt = 0x51DE_0004_0000_0000UL;

    private readonly List<SideFireShot> _sideFires = [];

    /// <summary>The gunships' side-cannon rounds fired in the last tick, in order (H3): draw the muzzle flash and tracer.</summary>
    public IReadOnlyList<SideFireShot> SideFires => _sideFires;

    // HasSideFire reports whether u carries a gunship's side cannon (H3).
    private static bool HasSideFire(UnitDef d)
    {
        foreach (var w in d.Weapons)
        {
            if (w.Effect.SideFire is not null)
            {
                return true;
            }
        }
        return false;
    }

    // SideAmmo reports whether any of u's side cannons has a round left.
    private static bool SideAmmo(Unit u)
    {
        foreach (var w in u.Def.Weapons)
        {
            if (w.Effect.SideFire is not null && HasAmmo(u, w))
            {
                return true;
            }
        }
        return false;
    }

    // GunshipOrder runs gunship u's attack or attack-ground order (called by the scout system each tick it flies free,
    // instead of the strike runs). It reports whether it handled u's order (false: not an attack on the ground).
    private bool GunshipOrder(Unit u, ScoutFlight f, GunshipSpec gs)
    {
        var kind = u.Order.Kind;
        if (kind is not (OrderKind.Attack or OrderKind.AttackGround))
        {
            u.Gunship = null;
            return false;
        }
        u.Strike = null;
        u.MutableAims.Clear();
        Unit? t = null;
        if (kind == OrderKind.Attack)
        {
            t = Unit(u.Order.Target);
            if (t is not null && AirBodyOfUnit(t) is not null)
            {
                u.Gunship = null;
                return false; // an air target: it has no air weapons (the scout system ends the order)
            }
            if (t is not { Alive: true } || t.Owner == u.Owner)
            {
                if (u.Gunship is not { } was)
                {
                    GiveUp(u, OrderRefusalReason.TargetDead);
                    return true;
                }
                // The ordered unit is gone: it works the area on (an attack-ground order there).
                Refuse(u, OrderRefusalReason.TargetDead, u.Order.Target, u.Order.Cell);
                u.Order = new Order(OrderKind.AttackGround, Cell.Of(was.Centre), 0, 0);
                t = null;
            }
            else if (IsVisibleTo(t, u.Owner))
            {
                if (u.Order.Cell != Cell.Of(t.Pos))
                {
                    u.Order = u.Order with { Cell = Cell.Of(t.Pos) }; // its last known cell
                }
            }
            else
            {
                t = null; // unseen (or inside something): it orbits the last known cell
            }
        }
        if (u.Homing)
        {
            u.Gunship = null;
            if (u.HomeReason == HomeReason.Refuel)
            {
                GiveUp(u, OrderRefusalReason.Bingo); // bingo fuel: home to refuel, then its task
            }
            else
            {
                EndStrike(u); // its own Rearm or ReturnToBase order
            }
            return true;
        }
        if (!SideAmmo(u))
        {
            // Every side stock empty: home on its own to rearm (refuelled, rearmed and repaired in its heavy bay), then held
            // there until its next order (2026-10-21 fix: it was a recall, refuelled only, and the hold was lost).
            Refuse(u, OrderRefusalReason.NoAmmo, u.Order.Kind == OrderKind.Attack ? u.Order.Target : 0, u.Order.Cell);
            u.Gunship = null;
            GoHome(u, null, HomeReason.Rearm, true);
            return true;
        }
        var g = u.Gunship ??= new GunshipOrbit(u.Def.Weapons.Count) { Since = Tick };
        var centre = Inner(f, t?.Pos ?? u.Order.Cell.Center(), gs.RadiusRaw);
        (g.Centre, g.Target) = (centre, u.Order.Kind == OrderKind.Attack ? u.Order.Target : 0);
        g.OnCircle = OnGunCircle(u, g.Centre, gs);
        if (g.OnCircle && !g.Joined)
        {
            g.Joined = true;
        }
        FireSides(u, g, gs, t);
        return true;
    }

    // OnGunCircle reports whether gunship u is on its firing circle round c: within onCircle of the radius (level
    // distance), the centre within bearing degrees of its left wing (y down: left of a heading (x, y) is (y, -x)).
    private bool OnGunCircle(Unit u, Vec c, GunshipSpec gs)
    {
        long rx = c.X.Raw - u.Pos.X.Raw, ry = c.Y.Raw - u.Pos.Y.Raw;
        var d = Len3(rx, ry, 0);
        if (Math.Abs(d - gs.RadiusRaw) > gs.OnCircleRaw || d == 0)
        {
            return false;
        }
        var (vx, vy) = u.Wing is { } st ? (st.VelX, st.VelY) : (u.Vel.X.Raw, u.Vel.Y.Raw);
        var vl = Len3(vx, vy, 0);
        if (vl == 0)
        {
            return false;
        }
        var dot = vy * rx - vx * ry; // the left wing's direction (vy, -vx) dotted with the way to the centre
        return dot * 1000 >= CosDeg(gs.Bearing) * vl * d;
    }

    // GunshipSteer is where gunship u steers: the drones' orbit law (the point of a circle 30 degrees ahead), counter-
    // clockwise on the map (a left-hand orbit), which brings it onto the circle from anywhere. Chasing a point 30 degrees
    // ahead it settles inside that circle by cos 30 degrees, so the circle it chases is its radius / cos 30 degrees: it
    // flies its radius. Several of its owner's gunships on one area space themselves round it (GunshipTrim).
    private Vec GunshipSteer(Unit u, ScoutFlight f, GunshipOrbit g, GunshipSpec gs, out long trim)
    {
        trim = f.Wing is { } wing ? GunshipTrim(u, wing, g) : 0;
        return OrbitPoint(u, g.Centre, gs.RadiusRaw * 1000 / OrbitAheadCos, false);
    }

    // GunshipTrim is the speed (raw a second; 0: its own) gunship u flies to space itself on a circle it shares with its
    // owner's other gunships on their circles round the same centre (within a cell): n of them, it slows (down to its
    // corner speed) while the nearest one ahead of it on the circle is less than 360 / n degrees ahead, else it flies its
    // own speed; nobody speeds up, so the gaps settle even (the one with the widest gap ahead sets the pace). Angles are
    // compared through a pseudo-angle (monotonic, 0 to 4,000 for 0 to 360 degrees ahead: no trigonometry).
    private long GunshipTrim(Unit u, FixedWingSpec wing, GunshipOrbit g)
    {
        if (!g.Joined)
        {
            return 0;
        }
        long ax = u.Pos.X.Raw - g.Centre.X.Raw, ay = u.Pos.Y.Raw - g.Centre.Y.Raw;
        var al = Len3(ax, ay, 0);
        var n = 1;
        var nearest = long.MaxValue;
        foreach (var o in _units) // in ID order
        {
            if (o == u || o.Gunship is not { Joined: true } og || !o.Alive || o.Owner != u.Owner || !Within(og.Centre, g.Centre, Fixed.One))
            {
                continue;
            }
            n++;
            long bx = o.Pos.X.Raw - g.Centre.X.Raw, by = o.Pos.Y.Raw - g.Centre.Y.Raw;
            var l = al * Len3(bx, by, 0);
            if (l == 0)
            {
                continue;
            }
            var cos = (ax * bx + ay * by) * 1000 / l;
            var p = ax * by - ay * bx < 0 ? 1000 - cos : 3000 + cos; // counter-clockwise on the map (y down): ahead below 2,000
            nearest = Math.Min(nearest, p);
        }
        if (n < 2 || nearest == long.MaxValue)
        {
            return 0;
        }
        var deg = 360 / n;
        var want = deg >= 180 ? 2000 : 1000 - CosDeg(deg);
        if (nearest >= want)
        {
            return 0;
        }
        return Math.Max(wing.Corner, wing.Cruise - (want - nearest) * (wing.Cruise - wing.Corner) / 500);
    }

    // FireSides runs each side cannon of u one tick: its reload, then (on the circle, with a round) a target or an
    // area point and a round at it.
    private void FireSides(Unit u, GunshipOrbit g, GunshipSpec gs, Unit? ordered)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if (w.Effect.SideFire is not { } sf)
            {
                continue;
            }
            if (g.ReloadArr[i] > 0)
            {
                g.ReloadArr[i]--;
                continue;
            }
            if (!g.OnCircle || !HasAmmo(u, w))
            {
                continue;
            }
            var t = SideTarget(u, g, gs, w, sf, ordered);
            Vec aim;
            var area = false;
            if (t is not null)
            {
                aim = t.Pos;
            }
            else if (sf.AreaFire && u.Order.Kind == OrderKind.AttackGround)
            {
                aim = AreaPoint(u, g, gs, w);
                area = true;
            }
            else
            {
                g.ReloadArr[i] = SideScanTicks; // nothing to fire at: look again soon
                continue;
            }
            if (!FireSide(u, g, w, sf, t, aim, area))
            {
                g.ReloadArr[i] = SideScanTicks; // out of its reach (slant)
                continue;
            }
            g.ReloadArr[i] = Math.Max(1, w.Reload);
        }
    }

    // SideTarget is side cannon w's target: the ordered unit when its armour class is in the weapon's priority and it is
    // in the area, else the best enemy u's side sees inside the area (the earliest listed class, then the nearest to the
    // centre, or with a "biggest" pick the most max HP; ties to the lower ID); null for none.
    private Unit? SideTarget(Unit u, GunshipOrbit g, GunshipSpec gs, WeaponDef w, SideFire sf, Unit? ordered)
    {
        var area = Fixed.FromRaw(gs.AreaRaw);
        if (ordered is not null && SideMayHit(u, w, sf, ordered) >= 0 && Within(ordered.Pos, g.Centre, area))
        {
            return ordered;
        }
        Unit? best = null;
        int bestRank = 0;
        long bestKey = 0;
        foreach (var t in _units)
        {
            var rank = SideMayHit(u, w, sf, t);
            if (rank < 0 || !Within(t.Pos, g.Centre, area))
            {
                continue;
            }
            var key = sf.Biggest ? -(long)t.Def.MaxHp : (t.Pos - g.Centre).LenSq();
            if (best is null || rank < bestRank || (rank == bestRank && key < bestKey))
            {
                (best, bestRank, bestKey) = (t, rank, key); // units in ID order: a tie keeps the lower ID
            }
        }
        return best;
    }

    // SideMayHit is the priority rank of t for side cannon w of u (-1: not a target): an enemy on the ground that u's side
    // sees, of a listed armour class its warhead can hit.
    private int SideMayHit(Unit u, WeaponDef w, SideFire sf, Unit t)
    {
        if (!t.Alive || t.Owner == u.Owner || t.Owner == 0 || t.Def.Archetype.Untargetable || !IsVisibleTo(t, u.Owner) ||
            AirBodyOfUnit(t) is not null)
        {
            return -1;
        }
        var armor = ArmorOf(t);
        if (armor < 0 || armor >= sf.Priority.Count || sf.Priority[armor] < 0 || !w.Warhead.CanHit(armor))
        {
            return -1;
        }
        return sf.Priority[armor];
    }

    // AreaPoint is a seeded point inside u's area (area fire): uniform in the disc (a square draw, pulled in to the disc
    // along its radius when it falls outside), on the map.
    private Vec AreaPoint(Unit u, GunshipOrbit g, GunshipSpec gs, WeaponDef w)
    {
        var r = gs.AreaRaw;
        var seed = Match.Setup.Seed;
        var salt = (ulong)(uint)w.Index << 16 | (ulong)(uint)g.Fired;
        var dx = (long)(Rand.Hash(seed, Tick, u.Id, salt | AreaXSalt) % (ulong)(2 * r + 1)) - r;
        var dy = (long)(Rand.Hash(seed, Tick, u.Id, salt | AreaYSalt) % (ulong)(2 * r + 1)) - r;
        var d = Len3(dx, dy, 0);
        if (d > r)
        {
            (dx, dy) = (dx * r / d, dy * r / d);
        }
        return OnMap(g.Centre.X.Raw + dx, g.Centre.Y.Raw + dy);
    }

    // FireSide fires one round of side cannon w from u at aim (t: the target, null for area fire): led by its flight
    // time when t moves, an aimed round scattered (seeded, per axis), within the weapon's slant range (false: out of
    // reach, nothing fired); a round, the projectile, the event.
    private bool FireSide(Unit u, GunshipOrbit g, WeaponDef w, SideFire sf, Unit? t, Vec aim, bool area)
    {
        var speed = Math.Max(1, w.Effect.ProjectileSpeed.Raw);
        var drop = u.Altitude.Raw - GroundRaw(aim);
        var slant = Len3(aim.X.Raw - u.Pos.X.Raw, aim.Y.Raw - u.Pos.Y.Raw, drop);
        if (slant > w.Range.Raw)
        {
            return false;
        }
        var eta = slant / speed + 1;
        if (t is not null && t.Vel != default)
        {
            aim = OnMap(aim.X.Raw + t.Vel.X.Raw * eta, aim.Y.Raw + t.Vel.Y.Raw * eta); // led by the round's flight
        }
        _nextId++;
        var id = _nextId;
        var s = sf.ScatterRaw;
        if (s > 0 && !area)
        {
            // Aimed rounds only: an area-fire point is a seeded point of the area already.
            var seed = Match.Setup.Seed;
            var dx = (long)(Rand.Hash(seed, Tick, u.Id, id | SideXSalt) % (ulong)(2 * s + 1)) - s;
            var dy = (long)(Rand.Hash(seed, Tick, u.Id, id | SideYSalt) % (ulong)(2 * s + 1)) - s;
            aim = OnMap(aim.X.Raw + dx, aim.Y.Raw + dy);
        }
        slant = Len3(aim.X.Raw - u.Pos.X.Raw, aim.Y.Raw - u.Pos.Y.Raw, u.Altitude.Raw - GroundRaw(aim));
        var impact = Tick + (uint)(slant / speed + 1);
        var tier = sf.Tier > 0 ? sf.Tier : StrikeTier(w.Damage);
        var p = new Projectile(id, u, w, aim, t?.Id ?? 0)
        {
            Altitude = u.Altitude,
            Bomb = new BombFall { Guided = true, Shell = true, Predicted = aim, ImpactTick = impact, Tier = tier },
        };
        _projectiles.Add(p);
        if (w.Stock >= 0)
        {
            u.MutableCharges[w.Stock]--;
        }
        u.LastShot = Tick;
        g.Fired++;
        _sideFires.Add(new SideFireShot(id, u.Id, w.Index, t?.Id ?? 0, u.Pos, u.Altitude, aim, impact, tier, area));
        return true;
    }

    /// <summary>
    /// Every air unit (an aircraft flying free or an air-body unit in the air) that radar structure <paramref name="radar"/>
    /// detects itself now, in ID order (H1 follow-up, H3): enemies within its air vision (slant from its top, scaled by
    /// each target's signature), the ones its dish follows. Empty when it is off (rubble, unbuilt, dead, packed up) or has no
    /// air vision. Read-only: the same rule that feeds its side's air tracking.
    /// </summary>
    public List<Unit> RadarTracks(Unit radar)
    {
        var list = new List<Unit>();
        if (radar.Def.Archetype.AirVision.Raw == 0 || !radar.Alive || !radar.IsBuilt || radar.Rubble || radar.Inside != 0 || !SetUp(radar))
        {
            return list; // (a unit that sets up, the radar truck or the mobile radar, tracks only set up: as it senses)
        }
        var eye = SensorEyeRaw(radar);
        foreach (var t in _units)
        {
            if (t.Owner == radar.Owner || !t.Alive || AirBodyOfUnit(t) is not { } b)
            {
                continue;
            }
            if (DetectsAir(radar, eye, b))
            {
                list.Add(t);
            }
        }
        return list;
    }
}

/// <summary>
/// A gunship's orbit attack in progress (H3; <see cref="Unit.Gunship"/>, hashed while present): the area's centre, the
/// ordered unit, whether it is on its firing circle, and each side cannon's reload.
/// </summary>
public sealed class GunshipOrbit
{
    internal GunshipOrbit(int weapons) => ReloadArr = new int[weapons];

    internal int[] ReloadArr { get; }

    /// <summary>The area's centre it orbits (the ordered cell's centre, or the ordered unit's position; on the map).</summary>
    public Vec Centre { get; internal set; }

    /// <summary>The unit it was ordered to attack (the area follows it), or 0 for a point.</summary>
    public uint Target { get; internal set; }

    /// <summary>Whether it is on its firing circle this tick (within onCircle of the radius, the centre off its left wing): only then it fires.</summary>
    public bool OnCircle { get; internal set; }

    /// <summary>Whether it has been on the circle since the order began (its way in is over).</summary>
    public bool Joined { get; internal set; }

    /// <summary>The tick the orbit began.</summary>
    public uint Since { get; internal set; }

    /// <summary>The rounds it has fired on this orbit.</summary>
    public int Fired { get; internal set; }

    /// <summary>Each weapon's ticks to its next round (by weapon index; 0 for weapons that are not side cannons).</summary>
    public IReadOnlyList<int> Reloads => ReloadArr;

    internal void HashInto(ref WordHash h)
    {
        h.Put(0x6A5);
        h.PutSigned(Centre.X.Raw);
        h.PutSigned(Centre.Y.Raw);
        h.Put(Target);
        h.Put(OnCircle ? 1UL : 0);
        h.Put(Joined ? 1UL : 0);
        h.Put(Since);
        h.PutSigned(Fired);
        h.PutSigned(ReloadArr.Length);
        foreach (var r in ReloadArr)
        {
            h.PutSigned(r);
        }
    }
}

/// <summary>A gunship's side-cannon round fired this tick (H3; <see cref="World.SideFires"/>), for renderers and logs.</summary>
/// <param name="Round">The round's projectile ID (its flight: <see cref="World.Projectiles"/>, <see cref="Projectile.Bomb"/> with Shell set).</param>
/// <param name="Aircraft">The gunship.</param>
/// <param name="Weapon">The weapon's index on the gunship (its archetype's weapon order: the light, heavy and very big cannons).</param>
/// <param name="Target">The unit it was fired at, or 0 (area fire).</param>
/// <param name="From">The gunship's position (the muzzles are on its left side).</param>
/// <param name="FromAltitude">The gunship's height (absolute, raw).</param>
/// <param name="Aim">Where the round will land (scattered and led).</param>
/// <param name="ImpactTick">The tick it lands.</param>
/// <param name="Tier">The airstrike tier its impact looks like (the weapon's look, else by its damage).</param>
/// <param name="AreaFire">Whether it was fired at a seeded point of the area (no target).</param>
public readonly record struct SideFireShot(uint Round, uint Aircraft, int Weapon, uint Target, Vec From, Fixed FromAltitude, Vec Aim,
    uint ImpactTick, int Tier, bool AreaFire);
