namespace SimCore.Sim;

/// <summary>
/// Reusable A* buffers. A generation counter marks which entries belong to the
/// current search, so nothing is cleared between searches. It is scratch
/// space, not game state.
/// </summary>
internal sealed class Pathfinder
{
    // Step costs: 10 straight, 14 diagonal (≈10√2).
    internal const int CostStraight = 10;
    internal const int CostDiagonal = 14;

    // Neighbors are visited in this fixed order so ties always break the same way.
    internal static readonly (int Dx, int Dy, int Cost)[] Neighbors =
    [
        (0, -1, CostStraight), (1, -1, CostDiagonal), (1, 0, CostStraight), (1, 1, CostDiagonal),
        (0, 1, CostStraight), (-1, 1, CostDiagonal), (-1, 0, CostStraight), (-1, -1, CostDiagonal),
    ];

    internal int[] G = [];
    internal int[] Parent = [];
    internal uint[] Seen = [];
    internal uint[] Closed = [];
    internal uint Gen;

    // The open set orders by f, then h, then cell index: a total order, and no
    // two entries are equal, so any correct priority queue pops in the same
    // order and the search is deterministic.
    internal readonly PriorityQueue<OpenNode, OpenNode> Open = new(OpenNodeOrder.Instance);

    internal void Reset(int n)
    {
        if (G.Length != n)
        {
            G = new int[n];
            Parent = new int[n];
            Seen = new uint[n];
            Closed = new uint[n];
            Gen = 0;
        }
        Gen++;
        if (Gen == 0) // wrapped: old marks could look current
        {
            Array.Clear(Seen);
            Array.Clear(Closed);
            Gen = 1;
        }
        Open.Clear();
    }

    internal void Push(OpenNode node) => Open.Enqueue(node, node);

    internal readonly record struct OpenNode(int F, int H, int Idx);

    private sealed class OpenNodeOrder : IComparer<OpenNode>
    {
        public static readonly OpenNodeOrder Instance = new();

        public int Compare(OpenNode a, OpenNode b)
        {
            if (a.F != b.F)
            {
                return a.F < b.F ? -1 : 1;
            }
            if (a.H != b.H)
            {
                return a.H < b.H ? -1 : 1;
            }
            return a.Idx.CompareTo(b.Idx);
        }
    }
}

public sealed partial class World
{
    private static int Octile(Cell a, Cell b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return Pathfinder.CostStraight * (dx + dy) + (Pathfinder.CostDiagonal - 2 * Pathfinder.CostStraight) * Math.Min(dx, dy);
    }

    /// <summary>
    /// Returns the cells from (not including) from to to, using 8-way A*
    /// without cutting corners. A blocked destination is replaced by the
    /// nearest passable cell; an unreachable one by the closest cell the
    /// search reached.
    /// </summary>
    internal Cell[] FindPath(Cell from, Cell to)
    {
        var g = Grid;
        if (!g.Passable(to))
        {
            if (!g.TrySpiral(to, 0, out var c))
            {
                return [];
            }
            to = c;
        }
        if (from == to || !g.Contains(from))
        {
            return [];
        }

        var pf = _pf;
        pf.Reset(g.Cells.Length);
        int start = g.Index(from), goal = g.Index(to);
        pf.G[start] = 0;
        pf.Parent[start] = -1;
        pf.Seen[start] = pf.Gen;
        var h0 = Octile(from, to);
        pf.Push(new Pathfinder.OpenNode(h0, h0, start));

        int best = start, bestH = h0;
        while (pf.Open.TryDequeue(out var cur, out _))
        {
            if (pf.Closed[cur.Idx] == pf.Gen)
            {
                continue;
            }
            pf.Closed[cur.Idx] = pf.Gen;
            if (cur.Idx == goal)
            {
                best = goal;
                break;
            }
            if (cur.H < bestH)
            {
                best = cur.Idx;
                bestH = cur.H;
            }
            var cc = g.CellAt(cur.Idx);
            foreach (var (dx, dy, cost) in Pathfinder.Neighbors)
            {
                var n = new Cell(cc.X + dx, cc.Y + dy);
                if (!g.Passable(n))
                {
                    continue;
                }
                if (dx != 0 && dy != 0 &&
                    (!g.Passable(new Cell(cc.X + dx, cc.Y)) || !g.Passable(new Cell(cc.X, cc.Y + dy))))
                {
                    continue;
                }
                var ni = g.Index(n);
                if (pf.Closed[ni] == pf.Gen)
                {
                    continue;
                }
                var ng = pf.G[cur.Idx] + cost;
                if (pf.Seen[ni] == pf.Gen && ng >= pf.G[ni])
                {
                    continue;
                }
                pf.G[ni] = ng;
                pf.Parent[ni] = cur.Idx;
                pf.Seen[ni] = pf.Gen;
                var h = Octile(n, to);
                pf.Push(new Pathfinder.OpenNode(ng + h, h, ni));
            }
        }

        var path = new List<Cell>();
        for (var i = best; i != start; i = pf.Parent[i])
        {
            path.Add(g.CellAt(i));
        }
        path.Reverse();
        return [.. path];
    }

    private void SetPath(Unit u, Cell dest)
    {
        PathRequests++;
        u.SetPath(FindPath(Cell.Of(u.Pos), dest));
    }
}
