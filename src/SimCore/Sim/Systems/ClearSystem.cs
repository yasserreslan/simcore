using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Clear up (docs/design.md, "Clear up"; decisions 963 to 971; the owner, 2026-11-07: "it cleans up an area from trees basically and it destroys buildings
// into ruins ... it keep cleaning until an area is empty basically from trees and buildings and stuff it can remvoe, not bridges tho").
// A bulldozer with a clear block takes the order for a rectangle of cells, the area form of the game's area orders. Each tick, in ID
// order, right after construction:
//  - the blade: within its reach, inside the area, undergrowth with no trunk left is scraped away (forest level 0: sight, cost and
//    cover open up) and fallen trunks that have lain long enough are taken away whole;
//  - its job: the nearest of the standing trees, ready fallen trunks and buildings in the area (by distance, then tree, trunk,
//    building, then cell or ID); a trunk that only just fell waits until nothing else is left, undergrowth comes last.
//    Out of reach it drives there. In reach, a tree: after the tree ticks every standing tree in reach falls, away from it. A building:
//    the first tick puts its occupants out at its doors; then its HP runs down at its max HP over its clear ticks, so the
//    damage stages show and repairs or shells change how long it lasts. At 0 HP the existing rules take over: occupants still inside
//    are hurt (RunCollapse), it leaves the world, its footprint stays blocked (the ruin), airfield pieces and tunnels stay as rubble.
//  - a target it gets no nearer to is skipped for good, and it carries on. Nothing left: it idles.
// Own buildings are skipped (a garrisoned building belongs to whoever is inside, so that can change while it works); bridges,
// untargetable and invulnerable pieces are never targets; an enemy building counts only while the player can see it, so the order
// never gives away what fog hides. Everything here is game state: the job in World.Hash, the scraped cells in HashClearedForest.
public sealed partial class World
{
    private readonly List<Cell> _clearedForest = []; // cells the blade scraped, in the order scraped: hashed (the terrain digest reads them as they were)

    /// <summary>Every forest cell a bulldozer's blade scraped so far, in order (the cell's forest is 0 now: <see cref="TerrainMap.Flattened"/>).</summary>
    public IReadOnlyList<Cell> ClearedForest => _clearedForest;

    /// <summary>
    /// The ticks spec's bulldozer takes to bring building b down from full HP: by its max HP and its footprint, within the block's limits.
    /// </summary>
    public static int ClearTicks(ClearSpec spec, Unit b) =>
        Math.Clamp((int)((long)b.Def.MaxHp * spec.TicksPerHundredHp / 100 + (long)FootW(b) * FootH(b) * spec.TicksPerCell),
            spec.MinBuildingTicks, spec.MaxBuildingTicks);

    // ClearArea gives the command's bulldozers the area: its corners in order, clipped to the map.
    private void ClearArea(Command c)
    {
        if (c.Cell2 is not { } to)
        {
            return;
        }
        int x0 = Math.Min(c.Cell.X, to.X), x1 = Math.Max(c.Cell.X, to.X);
        int y0 = Math.Min(c.Cell.Y, to.Y), y1 = Math.Max(c.Cell.Y, to.Y);
        var cx0 = Math.Max(x0, 0);
        var cy0 = Math.Max(y0, 0);
        var cx1 = Math.Min(x1, Grid.Width - 1);
        var cy1 = Math.Min(y1, Grid.Height - 1);
        if (cx0 > cx1 || cy0 > cy1)
        {
            return; // off the map
        }
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || u!.Def.Archetype.Clear is not { } spec || u.Garrison != 0 || u.Inside != 0 ||
                x1 - x0 + 1 > spec.MaxSide || y1 - y0 + 1 > spec.MaxSide)
            {
                continue;
            }
            Assign(u, new Order(OrderKind.ClearArea, new Cell(cx0, cy0), 0, 0));
            u.ClearState = new ClearJob(new Cell(cx0, cy0), new Cell(cx1, cy1));
        }
    }

    private void RunClearing()
    {
        foreach (var u in _units)
        {
            if (u.Order.Kind == OrderKind.ClearArea && u.Alive)
            {
                Clearing(u);
            }
        }
    }

    // Clearing runs one bulldozer's tick: its blade, its job, the drive to it (see the top of this file).
    private void Clearing(Unit u)
    {
        if (u.ClearState is not { } job || u.Def.Archetype.Clear is not { } spec)
        {
            u.Idle();
            return;
        }
        job.Working = false;
        if ((Tick + u.Id) % 5 == 0)
        {
            Blade(u, job, spec);
        }
        if ((job.Kind == ClearJobKind.None || !JobStands(u, job)) && !NextClearJob(u, job, spec))
        {
            u.Idle(); // nothing left
            return;
        }
        var goal = JobPoint(u, job);
        if (!Within(u.Pos, goal, spec.Reach))
        {
            Approach(u, goal, spec.Reach);
            var d2 = Dist2(u.Pos, goal);
            if (d2 < job.Best)
            {
                job.Best = d2;
                job.Stuck = 0;
            }
            else if (!u.PathPending && (job.Stuck += u.Moving ? 1 : 4) >= spec.StuckTicks)
            {
                job.Skip(JobKey(job, Grid.Width)); // it can't get there: on with the rest, never a stuck loop
                job.Kind = ClearJobKind.None;
                u.ClearPath();
            }
            return;
        }
        u.ClearPath();
        switch (job.Kind)
        {
            case ClearJobKind.Tree:
                job.Working = true;
                if (job.Work == 0)
                {
                    _treeEvents.Add(new TreeEvent(TreeEventKind.PushStarted, job.Cell, TreeTrunks.DirOf(u.Facing), 0, u.Id, TreeFallCause.Pushed, u.Pos));
                }
                if (++job.Work >= spec.TreeTicks)
                {
                    FellInReach(u, job, spec);
                    job.Kind = ClearJobKind.None;
                }
                break;
            case ClearJobKind.Building:
                job.Working = true;
                WearDown(u, job, spec);
                break;
            default:
                break; // a fallen trunk or undergrowth in reach: the blade's (a trunk that only just fell, once it has lain long enough)
        }
    }

    // Blade scrapes the undergrowth and takes away the ready fallen trunks in u's reach, inside the area.
    private void Blade(Unit u, ClearJob job, ClearSpec spec)
    {
        if (Ground is not { } g)
        {
            return;
        }
        var reach = spec.Reach.Raw;
        var cx0 = Math.Max((int)((u.Pos.X.Raw - reach) >> Fixed.Shift), job.From.X);
        var cx1 = Math.Min((int)((u.Pos.X.Raw + reach) >> Fixed.Shift), job.To.X);
        var cy0 = Math.Max((int)((u.Pos.Y.Raw - reach) >> Fixed.Shift), job.From.Y);
        var cy1 = Math.Min((int)((u.Pos.Y.Raw + reach) >> Fixed.Shift), job.To.Y);
        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                var c = new Cell(cx, cy);
                if (g.Forest(c) != 0 && !g.TrunkAt(cy * g.Width + cx) && Within(u.Pos, c.Center(), spec.Reach))
                {
                    ScrapeForest(g, c);
                }
            }
        }
        if (_fallenTrunks.Count == 0)
        {
            return;
        }
        SortTrees();
        List<FallenTrunk>? gone = null;
        foreach (var f in _fallenTrunks)
        {
            if (TrunkReady(f, spec, g) && TrunkInReach(u, job, f, spec))
            {
                (gone ??= []).Add(f);
            }
        }
        if (gone is null)
        {
            return;
        }
        foreach (var f in gone)
        {
            _fallenTrunks.Remove(f);
            _treeEvents.Add(new TreeEvent(TreeEventKind.Cleared, f.Stump, f.Dir, f.Length, f.By, f.Cause, default));
        }
        Relay(g, gone);
    }

    private void ScrapeForest(TerrainMap g, Cell c)
    {
        if (g.Flatten(c, 0))
        {
            _clearedForest.Add(c);
        }
    }

    // TrunkReady: it has lain the block's time (so its fall has landed).
    private bool TrunkReady(FallenTrunk f, ClearSpec spec, TerrainMap g) =>
        f.ExpiresAt <= Tick + (uint)g.Rules.Trees!.FallenTicks - (uint)spec.TrunkAge;

    // TrunkInReach: a cell of the trunk (its stump counted) inside the area and within reach.
    private bool TrunkInReach(Unit u, ClearJob job, FallenTrunk f, ClearSpec spec)
    {
        for (var k = 0; k <= f.Length; k++)
        {
            var c = k == 0 ? f.Stump : f.CellAt(k);
            if (job.Holds(c) && Within(u.Pos, c.Center(), spec.Reach))
            {
                return true;
            }
        }
        return false;
    }

    // FellInReach fells every standing tree in u's reach inside the area, in row then column order, each away from it, and
    // scrapes its cell.
    private void FellInReach(Unit u, ClearJob job, ClearSpec spec)
    {
        if (Ground is not { } g)
        {
            return;
        }
        var reach = spec.Reach.Raw + TreeTrunks.MaxOffsetRaw;
        var cx0 = Math.Max((int)((u.Pos.X.Raw - reach) >> Fixed.Shift), job.From.X);
        var cx1 = Math.Min((int)((u.Pos.X.Raw + reach) >> Fixed.Shift), job.To.X);
        var cy0 = Math.Max((int)((u.Pos.Y.Raw - reach) >> Fixed.Shift), job.From.Y);
        var cy1 = Math.Min((int)((u.Pos.Y.Raw + reach) >> Fixed.Shift), job.To.Y);
        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                if (!g.TrunkAt(cy * g.Width + cx))
                {
                    continue;
                }
                var c = new Cell(cx, cy);
                var p = TerrainMap.TrunkPoint(c);
                if (!Within(u.Pos, p, spec.Reach) && c != job.Cell)
                {
                    continue;
                }
                var dir = new Vec(Fixed.FromRaw(p.X.Raw - u.Pos.X.Raw), Fixed.FromRaw(p.Y.Raw - u.Pos.Y.Raw));
                FellTree(c, TreeTrunks.DirOf(dir), u.Id, TreeFallCause.Pushed, u.Pos);
                ScrapeForest(g, c);
            }
        }
    }

    // WearDown runs one tick of work on the job's building.
    private void WearDown(Unit u, ClearJob job, ClearSpec spec)
    {
        var b = Unit(job.Building)!;
        if (job.Work == 0)
        {
            if (b.Def.Archetype.Garrison is not null)
            {
                Evacuate(b, null); // they walk out when it starts on the building; whoever goes back in takes his chances (RunCollapse)
            }
        }
        job.Work++;
        var total = ClearTicks(spec, b);
        job.Carry += b.Def.MaxHp;
        b.HP -= job.Carry / total;
        job.Carry %= total;
        if (b.Owner != 0 && b.Owner != u.Owner)
        {
            RecordHit(b, u);
        }
    }

    // JobStands reports whether the job's target is still there and still fair game.
    private bool JobStands(Unit u, ClearJob job)
    {
        var g = Ground;
        switch (job.Kind)
        {
            case ClearJobKind.Tree:
                return g is not null && g.TrunkAt(job.Cell.Y * g.Width + job.Cell.X);
            case ClearJobKind.Brush:
                return g is not null && g.Forest(job.Cell) != 0;
            case ClearJobKind.Trunk:
                foreach (var f in _fallenTrunks)
                {
                    if (f == job.Trunk)
                    {
                        return true;
                    }
                }
                return false;
            case ClearJobKind.Building:
                return Unit(job.Building) is { } b && Clearable(u, b);
            default:
                return false;
        }
    }

    private Vec JobPoint(Unit u, ClearJob job) => job.Kind switch
    {
        ClearJobKind.Tree => TerrainMap.TrunkPoint(job.Cell),
        ClearJobKind.Building => AimPoint(u.Pos, Unit(job.Building)!),
        _ => job.Cell.Center(),
    };

    // The skip-list key of the job: a building by its ID above every cell index, a trunk's stump apart from a tree's cell.
    private static long JobKey(ClearJob job, int width) => job.Kind switch
    {
        ClearJobKind.Building => (1L << 40) + job.Building,
        ClearJobKind.Trunk => (1L << 36) + (long)job.Trunk.Stump.Y * width + job.Trunk.Stump.X,
        _ => (long)job.Cell.Y * width + job.Cell.X,
    };

    // Clearable: a building u may work on: standing, a real target, not a bridge, not its own side's, and known to its player.
    private static bool Clearable(Unit u, Unit b)
    {
        var a = b.Def.Archetype;
        return b.Def.IsStructure && b.Alive && !b.Rubble && !a.Invulnerable && !a.Untargetable && !a.Pavement && a.Bridge is null &&
            b.Bridge is null && b.Owner != u.Owner && (b.Owner == 0 || IsVisibleTo(b, u.Owner));
    }

    private static bool Overlaps(ClearJob job, Unit b)
    {
        var o = FootprintOrigin(b);
        return o.X <= job.To.X && o.X + FootW(b) - 1 >= job.From.X && o.Y <= job.To.Y && o.Y + FootH(b) - 1 >= job.From.Y;
    }

    private static long Dist2(Vec a, Vec b)
    {
        long dx = a.X.Raw - b.X.Raw, dy = a.Y.Raw - b.Y.Raw;
        return dx * dx + dy * dy;
    }

    // NextClearJob picks u's next job and counts what is left (see the top of this file): the area's cells in row then column order,
    // the fallen trunks by stump, the buildings by ID, the nearest first and the earlier on a tie. Reports whether there is one.
    private bool NextClearJob(Unit u, ClearJob job, ClearSpec spec)
    {
        int trees = 0, logs = 0, buildings = 0, brush = 0;
        var best = long.MaxValue;
        var kind = ClearJobKind.None;
        var cell = default(Cell);
        var trunk = default(FallenTrunk);
        uint building = 0;
        var bestBrush = long.MaxValue;
        var brushCell = default(Cell);
        var bestLater = long.MaxValue; // a trunk that only just fell: when nothing else is left
        var laterCell = default(Cell);
        var laterTrunk = default(FallenTrunk);
        if (Ground is { } g)
        {
            var k = g.Rules.ClassOf(u.Def);
            for (var y = job.From.Y; y <= job.To.Y; y++)
            {
                for (var x = job.From.X; x <= job.To.X; x++)
                {
                    var c = new Cell(x, y);
                    var i = y * g.Width + x;
                    var tree = g.TrunkAt(i);
                    if ((!tree && g.Forest(c) == 0) || !Grid.Passable(c) || (k >= 0 && !g.Passable(k, c)) || job.Skipped(i))
                    {
                        continue; // nothing there, or under a building, on a cliff or given up on
                    }
                    if (tree)
                    {
                        trees++;
                        var d = Dist2(u.Pos, TerrainMap.TrunkPoint(c));
                        if (d < best)
                        {
                            (best, kind, cell) = (d, ClearJobKind.Tree, c);
                        }
                        continue;
                    }
                    brush++;
                    var db = Dist2(u.Pos, c.Center());
                    if (db < bestBrush)
                    {
                        (bestBrush, brushCell) = (db, c);
                    }
                }
            }
            SortTrees();
            foreach (var f in _fallenTrunks)
            {
                var near = long.MaxValue;
                var nearCell = default(Cell);
                for (var s = 0; s <= f.Length; s++)
                {
                    var c = s == 0 ? f.Stump : f.CellAt(s);
                    if (job.Holds(c) && Dist2(u.Pos, c.Center()) is var d && d < near)
                    {
                        (near, nearCell) = (d, c);
                    }
                }
                if (near == long.MaxValue || job.Skipped((1L << 36) + (long)f.Stump.Y * g.Width + f.Stump.X))
                {
                    continue;
                }
                logs++;
                if (!TrunkReady(f, spec, g))
                {
                    if (near < bestLater)
                    {
                        (bestLater, laterCell, laterTrunk) = (near, nearCell, f);
                    }
                }
                else if (near < best)
                {
                    (best, kind, cell, trunk) = (near, ClearJobKind.Trunk, nearCell, f);
                }
            }
        }
        foreach (var b in _units)
        {
            if (!b.Def.IsStructure || !Clearable(u, b) || !Overlaps(job, b) || job.Skipped((1L << 40) + b.Id))
            {
                continue;
            }
            buildings++;
            var d = Dist2(u.Pos, AimPoint(u.Pos, b));
            if (d < best)
            {
                (best, kind, building) = (d, ClearJobKind.Building, b.Id);
            }
        }
        if (kind == ClearJobKind.None && bestLater != long.MaxValue)
        {
            (kind, cell, trunk) = (ClearJobKind.Trunk, laterCell, laterTrunk);
        }
        if (kind == ClearJobKind.None && bestBrush != long.MaxValue)
        {
            (kind, cell) = (ClearJobKind.Brush, brushCell);
        }
        (job.Trees, job.Logs, job.Buildings, job.Brush) = (trees, logs, buildings, brush);
        job.Start(kind, cell, trunk, building);
        u.RepathAt = Tick;
        return kind != ClearJobKind.None;
    }

    // HashClearedForest mixes in the cells the blade scraped: only once one was, so games without it hash as before.
    private void HashClearedForest(ref WordHash h)
    {
        if (_clearedForest.Count == 0)
        {
            return;
        }
        h.Put(0xC1EA);
        h.PutSigned(_clearedForest.Count);
        foreach (var c in _clearedForest)
        {
            h.PutSigned(c.X);
            h.PutSigned(c.Y);
        }
    }
}
