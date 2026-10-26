using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// A forest cell on fire (<see cref="World.Fires"/>; docs/design.md, "Forest fire"): it burns from tick <see cref="Start"/>
/// for <see cref="Length"/> ticks, its standing trunk (if any) with it, then burns out (<see cref="World.BurntCells"/>).
/// </summary>
/// <param name="Cell">The cell.</param>
/// <param name="Start">The tick it caught fire.</param>
/// <param name="Length">How long it burns (ticks), rolled per cell from the fire rules' range.</param>
/// <param name="SpreadPercent">The percent of the fire rules' spread chances it lights its neighbours with (0: it does not spread: a normal bomb's).</param>
/// <param name="By">The unit whose blast lit it (the shooter), or 0 (fire spreading from a neighbour, a blast without a shooter).</param>
public readonly record struct Fire(Cell Cell, uint Start, int Length, int SpreadPercent, uint By)
{
    /// <summary>The tick it burns out (in that tick's fire step).</summary>
    public uint Ends => Start + (uint)Length;
}

/// <summary>
/// A cell that burnt out (<see cref="World.BurntCells"/>): it lost one forest level; it glows (<see cref="FireState.Burnt"/>)
/// until <see cref="EndedAt"/> plus the fire rules' ember ticks, then it is cold ash (<see cref="FireState.Ash"/>).
/// </summary>
/// <param name="Cell">The cell.</param>
/// <param name="EndedAt">The tick its fire went out.</param>
public readonly record struct BurntCell(Cell Cell, uint EndedAt);

/// <summary>What fire did to a cell (<see cref="World.FireAt"/>).</summary>
public enum FireState : byte
{
    /// <summary>Never on fire.</summary>
    None,

    /// <summary>Burning now (<see cref="World.Fires"/>): flames, smoke, a wall to vehicles, hurting infantry.</summary>
    Burning,

    /// <summary>Burnt out lately: dark ground with embers (it lost one forest level; its trunk, if any, a black snag).</summary>
    Burnt,

    /// <summary>Burnt out and cold: ash (it never burns again).</summary>
    Ash,
}

/// <summary>What happened to a cell's fire this tick (<see cref="FireEvent"/>).</summary>
public enum FireEventKind : byte
{
    /// <summary>The cell caught fire (from a blast: <see cref="FireEvent.By"/> its shooter; or from a burning neighbour).</summary>
    Ignited,

    /// <summary>The cell burnt out: one forest level down, embers; <see cref="FireEvent.Snag"/> whether a black snag stands there.</summary>
    BurntOut,

    /// <summary>A burnt cell's embers went cold: ash.</summary>
    Cooled,
}

/// <summary>A fire change of this tick (<see cref="World.FireEvents"/>), for renderers. Not game state.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Cell">The cell.</param>
/// <param name="Length">Ignited: how long it will burn (ticks); otherwise 0.</param>
/// <param name="By">Ignited: the unit whose blast lit it, or 0.</param>
/// <param name="Spreads">Ignited: whether it can light its neighbours.</param>
/// <param name="Snag">Whether a tree trunk stands in the cell (burning, or a black snag once burnt).</param>
public readonly record struct FireEvent(FireEventKind Kind, Cell Cell, int Length, uint By, bool Spreads, bool Snag);

// Forest fire (docs/design.md, "Forest fire"; the owner, 2026-10-25: "fire must not lag", "normal airstrikes make minimal fire").
// It burns on a map with terrain when the content has a fire block (content/mvp/fire.json):
//  - a blast of a warhead with a fire block (warheads.json; IgniteByBlast, from the same hooks that fell trees, after them) lights
//    the forest cells within its radius: at most its rolled count of them, the cells with a standing trunk first, then the
//    nearest (a normal bomb: 1 or 2 that never spread), or every one (count left out: an incendiary, the hypersonic missile's
//    ring). Shells, rockets and missiles have no fire block: they light nothing;
//  - a lit cell burns for its rolled length (the rules' burnTicks), its trunk with it; every spreadEveryTicks (staggered by cell,
//    so the looks spread over the ticks) a burning cell that spreads lights each of its 8 neighbours with the rules' per-mille
//    chance by that neighbour's forest density (corners at the diagonal percent), times its spread percent; no wind. At most
//    maxBurning cells burn at once on the whole map, and spreading lights at most maxIgnitionsPerTick a tick;
//  - burnt out, a cell is burnt for good (it never burns again): embers for emberTicks, then ash; one forest level down (sight
//    opens a little, vehicles cross it as the thinner forest; infantry as before: thinnedForestAsBefore). Its standing trunk
//    stays: a black snag (an obstacle as any trunk);
//  - the terrain gets the fire in batches, every terrainEveryTicks (ApplyFire): the cells lit since become walls (or dearer) to
//    each movement class by the terrain rules' fireCost, the cells burnt out lose them and their forest level. Routing refreshes
//    only the cells round them and the classes they changed, and a group re-plans once a batch, not once a cell; ground units
//    on their own route whose way ahead crosses a newly walled cell ask for a new one (every RerouteEvery ticks);
//  - burning forest pops smoke screens (the smoke system's, Source 0): a burning cell not under a screen that lasts past its
//    next look pops one round itself, at most the rules' smoke max at once, so a forest fire works as a smoke screen;
//  - every damage tick the units with the rules' damage tags (infantry) standing in a burning cell lose their share of max HP;
//    they, and the vehicles to which fire is a wall (one parked where it caught), go to the nearest cell not burning (within
//    fleeCells) unless already on their way to one.
// Rolls never draw from the world's generator: each is an integer hash of the match seed, the cell and the tick (FireRoll), so
// fire never shifts another system's rolls. The burning list (sorted by cell), the burnt list (in burn-out order), the batch not
// yet applied and the blast-thinned cells are game state, hashed; the terrain's bits and the events are not.
public sealed partial class World
{
    private const int RerouteEvery = 10; // ticks between passes sending routes round newly burning cells

    private readonly List<Fire> _fires = [];          // burning, ascending by cell index (row, then column)
    private readonly List<BurntCell> _burntCells = [];// burnt out, in burn-out order (tick, then cell index)
    private readonly List<Cell> _thinned = [];        // blasted cells that lost a forest level, in the order they did
    private readonly List<FireEvent> _fireEvents = [];
    private readonly List<(int Cell, bool BurntOut)> _fireApply = []; // lit and burnt out since the last terrain tick, in order
    private readonly List<(int Cell, int SpreadPercent)> _spreadTo = []; // scratch: the cells spreading lights this tick
    private readonly List<(bool Trunk, long D2, int Cell)> _igniteAt = []; // scratch: a blast's candidate cells
    private int _cooled;          // the burnt cells (a prefix of _burntCells) whose embers went cold: a function of the tick
    private bool _newBurning;     // cells caught fire since the last reroute pass (their box below)
    private int _nbx0 = int.MaxValue, _nby0 = int.MaxValue, _nbx1 = int.MinValue, _nby1 = int.MinValue;
    private FireRules? _fireRules; // the content's fire rules, on a map with terrain; null: nothing burns
    private TagSet _fireTags;      // the units fire hurts (fire.damage.tags)
    private ulong _fireSalt;       // the match seed, for the fire's rolls

    /// <summary>The cells burning now, ascending by row then column (draw flames; a trunk there burns).</summary>
    public IReadOnlyList<Fire> Fires => _fires;

    /// <summary>
    /// The cells that burnt out, in the order they did (by <see cref="BurntCell.EndedAt"/>, then row and column): embers until
    /// the fire rules' ember ticks after, then ash (<see cref="FireAt"/>). A trunk still standing there is a black snag.
    /// </summary>
    public IReadOnlyList<BurntCell> BurntCells => _burntCells;

    /// <summary>The cells whose tree a blast blew down and that lost one forest level (sight opens there), in the order they did.</summary>
    public IReadOnlyList<Cell> BlastedCells => _thinned;

    /// <summary>This tick's fire changes (cells catching fire, burning out, going cold), for renderers. Not game state.</summary>
    public IReadOnlyList<FireEvent> FireEvents => _fireEvents;

    /// <summary>The fire rules in play (the content's, on a map with terrain), or null: nothing burns.</summary>
    public FireRules? FireRulesInPlay => _fireRules;

    /// <summary>What fire did to a cell: none, burning, burnt (embers) or ash. None off the map or without terrain.</summary>
    public FireState FireAt(Cell c)
    {
        if (Ground is not { } g)
        {
            return FireState.None;
        }
        if (g.Burning(c))
        {
            return FireState.Burning;
        }
        if (!g.Burnt(c))
        {
            return FireState.None;
        }
        for (var i = _cooled; i < _burntCells.Count; i++)
        {
            if (_burntCells[i].Cell == c)
            {
                return FireState.Burnt; // still glowing (the cells not yet cold are the list's tail)
            }
        }
        return FireState.Ash;
    }

    /// <summary>The fire burning in a cell (since when, how long), or null when it is not burning.</summary>
    public Fire? FireOf(Cell c)
    {
        if (Ground is not { } g || !g.Burning(c))
        {
            return null;
        }
        var k = FindFire(CellIndex(c));
        return k >= 0 ? _fires[k] : null;
    }

    /// <summary>Whether a black snag stands in a cell: a tree trunk still standing where the forest burnt (or burns).</summary>
    public bool IsSnag(Cell c) => Ground is { } g && g.Burnt(c) && g.HasTrunk(c);

    // FindFire is the position of the fire in cell index i in the burning list, or the bitwise complement of where it would go.
    private int FindFire(int i)
    {
        int lo = 0, hi = _fires.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var v = CellIndex(_fires[mid].Cell);
            if (v == i)
            {
                return mid;
            }
            if (v < i)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ~lo;
    }

    // ResolveFire takes the content's fire rules for a map with terrain (World.AttachTerrain).
    private void ResolveFire()
    {
        _fireRules = Content.Fire;
        _fireTags = _fireRules is { } r ? Content.TagsOf([.. r.DamageTags]) : default;
        _fireSalt = Match.Setup.Seed;
    }

    // FireRoll is an integer hash of the match seed and three values, from 0 to 999 (per mille): the same on every machine.
    private uint FireRoll(long a, long b, long c)
    {
        unchecked
        {
            var h = _fireSalt ^ 0xF14E_5EED_0000_0000UL;
            h ^= (ulong)a * 0x9E3779B97F4A7C15UL;
            h = (h ^ (h >> 30)) * 0xBF58476D1CE4E5B9UL;
            h ^= (ulong)b * 0xC2B2AE3D27D4EB4FUL;
            h = (h ^ (h >> 27)) * 0x94D049BB133111EBUL;
            h ^= (ulong)c * 0x165667B19E3779F9UL;
            h = (h ^ (h >> 31)) * 0xBF58476D1CE4E5B9UL;
            h ^= h >> 29;
            return (uint)(h % 1000);
        }
    }

    /// <summary>
    /// Sets the forest round a blast at <paramref name="at"/> of warhead w (its weapon's splash radius splash) on fire as its fire
    /// block says (<see cref="WarheadFire"/>; none: nothing): the forest cells within its radius (a cell's trunk point, or its
    /// centre without a trunk), the cells with a standing trunk first, then the nearest, then by row and column; at most the
    /// rolled count, and never past the rules' cap on cells burning. The blast's shooter (the unit hurting this tick) lit them.
    /// Explode calls it for every splash after the trees are blown down (so a bomb lights trees round its crater), and so do the
    /// detonations without splash.
    /// </summary>
    internal void IgniteByBlast(Vec at, Warhead w, Fixed splash)
    {
        if (w.Fire is not { } wf || _fireRules is not { } rules || Ground is not { } g || _fires.Count >= rules.MaxBurning)
        {
            return;
        }
        var r = wf.RadiusFor(splash);
        if (r <= 0)
        {
            return;
        }
        // A trunk stands up to MaxOffsetRaw off its cell's centre: the cells whose point can be within r.
        int cx0 = Math.Max((int)((at.X.Raw - r) >> Fixed.Shift) - 1, 0), cx1 = Math.Min((int)((at.X.Raw + r) >> Fixed.Shift) + 1, g.Width - 1);
        int cy0 = Math.Max((int)((at.Y.Raw - r) >> Fixed.Shift) - 1, 0), cy1 = Math.Min((int)((at.Y.Raw + r) >> Fixed.Shift) + 1, g.Height - 1);
        _igniteAt.Clear();
        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                var c = new Cell(cx, cy);
                if (!g.Burnable(c) || !Grid.Passable(c))
                {
                    continue;
                }
                var trunk = g.HasTrunk(c);
                var p = trunk ? TerrainMap.TrunkPoint(c) : c.Center();
                long dx = p.X.Raw - at.X.Raw, dy = p.Y.Raw - at.Y.Raw;
                var d2 = dx * dx + dy * dy;
                if (d2 <= r * r)
                {
                    _igniteAt.Add((trunk, d2, cy * g.Width + cx));
                }
            }
        }
        if (_igniteAt.Count == 0)
        {
            return;
        }
        _igniteAt.Sort(static (a, b) => a.Trunk != b.Trunk ? (a.Trunk ? -1 : 1) : a.D2 != b.D2 ? a.D2.CompareTo(b.D2) : a.Cell.CompareTo(b.Cell));
        var n = wf.CountMin == WarheadFire.Every ? _igniteAt.Count
            : wf.CountMin + (int)(FireRoll(at.X.Raw, at.Y.Raw, Tick) % (uint)(wf.CountMax - wf.CountMin + 1));
        var by = _hurtBy?.Id ?? 0;
        for (var k = 0; k < _igniteAt.Count && n > 0 && _fires.Count < rules.MaxBurning; k++)
        {
            var i = _igniteAt[k].Cell;
            if (Ignite(g, rules, new Cell(i % g.Width, i / g.Width), wf.SpreadPercent, by))
            {
                n--;
            }
        }
    }

    /// <summary>
    /// Sets forest cell c on fire, spreading at spreadPercent of the rules' chances (0: it does not spread), as if a blast of unit
    /// by lit it; reports whether it caught (it can burn and the rules' cap allows). For scripted demos (the sandbox's
    /// <see cref="CommandKind.SandboxIgnite"/>), tests and maps that start burning.
    /// </summary>
    internal bool IgniteCell(Cell c, int spreadPercent, uint by = 0) =>
        _fireRules is { } rules && Ground is { } g && _fires.Count < rules.MaxBurning && Ignite(g, rules, c, spreadPercent, by);

    // Ignite sets cell c on fire (it must be burnable; the cap is the caller's): its length rolled, the terrain's burning bit and
    // costs, the event, a smoke screen if none covers it, and the box of new fires for the reroute pass.
    private bool Ignite(TerrainMap g, FireRules rules, Cell c, int spreadPercent, uint by)
    {
        if (!g.Burnable(c) || !Grid.Passable(c))
        {
            return false;
        }
        var i = CellIndex(c);
        var span = rules.BurnTicksMax - rules.BurnTicksMin + 1;
        var length = rules.BurnTicksMin + (int)(((ulong)FireRoll(i, Tick, 0x1E57) * 1000 + FireRoll(i, Tick, 0x1E58)) % (ulong)span);
        var f = new Fire(c, Tick, length, spreadPercent, by);
        _fires.Insert(~FindFire(i), f);
        g.SetBurning(c, true);
        _fireApply.Add((i, false)); // its costs at the next terrain tick
        _fireEvents.Add(new FireEvent(FireEventKind.Ignited, c, length, by, spreadPercent > 0, g.HasTrunk(c)));
        SmokeOver(rules, c);
        return true;
    }

    // SmokeOver pops a fire smoke screen round burning cell c unless a screen lasting past its next look covers its centre, or
    // the rules' most fire screens stand already.
    private void SmokeOver(FireRules rules, Cell c)
    {
        if (rules.SmokeMax == 0 || rules.SmokeRadius.Raw <= 0)
        {
            return;
        }
        var p = c.Center();
        var fireSmokes = 0;
        foreach (var s in _smokes)
        {
            if (s.Until > Tick + (uint)rules.SpreadEveryTicks && Within(s.At, p, s.Radius))
            {
                return;
            }
            fireSmokes += s.Fire ? 1 : 0;
        }
        if (fireSmokes < rules.SmokeMax)
        {
            PopFireSmoke(p, rules.SmokeRadius, rules.SmokeTicks);
        }
    }

    // RunFire is the fire step, right after the tree step (see the top of this file).
    private void RunFire()
    {
        if (_fireRules is not { } rules || Ground is not { } g ||
            (_fires.Count == 0 && _cooled == _burntCells.Count && _fireApply.Count == 0 && !_newBurning))
        {
            return;
        }
        BurnOut(g);
        Cool(rules);
        Spread(g, rules);
        if (_fireApply.Count > 0 && Tick % (uint)rules.TerrainEveryTicks == 0)
        {
            ApplyFire(g);
        }
        if (_fires.Count > 0 && Tick % (uint)rules.DamageEveryTicks == 0)
        {
            Burn(g, rules);
        }
        if (_newBurning && Tick % RerouteEvery == 0)
        {
            RerouteAroundFire(g);
        }
    }

    // BurnOut puts out the fires whose time is up, in cell order: each cell burnt (one forest level down, embers).
    private void BurnOut(TerrainMap g)
    {
        var any = false;
        foreach (var f in _fires)
        {
            any |= f.Ends <= Tick;
        }
        if (!any)
        {
            return;
        }
        var kept = 0;
        for (var k = 0; k < _fires.Count; k++)
        {
            var f = _fires[k];
            if (f.Ends > Tick)
            {
                _fires[kept++] = f;
                continue;
            }
            g.SetBurning(f.Cell, false);
            _fireApply.Add((CellIndex(f.Cell), true)); // its forest and costs at the next terrain tick
            _burntCells.Add(new BurntCell(f.Cell, Tick));
            _fireEvents.Add(new FireEvent(FireEventKind.BurntOut, f.Cell, 0, f.By, false, g.HasTrunk(f.Cell)));
        }
        _fires.RemoveRange(kept, _fires.Count - kept);
    }

    // Cool turns the burnt cells whose embers ran their time to ash (they are the oldest: the list is in burn-out order).
    private void Cool(FireRules rules)
    {
        while (_cooled < _burntCells.Count && _burntCells[_cooled].EndedAt + (uint)rules.EmberTicks <= Tick)
        {
            var b = _burntCells[_cooled++];
            _fireEvents.Add(new FireEvent(FireEventKind.Cooled, b.Cell, 0, 0, false, Ground!.HasTrunk(b.Cell)));
        }
    }

    // Spread: every burning cell whose look is this tick (its cell index plus the tick, modulo spreadEveryTicks) keeps its smoke
    // up and, when it spreads, rolls for each of its 8 neighbours (north first, clockwise) that can burn; the cells lit are set
    // on fire after the pass, in that order, up to the rules' caps.
    private void Spread(TerrainMap g, FireRules rules)
    {
        _spreadTo.Clear();
        var every = (uint)rules.SpreadEveryTicks;
        foreach (var f in _fires)
        {
            var i = CellIndex(f.Cell);
            if (((uint)i + Tick) % every != 0)
            {
                continue;
            }
            SmokeOver(rules, f.Cell);
            if (f.SpreadPercent == 0)
            {
                continue;
            }
            for (var d = 0; d < 8; d++)
            {
                var (dx, dy) = TreeTrunks.Step(d);
                var n = new Cell(f.Cell.X + dx, f.Cell.Y + dy);
                if (!g.Burnable(n))
                {
                    continue;
                }
                var chance = (long)rules.SpreadPerMille[g.Forest(n)] * f.SpreadPercent / 100;
                if ((d & 1) != 0)
                {
                    chance = chance * rules.DiagonalPercent / 100; // a corner
                }
                if (chance > 0 && FireRoll(i, d, Tick) < chance)
                {
                    _spreadTo.Add((CellIndex(n), 100)); // what spreading lights spreads normally
                }
            }
        }
        var lit = 0;
        foreach (var (i, pct) in _spreadTo)
        {
            if (lit >= rules.MaxIgnitionsPerTick || _fires.Count >= rules.MaxBurning)
            {
                break;
            }
            if (Ignite(g, rules, new Cell(i % g.Width, i / g.Width), pct, 0))
            {
                lit++;
            }
        }
    }

    // ApplyFire hands the terrain the fires lit and burnt out since the last terrain tick, in the order they were: the cells
    // lit become walls or dearer to the classes, the cells burnt out lose them and a forest level. Routing and movement see a
    // tick's batch at once (a group re-plans once for it, not once a cell), sight the burnt cells.
    private void ApplyFire(TerrainMap g)
    {
        foreach (var (i, burntOut) in _fireApply)
        {
            var c = new Cell(i % g.Width, i / g.Width);
            g.ApplyFire(c, burntOut);
            if (!burntOut)
            {
                (_nbx0, _nby0, _nbx1, _nby1) = (Math.Min(_nbx0, c.X), Math.Min(_nby0, c.Y), Math.Max(_nbx1, c.X), Math.Max(_nby1, c.Y));
                _newBurning = true;
            }
        }
        _fireApply.Clear();
    }

    // Burn hurts every unit with the fire tags standing in a burning cell (on the ground, not inside anything) by the rules'
    // percent of its max HP, in ID order; it and every vehicle to whose class fire is a wall (one parked in a cell that caught)
    // walk or drive to the nearest cell not burning, unless already on their way to one.
    private void Burn(TerrainMap g, FireRules rules)
    {
        foreach (var u in _units)
        {
            if (!u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Def.IsStructure || u.Def.Layer != Layers.Ground || InAir(u) ||
                !g.Burning(Cell.Of(u.Pos)))
            {
                continue;
            }
            var hurt = u.Def.Tags.Matches(_fireTags);
            if (!hurt && (g.Rules.ClassOf(u.Def) is not (>= 0 and var cls) || g.Rules.Classes[cls].FireCost != 0))
            {
                continue; // neither hurt nor walled in
            }
            if (hurt && rules.DamagePercent > 0)
            {
                u.HP = Math.Max(0, u.HP - Math.Max(1, u.Def.MaxHp * rules.DamagePercent / 100));
            }
            if (u.HP <= 0 || (u.Order.Kind == OrderKind.Move && !g.Burning(u.Order.Cell)))
            {
                continue; // dead, or already on its way out
            }
            if (Escape(g, rules, u) is { } to)
            {
                u.Order = new Order(OrderKind.Move, to, 0, 0);
                u.ClearPath();
                RequestRoute(u, to);
            }
        }
    }

    // Escape is the nearest cell to u that is not burning and that u's class can stand on, a square ring at a time out to the
    // rules' flee cells (the nearest centre in the first ring with one, then by row and column), or null.
    private Cell? Escape(TerrainMap g, FireRules rules, Unit u)
    {
        var cls = g.Rules.ClassOf(u.Def);
        var at = Cell.Of(u.Pos);
        for (var d = 1; d <= rules.FleeCells; d++)
        {
            Cell? best = null;
            long bestD2 = long.MaxValue;
            for (var y = at.Y - d; y <= at.Y + d; y++)
            {
                for (var x = at.X - d; x <= at.X + d; x++)
                {
                    if (Math.Max(Math.Abs(x - at.X), Math.Abs(y - at.Y)) != d)
                    {
                        continue; // inside the ring
                    }
                    var c = new Cell(x, y);
                    if (!Grid.Contains(c) || !Grid.Passable(c) || g.Burning(c) || (cls >= 0 && !g.Passable(cls, c)))
                    {
                        continue;
                    }
                    var d2 = (c.Center() - u.Pos).LenSq();
                    if (d2 < bestD2)
                    {
                        (best, bestD2) = (c, d2);
                    }
                }
            }
            if (best is not null)
            {
                return best;
            }
        }
        return null;
    }

    // RerouteAroundFire sends every ground unit on its own route whose way ahead crosses a cell that caught fire since the last
    // pass (and to whose class a burning cell is a wall or dearer) on a new route; a group's flows re-plan once routing sees it.
    private void RerouteAroundFire(TerrainMap g)
    {
        int bx0 = _nbx0, by0 = _nby0, bx1 = _nbx1, by1 = _nby1;
        (_nbx0, _nby0, _nbx1, _nby1, _newBurning) = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue, false);
        var rules = g.Rules;
        foreach (var u in _units)
        {
            if (!u.Alive || u.Def.Layer != Layers.Ground || u.Def.IsStructure || u.Inside != 0 || !u.Moving || u.PathPending ||
                u.Mode != RouteMode.Direct || rules.ClassOf(u.Def) is not (>= 0 and var cls) || rules.Classes[cls].FireCost == 100)
            {
                continue;
            }
            var from = Cell.Of(u.Pos);
            for (var i = u.PathIdx; i < u.Path.Count; i++)
            {
                var to = u.Path[i];
                if (Math.Max(from.X, to.X) >= bx0 && Math.Min(from.X, to.X) <= bx1 && Math.Max(from.Y, to.Y) >= by0 &&
                    Math.Min(from.Y, to.Y) <= by1 && CrossesFire(g, from, to, bx0, by0, bx1, by1))
                {
                    RequestRoute(u, u.Dest);
                    break;
                }
                from = to;
            }
        }
    }

    // CrossesFire reports whether the straight way between two cells meets a burning cell inside the box of new fires.
    private static bool CrossesFire(TerrainMap g, Cell a, Cell b, int bx0, int by0, int bx1, int by1)
    {
        int dx = b.X - a.X, dy = b.Y - a.Y;
        var n = Math.Max(Math.Abs(dx), Math.Abs(dy));
        for (var s = 0; s <= n; s++)
        {
            var c = n == 0 ? a : new Cell(a.X + RoundDiv(dx * s, n), a.Y + RoundDiv(dy * s, n));
            if (c.X >= bx0 && c.X <= bx1 && c.Y >= by0 && c.Y <= by1 && g.Burning(c))
            {
                return true;
            }
        }
        return false;
    }

    // ThinBlasted drops the forest of a cell whose tree a blast blew down one level, once (the terrain rules' trees.blastThins).
    private void ThinBlasted(TerrainMap g, Cell stump)
    {
        if (g.Thin(stump))
        {
            _thinned.Add(stump);
        }
    }

    // HashFire mixes in the cells burning, burnt and thinned by blasts: only once one was, so games without fire hash as before.
    private void HashFire(ref WordHash h)
    {
        if (_fires.Count == 0 && _burntCells.Count == 0 && _thinned.Count == 0 && _fireApply.Count == 0)
        {
            return;
        }
        h.Put(0xF14E);
        h.PutSigned(_fireApply.Count);
        foreach (var (i, burntOut) in _fireApply)
        {
            h.PutSigned(i);
            h.Put(burntOut ? 1UL : 0);
        }
        h.PutSigned(_fires.Count);
        foreach (var f in _fires)
        {
            h.PutSigned(f.Cell.X);
            h.PutSigned(f.Cell.Y);
            h.Put(f.Start);
            h.PutSigned(f.Length);
            h.PutSigned(f.SpreadPercent);
            h.Put(f.By);
        }
        h.PutSigned(_burntCells.Count);
        foreach (var b in _burntCells)
        {
            h.PutSigned(b.Cell.X);
            h.PutSigned(b.Cell.Y);
            h.Put(b.EndedAt);
        }
        h.PutSigned(_thinned.Count);
        foreach (var c in _thinned)
        {
            h.PutSigned(c.X);
            h.PutSigned(c.Y);
        }
    }
}
