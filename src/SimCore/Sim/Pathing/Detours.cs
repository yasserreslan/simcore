using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

public sealed partial class World
{
    private readonly LocalSearch _detourSearch = new();
    private readonly List<int> _detourPath = [];
    private byte[] _detourPenalty = [];
    private int _detoursLeft;

    // Built only for a stuck unit, from current bodies in ID order. This overlay and the
    // per-tick budget are scratch; only the resulting path/end and retry tick persist (hashed).
    private bool TryDetour(Router r, Unit u)
    {
        _detoursLeft--;
        u.DetourRetryAt = Tick + (uint)Content.UnitAi.DetourRetryTicks;
        var k = r.ClassOf(u.Def);
        if (k < 0 || u.OutOf != 0) { return false; }
        var here = Cell.Of(u.Pos);
        var radius = Content.UnitAi.DetourRadiusCells;
        int x0 = Math.Max(0, here.X - radius), y0 = Math.Max(0, here.Y - radius);
        int x1 = Math.Min(Grid.Width - 1, here.X + radius), y1 = Math.Min(Grid.Height - 1, here.Y + radius);
        var bw = x1 - x0 + 1;
        var n = bw * (y1 - y0 + 1);
        if (_detourPenalty.Length < n) { _detourPenalty = new byte[n]; }
        Array.Clear(_detourPenalty, 0, n);
        var found = false;
        foreach (var v in _units)
        {
            if (v == u || !v.Alive || v.Inside != 0 || v.Garrison != 0 || v.Def.IsStructure || v.Def.Layer != Layers.Ground ||
                (v.CanAct && v.Deploy == DeployState.Stowed && v.Intent.Desired != default && !v.Intent.Arrived && !v.Intent.Stuck))
            {
                continue;
            }
            var body = v.Def.Archetype.Radius.Raw;
            var clearance = body + u.Def.Archetype.Radius.Raw;
            var reach = clearance + u.Def.Archetype.Radius.Raw;
            int vx0 = Math.Max(x0, (int)((v.Pos.X.Raw - reach) >> Fixed.Shift));
            int vx1 = Math.Min(x1, (int)((v.Pos.X.Raw + reach) >> Fixed.Shift));
            int vy0 = Math.Max(y0, (int)((v.Pos.Y.Raw - reach) >> Fixed.Shift));
            int vy1 = Math.Min(y1, (int)((v.Pos.Y.Raw + reach) >> Fixed.Shift));
            for (var y = vy0; y <= vy1; y++)
            for (var x = vx0; x <= vx1; x++)
            {
                var d = (new Cell(x, y).Center() - v.Pos).LenSq();
                if (d >= reach * reach) { continue; }
                found = true;
                var at = (y - y0) * bw + x - x0;
                _detourPenalty[at] = d < clearance * clearance ? byte.MaxValue :
                    (byte)Math.Max(_detourPenalty[at], Content.UnitAi.DetourCost);
            }
        }
        if (!found) { return false; }
        // Rejoin the farthest already-planned waypoint inside the window. The tail, route
        // legs, flow mode, group and formation slot are retained exactly as planned.
        var join = -1;
        for (var i = u.PathIdx; i < u.Path.Count; i++)
        {
            var c = u.Path[i];
            if (c.X < x0 || c.Y < y0 || c.X > x1 || c.Y > y1) { break; }
            if (_detourPenalty[(c.Y - y0) * bw + c.X - x0] != byte.MaxValue) { join = i; }
        }
        if (join < 0) { return false; }
        var start = r.Index(here);
        _detourPenalty[(here.Y - y0) * bw + here.X - x0] = 0; // let an overlapping start escape
        _detourPath.Clear();
        if (!_detourSearch.AStar(r.Nav, k, start, r.Index(u.Path[join]), (x0, y0, x1, y1), _detourPath,
                out _, _detourPenalty.AsSpan(0, n)) || _detourPath.Count == 0)
        {
            return false;
        }
        var path = new Cell[_detourPath.Count + u.Path.Count - join - 1];
        for (var i = 0; i < _detourPath.Count; i++) { path[i] = r.CellAt(_detourPath[i]); }
        for (var i = join + 1; i < u.Path.Count; i++) { path[_detourPath.Count + i - join - 1] = u.Path[i]; }
        u.SetPath(path);
        u.DetourEnd = _detourPath.Count;
        u.Intent = u.Intent with { Stuck = false, Arrived = false };
        u.StallTicks = 0;
        return true;
    }
}
