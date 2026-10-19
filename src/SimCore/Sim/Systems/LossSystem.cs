using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// Losses (E10; the plan's section 5): what an aircraft with a loss block leaves when it is lost in the air.
// All integer; rolls through Rand.Hash (no generator state); wrecks and chutes in ID order.
//
//  - The rule (5.1): Unit.KilledBy decides. A gun kill: the pilot ejects (always: zero-zero seats) and the
//    empty aircraft falls as a wreck. Anything else (a missile, a blast, fuel): the wreck falls, nobody
//    ejects. Destroyed on the ground (an airfield's stand or runway, Grounded): neither (the existing wreck
//    rules). Recovered (landed at its station): not a loss at all.
//  - The wreck (5.2, World.Wrecks, hashed): created at the cleanup of the tick it died, with the aircraft's ID,
//    position, height and exact velocity; from the next tick it falls under its crash gravity, losing its
//    drag percent of horizontal speed a second, never steering. A missile kill breaks the airframe up with
//    the crash block's breakUp chance (Rand.Hash of the seed, the tick, the aircraft and a salt): it then
//    loses breakUpDrag a second instead (a steeper fall). It goes off where it meets the ground (interpolated
//    within the tick) or a ground unit's top on the way down (a roof, a vehicle, a soldier: the drone dive's
//    rule): a direct hit on that unit and the splash (the crash warhead; decision D9b: a small blast), the
//    runway effect on a runway, World.WreckImpacts.
//  - The ejection (5.3, World.Chutes, hashed): on a gun kill, at the same cleanup, the pilot leaves at the
//    aircraft's position and height: the seat throws him up seatRise over riseTicks, then he comes down at the
//    chute rate (the drogue, then the main chute at chuteTicks: an event), drifting along the aircraft's last
//    heading at drift a tick for driftTicks (no map wind yet). Not a unit while he comes down: nothing can
//    target or hurt him (decision D8), he does nothing. At the ground under him he lands: on the nearest cell a
//    soldier can stand on (routing's passable cell for his class: not inside a structure, not on blocked
//    terrain), as an idle unit of his archetype of the aircraft's owner (World.EjectionEvents: Landed with
//    the unit's ID). Nowhere to stand near (or his side has no definition of the pilot): lost.
//  - Events for renderers (cleared at the start of the next tick, like World.AirLosses): World.EjectionEvents
//    (Canopy, Seat, SeatAway, ChuteOpen, Landed, Lost), World.WreckImpacts. The live World.Wrecks and
//    World.Chutes give every tick's position, height and velocity.
public sealed partial class World
{
    // A wreck or a chute that has not come down after this long is put down where it is (never happens with a
    // positive gravity or chute rate on a real map; a guard against content mistakes).
    private const int MaxFallTicks2 = 120 * TicksPerSecond;

    // Salt for the break-up roll, so it never matches another roll on the same aircraft and tick.
    private const ulong BreakUpSalt = 0xB4EA_0000_0000UL;

    // How far (cells) a landing pilot looks for a cell to stand on.
    private const int LandingSearch = 16;

    private readonly List<Wreck> _wrecks = []; // sorted by ID, ascending
    private readonly List<Chute> _chutes = []; // sorted by ID, ascending
    private readonly List<WreckImpact> _wreckImpacts = [];
    private readonly List<EjectionEvent> _ejectionEvents = [];

    /// <summary>The wrecks falling on their crash paths, by ID (the lost aircraft's), ascending (E10).</summary>
    public IReadOnlyList<Wreck> Wrecks => _wrecks;

    /// <summary>The ejected pilots on their way down, by ID, ascending (E10): not units until they land.</summary>
    public IReadOnlyList<Chute> Chutes => _chutes;

    /// <summary>The wrecks that went off in the last tick, in wreck ID order (E10).</summary>
    public IReadOnlyList<WreckImpact> WreckImpacts => _wreckImpacts;

    /// <summary>The ejection steps of the last tick (E10): per chute in ID order, the ejections of the tick's losses last.</summary>
    public IReadOnlyList<EjectionEvent> EjectionEvents => _ejectionEvents;

    /// <summary>
    /// Whether player sees the point p on the map this tick (fog of war: his fog grid shows its cell; always
    /// without fog). Wrecks and chutes are seen like ground units: where the player's side sees the ground.
    /// </summary>
    public bool SeesPoint(byte player, Vec p) =>
        FogOf(player) is not { } fog || fog.Visible((int)(p.X.Raw / Fixed.OneRaw), (int)(p.Y.Raw / Fixed.OneRaw));

    // ClearLossEvents starts a tick's loss reports (with the air combat reports).
    private void ClearLossEvents()
    {
        _wreckImpacts.Clear();
        _ejectionEvents.Clear();
    }

    // StartLoss turns aircraft u, lost in the air this tick (after RecordAirLosses set its KilledBy), into its
    // wreck and, on a gun kill, its pilot's chute.
    private void StartLoss(Unit u)
    {
        if (u.Def.Archetype.Loss is not { } loss)
        {
            return;
        }
        var (vx, vy) = u.Wing is { } st ? (st.VelX, st.VelY) : (u.Vel.X.Raw * TicksPerSecond, u.Vel.Y.Raw * TicksPerSecond);
        var vz = u.VelZ.Raw * TicksPerSecond;
        if (loss.Crash is { } cr)
        {
            var broken = u.KilledBy == KillCause.Missile && cr.BreakUp > 0 &&
                (int)(Rand.Hash(Match.Setup.Seed, Tick, u.Id, BreakUpSalt) % 100) < cr.BreakUp;
            _wrecks.Add(new Wreck(u.Id, u.Owner, u.Def.Archetype.Index, u.Def.Index, u.KilledBy, u.Killer, broken)
            {
                Pos = u.Pos, Altitude = u.Altitude, VelX = vx, VelY = vy, VelZ = vz,
            });
        }
        if (u.KilledBy == KillCause.Gun && loss.Eject is { } ej)
        {
            Vec drift = default;
            var heading = new Vec(Fixed.FromRaw(vx), Fixed.FromRaw(vy));
            if (ej.Drift.Raw > 0)
            {
                drift = heading.LenSq() > 0 ? heading.WithLen(ej.Drift) : Facing.Dir(u.Facing).WithLen(ej.Drift);
            }
            _nextId++;
            var c = new Chute(_nextId, u.Id, u.Def.Archetype.Index, u.Owner, ej.Unit, u.Pos, u.Altitude, drift);
            _chutes.Add(c);
            _ejectionEvents.Add(new EjectionEvent(c.Id, u.Id, u.Owner, EjectionEventKind.Canopy, c.Pos, c.Altitude, 0));
            _ejectionEvents.Add(new EjectionEvent(c.Id, u.Id, u.Owner, EjectionEventKind.Seat, c.Pos, c.Altitude, 0));
        }
    }

    // RunLosses moves the wrecks and the chutes one tick (after the projectiles): wrecks that meet the ground go
    // off, pilots that reach it land.
    private void RunLosses()
    {
        if (_wrecks.Count > 0)
        {
            foreach (var k in _wrecks)
            {
                StepWreck(k);
            }
            _wrecks.RemoveAll(k => k.Age < 0);
        }
        if (_chutes.Count > 0)
        {
            foreach (var c in _chutes)
            {
                StepChute(c);
            }
            _chutes.RemoveAll(c => c.Age < 0);
        }
    }

    // StepWreck moves wreck k one tick: drag off its horizontal speed, gravity off its vertical speed, a twentieth
    // of its velocity (remainders carried); it goes off at the ground or the first ground unit's top it reaches.
    private void StepWreck(Wreck k)
    {
        var cr = Content.Archetypes[k.Archetype].Loss!.Crash!;
        var drag = k.BrokenUp ? cr.BreakUpDrag : cr.Drag;
        var a = new BombKin { X = k.Pos.X.Raw, Y = k.Pos.Y.Raw, Z = k.Altitude.Raw, VX = k.VelX, VY = k.VelY, VZ = k.VelZ, RX = k.RemX, RY = k.RemY, RZ = k.RemZ };
        var b = a;
        if (drag > 0)
        {
            b.VX -= b.VX * drag / (100 * TicksPerSecond);
            b.VY -= b.VY * drag / (100 * TicksPerSecond);
        }
        b.Step(cr.GravityRaw / TicksPerSecond);
        k.Age++;
        var q = OnMap(b.X, b.Y);
        (k.VelX, k.VelY, k.VelZ) = (b.VX, b.VY, b.VZ);
        if (b.Z <= GroundRaw(q) || k.Age >= MaxFallTicks2)
        {
            var at = GroundPoint(a, b, out var ground);
            WreckBlast(k, cr, at, ground, DiveHit(k.Id, at, ground));
            return;
        }
        if (DiveHit(k.Id, q, b.Z) is var hit and not 0)
        {
            WreckBlast(k, cr, q, b.Z, hit); // a roof, a vehicle or a soldier on the way down
            return;
        }
        k.Pos = q;
        k.Altitude = Fixed.FromRaw(b.Z);
        (k.RemX, k.RemY, k.RemZ) = (b.RX, b.RY, b.RZ);
    }

    // WreckBlast sets wreck k off at `at` (height alt) on unit hit (0: the ground): the direct hit, the splash,
    // the runway effect, the report. Nobody is blamed (no return fire at a wreck's owner).
    private void WreckBlast(Wreck k, CrashSpec cr, Vec at, long alt, uint hit)
    {
        k.Pos = at;
        k.Altitude = Fixed.FromRaw(alt);
        k.Age = -1; // gone at the end of this step
        var wh = Content.Warheads[cr.Warhead];
        _hurtBy = null;
        var t = Unit(hit);
        if (t is not null && wh.CanHit(ArmorOf(t)))
        {
            Hurt(t, wh, cr.Damage, 100);
        }
        if (cr.Splash.Raw > 0)
        {
            Explode(at, cr.Damage, wh, cr.Splash, hit);
        }
        var crater = CraterKind.None;
        if (cr.Runway is { } re)
        {
            foreach (var af in _airfields)
            {
                if (OnRunwayStrip(af, at, out _, out _))
                {
                    crater = re.Crater;
                    break;
                }
            }
            RunwayBlast(at, re); // craters and wear (AirfieldDamageSystem.cs)
        }
        var h = Len3(k.VelX, k.VelY, 0);
        var angle = AngleDeg(-k.VelZ, h);
        _wreckImpacts.Add(new WreckImpact(k.Id, k.Owner, k.Archetype, k.Cause, k.BrokenUp, at, Fixed.FromRaw(alt), k.VelX, k.VelY, k.VelZ, angle,
            hit, t?.Def.IsStructure ?? false, crater));
    }

    // AngleDeg is the angle (whole degrees, 0 to 90) whose tangent is up / along (both raw, along >= 0); a
    // negative up counts as level.
    private static int AngleDeg(long up, long along)
    {
        if (up <= 0)
        {
            return 0;
        }
        if (along <= 0)
        {
            return 90;
        }
        // The smallest degree whose cosine falls to or under the horizontal share of the speed.
        var len = Math.Max(1, Len3(up, along, 0));
        var cos = along * 1000 / len;
        for (var d = 0; d <= 90; d++)
        {
            if (CosDeg(d) <= cos)
            {
                return d;
            }
        }
        return 90;
    }

    // StepChute moves an ejected pilot one tick: the seat's rise, then the descent at the chute rate (the main
    // chute opening at its tick), the drift; at the ground he lands.
    private void StepChute(Chute c)
    {
        var ej = Content.Archetypes[c.AircraftArchetype].Loss!.Eject!;
        c.Age++;
        if (c.Age <= ej.DriftTicks && (c.Drift.X.Raw != 0 || c.Drift.Y.Raw != 0))
        {
            c.Pos = OnMap(c.Pos.X.Raw + c.Drift.X.Raw, c.Pos.Y.Raw + c.Drift.Y.Raw);
        }
        if (c.Age <= ej.RiseTicks)
        {
            c.Altitude = Fixed.FromRaw(c.From.Raw + ej.SeatRiseRaw * c.Age / ej.RiseTicks);
        }
        else
        {
            var tz = c.RemZ - ej.ChuteRateRaw;
            var sz = tz / TicksPerSecond;
            c.RemZ = tz - sz * TicksPerSecond;
            c.Altitude = Fixed.FromRaw(c.Altitude.Raw + sz);
        }
        if (c.Age == ej.RiseTicks)
        {
            c.Phase = ChutePhase.Drogue;
            _ejectionEvents.Add(new EjectionEvent(c.Id, c.Aircraft, c.Owner, EjectionEventKind.SeatAway, c.Pos, c.Altitude, 0));
        }
        if (c.Age == ej.ChuteTicks)
        {
            c.Phase = ChutePhase.Open;
            _ejectionEvents.Add(new EjectionEvent(c.Id, c.Aircraft, c.Owner, EjectionEventKind.ChuteOpen, c.Pos, c.Altitude, 0));
        }
        var ground = GroundRaw(c.Pos);
        if (c.Age > ej.RiseTicks && (c.Altitude.Raw <= ground || c.Age >= MaxFallTicks2))
        {
            c.Altitude = Fixed.FromRaw(ground);
            Land(c);
        }
    }

    // Land puts the pilot of chute c on the ground: the nearest cell a soldier of his class can stand on (not
    // inside a structure or on blocked terrain), as an idle unit of the aircraft's owner; else he is lost.
    private void Land(Chute c)
    {
        c.Age = -1; // gone at the end of this step
        var def = Player(c.Owner)?.Setup.Defaults[c.Unit];
        var cell = def is null ? (Cell?)null : LandingCell(def, c.Pos);
        if (def is null || cell is not { } at)
        {
            _ejectionEvents.Add(new EjectionEvent(c.Id, c.Aircraft, c.Owner, EjectionEventKind.Lost, c.Pos, c.Altitude, 0));
            return;
        }
        var here = Cell.Of(c.Pos);
        var pos = at == here ? c.Pos : at.Center(); // where he came down, or the middle of the nearest free cell
        var facing = Facing.TryFromDirection(c.Drift, out var f) ? f : Facing.North;
        var u = Add(def, pos, facing);
        _ejectionEvents.Add(new EjectionEvent(c.Id, c.Aircraft, c.Owner, EjectionEventKind.Landed, pos, Fixed.FromRaw(GroundRaw(pos)), u.Id));
    }

    // LandingCell is the cell nearest p that a unit of def can stand on (routing's passable cells for its class,
    // within LandingSearch cells; structures' footprints and blocked terrain are not), or null.
    private Cell? LandingCell(UnitDef def, Vec p)
    {
        var r = SyncRouting();
        var k = r.ClassOf(def);
        var c = Cell.Of(p);
        if (k < 0)
        {
            return Grid.TrySpiral(c, 0, out var g) ? g : null;
        }
        var i = r.NearestPassable(k, c, LandingSearch);
        return i < 0 ? null : r.CellAt(i);
    }
}
