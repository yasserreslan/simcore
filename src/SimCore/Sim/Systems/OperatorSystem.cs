using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The operator system (INF-EE2, owner decision 32: "the engineer cant be used while the drone is being controlled ... the fpv
// can be controlled freely bascially but not simoultanelously with the eng"): a soldier who launches an FPV drone, by order or
// automatic, flies it until it is gone. He refuses orders (OrderRefusalReason.Operating) and doesn't move, fire or repair
// (Unit.CanAct); his side orders the drone directly: Move (it circles the point and watches), Attack (an enemy it sees) and
// ReturnToBase (Release: it crashes, it has no way home). If he dies the link is lost and it crashes at once. The Kaman drone
// is not flown this way: its station flies it and the engineer stays free.
public sealed partial class World
{
    // TakeControl: soldier u, who has just launched drone n, flies it when it is an FPV drone (a strike drone with the surveil
    // block); a station's launch (the Kaman's, a scout's) is never flown by a soldier.
    private static void TakeControl(Unit u, Unit n)
    {
        if (n.Def.Archetype.Surveil is null || n.Def.Strike is null || u.Def.IsStructure || u.Def.Layer != Layers.Ground)
        {
            return;
        }
        (u.Fpv, n.Operator) = (n.Id, u.Id);
    }

    // OperatorReady: soldier u, at his FPV launch (in reach), stops first; true when he launches now. Today he just stops where
    // he is and launches at once. INF-E1b's hook: hard cover within 2 cells (brief 6.4): false while he moves there, then while
    // he crouches; then he launches.
    private static bool OperatorReady(Unit u)
    {
        u.ClearPath();
        return true;
    }

    // OperatorCommand takes out of command c the soldiers flying an FPV drone and the drones they fly: a soldier refuses it
    // (Operating; ReturnToBase releases his drone instead), a drone takes Move, Attack and ReturnToBase from its side and ignores
    // the rest. The sandbox's own commands go on untouched.
    private Command OperatorCommand(Command c)
    {
        if (c.Kind is CommandKind.SandboxSpawn or CommandKind.SandboxSetHp or CommandKind.SandboxIgnite or CommandKind.SandboxUpgrade)
        {
            return c;
        }
        List<uint>? rest = null;
        for (var i = 0; i < c.Units.Count; i++)
        {
            var u = Unit(c.Units[i]);
            var flies = u is { Operating: true, Alive: true } && u.Owner == c.Player;
            var flown = u is { Operator: not 0, Alive: true } && u.Owner == c.Player;
            if (!flies && !flown)
            {
                rest?.Add(c.Units[i]);
                continue;
            }
            if (rest is null)
            {
                rest = new List<uint>(c.Units.Count);
                for (var k = 0; k < i; k++)
                {
                    rest.Add(c.Units[k]);
                }
            }
            if (flown)
            {
                FpvOrder(u!, c);
            }
            else if (c.Kind == CommandKind.ReturnToBase)
            {
                Release(Unit(u!.Fpv)); // Release, given to him: his drone crashes and he is free
            }
            else
            {
                Refuse(u!, OrderRefusalReason.Operating, u!.Fpv, c.Cell);
            }
        }
        return rest is null ? c : c with { Units = rest };
    }

    // FpvOrder: the side's direct order to FPV drone f, which a soldier flies.
    private void FpvOrder(Unit f, Command c)
    {
        switch (c.Kind)
        {
            case CommandKind.Move when Grid.Contains(c.Cell):
                FpvMove(f, c.Cell.Center());
                break;
            case CommandKind.Attack when Unit(c.Target) is { } t && t.Owner != c.Player && !t.Def.Archetype.Untargetable:
                if (!FpvAttack(f, t, IsVisibleTo(t, c.Player)))
                {
                    Refuse(f, OrderRefusalReason.NotVisible, t.Id, Cell.Of(t.Pos)); // not an enemy it sees
                }
                break;
            case CommandKind.ReturnToBase:
                Release(f);
                break;
        }
    }

    // FpvMove sends FPV drone f to circle the point and watch it (surveillance: its surveil vision), from attack too (it breaks
    // off its dive); a surveilling drone's circle moves there. Its battery stays as it is.
    private static void FpvMove(Unit f, Vec at)
    {
        if (f.Def.Archetype.Surveil is not { } sv)
        {
            return;
        }
        if (!f.Surveil)
        {
            f.Surveil = true;
            f.VisionBonus = sv.Vision - f.Def.Vision; // the surveil reach (SightRange)
        }
        f.Order = default; // no strike order: it circles (FlySurveil), the strike system never sets it off
        f.OrbitAt = at;
    }

    // Release ends FPV drone f's flight: it crashes where it is (no blast), as at the end of its battery.
    private static void Release(Unit? f)
    {
        if (f is not { Alive: true })
        {
            return;
        }
        f.HP = 0;
        f.KilledBy = KillCause.Crash;
    }

    // RunOperators, after the tick's damage: a soldier whose drone is gone (it hit, crashed, was shot down or released) is free
    // again; a drone whose operator is dead loses its link and crashes at once. INF-E1b: down as well (wounds), and suppression
    // 90 and over once it exists.
    private void RunOperators()
    {
        foreach (var u in _units)
        {
            if (u.Fpv != 0 && Unit(u.Fpv) is not { Alive: true })
            {
                u.Fpv = 0;
            }
            if (u.Operator != 0 && u.Alive && Unit(u.Operator) is not { Alive: true })
            {
                Release(u); // the link lost
            }
        }
    }
}
