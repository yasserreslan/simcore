using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// Helipads, homes, service and riders (the helicopters night; docs/design.md, "Helicopters").
//  - A helicopter's pads: its side's helipads (a structure with a helipad block: one helicopter at a time builds on it,
//    parks, refuels, rearms and is repaired there) and the stations of its side's airfields that take its class (a forward
//    airfield's or a main air base's stands and shelters; never the heavy bays, which take L and XL only). A pad is taken
//    by a helicopter bound for it or on it (its HeliState.Pad; an airfield station's Occupant and ReservedBy too, so the
//    jets and drones leave it alone) and by a helipad's build in progress.
//  - The owner's rules, as the jets': home on its own only to refuel (bingo); Return to base sends it home to refuel and
//    holds it there; Rearm sends it home for the full service (fuel, ammunition, flares, HP), then it resumes its order.
//  - Service on a pad: the helicopter's own 1x rates (its aircraft.service block) times the pad's percents (a helipad's
//    helipad block; a station's airfield block under the airfield's rules: a loading shelter needs its facility, a main
//    air base keeps refuelling with its command building down). A refuel stop refuels only; a Rearm visit, a hard landing
//    and a new build get everything.
//  - Riders (the light helicopter's benches): soldiers ordered to Enter it walk to it while it is on the ground (it lands
//    where it is to take them: Load lands it at a point and waits); Unload flies to a point and drops them by landing or
//    by hovering low, one every few ticks. Riders are inside it (Unit.Inside): unseen, untargetable, they never fire.
public sealed partial class World
{
    // The time (ticks) a helicopter allows for coming down on its pad, on top of the flight there (bingo's home time).
    private const int HeliLandAllowance = 5 * TicksPerSecond;

    // ---- Pads ----

    // IsPad: unit p can take helicopter u: a helipad of u's side, or a station of u's side's airfield that takes u's class.
    private bool IsPad(Unit u, Unit p)
    {
        if (!p.Alive || p.Owner != u.Owner || !p.IsBuilt || p.Rubble)
        {
            return false;
        }
        if (p.Def.Archetype.Helipad is not null)
        {
            return true;
        }
        return StationFor(p) is { } st && AirfieldOf(p) is { } af && u.Def.Archetype.Aircraft is { } ac && Fits(af, st, ac) && st.Kind != StationKind.Heavy;
    }

    // StationFor is the airfield station whose unit p is, or null.
    private AirfieldStation? StationFor(Unit p)
    {
        if (p.Def.Archetype.Airfield is not { Role: AirfieldRole.Station } || AirfieldOf(p) is not { } af)
        {
            return null;
        }
        foreach (var r in af.RowList)
        {
            foreach (var st in r.Stations)
            {
                if (st.Unit == p.Id)
                {
                    return st;
                }
            }
        }
        return null;
    }

    // PadPoint is where a helicopter sets down on pad p: its centre.
    private static Vec PadPoint(Unit p) => p.Pos;

    // PadTaken: pad p is taken for helicopter u: another helicopter is bound for it or on it, a helipad's build is in
    // progress (or queued), or an airfield station has another aircraft parked at or bound for it.
    private bool PadTaken(Unit p, Unit u)
    {
        if (p.Def.Archetype.Helipad is not null && (p.ProductionQueue.Count > 0 || Array.Exists(p.Bays, b => b.Def >= 0)))
        {
            return true;
        }
        if (StationFor(p) is { } st && ((st.Occupant != 0 && st.Occupant != u.Id) || (st.ReservedBy != 0 && st.ReservedBy != u.Id)))
        {
            return true;
        }
        foreach (var o in _units)
        {
            if (o.Heli is { } oh && o.Id != u.Id && o.Alive && oh.Pad == p.Id)
            {
                return true;
            }
        }
        return false;
    }

    // BestPad is the nearest pad helicopter u may take (free; its own pad counts), preferring `prefer` (a pad or a piece of an
    // airfield) when that can take it; ties by ID. With anyPad, the nearest pad even when taken (out of control: it sets down
    // beside it). Null when it has none.
    private Unit? BestPad(Unit u, Unit? prefer = null, bool anyPad = false)
    {
        Unit? best = null;
        long bestD = 0;
        foreach (var p in _units)
        {
            if ((p.Def.Archetype.Helipad is null && p.Def.Archetype.Airfield is not { Role: AirfieldRole.Station }) || !IsPad(u, p))
            {
                continue;
            }
            var mine = u.Heli!.Pad == p.Id;
            if (!mine && !anyPad && PadTaken(p, u))
            {
                continue;
            }
            var d = (p.Pos - u.Pos).LenSq();
            if (prefer is not null && (p.Id == prefer.Id || (prefer.Airfield != 0 && p.Airfield == prefer.Airfield)))
            {
                d = -1 - (long.MaxValue - d) / 2; // the preferred pad (or airfield) first, the nearest of them
            }
            if (anyPad && !mine && PadTaken(p, u))
            {
                d = d / 2 + long.MaxValue / 2; // out of control: a free pad first, a taken one only if none is free
            }
            if (best is null || d < bestD)
            {
                (best, bestD) = (p, d);
            }
        }
        return best;
    }

    // Bind makes pad p helicopter u's pad (an airfield station is reserved for it).
    private void Bind(Unit u, HeliState h, Unit p)
    {
        if (h.Pad != p.Id && Unit(h.Pad) is { } old && StationFor(old) is { } os)
        {
            (os.Occupant, os.ReservedBy) = (os.Occupant == u.Id ? 0 : os.Occupant, os.ReservedBy == u.Id ? 0 : os.ReservedBy);
        }
        h.Pad = p.Id;
        if (StationFor(p) is { } st && st.Occupant != u.Id)
        {
            st.ReservedBy = u.Id;
        }
    }

    // HeliGoHome sends helicopter u home now for `reason` (bingo's refuel, Return to base, Rearm): to `to` (a pad, or an
    // airfield's piece) when it can take it, else its nearest free pad. False when it has none (it hovers on).
    private bool HeliGoHome(Unit u, HomeReason reason, Unit? to = null)
    {
        var h = u.Heli!;
        if (h.Phase == HeliPhase.OutOfControl || BestPad(u, to) is not { } pad)
        {
            return false;
        }
        if (h.Phase == HeliPhase.Landed && h.Pad == pad.Id)
        {
            (u.Homing, u.HomeReason, h.Hold) = (true, reason, false); // already on it: the service here
            return true;
        }
        Bind(u, h, pad);
        (u.Homing, u.HomeReason) = (true, reason);
        StopAttack(h);
        if (u.AirMission is { } m)
        {
            m.Away = true;
        }
        return true;
    }

    // HeliFarthestHome is the longest helicopter u could need to get home from anywhere on the map (the map's width plus
    // height at cruise, plus the landing): a cheap bound, so the pad search runs only once its fuel runs low.
    private long HeliFarthestHome(Unit u) =>
        ((long)Grid.Width + Grid.Height) * Fixed.OneRaw * TicksPerSecond / Math.Max(1, Cruise(u)) + HeliLandAllowance;

    // HeliHomeTicks is the time helicopter u needs to get home (its nearest pad) at cruise, plus the landing.
    private long HeliHomeTicks(Unit u, ScoutFlight f)
    {
        var pad = Unit(u.Heli!.Pad) ?? BestPad(u);
        if (pad is null)
        {
            return 0;
        }
        return (pad.Pos - u.Pos).Len().Raw * TicksPerSecond / Math.Max(1, Cruise(u)) + HeliLandAllowance;
    }

    // ---- Service ----

    // ServiceOnPad gives landed helicopter u a tick of its pad's service and reports whether it is serviced (a full tank,
    // and on a full service full ammunition, flares and HP). Off a pad there is nothing to give: serviced.
    private bool ServiceOnPad(Unit u, HeliState h, ScoutFlight f)
    {
        if (Unit(h.Pad) is not { } pad || u.Def.Archetype.Aircraft is not { } ac)
        {
            return true;
        }
        var (fuel, rearm, repair) = PadRates(pad);
        var full = u.HomeReason == HomeReason.Rearm || !u.Homing && h.Hold;
        var max = u.Tank;
        if (fuel > 0 && max > 0 && u.Lifetime < max)
        {
            var per = (long)ac.RefuelTicks * 100;
            h.FuelAcc += (long)max * fuel;
            u.Lifetime = (int)Math.Min(max, u.Lifetime + h.FuelAcc / per);
            h.FuelAcc %= per;
        }
        if (full && rearm > 0 && ShortStock(u) is var stock and >= 0)
        {
            h.AmmoAcc += rearm;
            if (h.AmmoAcc >= (long)ac.RearmTicksPerRound * 100)
            {
                h.AmmoAcc -= (long)ac.RearmTicksPerRound * 100;
                u.MutableCharges[stock]++;
            }
        }
        else if (full && rearm > 0 && ShortSalvo(u) is var cm and >= 0)
        {
            h.AmmoAcc += rearm;
            if (h.AmmoAcc >= (long)u.Def.Archetype.Countermeasures[cm].RearmTicks * 100)
            {
                h.AmmoAcc -= (long)u.Def.Archetype.Countermeasures[cm].RearmTicks * 100;
                u.MutableSalvos[cm]++;
            }
        }
        if (full && repair > 0 && ac.RepairHpPerSecond > 0 && u.HP < u.Def.MaxHp)
        {
            h.HpAcc += (long)ac.RepairHpPerSecond * repair;
            u.HP = (int)Math.Min(u.Def.MaxHp, u.HP + h.HpAcc / (TicksPerSecond * 100L));
            h.HpAcc %= TicksPerSecond * 100L;
        }
        var tank = max <= 0 || u.Lifetime >= max || fuel == 0;
        return tank && (!full || ((ShortStock(u) < 0 && ShortSalvo(u) < 0 || rearm == 0) && (u.HP >= u.Def.MaxHp || repair == 0 || ac.RepairHpPerSecond == 0)));
    }

    // PadRates are pad p's service percents now: a helipad's own; an airfield station's while it works (its facility down:
    // the main air base's refuel only, the forward airfield's nothing).
    private (int Fuel, int Rearm, int Repair) PadRates(Unit p)
    {
        if (p.Def.Archetype.Helipad is { } hp)
        {
            return (hp.RefuelPercent, hp.RearmPercent, hp.RepairPercent);
        }
        if (AirfieldOf(p) is not { } af || p.Def.Archetype.Airfield is not { } spec)
        {
            return (0, 0, 0);
        }
        if (StationWorks(af, p))
        {
            return (spec.RefuelPercent, spec.RearmPercent, spec.RepairPercent);
        }
        return StationRefuelsOnly(af, p) && spec.RefuelOnly is { } ro ? (ro.RefuelPercent, 0, 0) : (0, 0, 0);
    }

    /// <summary>
    /// A helipad's state for a client (read-only): the helicopter parked on it, the one bound for it, its build in progress
    /// and whether it services its occupant now. Default for anything that is not a helipad.
    /// </summary>
    public PadStatus PadStatusOf(Unit p)
    {
        if (p.Def.Archetype.Helipad is null && StationFor(p) is null)
        {
            return default;
        }
        uint occ = 0, inbound = 0;
        foreach (var o in _units)
        {
            if (o.Heli is { } oh && o.Alive && oh.Pad == p.Id)
            {
                if (oh.Phase == HeliPhase.Landed)
                {
                    occ = o.Id;
                }
                else if (inbound == 0)
                {
                    inbound = o.Id;
                }
            }
        }
        var building = -1;
        var pct = 0;
        foreach (var b in p.Bays)
        {
            if (b.Def >= 0)
            {
                building = b.Def;
                var total = Math.Max(1, Match.Defs[b.Def].Archetype.BuildTime);
                pct = (int)Math.Clamp((total - b.Left) * 100L / total, 0, 100);
            }
        }
        var servicing = false;
        if (Unit(occ) is { } u && u.Def.Archetype.Aircraft is { })
        {
            var (fuel, rearm, repair) = PadRates(p);
            var full = u.HomeReason == HomeReason.Rearm || !u.Homing && u.Heli!.Hold;
            servicing = (fuel > 0 && u.Tank > 0 && u.Lifetime < u.Tank) ||
                (full && rearm > 0 && (ShortStock(u) >= 0 || ShortSalvo(u) >= 0)) || (full && repair > 0 && u.HP < u.Def.MaxHp);
        }
        return new PadStatus(occ, inbound, building, pct, servicing);
    }

    // ---- Production into a pad ----

    // ParkHeli puts helicopter def, just built by factory f (a helipad, or a main air base's hangar), down on its pad: the
    // helipad itself, or the hangar's base's first free station that takes it (a stand, then a shelter); landed, its rotor
    // stopped, full of fuel and stocks, held until its first order. False when no pad is free (it waits in its bay).
    private bool ParkHeli(Unit f, UnitDef def)
    {
        Unit? pad = null;
        if (f.Def.Archetype.Helipad is not null)
        {
            pad = f;
        }
        else if (ProducerField(f) is { } af && def.Archetype.Aircraft is { } ac)
        {
            foreach (var kind in (ReadOnlySpan<StationKind>)[StationKind.Normal, StationKind.Loading])
            {
                foreach (var r in ServedRows(af))
                {
                    foreach (var st in r.Stations)
                    {
                        if (pad is null && st.Kind == kind && st.Occupant == 0 && st.ReservedBy == 0 && Fits(af, st, ac) && Unit(st.Unit) is { } su &&
                            !HeliBound(su))
                        {
                            pad = su;
                        }
                    }
                }
            }
        }
        if (pad is null)
        {
            return false;
        }
        var u = Add(def, PadPoint(pad), pad.Def.IsStructure ? Facing.South : pad.Facing);
        var h = u.Heli!;
        u.Altitude = Fixed.FromRaw(GroundRaw(u.Pos));
        h.Phase = HeliPhase.Landed;
        (h.Rotor, h.Hold, h.Post) = (0, true, u.Pos);
        Park(u, h, pad);
        return true;
    }

    // HeliBound: some helicopter is bound for or parked on pad p.
    private bool HeliBound(Unit p)
    {
        foreach (var o in _units)
        {
            if (o.Heli is { } oh && o.Alive && oh.Pad == p.Id)
            {
                return true;
            }
        }
        return false;
    }

    // HeliKeepsStation: helicopter id is bound for or parked on station unit s (the airfield sweep keeps its reservation).
    internal bool HeliKeepsStation(uint id, uint s) => id != 0 && Unit(id) is { Heli: { } h, Alive: true } && h.Pad == s;

    // ---- Riders ----

    /// <summary>The soldiers riding helicopter u's benches, in ID order (empty for anything else).</summary>
    public IReadOnlyList<Unit> RidersOf(Unit u)
    {
        var list = new List<Unit>();
        if (u.Def.Archetype.Flight?.Rotor?.Riders is null)
        {
            return list;
        }
        foreach (var o in _units)
        {
            if (o.Inside == u.Id)
            {
                list.Add(o);
            }
        }
        return list;
    }

    // CanRideHeli: soldier s may board helicopter v's benches: v's own side, a seat free, one of the benches' tags.
    internal bool CanRideHeli(Unit s, Unit v) =>
        v.Alive && s.Alive && s.Id != v.Id && v.Owner == s.Owner && v.Heli is { Phase: not HeliPhase.OutOfControl } &&
        v.Def.Archetype.Flight?.Rotor?.Riders is { } rs && s.Inside == 0 && s.Garrison == 0 && s.Def.Crew is null && !s.Def.IsStructure &&
        s.Def.Layer == Layers.Ground && s.Def.Tags.Matches(rs.Tags) && RidersOf(v).Count < rs.Seats;

    // Boarding: someone of u's side is on an Enter order to it (and may still board).
    private bool Boarding(Unit u)
    {
        foreach (var o in _units)
        {
            if (o.Order.Kind == OrderKind.Enter && o.Order.Target == u.Id && o.Inside == 0 && o.Alive && CanRideHeli(o, u))
            {
                return true;
            }
        }
        return false;
    }

    // BoardHeli runs soldier s's Enter order to helicopter v (the boarding system): within the benches' reach of it on the
    // ground he climbs aboard; while it flies, he waits (it lands where it is to take him, see HeliWantsRiders).
    private void BoardHeli(Unit s, Unit v)
    {
        if (!CanRideHeli(s, v))
        {
            s.Idle();
            return;
        }
        if (v.Heli!.Phase == HeliPhase.Landed && Within(s.Pos, v.Pos, v.Def.Archetype.Flight!.Rotor!.Riders!.BoardRange))
        {
            s.Idle();
            s.AutoTarget = 0;
            s.SalvoLeft = 0;
            s.ClearPath();
            s.Inside = v.Id;
            s.Pos = v.Pos;
        }
    }

    // HeliWantsRiders: a flying, idle helicopter with soldiers sent to board it lands where it is to take them (Load).
    private void HeliWantsRiders(Unit u, HeliState h)
    {
        if (h.Phase == HeliPhase.Flying && h.Errand == HeliErrand.None && !u.Homing && u.Order.Kind == OrderKind.Idle &&
            u.Def.Archetype.Flight!.Rotor!.Riders is not null && (Tick + u.Id) % 5 == 0 && Boarding(u))
        {
            (h.Errand, h.LandAt) = (HeliErrand.Board, LandingSpot(u.Pos));
        }
    }

    // LandingSpot is the centre of the nearest cell to p a helicopter may set down on (passable open ground), or p.
    private Vec LandingSpot(Vec p) => Grid.TrySpiral(Cell.Of(p), 0, out var c) ? c.Center() : p;

    // Errand flies helicopter u's Load or Unload for a tick; false when it has nothing to do now (it flies its order).
    private bool Errand(Unit u, HeliState h, ScoutFlight f, RotarySpec r)
    {
        if (h.Errand == HeliErrand.Board)
        {
            if (FlyTo(u, h, f, r, h.LandAt, Cruise(u), null, f.HeightRaw, -1, true))
            {
                LeavePad(u, h);
                SetPhase(h, HeliPhase.Landing);
            }
            return true;
        }
        if (RidersOf(u).Count == 0)
        {
            h.Errand = HeliErrand.None;
            return false;
        }
        var arrived = FlyTo(u, h, f, r, h.LandAt, Cruise(u), null, f.HeightRaw, h.DropHover && Within(u.Pos, h.LandAt, Fixed.FromInt(3))
            ? GroundRaw(h.LandAt) + r.Riders!.DropHeightRaw : -1, true);
        if (!arrived)
        {
            return true;
        }
        if (!h.DropHover)
        {
            LeavePad(u, h);
            SetPhase(h, HeliPhase.Landing);
            return true;
        }
        if (u.Altitude.Raw <= GroundRaw(u.Pos) + r.Riders!.DropHeightRaw + 256)
        {
            DropRiders(u, h, r, false); // hovering low over the point: one jumps every dropTicks
        }
        return true;
    }

    // DropRiders lets helicopter u's riders out one every dropTicks (landed or hovering low) onto the free cells round it;
    // with none left its errand is over (it waits for its next order).
    private void DropRiders(Unit u, HeliState h, RotarySpec r, bool landed)
    {
        var riders = RidersOf(u);
        if (riders.Count == 0)
        {
            h.Errand = HeliErrand.None;
            return;
        }
        if (h.DropLeft > 0 && --h.DropLeft > 0)
        {
            return;
        }
        var s = riders[0];
        s.Inside = 0;
        s.Idle();
        s.AutoTarget = 0;
        s.Pos = Grid.TrySpiral(Cell.Of(u.Pos), riders.Count, out var c) ? c.Center() : u.Pos;
        h.DropLeft = r.Riders!.DropTicks;
        if (riders.Count == 1)
        {
            h.Errand = HeliErrand.None;
            if (!landed)
            {
                h.Post = u.Pos;
            }
        }
    }

    // ---- Losses: out of control, crashes, survivors ----

    // Scratch (not state): the crew survivors to set down after this tick's crashes (the crash's index, owner, archetype, where).
    private readonly List<(int Crash, byte Owner, int Archetype, Vec At)> _heliSurvivors = [];

    // RunHeliDamage runs after every system that hurts (before the crew system and the cleanup): a helicopter whose HP a hit
    // took to 0 in the air goes out of control instead (unless the hit destroys outright: the Patriot type); out of control,
    // its HP drains, any further hit destroys it in the air, and when its time runs out it crash-lands; its crew and riders
    // roll survival on a crash landing or on the ground.
    private void RunHeliDamage()
    {
        if (!_helis)
        {
            return;
        }
        _heliSurvivors.Clear();
        foreach (var u in _units)
        {
            if (u.Heli is not { } h || u.Recovered)
            {
                continue;
            }
            var r = u.Def.Archetype.Flight!.Rotor!;
            if (h.Phase == HeliPhase.OutOfControl)
            {
                if (u.HP < h.OocHp)
                {
                    Lose(u, h, r, HeliCrashKind.AirDestroyed); // a further hit while out of control
                    continue;
                }
                if (--h.OocLeft <= 0)
                {
                    (u.KilledBy, u.Killer) = (KillCause.Crash, 0);
                    Lose(u, h, r, HeliCrashKind.CrashLanded);
                    continue;
                }
                u.HP = h.OocHp = Math.Max(1, (int)((long)h.OocFrom * h.OocLeft / Math.Max(1, r.OutOfControl.Ticks)));
                continue;
            }
            if (u.HP > 0)
            {
                h.Outright = false;
                continue;
            }
            if (h.Phase == HeliPhase.Landed)
            {
                Lose(u, h, r, HeliCrashKind.DestroyedOnGround);
            }
            else if (h.Outright || u.KilledBy == KillCause.Fuel)
            {
                Lose(u, h, r, h.Outright ? HeliCrashKind.AirDestroyed : HeliCrashKind.CrashLanded);
            }
            else
            {
                OutOfControl(u, h, r);
            }
            h.Outright = false;
        }
        foreach (var (crash, owner, archetype, at) in _heliSurvivors)
        {
            // The crew who got out: soldiers of the helicopter's side on the nearest free cell (crew lost with nowhere to stand).
            var c = _heliCrashes[crash];
            if (Player(owner)?.Setup.Defaults[archetype] is { } def && LandingCell(def, at) is { } cell)
            {
                var s = Add(def, cell == Cell.Of(at) ? at : cell.Center(), Facing.South);
                _heliCrashes[crash] = c with { Survivors = [.. c.Survivors, s.Id] };
            }
            else
            {
                _heliCrashes[crash] = c with { Lost = c.Lost + 1 };
            }
        }
    }

    // OutOfControl starts helicopter u's flight out of control: HP at its share draining over its time, its order, errand
    // and attack gone (no player orders reach it), spinning a seeded way, flying for the nearest pad.
    private void OutOfControl(Unit u, HeliState h, RotarySpec r)
    {
        var o = r.OutOfControl;
        u.HP = Math.Max(1, (int)((long)u.Def.MaxHp * o.HpPercent / 100));
        (h.OocFrom, h.OocHp, h.OocLeft) = (u.HP, u.HP, o.Ticks);
        h.OocHeight = Math.Max(o.EndHeightRaw, u.Altitude.Raw - GroundRaw(u.Pos));
        h.Spin = Rand.Hash(Match.Setup.Seed, Tick, u.Id, 0x5A1D) % 2 == 0 ? 1 : -1;
        (u.KilledBy, u.Killer) = (KillCause.None, 0);
        u.Idle();
        u.Homing = false;
        (h.Errand, h.Hold, h.Target) = (HeliErrand.None, false, 0);
        StopAttack(h);
        h.SalvoLeft = 0;
        SetPhase(h, HeliPhase.OutOfControl);
        var pad = BestPad(u, null, true);
        if (pad is not null && !PadTaken(pad, u))
        {
            Bind(u, h, pad);
        }
        else
        {
            LeavePad(u, h);
            h.LandAt = pad is null ? default : LandingSpot(pad.Pos + new Vec(Fixed.FromInt(pad.Def.IsStructure ? FootW(pad) / 2 + 4 : 6), Fixed.Zero));
        }
    }

    // Lose ends helicopter u's flight: destroyed in the air (its wreck falls; nobody survives), crash-landed where it is or
    // destroyed on the ground (its crew and riders each roll their survival). Its pad is free again.
    private void Lose(Unit u, HeliState h, RotarySpec r, HeliCrashKind kind)
    {
        u.HP = 0;
        if (u.KilledBy == KillCause.None)
        {
            u.KilledBy = KillCause.Blast;
        }
        h.Crash = kind;
        LeavePad(u, h);
        var lost = 0;
        var at = u.Pos;
        var water = kind == HeliCrashKind.CrashLanded && InWater(at); // down in a river, lake or sea: it sinks (WorldWater.cs)
        var survive = kind != HeliCrashKind.AirDestroyed && !water; // no swimming
        var index = _heliCrashes.Count;
        for (var i = 0; i < r.Crew.Count; i++)
        {
            if (survive && r.Crew.Unit >= 0 && (int)(Rand.Hash(Match.Setup.Seed, Tick, u.Id, 0xC4E0UL + (ulong)i) % 100) < r.Crew.Survive)
            {
                _heliSurvivors.Add((index, u.Owner, r.Crew.Unit, at));
            }
            else
            {
                lost++;
            }
        }
        var riders = new List<uint>();
        var k = 0;
        foreach (var s in RidersOf(u))
        {
            if (survive && r.Riders is { } rs && (int)(Rand.Hash(Match.Setup.Seed, Tick, s.Id, 0x41DEUL) % 100) < rs.Survive)
            {
                s.Inside = 0;
                s.Idle();
                s.Pos = Grid.TrySpiral(Cell.Of(at), ++k, out var c) ? c.Center() : at;
                riders.Add(s.Id);
            }
            else
            {
                s.HP = 0;
                lost++;
            }
        }
        _heliCrashes.Add(new HeliCrash(u.Id, u.Owner, u.Def.Archetype.Index, kind, at, u.Altitude, riders, lost, water));
    }
}
