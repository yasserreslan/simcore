using System.Runtime.CompilerServices;
using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// INF-E1b-4: morale (brief 7.3; rules 12.4, 10.2, 10.3, 4.6, 6.2, 11.8, 16.8). One number per squad (rules 22.2), 0 to 100 from
// morale.start, in hundredths. Events change it at once: a member killed, down or wounded, the leader lost (with 11.8's pause), an
// enemy killed by the squad, flanked. Rates at each squad think: pinned members, quiet, the leader or a medic or a friendly tank or
// IFV near, a garrison, an enemy tank or aircraft that nobody in the squad can hurt (16.8: a MANPADS can hurt a helicopter). The
// state (Steady, Shaken, Broken; routing) goes to every member's fight (SoldierFight.Spirit, Routs, Cowed) for his own think.
// The rates that raise it restore it to morale.start, no higher: a squad at rest is at 80 when a fight starts (8.4's arithmetic);
// part 4: nothing lifts it above the losses' ceiling (LossCeiling), kills included.
public sealed partial class World
{
    private static int MoraleNow(SquadMind m, InfantryCombatRules r) => m.Morale100 < 0 ? r[CombatValue.MoStart] * 100 : m.Morale100;

    // MoraleChange: an event's change, in points, to squad s's morale; the state follows at once.
    private void MoraleChange(Squad? s, int points)
    {
        if (s?.Mind is not { } m || Combat is not { } r || !_fights || points == 0)
        {
            return;
        }
        var now = MoraleNow(m, r);
        var v = now + points * 100;
        m.Morale100 = Math.Clamp(points > 0 ? Math.Max(now, Math.Min(v, LossCeiling(s, m, r))) : v, 0, 10000); // part 4: lifts stop at the ceiling
        MoraleStateOf(s, m, r);
    }

    // LossCeiling (part 4, the coordinator's answer: rule 8.4's arithmetic is the intent): until the squad rallies (its fight over:
    // calm, 10.1's release), its morale can't rise above morale.start - morale.lossSpan x (men lost) / (men at the fight's start),
    // rounded down (part 5: the ceiling scales with the squad's size; for six it is 8.4's 80 - 15 x lost exactly). Men lost: killed
    // in this fight (SquadMind.Lost) or down now; men at the start: the living members plus those killed. A kill (+5), the leader,
    // a medic, helpers and rest lift it only up to there. In hundredths. Four of six lost: 20, Broken (MoraleStateOf), so an even
    // trade breaks the side that lost four; four of seven: 28, Shaken (scene 4's blue).
    private int LossCeiling(Squad s, SquadMind m, InfantryCombatRules r)
    {
        var lost = m.Lost;
        var men = m.Lost;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Alive: true } u)
            {
                men++;
                lost += IsDown(u) ? 1 : 0;
            }
        }
        var start = r[CombatValue.MoStart];
        if (lost == 0 || men == 0)
        {
            return start * 100;
        }
        var off = (r[CombatValue.MoLossSpan] * lost + men - 1) / men; // rounded up, so the ceiling is rounded down
        return (start - off) * 100;
    }

    // MoraleLoss (brief 7.3): member id of squad s killed, down or wounded (kind: MoKilled, MoDown, MoWounded); the leader killed or
    // down costs morale.leader more, once for him, and starts 11.8's pause (no new bounds or flanks, the choice kept).
    internal void MoraleLoss(Squad s, uint id, CombatValue kind)
    {
        if (s.Mind is not { } m || Combat is not { } r || !_fights)
        {
            return;
        }
        var points = -r[kind];
        m.Lost += kind == CombatValue.MoKilled ? 1 : 0; // part 4: the loss ceiling's count (a down man counts while down)
        if (kind != CombatValue.MoWounded)
        {
            NoteCallLoss(m); // part 2 (13.1): U +10 for each member downed or killed in the last lossTicks
        }
        if (kind != CombatValue.MoWounded && s.Leader == id && m.LeaderHit != id)
        {
            points -= r[CombatValue.MoLeader];
            (m.LeaderHit, m.LeaderPauseUntil) = (id, Tick + (uint)r[CombatValue.MoLeaderPauseTicks]);
        }
        MoraleChange(s, points);
    }

    // MoraleFlanked: squad s fired on from outside its cover's arc (brief 7.3, 17.3; a flank's arrival, 11.5): -flanked, once per
    // flankedTicks.
    private void MoraleFlanked(Squad? s)
    {
        if (s?.Mind is not { } m || Combat is not { } r || Tick < m.FlankedNoUntil)
        {
            return;
        }
        m.FlankedNoUntil = Tick + (uint)r[CombatValue.MoFlankedTicks];
        MoraleChange(s, -r[CombatValue.MoFlanked]);
    }

    // TickMorale (each squad think): the rates since the last one, then the state. Pinned: the share of the able members at pinned
    // (60+) for longer than pinnedTicks, up to pinnedRate a second; quiet (no shot near a member for quietTicks): quietRate; the
    // leader able, an able medic of the squad, or a friendly medic, tank or IFV within aidReach of the centre: aidRate; the share in
    // a garrison: garrisonRate; an enemy tank, IFV or helicopter of the picture within vehicleReach of the centre that nobody able
    // can hurt (a launcher or Kornet for armour; a MANPADS or an air weapon for a helicopter, 16.8): -vehicleRate. The aid and the
    // garrison count while no able member is under effective fire (section 3): with them in a fight an even squad that has lost four
    // of six sits at 20, not under it (8.4 counts it Broken).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // BATTLE-PERF: run every tick, compiled optimised at once (row 1390)
    private void TickMorale(Squad s, SquadMind m, InfantryCombatRules r)
    {
        var dt = m.MoraleTick == 0 ? 0 : (int)Math.Min(Tick - m.MoraleTick, 40u);
        var since = m.MoraleTick;
        m.MoraleTick = Tick;
        m.Morale100 = MoraleNow(m, r);
        int able = 0, pinned = 0, garrison = 0, total = 0;
        uint last = 0;
        bool flanked = false, hurtsArmour = false, hurtsAir = false, aid = false, fire = false;
        long sx = 0, sy = 0;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is not { Alive: true } u)
            {
                continue;
            }
            total++;
            garrison += u.Garrison != 0 ? 1 : 0;
            if (u.Fight is not { } f || !Able(u))
            {
                if (u.Fight is { } g)
                {
                    g.PinnedSince = 0;
                }
                continue;
            }
            able++;
            (sx, sy) = (sx + u.Pos.X.Raw, sy + u.Pos.Y.Raw);
            if (f.Suppression >= r[CombatValue.Pinned])
            {
                f.PinnedSince = f.PinnedSince == 0 ? Tick : f.PinnedSince;
                pinned += Tick - f.PinnedSince > (uint)r[CombatValue.MoPinnedTicks] ? 1 : 0;
            }
            else
            {
                f.PinnedSince = 0;
            }
            last = Math.Max(last, Math.Max(f.ShotNear1, u.LastHitTick));
            if (dt > 0 && u.LastHitTick > since && f.Axis >= 0)
            {
                var c = Cell.Of(u.Pos);
                var from = Dir32(u.LastHitFrom.X.Raw - u.Pos.X.Raw, u.LastHitFrom.Y.Raw - u.Pos.Y.Raw);
                flanked |= CoverToward(c, f.Axis, r) != CoverKind.None && CoverToward(c, from, r) == CoverKind.None;
            }
            var role = SquadRoleOf(u);
            hurtsArmour |= role == SquadRole.LongFlank;
            hurtsAir |= role == SquadRole.AirGuard;
            foreach (var w in u.Def.Weapons)
            {
                hurtsArmour |= r.Weapon(w.Effect)?.Class == CombatClass.Launcher;
                hurtsAir |= w.Effect.Interceptor is not null || w.Effect.AirGun is not null;
            }
            fire |= (f.ShotNear2 != 0 && Tick - f.ShotNear2 < (uint)r[CombatValue.EffectiveTicks]) ||
                (u.LastHitTick != 0 && Tick - u.LastHitTick < (uint)r[CombatValue.EffectiveHitTicks]); // section 3's effective fire
            aid |= u.Id == s.Leader || Content.Soldiers is { } sr && sr.IsMedic(u.Def.Tags);
        }
        if (dt > 0 && total > 0)
        {
            long d = 0, up = 0;
            if (able > 0)
            {
                var centre = new Vec(Fixed.FromRaw(sx / able), Fixed.FromRaw(sy / able));
                d -= (long)r[CombatValue.MoPinnedRate] * 5 * dt * pinned / able;
                aid = aid || AidNear(s, centre, r);
                var vr = (long)r[CombatValue.MoVehicleReach] * Fixed.OneRaw / 10;
                foreach (var k in m.PictureList)
                {
                    if (k.Enemy != 0 && (k.Class == CombatClass.Armour && !hurtsArmour || k.Class == CombatClass.Helicopter && !hurtsAir) &&
                        (k.At.Center() - centre).LenSq() <= vr * vr)
                    {
                        d -= (long)r[CombatValue.MoVehicleRate] * 5 * dt; // "nobody can hurt it": once, however many
                        break;
                    }
                }
            }
            up += last == 0 || Tick - last >= (uint)r[CombatValue.MoQuietTicks] ? (long)r[CombatValue.MoQuietRate] * 5 * dt : 0;
            up += aid && !fire ? (long)r[CombatValue.MoAidRate] * 5 * dt : 0; // (the aid and the garrison steady a squad that is not
            up += fire ? 0 : (long)r[CombatValue.MoGarrisonRate] * 5 * dt * garrison / total; // under effective fire: 8.4's "four lost, Broken")
            m.Morale100 = (int)Math.Clamp(m.Morale100 + d, 0, 10000);
            var cap = LossCeiling(s, m, r); // the rates restore it to morale.start less the losses' ceiling, no higher (8.4; part 4)
            m.Morale100 = m.Morale100 < cap ? (int)Math.Min(cap, m.Morale100 + up) : m.Morale100;
        }
        if (flanked)
        {
            MoraleFlanked(s);
        }
        MoraleStateOf(s, m, r);
    }

    // AidNear: a friendly medic of another squad, or a friendly tank or IFV, within aidReach of the centre (brief 7.3's +1/s).
    private bool AidNear(Squad s, Vec centre, InfantryCombatRules r)
    {
        var reach = (long)r[CombatValue.MoAidReach] * Fixed.OneRaw / 10;
        foreach (var o in _units)
        {
            if (o.Owner != s.Owner || !o.Alive || o.Squad == s.Id || (o.Pos - centre).LenSq() > reach * reach)
            {
                continue;
            }
            if (o.Life is null ? (o.Def.Tags.Bits & r.ArmourTags.Bits) != 0 : Content.Soldiers is { } sr && sr.IsMedic(o.Def.Tags) && !IsDown(o))
            {
                return true;
            }
        }
        return false;
    }

    // MoraleStateOf (brief 7.3, 12.4): Broken under brokenBelow; a broken squad rallies to Shaken at rally (rallyLed with a Veteran
    // or Elite leader), Steady at steadyAt; routing under routBelow until back to routEnd (still Broken). The members get the state;
    // with no order a broken squad's men are cowed (they return fire only) and a routing one's run. A change: an urgent squad think
    // and a Morale event (the client's alert and markers).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // BATTLE-PERF: run every tick, compiled optimised at once (row 1390)
    private void MoraleStateOf(Squad s, SquadMind m, InfantryCombatRules r)
    {
        m.Morale100 = MoraleNow(m, r);
        var v = m.Morale100 / 100;
        var (spirit, routing) = (m.Spirit, m.Routing);
        var rally = Unit(s.Leader) is { Life.Rank: SoldierRank.Veteran or SoldierRank.Elite } ? r[CombatValue.MoRallyLed] : r[CombatValue.MoRally];
        var floor = LossCeiling(s, m, r) <= r[CombatValue.MoBrokenBelow] * 100; // part 4 (8.4): four of six lost, "20 at best": Broken
        m.Spirit = v < r[CombatValue.MoBrokenBelow] || floor || (m.Spirit == MoraleState.Broken && v < rally) ? MoraleState.Broken
            : v >= r[CombatValue.MoSteadyAt] ? MoraleState.Steady : MoraleState.Shaken;
        m.Routing = v < r[CombatValue.MoRoutBelow] || (m.Routing && v < r[CombatValue.MoRoutEnd]);
        m.BrokenSafe &= m.Spirit == MoraleState.Broken;
        var free = s.Order == OrderKind.Idle;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Fight: { } f })
            {
                (f.Spirit, f.Routs, f.Cowed) = (m.Spirit, m.Routing && free, m.Spirit == MoraleState.Broken && free);
            }
        }
        if (spirit != MoraleState.Broken && m.Spirit == MoraleState.Broken && free && m.Choice == SquadChoice.Withdraw)
        {
            m.Choice = SquadChoice.FightInPlace; // part 2: broken in mid-withdrawal: the next think picks its way to safety (12.2), not the old point
        }
        if (spirit != m.Spirit || routing != m.Routing)
        {
            m.Urgent = true;
            _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Morale, s.Leader, (uint)m.Spirit + (m.Routing ? 8u : 0)));
        }
    }

    // TakeShelter (12.2, brief 7.3; the coordinator's answer of part 2): a broken squad with no order at its safety building's door
    // goes in (garrisons it: the shelter, not the door), each able member who may (within `near` of the door, raw); one who may not (it
    // is full) stays at the door. On the way there, a man a step from the door goes in at once (Withdraw).
    private void TakeShelter(Squad s, SquadMind m, long near)
    {
        if (m.SafeBuilding == 0 || Unit(m.SafeBuilding) is not { Alive: true } b || b.Def.Archetype.Garrison is null)
        {
            return;
        }
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { } u && Able(u) && u.Order.Kind != OrderKind.Enter && CanGarrison(u, b) &&
                (near == long.MaxValue || (u.Pos - m.WithdrawTo.Center()).LenSq() <= near * near))
            {
                Assign(u, new Order(OrderKind.Enter, default, b.Id, 0));
            }
        }
    }

    // OrderLiftsRout (12.4, decision 6): any order of the player is obeyed in full: the rout's and the cowed men's behaviour ends (the
    // aim and suppression penalties stay); with no order again, the next squad think brings them back while the state holds.
    private void OrderLiftsRout(Squad s)
    {
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Fight: { } f })
            {
                (f.Routs, f.Cowed) = (false, false);
            }
        }
    }

    // SpiritAim, SpiritSuppress, SpiritReact (brief 7.3, 4.6): the morale state's aim and suppression-gain percents and reaction ticks.
    private int SpiritAim(Unit u) => u.Fight is { Spirit: not MoraleState.Steady } f && Combat is { } r
        ? r[f.Spirit == MoraleState.Shaken ? CombatValue.MoShakenAim : CombatValue.MoBrokenAim] : 100;

    private int SpiritSuppress(Unit u) => u.Fight is { Spirit: not MoraleState.Steady } f && Combat is { } r
        ? r[f.Spirit == MoraleState.Shaken ? CombatValue.MoShakenSuppress : CombatValue.MoBrokenSuppress] : 100;

    private static int SpiritReact(SoldierFight f, InfantryCombatRules r) => f.Spirit switch
    {
        MoraleState.Shaken => r[CombatValue.MoShakenReact],
        MoraleState.Broken => r[CombatValue.MoBrokenReact],
        _ => 0,
    };

    // CowedHolds (12.4): a routing man never fires; a cowed one (broken, no order) fires only at an enemy within brokenFireRange or
    // one that fired at his squad within firedOnTicks.
    private bool CowedHolds(Unit u, SoldierFight f, Unit target, InfantryCombatRules r)
    {
        if (f.Routs)
        {
            return true;
        }
        if (!f.Cowed)
        {
            return false;
        }
        var near = (long)r[CombatValue.MoBrokenFireRange] * Fixed.OneRaw / 10;
        if ((target.Pos - u.Pos).LenSq() <= near * near)
        {
            return false;
        }
        var shot = target.Fight is { } tf ? (tf.LastTarget, tf.LastTargetTick) : (target.AutoTarget, Tick);
        return !(Tick - shot.Item2 <= (uint)r[CombatValue.MoFiredOnTicks] && Unit(shot.Item1) is { } v && v.Squad == u.Squad && u.Squad != 0);
    }

    // SafetyPoint (12.2, brief 7.3): a broken squad's safety: the nearest friendly (or neutral) garrisonable building with room within
    // safeReach of the centre, not toward the threat (its door level with the centre or behind it on the line's axis); its door
    // nearest the centre. None: the caller takes the best point behind the line (WithdrawPoint, hard cover first).
    private Cell? SafetyPoint(Squad s, SquadMind m, Cell centre, InfantryCombatRules r)
    {
        var axis = m.LineAxis >= 0 ? m.LineAxis : m.Axis; // (part 2: the threat axis without a line)
        if (axis < 0 || Unit(s.Leader) is not { } lead)
        {
            return null;
        }
        var (dx, dy) = InfantryCombatDirections(axis);
        var reach = (long)r[CombatValue.MoSafeReach] * Fixed.OneRaw / 10;
        Cell? best = null;
        var bestD = long.MaxValue;
        m.SafeBuilding = 0;
        foreach (var b in _units)
        {
            if (b.Def.Archetype.Garrison is null || !CanGarrison(lead.Inside == 0 && lead.Garrison == 0 ? lead : b, b))
            {
                continue;
            }
            for (var i = 0; i < DoorCount(b); i++)
            {
                var door = DoorCell(b, i);
                var d = (door.Center() - centre.Center()).LenSq();
                var ahead = (door.X - centre.X) * (long)dx + (door.Y - centre.Y) * (long)dy > 0;
                if (!ahead && d <= reach * reach && d < bestD && Grid.Contains(door) && Standable(door))
                {
                    (best, bestD, m.SafeBuilding) = (door, d, b.Id);
                }
            }
        }
        return best;
    }
}
