namespace SimCore.Sim;

// The repair system: units with a repair kit restore hit points to friendly
// units it affects (vehicles) in a short radius, every few ticks, never
// above max HP.
public sealed partial class World
{
    private void RunRepair()
    {
        foreach (var r in _units)
        {
            if (r.Def.Repairs.Count == 0 || !r.Alive || !r.CanAct)
            {
                continue;
            }
            foreach (var e in r.Def.Repairs)
            {
                if (Tick % (uint)e.EveryTicks != 0)
                {
                    continue;
                }
                foreach (var u in _units)
                {
                    if (u != r && u.Owner == r.Owner && u.Alive && u.Inside == 0 && u.HP < u.Def.MaxHp &&
                        u.Def.Tags.Matches(e.Affects) && Within(r.Pos, u.Pos, e.Radius))
                    {
                        u.HP = Math.Min(u.Def.MaxHp, u.HP + e.Rate);
                    }
                }
            }
        }
    }
}
