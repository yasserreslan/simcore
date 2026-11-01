using SimCore.Model;

namespace SimCore.Sim;

// Decoys (docs/design.md, "Decoys"): the Resistance's fake launchers, radar and cargo truck. A decoy is an ordinary unit of
// its own archetype (tag decoy: it only moves; no weapon, ability, radar, cargo, crew; little HP) whose archetype names a
// disguise, the real archetype it passes for. Its owner knows it for what it is; every other player is shown the real
// one: these queries are what a renderer, a card, a minimap and the skirmish AI read for another player's unit, and what
// the sim's own target choice reads about an enemy (a threat first, the gunship's biggest pick, a sprung ambush's
// vehicles). What happens to its body (damage, the strike drone's kill rule, the radar spotting that finds an emitter)
// reads the truth. Pure functions of the content and the unit's owner: nothing new hashed.
public sealed partial class World
{
    /// <summary>
    /// The archetype <paramref name="viewer"/> is shown for <paramref name="u"/>: a decoy's disguise (the real archetype it passes
    /// for) to every player but its owner, its own archetype to its owner and to viewer 0 (no player: a spectator, a replay
    /// viewer); every other unit's own archetype to everyone.
    /// </summary>
    public Archetype ShownArchetype(Unit u, byte viewer) => ShownDef(u, viewer).Archetype;

    /// <summary>
    /// The unit definition <paramref name="viewer"/> is shown for <paramref name="u"/> (see <see cref="ShownArchetype"/>): for a
    /// decoy seen by another player, its owner's definition of the real archetype (its name, glyph, max HP, cost, weapons and
    /// tags as that player would see a real one); otherwise <c>u.Def</c>. A card shows another player's HP as a percent of the
    /// unit's own max (<see cref="ShownHpPercent"/>), never the shown definition's max HP against the decoy's HP.
    /// </summary>
    public UnitDef ShownDef(Unit u, byte viewer)
    {
        var d = u.Def.Archetype.Disguise;
        if (d < 0 || viewer == u.Owner || viewer == 0 || u.Owner == 0 || u.Owner > Match.Players.Count)
        {
            return u.Def;
        }
        return Match.Players[u.Owner - 1].Defaults[d] ?? u.Def;
    }

    /// <summary>Whether <paramref name="viewer"/> knows <paramref name="u"/> for a decoy: its owner (or viewer 0) does, nobody else.</summary>
    public bool KnownDecoy(Unit u, byte viewer) => u.Def.Archetype.Disguise >= 0 && ShownDef(u, viewer) == u.Def;

    /// <summary>
    /// <paramref name="u"/>'s HP as a whole percent of its own max HP (0 to 100, rounded down, at least 1 while alive): what a card
    /// shows for another player's unit, so a decoy's HP looks like the real one's at the same share.
    /// </summary>
    public static int ShownHpPercent(Unit u) =>
        u.Def.MaxHp <= 0 || u.HP <= 0 ? 0 : Math.Clamp((int)((long)u.HP * 100 / u.Def.MaxHp), 1, 100);
}
