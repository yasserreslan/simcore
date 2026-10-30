using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Vehicle service through a garage's doors (the military garage: a factory with a service block; design: the game repo's
// docs/military-garage-design.md). Its doors are shared: production takes up to Bays of them (ProductionSystem.cs) and
// every other door services a vehicle. A Service command sends its side's vehicles with one of the service's tags there:
// each drives toward the nearest door (Approach) and, within ServiceNear cells of a door's exit, waits (Waiting); free
// doors go to waiting vehicles first come first served (the tick they got there, then ID), each the free door nearest to
// it (then the lowest). It drives to that door's exit point (ToDoor), the door opens (doorTicks), it drives in to the bay
// floor through its garage's footprint (DriveIn, OutOf as a vehicle driving out) and is in its bay (InBay): off the map
// like a unit underground (Inside = InGarage: nothing sees, targets, hits or pushes it, it neither sees nor fires, and
// orders don't reach it; the client draws it from the door's Serviced). There, in parallel, every weapon (and smoke
// launchers' charges) is refilled to full after reloadTicks, its HP rises evenly to full over repairTicks and an empty cargo truck is refilled after
// cargoTicks. When nothing is left it drives out through the same door to the garage's rally point (as a produced
// vehicle does), and the door closes once it has left the exit lane. Nothing is charged (no economy yet). A garage that
// is destroyed, unfinished, rubble or below its production minHpPercent services nothing: a vehicle inside or driving in
// drives out to the rally point, the others' orders end. Any other order ends the service of a vehicle not yet in its
// bay (driving in, it drives back out through its door). It runs after production, in the production phase.
public sealed partial class World
{
    private const int ServiceNear = 8;    // cells from a door's exit point where a vehicle waits for a door
    private const int ServiceAtDoor = 2;  // cells from its door's exit point where it is at the door (and stopped: 4)

    private readonly List<Unit> _serviceWaiting = []; // scratch: vehicles waiting for a door this tick

    private void RunService()
    {
        _serviceWaiting.Clear();
        foreach (var u in _units)
        {
            if (u.ServiceAt != 0)
            {
                ServiceStep(u);
            }
        }
        foreach (var g in _units)
        {
            if (g.Def.Archetype.Service is null || !ServiceWorks(g) || g.Doors.Length != g.Def.Archetype.Production!.Doors)
            {
                continue;
            }
            KeepServiceDoors(g);
            if (_serviceWaiting.Count > 0)
            {
                GiveDoors(g);
            }
        }
    }

    /// <summary>
    /// Whether garage g services vehicles now: it has a service block and stands finished, not rubble, at or above its
    /// production's minHpPercent (below it its doors are wreckage).
    /// </summary>
    public bool ServiceWorks(Unit g) =>
        g.Def.Archetype is { Service: not null, Production: { } pr } && g.Alive && g.IsBuilt && !g.Rubble &&
        (long)g.HP * 100 >= (long)g.Def.MaxHp * pr.MinHpPercent;

    // CanService: g's service takes u: its side's, alive and able to act, a ground vehicle (not a structure, an aircraft
    // or a flying drone) with one of the service's tags, out in the open.
    private static bool CanService(Unit g, ServiceSpec sv, Unit u) =>
        u.Owner == g.Owner && u.Alive && u.CanAct && !u.Def.IsStructure && u.Def.Layer == Layers.Ground && u.Def.Speed.Raw > 0 &&
        u.Def.Archetype.Aircraft is null && u.Def.Archetype.Flight is null && u.Def.Strike is null && u.Garrison == 0 &&
        u.Def.Tags.Matches(sv.Tags);

    // ServiceCommand sends the listed vehicles to garage c.Target for service (a vehicle already on its way there keeps
    // its place).
    private void ServiceCommand(Command c)
    {
        if (Unit(c.Target) is not { } g || g.Owner != c.Player || g.Def.Archetype.Service is not { } sv || !ServiceWorks(g))
        {
            return;
        }
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || !CanService(g, sv, u!))
            {
                continue; // the other side's, an aircraft, infantry, a structure, or a vehicle without a driver
            }
            if (u!.ServiceAt == g.Id && u.Order.Kind == OrderKind.Service)
            {
                continue;
            }
            if (u.ServiceAt != 0)
            {
                EndService(u, Unit(u.ServiceAt), false); // sent to another garage
            }
            Assign(u, new Order(OrderKind.Service, Cell.Of(g.Pos), g.Id, 0));
            (u.ServiceAt, u.ServiceStage, u.ServiceSince, u.ServiceDoor) = (g.Id, ServiceStage.Approach, 0, -1);
            if (u.Def.Archetype.DeployTicks > 0 && u.Deploy is DeployState.Deployed or DeployState.Deploying)
            {
                StartStow(u); // it packs up first (movement holds it until stowed)
            }
            RequestRoute(u, Cell.Of(ExitPoint(g, NearestDoor(g, u.Pos, false))));
        }
    }

    // NearestDoor is g's door whose exit point is nearest to at (ties: the lowest), among the free ones when onlyFree
    // (-1 when none is free).
    private int NearestDoor(Unit g, Vec at, bool onlyFree)
    {
        var n = g.Def.Archetype.Production!.Doors;
        var best = -1;
        var bestD = long.MaxValue;
        for (var i = 0; i < n; i++)
        {
            if (onlyFree && !DoorFree(g.Doors[i]))
            {
                continue;
            }
            var d = (ExitPoint(g, i) - at).LenSq();
            if (d < bestD)
            {
                (best, bestD) = (i, d);
            }
        }
        return best;
    }

    // DoorFree: nothing uses door d (shut, nothing behind it, not taken).
    private static bool DoorFree(FactoryDoor d) =>
        d.State == DoorState.Closed && d.Def < 0 && d.Vehicle == 0 && d.Serviced == 0 && d.Use == DoorUse.None;

    // ServiceStep moves vehicle u (sent for service) on: it checks its garage and its order, then its stage.
    private void ServiceStep(Unit u)
    {
        var g = Unit(u.ServiceAt);
        var ordered = u.Order.Kind == OrderKind.Service && u.Order.Target == u.ServiceAt;
        if (g is null || g.Owner != u.Owner || !u.Alive || !ServiceWorks(g) || !ordered || g.Doors.Length == 0)
        {
            EndService(u, g, ordered);
            return;
        }
        var sv = g.Def.Archetype.Service!;
        var pr = g.Def.Archetype.Production!;
        switch (u.ServiceStage)
        {
            case ServiceStage.Approach:
                var near = Within(u.Pos, ExitPoint(g, NearestDoor(g, u.Pos, false)), Fixed.FromInt(ServiceNear));
                if (near || (!u.Moving && !u.PathPending))
                {
                    u.ClearPath(); // there (or as near as it gets): it waits for a door
                    (u.ServiceStage, u.ServiceSince) = (ServiceStage.Waiting, Tick);
                    _serviceWaiting.Add(u);
                }
                break;
            case ServiceStage.Waiting:
                _serviceWaiting.Add(u);
                break;
            case ServiceStage.ToDoor:
                var door = g.Doors[u.ServiceDoor];
                var exit = ExitPoint(g, u.ServiceDoor);
                var stopped = !u.Moving && !u.PathPending;
                if (!Within(u.Pos, exit, Fixed.FromInt(stopped ? 2 * ServiceAtDoor : ServiceAtDoor)))
                {
                    if (stopped)
                    {
                        EndService(u, g, false); // it can't get to its door: the order ends
                    }
                    break;
                }
                if (door.State == DoorState.Closed)
                {
                    (door.State, door.Left) = (DoorState.Opening, pr.DoorTicks);
                }
                else if (door.State == DoorState.Open)
                {
                    DriveIn(u, g, u.ServiceDoor);
                }
                break;
            case ServiceStage.DriveIn:
                var d = g.Doors[u.ServiceDoor];
                if (!u.Moving || ++d.Wait >= pr.PushAfter)
                {
                    IntoBay(u, g, sv, u.ServiceDoor); // on its bay floor (or as far in as it got)
                }
                break;
            case ServiceStage.InBay:
                ServiceInBay(u, g, sv, u.ServiceDoor);
                break;
        }
    }

    // DriveIn starts u through its open door: straight to the bay floor, its garage's footprint not blocking it (OutOf),
    // its route out (if it is ordered away) starting at the door's exit (OutVia).
    private void DriveIn(Unit u, Unit g, int door)
    {
        var exit = Cell.Of(ExitPoint(g, door));
        var bay = Cell.Of(BayPoint(g, door));
        (u.OutOf, u.OutVia) = (g.Id, Grid.TrySpiral(exit, 0, out var via) ? via : exit);
        u.ClearPath(); // no route: a straight path in (routing never re-plans it)
        u.Dest = bay;
        u.SetPath([bay]);
        u.ServiceStage = ServiceStage.DriveIn;
        g.Doors[door].Wait = 0;
    }

    // IntoBay takes u off the map into its bay and starts what it needs: a reload if a weapon is short of ammo, a repair if
    // it is damaged, a cargo refill if it is an empty cargo truck. Logistics: a garage of an airfield with a limited stock (the
    // forward airbase's field garage) pays each part out of that stock as it starts (the reload a round at a time at the stock's
    // round cost or the weapon's depot cost, the repair at its repair cost per 100 HP, a cargo refill the load); a part the stock
    // cannot pay is skipped.
    private void IntoBay(Unit u, Unit g, ServiceSpec sv, int door)
    {
        u.ClearPath();
        (u.Vel, u.DriveSpeed, u.AutoTarget, u.SalvoLeft, u.AimAt, u.AimLeft) = (default, default, 0, 0, 0, 0);
        u.Inside = InGarage;
        u.ServiceStage = ServiceStage.InBay;
        var d = g.Doors[door];
        var needs = ServiceNeeds.None;
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (Serviced(weapons[i]) && u.Charges[weapons[i].Stock] < u.StockMax(weapons[i].Stock))
            {
                needs |= ServiceNeeds.Reload;
            }
        }
        if ((u.Def.SmokeStock is var sk and >= 0 && u.Charges[sk] < u.StockMax(sk)) ||
            (u.Def.ApsStock is var ak and >= 0 && u.Charges[ak] < u.StockMax(ak)))
        {
            needs |= ServiceNeeds.Reload; // smoke launchers and an APS (the tank's Trophy) reload with the weapons
        }
        if (u.HP < u.Def.MaxHp)
        {
            needs |= ServiceNeeds.Repair;
        }
        if (u.Def.Cargo is { } cg && u.Cargo < cg.Amount)
        {
            needs |= ServiceNeeds.Cargo;
        }
        if (AirfieldOf(g) is { } af && AirfieldStore(af) is { } store)
        {
            needs = PayService(u, store, needs);
        }
        d.Needs = needs;
        d.ReloadLeft = (needs & ServiceNeeds.Reload) != 0 ? sv.ReloadTicks : 0;
        d.RepairLeft = (needs & ServiceNeeds.Repair) != 0 ? sv.RepairTicks : 0;
        d.CargoLeft = (needs & ServiceNeeds.Cargo) != 0 ? sv.CargoTicks : 0;
        d.RepairFrom = u.HP;
        d.Wait = 0;
    }

    // PayService pays u's service needs out of store's stock, each in turn (reload, repair, cargo), and returns those it paid for.
    private static ServiceNeeds PayService(Unit u, Unit store, ServiceNeeds needs)
    {
        var sup = store.Def.Supply!;
        if ((needs & ServiceNeeds.Reload) != 0)
        {
            var cost = 0;
            var weapons = u.Def.Weapons;
            for (var i = 0; i < weapons.Count; i++)
            {
                if (Serviced(weapons[i]) && weapons[i].Stock is var k && FirstWeaponOf(u, k) == i)
                {
                    cost += (u.StockMax(k) - u.Charges[k]) * RoundCost(u, k, store);
                }
            }
            if (u.Def.SmokeStock is var sk and >= 0)
            {
                cost += (u.StockMax(sk) - u.Charges[sk]) * RoundCost(u, sk, store); // a smoke charge costs a round
            }
            if (u.Def.ApsStock is var ak and >= 0)
            {
                cost += (u.StockMax(ak) - u.Charges[ak]) * RoundCost(u, ak, store); // so does an APS charge
            }
            needs = Pay(store, cost) ? needs : needs & ~ServiceNeeds.Reload;
        }
        if ((needs & ServiceNeeds.Repair) != 0 && !Pay(store, (int)((long)(u.Def.MaxHp - u.HP) * sup.RepairCost / 100)))
        {
            needs &= ~ServiceNeeds.Repair;
        }
        if ((needs & ServiceNeeds.Cargo) != 0 && !Pay(store, u.Def.Cargo!.Amount - u.Cargo))
        {
            needs &= ~ServiceNeeds.Cargo;
        }
        return needs;
    }

    // Serviced: weapon w has a stock a garage's service refills (not one only a depot reloads: depotReload.noService, the Jericho
    // type's missile).
    private static bool Serviced(WeaponDef w) => w.Stock >= 0 && w.Effect.DepotReload is not { NoService: true };

    // FirstWeaponOf is the index of u's first weapon drawing on stock k (a stock shared by several weapons is paid once).
    private static int FirstWeaponOf(Unit u, int k)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Stock == k)
            {
                return i;
            }
        }
        return -1;
    }

    // ServiceInBay runs a tick of u's service in its bay; with nothing left it drives out.
    private void ServiceInBay(Unit u, Unit g, ServiceSpec sv, int door)
    {
        var d = g.Doors[door];
        if (d.ReloadLeft > 0 && --d.ReloadLeft == 0)
        {
            var charges = u.MutableCharges;
            var weapons = u.Def.Weapons;
            for (var i = 0; i < weapons.Count; i++)
            {
                if (Serviced(weapons[i]))
                {
                    charges[weapons[i].Stock] = u.StockMax(weapons[i].Stock);
                }
            }
            if (u.Def.SmokeStock >= 0)
            {
                charges[u.Def.SmokeStock] = u.StockMax(u.Def.SmokeStock);
            }
            if (u.Def.ApsStock >= 0)
            {
                charges[u.Def.ApsStock] = u.StockMax(u.Def.ApsStock);
            }
        }
        if (d.RepairLeft > 0)
        {
            d.RepairLeft--;
            // Evenly from its HP when it came in: after k of the repair's ticks, from + (max - from) * k / repairTicks.
            var hp = d.RepairFrom + (long)(u.Def.MaxHp - d.RepairFrom) * (sv.RepairTicks - d.RepairLeft) / sv.RepairTicks;
            u.HP = (int)Math.Clamp(hp, u.HP, u.Def.MaxHp);
        }
        if (d.CargoLeft > 0 && --d.CargoLeft == 0 && u.Def.Cargo is { } cg)
        {
            u.Cargo = cg.Amount;
        }
        if (d.ReloadLeft == 0 && d.RepairLeft == 0 && d.CargoLeft == 0)
        {
            EndService(u, g, true); // done: out through its door to the rally point
        }
    }

    // EndService ends u's service at garage g (null: gone). Its door is let go: a vehicle driving in or in its bay comes
    // out through it (the door closes behind it once it has left the exit lane), to g's rally point when its service
    // ended by itself (done, or the garage stopped: toRally) or it has no other order; a door it had not reached yet is
    // free again (one that was opening closes). Others' orders end.
    private void EndService(Unit u, Unit? g, bool toRally)
    {
        var inside = u.ServiceStage is ServiceStage.DriveIn or ServiceStage.InBay;
        if (g is not null && u.ServiceDoor >= 0 && u.ServiceDoor < g.Doors.Length && g.Doors[u.ServiceDoor].Serviced == u.Id)
        {
            var d = g.Doors[u.ServiceDoor];
            ClearServiceDoor(d);
            if (inside && u.Alive)
            {
                (d.Vehicle, d.Wait) = (u.Id, 0); // it drives out: the door closes once it has left the lane
            }
        }
        if (u.InGarage)
        {
            u.Inside = 0; // back on the map, on its bay floor
        }
        var ordered = u.Order.Kind == OrderKind.Service && u.Order.Target == u.ServiceAt;
        (u.ServiceAt, u.ServiceStage, u.ServiceSince, u.ServiceDoor) = (0, ServiceStage.None, 0, -1);
        if (!u.Alive)
        {
            return;
        }
        if (inside && g is not null && (toRally || ordered || u.Order.Kind == OrderKind.Idle))
        {
            var rally = RallyCell(g);
            Assign(u, new Order(OrderKind.Move, rally, 0, 0));
            RequestRoute(u, rally); // its route starts at its door's exit (OutOf, OutVia)
        }
        else if (ordered)
        {
            u.Idle();
        }
    }

    // ClearServiceDoor lets a service door go: a shut one is free at once; an opening or open one closes by itself
    // (Door: an open door with nothing to let out comes down).
    private static void ClearServiceDoor(FactoryDoor d)
    {
        (d.Serviced, d.Needs, d.ReloadLeft, d.RepairLeft, d.CargoLeft, d.RepairFrom, d.Wait) = (0, ServiceNeeds.None, 0, 0, 0, 0, 0);
        if (d.State == DoorState.Closed && d.Def < 0 && d.Vehicle == 0)
        {
            d.Use = DoorUse.None;
        }
    }

    // KeepServiceDoors lets go the service doors of garage g whose vehicle is gone (destroyed, or no longer serviced
    // there through that door).
    private void KeepServiceDoors(Unit g)
    {
        for (var i = 0; i < g.Doors.Length; i++)
        {
            var d = g.Doors[i];
            if (d.Serviced != 0 && !(Unit(d.Serviced) is { Alive: true } v && v.ServiceAt == g.Id && v.ServiceDoor == i))
            {
                ClearServiceDoor(d);
            }
        }
    }

    // GiveDoors gives garage g's free doors to the vehicles waiting for it, in the order they got there (then by ID), each
    // the free door nearest to it.
    private void GiveDoors(Unit g)
    {
        _serviceWaiting.Sort(static (a, b) => a.ServiceAt != b.ServiceAt ? a.ServiceAt.CompareTo(b.ServiceAt)
            : a.ServiceSince != b.ServiceSince ? a.ServiceSince.CompareTo(b.ServiceSince) : a.Id.CompareTo(b.Id));
        foreach (var u in _serviceWaiting)
        {
            if (u.ServiceAt != g.Id || u.ServiceStage != ServiceStage.Waiting)
            {
                continue;
            }
            var i = NearestDoor(g, u.Pos, true);
            if (i < 0)
            {
                return; // every door is busy: the rest wait
            }
            var d = g.Doors[i];
            (d.Use, d.Serviced, d.Wait) = (DoorUse.Service, u.Id, 0);
            (u.ServiceStage, u.ServiceDoor) = (ServiceStage.ToDoor, i);
            RequestRoute(u, Cell.Of(ExitPoint(g, i)));
        }
    }

    /// <summary>What door i of factory f is used for and by what (see <see cref="DoorOccupant"/>).</summary>
    public DoorOccupant DoorOccupantOf(Unit f, int door)
    {
        if (door < 0 || door >= f.Doors.Length)
        {
            return new DoorOccupant(DoorUse.None, 0, -1, -1);
        }
        var d = f.Doors[door];
        if (d.Use == DoorUse.Service || d.Serviced != 0)
        {
            return new DoorOccupant(DoorUse.Service, d.Serviced != 0 ? d.Serviced : d.Vehicle, -1, -1);
        }
        var bay = Array.FindIndex(f.Bays, b => b.Def >= 0 && b.Door == door);
        if (bay >= 0)
        {
            return new DoorOccupant(DoorUse.Production, 0, f.Bays[bay].Def, bay);
        }
        if (d.Use == DoorUse.Production || d.Def >= 0 || d.Vehicle != 0)
        {
            return new DoorOccupant(DoorUse.Production, d.Vehicle, d.Def >= 0 ? d.Def : Unit(d.Vehicle)?.Def.Index ?? -1, -1);
        }
        return new DoorOccupant(DoorUse.None, 0, -1, -1);
    }

    /// <summary>
    /// The factory door unit u is using and why: a vehicle being serviced (from when it is given a door), or one driving out of
    /// its factory after production or service; (0, -1, None) otherwise. A vehicle waiting for a door: (its garage, -1, Service).
    /// </summary>
    public (uint Factory, int Door, DoorUse Use) DoorOf(Unit u)
    {
        if (u.ServiceAt != 0)
        {
            return (u.ServiceAt, u.ServiceDoor, DoorUse.Service);
        }
        if (u.OutOf != 0 && Unit(u.OutOf) is { } f)
        {
            for (var i = 0; i < f.Doors.Length; i++)
            {
                if (f.Doors[i].Vehicle == u.Id)
                {
                    return (f.Id, i, f.Doors[i].Use == DoorUse.Service ? DoorUse.Service : DoorUse.Production);
                }
            }
        }
        return (0, -1, DoorUse.None);
    }

    /// <summary>
    /// The service progress of door i of garage g while a vehicle is in its bay (for progress bars): ticks left and the
    /// total of each of the reload, repair and cargo refill (total 0: it didn't need that one); null otherwise.
    /// </summary>
    public ServiceProgress? ServiceProgressOf(Unit g, int door)
    {
        if (g.Def.Archetype.Service is not { } sv || door < 0 || door >= g.Doors.Length || Unit(g.Doors[door].Serviced) is not { InGarage: true } u)
        {
            return null;
        }
        var d = g.Doors[door];
        return new ServiceProgress(u.Id,
            d.ReloadLeft, (d.Needs & ServiceNeeds.Reload) != 0 ? sv.ReloadTicks : 0,
            d.RepairLeft, (d.Needs & ServiceNeeds.Repair) != 0 ? sv.RepairTicks : 0,
            d.CargoLeft, (d.Needs & ServiceNeeds.Cargo) != 0 ? sv.CargoTicks : 0);
    }

    /// <summary>The vehicles sent to garage g for service and not yet given a door, in the order they take doors (arrived first, then by ID; those still driving there last, by ID).</summary>
    public List<uint> ServiceQueue(Unit g)
    {
        var q = new List<Unit>();
        foreach (var u in _units)
        {
            if (u.ServiceAt == g.Id && u.ServiceStage is ServiceStage.Approach or ServiceStage.Waiting)
            {
                q.Add(u);
            }
        }
        q.Sort(static (a, b) => a.ServiceStage != b.ServiceStage ? b.ServiceStage.CompareTo(a.ServiceStage)
            : a.ServiceSince != b.ServiceSince ? a.ServiceSince.CompareTo(b.ServiceSince) : a.Id.CompareTo(b.Id));
        return [.. q.Select(u => u.Id)];
    }
}

/// <summary>
/// What a factory door is used for and by what (<see cref="World.DoorOccupantOf"/>).
/// </summary>
/// <param name="Use">Free, production or service.</param>
/// <param name="Unit">The vehicle using it: the one being serviced (on its way, driving in, in its bay) or driving out; 0 while a bay builds behind it.</param>
/// <param name="Def">Production: what is being built behind it or coming out (an index into the match's Defs); -1 otherwise.</param>
/// <param name="Bay">Production: the bay building behind it (a garage with shared doors), or -1.</param>
public readonly record struct DoorOccupant(DoorUse Use, uint Unit, int Def, int Bay);

/// <summary>
/// A garage bay's service progress (<see cref="World.ServiceProgressOf"/>): ticks left and the total of each part; a total
/// of 0 means the vehicle didn't need it.
/// </summary>
public readonly record struct ServiceProgress(uint Unit, int ReloadLeft, int ReloadTotal, int RepairLeft, int RepairTotal, int CargoLeft, int CargoTotal);
