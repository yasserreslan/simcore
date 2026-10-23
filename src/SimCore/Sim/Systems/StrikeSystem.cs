namespace SimCore.Sim;

// The strike system: a launched drone that reaches its target (or the cell
// it was sent to) explodes there and is gone.
public sealed partial class World
{
    private void RunStrikes()
    {
        foreach (var u in _units)
        {
            if (u.Def.Strike is not { } s || !u.Alive || !AirGoal(u, out var goal) || !Within(u.Pos, goal, s.Contact))
            {
                continue;
            }
            u.HP = 0;
            SetHit(true, null); // a drone striking: from above (directional armour)
            Explode(u.Pos, s.Damage, s.Warhead, s.Splash);
            ClearHit();
            RunwayBlast(u.Pos, s.Runway);
        }
    }
}
