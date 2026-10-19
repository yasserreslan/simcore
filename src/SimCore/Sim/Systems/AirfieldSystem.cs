using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The airfield system, milestone M1 (the game repo's docs/airfield-design.md, section 5): the
// static, buildable airfield. A worker builds the facility, which founds an airfield and reserves
// its first block's plan (the minimum runway and row (0, 0)) against every player's structures.
// With the facility standing, rows and runway segments snap to the type's grid next to what
// exists: a row touches the airfield and has the row below it, its lane mouth over the taxiway;
// the first row lays the minimum runway on its one bar; segments extend either end while the
// approach corridor stays on the map. Every piece goes through the ordinary construction flow; a
// finished row spawns its stations. All pieces share the airfield's pad height, and pavement
// becomes road on terrain. Flight, landing, taxi, service, damage and craters come later (M2, M3):
// the station records already carry their occupant slots, and TaxiGraph derives the taxi network.
public sealed partial class World
{
    private readonly List<Airfield> _airfields = []; // sorted by ID (IDs only grow)
    private readonly List<uint> _gonePieces = [];     // scratch

    /// <summary>The airfields, sorted by ID.</summary>
    public IReadOnlyList<Airfield> Airfields => _airfields;

    /// <summary>Returns the airfield with this ID, or null.</summary>
    public Airfield? Airfield(uint id)
    {
        foreach (var af in _airfields)
        {
            if (af.Id == id)
            {
                return af;
            }
        }
        return null;
    }

    /// <summary>Returns the airfield a piece belongs to, or null.</summary>
    public Airfield? AirfieldOf(Unit u) => u.Airfield == 0 ? null : Airfield(u.Airfield);

    // ---- The airfield's frame ----

    // Turn maps a local vector to the map: a quarter turn clockwise on screen (y down) per 8 facings.
    private static (long X, long Y) Turn(int facing, long x, long y) => (FixedPoint.Facing.Wrap(facing) / 8) switch
    {
        0 => (x, y),
        1 => (-y, x),
        2 => (-x, -y),
        _ => (y, -x),
    };

    // MapRect is the map rectangle (top-left cell, width, height) of a local cell rectangle.
    private static AirfieldRect MapRect(AirfieldRole role, Cell origin, int facing, (int X0, int Y0, int X1, int Y1) r, bool corridor = false)
    {
        var (ax, ay) = Turn(facing, r.X0, r.Y0);
        var (bx, by) = Turn(facing, r.X1, r.Y1);
        return new AirfieldRect(role, new Cell(origin.X + (int)Math.Min(ax, bx), origin.Y + (int)Math.Min(ay, by)),
            (int)Math.Abs(bx - ax), (int)Math.Abs(by - ay), corridor);
    }

    private static (int, int, int, int) FacilityLocal(AirfieldType t, Archetype fac) =>
        (-t.FacilityGapCells - fac.FootprintW, t.SegmentDepth, -t.FacilityGapCells, t.SegmentDepth + fac.FootprintH);

    private static (int, int, int, int) RowLocal(AirfieldType t, int col, int row) =>
        (t.RowW * col, t.SegmentDepth + t.RowH * row, t.RowW * (col + 1), t.SegmentDepth + t.RowH * (row + 1));

    private static (int, int, int, int) SegmentLocal(AirfieldType t, int k) =>
        (t.SegmentCells * k, 0, t.SegmentCells * (k + 1), t.SegmentDepth);

    private static (int, int, int, int) StationLocal(AirfieldType t, int col, int row, RowSlot s) =>
        (t.RowW * col + s.X, t.SegmentDepth + t.RowH * row, t.RowW * col + s.X + t.StationCells, t.SegmentDepth + t.RowH * (row + 1));

    // The approach corridors beyond a runway from segment west to segment east, on the runway strip.
    private static AirfieldRect WestCorridor(AirfieldType t, Cell origin, int facing, int west) =>
        MapRect(AirfieldRole.Segment, origin, facing, (t.SegmentCells * west - t.CorridorCells, 0, t.SegmentCells * west, t.RunwayCells), true);

    private static AirfieldRect EastCorridor(AirfieldType t, Cell origin, int facing, int east) =>
        MapRect(AirfieldRole.Segment, origin, facing, (t.SegmentCells * (east + 1), 0, t.SegmentCells * (east + 1) + t.CorridorCells, t.RunwayCells), true);

    /// <summary>The top-left map cell of an airfield's row slot (column, row), as a Build command gives it.</summary>
    public static Cell AirfieldRowCell(Airfield af, int col, int row) =>
        MapRect(AirfieldRole.Row, af.Origin, af.Facing, RowLocal(af.Type, col, row)).Cell;

    /// <summary>The top-left map cell of an airfield's runway segment slot k (West - 1 and East + 1 extend it).</summary>
    public static Cell AirfieldSegmentCell(Airfield af, int k) =>
        MapRect(AirfieldRole.Segment, af.Origin, af.Facing, SegmentLocal(af.Type, k)).Cell;

    /// <summary>A point of an airfield's frame (tenths of a cell from its local origin) on the map.</summary>
    public static Vec AirfieldPoint(Airfield af, int tenthsX, int tenthsY)
    {
        var (x, y) = Turn(af.Facing, Fixed.Frac(tenthsX, 10).Raw, Fixed.Frac(tenthsY, 10).Raw);
        return new Vec(Fixed.FromRaw((long)af.Origin.X * Fixed.OneRaw + x), Fixed.FromRaw((long)af.Origin.Y * Fixed.OneRaw + y));
    }

    // FoundOrigin is the local origin of an airfield whose facility has c as its top-left cell.
    private static Cell FoundOrigin(AirfieldType t, Archetype fac, Cell c, int facing)
    {
        var r = MapRect(AirfieldRole.Facility, default, facing, FacilityLocal(t, fac));
        return new Cell(c.X - r.Cell.X, c.Y - r.Cell.Y);
    }

    // PlanRects is a founding plan: the facility, the minimum runway and row (0, 0).
    private static List<AirfieldRect> PlanRects(AirfieldType t, Archetype fac, Cell origin, int facing)
    {
        var list = new List<AirfieldRect> { MapRect(AirfieldRole.Facility, origin, facing, FacilityLocal(t, fac)) };
        for (var k = 0; k < t.MinSegments; k++)
        {
            list.Add(MapRect(AirfieldRole.Segment, origin, facing, SegmentLocal(t, k)));
        }
        list.Add(MapRect(AirfieldRole.Row, origin, facing, RowLocal(t, 0, 0)));
        return list;
    }

    /// <summary>
    /// The plan of an airfield its facility def would found with c its top-left cell, turned by
    /// facing: the facility, the minimum runway, row (0, 0) and the two approach corridors (for the
    /// build ghost). Empty for anything but a facility.
    /// </summary>
    public IReadOnlyList<AirfieldRect> AirfieldPlan(UnitDef facility, Cell c, int facing)
    {
        if (facility.Archetype.Airfield is not { Role: AirfieldRole.Facility } p)
        {
            return [];
        }
        var t = Content.AirfieldTypes[p.Type];
        var origin = FoundOrigin(t, facility.Archetype, c, facing);
        var list = PlanRects(t, facility.Archetype, origin, facing);
        list.Add(WestCorridor(t, origin, facing, 0));
        list.Add(EastCorridor(t, origin, facing, t.MinSegments - 1));
        return list;
    }

    // ---- Placement rules ----

    // A resolved airfield piece: where it goes and what it does there.
    private sealed record PiecePlan(AirfieldRole Role, Airfield? Field, AirfieldType Type, Cell Origin, int Facing, long Pad,
        AirfieldRect Rect, int Col, int Row, int Seg);

    private static bool Overlaps(AirfieldRect a, Cell c, int w, int h) =>
        a.Cell.X < c.X + w && c.X < a.Cell.X + a.W && a.Cell.Y < c.Y + h && c.Y < a.Cell.Y + a.H;

    // Reserved reports whether a w x h rectangle at c overlaps the first-block plan of an airfield
    // other than own: every player's structures keep off it.
    private bool Reserved(Cell c, int w, int h, Airfield? own)
    {
        foreach (var af in _airfields)
        {
            if (af == own)
            {
                continue;
            }
            var t = af.Type;
            for (var k = 0; k < t.MinSegments; k++)
            {
                if (Overlaps(MapRect(AirfieldRole.Segment, af.Origin, af.Facing, SegmentLocal(t, k)), c, w, h))
                {
                    return true;
                }
            }
            if (Overlaps(MapRect(AirfieldRole.Row, af.Origin, af.Facing, RowLocal(t, 0, 0)), c, w, h))
            {
                return true;
            }
        }
        return false;
    }

    private bool OnMap(AirfieldRect r) => r.Cell.X >= 0 && r.Cell.Y >= 0 && r.Cell.X + r.W <= Grid.Width && r.Cell.Y + r.H <= Grid.Height;

    // PieceGround: every cell on the map and passable; on terrain Flat or Gentle for movement class k
    // (k < 0: not checked) and every corner within the type's relief of the pad height.
    private bool PieceGround(AirfieldRect r, int k, long pad, long maxRelief)
    {
        if (!OnMap(r))
        {
            return false;
        }
        for (var y = r.Cell.Y; y < r.Cell.Y + r.H; y++)
        {
            for (var x = r.Cell.X; x < r.Cell.X + r.W; x++)
            {
                var cell = new Cell(x, y);
                if (!Grid.Passable(cell))
                {
                    return false;
                }
                if (Ground is { } g && k >= 0 && g.Slope(k, cell) is not (SlopeClass.Flat or SlopeClass.Gentle))
                {
                    return false;
                }
            }
        }
        if (Ground is { } gr)
        {
            for (var y = r.Cell.Y; y <= r.Cell.Y + r.H; y++)
            {
                for (var x = r.Cell.X; x <= r.Cell.X + r.W; x++)
                {
                    if (Math.Abs(gr.Corner(x, y).Raw - pad) > maxRelief)
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    // PieceFree: no structure on the rectangle and no other airfield's plan.
    private bool PieceFree(AirfieldRect r, Airfield? own)
    {
        foreach (var u in _units)
        {
            if (u.Def.IsStructure)
            {
                var o = FootprintOrigin(u);
                if (o.X < r.Cell.X + r.W && r.Cell.X < o.X + FootW(u) && o.Y < r.Cell.Y + r.H && r.Cell.Y < o.Y + FootH(u))
                {
                    return false;
                }
            }
        }
        return !Reserved(r.Cell, r.W, r.H, own);
    }

    // UnitsIn reports whether a ground unit other than the builders stands in the rectangle.
    private bool UnitsIn(AirfieldRect r, IReadOnlyList<Unit> builders)
    {
        foreach (var u in _units)
        {
            var at = Cell.Of(u.Pos);
            if (!u.Def.IsStructure && u.Def.Layer == Layers.Ground && u.Inside == 0 && u.Garrison == 0 &&
                at.X >= r.Cell.X && at.X < r.Cell.X + r.W && at.Y >= r.Cell.Y && at.Y < r.Cell.Y + r.H && !Contains(builders, u))
            {
                return true;
            }
        }
        return false;
    }

    // PadOf is the pad height of a founding plan: the mean of its rectangles' corners (0 on a flat map).
    private long PadOf(List<AirfieldRect> rects)
    {
        if (Ground is not { } g)
        {
            return 0;
        }
        long sum = 0, n = 0;
        foreach (var r in rects)
        {
            for (var y = r.Cell.Y; y <= r.Cell.Y + r.H; y++)
            {
                for (var x = r.Cell.X; x <= r.Cell.X + r.W; x++)
                {
                    sum += g.Corner(x, y).Raw;
                    n++;
                }
            }
        }
        return sum / n;
    }

    private bool Standing(uint id) => Unit(id) is { Alive: true, IsBuilt: true };

    // PieceDef is the owner's definition of an archetype, or null (not in its faction).
    private UnitDef? PieceDef(byte owner, int archetype) => Match.Players[owner - 1].Find(Content, Content.Archetypes[archetype].Id);

    // ResolvePiece checks an airfield piece the owner's builders would place with c its top-left
    // cell (k: the builders' movement class, -1 to skip slopes; spawn: a scenario placing it
    // finished, units may stand there). Rows and segments snap to one of the owner's airfields of
    // the piece's type; the command's facing only matters for a facility (the field turns with it).
    private PiecePlan? ResolvePiece(UnitDef def, Cell c, int facing, byte owner, IReadOnlyList<Unit> builders, bool spawn)
    {
        var spec = def.Archetype.Airfield!;
        var t = Content.AirfieldTypes[spec.Type];
        var k = Ground is { } g0 && builders.Count > 0 ? g0.Rules.ClassOf(builders[0].Def) : -1;
        switch (spec.Role)
        {
            case AirfieldRole.Facility:
                return ResolveFacility(def, t, c, facing, owner, builders, k, spawn);
            case AirfieldRole.Row:
                foreach (var af in _airfields)
                {
                    if (af.Owner == owner && af.Type == t && SnapRow(af, c, out var col, out var row) && ResolveRow(af, col, row, k) is { } p)
                    {
                        return p;
                    }
                }
                return null;
            case AirfieldRole.Segment:
                foreach (var af in _airfields)
                {
                    if (af.Owner == owner && af.Type == t && ResolveSegment(af, c, k) is { } p)
                    {
                        return p;
                    }
                }
                return null;
            default:
                return null; // stations come with their row (rebuilding one on its slot: M3)
        }
    }

    private PiecePlan? ResolveFacility(UnitDef def, AirfieldType t, Cell c, int facing, byte owner, IReadOnlyList<Unit> builders, int k, bool spawn)
    {
        if (facing % (FixedPoint.Facing.Count / 4) != 0 || PieceDef(owner, t.Row) is null || PieceDef(owner, t.Segment) is null)
        {
            return null;
        }
        var origin = FoundOrigin(t, def.Archetype, c, facing);
        var rects = PlanRects(t, def.Archetype, origin, facing);
        var pad = PadOf(rects);
        foreach (var r in rects)
        {
            if (!PieceGround(r, k, pad, t.MaxReliefRaw) || !PieceFree(r, null))
            {
                return null;
            }
        }
        if ((!spawn && UnitsIn(rects[0], builders)) || !OnMap(WestCorridor(t, origin, facing, 0)) || !OnMap(EastCorridor(t, origin, facing, t.MinSegments - 1)))
        {
            return null;
        }
        return new PiecePlan(AirfieldRole.Facility, null, t, origin, facing, pad, rects[0], 0, 0, 0);
    }

    // SnapRow finds the row slot of af whose top-left map cell is c.
    private static bool SnapRow(Airfield af, Cell c, out int col, out int row)
    {
        col = row = 0;
        var m00 = AirfieldRowCell(af, 0, 0);
        var m10 = AirfieldRowCell(af, 1, 0);
        var m01 = AirfieldRowCell(af, 0, 1);
        int dcx = m10.X - m00.X, dcy = m10.Y - m00.Y, drx = m01.X - m00.X, dry = m01.Y - m00.Y;
        int dx = c.X - m00.X, dy = c.Y - m00.Y;
        if (dcx != 0)
        {
            if (dx % dcx != 0 || dy % dry != 0)
            {
                return false;
            }
            (col, row) = (dx / dcx, dy / dry);
        }
        else
        {
            if (dy % dcy != 0 || dx % drx != 0)
            {
                return false;
            }
            (col, row) = (dy / dcy, dx / drx);
        }
        return AirfieldRowCell(af, col, row) == c;
    }

    // ResolveRow applies the growth rules to row (col, row) of af: the facility stands; the slot is
    // empty; the row below exists (row 0: it is (0, 0) or touches a row 0 beside it); its lane mouth
    // lies over the parallel taxiway (of the runway it would lay, for the first); its ground and the
    // runway it lays are free and level enough.
    private PiecePlan? ResolveRow(Airfield af, int col, int row, int k)
    {
        var t = af.Type;
        if (col < 0 || row < 0 || !Standing(af.Facility) || af.RowAt(col, row) is not null ||
            PieceDef(af.Owner, t.Loading) is null || PieceDef(af.Owner, t.Normal) is null)
        {
            return null;
        }
        var grows = row > 0 ? af.RowAt(col, row - 1) is not null : col == 0 || af.RowAt(col - 1, 0) is not null || af.RowAt(col + 1, 0) is not null;
        if (!grows)
        {
            return null;
        }
        var lays = !af.HasRunway;
        var (west, east) = lays ? (0, t.MinSegments - 1) : (af.West, af.East);
        var mouth = (t.RowW * col + t.StationCells) * 10 + t.LaneCells * 5; // tenths of a cell
        if (mouth < t.SegmentCells * west * 10 || mouth >= t.SegmentCells * (east + 1) * 10)
        {
            return null;
        }
        var rect = MapRect(AirfieldRole.Row, af.Origin, af.Facing, RowLocal(t, col, row));
        if (!PieceGround(rect, k, af.PadRaw, t.MaxReliefRaw) || !PieceFree(rect, af))
        {
            return null;
        }
        if (lays)
        {
            if (PieceDef(af.Owner, t.Segment) is null ||
                !OnMap(WestCorridor(t, af.Origin, af.Facing, west)) || !OnMap(EastCorridor(t, af.Origin, af.Facing, east)))
            {
                return null;
            }
            for (var s = west; s <= east; s++)
            {
                var sr = MapRect(AirfieldRole.Segment, af.Origin, af.Facing, SegmentLocal(t, s));
                if (!PieceGround(sr, k, af.PadRaw, t.MaxReliefRaw) || !PieceFree(sr, af))
                {
                    return null;
                }
            }
        }
        return new PiecePlan(AirfieldRole.Row, af, t, af.Origin, af.Facing, af.PadRaw, rect, col, row, 0);
    }

    // ResolveSegment extends af's runway at the end whose next slot has c as its top-left cell: the
    // facility stands, the first row has laid the runway, the corridor beyond stays on the map.
    private PiecePlan? ResolveSegment(Airfield af, Cell c, int k)
    {
        var t = af.Type;
        if (!af.HasRunway || af.LeadRow != 0 || !Standing(af.Facility))
        {
            return null;
        }
        foreach (var s in new[] { af.West - 1, af.East + 1 })
        {
            if (AirfieldSegmentCell(af, s) != c)
            {
                continue;
            }
            var corridor = s < af.West ? WestCorridor(t, af.Origin, af.Facing, s) : EastCorridor(t, af.Origin, af.Facing, s);
            var rect = MapRect(AirfieldRole.Segment, af.Origin, af.Facing, SegmentLocal(t, s));
            if (OnMap(corridor) && PieceGround(rect, k, af.PadRaw, t.MaxReliefRaw) && PieceFree(rect, af))
            {
                return new PiecePlan(AirfieldRole.Segment, af, t, af.Origin, af.Facing, af.PadRaw, rect, 0, 0, s);
            }
        }
        return null;
    }

    // CanPlaceAirfield is CanPlace for airfield pieces (see ResolvePiece).
    private bool CanPlaceAirfield(UnitDef def, Cell c, int facing, IReadOnlyList<Unit> builders) =>
        ResolvePiece(def, c, facing, builders[0].Owner, builders, false) is not null;

    // ---- Placing pieces ----

    // PlacePiece creates a piece on rectangle r: a site (or finished), its ground a pad at the
    // airfield's height (shape), cleared of trees, and road where it is pavement.
    private Unit PlacePiece(UnitDef def, AirfieldRect r, int facing, long pad, uint airfield, bool finished, bool shape)
    {
        var a = def.Archetype;
        if (shape && Ground is { } g)
        {
            g.SetPad(r.Cell, r.W, r.H, Fixed.FromRaw(pad), a.Construction?.PadBlend ?? 0);
            for (var y = r.Cell.Y - 1; y <= r.Cell.Y + r.H; y++)
            {
                for (var x = r.Cell.X - 1; x <= r.Cell.X + r.W; x++)
                {
                    if (g.Forest(new Cell(x, y)) != 0)
                    {
                        g.SetForest(new Cell(x, y), 0);
                    }
                }
            }
            if (a.Pavement)
            {
                g.SetRoad(r.Cell, r.W, r.H, true);
            }
        }
        var s = Add(def, FootprintCenter(r.Cell, r.W, r.H), facing);
        s.Turned = TurnsAt(a, facing);
        s.Airfield = airfield;
        if (!finished && a.Construction is { } cs)
        {
            s.Construction = ConstructionState.Building;
            s.Work = 0;
            s.HP = Math.Max(1, def.MaxHp * cs.StartHp / 100);
            s.Stock = 0;
        }
        return s;
    }

    // PlaceAirfieldPiece places a resolved piece for the owner: a facility founds its airfield, a row
    // joins its grid (the first one lays the minimum runway, passive sites on its bar), a segment
    // extends the runway. Finished (a scenario), a row spawns its stations at once.
    private Unit PlaceAirfieldPiece(UnitDef def, PiecePlan p, byte owner, bool finished)
    {
        var t = p.Type;
        switch (p.Role)
        {
            case AirfieldRole.Facility:
            {
                var site = PlacePiece(def, p.Rect, p.Facing, p.Pad, 0, finished, true);
                var af = new Airfield(site.Id, owner, t, p.Origin, p.Facing, p.Pad);
                site.Airfield = af.Id;
                _airfields.Add(af);
                return site;
            }
            case AirfieldRole.Row:
            {
                var af = p.Field!;
                var site = PlacePiece(def, p.Rect, p.Facing, p.Pad, af.Id, finished, true);
                af.InsertRow(new AirfieldRow(p.Col, p.Row, site.Id, t.RowSlots));
                if (!af.HasRunway)
                {
                    var segDef = PieceDef(owner, t.Segment)!;
                    long total = (long)def.Archetype.BuildTime * 100;
                    (af.West, af.East) = (0, t.MinSegments - 1);
                    for (var s = 0; s < t.MinSegments; s++)
                    {
                        var seg = PlacePiece(segDef, MapRect(AirfieldRole.Segment, af.Origin, af.Facing, SegmentLocal(t, s)), af.Facing, af.PadRaw, af.Id, finished, true);
                        af.SegmentList.Add(seg.Id);
                        Charge(owner, segDef);
                        total += (long)segDef.Archetype.BuildTime * 100;
                    }
                    if (!finished)
                    {
                        af.LeadRow = site.Id;
                        site.BuildTotal = (int)total; // one bar for the row and its runway
                    }
                }
                if (finished)
                {
                    SpawnStations(af, af.RowAt(p.Col, p.Row)!);
                }
                return site;
            }
            default:
            {
                var af = p.Field!;
                var site = PlacePiece(def, p.Rect, p.Facing, p.Pad, af.Id, finished, true);
                if (p.Seg < af.West)
                {
                    af.West = p.Seg;
                    af.SegmentList.Insert(0, site.Id);
                }
                else
                {
                    af.East = p.Seg;
                    af.SegmentList.Add(site.Id);
                }
                return site;
            }
        }
    }

    // SpawnStations puts a finished row's stations on their slots, finished.
    private void SpawnStations(Airfield af, AirfieldRow row)
    {
        var t = af.Type;
        for (var i = 0; i < t.RowSlots.Count; i++)
        {
            var slot = t.RowSlots[i];
            var def = PieceDef(af.Owner, slot.Kind == StationKind.Loading ? t.Loading : t.Normal);
            if (def is null || row.Stations[i].Unit != 0)
            {
                continue;
            }
            var r = MapRect(AirfieldRole.Station, af.Origin, af.Facing, StationLocal(t, row.Col, row.Row, slot));
            row.Stations[i].Unit = PlacePiece(def, r, af.Facing, af.PadRaw, af.Id, true, false).Id;
        }
    }

    // BuildAirfield runs a Build command for an airfield piece: the site, its builders' orders.
    private void BuildAirfield(Command c, UnitDef def, List<Unit> builders)
    {
        if (builders.Count == 0 || ResolvePiece(def, c.Cell, c.Facing, c.Player, builders, false) is not { } p)
        {
            return;
        }
        var site = PlaceAirfieldPiece(def, p, c.Player, false);
        Charge(c.Player, def);
        foreach (var u in builders)
        {
            Assign(u, new Order(OrderKind.Build, default, site.Id, 0));
        }
    }

    /// <summary>
    /// Places a finished airfield for its facility's player (scenarios, tests, the sandbox): the
    /// facility with c its top-left cell, turned by facing; the first row given (it lays the
    /// minimum runway); runway segments added at the west and east ends; then the other rows in the
    /// order given. Every piece follows the placement and growth rules (units standing there are no
    /// obstacle). Returns null when the facility can't stand there.
    /// </summary>
    /// <exception cref="ArgumentException">def is not a facility, or a row or segment breaks the rules.</exception>
    public Airfield? SpawnAirfield(UnitDef facility, Cell c, int facing, IReadOnlyList<(int Col, int Row)> rows, int westSegments = 0, int eastSegments = 0)
    {
        if (facility.Archetype.Airfield is not { Role: AirfieldRole.Facility })
        {
            throw new ArgumentException($"airfield: {facility.Name} is not a facility");
        }
        var owner = facility.Player;
        if (ResolvePiece(facility, c, facing, owner, [], true) is not { } fp)
        {
            return null;
        }
        PlaceAirfieldPiece(facility, fp, owner, true);
        var af = _airfields[^1];
        var t = af.Type;
        for (var i = 0; i < rows.Count; i++)
        {
            var (col, row) = rows[i];
            var p = ResolveRow(af, col, row, -1) ?? throw new ArgumentException($"airfield: row ({col}, {row}) breaks the growth or placement rules");
            PlaceAirfieldPiece(PieceDef(owner, t.Row)!, p, owner, true);
            for (var j = 0; i == 0 && j < westSegments + eastSegments; j++)
            {
                var k = j < westSegments ? af.West - 1 : af.East + 1;
                var sp = ResolveSegment(af, AirfieldSegmentCell(af, k), -1) ?? throw new ArgumentException($"airfield: segment {k} breaks the placement rules");
                PlaceAirfieldPiece(PieceDef(owner, t.Segment)!, sp, owner, true);
            }
        }
        return af;
    }

    // ---- Construction hooks ----

    // PassiveSite reports whether a site takes no work of its own: a runway segment the first row
    // is laying on its bar.
    private bool PassiveSite(Unit s) =>
        s.Airfield != 0 && s.Def.Archetype.Airfield is { Role: AirfieldRole.Segment } && AirfieldOf(s) is { LeadRow: not 0 };

    // TotalWork is the work a site needs: its build time, or its whole bar (the first row with its runway).
    private static long TotalWork(Unit s) => s.BuildTotal != 0 ? s.BuildTotal : (long)s.Def.Archetype.BuildTime * 100;

    // LeadProgress moves the first row's runway sites with its work: each at the row's fraction of
    // its own build (their HP rises with it, damage taken stays taken).
    private void LeadProgress(Unit row)
    {
        if (AirfieldOf(row) is not { } af || af.LeadRow != row.Id)
        {
            return;
        }
        var total = TotalWork(row);
        foreach (var id in af.SegmentList)
        {
            if (Unit(id) is { IsBuilt: false } seg)
            {
                var work = (int)((long)row.Work * TotalWork(seg) / total);
                seg.HP = Math.Min(seg.Def.MaxHp, seg.HP + SiteHp(seg, work) - SiteHp(seg, seg.Work));
                seg.Work = work;
            }
        }
    }

    // AirfieldFinished: a finished row spawns its stations; the first row finishes its runway.
    private void AirfieldFinished(Unit s)
    {
        if (AirfieldOf(s) is not { } af || s.Def.Archetype.Airfield is not { Role: AirfieldRole.Row })
        {
            return;
        }
        if (af.LeadRow == s.Id)
        {
            af.LeadRow = 0;
            foreach (var id in af.SegmentList)
            {
                if (Unit(id) is { IsBuilt: false } seg)
                {
                    seg.HP = Math.Min(seg.Def.MaxHp, seg.HP + SiteHp(seg, TotalWork(seg)) - SiteHp(seg, seg.Work));
                    Finish(seg);
                }
            }
        }
        s.BuildTotal = 0;
        foreach (var r in af.RowList)
        {
            if (r.Unit == s.Id)
            {
                SpawnStations(af, r);
            }
        }
    }

    // CancelAirfieldSite: cancelling the first row cancels the runway it was laying; a passive
    // segment can't be cancelled on its own. Returns false to refuse the cancel.
    private bool CancelAirfieldSite(Unit site, byte player)
    {
        if (PassiveSite(site))
        {
            return false;
        }
        if (AirfieldOf(site) is { } af && af.LeadRow == site.Id)
        {
            af.LeadRow = 0;
            foreach (var id in af.SegmentList.ToArray())
            {
                if (Unit(id) is { IsBuilt: false } seg)
                {
                    Refund(player, seg.Def, seg.Def.Archetype.Construction?.Refund ?? 0);
                    IdleBuilders(seg.Id);
                    _units.Remove(seg);
                    ForgetPiece(af, seg.Id);
                }
            }
        }
        return true;
    }

    private void IdleBuilders(uint site)
    {
        foreach (var u in _units)
        {
            if (u.Order.Kind == OrderKind.Build && u.Order.Target == site)
            {
                u.Idle();
            }
        }
    }

    // ForgetPiece drops a piece that is gone from its airfield's record: the facility slot empties,
    // a row (a cancelled site) leaves the grid, a segment slot empties (the runway's ends shrink
    // past empty slots), a station's slot empties. An airfield with nothing left is removed.
    private void ForgetPiece(Airfield af, uint id)
    {
        if (af.Facility == id)
        {
            af.Facility = 0;
        }
        for (var i = af.RowList.Count - 1; i >= 0; i--)
        {
            var r = af.RowList[i];
            if (r.Unit == id)
            {
                af.RowList.RemoveAt(i);
                continue;
            }
            foreach (var st in r.Stations)
            {
                if (st.Unit == id)
                {
                    st.Unit = 0;
                }
            }
        }
        for (var i = 0; i < af.SegmentList.Count; i++)
        {
            if (af.SegmentList[i] == id)
            {
                af.SegmentList[i] = 0;
            }
        }
        while (af.SegmentList.Count > 0 && af.SegmentList[0] == 0)
        {
            af.SegmentList.RemoveAt(0);
            af.West++;
        }
        while (af.SegmentList.Count > 0 && af.SegmentList[^1] == 0)
        {
            af.SegmentList.RemoveAt(af.SegmentList.Count - 1);
            af.East--;
        }
        if (af.SegmentList.Count == 0)
        {
            (af.West, af.East) = (0, -1);
        }
        if (af.Facility == 0 && af.RowList.Count == 0 && af.SegmentList.Count == 0)
        {
            _airfields.Remove(af);
        }
    }

    // RunAirfields: the airfield system's step (after the tunnels, before cleanup). M1: pieces that
    // are gone or down (0 HP) leave their airfield's record, in ID order. M2: the traffic (the
    // queues, the runway lock and the clearances; Sim/Systems/AirfieldFlightSystem.cs).
    private void RunAirfields()
    {
        for (var i = 0; i < _airfields.Count; i++)
        {
            var af = _airfields[i];
            var gone = _gonePieces;
            gone.Clear();
            Check(af.Facility);
            foreach (var r in af.RowList)
            {
                Check(r.Unit);
                foreach (var st in r.Stations)
                {
                    Check(st.Unit);
                }
            }
            foreach (var id in af.SegmentList)
            {
                Check(id);
            }
            if (af.LeadRow != 0 && Unit(af.LeadRow) is not { Alive: true })
            {
                af.LeadRow = 0; // its runway sites stay, ordinary sites now
            }
            gone.Sort();
            foreach (var id in gone)
            {
                ForgetPiece(af, id);
            }
            if (i < _airfields.Count && _airfields[i] != af)
            {
                i--; // removed
            }

            void Check(uint id)
            {
                if (id != 0 && Unit(id) is not { Alive: true })
                {
                    gone.Add(id);
                }
            }
        }
        AirfieldTraffic(); // M2: queues, the runway lock and clearances
    }

    // ---- Derived data for the flight rules (M2) ----

    /// <summary>
    /// The stations of af that could take an aircraft: standing and finished, of a type that
    /// serves its size class, no narrower than its span (their <see cref="AirfieldPieceSpec.MaxSpan"/>).
    /// In row order, then slot order. Nothing in it is specific to one aircraft.
    /// </summary>
    public List<AirfieldStation> StationsFor(Airfield af, AircraftSpec aircraft)
    {
        var list = new List<AirfieldStation>();
        if (!af.Type.Serves(aircraft.SizeClass))
        {
            return list;
        }
        foreach (var r in af.RowList)
        {
            foreach (var st in r.Stations)
            {
                if (Unit(st.Unit) is { Alive: true, IsBuilt: true } u && u.Def.Archetype.Airfield is { } spec &&
                    (spec.MaxSpan.Raw == 0 || aircraft.Span <= spec.MaxSpan))
                {
                    list.Add(st);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Derives an airfield's taxi graph from its pieces (not game state; the same pieces always give
    /// the same graph): runway centre-line nodes at its ends and connectors, the parallel taxiway at
    /// every connector and lane mouth, a lane node per row, a stop per station; edges along the
    /// runway, across each connector, along the taxiway, up each column's lane from its mouth, and
    /// from each row's lane node to its stations' stops.
    /// </summary>
    public TaxiGraph AirfieldTaxiGraph(Airfield af)
    {
        var t = af.Type;
        var nodes = new List<TaxiNode>();
        var edges = new List<(int, int)>();
        int Node(TaxiNodeKind kind, int x, int y, uint station = 0)
        {
            nodes.Add(new TaxiNode(kind, AirfieldPoint(af, x, y), (x, y), station));
            return nodes.Count - 1;
        }
        void Edge(int a, int b) => edges.Add(a < b ? (a, b) : (b, a));

        var connector = (int)(t.Connector.Raw * 10 / Fixed.OneRaw);
        var centre = t.RunwayCells * 5;
        var taxiY = (t.RunwayCells + t.GrassCells) * 10 + t.TaxiwayCells * 5;
        var taxiway = new List<(int X, int Node)>();
        if (af.HasRunway)
        {
            var prev = Node(TaxiNodeKind.RunwayEnd, t.SegmentCells * af.West * 10, centre);
            for (var k = af.West; k <= af.East; k++)
            {
                var x = t.SegmentCells * k * 10 + connector;
                var n = Node(TaxiNodeKind.RunwayConnector, x, centre);
                Edge(prev, n);
                prev = n;
                var tn = Node(TaxiNodeKind.TaxiwayConnector, x, taxiY);
                Edge(n, tn);
                taxiway.Add((x, tn));
            }
            Edge(prev, Node(TaxiNodeKind.RunwayEnd, t.SegmentCells * (af.East + 1) * 10, centre));
        }
        // Lanes, column by column (rows are sorted by column, then row).
        var i = 0;
        while (i < af.RowList.Count)
        {
            var col = af.RowList[i].Col;
            var mouthX = (t.RowW * col + t.StationCells) * 10 + t.LaneCells * 5;
            var mouth = -1;
            foreach (var (x, n) in taxiway)
            {
                if (x == mouthX)
                {
                    mouth = n;
                }
            }
            if (mouth < 0)
            {
                mouth = Node(TaxiNodeKind.LaneMouth, mouthX, taxiY);
                taxiway.Add((mouthX, mouth));
            }
            var (below, belowRow) = (mouth, -1);
            for (; i < af.RowList.Count && af.RowList[i].Col == col; i++)
            {
                var r = af.RowList[i];
                var rowY = (t.SegmentDepth + t.RowH * r.Row) * 10;
                var lane = Node(TaxiNodeKind.Lane, mouthX, rowY + t.RowH * 5);
                if (r.Row == belowRow + 1)
                {
                    Edge(below, lane); // a gap (a row gone) cuts the lane above it
                }
                (below, belowRow) = (lane, r.Row);
                for (var s = 0; s < r.Stations.Count; s++)
                {
                    if (r.Stations[s].Unit != 0)
                    {
                        var stop = t.RowSlots[s].ParkIn[^1];
                        Edge(lane, Node(TaxiNodeKind.Stop, t.RowW * col * 10 + stop.X, rowY + stop.Y, r.Stations[s].Unit));
                    }
                }
            }
        }
        taxiway.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Node.CompareTo(b.Node));
        for (var k = 1; k < taxiway.Count; k++)
        {
            Edge(taxiway[k - 1].Node, taxiway[k].Node);
        }
        edges.Sort();
        return new TaxiGraph(nodes, edges);
    }
}
