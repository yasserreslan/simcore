namespace SimCore.Sim;

internal sealed partial class Regions
{
    // A second level over sector-local connected regions, derived from the already-built
    // region links. Its edges certify connectivity, not the exact portal travel cost. Search
    // counts cardinal crossings (one each, a lower bound); the router checks retained portal
    // pairs against these regions, and local A* refines every resulting leg.
    // The router uses it only on maps above the small-map compatibility boundary.
    private int[] _coarseStart = [];
    private int[] _coarseTo = [];
    private int[] _coarseSector = [];
    private int[] _coarseG = [];
    private int[] _coarseParent = [];
    private bool[] _coarseClosed = [];
    private readonly MinHeap _coarseOpen = new();

    internal long CoarseWork { get; private set; }

    private long CoarseBytes => 4L * (_coarseStart.Length + _coarseTo.Length + _coarseSector.Length +
        _coarseG.Length + _coarseParent.Length) + _coarseClosed.Length;

    internal bool SectorRoute(int from, int goal, List<int> sectors, List<int>? regionNodes = null)
    {
        CoarseWork = 0;
        sectors.Clear();
        regionNodes?.Clear();
        if (Get(from) == 0 || Get(from) != Get(goal)) { return false; }
        if (_coarseStart.Length == 0) { BuildCoarse(); }
        var a = Node(from);
        var b = Node(goal);
        var bs = _coarseSector[b];
        Array.Fill(_coarseG, int.MaxValue);
        Array.Fill(_coarseParent, -1);
        Array.Clear(_coarseClosed);
        _coarseOpen.Clear();
        _coarseG[a] = 0;
        Push(a);
        while (_coarseOpen.Count > 0)
        {
            var (_, u) = _coarseOpen.Pop();
            if (_coarseClosed[u]) { continue; }
            _coarseClosed[u] = true;
            CoarseWork += 1 + _coarseStart[u + 1] - _coarseStart[u];
            if (u == b)
            {
                for (var n = b; n >= 0; n = _coarseParent[n])
                {
                    sectors.Add(_coarseSector[n]);
                    regionNodes?.Add(n);
                }
                sectors.Reverse();
                regionNodes?.Reverse();
                return true;
            }
            for (var i = _coarseStart[u]; i < _coarseStart[u + 1]; i++)
            {
                var v = _coarseTo[i];
                const int step = 1; // one crossing: a lower bound, ignoring travel inside sectors
                var g = _coarseG[u] + step;
                if (_coarseClosed[v] || g >= _coarseG[v]) { continue; }
                _coarseG[v] = g;
                _coarseParent[v] = u;
                Push(v);
            }
        }
        return false;

        int Node(int c)
        {
            int x = c % _nav.W, y = c / _nav.W, s = y / S * _sx + x / S;
            return _base[s] + Local(c, x, y, s) - 1;
        }
        void Push(int n)
        {
            var s = _coarseSector[n];
            var h = Math.Abs(s % _sx - bs % _sx) + Math.Abs(s / _sx - bs / _sx);
            _coarseOpen.Push(((long)(_coarseG[n] + h) << 32) | (uint)h, n);
        }
    }

    internal int RegionNode(int cell)
    {
        if (Get(cell) == 0) { return -1; }
        int x = cell % _nav.W, y = cell / _nav.W, sector = y / S * _sx + x / S;
        return _base[sector] + Local(cell, x, y, sector) - 1;
    }

    private void BuildCoarse()
    {
        var n = _base[^1] + _count[^1];
        _coarseStart = new int[n + 1];
        _coarseSector = new int[n];
        _coarseG = new int[n];
        _coarseParent = new int[n];
        _coarseClosed = new bool[n];
        for (var s = 0; s < _count.Length; s++)
        {
            Array.Fill(_coarseSector, s, _base[s], _count[s]);
            var links = _links[s];
            for (var i = 0; i < _linkLen[s]; i += 3)
            {
                if (s % _sx != links[i + 1] % _sx && s / _sx != links[i + 1] / _sx) { continue; }
                _coarseStart[_base[s] + links[i]]++;
                _coarseStart[_base[links[i + 1]] + links[i + 2]]++;
            }
        }
        for (var i = 1; i <= n; i++) { _coarseStart[i] += _coarseStart[i - 1]; }
        _coarseTo = new int[_coarseStart[n]];
        // Parent doubles as the write cursor only during construction.
        Array.Copy(_coarseStart, _coarseParent, n);
        for (var s = 0; s < _count.Length; s++)
        {
            var links = _links[s];
            for (var i = 0; i < _linkLen[s]; i += 3)
            {
                if (s % _sx != links[i + 1] % _sx && s / _sx != links[i + 1] / _sx) { continue; }
                int a = _base[s] + links[i] - 1, b = _base[links[i + 1]] + links[i + 2] - 1;
                _coarseTo[_coarseParent[a]++] = b;
                _coarseTo[_coarseParent[b]++] = a;
            }
        }
    }
}
