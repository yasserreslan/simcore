using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Trees as obstacles (docs/design.md, "Trees as obstacles"; the owner, 2026-10-25). A map's forest has standing trunks
// (TerrainMap.HasTrunk: worked out from the forest, TreeTrunks), each a wall to the classes that avoid them (jeeps, light
// trucks) and pushed over by the classes that push (tanks, heavy trucks); infantry ignore them. The terrain folds the trunks
// into its per-class cost grids, so routing (its clearance widths decide who fits between which trees) and movement (a wall
// is any cell its class can't enter) need no special case.
//
// The tree step runs right after ground movement, in the same phase:
//  - fallen trunks whose time ran out are gone (their cells clear);
//  - every pusher that drove this tick and whose body overlaps a standing trunk's cell ahead of it (the cell's nearest point
//    in front of its heading, or under its centre) presses on it: bodies in ID order, so a trunk's pusher is the lowest ID
//    on it. A push goes on while contact lasts, one tick at a time; at its pusher's push ticks the tree falls the way the
//    pusher faces (8 ways). Contact lost, the push starts over. A pusher in contact drives no faster than its push speed
//    next tick (MovementSystem.cs) and its push counts as progress;
//  - a fall: the stump's cell opens for good and the trunk lies along its direction from the next cell, the rules' length
//    (shorter where the map ends or a building, rock or water stops it), blocking every vehicle but the pushers that roll
//    over fallen trunks for the rules' ticks. Nobody under it is hurt; a vehicle in its cells is pushed out like from any
//    wall. Ground units on their own route whose path crosses it look for another (a group re-plans its flows once routing
//    sees the change; routing refreshes only the cells round it);
//  - building on the ground (construction, bridges, airfield pieces, tunnels) clears its trees and any fallen trunk across it
//    (ClearTrees).
// The felled stumps, the fallen trunks and the pushes are game state, hashed; the events are not.
public sealed partial class World
{
    private readonly List<Cell> _stumps = [];               // felled trunks' cells, ascending by index (y, then x); they stay stumps
    private readonly List<FallenTrunk> _fallenTrunks = [];  // lying, ascending by stump index
    private List<TreePush> _treePushes = [];                 // in progress, ascending by cell index
    private List<TreePush> _treePushScratch = [];
    private readonly List<TreeEvent> _treeEvents = [];
    private readonly List<FallenTrunk> _newFallen = [];     // fallen since the last reroute pass (not game state)
    private readonly List<(int Cell, uint Pusher, int Cls, int Dir, Vec From)> _contacts = [];

    /// <summary>
    /// The cells of every tree felled so far (docs/design.md, "Trees as obstacles"), ascending by row then column: the client
    /// may draw a stump there. Open ground for everyone; a stump stays one (its fallen trunk lies in <see cref="FallenTrunks"/>
    /// while it lasts).
    /// </summary>
    public IReadOnlyList<Cell> Stumps => _stumps;

    /// <summary>
    /// The fallen trunks lying on the map, ascending by their stump's row then column: each blocks the cells it covers
    /// (<see cref="FallenTrunk.CellAt"/>) for vehicles until <see cref="FallenTrunk.ExpiresAt"/>. For the client to draw the logs.
    /// </summary>
    public IReadOnlyList<FallenTrunk> FallenTrunks => _fallenTrunks;

    /// <summary>The pushes in progress (a pusher pressing on a standing trunk), ascending by the trunk's row then column.</summary>
    public IReadOnlyList<TreePush> TreePushes => _treePushes;

    /// <summary>This tick's tree changes (pushes starting, trees falling, fallen trunks gone), for renderers. Not game state.</summary>
    public IReadOnlyList<TreeEvent> TreeEvents => _treeEvents;

    // RunTrees is the tree step, right after ground movement (see the top of this file).
    private void RunTrees()
    {
        if (Ground is not { } g || g.Rules.Trees is null)
        {
            return;
        }
        ExpireFallen(g);
        if (g.HasTrunks)
        {
            Push(g);
        }
        RerouteAroundFallen(g);
    }

    // ExpireFallen takes away the fallen trunks whose time ran out.
    private void ExpireFallen(TerrainMap g)
    {
        var any = false;
        foreach (var f in _fallenTrunks)
        {
            any |= f.ExpiresAt <= Tick;
        }
        if (!any)
        {
            return;
        }
        var gone = new List<FallenTrunk>();
        _fallenTrunks.RemoveAll(f =>
        {
            if (f.ExpiresAt > Tick)
            {
                return false;
            }
            gone.Add(f);
            return true;
        });
        foreach (var f in gone)
        {
            _treeEvents.Add(new TreeEvent(TreeEventKind.Expired, f.Stump, f.Dir, f.Length, f.By, f.Cause, default));
        }
        Relay(g, gone);
    }

    // Relay lifts the fallen marks of trunks taken away and lays those of the trunks still lying again (where two crossed).
    private void Relay(TerrainMap g, List<FallenTrunk> gone)
    {
        foreach (var f in gone)
        {
            for (var k = 1; k <= f.Length; k++)
            {
                g.SetFallen(f.CellAt(k), false);
            }
        }
        foreach (var f in _fallenTrunks)
        {
            for (var k = 1; k <= f.Length; k++)
            {
                g.SetFallen(f.CellAt(k), true); // a no-op where it is still marked
            }
        }
    }

    // Push finds the pushers pressing on standing trunks after this tick's movement, carries the pushes on and fells the
    // trees whose push is done.
    private void Push(TerrainMap g)
    {
        var m = _mv;
        var rules = g.Rules;
        _contacts.Clear();
        for (var i = 0; i < m.N; i++)
        {
            var u = m.U[i];
            var cls = m.Cls[i];
            var pushes = cls >= 0 && rules.Classes[cls].Trunks == TrunkMode.Push && (m.Flags[i] & MoveScratch.Moving) != 0;
            u.PushContact = pushes && Contacts(g, u, m.R[i], cls);
        }
        if (_contacts.Count == 0 && _treePushes.Count == 0)
        {
            return;
        }
        // By trunk, then pusher: the lowest ID on a trunk pushes it.
        _contacts.Sort((a, b) => a.Cell != b.Cell ? a.Cell.CompareTo(b.Cell) : a.Pusher.CompareTo(b.Pusher));
        var next = _treePushScratch;
        next.Clear();
        var j = 0;
        var falls = new List<(Cell Cell, uint Pusher, int Dir, Vec From)>();
        for (var k = 0; k < _contacts.Count; k++)
        {
            var (cell, pusher, cls, dir, from) = _contacts[k];
            if (k > 0 && _contacts[k - 1].Cell == cell)
            {
                continue; // a higher ID on the same trunk
            }
            var at = new Cell(cell % g.Width, cell / g.Width);
            while (j < _treePushes.Count && CellIndex(_treePushes[j].Cell) < cell)
            {
                j++;
            }
            var ticks = j < _treePushes.Count && _treePushes[j].Cell == at ? _treePushes[j].Ticks + 1 : 1;
            if (ticks == 1)
            {
                _treeEvents.Add(new TreeEvent(TreeEventKind.PushStarted, at, dir, 0, pusher, TreeFallCause.Pushed, from));
            }
            if (ticks >= rules.Classes[cls].PushTicks)
            {
                falls.Add((at, pusher, dir, from));
                continue;
            }
            next.Add(new TreePush(at, pusher, ticks));
        }
        (_treePushes, _treePushScratch) = (next, _treePushes);
        foreach (var (cell, pusher, dir, from) in falls)
        {
            FellTree(cell, dir, pusher, TreeFallCause.Pushed, from);
        }
    }

    // Contacts records the standing trunks pusher u (radius r, class cls) presses on, and reports whether there are any: the
    // trunk cells its body overlaps whose nearest point lies ahead of its heading (or under its centre).
    private bool Contacts(TerrainMap g, Unit u, long r, int cls)
    {
        long px = u.Pos.X.Raw, py = u.Pos.Y.Raw;
        var h = u.Heading;
        if (h.X.Raw == 0 && h.Y.Raw == 0)
        {
            h = Facing.Dir(u.Facing);
        }
        int cx0 = Math.Max((int)((px - r) >> Fixed.Shift), 0), cx1 = Math.Min((int)((px + r) >> Fixed.Shift), g.Width - 1);
        int cy0 = Math.Max((int)((py - r) >> Fixed.Shift), 0), cy1 = Math.Min((int)((py + r) >> Fixed.Shift), g.Height - 1);
        var any = false;
        var dir = TreeTrunks.DirOf(u.Facing);
        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                var i = cy * g.Width + cx;
                if (!g.TrunkAt(i))
                {
                    continue;
                }
                long bx = (long)cx << Fixed.Shift, by = (long)cy << Fixed.Shift;
                var dx = Math.Clamp(px, bx, bx + Fixed.OneRaw) - px;
                var dy = Math.Clamp(py, by, by + Fixed.OneRaw) - py;
                var d2 = dx * dx + dy * dy;
                if (d2 >= r * r || (d2 != 0 && dx * h.X.Raw + dy * h.Y.Raw <= 0))
                {
                    continue; // clear of it, or beside or behind it
                }
                _contacts.Add((i, u.Id, cls, dir, u.Pos));
                any = true;
            }
        }
        return any;
    }

    /// <summary>
    /// Fells the standing trunk at stump the way dir points (0 north, clockwise; <see cref="TreeTrunks.Step"/>): its cell
    /// opens for good and the trunk lies along dir from the next cell for the rules' length (stopped by the map's edge, a
    /// building, rock or water) and time. Reports whether a trunk stood there.
    /// </summary>
    internal bool FellTree(Cell stump, int dir, uint by, TreeFallCause cause, Vec from)
    {
        if (Ground is not { } g || g.Rules.Trees is not { } trees || !g.FellTrunk(stump))
        {
            return false;
        }
        var si = CellIndex(stump);
        var at = _stumps.BinarySearch(stump, CellOrder.Instance);
        if (at < 0)
        {
            _stumps.Insert(~at, stump);
        }
        var len = 0;
        var (dx, dy) = TreeTrunks.Step(dir);
        for (var k = 1; k <= trees.FallenLengthCells; k++)
        {
            var c = new Cell(stump.X + dx * k, stump.Y + dy * k);
            if (!Grid.Contains(c) || !Grid.Passable(c) || g.OpenWater(c))
            {
                break; // the map's edge, a building or rock, water: it stops there
            }
            len++;
        }
        var f = new FallenTrunk(stump, dir & 7, len, Tick + (uint)trees.FallenTicks, by, cause);
        var pos = FallenIndex(si);
        if (pos < _fallenTrunks.Count && _fallenTrunks[pos].Stump == stump)
        {
            var old = _fallenTrunks[pos]; // a tree grown again on a stump whose trunk still lies (a script's forest): the old one goes
            _fallenTrunks.RemoveAt(pos);
            Relay(g, [old]);
        }
        _fallenTrunks.Insert(pos, f);
        for (var k = 1; k <= len; k++)
        {
            g.SetFallen(f.CellAt(k), true);
        }
        _treeEvents.Add(new TreeEvent(TreeEventKind.Fell, stump, f.Dir, len, by, cause, from));
        if (len > 0)
        {
            _newFallen.Add(f);
        }
        return true;
    }

    // FallenIndex is where a fallen trunk with this stump index goes in the list (the first at or after it).
    private int FallenIndex(int stumpIndex)
    {
        int lo = 0, hi = _fallenTrunks.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (CellIndex(_fallenTrunks[mid].Stump) < stumpIndex)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private int CellIndex(Cell c) => c.Y * Grid.Width + c.X;

    /// <summary>
    /// Clears a cell's trees for something built on it: its forest (and so its standing trunk) and every fallen trunk lying
    /// across it, taken away whole (a <see cref="TreeEventKind.Cleared"/> event each). Construction sites, bridges, airfield
    /// pieces and tunnel entrances clear their ground with it. Off the map or on a map without terrain: nothing.
    /// </summary>
    public void ClearTrees(Cell c)
    {
        if (Ground is not { } g || !Grid.Contains(c))
        {
            return;
        }
        if (g.Forest(c) != 0)
        {
            g.SetForest(c, 0);
        }
        if (_fallenTrunks.Count == 0 || !g.Fallen(c))
        {
            return;
        }
        var gone = new List<FallenTrunk>();
        _fallenTrunks.RemoveAll(f =>
        {
            if (!f.Covers(c))
            {
                return false;
            }
            gone.Add(f);
            return true;
        });
        foreach (var f in gone)
        {
            _treeEvents.Add(new TreeEvent(TreeEventKind.Cleared, f.Stump, f.Dir, f.Length, f.By, f.Cause, default));
        }
        Relay(g, gone);
    }

    // RerouteAroundFallen sends every ground unit on its own route whose way ahead crosses a trunk fallen since the last pass
    // (and which can't cross fallen trunks) on a new route. Group members follow their group's flows, re-planned once
    // routing sees the change.
    private void RerouteAroundFallen(TerrainMap g)
    {
        if (_newFallen.Count == 0)
        {
            return;
        }
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = int.MinValue, by1 = int.MinValue;
        foreach (var f in _newFallen)
        {
            var end = f.CellAt(f.Length);
            bx0 = Math.Min(bx0, Math.Min(f.Stump.X, end.X));
            by0 = Math.Min(by0, Math.Min(f.Stump.Y, end.Y));
            bx1 = Math.Max(bx1, Math.Max(f.Stump.X, end.X));
            by1 = Math.Max(by1, Math.Max(f.Stump.Y, end.Y));
        }
        var rules = g.Rules;
        foreach (var u in _units)
        {
            if (!u.Alive || u.Def.Layer != Layers.Ground || u.Def.IsStructure || u.Inside != 0 || !u.Moving || u.PathPending ||
                u.Mode != RouteMode.Direct || rules.ClassOf(u.Def) is not (>= 0 and var cls) || rules.Classes[cls].EntersFallen)
            {
                continue;
            }
            var from = Cell.Of(u.Pos);
            for (var i = u.PathIdx; i < u.Path.Count; i++)
            {
                var to = u.Path[i];
                if (Math.Max(from.X, to.X) >= bx0 && Math.Min(from.X, to.X) <= bx1 && Math.Max(from.Y, to.Y) >= by0 &&
                    Math.Min(from.Y, to.Y) <= by1 && Crosses(from, to))
                {
                    RequestRoute(u, u.Dest);
                    break;
                }
                from = to;
            }
        }
        _newFallen.Clear();
    }

    // Crosses reports whether the straight way between two cells (the cells a line between their centres passes) meets a
    // trunk fallen since the last reroute pass.
    private bool Crosses(Cell a, Cell b)
    {
        int dx = b.X - a.X, dy = b.Y - a.Y;
        var n = Math.Max(Math.Abs(dx), Math.Abs(dy));
        for (var s = 0; s <= n; s++)
        {
            var c = n == 0 ? a : new Cell(a.X + RoundDiv(dx * s, n), a.Y + RoundDiv(dy * s, n));
            foreach (var f in _newFallen)
            {
                if (f.Covers(c))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // HashTrees mixes in the felled stumps, the fallen trunks and the pushes: only once a tree fell or a push began, so games
    // without either hash as before.
    private void HashTrees(ref WordHash h)
    {
        if (_stumps.Count == 0 && _fallenTrunks.Count == 0 && _treePushes.Count == 0)
        {
            return;
        }
        h.Put(0x7EE5);
        h.PutSigned(_stumps.Count);
        foreach (var c in _stumps)
        {
            h.PutSigned(c.X);
            h.PutSigned(c.Y);
        }
        h.PutSigned(_fallenTrunks.Count);
        foreach (var f in _fallenTrunks)
        {
            h.PutSigned(f.Stump.X);
            h.PutSigned(f.Stump.Y);
            h.PutSigned(f.Dir);
            h.PutSigned(f.Length);
            h.Put(f.ExpiresAt);
            h.Put(f.By);
            h.PutByte((byte)f.Cause);
        }
        h.PutSigned(_treePushes.Count);
        foreach (var p in _treePushes)
        {
            h.PutSigned(p.Cell.X);
            h.PutSigned(p.Cell.Y);
            h.Put(p.Pusher);
            h.PutSigned(p.Ticks);
        }
    }

    // CellOrder sorts cells by row, then column (their index on the map).
    private sealed class CellOrder : IComparer<Cell>
    {
        internal static readonly CellOrder Instance = new();

        public int Compare(Cell a, Cell b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
    }
}
