using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The projectile system: shots in flight travel along their aim line toward the
// point they were aimed at. Each tick the segment a projectile covers is swept
// against units (a circle each, structures by footprint) and blocked terrain;
// the first thing it reaches goes off: a direct hit on that unit (full damage)
// plus splash around the point. Friends count too; the shooter and anyone
// inside a vehicle don't, and flying units only for weapons that hit the air.
// A projectile that reaches its aim point untouched goes off on the ground there. On a map
// with terrain, ballistic shots fly a 3D arc and go off where it meets the ground: a ridge in
// front of the target stops low arcs.
// Launcher rockets (salvo weapons) fly an arc over everything and only collide on
// the way down, within DescentCells of their aim point.
public sealed partial class World
{
    private const int TerrainSteps = 8; // terrain checks per cell along a projectile's path
    private const int DescentCells = 2;  // salvo rockets collide only this close to their aim point

    private readonly List<Detonation> _detonations = [];

    /// <summary>The projectiles that went off in the last tick, in projectile ID order.</summary>
    public IReadOnlyList<Detonation> Detonations => _detonations;

    private void RunProjectiles()
    {
        _detonations.Clear();
        _airBursts.Clear();
        _batteryLost.Clear();
        _incomingEvents.Clear();
        ClearApsEvents();
        foreach (var p in _projectiles)
        {
            if (p.Weapon.Effect.ApsThreat && !p.ApsSeen && StepAps(p))
            {
                continue; // stopped by its target's active protection (ApsSystem.cs)
            }
            if (p.Weapon.Effect.Interceptor is { } ic && p.Weapon.Effect.Guided is { } ig)
            {
                StepInterceptor(p, ic, ig);
                continue;
            }
            if (p.Weapon.Effect.AirStrike is { } st)
            {
                StepStrike(p, st); // an air strike missile (the watcher system)
                continue;
            }
            if (p.Weapon.Effect.Terminal is { } tf)
            {
                StepTerminal(p, tf); // a missile with a terminal profile (TerminalSystem.cs)
                continue;
            }
            if (p.Cruise is { } cs)
            {
                StepCruise(p, cs); // a cruise missile (CruiseSystem.cs)
                continue;
            }
            if (p.Bomb is { } bomb)
            {
                StepBomb(p, bomb); // an aircraft's bomb (the air-to-ground system, E7)
                continue;
            }
            if (p.Weapon.Effect.Guided is { } g)
            {
                StepGuided(p, g);
                continue;
            }
            if (p.Weapon.Effect.Drone is { } dr)
            {
                StepDrone(p, dr);
                continue;
            }
            var d = p.Dest - p.Pos;
            var speed = p.Weapon.Effect.ProjectileSpeed;
            var next = d.Len() > speed ? p.Pos + d.WithLen(speed) : p.Dest;
            var lobbed = p.Weapon.Effect.Lobbed;
            var from = p.Pos;
            if (lobbed && d.LenSq() > (long)DescentCells * Fixed.OneRaw * DescentCells * Fixed.OneRaw)
            {
                // Still high in the arc: only the part of this tick's path inside the descent zone can hit.
                var reach = d.Len() - Fixed.FromInt(DescentCells);
                from = reach >= speed ? next : p.Pos + d.WithLen(reach);
            }
            // On terrain, the arc meets the ground first where a hill is in the way (or on the slope
            // before the aim point): only what it reaches before that can stop it.
            var strike = GroundStrike(p, p.Pos, next);
            var end = next;
            if (strike >= 0)
            {
                var full = (next - p.Pos).Len().Raw;
                end = p.Pos + new Vec(Fixed.FromRaw((next - p.Pos).X.Raw * strike / full), Fixed.FromRaw((next - p.Pos).Y.Raw * strike / full));
                if ((from - p.Pos).LenSq() > (end - p.Pos).LenSq())
                {
                    from = end;
                }
            }
            if (FirstHit(p, from, end, out var at, out var hit, out var terrain))
            {
                Detonate(p, at, hit, terrain);
                continue;
            }
            if (strike >= 0)
            {
                Detonate(p, end, 0, false); // into the hillside
                continue;
            }
            p.Pos = next;
            if (next == p.Dest)
            {
                Detonate(p, p.Dest, 0, false);
            }
        }
        CruiseSightings(); // who sees the cruise missiles now (CruiseSystem.cs)
        _projectiles.RemoveAll(p => p.Done);
        RunLosses(); // falling wrecks and chutes (E10, LossSystem.cs)
    }

    // StepDrone moves a one-way attack drone one tick along its profile (DroneNext): climbing off
    // its rail and rounding off into level cruise at its cruise height (the map's highest ground
    // plus CruiseAboveMax), cruising, then the pitch-over arc into a steep dive that ends on its
    // point. Its path is sampled TerrainSteps times a cell: ground above its height stops it in any
    // phase (a hill in the climb); in the dive the first structure, vehicle or soldier it reaches
    // (lowest ID at a sample) stops it, else the ground at its point.
    private void StepDrone(Projectile p, DroneFlight d)
    {
        p.Age++;
        var a0 = p.Altitude.Raw;
        var n = DroneNext(p, d);
        if (n.Phase == DronePhase.Dive && p.Phase != DronePhase.Dive)
        {
            (p.DiveFrom, p.DiveDist, p.DiveSlope) = (p.Altitude, n.DiveDist, n.DiveSlope);
        }
        p.Phase = n.Phase;
        var step = n.Step;
        var next = p.Pos + step;
        var left = n.Left;
        var a1 = n.Altitude;
        (p.Vel, p.VelZ) = (step, Fixed.FromRaw(a1 - a0)); // this tick's motion (air defence's fuse reads it)
        var len = step.Len().Raw;
        var samples = (int)Math.Max(1, (len * TerrainSteps + Fixed.OneRaw - 1) / Fixed.OneRaw);
        for (var k = 1; k <= samples; k++)
        {
            var q = p.Pos + new Vec(Fixed.FromRaw(step.X.Raw * k / samples), Fixed.FromRaw(step.Y.Raw * k / samples));
            var aq = a0 + (a1 - a0) * k / samples;
            if (Ground is not null && aq <= GroundRaw(q))
            {
                p.Altitude = Fixed.FromRaw(aq);
                Detonate(p, q, 0, true); // into the hillside
                return;
            }
            if (p.Phase == DronePhase.Dive && DiveHit(p, q, aq) is var hit and not 0)
            {
                p.Altitude = Fixed.FromRaw(aq);
                Detonate(p, q, hit, false);
                return;
            }
        }
        p.Pos = next;
        p.Altitude = Fixed.FromRaw(a1);
        if (left == 0)
        {
            Detonate(p, p.Dest, 0, false); // the ground at its point
        }
    }

    /// <summary>A drone's next tick (DroneNext): its step, height, phase, and the dive it starts.</summary>
    internal readonly record struct DroneMove(Vec Step, long Altitude, long Left, DronePhase Phase, Fixed DiveDist, int DiveSlope);

    // DroneNext works out a drone's next tick without changing it (air defence reads it too). The
    // horizontal step is its speed toward its point. The height:
    // - Climb: up at most ClimbPerTick, and no faster than sqrt(2 a d) with d the height left to its
    //   cruise and a = ClimbPerTick / RoundOffTicks: the climb rate falls evenly to zero as it levels
    //   off (no corner at the top). Cruise once there.
    // - Pitch-over: it starts when its point is as close as the arc plus a straight dive from its
    //   height need: D = H / s + L/2 + s0 L / (2 s), with s the dive slope (permille), s0 its slope now
    //   and L = PitchOverTicks of flight. Over the first L of the dive its slope turns evenly from s0
    //   to -s (a parabola in height), then a straight line from the arc's end to its point.
    internal DroneMove DroneNext(Projectile p, DroneFlight d)
    {
        var speed = p.Weapon.Effect.ProjectileSpeed;
        var to = p.Dest - p.Pos;
        var remaining = to.Len();
        var step = remaining.Raw > speed.Raw ? to.WithLen(speed) : to;
        var left = remaining.Raw > speed.Raw ? remaining.Raw - speed.Raw : 0;
        var target = GroundRaw(p.Dest);
        var a0 = p.Altitude.Raw;
        var s = (long)d.DivePermille;
        var arc = (long)d.PitchOverTicks * speed.Raw;
        var phase = p.Phase;
        var (diveDist, diveSlope, from) = (p.DiveDist.Raw, p.DiveSlope, a0);
        if (phase != DronePhase.Dive)
        {
            var s0 = speed.Raw > 0 ? p.VelZ.Raw * 1000 / speed.Raw : 0; // its slope now (last tick's)
            var need = (a0 - target) * 1000 / s + arc / 2 + s0 * arc / (2 * s);
            if (remaining.Raw <= need)
            {
                (phase, diveDist, diveSlope) = (DronePhase.Dive, remaining.Raw, (int)s0);
            }
        }
        else
        {
            from = p.DiveFrom.Raw;
        }
        long a1;
        switch (phase)
        {
            case DronePhase.Climb:
                var up = MaxGroundRaw() + d.CruiseAboveMax - a0;
                var rate = d.ClimbPerTick;
                if (d.RoundOffTicks > 0 && up > 0)
                {
                    var accel = Math.Max(1, d.ClimbPerTick / d.RoundOffTicks);
                    rate = Math.Min(rate, Math.Max(1, Fixed.Sqrt(2 * accel * up)));
                }
                a1 = a0 + Math.Clamp(up, 0, rate);
                if (a1 - a0 >= up)
                {
                    phase = DronePhase.Cruise;
                }
                break;
            case DronePhase.Cruise:
                a1 = a0;
                break;
            default:
                var l = Math.Min(arc, diveDist * 2 / 3); // a short dive keeps a straight part
                var x = diveDist - left;                  // how far into the dive after this step
                var end = from + diveSlope * l / 1000 - (s + diveSlope) * l / 2000;
                if (x <= l && l > 0)
                {
                    a1 = from + diveSlope * x / 1000 - (s + diveSlope) * x * x / (2000 * l);
                }
                else
                {
                    a1 = diveDist - l > 0 ? target + (end - target) * left / (diveDist - l) : target;
                }
                break;
        }
        return new DroneMove(step, a1, left, phase, Fixed.FromRaw(diveDist), diveSlope);
    }

    // DiveHit returns the first ground unit (lowest ID) a diving drone at q, height aq, reaches:
    // a structure whose footprint holds q, or a unit within its radius of q, no lower than aq at
    // its top (its ground plus its archetype's height); or 0.
    private uint DiveHit(Projectile p, Vec q, long aq) => DiveHit(p.Shooter, q, aq);

    // DiveHit is DiveHit for anything coming down at q (height aq) that is not unit except (a falling wreck, E10).
    private uint DiveHit(uint except, Vec q, long aq)
    {
        var ground = GroundRaw(q);
        foreach (var u in _units)
        {
            if (!u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Id == except || u.Def.Layer != Layers.Ground ||
                aq > ground + u.Def.Archetype.HeightRaw || u.Def.Archetype.Pavement || u.Def.Archetype.Bridge is not null) // (onto what stands on a bridge)
            {
                continue;
            }
            if (u.Def.IsStructure)
            {
                var o = FootprintOrigin(u);
                if (q.X.Raw >= (long)o.X * Fixed.OneRaw && q.X.Raw < (long)(o.X + FootW(u)) * Fixed.OneRaw &&
                    q.Y.Raw >= (long)o.Y * Fixed.OneRaw && q.Y.Raw < (long)(o.Y + FootH(u)) * Fixed.OneRaw)
                {
                    return u.Id;
                }
                continue;
            }
            if (Within(u.Pos, q, u.Def.Archetype.Radius))
            {
                return u.Id;
            }
        }
        return 0;
    }

    private long GroundRaw(Vec q) => Ground?.HeightAt(q).Raw ?? 0;

    // The map's highest ground (raw), recomputed when the terrain changes (pads only lower it).
    private long _maxGround;
    private int _maxGroundVersion = -1;

    private long MaxGroundRaw()
    {
        if (Ground is not { } g)
        {
            return 0;
        }
        if (_maxGroundVersion != g.Version)
        {
            var m = long.MinValue;
            for (var y = 0; y <= g.Height; y++)
            {
                for (var x = 0; x <= g.Width; x++)
                {
                    m = Math.Max(m, g.Corner(x, y).Raw);
                }
            }
            foreach (var b in g.WaterBodies)
            {
                m = Math.Max(m, b.Surface.Raw); // a water surface above the ground round it (WorldWater.cs)
            }
            (_maxGround, _maxGroundVersion) = (m, g.Version);
        }
        return _maxGround;
    }

    // StepGuided moves a guided missile one tick: it steers toward its target while guided (its
    // velocity changes by at most TurnPermille of its speed), checks whether guidance holds, and
    // once unguided flies straight on for DropDistance and goes off on the ground. It hits only
    // its target or blocked terrain.
    private void StepGuided(Projectile p, Guidance g)
    {
        var speed = p.Weapon.Effect.ProjectileSpeed;
        var target = Unit(p.Target);
        if (p.Guided && (SmokeBreaksLock(p, g, target) || !Guidance(p, g, target))) // a smoke screen breaks the lock (SmokeSystem.cs)
        {
            p.Guided = false;
            p.DropLeft = g.DropDistance;
            EndGuidance(p);
        }
        // Fired at a point on the ground (attack ground), it is guided onto that point.
        var groundShot = p.Target == 0;
        if (p.Guided && (target is not null || groundShot))
        {
            var want = (target is null ? p.Dest : target.Def.IsStructure ? AimPoint(p.Pos, target) : target.Pos) - p.Pos;
            if (want.LenSq() > 0)
            {
                var dv = want.WithLen(speed) - p.Vel;
                var max = speed.Raw * g.TurnPermille / 1000;
                if (dv.LenSq() > max * max)
                {
                    dv = dv.WithLen(Fixed.FromRaw(max));
                }
                var nv = p.Vel + dv;
                p.Vel = nv.LenSq() > 0 ? nv.WithLen(speed) : want.WithLen(speed);
            }
        }
        var step = p.Vel;
        var landing = false;
        if (!p.Guided && p.DropLeft.Raw <= speed.Raw)
        {
            step = p.DropLeft.Raw > 0 ? p.Vel.WithLen(p.DropLeft) : new Vec(Fixed.Zero, Fixed.Zero);
            landing = true;
        }
        if (groundShot && p.Guided && (p.Dest - p.Pos).LenSq() <= speed.Raw * speed.Raw)
        {
            step = p.Dest - p.Pos; // it reaches its point this tick and goes off on the ground there
            landing = true;
        }
        var next = p.Pos + step;
        // On terrain it flies low and straight toward its aim: a crest higher than that stops it.
        var crest = CrestStrike(p, target, p.Pos, next);
        var end = crest >= 0 ? p.Pos + new Vec(Fixed.FromRaw(step.X.Raw * crest / Math.Max(step.Len().Raw, 1)), Fixed.FromRaw(step.Y.Raw * crest / Math.Max(step.Len().Raw, 1))) : next;
        if (GuidedHit(p, target, p.Pos, end, out var at, out var hit, out var terrain))
        {
            Detonate(p, at, hit, terrain);
            EndGuidance(p);
            return;
        }
        if (crest >= 0)
        {
            Detonate(p, end, 0, false); // into the crest
            EndGuidance(p);
            return;
        }
        p.Pos = next;
        p.Flown += step.Len();
        if (!p.Guided)
        {
            p.DropLeft -= step.Len();
        }
        if (landing)
        {
            Detonate(p, next, 0, false);
            EndGuidance(p);
        }
    }

    // CrestStrike finds where a guided missile's step from a to b meets the terrain on a map
    // with terrain (the ground above its low, straight flight: World.GuidedAltitude), sampled
    // TerrainSteps times a cell: the distance along the step (raw), or -1.
    private long CrestStrike(Projectile p, Unit? target, Vec a, Vec b)
    {
        if (Ground is not { } g || !p.LowFlight)
        {
            return -1;
        }
        var aim = target is not null ? (target.Def.IsStructure ? AimPoint(a, target) : target.Pos) : p.Dest;
        var seg = b - a;
        var len = seg.Len().Raw;
        var steps = (int)((len * TerrainSteps + Fixed.OneRaw - 1) / Fixed.OneRaw);
        for (var k = 1; k <= steps; k++)
        {
            var q = a + new Vec(Fixed.FromRaw(seg.X.Raw * k / steps), Fixed.FromRaw(seg.Y.Raw * k / steps));
            if (g.HeightAt(q).Raw >= GuidedAltitude(p, q, aim))
            {
                return len * k / steps;
            }
        }
        return -1;
    }

    // Guidance reports whether a guided missile still steers: within its guided flight, its
    // target alive, out in the open and in reach of the shooter, the shooter alive and (for an
    // operator-guided missile) standing still on the attack or idle.
    private bool Guidance(Projectile p, Guidance g, Unit? target)
    {
        if (g.FireAndForget)
        {
            // Its own seeker (a helicopter's missile): within its guided flight, at its target while that is out in the
            // open (or its point); no operator, no line from the shooter.
            return p.Flown < g.MaxFlight && (p.Target == 0 || target is { Alive: true, Inside: 0, Garrison: 0 });
        }
        var shooter = Unit(p.Shooter);
        if (p.Flown >= g.MaxFlight || shooter is not { Alive: true } || shooter.Guiding != p.Id)
        {
            return false;
        }
        if (p.Target == 0)
        {
            // A point on the ground (attack ground): guided while it stays in reach of the operator.
            if (!Within(shooter.Pos, p.Dest, g.MaxFlight))
            {
                return false;
            }
        }
        else if (target is not { Alive: true, Inside: 0, Garrison: 0 } || !Within(shooter.Pos, AimPoint(shooter.Pos, target), g.MaxFlight)) // a building: its nearest wall
        {
            return false;
        }
        if (Ground is not null && !LineOfFire(shooter, p.Target == 0 ? p.Dest : AimPoint(shooter.Pos, target!), p.Weapon, p.Target == 0 ? null : target))
        {
            return false; // the operator lost the line (a crest between them, on terrain)
        }
        return !g.RequiresOperatorStill || (!shooter.Moving && shooter.Order.Kind is OrderKind.Attack or OrderKind.AttackGround or OrderKind.Idle && shooter.CanAct);
    }

    // EndGuidance frees the shooter of a guided missile that went off or lost guidance: it
    // reloads from now (it could not while guiding).
    private void EndGuidance(Projectile p)
    {
        if (Unit(p.Shooter) is { } s && s.Guiding == p.Id)
        {
            s.Guiding = 0;
            s.Reload = p.Weapon.Reload * 100;
        }
    }

    // GuidedHit finds where a guided missile's segment a->b first reaches its target (circle or
    // footprint; only while guided), a building wall (a shot at a point on the ground) or
    // blocked terrain; nothing else stops it.
    private bool GuidedHit(Projectile p, Unit? target, Vec a, Vec b, out Vec at, out uint hit, out bool terrain)
    {
        at = b;
        hit = 0;
        terrain = false;
        var seg = b - a;
        var len = seg.Len().Raw;
        if (len == 0)
        {
            return false;
        }
        var best = long.MaxValue;
        if (p.Guided && target is { Alive: true, Inside: 0, Garrison: 0 }) // once unguided it only finds the ground
        {
            var s = target.Def.IsStructure ? EnterBox(a, seg, len, target, false) : EnterCircle(a, seg, len, target.Pos, target.Def.Archetype.Radius.Raw);
            if (s >= 0)
            {
                best = s;
                hit = target.Id;
            }
        }
        if (p.Target == 0)
        {
            // Fired at a point (attack ground): the first building wall in its way stops it, so a
            // point on a building hits the building (candidates in ID order: ties go to the lower ID).
            var m = _index.MaxRadiusRaw + Fixed.OneRaw;
            foreach (var u in Around(Math.Min(a.X.Raw, b.X.Raw) - m, Math.Min(a.Y.Raw, b.Y.Raw) - m,
                Math.Max(a.X.Raw, b.X.Raw) + m, Math.Max(a.Y.Raw, b.Y.Raw) + m, _nearHit, true))
            {
                if (!u.Alive || !u.Def.IsStructure || u.Id == p.Cover || u.Def.Archetype.Pavement || u.Def.Archetype.Bridge is not null)
                {
                    continue;
                }
                var s = EnterBox(a, seg, len, u, false);
                if (s >= 0 && s < best)
                {
                    best = s;
                    hit = u.Id;
                }
            }
        }
        var steps = (int)((len * TerrainSteps + Fixed.OneRaw - 1) / Fixed.OneRaw);
        for (var k = 1; k <= steps; k++)
        {
            var q = a + new Vec(Fixed.FromRaw(seg.X.Raw * k / steps), Fixed.FromRaw(seg.Y.Raw * k / steps));
            if (Grid.At(Cell.Of(q)) == Terrain.Blocked)
            {
                var s = len * k / steps;
                if (s < best)
                {
                    best = s;
                    hit = 0;
                    terrain = true;
                }
                break;
            }
        }
        if (best == long.MaxValue)
        {
            return false;
        }
        at = a + new Vec(Fixed.FromRaw(seg.X.Raw * best / len), Fixed.FromRaw(seg.Y.Raw * best / len));
        return true;
    }

    private void Detonate(Projectile p, Vec at, uint hit, bool terrain)
    {
        p.Pos = at;
        p.Done = true;
        var w = p.Weapon;
        _hurtBy = Unit(p.Shooter); // for Return fire and retaliation (Hurt)
        // Directional armour: a lobbed round comes down from above; a straight shot hits from where it was launched, a guided
        // missile from the way it flies in (ArmorSystem.cs).
        SetHit(w.Effect.Lobbed, w.Effect.Guided is not null && p.Vel != default ? at - p.Vel : p.Launched);
        if (Unit(hit) is { } t && w.Warhead.CanHit(ArmorOf(t)))
        {
            Hurt(t, w.Warhead, w.Damage, 100);
        }
        if (w.Effect.Splash.Raw > 0)
        {
            Explode(at, w.Damage, w.Warhead, w.Effect.Splash, hit, w.Effect.SplashCenter, w.Effect.SplashEdge, w.Effect.SplashInner, w.Effect.SplashOuterPercent);
        }
        else
        {
            FellByBlast(at, w.Warhead, Fixed.Zero); // no splash (an anti-tank missile): the trees right there (TreeSystem.cs)
            IgniteByBlast(at, w.Warhead, Fixed.Zero); // and the forest set on fire, if the warhead does (FireSystem.cs)
        }
        if (w.Effect.Fire is { } wf)
        {
            IgniteByFire(at, wf, w.Effect.Splash); // the weapon's own fire block, on top of its warhead's (the hypersonic missile)
        }
        _hurtBy = null;
        ClearHit();
        if (p.Target != 0 && Unit(p.Shooter) is { } shooter)
        {
            // Count shots in a row that missed the unit they were aimed at.
            if (hit == p.Target)
            {
                shooter.MissStreak = 0;
            }
            else
            {
                shooter.MissStreak = shooter.MissTarget == p.Target ? shooter.MissStreak + 1 : 1;
                shooter.MissTarget = p.Target;
            }
        }
        _detonations.Add(new Detonation(p.Id, at, hit, terrain, !terrain && WaterAt(at, hit)));
        NoteTerminalEnd(p, false); // an incoming strike landed (TerminalSystem.cs)
        RunwayBlast(at, w.Effect.Runway); // craters and wear (AirfieldDamageSystem.cs)
    }

    // FirstHit finds the first unit or rock the segment a->b reaches: the smallest
    // distance along it, ties going to the lower unit ID and then to terrain.
    private bool FirstHit(Projectile p, Vec a, Vec b, out Vec at, out uint hit, out bool terrain)
    {
        at = b;
        hit = 0;
        terrain = false;
        var seg = b - a;
        var len = seg.Len().Raw;
        if (len == 0)
        {
            return false;
        }
        var best = long.MaxValue;
        var airToo = (p.Weapon.Effect.Targets & Layers.Air) != 0;
        var lobbed = p.Weapon.Effect.Lobbed;
        // Candidates: units within the largest radius of the segment's box, structures whose footprint touches it.
        var m = _index.MaxRadiusRaw + Fixed.OneRaw;
        var near = Around(Math.Min(a.X.Raw, b.X.Raw) - m, Math.Min(a.Y.Raw, b.Y.Raw) - m,
            Math.Max(a.X.Raw, b.X.Raw) + m, Math.Max(a.Y.Raw, b.Y.Raw) + m, _nearHit, true);
        foreach (var u in near)
        {
            if (!u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Id == p.Shooter || u.Id == p.Cover || (u.Def.Layer == Layers.Air && !airToo) || u.Def.Archetype.Flight is not null || !IsRevealed(u, p.Owner) ||
                u.Def.Archetype.Pavement || u.Def.Archetype.Bridge is not null) // shots pass over an airfield's pavement and a bridge's deck and land on it
            {
                continue;
            }
            var s = u.Def.IsStructure ? EnterBox(a, seg, len, u, lobbed) : EnterCircle(a, seg, len, u.Pos, u.Def.Archetype.Radius.Raw);
            if (s >= 0 && s < best)
            {
                best = s;
                hit = u.Id;
            }
        }
        // Rocks and rubble (blocked cells) stop flat shots; a lobbed rocket comes down on them from
        // above and lands at its point (hills: GroundStrike).
        var steps = lobbed ? 0 : (int)((len * TerrainSteps + Fixed.OneRaw - 1) / Fixed.OneRaw);
        var cover = Unit(p.Cover);
        for (var k = 1; k <= steps; k++)
        {
            var q = a + new Vec(Fixed.FromRaw(seg.X.Raw * k / steps), Fixed.FromRaw(seg.Y.Raw * k / steps));
            if (Grid.At(Cell.Of(q)) == Terrain.Blocked && (cover is null || AimPoint(q, cover) != q))
            {
                var s = len * k / steps;
                if (s < best)
                {
                    best = s;
                    hit = 0;
                    terrain = true;
                }
                break;
            }
        }
        if (best == long.MaxValue)
        {
            return false;
        }
        at = a + new Vec(Fixed.FromRaw(seg.X.Raw * best / len), Fixed.FromRaw(seg.Y.Raw * best / len));
        return true;
    }

    // EnterCircle returns how far along the segment (raw units) it enters the circle,
    // or -1. A segment that starts inside the circle is leaving it and never hits it.
    private static long EnterCircle(Vec a, Vec seg, long len, Vec c, long r)
    {
        long fx = a.X.Raw - c.X.Raw, fy = a.Y.Raw - c.Y.Raw;
        var f2 = fx * fx + fy * fy;
        if (f2 <= r * r)
        {
            return -1;
        }
        // Distance along the segment to the point closest to the centre, and that point's distance.
        var along = -(fx * seg.X.Raw + fy * seg.Y.Raw) / len;
        if (along < 0)
        {
            return -1;
        }
        var h2 = f2 - along * along;
        if (h2 > r * r)
        {
            return -1;
        }
        var s = along - Fixed.Sqrt(r * r - h2);
        return s <= len ? Math.Max(s, 0) : -1;
    }

    // EnterBox returns how far along the segment it enters a structure's footprint, or -1. A
    // segment starting inside is leaving it, except for a lobbed rocket coming down: that one
    // lands on the roof where its descent starts.
    private static long EnterBox(Vec a, Vec seg, long len, Unit u, bool lobbed)
    {
        long hw = (long)FootW(u) * Fixed.OneRaw / 2, hh = (long)FootH(u) * Fixed.OneRaw / 2;
        long x0 = u.Pos.X.Raw - hw, x1 = u.Pos.X.Raw + hw, y0 = u.Pos.Y.Raw - hh, y1 = u.Pos.Y.Raw + hh;
        if (a.X.Raw >= x0 && a.X.Raw <= x1 && a.Y.Raw >= y0 && a.Y.Raw <= y1)
        {
            return lobbed ? 0 : -1;
        }
        // Slab test in fractions of the segment, scaled by len to stay in integers.
        long enter = 0, exit = len;
        if (!Slab(a.X.Raw, seg.X.Raw, x0, x1, len, ref enter, ref exit) || !Slab(a.Y.Raw, seg.Y.Raw, y0, y1, len, ref enter, ref exit))
        {
            return -1;
        }
        return enter <= exit ? enter : -1;
    }

    private static bool Slab(long o, long d, long lo, long hi, long len, ref long enter, ref long exit)
    {
        if (d == 0)
        {
            return o >= lo && o <= hi;
        }
        long t0 = (lo - o) * len / d, t1 = (hi - o) * len / d;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }
        enter = Math.Max(enter, t0);
        exit = Math.Min(exit, t1);
        return enter <= exit;
    }
}
