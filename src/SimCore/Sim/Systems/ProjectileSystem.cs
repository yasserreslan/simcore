using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The projectile system: shots in flight travel along their aim line toward the
// point they were aimed at. Each tick the segment a projectile covers is swept
// against units (a circle each, structures by footprint) and blocked terrain;
// the first thing it reaches goes off: a direct hit on that unit (full damage)
// plus splash around the point. Friends count too; the shooter and anyone
// inside a vehicle don't, and flying units only for weapons that hit the air.
// A projectile that reaches its aim point untouched goes off on the ground there.
// Launcher rockets (salvo weapons) fly an arc over everything and only collide on
// the way down, within DescentCells of their aim point.
public sealed partial class World
{
    private const int TerrainSteps = 8; // terrain checks per cell along a projectile's path
    private const int DescentCells = 2;  // salvo rockets collide only this close to their aim point

    private readonly List<Detonation> _detonations = [];

    /// <summary>The projectiles that went off in the last tick, in projectile ID order.</summary>
    public IReadOnlyList<Detonation> Detonations => _detonations;

    private void RunProjectiles()
    {
        _detonations.Clear();
        foreach (var p in _projectiles)
        {
            var d = p.Dest - p.Pos;
            var speed = p.Weapon.Effect.ProjectileSpeed;
            var next = d.Len() > speed ? p.Pos + d.WithLen(speed) : p.Dest;
            var lobbed = p.Weapon.Effect.SalvoSize > 1;
            var from = p.Pos;
            if (lobbed && d.LenSq() > (long)DescentCells * Fixed.OneRaw * DescentCells * Fixed.OneRaw)
            {
                // Still high in the arc: only the part of this tick's path inside the descent zone can hit.
                var reach = d.Len() - Fixed.FromInt(DescentCells);
                from = reach >= speed ? next : p.Pos + d.WithLen(reach);
            }
            if (FirstHit(p, from, next, out var at, out var hit, out var terrain))
            {
                Detonate(p, at, hit, terrain);
                continue;
            }
            p.Pos = next;
            if (next == p.Dest)
            {
                Detonate(p, p.Dest, 0, false);
            }
        }
        _projectiles.RemoveAll(p => p.Done);
    }

    private void Detonate(Projectile p, Vec at, uint hit, bool terrain)
    {
        p.Pos = at;
        p.Done = true;
        var w = p.Weapon;
        if (Unit(hit) is { } t && w.Warhead.CanHit(t.Def.Armor))
        {
            Hurt(t, w.Warhead, w.Damage, 100);
        }
        Explode(at, w.Damage, w.Warhead, w.Effect.Splash, hit, w.Effect.SplashCenter, w.Effect.SplashEdge);
        if (p.Target != 0 && Unit(p.Shooter) is { } shooter)
        {
            // Count shots in a row that missed the unit they were aimed at.
            if (hit == p.Target)
            {
                shooter.MissStreak = 0;
            }
            else
            {
                shooter.MissStreak = shooter.MissTarget == p.Target ? shooter.MissStreak + 1 : 1;
                shooter.MissTarget = p.Target;
            }
        }
        _detonations.Add(new Detonation(p.Id, at, hit, terrain));
    }

    // FirstHit finds the first unit or rock the segment a->b reaches: the smallest
    // distance along it, ties going to the lower unit ID and then to terrain.
    private bool FirstHit(Projectile p, Vec a, Vec b, out Vec at, out uint hit, out bool terrain)
    {
        at = b;
        hit = 0;
        terrain = false;
        var seg = b - a;
        var len = seg.Len().Raw;
        if (len == 0)
        {
            return false;
        }
        var best = long.MaxValue;
        var airToo = (p.Weapon.Effect.Targets & Layers.Air) != 0;
        var lobbed = p.Weapon.Effect.SalvoSize > 1;
        foreach (var u in _units)
        {
            if (!u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Id == p.Shooter || u.Id == p.Cover || (u.Def.Layer == Layers.Air && !airToo) || !IsVisibleTo(u, p.Owner))
            {
                continue;
            }
            var s = u.Def.IsStructure ? EnterBox(a, seg, len, u, lobbed) : EnterCircle(a, seg, len, u.Pos, u.Def.Archetype.Radius.Raw);
            if (s >= 0 && s < best)
            {
                best = s;
                hit = u.Id;
            }
        }
        var steps = (int)((len * TerrainSteps + Fixed.OneRaw - 1) / Fixed.OneRaw);
        var cover = Unit(p.Cover);
        for (var k = 1; k <= steps; k++)
        {
            var q = a + new Vec(Fixed.FromRaw(seg.X.Raw * k / steps), Fixed.FromRaw(seg.Y.Raw * k / steps));
            if (Grid.At(Cell.Of(q)) == Terrain.Blocked && (cover is null || AimPoint(q, cover) != q))
            {
                var s = len * k / steps;
                if (s < best)
                {
                    best = s;
                    hit = 0;
                    terrain = true;
                }
                break;
            }
        }
        if (best == long.MaxValue)
        {
            return false;
        }
        at = a + new Vec(Fixed.FromRaw(seg.X.Raw * best / len), Fixed.FromRaw(seg.Y.Raw * best / len));
        return true;
    }

    // EnterCircle returns how far along the segment (raw units) it enters the circle,
    // or -1. A segment that starts inside the circle is leaving it and never hits it.
    private static long EnterCircle(Vec a, Vec seg, long len, Vec c, long r)
    {
        long fx = a.X.Raw - c.X.Raw, fy = a.Y.Raw - c.Y.Raw;
        var f2 = fx * fx + fy * fy;
        if (f2 <= r * r)
        {
            return -1;
        }
        // Distance along the segment to the point closest to the centre, and that point's distance.
        var along = -(fx * seg.X.Raw + fy * seg.Y.Raw) / len;
        if (along < 0)
        {
            return -1;
        }
        var h2 = f2 - along * along;
        if (h2 > r * r)
        {
            return -1;
        }
        var s = along - Fixed.Sqrt(r * r - h2);
        return s <= len ? Math.Max(s, 0) : -1;
    }

    // EnterBox returns how far along the segment it enters a structure's footprint, or -1. A
    // segment starting inside is leaving it, except for a lobbed rocket coming down: that one
    // lands on the roof where its descent starts.
    private static long EnterBox(Vec a, Vec seg, long len, Unit u, bool lobbed)
    {
        long hw = (long)u.Def.Archetype.FootprintW * Fixed.OneRaw / 2, hh = (long)u.Def.Archetype.FootprintH * Fixed.OneRaw / 2;
        long x0 = u.Pos.X.Raw - hw, x1 = u.Pos.X.Raw + hw, y0 = u.Pos.Y.Raw - hh, y1 = u.Pos.Y.Raw + hh;
        if (a.X.Raw >= x0 && a.X.Raw <= x1 && a.Y.Raw >= y0 && a.Y.Raw <= y1)
        {
            return lobbed ? 0 : -1;
        }
        // Slab test in fractions of the segment, scaled by len to stay in integers.
        long enter = 0, exit = len;
        if (!Slab(a.X.Raw, seg.X.Raw, x0, x1, len, ref enter, ref exit) || !Slab(a.Y.Raw, seg.Y.Raw, y0, y1, len, ref enter, ref exit))
        {
            return -1;
        }
        return enter <= exit ? enter : -1;
    }

    private static bool Slab(long o, long d, long lo, long hi, long len, ref long enter, ref long exit)
    {
        if (d == 0)
        {
            return o >= lo && o <= hi;
        }
        long t0 = (lo - o) * len / d, t1 = (hi - o) * len / d;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }
        enter = Math.Max(enter, t0);
        exit = Math.Min(exit, t1);
        return enter <= exit;
    }
}
