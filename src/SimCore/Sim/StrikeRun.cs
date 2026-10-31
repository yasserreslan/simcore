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

    // ---- The pass plan (H2): fire modes, a queue of aims bombed together, a line ----

    /// <summary>
    /// Whether this pass is planned (H2): a free-fall bomb with fire modes, or an aircraft with a queue of aims or a line. Its
    /// bombs go at <see cref="Drops"/> in order; otherwise the pass is E7's (one release solution at the aim, then the stick).
    /// </summary>
    public bool Planned { get; internal set; }

    /// <summary>
    /// Whether the run line's direction is fixed by the plan (H2: a line, or aims bombed together), not by where it comes from:
    /// it flies to an entry point on the line's extension first (<see cref="Entry"/>) and follows the line on its run.
    /// </summary>
    public bool Forced { get; internal set; }

    /// <summary>A forced pass flying to its entry point (on the line's extension, behind its first drop) before it turns in (H2).</summary>
    public bool Entry { get; internal set; }

    /// <summary>The planned pass's last: after it, its order is done (a line bombed with nothing queued after it; H2): it leaves and the strike ends.</summary>
    public bool Final { get; internal set; }

    /// <summary>
    /// Where the planned pass's bombs go, in the order it releases them (H2; positions as planned: a moving unit's drops follow it).
    /// A renderer may draw them as the pass's aim marks. Empty before an unforced pass locks its run line.
    /// </summary>
    public IReadOnlyList<Vec> Drops => _drops;

    /// <summary>The next drop to release (an index into <see cref="Drops"/>).</summary>
    public int DropNext { get; internal set; }

    /// <summary>The aims this pass serves (H2): its order's first, then the queued ones bombed with it, in queue order.</summary>
    public IReadOnlyList<StrikeAim> Served => _served;

    /// <summary>Which of <see cref="Served"/> got a bomb on this pass (bit i: aim i).</summary>
    public ulong ServedHits { get; internal set; }

    /// <summary>
    /// Rows across the path (the supersonic bomber's fire modes, <see cref="FireModeDef.Across"/>): this many drops make a row, side by
    /// side across its run (drops i with the same i / Across), released in the same tick; 1: no rows (a stick along the path).
    /// </summary>
    public int Across { get; internal set; } = 1;

    /// <summary>
    /// Each drop's offset across the run from its row's centre (raw x and y; empty without rows). <see cref="Drops"/> holds the row's
    /// centre (where the aircraft releases it from: its run line), so drop i lands at Drops[i] + DropSide[i] (<see cref="DropPoint"/>).
    /// The stealth bomber's precision bombs (<see cref="Model.Delivery.Precision"/>) fill it too, with <see cref="Across"/> 1: each drop's
    /// own point aside of the run line (a pattern on a point, a queued aim off the line), steered there exactly.
    /// </summary>
    public IReadOnlyList<Vec> DropSide => _side;

    /// <summary>Where drop i goes as planned: its place (<see cref="Drops"/>) plus its offset across the run (<see cref="DropSide"/>).</summary>
    public Vec DropPoint(int i) =>
        i < _side.Count ? new Vec(Fixed.FromRaw(_drops[i].X.Raw + _side[i].X.Raw), Fixed.FromRaw(_drops[i].Y.Raw + _side[i].Y.Raw)) : _drops[i];

    internal List<Vec> MutableDrops => _drops;

    internal List<Vec> MutableSide => _side;

    internal List<StrikeAim> MutableServed => _served;

    // Each drop's aim (an index into Served), and each served aim's position when planned (a moving unit's drops follow it).
    internal List<int> DropAim { get; } = [];

    internal List<Vec> ServedAt { get; } = [];

    private readonly List<Vec> _drops = [];
    private readonly List<Vec> _side = [];
    private readonly List<StrikeAim> _served = [];

    // ClearPlan forgets the pass plan (an E7 pass again).
    internal void ClearPlan()
    {
        (Planned, Forced, Entry, DropNext, ServedHits, Across) = (false, false, false, 0, 0, 1);
        _drops.Clear();
        _side.Clear();
        _served.Clear();
        DropAim.Clear();
        ServedAt.Clear();
    }

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
        if (Planned || Final)
        {
            // Only planned passes (H2): E7 passes hash as before.
            h.Put(0x9A55);
            h.Put((Forced ? 1UL : 0) | (Entry ? 2UL : 0) | (Final ? 4UL : 0));
            h.PutSigned(DropNext);
            h.Put(ServedHits);
            h.PutSigned(_drops.Count);
            for (var i = 0; i < _drops.Count; i++)
            {
                h.PutSigned(_drops[i].X.Raw);
                h.PutSigned(_drops[i].Y.Raw);
                h.PutSigned(DropAim[i]);
            }
            h.PutSigned(_served.Count);
            for (var i = 0; i < _served.Count; i++)
            {
                var a = _served[i];
                h.Put(a.Target);
                h.PutSigned(a.Cell.X);
                h.PutSigned(a.Cell.Y);
                h.PutSigned(a.End.X);
                h.PutSigned(a.End.Y);
                h.Put(a.Line ? 1UL : 0);
                h.PutSigned(ServedAt[i].X.Raw);
                h.PutSigned(ServedAt[i].Y.Raw);
            }
            if (Across > 1 || _side.Count > 0)
            {
                // Only rows across the path (the supersonic bomber) or drops aside of the run line (the stealth bomber's precision
                // bombs): other passes hash as before.
                h.Put(0x5D0E);
                h.PutSigned(Across);
                h.PutSigned(_side.Count);
                for (var i = 0; i < _side.Count; i++)
                {
                    h.PutSigned(_side[i].X.Raw);
                    h.PutSigned(_side[i].Y.Raw);
                }
            }
        }
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

    /// <summary>A high-drag (retarded) bomb's horizontal speed lost a tick, permille (H2; 0: a slick bomb).</summary>
    public int Retard { get; internal set; }

    /// <summary>Where it is expected to land (the release solution's point, with the scatter; a guided bomb: its aim).</summary>
    public Vec Predicted { get; internal set; }

    /// <summary>The tick it is expected to land.</summary>
    public uint ImpactTick { get; internal set; }

    /// <summary>The airstrike tier of its blast (1 to 7, by its damage: <see cref="World.StrikeTier"/>; a gunship's round: its weapon's look).</summary>
    public int Tier { get; internal set; }

    /// <summary>A gunship's side-cannon round (H3): it flies straight at <see cref="Predicted"/> at its weapon's speed (as a glide bomb that missed).</summary>
    public bool Shell { get; internal set; }

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
        if (Retard != 0)
        {
            h.PutSigned(Retard); // only retarded bombs (H2)
        }
        if (Shell)
        {
            h.Put(0x5E11); // only a gunship's rounds (H3)
        }
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
/// <param name="Shell">Whether it was a gunship's side-cannon round (H3; <see cref="World.SideFires"/>): Tier is its weapon's look.</param>
/// <param name="Crater">The crater it leaves on the open ground, wherever it lands: <see cref="CraterKind.Deep"/> for a bunker buster (it stays:
/// <see cref="World.Craters"/>), else None (the client's own crater by the tier); None in the water.</param>
/// <param name="Water">Whether it went off in open water (a river, lake or sea; Hit 0): a water column by its tier, a flash under
/// the surface, rings, spray and foam; no crater (not even a bunker buster's), no fire or burnt ground. Altitude is the surface.</param>
public readonly record struct BombImpact(uint Bomb, uint Aircraft, byte Owner, int Weapon, Vec At, Fixed Altitude, int Damage, int Tier,
    string Warhead, uint Hit, bool Building, CraterKind Runway, bool Guided, bool Shell = false, CraterKind Crater = CraterKind.None, bool Water = false);

/// <summary>A strafing burst this tick (for renderers and logs; E7): tracers from the aircraft to the impact, a splash of hits there.</summary>
/// <param name="Shooter">The aircraft.</param>
/// <param name="Target">The unit it strafes, or 0 for a point.</param>
/// <param name="From">The shooter's position.</param>
/// <param name="FromAltitude">The shooter's height (absolute, raw).</param>
/// <param name="At">Where the burst lands (it walks across the target over the pass).</param>
/// <param name="AtAltitude">The ground's height there (absolute, raw).</param>
/// <param name="Weapon">The gun's index in the aircraft's weapons.</param>
/// <param name="Water">Whether the burst lands in open water (spouts of water instead of dust; AtAltitude is the surface).</param>
public readonly record struct StrafeBurst(uint Shooter, uint Target, Vec From, Fixed FromAltitude, Vec At, Fixed AtAltitude, int Weapon, bool Water = false);

/// <summary>Why an aircraft refused (or had to give up) a player's attack order (E13, decision D-ORD1).</summary>
public enum OrderRefusalReason : byte
{
    /// <summary>Every stock is empty: it rearms only on a Rearm order.</summary>
    NoAmmo,

    /// <summary>It carries ammunition, but nothing that can hurt this target (or point).</summary>
    NoWeaponForTarget,

    /// <summary>It is on its automatic trip home to refuel (bingo fuel): the order waits for nobody; give it again after.</summary>
    RefuelTrip,

    /// <summary>It is coming home on a Rearm or ReturnToBase order (holding or landing): it lands first.</summary>
    Homing,

    /// <summary>Sent at a target out of its side's sight, it flew to the last known position and found nothing there for 10 s (or lost it for 10 s on a strike).</summary>
    TargetLost,

    /// <summary>It cannot attack a target its side does not see (a strike drone: its missile needs a lock).</summary>
    NotVisible,

    /// <summary>
    /// Bingo fuel (E13, D-FUEL1): refused, its fuel will not take it to the target, through a pass and home with its
    /// reserves (the client: "Low fuel, returning"); or a strike ended because bingo sent it home.
    /// </summary>
    Bingo,

    /// <summary>A note, not a refusal: the order was taken, the target is out of its side's sight, it flies to the last known cell (D-ORD1).</summary>
    Unseen,

    /// <summary>The strike ended: its target is destroyed (or gone inside something).</summary>
    TargetDead,

    /// <summary>
    /// A note, not a refusal (E13, D-ORD2): the order was taken by an aircraft parked at home without the weapons it needs:
    /// it rearms and refuels at its station first (an implicit Rearm: still the player's command), then launches and attacks.
    /// </summary>
    Rearming,

    /// <summary>
    /// A cruise missile truck's route is refused: too many waypoints, a point off the map, or shorter or longer than its weapon's
    /// band (<see cref="World.CheckCruiseRoute(Unit, IReadOnlyList{Cell}?, Cell)"/> says which). Ground units too.
    /// </summary>
    RouteInvalid,

    /// <summary>A cruise missile truck's route is refused: a leg's terrain cannot be cleared at its climb and descent limits (<see cref="World.CheckCruiseRoute(Unit, IReadOnlyList{Cell}?, Cell)"/>: the leg).</summary>
    RouteTerrain,

    /// <summary>A Deploy order is refused: the ground under the launcher's footprint is steeper than its <see cref="Model.Archetype.DeploySlope"/>.</summary>
    Slope,

    /// <summary>
    /// A squad's Ambush order is refused: its kill zone (<see cref="Command.Cell"/> to <see cref="Command.Cell2"/>) is shorter or longer
    /// than the content allows (6 to 30 cells). <see cref="OrderRefusal.Aircraft"/> is the squad's leader, <see cref="OrderRefusal.Cell"/>
    /// the zone's start.
    /// </summary>
    ZoneLength,

    /// <summary>
    /// A cruise missile truck's route is refused: at a waypoint it bends more than its weapon's turn limit from the leg before it
    /// (<see cref="World.CheckCruiseRoute(Unit, IReadOnlyList{Cell}?, Cell)"/>: <see cref="CruiseRouteProblem.SharpTurn"/> and the
    /// waypoint).
    /// </summary>
    RouteTurn,
}

/// <summary>
/// An attack order an aircraft refused, ended or took with a note this tick (for the client's feedback: a voice line,
/// a toast, the card, the order log; E13, D-ORD1). Read after <see cref="World.Step"/>; no order is dropped without one.
/// </summary>
/// <param name="Aircraft">The aircraft.</param>
/// <param name="Player">Its player.</param>
/// <param name="Reason">Why.</param>
/// <param name="Target">The unit it was ordered at, or 0 for a point.</param>
/// <param name="Cell">The point it was ordered at (attack-ground), or the target's last known cell.</param>
public readonly record struct OrderRefusal(uint Aircraft, byte Player, OrderRefusalReason Reason, uint Target, Cell Cell);
