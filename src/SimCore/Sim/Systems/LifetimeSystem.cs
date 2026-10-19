namespace SimCore.Sim;

// The lifetime system: units with a limited lifetime (a drone's battery)
// count it down and drop out of the world when it runs out.
public sealed partial class World
{
    private void RunLifetimes()
    {
        foreach (var u in _units)
        {
            if (u.Lifetime > 0 && u.AirOps is not { Grounded: true }) // fuel does not burn on the ground
            {
                // A fixed wing in free flight burns by its throttle (E4: whole units of what its
                // flight burnt); everything else one a tick.
                var burn = WingFuel(u);
                if (burn > 0 && (u.Lifetime = Math.Max(0, u.Lifetime - burn)) == 0)
                {
                    u.HP = 0;
                    if (u.Def.Archetype.Flight is not null)
                    {
                        u.KilledBy = KillCause.Fuel; // ran dry in the air (E6)
                    }
                }
            }
        }
    }
}
