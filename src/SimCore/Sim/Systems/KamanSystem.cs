using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The Kaman system (docs/design.md, "Kaman drone"): a Kaman-22 type long-range attack drone. It is a flying unit, not a
// projectile, so that it is an air target like any aircraft (air defence and the jets engage its "kaman" class, team
// cueing and the radar track it, its wreck falls when it is shot down) and it spots for its side on its way (its vision,
// over ridges and woods like any air unit). A weapon that launches it (WeaponEffect.Launches: the Kaman truck's rail, the
// engineer's Kaman station) fires it like a one-way drone's rail, on an attack order at a unit or an attack-ground order
// at a point; the round leaves as this unit (LaunchKamans, after combat's fire) and takes no orders.
//
// In flight (FlyKaman, from the air movement system), each tick:
//  - the target: while its side sees the unit it was launched at (World.IsVisibleTo: the player's view, which under fog
//    of war is "spotted by his side"), its aim is that unit's position; once it does not (out of sight, inside something,
//    dead), it flies on to the last position seen (Lost) and takes the target up again if its side sees it again;
//  - the steering: the drone law of the scout drones (DroneControl, its flight's turn rate) toward the aim; level, the
//    scouts' height planner at its flight height over the ground (FlightHeight.cs), climbing off the rail at its climb rate;
//  - its missiles (its first weapon with ammunition): within the weapon's range of the aim (level distance) and within
//    the weapon's cone of its nose, one missile, then one a reload later: fire and forget at the target while seen, else
//    at the aim point;
//  - the dive: once the aim is as close as its height over the aim's ground needs at the dive slope, plus half the
//    pitch-over, it pitches over (its vertical speed changes by at most the dive's over PitchOverTicks a tick) onto the
//    straight line to the aim, steering on; within three steps the line is flown exactly;
//  - collisions along its step (sampled TerrainSteps times a cell): the ground in any phase, and in the dive the target
//    or, failing it, the first ground unit or structure top it reaches (the one-way drone's rule): its warhead goes off
//    there (Blast: a direct hit on that unit, the splash round the point, the runway effect).
// Events for renderers: World.KamanEvents (launch, missile, lost, reacquired, dive, blast), the tick's, in order.
public sealed partial class World
{
    // Scratch (not state): the rounds the launchers fired this tick, launched as units once combat's loop is over.
    private readonly List<(Unit Shooter, WeaponDef Weapon, uint Target, Vec Point)> _kamanLaunches = [];

    private readonly List<KamanEvent> _kamanEvents = [];

    /// <summary>
    /// What the Kaman drones did in the last tick (launched, fired a missile, lost or took up their target again, began
    /// the dive, went off), in the order it happened. Cleared at the start of the next tick's air movement.
    /// </summary>
    public IReadOnlyList<KamanEvent> KamanEvents => _kamanEvents;

    // LaunchKamans puts the Kaman drones launched this tick into the world: each at its launcher, off its rail at its flight's
    // launch height, facing its aim, sent at the unit fired at (while it lives) or the point.
    private void LaunchKamans()
    {
        foreach (var (s, w, target, point) in _kamanLaunches)
        {
            if (Player(s.Owner)?.Setup.Defaults[w.Effect.Launches] is not { } def || def.Archetype.Flight is not { } f)
            {
                continue; // its side has no such unit (content): nothing leaves the rail
            }
            var t = target != 0 ? Unit(target) : null;
            var aim = t is { Alive: true } ? t.Pos : point;
            Facing.TryFromDirection(aim - s.Pos, out var facing);
            var n = Add(def, s.Pos, facing);
            n.Source = s.Id;
            n.Altitude = Fixed.FromRaw(Math.Max(1, GroundRaw(s.Pos) + f.LaunchHeightRaw));
            n.OrbitAt = aim;
            n.Kaman = new KamanState { Target = t is { Alive: true } ? t.Id : 0, Aim = aim };
            _kamanEvents.Add(new KamanEvent(KamanEventKind.Launch, n.Id, n.Kaman.Target, aim, n.Altitude, 0, s.Id));
        }
        _kamanLaunches.Clear();
    }

    // FlyKaman moves Kaman drone u one tick (see the file's comment).
    private void FlyKaman(Unit u, ScoutFlight f, KamanSpec k)
    {
        if (u.Kaman is not { } s)
        {
            // Put down in the sandbox: at its height, sent at the point of its move order (or where it is).
            var at = u.Order.Kind is OrderKind.Move or OrderKind.Attack or OrderKind.AttackGround ? u.Order.Cell.Center() : u.Pos;
            s = u.Kaman = new KamanState { Aim = at };
            u.Altitude = Fixed.FromRaw(GroundRaw(u.Pos) + FlightHeight(f));
        }
        if (s.FireWait > 0)
        {
            s.FireWait--;
        }

        // The target: followed while its side sees it, else its last known position.
        if (s.Target != 0)
        {
            if (Unit(s.Target) is { Alive: true } t && IsVisibleTo(t, u.Owner))
            {
                s.Aim = t.Pos;
                if (s.Lost)
                {
                    s.Lost = false;
                    _kamanEvents.Add(new KamanEvent(KamanEventKind.Reacquired, u.Id, s.Target, s.Aim, u.Altitude, 0, 0));
                }
            }
            else if (!s.Lost)
            {
                s.Lost = true;
                _kamanEvents.Add(new KamanEvent(KamanEventKind.Lost, u.Id, s.Target, s.Aim, u.Altitude, 0, 0));
            }
        }

        var speed = u.Def.Speed.Raw;
        long vx = u.Vel.X.Raw, vy = u.Vel.Y.Raw;
        if (vx == 0 && vy == 0)
        {
            var d0 = Facing.Dir(u.Facing); // off its rail the way it faces
            (vx, vy) = (d0.X.Raw * speed / Fixed.OneRaw, d0.Y.Raw * speed / Fixed.OneRaw);
        }
        long ax = s.Aim.X.Raw - u.Pos.X.Raw, ay = s.Aim.Y.Raw - u.Pos.Y.Raw;
        var rem = Len3(ax, ay, 0); // the level distance to its aim
        var a0 = u.Altitude.Raw;
        var aimGround = GroundRaw(s.Aim);

        // Its missiles: within range and its nose's cone.
        FireKamanMissile(u, s, rem, ax, ay, vx, vy);

        // The dive starts once the aim is as close as its height needs at the dive slope, plus half the pitch-over.
        if (s.Phase != KamanPhase.Dive && rem <= Math.Max(0, a0 - aimGround) * 1000 / k.DivePermille + speed * k.PitchOverTicks / 2)
        {
            s.Phase = KamanPhase.Dive;
            _kamanEvents.Add(new KamanEvent(KamanEventKind.Dive, u.Id, s.Target, s.Aim, u.Altitude, 0, 0));
        }

        var (ox, oy) = (vx, vy);
        var from = u.Pos;
        Vec pos;
        if (s.Phase == KamanPhase.Dive && rem <= speed)
        {
            // The last step: onto the aim.
            u.Vel = P(ax, ay);
            pos = s.Aim;
            u.Pos = pos;
        }
        else
        {
            (vx, vy) = DroneControl(u, f, s.Aim, vx, vy, speed);
            pos = FlyStep(u, vx, vy);
        }

        if (s.Phase == KamanPhase.Dive)
        {
            // Onto the straight line to the aim: the vertical speed that ends on the aim's ground when the level distance
            // runs out, reached at the pitch-over's rate (exactly within the last three steps).
            var left = Math.Max(rem, 1);
            var want = -(a0 - aimGround) * Math.Min(speed, left) / left;
            var vz = want;
            if (rem > 3 * speed)
            {
                var acc = Math.Max(1, speed * k.DivePermille / 1000 / k.PitchOverTicks);
                vz = Math.Clamp(want, u.VelZ.Raw - acc, u.VelZ.Raw + acc);
            }
            u.VelZ = Fixed.FromRaw(vz);
            u.Altitude = Fixed.FromRaw(a0 + vz);
        }
        else
        {
            // Level flight: the scouts' height planner at its flight height, climbing off the rail at its climb rate.
            var under = GroundRaw(pos);
            var height = FlightHeight(f);
            var want = PlanHeight(pos, ox, oy, vx, vy, f.ClimbPerTick, ScoutLookAhead, height, ScoutRoofClearRaw, height, ScoutRoofClearRaw).Want;
            var down = Math.Max(1, f.ClimbPerTick / 2);
            VerticalStep(u, want, f.ClimbPerTick, down, Math.Max(1, f.ClimbPerTick / TicksPerSecond), under + ScoutFloorRaw, true);
            if (s.Phase == KamanPhase.Climb && u.VelZ.Raw <= 0 && u.Altitude.Raw >= want - HoldBandRaw)
            {
                s.Phase = KamanPhase.Cruise; // levelled off at its height
            }
        }

        // Collisions along the step: the ground in any phase; in the dive its target, else the first unit or roof it reaches.
        var a1 = u.Altitude.Raw;
        var step = pos - from;
        var len = step.Len().Raw;
        var samples = (int)Math.Max(1, (len * TerrainSteps + Fixed.OneRaw - 1) / Fixed.OneRaw);
        for (var i = 1; i <= samples; i++)
        {
            var q = from + new Vec(Fixed.FromRaw(step.X.Raw * i / samples), Fixed.FromRaw(step.Y.Raw * i / samples));
            var aq = a0 + (a1 - a0) * i / samples;
            if (s.Phase == KamanPhase.Dive && KamanHit(u, s, q, aq) is var hit and not 0)
            {
                KamanBlast(u, s, k, q, aq, hit);
                return;
            }
            if (aq <= GroundRaw(q))
            {
                KamanBlast(u, s, k, q, GroundRaw(q), 0);
                return;
            }
        }
    }

    // FireKamanMissile fires Kaman drone u's next missile when it is due: its first weapon with a round left, the aim within
    // the weapon's range (level distance rem; ax, ay toward it) and its cone off the nose (velocity vx, vy).
    private void FireKamanMissile(Unit u, KamanState s, long rem, long ax, long ay, long vx, long vy)
    {
        if (s.FireWait > 0)
        {
            return;
        }
        var weapons = u.Def.Weapons;
        WeaponDef? w = null;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Effect.Guided is not null && HasAmmo(u, weapons[i]) && weapons[i].Stock >= 0)
            {
                w = weapons[i];
                break;
            }
        }
        if (w is null || rem > w.Range.Raw)
        {
            return;
        }
        var cone = w.Effect.Cone;
        if (cone > 0 && rem > 0)
        {
            var vl = Math.Max(1, Len3(vx, vy, 0));
            if ((vx * ax + vy * ay) * 1000 / (vl * rem) < CosDeg(cone))
            {
                return; // not yet within its nose's cone
            }
        }
        var t = !s.Lost && s.Target != 0 && Unit(s.Target) is { Alive: true } tt ? tt : null;
        _nextId++;
        var shot = new Projectile(_nextId, u, w, t?.Pos ?? s.Aim, t?.Id ?? 0);
        Launch(shot, u);
        if (Ground is not null)
        {
            (shot.Origin, shot.LaunchHeight) = (u.Pos, u.Altitude); // from its height (the line its crest check follows)
        }
        shot.Altitude = u.Altitude;
        _projectiles.Add(shot);
        u.MutableCharges[w.Stock]--;
        u.LastShot = Tick;
        s.Fired++;
        s.FireWait = w.Reload;
        _kamanEvents.Add(new KamanEvent(KamanEventKind.Missile, u.Id, t?.Id ?? 0, shot.Dest, u.Altitude, shot.Id, 0));
    }

    // KamanHit is what diving Kaman drone u at q (height aq) goes off on: its target when it reaches it there, else the first
    // ground unit or structure (lowest ID) whose top it is under there (DiveHit, the one-way drone's rule); 0 for none.
    private uint KamanHit(Unit u, KamanState s, Vec q, long aq)
    {
        if (s.Target != 0 && Unit(s.Target) is { Alive: true, Inside: 0, Garrison: 0 } t && aq <= GroundRaw(q) + t.Def.Archetype.HeightRaw)
        {
            if (t.Def.IsStructure)
            {
                var o = FootprintOrigin(t);
                if (q.X.Raw >= (long)o.X * Fixed.OneRaw && q.X.Raw < (long)(o.X + FootW(t)) * Fixed.OneRaw &&
                    q.Y.Raw >= (long)o.Y * Fixed.OneRaw && q.Y.Raw < (long)(o.Y + FootH(t)) * Fixed.OneRaw)
                {
                    return t.Id;
                }
            }
            else if (Within(t.Pos, q, t.Def.Archetype.Radius))
            {
                return t.Id;
            }
        }
        return DiveHit(u.Id, q, aq);
    }

    // KamanBlast sets Kaman drone u's warhead off at `at` (height alt) on unit hit (0: the ground): a direct hit on that unit,
    // the splash round the point (friends too), the runway effect. The drone is spent (not an air loss).
    private void KamanBlast(Unit u, KamanState s, KamanSpec k, Vec at, long alt, uint hit)
    {
        u.Pos = at;
        u.Altitude = Fixed.FromRaw(alt);
        u.HP = 0;
        s.Detonated = true;
        var wh = Content.Warheads[k.Warhead];
        _hurtBy = u; // for Return fire and retaliation (Hurt)
        if (Unit(hit) is { } t && wh.CanHit(ArmorOf(t)))
        {
            Hurt(t, wh, k.Damage, 100);
        }
        if (k.Splash.Raw > 0)
        {
            Explode(at, k.Damage, wh, k.Splash, hit, k.SplashCenter, k.SplashEdge);
        }
        _hurtBy = null;
        RunwayBlast(at, k.Runway);
        _kamanEvents.Add(new KamanEvent(KamanEventKind.Blast, u.Id, s.Target, at, u.Altitude, 0, hit, WaterAt(at, hit)));
    }
}

/// <summary>Where a Kaman drone is in its flight.</summary>
public enum KamanPhase : byte
{
    /// <summary>Off its rail, climbing to its flight height.</summary>
    Climb,

    /// <summary>Level at its flight height, following its target.</summary>
    Cruise,

    /// <summary>Pitched over into the dive that ends in its target.</summary>
    Dive,
}

/// <summary>
/// A Kaman drone's attack (<see cref="Unit.Kaman"/>): what it was launched at, where it flies, its phase and missiles.
/// Hashed while present.
/// </summary>
public sealed class KamanState
{
    /// <summary>The unit it was launched at (it follows it), or 0 for a point on the ground.</summary>
    public uint Target { get; internal set; }

    /// <summary>Where it flies and dives: its target's position while its side sees it, else where it was last seen (or the point).</summary>
    public Vec Aim { get; internal set; }

    /// <summary>Whether its target is lost (its side does not see it, or it is gone): it flies to <see cref="Aim"/>, the last known position.</summary>
    public bool Lost { get; internal set; }

    /// <summary>Its flight phase.</summary>
    public KamanPhase Phase { get; internal set; }

    /// <summary>How many missiles it has fired.</summary>
    public int Fired { get; internal set; }

    /// <summary>Ticks until it may fire its next missile.</summary>
    public int FireWait { get; internal set; }

    /// <summary>Whether its warhead went off (it is spent: not an air loss).</summary>
    public bool Detonated { get; internal set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put(0x6A3A);
        h.Put(Target);
        h.PutSigned(Aim.X.Raw);
        h.PutSigned(Aim.Y.Raw);
        h.Put(Lost ? 1UL : 0);
        h.PutByte((byte)Phase);
        h.PutSigned(Fired);
        h.PutSigned(FireWait);
        h.Put(Detonated ? 1UL : 0);
    }
}

/// <summary>What a Kaman drone did (<see cref="World.KamanEvents"/>).</summary>
public enum KamanEventKind : byte
{
    /// <summary>It left its launcher's rail (<see cref="KamanEvent.Other"/>: the launcher).</summary>
    Launch,

    /// <summary>It fired a missile (<see cref="KamanEvent.Missile"/>: the projectile; <see cref="KamanEvent.At"/>: where it is sent).</summary>
    Missile,

    /// <summary>Its side stopped seeing its target: it flies on to <see cref="KamanEvent.At"/>, the last known position.</summary>
    Lost,

    /// <summary>Its side sees its target again: it follows it again.</summary>
    Reacquired,

    /// <summary>It pitched over into its dive.</summary>
    Dive,

    /// <summary>Its warhead went off at <see cref="KamanEvent.At"/> (<see cref="KamanEvent.Other"/>: the unit it hit, 0 the ground).</summary>
    Blast,
}

/// <summary>
/// One thing a Kaman drone did in the last tick.
/// </summary>
/// <param name="Kind">What it did.</param>
/// <param name="Drone">The drone's unit ID (gone after a blast).</param>
/// <param name="Target">The unit it was launched at, or 0 for a point (for a missile: the missile's target, 0 for its aim point).</param>
/// <param name="At">Its aim (a launch, lost, reacquired, dive), the missile's aim point, or the blast's point.</param>
/// <param name="Altitude">Its height (absolute) then.</param>
/// <param name="Missile">A missile's projectile ID, else 0.</param>
/// <param name="Other">The launcher (a launch) or the unit hit (a blast), else 0.</param>
/// <param name="Water">A blast in open water (a river, lake or sea): a water column, no crater.</param>
public readonly record struct KamanEvent(KamanEventKind Kind, uint Drone, uint Target, Vec At, Fixed Altitude, uint Missile, uint Other, bool Water = false);
