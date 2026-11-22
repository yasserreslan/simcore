namespace SimCore.Model;

/// <summary>A contact's danger class (the battle rules' 4.4 table): from a soldier's main arm, or a vehicle's tags.</summary>
public enum CombatClass : byte
{
    /// <summary>Unknown.</summary>
    Unknown,
    /// <summary>Rifle.</summary>
    Rifle,
    /// <summary>Carbine.</summary>
    Carbine,
    /// <summary>Submachine gun.</summary>
    Smg,
    /// <summary>Pistol.</summary>
    Pistol,
    /// <summary>Light machine gun.</summary>
    Lmg,
    /// <summary>Marksman rifle.</summary>
    Marksman,
    /// <summary>Rocket launcher (infantry target).</summary>
    Launcher,
    /// <summary>Heavy weapon crew (Kornet, MANPADS).</summary>
    Crew,
    /// <summary>Light vehicle with a machine gun.</summary>
    LightVehicle,
    /// <summary>Armoured vehicle.</summary>
    Armour,
    /// <summary>Attack helicopter.</summary>
    Helicopter,
}

/// <summary>What a soldier does when an enemy comes within his range band's tooClose (brief 6.8).</summary>
public enum TooCloseAct : byte
{
    /// <summary>He holds: his spot and his main arm.</summary>
    Hold,
    /// <summary>He draws his sidearm (ArmsSystem).</summary>
    Secondary,
    /// <summary>He moves away 2 to 4 cells to cover, then switches back (not built yet: he holds).</summary>
    BackOff,
}

/// <summary>
/// One small arm's numbers in the infantryCombat block: its class, its sound range (tenths of a cell) and its suppression a shot;
/// INF-E1b-5 part 4 (brief 6.8, section 8): the range band of the soldier whose main arm it is: preferred min and max in percent
/// of its range (0 and 0: none), the band against vehicles (0 and 0: the same), tooClose (tenths of a cell, 0: none) and onTooClose.
/// </summary>
public readonly record struct CombatWeapon(string Id, WeaponEffect Effect, CombatClass Class, int Sound, int Suppress, int BandMin = 0, int BandMax = 0,
    int VehicleMin = 0, int VehicleMax = 0, int TooClose = 0, TooCloseAct OnTooClose = TooCloseAct.Hold);

/// <summary>
/// The infantryCombat block (INF-E1b-1; docs/design.md, "Infantry combat: the ground and the senses"): perception, danger,
/// exposure, cover, posture, suppression, danger zones and the think cadence. Every number is a content default.
/// </summary>
public sealed class InfantryCombatRules
{
    /// <summary>The numbers, one per <see cref="CombatValue"/>.</summary>
    public required IReadOnlyList<int> Values { get; init; }

    /// <summary>The small arms, in content order (by weapon ID).</summary>
    public required IReadOnlyList<CombatWeapon> Weapons { get; init; }

    /// <summary>
    /// The weapons whose reach is beyond their crew's own sight (targeting.sideSightWeapons: the Kornet; INF-MOVE-6, decision 1219):
    /// on Guard and Aggressive they also take as targets the enemies their side sees now, by component ID.
    /// </summary>
    public IReadOnlyList<(string Id, WeaponEffect Effect)> SideSightWeapons { get; init; } = [];

    /// <summary>Whether weapon effect e takes its targets from its side's sight (a reference match in <see cref="SideSightWeapons"/>).</summary>
    public bool SideSight(WeaponEffect e)
    {
        foreach (var w in SideSightWeapons)
        {
            if (ReferenceEquals(w.Effect, e))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Tags that make a unit an armoured vehicle, a light vehicle or a helicopter for the danger table.</summary>
    public required TagSet ArmourTags { get; init; }

    /// <inheritdoc cref="ArmourTags"/>
    public required TagSet LightVehicleTags { get; init; }

    /// <inheritdoc cref="ArmourTags"/>
    public required TagSet HelicopterTags { get; init; }

    /// <summary>Tags that make a friendly vehicle high cover (5.5); any other with a coverLowTags tag is low cover.</summary>
    public required TagSet CoverHighTags { get; init; }

    /// <inheritdoc cref="CoverHighTags"/>
    public required TagSet CoverLowTags { get; init; }

    /// <summary>Grenades carried by archetype ID (14.1), in ID order; an LMG gunner of one carries grenades.lmgCarry.</summary>
    public required IReadOnlyList<(string Archetype, int Count)> GrenadeCarry { get; init; }

    /// <summary>One of the block's numbers.</summary>
    public int this[CombatValue v] => Values[(int)v];

    /// <summary>The entry for weapon effect e (a reference match), or null for a weapon the block doesn't list.</summary>
    public CombatWeapon? Weapon(WeaponEffect e)
    {
        foreach (var w in Weapons)
        {
            if (ReferenceEquals(w.Effect, e))
            {
                return w;
            }
        }
        return null;
    }

    internal void Digest(Action<long> put)
    {
        put((long)ArmourTags.Bits);
        put((long)LightVehicleTags.Bits);
        put((long)HelicopterTags.Bits);
        put((long)CoverHighTags.Bits);
        put((long)CoverLowTags.Bits);
        foreach (var (a, n) in GrenadeCarry)
        {
            foreach (var ch in a)
            {
                put(ch);
            }
            put(n);
        }
        put(Weapons.Count);
        foreach (var w in Weapons)
        {
            foreach (var ch in w.Id)
            {
                put(ch);
            }
            put((long)w.Class);
            put(w.Sound);
            put(w.Suppress);
            if (w.BandMax > 0 || w.TooClose > 0)
            {
                put(0xBA4D); // INF-E1b-5 part 4: only weapons with a range band (content without them hashes as before)
                put(w.BandMin);
                put(w.BandMax);
                put(w.VehicleMin);
                put(w.VehicleMax);
                put(w.TooClose);
                put((long)w.OnTooClose);
            }
        }
        put(Values.Count);
        foreach (var v in Values)
        {
            put(v);
        }
        if (SideSightWeapons.Count > 0)
        {
            put(SideSightWeapons.Count); // only content that lists them (INF-MOVE-6): content without the list hashes as before
            foreach (var (id, _) in SideSightWeapons)
            {
                foreach (var ch in id)
                {
                    put(ch);
                }
            }
        }
    }
}
