using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// A fixed-wing aircraft's kinematic state in free flight (<see cref="Unit.Wing"/>, E4; hashed while
/// present; read-only for renderers). Speeds are raw a second (raw a tick times 20), like
/// <see cref="AirOps.Speed"/>. The per-tick step is <see cref="Unit.Vel"/> and <see cref="Unit.VelZ"/>;
/// this is what the client needs on top of them: the exact velocity, the throttle (the afterburner's
/// flame, the nozzle), the lateral acceleration it pulled (the bank) and its load factor (vapour).
/// </summary>
public sealed class FixedWingState
{
    /// <summary>Its speed (the length of its level velocity), raw a second.</summary>
    public long Speed { get; internal set; }

    /// <summary>Its level velocity, raw a second (exact: <see cref="Unit.Vel"/> is this a tick, with the remainder carried).</summary>
    public long VelX { get; internal set; }

    /// <summary>Its level velocity's y, raw a second.</summary>
    public long VelY { get; internal set; }

    /// <summary>Its throttle over the last tick.</summary>
    public Throttle Throttle { get; internal set; }

    /// <summary>
    /// The lateral acceleration it pulled over the last tick, raw a second squared, signed: positive
    /// turning clockwise on the map (to its right with y down), negative the other way. The client's
    /// coordinated-turn bank is atan(lateral / g), g being 9.81 m/s^2 = 5023 raw a second squared.
    /// </summary>
    public long Lateral { get; internal set; }

    /// <summary>Its load factor over the last tick, thousandths of g: sqrt(1 + (lateral / g)^2) (1000 in level flight).</summary>
    public int LoadPermille { get; internal set; }

    /// <summary>
    /// Ticks it is ordered to dash (its afterburner, up to its dash speed): the hook for evasion and
    /// strike egress (E6, E7). 0: the mission's speed (cruise; attack speed on a strike order).
    /// </summary>
    public int Boost { get; internal set; }

    /// <summary>
    /// Ticks left of a break (evasion, E6: a max-g turn to the beam of <see cref="EvadeFrom"/>, a descent
    /// toward its floor, the afterburner); 0: no break.
    /// </summary>
    public int Evade { get; internal set; }

    /// <summary>The missile (projectile ID) it breaks against, or 0.</summary>
    public uint EvadeFrom { get; internal set; }

    // The fuel burnt this tick, percent of nominal, not yet taken off the lifetime (the lifetime
    // system takes whole units); and the level step's remainders (raw a second, under 20).
    internal long FuelAcc { get; set; }

    internal long RemX { get; set; }

    internal long RemY { get; set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put(0xF1E);
        h.PutSigned(Speed);
        h.PutSigned(VelX);
        h.PutSigned(VelY);
        h.PutByte((byte)Throttle);
        h.PutSigned(Lateral);
        h.PutSigned(LoadPermille);
        h.PutSigned(Boost);
        if (Evade != 0 || EvadeFrom != 0)
        {
            h.Put(0xE7AD);
            h.PutSigned(Evade);
            h.Put(EvadeFrom);
        }
        h.PutSigned(FuelAcc);
        h.PutSigned(RemX);
        h.PutSigned(RemY);
    }
}
