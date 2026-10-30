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
    private readonly List<BailOutEvent> _bailOuts = [];

    /// <summary>
    /// The passengers that left a destroyed vehicle in the last tick, alive at its rear door or dead (vehicle ID order, then
    /// passenger ID order), with the killing hit and the survival band (for renderers; not state).
    /// </summary>
    public IReadOnlyList<BailOutEvent> BailOuts => _bailOuts;

    private void RunBoarding()
    {
        RunDismounts();
        RecountAirlift(); // the airlifters' seats in use (AirliftSystem.cs)
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
            if (v?.Def.Archetype.Airlift is not null)
            {
                BoardAirlift(u, v); // an airlifter's vehicle hold (AirliftSystem.cs)
                continue;
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
            if (Within(u.Pos, BoardPoint(u, v), v.Def.Crew!.EnterRange))
            {
                if (CanBoard(u, v))
                {
                    Board(u, v);
                    if (v.Def.IsStructure)
                    {
                        _emplacementEvents.Add(new EmplacementEvent(EmplacementEventKind.CrewIn, v.Id, u.Id, v.Pos, 0, 0));
                    }
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
        u.Inside == 0 && u.Garrison == 0 && PassengerAllowed(v, u) && CountPassengers(v) < crew.Passengers;

    /// <summary>
    /// Whether vehicle v's passenger seats take a unit like u at all, whatever the seats left, the owner or where u is: v has
    /// passenger seats, u is a ground unit that is neither a vehicle with a crew nor a structure, has one of the seats'
    /// <c>passengerTags</c> (any, if none) and none of their <c>passengerExcludeTags</c> (the Merkava type: soldiers, not the
    /// heavy weapon soldiers). For the client's selection card and boarding cursor.
    /// </summary>
    public static bool PassengerAllowed(Unit v, Unit u) =>
        v.Def.Crew is { Passengers: > 0 } crew && u.Def.Crew is null && !u.Def.IsStructure && u.Def.Layer == Layers.Ground &&
        u.Def.Tags.Matches(crew.PassengerTags) && (crew.PassengerExcludeTags.IsEmpty || !u.Def.Tags.Matches(crew.PassengerExcludeTags));

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

    /// <summary>
    /// The rear door of vehicle v: the point a cell behind its hull by its facing (its radius plus a cell back from its centre),
    /// where its passengers get out (an unload, an Exit, a bail-out) and, with crew <c>door: "rear"</c> (the Merkava type), get
    /// in. It turns with the hull.
    /// </summary>
    public static Vec DoorPoint(Unit v) => v.Pos - Facing.Dir(v.Facing).WithLen(v.Def.Archetype.Radius + Fixed.One);

    // RearCell returns the cell just behind v, where its passengers get out (they spread round it). Behind a rear door (crew door
    // "rear") it is a cell further back, so the ring round it stays clear of the hull: they come out behind it, not beside it.
    private static Cell RearCell(Unit v) =>
        Cell.Of(v.Def.Crew is { RearDoor: true } ? v.Pos - Facing.Dir(v.Facing).WithLen(v.Def.Archetype.Radius + Fixed.FromInt(2)) : DoorPoint(v));

    // BoardPoint is where u boards v from (within its crew's enter range): a crewed emplacement's footprint, a rear door
    // (crew door "rear"), else the vehicle itself.
    internal Vec BoardPoint(Unit u, Unit v) =>
        v.Def.IsStructure ? AimPoint(u.Pos, v) : v.Def.Crew is { RearDoor: true } ? DoorPoint(v) : v.Pos;

    // Dismount puts passenger u out of v on the n-th free cell from v's rear.
    private void Dismount(Unit u, Unit v, int n)
    {
        u.Inside = 0;
        u.Idle();
        u.AutoTarget = 0;
        u.SalvoLeft = 0;
        u.Pos = Grid.TrySpiral(RearCell(v), n, out var c) ? c.Center() : v.Pos;
    }

    // Unload puts every passenger of v out behind it, in ID order; the driver stays. With a crew unloadInterval (the
    // Humvee's fast dismount) they get out one by one instead: the first this tick (RunDismounts, in the boarding phase),
    // then one every interval; an unload already under way goes on as it is.
    private void Unload(Unit v)
    {
        if (v.Def.Crew is { UnloadInterval: > 0 })
        {
            if (v.DismountWait == 0)
            {
                (v.DismountWait, v.Dismounted) = (1, 0);
            }
            return;
        }
        var k = 0;
        foreach (var u in PassengersOf(v))
        {
            Dismount(u, v, k);
            k++;
        }
    }

    // RunDismounts runs the one-by-one unloads (crew unloadInterval), in vehicle ID order: when a vehicle's wait runs out its
    // passenger with the lowest ID gets out behind it, on the next free cell after those out before, and the wait starts
    // again while any is left. It pauses while the vehicle is inside something (a garage bay, a tunnel) and ends when it is
    // empty (passengers may also get out one at a time by Exit) or destroyed (ejection takes the rest).
    private void RunDismounts()
    {
        foreach (var v in _units)
        {
            if (v.DismountWait == 0)
            {
                continue;
            }
            if (!v.Alive || v.Def.Crew is not { UnloadInterval: > 0 } crew)
            {
                (v.DismountWait, v.Dismounted) = (0, 0);
                continue;
            }
            if (v.Inside != 0 || --v.DismountWait > 0)
            {
                continue;
            }
            var riders = PassengersOf(v);
            if (riders.Count > 0)
            {
                Dismount(riders[0], v, v.Dismounted++);
            }
            if (riders.Count > 1)
            {
                v.DismountWait = crew.UnloadInterval;
            }
            else
            {
                (v.DismountWait, v.Dismounted) = (0, 0);
            }
        }
    }

    // CanBoard reports whether u may take v's driver seat: v is u's own
    // vehicle (or finished crewed emplacement: the howitzer) with an empty seat, and u is a soldier the seat accepts.
    internal static bool CanBoard(Unit u, Unit v) =>
        v.Alive && u.Alive && v.Id != u.Id && v.Def.Crew is { } crew && v.Owner == u.Owner && v.Driver == 0 && v.IsBuilt &&
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
        if (v.Def.IsStructure)
        {
            // A crewed emplacement: out on the cell just past the middle of its footprint's south edge (the next free one).
            var o = FootprintOrigin(v);
            d.Pos = Grid.TrySpiral(new Cell(o.X + FootW(v) / 2, o.Y + FootH(v)), 0, out var e) ? e.Center() : v.Pos;
        }
        else
        {
            d.Pos = Grid.TrySpiral(Cell.Of(v.Pos), 1, out var c) ? c.Center() : v.Pos;
        }
        if (v.Def.IsStructure)
        {
            _emplacementEvents.Add(new EmplacementEvent(EmplacementEventKind.CrewOut, v.Id, d.Id, d.Pos, 0, 0)); // next to its footprint
        }
    }

    // RunEjection runs just before cleanup: the driver of each destroyed
    // vehicle survives with the vehicle's crew survival chance, rolled on the
    // world's generator in vehicle ID order, and pops out damaged. Its
    // passengers then roll the same chance one by one in ID order, spread
    // over the cells behind the wreck.
    // A driver killed in the seat of a unit that still stands (an open emplacement's crewman: crew occupantDamage) leaves the seat
    // empty: the unit falls silent until another soldier takes it.
    private void RunEjection()
    {
        foreach (var v in _units)
        {
            if (v.Def.Crew is not { } crew)
            {
                continue;
            }
            if (v.Alive)
            {
                if (v.Driver != 0 && Unit(v.Driver) is not { Alive: true })
                {
                    if (v.Def.IsStructure)
                    {
                        _emplacementEvents.Add(new EmplacementEvent(EmplacementEventKind.CrewLost, v.Id, v.Driver, v.Pos, 0, 0));
                    }
                    v.Driver = 0;
                    v.Idle();
                    v.AutoTarget = 0;
                    v.SalvoLeft = 0;
                }
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
                    if (!d.Alive)
                    {
                        // Killed at his post by the same hit (an open emplacement's crewman): nothing to bail out.
                        if (v.Def.IsStructure)
                        {
                            _emplacementEvents.Add(new EmplacementEvent(EmplacementEventKind.CrewLost, v.Id, d.Id, d.Pos, 0, 0));
                        }
                    }
                    else
                    {
                        BailOut(d, crew);
                        if (v.Def.IsStructure)
                        {
                            _emplacementEvents.Add(new EmplacementEvent(EmplacementEventKind.CrewBailed, v.Id, d.Id, d.Pos, d.HP > 0 ? 1 : 0, 0));
                        }
                    }
                }
            }
            if (crew.Passengers == 0)
            {
                continue;
            }
            var k = 0;
            var band = crew.SurviveBandOf(v.KillHit);
            foreach (var p in PassengersOf(v))
            {
                if (!p.Alive)
                {
                    // Killed in it with it (a penetrator obliterates a carrier and everyone inside): nothing bails out.
                    p.Inside = 0;
                    p.Pos = v.Pos;
                    _bailOuts.Add(new BailOutEvent(v.Id, p.Id, p.Pos, false, v.KillHit, band, 0));
                    continue;
                }
                Dismount(p, v, k);
                k++;
                var chance = band >= 0 ? crew.PassengerSurvive[band].Survival : crew.Survival;
                BailOut(p, crew, chance);
                _bailOuts.Add(new BailOutEvent(v.Id, p.Id, p.Pos, p.HP > 0, v.KillHit, band, chance));
            }
        }
    }

    // BailOut rolls whether u survives its vehicle's destruction (the crew's
    // survival chance) and takes the exit damage, or dies.
    private void BailOut(Unit u, CrewSpec crew) => BailOut(u, crew, _rng.Intn(100) < crew.Survival);

    // BailOut with a passenger's chance: without survival bands the crew's roll as above (always drawn, as before); with bands
    // (crew passengerSurvive) the band's chance, rolled on the world's generator only between 0 and 100 (0 and 100 are certain
    // and draw nothing).
    private void BailOut(Unit u, CrewSpec crew, int chance)
    {
        if (crew.PassengerSurvive.Count == 0)
        {
            BailOut(u, crew);
            return;
        }
        BailOut(u, crew, chance >= 100 || (chance > 0 && _rng.Intn(100) < chance));
    }

    // BailOut: u comes out alive less the crew's exit damage, or dies.
    private static void BailOut(Unit u, CrewSpec crew, bool alive) =>
        u.HP = alive ? Math.Max(1, u.HP - u.Def.MaxHp * crew.ExitDamage / 100) : 0;
}

/// <summary>
/// A passenger leaving a destroyed vehicle (<see cref="World.BailOuts"/>), for renderers: the bail-out at the rear door, or the
/// body.
/// </summary>
/// <param name="Vehicle">The destroyed vehicle.</param>
/// <param name="Unit">The passenger.</param>
/// <param name="At">Where it came out (behind the wreck), or the wreck's position when it died inside.</param>
/// <param name="Survived">Whether it bailed out alive (it lost the crew's exit damage) or died.</param>
/// <param name="KillHit">The killing hit's damage before armour (a carrier with survival bands; 0 otherwise and for fire).</param>
/// <param name="Band">The survival band that hit fell in (crew <c>passengerSurvive</c>), or -1 without bands.</param>
/// <param name="Chance">The percent chance it had (0 when it died inside, with the vehicle).</param>
public readonly record struct BailOutEvent(uint Vehicle, uint Unit, Vec At, bool Survived, int KillHit, int Band, int Chance);
