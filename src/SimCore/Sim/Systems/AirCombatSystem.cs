using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// Air-to-air (E6; the plan's air-combat model, sections 2.8 to 2.11): aircraft as shooters, run by the
// air defence system (AirDefenseSystem.cs) after combat and before the projectiles, so a missile fired
// this tick flies this tick. All integer; rolls through Rand.Hash (no generator state); units by ID.
//
//  - Who engages: an aircraft flying free (not at its airfield, not homing) with air weapons (an
//    intercept missile, an airGun). On Guard with Aircraft or Drones in its filter it picks targets
//    itself (the owner's rule: Guard is the only automatic mode): the nearest enemy air target of those
//    kinds inside the area plus its leash that it detects (its own radar, airVision from its height,
//    scaled by the target's signature) or its side tracks (team cueing), keeping its target while it
//    may. On an attack order at an air unit (a Strike) it engages only that one. Otherwise never.
//  - Steering: it flies a lead pursuit at its target (the mission system's SteerAim) at the target's
//    height (never below its floor), and closing in for the gun it slows to the target's speed plus a
//    margin, so passes, overshoots and re-attacks (dogfights) come out of the flight model.
//  - The gun (airGun): within the weapon's range (slant) and its cone of the shooter's velocity, no lock:
//    one burst (a round, the reload), hit chance = hitBase(class) x crossing term (the line of sight's turn
//    rate relative to the shooter) x range term x signature kill; a hit deals the weapon's damage through
//    its warhead (a drone dies to a burst, a jet takes several). Never decoyed.
//  - Missiles (intercept): tracking counts while the target is within the widest launch cone (boresight)
//    of the missiles it could use; it fires the shortest-ranged missile whose range covers the target
//    once locked (lockTicks x signature lock), within that missile's cone, loaded, and no friendly
//    missile already flies at the target. The missile is the air defence's: lead pursuit, 3D fuse, the kill
//    model x signature kill x the countermeasures' percent.
//  - Self-protection (every mode, every flying aircraft, AirSelfProtect): a hostile guided missile homing on
//    it with a seeker its dispensers decoy, within a dispenser's window of time to go: one salvo against it
//    (at most perMissile), that missile's kill chance x the dispenser's pk. A fixed wing with an evade block
//    breaks within its warn ticks: a max-g turn to the beam, a descent to its floor, the afterburner.
//  - Kill attribution: Unit.KilledBy (Gun, Missile, Fuel, else Blast) and World.AirLosses.
public sealed partial class World
{
    // A missile-free lead pursuit leads its target by the time to close at its own speed, at most this.
    private const int PursuitLeadTicks = 20;

    // Closing in for the gun (within this many times its range, the target ahead) it flies at the target's
    // speed plus this (raw a second: 3 cells/s).
    private const int GunApproachRanges = 4;
    private const long GunClosingRaw = 3 * Fixed.OneRaw;

    // Salt for the gun's roll, so it never matches a missile's roll on the same pair and tick.
    private const ulong GunRollSalt = 0x6A11_0000_0000UL;

    // cos(d) in thousandths for whole degrees 0 to 90 (launch cones and the gun's cone).
    private static readonly int[] CosTable =
    [
        1000, 1000, 999, 999, 998, 996, 995, 993, 990, 988, 985, 982, 978, 974, 970, 966, 961, 956, 951, 946, 940, 934, 927, 921,
        914, 906, 899, 891, 883, 875, 866, 857, 848, 839, 829, 819, 809, 799, 788, 777, 766, 755, 743, 731, 719, 707, 695, 682,
        669, 656, 643, 629, 616, 602, 588, 574, 559, 545, 530, 515, 500, 485, 469, 454, 438, 423, 407, 391, 375, 358, 342, 326,
        309, 292, 276, 259, 242, 225, 208, 191, 174, 156, 139, 122, 105, 87, 70, 52, 35, 17, 0,
    ];

    // Scratch, rebuilt each tick air defence runs (not state): the Air-layer units without a flight that are
    // air targets (the FPV drone), in ID order.
    private readonly List<AirBody> _airUnits = [];

    private readonly List<GunBurst> _gunBursts = [];
    private readonly List<CountermeasureSalvo> _salvoEvents = [];
    private readonly List<AirLaunch> _airLaunches = [];
    private readonly List<AirLoss> _airLosses = [];

    /// <summary>The gun bursts aircraft fired at air targets in the last tick, in shooter ID order (E6).</summary>
    public IReadOnlyList<GunBurst> GunBursts => _gunBursts;

    /// <summary>The countermeasure salvos (flares, chaff) dispensed in the last tick, in missile ID order (E6).</summary>
    public IReadOnlyList<CountermeasureSalvo> Countermeasures => _salvoEvents;

    /// <summary>The missiles aircraft launched at air targets in the last tick, in shooter ID order (E6).</summary>
    public IReadOnlyList<AirLaunch> AirLaunches => _airLaunches;

    /// <summary>The aircraft lost in the last tick, in ID order, with what brought each down (E6: KilledBy).</summary>
    public IReadOnlyList<AirLoss> AirLosses => _airLosses;

    // ClearAirCombatEvents starts a tick's air combat reports (the air movement system calls it first).
    private void ClearAirCombatEvents()
    {
        _gunBursts.Clear();
        _salvoEvents.Clear();
        _airLaunches.Clear();
        _airLosses.Clear();
    }

    /// <summary>Whether units of d carry an air-to-air weapon (an intercept missile or an airGun).</summary>
    internal static bool HasAirWeapons(UnitDef d)
    {
        var weapons = d.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Effect.Interceptor is not null || weapons[i].Effect.AirGun is not null)
            {
                return true;
            }
        }
        return false;
    }

    // CosDeg is cos(deg) in thousandths (a cone of 180 degrees or more: everything is inside).
    private static int CosDeg(int deg) => deg <= 0 ? 1000 : deg <= 90 ? CosTable[deg] : deg < 180 ? -CosTable[180 - deg] : -1001;

    // ShooterVel is aircraft u's last step (raw a tick, x y z).
    private static (long X, long Y, long Z) ShooterVel(Unit u) => (u.Vel.X.Raw, u.Vel.Y.Raw, u.VelZ.Raw);

    // BoreCos is the cosine (thousandths) of the angle between aircraft u's velocity and its line of sight
    // to air target t from its eye.
    private static long BoreCos(Unit u, long eye, AirBody t)
    {
        long vx = u.Vel.X.Raw, vy = u.Vel.Y.Raw, vz = u.VelZ.Raw;
        long rx = t.Pos.X.Raw - u.Pos.X.Raw, ry = t.Pos.Y.Raw - u.Pos.Y.Raw, rz = t.Alt - eye;
        var vl = Len3(vx, vy, vz);
        var rl = Len3(rx, ry, rz);
        if (rl == 0)
        {
            return 1000;
        }
        return vl == 0 ? -1000 : (vx * rx + vy * ry + vz * rz) * 1000 / (vl * rl);
    }

    // AirShooter runs aircraft u's air-to-air for the tick: its target (Guard's pick or its ordered air
    // target), the gun, the lock and the missile launch.
    private void AirShooter(Unit u)
    {
        if (StrikeWeapon(u) is null && u.Reload > 0)
        {
            u.Reload = Math.Max(0, u.Reload - 100); // a strike drone's reload counts in the watcher system
        }
        if (!u.Alive || !u.CanAct || u.AirOps is not null || u.Homing || u.Altitude.Raw == 0)
        {
            DropTrack(u); // at its airfield, on the way home or not yet flying: no engagement
            return;
        }
        var eye = u.Altitude.Raw;
        if (AirTargetOf(u, eye) is not { } pick)
        {
            DropTrack(u);
            return;
        }
        var (t, seen) = pick;
        if (t.Id != u.AirTarget)
        {
            (u.AirTarget, u.TrackTicks) = (t.Id, 0); // a new target: the lock starts over
        }
        _tracked.Add((u.Owner, t.Id));
        if (!seen)
        {
            return; // an ordered target nobody on its side sees: it flies at it, no lock, no shot
        }
        var slant = Slant(u.Pos, eye, t);
        var cos = BoreCos(u, eye, t);
        if (u.Reload == 0 && GunOn(u, t) is { } gun && slant <= gun.Range.Raw && cos >= CosDeg(gun.Effect.AirGun!.Cone))
        {
            FireGun(u, gun, t, eye, slant);
            return;
        }
        WeaponDef? missile = null;
        var widest = -1;
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if (w.Effect.Interceptor is not { } ic || !ic.Engages(t.Class) || !HasAmmo(u, w))
            {
                continue;
            }
            var cone = ic.Boresight == 0 ? 180 : ic.Boresight;
            widest = Math.Max(widest, cone);
            if (slant <= w.Range.Raw && slant >= w.Effect.MinRange.Raw && (missile is null || w.Range.Raw < missile.Range.Raw))
            {
                missile = w; // the shortest-ranged missile that reaches it (the IR missile close in, the radar one far out)
            }
        }
        if (widest >= 0 && cos >= CosDeg(widest))
        {
            u.TrackTicks = Math.Min(u.TrackTicks + 1, TrackCap);
        }
        if (missile is null || u.Reload > 0 || InFlightAt(u.Owner, t.Id))
        {
            return;
        }
        var mic = missile.Effect.Interceptor!;
        if (cos < CosDeg(mic.Boresight == 0 ? 180 : mic.Boresight) || u.TrackTicks < LockTicksOn(mic, t))
        {
            return;
        }
        LaunchInterceptor(u, missile, t, eye, slant);
        _airLaunches.Add(new AirLaunch(_nextId, u.Id, t.Id, mic.Seeker));
    }

    // AirTargetOf is the air target aircraft u engages now and whether its side sees it: on an attack order
    // at an air unit (a Strike) that unit; on Guard hunting aircraft or drones its pick; else none.
    private (AirBody Body, bool Seen)? AirTargetOf(Unit u, long eye)
    {
        if (u.Order.Kind == OrderKind.Attack)
        {
            if (Unit(u.Order.Target) is { } ot && AirBodyOfUnit(ot) is { Gone: false } ob && ob.Owner != u.Owner && CanEngageAir(u, ob))
            {
                return (ob, Sees(u, eye, ob, Slant2(u.Pos, eye, ob)));
            }
            return null; // an attack on the ground is not air-to-air (E7)
        }
        if (u.AirMission is not { Kind: AirTaskKind.Guard } m || (m.Filter & (AirTargetKinds.Aircraft | AirTargetKinds.Drones)) == 0)
        {
            return null;
        }
        var f = u.Def.Archetype.Flight!;
        _huntClasses = EngageClasses(u);
        if (_huntClasses == 0)
        {
            return null; // nothing left to engage aircraft or drones with
        }
        var scan = (Tick + u.Id) % HuntScanTicks == 0;
        if (!scan)
        {
            // Between its scans (twice a second, staggered by ID, as Guard's ground hunt) it keeps its
            // target while it may, and looks for none.
            return u.AirTarget != 0 && AirBodyById(u.AirTarget) is { } kept && HuntValid(u, f, m, eye, kept, out _) ? (kept, true) : null;
        }
        var pick = new AirPick();
        foreach (var p in _projectiles)
        {
            if (p.Interceptible != InterceptClass.None && !p.Done)
            {
                HuntConsider(u, f, m, eye, new AirBody(p), ref pick);
            }
        }
        foreach (var t in _scouts)
        {
            HuntConsider(u, f, m, eye, new AirBody(t), ref pick);
        }
        foreach (var b in _airUnits)
        {
            HuntConsider(u, f, m, eye, b, ref pick);
        }
        if (u.AirTarget != 0 && AirBodyById(u.AirTarget) is { } cur && HuntValid(u, f, m, eye, cur, out _) &&
            (!InFlightAt(u.Owner, cur.Id) || pick.Free is null))
        {
            return (cur, true);
        }
        var best = pick.Free ?? pick.Any;
        return best is { } b0 ? (b0, true) : null;
    }

    // The air classes (bit 1 << class) the hunting aircraft's air weapons with ammunition engage (scratch
    // for one AirTargetOf).
    private int _huntClasses;

    // EngageClasses is the air classes (bit 1 << class) aircraft u's air weapons with ammunition engage.
    private static int EngageClasses(Unit u)
    {
        var bits = 0;
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if (!HasAmmo(u, w))
            {
                continue;
            }
            if (w.Effect.Interceptor is { } ic)
            {
                bits |= ic.Classes;
            }
            if (w.Effect.AirGun is { } g)
            {
                for (var k = 0; k < g.HitBase.Count; k++)
                {
                    bits |= g.HitBase[k].Chance > 0 ? 1 << (int)g.HitBase[k].Class : 0;
                }
            }
        }
        return bits & ~1; // never class None
    }

    // Sees: u's own radar (its air vision from its eye, x the target's signature detect) or its side's
    // sensors (team cueing) track air target b at squared slant distance n.
    private bool Sees(Unit u, long eye, AirBody b, long n) =>
        SlantWithin(n, DetectRange(u.Def.Archetype.AirVision.Raw, b)) || CuedFor(u.Owner, b.Id);

    // HuntValid: air target b is one Guard lets u engage: an enemy of a kind in the filter, one of its air
    // weapons can engage it (_huntClasses), inside the area plus the leash, and seen; n is its squared slant distance.
    private bool HuntValid(Unit u, ScoutFlight f, AirMission m, long eye, AirBody b, out long n)
    {
        n = 0;
        if (b.Gone || b.Owner == u.Owner || b.Owner == 0 || (_huntClasses & (1 << (int)b.Class)) == 0 || (AirKind(b) & m.Filter) == 0 ||
            !InGuardZone(u, f, m, b.Pos))
        {
            return false;
        }
        n = Slant2(u.Pos, eye, b);
        return Sees(u, eye, b, n);
    }

    private void HuntConsider(Unit u, ScoutFlight f, AirMission m, long eye, AirBody b, ref AirPick pick)
    {
        if (!HuntValid(u, f, m, eye, b, out var d))
        {
            return;
        }
        if (pick.Any is not { } a || d < pick.AnyD || (d == pick.AnyD && b.Id < a.Id))
        {
            (pick.Any, pick.AnyD) = (b, d);
        }
        if ((pick.Free is not { } fr || d < pick.FreeD || (d == pick.FreeD && b.Id < fr.Id)) &&
            !InFlightAt(u.Owner, b.Id) && !_tracked.Contains((u.Owner, b.Id))) // the list scans last: only for a nearer candidate
        {
            (pick.Free, pick.FreeD) = (b, d);
        }
    }

    // AirKind is the Guard filter kind of air target b: a one-way attack drone is a drone.
    private AirTargetKinds AirKind(AirBody b) => b.U is { } t ? KindOf(t) : AirTargetKinds.Drones;

    /// <summary>Whether one of aircraft u's air weapons with ammunition can engage air target b's class.</summary>
    private static bool CanEngageAir(Unit u, AirBody b)
    {
        var c = b.Class;
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if (((w.Effect.Interceptor is { } ic && ic.Engages(c)) || (w.Effect.AirGun is { } g && g.HitOn(c) > 0)) && HasAmmo(u, w))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether flying unit u may be ordered to attack unit t as an air target (a Strike at an aircraft or an FPV drone).</summary>
    internal bool CanAttackInAir(Unit u, Unit t) =>
        u.Def.Archetype.Flight is not null && t.Owner != u.Owner && AirBodyOfUnit(t) is { Gone: false } b && CanEngageAir(u, b);

    // GunOn is u's airGun weapon with ammunition that can hit air target t, or null.
    private static WeaponDef? GunOn(Unit u, AirBody t)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Effect.AirGun is { } g && g.HitOn(t.Class) > 0 && HasAmmo(u, weapons[i]))
            {
                return weapons[i];
            }
        }
        return null;
    }

    // FireGun fires one burst of u's gun w at air target t: one round, the reload, one roll.
    private void FireGun(Unit u, WeaponDef w, AirBody t, long eye, long slant)
    {
        var g = w.Effect.AirGun!;
        if (w.Stock >= 0)
        {
            u.MutableCharges[w.Stock]--;
        }
        u.Reload = w.Reload * 100;
        u.LastShot = Tick;
        var pk = g.HitOn(t.Class) * 10L;
        pk = pk * g.CrossHalf / (g.CrossHalf + LosRate(u.Pos, eye, t, ShooterVel(u)));
        var range = Math.Max(1, w.Range.Raw);
        pk = pk * Math.Max(0, 1000 - (1000 - 10L * g.RangeFar) * Math.Min(slant, range) / range) / 1000;
        if (t.Signature.Kill != 100)
        {
            pk = pk * t.Signature.Kill / 100; // a low signature: harder to hit
        }
        var roll = (int)(Rand.Hash(Match.Setup.Seed, Tick, u.Id, t.Id | GunRollSalt) % 1000);
        var hit = roll < pk;
        var killed = false;
        if (hit && t.P is { } drone)
        {
            drone.Done = true; // a one-way attack drone shot up: it breaks up in the air, no detonation
            killed = true;
        }
        else if (hit && t.U is { } v)
        {
            var dmg = w.Warhead.Damage(w.Damage, ArmorOf(v), 100);
            v.HP -= Math.Min(v.HP, dmg);
            if (!v.Alive)
            {
                (v.KilledBy, v.Killer) = (KillCause.Gun, u.Id); // the gun made the killing blow: the pilot ejects (E10)
                killed = true;
            }
        }
        _gunBursts.Add(new GunBurst(u.Id, t.Id, hit, killed, (int)pk, roll, u.Pos, u.Altitude, t.Pos, Fixed.FromRaw(t.Alt)));
    }

    // AirBodyOfUnit is unit t as an air target: an aircraft flying (not landed), or an Air-layer unit with an
    // airBody block (the FPV drone) outside; else null.
    internal AirBody? AirBodyOfUnit(Unit t)
    {
        if (t.Def.Archetype.Flight is not null)
        {
            return Grounded(t) ? null : new AirBody(t);
        }
        return t.Def.Archetype.AirBody is not null && t.Inside == 0 && t.Alive ? BodyOf(t) : null;
    }

    // BodyOf is an airBody unit (the FPV drone) as an air target: its height over the ground under it and
    // its next step (it flies straight at its goal: its step this tick stands for it).
    private AirBody BodyOf(Unit t)
    {
        var alt = GroundRaw(t.Pos) + t.Def.Archetype.AirBody!.HeightRaw;
        (long, long, long) step = (0, 0, 0);
        if (t.CanAct && AirGoal(t, out var goal))
        {
            var d = goal - t.Pos;
            var s = d.Len() <= t.Def.Speed ? d : d.WithLen(t.Def.Speed);
            step = (s.X.Raw, s.Y.Raw, 0);
        }
        return new AirBody(t, alt, step);
    }

    // CollectAirUnits gathers this tick's airBody units (the FPV drones), in ID order.
    private void CollectAirUnits()
    {
        _airUnits.Clear();
        foreach (var t in _units)
        {
            if (t.Def.Archetype.AirBody is not null && t.Alive && t.Inside == 0)
            {
                _airUnits.Add(BodyOf(t));
            }
        }
    }

    // AirEngagement is the air target aircraft u engages (its air-to-air track, still flying), or null.
    private AirBody? AirEngagement(Unit u) =>
        u.AirTarget != 0 && !u.Homing && HasAirWeapons(u.Def) && AirBodyById(u.AirTarget) is { Gone: false } b ? b : null;

    // PursuitPoint is where aircraft u steers to engage air target t: a lead pursuit (its position plus its
    // velocity times the time u needs to close at its own speed, at most PursuitLeadTicks).
    private Vec PursuitPoint(Unit u, AirBody t)
    {
        var (tx, ty, _) = AirVelocity(t);
        long rx = t.Pos.X.Raw - u.Pos.X.Raw, ry = t.Pos.Y.Raw - u.Pos.Y.Raw;
        var own = Math.Max(1, Len3(u.Vel.X.Raw, u.Vel.Y.Raw, 0));
        var lead = Math.Min(PursuitLeadTicks, Len3(rx, ry, 0) / own);
        return OnMap(t.Pos.X.Raw + tx * lead, t.Pos.Y.Raw + ty * lead);
    }

    // GunApproachSpeed is the speed (raw a second) fixed wing u wants closing in on air target t for its
    // gun: the target's speed plus GunClosingRaw once within GunApproachRanges of its gun's range with the
    // target ahead; else want unchanged.
    private long GunApproachSpeed(Unit u, AirBody t, long want, long vx, long vy)
    {
        if (GunOn(u, t) is not { } gun)
        {
            return want;
        }
        long rx = t.Pos.X.Raw - u.Pos.X.Raw, ry = t.Pos.Y.Raw - u.Pos.Y.Raw;
        if (rx * vx + ry * vy <= 0 || Len3(rx, ry, 0) > gun.Range.Raw * GunApproachRanges)
        {
            return want;
        }
        var (tx, ty, _) = AirVelocity(t);
        return Math.Min(want, Len3(tx, ty, 0) * TicksPerSecond + GunClosingRaw);
    }

    // AirSelfProtect runs every flying aircraft's automatic self-protection against the hostile guided
    // missiles homing on it (every mode; not targeting): a countermeasure salvo against a missile whose
    // seeker its dispenser decoys within the dispenser's window of time to go (at most perMissile a missile;
    // its kill chance x pk), and a fixed wing's break (evade) within its warn ticks. Missiles in ID order.
    private void AirSelfProtect()
    {
        foreach (var p in _projectiles)
        {
            if (p.Done || !p.Guided || p.Weapon.Effect.Interceptor is not { } ic || Unit(p.Target) is not { } t || !t.Alive ||
                t.Def.Archetype.Flight is null || Grounded(t))
            {
                continue;
            }
            var a = t.Def.Archetype;
            if (a.Countermeasures.Count == 0 && a.Evade is null)
            {
                continue;
            }
            var tgo = TimeToGo(p, new AirBody(t));
            if (ic.Seeker != Seeker.None)
            {
                for (var i = 0; i < a.Countermeasures.Count; i++)
                {
                    var cm = a.Countermeasures[i];
                    if (cm.Vs == ic.Seeker && tgo <= cm.Window && p.Decoys < cm.PerMissile && t.Salvos[i] > 0)
                    {
                        t.MutableSalvos[i]--;
                        p.Decoys++;
                        p.DecoyPercent = p.DecoyPercent * cm.Pk / 100;
                        _salvoEvents.Add(new CountermeasureSalvo(t.Id, cm.Kind, p.Id, t.Salvos[i]));
                    }
                }
            }
            if (a.Evade is { } ev && t.Wing is { } st && t.AirOps is null && tgo <= ev.Warn && st.Evade == 0 && st.EvadeFrom != p.Id)
            {
                (st.Evade, st.EvadeFrom) = (ev.MaxTicks, p.Id); // break (FlyWing flies it)
                st.Boost = Math.Max(st.Boost, ev.MaxTicks);
            }
        }
    }

    // TimeToGo is guided missile p's time to reach air target t (ticks): the range over the closing speed
    // (its speed less the target's speed away along the line of sight); long.MaxValue when not closing.
    private long TimeToGo(Projectile p, AirBody t)
    {
        long rx = t.Pos.X.Raw - p.Pos.X.Raw, ry = t.Pos.Y.Raw - p.Pos.Y.Raw, rz = t.Alt - p.Altitude.Raw;
        var rl = Len3(rx, ry, rz);
        if (rl == 0)
        {
            return 0;
        }
        var (tx, ty, tz) = AirVelocity(t);
        var closing = p.Weapon.Effect.ProjectileSpeed.Raw - (rx * tx + ry * ty + rz * tz) / rl;
        return closing <= 0 ? long.MaxValue : rl / closing;
    }

    // RecordAirLosses reports the aircraft that died this tick (World.AirLosses) before they are removed:
    // what brought each down (KilledBy: a gun, a missile, fuel; any other damage is a blast).
    private void RecordAirLosses()
    {
        foreach (var u in _units)
        {
            if (u.HP > 0 || u.Def.Archetype.Flight is null || u.Recovered)
            {
                continue;
            }
            if (u.KilledBy == KillCause.None)
            {
                u.KilledBy = KillCause.Blast;
            }
            _airLosses.Add(new AirLoss(u.Id, u.Owner, u.Def.Archetype.Index, u.KilledBy, u.Killer, u.Pos, u.Altitude, Grounded(u)));
        }
    }
}
