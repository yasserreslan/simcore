using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The pass plan (H2, the ground attack jet; generic for any aircraft with free-fall bombs, the B-52 type's in H4): fire
// modes, a queue of aims bombed together, a line on the ground. All integer; units by ID; aims in queue order.
//
//  - Fire modes (delivery.modes; Unit.FireMode, CommandKind.SetFireMode): how many bombs a pass releases at each aim
//    point, a stick along its path stickSpacing apart, centred on the point ("single" 1, "salvo4" 4).
//  - The queue (Unit.Aims): Shift on an attack, attack-ground or AttackLine order appends the aim; the first is its order.
//    A pass bombs its first aim and the queued points and units within the delivery's group (a stick's length) of it,
//    along the line from it to the farthest of them and at most groupAside off it; the rest wait for later passes, in
//    queue order. After a pass the aims it bombed are done (the next one is its order); the last one left goes on as a
//    plain order (bombs, then the gun), except a line: bombed, its order is done.
//  - A line (CommandKind.AttackLine): one pass along it in the drag's direction, the mode's bombs spread evenly from end to
//    end (one: the middle).
//  - A pass with its line fixed by the plan (a line, aims together: StrikeRun.Forced) flies to an entry point on the
//    line's extension, behind its first drop (StrikeRun.Entry), then turns in, aligns on the line and follows it on its
//    run; an unforced pass locks its line as E13's does and lays its stick on it. Each run tick the release solution's
//    impact point is checked against the next drop: released within half a tick's travel of it, skipped once past it
//    by more than the release window or off the line by more than the window, the scatter and (forced) groupAside.
public sealed partial class World
{
    // At most this many aims in an aircraft's queue (its order's and 7 more: Patrol's 8 waypoints).
    private const int MaxAims = 8;

    // A forced pass takes its line when its heading is within about 25 degrees of the run's direction (cos, thousandths).
    private const int ForcedLockCos = 900;

    private readonly List<int> _aimScratch = [];

    /// <summary>
    /// Aircraft u's bomb fire modes (H2): its first bomb weapon's (a delivery block with modes) in order, the first the default;
    /// empty when it has none. <see cref="Unit.FireMode"/> indexes this list.
    /// </summary>
    public static IReadOnlyList<FireModeDef> FireModesOf(Unit u)
    {
        foreach (var w in u.Def.Weapons)
        {
            if (w.Effect.Delivery is { FireModes.Count: > 0 } dl)
            {
                return dl.FireModes;
            }
        }
        return [];
    }

    // HasBombs: u carries a bomb weapon (a delivery block), loaded or not (H2: its queue of aims).
    private static bool HasBombs(Unit u)
    {
        foreach (var w in u.Def.Weapons)
        {
            if (w.Effect.Delivery is not null)
            {
                return true;
            }
        }
        return false;
    }

    // LineWeapon is u's first free-fall bomb (a delivery block, not guided): with ammunition (loaded) or any (an empty one).
    private static WeaponDef? LineWeapon(Unit u, bool loaded)
    {
        foreach (var w in u.Def.Weapons)
        {
            if (w.Effect.Delivery is { Guided: null } && (w.Effect.Targets & Layers.Ground) != 0 &&
                (loaded ? HasAmmo(u, w) : w.Stock >= 0 && u.Def.StockMax[w.Stock] > 0))
            {
                return w;
            }
        }
        return null;
    }

    // AimMatchesOrder: u's first queued aim is its order's (an attack at its target, an attack-ground at its cell).
    private static bool AimMatchesOrder(Unit u)
    {
        var a = u.Aims[0];
        return a.Target != 0 ? u.Order.Kind == OrderKind.Attack && u.Order.Target == a.Target
            : u.Order.Kind == OrderKind.AttackGround && u.Order.Cell == a.Cell;
    }

    // OrderAim is u's current ground strike order as an aim.
    private StrikeAim OrderAim(Unit u) =>
        u.Order.Kind == OrderKind.Attack
            ? new StrikeAim(u.Order.Target, Unit(u.Order.Target) is { } t ? Cell.Of(t.Pos) : u.Order.Cell, default, false)
            : new StrikeAim(0, u.Order.Cell, default, false);

    // AimPos is where an aim is now: a unit's position while it lives, else the aim's cell (a line: its start).
    private Vec AimPos(StrikeAim a) => a.Target != 0 && Unit(a.Target) is { Alive: true } t ? t.Pos : a.Cell.Center();

    // QueueAim (H2): Shift on aircraft u with bombs on a ground strike appends aim to its queue (its order's own aim first);
    // true when it was taken (appended, or ignored with the queue full), false to give the order the ordinary way.
    private bool QueueAim(Unit u, StrikeAim aim)
    {
        if (!HasBombs(u) || u.Order.Kind is not (OrderKind.Attack or OrderKind.AttackGround) ||
            (u.Order.Kind == OrderKind.Attack && Unit(u.Order.Target) is { } t && AirBodyOfUnit(t) is not null))
        {
            return false;
        }
        var aims = u.MutableAims;
        if (aims.Count > 0 && !AimMatchesOrder(u))
        {
            aims.Clear();
        }
        if (aims.Count == 0)
        {
            aims.Add(OrderAim(u));
        }
        if (aims.Count < MaxAims)
        {
            aims.Add(aim);
        }
        if (u.Strike is { Phase: StrikePhase.Ingress } run && run.Weapon >= 0 && run.Weapon < u.Def.Weapons.Count)
        {
            PlanPass(u, run, u.Def.Weapons[run.Weapon]); // not yet on a run: its first pass may take the new aim along
        }
        return true;
    }

    // NextAim (H2): u's first aim is done or gone (dead, lost): the next one becomes its order. False with none queued.
    private bool NextAim(Unit u)
    {
        var aims = u.MutableAims;
        if (aims.Count < 2)
        {
            return false;
        }
        aims.RemoveAt(0);
        Retarget(u, aims[0]);
        if (aims.Count == 1 && !aims[0].Line)
        {
            aims.Clear(); // the last one: a plain order
        }
        return true;
    }

    // Retarget makes aim u's order; a strike in progress flies on toward it (planned again before its next run).
    private void Retarget(Unit u, StrikeAim aim)
    {
        u.Order = aim.Target != 0 ? new Order(OrderKind.Attack, aim.Cell, aim.Target, 0) : new Order(OrderKind.AttackGround, aim.Cell, 0, 0);
        u.SeekTicks = 0;
        if (u.Strike is not { } run)
        {
            return;
        }
        (run.Target, run.Cell, run.Unseen) = (aim.Target, aim.Target == 0 ? aim.Cell : default, 0);
        if (run.Weapon < 0 || run.Weapon >= u.Def.Weapons.Count)
        {
            return;
        }
        if (run.Phase == StrikePhase.Align)
        {
            SetPhase(u, run, StrikePhase.Reposition); // its line was for the old aim: turn toward the new one
        }
        if (run.Phase is StrikePhase.Ingress or StrikePhase.Reposition)
        {
            PlanPass(u, run, u.Def.Weapons[run.Weapon]);
        }
    }

    // AimsAfterPass (H2): the aims u's pass bombed (a planned pass: those that got a bomb; else its first, if it released
    // or fired) are done. The next one left becomes its order; with none left the last bombed goes on as a plain order,
    // or, a line, its order is done (the strike ends after the break and extend).
    private void AimsAfterPass(Unit u, StrikeRun run)
    {
        var aims = u.MutableAims;
        if (aims.Count == 0)
        {
            return;
        }
        _aimScratch.Clear();
        for (var i = 0; i < aims.Count; i++)
        {
            var hit = false;
            if (run.Planned)
            {
                for (var j = 0; j < run.Served.Count && j < 64; j++)
                {
                    hit |= (run.ServedHits >> j & 1) != 0 && run.Served[j] == aims[i];
                }
            }
            else
            {
                hit = i == 0 && run.Released > 0;
            }
            if (hit)
            {
                _aimScratch.Add(i);
            }
        }
        if (_aimScratch.Count == 0)
        {
            return;
        }
        var last = aims[_aimScratch[^1]];
        for (var k = _aimScratch.Count - 1; k >= 0; k--)
        {
            aims.RemoveAt(_aimScratch[k]);
        }
        if (aims.Count == 0)
        {
            if (last.Line)
            {
                run.Final = true; // a line bombed: its order is done
                return;
            }
            Retarget(u, last); // the last one goes on as a plain order (bombs, then the gun)
            return;
        }
        var head = aims[0];
        if (!AimMatchesOrder(u))
        {
            Retarget(u, head);
        }
        if (aims.Count == 1 && !head.Line)
        {
            aims.Clear();
        }
    }

    // ---- The plan ----

    // PlanPass plans u's next pass with weapon w (H2; at a strike's start, a weapon change, a reposition, a new aim): a
    // planned pass for a free-fall bomb with fire modes or an aircraft with a queue (else an E7 pass). A line, or the first
    // aim with queued ones close to it, fixes the run line (forced: its drops now, its entry point); a lone aim's stick is
    // laid on the run line when that is locked (PlanDrops).
    private void PlanPass(Unit u, StrikeRun run, WeaponDef w)
    {
        run.ClearPlan();
        if (w.Effect.Delivery is not { Guided: null } dl || (dl.FireModes.Count == 0 && u.Aims.Count == 0))
        {
            return;
        }
        run.Planned = true;
        var salvo = Math.Max(1, dl.SalvoOf(u.FireMode));
        var head = u.Aims.Count > 0 ? u.Aims[0] : OrderAim(u);
        var hp = head.Line ? head.Cell.Center() : AimPos(head);
        run.MutableServed.Add(head);
        run.ServedAt.Add(hp);
        if (head.Line && head.End != head.Cell)
        {
            var end = head.End.Center();
            long lx = end.X.Raw - hp.X.Raw, ly = end.Y.Raw - hp.Y.Raw;
            var len = Math.Max(1, Len3(lx, ly, 0));
            for (var i = 0; i < salvo; i++)
            {
                long num = salvo == 1 ? 1 : i, den = salvo == 1 ? 2 : salvo - 1;
                run.MutableDrops.Add(OnMap(hp.X.Raw + lx * num / den, hp.Y.Raw + ly * num / den));
                run.DropAim.Add(0);
            }
            SetForced(run, (int)(lx * 1000 / len), (int)(ly * 1000 / len));
            return;
        }
        if (head.Line || dl.GroupRaw <= 0 || u.Aims.Count < 2)
        {
            return; // a lone aim: its stick on the run line it locks (PlanDrops)
        }
        // Queued aims within the group's reach of the first: the line from it to the farthest of them.
        var far = -1;
        long farD = -1;
        for (var i = 1; i < u.Aims.Count; i++)
        {
            if (u.Aims[i].Line)
            {
                continue;
            }
            var p = AimPos(u.Aims[i]);
            var d = Len3(p.X.Raw - hp.X.Raw, p.Y.Raw - hp.Y.Raw, 0);
            if (d <= dl.GroupRaw && d > farD)
            {
                (far, farD) = (i, d);
            }
        }
        if (far < 0)
        {
            return;
        }
        int ux, uy;
        var fp = AimPos(u.Aims[far]);
        if (farD > 0)
        {
            (ux, uy) = ((int)((fp.X.Raw - hp.X.Raw) * 1000 / farD), (int)((fp.Y.Raw - hp.Y.Raw) * 1000 / farD));
        }
        else
        {
            long ax = hp.X.Raw - u.Pos.X.Raw, ay = hp.Y.Raw - u.Pos.Y.Raw;
            var al = Len3(ax, ay, 0);
            (ux, uy) = al == 0 ? Heading(u) : ((int)(ax * 1000 / al), (int)(ay * 1000 / al));
        }
        for (var i = 1; i < u.Aims.Count; i++)
        {
            if (u.Aims[i].Line)
            {
                continue;
            }
            var p = AimPos(u.Aims[i]);
            long px = p.X.Raw - hp.X.Raw, py = p.Y.Raw - hp.Y.Raw;
            var along = (px * ux + py * uy) / 1000;
            var cross = Math.Abs(px * uy - py * ux) / 1000;
            if (Len3(px, py, 0) <= dl.GroupRaw && along >= 0 && along <= dl.GroupRaw && cross <= dl.GroupAsideRaw)
            {
                run.MutableServed.Add(u.Aims[i]);
                run.ServedAt.Add(p);
            }
        }
        if (run.Served.Count < 2)
        {
            return;
        }
        // The run starts at the end nearer the aircraft.
        if (Len3(u.Pos.X.Raw - fp.X.Raw, u.Pos.Y.Raw - fp.Y.Raw, 0) < Len3(u.Pos.X.Raw - hp.X.Raw, u.Pos.Y.Raw - hp.Y.Raw, 0))
        {
            (ux, uy) = (-ux, -uy);
        }
        for (var j = 0; j < run.Served.Count; j++)
        {
            AddStick(run, run.ServedAt[j], j, ux, uy, salvo, dl.StickSpacing.Raw);
        }
        SortDrops(run, hp, ux, uy);
        SetForced(run, ux, uy);
    }

    // SetForced fixes a planned pass's run line: flown in direction (ux, uy) (thousandths) through its drops; it flies to the
    // entry point first.
    private static void SetForced(StrikeRun run, int ux, int uy)
    {
        (run.DirX, run.DirY) = (-ux, -uy); // the run line's direction from the aim back toward the align's start
        (run.Forced, run.Entry) = (true, run.Phase is StrikePhase.Ingress or StrikePhase.Reposition);
    }

    // AddStick adds aim j's salvo of drops at p: spacing apart along (ux, uy), centred on p.
    private static void AddStick(StrikeRun run, Vec p, int j, int ux, int uy, int salvo, long spacing)
    {
        for (var i = 0; i < salvo; i++)
        {
            var off = (2L * i - (salvo - 1)) * spacing / 2;
            run.MutableDrops.Add(new Vec(Fixed.FromRaw(p.X.Raw + ux * off / 1000), Fixed.FromRaw(p.Y.Raw + uy * off / 1000)));
            run.DropAim.Add(j);
        }
    }

    // SortDrops orders a plan's drops along its run (direction (ux, uy) from `from`); ties keep their order (an insertion sort).
    private static void SortDrops(StrikeRun run, Vec from, int ux, int uy)
    {
        var d = run.MutableDrops;
        long Key(Vec p) => (p.X.Raw - from.X.Raw) * ux + (p.Y.Raw - from.Y.Raw) * uy;
        for (var i = 1; i < d.Count; i++)
        {
            var (p, a) = (d[i], run.DropAim[i]);
            var k = Key(p);
            var j = i - 1;
            while (j >= 0 && Key(d[j]) > k)
            {
                d[j + 1] = d[j];
                run.DropAim[j + 1] = run.DropAim[j];
                j--;
            }
            d[j + 1] = p;
            run.DropAim[j + 1] = a;
        }
    }

    // PlanDrops lays an unforced planned pass's stick on the run line it just locked, centred on its aim (H2).
    private void PlanDrops(Unit u, StrikeRun run)
    {
        if (!run.Planned || run.Forced || run.Served.Count == 0 || u.Def.Weapons[run.Weapon].Effect.Delivery is not { } dl)
        {
            return;
        }
        run.MutableDrops.Clear();
        run.DropAim.Clear();
        (run.DropNext, run.ServedHits) = (0, 0);
        var p = AimPos(run.Served[0]);
        run.ServedAt[0] = p;
        AddStick(run, p, 0, -run.DirX, -run.DirY, Math.Max(1, dl.SalvoOf(u.FireMode)), dl.StickSpacing.Raw);
    }

    // CurrentDrop is drop i of a planned pass now: as planned, a moving unit's following it (and led by the fall time).
    private Vec CurrentDrop(StrikeRun run, int i)
    {
        var a = run.DropAim[i];
        var b = run.Drops[i];
        var s = run.Served[a];
        if (s.Target == 0 || (a == 0 && run.Unseen > 0) || Unit(s.Target) is not { Alive: true, Inside: 0 } t)
        {
            return b;
        }
        long nx = t.Pos.X.Raw, ny = t.Pos.Y.Raw;
        if (t.Vel != default && !t.Def.IsStructure)
        {
            (nx, ny) = (nx + t.Vel.X.Raw * run.FallTicks, ny + t.Vel.Y.Raw * run.FallTicks);
        }
        var at = run.ServedAt[a];
        return OnMap(b.X.Raw + nx - at.X.Raw, b.Y.Raw + ny - at.Y.Raw);
    }

    // PlannedAim is the aim point of a planned pass: its next drop on the run, else its first.
    private Vec PlannedAim(StrikeRun run) =>
        CurrentDrop(run, run.Phase == StrikePhase.Run ? Math.Min(run.DropNext, run.Drops.Count - 1) : 0);

    // ---- Flying a forced pass ----

    // EntryPoint is a forced pass's entry point: on its line's extension, a run line plus a turn diameter behind its first
    // drop (kept inside its fly zone).
    private Vec EntryPoint(Unit u, StrikeRun run)
    {
        var back = LineLen(u, run) + 2 * RunRadius(u);
        long x = run.Aim.X.Raw + run.DirX * back / 1000, y = run.Aim.Y.Raw + run.DirY * back / 1000;
        var m = FlightOf(u).FlyZoneMarginRaw - RunRadius(u);
        long w = (long)Grid.Width * Fixed.OneRaw, h = (long)Grid.Height * Fixed.OneRaw;
        return P(Math.Clamp(x, -m, w + m), Math.Clamp(y, -m, h + m));
    }

    // EntryReached: u is within two captures of its forced pass's entry point.
    private bool EntryReached(Unit u, ScoutFlight f, StrikeRun run)
    {
        var e = EntryPoint(u, run);
        return Len3(e.X.Raw - u.Pos.X.Raw, e.Y.Raw - u.Pos.Y.Raw, 0) <= 2 * StrikeCapture(u, f);
    }

    // ForcedIngress: a forced pass's ingress takes the line at once when it already comes down it (behind the first drop
    // by at least the least align and at most a run line plus a turn diameter, within a turn radius of it, heading along
    // it); else it flies to the entry point and turns in from there (the reposition turn).
    private void ForcedIngress(Unit u, ScoutFlight f, StrikeRun run)
    {
        long px = u.Pos.X.Raw - run.Aim.X.Raw, py = u.Pos.Y.Raw - run.Aim.Y.Raw;
        var along = (px * run.DirX + py * run.DirY) / 1000;
        var cross = Math.Abs(px * run.DirY - py * run.DirX) / 1000;
        var least = run.ReleaseDist + run.IpDist + Math.Max(AlignLen(u) / 2, StrikeCapture(u, f));
        var (hx, hy) = Heading(u);
        var headCos = -((long)hx * run.DirX + (long)hy * run.DirY) / 1000;
        if (along >= least && along <= LineLen(u, run) + 2 * RunRadius(u) && cross <= RunRadius(u) + CaptureSlackRaw && headCos >= ForcedLockCos)
        {
            LockAlign(u, run, false);
        }
        else if (!run.Entry || EntryReached(u, f, run))
        {
            run.Entry = false;
            SetPhase(u, run, StrikePhase.Reposition); // at its entry point: the turn in onto the line
        }
    }

    // LineCarrot is where a forced pass steers on its run: a carrot a turn radius (at least a second) down its line from
    // where it is (past the first drop too: the line goes on through every drop).
    private Vec LineCarrot(Unit u, ScoutFlight f, StrikeRun run)
    {
        var a = CurrentDrop(run, 0);
        long px = u.Pos.X.Raw - a.X.Raw, py = u.Pos.Y.Raw - a.Y.Raw;
        var along = (px * run.DirX + py * run.DirY) / 1000;
        var lead = Math.Max(f.RadiusAt(Len3(u.Vel.X.Raw, u.Vel.Y.Raw, 0)), AirSpeedOf(u));
        var at = along - lead;
        return P(a.X.Raw + run.DirX * at / 1000, a.Y.Raw + run.DirY * at / 1000);
    }

    // StrikeRunPlanned flies a planned pass's run: each tick the release solution's impact point against its next drop
    // (released within half a tick's travel, at most one a tick, once in its profile; skipped when passed by more than the
    // release window or off the line by more than the window, the scatter and, forced, groupAside); its last drop
    // released or skipped, or its bombs gone, the break.
    private void StrikeRunPlanned(Unit u, StrikeRun run, WeaponDef w)
    {
        var dl = w.Effect.Delivery!;
        if (run.DropNext >= run.Drops.Count || !HasAmmo(u, w))
        {
            StartBreak(u, run);
            return;
        }
        var k = KinFor(u, dl);
        if (!Predict(k, dl.GravityRaw / TicksPerSecond, out var imp, out var ticks))
        {
            return;
        }
        run.FallTicks = ticks;
        var vl = Math.Max(1, Len3(k.VX, k.VY, 0));
        var half = vl / TicksPerSecond / 2;
        var window = RunSpec(u).WindowRaw;
        var tol = window + ScatterOf(dl, run.Kind).Raw + (run.Forced ? dl.GroupAsideRaw : 0);
        var ready = run.Kind switch
        {
            PassKind.Dive => -u.VelZ.Raw * 2000 >= Math.Max(1, AirSpeedOf(u) / TicksPerSecond) * TanPermille(dl.DiveAngle),
            PassKind.Toss => u.VelZ.Raw > 0,
            _ => (u.Altitude.Raw - GroundRaw(run.Aim)) * 10 >= dl.ReleaseHeightRaw * 8,
        };
        while (run.DropNext < run.Drops.Count)
        {
            var d = CurrentDrop(run, run.DropNext);
            long ex = d.X.Raw - imp.X.Raw, ey = d.Y.Raw - imp.Y.Raw;
            var along = (ex * k.VX + ey * k.VY) / vl;
            if (along > half)
            {
                break; // short of it still: fly on
            }
            var cross = Math.Abs(ex * k.VY - ey * k.VX) / vl;
            if (along < -half - window || cross > tol)
            {
                run.DropNext++; // past it, or off the line: no bomb there this pass
                continue;
            }
            if (!ready || (run.Released > 0 && run.LastRelease == Tick))
            {
                break; // not in its profile yet, or a bomb already left this tick
            }
            var aim = run.DropAim[run.DropNext];
            ReleaseBomb(u, run, w, Unit(run.Served[aim].Target), imp, ticks);
            run.ServedHits |= 1UL << Math.Min(aim, 63);
            run.DropNext++;
            break;
        }
        run.Aim = PlannedAim(run);
        if (run.DropNext >= run.Drops.Count || !HasAmmo(u, w))
        {
            StartBreak(u, run);
        }
    }

    // ---- Commands ----

    // SetFireMode (H2): each listed aircraft of the player with that many fire modes takes mode c.Index (from its next pass).
    private void SetFireMode(Command c)
    {
        foreach (var id in c.Units)
        {
            if (Unit(id) is { Alive: true } u && u.Owner == c.Player && c.Index >= 0 && c.Index < FireModesOf(u).Count)
            {
                u.FireMode = c.Index;
            }
        }
    }

    // AttackLine (H2): each listed aircraft with free-fall bombs bombs the line from c.Cell to c.Cell2 (Shift: queued), with
    // attack-ground's refusals (homing, bingo, out of bombs; parked without them: rearmed first).
    private void AttackLine(Command c)
    {
        if (!Grid.Contains(c.Cell) || c.Cell2 is not { } end || !Grid.Contains(end))
        {
            return;
        }
        var aim = end == c.Cell ? new StrikeAim(0, c.Cell, default, false) : new StrikeAim(0, c.Cell, end, true);
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || u!.Def.Archetype.Flight is null || LineWeapon(u, false) is null)
            {
                continue;
            }
            if (LineWeapon(u, true) is null)
            {
                if (DockedRearm(u, null))
                {
                    Refuse(u, OrderRefusalReason.Rearming, 0, c.Cell); // parked without bombs: rearmed first (D-ORD2)
                }
                else
                {
                    Refuse(u, OrderRefusalReason.NoAmmo, 0, c.Cell);
                    continue;
                }
            }
            else if (AirOrderRefusal(u) is { } why)
            {
                Refuse(u, why, 0, c.Cell);
                continue;
            }
            else if (!FuelFor(u, c.Cell.Center()))
            {
                Refuse(u, OrderRefusalReason.Bingo, 0, c.Cell);
                continue;
            }
            if (c.Queue && QueueAim(u, aim))
            {
                continue;
            }
            Assign(u, new Order(OrderKind.AttackGround, c.Cell, 0, 0));
            if (aim.Line)
            {
                u.MutableAims.Add(aim);
            }
            if (u.AirMission is { } am)
            {
                am.Hunt = 0;
            }
        }
    }
}
