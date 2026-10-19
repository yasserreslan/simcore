using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Tunnel entrances and the ground (docs/design.md, "Tunnels"). Placing an entrance reshapes the
// terrain like a building pad, in its footprint's frame (x across its front, y from its back to its
// mouth): the footprint and an apron in front of it are flat at the mean height of that ground;
// the headwall covers the first Depth rows: the ground behind its back row stands at the mound
// height (earth over the portal) and falls to the floor across its first row (hidden inside the
// headwall), so the portal's recess in its last row has flat ground under it (a heightfield has no
// overhangs); with walls, the outer columns are retaining walls falling from the mound height at
// the back to nothing at the mouth; around all of that the ground eases back to the land over the
// blend cells. The model is built for exactly that shape, so its edges meet the terrain. The mound height depends on the look
// picked from the land: built into a hillside when the ground behind the site rises by the
// archetype's hill grade or more (within 8 cells), otherwise standing alone. Once the entrance is gone the ground
// steps back to what it was, a step a second; a corner something else changed meanwhile is left alone.
public sealed partial class World
{
    // TunnelShape is where an entrance would go: its frame, its base height, its look.
    private readonly record struct TunnelShape(Cell Origin, int Size, int Facing, int Base, int Variant);

    // ShapeOf works out the base height and the look of an entrance of def at origin c turned by facing.
    private TunnelShape ShapeOf(UnitDef def, Cell c, int facing)
    {
        var t = def.Archetype.Tunnel!;
        var s = def.Archetype.FootprintW;
        if (Ground is not { } g)
        {
            return new TunnelShape(c, s, facing, 0, 0);
        }
        long sum = 0, n = 0;
        for (var j = 0; j <= s + t.Apron; j++)
        {
            for (var i = 0; i <= s; i++)
            {
                var (x, y) = LocalCorner(c, s, facing, i, j);
                sum += g.Corner(x, y).Raw;
                n++;
            }
        }
        var b = (int)(sum / n);
        // The land behind: the steepest mean rise over the rows 2 to 8 cells behind the back edge.
        var variant = 0;
        for (var j = -8; j <= -2 && variant == 0; j++)
        {
            long row = 0, m = 0;
            for (var i = 0; i <= s; i++)
            {
                var (x, y) = LocalCorner(c, s, facing, i, j);
                if (x >= 0 && y >= 0 && x <= g.Width && y <= g.Height)
                {
                    row += g.Corner(x, y).Raw;
                    m++;
                }
            }
            if (m > 0 && (row / m - b) * 100 >= (long)t.HillGrade * -j * Fixed.OneRaw)
            {
                variant = 1;
            }
        }
        return new TunnelShape(c, s, facing, b, variant);
    }

    // TemplateAt is the shaped height of footprint-local corner (i, j) inside the shaped box
    // [0, s] x [0, s + apron].
    private static int TemplateAt(TunnelSpec t, TunnelShape sh, int i, int j)
    {
        var s = sh.Size;
        var mound = sh.Variant == 1 ? t.HillMound : t.Mound;
        if (j > s || mound == 0)
        {
            return sh.Base; // the apron, or a flat hatch
        }
        if (j <= t.Depth - 2)
        {
            return sh.Base + mound; // the earth over the headwall's back
        }
        if (!t.Walls && (i == 0 || i == s))
        {
            return sh.Base + mound; // no wing walls (a hatch in a slope): the earth stands to its face
        }
        if (t.Walls && (i == 0 || i == s))
        {
            return sh.Base + (int)((long)mound * (s - j) / (s - t.Depth + 2)); // the wing walls' tops
        }
        return sh.Base; // the ramp, and the floor under the headwall's recess
    }

    // The corner box an entrance's shape touches, in footprint-local corners.
    private static (int I0, int J0, int I1, int J1) LocalBox(TunnelSpec t, int s) =>
        (-t.Blend, -t.Blend, s + t.Blend, s + t.Apron + t.Blend);

    // MapBox returns the box of map cells covering local corners (i0, j0)-(i1, j1).
    private static (int X0, int Y0, int X1, int Y1) MapBox(Cell o, int s, int facing, int i0, int j0, int i1, int j1)
    {
        var (ax, ay) = LocalCorner(o, s, facing, i0, j0);
        var (bx, by) = LocalCorner(o, s, facing, i1, j1);
        return (Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
    }

    // ShapeGround reshapes the terrain for an entrance (see the top of this file) and returns the
    // record of what it changed, for the ground to come back later.
    private TunnelGround? ShapeGround(UnitDef def, TunnelShape sh)
    {
        if (Ground is not { } g)
        {
            return null;
        }
        var t = def.Archetype.Tunnel!;
        var s = sh.Size;
        var (i0, j0, i1, j1) = LocalBox(t, s);
        var (x0, y0, x1, y1) = MapBox(sh.Origin, s, sh.Facing, i0, j0, i1, j1);
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        SnapRestores(x0, y0, x1, y1);
        var orig = g.ReadCorners(x0, y0, cw, ch);
        var cut = (int[])orig.Clone();
        var mask = new bool[cw * ch];
        for (var j = j0; j <= j1; j++)
        {
            for (var i = i0; i <= i1; i++)
            {
                var (x, y) = LocalCorner(sh.Origin, s, sh.Facing, i, j);
                var k = (y - y0) * cw + (x - x0);
                var ci = Math.Clamp(i, 0, s);
                var cj = Math.Clamp(j, 0, s + t.Apron);
                var d = Math.Max(Math.Abs(i - ci), Math.Abs(j - cj));
                var want = TemplateAt(t, sh, ci, cj);
                cut[k] = d == 0 ? want : orig[k] + (int)((long)(want - orig[k]) * (t.Blend + 1 - d) / (t.Blend + 1));
                mask[k] = true;
            }
        }
        g.WriteCorners(x0, y0, cw, ch, cut, mask);
        // Pad ground on the footprint and apron; no trees on the shaped ground.
        var (px0, py0, px1, py1) = MapBox(sh.Origin, s, sh.Facing, 0, 0, s, s + t.Apron);
        for (var y = py0; y < py1; y++)
        {
            for (var x = px0; x < px1; x++)
            {
                g.SetPadFlag(new Cell(x, y), true);
            }
        }
        for (var y = y0 - 1; y <= y1; y++)
        {
            for (var x = x0 - 1; x <= x1; x++)
            {
                if (g.Forest(new Cell(x, y)) != 0)
                {
                    g.SetForest(new Cell(x, y), 0);
                }
            }
        }
        return new TunnelGround
        {
            X0 = x0, Y0 = y0, W = cw, H = ch, Orig = orig, Cut = cut,
            PadX0 = px0, PadY0 = py0, PadW = px1 - px0, PadH = py1 - py0,
        };
    }

    // PlaceTunnel creates an entrance of def (a site, or finished when built) with its ground shaped.
    private Unit PlaceTunnel(UnitDef def, Cell c, int facing, bool built)
    {
        var sh = ShapeOf(def, c, facing);
        var rec = ShapeGround(def, sh);
        var e = Add(def, FootprintCenter(def, c), facing);
        e.Variant = sh.Variant;
        if (!built)
        {
            var a = def.Archetype;
            e.Construction = ConstructionState.Building;
            e.Work = 0;
            e.HP = Math.Max(1, def.MaxHp * a.Construction!.StartHp / 100);
            e.Stock = 0;
        }
        if (rec is not null)
        {
            rec.Entrance = e.Id;
            _tunnelGround.Add(rec);
        }
        return e;
    }

    // StartRestore starts the ground of entrance id returning to its old shape.
    private void StartRestore(uint id)
    {
        foreach (var r in _tunnelGround)
        {
            if (r.Entrance == id && r.Steps == 0 && Unit(id) is { } e)
            {
                r.Entrance = 0;
                r.Steps = Math.Max(1, e.Def.Archetype.Tunnel!.RestoreTicks / RestoreEvery);
                r.Wait = RestoreEvery;
            }
        }
    }

    // RunRestores moves the returning ground a step back, a step a second.
    private void RunRestores()
    {
        for (var i = 0; i < _tunnelGround.Count; i++)
        {
            var r = _tunnelGround[i];
            if (r.Steps == 0 || --r.Wait > 0)
            {
                continue;
            }
            r.Wait = RestoreEvery;
            StepRestore(r, r.Step + 1);
            if (r.Step >= r.Steps)
            {
                FinishRestore(r);
                _tunnelGround.RemoveAt(i);
                i--;
            }
        }
    }

    // StepRestore moves record r's ground to step k of its way back (only corners still as it left them).
    private void StepRestore(TunnelGround r, int k)
    {
        var g = Ground!;
        var cur = g.ReadCorners(r.X0, r.Y0, r.W, r.H);
        var next = new int[cur.Length];
        var mask = new bool[cur.Length];
        for (var i = 0; i < cur.Length; i++)
        {
            var was = r.Cut[i] + (int)((long)(r.Orig[i] - r.Cut[i]) * r.Step / r.Steps);
            next[i] = r.Cut[i] + (int)((long)(r.Orig[i] - r.Cut[i]) * k / r.Steps);
            mask[i] = cur[i] == was && next[i] != cur[i];
        }
        g.WriteCorners(r.X0, r.Y0, r.W, r.H, next, mask);
        r.Step = k;
    }

    // FinishRestore takes the pad off the ground that came back (unless a structure stands there now).
    private void FinishRestore(TunnelGround r)
    {
        var g = Ground!;
        for (var y = r.PadY0; y < r.PadY0 + r.PadH; y++)
        {
            for (var x = r.PadX0; x < r.PadX0 + r.PadW; x++)
            {
                if (g.Pad(new Cell(x, y)) && !UnderStructure(new Cell(x, y)))
                {
                    g.SetPadFlag(new Cell(x, y), false);
                }
            }
        }
    }

    private bool UnderStructure(Cell c)
    {
        foreach (var u in _units)
        {
            if (u.Def.IsStructure && u.Alive)
            {
                var o = FootprintOrigin(u);
                if (c.X >= o.X && c.Y >= o.Y && c.X < o.X + FootW(u) && c.Y < o.Y + FootH(u))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // SnapRestores finishes at once the returning ground overlapping a corner box (a new entrance there).
    private void SnapRestores(int x0, int y0, int x1, int y1)
    {
        for (var i = 0; i < _tunnelGround.Count; i++)
        {
            var r = _tunnelGround[i];
            if (r.Steps == 0 || r.X0 > x1 || r.Y0 > y1 || r.X0 + r.W - 1 < x0 || r.Y0 + r.H - 1 < y0)
            {
                continue;
            }
            StepRestore(r, r.Steps);
            FinishRestore(r);
            _tunnelGround.RemoveAt(i);
            i--;
        }
    }

    /// <summary>
    /// The look an entrance of def would get at origin c turned by facing (see <see cref="Unit.Variant"/>):
    /// 1 built into a hillside, 0 standing alone. For placement previews.
    /// </summary>
    public int TunnelVariantAt(UnitDef def, Cell c, int facing) => def.Archetype.Tunnel is null ? 0 : ShapeOf(def, c, facing).Variant;

    /// <summary>
    /// The quarter facing (0, 8, 16, 24; 0 = mouth south) an entrance of def at origin c should take:
    /// the one whose land behind rises most (facing downhill), ties to the lower facing.
    /// </summary>
    public int TunnelFacingAt(UnitDef def, Cell c)
    {
        if (def.Archetype.Tunnel is null || Ground is not { } g)
        {
            return 0;
        }
        var s = def.Archetype.FootprintW;
        var best = 0;
        long bestRise = long.MinValue;
        for (var f = 0; f < FixedPoint.Facing.Count; f += FixedPoint.Facing.Count / 4)
        {
            long front = 0, back = 0;
            for (var i = 0; i <= s; i++)
            {
                var (fx, fy) = LocalCorner(c, s, f, i, s + 2);
                var (bx, by) = LocalCorner(c, s, f, i, -2);
                front += g.Corner(fx, fy).Raw;
                back += g.Corner(bx, by).Raw;
            }
            if (back - front > bestRise)
            {
                (best, bestRise) = (f, back - front);
            }
        }
        return best;
    }

    // CanPlaceTunnel is CanPlace for a tunnel entrance: its footprint and apron on the map, passable
    // and flat enough (or walkable for its builders), no unit in the footprint but the builders, no
    // structure on its shaped ground, and no other entrance closer than its spacing or with shaped
    // ground overlapping.
    private bool CanPlaceTunnel(UnitDef def, Cell c, int facing, IReadOnlyList<Unit> builders)
    {
        var t = def.Archetype.Tunnel!;
        var s = def.Archetype.FootprintW;
        if (facing < 0 || facing >= FixedPoint.Facing.Count || facing % (FixedPoint.Facing.Count / 4) != 0)
        {
            return false;
        }
        var k = Ground is { } g0 && builders.Count > 0 ? g0.Rules.ClassOf(builders[0].Def) : -1;
        var (px0, py0, px1, py1) = MapBox(c, s, facing, 0, 0, s, s + t.Apron);
        for (var y = py0; y < py1; y++)
        {
            for (var x = px0; x < px1; x++)
            {
                var cell = new Cell(x, y);
                if (!Grid.Contains(cell) || !Grid.Passable(cell))
                {
                    return false;
                }
                if (Ground is { } g && (t.FlatGrade > 0 ? g.Grade(cell) > t.FlatGrade : k >= 0 ? !g.Passable(k, cell) : g.Grade(cell) > 40))
                {
                    return false;
                }
            }
        }
        var (i0, j0, i1, j1) = LocalBox(t, s);
        var (bx0, by0, bx1, by1) = MapBox(c, s, facing, i0, j0, i1, j1); // corners: cells bx0..bx1-1
        var center = FootprintCenter(def, c);
        foreach (var u in _units)
        {
            if (u.Def.IsStructure && u.Alive)
            {
                var o = FootprintOrigin(u);
                int w = FootW(u), h = FootH(u);
                if (o.X < bx1 && bx0 < o.X + w && o.Y < by1 && by0 < o.Y + h)
                {
                    return false; // a structure on the ground it would shape
                }
                if (u.Def.Archetype.Tunnel is { } ot &&
                    Within(u.Pos, center, ot.Spacing > t.Spacing ? ot.Spacing : t.Spacing))
                {
                    return false;
                }
                continue;
            }
            var at = Cell.Of(u.Pos);
            var fo = MapBox(c, s, facing, 0, 0, s, s);
            if (u.Def.Layer == Layers.Ground && u.Inside == 0 && u.Garrison == 0 &&
                at.X >= fo.X0 && at.X < fo.X1 && at.Y >= fo.Y0 && at.Y < fo.Y1 && !Contains(builders, u))
            {
                return false;
            }
        }
        foreach (var r in _tunnelGround)
        {
            if (r.Steps == 0 && r.X0 <= bx1 && r.Y0 <= by1 && r.X0 + r.W - 1 >= bx0 && r.Y0 + r.H - 1 >= by0)
            {
                return false; // another live entrance's shaped ground
            }
        }
        return true;
    }
}
