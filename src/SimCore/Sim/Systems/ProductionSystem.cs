using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The production system: factories (structures with Archetype.Production, the vehicle garage)
// produce units from a queue, in their bays at once. Each tick, for every finished factory by ID:
// a free bay takes the oldest waiting order and counts its archetype's BuildTime down; when it is
// done its door opens (DoorTicks); with the door up, friendly units standing in the exit lane are
// told to step aside, enemy units block it, and once it is clear (or after PushAfter ticks of
// waiting, when it goes anyway) the unit comes out at the exit crewed and drives to the rally
// point; when it has cleared the lane the door closes, and the bay is free again. A site under
// construction produces nothing; a destroyed factory loses its queue. Cost: no-op until there is
// an economy (Charge, Refund).
public sealed partial class World
{
    private const int LaneHalf = 2, LaneDepth = 4, AsideCells = 5, RallyCells = 5;

    private void RunProduction()
    {
        var made = new List<(Unit Factory, int Bay, UnitDef Def)>();
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
            for (var i = 0; i < f.Bays.Length; i++)
            {
                var b = f.Bays[i];
                switch (b.Door)
                {
                    case DoorState.Closed when b.Def < 0:
                        if (f.ProductionQueue.Count > 0)
                        {
                            b.Def = f.ProductionQueue[0];
                            f.ProductionQueue.RemoveAt(0);
                            b.Left = Math.Max(1, Match.Defs[b.Def].Archetype.BuildTime);
                        }
                        break;
                    case DoorState.Closed:
                        if (--b.Left <= 0)
                        {
                            (b.Left, b.Door, b.DoorLeft, b.Wait) = (0, DoorState.Opening, pr.DoorTicks, 0);
                            StepAside(f, i);
                        }
                        break;
                    case DoorState.Opening:
                        if (--b.DoorLeft <= 0)
                        {
                            (b.Door, b.DoorLeft) = (DoorState.Open, 0);
                        }
                        break;
                    case DoorState.Open when b.Def < 0:
                        // Out: the door closes once it has left the exit lane (or is gone, or after a while).
                        var v = Unit(b.Vehicle);
                        b.Wait++;
                        if (v is null || !InLaneBox(f, i, v, true) || b.Wait >= pr.PushAfter)
                        {
                            (b.Door, b.DoorLeft, b.Vehicle, b.Wait) = (DoorState.Closing, pr.DoorTicks, 0, 0);
                        }
                        break;
                    case DoorState.Closing:
                        if (--b.DoorLeft <= 0)
                        {
                            (b.Door, b.DoorLeft) = (DoorState.Closed, 0);
                        }
                        break;
                }
                if (b.Door == DoorState.Open && b.Def >= 0)
                {
                    // Waiting to come out: friends step aside, enemies block until PushAfter.
                    var (friends, enemies) = InLane(f, i);
                    if ((friends == 0 && enemies == 0) || b.Wait >= pr.PushAfter)
                    {
                        made.Add((f, i, Match.Defs[b.Def]));
                        (b.Def, b.Wait) = (-1, 0);
                    }
                    else
                    {
                        if (friends > 0 && b.Wait % 20 == 0)
                        {
                            StepAside(f, i);
                        }
                        b.Wait++;
                    }
                }
            }
        }
        foreach (var (f, i, def) in made)
        {
            var v = SpawnCrewed(f, i, def);
            f.Bays[i].Vehicle = v.Id;
        }
    }

    // SpawnCrewed puts a produced unit at bay i's exit, facing out, with its crew aboard, and sends
    // it to the factory's rally point.
    private Unit SpawnCrewed(Unit f, int bay, UnitDef def)
    {
        var exit = Cell.Of(ExitPoint(f, bay));
        if (!Grid.TrySpiral(exit, 0, out var at))
        {
            at = exit;
        }
        var v = Add(def, at.Center(), FixedPoint.Facing.South);
        if (def.Crew is { } crew && Match.Players[def.Player - 1].Find(Content, f.Def.Archetype.Production!.Crew) is { } driverDef &&
            (crew.Tags.IsEmpty || driverDef.Archetype.Tags.Matches(crew.Tags)))
        {
            var d = Add(driverDef, at.Center(), FixedPoint.Facing.South);
            Board(d, v);
        }
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

    /// <summary>Where bay i's units come out: in front of its door (the factory's front faces south, +y).</summary>
    public static Vec ExitPoint(Unit f, int bay)
    {
        var o = FootprintOrigin(f);
        var a = f.Def.Archetype;
        if (a.Production is { Exits.Count: > 0 } pe && bay < pe.Exits.Count)
        {
            var (ex, ey) = pe.Exits[bay];
            return new Vec(Fixed.FromRaw((long)o.X * Fixed.OneRaw + (long)ex * Fixed.OneRaw / 10), Fixed.FromRaw((long)o.Y * Fixed.OneRaw + (long)ey * Fixed.OneRaw / 10));
        }
        var n = Math.Max(1, a.Production?.Bays ?? 1);
        var x = (long)o.X * Fixed.OneRaw + (long)a.FootprintW * Fixed.OneRaw * (2 * bay + 1) / (2 * n);
        var y = (long)(o.Y + a.FootprintH) * Fixed.OneRaw + Fixed.OneRaw * 3 / 2;
        return new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y));
    }

    // InLane counts the friendly and the enemy ground units standing in bay i's exit lane (a box
    // LaneHalf cells either side of the exit, LaneDepth cells out from the wall).
    private (int Friends, int Enemies) InLane(Unit f, int bay)
    {
        int friends = 0, enemies = 0;
        foreach (var u in _units)
        {
            if (InLaneBox(f, bay, u))
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

    private bool InLaneBox(Unit f, int bay, Unit u, bool itsOwn = false)
    {
        if (!u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Def.Layer != Layers.Ground || u.Def.IsStructure || (u.Id == f.Bays[bay].Vehicle && !itsOwn))
        {
            return false;
        }
        var e = ExitPoint(f, bay);
        var wall = (long)(FootprintOrigin(f).Y + f.Def.Archetype.FootprintH) * Fixed.OneRaw;
        return Math.Abs(u.Pos.X.Raw - e.X.Raw) <= (long)LaneHalf * Fixed.OneRaw && u.Pos.Y.Raw >= wall && u.Pos.Y.Raw < wall + (long)LaneDepth * Fixed.OneRaw;
    }

    // StepAside tells the factory owner's idle or moving units in bay i's exit lane to step aside:
    // across the lane, AsideCells from its middle on the side they stand (west on a tie), to the
    // nearest free cell there (Generals-style; enemies are never moved).
    private void StepAside(Unit f, int bay)
    {
        var e = ExitPoint(f, bay);
        foreach (var u in _units)
        {
            if (u.Owner != f.Owner || !InLaneBox(f, bay, u) || !u.CanAct || u.Def.Speed.Raw == 0)
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
            var busy = f.Bays.Count(b => b.Def >= 0);
            if (f.ProductionQueue.Count + busy >= pr.QueueMax)
            {
                continue;
            }
            f.ProductionQueue.Add(def.Index);
            Charge(c.Player, def);
        }
    }

    // CancelProduce drops one order of the player's factory: the last (Index -1), a bay's (Index
    // below the bay count; only while it is still being made), or a waiting one.
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
        if (b.Def < 0 || b.Door != DoorState.Closed)
        {
            return null; // empty, or finished and coming out
        }
        var d = b.Def;
        (b.Def, b.Left) = (-1, 0);
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
