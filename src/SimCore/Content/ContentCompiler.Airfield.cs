using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Content;

// Airfields and aircraft (the game repo's docs/airfield-design.md): the generic aircraft block, the
// pieces' roles, the weapons' runway effects and the airfield types. An airfield type names its
// pieces; each piece archetype says its role, and belongs to exactly one type.
internal static partial class ContentCompiler
{
    private const int MaxSlotPoints = 32;

    // AirfieldPieces maps every archetype an airfield type names to its type, its role there and
    // (stations) its kind. An archetype serves one type in one role.
    private static Dictionary<string, (int Type, AirfieldRole Role, StationKind Kind)> AirfieldPieces(
        List<Src<AirfieldTypeJson>> types, Dictionary<string, int> archetypes)
    {
        var map = new Dictionary<string, (int, AirfieldRole, StationKind)>(StringComparer.Ordinal);
        for (var i = 0; i < types.Count; i++)
        {
            var t = types[i];
            var where = $"airfield type \"{t.Id}\"";
            var p = t.Def.Pieces ?? throw Fail(t.File, $"{where}: missing pieces");
            foreach (var (field, id, role, kind) in new[]
            {
                ("pieces.facility", p.Facility, AirfieldRole.Facility, StationKind.Loading),
                ("pieces.row", p.Row, AirfieldRole.Row, StationKind.Loading),
                ("pieces.segment", p.Segment, AirfieldRole.Segment, StationKind.Loading),
                ("pieces.loading", p.Loading, AirfieldRole.Station, StationKind.Loading),
                ("pieces.normal", p.Normal, AirfieldRole.Station, StationKind.Normal),
            })
            {
                if (id is null || !archetypes.ContainsKey(id))
                {
                    throw Fail(t.File, $"{where}: unknown archetype \"{id}\" in {field}");
                }
                if (!map.TryAdd(id, (i, role, kind)))
                {
                    throw Fail(t.File, $"{where}: archetype \"{id}\" ({field}) is already a piece of an airfield type");
                }
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
            _ => throw Fail(f, $"{where}: unknown airfield.role \"{j.Role}\" (want facility, row, segment or station)"),
        };
        if (role != piece.Role)
        {
            throw Fail(f, $"{where}: airfield.role \"{j.Role}\" differs from its role in its airfield type");
        }
        if (role != AirfieldRole.Station)
        {
            if (j.Kind is not null || j.NeedsFacility || j.Rearm != 0 || j.Repair != 0 || j.Refuel != 0 || j.MaxSpan != 0)
            {
                throw Fail(f, $"{where}: airfield: kind, needsFacility, rearm, repair, refuel and maxSpan are for stations");
            }
            return new AirfieldPieceSpec { Role = role, Type = piece.Type };
        }
        var kind = j.Kind switch
        {
            "loading" => StationKind.Loading,
            "normal" => StationKind.Normal,
            _ => throw Fail(f, $"{where}: unknown airfield.kind \"{j.Kind}\" (want loading or normal)"),
        };
        if (kind != piece.Kind)
        {
            throw Fail(f, $"{where}: airfield.kind \"{j.Kind}\" differs from its kind in its airfield type");
        }
        return new AirfieldPieceSpec
        {
            Role = role,
            Type = piece.Type,
            Kind = kind,
            NeedsFacility = j.NeedsFacility,
            RearmPercent = Num(f, where, "airfield.rearm", j.Rearm, 1, 1000),
            RepairPercent = Num(f, where, "airfield.repair", j.Repair, 1, 1000),
            RefuelPercent = Num(f, where, "airfield.refuel", j.Refuel, 1, 1000),
            MaxSpan = Tenths(Num(f, where, "airfield.maxSpan", j.MaxSpan, 0, Limit)),
        };
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
        foreach (var a in new[] { facility, row, segment })
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
            StationCells = station,
            LaneCells = lane,
            SegmentCells = seg,
            RunwayCells = width,
            GrassCells = grass,
            TaxiwayCells = taxiway,
            Connector = Tenths(connector),
            MinSegments = Num(f, where, "runway.minSegments", rw.MinSegments, 1, 64),
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
}
