namespace SimCore.Model;

/// <summary>
/// The sensors an air unit looks at the ground with (archetype or loadout <c>sensors</c>), as bits. They matter only for ground
/// units under canopy (<see cref="ConcealmentRules"/>); in the open every air unit sees its whole vision disc.
/// </summary>
[Flags]
public enum AirSensors : byte
{
    /// <summary>No sensor (a loadout without its own list: the archetype's).</summary>
    None = 0,

    /// <summary>A camera: the canopy hides from it as <see cref="ConcealmentRules.CameraReach"/> says. Every air unit's default.</summary>
    Camera = 1,

    /// <summary>
    /// Thermal: under any canopy it sees vehicles whose engine ran within <see cref="ConcealmentRules.EngineTicks"/> and moving
    /// infantry where <see cref="ConcealmentRules.ThermalMovingInfantry"/> allows; never still infantry.
    /// </summary>
    Thermal = 2,

    /// <summary>Ground radar (a moving-target radar): moving vehicles under any canopy; never infantry or a vehicle standing still.</summary>
    GroundRadar = 4,
}

/// <summary>
/// Concealment from the air under trees (content: the optional top-level <c>concealment</c> block; docs/design.md, "Concealment
/// from the air"). It applies with fog of war on a map with terrain: a ground unit that moves (infantry or a vehicle) standing in
/// forest whose density <see cref="Covers"/> is not spotted by the air units' discs on the fog grid; each air unit's sensors decide
/// instead, by the unit's kind (infantry or vehicle), whether it moved last tick, whether it fired lately and the density. Ground
/// units' sight into forest is unchanged. Distances are raw (level, from the air unit to the unit); <see cref="Seen"/> means seen
/// as in the open (within the air unit's vision), 0 hidden from cameras at any distance.
/// </summary>
public sealed class ConcealmentRules
{
    /// <summary>A camera reach meaning "seen as in the open" (no canopy effect).</summary>
    public const long Seen = -1;

    /// <summary>Within how far a camera sees still infantry, per forest density 0 to 3 (raw; <see cref="Seen"/>; 0: never).</summary>
    public required IReadOnlyList<long> InfantryStill { get; init; }

    /// <summary>Within how far a camera sees moving infantry, per forest density 0 to 3.</summary>
    public required IReadOnlyList<long> InfantryMoving { get; init; }

    /// <summary>Within how far a camera sees a still vehicle, per forest density 0 to 3.</summary>
    public required IReadOnlyList<long> VehicleStill { get; init; }

    /// <summary>Within how far a camera sees a moving vehicle, per forest density 0 to 3.</summary>
    public required IReadOnlyList<long> VehicleMoving { get; init; }

    /// <summary>A shot shows the shooter to every air sensor as in the open for this many vision passes after it (ticks).</summary>
    public required int FireRevealTicks { get; init; }

    /// <summary>Thermal sees a vehicle under canopy while it moved within this many ticks (its engine is warm).</summary>
    public required int EngineTicks { get; init; }

    /// <summary>Whether thermal sees moving infantry under canopy, per forest density 0 to 3.</summary>
    public required IReadOnlyList<bool> ThermalMovingInfantry { get; init; }

    /// <summary>A helicopter at most this high over the ground under it (raw) is low: its camera gets <see cref="HeliBonusRaw"/>.</summary>
    public required long HeliLowRaw { get; init; }

    /// <summary>What a low helicopter's camera adds to every limited reach (raw; a reach of 0, hidden, stays hidden).</summary>
    public required long HeliBonusRaw { get; init; }

    /// <summary>Whether canopy of this forest density (0 to 3) hides anything from a camera: then it is cover.</summary>
    public bool Covers(int density) =>
        InfantryStill[density] != Seen || InfantryMoving[density] != Seen || VehicleStill[density] != Seen || VehicleMoving[density] != Seen;

    /// <summary>Within how far a camera sees a unit of this kind and state under canopy of this density (raw; <see cref="Seen"/>; 0: never).</summary>
    public long CameraReach(bool vehicle, bool moving, int density) =>
        (vehicle ? moving ? VehicleMoving : VehicleStill : moving ? InfantryMoving : InfantryStill)[density];

    internal void Digest(Action<long> put)
    {
        foreach (var list in (IReadOnlyList<long>[])[InfantryStill, InfantryMoving, VehicleStill, VehicleMoving])
        {
            foreach (var v in list)
            {
                put(v);
            }
        }
        put(FireRevealTicks);
        put(EngineTicks);
        foreach (var b in ThermalMovingInfantry)
        {
            put(b ? 1 : 0);
        }
        put(HeliLowRaw);
        put(HeliBonusRaw);
    }
}
