using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The command system: the commands scheduled for this tick run first, in
// (player, sequence) order.
public sealed partial class World
{
    private void RunCommands()
    {
        _orderRefusals.Clear(); // the tick's refusals start here (commands run first)
        var n = 0;
        while (n < _pending.Count && _pending[n].Tick == Tick)
        {
            n++;
        }
        for (var i = 0; i < n; i++)
        {
            Apply(_pending[i]);
        }
        _pending.RemoveRange(0, n);
    }

    // Commandable reports whether player may order u around: it is theirs,
    // it can act (a vehicle needs a driver; a driver must exit first), and it
    // is not a launched drone, which flies itself.
    private static bool Commandable(Unit? u, byte player) =>
        u is not null && u.Owner == player && u.CanAct && u.Def.Strike is null && u.Heli is not { Phase: HeliPhase.OutOfControl } && // out of control: no player orders
        u.Def.Archetype.Kaman is null; // a Kaman drone takes none once launched

    // Apply runs one command, then starts the quick turn of every jet in free flight it gave a new order
    // or task (E12, D-TURN1).
    private void Apply(Command c)
    {
        var quick = c.Kind is CommandKind.Move or CommandKind.Attack or CommandKind.AttackGround or CommandKind.AirTask
            or CommandKind.ReturnToBase or CommandKind.Rearm or CommandKind.AttackLine;
        var element = c.Kind is CommandKind.Move or CommandKind.Attack or CommandKind.AttackGround or CommandKind.AirTask;
        if (element)
        {
            ElementBefore(c); // E13: who this command gives a new order forms elements
        }
        if (quick)
        {
            _quickBefore.Clear();
            foreach (var id in c.Units)
            {
                var u = Unit(id);
                _quickBefore.Add(u is { Wing: not null, AirOps: null } ? (u.Order, u.AirMission, u.Homing) : default);
            }
        }
        ApplyOrder(c);
        if (element)
        {
            FormElements(c);
        }
        if (!quick)
        {
            return;
        }
        for (var i = 0; i < c.Units.Count && i < _quickBefore.Count; i++)
        {
            if (Unit(c.Units[i]) is { Wing: { } st, AirOps: null, Alive: true } u && _quickBefore[i] != (u.Order, u.AirMission, u.Homing))
            {
                st.QuickTurn = QuickTurnTicks;
            }
        }
    }

    // ---- Order refusals (E13, D-ORD1: an aircraft never drops a player's attack order silently) ----

    private readonly List<OrderRefusal> _orderRefusals = [];

    /// <summary>
    /// The attack orders aircraft refused or gave up in the last tick (E13, D-ORD1), in order: no ammunition, nothing
    /// that hurts the target, a refuel trip, homing on a Rearm or ReturnToBase order, a target lost, a strike drone's
    /// unseen target. The client's feedback hook (a voice line, a toast).
    /// </summary>
    public IReadOnlyList<OrderRefusal> OrderRefusals => _orderRefusals;

    // Refuse reports that aircraft u refused (or gave up) an attack order.
    private void Refuse(Unit u, OrderRefusalReason why, uint target, Cell cell) =>
        _orderRefusals.Add(new OrderRefusal(u.Id, u.Owner, why, target, cell));

    // AirOrderRefusal is why aircraft u cannot take an attack order now: homing to refuel (the automatic trip), or
    // holding or landing on a Rearm or ReturnToBase order; null when it can (parked, it leaves; climbing out, it goes on).
    private static OrderRefusalReason? AirOrderRefusal(Unit u)
    {
        var inbound = u.AirOps is { Phase: FlightPhase.Holding or FlightPhase.Final } || (u.Homing && u.AirOps is null);
        if (!inbound)
        {
            return null;
        }
        return u.HomeReason == HomeReason.Refuel ? OrderRefusalReason.RefuelTrip : OrderRefusalReason.Homing;
    }

    // A strike's allowance in FuelFor: one pass's worth of flying (20 s) on top of the trip there and home.
    private const int StrikeFuelTicks = 20 * TicksPerSecond;

    // FuelFor: aircraft u (homed on an airfield) has the fuel to fly to `at`, fly a pass and go home with its reserves
    // (E13, D-FUEL1: refused with Bingo otherwise); an aircraft without an airfield home is not refused for fuel.
    private bool FuelFor(Unit u, Vec at)
    {
        if (u.Def.Lifetime <= 0 || _airfields.Count == 0 || u.AirOps is { Phase: >= FlightPhase.Rollout and <= FlightPhase.Parked } ||
            u.Def.Archetype.Aircraft is not { Landing: LandingKind.Runway } ac ||
            u.Def.Archetype.Flight is not { } f || HomeAirfield(u, ac, false) is not { } field)
        {
            return true;
        }
        var there = (at - u.Pos).Len().Raw / Math.Max(1, u.Def.Speed.Raw);
        return u.Lifetime > there + StrikeFuelTicks + AirfieldHomeTicksFrom(u, field, ac, at) + f.ReserveTicks;
    }

    // DockedRearm (E13, D-ORD2): aircraft u is parked (or rolling out, taxiing in) at home without the weapons for t (null:
    // a point) and its full loadout has one: it is put on a Rearm visit here (the full service, then it leaves), true.
    private bool DockedRearm(Unit u, Unit? t)
    {
        if (u.AirOps is not { Phase: >= FlightPhase.Rollout and <= FlightPhase.Parked } ops)
        {
            return false;
        }
        var can = false;
        foreach (var w in u.Def.Weapons)
        {
            if (w.Stock >= 0 && u.StockMax(w.Stock) > 0 && (w.Effect.Targets & Layers.Ground) != 0 && w.Effect.Airdrop is null &&
                (w.Effect.Delivery is not null || w.Effect.AirGun is not null || w.Effect.SideFire is not null) && (t is null || CanHit(w, t)))
            {
                can = true;
                break;
            }
        }
        if (!can)
        {
            return false;
        }
        (u.HomeReason, ops.Hold, ops.Launch) = (HomeReason.Rearm, false, false); // the full service where it is, then it leaves
        return true;
    }

    // OutOf is why aircraft u has nothing to attack t with (null: a point): NoAmmo when none of the weapons for that kind
    // of target (air or ground) has ammunition, else NoWeaponForTarget (ammunition, but nothing that hurts it).
    private OrderRefusalReason OutOf(Unit u, Unit? t)
    {
        var air = t is not null && AirBodyOfUnit(t) is not null;
        var any = air ? EngageClasses(u) != 0 : AirGroundWeapon(u, null) is not null || (HasSideFire(u.Def) && SideAmmo(u));
        return any ? OrderRefusalReason.NoWeaponForTarget : OrderRefusalReason.NoAmmo;
    }

    // A fresh player order's quick turn lasts at most this long (2 s; D-TURN1).
    private const int QuickTurnTicks = 2 * TicksPerSecond;

    // Scratch: each commanded jet's order, task and homing before the command (the quick turn's test).
    private readonly List<(Order, AirMission?, bool)> _quickBefore = [];

    // ApplyOrder runs one command. Units the player doesn't own or can't order,
    // dead units and invalid targets are skipped, the same way on every machine.
    private void ApplyOrder(Command c)
    {
        if (_helis)
        {
            c = HeliCommand(c); // the helicopters' part of the command (HeliCommandSystem.cs); the rest goes on without them
        }
        if (_squads.Count > 0)
        {
            c = SquadCommand(c); // an order given to squad members goes to their whole squads (SquadSystem.cs)
        }
        switch (c.Kind)
        {
            case CommandKind.Move:
                var dest = 0;
                var ground = new List<Unit>();
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (!Commandable(u, c.Player) || u!.Def.Speed.Raw == 0 || u.Garrison != 0)
                    {
                        continue; // soldiers in a building exit first
                    }
                    if (u.Def.Layer != Layers.Air)
                    {
                        ground.Add(u);
                        continue;
                    }
                    // Each aircraft gets its own cell around the target.
                    var ok = Grid.TrySpiral(c.Cell, dest, out var cell);
                    dest++;
                    if (ok)
                    {
                        u.Order = new Order(OrderKind.Move, cell, 0, 0);
                        u.ClearPath();
                    }
                }
                ground = TakeSquads(ground, c.Cell, OrderKind.Move); // squads walk their own paths (SquadSystem.cs)
                if (ground.Count > 0)
                {
                    MoveGround(ground, c.Cell); // one route, or a group move in formation
                }
                break;
            case CommandKind.AttackMove:
                var movers = new List<Unit>();
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (Commandable(u, c.Player) && u!.Def.Speed.Raw > 0 && u.Garrison == 0 && u.Def.Layer == Layers.Ground)
                    {
                        movers.Add(u);
                    }
                }
                if (movers.Count > 0 && Grid.Contains(c.Cell))
                {
                    movers = TakeSquads(movers, c.Cell, OrderKind.AttackMove); // squads walk their own paths (SquadSystem.cs)
                    if (movers.Count == 0)
                    {
                        break;
                    }
                    MoveGround(movers, c.Cell);
                    foreach (var u in movers)
                    {
                        if (u.Order.Kind == OrderKind.Move)
                        {
                            u.ResetAi();
                            u.Order = u.Order with { Kind = OrderKind.AttackMove };
                        }
                    }
                }
                break;
            case CommandKind.Guard:
                var ward = c.Target != 0 ? Unit(c.Target) : null;
                if (c.Target != 0 && (ward is null || ward.Owner != c.Player || !ward.Alive))
                {
                    return; // only the player's own units and buildings (or a cell)
                }
                if (ward is null && !Grid.Contains(c.Cell))
                {
                    return;
                }
                var guards = new List<Unit>();
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (Commandable(u, c.Player) && u!.Def.Speed.Raw > 0 && u.Garrison == 0 && u.Def.Layer == Layers.Ground && u != ward)
                    {
                        guards.Add(u);
                    }
                }
                if (ward is not null)
                {
                    foreach (var u in guards)
                    {
                        u.ResetAi();
                        Assign(u, new Order(OrderKind.Guard, Cell.Of(ward.Pos), ward.Id, 0));
                    }
                    break;
                }
                if (guards.Count > 0)
                {
                    // A post each: the group moves there in formation, and each guards its own slot.
                    MoveGround(guards, c.Cell);
                    foreach (var u in guards)
                    {
                        if (u.Order.Kind == OrderKind.Move)
                        {
                            u.ResetAi();
                            u.Order = u.Order with { Kind = OrderKind.Guard };
                        }
                    }
                }
                break;
            case CommandKind.Scatter:
                Scatter(c);
                break;
            case CommandKind.Attack:
                var target = Unit(c.Target);
                if (target is null || target.Owner == c.Player || target.Def.Archetype.Untargetable)
                {
                    return; // never at an untargetable piece (an airfield's pavement, an open stand)
                }
                var seen = IsVisibleTo(target, c.Player);
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (!Commandable(u, c.Player))
                    {
                        continue;
                    }
                    var air = u!.Def.Archetype.Flight is not null;
                    if (!seen && !air)
                    {
                        continue; // a ground unit attacks only what its side sees
                    }
                    var rearming = air && PickWeapon(u, target, false) < 0 && !CanAttackInAir(u, target) && DockedRearm(u, target);
                    if (!rearming && PickWeapon(u, target, false) < 0 && !CanAttackInAir(u, target))
                    {
                        if (air)
                        {
                            Refuse(u, OutOf(u, target), target.Id, Cell.Of(target.Pos)); // E13, D-ORD1
                        }
                        continue; // an aircraft may be sent at an air target its air weapons engage (a Strike; E6)
                    }
                    var cell = Grid.Contains(c.Cell) && c.Cell != default ? c.Cell : Cell.Of(target.Pos);
                    if (air && AirOrderRefusal(u) is { } why)
                    {
                        Refuse(u, why, target.Id, cell); // homing: it lands first (E13, D-ORD1)
                        continue;
                    }
                    if (air && !seen && StrikeWeapon(u) is not null)
                    {
                        Refuse(u, OrderRefusalReason.NotVisible, target.Id, cell); // a strike drone's missile needs a target in sight
                        continue;
                    }
                    if (air && !CanAttackInAir(u, target) && !FuelFor(u, seen ? target.Pos : cell.Center()))
                    {
                        Refuse(u, OrderRefusalReason.Bingo, target.Id, cell); // E13, D-FUEL1: not enough fuel for the trip
                        continue;
                    }
                    if (air && !seen)
                    {
                        Refuse(u, OrderRefusalReason.Unseen, target.Id, cell); // a note: taken, to the last known cell
                    }
                    if (rearming)
                    {
                        Refuse(u, OrderRefusalReason.Rearming, target.Id, cell); // a note: taken, it rearms at home first (D-ORD2)
                    }
                    if (c.Queue && air && !CanAttackInAir(u, target) && QueueAim(u, new StrikeAim(target.Id, cell, default, false)))
                    {
                        continue; // H2: Shift: appended to its queue of aims
                    }
                    // E13 (D-ORD1): an aircraft sent at a target its side does not see flies to the clicked cell (or
                    // the target's last known one: a structure's ghost) and attacks once it is seen. A player's order
                    // takes over from Guard's own pick until it is done.
                    Assign(u, new Order(OrderKind.Attack, seen ? default : cell, target.Id, 0));
                    u.SeekTicks = 0;
                    if (u.AirMission is { } am)
                    {
                        am.Hunt = 0;
                    }
                }
                break;
            case CommandKind.Stop:
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (Commandable(u, c.Player))
                    {
                        u!.Idle();
                        StopMission(u); // MissionSystem.cs: an aircraft on a mission orbits where it is
                        if (u.AirOps is { Phase: >= FlightPhase.Rollout and <= FlightPhase.Parked } ops)
                        {
                            ops.Hold = true; // landing or parked: no automatic sortie until its next order
                            ops.Launch = false;
                        }
                        u.AimAt = 0; // Stop drops an aim in progress (a missile already guided keeps flying)
                        u.AimLeft = 0;
                        if (u.SalvoLeft > 0)
                        {
                            // Stop ends a salvo at once: no further rockets leave.
                            u.SalvoLeft = 0;
                            u.Reload = u.Def.Weapons[u.SalvoWeapon].Reload * 100;
                        }
                    }
                }
                break;
            case CommandKind.Enter:
                var v = Unit(c.Target);
                if (v?.Def.Archetype.Tunnel is not null)
                {
                    // Go down a tunnel entrance of the player's that is finished and standing.
                    foreach (var id in c.Units)
                    {
                        var u = Unit(id);
                        if (Commandable(u, c.Player) && CanUseTunnel(u!, v))
                        {
                            Assign(u!, new Order(OrderKind.Enter, default, v.Id, 0));
                        }
                    }
                    return;
                }
                if (v?.Def.Archetype.Garrison is not null)
                {
                    // Garrison a building: neutral or already held by this player.
                    if (v.Owner != 0 && v.Owner != c.Player)
                    {
                        return;
                    }
                    foreach (var id in c.Units)
                    {
                        var u = Unit(id);
                        if (Commandable(u, c.Player) && CanGarrison(u!, v))
                        {
                            Assign(u!, new Order(OrderKind.Enter, default, v.Id, 0));
                        }
                    }
                    return;
                }
                if (v?.Def.Archetype.Airlift is not null)
                {
                    // An airlifter's vehicle hold (AirliftSystem.cs): its side's airliftable vehicles drive up its ramp while it is
                    // parked at a main air base.
                    foreach (var id in c.Units)
                    {
                        var u = Unit(id);
                        if (Commandable(u, c.Player) && CanAirlift(u!, v))
                        {
                            Assign(u!, new Order(OrderKind.Enter, default, v.Id, 0));
                        }
                    }
                    return;
                }
                if (v?.Heli is not null && v.Owner == c.Player)
                {
                    // A helicopter's rider benches (the light helicopter): they walk to it; an idle one in the air lands for them.
                    foreach (var id in c.Units)
                    {
                        var u = Unit(id);
                        if (Commandable(u, c.Player) && CanRideHeli(u!, v))
                        {
                            Assign(u!, new Order(OrderKind.Enter, default, v.Id, 0));
                        }
                    }
                    return;
                }
                if (v is null || v.Owner != c.Player || v.Def.Crew is null)
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (Commandable(u, c.Player) && CanEnter(u!, v))
                    {
                        Assign(u!, new Order(OrderKind.Enter, default, v.Id, 0));
                    }
                }
                break;
            case CommandKind.Exit:
                var dismounted = 0; // passengers listed one by one get out on separate cells
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is null || u.Owner != c.Player || u.InGarage || Unit(u.Inside) is { InGarage: true })
                    {
                        continue; // (a vehicle being serviced in a garage bay, and its crew, stay in)
                    }
                    if (u.Def.Archetype.Airlift is not null)
                    {
                        AirliftExit(u); // an airlifter: parked, its vehicles drive off (AirliftSystem.cs)
                        continue;
                    }
                    if (InAirlifter(u) || Unit(u.Inside) is { } held && InAirlifter(held))
                    {
                        continue; // a vehicle aboard an airlifter, and its crew, stay in (they drive off where it lets them)
                    }
                    // A building: everyone out (at the door nearest the command's cell, when on the map).
                    // A soldier inside one: just him, at the door nearest that cell.
                    Vec? toward = Grid.Contains(c.Cell) ? c.Cell.Center() : null;
                    if (u.Def.Archetype.Garrison is not null)
                    {
                        Evacuate(u, toward);
                        continue;
                    }
                    if (Unit(u.Garrison) is { } b)
                    {
                        LeaveBuilding(u, b, NearestDoor(b, toward ?? b.Pos), 0);
                        continue;
                    }
                    // A passenger: just him, behind the vehicle. A carrier with passengers: all of
                    // them (the driver stays at the wheel); without passengers, its driver.
                    if (u.Inside != 0 && Unit(u.Inside) is { } carrier && carrier.Driver != u.Id)
                    {
                        Dismount(u, carrier, dismounted++);
                        continue;
                    }
                    if (u.Def.Crew is { Passengers: > 0 } && CountPassengers(u) > 0)
                    {
                        Unload(u); // all at once, or one every unloadInterval ticks (the Humvee's fast dismount)
                        continue;
                    }
                    if ((u.Inside != 0 ? Unit(u.Inside) : u) is { Driver: not 0 } vehicle)
                    {
                        Unboard(vehicle);
                    }
                }
                break;
            case CommandKind.UseAbility:
                UseAbility(c);
                break;
            case CommandKind.SandboxSpawn:
                SandboxSpawn(c);
                break;
            case CommandKind.SandboxSetHp:
                if (Match.Setup.Scenario == Scenarios.Scenario.SandboxName && Unit(c.Target) is { Alive: true } hu)
                {
                    hu.HP = Math.Clamp(c.Index, 1, hu.Def.MaxHp); // a scripted demo's beat (E13): never a kill by itself
                }
                break;
            case CommandKind.SandboxIgnite:
                if (Match.Setup.Scenario == Scenarios.Scenario.SandboxName)
                {
                    IgniteCell(c.Cell, Math.Clamp(c.Index, 0, 1000)); // a scripted demo's fire (FireSystem.cs)
                }
                break;
            case CommandKind.FormSquad:
                FormSquadCommand(c); // SquadSystem.cs
                break;
            case CommandKind.Detach:
                DetachCommand(c);
                break;
            case CommandKind.Disband:
                DisbandCommand(c);
                break;
            case CommandKind.SetSquadMode:
                SetSquadModeCommand(c);
                break;
            case CommandKind.SandboxUpgrade:
                if (Match.Setup.Scenario == Scenarios.Scenario.SandboxName && c.Unit is { } upgrade)
                {
                    GrantUpgrade(c.Player, upgrade); // a scripted demo's upgrade (UpgradeSystem.cs)
                }
                break;
            case CommandKind.Deploy or CommandKind.Stow:
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (!Commandable(u, c.Player) || u!.Def.Archetype.DeployTicks == 0)
                    {
                        continue;
                    }
                    if (c.Kind == CommandKind.Deploy && u.Deploy == DeployState.Stowed && !DeploySlopeOk(u.Def, Cell.Of(u.Pos)))
                    {
                        Refuse(u, OrderRefusalReason.Slope, 0, Cell.Of(u.Pos)); // steeper than its slope rule under it (CruiseSystem.cs)
                        continue;
                    }
                    if (c.Kind == CommandKind.Deploy && u.Deploy == DeployState.Stowed)
                    {
                        u.Idle();
                        if (CanDeployAt(u, Cell.Of(u.Pos)))
                        {
                            StartDeploy(u);
                        }
                        else if (DeploySpot(u, default, Fixed.Zero) is { } spot)
                        {
                            // Too steep here: drive to the nearest spot it can set up on, then deploy.
                            u.Order = new Order(OrderKind.Move, spot, 0, 0);
                            RequestRoute(u, spot);
                            u.DeployAfterMove = true;
                        }
                    }
                    else if (c.Kind == CommandKind.Stow && u.Deploy is DeployState.Deployed or DeployState.Deploying)
                    {
                        u.Idle();
                        StartStow(u);
                    }
                }
                break;
            case CommandKind.Unload:
                var depotTarget = Unit(c.Target);
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (depotTarget is null || u is null || u.Owner != c.Player || u.Inside != 0 || u.Def.Speed.Raw == 0 ||
                        u.Def.Cargo is null || u.Cargo <= 0 || !(IsCargoDepot(depotTarget, u) || CanFeed(u, depotTarget)))
                    {
                        continue; // only cargo trucks with supply aboard, onto their own side's depot or a gun they can reload (the howitzer)
                    }
                    Assign(u, new Order(OrderKind.Unload, default, depotTarget.Id, 0));
                }
                break;
            case CommandKind.Smoke:
                PopSmoke(c);
                break;
            case CommandKind.LoadCargo:
                var storeTarget = Unit(c.Target);
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (storeTarget is null || u is null || u.Owner != c.Player || u.Inside != 0 || u.Def.Speed.Raw == 0 ||
                        u.Def.Cargo is not { } cg || u.Cargo >= cg.Amount || !IsCargoSource(storeTarget, u))
                    {
                        continue; // only empty cargo trucks, at their own side's store (logistics)
                    }
                    Assign(u, new Order(OrderKind.LoadCargo, default, storeTarget.Id, 0));
                }
                break;
            case CommandKind.Airdrop:
                AirdropCommand(c); // logistics (AirdropSystem.cs)
                break;
            case CommandKind.Deliver:
                DeliverCommand(c); // logistics (AirdropSystem.cs)
                break;
            case CommandKind.Build:
                Build(c);
                break;
            case CommandKind.Resume:
                Resume(c);
                break;
            case CommandKind.CancelBuild:
                CancelBuild(c);
                break;
            case CommandKind.Repair:
                RepairCommand(c); // AirfieldDamageSystem.cs
                break;
            case CommandKind.ReturnToBase:
                ReturnToBaseCommand(c); // AirfieldServiceSystem.cs
                break;
            case CommandKind.Rearm:
                RearmCommand(c); // AirfieldServiceSystem.cs
                break;
            case CommandKind.AirTask:
                AirTaskCommand(c); // MissionSystem.cs
                break;
            case CommandKind.Produce:
                Produce(c);
                break;
            case CommandKind.Service:
                ServiceCommand(c); // ServiceSystem.cs
                break;
            case CommandKind.CancelProduce:
                CancelProduce(c);
                break;
            case CommandKind.SetRally:
                SetRally(c);
                break;
            case CommandKind.SetFireMode:
                SetFireMode(c); // H2 (AttackPlanSystem.cs)
                break;
            case CommandKind.SetBombWeapon:
                SetBombWeapon(c); // the stealth bomber (AttackPlanSystem.cs)
                break;
            case CommandKind.SetLoadout:
                SetLoadout(c); // the stealth multirole's loadout modes (LoadoutSystem.cs)
                break;
            case CommandKind.AttackLine:
                AttackLine(c); // H2 (AttackPlanSystem.cs)
                break;
            case CommandKind.SetWeaponMode:
                if (c.Mode is not (WeaponMode.Auto or WeaponMode.PrimaryOnly))
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is not null && u.Owner == c.Player && u.Def.Weapons.Count > 0)
                    {
                        u.WeaponMode = c.Mode;
                    }
                }
                break;
            case CommandKind.Detonate or CommandKind.SetAutoDetonate:
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is null || u.Owner != c.Player || !u.Alive || u.Def.Trigger is null)
                    {
                        continue; // only its owner's traps
                    }
                    if (c.Kind == CommandKind.SetAutoDetonate)
                    {
                        u.AutoOff = !c.On;
                    }
                    else if (u.Fuse == 0)
                    {
                        u.Fuse = 1; // goes off in this tick's trigger pass
                    }
                }
                break;
            case CommandKind.TunnelGo:
                if (Unit(c.Target) is not { } tdest || tdest.Owner != c.Player || tdest.Def.Archetype.Tunnel is null || !tdest.IsBuilt || !tdest.Alive ||
                    (c.On && tdest.Rubble))
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is null || u.Owner != c.Player || !u.InTunnel || !u.Alive ||
                        (c.On && !TunnelExitable(tdest, u.Def.Archetype.TunnelSize)) ||
                        Unit(u.TunnelNext != 0 ? u.TunnelNext : u.Tunnel) is not { } at ||
                        Route(at, tdest, u.Def.Archetype.TunnelSize).Next == 0)
                    {
                        continue;
                    }
                    SendUnderground(u, tdest, c.On);
                }
                break;
            case CommandKind.TunnelEvacuate:
                foreach (var id in c.Units)
                {
                    if (Unit(id) is not { } e || e.Owner != c.Player || e.Def.Archetype.Tunnel is null || !e.IsBuilt || !e.Alive)
                    {
                        continue;
                    }
                    foreach (var u in TunnelOccupants(e))
                    {
                        if (NearestExit(u) is { } exit)
                        {
                            SendUnderground(u, exit, true);
                        }
                    }
                }
                break;
            case CommandKind.SetStance:
                if (c.Stance is not (Stance.Guard or Stance.HoldFire or Stance.ReturnFire or Stance.HoldGround or Stance.Aggressive or Stance.Hide))
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    // An emitter (the S-400 type radar) takes a stance with no weapon: Hide switches its radar off.
                    if (u is not null && u.Owner == c.Player && (u.Def.Weapons.Count > 0 || u.Def.Archetype.Emitter is not null))
                    {
                        u.Stance = c.Stance;
                    }
                }
                break;
            case CommandKind.AttackGround:
                if (!Grid.Contains(c.Cell))
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    var gw = Commandable(u, c.Player) ? GroundWeapon(u!, false) : -1;
                    if (gw < 0 && Commandable(u, c.Player) && u!.Def.Archetype.Flight is not null && DockedRearm(u, null))
                    {
                        // E13 (D-ORD2): parked at home without the weapons it needs: an implicit Rearm, then the attack.
                        Refuse(u, OrderRefusalReason.Rearming, 0, c.Cell);
                        Assign(u, new Order(OrderKind.AttackGround, c.Cell, 0, 0));
                        continue;
                    }
                    if (gw < 0)
                    {
                        if (Commandable(u, c.Player) && u!.Def.Archetype.Flight is not null && u.Def.Weapons.Count > 0)
                        {
                            Refuse(u, OutOf(u, null), 0, c.Cell); // E13, D-ORD1
                        }
                        continue;
                    }
                    if (u!.Def.Archetype.Flight is not null && AirOrderRefusal(u) is { } gr)
                    {
                        Refuse(u, gr, 0, c.Cell); // E13, D-ORD1: homing, it lands first
                        continue;
                    }
                    if (u.Def.Archetype.Flight is not null && !FuelFor(u, c.Cell.Center()))
                    {
                        Refuse(u, OrderRefusalReason.Bingo, 0, c.Cell); // E13, D-FUEL1: not enough fuel for the trip
                        continue;
                    }
                    if (c.Queue && u.Def.Archetype.Flight is not null && QueueAim(u, new StrikeAim(0, c.Cell, default, false)))
                    {
                        continue; // H2: Shift: appended to its queue of aims
                    }
                    var wd = u!.Def.Weapons[gw];
                    if (wd.Effect.Cruise is not null)
                    {
                        OrderCruise(u, c); // a cruise truck: its route checked and queued (CruiseSystem.cs)
                        continue;
                    }
                    if (wd.Effect.Drone is not null && u.Order.Kind == OrderKind.AttackGround && wd.Stock >= 0 && u.Charges[wd.Stock] >= 2 &&
                        (c.Queue || !u.HasNextGround))
                    {
                        // A drone launcher with its first point still pending: this is the second point.
                        (u.NextGround, u.HasNextGround) = (c.Cell, true);
                        continue;
                    }
                    if (u.Def.Speed.Raw == 0 && ShotAt(u, c.Cell.Center()) != GroundShot.InRange)
                    {
                        continue; // a unit that can't move (the howitzer) takes only points in its range, outside its minimum
                    }
                    if (c.Queue && u.Order.Kind == OrderKind.AttackGround && u.Def.Archetype.Flight is null && wd.Effect.Drone is null &&
                        wd.Effect.Launches < 0)
                    {
                        if (u.GroundQueue.Count < MaxGroundQueue)
                        {
                            u.MutableGroundQueue.Add(c.Cell); // Shift: one more point, fired at in turn
                        }
                        continue;
                    }
                    (u.NextGround, u.HasNextGround) = (default, false);
                    Assign(u, new Order(OrderKind.AttackGround, c.Cell, 0, 0));
                }
                break;
        }
    }

    private const int MaxGroundQueue = 16; // Shift-queued attack-ground points a ground unit holds after its current one

    private static readonly Fixed ScatterMin = Fixed.FromInt(2);

    // Scatter moves each listed mobile ground unit straight away from the group's centre (the
    // integer mean of their positions) by max(4 x its radius, 2 cells), farthest from the centre
    // first (then by ID); a unit exactly on the centre goes the way it faces. Each gets a move of its
    // own (no formation), to the nearest free cell of its spot.
    private void Scatter(Command c)
    {
        var group = new List<Unit>();
        long sx = 0, sy = 0;
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || u!.Def.Speed.Raw == 0 || u.Garrison != 0 || u.Def.Layer != Layers.Ground ||
                u.Deploy != DeployState.Stowed || group.Contains(u))
            {
                continue; // set-up launchers pack up first
            }
            group.Add(u);
            (sx, sy) = (sx + u.Pos.X.Raw, sy + u.Pos.Y.Raw);
        }
        if (group.Count == 0)
        {
            return;
        }
        var centre = new Vec(Fixed.FromRaw(sx / group.Count), Fixed.FromRaw(sy / group.Count));
        group.Sort((a, b) =>
        {
            var d = (b.Pos - centre).LenSq().CompareTo((a.Pos - centre).LenSq());
            return d != 0 ? d : a.Id.CompareTo(b.Id);
        });
        foreach (var u in group)
        {
            var away = u.Pos - centre;
            var dir = away.LenSq() == 0 ? u.Heading : away;
            var four = Fixed.FromRaw(u.Def.Archetype.Radius.Raw * 4);
            var to = u.Pos + dir.WithLen(four.Raw > ScatterMin.Raw ? four : ScatterMin);
            var cell = new Cell(Math.Clamp(Cell.Of(to).X, 0, Grid.Width - 1), Math.Clamp(Cell.Of(to).Y, 0, Grid.Height - 1));
            u.ResetAi();
            MoveGround([u], cell);
        }
    }

    // SandboxSpawn adds one unit for the issuing player in a sandbox match:
    // at the command's cell, or the nearest passable cell to it. Player 1's
    // units face east and everyone else's west, as in the battle scenario.
    // Drones are only launched from a kit: they need a target to fly at.
    private void SandboxSpawn(Command c)
    {
        if (Match.Setup.Scenario != Scenarios.Scenario.SandboxName || !Grid.Contains(c.Cell))
        {
            return;
        }
        var def = Match.Players[c.Player - 1].Find(Content, c.Unit ?? "");
        if (def?.Archetype.Airfield is { } piece)
        {
            // A facility founds a finished airfield (no rows yet) exactly where asked, facing the
            // command's facing, if it could be built there; other pieces are built from it. A main air
            // base's core (H1) lays the whole base finished (its built-in pieces; no extra rows).
            if (piece.Role is Model.AirfieldRole.Facility or Model.AirfieldRole.Core && c.Facing % (FixedPoint.Facing.Count / 4) == 0)
            {
                SpawnAirfield(def, c.Cell, c.Facing, []);
            }
            return;
        }
        if (def?.Archetype.Tunnel is not null)
        {
            // A tunnel entrance goes finished exactly where asked (top-left cell, the command's facing),
            // its ground shaped, if it could be built there.
            if (CanPlaceTunnel(def, c.Cell, c.Facing, []))
            {
                PlaceTunnel(def, c.Cell, c.Facing, true);
            }
            return;
        }
        if (def?.Archetype.Bridge is not null)
        {
            // A bridge goes exactly where asked (top-left cell, the command's facing: north or south for a road running
            // north-south, east or west for east-west), neutral, if it can stand there (BridgeSystem.cs).
            if (BridgeRefusal(def.Archetype, c.Cell, c.Facing) is null)
            {
                SpawnBridge(def, c.Cell, c.Facing);
            }
            return;
        }
        if (def?.Archetype is { Garrison: not null } or { Neutral: true })
        {
            // A building (a civilian structure too) goes exactly where asked (the top-left cell of its footprint as it
            // stands), neutral, if its footprint is free, its layout turned by the command's facing as a Build's: 0 as laid
            // out (the front to the south), 8 a quarter clockwise (front west), 16 a half turn (front north), 24 three
            // quarters (front east); any other facing places nothing. Its unit facing stays South, as before.
            var quarter = QuarterOf(c.Facing);
            if (quarter >= 0 && FootprintFree(def, c.Cell, quarter))
            {
                Spawn(def, c.Cell, FixedPoint.Facing.South, quarter);
            }
            return;
        }
        if (def is null || def.Strike is not null || !Grid.TrySpiral(c.Cell, 0, out var cell))
        {
            return;
        }
        if (def.IsStructure && (FootprintWet(def, cell) || FootprintOnAlley(cell, def.Archetype.FootprintW, def.Archetype.FootprintH)))
        {
            return; // no structure stands in the water (WorldWater.cs) or on an alley (WorldRoads.cs)
        }
        Spawn(def, cell, c.Player == 1 ? FixedPoint.Facing.East : FixedPoint.Facing.West);
    }

    // FootprintWet reports whether a structure with c as its top-left cell would stand on open water.
    private bool FootprintWet(UnitDef def, Cell c)
    {
        for (var y = 0; y < def.Archetype.FootprintH; y++)
        {
            for (var x = 0; x < def.Archetype.FootprintW; x++)
            {
                if (InWater(new Cell(c.X + x, c.Y + y)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // FootprintFree reports whether a structure fits with c as its top-left cell, its layout turned quarter (an odd
    // quarter swaps a non-square footprint's width and height): every cell is on the map and passable and no alley, no
    // ground unit stands in it and no bridge is there.
    private bool FootprintFree(UnitDef def, Cell c, int quarter)
    {
        var (w, h) = Footprint(def.Archetype, quarter * (FixedPoint.Facing.Count / 4));
        foreach (var b in _bridgeList)
        {
            var o = FootprintOrigin(b);
            if (o.X < c.X + w && c.X < o.X + FootW(b) && o.Y < c.Y + h && c.Y < o.Y + FootH(b))
            {
                return false; // (a bridge's cells are passable: they block no unit, but no building stands on one)
            }
        }
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!Grid.Passable(new Cell(c.X + x, c.Y + y)) || IsAlley(new Cell(c.X + x, c.Y + y)))
                {
                    return false;
                }
            }
        }
        foreach (var u in _units)
        {
            var at = Cell.Of(u.Pos);
            if (u.Def.Layer == Layers.Ground && u.Inside == 0 && at.X >= c.X && at.X < c.X + w && at.Y >= c.Y && at.Y < c.Y + h)
            {
                return false;
            }
        }
        return true;
    }

    private void UseAbility(Command c)
    {
        var comp = Content.Component(c.Ability ?? "");
        if (comp is null)
        {
            return;
        }
        if (c.Target != 0)
        {
            var t = Unit(c.Target);
            if (t is null || t.Owner == c.Player || !IsVisibleTo(t, c.Player))
            {
                return;
            }
        }
        else if (!Grid.Passable(c.Cell))
        {
            return;
        }
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || u!.Def.Ability(comp.Index) is not { } ab ||
                u.Charges[ab.Stock] == 0 || (ab.CellOnly && c.Target != 0))
            {
                continue;
            }
            Assign(u, new Order(OrderKind.UseAbility, c.Target == 0 ? c.Cell : default, c.Target, comp.Index));
        }
    }

    // Assign gives u an order, which the order system plans toward this tick.
    private void Assign(Unit u, Order o)
    {
        u.Order = o;
        u.MutableAims.Clear(); // H2: a new order replaces an aircraft's queue of aims (a Shift order appends instead)
        u.MutableGroundQueue.Clear(); // and a ground unit's Shift queue of attack-ground points
        u.MutableCruiseRoutes.Clear(); // and a cruise truck's routes
        u.AirdropAt = 0; // logistics: an Airdrop order sets it again after this
        u.ClearPath();
        u.RepathAt = Tick;
    }
}
