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

    // PickWeapon returns the index of u's weapon that deals the most damage
    // per tick to target, among those that can hit it and have ammo (and,
    // with inRange, are in range), or -1. Ties go to the earlier weapon, so
    // the primary wins over the secondary.
    internal static int PickWeapon(Unit u, Unit target, bool inRange)
    {
        var best = -1;
        long bestScore = 0;
        foreach (var w in u.Def.Weapons)
        {
            if ((u.WeaponMode == WeaponMode.PrimaryOnly && w.Index > 0) ||
                !CanHit(w, target) || !HasAmmo(u, w) || (inRange && (!Within(u.Pos, target.Pos, w.Range) || TooClose(u.Pos, target.Pos, w))))
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
        foreach (var w in u.Def.Weapons)
        {
            if ((u.WeaponMode == WeaponMode.PrimaryOnly && w.Index > 0) || (w.Effect.Targets & Layers.Ground) == 0 || !HasAmmo(u, w))
            {
                continue;
            }
            if (inRange && (!Within(u.Pos, u.Order.Cell.Center(), w.Range) || TooClose(u.Pos, u.Order.Cell.Center(), w)))
            {
                return -1; // the first ground weapon decides; keep closing in
            }
            return w.Index;
        }
        return -1;
    }

    // Explode deals splash damage around at to every unit on the map the
    // warhead can hit, friends included, falling off to SplashEdgePercent at
    // the edge. Drivers are inside their vehicles and safe.
    internal void Explode(Vec at, long damage, Warhead w, Fixed splash, uint except = 0, long center = 100, long edge = SplashEdgePercent)
    {
        var r = splash.Raw;
        foreach (var u in _units)
        {
            if (!u.Alive || u.Inside != 0 || u.Id == except || !w.CanHit(u.Def.Armor))
            {
                continue;
            }
            var d = (u.Pos - at).Len().Raw;
            if (d > r)
            {
                continue;
            }
            var pct = r == 0 ? center : center - (center - edge) * d / r;
            u.HP -= w.Damage(damage, u.Def.Armor, pct);
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
