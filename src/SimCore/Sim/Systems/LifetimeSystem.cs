namespace SimCore.Sim;

// The lifetime system: units with a limited lifetime (a drone's battery)
// count it down and drop out of the world when it runs out.
public sealed partial class World
{
    private void RunLifetimes()
    {
        foreach (var u in _units)
        {
            if (u.Lifetime > 0 && --u.Lifetime == 0)
            {
                u.HP = 0;
            }
        }
    }
}
