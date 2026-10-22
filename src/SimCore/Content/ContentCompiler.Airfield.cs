using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Content;

// Airfields and aircraft (the game repo's docs/airfield-design.md): the generic aircraft block, the
// pieces' roles, the weapons' runway effects and the airfield types. An airfield type names its
// pieces; each piece archetype says its role, and belongs to exactly one type.
internal static partial class ContentCompiler
{
    private const int MaxSlotPoints = 32;

    // AirfieldPieces maps every archetype an airfield type names to its (first) type, its role there and
    // (stations) its kind. An archetype has one role; several types may share a piece in the same role
    // (H1: the main air base reuses the forward airfield's row, shelter and stand).
    private static Dictionary<string, (int Type, AirfieldRole Role, StationKind Kind)> AirfieldPieces(
        List<Src<AirfieldTypeJson>> types, Dictionary<string, int> archetypes)
    {
        var map = new Dictionary<string, (int, AirfieldRole, StationKind)>(StringComparer.Ordinal);
        for (var i = 0; i < types.Count; i++)
        {
            var t = types[i];
            var where = $"airfield type \"{t.Id}\"";
            var p = t.Def.Pieces ?? throw Fail(t.File, $"{where}: missing pieces");
            var named = new List<(string, string?, AirfieldRole, StationKind)>
            {
                ("pieces.facility", p.Facility, AirfieldRole.Facility, StationKind.Loading),
                ("pieces.row", p.Row, AirfieldRole.Row, StationKind.Loading),
                ("pieces.segment", p.Segment, AirfieldRole.Segment, StationKind.Loading),
                ("pieces.loading", p.Loading, AirfieldRole.Station, StationKind.Loading),
                ("pieces.normal", p.Normal, AirfieldRole.Station, StationKind.Normal),
            };
            var core = p.Core is not null || p.Hangar is not null || p.Radar is not null || p.Heavy is not null;
            if (core)
            {
                // A main air base (H1): its core founds it and lays the hangar (with its heavy bays) and the radar (logistics: the
                // forward airbase has none).
                named.Add(("pieces.core", p.Core, AirfieldRole.Core, StationKind.Loading));
                named.Add(("pieces.hangar", p.Hangar, AirfieldRole.Hangar, StationKind.Loading));
                if (p.Radar is not null)
                {
                    named.Add(("pieces.radar", p.Radar, AirfieldRole.Radar, StationKind.Loading));
                }
                named.Add(("pieces.heavy", p.Heavy, AirfieldRole.Station, StationKind.Heavy));
            }
            foreach (var (field, id, role, kind) in named)
            {
                if (id is null || !archetypes.ContainsKey(id))
                {
                    throw Fail(t.File, $"{where}: unknown archetype \"{id}\" in {field}");
                }
                if (map.TryGetValue(id, out var had))
                {
                    if (had.Item2 != role || had.Item3 != kind || role is AirfieldRole.Facility or AirfieldRole.Core or AirfieldRole.Hangar or AirfieldRole.Radar)
                    {
                        throw Fail(t.File, $"{where}: archetype \"{id}\" ({field}) is already a piece of an airfield type in another role (only rows, segments and stations are shared)");
                    }
                    continue;
                }
                map.Add(id, (i, role, kind));
            }
        }
        return map;
    }

    // GroundArmorOf resolves an aircraft's groundArmor (the armor class it has on the ground), or -1.
    private static int GroundArmorOf(Context c, string f, string where, string? name)
    {
        if (name is null)
        {
            return -1;
        }
        if (!c.ArmorIndex.TryGetValue(name, out var i))
        {
            throw Fail(f, $"{where}: unknown aircraft.groundArmor \"{name}\" (want one of {string.Join(", ", c.ArmorClasses)})");
        }
        return i;
    }

    // AirfieldPieceOf compiles an archetype's airfield block: its role must be the one its airfield
    // type gives it; stations carry their kind, facility rule, service percents and widest span.
    private static AirfieldPieceSpec? AirfieldPieceOf(Context c, string f, string id, string where, AirfieldPieceJson? j, int fw)
    {
        var named = c.AirfieldPieces.TryGetValue(id, out var piece);
        if (j is null)
        {
            if (named)
            {
                throw Fail(f, $"{where}: an airfield type names it as a piece, but it has no airfield block");
            }
            return null;
        }
        if (!named)
        {
            throw Fail(f, $"{where}: airfield: no airfield type names it as a piece");
        }
        if (fw == 0)
        {
            throw Fail(f, $"{where}: airfield pieces are structures (they need a footprint)");
        }
        var role = j.Role switch
        {
            "facility" => AirfieldRole.Facility,
            "row" => AirfieldRole.Row,
            "segment" => AirfieldRole.Segment,
            "station" => AirfieldRole.Station,
            "core" => AirfieldRole.Core,
            "hangar" => AirfieldRole.Hangar,
            "radar" => AirfieldRole.Radar,
            _ => throw Fail(f, $"{where}: unknown airfield.role \"{j.Role}\" (want facility, row, segment, station, core, hangar or radar)"),
        };
        if (role != piece.Role)
        {
            throw Fail(f, $"{where}: airfield.role \"{j.Role}\" differs from its role in its airfield type");
        }
        if (role != AirfieldRole.Station)
        {
            if (j.Kind is not null || j.NeedsFacility || j.Rearm != 0 || j.Repair != 0 || j.Refuel != 0 || j.MaxSpan != 0 || j.MinClass is not null || j.MaxClass is not null)
            {
                throw Fail(f, $"{where}: airfield: kind, needsFacility, rearm, repair, refuel, maxSpan, minClass and maxClass are for stations");
            }
            return new AirfieldPieceSpec { Role = role, Type = piece.Type };
        }
        var kind = j.Kind switch
        {
            "loading" => StationKind.Loading,
            "normal" => StationKind.Normal,
            "heavy" => StationKind.Heavy,
            _ => throw Fail(f, $"{where}: unknown airfield.kind \"{j.Kind}\" (want loading, normal or heavy)"),
        };
        if (kind != piece.Kind)
        {
            throw Fail(f, $"{where}: airfield.kind \"{j.Kind}\" differs from its kind in its airfield type");
        }
        var minClass = j.MinClass is null ? AircraftClass.S : ClassOf(f, where, "airfield.minClass", j.MinClass);
        var maxClass = j.MaxClass is null ? AircraftClass.XL : ClassOf(f, where, "airfield.maxClass", j.MaxClass);
        if (minClass > maxClass)
        {
            throw Fail(f, $"{where}: airfield.minClass is larger than airfield.maxClass");
        }
        var spec = new AirfieldPieceSpec
        {
            Role = role,
            Type = piece.Type,
            Kind = kind,
            NeedsFacility = j.NeedsFacility,
            RearmPercent = Num(f, where, "airfield.rearm", j.Rearm, 1, 1000),
            RepairPercent = Num(f, where, "airfield.repair", j.Repair, 1, 1000),
            RefuelPercent = Num(f, where, "airfield.refuel", j.Refuel, 1, 1000),
            MaxSpan = Tenths(Num(f, where, "airfield.maxSpan", j.MaxSpan, 0, Limit)),
            MinClass = minClass,
            MaxClass = maxClass,
        };
        // Its service while the facility is down on a type that keeps refuelling then (H1): refuel only.
        spec.RefuelOnly = new AirfieldPieceSpec
        {
            Role = role,
            Type = piece.Type,
            Kind = kind,
            RefuelPercent = spec.RefuelPercent,
            MaxSpan = spec.MaxSpan,
            MinClass = minClass,
            MaxClass = maxClass,
        };
        return spec;
    }

    private static AircraftClass ClassOf(string f, string where, string field, string? s) => s switch
    {
        "S" => AircraftClass.S,
        "M" => AircraftClass.M,
        "L" => AircraftClass.L,
        "XL" => AircraftClass.XL,
        _ => throw Fail(f, $"{where}: unknown {field} \"{s}\" (want S, M, L or XL)"),
    };

    // AircraftOf compiles the generic aircraft block (flying archetypes only). A runway aircraft
    // needs its runway minimums and its approach; a vertical one has no runway minimums.
    private static AircraftSpec AircraftOf(string f, string where, AircraftJson j, Locomotor locomotor, int groundArmor, ScoutFlight? flight, Fixed cruise)
    {
        if (locomotor != Locomotor.Flying)
        {
            throw Fail(f, $"{where}: aircraft is for flying archetypes");
        }
        var landing = j.Landing switch
        {
            "runway" => LandingKind.Runway,
            "vertical" => LandingKind.Vertical,
            _ => throw Fail(f, $"{where}: unknown aircraft.landing \"{j.Landing}\" (want runway or vertical)"),
        };
        var runway = landing == LandingKind.Runway;
        var ap = j.Approach ?? throw Fail(f, $"{where}: aircraft needs an approach block");
        var sp = j.Speeds ?? throw Fail(f, $"{where}: aircraft needs a speeds block");
        var sv = j.Service ?? throw Fail(f, $"{where}: aircraft needs a service block");
        var minRun = runway ? 1 : 0;
        var maxRun = runway ? Limit : 0;
        var touchdown = Num(f, where, "aircraft.approach.touchdown", ap.Touchdown, 0, Limit);
        var flare = Num(f, where, "aircraft.approach.flare", ap.Flare, 0, Limit);
        var fix = Num(f, where, "aircraft.approach.fixDistance", ap.FixDistance, 1, Limit);
        var landRun = Num(f, where, "aircraft.minLandRun", j.MinLandRun, minRun, maxRun);
        if (runway && touchdown >= landRun)
        {
            throw Fail(f, $"{where}: aircraft.approach.touchdown must lie within minLandRun");
        }
        if (flare > fix + touchdown)
        {
            throw Fail(f, $"{where}: aircraft.approach.flare is longer than the descent from the fix");
        }
        var approach = PerSecond(Num(f, where, "aircraft.speeds.approach", sp.Approach, 1, Limit));
        var touchdownSpeed = PerSecond(Num(f, where, "aircraft.speeds.touchdown", sp.Touchdown, 1, Limit));
        var takeoffAccel = PerSecond(Num(f, where, "aircraft.speeds.takeoffAccel", sp.TakeoffAccel, 1, Limit));
        var milAccel = PerSecond(Num(f, where, "aircraft.speeds.militaryAccel", sp.MilitaryAccel, 0, Limit));
        if (milAccel.Raw > takeoffAccel.Raw)
        {
            throw Fail(f, $"{where}: aircraft.speeds.militaryAccel is more than takeoffAccel (the afterburner's)");
        }
        var aerobrake = PerSecond(Num(f, where, "aircraft.approach.aerobrakeSpeed", ap.AerobrakeSpeed, 0, Limit));
        if (aerobrake.Raw >= touchdownSpeed.Raw && aerobrake.Raw > 0)
        {
            throw Fail(f, $"{where}: aircraft.approach.aerobrakeSpeed must be under the touchdown speed");
        }
        var pattern = ap.Pattern switch
        {
            null or "straightIn" => ApproachPattern.StraightIn,
            "overhead" => ApproachPattern.Overhead,
            _ => throw Fail(f, $"{where}: unknown aircraft.approach.pattern \"{ap.Pattern}\" (want straightIn or overhead)"),
        };
        var overhead = pattern == ApproachPattern.Overhead;
        var initial = Num(f, where, "aircraft.approach.initialDistance", ap.InitialDistance, overhead ? 1 : 0, overhead ? Limit : 0);
        var patternHeight = Num(f, where, "aircraft.approach.patternHeight", ap.PatternHeight, overhead ? 1 : 0, overhead ? 5000 : 0) * 512L / 10;
        var breakSpeed = PerSecond(Num(f, where, "aircraft.approach.breakSpeed", ap.BreakSpeed, overhead ? 1 : 0, overhead ? Limit : 0));
        var fixHeight = Num(f, where, "aircraft.approach.fixHeight", ap.FixHeight, 1, 5000) * 512L / 10;
        long patternR = 0, joinR = 0;
        if (overhead)
        {
            if (!runway || flight is null)
            {
                throw Fail(f, $"{where}: an overhead pattern needs a runway landing and a flight block");
            }
            if (patternHeight < fixHeight || breakSpeed.Raw < approach.Raw)
            {
                throw Fail(f, $"{where}: an overhead pattern needs patternHeight >= fixHeight and breakSpeed >= the approach speed");
            }
            // The break and base turns: 5/4 of the tightest the wing allows at the break and approach speeds
            // (the larger), so both are flown within its envelope; the join onto the initial point is flown from
            // cruise speed, so its circle is 5/4 of the tightest turn at cruise.
            patternR = Math.Max(flight.RadiusAt(breakSpeed.Raw), flight.RadiusAt(approach.Raw)) * 5 / 4; // a quarter in hand: it tracks the circles
            joinR = flight.RadiusAt(cruise.Raw) * 5 / 4;
        }
        var corridor = Num(f, where, "aircraft.corridorNeed", j.CorridorNeed, 0, Limit);
        if (overhead && Tenths(corridor).Raw < Tenths(initial).Raw + joinR)
        {
            throw Fail(f, $"{where}: aircraft.corridorNeed {corridor} is under its initial distance plus its join turn " +
                $"({(Tenths(initial).Raw + joinR) * 10 / Fixed.OneRaw + 1} tenths of a cell)");
        }
        return new AircraftSpec
        {
            Pattern = pattern,
            InitialDistance = Tenths(initial),
            PatternHeightRaw = patternHeight,
            BreakSpeed = breakSpeed,
            AerobrakeSpeed = aerobrake,
            MilitaryAccel = milAccel,
            CorridorNeed = Tenths(corridor),
            PatternRadius = Fixed.FromRaw(patternR),
            JoinRadius = Fixed.FromRaw(joinR),
            ApproachRadius = Fixed.FromRaw(flight?.RadiusAt(approach.Raw) ?? 0),
            SizeClass = ClassOf(f, where, "aircraft.sizeClass", j.SizeClass),
            Span = Tenths(Num(f, where, "aircraft.span", j.Span, 1, Limit)),
            Length = Tenths(Num(f, where, "aircraft.length", j.Length, 1, Limit)),
            Landing = landing,
            GroundArmor = groundArmor,
            MinLandRun = Tenths(landRun),
            MinTakeoffRun = Tenths(Num(f, where, "aircraft.minTakeoffRun", j.MinTakeoffRun, minRun, maxRun)),
            FixDistance = Tenths(fix),
            FixHeightRaw = fixHeight,
            Touchdown = Tenths(touchdown),
            Flare = Tenths(flare),
            RolloutDecel = PerSecond(Num(f, where, "aircraft.approach.rolloutDecel", ap.RolloutDecel, 1, Limit)),
            Brake = PerSecond(Num(f, where, "aircraft.approach.brake", ap.Brake, 1, Limit)),
            ApproachSpeed = approach,
            TouchdownSpeed = touchdownSpeed,
            TaxiSpeed = PerSecond(Num(f, where, "aircraft.speeds.taxi", sp.Taxi, 1, Limit)),
            TurnSpeed = PerSecond(Num(f, where, "aircraft.speeds.turn", sp.Turn, 1, Limit)),
            PushbackSpeed = PerSecond(Num(f, where, "aircraft.speeds.pushback", sp.Pushback, 1, Limit)),
            TakeoffSpeed = PerSecond(Num(f, where, "aircraft.speeds.takeoff", sp.Takeoff, 1, Limit)),
            TakeoffAccel = takeoffAccel,
            RefuelTicks = Num(f, where, "aircraft.service.refuelTicks", sv.RefuelTicks, 1, Limit),
            RearmTicksPerRound = Num(f, where, "aircraft.service.rearmTicksPerRound", sv.RearmTicksPerRound, 1, Limit),
            RepairHpPerSecond = Num(f, where, "aircraft.service.repairHpPerSecond", sv.RepairHpPerSecond, 0, Limit),
            GroundVision = Tenths(Num(f, where, "aircraft.groundVision", j.GroundVision, 0, Limit)),
            GroundEyeRaw = Num(f, where, "aircraft.groundEye", j.GroundEye, 0, 1000) * 512L / 10,
            HoldReserveTicks = Num(f, where, "aircraft.holdReserve", j.HoldReserve, 0, Limit),
            Sortie = j.Sortie,
        };
    }

    // RunwayOf compiles a weapon's or strike's runway effect: a crater on impact or wear, not both.
    private static RunwayEffect? RunwayOf(string f, string where, RunwayEffectJson? j)
    {
        if (j is null)
        {
            return null;
        }
        var crater = j.Crater switch
        {
            null => CraterKind.None,
            "small" => CraterKind.Small,
            "large" => CraterKind.Large,
            _ => throw Fail(f, $"{where}.runway: unknown crater \"{j.Crater}\" (want small or large)"),
        };
        var wear = Num(f, where, "runway.wear", j.Wear, 0, 1000);
        if ((crater == CraterKind.None) == (wear == 0))
        {
            throw Fail(f, $"{where}.runway: give either a crater or wear");
        }
        return new RunwayEffect(crater, wear);
    }

    // Cells checks a grid size in tenths of a cell is whole cells (min..max cells) and returns the cells.
    private static int Cells(string f, string where, string field, int tenths, int min, int max)
    {
        if (tenths % 10 != 0)
        {
            throw Fail(f, $"{where}: {field} is {tenths}, want whole cells (a multiple of 10)");
        }
        return Num(f, where, field, tenths / 10, min, max);
    }

    private static List<(int X, int Y)> Points(string f, string where, string field, List<List<int>>? pts, int w, int h)
    {
        if (pts is null || pts.Count < 2 || pts.Count > MaxSlotPoints)
        {
            throw Fail(f, $"{where}: {field} needs 2 to {MaxSlotPoints} points");
        }
        var list = new List<(int, int)>();
        foreach (var p in pts)
        {
            if (p is not { Count: 2 } || p[0] < 0 || p[0] > w || p[1] < 0 || p[1] > h)
            {
                throw Fail(f, $"{where}: {field} points are [x, y] inside the row (0..{w}, 0..{h} tenths)");
            }
            list.Add((p[0], p[1]));
        }
        return list;
    }

    // CompileAirfieldType checks an airfield type against its pieces: the row is two stations and a
    // lane, the segment is runway, grass and taxiway deep and a segment long, the stations square;
    // the slots fit their row and keep loading and normal stations in equal numbers.
    private static AirfieldType CompileAirfieldType(Context c, Src<AirfieldTypeJson> s, int index, List<Archetype> archetypes)
    {
        var d = s.Def;
        var f = s.File;
        var where = $"airfield type \"{s.Id}\"";
        if (string.IsNullOrEmpty(d.Name))
        {
            throw Fail(f, $"{where}: missing name");
        }
        var p = d.Pieces!;
        Archetype Piece(string? id) => archetypes[c.Archetypes[id!]];
        var (facility, row, segment, loading, normal) = (Piece(p.Facility), Piece(p.Row), Piece(p.Segment), Piece(p.Loading), Piece(p.Normal));
        var hasCore = p.Core is not null;
        var station = Cells(f, where, "station", d.Station, 1, 32);
        var lane = Cells(f, where, "lane", d.Lane, 1, 32);
        var rw = d.Runway ?? throw Fail(f, $"{where}: missing runway");
        var seg = Cells(f, where, "runway.segment", rw.Segment, 1, 32);
        var width = Cells(f, where, "runway.width", rw.Width, 1, 32);
        var grass = Cells(f, where, "runway.grass", rw.Grass, 0, 32);
        var taxiway = Cells(f, where, "runway.taxiway", rw.Taxiway, 1, 32);
        foreach (var (a, wantW, wantH, what) in new[]
        {
            (row, 2 * station + lane, station, "row (two stations and the lane)"),
            (segment, seg, width + grass + taxiway, "segment (a segment long; runway, grass and taxiway deep)"),
            (loading, station, station, "loading station"),
            (normal, station, station, "normal station"),
        })
        {
            if (a.FootprintW != wantW || a.FootprintH != wantH)
            {
                throw Fail(f, $"{where}: the {what} \"{a.Id}\" needs a {wantW} x {wantH} footprint, has {a.FootprintW} x {a.FootprintH}");
            }
        }
        Archetype? radarPiece = hasCore && p.Radar is not null ? Piece(p.Radar) : null;
        foreach (var a in hasCore ? radarPiece is null ? new[] { facility, row, segment, Piece(p.Core), Piece(p.Hangar) }
                     : new[] { facility, row, segment, Piece(p.Core), Piece(p.Hangar), radarPiece } : new[] { facility, row, segment })
        {
            if (a.Construction is null || a.Locked)
            {
                throw Fail(f, $"{where}: \"{a.Id}\" must be buildable (a construction block, not locked)");
            }
        }
        if (!row.Pavement || !segment.Pavement)
        {
            throw Fail(f, $"{where}: its row and segment are pavement");
        }
        var rowW = (2 * station + lane) * 10;
        var rowH = station * 10;
        var slots = new List<RowSlot>();
        foreach (var (sj, i) in (d.RowSlots ?? []).Select((sj, i) => (sj, i)))
        {
            var sw = $"{where}: rowSlots #{i + 1}";
            var kind = sj?.Kind switch
            {
                "loading" => StationKind.Loading,
                "normal" => StationKind.Normal,
                _ => throw Fail(f, $"{sw}: unknown kind \"{sj?.Kind}\" (want loading or normal)"),
            };
            var x = Cells(f, sw, "x", sj!.X, 0, 2 * station + lane - station);
            if (x < station + lane && x + station > station)
            {
                throw Fail(f, $"{sw}: the station overlaps the lane (cells {station} to {station + lane})");
            }
            foreach (var other in slots)
            {
                if (x < other.X + station && other.X < x + station)
                {
                    throw Fail(f, $"{sw}: it overlaps another slot");
                }
            }
            slots.Add(new RowSlot
            {
                Kind = kind,
                X = x,
                Heading = Num(f, sw, "heading", sj.Heading, 0, Facing.Count - 1),
                Pushback = sj.Pushback,
                ParkIn = Points(f, sw, "parkIn", sj.ParkIn, rowW, rowH),
                ParkOut = Points(f, sw, "parkOut", sj.ParkOut, rowW, rowH),
            });
        }
        if (slots.Count == 0 || slots.Count(sl => sl.Kind == StationKind.Loading) * 2 != slots.Count)
        {
            throw Fail(f, $"{where}: rowSlots needs loading and normal stations in equal numbers");
        }
        var hold = d.Holding ?? throw Fail(f, $"{where}: missing holding");
        var cr = d.Craters ?? throw Fail(f, $"{where}: missing craters");
        CraterSpec Crater(string field, CraterJson? cj) => cj is null
            ? throw Fail(f, $"{where}: missing {field}")
            : new CraterSpec(Tenths(Num(f, where, $"{field}.radius", cj.Radius, 1, Limit)), Num(f, where, $"{field}.repairTicks", cj.RepairTicks, 1, Limit));
        var connector = Num(f, where, "runway.connector", rw.Connector, 1, seg * 10 - 1);
        var connectorEvery = rw.ConnectorEvery == 0 ? 1 : Num(f, where, "runway.connectorEvery", rw.ConnectorEvery, 1, 64);
        var depth = width + grass + taxiway;
        var rowOrigin = (0, depth);
        int maxColumns = 0, maxRows = 0;
        var extraRows = true;
        if (d.Rows is { } rj)
        {
            extraRows = rj.Extra ?? true;
            if (rj.Origin is { } o)
            {
                if (o.Count != 2)
                {
                    throw Fail(f, $"{where}: rows.origin is [x, y]");
                }
                rowOrigin = (Cells(f, where, "rows.origin x", o[0], 0, 1000), Cells(f, where, "rows.origin y", o[1], depth, 1000));
            }
            maxColumns = Num(f, where, "rows.columns", rj.Columns, 0, 64);
            maxRows = Num(f, where, "rows.max", rj.Max, 0, 64);
        }
        var minSegments = Num(f, where, "runway.minSegments", rw.MinSegments, 1, 64);
        var core = hasCore ? CoreLayoutOf(f, where, d.Core, Piece(p.Core), facility, Piece(p.Hangar), radarPiece, Piece(p.Heavy), seg, minSegments, rowOrigin, station, lane, connectorEvery) : null;
        if (!hasCore && d.Core is not null)
        {
            throw Fail(f, $"{where}: a core layout needs pieces.core, hangar and heavy (and radar when it has one)");
        }
        if (!extraRows && !hasCore)
        {
            throw Fail(f, $"{where}: rows.extra false is for a type with a core (its built-in rows only)");
        }
        var refuelDown = d.FacilityDown switch
        {
            null or "nothing" => false,
            "refuel" => true,
            _ => throw Fail(f, $"{where}: unknown facilityDown \"{d.FacilityDown}\" (want nothing or refuel)"),
        };
        var wearPatch = Cells(f, where, "craters.wearPatch", cr.WearPatch, 1, width);
        if (width % wearPatch != 0 || seg % wearPatch != 0)
        {
            throw Fail(f, $"{where}: craters.wearPatch must divide the runway's width and a segment's length");
        }
        return new AirfieldType
        {
            Index = index,
            Id = s.Id,
            Name = d.Name,
            MaxClass = ClassOf(f, where, "maxClass", d.MaxClass),
            Facility = facility.Index,
            Row = row.Index,
            Segment = segment.Index,
            Loading = loading.Index,
            Normal = normal.Index,
            Core = hasCore ? Piece(p.Core).Index : -1,
            Hangar = hasCore ? Piece(p.Hangar).Index : -1,
            Radar = radarPiece?.Index ?? -1,
            Heavy = hasCore ? Piece(p.Heavy).Index : -1,
            CoreLayout = core,
            RowOrigin = rowOrigin,
            MaxColumns = maxColumns,
            MaxRows = maxRows,
            ExtraRows = extraRows,
            ConnectorEvery = connectorEvery,
            RefuelWithoutFacility = refuelDown,
            LinkCells = Cells(f, where, "link", d.Link, 0, 1000),
            StationCells = station,
            LaneCells = lane,
            SegmentCells = seg,
            RunwayCells = width,
            GrassCells = grass,
            TaxiwayCells = taxiway,
            Connector = Tenths(connector),
            MinSegments = minSegments,
            CorridorCells = Cells(f, where, "runway.corridor", rw.Corridor, 0, 1000),
            FacilityGapCells = Cells(f, where, "facilityGap", d.FacilityGap, 0, 32),
            MaxReliefRaw = Num(f, where, "maxRelief", d.MaxRelief, 0, 5000) * 512L / 10,
            RowSlots = slots,
            HoldDistance = Tenths(Num(f, where, "holding.distance", hold.Distance, 1, Limit)),
            HoldRadius = Tenths(Num(f, where, "holding.radius", hold.Radius, 1, Limit)),
            HoldHeightRaw = Num(f, where, "holding.height", hold.Height, 1, 5000) * 512L / 10,
            HoldStepRaw = Num(f, where, "holding.step", hold.Step, 0, 5000) * 512L / 10,
            HoldPlaces = Num(f, where, "holding.places", hold.Places, 1, 64),
            LockReleaseTicks = Num(f, where, "lockRelease", d.LockRelease, 0, Limit),
            TaxiWaitTicks = Num(f, where, "taxiWait", d.TaxiWait, 1, Limit),
            MinTaxi = d.MinTaxi == 0 ? Fixed.Zero : PerSecond(Num(f, where, "minTaxi", d.MinTaxi, 1, Limit)),
            MinTurn = d.MinTurn == 0 ? Fixed.Zero : PerSecond(Num(f, where, "minTurn", d.MinTurn, 1, Limit)),
            BreakDistance = Tenths(Cells(f, where, "breakDistance", d.BreakDistance, 0, 1000) * 10),
            CollapseDamage = Num(f, where, "collapseDamage", d.CollapseDamage, 0, 100),
            CraterHitDamage = Num(f, where, "craterHitDamage", d.CraterHitDamage, 0, 100),
            SmallCrater = Crater("craters.small", cr.Small),
            LargeCrater = Crater("craters.large", cr.Large),
            CraterMargin = Tenths(Num(f, where, "craters.margin", cr.Margin, 0, Limit)),
            MaxCraters = Num(f, where, "craters.max", cr.Max, 1, 1000),
            WearPatchCells = wearPatch,
            WearToCrater = Num(f, where, "craters.wearToCrater", cr.WearToCrater, 1, 255),
            WearNeighbourPercent = Num(f, where, "craters.neighbourShare", cr.NeighbourShare, 0, 100),
            WearRepairPerSecond = Num(f, where, "craters.wearRepairPerSecond", cr.WearRepairPerSecond, 1, 1000),
        };
    }

    private static (int X, int Y) CellPair(string f, string where, string field, List<int>? v, int max)
    {
        if (v is not { Count: 2 })
        {
            throw Fail(f, $"{where}: {field} is [x, y]");
        }
        return (Cells(f, where, field + " x", v[0], 0, max), Cells(f, where, field + " y", v[1], 0, max));
    }

    // CoreLayoutOf compiles a main air base's core layout (H1): its built-in runway is the core's whole length and
    // the type's minimum; the facility, hangar and radar lie inside the core's footprint, behind the runway band;
    // the bays fill the hangar's depth side by side within its width, each with a heavy slot whose park-in path
    // starts at its lane node and ends inside the bay; the built-in rows fit the core.
    private static CoreLayout CoreLayoutOf(string f, string where, CoreJson? j, Archetype core, Archetype facility, Archetype hangar, Archetype? radar,
        Archetype heavy, int seg, int minSegments, (int X, int Y) rowOrigin, int station, int lane, int connectorEvery)
    {
        if (j is null)
        {
            throw Fail(f, $"{where}: a type with pieces.core needs a core layout");
        }
        var segments = Num(f, where, "core.segments", j.Segments, 1, 64);
        if (segments != minSegments || core.FootprintW != segments * seg)
        {
            throw Fail(f, $"{where}: core.segments ({segments}) must be runway.minSegments and fill the core's length ({core.FootprintW} cells)");
        }
        if (connectorEvery > 1 && (segments - 1) % connectorEvery != 0)
        {
            throw Fail(f, $"{where}: the built-in runway's last segment must hold a connector (core.segments - 1 a multiple of runway.connectorEvery)");
        }
        (int X, int Y) Inside(string field, List<int>? v, Archetype a)
        {
            var at = CellPair(f, where, field, v, 1000);
            if (at.X + a.FootprintW > core.FootprintW || at.Y + a.FootprintH > core.FootprintH)
            {
                throw Fail(f, $"{where}: {field} puts \"{a.Id}\" outside the core's footprint");
            }
            return at;
        }
        var fac = Inside("core.facility", j.Facility, facility);
        var hg = Inside("core.hangar", j.Hangar, hangar);
        var rd = radar is null ? j.Radar is null ? (-1, -1) : throw Fail(f, $"{where}: core.radar needs pieces.radar") : Inside("core.radar", j.Radar, radar);
        var apron = CellPair(f, where, "core.apron", j.Apron, core.FootprintH);
        if (apron.X >= apron.Y)
        {
            throw Fail(f, $"{where}: core.apron is [y from, y to]");
        }
        var rows = Num(f, where, "core.rows", j.Rows, 0, 32);
        if (rows > 0 && (rowOrigin.X + 2 * station + lane > core.FootprintW || rowOrigin.Y + rows * station > core.FootprintH))
        {
            throw Fail(f, $"{where}: core.rows do not fit the core's footprint from rows.origin");
        }
        var bays = new List<BaySlot>();
        var right = 0;
        foreach (var (bj, i) in (j.Bays ?? []).Select((bj, i) => (bj, i)))
        {
            var bw = $"{where}: core.bays #{i + 1}";
            var x = Cells(f, bw, "x", bj.X, 0, hangar.FootprintW);
            var w = Cells(f, bw, "w", bj.W, 1, hangar.FootprintW);
            if (x < right || x + w > hangar.FootprintW)
            {
                throw Fail(f, $"{bw}: bays lie side by side, west to east, within the hangar's width");
            }
            right = x + w;
            if (heavy.FootprintW != w || heavy.FootprintH != hangar.FootprintH)
            {
                throw Fail(f, $"{bw}: the heavy bay \"{heavy.Id}\" needs a {w} x {hangar.FootprintH} footprint (the bay), has {heavy.FootprintW} x {heavy.FootprintH}");
            }
            if (bj.Lane is not { Count: 2 })
            {
                throw Fail(f, $"{bw}: lane is [x, y]");
            }
            var bayW = w * 10;
            var bayH = hangar.FootprintH * 10;
            var laneAt = (bj.Lane[0], bj.Lane[1]);
            List<(int, int)> Path(string field, List<List<int>>? pts)
            {
                if (pts is null || pts.Count < 2 || pts.Count > MaxSlotPoints)
                {
                    throw Fail(f, $"{bw}: {field} needs 2 to {MaxSlotPoints} points");
                }
                var list = new List<(int, int)>();
                foreach (var pt in pts)
                {
                    if (pt is not { Count: 2 } || pt[0] < 0 || pt[0] > bayW || pt[1] < -1000 || pt[1] > bayH)
                    {
                        throw Fail(f, $"{bw}: {field} points are [x, y] in the bay's frame (x 0..{bayW}, y up to {bayH} tenths)");
                    }
                    list.Add((pt[0], pt[1]));
                }
                return list;
            }
            var parkIn = Path("parkIn", bj.ParkIn);
            var parkOut = Path("parkOut", bj.ParkOut);
            var stop = parkIn[^1];
            if (parkIn[0] != laneAt || parkOut[^1] != laneAt || parkOut[0] != stop || stop.Item2 <= 0)
            {
                throw Fail(f, $"{bw}: parkIn runs from the lane node to a stop inside the bay, parkOut from the stop back to the lane node");
            }
            var slot = new RowSlot
            {
                Kind = StationKind.Heavy,
                X = 0,
                Heading = Num(f, bw, "heading", bj.Heading, 0, Facing.Count - 1),
                Pushback = bj.Pushback,
                ParkIn = parkIn,
                ParkOut = parkOut,
            };
            bays.Add(new BaySlot
            {
                X = x,
                W = w,
                H = hangar.FootprintH,
                Lane = laneAt,
                Slot = slot,
                Slots = [slot],
            });
        }
        if (bays.Count == 0)
        {
            throw Fail(f, $"{where}: core.bays needs at least one bay");
        }
        return new CoreLayout { Segments = segments, Facility = fac, Hangar = hg, Radar = rd, Apron = apron, Rows = rows, Bays = bays };
    }
}
