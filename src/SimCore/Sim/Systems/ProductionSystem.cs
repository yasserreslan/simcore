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
public sealed partial class World
{
    private const int LaneHalf = 2, LaneDepth = 4, AsideCells = 5, RallyCells = 5;

    // A finished unit takes the door the last one took only when no other is free and it has
    // waited this long (2 s) for one; a factory with one door just uses it.
    private const int RepeatAfter = 40;

    private void RunProduction()
    {
        var made = new List<(Unit Factory, int Door, UnitDef Def)>();
        foreach (var f in _units)
        {
            if (f.Def.Archetype.Production is not { } pr || !f.Alive || !f.IsBuilt || (long)f.HP * 100 < (long)f.Def.MaxHp * pr.MinHpPercent)
            {
                continue; // a site, or too badly damaged (its doors are wreckage): it waits
            }
            if (f.Bays.Length != pr.Bays)
            {
                f.Bays = [.. Enumerable.Range(0, pr.Bays).Select(_ => new Bay())];
            }
            if (f.Doors.Length != pr.Doors)
            {
                f.Doors = [.. Enumerable.Range(0, pr.Doors).Select(_ => new FactoryDoor())];
            }
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
        }
    }

    // Work runs a bay: it takes the oldest waiting order, counts it down, and gives the finished
    // unit to a door (which opens).
    private void Work(Unit f, ProductionSpec pr, Bay b)
    {
        if (b.Def < 0)
        {
            if (f.ProductionQueue.Count > 0)
            {
                (b.Def, b.Left, b.Wait) = (f.ProductionQueue[0], Math.Max(1, Match.Defs[f.ProductionQueue[0]].Archetype.BuildTime), 0);
                f.ProductionQueue.RemoveAt(0);
            }
            return;
        }
        if (b.Left > 0 && --b.Left > 0)
        {
            return;
        }
        var d = PickDoor(f, b.Wait >= RepeatAfter);
        if (d < 0)
        {
            b.Wait++; // finished: waiting for a door
            return;
        }
        var door = f.Doors[d];
        (door.Def, door.State, door.Left, door.Wait) = (b.Def, DoorState.Opening, pr.DoorTicks, 0);
        (b.Def, b.Left, b.Wait) = (-1, 0, 0);
        (f.LastDoor, f.DoorSerial) = (d, f.DoorSerial + 1);
        StepAside(f, d);
    }

    /// <summary>
    /// The door a factory's next finished unit takes, or -1 to wait: a random one of the free doors
    /// (shut, nothing behind them) other than the one the last unit took, seeded by the match seed,
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
            if (f.Doors[i].State != DoorState.Closed || f.Doors[i].Def >= 0)
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
                    (d.State, d.Left) = (DoorState.Closed, 0);
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
                return new Cell(o.X + t.Def.Archetype.FootprintW / 2, o.Y + t.Def.Archetype.FootprintH + 1);
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
            var side = u.Pos.X.Raw > e.X.Raw ? 1 : -1;
            var want = new Cell(Cell.Of(e).X + side * AsideCells, Cell.Of(u.Pos).Y);
            if (Grid.TrySpiral(want, 0, out var to))
            {
                Assign(u, new Order(OrderKind.Move, to, 0, 0));
                RequestRoute(u, to);
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
            var busy = f.Bays.Count(b => b.Def >= 0) + f.Doors.Count(d => d.Def >= 0);
            if (f.ProductionQueue.Count + busy >= pr.QueueMax)
            {
                continue;
            }
            f.ProductionQueue.Add(def.Index);
            Charge(c.Player, def);
        }
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
                    dropped = CancelBay(f.Bays[i]);
                }
            }
        }
        else if (c.Index < f.Bays.Length)
        {
            dropped = CancelBay(f.Bays[c.Index]);
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

    private static int? CancelBay(Bay b)
    {
        if (b.Def < 0)
        {
            return null; // empty
        }
        var d = b.Def;
        (b.Def, b.Left, b.Wait) = (-1, 0, 0);
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
