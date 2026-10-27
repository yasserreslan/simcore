using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Distance, weapon choice and explosions, shared by the systems. All
// integer; ties always break by weapon order or unit ID.
public sealed partial class World
{
    private const long SplashEdgePercent = 50; // damage at the edge of a splash, in percent

    // Within reports whether b is at most r from a.
    internal static bool Within(Vec a, Vec b, Fixed r) => (b - a).LenSq() <= r.Raw * r.Raw;

    // CanHit reports whether weapon w can target t at all: t's layer is one
    // the weapon hits, and the warhead can target t's armor class.
    // A scout drone flies above every weapon but air defence of its class (the air defence system).
    // Untargetable archetypes (an airfield's pavement and open stands) are never targets, nor a bridge whose span is down.
    internal static bool CanHit(WeaponDef w, Unit t) =>
        (w.Effect.Targets & t.Def.Layer) != 0 && w.Warhead.CanHit(t.Def.Armor) && t.Def.Archetype.Flight is null && !t.Def.Archetype.Untargetable &&
        t.Bridge is not { Down: true };

    // TooClose reports whether b is inside w's minimum range from a.
    internal static bool TooClose(Vec a, Vec b, WeaponDef w) =>
        w.Effect.MinRange.Raw > 0 && (b - a).LenSq() < w.Effect.MinRange.Raw * w.Effect.MinRange.Raw;

    internal static bool HasAmmo(Unit u, WeaponDef w) => w.Stock < 0 || u.Charges[w.Stock] > 0;

    /// <summary>
    /// Whether u may fire from where it is: always outside a building, and always for units that
    /// don't set up to fire; a unit that must set up (the mortar) fires from inside only where the
    /// building's garrison lists one of its tags in deployTags (an observation post), and holds fire
    /// in any other building.
    /// </summary>
    public bool FiresFromHere(Unit u) =>
        u.Garrison == 0 || u.Def.Archetype.DeployTicks == 0 ||
        (Unit(u.Garrison)?.Def.Archetype.Garrison is { DeployTags.IsEmpty: false } g && u.Def.Tags.Matches(g.DeployTags));

    // Reach is how far u's weapon w fires: its range, plus the garrison bonus for a soldier in a building.
    internal static Fixed Reach(Unit u, WeaponDef w) => w.Range + u.RangeBonus;

    // From is where u's range and vision toward p count from: a garrisoned
    // soldier's building edge nearest p (its window), a structure's own
    // footprint edge, anyone else's position.
    internal static Vec From(Unit u, Vec p) =>
        u.Garrison != 0 ? Clamp(p, u.Pos, (long)u.Cover.W * Fixed.OneRaw / 2, (long)u.Cover.H * Fixed.OneRaw / 2)
        : u.Def.IsStructure ? AimPoint(p, u)
        : u.Pos;

    private static Vec Clamp(Vec p, Vec c, long hw, long hh) => new(
        Fixed.FromRaw(Math.Clamp(p.X.Raw, c.X.Raw - hw, c.X.Raw + hw)),
        Fixed.FromRaw(Math.Clamp(p.Y.Raw, c.Y.Raw - hh, c.Y.Raw + hh)));

    // ShotFrom is where u's weapons fire from toward p: a structure's gun at its centre (the howitzer; its range and minimum
    // range rings are circles round it), anyone else as From (a garrisoned soldier's window).
    internal static Vec ShotFrom(Unit u, Vec p) => u.Def.IsStructure ? u.Pos : From(u, p);

    // InReach reports whether u's weapon w reaches point p (from its window when garrisoned, a structure's centre).
    // (on terrain the reach depends on the height difference: see ReachTo).
    internal bool InReach(Unit u, Vec p, WeaponDef w) => Within(ShotFrom(u, p), p, ReachTo(u, p, w));

    // AimPoint is the point of t that counts for range from p: a structure's
    // footprint point nearest p, anything else's position.
    internal static Vec AimPoint(Vec p, Unit t)
    {
        if (!t.Def.IsStructure)
        {
            return t.Pos;
        }
        return Clamp(p, t.Pos, (long)FootW(t) * Fixed.OneRaw / 2, (long)FootH(t) * Fixed.OneRaw / 2);
    }

    // PickWeapon returns the index of u's weapon that deals the most damage
    // per tick to target, among those that can hit it and have ammo (and,
    // with inRange, are in range), or -1. Ties go to the earlier weapon, so
    // the primary wins over the secondary. A coaxial weapon (a tank's machine guns) is picked only when
    // no other weapon qualifies (it fires alongside the main weapon on its own: FireCoaxial).
    internal int PickWeapon(Unit u, Unit target, bool inRange)
    {
        if (u.Garrison != 0 && !FiresFromHere(u))
        {
            return -1; // a mortar in a building other than an observation post
        }
        var best = -1;
        long bestScore = 0;
        var bestCoax = false;
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++) // indexed: foreach over the interface would box an enumerator per call
        {
            var w = weapons[i];
            if ((u.WeaponMode == WeaponMode.PrimaryOnly && w.Index > 0) || w.Effect.Interceptor is not null || w.Effect.Cruise is not null || // a cruise missile: ground points only
                !CanHit(w, target) || !HasAmmo(u, w) ||
                (inRange && (!InReach(u, AimPoint(u.Pos, target), w) || TooClose(u.Pos, AimPoint(u.Pos, target), w) ||
                             !LineOfFire(u, AimPoint(u.Pos, target), w, target))))
            {
                continue;
            }
            var s = w.Score(target.Def.Armor);
            var coax = w.Effect.Coaxial;
            if (best < 0 || (bestCoax && !coax) || (coax == bestCoax && s > bestScore))
            {
                best = w.Index;
                bestScore = s;
                bestCoax = coax;
            }
        }
        return best;
    }

    // GroundWeapon returns the index of u's first weapon (primary first) that
    // can fire at the ground and has ammo (and, with inRange, reaches the
    // centre of u's ground target cell), or -1. Weapons that only hit the
    // air (AA missiles) never fire at the ground.
    internal int GroundWeapon(Unit u, bool inRange)
    {
        if (u.Garrison != 0 && !FiresFromHere(u))
        {
            return -1; // a mortar in a building other than an observation post
        }
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if ((u.WeaponMode == WeaponMode.PrimaryOnly && w.Index > 0) || (w.Effect.Targets & Layers.Ground) == 0 || !HasAmmo(u, w) ||
                w.Effect.Interceptor is not null || w.Effect.Airdrop is not null) // (logistics: cargo pallets only on an Airdrop order)
            {
                continue;
            }
            if (inRange && (!InReach(u, u.Order.Cell.Center(), w) || TooClose(u.Pos, u.Order.Cell.Center(), w) ||
                            !LineOfFire(u, u.Order.Cell.Center(), w, null)))
            {
                return -1; // the first ground weapon decides; keep closing in
            }
            return w.Index;
        }
        return -1;
    }

    // Explode deals splash damage around at to every unit on the map the
    // warhead can hit, friends included, falling off to SplashEdgePercent at
    // the edge (a structure counts from its nearest footprint point). Drivers are
    // inside their vehicles and safe; soldiers in a building get their share
    // through the building's hit (see Hurt).
    // With inner above zero it is a two-ring splash: center percent within inner, a flat outer
    // percent from there out to splash. On terrain the distance is 3D, and a unit behind a crest
    // takes ShieldedPercent of its share (never inside the inner ring; direct hits are separate).
    // A trap's blast (trapBlast) skips traps (they chain instead: the trigger system) and leaves every
    // unit with a spare tag that it would kill at 1 HP.
    internal void Explode(Vec at, long damage, Warhead w, Fixed splash, uint except = 0, long center = 100, long edge = SplashEdgePercent,
        Fixed inner = default, long outer = 0, bool trapBlast = false, TagSet spare = default)
    {
        var r = splash.Raw;
        var m = r + Fixed.OneRaw;
        (_blastHas, _blastAt) = (true, at); // directional armour takes a splash from where it went off (ArmorSystem.cs)
        foreach (var u in Around(at.X.Raw - m, at.Y.Raw - m, at.X.Raw + m, at.Y.Raw + m, _nearBlast, true))
        {
            if (!u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Id == except || (u.Def.Archetype.Flight is not null && !Grounded(u)) || !w.CanHit(ArmorOf(u)))
            {
                continue;
            }
            var q = AimPoint(at, u);
            var d = Ground is null ? (q - at).Len().Raw : SplashDistance(at, q); // 3D on terrain
            if (d > r)
            {
                continue;
            }
            var innerRing = inner.Raw > 0 && d <= inner.Raw;
            var pct = inner.Raw > 0 ? (innerRing ? center : outer)
                : r == 0 ? center : center - (center - edge) * d / r;
            if (!innerRing && Ground is not null && Shielded(at, q))
            {
                pct = pct * ShieldedPercent / 100; // a crest between the blast and the unit
            }
            if (trapBlast && u.Def.Trigger is not null)
            {
                continue;
            }
            Hurt(u, w, damage, pct);
            if (!spare.IsEmpty && u.HP <= 0 && u.Def.Tags.Matches(spare))
            {
                u.HP = 1;
            }
        }
        _blastHas = false;
        FellByBlast(at, w, splash); // trees blown down round it (TreeSystem.cs)
        IgniteByBlast(at, w, splash); // then the forest round it set on fire, if the warhead does (FireSystem.cs)
    }

    /// <summary>
    /// The scatter (raw, per axis) of weapon w for a shot over a horizontal distance (raw): its
    /// flat scatter, or with <see cref="WeaponEffect.ScatterNear"/> set, rising linearly from that
    /// at the minimum range (point blank without one) to the full scatter at the weapon's range.
    /// </summary>
    public static long ScatterAt(WeaponDef w, long distanceRaw)
    {
        var e = w.Effect;
        if (e.ScatterNear.Raw <= 0)
        {
            return e.Scatter.Raw;
        }
        var span = w.Range.Raw - e.MinRange.Raw;
        if (span <= 0)
        {
            return e.Scatter.Raw;
        }
        var t = Math.Clamp(distanceRaw - e.MinRange.Raw, 0, span);
        return e.ScatterNear.Raw + (e.Scatter.Raw - e.ScatterNear.Raw) * t / span;
    }

    /// <summary>Whether a ground order at p could be fired by u from where it stands (see <see cref="ShotAt"/>).</summary>
    public enum GroundShot
    {
        /// <summary>No weapon of u fires at the ground.</summary>
        NoWeapon,

        /// <summary>In range: it fires from where it stands (on terrain, once it can set up there).</summary>
        InRange,

        /// <summary>Inside the weapon's minimum range: it never fires there.</summary>
        TooClose,

        /// <summary>Beyond its reach: it walks closer first.</summary>
        OutOfRange,
    }

    /// <summary>
    /// The range rule of attack ground for a point p, from where u stands now, with its first weapon
    /// that fires at the ground (ammo aside): the client's fire radius. Pure: it changes nothing.
    /// </summary>
    public GroundShot ShotAt(Unit u, Vec p)
    {
        if (!FiresFromHere(u))
        {
            return GroundShot.NoWeapon; // a mortar in a building other than an observation post
        }
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if ((u.WeaponMode == WeaponMode.PrimaryOnly && w.Index > 0) || (w.Effect.Targets & Layers.Ground) == 0 || w.Effect.Interceptor is not null)
            {
                continue;
            }
            return TooClose(u.Pos, p, w) ? GroundShot.TooClose
                : !InReach(u, p, w) || !LineOfFire(u, p, w, null) ? GroundShot.OutOfRange
                : GroundShot.InRange;
        }
        return GroundShot.NoWeapon;
    }

    // Scatter offsets an aim point by up to the weapon's scatter per axis (growing with the
    // distance for a weapon with ScatterNear), drawing x before y from the world's generator.
    private Vec Scatter(WeaponDef w, Vec at, Unit? from = null)
    {
        var s = (int)(from is not null ? ScatterAt(w, (at - from.Pos).Len().Raw) : w.Effect.Scatter.Raw);
        if (from is not null && s > 0 && Ground is not null)
        {
            s = s * ScatterPercent(from.Pos, at) / 100; // from high ground tighter, from below wider
        }
        if (s == 0)
        {
            return at;
        }
        var dx = _rng.Range(-s, s);
        var dy = _rng.Range(-s, s);
        return at + new Vec(Fixed.FromRaw(dx), Fixed.FromRaw(dy));
    }
}
