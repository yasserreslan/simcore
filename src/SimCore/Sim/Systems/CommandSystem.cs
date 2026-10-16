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
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (!Commandable(u, c.Player) || u!.Def.Speed.Raw == 0)
                    {
                        continue;
                    }
                    // Each unit in the group gets its own cell around the target.
                    var ok = Grid.TrySpiral(c.Cell, dest, out var cell);
                    dest++;
                    if (!ok)
                    {
                        continue;
                    }
                    u.Order = new Order(OrderKind.Move, cell, 0, 0);
                    if (u.Def.Layer == Layers.Air)
                    {
                        u.ClearPath();
                    }
                    else
                    {
                        SetPath(u, cell);
                    }
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
                    }
                }
                break;
            case CommandKind.Enter:
                var v = Unit(c.Target);
                if (v is null || v.Owner != c.Player || v.Def.Crew is null)
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (Commandable(u, c.Player) && CanBoard(u!, v))
                    {
                        Assign(u!, new Order(OrderKind.Enter, default, v.Id, 0));
                    }
                }
                break;
            case CommandKind.Exit:
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is null || u.Owner != c.Player)
                    {
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
            case CommandKind.AttackGround:
                if (!Grid.Contains(c.Cell))
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (!Commandable(u, c.Player) || GroundWeapon(u!, false) < 0)
                    {
                        continue;
                    }
                    Assign(u!, new Order(OrderKind.AttackGround, c.Cell, 0, 0));
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
        if (def is null || def.Strike is not null || !Grid.TrySpiral(c.Cell, 0, out var cell))
        {
            return;
        }
        Spawn(def, cell, c.Player == 1 ? FixedPoint.Facing.East : FixedPoint.Facing.West);
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
