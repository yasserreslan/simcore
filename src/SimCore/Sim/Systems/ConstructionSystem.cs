using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The construction system: workers build sites (structures placed with a Build command). Each
// tick, workers with a Build order standing within their builder range of the site's footprint
// add work, visited in ID order: on one site the 1st, 2nd, 3rd ... builder adds the archetype's
// helper percent of its rate (100, 50, 0 for the depot). A site's HP rises with its work (damage
// taken stays taken); at BuildTime x 100 it is finished: functional, its builders idle. A site
// nobody works on simply waits. Sites are placed by CommandSystem (Build), cancelled there too.
public sealed partial class World
{
    // Scratch: the work added to each site this tick, sorted by site ID (no dictionaries).
    private readonly List<(uint Site, int Builders, long Work)> _siteWork = [];

    private void RunConstruction()
    {
        _siteWork.Clear();
        foreach (var u in _units)
        {
            u.Building = false;
            if (u.Order.Kind != OrderKind.Build || u.Def.Builder is not { } b || !u.Alive || u.Inside != 0 || u.Garrison != 0)
            {
                continue;
            }
            var site = Unit(u.Order.Target);
            if (site is null || site.IsBuilt || site.Owner != u.Owner || site.Def.Archetype.Construction is not { } cs ||
                !Within(u.Pos, AimPoint(u.Pos, site), b.Range))
            {
                continue;
            }
            var i = SiteSlot(site.Id);
            var (_, k, work) = _siteWork[i];
            var pct = cs.Helpers[Math.Min(k, cs.Helpers.Count - 1)];
            _siteWork[i] = (site.Id, k + 1, work + (long)b.Rate * pct / 100);
            u.Building = pct > 0;
        }
        foreach (var (id, _, work) in _siteWork)
        {
            var s = Unit(id)!;
            var total = (long)s.Def.Archetype.BuildTime * 100;
            var before = SiteHp(s, s.Work);
            s.Work = (int)Math.Min(total, s.Work + work);
            s.HP = Math.Min(s.Def.MaxHp, s.HP + SiteHp(s, s.Work) - before);
            if (s.Work >= total)
            {
                Finish(s);
            }
        }
    }

    // SiteSlot finds (or inserts, keeping the order) the scratch entry of a site.
    private int SiteSlot(uint site)
    {
        int lo = 0, hi = _siteWork.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_siteWork[mid].Site < site)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        if (lo == _siteWork.Count || _siteWork[lo].Site != site)
        {
            _siteWork.Insert(lo, (site, 0, 0));
        }
        return lo;
    }

    // SiteHp is a site's HP for an amount of work, before damage: StartHp percent of max HP at
    // nothing, rising linearly to max HP when finished.
    private static int SiteHp(Unit s, long work)
    {
        var a = s.Def.Archetype;
        long max = s.Def.MaxHp, start = Math.Max(1, max * a.Construction!.StartHp / 100);
        return (int)(start + (max - start) * work / ((long)a.BuildTime * 100));
    }

    // Finish completes a site: functional now (a worker-built supplier with its constructed stock),
    // its builders idle.
    private void Finish(Unit s)
    {
        s.Construction = ConstructionState.Complete;
        s.Work = 0;
        s.Stock = s.Def.Supply?.ConstructedStock ?? 0;
        foreach (var u in _units)
        {
            if (u.Order.Kind == OrderKind.Build && u.Order.Target == s.Id)
            {
                u.Idle();
            }
        }
    }

    /// <summary>
    /// A construction site's stage, 0 to 4 (by its work and the archetype's stage thresholds);
    /// 4 for a finished structure.
    /// </summary>
    public static int ConstructionStage(Unit s)
    {
        if (s.IsBuilt || s.Def.Archetype.Construction is not { } cs)
        {
            return 4;
        }
        var pct = (long)s.Work * 100 / ((long)s.Def.Archetype.BuildTime * 100);
        var stage = 0;
        while (stage < cs.Stages.Count && pct >= cs.Stages[stage])
        {
            stage++;
        }
        return stage;
    }

    /// <summary>A site's progress in percent (0 to 100); 100 for anything finished.</summary>
    public static int ConstructionPercent(Unit s) =>
        s.IsBuilt ? 100 : (int)((long)s.Work * 100 / ((long)s.Def.Archetype.BuildTime * 100));

    /// <summary>
    /// Whether builders could place a site of def with c its top-left cell, turned by facing: a
    /// buildable structure (construction, not locked), a quarter turn only for a square footprint,
    /// every footprint cell on the map and passable, no other structure on it, no ground unit in it
    /// but the builders, and on terrain every cell Flat or Gentle for the builders.
    /// </summary>
    public bool CanPlace(UnitDef def, Cell c, int facing, IReadOnlyList<Unit> builders)
    {
        var a = def.Archetype;
        if (a.Construction is null || a.Locked || !a.IsStructure || builders.Count == 0 ||
            facing < 0 || facing >= FixedPoint.Facing.Count || facing % (FixedPoint.Facing.Count / 4) != 0 ||
            (facing % (FixedPoint.Facing.Count / 2) != 0 && a.FootprintW != a.FootprintH))
        {
            return false;
        }
        int w = a.FootprintW, h = a.FootprintH;
        var k = Ground is { } g0 ? g0.Rules.ClassOf(builders[0].Def) : -1;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var cell = new Cell(c.X + x, c.Y + y);
                if (!Grid.Contains(cell) || !Grid.Passable(cell))
                {
                    return false;
                }
                if (Ground is { } g && k >= 0 && g.Slope(k, cell) is not (SlopeClass.Flat or SlopeClass.Gentle))
                {
                    return false;
                }
            }
        }
        foreach (var u in _units)
        {
            if (u.Def.IsStructure)
            {
                var o = FootprintOrigin(u);
                if (o.X < c.X + w && c.X < o.X + u.Def.Archetype.FootprintW && o.Y < c.Y + h && c.Y < o.Y + u.Def.Archetype.FootprintH)
                {
                    return false;
                }
                continue;
            }
            var at = Cell.Of(u.Pos);
            if (u.Def.Layer == Layers.Ground && u.Inside == 0 && u.Garrison == 0 && at.X >= c.X && at.X < c.X + w && at.Y >= c.Y && at.Y < c.Y + h &&
                !Contains(builders, u))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Contains(IReadOnlyList<Unit> list, Unit u)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == u)
            {
                return true;
            }
        }
        return false;
    }

    // PlaceSite creates a site of def for its player: 10% HP (the archetype's StartHp), no stock,
    // not functional. On terrain its ground is flattened to a pad at the footprint's mean height,
    // easing back over the archetype's PadBlend cells.
    private Unit PlaceSite(UnitDef def, Cell c, int facing)
    {
        var a = def.Archetype;
        if (Ground is { } g)
        {
            long sum = 0, n = 0;
            for (var y = 0; y <= a.FootprintH; y++)
            {
                for (var x = 0; x <= a.FootprintW; x++)
                {
                    sum += g.Corner(c.X + x, c.Y + y).Raw;
                    n++;
                }
            }
            g.SetPad(c, a.FootprintW, a.FootprintH, Fixed.FromRaw(sum / n), a.Construction!.PadBlend);
        }
        var s = Add(def, FootprintCenter(def, c), facing);
        s.Construction = ConstructionState.Building;
        s.Work = 0;
        s.HP = Math.Max(1, def.MaxHp * a.Construction!.StartHp / 100);
        s.Stock = 0;
        return s;
    }

    // Charge and Refund: a site's cost and a cancelled site's refund. No-ops until the game has
    // an economy (Player.Credits is hashed but nothing earns or spends it yet).
    private static void Charge(byte player, UnitDef def)
    {
        _ = (player, def);
    }

    private static void Refund(byte player, UnitDef def, int percent)
    {
        _ = (player, def, percent);
    }

    // Build places a site with the command's workers as its builders (see CanPlace).
    private void Build(Command c)
    {
        var builders = Builders(c);
        var def = Match.Players[c.Player - 1].Find(Content, c.Unit ?? "");
        if (def is null || !CanPlace(def, c.Cell, c.Facing, builders))
        {
            return;
        }
        var site = PlaceSite(def, c.Cell, c.Facing);
        Charge(c.Player, def);
        foreach (var u in builders)
        {
            Assign(u, new Order(OrderKind.Build, default, site.Id, 0));
        }
    }

    // Resume sends the command's workers to go on building one of the player's sites.
    private void Resume(Command c)
    {
        if (Unit(c.Target) is not { } site || site.IsBuilt || site.Owner != c.Player)
        {
            return;
        }
        foreach (var u in Builders(c))
        {
            Assign(u, new Order(OrderKind.Build, default, site.Id, 0));
        }
    }

    // CancelBuild removes one of the player's sites (no rubble, nobody killed) and refunds part
    // of its cost; its builders idle.
    private void CancelBuild(Command c)
    {
        if (Unit(c.Target) is not { } site || site.IsBuilt || site.Owner != c.Player || site.Def.Archetype.Construction is not { } cs)
        {
            return;
        }
        Refund(c.Player, site.Def, cs.Refund);
        foreach (var u in _units)
        {
            if (u.Order.Kind == OrderKind.Build && u.Order.Target == site.Id)
            {
                u.Idle();
            }
        }
        _units.Remove(site);
    }

    // Builders lists the command's units the player may order that can build, in its order.
    private List<Unit> Builders(Command c)
    {
        var list = new List<Unit>();
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (Commandable(u, c.Player) && u!.Def.Builder is not null && u.Garrison == 0 && u.Inside == 0)
            {
                list.Add(u);
            }
        }
        return list;
    }
}
