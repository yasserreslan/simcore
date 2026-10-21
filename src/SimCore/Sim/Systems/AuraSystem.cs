using SimCore.Model;

namespace SimCore.Sim;

// The aura system: healing auras (an aura effect with heal; the Sentinel's assault medic, docs/design.md, "Assault
// medic"). A source is alive, finished and on the map (not inside a vehicle, a building, a tunnel or a garage bay). Each
// unit it affects (its relation, one of its tags, within its radius of the source, alive, on the map, hurt) takes the
// aura's heal, never above max HP, then none again for the aura's interval. No stacking: a unit takes at most one aura
// heal at a time however many sources reach it: the strongest (most HP per tick, then the larger heal, then the lowest
// source ID, then component order). A source never heals itself. It runs after repair; units in ID order.
public sealed partial class World
{
    private readonly List<Unit> _auraSources = []; // scratch for one tick: not game state

    private void RunAuras()
    {
        var sources = _auraSources;
        sources.Clear();
        foreach (var s in _units)
        {
            if (s.Def.Auras.Count != 0 && s.Alive && s.IsBuilt && s.Inside == 0)
            {
                sources.Add(s);
            }
        }
        if (sources.Count == 0)
        {
            return; // no healers: nothing to do, and nothing hashed
        }
        foreach (var u in _units)
        {
            if (!u.Alive || u.Inside != 0 || u.HP >= u.Def.MaxHp || Tick < u.HealReady)
            {
                continue;
            }
            AuraEffect? best = null;
            foreach (var s in sources)
            {
                if (s == u)
                {
                    continue;
                }
                foreach (var a in s.Def.Auras)
                {
                    if (Reaches(a, s, u) && (best is null || Stronger(a, best)))
                    {
                        best = a;
                    }
                }
            }
            if (best is not null)
            {
                u.HP = Math.Min(u.Def.MaxHp, u.HP + best.Heal);
                u.HealReady = Tick + (uint)best.EveryTicks;
            }
        }
        sources.Clear();
    }

    // Reaches: aura a of source s affects u: u is its side's (ally) or another player's (enemy, never neutral), has one
    // of its tags (any unit with none) and stands within its radius of s.
    private static bool Reaches(AuraEffect a, Unit s, Unit u)
    {
        var side = a.Affects == Relation.Ally ? u.Owner == s.Owner : u.Owner != s.Owner && u.Owner != 0;
        return side && u.Def.Tags.Matches(a.AffectsTags) && Within(s.Pos, u.Pos, a.Radius);
    }

    // Stronger: aura a heals more HP per tick than b (cross-multiplied, exact), or as much with a larger single heal.
    private static bool Stronger(AuraEffect a, AuraEffect b)
    {
        long ra = (long)a.Heal * b.EveryTicks, rb = (long)b.Heal * a.EveryTicks;
        return ra != rb ? ra > rb : a.Heal > b.Heal;
    }
}
