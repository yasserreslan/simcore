using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The height planner (E12): how high an aircraft in free flight wants to be, and how it gets there.
//
// Before E12 the height command was its flight height over the higher of the ground under it and ONE
// point of ground a fixed time ahead, chased at its full climb or dive rate: bang-bang. A single far
// sample flicks between hilltop and valley as the aircraft turns (the point sweeps sideways across the
// ground), and over narrow features (a building's pad on a slope, a ridge) the sample passes them
// before the aircraft does, so it climbed, sank, climbed again; its vertical speed reversed from full
// climb to full dive in one tick (a jet: +28 to -40 m/s, about 140 g). Structures were not seen at all
// (a jet strafing at its 10 m floor flew through a 16 m block). Measured on the game's review map:
// 80 to 105 reversals per 1,000 ticks for the multirole, 25 to 35 for the drones (E12 report).
//
// Now, all integer and allocation-free per tick:
//  1. The clearance grid: per block of 2 x 2 cells the highest of its terrain corners, and apart the
//     highest top of the structures standing on it, both dilated by one block (so a sample within two
//     cells of a roof sees it). Scratch, not state: built the first time an aircraft asks, then kept up to date from the
//     terrain's change journal and a diff of the structure list (both in ID order), only round what
//     changed. Content without aircraft never builds it.
//  2. The profile: along the path it is flying (its current turn continued: an arc) every two cells
//     out to its look-ahead (5 s: the jets' fixedWing.lookAhead, the drones' ScoutLookAhead), its
//     flight height over the terrain there or its clearance over a roof there, whichever is higher,
//     less what it can climb by the time it gets there at three quarters of its climb rate. The
//     highest of those (and of the block under it) is the height it wants now: it starts climbing
//     early, at its climb rate, for anything ahead; a jet at 60 m does not hop over a 16 m block.
//  3. The command: that height (a strike's profile, a break or an air target never below the same
//     profile at its floor), tracked with a bounded vertical acceleration and the
//     largest vertical speed it can still stop from in time (deadbeat in integers: no overshoot, no
//     chatter), climbing at most its climb rate and descending gently (half its climb rate) when it
//     follows the ground (an error under 1 m left alone); a strike, a break or an engagement may
//     dive at its dive rate and pull its full g. The terrain floor stays as a last resort and is counted (AirFloorClamps; tests assert 0).
public sealed partial class World
{
    // Samples lie this far apart along the path (raw: 2 cells, one block).
    private const long PlanStepRaw = 2 * Fixed.OneRaw;

    // It plans to climb at this share of its climb rate (thousandths), keeping some in hand.
    private const int PlanClimbPermille = 750;

    // The clearance grid's block: 2 x 2 cells.
    private const int ClearShift = 1;

    // A block with no structure on or beside it (low enough that no clearance over it counts).
    private const int NoRoof = int.MinValue / 4;

    // Following the ground level, a height error this small is left alone (raw: 1 m): no bobbing over small bumps.
    private const long HoldBandRaw = 512;

    // Per block (raw heights): the terrain corners' max and the structure tops' max (NoRoof: none),
    // undilated, and the same dilated by one block (what the planner reads).
    private int[] _terrRaw = [], _roofRaw = [], _terr = [], _roof = [];
    private int _clearBw, _clearBh;
    private bool _clearBuilt;
    private int _clearTerrain;      // the terrain version the grid reflects
    private uint _clearTick = uint.MaxValue;
    private List<(uint Id, int X0, int Y0, int X1, int Y1)> _clearStamps = [], _clearStampsNext = [];
    private readonly List<Cell> _clearCells = [];

    /// <summary>
    /// Diagnostic, not game state: how many aircraft height steps the terrain floor had to lift (the
    /// planner keeps it at 0; tests assert it). The last resort, like <see cref="AirEdgeClamps"/>.
    /// </summary>
    internal int AirFloorClamps { get; private set; }

    /// <summary>
    /// The clearance the height planner sees at p (raw height): the highest terrain corner and structure top in
    /// its 2 x 2-cell block and the blocks round it. For tests and debugging views; read-only.
    /// </summary>
    public long ClearanceAt(Vec p)
    {
        EnsureClearance();
        var i = ClearIndex(p.X.Raw, p.Y.Raw);
        return Math.Max(_terr[i], _roof[i]);
    }

    // ClearIndex is the block of point (x, y) (raw), off the map the nearest edge block.
    private int ClearIndex(long x, long y)
    {
        var bx = (int)Math.Clamp(x >> (Fixed.Shift + ClearShift), 0, _clearBw - 1);
        var by = (int)Math.Clamp(y >> (Fixed.Shift + ClearShift), 0, _clearBh - 1);
        return by * _clearBw + bx;
    }

    // EnsureClearance brings the clearance grid up to date (once a tick, on the first aircraft's ask).
    private void EnsureClearance()
    {
        if (_clearTick == Tick && _clearBuilt)
        {
            return;
        }
        _clearTick = Tick;
        if (!_clearBuilt)
        {
            _clearBw = Math.Max(1, (Grid.Width + (1 << ClearShift) - 1) >> ClearShift);
            _clearBh = Math.Max(1, (Grid.Height + (1 << ClearShift) - 1) >> ClearShift);
            var n = _clearBw * _clearBh;
            (_terrRaw, _roofRaw, _terr, _roof) = (new int[n], new int[n], new int[n], new int[n]);
            _clearTerrain = Ground?.Version ?? 0;
            DiffStructures(false);
            RebuildClear(0, 0, _clearBw - 1, _clearBh - 1);
            _clearBuilt = true;
            return;
        }
        if (Ground is { } g && g.Version != _clearTerrain)
        {
            _clearCells.Clear();
            if (!g.ChangedSince(_clearTerrain, _clearCells) || _clearCells.Count > _clearBw * _clearBh)
            {
                _clearTerrain = g.Version;
                DiffStructures(false);
                RebuildClear(0, 0, _clearBw - 1, _clearBh - 1);
                return;
            }
            _clearTerrain = g.Version;
            foreach (var c in _clearCells)
            {
                // A corner change reaches the cells round it: the blocks of the cell and its neighbours.
                RebuildClear((c.X - 1) >> ClearShift, (c.Y - 1) >> ClearShift, (c.X + 1) >> ClearShift, (c.Y + 1) >> ClearShift);
            }
        }
        DiffStructures(true);
    }

    // DiffStructures compares the structures standing now with those stamped (both in ID order) and,
    // with rebuild, rebuilds the blocks round every one that came or went.
    private void DiffStructures(bool rebuild)
    {
        var next = _clearStampsNext;
        next.Clear();
        var same = true;
        var k = 0;
        foreach (var u in _units)
        {
            if (!u.Def.IsStructure || !u.Alive)
            {
                continue;
            }
            if (k >= _clearStamps.Count || _clearStamps[k].Id != u.Id)
            {
                same = false;
            }
            k++;
            var o = FootprintOrigin(u);
            next.Add((u.Id, o.X >> ClearShift, o.Y >> ClearShift, (o.X + FootW(u) - 1) >> ClearShift, (o.Y + FootH(u) - 1) >> ClearShift));
        }
        if (same && k == _clearStamps.Count)
        {
            return;
        }
        var old = _clearStamps;
        (_clearStamps, _clearStampsNext) = (next, old);
        if (!rebuild)
        {
            return;
        }
        // Rebuild round each stamp in one list and not the other (a merge on the IDs).
        int i = 0, j = 0;
        while (i < old.Count || j < next.Count)
        {
            if (j >= next.Count || (i < old.Count && old[i].Id < next[j].Id))
            {
                var s = old[i++];
                RebuildClear(s.X0, s.Y0, s.X1, s.Y1);
            }
            else if (i >= old.Count || next[j].Id < old[i].Id)
            {
                var s = next[j++];
                RebuildClear(s.X0, s.Y0, s.X1, s.Y1);
            }
            else
            {
                (i, j) = (i + 1, j + 1);
            }
        }
    }

    // RebuildClear recomputes blocks x0..x1, y0..y1 (clamped): the undilated values, then the dilated
    // ones one block further out.
    private void RebuildClear(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(_clearBw - 1, x1);
        y1 = Math.Min(_clearBh - 1, y1);
        if (x0 > x1 || y0 > y1)
        {
            return;
        }
        var g = Ground;
        const int span = 1 << ClearShift;
        for (var by = y0; by <= y1; by++)
        {
            for (var bx = x0; bx <= x1; bx++)
            {
                var top = int.MinValue;
                if (g is null)
                {
                    top = 0;
                }
                else
                {
                    for (var cy = by * span; cy <= by * span + span; cy++)
                    {
                        for (var cx = bx * span; cx <= bx * span + span; cx++)
                        {
                            top = Math.Max(top, (int)g.Corner(cx, cy).Raw);
                            if (g.HasWater && g.WaterBodyAt(new Cell(cx, cy)) is { } wb)
                            {
                                top = Math.Max(top, (int)wb.Surface.Raw); // a water surface above its bed (WorldWater.cs)
                            }
                        }
                    }
                }
                _terrRaw[by * _clearBw + bx] = top;
                _roofRaw[by * _clearBw + bx] = NoRoof;
            }
        }
        foreach (var s in _clearStamps)
        {
            if (s.X1 < x0 || s.X0 > x1 || s.Y1 < y0 || s.Y0 > y1 || Unit(s.Id) is not { } b)
            {
                continue;
            }
            var roof = (int)(GroundRaw(b.Pos) + b.Def.Archetype.HeightRaw);
            for (var by = Math.Max(y0, s.Y0); by <= Math.Min(y1, s.Y1); by++)
            {
                for (var bx = Math.Max(x0, s.X0); bx <= Math.Min(x1, s.X1); bx++)
                {
                    var i = by * _clearBw + bx;
                    _roofRaw[i] = Math.Max(_roofRaw[i], roof);
                }
            }
        }
        for (var by = Math.Max(0, y0 - 1); by <= Math.Min(_clearBh - 1, y1 + 1); by++)
        {
            for (var bx = Math.Max(0, x0 - 1); bx <= Math.Min(_clearBw - 1, x1 + 1); bx++)
            {
                int top = int.MinValue, roof = NoRoof;
                for (var ny = Math.Max(0, by - 1); ny <= Math.Min(_clearBh - 1, by + 1); ny++)
                {
                    for (var nx = Math.Max(0, bx - 1); nx <= Math.Min(_clearBw - 1, bx + 1); nx++)
                    {
                        top = Math.Max(top, _terrRaw[ny * _clearBw + nx]);
                        roof = Math.Max(roof, _roofRaw[ny * _clearBw + nx]);
                    }
                }
                _terr[by * _clearBw + bx] = top;
                _roof[by * _clearBw + bx] = roof;
            }
        }
    }

    // PlanHeight is the height an aircraft at pos wants now (raw): band over the terrain and clear over
    // the structure tops, under it and along its path (velocity vx, vy raw a tick, turning as from the
    // step (ox, oy) to it, its turn continued) every PlanStepRaw out to horizon ticks of travel, each
    // less what it can climb by then at PlanClimbPermille of climb (raw a tick). Low is the same with
    // band2 and clear2 (a jet's floor), worked out in the same pass.
    private (long Want, long Low) PlanHeight(Vec pos, long ox, long oy, long vx, long vy, long climb, int horizon, long band, long clear,
        long band2, long clear2)
    {
        EnsureClearance();
        var i0 = ClearIndex(pos.X.Raw, pos.Y.Raw);
        var best = Math.Max(_terr[i0] + band, _roof[i0] + clear);
        var low = Math.Max(_terr[i0] + band2, _roof[i0] + clear2);
        var speed = Len3(vx, vy, 0);
        if (speed == 0)
        {
            return (best, low);
        }
        // The turn over a sample's step (every ticks), as (cos, sin) in 1/16384: the turn a tick, from
        // the last step to this one (none: straight), compounded.
        var every = (int)Math.Max(1, PlanStepRaw / speed);
        long c = 16384, s = 0;
        var ol = Len3(ox, oy, 0);
        if (ol > 0)
        {
            var d = ol * speed;
            long c1 = (ox * vx + oy * vy) * 16384 / d, s1 = (ox * vy - oy * vx) * 16384 / d;
            for (var k = 0; k < every; k++)
            {
                (c, s) = ((c * c1 - s * s1) / 16384, (c * s1 + s * c1) / 16384);
            }
        }
        var grad = Math.Max(1, climb * PlanClimbPermille / 1000) * every;
        long px = pos.X.Raw, py = pos.Y.Raw, qx = vx * every, qy = vy * every, drop = 0;
        for (var t = every; t <= horizon; t += every)
        {
            px += qx;
            py += qy;
            drop += grad;
            if (s != 0)
            {
                (qx, qy) = ((qx * c - qy * s) / 16384, (qx * s + qy * c) / 16384);
            }
            var i = ClearIndex(px, py);
            int tr = _terr[i], rf = _roof[i];
            best = Math.Max(best, Math.Max(tr + band, rf + clear) - drop);
            low = Math.Max(low, Math.Max(tr + band2, rf + clear2) - drop);
        }
        return (best, low);
    }

    // VerticalStep moves u's height toward want (raw): its vertical speed changes by at most acc a
    // tick (raw a tick squared) and is at most the speed it can still stop from before want (so it
    // never overshoots), climbing at most up and sinking at most down (raw a tick); with hold, level
    // and within HoldBandRaw of want it stays level; then the terrain floor (counted when it bites). It sets
    // VelZ and Altitude.
    private void VerticalStep(Unit u, long want, long up, long down, long acc, long floor, bool hold)
    {
        var alt = u.Altitude.Raw;
        var e = want - alt;
        if (hold && u.VelZ.Raw == 0 && Math.Abs(e) <= HoldBandRaw)
        {
            e = 0; // level, and within the hold band: it stays level (moving, it goes all the way)
        }
        var ae = Math.Abs(e);
        // The most it may move now and still stop by want: v (v + acc) / 2 acc <= |e|.
        var stop = (Fixed.Sqrt(acc * acc + 8 * acc * ae) - acc) / 2;
        var v = Math.Min(ae, stop);
        v = e >= 0 ? Math.Min(v, up) : -Math.Min(v, down);
        var vz0 = u.VelZ.Raw;
        var vz = Math.Clamp(v, vz0 - acc, vz0 + acc);
        alt += vz;
        var step = vz;
        if (alt < floor)
        {
            // The floor lifts it (counted; a drone off its launcher starts under its floor): the lift is
            // not carried on as vertical speed (at most its climb rate), so it does not overshoot after.
            AirFloorClamps++;
            step = Math.Min(floor - u.Altitude.Raw, Math.Max(vz, up));
            alt = floor;
        }
        u.VelZ = Fixed.FromRaw(step);
        u.Altitude = Fixed.FromRaw(alt);
    }
}
