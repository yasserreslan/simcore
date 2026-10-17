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
    internal static bool CanHit(WeaponDef w, Unit t) =>
        (w.Effect.Targets & t.Def.Layer) != 0 && w.Warhead.CanHit(t.Def.Armor);

    // TooClose reports whether b is inside w's minimum range from a.
    internal static bool TooClose(Vec a, Vec b, WeaponDef w) =>
        w.Effect.MinRange.Raw > 0 && (b - a).LenSq() < w.Effect.MinRange.Raw * w.Effect.MinRange.Raw;

    internal static bool HasAmmo(Unit u, WeaponDef w) => w.Stock < 0 || u.Charges[w.Stock] > 0;

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

    // InReach reports whether u's weapon w reaches point p (from its window when garrisoned).
    internal static bool InReach(Unit u, Vec p, WeaponDef w) => Within(From(u, p), p, Reach(u, w));

    // AimPoint is the point of t that counts for range from p: a structure's
    // footprint point nearest p, anything else's position.
    internal static Vec AimPoint(Vec p, Unit t)
    {
        if (!t.Def.IsStructure)
        {
            return t.Pos;
        }
        return Clamp(p, t.Pos, (long)t.Def.Archetype.FootprintW * Fixed.OneRaw / 2, (long)t.Def.Archetype.FootprintH * Fixed.OneRaw / 2);
    }

    // PickWeapon returns the index of u's weapon that deals the most damage
    // per tick to target, among those that can hit it and have ammo (and,
    // with inRange, are in range), or -1. Ties go to the earlier weapon, so
    // the primary wins over the secondary.
    internal static int PickWeapon(Unit u, Unit target, bool inRange)
    {
        var best = -1;
        long bestScore = 0;
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++) // indexed: foreach over the interface would box an enumerator per call
        {
            var w = weapons[i];
            if ((u.WeaponMode == WeaponMode.PrimaryOnly && w.Index > 0) ||
                !CanHit(w, target) || !HasAmmo(u, w) ||
                (inRange && (!InReach(u, AimPoint(u.Pos, target), w) || TooClose(u.Pos, AimPoint(u.Pos, target), w))))
            {
                continue;
            }
            var s = w.Score(target.Def.Armor);
            if (best < 0 || s > bestScore)
            {
                best = w.Index;
                bestScore = s;
            }
        }
        return best;
    }

    // GroundWeapon returns the index of u's first weapon (primary first) that
    // can fire at the ground and has ammo (and, with inRange, reaches the
    // centre of u's ground target cell), or -1. Weapons that only hit the
    // air (AA missiles) never fire at the ground.
    internal static int GroundWeapon(Unit u, bool inRange)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if ((u.WeaponMode == WeaponMode.PrimaryOnly && w.Index > 0) || (w.Effect.Targets & Layers.Ground) == 0 || !HasAmmo(u, w))
            {
                continue;
            }
            if (inRange && (!InReach(u, u.Order.Cell.Center(), w) || TooClose(u.Pos, u.Order.Cell.Center(), w)))
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
    // percent from there out to splash.
    internal void Explode(Vec at, long damage, Warhead w, Fixed splash, uint except = 0, long center = 100, long edge = SplashEdgePercent,
        Fixed inner = default, long outer = 0)
    {
        var r = splash.Raw;
        var m = r + Fixed.OneRaw;
        foreach (var u in Around(at.X.Raw - m, at.Y.Raw - m, at.X.Raw + m, at.Y.Raw + m, _nearBlast, true))
        {
            if (!u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Id == except || !w.CanHit(u.Def.Armor))
            {
                continue;
            }
            var d = (AimPoint(at, u) - at).Len().Raw;
            if (d > r)
            {
                continue;
            }
            var pct = inner.Raw > 0 ? (d <= inner.Raw ? center : outer)
                : r == 0 ? center : center - (center - edge) * d / r;
            Hurt(u, w, damage, pct);
        }
    }

    // Scatter offsets an aim point by up to the weapon's scatter per axis,
    // drawing x before y from the world's generator.
    private Vec Scatter(WeaponDef w, Vec at)
    {
        var s = (int)w.Effect.Scatter.Raw;
        if (s == 0)
        {
            return at;
        }
        var dx = _rng.Range(-s, s);
        var dy = _rng.Range(-s, s);
        return at + new Vec(Fixed.FromRaw(dx), Fixed.FromRaw(dy));
    }
}
