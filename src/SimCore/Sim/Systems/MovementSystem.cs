using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The movement system (local movement): every ground unit standing on the map moves along its
// MoveIntent.Desired, which the path system wrote this tick. It owns everything local: terrain
// speed (slope up or down, forest), local avoidance, collision with other units, blocked cells
// and buildings, vehicle turning and acceleration, arrival and stuck detection. It never touches
// a unit's path (Path, PathIdx): routing owns those.
//
// One tick, all integer:
//  1. Bodies: each ground unit on the map (not a driver inside, not garrisoned, not a structure
//     or trap), in ID order. A new Goal resets Arrived, Stuck and the progress tracking.
//  2. Steering (moving bodies), from the positions and velocities at the start of the tick:
//     the preferred velocity is Desired scaled by the terrain speed percent, braked to land on
//     the goal; neighbours on a collision course add a sideways dodge (away from where they will
//     pass; to the right when dead ahead or oncoming, so opposing streams form lanes; only where
//     there is room); and it walks into a unit it touches no faster than that one walks away,
//     plus a slow nudge for a friendly standing still (none for an enemy). These are sums over
//     the neighbours, so the order they are visited in doesn't matter. The pairs of bodies that
//     may touch are collected on the way.
//  3. Integration: infantry take the steered velocity; vehicles turn their heading toward it at
//     their turn rate, only while rolling (no turning in place), slowing to a crawl for sharp
//     turns, and accelerate and brake. Blocked cells and buildings push the body's circle (its
//     radius is its clearance) out, so it slides along walls.
//  4. Collision: rounds of position projection over the pairs, in ID order (Gauss-Seidel), with
//     walls after each round. Overlap is shared by weight (radius squared): an idle friendly
//     gives way to a moving one unless hemmed in, enemies never push (a moving unit stops at an
//     enemy standing still), empty vehicles and deployed launchers don't move, and a unit pressed
//     against a wall leaves the correction to the other one.
//  5. Write back: position, velocity, facing; Arrived within tolerance of the goal; Stuck after
//     StuckTicks without progress (routing decides what a stuck unit near its end means).
public sealed partial class World
{
    // Avoidance looks this far beyond contact for neighbours on a collision course (raw).
    private const long AvoidLook = 1024;

    // ...and this many ticks ahead.
    private const long AvoidTicks = 30;

    // Pairs within contact plus this margin are checked for collision after integration (raw):
    // more than two bodies can close in one tick.
    private const long PairMargin = 512;

    // Personal space added to contact distance when dodging (raw).
    private const long AvoidGap = 48;

    // How fast, in percent of its speed, a unit walks into a friendly that stands still (it
    // nudges it aside); into an enemy standing still it doesn't walk at all.
    private const long NudgePercent = 25;

    // A body whose cell is this many cells or more from any wall can't touch one this tick
    // (radius up to 1.2 cells, plus its step and pushes): it skips the wall tests.
    private const byte NearTouch = 3;

    // Separation: within contact plus this gap (raw) a moving unit eases away from a neighbour,
    // at up to this percent of its speed.
    private const long SeparationGap = 192;
    private const long SeparationPercent = 50;

    // A vehicle stalled this many ticks with its goal behind it backs up while it turns.
    private const int ReverseAfter = 20;

    // Position projection rounds per tick: at least MinRounds, then more (up to the maximum)
    // while some pair still overlaps by more than RoundSlack percent of the smaller radius.
    private const int CollisionRounds = 10;
    private const int MinRounds = 3;
    private const int RoundSlack = 3;

    // A unit standing still with this many units touching it doesn't give way.
    private const int HemmedIn = 3;

    // Progress toward the goal must improve by this much (raw, 1/16 cell)...
    private const long ProgressEps = 64;

    // ...within this many ticks, or the unit is stuck.
    internal const int StuckTicks = 40;


    private readonly MoveScratch _mv = new();

    /// <summary>
    /// Test switch: visit neighbours and pairs in reverse order. The movement system only sums
    /// integer contributions, so the world must come out identical. Not game state.
    /// </summary>
    internal bool ReverseMoveOrder { get; set; }

    internal string MovementWorst { get; private set; } = "";

    private void RunGroundMovement()
    {
        var m = _mv;
        m.Reset(_units.Count, Grid.Width, Grid.Height);
        CollectBodies(m);
        if (m.N == 0)
        {
            return;
        }
        m.BuildGrid();
        for (var i = 0; i < m.N; i++)
        {
            Steer(m, i);
        }
        for (var i = 0; i < m.N; i++)
        {
            Integrate(m, i);
        }
        Collide(m);
        for (var i = 0; i < m.N; i++)
        {
            Finish(m, i);
        }
    }

    // ---- 1. Bodies ------------------------------------------------------------------------

    private void CollectBodies(MoveScratch m)
    {
        var rules = Ground?.Rules;
        foreach (var u in _units)
        {
            var def = u.Def;
            if (def.Layer != Layers.Ground || !u.Alive)
            {
                continue;
            }
            if (def.IsStructure)
            {
                if (def.Archetype.Garrison is null)
                {
                    // Garrisonable buildings block their cells in the grid; others (depots) block their box.
                    long hw = (long)def.Archetype.FootprintW * Fixed.OneRaw / 2, hh = (long)def.Archetype.FootprintH * Fixed.OneRaw / 2;
                    m.Boxes.Add((u.Pos.X.Raw - hw, u.Pos.Y.Raw - hh, u.Pos.X.Raw + hw, u.Pos.Y.Raw + hh));
                }
                continue;
            }
            if (u.Inside != 0 || u.Garrison != 0 || def.Archetype.Locomotor == Locomotor.None)
            {
                continue;
            }
            // A new goal starts over: not arrived, not stuck, progress measured from here.
            var intent = u.Intent;
            if (intent.Goal != u.StallGoal)
            {
                u.StallGoal = intent.Goal;
                u.StallBest = (intent.Goal - u.Pos).Len();
                u.StallTicks = 0;
                if (intent.Arrived || intent.Stuck)
                {
                    u.Intent = intent = intent with { Arrived = false, Stuck = false };
                }
            }
            var i = m.N++;
            m.U[i] = u;
            m.Id[i] = u.Id;
            m.Owner[i] = u.Owner;
            m.Enter[i] = u.Order.Kind == OrderKind.Enter ? u.Order.Target : 0;
            m.PVX[i] = u.Vel.X.Raw;
            m.PVY[i] = u.Vel.Y.Raw;
            m.X[i] = m.X0[i] = u.Pos.X.Raw;
            m.Y[i] = m.Y0[i] = u.Pos.Y.Raw;
            m.VX[i] = m.VY[i] = 0;
            var r = (int)def.Archetype.Radius.Raw;
            m.R[i] = r;
            m.MaxR = Math.Max(m.MaxR, r);
            var rr = Math.Max(1, r >> 5);
            m.Mass[i] = rr * rr;
            m.Cls[i] = rules?.ClassOf(def) ?? -1;
            m.Pct[i] = 100;
            byte f = 0;
            var deployed = def.Archetype.DeployTicks > 0 && u.Deploy != DeployState.Stowed;
            if (!u.CanAct || def.Speed.Raw == 0 || deployed)
            {
                f |= MoveScratch.Anchored; // an empty vehicle, a deployed launcher: nothing moves it
            }
            else
            {
                f |= MoveScratch.Mobile;
                if (!intent.Arrived && (intent.Desired.X.Raw != 0 || intent.Desired.Y.Raw != 0))
                {
                    f |= MoveScratch.Moving;
                }
            }
            if (def.Archetype.Locomotor is Locomotor.Wheeled or Locomotor.Tracked)
            {
                f |= MoveScratch.Vehicle;
            }
            m.Flags[i] = f;
        }
        for (var i = 0; i < m.N; i++)
        {
            m.Near[i] = NearWall(m, i);
        }
    }

    // NearWall is how many cells (Chebyshev, capped) body i's cell is from the nearest wall:
    // a cell its class can't enter, or a structure's box.
    private byte NearWall(MoveScratch m, int i)
    {
        var wall = WallDistances(m.Cls[i]);
        long x = m.X[i], y = m.Y[i];
        var cx = (int)(x >> Fixed.Shift);
        var cy = (int)(y >> Fixed.Shift);
        var near = Grid.Contains(new Cell(cx, cy)) ? wall[cy * Grid.Width + cx] : (byte)0;
        foreach (var (x0, y0, x1, y1) in m.Boxes)
        {
            const long reach = MoveScratch.WallCap * Fixed.OneRaw;
            if (x > x0 - reach && x < x1 + reach && y > y0 - reach && y < y1 + reach)
            {
                return 0;
            }
        }
        return near;
    }

    // For tests: the wall distances of a terrain class (-1 without terrain), built or brought up to date.
    internal byte[] WallDistancesFor(int cls) => WallDistances(cls);

    // WallDistances returns (building it when the grid or terrain changed) the capped Chebyshev
    // distance of every cell to the nearest cell a class can't enter, off the map included.
    private byte[] WallDistances(int cls)
    {
        var m = _mv;
        var n = (Ground?.Rules.Classes.Count ?? 0) + 1;
        if (m.Wall.Length != n || m.WallMap != Ground)
        {
            m.Wall = new byte[n][];
            m.WallGrid = new int[n];
            m.WallTerrain = new int[n];
            m.WallMap = Ground;
        }
        var k = cls + 1;
        var tv = Ground?.Version ?? 0;
        if (m.Wall[k] is { } ready && m.WallGrid[k] == Grid.Version && m.WallTerrain[k] == tv)
        {
            return ready;
        }
        int w = Grid.Width, h = Grid.Height;
        // Only cells within WallCap of a change can change: redo the box around the changes when
        // the change journals reach back far enough and the box is small, else the whole map.
        int bx0 = 0, by0 = 0, bx1 = w - 1, by1 = h - 1;
        if (m.Wall[k] is not null && Changes(m.WallGrid[k], m.WallTerrain[k], m.Changed))
        {
            (bx0, by0, bx1, by1) = (w, h, -1, -1);
            foreach (var c in m.Changed)
            {
                int cx = c % w, cy = c / w;
                (bx0, by0) = (Math.Min(bx0, cx - MoveScratch.WallCap), Math.Min(by0, cy - MoveScratch.WallCap));
                (bx1, by1) = (Math.Max(bx1, cx + MoveScratch.WallCap), Math.Max(by1, cy + MoveScratch.WallCap));
            }
            (bx0, by0, bx1, by1) = (Math.Max(bx0, 0), Math.Max(by0, 0), Math.Min(bx1, w - 1), Math.Min(by1, h - 1));
        }
        var d = m.Wall[k] ?? new byte[w * h];
        if (bx1 >= bx0 && by1 >= by0)
        {
            Chamfer(d, cls, bx0, by0, bx1, by1);
        }
        m.Wall[k] = d;
        m.WallGrid[k] = Grid.Version;
        m.WallTerrain[k] = tv;
        return d;
    }

    // Changes lists the cells changed in the grid and the terrain since the given versions, and
    // says whether the journals reached back that far.
    private bool Changes(int gridSince, int terrainSince, List<int> cells)
    {
        cells.Clear();
        if (!Grid.ChangedSince(gridSince, cells))
        {
            return false;
        }
        if (Ground is { } g)
        {
            var tc = new List<Cell>();
            if (!g.ChangedSince(terrainSince, tc))
            {
                return false;
            }
            foreach (var c in tc)
            {
                cells.Add(c.Y * Grid.Width + c.X);
            }
        }
        return cells.Count <= 4096;
    }

    // Chamfer computes the capped Chebyshev wall distance over a box of cells, reading the
    // cells around the box as they are (only cells within WallCap of a change can change, so a
    // box padded by WallCap around the changes has the right values all around it).
    private void Chamfer(byte[] d, int cls, int x0, int y0, int x1, int y1)
    {
        int w = Grid.Width, h = Grid.Height;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                d[y * w + x] = Blocked(x, y, cls) ? (byte)0 : MoveScratch.WallCap;
            }
        }
        // Two chamfer passes with every neighbour at 1: the Chebyshev distance (off-map is 0).
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var v = (int)d[y * w + x];
                v = Math.Min(v, 1 + (x > 0 ? d[y * w + x - 1] : 0));
                v = Math.Min(v, 1 + (y > 0 ? d[(y - 1) * w + x] : 0));
                v = Math.Min(v, 1 + (x > 0 && y > 0 ? d[(y - 1) * w + x - 1] : 0));
                v = Math.Min(v, 1 + (x < w - 1 && y > 0 ? d[(y - 1) * w + x + 1] : 0));
                d[y * w + x] = (byte)v;
            }
        }
        for (var y = y1; y >= y0; y--)
        {
            for (var x = x1; x >= x0; x--)
            {
                var v = (int)d[y * w + x];
                v = Math.Min(v, 1 + (x < w - 1 ? d[y * w + x + 1] : 0));
                v = Math.Min(v, 1 + (y < h - 1 ? d[(y + 1) * w + x] : 0));
                v = Math.Min(v, 1 + (x < w - 1 && y < h - 1 ? d[(y + 1) * w + x + 1] : 0));
                v = Math.Min(v, 1 + (x > 0 && y < h - 1 ? d[(y + 1) * w + x - 1] : 0));
                d[y * w + x] = (byte)v;
            }
        }
    }

    // Ignored reports whether two units pass through each other: a soldier and the vehicle it
    // is boarding (it climbs in from beside it).
    private static bool Ignored(MoveScratch m, int a, int b) => m.Enter[a] == m.Id[b] || m.Enter[b] == m.Id[a];

    // ---- 2. Steering ----------------------------------------------------------------------

    private void Steer(MoveScratch m, int i)
    {
        var u = m.U[i];
        var moving = (m.Flags[i] & MoveScratch.Moving) != 0;
        long vx = 0, vy = 0, max = 0;
        if (moving)
        {
            var d = u.Intent.Desired;
            var pct = TerrainPercent(m.Cls[i], u.Pos, d);
            m.Pct[i] = pct;
            vx = d.X.Raw * pct / 100;
            vy = d.Y.Raw * pct / 100;
            max = u.Def.Speed.Raw * pct / 100;
            // Brake to land on the goal rather than overshoot it.
            var gx = u.Intent.Goal.X.Raw - m.X[i];
            var gy = u.Intent.Goal.Y.Raw - m.Y[i];
            var dist = Fixed.Sqrt(gx * gx + gy * gy);
            var len = Fixed.Sqrt(vx * vx + vy * vy);
            if (len > dist && len > 0)
            {
                vx = vx * dist / len;
                vy = vy * dist / len;
            }
        }

        var ri = m.R[i];
        var look = moving ? AvoidLook : PairMargin;
        var range = ri + m.MaxR + look;
        long xi = m.X[i], yi = m.Y[i];
        int bx0 = m.Bx(xi - range), bx1 = m.Bx(xi + range), by0 = m.By(yi - range), by1 = m.By(yi + range);
        long sx = 0, sy = 0;
        var speed = moving ? Fixed.Sqrt(vx * vx + vy * vy) : 0;
        var rev = ReverseMoveOrder;
        var firstPair = m.Pairs;
        var kn = 0;
        m.Contacts[i] = 0;
        m.KN[i] = 0;
        for (var yy = by0; yy <= by1; yy++)
        {
            var by = rev ? by0 + by1 - yy : yy;
            for (var xx = bx0; xx <= bx1; xx++)
            {
                var bx = rev ? bx0 + bx1 - xx : xx;
                var b = by * m.Bw + bx;
                for (var kk = m.Start[b]; kk < m.Start[b + 1]; kk++)
                {
                    var j = m.Items[rev ? m.Start[b] + m.Start[b + 1] - 1 - kk : kk];
                    if (j == i)
                    {
                        continue;
                    }
                    var px = m.X[j] - xi;
                    var py = m.Y[j] - yi;
                    var touch = ri + m.R[j];
                    var lim = touch + look;
                    if (px > lim || px < -lim || py > lim || py < -lim)
                    {
                        continue;
                    }
                    var d2 = px * px + py * py;
                    if (d2 < (touch + 64) * (touch + 64))
                    {
                        m.Contacts[i]++;
                    }
                    if (j > i && d2 < (touch + PairMargin) * (touch + PairMargin))
                    {
                        if (!Ignored(m, i, j))
                        {
                            m.AddPair(i, j);
                        }
                    }
                    if (moving && speed > 0 && d2 < (touch + 64) * (touch + 64) && d2 > 0 && !Ignored(m, i, j))
                    {
                        // Touching another unit: walk into it no faster than it walks away, plus a
                        // nudge for some friendlies (they give way slowly; an enemy doesn't). Keeps the
                        // MaxContacts nearest (by distance, then ID), whatever the visiting order.
                        var k = ContactSlot(m, i, ref kn, d2, m.Id[j]);
                        if (k >= 0)
                        {
                            var dj = Fixed.Sqrt(d2);
                            var nx = px * Fixed.OneRaw / dj;
                            var ny = py * Fixed.OneRaw / dj;
                            var away = (m.PVX[j] * nx + m.PVY[j] * ny) >> Fixed.Shift;
                            m.KX[k] = nx;
                            m.KY[k] = ny;
                            // A friendly standing still or coming the other way can be nudged
                            // (crowds squeeze past each other); one walking the same way can't.
                            var nudge = m.Owner[j] == m.Owner[i] &&
                                        ((m.Flags[j] & MoveScratch.Moving) == 0 || m.PVX[j] * vx + m.PVY[j] * vy < 0);
                            m.KL[k] = Math.Max(0, away) + (nudge ? speed * NudgePercent / 100 : 0);
                        }
                    }
                    if (moving && speed > 0 && d2 < lim * lim)
                    {
                        Dodge(m, i, j, px, py, vx, vy, speed, ref sx, ref sy);
                        var sep = touch + SeparationGap;
                        if (d2 < sep * sep && d2 > 0 && (m.Flags[j] & MoveScratch.Moving) != 0 &&
                            m.PVX[j] * vx + m.PVY[j] * vy > 0 && !Ignored(m, i, j))
                        {
                            // Separation: ease away from a neighbour walking the same way closer
                            // than a little gap, so a stream funnelled onto one line spreads out
                            // before it overlaps (not from oncoming or standing units: dodging and
                            // nudging deal with those).
                            var dj = Fixed.Sqrt(d2);
                            var push = speed * SeparationPercent / 100 * (sep - dj) / SeparationGap;
                            sx -= px * push / dj;
                            sy -= py * push / dj;
                        }
                    }
                }
            }
        }
        // Collision visits pairs in ID order (body order is ID order): sort this body's new pairs.
        for (var a = firstPair + 1; a < m.Pairs; a++)
        {
            var pb = m.PairB[a];
            var k = a - 1;
            while (k >= firstPair && m.PairB[k] > pb)
            {
                m.PairB[k + 1] = m.PairB[k];
                k--;
            }
            m.PairB[k + 1] = pb;
        }
        if (moving)
        {
            vx += sx;
            vy += sy;
            var len = Fixed.Sqrt(vx * vx + vy * vy);
            if (len > max && len > 0)
            {
                vx = vx * max / len;
                vy = vy * max / len;
            }
            m.CX[i] = vx; // before the contacts: vehicles steer by it (they may have to turn first)
            m.CY[i] = vy;
            SortContacts(m, i, kn);
            Project(m, i, ref vx, ref vy);
        }
        m.VX[i] = vx;
        m.VY[i] = vy;
    }

    // SortContacts orders body i's kn contacts nearest first (then by ID): the same order
    // whatever order the neighbours were visited in.
    private static void SortContacts(MoveScratch m, int i, int kn)
    {
        var b0 = i * MoveScratch.MaxContacts;
        m.KN[i] = kn;
        for (var a = b0 + 1; a < b0 + kn; a++)
        {
            for (var k = a; k > b0 && (m.KD[k] < m.KD[k - 1] || (m.KD[k] == m.KD[k - 1] && m.KI[k] < m.KI[k - 1])); k--)
            {
                (m.KD[k], m.KD[k - 1]) = (m.KD[k - 1], m.KD[k]);
                (m.KI[k], m.KI[k - 1]) = (m.KI[k - 1], m.KI[k]);
                (m.KX[k], m.KX[k - 1]) = (m.KX[k - 1], m.KX[k]);
                (m.KY[k], m.KY[k - 1]) = (m.KY[k - 1], m.KY[k]);
                (m.KL[k], m.KL[k - 1]) = (m.KL[k - 1], m.KL[k]);
            }
        }
    }

    // Project limits velocity v of body i to each contact's limit in turn (the speed it may
    // walk into that unit), twice over.
    private static void Project(MoveScratch m, int i, ref long vx, ref long vy)
    {
        var b0 = i * MoveScratch.MaxContacts;
        var b1 = b0 + m.KN[i];
        for (var pass = 0; pass < 2; pass++)
        {
            for (var k = b0; k < b1; k++)
            {
                var into = (vx * m.KX[k] + vy * m.KY[k]) >> Fixed.Shift;
                if (into > m.KL[k])
                {
                    vx -= m.KX[k] * (into - m.KL[k]) >> Fixed.Shift;
                    vy -= m.KY[k] * (into - m.KL[k]) >> Fixed.Shift;
                }
            }
        }
    }

    // ContactSlot returns where to store a contact of body i at squared distance d2 with unit
    // id: a free slot, else the slot of the farthest stored contact (ties: the higher ID) when
    // this one is nearer, else -1.
    private static int ContactSlot(MoveScratch m, int i, ref int kn, long d2, uint id)
    {
        var b = i * MoveScratch.MaxContacts;
        if (kn < MoveScratch.MaxContacts)
        {
            var k = b + kn++;
            m.KD[k] = d2;
            m.KI[k] = id;
            return k;
        }
        var worst = b;
        for (var k = b + 1; k < b + MoveScratch.MaxContacts; k++)
        {
            if (m.KD[k] > m.KD[worst] || (m.KD[k] == m.KD[worst] && m.KI[k] > m.KI[worst]))
            {
                worst = k;
            }
        }
        if (d2 > m.KD[worst] || (d2 == m.KD[worst] && id > m.KI[worst]))
        {
            return -1;
        }
        m.KD[worst] = d2;
        m.KI[worst] = id;
        return worst;
    }

    // Dodge adds the sideways steer of body i (preferred velocity v, its length speed) away from
    // body j at offset p, when they are on course to pass closer than contact (plus a little
    // space) within AvoidTicks. Neighbours moving the same way never trigger it.
    private void Dodge(MoveScratch m, int i, int j, long px, long py, long vx, long vy, long speed, ref long sx, ref long sy)
    {
        if (Ignored(m, i, j))
        {
            return;
        }
        long ovx = m.PVX[j], ovy = m.PVY[j];
        long rx = vx - ovx, ry = vy - ovy; // i's velocity relative to j
        var dot = px * rx + py * ry;
        if (dot <= 0)
        {
            return; // not closing in
        }
        var r2 = rx * rx + ry * ry;
        if (r2 == 0 || dot > AvoidTicks * r2)
        {
            return; // too slow to meet soon
        }
        // Where j will be relative to i at the closest approach: p - r * (dot / r2).
        var cx = px - rx * dot / r2;
        var cy = py - ry * dot / r2;
        var reach = m.R[i] + m.R[j] + AvoidGap;
        var c2 = cx * cx + cy * cy;
        if (c2 >= reach * reach)
        {
            return; // they pass clear
        }
        var c = Fixed.Sqrt(c2);
        long dx, dy, dl;
        var oncoming = ovx * vx + ovy * vy < 0;
        if (c * 8 > reach && !oncoming)
        {
            dx = -cx; // away from where j will be
            dy = -cy;
            dl = c;
        }
        else
        {
            dx = -vy; // dead ahead or oncoming: keep right, so opposing streams form lanes (screen y grows down: right of (x, y) is (-y, x))
            dy = vx;
            dl = speed;
        }
        // Only dodge where there is room for it: try the other side, else push through.
        var room = m.R[i] + 2 * m.R[j];
        if (m.Near[i] < MoveScratch.WallCap && WallAhead(m, i, dx, dy, dl, room))
        {
            dx = -dx;
            dy = -dy;
            if (WallAhead(m, i, dx, dy, dl, room))
            {
                return;
            }
        }
        // Stronger the deeper and the sooner the conflict: speed * (reach - c) / reach * (T - t) / T.
        var t = dot / r2;
        var w = speed * (reach - c) / reach * (AvoidTicks - t) / AvoidTicks;
        if ((m.Flags[j] & MoveScratch.Moving) == 0 && m.Owner[j] == m.Owner[i])
        {
            w /= 2; // a friendly standing still gives way anyway
        }
        sx += dx * w / dl;
        sy += dy * w / dl;
    }

    // TerrainPercent is the speed percent of a step d from p for a movement class: the slope
    // class of the cell it stands in, uphill or not by the height change of the step along the
    // cell's slope, times the forest there. 100 on a map without terrain.
    private int TerrainPercent(int cls, Vec p, Vec d)
    {
        if (Ground is not { } g || cls < 0)
        {
            return 100;
        }
        // Uphill when the step climbs the cell's slope: the step against the gradient of its
        // corner heights (the first-order height change of the step).
        var c = Cell.Of(p);
        long h00 = g.Corner(c.X, c.Y).Raw, h10 = g.Corner(c.X + 1, c.Y).Raw;
        long h01 = g.Corner(c.X, c.Y + 1).Raw, h11 = g.Corner(c.X + 1, c.Y + 1).Raw;
        var uphill = d.X.Raw * (h10 + h11 - h00 - h01) + d.Y.Raw * (h01 + h11 - h00 - h10) > 0;
        var pct = g.SpeedPercent(cls, c, uphill);
        return pct > 0 ? pct : 25; // pushed into a cell it can't enter: crawl out
    }

    // ---- 3. Integration -------------------------------------------------------------------

    private void Integrate(MoveScratch m, int i)
    {
        var u = m.U[i];
        var f = m.Flags[i];
        if ((f & MoveScratch.Mobile) == 0)
        {
            m.VX[i] = m.VY[i] = 0;
            if ((f & MoveScratch.Vehicle) != 0)
            {
                u.DriveSpeed = Fixed.Zero;
            }
            return;
        }
        if ((f & MoveScratch.Vehicle) != 0)
        {
            Drive(m, i);
        }
        else if ((f & MoveScratch.Moving) != 0)
        {
            var v = new Vec(Fixed.FromRaw(m.VX[i]), Fixed.FromRaw(m.VY[i]));
            if (Facing.TryFromDirection(v, out var want))
            {
                u.Facing = Facing.TurnToward(u.Facing, want, u.Def.TurnRate);
                u.Heading = v.WithLen(Fixed.One);
                if (!u.Def.Turret)
                {
                    u.TurretFacing = u.Facing;
                }
            }
        }
        if (m.VX[i] != 0 || m.VY[i] != 0)
        {
            m.X[i] += m.VX[i];
            m.Y[i] += m.VY[i];
            if (m.Near[i] < NearTouch)
            {
                PushOut(m, i);
            }
        }
    }

    // Drive moves a vehicle: its heading turns toward the steered velocity at the turn rate,
    // only while it rolls, and its speed ramps toward what the turn allows.
    private void Drive(MoveScratch m, int i)
    {
        var u = m.U[i];
        var def = u.Def;
        var h = u.Heading;
        if (!Facing.TryFromDirection(h, out var hf) || hf != u.Facing)
        {
            h = Facing.Dir(u.Facing); // someone else turned the hull (aiming): follow it
        }
        // Steer by the velocity it wants before its contacts (it may have to turn away from
        // them first); the contacts limit what it then drives.
        long vx = m.CX[i], vy = m.CY[i];
        var want = (m.Flags[i] & MoveScratch.Moving) != 0 ? Fixed.Sqrt(vx * vx + vy * vy) : 0;
        var top = def.Speed.Raw * m.Pct[i] / 100;
        var accel = Math.Max(1, def.Speed.Raw / (def.Archetype.AccelTicks > 0 ? def.Archetype.AccelTicks : 5));
        var decel = Math.Max(1, def.Speed.Raw / 3);
        var crawl = Math.Max(1, def.Speed.Raw * 2 / 5); // the speed it keeps while turning hard
        var spd = u.DriveSpeed.Raw;
        long target = 0;
        long dot = 0, dx = 0, dy = 0;
        if (want > 0)
        {
            dx = vx * Fixed.OneRaw / want;
            dy = vy * Fixed.OneRaw / want;
            dot = (h.X.Raw * dx + h.Y.Raw * dy) >> Fixed.Shift; // cos(angle) * 1024
            if (dot < 0 && u.StallTicks >= ReverseAfter)
            {
                target = -Math.Min(crawl, top) / 2; // boxed in facing away: back up while turning
            }
            else if (dot < 724)
            {
                target = Math.Min(crawl, top); // more than 45 degrees off: turn at a crawl
            }
            else
            {
                target = Math.Max(Math.Min(crawl, want), want * dot >> Fixed.Shift);
                // Brake for the goal: no faster than it can stop in.
                var gx = u.Intent.Goal.X.Raw - m.X[i];
                var gy = u.Intent.Goal.Y.Raw - m.Y[i];
                var dist = Fixed.Sqrt(gx * gx + gy * gy);
                target = Math.Min(target, Math.Max(Fixed.Sqrt(2 * decel * dist), Math.Min(dist, crawl)));
            }
        }
        spd = target > spd ? Math.Min(target, spd + accel) : Math.Max(target, spd - decel);
        if (spd != 0 && want > 0)
        {
            // Turn while rolling (forward or back), by at most the turn rate.
            var rate = Math.Clamp(def.TurnRate, 0, Facing.Count / 2);
            var rot = Facing.Dir(rate);
            long cos = -rot.Y.Raw, sin = rot.X.Raw;
            if (dot >= cos)
            {
                h = new Vec(Fixed.FromRaw(dx), Fixed.FromRaw(dy));
            }
            else
            {
                var cross = h.X.Raw * dy - h.Y.Raw * dx;
                if (cross < 0)
                {
                    sin = -sin; // counter-clockwise
                }
                var nx = (h.X.Raw * cos - h.Y.Raw * sin) >> Fixed.Shift;
                var ny = (h.X.Raw * sin + h.Y.Raw * cos) >> Fixed.Shift;
                h = new Vec(Fixed.FromRaw(nx), Fixed.FromRaw(ny)).WithLen(Fixed.One);
            }
        }
        u.Heading = h;
        u.DriveSpeed = Fixed.FromRaw(spd);
        if (Facing.TryFromDirection(h, out var nf))
        {
            u.Facing = nf;
            if (!def.Turret)
            {
                u.TurretFacing = nf;
            }
        }
        vx = h.X.Raw * spd >> Fixed.Shift;
        vy = h.Y.Raw * spd >> Fixed.Shift;
        Project(m, i, ref vx, ref vy);
        m.VX[i] = vx;
        m.VY[i] = vy;
    }

    // Blocked reports whether a body of movement class cls can't enter cell (cx, cy): rock or a
    // building in the grid, off the map, or terrain its class can't cross (a cliff, steep ground
    // for wheels, dense forest for heavy vehicles).
    private bool Blocked(int cx, int cy, int cls)
    {
        var c = new Cell(cx, cy);
        if (!Grid.Passable(c))
        {
            return true;
        }
        return cls >= 0 && Ground is { } g && g.Cost(cls, c) == TerrainMap.Impassable;
    }

    // PushOut moves body i's circle (its radius is the clearance) out of blocked cells and
    // building boxes, in a fixed order, so it slides along walls instead of entering them.
    private void PushOut(MoveScratch m, int i)
    {
        long r = m.R[i];
        var cls = m.Cls[i];
        for (var pass = 0; pass < 2; pass++)
        {
            var moved = false;
            long x = m.X[i], y = m.Y[i];
            int cx0 = (int)((x - r) >> Fixed.Shift), cx1 = (int)((x + r) >> Fixed.Shift);
            int cy0 = (int)((y - r) >> Fixed.Shift), cy1 = (int)((y + r) >> Fixed.Shift);
            for (var cy = cy0; cy <= cy1; cy++)
            {
                for (var cx = cx0; cx <= cx1; cx++)
                {
                    if (Blocked(cx, cy, cls))
                    {
                        long bx = (long)cx << Fixed.Shift, by = (long)cy << Fixed.Shift;
                        moved |= OutOfBox(m, i, r, bx, by, bx + Fixed.OneRaw, by + Fixed.OneRaw);
                    }
                }
            }
            foreach (var (bx0, by0, bx1, by1) in m.Boxes)
            {
                if (m.X[i] + r > bx0 && m.X[i] - r < bx1 && m.Y[i] + r > by0 && m.Y[i] - r < by1)
                {
                    moved |= OutOfBox(m, i, r, bx0, by0, bx1, by1);
                }
            }
            if (!moved)
            {
                break;
            }
        }
    }

    // OutOfBox pushes body i's circle (radius r) out of the box [x0, x1] x [y0, y1]; reports
    // whether it moved.
    private static bool OutOfBox(MoveScratch m, int i, long r, long x0, long y0, long x1, long y1)
    {
        long x = m.X[i], y = m.Y[i];
        var qx = Math.Clamp(x, x0, x1);
        var qy = Math.Clamp(y, y0, y1);
        long dx = x - qx, dy = y - qy;
        var d2 = dx * dx + dy * dy;
        if (d2 >= r * r)
        {
            return false;
        }
        if (d2 == 0)
        {
            // The centre is inside: leave through the nearest side (ties in a fixed order).
            long l = x - x0, rt = x1 - x, t = y - y0, b = y1 - y;
            var min = Math.Min(Math.Min(l, rt), Math.Min(t, b));
            if (min == l)
            {
                m.X[i] = x0 - r;
            }
            else if (min == rt)
            {
                m.X[i] = x1 + r;
            }
            else if (min == t)
            {
                m.Y[i] = y0 - r;
            }
            else
            {
                m.Y[i] = y1 + r;
            }
            return true;
        }
        var d = Fixed.Sqrt(d2);
        var push = r - d + 1;
        m.X[i] = x + dx * push / Math.Max(d, 1);
        m.Y[i] = y + dy * push / Math.Max(d, 1);
        return true;
    }

    // ---- 4. Collision ---------------------------------------------------------------------

    private void Collide(MoveScratch m)
    {
        for (var round = 0; round < CollisionRounds; round++)
        {
            var any = false;
            m.Worst = 0;
            for (var p = 0; p < m.Pairs; p++)
            {
                any |= Separate(m, m.PairA[p], m.PairB[p]);
            }
            if (!any || (round + 1 >= MinRounds && m.Worst <= RoundSlack))
            {
                break;
            }
            // Walls and buildings take part in every round: whatever was pushed into one comes out.
            for (var i = 0; i < m.N; i++)
            {
                if ((m.Flags[i] & MoveScratch.Pushed) != 0)
                {
                    m.Flags[i] &= unchecked((byte)~MoveScratch.Pushed);
                    if (m.Near[i] < NearTouch)
                    {
                        PushOut(m, i);
                    }
                }
            }
        }
    }

    // Separate resolves the overlap of bodies a and b (a before b) by sharing it between them,
    // moving both at once (pairs are visited in ID order, Gauss-Seidel); reports whether they
    // overlapped.
    private bool Separate(MoveScratch m, int a, int b)
    {
        var dx = m.X[b] - m.X[a];
        var dy = m.Y[b] - m.Y[a];
        long touch = m.R[a] + m.R[b];
        var d2 = dx * dx + dy * dy;
        if (d2 >= touch * touch)
        {
            return false;
        }
        var d = Fixed.Sqrt(d2);
        if (d == 0)
        {
            // Exactly on top of each other: split along a direction picked from their IDs.
            var dir = Facing.Dir((int)((m.Id[a] * 7 + m.Id[b] * 13) % Facing.Count));
            dx = dir.X.Raw;
            dy = dir.Y.Raw;
            d = Fixed.OneRaw;
        }
        var overlap = touch - d;
        m.Worst = Math.Max(m.Worst, overlap * 100 / Math.Max(1, Math.Min(m.R[a], m.R[b])));
        long ma = m.Mass[a], mb = m.Mass[b];
        bool fa = (m.Flags[a] & MoveScratch.Anchored) != 0, fb = (m.Flags[b] & MoveScratch.Anchored) != 0;
        bool sa = (m.Flags[a] & MoveScratch.Moving) == 0, sb = (m.Flags[b] & MoveScratch.Moving) == 0;
        if (m.Owner[a] != m.Owner[b])
        {
            // Enemies don't push: one standing still holds its ground against one moving.
            fa |= sa && !sb;
            fb |= sb && !sa;
        }
        else
        {
            // An idle friendly gives way to a moving one, unless it is hemmed in by a crowd:
            // then the moving one takes most of it.
            if (sa && !sb)
            {
                ma = m.Contacts[a] >= HemmedIn ? ma * 4 : Math.Max(1, ma / 4);
            }
            if (sb && !sa)
            {
                mb = m.Contacts[b] >= HemmedIn ? mb * 4 : Math.Max(1, mb / 4);
            }
        }
        // A body pressed against a wall (or a building) in the direction it would be pushed can't
        // give way: the other one takes the whole correction.
        var wa = !fa && m.Near[a] < NearTouch && WallBehind(m, a, -dx, -dy, d);
        var wb = !fb && m.Near[b] < NearTouch && WallBehind(m, b, dx, dy, d);
        if (!fa && !fb && wa != wb)
        {
            fa = wa;
            fb = wb;
        }
        if (fa && fb)
        {
            return true;
        }
        long shareA = fa ? 0 : fb ? 1024 : mb * 1024 / (ma + mb);
        var shareB = fb ? 0 : 1024 - shareA;
        // Push a away from b and b away from a, along the line between them.
        var ax = dx * overlap * shareA / (d * 1024);
        var ay = dy * overlap * shareA / (d * 1024);
        var bxv = dx * overlap * shareB / (d * 1024);
        var byv = dy * overlap * shareB / (d * 1024);
        if (shareA > 0)
        {
            m.X[a] -= ax;
            m.Y[a] -= ay;
            m.Flags[a] |= MoveScratch.Pushed;
        }
        if (shareB > 0)
        {
            m.X[b] += bxv;
            m.Y[b] += byv;
            m.Flags[b] |= MoveScratch.Pushed;
        }
        return true;
    }

    // WallAhead reports whether the point at distance reach from body i in direction (dx, dy)
    // (length d) is blocked for it.
    private bool WallAhead(MoveScratch m, int i, long dx, long dy, long d, long reach)
    {
        var x = m.X[i] + dx * reach / d;
        var y = m.Y[i] + dy * reach / d;
        if (Blocked((int)(x >> Fixed.Shift), (int)(y >> Fixed.Shift), m.Cls[i]))
        {
            return true;
        }
        foreach (var (x0, y0, x1, y1) in m.Boxes)
        {
            if (x >= x0 && x <= x1 && y >= y0 && y <= y1)
            {
                return true;
            }
        }
        return false;
    }

    // WallBehind reports whether the point just beyond body i's edge in direction (dx, dy)
    // (length d) is blocked for it.
    private bool WallBehind(MoveScratch m, int i, long dx, long dy, long d) => WallAhead(m, i, dx, dy, d, m.R[i] + 128);

    // ---- 5. Write back --------------------------------------------------------------------

    private static void Finish(MoveScratch m, int i)
    {
        var u = m.U[i];
        var nx = m.X[i];
        var ny = m.Y[i];
        u.Vel = new Vec(Fixed.FromRaw(nx - m.X0[i]), Fixed.FromRaw(ny - m.Y0[i]));
        if (nx != m.X0[i] || ny != m.Y0[i])
        {
            u.Pos = new Vec(Fixed.FromRaw(nx), Fixed.FromRaw(ny));
        }
        var intent = u.Intent;
        if ((m.Flags[i] & MoveScratch.Moving) == 0)
        {
            u.StallTicks = 0;
            if (intent.Stuck && !intent.Arrived)
            {
                u.Intent = intent with { Stuck = false };
            }
            return;
        }
        var gx = intent.Goal.X.Raw - nx;
        var gy = intent.Goal.Y.Raw - ny;
        var dist = Fixed.Sqrt(gx * gx + gy * gy);
        var tol = Math.Max(m.R[i] / 2, 64);
        if (dist <= tol)
        {
            u.Intent = intent with { Arrived = true, Stuck = false };
            u.StallTicks = 0;
            return;
        }
        if (dist + ProgressEps <= u.StallBest.Raw)
        {
            u.StallBest = Fixed.FromRaw(dist);
            u.StallTicks = 0;
            if (intent.Stuck)
            {
                u.Intent = intent with { Stuck = false };
            }
            return;
        }
        if (u.StallTicks < 10_000)
        {
            u.StallTicks++;
        }
        if (u.StallTicks >= StuckTicks && !intent.Stuck)
        {
            u.Intent = intent with { Stuck = true };
        }
    }

    /// <summary>
    /// Measures the last movement step, for tools and tests (not game state; reads only the
    /// movement system's scratch): the deepest overlap left between two units that may touch,
    /// in percent of the smaller radius (a soldier and the vehicle it is boarding excluded), and
    /// how many pairs overlap by 10% or more, and how many units are flagged Stuck.
    /// </summary>
    public (int MaxOverlapPercent, int PairsOver10, int Stuck) MovementStats()
    {
        var m = _mv;
        long worst = 0;
        var over10 = 0;
        for (var p = 0; p < m.Pairs; p++)
        {
            int a = m.PairA[p], b = m.PairB[p];
            var dx = m.X[b] - m.X[a];
            var dy = m.Y[b] - m.Y[a];
            long touch = m.R[a] + m.R[b];
            var d2 = dx * dx + dy * dy;
            if (d2 >= touch * touch)
            {
                continue;
            }
            var over = (touch - Fixed.Sqrt(d2)) * 100 / Math.Max(1, Math.Min(m.R[a], m.R[b]));
            over10 += over >= 10 ? 1 : 0;
            if (over > worst)
            {
                worst = over;
                MovementWorst = $"{m.U[a].Id}:{m.Flags[a]}:{m.U[a].Intent.Arrived} {m.U[b].Id}:{m.Flags[b]}:{m.U[b].Intent.Arrived} at {m.X[a] >> 10},{m.Y[a] >> 10}";
            }
        }
        var stuck = 0;
        for (var i = 0; i < m.N; i++)
        {
            stuck += m.U[i].Intent.Stuck ? 1 : 0;
        }
        return ((int)worst, over10, stuck);
    }
}
