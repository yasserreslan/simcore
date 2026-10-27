using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Terrain physics (docs/terrain-design.md, phase 1): heights for range, splash, line of sight,
// ballistic arcs and deploying. Everything here applies only on a map with terrain (Ground);
// on a flat map every rule reduces to the flat one, so flat games play and hash as before.
public sealed partial class World
{
    // Heights in raw fixed point: 1024 per cell of 2 m, so 512 per metre.
    private const long RawPerMetre = Fixed.OneRaw / 2;

    /// <summary>Range per height: one cell more per 4 m the shooter stands above the target (less below).</summary>
    public const long RangePerHeightRaw = 4 * RawPerMetre;

    /// <summary>The height bonus or penalty to a weapon's range is capped at this percent of it.</summary>
    public const int RangeHeightCapPercent = 20;

    /// <summary>Splash beyond a crest: this percent of the damage (outer ring only for two-ring blasts).</summary>
    public const int ShieldedPercent = 50;

    // Heights of the line of a blast and of a unit for shielding: 0.5 m above the ground.
    private const long BlastEyeRaw = RawPerMetre / 2;

    // Ballistic arcs (the client draws the same parabola): launch heights above the ground (a
    // launcher's tubes, a shoulder), the height over the aim point's ground it comes down to,
    // and the arc's rise as a share of the range, in permille.
    private const long LaunchSalvoRaw = 2458 * RawPerMetre / 1024;   // 2.4 m
    private const long LaunchShoulderRaw = 1485 * RawPerMetre / 1024; // 1.45 m
    private const long LandRaw = 307 * RawPerMetre / 1024;            // 0.3 m
    private const long FlatArcRaw = 154 * RawPerMetre / 1024;         // 0.15 m: shoulder-fired, nearly flat
    private const int HighArcFromCells = 70;                          // launchers that reach beyond: the high arc

    // Deploying: launchers set up only on Flat or Gentle ground, and look this far for some.
    private const int DeploySearch = 4;

    /// <summary>The ground height at p (0 on a flat map).</summary>
    public Fixed GroundHeight(Vec p) => Ground?.HeightAt(p) ?? Fixed.Zero;

    /// <summary>
    /// Whether the straight line from a (at height ha) to b (at hb), heights absolute, clears the
    /// terrain: sampled every half cell, a sample blocks when the ground is above the line there.
    /// Not used for gameplay yet (fog of war will); always true on a flat map.
    /// </summary>
    public bool LineOfSight(Vec a, Fixed ha, Vec b, Fixed hb)
    {
        if (Ground is not { } g)
        {
            return true;
        }
        var d = b - a;
        var n = (int)(Math.Max(Math.Abs(d.X.Raw), Math.Abs(d.Y.Raw)) * 2 / Fixed.OneRaw);
        for (var i = 1; i < n; i++)
        {
            var p = new Vec(Fixed.FromRaw(a.X.Raw + d.X.Raw * i / n), Fixed.FromRaw(a.Y.Raw + d.Y.Raw * i / n));
            var line = ha.Raw + (hb.Raw - ha.Raw) * i / n;
            if (g.HeightAt(p).Raw > line)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The horizontal distance within which u's weapon w reaches the point p: on terrain the range
    /// grows by a cell per 4 m the shooter stands above p and shrinks as much below (capped at
    /// 20% of the range), and counts in 3D. The weapon's own range on a flat map.
    /// </summary>
    internal Fixed ReachTo(Unit u, Vec p, WeaponDef w)
    {
        var r = Reach(u, w);
        if (Ground is not { } g)
        {
            return r;
        }
        var dh = g.HeightAt(u.Pos).Raw - g.HeightAt(p).Raw;
        var cap = r.Raw * RangeHeightCapPercent / 100;
        var eff = r.Raw + Math.Clamp(dh * Fixed.OneRaw / RangePerHeightRaw, -cap, cap);
        var flat = eff * eff - dh * dh;
        return Fixed.FromRaw(flat <= 0 ? 0 : Fixed.Sqrt(flat));
    }

    // Eye and aim heights for lines of fire (raw, above the ground): a soldier's or a gunner's
    // eye, a soldier in a building's window, a body's centre, a wall.
    private const long EyeRaw = 3 * RawPerMetre / 2, VehicleEyeRaw = 2 * RawPerMetre, WindowRaw = 3 * RawPerMetre;
    private const long BodyRaw = RawPerMetre, WallRaw = 2 * RawPerMetre, GroundAimRaw = 3 * RawPerMetre / 10;

    /// <summary>Direct-fire weapons (everything not lobbed) need a line of fire over the terrain.</summary>
    public static bool DirectFire(WeaponDef w) => !w.Effect.Lobbed;

    /// <summary>
    /// Whether u's weapon w has a line of fire to the point p (of target t, or the ground when
    /// t is null): lobbed salvos and shots at aircraft always do; on a flat map always. The line
    /// runs from the shooter's eye (or window) to the target's body (a wall, a point just over
    /// the ground) and must clear the terrain (<see cref="LineOfSight"/>).
    /// </summary>
    internal bool LineOfFire(Unit u, Vec p, WeaponDef w, Unit? t)
    {
        if (Ground is not { } g || !DirectFire(w) || t?.Def.Layer == Layers.Air)
        {
            return true;
        }
        var from = ShotFrom(u, p);
        var eye = u.Garrison != 0 ? WindowRaw : u.Def.Crew is not null ? VehicleEyeRaw : EyeRaw;
        var aim = t is null ? GroundAimRaw : t.Def.IsStructure ? WallRaw : BodyRaw;
        return LineOfSight(from, Fixed.FromRaw(g.HeightAt(from).Raw + eye), p, Fixed.FromRaw(g.HeightAt(p).Raw + aim));
    }

    /// <summary>
    /// Scatter in percent of a weapon's for a shot from `from` at `at`: firing down from at least
    /// 4 m above the point, up to 25% less (a quarter off at 16 m and more); firing up from 4 m
    /// below, up to 25% more; 100 on a flat map.
    /// </summary>
    public int ScatterPercent(Vec from, Vec at)
    {
        if (Ground is not { } g)
        {
            return 100;
        }
        var dh = g.HeightAt(from).Raw - g.HeightAt(at).Raw;
        if (Math.Abs(dh) < 4 * RawPerMetre)
        {
            return 100;
        }
        var k = (int)Math.Min(25, Math.Abs(dh) * 25 / (16 * RawPerMetre));
        return dh > 0 ? 100 - k : 100 + k;
    }

    // GuidedAltitude is a guided missile's height (absolute, raw) over a point of its flight on
    // terrain: on the straight line from its launch height to its aim (a body over the target's
    // ground), a metre above it.
    private long GuidedAltitude(Projectile p, Vec at, Vec aim)
    {
        var g = Ground!;
        var total = (aim - p.Origin).Len().Raw;
        var aimH = g.HeightAt(aim).Raw + BodyRaw;
        var s = total == 0 ? 0 : Math.Clamp((at - p.Origin).Len().Raw, 0, total);
        return p.LaunchHeight.Raw + (total == 0 ? 0 : (aimH - p.LaunchHeight.Raw) * s / total) + RawPerMetre;
    }

    // Shielded reports whether a crest stands between a blast at `at` and a unit at p: the ground
    // at a third and at two thirds of the way rises above the line between them (each half a
    // metre above its ground).
    private bool Shielded(Vec at, Vec p)
    {
        if (Ground is not { } g)
        {
            return false;
        }
        long ha = g.HeightAt(at).Raw + BlastEyeRaw, hb = g.HeightAt(p).Raw + BlastEyeRaw;
        var d = p - at;
        for (var i = 1; i <= 2; i++)
        {
            var q = new Vec(Fixed.FromRaw(at.X.Raw + d.X.Raw * i / 3), Fixed.FromRaw(at.Y.Raw + d.Y.Raw * i / 3));
            if (g.HeightAt(q).Raw <= ha + (hb - ha) * i / 3)
            {
                return false;
            }
        }
        return true;
    }

    // SplashDistance is the distance from a blast at `at` to the point q of a unit, in 3D on terrain.
    private long SplashDistance(Vec at, Vec q)
    {
        var d2 = (q - at).LenSq();
        if (Ground is { } g)
        {
            var dh = g.HeightAt(q).Raw - g.HeightAt(at).Raw;
            d2 += dh * dh;
        }
        return Fixed.Sqrt(d2);
    }

    /// <summary>
    /// The rise of a ballistic arc over its chord (raw), for weapon w fired over a horizontal
    /// distance (raw): launcher rockets 7 to 17.5% of the range (more the farther), the high arc
    /// of long-range launchers 28 to 35%; shoulder-fired rounds fly nearly flat.
    /// </summary>
    public static long ArcRiseRaw(WeaponDef w, long distanceRaw)
    {
        if (w.Effect.ArcPercent > 0)
        {
            return distanceRaw * w.Effect.ArcPercent / 100; // its own arc (a ballistic missile's high apex)
        }
        if (w.Effect.SalvoSize <= 1)
        {
            return FlatArcRaw;
        }
        var cells = distanceRaw / Fixed.OneRaw;
        var permille = w.Effect.Range.Raw > HighArcFromCells * Fixed.OneRaw
            ? 280 + 70 * Math.Clamp(cells - 50, 0, 40) / 40
            : 70 + 105 * Math.Min(cells, 60) / 60;
        return distanceRaw * permille / 1000;
    }

    /// <summary>The launch height of weapon w's projectiles above the shooter's ground (raw).</summary>
    public static long LaunchHeightRaw(WeaponDef w) => w.Effect.Lobbed ? LaunchSalvoRaw : LaunchShoulderRaw;

    /// <summary>
    /// A ballistic projectile's height (absolute, raw) over a point of its flight: from its launch
    /// height down to 0.3 m over its aim point's ground, plus the arc's parabola (0 off terrain).
    /// </summary>
    public static long ArcHeightRaw(Projectile p, Vec at)
    {
        var total = (p.Dest - p.Origin).Len().Raw;
        if (total == 0)
        {
            return p.LandHeight.Raw;
        }
        var s = Math.Clamp((at - p.Origin).Len().Raw, 0, total);
        return p.LaunchHeight.Raw + (p.LandHeight.Raw - p.LaunchHeight.Raw) * s / total + 4 * p.ArcRise.Raw * s / total * (total - s) / total;
    }

    /// <summary>Whether air defence sees projectile p on its arc (a missile or rocket, not a drone or a guided missile): <see cref="ArcHeightRaw"/>.</summary>
    internal static bool OnArc(Projectile p) => p.Arcs && p.Weapon.Effect.Drone is null && p.Weapon.Effect.Guided is null && p.Weapon.Effect.Terminal is null; // a terminal profile flies its own closed form (TerminalSystem.cs)

    /// <summary>
    /// The step (raw x y z) an arcing projectile takes over its next tick (next: true) or took over its last: straight
    /// toward its aim point at its speed, its height along its arc.
    /// </summary>
    internal static (long X, long Y, long Z) ArcStep(Projectile p, bool next)
    {
        var d = p.Dest - p.Pos;
        var speed = p.Weapon.Effect.ProjectileSpeed;
        var len = d.Len();
        var h = len > speed ? d.WithLen(speed) : d;
        if (!next && len.Raw == 0)
        {
            return (0, 0, 0);
        }
        var a = next ? p.Pos : p.Pos - h;
        var b = next ? p.Pos + h : p.Pos;
        return (h.X.Raw, h.Y.Raw, ArcHeightRaw(p, b) - ArcHeightRaw(p, a));
    }

    // Launch sets a new projectile's arc on terrain: its origin, launch and landing heights, rise.
    private void Launch(Projectile p, Unit shooter)
    {
        if (p.Weapon.Effect.Drone is { } dr)
        {
            // A drone leaves its rail at the launch height and flies its own profile (StepDrone).
            p.Origin = shooter.Pos;
            p.Altitude = Fixed.FromRaw((Ground?.HeightAt(shooter.Pos).Raw ?? 0) + dr.LaunchHeight);
            return;
        }
        if (Ground is not { } g)
        {
            if (p.Interceptible != InterceptClass.None && p.Weapon.Effect.Guided is null)
            {
                // An interceptible missile or rocket (the Patriot-type launcher's prey) flies its arc's height on a flat map
                // too, so air defence can see and lead it (AirBody); nothing else reads it off terrain.
                p.Origin = shooter.Pos;
                p.LaunchHeight = Fixed.FromRaw(LaunchHeightRaw(p.Weapon));
                p.LandHeight = Fixed.FromRaw(LandRaw);
                p.ArcRise = Fixed.FromRaw(ArcRiseRaw(p.Weapon, (p.Dest - p.Origin).Len().Raw));
                p.Arcs = true;
            }
            return;
        }
        if (p.Weapon.Effect.Guided is not null)
        {
            // A guided missile flies low and straight: its origin and launch height, for the crests.
            p.Origin = shooter.Pos;
            p.LaunchHeight = Fixed.FromRaw(g.HeightAt(shooter.Pos).Raw + EyeRaw);
            p.LowFlight = true;
            return;
        }
        p.Origin = shooter.Pos;
        p.LaunchHeight = Fixed.FromRaw(g.HeightAt(shooter.Pos).Raw + LaunchHeightRaw(p.Weapon));
        p.LandHeight = Fixed.FromRaw(g.HeightAt(p.Dest).Raw + LandRaw);
        p.ArcRise = Fixed.FromRaw(ArcRiseRaw(p.Weapon, (p.Dest - p.Origin).Len().Raw));
        p.Arcs = true;
    }

    // GroundStrike finds where a ballistic projectile's flight from a to b first meets the
    // terrain (its arc at or below the ground), sampled TerrainSteps times a cell: the distance
    // along the segment (raw), or -1.
    private long GroundStrike(Projectile p, Vec a, Vec b)
    {
        if (!p.Arcs || Ground is not { } g)
        {
            return -1;
        }
        var seg = b - a;
        var len = seg.Len().Raw;
        var steps = (int)((len * TerrainSteps + Fixed.OneRaw - 1) / Fixed.OneRaw);
        for (var k = 1; k <= steps; k++)
        {
            var q = a + new Vec(Fixed.FromRaw(seg.X.Raw * k / steps), Fixed.FromRaw(seg.Y.Raw * k / steps));
            if (ArcHeightRaw(p, q) <= g.HeightAt(q).Raw)
            {
                return len * k / steps;
            }
        }
        return -1;
    }

    // CanDeployAt reports whether a launcher of u's kind can set up on cell c: a grade under its
    // movement class's DeployMaxGrade (Flat and Gentle by default; anywhere on a flat map).
    internal bool CanDeployAt(Unit u, Cell c)
    {
        if (Ground is not { } g)
        {
            return true;
        }
        var k = g.Rules.ClassOf(u.Def);
        return (k < 0 || g.Grade(c) < g.Rules.Classes[k].DeployMaxGrade) && DeploySlopeOk(u.Def, c); // and its own slope rule (the cruise truck)
    }

    // DeploySpot is the nearest cell, within a few cells, where u could set up and still reach
    // goal with weapon range r (any cell when r is zero), in the rings' fixed order; or null.
    private Cell? DeploySpot(Unit u, Vec goal, Fixed r)
    {
        var at = Cell.Of(u.Pos);
        var k = Ground?.Rules.ClassOf(u.Def) ?? -1;
        for (var d = 1; d <= DeploySearch; d++)
        {
            for (var y = at.Y - d; y <= at.Y + d; y++)
            {
                for (var x = at.X - d; x <= at.X + d; x++)
                {
                    if (Math.Max(Math.Abs(x - at.X), Math.Abs(y - at.Y)) != d)
                    {
                        continue;
                    }
                    var c = new Cell(x, y);
                    if (Grid.Passable(c) && (k < 0 || Ground!.Passable(k, c)) && CanDeployAt(u, c) &&
                        (r.Raw == 0 || Within(c.Center(), goal, r)))
                    {
                        return c;
                    }
                }
            }
        }
        return null;
    }
}
