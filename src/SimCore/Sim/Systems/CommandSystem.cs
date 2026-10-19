using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The command system: the commands scheduled for this tick run first, in
// (player, sequence) order.
public sealed partial class World
{
    private void RunCommands()
    {
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
        u is not null && u.Owner == player && u.CanAct && u.Def.Strike is null;

    // Apply runs one command. Units the player doesn't own or can't order,
    // dead units and invalid targets are skipped, the same way on every machine.
    private void Apply(Command c)
    {
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
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (!Commandable(u, c.Player) || u!.Def.Speed.Raw == 0 || u.Garrison != 0 || u.Def.Layer != Layers.Ground || u == ward)
                    {
                        continue;
                    }
                    u.ResetAi();
                    Assign(u, new Order(OrderKind.Guard, ward is not null ? Cell.Of(ward.Pos) : c.Cell, ward?.Id ?? 0, 0));
                }
                break;
            case CommandKind.Attack:
                var target = Unit(c.Target);
                if (target is null || target.Owner == c.Player || !IsVisibleTo(target, c.Player))
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (!Commandable(u, c.Player) || PickWeapon(u!, target, false) < 0)
                    {
                        continue;
                    }
                    Assign(u!, new Order(OrderKind.Attack, default, target.Id, 0));
                }
                break;
            case CommandKind.Stop:
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (Commandable(u, c.Player))
                    {
                        u!.Idle();
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
                    if (u is null || u.Owner != c.Player)
                    {
                        continue;
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
                        Unload(u);
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
            case CommandKind.Deploy or CommandKind.Stow:
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (!Commandable(u, c.Player) || u!.Def.Archetype.DeployTicks == 0)
                    {
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
                        u.Def.Cargo is not { } cg || u.Cargo < cg.Amount || !IsCargoDepot(depotTarget, u))
                    {
                        continue; // only full cargo trucks, onto their own side's depot
                    }
                    Assign(u, new Order(OrderKind.Unload, default, depotTarget.Id, 0));
                }
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
            case CommandKind.Produce:
                Produce(c);
                break;
            case CommandKind.CancelProduce:
                CancelProduce(c);
                break;
            case CommandKind.SetRally:
                SetRally(c);
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
                if (c.Stance is not (Stance.Guard or Stance.HoldFire or Stance.ReturnFire or Stance.HoldGround or Stance.Aggressive))
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is not null && u.Owner == c.Player && u.Def.Weapons.Count > 0)
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
                    if (gw < 0)
                    {
                        continue;
                    }
                    var wd = u!.Def.Weapons[gw];
                    if (wd.Effect.Drone is not null && u.Order.Kind == OrderKind.AttackGround && wd.Stock >= 0 && u.Charges[wd.Stock] >= 2 &&
                        (c.Queue || !u.HasNextGround))
                    {
                        // A drone launcher with its first point still pending: this is the second point.
                        (u.NextGround, u.HasNextGround) = (c.Cell, true);
                        continue;
                    }
                    (u.NextGround, u.HasNextGround) = (default, false);
                    Assign(u, new Order(OrderKind.AttackGround, c.Cell, 0, 0));
                }
                break;
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
        if (def?.Archetype.Garrison is not null)
        {
            // A building goes exactly where asked (top-left cell), neutral, if its footprint is free.
            if (FootprintFree(def, c.Cell))
            {
                Spawn(def, c.Cell, FixedPoint.Facing.South);
            }
            return;
        }
        if (def is null || def.Strike is not null || !Grid.TrySpiral(c.Cell, 0, out var cell))
        {
            return;
        }
        Spawn(def, cell, c.Player == 1 ? FixedPoint.Facing.East : FixedPoint.Facing.West);
    }

    // FootprintFree reports whether a structure fits with c as its top-left cell:
    // every cell is on the map and passable, and no ground unit stands in it.
    private bool FootprintFree(UnitDef def, Cell c)
    {
        int w = def.Archetype.FootprintW, h = def.Archetype.FootprintH;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!Grid.Passable(new Cell(c.X + x, c.Y + y)))
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
        u.ClearPath();
        u.RepathAt = Tick;
    }
}
