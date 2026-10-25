using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class SkirmishAi
{
    internal readonly record struct SeenStructure(uint Id, int Archetype, Cell Cell, uint LastSeen);
    private readonly List<SeenStructure> _memory = []; // sorted by enemy ID, only ever filled from visible enemies
    internal IReadOnlyList<SeenStructure> MemoryForTests => _memory;
    private readonly List<uint> _searching = []; // own wave members, sorted; keep searching when they go idle
    private bool Searching(Unit u) => _searching.BinarySearch(u.Id) >= 0;

    private void TrackWave(List<uint> members)
    {
        foreach (var id in members)
        {
            var at = _searching.BinarySearch(id);
            if (at < 0) { _searching.Insert(~at, id); }
        }
    }

    private Cell[] _probes = [];
    private uint[] _seen = [];
    private uint[] _assigned = []; // probe reservations by OWN unit ID, not knowledge of enemies
    private int _regionWidth;

    /// <summary>Cells per search region; the wave visits a reachable probe near its centre.</summary>
    public int RegionSizeCells { get; init; } = 16;

    /// <summary>Ticks before returning to a region whose probe was seen; unseen probes come first.</summary>
    public uint RescoutEveryTicks { get; init; } = 1200;

    private void UpdateScouting(World w, List<Unit> visibleEnemies)
    {
        // Never look up a remembered ID in World: hidden destruction or movement must not
        // leak into memory. Forget only when its old cell is visible and that ID is absent.
        for (var i = _memory.Count - 1; i >= 0; i--)
        {
            var m = _memory[i];
            if (SeesCell(w, m.Cell) && !visibleEnemies.Any(e => e.Id == m.Id && e.Def.IsStructure && Cell.Of(e.Pos) == m.Cell))
            {
                _memory.RemoveAt(i);
            }
        }
        foreach (var e in visibleEnemies)
        {
            if (!e.Def.IsStructure) { continue; }
            var lo = 0;
            var hi = _memory.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_memory[mid].Id < e.Id) { lo = mid + 1; } else { hi = mid; }
            }
            var seen = new SeenStructure(e.Id, e.Def.Archetype.Index, Cell.Of(e.Pos), w.Tick);
            if (lo < _memory.Count && _memory[lo].Id == e.Id) { _memory[lo] = seen; }
            else { _memory.Insert(lo, seen); }
        }
        var size = Math.Max(1, RegionSizeCells);
        var width = (w.Grid.Width + size - 1) / size;
        var height = (w.Grid.Height + size - 1) / size;
        if (_probes.Length != width * height)
        {
            _regionWidth = width;
            _probes = new Cell[width * height];
            _seen = new uint[_probes.Length];
            _assigned = new uint[_probes.Length];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                _probes[y * width + x] = new Cell(Math.Min(w.Grid.Width - 1, x * size + size / 2),
                    Math.Min(w.Grid.Height - 1, y * size + size / 2));
            }
        }
        for (var i = 0; i < _assigned.Length; i++)
        {
            if (_assigned[i] == 0) { continue; }
            var own = w.Unit(_assigned[i]);
            if (own is null || own.Owner != Player || !own.Alive || !own.CanAct) { _assigned[i] = 0; }
        }
        // Region probes, not an omniscient scan of the enemy. The side's own explored
        // history initializes a late-starting AI; current visible counts timestamp re-visits.
        if (w.FogOf(Player) is { } fog)
        {
            for (var i = 0; i < _probes.Length; i++)
            {
                var c = _probes[i];
                if (fog.Visible(c.X, c.Y)) { _seen[i] = w.Tick + 1; }
                else if (_seen[i] == 0 && fog.Explored(c.X, c.Y)) { _seen[i] = 1; }
            }
        }
        else
        {
            foreach (var u in w.Units)
            {
                if (u.Owner != Player || !u.Alive || u.Inside != 0 || u.Garrison != 0) { continue; }
                var c = Cell.Of(u.Pos);
                if (w.Grid.Contains(c)) { _seen[c.Y / size * _regionWidth + c.X / size] = w.Tick + 1; }
            }
        }
    }

    private bool SeesCell(World w, Cell cell)
    {
        if (w.FogOf(Player) is { } fog) { return fog.Visible(cell.X, cell.Y); }
        // Without a fog grid, still use only the AI's own viewers, never hidden enemy state.
        foreach (var u in w.Units)
        {
            if (u.Owner == Player && u.Alive && u.Inside == 0 && u.Garrison == 0 && World.Within(u.Pos, cell.Center(), u.Def.Vision)) { return true; }
        }
        return false;
    }

    private Cell Objective(World w, Unit fighter)
    {
        for (var i = 0; i < _assigned.Length; i++)
        {
            if (_assigned[i] == fighter.Id) { _assigned[i] = 0; }
        }
        SeenStructure? remembered = null;
        long distance = long.MaxValue;
        foreach (var m in _memory)
        {
            var d = (m.Cell.Center() - _home.Center()).LenSq();
            if (d < distance) { distance = d; remembered = m; } // ID order breaks ties
        }
        if (remembered is { } target) { return target.Cell; }
        var routing = w.Routing; // map/class reachability only, never enemy unit lookup
        var k = routing.ClassOf(fighter.Def);
        if (k < 0) { return _home; }
        var start = routing.NearestPassable(k, Cell.Of(fighter.Pos), Math.Max(1, RegionSizeCells / 2));
        if (start < 0) { return _home; }
        var component = routing.Component(k, start);
        var best = -1;
        var bestFar = false;
        var bestDistance = long.MaxValue;
        var destination = _home;
        for (var i = 0; i < _probes.Length; i++)
        {
            if (_assigned[i] != 0) { continue; }
            if (_seen[i] != 0 && w.Tick + 1 - _seen[i] < RescoutEveryTicks) { continue; }
            var c = _probes[i];
            var d = (c.Center() - _home.Center()).LenSq();
            var far = (c.X < w.Grid.Width / 2) != (_home.X < w.Grid.Width / 2) ||
                (c.Y < w.Grid.Height / 2) != (_home.Y < w.Grid.Height / 2);
            if (best >= 0 && (_seen[i] > _seen[best] || (_seen[i] == _seen[best] &&
                ((!far && bestFar) || (far == bestFar && d >= bestDistance))))) { continue; }
            var cell = routing.NearestPassable(k, c, Math.Max(1, RegionSizeCells / 2));
            if (cell < 0 || component == 0 || routing.Component(k, cell) != component) { continue; }
            best = i; bestFar = far; bestDistance = d; destination = routing.CellAt(cell);
        }
        if (best >= 0)
        {
            _probes[best] = destination; // remember the reachable probe, without marking it seen
            _assigned[best] = fighter.Id;
        }
        else { _searching.Remove(fighter.Id); } // all probes recent: guard until the next normal wave
        return destination;
    }
}
