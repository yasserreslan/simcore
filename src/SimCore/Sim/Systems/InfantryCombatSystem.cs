using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// Infantry combat, the ground and the senses (INF-E1b-1; docs/design.md, "Infantry combat: the ground and the senses"; the game
// repo's docs/plans/infantry-battle-rules.md 4, 5, 9, 21 to 23 and infantry-combat-brief.md 6.2 to 6.7). With the content's
// infantryCombat block only; without it nothing here runs and nothing is hashed.
//
// RunMinds (after the orders and demolitions, before the squads): the cover data's build and rebuilds, danger zones expiring,
// posture changes ticking, squad thinks (hearing, the picture, sharing, the squad axis) in squad ID order on their phase, then
// soldier thinks (seen contacts, the firing reveal, hit-from, reported contacts, memory, danger, the axis, in contact, reaction,
// the posture table) in unit ID order on their phase, urgent ones next tick up to a cap. RunSuppressionDecay after the tick's
// damage. Hooks: the hit roll (posture, cover, suppression), the combat pass (no firing while changing posture, pinned hard
// holds fire beyond 3 cells), movement (posture speeds), shots and blasts (suppression, zones), rescue (suppression, cover).
public sealed partial class World
{
    private const ulong ReactSalt = 0x1E61_0000_0000_0001UL; // 4.6 reaction draws
    private const ulong AreaSalt = 0x1E61_0000_0000_0002UL; // 4.1 a heard area's offset

    /// <summary>The 32 directions (21): unit vectors x 1024, 0 east, then clockwise on the map (y grows south) in 11.25° steps.</summary>
    public static readonly (int X, int Y)[] Directions =
    [
        (1024, 0), (1004, 200), (946, 392), (851, 569), (724, 724), (569, 851), (392, 946), (200, 1004), (0, 1024), (-200, 1004),
        (-392, 946), (-569, 851), (-724, 724), (-851, 569), (-946, 392), (-1004, 200), (-1024, 0), (-1004, -200), (-946, -392),
        (-851, -569), (-724, -724), (-569, -851), (-392, -946), (-200, -1004), (0, -1024), (200, -1004), (392, -946), (569, -851),
        (724, -724), (851, -569), (946, -392), (1004, -200),
    ];

    private static readonly (int X, int Y)[] SideSteps = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];

    private CoverMap? _cover;
    private bool _fights; // a soldier with a fight state exists: the minds run and are hashed
    private readonly List<DangerZone> _zones = [];
    private readonly List<Unit> _nearMind = [];
    private readonly List<Unit> _nearShot = [];
    private readonly List<(long D2, uint Id)> _heardScratch = [];
    private int _urgentThisTick;
    private int _maxSound; // the loudest listed weapon's sound range (tenths), from the rules
    private long _maxSight; // the widest vision (raw) of any soldier ever added (NewFight): bounds queries for who sees a point

    /// <summary>Test and bench switch: true runs the match as without the infantryCombat block (set before units are added).</summary>
    internal bool CombatOff { get; set; }

    /// <summary>Counters for the firefight bench (output, not state).</summary>
    internal (long Thinks, long Urgent, long UrgentCapped, long SquadThinks, long Heard, long CoverRebuilt) CombatStats;

    private InfantryCombatRules? Combat => CombatOff ? null : Content.InfantryCombat;

    /// <summary>The danger zones on the ground now (5.4).</summary>
    public IReadOnlyList<DangerZone> DangerZones => _zones;

    // NewFight gives a soldier (a unit with a Life) his fight state when the infantryCombat block applies (World.Add).
    private void NewFight(Unit u)
    {
        if (Combat is not null && u.Life is not null)
        {
            u.Fight = new SoldierFight { Grenades = GrenadesFor(u, Combat), Lmg = u.Def.Weapons.Count > 0 && Combat.Weapon(u.Def.Weapons[0].Effect) is { Class: CombatClass.Lmg } };
            _fights = true;
            _maxSight = Math.Max(_maxSight, u.Def.Vision.Raw);
        }
    }

    // ---- Directions ----

    /// <summary>The table direction (0 to 31) nearest to v: the largest dot product, ties the lower index; -1 for a zero vector.</summary>
    public static int Dir32(long vx, long vy)
    {
        if (vx == 0 && vy == 0)
        {
            return -1;
        }
        // Scale big vectors down so the dot product stays in 64 bits.
        while (Math.Abs(vx) > (1L << 40) || Math.Abs(vy) > (1L << 40))
        {
            vx >>= 8;
            vy >>= 8;
        }
        var best = 0;
        var bestDot = long.MinValue;
        for (var i = 0; i < 32; i++)
        {
            var dot = vx * Directions[i].X + vy * Directions[i].Y;
            if (dot > bestDot)
            {
                (best, bestDot) = (i, dot);
            }
        }
        return best;
    }

    /// <summary>The steps (0 to 16) between two of the 32 directions.</summary>
    public static int DirSteps(int a, int b)
    {
        var d = Math.Abs(a - b) % 32;
        return d > 16 ? 32 - d : d;
    }

    // ---- Cover data (5.1) ----

    // EnsureCover builds the cover data on first use (and turns the change logs on).
    private CoverMap EnsureCover(InfantryCombatRules r)
    {
        if (_cover is { } m)
        {
            return m;
        }
        m = BuildCover(r);
        _cover = m;
        return m;
    }

    // BuildCover builds the whole map's cover data: the standing structures stamped, every cell's object kind, then each cell's
    // sides from its 8 neighbours (an object fills the side toward it and the two next to that), crests and concealment.
    internal CoverMap BuildCover(InfantryCombatRules r)
    {
        var m = new CoverMap(Grid.Width, Grid.Height);
        Grid.CoverLog = [];
        if (Ground is { } g0)
        {
            g0.CoverLog = [];
        }
        m.Stamped = StandingCover(r);
        foreach (var s in m.Stamped)
        {
            StampCover(m, s, true);
        }
        var w = m.W;
        var kinds = new byte[w * m.H];
        for (var i = 0; i < kinds.Length; i++)
        {
            kinds[i] = (byte)ObjectKind(m, i % w, i / w);
        }
        for (var y = 0; y < m.H; y++)
        {
            for (var x = 0; x < w; x++)
            {
                m.Data[y * w + x] = CellCover(m, r, x, y, kinds);
            }
        }
        return m;
    }

    // StandingCover lists the standing structures that are cover, by ID: footprint box and whether it is high.
    private List<(uint Id, int X0, int Y0, int X1, int Y1, bool Tall)> StandingCover(InfantryCombatRules r)
    {
        var list = new List<(uint, int, int, int, int, bool)>();
        foreach (var u in _units)
        {
            var a = u.Def.Archetype;
            if (!u.Def.IsStructure || !u.Alive || !u.IsBuilt || u.Rubble || a.Hidden || a.Pavement || a.Bridge is not null)
            {
                continue;
            }
            var o = FootprintOrigin(u);
            var tall = a.SightBlockRaw / HalfMetreRaw >= r[CombatValue.TallHalfMetres];
            list.Add((u.Id, o.X, o.Y, o.X + FootW(u) - 1, o.Y + FootH(u) - 1, tall));
        }
        return list;
    }

    private static void StampCover(CoverMap m, (uint Id, int X0, int Y0, int X1, int Y1, bool Tall) s, bool on)
    {
        for (var y = Math.Max(0, s.Y0); y <= Math.Min(m.H - 1, s.Y1); y++)
        {
            for (var x = Math.Max(0, s.X0); x <= Math.Min(m.W - 1, s.X1); x++)
            {
                var i = y * m.W + x;
                CoverMap.SetBit(m.Solid, i, on);
                CoverMap.SetBit(m.Tall, i, on && s.Tall);
            }
        }
    }

    // ObjectKind is the cover the object in cell (x, y) gives the cells round it: a standing structure (high if tall, else low),
    // rock or rubble (blocked ground: low), a tree trunk (soft), a fallen trunk (low), or none.
    private CoverKind ObjectKind(CoverMap m, int x, int y)
    {
        if (x < 0 || y < 0 || x >= m.W || y >= m.H)
        {
            return CoverKind.None;
        }
        var i = y * m.W + x;
        if (CoverMap.Bit(m.Solid, i))
        {
            return CoverMap.Bit(m.Tall, i) ? CoverKind.High : CoverKind.Low;
        }
        var c = new Cell(x, y);
        if (Grid.At(c) == Terrain.Blocked)
        {
            return CoverKind.Low;
        }
        if (Ground is { } g)
        {
            if (g.HasTrunk(c))
            {
                return CoverKind.Soft;
            }
            if (g.Fallen(c))
            {
                return CoverKind.Low;
            }
        }
        return CoverKind.None;
    }

    private static int CoverRank(CoverKind k) => k switch { CoverKind.High => 3, CoverKind.Low => 2, CoverKind.Soft => 1, _ => 0 };

    // CellCover computes one cell's data from its neighbours' object kinds (kinds: a precomputed array, or null to compute them).
    private uint CellCover(CoverMap m, InfantryCombatRules r, int x, int y, byte[]? kinds)
    {
        Span<CoverKind> side = stackalloc CoverKind[8];
        for (var s = 0; s < 8; s++)
        {
            var (dx, dy) = SideSteps[s];
            int nx = x + dx, ny = y + dy;
            var k = kinds is not null ? (nx < 0 || ny < 0 || nx >= m.W || ny >= m.H ? CoverKind.None : (CoverKind)kinds[ny * m.W + nx])
                : ObjectKind(m, nx, ny);
            if (k == CoverKind.None)
            {
                continue;
            }
            for (var j = -1; j <= 1; j++)
            {
                var t = (s + j + 8) % 8;
                if (CoverRank(k) > CoverRank(side[t]))
                {
                    side[t] = k;
                }
            }
        }
        uint data = 0;
        for (var s = 0; s < 8; s++)
        {
            data |= (uint)side[s] << (2 * s);
        }
        if (Ground is { } g)
        {
            var c = new Cell(x, y);
            var reach = r[CombatValue.CrestReach];
            for (var s = 0; s < 8; s++)
            {
                var (dx, dy) = SideSteps[s];
                var t = new Cell(x + dx * reach, y + dy * reach);
                if (Grid.Contains(t) && CrestHides(c, t.Center()))
                {
                    data |= 1u << (CoverMap.CrestShift + s);
                }
            }
            data |= (uint)Math.Min(3, g.Forest(c)) << CoverMap.DensityShift;
        }
        return data;
    }

    // UpdateCover: every structureTicks the standing structures are compared with those stamped (a building gone, fallen or new
    // re-stamps its footprint), the grid's and the terrain's change logs are read; each changed cell and rebuildMargin round it
    // is queued; at most rebuildCells queued cells are rebuilt a tick, in the order queued (cell order within one change).
    private void UpdateCover(CoverMap m, InfantryCombatRules r)
    {
        var margin = r[CombatValue.RebuildMargin];
        if (Tick % (uint)Math.Max(1, r[CombatValue.StructureTicks]) == 0)
        {
            var now = StandingCover(r);
            var old = m.Stamped;
            int i = 0, j = 0;
            while (i < old.Count || j < now.Count)
            {
                if (j >= now.Count || (i < old.Count && old[i].Id < now[j].Id))
                {
                    Restamp(m, old[i], false, margin);
                    i++;
                }
                else if (i >= old.Count || now[j].Id < old[i].Id)
                {
                    Restamp(m, now[j], true, margin);
                    j++;
                }
                else
                {
                    if (old[i] != now[j])
                    {
                        Restamp(m, old[i], false, margin);
                        Restamp(m, now[j], true, margin);
                    }
                    i++;
                    j++;
                }
            }
            m.Stamped = now;
        }
        DrainCoverLog(m, Grid.CoverLog, margin);
        DrainCoverLog(m, Ground?.CoverLog, margin);
        var budget = r[CombatValue.RebuildCells];
        while (budget > 0 && m.QueueAt < m.Queue.Count)
        {
            var c = m.Queue[m.QueueAt++];
            CoverMap.SetBit(m.Queued, c, false);
            m.Data[c] = CellCover(m, r, c % m.W, c / m.W, null);
            budget--;
            CombatStats.CoverRebuilt++;
        }
        if (m.QueueAt >= m.Queue.Count)
        {
            m.Queue.Clear();
            m.QueueAt = 0;
        }
    }

    private void Restamp(CoverMap m, (uint Id, int X0, int Y0, int X1, int Y1, bool Tall) s, bool on, int margin)
    {
        StampCover(m, s, on);
        QueueBox(m, s.X0 - margin, s.Y0 - margin, s.X1 + margin, s.Y1 + margin);
    }

    private static void DrainCoverLog(CoverMap m, List<int>? log, int margin)
    {
        if (log is null || log.Count == 0)
        {
            return;
        }
        foreach (var i in log)
        {
            int x = i % m.W, y = i / m.W;
            QueueBox(m, x - margin, y - margin, x + margin, y + margin);
        }
        log.Clear();
    }

    private static void QueueBox(CoverMap m, int x0, int y0, int x1, int y1)
    {
        for (var y = Math.Max(0, y0); y <= Math.Min(m.H - 1, y1); y++)
        {
            for (var x = Math.Max(0, x0); x <= Math.Min(m.W - 1, x1); x++)
            {
                var i = y * m.W + x;
                if (!CoverMap.Bit(m.Queued, i))
                {
                    CoverMap.SetBit(m.Queued, i, true);
                    m.Queue.Add(i);
                }
            }
        }
    }

    /// <summary>The cover kind on side s (0 east, clockwise) of cell c, a crest counting as low; none off the map or without the rules.</summary>
    public CoverKind CoverSide(Cell c, int s)
    {
        if (Combat is not { } r || !Grid.Contains(c))
        {
            return CoverKind.None;
        }
        var m = EnsureCover(r);
        var i = c.Y * m.W + c.X;
        var k = m.Side(i, s);
        return k == CoverKind.None && m.Crest(i, s) ? CoverKind.Low : k;
    }

    /// <summary>The cover data's size in bytes (0 before it is built).</summary>
    public long CoverBytes => _cover?.Bytes ?? 0;

    // CoverToward is the cover on c's side facing direction d (one of 32): the nearest of the 8 sides; exactly between two: the
    // weaker (by firing protection).
    internal CoverKind CoverToward(Cell c, int d, InfantryCombatRules r)
    {
        if (d < 0)
        {
            return CoverKind.None;
        }
        var s = d / 4;
        switch (d % 4)
        {
            case 0:
                return CoverSide(c, s);
            case 1:
                return CoverSide(c, s);
            case 3:
                return CoverSide(c, (s + 1) % 8);
            default:
                var a = CoverSide(c, s);
                var b = CoverSide(c, (s + 1) % 8);
                return CoverValue(a, false, r) <= CoverValue(b, false, r) ? a : b;
        }
    }

    private static int CoverValue(CoverKind k, bool ducked, InfantryCombatRules r) => k switch
    {
        CoverKind.Low => r[ducked ? CombatValue.LowDucked : CombatValue.LowFiring],
        CoverKind.High => r[ducked ? CombatValue.HighDucked : CombatValue.HighFiring],
        CoverKind.Soft => r[ducked ? CombatValue.SoftDucked : CombatValue.SoftFiring],
        _ => 0,
    };

    /// <summary>
    /// Protection (0 to 100) of a soldier in posture p at cell c against a threat from direction d (5.2): the cover on that side
    /// at its firing or ducked value; none on that side: 0 (flanked); low cover keeps only lowStanding of it for a standing man.
    /// </summary>
    public int Protection(Cell c, Posture p, int d, bool ducked)
    {
        if (Combat is not { } r)
        {
            return 0;
        }
        var k = CoverToward(c, d, r);
        var v = CoverValue(k, ducked, r);
        return k == CoverKind.Low && p == Posture.Stand ? v * r[CombatValue.LowStanding] / 100 : v;
    }

    // ---- Danger zones (5.4) ----

    /// <summary>The danger zones' value at cell c (the strongest zone over it, burning ground included), 0 to 100.</summary>
    public int ZoneValue(Cell c)
    {
        if (Combat is not { } r)
        {
            return 0;
        }
        var v = Ground is { } g && g.Burning(c) ? r[CombatValue.ZoneFire] : 0;
        foreach (var z in _zones)
        {
            long dx = (long)(c.X - z.Centre.X) * Fixed.OneRaw, dy = (long)(c.Y - z.Centre.Y) * Fixed.OneRaw;
            var d2 = dx * dx + dy * dy;
            if (d2 <= (long)z.InnerRaw * z.InnerRaw)
            {
                v = Math.Max(v, z.InnerValue);
            }
            else if (d2 <= (long)z.OuterRaw * z.OuterRaw)
            {
                v = Math.Max(v, z.OuterValue);
            }
        }
        return v;
    }

    // NoteBlast (Explode): an explosion's suppression (+blast at its centre, falling to 0 at blastReachPercent of its splash) on
    // every soldier there, and a danger zone for an impact of minSplash or more.
    internal void NoteBlast(Vec at, Fixed splash)
    {
        if (Combat is not { } r || !_fights || splash.Raw <= 0)
        {
            return;
        }
        var reach = splash.Raw * r[CombatValue.BlastReachPercent] / 100;
        foreach (var s in Around(at.X.Raw - reach, at.Y.Raw - reach, at.X.Raw + reach, at.Y.Raw + reach, _nearShot, true))
        {
            if (s.Fight is null || !s.Alive || s.Inside != 0 || s.Garrison != 0)
            {
                continue;
            }
            var d = (s.Pos - at).Len().Raw;
            if (d < reach)
            {
                Suppress(s, (int)(r[CombatValue.SuppressBlast] * (reach - d) / reach), at, r);
            }
        }
        if (splash.Raw * 10 >= r[CombatValue.ZoneMinSplash] * Fixed.OneRaw)
        {
            if (_zones.Count >= r[CombatValue.ZoneMax])
            {
                _zones.RemoveAt(0);
            }
            _zones.Add(new DangerZone(Cell.Of(at), (int)(r[CombatValue.ZoneImpactReach] * Fixed.OneRaw / 10), r[CombatValue.ZoneImpact],
                (int)(r[CombatValue.ZoneImpactOuterReach] * Fixed.OneRaw / 10), r[CombatValue.ZoneImpactOuter], Tick + (uint)r[CombatValue.ZoneImpactTicks]));
            NoteImpact(at, r); // E1b-4 part 3 (14.4): the squads near it react
        }
    }

    // ---- Suppression (brief 6.6) ----

    // NoteSuppressingShot (the combat pass): u's instant shot of w at t, hit or not, striking at. The target and every other enemy
    // soldier of u within nearShot of where it struck gain the weapon's suppress (a hit on the target: +hit more); each notes the
    // shot near him (effective fire, firing at us); u notes his target.
    internal void NoteSuppressingShot(Unit u, WeaponDef w, Unit t, bool hit, Vec at)
    {
        if (Combat is not { } r || !_fights)
        {
            return;
        }
        NoteBurst(u, w, r); // INF-E1b-2: bursts and waits (15.5)
        if (hit && t.Fight is { } hf)
        {
            hf.Urgent = true; // a hit asks for an urgent think (21)
        }
        if (u.Fight is { } uf)
        {
            (uf.LastTarget, uf.LastTargetTick) = (t.Id, Tick);
        }
        OpenFire(u); // INF-E1b-3 (15.7): the squad has opened fire, or has been fired on
        OpenFire(t);
        HelperFired(u, r); // E1b-4 part 2 (13.4): "help arrived"
        var sup = r.Weapon(w.Effect)?.Suppress ?? 0;
        var range = Math.Max(1L, w.Range.Raw);
        if (t.Fight is not null && t.Owner != u.Owner)
        {
            Suppress(t, sup + (hit ? r[CombatValue.SuppressHit] : 0), u.Pos, r);
            ShotNear(t, u, range);
        }
        var near = r[CombatValue.NearShot] * Fixed.OneRaw / 10;
        foreach (var s in Around(at.X.Raw - near, at.Y.Raw - near, at.X.Raw + near, at.Y.Raw + near, _nearShot, true))
        {
            if (s == t || s.Fight is null || !s.Alive || s.Owner == u.Owner || s.Owner == 0 || s.Inside != 0 || s.Garrison != 0)
            {
                continue;
            }
            var d = s.Pos - at;
            if (d.X.Raw * d.X.Raw + d.Y.Raw * d.Y.Raw <= near * near)
            {
                Suppress(s, sup, u.Pos, r);
                ShotNear(s, u, range);
            }
        }
    }

    private void ShotNear(Unit s, Unit shooter, long range)
    {
        var f = s.Fight!;
        (f.ShotNear2, f.ShotNear1) = (f.ShotNear1, Tick);
        f.ShotFromPercent = (int)Math.Min(1000, (s.Pos - shooter.Pos).Len().Raw * 100 / range);
        var slot = 0;
        for (var i = 0; i < f.Shooters.Length; i++)
        {
            if (f.Shooters[i].Shooter == shooter.Id)
            {
                slot = i;
                break;
            }
            if (f.Shooters[i].Tick < f.Shooters[slot].Tick)
            {
                slot = i;
            }
        }
        f.Shooters[slot] = (shooter.Id, Tick);
    }

    // Suppress adds amount (whole points) to s's suppression from point from: halved in hard cover on that side, three quarters in
    // soft cover, three quarters prone, by his rank and wounds (SuppressionGainPercent); capped at 100. Crossing 30, 60 or 90
    // asks for an urgent think.
    private void Suppress(Unit s, int amount, Vec? from, InfantryCombatRules r)
    {
        var f = s.Fight!;
        if (amount <= 0)
        {
            return;
        }
        long gain = amount * 100L * SuppressionGainPercent(s) / 100 * SpiritSuppress(s) / 100; // E1b-4: Shaken +20%, Broken +40%
        if (from is { } p)
        {
            var k = CoverToward(Cell.Of(s.Pos), Dir32(p.X.Raw - s.Pos.X.Raw, p.Y.Raw - s.Pos.Y.Raw), r);
            gain = gain * (k is CoverKind.Low or CoverKind.High ? r[CombatValue.SuppressHardCover] : k == CoverKind.Soft ? r[CombatValue.SuppressSoftCover] : 100) / 100;
        }
        if (f.Posture == Posture.Prone && f.PostureLeft == 0)
        {
            gain = gain * r[CombatValue.SuppressProne] / 100;
        }
        var before = f.Suppression;
        f.Suppression100 = (int)Math.Min(10000, f.Suppression100 + gain);
        f.SuppressedAt = Tick;
        if (Band(before, r) != Band(f.Suppression, r))
        {
            f.Urgent = true;
        }
    }

    private static int Band(int s, InfantryCombatRules r) =>
        s >= r[CombatValue.PinnedHard] ? 3 : s >= r[CombatValue.Pinned] ? 2 : s >= r[CombatValue.Shaken] ? 1 : 0;

    // NoteMateKilled (NoteHurt): a soldier killed: his squadmates within mateReach gain mateKilled.
    internal void NoteMateKilled(Unit t)
    {
        foreach (var o in _units)
        {
            if (o.Fight is { } of && o.AutoTarget == t.Id)
            {
                of.Urgent = true; // his target is gone: he chooses again next tick (15.3)
            }
        }
        if (Combat is not { } r || !_fights || t.Squad == 0 || Squad(t.Squad) is not { } sq)
        {
            return;
        }
        var reach = (long)r[CombatValue.MateReach] * Fixed.OneRaw / 10;
        foreach (var id in sq.MemberList)
        {
            if (id != t.Id && Unit(id) is { Alive: true, Fight: not null } m)
            {
                var d = m.Pos - t.Pos;
                if (d.X.Raw * d.X.Raw + d.Y.Raw * d.Y.Raw <= reach * reach)
                {
                    Suppress(m, r[CombatValue.MateKilled], null, r);
                }
            }
        }
    }

    // RunSuppressionDecay (after the tick's damage): nothing for holdTicks after the last gain, then decay a tick.
    private void RunSuppressionDecay()
    {
        if (Combat is not { } r || !_fights)
        {
            return;
        }
        foreach (var u in _units)
        {
            if (u.Fight is not { Suppression100: > 0 } f || Tick - f.SuppressedAt < (uint)r[CombatValue.SuppressHoldTicks])
            {
                continue;
            }
            var before = f.Suppression;
            f.Suppression100 = Math.Max(0, f.Suppression100 - r[CombatValue.SuppressDecay]);
            if (Band(before, r) != Band(f.Suppression, r))
            {
                f.Urgent = true;
            }
        }
    }

    // ---- Hit chance (5.2, brief 6.3 and 6.6) ----

    // CombatChance scales a small-arms hit chance (per mille) of u at t: u's posture accuracy and suppression, t's posture and
    // his cover on the side facing u. The engine's small arms roll to hit, so protection applies to the hit chance (5.2).
    internal long CombatChance(Unit u, Unit t, long chance)
    {
        if (Combat is not { } r)
        {
            return chance;
        }
        if (u.Fight is { } uf)
        {
            chance = chance * (uf.Gait != Gait.None && Walking(u) ? GaitAim(u, uf, r) : PostureAim(uf.Posture, r)) / 100; // INF-E1b-2: a gait's aim on the move
            var s = uf.Suppression;
            chance = chance * (100 - (s >= r[CombatValue.Pinned] ? r[CombatValue.AimPinned] : s >= r[CombatValue.Shaken] ? r[CombatValue.AimShaken] : 0)) / 100;
        }
        if (t.Fight is { } tf)
        {
            var d = Dir32(u.Pos.X.Raw - t.Pos.X.Raw, u.Pos.Y.Raw - t.Pos.Y.Raw);
            var prot = Math.Max(Protection(Cell.Of(t.Pos), tf.Posture, d, tf.Ducked), VehicleProtection(Cell.Of(t.Pos), tf.Posture, d, tf.Ducked, t.Owner, r));
            chance = chance * (tf.Gait != Gait.None && Walking(t) ? GaitHit(tf, r) : PostureHit(tf.Posture, r)) / 100 * (100 - prot) / 100;
        }
        return chance;
    }

    private static int PostureAim(Posture p, InfantryCombatRules r) => p switch
    {
        Posture.Crouch => r[CombatValue.AimCrouch],
        Posture.Kneel => r[CombatValue.AimKneel],
        Posture.Prone => r[CombatValue.AimProne],
        _ => r[CombatValue.AimStand],
    };

    private static int PostureHit(Posture p, InfantryCombatRules r) => p switch
    {
        Posture.Crouch => r[CombatValue.HitCrouch],
        Posture.Kneel => r[CombatValue.HitKneel],
        Posture.Prone => r[CombatValue.HitProne],
        _ => r[CombatValue.HitStand],
    };

    // HoldsFire (the combat pass): no shot while changing posture; pinned hard, with no order of his player's, he fires only at
    // enemies within 3 cells (brief 6.6).
    private bool HoldsFire(Unit u, Unit target)
    {
        if (u.Fight is not { } f || Combat is not { } r)
        {
            return false;
        }
        if (f.PostureLeft > 0 || ThinkHolds(u, f, Tick)) // INF-E1b-2: a burst's wait, a new target's delay, a big turn, a throw, a carry
        {
            return true;
        }
        if (HoldsFireTactics(u, f, target, r)) // INF-E1b-3: concealed till the squad opens fire, a mate in the line, the LMG's set-up and arc
        {
            return true;
        }
        if (f.Suppression >= r[CombatValue.PinnedHard] && u.Order.Kind == OrderKind.Idle)
        {
            var d = target.Pos - u.Pos;
            return d.X.Raw * d.X.Raw + d.Y.Raw * d.Y.Raw > 9 * Fixed.OneRaw * Fixed.OneRaw;
        }
        return false;
    }

    /// <summary>His speed percent from his posture (brief 6.3): 0 while changing; a kneeling or prone man moving gets up first
    /// (prone and pinned hard: he crawls); crouched 60%; pinned 25% slower.</summary>
    internal int PosturePercent(Unit u)
    {
        if (u.Fight is not { } f || Combat is not { } r)
        {
            return 100;
        }
        var s = f.Suppression;
        int pct;
        if (f.PostureLeft > 0)
        {
            if (f.PostureTo >= f.Posture || f.Posture == Posture.Prone)
            {
                return 0; // going down, or up from prone: he waits for it
            }
            pct = GaitPercent(u, f, r) * r[CombatValue.GaitRiseMove] / 100; // part 2: he stands up as he steps, not before
            return s >= r[CombatValue.Pinned] ? pct * r[CombatValue.PinnedSpeed] / 100 : pct;
        }
        switch (f.Posture)
        {
            case Posture.Kneel:
                ChangePosture(u, f, s >= r[CombatValue.Shaken] ? Posture.Crouch : Posture.Stand, false, true, r);
                if (f.PostureLeft > 0)
                {
                    pct = GaitPercent(u, f, r) * r[CombatValue.GaitRiseMove] / 100; // part 2: he steps off as he rises
                    break;
                }
                return 100;
            case Posture.Prone:
                if (s >= r[CombatValue.PinnedHard])
                {
                    (f.Gait, pct) = (Gait.Crawl, r[CombatValue.CrawlSpeed]);
                    break;
                }
                ChangePosture(u, f, s >= r[CombatValue.Shaken] ? Posture.Crouch : Posture.Stand, false, true, r);
                return f.PostureLeft > 0 ? 0 : 100;
            default:
                pct = GaitPercent(u, f, r); // INF-E1b-2: the gaits and the back rule (7.2, 7.3)
                break;
        }
        return s >= r[CombatValue.Pinned] ? pct * r[CombatValue.PinnedSpeed] / 100 : pct;
    }

    // ---- Posture (brief 6.3, 9) ----

    private static int ChangeTicks(Posture a, Posture b, InfantryCombatRules r)
    {
        if (a == b)
        {
            return 0;
        }
        if (a > b)
        {
            (a, b) = (b, a);
        }
        return (a, b) switch
        {
            (Posture.Stand, Posture.Crouch) => r[CombatValue.StandCrouch],
            (Posture.Stand, Posture.Kneel) => r[CombatValue.StandKneel],
            (Posture.Crouch, Posture.Kneel) => r[CombatValue.CrouchKneel],
            (Posture.Stand, Posture.Prone) => Math.Max(r[CombatValue.StandCrouch] + r[CombatValue.DownProne], r[CombatValue.ProneUp]),
            _ => Math.Max(r[CombatValue.DownProne], r[CombatValue.ProneCrouch]),
        };
    }

    // ChangePosture starts a change to want (ducked: in cover, ducked). A posture is held postureHoldTicks unless force (a move
    // starting, suppression crossing a band). Stand to prone takes the stand-crouch time plus getting down; prone to stand
    // proneUp; the times scale by PostureChangePercent (a wounded man is slower).
    private void ChangePosture(Unit u, SoldierFight f, Posture want, bool ducked, bool force, InfantryCombatRules r)
    {
        if (want == f.PostureTo)
        {
            f.Ducked = ducked;
            return;
        }
        if (!force && f.PostureLeft == 0 && f.PostureSince != 0 && Tick - f.PostureSince < (uint)r[CombatValue.PostureHoldTicks])
        {
            return;
        }
        var from = f.Posture;
        var ticks = from == Posture.Prone && want == Posture.Stand ? r[CombatValue.ProneUp] :
            from == Posture.Stand && want == Posture.Prone ? r[CombatValue.StandCrouch] + r[CombatValue.DownProne] : ChangeTicks(from, want, r);
        ticks = ticks * PostureChangePercent(u) / 100;
        f.PostureTo = want;
        f.Ducked = ducked;
        f.PostureLeft = ticks;
        if (ticks == 0)
        {
            (f.Posture, f.PostureSince) = (want, Tick);
        }
    }

    // ---- RunMinds ----

    private void RunMinds()
    {
        if (Combat is not { } r || !_fights)
        {
            return;
        }
        BuildIndex(); // units added or put inside since the vision pass (abilities, boarding): the queries below see them as a full scan would
        var m = EnsureCover(r);
        UpdateCover(m, r);
        for (var i = _zones.Count - 1; i >= 0; i--)
        {
            if (Tick >= _zones[i].Until)
            {
                _zones.RemoveAt(i);
            }
        }
        foreach (var u in _units)
        {
            if (u.Fight is { PostureLeft: > 0 } f && --f.PostureLeft == 0)
            {
                (f.Posture, f.PostureSince) = (f.PostureTo, Tick);
            }
            if (u.Fight is { } tf && u.Alive)
            {
                TickFight(u, tf, r); // INF-E1b-2 (InfantryThinkSystem.cs)
            }
        }
        var every = (uint)r[CombatValue.SquadEvery];
        foreach (var s in _squads)
        {
            if (s.Id % every == Tick % every)
            {
                ThinkSquad(s, r);
            }
        }
        _urgentThisTick = 0;
        _searchesThisTick = 0;
        TickGrenades(r);
        foreach (var u in _units)
        {
            if (u.Fight is { SearchWaits: true } wf && u.Alive)
            {
                ThinkPosition(u, wf, r, true); // a search that found the cap full is first in line (21)
            }
        }
        var hot = (uint)r[CombatValue.SoldierEvery];
        var calm = (uint)r[CombatValue.SoldierCalmEvery];
        foreach (var u in _units)
        {
            if (u.Fight is not { } f || !u.Alive)
            {
                continue;
            }
            var due = f.InContact || f.Alert || f.Suppression > 0 ? u.Id % hot == Tick % hot : u.Id % calm == Tick % calm;
            if (!due && f.Urgent)
            {
                if (_urgentThisTick < r[CombatValue.UrgentThinks])
                {
                    _urgentThisTick++;
                    CombatStats.Urgent++;
                    due = true;
                }
                else
                {
                    CombatStats.UrgentCapped++;
                }
            }
            if (due)
            {
                f.Urgent = false;
                ThinkSoldier(u, f, r);
            }
        }
        RunSquadMinds(r); // INF-E1b-3: the squad think, after the soldiers' (SquadTacticsSystem.cs)
        DropIndex(); // units move before combat builds it again: a query until then scans all units, as RunOrders leaves it (row 1242)
    }

    // Able: a member who can take part (section 3): alive, not down, not carried or carrying, not inside.
    private static bool Able(Unit m) =>
        m.Alive && m.Fight is not null && m.Inside == 0 && m.Garrison == 0 && m.Life is not { Wound: WoundState.Down } && m.Life is not { Carrier: not 0 } &&
        m.Life is not { Carrying: not 0 };

    // SquadCentre: the able members' mean cell (integer division); false with none.
    private bool SquadCentre(Squad s, out Cell centre)
    {
        long sx = 0, sy = 0, n = 0;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { } m && Able(m))
            {
                var c = Cell.Of(m.Pos);
                (sx, sy, n) = (sx + c.X, sy + c.Y, n + 1);
            }
        }
        centre = n > 0 ? new Cell((int)(sx / n), (int)(sy / n)) : default;
        return n > 0;
    }

    // ThinkSquad: the hearing scan, the members' own contacts merged into the picture, memory, the cap, the squad axis, in contact;
    // in contact, its own entries go to the friendly squads within shareRadius (4.3).
    private void ThinkSquad(Squad s, InfantryCombatRules r)
    {
        var mind = s.Mind ??= new SquadMind();
        CombatStats.SquadThinks++;
        if (!SquadCentre(s, out var centre))
        {
            mind.PictureList.Clear();
            (mind.Axis, mind.Axis2, mind.InContact, mind.LastThink) = (-1, -1, false, Tick);
            return;
        }
        Hear(centre, s.Owner, mind.LastThink, mind.PictureList, r, true);
        var inContact = false;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is not { Fight: { } f } m || !Able(m))
            {
                continue;
            }
            inContact |= f.InContact;
            foreach (var k in f.ContactList)
            {
                if (k.Source != ContactSource.Reported)
                {
                    Merge(mind.PictureList, k, Tick + (uint)r[CombatValue.SquadShareTicks], false, r);
                }
            }
        }
        Forget(mind.PictureList, r);
        Cap(mind.PictureList, r[CombatValue.ContactsPerSquad], null, r);
        var axis = AxisFrom(centre, null, mind.PictureList, r, out var axis2);
        StepAxis(ref axis, mind.Axis, mind.AxisNext, mind.AxisCount, (int)(Tick - mind.LastThink), r, out var next, out var count);
        (mind.Axis, mind.Axis2, mind.AxisNext, mind.AxisCount) = (axis, axis2, next, count);
        mind.InContact = inContact;
        mind.LastThink = Tick;
        if (!inContact)
        {
            return;
        }
        var radius = (long)r[CombatValue.ShareRadius] * Fixed.OneRaw / 10;
        foreach (var o in _squads)
        {
            if (o == s || o.Owner != s.Owner || !SquadCentre(o, out var oc))
            {
                continue;
            }
            long dx = (long)(oc.X - centre.X) * Fixed.OneRaw, dy = (long)(oc.Y - centre.Y) * Fixed.OneRaw;
            if (dx * dx + dy * dy > radius * radius)
            {
                continue;
            }
            var om = o.Mind ??= new SquadMind();
            foreach (var k in mind.PictureList)
            {
                if (!k.Foreign)
                {
                    var c = k;
                    c.Source = ContactSource.Reported;
                    Merge(om.PictureList, c, Tick + (uint)r[CombatValue.NearShareTicks], true, r);
                }
            }
            Cap(om.PictureList, r[CombatValue.ContactsPerSquad], null, r);
        }
    }

    // Hear (4.1): enemy shooters of owner within their weapon's sound range of `at` who fired since `since`, not already seen now in
    // list, nearest first (ties by ID), at most heardPerThink; each becomes or refreshes a heard area: a square of areaHalf round
    // a centre offset from the shooter by a draw on his ID and tick / areaTicks (the same for every listener for 10 s).
    private void Hear(Cell at, byte owner, uint since, List<Contact> list, InfantryCombatRules r, bool squad)
    {
        if (_maxSound == 0)
        {
            foreach (var w in r.Weapons)
            {
                _maxSound = Math.Max(_maxSound, w.Sound);
            }
        }
        var reach = (long)_maxSound * Fixed.OneRaw / 10;
        var p = at.Center();
        _heardScratch.Clear();
        foreach (var e in Around(p.X.Raw - reach, p.Y.Raw - reach, p.X.Raw + reach, p.Y.Raw + reach, _nearMind, false)) // sorted below
        {
            if (!e.Alive || e.Owner == owner || e.Owner == 0 || e.LastShot == 0 || e.LastShot <= since || e.Def.Weapons.Count == 0 ||
                r.Weapon(e.Def.Weapons[0].Effect) is not { } cw)
            {
                continue;
            }
            var d = e.Pos - p;
            var d2 = d.X.Raw * d.X.Raw + d.Y.Raw * d.Y.Raw;
            var sound = (long)cw.Sound * Fixed.OneRaw / 10;
            if (d2 > sound * sound || SeenIn(list, e.Id))
            {
                continue;
            }
            _heardScratch.Add((d2, e.Id));
        }
        _heardScratch.Sort();
        var n = Math.Min(_heardScratch.Count, r[CombatValue.HeardPerThink]);
        for (var i = 0; i < n; i++)
        {
            var e = Unit(_heardScratch[i].Id)!;
            var h = Rand.Hash(Match.Setup.Seed, e.Id, Tick / (uint)r[CombatValue.AreaTicks], AreaSalt);
            var off = r[CombatValue.AreaOffset] / 10;
            var span = (ulong)(2 * off + 1);
            var c = Cell.Of(e.Pos);
            var centre = new Cell(Math.Clamp(c.X + (int)(h % span) - off, 0, Grid.Width - 1), Math.Clamp(c.Y + (int)((h >> 20) % span) - off, 0, Grid.Height - 1));
            var cls = r.Weapon(e.Def.Weapons[0].Effect)?.Class ?? CombatClass.Unknown;
            Merge(list, new Contact { Enemy = 0, At = centre, Source = ContactSource.Heard, Class = cls, Sources = 1, Tick = Tick, Heard = e.Id },
                squad ? Tick + (uint)r[CombatValue.SquadShareTicks] : Tick, false, r);
            CombatStats.Heard++;
        }
    }

    private static bool SeenIn(List<Contact> list, uint id)
    {
        foreach (var k in list)
        {
            if (k.Enemy == id && k.Source == ContactSource.SeenNow)
            {
                return true;
            }
        }
        return false;
    }

    // Merge puts contact k into list: a unit entry by its ID (the better source wins, then the newer tick); a heard area merged with
    // one whose centre is within areaMerge (the newer centre kept, a different shooter adds a source, at most 3); else added.
    private static void Merge(List<Contact> list, Contact k, uint shareAt, bool foreign, InfantryCombatRules r)
    {
        var merge = r[CombatValue.AreaMerge] / 10;
        for (var i = 0; i < list.Count; i++)
        {
            var o = list[i];
            if (k.Enemy != 0 && o.Enemy == k.Enemy)
            {
                if (o.Foreign && !foreign)
                {
                    o.Foreign = false; // seen or heard by the squad itself now: its own
                }
                if (k.Source < o.Source || (k.Source == o.Source && k.Tick > o.Tick))
                {
                    var better = k.Source < o.Source;
                    (o.Source, o.At, o.Tick, o.Class) = (k.Source, k.At, k.Tick, k.Class);
                    if (better)
                    {
                        o.ShareAt = shareAt;
                    }
                }
                o.FiringAtUs |= k.FiringAtUs;
                list[i] = o;
                return;
            }
            if (k.Enemy == 0 && o.Enemy == 0 && Math.Abs(o.At.X - k.At.X) <= merge && Math.Abs(o.At.Y - k.At.Y) <= merge)
            {
                if (k.Tick >= o.Tick)
                {
                    o.At = k.At;
                    o.Tick = k.Tick;
                }
                if (k.Heard != 0 && k.Heard != o.Heard)
                {
                    o.Sources = (byte)Math.Min(3, o.Sources + k.Sources);
                    o.Heard = k.Heard;
                }
                o.Source = k.Source < o.Source ? k.Source : o.Source;
                list[i] = o;
                return;
            }
        }
        k.ShareAt = shareAt;
        k.Foreign = foreign;
        list.Add(k);
    }

    // Forget drops contacts past their memory (seen before 6 s, heard and hit-from 10 s, reported 8 s) and heard areas a contact
    // seen now stands in.
    private void Forget(List<Contact> list, InfantryCombatRules r)
    {
        var half = r[CombatValue.AreaHalf] / 10;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var k = list[i];
            var memory = k.Source switch
            {
                ContactSource.SeenNow or ContactSource.SeenBefore => r[CombatValue.SeenMemory],
                ContactSource.Reported => r[CombatValue.ReportedMemory],
                _ => r[CombatValue.HeardMemory],
            };
            var gone = Tick - k.Tick > (uint)memory || (k.Enemy != 0 && Unit(k.Enemy) is not { Alive: true });
            if (!gone && k.Enemy == 0)
            {
                foreach (var o in list)
                {
                    if (o.Enemy != 0 && o.Source == ContactSource.SeenNow && Math.Abs(o.At.X - k.At.X) <= half && Math.Abs(o.At.Y - k.At.Y) <= half)
                    {
                        gone = true;
                        break;
                    }
                }
            }
            if (gone)
            {
                list.RemoveAt(i);
            }
        }
    }

    // Cap drops the least dangerous (to u; to a squad: without his own firing-at-us test) until max are left; ties: the oldest
    // tick, then an area before a unit, then the higher enemy ID.
    private void Cap(List<Contact> list, int max, Unit? u, InfantryCombatRules r)
    {
        while (list.Count > max)
        {
            var worst = 0;
            var wd = Danger(list[0], u, r);
            for (var i = 1; i < list.Count; i++)
            {
                var d = Danger(list[i], u, r);
                var a = list[i];
                var b = list[worst];
                if (d < wd || (d == wd && (a.Tick < b.Tick || (a.Tick == b.Tick && ((a.Enemy == 0 && b.Enemy != 0) ||
                    ((a.Enemy == 0) == (b.Enemy == 0) && a.Enemy > b.Enemy))))))
                {
                    (worst, wd) = (i, d);
                }
            }
            list.RemoveAt(worst);
        }
    }

    // ClassOf is unit e's danger class: a soldier's main arm's, else by tags (armour, light vehicle, helicopter).
    private static CombatClass ClassOf(Unit e, InfantryCombatRules r)
    {
        if (e.Life is not null && e.Def.Weapons.Count > 0)
        {
            return r.Weapon(e.Def.Weapons[0].Effect)?.Class ?? CombatClass.Unknown;
        }
        var t = e.Def.Tags;
        return (t.Bits & r.ArmourTags.Bits) != 0 ? CombatClass.Armour : (t.Bits & r.HelicopterTags.Bits) != 0 ? CombatClass.Helicopter :
            (t.Bits & r.LightVehicleTags.Bits) != 0 ? CombatClass.LightVehicle : CombatClass.Unknown;
    }

    /// <summary>
    /// The danger (4.4) of contact k to soldier u (null: to his squad, without the marksman's aim): the class's value, a heard
    /// area's per merged source (at most areaMax); halved when not seen now, doubled when firing at us.
    /// </summary>
    internal int Danger(Contact k, Unit? u, InfantryCombatRules r)
    {
        int d;
        if (k.Enemy == 0)
        {
            d = Math.Min(r[CombatValue.DangerAreaMax], r[CombatValue.DangerArea] * Math.Max(1, (int)k.Sources));
        }
        else
        {
            d = k.Class switch
            {
                CombatClass.Rifle => r[CombatValue.DangerRifle],
                CombatClass.Carbine => r[CombatValue.DangerCarbine],
                CombatClass.Smg => r[CombatValue.DangerSmg],
                CombatClass.Pistol => r[CombatValue.DangerPistol],
                CombatClass.Lmg => Unit(k.Enemy) is { Fight.Deployed: true } ? r[CombatValue.DangerLmgDeployed] : r[CombatValue.DangerLmg],
                CombatClass.Marksman => u is not null && Unit(k.Enemy) is { Fight: { } ef } && ef.LastTarget == u.Id &&
                    Tick - ef.LastTargetTick < (uint)r[CombatValue.ShotAtMemoryTicks] ? r[CombatValue.DangerMarksman] : r[CombatValue.DangerMarksmanOthers],
                CombatClass.Launcher => r[CombatValue.DangerLauncher],
                CombatClass.Crew => r[CombatValue.DangerCrew],
                CombatClass.LightVehicle => r[CombatValue.DangerLightVehicle],
                CombatClass.Armour => r[CombatValue.DangerArmour],
                CombatClass.Helicopter => r[CombatValue.DangerHelicopter],
                _ => r[CombatValue.DangerUnknown],
            };
        }
        var half = k.Source != ContactSource.SeenNow;
        if (half && !k.FiringAtUs)
        {
            d /= 2;
        }
        else if (!half && k.FiringAtUs)
        {
            d *= 2;
        }
        return d;
    }

    // ReachOf is contact k's reach (raw): its range x reachPercent (pistol and marksman: their range; launcher, crew and areas: a
    // fixed reach); RangeOf its weapon's range.
    private long ReachOf(Contact k, InfantryCombatRules r, out long range)
    {
        range = k.Enemy != 0 && Unit(k.Enemy) is { } e && e.Def.Weapons.Count > 0 ? e.Def.Weapons[0].Range.Raw : 5 * Fixed.OneRaw;
        long Tenths(CombatValue v) => r[v] * Fixed.OneRaw / 10;
        if (k.Enemy == 0)
        {
            range = Tenths(CombatValue.AreaReach);
            return range;
        }
        return k.Class switch
        {
            CombatClass.Pistol or CombatClass.Marksman => range,
            CombatClass.Launcher => Tenths(CombatValue.LauncherReach),
            CombatClass.Crew => Tenths(CombatValue.CrewReach),
            _ => range * r[CombatValue.ReachPercent] / 100,
        };
    }

    // OwnRange: the range of a soldier's first small arm (a crew's carbine), else of his first weapon.
    private static long OwnRange(Unit u, InfantryCombatRules r)
    {
        foreach (var w in u.Def.Weapons)
        {
            if (r.Weapon(w.Effect) is { Class: not (CombatClass.Launcher or CombatClass.Crew or CombatClass.Unknown) })
            {
                return w.Range.Raw;
            }
        }
        return u.Def.Weapons.Count > 0 ? u.Def.Weapons[0].Range.Raw : 0;
    }

    // AxisFrom (4.5): the table direction best fitting the sum of (direction from `at` to each contact) x danger, over the contacts
    // that have `at` in their reach or (a soldier) lie within his contact range; -1 with none. axis2: the second front.
    private int AxisFrom(Cell at, Unit? u, List<Contact> list, InfantryCombatRules r, out int axis2)
    {
        long sx = 0, sy = 0;
        int best1 = -1, d1 = 0, best2 = -1, d2 = 0;
        var own = u is null ? 0 : OwnRange(u, r) * r[CombatValue.ContactRangePercent] / 100;
        var p = at.Center();
        foreach (var k in list)
        {
            if (u is not null && k.KnownAt > Tick)
            {
                continue;
            }
            var q = k.At.Center();
            long dx = q.X.Raw - p.X.Raw, dy = q.Y.Raw - p.Y.Raw;
            var dist2 = dx * dx + dy * dy;
            var reach = ReachOf(k, r, out _);
            if (u is not null && dist2 > reach * reach && dist2 > own * own)
            {
                continue;
            }
            var dir = Dir32(dx, dy);
            if (dir < 0)
            {
                continue;
            }
            var dg = Danger(k, u, r);
            sx += (long)Directions[dir].X * dg;
            sy += (long)Directions[dir].Y * dg;
            if (dg > d1)
            {
                (best2, d2, best1, d1) = (best1, d1, dir, dg);
            }
            else if (dg > d2)
            {
                (best2, d2) = (dir, dg);
            }
        }
        axis2 = best1 >= 0 && best2 >= 0 && d2 >= r[CombatValue.TwoFrontDanger] && DirSteps(best1, best2) >= r[CombatValue.TwoFrontSteps] ? best2 : -1;
        if (axis2 >= 0)
        {
            return best1;
        }
        return sx == 0 && sy == 0 ? (best1 >= 0 ? best1 : -1) : Dir32(sx, sy);
    }

    // StepAxis: the hysteresis (4.5): a new axis 2 steps or more off the old one only after axisTicks in a row; at once with none.
    private static void StepAxis(ref int axis, int old, int oldNext, int oldCount, int elapsed, InfantryCombatRules r, out int next, out int count)
    {
        (next, count) = (-1, 0);
        if (axis < 0 || old < 0 || DirSteps(axis, old) < r[CombatValue.AxisSteps])
        {
            return;
        }
        var want = axis;
        (next, count) = want == oldNext ? (want, oldCount + elapsed) : (want, elapsed);
        if (count >= r[CombatValue.AxisTicks])
        {
            (next, count) = (-1, 0);
            return;
        }
        axis = old;
    }

    // ThinkSoldier: his contacts, axis, contact state, reaction and posture (4, 9).
    private void ThinkSoldier(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        CombatStats.Thinks++;
        var since = f.LastThink;
        var elapsed = (int)(Tick - since);
        var list = f.ContactList;
        var had = false;
        foreach (var k in list)
        {
            had |= k.KnownAt <= Tick;
        }
        var knewAny = list.Count > 0;
        var addedOwn = false;
        // Seen now: enemies within his vision his side sees; any who fired within fireRevealTicks within its range + 3 cells.
        var vision = u.Def.Vision.Raw;
        var extra = (long)r[CombatValue.FireRevealExtra] * Fixed.OneRaw / 10;
        var me = Cell.Of(u.Pos);
        if ((u.SeenBy & ~Bit(u.Owner)) != 0)
        {
            f.SpottedAt = Tick;
        }
        foreach (var e in AroundFrom(u, vision, _nearMind, true))
        {
            if (e.Owner == u.Owner || e.Owner == 0 || !e.Alive || e.Def.IsStructure || e.Def.Weapons.Count == 0 || e.Inside != 0)
            {
                continue;
            }
            var d = e.Pos - u.Pos;
            var d2 = d.X.Raw * d.X.Raw + d.Y.Raw * d.Y.Raw;
            if (d2 > vision * vision)
            {
                continue;
            }
            var reveal = e.LastShot != 0 && Tick - e.LastShot < (uint)r[CombatValue.FireRevealTicks] &&
                d2 <= (e.Def.Weapons[0].Range.Raw + extra) * (e.Def.Weapons[0].Range.Raw + extra);
            if (!reveal && !IsVisibleTo(e, u.Owner))
            {
                continue;
            }
            addedOwn |= !Has(list, e.Id);
            Upsert(list, new Contact { Enemy = e.Id, At = Cell.Of(e.Pos), Source = ContactSource.SeenNow, Class = ClassOf(e, r), Tick = Tick,
                KnownAt = Tick + (uint)r[CombatValue.LaterContact] });
        }
        for (var i = 0; i < list.Count; i++)
        {
            var k = list[i];
            if (k.Source == ContactSource.SeenNow && k.Tick != Tick)
            {
                k.Source = ContactSource.SeenBefore;
                list[i] = k;
            }
        }
        // Hit from: the direction a hit came from, as a heard contact.
        if (u.LastHitTick != 0 && u.LastHitTick > since)
        {
            var at = Cell.Of(u.LastHitFrom);
            if (!NearSeen(list, at, r[CombatValue.AreaMerge] / 10))
            {
                addedOwn = true;
                Merge(list, new Contact { At = at, Source = ContactSource.HitFrom, Class = CombatClass.Unknown, Sources = 1, Tick = Tick, KnownAt = Tick },
                    Tick, false, r);
            }
        }
        // Reported: the squad picture's entries once shared.
        var reported = false;
        if (u.Squad != 0 && Squad(u.Squad) is { Mind: { } mind })
        {
            foreach (var k in mind.PictureList)
            {
                if (k.ShareAt > Tick || (k.Enemy != 0 && Has(list, k.Enemy)))
                {
                    continue;
                }
                var c = k;
                c.Source = ContactSource.Reported;
                c.KnownAt = Tick;
                c.Foreign = false;
                c.ShareAt = 0;
                if (k.Enemy != 0 || !NearSeen(list, k.At, r[CombatValue.AreaMerge] / 10))
                {
                    reported = true;
                    Merge(list, c, 0, false, r);
                }
            }
        }
        else
        {
            Hear(me, u.Owner, since, list, r, false);
        }
        Forget(list, r);
        // Firing at us: one of the last shooters at him (or a squadmate) in the last 3 s (gathered once a think: E1b-2's cost cut).
        var shooters = ShootersAt(u, r);
        for (var i = 0; i < list.Count; i++)
        {
            var k = list[i];
            k.FiringAtUs = k.Enemy != 0 && shooters.Contains(k.Enemy);
            list[i] = k;
        }
        Cap(list, r[CombatValue.ContactsPerSoldier], u, r);
        // Reaction (4.6): one timer, from knowing no contact to knowing one.
        if (!knewAny && list.Count > 0)
        {
            var surprised = f.ShotNear1 != 0 && Tick - f.ShotNear1 < (uint)r[CombatValue.ShotAtMemoryTicks];
            int lo, hi;
            if (!addedOwn && reported)
            {
                (lo, hi) = (r[CombatValue.ShareReact], r[CombatValue.ShareReact]);
            }
            else if (surprised)
            {
                (lo, hi) = (r[CombatValue.SurpriseMin], r[CombatValue.SurpriseMax]);
            }
            else
            {
                (lo, hi) = (r[CombatValue.ReactMin], r[CombatValue.ReactMax]);
            }
            var delay = lo + (int)(Rand.Hash(Match.Setup.Seed, Tick, u.Id, ReactSalt) % (ulong)(hi - lo + 1));
            delay -= u.Life?.Rank switch
            {
                SoldierRank.Regular => r[CombatValue.ReactRegular],
                SoldierRank.Veteran => r[CombatValue.ReactVeteran],
                SoldierRank.Elite => r[CombatValue.ReactElite],
                _ => 0,
            };
            delay = Math.Max(r[CombatValue.ReactFloor], delay + SpiritReact(f, r)); // E1b-4 (4.6): Shaken +2, Broken +4
            f.ReactAt = Tick + (uint)delay;
            for (var i = 0; i < list.Count; i++)
            {
                var k = list[i];
                k.KnownAt = f.ReactAt;
                list[i] = k;
            }
        }
        else if (list.Count == 0)
        {
            f.ReactAt = 0;
        }
        _ = had;
        // Axis, in contact, alert.
        var axis = AxisFrom(me, u, list, r, out var axis2);
        StepAxis(ref axis, f.Axis, f.AxisNext, f.AxisCount, elapsed, r, out var next, out var count);
        (f.Axis, f.Axis2, f.AxisNext, f.AxisCount) = (axis, axis2, next, count);
        var inContact = (f.ShotNear1 != 0 && Tick - f.ShotNear1 < (uint)r[CombatValue.ShotAtMemoryTicks]) ||
            (u.LastHitTick != 0 && Tick - u.LastHitTick < (uint)r[CombatValue.ShotAtMemoryTicks]);
        var alert = false;
        var own = OwnRange(u, r) * r[CombatValue.ContactRangePercent] / 100;
        var alertRange = (long)r[CombatValue.AlertRange] * Fixed.OneRaw / 10;
        foreach (var k in list)
        {
            if (k.KnownAt > Tick)
            {
                continue;
            }
            var q = k.At.Center();
            long dx = q.X.Raw - u.Pos.X.Raw, dy = q.Y.Raw - u.Pos.Y.Raw;
            var dist2 = dx * dx + dy * dy;
            var reach = ReachOf(k, r, out _);
            inContact |= dist2 <= reach * reach || dist2 <= own * own;
            alert |= dist2 <= alertRange * alertRange;
        }
        (f.InContact, f.Alert) = (inContact, !inContact && alert);
        f.LastThink = Tick;
        ThinkFight(u, f, r); // INF-E1b-2: targets, facing, grenades, position, vehicles (InfantryThinkSystem.cs)
        ChoosePosture(u, f, r);
    }

    private static bool Has(List<Contact> list, uint id)
    {
        foreach (var k in list)
        {
            if (k.Enemy == id)
            {
                return true;
            }
        }
        return false;
    }

    private static bool NearSeen(List<Contact> list, Cell at, int cells)
    {
        foreach (var k in list)
        {
            if (k.Enemy != 0 && Math.Abs(k.At.X - at.X) <= cells && Math.Abs(k.At.Y - at.Y) <= cells)
            {
                return true;
            }
        }
        return false;
    }

    private static void Upsert(List<Contact> list, Contact k)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Enemy == k.Enemy)
            {
                var o = list[i];
                (o.At, o.Source, o.Class, o.Tick) = (k.At, k.Source, k.Class, k.Tick);
                list[i] = o;
                return;
            }
        }
        list.Add(k);
    }

    private readonly List<uint> _shooters = [];

    // ShootersAt: the enemies who shot at u, or at a squadmate of his, within shotAtMemoryTicks (each once, in the order met).
    private List<uint> ShootersAt(Unit u, InfantryCombatRules r)
    {
        var memory = (uint)r[CombatValue.ShotAtMemoryTicks];
        _shooters.Clear();
        void Add(SoldierFight f)
        {
            foreach (var (s, t) in f.Shooters)
            {
                if (s != 0 && Tick - t < memory && !_shooters.Contains(s))
                {
                    _shooters.Add(s);
                }
            }
        }
        Add(u.Fight!);
        if (u.Squad != 0 && Squad(u.Squad) is { } sq)
        {
            foreach (var m in sq.MemberList)
            {
                if (m != u.Id && Unit(m) is { Fight: { } mf })
                {
                    Add(mf);
                }
            }
        }
        return _shooters;
    }

    // FiredAt: enemy id shot at u, or at a squadmate of his, within shotAtMemoryTicks.
    private bool FiredAt(Unit u, uint id, InfantryCombatRules r)
    {
        var memory = (uint)r[CombatValue.ShotAtMemoryTicks];
        if (Shot(u.Fight!, id, memory))
        {
            return true;
        }
        if (u.Squad != 0 && Squad(u.Squad) is { } s)
        {
            foreach (var m in s.MemberList)
            {
                if (m != u.Id && Unit(m) is { Fight: { } mf } && Shot(mf, id, memory))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private bool Shot(SoldierFight f, uint id, uint memory)
    {
        foreach (var (s, t) in f.Shooters)
        {
            if (s == id && Tick - t < memory)
            {
                return true;
            }
        }
        return false;
    }

    // CoverWithin: a cell within `tenths` of c with cover toward direction d.
    private bool CoverWithin(Cell c, int d, int tenths, InfantryCombatRules r)
    {
        var rc = tenths / 10;
        for (var dy = -rc; dy <= rc; dy++)
        {
            for (var dx = -rc; dx <= rc; dx++)
            {
                var n = new Cell(c.X + dx, c.Y + dy);
                if (dx * dx + dy * dy <= rc * rc && Grid.Contains(n) && Grid.Passable(n) && CoverToward(n, d, r) != CoverKind.None)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ChoosePosture: the posture table (9.1) as far as it needs no squad think (row 2 is a hook; row 9's set-up: an lmg, marksman or
    // crew weapon in contact on open ground lies down to fire, the set-up itself is INF-E1b-2's).
    private void ChoosePosture(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        if (u.Life is { Wound: WoundState.Down } or { Carrier: not 0 } || u.Inside != 0 || u.Garrison != 0)
        {
            return; // row 1: as those states say
        }
        if (f.Forced is { } forced)
        {
            ChangePosture(u, f, forced, false, false, r); // row 2: a stance or order that forces one (none today)
            return;
        }
        if (f.ThrowAt != 0)
        {
            return; // throwing (14.1): as he was
        }
        var s = f.Suppression;
        var moving = Walking(u);
        var ordered = moving && u.Order.Kind != OrderKind.Idle;
        var reacted = f.ReactAt == 0 || Tick >= f.ReactAt;
        var axis = reacted ? f.Axis : -1;
        var inContact = f.InContact && reacted;
        var c = Cell.Of(u.Pos);
        var force = moving;
        var leaving = moving && f.SpotMoving && ZoneValue(c) > 0; // part 4 (14.4): on his way out of a danger zone he crouch-runs, not crawls
        if (s >= r[CombatValue.PinnedHard] && !ordered && !leaving)
        {
            // Row 3: pinned hard.
            if (axis >= 0 && CoverWithin(c, axis, r[CombatValue.PinnedCover], r))
            {
                ChangePosture(u, f, Posture.Crouch, true, true, r);
            }
            else
            {
                ChangePosture(u, f, Posture.Prone, false, true, r);
            }
            return;
        }
        if (moving)
        {
            // Rows 4 to 6 (gaits are INF-E1b-2's: walk stands, crouched under suppression 30+, prone and pinned hard crawls).
            var want = f.Posture == Posture.Prone && s >= r[CombatValue.PinnedHard] && !leaving ? Posture.Prone : s >= r[CombatValue.Shaken] ? Posture.Crouch : Posture.Stand;
            ChangePosture(u, f, want, false, force, r);
            return;
        }
        if (!reacted)
        {
            return; // 4.6: during his reaction delay he goes on as he was
        }
        var bandForce = s >= r[CombatValue.Pinned];
        var side = CoverToward(c, axis, r);
        if ((f.Cornered || (Tick < f.AirProneUntil && side == CoverKind.None)) && ProneAllowed(u, c))
        {
            ChangePosture(u, f, Posture.Prone, false, true, r); // E1b-4 part 3: the cornered routing man (12.4); an air attack run (14.4)
            return;
        }
        if (axis >= 0 && side == CoverKind.Low)
        {
            ChangePosture(u, f, Posture.Kneel, false, bandForce, r); // row 7
            return;
        }
        if (axis >= 0 && side is CoverKind.High or CoverKind.Soft)
        {
            ChangePosture(u, f, Posture.Stand, false, bandForce, r); // row 8
            return;
        }
        if (inContact)
        {
            var effective = (f.ShotNear2 != 0 && Tick - f.ShotNear2 < (uint)r[CombatValue.EffectiveTicks]) ||
                (u.LastHitTick != 0 && Tick - u.LastHitTick < (uint)r[CombatValue.EffectiveHitTicks]);
            var far = f.ShotFromPercent > r[CombatValue.ProneFireRangePercent];
            var pinned = s >= r[CombatValue.Pinned] && !CoverWithin(c, axis, r[CombatValue.ProneCover], r);
            var setUp = u.Def.Weapons.Count > 0 && r.Weapon(u.Def.Weapons[0].Effect) is { Class: CombatClass.Lmg or CombatClass.Marksman or CombatClass.Crew };
            if (ProneAllowed(u, c) && ((effective && far) || pinned || setUp || NoWayBack(u, r)))
            {
                ChangePosture(u, f, Posture.Prone, false, bandForce, r); // row 9 (part 4: and 12.2's no point worth it)
            }
            else
            {
                ChangePosture(u, f, Posture.Kneel, false, bandForce, r); // row 10
            }
            return;
        }
        var mode = u.Squad != 0 && Squad(u.Squad) is { } sq ? sq.Mode : SquadMode.March;
        ChangePosture(u, f, mode is SquadMode.Hold or SquadMode.Ambush ? Posture.Crouch : Posture.Stand, false, false, r); // rows 11, 12
    }

    /// <summary>Whether he is on the move: a path, or a step routing wants of him (a squad slot, a chase) last tick.</summary>
    internal static bool Walking(Unit u) => u.Moving || (!u.Intent.Arrived && (u.Intent.Desired.X.Raw != 0 || u.Intent.Desired.Y.Raw != 0));

    // ProneAllowed (9.2): not inside, not in water (no proneBlocked roads or rubble, no crops hiding the line of fire yet).
    private bool ProneAllowed(Unit u, Cell c) => u.Inside == 0 && u.Garrison == 0 && !InWater(c);

    /// <summary>
    /// Exposure (5.3) of soldier u at cell c in posture p against the contacts he knows, in hundredths of danger a second, plus the
    /// danger zones there: per contact within its reach danger x (100 - protection) x postureHit x concealFactor x rangeFactor,
    /// one division at the end. Alert (nobody can reach him yet): every contact within alertRange as if at its full range.
    /// </summary>
    public long Exposure(Unit u, Cell c, Posture p)
    {
        if (u.Fight is not { } f || Combat is not { } r)
        {
            return 0;
        }
        long sum = 0;
        var pc = c.Center();
        var concealed = _cover is { } m && Grid.Contains(c) && m.Density(c.Y * m.W + c.X) >= r[CombatValue.ConcealDensity];
        var alertRange = (long)r[CombatValue.AlertRange] * Fixed.OneRaw / 10;
        foreach (var k in f.ContactList)
        {
            if (k.KnownAt > Tick)
            {
                continue;
            }
            var q = k.At.Center();
            long dx = q.X.Raw - pc.X.Raw, dy = q.Y.Raw - pc.Y.Raw;
            var d2 = dx * dx + dy * dy;
            var reach = ReachOf(k, r, out var range);
            long rangeF;
            if (!f.InContact)
            {
                if (d2 > alertRange * alertRange)
                {
                    continue;
                }
                rangeF = r[CombatValue.AtRangeFactor];
            }
            else
            {
                if (d2 > reach * reach)
                {
                    continue;
                }
                var d = (q - pc).Len().Raw;
                var nearR = range * r[CombatValue.NearRangePercent] / 100;
                rangeF = d <= nearR ? 100 : d <= range ? 100 - (100 - r[CombatValue.AtRangeFactor]) * (d - nearR) / Math.Max(1, range - nearR) : r[CombatValue.InReachFactor];
            }
            var dk = Dir32(dx, dy);
            var prot = Math.Max(Protection(c, p, dk, false), VehicleProtection(c, p, dk, false, u.Owner, r));
            var conceal = concealed && Tick - f.SpottedAt > (uint)r[CombatValue.ConcealSeenTicks] ? r[CombatValue.ConcealFactor] : 100;
            sum += Danger(k, u, r) * (100L - prot) * PostureHit(p, r) * conceal * rangeF;
        }
        return sum / 1_000_000 + ZoneValue(c) * 100L;
    }

    // ---- Hash ----

    private void HashInfantryCombat(ref WordHash h)
    {
        if (!_fights)
        {
            return;
        }
        h.Put(0x1CB1); // only matches with the infantryCombat block and soldiers (INF-E1b-1)
        foreach (var u in _units)
        {
            if (u.Fight is { } f)
            {
                h.Put(u.Id);
                f.HashInto(ref h);
            }
        }
        foreach (var s in _squads)
        {
            if (s.Mind is { } m)
            {
                h.Put(s.Id);
                m.HashInto(ref h);
            }
        }
        HashThinkWorld(ref h); // INF-E1b-2: grenades
        h.PutSigned(_zones.Count);
        foreach (var z in _zones)
        {
            h.Put4(unchecked((ulong)(((long)z.Centre.X << 20) ^ (z.Centre.Y & 0xFFFFF))), (ulong)(uint)z.InnerRaw | ((ulong)(uint)z.InnerValue << 32),
                (ulong)(uint)z.OuterRaw | ((ulong)(uint)z.OuterValue << 32), z.Until);
        }
    }

    // HardCoverNearByData (the rescue's HardCoverNear with the infantryCombat block): the nearest standable cell within reach of p
    // whose side facing threat has hard cover (low, high, or a crest), nearest first, ties by row then column; null with none.
    private Cell? HardCoverNearByData(Vec p, Vec threat, Fixed reach)
    {
        var r = Combat!;
        var at = Cell.Of(p);
        Cell? best = null;
        long bestD = long.MaxValue;
        var rc = (int)(reach.Raw / Fixed.OneRaw);
        for (var dy = -rc; dy <= rc; dy++)
        {
            for (var dx = -rc; dx <= rc; dx++)
            {
                var c = new Cell(at.X + dx, at.Y + dy);
                long d2 = (long)dx * dx + (long)dy * dy;
                if (d2 * Fixed.OneRaw * Fixed.OneRaw > reach.Raw * reach.Raw || d2 >= bestD || !Grid.Contains(c) || !Grid.Passable(c))
                {
                    continue;
                }
                var cc = c.Center();
                if (CoverToward(c, Dir32(threat.X.Raw - cc.X.Raw, threat.Y.Raw - cc.Y.Raw), r) is CoverKind.Low or CoverKind.High)
                {
                    (best, bestD) = (c, d2);
                }
            }
        }
        return best;
    }
}
