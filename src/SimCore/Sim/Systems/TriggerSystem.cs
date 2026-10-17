using SimCore.Model;

namespace SimCore.Sim;

// The trigger system: a trap fires once, when an enemy ground unit it
// affects comes within its radius. Flying units never set it off. The first
// such unit in ID order sets it off; the blast hits everyone in its splash.
public sealed partial class World
{
    private void RunTriggers()
    {
        foreach (var trap in _units)
        {
            if (trap.Def.Trigger is not { } t || !trap.Alive)
            {
                continue;
            }
            foreach (var o in _units)
            {
                if (!o.Alive || o.Inside != 0 || o.Garrison != 0 || o.Owner == trap.Owner || o.Def.Layer != Layers.Ground ||
                    !o.Def.Tags.Matches(t.Affects) || !Within(trap.Pos, o.Pos, t.Radius))
                {
                    continue;
                }
                trap.HP = 0;
                Explode(trap.Pos, t.Damage, t.Warhead, t.Splash);
                break;
            }
        }
        DropIndex(); // the index built for combat ends here
    }
}
