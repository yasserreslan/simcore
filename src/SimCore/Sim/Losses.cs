using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// A burning wreck falling on its crash path (E10; <see cref="World.Wrecks"/>; hashed; read-only for
/// renderers): an aircraft lost in the air with a <c>loss.crash</c> block. It keeps the aircraft's ID, falls
/// under its crash gravity with its drag, never steers, and goes off where it meets the ground or a ground
/// unit's top (<see cref="World.WreckImpacts"/>).
/// </summary>
public sealed class Wreck
{
    internal Wreck(uint id, byte owner, int archetype, int def, KillCause cause, uint killer, bool brokenUp)
    {
        Id = id;
        Owner = owner;
        Archetype = archetype;
        Def = def;
        Cause = cause;
        Killer = killer;
        BrokenUp = brokenUp;
    }

    /// <summary>The lost aircraft's unit ID (the wreck carries it on).</summary>
    public uint Id { get; }

    /// <summary>Its player.</summary>
    public byte Owner { get; }

    /// <summary>Its archetype index (the model to draw).</summary>
    public int Archetype { get; }

    /// <summary>Its unit definition's index in the match (the loadout, for the stores still on it).</summary>
    public int Def { get; }

    /// <summary>What brought it down.</summary>
    public KillCause Cause { get; }

    /// <summary>The unit that made the killing blow, or 0.</summary>
    public uint Killer { get; }

    /// <summary>Whether the airframe broke up in the air (a missile kill's roll): it falls steeper, the client throws wing and tail pieces.</summary>
    public bool BrokenUp { get; }

    /// <summary>Its position (the point under it).</summary>
    public Vec Pos { get; internal set; }

    /// <summary>Its height (absolute, raw: 512 a metre).</summary>
    public Fixed Altitude { get; internal set; }

    /// <summary>Its velocity, raw a second (x).</summary>
    public long VelX { get; internal set; }

    /// <summary>Its velocity, raw a second (y).</summary>
    public long VelY { get; internal set; }

    /// <summary>Its vertical velocity, raw a second (up positive).</summary>
    public long VelZ { get; internal set; }

    /// <summary>Ticks since it was lost.</summary>
    public int Age { get; internal set; }

    internal long RemX { get; set; }

    internal long RemY { get; set; }

    internal long RemZ { get; set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put(Id);
        h.Put(Owner);
        h.PutSigned(Archetype);
        h.PutSigned(Def);
        h.Put((byte)Cause);
        h.Put(Killer);
        h.Put(BrokenUp ? 1UL : 0);
        h.PutSigned(Pos.X.Raw);
        h.PutSigned(Pos.Y.Raw);
        h.PutSigned(Altitude.Raw);
        h.PutSigned(VelX);
        h.PutSigned(VelY);
        h.PutSigned(VelZ);
        h.PutSigned(Age);
        h.PutSigned(RemX);
        h.PutSigned(RemY);
        h.PutSigned(RemZ);
    }
}

/// <summary>Where an ejected pilot is in his descent (<see cref="Chute.Phase"/>).</summary>
public enum ChutePhase : byte
{
    /// <summary>The seat's rocket throws him up.</summary>
    Seat,

    /// <summary>Out of the seat, under the drogue: coming down at the chute rate, the main chute not open yet.</summary>
    Drogue,

    /// <summary>Under the main canopy.</summary>
    Open,
}

/// <summary>
/// An ejected pilot on his way down (E10; <see cref="World.Chutes"/>; hashed; read-only for renderers): the
/// seat's rise, then the descent at the chute rate, drifting along the aircraft's last heading. Not a unit
/// until he lands (untargetable: decision D8); then a unit of his archetype of the aircraft's owner.
/// </summary>
public sealed class Chute
{
    internal Chute(uint id, uint aircraft, int aircraftArchetype, byte owner, int unit, Vec pos, Fixed altitude, Vec drift)
    {
        Id = id;
        Aircraft = aircraft;
        AircraftArchetype = aircraftArchetype;
        Owner = owner;
        Unit = unit;
        Pos = pos;
        Altitude = altitude;
        From = altitude;
        Drift = drift;
    }

    /// <summary>Its ID (from the unit ID sequence; the pilot unit gets a new one on landing: see <see cref="EjectionEvent.Unit"/>).</summary>
    public uint Id { get; }

    /// <summary>The aircraft he ejected from (gone: its wreck carries the ID).</summary>
    public uint Aircraft { get; }

    /// <summary>The aircraft's archetype index (its loss block's eject numbers).</summary>
    public int AircraftArchetype { get; }

    /// <summary>His player.</summary>
    public byte Owner { get; }

    /// <summary>The archetype he becomes on landing (the pilot), an index into the content's archetypes.</summary>
    public int Unit { get; }

    /// <summary>Where he is (the point under him).</summary>
    public Vec Pos { get; internal set; }

    /// <summary>His height (absolute, raw: 512 a metre).</summary>
    public Fixed Altitude { get; internal set; }

    /// <summary>The ejection's height (absolute, raw).</summary>
    public Fixed From { get; }

    /// <summary>His drift a tick while he drifts.</summary>
    public Vec Drift { get; }

    /// <summary>Ticks since the ejection.</summary>
    public int Age { get; internal set; }

    /// <summary>Where he is in his descent.</summary>
    public ChutePhase Phase { get; internal set; }

    internal long RemZ { get; set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put(Id);
        h.Put(Aircraft);
        h.PutSigned(AircraftArchetype);
        h.Put(Owner);
        h.PutSigned(Unit);
        h.PutSigned(Pos.X.Raw);
        h.PutSigned(Pos.Y.Raw);
        h.PutSigned(Altitude.Raw);
        h.PutSigned(From.Raw);
        h.PutSigned(Drift.X.Raw);
        h.PutSigned(Drift.Y.Raw);
        h.PutSigned(Age);
        h.Put((byte)Phase);
        h.PutSigned(RemZ);
    }
}

/// <summary>A wreck going off where it hit (for renderers and logs; E10): the ground explosion, the debris, the fire.</summary>
/// <param name="Wreck">The wreck's ID (the lost aircraft's).</param>
/// <param name="Owner">Its player.</param>
/// <param name="Archetype">Its archetype index.</param>
/// <param name="Cause">What brought it down.</param>
/// <param name="BrokenUp">Whether it broke up in the air.</param>
/// <param name="At">Where it hit.</param>
/// <param name="Altitude">The height there (absolute, raw: the ground, or the top of what it hit).</param>
/// <param name="VelX">Its velocity at impact, raw a second (x): the debris are thrown along it.</param>
/// <param name="VelY">Its velocity at impact (y).</param>
/// <param name="VelZ">Its vertical velocity at impact (down negative).</param>
/// <param name="AngleDeg">The impact angle below the horizontal, whole degrees (under 20: a shallow impact that spreads the debris along the heading).</param>
/// <param name="Hit">The ground unit it came down on (a structure's roof, a vehicle, a soldier), or 0.</param>
/// <param name="Building">Whether that unit is a structure.</param>
/// <param name="Runway">The crater it dug on a runway (none off the runway, or without a runway effect).</param>
/// <param name="Water">Whether it came down in open water (a river, lake or sea; Hit 0): a splash and it sinks, no burning wreck
/// and no crater; Altitude is the surface (its splash damage still applies round it).</param>
public readonly record struct WreckImpact(uint Wreck, byte Owner, int Archetype, KillCause Cause, bool BrokenUp, Vec At, Fixed Altitude,
    long VelX, long VelY, long VelZ, int AngleDeg, uint Hit, bool Building, CraterKind Runway, bool Water = false);

/// <summary>What happened in an ejection this tick (<see cref="EjectionEvent.Kind"/>).</summary>
public enum EjectionEventKind : byte
{
    /// <summary>The canopy jettisoned (the gun kill's tick, listed before the seat: the client plays it a fraction of a second ahead).</summary>
    Canopy,

    /// <summary>The seat fired: the climb begins (the gun kill's tick).</summary>
    Seat,

    /// <summary>The seat's rise is over: seat-man separation (the seat tumbles away, client only), the drogue.</summary>
    SeatAway,

    /// <summary>The main canopy opened (the deploy clip).</summary>
    ChuteOpen,

    /// <summary>He landed: <see cref="EjectionEvent.Unit"/> is the pilot unit now on the ground.</summary>
    Landed,

    /// <summary>He came down where no soldier can stand (no passable cell anywhere near) or his side has no pilot to spawn: lost.</summary>
    Lost,
}

/// <summary>A step of an ejection this tick (for renderers and logs; E10).</summary>
/// <param name="Chute">The chute's ID (<see cref="World.Chutes"/> while he comes down).</param>
/// <param name="Aircraft">The aircraft he ejected from.</param>
/// <param name="Owner">His player.</param>
/// <param name="Kind">What happened.</param>
/// <param name="At">Where he is (on landing: where the unit stands).</param>
/// <param name="Altitude">His height (absolute, raw).</param>
/// <param name="Unit">On landing, the pilot unit's ID; else 0.</param>
public readonly record struct EjectionEvent(uint Chute, uint Aircraft, byte Owner, EjectionEventKind Kind, Vec At, Fixed Altitude, uint Unit);
