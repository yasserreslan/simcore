using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>Where a helicopter is in its flight (<see cref="HeliState.Phase"/>).</summary>
public enum HeliPhase : byte
{
    /// <summary>In the air, flying its order.</summary>
    Flying,

    /// <summary>Coming straight down onto its pad or landing point.</summary>
    Landing,

    /// <summary>On the ground (a pad, an airfield station, or a field to board or drop riders); its rotor may run.</summary>
    Landed,

    /// <summary>Lifting off straight up to its height.</summary>
    TakingOff,

    /// <summary>Out of control (the owner's standard): flying itself to the nearest pad while its HP drains.</summary>
    OutOfControl,
}

/// <summary>A helicopter's attack step (<see cref="HeliState.Attack"/>; the pop-up and the gun runs).</summary>
public enum HeliAttack : byte
{
    /// <summary>Not attacking.</summary>
    None,

    /// <summary>A gun or rocket run: closing to its weapon's range, then sidestepping round the target, nose on it.</summary>
    Run,

    /// <summary>No cover found: hovering at its missile's standoff range, nose on the target.</summary>
    Standoff,

    /// <summary>Flying low (nap-of-the-earth) to its firing position behind cover.</summary>
    ToCover,

    /// <summary>Hiding behind the cover (a crest or woods between it and the target) low, until its missile is ready.</summary>
    Hide,

    /// <summary>Rising (the pop-up) until it has a line of sight over the cover.</summary>
    Rise,

    /// <summary>Exposed, firing: it launches and holds a moment.</summary>
    Fire,

    /// <summary>Ducking back down behind the cover.</summary>
    Duck,
}

/// <summary>An errand a helicopter flies besides its order (<see cref="HeliState.Errand"/>).</summary>
public enum HeliErrand : byte
{
    /// <summary>None.</summary>
    None,

    /// <summary>Load: it lands at <see cref="HeliState.LandAt"/> and waits for riders to board.</summary>
    Board,

    /// <summary>Unload: it flies to <see cref="HeliState.LandAt"/> and drops its riders there (landing, or hovering low).</summary>
    Drop,
}

/// <summary>How a helicopter's flight ended (<see cref="HeliCrash"/>).</summary>
public enum HeliCrashKind : byte
{
    /// <summary>Not crashed.</summary>
    None,

    /// <summary>Destroyed in the air (a further hit while out of control, or an outright kill): its wreck falls, nobody survives.</summary>
    AirDestroyed,

    /// <summary>Out of control, its time ran out: it crash-landed where it was; a wreck there, crew and riders rolled survival.</summary>
    CrashLanded,

    /// <summary>Out of control, it reached a pad and landed hard: it survives with little HP.</summary>
    HardLanded,

    /// <summary>Destroyed on the ground (parked or boarding): crew and riders rolled survival.</summary>
    DestroyedOnGround,
}

/// <summary>
/// A helicopter's state (<see cref="Unit.Heli"/>, every unit of a rotary flight has one; hashed): its exact velocity and
/// heading, its phase, the pad it is bound for or parked on, its errand, its attack step and its out-of-control clock.
/// Speeds are raw a second, the heading a vector of length 16384.
/// </summary>
public sealed class HeliState
{
    internal HeliState(int facing, int weapons)
    {
        var d = Facing.Dir(facing);
        (HeadX, HeadY) = ((int)(d.X.Raw * 16), (int)(d.Y.Raw * 16)); // a facing's unit vector is 1024 long
        Reloads = new int[weapons];
    }

    /// <summary>Its velocity (raw a second), x.</summary>
    public long VelX { get; internal set; }

    /// <summary>Its velocity (raw a second), y.</summary>
    public long VelY { get; internal set; }

    // What its velocity left over a tick (raw / 20).
    internal long RemX, RemY;

    /// <summary>Its heading (the nose), a vector 16384 long, x.</summary>
    public int HeadX { get; internal set; }

    /// <summary>Its heading (the nose), a vector 16384 long, y.</summary>
    public int HeadY { get; internal set; }

    /// <summary>Its yaw this tick (signed, 1/16384 of a radian a tick; positive clockwise on the map): turning on the spot shows here.</summary>
    public int Yaw { get; internal set; }

    /// <summary>Where it is in its flight.</summary>
    public HeliPhase Phase { get; internal set; }

    /// <summary>The tick its phase last changed.</summary>
    public uint Since { get; internal set; }

    /// <summary>Its rotor speed, permille of flying speed (0 stopped on the ground; it spools up before lift-off).</summary>
    public int Rotor { get; internal set; } = 1000;

    /// <summary>The pad it is bound for or parked on: a helipad's or an airfield station's unit ID, or 0.</summary>
    public uint Pad { get; internal set; }

    /// <summary>Whether it is held on its pad until its next order (Return to base, a hard landing, a new build).</summary>
    public bool Hold { get; internal set; }

    // Service remainders (fuel, ammunition, HP), as an airfield's (AirOps).
    internal long FuelAcc, AmmoAcc, HpAcc;

    /// <summary>Its errand (Load, Unload).</summary>
    public HeliErrand Errand { get; internal set; }

    /// <summary>Where it lands or hovers for its errand, or lands off a pad (a hard landing beside a full one).</summary>
    public Vec LandAt { get; internal set; }

    /// <summary>Unload: drop by hovering low (true) or by landing.</summary>
    public bool DropHover { get; internal set; }

    /// <summary>Unload: ticks until the next rider goes.</summary>
    public int DropLeft { get; internal set; }

    /// <summary>Its attack step.</summary>
    public HeliAttack Attack { get; internal set; }

    /// <summary>The unit it engages (an order's, or one it picked on Guard, attack-move, patrol or its stance), or 0.</summary>
    public uint Target { get; internal set; }

    /// <summary>The point it attacks on an attack-ground order (or the target's position as it last saw it).</summary>
    public Vec AimPoint { get; internal set; }

    /// <summary>Its firing position: behind cover (the pop-up) or at standoff, or the gun run's point.</summary>
    public Vec Cover { get; internal set; }

    /// <summary>The height (absolute, raw) it rises to at its cover to clear the line of sight.</summary>
    public long PopAlt { get; internal set; }

    /// <summary>Where the target stood when it chose its cover (a target that moves away makes it look again).</summary>
    public Vec CoverFrom { get; internal set; }

    /// <summary>Ticks in its attack step.</summary>
    public int AttackTicks { get; internal set; }

    /// <summary>Its post: where it hovers on Guard, idle (where it was sent), or picks its path up again on attack-move.</summary>
    public Vec Post { get; internal set; }

    /// <summary>Each weapon's reload (ticks), in its definition's weapon order.</summary>
    public int[] Reloads { get; }

    // A rocket salvo in progress: the weapon, the shots left, the ticks to the next, the aim and the target.
    internal int SalvoWeapon = -1, SalvoLeft, SalvoWait;
    internal Vec SalvoAim;
    internal uint SalvoTarget;

    /// <summary>Out of control: ticks left in the air.</summary>
    public int OocLeft { get; internal set; }

    /// <summary>Out of control: the HP it was left with when it went out of control.</summary>
    public int OocFrom { get; internal set; }

    // Out of control: the HP the drain left it last tick (a hit takes it below: destroyed in the air).
    internal int OocHp;

    /// <summary>Out of control: its height over the ground when it went out of control (raw).</summary>
    public long OocHeight { get; internal set; }

    /// <summary>Out of control: the way it spins (1 clockwise, -1 counter-clockwise).</summary>
    public int Spin { get; internal set; }

    // A hit this tick that destroys it outright (the Patriot type's): no out of control (set and used within the tick).
    internal bool Outright;

    /// <summary>How its flight ended (set on the tick it is lost or lands hard), or None.</summary>
    public HeliCrashKind Crash { get; internal set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put(0x4E11);
        h.PutSigned(VelX);
        h.PutSigned(VelY);
        h.PutSigned(RemX);
        h.PutSigned(RemY);
        h.PutSigned(HeadX);
        h.PutSigned(HeadY);
        h.PutByte((byte)Phase);
        h.Put(Since);
        h.PutSigned(Rotor);
        h.Put(Pad);
        h.Put(Hold ? 1UL : 0);
        h.PutSigned(FuelAcc);
        h.PutSigned(AmmoAcc);
        h.PutSigned(HpAcc);
        h.PutByte((byte)Errand);
        h.PutSigned(LandAt.X.Raw);
        h.PutSigned(LandAt.Y.Raw);
        h.Put(DropHover ? 1UL : 0);
        h.PutSigned(DropLeft);
        h.PutByte((byte)Attack);
        h.Put(Target);
        h.PutSigned(AimPoint.X.Raw);
        h.PutSigned(AimPoint.Y.Raw);
        h.PutSigned(Cover.X.Raw);
        h.PutSigned(Cover.Y.Raw);
        h.PutSigned(PopAlt);
        h.PutSigned(CoverFrom.X.Raw);
        h.PutSigned(CoverFrom.Y.Raw);
        h.PutSigned(AttackTicks);
        h.PutSigned(Post.X.Raw);
        h.PutSigned(Post.Y.Raw);
        foreach (var r in Reloads)
        {
            h.PutSigned(r);
        }
        h.PutSigned(SalvoWeapon);
        h.PutSigned(SalvoLeft);
        h.PutSigned(SalvoWait);
        h.PutSigned(SalvoAim.X.Raw);
        h.PutSigned(SalvoAim.Y.Raw);
        h.Put(SalvoTarget);
        h.PutSigned(OocLeft);
        h.PutSigned(OocFrom);
        h.PutSigned(OocHp);
        h.PutSigned(OocHeight);
        h.PutSigned(Spin);
        h.PutByte((byte)Crash);
    }
}

/// <summary>
/// A helicopter's flight ending this tick (<see cref="World.HeliCrashes"/>, read after <see cref="World.Step"/>): what
/// happened, where, and who got out. For the client's crash: an air kill throws a falling wreck (<see cref="World.Wrecks"/>),
/// a crash landing a wreck on the ground at <paramref name="At"/>, a hard landing a bounce on the pad.
/// </summary>
/// <param name="Helicopter">Its unit ID.</param>
/// <param name="Owner">Its player.</param>
/// <param name="Archetype">Its archetype index.</param>
/// <param name="Kind">How it ended.</param>
/// <param name="At">Where.</param>
/// <param name="Altitude">Its height (absolute, raw) then.</param>
/// <param name="Survivors">The unit IDs of the crew and riders who got out alive (stepped out as soldiers), in ID order.</param>
/// <param name="Lost">How many of its crew and riders died.</param>
/// <param name="Water">A crash landing in open water (a river, lake or sea): it sinks, no wreck on the ground, and nobody
/// gets out (no swimming).</param>
public sealed record HeliCrash(uint Helicopter, byte Owner, int Archetype, HeliCrashKind Kind, Vec At, Fixed Altitude, IReadOnlyList<uint> Survivors, int Lost,
    bool Water = false);

/// <summary>A helicopter's state for a client (<see cref="World.HeliStatusOf"/>; read-only).</summary>
/// <param name="Phase">Where it is in its flight.</param>
/// <param name="Attack">Its attack step (the pop-up's Hide, Rise, Fire, Duck; a gun run).</param>
/// <param name="Target">The unit it engages, or 0.</param>
/// <param name="HeadX">Its heading, x (a vector 16384 long): its nose, apart from where it flies.</param>
/// <param name="HeadY">Its heading, y.</param>
/// <param name="VelX">Its velocity, raw a second, x.</param>
/// <param name="VelY">Its velocity, raw a second, y.</param>
/// <param name="VelZ">Its climb (raw a tick; negative sinking).</param>
/// <param name="HeightOverGround">Its height over the ground under it (raw).</param>
/// <param name="Rotor">Its rotor speed, permille.</param>
/// <param name="Pad">The pad it is bound for or parked on, or 0.</param>
/// <param name="Riders">How many riders it carries.</param>
/// <param name="Seats">How many rider seats it has.</param>
/// <param name="OutOfControl">Whether it is out of control.</param>
/// <param name="OocTicksLeft">Out of control: ticks until it crash-lands.</param>
/// <param name="OocSpinRate">Out of control: its spin, degrees a second, signed (clockwise positive).</param>
/// <param name="OocSinkRate">Out of control: how fast it sinks toward its end height (raw a tick).</param>
/// <param name="Errand">Its Load or Unload errand.</param>
public readonly record struct HeliStatus(HeliPhase Phase, HeliAttack Attack, uint Target, int HeadX, int HeadY, long VelX, long VelY, long VelZ,
    long HeightOverGround, int Rotor, uint Pad, int Riders, int Seats, bool OutOfControl, int OocTicksLeft, int OocSpinRate, long OocSinkRate,
    HeliErrand Errand);

/// <summary>A helipad's state for a client (<see cref="World.PadStatusOf"/>; read-only).</summary>
/// <param name="Occupant">The helicopter parked on it, or 0.</param>
/// <param name="Inbound">The helicopter bound for it (landing or flying home to it), or 0.</param>
/// <param name="Building">The unit definition index of the helicopter it builds, or -1.</param>
/// <param name="BuildPercent">How far that build is, percent.</param>
/// <param name="Servicing">Whether it is servicing its occupant now (fuel, ammunition or HP short and the pad gives them).</param>
public readonly record struct PadStatus(uint Occupant, uint Inbound, int Building, int BuildPercent, bool Servicing);
