namespace SimCore.Sim;

// The match result system: a player is defeated once they own no unit with
// one of their faction's defeatedWithout tags (in Phase 1, no soldiers, on
// foot or driving). The match is over when at most one player is left.
public sealed partial class World
{
    private void RunMatchResult()
    {
        if (Result.Over)
        {
            return;
        }
        var standing = 0;
        byte last = 0;
        foreach (var p in _players)
        {
            if (!p.Defeated)
            {
                var tags = p.Setup.Faction.DefeatedWithout.Bits;
                p.Defeated = !_units.Exists(u => u.Owner == p.Id && (u.Def.Tags.Bits & tags) != 0);
            }
            if (!p.Defeated)
            {
                standing++;
                last = p.Id;
            }
        }
        if (standing == 0 || (standing == 1 && _players.Count > 1))
        {
            Result = new MatchResult(true, standing == 1 ? last : (byte)0, Tick + 1);
        }
    }
}
