using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The crew system: soldiers on an Enter order climb into their vehicle once
// they reach it, and drivers bail out of destroyed vehicles. A vehicle with
// a driver seat can't move or fire while the seat is empty; any driver is as
// good as any other, and only the owner's soldiers may drive. A vehicle with
// passenger seats (the infantry carrier) also takes riders once the driver
// seat is taken: they can't be seen, hit or fire out, they get out behind it
// (Exit), and when it is destroyed each rolls the driver's survival chance.
public sealed partial class World
{
    private void RunBoarding()
    {
        foreach (var u in _units)
        {
            if (u.Order.Kind != OrderKind.Enter || u.Inside != 0)
            {
                continue;
            }
            var v = Unit(u.Order.Target);
            if (v?.Def.Archetype.Tunnel is not null)
            {
                continue; // a tunnel entrance: the tunnel system takes it in
            }
            if (v?.Heli is not null)
            {
                BoardHeli(u, v); // a helicopter's rider benches (HeliPadSystem.cs)
                continue;
            }
            if (v?.Def.Archetype.Garrison is { } g)
            {
                if (!CanGarrison(u, v))
                {
                    u.Idle();
                }
                else if (Within(u.Pos, DoorCell(v, NearestDoor(v, u.Pos)).Center(), g.EnterRange))
                {
                    EnterBuilding(u, v);
                }
                continue;
            }
            if (v is null || !CanEnter(u, v))
            {
                u.Idle();
                continue;
            }
            if (Within(u.Pos, v.Pos, v.Def.Crew!.EnterRange))
            {
                if (CanBoard(u, v))
                {
                    Board(u, v);
                }
                else
                {
                    Ride(u, v);
                }
            }
        }
    }

    // CanEnter reports whether u may get into v now: its driver seat or a passenger seat.
    internal bool CanEnter(Unit u, Unit v) => CanBoard(u, v) || CanRide(u, v);

    // CanRide reports whether u may take a passenger seat in v: v is u's own
    // vehicle with a free seat, and u is a ground unit the seats accept.
    internal bool CanRide(Unit u, Unit v) =>
        v.Alive && u.Alive && v.Id != u.Id && v.Def.Crew is { Passengers: > 0 } crew && v.Owner == u.Owner &&
        u.Inside == 0 && u.Garrison == 0 && u.Def.Crew is null && !u.Def.IsStructure && u.Def.Layer == Layers.Ground &&
        u.Def.Tags.Matches(crew.PassengerTags) && CountPassengers(v) < crew.Passengers;

    // CountPassengers returns how many units ride in v's passenger seats.
    private int CountPassengers(Unit v)
    {
        var n = 0;
        foreach (var u in _units)
        {
            if (u.Inside == v.Id && u.Id != v.Driver)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>The units riding in vehicle v's passenger seats (not its driver), in ID order.</summary>
    public IReadOnlyList<Unit> PassengersOf(Unit v)
    {
        var list = new List<Unit>();
        if (v.Def.Crew is not { Passengers: > 0 })
        {
            return list;
        }
        foreach (var u in _units)
        {
            if (u.Inside == v.Id && u.Id != v.Driver)
            {
                list.Add(u);
            }
        }
        return list;
    }

    // Ride seats u in one of v's passenger seats at once, wherever u is (the
    // caller checks there is room); scenarios use it to spawn loaded carriers.
    internal static void Ride(Unit u, Unit v)
    {
        u.Idle();
        u.AutoTarget = 0;
        u.SalvoLeft = 0;
        u.Inside = v.Id;
        u.Pos = v.Pos;
    }

    // RearCell returns the cell just behind v, where its passengers get out.
    private static Cell RearCell(Unit v) =>
        Cell.Of(v.Pos - Facing.Dir(v.Facing).WithLen(v.Def.Archetype.Radius + Fixed.One));

    // Dismount puts passenger u out of v on the n-th free cell from v's rear.
    private void Dismount(Unit u, Unit v, int n)
    {
        u.Inside = 0;
        u.Idle();
        u.AutoTarget = 0;
        u.SalvoLeft = 0;
        u.Pos = Grid.TrySpiral(RearCell(v), n, out var c) ? c.Center() : v.Pos;
    }

    // Unload puts every passenger of v out behind it, in ID order; the driver stays.
    private void Unload(Unit v)
    {
        var k = 0;
        foreach (var u in PassengersOf(v))
        {
            Dismount(u, v, k);
            k++;
        }
    }

    // CanBoard reports whether u may take v's driver seat: v is u's own
    // vehicle with an empty seat, and u is a soldier the seat accepts.
    internal static bool CanBoard(Unit u, Unit v) =>
        v.Alive && u.Alive && v.Id != u.Id && v.Def.Crew is { } crew && v.Owner == u.Owner && v.Driver == 0 &&
        u.Inside == 0 && u.Garrison == 0 && u.Def.Crew is null && u.Def.Tags.Matches(crew.Tags);

    // Board seats u in v at once, wherever u is; scenarios use it to spawn
    // crewed vehicles.
    internal static void Board(Unit u, Unit v)
    {
        u.Idle();
        u.AutoTarget = 0;
        u.SalvoLeft = 0;
        u.Inside = v.Id;
        u.Pos = v.Pos;
        v.Driver = u.Id;
    }

    // Unboard puts v's driver on the first free cell next to v.
    private void Unboard(Unit v)
    {
        var d = Unit(v.Driver);
        v.Driver = 0;
        v.Idle();
        v.AutoTarget = 0;
        v.SalvoLeft = 0;
        if (d is null)
        {
            return;
        }
        d.Inside = 0;
        d.Pos = Grid.TrySpiral(Cell.Of(v.Pos), 1, out var c) ? c.Center() : v.Pos;
    }

    // RunEjection runs just before cleanup: the driver of each destroyed
    // vehicle survives with the vehicle's crew survival chance, rolled on the
    // world's generator in vehicle ID order, and pops out damaged. Its
    // passengers then roll the same chance one by one in ID order, spread
    // over the cells behind the wreck.
    private void RunEjection()
    {
        foreach (var v in _units)
        {
            if (v.Alive || v.Def.Crew is not { } crew)
            {
                continue;
            }
            if (v.Driver != 0)
            {
                var d = Unit(v.Driver);
                v.Driver = 0;
                if (d is not null)
                {
                    d.Inside = 0;
                    d.Pos = v.Pos;
                    BailOut(d, crew);
                }
            }
            if (crew.Passengers == 0)
            {
                continue;
            }
            var k = 0;
            foreach (var p in PassengersOf(v))
            {
                Dismount(p, v, k);
                k++;
                BailOut(p, crew);
            }
        }
    }

    // BailOut rolls whether u survives its vehicle's destruction (the crew's
    // survival chance) and takes the exit damage, or dies.
    private void BailOut(Unit u, CrewSpec crew)
    {
        if (_rng.Intn(100) < crew.Survival)
        {
            u.HP = Math.Max(1, u.HP - u.Def.MaxHp * crew.ExitDamage / 100);
        }
        else
        {
            u.HP = 0;
        }
    }
}
