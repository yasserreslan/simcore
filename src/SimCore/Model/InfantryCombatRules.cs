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

/// <summary>One small arm's numbers in the infantryCombat block: its class, its sound range (tenths of a cell) and its suppression a shot.</summary>
public readonly record struct CombatWeapon(string Id, WeaponEffect Effect, CombatClass Class, int Sound, int Suppress);

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

    /// <summary>Tags that make a unit an armoured vehicle, a light vehicle or a helicopter for the danger table.</summary>
    public required TagSet ArmourTags { get; init; }

    /// <inheritdoc cref="ArmourTags"/>
    public required TagSet LightVehicleTags { get; init; }

    /// <inheritdoc cref="ArmourTags"/>
    public required TagSet HelicopterTags { get; init; }

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
        }
        put(Values.Count);
        foreach (var v in Values)
        {
            put(v);
        }
    }
}
