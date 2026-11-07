using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// Submunitions (weapon submunitions; the cluster missile, docs/design.md, "Cluster missile truck"; written for a cluster bomb on jets
// too): a weapon that opens high over its point and throws out a few small missiles, each landing in its own spot of an ellipse, long along
// the flight line (the engine says where and when each lands; a renderer draws each one's own flight back from that). Air defence
// engages the missile until the burst (TerminalSystem.cs: a terminal profile with no dive, its way ending right over the point at
// the burst height); an interceptor still flying then has lost its target and misses, and the shells are never targets: they aren't
// projectiles, only pending impacts. A missile shot down or into a hill never gets here and drops nothing.
//
// The pattern is composed, not noise, so that no two bursts look alike and no safe spot can be read from one to the next:
//  - its outline: turned off the flight line and its length and width each varied, per burst;
//  - pockets: most of the shells fall in a few small pockets, each the best of a few hashed candidates (farthest from the
//    pockets already placed, leaning toward the centre), inside the outline with all of their shells;
//  - strays: the rest fall alone across the whole outline, leaning toward the centre;
//  - timing: the pockets go off in a rough walk along the flight line, each jittered and with a crackle of its own; a stray any time.
// Every roll is Rand.Hash(seed, tick, missile, index): no generator state, so a burst never shifts the world's other draws. The
// whole pattern is worked out at the burst, integers only. The warning (IncomingStrikes) gives the weapon's area, never this.
//
// Hashed state: the impacts still to come. Not state: this tick's burst events (SubmunitionBursts).
public sealed partial class World
{
    private const int DenseForest = 3; // TerrainMap.Forest's densest
    private const long AxisOne = 65536; // a unit axis's length in the pattern's arithmetic

    private readonly List<PendingBurst> _pendingBursts = [];
    private readonly List<SubmunitionBurst> _bursts = [];

    /// <summary>The bursts this tick, in projectile ID order (filter by <see cref="SubmunitionBurst.SeenBy"/>; not state).</summary>
    public IReadOnlyList<SubmunitionBurst> SubmunitionBursts => _bursts;

    // PendingBurst: the shells of one burst still to land, sorted by impact tick (Next: the first still to land).
    private sealed class PendingBurst(uint missile, uint shooter, Submunitions weapon, BombletImpact[] spots)
    {
        public readonly uint Missile = missile, Shooter = shooter;
        public readonly Submunitions Weapon = weapon;
        public readonly BombletImpact[] Spots = spots;
        public int Next;
    }

    // Burst opens missile p over its point: its pattern, its pending impacts, the event; the missile ends here.
    private void Burst(Projectile p, Submunitions sm)
    {
        p.Pos = p.Dest;
        p.Done = true;
        var pending = ReleaseBomblets(p.Id, p.Owner, p.Shooter, p.Dest, p.Altitude, FlightLine(p), sm);
        NoteTerminalEnd(p, false);
        _pendingBursts.Add(pending);
    }

    // ReleaseBomblets composes a burst and reports it; the caller keeps the impacts pending. For a cluster bomb the same, with its own line.
    private PendingBurst ReleaseBomblets(uint missile, byte owner, uint shooter, Vec at, Fixed altitude, Vec heading, Submunitions sm)
    {
        var pattern = ComposeBurst(sm, Match.Setup.Seed, Tick, missile, at, heading, Grid.Width, Grid.Height);
        var spots = pattern.Bomblets;
        _bursts.Add(new SubmunitionBurst(missile, owner, shooter, at, altitude, heading, Tick, sm.FallTicks, BurstSeenBy(owner, at, spots), spots));
        return new PendingBurst(missile, shooter, sm, spots);
    }

    // ReleaseBombletsForTest: a burst at a point with no missile (the seeded balance table and tools): the pattern and the damage, as they are.
    internal void ReleaseBombletsForTest(uint missile, byte owner, Vec at, Vec heading, Submunitions sm) =>
        _pendingBursts.Add(ReleaseBomblets(missile, owner, 0, at, Fixed.FromRaw(GroundRaw(at) + sm.BurstHeight.Raw), heading, sm));

    // RunBomblets lands the shells due this tick, burst by burst, each shell's blast and direct hit from above: a tank's top armour
    // (directional armour, ArmorSystem.cs), as any lobbed round.
    private void RunBomblets() => LandBomblets(Tick);

    // LandBombletsForTest lands every pending shell now (the seeded balance table measures many bursts without stepping a world).
    internal void LandBombletsForTest() => LandBomblets(uint.MaxValue);

    private void LandBomblets(uint upTo)
    {
        if (_pendingBursts.Count == 0)
        {
            return;
        }
        foreach (var b in _pendingBursts)
        {
            var sm = b.Weapon;
            _hurtBy = Unit(b.Shooter); // for Return fire and retaliation (Hurt)
            SetHit(true, null);
            while (b.Next < b.Spots.Length && b.Spots[b.Next].ImpactTick <= upTo)
            {
                Explode(b.Spots[b.Next].At, sm.Damage, sm.Warhead, sm.Splash, 0, sm.SplashCenter, SplashEdgePercent, sm.SplashInner,
                    sm.SplashOuterPercent, false, default, sm.CanopyPercent);
                b.Next++;
            }
            _hurtBy = null;
            ClearHit();
        }
        _pendingBursts.RemoveAll(b => b.Next >= b.Spots.Length);
    }

    private void HashBomblets(ref WordHash h)
    {
        if (_pendingBursts.Count == 0)
        {
            return;
        }
        h.Put(0xB0B1E7);
        h.PutSigned(_pendingBursts.Count);
        foreach (var b in _pendingBursts)
        {
            h.Put(b.Missile);
            h.Put(b.Shooter);
            h.PutSigned(b.Spots.Length - b.Next);
            for (var i = b.Next; i < b.Spots.Length; i++)
            {
                h.PutSigned(b.Spots[i].At.X.Raw);
                h.PutSigned(b.Spots[i].At.Y.Raw);
                h.Put(b.Spots[i].ImpactTick);
            }
        }
    }

    // BurstSeenBy: the shooter, and any player who sees its point or any shell's spot right now (fog of war; SeesPoint is always true without it).
    private uint BurstSeenBy(byte owner, Vec at, BombletImpact[] spots)
    {
        uint bits = 0;
        foreach (var pl in _players)
        {
            var seen = pl.Id == owner || SeesPoint(pl.Id, at);
            for (var i = 0; !seen && i < spots.Length; i++)
            {
                seen = SeesPoint(pl.Id, spots[i].At);
            }
            if (seen)
            {
                bits |= Bit(pl.Id);
            }
        }
        return bits;
    }

    // FlightLine: the way a missile flew, a vector one cell long (north for one with no way at all).
    private static Vec FlightLine(Projectile p)
    {
        var d = p.Dest - p.Origin;
        var total = d.Len().Raw;
        return total == 0 ? Facing.Dir(Facing.North) : new Vec(Fixed.FromRaw(d.X.Raw * Fixed.OneRaw / total), Fixed.FromRaw(d.Y.Raw * Fixed.OneRaw / total));
    }

    // What each roll is for (the top byte of Rand.Hash's index).
    private const ulong RollOutline = 1, RollPocket = 2, RollCandidate = 3, RollPocketShell = 4, RollStray = 5, RollStrayTick = 6;

    /// <summary>
    /// One burst's composition (see the file's header): pure, from the weapon, the match seed, the
    /// burst's tick and missile, its point and its flight line. <paramref name="width"/> and <paramref name="height"/> (cells) keep a
    /// burst by the map's edge on the map.
    /// </summary>
    internal static BurstPattern ComposeBurst(Submunitions sm, ulong seed, uint tick, uint missile, Vec at, Vec heading, int width, int height)
    {
        ulong Hash(ulong kind, int index) => Rand.Hash(seed, tick, missile, kind << 56 | (uint)index);
        int Roll(ulong kind, int index, int lo, int hi) => lo + (int)(Hash(kind, index) % (ulong)(hi - lo + 1));
        (long X, long Y) InDisc(ulong kind, int index)
        {
            // A point of the unit disc in thousandths (rejection, up to 8 tries; at 1 in 5 x 10^5 the last try is pulled in).
            long x = 0, y = 0;
            for (var t = 0; t < 8; t++)
            {
                var z = Hash(kind, index << 4 | t);
                x = (long)(z & 0xFFFFF) * 2001 / 0x100000 - 1000;
                y = (long)(z >> 20 & 0xFFFFF) * 2001 / 0x100000 - 1000;
                if (x * x + y * y <= 1_000_000)
                {
                    return (x, y);
                }
            }
            return (x * 7 / 10, y * 7 / 10);
        }

        // The outline.
        var a = sm.Length.Raw / 2 * (100 + Roll(RollOutline, 0, -sm.SizePercent, sm.SizePercent)) / 100;
        var b = sm.Width.Raw / 2 * (100 + Roll(RollOutline, 1, -sm.SizePercent, sm.SizePercent)) / 100;
        var (ax, ay) = Turned(heading, Roll(RollOutline, 2, -sm.TurnDegrees * 10, sm.TurnDegrees * 10));

        // The pockets: inside the outline with all their shells; each the best of its candidates.
        var n = Roll(RollOutline, 3, sm.PocketsMin, sm.PocketsMax);
        var pockets = new BurstPocket[n];
        var inside = 1000 - sm.PocketSizeMax.Raw * 1000 / 2 / Math.Min(a, b);
        var shells = sm.Count * sm.PocketPercent / 100;
        var weight = 0L;
        var gap = sm.PocketSizeMax.Raw * sm.PocketSizeMax.Raw; // pockets that don't touch, at their largest
        for (var k = 0; k < n; k++)
        {
            var best = long.MinValue;
            var (bu, bv) = (0L, 0L);
            for (var c = 0; c < sm.Candidates; c++)
            {
                var (x, y) = InDisc(RollCandidate, k << 8 | c);
                long u = x * inside / 1000 * a / 1000, v = y * inside / 1000 * b / 1000;
                var near = k == 0 ? 0 : long.MaxValue;
                for (var o = 0; o < k; o++)
                {
                    var (du, dv) = (u - pockets[o].U, v - pockets[o].V);
                    near = Math.Min(near, du * du + dv * dv);
                }
                var score = (near == 0 ? 0 : Fixed.Sqrt(near)) - Fixed.Sqrt(u * u + v * v) * sm.CentreLeanPercent / 100;
                if (k > 0 && near < gap)
                {
                    score -= a * 4; // overlapping another pocket: only if every candidate does
                }
                if (score > best)
                {
                    (best, bu, bv) = (score, u, v);
                }
            }
            var size = sm.PocketSizeMin.Raw == sm.PocketSizeMax.Raw ? sm.PocketSizeMin.Raw
                : sm.PocketSizeMin.Raw + (long)(Hash(RollPocket, k << 4) % (ulong)(sm.PocketSizeMax.Raw - sm.PocketSizeMin.Raw + 1));
            var w = Roll(RollPocket, k << 4 | 1, 70, 130); // the pockets aren't all as big
            weight += w;
            // Its share of the walk along the flight line (the shells that lead the way land first), its jitter and its crackle.
            var crackle = Roll(RollPocket, k << 4 | 2, sm.CrackleMinTicks, sm.CrackleMaxTicks);
            var span = Math.Max(0, sm.ReleaseTicks - crackle);
            var walk = (int)((bu + a) * span / Math.Max(1, 2 * a)) + Roll(RollPocket, k << 4 | 3, -sm.JitterTicks, sm.JitterTicks);
            pockets[k] = new BurstPocket(bu, bv, size / 2, w, Math.Clamp(walk, 0, span), crackle);
        }

        // The shells: each pocket's, then the strays; ticks counted so the list comes out sorted (stable: same tick, listed order).
        var offsets = new (long U, long V, int T)[sm.Count];
        var filled = 0;
        var given = 0L;
        for (var k = 0; k < n; k++)
        {
            var q = pockets[k];
            given += q.Weight;
            var upto = (int)(shells * given / Math.Max(1, weight));
            pockets[k] = q with { Shells = upto - filled };
            for (; filled < upto; filled++)
            {
                var (x, y) = InDisc(RollPocketShell, filled);
                offsets[filled] = (q.U + x * q.Radius / 1000, q.V + y * q.Radius / 1000, q.Start + Roll(RollPocketShell, filled << 4 | 15, 0, q.Crackle));
            }
        }
        for (; filled < sm.Count; filled++)
        {
            var (x, y) = InDisc(RollStray, filled);
            var r = Fixed.Sqrt(x * x + y * y);
            var f = (100 - sm.StrayLeanPercent) * 10 + sm.StrayLeanPercent * r / 100; // thousandths; 1000 at the rim
            offsets[filled] = (x * f / 1000 * a / 1000, y * f / 1000 * b / 1000, Roll(RollStrayTick, filled, 0, sm.ReleaseTicks));
        }
        var counts = new int[sm.ReleaseTicks + 2];
        foreach (var o in offsets)
        {
            counts[o.T + 1]++;
        }
        for (var t = 1; t < counts.Length; t++)
        {
            counts[t] += counts[t - 1];
        }
        var spots = new BombletImpact[sm.Count];
        var (maxX, maxY) = ((long)width * Fixed.OneRaw - 1, (long)height * Fixed.OneRaw - 1);
        foreach (var o in offsets)
        {
            var x = Math.Clamp(at.X.Raw + (o.U * ax - o.V * ay) / AxisOne, 0, maxX);
            var y = Math.Clamp(at.Y.Raw + (o.U * ay + o.V * ax) / AxisOne, 0, maxY);
            spots[counts[o.T]++] = new BombletImpact(new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y)), tick + (uint)(sm.FallTicks + o.T));
        }
        return new BurstPattern(spots, a, b, ax, ay, pockets);
    }

    // Turned is a one-cell heading as a unit axis (AxisOne long), turned by tenths of a degree (Taylor, integers: within 4 parts in
    // 10^4 at the 45 degrees content allows, within 1 in 10^6 at the cluster missile's 15).
    private static (long X, long Y) Turned(Vec heading, int tenths)
    {
        var len = Math.Max(1, heading.Len().Raw);
        long hx = heading.X.Raw * AxisOne / len, hy = heading.Y.Raw * AxisOne / len;
        var t = tenths * 11439L / 100; // radians, AxisOne to the radian (2 pi x 65536 / 3600 a tenth = 114.39)
        var t2 = t * t / AxisOne;
        var sin = t - t * t2 / (6 * AxisOne) + t * t2 / AxisOne * t2 / (120 * AxisOne);
        var cos = AxisOne - t2 / 2 + t2 * t2 / (24 * AxisOne);
        return ((hx * cos - hy * sin) / AxisOne, (hx * sin + hy * cos) / AxisOne);
    }
}

/// <summary>A shell's landing (<see cref="SubmunitionBurst.Bomblets"/>).</summary>
/// <param name="At">Its spot on the ground.</param>
/// <param name="ImpactTick">The tick it lands on (<see cref="World.Tick"/> during that step).</param>
public readonly record struct BombletImpact(Vec At, uint ImpactTick);

/// <summary>A burst this tick (<see cref="World.SubmunitionBursts"/>).</summary>
/// <param name="Missile">The missile's projectile ID (its <see cref="IncomingEventKind.Struck"/> event is this tick's too).</param>
/// <param name="Owner">The shooter's player.</param>
/// <param name="Shooter">The unit that fired it.</param>
/// <param name="At">The point on the ground it opened over.</param>
/// <param name="Altitude">Its height there (absolute, raw: 512 a metre).</param>
/// <param name="Heading">Its flight line (a vector one cell long).</param>
/// <param name="Tick">The tick it opened (<see cref="World.Tick"/> during that step).</param>
/// <param name="FallTicks">Ticks a shell takes to fall: a shell leaves at its ImpactTick less this.</param>
/// <param name="SeenBy">A bit (1 &lt;&lt; player) for each player who may be told: the shooter's, and any who sees its point or any spot now. Without the bit, it didn't happen for him.</param>
/// <param name="Bomblets">Every shell's spot and impact tick, sorted by tick. Per spot, <see cref="World.SeesPoint"/> tells what a player can see land.</param>
public readonly record struct SubmunitionBurst(uint Missile, byte Owner, uint Shooter, Vec At, Fixed Altitude, Vec Heading, uint Tick, int FallTicks, uint SeenBy,
    IReadOnlyList<BombletImpact> Bomblets)
{
    /// <summary>Whether <paramref name="player"/> may be told of this burst (see <see cref="SeenBy"/>).</summary>
    public bool SeenByPlayer(byte player) => (SeenBy & World.Bit(player)) != 0;
}

/// <summary>One pocket of a burst's composition (tests and tools).</summary>
/// <param name="U">Its centre along the outline's long axis, from the point (raw).</param>
/// <param name="V">Its centre across (raw).</param>
/// <param name="Radius">Its radius (raw).</param>
/// <param name="Weight">Its share of the pocket shells.</param>
/// <param name="Start">The tick (from the first shell's earliest landing) it starts to land.</param>
/// <param name="Crackle">Ticks it takes to land.</param>
/// <param name="Shells">Its shells.</param>
internal readonly record struct BurstPocket(long U, long V, long Radius, int Weight, int Start, int Crackle, int Shells = 0);

/// <summary>One burst's composition (tests and tools; the game reads <see cref="SubmunitionBurst"/>).</summary>
/// <param name="Bomblets">Every shell, sorted by tick.</param>
/// <param name="SemiLength">The outline's half length (raw).</param>
/// <param name="SemiWidth">The outline's half width (raw).</param>
/// <param name="AxisX">The outline's long axis, x (65536 long).</param>
/// <param name="AxisY">Its y.</param>
/// <param name="Pockets">The pockets.</param>
internal readonly record struct BurstPattern(BombletImpact[] Bomblets, long SemiLength, long SemiWidth, long AxisX, long AxisY, BurstPocket[] Pockets);
