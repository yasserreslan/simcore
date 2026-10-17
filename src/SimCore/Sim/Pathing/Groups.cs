using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>A route request waiting in the world's queue, served in (Tick, Unit) order.</summary>
/// <param name="Tick">The tick it was made.</param>
/// <param name="Unit">The unit (for a group: its lowest member ID when queued).</param>
/// <param name="Group">The group to plan, or 0 for the unit's own route.</param>
internal readonly record struct PathRequest(uint Tick, uint Unit, uint Group) : IComparable<PathRequest>
{
    public int CompareTo(PathRequest o) =>
        Tick != o.Tick ? Tick.CompareTo(o.Tick) : Unit != o.Unit ? Unit.CompareTo(o.Unit) : Group.CompareTo(o.Group);
}

/// <summary>
/// One group move order: its goal, its members (by ID), and per navigation class a flow (the
/// group's one abstract route, and its flow fields). Game state: hashed by the world.
/// </summary>
internal sealed class RouteGroup(uint id, Cell goal)
{
    internal uint Id { get; } = id;

    internal Cell Goal { get; } = goal;

    internal List<uint> Members { get; } = [];

    internal List<Flow> Flows { get; } = [];

    // Whether a plan request for it is in the queue.
    internal bool Queued { get; set; }

    internal Flow? FlowFor(int k)
    {
        foreach (var f in Flows)
        {
            if (f.K == k)
            {
                return f;
            }
        }
        return null;
    }
}

/// <summary>
/// Formation slots for a group move: a box facing the move direction (from the members' centre
/// to the goal) and centred on the goal, infantry ranks in front and vehicle ranks behind. Each
/// kind fills its block rank by rank: the members furthest along the move direction (the first
/// to arrive) take the front rank, and so on back (ties: lateral position, then ID); within a
/// rank they take the columns left to right by lateral position.
/// </summary>
internal static class Formation
{
    internal static void Assign(Router r, IReadOnlyList<Unit> units, IReadOnlyList<int> classes, Cell goal, int[] slots, Vec[] offsets)
    {
        var n = units.Count;
        long cx = 0, cy = 0;
        foreach (var u in units)
        {
            cx += u.Pos.X.Raw;
            cy += u.Pos.Y.Raw;
        }
        var centre = new Vec(Fixed.FromRaw(cx / n), Fixed.FromRaw(cy / n));
        var g = goal.Center();
        var f = (g - centre).WithLen(Fixed.One);
        if (f.LenSq() == 0)
        {
            f = new Vec(Fixed.Zero, -Fixed.One);
        }
        var l = new Vec(-f.Y, f.X);

        // Blocks: kind 0 infantry (width 1), kind 1 vehicles; spacing by the widest member.
        var count = new int[2];
        var spacing = new int[2] { 1, 1 };
        for (var i = 0; i < n; i++)
        {
            var kind = r.Nav.Width(classes[i]) > 1 ? 1 : 0;
            count[kind]++;
            spacing[kind] = Math.Max(spacing[kind], kind == 0 ? 1 : r.Nav.Width(classes[i]) + 1);
        }
        var cols = new int[2];
        var ranks = new int[2];
        for (var kind = 0; kind < 2; kind++)
        {
            var c = count[kind];
            cols[kind] = c <= 5 ? c : Math.Max(5, (int)Fixed.Sqrt(2L * c));
            ranks[kind] = cols[kind] == 0 ? 0 : (c + cols[kind] - 1) / cols[kind];
        }
        // Depths in half cells, front positive: the blocks back to back, centred on the goal.
        var depthI = ranks[0] > 0 ? (ranks[0] - 1) * spacing[0] * 2 : 0;
        var depthV = ranks[1] > 0 ? (ranks[1] - 1) * spacing[1] * 2 : 0;
        var gap = ranks[0] > 0 && ranks[1] > 0 ? spacing[0] + spacing[1] : 0;
        var total = depthI + gap + depthV;
        var front = new int[2] { total / 2, total / 2 - depthI - gap };

        r.BeginMarks();
        for (var kind = 0; kind < 2; kind++)
        {
            if (count[kind] == 0)
            {
                continue;
            }
            var idx = new List<int>();
            for (var i = 0; i < n; i++)
            {
                if ((r.Nav.Width(classes[i]) > 1 ? 1 : 0) == kind)
                {
                    idx.Add(i);
                }
            }
            // The members that will arrive first (furthest along the move direction) take the
            // front rank, and so on back; within a rank, left to right by lateral position.
            idx.Sort((a, b) =>
            {
                long fa = Dot(units[a].Pos - centre, f), fb = Dot(units[b].Pos - centre, f);
                if (fa != fb)
                {
                    return fb.CompareTo(fa);
                }
                long la = Dot(units[a].Pos - centre, l), lb = Dot(units[b].Pos - centre, l);
                return la != lb ? la.CompareTo(lb) : units[a].Id.CompareTo(units[b].Id);
            });
            var cl = cols[kind];
            for (var rank = 0; rank * cl < idx.Count; rank++)
            {
                var row = idx.GetRange(rank * cl, Math.Min(cl, idx.Count - rank * cl));
                row.Sort((a, b) =>
                {
                    long la = Dot(units[a].Pos - centre, l), lb = Dot(units[b].Pos - centre, l);
                    return la != lb ? la.CompareTo(lb) : units[a].Id.CompareTo(units[b].Id);
                });
                var first = (cl - row.Count) / 2; // a short last rank is centred
                for (var j = 0; j < row.Count; j++)
                {
                    var i = row[j];
                    var lat2 = (2 * (first + j) - (cl - 1)) * spacing[kind]; // half cells
                    var dep2 = front[kind] - rank * spacing[kind] * 2;
                    var p = g + Scale(f, dep2) + Scale(l, lat2);
                    var k = classes[i];
                    var cell = r.NearestPassable(k, Cell.Of(p), 6, skipMarked: true);
                    if (cell < 0)
                    {
                        cell = r.NearestPassable(k, goal, 16);
                    }
                    if (cell < 0)
                    {
                        cell = r.Index(new Cell(Math.Clamp(goal.X, 0, r.Nav.W - 1), Math.Clamp(goal.Y, 0, r.Nav.H - 1)));
                    }
                    r.Mark(cell);
                    slots[i] = cell;
                    offsets[i] = r.CellAt(cell).Center() - g;
                }
            }
        }
    }

    private static long Dot(Vec a, Vec b) => a.X.Raw * b.X.Raw + a.Y.Raw * b.Y.Raw;

    // Scale returns v (unit length) times h half cells.
    private static Vec Scale(Vec v, int h) => new(Fixed.FromRaw(v.X.Raw * h / 2), Fixed.FromRaw(v.Y.Raw * h / 2));
}
