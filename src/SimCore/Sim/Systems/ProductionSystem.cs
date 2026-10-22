using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The production system: factories (structures with Archetype.Production, the vehicle garage)
// produce units from a queue, in their bays at once, and send each finished one out through a
// free door. Each tick, for every finished factory by ID: a free bay takes the oldest waiting
// order and counts its archetype's BuildTime down; finished, it takes a door (PickDoor: a random
// free one, never the one the last finished unit took) and is free again; the door opens
// (DoorTicks); with it up, friendly units standing in the exit lane are told to step aside, enemy
// units block it, and once it is clear (or after PushAfter ticks of waiting, when it goes anyway)
// the unit appears crewed on its bay floor inside and drives out through the door to the rally
// point (its own factory's footprint doesn't block it meanwhile: Unit.OutOf); when it has left
// the exit lane the door closes and is free again. A site under construction produces nothing;
// a destroyed factory loses its queue. Cost: no-op until there is an economy (Charge, Refund).
// An airfield facility (production.intoStation, E8: decision D9) has no doors: a finished
// aircraft is parked at once into a free station of its airfield (ParkProduced), held there until
// its first order; with none free it waits in its bay. Rubble produces nothing.
// A garage with a service block (the military garage) shares its doors between production and
// service (ServiceSystem.cs): a bay takes a free door when it takes an order and builds behind it
// (at most Bays doors produce at once, counting finished vehicles still coming out), and every
// other door services a vehicle.
public sealed partial class World
{
    private const int LaneHalf = 2, LaneDepth = 4, AsideCells = 5, RallyCells = 5;

    // A finished unit takes the door the last one took only when no other is free and it has
    // waited this long (2 s) for one; a factory with one door just uses it.
    private const int RepeatAfter = 40;

    // Scratch for one tick (not state): the airfield facilities' finished bays, parked after the
    // factory loop (spawning there would change the unit list being walked).
    private readonly List<(Unit Factory, Bay Bay)> _toPark = [];

    private void RunProduction()
    {
        var made = new List<(Unit Factory, int Door, UnitDef Def)>();
        _toPark.Clear();
        foreach (var f in _units)
        {
            if (f.Def.Archetype.Production is not { } pr || !f.Alive || !f.IsBuilt || f.Rubble || (long)f.HP * 100 < (long)f.Def.MaxHp * pr.MinHpPercent ||
                !ProducerWorks(f))
            {
                continue; // a site, or too badly damaged (its doors are wreckage), or its airfield's facility down (H1): it waits
            }
            FitBaysAndDoors(f, pr);
            foreach (var b in f.Bays)
            {
                Work(f, pr, b);
            }
            for (var i = 0; i < f.Doors.Length; i++)
            {
                if (Door(f, pr, i) is { } def)
                {
                    made.Add((f, i, def));
                }
            }
        }
        foreach (var (f, i, def) in made)
        {
            f.Doors[i].Vehicle = SpawnCrewed(f, i, def).Id;
            ExclusiveBuilt(f.Owner, def.Archetype); // the first of an exclusive group locks its type (ExclusiveSystem.cs)
        }
        RunService(); // the military garage's service through the doors production leaves free (ServiceSystem.cs)
        foreach (var (f, b) in _toPark)
        {
            if (ParkProduced(f, Match.Defs[b.Def]))
            {
                ExclusiveBuilt(f.Owner, Match.Defs[b.Def].Archetype); // the first of an exclusive group locks its type (ExclusiveSystem.cs)
                (b.Def, b.Left, b.Wait) = (-1, 0, 0);
            }
            else
            {
                b.Wait++; // finished: waiting for a free station
            }
        }
    }

    // FitBaysAndDoors gives factory f its bays and doors (on its first working tick).
    private static void FitBaysAndDoors(Unit f, ProductionSpec pr)
    {
        if (f.Bays.Length != pr.Bays)
        {
            f.Bays = [.. Enumerable.Range(0, pr.Bays).Select(_ => new Bay())];
        }
        if (f.Doors.Length != pr.Doors)
        {
            f.Doors = [.. Enumerable.Range(0, pr.Doors).Select(_ => new FactoryDoor())];
        }
    }

    // Work runs a bay: it takes the oldest waiting order, counts it down, and gives the finished
    // unit to a door (which opens). With shared doors (a garage that services too) a bay takes an
    // order only with a free door to build behind, and only while fewer than Bays doors produce.
    private void Work(Unit f, ProductionSpec pr, Bay b)
    {
        var shared = f.Def.Archetype.Service is not null;
        if (b.Def < 0)
        {
            if (f.ProductionQueue.Count > 0)
            {
                if (shared)
                {
                    var used = 0;
                    foreach (var fd in f.Doors)
                    {
                        used += fd.Use == DoorUse.Production ? 1 : 0;
                    }
                    var free = used < pr.Bays ? PickDoor(f, true) : -1;
                    if (free < 0)
                    {
                        return; // every door is busy (or Bays of them produce): the order waits
                    }
                    (b.Door, f.Doors[free].Use) = (free, DoorUse.Production);
                    (f.LastDoor, f.DoorSerial) = (free, f.DoorSerial + 1);
                }
                (b.Def, b.Left, b.Wait) = (f.ProductionQueue[0], Math.Max(1, Match.Defs[f.ProductionQueue[0]].Archetype.BuildTime), 0);
                f.ProductionQueue.RemoveAt(0);
            }
            return;
        }
        if (b.Left > 0 && --b.Left > 0)
        {
            return;
        }
        if (pr.IntoStation)
        {
            _toPark.Add((f, b)); // parked into a free station after the factory loop
            return;
        }
        var d = shared ? b.Door : PickDoor(f, b.Wait >= RepeatAfter); // shared: the door it built behind
        if (d < 0)
        {
            b.Wait++; // finished: waiting for a door
            return;
        }
        var door = f.Doors[d];
        (door.Def, door.State, door.Left, door.Wait, door.Use) = (b.Def, DoorState.Opening, pr.DoorTicks, 0, DoorUse.Production);
        (b.Def, b.Left, b.Wait, b.Door) = (-1, 0, 0, -1);
        if (!shared)
        {
            (f.LastDoor, f.DoorSerial) = (d, f.DoorSerial + 1);
        }
        StepAside(f, d);
    }

    /// <summary>
    /// The door a factory's next finished unit takes, or -1 to wait: a random one of the free doors
    /// (shut, nothing behind them, not taken for production or service) other than the one the last unit took, seeded by the match seed,
    /// the tick, the factory and its door serial; the last unit's door only when it is the only free
    /// one and <paramref name="lastToo"/> (the unit has waited RepeatAfter ticks), or when the
    /// factory has one door.
    /// </summary>
    internal int PickDoor(Unit f, bool lastToo)
    {
        Span<int> free = stackalloc int[f.Doors.Length];
        var n = 0;
        var lastFree = false;
        for (var i = 0; i < f.Doors.Length; i++)
        {
            if (f.Doors[i].State != DoorState.Closed || f.Doors[i].Def >= 0 || f.Doors[i].Use != DoorUse.None)
            {
                continue;
            }
            if (i == f.LastDoor && f.Doors.Length > 1)
            {
                lastFree = true;
            }
            else
            {
                free[n++] = i;
            }
        }
        if (n == 0)
        {
            return lastFree && lastToo ? f.LastDoor : -1;
        }
        if (n == 1)
        {
            return free[0];
        }
        unchecked
        {
            var mix = Match.Setup.Seed ^ ((ulong)(uint)Tick * 0x9E3779B97F4A7C15UL) ^ ((ulong)f.Id * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(uint)f.DoorSerial * 0x165667B19E3779F9UL);
            var r = new Rng.Rand(mix);
            return free[r.Intn(n)];
        }
    }

    // Door runs door i: it opens, lets its unit out when the lane is clear (returns what comes
    // out), and closes once the unit has left the exit lane (or is gone, or after a while).
    private UnitDef? Door(Unit f, ProductionSpec pr, int i)
    {
        var d = f.Doors[i];
        switch (d.State)
        {
            case DoorState.Opening:
                if (--d.Left <= 0)
                {
                    (d.State, d.Left) = (DoorState.Open, 0);
                }
                break;
            case DoorState.Open when d.Def < 0 && d.Serviced != 0:
                break; // a vehicle being serviced (going in, or in its bay): the service runs it
            case DoorState.Open when d.Def < 0:
                var v = Unit(d.Vehicle);
                d.Wait++;
                if (v is null || !v.Alive || (v.OutOf != f.Id && !InLaneBox(f, i, v, true)) || d.Wait >= pr.PushAfter)
                {
                    if (v is not null && v.OutOf == f.Id)
                    {
                        v.OutOf = 0; // the door comes down: the footprint blocks it again
                    }
                    (d.State, d.Left, d.Vehicle, d.Wait) = (DoorState.Closing, pr.DoorTicks, 0, 0);
                }
                break;
            case DoorState.Closing:
                if (--d.Left <= 0)
                {
                    (d.State, d.Left, d.Use) = (DoorState.Closed, 0, DoorUse.None); // free again
                }
                break;
        }
        if (d.State != DoorState.Open || d.Def < 0)
        {
            return null;
        }
        // Waiting to come out: friends step aside, enemies block until PushAfter.
        var (friends, enemies) = InLane(f, i);
        if ((friends == 0 && enemies == 0) || d.Wait >= pr.PushAfter)
        {
            var def = Match.Defs[d.Def];
            (d.Def, d.Wait) = (-1, 0);
            return def;
        }
        if (friends > 0 && d.Wait % 20 == 0)
        {
            StepAside(f, i);
        }
        d.Wait++;
        return null;
    }

    // SpawnCrewed puts a produced unit on door i's bay floor inside the factory, facing out, with
    // its crew aboard, and sends it out through the door's exit to the factory's rally point: a
    // move order the movement system drives at its own speed; until it is out, its factory's
    // footprint doesn't block it (OutOf) and its route starts at the exit (OutVia).
    private Unit SpawnCrewed(Unit f, int door, UnitDef def)
    {
        var at = BayPoint(f, door);
        var v = Add(def, at, FixedPoint.Facing.South);
        if (def.Crew is { } crew && Match.Players[def.Player - 1].Find(Content, f.Def.Archetype.Production!.Crew) is { } driverDef &&
            (crew.Tags.IsEmpty || driverDef.Archetype.Tags.Matches(crew.Tags)))
        {
            var d = Add(driverDef, at, FixedPoint.Facing.South);
            Board(d, v);
        }
        var exit = Cell.Of(ExitPoint(f, door));
        (v.OutOf, v.OutVia) = (f.Id, Grid.TrySpiral(exit, 0, out var via) ? via : exit);
        Assign(v, new Order(OrderKind.Move, RallyCell(f), 0, 0));
        RequestRoute(v, RallyCell(f));
        return v;
    }

    /// <summary>A factory's rally cell: its rally target's front (a building) or cell (a unit), its set cell, or a few cells in front of it.</summary>
    public Cell RallyCell(Unit f)
    {
        if (Unit(f.RallyTarget) is { } t)
        {
            if (t.Def.IsStructure)
            {
                var o = FootprintOrigin(t);
                return new Cell(o.X + FootW(t) / 2, o.Y + FootH(t) + 1);
            }
            return Cell.Of(t.Pos);
        }
        if (f.Rally != default)
        {
            return f.Rally;
        }
        var fo = FootprintOrigin(f);
        if (f.Def.Archetype.Production?.Rally is { } r)
        {
            return new Cell(fo.X + r.X / 10, fo.Y + r.Y / 10);
        }
        return new Cell(fo.X + f.Def.Archetype.FootprintW / 2, fo.Y + f.Def.Archetype.FootprintH + RallyCells);
    }

    /// <summary>Where door i's units come out: in front of it (the factory's front faces south, +y).</summary>
    public static Vec ExitPoint(Unit f, int door)
    {
        var o = FootprintOrigin(f);
        var a = f.Def.Archetype;
        if (a.Production is { Exits.Count: > 0 } pe && door < pe.Exits.Count)
        {
            return Tenths(o, pe.Exits[door]);
        }
        var n = Math.Max(1, a.Production?.Doors ?? 1);
        var x = (long)o.X * Fixed.OneRaw + (long)a.FootprintW * Fixed.OneRaw * (2 * door + 1) / (2 * n);
        var y = (long)(o.Y + a.FootprintH) * Fixed.OneRaw + Fixed.OneRaw * 3 / 2;
        return new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y));
    }

    /// <summary>Where door i's units appear: the bay floor inside, behind the door.</summary>
    public static Vec BayPoint(Unit f, int door)
    {
        var o = FootprintOrigin(f);
        var a = f.Def.Archetype;
        if (a.Production is { BayPoints.Count: > 0 } pe && door < pe.BayPoints.Count)
        {
            return Tenths(o, pe.BayPoints[door]);
        }
        var e = ExitPoint(f, door);
        return new Vec(e.X, Fixed.FromRaw((long)(o.Y + a.FootprintH - 4) * Fixed.OneRaw));
    }

    private static Vec Tenths(Cell o, (int X, int Y) p) =>
        new(Fixed.FromRaw((long)o.X * Fixed.OneRaw + (long)p.X * Fixed.OneRaw / 10), Fixed.FromRaw((long)o.Y * Fixed.OneRaw + (long)p.Y * Fixed.OneRaw / 10));

    // InLane counts the friendly and the enemy ground units standing in door i's exit lane (a box
    // LaneHalf cells either side of the exit, LaneDepth cells out from the wall).
    private (int Friends, int Enemies) InLane(Unit f, int door)
    {
        int friends = 0, enemies = 0;
        foreach (var u in _units)
        {
            if (InLaneBox(f, door, u))
            {
                if (u.Owner == f.Owner)
                {
                    friends++;
                }
                else
                {
                    enemies++;
                }
            }
        }
        return (friends, enemies);
    }

    private bool InLaneBox(Unit f, int door, Unit u, bool itsOwn = false)
    {
        if (!u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Def.Layer != Layers.Ground || u.Def.IsStructure || (u.Id == f.Doors[door].Vehicle && !itsOwn))
        {
            return false;
        }
        var e = ExitPoint(f, door);
        var wall = (long)(FootprintOrigin(f).Y + f.Def.Archetype.FootprintH) * Fixed.OneRaw;
        return Math.Abs(u.Pos.X.Raw - e.X.Raw) <= (long)LaneHalf * Fixed.OneRaw && u.Pos.Y.Raw >= wall && u.Pos.Y.Raw < wall + (long)LaneDepth * Fixed.OneRaw;
    }

    // StepAside tells the factory owner's idle or moving units in door i's exit lane to step aside:
    // across the lane, AsideCells from its middle on the side they stand (west on a tie), to the
    // nearest free cell there (Generals-style; enemies are never moved).
    private void StepAside(Unit f, int door)
    {
        var e = ExitPoint(f, door);
        foreach (var u in _units)
        {
            if (u.Owner != f.Owner || !InLaneBox(f, door, u) || !u.CanAct || u.Def.Speed.Raw == 0)
            {
                continue;
            }
            if (u.ServiceStage >= ServiceStage.ToDoor)
            {
                continue; // on its way into a door of its garage
            }
            var side = u.Pos.X.Raw > e.X.Raw ? 1 : -1;
            var want = new Cell(Cell.Of(e).X + side * AsideCells, Cell.Of(u.Pos).Y);
            if (Grid.TrySpiral(want, 0, out var to))
            {
                if (u.ServiceAt == 0)
                {
                    Assign(u, new Order(OrderKind.Move, to, 0, 0));
                }
                RequestRoute(u, to); // a vehicle waiting for service steps aside and keeps its place
            }
        }
    }

    // Produce queues the command's unit at each listed factory of the player that can make it.
    private void Produce(Command c)
    {
        var def = Match.Players[c.Player - 1].Find(Content, c.Unit ?? "");
        if (def is null)
        {
            return;
        }
        foreach (var id in c.Units)
        {
            if (Unit(id) is not { } f || f.Owner != c.Player || !f.Alive || f.Def.Archetype.Production is not { } pr ||
                !def.Archetype.Tags.Matches(pr.Tags) || pr.Tags.IsEmpty)
            {
                continue;
            }
            if (pr.IntoStation && f.Def.Archetype.Helipad is not null)
            {
                if (def.Archetype.Flight?.Rotor is null || HeliBound(f))
                {
                    continue; // a helipad builds one helicopter, onto itself, while nothing else is bound for it or on it
                }
            }
            else if (pr.IntoStation && (def.Archetype.Aircraft is not { } ac || ProducerField(f) is not { } af || !af.Type.Serves(ac.SizeClass) ||
                (ac.Landing == LandingKind.Runway && af.HasRunway && !PatternFits(af, ac)) || !HeavyBayFor(f, af, ac)))
            {
                continue; // an airfield builds only aircraft of the classes it serves, whose corridor and pattern fit there (E9); a heavy only with a heavy bay free (H1)
            }
            var busy = f.Bays.Count(b => b.Def >= 0) + f.Doors.Count(d => d.Def >= 0);
            if (f.ProductionQueue.Count + busy >= pr.QueueMax)
            {
                continue;
            }
            if (ExclusiveOf(c.Player, def.Archetype) is { Allowed: false })
            {
                continue; // an exclusive group's rule: one heavy bomber at a time, its type locked by the first (ExclusiveSystem.cs)
            }
            f.ProductionQueue.Add(def.Index);
            Charge(c.Player, def);
        }
    }

    // ProducerField is the airfield an into-station factory builds for: its facility's (the forward airfield), or a
    // main air base's whose hangar it is (H1); null for anything else.
    private Airfield? ProducerField(Unit f) =>
        AirfieldOf(f) is { } af && (af.Facility == f.Id || (af.Hangar == f.Id && af.Hangar != 0)) ? af : null;

    // ProducerWorks: an into-station factory produces only while its airfield's facility stands (H1: a main air
    // base's hangar stops while its command building is rubble or gone; the forward facility is its own facility).
    private bool ProducerWorks(Unit f) =>
        f.Def.Archetype.Production is not { IntoStation: true } || f.Def.Archetype.Helipad is not null ||
        (ProducerField(f) is { } af && PieceStands(Unit(af.Facility)));

    // HeavyBayFor: an aircraft only heavy bays take (H1: class L and XL) is queued at factory f only while af has
    // more free heavy bays that fit it than f already has such aircraft queued or in its bays.
    private bool HeavyBayFor(Unit f, Airfield af, AircraftSpec ac)
    {
        if (!OnlyHeavyBays(af, ac))
        {
            return true;
        }
        var free = 0;
        foreach (var r in af.RowList)
        {
            foreach (var st in r.Stations)
            {
                if (st.Kind == StationKind.Heavy && st.Occupant == 0 && st.ReservedBy == 0 && Fits(af, st, ac))
                {
                    free++;
                }
            }
        }
        var pending = 0;
        foreach (var d in f.ProductionQueue)
        {
            pending += Match.Defs[d].Archetype.Aircraft is { } q && OnlyHeavyBays(af, q) ? 1 : 0;
        }
        foreach (var b in f.Bays)
        {
            pending += b.Def >= 0 && Match.Defs[b.Def].Archetype.Aircraft is { } q && OnlyHeavyBays(af, q) ? 1 : 0;
        }
        return free > pending;
    }

    // OnlyHeavyBays: the only stations of af's type that take aircraft ac are heavy bays (H1).
    private bool OnlyHeavyBays(Airfield af, AircraftSpec ac)
    {
        var t = af.Type;
        bool Takes(int a) => a >= 0 && Content.Archetypes[a].Airfield is { } s && s.Takes(ac.SizeClass);
        return Takes(t.Heavy) && !Takes(t.Loading) && !Takes(t.Normal);
    }

    /// <summary>
    /// The number of free heavy bays of a main air base (H1): standing, with no aircraft parked or bound there. Zero for
    /// an airfield without a hangar. For the base panel.
    /// </summary>
    public int FreeHeavyBays(Airfield af)
    {
        var n = 0;
        foreach (var r in af.RowList)
        {
            foreach (var st in r.Stations)
            {
                n += st.Kind == StationKind.Heavy && st.Occupant == 0 && st.ReservedBy == 0 && StationStands(af, st) ? 1 : 0;
            }
        }
        return n;
    }

    /// <summary>
    /// ParkProduced puts aircraft def, just built by airfield facility (or a main air base's hangar, H1) f, into a free
    /// station of f's airfield (an open stand first, then a loading shelter, then a heavy bay; rows and slots in order; a
    /// station standing, of its class and wide enough), parked at its stop mark facing out as an aircraft that taxied in,
    /// with full fuel and stocks, held until its first order (a Move or an AirTask sends it off). False when none is free.
    /// </summary>
    private bool ParkProduced(Unit f, UnitDef def)
    {
        if (def.Archetype.Flight?.Rotor is not null)
        {
            return ParkHeli(f, def); // a helicopter: onto its helipad, or a free stand of the hangar's base (HeliPadSystem.cs)
        }
        if (ProducerField(f) is not { } af || def.Archetype.Aircraft is not { } ac)
        {
            return false;
        }
        foreach (var kind in (ReadOnlySpan<StationKind>)[StationKind.Normal, StationKind.Loading, StationKind.Heavy])
        {
            foreach (var r in ServedRows(af)) // (H1: a base's linked forward airfields' stations too)
            {
                foreach (var st in r.Stations)
                {
                    if (st.Kind != kind || st.Occupant != 0 || st.ReservedBy != 0 || !Fits(af, st, ac))
                    {
                        continue;
                    }
                    var slot = SlotsOf(af.Type, r)[st.Slot];
                    var at = SlotPoint(af, r, slot.ParkIn[^1]);
                    var u = Unit(Spawn(def, Cell.Of(at), MapFacing(r.Field ?? af, slot.Heading)))!;
                    u.Pos = at;
                    u.TurretFacing = u.Facing;
                    u.Altitude = Fixed.FromRaw(GroundRaw(at));
                    // Since: the tick it rolled out into its station (a renderer opens a heavy bay's door for it).
                    u.AirOps = new AirOps(af.Id, Cell.Of(f.Pos)) { Phase = FlightPhase.Parked, Station = st.Unit, Hold = true, Since = Tick };
                    st.Occupant = u.Id;
                    _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.Parked, u.Id, af.Id, u.Pos));
                    return true;
                }
            }
        }
        return false;
    }

    // CancelProduce drops one order of the player's factory: the last (Index -1), a bay's (Index
    // below the bay count; only while it is still in its bay, not given a door), or a waiting one.
    private void CancelProduce(Command c)
    {
        if (Unit(c.Target) is not { } f || f.Owner != c.Player || f.Def.Archetype.Production is null)
        {
            return;
        }
        int? dropped = null;
        if (c.Index < 0)
        {
            if (f.ProductionQueue.Count > 0)
            {
                dropped = f.ProductionQueue[^1];
                f.ProductionQueue.RemoveAt(f.ProductionQueue.Count - 1);
            }
            else
            {
                for (var i = f.Bays.Length - 1; i >= 0 && dropped is null; i--)
                {
                    dropped = CancelBay(f, f.Bays[i]);
                }
            }
        }
        else if (c.Index < f.Bays.Length)
        {
            dropped = CancelBay(f, f.Bays[c.Index]);
        }
        else if (c.Index - f.Bays.Length < f.ProductionQueue.Count)
        {
            dropped = f.ProductionQueue[c.Index - f.Bays.Length];
            f.ProductionQueue.RemoveAt(c.Index - f.Bays.Length);
        }
        if (dropped is { } d)
        {
            Refund(c.Player, Match.Defs[d], 100);
        }
    }

    private static int? CancelBay(Unit f, Bay b)
    {
        if (b.Def < 0)
        {
            return null; // empty
        }
        var d = b.Def;
        if (b.Door >= 0 && b.Door < f.Doors.Length)
        {
            f.Doors[b.Door].Use = DoorUse.None; // the shut door it built behind is free again
        }
        (b.Def, b.Left, b.Wait, b.Door) = (-1, 0, 0, -1);
        return d;
    }

    // SetRally sets the listed factories' rally point: next to the target unit, or the cell.
    private void SetRally(Command c)
    {
        foreach (var id in c.Units)
        {
            if (Unit(id) is { } f && f.Owner == c.Player && f.Def.Archetype.Production is not null)
            {
                (f.Rally, f.RallyTarget) = (c.Target != 0 && Unit(c.Target) is not null ? default : c.Cell, c.Target != 0 && Unit(c.Target) is not null ? c.Target : 0);
            }
        }
    }
}
