using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// Where an aircraft is in an air-to-ground attack (<see cref="StrikeRun.Phase"/>; E7, the attack-run state
/// machine since E13): Ingress -> Align -> Run -> Break -> Extend -> Reposition -> Align again while ordered and
/// armed, or Egress after its last pass.
/// </summary>
public enum StrikePhase : byte
{
    /// <summary>Flying toward the target (at its ingress height) until it can lock a run line with room to align.</summary>
    Ingress,

    /// <summary>
    /// Wings level on the run line, locked from the aim point through where it was when the align began (the
    /// entry heading, chosen before the run): the turn-in is behind it. A dive pops up toward its roll-in height.
    /// </summary>
    Align,

    /// <summary>The run from the initial point: the profile's height and speed, steering at the aim point, releasing (or firing bursts).</summary>
    Run,

    /// <summary>Pulling off the target: a hard turn to its break side for at least the break's angle and travel, the afterburner, a climb.</summary>
    Break,

    /// <summary>Straight on, away from the target, until it has room to turn in and align again (or reaches the fly zone's band).</summary>
    Extend,

    /// <summary>The turn back (the break's side again) until the target is on its nose: then the next align.</summary>
    Reposition,

    /// <summary>After its last pass (out of what hits the target): the break and extend away, then the strike ends.</summary>
    Egress,
}

/// <summary>How a pass is flown (<see cref="StrikeRun.Kind"/>; E7).</summary>
public enum PassKind : byte
{
    /// <summary>A level bomb run: it holds the release height and its attack speed.</summary>
    Level,

    /// <summary>A dive: it pops up to the roll-in height and dives at the dive angle to the release height.</summary>
    Dive,

    /// <summary>A toss: low and fast (the afterburner), a pull-up and the release climbing.</summary>
    Toss,

    /// <summary>A guided (glide) bomb from a level run, released as soon as the target is within its reach.</summary>
    Guided,

    /// <summary>A strafing pass with the gun: a shallow dive, bursts walking across the target, a pull-out at the floor.</summary>
    Strafe,
}

/// <summary>
/// An aircraft's air-to-ground attack in progress (<see cref="Unit.Strike"/>, E7; hashed while present;
/// read-only for renderers and the sidebar): its ordered target or point, the weapon and the pass it
/// flies, the phase, the run-in line, the release distance its profile needs.
/// </summary>
public sealed class StrikeRun
{
    /// <summary>The unit it attacks, or 0 for a point (an attack-ground order).</summary>
    public uint Target { get; internal set; }

    /// <summary>The point it attacks (an attack-ground order's cell).</summary>
    public Cell Cell { get; internal set; }

    /// <summary>The weapon of this pass (its index in the unit's weapons).</summary>
    public int Weapon { get; internal set; }

    /// <summary>How this pass is flown.</summary>
    public PassKind Kind { get; internal set; }

    /// <summary>The phase.</summary>
    public StrikePhase Phase { get; internal set; }

    /// <summary>The run line's direction from the aim point toward the align's start (thousandths, a unit vector; set when the align begins).</summary>
    public int DirX { get; internal set; }

    /// <summary>The run line's y.</summary>
    public int DirY { get; internal set; }

    /// <summary>The side it breaks and turns back to: +1 clockwise on the map (right, y down), -1 the other way (E13).</summary>
    public int Side { get; internal set; }

    /// <summary>Its heading as the break began (thousandths, a unit vector; E13): the break lasts until it has turned its break angle.</summary>
    public int HeadX { get; internal set; }

    /// <summary>The heading's y.</summary>
    public int HeadY { get; internal set; }

    /// <summary>Where the phase began (the break's and the extend's travel are measured from it; E13).</summary>
    public Vec Mark { get; internal set; }

    /// <summary>The tick the phase began (E13; a wingman's align follows its lead's).</summary>
    public uint Since { get; internal set; }

    /// <summary>The horizontal distance from the aim point at which its profile releases (raw), worked out at the pass's start.</summary>
    public long ReleaseDist { get; internal set; }

    /// <summary>The initial point's distance from the release point (raw): the weapon's ipDistance.</summary>
    public long IpDist { get; internal set; }

    /// <summary>Ticks left of the egress (unused since E13's state machine; kept 0).</summary>
    public int Ticks { get; internal set; }

    /// <summary>Passes flown (each run that ended in a release, a burst or an abort).</summary>
    public int Passes { get; internal set; }

    /// <summary>Bombs (or bursts) released on this pass.</summary>
    public int Released { get; internal set; }

    /// <summary>The tick its last bomb or burst left (a stick's spacing; the walk of a strafing pass).</summary>
    public uint LastRelease { get; internal set; }

    /// <summary>The aim point (the target's lead point, or the point) as last worked out.</summary>
    public Vec Aim { get; internal set; }

    /// <summary>The fall time of its last predicted release (ticks), for the lead on a moving target.</summary>
    public int FallTicks { get; internal set; }

    /// <summary>Ticks in a row its target has been out of its side's sight (it attacks the last seen position).</summary>
    public int Unseen { get; internal set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put(0x57A1);
        h.Put(Target);
        h.PutSigned(Cell.X);
        h.PutSigned(Cell.Y);
        h.PutSigned(Weapon);
        h.PutByte((byte)Kind);
        h.PutByte((byte)Phase);
        h.PutSigned(DirX);
        h.PutSigned(DirY);
        h.PutSigned(Side);
        h.PutSigned(HeadX);
        h.PutSigned(HeadY);
        h.PutSigned(Mark.X.Raw);
        h.PutSigned(Mark.Y.Raw);
        h.Put(Since);
        h.PutSigned(ReleaseDist);
        h.PutSigned(IpDist);
        h.PutSigned(Ticks);
        h.PutSigned(Passes);
        h.PutSigned(Released);
        h.Put(LastRelease);
        h.PutSigned(Aim.X.Raw);
        h.PutSigned(Aim.Y.Raw);
        h.PutSigned(FallTicks);
        h.PutSigned(Unseen);
    }
}

/// <summary>
/// A bomb's fall (<see cref="Projectile.Bomb"/>, E7; hashed while present; read-only for renderers): an
/// integer parabola, velocity in raw a second (raw a tick times 20, like the fixed wing's) with the
/// twentieth's remainder carried, gravity taken off the vertical velocity each tick; or, for a guided
/// bomb, a straight glide at its target (a hit) or beside it (a miss) at the weapon's speed.
/// </summary>
public sealed class BombFall
{
    /// <summary>Its velocity, raw a second.</summary>
    public long VelX { get; internal set; }

    /// <summary>Its velocity's y, raw a second.</summary>
    public long VelY { get; internal set; }

    /// <summary>Its vertical velocity, raw a second (up positive).</summary>
    public long VelZ { get; internal set; }

    /// <summary>The vertical velocity it loses a tick (raw a second): the delivery's gravity / 20.</summary>
    public long GravityTick { get; internal set; }

    /// <summary>Whether it is a guided (glide) bomb.</summary>
    public bool Guided { get; internal set; }

    /// <summary>A guided bomb: whether its release roll hit (it homes on its target).</summary>
    public bool Hit { get; internal set; }

    /// <summary>Where it is expected to land (the release solution's point, with the scatter; a guided bomb: its aim).</summary>
    public Vec Predicted { get; internal set; }

    /// <summary>The tick it is expected to land.</summary>
    public uint ImpactTick { get; internal set; }

    /// <summary>The airstrike tier of its blast (1 to 7, by its damage: <see cref="World.StrikeTier"/>).</summary>
    public int Tier { get; internal set; }

    internal long RemX { get; set; }

    internal long RemY { get; set; }

    internal long RemZ { get; set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put(0xB0B);
        h.PutSigned(VelX);
        h.PutSigned(VelY);
        h.PutSigned(VelZ);
        h.PutSigned(GravityTick);
        h.Put(Guided ? 1UL : 0);
        h.Put(Hit ? 1UL : 0);
        h.PutSigned(Predicted.X.Raw);
        h.PutSigned(Predicted.Y.Raw);
        h.Put(ImpactTick);
        h.PutSigned(Tier);
        h.PutSigned(RemX);
        h.PutSigned(RemY);
        h.PutSigned(RemZ);
    }
}

/// <summary>A bomb released this tick (for renderers and logs; E7): draw it falling from here.</summary>
/// <param name="Bomb">The bomb's projectile ID (its position, height and step each tick: <see cref="World.Projectiles"/>).</param>
/// <param name="Aircraft">The aircraft that released it.</param>
/// <param name="Weapon">The weapon's index in the aircraft's weapons.</param>
/// <param name="Kind">How the pass was flown (level, dive, toss, guided).</param>
/// <param name="Target">The unit it was aimed at, or 0 for a point.</param>
/// <param name="At">Where it left the aircraft.</param>
/// <param name="Altitude">Its height there (absolute, raw: 512 a metre).</param>
/// <param name="VelX">Its velocity at release, raw a second.</param>
/// <param name="VelY">Its velocity's y.</param>
/// <param name="VelZ">Its vertical velocity (up positive).</param>
/// <param name="Predicted">Where it is expected to land.</param>
/// <param name="ImpactTick">When.</param>
/// <param name="Tier">The airstrike tier of its blast (1 to 7).</param>
public readonly record struct BombRelease(uint Bomb, uint Aircraft, int Weapon, PassKind Kind, uint Target, Vec At, Fixed Altitude,
    long VelX, long VelY, long VelZ, Vec Predicted, uint ImpactTick, int Tier);

/// <summary>
/// A bomb going off this tick (for renderers and logs; E7): the airstrike visuals' input. The client's
/// smoke picks its look by <see cref="Tier"/> (or <see cref="Damage"/> with its own table), seeded by
/// the bomb's ID; on a runway the airfield's crater (the weapon's runway effect) is drawn by the airfield
/// kit, elsewhere a terrain crater and scorch sized by the tier.
/// </summary>
/// <param name="Bomb">The bomb's projectile ID (the same ID's <see cref="Detonation"/> is in <see cref="World.Detonations"/>).</param>
/// <param name="Aircraft">The aircraft that released it.</param>
/// <param name="Owner">Its player.</param>
/// <param name="Weapon">The weapon's index in the aircraft's weapons.</param>
/// <param name="At">Where it went off.</param>
/// <param name="Altitude">Its height there (absolute, raw): the ground, or a roof it struck.</param>
/// <param name="Damage">The weapon's damage (before armour).</param>
/// <param name="Tier">The airstrike tier (1 to 7, by damage: <see cref="World.StrikeTier"/>).</param>
/// <param name="Warhead">The warhead's content ID.</param>
/// <param name="Hit">The unit it struck directly (a building's roof, a vehicle), or 0 for the ground.</param>
/// <param name="Building">Whether it struck a structure.</param>
/// <param name="Runway">The crater it dug on an airfield's runway (the weapon's runway effect), or None off the runway.</param>
/// <param name="Guided">Whether it was a guided bomb.</param>
public readonly record struct BombImpact(uint Bomb, uint Aircraft, byte Owner, int Weapon, Vec At, Fixed Altitude, int Damage, int Tier,
    string Warhead, uint Hit, bool Building, CraterKind Runway, bool Guided);

/// <summary>A strafing burst this tick (for renderers and logs; E7): tracers from the aircraft to the impact, a splash of hits there.</summary>
/// <param name="Shooter">The aircraft.</param>
/// <param name="Target">The unit it strafes, or 0 for a point.</param>
/// <param name="From">The shooter's position.</param>
/// <param name="FromAltitude">The shooter's height (absolute, raw).</param>
/// <param name="At">Where the burst lands (it walks across the target over the pass).</param>
/// <param name="AtAltitude">The ground's height there (absolute, raw).</param>
/// <param name="Weapon">The gun's index in the aircraft's weapons.</param>
public readonly record struct StrafeBurst(uint Shooter, uint Target, Vec From, Fixed FromAltitude, Vec At, Fixed AtAltitude, int Weapon);
