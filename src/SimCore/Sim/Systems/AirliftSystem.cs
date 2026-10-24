using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>What an <see cref="AirliftEvent"/> reports (the airlifter's vehicle hold).</summary>
public enum AirliftEventKind : byte
{
    /// <summary>A vehicle started up the airlifter's ramp (it is aboard <see cref="Model.AirliftSpec.BoardTicks"/> later).</summary>
    Boarding,

    /// <summary>A vehicle is aboard (off the map, inside the airlifter).</summary>
    Aboard,

    /// <summary>The airlifter's cargo pallets were taken off to make room for vehicles (free: at a main air base); Vehicle is 0.</summary>
    PalletsOff,

    /// <summary>A vehicle drove off the ramp (at a delivery, or let out by Exit).</summary>
    DroveOff,

    /// <summary>A vehicle was lost with the airlifter (shot down or destroyed with it aboard).</summary>
    Lost,
}

/// <summary>An airlift event of the last tick (<see cref="World.AirliftEvents"/>), for renderers and the HUD.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Aircraft">The airlifter.</param>
/// <param name="Vehicle">The vehicle (0 for <see cref="AirliftEventKind.PalletsOff"/>).</param>
/// <param name="At">Where: the ramp point (boarding, driving off) or the airlifter.</param>
public readonly record struct AirliftEvent(AirliftEventKind Kind, uint Aircraft, uint Vehicle, Vec At);

/// <summary>An airlifter's hold (<see cref="World.AirliftOf"/>): the vehicles aboard, its seats, and the vehicle going up the ramp (0: none).</summary>
/// <param name="Aboard">Vehicles aboard.</param>
/// <param name="Seats">Its seats (<see cref="Model.AirliftSpec.Seats"/>).</param>
/// <param name="Boarding">The vehicle on its ramp now, or 0.</param>
/// <param name="BoardLeft">Ticks until that vehicle is aboard, or 0.</param>
public readonly record struct AirliftStatus(int Aboard, int Seats, uint Boarding, int BoardLeft);

// The airlift (the light tank night, 2026-10-24; docs/design.md, "Light tank"): an airlifter (archetype airlift: AirliftSpec) parked at
// a station of an airfield of its side with unlimited supply (a main air base) takes vehicles with one of its tags (the Booker:
// "airliftable") ordered to Enter it. Each drives to the ramp point behind its tail and, within the board range and with the ramp
// free, goes up it in BoardTicks (one at a time, the lowest ID first), then is aboard (Unit.Inside: off the map). The first to start
// up takes the cargo pallets off (free: the base's supply is unlimited); while a seat is in use (Unit.Lift) it loads no pallets and
// takes no Airdrop order: it carries vehicles or pallets, never both. Boarding holds it parked. On a Deliver (to a forward airbase:
// AirdropSystem.cs) it lands and, at its stand, lets one vehicle drive off every UnloadTicks (the first once the ramp is down), each
// put on the ramp point facing away and sent to the field garage's rally point; then it goes home to load pallets again. Exit on it,
// parked, lets them all out where it stands. Destroyed with vehicles aboard (in the air or on the ground), they and their crews are
// lost (RecordAirLosses). Determinism: units in ID order; Lift is recounted every tick before boarding; both fields hashed when set.
public sealed partial class World
{
    private readonly List<AirliftEvent> _airliftEvents = [];

    /// <summary>The airlift events of the last tick, in order (boarding, aboard, pallets off, drove off, lost).</summary>
    public IReadOnlyList<AirliftEvent> AirliftEvents => _airliftEvents;

    /// <summary>The vehicles aboard airlifter u, in ID order (empty for anything without an airlift hold).</summary>
    public IReadOnlyList<Unit> AirliftAboard(Unit u)
    {
        var list = new List<Unit>();
        if (u.Def.Archetype.Airlift is null)
        {
            return list;
        }
        foreach (var o in _units)
        {
            if (o.Inside == u.Id && o.Alive)
            {
                list.Add(o);
            }
        }
        return list;
    }

    /// <summary>Airlifter u's hold: vehicles aboard, seats, the vehicle on its ramp and its ticks left; null without a hold.</summary>
    public AirliftStatus? AirliftOf(Unit u)
    {
        if (u.Def.Archetype.Airlift is not { } al)
        {
            return null;
        }
        var b = Boarder(u, 0);
        return new AirliftStatus(AirliftAboard(u).Count, al.Seats, b?.Id ?? 0, b?.AirliftBoard ?? 0);
    }

    /// <summary>Airlifter u's ramp point: <see cref="AirliftSpec.Ramp"/> behind its centre along its tail (where vehicles board and drive off).</summary>
    public static Vec AirliftRamp(Unit u, AirliftSpec al) => u.Pos - Facing.Dir(u.Facing).WithLen(al.Ramp);

    /// <summary>
    /// Where vehicle u drives to board airlifter v, and what its board range counts from: v's ramp point when u's movement class
    /// can stand on its cell, else the centre of the nearest cell (straight-line, ties by row then column, within 16 cells) it can
    /// stand on. In a heavy bay the ramp point is just inside the door, on ground the routing keeps a tank's hull off, so the
    /// vehicle's route ends short of it; counting the range from where the route really ends lets the second of two vehicles board
    /// wherever it came from (before, it could stop 4.1 cells from the ramp point, past the 4-cell range, and wait for ever).
    /// </summary>
    public Vec AirliftBoardSpot(Unit u, Unit v)
    {
        if (v.Def.Archetype.Airlift is not { } al)
        {
            return v.Pos;
        }
        var ramp = AirliftRamp(v, al);
        var r = SyncRouting();
        var k = r.ClassOf(u.Def);
        var c = Cell.Of(ramp);
        if (k < 0 || !InGrid(c.X, c.Y) || r.Passable(k, r.Index(c)))
        {
            return ramp;
        }
        var best = ramp;
        var bestD = long.MaxValue;
        for (var d = 1; d <= BoardSpotSearch; d++)
        {
            // Every cell of ring d is at least d - 1 cells from the ramp point: once that is past the best, nothing nearer is left.
            var min = (long)(d - 1) * Fixed.OneRaw;
            if (bestD != long.MaxValue && min * min > bestD)
            {
                break;
            }
            for (var y = c.Y - d; y <= c.Y + d; y++)
            {
                var edge = y == c.Y - d || y == c.Y + d;
                for (var x = c.X - d; x <= c.X + d; x += edge ? 1 : 2 * d)
                {
                    if (!InGrid(x, y) || !r.Passable(k, r.Index(new Cell(x, y))))
                    {
                        continue;
                    }
                    var at = new Cell(x, y).Center();
                    var dd = (at - ramp).LenSq();
                    if (dd < bestD)
                    {
                        (best, bestD) = (at, dd); // rows then columns ascending: the first of equals stays
                    }
                }
            }
        }
        return best;
    }

    private const int BoardSpotSearch = 16; // cells searched round the ramp point for one a vehicle can stand on (routing's DestSearch)

    private bool InGrid(int x, int y) => x >= 0 && y >= 0 && x < Grid.Width && y < Grid.Height;

    /// <summary>
    /// Whether airlifter v takes vehicles now: parked at a station (not leaving) of an airfield of its side with unlimited supply (a
    /// main air base).
    /// </summary>
    public bool AirliftDocked(Unit v) =>
        v.Alive && v.AirOps is { Phase: FlightPhase.Parked, Launch: false } ops && ops.Station != 0 && Airfield(ops.Airfield) is { } af &&
        af.Owner == v.Owner && AirfieldStore(af) is null;

    /// <summary>
    /// Whether vehicle u may board airlifter v (or go on boarding): v its side's, docked (<see cref="AirliftDocked"/>), a seat free
    /// (or u already on the ramp), u a ground vehicle on the map with one of the hold's tags.
    /// </summary>
    public bool CanAirlift(Unit u, Unit v) =>
        u.Alive && v.Id != u.Id && v.Owner == u.Owner && v.Def.Archetype.Airlift is { } al && u.Inside == 0 && u.Garrison == 0 &&
        !u.Def.IsStructure && u.Def.Layer == Layers.Ground && u.Def.Archetype.Flight is null && !al.Tags.IsEmpty && u.Def.Tags.Matches(al.Tags) &&
        (u.AirliftBoard > 0 || v.Lift < al.Seats) && AirliftDocked(v);

    // Boarder is the vehicle (other than `but`) going up airlifter v's ramp now, or null.
    private Unit? Boarder(Unit v, uint but)
    {
        foreach (var o in _units)
        {
            if (o.AirliftBoard > 0 && o.Id != but && o.Order.Kind == OrderKind.Enter && o.Order.Target == v.Id)
            {
                return o;
            }
        }
        return null;
    }

    // RecountAirlift (the boarding phase, first): every airlifter's seats in use (Unit.Lift): the vehicles aboard and the one on its
    // ramp; a vehicle whose Enter order to an airlifter ended stops boarding.
    private void RecountAirlift()
    {
        var any = false;
        foreach (var u in _units)
        {
            if (u.Def.Archetype.Airlift is not null)
            {
                u.Lift = 0;
                any = true;
            }
        }
        if (!any)
        {
            foreach (var u in _units)
            {
                u.AirliftBoard = 0;
            }
            return;
        }
        foreach (var u in _units)
        {
            if (u.AirliftBoard > 0 && !(u.Order.Kind == OrderKind.Enter && Unit(u.Order.Target) is { Def.Archetype.Airlift: not null }))
            {
                u.AirliftBoard = 0; // given another order on the ramp: it backs off
            }
            if (u.Inside != 0 && u.Alive && Unit(u.Inside) is { Def.Archetype.Airlift: not null } v)
            {
                v.Lift++;
            }
            else if (u.AirliftBoard > 0 && Unit(u.Order.Target) is { } t)
            {
                t.Lift++;
            }
        }
    }

    // BoardAirlift runs vehicle u's Enter order to airlifter v (the boarding system): no longer allowed, it stops; on the ramp, a tick
    // of it (aboard when done); within the board range of the ramp point with the ramp free, it starts up (the first takes the
    // pallets off and holds the airlifter parked).
    private void BoardAirlift(Unit u, Unit v)
    {
        var al = v.Def.Archetype.Airlift!;
        if (!CanAirlift(u, v))
        {
            u.AirliftBoard = 0;
            u.Idle();
            return;
        }
        if (u.AirliftBoard > 0)
        {
            u.ClearPath();
            if (--u.AirliftBoard > 0)
            {
                return;
            }
            u.Idle();
            u.AutoTarget = 0;
            u.SalvoLeft = 0;
            u.Inside = v.Id;
            u.Pos = v.Pos;
            _airliftEvents.Add(new AirliftEvent(AirliftEventKind.Aboard, v.Id, u.Id, v.Pos));
            return;
        }
        var ramp = AirliftRamp(v, al);
        if (!Within(u.Pos, AirliftBoardSpot(u, v), al.BoardRange) || Boarder(v, u.Id) is not null)
        {
            return; // on its way, or waiting for the ramp (the range counts from where its route ends: AirliftBoardSpot)
        }
        if (AirdropWeapon(v) is { Stock: >= 0 } w && v.Charges[w.Stock] > 0)
        {
            v.MutableCharges[w.Stock] = 0; // the pallets come off (free at a main air base): it carries vehicles now
            _airliftEvents.Add(new AirliftEvent(AirliftEventKind.PalletsOff, v.Id, 0, ramp));
        }
        v.AirOps!.Hold = true; // it waits for its vehicles (its next order takes it off)
        u.ClearPath();
        u.AirliftBoard = al.BoardTicks;
        v.Lift++;
        _airliftEvents.Add(new AirliftEvent(AirliftEventKind.Boarding, v.Id, u.Id, ramp));
    }

    // DriveOffParked runs a tick of parked airlifter u's delivery while it has vehicles aboard (DeliverParked): one drives off every
    // UnloadTicks, the first once the ramp is down, to the field garage's rally point. Always true (still unloading).
    private bool DriveOffParked(Unit u, Airfield af, AirOps ops, AirliftSpec al, Unit vehicle)
    {
        if (ops.UnloadLeft == 0)
        {
            ops.UnloadLeft = al.UnloadTicks;
        }
        if (--ops.UnloadLeft > 0)
        {
            return true;
        }
        LetOut(u, al, vehicle, 0);
        if (Unit(af.Facility) is { Alive: true, Def.Archetype.Production: not null } garage)
        {
            var rally = RallyCell(garage);
            Assign(vehicle, new Order(OrderKind.Move, rally, 0, 0));
            RequestRoute(vehicle, rally);
        }
        return true;
    }

    // LetOut puts vehicle u out of airlifter v on the n-th free cell from its ramp point, facing away from it.
    private void LetOut(Unit v, AirliftSpec al, Unit u, int n)
    {
        var ramp = AirliftRamp(v, al);
        u.Inside = 0;
        u.Idle();
        u.AutoTarget = 0;
        u.SalvoLeft = 0;
        u.ClearPath();
        u.Pos = Grid.TrySpiral(Cell.Of(ramp), n, out var c) ? c.Center() : ramp;
        u.Facing = Facing.Wrap(v.Facing + Facing.Count / 2);
        u.TurretFacing = u.Facing;
        _airliftEvents.Add(new AirliftEvent(AirliftEventKind.DroveOff, v.Id, u.Id, u.Pos));
    }

    // AirliftExit runs Exit on airlifter v: parked, every vehicle aboard drives off at once onto the free cells behind it.
    private void AirliftExit(Unit v)
    {
        if (v.Def.Archetype.Airlift is not { } al || v.AirOps is not { Phase: FlightPhase.Parked })
        {
            return;
        }
        var k = 0;
        foreach (var u in AirliftAboard(v))
        {
            LetOut(v, al, u, k++);
        }
    }

    // Lifting: airlifter u has a vehicle aboard or going up its ramp, counted now (Unit.Lift is recounted only at the boarding
    // phase). Parked so, it never leaves on its own (the sortie rule after a Rearm visit): only on an order (Deliver, Move, an air
    // task), decision 315.
    private bool Lifting(Unit u)
    {
        if (u.Def.Archetype.Airlift is null)
        {
            return false;
        }
        foreach (var o in _units)
        {
            if (o.Alive && (o.Inside == u.Id || (o.AirliftBoard > 0 && o.Order.Kind == OrderKind.Enter && o.Order.Target == u.Id)))
            {
                return true;
            }
        }
        return false;
    }

    // InAirlifter: u is aboard an airlifter (it and its driver get out only where the airlifter lets them).
    private bool InAirlifter(Unit u) => u.Inside != 0 && Unit(u.Inside) is { Def.Archetype.Airlift: not null };

    // AirliftLost: airlifter u died (RecordAirLosses): every vehicle aboard is lost with it, and their crews.
    private void AirliftLost(Unit u)
    {
        if (u.Def.Archetype.Airlift is null)
        {
            return;
        }
        foreach (var o in AirliftAboard(u))
        {
            o.HP = 0;
            foreach (var c in _units)
            {
                if (c.Inside == o.Id)
                {
                    c.HP = 0; // its driver (and any rider) dies in it
                }
            }
            o.Driver = 0;
            _airliftEvents.Add(new AirliftEvent(AirliftEventKind.Lost, u.Id, o.Id, u.Pos));
        }
    }
}
