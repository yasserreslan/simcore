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

    // Apply runs one command. Units the player doesn't own, dead units and
    // invalid targets are skipped, the same way on every machine.
    private void Apply(Command c)
    {
        switch (c.Kind)
        {
            case CommandKind.Move:
                var dest = 0;
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is null || u.Owner != c.Player || u.Def.Speed.Raw == 0)
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
                    u.Order = new Order(OrderKind.Move, cell, 0);
                    SetPath(u, cell);
                }
                break;
            case CommandKind.Attack:
                var target = Unit(c.Target);
                if (target is null || target.Owner == c.Player)
                {
                    return;
                }
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is null || u.Owner != c.Player || u.Def.Weapon is null)
                    {
                        continue;
                    }
                    u.Order = new Order(OrderKind.Attack, default, target.Id);
                    u.ClearPath();
                    u.RepathAt = Tick;
                }
                break;
            case CommandKind.Stop:
                foreach (var id in c.Units)
                {
                    var u = Unit(id);
                    if (u is not null && u.Owner == c.Player)
                    {
                        u.Order = default;
                        u.ClearPath();
                    }
                }
                break;
        }
    }
}
