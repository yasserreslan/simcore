using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The airfield's damage and repair, milestone M3 part A (the game repo's docs/airfield-design.md,
// 1.1 and 2.2), run at the start of the airfield system's step (after the tunnels, before cleanup):
//  1. Runway blasts: every blast this tick (projectiles, air strikes, drone strikes, instant
//     splash hits) whose point lies on a finished segment's runway strip applies its weapon's
//     runway effect: a small or large crater on impact, or wear on the 2 x 2 cell patch it lands
//     in plus a share to the 4 neighbours; a patch worn to the type's wearToCrater becomes a small
//     crater and it and its 8 neighbours start over. At most the type's max craters are open; past
//     it a new crater enlarges the nearest open one to a large crater (its fill starts over).
//  2. Rubble: a finished piece with a rubble stage (the facility, the loading shelters) that is
//     down to 0 HP becomes rubble at the archetype's percent of its max HP instead of dying; a
//     shelter falling on its parked aircraft takes the type's collapse percent of the aircraft's
//     max HP. Rubble hit down to 0 again is gone (the M1 pruning that follows removes it); a gone
//     shelter or facility is rebuilt from scratch on its slot (Build).
//  3. Repair: builders (workers, the bulldozer) on a Repair order add work in reach of their job:
//     a piece's HP rises (one builder restores the whole bar in the archetype's repairable percent
//     of its build time) and rubble at full HP stands again; on a runway each builder takes the
//     open crater nearest to it, which fills in its tier's repair ticks and is left as a repaired
//     patch, then the worn patch nearest to it, which loses wearRepairPerSecond a second. Helpers
//     on one job add the piece's (or the runway segment's) construction helper percents.
// Everything runs over airfields in ID order, craters by (position, ID), patches by index, workers
// by ID, with integer arithmetic only.
public sealed partial class World
{
    private readonly List<(Vec At, RunwayEffect Effect)> _runwayBlasts = []; // this tick's, in the order they went off
    private readonly List<RepairJob> _repairJobs = []; // scratch, sorted by (Airfield, Kind, Id)

    // A repair job of the tick: a piece (Kind 0, Id its unit ID), a crater (1, crater ID) or a worn
    // patch (2, patch index) of airfield Af; Builders and Work the builders on it and their work.
    private struct RepairJob
    {
        public uint Af;
        public int Kind;
        public uint Id;
        public int Builders;
        public long Work;
    }

    private const int JobPiece = 0, JobCrater = 1, JobPatch = 2;

    // RunwayBlast notes a blast at `at` whose weapon (or strike) has a runway effect; the airfield
    // system applies it this tick. Nothing is noted while no airfield exists.
    internal void RunwayBlast(Vec at, RunwayEffect? effect)
    {
        if (effect is not null && _airfields.Count > 0)
        {
            _runwayBlasts.Add((at, effect));
        }
    }

    /// <summary>Whether an airfield piece stands: alive, finished and not rubble.</summary>
    public static bool PieceStands(Unit? u) => u is { Alive: true, IsBuilt: true, Rubble: false };

    /// <summary>
    /// Whether a flying unit is on the ground at an airfield (parked at a station, or in any ground
    /// phase of M2: roll-out, taxi, parking, pushback, waiting at the line-up, the takeoff roll), so
    /// blasts reach it like a ground unit.
    /// </summary>
    internal bool Grounded(Unit u)
    {
        if (u.AirOps is { Grounded: true } || u.Heli is { Phase: HeliPhase.Landed })
        {
            return true; // (a helicopter on the ground anywhere: a pad, a station, a field)
        }
        foreach (var af in _airfields)
        {
            foreach (var r in af.RowList)
            {
                foreach (var st in r.Stations)
                {
                    if (st.Occupant == u.Id)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>The top-left map cell of an airfield's facility slot (where a gone facility is rebuilt).</summary>
    public Cell AirfieldFacilityCell(Airfield af) =>
        MapRect(AirfieldRole.Facility, af.Origin, af.Facing, FacilityLocal(af.Type, Content.Archetypes[af.Type.Facility])).Cell;

    /// <summary>The top-left map cell of station slot `slot` of row (col, row) (where a gone station is rebuilt).</summary>
    public static Cell AirfieldStationCell(Airfield af, int col, int row, int slot) =>
        MapRect(AirfieldRole.Station, af.Origin, af.Facing, StationLocal(af.Type, col, row, col < 0 ? af.Type.CoreLayout!.Bays[-1 - col].Slot : af.Type.RowSlots[slot])).Cell;

    /// <summary>The top-left map cell of a main air base's hangar slot (H1; where a gone hangar is rebuilt).</summary>
    public Cell AirfieldHangarCell(Airfield af) =>
        MapRect(AirfieldRole.Hangar, af.Origin, af.Facing, PieceLocal(af.Type.CoreLayout!.Hangar, Content.Archetypes[af.Type.Hangar])).Cell;

    /// <summary>The top-left map cell of a main air base's radar slot (H1; where a gone radar is rebuilt).</summary>
    public Cell AirfieldRadarCell(Airfield af) =>
        MapRect(AirfieldRole.Radar, af.Origin, af.Facing, PieceLocal(af.Type.CoreLayout!.Radar, Content.Archetypes[af.Type.Radar])).Cell;

    /// <summary>The map position of a point of the airfield's frame given in raw units (1024 a cell).</summary>
    public static Vec AirfieldLocalToMap(Airfield af, long xRaw, long yRaw)
    {
        var (x, y) = Turn(af.Facing, xRaw, yRaw);
        return new Vec(Fixed.FromRaw((long)af.Origin.X * Fixed.OneRaw + x), Fixed.FromRaw((long)af.Origin.Y * Fixed.OneRaw + y));
    }

    /// <summary>A map position in the airfield's frame, raw units (1024 a cell) from its local origin.</summary>
    public static (long X, long Y) AirfieldMapToLocal(Airfield af, Vec at) =>
        Turn(FixedPoint.Facing.Count - af.Facing, at.X.Raw - (long)af.Origin.X * Fixed.OneRaw, at.Y.Raw - (long)af.Origin.Y * Fixed.OneRaw);

    /// <summary>The map position of the centre of wear patch i of an airfield (see <see cref="Airfield.Wear"/>).</summary>
    public static Vec WearPatchCentre(Airfield af, int i)
    {
        var (x, y) = PatchLocal(af, i);
        return AirfieldLocalToMap(af, x, y);
    }

    private static (long X, long Y) PatchLocal(Airfield af, int i)
    {
        var t = af.Type;
        long patch = (long)t.WearPatchCells * Fixed.OneRaw;
        var across = af.WearAcross;
        return ((long)t.SegmentCells * af.WearWest * Fixed.OneRaw + (i / across) * patch + patch / 2, (i % across) * patch + patch / 2);
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // OnRunwayStrip finds where `at` lies on af's runway strip, in its frame (raw): only over a
    // finished segment.
    private bool OnRunwayStrip(Airfield af, Vec at, out long x, out long y)
    {
        (x, y) = AirfieldMapToLocal(af, at);
        var t = af.Type;
        long seg = (long)t.SegmentCells * Fixed.OneRaw;
        if (!af.HasRunway || y < 0 || y >= (long)t.RunwayCells * Fixed.OneRaw || x < af.West * seg || x >= (af.East + 1) * seg)
        {
            return false;
        }
        var k = (int)FloorDiv(x, seg) - af.West;
        return k >= 0 && k < af.SegmentList.Count && Unit(af.SegmentList[k]) is { IsBuilt: true };
    }

    // ---- The step ----

    private void RunAirfieldDamage()
    {
        if (_airfields.Count == 0)
        {
            _runwayBlasts.Clear();
            if (_standaloneRubble)
            {
                RunStandaloneRubble(); // H1: a radar site falls and is repaired without any airfield
                RunAirfieldRepair();
            }
            return;
        }
        // 1. Runway blasts.
        foreach (var (at, effect) in _runwayBlasts)
        {
            foreach (var af in _airfields)
            {
                if (OnRunwayStrip(af, at, out var x, out var y))
                {
                    if (effect.Crater != CraterKind.None)
                    {
                        AddCrater(af, effect.Crater, x, y, at);
                    }
                    if (effect.Wear > 0)
                    {
                        AddWear(af, effect.Wear, x, y);
                    }
                    break;
                }
            }
        }
        _runwayBlasts.Clear();
        // 2. Rubble.
        foreach (var af in _airfields)
        {
            SyncWear(af);
            Fall(af, af.Facility, null);
            Fall(af, af.Radar, null); // H1: a main air base's radar and hangar are targets too
            if (Fall(af, af.Hangar, null))
            {
                foreach (var r in af.RowList)
                {
                    foreach (var st in r.Stations)
                    {
                        if (r.IsBay)
                        {
                            Collapse(af, st); // the hangar falls on the heavies parked in its bays
                        }
                    }
                }
            }
            foreach (var r in af.RowList)
            {
                foreach (var st in r.Stations)
                {
                    Fall(af, st.Unit, st);
                }
            }
        }
        if (_standaloneRubble)
        {
            RunStandaloneRubble();
        }
        // 3. Repair.
        RunAirfieldRepair();
    }

    // Fall turns a finished piece down to 0 HP into rubble (its archetype's rubble percent of max
    // HP); a station's parked aircraft takes the collapse damage. It reports whether the piece fell now.
    private bool Fall(Airfield af, uint id, AirfieldStation? st)
    {
        if (Unit(id) is not { } u || !ToRubble(u))
        {
            return false;
        }
        if (st is not null)
        {
            Collapse(af, st);
        }
        return true;
    }

    // ToRubble turns finished structure u, down to 0 HP, into rubble at its archetype's percent of max HP.
    private static bool ToRubble(Unit u)
    {
        if (u is not { Alive: false, IsBuilt: true, Rubble: false } || u.Def.Archetype.RubbleHpPercent == 0)
        {
            return false;
        }
        u.Rubble = true;
        u.HP = Math.Max(1, (int)((long)u.Def.MaxHp * u.Def.Archetype.RubbleHpPercent / 100));
        u.RepairWork = 0;
        return true;
    }

    // Collapse: the aircraft parked at station st takes the type's collapse percent of its max HP.
    private void Collapse(Airfield af, AirfieldStation st)
    {
        if (st is { Occupant: not 0 } && Unit(st.Occupant) is { Alive: true } a)
        {
            a.HP -= Math.Max(1, (int)((long)a.Def.MaxHp * af.Type.CollapseDamage / 100));
        }
    }

    // Whether any unit ever added is a structure with a rubble stage outside the airfields and tunnels (H1: the radar
    // site); while none is, the standalone rubble and repair pass costs nothing. Derived from the units (not hashed).
    private bool _standaloneRubble;

    // NoteStandalone marks a new unit of an archetype that falls to rubble on its own (H1).
    private void NoteStandalone(Unit u)
    {
        if (u.Def.Archetype.RubbleHpPercent != 0 && u.Def.Archetype.Airfield is null && u.Def.Archetype.Tunnel is null)
        {
            _standaloneRubble = true;
        }
    }

    // RunStandaloneRubble turns standalone structures with a rubble stage (H1: the radar site) down to 0 HP into
    // rubble, in ID order; rubble hit to 0 again is gone (the cleanup removes it).
    private void RunStandaloneRubble()
    {
        foreach (var u in _units)
        {
            if (u.Def.Archetype.Airfield is null && u.Def.Archetype.Tunnel is null && u.Def.IsStructure)
            {
                ToRubble(u);
            }
        }
    }

    // ---- Craters and wear ----

    // AddCrater digs a crater of kind at (x, y) of af's frame (raw), map point at: past the type's
    // cap of open craters, the nearest open one (ties: lower ID) becomes a large crater instead.
    private static void AddCrater(Airfield af, CraterKind kind, long x, long y, Vec at)
    {
        var t = af.Type;
        RunwayCrater? near = null;
        var open = 0;
        var best = long.MaxValue;
        foreach (var c in af.CraterList)
        {
            if (c.Repaired)
            {
                continue;
            }
            open++;
            long dx = c.X.Raw - x, dy = c.Y.Raw - y;
            var d = dx * dx + dy * dy;
            if (d < best || (d == best && near is not null && c.Id < near.Id))
            {
                (best, near) = (d, c);
            }
        }
        if (open >= t.MaxCraters && near is not null)
        {
            near.Resize(CraterKind.Large, t.LargeCrater);
            return;
        }
        var spec = kind == CraterKind.Large ? t.LargeCrater : t.SmallCrater;
        var n = new RunwayCrater(++af.NextCrater, Fixed.FromRaw(x), Fixed.FromRaw(y), at, kind, spec);
        var i = 0;
        while (i < af.CraterList.Count && (af.CraterList[i].X < n.X || (af.CraterList[i].X == n.X && af.CraterList[i].Id < n.Id)))
        {
            i++;
        }
        af.CraterList.Insert(i, n);
    }

    // SyncWear keeps the wear array over the runway's segments as the runway grows (or shrinks).
    private static void SyncWear(Airfield af)
    {
        if (af.WearArray.Length == 0)
        {
            return;
        }
        var t = af.Type;
        var across = af.WearAcross;
        var perSeg = t.SegmentCells / t.WearPatchCells * across;
        var len = af.HasRunway ? (af.East - af.West + 1) * perSeg : 0;
        if (af.WearWest == af.West && af.WearArray.Length == len)
        {
            return;
        }
        var wear = new byte[len];
        var fill = new int[len];
        var shift = (af.WearWest - af.West) * perSeg;
        for (var i = 0; i < af.WearArray.Length; i++)
        {
            var j = i + shift;
            if (j >= 0 && j < len)
            {
                wear[j] = af.WearArray[i];
                fill[j] = af.WearFill[i];
            }
        }
        (af.WearArray, af.WearFill, af.WearWest) = (wear, fill, af.West);
        ClearWearIfNone(af);
    }

    // ClearWearIfNone drops the wear arrays once no patch is worn (nothing left to hash).
    private static void ClearWearIfNone(Airfield af)
    {
        foreach (var v in af.WearArray)
        {
            if (v != 0)
            {
                return;
            }
        }
        (af.WearArray, af.WearFill, af.WearWest) = ([], [], 0);
    }

    // AddWear adds a wearing hit at (x, y) of af's frame (raw): wear to its patch, the neighbour share
    // (rounded down) to each of the 4 neighbours; then every patch at wearToCrater, in index order,
    // becomes a small crater at its centre and it and its 8 neighbours start over.
    private static void AddWear(Airfield af, int wear, long x, long y)
    {
        var t = af.Type;
        var across = af.WearAcross;
        var perSeg = t.SegmentCells / t.WearPatchCells * across;
        if (af.WearArray.Length == 0)
        {
            var len = (af.East - af.West + 1) * perSeg;
            (af.WearArray, af.WearFill, af.WearWest) = (new byte[len], new int[len], af.West);
        }
        long patch = (long)t.WearPatchCells * Fixed.OneRaw;
        var along = af.WearArray.Length / across;
        var pa = (int)((x - (long)t.SegmentCells * af.WearWest * Fixed.OneRaw) / patch);
        var pc = (int)(y / patch);
        Add(pa, pc, wear);
        var share = wear * t.WearNeighbourPercent / 100;
        if (share > 0)
        {
            Add(pa - 1, pc, share);
            Add(pa + 1, pc, share);
            Add(pa, pc - 1, share);
            Add(pa, pc + 1, share);
        }
        for (var i = 0; i < af.WearArray.Length; i++)
        {
            if (af.WearArray[i] < t.WearToCrater)
            {
                continue;
            }
            var (cx, cy) = PatchLocal(af, i);
            AddCrater(af, CraterKind.Small, cx, cy, AirfieldLocalToMap(af, cx, cy));
            int a0 = i / across, c0 = i % across;
            for (var da = -1; da <= 1; da++)
            {
                for (var dc = -1; dc <= 1; dc++)
                {
                    if (In(a0 + da, c0 + dc))
                    {
                        var j = (a0 + da) * across + c0 + dc;
                        af.WearArray[j] = 0;
                        af.WearFill[j] = 0;
                    }
                }
            }
        }
        ClearWearIfNone(af);

        bool In(int a, int c) => a >= 0 && a < along && c >= 0 && c < across;

        void Add(int a, int c, int n)
        {
            if (In(a, c))
            {
                var j = a * across + c;
                af.WearArray[j] = (byte)Math.Min(byte.MaxValue, af.WearArray[j] + n);
            }
        }
    }

    // ---- Repair ----

    // NeedsRepair: a finished repairable structure below full HP or rubble.
    private static bool NeedsRepair(Unit t) =>
        t.Alive && t.IsBuilt && t.Def.Archetype.RepairPercentOfBuild > 0 && (t.Rubble || t.HP < t.Def.MaxHp);

    private static bool IsRunwaySegment(Unit t) => t.IsBuilt && t.Def.Archetype.Airfield is { Role: AirfieldRole.Segment };

    // RepairCommand runs a Repair command: the target is the player's repairable structure or a
    // runway segment; the listed builders go and repair it.
    private void RepairCommand(Command c)
    {
        if (Unit(c.Target) is not { } t || t.Owner != c.Player || !t.Alive || !t.IsBuilt)
        {
            return;
        }
        var runway = IsRunwaySegment(t);
        if (!runway && t.Def.Archetype.RepairPercentOfBuild == 0)
        {
            return;
        }
        foreach (var u in Builders(c))
        {
            Assign(u, new Order(OrderKind.Repair, default, t.Id, 0));
        }
    }

    // WorkerJob picks the runway job of a worker at pos: the open crater nearest to it (ties: lower
    // ID), else the worn patch nearest to it (ties: lower index). Returns false when nothing is left.
    private static bool WorkerJob(Airfield af, Vec pos, out int kind, out uint id, out Vec at, out Fixed reach)
    {
        (kind, id, at, reach) = (0, 0, default, default);
        var best = long.MaxValue;
        foreach (var c in af.CraterList)
        {
            var d = (c.At - pos).LenSq();
            if (!c.Repaired && (d < best || (d == best && c.Id < id)))
            {
                (best, kind, id, at, reach) = (d, JobCrater, c.Id, c.At, c.Radius);
            }
        }
        if (kind != 0)
        {
            return true;
        }
        for (var i = 0; i < af.WearArray.Length; i++)
        {
            if (af.WearArray[i] == 0)
            {
                continue;
            }
            var p = WearPatchCentre(af, i);
            var d = (p - pos).LenSq();
            if (d < best)
            {
                (best, kind, id, at, reach) = (d, JobPatch, (uint)i, p, Fixed.Frac(af.Type.WearPatchCells, 2));
            }
        }
        return kind != 0;
    }

    // ThinkRepair walks a worker on a Repair order into reach of its job; it idles once there is
    // nothing left to repair (or the target is gone, or not its owner's).
    private void ThinkRepair(Unit u)
    {
        if (Unit(u.Order.Target) is not { } t || t.Owner != u.Owner || u.Def.Builder is not { } b)
        {
            u.Idle();
            return;
        }
        if (IsRunwaySegment(t))
        {
            if (AirfieldOf(t) is not { } af || !WorkerJob(af, u.Pos, out _, out _, out var at, out var reach))
            {
                u.Idle();
                return;
            }
            Approach(u, at, reach + b.Range);
            return;
        }
        if (!NeedsRepair(t))
        {
            u.Idle();
            return;
        }
        Approach(u, AimPoint(u.Pos, t), b.Range);
    }

    // JobSlot finds (or inserts, keeping the order) the scratch entry of a job.
    private int JobSlot(uint af, int kind, uint id)
    {
        var i = 0;
        while (i < _repairJobs.Count && Before(_repairJobs[i], af, kind, id))
        {
            i++;
        }
        if (i == _repairJobs.Count || _repairJobs[i].Af != af || _repairJobs[i].Kind != kind || _repairJobs[i].Id != id)
        {
            _repairJobs.Insert(i, new RepairJob { Af = af, Kind = kind, Id = id });
        }
        return i;

        static bool Before(RepairJob j, uint af, int kind, uint id) =>
            j.Af != af ? j.Af < af : j.Kind != kind ? j.Kind < kind : j.Id < id;
    }

    // HelperPercent is the share the k-th builder on a job adds (the archetype's construction
    // helpers; all of it without them).
    private static int HelperPercent(Archetype a, int k) =>
        a.Construction is { Helpers.Count: > 0 } cs ? cs.Helpers[Math.Min(k, cs.Helpers.Count - 1)] : 100;

    // RunAirfieldRepair adds this tick's repair work: builders in reach of their job (in ID order)
    // add their helper share, then each job is applied.
    private void RunAirfieldRepair()
    {
        _repairJobs.Clear();
        foreach (var u in _units)
        {
            if (u.Order.Kind != OrderKind.Repair || u.Def.Builder is not { } b || !u.Alive || u.Inside != 0 || u.Garrison != 0 ||
                Unit(u.Order.Target) is not { } t || t.Owner != u.Owner)
            {
                continue;
            }
            int i;
            Archetype helpers;
            if (IsRunwaySegment(t))
            {
                if (AirfieldOf(t) is not { } af || !WorkerJob(af, u.Pos, out var kind, out var id, out var at, out var reach) ||
                    !Within(u.Pos, at, reach + b.Range))
                {
                    continue;
                }
                i = JobSlot(af.Id, kind, id);
                helpers = t.Def.Archetype;
            }
            else
            {
                if (!NeedsRepair(t) || !Within(u.Pos, AimPoint(u.Pos, t), b.Range))
                {
                    continue;
                }
                i = JobSlot(0, JobPiece, t.Id);
                helpers = t.Def.Archetype;
            }
            var job = _repairJobs[i];
            var pct = HelperPercent(helpers, job.Builders);
            job.Builders++;
            job.Work += (long)b.Rate * pct / 100;
            _repairJobs[i] = job;
            u.Building = pct > 0;
        }
        foreach (var job in _repairJobs)
        {
            if (job.Kind == JobPiece)
            {
                RepairPiece(Unit(job.Id)!, job.Work);
            }
            else if (Airfield(job.Af) is { } af)
            {
                RepairRunway(af, job.Kind, job.Id, job.Work);
            }
        }

    }

    // RepairPiece adds work to a structure: one builder restores the whole bar in the archetype's
    // repairable percent of its build time; full again, rubble stands again.
    private static void RepairPiece(Unit t, long work)
    {
        long full = (long)t.Def.Archetype.BuildTime * t.Def.Archetype.RepairPercentOfBuild;
        if (full <= 0 || work <= 0)
        {
            return;
        }
        var acc = t.RepairWork + work * t.Def.MaxHp;
        t.HP = (int)Math.Min(t.Def.MaxHp, t.HP + acc / full);
        t.RepairWork = acc % full;
        if (t.HP >= t.Def.MaxHp)
        {
            (t.HP, t.Rubble, t.RepairWork) = (t.Def.MaxHp, false, 0);
        }
    }

    // RepairRunway adds work to a crater (filled at its total: a repaired patch now) or a worn patch
    // (a wear point off per TicksPerSecond * 100 / wearRepairPerSecond of work).
    private static void RepairRunway(Airfield af, int kind, uint id, long work)
    {
        var t = af.Type;
        if (kind == JobCrater)
        {
            foreach (var c in af.CraterList)
            {
                if (c.Id == id && !c.Repaired)
                {
                    c.Work = (int)Math.Min(c.WorkTotal, c.Work + work);
                    if (c.Work >= c.WorkTotal)
                    {
                        c.Repaired = true;
                        DropOldPatches(af);
                    }
                    return;
                }
            }
            return;
        }
        var i = (int)id;
        if (i >= af.WearArray.Length || af.WearArray[i] == 0)
        {
            return;
        }
        var per = TicksPerSecond * 100 / t.WearRepairPerSecond;
        long fill = af.WearFill[i] + work;
        while (fill >= per && af.WearArray[i] > 0)
        {
            af.WearArray[i]--;
            fill -= per;
        }
        af.WearFill[i] = af.WearArray[i] == 0 ? 0 : (int)fill;
        ClearWearIfNone(af);
    }

    // DropOldPatches keeps at most the type's max craters of repaired patches: the oldest go first.
    private static void DropOldPatches(Airfield af)
    {
        var patches = 0;
        foreach (var c in af.CraterList)
        {
            patches += c.Repaired ? 1 : 0;
        }
        while (patches > af.Type.MaxCraters)
        {
            var oldest = -1;
            for (var i = 0; i < af.CraterList.Count; i++)
            {
                if (af.CraterList[i].Repaired && (oldest < 0 || af.CraterList[i].Id < af.CraterList[oldest].Id))
                {
                    oldest = i;
                }
            }
            af.CraterList.RemoveAt(oldest);
            patches--;
        }
    }

    // ---- Rebuilds ----

    // ResolveRebuild resolves a gone piece rebuilt on its slot of one of the owner's airfields: the
    // facility on its slot (the airfield without one), a station on its empty slot of a standing row.
    private PiecePlan? ResolveRebuild(UnitDef def, AirfieldPieceSpec spec, AirfieldType t, Cell c, byte owner, IReadOnlyList<Unit> builders, int k)
    {
        foreach (var af in _airfields)
        {
            if (af.Owner != owner || (af.Core != 0 && !Unit(af.Core)!.IsBuilt))
            {
                continue; // (H1: nothing of a main air base is rebuilt before its core is finished)
            }
            if (af.Type.HasCore && spec.Role is AirfieldRole.Hangar or AirfieldRole.Radar or AirfieldRole.Facility &&
                (spec.Role == AirfieldRole.Hangar ? af.Hangar : spec.Role == AirfieldRole.Radar ? af.Radar : af.Facility) == 0 &&
                def.Archetype.Index == (spec.Role == AirfieldRole.Hangar ? af.Type.Hangar : spec.Role == AirfieldRole.Radar ? af.Type.Radar : af.Type.Facility))
            {
                var core = af.Type.CoreLayout!;
                var at = spec.Role == AirfieldRole.Hangar ? core.Hangar : spec.Role == AirfieldRole.Radar ? core.Radar : core.Facility;
                var rect = MapRect(spec.Role, af.Origin, af.Facing, PieceLocal(at, def.Archetype));
                if (rect.Cell == c && !UnitsIn(rect, builders))
                {
                    return new PiecePlan(spec.Role, af, af.Type, af.Origin, af.Facing, af.PadRaw, rect, 0, 0, 0);
                }
                continue;
            }
            if (af.Type != t && !(spec.Role == AirfieldRole.Station && (af.Type.Loading == def.Archetype.Index || af.Type.Normal == def.Archetype.Index)))
            {
                continue;
            }
            if (spec.Role == AirfieldRole.Facility && af.Facility == 0 && !af.Type.HasCore)
            {
                var rect = MapRect(AirfieldRole.Facility, af.Origin, af.Facing, FacilityLocal(t, def.Archetype));
                if (rect.Cell == c && PieceGround(rect, k, af.PadRaw, t.MaxReliefRaw, af) && PieceFree(rect, af) && !UnitsIn(rect, builders))
                {
                    return new PiecePlan(AirfieldRole.Facility, af, t, af.Origin, af.Facing, af.PadRaw, rect, 0, 0, 0);
                }
            }
            if (spec.Role != AirfieldRole.Station)
            {
                continue;
            }
            foreach (var r in af.RowList)
            {
                if (!PieceStands(Unit(r.Unit)))
                {
                    continue;
                }
                var slots = SlotsOf(af.Type, r);
                for (var i = 0; i < r.Stations.Count; i++)
                {
                    if (r.Stations[i].Unit != 0 || slots[i].Kind != spec.Kind || r.IsBay)
                    {
                        continue;
                    }
                    var rect = MapRect(AirfieldRole.Station, af.Origin, af.Facing, StationLocal(af.Type, r.Col, r.Row, slots[i]));
                    if (rect.Cell == c && !UnitsIn(rect, builders))
                    {
                        return new PiecePlan(AirfieldRole.Station, af, af.Type, af.Origin, af.Facing, af.PadRaw, rect, r.Col, r.Row, i);
                    }
                }
            }
        }
        return null;
    }

    // PlaceRebuild places a resolved rebuild: the facility back on its slot, or a station's site on its slot.
    private Unit PlaceRebuild(UnitDef def, PiecePlan p, bool finished)
    {
        var af = p.Field!;
        if (p.Role == AirfieldRole.Facility)
        {
            var fac = PlacePiece(def, p.Rect, af.Facing, af.PadRaw, af.Id, finished, !af.Type.HasCore);
            af.Facility = fac.Id;
            return fac;
        }
        if (p.Role is AirfieldRole.Hangar or AirfieldRole.Radar)
        {
            // H1: a main air base's hangar or radar back on its slot (a finished hangar lays its heavy bays again).
            var piece = PlacePiece(def, p.Rect, af.Facing, af.PadRaw, af.Id, finished, false);
            if (p.Role == AirfieldRole.Hangar)
            {
                af.Hangar = piece.Id;
                if (finished)
                {
                    HangarFinished(af);
                }
            }
            else
            {
                af.Radar = piece.Id;
            }
            return piece;
        }
        var site = PlacePiece(def, p.Rect, af.Facing, af.PadRaw, af.Id, finished, false);
        af.RowAt(p.Col, p.Row)!.Stations[p.Seg].Unit = site.Id;
        return site;
    }

    // ---- Hashing ----

    // HashRepairState mixes in a structure's rubble and repair state (only when set).
    private static void HashRepairState(ref WordHash h, Unit u)
    {
        if (u.RepairWork != 0 || (u.Rubble && u.Def.Archetype.Tunnel is null))
        {
            h.Put(0xA1F3);
            h.Put(u.Rubble ? 1UL : 0);
            h.PutSigned(u.RepairWork);
        }
    }

    // HashRunwayDamage mixes in an airfield's craters, wear and runway repair (only when present).
    private static void HashRunwayDamage(ref WordHash h, Airfield af)
    {
        if (af.CraterList.Count == 0 && af.NextCrater == 0 && af.WearArray.Length == 0)
        {
            return;
        }
        h.Put(0xA1F4);
        h.Put(af.NextCrater);
        h.PutSigned(af.CraterList.Count);
        foreach (var c in af.CraterList)
        {
            h.Put(c.Id);
            h.PutSigned(c.X.Raw);
            h.PutSigned(c.Y.Raw);
            h.PutSigned(c.At.X.Raw);
            h.PutSigned(c.At.Y.Raw);
            h.PutByte((byte)c.Kind);
            h.PutSigned(c.Radius.Raw);
            h.PutSigned(c.Work);
            h.PutSigned(c.WorkTotal);
            h.Put(c.Repaired ? 1UL : 0);
        }
        h.PutSigned(af.WearWest);
        h.PutSigned(af.WearArray.Length);
        for (var i = 0; i < af.WearArray.Length; i++)
        {
            h.PutByte(af.WearArray[i]);
            h.PutSigned(af.WearFill[i]);
        }
    }
}
